# Cloud sessions archived 2026-10-16 — what to recover

Spawned by the cloud session "Geppetto workshop setup" (repo themightypooh/Geppetto-cloud-work).
All ~29 were archived by "Session management cleanup" (claude.ai/code/session_01Cb6wmpvRayicNc24umz4vs).
Archiving can be undone. Unpushed commits live only in each session's cloud VM, so unarchive
each one and tell it "push your branch to origin", one at a time.

## Safe already
- Mesh reports (topology/section/flex): cherry-picked onto local main (0433146, 88bd7e1, 3a7fc11).
- Helmet kit: themightypooh/podracer claude/confident-newton-01wzzo 496d7f4.
- Seated poses WIP: Geppetto-cloud-work claude/seated-poses-trouser-fit 7919e02.
- Viewport material preview WIP: Geppetto-cloud-work viewport-material-preview 9e29d1d (not compiled).
- Shape keys: already on main (42c3e36).

## Committed in a cloud VM, NOT pushed (recover by unarchive + push)
| Work | Branch | Commits |
|---|---|---|
| Simple deform, Lattice, multi-plane/radial symmetry | claude/deform-symmetry-masks-shell | 89d2806 d230a19 579c22f |
| Shared ClothSolver (layering/maps/wind/seat) | claude/live-cloth-layering | 42c7da4 ce26057 |
| Cuffs/rolls/gloves (WIP) | claude/cuffs-rolls-gloves | a14f02e |
| Sparse LDLT solver, heat + voxel weights | claude/sparse-heat-arap | 539ea1f 195b22e |
| Collars/necklines (design handoff only) | claude/collars-necklines | f8654e6 |
| Profile upgrades, spline radii, primitives (WIP) | claude/profile-primitives-upgrades | a360dd8 |
| Knots/cords, goggles, watches (untested) | feat/knots-goggles-watch | 74eb9f6 |
| Export: skinned LODs, FBX blendshapes, wrinkle maps | claude/export-gaps-lods-blendshapes-wrinkles | 055ac5f d7df4bf d8a42eb 46296ac |
| B-Mesh/SQM cage, Skin feature, Body from Bones | claude/kernel-bmesh-isoremesh-organics | aa4a1c0 |
| Blob body shapes | claude/blob-body-shapes | ? |
| Diagnostic fixes, Set Origin/turnaround, FBX import, bake | ? | reported committed |

## Body-horror sessions — user stopped these on purpose, do NOT redo
part generators (carpet/spikes/maw/webbing), tendril/ribbon/drip features, corruption-driven surface, blob body shapes.

## Unknown
~12 more sessions (garments, cloth, Marionette, UV …) never reported. Task list is in the
"Geppetto workshop setup" session.
