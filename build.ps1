$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $csc)) {
    throw "Cannot find csc.exe at $csc"
}

$files = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter '*.cs' |
         ForEach-Object { $_.FullName }

if ($files.Count -eq 0) { throw "No .cs files found" }

$opts = @(
    '/codepage:65001',
    '/optimize+',
    '/nologo',
    '/r:System.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll'
)

$gui  = Join-Path $root 'Voltide.exe'
$test = Join-Path $root 'Voltide-Test.exe'
$ico  = Join-Path $root 'app.ico'

# app.ico is the masked reference plate at 16,20,24,32,48,64,128,256.
# /win32icon embeds that group so Explorer and the taskbar pick a size.
# The same PNGs are manifest resources: the full-charge tray icon loads the
# frame closest to the current DPI (24 px at 150%). Missing file: build still succeeds.
$iconOpt = @()
if (Test-Path $ico) { $iconOpt = @("/win32icon:$ico") }

# Typefaces travel inside the exe, so a single file is enough to distribute.
# Files are staged under ASCII names first: csc's /resource switch mangles
# non-ASCII paths, and one of these is 汉仪旗黑.
#
# Fonts/ is gitignored: the licence does not allow shipping the files with the
# source. A missing face used to be skipped, and a clean tree then built an exe
# with no embedded typeface and no error. Fail here instead.
$resOpt = @()
$fontDir = Join-Path $root 'Fonts'
$staging = Join-Path $root 'obj'
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# The whole UI is unified on MiSans Medium (crisper than Regular at menu sizes);
# MiSans-Digits is a 12 KB subset (0-9 and %) reserved for the tray icon.
$faces = @(
    @{ Source = 'MiSans-Medium.ttf'; Embed = 'MiSans-Medium.ttf' },
    @{ Source = 'MiSans-Digits.ttf'; Embed = 'MiSans-Digits.ttf' }
)

foreach ($face in $faces) {
    $src = Join-Path $fontDir $face.Source
    if (-not (Test-Path $src)) {
        throw "Missing font: $src. Fonts\ is not in the repository. Place MiSans-Medium.ttf and MiSans-Digits.ttf in Fonts\ before building. The build stops so a checkout without those files cannot ship an exe that silently falls back to another face."
    }

    $staged = Join-Path $staging $face.Embed
    Copy-Item $src $staged -Force
    $resOpt += "/resource:$staged,$($face.Embed)"
}

$iconDir = Join-Path $root 'icons'
foreach ($px in 16, 20, 24, 32, 48, 64, 128, 256) {
    $frame = Join-Path $iconDir "app-icon-$px.png"
    if (Test-Path $frame) { $resOpt += "/resource:${frame},appicon-$px.png" }
}

Write-Host "Compiling GUI ($($files.Count) files)..."

& $csc /target:winexe /out:$gui @opts @iconOpt @resOpt $files
if ($LASTEXITCODE -ne 0) { throw "GUI compile failed" }

Write-Host "Compiling console test build..."

& $csc /target:exe /out:$test @opts @iconOpt @resOpt $files
if ($LASTEXITCODE -ne 0) { throw "Test compile failed" }

Write-Host ""
Write-Host ("GUI  -> {0}  ({1:N0} bytes)" -f $gui,  (Get-Item $gui).Length)
Write-Host ("Test -> {0}  ({1:N0} bytes)" -f $test, (Get-Item $test).Length)
