param(
    [string]$IsccPath,
    [string]$ZipPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
[xml]$project = Get-Content -LiteralPath (Join-Path $workspaceRoot 'src/TablePet.App/TablePet.App.csproj') -Raw -Encoding UTF8
$productVersion = [string]$project.Project.PropertyGroup.Version
if ($productVersion -notmatch '^(\d+\.\d+\.\d+)-skeletal$') { throw 'Expected a skeletal x.y.z-skeletal application version.' }
$version = $Matches[1]
if (!$ZipPath) { $ZipPath = Join-Path $workspaceRoot "dist/TablePet-$version-win-x64.zip" }
$ZipPath = (Get-Item -LiteralPath $ZipPath).FullName
$zipHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedHash = ((Get-Content -LiteralPath ($ZipPath + '.sha256') -Raw).Trim() -split '\s+')[0]
if ($zipHash -ne $expectedHash) { throw 'ZIP checksum does not match its release sidecar.' }

if (!$IsccPath) {
    $candidates = @(
        (Join-Path $workspaceRoot '.tools/inno-setup/compiler/ISCC.exe'),
        (Join-Path $workspaceRoot '.tools/inno-setup/compiler/{app}/ISCC.exe'),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (!$IsccPath) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $IsccPath = $command.Source }
    }
}
if (!$IsccPath) { throw 'Install Inno Setup 6 or pass -IsccPath. See installer/README.md.' }
$IsccPath = (Get-Item -LiteralPath $IsccPath).FullName
if (!$OutputDirectory) { $OutputDirectory = Join-Path $workspaceRoot 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$setupPath = Join-Path $OutputDirectory "TablePet-$version-Setup.exe"
if (Test-Path -LiteralPath $setupPath) { throw "Installer already exists; preserve it or choose another -OutputDirectory: $setupPath" }

# Package the exact published ZIP, not mutable build folders or authoring drafts.
$runName = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $workspaceRoot ('artifacts/skeletal-installer-build/' + $runName)
$payload = Join-Path $runDirectory 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $payload)
foreach ($required in @('TablePet.exe', 'Assets/strawberry-cat/manifest.json', 'Assets/strawberry-cat/fallback.png', 'Assets/strawberry-cat/tray.ico', 'Assets/strawberry-cat/skeletal/pack.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) { throw "Incomplete skeletal ZIP: $required" }
}
$exeVersion = (Get-Item -LiteralPath (Join-Path $payload 'TablePet.exe')).VersionInfo.ProductVersion
if (($exeVersion -split '\+')[0] -ne $productVersion) { throw "ZIP executable version $exeVersion differs from source $productVersion." }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compilerLog = Join-Path $runDirectory 'compiler.log'
& $IsccPath '/Qp' "/DAppVersion=$version" "/DPublishDir=$payload" "/DOutputDir=$OutputDirectory" (Join-Path $workspaceRoot 'installer/TablePet.iss') 2>&1 | Tee-Object -FilePath $compilerLog
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed: $LASTEXITCODE" }
if (!(Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw 'Compiler did not create the installer.' }
$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$setupHash  $([IO.Path]::GetFileName($setupPath))" | Set-Content -LiteralPath ($setupPath + '.sha256') -Encoding ASCII
$report = [ordered]@{
    version = $productVersion
    sourceZip = $ZipPath
    sourceZipSha256 = $zipHash
    installer = $setupPath
    installerBytes = (Get-Item -LiteralPath $setupPath).Length
    installerSha256 = $setupHash
    compiler = $IsccPath
    compilerVersion = (Get-Item -LiteralPath $IsccPath).VersionInfo.ProductVersion
    compilerLog = $compilerLog
    payloadDirectory = $payload
}
$json = ($report | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $runDirectory 'build.json'), $json, (New-Object Text.UTF8Encoding $false))
Write-Host "Installer: $setupPath"
Write-Host "SHA-256: $setupHash"
