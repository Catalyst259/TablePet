#requires -Version 5.1
param([string]$ApplicationPath = (Join-Path $PSScriptRoot 'TablePet.exe'))
$ErrorActionPreference = 'Stop'
try {
    $application = (Get-Item -LiteralPath $ApplicationPath -ErrorAction Stop).FullName
    $session = (Get-Process -Id $PID).SessionId
    # The old IPC name remains stable across the rename. Do not treat another
    # TablePet.exe (the archived edition) or an isolated --data-dir preview as
    # the default companion instance merely because its process name matches.
    $runningHere = @(Get-CimInstance Win32_Process -Filter "Name = 'TablePet.exe' OR Name = 'TablePet.Skeletal.exe'" |
        Where-Object {
            $_.SessionId -eq $session -and $_.ExecutablePath -ieq $application -and $_.CommandLine -and
            $_.CommandLine -notmatch '(?:^|\s)"?--(?:data-dir|check-assets|quit)"?(?:\s|$)'
        })
    if ($runningHere.Count -eq 1) { return }
    $instance = $null
    try { $instance = [Threading.Mutex]::OpenExisting('Local\TablePet.Skeletal.StrawberryCat') }
    catch [Threading.WaitHandleCannotBeOpenedException] { }
    if ($instance) {
        $ownsInstance = $false
        try {
            try { $ownsInstance = $instance.WaitOne(0) }
            catch [Threading.AbandonedMutexException] { $ownsInstance = $true }
            if (!$ownsInstance) {
                # Ask only the default instance to save and exit. Its mutex,
                # rather than unrelated processes, determines when it is done.
                $exitRequest = Start-Process -FilePath $application -ArgumentList '--quit' -WindowStyle Hidden -PassThru
                if (!$exitRequest.WaitForExit(12000)) { throw 'The current pet did not respond. Exit it from its tray menu, then try again.' }
                try { $ownsInstance = $instance.WaitOne(12000) }
                catch [Threading.AbandonedMutexException] { $ownsInstance = $true }
                if (!$ownsInstance) { throw 'The current pet is still running. Exit it from its tray menu, then try again.' }
            }
        }
        finally {
            if ($ownsInstance) { $instance.ReleaseMutex() }
            $instance.Dispose()
        }
    }
    Start-Process -FilePath $application -WorkingDirectory (Split-Path -Parent $application) -WindowStyle Hidden
}
catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, 'TablePet preview') | Out-Null
    exit 1
}
