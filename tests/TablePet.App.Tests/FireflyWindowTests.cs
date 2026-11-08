using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

[Collection("Native decoration windows")]
public sealed class FireflyWindowTests
{
    private const long NoActivate = 0x08000000;
    private const long ToolWindow = 0x00000080;
    private const long Transparent = 0x00000020;
    private const long Layered = 0x00080000;
    private const long Topmost = 0x00000008;

    [Fact]
    public void AutomaticFireflyUsesTheCatIndexThroughDragResumeAndCompletion()
    {
        WithWindows((_, _, firefly, catalog) =>
        {
            var clock = new SampleClock();
            var behavior = new BehaviorController(clock, catalog.Clips,
                new PetSettings { AutoSleep = false, BlinkMinMs = 100, BlinkMaxMs = 100 },
                (_, maximum) => maximum, [new("blink", 2), new("firefly", 1)]);
            void Advance(long milliseconds)
            {
                clock.NowMilliseconds += milliseconds;
                behavior.Tick();
                if (behavior.IsFireflyActive) firefly.Update(behavior.Player.FrameIndex);
                else firefly.Hide();
                DrainDispatcher();
            }

            Advance(100);
            Assert.Equal(PetState.Idle, behavior.State);
            Assert.True(behavior.IsAutomaticActionActive);
            Assert.True(behavior.IsFireflyActive);
            Assert.Same(catalog.Clips["firefly"], behavior.Player.CurrentClip);
            Assert.Equal(0, behavior.Player.FrameIndex);
            Assert.Equal(0, AlphaSum(Render(firefly)));
            Advance(100);
            Assert.Equal(1, behavior.Player.FrameIndex);
            var visible = Render(firefly);
            Assert.True(AlphaSum(visible) > 100_000);
            behavior.BeginDrag();
            Advance(1000);
            Assert.Equal(1, behavior.Player.FrameIndex);
            Assert.Equal(100, behavior.Player.ElapsedMs);
            Assert.Equal(visible, Render(firefly));
            behavior.EndDrag();
            Advance(100);
            Assert.Equal(2, behavior.Player.FrameIndex);
            Assert.Equal(0, AlphaSum(Render(firefly)));
            Advance(100);
            Assert.Equal("idle", behavior.Player.CurrentClip!.Name);
            Assert.False(behavior.IsFireflyActive);
            Assert.False(firefly.IsVisible);
            Assert.Equal(0, behavior.ReactionCooldownUntil);
        });
    }

    [Fact]
    public void FirstShowAndSubsequentMovesKeepTheExpandedPhysicalCanvasRegistered()
    {
        WithWindows((owner, _, firefly, _) =>
        {
            firefly.Update(1);
            DrainDispatcher();
            var handle = Handle(firefly);
            Assert.True(IsWindowVisible(handle));
            AssertRegisteredBounds(owner, firefly);

            // Non-square sizes exercise independent X/Y scaling and fractional
            // physical rounding, even on a single-DPI test computer.
            owner.Left += 83;
            owner.Top -= 57;
            owner.Width = 333;
            owner.Height = 271;
            DrainDispatcher();
            firefly.SyncBounds();
            AssertRegisteredBounds(owner, firefly);

            foreach (var topmost in new[] { true, false })
            {
                owner.Topmost = topmost;
                firefly.SyncBounds();
                DrainDispatcher();
                Assert.Equal(topmost, (GetStyle(Handle(owner)) & Topmost) != 0);
                Assert.Equal(topmost, (GetStyle(handle) & Topmost) != 0);
                AssertRegisteredBounds(owner, firefly);
            }

            firefly.Hide();
            owner.Left -= 41;
            owner.Top += 29;
            owner.Width = 181;
            owner.Height = 207;
            DrainDispatcher();
            firefly.Update(1);
            DrainDispatcher();
            Assert.Equal(handle, Handle(firefly));
            Assert.True(IsWindowVisible(handle));
            AssertRegisteredBounds(owner, firefly);
        });
    }

    [Fact]
    public void RepeatedSlotsAreStableInvalidIndicesHideAndOwnerCloseCannotRecreateTheWindow()
    {
        WithWindows((owner, _, firefly, catalog) =>
        {
            firefly.Update(0);
            DrainDispatcher();
            var handle = Handle(firefly);
            Assert.Equal(0, AlphaSum(Render(firefly)));

            firefly.Update(1);
            DrainDispatcher();
            var visible = Render(firefly);
            Assert.True(AlphaSum(visible) > 100_000);
            var image = Assert.IsType<Image>(firefly.Content);
            var source = image.Source;
            var misses = catalog.SynchronousCacheMisses;
            firefly.Update(1);
            DrainDispatcher();
            Assert.Same(source, image.Source);
            Assert.Equal(visible, Render(firefly));
            Assert.Equal(misses, catalog.SynchronousCacheMisses);

            foreach (var invalidIndex in new[] { -1, 3 })
            {
                firefly.Update(invalidIndex);
                Assert.False(firefly.IsVisible);
                Assert.False(IsWindowVisible(handle));
                firefly.Update(1);
                DrainDispatcher();
                Assert.Equal(handle, Handle(firefly));
                Assert.True(IsWindowVisible(handle));
                Assert.Equal(visible, Render(firefly));
            }

            firefly.Update(2);
            Assert.Equal(0, AlphaSum(Render(firefly)));
            owner.Hide();
            firefly.Update(1);
            Assert.False(firefly.IsVisible);
            owner.Show();
            firefly.Update(1);
            DrainDispatcher();
            Assert.Equal(handle, Handle(firefly));
            Assert.True(IsWindowVisible(handle));

            var ownerHandle = Handle(owner);
            owner.Close();
            DrainDispatcher();
            Assert.False(IsWindow(ownerHandle));
            Assert.False(IsWindow(handle));
            firefly.Update(1);
            firefly.SyncBounds();
            Assert.False(IsWindow(handle));
        });
    }

    [Fact]
    public void VisiblePixelsPassNativeHitTestingToOwnerAndToAnUnderlyingWindowOutsideTheOwner()
    {
        WithWindows((owner, underlay, firefly, catalog) =>
        {
            firefly.Update(1);
            DrainDispatcher();
            var handle = Handle(firefly);
            const long required = NoActivate | ToolWindow | Transparent | Layered;
            Assert.Equal(required, GetStyle(handle) & required);
            Assert.Equal(Handle(owner), GetWindow(handle, 4)); // GW_OWNER
            Assert.False(firefly.IsHitTestVisible);
            Assert.False(firefly.ShowActivated);
            Assert.False(firefly.Focusable);
            AssertRegisteredBounds(owner, firefly);

            // Both tested centres are opaque pixels in the fixture, so this
            // checks WS_EX_TRANSPARENT rather than only layered alpha hit testing.
            var pixels = new byte[640 * 640 * 4];
            catalog.Image("insect.png").CopyPixels(pixels, 640 * 4, 0);
            Assert.Equal(255, pixels[(320 * 640 + 320) * 4 + 3]);
            Assert.Equal(255, pixels[(28 * 640 + 320) * 4 + 3]);
            var inside = CanvasPoint(firefly, 320, 320);
            var outside = CanvasPoint(firefly, 320, 28);
            Assert.True(Contains(Bounds(Handle(owner)), inside));
            Assert.False(Contains(Bounds(Handle(owner)), outside));
            Assert.True(Contains(Bounds(Handle(underlay)), outside));
            Assert.Equal(Handle(owner), WindowFromPoint(inside));
            Assert.Equal(Handle(underlay), WindowFromPoint(outside));
        });
    }

    [Fact]
    public void ShowingTheExpandedOverlayPreservesAnExternalCoverAboveTheOwnerGroup()
    {
        WithWindows((owner, cover, firefly, _) =>
        {
            const uint flags = 0x0001 | 0x0002 | 0x0010; // NOSIZE | NOMOVE | NOACTIVATE
            Assert.True(SetWindowPos(Handle(cover), nint.Zero, 0, 0, 0, 0, flags));
            DrainDispatcher();
            AssertAbove(Handle(cover), Handle(owner));
            foreach (var frameIndex in new[] { 1, 0, 1 })
            {
                firefly.Update(frameIndex);
                DrainDispatcher();
                AssertAbove(Handle(cover), Handle(owner));
                AssertAbove(Handle(cover), Handle(firefly));
                AssertAbove(Handle(firefly), Handle(owner));
                AssertRegisteredBounds(owner, firefly);
                firefly.Hide();
                DrainDispatcher();
            }
        });
    }

    private static void WithWindows(Action<Window, Window, FireflyWindow, AssetCatalog> check) => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        WritePack(directory);
        var catalog = new AssetCatalog(directory.Root);
        Assert.False(catalog.IsFallback);
        Assert.False(catalog.HasOptionalActionFallback);
        catalog.CacheForSize(240, 1.5);
        var owner = CreateWindow(SystemParameters.VirtualScreenLeft - 10000, SystemParameters.VirtualScreenTop - 10000, 240, 240);
        var underlay = CreateWindow(owner.Left - 200, owner.Top - 200, 640, 640);
        var ownerClosed = false;
        var fireflyClosed = false;
        FireflyWindow? firefly = null;
        owner.Closed += (_, _) => ownerClosed = true;
        try
        {
            // No application singleton, settings, tray or physical input is used.
            // Keep all native windows outside the virtual desktop.
            underlay.Show();
            owner.Show();
            DrainDispatcher();
            firefly = new FireflyWindow(owner, catalog, catalog.Decorations["firefly"]);
            firefly.Closed += (_, _) => fireflyClosed = true;
            check(owner, underlay, firefly, catalog);
            Assert.False(catalog.HasFrameFallback);
        }
        finally
        {
            catalog.StopPrefetch();
            try
            {
                if (firefly is not null && !fireflyClosed) firefly.Close();
            }
            finally
            {
                try { if (!ownerClosed) owner.Close(); }
                finally { underlay.Close(); DrainDispatcher(); }
            }
        }
    });

    private static Window CreateWindow(double left, double top, double width, double height) => new()
    {
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.Manual,
        ShowInTaskbar = false,
        ShowActivated = false,
        Focusable = false,
        Background = Brushes.SlateGray,
        Left = left, Top = top, Width = width, Height = height
    };

    private static void WritePack(TestDirectory directory)
    {
        WritePng(directory.FilePath("rest.png"), 512, new Int32Rect(128, 128, 256, 256));
        WritePng(directory.FilePath("cat.png"), 512, new Int32Rect(112, 128, 288, 256));
        WritePng(directory.FilePath("transparent.png"), 640);
        WritePng(directory.FilePath("insect.png"), 640,
            new Int32Rect(304, 304, 32, 32), new Int32Rect(304, 12, 32, 32));
        object Clip(params string[] files) => new
        {
            loop = false,
            frames = files.Select(file => new { file, durationMs = 100 }).ToArray()
        };
        var clips = new Dictionary<string, object>();
        foreach (var name in new[] { "idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle" })
            clips[name] = Clip("rest.png");
        clips["firefly"] = Clip("rest.png", "cat.png", "rest.png");
        var manifest = new
        {
            canvas = new { width = 512, height = 512 },
            clips,
            decorations = new
            {
                firefly = new
                {
                    canvas = new { width = 640, height = 640 },
                    offset = new { x = -64, y = -100 },
                    loop = false,
                    frames = new[] { "transparent.png", "insect.png", "transparent.png" }
                        .Select(file => new { file, durationMs = 100 }).ToArray()
                }
            }
        };
        File.WriteAllText(directory.FilePath("manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static void WritePng(string path, int size, params Int32Rect[] opaqueRegions)
    {
        var pixels = new byte[size * size * 4];
        foreach (var region in opaqueRegions)
            for (var y = region.Y; y < region.Y + region.Height; y++)
                for (var x = region.X; x < region.X + region.Width; x++)
                {
                    var index = (y * size + x) * 4;
                    pixels[index] = 35; pixels[index + 1] = 215;
                    pixels[index + 2] = 255; pixels[index + 3] = 255;
                }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static byte[] Render(FireflyWindow firefly)
    {
        var content = (FrameworkElement)firefly.Content;
        content.Measure(new Size(640, 640));
        content.Arrange(new Rect(0, 0, 640, 640));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(640, 640, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var pixels = new byte[640 * 640 * 4];
        bitmap.CopyPixels(pixels, 640 * 4, 0);
        return pixels;
    }

    private static long AlphaSum(byte[] pixels)
    {
        long alpha = 0;
        for (var index = 3; index < pixels.Length; index += 4) alpha += pixels[index];
        return alpha;
    }

    private static void AssertRegisteredBounds(Window owner, FireflyWindow firefly)
    {
        var body = Bounds(Handle(owner));
        var width = body.Right - body.Left;
        var height = body.Bottom - body.Top;
        var left = (int)Math.Round(body.Left - width * 64d / 512);
        var top = (int)Math.Round(body.Top - height * 100d / 512);
        var right = left + (int)Math.Round(width * 640d / 512);
        var bottom = top + (int)Math.Round(height * 640d / 512);
        Assert.Equal((left, top, right, bottom), Bounds(Handle(firefly)));
    }

    private static NativePoint CanvasPoint(FireflyWindow firefly, int x, int y)
    {
        var bounds = Bounds(Handle(firefly));
        return new NativePoint
        {
            X = bounds.Left + (int)Math.Round((bounds.Right - bounds.Left) * x / 640d),
            Y = bounds.Top + (int)Math.Round((bounds.Bottom - bounds.Top) * y / 640d)
        };
    }

    private static bool Contains((int Left, int Top, int Right, int Bottom) bounds, NativePoint point) =>
        point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;

    private static void AssertAbove(nint upper, nint lower)
    {
        var seen = new HashSet<nint>();
        for (var next = GetWindow(upper, 2); next != nint.Zero && seen.Add(next); next = GetWindow(next, 2))
            if (next == lower) return;
        Assert.Fail("The expected native window must remain above the other window.");
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static nint Handle(Window window) => new WindowInteropHelper(window).Handle;
    private sealed class SampleClock : IClock { public long NowMilliseconds { get; set; } }
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
}
