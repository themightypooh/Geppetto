#!/usr/bin/env sh
#
# Effigy headless: look at a document, measure it, match it to a drawing, run a build script.
#
#   tools/effigy.sh render   doc.effigy out.png [sheet|front|side|top|iso|back|left] [size] [nowire] [plain] [bones]
#   tools/effigy.sh describe doc.effigy
#   tools/effigy.sh measure  doc.effigy <landmark|x,y,z> [second landmark]
#   tools/effigy.sh match    doc.effigy <view> ref.png [diff.png]
#   tools/effigy.sh script   build.txt [doc.effigy|-] [outdir]
#   tools/effigy.sh fix      doc.effigy loose,doubles,holes,normals|all [out.effigy]
#   tools/effigy.sh parts    out.png
#   tools/effigy.sh new      out.effigy
#
# The commands live in Effigy.Tests/EffigyCli.cs and run on the canonical kernel in Effigy/,
# so this needs no editor and no s&box. Landmarks: a body name, Body.top/.bottom/.front/.back/
# .left/.right, a bone name (.tail, .mid), ground, origin, or @landmark+dx,dy,dz.
set -eu

root=$( cd "$( dirname "$0" )/.." && pwd )
cd "$root/Effigy.Tests"

# An incremental build first: a few seconds when nothing changed, and never a stale kernel.
exec dotnet run -- "$@"
