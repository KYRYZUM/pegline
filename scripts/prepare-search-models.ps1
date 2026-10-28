<#
.SYNOPSIS
  Prepares the offline visual search models (Apple MobileCLIP-S2, ONNX FP32) in assets\CLIP.

.DESCRIPTION
  Downloads the pinned archive, checks its size and SHA-256 before extracting, then checks every file against
  src\Pegline.Core\Search\search-model-config.json. The models are bundled at build time; the running app never
  downloads them or sends queries anywhere. Builds without them work; visual search is then unavailable.

  -Check only verifies an existing assets\CLIP folder.
#>
param([switch]$Check)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$config = Get-Content (Join-Path $root 'src\Pegline.Core\Search\search-model-config.json') -Raw | ConvertFrom-Json
$target = Join-Path $root 'assets\CLIP'

function Test-Models([string]$folder) {
    foreach ($entry in $config.files.PSObject.Properties) {
        $file = Join-Path $folder $entry.Name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing $($entry.Name)" }
        $item = Get-Item -LiteralPath $file
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$($entry.Name) is a link" }
        if ($item.Length -ne $entry.Value.bytes) { throw "$($entry.Name) has the wrong size" }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value.sha256.ToUpperInvariant()) { throw "$($entry.Name) differs from the pinned file" }
    }
}

if ($Check) {
    Test-Models $target
    Write-Host "assets\CLIP matches $($config.version)."
    exit 0
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("pegline-models-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $archive = Join-Path $work 'models.zip'
    Write-Host "Downloading $($config.archive.url) ($([math]::Round($config.archive.bytes / 1MB)) MB)…"
    Invoke-WebRequest -Uri $config.archive.url -OutFile $archive -UseBasicParsing
    if ((Get-Item $archive).Length -ne $config.archive.bytes) { throw 'The archive has the wrong size.' }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $config.archive.sha256.ToUpperInvariant()) { throw 'The archive differs from the pinned one.' }

    # Only the pinned names are extracted; nothing else in the archive is written.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $extracted = Join-Path $work 'CLIP'
    New-Item -ItemType Directory -Path $extracted | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $names = @($config.files.PSObject.Properties.Name)
        if ($zip.Entries.Count -ne $names.Count) { throw 'The archive holds unexpected files.' }
        foreach ($entry in $zip.Entries) {
            if ($names -notcontains $entry.FullName) { throw "Unexpected archive entry: $($entry.FullName)" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $extracted $entry.FullName))
        }
    } finally { $zip.Dispose() }
    Test-Models $extracted

    if (Test-Path $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Move-Item -LiteralPath $extracted -Destination $target
    Write-Host "Prepared $($config.version) in assets\CLIP."
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
