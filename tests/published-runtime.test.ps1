# Synthetic metadata only: ensure the packaging gate rejects old/mixed/non-self-contained payloads.
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('sb-runtime-gate-' + [guid]::NewGuid().ToString('N'))
$version = '10.0.12'
$gate = Join-Path $PSScriptRoot '../scripts/Test-PublishedRuntime.ps1'
New-Item -ItemType Directory $root | Out-Null
function WriteFixture([string]$core, [string]$desktop, [string]$corePack, [string]$desktopPack) {
    @{ runtimeOptions = @{ includedFrameworks = @(@{ name='Microsoft.NETCore.App'; version=$core }, @{ name='Microsoft.WindowsDesktop.App'; version=$desktop }) } } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'SecureBrowser.runtimeconfig.json')
    @{ libraries = @{ "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/$corePack"=@{}; "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/$desktopPack"=@{} } } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'SecureBrowser.deps.json')
}
function ExpectRejection {
    $rejected = $false
    try { & $gate -PublishDirectory @($root) -ExpectedVersion $version -ReportPath '' }
    catch { $rejected = $true }
    if (!$rejected) { throw 'The published runtime gate accepted unsafe metadata.' }
}
try {
    WriteFixture $version $version $version $version
    & $gate -PublishDirectory @($root) -ExpectedVersion $version -ReportPath ''
    WriteFixture '10.0.0' '10.0.0' '10.0.0' '10.0.0'; ExpectRejection
    WriteFixture $version '10.0.0' $version $version; ExpectRejection
    WriteFixture $version $version $version '10.0.0'; ExpectRejection
    @{ runtimeOptions=@{ framework=@{ name='Microsoft.NETCore.App'; version=$version } } } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'SecureBrowser.runtimeconfig.json')
    ExpectRejection
    Write-Host 'PASS: runtime gate accepts patched metadata and rejects old, mixed-framework, mixed-pack and framework-dependent payloads (five cases).'
} finally { Remove-Item $root -Recurse -Force }
