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

internal static partial class Program
{
    private static int RenderWork(string assetRoot, string outputFolder)
    {
        var catalog = new AssetCatalog(assetRoot);
        RequireWorkCatalog(catalog);
        catalog.CacheForSize(512, 1);
        Directory.CreateDirectory(outputFolder);
        var clock = new SampleClock();
        var settings = new PetSettings { Mode = CompanionMode.Work };
        var behavior = new BehaviorController(clock, catalog.Clips, settings,
            randomBetween: (minimum, _) => minimum, idleActions: catalog.IdleActions);
        var assertions = new List<object>();
        void Check(bool condition, string name, object? evidence = null)
        {
            assertions.Add(new { name, passed = condition, evidence });
            if (!condition) throw new InvalidOperationException("Work acceptance failed: " + name);
        }
        Check(behavior.CanWork && behavior.Mode == CompanionMode.Work, "production assets enable work mode");
        Check(settings.WorkDurationMs == 1_500_000 && settings.BreakDurationMs == 300_000,
            "actual default 25 minute work and 5 minute break are used");
        var catOffset = new TranslateTransform();
        var cat = new Image { Stretch = Stretch.Fill, RenderTransform = catOffset, Focusable = false };
        var props = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Focusable = false };
        RenderOptions.SetBitmapScalingMode(cat, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(props, BitmapScalingMode.HighQuality);
        var scene = new Grid { Width = 512, Height = 512, UseLayoutRounding = true, SnapsToDevicePixels = true };
        scene.Children.Add(cat); scene.Children.Add(props);
        var owner = new Window
        {
            Title = "TablePet offscreen work acceptance",
            Width = 512, Height = 512,
            Left = SystemParameters.VirtualScreenLeft - 8000,
            Top = SystemParameters.VirtualScreenTop - 8000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            ShowActivated = false, ShowInTaskbar = false, Focusable = false,
            IsHitTestVisible = false, Content = scene
        };
        SpeechBubbleWindow? bubble = null;
        SleepIndicatorWindow? sleep = null;
        var samples = new List<WorkCapture>();
        var transitions = new List<object>();
        var cycles = new List<object>();
        long lastToken = -1;
        WorkPhase? lastPhase = null;
        string? lastSpeech = null;
        var tickCount = 0;
        void Observe()
        {
            if (lastToken == behavior.Player.Token && lastPhase == behavior.WorkPhase && lastSpeech == behavior.SpeechText) return;
            transitions.Add(new { atMs = clock.NowMilliseconds, phase = behavior.WorkPhase.ToString(),
                state = behavior.State.ToString(), clip = behavior.Player.CurrentClip!.Name,
                token = behavior.Player.Token, speech = behavior.SpeechText,
                workRemainingMs = behavior.WorkRemainingMs, breakRemainingMs = behavior.BreakRemainingMs });
            lastToken = behavior.Player.Token; lastPhase = behavior.WorkPhase; lastSpeech = behavior.SpeechText;
        }
        void AdvanceTo(long target)
        {
            if (target < clock.NowMilliseconds) throw new ArgumentOutOfRangeException(nameof(target));
            while (clock.NowMilliseconds < target)
            {
                var step = Math.Max(1, Math.Min(behavior.NextUpdateInMs, target - clock.NowMilliseconds));
                clock.NowMilliseconds += step;
                behavior.Tick(); tickCount++; Observe();
            }
        }
        void Until(Func<bool> condition, string description, long limitMs = 40_000)
        {
            var deadline = clock.NowMilliseconds + limitMs;
            while (!condition())
            {
                if (clock.NowMilliseconds >= deadline) throw new InvalidOperationException("Timed out waiting for " + description);
                AdvanceTo(Math.Min(deadline, clock.NowMilliseconds + Math.Max(1, behavior.NextUpdateInMs)));
            }
        }
        void UntilPhase(WorkPhase phase) => Until(() => behavior.WorkPhase == phase, phase.ToString());
        void UntilClip(string name) => Until(() => behavior.Player.CurrentClip?.Name == name, "clip " + name);
        void Capture(string label)
        {
            var clip = behavior.Player.CurrentClip!;
            catalog.PreparePlayback(clip, behavior.Player.FrameIndex);
            cat.Source = catalog.Image(behavior.Player.CurrentFrame!.File);
            WorkScenePresentation.Apply(catOffset, props, behavior, catalog, 512);
            bubble!.Update(behavior.SpeechElapsedMs, behavior.SpeechText ?? "", behavior.SpeechDurationMs);
            if (behavior.State == PetState.Sleep) sleep!.Update(behavior.Player.ElapsedMs);
            else if (sleep!.IsVisible) sleep.Hide();
            owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var body = RenderWorkElement(scene);
            var speech = bubble.IsVisible ? RenderWorkElement((FrameworkElement)bubble.Content) : null;
            var sleeping = sleep.IsVisible ? RenderWorkElement((FrameworkElement)sleep.Content) : null;
            var combined = Draw(512, 512, drawing =>
            {
                drawing.DrawImage(body, new Rect(0, 0, 512, 512));
                if (sleeping is not null) drawing.DrawImage(sleeping, new Rect(0, 0, 512, 512));
                if (speech is not null) drawing.DrawImage(speech, new Rect(0, 0, 512, 512));
            });
            var filename = $"{samples.Count:00}-{label}.png";
            SavePng(combined, Path.Combine(outputFolder, filename));
            var pixels = new byte[512 * 512 * 4];
            combined.CopyPixels(pixels, 512 * 4, 0);
            samples.Add(new WorkCapture(label, clock.NowMilliseconds, behavior.WorkPhase.ToString(),
                behavior.State.ToString(), clip.Name, behavior.Player.Token, behavior.Player.FrameIndex,
                behavior.Player.ElapsedMs, behavior.Player.CurrentFrame.File, behavior.SpeechText,
                behavior.SpeechElapsedMs, behavior.SpeechDurationMs, bubble.IsVisible, sleep.IsVisible,
                props.Visibility == Visibility.Visible, props.Opacity, catOffset.Y, filename,
                Convert.ToHexStringLower(SHA256.HashData(pixels)), combined));
            Check(Math.Abs(catOffset.Y - behavior.WorkBodyOffsetY) < 1e-9 &&
                Math.Abs(props.Opacity - behavior.WorkPropsOpacity) < 1e-9 && props.RenderTransform.Value.IsIdentity,
                "production presentation matches independent body and prop state: " + label);
        }
        try
        {
            owner.Show();
            bubble = new SpeechBubbleWindow(owner);
            sleep = new SleepIndicatorWindow(owner);
            Observe();
            for (var round = 1; round <= 2; round++)
            {
                Check(behavior.WorkPhase == WorkPhase.AnnouncingWork && behavior.Player.CurrentClip!.Name == "meow" &&
                    behavior.SpeechText == "开始工作啦~", $"round {round}: meow announces work");
                AdvanceTo(clock.NowMilliseconds + 500);
                Capture($"round-{round}-start-meow");
                Check(bubble.IsVisible, $"round {round}: real beginning speech is visible");
                UntilPhase(WorkPhase.Entering);
                AdvanceTo(clock.NowMilliseconds + 200);
                Capture($"round-{round}-enter-half");
                Check(Math.Abs(behavior.WorkPropsOpacity - .5) < 1e-9, $"round {round}: props enter at half opacity");
                UntilPhase(WorkPhase.Working);
                var workStartedAt = clock.NowMilliseconds;
                AdvanceTo(workStartedAt + 1400);
                Capture($"round-{round}-working-blink");
                AdvanceTo(workStartedAt + 2400);
                Capture($"round-{round}-working");
                Check(behavior.WorkPropsOpacity == 1 && Math.Abs(behavior.WorkBodyOffsetY + 2) < 1e-9,
                    $"round {round}: working props stay fixed while whole cat reaches breath peak");
                var token = behavior.Player.Token;
                var frame = behavior.Player.CurrentFrame;
                var remaining = behavior.WorkRemainingMs;
                behavior.Click(); Observe();
                Check(behavior.Player.Token == token && behavior.Player.CurrentFrame == frame && behavior.WorkRemainingMs == remaining &&
                    behavior.SpeechText == "工作呢，别打扰我", $"round {round}: work click changes speech only");
                AdvanceTo(clock.NowMilliseconds + 500);
                Capture($"round-{round}-busy-speech");
                Check(bubble.IsVisible && behavior.Player.CurrentClip!.Name == "work_loop" && behavior.Player.Token == token,
                    $"round {round}: busy speech overlays continuing work loop");
                behavior.DoubleClick(); Observe();
                Check(behavior.Player.Token == token && behavior.SpeechText == "工作呢，别打扰我",
                    $"round {round}: work double click cannot queue meow or a wake");
                var workDeadline = workStartedAt + settings.WorkDurationMs;
                AdvanceTo(workDeadline - 1);
                Check(behavior.WorkPhase == WorkPhase.Working && behavior.WorkRemainingMs == 1,
                    $"round {round}: complete 25 minute work deadline is respected");
                AdvanceTo(workDeadline);
                UntilPhase(WorkPhase.Exiting);
                var exitStartedAt = clock.NowMilliseconds;
                Check(exitStartedAt >= workDeadline && exitStartedAt - workDeadline <= 375,
                    $"round {round}: deadline exits after at most the active blink");
                AdvanceTo(exitStartedAt + 200);
                Capture($"round-{round}-exit-half");
                Check(Math.Abs(behavior.WorkPropsOpacity - .5) < 1e-9,
                    $"round {round}: props exit at half opacity");
                UntilPhase(WorkPhase.AnnouncingBreak);
                Check(behavior.Player.CurrentClip!.Name == "wink" && behavior.SpeechText == "休息一下吧~",
                    $"round {round}: wink announces break");
                AdvanceTo(clock.NowMilliseconds + Math.Min(500, catalog.Clips["wink"].DurationMs / 2));
                Capture($"round-{round}-break-wink");
                Check(bubble.IsVisible, $"round {round}: real break announcement is visible");
                UntilPhase(WorkPhase.FallingAsleep);
                AdvanceTo(clock.NowMilliseconds + catalog.Clips["fall_asleep"].DurationMs / 2);
                Capture($"round-{round}-falling-asleep");
                UntilPhase(WorkPhase.Resting);
                var breakStartedAt = clock.NowMilliseconds;
                var breakDeadline = breakStartedAt + settings.BreakDurationMs;
                Check(behavior.BreakRemainingMs == settings.BreakDurationMs,
                    $"round {round}: five minute break starts only when sleep begins");
                AdvanceTo(breakStartedAt + 500);
                Capture($"round-{round}-sleep");
                if (round == 1)
                {
                    behavior.Click(); Observe();
                    Check(behavior.WorkPhase == WorkPhase.RestWake, "rest click wakes before reacting");
                    UntilClip("wake");
                    AdvanceTo(clock.NowMilliseconds + catalog.Clips["wake"].DurationMs / 2);
                    Capture("rest-click-wake");
                    UntilPhase(WorkPhase.RestYawn);
                    Check(behavior.Player.CurrentClip!.Name == "yawn" && behavior.SpeechText == "和我一起休息",
                        "rest click starts production yawn and rest-together speech");
                    AdvanceTo(clock.NowMilliseconds + Math.Min(1800, catalog.Clips["yawn"].DurationMs / 2));
                    Capture("rest-click-yawn-speech");
                    Check(bubble.IsVisible, "rest-together text uses actual speech overlay");
                    UntilPhase(WorkPhase.FallingAsleep);
                    AdvanceTo(clock.NowMilliseconds + catalog.Clips["fall_asleep"].DurationMs / 2);
                    Capture("rest-click-resleep");
                    UntilPhase(WorkPhase.Resting);
                    Check(behavior.BreakRemainingMs == breakDeadline - clock.NowMilliseconds,
                        "rest click and yawn preserve original break deadline");
                }
                AdvanceTo(breakDeadline - 1);
                Check(behavior.WorkPhase == WorkPhase.Resting && behavior.BreakRemainingMs == 1,
                    $"round {round}: complete five minute break deadline is respected");
                AdvanceTo(breakDeadline);
                Check(behavior.WorkPhase == WorkPhase.WakeForNextWork,
                    $"round {round}: break expiry wakes for the next work round");
                UntilClip("wake");
                AdvanceTo(clock.NowMilliseconds + catalog.Clips["wake"].DurationMs / 2);
                Capture($"round-{round}-scheduled-wake");
                UntilPhase(WorkPhase.AnnouncingWork);
                cycles.Add(new { round, workStartedAt, workDeadline, exitStartedAt, breakStartedAt, breakDeadline,
                    workDurationMs = settings.WorkDurationMs, breakDurationMs = settings.BreakDurationMs,
                    nextAnnouncementAt = clock.NowMilliseconds, completed = true });
            }
            UntilPhase(WorkPhase.Working);
            AdvanceTo(clock.NowMilliseconds + 2400);
            behavior.ApplySettings(behavior.Settings with { Mode = CompanionMode.Daily }); Observe();
            Check(behavior.WorkPhase == WorkPhase.Exiting, "switching to Daily takes the work exit transition");
            AdvanceTo(clock.NowMilliseconds + 200);
            Capture("daily-switch-exit-half");
            UntilPhase(WorkPhase.None);
            Check(behavior.Mode == CompanionMode.Daily && behavior.State == PetState.Idle &&
                behavior.Player.CurrentClip!.Name == "idle" && behavior.WorkPropsOpacity == 0 &&
                behavior.WorkBodyOffsetY == 0 && behavior.SpeechText is null,
                "Daily restores idle with no props, body offset or work speech");
            Capture("daily-restored");
            behavior.DoubleClick(); Observe();
            Check(behavior.Player.CurrentClip!.Name == "meow", "Daily double click still performs meow");
            AdvanceTo(clock.NowMilliseconds + 500);
            Capture("daily-meow-regression");
            UntilClip("idle");
            behavior.ApplySettings(behavior.Settings with { Mode = CompanionMode.Work }); Observe();
            Check(behavior.WorkPhase == WorkPhase.AnnouncingWork, "re-entering Work restarts its announcement");
            UntilPhase(WorkPhase.Working);
            AdvanceTo(clock.NowMilliseconds + 2400);
            Capture("work-restored");
            Check(behavior.WorkRemainingMs == settings.WorkDurationMs - 2400,
                "re-entering Work starts a fresh complete work interval");
            Check(!catalog.IsFallback && !catalog.HasOptionalActionFallback && !catalog.HasFrameFallback,
                "all captured production frames and overlays decode without fallback");
            WriteWorkContactSheet(samples, Color.FromRgb(0xff, 0xf8, 0xef), Brushes.DarkSlateGray,
                Path.Combine(outputFolder, "contact-light.png"));
            WriteWorkContactSheet(samples, Color.FromRgb(0x24, 0x22, 0x2a), Brushes.WhiteSmoke,
                Path.Combine(outputFolder, "contact-dark.png"));
            var report = new
            {
                passed = true, assetRoot,
                manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(assetRoot, "manifest.json")))),
                renderer = "Actual AssetCatalog + BehaviorController + WorkScenePresentation + SpeechBubbleWindow + SleepIndicatorWindow, WPF RenderTargetBitmap",
                clock = "Controllable IClock advanced at BehaviorController.NextUpdateInMs; default durations unchanged",
                workDurationMs = settings.WorkDurationMs, breakDurationMs = settings.BreakDurationMs,
                completedDefaultCycles = cycles.Count, simulatedElapsedMs = clock.NowMilliseconds, tickCount,
                settingsWritten = false, cycles, assertions, transitions,
                captures = samples.Select(sample => sample.Evidence()),
                limitation = "Deterministic offscreen rendering verifies production behavior and WPF layers. It does not measure DWM refresh or replace real input acceptance; use --interactive-work for that."
            };
            File.WriteAllText(Path.Combine(outputFolder, "render-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { report.passed, outputFolder, report.completedDefaultCycles,
                report.simulatedElapsedMs, report.tickCount, assertions = assertions.Count, captures = samples.Count,
                report.workDurationMs, report.breakDurationMs }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(outputFolder, "failure-report.json"), JsonSerializer.Serialize(new
            {
                passed = false, error = error.ToString(), atMs = clock.NowMilliseconds,
                workPhase = behavior.WorkPhase.ToString(), clip = behavior.Player.CurrentClip?.Name,
                assertions, transitions, captures = samples.Select(sample => sample.Evidence())
            }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            catalog.StopPrefetch();
            bubble?.Close(); sleep?.Close(); owner.Close();
        }
    }

    private static void RequireWorkCatalog(AssetCatalog catalog)
    {
        if (catalog.IsFallback || catalog.HasOptionalActionFallback || catalog.CanvasWidth != 512 || catalog.CanvasHeight != 512 ||
            new[] { "work_enter", "work_loop", "work_exit", "work_props", "meow", "wink", "yawn" }.Any(name => !catalog.Clips.ContainsKey(name)))
            throw new InvalidDataException("Work QA requires complete current production work assets without fallback.");
    }

    private static BitmapSource RenderWorkElement(FrameworkElement element)
    {
        element.Measure(new Size(512, 512));
        element.Arrange(new Rect(0, 0, 512, 512));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); bitmap.Freeze(); return bitmap;
    }

    private sealed record WorkCapture(string Label, long AtMs, string WorkPhase, string State, string Clip,
        long Token, int FrameIndex, long ElapsedMs, string Source, string? Speech, long SpeechElapsedMs,
        int SpeechDurationMs, bool BubbleVisible, bool SleepIndicatorVisible, bool PropsVisible,
        double PropsOpacity, double BodyOffsetY, string File, string PixelSha256, BitmapSource Image)
    {
        public object Evidence() => new { Label, AtMs, WorkPhase, State, Clip, Token, FrameIndex, ElapsedMs, Source,
            Speech, SpeechElapsedMs, SpeechDurationMs, BubbleVisible, SleepIndicatorVisible,
            PropsVisible, PropsOpacity, BodyOffsetY, File, PixelSha256 };
    }

    private static void WriteWorkContactSheet(IReadOnlyList<WorkCapture> captures, Color background, Brush text, string path)
    {
        const int columns = 4, cellWidth = 256, cellHeight = 296;
        var rows = (captures.Count + columns - 1) / columns;
        var result = Draw(columns * cellWidth, rows * cellHeight, drawing =>
        {
            drawing.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, columns * cellWidth, rows * cellHeight));
            for (var index = 0; index < captures.Count; index++)
            {
                var capture = captures[index];
                var x = index % columns * cellWidth; var y = index / columns * cellHeight;
                var label = new FormattedText($"{index:00} {capture.Label}\n{capture.AtMs} ms · {capture.WorkPhase}",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, text, 1);
                drawing.DrawText(label, new Point(x + 6, y + 4));
                drawing.DrawImage(capture.Image, new Rect(x, y + 38, 256, 256));
            }
        });
        SavePng(result, path);
    }

    private static int InteractiveWork(string assetRoot, string settingsFolder)
    {
        var catalog = new AssetCatalog(assetRoot);
        RequireWorkCatalog(catalog);
        Directory.CreateDirectory(settingsFolder);
        var settingsPath = Path.Combine(settingsFolder, "work-qa-settings.json");
        var userSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPet", "settings.json");
        if (string.Equals(Path.GetFullPath(settingsPath), Path.GetFullPath(userSettings), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Interactive QA must use separate temporary settings.");
        using var settings = new SettingsService(settingsPath);
        settings.Update(new PetSettings
        {
            Mode = CompanionMode.Work, WorkDurationMs = 8000, BreakDurationMs = 8000,
            AutoSleep = false, BlinkMinMs = 86400000, BlinkMaxMs = 86400000,
            PetHeightDip = 480, Topmost = false, ClickThrough = false, StartWithWindows = false
        }, saveImmediately: true);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var title = new TextBlock { Text = "工作模式真实窗口验收", FontSize = 23, FontWeight = FontWeights.SemiBold };
        var instruction = new TextBlock { Text = "8 秒工作 / 8 秒休息 · 点击工作中的猫查看气泡，点击睡猫查看醒来与打哈欠", FontSize = 14, Margin = new Thickness(0, 6, 0, 0) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        Button AddButton(string text, Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
            button.Click += (_, _) => action(); controls.Children.Add(button); return button;
        }
        AddButton("日常模式", () => settings.Update(settings.Current with { Mode = CompanionMode.Daily }, saveImmediately: true));
        AddButton("工作模式", () => settings.Update(settings.Current with { Mode = CompanionMode.Work }, saveImmediately: true));
        AddButton("默认 25 / 5 分钟", () => settings.Update(settings.Current with { WorkDurationMs = 1_500_000, BreakDurationMs = 300_000 }, saveImmediately: true));
        AddButton("验收 8 / 8 秒", () => settings.Update(settings.Current with { WorkDurationMs = 8000, BreakDurationMs = 8000 }, saveImmediately: true));
        var heading = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Top };
        heading.Children.Add(title); heading.Children.Add(instruction); heading.Children.Add(controls);
        var status = new TextBlock { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Bottom,
            FontSize = 14, TextWrapping = TextWrapping.Wrap, Text = "正在载入真实 PetWindow…" };
        var canvas = new Grid(); canvas.Children.Add(heading); canvas.Children.Add(status);
        var host = new Window { Title = "TablePet Work QA", Width = 930, Height = 780, MinWidth = 850, MinHeight = 740,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true,
            Background = new SolidColorBrush(Color.FromRgb(0xff, 0xf8, 0xef)), Content = canvas };
        PetWindow? pet = null;
        BehaviorController? behavior = null;
        var tracePath = Path.Combine(settingsFolder, "work-interaction-trace.jsonl");
        var observed = "";
        void WriteObservation()
        {
            if (behavior is null) return;
            var key = $"{behavior.WorkPhase}/{behavior.Player.Token}/{behavior.SpeechText}/{behavior.SpeechElapsedMs < 100}";
            status.Text = $"模式 {behavior.Mode} · 阶段 {behavior.WorkPhase} · 动作 {behavior.Player.CurrentClip?.Name}\n" +
                $"工作剩余 {behavior.WorkRemainingMs / 1000d:0.0}s · 休息剩余 {behavior.BreakRemainingMs / 1000d:0.0}s · 气泡 {behavior.SpeechText ?? "无"}\n" +
                "独立临时设置：" + settings.SettingsPath;
            if (key == observed) return;
            observed = key;
            var record = JsonSerializer.Serialize(new { atUtc = DateTimeOffset.UtcNow, mode = behavior.Mode.ToString(),
                phase = behavior.WorkPhase.ToString(), state = behavior.State.ToString(), clip = behavior.Player.CurrentClip?.Name,
                token = behavior.Player.Token, speech = behavior.SpeechText, speechElapsedMs = behavior.SpeechElapsedMs,
                workRemainingMs = behavior.WorkRemainingMs, breakRemainingMs = behavior.BreakRemainingMs,
                propsOpacity = behavior.WorkPropsOpacity, bodyOffsetY = behavior.WorkBodyOffsetY });
            File.AppendAllText(tracePath, record + Environment.NewLine); Console.WriteLine(record);
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => WriteObservation();
        void Align()
        {
            if (pet is null || !pet.IsVisible || !host.IsVisible) return;
            var point = host.PointToScreen(new Point((host.ActualWidth - pet.Width) / 2, 140));
            var dpi = VisualTreeHelper.GetDpi(host);
            pet.Left = point.X / dpi.DpiScaleX; pet.Top = point.Y / dpi.DpiScaleY;
        }
        host.Loaded += (_, _) =>
        {
            pet = new PetWindow(settings, catalog) { Owner = host };
            behavior = (BehaviorController?)typeof(PetWindow).GetField("_behavior", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(pet)
                ?? throw new InvalidOperationException("Cannot observe the actual PetWindow controller.");
            pet.Loaded += (_, _) =>
            {
                Align(); WriteObservation();
                Console.WriteLine(JsonSerializer.Serialize(new { ready = true, title = host.Title, processId = Environment.ProcessId,
                    settings = settings.SettingsPath, tracePath, assetRoot }));
            };
            pet.Show(); timer.Start();
        };
        host.LocationChanged += (_, _) => Align(); host.SizeChanged += (_, _) => Align();
        var closing = false;
        host.Closing += (_, _) =>
        {
            if (closing) return; closing = true; timer.Stop();
            if (pet is not null) pet.Quit(); else app.Shutdown();
        };
        try { return app.Run(host); }
        finally { timer.Stop(); catalog.StopPrefetch(); }
    }
}
