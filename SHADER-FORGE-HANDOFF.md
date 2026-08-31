# Shader Forge — session handoff

**Live preview is in.** Close and reopen Shader Forge (layout cookie is now `ShaderForgeLive1` so the docks come back). Type `glowing` — no Generate click. The sphere should light up as the word lands. Tap words on the left, or Surprise me. Forge writes a slim `.shader` of just those blocks. The live material is `shaders/custom/shaderforge_live.shader` (every block, gated). First open may wait on compile; the viewport retries.

---

# Shader Forge — session handoff (original)

A third s&box tool alongside the RigControl animation editor and the Effigy modelling tool: a
shader previewer paired with a keyword-driven `.shader` generator, so someone who doesn't know HLSL
can describe an effect in plain English and get a working shader back.

All five phases from the design doc are built. It compiles and the window opens; the first
generated shader did not compile, and the four causes found from that run are fixed (see
"Corrections from the first real run"). Whether the HLSL compiles now is still unproven.

---

## Corrections from the first real run

The tool ran, generated `shaders/custom/wind.shad`, and the material system failed with
`ERROR_FILEOPEN` looking for `wind.shader_c`. Four causes, all fixed:

1. **The extension was wrong.** s&box shader source is `.shader`, not the `.shad` the design doc
   specified. A `.shad` is a file the asset pipeline never compiles, so nothing ever produced the
   `.shader_c` the material system then went looking for. Now `ShaderTemplate.Extension`.
2. **Parameter names could collide with engine globals.** `Assets/shaders/pixel_arms.shader` — the
   one shader in this project known to compile — carries a long warning at the top: shader
   parameters are globals in a shared namespace with the material system, and a plain name like
   `g_vColor` can collide and quietly lose. It cost that file several rounds of debugging. Every
   generated parameter is now prefixed `g_flSf*` / `g_vSf*`.
3. **`i.vNormalWs` is a float4 here**, so it needs `.xyz` before `normalize`. Six sites.
4. **`Depth()` → `Depth( S_MODE_DEPTH )`**, matching the shader known to work in this project.

Parameters now also carry `Attribute( "..." )` alongside `UiGroup`, so they can be driven at
runtime as well as edited in the material editor. Without it Hit Flash and Health Reactive are
pointless — the whole premise of those blocks is that game code pushes values at them.

`Assets/shaders/pixel_arms.shader` turned out to be the single most useful reference in the repo
for this work. Read it before changing the emitter.

**There is a stale `Assets/shaders/custom/wind.shad` from that run — delete it.**

---

## How to not waste the next session

**This ran in a cloud container with no engine source and no `dotnet`** (the installer and NuGet
both 403'd through the egress proxy). Nothing here was compiled by me and no test was run — the
corrections above came from a console log, not from a build. That is the exact failure mode
Marionette's own HANDOFF.md was written to prevent, so the provenance below is load-bearing.

**First two things to do, in order:**

1. `dotnet run --project ShaderForge.Tests -- out` — the generator is engine-free, so this runs
   without the editor. It checks block selection, conflict resolution and the structure of the
   emitted HLSL, and writes ten sample `.shader` files to `out/`.
2. Open the project and run **`shaderforge_probe`** in the console (or Shader Forge → Help → Check
   engine shader APIs). It reports which of the assumed shader APIs actually exist.

### What is assumed rather than known

| API | Used for | Confidence |
|---|---|---|
| `File.WriteAllText` to the assets folder | writing the `.shader` | **Confirmed working** — the first run wrote its file. |
| `Project.Current.GetAssetsPath()` | resolving where to write | **Confirmed working** — same run resolved the path. |
| `Material.FromShader( string )` | building the preview material | **Confirmed callable** — it reached the material system, which then failed on the uncompiled shader. |
| `Material.Set( string, float/Color )` | live tweaking | **Assumed.** Probe reports it. |
| `Shader.Load` / `Shader.Schema` | inspecting hand-written shaders | **Assumed, and read by reflection** so a wrong shape costs one panel, not the tool. |
| `SceneObject.SetMaterialOverride( material, string, int )` | per-slot preview | **Assumed.** Falls back to whole-model and says so in the UI. |
| `AssetSystem.All` / `AssetType.Model` | scanning project models | Assumed. Falls back to stock primitives. |
| `ModelRenderer.MaterialOverride` | whole-model preview | High — RigViewport already uses it. |
| `PointLight` + `.Radius` | the fill light | Assumed; no prior use in this repo. |

Everything in that table is funnelled through `Editor/ShaderForgeEditor/ShaderForgeBridge.cs` and
guarded, on purpose: **generating and saving shaders must work even if every preview API is wrong.**
If the preview is dead the tool still writes correct `.shader` files, and the panel says so rather
than the window dying.

### The one thing no test here can check

Whether the generated HLSL actually **compiles**. The tests verify structure — that vertex code
lands in `VS`, that uv warps precede `Material::From`, that braces balance — but only the s&box
shader compiler can judge the HLSL itself. Generate one, watch the console, and expect to fix
small things.

`pixel_arms.shader` confirms `m.Albedo`, `m.Normal`, `m.Roughness`, `m.Metalness`,
`i.vPositionWithOffsetWs.xyz`, `i.vPositionSs.xy` and `i.vTextureCoords.xy`. Still unconfirmed and
therefore the likeliest first failures: **`Material::From( i )`** (that file uses
`Material::Init()` instead), **`m.Emission`**, **`m.Opacity`**, and **`g_flTime`**.

---

## What's here

```
Editor/ShaderForge/            the generator kernel - ENGINE-FREE, no Sandbox/Editor references
  ShaderBlock.cs               block + parameter model; a param declares its HLSL, UI and Material.Set
  BlockLibrary.cs              the locked v1 set of 18 blocks
  ShaderForgeGenerator.cs      tokenise -> match -> resolve conflicts -> result
  ShaderTemplate.cs            emits the .shader file

Editor/ShaderForgeEditor/      the tool
  ShaderForgeWindow.cs         DockWindow: viewport centre, Preview left, Generator right
  ShaderForgeViewport.cs       orbit camera, key/fill/ambient rig, material overrides
  ShaderForgePreviewPanel.cs   model picker, material slot list, load an existing .shader
  ShaderForgeGeneratorPanel.cs description box, Generate, block report, tweak sliders, Save
  ShaderForgeBridge.cs         EVERY engine shader/material call, guarded + the probe concmd
  ShaderForgeModelLibrary.cs   project .vmdl scan and slot reading
  ShaderForgePrimitives.cs     stock shapes, built via the Effigy kernel

ShaderForge.Tests/             console runner over the kernel, same shape as Effigy.Tests
```

Appears in the Tools menu as **Shader Forge**.

### Design decisions worth not re-litigating

- **The kernel is not duplicated.** Effigy keeps two copies (`Effigy/` and `Editor/Effigy/`)
  because it is meant to stay portable to Godot. Shader Forge emits s&box shader format and is
  editor-only, so it lives once in `Editor/ShaderForge` and the test project compiles from there.
- **Blocks contribute to five slots** — Common, Vertex, Uv, Surface, Post — rather than the design
  doc's two. Splitting Uv out is what lets a warp run before `Material::From` samples textures;
  splitting Post out is what lets Toon band the *lit* result. Unrelated blocks combine because
  each only writes to its own slot.
- **`SFPulse()` is always emitted**, returning `1.0` when no time-modulation block was selected.
  That is the whole mechanism behind "glowing edges that pulse": Emissive multiplies by it
  unconditionally, and neither block knows the other exists.
- **Generate writes the file.** A `.shader` has to be on disk for the asset pipeline to compile it,
  so there is no separate in-memory preview path that could drift from what gets saved.
- **Tweak controls are built from the generator's own parameter list**, not from a schema read back
  off the compiled shader. The generator declared them, so it already knows their names, ranges
  and defaults exactly — a round trip through `Shader.Schema` would be asking an unverified API
  for something already known.
- **Nothing fails silently.** A description that matches nothing says "no block for that yet" and
  names the words it didn't understand; a conflict between two blocks reports the loser. That is
  the living-library promise from the design doc, and it is what the misses are supposed to feed.

---

## Scope: what it does and does not do

18 blocks: Emissive, Dissolve, Toon, Glass, Water, Wind, Hit Flash, Rim Light, Hologram, Outline,
Time Modulation, Colour Tint, Health Reactive, Loot Glow, Interactable Highlight, Team Colour,
Snow Cover, Heat Distortion.

Three of them are honest approximations, documented at their definitions:

- **Toon** bands the shaded luminance rather than replacing the lighting model. A real cel shader
  owns the whole shading path and loses everything else the standard model gives you.
- **Outline** is a silhouette band from the view-facing term, not an inverted hull. A hull outline
  is a second pass, and multi-pass is out of v1 scope.
- **Heat Distortion** warps the surface's own UVs, not the frame buffer. True screen-space haze
  needs a translucent pass and a frame-buffer grab. On an untextured surface it will look like
  nothing is happening — that is expected, not a bug.

**Do not grow the block library to cover hypotheticals.** The rule from the design doc is that
every prompt the tool cannot match is the roadmap for the next block. The generator already names
the words it missed; those are the queue.

---

## Where to pick this up

In order of what will actually block people:

1. Run the tests and the probe. Fix whatever they say.
2. Generate one shader and get it compiling. Expect a few field-name corrections.
3. The preview panel's "load an existing .shader" path takes a typed path. A real asset picker
   (`RigControlWindow.OpenPicker` is the precedent) would be better once the rest works.
4. Per-slot material override is written but unproven — the fallback message in the preview panel
   is what tells you which case you are in.
