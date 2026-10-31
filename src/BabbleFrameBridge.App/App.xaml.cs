// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Windows;
using System.Runtime.InteropServices;

namespace BabbleFrameBridge.App;

public partial class App : Application
{
    private Mutex? _instance;
    private Mutex? _installerPresence;
    private SystemThemeManager? _theme;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLine, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabbleFrameBridge");
        try { StartApplication(e, ref root); }
        catch (Exception error)
        {
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
        if (dataIndex >= 0 && dataIndex + 1 < e.Args.Length) root = Path.GetFullPath(e.Args[dataIndex + 1]);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..20];
        _instance = new Mutex(true, "Local\\BabbleFrameBridge-" + key, out var first);
        if (!first) { MessageBox.Show("Face Tracking Bridge is already open.", "Face Tracking Bridge"); Shutdown(); return; }
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
        var window = new MainWindow(root);
        _theme.AttachWindow(window);
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _theme?.Dispose(); _installerPresence?.Dispose(); _instance?.Dispose(); base.OnExit(e); }
}
