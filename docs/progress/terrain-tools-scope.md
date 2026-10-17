# Terrain tools: first cut (2026-10-17)

## Decision

Make **Effigy a terrain authoring and baking tool**, not a second terrain renderer. Its first output should be a deterministic, editable height field exported as a **16-bit heightmap** for s&box's native Terrain. Keep cliffs, caves, arches, mesa overhangs and individual rocks as ordinary Effigy models (`.vmdl`): a single-valued height field cannot represent them. Do not generate scene objects at runtime. A later scene-placement tool must save real objects/assets and still work after the generator is removed.

**Update after map-workflow research:** the authoring surface should be a **top-down layered map**, not a brush-only height grid. Elevation is one layer; drawn roads, rivers, material regions and reference art have different semantics. Their editable vector/raster sources should survive export. This is a scope, **not an implemented feature**. No engine/editor integration or terrain export was changed in this pass.

## What exists already

- s&box Terrain supports sculpting, importing 16-bit heightmaps, LOD, collision, holes, up to 32 terrain materials, material painting and foliage. A Terrain component references a `TerrainStorage` asset; the blank asset can be embedded in a scene or saved as `.terrain`. Its documented resolution is 256–8192 square, with default 512; world size and maximum height are in inches. Terrain Material assets (`.tmat`) are distinct from ordinary `.vmat` materials. [Terrain overview](https://sbox.game/dev/doc/gameplay/terrain), [creating terrain](https://sbox.game/dev/doc/gameplay/terrain/creating-terrain/), [materials](https://sbox.game/dev/doc/gameplay/terrain/terrain-materials/).
- The installed engine's `Sandbox.Engine.xml` documents `TerrainStorage`, `Terrain.SyncGPUTexture`, `Terrain.ApplyStorageChanges` and `Terrain.UpdateCollision`. It does **not** establish a stable public bulk-write contract here. Start with the supported editor heightmap importer; validate any direct `.terrain` authoring against a live editor before adopting it.
- Effigy's existing `PrimitiveFeature` can make a mesh plane and its OBJ/ModelDoc path exports static models. Its existing texture-paint height channel is a **material map**, not an s&box terrain-height import. There is no terrain feature in the Effigy feature tree today.
- `research_notes/desert_environments.md` already collects dune morphology, wind/material reference and erosion leads for the podracing case. Reuse that research, but don't make the core feature desert-only.
- No editor for this project was connected (`sbox-cli --list` returned `[]`), so the engine import and collision behavior still need an in-editor acceptance test.

## Smallest useful vertical slice

1. **Terrain document / feature in Effigy**, separate from a mesh body's sculpt and rig workflows. Store a regular float height grid plus aligned semantic layers: reference image (visual only), elevation source (16-bit raster/DEM), vector roads and rivers, optional labeled elevation contours, and named paint masks. Explicit resolution, world XY size (inches), vertical range (inches), seed, origin and image row direction. Named, reorderable non-destructive operations; quantize only at export. Test coordinates with an asymmetric corner marker.
2. **Map import and roads first:** align a top-down drawing to the world extent; trace/edit a road centerline over it, give the road width, shoulders, bank and target grade, then modify nearby terrain by cut/fill with a controllable blend width. Output both a graded height layer and a road-material mask. A drawn road's color must never be interpreted as elevation. Add broad ridge/height brushes after this; do not lead with noise or erosion. A fixed seed must rebuild bit-identically, and out-of-range heights need a visible warning rather than silent clipping.
3. **Export a lossless 16-bit grayscale heightmap**, with a preview and a receipt beside it recording resolution, world size, vertical range, min/max, seed and coordinate convention. Confirm whether the s&box importer expects PNG or RAW and its exact row/endianness and height mapping with a tiny known-value fixture **before** choosing the final writer format. No 8-bit intermediate or gamma correction.
4. In s&box, create a real Terrain object, import the fixture, set the matching size/height and save the scene/asset. Verify all four corners, a known ramp height, collision at the ramp, and persistence after reopening. Keep the terrain selectable and editable with the native sculpt/material tools. This is the gate before promising one-click scene baking.
5. Player-facing UI belongs in Effigy's terrain workspace/tool list and Space search, with a plain tooltip; add a CHANGELOG entry and usability-audit row when it ships. A headless build-script verb and kernel tests should share the same implementation, not duplicate the algorithm.

**Initial acceptance fixture:** 256 × 256 samples (the documented minimum resolution), one elevated corner, one ridge, and a road drawn over a top-down reference that cuts through the ridge. Re-export twice and compare bytes. The imported terrain should show the corner in the expected quadrant, have measured heights within one 16-bit quantization step, collide at the graded road, and survive save/reload without an Effigy component in the scene. At 2048 resolution the height array alone is about 8 MiB; avoid keeping full-grid copies per brush stroke (the engine docs estimate ~24 MiB for combined terrain maps at that resolution).

## After that, in order

| Priority | Tool | Why / dependency |
|---|---|---|
| Next | Height-aware contour interpolation and DEM/GeoTIFF import | Contour *geometry* requires a numeric elevation per line and enough constraints to infer the surface between lines. A photographed topo sheet is only reference art until traced and assigned heights. |
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

## Map-first pipeline and references (2026-10-17)

| Input | What it really contains | Effigy treatment | Bake target |
|---|---|---|---|
| Top-down illustration, satellite image, screenshot | Color and *hints* of roads, valleys, land cover; not reliable elevation | Reference overlay, calibrated to world extent; trace/edit paths and regions | None directly |
| DEM / grayscale **16-bit** heightmap | Numeric elevation samples (with explicit scale and no-data handling) | Base height layer; preserve source and vertical units | s&box Terrain height import |
| Topographic contour polylines | Constant elevation **if each line has a labeled Z** | Constrained interpolation to a DEM, with ridge/stream breaklines and manual correction; don't equate line color with height | Height layer |
| Road/river centerline vectors | Route and optional width, grade, bank, flow direction | Sample existing height, then grade/cut/fill road or carve channel; emit independent masks; bridge/culvert is separate model where appropriate | Height + masks + optional `.vmdl` details |
| Painted masks / polygons | Where a surface or process should apply, not its height | Sand/rock/road/water/no-scatter controls; preview each independently | Material-control maps after importer verification |
| Fully 3D shape | Overhangs, cave, arch, cliff undercut | Regular Effigy mesh/model, possibly plus a native terrain hole | Saved `.vmdl` and scene placement |

**Useful precedents:** [World Machine Layout](https://www.world-machine.com/features.php) sketches ridges, roads and rivers before generating terrain; [Houdini HeightField Output](https://www.sidefx.com/docs/houdini/nodes/sop/heightfield_output.html) exports separate height/mask layers; [Houdini HeightField Project](https://www.sidefx.com/docs/houdini/nodes/sop/heightfield_project.html) projects geometry onto a heightfield. [QGIS interpolation](https://docs.qgis.org/latest/en/docs/gentle_gis_introduction/spatial_analysis_interpolation.html) explains deriving a raster from known elevations, and its [Georeferencer](https://docs.qgis.org/latest/en/docs/user_manual/managing_data_source/georeferencer.html) aligns an arbitrary image to coordinates. [Blender Geometry Nodes](https://docs.blender.org/manual/en/latest/modeling/geometry_nodes/introduction.html) is a useful open-source model for mixing curves, meshes and fields. These are workflow references, not libraries to bundle; World Machine, Houdini and Gaea are not open-source tools.

**Critical design point:** One top-down picture is not a universal terrain file. Interpret an image only after the user chooses its role—reference, calibrated height, or a named mask. A topographic **map** (contours and labels) is different from mesh **topology** (vertices and faces). The editable map document should keep all inputs and operations; the heightmap alone is only a lossy game-engine bake.
