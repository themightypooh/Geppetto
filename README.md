# Marionette

A control-rig animation editor for [s&box](https://sbox.game) — pose a skinned model's bones,
keyframe it, and play it back, without round-tripping through Blender.

Built because there is currently no in-editor way to author character animation in s&box, and
"open Blender, export, reimport, discover it's wrong, repeat" is not a workflow.

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
- **Prop-attach events** — spawn a model on a bone for a frame range, for anything the character
  needs to be holding partway through a clip.

## Getting started

Open **Rig Control Editor** from the Tools menu, or double-click a `.riganim` asset.

1. Set **Source Model** in the BonesObject tab to any skinned model.
2. Click a bone in the viewport, drag to pose it.
3. Press `K` (or the diamond button on the timeline) to key it at the playhead.
4. Move the playhead, pose again, and play it back.

The status bar along the bottom explains whatever you're hovering, and there's a guided tutorial
under **Help → Start Wave Tutorial** that walks through building one real animation.

## Assets

Two asset types, deliberately separate so several clips can share one rig:

| Type | Extension | Holds |
|---|---|---|
| Rig Animation | `.riganim` | keyframes, anim events, frame rate |
| Control Rig | `.ctrlrig` | the model reference and its IK/Limit constraints |

## Sample content

`Assets/models/first_person/fp_arms.vmdl` is a rigged pair of first-person arms included as
something to pose immediately, along with a pixelated PS1-style shader (`pixel_arms.shader`).
Neither is required by the tool — it works with any skinned model, including the stock Citizen.

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
