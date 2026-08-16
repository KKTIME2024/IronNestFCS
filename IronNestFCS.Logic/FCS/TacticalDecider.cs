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
        public int Priority;     // 6:FDC, 5:火炮, 4:弹药库/高价值/3★, 3:装甲/工事, 2:普通, 1:其他
        public int Stars;        // 星标等级 0-3(3★ = 印钞机)
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
        /// <summary>是否 FDC(icon 含 fire direction, 模式指纹 + 暂停组合拳目标)</summary>
        public bool IsFdc => Priority >= 6;
    }

    /// <summary>
    /// 排序：优先级降序 → 同优先级按总机械时间升序。
    /// 实测：高低机 0→60 = 32s (1.875/s)，方向机 84→0° = 25s (3.36°/s)。
    /// cost ≈ 仰角上下时间 + 炮塔转动时间 = distance×2.56 + angleDelta×0.30（秒）
    /// CBT 双轨(§3.3)：宽裕期 3★ 印钞最高、吃紧期火炮(+30s)最高、FDC 仅吃紧放行。
    /// </summary>
    public static void SortTargets(List<TargetInfo> targets, float currentAngle, CbtMonitor.CbtPhase cbtPhase = CbtMonitor.CbtPhase.None)
    {
        bool cbt = cbtPhase is CbtMonitor.CbtPhase.Wide or CbtMonitor.CbtPhase.Urgent or CbtMonitor.CbtPhase.Critical or CbtMonitor.CbtPhase.Opening or CbtMonitor.CbtPhase.Paused;
        // Opening(倒计时未启动): 装填+飞行全不烧时间, 先打远目标白嫖更多(2026-08-15 用户指出
        // 先打近浪费未开始时间)——同优先级下距离大优先。暂停窗口(Paused)同此理(2026-08-16):
        // 窗口内弹不烧时间, 长 ToF 弹先落地=解除点 → 窗口 = 长 ToF(最大化);
        // 实测短 ToF(近)先落地 → 窗口 = 短 ToF, 白白浪费窗口长度。
        bool farFirst = cbtPhase is CbtMonitor.CbtPhase.Opening or CbtMonitor.CbtPhase.Paused;
        targets.Sort((a, b) =>
        {
            int pa = cbt ? CbtPriority(a, cbtPhase) : a.Priority;
            int pb = cbt ? CbtPriority(b, cbtPhase) : b.Priority;
            int pc = pb.CompareTo(pa);
            if (pc != 0) return pc;

            if (farFirst) return b.Distance.CompareTo(a.Distance);   // 远优先

            float costA = a.Distance * 2.56f + AngleDelta(currentAngle, a.Angle) * 0.30f;
            float costB = b.Distance * 2.56f + AngleDelta(currentAngle, b.Angle) * 0.30f;
            return costA.CompareTo(costB);
        });
    }

    /// <summary>CBT 双轨优先级重映射(§3.3/§3.4): 只换数字, 排序管道不动。
    /// 宽裕/开局: 3★(7,印钞) > 火炮(6) > 高价值/弹药库(5) > 1-2★(4) > 装甲/工事(3) > 普通(2), FDC(0)=扣留。
    /// 吃紧/危急: 火炮(8,+30s 硬通货) > FDC(7,暂停组合拳) > 3★(5) > 高价值(4) > 1-2★(3) > 装甲/工事(2) > 普通(1)。
    /// 暂停窗口(2026-08-16 用户修正): 火炮(8,+30s 顶倒计时是主角——FDC 暂停只冻结不解除危急,
    /// 窗口内不打火炮则 FDC 循环白烧储备收益 0) > FDC(7,续暂停保底) > 3★(6) >
    /// 高价值(5) > 1-2★(4) > 装甲/工事(3) > 普通(2)。窗口内无满装(最低装药=ToF 长=窗口长)。
    /// 数值只用于排序, FDC 派发与否最终由 FcsModule.PickTarget 的 FdcDispatchable 把关。</summary>
    private static int CbtPriority(TargetInfo t, CbtMonitor.CbtPhase phase)
    {
        bool urgent = phase is CbtMonitor.CbtPhase.Urgent or CbtMonitor.CbtPhase.Critical;
        bool paused = phase == CbtMonitor.CbtPhase.Paused;
        if (t.IsFdc) return paused ? 7 : urgent ? 7 : 0;   // 暂停窗口 FDC 7(火炮 8 优先); 宽裕期垫底(扣留)
        if (t.Priority == 5) return paused ? 8 : urgent ? 8 : 6;   // 火炮: 窗口内 +30s 顶倒计时最优先
        if (t.Priority == 4)
            return paused ? 6 : t.Stars >= 3 ? (urgent ? 5 : 7) : (urgent ? 4 : 5); // 3★ 印钞 vs 高价值/弹药库
        if (t.Priority == 3) return paused ? 4 : t.Stars >= 1 ? (urgent ? 3 : 4) : (urgent ? 2 : 3); // 1-2★ / 装甲工事
        return paused ? 2 : urgent ? 1 : 2;                  // 普通
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
    /// 自动弹种选择：装甲/地下 → AP（穿透）；软目标单点 → DRIL（超小毁伤半径精确弹——
    /// 直击必死且不误伤/不屏蔽邻居，比 LE 更精确）。免疫时沿链降级：软 DRIL→LE→HE→HCHE、
    /// 甲 AP→HE→HCHE。注意：集群路径不经过这里（TryBuildClusterTask 直接选 HE/HCHE）。
    /// CBT 基金纪律(§3.6, 仅 fundMargin != MaxValue 即 CBT 模式生效): AP 成本 10 点,
    /// 仅 3★ 装甲或地下目标(弹药库/地堡 HE 打不穿, 必须 AP)且扣款后仍 ≥ 基金线才用;
    /// 其余装甲降级 HE。非 CBT 模式任何装甲都打 AP。
    /// </summary>
    public static BulletType ChooseShellType(TargetInfo t, float fundMargin = float.MaxValue)
    {
        var immune = t.ImmuneShells ?? new HashSet<string>();
        bool cbtDiscipline = fundMargin != float.MaxValue;

        if (t.IsArmored || t.IsUnderground)
        {
            // AP 成本 10 点(§3.6): CBT 下仅 3★ 装甲或地下目标, 且扣款后仍 ≥ 基金线才用;
            // 地下目标(弹药库/地堡/FDC 指挥所)HE 打不穿 → 必须 AP, 不受星级限制
            // (否则 2★ 仓库/地下 FDC 发 HE 白费)。FDC 特别化(2026-08-16 用户): 唯一炮弹
            // 控制的 CBT 暂停来源——强制 AP 且不受基金纪律限制(买不起也买, 10 点破例;
            // 基金纪律是普通地下/装甲目标的规则)。
            bool apAllowed = t.IsFdc || (fundMargin >= 10f && (!cbtDiscipline || t.Stars >= 3 || t.IsUnderground));
            if (apAllowed && !immune.Contains(BulletType.AP.ToString())) return BulletType.AP;
            if (!immune.Contains(BulletType.HE.ToString())) return BulletType.HE;
            return BulletType.HCHE;
        }

        if (!immune.Contains(BulletType.DRIL.ToString())) return BulletType.DRIL;
        if (!immune.Contains(BulletType.LE.ToString())) return BulletType.LE;
        if (!immune.Contains(BulletType.HE.ToString())) return BulletType.HE;
        return BulletType.HCHE;
    }

    /// <summary>两个角度之间的最小差值 [0, 180]</summary>
    private static float AngleDelta(float a, float b)
    {
        float d = Mathf.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }
}
