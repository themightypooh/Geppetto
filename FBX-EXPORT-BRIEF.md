# Brief: get Effigy's export into a format the model compiler actually loads

**A temporary task doc.** The repo deliberately runs on four docs (commit `1a2bd4f`, "four docs
instead of eleven"). This is a fifth on purpose, scoped to one job, to be handed to a session cold.
When the work lands, fold the outcome into `WHAT-IS-BUILT.md` / `WHAT-IS-LEFT.md` and **delete this
file** rather than letting it become a fifth permanent doc.

Read `HANDOFF.md` first. Especially "the engine's own source is on disk. Read it before writing
against any API" — this brief exists because that rule was followed for the DMX *attribute names*
and skipped for the DMX *header*.

---

## 1. The problem, stated exactly

Effigy has three writers — `ObjWriter`, `SmdWriter`, `DmxWriter` (canonical copies in `Effigy/`,
mirrored into `Editor/Effigy/`, never edit the mirror). The export paths are in
[`EffigyWindow.CompileVmdl`](Editor/EffigyEditor/EffigyWindow.cs:1616):

| Path | Writes | Result |
|---|---|---|
| static (no bones) | `export.obj` + `export.vmdl` | compiles |
| rigged (bones) | `export.smd` + `export.dmx` + `export.vmdl` | **`export.vmdl` fails to compile** |

The failure, from po's editor console at 02:12:38:

```
models/effigy/export.vmdl: [Body_LOD0]: LoadDMXModel( ...\assets\models\effigy\export.dmx ):
                                        Couldn't load DMX file
models/effigy/export.vmdl: [Body_LOD0]: Node 'Body_LOD0' resolve failure
```

The SMD written alongside it is dead weight — the compiler rejects `.smd` outright:

```
Unknown/unsupported geometry/model format in specified filename, Supported types: FBX, DMX, OBJ, VOX
```

`DmxWriter`'s header already documents that discovery correctly. What it got wrong is the next step.

---

## 2. The finding that changes the plan

`DmxWriter`'s class comment justifies hand-writing DMX like this:

> *"Of the four, OBJ carries no bones and FBX is a binary format nobody should hand-write, which
> leaves DMX as the only way to compile a rigged model."*

**Both halves of that are wrong, and the engine ships the proof.** In
`C:\Program Files (x86)\Steam\steamapps\common\sbox\bin\win64\`:

```
fbx2dmx.exe        1.6 MB
libfbxsdk.dll      8.0 MB     ← Autodesk's official FBX SDK
```

```
$ fbx2dmx.exe
NAME
    fbx2dmx - Converts an FBX file to a DMX file
SYNOPSIS
    fbx2dmx [ opts ... ] < filename.fbx >
    -i | -input <$>          Specifies the input filename
    -o | -output <$>         Specifies the output filename
    -up                      One of [ x, y, z, -x, -y, -z ],    Def: y
    -fp | -forwardParity     One of [ even, odd, -even, -odd ], Def: x
    -s | -scale <#>          Specifies a scale factor for scene conversion
    -a                       Convert animation, normally models are converted
    -msp | -materialSearchPath <$>
    -v                       Each -v increases verbosity
```

Two consequences:

1. **FBX is not "a binary format nobody should hand-write."** The loader is the Autodesk SDK, which
   reads **ASCII FBX** as happily as binary. ASCII FBX 7.x is a plain nested-block text format —
   about the same difficulty as the KV2 that `DmxWriter` already emits.
2. **The engine converts FBX → DMX itself.** Writing FBX means `fbx2dmx.exe` produces the DMX, so
   the DMX correctness problem stops being ours. That is the whole argument for the format switch:
   not that FBX is nicer, but that it hands a decades-hardened importer the job we are currently
   doing by hand from strings scraped out of a DLL.

### 2.1 …and it hands you a known-good reference file for free

This is the single most useful thing in this brief. Any FBX in the project converts in one command:

```bash
"/c/Program Files (x86)/Steam/steamapps/common/sbox/bin/win64/fbx2dmx.exe" \
  -i Assets/models/lightswitch/lightswitch_plate.fbx -o /tmp/ref.dmx
```

(It warns `Cannot find a material resource for material :: sw_plate` — harmless, it still writes.)

`ref.dmx` is a **DMX the compiler definitely loads**, written by the engine's own converter. Diff
`DmxWriter`'s output against it instead of reasoning about the format.

Doing that diff on the very first line already finds something:

```
engine's:   <!-- dmx encoding keyvalues2_noids 4 format model 22 -->
DmxWriter:  <!-- dmx encoding keyvalues2 1 format model 22 -->
```

**Different encoding, different encoding version.** `keyvalues2_noids` v4 vs `keyvalues2` v1. The
`format model 22` half — the part that was carefully researched — matches. The header half was not.
`keyvalues2_noids` omits the per-element `"id" "elementid" "..."` line that `DmxWriter` writes on
every element, and v1 vs v4 of the KV2 encoding are not the same grammar.

**This is a candidate root cause for "Couldn't load DMX file" and it costs ten minutes to test:**
change the header to `keyvalues2_noids 4`, drop the `id` attributes, re-export, recompile. It may
just work. It may not — the payload could be wrong too — but either answer is worth having before
committing to an FBX writer, because if it works, the rigged path is unblocked *today* and the FBX
writer becomes an improvement rather than a rescue.

Do this first. Then decide.

---

## 3. The work, if FBX is still the answer

### 3.1 What the writer has to carry

`DmxWriter` is the spec for this — it already solves every content question, and an FBX writer is
the same data through a different syntax. From `Effigy/DmxWriter.cs`:

- **Positions** — `PolyMesh.Positions` (`Vec3`, inches, Source coords: +x forward, +y left, +z up).
- **Faces** — `PolyMesh.Faces`, each an index loop. **N-gons must survive.** Not triangulating on
  the way out is the reason `DmxWriter` exists at all; it keeps the quad cage Catmull-Clark needs.
  FBX handles n-gons natively via the negative-XOR terminator (see 3.2).
- **Normals** — `MeshNormals` with `DefaultSmoothingAngleDegrees`, same call the DMX path makes.
- **UVs** — per face-vertex, from `Face.UVs`. Note the DMX path sets `flipVCoordinates`; FBX's V
  convention differs again, so expect one flip to be wrong the first time and check against a
  textured test model rather than reasoning about it.
- **Materials** — one FBX material per Effigy slot, named via `_studio.NameForSlot`, falling back to
  `ObjWriter.DefaultMaterialName(slot)`. Faces carry `Face.Material` (a slot index) and FBX wants a
  `LayerElementMaterial` in `ByPolygon` mode: one material index per polygon.
- **Skeleton** — `Effigy.Skeleton`: `Bones` in **topological order** (parent index always lower than
  the child's, enforced by `AddBone`), each with `Name`, `Parent`, `Local` (an `Xform` bind pose
  *relative to the parent*), and `Length` (tail is `Length` along the bone's local +Y — Blender's
  convention).
- **Skin weights** — `PolyMesh.Skin`, a `SkinWeights` parallel to `Positions`. **Unbounded influence
  count by design**; prune to 4 at export (`SkinWeights.Prune`, `DmxWriter.MaxInfluences = 4`).
  Weights are non-negative and sum to 1 — preserve that after pruning by renormalising.

### 3.2 FBX specifics that will bite

- **ASCII FBX 7400** (7.4) is the version to target — it is what Blender and Maya emit and what the
  SDK is happiest with. Header is `; FBX 7.4.0 project file` plus an `FBXHeaderExtension` block.
- **Polygon index encoding:** `PolygonVertexIndex` is a flat list where the **last index of each
  polygon is bitwise-negated** (`~i`, i.e. `-i - 1`). That is how n-gon boundaries are marked; there
  is no per-face count array. Getting this wrong produces one enormous polygon rather than an error.
- **Connections drive everything.** FBX objects are inert until wired in the `Connections` block
  (`C: "OO", child, parent`). Mesh→Model, Model→root(0), Material→Model, Deformer→Mesh,
  SubDeformer→Deformer, Bone/Limb→parent Model. A missing connection is a silent omission, not a
  parse error.
- **Skinning is two levels:** one `Deformer` of type `Skin` for the mesh, then one `SubDeformer` of
  type `Cluster` **per bone**, each holding `Indexes` (vertex indices it affects), `Weights`, and a
  `Transform` / `TransformLink` pair (the bind-pose matrices — `TransformLink` is the bone's world
  bind transform, `Transform` is the mesh's world transform at bind time, usually identity here).
  Deriving world bind from `Bone.Local` means walking parents; `Skeleton` has `WorldBind` for it.
- **Units/axes:** `GlobalSettings` carries `UpAxis`/`FrontAxis`/`CoordAxis` and `UnitScaleFactor`.
  Effigy is Z-up inches, Source convention. `fbx2dmx` also takes `-up z -fp even`. Decide in **one**
  place and verify against `tools/inspect_fbx.py` (a Blender `bpy` script already in the repo that
  prints unit scale and world-space dimensions of an FBX) rather than eyeballing the viewport.
- Bones are `Model`s of subtype `LimbNode`, plus `NodeAttribute` of type `LimbNode` carrying `Size`.

### 3.3 Where it goes

- New file **`Effigy/FbxWriter.cs`** (canonical — *never* `Editor/Effigy/`, see
  `tools/sync-kernel.sh` for why the mirror is one-way and what happened the one time it was not).
  Shape it like `DmxWriter`: `Write(...)` returning a string, `WriteFile(...)` wrapping it, same
  parameter list (`mesh, skeleton, smoothingAngleDegrees, materialName, modelName`).
- Tests in **`Effigy.Tests/`**. The kernel is engine-free and the suite is 1926 checks; a writer is
  exactly the kind of thing that is fully testable there. `SmdWriter` ships a minimal reader for
  round-tripping in tests (`Effigy/SmdWriter.cs:188`) — do the same: parse back positions, polygon
  boundaries, material-per-polygon and cluster weights, and assert they match what went in. That
  catches the negated-index encoding and the weight pruning without an editor.
- Swap the call sites in [`EffigyWindow.CompileVmdl`](Editor/EffigyEditor/EffigyWindow.cs:1616) and
  `ExportObj`. `BuildVmdl` / `BuildSkinnedVmdl` just need the filename changed to `export.fbx` —
  the compiler takes FBX in the same slot as OBJ/DMX.
- **Delete `SmdWriter` and its call sites**, or keep it only if a DCC round-trip actually wants it —
  it is currently written on every rigged export into a format the compiler refuses.

### 3.4 How to know it worked

Not "the viewport looks right". In order:

1. `export.fbx` converts: `fbx2dmx.exe -i export.fbx -o /tmp/check.dmx` with no errors.
2. `tools/inspect_fbx.py` in Blender reports the expected vertex count, dimensions and bone count.
3. `asset.Compile(true)` in `CompileVmdl` leaves `IsCompileFailed == false`.
4. The rigged worked example in `WHAT-IS-LEFT.md` §7.2 — a bone per finger, export, pose in Rig
   Control, **then rebuild the parametric tree and confirm the binding survived**. That last step is
   the one that proves body-id binding works, and it is the actual goal behind all of this.

---

## 4. Do not get distracted by

There is a **separate, unrelated bug** being fixed in parallel: the engine boolean returns a face it
cut into as many coplanar fragments (one measured case: 88 triangles and quads in a single plane),
so clicking that face to assign a material hits one fragment. That is a `CoplanarMerge` pass in the
kernel plus a call from `EffigyMeshBoolean.ToPolyMesh`. It touches `Effigy/` and
`Editor/EffigyEditor/EffigyMeshBoolean.cs`. **Expect a conflict there and nowhere else.**

---

## 5. Outcome of §2's ten-minute test — read this before doing §3

**The DMX path was fixed instead. It compiles.** The header hypothesis in §2.1 was wrong: the
engine's own `dmxconvert.exe` reads `keyvalues2 1` without complaint, so the encoding and version
were never the problem. Three other things were:

1. an element inside an `element_array` was written with **no trailing comma**, so the parser ran the
   next member's type name onto the previous member — this is the actual "Couldn't load DMX file",
   and `dmxconvert` reports it as `export.dmx(56) : Expecting ',', didn't find it!`;
2. an element **reference** was written as a bare quoted id rather than the two tokens
   `"element" "<id>"`;
3. the `DmeVertexData` fields were named `positions` / `normals` / `textureCoordinates` /
   `jointWeights` / `jointIndices`, where the compiler keys on **`position$0` / `normal$0` /
   `texcoord$0` / `blendweights$0` / `blendindices$0`** — reported as "Missing position values".

All three are fixed in `Effigy/DmxWriter.cs` and covered by `Effigy.Tests/DmxGrammarTests.cs`, which
parses the output as KeyValues2 rather than searching it for substrings. A rigged cylinder now
compiles to a `.vmdl` with correct bounds (`1 x 1 x 4`, 1 mesh, 1 material).

**The most useful thing in this brief turned out to be §2.1, but not for the reason it says.**
`fbx2dmx.exe` is a *reference generator* and `dmxconvert.exe` is a *validator* — one command each,
no editor, precise error with a line number. That is what found all three bugs. The FBX field names
above came straight out of converting `Assets/models/first_person/fp_arms.fbx`.

**So §3 is no longer required to unblock the rigged path.** Whether an `FbxWriter` is still worth
having — it does hand the importer job to Autodesk's SDK, which remains a real argument — is now a
free choice rather than a rescue, and it is po's call. §3 stays below because it is still an accurate
spec for that work if it goes ahead.

Still unverified, either way: **that bones survive the compiler** (only geometry has been checked —
bone names are not recoverable from a `.vmdl_c` by inspection, not even for known-good `fp_arms`),
and **the UV V orientation** (`DmxWriter` writes `flipVCoordinates 0`; `fbx2dmx` writes `1` for FBX
input, which is not the same convention). Both need the editor, and both are noted in
`WHAT-IS-LEFT.md` §7.2.
