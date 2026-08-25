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

**On a machine with no .NET** — a cloud dev container, say — the SDK is one command away and is
worth installing before doing anything else:

```
apt-get update -qq && apt-get install -y dotnet-sdk-8.0
```

That matters more than it looks. The kernel is engine-free by design, so the whole of it and every
editor workflow that is "kernel calls in a particular order" can be verified without s&box on the
machine. A session that skips this ends up reasoning about the code by reading it, and reading it
is how a bug that made every parameter edit a silent no-op survived long enough to look like three
unrelated UI faults.

1066 checks, and it writes sample `.obj` files to `out/` — one per primitive plus a
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
| `Features/SolidFeatures.cs` | shell, bevel, UV project, face material — the ops that act on a solid once it exists |
| `Sketch/SketchPlane.cs` | the plane a sketch lives on, and plane↔world mapping |
| `Sketch/Sketch.cs` | points, lines, arcs, circles, tessellation |
| `Sketch/Profile.cs` | closed-region finding, nesting, orientation |
| `ShellOperation.cs` | hollow a solid to an exact wall thickness, with optional openings |
| `Bevel.cs` | flat chamfer by angle threshold: corner cutting, edge bridging, vertex caps |
| `PlaneOffset.cs` | the offset solve shell uses |
| `UVProjection.cs` | box and planar UV projection |
| `Rig/Skeleton.cs` | bones, bind pose, world transforms from the parent chain |
| `Rig/SkinWeights.cs` | per-vertex influences, blending and pruning |
| `Rig/SkinBinder.cs` | auto-binding by distance or by body, plus weight smoothing |
| `SmdWriter.cs` | the export path — static and skinned in one writer |
| `DmxWriter.cs` | the export ModelDoc actually accepts; SMD is not on its import list |
| `MeshNormals.cs` | angle-thresholded corner normals, shared by every exporter |
| `Triangulate.cs` | ear clipping, so a concave face does not fill its own notch |
| `MeshSection.cs` | where one solid crosses a plane — the footprints another body leaves on a face |
| `MeshRaycast.cs` | picking a face or a body in the viewport, as pure geometry |
| `Sketch/FacePlane.cs` | referring to a face that gets rebuilt, and riding it when it moves |
| `Sketch/IConstraint.cs` | one residual and its derivative — the solver never switches on a kind |
| `Sketch/Constraints.cs` | the constraint set, and the derivatives that make it solvable |
| `Sketch/SketchSolver.cs` | Levenberg-Marquardt, with degrees of freedom and redundancy reported |
| `MeshBoolean.cs` | what a boolean is, and where a host installs one that can actually do it |
| `LoopOffset.cs` | move a 2D loop in or out from its edges — what a draft angle is built on |
| `Features/StudioDocument.cs` | saving and loading the tree — the file the whole history depends on |

## Saving

`StudioDocument` reads and writes a `.effigy` file: hand-written text, like every other format in
here, for the reason the kernel has no dependencies at all. It also diffs, which matters for a format
holding someone's model — a corrupt binary is a shrug, a corrupt text file is usually one bad line
you can see.

**Fields are found by reflection rather than listed.** A feature's `Parameters` property is not
usable as the list to save: `PrimitiveFeature` changes its parameters with the shape dropdown, so a
box saved today would not know what to do with the radius it wants tomorrow. Public fields are
stable, so a new feature saves the moment it is written and there is no step to forget.

The risk that carries is a field of a type the writer has no case for, so `DocumentTests` sets every
field of every feature type in the assembly to a non-default value and round-trips it. Adding state
a save cannot carry fails the suite rather than quietly not saving — it caught `ShellFeature.OpenFaces`
on the first run.

Two things the format has to get right and is tested on:

**Ids are preserved exactly.** Body ids derive from the feature that made them and a `FaceRef` holds
a body id, so a load that reissued feature ids would break every sketch drawn on a face the moment
the file was reopened. That is the worst thing this format could do and it is asserted against.

**Unknown fields are skipped, not fatal.** A file written by a version with an extra parameter still
opens, minus that parameter. A file from a newer format version is refused by name.

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

### Two limits that used to be here, and are not

Both of the "deliberate limits" this section used to describe have been built, and both are worth
keeping a note of because in each case the stated reason for the limit outlived the limit itself.

**Branching sketches.** Built. Only points where exactly two curves met were followed, so a line
drawn across a rectangle — which is how anyone divides a shape — was reported as "not supported yet"
instead of split into the two regions it plainly is.

The fix is exactly the upgrade path this section used to describe: every curve becomes two directed
half-edges, the outgoing ones at each point are sorted by the direction they actually leave in, and
walking a face means arriving along one, turning onto its reverse, and leaving along whichever
half-edge sits immediately clockwise. Each half-edge belongs to exactly one face, so the walk always
terminates and always covers everything. Faces that come out counter-clockwise are regions; each
connected piece of the sketch also produces one clockwise face — the infinite one around it — and
dropping those is the whole of the special-case handling.

Two things it depends on. The angle has to be the **tangent**, not the bearing of the far endpoint,
or an arc and a line leaving the same point sort the wrong way round and the walk takes the wrong
curve. And dangling curves are pruned first, **repeatedly**, because removing one can leave the next
one dangling — they are reported rather than silently dropped, since building the good regions while
quietly discarding someone's geometry is the failure that looks like success.

Worth knowing when a junction seems not to work: **touching is not joining**. A line whose end sits
geometrically on an arc but shares no point index with it is not connected, and its free end gets
pruned. That is coincidence-as-identity working as designed, and it caught out the first version of
the test for this.

**Profiles with holes.** Built. This section used to say they were "the same problem as a boolean
subtract and better solved once, there", which was wrong and cost the feature a long time: capping
around a hole is a 2D TRIANGULATION problem and never needed CSG at all.

Each hole is spliced into the outer loop along a bridge — a segment out to the hole and back, which
turns a ring-with-a-hole into one boundary — and then it is an ordinary ear clip. The doubled bridge
edge needs no special handling because `IsEar` already refuses a zero-area corner. Hole walls come
for free: `ProfileFinder` hands holes back wound the opposite way, so the same wall code faces them
into the hole with no sign handling anywhere.

The cost is the cap. A face with a hole in it is not a polygon, so it cannot be the single n-gon this
kernel prefers, and a holed cap is triangulated instead — which subdivides worse, exactly as the
quads argument below predicts. That is a real tradeoff and the honest one: a plate with bolt holes is
hard surface that rarely gets subdivided, and the alternative was no feature. **Profiles without
holes are untouched and still get their single n-gon**, which is pinned by a test.

**Revolve handles them too**, and for less than it looked. Every loop sweeps rather than just the
outer one, and because holes arrive wound the opposite way their quads face into the hole with no
sign handling anywhere — the same free ride the extrude walls get. A partial revolution's two end
caps are the profile, so they borrow the holed cap above; a full revolution has no caps at all and
pays nothing for its holes beyond the extra sweep. A hole straddling the axis is refused exactly as
an outer loop straddling it is, and for the same reason.

Worth stealing as a test technique: a revolve is faceted, so its volume runs about 1.1% under the
true solid whether or not it has a hole, and checking a holed revolve against Pappus directly needs
a tolerance loose enough to hide a real error. Checking it against the UNHOLED sweep of the same
profile cancels the faceting exactly — both are approximated identically — and what is left measures
only the hole. The two ratios matched to five decimal places.

### One part, built out of several extrudes

Extruding off a face of an existing body **adds to that body** rather than starting a second one, so
three bosses on a block are one part in the list and not four. The rule is the sketch's attachment,
decided when the sketch was placed: on a face of something, it builds that thing up; on a global
plane, it starts a new part. `Result` on Extrude and Revolve overrides it either way.

**This is not a boolean.** The two meshes are combined and nothing cuts the interface between them,
so the face a boss stands on is still in there on the inside. For what it is for — the part list
reading correctly, the render and every exporter being right — that is the correct trade, and it
does not wait on a robust CSG. What it costs is that the merged mesh is non-manifold along the join,
so operations needing clean topology (shell especially) will refuse it. That refusal is the honest
failure, and it is why merging is not forced on features that did not ask for it.

**Remove is the fourth option and it is a real boolean**, because a cut cannot be faked the way Add
can — taking material away means genuinely recomputing the surface. It goes through `MeshBoolean`,
which is an interface and a provider slot rather than an implementation: the kernel knows what a
boolean IS without knowing how to do one, which keeps the engine-free promise intact. With no
provider installed, Remove fails with a message saying so instead of producing something plausible.

Auto never removes. Adding and removing look identical from the geometry — the same profile pulled
the same distance off the same face — so there is nothing for Auto to read, and a rule that guessed
would eventually guess a hole into someone's part.

### Materials per face

**Slots can be named.** `PartStudio.MaterialNames` maps a slot to whatever a person called it, and
`NameForSlot` hands that to any of the three exporters — falling back to `material_0`, `material_1`
for anything unnamed. A number is all the geometry needs and it is not what someone binding the model
in ModelDoc wants to look at. Names live on the studio rather than the mesh because a slot means the
same thing across every body in the document: slot 2 is "rubber" everywhere or it is nothing.


Every face carries a material slot and every exporter groups by it, so a model can arrive in ModelDoc
with several slots to bind. `FaceMaterialFeature` is what sets them: pick faces, give them a slot.

It is a feature rather than an edit because bodies are rebuilt from scratch every rebuild — paint the
mesh directly and the next parameter drag wipes it. In the tree it is re-applied after the geometry it
paints is remade, and it rolls back and suppresses like anything else. Faces are held as `FaceRef`s
and resolved through the same `FacePlane.TryResolveFace` a sketch-on-a-face uses, so the two cannot
disagree about which face is meant.

### Constraints

There is a solver now, and the prediction the last version of this section made held: nothing in
`Sketch.cs` or `Profile.cs` had to change for it. Coordinates are still what everything downstream
reads — `SketchSolver` moves the points to satisfy the rules, and profile finding, extrude and
revolve never learn that a solver exists.

Levenberg-Marquardt over the constraint residuals: each rule contributes an equation that reads zero
when it holds, plus its derivative, and the solve is the point positions that zero them all.
Coincident, distance, horizontal, vertical, equal length, parallel, perpendicular, angle,
point-on-line, symmetric and radius — with a new one costing one class and no change to the solver.

Angle is worth a note: its residual is `cross·cos θ − dot·sin θ` rather than an angle difference,
because an angle computed with `atan2` carries a branch cut, and a residual that jumps by 2π
somewhere in its domain sends the solver the wrong way the moment a line crosses it. Parallel and
perpendicular are the same rule at 0 and 90, kept as their own types because that is what a user
asks for.

**Arcs carry an implicit constraint.** An arc reads its radius off the centre-to-*start* distance,
and tessellation snaps its last sample onto the end point wherever that is — so an end that drifts
off the circle produces an arc at the wrong radius with a kink in its final segment, which looks like
a rendering glitch. Nothing enforced it while coordinates were only typed. A solver moves points for
a living, so every arc now contributes "both my endpoints are equidistant from my centre" whether the
user asked or not. It is not design intent, it is what an arc *is*.

Three things worth knowing before touching it:

**One point is pinned.** Every equation is about differences between points, so the whole sketch can
slide without changing any residual and the step is not unique. Pinning kills the slide but not
rotation, which is why a fully dimensioned rectangle still reports one degree of freedom — it really
can be spun. The editor should pin whatever point the user is dragging.

**Degrees of freedom are counted from the Jacobian's rank, not from the number of constraints.** Two
constraints saying the same thing remove one freedom; counting rows would claim they removed two,
and would call a perfectly fine sketch over-constrained. Redundant rows are reported separately,
which is the answer to "why did adding that dimension do nothing".

**An under-constrained sketch moves whatever is cheapest.** "These three points are collinear" is
equally satisfied by swinging the line onto the point as by moving the point onto the line, and with
nothing holding the line down the solver will do some of both. That is correct and it surprises
people — including the first version of the test for it. To get a specific answer, constrain the
reference geometry.

**A sketch that will not solve warns rather than failing.** The points are left at the closest fit
found. Erroring would blank the model every time a sketch passed through a contradictory state
mid-edit, which is most of the time while someone is adding constraints.

The derivatives are checked against finite differences in `ConstraintTests`, and that is not
ceremony: a wrong derivative does not produce a wrong answer, it produces a slow or unstable one, so
it presents as "the solver feels flaky" and never as a failing assert.

## Extrudes that measure instead of being told

`Termination` is Blind (a typed distance, as always), **Up to next** or **Through all**. Neither of
the last two needs a boolean, which is worth saying because "up to face" sits next to "cut" in every
CAD tool and reads like it must: both are questions about *distance*, answered by a raycast against
what is already built, and the solid they produce is an ordinary prism.

Rays go out from inside the profile — its centroid plus every corner pulled slightly in, because
casting from the centroid alone reads one point of the target and calls it the answer. The **nearest**
hit wins: a solid has to stop at the first thing in the way, and anything beyond that is already
hidden behind it. A hit at zero distance is ignored, which is what lets a sketch drawn *on* a face
measure past the face it starts on.

**The cap stays flat, and that is the honest limit of doing this without a boolean.** A real up-to-face
trims the new solid against the target *surface*, so a boss meeting an angled face ends in a matching
slope. This ends flat at the nearest point of contact — exactly right when the target is parallel,
and short of it by a visible gap when it is not. Visible rather than silent, and warned about besides:
if the sample rays disagree about the distance, the feature says so and names both numbers.

Through all deliberately clears the far surface rather than stopping flush with it. A prism ending
exactly on a face leaves two coplanar faces touching, which is the case every downstream operation
finds hardest — and precisely what a boolean would then have to resolve.

## Draft, and two-sided extrudes

`Taper` leans every wall by a given angle: the far cap is the near one offset by
`distance × tan(angle)`, so the lean is exact rather than approximate. `SecondDistance` runs the
extrude back the other way from the sketch plane by an independent amount, which a symmetric
checkbox cannot express — a boss 3 up and 1 down is not any symmetric extrude.

**The offset is measured from the EDGES, not the vertices**, and that is the whole difficulty. Push
each vertex along its own bisector and every corner that is not a right angle ends up a different
distance from its own edges, so the draft varies around the profile — and nothing in a render shows
it. `LoopOffset` slides the edge LINES and intersects them, which is exact at any corner angle, and
it is the same reasoning `PlaneOffset` carries into three dimensions for shell.

Holes fall out of the winding with no special case: an outer loop is counter-clockwise so its left
of travel points inward, a hole is clockwise so its left of travel points outward, and one rule
shrinks the part while widening the holes in it. That is what draft does in reality.

**Self-intersection is refused, not handled.** Three checks: the signed area keeps its sign, it has
not collapsed, and no edge has reversed direction. The third is not redundant — pushing a symmetric
profile past its own centre is a half-turn rotation, which *preserves* orientation, so an inside-out
square passes the first two while measuring perfectly healthy. A test drafts a square at 60 degrees
over its own width to keep that honest.

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

## Shell, and why the obvious version is wrong

Hollowing a solid looks like a one-liner: push every vertex along its normal by the wall thickness.
That is incorrect at every corner, and quietly so.

A box corner's area-weighted normal is `(1,1,1)/sqrt(3)`. Move it 0.1 along that and each wall ends
up `0.1/sqrt(3)` = **0.058** thick. The model looks perfectly fine and measures wrong — the worst
kind of bug, because nothing in a render tells you.

Thickness is a property of **planes**, not vertices. So for each vertex the kernel solves for the
point sitting exactly `thickness` from every face plane meeting there:

```
for each adjacent face i:   dot( f_i, p' ) = dot( f_i, p ) - thickness
```

an overdetermined system solved by least squares through the normal equations, in double precision
because a 3x3 determinant of near-parallel normals loses too many digits in single. At a box corner
it returns exactly `(0.9, 0.9, 0.9)` — the vertex travels `t*sqrt(3)`, not `t`, which is the correct
answer and the one the naive version misses.

The tests measure **plane-to-plane distance**, not vertex movement, across a box, cylinder, wedge,
extrusion and revolve. Asserting that a vertex moved by `thickness` would enshrine the exact bug
this avoids. A faceted cylinder makes the point sharpest: its inner vertices land at
`r - t/cos(pi/n)`, strictly tighter than a naive `r - t`, and that closed form is what the test
checks.

An opened face does **not** constrain the solve. It is being deleted, so requiring the inner surface
to stand clear of it would pull the wall back and turn the rim into a 45-degree chamfer instead of a
flat band. Dropping those constraints leaves rim vertices with only two planes, which is why the
solver takes the **least-norm** solution rather than any solution: it slides the vertex straight in
and not along the rim.

Known limits, stated rather than discovered:

- **Self-intersection is refused, not handled.** Shell a shape by more than its thinnest feature and
  the inner surface passes through itself. Building the offset surface properly, so the overlap is
  trimmed away instead, is a much larger algorithm and is still not here — but it is caught and
  refused rather than returned as a closed mesh that measures negative volume in places and looks
  fine from outside.

  Two checks, because one does not do it. **Enclosed volume** catches the common case: shell a
  1-thick plate by 0.6 and its two walls pass straight through each other, while neither *face*
  inverts — each is translated inward, and a translation preserves a normal, so nothing local goes
  wrong. Only the surface as a whole turns inside out. **A flipped face** catches the other kind: a
  cylinder shelled past its radius has its side faces invert around the axis, which is the same
  signature `LoopOffset` looks for one dimension down. The volume check applies only while nothing is
  opened, since an opened inner surface is genuinely not a closed volume.

  Still not caught, and named so the next person knows the difference between "checked" and "not
  possible": two distant walls closing on each other with no single face inverting.
- **Openings must form simple loops.** Two opened faces meeting at only a vertex would put four rim
  quads on one outer-to-inner edge — non-manifold, and nothing downstream accepts it. That case is
  refused with an explanation rather than returned broken.

## Bevel

Cuts every edge whose two face normals diverge past an angle threshold, by a fixed width on each
side — a flat chamfer (Segments=1), not a rounded fillet. `BevelFeature` wraps it into the tree with
two parameters, `Width` and `Angle threshold`, rather than "every edge" — the angle selection is what
tells a genuine corner from the seams a curved surface is tessellated into.

A face's corner is never split into two points. Instead each (face, vertex) pair gets exactly one new
point — the intersection, in that face's own plane, of its two boundary edges after sliding the
selected ones inward by `Width`. That single rule is what makes an edge bevel, a vertex cap and a
plain untouched corner all fall out of the same code: an untouched corner is the case where neither
incident line moved, so the "intersection" is just the original vertex.

The non-local part: a corner can still move because of an edge that **isn't** selected, simply because
the corner's other edge is — that moved point lands exactly on the unselected edge's own line, so the
face across that edge, even though nothing about it was selected, now disagrees with its neighbour
about where their shared edge ends. Left alone that's a T-junction (an open boundary, not a crash).
The fix is to reconcile every edge whose two sides disagree, not only the ones the angle threshold
picked — see the class comment on `Bevel` for the failed alternative (routing a bridge face through
the untouched vertex) and why it reliably added volume instead of removing it.

Skin weights come along if the body is rigged: every new corner point is a genuine cut of exactly one
original vertex, so it inherits that vertex's weights outright rather than an average of its
neighbours.

## UV projection

Box projection picks, per face, whichever of the six axis directions it most faces and projects onto
that plane. Undistorted on hard surface, seams land where the dominant axis changes, and because UVs
are per corner a seam costs nothing — the two faces simply disagree about the UV at a shared
position, which is what a seam *is*.

Handedness is the part that goes wrong. Each direction's `u` and `v` must satisfy
`cross(u, v) == normal`; get one of the six backwards and that side renders its texture mirrored,
which nothing in a grey-box render makes obvious. The test compares the signed area of every face's
UV polygon — all six signs must agree.

Worth knowing when writing tests against this: **a cube is seamless at its corners.** Every corner
has all three coordinates equal, so at `(2,2,2)` the +X face reads `(y,z)`, +Y reads `(z,x)` and +Z
reads `(x,y)` — all `(2,2)`. An unequal box is needed to observe a seam at all.

## Not here yet

**Phase two (next)**: Skeleton editing, auto-weighting, weight painting, SMD export. Bones come before sculpt because you have bone experience.

**Phase three**: Rounded (multi-segment) fillets, then Catmull-Clark subdivision brushes, multires deltas, normal-map bake. The sketch constraint solver was the other item here and has landed.

Boolean is still the notable absence, but it now has a shape. Robust mesh CSG is a decades-old
problem — coplanar faces, floating-point robustness, self-intersection — and a half-working one is
worse than none, so the plan was always an interface with an engine-backed implementation behind it.

The interface exists (`MeshBoolean`, `IMeshBoolean`) and Extrude and Revolve reach for it when
Result is Remove. What does not exist is the adapter between `PolyMesh` and s&box's `PolygonMesh`,
because that is the one piece that cannot be written without the engine in front of you — and a
guessed member name is a compile error that takes the whole editor assembly down, not a polite
runtime failure. `effigy_probe_boolean` in the editor console dumps the real API to write it from.

Everything on this side of that adapter is done and tested: the operand order (target is the part,
tool is the shape of the hole), the result replacing the body without changing its id, a provider
that refuses or throws being reported rather than crashing the rebuild, and a cut that would leave
nothing behind being refused.
