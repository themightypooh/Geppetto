# Modeling tool in Godot — session handoff

Companion to `MODELING-HANDOFF.md`, which works the same idea through s&box. This one asks whether
the tool should be built in Godot instead.

Short version: **Godot is the better engineering bet and the worse strategic one.** Everything hard
about the s&box version is already solved and documented in Godot; everything that made the idea
*worth doing* is weaker there. The rest of this document is the evidence for that sentence.

---

## Provenance — better than last time

The s&box handoff carries a warning that almost nothing in it was read from engine source. That
warning does not apply here. Godot is open source, its class documentation is generated from XML in
the engine repo, and GitHub is reachable from a cloud session even though `docs.godotengine.org`
itself is egress-blocked.

So: **read `raw.githubusercontent.com/godotengine/godot/master/...` instead of the docs site.** The
class XML under `doc/classes/` and `modules/*/doc_classes/` is the same text the docs site renders,
straight from the source of truth. Everything in the API table below was read that way.

Current version appears to be **4.7** — Cyclops Level Builder 1.5.0 lists it as its minimum. Confirm
before relying on it.

---

## What carries over unchanged

None of this is engine-specific. It was argued out in the s&box doc and survives the move intact:

- **Parametric over sculpting.** Asymmetric failure modes, retopo/UV being exactly what the target
  user cannot do, collision falling out of the primitive history, planar UV projection working on
  hard surface and not on organics.
- **Subdivide is not sculpting.** Displacement moves vertices on fixed topology, so it fails at
  every subdivision level rather than just low ones. Real sculpting needs dynamic topology or an
  SDF field. Pure geometry, no engine involved.
- **The architecture.** Own document type is the truth; live preview while editing; a bake/export
  step producing the engine's native asset.

What changes is only the API layer underneath. That is worth holding onto — see "Keep the kernel
portable" near the end.

---

## Verified Godot API

Read from the engine repo's own doc XML, not from memory.

| API | Signature | Note |
|---|---|---|
| `CSGShape3D.bake_static_mesh()` | → `ArrayMesh` | *"Returns a baked static ArrayMesh of this node's CSG operation result. Materials from involved CSG nodes are added as extra mesh surfaces."* Material slots handled for you |
| `CSGShape3D.bake_collision_shape()` | → `ConcavePolygonShape3D` | Empty unless called on a CSG **root**. Concave = **static bodies only** — see the trap below |
| `CSGShape3D.get_meshes()` | → `Array` | |
| `CSGShape3D.is_root_shape()` | → `bool` | |
| `ArrayMesh` / `SurfaceTool` | — | Mesh construction, documented and stable |
| `ResourceSaver.save()` | — | Writes an `ArrayMesh` straight to `.res`/`.tres`. No compile step, no source-file indirection |

Shipped CSG node types: `CSGBox3D`, `CSGCylinder3D`, `CSGSphere3D`, `CSGPolygon3D`, `CSGMesh3D`,
`CSGCombiner3D`, with union/subtraction/intersection.

**Engine's own warning, quoted:** *"CSG nodes are only intended for prototyping as they have a
significant CPU performance cost. Consider baking final CSG operation results into static
geometry."*

### The export problem inverts

This is the single biggest technical difference. s&box demands OBJ + a hand-written KV3 `.vmdl` +
an asset compile, with the schema unverified and `CreateResource( "vmdl", … )` an open question.
Godot is:

```gdscript
var mesh := csg_root.bake_static_mesh()
ResourceSaver.save( mesh, "res://props/thing.res" )
```

Two documented calls. The hardest open question in the s&box plan is a solved problem here.

*(Minor known wart: procedurally saved meshes come out larger on disk than imported equivalents —
a forum report, unquantified. Check it if file size matters.)*

### Trap: baked collision is static-only

`bake_collision_shape()` returns a **ConcavePolygonShape3D** — a trimesh. Trimesh collision does not
work on dynamic rigid bodies in Godot. So the engine's own bake gives you collision that cannot be
used on anything that moves.

Keeping the parametric history solves this and Godot's built-in path doesn't: a model known to be a
union of N convex primitives yields a convex decomposition directly, which *does* work on rigid
bodies. **That is a real, demonstrable advantage of the tool over the built-in workflow** — worth
building early, because it is easy to explain and immediately visible.

---

## The competitive picture

s&box has nothing in this space and a low bar. Godot does not — but the gap is narrower and more
specific than expected.

**Cyclops Level Builder** (`blackears/cyclopsLevelBuilder`) is the serious incumbent: convex-block
level blocking in the viewport, material assignment, collision on every block, a command
do/undo architecture, QuickHull, UV transform dock. Actively maintained, requires Godot 4.7+.

Its own design doc lists the limits:

- **convex blocks only**
- **no beveling, no subdivision, no boolean operations, no extrusion**
- single UV layer, triplanar projection only, UVs not individually settable

Others in the space — **Blockout** (Godot 3.x), **Codex** (LevelTile nodes → optimised meshes) — are
level-blocking tools too.

So the whole field is **blockout**, and the missing half is **modelling**: the modifier set, and
turning the result into a prop asset rather than level geometry. That is precisely the phase-one
scope from the s&box document, which is a good sign that the scope was aimed at something real.

The other side of the same fact: Cyclops occupies the mindshare and the obvious name. A new tool has
to read as a different category on sight, not as "another blockout plugin."

---

## The strategic argument

**For Godot:**

- Export is two documented calls instead of an unverified file format
- The CSG core is shipped, documented, and has bake methods
- Editor plugin API is documented and stable — `EditorPlugin`, `EditorUndoRedoManager`,
  `EditorNode3DGizmoPlugin`, `EditorInspectorPlugin`. None of the reflection-dumping that Marionette
  needed
- Distribution works: Asset Library + GitHub. s&box's package-reference screen still ships with
  Facepunch's own *"please don't expect it to work just yet"* warning
- Vastly more users; MIT; no gatekeeper

**Against Godot — and this is the part that actually decides it:**

- **The pain that justifies the tool is much smaller.** Marionette exists because s&box had no
  in-editor animation path and the Blender round trip was unbearable. Godot's Blender pipeline is
  genuinely good, including native `.blend` import. A Godot modelling tool cannot win by being the
  only option; it has to be *better than Blender at a specific job*. That is a much higher bar.
- **Audience mismatch.** "Players make usable models without leaving the editor" is a GMod-lineage
  cultural fit. Godot's users are developers, many with a Blender workflow already.
- **It abandons a position that already exists.** s&box has loud unmet demand, a low bar, and
  Marionette already standing in it.
- Most Godot editor plugins are GDScript; the C# editor-tool experience transfers only partly.

**Verdict:** if the goal is reach and a pleasant build, Godot. If the goal is filling a hole nobody
else will fill, s&box. The s&box version is a harder build with a clearer reason to exist.

---

## If it goes to Godot, the product has to be this

Not another blockout tool. The one-line pitch is **parametric props that stay editable and bake to
real assets** — and the three things that make it not-Cyclops:

1. **The modifier set nobody has** — bevel, subdivision surface, mirror, array, shell, extrude.
   Straight into the stated gap.
2. **Convex-decomposition collision from the primitive history**, so baked props work on rigid
   bodies, which `bake_collision_shape()` cannot deliver.
3. **Props, not level geometry** — a live parametric tree that bakes to a saved `ArrayMesh` asset,
   reusable and re-editable, not blocks welded into a room.

Ride `CSGCombiner3D` for the boolean core rather than writing one. It is slow, but it is slow at
*edit* time and bakes out, which is exactly the tradeoff a parametric tool wants.

---

## Keep the kernel portable

The decision does not have to be made now, and it is cheap to defer if the code is arranged for it.

Engine-agnostic — plain data and maths, no engine types:

- parametric primitive definitions and their tessellation
- the modifier stack and its evaluation graph
- bevel, subdivision, mirror, array
- planar/box UV projection
- convex decomposition from the primitive list
- the document format and its undo model

Engine-specific, and genuinely small:

- viewport rendering and gizmos
- the property panel
- undo integration
- export (`ArrayMesh` + `ResourceSaver` / OBJ + `.vmdl`)

**Write the kernel against arrays of floats and ints and nothing else.** Then the port is glue, and
the engine question becomes reversible rather than a bet.

---

## Open questions, in priority order

1. **Is `CSGCombiner3D` fast enough to drive a live parametric tree?** The engine calls it a
   significant CPU cost. Build the ugliest possible test — twenty nested boolean primitives, drag a
   parameter, watch the frame time. *This gates riding CSG at all.*
2. **What does `bake_static_mesh()` topology actually look like?** Triangle count and whether coplanar
   faces merge. If the output is a mess, the modifier set has to run before the bake, not after.
3. **Does `ResourceSaver.save()` on a baked `ArrayMesh` round-trip cleanly** — reopen, re-edit,
   reimport — and how bad is the file-size wart?
4. **GDScript or C#** for the plugin. Ecosystem convention is GDScript; existing skills say C#.
   Check how well C# editor plugins are actually supported in 4.7 before committing.
5. Confirm Godot 4.7 is current and that Cyclops is still the only serious incumbent.

Questions 1 and 2 together decide whether this is a small tool on top of shipped CSG or a full mesh
kernel. Do them first.

---

## Environment notes

- `docs.godotengine.org` is **egress-blocked** from the cloud container, as are `sbox.game` and
  `sboxcool.com`. GitHub is not. Read Godot class docs from
  `raw.githubusercontent.com/godotengine/godot/master/doc/classes/*.xml` and
  `modules/*/doc_classes/*.xml` — same content, reachable.
- `github.com/blackears/cyclopsLevelBuilder` is worth reading properly before writing anything. Its
  `doc/design.md` is a candid architecture write-up and its command/undo pattern is a solved version
  of a problem this tool will have.
