# Ensure we're running from the correct directory (location of this file).
Set-Location -LiteralPath $PSScriptRoot

$ErrorActionPreference = 'Stop'

$zipPath = Join-Path $env:TEMP 'bassasio.zip'

try {
    Invoke-WebRequest -Uri 'https://www.un4seen.com/stuff/bassasio.zip' -OutFile $zipPath

    $destX64 = Join-Path $PSScriptRoot 'runtimes\win-x64\native'
    $destX86 = Join-Path $PSScriptRoot 'runtimes\win-x86\native'

    New-Item -ItemType Directory -Force -Path $destX64 | Out-Null
    New-Item -ItemType Directory -Force -Path $destX86 | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)

    try {
        foreach ($entry in $archive.Entries) {
            switch ($entry.FullName) {
                'x64/bassasio.dll' {
                    $out = Join-Path $destX64 'bassasio.dll'
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out, $true)
                }
                'bassasio.dll' {
                    $out = Join-Path $destX86 'bassasio.dll'
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out, $true)
                }
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
}
