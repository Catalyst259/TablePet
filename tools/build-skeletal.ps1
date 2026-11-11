param([switch]$Test, [switch]$Publish, [string]$DotnetPath, [string]$BuildLog)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location -LiteralPath $workspaceRoot
[xml]$project = Get-Content -LiteralPath 'src/TablePet.App/TablePet.App.csproj' -Raw -Encoding UTF8
$productVersion = [string]$project.Project.PropertyGroup.Version
if ($productVersion -notmatch '^(\d+\.\d+\.\d+)-skeletal$') { throw 'Expected a skeletal x.y.z-skeletal application version.' }
$version = $Matches[1]
if ($Publish) {
    $output = Join-Path $workspaceRoot "dist/TablePet-$version-win-x64"
    $zip = $output + '.zip'
    # A fresh output prevents stale preview files from leaking into a release.
    if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $zip)) {
        throw "Versioned release output already exists; preserve it before rebuilding: $output"
    }
}
if (!$DotnetPath) {
    $localSdk = Join-Path $workspaceRoot '.tools\dotnet\dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$DotnetPath = (Get-Item -LiteralPath $DotnetPath).FullName
$env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
if (!(Test-Path -LiteralPath 'assets/strawberry-cat/skeletal/pack.json')) { throw 'Missing compiled skeletal pack.' }
& $DotnetPath build TablePet.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Test) {
    & $DotnetPath test TablePet.slnx -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
if ($Publish) {
    # Keep the .NET runtime self-contained. WPF cannot be safely trimmed.
    & $DotnetPath publish src/TablePet.App/TablePet.App.csproj -c Release -r win-x64 --self-contained true -o $output -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md -Destination $output -Force
    $docsOutput = Join-Path $output 'docs'
    New-Item -ItemType Directory -Path $docsOutput -Force | Out-Null
    Get-ChildItem -LiteralPath docs -Filter '*.md' -File | Copy-Item -Destination $docsOutput -Force
    Copy-Item -LiteralPath tools/launch-preview.cmd -Destination (Join-Path $output 'Start-Preview.cmd') -Force
    Copy-Item -LiteralPath tools/launch-preview.ps1 -Destination (Join-Path $output 'Start-Preview.ps1') -Force
    $check = Start-Process -FilePath (Join-Path $output 'TablePet.exe') -ArgumentList '--check-assets' -WindowStyle Hidden -Wait -PassThru
    if ($check.ExitCode -ne 0) { throw "Published asset check failed: $($check.ExitCode)" }
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $zip)" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
    & (Join-Path $PSScriptRoot 'verify-package.ps1') -BuildLog $BuildLog
    Write-Host "Published: $zip"
}
