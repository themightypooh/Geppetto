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

## The decision: parametric first, not sculpting

The tool is hard-surface modelling, not organic sculpting. The reasoning, shortest form:

- **The failure modes are not symmetrical.** A mediocre parametric model is a boring crate that
  drops into a scene and works. A mediocre sculpt is a shapeless blob with no UVs and no usable
  topology.
- **Retopology and UV unwrap are the two hardest problems in the pipeline**, they are exactly what
  a sculpt output demands, and they are exactly what a non-modeller cannot do.
- **Collision comes free from parametric history.** If the model is known to be a union of N convex
  primitives, that *is* the physics representation. A sculpt gives a triangle soup needing convex
  decomposition.
- **UVs nearly come free too.** Planar/box projection per face cluster looks good on hard surface
  and terrible on organics.
- **s&box builds hard surface.** Props, guns, furniture, machinery, signage. Organic characters are
  largely served by Citizen + clothing.

Sculpting is the better demo and the worse tool. If it ever gets built, it should sit on an SDF
core so it's an added brush set rather than a rewrite — see "Subdivide is not sculpting" below for
why the cheap version of it doesn't count.

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

## Subdivide is not sculpting

This came up as "can we subdivide faces enough to then sculpt them?" — the honest answer is no, and
the reason is structural rather than a matter of resolution.

Displacement moves existing vertices along their normals on a fixed grid. Topology never changes. So
it produces cliffs, rock faces, terrain, dented panels — and it cannot produce an undercut, an
overhang, or an ear, at **any** subdivision level. Subdividing further doesn't approach sculpting;
it quadruples vertex count per level and falls over on budget before it gets anywhere near.

Real sculpting needs one of:

- **dynamic topology** — retriangulate under the brush as it runs
- **an SDF/voxel field** remeshed with dual contouring or surface nets

Both are real engines, not a subdivide button. Worth building one day, on the SDF route so parametric
CSG and sculpt brushes share a core. Not phase one.

---

## Proposed phase one

Deliberately small, and it assumes the `PolygonMesh` question came back favourably.

1. Parametric primitives with numeric fields — box, cylinder, sphere, wedge, tube, stairs, arch
2. Modifiers over them — bevel, array, mirror, shell
3. Assembly by grouping; boolean **subtract only where required** (most props need no general CSG)
4. Planar/box-projection auto-UV per face cluster
5. Export — OBJ + vmdl, collision generated from the primitive list rather than from the triangles

Live tree throughout: change any number, the model rebuilds.

---

## Open questions, in priority order

1. **Reflection-dump `Editor.MeshEditor.PolygonMesh` and `EditorMeshComponent`** from a throwaway
   `[ConCmd]` — the technique that found `LocalTransform`. Constructible from tool code? Exposes
   faces and edges? Is subdivide or bevel already in there? *This one determines everything else.*
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
