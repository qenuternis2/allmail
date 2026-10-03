#requires -Version 7
<#
  Repeatable Windows build and fixture-free test run (spec §11).
  ./build.ps1            restore, build Core and Experimental proxy flavors, run core tests
  ./build.ps1 -Publish   additionally publish self-contained win-x64 builds to artifacts/
#>
param([switch]$Publish, [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
# Fail the script (and CI) when dotnet build/test returns a non-zero exit code.
$PSNativeCommandUseErrorActionPreference = $true
Set-Location $PSScriptRoot

dotnet --info | Select-String -Pattern 'Version|OS Name|RID' | ForEach-Object { $_.Line.Trim() }
dotnet restore ProtonProfiles.slnx
dotnet build ProtonProfiles.slnx -c $Configuration --no-restore
dotnet build src/ProtonProfiles.App -c $Configuration -p:ExperimentalProxy=true -o artifacts/build-experimental
dotnet test tests/ProtonProfiles.Core.Tests -c $Configuration --no-build --logger "trx;LogFileName=core-tests.trx" --results-directory artifacts/test-results
node --test tests/webrtc-guard.test.mjs

if ($Publish) {
  dotnet publish src/ProtonProfiles.App -c $Configuration -r win-x64 --self-contained true -o artifacts/publish-core
  dotnet publish src/ProtonProfiles.App -c $Configuration -r win-x64 --self-contained true -p:ExperimentalProxy=true -o artifacts/publish-experimental
  Write-Host 'Published: artifacts/publish-core (Core build), artifacts/publish-experimental (EXPERIMENTAL proxy build). Installer signing: outstanding.'
}
