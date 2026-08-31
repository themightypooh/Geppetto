# Effigy sculpting — design and build order

**Status: nothing in this document is built.** This is the plan for the second half of the tool,
written so a session picking it up cold can start at step 1 without re-deciding anything.

Read `../MODELING-HANDOFF.md` first, specifically the "CAD → subdivide → sculpt: this is the plan"
section. That document decides *what* the sculpt stage is and *why*; this one decides *how it is
built and in what order*. Where the two disagree, MODELING-HANDOFF wins on intent and this one wins
on mechanics.

---

## The one-paragraph version

The parametric stage already produces a clean quad cage with UVs. Catmull-Clark already turns that
cage into something dense. Sculpting is the third piece: brushes that move the dense vertices, with
the displacement stored **as per-level deltas against the cage** rather than baked into it — so the
cage stays parametric and editable underneath the sculpt, and the feature history does not die the
moment a brush touches the mesh. The cage is the low-poly and carries the UVs, so shipping is
"export the cage plus a normal map baked off the dense mesh" with no retopology step anywhere.

```
PartStudio history ──► cage (quads, UV'd) ──► subdivide L0..L4 ──► + deltas ──► sculpted mesh
        ▲                    │                                                        │
        │                    └────────────────── ship this ──────────┐                │
        └── still editable                                           └── bake normal map
```

---

## Decisions already made, so they are not re-argued

| Question | Answer | Why |
|---|---|---|
| Sculpt-first or CAD-first? | CAD-first | A sculpt-first model has no low-poly and no UVs. See MODELING-HANDOFF. |
| Multires or SDF/dual-contouring? | **Multires** | SDF is the better end state and weeks-to-months of work for the meshing alone. Multires gets most of the benefit for a fraction of it. |
| Half-edge mesh? | **No** | `PolyMesh.cs` argues adjacency-on-demand is enough, and half-edge is easy to corrupt. Its stated switch condition is "interactive *per-element* editing arrives" — sculpting is **not** that. Sculpting moves positions and never changes topology (see Limits), so the whole reason to switch does not apply. Do not rewrite the mesh. |
| Dynamic topology (dyntopo/voxel remesh)? | **No** | It destroys the cage correspondence, which is the entire value proposition. If a shape needs topology the cage does not have, that is a CAD-stage fix. |
| Where does brush code live? | The engine-free kernel | Same rule as everything in `Effigy/`. A brush is a pure function over a mesh and a stroke, which means it is testable headlessly, which is how everything else here got correct. |

---

## Limits, stated up front so they are not discovered as bugs

- **Detail only goes where polygons are.** A fine crease in a large flat area needs density there,
  allocated at CAD time.
- **No topology change.** No new protrusions from nothing, no merging separate forms, no new holes.
  Substantial deformation yes.
- **Hard pulls stretch quads thin** and detail quality degrades with them.
- **4× vertices per level.** A 500-face cage at L4 is ~128k. Fine in C#, but it is the budget.

---

## Architecture

### The data

```
SculptLayer                 one sculpt, living on one Body
├── BaseCageHash            what cage these deltas were authored against
├── Levels[0..N]            per level:
│     ├── Deltas: Vec3[]    one per vertex AT THAT LEVEL, in local frame
│     └── Dirty flags
└── Symmetry                plane + enabled
```

**Deltas are stored in a per-vertex local frame, not in world space.** This is the single decision
that makes the sculpt survive a parametric edit. If a delta is world-space and you go back and make
the cage 20% taller, every sculpted detail stays where it was and slides off the surface. Stored as
(normal, tangent, bitangent) coefficients, the detail rides the surface as the surface moves.

The frame must be **deterministic from the mesh alone** — derived, never stored, so it cannot drift
out of sync. Normal is the existing `ComputeVertexNormals()`. Tangent is the direction to the
lowest-indexed adjacent vertex, orthonormalised against the normal. Ugly but stable, which is the
only property that matters.

### The correspondence problem, and the one prerequisite change

Multires needs to know, for every vertex at level N+1, exactly which level-N element it came from.
`CatmullClark.SubdivideOnce` **almost** provides this already — its vertex layout is deterministic
and documented in the source:

```
[0 .. V)            updated original vertices
[V .. V+E)          edge points
[V+E .. V+E+F)      face points
```

The gap: the edge block's ordering comes from `foreach ( var key in edgeFaces.Keys )` — i.e.
`Dictionary` enumeration order. In practice that is insertion order for a dictionary with no
removals, but it is an implementation detail and not a contract, and a sculpt is persisted data
that has to survive a re-run, a re-build and a .NET upgrade. **Step 1 below makes that ordering an
explicit, sorted, guaranteed contract.** It is a small change and everything else rests on it.

### How a sculpt re-applies after a parametric edit

1. History rebuilds the cage as normal. `SculptFeature` is the last feature on that body.
2. Compare the new cage against `BaseCageHash` (vertex and face counts plus topology, not
   positions — positions are *expected* to change).
3. **Topology unchanged** → subdivide the new cage, re-derive frames, re-apply deltas. The sculpt
   follows the edit. This is the good path and should be the overwhelmingly common one.
4. **Topology changed** (a segment count went up, a feature was inserted) → the deltas no longer
   correspond. Do not silently drop them and do not silently misapply them. Keep them, mark the
   layer stale, and offer reprojection: raycast the new dense surface against the *old* sculpted
   surface and re-derive deltas from the hits. Lossy, honest, and better than either alternative.

Step 4 is the expensive one to build and it is deliberately last. Until it exists, the right
behaviour is a clear "this edit changed the cage topology; the sculpt cannot follow it" refusal.

### Where it sits in the history

`SculptFeature : Feature`, consuming one body, like `ShellFeature` does. It is not a normal feature
in one respect: its "parameters" are megabytes of deltas rather than a handful of numbers, so it
does not go in the parameter dialog and it does not serialise as JSON alongside everything else.
Deltas go to a side-car binary blob keyed by feature id, quantised to 16 bits per component against
a per-level bounding box. At L4 on a 500-face cage that is ~128k × 6 bytes ≈ 750 KB per level.

---

## Build order

Each step is independently testable and leaves the tool working. Steps 1–5 are pure kernel and can
be built and verified **headlessly with no s&box at all** — `cd Effigy.Tests && dotnet run -- out`.
That is the whole reason the kernel is engine-free, and it is how the sculpt stage should be built:
get the maths right where a test can see it, then put a cursor on it.

### Step 1 — Stable subdivision correspondence *(kernel, small)*

Make the edge ordering in `SubdivideOnce` an explicit contract: sort `edgeList` by `(A, B)` instead
of taking dictionary order, and say so in the comment. Add `CatmullClark.SubdivideWithMap` returning
a `SubdivisionMap` that states, per output vertex, whether it is an original / edge / face point and
which one.

*Test:* the same cage subdivided twice produces identical maps; a map's parent indices are in range;
round-tripping a known cage gives the documented layout.

### Step 2 — Local frames and the delta round-trip *(kernel, small)*

`SculptFrames.Build( PolyMesh )` → per-vertex orthonormal basis, deterministic. `SculptLayer.Apply`
and `SculptLayer.Capture` converting between world positions and frame-space deltas.

*Test:* capture-then-apply is the identity to tolerance. **Then the one that matters:** capture a
delta, scale the cage, re-apply — the detail is still on the surface and still the right size
relative to it. That test is the multires promise, and if it fails nothing downstream is worth
building.

### Step 3 — Spatial queries *(kernel, medium)*

A BVH over faces supporting ray hit-test and radius query. `MeshRaycast.cs` already exists and is
presumably linear; at 128k vertices a linear scan per stroke sample is not viable.

Refit rather than rebuild between stroke samples — a sculpt moves vertices but never changes
topology, so the tree structure stays valid and only the bounds need updating. That is the payoff
for refusing dyntopo, and it is worth taking.

*Test:* BVH ray hits agree with the existing linear raycast on every sample mesh; radius query
returns exactly the brute-force set; a refit after displacement still returns correct results.

### Step 4 — Brushes *(kernel, medium)*

```csharp
Brush.Apply( PolyMesh mesh, BrushStroke stroke, SculptFrames frames, float[] mask )
```

A `BrushStroke` is a list of samples (position, normal, radius, strength, direction) — the editor
produces them, the kernel consumes them, and the kernel never learns what a mouse is.

Build in this order, easiest and most useful first: **Smooth** (Laplacian, and the one you reach for
constantly), **Draw/Clay** (along normal), **Inflate** (along per-vertex normal), **Grab** (rigid
translate with falloff, no re-projection), **Flatten** (to a fitted plane), **Pinch** (toward the
stroke axis). Falloff is a shared curve — smooth, linear, sharp, constant.

*Test:* every brush is volume- and area-sane on a known sphere; smooth strictly reduces a curvature
metric; grab with zero strength is the identity; a stroke with symmetry produces a mesh symmetric
to tolerance. `Effigy.Tests` already writes OBJ samples — write a sculpted one per brush and eyeball
it in Blender once.

### Step 5 — Multires level management *(kernel, medium)*

Add a level, drop to a lower level, move between them with deltas preserved. Adding level N+1
subdivides the *displaced* level-N mesh and starts N+1's deltas at zero. Going down displays fewer
levels; it does not discard the higher ones.

*Test:* sculpt at L3, drop to L1, return to L3 — the mesh is unchanged. Sculpt at L1 after
sculpting at L3 — the L3 detail rides the L1 change rather than being flattened by it. That second
one is the whole feature.

### Step 6 — `SculptFeature` and persistence *(kernel + document, medium)*

The feature, the side-car blob, the quantisation, the `BaseCageHash`, and the honest refusal when
topology changed. Round-trip through `StudioDocument`.

*Test:* save, load, rebuild — identical mesh. Edit an upstream parameter — the sculpt follows. Edit
an upstream *segment count* — a clear staleness state, not a silent wrong answer.

### Step 7 — The editor *(s&box, large, cannot be verified headlessly)*

Brush cursor projected on the surface, stroke capture with sample coalescing, a level slider that
shows the cost table the kernel already computes, brush/radius/strength UI in the existing floating
strip idiom, symmetry toggle.

**Everything from punch-list item 4 applies here**: this half has to be run on po's machine before
any of it is believed. Budget real time for it — the CAD UI is the evidence for how much back-and-
forth widget work takes.

### Step 8 — Masking and visibility *(kernel + editor)*

Paint a mask, invert it, hide by mask. Genuinely useful and genuinely optional; it goes after the
tool works, not before.

### Step 9 — Normal-map bake *(kernel, medium)*

Cage + sculpted mesh + the cage's existing UVs → tangent-space normal map. This is what makes the
whole pipeline pay off: ray from each texel's cage position along the cage normal, hit the sculpted
mesh, encode the difference. `UVProjection.cs` and the step-3 BVH between them have most of it.

*Test:* a cage with a known bump sculpted into it bakes to a map whose centre pixel points the way
the bump does. Then look at one in the engine, because a normal map is a thing you have to see.

### Step 10 — Reprojection for changed topology

The step-4 fallback from "How a sculpt re-applies", built once everything else is real.

---

## What this depends on from the CAD side

Only two things, and both are about the cage rather than about features:

1. **Quad-dominant output stays a hard requirement.** Catmull-Clark makes every input n-gon into n
   quads, so a triangle becomes three valence-3 extraordinary vertices that pucker visibly under a
   brush. Boolean output is the risk here — `EffigyMeshBoolean` goes through the engine and there is
   no guarantee about what it hands back. **Check what a boolean actually returns before trusting a
   cut in a sculpt base**, and if it is triangle soup, that is a quad-remesh-after-boolean job.

   Sweep and loft are now also cage sources, and both are quad-only by construction — `Skinner`
   emits nothing but quads for the walls, and the two end caps are single n-gons that subdivide
   cleanly. Nothing to do there; noted so it does not get re-checked.
2. **UVs on the cage, assigned at CAD time.** `UVProjection` and per-corner UVs exist. The bake in
   step 9 needs them non-overlapping, which nothing currently checks.

Nothing else on the CAD list blocks sculpting. As of 30 August 2026 the kernel-side CAD gaps are
mostly closed anyway — sweep, loft, ellipses, splines, trim/extend/offset/fillet and the tangency
constraints all landed, at 1381 passing checks — so the cages available to sculpt on are already
much better than this plan assumed. What is left on that side (rounded fillets, draft, a hole
feature, and all of the UI) makes nicer cages still, but none of it is a prerequisite: this track
and the CAD track can run in parallel, which is the point of writing them down separately.

---

## The honest risk list

- **Step 2's scale test is the gate.** If frame-space deltas do not survive a cage edit cleanly,
  multires is not delivering the thing it was chosen for, and the plan needs revisiting before
  steps 3–10 are built on top of it. Do that test early and take it seriously.
- **Performance is unmeasured.** 128k vertices in C# with a per-stroke working set of a few
  thousand should be fine, and "should be fine" is not a measurement. Put a stopwatch in the step-4
  tests from the first brush, not after step 7 makes it feel bad.
- **Undo memory.** A naive undo snapshots the whole delta array per stroke. Store per-stroke
  affected-vertex diffs instead, and decide it at step 4 rather than retrofitting it.
- **The editor half is the long pole**, as it was for CAD. Steps 1–6 are perhaps a third of the
  calendar time despite being most of the intellectual content.
