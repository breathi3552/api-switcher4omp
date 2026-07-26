$ErrorActionPreference = 'Stop'

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    throw "未找到 .NET SDK：$dotnet。请先安装 .NET 8 SDK 8.0.423。"
}

$root = Split-Path $PSScriptRoot
$artifacts = Join-Path $root 'artifacts\publish'

& $dotnet publish (Join-Path $root 'ProviderPriceSwitcher.App\ProviderPriceSwitcher.App.csproj') `
    --configuration Release `
    --no-restore `
    --property:PublishProfile=FrameworkDependent `
    --output (Join-Path $artifacts 'framework-dependent')

& $dotnet publish (Join-Path $root 'ProviderPriceSwitcher.App\ProviderPriceSwitcher.App.csproj') `
    --configuration Release `
    --no-restore `
    --property:PublishProfile=SelfContained `
    --output (Join-Path $artifacts 'self-contained')
