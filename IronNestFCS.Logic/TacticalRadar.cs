using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

/// <summary>
/// 战术雷达：从 FireMission.Entities Dictionary (MapEntity) 扫描敌对单位，
/// 调用 TacticalDecider 决策后自动生成射击任务。
/// 相比旧的 FireMissionRoot.children 方案，Entities 包含全部 40 个目标槽位，
/// 包括动态 spawn 的第二波 FDC/火炮。
/// </summary>
public class TacticalRadar
{
    private const int RoleEnemy = 1;
    private const int RoleAlly = 2;
    private const int RoleTarget = 32;
    private const int RoleArtillery = 128;
    private const int RoleFortification = 65536;
    private const int RoleTank = 262144;
    private const int RoleReference = 33554432;
    private const int RoleAmmo = 8;
    private const int RoleHighValue = 16;

    private readonly FSC fcs;
    private readonly HashSet<string> sweptIds = new();

    // 移动侦测:每个实体的位置采样历史(≤4 点),首尾差分估速 → 4-5s 基线比单帧差分平滑。
    // 冻结快照的提前量精度依赖它(速度噪声随装填+飞行时长放大)。正式版移动目标匀速直线为主。
    private readonly Dictionary<string, Queue<(Vector3 pos, float time)>> moveHistory = new();
    private const int MoveHistorySize = 4;

    // 缓存的 FireMission 引用（按 F9 重载后失效，Scan 内自动刷新）
    private FireMission? _cachedFm;
    private PropertyInfo? _entitiesProp;
    private RectTransform? _coordinateRoot;   // 网格坐标→世界坐标的桥梁

    public bool AutoPlaceMarkers { get; set; } = true;
    public List<TacticalDecider.TargetInfo> AliveHostiles { get; private set; } = new();
    /// <summary>存活友军世界坐标(集群落点友军禁区检查用), Scan 时刷新。</summary>
    public List<Vector3> AllyPositions { get; private set; } = new();
    public int SweptCount => sweptIds.Count;

    public TacticalRadar(FSC fcs) => this.fcs = fcs;

    public bool IsSwept(string entityId) => sweptIds.Contains(entityId);
    public void MarkSwept(string entityId) => sweptIds.Add(entityId);

    public void OnGui()
    {
        var alive = AliveHostiles;
        if (alive == null || alive.Count == 0) return;
        float right = Screen.width - 10f;
        float y = 120f;
        GUI.contentColor = new Color(0.72f, 0.65f, 0.55f);
        GUI.Label(new Rect(right - 140f, y, 140f, 20f), $"Hostiles: {alive.Count}  Swept: {SweptCount}");
    }

    /// <summary>
    /// 从 FireMission.Entities Dictionary 扫描全部 MapEntity，
    /// 按 IsAlive && 敌对 过滤，按优先级+转角排序。
    /// </summary>
    public void Scan()
    {
        AliveHostiles.Clear();
        AllyPositions.Clear();
        // 每轮扫描刷新地图面引用(紧急移动转移可能重建, 5s 一次 Find 开销可接受)
        _radarMapSurface = GameObject.Find("Draggable Surface")?.transform;
        var targets = new List<TacticalDecider.TargetInfo>();
        var aliveIds = new List<string>();

        ForEachEntity((key, me) =>
        {
            if (IsAlive(me)) aliveIds.Add(key);
            // 存活友军坐标 → 集群落点友军禁区检查(不能炸到友军)
            if (IsAlly(me) && IsAlive(me) && TryGetWorldPos(me, out var allyPos))
                AllyPositions.Add(allyPos);
            var t = BuildTargetInfo(key, me);
            if (t != null) targets.Add(t.Value);
        });

        // 击杀确认: 死亡目标的登记立即解除(打中的目标下轮扫描恢复可拣)
        fcs.Registry.Reconcile(aliveIds);

        // CBT 模式级指纹: 本轮是否扫到存活 FDC(icon 含 fire direction) → CbtMonitor 模式识别
        // 2026-08-16 稳定线停用: 反炮兵为实验模式(已知 bug 多且用户少用), 从发布版剔除——
        // 不注入指纹 → IsCbtMode 永假 → 紧急移动/双轨装药/FDC 扣留/基金纪律/冻结窗口全部休眠,
        // 行为与非反炮兵关一致(该路径已验证稳定)。CBT 修复在 dev 分支进行, 稳定后恢复本行。
        fcs.Cbt.HasFdc = false;

        TacticalDecider.SortTargets(targets, fcs.Turret.LastSetAngle, fcs.Cbt.SortPhase);
        AliveHostiles = targets;

        var summary = string.Join(" | ", targets.Select(t =>
            $"({t.Priority}){t.EntityId} {(t.IsUnderground ? "UG" : "")}{(t.IsArmored ? "ARM" : "")}"));
        // allies 计数: 验证友军禁区检测是否工作(0 = 检测失败, 集群会误炸友军)
        MelonLogger.Msg($"[Radar] {targets.Count} hostiles: {summary} allies:{AllyPositions.Count}");

        if (AutoPlaceMarkers)
        {
            // 雷达只占用 T1/T2 两个标记(对应双管各派一发); T3/T4 永远留给玩家——
            // 玩家在自动模式下也能拖 T3/T4 手动入队(注册表登记后雷达 IsHandled 跳过)。
            for (int i = 1; i <= 2; i++)
            {
                if (i <= targets.Count)
                    fcs.MapTable.SetMarkerWorldPos(i, targets[i - 1].WorldPos);
                else
                    fcs.MapTable.ResetMarker(i);
            }
        }
    }

    /// <summary>反射枚举 FireMission.Entities, 对每个 MapEntity 回调 (key, MapEntity)。</summary>
    private void ForEachEntity(Action<string, object> action)
    {
        var (fm, entities) = GetEntitiesDict();
        if (entities == null) return;
        var getEnum = entities.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
        if (getEnum == null) return;
        var enumerator = getEnum.Invoke(entities, null);
        if (enumerator == null) return;
        var enumType = enumerator.GetType();
        var moveNext = enumType.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
        var currentProp = enumType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
        if (moveNext == null || currentProp == null) return;
        while ((bool)moveNext.Invoke(enumerator, null)!)
        {
            var kvp = currentProp.GetValue(enumerator);
            if (kvp == null) continue;
            var kvpType = kvp.GetType();
            var keyProp = kvpType.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
            var valueProp = kvpType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (keyProp == null || valueProp == null) continue;
            var key = keyProp.GetValue(kvp)?.ToString() ?? "";
            var me = valueProp.GetValue(kvp);
            if (me == null) continue;
            action(key, me);
        }
    }

    private static bool IsAlive(object me)
    {
        var aliveProp = me.GetType().GetProperty("IsAlive", BindingFlags.Public | BindingFlags.Instance);
        return aliveProp?.GetValue(me) is bool b && b;
    }

    private static int GetRole(object me)
    {
        var roleProp = me.GetType().GetProperty("Role", BindingFlags.Public | BindingFlags.Instance);
        var roleVal = roleProp?.GetValue(me);
        if (roleVal is int ri) return ri;
        if (roleVal is Enum e) return Convert.ToInt32(e);
        return -1;
    }

    /// <summary>敌对 = 带 Enemy/Target 位且非 Reference(Ally-only 已天然排除)</summary>
    private static bool IsHostileRole(int role) =>
        role >= 0 && (role & (RoleEnemy | RoleTarget)) != 0 && (role & RoleReference) == 0;

    /// <summary>友军 = 带 Ally 位且非 Reference</summary>
    private static bool IsAlly(object me)
    {
        int role = GetRole(me);
        return role >= 0 && (role & RoleAlly) != 0 && (role & RoleReference) == 0;
    }

    /// <summary>世界坐标: 优先 Location.transform.position, 兜底 coordinateRoot.TransformPoint</summary>
    private bool TryGetWorldPos(object me, out Vector3 worldPos)
    {
        var locProp = me.GetType().GetProperty("Location", BindingFlags.Public | BindingFlags.Instance);
        if (locProp?.GetValue(me) is EntityLocation location)
        {
            worldPos = location.transform.position;
            return true;
        }
        if (_coordinateRoot != null)
        {
            var posProp = me.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
            if (posProp?.GetValue(me) is Vector3 mp)
            {
                worldPos = _coordinateRoot.TransformPoint(mp);
                return true;
            }
        }
        worldPos = Vector3.zero;
        return false;
    }

    /// <summary>单个实体的 TargetInfo 构建: IsAlive && 敌对 过滤, 无世界坐标返回 null。</summary>
    private TacticalDecider.TargetInfo? BuildTargetInfo(string key, object me)
    {
        if (!IsAlive(me)) return null;
        int role = GetRole(me);
        if (!IsHostileRole(role)) return null;

        var meType = me.GetType();

        // Icon
        var iconProp = meType.GetProperty("Icon", BindingFlags.Public | BindingFlags.Instance);
        var icon = iconProp?.GetValue(me) is string s ? s : "";

        // Name
        var nameProp = meType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
        var name = nameProp?.GetValue(me) is string sn ? sn : key;

        // Stars
        var starsProp = meType.GetProperty("Stars", BindingFlags.Public | BindingFlags.Instance);
        int stars = starsProp?.GetValue(me) is int st ? st : 0;

        // Armour
        var armourProp = meType.GetProperty("Armour", BindingFlags.Public | BindingFlags.Instance);
        int armour = armourProp?.GetValue(me) is int ar ? ar : 0;

        // Position (MapEntity.Position 是地图坐标系)
        var posProp = meType.GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
        var mapPos = posProp?.GetValue(me) is Vector3 mp ? mp : Vector3.zero;

        // ImmuneShells
        var immune = new HashSet<string>();
        var immuneProp = meType.GetProperty("ImmuneShells", BindingFlags.Public | BindingFlags.Instance);
        if (immuneProp != null)
        {
            var iv = immuneProp.GetValue(me);
            if (iv is IEnumerable ie)
                foreach (var item in ie)
                    if (item != null) immune.Add(item.ToString() ?? "");
        }

        // 地下检测：名字/Icon 关键词
        bool isUnderground = IsUnderground(name, icon);

        // 装甲判断：MapEntity.Armour > 0 或 Role/Icon 匹配
        bool isArmored = armour > 0
                         || (role & RoleFortification) != 0
                         || (role & RoleTank) != 0
                         || (role & RoleAmmo) != 0
                         || (role & RoleHighValue) != 0
                         || icon.IndexOf("ammunition", StringComparison.OrdinalIgnoreCase) >= 0
                         || icon.IndexOf("cache", StringComparison.OrdinalIgnoreCase) >= 0
                         || icon.IndexOf("supply", StringComparison.OrdinalIgnoreCase) >= 0
                         || icon.IndexOf("fire direction", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!TryGetWorldPos(me, out var worldPos)) return null;

        // 移动侦测:位置采样历史首尾差分估速(4-5s 基线, 比单帧差分平滑)
        bool isMoving = false;
        bool velocityKnown = false;
        Vector3 velocity = Vector3.zero;
        if (moveHistory.TryGetValue(key, out var hist))
        {
            hist.Enqueue((worldPos, Time.time));
            while (hist.Count > MoveHistorySize) hist.Dequeue();
            velocityKnown = hist.Count >= 2;   // 首个采样点无速度, 等下一轮扫描
            var first = hist.Peek();
            var last = hist.Last();
            float dt = last.time - first.time;
            if (dt > 1f)
            {
                Vector3 disp = last.pos - first.pos;
                velocity = disp / dt;
                if (velocity.magnitude * 3.8164f > 0.001f)  // 约 1 m/s 阈值
                {
                    isMoving = true;
                    MelonLogger.Msg($"[Radar] MOVING: '{name}' ({key}) " +
                                    $"v={velocity.magnitude * 3.8164f:F3}km/s 位移={disp.magnitude * 3.8164f:F3}km/{dt:F1}s " +
                                    $"from {first.pos} to {last.pos}");
                }
            }
        }
        else
        {
            var q = new Queue<(Vector3 pos, float time)>();
            q.Enqueue((worldPos, Time.time));
            moveHistory[key] = q;
        }

        return new TacticalDecider.TargetInfo
        {
            Name = name,
            EntityId = key,
            Angle = CalcAngle(worldPos),
            Distance = CalcDistance(worldPos),
            Priority = CalcPriority(role, icon, stars),
            Stars = stars,
            IsArmored = isArmored,
            IsUnderground = isUnderground,
            WorldPos = worldPos,
            IsMoving = isMoving,
            VelocityKnown = velocityKnown,
            Velocity = velocity,
            ChildIndex = 0, // 不再使用，保留兼容
            ImmuneShells = immune
        };
    }

    /// <summary>
    /// 实时目标解析(手动开火用): 标记世界坐标附近最近的存活敌目标。
    /// 直接查 FireMission.Entities, 不依赖 AliveHostiles 缓存(手动模式雷达休眠)。
    /// </summary>
    public string? FindNearestHostileId(Vector3 worldPos, float maxDistanceKm)
    {
        string? nearest = null;
        float best = maxDistanceKm;
        ForEachEntity((key, me) =>
        {
            if (!IsAlive(me)) return;
            if (!IsHostileRole(GetRole(me))) return;
            if (!TryGetWorldPos(me, out var wp)) return;
            var d = Vector3.Distance(worldPos, wp) * 3.8164f;
            if (d < best) { best = d; nearest = key; }
        });
        return nearest;
    }

    /// <summary>
    /// 取目标最新一次扫描的运动状态(位置+速度)。速度未建立返回 false。
    /// 供冷启动任务装填期采纳快照用(一次性, 不连续查)。
    /// </summary>
    public bool TryGetMotion(string entityId, out Vector3 pos, out Vector3 vel)
    {
        foreach (var t in AliveHostiles)
        {
            if (t.EntityId != entityId || !t.VelocityKnown) continue;
            pos = t.WorldPos;
            vel = t.Velocity;
            return true;
        }
        pos = Vector3.zero;
        vel = Vector3.zero;
        return false;
    }

    /// <summary>获取 FireMission.Entities Dictionary（带缓存，F9 重载自动刷新）</summary>
    private (FireMission? fm, object? dict) GetEntitiesDict()
    {
        try
        {
            if (_cachedFm != null && _entitiesProp != null)
            {
                try { var test = _entitiesProp.GetValue(_cachedFm); if (test != null) return (_cachedFm, test); }
                catch { }
            }

            _cachedFm = null;
            _entitiesProp = null;
            _coordinateRoot = null;

            var fmGo = GameObject.Find("Fire Mission Root");
            if (fmGo == null) return (null, null);
            _cachedFm = fmGo.GetComponent<FireMission>();
            if (_cachedFm == null) return (null, null);

            _entitiesProp = _cachedFm.GetType().GetProperty("Entities", BindingFlags.Public | BindingFlags.Instance);
            if (_entitiesProp == null) return (null, null);

            // 缓存 coordinateRoot：网格坐标 → 世界坐标的桥梁
            var crProp = _cachedFm.GetType().GetProperty("coordinateRoot", BindingFlags.Public | BindingFlags.Instance);
            _coordinateRoot = crProp?.GetValue(_cachedFm) as RectTransform;

            var dict = _entitiesProp.GetValue(_cachedFm);
            return (_cachedFm, dict);
        }
        catch
        {
            return (null, null);
        }
    }

    // ─── 地下检测 ───

    private static bool IsUnderground(string name, string icon)
    {
        var low = name.ToLower();
        var lowIcon = icon.ToLower();
        foreach (var key in new[] {
            "bunker", "underground", "shelter", "bombproof", "pillbox", "dugout",
            "depot", "storage", "magazine", "cache", "armory", "warehouse",
            "subterranean", "tunnel", "cave", "vault", "casemate",
            // FDC 指挥所(2026-08-16 用户实测): 至少本关 FDC 全在地下, HE 打不穿必须 AP——
            // FDC 本体不免疫 HE, 但地下掩体按"地下必须 AP"规则处理(与 2★ 仓库同规则)。
            "fire direction"
        })
            if (low.Contains(key)) return true;
        foreach (var key in new[] { "underground", "bunker", "bombproof", "subterranean" })
            if (lowIcon.Contains(key)) return true;
        return false;
    }

    // ─── 优先级计算 ───

    private static int CalcPriority(int role, string icon, int stars)
    {
        bool isFdc = icon.ToLower().Contains("fire direction");
        if (isFdc) return 6;

        if ((role & RoleArtillery) != 0) return 5;

        if ((role & RoleAmmo) != 0 || (role & RoleHighValue) != 0) return 4;
        if (stars >= 3) return 4;

        if (stars >= 1) return 3;
        if ((role & RoleFortification) != 0 || (role & RoleTank) != 0) return 3;

        if ((role & RoleEnemy) != 0) return 2;

        return 1;
    }

    // ─── 坐标计算（世界坐标 → 地图坐标系）───
    // 基准用真实炮塔 TurretLocation(转移阵地时游戏直接移动它, 实时位置即新基准),
    // 不用 Player Turret Piece(可拖动标记, 转移后 5s 内未归位 → 角度/距离全错 → 打空)。
    // mapSurface 缓存(Scan 时刷新一次), 不做每次 Find——2026-08-15 性能修复。

    private Transform? _radarMapSurface;

    private void EnsureRadarMapSurface()
    {
        if (_radarMapSurface == null)
            _radarMapSurface = GameObject.Find("Draggable Surface")?.transform;
    }

    private Transform? AimTurretBase => fcs.MapTable.TurretLocation ?? fcs.MapTable.Turret;

    private float CalcAngle(Vector3 worldPos)
    {
        EnsureRadarMapSurface();
        var turret = AimTurretBase;
        if (_radarMapSurface == null || turret == null) return 0f;
        var turretLocal = _radarMapSurface.InverseTransformPoint(turret.position);
        var target = _radarMapSurface.InverseTransformPoint(worldPos) - turretLocal;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        return angle;
    }

    private float CalcDistance(Vector3 worldPos)
    {
        EnsureRadarMapSurface();
        var turret = AimTurretBase;
        if (_radarMapSurface == null || turret == null) return 0f;
        var turretLocal = _radarMapSurface.InverseTransformPoint(turret.position);
        var target = _radarMapSurface.InverseTransformPoint(worldPos) - turretLocal;
        return target.magnitude * 3.8164f;
    }
}
