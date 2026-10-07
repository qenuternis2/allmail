# CI-only: validate the requested client OS and install the existing x64 target's pinned SDK on ARM64.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$info = Get-ComputerInfo
$info | Select-Object WindowsProductName, OsArchitecture, OsName
$os = Get-CimInstance Win32_OperatingSystem
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($os.ProductType -ne 1 -or $os.Caption -notmatch 'Windows 11' -or $architecture -ne 'Arm64') {
    throw "Expected Windows 11 ARM64 workstation, observed $($os.Caption), ProductType=$($os.ProductType), architecture=$architecture."
}
New-Item -ItemType Directory -Force artifacts/test-results | Out-Null
[ordered]@{
    Caption = $os.Caption; WindowsProductName = $info.WindowsProductName; BuildNumber = $os.BuildNumber
    ProductType = $os.ProductType; OsArchitecture = $info.OsArchitecture; NativeArchitecture = $architecture
    PowerShellProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
} | ConvertTo-Json | Set-Content artifacts/test-results/windows11-environment.json
# Reuse setup-dotnet's installed official script; select architecture explicitly rather than spoofing OS variables.
$script = Join-Path (Split-Path $env:RUNNER_WORKSPACE -Parent) '_actions/actions/setup-dotnet/v4/externals/install-dotnet.ps1'
if (!(Test-Path $script)) { throw 'The setup-dotnet v4 installation script was not found.' }
$sdk = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
$root = Join-Path $env:RUNNER_TEMP 'securebrowser-dotnet-x64'
& $script -Version $sdk -Architecture x64 -InstallDir $root
"$root" | Out-File $env:GITHUB_PATH -Append -Encoding utf8
"DOTNET_ROOT=$root" | Out-File $env:GITHUB_ENV -Append -Encoding utf8
"DOTNET_ROOT_X64=$root" | Out-File $env:GITHUB_ENV -Append -Encoding utf8
& (Join-Path $root 'dotnet.exe') --info
