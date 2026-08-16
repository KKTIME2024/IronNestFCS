using System.Linq;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

public class FcsWindow
{
    private readonly FSC fcs;

    private bool showWindow = true;
    private Rect panelRect = new(20, 20, 290, 140);

    private static readonly Color ClrTitle = new(0.96f, 0.65f, 0.14f);
    private static readonly Color ClrLabel = new(0.72f, 0.65f, 0.55f);
    private static readonly Color ClrIdle = new(0.35f, 0.50f, 0.35f);
    private static readonly Color ClrActive = new(0.27f, 0.72f, 0.82f);
    private static readonly Color ClrWarning = new(0.96f, 0.65f, 0.14f);
    private static readonly Color ClrFailed = new(0.83f, 0.18f, 0.18f);
    private static readonly Color ClrGreen = new(0.18f, 0.62f, 0.35f);
    private static readonly Color ClrDiv = new(0.33f, 0.22f, 0.14f);
    private static readonly Color ClrSweep = new(0.96f, 0.35f, 0.14f);

    public bool AutoSweepEnabled { get; set; }

    public FcsWindow(FSC fcs) => this.fcs = fcs;

    public void OnGui()
    {
        if (!showWindow) return;

        float h = 22f;
        float lineH = h + 2f;

        float extra = 0f;
        if (fcs.LeftTask != null) extra += lineH * 4;
        else extra += lineH;
        if (fcs.RightTask != null) extra += lineH * 4;
        else extra += lineH;
        extra += lineH * fcs.InFlight.Count(t => t.progress == Progress.Finished);
        extra += lineH * (fcs.PendingCount + 1);
        extra += 12f;

        panelRect.height = 140f + extra;

        GUI.Box(panelRect, "");

        float x = panelRect.x + 8f;
        float w = panelRect.width - 16f;
        float y = panelRect.y + 4f;

        var oldColor = GUI.color;
        GUI.color = ClrTitle;
        GUI.Label(new Rect(x, y, w, h), "IronNestFCS-Automat");
        GUI.color = oldColor;
        y += lineH;

        GUI.color = AutoSweepEnabled ? ClrSweep : ClrLabel;
        GUI.Label(new Rect(x, y, w, h), AutoSweepEnabled ? "[AUTO]" : "[MANUAL]");
        GUI.color = oldColor;
        y += lineH;

        // CBT 状态行(无尽反炮兵模式: 阶段/剩余秒/积分)
        if (fcs.Cbt.IsCbtMode)
        {
            float remain = fcs.Cbt.TimeRemaining;
            string remainText = remain >= 0 ? $"{remain:F0}s" : "--";
            GUI.color = fcs.Cbt.Phase == CbtMonitor.CbtPhase.Critical ? ClrFailed
                : fcs.Cbt.Phase == CbtMonitor.CbtPhase.Urgent ? ClrWarning : ClrLabel;
            GUI.Label(new Rect(x, y, w, h),
                $"CBT {fcs.Cbt.Phase} {remainText} pts={fcs.Cbt.RequisitionPoints}");
            GUI.color = oldColor;
            y += lineH;
        }

        DrawDivider(x, y, w);
        y += 4f;

        if (!fcs.IsBound)
        {
            GUI.Label(new Rect(x, y, w, h), "Waiting for scene...");
            y += lineH;
            GUI.Label(new Rect(x, y, w, h), "Press F9 to reload");
            return;
        }

        y = DrawGunRow("Left  ", fcs.LeftTask, x, y, w, h, lineH);
        DrawDivider(x, y, w);
        y += 4f;
        y = DrawGunRow("Right ", fcs.RightTask, x, y, w, h, lineH);
        DrawDivider(x, y, w);
        y += 4f;

        GUI.color = ClrLabel;
        GUI.Label(new Rect(x, y, w, h), $"Queue: {fcs.PendingCount}");
        GUI.color = oldColor;
        y += lineH;

        foreach (var item in fcs.QueueCan)
        {
            string maxChargeTag = item.useMaxCharge ? " [MAX]" : "";
            GUI.Label(new Rect(x, y, w, h),
                $"  T{item.targetId}  {ConvertPosition(item.position)}  {item.angel,5:F1}°/{item.distance,5:F2}km  {item.bulletType}{maxChargeTag}");
            y += lineH;
        }

        // 已击发、炮弹仍在飞的任务: 倒计时到 0(=射表估计落地)后由 FSC 移出。
        // progress == Finished 的已离开炮位行, 不重复显示。
        foreach (var t in fcs.InFlight)
        {
            if (t.progress != Progress.Finished) continue;
            GUI.color = ClrSweep;
            GUI.Label(new Rect(x, y, w, h), $"  In flight T{t.targetId}  {ImpactText(t)}");
            GUI.color = oldColor;
            y += lineH;
        }
    }

    private float DrawGunRow(string label, ArtilleryTask? task, float x, float y, float w, float h, float lineH)
    {
        var oldColor = GUI.color;

        if (task == null)
        {
            GUI.color = ClrIdle;
            GUI.Label(new Rect(x, y, w, h), $"{label} Idle");
            GUI.color = oldColor;
            return y + lineH;
        }

        Color stateColor = task.progress switch
        {
            Progress.Failed => ClrFailed,
            Progress.Finished => ClrGreen,
            Progress.Pending => ClrLabel,
            Progress.Canceled => ClrLabel,
            _ => ClrActive
        };

        GUI.color = stateColor;
        string movingTag = task.IsMoving ? " [MOV]" : "";
        GUI.Label(new Rect(x, y, w, h), $"{label} T{task.targetId}  {task.bulletType}  {task.progress}{movingTag}");
        GUI.color = oldColor;
        y += lineH;

        GUI.color = ClrLabel;
        GUI.Label(new Rect(x + 12f, y, w - 12f, h),
            $"Target: {task.angel:F1}° / {task.distance:F2}km");
        GUI.color = oldColor;
        y += lineH;

        string chargeInfo = task.useMaxCharge ? "MAX" : BallisticCalculator.MinimumCharge(task.distance).ToString();
        GUI.color = ClrWarning;
        GUI.Label(new Rect(x + 12f, y, w - 12f, h),
            $"Charge: {chargeInfo} pks");
        GUI.color = oldColor;
        y += lineH;

        GUI.color = ClrWarning;
        GUI.Label(new Rect(x + 12f, y, w - 12f, h), ImpactText(task));
        GUI.color = oldColor;
        y += lineH;

        return y;
    }

    /// <summary>预计落弹: 解算后显示估计值(~Xs); 开火后倒计时到 0(射表估计落地)。</summary>
    private static string ImpactText(ArtilleryTask t)
    {
        if (t.EstimatedToF <= 0f) return "Impact: --";
        if (t.Fired)
        {
            float remaining = Mathf.Max(0f, t.EstimatedToF - (Time.time - t.FiredAt));
            return $"Impact: {remaining:F0}s";
        }
        return $"Impact: ~{t.EstimatedToF:F0}s";
    }

    private static void DrawDivider(float x, float y, float w)
    {
        var oldColor = GUI.color;
        GUI.color = ClrDiv;
        GUI.Label(new Rect(x, y, w, 1f), "");
        GUI.color = oldColor;
    }

    public static string ConvertPosition(Vector3 position)
    {
        int leterIndex = (int)position.x;
        string zoneCol = leterIndex >= 0 && leterIndex < 26 ? ((char)('A' + leterIndex)).ToString() : "#";
        int zoneRow = (int)position.y + 1;
        int subCol = (int)(position.x * 10) % 10;
        int subRow = (int)(position.y * 10) % 10;
        return $"{zoneCol}{zoneRow} {subCol}:{subRow}";
    }
}
