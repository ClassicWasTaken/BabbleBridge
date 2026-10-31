// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BabbleFrameBridge.App;

/// <summary>
/// Owns the notification-area icon on the WPF dispatcher thread. The native
/// message window remains hidden and never creates another taskbar button.
/// </summary>
public sealed class SystemTrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 0x247;
    private const uint IconId = 1;
    private const uint OpenCommand = 1;
    private const uint QuitCommand = 2;
    private const uint NotifyAdd = 0;
    private const uint NotifyModify = 1;
    private const uint NotifyDelete = 2;
    private const uint NotifySetFocus = 3;
    private const uint NotifySetVersion = 4;
    private readonly Dispatcher _dispatcher;
    private readonly Action _open;
    private readonly Action _quit;
    private readonly uint _taskbarCreated;
    private readonly HwndSource _source;
    private nint _icon;
    private bool _version4;
    private bool _disposed;
    private bool _showingMenu;

    public bool IsVisible { get; private set; }

    /// <summary>The shell could not restore the icon after restarting. Keep a window accessible.</summary>
    public event EventHandler? Unavailable;

    public SystemTrayIcon(string iconPath, Action open, Action quit)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(quit);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcher.VerifyAccess();
        _open = open;
        _quit = quit;
        var fullPath = Path.GetFullPath(iconPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The app's system tray icon could not be found.", fullPath);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The system tray recovery message could not be registered.");
        // Load an owned icon at the system's small-icon size; the ICO includes
        // multiple resolutions, including the sizes required by high-DPI trays.
        _icon = LoadImage(0, fullPath, 1, Math.Max(16, GetSystemMetrics(49)),
            Math.Max(16, GetSystemMetrics(50)), 0x0010); // IMAGE_ICON, LR_LOADFROMFILE
        if (_icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The app's system tray icon could not be loaded.");
        HwndSource? source = null;
        try
        {
            // A hidden top-level window receives Explorer's TaskbarCreated
            // broadcast; message-only HWNDs would miss that recovery message.
            source = new HwndSource(new HwndSourceParameters("Face Tracking Bridge tray")
            {
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP, without WS_VISIBLE
                ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW
                Width = 0,
                Height = 0
            });
            source.AddHook(WindowMessage);
            _source = source;
        }
        catch
        {
            source?.Dispose();
            DestroyIcon(_icon);
            _icon = 0;
            throw;
        }
    }

    /// <summary>Returns false if the shell is unavailable; callers must keep the app visible.</summary>
    public bool TryShow()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return false;
        var data = IconData();
        // Refreshing first also detects a shell restart before its broadcast has
        // reached us. Never hide the main window based only on a stale boolean.
        if (ShellNotifyIcon(NotifyModify, ref data)) { IsVisible = true; return true; }
        IsVisible = ShellNotifyIcon(NotifyAdd, ref data);
        if (!IsVisible) return false;
        data.Version = 4;
        _version4 = ShellNotifyIcon(NotifySetVersion, ref data);
        return true;
    }

    private NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _source.Handle,
        Id = IconId,
        Flags = 0x0001 | 0x0002 | 0x0004 | 0x0080, // MESSAGE | ICON | TIP | SHOWTIP
        Callback = CallbackMessage,
        Icon = _icon,
        Tip = "Face Tracking Bridge",
        Info = "",
        InfoTitle = ""
    };

    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed) return 0;
        if ((uint)message == _taskbarCreated)
        {
            IsVisible = false;
            if (!TryShow()) Dispatch(() => Unavailable?.Invoke(this, EventArgs.Empty));
            return 0;
        }
        if ((uint)message != CallbackMessage) return 0;
        var value = unchecked((ulong)lParam.ToInt64());
        var id = _version4 ? (uint)((value >> 16) & 0xffff) : unchecked((uint)wParam.ToInt64());
        if (id != IconId) return 0;
        var notification = _version4 ? (uint)(value & 0xffff) : (uint)value;
        handled = true;
        switch (notification)
        {
            case 0x0400: // NIN_SELECT
            case 0x0401: // NIN_KEYSELECT, Enter or Space from the notification area
            case 0x0203: // WM_LBUTTONDBLCLK
                Dispatch(_open);
                break;
            case 0x0202 when !_version4: // WM_LBUTTONUP on the legacy callback protocol
                Dispatch(_open);
                break;
            case 0x007b: // WM_CONTEXTMENU, including the keyboard context-menu key
            case 0x0205 when !_version4: // WM_RBUTTONUP on the legacy protocol
                Dispatch(ShowMenu);
                break;
        }
        return 0;
    }

    private void Dispatch(Action action) => _dispatcher.BeginInvoke(DispatcherPriority.Normal,
        new Action(() => { if (!_disposed) action(); }));

    private void ShowMenu()
    {
        if (_disposed || _showingMenu) return;
        _showingMenu = true;
        var menu = CreatePopupMenu();
        if (menu == 0) { _showingMenu = false; return; }
        uint selected = 0;
        try
        {
            if (!AppendMenu(menu, 0, OpenCommand, "&Open Face Tracking Bridge")
                || !AppendMenu(menu, 0x0800, 0, null)
                || !AppendMenu(menu, 0, QuitCommand, "&Quit")) return;
            SetMenuDefaultItem(menu, OpenCommand, false);
            var identifier = new NotifyIconIdentifier
            {
                Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = _source.Handle, Id = IconId
            };
            int x, y;
            if (ShellNotifyIconGetRect(ref identifier, out var bounds) == 0)
            {
                x = bounds.Left;
                y = bounds.Top;
            }
            else if (GetCursorPos(out var point)) { x = point.X; y = point.Y; }
            else { x = 0; y = 0; }
            // Foreground activation and a trailing WM_NULL are required for a
            // notification icon's popup to dismiss and reopen correctly.
            SetForegroundWindow(_source.Handle);
            var alignment = GetSystemMetrics(40) != 0 ? 0x0008u : 0u; // SM_MENUDROPALIGNMENT
            selected = TrackPopupMenu(menu, 0x0100 | 0x0080 | 0x0002 | 0x0020 | alignment,
                x, y, 0, _source.Handle, 0); // RETURNCMD | NONOTIFY | RIGHTBUTTON | BOTTOMALIGN
            PostMessage(_source.Handle, 0, 0, 0);
        }
        finally
        {
            DestroyMenu(menu);
            _showingMenu = false;
        }
        if (_disposed) return;
        if (selected == OpenCommand) Dispatch(_open);
        else if (selected == QuitCommand) Dispatch(_quit);
        else
        {
            var data = IconData();
            ShellNotifyIcon(NotifySetFocus, ref data);
        }
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        var data = IconData();
        ShellNotifyIcon(NotifyDelete, ref data);
        IsVisible = false;
        _source.RemoveHook(WindowMessage);
        _source.Dispose();
        if (_icon != 0) { DestroyIcon(_icon); _icon = 0; }
    }

    // These independent declarations describe the Windows SDK ABI. No vendor
    // runtime or SDK source is bundled. ABI reference:
    // https://learn.microsoft.com/windows/win32/api/shellapi/ns-shellapi-notifyicondataw
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid Guid;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect rectangle);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMenuDefaultItem(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rectangle);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
