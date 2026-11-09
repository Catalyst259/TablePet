param(
    [string]$InstallerPath,
    [string]$ZipPath,
    [string]$ExtractorPath
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$version = '0.5.0-skeletal'
if (!$InstallerPath) { $InstallerPath = Join-Path $workspaceRoot 'dist/TablePet.Skeletal-0.5.0-Setup.exe' }
if (!$ZipPath) { $ZipPath = Join-Path $workspaceRoot 'dist/TablePet.Skeletal-0.5.0-win-x64.zip' }
if (!$ExtractorPath) { $ExtractorPath = Join-Path $workspaceRoot '.tools/inno-verify/innounp-2.71.2/innounp.exe' }
foreach ($requiredPath in @($InstallerPath, $ZipPath, $ExtractorPath)) {
    if (!(Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Required file is missing: $requiredPath" }
}
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$ZipPath = (Resolve-Path -LiteralPath $ZipPath).Path
$ExtractorPath = (Resolve-Path -LiteralPath $ExtractorPath).Path
$reportRoot = Join-Path $workspaceRoot 'artifacts/skeletal-installer-check'
$runDirectory = Join-Path $reportRoot ('check-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$extractDirectory = Join-Path $runDirectory 'extracted'
$reportPrefix = [IO.Path]::GetFullPath($reportRoot).TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
if (![IO.Path]::GetFullPath($extractDirectory).StartsWith($reportPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Extraction destination is outside this workspace verification directory.'
}
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$utf8NoBom = New-Object Text.UTF8Encoding $false

function Write-Utf8Lf([string]$Path, [string]$Text) {
    $normalized = $Text.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd([char]10) + "`n"
    [IO.File]::WriteAllText($Path, $normalized, $utf8NoBom)
}
function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Get-SettingsSnapshot([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ path = $Path; exists = $false; bytes = $null; sha256 = $null; lastWriteTimeUtc = $null }
    }
    $item = Get-Item -LiteralPath $Path
    return [pscustomobject]@{
        path = $Path; exists = $true; bytes = $item.Length; sha256 = Get-Sha256 $Path
        lastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
    }
}
function Get-PetProcesses {
    $session = (Get-Process -Id $PID).SessionId
    return @(Get-Process -Name 'TablePet', 'TablePet.Skeletal' -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $session } | ForEach-Object {
            [pscustomobject]@{ id = $_.Id; name = $_.ProcessName; startedAtUtc = $_.StartTime.ToUniversalTime().ToString('o') }
        })
}
function Assert-RelativeZipName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.StartsWith('/') -or $Name.Contains(':') -or $Name.Contains([char]0)) {
        throw "Unsafe ZIP entry name: $Name"
    }
    foreach ($part in $Name.Split('/')) {
        if (!$part -or $part -eq '.' -or $part -eq '..' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '[<>"|?*]') {
            throw "Unsafe ZIP entry component: $Name"
        }
    }
}
function Invoke-Unpacker([string]$Mode, [string]$LogName) {
    $stdout = Join-Path $runDirectory ($LogName + '.stdout.log')
    $stderr = Join-Path $runDirectory ($LogName + '.stderr.log')
    # Paths resolved above cannot contain quotes. No command shell or setup executable is invoked.
    $arguments = @($Mode, '-b', '-q', '-a', '-u')
    if ($Mode -eq '-x') { $arguments += ('"-d' + $extractDirectory + '"') }
    $arguments += ('"' + $InstallerPath + '"')
    $process = Start-Process -FilePath $ExtractorPath -ArgumentList $arguments -WorkingDirectory $runDirectory `
        -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $exitCode = $process.ExitCode
    $text = [IO.File]::ReadAllText($stdout) + "`n" + [IO.File]::ReadAllText($stderr)
    Write-Utf8Lf (Join-Path $runDirectory ($LogName + '.log')) $text
    if ($exitCode -ne 0) { throw "Installer $LogName failed: exit code $exitCode. See $LogName.log." }
    return $exitCode
}

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
$errors = New-Object 'System.Collections.Generic.List[string]'
$verifiedFiles = New-Object 'System.Collections.Generic.List[object]'
$settingsBefore = @()
$beforeProcesses = @()
$snapshotsReady = $false
$result = [ordered]@{
    passed = $false
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    version = $version
    installer = $InstallerPath
    zip = $ZipPath
    extractor = $ExtractorPath
    extractionDirectory = $extractDirectory
    expectedPayloadSource = 'Published ZIP entries and their SHA-256 values only; no workspace document overrides'
    expectedPayloadFiles = 22
    expectedRuntimeAssets = 11
    actualInstallationExecuted = $false
    actualUninstallationExecuted = $false
    processScope = 'Existing TablePet and TablePet.Skeletal processes in the current desktop session'
    notYetVerified = @('Interactive installation and shortcut launch', 'In-place upgrade', 'Actual uninstall', 'A Windows PC without .NET/development tools')
}
try {
    $settingsBefore = @(
        Get-SettingsSnapshot (Join-Path $env:LOCALAPPDATA 'DesktopPet/settings.json')
        Get-SettingsSnapshot (Join-Path $env:LOCALAPPDATA 'DesktopPet.Skeletal/settings.json')
    )
    $beforeProcesses = @(Get-PetProcesses)
    $snapshotsReady = $true
    $result['settingsBefore'] = $settingsBefore
    $result['existingPetProcessesBefore'] = $beforeProcesses
    $result['installerBytes'] = (Get-Item -LiteralPath $InstallerPath).Length
    $result['installerSha256'] = Get-Sha256 $InstallerPath
    $result['zipBytes'] = (Get-Item -LiteralPath $ZipPath).Length
    $result['zipSha256'] = Get-Sha256 $ZipPath
    $result['extractorSha256'] = Get-Sha256 $ExtractorPath
    $result['zipSidecarVerified'] = $false
    if (Test-Path -LiteralPath ($ZipPath + '.sha256') -PathType Leaf) {
        $recordedHash = ((Get-Content -LiteralPath ($ZipPath + '.sha256') -Raw).Trim() -split '\s+')[0]
        if ($recordedHash -notmatch '^[0-9a-fA-F]{64}$' -or $recordedHash.ToLowerInvariant() -ne $result.zipSha256) {
            throw 'Published ZIP SHA-256 sidecar does not match the archive.'
        }
        $result['zipSidecarVerified'] = $true
    }

    # Never rebuild expected files from the publish directory or current README/docs.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    $expected = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
    $windowsNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace([char]92, [char]47)
            if ($name.EndsWith('/')) { continue }
            Assert-RelativeZipName $name
            if (!$windowsNames.Add($name)) { throw "Duplicate or case-colliding ZIP entry: $name" }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
            finally { $stream.Dispose(); $sha.Dispose() }
            $expected.Add($name, [pscustomobject]@{ bytes = $entry.Length; sha256 = $hash })
        }
    } finally { $zip.Dispose() }
    if ($expected.Count -ne 22) { throw "Published ZIP must contain exactly 22 files; found $($expected.Count)." }
    foreach ($required in @('TablePet.Skeletal.exe', 'README.md', 'Start-Preview.cmd', 'Start-Preview.ps1') + $runtimeNames) {
        if (!$expected.ContainsKey($required)) { throw "Published ZIP is missing required file: $required" }
    }
    $assetNames = @($expected.Keys | Where-Object { $_.StartsWith('Assets/', [StringComparison]::Ordinal) })
    if ($assetNames.Count -ne 11 -or @($assetNames | Where-Object { $runtimeNames -cnotcontains $_ }).Count -ne 0) {
        throw 'Published ZIP does not contain exactly the 11 skeletal runtime asset files.'
    }
    if (@($expected.Keys | Where-Object { $_ -match '(^|/)frames(/|$)' }).Count -gt 0) {
        throw 'Published ZIP contains a legacy raster-frame directory.'
    }

    $result['integrityExitCode'] = Invoke-Unpacker '-t' 'integrity'
    $result['extractionExitCode'] = Invoke-Unpacker '-x' 'extraction'
    $payloadRoot = Join-Path $extractDirectory '{app}'
    if (!(Test-Path -LiteralPath $payloadRoot -PathType Container)) { throw 'Expected the extracted {app} payload directory.' }
    $payloadPrefix = [IO.Path]::GetFullPath($payloadRoot).TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
    $extractPrefix = [IO.Path]::GetFullPath($extractDirectory).TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
    foreach ($item in Get-ChildItem -LiteralPath $extractDirectory -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unexpected extracted reparse point: $($item.FullName)" }
        if (!$item.PSIsContainer -and !$item.FullName.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            $relative = $item.FullName.Substring($extractPrefix.Length).Replace([char]92, [char]47)
            # innounp may generate its reconstructed script as inspection metadata.
            if ($relative -cne 'install_script.iss') { throw "Unexpected installer payload outside {app}: $relative" }
        }
    }
    $files = @(Get-ChildItem -LiteralPath $payloadRoot -File -Force -Recurse)
    if ($files.Count -ne $expected.Count) { throw "Payload file count differs: $($files.Count) actual, $($expected.Count) expected." }
    foreach ($file in $files) {
        if (!$file.FullName.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Extracted payload path escaped its directory.' }
        $relative = $file.FullName.Substring($payloadPrefix.Length).Replace([char]92, [char]47)
        if (!$expected.ContainsKey($relative)) { throw "Unexpected or differently cased payload file: $relative" }
        $actualHash = Get-Sha256 $file.FullName
        if ($file.Length -ne $expected[$relative].bytes -or $actualHash -ne $expected[$relative].sha256) {
            throw "Extracted payload differs from the published ZIP: $relative"
        }
        $verifiedFiles.Add([pscustomobject]@{ path = $relative; bytes = $file.Length; sha256 = $actualHash })
    }
    Write-Utf8Lf (Join-Path $runDirectory 'verified-files.json') ($verifiedFiles.ToArray() | ConvertTo-Json -Depth 4)
    $result['verifiedPayloadFiles'] = $verifiedFiles.Count
    $result['verifiedRuntimeAssets'] = $runtimeNames.Count
    $result['payloadBytes'] = [long](($files | Measure-Object Length -Sum).Sum)
    $result['allPayloadFilesMatchPublishedZip'] = $true
    $result['containsFrameSequencePngs'] = $false

    $executablePath = Join-Path $payloadRoot 'TablePet.Skeletal.exe'
    $productVersion = (Get-Item -LiteralPath $executablePath).VersionInfo.ProductVersion
    if (($productVersion -split '\+')[0] -ne $version) { throw "Extracted application version differs: $productVersion" }
    $setupVersion = (Get-Item -LiteralPath $InstallerPath).VersionInfo.ProductVersion.Trim()
    if (@($version, '0.5.0', '0.5.0.0') -notcontains $setupVersion) { throw "Installer product version differs: $setupVersion" }
    $result['applicationProductVersion'] = $productVersion
    $result['installerProductVersion'] = $setupVersion
    $result['deploymentLayout'] = 'Self-contained single-file TablePet.Skeletal.exe; byte-identical to the published ZIP'

    # This flag exits before any settings, windows, tray icons or instance signals.
    # The setup program and uninstaller are never started by this script.
    $checkProcess = Start-Process -FilePath $executablePath -ArgumentList '--check-assets' -WorkingDirectory $payloadRoot `
        -WindowStyle Hidden -PassThru -Wait
    $result['assetCheckExitCode'] = $checkProcess.ExitCode
    if ($checkProcess.ExitCode -ne 0) { throw "Extracted application asset check failed: $($checkProcess.ExitCode)" }
    if ((Get-Sha256 $ZipPath) -ne $result.zipSha256 -or (Get-Sha256 $InstallerPath) -ne $result.installerSha256) {
        throw 'The ZIP or installer changed during verification; rerun against stable release files.'
    }
    $result['releaseInputsUnchanged'] = $true
} catch {
    $errors.Add($_.Exception.Message)
} finally {
    if ($snapshotsReady) {
        try {
            $settingsAfter = @($settingsBefore | ForEach-Object { Get-SettingsSnapshot $_.path })
            $settingsUnchanged = $true
            for ($i = 0; $i -lt $settingsBefore.Count; $i++) {
                $before = $settingsBefore[$i]; $after = $settingsAfter[$i]
                if ($before.exists -ne $after.exists -or $before.bytes -ne $after.bytes -or $before.sha256 -ne $after.sha256 -or $before.lastWriteTimeUtc -ne $after.lastWriteTimeUtc) {
                    $settingsUnchanged = $false
                    $errors.Add("User settings changed during verification: $($before.path). Investigate concurrent application/user changes.")
                }
            }
            $result['settingsAfter'] = $settingsAfter
            $result['userSettingsUnchanged'] = $settingsUnchanged
            $processesPreserved = $true
            foreach ($original in $beforeProcesses) {
                $running = Get-Process -Id $original.id -ErrorAction SilentlyContinue
                if (!$running -or $running.ProcessName -ne $original.name -or $running.StartTime.ToUniversalTime().ToString('o') -ne $original.startedAtUtc) {
                    $processesPreserved = $false
                    $errors.Add("An existing pet process changed during verification: $($original.name), PID $($original.id).")
                }
            }
            $result['existingPetProcessesPreserved'] = $processesPreserved
            $result['existingPetProcessesAfter'] = @(Get-PetProcesses)
        } catch { $errors.Add('State-preservation check failed: ' + $_.Exception.Message) }
    }
    $result['passed'] = $errors.Count -eq 0 -and $snapshotsReady -and $verifiedFiles.Count -eq 22 -and $result.assetCheckExitCode -eq 0
    $result['errors'] = $errors.ToArray()
    $json = $result | ConvertTo-Json -Depth 8
    Write-Utf8Lf (Join-Path $runDirectory 'summary.json') $json
    Write-Utf8Lf (Join-Path $reportRoot 'summary.json') $json
}
Write-Output $json
if (!$result.passed) { throw "Installer verification failed. See $(Join-Path $runDirectory 'summary.json')." }
