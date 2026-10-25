param(
    [string]$OutputDirectory = '.runtime\appearance-mode-stress',
    [switch]$SelfCheck
)
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $projectPath $OutputDirectory }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$compilerRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $compilerRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The 64-bit .NET Framework 4.x compiler is required.' }
$referencePaths = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Runtime.dll') | ForEach-Object { Join-Path $compilerRoot $_ }
$referencePaths += @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object { Join-Path (Join-Path $compilerRoot 'WPF') $_ }
$referencePaths += @('Windows.UI.winmd','Windows.Foundation.winmd','Windows.ApplicationModel.winmd','Windows.Media.winmd','Windows.Storage.winmd','Windows.Graphics.winmd','Windows.Globalization.winmd') | ForEach-Object { Join-Path (Join-Path $env:WINDIR 'System32\WinMetadata') $_ }
foreach($reference in $referencePaths) { if(-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw ('Missing reference: '+$reference) } }
$stressSource = Join-Path $PSScriptRoot 'AppearanceModeStress.cs'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -File -Filter '*.cs' -Recurse | Sort-Object FullName | ForEach-Object FullName) + @($stressSource)
$hashes = @(foreach($source in $sources) { [ordered]@{Path=$source;SHA256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash} })
$target = Join-Path $outputPath 'NativeTests.exe'
$options = @('/nologo','/langversion:5','/target:exe','/main:CodexIsland.AppearanceModeStress',('/out:'+$target))
$arguments = $options + @($referencePaths | ForEach-Object { '/reference:'+$_ }) + $sources
& $compiler $arguments 2>&1 | Tee-Object -FilePath (Join-Path $outputPath 'compile.log')
if($LASTEXITCODE -ne 0) { throw 'Appearance/mode diagnostic compilation failed.' }
foreach($item in $hashes) { if((Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash -ne $item.SHA256) { throw ('Compile input changed; rebuild after edits finish: '+$item.Path) } }
$manifest = [ordered]@{
    Status='compile_only_passed';CompiledUtc=[DateTime]::UtcNow.ToString('o');Target=$target
    TargetSHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    Entry='CodexIsland.AppearanceModeStress';Compiler=$compiler
    CompilerVersion=(Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    CompilerSHA256=(Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
    CompilerOptions=$options;Sources=$hashes
    StressSourceSHA256=(Get-FileHash -LiteralPath $stressSource -Algorithm SHA256).Hash
    BuildScriptSHA256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    FrameworkReferences=@(foreach($reference in $referencePaths) { [ordered]@{Path=$reference;SHA256=(Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash} })
    SourceHashesStableDuringCompile=$true;RegressionSuiteInvoked=$false;UiExecuted=$false
    FixedScenarios=16;ExpectedInMotionAppearanceActions=64;ExpectedReversals=64
    SelfCheckRequested=[bool]$SelfCheck
}
$manifestPath = Join-Path $outputPath 'appearance-mode-stress-build.json'
$manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
if($SelfCheck) {
    # This argument validates the fixed plan, reflection contracts and hashes;
    # it returns before any Window, Application, Dispatcher or UI is created.
    & $target --self-check 2>&1 | Tee-Object -FilePath (Join-Path $outputPath 'self-check.log')
    if($LASTEXITCODE -ne 0) { throw 'Non-UI diagnostic self-check failed.' }
}
Write-Output ('Compiled diagnostic only (UI not launched): '+$target)
Write-Output ('Source and binary evidence: '+$manifestPath)
