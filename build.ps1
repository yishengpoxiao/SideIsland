param([switch]$Test, [switch]$CompileTests)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION')).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain a three-part release version.' }
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler is required.' }
$output = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$metadata = Join-Path $output 'AssemblyVersion.generated.cs'
[IO.File]::WriteAllText($metadata, ('using System.Reflection;' + [Environment]::NewLine + '[assembly: AssemblyVersion("' + $version + '.0")]' + [Environment]::NewLine + '[assembly: AssemblyFileVersion("' + $version + '.0")]' + [Environment]::NewLine + '[assembly: AssemblyInformationalVersion("' + $version + '")]'), (New-Object Text.UTF8Encoding($false)))
$icon = Join-Path $projectRoot 'assets\island.ico'
if (-not (Test-Path -LiteralPath $icon)) {
    $iconBuilder = Join-Path $output 'IconBuilder.exe'
    & $compiler /nologo /utf8output /reference:System.Drawing.dll ('/out:' + $iconBuilder) (Join-Path $projectRoot 'tools\IconBuilder.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Icon build failed.' }
    & $iconBuilder $icon
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
    Remove-Item -LiteralPath $iconBuilder
}
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $framework 'WPF') $_) }
$references += '/reference:' + (Join-Path $framework 'System.Runtime.dll')
$references += @('Windows.UI.winmd', 'Windows.Foundation.winmd', 'Windows.ApplicationModel.winmd', 'Windows.Media.winmd', 'Windows.Storage.winmd', 'Windows.Graphics.winmd', 'Windows.Globalization.winmd') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $env:WINDIR 'System32\WinMetadata') $_) }
$source = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -File -Recurse | Sort-Object FullName | ForEach-Object FullName) + @($metadata)
if (-not $CompileTests -or $Test) {
    $arguments = @('/nologo', '/utf8output', '/langversion:5', '/optimize+', '/platform:anycpu', '/target:winexe', ('/win32icon:' + $icon), ('/out:' + (Join-Path $output 'CodexIsland.exe')), ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest'))) + $references + $source
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'CodexIsland.exe.config') -Destination $output -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt') -Destination $output -Force
    Write-Output ('Built: ' + (Join-Path $output 'CodexIsland.exe'))
}
if ($Test -or $CompileTests) {
    $testOutput = Join-Path $projectRoot '.runtime\tests'
    New-Item -ItemType Directory -Force -Path $testOutput | Out-Null
    $testSource = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' -File -Recurse | Sort-Object FullName | ForEach-Object FullName
    $testArguments = @('/nologo', '/utf8output', '/langversion:5', '/target:exe', '/main:CodexIsland.NativeTests', ('/out:' + (Join-Path $testOutput 'NativeTests.exe'))) + $references + $source + $testSource
    & $compiler $testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    Write-Output ('Compiled tests: ' + (Join-Path $testOutput 'NativeTests.exe'))
    if ($Test) {
        & (Join-Path $testOutput 'NativeTests.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
}
