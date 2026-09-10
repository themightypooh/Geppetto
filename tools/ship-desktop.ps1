# Ship Geppetto from a desktop icon. One double-click: commit, test, push, publish, and a text
# document with the changelist typed out ready to paste into the site.
#
# WHY THIS EXISTS. tools/ship.sh already does the shipping and does it well, but it wants a shell,
# a -m message decided before you start, and it ends by printing the changelist into a terminal
# that closes. This is that script with a front door on it: it asks for the message when there is
# something to commit, says up front whether the editor is there to publish with, and leaves the
# changelist in a file you can keep open in one window while you fill the form in another.
#
# IT ADDS NOTHING TO THE SHIP ITSELF. Every git and publish decision is still ship.sh's, so there
# is one description of how Geppetto ships rather than a second one quietly drifting out of step
# with it. Read tools/ship.sh's header for what actually happens; read this one for what the icon
# does around it.
#
# THE CHANGELIST DOCUMENT IS THE POINT. The engine's package API can read changelists and has no
# method that writes one, so the form on sbox.game is the only door and a person has to walk
# through it. What that person should not have to do is read a closing terminal and retype. The
# document this writes carries the title, the version, the date and the five boxes, each block
# clean enough to select and paste whole.
#
#   powershell -ExecutionPolicy Bypass -File tools\ship-desktop.ps1
#   tools\install-ship-icon.ps1        puts the icon on the desktop that runs this
#
[CmdletBinding()]
param(
	# Skipping the suite is for when you have just run it yourself. The icon does not pass it:
	# a double-click should take the safe path.
	[switch] $NoTest,
	[switch] $NoPublish,

	# Write the changelist document and ship nothing. For the run where the paste went wrong, or
	# the window was closed, or the notes were edited after the fact - the alternative is shipping
	# again to get a piece of paper.
	[switch] $ChangelistOnly
)

$ErrorActionPreference = 'Stop'

# Everything downstream speaks UTF-8 and the notes are full of em dashes and arrows. Without this,
# PowerShell decodes bash's output as cp1252 and the changelist arrives with mojibake in it - which
# would then be pasted onto the store page.
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false

$root = Split-Path -Parent $PSScriptRoot
$boxes = @('Added', 'Improved', 'Fixed', 'Removed', 'Known Issues')

# Where the changelist documents land. NOT in the checkout: ship.sh commits with `git add -A`, so
# a file written into the repo would be swept into the next ship as if it were source.
$docs = Join-Path $env:LOCALAPPDATA 'Geppetto'

$Host.UI.RawUI.WindowTitle = 'Ship Geppetto'

function Say([string] $text, [string] $colour = 'Gray') {
	Write-Host $text -ForegroundColor $colour
}

function Rule([string] $text) {
	Write-Host ''
	Write-Host "  $text" -ForegroundColor Cyan
	Write-Host "  $('-' * 58)" -ForegroundColor DarkCyan
}

# Every exit goes through here, the failures included, because a window that vanishes takes the
# reason with it and a desktop icon leaves no scrollback to go back to.
function Stop-Here([int] $code) {
	Write-Host ''
	Write-Host '  press enter to close' -ForegroundColor DarkGray
	[void] (Read-Host)
	exit $code
}

Write-Host ''
Say '   ===============================================================' 'DarkCyan'
Say '    SHIP GEPPETTO    commit, test, push, publish, changelist' 'White'
Say '   ===============================================================' 'DarkCyan'

# --- the things that have to be true before anything moves ------------------------------------

# Git's own bash, because ship.sh and everything under it are POSIX shell. Ask git where it lives
# rather than hard-coding Program Files, which is wrong on a machine that put it elsewhere.
$bash = 'C:\Program Files\Git\bin\bash.exe'

if (-not (Test-Path $bash)) {
	$git = (Get-Command git -ErrorAction SilentlyContinue).Source

	if ($git) {
		$bash = Join-Path (Split-Path -Parent (Split-Path -Parent $git)) 'bin\bash.exe'
	}
}

if (-not (Test-Path $bash)) {
	Write-Host ''
	Say '  no Git Bash here, and ship.sh is a shell script.' 'Red'
	Say '  Install Git for Windows, or run tools/ship.sh from a shell yourself.' 'Red'
	Stop-Here 1
}

Set-Location $root

$null = New-Item -ItemType Directory -Force -Path $docs
$log = Join-Path $docs 'last-ship.log'

$branch = (& git rev-parse --abbrev-ref HEAD).Trim()

if ($branch -ne 'main' -and -not $ChangelistOnly) {
	Write-Host ''
	Say "  on '$branch', not main." 'Red'
	Say '  Shipping pushes to the public repo, so a branch would go out as the release.' 'Red'
	Say '  Switch to main first.' 'Red'
	Stop-Here 1
}

# --- what is going out --------------------------------------------------------------------------

$message = ''
$dirty = if ($ChangelistOnly) { '' } else { & git status --porcelain }

if ($ChangelistOnly) {
	Rule 'changelist only - nothing is being shipped'
} else {
	Rule 'what is going out'
}

if ($dirty) {
	& git status --short | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
	Write-Host ''
	Say '  These all get committed. Describe them in one line, or leave it empty to stop.' 'White'
	Write-Host ''
	$message = (Read-Host '  message').Trim()

	if (-not $message) {
		Write-Host ''
		Say '  nothing shipped.' 'DarkGray'
		Stop-Here 0
	}
} elseif (-not $ChangelistOnly) {
	Say '    nothing uncommitted - shipping what is already here:' 'DarkGray'
	Say "    $(& git log -1 --format='%h %s')" 'Yellow'
}

# --- can the package actually go? -----------------------------------------------------------------

# THE UPLOAD IS THE EDITOR'S. geppetto_publish is a ConCmd inside s&box and publish.sh reaches it
# over the editor's MCP bridge. No editor is not a failure - ship.sh commits and pushes either way
# - but it is the difference between reaching people who INSTALLED Geppetto and reaching GitHub,
# and that is worth knowing before the run rather than three minutes into it.
$editor = $false

if (-not $NoPublish -and -not $ChangelistOnly) {
	try {
		$probe = New-Object System.Net.Sockets.TcpClient
		$editor = $probe.ConnectAsync('127.0.0.1', 7269).Wait(700)
		$probe.Close()
	} catch {
		$editor = $false
	}

	Write-Host ''

	if ($editor) {
		Say '    editor is up - the package will be published' 'Green'
	} else {
		Say '    NO EDITOR on 127.0.0.1:7269 - git only, the package will NOT go out.' 'Yellow'
		Say '    Open Geppetto in s&box first if you want this one to reach installs.' 'Yellow'
		Write-Host ''

		if ((Read-Host '  ship anyway? [y/N]').Trim() -notmatch '^[Yy]') {
			Write-Host ''
			Say '  nothing shipped.' 'DarkGray'
			Stop-Here 0
		}
	}
}

# --- the ship itself ------------------------------------------------------------------------------

# Bash wants a bash-shaped path, and this checkout sits under a directory with an & in its name.
$unix = '/' + $root.Substring(0, 1).ToLower() + $root.Substring(2).Replace('\', '/')

if (-not $ChangelistOnly) {
	$flags = @()
	if ($message)   { $flags += '-m "$SHIP_MESSAGE"' }
	if ($NoTest)    { $flags += '--no-test' }
	if ($NoPublish) { $flags += '--no-publish' }

	# The message travels in the environment rather than on the command line, so an apostrophe or
	# a $ in it is never re-parsed by the shell that runs the script.
	$env:SHIP_MESSAGE = $message

	# WRITTEN TO A FILE RATHER THAN PASSED TO bash -c. PowerShell 5.1 mangles double quotes on
	# their way to a native program, and the -m argument has to keep its pair of them or a message
	# of more than one word arrives as several arguments and ship.sh refuses it.
	#
	# stderr is folded into stdout INSIDE bash, too: doing it on the PowerShell side wraps every
	# stderr line in an ErrorRecord, which turns the suite's ordinary progress chatter into what
	# looks like a stack of failures and makes the exit code lie.
	$runner = Join-Path $docs 'ship-run.sh'
	$steps = @(
		"cd '$unix'",
		"exec tools/ship.sh $($flags -join ' ') 2>&1"
	) -join "`n"

	[IO.File]::WriteAllText($runner, $steps + "`n", (New-Object System.Text.UTF8Encoding $false))

	Rule 'shipping'
	Write-Host ''

	& $bash ('/' + $runner.Substring(0, 1).ToLower() + $runner.Substring(2).Replace('\', '/')) |
		Tee-Object -FilePath $log

	$shipped = $LASTEXITCODE
	$env:SHIP_MESSAGE = ''

	if ($shipped -ne 0) {
		Write-Host ''
		Say '  the ship stopped - see above. Nothing further was done. The log is at:' 'Red'
		Say "    $log" 'DarkGray'
		Stop-Here $shipped
	}
}

# --- which revision was that, really? ---------------------------------------------------------------

# On a -ChangelistOnly run this is the last ship's log, which is the run whose changelist is being
# written out again, so its revision is the right one to carry over.
$output = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
$revision = ''
$moved = $false

# publish.sh's last line is "revision <id> <moved>", written for exactly this.
if ($output -match '(?m)^revision (\d+) ([01])') {
	$revision = $Matches[1]
	$moved = $Matches[2] -eq '1'
}

$subject = & git log -1 --format='%s'
$stamped = $output -match 'stamped Unreleased as v'

# --- the document -----------------------------------------------------------------------------------

# Read the boxes back out of changelist.sh rather than deriving them again here. That script and
# its python already decide which bullet belongs in which box, strip the (`Foo.cs`) notes and
# flatten wrapped sentences to one line; a second opinion about any of that is a second thing to
# keep in step.
#
# Which section: ship.sh moves Unreleased under a v<revision> heading when it stamps, so after a
# stamped run the notes that just went out live under that version and Unreleased is empty.
$section = if ($stamped -and $revision) { $revision } else { '' }
$raw = & $bash -c "cd '$unix' && tools/changelist.sh $section 2>&1"

if ($LASTEXITCODE -ne 0 -and $section) {
	$raw = & $bash -c "cd '$unix' && tools/changelist.sh 2>&1"
}

$filled = [ordered] @{}
$boxes | ForEach-Object { $filled[$_] = @() }
$unpublishable = @()
$current = ''

foreach ($line in ($raw -split "`r?`n")) {
	if ($line -match '^--- (.+?) -+\s*$')  { $current = $Matches[1].Trim(); continue }
	if ($line -match '^!!! ')              { $current = '!'; $unpublishable += $line; continue }
	if ($line -match '^# ')                { continue }
	if ($line -match '^Paste these into ') { continue }
	if (-not $line.Trim())                 { continue }

	if ($current -eq '!')               { $unpublishable += $line }
	elseif ($filled.Contains($current)) { $filled[$current] += $line.Trim() }
}

# A title the form can take as-is. Commit subjects here read "Effigy: import a mesh as a body",
# and the box wants the half after the colon.
$title = ($subject -replace '^[A-Za-z][A-Za-z ]*: ', '').Trim()
if ($title) { $title = $title.Substring(0, 1).ToUpper() + $title.Substring(1) }

$doc = New-Object System.Text.StringBuilder
function Line([string] $text = '') { [void] $doc.AppendLine($text) }

Line '================================================================='
Line ' GEPPETTO CHANGELIST'
Line ' https://sbox.game/pooh/geppetto/changes   >   New changelist'
Line '================================================================='
Line ''
Line ' New changelist makes an empty draft the moment you click it, so'
Line ' fill this one in rather than backing out of it. Tick "Visible to'
Line ' the public" - a new draft is hidden until you do - then Save'
Line ' Changes, and assign the revision AFTER saving, not before.'
Line ''
Line '-----------------------------------------------------------------'
Line ''
Line "TITLE     $title"
Line ("VERSION   " + $(if ($revision) { "v$revision" } else { '(nothing published - see below)' }))
Line ("DATE      " + (Get-Date -Format 'dd MMMM yyyy'))
Line ''

if ($revision -and -not $moved) {
	Line ' THE VERSION ABOVE IS THE ONE THAT WAS ALREADY LIVE. The publish'
	Line ' reported that the version did not move, and every time it has said'
	Line ' that, the site had in fact made a new revision with a much higher'
	Line ' number. Do not trust it and do not paste it. Open the edit page,'
	Line ' look under "Assign to a revision" and take the one named after'
	Line ' this commit:'
	Line ''
	Line "     $subject"
	Line ''
} elseif ($revision) {
	Line ' The backend can take half a minute to serve a new revision, so if'
	Line ' the picker does not offer this one yet, wait and reload. Revisions'
	Line ' are named after the commit that made them:'
	Line ''
	Line "     $subject"
	Line ''
} else {
	Line ' NOTHING WAS PUBLISHED this run - only the git push happened, so'
	Line ' the package has not reached anybody who installed it. Publish from'
	Line ' the editor console with `geppetto_publish commit`, then fill this'
	Line ' in with the revision it makes.'
	Line ''
}

Line ' Each block below is one box on the form, in the order the form asks'
Line ' for them, and one line is one bullet. Anything that still reads like'
Line ' a paragraph wants splitting before it goes on the store page.'
Line ''

$empty = $true

foreach ($box in $boxes) {
	if (-not $filled[$box]) { continue }

	$empty = $false
	Line ''
	Line ('=' * 65)
	Line " $($box.ToUpper())"
	Line ('=' * 65)
	Line ''

	foreach ($line in $filled[$box]) { Line $line }
}

if ($empty) {
	Line ''
	Line ' NOTHING TO POST. The section this read has no bullets under any of'
	Line ' the five headings, so either the notes are not written yet or they'
	Line ' are sitting under headings the form does not have.'
}

# Bullets that arrive as paragraphs are the one thing that reliably needs a hand before pasting -
# a wall of prose on a store page is not what a changelist is for. Naming them down here rather
# than marking them inline keeps every block above clean enough to select and paste whole.
$long = @()

foreach ($box in $boxes) {
	foreach ($line in $filled[$box]) {
		if ($line.Length -gt 150) {
			$long += "  [$box] " + $line.Substring(0, 70) + '...'
		}
	}
}

if ($long) {
	Line ''
	Line ''
	Line ('-' * 65)
	Line ' SPLIT THESE FIRST - one idea per line, keep the detail'
	Line ('-' * 65)
	Line ''
	$long | ForEach-Object { Line $_ }
}

if ($unpublishable) {
	Line ''
	Line ''
	Line ('-' * 65)
	Line ' THESE WILL NOT REACH THE FORM'
	Line ('-' * 65)
	Line ''
	$unpublishable | ForEach-Object { Line $_ }
}

$stamp = if ($revision) { "v$revision" } else { Get-Date -Format 'yyyy-MM-dd-HHmm' }
$path = Join-Path $docs "changelist-$stamp.txt"

# Notepad reads UTF-8 with a BOM without being told, and reads one without a BOM as cp1252 - which
# is how an em dash becomes three characters on the way to the form.
[IO.File]::WriteAllText($path, $doc.ToString(), (New-Object System.Text.UTF8Encoding $true))

Rule 'done'
Write-Host ''
Say "    $path" 'Green'
Write-Host ''

# Opened by association rather than by handing the path to notepad.exe. Windows 11's notepad is a
# launcher stub in front of the packaged app, and a path arriving quoted comes out the far side
# still carrying its quotes - it then reports "the system cannot find the path specified" about a
# file that is plainly there.
Invoke-Item -LiteralPath $path
Start-Process 'https://sbox.game/pooh/geppetto/changes'

Stop-Here 0
