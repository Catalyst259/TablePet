param(
    [string]$AssetDirectory = 'assets/strawberry-cat',
    [int[]]$SampleRates = @(120, 240),
    [double]$HeightDip = 240,
    [double]$DpiScale = 1.5,
    [string]$ReportDirectory = 'artifacts/motion-performance'
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$assetRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $AssetDirectory))
$reportRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $ReportDirectory))
if (!(Test-Path -LiteralPath (Join-Path $assetRoot 'manifest.json'))) { throw "Missing asset manifest: $assetRoot" }
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$dotnet = Join-Path $workspaceRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$probeProject = Join-Path $workspaceRoot 'tests/TablePet.PlaybackProbe/TablePet.PlaybackProbe.csproj'
& $dotnet build $probeProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Playback probe build failed.' }
$probeDll = Join-Path $workspaceRoot 'tests/TablePet.PlaybackProbe/bin/Release/net10.0-windows/TablePet.PlaybackProbe.dll'
$reports = @()
foreach ($rate in $SampleRates) {
    if ($rate -lt 1 -or $rate -gt 1000) { throw "Invalid sample rate: $rate" }
    $json = & $dotnet $probeDll $assetRoot $rate $HeightDip.ToString([Globalization.CultureInfo]::InvariantCulture) $DpiScale.ToString([Globalization.CultureInfo]::InvariantCulture) | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Playback probe failed at $rate Hz." }
    $report = $json | ConvertFrom-Json
    if (!$report.passed) { throw "Playback validation failed at $rate Hz." }
    $file = Join-Path $reportRoot "playback-$rate-hz.json"
    $json | Set-Content -LiteralPath $file -Encoding utf8
    $reports += [pscustomobject]@{
        sampleRate = $rate
        initializationMs = $report.initializationMs
        peakDecodedPixelCacheMiB = $report.peakDecodedPixelCacheMiB
        processWorkingSetMiB = $report.processWorkingSetMiB
        synchronousCacheMisses = ($report.clips | Measure-Object -Property synchronousCacheMisses -Sum).Sum
        maximumFetchMs = ($report.clips | Measure-Object -Property fetchMaxMs -Maximum).Maximum
        report = $file
    }
}
$summary = [ordered]@{
    checkedAt = [DateTimeOffset]::Now.ToString('o')
    assetDirectory = $assetRoot
    manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $assetRoot 'manifest.json') -Algorithm SHA256).Hash
    heightDip = $HeightDip
    dpiScale = $DpiScale
    reports = $reports
    limitation = 'Noninteractive WPF decoding/cache/timeline test. Does not open or control windows, and does not measure actual display presentation FPS. Active waiting is used only by this probe to avoid Windows timer quantization; the app uses CompositionTarget.Rendering. The decoded-pixel cache budget is not a process-memory limit.'
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
