# Verify ez2lazer.Framework nupkg contains plutosvgft RID assets required for OT-SVG emoji.
# Usage:
#   powershell -File scripts/Verify-FrameworkNupkgPlutoSvg.ps1 -Nupkg path\to\ez2lazer.Framework.*.nupkg

param(
    [Parameter(Mandatory = $true)]
    [string]$Nupkg
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Nupkg)) { throw "Nupkg not found: $Nupkg" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $Nupkg))
try {
    $names = $zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }
    $required = @(
        "runtimes/win-x64/native/plutosvgft.dll",
        "runtimes/linux-x64/native/libplutosvgft.so"
    )
    $missing = @()
    foreach ($r in $required) {
        if (-not ($names | Where-Object { $_ -eq $r -or $_.EndsWith($r) })) {
            $missing += $r
        }
    }
    if ($missing.Count -gt 0) {
        Write-Host "Entries sample:"
        $names | Where-Object { $_ -match "runtimes|plutosvg" } | ForEach-Object { Write-Host "  $_" }
        throw "Missing required nupkg assets:`n - $($missing -join "`n - ")"
    }
    Write-Host "OK: plutosvgft win-x64 + linux-x64 present in $Nupkg"
}
finally {
    $zip.Dispose()
}
