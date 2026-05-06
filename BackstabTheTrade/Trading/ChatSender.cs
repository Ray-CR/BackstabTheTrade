using System;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace BackstabTheTrade;

/// <summary>
/// Sends a chat command through the game's shell module.
/// </summary>
public sealed class ChatSender : IDisposable
{
    public bool IsAvailable => true;

    public unsafe bool Send(string message)
    {
        try
        {
            var fw  = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (fw == null) return false;

            var ui  = fw->GetUIModule();
            if (ui == null) return false;

            var shellModule = ui->GetRaptureShellModule();
            if (shellModule != null)
            {
                var text = Utf8String.FromString(message);
                if (text != null)
                {
                    try
                    {
                        shellModule->ExecuteCommandInner(text, ui);
                        BackstabTheTrade.Log.Information($"[AutoTrade] Sent via ExecuteCommandInner: {message}");
                        return true;
                    }
                    finally
                    {
                        text->Dtor(true);
                    }
                }
            }
            
            BackstabTheTrade.Log.Warning("[AutoTrade] ExecuteCommandInner unavailable; chat command not sent.");
            return false;
        }
        catch (Exception ex)
        {
            BackstabTheTrade.Log.Error(ex, $"[AutoTrade] ChatSender.Send failed for: {message}");
            return false;
        }
    }

    public void Dispose() { }
}

