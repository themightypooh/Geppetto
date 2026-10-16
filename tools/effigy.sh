#!/usr/bin/env sh
#
# Effigy headless: look at a document, measure it, match it to a drawing, run a build script.
#
#   tools/effigy.sh render   doc.effigy out.png [sheet|front|side|top|iso|back|left] [size] [nowire] [plain] [bones]
#   tools/effigy.sh shade    model.obj outdir [size=] [views=] [turn=] [cam= look= lens=] [clay=1] [silhouette=0] [ground=0] [style=studio]   lit judging renders, no Blender
#   tools/effigy.sh turnaround doc.effigy outdir [shade=clay|material] [size=512] [views=front,quarter,side,back]   File > Render Turnaround, headless
#   tools/effigy.sh describe doc.effigy
#   tools/effigy.sh measure  doc.effigy <landmark|x,y,z> [second landmark]
#   tools/effigy.sh match    doc.effigy <view> ref.png [diff.png]
#   tools/effigy.sh topology doc.effigy|model.obj [Body] [poles=12]   quads/tris/n-gons, poles, edge CV, quad shape, manifold, symmetry, loops per joint
#   tools/effigy.sh section  doc.effigy|model.obj Body|all z=10,20 | x=.. | y=.. | along=Bone [at=0,0.5,1]   width/depth/perimeter/area per slice (z=0..60:7 for a range)
#   tools/effigy.sh flex     doc.effigy Body|all [bones=a,b] [angles=90,140] [axis=auto|x|y|z] [render=outdir]   bend each joint: local volume ratio, new self-crossings, stills
#   tools/effigy.sh script   build.txt [doc.effigy|-] [outdir]
#   tools/effigy.sh fix      doc.effigy loose,doubles,holes,normals|all [out.effigy]
#   tools/effigy.sh parts    out.png
#   tools/effigy.sh new      out.effigy
#   tools/effigy.sh wardrobe outdir [recipe] [size] [nodrape]   every garment on a Citizen-shaped body
#   tools/effigy.sh clothing outdir [recipe] [quick]           the Clothing bar end to end, and the live cloth stepped
#   tools/effigy.sh take     dump.json clip.riganim [options]   a clip on a dumped model: crossings, gaps, stills
#   tools/effigy.sh sim      clip.riganim [model=dump.json|tentacle|citizen] [options]   play a clip headlessly: pops, slides, reach, holds, loop, trail, bake
#   tools/effigy.sh simrig   tentacle|citizen out.json [cables=5] [bones=12]   a stand-in model dump for take and sim
#   tools/effigy.sh key      clip.riganim bone goal|fk "frame:x,y,z[:p,y,r][:ease][:w=];.." [rig=] [model=] [write=]   key from targets
#   tools/effigy.sh key      clip.riganim morph=Name "frame:weight[:ease];.." [shapes=a,b] [write=]   key a shape key's weight curve
#   tools/effigy.sh fbxdump  bones.json source.fbx out.json   a model dump from its source FBX, on an engine skeleton
#   tools/effigy.sh fbxskin  dump.json source.fbx [out.json]  skin weights for an editor dump, from the source FBX
#
# The commands live in Effigy.Tests/EffigyCli.cs and run on the canonical kernel in Effigy/,
# so this needs no editor and no s&box. Landmarks: a body name, Body.top/.bottom/.front/.back/
# .left/.right, a bone name (.tail, .mid), ground, origin, or @landmark+dx,dy,dz.
set -eu

root=$( cd "$( dirname "$0" )/.." && pwd )

# An incremental build first: a few seconds when nothing changed, and never a stale kernel.
# --project rather than a cd, so relative paths mean the caller's directory, not Effigy.Tests/.
exec dotnet run --project "$root/Effigy.Tests" -- "$@"
