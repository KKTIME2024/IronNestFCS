using System;
using System.Collections.Generic;
using IronNestFCS.Logic.FCS;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

/// <summary>
/// 地图 overlay v2 —— 铁巢作战意图可视化。
/// 视觉规格: docs/superpowers/specs/2026-08-13-map-overlay-visual-design.md
/// 渲染机制: 照搬 renchonghan/IronNestFCS enhanced-merge (d18b459) 的 Il2CppShapes.Line 路线 ——
///   本游戏(IL2CPP 裁剪)运行时 AddComponent&lt;TextMeshPro&gt; 不渲染、CreatePrimitive 材质被裁剪(见其 GameInternals.md),
///   所以一切几何与文字都用游戏自带 Il2CppShapes.Line 画线段; 文字用扩展线段字符集(数字 + 全字母 + 单位符号)。
/// 元素: 毁伤圈(24 段 Line 多边形) / 圈标签(弹种+整数秒倒计时, 落点右上偏移+深红引线)
///       / 火力线(玩家→落点) / 火力线标签(距离+方位角, 顺线+自动翻转)
///       / 移动路径(白虚线+箭头) / 速度标签。
/// 1Hz tick, 按任务建/销毁槽, 所有对象登记 tracked 保证热重载(F9)安全。
/// 只读 FSC 公开 API, 保持 FSC 纯领域逻辑分离。
/// </summary>
public class MapOverlay
{
    // ==== 调参(装机实测后再校) ====
    private const float TickInterval = 1f;
    private const float KmPerMapUnit = 3.8164f;    // map-local × 3.8164 = km (与 FSC.DistKm 同)
    private const int RingSegments = 24;
    private const float RingThickness = 0.01f;     // 圈描边宽(板面单位)
    private const float FireThickness = 0.010f;    // 火力线宽(手绘墨线, 不过分粗)
    private const float LineThickness = 0.008f;    // 路径宽
    private const float LeaderThickness = 0.005f;  // 引导线宽(虚线, 比主线弱一档)
    private const float RulerStep = 0.3f;          // 火线上量距刻度间距(板面单位)
    private const float RulerHalfLen = 0.02f;      // 量距刻度半长(垂直线)
    private const float RulerTickThickness = 0.004f; // 量距刻度宽(极细)
    private const float CenterHalf = 0.03f;        // 圆心罗盘点小十字半长
    private const float CenterThickness = 0.006f;  // 罗盘点十字宽
    private const float CharThickness = 0.010f;    // 字符段线宽(装机实测: 0.008 太细, 加粗后无需描边)
    private const float TextScale = 0.5f;          // 文字整体缩放(装机实测: 原字号太喜感, 减半)
    private const float LabelSegW = 0.045f;        // 字符宽(板面单位, 继承 renchonghan)
    private const float LabelSpacing = 1.4f;       // 字符间距 = 字宽 × 系数
    private const float LabelOffset = 0.15f;       // 圈标签右上偏移(装机实测: 0.3 引导线太长, 减半)
    private const float PathLengthKm = 1.5f;       // 移动路径固定可见长度(km)
    private const float ArrowLen = 0.12f;          // 路径箭头半长(板面单位)
    private const float ZGeom = -0.005f;           // 几何层 z(装机实测: 0.02 太浮空, 压到贴近板面)
    private const float ZLabel = -0.008f;          // 标签层 z(比几何层略高, 压线可读)

    // 1930 手绘测量风: 统一墨线红家族(均匀色, 无渐变——手绘线不渐变)
    private static readonly Color InkRed = new(0.62f, 0.1f, 0.08f);          // 红墨线主色(圈/火线/刻度)
    private static readonly Color LeaderColor = new(0.5f, 0.08f, 0.07f);     // 引导线(略暗)
    private static readonly Color FireLabelColor = new(0.82f, 0.2f, 0.15f);  // 火力线标签文字(亮一档)
    private static readonly Color PathColor = new(0.9f, 0.9f, 0.9f);         // 白(移动路径, 尺规语义)
    private static readonly Color LabelColor = Color.white;

    private readonly FSC fcs;
    private Transform? mapSurface;
    private readonly List<GameObject> tracked = new();
    private readonly Dictionary<ArtilleryTask, Slot> slots = new();
    private float lastTick;

    public MapOverlay(FSC fcs) { this.fcs = fcs; }

    /// <summary>每帧调用, 内部 1Hz 节流。收集活动任务 → 更新/创建槽 → 销毁失效槽。</summary>
    public void Update()
    {
        if (Time.time - lastTick < TickInterval) return;
        lastTick = Time.time;
        // 场景可能尚未就绪, 每次空引用时重找
        if (mapSurface == null) mapSurface = GameObject.Find("Draggable Surface")?.transform;
        if (mapSurface == null || fcs.MapTable.Turret == null) return;

        var active = new List<ArtilleryTask>(3);
        if (fcs.LeftTask != null) active.Add(fcs.LeftTask);
        if (fcs.RightTask != null && !ReferenceEquals(fcs.RightTask, fcs.LeftTask)) active.Add(fcs.RightTask);
        foreach (var t in fcs.InFlight)
            if (!active.Contains(t)) active.Add(t);

        foreach (var t in active)
        {
            if (!slots.TryGetValue(t, out var slot)) { slot = new Slot(mapSurface); slots[t] = slot; }
            UpdateSlot(slot, t);
        }

        var stale = new List<ArtilleryTask>();
        foreach (var kv in slots)
            if (!active.Contains(kv.Key)) stale.Add(kv.Key);
        foreach (var t in stale) { DestroySlot(slots[t]); slots.Remove(t); }
    }

    /// <summary>热重载/卸载: 销毁全部渲染对象。</summary>
    public void Shutdown()
    {
        foreach (var go in tracked) { if (go != null) Object.Destroy(go); }
        tracked.Clear();
        slots.Clear();
    }

    // ==== 槽生命周期 ====

    /// <summary>一个任务对应的渲染槽。root 挂板面, 位置=落点, 几何都在 root 局部系。</summary>
    private sealed class Slot
    {
        public readonly GameObject root;
        public readonly List<GameObject> ring = new();   // 毁伤圈 24 段(手绘抖动)
        public readonly List<GameObject> rulerTicks = new(); // 火线量距刻度(数量随线长重建)
        public GameObject? centerMark;                   // 圆心罗盘点小十字
        public GameObject? fireLine;                     // 火力线
        public GameObject? leader;                       // 圈标签引线
        public GameObject? labelRoot;                    // 圈标签(文本变时重建)
        public GameObject? fireLabelRoot;                // 火力线标签(文本变时重建)
        public GameObject? pathRoot;                     // 移动路径(虚线+箭头, 挂板面绝对位)
        public string labelText = "";                    // 上次渲染文本, 仅变时重建
        public string fireLabelText = "";

        public Slot(Transform mapSurface)
        {
            root = new GameObject("FCS_OverlaySlot");
            root.transform.SetParent(mapSurface, false);
        }
    }

    private void DestroySlot(Slot s)
    {
        foreach (var go in Collect(s))
        {
            if (go == null) continue;
            tracked.Remove(go);
            Object.Destroy(go);
        }
    }

    private static IEnumerable<GameObject> Collect(Slot s)
    {
        yield return s.root;
        foreach (var g in s.ring) yield return g;
        foreach (var g in s.rulerTicks) yield return g;
        if (s.centerMark != null) yield return s.centerMark;
        if (s.fireLine != null) yield return s.fireLine;
        if (s.leader != null) yield return s.leader;
        if (s.labelRoot != null) yield return s.labelRoot;
        if (s.fireLabelRoot != null) yield return s.fireLabelRoot;
        if (s.pathRoot != null) yield return s.pathRoot;
    }

    // ==== 每 tick 更新 ====

    private void UpdateSlot(Slot s, ArtilleryTask t)
    {
        // 落点: 静态=目标位置; 移动=提前点(与 FSC 瞄准同公式)
        Vector3 impactMap = ImpactMapLocal(t);
        Vector3 player = fcs.MapTable.GetTurretLocal();
        s.root.transform.localPosition = new Vector3(impactMap.x, impactMap.y, ZGeom);

        // 毁伤圈: 24 段多边形, 半径 = 注册表同源 BlastRadiusKm; 固定抖动模拟手绘圆(帧间稳定)
        float r = t.BlastRadiusKm / KmPerMapUnit;
        if (r > 0f)
        {
            EnsureRing(s);
            for (int i = 0; i < RingSegments; i++)
            {
                float a0 = i * 2f * Mathf.PI / RingSegments;
                float a1 = (i + 1) * 2f * Mathf.PI / RingSegments;
                float r0 = r * (1f + RingJitter[i]);
                float r1 = r * (1f + RingJitter[(i + 1) % RingSegments]);
                var l = s.ring[i].GetComponent<Il2CppShapes.Line>();
                SetLine(l, new Vector3(Mathf.Cos(a0) * r0, Mathf.Sin(a0) * r0, 0f),
                           new Vector3(Mathf.Cos(a1) * r1, Mathf.Sin(a1) * r1, 0f));
            }
            foreach (var g in s.ring) if (!g.activeSelf) g.SetActive(true);
            if (s.centerMark != null && !s.centerMark.activeSelf) s.centerMark.SetActive(true);
        }
        else if (s.ring.Count > 0)
        {
            foreach (var g in s.ring) if (g.activeSelf) g.SetActive(false);
            if (s.centerMark != null && s.centerMark.activeSelf) s.centerMark.SetActive(false);
        }

        // 圈标签: 只保留倒计时数字(弹种前缀/单位后缀已按实测删减, 面板有完整信息)
        int secs = t.Fired
            ? Mathf.Max(0, (int)(t.EstimatedToF - (Time.time - t.FiredAt)))
            : (int)t.EstimatedToF;
        string labelText = secs.ToString();
        if (labelText != s.labelText)
        {
            s.labelText = labelText;
            RebuildText(s, ref s.labelRoot, labelText, LabelColor, s.root.transform,
                new Vector3(LabelOffset, LabelOffset, ZLabel - ZGeom));
            if (s.leader == null)
            {
                s.leader = MakeLine(s.root.transform, "FCS_OverlayLeader", LeaderColor, LeaderThickness,
                    new Vector3(LabelOffset, LabelOffset, ZLabel - ZGeom));
                tracked.Add(s.leader);
            }
            var ll = s.leader.GetComponent<Il2CppShapes.Line>();
            SetLine(ll, Vector3.zero, -new Vector3(LabelOffset, LabelOffset, 0f));
            ll.Dashed = true;   // 引导线虚线: 视觉模型建议与主火线(实线)区分
        }

        // 火力线: 玩家 → 落点(根局部系); 均匀墨线红(手绘线不渐变), 在飞(已击发)变虚线
        if (s.fireLine == null)
        {
            s.fireLine = MakeLine(s.root.transform, "FCS_OverlayFireLine", InkRed, FireThickness, Vector3.zero);
            tracked.Add(s.fireLine);
        }
        var fl = s.fireLine.GetComponent<Il2CppShapes.Line>();
        SetLine(fl, player - impactMap, Vector3.zero);
        fl.Dashed = t.Fired;   // 计划实线 / 在飞虚线

        // 手绘测量风: 沿火线等距短刻度(尺规量距感), 数量随线长变化时重建
        Vector3 fdir = impactMap - player;
        int needTicks = fdir.magnitude > 0.001f ? (int)(fdir.magnitude / RulerStep) : 0;
        if (s.rulerTicks.Count != needTicks)
        {
            foreach (var g in s.rulerTicks) { tracked.Remove(g); Object.Destroy(g); }
            s.rulerTicks.Clear();
            for (int i = 0; i < needTicks; i++)
            {
                var g = MakeLine(s.root.transform, "FCS_OverlayRulerTick", InkRed, RulerTickThickness, Vector3.zero);
                s.rulerTicks.Add(g);
                tracked.Add(g);
            }
        }
        if (needTicks > 0)
        {
            Vector3 fnorm = fdir.normalized;
            Vector3 fperp = new Vector3(-fnorm.y, fnorm.x, 0f);
            for (int i = 0; i < needTicks; i++)
            {
                Vector3 at = -fdir + fnorm * (RulerStep * (i + 1));   // 从炮口端量起
                var tl = s.rulerTicks[i].GetComponent<Il2CppShapes.Line>();
                SetLine(tl, at - fperp * RulerHalfLen, at + fperp * RulerHalfLen);
            }
        }

        // 火力线标签: 距离/方位角, 线中点, 顺线 + 自动翻转(位置/旋转每 tick 跟, 文本变才重建)
        var dir = impactMap - player;
        float distKm = dir.magnitude * KmPerMapUnit;
        float bearing = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;   // 0°=+Y, 顺时针(同 GetMarkTarget 约定)
        if (bearing < 0f) bearing += 360f;
        string fireLabelText = $"{distKm:F1}km {bearing:F0}°";   // 小写 km: 手绘测量标注习惯
        if (fireLabelText != s.fireLabelText)
        {
            s.fireLabelText = fireLabelText;
            RebuildText(s, ref s.fireLabelRoot, fireLabelText, FireLabelColor, s.root.transform,
                (player - impactMap) * 0.5f);
        }
        if (s.fireLabelRoot != null)
        {
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            if (angle > 90f && angle < 270f) angle += 180f;   // 自动翻转保证从观看角度正读
            s.fireLabelRoot.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        // 移动目标: 前进路线(白虚线+箭头, 板面绝对位) + 根部速度标签
        bool showPath = t.IsMoving && TargetLeadSolver.IsMoving(t.AimVel);
        if (showPath)
        {
            Vector3 now = fcs.MapTable.WorldToMapLocal(t.AimP0 + t.AimVel * (Time.time - t.AimStartTime));
            Vector3 pathDir = t.AimVel.normalized;
            float len = PathLengthKm / KmPerMapUnit;
            if (s.pathRoot == null)
            {
                s.pathRoot = new GameObject("FCS_OverlayPath");
                s.pathRoot.transform.SetParent(mapSurface, false);
                var dash = MakeLine(s.pathRoot.transform, "FCS_OverlayPathDash", PathColor, LineThickness, Vector3.zero);
                dash.GetComponent<Il2CppShapes.Line>().Dashed = true;   // 原生虚线, 无需贴图
                MakeLine(s.pathRoot.transform, "FCS_OverlayPathArrow", PathColor, LineThickness, Vector3.zero);
                MakeLine(s.pathRoot.transform, "FCS_OverlayPathArrow", PathColor, LineThickness, Vector3.zero);
                tracked.Add(s.pathRoot);
            }
            if (!s.pathRoot.activeSelf) s.pathRoot.SetActive(true);
            s.pathRoot.transform.localPosition = new Vector3(now.x, now.y, ZGeom);
            var dl = s.pathRoot.transform.GetChild(0).GetComponent<Il2CppShapes.Line>();
            var ar1 = s.pathRoot.transform.GetChild(1).GetComponent<Il2CppShapes.Line>();
            var ar2 = s.pathRoot.transform.GetChild(2).GetComponent<Il2CppShapes.Line>();
            Vector3 tip = pathDir * len;
            Vector3 perp = new Vector3(-pathDir.y, pathDir.x, 0f);
            SetLine(dl, Vector3.zero, tip);
            SetLine(ar1, tip, tip - pathDir * ArrowLen + perp * ArrowLen * 0.6f);
            SetLine(ar2, tip, tip - pathDir * ArrowLen - perp * ArrowLen * 0.6f);
            // 速度标签已按实测删除(文字全部减负, 只留火力线标签+倒计时数字)
        }
        else
        {
            if (s.pathRoot != null && s.pathRoot.activeSelf) s.pathRoot.SetActive(false);
        }
    }

    /// <summary>落点(世界) → 板面局部。移动目标用与 FSC 相同的提前点公式。</summary>
    private Vector3 ImpactMapLocal(ArtilleryTask t)
    {
        Vector3 impactWorld = t.position;
        if (t.IsMoving && TargetLeadSolver.IsMoving(t.AimVel))
        {
            float tof = ToFTable.FlightTime(t.distance, t.LoadedCharge);
            impactWorld = TargetLeadSolver.LeadPoint(t.AimP0, t.AimVel, Time.time - t.AimStartTime, tof);
        }
        return fcs.MapTable.WorldToMapLocal(impactWorld);
    }

    private void EnsureRing(Slot s)
    {
        if (s.ring.Count > 0) return;
        for (int i = 0; i < RingSegments; i++)
        {
            var g = MakeLine(s.root.transform, "FCS_OverlayRing", InkRed, RingThickness, Vector3.zero);
            s.ring.Add(g);
            tracked.Add(g);
        }
        // 圆心罗盘点小十字(测绘本命, 手绘十字比 HUD 准星刻度更贴 1930 风格)
        if (s.centerMark == null)
        {
            s.centerMark = new GameObject("FCS_OverlayCenterMark");
            s.centerMark.transform.SetParent(s.root.transform, false);
            var h = MakeLine(s.centerMark.transform, "FCS_OverlayCenterSeg", InkRed, CenterThickness, Vector3.zero);
            var v = MakeLine(s.centerMark.transform, "FCS_OverlayCenterSeg", InkRed, CenterThickness, Vector3.zero);
            SetLine(h.GetComponent<Il2CppShapes.Line>(), new Vector3(-CenterHalf, 0f, 0f), new Vector3(CenterHalf, 0f, 0f));
            SetLine(v.GetComponent<Il2CppShapes.Line>(), new Vector3(0f, -CenterHalf, 0f), new Vector3(0f, CenterHalf, 0f));
            tracked.Add(s.centerMark);
        }
    }

    /// <summary>文本变才重建: 销毁旧根, 建新根(线段字符), 挂到 parent 的 localPos。</summary>
    private void RebuildText(Slot s, ref GameObject? root, string text, Color color,
        Transform parent, Vector3 localPos)
    {
        if (root != null) { tracked.Remove(root); Object.Destroy(root); root = null; }
        if (string.IsNullOrEmpty(text)) return;
        root = BuildText(parent, text, color);
        root.transform.localPosition = localPos;
        tracked.Add(root);
    }

    /// <summary>线段字符文本根节点: 水平居中, 每字符若干 Line 段(无描边, 实测同面重叠发灰)。</summary>
    private static GameObject BuildText(Transform parent, string text, Color color)
    {
        var go = new GameObject("FCS_OverlayText");
        go.transform.SetParent(parent, false);
        float segW = LabelSegW * TextScale;
        float step = segW * LabelSpacing;
        float total = (text.Length - 1) * step + segW;
        go.transform.localPosition = new Vector3(-total / 2f, 0f, 0f);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ') continue;
            bool lower = char.IsLower(c);
            float scale = lower ? 0.72f : 1f;
            float dy = lower ? -0.3f : 0f;   // 小写字母压低到基线
            float x0 = i * step + (1f - scale) * segW * 0.5f;
            foreach (var (a, b) in SegmentsFor(c))
            {
                var pa = new Vector3(x0 + a.x * segW * scale, (a.y + dy) * segW * scale, 0f);
                var pb = new Vector3(x0 + b.x * segW * scale, (b.y + dy) * segW * scale, 0f);
                var cg = MakeLine(go.transform, "FCS_CharSeg", color, CharThickness, Vector3.zero);
                SetLine(cg.GetComponent<Il2CppShapes.Line>(), pa, pb);
            }
        }
        return go;
    }

    // ==== 渲染对象工厂 ====

    private static GameObject MakeLine(Transform parent, string name, Color color, float thickness, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        var l = go.AddComponent<Il2CppShapes.Line>();
        l.Thickness = thickness;
        l.Color = color;
        l.ColorStart = color;
        l.ColorEnd = color;
        return go;
    }

    private static void SetLine(Il2CppShapes.Line line, Vector3 start, Vector3 end)
    {
        line.Start = start;
        line.End = end;
    }

    // ==== 线段字符集 ====
    // 单位格: 宽 1 × 高 1.6, 缩放 LabelSegW。数字/部分字母用七段 a-g(继承 renchonghan),
    // 全字母用笔画表(覆盖 20 种弹种枚举名), 符号 ° . / - + ? : 备齐。小写按 0.72 缩 + 压低。

    private static (Vector2, Vector2)[] SegmentsFor(char c)
    {
        switch (char.ToUpperInvariant(c))
        {
            // 手写体数字(1930 手绘测量标注: 开放 4、带横杠 7、手绘弧线 2/3/8, 替代七段数码的现代感)
            case '0': return P((0.28f, 0.1f, 0.18f, 1.5f), (0.18f, 1.5f, 0.72f, 1.5f), (0.72f, 1.5f, 0.8f, 0.12f), (0.8f, 0.12f, 0.28f, 0.1f));
            case '1': return P((0.32f, 1.42f, 0.5f, 1.6f), (0.5f, 1.6f, 0.5f, 0.12f), (0.32f, 0.2f, 0.65f, 0.1f));
            case '2': return P((0.18f, 1.32f, 0.38f, 1.55f), (0.38f, 1.55f, 0.62f, 1.55f), (0.62f, 1.55f, 0.78f, 1.3f),
                (0.78f, 1.3f, 0.68f, 1f), (0.68f, 1f, 0.52f, 0.9f), (0.52f, 0.9f, 0.15f, 0.08f), (0.15f, 0.08f, 0.85f, 0.08f));
            case '3': return P((0.22f, 1.42f, 0.42f, 1.58f), (0.42f, 1.58f, 0.62f, 1.58f), (0.62f, 1.58f, 0.74f, 1.4f),
                (0.74f, 1.4f, 0.64f, 1.15f), (0.64f, 1.15f, 0.42f, 1.05f), (0.42f, 1.05f, 0.66f, 0.95f),
                (0.66f, 0.95f, 0.75f, 0.72f), (0.75f, 0.72f, 0.6f, 0.45f), (0.6f, 0.45f, 0.32f, 0.38f),
                (0.32f, 0.38f, 0.22f, 0.52f), (0.22f, 0.52f, 0.3f, 0.68f));
            case '4': return P((0.55f, 1.6f, 0.55f, 0.1f), (0.55f, 0.72f, 0.12f, 0.95f), (0.12f, 0.95f, 0.82f, 0.95f));
            case '5': return P((0.68f, 1.6f, 0.22f, 1.6f), (0.22f, 1.6f, 0.22f, 1.05f), (0.22f, 1.05f, 0.6f, 0.95f),
                (0.6f, 0.95f, 0.72f, 0.75f), (0.72f, 0.75f, 0.6f, 0.42f), (0.6f, 0.42f, 0.32f, 0.38f),
                (0.32f, 0.38f, 0.22f, 0.55f));
            case '6': return P((0.68f, 1.5f, 0.45f, 1.6f), (0.45f, 1.6f, 0.22f, 1.35f), (0.22f, 1.35f, 0.16f, 0.6f),
                (0.16f, 0.6f, 0.25f, 0.2f), (0.25f, 0.2f, 0.5f, 0.1f), (0.5f, 0.1f, 0.72f, 0.25f),
                (0.72f, 0.25f, 0.75f, 0.55f), (0.75f, 0.55f, 0.45f, 0.6f), (0.45f, 0.6f, 0.24f, 0.48f));
            case '7': return P((0.15f, 1.6f, 0.78f, 1.6f), (0.78f, 1.6f, 0.48f, 0.05f), (0.48f, 0.85f, 0.68f, 0.85f));
            case '8': return P((0.3f, 1.52f, 0.55f, 1.6f), (0.55f, 1.6f, 0.7f, 1.42f), (0.7f, 1.42f, 0.6f, 1.08f),
                (0.6f, 1.08f, 0.3f, 1.08f), (0.3f, 1.08f, 0.24f, 1.35f), (0.24f, 1.35f, 0.3f, 1.52f),
                (0.3f, 0.85f, 0.5f, 0.82f), (0.5f, 0.82f, 0.7f, 0.6f), (0.7f, 0.6f, 0.72f, 0.15f),
                (0.72f, 0.15f, 0.5f, 0.05f), (0.5f, 0.05f, 0.25f, 0.12f), (0.25f, 0.12f, 0.28f, 0.55f));
            case '9': return P((0.72f, 0.45f, 0.55f, 0.1f), (0.55f, 0.1f, 0.3f, 0.15f), (0.3f, 0.15f, 0.2f, 0.35f),
                (0.2f, 0.35f, 0.2f, 0.85f), (0.2f, 0.85f, 0.3f, 1.1f), (0.3f, 1.1f, 0.55f, 1.15f),
                (0.55f, 1.15f, 0.75f, 0.98f), (0.75f, 0.98f, 0.72f, 0.65f), (0.72f, 0.65f, 0.5f, 0.62f),
                (0.5f, 0.62f, 0.28f, 0.7f));
            case 'A': return P((0.12f, 0f, 0.5f, 1.6f), (0.88f, 0f, 0.5f, 1.6f), (0.3f, 0.72f, 0.7f, 0.72f));
            case 'B': return P((0.28f, 0f, 0.28f, 1.6f), (0.28f, 1.6f, 0.62f, 1.6f), (0.62f, 1.6f, 0.7f, 1.5f),
                (0.7f, 1.5f, 0.7f, 1.2f), (0.7f, 1.2f, 0.6f, 1.1f), (0.6f, 1.1f, 0.28f, 1.1f),
                (0.28f, 1.1f, 0.66f, 1.1f), (0.66f, 1.1f, 0.74f, 0.98f), (0.74f, 0.98f, 0.74f, 0.12f),
                (0.74f, 0.12f, 0.66f, 0f), (0.66f, 0f, 0.28f, 0f));
            case 'C': return P((0.82f, 1.28f, 0.6f, 1.55f), (0.6f, 1.55f, 0.35f, 1.55f), (0.35f, 1.55f, 0.18f, 1.32f),
                (0.18f, 1.32f, 0.18f, 0.28f), (0.18f, 0.28f, 0.35f, 0.05f), (0.35f, 0.05f, 0.6f, 0.05f),
                (0.6f, 0.05f, 0.82f, 0.32f));
            case 'D': return P((0.3f, 0f, 0.3f, 1.6f), (0.3f, 1.6f, 0.62f, 1.6f), (0.62f, 1.6f, 0.8f, 1.42f),
                (0.8f, 1.42f, 0.8f, 0.18f), (0.8f, 0.18f, 0.62f, 0f), (0.62f, 0f, 0.3f, 0f));
            case 'E': return P((0.3f, 0f, 0.3f, 1.6f), (0.3f, 1.6f, 0.85f, 1.6f), (0.3f, 0.8f, 0.75f, 0.8f),
                (0.3f, 0f, 0.85f, 0f));
            case 'F': return P((0.3f, 0f, 0.3f, 1.6f), (0.3f, 1.6f, 0.85f, 1.6f), (0.3f, 0.8f, 0.75f, 0.8f));
            case 'G': return P((0.82f, 1.28f, 0.6f, 1.55f), (0.6f, 1.55f, 0.35f, 1.55f), (0.35f, 1.55f, 0.18f, 1.32f),
                (0.18f, 1.32f, 0.18f, 0.28f), (0.18f, 0.28f, 0.35f, 0.05f), (0.35f, 0.05f, 0.6f, 0.05f),
                (0.6f, 0.05f, 0.82f, 0.32f), (0.4f, 0.5f, 0.9f, 0.5f), (0.9f, 0.5f, 0.9f, 0.85f));
            case 'H': return P((0.2f, 0f, 0.2f, 1.6f), (0.8f, 0f, 0.8f, 1.6f), (0.2f, 0.8f, 0.8f, 0.8f));
            case 'I': return P((0.28f, 1.6f, 0.72f, 1.6f), (0.5f, 0f, 0.5f, 1.6f), (0.28f, 0f, 0.72f, 0f));
            case 'J': return P((0.28f, 1.5f, 0.5f, 1.6f), (0.5f, 1.6f, 0.72f, 1.6f), (0.72f, 1.6f, 0.72f, 0.3f),
                (0.72f, 0.3f, 0.5f, 0.06f), (0.5f, 0.06f, 0.32f, 0.14f));
            case 'K': return P((0.25f, 0f, 0.25f, 1.6f), (0.25f, 0.8f, 0.8f, 1.6f), (0.25f, 0.8f, 0.8f, 0f));
            case 'L': return P((0.3f, 0f, 0.3f, 1.6f), (0.3f, 0f, 0.85f, 0f));
            case 'M': return P((0.15f, 0f, 0.15f, 1.6f), (0.15f, 1.6f, 0.5f, 0.55f), (0.5f, 0.55f, 0.85f, 1.6f),
                (0.85f, 1.6f, 0.85f, 0f));
            case 'N': return P((0.2f, 0f, 0.2f, 1.6f), (0.2f, 1.6f, 0.8f, 0f), (0.8f, 0f, 0.8f, 1.6f));
            case 'O': return P((0.2f, 0.05f, 0.2f, 1.55f), (0.2f, 1.55f, 0.8f, 1.55f), (0.8f, 1.55f, 0.8f, 0.05f),
                (0.8f, 0.05f, 0.2f, 0.05f));
            case 'P': return P((0.28f, 0f, 0.28f, 1.6f), (0.28f, 1.6f, 0.62f, 1.6f), (0.62f, 1.6f, 0.7f, 1.5f),
                (0.7f, 1.5f, 0.7f, 1.2f), (0.7f, 1.2f, 0.6f, 1.1f), (0.6f, 1.1f, 0.28f, 1.1f), (0.28f, 0.5f, 0.66f, 0.5f));
            case 'Q': return P((0.2f, 0.05f, 0.2f, 1.55f), (0.2f, 1.55f, 0.8f, 1.55f), (0.8f, 1.55f, 0.8f, 0.05f),
                (0.8f, 0.05f, 0.2f, 0.05f), (0.55f, 0.28f, 0.85f, 0.05f));
            case 'R': return P((0.28f, 0f, 0.28f, 1.6f), (0.28f, 1.6f, 0.62f, 1.6f), (0.62f, 1.6f, 0.7f, 1.5f),
                (0.7f, 1.5f, 0.7f, 1.2f), (0.7f, 1.2f, 0.6f, 1.1f), (0.6f, 1.1f, 0.28f, 1.1f), (0.28f, 0.5f, 0.66f, 0.5f),
                (0.3f, 0.45f, 0.85f, 0f));
            case 'S': return P((0.7f, 1.5f, 0.48f, 1.6f), (0.48f, 1.6f, 0.28f, 1.52f), (0.28f, 1.52f, 0.25f, 1.3f),
                (0.25f, 1.3f, 0.32f, 1.05f), (0.32f, 1.05f, 0.72f, 0.95f), (0.72f, 0.95f, 0.75f, 0.68f),
                (0.75f, 0.68f, 0.6f, 0.45f), (0.6f, 0.45f, 0.32f, 0.38f), (0.32f, 0.38f, 0.22f, 0.5f),
                (0.22f, 0.5f, 0.3f, 0.62f));
            case 'T': return P((0.2f, 1.6f, 0.8f, 1.6f), (0.5f, 1.6f, 0.5f, 0f));
            case 'U': return P((0.2f, 1.6f, 0.2f, 0.3f), (0.2f, 0.3f, 0.5f, 0.08f), (0.5f, 0.08f, 0.8f, 0.3f),
                (0.8f, 0.3f, 0.8f, 1.6f));
            case 'V': return P((0.15f, 1.6f, 0.5f, 0f), (0.5f, 0f, 0.85f, 1.6f));
            case 'W': return P((0.1f, 1.6f, 0.28f, 0f), (0.28f, 0f, 0.5f, 0.85f), (0.5f, 0.85f, 0.72f, 0f),
                (0.72f, 0f, 0.9f, 1.6f));
            case 'X': return P((0.15f, 1.6f, 0.85f, 0f), (0.85f, 1.6f, 0.15f, 0f));
            case 'Y': return P((0.15f, 1.6f, 0.5f, 0.75f), (0.5f, 0.75f, 0.85f, 1.6f), (0.5f, 0.75f, 0.5f, 0f));
            case 'Z': return P((0.15f, 1.6f, 0.85f, 1.6f), (0.85f, 1.6f, 0.15f, 0f), (0.15f, 0f, 0.85f, 0f));
            case '°': return P((0.32f, 1.32f, 0.62f, 1.32f), (0.62f, 1.32f, 0.62f, 1.58f),
                (0.62f, 1.58f, 0.32f, 1.58f), (0.32f, 1.58f, 0.32f, 1.32f));
            case '.': return P((0.35f, 0.06f, 0.6f, 0.06f));
            case '/': return P((0.15f, 1.6f, 0.85f, 0f));
            case '-': return P((0.2f, 0.8f, 0.8f, 0.8f));
            case '+': return P((0.4f, 0.55f, 0.4f, 1.05f), (0.15f, 0.8f, 0.65f, 0.8f));
            case '?': return P((0.2f, 1.45f, 0.35f, 1.6f), (0.35f, 1.6f, 0.6f, 1.6f), (0.6f, 1.6f, 0.75f, 1.45f),
                (0.75f, 1.45f, 0.72f, 1.18f), (0.72f, 1.18f, 0.5f, 1f), (0.5f, 1f, 0.5f, 0.72f),
                (0.42f, 0.12f, 0.58f, 0.12f));
            case ':': return P((0.35f, 1.1f, 0.6f, 1.1f), (0.35f, 0.35f, 0.6f, 0.35f));
            default: return Array.Empty<(Vector2, Vector2)>();
        }
    }

    /// <summary>手绘圆抖动表: 固定伪随机序列, 帧间稳定(不闪烁), 半径 ±5% 的不完美感。</summary>
    private static readonly float[] RingJitter = BuildJitter(RingSegments, 0.05f);

    private static float[] BuildJitter(int n, float amp)
    {
        var r = new float[n];
        uint s = 0x9E3779B9u;
        for (int i = 0; i < n; i++)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            r[i] = (s / (float)uint.MaxValue * 2f - 1f) * amp;
        }
        return r;
    }

    /// <summary>浮点笔画 → 线段数组。</summary>
    private static (Vector2, Vector2)[] P(params (float x1, float y1, float x2, float y2)[] pts)
    {
        var arr = new (Vector2, Vector2)[pts.Length];
        for (int i = 0; i < pts.Length; i++)
            arr[i] = (new Vector2(pts[i].x1, pts[i].y1), new Vector2(pts[i].x2, pts[i].y2));
        return arr;
    }
}
