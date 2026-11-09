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
$expectedAssets = @(Get-ChildItem -LiteralPath (Join-Path $workspaceRoot 'assets/strawberry-cat') -Recurse -File)
if ($assets.Count -ne $expectedAssets.Count -or $assets.Count -ne 11) { throw 'Asset coverage mismatch.' }
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
    buildLog = 'artifacts/final-build.log'
    files = $files
}
$report = Join-Path $workspaceRoot 'reference/skeletal/package-verification.json'
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $report -Encoding UTF8
Write-Output "Package verified: $($files.Count) files, $($assets.Count) runtime assets, SHA-256 $zipHash"
