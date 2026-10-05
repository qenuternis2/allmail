# Exercise the same Evergreen Runtime channel used by the shipped application.
$ErrorActionPreference = 'Stop'
$installer = Join-Path $env:RUNNER_TEMP 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
Invoke-WebRequest 'https://go.microsoft.com/fwlink/?LinkId=2124701' -OutFile $installer
$signature = Get-AuthenticodeSignature $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'WebView2 installer Microsoft signature is invalid.' }
$process = Start-Process $installer -ArgumentList '/silent', '/install' -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "WebView2 installer failed: $($process.ExitCode)" }
