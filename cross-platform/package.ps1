[CmdletBinding()]
param(
    [ValidateSet('osx-arm64', 'linux-x64', 'linux-arm64', 'win-x64')]
    [string]$Runtime = 'osx-arm64',
    [string]$PublishDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$crossRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$releaseVersion = (Get-Content -LiteralPath (Join-Path $crossRoot '../VERSION') -Raw).Trim()
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The repository VERSION must declare a three-part release version.' }
if ($Version -and $Version -ne $releaseVersion) { throw ('Package Version must match the repository VERSION (' + $releaseVersion + '). Rebuild instead of relabeling another release.') }
$Version = $releaseVersion
$projectRoot = [IO.Path]::GetFullPath((Join-Path $crossRoot '..'))
$pathComparison = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$executableMagics = @('464C457F', 'FEEDFACE', 'CEFAEDFE', 'FEEDFACF', 'CFFAEDFE', 'CAFEBABE', 'BEBAFECA', 'CAFEBABF', 'BFBAFECA') | ForEach-Object { [Convert]::ToUInt32($_, 16) }
$runtimeId = $Runtime.ToLowerInvariant()
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $crossRoot ('out/' + $runtimeId) }
if (-not [IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory = Join-Path $crossRoot $PublishDirectory }
$publish = [IO.Path]::GetFullPath($PublishDirectory)
$releaseRoot = Join-Path $projectRoot 'release'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$evidence = Join-Path $projectRoot ('artifacts/cross-platform/packaging/' + $runtimeId + '-' + $stamp)
$stage = Join-Path $evidence 'stage'
$archiveName = $(if ($runtimeId -eq 'win-x64') { 'SideIsland-Preview-v' } else { 'SideIsland-v' }) + $Version + '-' + $runtimeId + '.zip'
$archivePath = Join-Path $releaseRoot $archiveName
$temporaryArchive = Join-Path $releaseRoot ('.' + $runtimeId + '-' + [Guid]::NewGuid().ToString('N') + '.zip')

function Checked-Path([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, $pathComparison)) { throw ('Packaging paths must remain inside this project: ' + $full) }
    for ($cursor = $full; $cursor -and $cursor.Length -gt $projectRoot.Length; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Packaging does not follow links or reparse points: ' + $cursor) }
    }
    return $full
}

function Assert-ReleaseAvailable {
    foreach ($target in @($archivePath, ($archivePath + '.sha256'), ($archivePath + '.manifest.json'))) {
        $null = Checked-Path $target
        if (Test-Path -LiteralPath $target) { throw ('Release already exists; review it before explicitly replacing it: ' + $target) }
    }
}

function Tree-Files([string]$Directory) {
    $files = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push((Checked-Path $Directory))
    while ($pending.Count -gt 0) {
        foreach ($child in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $null = Checked-Path $child.FullName
            if ($child.PSIsContainer) { $pending.Push($child.FullName) } else { $files.Add($child) }
        }
    }
    return @($files | Sort-Object FullName)
}

function File-Header([string]$Path, [int]$Count = 64) {
    $stream = [IO.File]::OpenRead($Path)
    try { $bytes = New-Object byte[] $Count; $read = $stream.Read($bytes, 0, $bytes.Length); $header = New-Object byte[] $read; [Array]::Copy($bytes, $header, $read); return ,$header }
    finally { $stream.Dispose() }
}

function Native-Apphost-Check([string]$Path) {
    $head = File-Header $Path
    if ($head.Length -lt 24) { throw 'Native apphost is truncated.' }
    if ($runtimeId -eq 'osx-arm64') {
        if ([BitConverter]::ToUInt32($head, 0) -ne [Convert]::ToUInt32('FEEDFACF', 16) -or [BitConverter]::ToUInt32($head, 4) -ne 0x0100000C) { throw 'Expected a thin 64-bit Apple Silicon Mach-O apphost.' }
    }
    elseif ($runtimeId.StartsWith('linux-')) {
        if ($head[0] -ne 0x7F -or $head[1] -ne 0x45 -or $head[2] -ne 0x4C -or $head[3] -ne 0x46 -or $head[4] -ne 2 -or $head[5] -ne 1) { throw 'Expected a little-endian 64-bit ELF apphost.' }
        $machine = [BitConverter]::ToUInt16($head, 18)
        if (($runtimeId -eq 'linux-x64' -and $machine -ne 62) -or ($runtimeId -eq 'linux-arm64' -and $machine -ne 183)) { throw 'ELF architecture differs from the requested runtime.' }
    }
    else {
        if ($head.Length -lt 64 -or $head[0] -ne 0x4D -or $head[1] -ne 0x5A) { throw 'Expected a Windows PE apphost.' }
        $stream = [IO.File]::OpenRead($Path)
        $reader = New-Object IO.BinaryReader($stream)
        try { $stream.Position = [BitConverter]::ToUInt32($head, 60); if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Expected an AMD64 PE apphost.' } }
        finally { $reader.Dispose(); $stream.Dispose() }
    }
}

function Is-Executable([IO.FileInfo]$File) {
    if ($File.Name -in @('SideIsland.Desktop', 'SideIsland.Desktop.exe', 'side-island')) { return $true }
    if ($File.Length -lt 4) { return $false }
    $head = File-Header $File.FullName 4
    $magic = [BitConverter]::ToUInt32($head, 0)
    # All ELF/Mach-O native libraries receive execute bits too; managed PE DLLs remain 0644.
    return $magic -in $executableMagics
}

function External-Attributes([int]$Mode, [bool]$Directory) {
    $bits = [uint32]($Mode * 65536)
    if ($Directory) { $bits = $bits -bor 0x10 }
    return [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]$bits), 0)
}

function Stream-Hash([IO.Stream]$Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Set-Unix-Creator([string]$Path, [int]$ExpectedEntries) {
    # ExternalAttributes alone is insufficient on Windows: many unzip tools only
    # honor POSIX mode bits when central-directory "version made by" identifies Unix.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        $tailLength = [int][Math]::Min(65557, $stream.Length)
        $stream.Position = $stream.Length - $tailLength; $tail = $reader.ReadBytes($tailLength); $eocd = -1
        for ($i = $tail.Length - 22; $i -ge 0; $i--) {
            if ([BitConverter]::ToUInt32($tail, $i) -eq 0x06054B50 -and $i + 22 + [BitConverter]::ToUInt16($tail, $i + 20) -eq $tail.Length) { $eocd = $i; break }
        }
        if ($eocd -lt 0) { throw 'ZIP end-of-directory record not found.' }
        $entries = [BitConverter]::ToUInt16($tail, $eocd + 10)
        $directorySize = [BitConverter]::ToUInt32($tail, $eocd + 12)
        $directoryOffset = [BitConverter]::ToUInt32($tail, $eocd + 16)
        if ($entries -eq 65535 -or $directoryOffset -eq [uint32]::MaxValue -or $directorySize -eq [uint32]::MaxValue) { throw 'This portable preview packager requires a non-ZIP64 archive.' }
        if ($entries -ne $ExpectedEntries -or [long]$directoryOffset + $directorySize -gt $stream.Length - $tailLength + $eocd) { throw 'Unexpected ZIP directory bounds or entry count.' }
        $cursor = [long]$directoryOffset
        for ($index = 0; $index -lt $entries; $index++) {
            $stream.Position = $cursor
            if ($reader.ReadUInt32() -ne 0x02014B50) { throw 'Unexpected ZIP central-directory signature.' }
            $stream.Position = $cursor + 28; $nameLength = $reader.ReadUInt16(); $extraLength = $reader.ReadUInt16(); $commentLength = $reader.ReadUInt16()
            $stream.Position = $cursor + 5; $stream.WriteByte(3)
            $cursor += 46 + $nameLength + $extraLength + $commentLength
        }
        if ($cursor -ne [long]$directoryOffset + $directorySize) { throw 'ZIP directory size differs from the parsed entry records.' }
        $stream.Flush()
        $creator = New-Object 'System.Collections.Generic.List[object]'
        $cursor = [long]$directoryOffset
        for ($index = 0; $index -lt $entries; $index++) {
            $stream.Position = $cursor + 5; if ($reader.ReadByte() -ne 3) { throw 'ZIP creator OS is not Unix after patching.' }
            $stream.Position = $cursor + 28; $nameLength = $reader.ReadUInt16(); $extraLength = $reader.ReadUInt16(); $commentLength = $reader.ReadUInt16()
            $stream.Position = $cursor + 46; $name = [Text.Encoding]::UTF8.GetString($reader.ReadBytes($nameLength))
            $creator.Add([pscustomobject]@{ Path = $name; CreatorOS = 3 })
            $cursor += 46 + $nameLength + $extraLength + $commentLength
        }
        return $creator.ToArray()
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

$null = Checked-Path $publish
$null = Checked-Path $archivePath
$null = Checked-Path $temporaryArchive
$null = Checked-Path $evidence
# Check all three outputs before creating staging/evidence or reading a publish
# payload. The Windows preview must never collide with the separate WPF release.
Assert-ReleaseAvailable
if (-not (Test-Path -LiteralPath $publish -PathType Container)) { throw ('Publish directory not found: ' + $publish) }
$apphost = Join-Path $publish $(if ($runtimeId -eq 'win-x64') { 'SideIsland.Desktop.exe' } else { 'SideIsland.Desktop' })
if (-not (Test-Path -LiteralPath $apphost -PathType Leaf)) { throw 'Publish a self-contained desktop app before packaging.' }
Native-Apphost-Check $apphost
$publishMarker = Join-Path $publish '.side-island-publish.json'
if (-not (Test-Path -LiteralPath $publishMarker -PathType Leaf)) { throw 'Publish with build.ps1 first; packaging requires its completed release marker.' }
$published = Get-Content -LiteralPath $publishMarker -Raw | ConvertFrom-Json
if ($published.Kind -ne 'side-island-generated-publish' -or $published.Runtime -ne $runtimeId -or -not $published.Complete -or $published.Version -ne $Version) { throw 'The publish marker does not match this completed runtime/version. Rebuild before packaging.' }
if ($published.ExecutableSHA256 -ne (Get-FileHash -LiteralPath $apphost -Algorithm SHA256).Hash) { throw 'The native apphost changed after publication; rebuild before packaging.' }
$managedAssembly = Join-Path $publish 'SideIsland.Desktop.dll'
if (-not (Test-Path -LiteralPath $managedAssembly -PathType Leaf) -or [Reflection.AssemblyName]::GetAssemblyName($managedAssembly).Version.ToString(3) -ne $Version) { throw 'The desktop assembly version does not match this release. Rebuild before packaging.' }
New-Item -ItemType Directory -Path $evidence, $stage, $releaseRoot -Force | Out-Null
$result = [ordered]@{ Runtime = $runtimeId; Version = $Version; PlatformStatus = 'preview'; Passed = $false; TargetRuntimeExecuted = $false; AppleDeveloperIdSigned = $false; AppleNotarized = $false; StartedUtc = [DateTime]::UtcNow.ToString('o') }
try {
    $sourceFiles = @(Tree-Files $publish)
    $sourceManifest = @($sourceFiles | ForEach-Object { [pscustomobject]@{ RelativePath = $_.FullName.Substring($publish.Length + 1).Replace('\', '/'); Length = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    if ($runtimeId -eq 'osx-arm64') {
        $bundle = Join-Path $stage 'Side Island.app'; $destination = Join-Path $bundle 'Contents/MacOS'
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $plist = New-Object Xml.XmlDocument; $plist.XmlResolver = $null; $plist.Load((Join-Path $crossRoot 'packaging/Info.plist'))
        foreach ($key in @('CFBundleShortVersionString', 'CFBundleVersion', 'SideIslandPreviewVersion')) {
            $node = $plist.SelectSingleNode("/plist/dict/key[text()='$key']/following-sibling::*[1]")
            if (-not $node) { throw ('Missing bundle metadata: ' + $key) }
            $node.InnerText = $(if ($key -eq 'SideIslandPreviewVersion') { $Version } else { $Version.Split('-')[0] })
        }
        $plist.Save((Join-Path $bundle 'Contents/Info.plist'))
    }
    else {
        $bundle = Join-Path $stage 'SideIsland'; $destination = $bundle
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
    }
    foreach ($source in $sourceFiles) {
        $relative = $source.FullName.Substring($publish.Length + 1)
        $target = Checked-Path (Join-Path $destination $relative)
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $source.FullName -Destination $target
    }
    if ($runtimeId.StartsWith('linux-')) {
        $launcher = "#!/bin/sh`nset -eu`napp_path=`$(readlink -f -- `"`$0`")`nexec `"`$(dirname -- `"`$app_path`")/SideIsland.Desktop`" `"`$@`"`n"
        [IO.File]::WriteAllText((Join-Path $bundle 'side-island'), $launcher, (New-Object Text.UTF8Encoding($false)))
        Copy-Item -LiteralPath (Join-Path $crossRoot 'packaging/side-island.desktop') -Destination (Join-Path $bundle 'side-island.desktop')
    }
    foreach ($readme in @('README.md', 'README.zh-CN.md')) {
        if (Test-Path -LiteralPath (Join-Path $crossRoot $readme)) { Copy-Item -LiteralPath (Join-Path $crossRoot $readme) -Destination (Join-Path $stage $readme) }
    }
    $documentation = Join-Path $crossRoot 'docs'
    if (Test-Path -LiteralPath $documentation -PathType Container) {
        foreach ($document in Tree-Files $documentation) {
            $target = Checked-Path (Join-Path (Join-Path $stage 'docs') $document.FullName.Substring($documentation.Length + 1))
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
            Copy-Item -LiteralPath $document.FullName -Destination $target
        }
    }
    # Verify the complete publish payload survived copying, rather than trusting Copy-Item.
    foreach ($file in $sourceManifest) {
        $copy = Join-Path $destination $file.RelativePath
        if (-not (Test-Path -LiteralPath $copy) -or (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $file.SHA256) { throw ('Publish payload copy mismatch: ' + $file.RelativePath) }
        $original = Join-Path $publish $file.RelativePath
        if ((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $file.SHA256) { throw ('Publish payload changed while copying: ' + $file.RelativePath) }
    }

    $payload = @(Tree-Files $stage)
    $entries = New-Object 'System.Collections.Generic.List[object]'
    $directories = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)
    foreach ($file in $payload) {
        for ($parent = $file.DirectoryName; $parent.Length -gt $stage.Length; $parent = [IO.Path]::GetDirectoryName($parent)) { $null = $directories.Add($parent.Substring($stage.Length + 1).Replace('\', '/') + '/') }
    }
    $zip = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $directories) {
            $entry = $zip.CreateEntry($name); $entry.ExternalAttributes = External-Attributes 0x41ED $true
            $entries.Add([pscustomobject]@{ Path = $name; Directory = $true; Length = 0; UnixMode = 0x41ED; Permissions = '0755'; SHA256 = $null })
        }
        foreach ($file in $payload) {
            $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            $executable = Is-Executable $file; $mode = $(if ($executable) { 0x81ED } else { 0x81A4 })
            $entry = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.ExternalAttributes = External-Attributes $mode $false
            $entries.Add([pscustomobject]@{ Path = $name; Directory = $false; Length = $file.Length; UnixMode = $mode; Permissions = $(if ($executable) { '0755' } else { '0644' }); SHA256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash })
        }
    }
    finally { $zip.Dispose() }
    $creators = @(Set-Unix-Creator $temporaryArchive $entries.Count)
    if ($creators.Count -ne $entries.Count) { throw 'ZIP creator verification did not cover every entry.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($temporaryArchive)
    try {
        if ($zip.Entries.Count -ne $entries.Count) { throw 'ZIP entry count differs from the payload manifest.' }
        foreach ($expected in $entries) {
            $matches = @($zip.Entries | Where-Object FullName -eq $expected.Path)
            if ($matches.Count -ne 1 -or $matches[0].Length -ne $expected.Length) { throw ('ZIP entry mismatch: ' + $expected.Path) }
            $bits = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$matches[0].ExternalAttributes), 0)
            if (($bits -shr 16) -ne $expected.UnixMode) { throw ('ZIP POSIX mode mismatch: ' + $expected.Path) }
            if (-not $expected.Directory) {
                $input = $matches[0].Open()
                try { if ((Stream-Hash $input) -ne $expected.SHA256) { throw ('Decompressed ZIP hash differs: ' + $expected.Path) } }
                finally { $input.Dispose() }
            }
        }
    }
    finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash
    $manifest = [ordered]@{ Version = $Version; Runtime = $runtimeId; PlatformStatus = 'preview'; Archive = $archiveName; SHA256 = $hash; Verified = $true; UnixCreatorOS = 3; Files = $entries.ToArray(); PublishPayload = $sourceManifest; TargetRuntimeExecuted = $false; AppleDeveloperIdSigned = $false; AppleNotarized = $false; VerifiedUtc = [DateTime]::UtcNow.ToString('o') }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'package-manifest.json') -Encoding UTF8
    $pendingManifest = Join-Path $evidence 'release.manifest.json'
    $pendingChecksum = Join-Path $evidence 'release.sha256'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $pendingManifest -Encoding UTF8
    [IO.File]::WriteAllText($pendingChecksum, $hash + '  ' + $archiveName + "`n", (New-Object Text.UTF8Encoding($false)))
    Assert-ReleaseAvailable
    # File.Move's two-argument overload refuses an existing destination, including
    # one created after the preflight. No release or sidecar is overwritten.
    [IO.File]::Move($temporaryArchive, $archivePath)
    [IO.File]::Move($pendingChecksum, ($archivePath + '.sha256'))
    [IO.File]::Move($pendingManifest, ($archivePath + '.manifest.json'))
    # The stage is this invocation's generated copy, never the publish directory.
    # Only remove it after both the payload hashes and published archive passed.
    $null = Checked-Path $stage
    if (-not $stage.StartsWith($evidence + [IO.Path]::DirectorySeparatorChar, $pathComparison)) { throw 'Unexpected stage directory.' }
    $remaining = @(Tree-Files $stage)
    $fileEntries = @($entries | Where-Object { -not $_.Directory })
    if ($remaining.Count -ne $fileEntries.Count) { throw 'Staging changed after verification; its contents were retained.' }
    foreach ($file in $remaining) {
        $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $expected = @($fileEntries | Where-Object Path -eq $relative)
        if ($expected.Count -ne 1 -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $expected[0].SHA256) { throw 'Staging changed after verification; its contents were retained.' }
    }
    foreach ($process in Get-Process) {
        try { $image = $process.Path } catch { $image = $null }
        if ($image -and $image.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, $pathComparison)) { throw 'A process is using the staging directory; its contents were retained.' }
    }
    $null = Checked-Path $stage
    Remove-Item -LiteralPath $stage -Recurse -Force
    $result.Passed = $true; $result.Archive = $archivePath; $result.ArchiveSHA256 = $hash; $result.Entries = $entries.Count; $result.PublishFiles = $sourceManifest.Count; $result.CreatorOS = 'Unix'; $result.DecompressedHashesVerified = $true; $result.UnixModesVerified = $true
    $result.StagingRemovedAfterVerification = $true
    Write-Output ('PASS: packaged ' + $runtimeId + ', ' + $sourceManifest.Count + ' publish files, verified decompressed SHA256 and Unix execution modes: ' + $archivePath)
    if ($runtimeId -eq 'osx-arm64') { Write-Output 'Preview only: this package has not been Developer ID signed, notarized, or run on a physical Mac by this packager.' }
}
catch { $result.Error = $_.Exception.ToString(); throw }
finally {
    $result.EndedUtc = [DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding UTF8
}
