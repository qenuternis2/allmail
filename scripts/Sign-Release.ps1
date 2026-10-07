#requires -Version 7
param([Parameter(Mandatory)][string[]]$Path)
$ErrorActionPreference = 'Stop'
# Optional real certificate; absent credentials produce an explicitly unsigned release.
if (!$env:SECUREBROWSER_SIGN_PFX) {
  Write-Host 'Signing not configured: release is unsigned.'
  return
}
$password = ConvertTo-SecureString $env:SECUREBROWSER_SIGN_PASSWORD -AsPlainText -Force
$certificate = Import-PfxCertificate -FilePath $env:SECUREBROWSER_SIGN_PFX -Password $password -CertStoreLocation Cert:\CurrentUser\My
try {
  if ($certificate.Count -ne 1 -or !$certificate.HasPrivateKey -or
      $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date) -or
      !($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' })) { throw 'A valid code-signing certificate with a private key is required.' }
  foreach ($file in $Path) {
    $signed = Set-AuthenticodeSignature -FilePath $file -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer 'https://timestamp.digicert.com'
    if ($signed.Status -ne 'Valid') { throw "Signature verification failed for $file`: $($signed.Status)" }
  }
} finally {
  if ($certificate.Thumbprint) { Remove-Item "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -DeleteKey }
}
