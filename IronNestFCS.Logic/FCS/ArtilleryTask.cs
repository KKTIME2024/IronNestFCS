using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public enum Progress {
    Pending,
    Calculating,
    SelectingBullet,
    LoadingBullet,
    LoadingPowder,
    WaitLoading,
    Aiming,
    WaitingForFire,
    BackToIdle,
    Finished,
    Failed,
    Canceled,
}

public class ArtilleryTask {
    public int targetId;
    public string entityId = "";   // MapEntity key for dedup
    public float angel;
    public float distance;
    public Vector3 position;
    public BulletType bulletType;
    public Progress progress;
    /// <summary>为 true 时强制满装药量(6包)，覆盖用户全局 maxCharge 设置</summary>
    public bool useMaxCharge;
    /// <summary>切手动后置位:已开始装填的任务必须发射出去(原子化),击发段无视 AutoFire 自动开火</summary>
    public bool forceFire;
    /// <summary>任务来源: 雷达自动 or 玩家手动(手动任务不被自动清队列清掉)</summary>
    public TaskSource Source = TaskSource.Auto;
    /// <summary>切手动时置位: 未开始装填的自动任务干净放弃, 不碰炮膛</summary>
    public bool Canceled;
    /// <summary>已击发(Registry 飞行窗口计时依据; 未击发的任务结束时 Release 登记)</summary>
    public bool Fired;
    /// <summary>解算后置位: 射表估计飞行时间(秒)。面板倒计时依据, 归零=估计落地。</summary>
    public float EstimatedToF;
    /// <summary>开火瞬间 Time.time, 面板倒计时起点。</summary>
    public float FiredAt;
    /// <summary>该任务是否按移动目标处理（创建时由雷达 IsMoving 一次判定；手动任务一律 false）</summary>
    public bool IsMoving;
    /// <summary>创建时目标速度未建立(刚出现/热重载): 装填期从雷达采纳后置 false, 快照重置</summary>
    public bool VelocityUnknown;
    /// <summary>装填时定格的装药数（覆盖全程预测最远距离, 仰角重算用它）</summary>
    public int LoadedCharge;
    /// <summary>移动目标冻结快照: 创建时位置(世界单位) + 速度(世界单位/s) + 参考时刻。
    /// aim(t) = AimP0 + AimVel×(t − AimStartTime + ToF), 匀速假设下闭合式自洽。</summary>
    public Vector3 AimP0;
    public Vector3 AimVel;
    public float AimStartTime;
    /// <summary>该弹毁伤半径(km), 集群/爆区覆盖用。0 = 无爆区(未设置)。</summary>
    public float BlastRadiusKm;
    /// <summary>移动集群覆盖成员(entityId): 击发时按实体登记——爆区几何以落点为中心,
    /// 车列在落点后方(提前点在行进方向前方), 在飞屏蔽需按实体。死亡由 Reconcile 释放。</summary>
    public List<string>? ClusterMembers;
    /// <summary>变速/停车二次采纳的跟踪对象: 集群任务自身 entityId=""(位置提交, 注册表按落点覆盖),
    /// 但停车采纳需要跟踪领队(车列编队同步动停)。单点任务不设, 回退用 entityId。</summary>
    public string TrackEntityId = "";
    /// <summary>移动集群落点相对领队的偏移(世界单位): 停车采纳时落点 = 领队当前位置 + 偏移,
    /// 保持车列几何不变(落点仍在车列中点)——否则落点跳车头, 覆盖判定与爆炸错位(5 节只炸前 3 节)。</summary>
    public Vector3 ClusterOffset;
}