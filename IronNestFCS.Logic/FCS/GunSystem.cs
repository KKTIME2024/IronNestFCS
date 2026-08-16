using System.Collections;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;


public enum BulletType {
    AP = 1,
    APHE = 2,
    ATMC = 3,
    CLMN = 4,
    CYAN = 5,
    DRIL = 6,
    EQKE = 7,
    FLCH = 8,
    HCHE = 9,
    HE = 10,
    INCN = 11,
    LE = 12,
    PCLM = 13, // 采购卡/玩家习惯叫 PCLM,游戏弹舱 ShellId 叫 PLCM(读时转换)
    PHGN = 14,
    PRPG = 15,
    SMK = 16,
    STAR = 17,
    TEAR = 18,
    THRM = 19,
    WP = 20,
}

public class GunSystem {
    private const float MinimumPostShotRecoverySeconds = 13f;

    private string _surfix = "";

    private CylinderShellSelector? shellSelector;
    
    private List<string?> bullets = new();
    private LookAtTarget? nextBulletButton;
    private LookAtTarget? loadBulletButton;
    private List<LookAtTarget> powderButtons = new();
    private LookAtTarget? loadPowderButton;
    private GunController? gunController;
    private ArtilleryReloadController? reloadController;
    private LinearSliderInteractable? elevationLever;
    private OdometerDisplay? remainingCharges;

    private TextMeshPro shellId;

    public bool TryBind(string surfix) {
        this._surfix = surfix;
        
        var gunSystem = GameObject.Find("Gun System " + surfix).transform;
        var reloadingConsole = gunSystem.Find("--Reloading Console");
        if (reloadingConsole == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find --Reloading Console");
            return false;
        }

        remainingCharges = reloadingConsole.GetComponentInChildren<OdometerDisplay>();
        
        nextBulletButton = 
            reloadingConsole.Find("Universal Button Move Cylinder")
                .GetComponent<LookAtTarget>();    
        shellSelector = gunSystem.GetComponentInChildren<CylinderShellSelector>();
        
        shellId = GameObject.Find("Shell ID " + surfix)
            .GetComponent<TextMeshPro>();
        var loadShell = reloadingConsole.FindChild("Universal Button Load shell Rammer");
        if (loadShell == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find Universal Button Load shell Rammer");
            return false;
        }
        loadBulletButton = loadShell.GetComponent<LookAtTarget>();

        var powderController = reloadingConsole.Find("PowderChargeController");
        for (var i = 0; i < powderController.childCount; ++i) {
            var child = powderController.GetChild(i);
            if (!child.name.StartsWith("Button Dispencer")) continue;
            var button = child.GetComponent<LookAtTarget>();
            if (button == null) {
                MelonLogger.Error($"[FCS] GunSystem {surfix}: Found {child.name} but lack of LookAtTarget Component");
                return false;
            }
            powderButtons.Add(button);
        }

        loadPowderButton = reloadingConsole.FindChild("Universal Button Charge Rammer (1)").GetComponent<LookAtTarget>();
        gunController = GameObject.Find("Gun"+surfix).GetComponent<GunController>();
        reloadController = gunController?.artilleryReloadController;
        elevationLever = GameObject.Find(".Elevation Lever Baseplate")?.transform.FindChild(".Elevation Lever " + surfix)
            .GetComponent<LinearSliderInteractable>();
        return true;
    }
    
    /// <summary>膛内弹来源: 自动任务装填(Auto) vs 用户手动装填(Manual/未知)。
    /// 用于保护用户资产——用户手动屯的弹(如 6 包核弹)切自动后不应被自动模式复用打掉。
    /// 自动任务装填成功标 Auto; 退壳/击发后膛空标 Unknown; 膛内有弹但非 Auto → Manual。</summary>
    public enum ChamberOrigin { Unknown, Auto, Manual }

    private ChamberOrigin _chamberOrigin = ChamberOrigin.Unknown;

    public ChamberOrigin Origin => _chamberOrigin;

    /// <summary>自动任务装填成功时调用(弹种校验通过后)。</summary>
    public void MarkAutoLoaded() => _chamberOrigin = ChamberOrigin.Auto;

    /// <summary>退壳/击发后膛空时调用。</summary>
    public void MarkChamberEmpty() => _chamberOrigin = ChamberOrigin.Unknown;

    /// <summary>膛内有弹但非自动装填 → 标记用户手动(自动派发跳过该炮管)。</summary>
    public void MarkManual() => _chamberOrigin = ChamberOrigin.Manual;

    public bool CanFire() {
        return gunController != null && gunController.CanFire;
    }

    /// <summary>膛内状态(每发任务开始前评估, 决定装填路径)。</summary>
    public enum ChamberState
    {
        Empty,          // 膛空 + 未装药 → 正常装填
        ShellLoaded,    // 弹在膛(未装药或药未推完) → 跳过推弹直接装药
        ReadyToFire,    // 弹+药已装好 CanFire=true(未发射) → 直接复用, 跳过整个装填
        Dirty,          // 弹在膛但弹种未知/状态异常 → 退壳重装
    }

    /// <summary>
    /// 评估膛内状态: 只读不写。每发任务装填段前调用——
    /// 手动半装填/任务中断/转移阵地后炮管可能停在任意中间态, 这里决定接管策略。
    /// ReadyToFire 判定: CanFire=true 且无 pendingReload(击发后等待退壳) → 整发可复用。
    /// </summary>
    public ChamberState AssessChamber() {
        if (gunController == null) return ChamberState.Empty;
        var inChamber = BulletInChamber();
        if (inChamber == null)
            return ChamberState.Empty;   // 膛空(不管装药残留——装药残留不阻碍装填, 且库存读数不可靠)
        if (CanFire() && !IsPendingReload())
            return ChamberState.ReadyToFire;   // 完全就绪, 直接打
        return ChamberState.ShellLoaded;       // 弹在膛但未就绪(半装填)
    }

    /// <summary>膛内弹种是否与期望一致(供 ShellLoaded 分支决定复用或退壳)。</summary>
    public bool ChamberMatches(BulletType type) {
        return BulletInChamber() == type.ToString();
    }

    /// <summary>
    /// 膛内实际装药数(已选并推入的装药档)。来自 PowderChargeController.currentSelectedCharges
    /// (private 字段, 反射读)。注意与库存区分: PowderChargeInventory._currentCharges 是装药库存
    /// (开局 40 包), 不是膛内装药——2026-08-15 实测教训。读取失败返回 -1(无法确认)。
    /// PowderChargeController 引用缓存(热重载后失效重找), 避免每次 GameObject.Find 全场景搜索。
    /// </summary>
    private PowderChargeController? _cachedPowderCtrl;

    public int ChamberCharges() {
        try {
            if (_cachedPowderCtrl == null)
            {
                var reloadingConsole = GameObject.Find("Gun System " + _surfix)?.transform.Find("--Reloading Console");
                if (reloadingConsole == null) return -1;
                _cachedPowderCtrl = reloadingConsole.GetComponentInChildren<PowderChargeController>();
            }
            if (_cachedPowderCtrl == null) return -1;
            var f = _cachedPowderCtrl.GetType().GetField("currentSelectedCharges",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return f?.GetValue(_cachedPowderCtrl) is int n ? n : -1;
        }
        catch { return -1; }
    }

    /// <summary>炮管是否为"脏状态"(残留弹)。注意: 只用可靠信号判定——
    /// 膛内有弹(BulletInChamber 非空)。装药计数(RemainingCharges)读的是装药库存
    /// (正常 40 包), 不是炮管内装药量, 不能用作脏判断(2026-08-15 实测教训)。</summary>
    public bool IsDirty() {
        if (gunController == null) return false;
        return !IsChamberEmpty();
    }

    /// <summary>
    /// 清洗炮管到干净初始态, 任何炮管状态都适用(2026-08-15 加固):
    ///   1. 解锁炮闩(合栓状态 Eject 无效——退壳是动画事件驱动, 依赖状态机在开栓位)
    ///   2. 状态机强制复位到初始(开栓)
    ///   3. 退壳(膛内残留弹) —— 动画驱动, 轮询确认弹出膛, 失败回退状态机重试
    ///   4. 复位仰角(装填/退壳机构要求炮管在装填仰角, 抬升后需先放下)
    ///   5. 释放重装保持
    /// 幂等: 干净状态调用无副作用。调用方用 yield return 等待退壳完成。
    /// 方法/属性在 Il2Cpp stub 中缺失 → 反射调用(与 TacticalRadar 同风格)。
    /// </summary>
    public IEnumerator CleanState() {
        if (gunController == null) yield break;
        var gcType = gunController.GetType();
        // 1. 复位仰角到装填位(低位): 装填/退壳机构要求炮管在低位——合栓/击发后炮管可能
        //    抬在高仰角, 先摇回 0 再退膛动画(2026-08-15 用户指出)。ResetElevation 非阻塞,
        //    后续步骤与仰角下降并行。
        InvokeReflected(gcType, gunController, "ResetElevation");
        // 解锁炮闩(合栓状态)
        try {
            var setLock = gcType.GetMethod("SetExternalReloadLoweringLocked",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            setLock?.Invoke(gunController, new object[] { false });
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] GunSystem {_surfix}: 解锁炮闩失败: {ex.GetType().Name}: {ex.Message}");
        }
        var rcType = reloadController?.GetType();
        if (rcType != null && reloadController != null)
        {
            // 2. 状态机回退到"可装填"位: 合栓/半装填状态 Eject 无效, 需先回退。
            //    CanLoadBullet() 是游戏自带的可装填判定, 回退直到它为 true(最多 6 步)。
            //    相比 ForceResetStateToInitial(可能只清状态不驱动动画), RegressState 走真实回退。
            var canLoad = rcType.GetMethod("CanLoadBullet",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            for (var step = 0; step < 6; step++)
            {
                bool ok = false;
                try { ok = canLoad != null && (bool)canLoad.Invoke(reloadController, null)!; } catch { }
                if (ok) break;
                InvokeReflected(rcType, reloadController, "RegressState");
                InvokeReflected(rcType, reloadController, "ResetAnimators");
                // 等回退动画推进(机构真实状态) + 仰角降到低位
                for (var w = 0; w < 10 && (reloadController.working || gunController.elevationChangeVelocity != 0); w++)
                    yield return new WaitForSeconds(0.5f);
            }
            // 兜底: 回退到位后再强制复位(幂等)
            InvokeReflected(rcType, reloadController, "ForceResetStateToInitial");
            InvokeReflected(rcType, reloadController, "ResetAnimators");
        }

        // 3. 退壳(膛内残留弹)。EjectChamberedShell 在 ArtilleryReloadController 上,
        //    不在 GunController——调错对象会静默跳过(2026-08-15 实测: 弹一直退不掉)。
        //    退壳是动画驱动, 调用后需等待弹真的出膛; 合栓状态需先回退状态机再退。
        if (!IsChamberEmpty()) {
            MelonLogger.Msg($"[FCS] GunSystem {_surfix}: 退壳残留弹 {BulletInChamber()}");
            if (reloadController == null)
            {
                MelonLogger.Warning($"[FCS] GunSystem {_surfix}: reloadController 未绑定, 无法退壳");
            }
            else
            {
                // 退壳: 调 Eject; 弹未出膛则回退状态机重试
                for (var attempt = 0; attempt < 3 && !IsChamberEmpty(); attempt++)
                {
                    InvokeReflected(rcType!, reloadController, "EjectChamberedShell");
                    // 等动画完成, 弹出膛
                    var ejectTimeout = 0;
                    while (!IsChamberEmpty() && ejectTimeout < 10) {
                        yield return new WaitForSeconds(0.5f);
                        ejectTimeout++;
                    }
                    if (IsChamberEmpty())
                    {
                        MelonLogger.Msg($"[FCS] GunSystem {_surfix}: 退壳成功");
                        MarkChamberEmpty();
                        break;
                    }
                    MelonLogger.Warning($"[FCS] GunSystem {_surfix}: 退壳第{attempt + 1}次后弹仍在膛, 回退状态机重试");
                    InvokeReflected(rcType!, reloadController, "RegressState");
                    InvokeReflected(rcType!, reloadController, "ResetAnimators");
                    for (var w = 0; w < 10 && reloadController.working; w++)
                        yield return new WaitForSeconds(0.5f);
                }
            }
        }
        // 4. 释放"重装完成后保持"状态(游戏可能在等待手动恢复)
        InvokeReflected(gcType, gunController, "ReleaseReloadHoldAndRestore");
        // 5. 等机构真正空闲(动画/回退/仰角下降完成), 防止紧接着的 LoadBullet 过早点击
        for (var w = 0; w < 20 && (reloadController == null || reloadController.working
            || gunController.elevationChangeVelocity != 0); w++)
            yield return new WaitForSeconds(0.5f);
    }

    /// <summary>反射调用实例方法(无参)。方法不存在时静默跳过(不同游戏版本 stub 差异)。</summary>
    private static void InvokeReflected(System.Type type, object instance, string methodName) {
        try {
            var m = type.GetMethod(methodName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            m?.Invoke(instance, null);
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] 反射调用 {type.Name}.{methodName} 失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public IEnumerator SetElevation(float elevation) {
        if (elevationLever == null || gunController == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Elevation lever or gun controller unbound");
            yield break;
        }
        elevationLever.SetSliderValue(elevation);
        yield return new WaitForSeconds(0.1f);
        while (!Mathf.Approximately(gunController.CurrentElevation, elevation)) {
            elevationLever.SetSliderValue(elevation);
            yield return new WaitForSeconds(1f);
        }
    }
    
    public string? BulletInChamber() {
        return gunController?.ChamberedShellBlueprint?.shellDefinition?.ShellId?.Replace("PLCM", "PCLM");
    }
    
    public bool IsChamberEmpty() {
        return BulletInChamber() == null;
    }

    private void RefreshBullets() {
        bullets.Clear();
        if (shellSelector == null) return;
        foreach (var shell in shellSelector.bullets) {
            bullets.Add(shell?.GetComponent<ShellBlueprint>()?.shellDefinition?.ShellId?.Replace("PLCM", "PCLM"));
        }
        MelonLogger.Msg($"[FCS] GunSystem {_surfix}: Cylinder bullets: {string.Join(", ", bullets)}");
    }

    public void NextBullet() {
        if (nextBulletButton == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: NextBulletButton unbound");
        }
        MelonLogger.Msg("[GunSystem] NextBullet");
        nextBulletButton!.OnClickDown();
    }

    /// <summary>
    /// 正式版的装填状态索引是数据驱动的，空闲时可能处于不同的 CurrentStateIndex，
    /// 因此不把某个固定索引当作"可装填"。只依据控制器实际的 working、炮闩锁定和炮管运动状态判断。
    /// （上游 svr2kos2 3951d055 移植）
    /// 超时兜底(2026-08-15): 转移阵地/任务中断后状态机可能停在中间(炮闩合上永不解锁) →
    /// 永久等待会卡死任务; 20s 超时强制放行(让上层 BulletInChamber 校验/看门狗兜底)。
    /// </summary>
    private IEnumerator WaitForReloadReady() {
        var deadline = Time.time + 20f;
        while (gunController != null && Time.time < deadline) {
            var mechanismReady = reloadController == null || !reloadController.working;
            var breechReady = !gunController.ExternalReloadLoweringLocked;
            var motionReady = gunController.elevationChangeVelocity == 0;
            if (mechanismReady && breechReady && motionReady)
                yield break;

            yield return new WaitForSeconds(0.1f);
        }
    }
    
    /// <summary>
    /// 装填指定弹种：先把弹仓转到目标弹，再按装填。转弹仓每步之间要等 1 秒
    /// （游戏有转动动画/物理）。返回 IEnumerator，调用方用 yield return 等待它跑完。
    /// 必须走协程而非 async：continuation 要留在主线程才能安全访问 IL2CPP 对象。
    /// </summary>
    public IEnumerator LoadBullet(BulletType type) {
        // 上一发的退壳/炮闩/复位机构可能仍在工作。先等待真实机构状态空闲，
        // 再开始下一轮弹仓和推弹操作，避免连续射击时过早点击后续控件。
        yield return WaitForReloadReady();

        RefreshBullets();
        var index = bullets.IndexOf(type.ToString());
        if (index == -1) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: " +
                              $"No {type} available in cylinder, current bullets: {string.Join(", ", bullets)}");
            yield break;
        }
        
        for (var i = 0; i < bullets.Count; ++i) {
            if (bullets[0] == type.ToString()) {
                break;
            };
            NextBullet();
            yield return new WaitForSeconds(1.5f);
            RefreshBullets();
        }
        if (bullets[0] != type.ToString()) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Can't find {type} after rotation, " +
                              $"current: {string.Join(", ", bullets)}");
            yield break;
        }

        yield return WaitForReloadReady();
        // 推弹前确保状态机在"可装填"位: 合栓/半装填状态退壳后可能停在中间, 推弹杆不激活。
        // CanLoadBullet() 是游戏自带判定; 10s 等不到 → RegressState 回退再等(最多 3 轮)。
        if (reloadController != null)
        {
            var canLoad = reloadController.GetType().GetMethod("CanLoadBullet",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            for (var tryStep = 0; tryStep < 3; tryStep++)
            {
                var ok = false;
                try { ok = canLoad != null && (bool)canLoad.Invoke(reloadController, null)!; } catch { }
                if (ok) break;
                if (tryStep > 0)
                {
                    InvokeReflected(reloadController.GetType(), reloadController, "RegressState");
                    InvokeReflected(reloadController.GetType(), reloadController, "ResetAnimators");
                }
                var w = 0;
                while (!ok && w < 20)
                {
                    yield return new WaitForSeconds(0.5f);
                    try { ok = canLoad != null && (bool)canLoad.Invoke(reloadController, null)!; } catch { }
                    w++;
                }
                if (ok) break;
            }
        }
        yield return FcsSceneInteractor.WaitAndClick(loadBulletButton!);
    }

    private IEnumerator SelectPowder(int count) {
        for (var i = 0; i < count; i++) {
            yield return FcsSceneInteractor.WaitAndClick(powderButtons[i]!);
        }
    }

    public IEnumerator LoadPowder(int count) {
        yield return SelectPowder(count);
        yield return FcsSceneInteractor.WaitAndClick(loadPowderButton!);
    }

    public bool HaveBulletInCylinder(BulletType type) {
        RefreshBullets();
        return bullets.Contains(type.ToString());
    }
    
    public bool HaveEmptyShellInCylinder() {
        RefreshBullets();
        return bullets.Contains(null);
    }

    public IEnumerator WaitBackToIdle() {
        // 保留原来的 13 秒最小恢复窗口，但同时要求正式版装填机构真正结束工作。
        // 这样下一任务不会只因为炮管停止运动就过早进入装填。
        // （上游 svr2kos2 3951d055 移植）
        // 超时兜底(2026-08-15): 转移阵地/任务中断可能让机构永久卡在非就绪态 → 30s 强制放行。
        var minimumRecoveryUntil = Time.realtimeSinceStartup + MinimumPostShotRecoverySeconds;
        var deadline = Time.realtimeSinceStartup + 30f;
        while (gunController != null && Time.realtimeSinceStartup < deadline) {
            var minimumDelayDone = Time.realtimeSinceStartup >= minimumRecoveryUntil;
            var mechanismReady = reloadController == null || !reloadController.working;
            var breechReady = !gunController.ExternalReloadLoweringLocked;
            var motionReady = gunController.elevationChangeVelocity == 0;
            if (minimumDelayDone && mechanismReady && breechReady && motionReady)
                yield break;

            yield return new WaitForSeconds(0.1f);
        }
    }

    public IEnumerator WaitFire() {
        while (gunController != null && !gunController.pendingReload) {
            yield return new WaitForSeconds(0.1f);
        }
    }

    /// <summary>非阻塞: 设仰角杆目标值, 不等待。连续跟踪每帧调用。</summary>
    public void SetElevationTarget(float elevation) {
        if (elevationLever == null) return;
        elevationLever.SetSliderValue(elevation);
    }

    /// <summary>当前仰角(度)与目标差。就绪判定用。</summary>
    public float ElevationError(float target) {
        if (gunController == null) return 0f;
        return Mathf.Abs(gunController.CurrentElevation - target);
    }

    /// <summary>是否已处于击发/待击发状态（玩家拉扳机后为 true）。手动等待判定用。</summary>
    public bool IsPendingReload() => gunController != null && gunController.pendingReload;

    public int RemainingCharges() {
        return (int)remainingCharges.CurrentNumber;
    }

}