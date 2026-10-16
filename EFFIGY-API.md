# Building models with Effigy from code

Effigy's kernel (`Effigy/`) is plain C# with no engine types in it, so you can build, inspect
and export a model from a console program, a test, or a script, with no editor running. This is
the surface you need for that. Working generators to copy from: `Effigy.Tests/TreeGen.cs`,
`TentacleGen.cs` and `PaintGen.cs`.

There are three ways in, from most control to least:

1. **C#** against `PartStudio` and its features: everything below.
2. **A build script**, one move per line, run headlessly or on the open editor. See
   [Build scripts](#build-scripts).
3. **The shell**: `tools/effigy.sh` to render, describe, measure and fix a saved `.effigy`. See
   [The CLI](#the-cli).

## The shape of every generator

```csharp
var studio = new PartStudio();

var box = studio.Add( new PrimitiveFeature() );
box.Name = "Field slab";
box.Shape.Index = 0;                       // Box
box.SizeX.Value = 4000f;
box.SizeY.Value = 3000f;
box.SizeZ.Value = 40f;
box.Position.Value = new Vec3( 0, 0, 0 );

var report = studio.Rebuild();
if ( report.HasErrors ) throw new Exception( report.ToString() );

var mesh = studio.ToMesh();                // every body; ToVisibleMesh() skips hidden ones
```

`studio.Add( feature )` appends a feature and returns it, typed. Order is history: features run
top to bottom, each acting on what the ones above produced. Nothing is evaluated until
`Rebuild()`. `Insert`, `Remove` and `Move` edit the list; `studio.Bodies` is what the last
rebuild produced.

## Parameters

Every feature exposes typed parameters:

| Type | Set it with | Note |
|---|---|---|
| `FloatParam` | `.Value = 1.5f` | `.Clamped` respects min/max |
| `IntParam` | `.Value = 3` | also has `.Clamped` |
| `BoolParam` | `.Value = true` | |
| `Vec3Param` | `.Value = new Vec3( x, y, z )` | |
| `ChoiceParam` | `.Index = 2` | **`.Value` is read-only**: it is `Options[Index]` |
| `StringParam` | `.Value = "..."` | paths, outline text |
| `BodySelectionParam` | `.BodyIds.Add( id )` | **empty means every body** |

## Garment details

After a `GarmentFeature`, add `PocketFeature` (Patch/Cargo/Zipper), `HardwareFeature`
(studs, spikes, eyelets, pins, chain, strap/buckle, port or cable), `WearFeature`
(Abrasion/Dirt/Rip/Hole), or `GlowFeature` (LED panel, strip, EL wire, armour or tape).
All take `Bodies`, `Spot`, `Side`, `Across`, `Up` and `Angle`. Distances are inches;
spots come from the rig. Empty `Bodies` decorates all garments, excluding other details.
Select a specific garment when layering. New detail bodies carry `IsGarmentDetail`
and inherit the garment's skin weights. `IDetailMaterials` supplies their material recipes;
the editor writes and binds those recipes automatically. Headless callers must write/bind
the recipes themselves (see `Effigy.Tests/GarmentDetailDemo.cs`).

`Hardware.Layout` places rows around openings; `Glow.Openings` does the same for EL wire.
`Wear.Area` targets a body part or openings. Cuts remove whole faces, so subdivide before
small tears; put cuts before Fur. Wear/dirt preserve generated Fabric settings, but a custom
external fabric currently falls back to a generated jersey variant. Eyelet centres are dark
insets, not bores through the underlying garment. Tape is a bright material, not a physically
retroreflective shader. LED patterns scroll; they are not GIF/video playback.

Run focused checks with `dotnet run --project Effigy.Tests -- --garment-details`.
Generate a small cropped-jacket-over-shirt example with
`dotnet run --project Effigy.Tests -- --garment-details-demo /tmp/opencode/garment-preview`
(requires the Citizen source FBX). Judge its OBJs using `sh tools/effigy.sh shade`.

## Body ids

A feature's bodies are `feature.Id + "b0"`, `"b1"`, … in the order it made them. That is how a
later feature points at an earlier one:

```csharp
var move = studio.Add( new TransformFeature() );
move.Bodies.BodyIds.Add( box.Id + "b0" );
move.Translate.Value = new Vec3( 0, 0, 100 );
```

Ids are stable across rebuilds, which is why body names, materials and collision settings are
keyed on them.

## Sketches and what consumes them

```csharp
var sk = studio.Add( new SketchFeature() );
sk.Name = "Plate outline";
sk.Plane.Index = 0;                                          // 0 Top (XY), 1 Front (XZ), 2 Right (YZ)
sk.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 40, 20 ) );
sk.Sketch.AddCircle( new Vec2( 10, 10 ), 3 );                // a hole: regions are found, not declared

var a = sk.Sketch.AddPoint( 50, 0 );                         // AddPoint returns an index
var b = sk.Sketch.AddPoint( 60, 10 );
sk.Sketch.Add( new SketchLine( a, b ) );                     // or AddLine( Vec2, Vec2 ), AddPolygon( ... )

var ex = studio.Add( new ExtrudeFeature() );
ex.SketchFeatureId = sk.Id;
ex.Distance.Value = 5f;
```

Closed regions are **found** by the profile finder, so a circle inside a rectangle is a hole.

A sketch sits on one of three things; the most specific one that is set wins:
`PlaneFeatureId` (a `PlaneFeature`), then `Face` (a face of an existing body), then
`Plane.Index`. `PlaneOffset` shifts a global plane along its normal.

**`ExtrudeFeature`**: `Distance`, `Symmetric`, `Flip`, `SecondDistance`, `Taper` (degrees),
`Termination` (`0` Blind, `1` Up to next, `2` Through all), `Material`, and `Result`:
`0` Auto (the default), `1` New body, `2` Add to the body it grows from, `3` Remove from the
body it cuts into. Set `Faces` instead of a sketch to pull faces of an existing part.

**`RevolveFeature`**, **`SweepFeature`** (`PathSketchId` or `PathSplineId`, `Twist`) and
**`LoftFeature`** (`Sections`, a list of sketch ids; `Segments`, `Closed`) consume sketches the
same way.

**`PlaneFeature`** is a construction plane with no geometry: `Base.Index` (same order as
`SketchFeature.Plane`) or `Face` or `BasePlaneId`, then `Offset` along the normal and `Angle`
(±90°) hinged on `Hinge.Index` (`0` its X axis, `1` its Y axis).

```csharp
var roof = studio.Add( new PlaneFeature() );
roof.Name = "Roof line";
roof.Offset.Value = 12f;
roof.Angle.Value = 30f;

var onRoof = studio.Add( new SketchFeature() );
onRoof.PlaneFeatureId = roof.Id;
```

`FacePlane.Capture( body, faceIndex, point )` makes the `FaceRef` that `Face` takes; a plane or
sketch built that way follows the face when the part changes.

## Features

### Making bodies

| Feature | Parameters |
|---|---|
| `PrimitiveFeature` | `Shape.Index`: `0` Box, `1` Cylinder, `2` Sphere, `3` Wedge, `4` Tube, `5` Plane. Box/Wedge use `SizeX/Y/Z`; Cylinder `Radius`, `SizeZ`, `Segments`; Sphere `Radius`, `Divisions`; Tube adds `InnerRadius`; Plane `SizeX/Y`, `Segments`. All take `Position`, `Scale`, `RotationAxis`, `RotationAngle`, `Material`. |
| `ImportFeature` | A Wavefront OBJ or an FBX (binary, or ASCII 7.x) as bodies, one per OBJ object or FBX mesh. `BindSource( path )` reads it and keeps the bytes, so a rebuild doesn't need the file to still exist. FBX meshes keep node placement, unit scale (to inches), up axis (to Z), UVs, vertex colours and material slots (`SourceMaterialNames`); skin weights land by bone name on the document's rig, and `ImportFeature.InstallSkeleton( studio, import, out msg )` makes the file's skeleton the rig when there is none (the editor and `add Import source=…` do this on pick). GLB/glTF is refused with a remedy. Conversion lives in `FbxImport.Read( bytes )`. |
| `PartFeature` | A kitbash part: `Part.Index` over Cog, Bolt, Hex bolt, Rivet, Knob, Hinge, Panel, Pipe elbow, Strap, Buckle. `Size`, `Length`, `Thickness`, `Count`, `Angle` mean different things per part (table below). `Position`, `RotationAxis`, `RotationAngle`. |
| `ProfileFeature` | A body from outlines (see below). |
| `SplineFeature` | A 3D path through `Points` (a `List<Vec3>` field). `Tube` makes it a solid pipe with `Radius`, `Taper`, `Sides`, `Twist`. `Closed`, `Smooth`, `Resolution`. |
| `BlobFeature` | Shapes fused into one skin (smooth union, smooth-subtracted cutters, meshed by SurfaceNets). `Shapes` is text, one shape per `;`: `sphere C R`, `ellipsoid C RX,RY,RZ`, `capsule A B R`, `cone A RA B RB`, `tube P1\|P2\|P3 R1\|R2\|R3`; a leading `-` makes a cutter, `mat=N` sets its slot. `Blend`, `CutBlend`, `Voxel`, `Smooth`, `Noise`, `NoiseSize`, `Seed`. No UVs: unwrap before baking. Script: `add Blob shapes="capsule 0,0,0 0,0,12 3; -sphere 0,-3,6 1" voxel=0.3`. |

What each part reads from its dials:

| Part | Size | Length | Thickness | Count | Angle |
|---|---|---|---|---|---|
| Cog | outer radius | tooth depth | height | teeth | |
| Bolt / Hex bolt | head radius | shank length | shank radius | sides | |
| Rivet | radius | | height | sides | |
| Knob | radius | height | | sides | |
| Hinge | knuckle radius | length | leaf thickness | knuckles | how far open |
| Panel | width | height | thickness | | |
| Pipe elbow | pipe radius | bend radius | | sides | bend |
| Strap | width | length | thickness | | |
| Buckle | width | height | bar thickness | | |

**`ProfileFeature`** is how a limb or a torso is described: rings stacked along an axis, sized by
two outlines given as text.

- `Front`: `"height,half-width;…"`, width along y. It needs at least two pairs.
- `Side`: `"height,half-depth;…"`, along x. Empty means the same as Front.
- `Curve`: `"height,forward,sideways;…"` bends the centre line.
- `Twist`: `"height,degrees;…"`.
- `Along`: Up, Forward or Left. `Mirror` adds a twin across y = 0. `Sides` and `Squareness`
  shape the ring.

### Changing bodies

| Feature | Parameters |
|---|---|
| `TransformFeature` | `Bodies`, `RotationAxis`, `RotationAngle` (degrees), `Translate`, `Scale`. Rotation happens before translation. |
| `MirrorFeature` | `Bodies`, `PlanePoint`, `PlaneNormal`, `KeepOriginal`, `Merge`. |
| `LinearPatternFeature` | `Bodies`, `Direction`, `Spacing`, `Count` (up to 4096), `Merge`. |
| `CircularPatternFeature` | `Bodies`, `AxisPoint`, `AxisDirection`, `Count`, `TotalAngle`, `Merge`. |
| `BooleanFeature` | `Operation.Index` (`0` Union, `1` Subtract, `2` Intersect), `Targets`, `Tools`, `KeepTools`, `Method` (Auto, Exact, Voxel), `Voxel` (in, 0 = auto). Tool bodies are consumed unless kept. **Where it works:** `Exact` is the engine's boolean, so it runs only in the s&box editor and refuses elsewhere. `Voxel` is the kernel's `VoxelBoolean` and runs everywhere (CLI, MCP, tests): a fresh surface-nets skin with edges rounded by about a voxel, no UVs or weights, and a face count set by the voxel. `Auto` (the default) takes Exact where the engine is installed and Voxel otherwise, and warns when it used Voxel. Both inputs must be closed. Extrude's Remove and `HoleFeature` still need the engine. |
| `ShellFeature` | `Bodies`, `Thickness`. |
| `FilletFeature` | `Radius`, `Segments`, `AngleThreshold`, `Edges`. Leave `Edges` empty for every sharp edge. |
| `ChamferFeature` | `Width`, `AngleThreshold`, `Edges`. |
| `HoleFeature` | `Faces`, `Style` (Simple, Counterbore, Countersink), `Diameter`, `Depth` (0 = through), `HeadDiameter`, `HeadDepth`, `SinkAngle`. |
| `MoveFaceFeature` | `Faces`, `Mode` (Offset, Translate), `Distance`, `Direction`. |
| `DeleteFaceFeature` | `Faces`. Deletes them and heals the surface. |
| `DraftFeature` | `Faces`, `Pull`, `Angle`, `Neutral`. |
| `CurveDeformFeature` | Bends `Bodies` along a spline: `SplineId` (empty means the last one), `Axis`, `Stretch`. |
| `SubdivideFeature` | `Bodies`, `Levels` (0–6), `AllFaces`. |
| `RemeshFeature` | `Bodies`, `Mode` (Decimate, Quad, Voxel). Decimate: `Target` (Percentage, Triangle count), `Percent`, `Triangles`, `HoldBorders`, `Weld`; takes a dense import down to a triangle budget. Quad: `Faces`, `SymmetryX`; an all-quad retopo. Voxel: `Voxel` (edge length, in), `Fuse` (melt overlapping parts into one body, default on), `Fillet` (in), `Smooth`; Dynamesh. Voxel keeps material slots, drops UVs and weights. |
| `StrokeFeature` | One sculpt stroke as data, replayed every rebuild: `Bodies`, `Points` (the path, landmarks allowed; projected onto the skin), `Brush` (any `BrushKind`: Crease, Clay, ClayStrips, SnakeHook, Inflate…), `Radius`, `Strength`, `Invert`, `Falloff`, `Mirror`, `Spacing`, `Only` (Everywhere, Creases, Ridges), `Alpha PNG`, `AlphaScale`. Fails if it moves nothing (path off the body, or a radius under two edges: subdivide first). |
| `DetailFeature` ("Surface detail") | SurfaceDetail skin passes: `Pass` Grain (fbm lumps + Worley crease cells), Pustules (clusters), Scatter (Poisson domes, or pits), Sag folds. `Size`, `Depth`, `Lumps`, `LumpSize`, `Clusters`, `PerCluster`, `Spread`, `Spacing`, `Pits`; masks `Busy` (share of skin, default 0.4), `Only` (creases/ridges), `Near` + `Reach`. Script: `add Detail bodies=B pass=Grain size=1.2 depth=0.06`. |
| `SculptFeature` | `Bodies`, `Reproject`. Sculpt deltas live in a sidecar beside the `.effigy`. |
| `MeshEditFeature` | A direct, by-hand edit of a body, with live mirror, array, subdivide and thickness modifiers. Written by the editor's Model workspace. |
| `EditOpFeature` ("Edit op") | One edit-mode operation replayed through `MeshEditSession` on every rebuild: `Bodies`, `Op` (Extrude, Extrude individual, Extrude along normals, Inset, Bevel, Loop cut, Bridge, Symmetrize, Dissolve, Limited dissolve, Delete), `Select` (a rule, see [Edit ops](#edit-ops)), `Element` (Auto, Faces, Edges, Vertices), `Distance`, `Segments`, `At`, `Axis`, `Side`. Unlike `MeshEditFeature` it stores what was asked, not the result, so it follows upstream changes. Script: `edit Body op …`. |

### Surfaces and materials

| Feature | Parameters |
|---|---|
| `FaceMaterialFeature` | `Faces`, `Material` (a slot number). |
| `UVProjectFeature` | `Mode` (Box, Planar, Unwrap, Organic: LSCM-flattened, few seams, for sculpted blobs), `Direction`, `Scale` (units per tile), `ChartAngle`, `Margin`. |
| `PaintFeature` | One paint layer, composited over the paint below it. `Bodies`, `Resolution`, `Mode` (Blend Mode: Normal, Multiply, Screen, Overlay, Add), `Opacity`, `Fill` (Fill Layer) with `FillUseColor`/`FillColor`, `FillUseRoughness`/`FillRoughness`, `FillUseMetalness`/`FillMetalness`, `FillUseHeight`/`FillHeight`; `MaskGenerator` (None, Edge Wear, Dirt, Gradient, Material ID), `MaskLevel`, `MaskSoftness`, `InvertMask`, `MaskSlot`; `Strokes` (each on a channel: colour, or `@rough`/`@metal`/`@height`/`@mask` tagged in the file). `Blend` (Tint/Replace) is legacy: loaded and saved, read by nothing. Kernel: `PaintLayers`, `PaintMasks`, `PaintTextureSet`; script verb `paintmaps Body folder [name=] [assets=] [bump=2]`. |
| `FleshMaterialFeature` | `Bodies`, `Preset` (Wet flesh, Raw meat, Lung, Gut mucosa, Bone, Tooth, Gum, Wet gore, Fat, Infected skin, Oil black, Black spike, Entity ribbon), `Resolution`, `Detail` (noise size ×), `Seed`, `Folder` (writes `flesh_<body>_color/_rough[/_glow].png` + `.vmat`), `Show`, `PerVertex` (colour the vertices; no UVs, no files; s&box's shaders ignore vertex colour, so it warns — for preview or a custom shader), `Seeds` (`"x,y,z;x,y,z"`: corruption grows from these over the surface), `Reach` and `Front` (in; front = reach bands skin → bruise → raw → oil). Without seeds, corruption is the vertex colours' red channel. `Shader` (Preset's, Skin, Complex): living-tissue presets go out on s&box's `shaders/skin.shader` (SSS, red AO bleed, wet second lobe, transmission; `F_MORPH_SUPPORTED 1` when the body has shape keys) and also write `_ao.png` and `_transmission.png`; Bone, Tooth, Oil black, Black spike and Entity ribbon stay on complex (skin has no glow and refuses see-through). `LightThrough` (× the preset's k: Lung/Fat 3, Wet flesh/Gut 2, others 1; 0 = none): the transmission mask is skin's red transmittance through the wall thickness (`FleshMaterial.Thickness`: a ray along −N per vertex) ÷ k. Warns when skin drops a glow or when over 40% of the surface lets light through. Kernel: `FleshStack`/`FleshLayer`/`Mask` (Cavity, Ao, Curvature, Height, Worley, Fbm, Painted, VertexColour, Corruption, Triplanar), `TriplanarImage`, `FleshMaterial.Corruption`/`PackMasks`/`BakeVertices`/`Shade`. |

### Clothing

| Feature | Parameters |
|---|---|
| `WearerFeature` | An imported rigged model to dress. Never exported. |
| `GarmentFeature` | A garment recipe fitted to the wearer's body: `Recipe` (T-shirt, Long sleeve, Tank top, Trousers, Shorts, Beanie, Gloves, Socks, Bodysuit), `Length`, `Sleeve`, `Neckline`, `Fit`, `Looseness`, `Flare`, `Cinch`, `Wrinkles`, `Thickness`, `Drape`, `Fabric`, `Clearance` and more. A recipe, not a mesh, so it refits when the body changes. |
| `TrimFeature` | `Style` (Frill, Ribbon, Pleats, Fringe) on `Where` (All openings, Hem, Collar, Cuffs). |
| `FurFeature` | Fur shells on a garment's faces. |
| `FabricFeature` | A procedural tiling fabric material bound to a slot: Jersey, Rib knit, Fleece, Denim, Corduroy, Leather, Satin, Canvas, Plaid, Gingham, Quilted. |

## Naming

```csharp
feature.Name = "Front rail";                                // every feature, sketches included
studio.BodyNames[box.Id + "b0"] = "Plate";                  // every body that survives
studio.MaterialNames[1] = "materials/dev/gray_50.vmat";      // slot -> .vmat, the SOURCE path
```

Name things for what they are, as you add them. A tree of `Box 1`, `Extrude 3`, `Mirror 2` is
unreadable a week later, and body names are what landmarks, scripts and `describe` use.

## Saving and exporting

```csharp
StudioDocument.WriteFile( studio, "part.effigy" );          // ReadFile( path ) loads one back
ObjWriter.WriteFile( mesh, "part.obj", "part" );
FbxWriter.WriteBinaryFile( mesh, "part.fbx" );              // pass a Skeleton for bones and skin
```

What the engine gets is what the bake used. `DmxWriter` (the rigged compile path) writes the faces
**triangulated the bake's way** (`NormalBake.FaceTriangles`) and per-corner **`tangent$0`** from
`NormalBake.ExportTangents` (xyz tangent, w = bitangent sign, s&box's convention); pass
`triangulate: false` / `tangents: false` for n-gons or none. `ObjWriter` takes `triangulate: true`
(the editor's static compile uses it; OBJ cannot carry tangents). `FbxWriter` writes tangent and
binormal layers and keeps quads unless `triangulate: true`. Vertex colour goes out as **straight
RGBA** in DMX and FBX, so packed masks (`FleshMaterial.PackMasks`: R corruption, G wet, B veins,
A AO) survive; OBJ has no alpha. Every writer keeps the four strongest bone weights and renormalises.

`.effigy` is plain text, one line per thing, so it diffs well in git. Imports, sculpts and mesh
edits are written beside it by `ImportSidecar`, `SculptSidecar` and `MeshEditSidecar`.

For a `.vmdl`, build the text with `VmdlDocument`:

```csharp
var vmdl = VmdlDocument.Static( "part.obj",
	VmdlPhysics.MeshFromRender( "metal" ),
	VmdlMaterials.GroupList( studio, mesh ) );
```

`VmdlDocument.Skinned( meshFile, skeleton, physics, materials )` is the rigged version;
`TreeGen.cs` shows it end to end. **Don't hand-write the material group list.** Every slot the
mesh uses must name a real asset, or the compiled model renders in the bright red
missing-material shader.

## Looking at it

```csharp
var image = Effigy.Render.ModelRender.Named( studio.Bodies, "sheet", 512 );
image.SavePng( "part.png" );
```

`"sheet"` is front, side, top and iso on one image, with a ground grid (its step is printed),
axes (x red forward, y green left, z blue up), wire and body labels. Other views: `front`,
`side`, `top`, `iso`, `back`, `left`, `turn8` (a turntable) or `turn45` (one angle).
`RenderOptions` turns off the wire, grid, labels and axes, or draws bones.

### Lit renders: ShadedRender

`ModelRender` is for measuring; `Effigy.Render.ShadedRender.Render( scene, camera, w, h, ss )` is
for judging how a thing looks (CPU only, no Blender). Every option below is off by default, so old
scenes render as they did; `scene.Portfolio()` turns the lot on (studio IBL, 24-sample denoised AO
with bent normals, contact shadows, bloom 0.6, AgX punchy, vignette 0.3, floor reflection 0.15, tent
filter). That costs 2-2.5x a plain render (900x1200 at ss=2 on 16 threads: ~4.4 s against ~1.9 s).

```csharp
var scene = new ShadeScene().Portfolio();          // or set the pieces below one by one
scene.Add( helmet, looks, looks[Shell] );          // one mesh, a material per Face.Material id
scene.Add( glove, new ShadeMaterial { Albedo = …, Roughness = 0.4f, Coat = 0.5f, CoatRoughness = 0.15f,
	MapSize = 512, AlbedoMap = grain, RoughnessMap = grainRough, NormalMap = grainNormal,
	MapScale = new Vec2( 12, 12 ) } );              // tiles 12x; or TriplanarScale = 0.5f with no UVs
```

| Scene field | What it does |
|---|---|
| `Environment` | A `ShadeEnvironment` (`ShadeEnvironment.Studio()`: warm key softbox, cool fill, top light, two rim strips, a horizon seam; add your own `ShadeSoftbox`es). Reflections and diffuse fill come from it through split-sum GGX (pre-filtered once and cached; multiple-scattering compensation keeps rough metals bright). `CameraRelative` (default) keeps the lighting the same in every view of a turnaround; `Diffuse` / `Specular` scale it. Not drawn as the backdrop. |
| `Bloom`, `BloomThreshold`, `BloomRadius`, `EmissiveBloom` | Glow round bright pixels; emission always blooms. |
| `Tonemap` | `Legacy` (default), `Aces`, `AgX`, `AgXPunchy`. |
| `Vignette` | Darkens the corners, 0..1. |
| `FloorReflection`, `FloorReflectionBlur` | Screen-space mirror on the floor. |
| `AoSamples`, `AoDenoise` | More occlusion samples, and a depth/normal-aware blur. |
| `ContactShadows` | Short screen-space shadow rays toward each light: model units, negative = half of `AoRadius`. |
| `SoftFilter` | Tent instead of box downsampling: thin parts stop stair-stepping. |
| `FaceMaterials` | Filled by `Add( mesh, byFaceMaterial, fallback )`. Prefer it to splitting a mesh by material: split parts get separate normals, so every material boundary shows as a lit seam. |

| Material field | What it does |
|---|---|
| `AlbedoMap`, `RoughnessMap`, `MetallicMap`, `NormalMap`, `AoMap`, `EmissionMap`, `MapSize`, `MapTint` | RGBA maps on the face UVs, all `MapSize` square; colour maps sRGB. Normal maps are tangent space, OpenGL green. All are mip-mapped: a fine tiling fades to its average at distance, and a mip-shortened normal raises roughness (Toksvig) instead of sparkling. |
| `MapScale` | Repeats across 0..1 UV. Anything but (1, 1) also wraps v. |
| `Mapping`, `TriplanarScale` | `Auto` uses UVs where a triangle has them and the triplanar projection (`TriplanarScale` model units per tile) where it does not; `UV` / `Triplanar` force one. |
| `NormalStrength` | Scales the normal map's tilt. |
| `CoatFollowsNormalMap` | Off (default): the clear coat reflects off the smooth surface over the bumpy base, which is what makes lacquered carbon and polished leather read as glossy. |

`tools/effigy.sh shade model.obj out style=studio` is the same look from the command line;
`bloom=`, `tonemap=agx|punchy|aces`, `vignette=`, `floorrefl=`, `contact=` and `ibl=0|1` set one
piece each.

**Jagged colour edges are in the mesh, not the renderer.** A face carries one material, so an accent
painted by face material runs along triangle edges; on an irregular (SDF or remeshed) surface that
is a zigzag no supersampling removes. Cut the faces along the real boundary, make the accent
geometry (a groove or a raised trim), or put it in a texture mask.

## Build scripts

A build script is a model as a list of moves, one per line. `BuildScript.Run( studio, text )`
runs it in C#, `tools/effigy.sh script build.txt` runs it from the shell, and `effigy_run` runs
it on the editor's open document as one undo step.

```
add Profile name=Torso front=0,5;8,9;22,10;30,8;34,5 side=0,4;8,6;22,6;34,4 position=0,0,20
add Primitive name=Head shape=Box sizex=9 sizey=10 sizez=9 position=@Torso.top+0,0,6
add Part name=Cog part=Cog size=4 thickness=1.5 count=10 position=@Torso.front+1,0,0
add Spline name=Cable points=@Torso.back;-10,4,40;@Head.back radius=0.6
set Head sizez=12
fix Torso all
describe
render out/robot.png
save out/robot.effigy
```

| Verb | Form |
|---|---|
| `add` | `add TYPE key=value…` (the class name, with or without `Feature`) |
| `edit` | `edit Body op [select=rule] [distance=] [segments=] [at=] [axis=] [side=] [element=]` (an `EditOpFeature`; see [Edit ops](#edit-ops)) |
| `set` | `set Feature key=value…` |
| `remove` | `remove Feature` |
| `rebuild` | `rebuild` |
| `fix` | `fix Body\|all loose doubles holes normals` |
| `render` | `render path [view=sheet] [size=512] [wire=1] [bones=0] [labels=1] [grid=1] [focus=Body]` |
| `render … shade=` | `render path shade=material\|clay [views=4] [size=512] [elevation=12] [preset=Wet gore] [focus=Body] [label=OURS]`: a lit, labelled turnaround (ShadedRender: wet coat, subsurface glow, oil film, baked maps). A body shows its Flesh Material; with none, `preset=` per vertex, else its vertex colours, else clay. |
| `describe` | `describe` |
| `measure` / `at` | `measure landmark` |
| `distance` | `distance A B` |
| `symmetry` | `symmetry [Body]` |
| `topology` | `topology [Body\|all] [poles=12]`: welded face kinds (quad/tri/n-gon %), non-manifold and open edges, poles by valence with places as `@bone+dx,dy,dz` (or `@Body.side+…` without a rig) and their distance to the nearest joint in edge lengths, edge-length mean/CV, quad aspect and skew (mean, 90th percentile, max), smallest triangle angle, left-right Hausdorff across y = 0, and with a rig the edge loops within ±1 limb radius of each bone head. Each line carries its craft target; a miss starts with `!`. Also Object mode ▸ Mesh ▸ Topology report. (`TopologyReport`) |
| `section` | `section Body\|all z=10,20 \| x=… \| y=… \| along=Bone [at=0,0.5,1]`: one line per slice with width (along y; across the bone along its X), depth, perimeter, area and centre, and each outline separately when the cut meets several (two legs) or a hole (a sleeve). A list may be a range, `z=0..60:7`. Fails only when every slice misses, and gives the body's span. (`SectionReport`) |
| `flex` | `flex Body\|all [bones=a,b] [angles=90,140] [axis=auto\|x\|y\|z\|x,y,z] [render=dir] [views=4] [size=320]`: binds as `export` does (body→bone map, smoothing, weight paint), bends each bone about its hinge (across the chain's existing bend, else the bone's X) and reports the local volume ratio (cone volume from the joint over triangles within two limb radii; target ≥ 0.8) and self-crossings new against the bind pose (target 0). `render=` writes a clay still per bend, `OURS · bone angle`. Default bones: those with skin on both sides of the joint. (`FlexReport`) |
| `match` | `match view ref.png [diff.png]`, or several views at once: `match front=ref_front.png side=ref_side.png [out=dir]` adds a combined line (mean overlap, worst view) and writes `match_<view>.png` diffs to `out=` |
| `material` | `material slot path.vmat` |
| `pivot` | `pivot Body @landmark` |
| `export` | `export folder [split=1] [assets=root] [name=x] [lods=0\|1\|0.5,0.25] [loddistance=a,b] [collision=none\|auto\|hulls\|render] [surface=default] [skin=auto\|0\|1] [fbx=1]`. Static: `.obj` + `.vmdl` (+ `_lodN.obj` and a LOD list with `lods=`; `lods=1` is half and a quarter, switching at 6x and 15x the model's diagonal unless `loddistance=` says). Skinned (default when the studio has bones and `split` is off): `.dmx` + `.smd` + a skinned `.vmdl`, bound like the editor's export (body→bone map, smoothing, weight paint), skeleton moved with the pivot; no LODs. `collision=auto` is the editor's choice (exact shapes from the history, else a hull per body; on a skinned model a hull per body on its bone), `hulls` a hull per body, `render` physics from the render mesh; default none. `fbx=1` adds a binary `.fbx` (skinned when the model is) for Blender; the `.vmdl` still loads the `.obj`/`.dmx`. Reference bodies are left out. |
| `bake` | `bake LOW HIGH[,HIGH2…] folder` or `bake * folder` (pairs by _low/_high/_cage name) `[size=1024] [maps=normal,ao,curvature,cavity,height,thickness,bentnormal,objectnormal,position,id\|all\|everything] [cage=0\|CageBody] [ss=1] [bits=8\|16] [id=material\|body] [vmat=0] [shader=complex\|skin preset= k=]` (see [Baking](#baking)) |
| `corrective` | `corrective bone=elbow_L angle=90 [axis=x,y,z]` adds a corrective shape (saved with the rig); `corrective remove bone= [angle=]`; bare `corrective` lists them. At a skinned export each becomes a morph `bone_bend90` built from the export's own 4-bone weights (`CorrectiveMorph`): it pushes the blend zone's in-plane offset from the joint out by 1/s(w) − 1, so at that bend the joint keeps its rest volume instead of cos(A/2) of it. Drive it 0 straight → 1 at the angle (`CorrectiveMorph.WeightAt` for in between). The export also logs when the 4-weight cap drops more than 5% of a vertex's weight (`SkinWeights.CapLoss`). |
| `save` | `save path` |
| `echo` | `echo text` |

How values are read:

- Keys match a parameter's label or field name, ignoring case, spaces and `_`.
- A choice takes its option text (exact, then a prefix, then a substring) or its index.
- A `Vec3` is `x,y,z` or a landmark plus an offset: `@Torso.top+0,0,6`.
- A point list is `a;b;c`. A body list is body names. A feature is named by its name, its id,
  or `#N` (1-based).
- A failed feature stops the script and reports its own error, which tells you what to change.

**Landmarks** name a place on the model as built so far: a body name (its centre),
`Body.top`, `.bottom`, `.front`, `.back`, `.left`, `.right`, `.min`, `.max`, `.centre`; a bone
name (its head; `.tail` and `.mid` too); `ground`; `origin`; a literal `x,y,z`; or
`@landmark+dx,dy,dz`.

### Edit ops

`edit Body op …` adds an `EditOpFeature`: one edit-mode operation, picked by a rule rather than
by clicking, and replayed every rebuild (so `set Torso sizez=40` above it moves the extrusion).

```
edit Torso extrude select=normal:up distance=4
edit Torso inset select=normal:up&height:90%.. distance=1.5
edit Torso bevel select=normal:up distance=0.5 segments=3       # the rim of the picked faces
edit Torso bevel element=edges select=sharp:30 distance=0.25
edit Torso loopcut at=@Torso.top-0,0,6 axis=z                   # the ring crossing that point
edit Torso delete select=near:@Torso.front~3
edit Torso bridge select=boundary                               # exactly two open rims
edit Torso symmetrize axis=x side=positive                      # keep +x, mirror it onto -x
edit Torso dissolve element=faces select=normal:front           # merge the picked faces into one
edit Torso limiteddissolve
```

| Op | Uses |
|---|---|
| `extrude`, `extrudeindividual`, `extrudealongnormals` | faces (`element=edges` extrudes edges into walls), `distance` |
| `inset` | faces, `distance` (refused past the middle, with the most that fits) |
| `bevel` | the rim of the picked faces; `element=edges` the picked edges; `element=vertices` a vertex bevel; `distance`, `segments` (1 = chamfer) |
| `loopcut` | `at` (point or landmark) and `axis` (the direction of the edges being cut); `select` narrows the candidate edges |
| `bridge` | the picked open boundary edges; must be two rims |
| `symmetrize` | `axis` x/y/z, `side` positive/negative |
| `dissolve` | edges (default), `element=vertices`, or `element=faces` (the edges between them) |
| `limiteddissolve` | merges coplanar faces over the whole body |
| `delete` | faces (default), or the faces touching picked edges/vertices |

**Selection rules** (`select=`; left out, the up-facing faces for the extrudes and everything for the rest): `all`; `index:3,5,10..20` (faces or vertices;
edges as `index:3/4,4/9`); `normal:up~20` (within 20°, default 30, of
up/down/front/back/left/right or `x,y,z`); `x:a..b`, `y:`, `z:`, `height:` (a band on the
element's centre; either end optional; `80%` is of the body's bounds); `near:x,y,z~r` or
`near:@Landmark~r`; `sharp:30` (edges whose faces meet at more than 30°; a face or vertex
on one); `boundary` (open edges; a face or vertex on one). Join with `&` (and) and `|` (or);
`!` negates a term. Faces are judged by centroid and normal, edges by midpoint and mean normal,
vertices by position and mean normal. Landmarks resolve when the line runs, so the saved
feature holds numbers.

Every op fails rather than doing nothing: a rule that picks nothing says how many each term
picked on its own (and for `normal:`, how far off the closest face is); an operation the
session reports as changing nothing fails with the session's reason.

### Baking

`bake LOW HIGH folder` bakes the HIGH body onto the LOW body's UVs, one PNG per map, named
`<name>_<map>.png`. It logs each file it writes, followed by the numbers needed to read the maps.
LOW must have non-overlapping UVs (`add UVProject bodies=LOW mode=Unwrap`); otherwise the verb
refuses and says why. In C#, the same bake is `NormalBake.BakeAll( low, high, w, h, BakeMaps.All,
options )`, which returns a `BakeSet`.

```
add UVProject bodies=Cage mode=Unwrap
bake Cage Sculpt materials/creature size=2048 maps=normal,ao,curvature,cavity,height cage=0.5 ss=2 vmat=1 assets=C:/proj/Assets
```

Every map is sampled per texel from the high mesh, not interpolated from its vertices.

| Map | What it is |
|---|---|
| `normal` | Tangent space, on MikkTSpace tangents (checked against Blender's mikktspace.c) over the same triangles the exporters write, interpolated the way a shader rebuilds them |
| `ao` | Cosine-weighted hemisphere rays (`rays=64`, out to `aodistance=`, default 1/20 of the sculpt's diagonal). White is open |
| `curvature` | How fast the high normal turns across 1.5 texels. Mid grey is flat, brighter convex, darker concave. White's value is logged |
| `cavity` | Depth below a ring of neighbours 4 texels out, measured against the cage so the cage's own curve doesn't count. White on flats and ridges, black in pits |
| `height` | Signed distance from the cage along its normal. Mid grey is on the cage. The logged `+-range` is what black and white stand for |
| `bentnormal` | The average unblocked direction of the AO rays, in the same tangent space and green convention as `normal`. Bound by the skin vmat as `TextureBentNormal` with `F_USE_BENT_NORMALS 1` |
| `objectnormal` | The high normal in object space, x y z straight into R G B (−1 black, +1 white). Unhit texels mid grey |
| `position` | The hit point across the highs' bounding box per axis; the box is logged (`BakeSet.PositionMin/Max`) |
| `id` | A flat colour per material slot (`id=body`: per high body), padded by copying so no third colour appears. The colours are logged (`BakeSet.IdColors`, `NormalBake.IdColour( k )`) |
| `thickness` | Mean distance through the sculpt behind the surface. White is a quarter of the diagonal or more. The straight path along −N is kept unquantised in `BakeSet.ThicknessUnits`, and `SkinMaterial.TransmissionMask( set, k )` turns it into a transmission mask (skin's red transmittance; `k` 1 = skin, 3–10 gel-like flesh) |

| Key | Meaning |
|---|---|
| `size=` | Texels a side (default 1024) |
| `cage=` | Explicit cage: rays start this far out along the averaged normal and stop `distance=` past the low surface (the cage distance again if unset). Without it, the search distance is measured off the two meshes |
| `ss=` | Supersampling, 2 for 2x2, 4 for 4x4. AO and thickness divide their rays between the samples |
| `green=dx` | DirectX green. The default `gl` is OpenGL-style (+green up the image), which is what s&box reads: its shader negates y and its bitangent is +dP/dv (read from `normal.hlsl` and sbox-public's `PolygonMesh`). Keep `flipv=0` (v = 0 at the top) for s&box |
| `padding=` | Edge bleed in texels (default 4) |
| `name=` | File prefix (default: the low body's name) |
| `cage=CageBody` | A cage MESH: a copy of LOW pushed out (same vertices and faces). Each ray starts on it and runs through the matching LOW point, `distance=` beyond. In C#: `BakeOptions.CageMesh`; `NormalBake.CageMismatch` says why a mesh can't be one |
| `HIGH,HIGH2` | Several high bodies, combined. `id=body` colours the ID map per high body |
| `bake * folder` | By name: every `X_low` from its `X_high` bodies (`X_high_2`, `X_high3` …) and `X_cage`, case-insensitive. Each pair is baked against only its own highs (an exploded bake, without moving anything) into one shared map set, so the lows' UVs must not overlap each other. Unpaired bodies are logged as warnings. In C#: `BakePairs.Match` → `BakePairs.ToGroups` → `NormalBake.BakeGroups` |
| `bits=16` | height, normal, bent normal, object normal and position as 16-bit PNGs (`BakeOptions.SixteenBit`, `BakedMap.Rgb16`, `PngWriter.WriteFile16`; `PngReader.Read16` reads 8- or 16-bit). Other maps stay 8-bit. Not yet confirmed in the s&box texture compiler, hence not the default |
| `shader=skin` | The vmat is `shaders/skin.shader` with `preset=Body\|Head\|Mouth\|Wet flesh`; with thickness baked it also writes `<name>_transmission.png` at translucency `k=` (default 1), and binds a baked bent normal |
| `vmat=1` | Also writes `<name>.vmat` for `complex.vfx`, binding the baked normal and AO (`BakeMaterial.VmatSource`). `color=` and `rough=` add those textures by asset path, `metal=` sets metalness, and texture paths are made relative to `assets=` (default: the script's folder). Height is written to disk but not bound, because complex reads it only under `F_PARALLAX_OCCLUSION` |

**In the editor**, a Sculpt feature's **Bake** button writes the normal map plus whichever of AO,
curvature, cavity, height and thickness (with its transmission mask) **Edit ▸ Settings ▸ Bake**
switches on, and optionally a `.vmat`: Complex, or s&box's skin shader with a Facepunch preset. In C#
that is `BakeMaterial.WriteSet( set, folder, name, flipV, assetsRoot, material )`. **Paint ▸ Bake Selected
to Active** bakes the other selected bodies onto the one selected last (a selected `…_cage` body is
its cage) and **Paint ▸ Bake by Name** runs the `bake *` pairing; both use the same Settings ▸ Bake
maps, size, material, cage distance and 16-bit switch.

**Skin shader.** `SkinMaterial.VmatSource( SkinMaterial.Body() | Head() | Mouth() | WetFlesh(),
color, normal, rough, ao, transmissiveMask, bentNormal )` writes `shaders/skin.shader` (subsurface
scattering and transmission, the only s&box shader with either) with the keys and values of
Facepunch's `citizen_human` vmats: body curvature 0.463 / blur 2 / lobe 0.008, head 0.279 / 1.834,
mouth 0.569 / 0.435 with a flat 0.552 roughness and a 0.263 second lobe, red AO bleed
`[0.40 0.149 0.129]`, transmission colour #BF6302. `WetFlesh()` is the body with the mouth's lobe.


## The CLI

`tools/effigy.sh` runs these on the kernel. It needs the .NET 8 SDK and nothing else.

| Command | What it does |
|---|---|
| `render doc.effigy out.png [view] [size] [nowire] [plain] [bones] [focus=Body]` | Draw it. `plain` is a clean silhouette. |
| `describe doc.effigy` | The model in words with a number on every line: size, whether it stands on the ground, each body's size and position, open edges, the rig, the history and what failed. |
| `measure doc.effigy <landmark> [second]` | What is at a place, or the distance between two. |
| `match doc.effigy <view> ref.png [diff.png]` | Score the silhouette against a reference drawing: overlap, proportions, and which heights are too wide or narrow. The diff is orange where only the model is and blue where only the drawing is. |
| `topology doc.effigy\|model.obj [Body] [poles=12]` | The `topology` report (see the script verbs); an OBJ is imported as one body named after the file. |
| `section doc.effigy\|model.obj Body\|all z=… \| along=Bone [at=…]` | The `section` report. |
| `flex doc.effigy Body\|all [bones=] [angles=90,140] [render=dir]` | The `flex` bend test; needs a document with bones. |
| `script build.txt [doc.effigy\|-] [outdir]` | Run a build script, starting from a document or empty. |
| `fix doc.effigy loose,doubles,holes,normals\|all [out.effigy]` | Clean up meshes. |
| `parts out.png` | A sheet of every kitbash part. |
| `new out.effigy` | An empty document. |
| `wardrobe outdir [recipe] [size] [nodrape]` | Every garment recipe on a Citizen-shaped body, rendered. |
| `shade model.obj outdir [size=] [views=] [cam= look= lens=] [style=studio] [bloom=] [tonemap=]` | Lit judging renders of an OBJ on ShadedRender, its vmats' textures on its UVs. `style=studio` is `ShadeScene.Portfolio()`. |

The same tools are in the editor as the `effigy` MCP toolset, for an agent driving the open
document: `effigy_snapshot`, `effigy_describe`, `effigy_measure`, `effigy_symmetry`,
`effigy_run`, `effigy_help`, `effigy_parts`, `effigy_match`, `effigy_fix`, `effigy_open`,
`effigy_save`.

## Animated takes

The kernel can play a Marionette clip on a real model and check it, with no editor running.
That needs the model as a **dump**: its skeleton, its triangles with skin weights, and
optionally props. The editor writes one (`effigy_model_dump <model.vmdl> <out.json>
[sequence] [times] [props]`), or `tools/effigy.sh fbxdump bones.json source.fbx out.json` builds
one from the model's source FBX on any dump that has the bones.

| Command | What it does |
|---|---|
| `take dump.json clip.riganim [substeps=4] [depth=0] [allow=a:b@depth,…] [noself] [nopairs] [noprops] [stills=N \| frames=a,b,c] [camera=bone \| eye=x,y,z look=x,y,z \| eyetrack=take.json] [fov=80] [out=dir]` | Play the clip, count crossings and gaps, render stills. Exits 3 when anything crosses. |
| `fbxdump bones.json source.fbx out.json` | A dump from an FBX, lined up with the engine's model space by the bones the two share. |
| `fbxskin dump.json source.fbx [out.json]` | Give a dump's meshes the FBX's skin weights. |

The pieces are there to call directly:

```csharp
var dump = ModelDump.ReadFile( "arms_dump.json" );
var clip = RigAnimFile.ReadFile( "switch_throw.riganim" );

var local = clip.SampleLocal( dump.Skeleton, 12.5f );         // any frame, fractional too
var world = RigAnimFile.ModelSpace( dump.Skeleton, local );
var skin = dump.Deform( dump.Meshes[0], world );

// The chain is the bone, its parent and its grandparent; null when it is not three deep.
var solved = TwoBoneIk.Apply( dump.Skeleton, world, dump.Skeleton.IndexOf( "hand_R" ),
	target: new Vec3( 20, -6, -10 ), pole: new Vec3( 0, -1, -1 ), softness: 1f );

var glove = TriangleSet.From( skin, dump.Meshes[0].Triangles );
var report = MeshClash.Self( glove );                           // or Between( a, b ), Nearest( a, b )

var result = new TakeAudit( dump, clip ).Run( new TakeAudit.Options { Substeps = 4, DepthTolerance = 0.4f } );
```

- `RigAnimFile` evaluates a track the way Marionette's playback does: held before the first
  key and after the last, eased by the key that starts each segment. It matches s&box's own
  `Transform.Lerp` to float precision.
- `MeshClash` counts proper crossings only. Surfaces resting flat on each other are contact,
  not a crossing. Depth is measured to the nearest surface, so it is the way out, not the way in.
- `TakeAudit` leaves out pairs that already cross at the bind pose, so a cuff modelled into its
  sleeve never fails a take. Pairs meant to overlap further as the arm moves get an
  `Allowance` (globbed part names, a depth) and are reported as set-in while within it.
- `EyeTrack` reads a take file's per-frame eye (rows of position/forward/up, or position and
  s&box pitch/yaw/roll) and gives the camera at any time, for stills from the moving eye.

## Traps

- **`ChoiceParam.Value` is read-only.** Set `.Index`.
- **An empty `BodySelectionParam` means every body**, not none.
- **Subdivide is exponential.** A box at level 6 is 24,576 faces. It is for organic shapes about
  to be sculpted, not for hard-surface parts.
- **A Remesh budget is in triangles, not faces.** A 500-face quad cage is 1000 triangles.
- **Check `report.HasErrors` after every `Rebuild()`.** A failed feature leaves the ones above
  it intact, so an unchecked error gives you a model that is quietly missing a part.
- **Fillet and chamfer refuse a radius that is too big** for the faces it would eat.
- **Material paths are source paths** (`materials/x.vmat`). The engine adds `_c` itself, so
  storing `.vmat_c` makes it look for `.vmat_c_c`, and the model renders red.
