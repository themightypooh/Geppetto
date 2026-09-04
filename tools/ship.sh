#!/usr/bin/env sh
#
# Ship a change: kernel sync, commit, tests, push, publish - in that order.
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
# ALL THREE CHANNELS. Two git repos and the s&box package - the copy that reaches people who
# INSTALLED Geppetto rather than cloned it. The package used to be the one a script could not
# reach; tools/publish.sh reaches it now, by driving the editor's console over its MCP bridge, so
# shipping is one command again instead of one command and a thing to remember afterwards.
#
# THE PACKAGE NEEDS THE EDITOR OPEN, on this project, because the upload is the editor's. If it is
# not, publish.sh says so and this still exits 0: the commit is pushed either way, and a closed
# editor should not read as a failed ship. Run tools/publish.sh --commit later to finish it.
#
#   tools/ship.sh -m "message"    commit everything staged and unstaged, then ship
#   tools/ship.sh                 ship what is already committed
#   tools/ship.sh --no-test       skip the suite (use when you have just run it)
#   tools/ship.sh --no-publish    git only, leave the package alone
#
set -eu

root=$( cd "$( dirname "$0" )/.." && pwd )
cd "$root"

message=""
run_tests=1
publish=1

while [ $# -gt 0 ]; do
	case "$1" in
		-m) shift; message=${1:-}; [ -n "$message" ] || { echo "-m needs a message" >&2; exit 1; } ;;
		--no-test) run_tests=0 ;;
		--no-publish) publish=0 ;;
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

# LAST, because it is the only step that cannot be taken back. Everything before this is a commit
# you can amend or a push you can force over; a published version is out. Putting it at the end
# means a run that fails anywhere earlier has published nothing.
if [ "$publish" -eq 1 ]; then
	echo ""
	tools/publish.sh --commit
fi
