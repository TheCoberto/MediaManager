# Dumps an MP4 / MOV file's box tree with the lengths stored in mvhd / tkhd / mdhd / mehd / elst. Read-only.
# Usage: .\boxes.ps1 <file>   (top-level boxes repeated more than 3 times are summarised as a count)
param([string]$Path)
$fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
function U32($b, $o) { ([uint32]$b[$o] -shl 24) -bor ([uint32]$b[$o+1] -shl 16) -bor ([uint32]$b[$o+2] -shl 8) -bor [uint32]$b[$o+3] }
function U64($b, $o) { ([uint64](U32 $b $o) -shl 32) -bor [uint64](U32 $b ($o+4)) }
function Read-At($pos, $n) { $fs.Position = $pos; $b = New-Object byte[] $n; [void]$fs.Read($b, 0, $n); $b }
$containers = 'moov','trak','mdia','minf','stbl','edts','mvex','udta'
function Walk($start, $end, $depth) {
  $pos = $start; $count = @{}
  while ($pos + 8 -le $end) {
    $h = Read-At $pos 16
    $size = [int64](U32 $h 0); $type = [Text.Encoding]::ASCII.GetString($h, 4, 4); $hdr = 8
    if ($size -eq 1) { $size = [int64](U64 $h 8); $hdr = 16 } elseif ($size -eq 0) { $size = $end - $pos }
    if ($size -lt 8) { "$('  '*$depth)BAD size at $pos"; return }
    $count[$type]++
    if ($depth -eq 0 -and $count[$type] -gt 3) { $pos += $size; continue }
    $extra = ''
    if ($type -in 'mvhd','tkhd','mdhd','mehd') {
      $b = Read-At ($pos + $hdr) 40; $ver = $b[0]
      switch ($type) {
        'mvhd' { if ($ver -eq 1) { $ts = U32 $b 20; $d = U64 $b 24 } else { $ts = U32 $b 12; $d = U32 $b 16 }; $extra = "v$ver timescale=$ts duration=$d ($([math]::Round($d/$ts,2))s)" }
        'mdhd' { if ($ver -eq 1) { $ts = U32 $b 20; $d = U64 $b 24 } else { $ts = U32 $b 12; $d = U32 $b 16 }; $extra = "v$ver timescale=$ts duration=$d ($([math]::Round($d/$ts,2))s)" }
        'tkhd' { if ($ver -eq 1) { $d = U64 $b 28 } else { $d = U32 $b 20 }; $extra = "v$ver duration=$d (movie timescale)" }
        'mehd' { if ($ver -eq 1) { $d = U64 $b 4 } else { $d = U32 $b 4 }; $extra = "fragment_duration=$d" }
      }
    }
    if ($type -eq 'elst') {
      $b = Read-At ($pos + $hdr) 64; $ver = $b[0]; $n = U32 $b 4
      $extra = "v$ver entries=$n "
      if ($ver -eq 1) { $extra += "segdur=$(U64 $b 8) mediatime=$(U64 $b 16)" } else { $extra += "segdur=$(U32 $b 8) mediatime=$(U32 $b 12)" }
    }
    "$('  '*$depth)$type size=$size @ $pos $extra"
    if ($type -in $containers) { Walk ($pos + $hdr) ($pos + $size) ($depth + 1) }
    $pos += $size
  }
  if ($depth -eq 0) { $count.GetEnumerator() | Where-Object { $_.Value -gt 3 } | ForEach-Object { "($($_.Key) x $($_.Value) total)" } }
}
Walk 0 $fs.Length 0
$fs.Close()
