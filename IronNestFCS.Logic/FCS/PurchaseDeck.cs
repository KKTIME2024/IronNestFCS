using Il2Cpp;
using MelonLoader;
using UnityEngine;
using System.Collections;
using static System.Enum;

namespace IronNestFCS.Logic.FCS;

public class PurchaseDeck {
    private Dictionary<BulletType, Transform> bulletCards = new();
    private Transform? _powderCard;
    private LookAtTarget? _buyButton;


    public bool TryBind() {
        var requisitionConsole = GameObject.Find("Requisition Console").transform;
        var cards = requisitionConsole.GetComponentsInChildren<PunchcardRuntime>();
        foreach (var card in cards) {
            MelonLogger.Msg($"[FCS] PurchaseDeck: Found card {card.CurrentDefinition.ID}");
            // APHCHE 特殊卡: 由 AphcheDeck 每局注入, ID="APShellMod", 不能靠 Replace 后 TryParse 命中
            if (card.CurrentDefinition.ID == AphcheDeck.CardId) {
                bulletCards[BulletType.APHCHE] = card.transform;
                continue;
            }
            // AP 卡: 游戏内卡 ID 是 "APShell", 去掉 Shell 后是 "AP", 而枚举名是 APHE——直接特判
            if (card.CurrentDefinition.ID == "APShell") {
                bulletCards[BulletType.APHE] = card.transform;
                continue;
            }
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

    /// <summary>AphcheDeck 加卡成功后动态注册该卡，避免错过 TryBind 时机的卡片。</summary>
    public void RegisterCard(BulletType type, Transform card) {
        bulletCards[type] = card;
        MelonLogger.Msg($"[FCS] PurchaseDeck: RegisterCard {type} → {card.name}");
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
        var target = new Vector3(6.4814f, -2.4675f, -22.0968f);
        card.position = target;
        card.GetComponent<DraggableItem>().MoveToSlot();
        yield return new WaitForSeconds(0.5f);
        
        switch (leftRight) {
            case LeftRight.Left:
                GetLeftRightDial().SetDialValue(0);
                break;
            case LeftRight.Right:
                GetLeftRightDial().SetDialValue(1);
                break;
        }
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return new WaitForSeconds(2f);
    }

    public IEnumerator BuyPowders() {
        if (_powderCard == null) {
            MelonLogger.Error("[FCS] BuyPowders: Can't find PowderCharges card");
            yield break;
        }
        _powderCard.position = new Vector3(6.4814f, -2.4675f, -22.0968f);
        _powderCard.GetComponent<DraggableItem>().MoveToSlot();
        // 与 BuyShell 一致：等卡牌入槽稳定后再点购买，避免点击早于入槽导致本次采购无效。
        yield return new WaitForSeconds(0.5f);
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return new WaitForSeconds(2f);
    }
    
}