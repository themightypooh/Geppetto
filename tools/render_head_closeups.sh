#!/usr/bin/env sh
# Close-ups of the four heads from the last render_heads.sh build: mouths (colour front, clay
# three-quarters both sides) and upper faces (colour and clay). Writes close_sheet.png and eyes_sheet.png.
set -eu
root=$( cd "$( dirname "$0" )/.." && pwd )
out=${1:-/tmp/claude-1000/abom/heads}
cd "$root"
r() { dotnet run --project "$root/Effigy.Tests" -c Release --no-build -- shade "$out/heads.obj" "$out" size=600 "$@" silhouette=0 >/dev/null; }
i=0
for y in 24 8 -8 -24; do
	i=$((i+1))
	r cam=16,$y,17.0 look=4,$y,16.6 lens=65 clay=0; mv "$out/heads_custom.png" "$out/m${i}_front.png"
	r cam=13,$((y-9)),18.0 look=3.5,$y,16.8 lens=65 clay=1; mv "$out/heads_custom.png" "$out/m${i}_quarter.png"
	r cam=13,$((y+9)),18.0 look=3.5,$y,16.8 lens=65 clay=1; mv "$out/heads_custom.png" "$out/m${i}_quarterL.png"
	r cam=16,$y,21.0 look=4,$y,20.8 lens=80 clay=0; mv "$out/heads_custom.png" "$out/e${i}_colour.png"
	r cam=16,$y,21.0 look=4,$y,20.8 lens=80 clay=1; mv "$out/heads_custom.png" "$out/e${i}_clay.png"
done
montage "$out"/m1_front.png "$out"/m2_front.png "$out"/m3_front.png "$out"/m4_front.png "$out"/m1_quarter.png "$out"/m2_quarter.png "$out"/m3_quarter.png "$out"/m4_quarter.png "$out"/m1_quarterL.png "$out"/m2_quarterL.png "$out"/m3_quarterL.png "$out"/m4_quarterL.png -tile 4x3 -geometry +3+3 -background '#111' "$out/close_sheet.png"
montage "$out"/e1_colour.png "$out"/e2_colour.png "$out"/e3_colour.png "$out"/e4_colour.png "$out"/e1_clay.png "$out"/e2_clay.png "$out"/e3_clay.png "$out"/e4_clay.png -tile 4x2 -geometry +3+3 -background '#111' "$out/eyes_sheet.png"
echo "$out/close_sheet.png $out/eyes_sheet.png"
