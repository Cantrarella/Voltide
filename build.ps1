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

# app.ico is drawn by IconFactory (Voltide.exe --make-icon), one native bitmap
# per size. /win32icon embeds that group so Explorer and the taskbar pick a
# size instead of scaling a single image. Missing file: build still succeeds.
$iconOpt = @()
if (Test-Path $ico) { $iconOpt = @("/win32icon:$ico") }

# Typefaces travel inside the exe, so a single file is enough to distribute.
# Files are staged under ASCII names first: csc's /resource switch mangles
# non-ASCII paths, and one of these is 汉仪旗黑.
$resOpt = @()
$fontDir = Join-Path $root 'Fonts'
if (Test-Path $fontDir) {
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
        if (-not (Test-Path $src)) { continue }

        $staged = Join-Path $staging $face.Embed
        Copy-Item $src $staged -Force
        $resOpt += "/resource:$staged,$($face.Embed)"
    }
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
