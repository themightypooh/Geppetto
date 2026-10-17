# Terrain tools: first cut (2026-10-17)

## Decision

Make **Effigy a terrain authoring and baking tool**, not a second terrain renderer. Its first output should be a deterministic, editable height field exported as a **16-bit heightmap** for s&box's native Terrain. Keep cliffs, caves, arches, mesa overhangs and individual rocks as ordinary Effigy models (`.vmdl`): a single-valued height field cannot represent them. Do not generate scene objects at runtime. A later scene-placement tool must save real objects/assets and still work after the generator is removed.

This is a scope, **not an implemented feature**. No engine/editor integration or terrain export was changed in this pass.

## What exists already

- s&box Terrain supports sculpting, importing 16-bit heightmaps, LOD, collision, holes, up to 32 terrain materials, material painting and foliage. A Terrain component references a `TerrainStorage` asset; the blank asset can be embedded in a scene or saved as `.terrain`. Its documented resolution is 256–8192 square, with default 512; world size and maximum height are in inches. Terrain Material assets (`.tmat`) are distinct from ordinary `.vmat` materials. [Terrain overview](https://sbox.game/dev/doc/gameplay/terrain), [creating terrain](https://sbox.game/dev/doc/gameplay/terrain/creating-terrain/), [materials](https://sbox.game/dev/doc/gameplay/terrain/terrain-materials/).
- The installed engine's `Sandbox.Engine.xml` documents `TerrainStorage`, `Terrain.SyncGPUTexture`, `Terrain.ApplyStorageChanges` and `Terrain.UpdateCollision`. It does **not** establish a stable public bulk-write contract here. Start with the supported editor heightmap importer; validate any direct `.terrain` authoring against a live editor before adopting it.
- Effigy's existing `PrimitiveFeature` can make a mesh plane and its OBJ/ModelDoc path exports static models. Its existing texture-paint height channel is a **material map**, not an s&box terrain-height import. There is no terrain feature in the Effigy feature tree today.
- `research_notes/desert_environments.md` already collects dune morphology, wind/material reference and erosion leads for the podracing case. Reuse that research, but don't make the core feature desert-only.
- No editor for this project was connected (`sbox-cli --list` returned `[]`), so the engine import and collision behavior still need an in-editor acceptance test.

## Smallest useful vertical slice

1. **Terrain document / feature in Effigy**, separate from a mesh body's sculpt and rig workflows. A regular float height grid with explicit resolution, world XY size (inches), vertical range (inches), seed and named, reorderable non-destructive operations. Keep source values in world units; quantize only at export. Define the origin and image row direction once, and test them with an asymmetric corner marker.
2. **Two operations to start:** a broad mound/ridge brush with falloff, and a flat/grade operation for a road or buildable area. Support additive and absolute height modes; clamp to the declared vertical range with a visible warning, not silent clipping. A fixed seed must rebuild bit-identically. Don't lead with noise or erosion: those are poor substitutes for art-directed large forms.
3. **Export a lossless 16-bit grayscale heightmap**, with a preview and a receipt beside it recording resolution, world size, vertical range, min/max, seed and coordinate convention. Confirm whether the s&box importer expects PNG or RAW and its exact row/endianness and height mapping with a tiny known-value fixture **before** choosing the final writer format. No 8-bit intermediate or gamma correction.
4. In s&box, create a real Terrain object, import the fixture, set the matching size/height and save the scene/asset. Verify all four corners, a known ramp height, collision at the ramp, and persistence after reopening. Keep the terrain selectable and editable with the native sculpt/material tools. This is the gate before promising one-click scene baking.
5. Player-facing UI belongs in Effigy's terrain workspace/tool list and Space search, with a plain tooltip; add a CHANGELOG entry and usability-audit row when it ships. A headless build-script verb and kernel tests should share the same implementation, not duplicate the algorithm.

**Initial acceptance fixture:** 256 × 256 samples (the documented minimum resolution), one elevated corner, one broad ridge, and a straight graded strip. Re-export twice and compare bytes. The imported terrain should show the corner in the expected quadrant, have measured heights within one 16-bit quantization step, collide at the graded strip, and survive save/reload without an Effigy component in the scene. At 2048 resolution the height array alone is about 8 MiB; avoid keeping full-grid copies per brush stroke (the engine docs estimate ~24 MiB for combined terrain maps at that resolution).

## After that, in order

| Priority | Tool | Why / dependency |
|---|---|---|
| Next | Path-aware grade/cut/fill with shoulder width | Roads and racetracks need controllable slopes and banks; start from an editable spline, not a painted flat stripe. |
| Next | Masks and material-control export | Wet ground, rock, sand and track need different surfaces; verify the native splat/control-map import before implementing its file format. Keep material assets in s&box. |
| Later | Directional dunes, terrace/mesa, talus and erosion | Specialized shape operators after the basic grid and masks are trustworthy. Dunes share one wind vector; steep slip faces should be constrained by grid spacing and slope, not a generic fBm displacement. |
| Later | Mesh extras and scatter bake | Effigy `.vmdl` for non-heightfield forms; saved scene instances for rocks/foliage. Scatter must be deterministic, editable and baked—never `NotSaved` runtime preview objects. |
| Investigate | Direct `.terrain` writes and in-editor live preview | Only if the importer loop is too limiting, after validating engine API and undo/save/collision behavior in a running editor. |

## Open decisions / risks

- Is terrain authoring best exposed as a sixth **Terrain** workspace in Effigy, or as its own small window sharing Effigy's kernel? Start with the data model and export fixture; choose the UI after testing the importer workflow. Do not squeeze a kilometer-scale height grid into a `PolyMesh` feature merely because that pipeline already exists.
- Exact importer format, channel interpretation, image origin and height normalization need the fixture test. The public docs promise 16-bit import but don't specify all of these details.
- High vertical range wastes low-elevation precision; show the resulting height step (`verticalRange / 65535`) in the export UI.
- Operations need dirty-region evaluation, bounds checks and a memory budget before advertising 4096–8192 grids. Preserve authored data when changing resolution or range; require an explicit resample/confirm rather than silently reshaping terrain.
- External algorithms are references, not drop-in dependencies: review license and numerical behavior before porting any erosion implementation. `research_notes/desert_environments.md` §13 records candidate repositories.
