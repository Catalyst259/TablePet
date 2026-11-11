param([string]$BuildLog)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
[xml]$project = Get-Content -LiteralPath (Join-Path $workspaceRoot 'src/TablePet.App/TablePet.App.csproj') -Raw -Encoding UTF8
$productVersion = [string]$project.Project.PropertyGroup.Version
if ($productVersion -notmatch '^(\d+\.\d+\.\d+)-skeletal$') { throw 'Expected a skeletal x.y.z-skeletal application version.' }
$version = $Matches[1]
$published = Join-Path $workspaceRoot "dist/TablePet-$version-win-x64"
$zipPath = $published + '.zip'
$expectedZipHash = ((Get-Content -LiteralPath ($zipPath + '.sha256') -Raw).Trim() -split '\s+')[0]
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($zipHash -ne $expectedZipHash) { throw 'ZIP checksum mismatch.' }

# Explicit runtime coverage prevents drafts, prompts or legacy frame folders from shipping.
$runtimeNames = @(
    'Assets/strawberry-cat/fallback.png',
    'Assets/strawberry-cat/manifest.json',
    'Assets/strawberry-cat/tray.ico',
    'Assets/strawberry-cat/skeletal/pack.json',
    'Assets/strawberry-cat/skeletal/firefly/firefly.json',
    'Assets/strawberry-cat/skeletal/firefly/textures.bin.z',
    'Assets/strawberry-cat/skeletal/legacy/legacy.bin',
    'Assets/strawberry-cat/skeletal/legacy/legacy.json',
    'Assets/strawberry-cat/skeletal/legacy/verification.json',
    'Assets/strawberry-cat/skeletal/work/work.bin',
    'Assets/strawberry-cat/skeletal/work/work.json'
)
$catalog = Get-Content -LiteralPath (Join-Path $workspaceRoot 'src/TablePet.Core/Companionship/companion-content.zh-CN.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$memoryIds = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($memory in $catalog.memories) {
    if ($memory.id -cnotmatch '^[a-z0-9_]+$' -or !$memoryIds.Add([string]$memory.id)) { throw 'Invalid or duplicate memory illustration ID.' }
    $runtimeNames += "Assets/strawberry-cat/memories/$($memory.id).png"
}
if (!$memoryIds.Count) { throw 'Memory illustration catalog is empty.' }
$expectedPayloadNames = @('TablePet.exe', 'TablePet.pdb', 'TablePet.Core.pdb', 'README.md', 'Start-Preview.cmd', 'Start-Preview.ps1') + $runtimeNames
$expectedPayloadNames += @(Get-ChildItem -LiteralPath (Join-Path $workspaceRoot 'docs') -Filter '*.md' -File | ForEach-Object { 'docs/' + $_.Name })
$expectedPayload = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($name in $expectedPayloadNames) { if (!$expectedPayload.Add($name)) { throw "Duplicate expected package path: $name" } }

function Assert-RelativeZipName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.StartsWith('/') -or $Name.Contains(':') -or $Name.Contains([char]0)) { throw "Unsafe ZIP entry name: $Name" }
    foreach ($part in $Name.Split('/')) {
        if (!$part -or $part -eq '.' -or $part -eq '..' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '[<>"|?*]') {
            throw "Unsafe ZIP entry component: $Name"
        }
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
$files = @()
$assets = @()
$windowsNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$prefix = [IO.Path]::GetFullPath($published).TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
try {
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace([char]92, [char]47)
        if ($name.EndsWith('/')) { continue }
        Assert-RelativeZipName $name
        if (!$windowsNames.Add($name)) { throw "Duplicate or case-colliding ZIP entry: $name" }
        if (!$expectedPayload.Contains($name)) { throw "Unexpected package file: $name" }
        $file = [IO.Path]::GetFullPath((Join-Path $published $name))
        if (!$file.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP entry escapes package.' }
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Published file is missing: $name" }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($entryHash -ne (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "ZIP differs from checked publish: $name" }
        $files += [pscustomobject]@{ file = $name; bytes = $entry.Length; sha256 = $entryHash }
        if ($name.StartsWith('Assets/', [StringComparison]::Ordinal)) {
            $relative = $name.Substring('Assets/strawberry-cat/'.Length)
            $source = Join-Path $workspaceRoot ('assets/strawberry-cat/' + $relative)
            if ($entryHash -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Published asset differs from verified source: $relative" }
            $assets += $name
        }
    }
} finally { $zip.Dispose() }
if (!$expectedPayload.SetEquals([string[]]$files.file)) { throw 'ZIP payload coverage mismatch.' }
$publishedNames = @(Get-ChildItem -LiteralPath $published -Recurse -File | ForEach-Object { $_.FullName.Substring($prefix.Length).Replace([char]92, [char]47) })
if (!$expectedPayload.SetEquals([string[]]$publishedNames)) { throw 'Publish directory contains missing or unexpected files.' }
$exeVersion = (Get-Item -LiteralPath (Join-Path $published 'TablePet.exe')).VersionInfo.ProductVersion
if (($exeVersion -split '\+')[0] -ne $productVersion) { throw "Published executable version differs: $exeVersion" }
$result = [ordered]@{
    passed = $true
    version = $productVersion
    zip = $zipPath
    zipBytes = (Get-Item -LiteralPath $zipPath).Length
    zipSha256 = $zipHash
    packagedFiles = $files.Count
    runtimeAssets = $assets.Count
    memoryIllustrations = $memoryIds.Count
    allPublishedFilesMatchZip = $true
    allRuntimeAssetsMatchVerifiedSource = $true
    containsFrameSequencePngs = $false
    buildLog = $BuildLog
    files = $files
}
$reportDirectory = Join-Path $workspaceRoot "artifacts/release-$version"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$report = Join-Path $reportDirectory 'package-verification.json'
$json = ($result | ConvertTo-Json -Depth 5).Replace([string][char]13 + [char]10, [string][char]10) + [char]10
[IO.File]::WriteAllText($report, $json, (New-Object Text.UTF8Encoding $false))
Write-Output "Package verified: $($files.Count) files, $($assets.Count) runtime assets, SHA-256 $zipHash"
Write-Output "Verification report: $report"
