# Marionette

Three s&box editor tools that share one thesis: **make a usable, rigged, animated, shaded model
without leaving the editor.**

| Tool | What it is | Where |
|---|---|---|
| **Rig Control Editor** | A control-rig animation editor. Pose bones, keyframe them, solve IK. | `Code/RigControl`, `Editor/RigControlEditor` |
| **Effigy** | A parametric CAD modeller. Sketch, extrude, boolean, rig, export. | `Effigy`, `Editor/EffigyEditor` |
| **Shader Forge** | Describe a shader in English, get a working `.shader` back. | `Editor/ShaderForge`, `Editor/ShaderForgeEditor` |

They are meant to run as a pipeline, and each stage feeds the next:

```
Effigy: CAD → subdivide → sculpt → bake → bones
                                            └→ Marionette: pose, keyframe, play back
Shader Forge: give it a surface
```

**The low-poly cage the CAD stage produces is the spine of the whole thing.** It carries the UVs, it
receives baked sculpt detail, it is what gets skinned, and it is what Marionette ends up posing.
Nothing downstream works if the cage is not clean — which is the entire reason the modeller starts
parametric rather than starting with a sculpt.

For what is actually finished, see [WHAT-IS-BUILT.md](WHAT-IS-BUILT.md). For what is not, see
[WHAT-IS-LEFT.md](WHAT-IS-LEFT.md). For how to work in this repo, see [HANDOFF.md](HANDOFF.md).

---

## Install

**1. Get the files.**

```
git clone https://github.com/themightypooh/marionette.git
```

**2. Copy the folders you want into your own s&box project.** Each tool is independent.

```
Rig Control Editor:
  Code/RigControl/          →  YourProject/Code/RigControl/
  Editor/RigControlEditor/  →  YourProject/Editor/RigControlEditor/

Effigy:
  Effigy/                   →  YourProject/Effigy/
  Editor/Effigy/            →  YourProject/Editor/Effigy/        (a mirror — see HANDOFF.md)
  Editor/EffigyEditor/      →  YourProject/Editor/EffigyEditor/

Shader Forge:
  Editor/ShaderForge/       →  YourProject/Editor/ShaderForge/
  Editor/ShaderForgeEditor/ →  YourProject/Editor/ShaderForgeEditor/
```

**3. Open your project.** It compiles on load and the tools appear in the Tools menu. Plain C#
against the editor API, no dependencies.

<details>
<summary>Why copying rather than a package reference</summary>

s&box can reference other packages — Project Settings → Packages — and in principle you'd add
`pooh.marionette` there and be done. As of writing, that screen carries Facepunch's own warning:

> This stuff hasn't been properly end to end tested - please don't expect it to work just yet!

It's also unclear whether a referenced package's `Editor/` code compiles into the consuming
project's editor assembly at all, which is precisely what an editor tool needs. So: copy the
folders. It works today.

</details>

### Optional sample content

Only needed for Rig Control's bundled example clip and tutorial. The tools work without it.

```
Assets/models/lightswitch/     →  YourProject/Assets/models/lightswitch/
Assets/materials/lightswitch/  →  YourProject/Assets/materials/lightswitch/
Assets/animations/             →  YourProject/Assets/animations/
```

The first-person arms the tutorial uses come from the **citizen** addon, which ships with s&box.

---

## Rig Control Editor

Open it from the Tools menu, or double-click a `.riganim` asset. It starts with the Citizen's
first-person arms loaded and a tutorial panel offering to walk you through building a
reach-and-flip-a-switch animation. Skip it if you'd rather poke around.

- **Pose bones directly in a 3D viewport.** Click a bone's dot, drag to rotate. Hold `E` to move
  instead. The skeleton draws x-ray over the mesh, so bones buried inside the model stay visible
  and clickable.
- **Keyframe timeline**, one lane per bone, with the interpolation curve drawn between keys —
  sampled from the real easing function, not an approximation of it.
- **Three interpolation modes** — Smooth (eased, the default), Linear, Stepped (hold + snap). The
  keyframe's *shape* tells you which: circle, diamond, square.
- **IK and rotation limits.** Two-bone IK solves in closed form; drag a hand and the elbow and
  shoulder follow. Limits clamp a joint so it can't bend backwards.
- **Undo/redo** with labelled steps. One drag is one undo step.
- **First person view** — frames the model off its own camera bone, so viewmodel arms are judged
  the way the player will actually see them.
- **Reference props** — drop a static model in the viewport and pose against it.
- **Numeric inspector**, so exact values can be typed rather than eyeballed off a drag.
- **Bone hiding** — right-click a bone to hide it and its children.
- **Prop-attach events** — spawn a model on a bone for a frame range.

### Getting started

1. Set **Source Model** in the BonesObject tab to any skinned model.
2. Click a bone in the viewport, drag to pose it.
3. Press `K` (or the diamond button on the timeline) to key it at the playhead.
4. Move the playhead, pose again, and play it back.

The status bar explains whatever you're hovering, and **Help → Start Animation Tutorial** walks
through building one real animation.

### Assets

Two asset types, deliberately separate so several clips can share one rig:

| Type | Extension | Holds |
|---|---|---|
| Rig Animation | `.riganim` | keyframes, anim events, frame rate |
| Control Rig | `.ctrlrig` | the model reference and its IK/Limit constraints |

### Design decisions

- **Dragging rotates by default**, not translates. Joints pivot; they don't slide. Translating a
  bone stretches the skin and is the most common way a first pose ends up looking broken.
- **Constraints bake into keyframes** rather than being re-solved at playback, so a clip plays
  identically in the editor, in game, and anywhere else that reads a keyframe — no solver involved.
  The trade is that changing a constraint doesn't retroactively change existing poses.
- **Smooth is the default easing.** Linear interpolation moves at constant speed and stops dead,
  which reads as robotic no matter how good the poses are.

---

## Effigy

A parametric CAD modeller, built on an **engine-free kernel** — `Effigy/` contains no reference to
any engine type anywhere in it, and compiles under s&box, Godot's C#, or a bare console runner.
That is not tidiness; it is what makes the whole thing testable without an engine in front of you,
and 1407 headless checks are the payoff.

### The loop

Sketch on a plane (or on a face of an existing solid), then turn the sketch into a solid:

```csharp
var studio = new PartStudio();

var sketch = studio.Add( new SketchFeature() );
sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 4, 2 ) );

studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
studio.Rebuild();
```

- **An ordered feature history** with rollback and incremental rebuild — modelled on Onshape's Part
  Studio, because that structure is what makes a modeller parametric rather than a stack of bakes.
- **A sketcher** with lines, arcs, circles, ellipses and splines; closed regions found rather than
  declared; trim, extend, fillet and offset as in-place edits.
- **A constraint solver** — Levenberg-Marquardt over seventeen constraint kinds, reporting degrees
  of freedom from the Jacobian's rank so a sketch is diagnosable rather than mysterious.
- **Solids** — extrude, revolve, sweep, loft, shell, bevel, mirror, linear and circular pattern,
  subdivide, transform, UV project, per-face materials.
- **Booleans that actually cut**, through s&box's own `PolygonMesh`.
- **Rigging** — a skeleton, auto-weighting, and export as a real skinned `.vmdl` that Rig Control
  then opens and poses.

### Assets and export

`.effigy` is the document — hand-written text, one record per line, diffable, with fields found by
reflection so a new feature saves the moment it is written. Export writes DMX (what ModelDoc
actually imports) or OBJ for static geometry, plus a hand-written KV3 `.vmdl` beside it.

---

## Shader Forge

Type `glowing` and the preview sphere lights up as the word lands — no Generate click. Tap words on
the left, or hit Surprise me. Forge writes a slim `.shader` containing just the blocks you asked
for.

18 blocks: Emissive, Dissolve, Toon, Glass, Water, Wind, Hit Flash, Rim Light, Hologram, Outline,
Time Modulation, Colour Tint, Health Reactive, Loot Glow, Interactable Highlight, Team Colour, Snow
Cover, Heat Distortion.

**Nothing fails silently.** A description that matches nothing says "no block for that yet" and
names the words it didn't understand; a conflict between two blocks reports the loser. Those misses
are the roadmap for the next block — the library is not meant to grow to cover hypotheticals.

---

## Health check

```
rig_test_pose        verifies bone-write timing and the pose path
rig_test_ik          verifies the two-bone IK solve
effigy_test_boolean  verifies the mesh boolean against known arithmetic
effigy_dump_tree     prints boundary edges, bridged faces and reinstated openings
shaderforge_probe    reports which assumed shader APIs actually exist
```

And the kernel suite, which needs no engine at all:

```sh
./tools/test.sh
```

## License

MIT.

One exception, and it never ships: `Editor/HaloMount` is untracked, unrelated personal work
carrying GPL-3.0 binaries. See [HANDOFF.md](HANDOFF.md).
