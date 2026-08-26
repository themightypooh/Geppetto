# How other CAD tools solve the problems Effigy has

Notes taken by reading the source of two established open-source parametric CAD systems, checked
against the specific things Effigy is currently missing or unsure about.

## Read this first — licensing

**Solvespace is GPL. FreeCAD is LGPL 2.1. Marionette is MIT.**

Nothing here is copied code and nothing here may become copied code. GPL source cannot be lifted
into an MIT project, and this repo already carries one licensing scar over exactly that
(`Editor/HaloMount` and its GPL binaries — see `HANDOFF.md`). What follows is *how they approach
the problem*, written from reading, so Effigy's own implementation can be written independently.
Architecture and approach are not copyrightable; expression is. Keep it that way.

Both were read at a shallow clone of `main`, August 2026.

---

## 1. Sketching on an existing face

**The thing to understand: neither of them treats this as a sketching mode.** It is a *derived
plane*, and the sketch then works exactly as it always does.

- **Solvespace** has workplane groups — `WORKPLANE_BY_POINT_NORMAL`, `WORKPLANE_BY_LINE_SEGMENTS`.
  A sketch group carries an `activeWorkplane` handle. Notably there are commented-out enum values
  for `WORKPLANE_BY_POINT_FACE` and `WORKPLANE_BY_FACE`, so even there, picking a face directly was
  considered and not shipped.
- **FreeCAD** has an *Attacher*: a datum or sketch holds `AttachmentSupport` (what it is attached
  to) plus a `MapMode` (how to derive a placement from it), and recomputes its placement from that
  reference on every rebuild. `PartDesign::Plane` is a datum plane produced this way.

**For Effigy.** `SketchPlane` is already exactly the right shape for this — an origin and two axes.
Today `SketchFeature.Plane` is a `ChoiceParam` over three fixed planes. The move is to let it
resolve from a *source* instead:

- keep the three global planes as the default source,
- add "offset from a plane" (`SketchPlane.Offset` already exists),
- add "a planar face of a body", which computes origin + axes from that face's plane.

That is a derived-plane feature, not a new sketcher. The sketcher does not change at all.

## 2. Cut versus boss

- **FreeCAD**: `FeatureAddSub` with `enum Type { Additive, Subtractive }`. Pad and Pocket are the
  *same feature* with that flag; so are Revolution/Groove. The feature produces a shape and the
  body combines it in.
- **Solvespace**: the group carries `meshCombine`, one of
  `CombineAs { UNION, DIFFERENCE, ASSEMBLE, INTERSECTION }`.

Both put the operation **on the feature**, not in a separate boolean feature.

**For Effigy.** `ExtrudeFeature` should gain an operation parameter — New body / Add / Subtract —
rather than a separate boolean feature being invented. The parameter is trivial; the work is
entirely the mesh boolean underneath it, which Effigy does not have.

**Built, and here is the part that did not need the boolean.** `Result` now sits on both
sketch-consuming features, with New body and Add — and Add merges the meshes rather than unioning
them, which is enough for the thing that was actually broken: every extrude made its own body, so
building a part up out of four extrudes listed four parts. The interface between them is left
uncut, which costs manifoldness along the join and nothing else that matters at this stage.
Remove is offered as well, and it is the honest kind: subtracting genuinely cannot be faked this way
— there is no "combine and leave the interface" answer to taking material away — so it goes through
a boolean provider (`MeshBoolean`) and says plainly when none is installed rather than producing
something plausible.

The default is neither New nor Add but **Auto**, which reads the sketch's attachment: on a face of a
body it adds to that body, on a global plane it starts a new one. That is the same information
FreeCAD's Attacher carries and Solvespace's workplane groups carry, used for a second purpose.

That boolean is the real cost and should not be underestimated: it is the single hardest thing on
Effigy's roadmap, and both of these projects lean on decades of work for it (Solvespace has its own
mesh/shell boolean; FreeCAD uses Open CASCADE). Effigy's own `ShellOperation` and `Bevel` are much
smaller problems than a robust CSG.

## 3. Referring to geometry that gets rebuilt — where Effigy is ahead

FreeCAD's `PropertyLinkSub` stores a reference to a sub-element **by name**, as a string like
`"Face6"`. Those names come from the shape's element ordering, and that ordering changes when
anything upstream changes. This is the *topological naming problem*, and it is the best-known
long-running defect in FreeCAD: a pocket attached to `Face6` silently moves to a different face
after an unrelated edit upstream.

**Effigy already avoids this**, by accident of a decision made for other reasons.
`SketchConsumingFeature.RegionSeed` is a *point inside the region* rather than an index into a
list, precisely because profiles are re-found from the curve graph on every rebuild and their
order is whatever the walk discovers. A point survives any edit that does not destroy the region.

**Keep that principle when faces become referenceable.** A face reference should be geometry that
can be re-found, not "face 6 of body 3". This is the single most valuable thing in this document.

**Correction, from building it.** Pure geometry is not sufficient on its own, and a test caught
that within an hour of this being written. A point and a normal survive an unrelated upstream edit
perfectly — and break the moment the referenced face ITSELF moves, because the stored point is no
longer anywhere near it. Make the block taller and the sketch on its top face is lost. FreeCAD's
`"Face6"` has exactly the opposite failure: it follows a face that moves, and jumps to a different
one when the ordering changes.

`FaceRef` therefore carries **the body id as well** as the point and normal. Body ids are already
kept stable across rebuilds (`FeatureContext.SeedIdCounter` exists for this). Resolution is: find
that body, take its faces pointing the right way, and among those pick the one nearest the stored
point — so the point *disambiguates between candidates* rather than acting as a hard constraint.
That is what lets the face move and still be found, and it is why `FaceSketchTests` can assert that
growing a block carries the boss on its top face up with it.

## 4. Regeneration and dirtiness — Effigy's model is right

Solvespace's `MarkGroupDirty` walks the group order, and from the changed group onward sets
`clean = false` on every one. Regeneration then re-runs from the first unclean group.

That is precisely `PartStudio.MarkDirty( index )` setting `_dirtyFrom`, and it confirms the model
is the conventional one. It also underlines the bug fixed in this branch: the editor was changing
parameters *without* marking anything dirty, so nothing ever re-ran. Solvespace calls
`MarkGroupDirty` from the entity-changed path itself, so an edit cannot skip it — worth copying as
a discipline: the mark belongs as close to the mutation as possible, not in the caller.

## 5. Feature list state — one thing Effigy is missing

Solvespace's `Group` carries, among others:

| field | Effigy equivalent |
|---|---|
| `order` | list position |
| `suppress` | `Feature.Suppressed` |
| `visible` | **missing** |
| `clean` | `PartStudio._dirtyFrom` (studio-wide rather than per-feature) |
| `activeWorkplane` | `SketchFeature.Plane` |

`visible` is the hide/show that the feature list wants and Effigy has no concept of. It is worth
noting it is *separate from suppress*: suppress removes the feature's effect from the model, hide
only stops drawing it. Conflating them would be wrong.

## 6. Extrude termination

FreeCAD's extrude is parameterised well beyond a distance: `Type`/`Type2` (blind, through all, up
to face, up to shape, symmetric), `Length`/`Length2`, `TaperAngle`, `Offset`, `UseCustomVector`,
`AlongSketchNormal`, `ReferenceAxis`, `StartType`/`StartOffset`.

**For Effigy**, the cheap wins that need no boolean are taper angle and a second, independent
distance for the other side. "Up to face" and "through all" need the boolean and belong with it.

**Correction, from building them.** They do not. Both are questions about DISTANCE — how far to the
first thing in the way, how far past the last — and a raycast answers each without any CSG at all.
What the boolean would add is trimming the new solid against the target SURFACE, so a boss meeting an
angled face ends in a matching slope instead of a flat cap short of it. That is a real difference and
a much smaller one than "needs the boolean" implied: it is the difference between exact and
approximate on angled targets, not between working and not working. Both are built, and the flat cap
is warned about whenever the sample rays disagree.

## 7. The constraint solver

Effigy's sketcher has inference (horizontal/vertical/point snapping) and stores
`SketchConstraint`s, but nothing solves them — `MODELING-HANDOFF.md` calls this out as the one
sketcher piece not built.

Solvespace is the reference implementation worth studying here: it is a general Newton solver over
constraint equations, with explicit handling for redundancy and degrees-of-freedom reporting
(`Group.solved` carries `dof`, `how`, and a list of constraints to remove). The DOF reporting is
what makes a sketch feel diagnosable rather than mysterious — it can tell you a sketch is
under-constrained and by how much.

**This is a large, self-contained piece of work** and it is *pure kernel* — meaning, in this repo,
it is fully testable without s&box. That makes it unusually well suited to being built and verified
here rather than at the far end of a compile.

---

## Audit: what each feature needs to select, and whether it can

Taken from the parameter each feature declares against what the dialog can actually render.
`AllFeaturesTests` proves every one of these BUILDS; this table is about whether you can point at
what it should act on.

| Feature | Needs to select | State |
|---|---|---|
| Sketch | a plane | **works** — plane selector, and it is the affordance to copy |
| Sketch | a face of a body | **works** — the same box, one click |
| Face material | which faces to paint | **works** — multi-select face box, tinted in the viewport |
| Primitive | nothing | n/a |
| Extrude | which sketch | **works** — sketch selector |
| Extrude | which region of it | kernel supports it (`RegionSeed`); no UI yet |
| Revolve | which sketch | **works** |
| Revolve | **an axis** | typed Vec3 only. Its default runs through the sketch origin, so the first press on a normal sketch always fails |
| Shell, Bevel, Subdivide, Transform, UV Project, Mirror, Linear Pattern, Circular Pattern | which bodies | **works** — selection box, multi-select, empty still means all |
| Mirror | a mirror plane | typed Vec3 point + normal |
| Linear Pattern | a direction | typed Vec3 |
| Circular Pattern | an axis | typed Vec3 point + direction |

Body selection was the one to fix first — a single control unblocking eight tools, with the parameter
behind it already working — and it is now a selection box on the same pattern as the plane and
profile ones. Multi-select, because unlike a plane the question has any number of answers: a click
toggles a body and the box stays armed. Empty still means every body, so no existing document
changes meaning.

Revolve's axis is now first, and is the clearest case of a tool that looks broken rather than
unfinished: the default axis passes through the sketch origin, which is where people draw, so the
button reliably errors the first time it is pressed. The error now names how far the profile
reaches either side, but the real fix is picking the axis in the viewport.

The typed Vec3 directions (mirror plane, pattern axis) are usable as-is and are a much lower
priority than either.

---

## What neither of them had to solve, because they always had it

Both Solvespace and FreeCAD have had a document format since before either had most of its features,
which is why neither codebase has anything interesting to say about it: it is not a hard problem, it
is a load-bearing one. Effigy went the other way and had a full parametric history with nowhere to
put it, which made every session a one-shot bake — you kept the OBJ and lost the model.

`StudioDocument` closes that. Worth noting for anyone comparing: FreeCAD stores its document as a
zipped XML of typed properties, and Solvespace writes a flat text file of group/request/constraint
records. Effigy's is nearer Solvespace's — flat text, one record per line — with the difference that
the field list comes from reflection rather than a hand-maintained table, so the format cannot fall
behind the features the way a table does.

## Planar face traversal, which is where both of them ended up

Solvespace and FreeCAD both do region-finding by proper planar face traversal, and neither treats it
as notable — it is simply what you do once a sketch can branch. Effigy followed only degree-2 points
for a long time, which covers a rectangle and refuses a rectangle with a line across it.

That is built now, and the shape of it is the textbook one: half-edges, sorted by leaving tangent at
each point, next-face-edge is the clockwise neighbour of the reverse. The one detail worth writing
down for anyone porting this reasoning: the sort key must be the TANGENT the curve leaves in, not the
direction to its far endpoint. An arc and a line sharing a point sort wrongly under the second, and a
wrong sort silently returns the wrong faces rather than failing.

## Suggested order for Effigy

Ranked by value against cost, given a mesh boolean is the expensive prerequisite for the headline
feature:

1. ~~**`visible` on features**~~ — done.
2. ~~**Derived sketch planes: offset, and from a planar face.**~~ Done, and the face reference rides
   its face when that face moves rather than being pinned to an absolute point.
3. ~~**Extrude taper and two-sided distances.**~~ Built. FreeCAD's list also has Offset, UseCustomVector,
   AlongSketchNormal and ReferenceAxis; none of those has been asked for yet.

   One deliberate divergence: draft is measured from the START OF THE SWEEP, so the solid is one
   frustum. Onshape measures from the sketch plane, which makes a symmetric extrude draft away from
   that plane in both directions — right for a moulded part with a parting line, and a separate
   option if it is ever wanted, rather than a hidden difference in what Symmetric means.

   (Profiles with holes were on this list as needing the boolean. They did not: capping around a
   hole is 2D triangulation, and it is built. Worth remembering as a case where the stated reason
   for a limitation outlived the limitation itself — the refusal message was still quoting the
   boolean long after ear clipping arrived.)
4. ~~**Operation parameter on Extrude (New/Add/Subtract)**~~ — done for New and Add, with Add
   merging rather than unioning (see section 2). Subtract still waits on the boolean and is not
   offered until it exists.
5. **Mesh boolean.** The big one, and now the only thing between Remove and working: the seam, the
   dropdown, the operand order and every failure path are built and tested. What is missing is the
   adapter from PolyMesh to the engine's PolygonMesh, which cannot be written without the engine in
   front of you — `effigy_probe_boolean` dumps the API it gets written from. A portable
   implementation stays off the table until something genuinely needs one.
6. ~~**Constraint solver.**~~ Landed, and since extended with angle, point-on-line, symmetric and
   radius — the set a dimension tool needs underneath it. Solvespace's DOF reporting turned out to be
   the valuable part, exactly as this document guessed: counting the Jacobian's RANK rather than its
   rows is what separates "under defined by two" from "four constraints that say three things": Levenberg-Marquardt over the residuals, seven constraint kinds,
   degrees of freedom counted from the Jacobian's rank. What it still needs is the UI — there is no
   way to add a constraint in the editor, so the solver currently only runs on constraints the
   inference puts there. That, and a dimension tool, are what turn it into something a user can use.

Note that 3 and 4 are both reachable and verifiable in this repo with no engine present.

## Where this left off

Everything below the line is kernel-side and verified by `tools/test.sh` (1275 checks). Everything
in `Editor/EffigyEditor` is written and syntax-checked but **has never been compiled** — there is no
s&box assembly in this repo, so nothing there resolves names. That is the standing risk and the
reason the split below is drawn where it is: anything that could be moved into the kernel and tested
was.

**Face materials by right-click** (merged in #12). Right-clicking a face of the model opens a material menu — the slots, a rename for
the one it is on, and the viewport's slot-shading toggle. The toolbar's Face Material feature is
still there for painting a set of faces deliberately; this is for the common case of one face, one
slot, now.

- `Effigy/Features/FaceMaterialEdit.cs` — the bookkeeping, and the tested half. Which assignment to
  reuse, what happens to the one a face is leaving, where a new one goes in a rolled-back tree.
  Covered by `Effigy.Tests/FaceMenuTests.cs`.
- `Editor/EffigyEditor/EffigyViewport.FaceMenu.cs` — the raycast and the right-click. Holds last
  frame's cursor ray, because `Gizmo.CurrentRay` means nothing inside a menu callback, and refuses
  the menu for a quarter second after the fly camera last actually moved, because the context-menu
  event arrives on the button RELEASE and every orbit ends over the model.
- `EffigyWindow.OpenFaceMaterialMenu` — the menu itself. Undo now snapshots face sets and slot names,
  which it did not before; without that, Ctrl+Z after a right-click removed the feature it had added
  and left a face added to an existing one exactly where it was.

**Two things still need the user's machine, and only the user's machine.**

1. A compile of the editor half. Nothing in `Editor/EffigyEditor` has been through a compiler.
2. `effigy_probe_boolean`, which dumps s&box's `PolygonMesh` API. That is the whole of what stands
   between the built-and-tested cut/Remove path and it working — see item 5 above.

**Since then**, #13 landed the bone-authoring panel (`EffigyRigPanel.cs`), made `Skeleton` editable
rather than read-only, and fixed Bevel flinging corners thousands of units out on collinear edges —
found by a *render*, not by the suite, which stayed green throughout because the result was still
closed, manifold and Euler-correct. The bone tool also took ownership of the right button, so the
face menu's guard now includes `BoneToolActive`.

**The toolbar's icons — done.** Both rows are drawn rather than borrowed from a font. The six the
first render condemned were redrawn: Extrude now grows UP off its profile instead of reading as a
plumb bob, Revolve draws the turned SHAPE rather than the operation (three attempts at profile +
axis + sweep arrow all collapsed — one came out as a bar and a blob, another as an eye), Bevel is a
filled block with a deep bright chamfer instead of a page icon, Shell fills the WALL and leaves the
void out instead of being a square-in-square frame, Subdivide shows one quadrant genuinely denser
instead of reading as "add", and Circular pattern's dashed ring is a solid one because twelve dashes
at toolbar size are a smudge.

One correction worth keeping: the first pass judged everything at 18px. The real size is
`ButtonSize` 54 with `IconScale` 1.5, so the ±8-unit glyph box lands at about **24 pixels**. Judge
at 24; 18 is more pessimistic than anything the toolbar actually does.

**Constraint UI — built.** `ConstraintTools` in the kernel turns a selection into the constraints it
allows, measured and ready; the editor adds a persistent sketch selection (click to accumulate,
click empty space to clear) and a right-click menu over it. Dimensions open pre-filled with what the
sketch currently is, and go through the same expression evaluator as every other numeric field, so
"25/2" works. A rule that cannot be satisfied is taken back out and the geometry restored exactly,
rather than left contradicting the sketch forever.

Two things it does NOT do, both deliberate. There is no constraint TOOLBAR — offers change with
every click, so a strip of buttons would relabel and re-enable itself per frame, and that is widget
code this repo cannot compile to check; a menu is built fresh each time from machinery already
proven elsewhere. And selection accumulates on plain clicks rather than Ctrl-clicks, because no
modifier-key API is proven in this corpus and an unproven member name takes the whole editor
assembly down.

Constraint GLYPHS are drawn too, and clicking one removes that rule — "why will this line not move"
is a question about a specific place on the drawing, so the answer sits there next to it. A rule
relating two segments marks BOTH of them, since one glyph in the middle would leave you guessing
which pair it meant on a sketch with six lines in it; an angle is marked where its two lines
actually cross, out in space if that is where the extended lines would meet.

**A render-based half of the test suite — built.** `RenderCheck` rasterises a mesh and reduces it to
coverage, island count and front/back parity, which catch the mistakes counting cannot: a vertex in
the wrong place, a detached fragment, and a face wound backwards. That last one leaves a mesh closed,
manifold and Euler-correct, so every other oracle in the suite calls it fine. The Bevel bug is what
prompted this; the tests damage good models three ways and fail if a check stays quiet.

**A habit worth keeping.** Three separate limitations in this file were documented as needing the
mesh boolean and none of them did — profiles with holes, revolve with holes, and up-to-face
termination. Treat every remaining "not supported yet" string in the kernel as suspect until it has
been re-derived rather than re-read.
