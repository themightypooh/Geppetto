# Effigy

The engine-free half of the modelling tool. Parametric primitives in, subdivision surfaces and an
OBJ out, with no reference to any engine type anywhere in it.

See `../MODELING-HANDOFF.md` for why the tool exists and `../MODELING-HANDOFF-GODOT.md` for the
engine question this folder is designed to keep open.

## Why it's engine-free

The same source is meant to compile under s&box, Godot's C#, or a bare console runner. That is not
tidiness — it is the thing that keeps the engine decision reversible while it is still undecided.
The kernel has its own `Vec3`/`Vec2` for exactly this reason; engine glue converts at the boundary,
which is a handful of lines paid once per engine.

The test project compiles the kernel **from source** rather than referencing a built library, so if
anything in here ever picks up a dependency, the build breaks and says so.

## Running the tests

```
cd Effigy.Tests
dotnet run -- out
```

248 checks, and it writes sample `.obj` files to `out/` — one per primitive plus a
2-level-subdivided version of each. Those are the fastest way to see whether something is actually
right: open them in Blender, or drop one into ModelDoc to find out what s&box makes of it.

Exit code is non-zero on failure, so it works as a pre-commit or CI check unchanged.

## What's here

| File | |
|---|---|
| `Vec.cs` | `Vec3`, `Vec2`. Deliberately not the engine's |
| `Xform.cs` | transforms, and the winding reversal a mirror needs |
| `PolyMesh.cs` | n-gon mesh, adjacency, validation, Euler characteristic |
| `Primitives.cs` | box, plane, cylinder, quad sphere, wedge, tube — all quad-dominant |
| `CatmullClark.cs` | subdivision, boundary rules, cost prediction |
| `ObjWriter.cs` | OBJ export with angle-thresholded normals, plus a reader for round-trip tests |
| `Features/Feature.cs` | feature base, self-describing parameters, bodies |
| `Features/PartStudio.cs` | the ordered history: rollback and incremental rebuild |
| `Features/BasicFeatures.cs` | primitive, transform, linear/circular pattern, mirror, subdivide |
| `Features/SketchFeatures.cs` | sketch, extrude, revolve |
| `Sketch/SketchPlane.cs` | the plane a sketch lives on, and plane↔world mapping |
| `Sketch/Sketch.cs` | points, lines, arcs, circles, tessellation |
| `Sketch/Profile.cs` | closed-region finding, nesting, orientation |

## The feature tree

Modelled on Onshape's Part Studio, because that structure is what makes a parametric modeller
parametric rather than a stack of bakes.

```csharp
var studio = new PartStudio();

var box = studio.Add( new PrimitiveFeature() );
box.SizeX.Value = 4f;

var mirror = studio.Add( new MirrorFeature() );
mirror.PlaneNormal.Value = new Vec3( 1, 0, 0 );

studio.Add( new SubdivideFeature() ).Levels.Value = 2;

studio.Rebuild();
```

Two properties do the work, and both are tested:

**Rollback.** `RollbackIndex` evaluates only the first N features, so you can go back and see the
model as it was. Rolling back above the Subdivide feature is how you get at the low-poly cage — the
same cage the sculpt stage eventually bakes down onto. That is why subdivision is a feature in the
tree rather than an export step.

**Incremental rebuild.** The body list after each feature is cached, so editing feature 7 of 20
re-runs 7 onward and reuses the snapshot from 6. Without it every parameter drag re-runs the whole
tree and the tool stops feeling live at about a dozen features.

Parameters describe themselves (`FloatParam`, `IntParam`, `ChoiceParam`, …) so one generic panel can
render any feature's dialog. That is copied from Onshape deliberately: every dialog there has the
same shape, and `PrimitiveFeature.Parameters` changes with the shape dropdown the way Onshape's
does — a box asks for three lengths and doesn't mention radius.

A feature that throws records an error and the rebuild carries on, so one upstream mistake doesn't
cascade into every later feature also failing.

## The sketcher

Onshape's core loop — sketch on a plane, then extrude or revolve it.

```csharp
var sketch = studio.Add( new SketchFeature() );
sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 4, 2 ) );

studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
```

**Curves reference shared point indices**, so two lines meeting at a corner point at the same
index. Coincidence is identity rather than a constraint that can drift, dragging a corner moves
both lines with no bookkeeping, and finding closed regions is an integer graph walk instead of
floating-point position matching.

**Profiles are found, not declared.** `ProfileFinder` walks the curve graph for cycles, works out
which loops nest inside which, and orients every outer loop counter-clockwise — which is what makes
extrude's winding questions answer themselves. Construction geometry is excluded. Circles close on
their own.

Lines and arcs stitch into one loop, so rounded profiles work. Arc tessellation derives its segment
count from the allowed sagitta, so small arcs aren't over-sampled and big ones aren't visibly
faceted.

**Caps are single n-gons, not triangle fans** — Catmull-Clark turns an n-gon into n clean quads, so
a sketched profile subdivides properly.

### Two known limits, both deliberate

**Branching sketches.** Only points where exactly two curves meet are followed. A line drawn across
a rectangle is ambiguous without full planar face traversal, so it's reported as a warning rather
than guessed at. That still covers rectangles, polygons, circles, slots and rounded profiles.
Proper face traversal — sort half-edges by angle at each vertex, always take the next one clockwise
— is the upgrade, and doesn't change `ProfileFinder`'s interface.

**Profiles with holes.** Detected and reported, not built. Capping around a hole is the same problem
as a boolean subtract and is better solved once, there. Until then use the Tube primitive.

### Not yet: constraints

There's no solver, so sketch coordinates are typed rather than derived. This was shipped first on
purpose — the sketch→extrude loop works end to end while the solver is built, and nothing in
`Sketch.cs` or `Profile.cs` has to change when it lands. The solver's job is to let coordinates be
implied by constraints; the geometry and topology layers below it are already done.

## Two decisions worth knowing before changing anything

**Quads are a requirement, not a preference.** Catmull-Clark turns clean quads into a clean surface
and triangle soup into a lumpy one, and triangles leave valence-3 vertices that stay extraordinary
at every level and pucker under a sculpt brush. That is why there is no UV sphere here — its pole
fans are triangles. `QuadSphere` costs nothing extra and has no poles.

**UVs are stored per face corner, not per vertex.** A box corner belongs to three faces that each
want a different UV for the same position; per-vertex UVs would force one value and smear the
texture across every seam. It also makes UV subdivision purely local — a face's new UVs are computed
from that face's own corners and nothing else — so seams survive subdivision for free. There is a
test for this.

## What's verified

Not "it renders, looks fine". Subdivision is the classic looks-right-is-wrong case: a vertex rule
that is subtly off still produces a smooth plausible blob, it just converges to the wrong surface.
So the tests check things that fail loudly instead:

- Euler characteristic, before and after subdivision, including the tube at genus 1
- the exact growth laws — `V' = V+E+F`, `F' = total corners`, `E' = 2E + corners`
- winding, via the divergence theorem: enclosed volume must come out positive, and a 2×2×2 box must
  come out at exactly 8
- successive subdivision levels must move points *less* each time, not drift
- an open mesh keeps its boundary, stays planar, and keeps its corners
- a box exports with exactly 6 hard normals; a 16-segment cylinder with at least 16

On the tree side: that editing feature 4 of 6 reuses exactly 3 and re-runs exactly 3, that a clean
rebuild does no work, that rollback and roll-forward round-trip, that a broken feature doesn't stop
the ones after it, and that **a mirrored body's enclosed volume stays positive** — the winding-
reversal check, which guards a bug that renders black and looks fine in wireframe.

On the sketch side, solids are checked against known volumes rather than eyeballed: an extruded
2×3 rectangle must enclose exactly 24, a revolved square must match **Pappus' theorem**, and a
quarter revolution must be exactly a quarter of the full one. Plane coordinates round-trip through
world space on all three planes. Every solid asserts positive enclosed volume, because an
inside-out sweep looks completely normal in wireframe.

Three of these tests exist because they caught real bugs:

- **Pattern merge** appended into the source mesh while the loop kept re-reading it, so instance
  counts doubled instead of incrementing — 6, 12, 24, 48 faces rather than 6, 12, 18, 24.
- **Revolve winding** came out inverted. Rather than enumerate the cases — axis direction, sign of
  the angle, which side the profile sits on — the fix measures the finished volume and flips if it
  is negative. One cheap pass, correct for all of them.
- **A profile straddling the axis** produced a mesh where every face existed twice with opposite
  winding: zero enclosed volume, vertices welded that should have stayed apart, and entirely
  plausible until measured. Now refused with the same reasoning Onshape gives.

## Not here yet

The sketch constraint solver, fillet and chamfer, shell, boolean subtract,
planar UV projection. Then the whole phase-two sculpt side — brushes, multires deltas, normal-map
bake. See the open questions in the handoff docs; two of them gate how this connects to an actual
editor.
