param([switch]$AuditOnly)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$workspacePrefix = $workspaceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$reportRoot = Join-Path $workspaceRoot 'artifacts/source-package-check'
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
[xml]$project = [IO.File]::ReadAllText((Join-Path $workspaceRoot 'src/TablePet.App/TablePet.App.csproj'), [Text.Encoding]::UTF8)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'The project version is not a safe archive filename.' }
$archiveStem = "TablePet-$version-source"
$archivePath = Join-Path $workspaceRoot "dist/$archiveStem.zip"
$rootFiles = @('.gitignore', 'Directory.Build.props', 'global.json', 'package.json', 'package-lock.json', 'README.md', 'TablePet.slnx')
$sourceFolders = @('src', 'tests', 'assets', 'reference', 'docs', 'tools', 'installer')
$excludedDirectoryNames = @('.git', '.tools', 'node_modules', 'bin', 'obj', 'dist', 'artifacts', '.vs')
$excludedFilePattern = '(?i)(^\.env(?:\..*)?$|^settings\.json$|^credentials?(?:\..*)?$|^secrets?(?:\..*)?$|^tokens?(?:\..*)?$|\.(?:user|suo|pfx|p12|pem|key)$)'
$sources = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
$excluded = New-Object 'System.Collections.Generic.List[string]'
$missingFolders = New-Object 'System.Collections.Generic.List[string]'

function Assert-WorkspacePath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (!$absolute.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the source workspace: $absolute"
    }
    return $absolute
}

function Get-RelativeSourcePath([string]$Path) {
    $absolute = Assert-WorkspacePath $Path
    return $absolute.Substring($workspacePrefix.Length).Replace('\', '/')
}

foreach ($relative in $rootFiles) {
    $item = Get-Item -LiteralPath (Join-Path $workspaceRoot $relative) -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source links are not followed: $relative" }
    $sources.Add($item)
}
foreach ($folder in $sourceFolders) {
    $folderPath = Join-Path $workspaceRoot $folder
    if (!(Test-Path -LiteralPath $folderPath -PathType Container)) { $missingFolders.Add($folder); continue }
    $pending = New-Object 'System.Collections.Generic.Stack[System.IO.DirectoryInfo]'
    $pending.Push((Get-Item -LiteralPath $folderPath -Force))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $null = Assert-WorkspacePath $directory.FullName
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source links are not followed: $($directory.FullName)" }
        foreach ($item in Get-ChildItem -LiteralPath $directory.FullName -Force) {
            $relative = Get-RelativeSourcePath $item.FullName
            if ($item.PSIsContainer -and $excludedDirectoryNames -contains $item.Name) { $excluded.Add($relative + '/'); continue }
            if (!$item.PSIsContainer -and $item.Name -match $excludedFilePattern) { $excluded.Add($relative); continue }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source links are not followed: $relative" }
            if ($item.PSIsContainer) { $pending.Push($item) } else { $sources.Add($item) }
        }
    }
}
$ordered = @($sources | Sort-Object FullName)
$fileList = @($ordered | ForEach-Object { [ordered]@{ path = (Get-RelativeSourcePath $_.FullName); bytes = $_.Length } })
$inventory = [ordered]@{
    checkedAt = [DateTimeOffset]::Now.ToString('o')
    version = $version
    sourceFileCount = $ordered.Count
    uncompressedBytes = [long](($ordered | Measure-Object Length -Sum).Sum)
    includedRootFiles = $rootFiles
    includedFolders = $sourceFolders
    missingFolders = @($missingFolders)
    excludedDirectoryNames = $excludedDirectoryNames
    excludedFilesOrDirectories = @($excluded)
    files = $fileList
}
$inventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportRoot 'inventory.json') -Encoding UTF8
if ($AuditOnly) {
    [ordered]@{ auditOnly = $true; files = $ordered.Count; bytes = $inventory.uncompressedBytes; missingFolders = @($missingFolders); inventory = (Join-Path $reportRoot 'inventory.json') } | ConvertTo-Json
    return
}
if ($missingFolders.Count -gt 0) { throw ('Complete these source folders before final packaging: ' + ($missingFolders -join ', ')) }
$null = Assert-WorkspacePath $archivePath
if (Test-Path -LiteralPath $archivePath) { throw "Archive already exists and will not be overwritten: $archivePath" }
New-Item -ItemType Directory -Path (Split-Path -Parent $archivePath) -Force | Out-Null
$temporaryPath = Assert-WorkspacePath ($archivePath + '.building-' + [Guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$expected = @{}
$manifestLines = New-Object 'System.Collections.Generic.List[string]'
$archive = $null
try {
    $archive = [IO.Compression.ZipFile]::Open($temporaryPath, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($file in $ordered) {
        $relative = Get-RelativeSourcePath $file.FullName
        $entryName = "$archiveStem/$relative"
        if ($expected.ContainsKey($entryName)) { throw "Duplicate archive path: $entryName" }
        $sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $expected[$entryName] = [pscustomobject]@{ source = $file.FullName; sha256 = $sha256; bytes = $file.Length }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $manifestLines.Add("$sha256  $relative")
    }
    $manifestText = ($manifestLines -join "`n") + "`n"
    $manifestEntryName = "$archiveStem/SOURCE-MANIFEST.sha256"
    $manifestEntry = $archive.CreateEntry($manifestEntryName, [IO.Compression.CompressionLevel]::Optimal)
    $writer = New-Object IO.StreamWriter($manifestEntry.Open(), ([Text.UTF8Encoding]::new($false)))
    try { $writer.Write($manifestText) } finally { $writer.Dispose() }
    $archive.Dispose(); $archive = $null

    # Verify every archived byte against the recorded digest and current source.
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryPath)
    if ($archive.Entries.Count -ne $expected.Count + 1) { throw 'Unexpected archive entry count.' }
    $verified = 0
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -eq $manifestEntryName) {
            $reader = New-Object IO.StreamReader($entry.Open(), [Text.Encoding]::UTF8)
            try { if ($reader.ReadToEnd() -cne $manifestText) { throw 'The embedded source manifest differs.' } } finally { $reader.Dispose() }
            continue
        }
        if (!$expected.ContainsKey($entry.FullName)) { throw "Unexpected archive entry: $($entry.FullName)" }
        $record = $expected[$entry.FullName]
        if ($entry.Length -ne $record.bytes) { throw "Length mismatch: $($entry.FullName)" }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actualHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($actualHash -ne $record.sha256) { throw "Archive content differs from source digest: $($entry.FullName)" }
        if ((Get-FileHash -LiteralPath $record.source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $record.sha256) {
            throw "Source changed while packaging; retry after edits finish: $($entry.FullName)"
        }
        $verified++
    }
    $archive.Dispose(); $archive = $null
    # Only the newly created temporary ZIP is renamed; existing files are preserved.
    [IO.File]::Move($temporaryPath, $archivePath)
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$archiveHash  $archiveStem.zip" | Set-Content -LiteralPath ($archivePath + '.sha256') -Encoding ASCII
    $result = [ordered]@{
        passed = $true
        checkedAt = [DateTimeOffset]::Now.ToString('o')
        version = $version
        archive = $archivePath
        archiveBytes = (Get-Item -LiteralPath $archivePath).Length
        sha256 = $archiveHash
        verifiedSourceFiles = $verified
        embeddedManifest = $manifestEntryName
        sourceBytes = $inventory.uncompressedBytes
        exclusionAudit = 'Allowlisted source roots only; build output, SDKs, local dependencies, Git metadata, user preferences and credential-like files excluded.'
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding UTF8
    $result | ConvertTo-Json -Depth 5
}
finally {
    if ($archive) { $archive.Dispose() }
    # Leave a failed newly-created temporary ZIP for diagnosis; never remove source files.
}
