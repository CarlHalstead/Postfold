#requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$FixtureDirectory,
    [Parameter(Mandatory)][string]$PublicKeyPath,
    [Parameter(Mandatory)][string]$SignerPath,
    [Parameter(Mandatory)][string]$ReadmePath,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$area = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $area) { throw 'Preserve previous test outputs.' }
$fixture = [IO.Path]::GetFullPath($FixtureDirectory)
$manifest = Get-Content -Raw -LiteralPath (Join-Path $fixture 'Postfold.win-x64-beta.manifest.json') | ConvertFrom-Json
$draft = Join-Path $area 'draft'
$release = Join-Path $draft 'release'
[IO.Directory]::CreateDirectory($release) | Out-Null
$names = @($manifest.package.name, 'Postfold-win-x64-beta-Setup.exe', 'releases.win-x64-beta.json', 'assets.win-x64-beta.json', 'RELEASES-win-x64-beta')
$rows = @()
foreach ($name in $names)
{
    [IO.File]::Copy((Join-Path $fixture $name), (Join-Path $release $name), $false)
    $file = Get-Item -LiteralPath (Join-Path $release $name)
    $rows += [ordered]@{name=$name;length=$file.Length;sha256=(Get-FileHash $file.FullName).Hash}
}
$audit = Join-Path $draft 'artifact-hashes.json'
[IO.File]::WriteAllText($audit, (([ordered]@{sourceCommit=$manifest.sourceCommit;version=$manifest.version;channel='beta';rid='win-x64';artifacts=$rows} | ConvertTo-Json -Depth 8) + "`n"), [Text.UTF8Encoding]::new($false))
$prepared = Join-Path $area 'release'
$script = Join-Path $PSScriptRoot 'Invoke-UpdateRelease.ps1'
$generated = Join-Path $area 'generated'
& $script -Action Prepare -Version $manifest.version -SourceCommit $manifest.sourceCommit -Sequence $manifest.sequence -DraftDirectory $draft -DraftAuditSha256 (Get-FileHash $audit).Hash -ReleaseDirectory $generated -SignerPath $SignerPath -PublicKeyPath $PublicKeyPath -ReadmePath $ReadmePath
$generatedManifest = [IO.File]::ReadAllText((Join-Path $generated 'inputs/Postfold.win-x64-beta.manifest.json'))
if (-not $generatedManifest.Contains("`n    `"feed`": {`n        `"name`": ") -or $generatedManifest.Contains("`r") -or -not $generatedManifest.EndsWith("`n")) { throw 'Generated manifest formatting changed.' }
& $script -Action Check -Version $manifest.version -ReleaseDirectory $generated -ReviewSha256 (Get-FileHash (Join-Path $generated 'release-review.json')).Hash
& $script -Action Prepare -Version $manifest.version -SourceCommit $manifest.sourceCommit -Sequence $manifest.sequence -DraftDirectory $draft -DraftAuditSha256 (Get-FileHash $audit).Hash -ReleaseDirectory $prepared -SignerPath $SignerPath -PublicKeyPath $PublicKeyPath -ReadmePath $ReadmePath -ManifestPath (Join-Path $fixture 'Postfold.win-x64-beta.manifest.json')
$reviewHash = (Get-FileHash (Join-Path $prepared 'release-review.json')).Hash
$arguments = @{Version=$manifest.version;ReleaseDirectory=$prepared;ReviewSha256=$reviewHash}
& $script -Action Check @arguments
$refused = $false
try { & $script -Action Check -Version ($manifest.version + '0') -ReleaseDirectory $prepared -ReviewSha256 $reviewHash }
catch { $refused = $true }
if (-not $refused) { throw 'Wrong version accepted.' }
$refused = $false
try { & $script -Action Check -Version $manifest.version -ReleaseDirectory $prepared -ReviewSha256 ('0' * 64) }
catch { $refused = $true }
if (-not $refused) { throw 'Wrong review hash accepted.' }
$setup = Join-Path $prepared 'inputs/Postfold-win-x64-beta-Setup.exe'
$bytes = [IO.File]::ReadAllBytes($setup)
try
{
    $changed = [byte[]]$bytes.Clone()
    $changed[0] = $changed[0] -bxor 1
    [IO.File]::WriteAllBytes($setup, $changed)
    $refused = $false
    try { & $script -Action Check @arguments } catch { $refused = $true }
    if (-not $refused) { throw 'Changed installer accepted.' }
}
finally { [IO.File]::WriteAllBytes($setup, $bytes) }
[IO.File]::Copy((Join-Path $fixture 'Postfold.win-x64-beta.manifest.sig'), (Join-Path $prepared 'inputs/Postfold.win-x64-beta.manifest.sig'), $false)
& $script -Action Check @arguments
& $script -Action Bundle @arguments
$archive = Join-Path $prepared ('distribution/Postfold-' + $manifest.version + '-win-x64.zip')
if (-not (Test-Path $archive)) { throw 'Final ZIP absent.' }
$refused = $false
try { & $script -Action Bundle @arguments } catch { $refused = $true }
if (-not $refused) { throw 'Existing output overwritten.' }
$result = [ordered]@{shellVersion=$PSVersionTable.PSVersion.ToString();version=$manifest.version;prepared=$true;unsignedCheck=$true;signedCheck=$true;wrongVersionRefused=$true;wrongReviewRefused=$true;changedInstallerRefused=$true;overwriteRefused=$true;archiveSha256=(Get-FileHash $archive).Hash;privateKeyAccessed=$false;published=$false}
[IO.File]::WriteAllText((Join-Path $area 'qualification.json'), (($result | ConvertTo-Json) + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output 'PASS: reusable release preparation, unsigned/signed checks, nine-leaf bundle and refusal cases. No private key accessed.'
