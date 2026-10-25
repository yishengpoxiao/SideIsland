[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION')).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain a three-part release version.' }
$expectedVersion = $version + '.0'
$executable = Join-Path $projectRoot 'dist\CodexIsland.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Build dist/CodexIsland.exe first.' }
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
$releaseRoot = Join-Path $projectRoot 'release'
$archiveName = 'SideIsland-v' + $version + '-win-x64.zip'
$archive = Join-Path $releaseRoot $archiveName
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$evidence = Join-Path $projectRoot ('artifacts\releases\v' + $version + '\' + $stamp)
$stage = Join-Path $evidence 'stage'
$temporaryArchive = Join-Path $evidence 'verified-payload.zip'
$sources = New-Object 'System.Collections.Generic.List[object]'
$result = [ordered]@{ Version = $version; ExecutableVersion = $actualVersion; Archive = $archiveName; StartedUtc = [DateTime]::UtcNow.ToString('o'); Passed = $false; StageRemoved = $false }
$transcriptStarted = $false

function Checked-Path([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release paths must remain in the project workspace.' }
    for ($cursor = $full; $cursor -and $cursor.Length -gt $projectRoot.Length; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Release paths must not follow a reparse point: ' + $cursor) }
    }
    return $full
}
function Tree-Files([string]$Root) {
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $files = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
    $pending.Push((Checked-Path $Root))
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $null = Checked-Path $item.FullName
            if ($item.PSIsContainer) { $pending.Push($item.FullName) } else { $files.Add($item) }
        }
    }
    return @($files | Sort-Object FullName)
}
function Copy-ReleaseFile([string]$Source, [string]$Relative) {
    $sourcePath = Checked-Path (Join-Path $projectRoot $Source)
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw ('Missing release input: ' + $Source) }
    $target = Checked-Path (Join-Path $stage $Relative)
    $hash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $target
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw ('Copy verification failed: ' + $Relative) }
    $sources.Add([pscustomobject]@{ Source = $Source; Path = $Relative.Replace('\', '/'); SHA256 = $hash })
}
function Copy-Documentation([string]$Relative, [string[]]$Extensions) {
    $root = Checked-Path (Join-Path $projectRoot $Relative)
    foreach ($file in Tree-Files $root) {
        if ($file.Extension.ToLowerInvariant() -notin $Extensions) { throw ('Unexpected file in documentation/license directory: ' + $Relative + '/' + $file.Name) }
        $relativeFile = $Relative + '/' + $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        Copy-ReleaseFile $relativeFile $relativeFile
    }
}
function Snapshot-Stage {
    return @(Tree-Files $stage | ForEach-Object { [pscustomobject]@{ Path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); Length = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
}
function Require-StageUnchanged($Before) {
    $after = @(Snapshot-Stage)
    if ($after.Count -ne $Before.Count) { throw 'Release staging file count changed.' }
    for ($i = 0; $i -lt $Before.Count; $i++) {
        if ($after[$i].Path -ne $Before[$i].Path -or $after[$i].Length -ne $Before[$i].Length -or $after[$i].SHA256 -ne $Before[$i].SHA256) { throw ('Release staging changed: ' + $Before[$i].Path) }
    }
    foreach ($source in $sources) { if ((Get-FileHash -LiteralPath (Checked-Path (Join-Path $projectRoot $source.Source)) -Algorithm SHA256).Hash -ne $source.SHA256) { throw ('Release input changed during packaging: ' + $source.Source) } }
}
function Verify-DocumentationLinks {
    foreach ($file in Tree-Files $stage | Where-Object { $_.Extension -eq '.md' }) {
        foreach ($match in [regex]::Matches([IO.File]::ReadAllText($file.FullName), '\]\(([^)]+)\)')) {
            $link = $match.Groups[1].Value.Trim()
            if ($link -match '^(?:[A-Za-z][A-Za-z0-9+.-]*:|#)') { continue }
            $relative = [Uri]::UnescapeDataString(([regex]::Replace($link, '[?#].*$', '')).Trim('<', '>'))
            if (-not $relative) { continue }
            $target = Checked-Path (Join-Path $file.DirectoryName $relative)
            if (-not $target.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $target)) { throw ('Broken packaged documentation link: ' + $file.Name + ' -> ' + $relative) }
        }
    }
}
try {
    $null = Checked-Path $releaseRoot; $null = Checked-Path $stage
    foreach ($target in @($archive, ($archive + '.sha256'), ($archive + '.manifest.json'))) { $null = Checked-Path $target; if (Test-Path -LiteralPath $target) { throw ('Release already exists; review it before explicitly replacing it: ' + $target) } }
    New-Item -ItemType Directory -Path $releaseRoot, $stage -Force | Out-Null
    Start-Transcript -LiteralPath (Join-Path $evidence 'build-release.log') | Out-Null; $transcriptStarted = $true
    if ($actualVersion -ne $expectedVersion) { throw ('The executable is ' + $actualVersion + '; expected ' + $expectedVersion + '. Rebuild the app; renaming an old archive is not a release build.') }
    foreach ($name in @('CodexIsland.exe', 'CodexIsland.exe.config')) { Copy-ReleaseFile ('dist/' + $name) $name }
    foreach ($name in @('README.md', 'README.zh-CN.md', 'VERSION', 'THIRD-PARTY-NOTICES.txt', 'install-notifications.ps1', 'install-notifications.cmd', '启动通知版.cmd')) { Copy-ReleaseFile $name $name }
    foreach ($name in @('AppxManifest.xml', 'stage-package.ps1', 'validate-manifest.ps1', 'build-package.ps1', 'build-assets.ps1', 'README.md')) { Copy-ReleaseFile ('packaging/' + $name) ('packaging/' + $name) }
    foreach ($name in @('Square150x150Logo.png', 'Square44x44Logo.png', 'StoreLogo.png')) { Copy-ReleaseFile ('packaging/NotificationAssets/' + $name) ('packaging/NotificationAssets/' + $name) }
    Copy-Documentation 'docs' @('.md', '.txt')
    foreach ($name in @('README.md', 'README.zh-CN.md')) { Copy-ReleaseFile ('cross-platform/' + $name) ('cross-platform/' + $name) }
    Copy-Documentation 'cross-platform/docs' @('.md', '.txt')
    Copy-Documentation 'cross-platform/licenses' @('.txt', '.json', '.md')
    Copy-ReleaseFile 'tools/native-diagnostics/README.md' 'tools/native-diagnostics/README.md'
    Verify-DocumentationLinks
    $files = @(Snapshot-Stage)
    $sources.ToArray() | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'source-inputs.json') -Encoding UTF8
    $zip = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
    try { foreach ($file in $files) { $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $stage $file.Path), $file.Path, [IO.Compression.CompressionLevel]::Optimal) } }
    finally { $zip.Dispose() }
    $reader = [IO.Compression.ZipFile]::OpenRead($temporaryArchive)
    try {
        if ($reader.Entries.Count -ne $files.Count) { throw 'ZIP file count differs from the verified stage.' }
        foreach ($file in $files) {
            $entry = $reader.GetEntry($file.Path)
            if ($null -eq $entry -or $entry.Length -ne $file.Length) { throw ('ZIP entry missing or length differs: ' + $file.Path) }
            $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', ''); if ($hash -ne $file.SHA256) { throw ('ZIP decompressed SHA256 differs: ' + $file.Path) } }
            finally { $sha.Dispose(); $stream.Dispose() }
        }
    }
    finally { $reader.Dispose() }
    Require-StageUnchanged $files
    $archiveHash = (Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash
    $manifest = [ordered]@{ Version = $version; Runtime = 'win-x64'; ExecutableVersion = $actualVersion; Archive = $archiveName; SHA256 = $archiveHash; FileCount = $files.Count; Files = $files; DecompressedHashesVerified = $true; DocumentationLinksVerified = $true; SourceInputsUnchanged = $true; VerifiedUtc = [DateTime]::UtcNow.ToString('o') }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'release.manifest.json') -Encoding UTF8
    [IO.File]::WriteAllText((Join-Path $evidence 'release.sha256'), ($archiveHash + '  ' + $archiveName + [Environment]::NewLine), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporaryArchive -Destination $archive
    Copy-Item -LiteralPath (Join-Path $evidence 'release.manifest.json') -Destination ($archive + '.manifest.json')
    Copy-Item -LiteralPath (Join-Path $evidence 'release.sha256') -Destination ($archive + '.sha256')
    Require-StageUnchanged $files
    $cleanup = Checked-Path $stage
    if ($cleanup -ne [IO.Path]::GetFullPath((Join-Path $evidence 'stage')) -or -not $cleanup.StartsWith((Join-Path $projectRoot ('artifacts\releases\v' + $version)) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside this generated release stage.' }
    Remove-Item -LiteralPath $cleanup -Recurse -Force
    $result.StageRemoved = $true; $result.Passed = $true; $result.SHA256 = $archiveHash; $result.FileCount = $files.Count
    Write-Output ('Verified release: ' + $archive)
}
catch { $result.Error = $_.Exception.ToString(); throw }
finally {
    $result.EndedUtc = [DateTime]::UtcNow.ToString('o')
    if (Test-Path -LiteralPath $evidence) { $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding UTF8 }
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}
