param(
    [string]$IsccPath,
    [string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
[xml]$project = Get-Content (Join-Path $workspaceRoot 'src/TablePet.App/TablePet.App.csproj') -Raw -Encoding UTF8
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Installer requires a numeric x.y.z version.' }
if (!$PublishDirectory) { $PublishDirectory = Join-Path $workspaceRoot "dist/TablePet-$version-win-x64" }
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
foreach ($required in @('TablePet.exe', 'TablePet.dll', 'TablePet.runtimeconfig.json', 'coreclr.dll', 'PresentationFramework.dll', 'Assets/strawberry-cat/manifest.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $PublishDirectory $required) -PathType Leaf)) { throw "Incomplete self-contained release: $required" }
}
$payloadVersion = (Get-Item -LiteralPath (Join-Path $PublishDirectory 'TablePet.exe')).VersionInfo.ProductVersion
if (($payloadVersion -split '\+')[0] -ne $version) { throw "Payload version $payloadVersion does not match source $version." }
if (!$IsccPath) {
    $candidates = @(
        (Join-Path $workspaceRoot '.tools/inno-setup/compiler/ISCC.exe'),
        (Join-Path $workspaceRoot '.tools/inno-setup/compiler/{app}/ISCC.exe'),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$IsccPath) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $IsccPath = $command.Source }
    }
}
if (!$IsccPath -or !(Test-Path -LiteralPath $IsccPath)) {
    throw 'Install Inno Setup 6 from https://jrsoftware.org/isdl.php or pass -IsccPath to its ISCC.exe. See installer/README.md.'
}
$IsccPath = (Resolve-Path -LiteralPath $IsccPath).Path
$outputDirectory = Join-Path $workspaceRoot 'dist'
$setupPath = Join-Path $outputDirectory "TablePet-$version-Setup.exe"
if (Test-Path -LiteralPath $setupPath) { throw "Installer already exists; preserve it or choose a new version before rebuilding: $setupPath" }
New-Item -ItemType Directory -Force $outputDirectory | Out-Null
& $IsccPath '/Qp' "/DAppVersion=$version" "/DPublishDir=$PublishDirectory" "/DOutputDir=$outputDirectory" (Join-Path $workspaceRoot 'installer/TablePet.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed: $LASTEXITCODE" }
if (!(Test-Path -LiteralPath $setupPath)) { throw 'Compiler did not create the installer.' }
$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($setupPath))" | Set-Content -LiteralPath ($setupPath + '.sha256') -Encoding ASCII
Write-Host "Installer: $setupPath"
Write-Host "SHA-256: $hash"
