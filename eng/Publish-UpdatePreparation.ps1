param(
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SourceExportManifest,
    [string]$SourceExportSha256
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sourceArguments = @{ SourceCommit=$SourceCommit; SourceExportManifest=$SourceExportManifest; SourceExportSha256=$SourceExportSha256 }
$files = @(& (Join-Path $PSScriptRoot 'Test-DraftSource.ps1') @sourceArguments)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$broker = $output + '-broker'
$inventory = $output + '-native.json'
if ((Test-Path $output) -or (Test-Path $broker) -or (Test-Path $inventory)) { throw 'Select fresh output paths; preserve existing builds.' }
Set-Location -LiteralPath $root
function Invoke-Dotnet([string[]]$Arguments)
{
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
Invoke-Dotnet -Arguments @('publish','Postfold.UpdateBroker','-c','Release','-r','win-x64','--self-contained','true','-p:RestoreLockedMode=true','-p:RuntimeFrameworkVersion=10.0.13','-p:PublishTrimmed=false','-p:PublishAot=false',"-p:Version=$Version","-p:SourceRevisionId=$SourceCommit",'--disable-build-servers','-p:UseSharedCompilation=false','-nr:false','-o',$broker)
if (-not $SourceExportSha256) {
    $sourceRows = @($files | Sort-Object | ForEach-Object { $_ + ':' + (Get-FileHash -LiteralPath (Join-Path $root $_)).Hash })
    $SourceExportSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($sourceRows -join "`n"))))
}
& (Join-Path $PSScriptRoot 'New-ProductionNativeInventory.ps1') -BrokerPath (Join-Path $broker 'Postfold.UpdateBroker.exe') -SourceCommit $SourceCommit -Version $Version -SourceExportSha256 $SourceExportSha256 -OutputPath $inventory
Invoke-Dotnet -Arguments @('restore','Postfold.Desktop','-p:RuntimeIdentifier=win-x64','-p:SelfContained=true','--locked-mode','--disable-build-servers','-nr:false')
Invoke-Dotnet -Arguments @('publish','Postfold.Desktop','-c','Release','-r','win-x64','--self-contained','true','--no-restore','-p:RuntimeFrameworkVersion=10.0.13','-p:PublishTrimmed=false','-p:PublishAot=false',"-p:Version=$Version","-p:SourceRevisionId=$SourceCommit","-p:ProductionNativeInventory=$inventory",'--disable-build-servers','-p:UseSharedCompilation=false','-nr:false','-o',$output)
[IO.File]::Copy((Join-Path $broker 'Postfold.UpdateBroker.exe'), (Join-Path $output 'Postfold.UpdateBroker.exe'))
[IO.File]::Copy((Join-Path $root 'LICENSE'), (Join-Path $output 'POSTFOLD-LICENSE.txt'))
[IO.File]::Copy((Join-Path $root 'THIRD-PARTY-NOTICES.md'), (Join-Path $output 'POSTFOLD-NOTICES.md'))
Copy-Item -LiteralPath (Join-Path $root 'ThirdPartyNotices') -Destination $output -Recurse
& (Join-Path $PSScriptRoot 'Test-PublishInventory.ps1') -PublishDirectory $output -ProductionPreparation
& (Join-Path $PSScriptRoot 'Test-DraftSource.ps1') @sourceArguments | Out-Null
Write-Output 'Dormant production preparation published; no installer, signing or updater activation.'
