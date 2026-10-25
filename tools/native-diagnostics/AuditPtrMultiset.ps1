param(
    [Parameter(Mandatory=$true)][string]$DumpPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$ArrayAddressHex,
    [Parameter(Mandatory=$true)][int]$ElementCount,
    [Parameter(Mandatory=$true)][string]$DeadObjectAddressHex
)
$ErrorActionPreference = 'Stop'
# Offline only: the helper reads the original dump without modifying it.
if ($ElementCount -lt 1 -or $ElementCount -gt 100000) { throw 'Invalid bounded count.' }
$taskRoot = (Get-Location).Path
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskOutput.StartsWith($taskRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Output must remain inside project.' }
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$arrayAddress = [Convert]::ToUInt64($ArrayAddressHex.Replace('0x',''),16)
$deadAddress = [Convert]::ToUInt64($DeadObjectAddressHex.Replace('0x',''),16)
$rawOutput = Join-Path $taskOutput 'ptrmultiset-raw.txt'
& (Join-Path $taskRoot '.runtime/native-diagnostics/ReadDumpMemory.exe') $DumpPath $rawOutput $ArrayAddressHex (($ElementCount+3)*8)
if ($LASTEXITCODE -ne 0) { throw 'Dump reader failed.' }
$bytes = New-Object 'Collections.Generic.List[byte]'
foreach ($line in [IO.File]::ReadAllLines($rawOutput)) {
    if ($line -match '^[0-9A-F]{16}  ([0-9A-F ]{2,47})  ') {
        foreach ($value in $Matches[1].Trim().Split(' ',[StringSplitOptions]::RemoveEmptyEntries)) { $bytes.Add([Convert]::ToByte($value,16)) }
    }
}
if ($bytes.Count -ne ($ElementCount+3)*8) { throw ('Expected full array bytes, observed '+$bytes.Count) }
$buffer = $bytes.ToArray()
$count = [BitConverter]::ToUInt64($buffer,0)
$capacity = [BitConverter]::ToUInt64($buffer,8)
$metadata = [BitConverter]::ToUInt64($buffer,16)
$sortedFlag = ($metadata -band 0x80000000L) -ne 0
$removedHeader = $metadata -band 0x7fffffffL
$rows = @(); $violations = @(); $taggedCount = 0; $deadLive = 0; $deadRemoved = 0
for ($index=0; $index -lt $ElementCount; $index++) {
    $value = [BitConverter]::ToUInt64($buffer,24+8*$index)
    $tags = $value -band 3
    $base = $value - $tags
    $removed = $tags -eq 3
    if ($removed) { $taggedCount++ }
    if ($base -eq $deadAddress) { if ($removed) { $deadRemoved++ } else { $deadLive++ } }
    if ($index -gt 0 -and $value -lt $previous) {
        $violations += [ordered]@{PreviousIndex=$index-1; Previous=('0x'+$previous.ToString('X16')); Index=$index; Value=('0x'+$value.ToString('X16'))}
    }
    $rows += [pscustomobject]@{Index=$index; Address=('0x'+($arrayAddress+24+8*$index).ToString('X16')); Value=('0x'+$value.ToString('X16')); Base=('0x'+$base.ToString('X16')); Tag=$tags; Removed=$removed; IsDeadObject=($base -eq $deadAddress)}
    $previous=$value
}
$rows | Export-Csv -LiteralPath (Join-Path $taskOutput 'ptrmultiset-elements.csv') -NoTypeInformation -Encoding UTF8
$summary=[ordered]@{
    Kind='read-only-actual-dump-CPtrMultiset-audit'; Dump=[IO.Path]::GetFullPath($DumpPath); ArrayAddress=('0x'+$arrayAddress.ToString('X16'));
    HeaderCount=$count; RequestedCount=$ElementCount; Capacity=$capacity; Metadata=('0x'+$metadata.ToString('X16')); SortedFlag=$sortedFlag;
    HeaderRemoved=$removedHeader; ActualTaggedRemoved=$taggedCount; RemovedCountMatches=($removedHeader -eq $taggedCount);
    ActualSorted=($violations.Count -eq 0); SortedViolationCount=$violations.Count; SortedViolations=$violations;
    DeadObject=('0x'+$deadAddress.ToString('X16')); DeadObjectLiveEntries=$deadLive; DeadObjectRemovedEntries=$deadRemoved;
    Meaning='Duplicates are legal. Sortedness compares complete tagged pointer values as the implementation comparator does. Tagged low bits 3 indicate removed; this record does not assign an original type from padding.'
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'ptrmultiset-audit.json') -Encoding UTF8
$summary | ConvertTo-Json -Depth 5
