# Marionette — session handoff

A control-rig animation editor for s&box, split out of the `midnight_am` game project into its own
repo: <https://github.com/themightypooh/marionette>

Everything below is verified unless it says otherwise. **Read "How to not waste this session"
first** — it is the single most useful thing here.

---

## How to not waste this session

**The engine's own source is on disk. Read it before writing against any API.**

```
C:\Program Files (x86)\Steam\steamapps\common\sbox\addons\tools\Code   ← Base Editor Library
C:\Program Files (x86)\Steam\steamapps\common\sbox\editor\             ← ShaderGraph, AnimGraph, MovieMaker, Hammer…
```

Nearly every bug in the previous session came from inferring an API from its parameter names
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

**If there is no .NET on the machine, install it first**: `apt-get install -y dotnet-sdk-8.0`.
The Effigy kernel is engine-free, so `cd Effigy.Tests && dotnet run -- out` compiles and runs ~480
checks with no s&box anywhere — including headless replays of editor workflows (EditorFlowTests).
That is the difference between verifying a change and reading it and hoping.

The MCP `sbox` server is attached to whatever project the editor has open. `compile_status` after
every edit; `read_console` for runtime output. When an API's shape is unknown, a throwaway
`[ConCmd]` that reflection-dumps the type beats guessing — that's how `LocalTransform` was found.

---

## Engine behaviours this tool stands on

Each cost real time to find. All are documented at their call sites too.

- **`SceneRenderingWidget` renders its scene but never ticks it.** A tool hosting its own editor
  scene must call `Scene.EditorTick(RealTime.Now, RealTime.Delta)` itself — see ShaderGraph's
  `Preview.PreFrame()`. Without it nothing you write ever appears.
- **Bone writes land on the next tick.** `SetBoneTransform` reads back stale in the same frame and
  correct after a tick. `SceneModel.SetBoneWorldTransform` reads back instantly but sets no
  override and is stomped — it looks right and isn't.
- **Bone overrides are world-space, so they kill inheritance.** A bone with an override no longer
  follows its parent. Anything that moves a bone must re-resolve its descendants —
  `RigViewport.PropagateToDescendants`.
- **`DockWindow` has no `Layout`.** The dock manager owns the client area; `Layout.Add` throws.
  Full-width strips go through `Window.StatusBar`, which only accepts `Editor.StatusBar`.
- **`BuildDefaultLayout` only runs when no saved layout exists.** New docks must also be opened
  explicitly at startup (`SetDockState` + `RaiseDock`), or they never appear for anyone who has
  opened the tool before. Bump `StateCookie` when the default layout changes.
- **`AssetSystem.CreateResource` takes an absolute path.** Relative resolves against the sbox
  install and throws.

`rig_test_pose` and `rig_test_ik` verify the first two and the IK solve. One command each.

---

## Where things are

```
Code/RigControl/          runtime + asset types (.riganim, .ctrlrig), IK/limit solver
Editor/RigControlEditor/  the tool
Assets/animations/        the worked example clip
Assets/models/lightswitch/, Assets/models/first_person/   sample content
Editor/HaloMount/         UNRELATED. po's Halo MCC mount work — do not touch
```

`RigViewport.cs` is the heart of it and the file that has caused every hard bug.
`EvaluatePose` in particular has been rewritten four times; read its comments before changing it,
they record what each previous version broke.

---

## State

Compiles clean, zero warnings. Everything below is pushed.

**Working and verified by use:** bone posing (rotate default, E to translate), the timeline,
undo/redo, two-bone IK, bone hiding, reference props, viewmodel camera lock, timeline zoom
(ctrl+wheel) and pan (shift+wheel).

**Written but never seen render:** most of the tutorial panel's finer layout. It works; whether it
*looks* right at various dock sizes is unconfirmed.

**Known rough edges:**

- The example clip's wrist never rotates — the IK solver keeps the end bone's orientation, so the
  hand arrives without turning to face the switch. Tutorial step 6 teaches doing this by hand.
- The tutorial's settle step (`frame 22`) checks only "a key exists after frame 21", so it ticks
  off whether or not you actually overshot.
- The reference-prop step ticks the moment a model is assigned, before it's placed anywhere useful.
- `Editor/HaloMount` ships inside this package. It contains **GPL-3.0 binaries** (`Reclaimer.Blam`)
  and its own source says they must never be shipped. po knows; the plan is to pull it out into its
  own project before publishing. **Do not publish Marionette until that happens.**

---

## Publishing — read before advising on it

`.sbproj` is `Type: addon`, `Org: pooh` → ident `pooh.marionette`. Target Game deliberately empty.

**The consumption path is not proven.** Project Settings → Packages, the screen where another dev
would add `pooh.marionette`, ships with Facepunch's own warning: *"This stuff hasn't been properly
end to end tested - please don't expect it to work just yet!"* It's also unclear whether a
referenced package's `Editor/` code reaches the consumer's editor assembly at all.

So the README leads with **copy these two folders** (`Code/RigControl`, `Editor/RigControlEditor`),
which works today. asset.party is for discovery; the forum thread is the real distribution.

Relevant thread: s&box forums, "AnimGraph 2" — loud unmet demand for exactly this. `matt` is
Facepunch. `dictateurfou` is building a similar control-rig editor and has not shipped it.
`redsnail.roadtool` at 17 votes is the most-voted comparable tool, so the bar is low.

---

## Working with po

Direct, moves fast, gets justifiably annoyed at repeated failures on the same thing. Give the
answer, not a preamble. When something has broken several times, say plainly which attempt this is
and what would falsify it rather than announcing it's fixed again.

**They are learning to animate — that's why this tool exists.** Craft questions ("where should the
first frame be?") are as real as code questions, and worth answering properly.

**They write better UX copy than I did.** The tutorial text was rewritten several times before
they mocked up what they wanted, and their version was plainer and better. If they hand you copy,
wire it in verbatim — do not improve it.

Do not touch `Editor/HaloMount` unless asked. Commit only your own files when their work in that
folder is mid-edit and failing to build.
