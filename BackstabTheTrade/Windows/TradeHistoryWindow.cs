using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface.Windowing;

namespace BackstabTheTrade;

public sealed class TradeHistoryWindow : Window, IDisposable
{
    private readonly TradeManager _tradeManager;
    private bool _showSummary;

    public TradeHistoryWindow(TradeManager tradeManager)
        : base("Trade History###BackstabTheTradeHistory")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 320),
            MaximumSize = new Vector2(1200, 900),
        };

        _tradeManager = tradeManager;
        IsOpen = false;
    }

    public override void Draw()
    {
        var history = _tradeManager.TradeHistory;

        if (ImGui.Button("Summary"))
            _showSummary = !_showSummary;

        ImGui.SameLine();
        if (ImGui.Button(_tradeManager.TradeHistoryTrackingEnabled ? "Stop Track" : "Start Track"))
            _tradeManager.SetTradeHistoryTracking(!_tradeManager.TradeHistoryTrackingEnabled);

        ImGui.SameLine();
        if (ImGui.Button("Copy History"))
        {
            var sb = new StringBuilder();
            foreach (var entry in history)
                sb.AppendLine($"{entry.At:yyyy-MM-dd HH:mm:ss} | {entry.Mode} | {entry.Result} | Attempt {entry.AttemptNumber} | {entry.Summary} | {entry.Detail}");

            ImGui.SetClipboardText(sb.ToString());
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear History"))
            _tradeManager.ClearTradeHistory();

        ImGui.SameLine();
        ImGui.TextDisabled($"{(_tradeManager.TradeHistoryTrackingEnabled ? "Tracking on" : "Tracking off")} | Last {history.Count} / 50 records");

        ImGui.Separator();

        if (_showSummary)
        {
            DrawSummary(history);
            ImGui.Separator();
        }

        if (history.Count == 0)
        {
            ImGui.TextDisabled("No trade records yet.");
            return;
        }

        if (ImGui.BeginTable("tradeHistoryTable", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Attempt", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("Summary", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            var clipper = new ImGuiListClipper();
            clipper.Begin(history.Count);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var entry = history[i];
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(entry.At.ToString("HH:mm:ss"));

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(entry.Mode);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextColored(GetResultColor(entry.Result), entry.Result);

                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(entry.AttemptNumber > 0 ? entry.AttemptNumber.ToString() : "-");

                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextWrapped(entry.Summary);

                    ImGui.TableSetColumnIndex(5);
                    ImGui.TextWrapped(entry.Detail);
                }
            }
            clipper.End();

            ImGui.EndTable();
        }
    }

    private static void DrawSummary(IReadOnlyList<TradeHistoryEntry> history)
    {
        ImGui.TextDisabled("Summary");

        int changedTrades = history.Count(entry => entry.GilDelta != 0 || entry.ItemDeltas.Length > 0);
        long gilNet = history.Sum(entry => entry.GilDelta);
        ImGui.TextUnformatted($"Trades with detected changes: {changedTrades}");
        ImGui.TextUnformatted($"Gil net: {(gilNet > 0 ? "+" : string.Empty)}{gilNet:N0}");

        var itemTotals = history
            .SelectMany(entry => entry.ItemDeltas)
            .GroupBy(delta => delta.Name)
            .Select(group => new { Name = group.Key, Delta = group.Sum(x => x.QuantityDelta) })
            .Where(x => x.Delta != 0)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (itemTotals.Length > 0)
        {
            foreach (var item in itemTotals)
                ImGui.TextUnformatted($"{item.Name}: {(item.Delta > 0 ? "+" : string.Empty)}{item.Delta:N0}");
        }
        else
        {
            ImGui.TextDisabled("No net item change recorded yet.");
        }

        if (history.Count > 0)
        {
            var latest = history[0];
            ImGui.TextUnformatted($"Latest: {latest.At:HH:mm:ss} | {latest.Mode} | {latest.Result} | {latest.Summary}");
        }
    }

    private static Vector4 GetResultColor(string result) => result switch
    {
        "Started" => new Vector4(0.45f, 0.75f, 1f, 1f),
        "Retrying" => new Vector4(1f, 0.8f, 0.45f, 1f),
        "Success" => new Vector4(0.4f, 1f, 0.4f, 1f),
        "Failed" => new Vector4(1f, 0.45f, 0.45f, 1f),
        "Stopped" => new Vector4(1f, 0.85f, 0.35f, 1f),
        "Timeout" => new Vector4(1f, 0.65f, 0.35f, 1f),
        "Unverified" => new Vector4(0.7f, 0.8f, 1f, 1f),
        _ => new Vector4(0.85f, 0.85f, 0.85f, 1f),
    };

    public void Dispose()
    {
    }
}

