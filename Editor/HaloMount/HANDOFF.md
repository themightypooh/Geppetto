# Halo MCC → s&box mount — handoff

Personal-use project: a real runtime `BaseGameMount` that reads the user's own Halo:
The Master Chief Collection install and converts Halo 3 tags to s&box `Model`
resources on demand — same pattern as the official Quake/HL2/GoldSrc mounts. Lives
entirely under `Editor/HaloMount/` in the **marionette** project (moved here from
midnight_am partway through — marionette is the user's general tools project, and
nothing Halo-derived should ever end up in a shippable game).

Not to be re-derived from scratch: read this first, then the source files (all
short, all commented with the *why*, not just the *what*).

## Why this is Editor-only, permanently

`Reclaimer.Blam` (the library doing the actual Halo tag parsing) is GPL-3.0. Fine for
personal use, but if this code or its output ever ends up in a published game, that's
a real licensing problem. Keep everything under `Editor/`, never `Code/`, never
referenced by a real game scene. The user is aware and has accepted this boundary.

## The core trick: no compile-time DLL reference

s&box tool/editor projects have no supported way to add a third-party `<Reference>`
— the `.csproj` is silently regenerated from the `.sbproj` on every project load,
discarding any hand edit (confirmed open issue: `Facepunch/sbox-public#6826`). So
instead of referencing `Reclaimer.Blam.dll`/`Reclaimer.Core.dll` at compile time,
`HaloMCCMount.LoadReclaimer()` does `Assembly.LoadFrom()` at runtime and everything
talks to Reclaimer types via `dynamic`. This is why the code looks unusual (lots of
`dynamic`, reflection for anything not on a plain `Sandbox.*` type) — it's not
style, it's the only way this works at all.

The built DLLs live in `Editor/HaloMount/Libraries/` (checked in as binaries, built
from `Gravemind2401/Reclaimer` via `dotnet build` on the `Reclaimer.Blam` project —
see the paths hardcoded as `LibrariesDir` in both `HaloMCCMount.cs` and
`HaloMountSpike.cs`, currently `C:\Users\po\Documents\s&box projects\marionette\Editor\HaloMount\Libraries`).

## File map

- **`HaloMCCMount.cs`** — the `BaseGameMount`. `Initialize()` detects the Steam app
  (976730) and locates `halo3/maps`. `Mount()` scans every campaign map + a hardcoded
  list of ODST mission maps (`campaign.map`/`shared.map` throw when read directly —
  excluded) for `weap`/`bipd`/`sbsp` tags with a matching `mode` (render_model) tag,
  and registers each as `weapons/{name}.vmdl`, `characters/{name}.vmdl`, or
  `maps/{mapname}.vmdl`. Results are cached in a `static` field (`discoveryCache`)
  across `Mount()` calls — the first mount in a session pays the full scan cost
  (~40 map files), every remount after that (constant during iteration, via
  `halomount_remount`) is near-instant. **`MountContext` is a ref-struct-like type**
  — it cannot cross into an `async` method or be captured by any lambda/closure at
  all (confirmed via compiler errors, not guessed). Backgrounding the scan via
  `Task.Run`/`ContinueWith` is a dead end for this reason; `Mount()` must stay fully
  synchronous. The cache is the only real fix for the "hangs while mounting" problem.
- **`HaloRenderModelLoader.cs`** — turns one `mode` (render_model) tag into a
  `Sandbox.Model`. Scopes to the union of every **region's first permutation**
  (not just the first region — a biped's body is spread across multiple regions,
  e.g. Grunt has 6: arms/backpack/head/helmet/legs/torso, one mesh each; a weapon
  has one region with alternate-variant permutations after the first, which is why
  "first permutation only, but every region" is the rule that covers both).
- **`HaloBspLoader.cs`** — same idea for `sbsp` (structure_bsp) tags, i.e. level
  geometry. A BSP's first Region is always the "clusters" group (static level
  shell); unlike weapons, *every* permutation in that region is real geometry, not
  an alternate variant, so all of them get converted. Embedded scenery/instances in
  later regions are not handled (v1 = static shell only).
- **`HaloMeshConverter.cs`** — the shared geometry/material/skeleton conversion
  logic both loaders call into. This is where almost everything hard lives. See
  "Known-good" and "Still broken" below.
- **`ProceduralMetal.cs`** — self-authored (not Halo-derived) procedural gunmetal
  PBR generator (value-noise albedo + baked normal map + roughness), used as a
  fallback when a real Halo texture can't be decoded.
- **`HaloMountSpike.cs`** — diagnostic console commands, genuinely useful, keep:
  - `halomount_remount` — force a full Unmount+Mount. `MountHost` is `internal` to
    `Sandbox.Mounting.dll` and isn't reachable as a singleton from outside, but every
    `BaseGameMount` holds a private `_host` field pointing at it — this reflects
    into `HaloMCCMount.ActiveInstance`'s `_host` rather than hunting for a static
    accessor that doesn't exist. **Resource loaders cache their result after first
    `Load()`, and that cache does NOT reliably invalidate on remount or code
    changes** — see the pitfall below, this cost a lot of time.
  - `halomount_diag_load <displayPath>` — reproduces a resource load directly
    (bypasses the engine's resource pipeline, which swallows real exceptions down
    to a generic "Exception when loading" with no message/stack — confirmed on
    `civilian_fem.vmdl`). Also dumps per-mesh `BoneIndex`/`HasBlendIndices`/
    `HasBlendWeights`/region+permutation layout. **Use this before guessing.**
  - `halomount_find_biped <substring>` — scans every campaign map for any tag
    (any class, not just `bipd`) whose name contains the substring. Used to confirm
    there's genuinely only one Grunt tag across all of Halo 3 (no separate
    rank/armor variants as distinct assets — Halo 3 does rank coloring via a
    material swap on the same body, not separate geometry).
  - `halomount_inspect_mounthost`, `halomount_inspect_guardian`,
    `halomount_find_pistol`, `halomount_test` — earlier exploratory diagnostics,
    lower value now but harmless to keep.

## Known-good (verified working, don't re-litigate)

- **Position/UV decompression**: Halo stores positions and UVs normalized `[0,1]`
  per-axis against each *mesh's own* `PositionBounds`/`TextureBounds`, not already
  real-world values. Must lerp per-axis from `Min`→`Max`. Skipping this = squished
  geometry (the very first bug found and fixed).
- **Rigid bone placement**: `Mesh.BoneIndex` (nullable `byte`) means the whole mesh
  is stored relative to one bone's local space. `Model.GetBoneWorldTransform(i)`
  is **NOT** a bone-local→object-space transform despite the name — for Halo3,
  `Bone.WorldTransform` is precomputed by Reclaimer from the tag's
  `InverseTransform`/`InverseScale` fields, i.e. it's the **inverse bind matrix**
  (object-space→bone-local, what skinning math conventionally wants). Applying it
  directly to a bone-local vertex sends it somewhere unrelated ("teleporting" bug).
  Fix: invert it (`SysMatrix4x4.Invert`) before use. This part is confirmed correct
  — verified on the automag pistol (single rigid mesh) and on the Grunt's rigid
  pieces (backpack `BoneIndex=7`, helmet `BoneIndex=14`) both looking right in
  isolation.
- **Real Halo textures**: `dds.AsUncompressed().CopyPixelData()` gives BGRA8888
  regardless of source compression (Reclaimer's `DdsConvert.cs` handles BC1–BC7
  internally, dispatched via `[DxgiDecompressorAttribute]` — no need to hand-roll
  block decompression). The actual blocker for a long time wasn't the texture data
  (verified sane by sampling bytes) — it was the **material parameter name**.
  `"TextureColor"` is the `.vmat` *file-format* friendly name (confirmed from this
  project's own `.vmat`s); `Material.Set()` at the code level needs the shader's
  raw HLSL parameter name, `"g_tColor"` for `shaders/complex.shader`'s albedo slot.
  Setting both is cheap insurance. `HaloMeshConverter.BuildMaterial` tries the real
  texture first via `g_tColor`/`TextureColor`, falls back to `ProceduralMetal` only
  if decode fails.
- **Skeleton (bones, no deformation)**: `HaloMeshConverter.BuildSkeleton` adds
  Reclaimer's `Model.Bones` to the `ModelBuilder` via
  `AddBone(name, localPosition, localRotation, parentName)`, using `Bone.LocalTransform`
  (parent-relative, NOT the inverse-bind `WorldTransform`). This part appears to work
  — the skeleton itself was never the reported problem, only mesh placement was.

## Still broken: smooth-skinned bipeds render as scattered/exploded geometry

The Grunt (and presumably every other biped — elite, brute, etc., not individually
confirmed) has 6 meshes: 2 rigid (backpack, helmet — confirmed correct in
isolation) and 4 smooth-skinned (arms, head, legs, torso — `BoneIndex` null,
`VertexBuffer.HasBlendIndices`/`HasBlendWeights` both true).

Two fix attempts so far, neither has produced a clean result on `asset_thumbnail`
(a reliable, freshly-rendered auto-framed view — see pitfall below on why this is
the tool to trust):

1. Left smooth-skinned vertices completely untransformed (original assumption:
   already object-space, since blend-weighted vertices can't sensibly live in one
   bone's local space). Produced visible improvement in some manual screenshots but
   `asset_thumbnail` after this fix still showed disconnected floating pieces —
   this assumption is confirmed wrong.
2. Implemented real per-vertex linear blend skinning: for each vertex, read up to 4
   `(boneIndex, weight)` pairs from `BlendIndexChannels[0]`/`BlendWeightChannels[0]`
   (each an `IVector`, components `X/Y/Z/W`), transform the raw position/normal by
   each influencing bone's *forward* transform (same inversion fix as the rigid
   case, precomputed once per bone up front — `forwardBoneTransforms[boneCount]`),
   weighted-sum, normalize by total weight. Still broken —
   `asset_thumbnail` shows a *different* scattered arrangement than attempt 1
   (confirming the code path did change and re-run), but still not assembled.

**Leading unverified theory, not yet implemented**: Halo3 render models may use a
**per-section local bone palette** — `SectionBlock` has a `NodeMaps` collection
(`BlockCollection<NodeMapBlock>`, each with an `Indices` list) that was visible in
the very first read of `RenderModelTag.cs` this session and has never been used.
If blend indices in the vertex data are *local* indices into a small per-section
palette (common in many engines, to keep blend-index vertex components small),
not *global* bone indices directly, then attempt 2 above blends against
essentially arbitrary wrong bones — which would produce exactly this kind of
scatter. **Next step: read `Halo3GeometryArgs`/`Halo3Common.GetMeshes` in
Reclaimer.Blam (`Blam/Halo3/Halo3Common.cs`, not yet read this session) to confirm
whether/how `NodeMaps` should remap blend indices before use, and check whether
Reclaimer's own `Mesh`/`VertexBuffer` abstraction already resolves this for us
(in which case the bug is elsewhere) or leaves it raw (in which case
`HaloMeshConverter.ConvertMesh` needs the remap step added).**

Other things worth checking if the NodeMaps theory doesn't pan out:
- Whether blend weights actually sum to ~1.0 per vertex as assumed, or need
  normalization from some other packed range.
- Whether blend index components need rounding differently (currently
  `MathF.Round`) — could be off-by-one or a different packing.
- Get a second data point beyond the Grunt (e.g. `elite.vmdl`) to see if the
  failure mode is identical, which would support a systemic bone-index bug over
  something Grunt-specific.

## Separate, unrelated bug: some characters fail to load entirely

`characters/civilian_fem.vmdl` (and likely others) throws
`System.InvalidOperationException: Data not found` at
`Reclaimer.Blam.Halo3.ResourceIdentifier.ReadData`, inside Reclaimer's own
`Halo3Common.GetMeshes` → `RenderModelTag.GetModelContent()`. Reproduced cleanly via
`halomount_diag_load characters/civilian_fem.vmdl`. Root cause: the tag's actual
geometry resource lives in `shared.map`'s resource pool, not in the map where the
tag itself was found (`050_floodvoi.map`) — we open only the single map file per
load, with no link to `shared.map`'s resources (which are also currently excluded
from scanning entirely, since reading `shared.map`'s `TagIndex` directly throws a
different error — Reclaimer's engine-detection misreads it). Not investigated
further. Two possible directions: figure out how to properly open a map alongside
its shared resource map in Reclaimer's API, or just wrap `Load()` to fail
gracefully (skip/log) rather than surfacing an ERROR-textured placeholder in the
Asset Browser.

## Content-availability facts (not bugs, don't re-investigate)

- Only Halo 3 + Halo 3: ODST have any map files actually downloaded/installed
  right now. Halo 1, 2, Reach, and 4 are installed but their `maps/` folders are
  **empty** — nothing to scan there yet. If the user wants richer content
  (Reach's Grunts are more detailed, for instance), they need to install that
  game's content via Steam first, same as they did for the Halo 3 MP maps
  mid-session.
- There is exactly **one** Grunt-related `bipd`/`mode` tag pair across every single
  Halo 3 campaign map (confirmed via `halomount_find_biped grunt`, checked every
  class code, not just `bipd`). Halo 3 achieves Grunt rank/armor visual variety at
  the material/shader level (color swap), not via separate geometry — there is no
  "Grunt Ultra" render_model to extract, even in principle, from what's installed.
- 28 weapons, 191 level BSPs, 42 characters currently register successfully
  (`Registered 261 resources` in the console after a scan) — the smooth-skinning
  and shared.map bugs affect *some* of the 42 characters' visual correctness, not
  their registration.

## Process pitfalls that cost real time — read before repeating them

- **`asset_thumbnail` is unreliable UNLESS you know `Load()` hasn't re-run since.**
  It repeatedly showed byte-identical stale images across multiple confirmed code
  changes + remounts earlier in this session. However: `ResourceLoader.Load()` is
  only invoked once per resource path per mount cycle (confirmed by adding an
  instance-hash log line — two separate `spawn_model` calls for the same path
  produced only one `Load() ENTERED` log line). So **immediately after a fresh
  `halomount_remount`, before spawning anything else that would reference the same
  path**, `asset_thumbnail` reflects the current code reliably and is much faster
  than manually framing a camera shot. If in doubt, add a throwaway
  `Log.Info($"...instance {GetHashCode()}")` at the top of `Load()` and check the
  console — don't guess from screenshots.
- **Manual `editor_camera_screenshot` framing is error-prone and easy to
  misjudge.** Several "exploded geometry" readings this session turned out to
  just be the camera placed too close to/inside the model (backface culling and
  losing overall shape at close range looks a lot like genuinely broken geometry).
  Get the object's real `WorldPosition` via `get_game_object` first and back the
  camera off generously, or just use `asset_thumbnail` (auto-framed) per above.
- **`MountContext` cannot be captured by ANY closure** — not just disallowed in
  `async` methods, disallowed in *any* lambda/anonymous method/local function.
  Confirmed via compiler error, not assumption. Don't try to background `Mount()`
  work via `Task.Run().ContinueWith(lambda-using-context)` — it will not compile.
- **Reclaimer's `Model.GetBoneWorldTransform` name is misleading.** It returns the
  precomputed inverse bind matrix when set (always, for Halo3), not a true
  world/forward transform, despite what the name suggests. Always sanity-check
  Reclaimer's own naming against what `RenderModelTag.GetModelContent()` actually
  stores before trusting it.
- **Named `ValueTuple` field names (`.Index`, `.Count`, etc.) are compiler sugar
  only** — through `dynamic`, the real runtime fields are `.Item1`/`.Item2`.
- **`Sandbox.Mounting.Directory` exists and collides with `System.IO.Directory`**
  by bare name — always qualify `System.IO.Directory` explicitly in files that
  touch `Sandbox.Mounting` types, or use a `using SysX = System.Numerics.X;` alias
  pattern (see the top of `HaloMeshConverter.cs`) for any BCL type that might
  collide with a Sandbox namespace.
