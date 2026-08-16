using System.Collections.Generic;
using Il2Cpp;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public class FcsModule : IFcsModule
{
    private readonly FSC fcs = new();
    private FcsWindow? window;
    private TacticalRadar? radar;
    private MapOverlay? overlay;

    private float lastScanTime;
    private float nextSweepTime;
    private float lastCbtPollTime;
    private float _lastMoveBlockLog;    // 紧急移动延迟日志节流
    private bool _lastFocused = true;   // 失焦/恢复诊断(2026-08-16 用户: Alt+Tab 后游戏未响应)
    private float _lastTimeScaleWarn;   // 聚焦但 timeScale=0 警告节流
    private bool autoMode;          // 全自动模式:true=雷达接管;false=手动(雷达完全休眠)
    private bool _emergencyMoveTriggered;  // 本危急周期已触发过紧急移动(防重复采购)
    private float _emergencyMoveAttemptTime; // 触发时刻(30s 无暂停 → 采购失败, 允许重试)
    private float _lastEmergencyMoveTime;   // 上次紧急移动成功时刻(冷却期内不重复花 65 点)
    private bool _emergencyMoveInProgress;  // 转移阵地进行中(暂停自动派发, 防炮管被重置后新任务卡死)
    private const float EmergencyMoveCooldown = 45f;  // 紧急移动冷却(秒): 防 FDC 暂停误触发连买

    /// <summary>
    /// 紧急移动: 采购 MoveZone 卡(65 点, 暂停倒计时到下一发落地 + 转移阵地)。
    /// 转移期间暂停自动派发(阵地移动会重置炮管状态, 弹被卸下 → 新任务瞄准段卡死);
    /// 正在执行的任务置 Canceled(瞄准/装填循环新增检查点, 干净退出释放锁/槽位);
    /// 等倒计时暂停生效(IsRunning=false)后复位, 并触发补派。
    /// </summary>
    private void StartEmergencyMove()
    {
        _emergencyMoveInProgress = true;
        fcs.ClearPendingTasks();   // 队列里的自动任务先清掉(阵地转移后目标角度全变)
        // 双管正在执行的任务置 Canceled: 转移阵地重置炮管状态(弹被卸下), 继续瞄准必卡死
        if (fcs.LeftTask != null) fcs.LeftTask.Canceled = true;
        if (fcs.RightTask != null) fcs.RightTask.Canceled = true;
        fcs.StartEmergencyMove(OnEmergencyMoveDone);
    }

    /// <summary>玩家手动转移阵地(手动买 MoveZone 卡)后的清理(2026-08-16 用户):
    /// 游戏照常重置炮管状态, 但我们没采购卡——跳过采购, 复用转移后清理
    /// (停派/取消任务/清洗炮管/等基准静止), 防任务卡死(玩家反馈 bug)。</summary>
    private void StartManualMoveCleanup()
    {
        _emergencyMoveInProgress = true;
        fcs.ClearPendingTasks();
        if (fcs.LeftTask != null) fcs.LeftTask.Canceled = true;
        if (fcs.RightTask != null) fcs.RightTask.Canceled = true;
        fcs.StartTransferCleanup(OnEmergencyMoveDone);
    }

    /// <summary>紧急移动完成(倒计时暂停生效或超时) → 恢复自动派发。</summary>
    private void OnEmergencyMoveDone()
    {
        _emergencyMoveInProgress = false;
        _lastEmergencyMoveTime = Time.time;
        nextSweepTime = 0;   // 立即补派一轮
        OnGunIdle();
    }

    public bool Initialize()
    {
        window = new FcsWindow(fcs);
        radar = new TacticalRadar(fcs);
        overlay = new MapOverlay(fcs);
        fcs.EntityLocator = radar;   // 手动任务目标解析
        fcs.OnGunIdle += OnGunIdle;
        bool bound = fcs.TryBind();
        if (bound) fcs.SyncTurretPiece();   // 开局强制归位 Piece 一次(忘了放也准)
        return bound;
    }

    /// <summary>任一炮管退膛 → 扫描 → 清空队列 → 给每门空闲炮管各派一个目标。扫荡中每 5s 也跑一次，用于在飞窗口到期后恢复派发。</summary>
    private void OnGunIdle()
    {
        if (!autoMode) return;
        if (Time.time < nextSweepTime) return;
        nextSweepTime = Time.time + 0.5f;

        if (radar == null || !fcs.IsBound) return;
        // 紧急移动转移阵地期间不派发: 阵地移动会重置炮管状态(弹被卸下), 新任务会在瞄准段卡死。
        // 转移完成后(倒计时暂停生效)由 EmergencyMoveRoutine 复位标志并触发一次补派。
        if (_emergencyMoveInProgress) return;
        AdjustAllValves(0f);
        radar.Scan();

        fcs.ClearPendingTasks();   // 只清自动任务, 手动任务保留

        // 逐空闲炮管派发（TryDispatch 按 Left→Right 取队首，入队顺序即分配顺序）。
        // 派发即登记(Registry.Commit 在 TryDispatch), 同帧双管去重由注册表覆盖,
        // 在飞窗口约束也由注册表统一承担(炮上/排队/在飞/手动提交的目标全部跳过)。
        // 2026-08-16: 移除手动弹保护(用户)——膛内有任何弹(含手动装的)都照常派发,
        // 膛内状态接管(ReadyToFire 复用/退壳重装)统一处理, 不再跳过本管。
        int nextTargetId = 1;
        foreach (var barrel in new[] { LeftRight.Left, LeftRight.Right })
        {
            if (barrel == LeftRight.Left ? fcs.LeftTask != null : fcs.RightTask != null) continue;

            var t = PickTarget();
            if (t == null) continue;
            var ti = t.Value;  // TargetInfo 是 struct,Nullable 解包

            // 软+静态目标先试集群(一发清多, 省弹); 无集群回单点。选择流程不动。
            var fundMargin = fcs.Cbt.IsCbtMode ? fcs.Cbt.FundMargin : float.MaxValue;
            var task = fcs.TryBuildClusterTask(ti, nextTargetId);
            if (task == null) task = CreateAutoTask(ti, nextTargetId, fcs.Cbt.ShouldUseMaxCharge(), fundMargin);
            nextTargetId++;
            fcs.EnqueueTask(task);
        }
    }

    /// <summary>单点自动任务(非集群路径): 现有选择/弹种/移动快照逻辑。BlastRadiusKm 供击发爆区注册。
    /// CBT 双轨装药(§3.2): 吃紧/危急 → useMaxCharge=true(满装 6 包抢节奏), 其余最低装药。
    /// CBT 基金纪律(§3.6): fundMargin 传入弹种选择(AP 仅 3★ 装甲且扣款后 ≥ 基金线)。</summary>
    private static ArtilleryTask CreateAutoTask(TacticalDecider.TargetInfo ti, int targetId, bool useMaxCharge, float fundMargin)
    {
        var task = new ArtilleryTask
        {
            targetId = targetId,
            entityId = ti.EntityId,
            angel = ti.Angle,
            distance = ti.Distance,
            position = ti.WorldPos,
            bulletType = TacticalDecider.ChooseShellType(ti, fundMargin),
            useMaxCharge = useMaxCharge,
            IsFdc = ti.IsFdc,
            Source = TaskSource.Auto
        };
        // 移动目标冻结快照: 提前量解算全程从这三个字段外推(匀速假设, 不查雷达)。
        // 速度未建立(刚出现)时标记 VelocityUnknown: 装填期从雷达采纳后再外推。
        task.IsMoving = ti.IsMoving;
        task.AimP0 = ti.WorldPos;
        task.AimVel = ti.Velocity;
        task.AimStartTime = Time.time;
        task.VelocityUnknown = !ti.VelocityKnown;
        task.BlastRadiusKm = ShellData.BlastRadiusKm(task.bulletType);
        return task;
    }

    /// <summary>
    /// 挑目标: 注册表(炮上/排队/在飞/手动提交)中的目标跳过, 返回最高优先级可选目标。
    /// CBT 扣留(§3.4): 非吃紧期(开局/宽裕)不派 FDC——开局打=浪费暂停储备, 宽裕期打=浪费。
    /// CBT 基金纪律(§3.6): 装甲/地下目标需 AP(10 点), 扣款后仍 ≥ 基金线才可派;
    /// 买不起 AP 时跳过(HE 打不穿, 发了白费)。
    /// 全部可选目标都被承包 → 返回 null，炮管空等；窗口到期后由周期重扫恢复派发。
    /// </summary>
    private TacticalDecider.TargetInfo? PickTarget()
    {
        var phase = fcs.Cbt.Phase;

        // FDC 活动期停派(2026-08-16 用户): FDC 装填/在飞/未确认期间, 非暂停期不派其他目标。
        // Paused 窗口内放行: 窗口内火炮(+30s 顶倒计时)是主角——FDC 暂停只冻结不解除危急,
        // 窗口内不打火炮则 FDC 循环白烧储备(收益 0, 用户 16:06 指出)。Paused 排序火炮 8 >
        // FDC 7 保证火炮先派先装填先击发先落地 → FDC 弹最后落地续暂停, 不重复 16:05 白费。
        if (phase != CbtMonitor.CbtPhase.Paused && fcs.HasActiveFdcTask) return null;

        // 流程 1(2026-08-16 用户设定): 倒计时 ≤ Critical = 紧急状态, 派发停止——不派任何新目标。
        // FDC = 移动的 fallback: 移动可用(积分≥90 且非 FastNoMove 且冷却过) → 完全不派,
        // 双管空闲, 无在飞弹时触发 MoveZone(暂停+转移); 移动不可用(没积分/FastNoMove/冷却中)
        // → 只派 FDC 立刻装填(AP)——开火前落地顺序保护保证 FDC 弹是最后一发落地
        // (旧弹先落地 ≥0.1s, 暂停不被立即解除)。
        if (phase == CbtMonitor.CbtPhase.Critical)
        {
            bool moveAvailable = fcs.Cbt.Mode != CbtMonitor.TestMode.FastNoMove
                && fcs.Cbt.RequisitionPoints >= CbtMonitor.FundLine
                && Time.time - _lastEmergencyMoveTime > EmergencyMoveCooldown;
            if (moveAvailable) return null;
            foreach (var t in radar.AliveHostiles)
            {
                if (!t.IsFdc) continue;
                if (fcs.Registry.IsHandled(t.EntityId)) continue;
                if (fcs.Registry.IsHandledNear(t.WorldPos, 0f)) continue;
                if (!fcs.Cbt.FdcDispatchable()) continue;
                if (fcs.HasActiveFdcTask) continue;   // 一次只派一个(含在飞)
                return t;
            }
            return null;
        }

        // 流程 2 窗口内(Paused)与常规: 恢复正常排序派发。
        // Paused 排序(FDC 8 > 火炮 7 > 3★ 6)即窗口内组合拳: 第一管 FDC 续暂停 +
        // 第二管火炮(+30s 白赚), 双管 ReadyToFire 后由击发协调齐射。
        foreach (var t in radar.AliveHostiles)
        {
            if (fcs.Registry.IsHandled(t.EntityId)) continue;
            if (fcs.Registry.IsHandledNear(t.WorldPos, 0f)) continue;
            // FDC 扣留: 只在危急/暂停窗口放行(流程 1/2); 吃紧/宽裕期跳过
            if (t.IsFdc && !fcs.Cbt.FdcDispatchable()) continue;
            // FDC 一次只派一个: 双管同时打 2 个 FDC → 两发几乎同时落地, 暂停窗口重叠浪费
            if (t.IsFdc && fcs.HasActiveFdcTask) continue;
            // 基金纪律: CBT 下装甲/地下目标必须 AP 且扣款后 ≥ 基金线; 买不起 → 跳过(HE 打不穿)。
            // FDC 豁免(用户): 唯一炮弹控制的暂停来源, 买不起 AP 也派(10 点破例)。
            if (fcs.Cbt.IsCbtMode && !t.IsFdc && (t.IsArmored || t.IsUnderground) && fcs.Cbt.FundMargin < 10f) continue;
            return t;
        }
        return null;
    }

    public void Update()
    {
        fcs.Update();
        overlay?.Update();   // 地图 overlay: 打击中任务可视化(内部 1Hz 节流)

        // 失焦/恢复诊断(2026-08-16 用户: Alt+Tab 后游戏未响应——怀疑失焦暂停 timeScale=0
        // 未恢复的假死)。日志能打 = 假死(Update 还在跑, 可强制恢复 timeScale);
        // 日志停 = 真卡死(主线程死循环, 与 mod 无关, 游戏自身 bug)。
        bool focused = Application.isFocused;
        if (focused != _lastFocused)
        {
            _lastFocused = focused;
            MelonLogger.Msg($"[FCS] 焦点变化: isFocused={focused} timeScale={Time.timeScale} (Alt+Tab 未响应排查)");
        }
        else if (focused && Time.timeScale == 0f && Time.time - _lastTimeScaleWarn > 5f)
        {
            // 聚焦但 timeScale=0: 失焦暂停未恢复(游戏假死) —— 先只日志, 确认后加恢复保护
            _lastTimeScaleWarn = Time.time;
            MelonLogger.Warning($"[FCS] 聚焦但 timeScale=0 (失焦暂停未恢复? 游戏可能未响应)");
        }

        // 被动扫描:全自动模式下每 5s 周期重扫+派发(在飞窗口到期后恢复派发,或双管全空时补派);
        // 手动模式雷达完全休眠
        if (radar != null && fcs.IsBound && autoMode && Time.time - lastScanTime > 5f)
        {
            lastScanTime = Time.time;
            OnGunIdle();
        }

        // 紧急移动自动触发(§3.5): 危急(阈值内) 且积分 ≥ 基金线(90) → 采购 MoveZone 卡(65 点,
        // 暂停倒计时到下一发落地 + 转移阵地)。仅全自动模式。
        if (fcs.IsBound && autoMode && Time.time - lastCbtPollTime > 5f)
        {
            lastCbtPollTime = Time.time;
            // 统一紧急链路(2026-08-16 用户设计): CRITICAL 检测 → 紧急状态。
            // 积分分流: 有积分(≥基金线90) → 紧急移动(MoveZone 65 点, 暂停+转移阵地);
            // 没积分 → 派发层走 FDC(免费暂停, 见 PickTarget 紧急收敛)。
            if (fcs.Cbt.ShouldEmergencyMove() && !_emergencyMoveTriggered
                // FDC 暂停窗口内不触发: FDC 击杀暂停已生效, 此时买 MoveZone 65 点
                // 重复花(暂停部分浪费)+ 转移阵地重置炮管 → 组合拳被毁。
                // 等暂停解除(下一发落地)后再评估, 紧急移动的暂停从干净状态开始。
                && !fcs.Cbt.IsPausedWindow
                && Time.time - _lastEmergencyMoveTime > EmergencyMoveCooldown)
            {
                // 暂停收益保护(2026-08-15): 任何在飞炮弹落地都会立即解除暂停——若此刻有弹在飞,
                // 触发紧急移动的暂停窗口会被压缩到"触发→那发弹落地", 65 点白花。
                // 正确做法: 等所有在飞弹落地(落地瞬间=装填间隙)再触发, 窗口从干净状态开始,
                // 完整覆盖下一轮装填+飞行。判定与 InFlight 移除同源(射表), 落地后下周期放行。
                float soonestLanding = fcs.SoonestImpactIn();
                if (soonestLanding > 0f)
                {
                    if (Time.time - _lastMoveBlockLog > 20f)
                    {
                        _lastMoveBlockLog = Time.time;
                        MelonLogger.Msg($"[CBT] 危急但有在飞弹({soonestLanding:F0}s后落地), 等落地后再紧急移动(避免暂停被立即解除)");
                    }
                }
                else
                {
                    _emergencyMoveTriggered = true;
                    _emergencyMoveAttemptTime = Time.time;
                    MelonLogger.Msg($"[CBT] 危急: 触发紧急移动 (remain={fcs.Cbt.TimeRemaining:F1}s pts={fcs.Cbt.RequisitionPoints})");
                    StartEmergencyMove();
                }
            }
            // 倒计时已暂停(紧急移动卡生效) → 允许下次再次触发
            if (_emergencyMoveTriggered && !fcs.Cbt.IsRunning)
                _emergencyMoveTriggered = false;
            // 采购失败兜底: 触发 30s 后倒计时仍 ≥阈值(未暂停) → 允许重试
            else if (_emergencyMoveTriggered && Time.time - _emergencyMoveAttemptTime > 30f)
                _emergencyMoveTriggered = false;
        }

        // 玩家手动紧急移动检测(2026-08-16 用户): 手动买 MoveZone 卡/转移阵地 → 游戏移动
        // TurretLocation(基准突变) → 复用转移后清理(停派/取消任务/清洗炮管), 防任务卡死
        // (玩家反馈 bug: 手动移动后流程卡住)。自动紧急移动期间跳过(自身在清理)。
        if (fcs.IsBound && autoMode && !_emergencyMoveInProgress && fcs.DetectManualTransferMove())
        {
            MelonLogger.Msg("[FCS] 检测到玩家手动转移阵地, 执行转移后清理(取消任务+清洗炮管)");
            StartManualMoveCleanup();
        }

        // 手柄:Select 键切换全自动/手动模式(等价 Numpad 0)
        var gp = Gamepad.current;
        if (gp != null && gp.selectButton.wasPressedThisFrame && fcs.IsBound)
        {
            ToggleAutoMode();
            return;
        }

        var kb = Keyboard.current;
        if (kb == null || !fcs.IsBound)
            return;

        // Numpad 0: 切换全自动/手动模式
        if (kb.numpad0Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit0Key.wasPressedThisFrame))
        {
            ToggleAutoMode();
            return;
        }

        // Numpad 1-4: manual fire targets
        if (kb.numpad1Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit1Key.wasPressedThisFrame))
            fcs.FireTarget(1);
        else if (kb.numpad2Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit2Key.wasPressedThisFrame))
            fcs.FireTarget(2);
        else if (kb.numpad3Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit3Key.wasPressedThisFrame))
            fcs.FireTarget(3);
        else if (kb.numpad4Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit4Key.wasPressedThisFrame))
            fcs.FireTarget(4);

        // Numpad 8: CBT 测试档位切换(2026-08-16 用户: 注释掉, 不再用测试档)
        // if (kb.numpad8Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit8Key.wasPressedThisFrame))
        //     fcs.Cbt.CycleTestMode();

        // Numpad 9: CBT 扫描探针(2026-08-16 用户: 注释掉, 临时调试已完成)
        // if (kb.numpad9Key.wasPressedThisFrame)
        //     fcs.CbtScanProbe();
    }

    /// <summary>切换全自动/手动模式。切手动时清空自动队列(正在打的不打断,打完自然停)。</summary>
    private void ToggleAutoMode()
    {
        autoMode = !autoMode;
        nextSweepTime = 0;  // 重设时间窗，防止立即触发的首轮被防重入窗口跳过
        if (!autoMode)
        {
            fcs.ClearPendingTasks();   // 只清自动入队的队列, 手动任务保留
            CancelOrForceFire(fcs.LeftTask);
            CancelOrForceFire(fcs.RightTask);
            MelonLogger.Msg("[FCS] 手动模式:雷达休眠,手动标点 T1-T4 接管");
        }
        else
        {
            OnGunIdle();
            MelonLogger.Msg("[FCS] 全自动模式:雷达接管");
        }
        if (window != null) window.AutoSweepEnabled = autoMode;
    }

    /// <summary>
    /// 切手动: 自动任务按进度分流——未开始装填(未碰炮膛)的干净取消, 不浪费弹;
    /// 已进入装填(弹已上膛)的强制击发, 原子化防卡膛。
    /// 手动任务保持待击发, 由玩家自己拉扳机(WaitFire 等待玩家手动击发)。
    /// </summary>
    private static void CancelOrForceFire(ArtilleryTask? task)
    {
        if (task == null || task.Source != TaskSource.Auto) return;
        if (task.progress < Progress.LoadingBullet) task.Canceled = true;
        else task.forceFire = true;
    }

    public void OnGui()
    {
        window?.OnGui();
        radar?.OnGui();
    }

    public void Shutdown()
    {
        fcs.OnGunIdle -= OnGunIdle;
        overlay?.Shutdown();   // 销毁全部 overlay 渲染对象
        fcs.Dispose();
        window = null;
        radar = null;
        overlay = null;
    }

    /// <summary>找到所有蒸汽泄漏点，收紧最近阀门到指定值（0=拧紧, 999=全开）</summary>
    private static void AdjustAllValves(float value)
    {
        var dials = new List<(DialInteractable di, Vector3 pos)>();
        foreach (var go in Object.FindObjectsOfType<GameObject>(true))
        {
            var di = go.GetComponent<DialInteractable>();
            if (di != null) dials.Add((di, go.transform.position));
        }
        int done = 0;
        foreach (var go in Object.FindObjectsOfType<GameObject>(true))
        {
            if (go == null || !go.name.ToLower().Contains("steam leak")) continue;
            DialInteractable? nearest = null;
            float minDist = float.MaxValue;
            foreach (var (di, pos) in dials)
            {
                var d = (pos - go.transform.position).magnitude;
                if (d < minDist) { minDist = d; nearest = di; }
            }
            if (nearest == null) continue;
            nearest.SetDialValue(value);
            done++;
        }
        // 阀门日志太吵已移除; 拧紧功能保留
    }
}
