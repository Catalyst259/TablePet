#requires -Version 5.1
param([string]$ApplicationPath = (Join-Path $PSScriptRoot 'TablePet.exe'))
$ErrorActionPreference = 'Stop'
try {
    $application = (Get-Item -LiteralPath $ApplicationPath -ErrorAction Stop).FullName
    $running = @(Get-Process -Name TablePet -ErrorAction SilentlyContinue)
    if ($running.Count -eq 1 -and $running[0].Path -ieq $application) { return }
    if ($running.Count) {
        # Use the app's graceful exit signal; never kill a running pet or edit its settings.
        $exitRequest = Start-Process -FilePath $application -ArgumentList '--quit' -WindowStyle Hidden -PassThru
        if (!$exitRequest.WaitForExit(12000)) { throw 'The current pet did not respond. Exit it from its tray menu, then try again.' }
        foreach ($process in $running) {
            if (!$process.HasExited -and !$process.WaitForExit(12000)) {
                throw 'The current pet is still running. Exit it from its tray menu, then try again.'
            }
        }
    }
    Start-Process -FilePath $application -WorkingDirectory (Split-Path -Parent $application) -WindowStyle Hidden
}
catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, 'TablePet preview') | Out-Null
    exit 1
}
