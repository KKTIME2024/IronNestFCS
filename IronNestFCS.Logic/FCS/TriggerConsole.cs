using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class TriggerConsole {
    private LookAtTarget? _taskCheck;
    private LookAtTarget? _bulletCheck;
    private LookAtTarget? _rotationCheck;
    private LookAtTarget? _elevationCheck;
    private LookAtTarget? _readyFire;
    private LookAtTarget? _armLeft;
    private LookAtTarget? _armRight;
    private SliderEnergyMomentumSpinner? _fire;

    /// <summary>快速确认点击节奏(秒)。开关是纯 0/1 flag, 一口气连点即可。
    /// 若游戏点击识别需要更长按下时间, 构建机实测后调大。</summary>
    private const float FastConfirmTapSeconds = 0.01f;

    public bool TryBind() {
        var console = GameObject.Find(".Review Console Parent").transform;
        var buttons = new List<LookAtTarget>();
        
        for (var i = 0; i < console.childCount; ++i) {
            var child = console.GetChild(i);
            if (child.name.StartsWith(".Check Switch")) {
                buttons.Add(child.GetComponentInChildren<LookAtTarget>());
            }
        }

        if (buttons.Count != 5) {
            MelonLogger.Error("Can't bind trigger console.");
        }
        _taskCheck = buttons[0];
        _bulletCheck = buttons[1];
        _rotationCheck = buttons[2];
        _elevationCheck = buttons[3];
        _readyFire = buttons[4];
        _armLeft = GameObject.Find(".ArmingLeverParent Left").GetComponentInChildren<LookAtTarget>();
        _armRight = GameObject.Find(".ArmingLeverParent Right").GetComponentInChildren<LookAtTarget>();
        _fire = GameObject.Find(".Trigger Core").transform.FindChild(".Generator Spinner")
            .GetComponentInChildren<SliderEnergyMomentumSpinner>();
        return true;
    }

    public void Fire() {
        _fire?.AddEnergy(255);
    }

    /// <summary>臂杆按下与 5 个快速确认并行(开关是纯 0/1 flag, 无值语义, 顺序无关)。
    /// 确认在臂杆保持期内一口气点完, 总时长不变(~1.2s), 但确认不再占独立时序。</summary>
    public IEnumerator ArmWithFastConfirm(LeftRight leftRight) {
        var arm = leftRight == LeftRight.Left ? _armLeft : _armRight;
        arm?.OnClickDown();
        var holdStart = Time.realtimeSinceStartup;
        yield return ConfirmAllFast();
        var held = Time.realtimeSinceStartup - holdStart;
        if (held < 0.2f) yield return new WaitForSeconds(0.2f - held);
        arm?.OnClickUp();
        yield return new WaitForSeconds(1f);
    }

    /// <summary>5 个确认开关一口气点完(任务/弹种/方位/仰角/就绪)。重复点击幂等。</summary>
    private IEnumerator ConfirmAllFast() {
        var switches = new[] { _taskCheck, _bulletCheck, _rotationCheck, _elevationCheck, _readyFire };
        foreach (var s in switches) {
            s?.OnClickDown();
            yield return new WaitForSeconds(FastConfirmTapSeconds);
            s?.OnClickUp();
            yield return new WaitForSeconds(FastConfirmTapSeconds);
        }
    }
}