# Exercise the same Evergreen Runtime channel used by the shipped application.
# Official bootstrapper selects the device architecture, including ARM64 on Windows 11.
$ErrorActionPreference = 'Stop'
$installer = Join-Path $env:RUNNER_TEMP 'MicrosoftEdgeWebview2Setup.exe'
$partial = "$installer.part"
for ($attempt = 1; $attempt -le 3; $attempt++) {
  try {
    if ($attempt -lt 3) {
      Invoke-WebRequest 'https://go.microsoft.com/fwlink/?LinkId=2124703' -OutFile $partial -TimeoutSec 120 -HttpVersion 1.1
    } else {
      # An independent Windows HTTP client also handles transient HttpClient ResponseEnded failures.
      # Only HTTPS redirects are allowed; the Microsoft signature is still mandatory below.
      $curl = Join-Path ([Environment]::GetFolderPath('System')) 'curl.exe'
      & $curl --http1.1 --fail --location --proto '=https' --proto-redir '=https' --connect-timeout 20 --max-time 120 --output $partial 'https://go.microsoft.com/fwlink/?LinkId=2124703'
      if ($LASTEXITCODE -ne 0) { throw "Runtime curl download failed: $LASTEXITCODE" }
    }
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
