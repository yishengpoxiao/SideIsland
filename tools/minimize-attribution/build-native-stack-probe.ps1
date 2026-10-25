param([string]$OutputDirectory='.runtime\native-minimize-stack-probe-v1',[switch]$SelfCheck)
$ErrorActionPreference='Stop'
$projectPath=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory=Join-Path $projectPath $OutputDirectory}
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($projectPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be within project.'}
if(Test-Path -LiteralPath $out){throw 'Fresh build output required.'}
[IO.Directory]::CreateDirectory($out)|Out-Null
$deps=Join-Path $projectPath '.runtime\native-probe-zig-deps'
$zig=Join-Path $deps 'toolchain\zig-x86_64-windows-0.17.0\zig.exe'
$download=Get-Content -LiteralPath (Join-Path $deps 'download-verification.json') -Raw|ConvertFrom-Json
if(-not $download.Verified -or (Get-FileHash -LiteralPath $download.Archive -Algorithm SHA256).Hash -ne $download.OfficialSHA256){throw 'Verified official portable Zig required.'}
$oldGlobal=[Environment]::GetEnvironmentVariable('ZIG_GLOBAL_CACHE_DIR')
$oldLocal=[Environment]::GetEnvironmentVariable('ZIG_LOCAL_CACHE_DIR')
try{
    [Environment]::SetEnvironmentVariable('ZIG_GLOBAL_CACHE_DIR',(Join-Path $deps 'global-cache'))
    [Environment]::SetEnvironmentVariable('ZIG_LOCAL_CACHE_DIR',(Join-Path $deps 'local-cache'))
    foreach($name in @('NativeMinimizeStackProbe','NativeMinimizeStackControl')){
        $nativeSource=Join-Path $PSScriptRoot ($name+'.c')
        & $zig cc -target x86_64-windows-gnu -shared -O1 -g -fno-omit-frame-pointer -fno-optimize-sibling-calls $nativeSource -o (Join-Path $out ($name+'.dll')) -luser32 2>&1|Tee-Object -FilePath (Join-Path $out ($name+'.compile.log'))
        if($LASTEXITCODE -ne 0){throw ('Native compilation failed: '+$name)}
    }
}finally{
    [Environment]::SetEnvironmentVariable('ZIG_GLOBAL_CACHE_DIR',$oldGlobal)
    [Environment]::SetEnvironmentVariable('ZIG_LOCAL_CACHE_DIR',$oldLocal)
}
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$references=@('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Xaml.dll')|ForEach-Object{Join-Path $framework $_}
$references+=@('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll')|ForEach-Object{Join-Path (Join-Path $framework 'WPF') $_}
$sources=@(Join-Path $PSScriptRoot 'NativeMinimizeStackProbe.cs')+@(Join-Path $PSScriptRoot 'NativeMinimizeStackCalibration.cs')
$target=Join-Path $out 'NativeStackCalibration.exe'
& $compiler (@('/nologo','/langversion:5','/platform:x64','/target:exe','/main:CodexIsland.NativeMinimizeStackCalibration',('/out:'+$target))+@($references|ForEach-Object{'/reference:'+$_})+$sources) 2>&1|Tee-Object -FilePath (Join-Path $out 'managed.compile.log')
if($LASTEXITCODE -ne 0){throw 'Managed native-probe bridge compilation failed.'}
$inputs=@(foreach($path in @($sources)+@(Join-Path $PSScriptRoot 'NativeMinimizeStackProbe.c')+@(Join-Path $PSScriptRoot 'NativeMinimizeStackControl.c')){[ordered]@{Path=$path;SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}})
$outputs=@(Get-ChildItem -LiteralPath $out -File|Where-Object{$_.Extension -in @('.dll','.exe')}|ForEach-Object{[ordered]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
[ordered]@{CompiledUtc=[DateTime]::UtcNow.ToString('o');Zig=$zig;OfficialZigArchiveSHA256=$download.OfficialSHA256;Inputs=$inputs;Outputs=$outputs;UiExecuted=$false;HookInstalled=$false;SystemConfigurationChanged=$false}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $out 'build-manifest.json') -Encoding UTF8
if($SelfCheck){& $target --self-check 2>&1|Tee-Object -FilePath (Join-Path $out 'self-check.log');if($LASTEXITCODE -ne 0){throw 'Non-UI native export self-check failed.'}}
Write-Output ('Native probe compiled; no UI/hook executed: '+$out)
