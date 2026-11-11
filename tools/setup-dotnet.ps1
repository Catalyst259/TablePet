$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$toolDirectory = Join-Path $workspaceRoot '.tools'
$sdkDirectory = Join-Path $toolDirectory 'dotnet'
$sdkZip = Join-Path $toolDirectory 'dotnet-sdk.zip'
$expectedHash = '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48'
New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
if (!(Test-Path -LiteralPath $sdkZip) -or (Get-FileHash -LiteralPath $sdkZip -Algorithm SHA512).Hash -ne $expectedHash) {
    Invoke-WebRequest -Uri 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip' -OutFile $sdkZip
}
if ((Get-FileHash -LiteralPath $sdkZip -Algorithm SHA512).Hash -ne $expectedHash) { throw 'SDK 校验失败，未解压。' }
Expand-Archive -LiteralPath $sdkZip -DestinationPath $sdkDirectory -Force
& (Join-Path $sdkDirectory 'dotnet.exe') --version
