using HarmonyInstance = HarmonyLib.Harmony;
using System.Collections;
using System.Linq;
using System.Reflection;
using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public enum LeftRight {
    Left,
    Right,
}

/// <summary>
/// 纯火控领域逻辑：查找游戏对象、读取游戏数据、操控游戏内交互（dial 等）。
/// 不含任何 UI / IMGUI / 生命周期框架代码——那些在 <see cref="FcsModule"/> 和 <see cref="FcsWindow"/> 里。
///
/// 重载安全规则：
///  - 不要在这里注册新的 IL2CPP 类型（同一类型进程内只能注册一次）。
///  - 每次实例用独立的 Harmony 实例；Shutdown 时 UnpatchSelf。
///  - 所有对 IL2CPP 对象的引用在 Shutdown 时清空，便于旧 ALC 回收。
/// </summary>
public class FSC
{
    private const string HarmonyId = "com.svr2kos2.ironnestfcs.logic";

    // 驻留装药补给:装药低于阈值时每 5s 补一包(deskLock 保护),与任务内购买互补。
    private const float PowderCheckInterval = 5f;
    private const int PowderReplenishThreshold = 6;

    private HarmonyInstance? _harmony;
    
    private FcsSceneInteractor _sceneInteractor;
    private readonly PurchaseDeck _purchaseDeck = new();
    public readonly MapTable MapTable = new MapTable();
    public readonly BallisticCalculator BallisticCalculator = new BallisticCalculator();
    public readonly GunSystem LeftGun = new GunSystem();
    public readonly GunSystem RightGun = new GunSystem();
    public readonly Turret Turret = new Turret();
    public readonly TriggerConsole TriggerConsole = new();
    
    // ===== 任务调度 =====
    // 用户不再指定炮管：任务入队后由调度器派给空闲炮管，炮管打完一发自动拉下一个。
    // 所有读写都在 Unity 主线程（入队来自点击回调，派发/完成来自协程），无并发，无需锁。
    private readonly Queue<ArtilleryTask> _taskQueue = new();

    /// <summary>人机协同注册表: 在飞/排队/炮上目标的统一承包登记(手动+自动共用)</summary>
    public readonly TargetRegistry Registry = new();

    /// <summary>CBT(无尽反炮兵)模式状态机: 阶段识别/双轨装药/FDC 扣留/紧急移动/基金纪律。</summary>
    public readonly CbtMonitor Cbt = new();

    /// <summary>已击发、炮弹仍在飞行中的任务(面板倒计时显示)。归零(=射表估计落地)时移除。</summary>
    public readonly List<ArtilleryTask> InFlight = new();

    /// <summary>机构就绪容差(度): 双机构与 aim 差小于此值且 CanFire 才进入确认。
    /// 0.1°——1°@10km≈175m 已超 HE 致死半径(120m), "对准"可能是打偏; 0.1°@10km≈17.5m
    /// 远小于任何毁伤半径。机构 4°/s、漂移 ~0.3°/s, 收敛有余量。与齐射门容差一致。</summary>
    private const float AimToleranceDeg = 0.1f;
    /// <summary>齐射方位容差(度): 两管目标方位差小于此值视为同方向, 放行双管齐射(玩家拉一次扳机=双弹)。
    /// 过严=丢齐射(退化为串行); 过松=近距目标误齐射。0.1°@10km≈17m。实战校准。</summary>
    private const float SalvoBearingToleranceDeg = 0.1f;
    /// <summary>Piece 自动归位容差(地图局部单位): 超过此值视为放错位置, 拉回真实炮塔。过小会每 5s 抖动。</summary>
    private const float TurretPieceSnapTolerance = 0.01f;
    /// <summary>移动任务装药预测: 额外覆盖装填时长不确定性的距离余量(km)</summary>
    private const float MovingDistanceMarginKm = 1.5f;

    // 连续瞄准几何缓存(Draggable Surface + turret), 避免每帧 GameObject.Find
    private Transform? _mapSurface;
    private Transform? _turretXf;

    /// <summary>手动任务目标解析器(FcsModule 创建雷达后注入)</summary>
    public TacticalRadar? EntityLocator { get; set; }

    /// <summary>当前各炮管正在执行的任务；null 表示该炮管空闲。供 UI 显示与调度判断。</summary>
    public ArtilleryTask? LeftTask { get; private set; }
    public ArtilleryTask? RightTask { get; private set; }

    /// <summary>是否有 FDC 任务在活动(装填/瞄准/在飞/队列, 未结束)。
    /// 2026-08-16 用户: FDC 一次只派一个——双管同时打 2 个 FDC, 两发几乎同时落地,
    /// 第一个 FDC 触发的暂停会被第二个 FDC 弹落地立即解除, 窗口重叠浪费。
    /// 08-16 修正: 【在飞弹也算活动】——FDC 弹击发后任务已退壳(Finished)但弹未落地,
    /// 旧判定会放行第二个 FDC → 双 FDC 弹重叠(13:29 实测)。</summary>
    public bool HasActiveFdcTask
    {
        get
        {
            if (LeftTask is { IsFdc: true } && LeftTask.progress is not (Progress.Finished or Progress.Canceled or Progress.Failed)) return true;
            if (RightTask is { IsFdc: true } && RightTask.progress is not (Progress.Finished or Progress.Canceled or Progress.Failed)) return true;
            foreach (var t in _taskQueue) if (t.IsFdc) return true;
            foreach (var t in InFlight) if (t.IsFdc) return true;   // FDC 弹在飞(未落地/未确认)也算活动
            return false;
        }
    }

    /// <summary>等待派发的任务数（已入队但还没分到炮管）。供 UI 显示。</summary>
    public int PendingCount => _taskQueue.Count;
    public Queue<ArtilleryTask> QueueCan => new Queue<ArtilleryTask>(_taskQueue);

    /// <summary>
    /// 控制台互斥锁：保护弹道计算器、确认开关台、采购台这三组全局唯一的"短操作"硬件。
    /// 临界区都很短（解算 / 确认弹 / 击发前的确认+击发），用完即放。
    /// </summary>
    private readonly CoroutineLock _deskLock = new();

    /// <summary>
    /// 炮塔方向角锁：方向角是全炮塔共享的，且一旦为某任务转到位，必须独占到这一发打出去为止
    /// （中途被另一任务转走就会打偏）。与 <see cref="_deskLock"/> 分开，是为了让本任务能在
    /// 后台早早抢占炮塔、与装填/升仰角重叠，而不挡住另一管炮在 deskLock 上的解算。
    ///
    /// 防死锁：凡同时需要两把锁处，一律"先 turret 后 desk"。本类只有击发段会嵌套两把锁
    /// （此时炮塔已由后台预约持有，再去抢 desk），解算/确认弹只单独用 desk，故无环、不死锁。
    /// </summary>
    private readonly CoroutineLock _turretLock = new();
    private float _lastPieceSyncTime;   // Piece 自动归位节流

    // ===== 任务看门狗 =====
    // 协程死亡(异常/被 Stop/子协程 yield 卡死)后, 槽位 LeftTask/RightTask 可能残留非 null,
    // TryDispatch 永远认为炮管忙 → 调度停摆且切 MANUAL/AUTO 都救不回(它们只改 autoMode)。
    // 协程内超时(装填 120s/瞄准 240s)依赖协程存活, 协程死了就失效 → 必须在协程外(Update)兜底。
    private Progress _watchdogProgressLeft = Progress.Pending;
    private float _watchdogSinceLeft;
    private Progress _watchdogProgressRight = Progress.Pending;
    private float _watchdogSinceRight;
    private float _lastWatchdogTime;   // 看门狗 5s 节流(低频兜底, 阈值都是 90s+)
    // 任务→炮塔预约映射(看门狗强制释放用): 任务协程创建预约时登记, 结束后移除。
    private readonly Dictionary<ArtilleryTask, TurretReservation> _taskTurretRes = new();
    private readonly Dictionary<Progress, float> _watchdogLimit = new()
    {
        [Progress.SelectingBullet] = 90f,   // 采购+回退链最长 ~4 次采购
        [Progress.LoadingBullet] = 240f,    // 装填(转弹仓+推弹)机械时间, 预留余量
        [Progress.LoadingPowder] = 120f,    // 装药采购循环
        [Progress.WaitLoading] = 150f,      // 等 CanFire(装填完成确认)
        [Progress.Aiming] = 300f,           // 瞄准跟踪(协程内已有 240s 超时, 此处兜底协程死亡)
        [Progress.WaitingForFire] = 120f,   // 等击发
        [Progress.BackToIdle] = 180f,       // 退膛回位
    };

    // 正在运行的协程句柄。Dispose 时全部停掉，避免热重载后旧 ALC 的协程继续执行导致崩溃。
    private readonly List<object> _runningCoroutines = new();
    public FSC() {
        this._sceneInteractor = new FcsSceneInteractor(this);
    }

    public bool IsBound { get; private set; } = false;

    /// <summary>查找并绑定游戏对象。返回 false 表示当前场景还没有目标控件。</summary>
    public bool TryBind()
    {
        // 每次重载创建全新的 Harmony 实例，避免与上一版补丁冲突。
        _sceneInteractor = new FcsSceneInteractor(this);
        _sceneInteractor.Initialize();
        _harmony = new HarmonyInstance(HarmonyId);
        _deskLock.Reset();
        _turretLock.Reset();
        IsBound = MapTable.TryBind()
                  && BallisticCalculator.TryBind()
                  && LeftGun.TryBind("Left")
                  && RightGun.TryBind("Right")
                  && _purchaseDeck.TryBind()
                  && Turret.TryBind()
                  && TriggerConsole.TryBind();
        MelonLogger.Msg("[FCS] Initialize: " + (IsBound ? "success" : "failed"));
        if (IsBound) {
            CacheAimGeometry();   // 连续瞄准几何缓存(一次, 避免每帧 GameObject.Find)
            // 驻留装药补给协程:保证装药充足,减少任务内等待购买。
            _runningCoroutines.Add(MelonCoroutines.Start(ReplenishPowderLoop()));
        }
        // 探针已完成使命，需要时取消注释：
        // if (IsBound) RunEnemyProbe();

        return IsBound;
    }

    /// <summary>
    /// 深探 FireMission 的 Il2Cpp Dictionary 结构：
    ///   .Entities → Dictionary&lt;string, MapEntity&gt; (目标数据库)
    ///   .RunningTimers → Dictionary&lt;string, TimerValue&gt; (CBT 计时器)
    /// Il2Cpp 的泛型字典与 .NET 标准 Dictionary API 不同，需要探查正确的读取方式。
    /// </summary>
    private void RunEnemyProbe()
    {
        try
        {
            MelonLogger.Msg("[FCS] === MapEntity deep probe ===");
            var fm = GameObject.Find("Fire Mission Root")?.GetComponent<FireMission>();
            if (fm == null) { MelonLogger.Msg("[FCS]   no FireMission"); return; }

            var fmType = fm.GetType();
            var entitiesProp = fmType.GetProperty("Entities", BindingFlags.Public | BindingFlags.Instance);
            if (entitiesProp == null) { MelonLogger.Msg("[FCS]   no Entities prop"); return; }
            var entities = entitiesProp.GetValue(fm);
            if (entities == null) { MelonLogger.Msg("[FCS]   Entities == null"); return; }

            // 获取 Count
            var countProp = entities.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            var total = countProp != null ? (int)countProp.GetValue(entities) : -1;
            MelonLogger.Msg($"[FCS] Entities.Count = {total}");

            // 通过 Il2Cpp 泛型 GetEnumerator() 枚举前 5 条
            var getEnum = entities.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
            if (getEnum == null) { MelonLogger.Msg("[FCS]   no GetEnumerator"); return; }
            var enumerator = getEnum.Invoke(entities, null);
            if (enumerator == null) { MelonLogger.Msg("[FCS]   GetEnumerator returned null"); return; }

            var enumType = enumerator.GetType();
            var moveNext = enumType.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
            var currentProp = enumType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
            if (moveNext == null || currentProp == null) { MelonLogger.Msg("[FCS]   no MoveNext/Current on enumerator"); return; }

            int shown = 0;
            while (shown < 5 && (bool)moveNext.Invoke(enumerator, null)!)
            {
                var kvp = currentProp.GetValue(enumerator);
                if (kvp == null) continue;

                var kvpType = kvp.GetType();
                var keyProp = kvpType.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                var valueProp = kvpType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);

                var key = keyProp?.GetValue(kvp)?.ToString() ?? "?";
                var mapEntity = valueProp?.GetValue(kvp);

                MelonLogger.Msg($"[FCS] --- Entities['{key}'] ---");
                if (mapEntity == null) { MelonLogger.Msg("[FCS]   MapEntity = null"); shown++; continue; }

                var meType = mapEntity.GetType();
                MelonLogger.Msg($"[FCS]   MapEntity type: {meType.FullName}");
                MelonLogger.Msg($"[FCS]   MapEntity.ToString(): {mapEntity}");

                // Dump MapEntity 所有属性
                foreach (var p in meType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.Name is "ObjectClass" or "Pointer" or "WasCollected")
                        continue;
                    try
                    {
                        var v = p.GetValue(mapEntity);
                        if (v == null)
                            MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) = null");
                        else
                            MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) = {v}");
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) → {ex.GetType().Name}: {ex.Message}");
                    }
                }
                shown++;
            }
            MelonLogger.Msg($"[FCS] === Enumerated {shown} MapEntity entries ===");

            // Dump coordinateRoot: 这是游戏网格坐标 → 地图桌面的映射桥梁
            DumpCoordinateRoot(fm);

            DumpTimerValue(fm);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS] MapEntity probe failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void DumpCoordinateRoot(FireMission fm)
    {
        try
        {
            var crProp = fm.GetType().GetProperty("coordinateRoot", BindingFlags.Public | BindingFlags.Instance);
            if (crProp == null) { MelonLogger.Msg("[FCS] --- coordinateRoot: no property ---"); return; }
            var cr = crProp.GetValue(fm) as RectTransform;
            if (cr == null) { MelonLogger.Msg("[FCS] --- coordinateRoot: null ---"); return; }

            MelonLogger.Msg("[FCS] --- coordinateRoot RectTransform ---");
            MelonLogger.Msg($"[FCS]   rect = {cr.rect}");
            MelonLogger.Msg($"[FCS]   anchorMin = {cr.anchorMin}, anchorMax = {cr.anchorMax}");
            MelonLogger.Msg($"[FCS]   pivot = {cr.pivot}");
            MelonLogger.Msg($"[FCS]   sizeDelta = {cr.sizeDelta}");
            MelonLogger.Msg($"[FCS]   localPosition = {cr.localPosition}");
            MelonLogger.Msg($"[FCS]   localScale = {cr.localScale}");
            MelonLogger.Msg($"[FCS]   anchoredPosition = {cr.anchoredPosition}");
            MelonLogger.Msg($"[FCS]   lossyScale = {cr.lossyScale}");
            // 也 dump 父 Transform（可能是 Draggable Surface 或 Map 相关）
            var parent = cr.parent;
            if (parent != null)
                MelonLogger.Msg($"[FCS]   parent = '{parent.name}', parent.localScale = {parent.localScale}, parent.localPosition = {parent.localPosition}");

            // 尝试把 MapEntity.Position 通过 coordinateRoot 转换
            var entitiesProp = fm.GetType().GetProperty("Entities", BindingFlags.Public | BindingFlags.Instance);
            if (entitiesProp != null)
            {
                var entities = entitiesProp.GetValue(fm);
                if (entities != null)
                {
                    var getEnum = entities.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
                    if (getEnum != null)
                    {
                        var enumerator = getEnum.Invoke(entities, null);
                        if (enumerator != null)
                        {
                            var moveNext = enumerator.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
                            var currentProp = enumerator.GetType().GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
                            if (moveNext != null && currentProp != null && (bool)moveNext.Invoke(enumerator, null)!)
                            {
                                var kvp = currentProp.GetValue(enumerator);
                                var valueProp = kvp?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                                var me = valueProp?.GetValue(kvp);
                                var posProp = me?.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
                                if (posProp != null)
                                {
                                    var mapPos = (Vector3)posProp.GetValue(me)!;
                                    // 试 RectTransformUtility 转换
                                    var worldFromRect = cr.TransformPoint(mapPos);
                                    MelonLogger.Msg($"[FCS]   Test: MapEntity.Pos={mapPos} → coordRoot.TransformPoint={worldFromRect}");
                                    // 试 Draggable Surface TransformPoint
                                    var ds = GameObject.Find("Draggable Surface")?.transform;
                                    if (ds != null)
                                        MelonLogger.Msg($"[FCS]   Test: MapEntity.Pos={mapPos} → DragSurface.TransformPoint={ds.TransformPoint(mapPos)}");
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) { MelonLogger.Warning($"[FCS] coordinateRoot dump: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void DumpTimerValue(FireMission fm)
    {
        try
        {
            var timersProp = fm.GetType().GetProperty("RunningTimers", BindingFlags.Public | BindingFlags.Instance);
            if (timersProp == null) return;
            var timers = timersProp.GetValue(fm);
            if (timers == null) { MelonLogger.Msg("[FCS] --- TimerValue: no timers (empty dictionary) ---"); return; }

            // 尝试从 Values 获取元素
            var valuesProp = timers.GetType().GetProperty("Values", BindingFlags.Public | BindingFlags.Instance);
            if (valuesProp == null) return;
            var values = valuesProp.GetValue(timers);
            if (values == null) return;

            var getEnum = values.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
            if (getEnum == null) return;
            var enumerator = getEnum.Invoke(values, null);
            if (enumerator == null) return;

            var moveNext = enumerator.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
            var currentProp = enumerator.GetType().GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
            if (moveNext == null || currentProp == null) return;

            if ((bool)moveNext.Invoke(enumerator, null)!)
            {
                var tv = currentProp.GetValue(enumerator);
                if (tv != null)
                {
                    MelonLogger.Msg($"[FCS] --- TimerValue: {tv.GetType().FullName} ---");
                    foreach (var p in tv.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (p.Name is "ObjectClass" or "Pointer" or "WasCollected") continue;
                        try { MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) = {p.GetValue(tv)}"); }
                        catch (Exception ex) { MelonLogger.Msg($"[FCS]   .{p.Name} → {ex.GetType().Name}: {ex.Message}"); }
                    }
                }
            }
            else MelonLogger.Msg("[FCS] --- TimerValue: enumerator empty ---");
        }
        catch (Exception ex) { MelonLogger.Warning($"[FCS] TimerValue dump: {ex.GetType().Name}: {ex.Message}"); }
    }

    // ===== CBT 扫描探针 v2(定位 CounterBatteryTimer 单例后删除) =====
    // 反编译确认: 真正的 CBT 倒计时是场景单例 CounterBatteryTimer(MonoBehaviour):
    //   totalDurationSeconds(初始时长, Inspector 配置=600) / _remainingSeconds / _running / _expired /
    //   _permanentlyStopped / endTime(double)
    //   方法: Init(float) / StartTimer()(第一发落地后启动) / AddTime(float)(击杀+30s) /
    //         SetTime(float)(紧急移动重置600) / PauseTimer() / UnpauseTimer()(FDC 击杀暂停) / PermanentlyStop()
    //   属性: TimeRemaining / IsRunning / IsExpired / IsPermanentlyStopped
    //   事件: onTimerStarted(after first impact) / onTimerTick(每帧, 剩余秒) / onTimerExpired /
    //         onTimerPaused / onTimerUnpaused / onTimerPermanentlyStopped
    // FireMission.RunningTimers 的 TimerValue(InitialSeconds=36000=10h, CurrentSeconds 每秒+1) 是
    // 另一个通用计时器, 与 600s CBT 无关 —— v1 探针找错了对象, v2 改读 CounterBatteryTimer。

    public void CbtScanProbe()
    {
        _runningCoroutines.Add(MelonCoroutines.Start(CbtSampleLoop()));
    }

    /// <summary>每秒采样 CounterBatteryTimer 状态 x12: 看 TimeRemaining 递减速率/暂停/重置。
    /// 顺带 dump FireMission.RunningTimers keys(确认 36000 计时器身份) + UI 文本(TMP)。</summary>
    private IEnumerator CbtSampleLoop()
    {
        MelonLogger.Msg("[CBT-SCAN] === v2: CounterBatteryTimer 每秒采样 x12 ===");
        for (int i = 0; i < 12; i++)
        {
            DumpCbtTimer(i);
            yield return new WaitForSeconds(1f);
        }
        MelonLogger.Msg("[CBT-SCAN] === v2 done ===");
    }

    private void DumpCbtTimer(int tick)
    {
        try
        {
            var cbt = Object.FindObjectOfType<CounterBatteryTimer>();
            if (cbt == null)
            {
                MelonLogger.Msg($"[CBT-SCAN] t={tick}: CounterBatteryTimer NOT FOUND in scene");
                DumpTimerKeys(tick);
                DumpTimerTexts();
                return;
            }
            var t = cbt.GetType();
            var total = t.GetField("totalDurationSeconds")?.GetValue(cbt);
            var remain = t.GetProperty("TimeRemaining")?.GetValue(cbt);
            var running = t.GetProperty("IsRunning")?.GetValue(cbt);
            var expired = t.GetProperty("IsExpired")?.GetValue(cbt);
            var permStopped = t.GetProperty("IsPermanentlyStopped")?.GetValue(cbt);
            var endTime = t.GetField("endTime", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(cbt);
            MelonLogger.Msg($"[CBT-SCAN] t={tick}: total={total} remain={remain} running={running} expired={expired} permStop={permStopped} endTime={endTime} go={cbt.gameObject.name}");
            if (tick == 0)
            {
                // 首帧额外 dump 全部字段+属性(识别暂停/启动标志)
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.Name is "ObjectClass" or "Pointer" or "WasCollected") continue;
                    try { MelonLogger.Msg($"[CBT-SCAN]   .{f.Name} ({f.FieldType.Name}) = {f.GetValue(cbt)}"); }
                    catch (Exception ex) { MelonLogger.Msg($"[CBT-SCAN]   .{f.Name} -> {ex.GetType().Name}"); }
                }
                DumpTimerKeys(tick);
                DumpTimerTexts();
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Msg($"[CBT-SCAN] t={tick}: err {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>RunningTimers 字典 keys + 每个 TimerValue 数值 —— 确认 36000 计时器是什么。</summary>
    private void DumpTimerKeys(int tick)
    {
        try
        {
            var fm = GameObject.Find("Fire Mission Root")?.GetComponent<FireMission>();
            if (fm == null) { MelonLogger.Msg($"[CBT-SCAN] t={tick}: no FireMission"); return; }
            var timersProp = fm.GetType().GetProperty("RunningTimers", BindingFlags.Public | BindingFlags.Instance);
            if (timersProp == null) return;
            var timers = timersProp.GetValue(fm);
            if (timers == null) { MelonLogger.Msg($"[CBT-SCAN] t={tick}: RunningTimers null"); return; }
            var countProp = timers.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            MelonLogger.Msg($"[CBT-SCAN] t={tick}: RunningTimers count={(int)countProp!.GetValue(timers)!}");

            var getEnum = timers.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
            var enumerator = getEnum!.Invoke(timers, null);
            var moveNext = enumerator!.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
            var currentProp = enumerator.GetType().GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
            int i = 0;
            while ((bool)moveNext!.Invoke(enumerator, null)!)
            {
                var kvp = currentProp!.GetValue(enumerator);
                if (kvp == null) continue;
                var keyProp = kvp.GetType().GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                var valueProp = kvp.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                var key = keyProp?.GetValue(kvp)?.ToString() ?? "?";
                var tv = valueProp?.GetValue(kvp);
                i++;
                if (tv == null) { MelonLogger.Msg($"[CBT-SCAN] t={tick}:   [{key}] = null"); continue; }
                var initial = tv.GetType().GetProperty("InitialSeconds")?.GetValue(tv);
                var current = tv.GetType().GetProperty("CurrentSeconds")?.GetValue(tv);
                var started = tv.GetType().GetProperty("StartedAt")?.GetValue(tv);
                MelonLogger.Msg($"[CBT-SCAN] t={tick}:   [{key}] Initial={initial} Current={current} StartedAt={started}");
            }
            if (i == 0) MelonLogger.Msg($"[CBT-SCAN] t={tick}:   (no entries)");
        }
        catch (Exception ex) { MelonLogger.Msg($"[CBT-SCAN] t={tick}: keys err {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>扫描场景里所有 Text/TMP 文本, 找时间格式的 UI 显示(倒计时), 与 TimerValue 对照。</summary>
    private void DumpTimerTexts()
    {
        int shown = 0;
        foreach (var go in Object.FindObjectsOfType<GameObject>(true))
        {
            string? t = null;
            var txt = go.GetComponent<Text>();
            if (txt != null) t = txt.text;
            else
            {
                var tmp = go.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                if (tmp != null) t = tmp.text;
            }
            if (string.IsNullOrEmpty(t)) continue;
            t = t.Trim();
            if (t.Length > 24) continue;
            // 时间格式: 纯数字/冒号分隔(如 600:00, 09:58:22)
            if (!System.Text.RegularExpressions.Regex.IsMatch(t, @"^[\d:\s.,]+$")) continue;
            if (shown++ >= 20) { MelonLogger.Msg("[CBT-SCAN]   ...texts truncated"); break; }
            MelonLogger.Msg($"[CBT-SCAN]   UI text [{go.name}] = \"{t}\"");
        }
        if (shown == 0) MelonLogger.Msg("[CBT-SCAN]   no timer-like UI texts found");
    }

    public void Update() {
        _sceneInteractor.Update();
        // CBT 阶段机(0.5s 节流, 驱动双轨装药/优先级/FDC 扣留/紧急移动)
        Cbt.Update();
        // 膛内弹来源检测(5s 节流): 膛内有弹但来源 Unknown(开局/退壳后) → 用户手动装的,
        // 标 Manual 供派发保护(自动任务不碰用户屯的弹)。活动中的自动任务跳过(见 CheckChamberOrigin)。
        if (Time.time - _lastOriginCheckTime > 5f)
        {
            _lastOriginCheckTime = Time.time;
            CheckChamberOrigin(LeftGun, LeftTask);
            CheckChamberOrigin(RightGun, RightTask);
        }
        // 任务看门狗(5s 节流): 协程死亡/卡死后槽位残留会永久卡死调度, 这里按进度停留时长强制清理。
        if (Time.time - _lastWatchdogTime > 5f)
        {
            _lastWatchdogTime = Time.time;
            WatchdogSlot(LeftTask, ref _watchdogProgressLeft, ref _watchdogSinceLeft);
            WatchdogSlot(RightTask, ref _watchdogProgressRight, ref _watchdogSinceRight);
        }
        // 面板倒计时归零(=射表估计落地)的任务移出在飞列表
        if (InFlight.Count > 0)
            InFlight.RemoveAll(t => Time.time - t.FiredAt >= t.EstimatedToF);
        // Piece 自动归位: 忘了放/紧急移动后 5s 内自愈(手动/自动都生效)
        if (Time.time - _lastPieceSyncTime > 5f) {
            _lastPieceSyncTime = Time.time;
            SyncTurretPiece();
        }
    }

    private float _lastOriginCheckTime;
    private float _lastFdcWaitLog;   // FDC 等旧弹落地诊断日志节流(2026-08-16)

    /// <summary>膛内弹来源检测: 膛空 → 复位 Unknown; 膛内有弹且 Unknown → 用户手动装的(标 Manual)。
    /// Auto(自动任务装的)保持——任务中断遗留的弹仍可被下个自动任务复用。
    /// 2026-08-16: 自动任务活动期(装填/等待/瞄准/回位)跳过本管——"推弹完成→确认弹种"之间
    /// 膛内弹已出现但 Origin 可能还没标 Auto, 5s 轮询撞上会误判成用户手动 → 本管被永久跳过派发。</summary>
    private static void CheckChamberOrigin(GunSystem gun, ArtilleryTask? task)
    {
        if (gun.IsChamberEmpty())
        {
            if (gun.Origin != GunSystem.ChamberOrigin.Unknown) gun.MarkChamberEmpty();
            return;
        }
        bool autoTaskActive = task != null && task.Source == TaskSource.Auto
            && task.progress is not (Progress.Pending or Progress.Finished or Progress.Canceled or Progress.Failed);
        if (autoTaskActive) return;   // 膛内弹归正在执行的任务管, 轮询不抢判
        if (gun.Origin == GunSystem.ChamberOrigin.Unknown)
        {
            MelonLogger.Msg($"[FCS] 检测到膛内弹 {gun.BulletInChamber()} (非自动装填), 标记用户手动, 自动任务将跳过本管");
            gun.MarkManual();
        }
    }

    /// <summary>看门狗: 任务在某个进度停留超过阈值 → 协程可能已死亡, 强制取消并释放槽位恢复调度。
    /// 幂等: 协程若还活着, Canceled 会在下一个检查点退出(见 RunTaskRoutine); 已死的直接清引用。</summary>
    private void WatchdogSlot(ArtilleryTask? task, ref Progress lastProgress, ref float since)
    {
        if (task == null || task.progress == Progress.Finished || task.progress == Progress.Canceled
            || task.progress == Progress.Failed || task.progress == Progress.Pending)
        {
            lastProgress = Progress.Pending;
            since = 0f;
            return;
        }
        // 进度变化 → 重置计时
        if (task.progress != lastProgress)
        {
            lastProgress = task.progress;
            since = Time.time;
            return;
        }
        if (!_watchdogLimit.TryGetValue(task.progress, out var limit)) return;
        if (Time.time - since <= limit) return;
        MelonLogger.Error($"[FCS] 看门狗: 任务 T{task.targetId}({task.entityId}) 卡在 {task.progress} 超过 {limit}s, " +
                          $"bullet={task.bulletType} charged={task.LoadedCharge} fired={task.Fired} canceled={task.Canceled} " +
                          $"btn.max={_sceneInteractor.maxCharge} task.max={task.useMaxCharge}, 强制取消释放槽位");
        task.Canceled = true;
        // 登记/槽位立即释放(协程若还活着, finally 幂等; 已死的这里兜底)——否则目标被永久屏蔽
        if (!task.Fired) Registry.Release(task);
        if (LeftTask == task) LeftTask = null;
        if (RightTask == task) RightTask = null;
        // 炮塔锁: 协程可能卡死在无检查点的等待上(后台 ReserveTurretAndRotate 还在转),
        // 直接标记 Released 让后台自归; 已持锁的立即释放。
        if (_taskTurretRes.TryGetValue(task, out var res))
        {
            res.Released = true;
            if (res.Acquired) _turretLock.Release();
            _taskTurretRes.Remove(task);
        }
        // deskLock: 任务卡死在装药采购等锁点, 可能是驻留协程(SyncCalculatorVisual/ReplenishPowderLoop)
        // 持锁死亡泄漏 → 强制复位, 防止所有后续任务永久等锁。
        if (task.progress == Progress.LoadingPowder || task.progress == Progress.SelectingBullet)
        {
            MelonLogger.Error($"[FCS] 看门狗: 任务卡在采购锁区, 强制复位 deskLock");
            _deskLock.Reset();
        }
        lastProgress = Progress.Pending;
        since = 0f;
        try { OnGunIdle?.Invoke(); } catch { }
    }

    /// <summary>自动归位 Player Turret Piece 到真实炮塔位置(忘了放/紧急移动后找不到自己时自愈)。
    /// Piece 与 GetTurretLocal 都是地图局部坐标, 直接写 localPosition。TurretLocation 找不到时 GetTurretLocal 回退自身 → 无操作。</summary>
    public void SyncTurretPiece() {
        var piece = MapTable.Turret;
        if (piece == null) return;
        var real = MapTable.GetTurretLocal();
        if ((piece.localPosition - real).magnitude > TurretPieceSnapTolerance)
            piece.localPosition = real;
    }

    /// <summary>最近的在飞炮弹预计落地剩余秒数。无在飞弹返回 -1。
    /// 紧急移动/FDC 暂停收益保护: 在飞弹落地会立即解除暂停, 触发前需避开。
    /// 判定与 InFlight 移除同源(射表 remain<0 即视为落地)——宁等不早, 防射表误差导致
    /// 误触发(弹实际还在飞, 暂停被立即解除)。注意: 返回的是【最早】落地——只够"有无在飞弹"
    /// 判定, FDC 落地顺序保护要用 <see cref="LatestImpactIn"/>。</summary>
    public float SoonestImpactIn()
    {
        float best = -1f;
        foreach (var t in InFlight)
        {
            float remain = t.EstimatedToF - (Time.time - t.FiredAt);
            if (remain < 0f) continue;   // 射表已落地(与 InFlight 移除同判定)
            if (best < 0f || remain < best) best = remain;
        }
        return best;
    }

    /// <summary>最晚的在飞炮弹预计落地剩余秒数。无在飞弹返回 -1。
    /// FDC 落地顺序保护用: 需保证【所有】旧弹都比 FDC 弹先落地(max < fdcToF),
    /// 否则任一旧弹在 FDC 弹之后落地都会立即解除 FDC 暂停。SoonestImpactIn 是 min, 不适用。</summary>
    public float LatestImpactIn()
    {
        float best = -1f;
        foreach (var t in InFlight)
        {
            float remain = t.EstimatedToF - (Time.time - t.FiredAt);
            if (remain < 0f) continue;
            if (remain > best) best = remain;
        }
        return best;
    }

    /// <summary>键盘快捷键触发射击目标（小键盘 1-4）</summary>
    public void FireTarget(int targetId) {
        _sceneInteractor.FireTarget(targetId);
    }

    /// <summary>释放：撤销补丁、清空 IL2CPP 引用。</summary>
    public void Dispose()
    {
        // 停掉所有未完成的协程，否则热重载后旧 ALC 的协程仍会被 Unity 驱动 → 崩溃。
        foreach (var handle in _runningCoroutines) {
            try { MelonCoroutines.Stop(handle); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] Stop coroutines failed: {ex}"); }
        }
        _runningCoroutines.Clear();

        // 清空调度状态，避免热重载后残留任务/槽位影响新一轮绑定。
        _taskQueue.Clear();
        LeftTask = null;
        RightTask = null;
        InFlight.Clear();
        Registry.Clear();

        _sceneInteractor.ShutDown();
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { MelonLogger.Error($"[FCS] UnpatchSelf failed: {ex}"); }
        _harmony = null;
    }

    /// <summary>
    /// 驻留协程:每 5s 检查两管炮装药,低于阈值补一包。用 _deskLock 保护(采购台是共享硬件,
    /// 与任务内采购互斥)。TryBind 成功后登记进 _runningCoroutines,Dispose 时随协程一起 Stop。
    /// CBT 基金纪律(§3.6): 跌破基金线时停止装药囤积(只用 DRIL 维持循环, 任务内按需仍会买)。
    /// </summary>
    private IEnumerator ReplenishPowderLoop() {
        var lastFundLogTime = -1f;   // 基金线以下日志节流: 只打一次, 恢复后重置
        while (true) {
            yield return new WaitForSeconds(PowderCheckInterval);
            // 取两管炮装药的最小值:任一管低于阈值就补
            var charges = Math.Min(LeftGun.RemainingCharges(), RightGun.RemainingCharges());
            if (charges >= PowderReplenishThreshold) continue;
            // §3.6: 积分跌破基金线(90) → 停囤积, 保 65+25 重启资金。
            // 日志节流(2026-08-15): 基金线以下是常态(紧急移动后 pts=24), 每 5s 刷屏会拖慢主线程。
            if (Cbt.IsCbtMode && Cbt.FundMargin < 0f) {
                if (lastFundLogTime < 0 || Time.time - lastFundLogTime > 60f) {
                    MelonLogger.Msg($"[FCS] AutoReplenish: 基金线以下({Cbt.RequisitionPoints}<{CbtMonitor.FundLine}), 停止装药囤积");
                    lastFundLogTime = Time.time;
                }
                continue;
            }
            lastFundLogTime = -1f;
            MelonLogger.Msg(
                $"[FCS] AutoReplenish: powder charges {charges} < {PowderReplenishThreshold}, buying one");
            yield return _deskLock.Acquire();
            try {
                yield return _purchaseDeck.BuyPowders();
            }
            finally {
                _deskLock.Release();
            }
        }
    }

    public IEnumerator ExposeAllEntities() {
        while (true) {
            foreach (var m in MapTable.GetAllFireMissionEntities()) {
                m.GetComponent<Image>().enabled = true;
            }

            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>
    /// 把任务加入调度队列。用户不指定炮管——调度器自动派给空闲炮管。
    /// 入队后立即尝试派发；若两管炮都忙，任务留在队列里，等某管炮打完自动拉取。
    /// 必须在主线程调用（点击回调即是）。
    /// </summary>
    public void EnqueueTask(ArtilleryTask task) {
        task.progress = Progress.Pending;
        // 手动任务: 解析标记位置的存活敌目标 → entityId(注册表按目标屏蔽);
        // 解析失败保持空 → 按位置提交(小半径屏蔽)。
        if (task.Source == TaskSource.Manual && task.entityId is not { Length: > 0 } && EntityLocator != null)
            task.entityId = EntityLocator.FindNearestHostileId(task.position, TargetRegistry.ManualResolveMaxDistance) ?? "";
        _taskQueue.Enqueue(task);
        // 手动任务入队即登记(队列在自动清队列中存活); 自动任务在派发时登记。
        if (task.Source == TaskSource.Manual && IsKillContract(task)) Registry.Commit(task);
        TryDispatch();
    }

    /// <summary>击杀契约: 非杀伤弹(STAR 照明)不登记注册表——照明弹的意义是把目标暴露出来打,
    /// 登记反而触发 65s 在飞窗口屏蔽, 雷达看着刚被照亮的活目标干瞪眼。
    /// ponytail: 只列 STAR; 其他非杀伤弹(SMOKE 等)出现同样屏蔽再扩展。</summary>
    private static bool IsKillContract(ArtilleryTask t) => t.bulletType != BulletType.STAR;

    /// <summary>
    /// 紧急移动(§3.5, 2026-08-15 实测修正): MoveZone 卡 = 暂停倒计时到下一发落地 + 转移阵地,
    /// 不是重置 600s。协程持 _deskLock 采购; 采购完成后等待倒计时暂停生效(IsRunning→false)
    /// 或 15s 超时, 再等转移动画稳定(5s), 清洗双管炮管脏状态, 然后回调通知恢复派发。
    /// 注意: 转移阵地会重置炮管状态(弹被卸下)——FcsModule 在转移期间暂停派发防卡死,
    /// 且新任务开始前必须确认炮管已回到干净初始态(否则装填卡死)。
    /// </summary>
    public void StartEmergencyMove(Action? onDone = null)
    {
        _runningCoroutines.Add(MelonCoroutines.Start(EmergencyMoveRoutine(onDone)));
    }

    private IEnumerator EmergencyMoveRoutine(Action? onDone)
    {
        if (!_purchaseDeck.HasEmergencyMoveCard)
        {
            MelonLogger.Error("[FCS] 紧急移动: 未找到 65 点采购卡, 跳过自动触发");
            onDone?.Invoke();
            yield break;
        }
        yield return _deskLock.Acquire();
        try
        {
            yield return _purchaseDeck.BuyEmergencyMove();
        }
        finally
        {
            _deskLock.Release();
        }
        // 等游戏暂停倒计时(MoveZone 卡 → PauseTimer); 最多等 15s
        for (var i = 0; i < 30; i++)
        {
            yield return new WaitForSeconds(0.5f);
            if (!Cbt.IsRunning) break;
        }
        // 等转移动画稳定: 倒计时暂停≠转移完成, 立即派发会在炮管被重置时装填 → 卡死
        yield return new WaitForSeconds(5f);
        // 几何引用刷新(一次性): 转移可能重建 Piece/地图面, 缓存引用失效 → 瞄准基准错误
        RefreshAimGeometry();
        // 移动中禁止创建任务(2026-08-16 用户): 固定 5s 未必覆盖完整转移动画——若真实炮塔/
        // 地图面仍在移动, 此刻派发的任务会把瞄准基准冻结在移动中的位置 → 转移后打空。
        // 等到基准连续两次采样(1s 间隔)一致(上限 10s)才放行; 引用不可用时无法判定直接放行。
        yield return WaitForMoveSettle(10f);
        // 清洗双管炮管脏状态(残留弹/装药/状态机停在中间)——直接装填会卡 WaitLoading。
        // 注意: 用户手动装的弹(Origin=Manual)不退壳——转移阵地后游戏自己会重置炮管,
        // 但手动弹是用户资产, 保留(若游戏重置了也拦不住, 至少我们不主动退)。
        if (LeftGun.Origin != GunSystem.ChamberOrigin.Manual) yield return LeftGun.CleanState();
        if (RightGun.Origin != GunSystem.ChamberOrigin.Manual) yield return RightGun.CleanState();
        MelonLogger.Msg($"[FCS] 紧急移动完成, 倒计时暂停={!Cbt.IsRunning} remain={Cbt.TimeRemaining:F1}s 炮管已清洗");
        onDone?.Invoke();
    }

    /// <summary>等待转移基准完全静止: 真实炮塔在地图面下的局部位置连续两次采样(1s 间隔)一致。
    /// 仅紧急移动协程内调用(有界 ≤durSeconds), 不做每帧轮询; 引用不可用(无法判定)立即放行。
    /// 判定用局部坐标——Bearing/DistKm/雷达角度都以它为基准, 它静止了任务才不会被冻结错基准。</summary>
    private IEnumerator WaitForMoveSettle(float durSeconds)
    {
        var deadline = Time.time + durSeconds;
        Vector3? prev = TurretLocalOnSurface();
        while (Time.time < deadline)
        {
            yield return new WaitForSeconds(1f);
            var cur = TurretLocalOnSurface();
            if (prev == null || cur == null) yield break;              // 引用不可用 → 无法判定, 放行
            if ((cur.Value - prev.Value).sqrMagnitude < 0.0001f) yield break;  // 已静止
            prev = cur;
        }
    }

    /// <summary>真实炮塔在地图面下的局部坐标(瞄准基准)。引用缺失返回 null。</summary>
    private Vector3? TurretLocalOnSurface()
    {
        if (_mapSurface == null || _turretXf == null) return null;
        return _mapSurface.InverseTransformPoint(_turretXf.position);
    }

    private Vector3? _lastTurretLocal;         // 手动转移检测: 上次采样基准
    private float _lastTransferCheckTime;

    /// <summary>玩家手动紧急移动检测(2026-08-16 用户): 玩家手动买 MoveZone 卡/转移阵地 →
    /// 游戏移动 TurretLocation → 基准位置突变 → 返回 true(调用方执行转移后清理, 防任务卡死——
    /// 玩家反馈 bug: 手动移动后流程卡住)。正常游戏 TurretLocation 不动(玩家拖 Piece 不影响它);
    /// 自动紧急移动期间(_emergencyMoveInProgress)由调用方跳过。5s 节流(用户: 5s 就行——
    /// 转移是持续过程, 5s 间隔采样对比移动前后位置差, 一样能触发)。</summary>
    public bool DetectManualTransferMove()
    {
        if (Time.time - _lastTransferCheckTime < 5f) return false;
        _lastTransferCheckTime = Time.time;
        var local = TurretLocalOnSurface();
        if (local == null) { _lastTurretLocal = null; return false; }
        if (_lastTurretLocal == null) { _lastTurretLocal = local; return false; }   // 首次采样(开局)
        float dist = (local.Value - _lastTurretLocal.Value).magnitude;
        _lastTurretLocal = local;
        // 阈值 0.3 world unit ≈ 80m/s: 转移是 km 级移动(动画期每秒 >1 unit); 正常静止为 0
        return dist > 0.3f;
    }

    /// <summary>手动转移后的清理(2026-08-16 用户): 玩家已手动买 MoveZone 卡, 跳过采购——
    /// 等转移动画稳定 → 刷新几何引用 → 等基准静止 → 清洗双管(手动弹不退壳) → 回调恢复派发。
    /// 与自动紧急移动的转移后段一致, 防炮管状态被重置后任务卡死。</summary>
    public void StartTransferCleanup(Action? onDone = null)
    {
        _runningCoroutines.Add(MelonCoroutines.Start(TransferCleanupRoutine(onDone)));
    }

    private IEnumerator TransferCleanupRoutine(Action? onDone)
    {
        yield return new WaitForSeconds(5f);   // 转移动画稳定
        RefreshAimGeometry();
        yield return WaitForMoveSettle(10f);
        if (LeftGun.Origin != GunSystem.ChamberOrigin.Manual) yield return LeftGun.CleanState();
        if (RightGun.Origin != GunSystem.ChamberOrigin.Manual) yield return RightGun.CleanState();
        MelonLogger.Msg("[FCS] 手动转移清理完成, 炮管已清洗");
        onDone?.Invoke();
    }

    /// <summary>弹种不可用回退链: DRIL→LE→HE(开局即有)→HCHE; AP 不可用 → HE。链尾返回自身。</summary>
    private static BulletType FallbackShell(BulletType t) => t switch
    {
        BulletType.DRIL => BulletType.LE,
        BulletType.LE => BulletType.HE,
        BulletType.AP => BulletType.HE,
        BulletType.HE => BulletType.HCHE,
        _ => t
    };

    /// <summary>插队：任务放到队列最前面，用于高优先级目标</summary>
    public void EnqueueTaskFront(ArtilleryTask task) {
        task.progress = Progress.Pending;
        var existing = _taskQueue.ToArray();
        _taskQueue.Clear();
        _taskQueue.Enqueue(task);
        foreach (var t in existing) _taskQueue.Enqueue(t);
        TryDispatch();
    }

    /// <summary>把队首任务派给空闲炮管，直到没有空闲炮管或队列空。</summary>
    private void TryDispatch() {
        while (_taskQueue.Count > 0) {
            LeftRight slot;
            if (LeftTask == null) slot = LeftRight.Left;
            else if (RightTask == null) slot = LeftRight.Right;
            else break; // 两管炮都忙

            var task = _taskQueue.Dequeue();
            if (slot == LeftRight.Left) LeftTask = task;
            else RightTask = task;
            if (IsKillContract(task)) Registry.Commit(task);   // 手动任务幂等(入队已登记); STAR 不登记
            StartTaskRoutine(slot, task);
        }
    }

    /// <summary>启动一个火控任务协程。用 MelonCoroutines 跑协程实现延时——
    /// 协程由 Unity 在主线程分帧驱动，yield 期间不阻塞、恢复后仍在主线程，
    /// 因此可安全访问 IL2CPP 对象。绝不能用 async/Task.Delay：其 continuation
    /// 会在线程池线程恢复，跨线程访问 IL2CPP 运行时会导致进程崩溃且无日志。
    /// 经守护层启动: 手动驱动子协程 MoveNext, 异常必定捕获(日志+槽位清理),
    /// 防协程静默死亡后 LeftTask/RightTask 残留 → 调度停摆。</summary>
    private void StartTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var handle = MelonCoroutines.Start(TaskRoutineGuard(leftRight, task));
        _runningCoroutines.Add(handle);
    }

    /// <summary>任务协程守护层: 手动驱动 RunTaskRoutine 的 MoveNext, 捕获异常打日志并清理槽位。
    /// C# 迭代器限制(含 catch 的 try 内不能 yield) → 不能 yield return 子协程, 只能手动驱动。
    /// 协程被 Stop(热重载)时本层 Dispose, 内层 finally 照常执行 → 锁不泄漏。</summary>
    private IEnumerator TaskRoutineGuard(LeftRight leftRight, ArtilleryTask task) {
        Exception? ex = null;
        var inner = RunTaskRoutine(leftRight, task);
        try {
            while (true) {
                bool moved;
                try { moved = inner.MoveNext(); }
                catch (Exception e) { ex = e; break; }
                if (!moved) break;
                yield return inner.Current;
            }
        }
        finally {
            if (ex != null) {
                MelonLogger.Error($"[FCS] 任务协程异常 T{task.targetId}({task.entityId}): {ex}");
                task.Canceled = true;
                if (!task.Fired) Registry.Release(task);
                if (leftRight == LeftRight.Left) LeftTask = null;
                else RightTask = null;
                _taskTurretRes.Remove(task);
                try { OnGunIdle?.Invoke(); } catch { }
            }
            else {
                _taskTurretRes.Remove(task);
            }
        }
    }

    /// <summary>任一炮管退膛时触发回调（WaitBackToIdle 完成后）。供雷达层刷新队列。</summary>
    public event Action? OnGunIdle;

    /// <summary>清空队列中雷达自动入队的任务, 保留玩家手动入队的任务。</summary>
    public void ClearPendingTasks() {
        var kept = _taskQueue.Where(t => t.Source == TaskSource.Manual).ToList();
        _taskQueue.Clear();
        foreach (var t in kept) _taskQueue.Enqueue(t);
    }

    /// <summary>炮管打完一发后释放槽位并尝试拉取队列里的下一个任务。</summary>
    private void ReleaseSlot(LeftRight leftRight) {
        if (leftRight == LeftRight.Left) LeftTask = null;
        else RightTask = null;
        TryDispatch();
    }

    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;

        // ===== 炮塔预约：任务一开始就在后台抢炮塔锁并装填期追方位 =====
        // 方向旋转与装填/升仰角不冲突。后台协程阻塞式抢炮塔锁（"一旦释放就立即获取"），
        // 装填期每帧追实时方位(装填要求仰角静止)；装填完成后主流程接管双机构驱动。
        // 炮塔方向必须独占到这一发打出去为止，故锁一直持有到击发完成(ReleaseOnce 归还)。
        var turret = new TurretReservation();
        _taskTurretRes[task] = turret;   // 看门狗强制释放炮塔锁用
        // 独立的 fire-and-forget 协程，必须登记以便 Dispose 时一并 Stop，
        // 否则热重载后旧 ALC 的它仍被 Unity 驱动 → 崩溃。
        _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));

        // ===== 临界区 1：采购炮弹 =====
        // 仰角/方向角由 aim(t) 公式每帧算, 游戏计算器仅作并行装饰同步(见 SyncCalculatorVisual)。
        // desk 锁只保护采购台(全局唯一硬件), 两门炮"抢占"问题随之消失。
        bool viable = true;
        bool slotReleased = false;
        yield return _deskLock.Acquire();
        try {
            task.progress = Progress.SelectingBullet;
            // 弹仓里没有目标弹种则采购（采购台也是共享硬件，放在锁内）。
            // 未解锁弹种(如 LE)采购点击无效——采购后核验弹是否真进舱, 未进则沿回退链换弹重试,
            // 避免"LE 永远买不到 → 任务失败 → 重新派发再试 LE"的死循环。
            if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                if (!gunSys.HaveEmptyShellInCylinder()) {
                    task.progress = Progress.Failed;
                    viable = false;
                }
                else {
                    for (var attempt = 0; attempt < 4; attempt++) {
                        yield return _purchaseDeck.BuyShell(task.bulletType, leftRight);
                        if (gunSys.HaveBulletInCylinder(task.bulletType)) break;   // 到货
                        var next = FallbackShell(task.bulletType);
                        if (next == task.bulletType) break;   // 链尽头, 不再换
                        MelonLogger.Msg($"[FCS] {leftRight} 弹种 {task.bulletType} 不可用(未解锁?), 回退 {next}");
                        task.bulletType = next;
                    }
                }
            }
        }
        finally {
            _deskLock.Release();
        }

        try {
            // 3b 切割点: 切手动时置 Canceled 的自动任务在此干净放弃——
            // 还没碰炮膛(未 LoadBullet), 无卡膛风险。复用 viable 分支的 abort 模式。
            if (task.Canceled) {
                task.progress = Progress.Canceled;
                turret.Canceled = true;
                ReleaseTurretOnce(turret);
                Registry.Release(task);
                ReleaseSlot(leftRight);
                slotReleased = true;
                yield break;
            }
            if (!viable) {
                // 任务不可行：取消炮塔预约并归还（后台若尚未抢到，会在抢到后自行归还），
                // 并释放炮管槽位让队列里的下一个任务能用这管炮。
                turret.Canceled = true;
                ReleaseTurretOnce(turret);
                ReleaseSlot(leftRight);
                slotReleased = true;
                yield break;
            }

            // ===== 锁外：装填（每管炮独立，最耗时段，可与另一管炮全程并行）=====
            // 膛内状态接管(2026-08-15): 每发任务都先评估膛内状态——手动半装填/任务中断/
            // 转移阵地后炮管可能停在任意中间态, 按状态分流:
            //   ReadyToFire: 弹+药已装好(CanFire) → 跳过推弹+装药, 直接复用(省整个装填周期)
            //   ShellLoaded: 弹在膛未就绪 → 弹种匹配则跳过推弹只装药; 不匹配则退壳重装
            //   Empty: 正常装填
            // 2026-08-16: 移除手动弹保护(用户)——膛内任何弹(含手动装的)都按上述状态接管,
            // 弹种匹配就复用、不匹配退壳重装, 不再放弃任务留炮管。
            task.progress = Progress.LoadingBullet;
            bool skipLoadBullet = false;   // 跳过推弹(膛内已有匹配弹)
            bool reuseReady = false;       // 整发复用(跳过推弹+装药, 直接瞄准)
            var chamberState = gunSys.AssessChamber();
            if (chamberState == GunSystem.ChamberState.ReadyToFire)
            {
                // 整发已装填完毕未发射(手动装好/上一任务遗留)。
                // 复用前提: 弹种匹配 + 膛内装药 ≥ 本次所需。
                //   装药 == 所需 → 直接复用, 仰角按所需算
                //   装药 > 所需  → 复用但仰角按【实际装药】算(装药多=射程远, 调低仰角打准,
                //                    不必退壳浪费这发——2026-08-15 用户指出)
                //   装药 < 所需  → 射程不足打不到, 退壳重装
                //   装药读取失败 → 不赌, 退壳重装
                if (gunSys.ChamberMatches(task.bulletType))
                {
                    // 先按当前距离算所需装药, 再比对
                    AdoptVelocityIfNeeded(task);
                    var d0 = task.distance;
                    var dLead = d0;
                    if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel)) {
                        var t0 = ToFTable.FlightTime(d0, TargetLeadSolver.ChargeFor(d0));
                        var f0 = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel, Time.time - task.AimStartTime + 60f, t0);
                        dLead = Mathf.Max(d0, DistKm(f0));
                    }
                    var needCharges = task.useMaxCharge || _sceneInteractor.maxCharge
                        ? 6
                        : Mathf.Min(6, TargetLeadSolver.ChargeFor(
                            dLead + (task.IsMoving ? MovingDistanceMarginKm : 0f)));
                    int chamberCharges = gunSys.ChamberCharges();
                    if (chamberCharges >= needCharges)
                    {
                        // 装药够 → 复用。仰角按实际装药定格(装药多则调低, 打准)。
                        if (chamberCharges > needCharges)
                            MelonLogger.Msg($"[FCS] {leftRight} 膛内装药 {chamberCharges}包 > 所需 {needCharges}包, 复用并按实际装药瞄准");
                        reuseReady = true;
                        task.LoadedCharge = chamberCharges;   // 实际装药, 覆盖公式值
                    }
                    else
                    {
                        MelonLogger.Msg($"[FCS] {leftRight} 膛内装药 {chamberCharges}包 < 所需 {needCharges}包, 退壳重装");
                        yield return gunSys.CleanState();   // 轮询确认弹出膛
                    }
                }
                else
                {
                    // 弹种不匹配 → 退壳重装(放弃这发已装好的弹)
                    MelonLogger.Msg($"[FCS] {leftRight} 膛内弹种 {gunSys.BulletInChamber()} ≠ 期望 {task.bulletType}, 退壳重装");
                    yield return gunSys.CleanState();   // 轮询确认弹出膛
                }
            }
            else if (chamberState == GunSystem.ChamberState.ShellLoaded)
            {
                // 弹在膛但未就绪(半装填): 弹种匹配 → 跳过推弹(避免推弹杆顶到已有弹)
                if (gunSys.ChamberMatches(task.bulletType))
                {
                    MelonLogger.Msg($"[FCS] {leftRight} 膛内已有 {gunSys.BulletInChamber()}(半装填), 跳过推弹直接装药");
                    skipLoadBullet = true;
                }
                else
                {
                    // 弹种不匹配 → 退壳重装
                    MelonLogger.Msg($"[FCS] {leftRight} 膛内残留 {gunSys.BulletInChamber()} ≠ 期望 {task.bulletType}, 退壳重装");
                    yield return gunSys.CleanState();   // 轮询确认弹出膛
                }
            }

            // ===== 推弹（除非跳过: 膛内已有匹配弹/整发复用）=====
            if (!skipLoadBullet && !reuseReady)
            {
                // 决定推弹即标 Auto(2026-08-16): 来源检测 5s 轮询可能在"推弹完成→确认弹种"
                // 之间把自动装的弹误判成用户手动。此刻膛必空(状态接管已分流), 标 Auto 安全;
                // 任务中断遗留的弹也保持 Auto(可复用), 不再被误标 Manual 堵死本管。
                gunSys.MarkAutoLoaded();
                yield return gunSys.LoadBullet(task.bulletType);
                // LoadBullet 只是点推弹杆按钮——炮弹从挂架到炮膛需要机械时间，
                // 不能立刻读 BulletInChamber()，否则会误判为装填失败。
                {
                    var chamberTimeout = 0;
                    while (gunSys.BulletInChamber() == null && chamberTimeout < 30) {
                        yield return new WaitForSeconds(0.5f);
                        chamberTimeout++;
                    }
                    if (gunSys.BulletInChamber() != task.bulletType.ToString()) {
                        MelonLogger.Error($"[FCS] {leftRight} 炮管：装填后弹种不匹配，" +
                                          $"期望 {task.bulletType}，实际 {gunSys.BulletInChamber() ?? "null"}");
                        task.progress = Progress.Failed;
                        yield break;
                    }
                    gunSys.MarkAutoLoaded();   // 自动装填成功, 来源标 Auto
                }
            }
            else if (skipLoadBullet)
            {
                // 复用膛内匹配弹(ShellLoaded 半装填): 弹是自动任务遗留的(Auto 来源) → 保持 Auto
            }

            // ===== 装药决策（装弹完成后定格）=====
            // 装弹(转弹仓+推弹)必远超雷达 5s 扫描周期 → 冷启动目标此刻速度必然已建立并被采纳,
            // 按真实提前量定格装药, 无需猜测余量。静止目标 = ChargeFor(当前距离)。
            // ReadyToFire 复用: LoadedCharge 已在状态接管段设为实际装药(≥所需), 这里不覆盖。
            int powderCount;
            if (!reuseReady)
            {
                AdoptVelocityIfNeeded(task);
                var distNowKm = task.distance;
                var distLeadMaxKm = distNowKm;
                if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel)) {
                    var tof = ToFTable.FlightTime(distNowKm, TargetLeadSolver.ChargeFor(distNowKm));
                    var far = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel, Time.time - task.AimStartTime + 60f, tof);
                    distLeadMaxKm = Mathf.Max(distNowKm, DistKm(far));
                }
                powderCount = task.useMaxCharge || _sceneInteractor.maxCharge
                    ? 6
                    : Mathf.Min(6, TargetLeadSolver.ChargeFor(
                        distLeadMaxKm + (task.IsMoving ? MovingDistanceMarginKm : 0f)));
                task.LoadedCharge = powderCount;
                task.EstimatedToF = ToFTable.FlightTime(task.distance, powderCount);   // 面板倒计时起点
                MelonLogger.Msg($"[FCS] {leftRight} 装药决策: task.max={task.useMaxCharge} btn.max={_sceneInteractor.maxCharge} dist={task.distance:F2} → {powderCount}包");
            }
            else
            {
                // 复用膛内已装弹: LoadedCharge 已在状态接管段定格(实际装药), 补射表
                powderCount = task.LoadedCharge;
                task.EstimatedToF = ToFTable.FlightTime(task.distance, task.LoadedCharge);
                MelonLogger.Msg($"[FCS] {leftRight} 装药决策(复用膛内): dist={task.distance:F2} 实际装药={task.LoadedCharge}包");
            }

            // 计算器纯装饰视觉同步: 与装填/瞄准并行(不进任务关键路径)。desk 锁短持有, 登记以便 Dispose 停掉。
            if (!reuseReady) _runningCoroutines.Add(MelonCoroutines.Start(SyncCalculatorVisual(task, powderCount)));

            // ===== 临界区 2：装药采购（定格后不足才买; 弹种装填失败不再浪费装药）=====
            // ReadyToFire 复用路径已装好药 → 跳过采购+推药。
            if (!reuseReady)
            {
                task.progress = Progress.LoadingPowder;
                yield return _deskLock.Acquire();
                try {
                    // 单次采购未必补满（偶发点击早于卡牌入槽而失败），循环购买直到够本次发射所需;
                    // 购买次数上限兜底, 采购始终无效时不至于无限循环（每次约 2.5s）。
                    // 补购失败＝任务不可行（finally 兜底释放槽位/炮塔/登记）。
                    var powderPurchaseAttempts = 0;
                    while (gunSys.RemainingCharges() < powderCount) {
                        yield return _purchaseDeck.BuyPowders();
                        if (++powderPurchaseAttempts >= 10) {
                            MelonLogger.Error(
                                $"[FCS] {leftRight} 炮管：购买装药 {powderPurchaseAttempts} 次后仍不足 " +
                                $"{powderCount}（当前 {gunSys.RemainingCharges()}），停止补购。");
                            task.progress = Progress.Failed;
                            viable = false;
                            yield break;
                        }
                    }
                }
                finally {
                    _deskLock.Release();
                }

                yield return gunSys.LoadPowder(powderCount);
                task.progress = Progress.WaitLoading;
                var loadTimeout = 0;
                while (!gunSys.CanFire()) {
                    if (task.Canceled) { task.progress = Progress.Canceled; yield break; }
                    yield return new WaitForSeconds(1f);
                    if (++loadTimeout >= 120) { // 2 分钟超时
                        MelonLogger.Error($"[FCS] {leftRight} 炮管：等待装填完成超时");
                        task.progress = Progress.Failed;
                        yield break;
                    }
                }
            }

            // ===== 锁外：连续瞄准跟踪 =====
            // 装填已完成 → 仰角可动。每帧重算 aim(t) 并驱动双机构, 直到收敛 + CanFire。
            // 移动目标走提前量(冻结快照外推); 静态退化为恒定 aim（同一循环, 无分类边界）。
            // 后台协程停止驱动方位, 主流程接管。
            turret.PostLoad = true;
            // 击发串行化门(6bf05e9 回归修复): 全局扳机 Fire() 击发所有臂杆已拉下的炮管,
            // 两管任务同时进击发段 = 双管齐射。锁由后台持有到击发完成(ReleaseTurretOnce)。
            // 仰角杆每管独立——等锁期间照样追仰角(与另一管并行), 只有方位/击发段被锁串行化。
            task.progress = Progress.Aiming;
            // 同方位齐射门: 另一管与本管目标方位差 ≤ 容差 → 放行, 两管都进击发流程,
            // 玩家拉一次扳机 = 双弹齐射(如 AP+HE 打同一目标)。不同方位仍等锁串行——
            // 座圈只能一个方向, 齐射必一发偏; 放行后两管驱动同一方位值, 不抢舵。
            while (!turret.Acquired && !SameBearingAsOther()) {
                TryComputeAimTargets(task, out _, out var elev);
                gunSys.SetElevationTarget(elev);
                yield return null;
            }
            turret.Aiming = true;   // 主流程接管方位(后台停止驱动)
            MelonLogger.Msg($"[FCS] {leftRight} 炮塔锁已持有, 进入瞄准/击发段");

            // 另一管当前目标方位与本管差 ≤ 容差 → 可齐射放行。移动目标只放行同实体:
            // 不同移动目标同方位会随时间发散 → 两管收敛循环抢舵(锁门原本防的正是抢舵)。
            bool SameBearingAsOther()
            {
                var other = leftRight == LeftRight.Left ? RightTask : LeftTask;
                if (other == null) return false;
                if ((task.IsMoving || other.IsMoving)
                    && (task.entityId is not { Length: > 0 } || task.entityId != other.entityId)) return false;
                TryComputeAimTargets(task, out var b, out _);
                TryComputeAimTargets(other, out var ob, out _);
                return Mathf.Abs(Mathf.DeltaAngle(b, ob)) <= SalvoBearingToleranceDeg;
            }
            var aimTrackStart = Time.time;
            while (true) {
                // 紧急移动/切手动: 转移阵地会重置炮管状态(弹被卸下), 瞄准永远不收敛 →
                // 置 Canceled 后在此干净退出(锁/槽位/登记由 finally 兜底释放)。
                if (task.Canceled) { task.progress = Progress.Canceled; yield break; }
                TryComputeAimTargets(task, out var bearingTarget, out var elevTarget);
                // 出装药覆盖检查: 移动目标提前点距离超过装药射程 → 退化
                if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel)) {
                    var tof = ToFTable.FlightTime(task.distance, task.LoadedCharge);
                    var leadDist = DistKm(TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel,
                        Time.time - task.AimStartTime, tof));
                    if (leadDist * 0.2f > task.LoadedCharge + 0.1f) {
                        task.progress = Progress.Failed;
                        yield break;
                    }
                }
                Turret.SetDesiredRotation(bearingTarget);
                gunSys.SetElevationTarget(elevTarget);
                // 收敛判定对"当前帧目标值"比较（移动时 aim 每帧漂移, 机构追上即收敛）
                if (gunSys.ElevationError(elevTarget) < AimToleranceDeg
                    && Turret.AngleError(bearingTarget) < AimToleranceDeg
                    && gunSys.CanFire())
                    break;
                // 兜底: 时间制(帧数在 60fps 下只有 1/60 秒/次, 帧数会误杀 32s 仰角摆动)
                if (Time.time - aimTrackStart > 240f) { task.progress = Progress.Failed; yield break; }
                yield return null;
            }

            // ===== FDC 落地顺序保护(2026-08-15 用户提出, 08-16 流程 1 保留) =====
            // FDC 弹落地(≈击杀时刻)触发暂停, 暂停在"下一次炮弹落地"解除。
            // 若击发 FDC 时仍有旧弹在飞且其落地晚于 FDC 弹 → 旧弹成为"下一次落地" →
            // FDC 触发的暂停被立即解除, FDC 白费。
            // 约束: 击发前等所有在飞弹的剩余落地时间 < 本弹 ToF(旧弹先落地, FDC 弹最后落地,
            // 且旧弹至少提前 0.1s——"追求上一发炮弹落地 ≥0.1s 后 FDC 落地", 用户 08-16 流程设定)。
            // 不做专门的卡时间对齐(用户 08-16: 不值得——收益依赖 FDC 击杀成功, 且落地顺序
            // 保护已天然保证"旧弹先落地", 间隙≈击发段耗时即可接受)。
            if (task.IsFdc && _sceneInteractor.AutoFire && !task.forceFire && !task.Canceled)
            {
                float fdcToF = ToFTable.FlightTime(task.distance, task.LoadedCharge);
                var fdcCoordDeadline = Time.time + 45f;   // 最长等 45s(旧弹最晚落地), 防死等
                while (Time.time < fdcCoordDeadline && !task.Canceled)
                {
                    // 必须用【最晚】落地(2026-08-16 修正): SoonestImpactIn 返回最早, 只要有一发
                    // 旧弹比 FDC 先落地就放行——另一发更晚落地的旧弹会在 FDC 弹落地后立即解除暂停。
                    float latestImpact = LatestImpactIn();
                    if (latestImpact <= 0f || latestImpact + 0.1f < fdcToF) break;   // 旧弹都先落地 ≥0.1s(或无在飞弹)
                    // 诊断(2026-08-16 用户: FDC 等火炮弹落地才开火)——打印等待数值定位:
                    // fdcToF 正常(10s+)时 FDC 在火炮弹剩余 ≈ fdcToF 时击发(对齐); 异常小则等火炮弹落地。
                    if (Time.time - _lastFdcWaitLog > 10f)
                    {
                        _lastFdcWaitLog = Time.time;
                        MelonLogger.Msg($"[FCS] FDC 等旧弹落地: fdcToF={fdcToF:F1}s latestImpact={latestImpact:F1}s dist={task.distance:F2} 装药={task.LoadedCharge} (对齐点=剩余 {fdcToF - 0.1f:F1}s)");
                    }
                    // 有旧弹会在 FDC 弹之后落地 → 等它落地(维持瞄准)
                    TryComputeAimTargets(task, out var fb, out var fe);
                    Turret.SetDesiredRotation(fb);
                    gunSys.SetElevationTarget(fe);
                    yield return null;
                }
                if (task.Canceled) { task.progress = Progress.Canceled; yield break; }
            }

            // ===== 冻结窗口击发协调(流程 2, 2026-08-16 用户设定) =====
            // 冻结窗口 = 暂停生效(开局倒计时未启动 / FDC 击杀 / MoveZone 卡):
            // 倒计时不走, 炮弹飞行不烧时间——双管装填白嫖(最低装药, ToF 长=窗口长)
            // → 齐射: 短 ToF 等长 ToF 先击发, 两发追求同时落地(窗口 = 长 ToF, 两发都白嫖;
            // 开局时第一发落地 = StartTimer, 晚启动=更多白嫖); 炮塔转动/异方位无法同时
            // 就绪时退化为先 Ready 先打(尽力而为)。手动强制 / 取消时跳过协调。
            bool freezeWindow = Cbt.IsPausedWindow || Cbt.Phase == CbtMonitor.CbtPhase.Opening;
            if (freezeWindow && _sceneInteractor.AutoFire && !task.forceFire && !task.Canceled)
            {
                // 等另一管也 ReadyToFire(CanFire), 或暂停即将解除(有其他在飞弹即将落地)
                var otherTask = leftRight == LeftRight.Left ? RightTask : LeftTask;
                var otherGun = leftRight == LeftRight.Left ? RightGun : LeftGun;
                // 异方位(超过齐射门 0.1°): 单座圈炮塔锁串行, 无法同时击发——不等待,
                // 各自打(先 Ready 先打)。串行落地差 = 转炮塔 + ToF 差; Paused 排序 farFirst
                // 让长 ToF 管先派先装填先击发 → 长弹先落地, 落地差 = |ToF差 − 转炮塔|(抵消)。
                bool sameBearing = otherTask != null && otherTask != task
                    && Mathf.Abs(Mathf.DeltaAngle(task.angel, otherTask.angel)) <= SalvoBearingToleranceDeg;
                var pauseCoordDeadline = Time.time + 90f;   // 冻结窗口内等待无成本
                while (sameBearing && freezeWindow && Time.time < pauseCoordDeadline)
                {
                    // 同方位: 等另一管真正 Ready(CanFire + 瞄准对齐)——只等 progress>=Aiming
                    // 会提前放行(另一管刚进瞄准未收敛) → 先收敛先击发 → 串行落地
                    // (16:03 实测: ToF 相同却差 15s, 火炮弹先落地解除窗口, FDC 走秒=浪费暂停)。
                    bool otherAligned = otherTask != null && otherTask != task
                        && otherTask.progress >= Progress.Aiming
                        && otherGun.CanFire()
                        && otherGun.ElevationError(TargetLeadSolver.Elevation(otherTask.distance, otherTask.LoadedCharge)) < AimToleranceDeg
                        && Turret.AngleError(otherTask.angel) < AimToleranceDeg;
                    if (otherAligned) break;
                    if (task.Canceled) break;
                    // 维持瞄准(机构继续追 aim, 不丢收敛)
                    TryComputeAimTargets(task, out var pb, out var pe);
                    Turret.SetDesiredRotation(pb);
                    gunSys.SetElevationTarget(pe);
                    yield return null;
                }
                // 双管都就绪: 比较 ToF, 长的先击发(短的自然排队等炮塔锁/齐射门)
                if (sameBearing && freezeWindow && !task.Canceled)
                {
                    var other = leftRight == LeftRight.Left ? RightTask : LeftTask;
                    float myToF = ToFTable.FlightTime(task.distance, task.LoadedCharge);
                    float otherToF = other != null && other != task
                        ? ToFTable.FlightTime(other.distance, other.LoadedCharge)
                        : 0f;
                    // 本管 ToF 更短且另一管就绪 → 等长 ToF 那管先打(两发同时落地)
                    if (myToF < otherToF && other != null && other != task)
                    {
                        var waitFor = Time.time + (otherToF - myToF);   // 最多等到长弹落地前
                        while (freezeWindow && Time.time < waitFor && !task.Canceled)
                        {
                            TryComputeAimTargets(task, out var pb, out var pe);
                            Turret.SetDesiredRotation(pb);
                            gunSys.SetElevationTarget(pe);
                            yield return null;
                        }
                    }
                }
                else if (!sameBearing && freezeWindow && !task.Canceled
                    && otherTask != null && otherTask != task && otherTask.Fired)
                {
                    // 异方位串行·后打者落地对齐(2026-08-16 用户公式):
                    // 落地差 = ToF差 − 转炮塔 − 等待 ≤ 0 —— 本管(后打者)击发前等先打者
                    // 弹剩余落地 ≤ 本管 ToF → 本管弹落地 ≥ 先打者弹(先打者先落地/同时)。
                    // farFirst 保证长 ToF 管先派先打 → 长弹先落地 → 窗口 = 长 ToF;
                    // 短管(后打者)转炮塔+对齐等待后击发 → 落地追平(差 → 0)。
                    float myToF = ToFTable.FlightTime(task.distance, task.LoadedCharge);
                    while (freezeWindow && !task.Canceled && Time.time < pauseCoordDeadline)
                    {
                        float otherRemain = otherTask.EstimatedToF - (Time.time - otherTask.FiredAt);
                        if (otherRemain <= 0f || otherRemain <= myToF) break;   // 先打者弹将先落地/已落地
                        // 维持瞄准继续等(先打者弹还有较久才落地)
                        TryComputeAimTargets(task, out var cb, out var ce);
                        Turret.SetDesiredRotation(cb);
                        gunSys.SetElevationTarget(ce);
                        yield return null;
                    }
                    if (task.Canceled) { task.progress = Progress.Canceled; yield break; }
                }
            }

            // ===== 击发（turret 锁仍由后台持有, 直接确认+击发）=====
            task.progress = Progress.WaitingForFire;
            try {
                // 臂杆按下与 5 个确认并行(开关是纯 0/1 flag, 一口气点完, 在臂杆保持期内完成)。
                // 臂杆自动拉下 = 该炮管就绪(此刻方位/仰角已收敛, 不会在错误方向角臂下)
                yield return TriggerConsole.ArmWithFastConfirm(leftRight);
                if (_sceneInteractor.AutoFire || task.forceFire) {
                    // 确认序列 ~3-4s 期间 aim 漂移(机构追着走); 复检对齐后再打(5s 内追上, 否则尽力打)
                    var recheckStart = Time.time;
                    while (!AlignOk(task, gunSys, AimToleranceDeg) && Time.time - recheckStart < 5f) yield return null;
                    if (turret.Acquired) {
                        // 只有持锁者拉扳机——全局扳机击发所有已武装炮管, 同方位齐射由它一并带出
                        TriggerConsole.Fire();
                        yield return gunSys.WaitFire();
                    } else {
                        // 同方位非持锁者: 已武装, 等持锁者击发带自己(直接 Fire 会双重 AddEnergy);
                        // 持锁者若瞄准失败放弃 → 自己拿到锁后兜底击发, 防 AutoFire 下永久挂起
                        while (!gunSys.IsPendingReload() && !turret.Acquired) {
                            TryComputeAimTargets(task, out var b, out var e);
                            Turret.SetDesiredRotation(b);
                            gunSys.SetElevationTarget(e);
                            yield return null;
                        }
                        if (!gunSys.IsPendingReload() && turret.Acquired) {
                            TriggerConsole.Fire();
                            yield return gunSys.WaitFire();
                        }
                    }
                } else {
                    // AutoFire 关: 持续跟踪 aim(t) 直到玩家扳机——否则炮管停在旧点, 移动目标必偏。
                    // 静态任务此循环目标值恒定, 退化为无操作等待。
                    while (!gunSys.IsPendingReload()) {
                        TryComputeAimTargets(task, out var b, out var e);
                        Turret.SetDesiredRotation(b);
                        gunSys.SetElevationTarget(e);
                        yield return null;
                    }
                }
                // 爆区覆盖: 落点 = 静态任务目标/集群质心, 移动任务提前点; 半径 = 该弹毁伤半径
                Vector3 impactPos = task.position;
                if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel))
                {
                    var tof = ToFTable.FlightTime(task.distance, task.LoadedCharge);
                    impactPos = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel,
                        Time.time - task.AimStartTime, tof);
                }
                float blastKm = task.BlastRadiusKm > 0f ? task.BlastRadiusKm : ShellData.BlastRadiusKm(task.bulletType);
                task.Fired = true;
                task.FiredAt = Time.time;
                Registry.MarkFiredBlast(task, impactPos, blastKm);
                // 集群成员派发时已登记(Commit), 击发无需再登记
                InFlight.Add(task);   // 面板倒计时从这里开始, 归零(估计落地)时移除
            }
            finally {
                ReleaseTurretOnce(turret);
            }

            // ===== 锁外：回位（仰角回 0，每管炮独立，最耗时段之一）=====
            task.progress = Progress.BackToIdle;
            yield return gunSys.WaitBackToIdle();
            gunSys.MarkChamberEmpty();   // 击发+退壳完成, 膛空 → 来源复位
            task.progress = Progress.Finished;
            _sceneInteractor.TaskFinished(task);
            ReleaseSlot(leftRight);
            slotReleased = true;
        }
        finally {
            // 未击发的任务结束时解除登记(击发过的留给击杀确认/窗口到期)。
            // Canceled/Failed 分支幂等。
            if (!task.Fired) Registry.Release(task);
            // 防泄漏：协程崩了或 yield break 没走到 ReleaseSlot 时兜底释放
            if (!slotReleased) {
                if (leftRight == LeftRight.Left) LeftTask = null;
                else RightTask = null;
                TryDispatch();
            }
            // 确保炮塔锁归还（已归还的 ReleaseTurretOnce 是幂等的）
            ReleaseTurretOnce(turret);
            _taskTurretRes.Remove(task);   // 看门狗映射清理
            // 通知雷达：有一门炮退膛完毕，可以刷新队列了
            try { OnGunIdle?.Invoke(); } catch { }
        }
    }

    /// <summary>
    /// 炮塔预约状态。四个标志全在主线程协作式调度下读写，无真正并发。
    /// 生命周期：后台 <see cref="ReserveTurretAndRotate"/> 抢锁并装填期追方位；
    /// 主流程装填完成后置 PostLoad 接管驱动；击发后 / 任务放弃时 ReleaseTurretOnce 归还。
    /// Released 保证恰好归还一次。
    /// </summary>
    private sealed class TurretReservation {
        public bool Acquired;   // 已拿到炮塔锁
        public bool PostLoad;   // 装填完成(语义保留: 主流程接管时机)
        public bool Aiming;     // 主流程已拿到锁接管方位驱动(后台停止)
        public bool Canceled;   // 主流程已放弃本次预约
        public bool Released;   // 锁已归还（防重复 Release）
    }

    /// <summary>
    /// 后台预约炮塔。阻塞式抢锁实现"一旦炮塔释放就立即获取"。
    /// 抢到后若发现已被取消则立即归还；否则装填期每帧追实时方位
    /// （装填要求仰角静止, 只转方位）；装填完成后主流程接棒(PostLoad), 此处只持锁。
    /// </summary>
    private IEnumerator ReserveTurretAndRotate(ArtilleryTask task, TurretReservation res) {
        yield return _turretLock.Acquire();
        res.Acquired = true;
        // 主流程可能在抢到锁前已结束(取消/同方位齐射非持锁者提前完成)——拿到锁立即归还, 防锁泄漏。
        if (res.Canceled || res.Released) {
            _turretLock.Release();
            yield break;
        }
        while (!res.Released) {
            // 主流程未接管前持续追方位(装填期+等锁期); 拿到锁接管后(Aiming)停止。
            if (!res.Aiming && TryGetMovingBearing(task, out var bearing))
                Turret.SetDesiredRotation(bearing);
            yield return null;
        }
    }

    /// <summary>归还炮塔锁。Released 标记"主流程已结束占用"(无论是否持锁), 后台抢到锁时据此自归。
    /// 只在确实持锁(Acquired)时立即释放; 未持锁的提前结束(同方位齐射非持锁者)由后台拿到锁后归还。</summary>
    private void ReleaseTurretOnce(TurretReservation res) {
        if (res.Released) return;
        res.Released = true;
        if (res.Acquired) _turretLock.Release();
    }

    // ===== 连续瞄准几何与 aim 辅助（统一路径共用）=====

    /// <summary>缓存地图面 + turret 引用（一次, TryBind 时调用）。避免每帧 GameObject.Find。
    /// 优先用真实炮塔 TurretLocation(转移阵地时游戏移动它, 实时位置即新基准);
    /// 找不到时回退 Player Turret Piece(可拖动标记)。</summary>
    private void CacheAimGeometry() {
        _mapSurface = GameObject.Find("Draggable Surface")?.transform;
        _turretXf = MapTable.TurretLocation ?? MapTable.Turret;
    }

    /// <summary>几何引用刷新(2026-08-15): 紧急移动转移阵地可能销毁重建 Piece/地图面,
    /// 缓存变 fake-null → Bearing/DistKm 返回 0 → 打空。仅紧急移动完成后显式调用一次,
    /// 不做每帧 Find——GameObject.Find 是 O(n) 场景搜索, 每帧执行是性能杀手。</summary>
    private void RefreshAimGeometry() {
        _mapSurface = GameObject.Find("Draggable Surface")?.transform;
        _turretXf = MapTable.TurretLocation ?? MapTable.Turret;
    }

    /// <summary>世界坐标 → 距离 km（与 TacticalRadar.CalcDistance 同换算）</summary>
    private float DistKm(Vector3 worldPos) {
        if (_mapSurface == null || _turretXf == null) return 0f;
        // 统一转地图面局部坐标: Piece 父=地图面(localPosition 即局部), TurretLocation 父=MapRoot
        // (须 InverseTransformPoint 世界坐标)。用世界坐标转换对两者都正确(2026-08-15)。
        var turretLocal = _mapSurface.InverseTransformPoint(_turretXf.position);
        var target = _mapSurface.InverseTransformPoint(worldPos) - turretLocal;
        return target.magnitude * 3.8164f;
    }

    /// <summary>世界坐标 → 方位角（与 TacticalRadar.CalcAngle 同逻辑）</summary>
    private float Bearing(Vector3 worldPos) {
        if (_mapSurface == null || _turretXf == null) return 0f;
        var turretLocal = _mapSurface.InverseTransformPoint(_turretXf.position);
        var target = _mapSurface.InverseTransformPoint(worldPos) - turretLocal;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        return angle < 0 ? angle + 360f : angle;
    }

    /// <summary>当前 aim 目标值。移动目标从冻结快照外推; 静态用任务创建时定格的固定值
    /// (angel/distance)——大部分目标是静态, 固定值零每帧开销(2026-08-15 性能回归)。
    /// 固定值由雷达按真实炮塔 TurretLocation 基准算好, 紧急移动转移后新派发的任务
    /// 自动用新基准, 不需要每帧重算。返回是否算移动。</summary>
    private bool TryComputeAimTargets(ArtilleryTask task, out float bearing, out float elev) {
        AdoptVelocityIfNeeded(task);
        bearing = task.angel;
        elev = TargetLeadSolver.Elevation(task.distance, task.LoadedCharge);
        if (!task.IsMoving || !TargetLeadSolver.IsMoving(task.AimVel)) return false;
        var tof = ToFTable.FlightTime(task.distance, task.LoadedCharge);
        var aim = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel, Time.time - task.AimStartTime, tof);
        bearing = Bearing(aim);
        elev = TargetLeadSolver.Elevation(DistKm(aim), task.LoadedCharge);
        return true;
    }

    /// <summary>
    /// 快照采纳(装填期与瞄准期调用):
    /// 1. 冷启动(一次性): 目标刚出现无速度, 从雷达最新扫描采纳位置+速度, 重新冻结快照。
    /// 2. 变速/停车(按需): 恒定匀速假设对"可被逼停"的目标失效(实测列车 0.008→0——轨道被毁停车),
    ///    雷达估速明显变化(>2m/s)时重冻结快照; 速度跌破阈值 → 退化为静态瞄准当前位置, 停止追幽灵提前点。
    /// 仍按冻结公式外推, 不连续跟踪——只在速度实质变化时重置。
    /// </summary>
    private void AdoptVelocityIfNeeded(ArtilleryTask task) {
        if (EntityLocator == null) return;
        // 跟踪对象: 集群任务看领队(车列编队同步动停), 单点任务就是自身
        var trackId = task.TrackEntityId is { Length: > 0 } ? task.TrackEntityId : task.entityId;
        if (task.VelocityUnknown) {
            if (trackId is { Length: > 0 } && EntityLocator.TryGetMotion(trackId, out var pos, out var vel)) {
                task.AimP0 = pos;
                task.AimVel = vel;
                task.AimStartTime = Time.time;
                task.VelocityUnknown = false;
                task.IsMoving = TargetLeadSolver.IsMoving(vel);
                MelonLogger.Msg($"[FCS] 已采纳 {trackId} 速度 {vel.magnitude * 3.8164f:F3}km/s, 快照重置");
            }
            return;
        }
        if (task.IsMoving && trackId is { Length: > 0 }
            && EntityLocator.TryGetMotion(trackId, out var livePos, out var liveVel)
            && (liveVel - task.AimVel).magnitude * ShellData.KmPerWorldUnit > 0.002f)
        {
            task.AimP0 = livePos;
            task.AimVel = liveVel;
            task.AimStartTime = Time.time;
            task.IsMoving = TargetLeadSolver.IsMoving(liveVel);
            if (!task.IsMoving) {
                // 停车 → 静态瞄准点: 集群任务 = 领队当前位置 + 集群偏移(保持车列中点, 落点不跳车头);
                // 单点任务 = 目标当前位置(不再追幽灵提前点)
                task.position = task.TrackEntityId is { Length: > 0 } && task.ClusterOffset.sqrMagnitude > 0f
                    ? livePos + task.ClusterOffset
                    : livePos;
                task.angel = Bearing(task.position);
                task.distance = DistKm(task.position);
            }
            MelonLogger.Msg($"[FCS] 变速采纳 {trackId}: v={liveVel.magnitude * 3.8164f:F3}km/s, 快照重置{(task.IsMoving ? "" : " (停车→静态, 落点改当前位置)")}");
        }
    }

    /// <summary>开火前对齐复检: 双机构对当前 aim(t) 都在容差内。</summary>
    private bool AlignOk(ArtilleryTask task, GunSystem gunSys, float tol) {
        TryComputeAimTargets(task, out var bearing, out var elev);
        return gunSys.ElevationError(elev) < tol && Turret.AngleError(bearing) < tol;
    }

    /// <summary>移动目标当前方位（提前点方向, 冻结快照外推）。非移动返回 false。</summary>
    private bool TryGetMovingBearing(ArtilleryTask task, out float bearing) {
        AdoptVelocityIfNeeded(task);   // 装填期采纳: 炮塔尽早转向提前方位, 避免装填完再大角度追
        bearing = task.angel;
        if (!task.IsMoving || !TargetLeadSolver.IsMoving(task.AimVel)) return false;
        var tof = ToFTable.FlightTime(task.distance, task.LoadedCharge);
        bearing = Bearing(TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel,
            Time.time - task.AimStartTime, tof));
        return true;
    }

    /// <summary>
    /// 集群收益分析: 对静态软目标 T, 求 HE/HCHE 最大可覆盖集群(MEC 圆心落点, 友军禁区)。
    /// 有集群(≥2) → 返回集群任务(位置提交, 打质心, 注册表按毁伤半径覆盖); 无 → null(调用方走单点)。
    /// 选择流程不动——只在"本应打 HE"的软目标上做升级。
    /// </summary>
    public ArtilleryTask? TryBuildClusterTask(TacticalDecider.TargetInfo ti, int targetId)
    {
        if (ti.IsArmored || ti.IsUnderground) return null;   // 只软目标(装甲/地下不走集群)
        if (EntityLocator == null) return null;

        // 候选 = 未处理软目标世界坐标(含已派发的覆盖区过滤)。
        // 移动集群(列车/车队): 编队刚体——成员与 T 同向同速才可同簇(集群几何随动不变),
        // 快照位置成簇, 任务带 IsMoving 快照, 现有提前量路径把整簇带到命中点。
        var candidates = new List<Vector3>();
        foreach (var o in EntityLocator.AliveHostiles)
        {
            if (o.EntityId == ti.EntityId) continue;
            if (o.IsArmored || o.IsUnderground) continue;
            if (o.IsMoving != ti.IsMoving) continue;
            if (o.IsMoving && (o.Velocity - ti.Velocity).magnitude * ShellData.KmPerWorldUnit > 0.002f) continue;
            if (Registry.IsHandled(o.EntityId)) continue;
            if (Registry.IsHandledNear(o.WorldPos, 0f)) continue;
            candidates.Add(o.WorldPos);
        }
        var friendlies = EntityLocator.AllyPositions;

        var he = ClusterSolver.Best(ti.WorldPos, candidates,
            ShellData.BlastRadiusKm(BulletType.HE), friendlies, ShellData.FriendlySafeRadiusKm(BulletType.HE));
        var hche = ClusterSolver.Best(ti.WorldPos, candidates,
            ShellData.BlastRadiusKm(BulletType.HCHE), friendlies, ShellData.FriendlySafeRadiusKm(BulletType.HCHE));

        // 集群门槛(用户 2026-08-13 定): HE ≥2、HCHE ≥4——集群打得勤快比省点更重要。
        // DRIL 基准纯成本是 HE 4/HCHE 6, 时间补偿降 1 是 3/5——再放宽到 2/4 图个热闹。
        bool heValid = he.HasValue && he.Value.Count >= 2;
        bool hcheValid = hche.HasValue && hche.Value.Count >= 4;
        // CBT 基金纪律(§3.6): HCHE(18 点) 仅扣款后仍 ≥ 基金线才用; 跌破基金线降级 HE。
        if (Cbt.IsCbtMode && Cbt.FundMargin < ShellData.Cost(BulletType.HCHE))
            hcheValid = false;
        // 性价比选弹: 每申请点覆盖目标数 = 覆盖数/成本(HE 10 点, HCHE 18 点)。
        // 交叉相乘免浮点: hche.Count×10 > he.Count×18 → HCHE 每点覆盖更多 → 升级。
        // 例: HE 3 vs HCHE 5 → 3×18=54 > 5×10=50 → HE 更划算(保持); HE 2 vs HCHE 5 → 2×18<5×10 → HCHE。
        bool hcheWorthIt = hcheValid && (!heValid
            || hche.Value.Count * ShellData.Cost(BulletType.HE) > he.Value.Count * ShellData.Cost(BulletType.HCHE));

        BulletType shell;
        Vector3 impact;
        int coverCount;
        if (heValid && !hcheWorthIt) { shell = BulletType.HE; impact = he.Value.Impact; coverCount = he.Value.Count; }
        else if (hcheValid) { shell = BulletType.HCHE; impact = hche.Value.Impact; coverCount = hche.Value.Count; }
        else return null;

        // 实测毁伤半径: 落点 1km 内所有存活目标距落点的径向距离 + 覆盖数。
        // 配合 Reconcile 击杀日志读出真实杀伤半径(维基 0.27/0.63 疑似偏大——覆盖成员没死)。
        // 探针: MEC 半径(he/hche) + 覆盖目标 WorldPos 原始坐标——对照游戏 UI 定位 20% 距离偏差。完成后移除。
        var nearby = EntityLocator.AliveHostiles
            .Where(h => (h.WorldPos - impact).magnitude * ShellData.KmPerWorldUnit < 1.0f)
            .Select(h => $"{h.EntityId}@{(h.WorldPos - impact).magnitude * ShellData.KmPerWorldUnit:F2}km");
        var candPos = candidates
            .Select(p => $"({p.x:F3},{p.y:F3},{p.z:F3})");
        MelonLogger.Msg($"[FCS] 集群 {ti.EntityId}: {shell} 落点{DistKm(impact):F2}km MECr={((shell == BulletType.HE ? he : hche).Value.RadiusKm):F2}km 覆盖{coverCount}个{(ti.IsMoving ? " [移动]" : "")} | 1km内: {string.Join(" ", nearby)} | 候选:{string.Join(" ", candPos)}");

        // 移动集群覆盖成员: 击发时按 entityId 登记——爆区几何以落点为中心, 车列在落点后方,
        // 在飞期间几何屏蔽拦不住, 需按实体屏蔽(死亡由 Reconcile 释放)。
        List<string>? members = null;
        if (ti.IsMoving)
        {
            float blastKm = ShellData.BlastRadiusKm(shell);
            members = new List<string>();
            foreach (var o in EntityLocator.AliveHostiles)
            {
                if (o.IsArmored || o.IsUnderground || !o.IsMoving) continue;
                if ((o.Velocity - ti.Velocity).magnitude * ShellData.KmPerWorldUnit > 0.002f) continue;
                if ((o.WorldPos - impact).magnitude * ShellData.KmPerWorldUnit <= blastKm)
                    members.Add(o.EntityId);
            }
        }

        return new ArtilleryTask
        {
            targetId = targetId,
            entityId = "",                                  // 位置提交(集群), 注册表按毁伤半径覆盖
            angel = Bearing(impact),
            distance = DistKm(impact),
            position = impact,
            bulletType = shell,
            useMaxCharge = Cbt.ShouldUseMaxCharge(),        // CBT 双轨装药(§3.2): 吃紧期满装抢节奏
            Source = TaskSource.Auto,
            BlastRadiusKm = ShellData.BlastRadiusKm(shell),
            // 移动集群: 冻结快照带整簇(装填期采纳无必要——速度已知), 提前量路径击发前重算提前点。
            // TrackEntityId=领队: 车列剧情停车/变速时二次采纳(AdoptVelocityIfNeeded)跟踪对象。
            // ClusterOffset=落点相对领队: 停车采纳时落点跟领队平移, 保持车列中点。
            IsMoving = ti.IsMoving,
            AimP0 = impact,
            AimVel = ti.Velocity,
            AimStartTime = Time.time,
            VelocityUnknown = false,
            ClusterMembers = members,
            TrackEntityId = ti.EntityId,
            ClusterOffset = impact - ti.WorldPos,
        };
    }

    /// <summary>纯装饰: 装填期并行驱动一次游戏计算器（静态=正确数据, 移动=大致数据）。
    /// 不回传仰角, 不参与瞄准。desk 锁短持有, 与采购/另一门炮互斥。
    /// 守护: 计算器卡死(游戏 UI 异常)会永久持有 deskLock → 所有任务卡 LoadingPowder,
    /// 每步用手动驱动+8s 竞速超时, 超时放弃并释放锁(装饰失败不影响任务)。</summary>
    private IEnumerator SyncCalculatorVisual(ArtilleryTask task, int powderCount) {
        yield return _deskLock.Acquire();
        try {
            yield return RaceTimeout(BallisticCalculator.SetDistance(task.distance), 8f, "SetDistance");
            yield return RaceTimeout(BallisticCalculator.SetDirection(task.angel), 8f, "SetDirection");
            yield return RaceTimeout(BallisticCalculator.SetCharge(powderCount), 8f, "SetCharge");
            yield return RaceTimeout(BallisticCalculator.SetShellType(task.bulletType), 8f, "SetShellType");
            yield return RaceTimeout(BallisticCalculator.Calculate(), 8f, "Calculate");
        } finally {
            _deskLock.Release();
        }
    }

    /// <summary>手动驱动子协程 + 竞速超时: 子协程卡死(永不完成)时超时放弃, 不让调用方永久等待。
    /// 与 TaskRoutineGuard 同理——yield return 子协程若内部卡死, 外层检查永远不会执行。</summary>
    private IEnumerator RaceTimeout(IEnumerator inner, float timeout, string what) {
        float deadline = Time.time + timeout;
        try {
            while (Time.time < deadline) {
                bool moved;
                try { moved = inner.MoveNext(); }
                catch (Exception ex) { MelonLogger.Warning($"[FCS] 装饰协程 {what} 异常: {ex.GetType().Name}: {ex.Message}"); yield break; }
                if (!moved) yield break;
                yield return inner.Current;
            }
            MelonLogger.Warning($"[FCS] 装饰协程 {what} 超时放弃");
        }
        finally {
            try { (inner as System.IDisposable)?.Dispose(); } catch { }
        }
    }
}
