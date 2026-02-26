param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot ".")).Path
)

$ErrorActionPreference = "Stop"

$targetFile = Join-Path $ProjectRoot "addons\gdUnit4\src\core\GdUnitFileAccess.gd"
$pluginCfg = Join-Path $ProjectRoot "addons\gdUnit4\plugin.cfg"
$versionLock = Join-Path $ProjectRoot "addons\gdunit4.version"

if (-not (Test-Path $targetFile)) {
    Write-Error "gdUnit4 hotfix target not found: $targetFile"
    exit 1
}

if (-not (Test-Path $pluginCfg)) {
    Write-Warning "plugin.cfg not found: $pluginCfg"
} else {
    $pluginText = Get-Content -Path $pluginCfg -Raw -Encoding UTF8
    if ($pluginText -notmatch 'version="6\.0\.0"') {
        Write-Warning "This script was validated for gdUnit4 version 6.0.0. Please verify compatibility."
    }
}

$source = Get-Content -Path $targetFile -Raw -Encoding UTF8
$old = "return file.get_as_text(true)"
$new = "return file.get_as_text()"

if ($source.Contains($new)) {
    Write-Output "Hotfix already applied: $targetFile"
} elseif ($source.Contains($old)) {
    $patched = $source.Replace($old, $new)
    Set-Content -Path $targetFile -Value $patched -Encoding UTF8
    Write-Output "Hotfix applied: $targetFile"
} else {
    Write-Error "Expected source pattern not found in $targetFile"
    exit 2
}

if (Test-Path $versionLock) {
    Write-Output "Version lock: $(Get-Content -Path $versionLock -Raw -Encoding UTF8)"
}
