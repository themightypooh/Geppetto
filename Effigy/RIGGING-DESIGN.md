# Effigy rigging design

Status: design starting point, August 2026.

## Decision

Effigy owns rig authoring and binding. Rig Control owns posing, keyframes, IK, and constraints.
Effigy should not grow a second animation editor.

The handoff is a normal s&box skinned model:

```text
Effigy document -> rebuild bodies -> reapply body-id bindings -> write SMD + VMDL
                -> .ctrlrig points at the VMDL -> .riganim opens in Rig Control
```

This uses the existing `SmdWriter`, which already carries hierarchy, bind pose, materials, and
weights. The existing VMDL pipeline has also been proven with the static demo mesh.

## What lives where

The Effigy document stores a `Skeleton`, a mapping from stable `Body.Id` to bone name, binding
settings, and the export identity/path. The skeleton is part of the Effigy document rather than
`RigDocument`: `RigDocument` deliberately owns a model reference and animation constraints, while
the engine-free Effigy document is the source of truth for parametric binding.

On every rebuild, `PartStudio.ToMeshWithBodies()` supplies `BodyRange` values and
`SkinBinder.BindBodies()` regenerates weights from the body-id mapping. Vertex indices are never
stored. The resulting weights are assigned to the rebuilt mesh and written with the stored
skeleton. The SMD/VMDL is a baked consumer artifact; the Effigy document remains authoritative.

The first binding mode is rigid body binding. Each mapped body gets full weight to its bone;
unmapped bodies use the documented nearest-bone fallback but are reported as warnings. A later
smooth pass can use `SkinBinder.SmoothWeights()` without changing the handoff.

## Authoring UI: small, in Effigy

Effigy needs a rig panel, but not a posing timeline. The first useful version should support:

1. Create a named bone with a parent and draw its head-to-tail in the viewport. This calls
   `Skeleton.AddBoneFromPoints()`.
2. Select a body and assign it to a bone. The body list shows its name and stable id.
3. Preview bones and the current binding over the live rebuilt mesh.
4. Rebuild/export the skinned model and expose its VMDL to the asset system.

For the target hand: make the palm and finger extrusions separate bodies, create a palm/root bone,
draw one bone down each finger, assign each finger body to that bone, and export. The initial result
is intentionally rigid: each finger moves as a unit. Segmented fingers can later use parented bones
and smooth weights.

Effigy should report zero-length bones, missing mapped bones, and failed `SkinWeights.Validate()`
results. Vertex painting is not part of the first pass.

## Rig Control handoff

The generated VMDL is assigned as `RigDocument.SourceModel`. The user creates or opens a `.riganim`;
`RigControlWindow` already resolves the model from the clip or its `.ctrlrig`, draws its bones, and
keys them by name. Bone names are therefore stable identifiers: renaming one is a deliberate
breaking change for existing clips.

Effigy may later offer a convenience action to create the `.ctrlrig` and open Rig Control, but this
is only integration sugar. Constraints and animation remain in Marionette's existing assets.

## Non-goals and implementation order

No Effigy timeline or duplicate FK/IK implementation; no vertex-index rig storage; no SMD import back
into the parametric document; no heat-diffusion solver in the first pass.

1. Add engine-free binding data and rebuild/rebind stability tests.
2. Add the Effigy bone/body assignment panel and viewport interaction.
3. Wire `SmdWriter` into the proven VMDL export path.
4. Verify the cube-and-fingers example in Rig Control, including a rebuild after keyframing.
