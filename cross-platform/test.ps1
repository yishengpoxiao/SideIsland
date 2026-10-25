[CmdletBinding()]
param([string]$DotnetPath, [ValidateRange(1, 5)][int]$Rounds = 1)
$ErrorActionPreference = 'Stop'
$crossRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$releaseVersion = (Get-Content -LiteralPath (Join-Path $crossRoot '../VERSION') -Raw).Trim()
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The repository VERSION must declare a three-part release version.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $crossRoot '..'))
if (-not $DotnetPath) {
    foreach ($name in @('dotnet.exe','dotnet')) {
        $candidate = Join-Path $projectRoot ('.runtime/cross-platform-sdk/10.0.402/' + $name)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $DotnetPath = $candidate; break }
    }
    if (-not $DotnetPath) { $command = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1; if ($command) { $DotnetPath = $command.Source } }
    if (-not $DotnetPath) { throw 'A .NET 10 SDK is required; pass -DotnetPath or install the official SDK on this host.' }
}
$dotnet = (Get-Item -LiteralPath $DotnetPath).FullName
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$evidence = Join-Path $projectRoot ('artifacts/cross-platform/regression/' + $stamp)
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$saved = @{}; $pushed = $false
$result = [ordered]@{ Version = $releaseVersion; PlatformStatus = 'preview'; Passed = $false; Rounds = $Rounds; TargetNativeDesktopTested = $false; StartedUtc = [DateTime]::UtcNow.ToString('o') }
function Source-Hashes {
    $inputs = @(Get-ChildItem -LiteralPath $crossRoot -Recurse -File | Where-Object { $_.FullName -notmatch '[/\\](bin|obj|out)[/\\]' -and $_.Extension -in @('.cs','.csproj','.props','.manifest') })
    foreach ($file in $inputs | Sort-Object FullName) { [pscustomobject]@{ Path = $file.FullName; SHA256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash } }
    $versionPath = [IO.Path]::GetFullPath((Join-Path $projectRoot 'VERSION'))
    [pscustomobject]@{ Path = $versionPath; SHA256 = (Get-FileHash -LiteralPath $versionPath -Algorithm SHA256).Hash }
    foreach ($project in $inputs | Where-Object Extension -eq '.csproj') {
        [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw
        foreach ($node in $xml.SelectNodes('//Compile[@Include]')) {
            $path = [IO.Path]::GetFullPath((Join-Path $project.DirectoryName ([string]$node.Include)))
            [pscustomobject]@{ Path = $path; SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        }
    }
}
try {
    foreach ($name in @('DOTNET_ROOT','DOTNET_CLI_HOME','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','NUGET_PACKAGES')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
    $env:DOTNET_ROOT = [IO.Path]::GetDirectoryName($dotnet)
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.runtime/cli-home-cross'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'; $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.runtime/nuget-cross'
    Push-Location -LiteralPath $crossRoot; $pushed = $true
    $before = @(Source-Hashes); $before | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'source-before.json') -Encoding UTF8
    & $dotnet restore SideIsland.Tests --configfile NuGet.Config --nologo 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw 'Test restore failed; see restore.log.' }
    & $dotnet build SideIsland.Tests --no-restore --nologo 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed; see build.log.' }
    $test = Join-Path $crossRoot 'SideIsland.Tests/bin/Debug/net10.0/SideIsland.Tests.dll'
    $result.TestAssemblySHA256 = (Get-FileHash -LiteralPath $test -Algorithm SHA256).Hash
    $result.DesktopAssemblySHA256 = (Get-FileHash -LiteralPath (Join-Path $crossRoot 'SideIsland.Desktop/bin/Debug/net10.0/SideIsland.Desktop.dll') -Algorithm SHA256).Hash
    for ($round = 1; $round -le $Rounds; $round++) {
        $folder = Join-Path $evidence ('round-' + $round); New-Item -ItemType Directory -Path $folder -Force | Out-Null
        & $dotnet $test $folder 2>&1 | Tee-Object -FilePath (Join-Path $folder 'tests.log')
        if ($LASTEXITCODE -ne 0) { $result.FailedRound = $round; throw ('Regression failed in round ' + $round) }
    }
    $after = @(Source-Hashes); $after | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'source-after.json') -Encoding UTF8
    if (Compare-Object ($before | ForEach-Object { $_.Path + ':' + $_.SHA256 }) ($after | ForEach-Object { $_.Path + ':' + $_.SHA256 })) { throw 'Source files changed during regression; this run cannot validate the final code.' }
    $result.SourcesUnchanged = $true; $result.Passed = $true
    Write-Output ('PASS: ' + $Rounds + ' complete portable regression round(s); immutable input hashes retained in ' + $evidence)
}
catch { $result.Error = $_.Exception.ToString(); throw }
finally {
    try { $finalHashes = @(Source-Hashes); $finalHashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'source-at-exit.json') -Encoding UTF8 } catch { $result.SourceHashError = $_.Exception.Message }
    if ($pushed) { Pop-Location }
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name,$saved[$name],'Process') }
    $result.EndedUtc = [DateTime]::UtcNow.ToString('o'); $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding UTF8
}
