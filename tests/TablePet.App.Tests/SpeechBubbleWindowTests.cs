using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace TablePet.App.Tests;

[Collection("Native decoration windows")]
public sealed class SpeechBubbleWindowTests
{
    [Fact]
    public void BubbleFollowsMouthTimingAndRendersOnlyInUpperRight()
    {
        WithWindows((owner, bubble) =>
        {
            bubble.Update(0);
            bubble.Update(180);
            Assert.False(bubble.IsVisible);
            bubble.Update(500);
            DrainDispatcher();
            Assert.True(bubble.IsVisible);
            var full = Render(bubble);
            Assert.True(full.Alpha > 100_000);
            Assert.InRange(full.Left, 179, 183);
            Assert.InRange(full.Top, 50, 54);
            Assert.InRange(full.Right, 230, 234);
            Assert.InRange(full.Bottom, 94, 100);

            bubble.Update(1250);
            DrainDispatcher();
            var fading = Render(bubble);
            Assert.InRange(fading.Alpha, full.Alpha * 0.45, full.Alpha * 0.55);
            // The same elapsed time is the same rendered image: pausing the pet
            // never advances a separate timer or leaves a drifting speech bubble.
            bubble.Update(1250);
            DrainDispatcher();
            Assert.Equal(fading, Render(bubble));
            bubble.Update(1400);
            Assert.False(bubble.IsVisible);
            bubble.Update(500);
            DrainDispatcher();
            Assert.Equal(full, Render(bubble));
        });
    }

    [Fact]
    public void BubbleIsNativeClickThroughTracksOwnerAndClosesWithIt()
    {
        WithWindows((owner, bubble) =>
        {
            bubble.Update(500);
            DrainDispatcher();
            var handle = new WindowInteropHelper(bubble).Handle;
            var ownerHandle = new WindowInteropHelper(owner).Handle;
            const long required = 0x08000000 | 0x00000080 | 0x00000020 | 0x00080000;
            Assert.Equal(required, GetStyle(handle) & required);
            Assert.Equal(ownerHandle, GetWindow(handle, 4));
            Assert.False(bubble.IsHitTestVisible);
            Assert.False(bubble.ShowActivated);
            Assert.Equal(Bounds(ownerHandle), Bounds(handle));
            // Query the centre of the visibly opaque cream bubble, not an empty
            // corner of its transparent HWND. No mouse movement or click is sent.
            var bubblePoint = bubble.PointToScreen(new Point(207, 69));
            Assert.Equal(ownerHandle, WindowFromPoint(new NativePoint
            {
                X = (int)Math.Round(bubblePoint.X), Y = (int)Math.Round(bubblePoint.Y)
            }));

            owner.Left += 47; owner.Top += 31;
            owner.Width = owner.Height = 360;
            owner.Topmost = true;
            DrainDispatcher();
            bubble.SyncBounds();
            Assert.Equal(Bounds(ownerHandle), Bounds(handle));
            Assert.NotEqual(0, GetStyle(handle) & 8);
            owner.Topmost = false;
            bubble.SyncBounds();
            Assert.Equal(0, GetStyle(handle) & 8);

            owner.Hide();
            bubble.Update(600);
            Assert.False(bubble.IsVisible);
            owner.Show();
            bubble.Update(600);
            DrainDispatcher();
            Assert.Equal(handle, new WindowInteropHelper(bubble).Handle);
            Assert.True(bubble.IsVisible);
            Assert.Equal(required, GetStyle(handle) & required);
            owner.Close();
            DrainDispatcher();
            Assert.False(IsWindow(handle));
            bubble.Update(700);
            Assert.False(IsWindow(handle));
        });
    }

    private static void WithWindows(Action<Window, SpeechBubbleWindow> check) => Sta.Run(() =>
    {
        var owner = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false,
            ShowActivated = false, Focusable = false,
            // Keep a separate native hit-test area from the sleep-indicator test
            // class, which xUnit may run concurrently on another STA thread.
            Left = SystemParameters.VirtualScreenLeft - 6000,
            Top = SystemParameters.VirtualScreenTop - 6000, Width = 240, Height = 240
        };
        var ownerClosed = false;
        var bubbleClosed = false;
        SpeechBubbleWindow? bubble = null;
        owner.Closed += (_, _) => ownerClosed = true;
        try
        {
            owner.Show();
            DrainDispatcher();
            bubble = new SpeechBubbleWindow(owner);
            bubble.Closed += (_, _) => bubbleClosed = true;
            check(owner, bubble);
        }
        finally
        {
            if (bubble is not null && !bubbleClosed) bubble.Close();
            if (!ownerClosed) owner.Close();
            DrainDispatcher();
        }
    });

    private static (long Alpha, int Left, int Top, int Right, int Bottom) Render(SpeechBubbleWindow window)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(240, 240, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var pixels = new byte[240 * 240 * 4];
        bitmap.CopyPixels(pixels, 240 * 4, 0);
        long alpha = 0;
        var left = 240; var top = 240; var right = -1; var bottom = -1;
        for (var y = 0; y < 240; y++)
            for (var x = 0; x < 240; x++)
            {
                var a = pixels[(y * 240 + x) * 4 + 3];
                alpha += a;
                if (a < 8) continue;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        return (alpha, left, top, right, bottom);
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static (int Left, int Top, int Right, int Bottom) Bounds(nint handle)
    {
        Assert.True(GetWindowRect(handle, out var bounds));
        return (bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    private static long GetStyle(nint handle) => nint.Size == 8
        ? GetWindowLongPtr64(handle, -20).ToInt64() : GetWindowLong32(handle, -20);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect bounds);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint handle, int index);
}
