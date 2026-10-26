param(
    [string]$InstallerPath,
    [string]$PublishDirectory,
    [string]$ExtractorPath
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
[xml]$project = Get-Content (Join-Path $workspaceRoot 'src/TablePet.App/TablePet.App.csproj') -Raw -Encoding UTF8
$version = [string]$project.Project.PropertyGroup.Version
if (!$InstallerPath) { $InstallerPath = Join-Path $workspaceRoot "dist/TablePet-$version-Setup.exe" }
if (!$PublishDirectory) { $PublishDirectory = Join-Path $workspaceRoot "dist/TablePet-$version-win-x64" }
if (!$ExtractorPath) { $ExtractorPath = Join-Path $workspaceRoot '.tools/inno-verify/innounp-2.71.2/innounp.exe' }
foreach ($path in @($InstallerPath, $ExtractorPath)) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file is missing: $path" }
}
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$ExtractorPath = (Resolve-Path -LiteralPath $ExtractorPath).Path
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$publishPrefix = $PublishDirectory.TrimEnd('\') + '\'
$reportRoot = Join-Path $workspaceRoot 'artifacts/installer-check'
$runDirectory = Join-Path $reportRoot ('check-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$extractDirectory = Join-Path $runDirectory 'extracted'
$beforeProcesses = @(Get-Process -Name TablePet -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{ id = $_.Id; startedAt = $_.StartTime.ToUniversalTime().ToString('o') }
})
$settingsPath = Join-Path $env:LOCALAPPDATA 'DesktopPet/settings.json'
$settingsHashBefore = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash } else { $null }

# Run the unpacker, never the setup program. This does not install or unregister anything.
& $ExtractorPath '-t' '-b' '-q' $InstallerPath 2>&1 | Set-Content -LiteralPath (Join-Path $runDirectory 'integrity.log') -Encoding UTF8
if ($LASTEXITCODE -ne 0) { throw "Installer integrity test failed: $LASTEXITCODE" }
& $ExtractorPath '-x' '-b' '-q' ('-d' + $extractDirectory) $InstallerPath 2>&1 | Set-Content -LiteralPath (Join-Path $runDirectory 'extraction.log') -Encoding UTF8
if ($LASTEXITCODE -ne 0) { throw "Installer extraction failed: $LASTEXITCODE" }
$payloadRoot = Join-Path $extractDirectory '{app}'
if (!(Test-Path -LiteralPath (Join-Path $payloadRoot 'TablePet.exe'))) {
    $matches = @(Get-ChildItem -LiteralPath $extractDirectory -Filter TablePet.exe -File -Recurse)
    if ($matches.Count -ne 1) { throw 'Expected exactly one extracted TablePet.exe payload.' }
    $payloadRoot = $matches[0].DirectoryName
}
$payloadPrefix = $payloadRoot.TrimEnd('\') + '\'
$expected = @{}
foreach ($file in Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse) {
    $relative = $file.FullName.Substring($publishPrefix.Length).Replace('\', '/')
    $expected[$relative] = $file.FullName
}
# These are the only publication-document overrides declared in TablePet.iss.
$expected['README.md'] = Join-Path $workspaceRoot 'README.md'
$expected['docs/INSTALLATION.md'] = Join-Path $workspaceRoot 'docs/INSTALLATION.md'
$files = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse)
if ($files.Count -ne $expected.Count) { throw "Payload file count differs: $($files.Count) actual, $($expected.Count) expected." }
$verifiedFiles = New-Object 'System.Collections.Generic.List[object]'
foreach ($file in $files) {
    $relative = $file.FullName.Substring($payloadPrefix.Length).Replace('\', '/')
    if (!$expected.ContainsKey($relative)) { throw "Unexpected payload file: $relative" }
    $sourceHash = (Get-FileHash -LiteralPath $expected[$relative] -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $sourceHash) { throw "Payload content differs from release: $relative" }
    $verifiedFiles.Add([pscustomobject]@{ path = $relative; bytes = $file.Length; sha256 = $actualHash })
}
$verifiedFiles | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runDirectory 'verified-files.json') -Encoding UTF8
$executablePath = Join-Path $payloadRoot 'TablePet.exe'
$productVersion = (Get-Item -LiteralPath $executablePath).VersionInfo.ProductVersion
if (($productVersion -split '\+')[0] -ne $version) { throw "Extracted application version differs: $productVersion" }
$setupVersion = (Get-Item -LiteralPath $InstallerPath).VersionInfo.ProductVersion.Trim()
if ($setupVersion -ne $version -and $setupVersion -ne "$version.0") { throw "Installer version differs: $setupVersion" }
foreach ($required in @('coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll', 'WindowsBase.dll', 'TablePet.runtimeconfig.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $payloadRoot $required))) { throw "Bundled runtime is missing: $required" }
}
$runtime = Get-Content (Join-Path $payloadRoot 'TablePet.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$frameworkNames = @($runtime.runtimeOptions.includedFrameworks | ForEach-Object { $_.name })
foreach ($requiredFramework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    if ($frameworkNames -notcontains $requiredFramework) { throw "Self-contained runtime is missing: $requiredFramework" }
}
$assetRoot = Join-Path $payloadRoot 'Assets/strawberry-cat'
$manifest = Get-Content (Join-Path $assetRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$framePaths = @($manifest.clips.PSObject.Properties | ForEach-Object { $_.Value.frames } | ForEach-Object { $_.file })
$uniqueFrames = @($framePaths | Sort-Object -Unique)
foreach ($frame in $uniqueFrames) {
    $framePath = [IO.Path]::GetFullPath((Join-Path $assetRoot $frame))
    if (!$framePath.StartsWith(($assetRoot.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw "Frame path escapes asset folder: $frame" }
    if (!(Test-Path -LiteralPath $framePath -PathType Leaf)) { throw "Referenced frame is missing: $frame" }
}
if ($version -eq '0.2.1' -and ($uniqueFrames.Count -ne 1316 -or $framePaths.Count -ne 2062)) {
    throw "Version 0.2.1 animation counts differ: $($uniqueFrames.Count) files / $($framePaths.Count) slots."
}

# This app mode exits before creating windows, instance signals, settings, or tray icons.
$checkProcess = Start-Process -FilePath $executablePath -ArgumentList '--check-assets' -WorkingDirectory $payloadRoot -WindowStyle Hidden -PassThru -Wait
if ($checkProcess.ExitCode -ne 0) { throw "Extracted application asset check failed: $($checkProcess.ExitCode)" }
$settingsHashAfter = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash } else { $null }
if ($settingsHashBefore -ne $settingsHashAfter) { throw 'User settings changed during the package verification; investigate concurrent changes.' }
foreach ($originalProcess in $beforeProcesses) {
    $process = Get-Process -Id $originalProcess.id -ErrorAction SilentlyContinue
    if (!$process -or $process.StartTime.ToUniversalTime().ToString('o') -ne $originalProcess.startedAt) {
        throw "The previously running pet process changed during verification: $($originalProcess.id)"
    }
}
$result = [ordered]@{
    passed = $true
    checkedAt = [DateTimeOffset]::Now.ToString('o')
    version = $version
    installer = $InstallerPath
    installerBytes = (Get-Item -LiteralPath $InstallerPath).Length
    installerSHA256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    installerSignature = [string](Get-AuthenticodeSignature -LiteralPath $InstallerPath).Status
    extractionDirectory = $extractDirectory
    verifiedPayloadFiles = $verifiedFiles.Count
    payloadBytes = [long](($files | Measure-Object Length -Sum).Sum)
    integrityCheck = 'passed'
    payloadSHA256Comparison = 'passed for every file'
    runtimeFrameworks = @($runtime.runtimeOptions.includedFrameworks)
    referencedPNGFiles = $uniqueFrames.Count
    animationFrameSlots = $framePaths.Count
    assetCheckExitCode = $checkProcess.ExitCode
    existingPetProcessIdsPreserved = @($beforeProcesses | ForEach-Object { $_.id })
    userSettingsUnchanged = $true
    actualInstallationExecuted = $false
    notYetVerified = @('Interactive installation and shortcut launch', 'In-place upgrade', 'Actual uninstall', 'A second Windows PC without development tools', 'Mixed-DPI multi-monitor behavior')
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDirectory 'summary.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 6
