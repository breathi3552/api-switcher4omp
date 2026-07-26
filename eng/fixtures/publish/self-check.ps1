$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$publish = Join-Path $root 'eng/Publish.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('publish-fixture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    function Invoke-PublishFixture([string[]] $Arguments) {
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & powershell.exe -NoProfile -File $publish @Arguments 2>&1 | Out-Null
            return $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previousPreference }
    }
    function Write-Fake([string] $ScriptPath, [string] $LogPath, [bool] $FailFirst, [bool] $WriteArtifact = $true) {
        $body = @"
`$log = '$($LogPath.Replace("'", "''"))'
`$count = 0
if (Test-Path `$log) { `$count = [int](Get-Content `$log) }
`$count++
Set-Content `$log `$count
if ('$FailFirst' -eq 'True' -and `$count -eq 1) { exit 17 }
if ('$WriteArtifact' -eq 'True') {
    `$target = `$args[(`$args.IndexOf('--output') + 1)]
    New-Item -ItemType Directory -Path `$target -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path `$target 'ProviderPriceSwitcher.App.exe'), [byte[]](1,2,3))
}
exit 0
"@
        Set-Content -Path $ScriptPath -Value $body -Encoding UTF8
    }
    $fake = Join-Path $temp 'fake-dotnet.ps1'
    $log = Join-Path $temp 'calls.txt'
    Write-Fake $fake $log $true
    $failedCode = Invoke-PublishFixture @('-SelfCheck','-DotnetPath',$fake,'-OutputRoot',(Join-Path $temp 'failed'))
    if ($failedCode -eq 0) { throw 'first failure unexpectedly passed' }
    if ([int](Get-Content $log) -ne 1) { throw 'second publish was invoked after first failure' }
    if (Test-Path (Join-Path $temp 'failed/self-contained/ProviderPriceSwitcher.App.exe')) { throw 'second evidence exists after first failure' }
    $stale = Join-Path $temp 'stale'; New-Item -ItemType Directory -Path (Join-Path $stale 'framework-dependent') -Force | Out-Null
    Set-Content (Join-Path $stale 'framework-dependent/ProviderPriceSwitcher.App.exe') 'old'
    Remove-Item $log -Force
    Write-Fake $fake $log $false $false
    $staleCode = Invoke-PublishFixture @('-SelfCheck','-DotnetPath',$fake,'-OutputRoot',$stale)
    if ($staleCode -eq 0) { throw 'stale output failure unexpectedly passed' }

    Remove-Item $log -Force
    Write-Fake $fake $log $false
    $successCode = Invoke-PublishFixture @('-SelfCheck','-DotnetPath',$fake,'-OutputRoot',(Join-Path $temp 'success'))
    if ($successCode -ne 0) { throw 'two-stage success failed' }
    if ([int](Get-Content $log) -ne 2) { throw 'expected two publish calls' }
    foreach ($name in @('framework-dependent','self-contained')) {
        $exe = Join-Path $temp "success/$name/ProviderPriceSwitcher.App.exe"
        if (-not (Test-Path $exe) -or (Get-Item $exe).Length -le 0) { throw "missing evidence: $name" }
    }
    Write-Host 'publish fixture self-check passed'
} finally { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
