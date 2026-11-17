using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private static string BrowserReceiptPath(string appName) => Path.Combine(AppDataDir, appName + ".process.json");

    private static ApplicationProcessIdentity? TrackedBrowser(string appName)
    {
        if (appName is not ("VatEFS" or "VATIRIS")) return null;
        try
        {
            var path = BrowserReceiptPath(appName);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var identity = JsonSerializer.Deserialize<ApplicationProcessIdentity>(File.ReadAllText(path));
            return identity != null && Path.GetFileName(identity.ExecutablePath).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
                && ApplicationProcessControl.IsAlive(identity) ? identity : null;
        }
        catch { return null; }
    }

    private static void RememberBrowser(string appName, Process process)
    {
        try
        {
            var identity = ApplicationProcessControl.Identity(process);
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(BrowserReceiptPath(appName), JsonSerializer.Serialize(identity));
        }
        catch { /* Browser launch can succeed after its starter process exits. Do not guess ownership. */ }
    }

    // Edge does not restore --app placement for hosts with dots (see CLAUDE.md). Launchpad samples the
    // verified tracked window on the process timer and restores the saved placement when it starts it.
    private readonly BrowserPlacementTracker _browserPlacement = new();

    private void ObserveBrowserPlacement(string appName, ApplicationProcessIdentity? tracked)
    {
        if (!BrowserWindowSettings.Supports(appName) || _browserRestoreApp == appName) return;
        var observed = tracked is null ? null : WindowPlacementService.CaptureExternal(ApplicationProcessControl.MainWindow(tracked));
        // Saving writes all of _settings, so wait while an operation is running or another choice awaits
        // its explicit Retry save: this background save must not persist that state behind the user's back.
        bool canSave = CanControlRunningApplication() && !SaamanNeedsSaveRetry && !_vatEfsNeedsSaveRetry;
        if (_browserPlacement.Observe(_settings, appName, tracked is not null, observed, canSave))
            SettingsService.TrySave(_settings);
    }

    // Edge gets the saved placement as launch arguments (its restore rectangle). Returns a maximize sequence when the
    // saved window was maximized; null otherwise, including when nothing is saved or the saved display is no longer
    // connected, in which case Edge opens with its own default placement.
    private BrowserMaximizeSequence? AddBrowserPlacementArguments(string appName, ProcessStartInfo start)
    {
        if (BrowserWindowSettings.Get(_settings, appName) is not { } saved) return null;
        if (WindowPlacementService.PlanExternal(saved) is not { } target) return null;
        foreach (var argument in ExternalWindowLayout.EdgeArguments(target))
            start.ArgumentList.Add(argument);
        return target.Maximize ? new BrowserMaximizeSequence() : null;
    }

    // The exception to never touching the browser window: a launch whose saved placement was maximized opens at its
    // display's work area and is maximized once shown. Only the exact launched process is touched, the request is
    // posted asynchronously so an unresponsive Edge cannot block Launchpad's dispatcher, and placement capture pauses
    // until the sequence finishes.
    private static readonly TimeSpan BrowserMaximizeTimeout = TimeSpan.FromSeconds(15);
    private DispatcherTimer? _browserRestoreTimer;
    private string? _browserRestoreApp;

    private void MaximizeBrowserWhenShown(string appName, BrowserMaximizeSequence sequence)
    {
        StopBrowserRestore();
        if (TrackedBrowser(appName) is not { } launched) return;
        var deadline = DateTime.UtcNow + BrowserMaximizeTimeout;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            if (_windowClosed || now > deadline || !ApplicationProcessControl.IsAlive(launched)) { StopBrowserRestore(); return; }
            var hwnd = ApplicationProcessControl.MainWindow(launched);
            switch (sequence.Next(WindowPlacementService.ObserveExternal(hwnd), now))
            {
                case BrowserMaximizeAction.Maximize: WindowPlacementService.RequestMaximizeExternal(hwnd); break;
                case BrowserMaximizeAction.Done: StopBrowserRestore(); break;
            }
        };
        _browserRestoreTimer = timer;
        _browserRestoreApp = appName;
        timer.Start();
    }

    private void StopBrowserRestore()
    {
        _browserRestoreTimer?.Stop();
        _browserRestoreTimer = null;
        _browserRestoreApp = null;
    }

    private static IReadOnlyList<ApplicationProcessIdentity> RunningCopies(CheckResult row) => row.IsWebApp
        ? TrackedBrowser(row.AppName) is { } tracked ? [tracked] : []
        : ApplicationProcessControl.Find(row.LaunchPath);

    private void ShowRunningApplication(CheckResult row)
    {
        bool shown = RunningCopies(row).Any(process => ApplicationProcessControl.TryAct(process, RunningApplicationAction.Show));
        if (!shown)
        {
            row.StatusMessage = "The application is running, but its window could not be brought forward. Use the Windows taskbar or Alt+Tab. No application was closed.";
            FocusApplicationRow(row);
        }
    }

    private void ShowRunning_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CheckResult row }) ActOnRunningApplication(row, RunningApplicationAction.Show);
    }

    private void CloseApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CheckResult row }) ActOnRunningApplication(row, RunningApplicationAction.CloseNormally);
    }

    private void RunningActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckResult row } button) return;
        var menu = new ContextMenu();
        foreach (var (label, action) in new[] { ("Show application", RunningApplicationAction.Show),
            ("Close…", RunningApplicationAction.CloseNormally), ("Force stop…", RunningApplicationAction.ForceStop) })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => ActOnRunningApplication(row, action);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void ActOnRunningApplication(CheckResult row, RunningApplicationAction action)
    {
        if (!CanControlRunningApplication() || !TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        var copies = RunningCopies(row).Where(process => action == RunningApplicationAction.ForceStop || ApplicationProcessControl.HasWindow(process)).ToArray();
        if (copies.Length == 0)
        {
            row.StatusMessage = "Launchpad could not verify a running copy of this application. Use its own window or the Windows taskbar.";
            FocusApplicationRow(row);
            return;
        }
        if (action == RunningApplicationAction.Show) { ShowRunningApplication(row); return; }
        if (action is RunningApplicationAction.ForceStop or RunningApplicationAction.CloseNormally)
        {
            var verb = action == RunningApplicationAction.ForceStop ? "Force stop" : "Ask to close";
            var detail = action == RunningApplicationAction.ForceStop
                ? "Unsaved work may be lost and active network connections will end." : "Active network connections may end. The application may ask you to save or confirm. Launchpad will not force it to stop.";
            var targets = action == RunningApplicationAction.ForceStop ? $"This affects {copies.Length} verified process{(copies.Length == 1 ? "" : "es")} for the configured application." : $"This asks {copies.Length} application windows to close.";
            if (MessageBox.Show(this, $"{verb} {row.DisplayName}?\n\n{targets}\n\n{detail}",
                    verb + " " + row.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        int accepted = copies.Count(process => ApplicationProcessControl.TryAct(process, action));
        row.StatusMessage = accepted == 0 ? "The application did not accept the request. Use its own window to close it."
            : action == RunningApplicationAction.CloseNormally ? "Close requested. The application may be waiting for you to save or confirm; check its window."
            : "Force-stop requested for the verified running copy. Other installations were left alone.";
        FocusApplicationRow(row);
        UpdateRunningStates();
    }
}
