$ErrorActionPreference = 'Stop'
$qaProject = Join-Path $PSScriptRoot 'Probe.csproj'
$qaArtifacts = Join-Path ([System.IO.Path]::GetTempPath()) 'XinDong-QA-20261028'
dotnet run --project $qaProject -c Release --artifacts-path $qaArtifacts -- $PSScriptRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$qaResult = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'results.json') -Raw | ConvertFrom-Json
if ($qaResult.Failed -gt 0) { exit 1 }
