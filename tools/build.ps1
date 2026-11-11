param([switch]$Publish, [switch]$Test, [string]$DotnetPath)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-skeletal.ps1') -Publish:$Publish -Test:$Test -DotnetPath $DotnetPath
