#Requires -RunAsAdministrator
<#
  Sets up one IIS site to run Media Manager, or updates an existing one. Run it once per site.

  It:
    1. Backs up the IIS configuration. The first backup in -BackupDir is kept as the rollback point for
       Rollback-IIS.ps1, even if you run this script again later.
    2. Creates the app pool and site if they don't exist yet (that needs -Port), or repoints an existing site.
    3. Copies the published app (-AppSource) into the site's own folder (-AppDir), plus this site's
       Site.config / ConnectionStrings.config from -ConfigDir. Those two files are never overwritten by
       publishing later.
    4. Adds (or repoints) the "/media" virtual directory to the site's media folder (-MediaRoot).
    5. Moves a Web.config found directly in the media folder out of the way: it would apply to /media and
       could break it or switch off the login check for media files.
    6. With -GrantMediaAccess, lets the app pool read and change files in the media folder (needed for
       uploads, moving, renaming and the Recycle Bin).

  Before running it:
    - Publish the app to a folder:   .\deploy\Publish-All.ps1 -To "$env:LOCALAPPDATA\MediaManagerStaging\app"
    - Make a folder holding this site's Site.config and ConnectionStrings.config (start from the .example
      files in the repo; set the password with deploy\New-PasswordHash.ps1 -SiteConfig <that Site.config>).

  Examples (elevated PowerShell, from the repo folder):
    # A new site on port 8080 showing D:\Media
    powershell -ExecutionPolicy Bypass -File .\deploy\Setup-IIS.ps1 -SiteName 'Media Manager' -Port 8080 `
        -MediaRoot 'D:\Media' -ConfigDir 'C:\MediaManagerConfig\MediaManager' -GrantMediaAccess

    # An existing site: copy in the new app and repoint it, keeping the Site.config it already has
    powershell -ExecutionPolicy Bypass -File .\deploy\Setup-IIS.ps1 -SiteName 'Media Manager' -MediaRoot 'D:\Media'

  Undo with deploy\Rollback-IIS.ps1 (same -BackupDir).
#>
param(
    # IIS site name. Created if it doesn't exist (then -Port is required).
    [Parameter(Mandatory)][string]$SiteName,

    # Folder whose files the site shows. Becomes the site's "/media" virtual directory.
    [Parameter(Mandatory)][string]$MediaRoot,

    # Where the app's files go. Default: C:\inetpub\<SiteName without spaces>.
    [string]$AppDir,

    # App pool. Default: the existing site's pool, or one named after the site for a new site.
    [string]$AppPool,

    # Port for a NEW site's http binding (ignored for an existing site).
    [ValidateRange(1, 65535)][int]$Port,

    # Optional host name for a NEW site's binding (e.g. media.local). Empty = any host name.
    [string]$HostName = '',

    # The published app (the folder with Web.config and bin\).
    [string]$AppSource = "$env:LOCALAPPDATA\MediaManagerStaging\app",

    # Folder with this site's Site.config and ConnectionStrings.config. Optional if -AppDir already has them.
    [string]$ConfigDir,

    # Where the IIS backup name and anything moved out of the media folder are kept.
    [string]$BackupDir = "$env:SystemDrive\inetpub\_mediamanager-backup",

    # Give the app pool modify rights on -MediaRoot (can take a while on a big folder tree).
    [switch]$GrantMediaAccess
)

$ErrorActionPreference = 'Stop'
$appcmd = "$env:windir\system32\inetsrv\appcmd.exe"

function Invoke-AppCmd {
    $out = & $appcmd @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "appcmd $($args -join ' ') failed: $out" }
    $out
}

if (-not $AppDir) { $AppDir = Join-Path "$env:SystemDrive\inetpub" ($SiteName -replace '[^A-Za-z0-9_.-]', '') }
$MediaRoot = $MediaRoot.TrimEnd('\')
if ($MediaRoot -match '^[A-Za-z]:$') { $MediaRoot += '\' }   # "E:" -> "E:\" (a bare drive letter means the current folder on that drive)
$configFiles = 'Site.config', 'ConnectionStrings.config'

# ---- Pre-flight checks (nothing is changed until all of these pass) ----
if (-not (Test-Path $appcmd)) { throw "IIS isn't installed (no $appcmd). See the README: Windows features to turn on." }
$aspnet = Get-WindowsOptionalFeature -Online -FeatureName IIS-ASPNET45 -ErrorAction SilentlyContinue
if ($aspnet -and $aspnet.State -ne 'Enabled') { throw "IIS's ASP.NET 4.x support is off. Turn on 'ASP.NET 4.8' under Internet Information Services > World Wide Web Services > Application Development Features." }
if (-not (Test-Path "$AppSource\Web.config") -or -not (Test-Path "$AppSource\bin\MediaManager.dll")) {
    throw "No published app in $AppSource. Publish first:  .\deploy\Publish-All.ps1 -To `"$AppSource`""
}
if (-not (Test-Path $MediaRoot -PathType Container)) { throw "Media folder $MediaRoot does not exist." }
if ($ConfigDir -and -not (Test-Path $ConfigDir -PathType Container)) { throw "Config folder $ConfigDir does not exist." }
foreach ($f in $configFiles) {
    $have = ($ConfigDir -and (Test-Path (Join-Path $ConfigDir $f))) -or (Test-Path (Join-Path $AppDir $f))
    if (-not $have) {
        throw "No $f for this site: put one in -ConfigDir (start from $f.example in the repo), or in $AppDir."
    }
}

$siteExists = [bool](& $appcmd list site "$SiteName")
if (-not $siteExists -and -not $Port) { throw "IIS site '$SiteName' doesn't exist. To create it, also give -Port (e.g. -Port 8080)." }
if (-not $AppPool) {
    $AppPool = if ($siteExists) { (& $appcmd list app "$SiteName/" /text:applicationPool) } else { $SiteName }
}

# ---- 1. Back up IIS configuration ----
New-Item -ItemType Directory -Force $BackupDir | Out-Null
$backupName = "mediamanager-$(Get-Date -Format yyyyMMdd-HHmmss)"
Invoke-AppCmd add backup $backupName | Out-Null
if (-not (Test-Path "$BackupDir\iis-backup-name.txt")) { Set-Content "$BackupDir\iis-backup-name.txt" $backupName }
Write-Host "IIS config backed up as '$backupName' (rollback point: $(Get-Content "$BackupDir\iis-backup-name.txt"))" -ForegroundColor Green

Write-Host "`n=== $SiteName ===" -ForegroundColor Cyan

# ---- 2. App folder ----
New-Item -ItemType Directory -Force $AppDir | Out-Null
robocopy $AppSource $AppDir /E /XF Site.config ConnectionStrings.config /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed for $AppDir (exit $LASTEXITCODE)" }
if ($ConfigDir) {
    foreach ($f in $configFiles) {
        $src = Join-Path $ConfigDir $f
        if (Test-Path $src) { Copy-Item $src $AppDir -Force; Write-Host "  $f copied from $ConfigDir" }
    }
}
[xml]$siteConfig = Get-Content (Join-Path $AppDir 'Site.config')
if (-not ($siteConfig.appSettings.add | Where-Object { $_.key -eq 'LoginPasswordHash' -and $_.value -and $_.value -notmatch 'REPLACE_ME' })) {
    Write-Warning "Site.config has no LoginPasswordHash, so nobody can log in yet. Set one with:  .\deploy\New-PasswordHash.ps1 -SiteConfig `"$AppDir\Site.config`" -Restart"
}

# IIS can read it; you can publish to it later without admin rights.
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
icacls $AppDir /grant "IIS_IUSRS:(OI)(CI)RX" /grant "${me}:(OI)(CI)M" /Q | Out-Null
Write-Host "  App copied to $AppDir"

# ---- 3. App pool and site ----
if (-not (& $appcmd list apppool "$AppPool")) {
    Invoke-AppCmd add apppool "/name:$AppPool" /managedRuntimeVersion:v4.0 /managedPipelineMode:Integrated | Out-Null
    Write-Host "  App pool '$AppPool' created"
}
if ($siteExists) {
    Invoke-AppCmd set vdir "$SiteName/" "/physicalPath:$AppDir" | Out-Null
    Invoke-AppCmd set app "$SiteName/" "/applicationPool:$AppPool" | Out-Null
} else {
    Invoke-AppCmd add site "/name:$SiteName" "/bindings:http/*:${Port}:$HostName" "/physicalPath:$AppDir" | Out-Null
    Invoke-AppCmd set app "$SiteName/" "/applicationPool:$AppPool" | Out-Null
    Write-Host "  Site created: http://$(if ($HostName) { $HostName } else { 'localhost' }):$Port/"
}

# ---- 4. /media -> media folder ----
# appcmd "list" exits 0 even when nothing matches, so check its output instead.
if (& $appcmd list vdir "/app.name:$SiteName/" /path:/media) {
    Invoke-AppCmd set vdir "$SiteName/media" "/physicalPath:$MediaRoot" | Out-Null
} else {
    Invoke-AppCmd add vdir "/app.name:$SiteName/" /path:/media "/physicalPath:$MediaRoot" | Out-Null
}
Write-Host "  Site -> $AppDir;  /media -> $MediaRoot;  app pool '$AppPool'"

# ---- 5. Move a Web.config out of the media folder (recorded so Rollback-IIS.ps1 can put it back) ----
$strayConfig = Join-Path $MediaRoot 'Web.config'
if (Test-Path $strayConfig) {
    $saveDir = Join-Path $BackupDir (($SiteName -replace '[^A-Za-z0-9_.-]', '') + "-$(Get-Date -Format yyyyMMdd-HHmmss)")
    New-Item -ItemType Directory -Force $saveDir | Out-Null
    Move-Item $strayConfig "$saveDir\Web.config"
    [pscustomobject]@{ Original = $strayConfig; Saved = "$saveDir\Web.config" } |
        Export-Csv "$BackupDir\moved-files.csv" -Append -NoTypeInformation
    Write-Host "  Web.config in the media folder moved to $saveDir\"
}

# ---- 6. Media folder permissions ----
if ($GrantMediaAccess) {
    $identityType = (& $appcmd list apppool "$AppPool" /text:processModel.identityType)
    if ($identityType -eq 'ApplicationPoolIdentity') {
        Write-Host "  Granting 'IIS AppPool\$AppPool' modify rights on $MediaRoot (may take a while)..."
        icacls $MediaRoot /grant "IIS AppPool\${AppPool}:(OI)(CI)M" /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "icacls reported problems (exit $LASTEXITCODE); some files may not be writable by the site." }
    } else {
        Write-Warning "App pool '$AppPool' runs as $identityType, not its own identity: give that account modify rights on $MediaRoot yourself."
    }
}

# ---- Start / restart (each is a harmless no-op if it's already in that state) ----
foreach ($verb in 'start', 'recycle') { try { & $appcmd $verb apppool "/apppool.name:$AppPool" | Out-Null } catch { } }
try { & $appcmd start site "/site.name:$SiteName" | Out-Null } catch { }

Write-Host "`nDone. '$SiteName' runs Media Manager." -ForegroundColor Green
Write-Host "Undo with: powershell -ExecutionPolicy Bypass -File .\deploy\Rollback-IIS.ps1 -BackupDir `"$BackupDir`""
