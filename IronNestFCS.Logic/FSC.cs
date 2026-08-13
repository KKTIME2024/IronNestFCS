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

    public (bool hasTimer, string keys) PollRunningTimers()
    {
        try
        {
            var fm = GameObject.Find("Fire Mission Root")?.GetComponent<FireMission>();
            if (fm == null) return (false, "no FireMission");

            var timersProp = fm.GetType().GetProperty("RunningTimers",
                BindingFlags.Public | BindingFlags.Instance);
            if (timersProp == null) return (false, "no RunningTimers prop");

            var timers = timersProp.GetValue(fm);
            if (timers == null) return (false, "RunningTimers == null");

            // 读 Count 属性
            var countProp = timers.GetType().GetProperty("Count",
                BindingFlags.Public | BindingFlags.Instance);
            if (countProp == null) return (false, "no Count prop");

            var count = (int)countProp.GetValue(timers);
            if (count == 0) return (false, "0 timers");

            // 读 Keys，拼接键名
            var keysProp = timers.GetType().GetProperty("Keys",
                BindingFlags.Public | BindingFlags.Instance);
            if (keysProp == null) return (true, $"count={count}, no Keys prop");

            var keys = keysProp.GetValue(timers);
            if (keys is not IEnumerable keyEnum) return (true, $"count={count}, keys not IEnumerable");

            var keyList = new List<string>();
            foreach (var k in keyEnum) keyList.Add(k.ToString() ?? "?");
            return (true, $"count={count}, keys=[{string.Join(", ", keyList)}]");
        }
        catch (Exception ex)
        {
            return (false, $"error: {ex.Message}");
        }
    }

    public void Update() {
        _sceneInteractor.Update();
        // 面板倒计时归零(=射表估计落地)的任务移出在飞列表
        if (InFlight.Count > 0)
            InFlight.RemoveAll(t => Time.time - t.FiredAt >= t.EstimatedToF);
        // Piece 自动归位: 忘了放/紧急移动后 5s 内自愈(手动/自动都生效)
        if (Time.time - _lastPieceSyncTime > 5f) {
            _lastPieceSyncTime = Time.time;
            SyncTurretPiece();
        }
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
    /// </summary>
    private IEnumerator ReplenishPowderLoop() {
        while (true) {
            yield return new WaitForSeconds(PowderCheckInterval);
            // 取两管炮装药的最小值:任一管低于阈值就补
            var charges = Math.Min(LeftGun.RemainingCharges(), RightGun.RemainingCharges());
            if (charges >= PowderReplenishThreshold) continue;
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

    /// <summary>
    /// 启动一个火控任务协程。用 MelonCoroutines 跑协程实现延时——
    /// 协程由 Unity 在主线程分帧驱动，yield 期间不阻塞、恢复后仍在主线程，
    /// 因此可安全访问 IL2CPP 对象。绝不能用 async/Task.Delay：其 continuation
    /// 会在线程池线程恢复，跨线程访问 IL2CPP 运行时会导致进程崩溃且无日志。
    /// </summary>
    private void StartTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var handle = MelonCoroutines.Start(RunTaskRoutine(leftRight, task));
        _runningCoroutines.Add(handle);
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
            task.progress = Progress.LoadingBullet;
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
            }

            // ===== 装药决策（装弹完成后定格）=====
            // 装弹(转弹仓+推弹)必远超雷达 5s 扫描周期 → 冷启动目标此刻速度必然已建立并被采纳,
            // 按真实提前量定格装药, 无需猜测余量。静止目标 = ChargeFor(当前距离)。
            AdoptVelocityIfNeeded(task);
            var distNowKm = task.distance;
            var distLeadMaxKm = distNowKm;
            if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel)) {
                var tof = ToFTable.FlightTime(distNowKm, TargetLeadSolver.ChargeFor(distNowKm));
                var far = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel, Time.time - task.AimStartTime + 60f, tof);
                distLeadMaxKm = Mathf.Max(distNowKm, DistKm(far));
            }
            var powderCount = task.useMaxCharge || _sceneInteractor.maxCharge
                ? 6
                : Mathf.Min(6, TargetLeadSolver.ChargeFor(
                    distLeadMaxKm + (task.IsMoving ? MovingDistanceMarginKm : 0f)));
            task.LoadedCharge = powderCount;
            task.EstimatedToF = ToFTable.FlightTime(task.distance, powderCount);   // 面板倒计时起点
            MelonLogger.Msg($"[FCS] {leftRight} 装药决策: task.max={task.useMaxCharge} btn.max={_sceneInteractor.maxCharge} dist={task.distance:F2} → {powderCount}包");

            // 计算器纯装饰视觉同步: 与装填/瞄准并行(不进任务关键路径)。desk 锁短持有, 登记以便 Dispose 停掉。
            _runningCoroutines.Add(MelonCoroutines.Start(SyncCalculatorVisual(task, powderCount)));

            // ===== 临界区 2：装药采购（定格后不足才买; 弹种装填失败不再浪费装药）=====
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
                yield return new WaitForSeconds(1f);
                if (++loadTimeout >= 120) { // 2 分钟超时
                    MelonLogger.Error($"[FCS] {leftRight} 炮管：等待装填完成超时");
                    task.progress = Progress.Failed;
                    yield break;
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

    /// <summary>缓存地图面 + turret 引用（一次, TryBind 时调用）。避免每帧 GameObject.Find。</summary>
    private void CacheAimGeometry() {
        _mapSurface = GameObject.Find("Draggable Surface")?.transform;
        _turretXf = MapTable.Turret;
    }

    /// <summary>世界坐标 → 距离 km（与 TacticalRadar.CalcDistance 同换算）</summary>
    private float DistKm(Vector3 worldPos) {
        if (_mapSurface == null || _turretXf == null) return 0f;
        var target = _mapSurface.InverseTransformPoint(worldPos) - _turretXf.localPosition;
        return target.magnitude * 3.8164f;
    }

    /// <summary>世界坐标 → 方位角（与 TacticalRadar.CalcAngle 同逻辑）</summary>
    private float Bearing(Vector3 worldPos) {
        if (_mapSurface == null || _turretXf == null) return 0f;
        var target = _mapSurface.InverseTransformPoint(worldPos) - _turretXf.localPosition;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        return angle < 0 ? angle + 360f : angle;
    }

    /// <summary>当前 aim 目标值。移动目标从冻结快照外推; 静态回退固定值。返回是否算移动。</summary>
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

        // DRIL 基准(3 点/个) + 时间补偿: 纯成本门槛 = ceil(成本/3)(HE 4/HCHE 6), 但集群一发省
        // 多发装填+飞行(每发 ~60-90s, CBT 竞速时间即点数)——门槛降 1: HE(10)→≥3(贵1点省2发时间)、
        // HCHE(18)→≥5(贵3点省4发时间)。低于门槛(2 个以内)走单点 DRIL。
        bool heValid = he.HasValue && he.Value.Count >= 3;
        bool hcheValid = hche.HasValue && hche.Value.Count >= 5;
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
            useMaxCharge = false,
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
    /// 不回传仰角, 不参与瞄准。desk 锁短持有, 与采购/另一门炮互斥。</summary>
    private IEnumerator SyncCalculatorVisual(ArtilleryTask task, int powderCount) {
        yield return _deskLock.Acquire();
        try {
            yield return BallisticCalculator.SetDistance(task.distance);
            yield return BallisticCalculator.SetDirection(task.angel);
            yield return BallisticCalculator.SetCharge(powderCount);
            yield return BallisticCalculator.SetShellType(task.bulletType);
            yield return BallisticCalculator.Calculate();
        } finally {
            _deskLock.Release();
        }
    }
}
