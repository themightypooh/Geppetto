# What is left

Ordered by what will actually block progress. Every item names its **method** where one is known,
because "add rounded fillets" is not a task and "rework the cap pass alongside the bridge pass,
verify with `RenderCheck`" is.

Two words are used precisely throughout:

- **Written** — it compiles, and has never been run. Compiling is not behaving.
- **Not started** — no code exists.

---

## 0. Run the thing, before building anything else

Everything in this section is written, compiles, and **has never been seen on screen.** The
environment that produced it had no s&box; that is no longer true, so this is now the cheapest work
in the project and it gates judgement on everything else.

**1. The cube → sketch-on-its-face → extrude demo.** The single highest-value thing here. It is the
one po explicitly wants on video, and it exercises the face picker, the sketch, the extrude and the
boolean in one pass. The kernel half is fully tested (`FaceSketchTests`, `RaycastTests`,
`EditorFlowTests`); the UI half — `MeshRaycast` behind `EffigyPlaneSelector`, and
`EffigyViewport.FacePickMode`/`FacePicked` wrapping `Gizmo.CurrentRay` — has only ever been
compiled. Sketch on a face of a primitive box, extrude it, confirm the boss lands in the right
place.

**2. The extrude gizmo and numeric entry, with screenshots.** po wants visual confirmation that
picking a face arms the pull gizmo, that dragging grows the solid live, and that a numeric box
accepts an exact distance instead of a drag. Same sitting as item 1 — the editor's MCP server takes
the pictures (`camera_screenshot`, `editor_camera_screenshot`). **Do not assume the gizmo works
until it has been run once.**

**3. The sketch toolbar swapping.** Ten seconds: enter a sketch and watch the strip. The feature
strip and the sketch strip are now the same kind of floating widget and `EnterSketch`/`FinishSketch`
toggle both, which is the fix for them previously being two unrelated systems where nothing ever hid
the feature strip.

**4. The Settings window** — judge the hand-painted grid switch and its slide animation, and pick a
palette. **The dark-mode contrast call needs po's actual monitor, not RGB values** — `OnshapeDark`
may already qualify.

**5. Plane corner resize** — whether the handle fades in at the right distance and the drag feels
right.

**6. The two new strip buttons and the six new constraints.** Same sitting, because they are the same
kind of unknown: Sweep and Loft now have buttons and hand-drawn glyphs, and the constraint menu now
offers Diameter, Midpoint, Concentric, Fix and both tangencies. All of it compiles and the kernel half
is tested; none of it has been looked at. See 2.1 and 2.2 for what to check.

**7. The diagnostic panel and the tree tooltip.** Open a fillet on a 2×2×2 cube, drag radius past
0.85, and read the dialog: problem in red, cause with the volumes, a button that sets the radius to
the largest that fits. The tree row should show the problem on hover, and a warning (fillet at 0.8)
should colour the icon yellow. Kernel half is tested; this is the sitting.

---

## 1. Effigy kernel

The kernel's named gaps for phase one are **closed** - every item below this line is struck
through. What is left of the kernel is running it against the engine's own output rather than
against hand-built fixtures. All of it is headless-testable — no s&box anywhere, and
2272 checks say so.

### 1.1 ~~Exercise the boolean past the one case that works~~ — **done**

Every case this section listed is now closed, and the repair is four passes rather than one. They
are chained inside `CloseBoundaryLoopsIntoFaces`, each seeing only what the one before it declined,
so a caller still never has to know which shape of mouth it has:

1. **`MeshHoleRepair`** — the loop lies inside exactly one coplanar face. Splices it in as a hole.
2. **`MeshHoleRepairSpan`** — the loop crosses one edge between two coplanar faces. Notches both.
3. **`MeshHoleRepairCurved`** — the loop crosses ANY number of faces, coplanar or not. Each face gets
   the arc of the loop that lies in it and is re-partitioned by its arcs.
4. **`MeshHoleRepairFragment`** — the loop lies inside a surface that is already in pieces. Takes the
   whole coplanar group as one region and adds the mouth as one more hole.

- ~~a cut through a **curved** face~~ — **done, and it is the one that could not be phrased as a
  plane at all.** A mouth across a ridge has no normal, so there is no containing face to look for
  and no shared basis to work in. `MeshHoleRepairCurved` reads the answer off the WALL instead: every
  loop edge is used by exactly one face, two faces sharing an edge traverse it opposite ways, so the
  surface must walk each arc against the wall — and a region that walks one of its arcs the wall's
  way is on the void side of it. No plane appears in that argument anywhere, which is why it survives
  where every planar argument stops. Covered by a tent with a shaft through its ridge.
- ~~a cut meeting an edge, so the mouth spans two faces~~ — **done.** `MeshHoleRepairSpan` splits the
  loop at the crossing and notches BOTH faces, rather than loosening the containment test the
  single-face repair does properly.
- ~~two cuts overlapping, and cutting a body that has already been cut~~ — **done, and the shape of
  the problem was not what this line assumed.** The second cut is not hard because it overlaps the
  first; it is hard because the first repair TRIANGULATED the face it fixed, so the second mouth
  lands across a dozen coplanar fragments whose edges it crosses at ordinary points rather than at
  vertices. That defeats all three of the passes above on purpose. `MeshHoleRepairFragment` stops
  treating the fragments as faces: they are one surface a previous repair left in pieces, so it takes
  the group whole and puts the mouth in as another hole. Covered by a fixture that runs the first
  repair for real rather than hand-writing the fan it produces.
- ~~a cut that separates the body into two pieces~~ — **done, and the fix was not in the repair.** The
  boolean returns a perfectly good mesh; the bug was that a Body is assumed to be one solid by
  everything that reads it, so a severed part showed as one part in the list, hid as one, painted as
  one, and got one convex hull wrapped around the gap the cut had just made. `MeshSplit` answers "how
  many solids is this", `Feature.SeparatePieces` puts the offcuts in the part list, and both cutting
  features call it. **The largest piece keeps the original body's id**, because every sketch and
  picked face hanging off that part is holding it. The piece order is a promise for the same reason —
  largest volume first, ties broken on the minimum corner — since anything else renames bodies
  between rebuilds. See `SplitTests`.
- ~~two holes in one face~~ — **done, and it was the shared limit rather than the boolean's own.**
  `SplitBridgedLoop` peels one bridge at a time, shortest run first, and `SplitIntoFaces` cuts each
  hole against whichever face it landed in: **n holes, n+1 faces**, on both paths. A face with two
  pockets returns three faces; a drawn plate with two bolt holes extrudes to eighteen.

**What the two new passes still decline, deliberately.** A crossing that is not already a vertex —
inventing one means splitting a face the repair was not asked to touch. A loop whose vertices sit
strictly inside two faces at once. And a fragmented group whose mouth is not strictly inside its
outer contour and outside every hole it already has. Each refusal leaves the opening visibly open,
which is the failure everyone can see rather than the one nobody can.

**Both new passes check their own work and roll back.** Splitting polygons by chords and deciding
materiality from a winding is far too much machinery to run on trust, so the mesh is measured before
and after every loop and a repair that did not strictly reduce the open boundary — or that introduced
a non-manifold edge — is undone. That is what makes "it declines" a guarantee rather than an
intention.

**What is still only the sitting:** running all four against the ENGINE's own loops rather than against
hand-built fixtures. `effigy_dump_tree` names the failure mode directly — `boundary edges`,
`bridged faces`, `opening(s) reinstated`. Where a case fails, reproduce the mesh shape as a fixture in
`HoleTests` or `CurvedHoleTests` and fix against that.

**Do not measure this by eye.** All four bugs fixed in the boolean produced closed, manifold,
Euler-correct, valid meshes, and so would every wrong answer the new passes could give.

### 1.2 ~~Rounded (multi-segment) fillets~~ — **done**

`Bevel` is now `EdgeBlend`, and it offers `Chamfer` and `Fillet`. In the editor they are two
features with the names Onshape gives them — Chamfer sized in a **distance**, Fillet in a **radius**
and a segment count — rather than one tool with a control that means a different thing at each end
of its range. `BevelFeature` still loads: `StudioDocument.RenamedFeatures` maps it to
`ChamferFeature`, parameters intact.

It was done the way this section said to do it. The cap pass and the bridge pass were reworked
together: `ArcRails` builds each rounded edge's points once, the bridge lays them across the edge
and the cap threads the same indices around the vertex in the walk's own direction. The 20× corner
clamp stayed.

Two things the plan did not anticipate, both now in `EdgeBlend`'s comments:

- **A radius is not a setback.** A chamfer's distance is measured back along each face; a fillet's
  radius is the arc's own, and the tangent points fall `r/tan(φ/2)` back for an edge opening at φ.
  So the setback is per-edge, not one number for the mesh. On a cube every edge opens at 90° and the
  two coincide — which is why one global width was enough while only chamfers existed, and why a
  cube would never have caught it. `TestRadiusIsARadius` uses a wedge.
- **The arc's centre is found twice and checked**, once from each tangent point. Where they disagree
  the corner has been dragged somewhere the arc cannot follow, and that edge falls back to a flat
  quad rather than guessing.

`TestArcIsRound` is the check that matters: closed, manifold, Euler and a plausible face count are
all satisfied just as well by a chamfer cut into n flat strips, which is what a slerp silently
degrading to a lerp would produce. It measures every strip point against the edge's axis instead.

### 1.2b ~~Diagnostics — why a feature refused, and what to do instead~~ — **done**

Kernel half is tested (`DiagnosticTests`, 1609 checks). `Fillet(cube, 0.85)` is an error; the
inverted-solid table from the brief cannot happen silently any more. The dialog panel (problem /
cause / remedy-as-button) and the tree tooltip / yellow warning icon are **written, not seen** —
they belong in section 0 the next time the editor is open.

### 1.3 ~~Collision from the primitive history~~ — **done**

`CollisionBuilder` walks the history and emits primitives where it can: a model built out of boxes IS
its own collision, exactly. Anything a primitive cannot describe — a boolean, a fillet, a subdivide,
a rotated Transform — spoils the decomposition and it falls back to one convex hull per body, naming
the feature that spoiled it. `ConvexHull` is an incremental hull, tested for containment (every
vertex of the part inside it) rather than for looking right.

Deliberately all-or-nothing rather than per body: a physics representation that is exact for three
props and quietly wrong for the fourth is worse than one that is approximate for all four and says so.

**Now written into the .vmdl too.** `VmdlPhysics.ShapeList` emits the PhysicsShapeList node and
`EffigyWindow.BuildVmdl` splices it in, so a static export carries its own exact collision.

**The schema was measured, not guessed**, which is what the old note was waiting for. Each shape went
into a probe .vmdl, the engine compiled it, and the compiled model's own physics bounds were read
back. What that settled:

| | |
|---|---|
| `PhysicsShapeBox` | `dimensions` is the **full size**, not the half-extents; placed by `origin`; `angles` works |
| | `center`, `translation` and `position` all compile on a box and are **ignored** |
| `PhysicsShapeSphere` | `radius` plus `center` — and `center` only, which is not the box's key |
| `PhysicsShapeCylinder` | `radius` plus `point0`/`point1`, which are where it goes |
| `PhysicsShapeHull` | `hull_vertices`, in **model space**, exact |
| all of them | `parent_bone`, `surface_prop`, `collision_tags`, off citizen's own prefab |

**And it turned up a bug that predates it.** ModelDoc's OBJ importer does not land the mesh in the
coordinates the file gives it — a bar written along +x compiles lying along +y. That was survivable
while the .vmdl carried no collision (the part was simply a quarter turn from how it was drawn) and
stops being survivable the moment shapes go in, because those ARE in the file's own coordinates.
`import_rotation = [ 0, -90, 0 ]` on the OBJ RenderMeshFile cancels it; both signs were tried against
a one-sided bar, and +90 put the mesh at x = -10..0. The DMX path does NOT get this and must not.

**Verified end to end on 2026-08-31**: the two-box sample compiled to `Bounds 13 x 4 x 4` AND
`PhysicsBounds 13 x 4 x 4` — the mesh where it was drawn, and the collision on it rather than beside
it. `out/sample_physics.{obj,vmdl}` is written by the suite for re-running that check; see
`VmdlPhysicsTests.WriteSample` for the four numbers and what each failure looks like.

**A rigged export still uses `PhysicsMeshFromRender`, deliberately.** Every shape CollisionBuilder
produces is in model space with no bone to hang off, and the mapping from a body to the bone that
drives it is exactly what the rig panel exists to let somebody decide. Writing them all against the
root would put static collision on an animating character — right until something moves.

`File > Collision Report` stays, because it is still the only place that says WHY a part came out as
hulls rather than as the boxes it was drawn from.

*The old note, kept because it was the reasoning: nothing existed, and `Collision` appeared nowhere
in the kernel.*

A model known to be a union of N convex primitives **is** its own physics representation, so this is
bookkeeping rather than geometry.

**Method:** walk the feature tree rather than the finished mesh. A `PrimitiveFeature` contributes its
own shape and transform; a pattern or mirror contributes copies; anything that has been through a
boolean or a subdivide falls back to a convex hull of its body, or to the mesh itself. Emit a list of
convex shapes, not triangles. Testable headlessly by volume and count.

### 1.4 ~~Draft on existing faces~~ — **done**

`DraftOperation` plus `DraftFeature`, on the strip next to Shell. Every vertex moves along the
horizontal component of its own normal, proportional to its signed distance from the neutral plane.

Two things the tests caught that reading would not have. **A corner belongs to two walls and has to
lean both by the angle** — moving it along the averaged normal leans each wall by only its component,
about 7 degrees out of 10, and a part comes out under-drafted, which is the one failure that matters
for a mould. And the third of the three checks LoopOffset uses — *no edge reversed* — was named in the
comment and not written; without it a drafted box folds into a bow-tie that keeps its area and its
Newell normal and passes both of the other two.

*The old note: well defined, small, and genuinely absent.*

Extrude has `Taper`, which covers a face being **made**. Drafting faces of a solid that already
exists does not exist at all.

**Method:** pick faces plus a neutral plane and a pull direction. Move each vertex along the
horizontal component of its own normal, proportional to its signed distance from the neutral plane.
Refuse self-intersection with the three checks `LoopOffset` already uses — signed area keeps its
sign, it has not collapsed, no edge reversed — because the third catches the inside-out case the
first two call healthy.

### 1.5 ~~A hole feature~~ — **done**

`HoleOperation` builds the negative — simple, counterbore or countersink — and `HoleFeature` hands it
to the boolean as a tool, drilled into the picked face along that face's own normal. A countersink is
specified by an included angle and a head size, never a depth, so the depth follows from them.

The test caught the one that matters: the cone was reaching its head diameter a third of a unit ABOVE
the part, because the overshoot that keeps the boolean off a coplanar face was lifting the wide ring
straight up instead of continuing the taper through it. The hole would have been narrower than asked
for at the only place anybody measures it.

*The old note: convenience, not capability.*

Counterbore and countersink as a tool solid emitted with `Result = Remove`. Holes already work as
inner loops of a profile and cuts now work, so this is a parameterised shape and a dialog. It cannot
build in the headless suite without a boolean provider — `MergeTests` installs a stub for exactly
this, do the same.

---

## 2. Effigy editor — the bigger gap

The kernel can do things the tool cannot reach. This is now the larger half of the project.

### 2.1 Sweep and loft on the strip — **written, and never seen on screen**

Done as code: `ToolKind.Sweep`/`ToolKind.Loft`, a `CreateTools` row each, and hand-drawn
`EffigyIcon.Sweep`/`EffigyIcon.Loft` glyphs in the same idiom as the rest of the strip — a profile
carried along a curved path, and two sections with the skin ruled between them. The strip is now
fifteen buttons. The editor assembly compiles with no new warnings.

Neither needs its selector filled in to do something, which is why a button alone is enough: an empty
`SweepFeature.PathSketchId` means "the sketch before the profile's", and a `LoftFeature` with fewer
than two `Sections` lofts every sketch available. `EffigySketchSelector`
(`EffigyFeatureDialog.cs:1244`) arms for both, being `SketchConsumingFeature`s.

**What is left:** the sitting. Draw two sketches, press each button, and look — at the result, and at
the two new glyphs at strip size, which have been drawn against a nominal 18×18 box and never
rendered. Then the refinements: a path selector for sweep, an ordered section list for loft.

### 2.2 The six constraints that had no way in — **written, and never seen on screen**

`SketchConstraintKind` has seventeen kinds and `ConstraintTools` offered eleven. The other six —
`Diameter`, `Midpoint`, `Concentric`, `Fixed`, `Tangent`, `TangentArcs` — are now offered, marked up
and covered:

- **Diameter** joins Radius on one arc, opening on twice the radius. Having either means being
  offered neither again: they are one rule written two ways, and a sketch carrying both solves and
  then reports redundancy.
- **Midpoint** joins Point-on-line on a point and a line, being the same selection said exactly.
- **Concentric** on any two things with centres — the one new rule a *circle* can take part in,
  since it contributes its centre to the solve and nothing else.
- **Fix** on a single point, carrying a position rather than a magnitude. `ConstraintOffer` gained a
  `ValueY` for it: `Apply` writes the offer's value onto the constraint, so a fix whose y did not
  make that trip would have been applied to the right x and y = 0.
- **Tangent** on a line and an arc.
- **Tangent** on two arcs, with **internal or external read off the sketch** rather than asked for —
  whichever arrangement the drawing is already closer to.

Arcs only for the two tangencies and for Diameter, for the reason the file already gives about
Radius: a tangency is written against a centre and a rim point, and a circle has no rim point for the
solver to move.

`ConstraintToolTests` grew a section that offers *and applies* each one and checks the geometry
obeyed — a table of offers reads as correct while the point indices inside it are wrong. 1423 checks
pass.

**What is left:** the sitting. Select the geometry, right-click, and confirm each of the six appears
with a sensible icon, applies, and leaves a mark that can be clicked to delete it. The new marks are
`D`, `MID`, `CO`, `FIX` and `T`, ASCII for the reason the file gives about the perpendicular sign.

### 2.3 ~~The missing sketch tools~~ — **compiles, and never seen on screen**

All six are on the strip: ellipse and spline with the drawing tools, and trim, extend, fillet and
offset in a group of their own, because clicking one of those on empty space does nothing and the
grouping is what says why.

**They intercept before the existing switches rather than adding cases to them.** Twelve drawing
tools already share that state machine and all twelve work; threading six more cases through its
click, preview and prompt switches means editing known-good code, blind, to add tools that are not.
So there are three one-line hooks and everything else is in `EffigyViewport.SketchTools.cs`.

Every refusal is the kernel's own words — `SketchEdit` was written to hand back a reason, and the
tools show it rather than inventing one.

**It compiles in s&box** — confirmed on the second batch as it was on the first. The missing-`using`
lint over `Editor/` caught nothing this time, which is the point of having written it. Compiling is
still not behaving: none of these six has been clicked.

*What the old note said, which is still the useful summary of what each one wants:*

`SketchToolKind` has twelve entries — Select plus eleven draw tools. There is no ellipse tool, no
spline tool, and no trim, extend, fillet or offset tool, though `SketchEdit` implements all four and
`SketchEllipse`/`SketchSpline` are real curve classes. These were absent-rather-than-dead when there
was nothing underneath them; now the hard half of each is done and tested.

- **Trim** wants a click on the piece to remove; the kernel takes the curve and a pick point.
- **Extend** wants a click on the end to stretch; the kernel takes the curve and which end.
- **Offset** wants a chain and a distance, and reports corners it could not close.
- **Fillet** wants the corner point and a radius. It refuses a radius too big for its arms rather
  than clamping, so the editor has a message to show rather than a silently different result.
- **Spline** wants click-to-place points; **ellipse** a centre, a major-axis point and a minor
  radius. Both are ordinary sketch points, so dragging and dimensioning them already work.

### 2.4 ~~Hide affordances for planes and origin~~ — **already done, and this note was stale**

Re-checked: `DefaultGeometryChildNode` implements `IVisibilityNode` and paints through `TreeEyeIcon`,
which is hover-reveal and stays visible while hidden — exactly po's spec. `OnTreeVisibilityToggled`
wires all four keys (`origin`, `top`, `front`, `right`) to the viewport's own flags.

**Worth reading the note below anyway**, because it is a lesson about these documents rather than
about the feature: it said "Re-verified 30 August 2026: no eye-icon or per-plane visibility code
exists", and it was wrong. A dated re-verification reads as more trustworthy than an undated claim
and is worth exactly as much. Check the code.

*The old note, kept as written:*

*po's spec, verbatim, and the only one of po's UI items with nothing written at all.*

- An eye icon sits to the **right** of each plane's (and the origin's) row in the feature tree.
- The icon is only **visible on hover** over that row — not shown when not hovering.
- Clicking it hides the plane/origin in the viewport.
- After hiding, the icon **stays visible in that row** so it can be clicked again to unhide — the
  hidden state pins the icon visible even without hovering; only the *unhidden* state is hover-only.

Re-verified 30 August 2026: no eye-icon or per-plane visibility code exists. Closest precedent is
`Feature.Visible`/`Body.Visible`, which hides body *geometry*, not tree chrome. Planes and origin are
drawn by the viewport directly rather than modelled as bodies, so this needs its own visibility flags
on the viewport plus the tree-row UI.

Worth noting the distinction Solvespace draws and this should keep: **`visible` is separate from
`suppress`.** Suppress removes a feature's effect from the model; hide only stops drawing it.
Conflating them would be wrong.

### 2.5 Hover a sketch face to select its owning sketch

*po's spec.* Hovering a **closed sketch face** in the viewport (the shaded region) should select the
sketch that owns it — as the general "point at a face" affordance, not just during a face pick for
Extrude. Nothing implements that today.

The other two thirds of this item are done: right-clicking a face gives a real menu, and tree click
selects.

### 2.6 Sketch-strip icons — **drawn; now rendered, and the sizing note is confirmed by measurement**

**The first half of this note was stale.** All ~14 sketch-strip glyphs are hand-drawn in the
`EffigyIcons` idiom — the enum's own comment records what they replaced ("`show_chart` for Line,
`cached` for Arc, `crop_square` for Rectangle") — and so are the eleven sculpt glyphs, Draft, Hole
and the six later sketch tools. Fifty-one in total. `_scale` exists and the strip passes
`IconScale`, so the fixed-18px-glyph-in-a-big-button problem the second half named is also fixed.

**What was true is that none of them had ever been rendered.** `tools/iconsheet` fixes that without
s&box: `EffigyIcons.cs` is LINKED into a small project alongside a shim for the four engine types it
touches, `Editor.Paint` records to SVG instead of to a widget, and the run writes one SVG per glyph
plus an `icon-sheet.html` contact sheet at the strip's real geometry — a 54×54 button with the glyph
at scale 1.5, on both palettes.

```sh
dotnet run --project tools/iconsheet -- out
```

It is exact on geometry, because geometry is all `EffigyIcons` computes. It cannot judge s&box's
rasteriser; pen cap and join are the one guess and are sub-pixel at a 1.6px stroke.

**What the first render settled, by measuring rather than by eye.** Every glyph is authored against
a "nominal 18×18 box", and they are not:

- **The median glyph's largest dimension is 24.6px in a 54px button — 46% of it.** An icon in a
  button of that size normally reads at nearer 60%. `IconScale` is 1.5; about 1.9 would land it
  there. This is the "glyph sits slightly small inside it" observation, now with a number on it.
- **The spread is 2:1, which is the bigger problem.** `CircleTool` covers 34% and
  `ArcThreePointTool` covers 68% — 36.9px wide, which is 24.6 nominal units, well outside the 18×18
  box the file says everything is drawn in. `ArcTool` (58%), `LinearPattern` (57%) and `SplineTool`
  (56%) are also over. A strip whose glyphs vary two-fold in optical size reads as unfinished
  regardless of how good each drawing is.

**What is left is a judgement, and it now has a preview loop behind it:** raise `IconScale`, bring
the four outliers back inside the box, and re-render. Then the drawings themselves — the sheet makes
the weak ones visible, and the candidates are the pairs that read alike at strip size
(`SculptLevelDown`/`SculptLevelUp`/`Subdivide` are all "a grid"; `PolygonTool` and
`PolygonCircumscribedTool` differ only in a circle nobody will see; `Sculpt`, `SculptDraw` and
`SculptGrab` share a silhouette) and `Sweep`, `Loft` and `SculptInflate`, which read as blobs rather
than as operations.

### 2.7 Smaller editor gaps

- ~~**Revolve's axis is typed Vec3 only**~~ — **this note was stale, and the code says so.**
  `RevolveFeature.AxisMode` is a ChoiceParam offering the profile's four edges and the sketch's two
  axes, and `EffigyWindow.NewFeature` creates new revolves on "Profile's left edge" — which is what a
  lathe wants essentially always. Custom stays at index 0 for a documented reason: a ChoiceParam
  serialises its INDEX, so an edge mode at 0 would move every revolve in every saved file. Picking the
  axis in the VIEWPORT is still not done, and that is what remains of this item.
- **Extrude's region choice** has kernel support (`RegionSeed`) and no UI.
- **Mirror plane and pattern axis/direction** are typed Vec3. Usable, and a much lower priority.
- **Per-part hide/show** is done. The Parts list eye and its Hide menu item key `HiddenBodyIds`
  by body id, so one copy of a pattern can be hidden without the rest. The viewport preview already
  took `ToVisibleMesh`.
- **The view cube was a text label.** Removed — this camera flies, so a corner cube is the wrong
  affordance. Named views stay in the View menu.
- **The preview panel's "load an existing .shader"** in Shader Forge takes a typed path.
  `RigControlWindow.OpenPicker` is the precedent for a real asset picker.

---

## 3. Rigging — what remains

1. ~~**Weight painting**~~ — **the kernel half is done; the editor half is not written.** Same shape
   as every other stage here, and the same place in it.

   `WeightBrush` has Add, Subtract, Set and Smooth, with radius, falloff, strength and mirror-X, and
   per-stroke undo that stores only the vertices a stroke touched. `WeightPaintSession` is
   everything that is arithmetic rather than widgets — active bone, stroke lifecycle, undo/redo,
   spacing, and `Influence(bone)` for a viewport that wants to colour the model by weight.

   **The invariant is the whole problem, and it is what the tests measure.** Every vertex's
   influences are non-negative and sum to one, and everything downstream leans on it. So a brush
   cannot simply add to one bone: what the painted bone gains has to come from the others
   proportionally, and what it loses has to go back the same way. Every operation is written as
   "move the painted bone to w, rescale the rest to 1 - w".

   **The case with no answer, stated so it is not discovered as a bug:** a vertex weighted ENTIRELY
   to the bone being subtracted from has nowhere to put the weight. Both tempting fixes are worse
   than refusing — normalising an all-zero set binds the vertex to nothing, which collapses it to
   the model origin on export, and quietly leaving 1.0 makes the brush look broken. It is refused
   and counted, so the tool can say "these have one bone; paint the bone you want them to move to".

   **`WeightPaintLayer` is what makes paint survive a rebuild**, and it is the sculpt stage's answer
   arriving at the rig: Effigy never stores a rig by vertex index, so a naive paint would be wiped
   the next time anything rebuilt. Paint is keyed on `MultiresSculpt.TopologyId` — counts and face
   indices, deliberately not positions — and re-applied AFTER `BindBodies`. Topology unchanged means
   the numbering still means what it did; topology changed means the paint is KEPT and marked stale
   rather than misapplied. Two things differ from a sculpt delta and both matter: **bones are stored
   by NAME**, because `RemoveBone` re-indexes everything after the hole, and **the painted result is
   stored rather than a delta**, because a delta of a normalised quantity is not well defined.

   **What is left is the editor**: a brush in the viewport, a weight ramp over the model, a bone to
   paint chosen from the rig tree, and the layer re-applied on rebuild. `WeightPaintSession` is
   written so that half is thin — see `SculptSession` for the same split and how much of the sculpt
   tool turned out to live above it.

2. ~~**`AnimBindPose`** is unverified~~ — **verified, written, and compiled.** The method this note
   named was the right one and it worked: `first_person_arms_preview.vmdl` ships as source and
   carries the node whole, so `VmdlAnimation.BindPoseList` copies it field for field rather than
   guessing. The skinned export now writes it, and a sample .vmdl around the suite's rigged DMX
   compiles clean.

   **And the run answered §7.2's other open question at the same time.** "That the bones survive the
   compiler" had never been established, because bone names are not recoverable from a `.vmdl_c` by
   inspection. `rig_test_follow <model> __list__` lists them, and the answer came in two parts:
   without a `BoneMarkupList` the two-bone sample compiled to **one bone** — `root` survived, `child`
   was pruned — and with one it compiled to **both**. So the markup list is load-bearing rather than
   belt and braces, and it now lives in the kernel (`VmdlAnimation.BoneMarkupList`) so the sample and
   the editor write the same node.

3. ~~**The Effigy rig panel's remaining reporting**~~ — **done.** `RigDiagnostics.Check` runs on
   every panel refresh and on every studio rebuild, and its problems are a list under the inspector:
   one row each, coloured by severity the way a feature's own diagnostic is, cause and remedy in the
   tooltip, and a target button that selects the bone the problem names — which is what
   `RigProblem.Bone` has been carrying since it was written.

   Two judgement calls in there. The list and its header **hide entirely when there is nothing to
   say**, because a permanently visible "0 problems" panel is one people stop reading. And a rig
   with **no bones yet** is treated as silence rather than as a problem: "this model has no
   skeleton" is true and is not news to somebody who has not placed a bone.

4. **Effigy → Rig Control convenience**: an action to create the `.ctrlrig` and open Rig Control.
   Integration sugar, explicitly not a priority — constraints and animation stay in Rig Control's
   assets.

**Non-goals, so they are not re-proposed:** no Effigy timeline or duplicate FK/IK implementation; no
vertex-index rig storage; no SMD import back into the parametric document; no heat-diffusion solver.

---

## 4. The sculpt stage

Every step of the sculpt plan now has code, and every part of it that can be tested headlessly is
(`SculptTests`, 1926 checks in the suite). The kernel half is done: the maths is
right where a test can see it, and what is left is putting a cursor on it. **Next is step 7, the
editor — the long pole**, exactly as it was for CAD.

The shape of it:

```
PartStudio history ──► cage (quads, UV'd) ──► subdivide L0..L4 ──► + deltas ──► sculpted mesh
        ▲                    │                                                        │
        │                    └────────────────── ship this ──────────┐                │
        └── still editable                                           └── bake normal map
```

**Deltas are stored in a per-vertex local frame, not in world space.** This is the single decision
that makes a sculpt survive a parametric edit: if a delta is world-space and you go back and make the
cage 20% taller, every sculpted detail stays where it was and slides off the surface. Stored as
(normal, tangent, bitangent) coefficients, the detail rides the surface as the surface moves. The
frame must be **deterministic from the mesh alone** — derived, never stored. Normal is the existing
`ComputeVertexNormals()`; tangent is the direction to the lowest-indexed adjacent vertex,
orthonormalised. Ugly but stable, which is the only property that matters.

**How a sculpt re-applies after a parametric edit:** rebuild the cage, compare against
`BaseCageHash` (vertex and face counts plus topology, **not** positions — those are expected to
change). Topology unchanged → subdivide, re-derive frames, re-apply deltas; the sculpt follows the
edit, and this should be the overwhelmingly common path. Topology changed → do not silently drop the
deltas and do not silently misapply them: keep them, mark the layer stale, and offer reprojection.
Until reprojection exists, the right behaviour is a clear refusal.

### Build order

**Step 1 — Stable subdivision correspondence** *(kernel, small)* — **done.** Edge points are sorted
by `(A, B)`. `SubdivideWithMap` returns a `SubdivisionMap`. Verified: same cage twice is the same
map; reversing faces does not shuffle the edge block; layout is originals, then edges, then faces.

**Step 2 — Local frames and the delta round-trip** *(kernel, small)* — **done, and the gate held.**
`SculptFrames.Build`, `SculptLayer.Capture`/`Apply`. Capture-then-apply is the identity. Uniform 2×
scale keeps the bump on the new normal and the same size relative to the cage (local edge length is
derived with the frame, not stored). A 20% taller cage keeps the bump on the surface. Frames stay
orthonormal. See WHAT-IS-BUILT.md.

**Step 3 — Spatial queries** *(kernel, medium)* — **done.** `MeshBVH` over faces, ray + radius,
`Refit` after displacement. Ray hits match linear on point and distance; radius query matches brute
force; refit keeps both true.

**Step 4 — Brushes** *(kernel, medium)* — **done.** `Brush.Apply( mesh, stroke, frames, mask )`.
Smooth, Draw, Inflate, Grab, Flatten, Pinch. Per-stroke undo is affected-vertex diffs, not a full
snapshot. Stopwatch is in the suite from the first brush.

**Step 5 — Multires level management** *(kernel, medium)* — **done.** `MultiresSculpt`. Adding a
level subdivides the *displaced* level below and starts at zero deltas; `ViewLevel` drops the display
without discarding anything. Both named tests hold: sculpt at L3, drop to L1, return — unchanged; and
a level-1 edit after sculpting at L3 moves the surface L3 is written against, with the L3 detail
riding it rather than being flattened. `SetCage` re-bases the stack and refuses a topology change
with both models' numbers; `Stroke`/`Undo` bridge the brushes to the levels. See WHAT-IS-BUILT.md.

The three checks that matter were confirmed by breaking the kernel on purpose and watching them
fail — subdividing the rest mesh instead of the displaced one, dropping the cache invalidation in
`Record`, and handing a brush the rest frames. A green suite that stays green under those is not
evidence of anything.

**Step 6 — `SculptFeature` and persistence** *(kernel + document, medium)* — **done.** Consumes one
body like `ShellFeature`, outputs the top level, refuses a topology change with a cause and remedies
while keeping the deltas. `SculptBlob` is the side-car: 16 bits per component against a per-level
bounding box, six bytes a vertex, keyed by feature id under `model.sculpt/` beside `model.effigy`.
An untouched level round-trips bit-exact. See WHAT-IS-BUILT.md.

Two things came out of this that were not in the plan. `Feature.IsStale` had to exist — a brush
mutates the sculpt nowhere near the studio, so `MarkDirty` is never called and the rebuild served a
cached body; without it the sculpt tool would have looked like it did nothing. And `MultiresSculpt`
gained `Revision` and `SetLayer` to support it and the blob reader.

**Step 7 — The editor** *(s&box, large)* — **written, and never seen on screen.**

`SculptSession` holds everything that is arithmetic rather than widgets, tested headlessly. The s&box
layer on top of it is now written too: a Sculpt button on the feature strip, a sculpt strip sharing
the spot with the other two, `EffigySculptBar` for radius/strength/level, `EffigyViewport.Sculpting.cs`
for the rays and the brush ring, and a bake button that writes a PNG. See WHAT-IS-BUILT.md.

**What is left is the sitting, and it is the largest one on the list.** None of the below can be
judged from outside s&box:

1. ~~**Does it compile in the engine?**~~ **— yes, it does, as of this writing.** It took one fix:
   `EffigyViewport.Sculpting.cs` was missing `using Editor;`, so `Widget`, `KeyEvent` and `KeyCode`
   were all unresolved and two more errors cascaded off the broken signatures.

   **The check that missed it is worth knowing about**, because the same trap is there for the next
   editor change. Compiling the editor sources against the kernel with the s&box assemblies ABSENT
   cannot tell a missing `using` from a missing assembly — both are CS0246 — so one forgotten
   directive hid inside 920 identical-looking errors. A lint over `Editor/` for exactly that
   (any file naming a type from the `Editor` namespace must import it) is what catches it, and it
   is the first thing to run after writing editor code blind.

   Still unproven by a compile: whether the s&box calls DO the right thing. Compiling is not
   behaving, which is the whole reason for the rest of this list.
2. **Add a Sculpt feature on a box, open it from the tree menu, and drag.** Does the surface follow
   the cursor? Is the ring where the brush actually bites?
3. **The level buttons.** Coarser, finer, and one press past the top should ADD a level and say what
   it cost.
4. **X and M.** Symmetry and masking are the two shortcuts; the strip's ticks should follow them.
5. **The bake button** on a box — which has box-projected UVs, so it should REFUSE and say why. Then
   on something with clean UVs, and open the PNG.
6. **Save, close, reopen.** The deltas go to a side-car beside the `.effigy` file, and the round
   trip is tested headlessly — but the editor's own Save/Open path calling it has never run.
7. **Ctrl+Z mid-sculpt**, which undoes the stroke rather than the studio.
8. **The Edit menu's sculpt entries**: invert/clear mask, paint-vs-erase, hide masked, and the three
   normal-map settings.
9. **The eleven new glyphs at strip size.** Drawn against a nominal 18x18 box and never rendered.

~~**Deliberately not exposed**: `MultiresSculpt.RemoveTopLevel`.~~ — **stale; it was exposed once the
session could undo it.** `SculptEdit` carries a level entry, `RestoreTopLevel` is its inverse, and the
"coarser" button removes the finest level only when it is EMPTY of detail. See WHAT-IS-BUILT,
"Removing a sculpt level can be undone, so it is offered".

**Radius is on the bar, not the bracket keys**, and that is a deliberate stop rather than a choice:
nothing in this editor has ever named a `KeyCode` outside letters, Escape, Enter, Delete and
Backspace, so the bracket names would be a guess. Read the real enum out of the shipped assembly and
put them on.

**Step 8 — Masking and visibility** — **done.** `SculptMask`, paint/invert/clear, hide-by-mask, and
a mask button on the strip. Masks are per level and not persisted, which is deliberate. See
WHAT-IS-BUILT.md.

**Step 9 — Normal-map bake** *(kernel, medium)* — **done, except the part that needs eyes.**
`NormalBake` bakes cage + sculpt + UVs to a tangent-space map, with per-texel frames, mirrored-UV
handedness, edge bleed, and `Measure` for the non-overlapping-UV check this file has wanted since it
was written. 28 checks. See WHAT-IS-BUILT.md.

**What is left is the sitting**, and it is small but real: `Effigy.Tests/out/sample_normal_bake.png`
has to be looked at in s&box to settle two conventions the suite cannot judge — whether the green
channel wants flipping (`BakeOptions.FlipGreen`), and which end of the image v = 0 belongs at. Both
light a model exactly as wrongly as each other and neither shows in a thumbnail.

Also unmeasured: how often `SmoothNormal`'s fallback fires on a heavily sculpted model. It is correct
either way; nobody has counted.

**Step 10 — Reprojection for changed topology** — **done.** `SculptReprojection`, offered as an
opt-in `BoolParam` on the feature and warned about loudly when it runs, because it is lossy and the
original deltas do not survive it. See WHAT-IS-BUILT.md.

### Limits, stated up front so they are not discovered as bugs

- **Detail only goes where polygons are.** A fine crease in a large flat area needs density there,
  allocated at CAD time.
- **No topology change.** No new protrusions from nothing, no merging separate forms, no new holes.
  Substantial deformation yes.
- **Hard pulls stretch quads thin** and detail quality degrades with them.
- **4× vertices per level.** A 500-face cage at L4 is ~128k. Fine in C#, but it is the budget.

### What it needs from the CAD side

Only two things, both about the cage rather than about features:

1. **Quad-dominant output stays a hard requirement.** The risk is boolean output — but
   `EffigyMeshBoolean` has been run and returns n-gons rather than triangle soup, so that risk is
   retired. Sweep and loft are quad-only by construction. Nothing to do here; noted so it is not
   re-checked.
2. ~~**UVs on the cage, assigned at CAD time.**~~ — **also stale.** `UVProjection` and per-corner UVs
   exist, `UVUnwrap` produces non-overlapping ones where the projections do not, and `NormalBake.Measure`
   reports overlapping texels so the bake can refuse rather than write a plausible wrong map. What is
   left is the sitting, not the check.

Nothing else on the CAD list blocks sculpting, so this track and the CAD track can run in parallel.

---

## 5. Shader Forge

In the order that will actually block people:

1. **Run the tests and the probe.** `dotnet run --project ShaderForge.Tests -- out` checks block
   selection, conflict resolution and emitted-HLSL structure. Then `shaderforge_probe` in the console
   reports which assumed shader APIs actually exist.
2. **Generate one shader and get it compiling.** No test here can check this — the tests verify
   structure (vertex code lands in `VS`, uv warps precede `Material::From`, braces balance), but only
   s&box's shader compiler can judge the HLSL. Expect a few field-name corrections. The likeliest
   first failures, all still unconfirmed: **`Material::From( i )`** (`pixel_arms.shader` uses
   `Material::Init()` instead), **`m.Emission`**, **`m.Opacity`**, **`g_flTime`**.
3. **Per-slot material override** is written but unproven. The fallback message in the preview panel
   tells you which case you are in.

**There is a stale `Assets/shaders/custom/wind.shad` from an early run — delete it.**

### Still assumed rather than known

| API | Used for | Confidence |
|---|---|---|
| `File.WriteAllText` to the assets folder | writing the `.shader` | **Confirmed working** |
| `Project.Current.GetAssetsPath()` | resolving where to write | **Confirmed working** |
| `Material.FromShader( string )` | the preview material | **Confirmed callable** |
| `Material.Set( string, float/Color )` | live tweaking | Assumed; the probe reports it |
| `Shader.Load` / `Shader.Schema` | inspecting hand-written shaders | Assumed, read by reflection so a wrong shape costs one panel |
| `SceneObject.SetMaterialOverride( material, string, int )` | per-slot preview | Assumed; falls back to whole-model and says so |
| `AssetSystem.All` / `AssetType.Model` | scanning project models | Assumed; falls back to stock primitives |
| `ModelRenderer.MaterialOverride` | whole-model preview | High — RigViewport already uses it |
| `PointLight` + `.Radius` | the fill light | Assumed; no prior use in this repo |

---

## 6. Rig Control — known rough edges

Small, and each one is a real papercut rather than a missing feature:

- The example clip's **wrist never rotates** — the IK solver keeps the end bone's orientation, so the
  hand arrives without turning to face the switch.
- The tutorial's **settle step** (`frame 22`) checks only "a key exists after frame 21", so it ticks
  off whether or not you actually overshot.
- The **reference-prop step** ticks the moment a model is assigned, before it is placed anywhere
  useful.
- The **tutorial panel's finer layout** has never been judged at various dock sizes.

---

## 7. Investigations

### 7.1 The painting foundation — `wes-kay/sbox-wargame`

po: *"I THINK I ALREADY HAVE THE FOUNDATION FOR PAINTING MODELS"* — recalls having drawn on models
before, imprecisely: wrong click registration, fixed camera angle, models not built for it, using
code from `github.com/wes-kay/sbox-wargame/tree/main/Code`.

**The vision:** a Substance-Painter-like tool — material slots you paint into, raycasts that hit
exactly where aimed, brush settings, in a proper standalone tool panel rather than the in-game HUD
panel the wargame code apparently uses.

**Status: no longer blocked, still not investigated.** The block was environmental — a container with
GitHub access scoped to this repo only. Work now happens on po's own machine with ordinary network
access, so it is a plain clone away.

**What to find out:** what decal/paint mechanism it uses (render target? vertex colours? projected
decals?), how accurate its raycast-to-UV or raycast-to-triangle mapping is, and what would need to
change to (a) fix the aim accuracy and (b) lift it out of an in-game panel into an editor tool
window. **Significant enough new scope that it may deserve its own doc once investigated.**

### 7.2 Bones for Effigy-built meshes — the worked example

po's target: a cube with several rectangles sketched on its faces, each extruded into a finger-like
shape, each with its own bone — a basic animatable hand, built entirely in Effigy then rigged.

The pieces exist (`Skeleton`, `SkinBinder`, `SkinWeights`, `EffigyRigPanel`, and the DMX+VMDL export
that Rig Control opens). What has not been done is **the run**: make the palm and finger extrusions
separate bodies, create a palm/root bone, draw one bone down each finger, assign each finger body to
that bone, export, and pose it in Rig Control — **including a rebuild after keyframing**, which is
the part that proves body-id binding actually survives a parametric edit.

**The export no longer blocks this.** It did until 2026-08-31: the rigged path wrote a DMX the
compiler rejected outright ("Couldn't load DMX file" / "Node 'Body_LOD0' resolve failure"). Three
things were wrong in `DmxWriter` — two pieces of KeyValues2 punctuation and the vertex-format field
names — all now fixed and covered by `Effigy.Tests/DmxGrammarTests.cs`, which parses the output
rather than searching it for substrings. A rigged cylinder now compiles to a `.vmdl` with correct
bounds. Details and the reproducing commands are in `DmxWriter`'s class comment.

One thing that run does still have to establish:

- ~~**that the bones survive the compiler.**~~ — **established.** Bone names are not recoverable from
  a `.vmdl_c` by inspection, but the model can be loaded and asked: `rig_test_follow <model>
  __list__` names a bone that does not exist, and this project's own rig probe answers by listing
  every bone it does have. A two-bone sample compiled to `Bones: root, child`.
  **The finding worth keeping** is what came out of doing it wrong first: the same sample WITHOUT a
  `BoneMarkupList` compiled to `Bones: root` — `child` was pruned for being neither weighted nor
  animated. So the markup list is what keeps bones alive, not a precaution.
- **the UV V orientation.** `DmxWriter` writes `flipVCoordinates 0`; `fbx2dmx` writes `1` for FBX
  input. Effigy's UVs are not in FBX's convention, so copying that would be a guess either way.
  Check it against a textured model rather than reasoning about it.

**An FBX exporter is no longer needed to unblock this**, though one is being written in parallel and
may land anyway. The engine does ship `fbx2dmx.exe` and Autodesk's SDK, so FBX *is* a viable input
and the old claim in `DmxWriter` that it "is a binary format nobody should hand-write" was wrong on
both counts — handing the importer job to that SDK stays a real argument for it. But the DMX path
works now, so that is a free choice rather than a rescue. `fbx2dmx`'s immediate value turned out to
be as a **reference generator and validator** for the DMX path rather than as a format to switch to.

The initial result is intentionally rigid: each finger moves as a unit. Segmented fingers can later
use parented bones and smooth weights.

---

## 8. Publishing — read before advising on it

`.sbproj` is `Type: addon`, `Org: pooh` → ident `pooh.marionette`. Target Game deliberately empty.

**The consumption path is not proven.** Project Settings → Packages — the screen where another dev
would add `pooh.marionette` — ships with Facepunch's own warning: *"This stuff hasn't been properly
end to end tested - please don't expect it to work just yet!"* It is also unclear whether a
referenced package's `Editor/` code reaches the consumer's editor assembly at all, which is precisely
what an editor tool needs.

So the README leads with **copy these folders**, which works today. asset.party is for discovery; the
forum thread is the real distribution.

Relevant context: the s&box forums thread "AnimGraph 2" shows loud unmet demand for exactly this.
`matt` is Facepunch. `dictateurfou` is building a similar control-rig editor and has not shipped it.
`redsnail.roadtool` at 17 votes is the most-voted comparable tool, so the bar is low.

**The GPL blocker is gone.** `Editor/HaloMount` was removed from the repo in `28abc7e` and is
gitignored — see [HANDOFF.md](HANDOFF.md). Nothing GPL ships.
