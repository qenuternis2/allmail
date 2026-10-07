#requires -Version 7
param([Parameter(Mandatory)][string[]]$Path)
$ErrorActionPreference = 'Stop'
# Optional real certificate; absent credentials produce an explicitly unsigned release.
if (!$env:SECUREBROWSER_SIGN_PFX) {
  Write-Host 'Signing not configured: release is unsigned.'
  return
}
# Load an owned temporary certificate/key; never import/delete a user's existing store entry.
$certificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile(
  $env:SECUREBROWSER_SIGN_PFX, $env:SECUREBROWSER_SIGN_PASSWORD,
  [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet, $null)
try {
  if (!$certificate.HasPrivateKey -or
      $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date) -or
      !($certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } | ForEach-Object { $_.EnhancedKeyUsages } | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })) { throw 'A valid code-signing certificate with a private key is required.' }
  foreach ($file in $Path) {
    $signed = Set-AuthenticodeSignature -FilePath $file -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer 'https://timestamp.digicert.com'
    if ($signed.Status -ne 'Valid') { throw "Signature verification failed for $file`: $($signed.Status)" }
  }
} finally {
  $certificate.Dispose()
}
