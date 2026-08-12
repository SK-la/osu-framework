# Build plutosvgft (FreeType OT-SVG hooks) and copy into osu.Framework/runtimes.
# Requires: portable toolchain under repo .tools/ (cmake + llvm-mingw and/or zig),
# or system cmake + a C compiler. See Native/PlutoSvgFt/NOTICE.
#
# Usage (from repo root):
#   powershell -File osu.Framework/Native/PlutoSvgFt/scripts/Build-PlutoSvgFt.ps1
#   powershell -File osu.Framework/Native/PlutoSvgFt/scripts/Build-PlutoSvgFt.ps1 -Target linux-x64

param(
    [ValidateSet("win-x64", "linux-x64")]
    [string]$Target = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")
$nativeRoot = Join-Path $root "osu.Framework\Native\PlutoSvgFt"
$outDir = Join-Path $root "osu.Framework\runtimes\$Target\native"
$cmake = Join-Path $root ".tools\cmake\bin\cmake.exe"
$ninja = Join-Path $root ".tools\ninja\ninja.exe"

if (-not (Test-Path $cmake)) {
    $cmakeCmd = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmakeCmd) { $cmake = $cmakeCmd.Source } else { throw "cmake not found. Place portable CMake under .tools/cmake or install cmake." }
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if ($Target -eq "win-x64") {
    $buildDir = Join-Path $nativeRoot "build-win-x64"
    New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

    $llvmBin = Join-Path $root ".tools\llvm-mingw\bin"
    if (-not (Test-Path (Join-Path $llvmBin "clang.exe"))) {
        throw "llvm-mingw not found under .tools/llvm-mingw (needed for win-x64)."
    }

    $env:PATH = "$llvmBin;" + $env:PATH
    $configureArgs = @(
        "-S", $nativeRoot,
        "-B", $buildDir,
        "-G", "Ninja",
        "-DCMAKE_BUILD_TYPE=Release",
        "-DCMAKE_C_COMPILER=clang",
        "-DCMAKE_MAKE_PROGRAM=$ninja"
    )
    if (-not (Test-Path $ninja)) { $configureArgs = @("-S", $nativeRoot, "-B", $buildDir, "-DCMAKE_BUILD_TYPE=Release") }

    & $cmake @configureArgs
    & $cmake --build $buildDir --config Release
    Copy-Item (Join-Path $buildDir "plutosvgft.dll") (Join-Path $outDir "plutosvgft.dll") -Force
    Write-Host "Wrote $(Join-Path $outDir 'plutosvgft.dll')"
}
elseif ($Target -eq "linux-x64") {
    Write-Host "linux-x64: use Zig cross-build helper if present, else document CI build."
    $zig = Get-ChildItem (Join-Path $root ".tools") -Recurse -Filter "zig.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $zig) { throw "zig.exe not found under .tools for linux-x64 cross-compile." }

    # Expect a previously prepared object amalgamation or rebuild via cmake with zig cc — see agent notes.
    $so = Join-Path $outDir "libplutosvgft.so"
    if (-not (Test-Path $so)) {
        throw "libplutosvgft.so missing at $so. Re-run the Zig cross-compile steps used to produce it (see repo history / Native/PlutoSvgFt)."
    }
    Write-Host "Found $so"
}
