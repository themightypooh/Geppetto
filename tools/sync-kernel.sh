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

rm -rf "$dst"
mkdir -p "$dst"

# Source only. The README documents the canonical copy and would be a lie sitting in the mirror.
( cd "$src" && find . -name '*.cs' -print ) | while read -r f; do
	mkdir -p "$dst/$( dirname "$f" )"
	cp "$src/$f" "$dst/$f"
done

echo "synced $( find "$dst" -name '*.cs' | wc -l | tr -d ' ' ) kernel files into Editor/Effigy"
