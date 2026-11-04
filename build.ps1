param([switch]$Test, [switch]$Package)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$wpf = Join-Path $framework 'WPF'
$output = Join-Path $root 'dist'
$obj = Join-Path $root 'obj'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The Windows .NET Framework 4.x compiler was not found.' }
if ($Test -or $Package) {
    if (-not (Get-Command node -CommandType Application -ErrorAction SilentlyContinue)) { throw 'Node.js 22 or later is required for extension tests.' }
    $nodeVersion = & node --version
    if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt 22) { throw 'Node.js 22 or later is required for extension tests.' }
}
New-Item -ItemType Directory -Force -Path $output, $obj, (Join-Path $root 'extension\icons') | Out-Null
# Remove stale release assets even when a later build or test fails.
$zip = Join-Path $output 'Usugami-windows.zip'
$checksum = $zip + '.sha256'
foreach ($asset in @($zip, $checksum)) { if (Test-Path -LiteralPath $asset) { Remove-Item -LiteralPath $asset -Force } }

function Reset-GeneratedDirectory([string]$path) {
    $resolved = [System.IO.Path]::GetFullPath($path)
    $workspace = [System.IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($workspace, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Output directory is outside the workspace: $path" }
    if (Test-Path -LiteralPath $resolved) {
        # Refuse links/junctions in generated trees before recursive deletion.
        $entries = @(Get-Item -LiteralPath $output) + @(Get-Item -LiteralPath $resolved) + @(Get-ChildItem -LiteralPath $resolved -Force -Recurse)
        if ($entries | Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 }) { throw "Generated directory contains a link: $path" }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolved | Out-Null
}

# App / tray / extension icon: gradient tile with two ink waves (the "layers").
Add-Type -AssemblyName System.Drawing
function New-LogoPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $r = 66 * $s; $max = $size - 1
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc(0, 0, $r, $r, 180, 90); $tile.AddArc($max - $r, 0, $r, $r, 270, 90)
    $tile.AddArc($max - $r, $max - $r, $r, $r, 0, 90); $tile.AddArc(0, $max - $r, $r, $r, 90, 90); $tile.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), [System.Drawing.Color]::FromArgb(255, 99, 102, 241), [System.Drawing.Color]::FromArgb(255, 34, 211, 238))
    $g.FillPath($fill, $tile)
    foreach ($wave in @(@(120, 128, 26), @(255, 176, 30))) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($wave[0], 255, 255, 255)), ([Math]::Max(1.4, $wave[2] * $s))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round; $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $y = $wave[1]
        $g.DrawBezier($pen, 62 * $s, $y * $s, 100 * $s, ($y - 58) * $s, 150 * $s, ($y + 22) * $s, 196 * $s, ($y - 36) * $s)
        $pen.Dispose()
    }
    $fill.Dispose(); $tile.Dispose()
    $g.Dispose()
    $memory = New-Object System.IO.MemoryStream
    $bmp.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    $bytes = $memory.ToArray(); $memory.Dispose()
    return , $bytes
}
$sizes = @(16, 24, 32, 48, 64, 256)
$images = @(); foreach ($size in $sizes) { $images += , (New-LogoPng $size) }
$icon = Join-Path $obj 'usugami.ico'
$writer = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($icon))
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = [byte]($sizes[$i] % 256)
    $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32); $writer.Write([UInt32]$images[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Close()
# Rewrite source icons only when their pixels changed, so a plain build leaves the working tree clean.
foreach ($size in @(16, 32, 48, 128)) {
    $iconPath = Join-Path $root "extension\icons\icon-$size.png"
    $bytes = New-LogoPng $size
    $current = if (Test-Path -LiteralPath $iconPath) { [System.IO.File]::ReadAllBytes($iconPath) } else { $null }
    if ($null -eq $current -or [Convert]::ToBase64String($current) -ne [Convert]::ToBase64String($bytes)) { [System.IO.File]::WriteAllBytes($iconPath, $bytes) }
}

$references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Xml.dll', 'System.Xml.Linq.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Runtime.Serialization.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $wpf $_) }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ("/win32manifest:" + (Join-Path $root 'app.manifest')) ("/win32icon:" + $icon) ("/resource:" + $icon + ",Usugami.usugami.ico") ("/out:" + (Join-Path $output 'Usugami.exe')) $references (Join-Path $root 'src\Usugami.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
foreach ($file in @('README.md', 'SECURITY.md', 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $output -Force }
$extensionOut = Join-Path $output 'extension'
Reset-GeneratedDirectory $extensionOut
# Only reviewed runtime files belong in the release, never local settings or backups.
$extensionFiles = @('manifest.json', 'background.js', 'content.js', 'options.html', 'options.js', 'options.css', 'icons\icon-16.png', 'icons\icon-32.png', 'icons\icon-48.png', 'icons\icon-128.png')
New-Item -ItemType Directory -Path (Join-Path $extensionOut 'icons') | Out-Null
foreach ($file in $extensionFiles) { Copy-Item -LiteralPath (Join-Path (Join-Path $root 'extension') $file) -Destination (Join-Path $extensionOut $file) -Force }
$docsOut = Join-Path $output 'docs'
Reset-GeneratedDirectory $docsOut
Copy-Item -LiteralPath (Join-Path $root 'docs\toolbar.png') -Destination $docsOut -Force
Write-Host "Built: $output\Usugami.exe"

if ($Test -or $Package) {
    $testOutput = Join-Path $output 'self-test.txt'
    if (Test-Path -LiteralPath $testOutput) { Remove-Item -LiteralPath $testOutput -Force }
    $process = Start-Process -FilePath (Join-Path $output 'Usugami.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Self-test timed out after 60 seconds.' }
        if (Test-Path -LiteralPath $testOutput) { Get-Content -LiteralPath $testOutput -Encoding UTF8 } else { throw 'Self-test did not produce a report.' }
        if ($process.ExitCode -ne 0) { throw 'Self-test failed.' }
    } finally { $process.Dispose() }
    & node (Join-Path $root 'tests\extension.test.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'Extension tests failed.' }
}
if ($Package) {
    # Explicit payload prevents unrelated files in dist from entering the release.
    $payload = @('Usugami.exe', 'README.md', 'SECURITY.md', 'LICENSE', 'extension', 'docs') | ForEach-Object { Join-Path $output $_ }
    # Compress-Archive on Windows PowerShell 5.1 stores "\" in entry names, which other unzip tools treat as part of the file name.
    # Build the archive directly so every entry uses the "/" separator the zip format requires.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $prefix = [System.IO.Path]::GetFullPath($output).TrimEnd('\') + '\'
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $payload) {
            $files = if (Test-Path -LiteralPath $item -PathType Container) { @(Get-ChildItem -LiteralPath $item -File -Recurse | Sort-Object FullName) } else { @(Get-Item -LiteralPath $item) }
            foreach ($file in $files) {
                $entry = $file.FullName.Substring($prefix.Length).Replace('\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
    } finally { $archive.Dispose() }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    # LF, as sha256sum -c on macOS/Linux reads a CR as part of the file name.
    [System.IO.File]::WriteAllText($checksum, "$hash  Usugami-windows.zip`n", [System.Text.Encoding]::ASCII)
    Write-Host "Packaged: $zip"
    Write-Host "SHA-256: $checksum"
}
