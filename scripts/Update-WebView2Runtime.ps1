# Use the Microsoft fixed Runtime matching the user's failing diagnostics.
$ErrorActionPreference = 'Stop'
$version = '154.0.4258.53'
$cab = Join-Path $env:RUNNER_TEMP 'WebView2Runtime.cab'
$destination = Join-Path $env:RUNNER_TEMP 'webview2-runtime-154'
Invoke-WebRequest 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/0b89c3a3-0043-4746-b39e-65830da7744d/Microsoft.WebView2.FixedVersionRuntime.154.0.4258.53.x64.cab' -OutFile $cab
New-Item -ItemType Directory -Path $destination -Force | Out-Null
& expand.exe $cab '-F:*' $destination | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'WebView2 Runtime CAB extraction failed.' }
$executable = @(Get-ChildItem $destination -Recurse -Filter msedgewebview2.exe)
if ($executable.Count -ne 1) { throw 'Expected one WebView2 executable.' }
$signature = Get-AuthenticodeSignature $executable[0].FullName
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'WebView2 Runtime Microsoft signature is invalid.' }
if ($executable[0].VersionInfo.FileVersion -ne $version) { throw 'WebView2 Runtime version mismatch.' }
"WEBVIEW2_BROWSER_EXECUTABLE_FOLDER=$($executable[0].DirectoryName)" >> $env:GITHUB_ENV
Write-Host "Verified Microsoft WebView2 Runtime $version"
