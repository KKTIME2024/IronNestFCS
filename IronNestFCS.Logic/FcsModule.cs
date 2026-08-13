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
    private bool autoMode;          // 全自动模式:true=雷达接管;false=手动(雷达完全休眠)
    private int lastCbtCount = -1;  // 检测 CBT timer 数量变化

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
        AdjustAllValves(0f);
        radar.Scan();

        fcs.ClearPendingTasks();   // 只清自动任务, 手动任务保留

        // 逐空闲炮管派发（TryDispatch 按 Left→Right 取队首，入队顺序即分配顺序）。
        // 派发即登记(Registry.Commit 在 TryDispatch), 同帧双管去重由注册表覆盖,
        // 在飞窗口约束也由注册表统一承担(炮上/排队/在飞/手动提交的目标全部跳过)。
        int nextTargetId = 1;
        foreach (var barrel in new[] { LeftRight.Left, LeftRight.Right })
        {
            if (barrel == LeftRight.Left ? fcs.LeftTask != null : fcs.RightTask != null) continue;

            var t = PickTarget();
            if (t == null) continue;
            var ti = t.Value;  // TargetInfo 是 struct,Nullable 解包

            // 软+静态目标先试集群(一发清多, 省弹); 无集群回单点。选择流程不动。
            var task = fcs.TryBuildClusterTask(ti, nextTargetId);
            if (task == null) task = CreateAutoTask(ti, nextTargetId);
            nextTargetId++;
            fcs.EnqueueTask(task);
        }
    }

    /// <summary>单点自动任务(非集群路径): 现有选择/弹种/移动快照逻辑。BlastRadiusKm 供击发爆区注册。</summary>
    private static ArtilleryTask CreateAutoTask(TacticalDecider.TargetInfo ti, int targetId)
    {
        var task = new ArtilleryTask
        {
            targetId = targetId,
            entityId = ti.EntityId,
            angel = ti.Angle,
            distance = ti.Distance,
            position = ti.WorldPos,
            bulletType = TacticalDecider.ChooseShellType(ti),
            useMaxCharge = TacticalDecider.ShouldUseMaxCharge(ti),
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
    /// 全部可选目标都被承包 → 返回 null，炮管空等；窗口到期后由周期重扫恢复派发。
    /// </summary>
    private TacticalDecider.TargetInfo? PickTarget()
    {
        foreach (var t in radar.AliveHostiles)
        {
            if (fcs.Registry.IsHandled(t.EntityId)) continue;
            if (fcs.Registry.IsHandledNear(t.WorldPos, 0f)) continue;
            return t;
        }
        return null;
    }

    public void Update()
    {
        fcs.Update();
        overlay?.Update();

        // 被动扫描:全自动模式下每 5s 周期重扫+派发(在飞窗口到期后恢复派发,或双管全空时补派);
        // 手动模式雷达完全休眠
        if (radar != null && fcs.IsBound && autoMode && Time.time - lastScanTime > 5f)
        {
            lastScanTime = Time.time;
            OnGunIdle();
        }

        // 轮询 CBT 计时器（每 5s），只在发现 timer 时打日志——仅全自动模式
        if (fcs.IsBound && autoMode && Time.time - lastCbtPollTime > 5f)
        {
            lastCbtPollTime = Time.time;
            var (hasTimer, info) = fcs.PollRunningTimers();
            if (hasTimer && lastCbtCount == 0)
            {
                lastCbtCount = 1;
                MelonLogger.Msg($"[FCS] CBT active: {info}");
            }
            else if (!hasTimer && lastCbtCount != 0)
            {
                lastCbtCount = 0;
                MelonLogger.Msg("[FCS] CBT ended");
            }
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
        fcs.Dispose();
        overlay?.Shutdown();
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
