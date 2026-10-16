param([Parameter(Mandatory)][string]$PublishDirectory)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$expected = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'win-x64-publish-files.json') -Raw | ConvertFrom-Json)
$actual = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | ForEach-Object {
    [System.IO.Path]::GetRelativePath($PublishDirectory, $_.FullName).Replace('\', '/')
})
$difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual -CaseSensitive)
if ($difference.Count -ne 0)
{
    throw "Publish allowlist mismatch: $($difference | ConvertTo-Json -Compress)"
}
foreach ($entry in (Get-ChildItem -LiteralPath (Join-Path $PublishDirectory 'ThirdPartyNotices') -Recurse -File))
{
    $relative = [System.IO.Path]::GetRelativePath($PublishDirectory, $entry.FullName)
    if ((Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath (Join-Path $root $relative) -Algorithm SHA256).Hash)
    {
        throw "Shipping notice bytes changed: $relative"
    }
}
Write-Output "Exact Windows publish allowlist passed ($($actual.Count) files)."
