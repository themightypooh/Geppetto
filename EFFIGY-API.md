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
| `ImportFeature` | A Wavefront OBJ as bodies. `BindSource( path )` reads it and keeps the bytes, so a rebuild doesn't need the file to still exist. FBX and GLB are refused. |
| `PartFeature` | A kitbash part: `Part.Index` over Cog, Bolt, Hex bolt, Rivet, Knob, Hinge, Panel, Pipe elbow, Strap, Buckle. `Size`, `Length`, `Thickness`, `Count`, `Angle` mean different things per part (table below). `Position`, `RotationAxis`, `RotationAngle`. |
| `ProfileFeature` | A body from outlines (see below). |
| `SplineFeature` | A 3D path through `Points` (a `List<Vec3>` field). `Tube` makes it a solid pipe with `Radius`, `Taper`, `Sides`, `Twist`. `Closed`, `Smooth`, `Resolution`. |

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
| `BooleanFeature` | `Operation.Index` (`0` Union, `1` Subtract, `2` Intersect), `Targets`, `Tools`, `KeepTools`. Tool bodies are consumed unless kept. |
| `ShellFeature` | `Bodies`, `Thickness`. |
| `FilletFeature` | `Radius`, `Segments`, `AngleThreshold`, `Edges`. Leave `Edges` empty for every sharp edge. |
| `ChamferFeature` | `Width`, `AngleThreshold`, `Edges`. |
| `HoleFeature` | `Faces`, `Style` (Simple, Counterbore, Countersink), `Diameter`, `Depth` (0 = through), `HeadDiameter`, `HeadDepth`, `SinkAngle`. |
| `MoveFaceFeature` | `Faces`, `Mode` (Offset, Translate), `Distance`, `Direction`. |
| `DeleteFaceFeature` | `Faces`. Deletes them and heals the surface. |
| `DraftFeature` | `Faces`, `Pull`, `Angle`, `Neutral`. |
| `CurveDeformFeature` | Bends `Bodies` along a spline: `SplineId` (empty means the last one), `Axis`, `Stretch`. |
| `SubdivideFeature` | `Bodies`, `Levels` (0–6), `AllFaces`. |
| `RemeshFeature` | `Bodies`, `Target` (Percentage, Triangle count), `Percent`, `Triangles`, `HoldBorders`, `Weld`. Takes a dense import down to a triangle budget. |
| `SculptFeature` | `Bodies`, `Reproject`. Sculpt deltas live in a sidecar beside the `.effigy`. |
| `MeshEditFeature` | A direct, by-hand edit of a body, with live mirror, array, subdivide and thickness modifiers. Written by the editor's Model workspace. |

### Surfaces and materials

| Feature | Parameters |
|---|---|
| `FaceMaterialFeature` | `Faces`, `Material` (a slot number). |
| `UVProjectFeature` | `Mode` (Box, Planar, Unwrap), `Direction`, `Scale` (units per tile), `ChartAngle`, `Margin`. |
| `PaintFeature` | `Bodies`, `Resolution`, `Blend` (Tint, Replace), `Strokes`. Strokes are stored in the document and replayed onto the texture. |

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
| `set` | `set Feature key=value…` |
| `remove` | `remove Feature` |
| `rebuild` | `rebuild` |
| `fix` | `fix Body\|all loose doubles holes normals` |
| `render` | `render path [view=sheet] [size=512] [wire=1] [bones=0] [labels=1] [grid=1] [focus=Body]` |
| `describe` | `describe` |
| `measure` / `at` | `measure landmark` |
| `distance` | `distance A B` |
| `symmetry` | `symmetry [Body]` |
| `match` | `match view ref.png [diff.png]` |
| `material` | `material slot path.vmat` |
| `pivot` | `pivot Body @landmark` |
| `export` | `export folder [split=1] [assets=root] [name=x]` (writes `.obj` and `.vmdl`) |
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

## The CLI

`tools/effigy.sh` runs these on the kernel. It needs the .NET 8 SDK and nothing else.

| Command | What it does |
|---|---|
| `render doc.effigy out.png [view] [size] [nowire] [plain] [bones] [focus=Body]` | Draw it. `plain` is a clean silhouette. |
| `describe doc.effigy` | The model in words with a number on every line: size, whether it stands on the ground, each body's size and position, open edges, the rig, the history and what failed. |
| `measure doc.effigy <landmark> [second]` | What is at a place, or the distance between two. |
| `match doc.effigy <view> ref.png [diff.png]` | Score the silhouette against a reference drawing: overlap, proportions, and which heights are too wide or narrow. The diff is orange where only the model is and blue where only the drawing is. |
| `script build.txt [doc.effigy\|-] [outdir]` | Run a build script, starting from a document or empty. |
| `fix doc.effigy loose,doubles,holes,normals\|all [out.effigy]` | Clean up meshes. |
| `parts out.png` | A sheet of every kitbash part. |
| `new out.effigy` | An empty document. |
| `wardrobe outdir [recipe] [size] [nodrape]` | Every garment recipe on a Citizen-shaped body, rendered. |

The same tools are in the editor as the `effigy` MCP toolset, for an agent driving the open
document: `effigy_snapshot`, `effigy_describe`, `effigy_measure`, `effigy_symmetry`,
`effigy_run`, `effigy_help`, `effigy_parts`, `effigy_match`, `effigy_fix`, `effigy_open`,
`effigy_save`.

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
