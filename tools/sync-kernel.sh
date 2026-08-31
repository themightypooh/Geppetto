#!/usr/bin/env sh
#
# Mirror the canonical kernel into the s&box editor assembly.
#
# WHY THERE ARE TWO COPIES AT ALL: s&box compiles `Code/` into the game assembly and `Editor/`
# into the editor assembly, and nothing else. A top-level `Effigy/` is invisible to it. The kernel
# cannot live in `Code/` either - ObjWriter and SmdWriter call File.WriteAllText, which the game
# assembly's sandbox whitelist does not allow. So the editor needs its own copy.
#
# `Effigy/` is the canonical one: it is what Effigy.Tests compiles, and keeping it outside any
# engine folder is what keeps the Godot option open (see MODELING-HANDOFF-GODOT.md).
# `Editor/Effigy/` is a mirror and must never be edited by hand.
#
# KernelSyncTests fails the test run when the two diverge, so drift is caught rather than silently
# shipping a stale kernel to the editor.
set -eu

root=$( cd "$( dirname "$0" )/.." && pwd )
src="$root/Effigy"
dst="$root/Editor/Effigy"

[ -d "$src" ] || { echo "no kernel at $src" >&2; exit 1; }

# REFUSE TO SYNC OVER WORK THAT ONLY EXISTS IN THE MIRROR.
#
# This script copies one way, canonical over mirror, and the copy used to start with `rm -rf`. That
# is correct exactly as long as the mirror is downstream. It stopped being true once: a session
# edited Editor/Effigy directly - three new files and six changed ones, DmxWriter among them - and
# the next run of tools/test.sh, which calls this script BEFORE testing, was one command away from
# deleting all of it. Committed work, so recoverable, but nothing would have said a word.
#
# A file in the mirror that the canonical kernel has never heard of is not stale. It is the newer
# copy, and the fix is to move it up to Effigy/ rather than to delete it here.
orphans=
if [ -d "$dst" ]; then
	orphans=$( cd "$dst" && find . -name '*.cs' -print | while read -r f; do
		[ -f "$src/$f" ] || echo "  ${f#./}"
	done )
fi

# The same failure with an EDITED file rather than a new one: the mirror's copy differs and its
# last commit is more recent than the canonical copy's, which means the edit was made in the mirror
# and has never been carried up. Content alone cannot tell the two directions apart - only which
# side was touched last can - so this asks git. Outside a checkout it simply does not fire.
if [ -d "$dst" ] && command -v git >/dev/null 2>&1 && git -C "$root" rev-parse --git-dir >/dev/null 2>&1; then
	newer=$( cd "$dst" && find . -name '*.cs' -print | while read -r f; do
		rel=${f#./}
		[ -f "$src/$rel" ] || continue
		cmp -s "$src/$rel" "$dst/$rel" && continue

		# Uncommitted edits in the canonical copy mean it is the side being worked on right now,
		# whatever the commit dates say. Without this the guard fires on the ordinary case of
		# carrying mirror work up by hand and then syncing it back down.
		[ -n "$( git -C "$root" status --porcelain -- "Effigy/$rel" 2>/dev/null )" ] && continue

		s_at=$( git -C "$root" log -1 --format=%ct -- "Effigy/$rel" 2>/dev/null || echo 0 )
		d_at=$( git -C "$root" log -1 --format=%ct -- "Editor/Effigy/$rel" 2>/dev/null || echo 0 )
		[ "${d_at:-0}" -gt "${s_at:-0}" ] && echo "  $rel"
	done )

	if [ -n "$newer" ] && [ "${1:-}" != "--force" ]; then
		echo "refusing to sync: the mirror's copy of these was committed after the canonical one," >&2
		echo "so the edit was made in Editor/Effigy and has not been carried up:" >&2
		echo "$newer" >&2
		echo "" >&2
		echo "copy them up into Effigy/ first, or pass --force to discard them." >&2
		exit 1
	fi
fi

if [ -n "$orphans" ] && [ "${1:-}" != "--force" ]; then
	echo "refusing to sync: these files exist only in the mirror, so the mirror is ahead" >&2
	echo "$orphans" >&2
	echo "" >&2
	echo "copy them up into Effigy/ first (that is where the kernel is edited), or pass --force" >&2
	echo "to discard them." >&2
	exit 1
fi

# NEVER `rm -rf "$dst"` HERE, however tempting a clean slate is.
#
# s&box watches this tree and recompiles the editor assembly the moment it changes. Deleting the
# directory hands the compiler a source tree with no Effigy in it, so the whole assembly fails with
# several hundred "The type or namespace name 'Effigy' could not be found" errors - and it takes
# HaloMount, ShaderForge and everything else under Editor/ down with it, none of which is at fault.
# The files reappear a moment later, but the FAILED build is the one that sticks: s&box keeps
# running the last assembly that compiled, so every edit made afterwards silently does nothing and
# the editor shows stale results with no sign they are stale.
#
# That has cost this project whole sessions of chasing bugs in code that was already correct
# (sbox-dev logs: 2026-08-26 23:41, and every compile from 2026-08-30 21:07 onward). The tell is a
# compile failure whose errors are ALL "namespace Effigy could not be found" with none inside
# Editor/Effigy itself - the mirror was mid-rebuild, nothing more. Touch any source file to trigger
# a fresh compile and it goes green.
#
# Copying over the files in place fixes it: every intermediate state the watcher can observe still
# contains a complete kernel, so no observable state fails to compile.
mkdir -p "$dst"

# Source only. The README documents the canonical copy and would be a lie sitting in the mirror.
# Unchanged files are skipped so the watcher sees as few writes as possible.
( cd "$src" && find . -name '*.cs' -print ) | while read -r f; do
	mkdir -p "$dst/$( dirname "$f" )"
	cmp -s "$src/$f" "$dst/$f" 2>/dev/null || cp "$src/$f" "$dst/$f"
done

# Mirror-only files are refused above, so this only ever fires under --force. Removing them after
# the copy rather than wiping the tree first keeps the no-broken-intermediate-state guarantee.
( cd "$dst" && find . -name '*.cs' -print ) | while read -r f; do
	[ -f "$src/$f" ] || rm -f "$dst/$f"
done
find "$dst" -mindepth 1 -type d -empty -delete 2>/dev/null || true

echo "synced $( find "$dst" -name '*.cs' | wc -l | tr -d ' ' ) kernel files into Editor/Effigy"
