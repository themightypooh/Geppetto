#!/usr/bin/env sh
# Render the heads test: each head front and three-quarter, close, clay. Writes heads_sheet.png.
set -eu
root=$( cd "$( dirname "$0" )/.." && pwd )
out=${1:-/tmp/claude-1000/abom/heads}
mkdir -p "$out"
cd "$root/Effigy.Tests"
dotnet run -c Release --no-build -- --creature heads "$out" voxel=0.25 smooth=1 | grep -E "sculpted|rror" || true
cd "$root"
i=0
for y in 24 8 -8 -24; do
	i=$((i+1))
	dotnet run --project "$root/Effigy.Tests" -c Release --no-build -- shade "$out/heads.obj" "$out" size=700 cam=24,$y,22 look=0,$y,19.5 lens=60 clay=1 silhouette=0 >/dev/null
	mv "$out/heads_custom.png" "$out/h${i}_front.png"
	dotnet run --project "$root/Effigy.Tests" -c Release --no-build -- shade "$out/heads.obj" "$out" size=700 cam=17,$((y-16)),22 look=0,$y,19.5 lens=60 clay=1 silhouette=0 >/dev/null
	mv "$out/heads_custom.png" "$out/h${i}_quarter.png"
done
montage "$out"/h1_front.png "$out"/h2_front.png "$out"/h3_front.png "$out"/h4_front.png "$out"/h1_quarter.png "$out"/h2_quarter.png "$out"/h3_quarter.png "$out"/h4_quarter.png -tile 4x2 -geometry +3+3 -background '#111' "$out/heads_sheet.png"
echo "$out/heads_sheet.png"
