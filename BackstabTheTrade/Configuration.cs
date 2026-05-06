using System;
using Dalamud.Configuration;

namespace BackstabTheTrade;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Default delay between trade actions in milliseconds
    public int ActionDelayMs { get; set; } = 0;

    // Delay between /target and /trade
    public int TargetToTradeDelayMs { get; set; } = 0;

    // Delay before retrying /trade if trade window did not open
    public int TradeRetryDelayMs { get; set; } = 0;

    // Delay after opening trade window before entering gil
    public int TradeOpenDelayMs { get; set; } = 0;

    // Delay before retrying gil bar click
    public int GilBarRetryDelayMs { get; set; } = 0;

    // Delay after InputNumeric opens before pressing OK
    public int OkButtonDelayMs { get; set; } = 0;

    // Delay before retrying InputNumeric enter/OK
    public int InputNumericRetryDelayMs { get; set; } = 0;

    // Delay after entering gil before pressing Trade button
    public int ConfirmDelayMs { get; set; } = 0;

    // Delay before retrying Trade button click
    public int TradeButtonRetryMs { get; set; } = 350;

    // Delay after SelectYesno opens before pressing Yes
    public int YesButtonDelayMs { get; set; } = 0;

    // Delay before triggering the planned item context-menu Trade callback.
    public int PlannedContextMenuTriggerDelayMs { get; set; } = 60;

    // Delay after InputNumeric becomes ready before applying planned item quantity.
    public int PlannedQuantityReadyDelayMs { get; set; } = 20;

    // Delay after planned item quantity OK before polling trade slot readiness.
    public int PlannedPopupCloseWaitMs { get; set; } = 50;

    // Poll interval while waiting for planned item to appear in a trade slot.
    public int PlannedTradeSlotPollMs { get; set; } = 15;

    // Delay before moving on to the next planned item chunk.
    public int PlannedNextItemWaitMs { get; set; } = 20;

    // Receiver helper: wait after seeing the other player's offer before pressing Trade.
    public int ReceiverOfferSettleDelayMs { get; set; } = 500;

    // Receiver helper: wait after pressing Trade before retrying if confirmation has not opened.
    public int ReceiverTradeRetryDelayMs { get; set; } = 1000;

    // Receiver helper: internal poll interval while waiting for stable offer / confirm.
    public int ReceiverPollDelayMs { get; set; } = 100;

    // Receiver helper: faster internal poll interval right after partner offer changes.
    public int ReceiverChangedPollDelayMs { get; set; } = 75;

    // Receiver helper: automatically click Trade and confirm Yes when receiving a trade.
    public bool ReceiverModeAutoConfirm { get; set; } = false;

    public void Save()
    {
        BackstabTheTrade.PluginInterface.SavePluginConfig(this);
    }
}

