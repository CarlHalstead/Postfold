param(
    [Parameter(Mandatory)][string]$BrokerPath,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$SourceExportSha256,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) { throw 'Preserve existing inventories; select a fresh output.' }
if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $SourceExportSha256 -cnotmatch '^[0-9A-F]{64}$') { throw 'Exact source identities are required.' }
$broker = Get-Item -LiteralPath $BrokerPath
$identity = [Diagnostics.FileVersionInfo]::GetVersionInfo($broker.FullName).ProductVersion
if ($identity -cne "$Version+$SourceCommit" -or $broker.Length -le 1048576 -or $broker.Length -gt 134217728) { throw 'A matching self-contained single-file broker is required.' }
# The pinned vpk owns this native helper; never select an adjacent/downloaded replacement.
$helper = Join-Path $env:USERPROFILE '.nuget/packages/vpk/1.2.161/vendor/update_x64.exe'
$helperVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($helper).ProductVersion
if ($helperVersion -notmatch '^1\.2\.161(\+|$)') { throw 'Native helper version mismatch.' }
# vpk changes the helper's icon before storing it as lib/app/Squirrel.exe. Reuse that exact
# pinned resource editor, not the unmodified vendor hash or an earlier qualification helper.
$preparedHelper = $OutputPath + '-helper.exe'
if (Test-Path -LiteralPath $preparedHelper) { throw 'Preserve existing helper preparation.' }
[IO.File]::Copy($helper, $preparedHelper)
$toolDirectory = Join-Path $env:USERPROFILE '.nuget/packages/vpk/1.2.161/tools/net10.0/any'
$context = [Runtime.Loader.AssemblyLoadContext]::new('PostfoldHelperPreparation', $true)
$resolver = [Func[Runtime.Loader.AssemblyLoadContext,Reflection.AssemblyName,Reflection.Assembly]]{
    param($loadContext, $assemblyName)
    $dependency = Join-Path $toolDirectory ($assemblyName.Name + '.dll')
    if (Test-Path -LiteralPath $dependency) { return $loadContext.LoadFromAssemblyPath($dependency) }
    return $null
}
$context.add_Resolving($resolver)
try {
    $logging = $context.LoadFromAssemblyPath((Join-Path $toolDirectory 'Microsoft.Extensions.Logging.Abstractions.dll'))
    $logger = $logging.GetType('Microsoft.Extensions.Logging.Abstractions.NullLogger').GetProperty('Instance').GetValue($null)
    $packaging = $context.LoadFromAssemblyPath((Join-Path $toolDirectory 'Velopack.Packaging.Windows.dll'))
    $editorType = $packaging.GetType('Velopack.Packaging.Windows.ResourceEdit', $true)
    [object[]]$constructorArguments = [object[]]::new(2)
    $constructorArguments[0] = $preparedHelper.PSObject.BaseObject
    $constructorArguments[1] = $logger.PSObject.BaseObject
    $editor = $editorType.GetConstructors()[0].Invoke($constructorArguments)
    $icon = Join-Path (Split-Path -Parent $PSScriptRoot) 'Postfold.UI/Resources/Postfold.ico'
    $editor.SetExeIcon($icon)
    $editor.Commit()
}
finally {
    $context.remove_Resolving($resolver)
    $context.Unload()
}
$receipt = [ordered]@{
    product = 'Postfold'
    rid = 'win-x64'
    channel = 'beta'
    keyId = 'A022E28E638426A0901C393CE163EF06246066CB5E3496B6DCF4965560E4CFE4'
    version = $Version
    sourceCommit = $SourceCommit
    sourceExportSha256 = $SourceExportSha256
    brokerSha256 = (Get-FileHash -LiteralPath $broker.FullName).Hash
    helperSha256 = (Get-FileHash -LiteralPath $preparedHelper).Hash
    helperVendorSha256 = (Get-FileHash -LiteralPath $helper).Hash
    helperIconSha256 = (Get-FileHash -LiteralPath $icon).Hash
    velopack = '1.2.161'
}
[IO.File]::WriteAllText($OutputPath, (($receipt | ConvertTo-Json) + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output "Prepared dormant native inventory: $OutputPath"
