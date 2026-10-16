#!/usr/bin/env sh
# Build Choir, Stalk and Bloom and render each (clay, all views), then a lineup sheet:
# quarter views on top, fronts below. Usage: render_lineup.sh [outroot] [voxel]
set -eu
root=$( cd "$( dirname "$0" )/.." && pwd )
out=${1:-/tmp/claude-1000/abom}
voxel=${2:-0.3}
cd "$root/Effigy.Tests"
for c in choir stalk bloom; do
	mkdir -p "$out/$c"
	dotnet run -c Release --no-build -- --creature $c "$out/$c" voxel=$voxel 2>&1 | grep -E "rror|verts" || true
done
cd "$root"
for c in choir stalk bloom; do
	dotnet run --project "$root/Effigy.Tests" -c Release --no-build -- shade "$out/$c/$c.obj" "$out/$c" size=1000 clay=1 >/dev/null &
done
wait
montage "$out"/choir/choir_quarter.png "$out"/stalk/stalk_quarter.png "$out"/bloom/bloom_quarter.png "$out"/choir/choir_front.png "$out"/stalk/stalk_front.png "$out"/bloom/bloom_front.png -tile 3x2 -geometry +4+4 -background '#111' "$out/lineup.png"
echo "$out/lineup.png"
