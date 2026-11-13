<#
  One-time setup so debugging from Visual Studio (IIS Express) sees the same files as the live site.

  The app finds all media through the "/media" virtual directory (Models/MediaPaths.cs). The IIS sites
  have it (Setup-IIS.ps1); Visual Studio's IIS Express doesn't, so without this a debug session shows no files.
  This adds "/media" -> -MediaRoot to the project's IIS Express config (.vs\...\applicationhost.config,
  which is local and not in git). Run it again if Visual Studio ever recreates that file.

  Debugging uses the database in the repo's ConnectionStrings.config (normally the live MediaManager one),
  so pointing at the live media folder keeps files and database records (favorites, flags, Recycle Bin) in step.

  Usage:  .\deploy\Setup-Debug.ps1 -MediaRoot D:\Media       (the live site's media folder)
          .\deploy\Setup-Debug.ps1 -MediaRoot D:\TestMedia   (a separate test folder)
#>
param([Parameter(Mandatory)][string]$MediaRoot)
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$config = Get-ChildItem "$repo\.vs" -Recurse -Filter applicationhost.config -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $config) { throw "No .vs\...\applicationhost.config yet: open the project in Visual Studio and start debugging once first." }
$appcmd = "$env:ProgramFiles\IIS Express\appcmd.exe"
if (-not (Test-Path $appcmd)) { throw "IIS Express isn't installed ($appcmd)." }
if (-not (Test-Path $MediaRoot)) { throw "Media folder not found: $MediaRoot" }

# The IIS Express site whose root is this repo
$xml = [xml](Get-Content $config.FullName)
$site = $xml.configuration.'system.applicationHost'.sites.site | Where-Object {
    $_.application | Where-Object { $_.path -eq '/' -and ($_.virtualDirectory | Where-Object { $_.path -eq '/' -and $_.physicalPath.TrimEnd('\') -eq $repo.TrimEnd('\') }) }
} | Select-Object -First 1
if (-not $site) { throw "Couldn't find the IIS Express site for $repo in $($config.FullName)." }

$app = "$($site.name)/"
& $appcmd delete vdir /vdir.name:"$($site.name)/media" /apphostconfig:"$($config.FullName)" 2>$null | Out-Null
& $appcmd add vdir /app.name:"$app" /path:/media /physicalPath:"$MediaRoot" /apphostconfig:"$($config.FullName)"
if ($LASTEXITCODE -ne 0) { throw "appcmd failed." }
Write-Host "Debug /media now points at $MediaRoot (site '$($site.name)')." -ForegroundColor Green
