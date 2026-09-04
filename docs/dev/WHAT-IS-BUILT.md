# What is built

Everything here is done and verified unless it says otherwise, and where something was verified the
method is named — because on this project the difference between "the tests pass" and "I looked at
it" has repeatedly been the difference between right and wrong.

**The standing rules:**

- A mesh can be closed, manifold, Euler-correct and valid while being visibly wrong. Every hard
  bug in this repo passed most of its checks. Measure enclosed volume, covered area, or
  boundary-edge count — not "it renders and looks fine".
- A feature that cannot do what was asked says what it was asked, what stopped it, with this
  model's numbers, and what would work instead. A feature that did nothing is never a success.

Verified as of 31 August 2026: **2272 kernel checks, 0 failing** (`./tools/test.sh`). The diagnostic
dialog and tree tooltip are written against the shipped `Editor.Label.WordWrap` and
`TreeNode.GetTooltip` APIs; they have not been judged on screen.

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

### Diagnostics

A feature that fails or degrades produces a `FeatureDiagnostic`: a one-line **problem**, a **cause
with this model's numbers**, and a list of **remedies**. `Feature.Error` / `Feature.Warning` stay as
strings (`Error = diagnostic.Problem`) so PartStudio, RebuildReport and existing tests keep working.
`Fail` / `Warn` make the shape hard to get wrong. A plain `InvalidOperationException` still becomes
a diagnostic with only a problem line, so every existing throw keeps working.

`PolyMesh.SignedVolume` is the kernel quantity a refusal can rest on — the eleven private `Volume()`
copies in the test project are gone.

**Fillet and Chamfer** are the case this exists for. On a 2×2×2 cube, `Fillet(cube, 0.85, 15, 4)`
used to return an inverted solid (`volume −0.43`, Euler 2, `valid, closed`) and report success.
It is now an error, and the diagnostic names the largest radius that still fits. `Fillet(cube, 0.2)`
still builds. A blend that removes more than half the solid warns rather than fails. A no-op
(`Fillet(cube, 0.1, 179)`) is an error, not a silent success. The five silent EdgeBlend degradations
(clamped setbacks, squared corners, flattened arcs, uncapped boundary vertices, empty selection)
speak as warnings, except the empty selection which is an error.

**The boolean** no longer stops at *"the engine's boolean rejected these two solids"*. Before
handing that up, `MeshBoolean.Apply` says whether either solid is open and whether their bounds
even overlap — including the gap along which axis, as a number.

**Shell** refuses a thickness that turns the inner surface inside out, and the diagnostic names a
thickness that still fits, computed by the same bisection fillet uses. Pinched openings and
"open every face" are named as themselves, not as a thickness problem.

**A selection naming a body that no longer exists is an error**, not a warning and not a silent
no-op. The geometry upstream is left alone.

The empty-studio sweep (`AllFeaturesTests.TestEmptyStudioErrors`) now requires a `Cause` and a
non-empty `Remedies` list, so every new feature is held to it.

Tested: `DiagnosticTests` — oversized fillet/chamfer error and do not invert, small fillet still
builds, no-op is an error, too-thick shell offers a thickness under half the plate, boolean miss
names the gap along X, boolean on a plane names the boundary, missing-body selection is an error,
empty-studio fillet has a cause and a remedy, signed volume of a 2×2×2 box is 8. The kernel half is
headless; the dialog panel and the yellow warning icon on the tree are written and not yet seen.

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

**A holed cap is n+1 faces, not a pile of triangles.** `Triangulate.SplitWithHoles` cuts each hole
against whichever face it landed in, so one hole gives two faces, two give three, and the cap pass
falls back to the ear clipper only when the splitter refuses. A 6x6 plate with a 2x2 hole extrudes to
twelve faces where it used to be twenty-four; add a second hole and it is eighteen. Two is the floor
for one hole, not one — a face is a single loop of corners, so a face with a hole in it cannot be
fewer.

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

**Cut is trim swept rather than clicked.** `SketchCut` takes one segment of a drag — the piece of
path the cursor covered since the last sample — finds every curve it crossed, and hands each
crossing point to `SketchEdit.Trim`. Deliberately the same call, so the two tools cannot disagree:
swiping a rectangle's edge takes the whole edge because its corners are where it meets its
neighbours, and swiping a lone line takes the line because a curve crossing nothing has no piece
smaller than itself. Splines and ellipses, which trim refuses, are removed whole — a cut tool that
silently does nothing when dragged through one reads as broken rather than as careful. The stroke
is never geometry; only the editor holds it, and one drag is one undo step, taken on the first cut
rather than on the press so an empty sweep is not a Ctrl+Z that restores an identical sketch.

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

### Chamfer and Fillet (`EdgeBlend`)

Cuts every edge whose two face normals diverge past an angle threshold, on both adjacent faces. The
angle selection is what tells a genuine corner from the seams a curved surface is tessellated into.

**Two features, one algorithm.** `EdgeBlend.Chamfer` takes a distance and leaves a flat cut;
`EdgeBlend.Fillet` takes a radius and a segment count and leaves an arc. They are separate features
in the editor, named and dimensioned the way Onshape names and dimensions them, because a rounded
corner is not a chamfer with a number turned up to anyone using it. `BevelFeature`, what the chamfer
used to be called, still loads out of old documents — see `StudioDocument.RenamedFeatures`.

They differ in exactly two places, both in `EdgeBlend`'s comments. The **setback is derived** for a
fillet (`r/tan(φ/2)`, per edge, from that edge's own opening angle) where a chamfer's distance is
the setback outright — the same number only on a 90° edge. And the **single bridging quad becomes n
quads across an arc**, whose points are threaded into the vertex cap at each end in the cap's own
cyclic order; arc points in the bridge but not the cap are T-junctions that pass every numeric
check. A one-segment fillet is byte-for-byte the chamfer, and a test asserts it.

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
`Triangulate.SplitBridgedLoop` now recovers the outer boundary and every hole from the bridged loop —
peeling one bridge at a time, shortest run first, because an innermost run cannot have another nested
inside it — and cuts each hole against whichever face it landed in, giving **n+1 n-gons**. A face
with two pockets in it comes back as three faces. Two is the floor for one hole, not one: a face is a
single loop of corners, so a face with a hole in it cannot be fewer, and no modeller does better.
The splitter refuses anything it is not certain of — repeats that do not sit where a bridge puts them,
a hole that lands in no face, a hole with no valid pair of bridges — and falls back to triangulating,
which is never wrong, only coarse. `HoleTests` asserts the **face count**, which nothing did before,
and builds its twice-bridged fixture by splicing rather than by hand, because a sixteen-corner ring
with two seams typed out as a literal is one nobody can check by reading.

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

### Weight painting, and how paint survives a rebuild

`WeightBrush`, `WeightPaintLayer`, `WeightPaintSession`. Auto-weighting is nearest-bone smoothed
across adjacency, and it is right most of the time and wrong in exactly the places that show — a
finger picking up its neighbour's bone because the two are closer through space than along the
surface, an armpit, the inside of an elbow. Those are minutes of painting and hours of anything else.

**The invariant is the whole problem.** Every vertex's influences are non-negative and sum to one,
and `Prune`, Catmull-Clark and the compiler's own culling all lean on it. So a brush cannot add to
one bone: what it gains comes from the others proportionally and what it loses goes back the same
way. Every operation is "move the painted bone to w, rescale the rest to 1 - w".

**The case with no answer.** A vertex weighted entirely to the bone being subtracted from has nowhere
to put the weight. Normalising an all-zero set binds it to nothing, which collapses the vertex to the
model origin on export; leaving 1.0 silently makes the brush look broken. It is refused and counted,
so the tool can say which vertices could not move and why.

**Paint survives a rebuild by the sculpt stage's own argument.** Effigy never stores a rig by vertex
index — that is what lets the parametric history stay alive under a rig — so a naive paint would be
recomputed away. `WeightPaintLayer` keys paint on `MultiresSculpt.TopologyId` (counts and face
indices, deliberately not positions) and re-applies it AFTER `BindBodies`: topology unchanged means
the numbering still means what it did, topology changed means the paint is kept and marked stale
rather than misapplied. Two differences from a sculpt delta, both load-bearing: **bones are stored by
NAME**, because `RemoveBone` re-indexes everything after the hole; and **the painted result is stored
rather than a delta**, because a delta of a normalised quantity is not a well-defined thing.

The editor half is not written. `WeightPaintSession` exists so it will be thin.

### The bind pose, and the bones that were quietly being pruned

`VmdlAnimation`. ModelDoc's docs say a non-static model needs an `AnimBindPose` or morph targets and
IK data break quietly, and this project never had one because nothing here had seen the node's real
shape. `first_person_arms_preview.vmdl` ships as source and carries it whole, so it is copied field
for field — every field, including the ones that look like defaults, because the compiler's defaults
are not documented anywhere this project can read and the file known to work has all of them.

**Then the run answered a second question and turned up a third.** Bone names are not recoverable
from a `.vmdl_c` by inspection, but this project's own `rig_test_follow` lists them when asked for a
bone that does not exist. A two-bone sample came back `Bones: root` — one of two. Adding a
`BoneMarkupList` made it `Bones: root, child`. So the markup list is not a precaution against a
theoretical prune; it is the only reason Effigy's bones exist in the compiled model at all, and it
moved into the kernel so the sample and the editor cannot disagree about it.

### A hand-authored clip reaches the compiled model

`DmxAnimWriter`, plus `VmdlAnimation.AnimationList`. Effigy could export a rig and a skinned mesh but
had no way to put *motion* inside a compiled model, so nothing it made could be handed to AnimGraph.

**The obvious answer was SMD and it is the wrong one.** A sequence SMD is the classic way to
hand-write Source animation, and `SmdWriter` already emits the `skeleton` / `time N` / bone-row block
one needs — an afternoon's work. ModelDoc does not read SMD at all; its loader names FBX, DMX, OBJ and
VOX and nothing else. The mesh path learned that expensively once, and `DmxAnimWriter`'s header exists
so the animation path does not learn it again.

**Copied, not guessed — and there is a command that produces the thing to copy.** `fbx2dmx.exe` ships
in `bin/win64`, and its `-a` flag converts animation rather than geometry:

```sh
fbx2dmx.exe -a -i .../animations/face/Citizen@Eyes_Blink.fbx -o ref.dmx
```

Every element, attribute and spelling was read off that output for a shipping clip. What it settled,
none of it inferable from the element names:

- `animationList` hangs off the **root** DmElement, beside `skeleton` and `model` — not inside the
  DmeModel, which is where it looks like it belongs;
- a channel targets the bone's **DmeTransform, not its DmeJoint**, by id. Pointing it at the joint
  parses, loads, and animates nothing;
- each bone needs **two** channels, `_p` and `_o`, writing `position` and `orientation`. One channel
  carrying both does not exist;
- `mode` is 3 on every channel;
- the log layer's `curvetypes` array is **present and empty** — "no per-key curve override" — which is
  a different statement from omitting it.

The reference also carries an empty `compressed` blob per layer. An empty blob says nothing its
absence does not, and KeyValues2's binary literal is a multi-line quoted form with no second example
to check a guess against, so nothing is written rather than something invented.

`VmdlAnimation.AnimationList` puts `AnimFile` entries beside the existing `AnimBindPose` rather than
replacing it — every field copied, for the same reason the bind pose's were — so every skinned export
now goes through that path and an empty clip list is byte-identical to what `BindPoseList` always
wrote.

**How this is verified.** `DmxAnimTests` — 
the document is *parsed*, not substring-searched, for the same reason as `DmxGrammarTests`: a missing
comma, a bare id where the two-token reference form belongs, or a joint-targeted channel all produce a
file that reads fine and fails as "Couldn't load DMX file" with no line number. The suite checks that
channels reach DmeTransforms that exist and no DmeJoint, that times and values agree in length, that
duration counts intervals rather than frames, that the poses that went in come back out and are not
all frame zero, and that the writer is byte-deterministic. Part of the **2711-check** run.

**The engine has now passed judgement on the format, and it accepts it.** Two checks, both run:

`dmxconvert.exe -i out/sample_anim.dmx -o check.dmx` round-trips the clip to binary and the output
still names `animationList`, `DmeChannelsClip wave`, `root_p`, `DmeVector3Log`, `curvetypes` and
`DmeTransform`. That exit code means something: deleting ONE comma from the same file gives
`Expecting ',', didn't find it!` at the line it happened on, exit 127, and no output — so the clean
pass is a real answer rather than a tool that shrugs at anything.

`sample_anim.vmdl` then went through the actual asset compiler, and the resulting `.vmdl_c` contains
`wave`, `!embedded_sequence_data!.../sample_anim.vmdl`, `m_animArray`, `bindpose`, and BOTH bone
names. So ModelDoc accepts the AnimationList node, embeds the clip, keeps its name, and keeps the
skeleton. The only complaint was a missing `material_0.vmat`, which is the probe's material and not
the animation.

Method, since it is reusable: the editor's MCP server is reachable over plain HTTP at
`127.0.0.1:7269/mcp` with curl — `initialize`, then `tools/call` — so `asset_compile` and
`read_console` do not need an MCP client wired into the agent. The compile ran in whatever project
the editor has open, from a throwaway `Assets/models/effigy_probe/` folder that was deleted after.

**What is still unclicked is the EDITOR half** — the dialog, the picker, the menu item. Those need
the tool open in front of a person. WHAT-IS-LEFT §0 item 8 has the order to check them in.

### The clip export is wired to a menu

`EffigyAnimExport` + `EffigyAnimClipsWindow`. `DmxAnimWriter` was reachable only from the test suite
— kernel with no caller, which is half a feature. `File → Animation Clips...` picks `.riganim` files
and `Compile .vmdl` samples each one onto the exported skeleton, writes `anim_<name>.dmx` beside the
mesh, and lists it in the model's AnimationList.

**It does not author clips, and that is the design rather than a shortcut.** Effigy owns rig
authoring and binding; Marionette owns posing, keyframes, IK and constraints. So the dialog picks a
`.riganim` that already exists, and the only two things editable in it are the two that belong to the
export rather than to the animation: the name the clip has inside the model, and whether it loops.

**The bridge is in the editor layer, not the kernel**, because `RigAnimDocument`, `Transform` and
`Asset` are engine types and `Effigy/` references no engine type anywhere — a test enforces that. The
kernel keeps the part that needs no engine: `AnimClip` holds the poses, `DmxAnimWriter` writes them.

Four things that would each have failed quietly, all of them found by reading the types rather than
by running it:

- **The unkeyed-bone fallback is the bind pose, not zero.** `BoneTrack.Evaluate` answers
  `Transform.Zero` for a track with no keyframes, and a bone with no track has no Evaluate at all.
  The obvious loop poses every unkeyed bone at the origin and collapses everything nobody animated
  into a heap at the root.
- **The frame span comes from the last keyframe, not `FrameCount`.** That property defaults to 900 —
  thirty seconds, because a shorter timeline read as broken — so writing to it puts 900 frames of
  identical poses per bone per channel in a file whose last key is at frame 20.
- **The skeleton sampled against is the PIVOTED one**, the same one the mesh was written with. Using
  the unpivoted rig poses the model correctly against bones that have since moved, which is a
  whole-model offset that only appears once the clip plays.
- **A clip naming none of this rig's bones is refused rather than exported.** ModelDoc matches clips
  to bones by name and drops the misses silently, so the failure mode is a model that compiles
  perfectly and does not move. It is almost always a clip authored against a different rig.

`BuildSkinnedVmdl` grew one optional parameter and the no-clips path is unchanged rather than merely
equivalent — `VmdlAnimation.AnimationList()` with no clips is byte-identical to `BindPoseList()`, and
a test holds that.

**How this is verified, and how it is not.** The editor assembly **compiles** — `dotnet build
Editor/geppetto.editor.csproj`, 0 errors, no new warnings — and the kernel
suite is still **2711 checks, 0 failing**. The FORMAT this writes is now confirmed against the real
compiler (see the section above: dmxconvert round-trips it, and a compiled `.vmdl_c` carries the clip
under its name). What that does not cover is this file's own sampling and the dialog around it:
**nothing here has been clicked.**
The dialog has never been opened, no `.riganim` has been picked, and no model with a clip in it has
been through the compiler. This is WHAT-IS-LEFT §0 work of exactly the kind that file already
describes, and it is listed there.

### The rig panel says what is wrong with the rig

`RigDiagnostics.Check` had been correct and unshown since it was written. It now runs on every panel
refresh and every studio rebuild — most of its findings are about the studio rather than the
skeleton, so an ordinary CAD edit is exactly when they appear and disappear — and its problems are a
list under the inspector, coloured by severity the way a feature's diagnostic is, cause and remedy in
the tooltip, and a target button that selects the bone the problem names.

The list hides entirely when there is nothing to say: a permanently visible "0 problems" panel is one
people stop reading. And a rig with no bones is silence rather than a problem — "this model has no
skeleton" is true and is not news to somebody who has not placed a bone yet.

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

**Subdivide takes a face selection, and that form is linear.** `SubdivideFeature.Faces` is empty by
default and means the whole body, which is the Catmull-Clark it has always been. Pick faces and it
runs `CatmullClark.SubdivideFaces` instead: midpoints and centroids, no limit rules. This is not a
shortcut — the limit rules move every original vertex, so smoothing part of a mesh would drag the
untouched part with it through the vertices they share. Density where you need it has to leave the
shape alone. Faces bordering the selection are not subdivided but ARE stitched: the new midpoint is
inserted into their loop, so they become n-gons of the same shape rather than carrying a T-junction
against the split edge. `FeatureTests` asserts both — the box stays 2 units across, and every edge
still has exactly two faces.

**UVs are stored per face corner, not per vertex.** A box corner belongs to three faces that each
want a different UV for the same position; per-vertex UVs would force one value and smear the
texture across every seam. It also makes UV subdivision purely local, so seams survive subdivision
for free.

### Sculpt correspondence, frames, BVH, brushes

Kernel only, verified headlessly in `SculptTests`. No editor and no `SculptFeature` yet.

**Correspondence is a contract.** `CatmullClark.SubdivideOnce` sorts edge points by `(A, B)` rather
than dictionary insertion order, and `SubdivideWithMap` returns a `SubdivisionMap` naming each
output vertex as an original, an edge point, or a face point. Same cage twice is the same map;
reversing the face list does not shuffle the edge block. That is what a stored sculpt has to
survive a rebuild and a runtime upgrade.

**Deltas live in a derived local frame.** `SculptFrames.Build` makes a right-handed orthonormal
basis per vertex (normal from `ComputeVertexNormals()`, tangent toward the lowest-indexed neighbour)
plus a local edge length. `SculptLayer.Capture` / `Apply` convert through that frame. Capture-then-
apply is the identity. Uniform 2× scale of the cage keeps the bump on the new normal and the same
size relative to the cage; a 20% height edit keeps it on the surface.

**Spatial queries refit, they do not rebuild.** `MeshBVH` is an AABB tree over faces. Ray hits
agree with the linear `MeshRaycast` on point and distance (a ray through a shared vertex may name
either adjacent face — same `t`). Radius query returns exactly the brute-force vertex set. After a
pull, `Refit` updates boxes without touching tree structure, and both queries stay correct.

**Brushes consume strokes, not a mouse.** `Brush.Apply` takes a `BrushStroke` of samples. Smooth,
Draw, Inflate, Grab, Flatten, Pinch. Grab at zero strength is the identity. Smooth strictly reduces
Laplacian energy. Mirror-X produces a mesh symmetric across X. Undo stores only the vertices the
stroke moved. Eight Smooth samples on 1538 verts finish in well under two seconds.

### Multires levels

`MultiresSculpt` — kernel only, verified headlessly in `SculptTests`. Still no editor and no
`SculptFeature`.

**One rule, and the rest is bookkeeping around it: level N+1's rest surface is the subdivision of
level N *displaced*, not of level N at rest.** That is what lets a coarse edit carry fine detail.
Sculpt a bump at L3, go back to L1 and lift the top half: L1's deltas move the surface L2 and L3 are
subdivided from, so the bump rides the lift instead of being flattened by it. Subdividing the rest
mesh instead produces a perfectly plausible model that silently cannot do this, so the rule is
checked directly — `Rest(2)` is compared against the subdivision of the displaced L1 *and* against
the subdivision of the flat one — rather than inferred from the behaviour it causes.

Level 0 is the cage and always exists, so a coarse edit needs no level added first. `ViewLevel` is
display only: dropping to L1 and returning to L3 leaves the model bit-identical, and the L3 deltas
are never touched. `RemoveTopLevel` is the only call that destroys anything and it hands the dropped
layer back so the caller can undo it.

`SetCage` re-bases the whole stack on a rebuilt cage — every level re-derives its frames and every
delta rides the edit. It **refuses** a cage whose topology changed rather than misapplying the deltas
or silently dropping them, and `CanRebase` gives the reason with both models' numbers. The refusal
catches the case a count check alone waves through: same vertex and face counts, different wiring.
`TopologyId` is the stable id persistence (step 6) will store beside the deltas — counts and face
indices, deliberately not positions, since positions are exactly what a parametric edit changes.

`Stroke` and `Undo` bridge the brushes to the levels, and exist to get two sets of frames the right
way round: a brush works on the **displaced** mesh's frames, a delta is captured through the **rest**
mesh's. Swapping them is invisible on a fresh level — the two are the same mesh until something is
sculpted there — and wrong on every level after that. Only Inflate reads the frames at all, which is
what makes the bug easy to leave in and is why there is a test that tilts a level first and then
checks which surface the stroke followed.

The cache of rest surfaces is an optimisation and is not observable: a missed invalidation shows up
as a low-level edit that fails to move the detail above it, and there is a check for exactly that.

Verified by mutation, not just by the suite being green: subdividing the rest mesh instead of the
displaced one fails 4 checks, dropping the cache invalidation in `Record` fails 2, and handing the
brush the rest frames fails 1.

### The rig says what is wrong with it

`RigDiagnostics.Check` finds what the exporter and the compiler would otherwise report as something
else: an unweighted vertex arrives as a vertex that does not move, a zero-length bone as a bone with
no orientation, an assignment pointing at a deleted body as nothing at all. Every one is knowable
while the numbers that caused it are still to hand.

Errors and warnings, the same split features make — one error outranks any number of warnings, so a
panel can colour its header from `Worst`. `SkinWeights.Validate`'s output is passed through verbatim
rather than reworded, because it already names the vertex and the number.

**It deliberately overlaps what `Skeleton` already refuses.** AddBone will not take a duplicate name,
AddBoneFromPoints will not make a zero-length bone, RenameBone will not rename onto a collision — so
a rig built through that API cannot reach most of these. What can: the `Bone` fields are public and
get written directly, and a skeleton read back off disk has been through none of those constructors.
Both facts were found by the tests refusing to build the broken fixtures.

The panel does not show them yet.

### A mouth that lands across two faces

`MeshHoleRepairSpan`. The single-face repair wants one coplanar face containing the whole loop and is
right to — a guess there seals a surface the wrong way — so a cut landing where two coplanar faces
meet was declined and the opening stayed open. The fix is a DETOUR, not a patch: each face keeps its
own boundary and detours around its half of the mouth where that boundary runs along the shared edge.

**The check that made it trustworthy is area.** Splicing the arc in the wrong direction produces a
bow-tie that keeps its vertex count, keeps zero boundary edges, and whose Newell normal still points
the right way — so boundary count, manifoldness, validity and even enclosed volume all waved it
through. Only the face's area does not survive: 19 against the 13 a notched half-lid must have.

### A mouth across any number of faces, and one across a ridge

`MeshHoleRepairCurved`. The span repair above wants exactly two coplanar faces; a cut through a
CURVED surface has neither. Its mouth is not planar at all, so there is no loop normal, no containing
face, and no shared basis to test containment in — every argument the first two repairs are built on
stops being available.

**The answer is read off the wall, and that is why it survives.** Every loop edge is used by exactly
one face — the tunnel wall — and two faces sharing an edge traverse it in opposite directions. So the
surface's own boundary must run along each arc the OTHER way from the wall, and a region that walks
one of its arcs the wall's way is on the void side of it. No plane appears anywhere in that.

Each face gets the arc of the loop lying in it and is re-partitioned by its arcs, which are chords
with both ends on its boundary. A face can get more than one: the middle quad of a cylinder wall has
the hole passing through it top and bottom, and the strip between those two arcs is hole rather than
material.

**The bug that was there before the tests were:** materiality cannot be carried down the chord
recursion. Marking the half that walks a chord the wall's way as void and letting its children inherit
that is the obvious implementation and it is wrong — that half is not a region yet. On a mouth across
three strips the first chord cuts off the top strip and leaves "the mouth plus the bottom strip" as one
lump; the second chord separates them, and the bottom strip is material despite having come out of the
lump. Every finished ring is asked directly instead.

### A second cut into a face the first repair already took apart

`MeshHoleRepairFragment`. Not hard because the cuts overlap — hard because the single-face repair
TRIANGULATES the face it fixes, so the next mouth to land there crosses a dozen coplanar fragments at
ordinary points on their edges rather than at vertices. All three earlier passes decline that, and
correctly: naming a crossing that is not a vertex means splitting a face the repair was not asked to
touch.

So it stops treating the fragments as faces. They are one surface a previous repair left in pieces,
and the mouth is a hole in that SURFACE — so the whole coplanar group is taken as one region, its own
outer contour and existing holes are derived from the edges only one of its faces uses, and the mouth
goes in as one more hole. Nothing has to be crossed, because the mouth crosses nothing.

Ordered LAST for a reason: rebuilding the group throws away the partition the fragments had, and the
earlier repairs preserve theirs. Both new passes also measure the mesh before and after every loop and
roll back a repair that did not strictly reduce the open boundary — splitting polygons by chords and
deciding materiality from a winding is too much machinery to run on trust.

### A cut is allowed to sever a part, and the part list now knows

`MeshSplit`, `Feature.SeparatePieces`. Drill a slot across a bar and the boolean returns one mesh
holding two blocks that touch nowhere. Nothing was wrong with the mesh; what was wrong is that a Body
is assumed to be one solid by everything that reads it — the parts list showed one part where the
screen showed two, hiding one hid both, and the collision builder wrapped a single convex hull around
the pair, filling in the gap the cut had just made.

Two decisions that are not details:

- **Connected means sharing a VERTEX, not an edge.** Two blocks joined at one corner are one solid by
  the vertex rule and two by the edge rule, and the vertex rule can only ever split things genuinely
  apart. Splitting a part somebody thinks of as one part renames bodies underneath them.
- **The order the pieces come back in is a promise** — largest volume first, ties broken on the
  minimum corner. The original body keeps its id and its largest piece, because every sketch and
  picked face hanging off that part is holding that id. Volume alone is not enough: a symmetric part
  cut down the middle gives two pieces whose float volumes do not reliably compare the same way twice.

### Collision reaches the .vmdl, and the schema was measured

`VmdlPhysics`. `CollisionBuilder`'s shapes had been correct and tested for a while with nowhere to go,
because writing them meant guessing ModelDoc's KV3 and a guessed node fails as a model that will not
load. Every key was probed instead: written into a .vmdl, compiled by the engine, and read back off
the compiled model's own physics bounds.

What that answered, none of which was readable anywhere: `PhysicsShapeBox.dimensions` is the FULL size
and it is placed by `origin` — `center`, `translation` and `position` all compile on a box and are
ignored. `PhysicsShapeSphere` is placed by `center`, which is not the box's key. `PhysicsShapeHull`
takes `hull_vertices` in model space, exactly.

**And it exposed an older bug.** ModelDoc's OBJ importer turns the mesh: a bar written along +x
compiles lying along +y. Harmless while the .vmdl carried no collision — the part was a quarter turn
from how it was drawn — and not harmless at all once shapes go in, since those are in the file's own
coordinates. `import_rotation = [ 0, -90, 0 ]` cancels it, and both signs were tried: +90 put the mesh
at x = -10..0. The two-box sample now compiles to `Bounds 13 x 4 x 4` and `PhysicsBounds 13 x 4 x 4`
— the mesh where it was drawn, and the collision on it rather than beside it.

### The UV packer tries four arrangements

Charts have no up, so turning one costs nothing — but "turn every tall chart" is too blunt a rule to
trust: measured, it gains 21 points on a long box and LOSES 7 on a cylinder. Packing is cheap, so all
four arrangements are packed and the one that fits at the largest scale wins.

Doing that surfaced a bug worth recording: **`List.Sort` is not stable**, and sorting the charts twice
— once to measure a scale, once to commit it — put them in two different orders, so the commit packed
at a scale measured against a different arrangement and the islands landed ON TOP of each other. The
tell was coverage coming out HIGHER than any single policy could achieve. The ordering is total now.
Coverage runs 45–67%; a real bin packer is the remaining improvement.

### Removing a sculpt level can be undone, so it is offered

`RemoveTopLevel` sat in the kernel unexposed because nothing could undo it, and a destructive button
with no way back is one nobody should be given. `RestoreTopLevel` is the other half; the session
holds the dropped layer as an undo entry, and the editor's "coarser" button removes the finest level
only when it is EMPTY of detail.

### The CAD gaps that were named as absent

**`UVUnwrap` is the one that mattered.** Box and planar projection both overlap by construction on a
closed solid — box projection maps +X and −X onto the same square on purpose, because it is built for
tiling a texture across a wall. Until this existed `NormalBake.Measure` correctly refused every model
the tool could make, and the whole sculpt pipeline could only pay off on a hand-UV'd plane. Chart by
normal, flatten each chart onto its average, pack with one shared scale so texel density is uniform.
Shelf packing reaches about 45% of the square on a sphere; rotating a chart when it packs better on
its side, and a real bin packer, are the two things that would raise it.

**`DraftOperation`** tapers faces of a solid that already exists — extrude's Taper covers a face being
made, which by the time you need draft is twenty features back. **`HoleOperation`** builds simple,
counterbore and countersink negatives and hands them to the boolean. **`CollisionBuilder`** reads the
feature history: a model built out of boxes IS its own collision, and anything a primitive cannot
describe falls back to a convex hull per body, naming what spoiled it.

**Revolve works on the first press now.** Its axis ran through the sketch origin, which is where
people draw, so the first press on a normal sketch reliably refused — correct, and a terrible first
impression. An Axis dropdown offers the profile's own edges, which are tangent to it and therefore
always legal. The kernel default stays the typed axis and that is deliberate: a ChoiceParam
serialises its INDEX, so a document saved before the dropdown existed loads on index 0, and if index
0 were an edge mode every revolve in every saved file would quietly move on the next open. The editor
sets the friendly mode when it creates one.

**Four bugs the tests caught that reading would not have**, all of the same kind — plausible output
that is wrong:

- a drafted corner belongs to two walls and leans by only its component of the average, so a part
  comes out **under-drafted**, which is the one failure a mould angle exists to prevent
- the third of LoopOffset's three checks, *no edge reversed*, was named in a comment and never
  written; without it a drafted box folds into a bow-tie that keeps its area and its Newell normal
- a countersink reached its head diameter a third of a unit ABOVE the part, so the hole was narrower
  than asked for at the only place anybody measures it
- the bake's rasteriser used a tolerance, so a texel centre on the edge between two coplanar faces
  satisfied BOTH — `Measure` reported overlapping UVs on a mesh whose UVs were perfect. A top-left
  fill rule is the exact answer; a tolerance is the one that invites tuning the threshold.

**And one the bake itself was wrong about.** Its search range was a tenth of the model's diagonal,
which is nothing like enough when the cage IS a coarse box and the sculpt IS its subdivision — they
sit 2.6 units apart on a 2x2x2 box against a range of 0.35, so three quarters of the map came back
flat. The range is measured off the two surfaces now. Worth knowing regardless: **a bake wants a cage
that hugs its sculpt**, and a coarse box does not.

### The sculpt feature, and where its deltas live

`SculptFeature` consumes one body the way `ShellFeature` does and replaces its mesh with the sculpted
one, so export, rigging and the boolean never learn a sculpt happened. It goes on the cage **in place
of a Subdivide** — the levels are the subdivision.

**It outputs the top level, always.** `ViewLevel` is an editing convenience and deliberately does not
reach the model, because dropping to L1 to work coarsely must not quietly export an L1 model. Blender
draws the same line as separate viewport and render levels.

**A parametric edit carries the sculpt, and a topology change is refused rather than absorbed.**
Making the box taller rebuilds with the sculpt still on the surface; turning it into a cylinder is an
error with a cause and two remedies, the deltas are **kept**, and undoing the edit restores the sculpt
exactly. That last check is the point of keeping them — a refusal that threw the deltas away would
make one wrong click unrecoverable.

**The deltas are not in the .effigy file.** `StudioDocument` saves public fields by reflection, so the
sculpt is a private field and persistence goes through `SculptBlob` and `SculptSidecar` instead: one
directory beside the document, one blob per feature, keyed by feature id so re-ordering the history
cannot shuffle a sculpt onto the wrong feature. Saving never deletes a blob it did not write — that is
the cheapest undo of "I deleted the feature and saved" — and `Prune` has to be asked for by name.
The reflection sweep in `DocumentTests` cannot cover this one, so there is an end-to-end test that
writes a sculpted studio, reads it back, rebuilds it and compares every vertex.

**16 bits per component against a per-level bounding box**, six bytes a vertex, which is the budget
the plan was written against: ~750 KB per level at L4 on a 500-face cage. A level nobody touched comes
back **exactly** zero rather than nearly zero, so a model saved and reloaded twenty times does not
drift away from its cage a hundredth at a time. Blobs are refused by magic, by version, and by cage —
including a cage with the same vertex and face counts but different wiring.

Verified by mutation: spending 8 bits of the 16 fails the precision check and the end-to-end reload;
silently restarting the sculpt on a changed cage fails the refusal checks.

### The sculpt tool, with no cursor in it

`SculptSession` is step 7's kernel half. The editor cannot be compiled outside s&box, so anything
living there is verified by reading it — which is how the silent-no-op bug survived a day looking
like three unrelated UI faults. Everything between the pointer and the mesh is arithmetic, so it
lives here and is tested headlessly, the same argument `EditorFlowTests` was written on. What is left
for the s&box half is thin: hand it rays, draw `DisplayMesh`, draw a ring at `Hover`.

**A stroke works on a mesh, not on the level stack.** `BeginStroke` evaluates once and brushes a
working copy the viewport draws live; `Record` is called exactly once, at `EndStroke`. Recording per
sample would be correct and unusable at L3. The test for it is that a nine-sample stroke is **one**
revision and one undo entry — which is also what a user means by "undo".

**Sample coalescing, both directions.** A pointer reports far faster than a brush needs: without
spacing, holding still would bite harder the longer you hovered and a slow drag would cut deeper than
a quick one for the same gesture. And a drag that outruns the sampling gets the gap filled along the
straight line between reports, capped so one flick across the model cannot stall the tool. Ten reports
from a still pointer produce no samples and do not move the mesh; one big jump becomes several and
sculpts the middle of the path.

**Frames are built once per stroke, from the surface the stroke found.** Rebuilding them per dab is
the obvious way to make the tool unusable, and a brush that re-reads its own output feeds back — an
Inflate would run away following normals it had just moved.

The rest is what a tool needs to not feel broken: a click leaves a mark rather than waiting for a
move, clicking past the model starts nothing, dragging off the silhouette and back keeps the stroke
alive, cancelling leaves the model alone, and `DisplayMesh` is cached against the revision because a
viewport asks every frame. Undo and redo are sparse and symmetric — `SculptEdit` holds before and
after for the vertices the stroke touched, not two copies of a 128k-vertex level.

Verified by mutation: removing the spacing check, collapsing the interpolation to one sample, and
absorbing stroke undos latest-first each fail the checks written for them. That last one is the
subtle one — a vertex moved by three dabs has to go back to where it was before the first.

### Baking the sculpt down onto the cage

`NormalBake` is where the pipeline pays off: cage + sculpted mesh + the cage's UVs in, a tangent-space
normal map out, so the model that ships is the cage — a few hundred faces, already unwrapped, already
rigged — wearing the detail of something a thousand times heavier. Tangent-space rather than
object-space because the cage deforms with the rig.

For each texel: find the cage point that owns it, fire a ray along the cage normal from outside in,
hit the sculpt, and write the difference as a direction in the cage's own tangent frame. The frame is
orthonormalised per texel against the interpolated normal rather than per triangle, or the map facets.

**Mirrored UVs are handled, and are the case worth knowing about.** Half a character is usually the
other half flipped, and this tool has a Mirror feature. A mirrored island's tangent runs backwards, so
the frame's handedness is read off the UV winding; get it wrong and that island's green channel comes
out inverted, which lights every bump on one side of the model as a dent. There is a two-quad fixture
with one island mirrored, because an unmirrored fixture never takes the branch — which is exactly what
happened: the first version of the correction survived a mutation test until that fixture existed.

**`Measure` is the check the plan has been asking for since it was written.** UVs must not overlap; a
bake over overlapping UVs does not fail, it produces a plausible map that is wrong wherever two faces
shared a texel. It reports coverage, overlapping texels and faces outside the 0-1 square, and it names
box projection — the tool's own default — as unbakeable, which it is: it tiles on purpose, which is
right for a wall and wrong for a bake.

Edges bleed outward by four texels so seams do not glow once mipmaps mix in what sits outside the
island, and padded texels are not counted as measured.

**Two conventions are still coin flips and must be settled on screen**: the green channel's sign
(`BakeOptions.FlipGreen`) and which end of the image v = 0 lands at. Neither is visible in a thumbnail
and both light a model exactly as wrongly as the other. `Effigy.Tests/out/sample_normal_bake.png` is
written by the suite for that purpose — a flat lilac sheet with a dome in the middle, pink to the
right of centre, cyan below it.

**How faceting is tested, because the obvious way does not work.** A map baked from face normals rather
than interpolated ones is faceted, and passes every other check here. An absolute threshold on the step
between neighbouring texels cannot catch it, because a smooth map of a steep dome has a large step too.
What separates them is behaviour under resolution: a smooth bake samples a continuous function and
doubling the map halves the step (0.086 → 0.048 → 0.024 at 64/128/256), while a faceted one is stuck to
the source geometry and does not improve at all (0.3207 at both 64 and 128). That is the check.

### Masking, and reprojection onto a cage it was not made on

**`SculptMask` is one float per vertex, 1 meaning "brush me normally".** The sense is that way round
because `Brush` already multiplies by it, so an all-ones mask is the same as no mask and a fresh one
changes nothing; storing "how protected" instead would invert every brush in the tool the moment a
mask existed. Painting reuses the session's own spacing and gap-filling, so a mask stroke behaves
like any other stroke, and it is undoable as a sparse diff rather than a copy of the array. Hide-by-
mask drops a face only when EVERY corner is protected — dropping it on one corner would eat the
boundary of every mask, so the visible edge would creep inward each time it was used. Masks are
per level and deliberately **not persisted**: a mask is the sculpting equivalent of a selection.

**`SculptReprojection` is the last resort, and it is meant to be.** A cage whose topology changed has
no vertex to put deltas on, and refusing is right nearly always — the usual cause is an edit nobody
meant, and undoing it brings the sculpt back exactly. Reprojection is for the other case: raycast the
new dense surface against the old sculpted one and re-derive from the hits. `SculptFeature.Reproject`
is a `BoolParam` defaulting to **false**, because reprojection is lossy and cannot be undone by
undoing the upstream edit — the original deltas are gone once it runs. When it does run the feature
**warns** rather than saying nothing, and the warning names what was lost: detail finer than the new
cage, and the level structure, which all collapses into the top level.

`ReprojectionReport.Coverage` is what tells a caller the two shapes had nothing to do with each
other. Worth knowing: a small search radius is NOT the same test as an unrelated cage — two surfaces
sitting on each other still hit at any radius at all, correctly.

### The sculpt is in the editor

**It compiles in s&box** — confirmed, not inferred — and has **never been seen on screen**, which
is the category [WHAT-IS-LEFT.md](WHAT-IS-LEFT.md) uses for editor work and the honest one for all
of it. Compiling is not behaving.

- **A Sculpt button on the feature strip**, next to Subdivide because it replaces it: the levels ARE
  the subdivision, and a Subdivide underneath would hand the sculpt a dense mesh as its cage.
- **Sculpt mode**, entered from the feature tree's context menu, swapping in a third floating strip
  where the feature and sketch strips already trade places. Six brushes, mask, symmetry, level down
  and up, bake, finish.
- **`EffigySculptBar`**, a second row with radius and strength as expression fields and a readout
  saying what the level costs and — when the view is below the top — that the model still builds at
  the top. Levels are exponential; a level control with no number beside it is a way to hang the
  editor politely.
- **`EffigyViewport.Sculpting.cs`** is deliberately the thinnest part: convert Vector3 to Vec3, call
  four session methods, draw a ring. The ring lies in the SURFACE'S plane rather than facing the
  camera, because the radius is in world units along the surface and a screen-facing circle on a
  face turned away covers far more of the model than it claims.
- **The bake writes a PNG**, through the new kernel `PngWriter`, and checks the UVs with
  `NormalBake.Measure` BEFORE writing anything.
- **Eleven hand-drawn glyphs** in the existing idiom, each drawn as what the brush DOES to a surface
  rather than as a tool shape — six brush heads with a small badge each is six identical blobs at
  27px.

**Saving a sculpt saves it.** `SculptSidecar` is called from `WriteDocument` and `LoadDocument` —
worth stating because for a while it was not, and a side-car nothing calls is a save that looks like
it worked and loses the sculpt. Loading happens BEFORE the first rebuild, which is when the deltas
are consumed. A failure to write the blobs is logged as an error rather than swallowed.

**Ctrl+Z reaches the stroke.** Sculpt mode owns undo outright while it is open and does NOT fall
through to the studio's undo when its own stack is empty: the studio's restores a feature list, and a
snapshot from before the sculpt feature existed would leave the live session holding a feature the
studio no longer has.

**The mask actions that are not brush strokes** — invert, clear, paint/erase, hide-masked — are in
the Edit menu rather than on the strip, because four more hand-painted glyphs is real design work for
things nobody reaches for mid-stroke. `HideMasked` is a view, like `ViewLevel`, and reaches the model
exactly as far as that one does: nowhere.

**The bake's two conventions are controls, not constants.** Green channel and V orientation are the
two things the suite cannot judge, and the sitting exists to settle them — a bake button that could
write only one of the four combinations would make that sitting impossible to finish. The size cycles
too, and the prompt names which convention was used, because two files differing only in the sign of
one channel are indistinguishable once they are on disk.

**Only one full rebuild happens in sculpt mode, on finish.** Rebuilding the tree per stroke would be
slow and wrong to look at: the tree builds the TOP level while the viewport may be showing a coarser
one. Strokes refresh the viewport straight from the session and mark the document unsaved; the tree
catches up when you leave.

**It compiles in s&box.** It took one fix to get there — a missing `using Editor;` in
`EffigyViewport.Sculpting.cs` — and that fix is worth recording because of what failed to catch it.

Compiling the editor sources against the kernel with the s&box assemblies ABSENT is a genuinely
useful check: it proves every *Effigy* type and member the editor calls resolves, which is where a
blind kernel-API mistake would show. What it structurally cannot see is a missing `using` for an
s&box namespace, because an unimported type and an unreferenced assembly are both CS0246 — so one
forgotten directive sat inside 920 identical errors and read as normal. The lint that catches it is
"every file under Editor/ naming a type from the Editor namespace must import it", and it is the
first thing to run after writing editor code that cannot be compiled locally.

Compiling is still not behaving. The s&box widget API doing the RIGHT thing is unproven, and that is
what the sitting is for.

### A feature can say its own cache is stale

`Feature.IsStale` is new, and `PartStudio.Rebuild` asks every reusable feature before it trusts the
snapshot. Everywhere else the convention is that whoever edits a feature calls `MarkDirty`, which is
one call in one place for a dialog full of numbers. It does not hold for a feature whose state is a
live object someone else is mutating: a brush changes `MultiresSculpt` hundreds of times a stroke,
nowhere near the code that owns the studio. `MultiresSculpt.Revision` counts model-changing edits
(`ViewLevel` deliberately does not bump it) and `SculptFeature` compares it against the revision it
last built at.

This was found rather than designed: the first run of the feature tests returned the 8-vertex cage
from cache after a sculpt, which in the editor would have read as "the sculpt tool does nothing"
rather than as a caching bug. Removing the check in `Rebuild` still fails those tests.

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
detached fragment, and a face wound backwards. The chamfer bug is what prompted it. The tests damage
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
  **feature context menu** (rename, suppress, roll-to-here, delete). The **parts list** has the
  same kind of menu: rename, edit the feature that made it, hide/show, isolate, delete. Names and
  hide flags are keyed by body id and survive rebuild and save.
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
  plumb bob, Revolve draws the turned *shape* rather than the operation, Chamfer is a filled block
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
- **The Materials dock is a material browser you drag out of** (`EffigyMaterialsPanel.cs`) — the
  project's `.vmat` files **in their folders**, as a grid of real asset thumbnails. Drag a cell onto
  a face and that face wears the material; double-click one and the whole part does, which is the
  one binding a drag deliberately cannot make.
  - **It replaced a column of slot rows** — "Slot 3 · material_3 (default) · \[Browse...\] · \[×\]",
    eight of them — and the reason is worth keeping: that panel made you start from a *number*. To
    put brushed steel on something you picked a slot you had no opinion about, opened a modal
    picker, found the material, closed it, then went and painted faces. Seven eighths of the dock
    was permanently a list of names of things that did not exist yet, and the materials themselves,
    the only part with a picture, were never on screen at all.
  - **Then it replaced a flat alphabetical grid, which is why it navigates.** This project can see
    **1248 materials, 363 of them its own**, and most are
    `materials/halo/characters/<something>/halo_0…halo_12.vmat` — hundreds of cells all named
    `halo_3`. The folder is where the meaning is: `elite` tells you what you are looking at and
    `halo_3` tells you nothing, so a listing that discards the folders and sorts the leaves
    alphabetically is worse than no listing. You land in `materials`, folders come first with a
    count each, double-click descends, and a path bar walks you back out. Search is recursive **from
    where you are standing** and matches the whole relative path, so "elite" finds the twelve
    materials in that folder, none of which are called that. A scope button toggles project-only —
    the default, because 363 is the number you want and 1248 is engine and mounted content.
  - The path bar is **one hand-painted widget with no child buttons**, deliberately: a row of
    buttons rebuilt on navigation means rebuilding the row from inside the `Clicked` callback of one
    of the buttons being deleted — the same hazard the old panel documented about its `×`.
  - **The slot did not go away, it stopped being the question.** A material the document uses wears
    its slot number as a badge, *in that slot's viewport tint* (`EffigyViewport.SlotColor`) — so the
    green patch on the model and the green badge on the material are visibly the same fact. The
    footer counts bound slots, which is what the old list was genuinely good for. Right-click a cell
    to bind it to a specific slot by hand or unbind it, which is everything the eight rows could do,
    reached from the material rather than from a number.
  - The drag carries the asset's `RelativePath` as `Drag.Data.Text`, the same payload the editor's
    own asset list sets — so a material dragged from *here* lands anywhere in the editor that takes
    one, and a material dragged from the *real* asset browser lands on an Effigy face.
  - The drop cannot borrow the cursor ray every other pick in the viewport uses: the canvas does not
    report itself under the mouse during a drag, so `CaptureCursorRay` is still holding the ray from
    wherever the drag began. It builds one from the drop position instead — canvas-local, scaled by
    `DpiScale`, because the camera renders at physical pixels while Qt reports logical ones.
  - **Which slot it lands on is the whole problem**, and it is in the kernel where it can be tested
    (`Effigy/Features/MaterialDrop.cs`, `Effigy.Tests/MaterialDropTests.cs`). One slot per material,
    reused — thirty faces of one material make one slot, not thirty. Slots that are *painted but
    unnamed* are skipped, or a drop would silently repaint faces somebody had put on slot 3 by hand.
    Slot 0 is never allocated, because it is what every unpainted face is on and handing it to a drop
    would paint the entire part; the double-click is what deliberately reaches it. Dropping a
    material onto the face already wearing it reports *no change*, so a near-miss does not put a
    do-nothing step on the undo stack.
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
| **`bin/win64/fbx2dmx.exe` converts any FBX to a DMX the compiler loads, and `dmxconvert.exe` reads a DMX and names the first thing wrong with it — with a line number, in about a second.** Together they are a reference file and a validator for the rigged export, with no editor involved | run against `lightswitch_plate.fbx` and `fp_arms.fbx` |
| A `DmeVertexData` field is named **`<semantic>$<set>`** — `position$0`, `normal$0`, `texcoord$0`, `blendweights$0`, `blendindices$0` — and its index array is that name plus `Indices`. The plural spellings (`positions`, `jointWeights`, …) are also strings in `modeldoc_utils.dll` but are **not** what a vertex format is keyed on; a file using them compiles to "Missing position values" | `fbx2dmx` output, then run |
| `blendweights$0`/`blendindices$0` carry **no index array** — they are `jointCount` entries per position, indexed by the position index. `fp_arms`: 260 positions, 260 blendweights at jointCount 1, against 944 face corners | same |
| In KeyValues2, **every element_array member takes a trailing comma, nested elements included**, and a reference is the two tokens `"element" "<id>"` — a bare quoted id is read as an element *type* name | same |
| `PolygonMesh.PerformBoolean` mutates its receiver; the relative transform places the second mesh against the first; UVs must be recomputed after | `BooleanTool.cs` + reflection dump, then run |
| **`PhysicsShapeBox.dimensions` is the box's FULL size**, not its half-extents, and the box is placed by **`origin`** — `center`, `translation` and `position` all compile on it and are silently ignored. `angles` works | probed: written, compiled, physics bounds read back |
| **`PhysicsShapeSphere` is placed by `center`**, and by nothing else — the one shape whose placement key is not the box's | same |
| `PhysicsShapeCapsule` and `PhysicsShapeCylinder` take `radius` plus `point0`/`point1`, and the points are where they go — no separate placement key | `citizen_physicsshapelist.vmdl_prefab`, then probed |
| **`PhysicsShapeHull.hull_vertices` takes points in MODEL space**, exactly — a hull written from a 20-unit cube offset along x measured 20 across, so nothing is re-centred underneath | probed |
| `parent_bone`, `surface_prop` and `collision_tags` are the base keys every physics shape carries | `citizen_physicsshapelist.vmdl_prefab` |
| **ModelDoc's OBJ importer turns the mesh a quarter turn**: a bar written along +x compiles lying along +y. `import_rotation = [ 0, -90, 0 ]` cancels it — both signs were tried, and +90 put the mesh at x = -10..0. Physics shapes are NOT turned, so without this they sit at ninety degrees to the model | probed with a one-sided bar and a matching PhysicsShapeBox, unioned |
| A `PhysicsShapeList` with no children is a model that declares collision and has none — write no node at all instead | reasoned from the above, and why `VmdlPhysics.ShapeList` returns "" |
| **ModelDoc prunes any bone that is neither weighted nor animated, and a `BoneMarkupList` with `do_not_discard` is what stops it.** A two-bone sample .vmdl compiled to ONE bone without it and both bones with it | probed: compiled, then `rig_test_follow <model> __list__` |
| `AnimBindPose` lives inside an `AnimationList` (which also carries `default_root_bone_name`), and the node's fields are copyable whole off `first_person_arms_preview.vmdl`, which ships as source | that file, then compiled |
| A compiled model's bone names can be read back with this project's own `rig_test_follow <model> <bone>` - naming a bone that does not exist makes it list every bone that does | run |
| s&box ships classic `MaterialIcons-Regular.ttf`, **not** Material Symbols — a Symbols name renders as nothing | `RigIconButton` class comment |
