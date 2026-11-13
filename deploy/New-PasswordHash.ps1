<#
  Makes a password hash for Site.config (LoginPasswordHash or GuestPasswordHash), and optionally puts it there.

  The hash is PBKDF2-SHA256 with a random salt, in the format Models\PasswordHasher.cs reads:
      pbkdf2-sha256$<iterations>$<salt>$<hash>
  The password itself is never stored or shown.

  Examples:
    .\deploy\New-PasswordHash.ps1                                   # asks for a password, prints the hash
    .\deploy\New-PasswordHash.ps1 -SiteConfig C:\inetpub\MediaManager\Site.config -Restart
    .\deploy\New-PasswordHash.ps1 -SiteConfig C:\inetpub\MediaManager\Site.config -Key GuestPasswordHash -Restart

  -SiteConfig  writes the hash into that file (adding or replacing the key; everything else is kept).
  -Restart     restarts that site afterwards (by touching the Web.config next to it), so the new
               password works straight away. Without it, it applies the next time the site restarts.
#>
param(
    [string]$SiteConfig,
    [ValidateSet('LoginPasswordHash', 'GuestPasswordHash')][string]$Key = 'LoginPasswordHash',
    [ValidateRange(10000, 10000000)][int]$Iterations = 210000,   # keep in step with PasswordHasher.DefaultIterations
    [securestring]$Password,                                    # normally omitted: you're asked for it
    [switch]$Restart
)

$ErrorActionPreference = 'Stop'

function Get-Plain([securestring]$s) { [Net.NetworkCredential]::new('', $s).Password }

if ($SiteConfig -and -not (Test-Path $SiteConfig)) { throw "Site.config not found: $SiteConfig" }

# ---- Ask for the password (twice) ----
if ($Password) {
    $plain = Get-Plain $Password
} else {
    $who = if ($Key -eq 'GuestPasswordHash') { 'guest' } else { 'owner' }
    $first  = Get-Plain (Read-Host "New $who password" -AsSecureString)
    $second = Get-Plain (Read-Host 'Same password again' -AsSecureString)
    if ($first -cne $second) { throw "The two passwords don't match. Nothing was changed." }
    $plain = $first
}
if ([string]::IsNullOrEmpty($plain)) { throw 'The password is empty. Nothing was changed.' }
if ($plain.Length -lt 8) { Write-Warning 'That password is short. Longer is much harder to guess (a few random words works well).' }

# ---- Hash it ----
$salt = New-Object byte[] 16
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($salt); $rng.Dispose()
$kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($plain, $salt, $Iterations, [Security.Cryptography.HashAlgorithmName]::SHA256)
$hash = 'pbkdf2-sha256${0}${1}${2}' -f $Iterations, [Convert]::ToBase64String($salt), [Convert]::ToBase64String($kdf.GetBytes(32))
$kdf.Dispose()
$plain = $null; $first = $null; $second = $null

if (-not $SiteConfig) {
    Write-Host "`nAdd this line to the site's Site.config (inside <appSettings>):`n" -ForegroundColor Green
    Write-Output "  <add key=`"$Key`" value=`"$hash`" />"
    return
}

# ---- Write it into Site.config, keeping comments and layout ----
$path = (Resolve-Path $SiteConfig).Path
$doc = New-Object Xml.XmlDocument
$doc.PreserveWhitespace = $true
$doc.Load($path)
$settings = $doc.SelectSingleNode('/appSettings')
if (-not $settings) { throw "$path doesn't look like a Site.config (no <appSettings> root)." }

$node = $settings.SelectSingleNode("add[@key='$Key']")
if ($node) {
    $node.SetAttribute('value', $hash)
    # Drop an "old hash" note right after it on the same line (Site.config files may have one); it no longer applies.
    $next = $node.NextSibling
    if ($next -and $next.NodeType -eq 'Whitespace' -and $next.Value -notmatch '\n') { $next = $next.NextSibling }
    if ($next -and $next.NodeType -eq 'Comment' -and $next.Value -match 'legacy SHA-256') {
        $before = $next.PreviousSibling
        [void]$settings.RemoveChild($next)
        if ($before -and $before.NodeType -eq 'Whitespace' -and $before.Value -notmatch '\n') { [void]$settings.RemoveChild($before) }
    }
    $action = 'Replaced'
} else {
    $node = $doc.CreateElement('add')
    $node.SetAttribute('key', $Key)
    $node.SetAttribute('value', $hash)
    # Before the closing tag, indented like the rest
    $last = $settings.LastChild
    if ($last -and $last.NodeType -eq 'Whitespace') {
        [void]$settings.InsertBefore($doc.CreateWhitespace("`r`n  "), $last)
        [void]$settings.InsertBefore($node, $last)
    } else {
        [void]$settings.AppendChild($doc.CreateWhitespace("`r`n  "))
        [void]$settings.AppendChild($node)
        [void]$settings.AppendChild($doc.CreateWhitespace("`r`n"))
    }
    $action = 'Added'
}
$doc.Save($path)
Write-Host "$action $Key in $path" -ForegroundColor Green

# ---- Restart the site so it takes effect now ----
$webConfig = Join-Path (Split-Path $path -Parent) 'Web.config'
if ($Restart) {
    if (Test-Path $webConfig) {
        (Get-Item $webConfig).LastWriteTime = Get-Date
        Write-Host 'Site restarting (Web.config touched). Everyone will need to log in again.'
    } else {
        Write-Warning "No Web.config next to $path, so nothing was restarted."
    }
} else {
    Write-Host 'Takes effect when the site next restarts (publish, app pool recycle, or run again with -Restart).'
}
