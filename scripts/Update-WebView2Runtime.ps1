# Exercise the same Evergreen Runtime channel used by the shipped application.
$ErrorActionPreference = 'Stop'
$installer = Join-Path $env:RUNNER_TEMP 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
$partial = "$installer.part"
for ($attempt = 1; $attempt -le 3; $attempt++) {
  try {
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/?LinkId=2124701' -OutFile $partial -TimeoutSec 120
    Move-Item $partial $installer -Force
    break
  } catch {
    Remove-Item $partial -ErrorAction SilentlyContinue
    if ($attempt -eq 3) { throw }
    Write-Warning "Runtime download attempt $attempt failed; retrying."
    Start-Sleep -Seconds (2 * $attempt)
  }
}
$signature = Get-AuthenticodeSignature $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'WebView2 installer Microsoft signature is invalid.' }
$process = Start-Process $installer -ArgumentList '/silent', '/install' -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "WebView2 installer failed: $($process.ExitCode)" }
