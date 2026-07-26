 [CmdletBinding()]
 param(
     [Parameter(Mandatory = $true)]
     [ValidateSet('Document','Internal','Behavior','CrossLayer')]
     [string] $Impact,
     [string[]] $Runner,
     [switch] $AllRunners,
     [string] $ManifestPath,
     [string] $DependencyMatrixPath,
     [switch] $SkipCommands
 )

 $ErrorActionPreference = 'Stop'
 $root = (Resolve-Path $PSScriptRoot).Path | Split-Path -Parent
 if (-not $ManifestPath) { $ManifestPath = Join-Path $PSScriptRoot 'verify-manifest.json' }
 if (-not $DependencyMatrixPath) { $DependencyMatrixPath = Join-Path $PSScriptRoot 'verify-dependency-matrix.json' }
 $solution = Join-Path $root 'ProviderPriceSwitcher.sln'
 $stages = New-Object 'System.Collections.Generic.List[string]'

 function Fail([string] $Message) { throw "VERIFY FAILED: $Message" }
 function Stage([string] $Name) { [void]$stages.Add($Name); Write-Host "[verify] $Name" }
 function Invoke-Native([string] $File, [object[]] $Arguments) {
     & $File @Arguments
     $code = $LASTEXITCODE
     if ($code -ne 0) { Fail "command failed ($code): $File $($Arguments -join ' ')" }
 }
 function Read-Json([string] $Path) {
     if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Fail "missing JSON: $Path" }
     try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) } catch { Fail "invalid JSON: $Path" }
 }
 function Resolve-RepoPath([string] $Relative, [string] $Purpose) {
     if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)) { Fail "$Purpose path must be relative" }
     $full = [IO.Path]::GetFullPath((Join-Path $root $Relative))
     $rootFull = ([IO.Path]::GetFullPath($root)).TrimEnd('\') + '\'
     if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { Fail "$Purpose path escapes repository" }
     return $full
 }
 function Assert-Manifest($Manifest) {
     $expected = @('Core','Adapters','Application','Infrastructure','OmpConfig','OmpProcess','Refresh','App')
     if ($null -eq $Manifest.runners -or @($Manifest.runners).Count -ne 8) { Fail 'manifest must contain exactly eight runners' }
     $actual = @($Manifest.runners | ForEach-Object { $_.name })
     if ((@($actual | Sort-Object -Unique)).Count -ne 8) { Fail 'manifest contains duplicate runner names' }
     for ($i = 0; $i -lt 8; $i++) { if ($actual[$i] -ne $expected[$i]) { Fail "runner order mismatch at ${i}: expected $($expected[$i]), got $($actual[$i])" } }
     $paths = @()
     foreach ($r in $Manifest.runners) {
         if ([string]::IsNullOrWhiteSpace([string]$r.name) -or [string]::IsNullOrWhiteSpace([string]$r.project)) { Fail 'runner name/project is required' }
         if ([string]::IsNullOrWhiteSpace([string]$r.layer) -or $null -eq $r.sideEffects) { Fail "runner required fields missing: $($r.name)" }
         if ($r.layer -notin @('Core','Adapters','Application','Infrastructure','App')) { Fail "illegal runner layer: $($r.name)" }
         if ($r.disabled -eq $true) { Fail "runner disabled: $($r.name)" }
         if ($r.minimumImpact -notin @('Internal','Behavior','CrossLayer')) { Fail "illegal minimumImpact: $($r.name)" }
         $projectPath = Resolve-RepoPath ([string]$r.project) 'runner'
         if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { Fail "runner project missing: $($r.name)" }
         if ($paths -contains $projectPath) { Fail 'duplicate runner project path' }; $paths += $projectPath
         $projectName = [IO.Path]::GetFileNameWithoutExtension($projectPath)
         if ($projectName -ne "ProviderPriceSwitcher.$($r.name).Tests") { Fail "runner project/name mismatch: $($r.name)" }
     }
     $solutionText = Get-Content -LiteralPath $solution -Raw
     $solutionSet = @([regex]::Matches($solutionText, 'Project\([^\r\n]+\) = "(ProviderPriceSwitcher\.[^"]+\.Tests)", "([^"]+\.csproj)"') | ForEach-Object { (Join-Path $root $_.Groups[2].Value).ToLowerInvariant() } | Sort-Object -Unique)
     $manifestSet = @($paths | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique)
     if ((Compare-Object $solutionSet $manifestSet)) { Fail 'manifest and solution runner project sets differ' }
     return $actual
 }
 function Assert-Dependencies($Matrix) {
     if ($null -eq $Matrix.projects) { Fail 'dependency matrix has no projects' }
     foreach ($entry in $Matrix.projects.psobject.Properties) {
         if ($entry.Name -notmatch '^ProviderPriceSwitcher\.(Core|Application|Adapters|Infrastructure|App)$') { Fail "illegal matrix project: $($entry.Name)" }
         $shortName = $entry.Name.Substring('ProviderPriceSwitcher.'.Length)
         $path = Resolve-RepoPath "ProviderPriceSwitcher.$shortName/ProviderPriceSwitcher.$shortName.csproj" 'matrix'
         if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "matrix project missing: $($entry.Name)" }
         [xml]$xml = Get-Content -LiteralPath $path -Raw
         $actual = @($xml.Project.ItemGroup.ProjectReference.Include | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) }) | Where-Object { $_ -like 'ProviderPriceSwitcher.*' -and $_ -notlike '*.Tests' } | Sort-Object -Unique
         $allowed = @($entry.Value) | Sort-Object -Unique
         if ($actual.Count -ne $allowed.Count -or @($actual | Where-Object { $_ -notin $allowed }).Count -gt 0 -or @($allowed | Where-Object { $_ -notin $actual }).Count -gt 0) { Fail "dependency mismatch for $($entry.Name)" }
     }
 }
 try {
     if ($SkipCommands) { Fail 'SkipCommands is not supported' }
     if ($Impact -eq 'Document' -and ($Runner -or $AllRunners)) { Fail 'Document impact rejects Runner and AllRunners' }
     if ($Runner) { $Runner = @($Runner | ForEach-Object { $_ -split ',' } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) }
     if ($Impact -in @('Internal','Behavior') -and ($AllRunners -or $null -eq $Runner -or @($Runner).Count -eq 0)) { Fail "$Impact impact requires at least one Runner and rejects AllRunners" }
     if ($Impact -eq 'CrossLayer' -and (-not $AllRunners -or $Runner)) { Fail 'CrossLayer requires AllRunners and rejects Runner' }
     Stage 'static manifest and dependency checks'
     $Manifest = Read-Json $ManifestPath; $names = Assert-Manifest $Manifest
     $Matrix = Read-Json $DependencyMatrixPath; Assert-Dependencies $Matrix
     if ($Impact -eq 'Document') { Write-Host '[verify] Document impact: static checks only; SDK/build/format/runners skipped'; exit 0 }
     if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) { Fail 'solution missing' }
     Stage 'SDK environment'
     $dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
     if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { Fail "required SDK executable missing: $dotnet" }
     $version = (& $dotnet --version 2>&1 | Out-String).Trim(); $code = $LASTEXITCODE
     if ($code -ne 0) { Fail "SDK --version failed ($code)" }; if ($version -ne '8.0.423') { Fail "SDK version must be 8.0.423, got $version" }
     if ($AllRunners) { $selected = @($names) } else { $selected = @($Runner) }
     foreach ($n in $selected) { if ($n -notin $names) { Fail "Runner must name manifest entries: $n" } }
     Stage 'solution build'; Invoke-Native $dotnet @('build', $solution)
     if ($Impact -eq 'CrossLayer') { Stage 'format verify'; Invoke-Native $dotnet @('format', $solution, '--verify-no-changes', '--no-restore') }
     Stage 'serial runner execution'
     foreach ($n in $selected) { $r = @($Manifest.runners | Where-Object { $_.name -eq $n })[0]; $project = Resolve-RepoPath $r.project 'runner'; Invoke-Native $dotnet @('run','--project',$project,'--no-build') }
     Write-Host "[verify] completed: $($stages -join ' -> ')"; exit 0
 } catch { Write-Error $_; exit 1 }
