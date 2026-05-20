# Builds SolidLinq.Quantower.Algo and copies DLLs into Quantower's Strategies folder.
# Usage:
#   $env:QUANTOWER_ALGO_SDK = "D:\AMP Quantower\TradingPlatform\v1.145.3\bin"
#   .\scripts\Deploy-QuantowerAlgo.ps1
# Optional:
#   -QuantowerStrategiesDir "C:\Users\you\AppData\Local\Quantower\...\Strategies"

param(
    [string]$Configuration = "Release",
    [string]$QuantowerStrategiesDir = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$algoProj = Join-Path $repoRoot "src\SolidLinq.Quantower.Algo\SolidLinq.Quantower.Algo.csproj"

if (-not $env:QUANTOWER_ALGO_SDK) {
    Write-Error "Set QUANTOWER_ALGO_SDK to your Quantower bin folder (contains TradingPlatform.BusinessLayer.dll)."
}

Write-Host "Building Algo ($Configuration)..."
dotnet build $algoProj -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$outDir = Join-Path $repoRoot "src\SolidLinq.Quantower.Algo\bin\$Configuration\net8.0"
$bridgeOut = Join-Path $repoRoot "src\SolidLinq.Quantower.Bridge\bin\$Configuration\net8.0"

if ([string]::IsNullOrWhiteSpace($QuantowerStrategiesDir)) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Quantower\Strategies"),
        (Join-Path $env:LOCALAPPDATA "Quantower\Algo\Strategies")
    )
    foreach ($root in (Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA "Quantower") -Directory -ErrorAction SilentlyContinue)) {
        $candidates += (Join-Path $root.FullName "Strategies")
        $candidates += (Join-Path $root.FullName "Algo\Strategies")
    }
    $QuantowerStrategiesDir = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($QuantowerStrategiesDir) -or -not (Test-Path $QuantowerStrategiesDir)) {
    Write-Error "Quantower Strategies folder not found. Pass -QuantowerStrategiesDir explicitly."
}

Write-Host "Copying to: $QuantowerStrategiesDir"
Copy-Item (Join-Path $outDir "SolidLinq.Quantower.Algo.dll") -Destination $QuantowerStrategiesDir -Force
Copy-Item (Join-Path $bridgeOut "SolidLinq.Quantower.Bridge.dll") -Destination $QuantowerStrategiesDir -Force

Write-Host "Done. Restart Quantower (or remove and re-add SolidLinqBridgeStrategy) so settings refresh."
