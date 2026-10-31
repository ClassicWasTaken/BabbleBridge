// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BabbleFrameBridge.App;

public partial class App : Application
{
    private Mutex? _instance;
    private Mutex? _installerPresence;
    private SystemThemeManager? _theme;
    private CancellationTokenSource? _steamVrMonitor;
    private SystemTrayIcon? _tray;
    private HwndSource? _windowSource;
    private uint _restoreMessage, _quitMessage;
    private EventWaitHandle? _instanceReady;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLine, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabbleFrameBridge");
        var maintenance = e.Args.Contains("--refresh-steamvr-registration") || e.Args.Contains("--unregister-steamvr") || e.Args.Contains("--quit");
        try { StartApplication(e, ref root); }
        catch (Exception error)
        {
            if (maintenance)
            {
                LogMaintenanceError(root, error);
                Shutdown(1);
                return;
            }
            // Constructor failures happen before the window is shown. Handling them
            // only through DispatcherUnhandledException leaves an invisible instance
            // holding the single-instance and installer mutexes.
            MessageBox.Show("Face Tracking Bridge could not start.\n\n" + error.Message
                + "\n\nYour settings and imported module files have been preserved. Check the saved settings and module files in:\n"
                + root + "\n\nRestore a valid saved backup or correct the reported file problem, then open Face Tracking Bridge again.",
                "Face Tracking Bridge startup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartApplication(StartupEventArgs e, ref string root)
    {
        var dataIndex = Array.IndexOf(e.Args, "--data");
        if (dataIndex >= 0)
        {
            if (dataIndex + 1 >= e.Args.Length) throw new ArgumentException("The --data option needs a folder path.");
            root = Path.GetFullPath(e.Args[dataIndex + 1]);
        }
        var steamVr = new SteamVrIntegration(root, Environment.ProcessPath
            ?? throw new InvalidOperationException("The app executable could not be located."));
        if (SteamVrMaintenance.TryRun(e.Args, root, () => steamVr.ConfigureAutoLaunch(true), steamVr.Unregister)) { Shutdown(); return; }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..20];
        _restoreMessage = RegisterWindowMessage("ClassicWasTaken.FaceTrackingBridge.Open." + key);
        _quitMessage = RegisterWindowMessage("ClassicWasTaken.FaceTrackingBridge.Quit." + key);
        if (_restoreMessage == 0 || _quitMessage == 0) throw new InvalidOperationException("Windows could not register the app's Open and Quit actions.");
        _instanceReady = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\FaceTrackingBridge.Ready-" + key);
        _instance = new Mutex(true, "Local\\BabbleFrameBridge-" + key, out var first);
        if (!first)
        {
            // Reopening the shortcut restores a hidden instance. SteamVR's
            // automatic launch never interrupts an already-running session.
            if (e.Args.Contains("--quit") || !e.Args.Contains("--steamvr"))
            {
                if (!_instanceReady.WaitOne(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("Face Tracking Bridge is still starting or shutting down. Try opening it again in a moment.");
                PostMessage(0xffff, e.Args.Contains("--quit") ? _quitMessage : _restoreMessage, 0, 0);
            }
            Shutdown(); return;
        }
        _instanceReady.Reset();
        if (e.Args.Contains("--quit")) { Shutdown(); return; }
        // Stable presence marker lets future installers detect even portable copies.
        // Profile mutexes continue to keep normal and diagnostic data isolated.
        _installerPresence = new Mutex(false, "Local\\FrameBridge.Desktop");
        if (dataIndex < 0) {
            // Match the installer shortcuts so Windows groups the running app with
            // the shortcut that carries its explicit, current headset icon.
            SetCurrentProcessExplicitAppUserModelID("FrameBridge.Desktop");
            // Allow Restart Manager to reopen the app after a software update;
            // do not register automatic crash, hang, or system-reboot relaunches.
            RegisterApplicationRestart("", 1 | 2 | 8);
        }
        DispatcherUnhandledException += (_, error) => {
            MessageBox.Show(error.Exception.Message, "Face Tracking Bridge", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
        };
        _theme = new SystemThemeManager(this);
        var window = new MainWindow(root, configureSteamVrAutoLaunch: steamVr.ConfigureAutoLaunchAsync);
        var windowRoot = root;
        MainWindow = window;
        _theme.AttachWindow(window);
        window.SourceInitialized += (_, _) => {
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            _windowSource?.AddHook(InstanceWindowMessage);
            if (_windowSource is not null) _instanceReady.Set();
        };
        window.StartupPreferencesChanged += (_, _) => UpdateSteamVrMonitor(steamVr, window, windowRoot);
        window.Closed += (_, _) => StopSteamVrMonitor();
        SessionEnding += (_, _) => window.AllowSystemExit();
        try
        {
            _tray = new SystemTrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "FaceTrackingBridge-headset-v2.ico"),
                window.RestoreFromSystemTray, window.RequestExit);
            window.ConfigureSystemTray(_tray.TryShow);
            _tray.Unavailable += (_, _) => window.RestoreFromSystemTray();
            _tray.TryShow();
        }
        catch (Exception error) { LogMaintenanceError(root, error); }
        UpdateSteamVrMonitor(steamVr, window, root);
        if ((window.StartMinimized || e.Args.Contains("--minimized")) && window.HideToSystemTray())
        {
            // Create a hidden HWND for Open/Quit IPC, without showing a taskbar
            // button or activating a window during startup.
            new WindowInteropHelper(window).EnsureHandle();
            _ = window.InitializeBridgeAsync();
        }
        else window.Show();
    }

    private nint InstanceWindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == _restoreMessage || (uint)message == _quitMessage)
        {
            handled = true;
            var quit = (uint)message == _quitMessage;
            _ = Dispatcher.BeginInvoke(new Action(() => {
                if (MainWindow is MainWindow main) { if (quit) main.RequestExit(); else main.RestoreFromSystemTray(); }
            }));
        }
        return 0;
    }

    private void UpdateSteamVrMonitor(SteamVrIntegration steamVr, MainWindow window, string root)
    {
        // Only a change to the close preference should reset the observation.
        // Saving another preference while SteamVR exits must not lose the session.
        if (window.CloseWithSteamVr && _steamVrMonitor is not null) return;
        StopSteamVrMonitor();
        if (!window.CloseWithSteamVr) return;
        _steamVrMonitor = new CancellationTokenSource();
        _ = ObserveSteamVrExitAsync(steamVr, window, root, _steamVrMonitor.Token);
    }

    private async Task ObserveSteamVrExitAsync(SteamVrIntegration steamVr, MainWindow window, string root, CancellationToken token)
    {
        try
        {
            await steamVr.MonitorSessionAsync(() => Dispatcher.BeginInvoke(() =>
            {
                if (!token.IsCancellationRequested && window.CloseWithSteamVr)
                    window.RequestExit();
            }), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { LogMaintenanceError(root, error); }
    }

    private void StopSteamVrMonitor()
    {
        var monitor = _steamVrMonitor;
        _steamVrMonitor = null;
        monitor?.Cancel();
        monitor?.Dispose();
    }

    private static void LogMaintenanceError(string root, Exception error)
    {
        try
        {
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "steamvr-integration.log"),
                DateTimeOffset.Now.ToString("O") + " " + error.Message + Environment.NewLine);
        }
        catch { /* Maintenance must remain silent even when its diagnostic file cannot be written. */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopSteamVrMonitor();
        _tray?.Dispose();
        _windowSource?.RemoveHook(InstanceWindowMessage);
        _instanceReady?.Dispose();
        _theme?.Dispose(); _installerPresence?.Dispose(); _instance?.Dispose(); base.OnExit(e);
    }
}
