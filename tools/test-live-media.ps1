param(
    [string]$SourceId = '',
    [ValidateRange(1,10)][int]$Samples = 1,
    [ValidateRange(1,5)][int]$IntervalSeconds = 1,
    [string]$OutputPath = '',
    [switch]$OnlineLyrics,
    [ValidateSet('toggle','previous','next')][string]$Command,
    [ValidateRange(0,600)][int]$TimeoutSeconds = 0,
    [switch]$CompileOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ($SourceId.Length -gt 512 -or $SourceId.IndexOf([char]0) -ge 0) { throw 'Invalid SourceId.' }
if ($Command -and [string]::IsNullOrWhiteSpace($SourceId)) { throw 'An explicit command requires the exact nonempty SourceId from a read-only report.' }
if ($Command) { $Command = $Command.ToLowerInvariant() }
if (-not $OutputPath) {
    $OutputPath = Join-Path $projectRoot ('artifacts\live-media-tests\{0}-{1}.json' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N').Substring(0,8))
}
if (-not [IO.Path]::IsPathRooted($OutputPath)) { $OutputPath = Join-Path $projectRoot $OutputPath }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$projectPrefix = $projectRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $OutputPath.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($OutputPath) -ne '.json') { throw 'OutputPath must be a JSON file inside this project.' }
if ($TimeoutSeconds -eq 0) {
    $TimeoutSeconds = 15 + $Samples * (23 + $(if ($OnlineLyrics) { 7 } else { 0 })) + ($Samples - 1) * $IntervalSeconds + $(if ($Command) { 45 } else { 0 })
}
if ($TimeoutSeconds -lt 10) { throw 'TimeoutSeconds must be zero (automatic) or at least 10.' }
$runtime = Join-Path $projectRoot '.runtime\live-media'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler is required.' }
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $framework 'WPF') $_) }
$references += '/reference:' + (Join-Path $framework 'System.Runtime.dll')
$references += @('Windows.UI.winmd','Windows.Foundation.winmd','Windows.ApplicationModel.winmd','Windows.Media.winmd','Windows.Storage.winmd','Windows.Graphics.winmd','Windows.Globalization.winmd') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $env:WINDIR 'System32\WinMetadata') $_) }
$executable = Join-Path $runtime 'LiveMediaProbe.exe'
$source = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -Recurse | ForEach-Object FullName
$arguments = @('/nologo','/langversion:5','/optimize+','/platform:anycpu','/target:exe','/main:CodexIsland.LiveMediaProbe',('/out:' + $executable),('/win32manifest:' + (Join-Path $projectRoot 'app.manifest'))) + $references + $source + (Join-Path $PSScriptRoot 'LiveMediaProbe.cs')
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'Live-media probe compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'CodexIsland.exe.config') -Destination ($executable + '.config') -Force
if ($CompileOnly) { Write-Output ('Compiled without querying or controlling media: ' + $executable); return }
$runId = [Guid]::NewGuid().ToString('N')
$optionsFile = Join-Path $runtime ('options-' + $runId + '.json')
$stdout = Join-Path $runtime ('stdout-' + $runId + '.log')
$stderr = Join-Path $runtime ('stderr-' + $runId + '.log')
$options = [ordered]@{
    SourceId=$SourceId; Samples=$Samples; IntervalSeconds=$IntervalSeconds; OutputPath=$OutputPath
    OnlineLyrics=$OnlineLyrics.IsPresent; Command=$Command; TimeoutSeconds=$TimeoutSeconds; ProjectRoot=$projectRoot
}
[IO.File]::WriteAllText($optionsFile, ($options | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
if ($Command) { Write-Output ('Explicit one-shot SMTC command: ' + $Command + ' -> ' + $SourceId) }
else { Write-Output 'Read-only probe; no media or audio control commands will be sent.' }
$process = Start-Process -FilePath $executable -ArgumentList @('--options-file', ('"' + $optionsFile + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$watch = [Diagnostics.Stopwatch]::StartNew()
$timedOut = $false
while (-not $process.WaitForExit(250)) {
    if ($watch.Elapsed.TotalSeconds -gt $TimeoutSeconds + 5) {
        $timedOut = $true
        # Only terminate the helper created above, never a player or the island app.
        $process.Kill()
        if (-not $process.WaitForExit(5000)) { throw 'Probe watchdog could not stop its helper within five seconds; no unbounded wait was performed.' }
        break
    }
}
$process.Refresh()
if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -Encoding UTF8 }
if ((Test-Path -LiteralPath $stderr) -and (Get-Item -LiteralPath $stderr).Length -gt 0) { Get-Content -LiteralPath $stderr -Encoding UTF8 | Write-Warning }
Write-Output ('Report: ' + $OutputPath)
if ($timedOut) { throw 'Probe watchdog expired; the report, if present, contains only completed observations. A command timeout must never be automatically retried.' }
if ($process.ExitCode -ne 0) { throw ('Live-media probe ended with exit code ' + $process.ExitCode + '; inspect the report and logs.') }
# A completed probe is an observation, not a claim that a player passed compatibility.
