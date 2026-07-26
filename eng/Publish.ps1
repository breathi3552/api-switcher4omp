[CmdletBinding()]
param(
    [string] $DotnetPath,
    [string] $OutputRoot,
    [switch] $SelfCheck
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$defaultDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$defaultOutputRoot = Join-Path $root 'artifacts\publish'
if (-not $SelfCheck -and ($PSBoundParameters.ContainsKey('DotnetPath') -or $PSBoundParameters.ContainsKey('OutputRoot'))) { throw 'Custom publish paths require explicit -SelfCheck.' }
if ([string]::IsNullOrWhiteSpace($DotnetPath)) { $DotnetPath = $defaultDotnet }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = $defaultOutputRoot }
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { throw "未找到 .NET SDK：$DotnetPath。请先安装 .NET 8 SDK 8.0.423。" }
$project = Join-Path $root 'ProviderPriceSwitcher.App\ProviderPriceSwitcher.App.csproj'
function Invoke-PublishStage {
    param([string] $Name, [string] $Profile, [string] $Target)
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    $exe = Join-Path $Target 'ProviderPriceSwitcher.App.exe'
    if (Test-Path -LiteralPath $exe) { Remove-Item -LiteralPath $exe -Force }
    $startedAt = [DateTime]::UtcNow
    if ($SelfCheck) { & (Join-Path $PSHOME 'powershell.exe') -NoProfile -File $DotnetPath publish $project --configuration Release --no-restore --property:PublishProfile=$Profile --output $Target }
    else { & $DotnetPath publish $project --configuration Release --no-restore --property:PublishProfile=$Profile --output $Target }
    $exitCode = $LASTEXITCODE
    Write-Host ("publish stage={0} target={1} exitCode={2}" -f $Name, $Target, $exitCode)
    if ($exitCode -ne 0) { throw "publish stage '$Name' failed with exit code $exitCode." }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "publish stage '$Name' missing executable evidence." }
    $item = Get-Item -LiteralPath $exe
    if ($item.Length -le 0 -or $item.LastWriteTimeUtc -lt $startedAt) { throw "publish stage '$Name' produced stale or empty executable evidence." }
    Write-Host ("publish evidence stage={0} exe={1} bytes={2}" -f $Name, $exe, $item.Length)
}
try {
    Invoke-PublishStage -Name 'framework-dependent' -Profile 'FrameworkDependent' -Target (Join-Path $OutputRoot 'framework-dependent')
    Invoke-PublishStage -Name 'self-contained' -Profile 'SelfContained' -Target (Join-Path $OutputRoot 'self-contained')
    exit 0
} catch { Write-Error $_; exit 1 }
