#requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$NextDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceCommit
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Installed native update qualification requires Windows.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$area = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (-not $area.StartsWith($root + '\artifacts\', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $area))
{
    throw 'Use a fresh repository artifacts directory for synthetic qualification.'
}
$candidate = [IO.Path]::GetFullPath($CandidateDirectory)
$next = [IO.Path]::GetFullPath($NextDirectory)
$receipt = Get-Content -LiteralPath (Join-Path $candidate 'artifact-hashes.json') -Raw | ConvertFrom-Json
$nextInventory = Get-Content -LiteralPath (Join-Path $next 'publish-native.json') -Raw | ConvertFrom-Json
if ($receipt.sourceCommit -cne $SourceCommit -or $nextInventory.sourceCommit -cne $SourceCommit -or $receipt.version -ceq $nextInventory.version)
{
    throw 'Qualification requires identified candidate and later-version packages from the same fixed source.'
}
foreach ($row in $receipt.artifacts)
{
    $file = Join-Path $candidate ('release/' + $row.name)
    if ((Get-Item -LiteralPath $file).Length -ne $row.length -or (Get-FileHash -LiteralPath $file).Hash -cne $row.sha256) { throw 'Candidate artifact changed.' }
}
$installation = Join-Path $area 'installation'
$current = Join-Path $installation 'current'
[IO.Directory]::CreateDirectory($current) | Out-Null
$records = @{}
$passed = $false
$reopened = $null
$dotnet = (Get-Command dotnet).Source
$driver = Join-Path $root 'Postfold.Foundation.Tests/bin/Release/net10.0/Postfold.Foundation.Tests.dll'
$registrationPath = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/Postfold'
function Read-Registration
{
    if (-not (Test-Path -LiteralPath $registrationPath)) { return 'absent' }
    return (Get-ItemProperty -LiteralPath $registrationPath | Select-Object DisplayVersion, InstallLocation, UninstallString | ConvertTo-Json -Compress)
}
$registration = Read-Registration
function Observe-Children
{
    $snapshot = @(Get-CimInstance Win32_Process)
    do
    {
        $added = $false
        foreach ($child in $snapshot)
        {
            if ($records.ContainsKey([int]$child.ParentProcessId) -and -not $records.ContainsKey([int]$child.ProcessId) -and
                $child.CreationDate.ToUniversalTime() -ge $records[[int]$child.ParentProcessId].start)
            {
                $records[[int]$child.ProcessId] = @{pid=[int]$child.ProcessId;start=$child.CreationDate.ToUniversalTime();path=$child.ExecutablePath;parent=[int]$child.ParentProcessId}
                $added = $true
            }
        }
    } while ($added)
}
function Invoke-Driver([string]$Mode, [int]$Seconds = 180)
{
    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Arguments = '"' + $driver + '" --qualify-installed-update ' + $Mode + ' "' + (Join-Path $area 'specification.json') + '"'
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $null = $process.Handle
    $records[$process.Id] = @{pid=$process.Id;start=$process.StartTime.ToUniversalTime();path=$dotnet;mode=$Mode}
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try
    {
        $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
        while (-not $process.HasExited)
        {
            Observe-Children
            if ([DateTime]::UtcNow -ge $deadline) { throw ('Qualification driver deadline: ' + $Mode) }
            $null = $process.WaitForExit(200)
        }
        Observe-Children
        $process.WaitForExit()
        if (-not $stdout.Wait(10000) -or -not $stderr.Wait(10000)) { throw 'Driver output did not settle.' }
        [IO.File]::WriteAllText((Join-Path $area ($Mode + '.stdout.log')), $stdout.Result)
        [IO.File]::WriteAllText((Join-Path $area ($Mode + '.stderr.log')), $stderr.Result)
        $records[$process.Id].exit = $process.ExitCode
        if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath (Join-Path $area 'failure.json'))) { throw ('Qualification driver failed: ' + $Mode) }
    }
    finally
    {
        if (-not $process.HasExited) { $process.Kill(); if (-not $process.WaitForExit(10000)) { throw ('Owned driver remains: ' + $process.Id) } }
        $process.Dispose()
    }
}
try
{
    $package = @(Get-ChildItem -LiteralPath (Join-Path $candidate 'release') -File -Filter '*-full.nupkg')
    if ($package.Count -ne 1) { throw 'Exactly one candidate full package is required.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($package[0].FullName)
    try
    {
        foreach ($entry in $archive.Entries)
        {
            if (-not $entry.FullName.StartsWith('lib/app/', [StringComparison]::Ordinal) -or $entry.FullName.EndsWith('/')) { continue }
            $destination = [IO.Path]::GetFullPath((Join-Path $current $entry.FullName.Substring(8)))
            if (-not $destination.StartsWith($current + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes isolated current.' }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
        }
        $manifest = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') -and -not $_.FullName.Contains('/') })
        if ($manifest.Count -ne 1) { throw 'Candidate native identity is ambiguous.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($manifest[0], (Join-Path $current 'sq.version'), $false)
    }
    finally { $archive.Dispose() }
    [IO.File]::Copy((Join-Path $current 'Squirrel.exe'), (Join-Path $installation 'Update.exe'))
    # This marker suppresses uninstall registration/shortcut writes. The real native current-folder
    # replacement, process guards, old-process exit and normal reopening remain exercised unchanged.
    [IO.File]::WriteAllText((Join-Path $installation '.portable'), '')
    [IO.File]::WriteAllText((Join-Path $area '.synthetic-update-qualification'), $SourceCommit)
    $specification = [ordered]@{Area=$area;Installation=$installation;ReleaseDirectory=(Join-Path $next 'release');InstalledVersion=$receipt.version;SourceCommit=$SourceCommit}
    [IO.File]::WriteAllText((Join-Path $area 'specification.json'), ($specification | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Invoke-Driver 'prepare'
    Invoke-Driver 'run'
    $state = Join-Path $installation 'updates/win-x64/beta'
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    $events = @()
    while (-not $reopened)
    {
        Observe-Children
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Native apply/reopening deadline exceeded.' }
        if (Test-Path -LiteralPath (Join-Path $area 'restart-blocked.json')) { throw 'The production-layout restart was blocked.' }
        $logs = @(Get-ChildItem -LiteralPath $state -File -Filter '*.apply-*.log' -ErrorAction SilentlyContinue)
        $events = @($logs | ForEach-Object { Get-Content -LiteralPath $_.FullName })
        if (@($events | Where-Object { $_ -like 'refused:*' }).Count) { throw ('Native boundary refused: ' + ($events -join ', ')) }
        $line = @($events | Where-Object { $_ -like 'reopened:*' })
        if ($line.Count -eq 1)
        {
            $parts = $line[0].Split(':')
            $reopened = Get-Process -Id ([int]$parts[1])
            $null = $reopened.Handle
            if ($reopened.StartTime.ToUniversalTime().Ticks -ne [long]$parts[2] -or
                -not [string]::Equals($reopened.Path, (Join-Path $current 'Postfold.exe'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Normal reopening identity differs.' }
            $records[$reopened.Id] = @{pid=$reopened.Id;start=$reopened.StartTime.ToUniversalTime();path=$reopened.Path;mode='normal-reopen'}
        }
        else { Start-Sleep -Milliseconds 200 }
    }
    foreach ($milestone in @('verified', 'old-exited', 'profile-released', 'applying', ('installed:' + $nextInventory.version)))
    {
        if ($events -cnotcontains $milestone) { throw ('Missing real native milestone: ' + $milestone) }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $admitted = $false
    while (-not $admitted)
    {
        Observe-Children
        $reopened.Refresh()
        if ($reopened.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw 'Normal application did not admit the synthetic profile.' }
        try
        {
            $lease = [IO.File]::Open((Join-Path $installation 'profiles/default/profile.db.lease'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $lease.Dispose()
        }
        catch [IO.IOException] { $admitted = $reopened.MainWindowHandle -ne [IntPtr]::Zero }
        if (-not $admitted) { $null = $reopened.WaitForExit(100) }
    }
    if (-not $reopened.CloseMainWindow() -or -not $reopened.WaitForExit(30000)) { throw 'Normal qualification app did not close normally.' }
    $records[$reopened.Id].exit = $reopened.ExitCode
    Invoke-Driver 'audit'
    if ((Read-Registration) -cne $registration) { throw 'Real Postfold uninstall registration changed.' }
    $passed = $true
    Write-Output 'PASS: candidate production layout checked/downloaded, safely restarted through real native apply, reopened the normal target app, and preserved dirty draft plus queued/uncertain work. Real registration unchanged.'
}
finally
{
    Observe-Children
    $remaining = @()
    foreach ($record in $records.Values)
    {
        $process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        $processStart = if ($process) { $process.StartTime } else { $null }
        if ($process -and $null -eq $processStart)
        {
            if (Get-CimInstance Win32_Process -Filter ('ProcessId=' + $record.pid)) { $remaining += $record.pid }
            continue
        }
        if ($process -and [Math]::Abs(($processStart.ToUniversalTime() - $record.start).TotalSeconds) -lt 1)
        {
            $record.forcedCleanup = $true
            Stop-Process -Id $record.pid -Force -ErrorAction SilentlyContinue
            if (-not $process.WaitForExit(10000)) { $remaining += $record.pid }
            $process.Dispose()
        }
    }
    if ($reopened) { $reopened.Dispose() }
    $audit = [ordered]@{passed=$passed;sourceCommit=$SourceCommit;candidateVersion=$receipt.version;nextVersion=$nextInventory.version;candidatePackageSha256=$(if ($package) { (Get-FileHash -LiteralPath $package[0].FullName).Hash });registrationUnchanged=((Read-Registration) -ceq $registration);installerExecuted=$false;portableIsolation=$true;owned=@($records.Values);remainingOwned=$remaining;completed=[DateTime]::UtcNow}
    [IO.File]::WriteAllText((Join-Path $area 'qualification-receipt.json'), ($audit | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    if ($remaining.Count) { throw ('Owned qualification processes remain: ' + ($remaining -join ', ')) }
}
