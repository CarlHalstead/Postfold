param([switch]$IncludeUntracked, [string[]]$SourceFiles)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$trackedFiles = if ($SourceFiles)
{
    $SourceFiles
}
elseif ($IncludeUntracked)
{
    git -C $repositoryRoot ls-files --cached --others --exclude-standard
}
else
{
    git -C $repositoryRoot ls-files
}
if (-not $SourceFiles -and $LASTEXITCODE -ne 0)
{
    throw 'Cannot inspect tracked source.'
}

foreach ($relativePath in $trackedFiles)
{
    if ($relativePath -match '(?i)(^|/)(AGENTS\.md|SKILL\.md|skills|\.agents|\.codex|\.aws|\.claude|\.vs|\.env|Postfold-Planning)(/|$)')
    {
        throw "Private resource is tracked: $relativePath"
    }

    if ($relativePath -match '(?i)(^|/)(bin|obj|TestResults|artifacts)(/|$)')
    {
        throw "Build output is tracked: $relativePath"
    }

    $filePath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf))
    {
        # Working-tree moves retain their old index paths until staging.
        continue
    }

    if ([System.IO.Path]::GetExtension($filePath) -eq '.cs')
    {
        $parentPath = Split-Path -Parent $filePath
        $fileName = Split-Path -Leaf $filePath
        $projectFiles = @(Get-ChildItem -LiteralPath $parentPath -Filter '*.csproj' -File)
        if ($projectFiles.Count -gt 0 -and $fileName -cnotin @('Program.cs', 'App.cs', 'AssemblyInfo.cs'))
        {
            throw "Authored C# file is forbidden at a project root: $relativePath"
        }
    }
    if ([System.IO.Path]::GetExtension($filePath) -eq '.ps1')
    {
        $scriptTokens = $null
        $scriptErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($filePath, [ref]$scriptTokens, [ref]$scriptErrors) | Out-Null
        if ($scriptErrors.Count -gt 0)
        {
            throw "PowerShell syntax errors in $relativePath : $($scriptErrors.Message -join '; ')"
        }
    }

    if ($relativePath -notmatch '\.(cs|csproj|props|targets|slnx|axaml|resx|manifest|json|ya?ml|ps1|md)$' -and
        $relativePath -notin @('LICENSE', '.editorconfig', '.gitattributes', 'NuGet.Config'))
    {
        continue
    }

    $bytes = [System.IO.File]::ReadAllBytes($filePath)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    {
        throw "UTF-8 BOM is forbidden: $relativePath"
    }

    $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    # Velopack feed bytes are authenticated by the signed manifest; never normalise packer output.
    $exactReleaseFeed = $relativePath -cmatch '\Aupdates/releases/v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-beta\.[1-9][0-9]*)?/win-x64-beta/releases\.win-x64-beta\.json\z'
    if (-not $exactReleaseFeed -and ($text.Contains("`r") -or ($bytes.Length -gt 0 -and -not $text.EndsWith("`n"))))
    {
        throw "Expected LF and final newline: $relativePath"
    }
}

Write-Output 'Source privacy, project-root layout, UTF-8, LF and final newline checks passed.'
