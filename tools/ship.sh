#!/usr/bin/env sh
#
# Ship a change: kernel sync, commit, tests, push - in that order.
#
# WHY THIS EXISTS. Getting one fix in front of people took five commands in a fixed order, and the
# order was load-bearing in ways nothing said out loud - sync the kernel BEFORE committing, or the
# mirror lands in the next commit instead of this one. Five commands remembered correctly every
# time is a thing a person gets wrong on the day it matters.
#
# ONE REPO. Geppetto, its kernel, its tests and this script all live here now. There was a spell
# where the product and the kernel that built it sat in two separate checkouts, which meant every
# change needed the same commit made twice; that is over.
#
# WHAT IT DOES NOT DO: publish the library package to s&box. That is an upload from the editor's
# own Publish dialog with no command-line equivalent, so it stays manual and this script ends by
# saying so. Both git repos are up to date either way; the package is the third channel and the
# only one a script cannot reach.
#
#   tools/ship.sh -m "message"    commit everything staged and unstaged, then ship
#   tools/ship.sh                 ship what is already committed
#   tools/ship.sh --no-test       skip the suite (use when you have just run it)
#
set -eu

root=$( cd "$( dirname "$0" )/.." && pwd )
cd "$root"

message=""
run_tests=1

while [ $# -gt 0 ]; do
	case "$1" in
		-m) shift; message=${1:-}; [ -n "$message" ] || { echo "-m needs a message" >&2; exit 1; } ;;
		--no-test) run_tests=0 ;;
		# Comment lines only, from the header's first line until the code starts, so adding a
		# paragraph above never drags `set -eu` into the help text.
		-h|--help) awk 'NR>2 && /^#/ { sub(/^# ?/, ""); print; next } NR>2 { exit }' "$0"; exit 0 ;;
		*) echo "unknown argument: $1" >&2; exit 1 ;;
	esac
	shift
done

branch=$( git rev-parse --abbrev-ref HEAD )

if [ "$branch" != "main" ]; then
	echo "on '$branch', not main - this pushes to the public repo, so shipping a branch would" >&2
	echo "publish it as if it were the release. Switch to main first." >&2
	exit 1
fi

# FIRST, because the mirror is generated. Running it after the commit means the very next status
# is dirty with files the commit should have carried, and the editor is compiling a kernel that no
# commit contains. The script's own guards refuse if the mirror is somehow ahead.
echo "==> syncing kernel"
tools/sync-kernel.sh

if [ -n "$( git status --porcelain )" ]; then
	if [ -z "$message" ]; then
		echo "" >&2
		echo "uncommitted changes, and no -m to commit them with:" >&2
		git status --short >&2
		echo "" >&2
		echo "pass -m \"message\" to commit them, or commit by hand first." >&2
		exit 1
	fi

	echo "==> committing"
	git add -A
	git commit -q -m "$message"
fi

if [ "$run_tests" -eq 1 ]; then
	echo "==> tests"
	tools/test.sh
fi

echo "==> pushing"
git push origin main

echo ""
echo "shipped $( git rev-parse --short HEAD ) - $( git log -1 --format=%s )"
echo ""
echo "  https://github.com/themightypooh/Geppetto"
echo ""
echo "The s&box package is a separate channel - the people who INSTALLED Geppetto rather than"
echo "cloned it. To send this to them, in the editor console:"
echo ""
echo "    geppetto_publish            what would go, uploads nothing"
echo "    geppetto_publish commit     send it, notes taken from the commit above"
echo ""
echo "Left deliberate rather than automatic: a published version cannot be taken back."
