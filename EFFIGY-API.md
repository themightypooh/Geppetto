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
