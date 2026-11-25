[CmdletBinding()]
param(
    [switch]$Smoke,
    [ValidateSet('Smoke', 'Standard', 'Large')]
    [string]$Scale = 'Standard',
    [switch]$VerifyDeterminism,
    [switch]$RequireAllCompetitors,
    [string]$OutputDir = '',
    [string]$GenerateCorpus = '',
    [string]$Categories = '',
    [string]$Tools = ''
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'benchmarks\UltraCompress.Benchmarks\UltraCompress.Benchmarks.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 8 SDK ('dotnet') is required on PATH."
}

$sevenZipCandidate = Join-Path ${env:ProgramFiles} '7-Zip\7z.exe'
if ((-not $env:ULTRACOMPRESS_7ZA_PATH) -and (Test-Path $sevenZipCandidate)) {
    $env:ULTRACOMPRESS_7ZA_PATH = $sevenZipCandidate
}

Write-Host "[UltraCompress Benchmarks] Building Release harness..."
dotnet build $Project -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$benchArgs = @()
if ($Smoke) {
    $benchArgs += '--smoke'
} else {
    $benchArgs += @('--scale', $Scale)
}

if ($VerifyDeterminism) { $benchArgs += '--verify-determinism' }
if ($RequireAllCompetitors) { $benchArgs += '--require-all-competitors' }
if ($OutputDir) { $benchArgs += @('--output-dir', $OutputDir) }
if ($GenerateCorpus) { $benchArgs += @('--generate-corpus', $GenerateCorpus) }
if ($Categories) { $benchArgs += @('--categories', $Categories) }
if ($Tools) { $benchArgs += @('--tools', $Tools) }

Write-Host "[UltraCompress Benchmarks] Executing benchmark suite..."
dotnet run -c Release --no-build --project $Project -- @benchArgs
exit $LASTEXITCODE
