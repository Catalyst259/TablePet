param([string]$BuildLog)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$published = Join-Path $workspaceRoot 'dist/TablePet.Skeletal-0.5.0-win-x64'
$zipPath = $published + '.zip'
$expectedZipHash = ((Get-Content -LiteralPath ($zipPath + '.sha256') -Raw).Trim() -split '\s+')[0]
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($zipHash -ne $expectedZipHash) { throw 'ZIP checksum mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
$files = @()
$assets = @()
$prefix = [IO.Path]::GetFullPath($published).TrimEnd('\') + '\'
try {
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.EndsWith('/')) { continue }
        $file = [IO.Path]::GetFullPath((Join-Path $published $name))
        if (!$file.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP entry escapes package.' }
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Unexpected package file: $name" }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($entryHash -ne (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "ZIP differs from checked publish: $name" }
        $files += [pscustomobject]@{ file = $name; bytes = $entry.Length; sha256 = $entryHash }
        if ($name.StartsWith('Assets/strawberry-cat/')) {
            $relative = $name.Substring('Assets/strawberry-cat/'.Length)
            if ($relative.StartsWith('frames/')) { throw 'Package contains a raster frame sequence.' }
            $source = Join-Path $workspaceRoot ('assets/strawberry-cat/' + $relative)
            if ($entryHash -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Published asset differs from verified source: $relative" }
            $assets += $relative
        }
    }
} finally { $zip.Dispose() }
$assetRoot = Join-Path $workspaceRoot 'assets/strawberry-cat'
# Preserve local legacy drafts, but never treat them as skeletal runtime files.
$expectedAssets = @(Get-ChildItem -LiteralPath (Join-Path $assetRoot 'skeletal') -Recurse -File)
foreach ($common in @('manifest.json', 'fallback.png', 'tray.ico')) { $expectedAssets += Get-Item -LiteralPath (Join-Path $assetRoot $common) }
$expectedNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($asset in $expectedAssets) { $null = $expectedNames.Add($asset.FullName.Substring($assetRoot.Length + 1).Replace('\', '/')) }
if (!$expectedNames.SetEquals([string[]]$assets)) { throw 'Asset coverage mismatch.' }
if (!($files.file -contains 'TablePet.Skeletal.exe')) { throw 'Self-contained application is missing.' }
$result = [ordered]@{
    passed = $true
    zip = $zipPath
    zipBytes = (Get-Item -LiteralPath $zipPath).Length
    zipSha256 = $zipHash
    packagedFiles = $files.Count
    runtimeAssets = $assets.Count
    allPublishedFilesMatchZip = $true
    allRuntimeAssetsMatchVerifiedSource = $true
    containsFrameSequencePngs = $false
    buildLog = $BuildLog
    files = $files
}
$report = Join-Path $workspaceRoot 'reference/skeletal/package-verification.json'
$json = ($result | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText($report, $json, (New-Object Text.UTF8Encoding $false))
Write-Output "Package verified: $($files.Count) files, $($assets.Count) runtime assets, SHA-256 $zipHash"
