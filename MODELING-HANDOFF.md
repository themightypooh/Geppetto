# Modeling tool — session handoff

Research session for a second s&box tool: **let people build usable 3D models without leaving the
editor**, the same thesis as Marionette applied to meshes instead of animation.

No code was written. This document exists so the next session starts from what was established
rather than re-deriving it.

---

## How to not waste this session

**Almost nothing here was read from engine source. Treat every API shape below as a lead, not a
fact.**

This session ran in a cloud container. The engine source that Marionette's handoff points at —

```
C:\Program Files (x86)\Steam\steamapps\common\sbox\addons\tools\Code
C:\Program Files (x86)\Steam\steamapps\common\sbox\editor\
```

— was not reachable, and neither was the API browser (`sbox.game` and `sboxcool.com` are both
blocked by the container's egress proxy; GitHub is not). So findings came from Facepunch's docs
repo (`github.com/Facepunch/sbox-docs`, read directly) and from web search snippets.

That is exactly the failure mode Marionette's handoff was written to prevent. The provenance
column in the tables below is load-bearing — **anything marked "search snippet" has the same
standing as a guess.** Read the shipped source before writing against it.

---

## The decision: parametric first, then sculpt on top

The tool is parametric hard-surface modelling **first**, with sculpting as a second stage layered
over it — not sculpting as the starting point. The reasoning, shortest form:

- **The failure modes are not symmetrical.** A mediocre parametric model is a boring crate that
  drops into a scene and works. A mediocre *sculpt-first* model is a shapeless blob with no UVs and
  no usable topology.
- **Retopology and UV unwrap are the two hardest problems in the pipeline**, they are exactly what
  a sculpt-first output demands, and they are exactly what a non-modeller cannot do. Starting
  parametric means they never come up — see the pipeline section below, this is the crux.
- **Collision comes free from parametric history.** If the model is known to be a union of N convex
  primitives, that *is* the physics representation. A sculpt gives a triangle soup needing convex
  decomposition.
- **UVs nearly come free too.** Planar/box projection per face cluster looks good on hard surface
  and terrible on organics — and the parametric stage is where you get to do it on hard surface.
- **s&box builds hard surface.** Props, guns, furniture, machinery, signage. Organic characters are
  largely served by Citizen + clothing.

Sculpting starting from nothing is the better demo and the worse tool. Sculpting *on top of a
parametric base* is neither — it is the actual goal, and the parametric stage is what makes it
work.

---

## Confirmed

Everything in this table came from Facepunch's own documentation, read directly out of the docs
repo.

| Finding | Where |
|---|---|
| ModelDoc imports **DMX, SMD, FBX, OBJ, VOX**. OBJ is on the list. | `docs/editor/model-editor.md` |
| ModelDoc's *Export As…* writes OBJ and FBX, including skinned meshes | same |
| A model that isn't fully static needs at least an `AnimBindPose` node, or morph targets and IK data silently break | same |
| `citizen.vmdl` ships as a readable source file at `sbox\addons\citizen\Assets\models\citizen\citizen.vmdl` | same |
| Scene Mapping mode (`M`) ships Primitive, Vertex, Edge, Face, Texture, Vertex Paint and **Displacement** tools. Displacement is described as *"Sculpt and displace vertices to create organic shapes."* | `docs/editor/mapping/index.md` |
| The Texture tool already does per-face material assignment plus UV align/scale/rotate | same |
| Hammer is slated for removal once scene mesh editing replaces it | s&box forums / VDC |
| Mounts synthesise models at `.vmdl` paths from arbitrary bytes via `ResourceLoader<T>` + `ResourceType.Model` | `docs/game-mounts/creating-mounts.md` |
| `AssetSystem.CreateResource` takes an **absolute** path | Marionette, `RigSampleBuilder.cs:148` |

## Unverified — leads only

| Lead | Provenance | Why it matters |
|---|---|---|
| `Model.Builder.AddMesh( mesh ).AddCollisionMesh( positions, indices ).Create()` | third-party wiki via search snippet, **not** Facepunch | the live-preview path; if the signature is wrong the whole viewport plan shifts |
| `Editor.MeshEditor.PrimitiveBuilder` exists | API-browser search snippet | would mean primitive generation is already written |
| `PolygonMesh` exists and has a `.Vertices` list | API-browser search snippet, plus a release note about it writing world-space properties into JSON and breaking prefab diffing | the single biggest unknown — see below |
| `EditorMeshComponent` is the scene-mesh component | search snippet | ditto |
| `CreateResource` accepts `"vmdl"` | **pure guess** | `vmdl` is an engine type, not a `GameResource`. May well refuse |
| The `.vmdl` KV3 node schema for `RenderMeshFile` | **pure guess** | needed to emit a vmdl at all; `citizen.vmdl` answers it in one read |

---

## The two facts that shape the tool

### 1. The export path is OBJ

`.vmdl` is a text KV3 source file that references a source mesh; ModelDoc imports OBJ; OBJ is plain
text and takes about sixty lines of C# to write, UVs and per-material groups included. So:

```
tool document  →  thing.obj  +  thing.vmdl (RenderMeshFile → thing.obj)  →  compile
```

`Model.Builder` handles the live model in the tool's viewport, OBJ+vmdl handles export. Structurally
identical to Marionette: the tool's own document type is the truth, the compiled asset is build
output.

### 2. Facepunch already shipped most of a mesh editor

Scene Mapping mode has primitives, vertex/edge/face editing, per-face materials with UV controls,
and vertex paint. **The gap is not "edit meshes in the editor" — it's that mapping meshes are not
props.** There is no clean path from "I blocked this out" to "reusable `.vmdl` with collision and
materials".

If `PolygonMesh` turns out to be usable from tool code, the tool collapses to two things worth
building:

- the modelling operations mapping lacks — bevel, subdivision surface, mirror, array, boolean
- the asset-ification step — OBJ/vmdl export, collision from the primitive history, sane material slots

That is a fraction of the work of a CAD kernel or a sculpt engine, and it is the piece nobody has.
**Whether `PolygonMesh` is reachable is therefore the question that decides the tool's size.**

---

## CAD → subdivide → sculpt: this is the plan

**Correction to an earlier draft of this document, which claimed subdivide-then-sculpt does not
work. It does. The earlier claim conflated two different things:**

| | Moves vertices | Undercuts / overhangs |
|---|---|---|
| **Displacement** (Source-style, and s&box's mapping Displacement tool) | along normals only, heightfield on a fixed grid | **no**, at any subdivision level |
| **Sculpting on a subdivided mesh** (ZBrush subdivision levels, Mudbox, Blender multires) | freely, in 3D | **yes** |

The first genuinely cannot make an ear. The second is the industry-standard workflow and is what
this tool should do. Do not let the earlier version of this section talk anyone out of it.

### Why starting from CAD is what makes the sculpt usable

This is the important part. A sculpt-first model is unusable as a game asset because it has no
low-poly version and no UVs, and getting them means retopology and unwrapping. In a subdivide-and-
sculpt pipeline **the base cage is already the low-poly, with the UVs assigned at CAD time.** So:

```
parametric base (quads, UV'd)  →  subdivide 3-4 levels  →  sculpt the dense mesh
        └────────────── bake detail to a normal map ──────────────┘
                    ship the cage + the normal map
```

No retopo step, because the parametric stage produced clean topology as a side effect. That closes
the exact objection that ruled out sculpting-first, and it is why the two halves belong in one tool
rather than two.

### What it takes

- **Quad-dominant output from the CAD stage.** Catmull-Clark needs quads; general boolean output is
  triangle soup and subdivides badly. Keep primitives quad-based and lean on grouping with
  subtract-only-where-needed — which is already the phase-one scope, so this constrains nothing that
  wasn't already constrained. (Triangle regions can fall back to Loop subdivision.)
- **A half-edge mesh and Catmull-Clark subdivision**, levels 0–4, switchable up and down.
- **Brushes** — grab, inflate, smooth, pinch, flatten, clay — with a falloff radius and an octree or
  BVH for hit-testing once past ~100k verts.
- **Multires displacement deltas**, storing the sculpt per level so the base cage stays editable
  underneath it. This is the hard part and the one that decides whether the tool feels good or feels
  like a trap. Without it, sculpting is a one-way door and the parametric history dies the moment a
  brush touches the mesh.

### Real limits, so they aren't surprises

- Detail only goes where polygons were allocated. A fine crease in a large flat area needs density
  there, at CAD time.
- Hard pulls stretch triangles thin and detail quality degrades with them.
- No new protrusion from nothing, no merging separate forms, no new holes. Substantial deformation
  yes; topology change no.
- 4× vertex count per level. A 500-triangle base at level 4 is ~128k — fine in C#.

### The SDF route, for the record

A single signed-distance field with CSG primitives and sculpt brushes both writing into it, meshed
with dual contouring, gives live-editable parametric *and* sculpting with sharp edges preserved.
It is the better end state and a much larger build — weeks to months for the meshing alone. Multires
gets most of the benefit for a fraction of the work. Revisit only if the live-both-ways property
turns out to be the actual point.

---

## Proposed phase one — the parametric base

Deliberately small, and it assumes the `PolygonMesh` question came back favourably.

1. Parametric primitives with numeric fields — box, cylinder, sphere, wedge, tube, stairs, arch.
   **Quad-dominant output is a hard requirement**, not a nicety — phase two subdivides this.
2. Modifiers over them — bevel, array, mirror, shell
3. Assembly by grouping; boolean **subtract only where required** (most props need no general CSG)
4. Planar/box-projection auto-UV per face cluster. These UVs are what the phase-two normal-map bake
   uses, so they have to be real, not placeholder.
5. Export — OBJ + vmdl, collision generated from the primitive list rather than from the triangles

Live tree throughout: change any number, the model rebuilds.

## Phase two — subdivide and sculpt

1. Catmull-Clark subdivision over a half-edge mesh, levels 0–4, switchable
2. Brushes — grab, inflate, smooth, pinch, flatten, clay — with BVH hit-testing
3. Multires displacement deltas, so the base cage survives underneath the sculpt
4. Normal-map bake from the dense mesh down onto the cage
5. Export the cage plus its normal map

Phase one is worth shipping on its own. Phase two is worth building only after phase one produces
clean quads, because it is built directly on top of them.

---

## Open questions, in priority order

1. **Reflection-dump `Editor.MeshEditor.PolygonMesh` and `EditorMeshComponent`** from a throwaway
   `[ConCmd]` — the technique that found `LocalTransform`. Constructible from tool code? Exposes
   faces and edges? Is subdivide or bevel already in there? **Does it hold n-gons/quads, or does it
   triangulate on the way in?** That last one decides whether phase two can build on it at all, since
   Catmull-Clark needs quads. *This question determines everything else.*
2. **Open `citizen.vmdl` in a text editor.** Confirm KV3, copy the `RenderMeshFile` node shape.
   One read answers the whole export format question.
3. **Does `AssetSystem.CreateResource( "vmdl", abs )` work**, or does it only accept `GameResource`
   types? If it refuses, find how ModelDoc itself writes a vmdl.
4. **Does the mapping toolbar already have "create model from selection"?** If yes, scope shrinks
   again and the tool may reduce to modifiers alone.
5. Confirm the real `Model.Builder` signature against shipped source before building the viewport on it.

Questions 1 and 2 are both cheap and both gate everything. Do them first, in that order.

---

## Environment notes

- `sbox.game` and `sboxcool.com` are **blocked by the cloud container's egress proxy**. GitHub is
  reachable, so `github.com/Facepunch/sbox-docs` is the usable docs route from a cloud session.
  API-browser lookups have to happen on a local machine.
- The `sbox` MCP server referenced in Marionette's handoff attaches to whatever project the editor
  has open — local only, and the fastest way to answer questions 1–4.
