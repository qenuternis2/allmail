#requires -Version 7
param(
  [string]$PublishDirectory = 'artifacts/publish-experimental',
  [string]$OutputDirectory = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-Location (Split-Path $PSScriptRoot)
[xml]$props = Get-Content Directory.Build.props
$version = $props.Project.PropertyGroup.Version
$publish = (Resolve-Path $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory $output -Force | Out-Null
$toolRoot = Join-Path ([IO.Path]::GetTempPath()) 'securebrowser-inno-7.1.0'
$iscc = Join-Path $toolRoot 'ISCC.exe'
if (!(Test-Path $iscc)) {
  $download = Join-Path ([IO.Path]::GetTempPath()) 'securebrowser-innosetup-7.1.0-x64.exe'
  Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -OutFile $download
  $expected = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
  if ((Get-FileHash $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Inno Setup installer checksum mismatch.' }
  if ((Get-AuthenticodeSignature $download).Status -ne 'Valid') { throw 'Inno Setup installer signature is not valid.' }
  $install = Start-Process $download -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="' + $toolRoot + '"')) -Wait -PassThru
  if ($install.ExitCode -ne 0) { throw "Inno Setup install failed: $($install.ExitCode)" }
}
& $iscc "/DAppVersion=$version" "/DPublishDir=$publish" "/DOutputDir=$output" installer/SecureBrowser.iss
$installer = Join-Path $output "SecureBrowser-$version-setup-win-x64.exe"
if (!(Test-Path $installer)) { throw 'Installer was not produced.' }
& "$PSScriptRoot/Sign-Release.ps1" -Path $installer
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content "$installer.sha256" -Encoding ascii
Write-Host "Installer: $installer"
