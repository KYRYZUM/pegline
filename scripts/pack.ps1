<#
.SYNOPSIS
  Builds the Pegline installer (Setup.exe), the portable zip and the update packages with Velopack.

.DESCRIPTION
  Publishes a self-contained win-x64 build with the visual search models, then packs it into Releases\.
  The package id is "PeglineApp": Velopack installs to %LocalAppData%\<packId>, and %LocalAppData%\Pegline
  already holds the user's library, which an uninstall must never touch.

  Code signing is optional: pass -SignParams with signtool.exe arguments once a certificate exists.

.EXAMPLE
  pwsh scripts/pack.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Output = 'Releases',
    [string]$SignParams = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    # An installer without visual search would silently ship a weaker app.
    & (Join-Path $PSScriptRoot 'prepare-search-models.ps1') -Check

    $publish = Join-Path $root 'publish\win-x64'
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish src/Pegline.App -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    if (-not (Test-Path (Join-Path $publish 'CLIP\model-source.json'))) { throw 'The published app is missing the visual search models' }

    dotnet tool restore
    $pack = @(
        'vpk', 'pack',
        '--packId', 'PeglineApp',
        '--packTitle', 'Pegline',
        '--packAuthors', 'Pegline',
        '--packVersion', $Version,
        '--packDir', $publish,
        '--mainExe', 'Pegline.exe',
        '--runtime', 'win-x64',
        '--icon', 'assets\brand\pegline.ico',
        '--splashImage', 'assets\brand\splash.png',
        '--splashProgressColor', '#FF8A1F',
        '--outputDir', $Output
    )
    if ($SignParams) { $pack += @('--signParams', $SignParams) }
    dotnet @pack
    if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
    Get-ChildItem $Output | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
}
finally {
    Pop-Location
}
