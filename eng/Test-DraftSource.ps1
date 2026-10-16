param(
    [Parameter(Mandatory)][string]$SourceCommit,
    [string]$SourceExportManifest,
    [string]$SourceExportSha256
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $SourceExportManifest -and -not $SourceExportSha256)
{
    $actualCommit = git -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $actualCommit -cne $SourceCommit)
    {
        throw 'The checkout must match the explicitly selected source commit.'
    }
    $status = @(git -C $root status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0)
    {
        throw 'Use a clean checkout; the draft workflow never stages, resets or commits source.'
    }
    return git -C $root ls-files
}
if (-not $SourceExportManifest -or $SourceExportSha256 -cnotmatch '^[A-F0-9]{64}$' -or (Test-Path -LiteralPath (Join-Path $root '.git')))
{
    throw 'Export qualification requires a complete manifest, its explicit SHA-256, and a source directory without Git metadata.'
}
if ((Get-FileHash -LiteralPath $SourceExportManifest -Algorithm SHA256).Hash -cne $SourceExportSha256)
{
    throw 'The source export manifest does not match its selected identity.'
}
$manifest = Get-Content -LiteralPath $SourceExportManifest -Raw | ConvertFrom-Json
if ($manifest.baseCommit -cne $SourceCommit -or $manifest.kind -cne 'PostfoldQualificationSourceExport' -or $manifest.files.Count -eq 0)
{
    throw 'The source export must identify the selected base commit and complete file inventory.'
}
$paths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest.files)
{
    if ($file.path -cnotmatch '^[A-Za-z0-9_. /-]+$' -or $file.path -match '(^/|(^|/)\.\.(/|$)|:|\\)' -or -not $paths.Add($file.path))
    {
        throw 'Invalid or duplicate source export path.'
    }
    $path = Join-Path $root $file.path
    if ((Get-Item -LiteralPath $path).Attributes -band [System.IO.FileAttributes]::ReparsePoint -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $file.sha256)
    {
        throw "Source export bytes changed: $($file.path)"
    }
}
foreach ($entry in (Get-ChildItem -LiteralPath $root -Force -Recurse))
{
    $relative = [System.IO.Path]::GetRelativePath($root, $entry.FullName).Replace('\', '/')
    if ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint)
    {
        throw "Reparse points are forbidden in qualification source: $relative"
    }
    if (-not $entry.PSIsContainer -and -not $paths.Contains($relative) -and
        $relative -notmatch '^(artifacts/|Postfold\.[A-Za-z.]+/(bin|obj)/)')
    {
        throw "Unidentified source export input: $relative"
    }
}
return $manifest.files.path
