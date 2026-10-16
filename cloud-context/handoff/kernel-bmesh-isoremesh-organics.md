# Kernel b/c + procedural organics — session handoff (2026-10-16)

Branch: `claude/kernel-bmesh-isoremesh-organics` (local commit; NOT pushed — push target was
ambiguous in relayed messages, ask po).

## Done (item 1: B-Mesh / SQM cage)
- `Effigy/SkeletonCage.cs`: stick figure (nodes+radii+edges) → all-quad closed manifold cage.
  Branch joints = convex hull of limb directions (+extra points where the sphere is open),
  one Catmull-Clark split, limb star removed → even ring (2×valence) per limb. Tubes via
  parallel transport; joint-to-joint ring-size change with 3-to-1 quads; quad fan caps.
  Loops refused. `Fit` = B-Mesh evolution onto smooth union of round cones.
- `SkinBlockout.Build(skel, radiusOf, levels, Joints.Hull, sides, fit)`; old 3-arg API unchanged
  (cube). Editor File/Model ▸ Add ▸ Body from Bones now uses Hull.
- `SkinFeature` (`add Skin bones="x,y,z r | x,y,z r; ..."`), Model ▸ Add ▸ "Skin (stick figure)".
- Tests: `Effigy.Tests/SkeletonCageTests.cs` (46 checks pass in a scratch runner: lone stick,
  biped incl. mirror symmetry, quadruped, 3/5/7/12-branch stars, urchin, corkscrew twist,
  transition, fit radius within 1%, citizen rig, 7-bone joint, refusals). Full suite NOT re-run.
- Renders (Effigy shade, clay) looked right: biped cage/fit, citizen hull with 5-finger hands.

## Not done — resume here
- SkinFeature has no dedicated test yet (Parse + Execute); add one.
- Item 2: isotropic remesh (Botsch–Kobbelt) as Remesh mode=Isotropic — not started.
- Item 3: Tree (TreeGen.cs → kernel; can mesh branches with SkeletonCage) and Rock features — not started.
- Item 4: remesh guide curves / density for Quad mode — not investigated.
- Usability-audit row for Skin (stick figure) not added.
- Editor code unverified in s&box (cannot compile Editor/ here).
