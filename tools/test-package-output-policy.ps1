[CmdletBinding()]
param([string]$PackagerPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PackagerPath) { $PackagerPath = Join-Path $projectRoot 'cross-platform/package.ps1' }
$PackagerPath = (Get-Item -LiteralPath $PackagerPath).FullName
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$evidence = Join-Path $projectRoot ('artifacts/packaging-output-policy/' + $stamp)
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$checks = New-Object 'System.Collections.Generic.List[object]'
$result = [ordered]@{ Passed = $false; PackagerSHA256 = (Get-FileHash -LiteralPath $PackagerPath -Algorithm SHA256).Hash; StartedUtc = [DateTime]::UtcNow.ToString('o'); Scope = 'Actual packaging script with isolated synthetic apphost fixtures; no target OS execution or published assets changed.' }

function Snapshot([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { return @() }
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Root.Length + 1) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })
}
function Assert-Equal($Actual, $Expected, [string]$Message) {
    if (($Actual -join "`n") -cne ($Expected -join "`n")) { throw $Message }
}
function New-Fixture([string]$Name) {
    $root = Join-Path $evidence $Name
    New-Item -ItemType Directory -Path (Join-Path $root 'cross-platform'), (Join-Path $root 'release') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'VERSION'), '1.0.0')
    Copy-Item -LiteralPath $PackagerPath -Destination (Join-Path $root 'cross-platform/package.ps1')
    return $root
}
function Archive-Name([string]$Runtime) {
    if ($Runtime -eq 'win-x64') { return 'SideIsland-Preview-v1.0.0-win-x64.zip' }
    return 'SideIsland-v1.0.0-' + $Runtime + '.zip'
}
function Expect-Occupied([string]$Root, [string]$Runtime, [string]$ExpectedPath, [string]$Name) {
    $before = @(Snapshot $Root); $failure = $null
    try { & (Join-Path $Root 'cross-platform/package.ps1') -Runtime $Runtime -PublishDirectory './out/fixture' | Out-Null }
    catch { $failure = $_.Exception.Message }
    if (-not $failure -or -not $failure.Contains('Release already exists;') -or -not $failure.Contains($ExpectedPath)) { throw ('Expected an early occupied-release error for ' + $Name + '; got: ' + $failure) }
    Assert-Equal @(Snapshot $Root) $before ('Occupied output changed files: ' + $Name)
    $checks.Add([pscustomobject]@{ Name = $Name; Passed = $true; Error = $failure; FilesUnchanged = $true })
}

$publishedBefore = @(Snapshot (Join-Path $projectRoot 'release'))
try {
    foreach ($runtime in @('win-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')) {
        foreach ($suffix in @('', '.sha256', '.manifest.json')) {
            $name = $runtime + '-occupied-' + $(if ($suffix) { $suffix.TrimStart('.') } else { 'zip' })
            $root = New-Fixture $name
            $occupied = Join-Path $root ('release/' + (Archive-Name $runtime) + $suffix)
            [IO.File]::WriteAllText($occupied, 'keep this original release file')
            Expect-Occupied $root $runtime $occupied $name
        }
    }

    # These fixtures deliberately exercise only packager architecture/header and
    # version checks. Their tiny native headers are not runnable applications.
    $fixtureAssembly = Join-Path $evidence 'VersionFixture.dll'
    Add-Type -TypeDefinition ('using System.Reflection; [assembly: AssemblyVersion("1.0.0.0")] public static class PackageVersion' + [Guid]::NewGuid().ToString('N') + ' {}') -OutputAssembly $fixtureAssembly
    foreach ($runtime in @('win-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')) {
        $root = New-Fixture ($runtime + '-new-output')
        $cross = Join-Path $root 'cross-platform'; $publish = Join-Path $cross 'out/fixture'
        New-Item -ItemType Directory -Path $publish, (Join-Path $cross 'packaging') -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $cross 'README.md'), 'Synthetic packaging fixture. Not a runnable release.')
        [IO.File]::WriteAllText((Join-Path $cross 'README.zh-CN.md'), 'Synthetic packaging fixture. Not a runnable release.')
        [IO.File]::WriteAllText((Join-Path $cross 'packaging/side-island.desktop'), '[Desktop Entry]')
        [IO.File]::WriteAllText((Join-Path $cross 'packaging/Info.plist'), '<plist><dict><key>CFBundleShortVersionString</key><string>0.0.0</string><key>CFBundleVersion</key><string>0.0.0</string><key>SideIslandPreviewVersion</key><string>0.0.0</string></dict></plist>')
        $head = New-Object byte[] 70
        if ($runtime -eq 'win-x64') { $head[0] = 0x4D; $head[1] = 0x5A; $head[60] = 64; $head[64] = 0x50; $head[65] = 0x45; $head[68] = 0x64; $head[69] = 0x86 }
        elseif ($runtime -eq 'osx-arm64') { [BitConverter]::GetBytes([Convert]::ToUInt32('FEEDFACF', 16)).CopyTo($head, 0); [BitConverter]::GetBytes([uint32]0x0100000C).CopyTo($head, 4) }
        else { $head[0] = 0x7F; $head[1] = 0x45; $head[2] = 0x4C; $head[3] = 0x46; $head[4] = 2; $head[5] = 1; $head[18] = $(if ($runtime -eq 'linux-x64') { 62 } else { 183 }) }
        $apphost = Join-Path $publish $(if ($runtime -eq 'win-x64') { 'SideIsland.Desktop.exe' } else { 'SideIsland.Desktop' })
        [IO.File]::WriteAllBytes($apphost, $head)
        Copy-Item -LiteralPath $fixtureAssembly -Destination (Join-Path $publish 'SideIsland.Desktop.dll')
        [ordered]@{ Kind = 'side-island-generated-publish'; Runtime = $runtime; Version = '1.0.0'; Complete = $true; ExecutableSHA256 = (Get-FileHash -LiteralPath $apphost -Algorithm SHA256).Hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish '.side-island-publish.json') -Encoding UTF8
        $wpf = Join-Path $root 'release/SideIsland-v1.0.0-win-x64.zip'
        foreach ($suffix in @('', '.sha256', '.manifest.json')) { [IO.File]::WriteAllText(($wpf + $suffix), 'preserve the WPF release') }
        $wpfBefore = @(Snapshot (Join-Path $root 'release'))
        & (Join-Path $cross 'package.ps1') -Runtime $runtime -PublishDirectory './out/fixture' | Set-Content -LiteralPath (Join-Path $root 'invocation.log') -Encoding UTF8
        $archive = Join-Path $root ('release/' + (Archive-Name $runtime))
        foreach ($suffix in @('', '.sha256', '.manifest.json')) { if (-not (Test-Path -LiteralPath ($archive + $suffix) -PathType Leaf)) { throw ('Missing output: ' + $archive + $suffix) } }
        $afterWpf = @(Snapshot (Join-Path $root 'release') | Where-Object { $_.StartsWith('SideIsland-v1.0.0-win-x64.zip') })
        Assert-Equal $afterWpf $wpfBefore 'Packaging changed the separate WPF release.'
        $manifest = Get-Content -LiteralPath ($archive + '.manifest.json') -Raw | ConvertFrom-Json
        if (-not $manifest.Verified -or $manifest.Archive -ne (Archive-Name $runtime) -or $manifest.SHA256 -ne (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash) { throw 'New output failed manifest/hash checks.' }
        $checks.Add([pscustomobject]@{ Name = $runtime + '-new-output'; Passed = $true; WpfReleaseUnchanged = $true; VerifiedArchive = $manifest.Archive })
        Expect-Occupied $root $runtime $archive ($runtime + '-repeat-refused')
    }
    $result.Passed = $true
}
catch { $result.Error = $_.Exception.ToString(); throw }
finally {
    $unchanged = (($publishedBefore -join "`n") -ceq ((@(Snapshot (Join-Path $projectRoot 'release'))) -join "`n"))
    $result.PublishedAssetsUnchanged = $unchanged
    if (-not $unchanged) { $result.Passed = $false; $result.Error = 'The real release directory changed during verification; inspect concurrent operations.' }
    $result.EndedUtc = [DateTime]::UtcNow.ToString('o'); $result.Checks = $checks.ToArray()
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding UTF8
    Write-Output ('Packaging output policy: ' + $checks.Count + ' checks; passed=' + $result.Passed + '; evidence=' + $evidence)
    if (-not $unchanged) { throw $result.Error }
}
