# Toolshed

Two s&box editor tools that share one thesis: **make a usable, rigged, animated
model without leaving the editor.**

| Tool | What it is | Where |
|---|---|---|
| **Marionette** | A control-rig animation editor. Pose bones, keyframe them, solve IK. | `Code/RigControl`, `Editor/RigControlEditor` |
| **Effigy** | A parametric CAD modeller. Sketch, extrude, boolean, rig, export. | `Effigy`, `Editor/EffigyEditor` |

Both ship together as one s&box library, **Geppetto**, which is the whole of
``. This repo is that library plus the project that mounts it —
so the tools are developed exactly as a consumer gets them.

They are meant to run as a pipeline, and each stage feeds the next:

```
Effigy: CAD → subdivide → sculpt → bake → bones
                                            └→ Marionette: pose, keyframe, play back
```

**The low-poly cage the CAD stage produces is the spine of the whole thing.** It
carries the UVs, it receives baked sculpt detail, it is what gets skinned, and
it is what Marionette ends up posing. Nothing downstream works if the cage is
not clean — which is the entire reason the modeller starts parametric rather
than starting with a sculpt.

Rig Control is usable — open it and run the tutorial. Effigy's kernel is
covered by a headless test suite. Issues and PRs welcome; see
[CONTRIBUTING.md](CONTRIBUTING.md).

For how to work in this repo, see `HANDOFF.md` in the dev notes, which live in a
`geppetto-docs/dev/` folder beside this checkout rather than inside it — an s&box
package ships whatever sits in the project directory, so private notes kept here
went out to everyone who installed it.

---

## Install

**1. Get the files.**

```
git clone https://github.com/themightypooh/Geppetto.git
```

You can open `geppetto.sbproj` in s&box and use this project as-is.

**2. Or drop Geppetto into your own s&box project.** Copy one folder:

```
  →  YourProject/
```

That one folder, not the clone. Dropping the whole repo into `Libraries/` gives
you this project's own host code as well, which references dev-only files that
are not published, and the editor build fails.

s&box compiles every library under `Libraries/` alongside your project and
mounts it, so both tools appear in the Tools menu with nothing else to wire up.
You get Marionette and Effigy together — they share the mesh kernel and are not
separable.

**3. Open your project.** It compiles on load and the tools appear in the Tools
menu. Plain C# against the editor API, no dependencies.

<details>
<summary>Why a copied folder rather than a package reference</summary>

s&box can reference published packages — Project Settings → Packages — and in
principle you'd add `pooh.geppetto` there and be done. As of writing, that
screen carries Facepunch's own warning:

> This stuff hasn't been properly end to end tested - please don't expect it to work just yet!

The unknown that matters for an editor tool is whether a referenced package's
`Editor/` code compiles into the consuming project's editor assembly at all. A
library folder copied into `Libraries/` definitely does, which is why that is
the documented route. Try the package reference by all means; fall back to the
copy if the Tools menu comes up empty.

</details>

### Optional sample content

Only needed for Rig Control's bundled example clip and tutorial. The tools work
without it.

```
Assets/models/lightswitch/     →  YourProject/Assets/models/lightswitch/
Assets/materials/lightswitch/  →  YourProject/Assets/materials/lightswitch/
Assets/animations/             →  YourProject/Assets/animations/
```

The first-person arms the tutorial uses come from the **citizen** addon, which
ships with s&box.

---

## Marionette

Open it from the Tools menu, or double-click a `.riganim` asset. It starts with
the Citizen's first-person arms loaded and a tutorial panel offering to walk you
through building a reach-and-flip-a-switch animation. Skip it if you'd rather
poke around.

- **Pose bones directly in a 3D viewport.** Click a bone's dot, drag to rotate.
  Hold `E` to move instead. The skeleton draws x-ray over the mesh, so bones
  buried inside the model stay visible and clickable.
- **Keyframe timeline**, one lane per bone, with the interpolation curve drawn
  between keys — sampled from the real easing function, not an approximation of
  it.
- **Three interpolation modes** — Smooth (eased, the default), Linear, Stepped
  (hold + snap). The keyframe's *shape* tells you which: circle, diamond, square.
- **IK and rotation limits.** Two-bone IK solves in closed form; drag a hand and
  the elbow and shoulder follow. Limits clamp a joint so it can't bend backwards.
- **Undo/redo** with labelled steps. One drag is one undo step.
- **First person view** — frames the model off its own camera bone, so viewmodel
  arms are judged the way the player will actually see them.
- **Reference props** — drop a model in the viewport and pose against it.
- **Whole parts animate too.** Drag a reference prop and it gets its own timeline
  lane, keyed exactly like a bone — so a door, a lever or a magazine moves on the
  same playhead as the hand that works it, instead of being a static stand-in you
  have to animate somewhere else.
- **Several objects at once, each with its own skeleton.** A prop that is a rigged
  model gets bone handles like the main model does: pose the hand, the weapon it
  holds, and that weapon's bolt in one clip, on one playhead. Tracks are named
  `object/bone`, so two objects can each have a bone called `root`. The main
  model's bones keep their bare names, so every clip you have already made reads
  back unchanged.
- **Objects, with parts.** A clip holds objects that are not the main model — a
  door, a weapon, a fridge — and each one is made of parts. Move the object and
  everything in it goes; move a part and it moves within the object. Every one of
  them records to the timeline exactly as a bone does: drag it, and there is a key
  at the playhead. They live in their own **Objects** tree under the bone tree,
  each object collapsible so a forty-part import doesn't bury the arm you're
  posing.
- **Import an OBJ, split into its parts.** File → Import OBJ loads a Wavefront
  file and makes one object with one part per `o`/`g` group in it, so a door
  exported with its handle and hinge kept separate arrives as one door you can
  place and three pieces you can animate — no compiling a `.vmdl` per piece first.
  The mesh is copied beside the `.riganim` so the clip keeps working when the
  original moves.
- Play it all back with `RigAnimPlayerComponent`'s **Parts** list: object name on
  the left, the GameObject it drives on the right. That one row both places the
  object and hosts its bones. `.vmdl` export is still the main model's bones only
  — it is a model animation, and the rest of the scene is not in it.
- **Numeric inspector**, so exact values can be typed rather than eyeballed off
  a drag.
- **Bone hiding** — right-click a bone to hide it and its children.
- **Prop-attach events** — spawn a model on a bone for a frame range.

### Getting started

1. Set **Source Model** in the BonesObject tab to any skinned model.
2. Click a bone in the viewport, drag to pose it.
3. Press `K` (or the diamond button on the timeline) to key it at the playhead.
4. Move the playhead, pose again, and play it back.

The status bar explains whatever you're hovering, and **Help → Start Animation
Tutorial** walks through building one real animation.

### Assets

Two asset types, deliberately separate so several clips can share one rig:

| Type | Extension | Holds |
|---|---|---|
| Rig Animation | `.riganim` | keyframes, anim events, frame rate |
| Control Rig | `.ctrlrig` | the model reference and its IK/Limit constraints |

### Design decisions

- **Dragging rotates by default**, not translates. Joints pivot; they don't
  slide. Translating a bone stretches the skin and is the most common way a
  first pose ends up looking broken.
- **Constraints bake into keyframes** rather than being re-solved at playback,
  so a clip plays identically in the editor, in game, and anywhere else that
  reads a keyframe — no solver involved. The trade is that changing a constraint
  doesn't retroactively change existing poses.
- **Smooth is the default easing.** Linear interpolation moves at constant speed
  and stops dead, which reads as robotic no matter how good the poses are.

---

## Effigy

A parametric CAD modeller, built on an **engine-free kernel** — `Effigy/`
contains no reference to any engine type anywhere in it, and compiles under
s&box, Godot's C#, or a bare console runner. That is not tidiness; it is what
makes the whole thing testable without an engine in front of you, and 2700+
headless checks are the payoff.

### The loop

Sketch on a plane (or on a face of an existing solid), then turn the sketch into
a solid:

```csharp
var studio = new PartStudio();

var sketch = studio.Add( new SketchFeature() );
sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 4, 2 ) );

studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
studio.Rebuild();
```

- **An ordered feature history** with rollback and incremental rebuild —
  modelled on Onshape's Part Studio, because that structure is what makes a
  modeller parametric rather than a stack of bakes.
- **A sketcher** with lines, arcs, circles, ellipses and splines; closed regions
  found rather than declared; trim, extend, fillet and offset as in-place edits.
- **A constraint solver** — Levenberg-Marquardt over seventeen constraint kinds,
  reporting degrees of freedom from the Jacobian's rank so a sketch is
  diagnosable rather than mysterious.
- **Solids** — extrude, revolve, sweep, loft, shell, bevel, mirror, linear and
  circular pattern, subdivide, transform, UV project, per-face materials.
- **Booleans that actually cut**, through s&box's own `PolygonMesh`.
- **Rigging** — a skeleton, auto-weighting, and export as a real skinned `.vmdl`
  that Rig Control then opens and poses.

### Exporting

Everything you need is on the **File** menu.

**Use `Compile .vmdl`.** That's the one that gives you a model you can actually
drop into a scene. It saves the model, builds it, and shows you the result in
the viewport.

**Use `Export OBJ`** only if you want the raw shape to open in Blender or
similar. It's just geometry — no bones, no animation.

Files go to **`Assets/models/effigy/`** in your project. Nothing gets written
anywhere else.

#### If your model has bones

`Compile .vmdl` notices and does the extra work: it figures out which bones move
which parts of the model, then builds it so it can be posed and animated.

You get `export.vmdl` (the model), plus `export.dmx` and `export.smd` — those two
are the same thing in two formats, kept around so other 3D programs can open your
model too. You can ignore them.

#### If something goes wrong

**"cannot compile — studio has errors or no bodies"** means a feature is broken.
Look at the feature list on the left; the bad one is marked. Fix it and try again.

**The build failed** — the error in the console says why. Your exported files are
still on disk, so nothing is lost.

#### Making a playermodel

A playermodel is a character you can walk around in, using the animations s&box
already ships. You don't animate it yourself — you borrow the built-in
character's walk, run, jump, crouch and the rest.

The quickest way in is **Help → Start Playermodel Tutorial**, which walks you
through it in the editor and offers an example robot to practise on. This is the
same thing in writing.

**Your model keeps its own shape.** Effigy slides the built-in character's
skeleton *inside* your model rather than reshaping your model to match its body.
So the animations only ever say how far each joint bends — never how long your
arms are, or how broad your shoulders. A stocky robot stays stocky and still
walks. This is the whole trick, and it's why you don't have to model to
somebody else's proportions.

There are three things you do have to get right.

**1. Stand it the way the built-in character stands.**

- Feet flat on the floor — the lowest point of the model at zero.
- Facing forward, along the red arrow.
- Arms out to the sides, legs straight down.
- Hips about **31 units** off the floor.

The hips are the one measurement that matters. The walk decides how high they
ride, so a model much taller or shorter at the hips will float or sink. Legs a
little shorter than the built-in character's leave the knees looking slightly
straight. Everything else about your proportions is yours to choose.

**2. Build the model as separate parts, one per bone.** A palm and three finger
joints, not one merged hand. Each part needs to be **longer along its bone than
it is across** — Effigy works out where a bone goes by measuring which way the
part is longest, so a torso that's wider than it is tall gets a bone across the
chest instead of up the spine. A perfect cube has no longest direction at all
and gets skipped.

**3. Name the bones what the animations call them, hanging off the right
parent.** This is the part that has to be exact. A bone spelled any other way is
a bone the animations can't find, and it just holds still.

| | Bones, each hanging off the one before it |
|---|---|
| Body | `pelvis` → `spine_01` → `spine_02` → `chest` → `neck` → `head` |
| Each arm | `chest` → `clavicle_L` → `upperarm_L` → `forearm_L` → `hand_L` |
| Each leg | `pelvis` → `thigh_L` → `calf_L` → `foot_L` (→ `toe_L`, optional) |
| Optional | fingers `index_01_L` … `ring_03_L`, `pinky_*`, `thumb_01_L`, `thumb_02_L` |

`_L` is the **model's** left — the side on your left when you stand behind it.
Swap `_L` for `_R` for the other side. If your rig already uses the built-in
character's own bone names (`arm_upper_L`, `leg_upper_L`, `spine_0`), those work
too — you don't have to rename anything.

The order you make bones in is the order they hang in: select a bone, then press
**Bone from Part**, and the new bone hangs off the selected one. So start at the
hips and work outwards. You can select two parts — both shoulders, both thighs —
and press it once.

**Then build it.** **File → Make Player** compiles the model and writes a small
scene next to it with a floor, a light and a player wearing your model. Open
that scene and press Play. **File → Compile Playermodel** does just the model,
for when you have a scene of your own.

**Read the console afterwards.** It lists any bone whose name the animations
didn't recognise. That isn't an error — the bone just holds still — but it's
almost always a typo, and it's the only place you'll be told.

Two things that catch people out:

- **A limb that stays rigid is a naming problem, not a weighting problem.**
  Check the spelling first, then check what it hangs off.
- **Don't set a pivot for a playermodel.** Its origin has to be between its
  feet, so `Make Player` and `Compile Playermodel` ignore the pivot handle. The
  ordinary `Compile .vmdl` still uses it.

#### Getting animation into your game

Make the model in Effigy, animate it in Marionette, then get it into your game.
There are two ways to do that last part, and they suit different things.

**Bake the animation into the model.** The model file carries its own
animations, so it behaves like any other animated model you'd use — nothing
extra to add in the scene, and it works even if you later remove these tools
from your project.

1. Model and rig it in Effigy, then **Compile .vmdl**.
2. Open that model in Marionette, pose it, and save.
3. Back in Effigy: **File → Animation Clips...** → **Add Clip** → pick what you
   saved.
4. **Compile .vmdl** again.

Your model now has the animation inside it, under whatever name you typed in the
clip list — that's the name you use to play it. Each clip has a **Looping**
switch for whether it repeats or plays once. Add as many clips as you want.

**Or play the animation file directly.** Add the `RigAnimPlayerComponent` to
your object, point it at your animation and at the model, and it plays. No
recompiling, so it's quicker while you're still changing the animation a lot.
The trade is that your project keeps needing these tools installed, and that
component drives the bones itself — so don't use it on something you also want
animated the normal way.

Rule of thumb: **use the component while you're iterating, bake it in when
you're done.**

Two things that catch people out:

- **An animation only works on the model it was made for.** Bones are matched by
  name, so a clip made for a different character does nothing at all. Effigy
  checks before exporting and tells you which bones didn't match — check the
  console if a clip seems to be ignored.
- **The clip list is forgotten when you close Effigy.** It's an export setting
  rather than part of your model, so you'll need to add the clips again next
  time. Your animation files themselves are untouched.

> **Heads up:** baking clips in is new. The file format is confirmed working
> against the engine's own model compiler, but the menu and dialog haven't had a
> real workout yet. If they give you trouble, the component route above is the
> well-trodden one.

### Your saved file

Your work saves as an `.effigy` file. It's plain text, one line per thing you
made, so it works properly with git — you can see exactly what changed between
two versions instead of getting "binary file differs".

---

## License

MIT. See [LICENSE](LICENSE).
