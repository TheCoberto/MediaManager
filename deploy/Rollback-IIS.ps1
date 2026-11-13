#Requires -RunAsAdministrator
<#
  Undoes Setup-IIS.ps1: restores the IIS configuration from the first backup Setup-IIS.ps1 made in
  -BackupDir, and puts back any Web.config files it moved out of media folders.
  The app folders (e.g. C:\inetpub\MediaManager) are left in place; delete them yourself if you want.

  Usage (elevated):  powershell -ExecutionPolicy Bypass -File .\deploy\Rollback-IIS.ps1
                     powershell -ExecutionPolicy Bypass -File .\deploy\Rollback-IIS.ps1 -BackupDir D:\some\other\backup
#>
param(
    [string]$BackupDir = "$env:SystemDrive\inetpub\_mediamanager-backup"
)

$ErrorActionPreference = 'Stop'
$appcmd = "$env:windir\system32\inetsrv\appcmd.exe"

if (-not (Test-Path "$BackupDir\iis-backup-name.txt")) { throw "No rollback point in $BackupDir (iis-backup-name.txt is missing)." }
$backupName = (Get-Content "$BackupDir\iis-backup-name.txt" | Select-Object -First 1).Trim()

& $appcmd restore backup $backupName
if ($LASTEXITCODE -ne 0) { throw "Restoring IIS backup '$backupName' failed." }

# Web.config files that were moved out of media folders
$moved = "$BackupDir\moved-files.csv"
if (Test-Path $moved) {
    # Newest first, so if the same file was moved twice the oldest (original) one is put back last and wins.
    foreach ($m in (Import-Csv $moved | Sort-Object Saved -Descending)) {
        if (Test-Path $m.Saved) {
            Move-Item $m.Saved $m.Original -Force
            Write-Host "Restored $($m.Original)"
        }
    }
    Remove-Item $moved
} else {
    $saved = Get-ChildItem $BackupDir -Recurse -Filter Web.config -ErrorAction SilentlyContinue
    if ($saved) {
        Write-Warning "These saved Web.config files have no record of where they came from; move them back by hand if needed:"
        $saved | ForEach-Object { Write-Warning "  $($_.FullName)" }
    }
}

Write-Host "Rolled back to IIS backup '$backupName'." -ForegroundColor Green
