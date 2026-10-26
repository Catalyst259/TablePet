param(
    [string]$PublishDirectory = 'dist/TablePet-0.2.1-win-x64',
    [switch]$ProbeAppHost,
    [switch]$SkipAssets,
    [int]$Cycles = 1000
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $PublishDirectory))
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
        var catalog = new AssetCatalog(root);
        Check(!catalog.IsFallback, "Final manifest loads without fallback");
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
        var clock = new FakeClock();
        var settings = new PetSettings { SleepAfterMs = 60000, BlinkMinMs = 3000, BlinkMaxMs = 3000, CooldownMs = 30, HoldMs = 100 };
        var controller = new BehaviorController(clock, catalog.Clips, settings, (a, b) => a);
        var observed = new HashSet<string>();
        void Tick() { controller.Tick(); observed.Add(controller.Player.CurrentClip!.Name); }
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
        var started = Stopwatch.StartNew();
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
        File.WriteAllText(report, JsonSerializer.Serialize(new {
            passed = true, manifest = Path.Combine(root, "manifest.json"), clips = catalog.Clips.Count,
            uniquePngs = files.Length, checkedSizes = new[] { 120, 240, 480 },
            decodedDpiScales = new[] { 100, 125, 150, 200 }, cycles,
            nativeBitmapHeightCap = catalog.CanvasHeight,
            observedClips = observed.Order().ToArray(), checkCount = Checks.Count,
            elapsedMilliseconds = started.ElapsedMilliseconds,
            note = "DPI values above verify bitmap decoding only, not monitor movement. Clock cycles are accelerated, not real-time desktop residency."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {catalog.Clips.Count} clips, {files.Length} PNGs, {cycles} real-asset behavior cycles, {Checks.Count} assertions.");
        return 0;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Checks.Add(message); }
    private sealed class FakeClock : IClock { public long NowMilliseconds { get; set; } }
}
'@ | Set-Content -LiteralPath (Join-Path $scratch 'Program.cs') -Encoding utf8
    $dotnet = Join-Path $workspaceRoot '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
    $assetReport = Join-Path $reportRoot 'asset-runtime.json'
    & $dotnet run --project (Join-Path $scratch 'RuntimeVerification.csproj') -c Release -- $publishedAssets $assetReport $Cycles
    if ($LASTEXITCODE -ne 0) { throw 'Real-asset runtime checks failed.' }
    $summary.assets = Get-Content -LiteralPath $assetReport -Raw -Encoding UTF8 | ConvertFrom-Json
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
