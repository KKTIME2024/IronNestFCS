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
    private const int RingSegments = 16;           // 圈段数(性能: 24→16, 每任务省 8 渲染对象)
    private const float RingThickness = 0.005f;    // 圈描边宽(全部线宽砍半后)
    private const float FireThickness = 0.005f;    // 火力线宽(砍半)
    private const float LineThickness = 0.004f;    // 路径宽(砍半)
    private const float LeaderThickness = 0.003f;  // 引导线宽(0.005/2=0.0025 低于可渲染下限 0.003)
    private const float CenterHalf = 0.03f;        // 圆心罗盘点小十字半长
    private const float CenterThickness = 0.003f;  // 罗盘点十字宽(砍半)
    private const int OverlayQueue = 3500;         // 绘制队列: 在照片层之后(部分关卡照片是透明 3000+, 2500 会被其 alpha 盖成半透明)
    private const float CharThickness = 0.005f;    // 字符段线宽(砍半)
    private const float TextScale = 0.5f;          // 文字整体缩放(装机实测: 原字号太喜感, 减半)
    private const float LabelSegW = 0.045f;        // 字符宽(板面单位, 继承 renchonghan)
    private const float LabelSpacing = 1.4f;       // 字符间距 = 字宽 × 系数
    private const float LabelOffset = 0.075f;      // 圈标签右上偏移(自适应前 0.15 再砍半, 引导线跟随)
    private const float PerpLabelOffset = 0.06f;   // 火力线标签离线垂直偏移(砍半, 左右炮镜像)
    private const float SpeedLabelOffsetY = 0.15f; // 速度注记距路径根部偏移(砍半)
    private const float EdgeMargin = 0.15f;        // 边缘自适应安全边距(标签不出板面)
    private const float ArrowLen = 0.08f;          // 路径箭头基长(按路径长度等比缩放, 尖峰不过大)
    private const float ZGeom = -0.015f;           // 几何层 z(-0.01 被航拍照片层覆盖, -0.02 浮空)
    private const float ZLabel = -0.02f;           // 标签层 z(比几何层略高, 压线可读)

    // 1930 手绘测量风: 统一墨线红家族(均匀色, 无渐变——手绘线不渐变)
    private static readonly Color InkRed = new(0.62f, 0.1f, 0.08f);          // 红墨线主色(圈/火线/刻度)
    private static readonly Color LeaderColor = new(0.5f, 0.08f, 0.07f);     // 引导线(略暗)
    private static readonly Color FireLabelColor = new(0.82f, 0.2f, 0.15f);  // 火力线标签文字(亮一档)
    private static readonly Color PathColor = new(0.9f, 0.9f, 0.9f);         // 白(移动路径, 尺规语义)
    private static readonly Color LabelColor = Color.white;
    private static readonly Color QueuedColor = new(0.55f, 0.6f, 0.62f);     // 排队任务铅笔灰(计划态, 活动后转红墨)

    private readonly FSC fcs;
    private Transform? mapSurface;
    private Vector2 boardHalf = new(3.0f, 1.35f);  // 板面半宽/半高(从 Draggable Surface 的 BoxCollider 读, 回退值)
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
        if (mapSurface == null)
        {
            mapSurface = GameObject.Find("Draggable Surface")?.transform;
            if (mapSurface != null)
            {
                var bc = mapSurface.GetComponent<BoxCollider>();
                if (bc != null) boardHalf = new Vector2(bc.size.x * 0.5f, bc.size.y * 0.5f);
            }
        }
        if (mapSurface == null || fcs.MapTable.Turret == null) return;

        var active = new List<ArtilleryTask>(8);
        if (fcs.LeftTask != null) active.Add(fcs.LeftTask);
        if (fcs.RightTask != null && !ReferenceEquals(fcs.RightTask, fcs.LeftTask)) active.Add(fcs.RightTask);
        foreach (var t in fcs.InFlight)
            if (!SameTask(active, t)) active.Add(t);
        // 排队任务也上图(2026-08-17 用户): 待派发任务的圈/标签以铅笔灰呈现, 面板不再重复几何信息
        foreach (var t in fcs.QueueCan)
            if (!SameTask(active, t)) active.Add(t);

        foreach (var t in active)
        {
            if (!slots.TryGetValue(t, out var slot))
            {
                slot = new Slot(mapSurface);
                slots[t] = slot;
            }
            // 角色变化(排队↔活动↔在飞)时更新排队态与炮位镜像; 在飞任务沿用创建时标记
            slot.queued = !ReferenceEquals(t, fcs.LeftTask) && !ReferenceEquals(t, fcs.RightTask) && !t.Fired;
            if (ReferenceEquals(t, fcs.RightTask)) slot.barrel = 1;
            else if (ReferenceEquals(t, fcs.LeftTask)) slot.barrel = 0;
            UpdateSlot(slot, t);
        }

        var stale = new List<ArtilleryTask>();
        foreach (var kv in slots)
            if (!SameTask(active, kv.Key)) stale.Add(kv.Key);
        foreach (var t in stale) { DestroySlot(slots[t]); slots.Remove(t); }
    }

    /// <summary>引用相等判断(ArtilleryTask 未重写 Equals, 避免误判不同任务)。</summary>
    private static bool SameTask(List<ArtilleryTask> list, ArtilleryTask t)
    {
        foreach (var x in list) if (ReferenceEquals(x, t)) return true;
        return false;
    }

    /// <summary>热重载/卸载: 销毁全部渲染对象与共享材质。</summary>
    public void Shutdown()
    {
        foreach (var go in tracked) { if (go != null) Object.Destroy(go); }
        if (overlayMat != null) { Object.Destroy(overlayMat); overlayMat = null; }
        tracked.Clear();
        slots.Clear();
    }

    // ==== 槽生命周期 ====

    /// <summary>一个任务对应的渲染槽。root 挂板面, 位置=落点, 几何都在 root 局部系。</summary>
    private sealed class Slot
    {
        public readonly GameObject root;
        public readonly List<GameObject> ring = new();   // 毁伤圈 24 段
        public GameObject? centerMark;                   // 圆心罗盘点小十字
        public GameObject? fireLine;                     // 火力线
        public GameObject? leader;                       // 圈标签引线
        public GameObject? labelRoot;                    // 圈标签(文本变时重建)
        public GameObject? fireLabelRoot;                // 火力线标签(文本变时重建)
        public GameObject? pathRoot;                     // 移动路径(虚线+箭头+当前位置十字, 挂板面绝对位)
        public GameObject? pathCross;                    // 当前位置罗盘点(路径根部)
        public GameObject? speedRoot;                    // 速度注记(文本变时重建)
        public string labelText = "";                    // 上次渲染文本, 仅变时重建
        public string fireLabelText = "";
        public string speedText = "";
        public int barrel;                               // 0=左炮 1=右炮(标签镜像方向用; 在飞任务沿用创建时标记)
        public bool queued;                              // 排队态(未派发): 铅笔灰虚线圈+标签, 不画火力线
        public bool lastQueued;                          // 上次排队态(切换时强制重建标签换色)
        public Vector3 lastImpact = new(float.MinValue, float.MinValue, float.MinValue); // 上次落点(几何仅变化时更新)
        public Vector3? frozenImpact;                    // 击发瞬间锁存的落点(在飞期火力线/弹着点固定)
        public Vector3 labelOff;                         // 上次使用的标签偏移(边缘自适应变化时更新引导线)

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
        if (s.centerMark != null) yield return s.centerMark;
        if (s.fireLine != null) yield return s.fireLine;
        if (s.leader != null) yield return s.leader;
        if (s.labelRoot != null) yield return s.labelRoot;
        if (s.fireLabelRoot != null) yield return s.fireLabelRoot;
        if (s.pathRoot != null) yield return s.pathRoot;
        if (s.speedRoot != null) yield return s.speedRoot;
    }

    // ==== 每 tick 更新 ====

    private void UpdateSlot(Slot s, ArtilleryTask t)
    {
        // 落点: 静态=目标位置; 移动=提前点(与 FSC 瞄准同公式)
        // 击发后锁存: 在飞期火力线/弹着点固定, 不再随目标外推(与 FSC 在飞注册表语义一致)
        Vector3 impactMap = ImpactMapLocal(t);
        if (t.Fired)
        {
            if (s.frozenImpact == null) s.frozenImpact = impactMap;
            impactMap = s.frozenImpact.Value;
        }
        else
        {
            s.frozenImpact = null;
        }
        // 静态几何仅在落点变化时更新: 静态目标任务周期内零更新, 在飞任务击发后零更新
        bool impactChanged = (impactMap - s.lastImpact).sqrMagnitude > 1e-10f;
        if (impactChanged) s.lastImpact = impactMap;
        Vector3 player = fcs.MapTable.GetTurretLocal();

        if (impactChanged)
        {
        s.root.transform.localPosition = new Vector3(impactMap.x, impactMap.y, ZGeom);

        // 毁伤圈: 24 段正圆, 半径 = 注册表同源 BlastRadiusKm(回滚: 缺口/二次描圈像渲染 bug)
        float r = t.BlastRadiusKm / KmPerMapUnit;
        if (r > 0f)
        {
            EnsureRing(s);
            for (int i = 0; i < RingSegments; i++)
            {
                float a0 = i * 2f * Mathf.PI / RingSegments;
                float a1 = (i + 1) * 2f * Mathf.PI / RingSegments;
                var l = s.ring[i].GetComponent<Il2CppShapes.Line>();
                SetLine(l, new Vector3(Mathf.Cos(a0) * r, Mathf.Sin(a0) * r, 0f),
                           new Vector3(Mathf.Cos(a1) * r, Mathf.Sin(a1) * r, 0f));
                if (!l.gameObject.activeSelf) l.gameObject.SetActive(true);
            }
            if (s.centerMark != null && !s.centerMark.activeSelf) s.centerMark.SetActive(true);
        }
        else if (s.ring.Count > 0)
        {
            foreach (var g in s.ring) if (g.activeSelf) g.SetActive(false);
            if (s.centerMark != null && s.centerMark.activeSelf) s.centerMark.SetActive(false);
        }
        }   // impactChanged 块结束(落点未变则不再触碰静态几何)

        // 排队态样式(2026-08-17): 铅笔灰虚线圈 = 计划中; 活动/在飞恢复红墨实线
        if (s.lastQueued != s.queued)
        {
            s.lastQueued = s.queued;
            s.labelText = "";      // 强制重建: 标签颜色随排队/活动态切换
            s.fireLabelText = "";
            s.speedText = "";
        }
        if (s.ring.Count > 0)
        {
            var ringColor = s.queued ? QueuedColor : InkRed;
            foreach (var g in s.ring)
            {
                var l = g.GetComponent<Il2CppShapes.Line>();
                l.Color = ringColor; l.ColorStart = ringColor; l.ColorEnd = ringColor;
                l.Dashed = s.queued;
            }
            if (s.centerMark != null)
            {
                foreach (var cl in s.centerMark.GetComponentsInChildren<Il2CppShapes.Line>(true))
                { cl.Color = ringColor; cl.ColorStart = ringColor; cl.ColorEnd = ringColor; }
            }
        }
        if (s.leader != null)
        {
            var lc = s.queued ? QueuedColor : LeaderColor;
            var ll = s.leader.GetComponent<Il2CppShapes.Line>();
            ll.Color = lc; ll.ColorStart = lc; ll.ColorEnd = lc;
        }

        // 圈标签: 手写注记式倒计时(炮兵秒符号 "); 左炮右上/右炮左上镜像; 边缘自适应翻转(出框侧反折)
        int secs = t.Fired
            ? Mathf.Max(0, (int)(t.EstimatedToF - (Time.time - t.FiredAt)))
            : (int)t.EstimatedToF;
        string labelText = secs + "\"";
        Vector3 labelOff = AdaptiveLabelOff(s, impactMap);
        if (labelOff != s.labelOff)
        {
            s.labelOff = labelOff;
            if (s.labelRoot != null) s.labelRoot.transform.localPosition = labelOff;
            if (s.leader != null)
            {
                s.leader.transform.localPosition = labelOff;
                var ll = s.leader.GetComponent<Il2CppShapes.Line>();
                SetLine(ll, Vector3.zero, -new Vector3(labelOff.x, labelOff.y, 0f));
            }
        }
        if (labelText != s.labelText)
        {
            s.labelText = labelText;
            RebuildText(s, ref s.labelRoot, labelText, s.queued ? QueuedColor : LabelColor, s.root.transform, labelOff);
            if (s.leader == null)
            {
                s.leader = MakeLine(s.root.transform, "FCS_OverlayLeader", LeaderColor, LeaderThickness, labelOff);
                tracked.Add(s.leader);
            }
            var ll = s.leader.GetComponent<Il2CppShapes.Line>();
            SetLine(ll, Vector3.zero, -new Vector3(labelOff.x, labelOff.y, 0f));
            ll.Dashed = true;   // 引导线虚线: 与主火线(实线)区分
        }

        // 火力线: 玩家 → 落点(根局部系); 排队任务只给圈/标签不画线(避免多任务线网交叉)
        if (s.queued)
        {
            if (s.fireLine != null && s.fireLine.activeSelf) s.fireLine.SetActive(false);
        }
        else
        {
            if (s.fireLine == null)
            {
                s.fireLine = MakeLine(s.root.transform, "FCS_OverlayFireLine", InkRed, FireThickness, Vector3.zero);
                tracked.Add(s.fireLine);
            }
            var fl = s.fireLine.GetComponent<Il2CppShapes.Line>();
            fl.Dashed = t.Fired;   // 计划实线 / 在飞虚线(击发翻转可能发生在落点未变的 tick, 单独每 tick 设)
            if (impactChanged) SetLine(fl, player - impactMap, Vector3.zero);
        }

        // 火力线标签: 距离/方位角; 线中点垂直线偏移(左炮线左/右炮线右, 防双线标签重合), 顺线旋转+自动翻转
        var dir = impactMap - player;
        float distKm = dir.magnitude * KmPerMapUnit;
        float bearing = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;   // 0°=+Y, 顺时针(同 GetMarkTarget 约定)
        if (bearing < 0f) bearing += 360f;
        // 2026-08-17 修复(用户: 左向火力线标签翻到线下且文字镜像): 线指向左半面(超出 ±90°)时
        // 旋转归一化到(-90,90]保证文字正读——旧判定 (90,270) 漏掉 (-180,-90) 象限(左下向线文字倒置);
        // 同时翻转垂直线偏移侧, 标签与右向线一致停在线上方, 不再翻到线下。
        float lineAngle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        bool flipSide = lineAngle > 90f || lineAngle < -90f;
        if (flipSide) lineAngle += 180f;
        Vector3 firePerp = dir.magnitude > 0.001f
            ? new Vector3(-dir.y, dir.x, 0f).normalized * PerpLabelOffset * (s.barrel == 0 ? 1f : -1f)
            : Vector3.zero;
        if (flipSide) firePerp = -firePerp;
        string fireLabelText = $"{distKm:F1}km {bearing:F0}°";   // 小写 km: 手绘测量标注习惯
        if (fireLabelText != s.fireLabelText)
        {
            s.fireLabelText = fireLabelText;
            Vector3 fireLabelPos = (player - impactMap) * 0.5f + firePerp;
            RebuildText(s, ref s.fireLabelRoot, fireLabelText, s.queued ? QueuedColor : FireLabelColor, s.root.transform, fireLabelPos);
        }
        if (s.fireLabelRoot != null && impactChanged)
        {
            s.fireLabelRoot.transform.localRotation = Quaternion.Euler(0f, 0f, lineAngle);
        }

        // 移动目标: 前进路线 = 当前位置 → 落点(提前点), 白虚线+箭头尖端扎进毁伤圈(排队任务不画路径)
        bool showPath = !s.queued && t.IsMoving && TargetLeadSolver.IsMoving(t.AimVel);
        if (showPath)
        {
            Vector3 now = fcs.MapTable.WorldToMapLocal(t.AimP0 + t.AimVel * (Time.time - t.AimStartTime));
            Vector3 toImpact = impactMap - now;   // 方向 = 目标运动方向, 长度 = 距离提前点(速度×ToF)
            float len = toImpact.magnitude;
            if (len > 0.001f)
            {
                Vector3 pathDir = toImpact / len;
                float speedKmS = t.AimVel.magnitude * KmPerMapUnit;
                if (s.pathRoot == null)
                {
                    s.pathRoot = new GameObject("FCS_OverlayPath");
                    s.pathRoot.transform.SetParent(mapSurface, false);
                    var dash = MakeLine(s.pathRoot.transform, "FCS_OverlayPathDash", PathColor, LineThickness, Vector3.zero);
                    dash.GetComponent<Il2CppShapes.Line>().Dashed = true;   // 原生虚线, 无需贴图
                    MakeLine(s.pathRoot.transform, "FCS_OverlayPathArrow", PathColor, LineThickness, Vector3.zero);
                    MakeLine(s.pathRoot.transform, "FCS_OverlayPathArrow", PathColor, LineThickness, Vector3.zero);
                    // 当前位置罗盘点(与圆心罗盘点同款红墨线小十字): 现在在哪 → 会落到哪
                    s.pathCross = new GameObject("FCS_OverlayPathCross");
                    s.pathCross.transform.SetParent(s.pathRoot.transform, false);
                    var ch = MakeLine(s.pathCross.transform, "FCS_OverlayPathCrossSeg", InkRed, CenterThickness, Vector3.zero);
                    var cv = MakeLine(s.pathCross.transform, "FCS_OverlayPathCrossSeg", InkRed, CenterThickness, Vector3.zero);
                    SetLine(ch.GetComponent<Il2CppShapes.Line>(), new Vector3(-CenterHalf, 0f, 0f), new Vector3(CenterHalf, 0f, 0f));
                    SetLine(cv.GetComponent<Il2CppShapes.Line>(), new Vector3(0f, -CenterHalf, 0f), new Vector3(0f, CenterHalf, 0f));
                    tracked.Add(s.pathRoot);
                }
                if (!s.pathRoot.activeSelf) s.pathRoot.SetActive(true);
                s.pathRoot.transform.localPosition = new Vector3(now.x, now.y, ZGeom);
                var dl = s.pathRoot.transform.GetChild(0).GetComponent<Il2CppShapes.Line>();
                var ar1 = s.pathRoot.transform.GetChild(1).GetComponent<Il2CppShapes.Line>();
                var ar2 = s.pathRoot.transform.GetChild(2).GetComponent<Il2CppShapes.Line>();
                Vector3 tip = pathDir * len;
                Vector3 perp = new Vector3(-pathDir.y, pathDir.x, 0f);
                float aLen = Mathf.Min(ArrowLen, len * 0.3f);   // 箭头随路径等比, 短路径尖峰不过大
                SetLine(dl, Vector3.zero, tip);
                SetLine(ar1, tip, tip - pathDir * aLen + perp * aLen * 0.6f);
                SetLine(ar2, tip, tip - pathDir * aLen - perp * aLen * 0.6f);

                // 速度注记(手写体): 路径根部上方, 文本变才重建
                string speedText = $"{speedKmS * 3600f:F0}km/h";
                if (speedText != s.speedText)
                {
                    s.speedText = speedText;
                    RebuildText(s, ref s.speedRoot, speedText, LabelColor, mapSurface, Vector3.zero);
                }
                if (s.speedRoot != null)
                {
                    if (!s.speedRoot.activeSelf) s.speedRoot.SetActive(true);
                    // 速度注记在路径根部上方; 近顶边时翻到下方, 不出板面
                    float sy = now.y + SpeedLabelOffsetY > boardHalf.y - EdgeMargin
                        ? -SpeedLabelOffsetY : SpeedLabelOffsetY;
                    s.speedRoot.transform.localPosition = new Vector3(now.x, now.y + sy, ZLabel);
                }
            }
            else
            {
                if (s.pathRoot != null && s.pathRoot.activeSelf) s.pathRoot.SetActive(false);
                if (s.speedRoot != null && s.speedRoot.activeSelf) s.speedRoot.SetActive(false);
            }
        }
        else
        {
            if (s.pathRoot != null && s.pathRoot.activeSelf) s.pathRoot.SetActive(false);
            if (s.speedRoot != null && s.speedRoot.activeSelf) s.speedRoot.SetActive(false);
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

    /// <summary>倒计时标签偏移: 默认左炮右上/右炮左上; 目标靠板边时向内侧/下方反折, 引导线不出框。</summary>
    private Vector3 AdaptiveLabelOff(Slot s, Vector3 impactMap)
    {
        float offX = s.barrel == 0 ? LabelOffset : -LabelOffset;
        float offY = LabelOffset;
        if (impactMap.y + offY > boardHalf.y - EdgeMargin) offY = -LabelOffset;
        else if (impactMap.y + offY < -boardHalf.y + EdgeMargin) offY = LabelOffset;
        if (impactMap.x + offX > boardHalf.x - EdgeMargin) offX = -LabelOffset;
        else if (impactMap.x + offX < -boardHalf.x + EdgeMargin) offX = LabelOffset;
        return new Vector3(offX, offY, ZLabel - ZGeom);
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

    /// <summary>共享线段材质: 所有 overlay 线段共用一个克隆实例(原实现每线一个材质实例, 材质切换 ~200 次/帧)。</summary>
    private static Material? overlayMat;

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
        var rend = go.GetComponent<Renderer>();
        if (rend != null)
        {
            if (overlayMat == null)
            {
                overlayMat = rend.material;   // 首次克隆游戏线段材质
                if (overlayMat != null) overlayMat.renderQueue = OverlayQueue;   // 统一队列, 只设一次
            }
            if (overlayMat != null) rend.material = overlayMat;   // 全部共享同一实例
        }
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
            // 七段数码(回滚: 手写体笔画表在 0.5 字号下糊成一团, 七段式在小字号可读性最好)
            case '0': return Seg7("abcdef");
            case '1': return Seg7("bc");
            case '2': return Seg7("abged");
            case '3': return Seg7("abgcd");
            case '4': return Seg7("fgbc");
            case '5': return Seg7("afgcd");
            case '6': return Seg7("afgedc");
            case '7': return Seg7("abc");
            case '8': return Seg7("abcdefg");
            case '9': return Seg7("abcdfg");
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
            case 'S': return Seg7("afgcd");
            case 'T': return P((0.2f, 1.6f, 0.8f, 1.6f), (0.5f, 1.6f, 0.5f, 0f));
            case 'U': return P((0.2f, 1.6f, 0.2f, 0.3f), (0.2f, 0.3f, 0.5f, 0.08f), (0.5f, 0.08f, 0.8f, 0.3f),
                (0.8f, 0.3f, 0.8f, 1.6f));
            case 'V': return P((0.15f, 1.6f, 0.5f, 0f), (0.5f, 0f, 0.85f, 1.6f));
            case 'W': return P((0.1f, 1.6f, 0.28f, 0f), (0.28f, 0f, 0.5f, 0.85f), (0.5f, 0.85f, 0.72f, 0f),
                (0.72f, 0f, 0.9f, 1.6f));
            case 'X': return P((0.15f, 1.6f, 0.85f, 0f), (0.85f, 1.6f, 0.15f, 0f));
            case 'Y': return P((0.15f, 1.6f, 0.5f, 0.75f), (0.5f, 0.75f, 0.85f, 1.6f), (0.5f, 0.75f, 0.5f, 0f));
            case 'Z': return P((0.15f, 1.6f, 0.85f, 1.6f), (0.85f, 1.6f, 0.15f, 0f), (0.15f, 0f, 0.85f, 0f));
            case '"': return P((0.28f, 1.5f, 0.4f, 1.28f), (0.5f, 1.5f, 0.62f, 1.28f));   // 炮兵秒注记
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

    /// <summary>七段数码段坐标(单位格, 与 renchonghan 一致)。</summary>
    private static (Vector2, Vector2)[] Seg7(string keys)
    {
        var list = new List<(Vector2, Vector2)>(keys.Length);
        foreach (char k in keys)
        {
            (Vector2 a, Vector2 b) = k switch
            {
                'a' => (new Vector2(0.05f, 1.6f), new Vector2(0.95f, 1.6f)),
                'b' => (new Vector2(0.95f, 1.6f), new Vector2(0.95f, 0.8f)),
                'c' => (new Vector2(0.95f, 0.8f), new Vector2(0.95f, 0f)),
                'd' => (new Vector2(0.05f, 0f), new Vector2(0.95f, 0f)),
                'e' => (new Vector2(0.05f, 0f), new Vector2(0.05f, 0.8f)),
                'f' => (new Vector2(0.05f, 1.6f), new Vector2(0.05f, 0.8f)),
                'g' => (new Vector2(0.05f, 0.8f), new Vector2(0.95f, 0.8f)),
                _ => (Vector2.zero, Vector2.zero),
            };
            list.Add((a, b));
        }
        return list.ToArray();
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
