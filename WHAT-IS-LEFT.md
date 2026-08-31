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

---

## 1. Effigy kernel

The kernel is roughly **92% of phase one**. All of it is headless-testable — no s&box anywhere.

### 1.1 Exercise the boolean past the one case that works

*Highest value, lowest effort, and the only item here whose outcome could change the others.*

One hole in one box is a proven **path**, not a proven envelope. `MeshHoleRepair` is deliberately
conservative — it declines any boundary loop it cannot place in exactly one coplanar face — so each
of these is unexercised and at least one is likely to fail:

- a cut through a **curved** face (the mouth is not planar, so `FindContainingFace` finds nothing
  and declines; the repair will need per-face loop splitting rather than one whole loop)
- a cut meeting an edge, so the mouth spans two faces (same failure; the honest fix is to split the
  loop where it crosses an edge)
- two cuts overlapping, and cutting a body that has already been cut
- a cut that separates the body into two pieces — nothing downstream expects one body to become two

**Method:** build each in the editor and run `effigy_dump_tree`. `boundary edges`, `bridged faces`
and `opening(s) reinstated` name the failure mode directly. Where a case fails, reproduce the mesh
shape as a fixture in `HoleTests` — `TestBoundaryLoopRepair` is the template, it hand-builds the
defect rather than needing an engine — and fix against that.

**Do not measure this by eye.** All four bugs fixed in the boolean produced closed, manifold,
Euler-correct, valid meshes.

### 1.2 Rounded (multi-segment) fillets

*The one CAD capability deliberately never attempted.*

`Bevel` is a flat chamfer. The obvious move — give its bridging pass N segments on an arc instead of
one quad — does not work as a local change, and the reason is in `Bevel.cs`'s class comment: a bevel
is explicitly **not** local to the selected edge, and the vertex-cap pass builds its n-gon from every
distinct point converging on a vertex. Arc points threaded into a bridge without being threaded into
that cap in the right cyclic order leave T-junctions, which pass closed, manifold and Euler checks
while rendering wrong.

**Method:** rework the cap pass and the bridge pass **together**, not one then the other. Order the
arc points into the cap's cyclic sequence as they are generated. Verify with `RenderCheck` — this is
exactly the class of defect it exists for, and exactly the class the numeric suite cannot see. The
20× width corner cap stays; a fillet has the same nearly-collinear blowup a chamfer does.

### 1.3 Collision from the primitive history

*Nothing exists — `Collision` appears nowhere in the kernel.*

A model known to be a union of N convex primitives **is** its own physics representation, so this is
bookkeeping rather than geometry.

**Method:** walk the feature tree rather than the finished mesh. A `PrimitiveFeature` contributes its
own shape and transform; a pattern or mirror contributes copies; anything that has been through a
boolean or a subdivide falls back to a convex hull of its body, or to the mesh itself. Emit a list of
convex shapes, not triangles. Testable headlessly by volume and count.

### 1.4 Draft on existing faces

*Well defined, small, and genuinely absent.*

Extrude has `Taper`, which covers a face being **made**. Drafting faces of a solid that already
exists does not exist at all.

**Method:** pick faces plus a neutral plane and a pull direction. Move each vertex along the
horizontal component of its own normal, proportional to its signed distance from the neutral plane.
Refuse self-intersection with the three checks `LoopOffset` already uses — signed area keeps its
sign, it has not collapsed, no edge reversed — because the third catches the inside-out case the
first two call healthy.

### 1.5 A hole feature

*Convenience, not capability.*

Counterbore and countersink as a tool solid emitted with `Result = Remove`. Holes already work as
inner loops of a profile and cuts now work, so this is a parameterised shape and a dialog. It cannot
build in the headless suite without a boolean provider — `MergeTests` installs a stub for exactly
this, do the same.

---

## 2. Effigy editor — the bigger gap

The kernel can do things the tool cannot reach. This is now the larger half of the project.

### 2.1 Toolbar entries for sweep and loft — the cheapest item in the repo

Both features are built, volume-tested, and reachable from **nothing**. The feature strip has
thirteen buttons and `ToolKind` has thirteen entries; Sweep and Loft are in neither.

This was previously written up as blocked on a sketch picker "which nothing in the tool has yet".
That was wrong twice over: `EffigySketchSelector` (`EffigyFeatureDialog.cs:1244`) exists and arms for
any `SketchConsumingFeature`, which both are — and neither strictly needs it, because an empty
`SweepFeature.PathSketchId` means "the sketch before the profile's" and a `LoftFeature` with fewer
than two `Sections` lofts every sketch available.

**Method:** a `ToolKind` entry, a `CreateTools` row and an icon each, in `EffigyWindow.cs`. Do this
before anything harder. The refinements each would still want — a path selector for sweep, an ordered
section list for loft — are follow-ups, not prerequisites.

### 2.2 Wire the six unreachable constraints

*An hour, and it has been open for several sessions.*

`SketchConstraintKind` has **seventeen** kinds. `ConstraintTools` — which turns a selection into the
constraints it allows — offers **eleven**. The six with no way to reach them, named as the enum
spells them: `Diameter`, `Midpoint`, `Concentric`, `Fixed`, `Tangent`, `TangentArcs`. All six solve,
all round-trip through the file.

**Method:** one case each in `Offers`, one menu entry each in `EffigyViewport.Constraints.cs`. This
is kernel work before it is editor work.

### 2.3 The missing sketch tools

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

### 2.4 Hide affordances for planes and origin

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

### 2.6 Better sketch-strip icons, and the glyph-scaling gap

The sketch strip has safe, non-blank, but **generic** font glyphs (`Paint.DrawIcon`, classic
Material Icon names), not the hand-drawn CAD-operation-specific style `EffigyIcons` uses for the
feature strip — where Bevel shows a corner being cut and Shell shows a wall inside a shape. Drawing
~14 more in that style is real design work: line, rectangle ×2, circle ×2, arc ×2, polygon ×2, slot,
point, construction, profile inspector, finish.

Alongside it: the hand-painted `EffigyIcons` glyphs are still drawn at their original nominal 18px
weight rather than scaled with the bigger 40×40 button, so a glyph sits slightly small inside it.
`Paint`'s scaling API was not confirmed safe to guess at from the old environment — it can now be
read out of the shipped Base Editor Library instead.

### 2.7 Smaller editor gaps

- **Revolve's axis is typed Vec3 only**, and its default runs through the sketch origin — which is
  where people draw — so the first press on a normal sketch reliably errors. The error now names how
  far the profile reaches either side, but the real fix is picking the axis in the viewport. This is
  the clearest case of a tool that looks *broken* rather than unfinished.
- **Extrude's region choice** has kernel support (`RegionSeed`) and no UI.
- **Mirror plane and pattern axis/direction** are typed Vec3. Usable, and a much lower priority.
- **Per-part hide/show** is not implemented rather than stubbed: the viewport previews one merged
  mesh (`PartStudio.ToMesh`), so there is nothing per-body to hide yet.
- **The view cube is a text label, not clickable.**
- **The preview panel's "load an existing .shader"** in Shader Forge takes a typed path.
  `RigControlWindow.OpenPicker` is the precedent for a real asset picker.

---

## 3. Rigging — what remains

1. **Weight painting**, to fix what auto-weighting gets wrong by hand. Not started, and the one item
   on the phase-two list with no progress at all.
2. **`AnimBindPose`** is **unverified, not missing.** ModelDoc's docs say a non-static model needs
   one or morph targets and IK data silently break, but nothing in this repo has seen its real KV3
   shape, and a guessed one risks breaking a compile that currently works. **Method:** a real editor
   session against `citizen.vmdl`, or the Model Editor's own sequence UI. Not a guess.
3. **The Effigy rig panel's remaining reporting** — zero-length bones, missing mapped bones, and
   failed `SkinWeights.Validate()` results should surface as warnings.
4. **Effigy → Rig Control convenience**: an action to create the `.ctrlrig` and open Rig Control.
   Integration sugar, explicitly not a priority — constraints and animation stay in Rig Control's
   assets.

**Non-goals, so they are not re-proposed:** no Effigy timeline or duplicate FK/IK implementation; no
vertex-index rig storage; no SMD import back into the parametric document; no heat-diffusion solver.

---

## 4. The sculpt stage

**Nothing in this section is built.** This is the plan for the second half of Effigy, written so a
session picking it up cold can start at step 1 without re-deciding anything. Steps 1–5 are pure
kernel and verifiable headlessly, which is how they should be built: get the maths right where a
test can see it, then put a cursor on it.

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

**Step 1 — Stable subdivision correspondence** *(kernel, small)*. `SubdivideOnce`'s vertex layout is
already deterministic and documented — `[0..V)` updated originals, `[V..V+E)` edge points,
`[V+E..V+E+F)` face points — but the edge block's ordering comes from `Dictionary` enumeration. In
practice that is insertion order; it is an implementation detail, not a contract, and a sculpt is
persisted data that has to survive a re-run, a rebuild and a .NET upgrade. Sort `edgeList` by
`(A, B)`, say so in the comment, and add `SubdivideWithMap` returning a `SubdivisionMap` stating per
output vertex whether it is an original / edge / face point and which one. Everything else rests on
this.

**Step 2 — Local frames and the delta round-trip** *(kernel, small)*. `SculptFrames.Build`,
`SculptLayer.Apply`/`Capture`. *Test:* capture-then-apply is the identity to tolerance. **Then the
one that matters:** capture a delta, scale the cage, re-apply — the detail is still on the surface
and still the right size relative to it. **That test is the multires promise, and it is the gate: if
frame-space deltas do not survive a cage edit cleanly, multires is not delivering the thing it was
chosen for and the plan needs revisiting before steps 3–10 are built on it.**

**Step 3 — Spatial queries** *(kernel, medium)*. A BVH over faces with ray hit-test and radius
query. `MeshRaycast` exists and is linear; at 128k vertices a linear scan per stroke sample is not
viable. **Refit rather than rebuild** between samples — sculpting never changes topology, so the tree
structure stays valid and only bounds need updating. That is the payoff for refusing dyntopo.

**Step 4 — Brushes** *(kernel, medium)*. `Brush.Apply( mesh, stroke, frames, mask )`. A
`BrushStroke` is a list of samples (position, normal, radius, strength, direction) — the editor
produces them, the kernel consumes them, and the kernel never learns what a mouse is. Order, easiest
and most useful first: **Smooth** (Laplacian, and the one you reach for constantly), **Draw/Clay**,
**Inflate**, **Grab**, **Flatten**, **Pinch**. *Test:* volume- and area-sane on a known sphere;
smooth strictly reduces a curvature metric; grab at zero strength is the identity; symmetry produces
a symmetric mesh. **Put a stopwatch in these tests from the first brush**, not after step 7 makes it
feel bad. **Decide undo here too** — a naive undo snapshots the whole delta array per stroke; store
per-stroke affected-vertex diffs instead, rather than retrofitting it.

**Step 5 — Multires level management** *(kernel, medium)*. Adding level N+1 subdivides the
*displaced* level-N mesh and starts N+1's deltas at zero. Going down displays fewer levels; it does
not discard the higher ones. *Test:* sculpt at L3, drop to L1, return — unchanged. Sculpt at L1 after
sculpting at L3 — the L3 detail **rides** the L1 change rather than being flattened by it. That
second one is the whole feature.

**Step 6 — `SculptFeature` and persistence** *(kernel + document, medium)*. It consumes one body like
`ShellFeature` does, but its "parameters" are megabytes of deltas rather than a handful of numbers,
so it does not go in the parameter dialog and does not serialise alongside everything else. Deltas go
to a side-car binary blob keyed by feature id, quantised to 16 bits per component against a per-level
bounding box — at L4 on a 500-face cage, ~128k × 6 bytes ≈ 750 KB per level.

**Step 7 — The editor** *(s&box, large)*. Brush cursor projected on the surface, stroke capture with
sample coalescing, a level slider showing the cost table the kernel already computes,
brush/radius/strength UI in the existing floating-strip idiom, symmetry toggle. **This is the long
pole**, as it was for CAD — steps 1–6 are perhaps a third of the calendar time despite being most of
the intellectual content.

**Step 8 — Masking and visibility.** Paint a mask, invert it, hide by mask. Genuinely useful and
genuinely optional; after the tool works, not before.

**Step 9 — Normal-map bake** *(kernel, medium)*. Cage + sculpted mesh + the cage's existing UVs →
tangent-space normal map. This is what makes the whole pipeline pay off. Ray from each texel's cage
position along the cage normal, hit the sculpted mesh, encode the difference. `UVProjection` and the
step-3 BVH have most of it. *Test:* a cage with a known bump bakes to a map whose centre pixel points
the way the bump does — then look at one in the engine, because a normal map is a thing you have to
see.

**Step 10 — Reprojection for changed topology.** Raycast the new dense surface against the *old*
sculpted surface and re-derive deltas from the hits. Lossy, honest, and better than either
alternative. Deliberately last.

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
2. **UVs on the cage, assigned at CAD time.** `UVProjection` and per-corner UVs exist. The bake needs
   them **non-overlapping**, which nothing currently checks.

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
