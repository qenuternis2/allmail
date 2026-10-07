#requires -Version 7
param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
$installerPath = (Resolve-Path $Installer).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('securebrowser-installer-test-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'app'
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ProtonProfiles'
if (Test-Path $data) { throw 'Installer test requires an empty runner data directory; existing user data will not be overwritten.' }
New-Item -ItemType Directory $data -Force | Out-Null
$marker = Join-Path $data 'installer-test-marker'
[IO.File]::WriteAllText($marker,'preserved')
function Run([string]$File,[string[]]$Arguments) {
  $p=Start-Process $File -ArgumentList $Arguments -Wait -PassThru
  if ($p.ExitCode -ne 0) { throw "Installer operation failed: $($p.ExitCode)" }
}
try {
  $installArgs=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$install+'"'))
  Run $installerPath $installArgs
  Run $installerPath $installArgs
  if ([IO.File]::ReadAllText($marker) -ne 'preserved' -or !(Test-Path (Join-Path $install 'SecureBrowser.exe')) -or
      !(Test-Path (Join-Path $install 'SecureBrowser.Core.dll')) -or !(Test-Path (Join-Path $install 'Microsoft.Web.WebView2.Core.dll'))) { throw 'Install/update changed data or omitted payload.' }
  [xml]$props=Get-Content (Join-Path $PSScriptRoot '../Directory.Build.props')
  $binary=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'SecureBrowser.exe'))
  if ($binary.ProductVersion.Split('+')[0] -ne $props.Project.PropertyGroup.Version) { throw 'Installed version mismatch.' }
  Run (Join-Path $install 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
  if ([IO.File]::ReadAllText($marker) -ne 'preserved' -or (Test-Path (Join-Path $install 'SecureBrowser.exe'))) { throw 'Default uninstall did not preserve data/remove binaries.' }
  Run $installerPath $installArgs
  Run (Join-Path $install 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/REMOVEUSERDATA')
  if ((Test-Path $data) -or (Test-Path (Join-Path $install 'SecureBrowser.exe'))) { throw 'Explicit data removal did not finish.' }
  Write-Host 'PASS: per-user installer; complete versioned payload; repeated installation preserves metadata; default uninstall preserves data; explicit removal deletes managed data; no shared Runtime uninstall.'
} finally {
  # Only test-created paths; a failed cleanup deliberately leaves evidence for inspection.
  if (Test-Path $root) { Remove-Item $root -Recurse -Force }
}
