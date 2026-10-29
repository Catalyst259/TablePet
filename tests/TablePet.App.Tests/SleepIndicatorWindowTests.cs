using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;

namespace TablePet.App.Tests;

[Collection("Native decoration windows")]
public sealed class SleepIndicatorWindowTests
{
    private const int ExtendedStyle = -20;
    private const long NoActivate = 0x08000000;
    private const long ToolWindow = 0x00000080;
    private const long Transparent = 0x00000020;
    private const long Layered = 0x00080000;
    private const long Topmost = 0x00000008;

    [Fact]
    public void DecorationIsNativeClickThroughReusesItsHandleAndClosesWithOwner()
    {
        WithWindows((owner, decoration) =>
        {
            var ownerHandle = new WindowInteropHelper(owner).Handle;
            var handle = new WindowInteropHelper(decoration).Handle;
            Assert.NotEqual(nint.Zero, handle);
            Assert.Same(owner, decoration.Owner);
            Assert.Equal(ownerHandle, GetWindow(handle, 4)); // GW_OWNER
            var required = NoActivate | ToolWindow | Transparent | Layered;
            Assert.Equal(required, GetStyle(handle) & required);
            Assert.True(IsWindowVisible(handle));
            Assert.False(decoration.IsHitTestVisible);

            decoration.Hide();
            DrainDispatcher();
            Assert.False(IsWindowVisible(handle));
            Assert.True(IsWindow(handle));

            decoration.Update(2200);
            DrainDispatcher();
            Assert.Equal(handle, new WindowInteropHelper(decoration).Handle);
            Assert.True(IsWindowVisible(handle));
            Assert.Equal(required, GetStyle(handle) & required);

            owner.Close();
            DrainDispatcher();
            Assert.False(IsWindow(handle));
            Assert.False(IsWindow(ownerHandle));
            // A queued render callback after the owner closes must not recreate a window.
            decoration.Update(2400);
            Assert.False(IsWindow(handle));
        });
    }

    [Fact]
    public void DecorationTracksPhysicalBoundsAndBothTopmostTransitions()
    {
        WithWindows((owner, decoration) =>
        {
            var ownerHandle = new WindowInteropHelper(owner).Handle;
            var decorationHandle = new WindowInteropHelper(decoration).Handle;
            AssertMatchingBounds(ownerHandle, decorationHandle);
            var initialBounds = Bounds(ownerHandle);

            owner.Left += 83;
            owner.Top += 57;
            owner.Width = 360;
            owner.Height = 300;
            DrainDispatcher();
            decoration.SyncBounds();
            DrainDispatcher();
            Assert.NotEqual(initialBounds, Bounds(ownerHandle));
            AssertMatchingBounds(ownerHandle, decorationHandle);

            foreach (var topmost in new[] { true, false })
            {
                owner.Topmost = topmost;
                decoration.SyncBounds();
                DrainDispatcher();
                Assert.Equal(topmost, (GetStyle(ownerHandle) & Topmost) != 0);
                Assert.Equal(topmost, (GetStyle(decorationHandle) & Topmost) != 0);
                AssertMatchingBounds(ownerHandle, decorationHandle);
            }

            // Native owner/owned-window Z-order operations can leave the WPF
            // requested property unchanged while altering WS_EX_TOPMOST. Repair
            // both directions without changing the user's requested setting.
            foreach (var requested in new[] { false, true })
            {
                owner.Topmost = requested;
                decoration.SyncBounds();
                Assert.True(SetWindowPos(ownerHandle, new nint(requested ? -2 : -1), 0, 0, 0, 0,
                    0x0001 | 0x0002 | 0x0010 | 0x0200));
                Assert.Equal(!requested, (GetStyle(ownerHandle) & Topmost) != 0);
                Assert.Equal(requested, owner.Topmost);
                decoration.SyncBounds();
                DrainDispatcher();
                Assert.Equal(requested, (GetStyle(ownerHandle) & Topmost) != 0);
                Assert.Equal(requested, (GetStyle(decorationHandle) & Topmost) != 0);
            }

            // A hidden decoration is aligned again when it reappears, even if
            // the owner moved and changed size while there was no render callback.
            decoration.Hide();
            owner.Left += 41;
            owner.Top -= 29;
            owner.Width = 180;
            owner.Height = 180;
            DrainDispatcher();
            decoration.Update(1800);
            DrainDispatcher();
            Assert.Equal(decorationHandle, new WindowInteropHelper(decoration).Handle);
            AssertMatchingBounds(ownerHandle, decorationHandle);
        });
    }

    [Fact]
    public void ShowingDecorationPreservesOcclusionOfNonTopmostOwner()
    {
        Sta.Run(() =>
        {
            var owner = CreateOffscreenWindow();
            var cover = CreateOffscreenWindow();
            SleepIndicatorWindow? decoration = null;
            var decorationClosed = false;
            try
            {
                owner.Show();
                DrainDispatcher();
                cover.Show();
                DrainDispatcher();
                var ownerHandle = new WindowInteropHelper(owner).Handle;
                var coverHandle = new WindowInteropHelper(cover).Handle;
                // Establish an ordinary application window above the sleeping
                // pet without activating either test window or moving the cursor.
                Assert.True(SetWindowPos(coverHandle, nint.Zero, 0, 0, 0, 0,
                    0x0001 | 0x0002 | 0x0010)); // NOSIZE | NOMOVE | NOACTIVATE; HWND_TOP
                DrainDispatcher();
                AssertAbove(coverHandle, ownerHandle, "The cover must initially occlude the owner.");
                Assert.Equal(0, GetStyle(ownerHandle) & Topmost);
                Assert.Equal(0, GetStyle(coverHandle) & Topmost);

                decoration = new SleepIndicatorWindow(owner);
                decoration.Closed += (_, _) => decorationClosed = true;
                decoration.Update(1800);
                DrainDispatcher();
                var decorationHandle = new WindowInteropHelper(decoration).Handle;
                Assert.True(IsWindowVisible(decorationHandle));
                AssertAbove(coverHandle, ownerHandle,
                    "First showing the decoration must not raise its owner above the covering window.");
                AssertAbove(coverHandle, decorationHandle,
                    "First showing the decoration must keep the letters below the covering window.");
                AssertAbove(decorationHandle, ownerHandle,
                    "The decoration must remain above its owner within the occluded group.");

                decoration.Hide();
                DrainDispatcher();
                AssertAbove(coverHandle, ownerHandle, "Hiding the decoration must preserve owner occlusion.");
                decoration.Update(2200);
                DrainDispatcher();
                Assert.Equal(decorationHandle, new WindowInteropHelper(decoration).Handle);
                Assert.True(IsWindowVisible(decorationHandle));
                AssertAbove(coverHandle, ownerHandle,
                    "Showing the decoration again must not raise its owner above the covering window.");
                AssertAbove(coverHandle, decorationHandle,
                    "Showing the decoration again must keep the letters below the covering window.");
                AssertAbove(decorationHandle, ownerHandle,
                    "After reappearing the decoration must still sit above its owner.");
            }
            finally
            {
                try
                {
                    if (decoration is not null && !decorationClosed) decoration.Close();
                }
                finally
                {
                    try { cover.Close(); }
                    finally { owner.Close(); DrainDispatcher(); }
                }
            }
        });
    }

    private static void WithWindows(Action<Window, SleepIndicatorWindow> check) => Sta.Run(() =>
    {
        // No global Application, tray, pet instance, or settings are created.
        // Keep both HWNDs well outside the virtual desktop during native checks.
        var owner = CreateOffscreenWindow();
        SleepIndicatorWindow? decoration = null;
        var ownerClosed = false;
        var decorationClosed = false;
        owner.Closed += (_, _) => ownerClosed = true;
        try
        {
            owner.Show();
            DrainDispatcher();
            decoration = new SleepIndicatorWindow(owner);
            decoration.Closed += (_, _) => decorationClosed = true;
            decoration.Update(1800);
            DrainDispatcher();
            check(owner, decoration);
        }
        finally
        {
            try
            {
                if (decoration is not null && !decorationClosed) decoration.Close();
            }
            finally
            {
                if (!ownerClosed) owner.Close();
                DrainDispatcher();
            }
        }
    });

    private static Window CreateOffscreenWindow()
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            Topmost = false,
            Left = SystemParameters.VirtualScreenLeft - 2000,
            Top = SystemParameters.VirtualScreenTop - 2000,
            Width = 240,
            Height = 240
        };
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            SetStyle(handle, GetStyle(handle) | NoActivate | ToolWindow);
        };
        return window;
    }

    private static void AssertAbove(nint upper, nint lower, string message)
    {
        var seen = new HashSet<nint>();
        for (var next = GetWindow(upper, 2); next != nint.Zero && seen.Add(next); next = GetWindow(next, 2)) // GW_HWNDNEXT
            if (next == lower) return;
        Assert.Fail(message);
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void AssertMatchingBounds(nint owner, nint decoration) =>
        Assert.Equal(Bounds(owner), Bounds(decoration));

    private static (int Left, int Top, int Right, int Bottom) Bounds(nint handle)
    {
        Assert.True(GetWindowRect(handle, out var bounds));
        return (bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    private static long GetStyle(nint handle) => nint.Size == 8
        ? GetWindowLongPtr64(handle, ExtendedStyle).ToInt64()
        : GetWindowLong32(handle, ExtendedStyle);

    private static void SetStyle(nint handle, long style)
    {
        if (nint.Size == 8) SetWindowLongPtr64(handle, ExtendedStyle, new nint(style));
        else SetWindowLong32(handle, ExtendedStyle, unchecked((int)style));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect bounds);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint handle, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint handle, int index, int value);
}
