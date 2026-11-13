<#
  Tiny mDNS responder: answers ".local" name lookups (e.g. media.local) on the home network with this
  PC's current LAN IPv4 address, so phones/laptops can reach the IIS sites by name.

  Installed as a startup task by Setup-LocalNames.ps1, which passes the names. Picks up IP changes automatically.
#>
param(
    # Names to answer for. "a.local,b.local" also works (that's how the startup task passes them).
    [string[]]$Names = @('media.local'),
    [string]$LogFile = (Join-Path $PSScriptRoot 'LocalNames.log')
)

$ErrorActionPreference = 'Stop'
$MdnsGroup = [Net.IPAddress]::Parse('224.0.0.251')
$MdnsPort  = 5353
$Ttl       = 120
$Names     = $Names -split ',' | ForEach-Object { $_.Trim().TrimEnd('.').ToLowerInvariant() } | Where-Object { $_ }

function Write-Log([string]$msg) {
    try {
        if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 1MB) { Remove-Item $LogFile -Force }
        Add-Content $LogFile "$(Get-Date -Format s)  $msg"
    } catch { }
}

# IPv4 address of the adapter that has the default gateway (the LAN connection).
function Get-LanIP {
    $cfg = Get-NetIPConfiguration -ErrorAction SilentlyContinue |
        Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
        Select-Object -First 1
    if ($cfg) { [Net.IPAddress]::Parse(@($cfg.IPv4Address)[0].IPAddress) }
}

function Write-UInt16([IO.MemoryStream]$ms, [int]$v) {
    $ms.WriteByte(($v -shr 8) -band 0xFF); $ms.WriteByte($v -band 0xFF)
}

function Write-Name([IO.MemoryStream]$ms, [string]$name) {
    foreach ($label in $name.Split('.')) {
        $b = [Text.Encoding]::UTF8.GetBytes($label)
        $ms.WriteByte($b.Length); $ms.Write($b, 0, $b.Length)
    }
    $ms.WriteByte(0)
}

# NOTE: bytes are cast to [int] before -shl; PowerShell keeps [byte] -shl 8 as a byte (= 0).
# Reads a (possibly compressed) DNS name starting at $pos; advances $pos past it.
function Read-Name([byte[]]$buf, [ref]$pos) {
    $labels = New-Object System.Collections.Generic.List[string]
    $p = $pos.Value; $jumped = $false; $guard = 0
    while ($true) {
        if ($p -ge $buf.Length -or $guard++ -gt 64) { throw 'malformed name' }
        $len = [int]$buf[$p]
        if ($len -eq 0) { $p++; break }
        if (($len -band 0xC0) -eq 0xC0) {
            if (-not $jumped) { $pos.Value = $p + 2 }
            $jumped = $true
            $p = (($len -band 0x3F) -shl 8) -bor [int]$buf[$p + 1]
            continue
        }
        $labels.Add([Text.Encoding]::UTF8.GetString($buf, $p + 1, $len))
        $p += 1 + $len
    }
    if (-not $jumped) { $pos.Value = $p }
    return ($labels -join '.')
}

# Builds an A-record answer. "Legacy" = a plain DNS client (not port 5353): echo the ID and the question.
function New-Answer([int]$id, [string]$name, [Net.IPAddress]$ip, [bool]$legacy) {
    $ms = New-Object IO.MemoryStream
    Write-UInt16 $ms $id
    Write-UInt16 $ms 0x8400                         # response, authoritative
    Write-UInt16 $ms ([int]$legacy)                 # questions
    Write-UInt16 $ms 1                              # answers
    Write-UInt16 $ms 0; Write-UInt16 $ms 0
    if ($legacy) { Write-Name $ms $name; Write-UInt16 $ms 1; Write-UInt16 $ms 1 }
    Write-Name $ms $name
    Write-UInt16 $ms 1                              # type A
    Write-UInt16 $ms $(if ($legacy) { 1 } else { 0x8001 })  # class IN (+ cache-flush for mDNS)
    Write-UInt16 $ms 0; Write-UInt16 $ms $Ttl       # TTL
    Write-UInt16 $ms 4
    $ipBytes = $ip.GetAddressBytes(); $ms.Write($ipBytes, 0, 4)
    return , $ms.ToArray()
}

Write-Log "Starting; names: $($Names -join ', ')"

while ($true) {
    $udp = $null
    try {
        $ip = Get-LanIP
        if (-not $ip) { Start-Sleep -Seconds 10; continue }

        $udp = New-Object Net.Sockets.UdpClient
        $udp.ExclusiveAddressUse = $false
        $udp.Client.SetSocketOption([Net.Sockets.SocketOptionLevel]::Socket, [Net.Sockets.SocketOptionName]::ReuseAddress, $true)
        $udp.Client.Bind((New-Object Net.IPEndPoint([Net.IPAddress]::Any, $MdnsPort)))
        $udp.JoinMulticastGroup($MdnsGroup, $ip)
        $udp.Client.SetSocketOption([Net.Sockets.SocketOptionLevel]::IP, [Net.Sockets.SocketOptionName]::MulticastInterface, $ip.GetAddressBytes())
        $udp.Client.SetSocketOption([Net.Sockets.SocketOptionLevel]::IP, [Net.Sockets.SocketOptionName]::MulticastTimeToLive, 255)
        $udp.MulticastLoopback = $true
        $udp.Client.ReceiveTimeout = 5000
        $groupEP = New-Object Net.IPEndPoint($MdnsGroup, $MdnsPort)

        Write-Log "Answering with $ip"
        foreach ($n in $Names) { $a = New-Answer 0 $n $ip $false; [void]$udp.Send($a, $a.Length, $groupEP) }  # announce

        $lastIpCheck = Get-Date
        while ($true) {
            if (((Get-Date) - $lastIpCheck).TotalSeconds -ge 60) {
                $lastIpCheck = Get-Date
                $now = Get-LanIP
                if ($now -and -not $now.Equals($ip)) { Write-Log "IP changed $ip -> $now"; break }
            }

            $remote = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
            try { $buf = $udp.Receive([ref]$remote) } catch { continue }   # timeout -> loop (re-checks IP)

            try {
                if ($buf.Length -lt 12) { continue }
                $flags = ([int]$buf[2] -shl 8) -bor [int]$buf[3]
                if ($flags -band 0x8000) { continue }                       # ignore responses
                $id = ([int]$buf[0] -shl 8) -bor [int]$buf[1]
                $qdCount = ([int]$buf[4] -shl 8) -bor [int]$buf[5]
                $pos = 12
                for ($i = 0; $i -lt $qdCount; $i++) {
                    $qname = Read-Name $buf ([ref]$pos)
                    $qtype = ([int]$buf[$pos] -shl 8) -bor [int]$buf[$pos + 1]
                    $qclass = ([int]$buf[$pos + 2] -shl 8) -bor [int]$buf[$pos + 3]
                    $pos += 4
                    if ($Names -notcontains $qname.ToLowerInvariant() -or ($qtype -ne 1 -and $qtype -ne 255)) { continue }

                    $legacy = $remote.Port -ne $MdnsPort
                    $answer = New-Answer $(if ($legacy) { $id } else { 0 }) $qname.ToLowerInvariant() $ip $legacy
                    if ($legacy -or ($qclass -band 0x8000)) { [void]$udp.Send($answer, $answer.Length, $remote) }  # unicast reply
                    if (-not $legacy) { [void]$udp.Send($answer, $answer.Length, $groupEP) }                      # multicast reply
                }
            } catch { }  # ignore malformed packets
        }
    } catch {
        Write-Log "Error: $($_.Exception.Message)"
        Start-Sleep -Seconds 5
    } finally {
        if ($udp) { $udp.Close() }
    }
}
