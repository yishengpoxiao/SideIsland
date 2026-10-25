#requires -Version 5.1
[CmdletBinding()]
param([string]$Repository = (Split-Path -Parent $PSScriptRoot))

# Reads only the Git index. Never prints matched secret text or reads an account store.
$ErrorActionPreference = 'Stop'
$git = @(Get-Command git -CommandType Application -ErrorAction Stop)[0].Source
$repo = [IO.Path]::GetFullPath($Repository)
function Read-Git([string[]]$Arguments) {
    $output = @(& $git -C $repo @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'Git index command failed. Run this tool inside an initialized repository with staged files.' }
    return [string]::Join("`n", @($output | ForEach-Object { [string]$_ }))
}
$null = Read-Git @('rev-parse', '--show-toplevel')
$paths = @((Read-Git @('-c', 'core.quotepath=false', 'diff', '--cached', '--name-only', '--diff-filter=ACMR', '-z')).Split([char]0) | Where-Object { $_.Length -gt 0 })
$findings = New-Object 'System.Collections.Generic.List[object]'
function Finding([string]$Path, [string]$Code, [int]$Line = 0) {
    $findings.Add([pscustomobject]@{ File = $Path; Line = $Line; Code = $Code })
}
$textExtensions = @('.cs', '.c', '.h', '.ps1', '.cmd', '.bat', '.csproj', '.props', '.targets', '.sln', '.slnx', '.md', '.txt', '.xml', '.config', '.manifest', '.plist', '.desktop', '.yml', '.yaml', '.toml', '.json', '.gitignore', '.gitattributes', '.editorconfig')
$rootFiles = '^(?:README(?:\.[A-Za-z0-9-]+)?\.md|VERSION|CONTRIBUTING\.md|SECURITY\.md|CHANGELOG\.md|CODE_OF_CONDUCT\.md|LICENSE(?:\.[A-Za-z]+)?|NOTICE(?:\.[A-Za-z]+)?|THIRD-PARTY-NOTICES\.txt|\.gitignore|\.gitattributes|\.editorconfig|build(?:-release)?\.ps1|install-notifications\.(?:ps1|cmd)|启动通知版\.cmd|app\.manifest|CodexIsland\.exe\.config|NuGet\.Config|global\.json)$'
$secretPatterns = [ordered]@{
    'private-key-material' = '-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----\s*[A-Za-z0-9+/=\r\n]{32,}'
    'github-token' = '(?<![A-Za-z0-9_-])(?:ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})'
    'api-key' = '(?<![A-Za-z0-9_-])sk-[A-Za-z0-9_-]{20,}'
    'aws-access-id' = '(?<![A-Za-z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Za-z0-9])'
    'literal-jwt' = '(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{16,}'
    'local-user-home' = '(?i)(?:[A-Z]:\\Users\\(?!Public(?:\\|$)|Default(?:\\|$))[^\\\s"''<>]+|/(?:Users|home)/[^/\s"''<>]+)'
    'developer-workspace-path' = '(?i)D:\\data\\tools\\codex-island'
}
foreach ($path in $paths) {
    if ($path -match '[\r\n\x00-\x1f]') { Finding '<unusual-path>' 'control-character-path'; continue }
    if ($path -notmatch '^(?:src|tests|tools|assets|docs|packaging|cross-platform|\.github)/' -and $path -notmatch $rootFiles) { Finding $path 'outside-source-allowlist'; continue }
    if ($path -match '(?i)(?:^|/)(?:\.runtime|artifacts|release|dist|build|bin|obj|out|node_modules|\.vs|\.idea|\.git)(?:/|$)' -or $path -match '^packaging/app/') { Finding $path 'generated-or-private-directory'; continue }
    if ($path -match '(?i)(?:\.(?:exe|dll|pdb|zip|7z|gz|nupkg|msix|appx|dmp|mdmp|hdmp|etl|pcap|log|jsonl|pfx|p12|pem|key|snk|cer|crt|mp3|m4a|flac|wav|mp4|mov|lnk)|\.env(?:\.[^/]+)?)$') { Finding $path 'binary-secret-or-capture-file'; continue }
    if ($path -match '(?i)(?:^|/)(?:auth|credentials?|preferences|settings|clipboard|notifications?|history|media-session|lyrics|task-activity)[^/]*\.json$') { Finding $path 'local-user-state'; continue }
    $indexEntry = Read-Git @('ls-files', '--stage', '-z', '--', $path)
    if ($indexEntry -notmatch '^100(?:644|755) ') { Finding $path 'nonregular-index-entry'; continue }
    $isAsset = $path -match '^(?:assets/[^/]+\.(?:ico|icns|png)|packaging/NotificationAssets/(?:Square150x150Logo|Square44x44Logo|StoreLogo)\.png)$'
    if ($isAsset) { continue } # Reviewed application icons only, no captured screenshots.
    $extension = [IO.Path]::GetExtension($path).ToLowerInvariant()
    if ($extension -notin $textExtensions -and $path -notmatch '(?:^|/)(?:VERSION|LICENSE|NOTICE|\.gitignore|\.gitattributes|\.editorconfig)$') { Finding $path 'unreviewed-file-type'; continue }
    $blobSize = Read-Git @('cat-file', '-s', (':' + $path))
    if ([long]$blobSize -gt 2MB) { Finding $path 'oversized-source-file'; continue }
    $content = Read-Git @('show', (':' + $path))
    foreach ($rule in $secretPatterns.GetEnumerator()) {
        foreach ($match in [regex]::Matches($content, $rule.Value)) {
            $line = 1 + ([regex]::Matches($content.Substring(0, $match.Index), "`n")).Count
            Finding $path $rule.Key $line
        }
    }
}
if ($findings.Count -gt 0) {
    $findings | Sort-Object File, Line, Code | Format-Table -AutoSize
    Write-Output ('FAIL: ' + $findings.Count + ' staged-source findings. Values are intentionally suppressed. Review or unstage the named files; no files were changed.')
    exit 1
}
Write-Output ('PASS: ' + $paths.Count + ' staged source files checked. No matched secret values were printed. Pattern checks do not replace review of public documentation and image assets.')
