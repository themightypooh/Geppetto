# What is built

Everything here is done and verified unless it says otherwise, and where something was verified the
method is named — because on this project the difference between "the tests pass" and "I looked at
it" has repeatedly been the difference between right and wrong.

**The standing rule:** a mesh can be closed, manifold, Euler-correct and valid while being visibly
wrong. Every hard bug in this repo passed most of its checks. Measure enclosed volume, covered
area, or boundary-edge count — not "it renders and looks fine".

Verified as of 31 August 2026: **1407 kernel checks, 0 failing** (`./tools/test.sh`), and the whole
project **compiles clean in the s&box editor**, 0 errors, 61 tools registered.

---

## Rig Control Editor

**Working and verified by use:** bone posing (rotate by default, `E` to translate), the timeline,
undo/redo, two-bone IK, bone hiding, reference props, viewmodel camera lock, timeline zoom
(ctrl+wheel) and pan (shift+wheel), prop-attach events, the numeric inspector.

**Written but never judged on screen:** most of the tutorial panel's finer layout. It works;
whether it *looks* right at various dock sizes is unconfirmed.

`RigViewport.cs` is the heart of it and the file that has caused every hard bug. `EvaluatePose` in
particular has been rewritten four times — read its comments before changing it, they record what
each previous version broke.

### Known rough edges

- The example clip's wrist never rotates. The IK solver keeps the end bone's orientation, so the
  hand arrives without turning to face the switch. Tutorial step 6 teaches doing it by hand.
- The tutorial's settle step (`frame 22`) checks only "a key exists after frame 21", so it ticks off
  whether or not you actually overshot.
- The reference-prop step ticks the moment a model is assigned, before it's placed anywhere useful.

---

## Effigy — the kernel

Engine-free by design. The test project compiles it **from source** rather than referencing a built
library, so if anything in here ever picks up a dependency the build breaks and says so.

### The feature tree

Ordered history with two properties doing the work, both tested:

**Rollback.** `RollbackIndex` evaluates only the first N features. Rolling back above a Subdivide is
how you get at the low-poly cage — the same cage the sculpt stage will eventually bake onto. That is
why subdivision is a feature in the tree rather than an export step.

**Incremental rebuild.** The body list after each feature is cached, so editing feature 7 of 20
re-runs 7 onward and reuses the snapshot from 6. Without it every parameter drag re-runs the whole
tree and the tool stops feeling live at about a dozen features.

Parameters describe themselves (`FloatParam`, `IntParam`, `ChoiceParam`, …) so one generic panel
renders any feature's dialog — copied from Onshape deliberately. A feature that throws records an
error and the rebuild carries on, so one upstream mistake doesn't cascade.

Tested: editing feature 4 of 6 reuses exactly 3 and re-runs exactly 3; a clean rebuild does no work;
rollback and roll-forward round-trip; a broken feature doesn't stop the ones after it.

### The sketcher

**Curves reference shared point indices**, so two lines meeting at a corner point at the same index.
Coincidence is identity rather than a constraint that can drift, dragging a corner moves both lines
with no bookkeeping, and finding closed regions is an integer graph walk instead of floating-point
position matching.

**Profiles are found, not declared.** `ProfileFinder` walks the curve graph for cycles, works out
which loops nest inside which, and orients every outer loop counter-clockwise — which makes
extrude's winding questions answer themselves. Construction geometry is excluded.

**Caps are single n-gons, not triangle fans.** Catmull-Clark turns an n-gon into n clean quads, so a
sketched profile subdivides properly.

**Branching sketches** work — a line drawn across a rectangle splits it into the two regions it
plainly is. Every curve becomes two directed half-edges, outgoing ones at each point are sorted by
the direction they actually leave in, and walking a face means arriving along one, turning onto its
reverse, and leaving along whichever half-edge sits immediately clockwise. Two dependencies worth
knowing: the sort key must be the **tangent**, not the bearing of the far endpoint, or an arc and a
line leaving the same point sort the wrong way; and dangling curves are pruned **repeatedly**,
because removing one can leave the next one dangling. They are reported rather than silently
dropped.

**Touching is not joining.** A line whose end sits geometrically on an arc but shares no point index
with it is not connected, and its free end gets pruned. That is coincidence-as-identity working as
designed, and it caught out the first version of the test for it.

**Profiles with holes** work. Each hole is spliced into the outer loop along a bridge — out to the
hole and back — turning a ring-with-a-hole into one boundary. Hole walls come free: `ProfileFinder`
hands holes back wound the opposite way, so the same wall code faces them into the hole with no sign
handling anywhere. Profiles *without* holes are untouched and still get their single n-gon, pinned by
a test.

**A holed cap is two n-gons, not a pile of triangles.** `Triangulate.SplitWithHoles` hands the
bridged ring to the same `SplitBridgedLoop` the boolean's cut faces go through, which cuts it on a
second bridge; the cap pass falls back to the ear clipper when the splitter refuses. A 6x6 plate with
a 2x2 hole extrudes to twelve faces where it used to be twenty-four. Two is the floor, not one — a
face is a single loop of corners — and **one hole only**, because two holes put two bridges in the
ring and an n-holed face needs n+1 cuts.

**A concave face's area is projected, not summed.** `PolyMesh.FaceArea` fans about the centroid, and
it used to sum `|cross|`, which is exact for a convex face and too big for a concave one: every
backward fan triangle counted as material rather than as the notch it stands for. Nothing here was
concave until holed caps became two n-gons, each wrapping around its hole — and then four volume
tests failed on meshes that were correct. Projecting each fan triangle onto the face's own Newell
normal is exact for any planar polygon and identical for a convex one. `OrientOutward` and the
area-weighted vertex normals read the same number, so it was never only a test-side concern.

Curve types: lines, arcs, circles, **ellipses and splines**. The spline interpolates (centripetal
Catmull-Rom) rather than using control points, which is what makes every existing constraint mean
the obvious thing when pointed at a spline point. `ProfileFinder` no longer switches on curve type —
`IsClosed` and `Endpoints` are questions the curve answers, so a new curve is one new class.

**Trim, extend, fillet and offset** are edits, not features: they change the curve list in place and
undo takes them back, which is the line Onshape draws too. `SketchIntersect` is exact for line/line,
line/circle and circle/circle; splines and ellipses fall back to sampled tessellations and say so.

### The constraint solver

Levenberg-Marquardt over the constraint residuals. Each rule contributes an equation reading zero
when it holds, plus its derivative. **Seventeen kinds** — horizontal, vertical, coincident,
distance, equal length, parallel, perpendicular, angle, point-on-line, symmetric, radius, diameter,
midpoint, concentric, fixed, tangent (line-to-arc) and tangent-arcs. A new one costs one class and
no change to the solver.

Nothing in `Sketch.cs` or `Profile.cs` had to change for it: coordinates are still what everything
downstream reads. The solver moves points; profile finding, extrude and revolve never learn it
exists.

Five things worth knowing before touching it:

**Tangent is written in length-squared, and the scaling is the reason.** The obvious residual for
line-to-circle is `cross(d,w)² − r²|d|²`, which is smooth and correct and badly scaled — the
convergence test is an absolute 1e-6, so a quartic residual reaching 1e-6 can still be a visibly
untangent line. Dividing through by `|d|²` makes both terms an area, and 1e-6 becomes about 5e-7 of
radius.

**A circle is addressed as a centre plus a rim point, never as a radius.** A radius living in a
float field is invisible to the Jacobian and nothing could drive it.

**Fix is not the solver's pin.** `Solve` takes one `pinnedPoint` and removes its columns entirely,
which is how the sketch gets an absolute frame — one point, chosen by the caller. `Fixed` is the
user-facing rule, there can be as many as you like, and a fix fighting a dimension shows up honestly
as a sketch that will not converge rather than as a rule quietly ignored.

**Angle's residual is `cross·cos θ − dot·sin θ`**, not an angle difference, because `atan2` carries
a branch cut and a residual that jumps by 2π sends the solver the wrong way the moment a line
crosses it.

**Arcs carry an implicit constraint.** An arc reads its radius off the centre-to-*start* distance,
and tessellation snaps its last sample onto the end point wherever that is — so an end that drifts
off the circle produces an arc at the wrong radius with a kink in its final segment. Nothing
enforced it while coordinates were only typed; a solver moves points for a living, so every arc now
contributes "both my endpoints are equidistant from my centre". It is not design intent, it is what
an arc *is*.

Three behaviours that surprise people, including the first version of their own tests:

- **One point is pinned**, because every equation is about differences and the whole sketch could
  otherwise slide. Pinning kills the slide but not rotation, which is why a fully dimensioned
  rectangle still reports one degree of freedom — it really can be spun.
- **Degrees of freedom come from the Jacobian's rank, not the constraint count.** Two constraints
  saying the same thing remove one freedom; counting rows would call a fine sketch over-constrained.
  Redundant rows are reported separately, which is the answer to "why did adding that dimension do
  nothing".
- **An under-constrained sketch moves whatever is cheapest.** "These three points are collinear" is
  equally satisfied by swinging the line onto the point as by moving the point onto the line. To get
  a specific answer, constrain the reference geometry.

A sketch that will not solve **warns rather than failing**, leaving points at the closest fit found.
Erroring would blank the model every time a sketch passed through a contradictory state mid-edit,
which is most of the time while someone is adding constraints.

Derivatives are checked against finite differences in `ConstraintTests`, and that is not ceremony: a
wrong derivative does not produce a wrong answer, it produces a slow or unstable one — so it
presents as "the solver feels flaky" and never as a failing assert.

### Sketching on a face of a solid

`FaceRef` carries **the body id as well as** a point and a normal. That combination is the whole
design, and it was arrived at by breaking both alternatives:

- A pure index (`"Face6"`, FreeCAD's approach) follows a face that moves but jumps to a different
  face when the ordering changes. This is the topological naming problem, FreeCAD's best-known
  long-running defect.
- A pure point-and-normal survives an unrelated upstream edit perfectly, and breaks the moment the
  referenced face **itself** moves — make the block taller and the sketch on its top face is lost.

Resolution is: find that body, take its faces pointing the right way, and among those pick the one
nearest the stored point — so the point *disambiguates between candidates* rather than acting as a
hard constraint. `FaceSketchTests` asserts that growing a block carries the boss on its top face up
with it.

The same principle applies to regions: `RegionSeed` is a **point inside the region** rather than an
index, because profiles are re-found from the curve graph every rebuild and their order is whatever
the walk discovers. A reference should be geometry that can be re-found.

### Building one part out of several extrudes

Extruding off a face of an existing body **adds to that body** rather than starting a second one, so
three bosses on a block are one part and not four. The rule is the sketch's attachment: on a face of
something, it builds that thing up; on a global plane, it starts a new part. `Result` on Extrude and
Revolve overrides either way.

**Add is not a boolean.** The two meshes are combined and nothing cuts the interface, so the face a
boss stands on is still in there on the inside. For what it is for — the part list reading
correctly, the render and every exporter being right — that is the correct trade. What it costs is
that the merged mesh is non-manifold along the join, so operations needing clean topology (shell
especially) will refuse it. That refusal is the honest failure.

**Remove is a real boolean**, because a cut cannot be faked the way Add can. **Auto never removes** —
adding and removing look identical from the geometry, so there is nothing for Auto to read, and a
rule that guessed would eventually guess a hole into someone's part.

### Extrude termination, taper, two-sided

`Termination` is Blind, **Up to next** or **Through all**. Neither of the last two needs a boolean,
which is worth saying because "up to face" sits next to "cut" in every CAD tool and reads like it
must: both are questions about *distance*, answered by a raycast against what is already built.

Rays go out from inside the profile — its centroid plus every corner pulled slightly in, because
casting from the centroid alone reads one point of the target and calls it the answer. The
**nearest** hit wins. A hit at zero distance is ignored, which is what lets a sketch drawn *on* a
face measure past the face it starts on.

**The cap stays flat, and that is the honest limit of doing this without a boolean.** A real
up-to-face trims against the target *surface*, so a boss meeting an angled face ends in a matching
slope. This ends flat at the nearest point of contact — exact when the target is parallel, short of
it by a visible gap when it is not. Warned about: if the sample rays disagree, the feature says so
and names both numbers. Through all deliberately clears the far surface rather than stopping flush,
because a prism ending exactly on a face leaves two coplanar faces touching.

`Taper` leans every wall by an angle — the far cap is the near one offset by
`distance × tan(angle)`, so the lean is exact. `SecondDistance` runs the extrude back the other way
by an independent amount, which a symmetric checkbox cannot express.

**The offset is measured from the EDGES, not the vertices**, and that is the whole difficulty. Push
each vertex along its own bisector and every non-right-angled corner ends a different distance from
its own edges, so the draft varies around the profile — and nothing in a render shows it.
`LoopOffset` slides the edge *lines* and intersects them, exact at any corner angle. Holes fall out
of the winding with no special case: one rule shrinks the part while widening the holes in it, which
is what draft does in reality.

**Self-intersection is refused, not handled.** Three checks: the signed area keeps its sign, it has
not collapsed, and no edge has reversed. The third is not redundant — pushing a symmetric profile
past its own centre is a half-turn rotation, which *preserves* orientation, so an inside-out square
passes the first two while measuring perfectly healthy.

### Sweep and loft

Over one shared `Skinner`. Sweep propagates **rotation-minimising frames** rather than rebuilding a
frame per station from a fixed up-vector, which is what stops a swept helix corkscrewing. Loft
aligns each section to the previous by least total squared distance, and reverses it first if the
windings disagree.

Both are checked by **volume**, because a twisted or inside-out result passes every
closed-and-manifold check: a swept prism encloses exactly area times length, a torus matches Pappus,
a loft between different sizes matches the frustum formula.

Both are quad-only by construction — the walls are nothing but quads and the end caps are single
n-gons — so they are safe cage sources for the sculpt stage.

Both are on the feature strip. Their defaults are what makes a bare button enough, with no selector
to fill in first: an empty `SweepFeature.PathSketchId` means "the sketch before the profile's", and a
`LoftFeature` with fewer than two `Sections` lofts every sketch available — the order a person draws
them in. A path selector for sweep and an ordered section list for loft are refinements, not
prerequisites.

### Shell, and why the obvious version is wrong

Hollowing looks like a one-liner: push every vertex along its normal by the wall thickness. That is
incorrect at every corner, and quietly so. A box corner's area-weighted normal is `(1,1,1)/√3`; move
it 0.1 along that and each wall ends up `0.1/√3` = **0.058** thick. The model looks perfectly fine
and measures wrong.

Thickness is a property of **planes**, not vertices. For each vertex the kernel solves for the point
sitting exactly `thickness` from every face plane meeting there — an overdetermined system solved by
least squares through the normal equations, **in double precision**, because a 3×3 determinant of
near-parallel normals loses too many digits in single. At a box corner it returns exactly
`(0.9, 0.9, 0.9)`: the vertex travels `t√3`, not `t`.

The tests measure **plane-to-plane distance**, not vertex movement — asserting that a vertex moved by
`thickness` would enshrine the exact bug this avoids. A faceted cylinder makes the point sharpest:
its inner vertices land at `r − t/cos(π/n)`, strictly tighter than a naive `r − t`.

An opened face does **not** constrain the solve. It is being deleted, so requiring the inner surface
to stand clear of it would pull the wall back and turn the rim into a 45° chamfer. Dropping those
constraints leaves rim vertices with only two planes, which is why the solver takes the
**least-norm** solution: it slides the vertex straight in and not along the rim.

Known limits, stated rather than discovered:

- **Self-intersection is refused, not handled**, by two checks because one does not do it.
  **Enclosed volume** catches the common case — shell a 1-thick plate by 0.6 and its walls pass
  through each other while neither *face* inverts, since a translation preserves a normal and only
  the surface as a whole turns inside out. **A flipped face** catches the other kind — a cylinder
  shelled past its radius has its side faces invert around the axis. Still not caught, and named so
  the next person knows the difference between "checked" and "not possible": two distant walls
  closing on each other with no single face inverting.
- **Openings must form simple loops.** Two opened faces meeting at only a vertex would put four rim
  quads on one outer-to-inner edge — non-manifold. Refused with an explanation.

### Bevel

Cuts every edge whose two face normals diverge past an angle threshold, by a fixed width each side —
a flat chamfer, not a rounded fillet. The angle selection is what tells a genuine corner from the
seams a curved surface is tessellated into.

A face's corner is never split into two points: each (face, vertex) pair gets exactly one new point,
the intersection in that face's own plane of its two boundary edges after sliding the selected ones
inward. That single rule makes an edge bevel, a vertex cap and an untouched corner all fall out of
the same code.

**The non-local part**, and it is the thing to understand before changing anything here: a corner can
move because of an edge that **isn't** selected, simply because the corner's other edge is. That
moved point lands on the unselected edge's own line, so the face across it — nothing about it
selected — now disagrees with its neighbour about where their shared edge ends. Left alone that is a
T-junction. The fix reconciles every edge whose two sides disagree, not only the ones the threshold
picked.

**A corner is capped at 20× width from its original vertex**, and that cap is load-bearing. A corner
lands at roughly `width / sin(turn)` from its vertex, so a nearly-straight corner throws it
arbitrarily far — and ear clipping a thin annulus produces genuinely collinear corners, turn 180°,
sin 1.5e-5. Those put vertices **15,000 units away on a model 20 across**.

Worth knowing how long that hid: the result stayed finite, closed, manifold and Euler-correct, so
every numeric check passed while it was live. The guard meant to catch it tested `|sin| < 1e-9`,
which reads as a floating-point epsilon but means "only reject corners straighter than 6e-8 degrees"
— no mesh is ever that straight, so it never fired once. **What found it was a render**: the model
collapsed to a speck because the view had to stretch to fit one stray vertex a thousand diameters
out.

Skin weights come along if the body is rigged: every new corner point is a genuine cut of exactly
one original vertex, so it inherits that vertex's weights outright rather than an average.

### The mesh boolean

`MeshBoolean`/`IMeshBoolean` is an interface and a provider slot rather than an implementation: the
kernel knows what a boolean IS without knowing how to do one, which keeps the engine-free promise
intact. With no provider installed, Remove fails with a message saying so instead of producing
something plausible.

The adapter is `Editor/EffigyEditor/EffigyMeshBoolean.cs`, over `Sandbox.PolygonMesh.PerformBoolean`.
It was written from Facepunch's own call site (`addons/tools/Code/Scene/Mesh/Tools/BooleanTool.cs`)
plus a reflection dump, rather than guessed: `PerformBoolean` mutates its receiver, the relative
transform places the second mesh against the first, and UVs must be recomputed after because the
boolean makes faces that never had any.

**It has been run and it works.** `effigy_test_boolean` on two unit boxes offset by half returns
union 20v/12f, subtract 14v/9f, intersect 8v/6f — all closed, all exactly the arithmetic answer. And
the question that hung over it longest is answered well: **it returns n-gons, not triangle soup**, so
a cut does not poison the subdivision cage and the sculpt stage is safe.

Getting from "the adapter works" to "a cut appears on screen" took four bugs, every one producing a
mesh that passed some checks and looked plausible:

**Direction.** A sketch on a face takes that face's *outward* normal, so a cut extruded off it left
the part instead of entering it — the two solids touched on a plane and enclosed nothing. Fixed in
the kernel (`ExtrudeFeature.DirectionSign`).

**Bridged faces.** A half-edge face is one closed loop, so the engine returns a face-with-a-hole as a
single loop running out to the hole and back along the same seam, visiting two vertices twice.
`PolyMesh` forbids that. Handed straight to `AddFace` it produced a mesh whose OBJ Blender filled in
solid — the tunnel was there and its mouth was covered. `AddFaceSplittingBridges` takes them apart
on the way in.

**What that first cost, and no longer does.** Taking them apart meant *triangulating* them, which is
correct and expensive in the only currency the user spends: a `Face` is the unit of selection and of
material assignment, so a 24-gon cylinder cap with a pocket cut into it came back as **29 triangles**
and clicking it to paint it painted one of them. The mesh was closed, manifold and the right volume
every time — the existing hole tests all passed while the face a person clicks on had been shattered.
`Triangulate.SplitBridgedLoop` now recovers the outer boundary and the hole from the bridged loop and
cuts the ring on a **second bridge**, giving **two n-gons**. Two is the floor, not one: a face is a
single loop of corners, so a face with a hole in it cannot be fewer, and no modeller does better.
The splitter refuses anything it is not certain of — more than one hole in a face, repeats that do not
sit where a bridge puts them, a hole with no valid second bridge — and falls back to triangulating,
which is never wrong, only coarse. `HoleTests` asserts the **face count**, which nothing did before.

**The wrong ear clipper.** `Triangulate.Polygon` assumes a simple polygon and does not fail on a
bridged loop — it returns an overlapping fan that covers the hole back in. `Triangulate.BridgedLoop`
welds first, then clips.

**Exact float welding, the last and the worst.** Reading the result back welded vertices by exact
equality, on the reasoning that the engine's floats come back untouched. True of a corner the
boolean copied; false of one it **calculated** — an intersection reached along two edges is computed
twice and agrees to about six digits. Two vertices a hair apart meant every edge through them was
claimed by one face rather than two, so the cut's mouth was an open boundary that nothing closed and
the flat face sat over it looking solid. Welding within 1e-4 fixed it.

Each was found by measuring something different: a bounds overlap, a repeated-vertex check, a
covered *area*, and a boundary-edge count. `MeshHoleRepair` also exists for when the engine drops an
inner loop entirely rather than bridging it, and `effigy_dump_tree` prints all of these at once,
which is what made them findable at all.

### Rigging

- **Skeleton** — bones, hierarchy, orientation, bind pose, and editable: add, remove, rename, edit a
  bone's head/tail, mirror a subtree across a plane.
- **Skin weights capped at 4 influences per vertex** (`SkinWeights.Prune`, applied by both writers),
  because the compiler culls beyond that and the docs call the automatic culling "far from ideal".
- **Auto-weighting** without heat diffusion: nearest-bone rigid weighting (`BindRigid`) smoothed
  across mesh adjacency (`SmoothWeights`), with an optional per-body-to-bone pin (`BindBodies`) for
  anything that should stay rigid.
- **`BindBodies` and `BodyRange` exist so a rig can be reapplied to new geometry rather than
  invalidated** across rebuilds. Vertex indices are never stored. That is what lets the parametric
  history stay alive under a rig.

**The pipeline works end to end today**: place bones on a model, export a real skinned `.vmdl`,
bring it into Rig Control and pose it.

### The rig design, as decided

Effigy owns rig **authoring and binding**. Rig Control owns **posing, keyframes, IK and
constraints**. Effigy does not grow a second animation editor.

```
Effigy document → rebuild bodies → reapply body-id bindings → write DMX + VMDL
                → .ctrlrig points at the VMDL → .riganim opens in Rig Control
```

The skeleton lives in the Effigy document rather than in `RigDocument`, because `RigDocument`
deliberately owns a model reference and animation constraints while the engine-free Effigy document
is the source of truth for parametric binding. The exported model is a baked consumer artifact; the
Effigy document remains authoritative.

Bone names are stable identifiers — Rig Control keys bones by name, so renaming one is a deliberate
breaking change for existing clips.

### Saving

`StudioDocument` reads and writes `.effigy`: hand-written text, one record per line, and it diffs —
which matters for a format holding someone's model, since a corrupt binary is a shrug and a corrupt
text file is usually one bad line you can see.

**Fields are found by reflection rather than listed.** A feature's `Parameters` property is not
usable as the save list: `PrimitiveFeature` changes its parameters with the shape dropdown, so a box
saved today would not know what to do with the radius it wants tomorrow. Public fields are stable,
so a new feature saves the moment it is written.

The risk that carries is a field of a type the writer has no case for, so `DocumentTests` sets every
field of every feature type in the assembly to a non-default value and round-trips it — it caught
`ShellFeature.OpenFaces` on the first run.

Two things the format must get right, both asserted: **ids are preserved exactly** (a `FaceRef`
holds a body id, so a load that reissued them would break every sketch drawn on a face the moment
the file reopened — the worst thing this format could do), and **unknown fields are skipped, not
fatal**, though a file from a newer format version is refused by name.

### Two decisions worth knowing before changing anything

**Quads are a requirement, not a preference.** Catmull-Clark turns clean quads into a clean surface
and triangle soup into a lumpy one, and triangles leave valence-3 vertices that stay extraordinary at
every level and pucker under a sculpt brush. That is why there is no UV sphere here — its pole fans
are triangles. `QuadSphere` costs nothing extra and has no poles. The same requirement arrives
independently from skinning: quads deform correctly, triangles pinch at joints.

**UVs are stored per face corner, not per vertex.** A box corner belongs to three faces that each
want a different UV for the same position; per-vertex UVs would force one value and smear the
texture across every seam. It also makes UV subdivision purely local, so seams survive subdivision
for free.

### What the kernel suite actually checks

Not "it renders, looks fine". Subdivision is the classic looks-right-is-wrong case: a vertex rule
that is subtly off still produces a smooth plausible blob, it just converges to the wrong surface.

- Euler characteristic before and after subdivision, including the tube at genus 1
- the exact growth laws — `V' = V+E+F`, `F' = total corners`, `E' = 2E + corners`
- winding via the divergence theorem: enclosed volume must be positive, and a 2×2×2 box exactly 8
- successive subdivision levels must move points *less* each time, not drift
- an open mesh keeps its boundary, stays planar, keeps its corners
- a box exports with exactly 6 hard normals; a 16-segment cylinder with at least 16
- solids against known volumes: an extruded 2×3 rectangle encloses exactly 24, a revolved square
  matches **Pappus' theorem**, a quarter revolution is exactly a quarter of the full one
- a mirrored body's enclosed volume stays positive — the winding-reversal check, guarding a bug that
  renders black and looks fine in wireframe

Three of those exist because they caught real bugs: **pattern merge** appended into the source mesh
while the loop kept re-reading it, so instance counts doubled (6, 12, 24, 48 faces rather than 6,
12, 18, 24); **revolve winding** came out inverted, and rather than enumerate the cases the fix
measures the finished volume and flips if negative; **a profile straddling the axis** produced a
mesh where every face existed twice with opposite winding — zero enclosed volume, and entirely
plausible until measured.

**`RenderCheck` is the other half of the suite.** It rasterises a mesh and reduces it to coverage,
island count and front/back parity, catching what counting cannot: a vertex in the wrong place, a
detached fragment, and a face wound backwards. The Bevel bug is what prompted it. The tests damage
good models three ways and fail if a check stays quiet.

**A habit worth keeping:** three separate limitations in this repo were documented as needing the
mesh boolean and none of them did — profiles with holes, revolve with holes, and up-to-face
termination. Treat every remaining "not supported yet" string in the kernel as suspect until it has
been re-derived rather than re-read.

---

## Effigy — the editor

All of it compiles clean. Where something has not been *run*, it says so, and
[WHAT-IS-LEFT.md](WHAT-IS-LEFT.md) tracks it.

- **The feature tree panel** with the Default geometry node, a **parts list** populated from
  `_studio.Bodies`, a **rollback bar** driven from the context menu and the Edit menu, and a
  **feature context menu** (rename, suppress, roll-to-here, delete).
- **Feature dialogs** on Onshape's model: red spine plus a status line naming the reason, the tick
  disabled while the feature is broken, `Accept()` refusing to commit, and Enter / Escape /
  Shift+Enter wired. `IsBroken` is the single predicate.
- **Numeric fields with an expression evaluator** — `EffigyNumericField` + `EffigyExpression`, a
  hand-written recursive-descent parser with no dependency. `1/8` → `0.125`, `sqrt(2)*10`, `2^-1`,
  `max(3,7)`, `pi`, `1e3`. Precedence follows every calculator: `-2^2` is `-4`, `2^3^2` is `512`.
  Trig is in **degrees**. Lengths are **dimensionless** and reject unit suffixes — `5mm` fails
  rather than silently storing 5, because the kernel has no millimetre. **No implicit
  multiplication**: `2pi` is rejected as a typo. A slider appears alongside the field only when the
  parameter declares finite bounds within 1024. The grammar was verified before commit by
  transliterating it to Python and running 43 cases, including every input that must be *rejected*.
- **Selection boxes** for planes, faces, bodies, sketches and regions, all on one pattern: arm the
  box, click in the viewport. `EffigySketchSelector` arms for any `SketchConsumingFeature`. The
  plane selector *also* arms face-of-solid picking at the same time, so one click resolves to
  whichever was hit — exactly like Onshape never asking "plane or face?" first.
- **Floating tool strips** on the 3D canvas rather than window furniture: a feature strip and a
  sketch strip, only ever one visible, swapped together by `EnterSketch`/`FinishSketch`. Buttons are
  40×40 with 5px in-group and 16px between-group spacing.
- **Hand-drawn icons** in `EffigyIcons`, not font glyphs. That matters: s&box ships classic
  `MaterialIcons-Regular.ttf`, so a Material *Symbols* name renders as nothing at all. Six icons the
  first render condemned were redrawn — Extrude grows up off its profile instead of reading as a
  plumb bob, Revolve draws the turned *shape* rather than the operation, Bevel is a filled block
  with a deep chamfer, Shell fills the *wall* and leaves the void out, Subdivide shows one quadrant
  genuinely denser, Circular pattern's ring is solid because twelve dashes at toolbar size are a
  smudge. **Judge icons at 24px**, not 18 — `ButtonSize` 54 with `IconScale` 1.5 lands the ±8-unit
  glyph box at about 24.
- **Constraint UI** — a persistent sketch selection (click to accumulate, click empty space to
  clear) with a right-click menu built fresh from `ConstraintTools`. Dimensions open pre-filled with
  what the sketch currently is and go through the same expression evaluator, so `25/2` works. A rule
  that cannot be satisfied is taken back out and the geometry restored exactly. **Constraint glyphs
  are drawn**, and clicking one removes that rule — "why will this line not move" is a question about
  a specific place on the drawing, so the answer sits next to it. A rule relating two segments marks
  **both**; an angle is marked where its two lines actually cross, out in space if that is where the
  extended lines would meet. **All seventeen kinds are reachable** — the last six went in with the
  selections that name them unambiguously: Diameter beside Radius on an arc (offer either and neither
  is offered again, being one rule written two ways), Midpoint beside Point-on-line, Concentric on
  any two centres, Fix on a lone point, and a tangency both for a line and an arc and for two arcs —
  the second picking internal or external from the arrangement already drawn rather than asking.
- **Right-click a face for a material menu** — the slots, a rename for the one it is on, and the
  slot-shading toggle. It holds last frame's cursor ray, because `Gizmo.CurrentRay` means nothing
  inside a menu callback, and refuses the menu for a quarter second after the fly camera last moved,
  because the context-menu event arrives on button *release* and every orbit ends over the model.
- **Tree click selects** (`_tree.OnSelectionChanged`).
- **Plane corner resize** — per-plane size state (`_planeHalfSize`, replacing one shared constant),
  hover handles (`DrawPlaneCornerHandles`) and a drag gesture with a minimum-size clamp.
- **A Settings window** with a hand-painted "show plane grid" switch (a checkbox reads as "tick this
  to agree"; what is wanted is "this is on") and a palette dropdown over `EffigyPalette.All`.
- **A bone-authoring panel** (`EffigyRigPanel.cs`) — place bones by clicking the model, chain and
  branch from a selected bone, rename/delete with correct re-parenting, mirror one side onto the
  other, a numeric inspector, optional body-to-bone assignment, and full undo/redo. Nothing in the
  rigging pipeline happens without it.
- **Sketch shortcuts** — `N` normal-to (flipping on the second press), `L` line, `C` circle, `Q`
  construction. Deliberately, picking a plane does **not** rotate the view: Onshape does not either,
  and automating it is a standing request on their forum rather than shipped behaviour.
- **Region shading and endpoint-degree diagnostics** — closed regions shade, unshaded means the
  profile is open and will not extrude.

### Editor divergences from Onshape, all deliberate

- Onshape swaps a numeric field's text between expression and result on focus change. That needs
  `LineEdit` focus events, unproven against this editor's API, so the evaluated result sits
  continuously in a label beside the field — `1/8` reads `= 0.125`.
- Onshape's rollback bar is dragged between rows. A `TreeView` gives no row to drag into, so the bar
  moves by menu command with a "Rolled back — N of M features active" readout.
- The feature context menu handler is on the panel widget, not the tree rows, because `TreeNode` has
  no context-menu hook proven against this API — so you select a feature first, then right-click.
- Selection accumulates on plain clicks rather than Ctrl-clicks, because no modifier-key API is
  proven in this corpus and an unproven member name takes the whole editor assembly down.
- There is no constraint *toolbar*: offers change with every click, so a strip of buttons would
  relabel and re-enable itself per frame. A menu is built fresh each time from proven machinery.
- The view-cube corner indicator is a text label, not clickable. The seven orientations are in the
  View menu.

---

## Shader Forge

All five phases from the design doc are built, and **live preview is in** — type a word and the
preview sphere changes as it lands, no Generate click. Forge writes a slim `.shader` of just the
matched blocks; the live material is `shaders/custom/shaderforge_live.shader`, every block gated.

Four causes of the first run's failure are fixed, and they are the useful record here:

1. **The extension was wrong** — s&box shader source is `.shader`, not `.shad`. A `.shad` is never
   compiled, so nothing produced the `.shader_c` the material system went looking for.
2. **Parameter names could collide with engine globals.** Shader parameters are globals in a shared
   namespace with the material system, so a plain `g_vColor` can collide and quietly lose. Every
   generated parameter is now prefixed `g_flSf*` / `g_vSf*`.
3. **`i.vNormalWs` is a float4 here**, so it needs `.xyz` before `normalize`.
4. **`Depth()` → `Depth( S_MODE_DEPTH )`.**

Parameters carry `Attribute( "..." )` alongside `UiGroup` so they can be driven at runtime — without
it Hit Flash and Health Reactive are pointless, since the whole premise is that game code pushes
values at them.

**`Assets/shaders/pixel_arms.shader` is the single most useful reference in the repo for this work.**
Read it before changing the emitter. It confirms `m.Albedo`, `m.Normal`, `m.Roughness`,
`m.Metalness`, `i.vPositionWithOffsetWs.xyz`, `i.vPositionSs.xy` and `i.vTextureCoords.xy`.

### Design decisions worth not re-litigating

- **The kernel is not duplicated.** Effigy keeps two copies because it stays portable to Godot;
  Shader Forge emits s&box shader format and is editor-only, so it lives once in
  `Editor/ShaderForge`.
- **Blocks contribute to five slots** — Common, Vertex, Uv, Surface, Post — rather than the design
  doc's two. Splitting Uv out lets a warp run before `Material::From` samples textures; splitting
  Post out lets Toon band the *lit* result. Unrelated blocks combine because each writes only to its
  own slot.
- **`SFPulse()` is always emitted**, returning `1.0` when no time-modulation block was selected.
  That is the whole mechanism behind "glowing edges that pulse": Emissive multiplies by it
  unconditionally, and neither block knows the other exists.
- **Generate writes the file**, because a `.shader` has to be on disk for the pipeline to compile it
  — so there is no in-memory preview path that could drift from what gets saved.
- **Tweak controls are built from the generator's own parameter list**, not read back off the
  compiled shader. The generator declared them, so a round trip through `Shader.Schema` would be
  asking an unverified API for something already known.
- **Every engine call is funnelled through `ShaderForgeBridge.cs` and guarded**, on purpose:
  generating and saving shaders must work even if every preview API is wrong. If the preview is dead
  the tool still writes correct files and the panel says so, rather than the window dying.

Three blocks are honest approximations, documented at their definitions: **Toon** bands shaded
luminance rather than replacing the lighting model; **Outline** is a silhouette band from the
view-facing term, not an inverted hull; **Heat Distortion** warps the surface's own UVs, not the
frame buffer — on an untextured surface it will look like nothing is happening, and that is expected.

---

## Decisions taken, so they are not re-argued

**Parametric first, sculpt on top — not sculpting as the starting point.**

- A sculpt-first model cannot be rigged. It has no clean topology and no UVs, and getting them means
  retopology and unwrapping — the two hardest jobs in the pipeline and exactly the two a
  non-modeller cannot do. Starting parametric means they never come up.
- **The failure modes are not symmetrical.** A mediocre parametric model is a plain shape that
  works. A mediocre sculpt-first model is a shapeless blob nothing downstream can consume.
- Quads deform correctly when skinned; triangles pinch at joints. Same requirement Catmull-Clark
  imposes, arriving from a different direction — a good sign the constraint is real.
- UVs nearly come free on parametric geometry, and those UVs are what the normal-map bake needs.
- Collision comes free from parametric history: a model known to be a union of N convex primitives
  *is* its own physics representation.

**Subdivide-then-sculpt does work.** An earlier draft claimed otherwise by conflating two things:

| | Moves vertices | Undercuts / overhangs |
|---|---|---|
| **Displacement** (Source-style, and s&box's mapping Displacement tool) | along normals only, heightfield on a fixed grid | **no**, at any level |
| **Sculpting on a subdivided mesh** (ZBrush, Mudbox, Blender multires) | freely, in 3D | **yes** |

The first genuinely cannot make an ear. The second is the industry-standard workflow and is what
this tool should do.

**Multires, not SDF.** A single signed-distance field with CSG primitives and brushes both writing
into it, meshed with dual contouring, is the better end state and weeks-to-months of work for the
meshing alone. Multires gets most of the benefit for a fraction of it. Revisit only if the
live-both-ways property turns out to be the actual point.

**No half-edge mesh, and no dynamic topology.** `PolyMesh` argues adjacency-on-demand is enough, and
its stated switch condition is "interactive per-element editing arrives" — sculpting is not that,
since it moves positions and never changes topology. Dyntopo destroys the cage correspondence, which
is the entire value proposition.

**s&box, not Godot.** This was evaluated seriously and the verdict is recorded because it will come
up again. Godot is the better engineering bet and the worse strategic one:

- *For Godot:* export is two documented calls (`bake_static_mesh()` + `ResourceSaver.save()`)
  instead of a hand-written KV3 `.vmdl`; the CSG core is shipped and documented; bones and weights
  are just surface arrays with no file format to author; the plugin API is documented and stable;
  vastly more users; no gatekeeper.
- *Against Godot, and this is what decides it:* **the pipeline ends in Marionette, which is
  s&box-only.** A Godot modeller would have nothing to hand its rigged output to, so the real cost
  is the modeller *plus* a second Marionette. Beyond that, the pain justifying the tool is much
  smaller — Godot's Blender pipeline is genuinely good, including native `.blend` import, so a Godot
  tool has to beat Blender at a specific job rather than being the only option. And s&box has loud
  unmet demand, a low bar, and Marionette already standing in it.
- One trap worth keeping if it is ever revisited: riding built-in CSG buys phase one and costs phase
  two, because `bake_static_mesh()` returns triangles and triangle soup subdivides badly. And
  Godot's `bake_collision_shape()` returns a **ConcavePolygonShape3D**, which does not work on
  dynamic rigid bodies at all — parametric history solves that and the built-in path does not.

**Keep the kernel portable anyway.** Engine-agnostic: primitives and tessellation, the modifier
stack, bevel/mirror/array/shell, UV projection, convex decomposition, subdivision and multires
deltas, brush maths and the BVH, normal-map baking, the document format and undo model. That is most
of the tool. Engine-specific and genuinely small: viewport rendering and gizmos, the property panel,
undo integration, export.

---

## Confirmed engine facts

Read from Facepunch's own documentation or shipped source. Anything not on this list should be
treated as a lead until it is.

| Finding | Where |
|---|---|
| ModelDoc imports **DMX, SMD, FBX, OBJ, VOX** — but **SMD is not on the import list in practice**, which is why DMX is what the rigged export writes | `docs/editor/model-editor.md` |
| A model that isn't fully static needs at least an `AnimBindPose` node, or morph targets and IK data silently break | same |
| **Max 4 weight influences per vertex.** Extra weights are culled and normalised automatically, which the docs call "far from ideal" | same |
| Creating bones in ModelDoc is a last resort. Bones belong in the source mesh | same |
| `citizen.vmdl` ships as a readable source file at `sbox\addons\citizen\Assets\models\citizen\citizen.vmdl` | same |
| Scene Mapping mode (`M`) ships Primitive, Vertex, Edge, Face, Texture, Vertex Paint and Displacement tools | `docs/editor/mapping/index.md` |
| `AssetSystem.CreateResource` takes an **absolute** path | `RigSampleBuilder.cs:148` |
| The export route that actually works: hand-written KV3 `.vmdl` (`EffigyWindow.BuildSkinnedVmdl`/`BuildVmdl`) alongside a DMX or OBJ, registered and compiled through `ExternalAssetTools`/`AssetSystem.FindByPath` | built and run |
| `PolygonMesh.PerformBoolean` mutates its receiver; the relative transform places the second mesh against the first; UVs must be recomputed after | `BooleanTool.cs` + reflection dump, then run |
| s&box ships classic `MaterialIcons-Regular.ttf`, **not** Material Symbols — a Symbols name renders as nothing | `RigIconButton` class comment |
