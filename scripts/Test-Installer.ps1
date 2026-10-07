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
$runtimeRoots=@(
  (Join-Path ${env:ProgramFiles(x86)} 'Microsoft/EdgeWebView/Application'),
  (Join-Path $env:ProgramFiles 'Microsoft/EdgeWebView/Application'),
  (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft/EdgeWebView/Application')
)
$runtimeFiles=@($runtimeRoots | Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem $_ -Filter msedgewebview2.exe -Recurse -File })
if (!$runtimeFiles.Count) { throw 'Shared WebView2 Runtime prerequisite is missing from installer test runner.' }
$runtimeHashes=@($runtimeFiles | ForEach-Object { [pscustomobject]@{Path=$_.FullName;Hash=(Get-FileHash $_.FullName -Algorithm SHA256).Hash} })
$script:operation = 0
function Run([string]$File,[string[]]$Arguments) {
  $script:operation++
  $logs = Join-Path $PWD 'artifacts/test-results'
  New-Item -ItemType Directory -Force $logs | Out-Null
  $log = Join-Path $logs ("installer-operation-$script:operation.log")
  Write-Output "Installer operation $script:operation starting: $([IO.Path]::GetFileName($File))"
  # Inno's uninstaller launches a temporary child and its first process may
  # exit before removal finishes. -Wait observes the whole tree; WaitForExit
  # on that first Process does not. The CI step provides the outer timeout.
  $p=Start-Process $File -ArgumentList ($Arguments + ('/LOG="' + $log + '"')) -Wait -PassThru
  try {
    if ($p.ExitCode -ne 0) { throw "Installer operation failed: $($p.ExitCode); see $log." }
    Write-Output "Installer operation $script:operation completed."
  } finally { $p.Dispose() }
}
try {
  New-Item -ItemType Directory $root -Force | Out-Null
  $externalDownload=Join-Path $root 'external-download.txt'
  [IO.File]::WriteAllText($externalDownload,'external attachment preserved')
  $installArgs=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$install+'"'))
  Run $installerPath $installArgs
  Run $installerPath $installArgs
  if ([IO.File]::ReadAllText($marker) -ne 'preserved' -or !(Test-Path (Join-Path $install 'SecureBrowser.exe')) -or
      !(Test-Path (Join-Path $install 'SecureBrowser.Core.dll')) -or !(Test-Path (Join-Path $install 'Microsoft.Web.WebView2.Core.dll'))) { throw 'Install/update changed data or omitted payload.' }
  [xml]$props=Get-Content (Join-Path $PSScriptRoot '../Directory.Build.props')
  $binary=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'SecureBrowser.exe'))
  if ($binary.ProductVersion.Split('+')[0] -ne $props.Project.PropertyGroup.Version) { throw 'Installed version mismatch.' }
  $browser = Start-Process (Join-Path $install 'SecureBrowser.exe') -PassThru
  try {
    $deadline = (Get-Date).AddSeconds(30)
    do {
      Start-Sleep -Milliseconds 100
      $browser.Refresh()
      if ($browser.HasExited) { throw "Installed GUI exited during startup: $($browser.ExitCode)" }
    } while ($browser.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
    if ($browser.MainWindowHandle -eq 0 -or $browser.MainWindowTitle -notlike '*SecureBrowser*' -or !(Test-Path (Join-Path $data 'profiles.db'))) { throw 'Installed production GUI did not initialize.' }
    if (!$browser.CloseMainWindow() -or !$browser.WaitForExit(30000) -or $browser.ExitCode -ne 0) { throw 'Installed production GUI did not close cleanly.' }
  } finally {
    if (!$browser.HasExited) { $browser.Kill() } # Exact test-owned application PID only.
    $browser.Dispose()
  }
  $database=Join-Path $data 'profiles.db'
  $databaseHash=(Get-FileHash $database -Algorithm SHA256).Hash
  Run (Join-Path $install 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
  if ([IO.File]::ReadAllText($marker) -ne 'preserved' -or (Get-FileHash $database -Algorithm SHA256).Hash -ne $databaseHash -or (Test-Path (Join-Path $install 'SecureBrowser.exe'))) { throw 'Default uninstall did not preserve metadata/remove binaries.' }
  Run $installerPath $installArgs
  if ((Get-FileHash $database -Algorithm SHA256).Hash -ne $databaseHash) { throw 'Reinstallation changed existing production metadata.' }
  Run (Join-Path $install 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/REMOVEUSERDATA')
  if ((Test-Path $data) -or (Test-Path (Join-Path $install 'SecureBrowser.exe'))) { throw 'Explicit data removal did not finish.' }
  if ([IO.File]::ReadAllText($externalDownload) -ne 'external attachment preserved') { throw 'Uninstall changed an external download.' }
  foreach ($runtimeFile in $runtimeHashes) {
    if (!(Test-Path $runtimeFile.Path) -or (Get-FileHash $runtimeFile.Path -Algorithm SHA256).Hash -ne $runtimeFile.Hash) { throw 'Uninstall changed shared WebView2 Runtime.' }
  }
  Write-Output 'PASS: per-user installer; complete versioned payload; real production GUI starts/closes; install/uninstall/reinstall preserves actual metadata hash; default uninstall preserves data; explicit removal deletes managed data; external attachment and shared Runtime preserved.'
} finally {
  # Only test-created paths; a failed cleanup deliberately leaves evidence for inspection.
  if (Test-Path $root) { Remove-Item $root -Recurse -Force }
}
