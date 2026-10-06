# Building models with Effigy from code

Read this before writing a generator. It is the whole API surface you need; you should not have to
go spelunking for it. Working examples in the repo: `Effigy.Tests/TreeGen.cs`,
`TentacleGen.cs`, `PaintGen.cs` — all run headlessly from
`Effigy.Tests.exe --<name> [outDir]`.

## The shape of every generator

```csharp
var studio = new PartStudio();

var box = studio.Add( new PrimitiveFeature() );
box.Name = "Field slab";              // ALWAYS name it
box.Shape.Index = 0;                  // Box
box.SizeX.Value = 4000f;
box.SizeY.Value = 3000f;
box.SizeZ.Value = 40f;
box.Position.Value = new Vec3( 0, 0, 0 );
box.Material.Value = 0;               // material slot

var report = studio.Rebuild();
if ( report.HasErrors ) throw new Exception( report.ToString() );

var mesh = studio.ToMesh();           // everything;  ToVisibleMesh() honours hidden bodies
```

**`studio.Add<T>()` appends a feature and returns it.** Order is the history: features run top to
bottom, each acting on what the ones above produced. Nothing is evaluated until `Rebuild()`.

## Parameters — the kinds

Every feature exposes typed params. They all carry a value plus editor metadata:

| Kind | Set it with | Note |
|---|---|---|
| `FloatParam` | `.Value = 1.5f` | `.Clamped` respects min/max |
| `IntParam` | `.Value = 3` | |
| `BoolParam` | `.Value = true` | |
| `ChoiceParam` | `.Index = 2` | **`.Value` is READ-ONLY** — it is `Options[Index]` |
| `Vec3Param` / `Vec2Param` | `.Value = new Vec3( x, y, z )` | |
| `StringParam` | `.Value = @"meshes/part.obj"` | paths, free text; Import is the feature that asked for it |
| `BodySelectionParam` | `.BodyIds.Add( id )` | **empty means every body** |

`ChoiceParam.Index` is the one that catches people. `Shape.Index = 0` not `Shape.Value = "Box"`.

## Body ids

A feature's bodies are `feature.Id + "b0"`, `"b1"`, … in creation order. That is how you point a
later feature at an earlier one:

```csharp
var xf = studio.Add( new TransformFeature() );
xf.Bodies.BodyIds.Add( box.Id + "b0" );
```

Ids are stable across rebuilds, which is why names and material scales can be keyed on them.

## The features

**`PrimitiveFeature`** — `Shape.Index`: `0` Box, `1` Cylinder, `2` Quadsphere, `3` Wedge, `4` Tube.
`SizeX/Y/Z`, `Position`, `Material`. The fastest way to get a solid.

**`ImportFeature`** — a Wavefront OBJ as a body. `Source.Value` is the path; `BindSource(path)`
also takes the file's bytes so a later rebuild does not depend on the path still existing.
A rebuild parses `Source` again only when its size or write time has changed, so deleting a piece
or undoing costs what is left of the import, not the whole file.
The mesh never goes into the `.effigy` text — `ImportSidecar.Save` / `Load` write it beside the
document, the same way `SculptSidecar` does for sculpt deltas. FBX/GLB are a refusal, not a parse.

```csharp
var import = studio.Add( new ImportFeature() );
import.Name = "Host";
import.BindSource( Path.Combine( sourceDir, "host_target.obj" ) );
```

**`SketchFeature`** — carries a `Sketch` you fill in directly. `Plane.Index`: `0` Top (XY),
`1` Front (XZ), `2` Right (YZ); `PlaneOffset` shifts it. `Face` draws on a face of an existing
body; `PlaneFeatureId` draws on a `PlaneFeature`. Those three are the same question answered three
ways, and the most specific set wins: plane, then face, then `Plane.Index`.

```csharp
var sk = studio.Add( new SketchFeature() );
sk.Name = "Terrace profile";
sk.Plane.Index = 1;                                   // Front (XZ)
var a = sk.Sketch.AddPoint( 0f, 0f );
var b = sk.Sketch.AddPoint( 100f, 0f );
sk.Sketch.Add( new SketchLine { A = a, B = b } );     // lines, arcs, circles, splines
```

Closed regions are **found**, not declared — draw a closed loop and the profile finder picks it up.

**`ExtrudeFeature`** (and Revolve/Sweep/Loft) — consumes a sketch, or faces of an existing solid.
`SketchFeatureId = sk.Id` names the sketch. `Distance`, `Symmetric`, `Flip`, `SecondDistance`,
`Termination.Index` (blind / up-to-next / through-all), and a `Result` choice for
New body / Add / Remove. It can also take `Faces` (a `List<FaceRef>`) to pull a face of a part with
no sketch at all.

**`PlaneFeature`** — a plane to build on that is not one of the three global ones. `Base.Index`
(same order as `SketchFeature.Plane`), or `Face`, or `BasePlaneId` naming an earlier `PlaneFeature`;
then `Offset` along the normal and `Angle` (±90°) hinged on `Hinge.Index` — `0` the plane's own X
axis, `1` its Y. It produces no geometry, so name it and point a sketch at it:

```csharp
var top = studio.Add( new PlaneFeature() );
top.Name = "Roof line";
top.Face = FacePlane.Capture( body, faceIndex, point );   // rides that face when the part changes
top.Offset.Value = 12f;
top.Angle.Value = 30f;

var sk = studio.Add( new SketchFeature() );
sk.PlaneFeatureId = top.Id;
```

A plane built from a face carries that body along, so a sketch on it extrudes into the same part
under `Result` = Auto rather than starting a new one.

**`TransformFeature`** — `Bodies`, `RotationAxis`, `RotationAngle` (degrees), `Translate`. Rotation
happens first, then the translation. This is how you place a primitive that has no orientation of
its own.

**`MirrorFeature`** — `Bodies`, `PlanePoint`, `PlaneNormal`, `KeepOriginal`, `Merge`. The cheapest
way to get symmetry; use it instead of building the same thing twice.

**`LinearPatternFeature`** — `Bodies`, `Direction`, `Spacing`, `Count` (up to 4096), `Merge`.
**`CircularPatternFeature`** — the same idea about an axis.

**`BooleanFeature`** — `Operation.Index`: `0` Union, `1` Subtract, `2` Intersect. `Targets`,
`Tools`, `KeepTools`. The tool body is consumed unless you keep it.

**`ShellFeature`** — `Bodies`, `Thickness`. **`FilletFeature` / `ChamferFeature`** — a radius, and
optionally picked edges; leave the edge list empty for every sharp edge. **`DraftFeature`**,
**`HoleFeature`**, **`MoveFaceFeature`**, **`SubdivideFeature`**, **`UVProjectFeature`**,
**`FaceMaterialFeature`**, **`SculptFeature`**, **`PaintFeature`**.

**`RemeshFeature`** — `Bodies`, `Target` (`Percentage` | `Triangle count`), `Percent`, `Triangles`,
`HoldBorders`, `Weld`. Subdivide's opposite: takes a dense import down to a triangle budget and
keeps its silhouette, open borders and material seams. Returns triangles, so it is for imports
rather than a quad cage you built with sketches.

## Naming — do it as you go

```csharp
feature.Name = "Front rail";                    // every feature, including sketches
studio.BodyNames[box.Id + "b0"] = "Plate";      // every body that survives
studio.MaterialNames[1] = "materials/dev/gray_50.vmat";   // slot -> vmat, SOURCE path
```

A tree of `Box 1` / `Extrude 3` / `Mirror 2` is a lump with a history attached. Name things for
what they ARE, not for the tool that made them, and do it at the point you add them.

## Exporting

```csharp
ObjWriter.WriteFile( mesh, Path.Combine( outDir, "part.obj" ), "part" );
StudioDocument.Save( studio, Path.Combine( outDir, "part.effigy" ) );
```

For a `.vmdl`, copy the block in `TreeGen` — it writes the KV3 with
`VmdlMaterials.GroupList( studio, mesh )` for the material remaps and `VmdlPhysics` for the hull.
**Do not hand-roll the MaterialGroupList**: every material slot the mesh uses must name a real
asset or the compiled model renders in the bright red missing-material shader.

## Traps that have actually bitten

- **`ChoiceParam.Value` is read-only.** Set `.Index`.
- **An empty `BodySelectionParam` means EVERY body**, not none. Subdivide once read that as
  "everything" and quadrupled the whole document.
- **Subdivide is exponential.** A box at level 6 is 24,576 faces. Architectural models want none of
  it — it is for organic shapes you are about to sculpt.
- **A Remesh target is in TRIANGLES, not faces** — a 500-face quad cage is 1000 triangles, and
  typing 500 into the budget gets you half of what you meant. `Decimate.TriangleCount`, not
  `mesh.FaceCount`.
- **Check `report.HasErrors` after every `Rebuild()`** and fail loudly. A feature that failed leaves
  the ones above it intact, so a silent error gives you a model that is quietly missing a part.
- **Fillet and chamfer refuse oversized radii**, correctly — on a 2-unit cube anything above ~1.0
  has eaten more of a face than the face had.
- **Material paths are stored as the SOURCE path** (`materials/x.vmat`). The engine appends `_c`
  itself, so storing the compiled `.vmat_c` makes it look for `.vmat_c_c` and it renders red.

## Checking your work

`sh tools/test.sh` runs the suite and also writes sample OBJ/DMX/VMDL files and SVG previews into
`Effigy.Tests/out/`. `PngPreview` renders a mesh to a PNG so you can look at what you built without
opening the editor — `PaintGen` uses it. Use it; a model you have not looked at is a guess.

## Modelling as an agent: see, measure, script

Everything above builds a model by hand in C#. The faster loop is the one a person has —
look, adjust, look — and the kernel now has the eyes and the ruler for it, headless
(`tools/effigy.sh`) and in the editor (the `effigy` MCP toolset: `effigy_snapshot`,
`effigy_describe`, `effigy_measure`, `effigy_run`, `effigy_match`, `effigy_fix`, `effigy_help`,
`effigy_parts`, `effigy_open`, `effigy_save`).

**See.** `tools/effigy.sh render doc.effigy out.png` writes a sheet of four orthographic views —
front, side, top, iso — fitted to the model, with a ground grid whose step is printed, the axes
(x red forward, y green left, z blue up), the wire, and a label per body. One view:
`render doc.effigy out.png front 600`. `plain` drops the grid, wire and labels for a clean
silhouette. Read the PNG; it is a drawing you can hold a ruler to.

**Measure.** `describe` says the model in words with a number on every line: overall size, whether
it stands on the ground, each body's size and centre and where it sits (upper left front…),
open edges, the rig, the history and what failed. `measure doc.effigy Torso.top` says what is at
a place; `measure doc.effigy Hand_L Hand_R` the distance between two. A place is a **landmark**:
a body name (its centre), `Body.top/.bottom/.front/.back/.left/.right/.min/.max`, a bone name
(its head; `.tail`, `.mid`), `ground`, `origin`, a literal `x,y,z`, or `@landmark+dx,dy,dz`.

**Match.** `match doc.effigy front drawing.png diff.png` scores the model's silhouette against a
reference drawing from the same view — dark ink on paper, a cut-out on transparency, or a light
model on a dark ground — as an overlap percentage, whether the proportions agree, and which
bands of height are wider or narrower than the drawing. The diff PNG is orange where only the
model is and blue where only the drawing is.

**Script.** A build script is the model as a list of moves, one per line, that runs headlessly
(`script build.txt`) or on the open studio (`effigy_run`), with landmarks resolved against the
model as built so far:

    add Profile name=Torso front=0,5;8,9;22,10;30,8;34,5 side=0,4;8,6;22,6;34,4 position=0,0,20
    add Primitive name=Head shape=Box sizex=9 sizey=10 sizez=9 position=@Torso.top+0,0,6
    add Part name=Cog part=Cog size=4 thickness=1.5 count=10 position=@Torso.front+1,0,0 rotationaxis=0,1,0 rotation=90
    add Spline name=Cable points=@Torso.back;-10,4,40;@Head.back radius=0.6
    add Primitive name=Tail shape=Box sizex=20 sizey=2 sizez=2 position=0,0,30
    add Spline name=TailPath tube=0 points=@Torso.back;-12,0,24;-24,0,4
    add CurveDeform bodies=Tail
    set Torso sizez=32
    fix Torso all
    describe
    render out/robot.png
    save out/robot.effigy

`add TYPE key=value…` takes any feature type (`effigy_help` lists them; `effigy_help Spline` its
parameters) with keys by label or field name — a `ChoiceParam` by option, a body list by body
names, a point list as `a;b;c`. A feature that fails stops the script with its own cause and
remedies, which is what to change. `fix BODY|all loose doubles holes normals` cleans by name.

**Parts and profiles.** `PartFeature` (`add Part part=Cog|Bolt|Hex bolt|Rivet|Knob|Hinge|Panel|Pipe
elbow|Strap|Buckle`) is the kitbash shelf — `effigy_parts` says what Size, Length, Thickness,
Count and Angle mean for each; every part stands on z = 0 at the origin and takes Position and
Rotation. `ProfileFeature` builds a body from a front outline and a side outline — `height,half-
width;…` — which is how a limb or a torso is actually described, and eight numbers is a torso.
