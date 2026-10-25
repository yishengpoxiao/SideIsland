param(
    [string]$OutputDirectory = '.runtime\fixed-native-loop'
)
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$inputDirectory = Join-Path $projectPath 'tools\native-diagnostics\fixtures\layout-loop'
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectPath $OutputDirectory
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$target = Join-Path $outputPath 'NativeTests.exe'

# These are the original uninstrumented fixtures and unchanged eight-iteration
# entry used for the captured failure. Rebuilds must not silently replace them
# with the subsequently instrumented/current fixtures from tests/.
$fixedInputs = @(
    @{ Name = 'CompactContentStateTests.cs'; SHA256 = '3D8A71256140309130019CC79505533A40E1D43C1307DFD65BA35CAE61E6C9C7' },
    @{ Name = 'CompactMediaStateTests.cs'; SHA256 = '10606EA01CD5B1906FE1F7D6DBA3103C4061CBCC30520BF80EFBAB8437A8FB20' },
    @{ Name = 'LoopEntry.cs'; SHA256 = '0134EB322529383E2E20F841194AA6E562A9F23C7E7FA637A366746A82195C00' }
)
$verifiedInputs = foreach ($fixedInput in $fixedInputs) {
    $path = Join-Path $inputDirectory $fixedInput.Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Missing retained loop input: ' + $path) }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($hash -ne $fixedInput.SHA256) { throw ('Retained loop input hash mismatch: ' + $path) }
    [ordered]@{ Name = $fixedInput.Name; Path = $path; SHA256 = $hash }
}

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The 64-bit .NET Framework 4.x compiler is required for the native layout diagnostic target.'
}
$referencePaths = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll') |
    ForEach-Object { Join-Path $framework $_ }
$referencePaths += @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll') |
    ForEach-Object { Join-Path (Join-Path $framework 'WPF') $_ }
$referencePaths += Join-Path $framework 'System.Runtime.dll'
$referencePaths += @('Windows.UI.winmd', 'Windows.Foundation.winmd', 'Windows.ApplicationModel.winmd', 'Windows.Media.winmd', 'Windows.Storage.winmd', 'Windows.Graphics.winmd', 'Windows.Globalization.winmd') |
    ForEach-Object { Join-Path (Join-Path $env:WINDIR 'System32\WinMetadata') $_ }
foreach ($reference in $referencePaths) {
    if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw ('Missing framework reference: ' + $reference) }
}
$references = @($referencePaths | ForEach-Object { '/reference:' + $_ })
$productionSource = @(Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -Filter '*.cs' -Recurse -File | Sort-Object FullName | ForEach-Object FullName)
$testFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectPath 'tests') -Filter '*.cs' -File | Sort-Object FullName)
$excludedTestFiles = @($testFiles | Where-Object {
    $_.Name -eq 'CompactContentStateTests.cs' -or
    $_.Name -eq 'CompactMediaStateTests.cs' -or
    $_.Name -like 'NativeLayoutCrashRepro*.cs'
})
$testSource = @($testFiles | Where-Object {
    $_.Name -ne 'CompactContentStateTests.cs' -and
    $_.Name -ne 'CompactMediaStateTests.cs' -and
    $_.Name -notlike 'NativeLayoutCrashRepro*.cs'
} | ForEach-Object FullName)
$source = @($productionSource) + @($testSource) + @($verifiedInputs | ForEach-Object { $_.Path })
$sourceHashes = @(foreach ($path in $source) {
    [ordered]@{ Path = $path; SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
})
$referenceHashes = @(foreach ($path in $referencePaths) {
    [ordered]@{ Path = $path; SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
})

[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$manifestPath = Join-Path $outputPath 'layout-loop-build.json'
$sourceListPath = Join-Path $outputPath 'compiled-sources.json'
$compilerLogPath = Join-Path $outputPath 'compile.log'
$options = @('/nologo', '/langversion:5', '/target:exe', '/main:CodexIsland.NativeLayoutLoopEntry', ('/out:' + $target))
$arguments = $options + $references + $source
$manifest = [ordered]@{
    Status = 'compile_pending'
    StartedUtc = [DateTime]::UtcNow.ToString('o')
    Project = $projectPath
    Target = $target
    Entry = 'CodexIsland.NativeLayoutLoopEntry'
    MaximumIterations = 8
    OriginalToolLayoutRunCalledUnchanged = $true
    OriginalRawFixtureHashesVerified = $true
    FixedInputs = $verifiedInputs
    CurrentProductionAndOtherTestSources = $true
    SourceCount = $source.Count
    SourceManifest = $sourceListPath
    ExcludedTestSources = @($excludedTestFiles | ForEach-Object FullName)
    InstrumentationExcluded = $true
    UiExecuted = $false
    ArgsIgnoredForCollectorCompatibility = $true
    AddedSleepOrGcOrRenderPolicy = $false
    RegressionCoverageClaim = $false
    Purpose = 'Fault-trigger pressure sequence; cannot replace final complete NativeTests regression or establish a root-cause fix by normal exit.'
    Compiler = $compiler
    CompilerVersion = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    CompilerSHA256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
    CompilerOptions = $options
    FrameworkReferences = $referenceHashes
    CompilerLog = $compilerLogPath
    BuildScriptSHA256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
}
$sourceHashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $sourceListPath -Encoding UTF8
$manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
try {
    # Compile only. This script never invokes NativeTests.exe or another UI.
    & $compiler $arguments 2>&1 | Tee-Object -FilePath $compilerLogPath
    if ($LASTEXITCODE -ne 0) { throw ('Native layout loop compilation failed, compiler exit ' + $LASTEXITCODE + '.') }
    foreach ($item in $sourceHashes) {
        if ((Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash -ne $item.SHA256) {
            throw ('A compile input changed while csc was running; rebuild after edits finish: ' + $item.Path)
        }
    }
    $manifest['Status'] = 'compile_only_passed'
    $manifest['TargetSHA256'] = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    $manifest['SourceHashesStableDuringCompile'] = $true
    $manifest['CompletedUtc'] = [DateTime]::UtcNow.ToString('o')
    Write-Output ('Compiled diagnostic loop only: ' + $target)
    Write-Output ('Build evidence: ' + $manifestPath)
}
catch {
    $manifest['Status'] = 'compile_failed'
    $manifest['Error'] = $_.Exception.Message
    $manifest['CompletedUtc'] = [DateTime]::UtcNow.ToString('o')
    throw
}
finally {
    $manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}
