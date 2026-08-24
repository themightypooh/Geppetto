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
| Sketch | a face of a body | kernel supports it (`FaceRef`); no UI yet |
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

## Suggested order for Effigy

Ranked by value against cost, given a mesh boolean is the expensive prerequisite for the headline
feature:

1. ~~**`visible` on features**~~ — done.
2. ~~**Derived sketch planes: offset, and from a planar face.**~~ Done, and the face reference rides
   its face when that face moves rather than being pinned to an absolute point.
3. **Extrude taper and two-sided distances.** Small, no boolean. The cheapest thing left.
4. **Operation parameter on Extrude (New/Add/Subtract)** — wire the parameter and the UI first,
   erroring clearly on Add/Subtract until the boolean exists.
5. **Mesh boolean.** The big one. Everything above is useful without it; nothing below is possible
   without it.
6. ~~**Constraint solver.**~~ Landed: Levenberg-Marquardt over the residuals, seven constraint kinds,
   degrees of freedom counted from the Jacobian's rank. What it still needs is the UI — there is no
   way to add a constraint in the editor, so the solver currently only runs on constraints the
   inference puts there. That, and a dimension tool, are what turn it into something a user can use.

Note that 3 and 4 are both reachable and verifiable in this repo with no engine present.
