<#
  Builds the app (Release) and publishes it. No admin needed once Setup-IIS.ps1 has given you write access
  to the site folders. Each site's Site.config / ConnectionStrings.config are never touched (they aren't
  part of the published files).

  Usage:
    .\deploy\Publish-All.ps1
        Publishes to every folder ("File System") publish profile in Properties\PublishProfiles\*.pubxml,
        i.e. every site you've set up a profile for. Other kinds of profile (e.g. Azure) are skipped.

    .\deploy\Publish-All.ps1 -Profile MediaManager
        Just that profile (the .pubxml file name without the extension).

    .\deploy\Publish-All.ps1 -To "$env:LOCALAPPDATA\MediaManagerStaging\app"
        Publishes to a folder without needing a profile, e.g. as the -AppSource for Setup-IIS.ps1.
#>
param(
    [string[]]$Profile,
    [string]$To
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'Visual Studio (or its Build Tools) with the web development workload is needed to build.' }
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found. Install Visual Studio (or Build Tools) with the "ASP.NET and web development" workload.' }

$common = @("$repo\MediaManager.csproj", '/p:Configuration=Release', '/p:DeployOnBuild=true', '/v:minimal', '/nologo')

if ($To) {
    $To = [IO.Path]::GetFullPath($To)
    Write-Host "Publishing to $To..." -ForegroundColor Cyan
    & $msbuild @common /p:DeployDefaultTarget=WebPublish /p:WebPublishMethod=FileSystem "/p:PublishUrl=$To" /p:DeleteExistingFiles=false
    if ($LASTEXITCODE -ne 0) { throw "Publish to $To failed." }
    Write-Host "Published to $To" -ForegroundColor Green
    return
}

$profileDir = Join-Path $repo 'Properties\PublishProfiles'
$profiles = if ($Profile) {
    $Profile
} else {
    Get-ChildItem $profileDir -Filter *.pubxml -ErrorAction SilentlyContinue |
        Where-Object { ([xml](Get-Content $_.FullName)).Project.PropertyGroup.WebPublishMethod -contains 'FileSystem' } |
        ForEach-Object { $_.BaseName }
}
if (-not $profiles) { throw "No folder publish profiles in $profileDir. Use -To <folder>, or create a profile (see the README)." }

foreach ($p in $profiles) {
    Write-Host "Publishing $p..." -ForegroundColor Cyan
    & $msbuild @common "/p:PublishProfile=$p"
    if ($LASTEXITCODE -ne 0) { throw "Publish to $p failed." }
}
Write-Host "Published: $($profiles -join ', ')" -ForegroundColor Green
