# Handoff

For an AI or a person picking this up cold, mid-task, with no memory of the last session. Read this
before touching anything.

The other three docs: [README.md](README.md) is what the tools are,
[WHAT-IS-BUILT.md](WHAT-IS-BUILT.md) is what is finished and how it was verified,
[WHAT-IS-LEFT.md](WHAT-IS-LEFT.md) is what is not and how to do each piece.

---

## How to not waste this session

**1. The engine's own source is on disk. Read it before writing against any API.**

```
C:\Program Files (x86)\Steam\steamapps\common\sbox\addons\tools\Code   ← Base Editor Library
C:\Program Files (x86)\Steam\steamapps\common\sbox\editor\             ← ShaderGraph, AnimGraph, MovieMaker, Hammer…
```

Nearly every bug in this project's history came from inferring an API from its parameter names
instead of reading shipped code that uses it. Each of these cost a round trip or worse:

| Assumed | Actually |
|---|---|
| `Control.Position` returns an absolute position | per-frame **delta** you accumulate (`PositionEditorTool`) |
| `Control.Rotate` matches it | **cumulative** since grab — the two controls differ |
| `Control.Position` takes 3 args | takes a **4th**, the handle rotation/basis — omitting it made every axis drag along Z |
| `OnWheel` | it's `OnMouseWheel`, and `WheelEvent` carries **no position** |
| `[Shortcut(name, keys)]` | needs `ShortcutType.Window` or the key never reaches your window |
| Redo is Ctrl+Shift+Z | editor convention is **Ctrl+Y** |
| No bind-pose accessor exists | `BoneCollection.Bone.LocalTransform` |
| Material **Symbols** icon names | s&box ships classic `MaterialIcons-Regular.ttf`; a Symbols name renders as **nothing** |

When an API's shape is unknown, a throwaway `[ConCmd]` that reflection-dumps the type beats
guessing — that is how `LocalTransform` was found, and how the boolean adapter was written.

**2. Run the kernel suite before anything else.**

```sh
export PATH="/c/Program Files/dotnet:$PATH" && ./tools/test.sh
```

`dotnet` is installed but not on `PATH` in Git Bash, hence the prefix. About 25 seconds, **1609
checks**. The script syncs the editor's kernel mirror first, then runs the suite.

The Effigy kernel is engine-free, so all of it — plus every editor workflow that is "kernel calls in
a particular order", plus sketch snapping and the expression evaluator — compiles and runs with no
s&box anywhere, including headless replays of editor workflows (`EditorFlowTests`). **That is the
difference between verifying a change and reading it and hoping.** A session that skips this ends up
reasoning about code by reading it, and reading is how a bug that made every parameter edit a silent
no-op survived long enough to look like three unrelated UI faults.

On a fresh Linux container with no SDK the script installs one (`apt-get install -y dotnet-sdk-8.0`)
before running.

**3. Check `editor_status` before trusting anything about the editor.**

Having the wrong copy of the project open is an easy mistake and everything downstream then answers
about the wrong files.

---

## The editor's MCP server

**s&box ships one; no third-party addon is needed.** It lives in the shipped editor source at
`addons/tools/Code/Mcp`, is switched on in **Editor Preferences → MCP Server**, and is registered for
this project in `.mcp.json` at the repo root. It runs at `http://127.0.0.1:7269/mcp`, loopback only,
and dies with the editor.

- **`editor_status`** — which project is open, the active scene, whether play mode is running, and
  the compiler state: `IsCompiling`, `LastCompileSucceeded`, `LastCompileErrors`. **Use this rather
  than the console to check a compile** — a passing compile can scroll out of view or be mistaken for
  the last failure you saw.
- **`read_console`** — what the editor and game logged, errors with the top of their stack.
- **`search_tools` / `call_tool`** — the tool list you connect with is only the entry points. The
  real tools live in editor and addon code and come and go as code hotloads, so search the live
  registry rather than trusting the initial list.
- **`camera_screenshot` / `editor_camera_screenshot`** — this is what closes the long-standing "can't
  be verified without po at the machine" gap. UI work can now be looked at.

Conventions across the whole registry: paging is `limit`/`offset` and out-of-range values clamp;
vectors and angles are comma strings (`"x,y,z"`, `"pitch,yaw,roll"`), not arrays; the coordinate
system is Source convention — one unit is one inch, +x forward, +y left, +z up, angles in degrees;
game objects and components are identified by guid, assets by the relative path `asset_search`
returns. Every tool that edits the scene pushes an undo step.

---

## Engine behaviours the tools stand on

Each cost real time to find. All are documented at their call sites too.

- **`SceneRenderingWidget` renders its scene but never ticks it.** A tool hosting its own editor
  scene must call `Scene.EditorTick( RealTime.Now, RealTime.Delta )` itself — see ShaderGraph's
  `Preview.PreFrame()`. Without it nothing you write ever appears.
- **Bone writes land on the next tick.** `SetBoneTransform` reads back stale in the same frame and
  correct after a tick. `SceneModel.SetBoneWorldTransform` reads back instantly but sets no override
  and is stomped — it looks right and isn't.
- **Bone overrides are world-space, so they kill inheritance.** A bone with an override no longer
  follows its parent. Anything that moves a bone must re-resolve its descendants —
  `RigViewport.PropagateToDescendants`.
- **`DockWindow` has no `Layout`.** The dock manager owns the client area; `Layout.Add` throws.
  Full-width strips go through `Window.StatusBar`, which only accepts `Editor.StatusBar`.
- **`BuildDefaultLayout` only runs when no saved layout exists.** New docks must also be opened
  explicitly at startup (`SetDockState` + `RaiseDock`), or they never appear for anyone who has
  opened the tool before. Bump `StateCookie` when the default layout changes.
- **`AssetSystem.CreateResource` takes an absolute path.** Relative resolves against the sbox install
  and throws.
- **`Gizmo.CurrentRay` means nothing inside a menu callback.** Hold last frame's cursor ray — see
  `EffigyViewport.FaceMenu.cs`.
- **A context-menu event arrives on button RELEASE**, and every orbit ends over the model, so a
  right-click menu needs a short guard after the fly camera last moved.

`rig_test_pose` and `rig_test_ik` verify the first two and the IK solve. One command each.

### Known-unverified API, in one list

Still guessed rather than read. Each is noted at its call site.

| Symbol | Where | Note |
|---|---|---|
| `KeyCode.Enter` | `EffigyFeatureDialog.OnKeyPress` | one-word fix if named differently |
| `KeyCode.Shift` | same | only Shift+Enter depends on it |
| `Gizmo.CurrentRay` | `EffigyViewport.Sketching.CursorToPlane` | pre-existing; the whole sketcher rests on it |
| `Gizmo.Draw.LineThickness` | sketch + plane rendering | pre-existing |
| `Gizmo.Draw.SolidTriangle` | region shading, plane highlight | pre-existing |
| `Gizmo.Draw.WorldText` | axis labels | pre-existing |

`TreeNode` context menus and `LineEdit` focus events were both wanted and both avoided for the same
reason: no proven usage to copy, and an unproven member name takes the whole editor assembly down.

---

## Where things are

```
Code/RigControl/          runtime + asset types (.riganim, .ctrlrig), IK/limit solver
Editor/RigControlEditor/  the animation tool
Assets/animations/        the worked example clip
Assets/models/lightswitch/, Assets/models/first_person/   sample content
Assets/shaders/pixel_arms.shader   the one shader here known to compile — the Shader Forge reference

Effigy/                   the CAD kernel — engine-free, the CANONICAL copy
Editor/Effigy/            a MIRROR of the kernel. Never hand-edit
Editor/EffigyEditor/      the CAD tool's UI
Effigy.Tests/             1609 headless checks

Editor/ShaderForge/       the shader generator kernel — engine-free
Editor/ShaderForgeEditor/ the shader tool
ShaderForge.Tests/        console runner over that kernel

Editor/HaloMount/         UNRELATED personal work — do not touch.
                          UNTRACKED and absent from a fresh clone; see below
```

### The two kernel copies

`Effigy/` is canonical. `Editor/Effigy/` is a **mirror** and must never be hand-edited.

The reason is structural: s&box compiles `Code/` into the game assembly and `Editor/` into the editor
assembly, and nothing else — a top-level `Effigy/` is invisible to it. The kernel cannot live in
`Code/` either, because `ObjWriter` and `SmdWriter` call `File.WriteAllText` and the game assembly's
sandbox whitelist does not allow it. Hence the mirror.

Run `tools/sync-kernel.sh` after any kernel edit. `tools/test.sh` runs it first anyway, so a test run
can never pass against source the editor is not actually compiling. `KernelSyncTests` fails the run
when the two diverge — the mirror was once committed with a stray blank line, a diff of no
consequence and proof that nothing was checking.

`RigViewport.cs` is the heart of Rig Control and the file that has caused every hard bug.
`EvaluatePose` has been rewritten four times; read its comments before changing it — they record
what each previous version broke.

---

## Editor/HaloMount — untracked on purpose, and how to get it back

Unrelated to any of these tools: a runtime `BaseGameMount` reading a Halo: The Master Chief
Collection install and converting Halo 3 tags to s&box models on demand. Personal use.

Commit `28abc7e` took it **out of the repo** precisely because it carries **GPL-3.0 binaries**
(`Reclaimer.Blam` and friends) that must never ship in a package whose README tells people to copy
its folders. `.gitignore` keeps it out from here on, and that is why nothing GPL blocks publishing
any more.

The consequence, which has already cost a session's confusion: **it exists only on the machine it
was worked on.** Cloning or unzipping this repo will not bring it, and it is not lost when that
happens — every file is still in history:

```sh
git archive 28abc7e^ Editor/HaloMount | tar -x
```

Three things to know after restoring, all verified as real problems on a second machine:

- **`LibrariesDir` is a hardcoded absolute path** in both `HaloMCCMount.cs` and `HaloMountSpike.cs`,
  and it has to be: hot-reloaded editor assemblies don't live at the project's source path, so
  `Assembly.Location` resolves into the sbox install. It will point at whatever machine it was last
  edited on.
- **It reads a real MCC install** (Steam app 976730). Without MCC installed it finds nothing.
  `marionette.sbproj`'s `"Mounts": ["halomcc"]` refers to this mount's own `Ident`.
- **There is no compile-time DLL reference and there cannot be.** s&box regenerates the `.csproj`
  from the `.sbproj` on every project load, discarding hand edits (`Facepunch/sbox-public#6826`), so
  `LoadReclaimer()` does `Assembly.LoadFrom()` at runtime and everything talks to Reclaimer types via
  `dynamic`. The code looks unusual for that reason — it is not style, it is the only way it works.

Keep everything under `Editor/`, never `Code/`, never referenced by a real game scene.

---

## Licensing

**This repo is MIT.** `CAD-REFERENCE`-style design study of Solvespace (GPL) and FreeCAD (LGPL 2.1)
informed several decisions recorded in [WHAT-IS-BUILT.md](WHAT-IS-BUILT.md) — **nothing here is
copied code and nothing here may become copied code.** Architecture and approach are not
copyrightable; expression is. Keep it that way.

The one licensing scar is HaloMount above, and it is handled by being untracked.

---

## Working with po

Direct, moves fast, gets justifiably annoyed at repeated failures on the same thing. Give the answer,
not a preamble. When something has broken several times, say plainly which attempt this is and what
would falsify it, rather than announcing it's fixed again.

**They are learning to animate — that's why these tools exist.** Craft questions ("where should the
first frame be?") are as real as code questions and worth answering properly.

**They write better UX copy than I did.** The tutorial text was rewritten several times before they
mocked up what they wanted, and their version was plainer and better. If they hand you copy, wire it
in verbatim — do not improve it.

**Keep these docs updated continuously**, not at a clean stopping point. A session has ended
mid-work before and the context was lost; that is the whole reason this file exists. When a status
changes, change it here in the same pass.

Do not touch `Editor/HaloMount` unless asked.

---

## Habits this project earned the hard way

- **A valid mesh can be visibly wrong.** Closed, manifold, Euler-correct and still broken. Measure
  enclosed volume, covered area, or boundary-edge count. The chamfer bug that threw vertices 15,000
  units out passed every numeric check and was found by a *render*.
- **A green suite and a picture catch different mistakes.** `RenderCheck` exists for that reason.
- **Treat every "not supported yet" string as suspect** until it has been re-derived rather than
  re-read. Three limitations here were documented as needing the mesh boolean and none of them did.
- **Put the dirty-mark next to the mutation**, not in the caller. The editor once changed parameters
  without marking anything dirty, so nothing ever re-ran — three UI faults that were one bug.
- **Verify the derivative, not just the answer.** A wrong derivative gives a slow or unstable solve,
  never a failing assert — it presents as "the solver feels flaky".
- **Anything that could be moved into the kernel and tested, was.** That is why the split between
  `Effigy/` and `Editor/EffigyEditor/` is drawn where it is.
