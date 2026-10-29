param(
    [string]$PublishDirectory,
    [switch]$ProbeAppHost,
    [switch]$SkipAssets,
    [int]$Cycles = 1000,
    [string]$ExpectedIdleActions = ''
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $workspaceRoot 'src/TablePet.App/TablePet.App.csproj') -Raw -Encoding UTF8
    $version = [string]$project.Project.PropertyGroup.Version
    $PublishDirectory = "dist/TablePet-$version-win-x64"
}
$publishRoot = if ([IO.Path]::IsPathRooted($PublishDirectory)) { [IO.Path]::GetFullPath($PublishDirectory) }
    else { [IO.Path]::GetFullPath((Join-Path $workspaceRoot $PublishDirectory)) }
$reportRoot = Join-Path $workspaceRoot 'artifacts/runtime-check'
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$requiredRuntimeFiles = @('TablePet.exe','TablePet.dll','TablePet.Core.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll')
foreach ($file in $requiredRuntimeFiles) {
    $item = Get-Item -LiteralPath (Join-Path $publishRoot $file)
    if ($item.Length -eq 0) { throw "Empty runtime file: $file" }
}
$runtime = Get-Content -LiteralPath (Join-Path $publishRoot 'TablePet.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (@($runtime.runtimeOptions.includedFrameworks).Count -ne 2) { throw 'The package is not a self-contained WPF application.' }
$exeBytes = [IO.File]::ReadAllBytes((Join-Path $publishRoot 'TablePet.exe'))
$peOffset = [BitConverter]::ToInt32($exeBytes, 0x3c)
$machine = [BitConverter]::ToUInt16($exeBytes, $peOffset + 4)
if ($machine -ne 0x8664) { throw 'The published apphost is not Windows x64.' }
$summary = [ordered]@{
    checkedAt = [DateTimeOffset]::Now.ToString('o')
    publishDirectory = $publishRoot
    runtimeFiles = $requiredRuntimeFiles
    frameworks = $runtime.runtimeOptions.includedFrameworks
    peMachine = 'AMD64 (0x8664)'
    apphostProbe = 'Not requested'
    assets = 'Not checked'
}

if ($ProbeAppHost) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $publishRoot 'TablePet.exe'
    $start.Arguments = '--check-assets'
    $start.WorkingDirectory = $publishRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $reportRoot 'intentionally-missing-dotnet'
    $start.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $reportRoot 'intentionally-missing-dotnet'
    $start.EnvironmentVariables['PATH'] = Join-Path $env:SystemRoot 'System32'
    $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $process = [Diagnostics.Process]::Start($start)
    if (!$process.WaitForExit(60000)) {
        $process.Kill()
        throw 'The isolated apphost probe did not exit within 60 seconds.'
    }
    if ($process.ExitCode -ne 0) { throw "The isolated apphost probe exited with code $($process.ExitCode)." }
    $summary.apphostProbe = 'Passed: --check-assets loads and decodes real assets without fallback, exit 0, DOTNET_ROOT unavailable and PATH restricted. No window, tray, settings or instance signalling. Does not verify full UI startup or a clean operating system.'
}

if (!$SkipAssets) {
    $sourceAssets = Join-Path $workspaceRoot 'assets/strawberry-cat'
    $publishedAssets = Join-Path $publishRoot 'Assets/strawberry-cat'
    foreach ($manifestPath in @((Join-Path $sourceAssets 'manifest.json'), (Join-Path $publishedAssets 'manifest.json'))) {
        if (!(Test-Path -LiteralPath $manifestPath)) { throw "Missing final manifest: $manifestPath" }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $sourceAssets 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $assetFiles = @('manifest.json','fallback.png','tray.ico') + @($manifest.clips.PSObject.Properties.Value.frames.file | Sort-Object -Unique)
    foreach ($relative in $assetFiles) {
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $sourceAssets $relative) -Algorithm SHA256).Hash
        $publishedHash = (Get-FileHash -LiteralPath (Join-Path $publishedAssets $relative) -Algorithm SHA256).Hash
        if ($sourceHash -ne $publishedHash) { throw "Published asset differs from source: $relative" }
    }
    $scratch = Join-Path $workspaceRoot '.tools/runtime-verification'
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $appProject = [Security.SecurityElement]::Escape((Join-Path $workspaceRoot 'src/TablePet.App/TablePet.App.csproj'))
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$appProject" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $scratch 'RuntimeVerification.csproj') -Encoding utf8
    @'
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TablePet.App;
using TablePet.Core;

internal static class Program
{
    private static readonly List<string> Checks = new();
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var report = Path.GetFullPath(args[1]);
        var cycles = int.Parse(args[2]);
        Check(cycles > 0, "At least one behavior cycle is requested");
        var catalog = new AssetCatalog(root);
        Check(!catalog.IsFallback, "Final manifest loads without fallback");
        Check(!catalog.HasOptionalActionFallback, "All declared optional expressions are available without fallback");
        var idleNames = catalog.IdleActions.Select(action => action.Clip).ToHashSet();
        Check(idleNames.Contains("blink"), "Blink remains in the random idle choices");
        if (args.Length > 3 && !string.IsNullOrWhiteSpace(args[3]))
            Check(idleNames.SetEquals(args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                "Available idle expressions match the requested acceptance set");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var metadata = manifest.RootElement;
        Check(!string.IsNullOrWhiteSpace(metadata.GetProperty("id").GetString()), "Character id is present");
        Check(metadata.TryGetProperty("version", out var version) && version.ValueKind is JsonValueKind.String or JsonValueKind.Number, "Asset version is present");
        var anchor = metadata.GetProperty("anchor");
        Check(anchor.GetProperty("x").GetDouble() >= 0 && anchor.GetProperty("x").GetDouble() <= catalog.CanvasWidth
            && anchor.GetProperty("y").GetDouble() >= 0 && anchor.GetProperty("y").GetDouble() <= catalog.CanvasHeight, "Anchor is within the canvas");
        var region = metadata.GetProperty("hitRegion");
        Check(region.GetProperty("width").GetDouble() > 0 && region.GetProperty("height").GetDouble() > 0, "Interaction region is described");
        var files = catalog.Clips.Values.SelectMany(c => c.Frames).Select(f => f.File).Distinct().ToArray();
        var checksums = new Dictionary<string, string>();
        foreach (var file in files)
        {
            var encoded = File.ReadAllBytes(Path.Combine(root, file));
            Check(encoded.Length > 26 && encoded[25] == 6, "PNG uses RGBA color type " + file);
            using var stream = File.OpenRead(Path.Combine(root, file));
            var decoded = PngBitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Check(decoded.PixelWidth == catalog.CanvasWidth && decoded.PixelHeight == catalog.CanvasHeight, "Canvas " + file);
            var bitmap = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var opaque = 0; var transparent = 0;
            for (var i = 3; i < pixels.Length; i += 4) { if (pixels[i] >= 128) opaque++; if (pixels[i] == 0) transparent++; }
            Check(opaque > 100 && transparent > 100, "Visible subject and true alpha " + file);
            Check(pixels[3] == 0 && pixels[(bitmap.PixelWidth - 1) * 4 + 3] == 0 && pixels[^1] == 0, "Transparent corners " + file);
            checksums[file] = Convert.ToHexString(SHA256.HashData(pixels));
        }
        string First(string clip) => catalog.Clips[clip].Frames[0].File;
        string Last(string clip) => catalog.Clips[clip].Frames[^1].File;
        void Same(string a, string b, string label) => Check(checksums[a] == checksums[b], label);
        foreach (var clip in new[] { "blink", "fall_asleep", "rise" }) Same(First("idle"), First(clip), "Shared idle start " + clip);
        foreach (var clip in new[] { "blink", "wake", "settle" }) Same(First("idle"), Last(clip), "Shared idle end " + clip);
        foreach (var action in catalog.IdleActions)
        {
            var clip = catalog.Clips[action.Clip];
            Same(First("idle"), First(action.Clip), "Shared idle expression start " + action.Clip);
            Same(First("idle"), Last(action.Clip), "Shared idle expression end " + action.Clip);
            Check(!clip.Loop, "Idle expression is a one-shot " + action.Clip);
            Check(clip.Frames.Any(frame => checksums[frame.File] != checksums[First("idle")]),
                "Idle expression contains a visibly different decoded pose " + action.Clip);
        }
        Same(Last("fall_asleep"), First("sleep_loop"), "Fall asleep to sleep boundary");
        Same(First("sleep_loop"), First("wake"), "Sleep to wake boundary");
        Same(Last("rise"), First("hold"), "Rise to hold boundary");
        Same(First("hold"), First("settle"), "Hold to settle boundary");
        foreach (var height in new[] { 120d, 240d, 480d })
        foreach (var dpi in new[] { 1d, 1.25d, 1.5d, 2d })
        {
            catalog.CacheForSize(height, dpi);
            foreach (var file in files)
            {
                var bitmap = catalog.Image(file);
                Check(bitmap.PixelHeight == catalog.CanvasHeight, $"Native canvas decode {file} for {height} DIP / {dpi * 100}% presentation");
                Check(!catalog.Hit(file, 0, 0), "Transparent corner is not interaction region");
            }
        }
        catalog.CacheForSize(240, 1.5);
        Check(!catalog.HasFrameFallback, "All real frames decode without an image fallback");
        var started = Stopwatch.StartNew();
        var idleAcceptance = VerifyIdleActions(catalog, checksums);
        var clock = new FakeClock();
        var settings = new PetSettings { SleepAfterMs = 60000, BlinkMinMs = 3000, BlinkMaxMs = 3000, CooldownMs = 30, HoldMs = 100 };
        var random = new Random(20261027);
        // These cycles exercise rise/hold/settle; keep click selection separate from idle randomness.
        var controller = new BehaviorController(clock, catalog.Clips, settings,
            (a, b) => a == 0 && b == 1 ? 0 : random.Next(a, b + 1), catalog.IdleActions);
        var observed = new HashSet<string>();
        var idleSelectionCounts = idleNames.ToDictionary(name => name, _ => 0);
        string? previousIdle = null;
        long observedToken = -1;
        void Tick()
        {
            controller.Tick();
            var name = controller.Player.CurrentClip!.Name;
            observed.Add(name);
            if (observedToken == controller.Player.Token) return;
            observedToken = controller.Player.Token;
            if (!idleSelectionCounts.ContainsKey(name)) return;
            if (idleNames.Count > 1) Check(name != previousIdle, "Random idle expression does not immediately repeat");
            idleSelectionCounts[name]++;
            previousIdle = name;
        }
        void Until(Func<bool> done, long limit = 120000)
        {
            var deadline = clock.NowMilliseconds + limit;
            while (!done() && clock.NowMilliseconds < deadline)
            {
                clock.NowMilliseconds += Math.Clamp(controller.NextUpdateInMs, 1, 200);
                Tick();
            }
            if (!done()) throw new InvalidOperationException("State transition exceeded deadline: " + controller.State);
        }
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            Until(() => controller.State == PetState.FallAsleep);
            if (cycle % 2 == 0)
            {
                clock.NowMilliseconds += Math.Max(1, catalog.Clips["fall_asleep"].DurationMs / 3);
                Tick();
                var expected = controller.Player.CurrentFrame!.File;
                var stale = new AnimationCompleted(controller.Player.Token, "fall_asleep");
                controller.Touch();
                Check(controller.Player.CurrentFrame!.File == expected, "Interrupted sleep retains exact visible frame");
                controller.HandleAnimationCompleted(stale);
                Check(controller.State == PetState.Wake, "Obsolete callback cannot restore sleep");
            }
            else
            {
                Until(() => controller.State == PetState.Sleep);
                clock.NowMilliseconds += catalog.Clips["sleep_loop"].DurationMs * 4;
                Tick();
                controller.SetSuspended(true); clock.NowMilliseconds += 200000; Tick();
                controller.SetSuspended(false);
                Check(controller.State == PetState.Sleep, "Suspended sleep restores as sleep without backlog");
                controller.Touch();
            }
            Until(() => controller.State == PetState.React && controller.ReactionPhase == ReactionPhase.Hold);
            controller.BeginDrag();
            var held = controller.Player.CurrentFrame!.File;
            clock.NowMilliseconds += 1000; Tick();
            Check(controller.Player.CurrentFrame!.File == held, "Dragging freezes real frame");
            controller.EndDrag();
            Until(() => controller.State == PetState.Idle);
            controller.Touch();
            Check(controller.State == PetState.Idle, "Cooldown touch does not queue replay");
        }
        foreach (var action in idleNames)
            Check(idleSelectionCounts[action] > 0, "Seeded automatic cycles selected real idle expression " + action);
        File.WriteAllText(report, JsonSerializer.Serialize(new {
            passed = true, manifest = Path.Combine(root, "manifest.json"), clips = catalog.Clips.Count,
            uniquePngs = files.Length, checkedSizes = new[] { 120, 240, 480 },
            decodedDpiScales = new[] { 100, 125, 150, 200 }, cycles,
            nativeBitmapHeightCap = catalog.CanvasHeight,
            idleActions = catalog.IdleActions, idleSelectionCounts, idleAcceptance,
            observedClips = observed.Order().ToArray(), checkCount = Checks.Count,
            elapsedMilliseconds = started.ElapsedMilliseconds,
            note = "DPI values above verify bitmap decoding only, not monitor movement. Clock cycles are accelerated, not real-time desktop residency."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {catalog.Clips.Count} clips, {files.Length} PNGs, {cycles} real-asset behavior cycles, {Checks.Count} assertions.");
        return 0;
    }

    private static IReadOnlyList<object> VerifyIdleActions(AssetCatalog catalog, Dictionary<string, string> checksums)
    {
        var reports = new List<object>();
        var rest = catalog.Clips["idle"].Frames[0].File;
        foreach (var action in catalog.IdleActions)
        {
            var clip = catalog.Clips[action.Clip];
            var ticket = 1;
            foreach (var choice in catalog.IdleActions)
            {
                if (choice.Clip == action.Clip) break;
                ticket += choice.Weight;
            }
            (FakeClock Clock, BehaviorController Pet) Start(bool autoSleep = false, int? sleepAfter = null)
            {
                var clock = new FakeClock();
                var settings = new PetSettings { AutoSleep = autoSleep, BlinkMinMs = 100, BlinkMaxMs = 100,
                    SleepAfterMs = sleepAfter ?? 60000, HoldMs = 100, CooldownMs = 30 };
                var pet = new BehaviorController(clock, catalog.Clips, settings,
                    // An idle-expression ticket must not also choose the independent Wink reaction.
                    (min, max) => min == 0 && max == 1 ? 0 : min == max ? min : Math.Clamp(ticket, min, max), catalog.IdleActions);
                Step(clock, pet, 99);
                Check(pet.Player.CurrentClip!.Name == "idle", "Expression waits for its automatic deadline " + action.Clip);
                Step(clock, pet, 1);
                Check(pet.Player.CurrentClip!.Name == action.Clip, "Random ticket automatically starts real expression " + action.Clip);
                Check(pet.LastPetInteraction == 0, "Automatic expression is not user interaction " + action.Clip);
                return (clock, pet);
            }
            void SampleVisiblePose(FakeClock clock, BehaviorController pet)
            {
                var starts = new List<(int Index, long Start)>();
                long elapsed = 0;
                for (var i = 0; i < clip.Frames.Count; i++)
                {
                    if (checksums[clip.Frames[i].File] != checksums[rest]) starts.Add((i, elapsed));
                    elapsed += clip.Frames[i].DurationMs;
                }
                var target = starts.MinBy(item => Math.Abs(item.Start - clip.DurationMs / 2d));
                Step(clock, pet, target.Start + clip.Frames[target.Index].DurationMs / 2);
                Check(checksums[pet.Player.CurrentFrame!.File] != checksums[rest], "Exercise a non-rest expression pose " + action.Clip);
            }

            var (clickClock, clicked) = Start();
            SampleVisiblePose(clickClock, clicked);
            var visible = clicked.Player.CurrentFrame!.File;
            var originalToken = clicked.Player.Token;
            clicked.Click();
            Check(clicked.Player.CurrentFrame!.File == visible, "Click preserves exact currently displayed expression frame " + action.Clip);
            var returnToken = clicked.Player.Token;
            var returnClip = clicked.Player.CurrentClip!;
            if (action.Clip == "blink") Check(returnToken == originalToken, "Click lets the existing blink finish");
            else
            {
                Check(returnClip.Name == "idle_to_react" && !returnClip.Loop, "Long expression starts a one-shot return " + action.Clip);
                Check(returnClip.DurationMs <= 240, "Click return stays responsive " + action.Clip);
                Check(checksums[returnClip.Frames[^1].File] == checksums[rest], "Click return reaches the shared resting pose " + action.Clip);
            }
            clicked.Click(); clicked.Hover();
            clicked.HandleAnimationCompleted(new AnimationCompleted(returnToken == originalToken ? originalToken - 1 : originalToken, action.Clip));
            Check(clicked.Player.Token == returnToken && clicked.State == PetState.Idle,
                "Repeated input and stale completion cannot restart or skip the return " + action.Clip);
            UntilState(clickClock, clicked, () => clicked.State == PetState.React, clip.DurationMs + 1000);
            Check(clicked.ReactionPhase == ReactionPhase.Rise && clicked.Player.CurrentClip!.Name == "rise",
                "Explicit click connects expression to rise " + action.Clip);

            var (hoverClock, hovered) = Start();
            SampleVisiblePose(hoverClock, hovered);
            var hoverToken = hovered.Player.Token;
            hovered.Hover();
            Check(hovered.Player.Token == hoverToken && hovered.Player.CurrentClip!.Name == action.Clip,
                "Hover leaves the current expression uninterrupted " + action.Clip);
            Check(hovered.LastPetInteraction == hoverClock.NowMilliseconds, "Hover refreshes interaction time " + action.Clip);
            UntilState(hoverClock, hovered, () => hovered.Player.CurrentClip!.Name == "idle", clip.DurationMs + 1000);
            Check(hovered.State == PetState.Idle && hovered.ReactionPhase == ReactionPhase.None, "Hover never queues rise " + action.Clip);

            var (pauseClock, paused) = Start();
            SampleVisiblePose(pauseClock, paused);
            paused.SetAutomaticActionsPaused(true);
            UntilState(pauseClock, paused, () => paused.Player.CurrentClip!.Name == "idle", clip.DurationMs + 1000);
            Step(pauseClock, paused, 100000);
            Check(paused.Player.CurrentClip!.Name == "idle" && paused.NextUpdateInMs == long.MaxValue,
                "Automatic pause allows a natural finish then suppresses future expressions " + action.Clip);
            paused.Click();
            Check(paused.State == PetState.React, "Automatic pause still permits explicit clicks " + action.Clip);

            var (dragClock, dragged) = Start();
            SampleVisiblePose(dragClock, dragged);
            var held = dragged.Player.CurrentFrame!.File;
            var elapsedBeforeDrag = dragged.Player.ElapsedMs;
            dragged.BeginDrag(); Step(dragClock, dragged, 10000);
            Check(dragged.Player.CurrentFrame!.File == held && dragged.Player.ElapsedMs == elapsedBeforeDrag,
                "Dragging freezes the real expression frame and elapsed time " + action.Clip);
            dragged.EndDrag();
            UntilState(dragClock, dragged, () => dragged.Player.CurrentClip!.Name == "idle", clip.DurationMs + 1000);
            Check(dragged.State == PetState.Idle && dragged.ReactionPhase == ReactionPhase.None, "Drag release does not trigger rise " + action.Clip);

            var (suspendClock, suspended) = Start();
            SampleVisiblePose(suspendClock, suspended);
            suspended.Click();
            var pendingToken = suspended.Player.Token;
            var pendingName = suspended.Player.CurrentClip!.Name;
            suspended.SetSuspended(true); Step(suspendClock, suspended, 10000); suspended.SetSuspended(false);
            suspended.HandleAnimationCompleted(new AnimationCompleted(pendingToken, pendingName));
            Check(suspended.State == PetState.Idle && suspended.Player.CurrentClip!.Name == "idle" && suspended.ReactionPhase == ReactionPhase.None,
                "Suspending cancels a queued expression click and stale return callback " + action.Clip);

            var sleepAfter = checked(100 + (int)Math.Max(1, clip.DurationMs / 2));
            var (sleepClock, sleepy) = Start(autoSleep: true, sleepAfter: sleepAfter);
            Step(sleepClock, sleepy, sleepAfter - sleepClock.NowMilliseconds);
            Check(sleepy.State == PetState.Idle && sleepy.Player.CurrentClip!.Name == action.Clip,
                "Sleep waits for the expression's shared endpoint " + action.Clip);
            Check(sleepy.LastPetInteraction == 0 && sleepy.SleepDeadline == sleepAfter,
                "Expressions do not postpone the existing sleep deadline " + action.Clip);
            UntilState(sleepClock, sleepy, () => sleepy.State == PetState.FallAsleep, clip.DurationMs + 1000);
            Check(sleepClock.NowMilliseconds >= 100 + clip.DurationMs && sleepy.Player.CurrentClip!.Name == "fall_asleep",
                "Overdue sleep begins as soon as the real expression finishes " + action.Clip);
            reports.Add(new { clip = action.Clip, weight = action.Weight, durationMs = clip.DurationMs,
                uniquePngs = clip.Frames.Select(frame => frame.File).Distinct().Count(),
                automaticSelection = true, click = true, hover = true, automaticPause = true,
                drag = true, suspension = true, sleepDeadline = true });
        }
        return reports;
    }

    private static void Step(FakeClock clock, BehaviorController controller, long milliseconds)
    {
        clock.NowMilliseconds += milliseconds;
        controller.Tick();
    }

    private static void UntilState(FakeClock clock, BehaviorController controller, Func<bool> done, long limit)
    {
        var deadline = clock.NowMilliseconds + limit;
        while (!done() && clock.NowMilliseconds < deadline)
            Step(clock, controller, Math.Clamp(controller.NextUpdateInMs, 1, 200));
        Check(done(), "Real-asset transition completes before its deadline: " + controller.Player.CurrentClip!.Name);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Checks.Add(message); }
    private sealed class FakeClock : IClock { public long NowMilliseconds { get; set; } }
}
'@ | Set-Content -LiteralPath (Join-Path $scratch 'Program.cs') -Encoding utf8
    $dotnet = Join-Path $workspaceRoot '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
    $assetReport = Join-Path $reportRoot 'asset-runtime.json'
    & $dotnet run --project (Join-Path $scratch 'RuntimeVerification.csproj') -c Release -- $publishedAssets $assetReport $Cycles $ExpectedIdleActions
    if ($LASTEXITCODE -ne 0) { throw 'Real-asset runtime checks failed.' }
    $summary.assets = Get-Content -LiteralPath $assetReport -Raw -Encoding UTF8 | ConvertFrom-Json
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
