// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BabbleFrameBridge.App;

/// <summary>Follows Windows' application color preference without changing it.</summary>
public sealed class SystemThemeManager : IDisposable
{
    private const string PreferencePath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly Application application;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<Window, WindowRegistration> windows = [];
    private ResourceDictionary? themeDictionary;
    private volatile bool disposed;
    private int refreshQueued;

    public bool CurrentIsDark { get; private set; }
    public event EventHandler? ThemeChanged;

    public SystemThemeManager(Application application)
    {
        this.application = application ?? throw new ArgumentNullException(nameof(application));
        dispatcher = application.Dispatcher;
        dispatcher.VerifyAccess();
        RefreshFromSystem();
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
    }

    /// <summary>Read AppsUseLightTheme; a missing or inaccessible preference uses light mode.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PreferencePath);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void RefreshFromSystem()
    {
        if (disposed) return;
        if (!dispatcher.CheckAccess()) { QueueRefresh(); return; }
        ApplyTheme(IsSystemDark());
    }

    /// <summary>Applies an in-process palette. Normal app use calls RefreshFromSystem.</summary>
    public void ApplyTheme(bool isDark)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (themeDictionary != null && CurrentIsDark == isDark) return;

        var assembly = typeof(SystemThemeManager).Assembly.GetName().Name;
        var next = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{assembly};component/Themes/{(isDark ? "Dark" : "Light")}.xaml", UriKind.Absolute)
        };
        var merged = application.Resources.MergedDictionaries;
        // Only replace a theme dictionary. Logo and other resource dictionaries stay in place.
        var previous = themeDictionary ?? merged.FirstOrDefault(IsThemeDictionary);
        var index = previous == null ? -1 : merged.IndexOf(previous);
        if (index >= 0) merged[index] = next;
        else merged.Insert(0, next);
        themeDictionary = next;
        CurrentIsDark = isDark;
        foreach (var registration in windows.Values) registration.UpdateTitleBar();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return source?.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) == true
            || source?.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Follows live Windows messages and applies a matching native title bar.</summary>
    public void AttachWindow(Window window)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(window);
        if (windows.ContainsKey(window)) return;
        windows.Add(window, new WindowRegistration(this, window));
    }

    private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueRefresh();

    private void QueueRefresh()
    {
        if (disposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished || Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref refreshQueued, 0);
                RefreshFromSystem();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref refreshQueued, 0); }
    }

    private void DetachWindow(Window window)
    {
        if (windows.Remove(window, out var registration)) registration.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        dispatcher.VerifyAccess();
        disposed = true;
        SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
        foreach (var registration in windows.Values) registration.Dispose();
        windows.Clear();
        ThemeChanged = null;
    }

    private sealed class WindowRegistration : IDisposable
    {
        private readonly SystemThemeManager owner;
        private readonly Window window;
        private HwndSource? source;
        private bool disposed;

        public WindowRegistration(SystemThemeManager owner, Window window)
        {
            this.owner = owner;
            this.window = window;
            window.SourceInitialized += SourceInitialized;
            window.Closed += Closed;
            HookSource();
        }

        private void SourceInitialized(object? sender, EventArgs e) => HookSource();
        private void Closed(object? sender, EventArgs e) => owner.DetachWindow(window);

        private void HookSource()
        {
            if (disposed || source != null) return;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            source = HwndSource.FromHwnd(handle);
            source?.AddHook(WindowMessage);
            UpdateTitleBar();
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WM_SETTINGCHANGE, WM_SYSCOLORCHANGE and WM_THEMECHANGED cover both
            // Settings' app-mode control and Windows broadcasts after personalization.
            if (message is 0x001A or 0x0015 or 0x031A) owner.QueueRefresh();
            return IntPtr.Zero;
        }

        public void UpdateTitleBar()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var dark = owner.CurrentIsDark ? 1 : 0;
            // Older Windows 10 builds use attribute19; newer builds use attribute20.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) < 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            window.SourceInitialized -= SourceInitialized;
            window.Closed -= Closed;
            source?.RemoveHook(WindowMessage);
            source = null;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
