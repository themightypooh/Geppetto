# Shader Forge — session handoff

A third s&box tool, alongside the RigControl animation editor and the Effigy modelling tool: a
shader previewer paired with a keyword-driven `.shad` generator, so someone who doesn't know HLSL
can describe an effect in plain English and get a working shader back. Full scope, block library
and phase plan live in the tool design doc this session started from.

The design doc's own build order is explicit: get the editor window rendering a preview model
before anything else, then layer preview hardening, manual shader loading, generation and save on
top, each phase independently shippable. **This session did Phase 1 only** — the shell — on
purpose, per that order.

---

## How to not waste the next session

**This session ran in a cloud container with no engine source and no `dotnet` reachable through the
proxy** (installer and NuGet both 403'd). Nothing here was compiled or opened in the editor. The
camera, lighting and Model.Builder patterns are copied verbatim from `EffigyViewport` /
`EffigyPreview`, which HAVE been run and are trustworthy. The one piece that is a first use in this
repo and therefore unverified:

| Used | Where | Confidence |
|---|---|---|
| `PointLight` component, `.Radius` property | `ShaderForgeViewport` fill light | Not verified against engine source or a build. `DirectionalLight`/`AmbientLight` are proven elsewhere in this repo; `PointLight` is new here. If it doesn't compile, check the actual component name/property first via `sbox` MCP `compile_status`, or a throwaway reflection dump — see Marionette's own HANDOFF.md for that trick. |

Open the project in the editor and check `compile_status` before touching anything else.

---

## What's here

```
Editor/ShaderForgeEditor/ShaderForgeWindow.cs       DockWindow shell, menu bar, model-picker toolbar, status bar
Editor/ShaderForgeEditor/ShaderForgeViewport.cs      SceneRenderingWidget preview: orbit camera, key/fill/ambient lights
Editor/ShaderForgeEditor/ShaderForgePrimitives.cs    Stock Sphere/Cube/Plane/Cylinder, built from Effigy.Primitives
```

Registered as `[EditorApp("Marionette", ...)]`, same as `EffigyWindow` — it should appear in the
Tools menu as **Shader Forge** once it compiles.

**Done = open the window and see an orbit-able sphere**, per the design doc's Phase 1 exit
criterion. The model picker toolbar switches it to a cube, plane, or cylinder, all generated
through the Effigy kernel and its existing `EffigyPreview.Build` (PolyMesh → runtime `Model`) so
Shader Forge carries no geometry code of its own.

Deliberately **not** built yet, in build-order order:

- Phase 2 — scanning the project for real `.vmdl` files, material slot list, full lighting rig
  (this session already added the point light Phase 2 calls for, since it was nearly free once the
  key light and ambient existed — everything else in Phase 2 is still open)
- Phase 3 — loading an existing `.shad`, reading `Shader.Schema`, auto-built tweak controls
- Phase 4 — the keyword parser, block library, and Generate button
- Phase 5 — save to `shaders/custom/` via `FileSystem.Mounted`

No right-hand generator panel exists yet either, on purpose — this project's own convention
(see the sketch toolbar comment in `EffigyWindow`) is that a control with nothing behind it is
worse than no control, so the panel arrives with Phase 4 rather than sitting there disabled.

---

## Where to pick this up

Phase 2's material slot list and model scan are the natural next step — `Model.Materials` /
`Model.BodyParts` for slots, `FileSystem.Mounted.FindFile()` for `.vmdl` discovery, both already
named in the design doc's API table. `RigControlWindow`'s `OpenPicker` (model loading from the
project) is the closest existing precedent for the file-picker side of that.
