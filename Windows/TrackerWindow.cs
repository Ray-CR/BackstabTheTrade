using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface.Windowing;

namespace BackstabTheTrade;

public sealed class TrackerWindow : Window, IDisposable
{
    private const string IssueUrl = "https://github.com/Ray-CR/BackstabTheTrade/issues/new";
    private readonly BackstabTheTrade _plugin;
    private readonly MainWindow _mainWindow;
    private string? _statusMessage;
    private Vector4 _statusColor = new(0.85f, 0.85f, 0.85f, 1f);

    public TrackerWindow(BackstabTheTrade plugin, MainWindow mainWindow)
        : base("Diagnostic Tracker###BackstabTheTradeTracker")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 420),
            MaximumSize = new Vector2(1000, 1000),
        };

        _plugin = plugin;
        _mainWindow = mainWindow;
        IsOpen = false;
    }

    public override void Draw()
    {
        var tracker = _plugin.EventTracker;

        if (tracker.IsTracking)
        {
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "TRACKING");
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
                tracker.StopTracking();
        }
        else
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Paused");
            ImGui.SameLine();
            if (ImGui.Button("Start Tracking"))
                tracker.StartTracking();
        }

        ImGui.SameLine();
        bool takeOverEnabled = _mainWindow.CanManualStart();
        ImGui.BeginDisabled(!takeOverEnabled);
        if (ImGui.Button("Take Over Open Trade Window"))
            _mainWindow.StartManual();
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            tracker.ClearLog();

        ImGui.SameLine();
        if (ImGui.Button("Copy Event Log"))
        {
            ImGui.SetClipboardText(BuildLogText(tracker));
            SetStatus("Event log copied to clipboard.", new Vector4(0.4f, 1f, 0.4f, 1f));
        }

        ImGui.SameLine();
        if (ImGui.Button("Save Event Log"))
        {
            if (TrySaveLogToFile(tracker, out var savedPath, out var error))
                SetStatus($"Saved event log to {savedPath}", new Vector4(0.4f, 1f, 0.4f, 1f));
            else
                SetStatus($"Failed to save event log: {error}", new Vector4(1f, 0.45f, 0.45f, 1f));
        }

        ImGui.SameLine();
        if (ImGui.Button("Open Issue Page"))
        {
            if (TryOpenIssuePage(out var error))
                SetStatus("Opened GitHub issue page.", new Vector4(0.4f, 1f, 0.4f, 1f));
            else
                SetStatus($"Failed to open issue page: {error}", new Vector4(1f, 0.45f, 0.45f, 1f));
        }

        ImGui.Separator();

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            ImGui.TextColored(_statusColor, _statusMessage);
            ImGui.Separator();
        }

        ImGui.BeginChild("log", new Vector2(-1, -1), false, ImGuiWindowFlags.HorizontalScrollbar);

        lock (tracker.Log)
        {
            foreach (var line in tracker.Log)
                ImGui.TextUnformatted(line);
        }

        if (tracker.IsTracking)
            ImGui.SetScrollHereY(1f);

        ImGui.EndChild();
    }

    public void Dispose()
    {
    }

    private static string BuildLogText(EventTracker tracker)
    {
        var sb = new StringBuilder();
        lock (tracker.Log)
        {
            foreach (var line in tracker.Log)
                sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static bool TrySaveLogToFile(EventTracker tracker, out string savedPath, out string error)
    {
        try
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string folder = Path.Combine(documents, "BackstabTheTrade", "Logs");
            Directory.CreateDirectory(folder);

            string fileName = $"event-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            savedPath = Path.Combine(folder, fileName);
            File.WriteAllText(savedPath, BuildLogText(tracker), Encoding.UTF8);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            savedPath = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static bool TryOpenIssuePage(out string error)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = IssueUrl,
                UseShellExecute = true,
            });

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void SetStatus(string message, Vector4 color)
    {
        _statusMessage = message;
        _statusColor = color;
    }
}

