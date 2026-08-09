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
    PLCM = 13,
    PHGN = 14,
    PRPG = 15,
    SMK = 16,
    STAR = 17,
    TEAR = 18,
    THRM = 19,
    WP = 20,
}

public class GunSystem {
    private string _surfix = "";

    private CylinderShellSelector? shellSelector;
    
    private List<string?> bullets = new();
    private LookAtTarget? nextBulletButton;
    private LookAtTarget? loadBulletButton;
    private List<LookAtTarget> powderButtons = new();
    private LookAtTarget? loadPowderButton;
    private GunController? gunController;
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
        elevationLever = GameObject.Find(".Elevation Lever Baseplate")?.transform.FindChild(".Elevation Lever " + surfix)
            .GetComponent<LinearSliderInteractable>();
        return true;
    }
    
    public bool CanFire() {
        return gunController.CanFire;
    }

    public IEnumerator SetElevation(float elevation) {
        elevationLever.SetSliderValue(elevation);
        yield return new WaitForSeconds(0.1f);
        while (gunController.CurrentElevation != elevation) {
            elevationLever.SetSliderValue(elevation);
            yield return new WaitForSeconds(1f);
        }
    }
    
    public string? BulletInChamber() {
        return gunController?.ChamberedShellBlueprint?.shellDefinition?.ShellId;
    }
    
    public bool IsChamberEmpty() {
        return BulletInChamber() == null;
    }

    private void RefreshBullets() {
        bullets.Clear();
        if (shellSelector == null) return;
        foreach (var shell in shellSelector.bullets) {
            bullets.Add(shell?.GetComponent<ShellBlueprint>()?.shellDefinition?.ShellId);
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
    /// 装填指定弹种：先把弹仓转到目标弹，再按装填。转弹仓每步之间要等 1 秒
    /// （游戏有转动动画/物理）。返回 IEnumerator，调用方用 yield return 等待它跑完。
    /// 必须走协程而非 async：continuation 要留在主线程才能安全访问 IL2CPP 对象。
    /// </summary>
    public IEnumerator LoadBullet(BulletType type) {
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
        // ponytail: 13s 不是魔数，是炮管退膛→抛壳→复进→闭锁的机械循环时间。
        // 改短会导致下一发装填时 breech 未复位，装填卡住。
        while (gunController.elevationChangeVelocity != 0) {
            yield return new WaitForSeconds(0.1f);
        }
        yield return new WaitForSeconds(13);
    }

    public IEnumerator WaitFire() {
        while (!gunController.pendingReload) {
            yield return new WaitForSeconds(0.1f);
        }
    }
    
    public int RemainingCharges() {
        return (int)remainingCharges.CurrentNumber;
    }

}
