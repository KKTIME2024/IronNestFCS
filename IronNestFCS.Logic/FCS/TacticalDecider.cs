using System.Collections.Generic;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 战术决策：纯数据层，不碰任何 IL2CPP 对象。
/// 输入：目标信息 + 当前炮塔角 → 输出：推荐的弹种、装药量和排序。
/// 流水线完全不动——只在创建 ArtilleryTask 时读这里的返回值。
/// </summary>
public static class TacticalDecider
{
    /// <summary>可直接用于排序的轻量目标快照</summary>
    public struct TargetInfo
    {
        public string Name;
        public string EntityId;     // MapEntity key (e.g. "target1")
        public float Angle;
        public float Distance;
        public int Priority;     // 6:FDC, 5:火炮, 4:弹药库/高价值, 3:装甲/工事, 2:普通, 1:其他
        public bool IsArmored;
        public bool IsUnderground;
        public bool IsMoving;
        /// <summary>速度已建立(≥2 个采样点): false=目标刚出现, 快照无提前量, 首发会打出生点</summary>
        public bool VelocityKnown;
        /// <summary>目标速度向量(桌面单位/秒 ×3.8164 = km/s),移动目标预测用</summary>
        public Vector3 Velocity;
        public Vector3 WorldPos;
        public int ChildIndex;   // deprecated, use EntityId
        /// <summary>目标免疫的弹种 ID 集合（如 {"HE"}），用于自动弹种选择</summary>
        public HashSet<string> ImmuneShells;
    }

    /// <summary>
    /// 排序：优先级降序 → 同优先级按总机械时间升序。
    /// 实测：高低机 0→60 = 32s (1.875/s)，方向机 84→0° = 25s (3.36°/s)。
    /// cost ≈ 仰角上下时间 + 炮塔转动时间 = distance×2.56 + angleDelta×0.30（秒）
    /// </summary>
    public static void SortTargets(List<TargetInfo> targets, float currentAngle)
    {
        targets.Sort((a, b) =>
        {
            int pc = b.Priority.CompareTo(a.Priority);
            if (pc != 0) return pc;

            float costA = a.Distance * 2.56f + AngleDelta(currentAngle, a.Angle) * 0.30f;
            float costB = b.Distance * 2.56f + AngleDelta(currentAngle, b.Angle) * 0.30f;
            return costA.CompareTo(costB);
        });
    }

    /// <summary>
    /// 满装药唯一收益是缩短飞行时间，仅在 CBT 竞速时有价值。
    /// 当前无法检测 CBT 状态，统一用最低装药。
    /// 恢复时改为：t.Priority >= 5（仅火炮/FDC 满装抢时间）。
    /// </summary>
    public static bool ShouldUseMaxCharge(TargetInfo t)
    {
        return false;
    }

    /// <summary>
    /// 自动弹种选择，成本优先：软目标单点 LE（轻弹省点，单个目标用不上 HE 的杀伤包络），
    /// 装甲/地下 → AP（穿透）。免疫时交叉降级，HCHE 仅作最后兜底。
    /// 注意：集群路径不经过这里（TryBuildClusterTask 直接选 HE/HCHE，覆盖 ≥2 才成簇）。
    /// </summary>
    public static BulletType ChooseShellType(TargetInfo t)
    {
        var immune = t.ImmuneShells ?? new HashSet<string>();

        // 首选：装甲/地下需要穿透，软目标单点 LE 足够
        BulletType primary = (t.IsArmored || t.IsUnderground) ? BulletType.AP : BulletType.LE;

        if (!immune.Contains(primary.ToString()))
            return primary;

        // 首选被免疫 → 换另一个低成本弹种
        var fallback = primary == BulletType.AP ? BulletType.LE : BulletType.AP;
        if (!immune.Contains(fallback.ToString()))
            return fallback;

        // 两个都被免疫 → 上 HCHE（贵但可用）
        return BulletType.HCHE;
    }

    /// <summary>两个角度之间的最小差值 [0, 180]</summary>
    private static float AngleDelta(float a, float b)
    {
        float d = Mathf.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }
}
