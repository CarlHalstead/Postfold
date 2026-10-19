param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-beta\.(0|[1-9][0-9]*))?$')][string]$Version,
    [Parameter(Mandatory)][ValidateSet('stable', 'beta')][string]$Channel,
    [string]$SourceExportManifest,
    [string]$SourceExportSha256
)

$ErrorActionPreference = 'Stop'
if ($Channel -ne 'beta')
{
    throw 'Only the reviewed beta production identity is prepared; stable requires separate configuration review.'
}
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows))
{
    throw 'This draft workflow targets Windows x64 only.'
}
if ($Version.Length -gt 64 -or ($Channel -eq 'stable' -and $Version.Contains('-')))
{
    throw 'The version does not match the selected channel.'
}
foreach ($component in ($Version -replace '-beta\.', '.').Split('.'))
{
    $parsedComponent = 0
    if (-not [int]::TryParse($component, [ref]$parsedComponent))
    {
        throw 'Version components must fit Int32.'
    }
}
if (@(Get-ChildItem Env:VPK_*).Count -ne 0)
{
    throw 'Inherited VPK options are forbidden in the unsigned draft workflow.'
}
Set-Location -LiteralPath $repositoryRoot
$sourceArguments = @{ SourceCommit = $SourceCommit; SourceExportManifest = $SourceExportManifest; SourceExportSha256 = $SourceExportSha256 }
$sourceFiles = @(& (Join-Path $PSScriptRoot 'Test-DraftSource.ps1') @sourceArguments)
& (Join-Path $PSScriptRoot 'Verify-Source.ps1') -SourceFiles $sourceFiles
$sdk = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
if ((dotnet --version) -cne $sdk)
{
    throw 'The exact pinned SDK is required.'
}

function Invoke-Dotnet([string[]]$Arguments)
{
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet command failed: $($Arguments -join ' ')"
    }
}

Invoke-Dotnet -Arguments @('restore', 'Postfold.slnx', '--locked-mode')
Invoke-Dotnet -Arguments @('restore', 'Postfold.Desktop/Postfold.Desktop.csproj', '-p:RuntimeIdentifier=win-x64', '-p:SelfContained=true', '--locked-mode')
Invoke-Dotnet -Arguments @('tool', 'restore')
$localTools = @(dotnet tool list --local)
if ($LASTEXITCODE -ne 0 -or @($localTools | Where-Object { $_ -cmatch '^vpk\s+1\.2\.161\s+' }).Count -ne 1)
{
    throw 'The exact pinned vpk tool is required.'
}
# NuGet serialises regenerated locks with native Windows newlines; preserve the repository's exact LF convention.
foreach ($relativePath in ($sourceFiles | Where-Object { $_ -match '(^|/)packages.*\.lock\.json$' }))
{
    $lockPath = Join-Path $repositoryRoot $relativePath
    $lockText = [System.IO.File]::ReadAllText($lockPath).Replace("`r`n", "`n").TrimEnd() + "`n"
    [System.IO.File]::WriteAllText($lockPath, $lockText, [System.Text.UTF8Encoding]::new($false))
}
& (Join-Path $PSScriptRoot 'Test-DraftSource.ps1') @sourceArguments | Out-Null
Invoke-Dotnet -Arguments @('build', 'Postfold.slnx', '-c', 'Release', '--no-restore')
Invoke-Dotnet -Arguments @('format', 'Postfold.slnx', '--verify-no-changes', '--no-restore')
Invoke-Dotnet -Arguments @('format', 'style', 'Postfold.slnx', '--verify-no-changes', '--no-restore', '--severity', 'info', '--diagnostics', 'IDE0005')
Invoke-Dotnet -Arguments @('ef', 'migrations', 'has-pending-model-changes', '--project', 'Postfold.Storage', '--startup-project', 'Postfold.Storage', '--context', 'ProfileDbContext', '--configuration', 'Release', '--no-build')
Invoke-Dotnet -Arguments @('test', 'Postfold.slnx', '-c', 'Release', '--no-build', '--no-restore')

$draftRoot = Join-Path $repositoryRoot "artifacts/update-draft/$Version-$Channel"
if (Test-Path -LiteralPath $draftRoot)
{
    throw 'Use a new draft identity; existing draft artifacts are never overwritten or removed.'
}
$publishDirectory = Join-Path $draftRoot 'publish'
$releaseDirectory = Join-Path $draftRoot 'release'
[System.IO.Directory]::CreateDirectory($draftRoot) | Out-Null
& (Join-Path $PSScriptRoot 'Publish-UpdatePreparation.ps1') -SourceCommit $SourceCommit -Version $Version -OutputDirectory $publishDirectory -SourceExportManifest $SourceExportManifest -SourceExportSha256 $SourceExportSha256
foreach ($file in (Get-ChildItem -LiteralPath $publishDirectory -Recurse -File))
{
    if ($file.FullName -match '(?i)(Postfold-Planning|AGENTS\.md|SKILL\.md|\.agents|\.codex|Microsoft\.EntityFrameworkCore\.Design|Microsoft\.CodeAnalysis)')
    {
        throw "Forbidden publish input: $($file.Name)"
    }
}
$managedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDirectory 'Velopack.dll')).ProductVersion
if ($managedVersion -notmatch '^1\.2\.161(\+|$)')
{
    throw 'The shipping Velopack library must match the pinned CLI/native updater family.'
}
Invoke-Dotnet -Arguments @('vpk', 'pack', '--packId', 'Postfold', '--packVersion', $Version, '--packDir', $publishDirectory, '--mainExe', 'Postfold.exe', '--icon', (Join-Path $repositoryRoot 'Postfold.UI/Resources/Postfold.ico'), '--channel', "win-x64-$Channel", '--runtime', 'win-x64', '--delta', 'None', '--noPortable', '--outputDir', $releaseDirectory)
& (Join-Path $PSScriptRoot 'Test-DraftSource.ps1') @sourceArguments | Out-Null
if (@(Get-ChildItem -LiteralPath $releaseDirectory -Filter '*-delta.nupkg').Count -ne 0)
{
    throw 'Delta artifacts are forbidden in v1.'
}
$fullPackages = @(Get-ChildItem -LiteralPath $releaseDirectory -Filter '*-full.nupkg')
if ($fullPackages.Count -ne 1) { throw 'Exactly one full package is required.' }
$nativeInventory = Get-Content -LiteralPath ($publishDirectory + '-native.json') -Raw | ConvertFrom-Json
$archive = [System.IO.Compression.ZipFile]::OpenRead($fullPackages[0].FullName)
try
{
    $helperEntry = $archive.GetEntry('lib/app/Squirrel.exe')
    if ($null -eq $helperEntry) { throw 'Packaged native helper is missing.' }
    $helperStream = $helperEntry.Open()
    try
    {
        $helperHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($helperStream))
        if ($helperHash -cne $nativeInventory.helperSha256) { throw 'Packaged helper differs from the embedded native inventory.' }
    }
    finally { $helperStream.Dispose() }
}
finally { $archive.Dispose() }
$artifacts = @(Get-ChildItem -LiteralPath $releaseDirectory -File | ForEach-Object {
    [ordered]@{
        name = $_.Name
        length = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$receipt = [ordered]@{
    sourceCommit = $SourceCommit
    sourceKind = $(if ($SourceExportManifest) { 'qualification-export' } else { 'git-commit' })
    sourceExportSha256 = $SourceExportSha256
    sdk = $sdk
    version = $Version
    channel = $Channel
    rid = 'win-x64'
    velopack = $managedVersion
    vpk = '1.2.161'
    authenticated = $false
    distributionApproved = $false
    artifacts = $artifacts
}
[System.IO.File]::WriteAllText((Join-Path $draftRoot 'artifact-hashes.json'), ($receipt | ConvertTo-Json -Depth 5) + "`n", [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText((Join-Path $draftRoot 'QUALIFICATION-GAPS.txt'), "Unsigned draft only; distribution is not approved. Installer lifecycle requires separate local evidence. Authenticated native update handoff and packaged N-to-N+1 migration remain U04-U06 gates. No trusted manifest or signature is produced by CI.`n", [System.Text.UTF8Encoding]::new($false))
Write-Output "Draft artifacts prepared at $draftRoot. Offline signing and publication require their separate explicit approvals."
