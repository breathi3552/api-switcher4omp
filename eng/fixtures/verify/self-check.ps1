$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$verify = Join-Path $root 'eng/Verify.ps1'
$manifestPath = Join-Path $root 'eng/verify-manifest.json'
$matrixPath = Join-Path $root 'eng/verify-dependency-matrix.json'
$base = Get-Content $manifestPath -Raw | ConvertFrom-Json
$temp = Join-Path ([IO.Path]::GetTempPath()) ('verify-fixture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    function Expect-Fail([string] $Name, [scriptblock] $Action) {
        try { & $Action; throw "fixture unexpectedly passed: $Name" } catch { if ($_.Exception.Message -like "fixture unexpectedly passed:*") { throw }; Write-Host "PASS $Name" }
    }
    $valid = Join-Path $temp 'valid.json'; $base | ConvertTo-Json -Depth 10 | Set-Content $valid
    & $verify -Impact Document -ManifestPath $valid -DependencyMatrixPath $matrixPath
    if ($LASTEXITCODE -ne 0) { throw 'baseline fixture failed' }
    $cases = @(
        @{ Name = 'duplicate-runner'; Mutate = { $working.runners += $working.runners[0] } },
        @{ Name = 'disabled-runner'; Mutate = { $working.runners[0].disabled = $true } },
        @{ Name = 'order-drift'; Mutate = { $x = $working.runners[0]; $working.runners[0] = $working.runners[1]; $working.runners[1] = $x } },
        @{ Name = 'missing-runner'; Mutate = { $working.runners = @($working.runners | Select-Object -Skip 1) } },
        @{ Name = 'missing-project'; Mutate = { $working.runners[0].project = 'ProviderPriceSwitcher.DoesNotExist.Tests/ProviderPriceSwitcher.DoesNotExist.Tests.csproj' } },
        @{ Name = 'path-escape'; Mutate = { $working.runners[0].project = '../outside.csproj' } },
        @{ Name = 'illegal-impact-field'; Mutate = { $working.runners[0].minimumImpact = 'Bogus' } },
        @{ Name = 'illegal-reference'; Mutate = { $working.runners[0].project = 'ProviderPriceSwitcher.Core/ProviderPriceSwitcher.Core.csproj' } }
    )
    foreach ($case in $cases) {
        $working = Get-Content $manifestPath -Raw | ConvertFrom-Json
        & $case.Mutate
        $path = Join-Path $temp ($case.Name + '.json'); $working | ConvertTo-Json -Depth 10 | Set-Content $path
        Expect-Fail $case.Name { & $verify -Impact Document -ManifestPath $path -DependencyMatrixPath $matrixPath }
    }
    Expect-Fail 'invalid-impact' { & $verify -Impact Invalid -ManifestPath $valid -DependencyMatrixPath $matrixPath }
    Expect-Fail 'document-runner' { & $verify -Impact Document -Runner Core -ManifestPath $valid -DependencyMatrixPath $matrixPath }
    Expect-Fail 'internal-no-runner' { & $verify -Impact Internal -ManifestPath $valid -DependencyMatrixPath $matrixPath }
    Expect-Fail 'crosslayer-runner' { & $verify -Impact CrossLayer -Runner Core -AllRunners -ManifestPath $valid -DependencyMatrixPath $matrixPath }
    $matrixWorking = Get-Content $matrixPath -Raw | ConvertFrom-Json
    $matrixWorking.projects.'ProviderPriceSwitcher.Core' = @('ProviderPriceSwitcher.Application')
    $drift = Join-Path $temp 'matrix-drift.json'; $matrixWorking | ConvertTo-Json -Depth 10 | Set-Content $drift
    Expect-Fail 'solution-matrix-drift' { & $verify -Impact Document -ManifestPath $valid -DependencyMatrixPath $drift }
    Write-Host 'verify fixture self-check passed'
} finally { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
