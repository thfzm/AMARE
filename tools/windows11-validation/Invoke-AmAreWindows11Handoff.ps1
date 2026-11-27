param(
    [string]$PackageZip = "",
    [switch]$RunUiSmoke
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($PackageZip)) {
    $PackageZip = Get-ChildItem -LiteralPath $root -Filter "AMARE-*-self-contained-unsigned.zip" -File |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($PackageZip) -or -not (Test-Path -LiteralPath $PackageZip)) {
    throw "Self-contained ZIP was not found next to this script. Place AMARE-0.92.0-win-x64-self-contained-unsigned.zip in the same folder."
}

$sidecar = "$PackageZip.sha256"
if (-not (Test-Path -LiteralPath $sidecar)) {
    throw "SHA-256 sidecar is missing: $sidecar"
}
$expected = ((Get-Content -LiteralPath $sidecar -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$stream = [System.IO.File]::OpenRead($PackageZip)
try {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace("-", "").ToLowerInvariant() }
    finally { $sha.Dispose() }
}
finally { $stream.Dispose() }
if ($actual -ne $expected) {
    throw "ZIP SHA-256 does not match its sidecar. Re-copy the handoff folder."
}

$bundle = Join-Path $root "bundle-manifest.json"
if (Test-Path -LiteralPath $bundle) {
    $manifest = Get-Content -LiteralPath $bundle -Raw | ConvertFrom-Json
    if ($manifest.package.sha256 -ne $actual) {
        throw "bundle-manifest.json package SHA-256 does not match the ZIP."
    }
    if ($manifest.validation_script_sha256) {
        $scriptPath = Join-Path $root "Invoke-AmAreWindows11Validation.ps1"
        $scriptStream = [System.IO.File]::OpenRead($scriptPath)
        try {
            $scriptSha = [System.Security.Cryptography.SHA256]::Create()
            try { $scriptActual = ([BitConverter]::ToString($scriptSha.ComputeHash($scriptStream))).Replace("-", "").ToLowerInvariant() }
            finally { $scriptSha.Dispose() }
        }
        finally { $scriptStream.Dispose() }
        if ($scriptActual -ne $manifest.validation_script_sha256) {
            throw "validation script SHA-256 does not match bundle-manifest.json."
        }
    }
    Write-Host ("Bundle: {0}; app_head={1}; package_sha256={2}" -f $manifest.phase, $manifest.app_head, $actual)
}

Write-Host ""
Write-Host "AMARE Windows 11 validation handoff" -ForegroundColor Cyan
Write-Host "Package: $PackageZip"
Write-Host "SHA-256: $actual"
Write-Host "The script will use isolated AMARE_DATA_ROOT test roots and will not use %LOCALAPPDATA%\\AMARE user data."
Write-Host "WebView2 Evergreen Runtime is required. No administrator elevation is requested."
Write-Host ""

$main = Join-Path $root "Invoke-AmAreWindows11Validation.ps1"
if (-not (Test-Path -LiteralPath $main)) {
    throw "Invoke-AmAreWindows11Validation.ps1 is missing from the handoff folder."
}
$parameters = @{ PackageZip = $PackageZip }
if ($RunUiSmoke) { $parameters.RunUiSmoke = $true }
& $main @parameters
exit $LASTEXITCODE
