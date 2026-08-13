using System;
using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class Turret {
    private TurretController? _turret;


    public bool TryBind() {
        var turretObj = GameObject.Find("TurretSystem");
        if (turretObj == null) {
            MelonLogger.Error("[FCS] Aiming: Can't find TurretSystem");
            return false;
        }
        _turret = turretObj.GetComponent<TurretController>();
        return true;
    }
    
    public IEnumerator SetRotation(float angle) {
        if (_turret == null) {
            MelonLogger.Error("[FCS] Aiming: unbound TurretController");
            yield break;
        }

        _turret.DesiredRotation = -angle;
        LastSetAngle = angle;
        yield return new WaitForSeconds(1f);
        while (_turret.rotationVelocity != 0) {
            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>最近一次 SetRotation 的目标角度，同优先级任务按此排序以减少转动</summary>
    public float LastSetAngle { get; private set; }

    /// <summary>非阻塞: 直接设目标方位角, 不等待转完。连续跟踪每帧调用。</summary>
    public void SetDesiredRotation(float angle) {
        if (_turret == null) return;
        _turret.DesiredRotation = -angle;
        LastSetAngle = angle;
    }

    /// <summary>就绪判定: 实际方位(CurrentAngle, 与 DesiredRotation 同负号坐标系)与目标方位差(0-180)。
    /// 读实际值而非期望值——设目标后物理未响应的瞬态(velocity=0 但未转)不再误判"已收敛"。
    /// rotationVelocity!=0 返回 180 防"转动中扫过目标角"的瞬态误判。转到位 = 实际方位==目标。</summary>
    public float AngleError(float targetAngle) {
        if (_turret == null) return 0f;
        if (_turret.rotationVelocity != 0f) return 180f;   // 仍在转 → 未就绪
        float d = Mathf.Abs(_turret.CurrentAngle + targetAngle) % 360f;
        return d > 180f ? 360f - d : d;
    }

}