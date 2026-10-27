param([switch]$Publish, [switch]$Test, [switch]$Launch)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $workspaceRoot
$localDotnet = Join-Path $workspaceRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
} else {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $sdkList = & $dotnet --list-sdks
    if (-not ($sdkList -match '^10\.0\.403 ')) {
        throw '需要 .NET SDK 10.0.403。请先运行 powershell -File tools\setup-dotnet.ps1。'
    }
}
& $dotnet build TablePet.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
if ($Test) {
    & $dotnet test TablePet.slnx -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
}
if ($Publish) {
    [xml]$project = Get-Content -LiteralPath src/TablePet.App/TablePet.App.csproj -Raw -Encoding UTF8
    $version = [string]$project.Project.PropertyGroup.Version
    $publishDirectory = Join-Path $workspaceRoot "dist/TablePet-$version-win-x64"
    $packagePath = Join-Path $workspaceRoot "dist/TablePet-$version-win-x64.zip"
    & $dotnet publish src/TablePet.App/TablePet.App.csproj -c Release -r win-x64 --self-contained true -o $publishDirectory -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
    Copy-Item -LiteralPath README.md -Destination (Join-Path $publishDirectory 'README.md') -Force
    $docsDirectory = Join-Path $publishDirectory 'docs'
    New-Item -ItemType Directory -Path $docsDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath docs -Filter '*.md' | Copy-Item -Destination $docsDirectory -Force
    Copy-Item -LiteralPath tools/launch-preview.cmd -Destination (Join-Path $publishDirectory 'Start-Preview.cmd') -Force
    Copy-Item -LiteralPath tools/launch-preview.ps1 -Destination (Join-Path $publishDirectory 'Start-Preview.ps1') -Force
    $previewDirectory = Join-Path $publishDirectory 'Assets/strawberry-cat/previews'
    New-Item -ItemType Directory -Path $previewDirectory -Force | Out-Null
    foreach ($previewName in @('idle-actions-preview.html', "idle-actions-preview-$version.html", 'idle-actions.gif')) {
        $previewSource = Join-Path $workspaceRoot "assets/strawberry-cat/previews/$previewName"
        if (Test-Path -LiteralPath $previewSource) { Copy-Item -LiteralPath $previewSource -Destination $previewDirectory -Force }
    }
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $packagePath -Force
    Copy-Item -LiteralPath $packagePath -Destination (Join-Path $workspaceRoot 'dist/TablePet-win-x64.zip') -Force
    $checksum = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$checksum  $(Split-Path -Leaf $packagePath)" | Set-Content -LiteralPath "$packagePath.sha256" -Encoding ascii
    "$checksum  TablePet-win-x64.zip" | Set-Content -LiteralPath (Join-Path $workspaceRoot 'dist/TablePet-win-x64.zip.sha256') -Encoding ascii
    Write-Host "已发布：$publishDirectory\TablePet.exe（含 .NET 运行时，无需额外安装）"
}
if ($Launch) {
    $applicationPath = if ($Publish) { Join-Path $publishDirectory 'TablePet.exe' } else { 'src/TablePet.App/bin/Release/net10.0-windows/TablePet.exe' }
    Start-Process -FilePath $applicationPath -WindowStyle Hidden
}
