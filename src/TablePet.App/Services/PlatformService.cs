using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using TablePet.Core;
using Screen = System.Windows.Forms.Screen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace TablePet.App.Services;

/// <summary>Windows-only behavior. All public window operations run on the WPF UI thread.</summary>
public sealed class PlatformService : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExTransparent = 0x00000020;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private readonly Window _window;
    private HwndSource? _source;
    private nint _handle;
    private bool _sessionLocked;
    private bool _powerSuspended;
    private bool _lastSuspension;
    private bool _disposed;

    public PlatformService(Window petWindow) => _window = petWindow;

    public event Action<bool>? SuspensionChanged;
    public event Action? DisplayChanged;
    public event Action? DpiChanged;

    public void Attach()
    {
        _window.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is not null) return;
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("无法取得宠物窗口句柄。");
        _source.AddHook(WindowProc);
        var style = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        SetExtendedStyle(style | WsExNoActivate | WsExToolWindow);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void SetClickThrough(bool enabled)
    {
        EnsureAttached();
        var style = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetExtendedStyle(style | WsExNoActivate | WsExToolWindow);
    }

    private void SetExtendedStyle(long style) => SetExtendedStyle(_handle, style);

    private static void SetExtendedStyle(nint handle, long style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(handle, GwlExStyle, new nint(style));
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
        if (!SetWindowPos(handle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged))
            throw new Win32Exception();
    }

    /// <summary>Visible decoration pixels must also pass input to other applications underneath.</summary>
    public static void ConfigureDecorationWindow(Window decoration)
    {
        decoration.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(decoration).EnsureHandle();
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetExtendedStyle(handle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    /// <summary>Showing an owned WPF window must not raise an occluded pet in the desktop stack.</summary>
    public static void ShowDecorationWindow(Window owner, Window decoration)
    {
        owner.Dispatcher.VerifyAccess();
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var decorationHandle = new WindowInteropHelper(decoration).Handle;
        var aboveOwner = GetWindow(ownerHandle, 3); // GW_HWNDPREV, before HWND creation or Show.
        if (aboveOwner != 0 && aboveOwner == decorationHandle) aboveOwner = GetWindow(aboveOwner, 3);
        new WindowInteropHelper(decoration).EnsureHandle();
        AlignDecorationWindow(owner, decoration);
        decoration.Show();
        decorationHandle = new WindowInteropHelper(decoration).Handle;
        // WPF Show can raise the whole owner group even with ShowActivated=false.
        // Restore it synchronously before rendering, then put only the decoration
        // directly above its owner. A foreign window may close during Show.
        if (aboveOwner != 0 && !IsWindow(aboveOwner)) return;
        const uint flags = SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder;
        if (!SetWindowPos(ownerHandle, aboveOwner, 0, 0, 0, 0, flags) ||
            !SetWindowPos(decorationHandle, aboveOwner, 0, 0, 0, 0, flags))
            throw new Win32Exception();
    }

    /// <summary>Align an owned overlay in physical coordinates, including on monitors with different DPI.</summary>
    public static void AlignDecorationWindow(Window owner, Window decoration)
    {
        owner.Dispatcher.VerifyAccess();
        decoration.Dispatcher.VerifyAccess();
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var decorationHandle = new WindowInteropHelper(decoration).Handle;
        if (ownerHandle == 0 || decorationHandle == 0) return;
        if (decoration.Topmost != owner.Topmost) decoration.Topmost = owner.Topmost;
        if (!GetWindowRect(ownerHandle, out var bounds)) throw new Win32Exception();
        if (!GetWindowRect(decorationHandle, out var current)) throw new Win32Exception();
        if (bounds.Left == current.Left && bounds.Top == current.Top &&
            bounds.Right == current.Right && bounds.Bottom == current.Bottom) return;
        if (!SetWindowPos(decorationHandle, 0, bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, SwpNoZOrder | SwpNoActivate))
            throw new Win32Exception();
    }

    public static Point GetCursorPhysical()
    {
        if (!GetCursorPos(out var point)) throw new Win32Exception();
        return new Point(point.X, point.Y);
    }

    /// <summary>The input desktop can temporarily be inaccessible during lock or secure-desktop transitions.</summary>
    public static bool TryGetCursorPhysical(out Point point)
    {
        if (GetCursorPos(out var native))
        {
            point = new Point(native.X, native.Y);
            return true;
        }
        point = default;
        return false;
    }

    public static bool IsLeftButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    public static nint GetForegroundWindowHandle() => GetForegroundWindow();

    public Rect GetWindowBoundsPhysical()
    {
        EnsureAttached();
        if (!GetWindowRect(_handle, out var rect)) throw new Win32Exception();
        return new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>Occluded pets must not react to mouse movement over another application's window.</summary>
    public bool IsPointerOverPet(Point physical)
    {
        EnsureAttached();
        var hit = WindowFromPoint(new NativePoint { X = (int)Math.Round(physical.X), Y = (int)Math.Round(physical.Y) });
        return hit == _handle || (hit != 0 && GetAncestor(hit, 2) == _handle); // GA_ROOT
    }

    /// <summary>Signed physical coordinates; negative monitor coordinates are preserved.</summary>
    public void MovePhysical(double x, double y)
    {
        EnsureAttached();
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        if (!SetWindowPos(_handle, 0, (int)Math.Round(x), (int)Math.Round(y), 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate)) throw new Win32Exception();
    }

    public WindowPlacement CapturePlacement()
    {
        var bounds = GetWindowBoundsPhysical();
        var screen = Screen.FromHandle(_handle);
        var work = screen.WorkingArea;
        return new WindowPlacement
        {
            MonitorId = screen.DeviceName,
            RelativeX = Math.Clamp((bounds.Left - work.Left) / Math.Max(1, work.Width - bounds.Width), 0, 1),
            RelativeY = Math.Clamp((bounds.Top - work.Top) / Math.Max(1, work.Height - bounds.Height), 0, 1)
        };
    }

    /// <summary>Restore normalized position within a monitor's available working area.</summary>
    public void RestorePlacement(WindowPlacement? placement = null)
    {
        EnsureAttached();
        var target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == placement?.MonitorId)
            ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var work = target.WorkingArea;
        var relativeX = Math.Clamp(placement?.RelativeX ?? 0.9, 0, 1);
        var relativeY = Math.Clamp(placement?.RelativeY ?? 0.92, 0, 1);
        // Move into the target monitor first. WPF then applies that monitor's DPI to the DIP size.
        var bounds = GetWindowBoundsPhysical();
        MovePhysical(work.Left + Math.Max(0, work.Width - bounds.Width) * relativeX,
            work.Top + Math.Max(0, work.Height - bounds.Height) * relativeY);
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_disposed) return;
            var resized = GetWindowBoundsPhysical();
            MovePhysical(work.Left + Math.Max(0, work.Width - resized.Width) * relativeX,
                work.Top + Math.Max(0, work.Height - resized.Height) * relativeY);
        }));
    }

    /// <summary>Clamp the entire character canvas after display changes or resizing.</summary>
    public void KeepVisible()
    {
        EnsureAttached();
        var bounds = GetWindowBoundsPhysical();
        var work = Screen.FromHandle(_handle).WorkingArea;
        var x = Math.Clamp(bounds.X, work.Left, work.Left + Math.Max(0, work.Width - bounds.Width));
        var y = Math.Clamp(bounds.Y, work.Top, work.Top + Math.Max(0, work.Height - bounds.Height));
        if (Math.Abs(x - bounds.X) > 0.5 || Math.Abs(y - bounds.Y) > 0.5) MovePhysical(x, y);
    }

    /// <summary>Call only in response to an explicit change of the startup setting.</summary>
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? throw new IOException("无法打开当前用户的启动设置。");
        if (!enabled)
        {
            key.DeleteValue("TablePet", throwOnMissingValue: false);
            return;
        }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
            throw new IOException("无法确定当前应用程序的位置。");
        var command = $"\"{executable}\"";
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            command += $" \"{Assembly.GetEntryAssembly()?.Location ?? throw new IOException("无法确定应用程序入口。")}\"";
        key.SetValue("TablePet", command, RegistryValueKind.String);
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return 3; // MA_NOACTIVATE: deliver the mouse event without stealing keyboard focus.
        }
        if (message == 0x02E0) // WM_DPICHANGED; let WPF apply the suggested rectangle first.
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_disposed) DpiChanged?.Invoke();
            }));
        return 0;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatch(() =>
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            _sessionLocked = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _sessionLocked = false;
        PublishSuspension();
    });

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) => Dispatch(() =>
    {
        if (e.Mode == PowerModes.Suspend) _powerSuspended = true;
        else if (e.Mode == PowerModes.Resume) _powerSuspended = false;
        PublishSuspension();
    });

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatch(() =>
    {
        KeepVisible();
        DisplayChanged?.Invoke();
    });

    private void PublishSuspension()
    {
        var suspended = _sessionLocked || _powerSuspended;
        if (suspended == _lastSuspension) return;
        _lastSuspension = suspended;
        SuspensionChanged?.Invoke(suspended);
    }

    private void Dispatch(Action action)
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }

    private void EnsureAttached()
    {
        _window.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle == 0) Attach();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _source?.RemoveHook(WindowProc);
        _source = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hwnd, int index, int value);
    private static nint GetWindowLongPtr(nint hwnd, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new nint(GetWindowLong32(hwnd, index));
    private static nint SetWindowLongPtr(nint hwnd, int index, nint value) =>
        nint.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new nint(SetWindowLong32(hwnd, index, value.ToInt32()));
}
