#Requires -RunAsAdministrator
<#
  Makes http://<name>.local work for devices on the home network, one name per IIS site.

    - Installs LocalNames.ps1 (tiny mDNS responder) to C:\ProgramData\LocalNames and runs it at startup as SYSTEM.
    - Allows mDNS (UDP 5353) in from the local subnet only, for that script.
    - Adds host-name bindings on port 80 to the IIS sites (their existing bindings stay).

  Usage (elevated PowerShell, from the repo folder):
    .\deploy\Setup-LocalNames.ps1                                     # 'Media Manager' site -> media.local
    .\deploy\Setup-LocalNames.ps1 -Bindings @{ 'Media Manager' = 'media.local'; 'Private Media' = 'private.local' }

  Run it again with a different -Bindings to change the names (bindings for names you drop are left in IIS).
  Undo with:  powershell -ExecutionPolicy Bypass -File .\deploy\Setup-LocalNames.ps1 -Uninstall
#>
param(
    # IIS site name -> host name it should answer to (must end in .local)
    [hashtable]$Bindings = @{ 'Media Manager' = 'media.local' },
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$appcmd    = "$env:windir\system32\inetsrv\appcmd.exe"
$installTo = "$env:ProgramData\LocalNames"
$savedFile = Join-Path $installTo 'bindings.json'   # what was installed, so -Uninstall knows what to remove
$taskName  = 'LocalNames mDNS responder'
$ruleName  = 'LocalNames mDNS (UDP 5353 from local network)'
$psExe     = "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe"

$bindingList = @($Bindings.GetEnumerator() | ForEach-Object { [pscustomobject]@{ Site = [string]$_.Key; Host = ([string]$_.Value).ToLowerInvariant() } })
foreach ($b in $bindingList) {
    if ($b.Host -notmatch '^[a-z0-9]([a-z0-9-]*[a-z0-9])?\.local$') { throw "'$($b.Host)' isn't a valid name: use letters, digits and dashes, ending in .local (e.g. media.local)." }
}

function Get-SiteLine($site) { (& $appcmd list site "$site") -join '' }

if ($Uninstall) {
    # Remove the bindings that were installed (older installs didn't record them: fall back to -Bindings)
    $bindings = if (Test-Path $savedFile) { @(Get-Content $savedFile -Raw | ConvertFrom-Json) } else { $bindingList }
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match '\\LocalNames\\LocalNames\.ps1' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    foreach ($b in $bindings) {
        if ((Get-SiteLine $b.Site) -match [regex]::Escape(":80:$($b.Host)")) {
            & $appcmd set site "/site.name:$($b.Site)" "/-bindings.[protocol='http',bindingInformation='*:80:$($b.Host)']" | Out-Null
        }
    }
    Remove-Item $installTo -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'LocalNames removed.' -ForegroundColor Green
    return
}

# 1. Install the responder
New-Item -ItemType Directory -Force $installTo | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'LocalNames.ps1') $installTo -Force
ConvertTo-Json @($bindingList) | Set-Content $savedFile -Encoding UTF8
$names = ($bindingList | ForEach-Object Host | Sort-Object -Unique) -join ','
Write-Host "Responder installed to $installTo (names: $names)"

# 2. Firewall: let other devices' mDNS lookups reach it (local subnet only)
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol UDP -LocalPort 5353 `
        -Program $psExe -RemoteAddress LocalSubnet -Profile Any | Out-Null
}
Write-Host 'Firewall rule in place'

# 3. Startup task (SYSTEM), restarts if it ever stops
$action    = New-ScheduledTaskAction -Execute $psExe -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$installTo\LocalNames.ps1`" -Names $names"
$trigger   = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
                -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match '\\LocalNames\\LocalNames\.ps1' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Host "Startup task '$taskName' registered and started"

# 4. IIS host-name bindings
foreach ($b in $bindingList) {
    if ((Get-SiteLine $b.Site) -notmatch [regex]::Escape(":80:$($b.Host)")) {
        $out = & $appcmd set site "/site.name:$($b.Site)" "/+bindings.[protocol='http',bindingInformation='*:80:$($b.Host)']"
        if ($LASTEXITCODE -ne 0) { throw "Adding binding $($b.Host) to $($b.Site) failed: $out" }
    }
    Write-Host "  http://$($b.Host) -> $($b.Site)"
}

$urls = ($bindingList | ForEach-Object { "http://$($_.Host)" }) -join ' and '
Write-Host "`nDone. Try $urls from your phone (same Wi-Fi)." -ForegroundColor Green
