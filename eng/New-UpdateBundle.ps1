#requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$AuditPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$AuditSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-beta\.[1-9][0-9]*)?$')][string]$Version,
    [Parameter(Mandatory)][string]$PublicKeyPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$PublicKeySha256,
    [Parameter(Mandatory)][string]$VerifierPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$VerifierSha256,
    [Parameter(Mandatory)][string]$ReadmePath,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ReadmeSha256,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$inputRoot = [IO.Path]::GetFullPath($InputDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a new output directory; preserve existing attempts.' }
if ($output.StartsWith($inputRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be separate from audited inputs.' }

function Open-Reviewed([string]$Path, [string]$Hash)
{
    $full = [IO.Path]::GetFullPath($Path)
    for ($ancestor = $full; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor))
    {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reviewed paths cannot use links.' }
    }
    $guard = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if ((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -cne $Hash) { $guard.Dispose(); throw 'Reviewed input changed.' }
    return $guard
}

function Add-ReviewedEntry($Archive, [string]$Path, [string]$Name)
{
    $entry = $Archive.CreateEntry($Name, [IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $entry.ExternalAttributes = 0
    $source = [IO.File]::OpenRead($Path)
    $destination = $entry.Open()
    try { $source.CopyTo($destination) } finally { $destination.Dispose(); $source.Dispose() }
}

$guards = [Collections.Generic.List[IDisposable]]::new()
$process = $null
$processReceipt = [ordered]@{ shellPid=$PID; shellStart=(Get-Process -Id $PID).StartTime.ToUniversalTime(); started=[DateTime]::UtcNow; verified=$false; published=$false }
try
{
    $guards.Add((Open-Reviewed $AuditPath $AuditSha256))
    $guards.Add((Open-Reviewed $PublicKeyPath $PublicKeySha256))
    $guards.Add((Open-Reviewed $VerifierPath $VerifierSha256))
    $guards.Add((Open-Reviewed $ReadmePath $ReadmeSha256))
    $audit = Get-Content -LiteralPath $AuditPath -Raw | ConvertFrom-Json
    if ($audit.sourceCommit -cne $SourceCommit -or $audit.version -cne $Version -or $audit.channel -cne 'beta' -or $audit.rid -cne 'win-x64') { throw 'Audited source/version/channel mismatch.' }
    $names = @("Postfold-$Version-win-x64-beta-full.nupkg", 'Postfold-win-x64-beta-Setup.exe', 'releases.win-x64-beta.json', 'assets.win-x64-beta.json', 'RELEASES-win-x64-beta', 'Postfold.win-x64-beta.manifest.json', 'Postfold.win-x64-beta.manifest.sig')
    $rows = @()
    foreach ($name in $names)
    {
        $path = Join-Path $inputRoot $name
        $accepted = @($audit.artifacts | Where-Object name -ceq $name)
        if ($accepted.Count -ne 1 -or $accepted[0].length -le 0) { throw "Missing exact audited input: $name" }
        $guards.Add((Open-Reviewed $path $accepted[0].sha256))
        if ((Get-Item -LiteralPath $path).Length -ne $accepted[0].length) { throw 'Audited input length mismatch.' }
        $rows += [ordered]@{name=$name;length=$accepted[0].length;sha256=$accepted[0].sha256}
    }
    if (@(Get-ChildItem -LiteralPath $inputRoot -Force).Count -ne $names.Count) { throw 'Input directory must contain only the seven reviewed distribution inputs.' }
    $manifest = Join-Path $inputRoot 'Postfold.win-x64-beta.manifest.json'
    $signature = Join-Path $inputRoot 'Postfold.win-x64-beta.manifest.sig'
    $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($VerifierPath))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $inputRoot
    $arguments = @('verify', $manifest, $inputRoot, [IO.Path]::GetFullPath($PublicKeyPath), $signature)
    foreach ($argument in $arguments) { if ($argument.Contains('"') -or $argument.EndsWith('\')) { throw 'Unsupported command argument.' } }
    $start.Arguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    foreach ($name in @($start.EnvironmentVariables.Keys))
    {
        if ($name -match '^(DOTNET_|CORECLR_|COR_|COMPlus_)') { $start.EnvironmentVariables.Remove($name) }
    }
    $process = [Diagnostics.Process]::Start($start)
    $null = $process.Handle
    $processReceipt.pid = $process.Id
    $processReceipt.start = $process.StartTime.ToUniversalTime()
    if (-not $process.WaitForExit(600000)) { throw 'Public verification deadline reached.' }
    $processReceipt.exit = $process.ExitCode
    if ($process.ExitCode -ne 0) { throw 'Public verification refused; no final ZIP was produced.' }
    $document = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    if ($document.sourceCommit -cne $SourceCommit -or $document.version -cne $Version -or $document.channel -cne 'beta') { throw 'Signed source/version mismatch.' }
    $total = [long](Get-Item -LiteralPath $ReadmePath).Length
    foreach ($row in $rows) { $total += [long]$row.length }
    if ($total -gt 2147483648 -or (Get-Item -LiteralPath $ReadmePath).Length -gt 65536) { throw 'Distribution declared size exceeded.' }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $processReceipt.verified = $true
    $metadata = Join-Path $output "repository-metadata/updates/releases/v$Version/win-x64-beta"
    [IO.Directory]::CreateDirectory($metadata) | Out-Null
    $archivePath = Join-Path $output "Postfold-$Version-win-x64.zip"
    $file = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try
    {
        $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $true)
        try
        {
            foreach ($row in $rows)
            {
                Add-ReviewedEntry $zip (Join-Path $inputRoot $row.name) $row.name
            }
            Add-ReviewedEntry $zip ([IO.Path]::GetFullPath($ReadmePath)) 'README.txt'
            $rows += [ordered]@{name='README.txt';length=(Get-Item -LiteralPath $ReadmePath).Length;sha256=$ReadmeSha256}
            $checksums = (($rows | Sort-Object name | ForEach-Object { $_.sha256 + '  ' + $_.name }) -join "`n") + "`n"
            $entry = $zip.CreateEntry('SHA256SUMS.txt')
            $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $entry.ExternalAttributes = 0
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($checksums) } finally { $writer.Dispose() }
        }
        finally { $zip.Dispose() }
        $file.Flush($true)
        if ($file.Length -gt 536870912) { throw 'Final ZIP exceeds the updater transfer bound.' }
    }
    finally { $file.Dispose() }
    foreach ($name in @('Postfold.win-x64-beta.manifest.json', 'Postfold.win-x64-beta.manifest.sig', 'releases.win-x64-beta.json'))
    {
        [IO.File]::Copy((Join-Path $inputRoot $name), (Join-Path $metadata $name), $false)
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try
    {
        if ($zip.Entries.Count -ne 9) { throw 'Unexpected ZIP entry count.' }
        foreach ($entry in $zip.Entries)
        {
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($entry.FullName -ceq 'SHA256SUMS.txt')
            {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $expectedHash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($checksums))).Replace('-', '') } finally { $sha.Dispose() }
            }
            else
            {
                $expected = @($rows | Where-Object name -ceq $entry.FullName)
                if ($expected.Count -ne 1 -or $entry.Length -ne $expected[0].length) { throw 'ZIP entry differs from the audited inventory.' }
                $expectedHash = $expected[0].sha256
            }
            if ($hash -cne $expectedHash) { throw 'ZIP bytes differ from reviewed inputs.' }
        }
    }
    finally { $zip.Dispose() }
    foreach ($name in @('Postfold.win-x64-beta.manifest.json', 'Postfold.win-x64-beta.manifest.sig', 'releases.win-x64-beta.json'))
    {
        if ((Get-FileHash -LiteralPath (Join-Path $metadata $name)).Hash -cne (Get-FileHash -LiteralPath (Join-Path $inputRoot $name)).Hash) { throw 'Repository metadata copy differs.' }
    }
    $processReceipt.archive = [IO.Path]::GetFileName($archivePath)
    $processReceipt.bytes = (Get-Item -LiteralPath $archivePath).Length
    $processReceipt.sha256 = (Get-FileHash -LiteralPath $archivePath).Hash
    $processReceipt.sourceCommit = $SourceCommit
    $processReceipt.version = $Version
    $processReceipt.metadataDirectory = $metadata
    Write-Output "One ZIP verified at $archivePath; repository metadata is a separate local publication preparation. Nothing published."
}
finally
{
    if ($process)
    {
        if (-not $process.HasExited) { $process.Kill(); if (-not $process.WaitForExit(10000)) { throw "Verifier PID $($process.Id) remains; report immediately." } }
        $processReceipt.processExited = $process.HasExited
        $process.Dispose()
    }
    foreach ($guard in $guards) { $guard.Dispose() }
    $processReceipt.ended = [DateTime]::UtcNow
    if (Test-Path -LiteralPath $output) { [IO.File]::WriteAllText((Join-Path $output 'bundle-receipt.json'), ($processReceipt | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false)) }
}
