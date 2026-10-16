# Geppetto

Two s&box editor tools for making a rigged, animated model without leaving the editor.

| Tool | What it does |
|---|---|
| **Effigy** | A parametric modeller. Sketch, extrude, boolean, sculpt, paint, rig, dress, and export a real `.vmdl`. |
| **Marionette** | An animation editor. Pose a skinned model's bones, keyframe them, solve IK, and play it back in game. |

They're built to be used in order: make the model in Effigy, compile it, open it in Marionette,
animate it, and either bake the clips back into the model or play them with a component.

```
Effigy:  sketch → solid → sculpt / paint → rig → Compile .vmdl
                                                     └→ Marionette: pose → keyframe → export / play
```

## Install

**From sbox.game:** add the **Geppetto** library (`pooh.geppetto`) to your project.

**From source:** clone this repo into your project's `Libraries/` folder:

```sh
git clone https://github.com/themightypooh/Geppetto.git YourProject/Libraries/geppetto
```

Either way, open the project. s&box compiles the library with it, and **Effigy** and
**Marionette** appear in the **Tools** menu. You can also open `geppetto.sbproj` on its own to
work on the tools themselves.

---

## Effigy

Open it from **Tools ▸ Effigy**, or double-click a `.effigy` file. **Help** has four guided
tutorials (a house, a rigged signpost, a hand, and a playermodel), which are the quickest way to
learn it.

### Workspaces

The bar along the top switches between five workspaces:

- **CAD**: sketches, solids, and the feature tree. Every step is a feature you can go back and
  edit, and everything after it rebuilds. Tools are grouped into Sketch, Solid, Detail, Repeat
  and Finish.
- **Model**: shape a body by hand, Blender-style. Object, Edit, Sculpt and Retopo modes. Edit
  has vertex/edge/face selection, extrude, inset, bevel, loop cut, knife, bridge, live
  modifiers (mirror, array, subdivide, thickness), UV tools and weight painting. Sculpt has
  multires brushes and masks. Retopo traces a clean cage over a dense sculpt.
- **Paint**: paint colour onto a body's texture.
- **Rig**: place bones, bind parts to them, paint weights, make bones soft, and preview a pose.
- **Clothing**: load a rigged model, cut garments to fit it, add trim, fabric and fur, and
  publish them as clothing or live cloth.

### Saving

Your work saves as a `.effigy` file. It's plain text, one line per feature, so git can show
exactly what changed. Imported meshes, sculpts and hand edits are saved in folders next to it
(`name.import/`, `name.sculpt/`, `name.meshedit/`). Keep them with the file.

### Exporting

Everything is on the **File** menu, and everything goes to **`Assets/models/effigy/`**, named
after your document.

| Command | Gives you |
|---|---|
| **Compile .vmdl** | A model you can drop into a scene. If the model has bones, it's skinned, and it includes any animation clips you've added. |
| **Compile Playermodel** | A model on the Citizen skeleton that walks with s&box's own animations. |
| **Make Player** | The playermodel, plus a test scene at `Assets/scenes/<name>_walk.scene` with a floor, a light and a player wearing it. |
| **Export OBJ** | Just the shape, for other 3D programs. |
| **Export FBX** | The shape with its bones and skin weights, for Blender. |

Tick **Export with LODs** to also write half- and quarter-detail versions with a static model.
**Collision Report** prints the model's collision hulls to the console.

If the compile says the studio has errors, a feature in the tree is marked red. Fix it and
compile again. If the engine's build fails, the console says why, and your exported files are
still on disk.

### Making a playermodel

A playermodel borrows the Citizen's walk, run, jump and crouch. Effigy fits the Citizen's
skeleton *inside* your model, so your model keeps its own proportions. **Help ▸ Start Playermodel
Tutorial** walks you through it. Three things matter:

1. **Stand it like the Citizen:** feet on the floor at zero, facing forward along the red arrow,
   arms out, legs straight, hips about **31 units** up. The hip height matters most; too far off
   and the model floats or sinks.
2. **Build it from separate parts, one per bone,** each longer along its bone than across. In the
   Rig workspace, select a part and press **Bone from Part**; the new bone hangs off whichever
   bone is selected, so start at the hips and work outwards.
3. **Name the bones the way the animations expect:**

   | | Chain |
   |---|---|
   | Body | `pelvis` → `spine_01` → `spine_02` → `chest` → `neck` → `head` |
   | Each arm | `chest` → `clavicle_L` → `upperarm_L` → `forearm_L` → `hand_L` |
   | Each leg | `pelvis` → `thigh_L` → `calf_L` → `foot_L` (→ `toe_L`) |
   | Optional | fingers `index_01_L`…, `thumb_01_L`… |

   `_L` is the model's own left. The Citizen's own names (`arm_upper_L`, `leg_upper_L`,
   `spine_0`) work too.

Then **File ▸ Make Player**, open the scene it wrote, and press Play. To use the model in your
own scene, set the **Skinned Model Renderer** on your Player Controller's **Body** to it, and
leave **Use Anim Graph** ticked.

A limb that stays rigid is almost always a misspelled bone name. The console lists every bone
the animations didn't recognise.

### Clothing

In the **Clothing** workspace, load the model to dress (it's a reference and is never
exported), then add a **Garment**: T-shirt, long sleeve, tank top, trousers, shorts, beanie,
gloves, socks or bodysuit. A garment is a recipe cut from the body's bones, not a fixed mesh,
so it refits when the body changes. Adjust the fit, length and drape; add **Trim** (frill,
ribbon, pleats, fringe), a **Fabric** (denim, leather, plaid, and more) or **Fur**.

**Make clothing** compiles it as a clothing item for a Citizen or Dresser. **Make live cloth**
writes a `.cloth.json` for the **Garment Cloth** component, which simulates it on the wearer
in game.

### Effigy from code

The modelling kernel is plain C# with no engine dependency, so models can be built from code,
from a build script, or from the shell with `tools/effigy.sh`. See
[EFFIGY-API.md](EFFIGY-API.md).

---

## Marionette

Open it from **Tools ▸ Marionette**, or double-click a `.riganim` file. It starts with the
Citizen's first-person arms loaded and the animation tutorial open.

### Getting started

1. Set **Source Model** in the **Scene** panel to any skinned model.
2. Click a bone in the viewport and drag to rotate it. Press **W** to move bones instead, **E** to
   rotate again, or **R** to scale, the same keys as Effigy.
3. Press **K** to key the bone at the playhead.
4. Move the playhead, pose again, and press play.

The status bar explains whatever you hover over.

### What it does

- **Posing:** drag rotates by default. W, E and R switch to Move, Rotate and Scale, and the
  toolbar cycles them too. The skeleton draws over the mesh so buried bones stay clickable.
  Right-click a bone to hide it and its children. The **Inspector** panel takes typed values.
- **Timeline:** one lane per bone. The keyframe shapes show the easing (Smooth, Linear,
  Stepped, Ease In, Ease Out). Copy, cut and paste keys, even between clips. Turn on **Auto-Key**
  to key as you drag.
- **IK and limits:** two-bone IK and joint angle limits, added in the **Constraints** panel.
  They bake into keyframes, so a clip plays back identically anywhere.
- **Props and objects:** drop in a model to pose against. With **Collide** on, a dragged bone
  stops at its surface. A prop, or an OBJ imported with one part per group, can be keyed like a
  bone, so a door, lever or weapon moves on the same timeline as the hand that works it. A rigged
  prop gets its own bone handles.
- **First Person View** frames the model from its own camera bone.
- **Lights and cameras** in their own panels, to judge a pose by its shadows or keep a shot's
  framing. Tick **Export With Clip** to have them spawn when the clip plays.
- **Events:** spawn a model on a bone for a range of frames, or fire a morph event.
- **Undo and redo**: one drag is one step.

### Files

| Type | Extension | Holds |
|---|---|---|
| Rig Animation | `.riganim` | Keyframes, events, props, objects, lights, cameras, frame rate. |
| Control Rig | `.ctrlrig` | A model and its IK and limit constraints. Several clips can share one. |

### Getting animation into your game

**Play the clip with a component.** Add **RigAnimPlayerComponent** to your object, set
**Anim** to the `.riganim` and **Target** to the model's renderer. It plays on start, loops by
default, and can blend as an overlay or additive layer with a bone mask. Its **Parts** list
connects the clip's objects to GameObjects in your scene. There's no recompiling, which suits
iterating, but your project keeps needing Geppetto installed.

**Or bake it into the model.** In Effigy, **File ▸ Animation Clips… ▸ Add Clip**, pick the
`.riganim`, name it, set **Looping**, and **Compile .vmdl** again. The model then carries the
animation like any other, under the name you gave it.

**Or export it on its own.** Marionette's **File ▸ Export Animation…** writes an
animation-only `.vmdl` for the source model.

A clip only works on the model it was made for. Bones are matched by name; when you compile,
the console lists any bones in the clip that the model doesn't have.

---

## Known issues

- **Effigy forgets the Animation Clips list when you close it.** Add the clips again in the same
  session you compile in. Your `.riganim` files are not affected.
- **Live cloth has no self-collision.** It collides with the wearer's body only, so a fold can
  pass through itself, and a jacket passes through a shirt under it.
- **Marionette's tutorial asks for a lightswitch model that no longer ships** with the package.
  Use any model as a stand-in for that step.
- Cameras exported with a clip are also written to a `.vdmx`, and that file hasn't been
  confirmed to load in the engine yet.

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the layout,
how to run the tests, and how the kernel is kept in sync. [CHANGELOG.md](CHANGELOG.md) lists
what changed in each release.

## License

MIT. See [LICENSE](LICENSE).
