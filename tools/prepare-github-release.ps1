param(
    [string]$Version = "0.22.0",
    [string]$Repository = "thfzm/AMARE",
    [switch]$CreateDraft
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$releaseDir = Join-Path (Get-Location) "artifacts\windows"
if (-not (Test-Path (Join-Path $releaseDir "AMARE-$Version-win-x64.zip"))) {
    $releaseDir = Join-Path $releaseDir "release"
}
$portable = Join-Path $releaseDir "AMARE-$Version-win-x64.zip"
$checksum = "$portable.sha256"
$msix = Join-Path $releaseDir "AMARE-$Version-win-x64-unsigned.msix"
$checksums = Join-Path $releaseDir "checksums.txt"
if (-not (Test-Path $checksums)) {
    $checksums = Join-Path $releaseDir "AMARE-$Version-checksums.txt"
}
$manifest = Join-Path $releaseDir "AMARE-$Version-release-manifest.json"
$notes = Join-Path (Get-Location) "docs\releases\AMARE-$Version.md"

foreach ($path in @($portable, $checksum, $msix, $checksums, $manifest, $notes)) {
    if (-not (Test-Path $path)) { throw "Release input is missing: $path" }
}

$actual = (Get-FileHash $portable -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = ((Get-Content $checksum -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
if ($actual -ne $expected) { throw "Portable checksum sidecar does not match the ZIP." }
$releaseData = Get-Content $manifest -Raw | ConvertFrom-Json
if ($releaseData.version -ne $Version) { throw "Release manifest version mismatch." }
if ($releaseData.architecture -ne "win-x64") { throw "Release manifest architecture mismatch." }
$assetNames = @($releaseData.assets | ForEach-Object { $_.name })
if (@($assetNames | Select-Object -Unique).Count -ne $assetNames.Count) { throw "Duplicate release assets." }
foreach ($asset in $releaseData.assets) {
    $path = Join-Path $releaseDir $asset.name
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $asset.sha256) { throw "Release manifest hash mismatch: $($asset.name)" }
}

$tag = "v$Version"
$title = "AMARE $Version"
$assets = @($portable, $checksum, $msix, $checksums, $manifest)
$command = @("release", "create", $tag, "--repo", $Repository, "--draft", "--title", $title, "--notes-file", $notes) + $assets

if (-not $CreateDraft) {
    Write-Host "Release draft is prepared but was not sent to GitHub."
    Write-Host "Public publishing requires explicit approval."
    Write-Host ("gh " + ($command -join " "))
    exit 0
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is required to create the draft release."
}
& gh release view $tag --repo $Repository 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    throw "Release $tag already exists. Refusing to create a duplicate draft."
}
& gh @command
if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE." }
Write-Host "Draft release created: $tag (not published)"
