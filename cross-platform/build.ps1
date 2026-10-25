[CmdletBinding()]
param(
    [ValidateSet('osx-arm64', 'linux-x64', 'linux-arm64', 'win-x64')]
    [string]$Runtime = 'osx-arm64',
    [string]$DotnetPath,
    [switch]$SkipPackage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$crossRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$releaseVersion = (Get-Content -LiteralPath (Join-Path $crossRoot '../VERSION') -Raw).Trim()
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The repository VERSION must declare a three-part release version.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $crossRoot '..'))
$pathComparison = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$runtimeId = $Runtime.ToLowerInvariant()
$desktopProject = Join-Path $crossRoot 'SideIsland.Desktop/SideIsland.Desktop.csproj'
$nugetConfig = Join-Path $crossRoot 'NuGet.Config'
$stateRoot = Join-Path $projectRoot '.runtime'
$outRoot = Join-Path $crossRoot 'out'
$output = [IO.Path]::GetFullPath((Join-Path $outRoot $runtimeId))
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$logRoot = Join-Path $projectRoot ('artifacts/cross-platform/build/' + $runtimeId + '-' + $stamp)

function Assert-ProjectChild([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, $pathComparison)) { throw ('Path must remain inside this project: ' + $resolved) }
    for ($cursor = $resolved; $cursor -and $cursor.Length -gt $projectRoot.Length; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Reparse-point build path is not supported: ' + $cursor) }
    }
    return $resolved
}

function Find-Dotnet {
    if ($DotnetPath) { return (Get-Item -LiteralPath $DotnetPath -ErrorAction Stop).FullName }
    foreach ($name in @('dotnet.exe', 'dotnet')) {
        $local = Join-Path $stateRoot ('cross-platform-sdk/10.0.402/' + $name)
        if (Test-Path -LiteralPath $local) { return (Get-Item -LiteralPath $local).FullName }
    }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    throw 'A .NET 10 SDK is required. Install an official SDK or use -DotnetPath; a runtime alone cannot compile this project.'
}

function Source-Manifest {
    $files = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($folder in @('SideIsland.Core', 'SideIsland.Desktop')) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $crossRoot $folder) -File -Recurse -Force) {
            $relative = $file.FullName.Substring($crossRoot.Length + 1).Replace('\', '/')
            if ($relative -match '(^|/)(bin|obj)/') { continue }
            if ($file.Extension -in @('.cs', '.csproj', '.axaml', '.manifest', '.json', '.png', '.ico', '.icns')) { $files[$file.FullName] = $relative }
        }
    }
    foreach ($name in @('Directory.Build.props', 'global.json', 'NuGet.Config', 'build.ps1', 'package.ps1', 'packaging/Info.plist', 'packaging/side-island.desktop')) {
        $path = Join-Path $crossRoot $name
        if (Test-Path -LiteralPath $path) { $files[(Get-Item -LiteralPath $path).FullName] = 'cross-platform/' + $name }
    }
    $versionPath = [IO.Path]::GetFullPath((Join-Path $projectRoot 'VERSION'))
    $files[$versionPath] = 'VERSION'
    # Include linked legacy models as build inputs without editing the Windows project.
    foreach ($project in Get-ChildItem -LiteralPath $crossRoot -Filter '*.csproj' -File -Recurse | Where-Object { $_.FullName -notmatch '[/\\](bin|obj|out)[/\\]' }) {
        [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw
        foreach ($node in $xml.SelectNodes('//Compile[@Include]')) {
            $include = [string]$node.Include
            if ($include.Contains('*')) { throw ('Explicit linked source paths required for the manifest: ' + $include) }
            $path = [IO.Path]::GetFullPath((Join-Path $project.DirectoryName $include))
            if (-not (Test-Path -LiteralPath $path)) { throw ('Missing linked source: ' + $path) }
            $null = Assert-ProjectChild $path
            $files[$path] = $path.Substring($projectRoot.Length + 1).Replace('\', '/')
        }
    }
    return @($files.Keys | Sort-Object | ForEach-Object { [pscustomobject]@{ Path = $_; RelativePath = $files[$_]; SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
}

$null = Assert-ProjectChild $output
$null = Assert-ProjectChild $logRoot
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$result = [ordered]@{ Runtime = $runtimeId; Version = $releaseVersion; PlatformStatus = 'preview'; StartedUtc = [DateTime]::UtcNow.ToString('o'); Passed = $false; TargetRuntimeExecuted = $false; WindowsApplicationModified = $false }
$savedEnvironment = @{}
foreach ($name in @('DOTNET_ROOT', 'DOTNET_CLI_HOME', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$pushed = $false
try {
    $dotnet = Find-Dotnet
    $dotnetFile = Get-Item -LiteralPath $dotnet
    if ($dotnetFile.PSObject.Methods['ResolveLinkTarget']) {
        $linkTarget = $dotnetFile.ResolveLinkTarget($true)
        if ($linkTarget) { $dotnet = $linkTarget.FullName }
    }
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', [IO.Path]::GetDirectoryName($dotnet), 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_HOME', (Join-Path $stateRoot 'cli-home-cross'), 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_SKIP_FIRST_TIME_EXPERIENCE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_NOLOGO', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_GENERATE_ASPNET_CERTIFICATE', 'false', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'false', 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_PACKAGES', (Join-Path $stateRoot 'nuget-cross'), 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_HTTP_CACHE_PATH', (Join-Path $stateRoot 'nuget-http-cross'), 'Process')
    Push-Location -LiteralPath $crossRoot; $pushed = $true
    $sdk = (& $dotnet --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') { throw ('The pinned project requires a .NET 10 SDK; detected: ' + $sdk) }
    $result.Dotnet = $dotnet; $result.SdkVersion = $sdk
    $before = @(Source-Manifest)
    $before | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $logRoot 'sources-before.json') -Encoding UTF8

    if (Test-Path -LiteralPath $output) {
        $marker = Join-Path $output '.side-island-publish.json'
        if (-not (Test-Path -LiteralPath $marker)) { throw ('Refusing to remove an unmarked output directory: ' + $output) }
        $old = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($old.Runtime -ne $runtimeId -or $old.Kind -ne 'side-island-generated-publish') { throw ('Unexpected output marker: ' + $marker) }
        foreach ($process in Get-Process) {
            try { $image = $process.Path } catch { $image = $null }
            if ($image -and $image.StartsWith($output + [IO.Path]::DirectorySeparatorChar, $pathComparison)) { throw ('Stop the preview process before rebuilding ' + $output) }
        }
        $null = Assert-ProjectChild $output
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    # Mark only this script's exact generated output; failed builds remain inspectable.
    [ordered]@{ Kind = 'side-island-generated-publish'; Runtime = $runtimeId; Version = $result.Version; Complete = $false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output '.side-island-publish.json') -Encoding UTF8

    & $dotnet restore $desktopProject --configfile $nugetConfig --runtime $runtimeId '-p:SelfContained=true' 2>&1 | Tee-Object -FilePath (Join-Path $logRoot 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw 'Cross-platform restore failed; see restore.log.' }
    & $dotnet publish $desktopProject --configuration Release --runtime $runtimeId --self-contained true --no-restore --output $output '-p:PublishReadyToRun=false' '-p:DebugType=None' '-p:DebugSymbols=false' 2>&1 | Tee-Object -FilePath (Join-Path $logRoot 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw 'Cross-platform publish failed; see publish.log.' }
    $main = Join-Path $output $(if ($runtimeId -eq 'win-x64') { 'SideIsland.Desktop.exe' } else { 'SideIsland.Desktop' })
    if (-not (Test-Path -LiteralPath $main) -or (Get-Item -LiteralPath $main).Length -eq 0) { throw 'Publish did not produce the expected native apphost.' }
    $after = @(Source-Manifest)
    if ($before.Count -ne $after.Count) { throw 'Build source set changed during publication.' }
    for ($i = 0; $i -lt $before.Count; $i++) { if ($before[$i].Path -ne $after[$i].Path -or $before[$i].SHA256 -ne $after[$i].SHA256) { throw ('Build input changed: ' + $before[$i].Path) } }
    $after | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $logRoot 'sources-after.json') -Encoding UTF8
    [ordered]@{ Kind = 'side-island-generated-publish'; Runtime = $runtimeId; Version = $result.Version; Complete = $true; ExecutableSHA256 = (Get-FileHash -LiteralPath $main -Algorithm SHA256).Hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output '.side-island-publish.json') -Encoding UTF8
    $result.PublishDirectory = $output; $result.ExecutableSHA256 = (Get-FileHash -LiteralPath $main -Algorithm SHA256).Hash; $result.SourcesUnchanged = $true
    if (-not $SkipPackage) {
        & (Join-Path $crossRoot 'package.ps1') -Runtime $runtimeId -PublishDirectory $output -Version $result.Version
    }
    $result.Passed = $true
    Write-Output ('PASS: published ' + $runtimeId + ' from unchanged sources; target OS execution is a separate validation step.')
}
catch { $result.Error = $_.Exception.ToString(); throw }
finally {
    if ($pushed) { Pop-Location }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    $result.EndedUtc = [DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $logRoot 'result.json') -Encoding UTF8
}
