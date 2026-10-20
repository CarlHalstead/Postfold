#requires -Version 5.1
param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'Check', 'Sign', 'Bundle')][string]$Action,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-beta\.[1-9][0-9]*$')][string]$Version,
    [string]$ReleaseDirectory,
    [string]$SourceCommit,
    [long]$Sequence,
    [string]$DraftDirectory,
    [string]$DraftAuditSha256,
    [string]$SignerPath,
    [string]$PublicKeyPath,
    [string]$ReadmePath,
    [string]$ManifestPath,
    [string]$ReviewSha256,
    [string]$EncryptedKeyPath = (Join-Path $env:LOCALAPPDATA 'PostfoldSigning/working-key.pk8')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $ReadmePath) { $ReadmePath = Join-Path $PSScriptRoot 'UpdateBundle.README.txt' }
if (-not $ReleaseDirectory) { $ReleaseDirectory = Join-Path $root "artifacts/update-release/$Version" }
if (-not $DraftDirectory) { $DraftDirectory = Join-Path $root "artifacts/update-draft/$Version-beta" }
if (-not $SignerPath) { $SignerPath = Join-Path $root 'artifacts/tools/update-signer/Postfold.UpdateSigner.exe' }
$area = [IO.Path]::GetFullPath($ReleaseDirectory)
$inputs = Join-Path $area 'inputs'
$reviewPath = Join-Path $area 'release-review.json'
$manifestName = 'Postfold.win-x64-beta.manifest.json'
$signatureName = 'Postfold.win-x64-beta.manifest.sig'
$names = @("Postfold-$Version-win-x64-beta-full.nupkg", 'Postfold-win-x64-beta-Setup.exe', 'releases.win-x64-beta.json', 'assets.win-x64-beta.json', 'RELEASES-win-x64-beta')

function Write-Json([string]$Path, $Value)
{
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 12).Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
}

function Get-Row([string]$Path)
{
    $file = Get-Item -LiteralPath $Path
    return [ordered]@{name=$file.Name;length=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}
}

function Open-Reviewed([string]$Path, [string]$Hash)
{
    $full = [IO.Path]::GetFullPath($Path)
    for ($ancestor = $full; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor))
    {
        if ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reviewed paths cannot use links.' }
    }
    $guard = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if ($Hash -cnotmatch '^[A-F0-9]{64}$' -or (Get-FileHash -LiteralPath $full).Hash -cne $Hash) { $guard.Dispose(); throw 'Reviewed input changed.' }
    return $guard
}

if ($Action -ceq 'Prepare')
{
    if (Test-Path -LiteralPath $area) { throw 'Preserve existing release outputs; select a fresh directory.' }
    if ($SourceCommit -cnotmatch '^[a-f0-9]{40}$' -or $Sequence -le 0) { throw 'Select approved source and a separately reviewed monotonic sequence.' }
    $draftGuard = Open-Reviewed (Join-Path $DraftDirectory 'artifact-hashes.json') $DraftAuditSha256
    try
    {
        $draft = Get-Content -Raw -LiteralPath (Join-Path $DraftDirectory 'artifact-hashes.json') | ConvertFrom-Json
        if ($draft.sourceCommit -cne $SourceCommit -or $draft.version -cne $Version -or $draft.channel -cne 'beta' -or $draft.rid -cne 'win-x64' -or $draft.artifacts.Count -ne 5) { throw 'Draft identity refused.' }
        foreach ($name in $names)
        {
            $row = @($draft.artifacts | Where-Object name -ceq $name)
            $file = Join-Path $DraftDirectory ('release/' + $name)
            if ($row.Count -ne 1 -or (Get-Item $file).Length -ne $row[0].length -or (Get-FileHash $file).Hash -cne $row[0].sha256) { throw 'Draft artifact changed.' }
        }
        $feed = Get-Content -Raw -LiteralPath (Join-Path $DraftDirectory 'release/releases.win-x64-beta.json') | ConvertFrom-Json
        $package = Get-Row (Join-Path $DraftDirectory ('release/' + $names[0]))
        $assets = @($feed.Assets)
        if ($assets.Count -ne 1 -or $assets[0].PackageId -cne 'Postfold' -or $assets[0].Type -cne 'Full' -or $assets[0].Version -cne $Version -or $assets[0].FileName -cne $package.name -or $assets[0].Size -ne $package.length -or $assets[0].SHA256 -cne $package.sha256) { throw 'Full feed/package identity refused.' }
        [IO.Directory]::CreateDirectory($inputs) | Out-Null
        foreach ($name in $names) { [IO.File]::Copy((Join-Path $DraftDirectory ('release/' + $name)), (Join-Path $inputs $name), $false) }
        if ($PublicKeyPath) { $keyText = [IO.File]::ReadAllText([IO.Path]::GetFullPath($PublicKeyPath)).Trim() }
        else
        {
            $identity = [IO.File]::ReadAllText((Join-Path $root 'Postfold.Desktop/Services/Updates/ProductionUpdateIdentity.cs'))
            $match = [regex]::Match($identity, 'internal const string PublicKey = "([A-Za-z0-9+/=]+)";')
            if (-not $match.Success) { throw 'Production public root missing.' }
            $keyText = $match.Groups[1].Value
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $keyId = [BitConverter]::ToString($sha.ComputeHash([Convert]::FromBase64String($keyText))).Replace('-', '') }
        finally { $sha.Dispose() }
        [IO.File]::WriteAllText((Join-Path $area 'public-spki.txt'), $keyText + "`n", [Text.UTF8Encoding]::new($false))
        if ($ManifestPath) { [IO.File]::Copy([IO.Path]::GetFullPath($ManifestPath), (Join-Path $inputs $manifestName), $false) }
        else
        {
            $feedRow = Get-Row (Join-Path $inputs 'releases.win-x64-beta.json')
            $document = [ordered]@{
                kind='PostfoldUpdateManifest';schema=1;keyId=$keyId;product='Postfold';rid='win-x64';channel='beta';toolChannel='win-x64-beta';sequence=$Sequence;version=$Version;sourceCommit=$SourceCommit
                minimumApp='0.1.0-beta.1';minimumFormat=2;maximumFormat=2;targetFormat=2
                originMigrations=@('20261014150754_InitialEfProfile');targetMigration='20261014150754_InitialEfProfile'
                feed=[ordered]@{name=$feedRow.name;length=$feedRow.length;sha256=$feedRow.sha256}
                package=[ordered]@{name=$package.name;length=$package.length;sha256=$package.sha256}
            }
            Write-Json (Join-Path $inputs $manifestName) $document
        }
        $document = Get-Content -Raw -LiteralPath (Join-Path $inputs $manifestName) | ConvertFrom-Json
        if ($document.sourceCommit -cne $SourceCommit -or $document.version -cne $Version -or $document.sequence -ne $Sequence -or $document.keyId -cne $keyId -or $document.channel -cne 'beta' -or $document.rid -cne 'win-x64') { throw 'Manifest identity refused.' }
        $manifestFeed = Get-Row (Join-Path $inputs 'releases.win-x64-beta.json')
        foreach ($property in @('name', 'length', 'sha256'))
        {
            if ($document.feed.$property -cne $manifestFeed[$property] -or $document.package.$property -cne $package[$property]) { throw 'Manifest artifact binding refused.' }
        }
        [IO.File]::Copy([IO.Path]::GetFullPath($ReadmePath), (Join-Path $area 'README.txt'), $false)
        $review = [ordered]@{
            sourceCommit=$SourceCommit;version=$Version;sequence=$Sequence;channel='beta';rid='win-x64';keyId=$keyId
            signerPath=[IO.Path]::GetFullPath($SignerPath);signerSha256=(Get-FileHash -LiteralPath $SignerPath).Hash
            publicKeySha256=(Get-FileHash (Join-Path $area 'public-spki.txt')).Hash;readmeSha256=(Get-FileHash (Join-Path $area 'README.txt')).Hash
            artifacts=@($names + $manifestName | ForEach-Object { Get-Row (Join-Path $inputs $_) })
        }
        Write-Json $reviewPath $review
        Write-Output "Prepared release data only. Review SHA256: $((Get-FileHash $reviewPath).Hash). No signing or publication."
    }
    finally { $draftGuard.Dispose() }
    return
}

$guards = [Collections.Generic.List[IDisposable]]::new()
$process = $null
$receipt = [ordered]@{action=$Action;version=$Version;shellPid=$PID;shellStart=(Get-Process -Id $PID).StartTime.ToUniversalTime();started=[DateTime]::UtcNow;published=$false}
try
{
    $guards.Add((Open-Reviewed $reviewPath $ReviewSha256))
    $review = Get-Content -Raw -LiteralPath $reviewPath | ConvertFrom-Json
    if ($review.version -cne $Version -or $review.channel -cne 'beta' -or $review.rid -cne 'win-x64' -or $review.sourceCommit -cnotmatch '^[a-f0-9]{40}$' -or $review.sequence -le 0 -or $review.artifacts.Count -ne 6) { throw 'Release review identity refused.' }
    $key = Join-Path $area 'public-spki.txt'
    $readme = Join-Path $area 'README.txt'
    $guards.Add((Open-Reviewed $review.signerPath $review.signerSha256))
    $guards.Add((Open-Reviewed $key $review.publicKeySha256))
    $guards.Add((Open-Reviewed $readme $review.readmeSha256))
    foreach ($name in ($names + $manifestName))
    {
        $row = @($review.artifacts | Where-Object name -ceq $name)
        if ($row.Count -ne 1 -or (Get-Item (Join-Path $inputs $name)).Length -ne $row[0].length) { throw 'Release input inventory refused.' }
        $guards.Add((Open-Reviewed (Join-Path $inputs $name) $row[0].sha256))
    }
    $document = Get-Content -Raw -LiteralPath (Join-Path $inputs $manifestName) | ConvertFrom-Json
    if ($document.version -cne $Version -or $document.sourceCommit -cne $review.sourceCommit -or $document.sequence -ne $review.sequence -or $document.keyId -cne $review.keyId) { throw 'Reviewed manifest changed.' }
    Write-Output "Postfold $Version / beta / sequence $($review.sequence) / source $($review.sourceCommit)"
    Write-Output "Manifest SHA256: $((Get-FileHash (Join-Path $inputs $manifestName)).Hash)"
    $signature = Join-Path $inputs $signatureName
    if ($Action -ceq 'Check' -and -not (Test-Path $signature)) { Write-Output 'PASS: exact reviewed unsigned inputs and reusable signer. No private key accessed.'; return }
    if ($Action -ceq 'Sign' -and (Test-Path $signature)) { throw 'Signature already exists; preserve it.' }
    if ($Action -ceq 'Bundle')
    {
        $signedAudit = Join-Path $area 'signed-input-audit.json'
        if (Test-Path $signedAudit) { throw 'Preserve existing signed audit.' }
        $rows = @($review.artifacts) + (Get-Row $signature)
        Write-Json $signedAudit ([ordered]@{sourceCommit=$review.sourceCommit;version=$Version;channel='beta';rid='win-x64';artifacts=$rows})
        & (Join-Path $PSScriptRoot 'New-UpdateBundle.ps1') -InputDirectory $inputs -AuditPath $signedAudit -AuditSha256 (Get-FileHash $signedAudit).Hash -SourceCommit $review.sourceCommit -Version $Version -PublicKeyPath $key -PublicKeySha256 $review.publicKeySha256 -VerifierPath $review.signerPath -VerifierSha256 $review.signerSha256 -ReadmePath $readme -ReadmeSha256 $review.readmeSha256 -OutputDirectory (Join-Path $area 'distribution')
        return
    }
    if ($Action -ceq 'Sign' -and [Console]::IsInputRedirected) { throw 'Signing requires an owner-operated interactive Windows console.' }
    $arguments = if ($Action -ceq 'Sign') { @('manifest', (Join-Path $inputs $manifestName), $inputs, [IO.Path]::GetFullPath($EncryptedKeyPath), $signature) } else { @('verify', (Join-Path $inputs $manifestName), $inputs, $key, $signature) }
    foreach ($argument in $arguments) { if ($argument.Contains('"') -or $argument.EndsWith('\')) { throw 'Unsupported process argument.' } }
    $start = [Diagnostics.ProcessStartInfo]::new($review.signerPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $Action -cne 'Sign'
    $start.WorkingDirectory = $area
    $start.Arguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    foreach ($name in @($start.EnvironmentVariables.Keys)) { if ($name -match '^(DOTNET_|CORECLR_|COR_|COMPlus_)') { $start.EnvironmentVariables.Remove($name) } }
    $process = [Diagnostics.Process]::Start($start)
    $null = $process.Handle
    $receipt.pid = $process.Id
    $receipt.start = $process.StartTime.ToUniversalTime()
    if (-not $process.WaitForExit(900000)) { throw 'Signing/verification deadline reached.' }
    $receipt.exit = $process.ExitCode
    if ($process.ExitCode -ne 0) { throw 'Signer refused; preserve outputs and report its message.' }
    Write-Output 'PASS: operation completed. Publication and activation require their separate approvals.'
}
finally
{
    if ($process)
    {
        if (-not $process.HasExited) { $process.Kill(); if (-not $process.WaitForExit(10000)) { throw "Owned signer remains: $($process.Id)" } }
        $receipt.processExited = $process.HasExited
        $process.Dispose()
    }
    foreach ($guard in $guards) { $guard.Dispose() }
    $receipt.ended = [DateTime]::UtcNow
    if (Test-Path -LiteralPath $area) { Write-Json (Join-Path $area ('operation-' + [Guid]::NewGuid().ToString('N') + '.json')) $receipt }
}
