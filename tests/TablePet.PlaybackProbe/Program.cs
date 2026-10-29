using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TablePet.App;
using TablePet.App.Services;
using TablePet.Core;

// Exercises the actual WPF decoder/cache and animation clock without a tray, settings,
// input injection or instance signals. Render mode uses a nonactivating offscreen owner
// for the real speech overlay. Neither mode measures DWM presentation.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: TablePet.PlaybackProbe <asset-root> [sample-hz=240] [height-dip=240] [dpi=1.5]");
            Console.Error.WriteLine("       TablePet.PlaybackProbe --render-meow <asset-root> <output-folder>");
            Console.Error.WriteLine("       TablePet.PlaybackProbe --interactive-meow <asset-root> <temp-settings-dir>");
            return 1;
        }
        if (args[0] == "--render-meow")
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("Usage: TablePet.PlaybackProbe --render-meow <asset-root> <output-folder>");
                return 1;
            }
            return RenderMeow(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        }
        if (args[0] == "--interactive-meow")
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("Usage: TablePet.PlaybackProbe --interactive-meow <asset-root> <temp-settings-dir>");
                return 1;
            }
            return InteractiveMeow(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        }
        var hz = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 240;
        var height = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 240;
        var dpi = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 1.5;
        if (hz is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(hz));
        var initialization = Stopwatch.StartNew();
        var catalog = new AssetCatalog(Path.GetFullPath(args[0]));
        catalog.CacheForSize(height, dpi);
        initialization.Stop();
        var clock = new StopwatchClock();
        var player = new AnimationPlayer(clock);
        var results = new List<object>();
        var fetchTimes = new List<double>();
        var actualIntervals = new List<double>();
        var rendered = 0;
        long maxCache = catalog.CachedBytes;
        player.FrameChanged += frame =>
        {
            var start = Stopwatch.GetTimestamp();
            catalog.PreparePlayback(player.CurrentClip!, player.FrameIndex);
            var bitmap = catalog.Image(frame.File);
            if (!bitmap.IsFrozen) throw new InvalidOperationException("The decoder returned a thread-affine image.");
            fetchTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            maxCache = Math.Max(maxCache, catalog.CachedBytes);
            rendered++;
        };
        foreach (var name in new[] { "rise", "hold", "settle", "fall_asleep", "sleep_loop", "wake" }
            .Concat(catalog.Clips.ContainsKey("wink") ? new[] { "wink" } : [])
            .Concat(catalog.Clips.ContainsKey("meow") ? new[] { "meow" } : [])
            .Concat(catalog.IdleActions.Select(action => action.Clip)).Distinct())
        {
            fetchTimes.Clear(); actualIntervals.Clear(); rendered = 0;
            var beforeMisses = catalog.SynchronousCacheMisses;
            var clip = catalog.Clips[name];
            var timer = Stopwatch.StartNew();
            player.Play(clip);
            long previousSample = Stopwatch.GetTimestamp();
            var sample = 1;
            while (timer.Elapsed.TotalMilliseconds < clip.DurationMs)
            {
                var target = sample * 1000d / hz;
                while (timer.Elapsed.TotalMilliseconds < target)
                {
                    // Windows may round Sleep(1) to ~16 ms. Active wait is deliberate
                    // in this short-lived probe, never used by the desktop application.
                    if (target - timer.Elapsed.TotalMilliseconds > 20) Thread.Sleep(1);
                    else Thread.SpinWait(32);
                }
                var now = Stopwatch.GetTimestamp();
                actualIntervals.Add((now - previousSample) * 1000d / Stopwatch.Frequency);
                previousSample = now;
                player.Tick();
                // Skip expired sample slots after a slow decode instead of dispatching a burst.
                sample = Math.Max(sample + 1, (int)Math.Floor(timer.Elapsed.TotalMilliseconds * hz / 1000) + 1);
            }
            results.Add(new
            {
                clip = name,
                frames = clip.Frames.Count,
                durationMs = clip.DurationMs,
                elapsedMs = timer.Elapsed.TotalMilliseconds,
                samples = actualIntervals.Count,
                sampledHz = actualIntervals.Count * 1000d / timer.Elapsed.TotalMilliseconds,
                displayedFrameChanges = rendered,
                synchronousCacheMisses = catalog.SynchronousCacheMisses - beforeMisses,
                fetchP95Ms = Percentile(fetchTimes, .95),
                fetchMaxMs = fetchTimes.Max(),
                sampleIntervalP95Ms = Percentile(actualIntervals, .95),
                cachedFrames = catalog.CachedFrameCount,
                cachedMiB = catalog.CachedBytes / 1048576d
            });
        }
        catalog.StopPrefetch();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            passed = !catalog.IsFallback && !catalog.HasFrameFallback && !catalog.HasOptionalActionFallback && maxCache <= catalog.CacheBudgetBytes,
            requestedSampleHz = hz,
            measurementUsesActiveWait = true,
            heightDip = height,
            dpiScale = dpi,
            initializationMs = initialization.Elapsed.TotalMilliseconds,
            cacheBudgetMiB = catalog.CacheBudgetBytes / 1048576d,
            peakDecodedPixelCacheMiB = maxCache / 1048576d,
            processWorkingSetMiB = Process.GetCurrentProcess().WorkingSet64 / 1048576d,
            clips = results,
            limitation = "Headless timing/decoding probe only. The sampling rate is requested by this probe and does not establish actual window/compositor presentation FPS. Cache budget excludes WPF native allocations and transient decode/GC overhead."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return catalog.IsFallback || catalog.HasFrameFallback || catalog.HasOptionalActionFallback || maxCache > catalog.CacheBudgetBytes ? 2 : 0;
    }

    private static int RenderMeow(string assetRoot, string outputFolder)
    {
        var catalog = new AssetCatalog(assetRoot);
        if (catalog.IsFallback || catalog.HasOptionalActionFallback || !catalog.Clips.TryGetValue("meow", out var clip))
            throw new InvalidDataException("Render mode requires an installed, valid meow clip without asset fallback.");
        if (catalog.CanvasWidth != 512 || catalog.CanvasHeight != 512 || clip.DurationMs != 1400)
            throw new InvalidDataException("Render mode expects the 512-unit, 1400 ms meow artwork and speech timeline.");
        catalog.CacheForSize(512, 1);
        Directory.CreateDirectory(outputFolder);
        var sequenceFolder = Path.Combine(outputFolder, "sequence");
        Directory.CreateDirectory(sequenceFolder);
        var clock = new SampleClock();
        var player = new AnimationPlayer(clock);
        player.Play(clip);
        var owner = new Window
        {
            Title = "TablePet offscreen meow rendering probe",
            Width = 512,
            Height = 512,
            Left = SystemParameters.VirtualScreenLeft - 2048,
            Top = SystemParameters.VirtualScreenTop - 2048,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowActivated = false,
            ShowInTaskbar = false,
            Focusable = false,
            IsHitTestVisible = false
        };
        SpeechBubbleWindow? bubble = null;
        var samples = new List<(long Time, BitmapSource Image)>();
        var provenance = new List<object>();
        var contactTimes = new HashSet<long> { 0, 180, 240, 300, 650, 850, 1050, 1250, 1400 };
        var sequenceTimes = Enumerable.Range(0, 71).Select(index => (long)index * 20).ToArray();
        try
        {
            owner.Show();
            bubble = new SpeechBubbleWindow(owner);
            foreach (var time in sequenceTimes.Concat(contactTimes).Distinct().Order())
            {
                clock.NowMilliseconds = time;
                player.Tick();
                catalog.PreparePlayback(clip, player.FrameIndex);
                var frame = player.CurrentFrame!;
                var body = catalog.Image(frame.File);
                bubble.Update(time);
                // Show initializes the real overlay, including its runtime content and
                // vector transforms. Drain layout before sampling that public Content.
                bubble.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                BitmapSource? speech = null;
                if (bubble.IsVisible)
                {
                    var content = (FrameworkElement)bubble.Content;
                    content.Measure(new Size(512, 512));
                    content.Arrange(new Rect(0, 0, 512, 512));
                    content.UpdateLayout();
                    var overlay = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32);
                    overlay.Render(content);
                    overlay.Freeze();
                    speech = overlay;
                }
                var composite = Draw(512, 512, drawing =>
                {
                    drawing.DrawImage(body, new Rect(0, 0, 512, 512));
                    if (speech is not null) drawing.DrawImage(speech, new Rect(0, 0, 512, 512));
                });
                var filename = $"meow-{time:0000}.png";
                if (time % 20 == 0) SavePng(composite, Path.Combine(sequenceFolder, filename));
                if (contactTimes.Contains(time))
                {
                    SavePng(composite, Path.Combine(outputFolder, filename));
                    samples.Add((time, composite));
                    provenance.Add(new { timeMs = time, frameIndex = player.FrameIndex, source = frame.File,
                        bubbleVisible = bubble.IsVisible, rendered = filename });
                }
            }
            WriteContactSheet(samples, Color.FromRgb(0xff, 0xf8, 0xef), Brushes.DarkSlateGray,
                Path.Combine(outputFolder, "contact-light.png"));
            WriteContactSheet(samples, Color.FromRgb(0x24, 0x22, 0x2a), Brushes.WhiteSmoke,
                Path.Combine(outputFolder, "contact-dark.png"));
            var report = JsonSerializer.Serialize(new
            {
                passed = !catalog.IsFallback && !catalog.HasOptionalActionFallback && !catalog.HasFrameFallback,
                assetRoot,
                manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(assetRoot, "manifest.json")))),
                clip = clip.Name,
                durationMs = clip.DurationMs,
                sampledCanvasPixels = 512,
                sequence = new { folder = "sequence", frameCount = sequenceTimes.Length, stepMs = 20, includesEndPose = true },
                renderer = "WPF RenderTargetBitmap with AssetCatalog PNGs and actual SpeechBubbleWindow.Content",
                limitation = "Deterministic offscreen composition, not a screenshot or measurement of compositor presentation.",
                samples = provenance
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outputFolder, "render-report.json"), report);
            Console.WriteLine(report);
            return catalog.HasFrameFallback ? 2 : 0;
        }
        finally
        {
            catalog.StopPrefetch();
            bubble?.Close();
            owner.Close();
        }
    }

    private static int InteractiveMeow(string assetRoot, string settingsFolder)
    {
        var catalog = new AssetCatalog(assetRoot);
        if (catalog.IsFallback || catalog.HasOptionalActionFallback || !catalog.Clips.ContainsKey("meow"))
            throw new InvalidDataException("Interactive QA requires the installed meow assets.");
        Directory.CreateDirectory(settingsFolder);
        using var settings = new SettingsService(Path.Combine(settingsFolder, "qa-settings.json"));
        settings.Update(new PetSettings
        {
            AutoSleep = false,
            BlinkMinMs = 86400000,
            BlinkMaxMs = 86400000,
            PetHeightDip = 480,
            Topmost = false,
            ClickThrough = false,
            StartWithWindows = false
        }, saveImmediately: true);
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var instructions = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Top };
        instructions.Children.Add(new TextBlock { Text = "Double-click the cat", FontSize = 24, FontWeight = FontWeights.SemiBold });
        instructions.Children.Add(new TextBlock { Text = "Actual PetWindow · isolated QA settings · close this window to stop", FontSize = 14, Margin = new Thickness(0, 8, 0, 0) });
        var status = new TextBlock { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Bottom,
            FontSize = 14, TextWrapping = TextWrapping.Wrap, Text = "Starting actual pet window…" };
        var canvas = new Grid();
        canvas.Children.Add(instructions);
        canvas.Children.Add(status);
        var host = new Window
        {
            Title = "TablePet Meow QA",
            Width = 800,
            Height = 680,
            MinWidth = 640,
            MinHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Background = new SolidColorBrush(Color.FromRgb(0xff, 0xf8, 0xef)),
            Content = canvas
        };
        PetWindow? pet = null;
        BehaviorController? behavior = null;
        var lastReaction = "none";
        var observedToken = -1L;
        var tracePath = Path.Combine(settingsFolder, "interaction-trace.jsonl");
        var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        statusTimer.Tick += (_, _) =>
        {
            if (behavior is null) return;
            status.Text = $"State: {behavior.State} · phase: {behavior.ReactionPhase} · clip: {behavior.Player.CurrentClip?.Name} · elapsed: {behavior.Player.ElapsedMs} ms\nLast reaction: {lastReaction}";
        };
        var stopping = false;
        void AlignPet()
        {
            if (pet is null || !pet.IsVisible || !host.IsVisible) return;
            var point = host.PointToScreen(new Point((host.ActualWidth - pet.Width) / 2, 100));
            var dpi = VisualTreeHelper.GetDpi(host);
            pet.Left = point.X / dpi.DpiScaleX;
            pet.Top = point.Y / dpi.DpiScaleY;
        }
        host.Loaded += (_, _) =>
        {
            pet = new PetWindow(settings, catalog) { Owner = host };
            // Dev-only observation of the real state machine; no input or state is
            // injected. Retain evidence even when a UI screenshot arrives after Meow.
            behavior = (BehaviorController?)typeof(PetWindow).GetField("_behavior", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(pet)
                ?? throw new InvalidOperationException("The QA observer could not find the pet behavior controller.");
            behavior.Player.FrameChanged += _ =>
            {
                if (observedToken == behavior.Player.Token) return;
                observedToken = behavior.Player.Token;
                if (behavior.State == PetState.React) lastReaction = behavior.Player.CurrentClip!.Name;
                var observation = JsonSerializer.Serialize(new { atUtc = DateTimeOffset.UtcNow,
                    state = behavior.State.ToString(), phase = behavior.ReactionPhase.ToString(),
                    clip = behavior.Player.CurrentClip!.Name, token = observedToken });
                File.AppendAllText(tracePath, observation + Environment.NewLine);
                Console.WriteLine(observation);
            };
            pet.Loaded += (_, _) =>
            {
                AlignPet();
                Console.WriteLine(JsonSerializer.Serialize(new { ready = true, title = host.Title,
                    processId = Environment.ProcessId, settings = settings.SettingsPath, assetRoot }));
            };
            pet.Show();
            statusTimer.Start();
        };
        host.LocationChanged += (_, _) => AlignPet();
        host.SizeChanged += (_, _) => AlignPet();
        host.Closing += (_, _) =>
        {
            if (stopping) return;
            stopping = true;
            statusTimer.Stop();
            if (pet is not null) pet.Quit();
            else application.Shutdown();
        };
        try { return application.Run(host); }
        finally { catalog.StopPrefetch(); }
    }

    private static BitmapSource Draw(int width, int height, Action<DrawingContext> paint)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) paint(drawing);
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    private static void WriteContactSheet(IReadOnlyList<(long Time, BitmapSource Image)> samples,
        Color background, Brush labelBrush, string path)
    {
        const int columns = 3, cellWidth = 512, cellHeight = 544;
        var rows = (samples.Count + columns - 1) / columns;
        var result = Draw(columns * cellWidth, rows * cellHeight, drawing =>
        {
            drawing.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, columns * cellWidth, rows * cellHeight));
            for (var index = 0; index < samples.Count; index++)
            {
                var x = index % columns * cellWidth;
                var y = index / columns * cellHeight;
                var label = new FormattedText($"{samples[index].Time} ms", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 20, labelBrush, 1);
                drawing.DrawText(label, new Point(x + 16, y + 7));
                drawing.DrawImage(samples[index].Image, new Rect(x, y + 32, 512, 512));
            }
        });
        SavePng(result, path);
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private sealed class SampleClock : IClock
    {
        public long NowMilliseconds { get; set; }
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * percentile) - 1)];
    }
}
