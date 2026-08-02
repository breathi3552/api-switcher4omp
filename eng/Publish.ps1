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
function Assert-SidecarAsset([string] $Target) {
    $asset = Join-Path $root 'ProviderPriceSwitcher.App\Assets\Bifrost\bifrost-sidecar.exe'
    $manifestPath = Join-Path $root 'ProviderPriceSwitcher.App\Assets\Bifrost\bifrost-sidecar.manifest.json'
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { throw "sidecar asset missing: $asset" }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "sidecar manifest missing: $manifestPath" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $actual = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne ([string]$manifest.sha256).ToLowerInvariant()) { throw "sidecar hash mismatch: expected $($manifest.sha256), actual $actual" }
    Copy-Item -LiteralPath $asset -Destination (Join-Path $Target 'bifrost-sidecar.exe') -Force
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $Target 'bifrost-sidecar.manifest.json') -Force
}
function Invoke-PublishStage {
    param([string] $Name, [string] $Profile, [string] $Target)
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    $exe = Join-Path $Target 'ProviderPriceSwitcher.App.exe'
    if (Test-Path -LiteralPath $exe) { Remove-Item -LiteralPath $exe -Force }
    if ($SelfCheck) { & $DotnetPath publish $project --configuration Release --no-restore --property:PublishProfile=$Profile --output $Target }
    else { & $DotnetPath publish $project --configuration Release --no-restore --property:PublishProfile=$Profile --output $Target }
    $exitCode = $LASTEXITCODE
    Write-Host ("publish stage={0} target={1} exitCode={2}" -f $Name, $Target, $exitCode)
    if ($exitCode -ne 0) { throw "publish stage '$Name' failed with exit code $exitCode." }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "publish stage '$Name' missing executable evidence." }
    $item = Get-Item -LiteralPath $exe
    $sourceSidecar = Join-Path $root 'ProviderPriceSwitcher.App\Assets\Bifrost\bifrost-sidecar.exe'
    $sourceManifest = Join-Path $root 'ProviderPriceSwitcher.App\Assets\Bifrost\bifrost-sidecar.manifest.json'
    Copy-Item -LiteralPath $sourceSidecar -Destination (Join-Path $Target 'bifrost-sidecar.exe') -Force
    Copy-Item -LiteralPath $sourceManifest -Destination (Join-Path $Target 'bifrost-sidecar.manifest.json') -Force
    $publishedSidecar = Join-Path $Target 'bifrost-sidecar.exe'
    $publishedManifest = Join-Path $Target 'bifrost-sidecar.manifest.json'
    $publishedHash = (Get-FileHash -LiteralPath $publishedSidecar -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = ([string](Get-Content -LiteralPath $publishedManifest -Raw | ConvertFrom-Json).sha256).ToLowerInvariant()
    if ($publishedHash -ne $expectedHash) { throw "published sidecar hash mismatch" }
    Write-Host ("publish evidence stage={0} exe={1} bytes={2}" -f $Name, $exe, $item.Length)
}
try {
    Invoke-PublishStage -Name 'framework-dependent' -Profile 'FrameworkDependent' -Target (Join-Path $OutputRoot 'framework-dependent')
    Invoke-PublishStage -Name 'self-contained' -Profile 'SelfContained' -Target (Join-Path $OutputRoot 'self-contained')
    exit 0
} catch { Write-Error $_; exit 1 }
