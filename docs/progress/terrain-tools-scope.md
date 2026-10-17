# Terrain tools: first cut (2026-10-17)

## Decision

Make **Geppetto an independent terrain authoring system**, not an accessory to any particular engine. Its native project is the source of truth: editable rasters, vector paths, regions, masks, geometry and operations, with its own 2D/3D preview. Outputs are adapters—heightmaps, GIS rasters, meshes and engine-specific terrain/assets—not the internal data model. A single-valued height field cannot represent caves or overhangs, so preserve separate 3D geometry for those. Scene integrations bake real saved content; never rely on runtime-only generated previews.

**Update after map-workflow research:** the authoring surface should be a **top-down layered map**, not a brush-only height grid. Elevation is one layer; drawn roads, rivers, material regions and reference art have different semantics. Their editable vector/raster sources should survive export. This is a scope, **not an implemented feature**. No engine/editor integration or terrain export was changed in this pass.

## What exists already

- Effigy's existing `PrimitiveFeature` can make a mesh plane and export static models. Its texture-paint height channel is a **material map**, not a terrain-height source. There is no terrain feature in the feature tree today.
- s&box Terrain is **one downstream adapter**: it supports importing 16-bit heightmaps, LOD, collision, holes, up to 32 terrain materials, material painting and foliage. A Terrain component references a `TerrainStorage` asset; its documented resolution is 256–8192 square. [Terrain overview](https://sbox.game/dev/doc/gameplay/terrain), [creating terrain](https://sbox.game/dev/doc/gameplay/terrain/creating-terrain/), [materials](https://sbox.game/dev/doc/gameplay/terrain/terrain-materials/).
- The installed s&box `Sandbox.Engine.xml` documents `TerrainStorage`, `Terrain.SyncGPUTexture`, `Terrain.ApplyStorageChanges` and `Terrain.UpdateCollision`, but does **not** establish a stable public bulk-write contract here. Its importer is a sensible adapter test, not a constraint on Geppetto's native document.
- `research_notes/desert_environments.md` already collects dune morphology, wind/material reference and erosion leads for the podracing case. Reuse that research, but don't make the core feature desert-only.
- No editor for this project was connected (`sbox-cli --list` returned `[]`), so the engine import and collision behavior still need an in-editor acceptance test.

## Smallest useful vertical slice

1. **Native terrain document**, separate from a mesh body's sculpt and rig workflows. Store typed sources: reference images, elevation grids/DEMs, vector roads and rivers, labeled contours, named masks, and 3D meshes; each retains provenance, bounds, units and a local transform (optional geographic CRS for GIS imports). Use explicit resolution, physical XY extent, vertical datum/range, seed, origin and image row direction. Named, reorderable non-destructive operations; quantize only at export. Test coordinates with an asymmetric corner marker. Do not hard-code inches or an engine-specific axis convention into the kernel.
2. **Map import and roads first:** align a top-down drawing to the world extent; trace/edit a road centerline over it, give the road width, shoulders, bank and target grade, then modify nearby terrain by cut/fill with a controllable blend width. Output both a graded height layer and a road-material mask. A drawn road's color must never be interpreted as elevation. Add broad ridge/height brushes after this; do not lead with noise or erosion. A fixed seed must rebuild bit-identically, and out-of-range heights need a visible warning rather than silent clipping.
3. **Export portable outputs first:** a 16-bit heightmap plus independent named masks and optional mesh (OBJ/glTF when supported), with a manifest of resolution, physical extent, vertical range, min/max, units, origin, seed and coordinate convention. Provide a lossless working elevation format for values outside unsigned 16-bit range (e.g. float GeoTIFF/EXR, subject to library support). No 8-bit intermediate or gamma correction. The native project must retain every editable source.
4. **Prove portability:** reopen the native document and compare results; test an independent image/GIS or mesh consumer. Then test the s&box adapter: confirm PNG/RAW format, row order and height mapping with a known-value fixture, import a real Terrain asset, verify height/collision and save/reload. Engine-specific tests cannot be the sole acceptance gate.
5. Player-facing UI belongs in Effigy's terrain workspace/tool list and Space search, with a plain tooltip; add a CHANGELOG entry and usability-audit row when it ships. A headless build-script verb and kernel tests should share the same implementation, not duplicate the algorithm.

**Initial acceptance fixture:** 256 × 256 samples, one elevated corner, one ridge, and a road drawn over a top-down reference that cuts through the ridge. Re-export twice and compare bytes; reopen the native document and confirm the road/path remains editable, the corner stays in the right quadrant, and heights agree within one output quantization step. Import into s&box later and verify collision and persistence, without a Geppetto component in the scene. At 2048 resolution the height array alone is about 8 MiB; avoid keeping full-grid copies per brush stroke.

## After that, in order

| Priority | Tool | Why / dependency |
|---|---|---|
| Next | Height-aware contour interpolation and DEM/GeoTIFF import | Contour *geometry* requires a numeric elevation per line and enough constraints to infer the surface between lines. A photographed topo sheet is only reference art until traced and assigned heights. |
| Next | Named mask and material-weight export | Wet ground, rock, sand and track need different surfaces. Export portable maps first; map them to each target's material system in its adapter. |
| Later | Directional dunes, terrace/mesa, talus and erosion | Specialized shape operators after the basic grid and masks are trustworthy. Dunes share one wind vector; steep slip faces should be constrained by grid spacing and slope, not a generic fBm displacement. |
| Later | Mesh extras and scatter bake | Portable meshes for non-heightfield forms; saved instances in each scene target. Scatter must be deterministic, editable and baked—never runtime-only preview objects. |
| Investigate | s&box direct `.terrain` writes and in-editor live preview | Adapter-specific convenience only, after validating engine API and undo/save/collision behavior in a running editor. |

## Open decisions / risks

- Is terrain authoring best exposed as a sixth **Terrain** workspace in Effigy, or as its own small window sharing Geppetto's kernel? Choose from the native workflow, not an engine importer's UX. Do not squeeze a kilometer-scale height grid into a `PolyMesh` feature merely because that pipeline already exists.
- Each target has its own importer format, channel interpretation, image origin and height normalization; adapters need separate fixture tests. The s&box public docs promise 16-bit import but don't specify all of these details.
- High vertical range wastes low-elevation precision; show the resulting height step (`verticalRange / 65535`) in the export UI.
- Operations need dirty-region evaluation, bounds checks and a memory budget before advertising 4096–8192 grids. Preserve authored data when changing resolution or range; require an explicit resample/confirm rather than silently reshaping terrain.
- External algorithms are references, not drop-in dependencies: review license and numerical behavior before porting any erosion implementation. `research_notes/desert_environments.md` §13 records candidate repositories.

## Map-first pipeline and references (2026-10-17)

| Input | What it really contains | Effigy treatment | Bake target |
|---|---|---|---|
| Top-down illustration, satellite image, screenshot | Color and *hints* of roads, valleys, land cover; not reliable elevation | Reference overlay, calibrated to world extent; trace/edit paths and regions | None directly |
| DEM / grayscale **16-bit** heightmap | Numeric elevation samples (with explicit scale and no-data handling) | Base height layer; preserve source and vertical units | Native elevation layer → portable/engine exports |
| Topographic contour polylines | Constant elevation **if each line has a labeled Z** | Constrained interpolation to a DEM, with ridge/stream breaklines and manual correction; don't equate line color with height | Height layer |
| Road/river centerline vectors | Route and optional width, grade, bank, flow direction | Sample existing height, then grade/cut/fill road or carve channel; emit independent masks; bridge/culvert is separate model where appropriate | Height + masks + optional mesh details |
| Painted masks / polygons | Where a surface or process should apply, not its height | Sand/rock/road/water/no-scatter controls; preview each independently | Named masks; adapter maps to target materials |
| Fully 3D shape | Overhangs, cave, arch, cliff undercut | Regular Effigy mesh/model, with optional terrain cutout | Portable mesh; engine-specific asset/placement adapters |

**Useful precedents:** [World Machine Layout](https://www.world-machine.com/features.php) sketches ridges, roads and rivers before generating terrain; [Houdini HeightField Output](https://www.sidefx.com/docs/houdini/nodes/sop/heightfield_output.html) exports separate height/mask layers; [Houdini HeightField Project](https://www.sidefx.com/docs/houdini/nodes/sop/heightfield_project.html) projects geometry onto a heightfield. [QGIS interpolation](https://docs.qgis.org/latest/en/docs/gentle_gis_introduction/spatial_analysis_interpolation.html) explains deriving a raster from known elevations, and its [Georeferencer](https://docs.qgis.org/latest/en/docs/user_manual/managing_data_source/georeferencer.html) aligns an arbitrary image to coordinates. [Blender Geometry Nodes](https://docs.blender.org/manual/en/latest/modeling/geometry_nodes/introduction.html) is a useful open-source model for mixing curves, meshes and fields. These are workflow references, not libraries to bundle; World Machine, Houdini and Gaea are not open-source tools.

**Critical design point:** One top-down picture is not a universal terrain file. Interpret an image only after the user chooses its role—reference, calibrated height, or a named mask. A topographic **map** (contours and labels) is different from mesh **topology** (vertices and faces). The editable Geppetto document keeps all inputs and operations; heightmaps, meshes and engine assets are outputs, not its identity.
