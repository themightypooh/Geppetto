# Put the Ship Geppetto icon on the desktop. Run this once; run it again after moving the checkout
# or if the shortcut ever loses its picture.
#
# WHAT IT MAKES. A .lnk on the desktop pointing at tools/ship-desktop.ps1, and the .ico it wears -
# built here from Assets/editor/effigy_icon.png, because Windows shortcuts cannot take a .png and
# the repo has no .ico to commit. Both the icon and the changelist documents live in
# %LOCALAPPDATA%\Geppetto rather than in the checkout: publishing ships the project's files, and a
# desktop icon is not part of the package.
#
#   tools\install-ship-icon.ps1              the icon, full ship, suite and all
#   tools\install-ship-icon.ps1 -NoTest      a second icon that skips the suite
#
[CmdletBinding()]
param(
	[switch] $NoTest,
	[string] $Name
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$docs = Join-Path $env:LOCALAPPDATA 'Geppetto'
$source = Join-Path $root 'Assets\editor\effigy_icon.png'
$ico = Join-Path $docs 'geppetto-ship.ico'

$null = New-Item -ItemType Directory -Force -Path $docs

# --- the icon -------------------------------------------------------------------------------------

# A .ico is a tiny directory of images. Windows picks the size it wants for the desktop, the taskbar
# and alt-tab, and handing it only the 256 leaves it downscaling that to 16 pixels for the tray -
# which looks like a smudge. Each entry here is a PNG payload, which the shell has read at every
# size since Vista.
if (Test-Path $source) {
	$png = [System.Drawing.Image]::FromFile($source)
	$sizes = @(256, 64, 48, 32, 16)
	$frames = @()

	foreach ($size in $sizes) {
		$canvas = New-Object System.Drawing.Bitmap $size, $size
		$g = [System.Drawing.Graphics]::FromImage($canvas)
		$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
		$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
		$g.DrawImage($png, 0, 0, $size, $size)
		$g.Dispose()

		$buffer = New-Object System.IO.MemoryStream
		$canvas.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
		$canvas.Dispose()

		$frames += , $buffer.ToArray()
	}

	$png.Dispose()

	$out = New-Object System.IO.MemoryStream
	$write = New-Object System.IO.BinaryWriter $out

	$write.Write([uint16] 0)               # reserved
	$write.Write([uint16] 1)               # type: icon
	$write.Write([uint16] $sizes.Count)

	# The first image starts after the header and one 16-byte entry per image, and each entry has
	# to name its own offset, so the offsets are walked forward as the entries are written.
	$offset = 6 + (16 * $sizes.Count)

	for ($i = 0; $i -lt $sizes.Count; $i++) {
		# 256 is written as 0: the field is one byte and 256 does not fit in it.
		$edge = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }

		$write.Write([byte] $edge)         # width
		$write.Write([byte] $edge)         # height
		$write.Write([byte] 0)             # colours in palette: none, it is truecolour
		$write.Write([byte] 0)             # reserved
		$write.Write([uint16] 1)           # planes
		$write.Write([uint16] 32)          # bits per pixel
		$write.Write([uint32] $frames[$i].Length)
		$write.Write([uint32] $offset)

		$offset += $frames[$i].Length
	}

	foreach ($frame in $frames) { $write.Write($frame) }

	$write.Flush()
	[IO.File]::WriteAllBytes($ico, $out.ToArray())
	$write.Dispose()

	Write-Host "  icon    $ico" -ForegroundColor DarkGray
} else {
	Write-Host "  no $source - the shortcut will wear PowerShell's own icon" -ForegroundColor Yellow
	$ico = ''
}

# --- the shortcut ----------------------------------------------------------------------------------

if (-not $Name) { $Name = if ($NoTest) { 'Ship Geppetto (no tests)' } else { 'Ship Geppetto' } }

$link = Join-Path ([Environment]::GetFolderPath('Desktop')) "$Name.lnk"
$driver = Join-Path $root 'tools\ship-desktop.ps1'

# -ExecutionPolicy Bypass because the file is not signed and the machine's policy is not this
# script's business to change. -NoProfile so a slow or noisy profile does not land in the middle of
# a ship. The window stays open on its own: ship-desktop.ps1 waits for a key before it exits, which
# -NoExit would do too but only for the runs that reach the end.
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$driver`""
if ($NoTest) { $arguments += ' -NoTest' }

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($link)
$shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shortcut.Arguments = $arguments
$shortcut.WorkingDirectory = $root
$shortcut.Description = 'Commit, test, push, publish Geppetto, and open the changelist to paste'
if ($ico) { $shortcut.IconLocation = "$ico,0" }
$shortcut.Save()

Write-Host "  icon    $link" -ForegroundColor Green
Write-Host ''
Write-Host '  Double-click it to ship. It asks for a commit message if there is anything' -ForegroundColor Gray
Write-Host '  uncommitted, and finishes by opening the changelist in Notepad.' -ForegroundColor Gray
