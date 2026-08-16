using Il2Cpp;
using MelonLoader;
using UnityEngine;
using System;
using System.Collections;
using static System.Enum;

namespace IronNestFCS.Logic.FCS;

public class PurchaseDeck {
    private Dictionary<BulletType, Transform> bulletCards = new();
    private Transform? _powderCard;
    private Transform? _emergencyMoveCard;  // Cost==65: 紧急移动(转移阵地+重置倒计时)
    private LookAtTarget? _buyButton;

    public bool HasEmergencyMoveCard => _emergencyMoveCard != null;

    public bool TryBind() {
        var requisitionConsole = GameObject.Find("Requisition Console").transform;
        var cards = requisitionConsole.GetComponentsInChildren<PunchcardRuntime>();
        foreach (var card in cards) {
            MelonLogger.Msg($"[FCS] PurchaseDeck: Found card {card.CurrentDefinition.ID} (cost={card.CurrentDefinition.Cost})");
            // 紧急移动卡: 65 点(§6)。按 Cost 识别, 不依赖 ID(关卡命名可能不同)。
            if (card.CurrentDefinition.Cost == 65)
                _emergencyMoveCard = card.transform;
            if (TryParse(
                    card.CurrentDefinition.ID.Replace("SMOKE", "SMK").Replace("Shell", ""),
                    out BulletType type
                )) {
                bulletCards[type] = card.transform;
            }
            else if (card.CurrentDefinition.ID == "PowderCharges") {
                _powderCard = card.transform;
            }
        }
        
        _buyButton = requisitionConsole.FindChild("Universal Button").GetComponent<LookAtTarget>();
        
        return true;
    }
    
    private DialInteractable GetLeftRightDial() {
        var consoleBox = GameObject.Find("Console Box").transform;
        return  consoleBox.GetComponentInChildren<DialInteractable>();
    }

    public IEnumerator BuyShell(BulletType type, LeftRight leftRight) {
        var card = bulletCards.GetValueOrDefault(type);
        if (card == null) {
            MelonLogger.Error($"[FCS] BuyShell: Can't find {type} card");
            yield break;
        }
        yield return BuyCard(card, () =>
        {
            switch (leftRight) {
                case LeftRight.Left:
                    GetLeftRightDial().SetDialValue(0);
                    break;
                case LeftRight.Right:
                    GetLeftRightDial().SetDialValue(1);
                    break;
            }
        });
    }

    public IEnumerator BuyPowders() {
        if (_powderCard == null) {
            MelonLogger.Error("[FCS] BuyPowders: Can't find PowderCharges card");
            yield break;
        }
        yield return BuyCard(_powderCard, null);
    }

    /// <summary>紧急移动: 采购 65 点卡(转移阵地 + 重置倒计时回 600s, 游戏节点图处理后续)。</summary>
    public IEnumerator BuyEmergencyMove() {
        if (_emergencyMoveCard == null) {
            MelonLogger.Error("[FCS] BuyEmergencyMove: Can't find cost=65 card");
            yield break;
        }
        yield return BuyCard(_emergencyMoveCard, null);
        MelonLogger.Msg("[FCS] 紧急移动卡已采购(65 点), 等待游戏转移阵地/重置倒计时");
    }

    /// <summary>通用采购: 拖卡入槽 → 等卡稳定 → (可选拨炮管拨盘) → 点购买按钮。
    /// 与原 BuyShell 时序一致(拨盘在点击前, 避免点击早于入槽导致采购无效)。</summary>
    private IEnumerator BuyCard(Transform card, Action? beforeClick) {
        var target = new Vector3(6.4814f, -2.4675f, -22.0968f);
        card.position = target;
        card.GetComponent<DraggableItem>().MoveToSlot();
        yield return new WaitForSeconds(0.5f);
        beforeClick?.Invoke();
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return new WaitForSeconds(2f);
    }
    
}