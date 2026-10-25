param([string]$OutputDirectory = '.runtime\notification-menu-baseline-build')
$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $project $OutputDirectory }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($project + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Project output required.' }
for ($cursor = $output; $cursor -and $cursor.Length -ge $project.Length; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Output must not pass through a reparse point.' }
}
if (Test-Path -LiteralPath $output) { throw 'Fresh isolated build directory required.' }
New-Item -ItemType Directory -Path $output | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'; $compiler = Join-Path $framework 'csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler is required.' }
$production = @(Get-ChildItem -LiteralPath (Join-Path $project 'src') -File -Filter '*.cs' -Recurse | Sort-Object FullName | ForEach-Object FullName)
$harness = Join-Path $PSScriptRoot 'NotificationMenuBenchmark.cs'
$appManifest = Join-Path $project 'app.manifest'
$config = Join-Path $project 'CodexIsland.exe.config'
$productionHashes = @($production | ForEach-Object { [ordered]@{ Path = $_; SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
$inputHashes = @(@($harness, $appManifest, $config, $PSCommandPath) | ForEach-Object { [ordered]@{ Path = $_; SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll', 'System.Runtime.dll') | ForEach-Object { Join-Path $framework $_ }
$references += @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll') | ForEach-Object { Join-Path (Join-Path $framework 'WPF') $_ }
$references += @('Windows.UI.winmd', 'Windows.Foundation.winmd', 'Windows.ApplicationModel.winmd', 'Windows.Media.winmd', 'Windows.Storage.winmd', 'Windows.Graphics.winmd', 'Windows.Globalization.winmd') | ForEach-Object { Join-Path (Join-Path $env:WINDIR 'System32\WinMetadata') $_ }
$exe = Join-Path $output 'NotificationMenuBenchmark.exe'
$options = @('/nologo', '/langversion:5', '/optimize+', '/platform:anycpu', '/target:exe', '/main:CodexIsland.NotificationMenuBenchmark', ('/win32manifest:' + $appManifest), ('/out:' + $exe))
& $compiler ($options + @($references | ForEach-Object { '/reference:' + $_ }) + $production + @($harness)) 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw 'Notification menu benchmark build failed.' }
foreach ($item in @($productionHashes) + @($inputHashes)) { if ((Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash -ne $item.SHA256) { throw ('Build input changed: ' + $item.Path) } }
Copy-Item -LiteralPath $config -Destination ($exe + '.config')
[ordered]@{
    Status = 'compile_only_passed'; UiExecuted = $false; EntryPoint = 'CodexIsland.NotificationMenuBenchmark'
    CompiledUtc = [DateTime]::UtcNow.ToString('o'); ProjectRoot = $project; Executable = $exe
    ExecutableSHA256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    RuntimeConfigSHA256 = (Get-FileHash -LiteralPath ($exe + '.config') -Algorithm SHA256).Hash
    Compiler = $compiler; CompilerVersion = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion; CompilerOptions = $options
    References = @($references); ProductionSources = $productionHashes; HarnessInputs = $inputHashes
    NativeWindowCreated = $false; MenusOpened = $false; ExternalActionsStarted = $false
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'notification-menu-build.json') -Encoding UTF8
Write-Output ('Compiled only; not executed: ' + $exe)
