#!/usr/bin/env sh
#
# Mirror the canonical kernel into the Geppetto library's editor assembly.
#
# WHY THERE ARE TWO COPIES AT ALL: s&box compiles `Code/` into the game assembly and `Editor/`
# into the editor assembly, and nothing else. A top-level `Effigy/` is invisible to it. The kernel
# cannot live in `Code/` either - ObjWriter and SmdWriter call File.WriteAllText, which the game
# assembly's sandbox whitelist does not allow. So the editor needs its own copy.
#
# `Effigy/` is the canonical one: it is what Effigy.Tests compiles, and keeping it outside any
# engine folder is what keeps the Godot option open.
# `Editor/Effigy/` is a mirror and must never be edited by hand.
#
# KernelSyncTests fails the test run when the two diverge, so drift is caught rather than silently
# shipping a stale kernel to the editor.
set -eu

root=$( cd "$( dirname "$0" )/.." && pwd )
src="$root/Effigy"

# The handful of kernel files the GAME assembly also compiles - see THE RUNTIME SUBSET below.
# Declared up here because the editor mirror has to skip them.
runtime_files="Vec.cs Xform.cs Rig/Skeleton.cs Rig/SoftBone.cs"
# ONE REPO, TWO COPIES, both here. `Effigy/` is the canonical kernel and `Editor/Effigy/` is the
# mirror s&box actually compiles - the engine only builds `Code/` and `Editor/`, so a top-level
# `Effigy/` is invisible to it, and the kernel cannot live in `Code/` because its writers call
# File.WriteAllText, which the game assembly's sandbox forbids.
lib="$root"
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

# THE COMMIT-DATE GUARD THAT USED TO LIVE HERE IS GONE, and its absence is deliberate.
#
# It compared `git log -1` timestamps on the two copies to work out which side had been edited
# last, so an edit made in the mirror and never carried up would refuse to be overwritten. That
# reasoning needed both copies in ONE repository. Geppetto is its own repo now, with its own
# history, so the two dates describe unrelated timelines - the first run after the split declared
# 69 of 84 files "newer in the mirror" purely because the split rewrote their commits.
#
# What remains is the orphan check below, which asks the only question that still has a meaningful
# answer: does the mirror hold a file the kernel has never heard of? That one is content, not
# chronology, and it catches the case that actually costs work - new files written on the wrong
# side. An EDIT made only in the mirror is now caught by KernelSyncTests failing the suite, which
# is where a cross-repo disagreement belongs.

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
# HaloMount and everything else under Editor/ down with it, none of which is at fault.
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
#
# EXCEPT THE RUNTIME SUBSET, which is the one place two copies is one too many. Those files go to
# Code/ as well, the editor assembly references the game assembly, and a type defined in both is
# CS0436: "the type 'Vec2' in Editor/Effigy/Vec.cs conflicts with the imported type 'Vec2' in
# package.pooh.geppetto". The compiler picks the local one and warns - 1857 times, which is a
# warning list nobody reads, hiding the ones that matter.
#
# It is also a real hazard rather than only noise: a Vec2 built by game code and a Vec2 built by
# editor code are different types that look identical in the source, so anything passing one
# across that boundary fails to compile for a reason the error does not explain.
#
# So the editor gets these from the game assembly it already references, and the mirror leaves
# them out.
( cd "$src" && find . -name '*.cs' -print ) | while read -r f; do
	rel=${f#./}

	case " $runtime_files " in
		*" $rel "*) continue ;;
	esac

	mkdir -p "$dst/$( dirname "$f" )"
	cmp -s "$src/$f" "$dst/$f" 2>/dev/null || cp "$src/$f" "$dst/$f"
done

# Mirror-only files are refused above, so this only ever fires under --force. Removing them after
# the copy rather than wiping the tree first keeps the no-broken-intermediate-state guarantee.
( cd "$dst" && find . -name '*.cs' -print ) | while read -r f; do
	rel=${f#./}

	# A runtime-subset file sitting in the mirror is left over from before the split above and is
	# the thing generating the CS0436 flood, so it goes whether or not the kernel still has it.
	case " $runtime_files " in
		*" $rel "*) rm -f "$dst/$rel"; continue ;;
	esac

	[ -f "$src/$f" ] || rm -f "$dst/$f"
done
find "$dst" -mindepth 1 -type d -empty -delete 2>/dev/null || true

echo "synced $( find "$dst" -name '*.cs' | wc -l | tr -d ' ' ) kernel files into Editor/Effigy" 

# ---------------------------------------------------------------------------------------------
# THE RUNTIME SUBSET.
#
# Everything above ships the whole kernel to the EDITOR assembly. A game assembly cannot have it -
# the writers call File.WriteAllText and the sandbox whitelist refuses - but it does not follow
# that a game can have none of it. SoftSolver is arithmetic on Vec3 and Xform with no filesystem
# anywhere near it, and a game that wants soft bones at runtime needs exactly that much.
#
# So a second, much smaller mirror goes to Code/. The list is deliberately explicit rather than
# "everything that does not mention System.IO": a file quietly growing a dependency should break
# this sync loudly instead of silently enlarging what the game assembly is asked to compile.
runtime="$root/Code/Effigy"

mkdir -p "$runtime"
for f in $runtime_files; do
	[ -f "$src/$f" ] || { echo "runtime subset wants $f, which the kernel does not have" >&2; exit 1; }
	mkdir -p "$runtime/$( dirname "$f" )"
	cmp -s "$src/$f" "$runtime/$f" 2>/dev/null || cp "$src/$f" "$runtime/$f"
done

# Anything else under there is stale - a file dropped from the subset, or one somebody added by
# hand. Same reasoning as the mirror above: the canonical kernel is the only source.
( cd "$runtime" && find . -name '*.cs' -print ) | while read -r f; do
	rel=${f#./}
	case " $runtime_files " in
		*" $rel "*) ;;
		*) rm -f "$runtime/$rel" ;;
	esac
done
find "$runtime" -mindepth 1 -type d -empty -delete 2>/dev/null || true

echo "synced $( echo $runtime_files | wc -w | tr -d ' ' ) runtime kernel files into Code/Effigy" 
