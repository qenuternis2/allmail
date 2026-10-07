# Verify both self-contained flavors before packaging. No binaries are executed.
[CmdletBinding()]
param(
    [string[]]$PublishDirectory = @('artifacts/publish-core', 'artifacts/publish-experimental'),
    [string]$ExpectedVersion = '',
    [string]$ReportPath = 'artifacts/test-results/published-runtime.json'
)
$ErrorActionPreference = 'Stop'
if (!$ExpectedVersion) {
    [xml]$project = Get-Content (Join-Path $PSScriptRoot '../src/ProtonProfiles.App/ProtonProfiles.App.csproj')
    $ExpectedVersion = $project.Project.PropertyGroup.RuntimeFrameworkVersion | Where-Object { $_ } | Select-Object -First 1
}
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'A pinned runtime patch version is required.' }
$results = foreach ($directory in $PublishDirectory) {
    $config = Get-Content (Join-Path $directory 'SecureBrowser.runtimeconfig.json') -Raw | ConvertFrom-Json
    $deps = Get-Content (Join-Path $directory 'SecureBrowser.deps.json') -Raw | ConvertFrom-Json
    $frameworks = @($config.runtimeOptions.includedFrameworks)
    if ($frameworks.Count -ne 2) { throw "Expected two self-contained frameworks in $directory." }
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $match = @($frameworks | Where-Object { $_.name -eq $name })
        if ($match.Count -ne 1 -or $match[0].version -ne $ExpectedVersion) {
            throw "Unexpected $name runtime version in $directory; expected $ExpectedVersion."
        }
    }
    $packs = @($deps.libraries.PSObject.Properties | Where-Object { $_.Name.StartsWith('runtimepack.Microsoft.') })
    $expectedPacks = @("runtimepack.Microsoft.NETCore.App.Runtime.win-x64/$ExpectedVersion", "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/$ExpectedVersion")
    if ($packs.Count -ne 2 -or @($expectedPacks | Where-Object { $_ -notin $packs.Name }).Count -ne 0) {
        throw "Self-contained runtime packs do not match $ExpectedVersion win-x64 in $directory."
    }
    [ordered]@{ Directory = [IO.Path]::GetFullPath($directory); Frameworks = $frameworks; RuntimePacks = @($packs.Name) }
}
if ($ReportPath) {
    $parent = Split-Path ([IO.Path]::GetFullPath($ReportPath))
    New-Item -ItemType Directory -Force $parent | Out-Null
    ConvertTo-Json -InputObject @($results) -Depth 6 | Set-Content $ReportPath -Encoding utf8
}
Write-Host "PASS: self-contained runtime config and packs match $ExpectedVersion win-x64 in $($PublishDirectory.Count) publish directories."
