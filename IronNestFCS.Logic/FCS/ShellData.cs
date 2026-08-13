namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 弹种数据（维基静态表）: 毁伤半径(km) + 成本(申请点)。正式版价格。
/// 常用对敌弹仅 AP/HE/HCHE 三个。纯数据, 无 IL2CPP 引用。
/// </summary>
public static class ShellData
{
    /// <summary>地图比例: km / 世界单位（与 CalcDistance 一致）</summary>
    public const float KmPerWorldUnit = 3.8164f;

    /// <summary>km → 世界单位（毁伤半径等比较距离前转换）</summary>
    public static float KmToWorld(float km) => km / KmPerWorldUnit;

    /// <summary>
    /// 致死半径(km)——实测致死边界(2026-08-13, 多装药验证):
    ///   HE: 0.17km 死、0.21km 活(1 包和 6 包都验证) → 致死 ≈0.19。转盘 0.23 对 HE 偏大
    ///   (0.21 ≤ 0.23 覆盖判定通过但炸不死 → 漏杀目标被反复重打; 收紧后边缘目标走单点直击)。
    ///   HCHE: 0.43km 杀、0.47km 活 → 致死 ≈0.45, 取 0.43(已验证 0.43 集群全杀)。
    ///   覆盖判定与注册表爆区屏蔽统一用它——边缘目标不入选集群也不被屏蔽 → 立即单点直击打死。
    ///   ShellDefinition.ImpactRadius(HE 0.25/HCHE 0.55)是满伤包络, 仅作友军禁区基数。
    /// </summary>
    public static float BlastRadiusKm(BulletType t) => t switch
    {
        BulletType.AP => 0.10f,
        BulletType.DRIL => 0.05f,   // 超小毁伤半径(包络 0.07×0.7)——单点精确弹, 不误伤/不屏蔽邻居
        BulletType.HE => 0.19f,
        BulletType.HCHE => 0.43f,
        BulletType.LE => 0.10f,
        _ => 0f
    };

    /// <summary>杀伤包络(ShellDefinition.ImpactRadius 实测): 包络内可能受伤(含衰减区)——友军禁区基数,
    /// 集群覆盖不用它(致死半径更小)。</summary>
    public static float DamageRadiusKm(BulletType t) => t switch
    {
        BulletType.AP => 0.15f,
        BulletType.DRIL => 0.07f,   // ImpactRadius 实测
        BulletType.HE => 0.25f,
        BulletType.HCHE => 0.55f,
        BulletType.LE => 0.15f,   // 与 AP 相同(实测一致)
        _ => 0f
    };

    /// <summary>友军禁区半径 = 杀伤包络 + 20% 余量(不赌包络边缘)</summary>
    public static float FriendlySafeRadiusKm(BulletType t) => DamageRadiusKm(t) * 1.2f;

    /// <summary>成本(申请点)。正式版: AP/HE=10, HCHE=18, DRIL=3(用户确认——软目标单点首选, 省 70%)。</summary>
    public static int Cost(BulletType t) => t switch
    {
        BulletType.HCHE => 18,
        BulletType.DRIL => 3,
        _ => 10
    };
}
