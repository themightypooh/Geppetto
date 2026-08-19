# Marionette

A control-rig animation editor 

# Effigy 
A parametric CAD modeling tool

## Install

**1. Get the files.** Clone, or download the ZIP from the green **Code** button above.

```
git clone https://github.com/themightypooh/marionette.git
```

**2. Copy two folders into your own s&box project.**

```
Code/RigControl/          →  YourProject/Code/RigControl/
Editor/RigControlEditor/  →  YourProject/Editor/RigControlEditor/
```

**3. Open your project.** It compiles on load, and **Rig Control Editor** appears in the Tools
menu. Nothing else to configure — it's plain C# against the editor API with no dependencies.

### Optional: the sample content

Only needed for the bundled example clip and for the tutorial's light switch. The tool works
without them.

```
Assets/models/lightswitch/     →  YourProject/Assets/models/lightswitch/
Assets/materials/lightswitch/  →  YourProject/Assets/materials/lightswitch/
Assets/animations/             →  YourProject/Assets/animations/
```

The first-person arms the tutorial uses come from the **citizen** addon, which ships with s&box —
nothing to copy for those.

### First run

Open **Rig Control Editor** from the Tools menu. It starts with the Citizen's first-person arms
loaded and a tutorial panel offering to walk you through building a reach-and-flip-a-switch
animation. Skip it if you'd rather poke around; it's one button and it stays out of your way.

<details>
<summary>Why copying rather than a package reference</summary>

s&box can reference other packages — Project Settings → Packages — and in principle you'd add
`pooh.marionette` there and be done. As of writing, that screen carries Facepunch's own warning:

> This stuff hasn't been properly end to end tested - please don't expect it to work just yet!

It's also unclear whether a referenced package's `Editor/` code compiles into the consuming
project's editor assembly at all, which is precisely what an editor tool needs. So: copy the
folders. It works today, and it's two folders.

Once package references are finished this section gets shorter.

</details>

## What it does

- **Pose bones directly in a 3D viewport.** Click a bone's dot, drag to rotate it. Hold `E` to
  move instead. The skeleton draws x-ray over the mesh so bones buried inside the model are still
  visible and clickable.
- **Keyframe timeline**, one lane per bone, with the interpolation curve drawn between keys —
  sampled from the real easing function, not an approximation of it.
- **Three interpolation modes** — Smooth (eased, the default), Linear, and Stepped (hold + snap).
  The keyframe's *shape* tells you which: circle, diamond, square.
- **IK and rotation limits.** Two-bone IK solves in closed form; drag a hand and the elbow and
  shoulder follow. Limits clamp a joint's rotation so it can't bend backwards.
- **Undo/redo** with labelled steps, and one drag is one undo step.
- **First person view** — frames the model off its own camera bone, so viewmodel arms are judged
  the way the player will actually see them. You can still look around inside it.
- **Reference props** — drop a static model in the viewport and pose against it. Click it to grab
  it, drag to move, hold `E` to rotate.
- **Numeric inspector**, so exact values can be typed rather than eyeballed off a drag.
- **Bone hiding** — right-click a bone to hide it and its children. Every rig carries plumbing
  nobody poses; this gets it out of the way.
- **Prop-attach events** — spawn a model on a bone for a frame range, for anything the character
  needs to be holding partway through a clip.

## Getting started

Open **Rig Control Editor** from the Tools menu, or double-click a `.riganim` asset.

1. Set **Source Model** in the BonesObject tab to any skinned model.
2. Click a bone in the viewport, drag to pose it.
3. Press `K` (or the diamond button on the timeline) to key it at the playhead.
4. Move the playhead, pose again, and play it back.

The status bar along the bottom explains whatever you're hovering, and there's a guided tutorial
under **Help → Start Animation Tutorial** that walks through building one real animation.

## Assets

Two asset types, deliberately separate so several clips can share one rig:

| Type | Extension | Holds |
|---|---|---|
| Rig Animation | `.riganim` | keyframes, anim events, frame rate |
| Control Rig | `.ctrlrig` | the model reference and its IK/Limit constraints |

## Sample content

| What | Where | Needed? |
|---|---|---|
| First-person arms | `models/first_person/first_person_arms_preview.vmdl` | ships with s&box's **citizen** addon - nothing to copy |
| Light switch | `Assets/models/lightswitch/` + `Assets/materials/lightswitch/` | optional, used by the tutorial |
| Example clip | `Assets/animations/reach_and_flip_switch.riganim` | optional |
| Pixel arms shader | `Assets/shaders/pixel_arms.shader` | optional, a PS1-style look for viewmodel arms |

None of it is required. The tool works with any skinned model, including the stock Citizen.

## Notes on design

A few choices that are deliberate rather than accidental:

- **Dragging rotates by default**, not translates. Joints pivot; they don't slide. Translating a
  bone stretches the skin and is the most common way a first pose ends up looking broken.
- **Constraints bake into keyframes** rather than being re-solved at playback, so a clip plays
  identically in the editor, in game, and anywhere else that reads a keyframe — with no solver
  involved. The trade is that changing a constraint doesn't retroactively change existing poses.
- **Smooth is the default easing.** Linear interpolation moves a bone at constant speed and stops
  dead, which reads as robotic no matter how good the poses are.

## Health check

Two console commands verify the engine behaviours the tool stands on, so an engine update that
breaks them says so in one command:

```
rig_test_pose
rig_test_ik
```

## License

MIT
