# Changelog

What shipped in each Geppetto package revision on sbox.game.

Add lines to **Unreleased** as you go, under one of the five headings below.
They are not arbitrary: they are the boxes the changelist form on sbox.game
asks for, so `tools/changelist.sh` can hand you the text for each box instead
of guessing which one a line belongs in. Use the same five in every release.

- **Added** — something you can now do that you could not before.
- **Improved** — something that already worked and now works better.
- **Fixed** — something that was broken.
- **Removed** — something that is gone.
- **Known Issues** — something broken that is not fixed yet.

Write for whoever installed the package, not for whoever works on it. Trailing
file references in backticks are fine — `changelist.sh` strips them on the way
out, so they stay useful here and never reach the store page.

KEEP THE STORE-PAGE BULLETS SHORT. An entry here can be a paragraph — the file
has room to explain itself, and should. The form does not: every line you paste
becomes its own bullet, and a five-sentence bullet reads as a wall of text on a
package page. Split each entry into one-idea lines on the way out, and leave
behind anything only somebody working on the repo would feel.

When you publish a revision, rename **Unreleased** to its version, then run
`tools/changelist.sh <version>` and paste each block into its box on the site.
`tools/changelist.sh` with no argument prints Unreleased.

NOT EVERY REVISION EARNS A CHANGELIST. A publish that moved only tests, build
scripts or repo layout changed nothing an installed user can feel, and a
changelist saying so is noise on the package page. Those revisions are listed
below the sections, named, so it is clear they were considered rather than
forgotten.

## Unreleased

### Added
- **Light the Marionette viewport.** A pose reads by its shadows, and the viewport had one
  fixed sun over the shoulder — under which an arm in front of a chest is a flat shape and a
  hand turned over looks the same either way. There is now a **Lights** tab (View ▸ Lights):
  add directional, point, spot and ambient lights, each with colour, brightness, range, cone
  and shadows, saved on the clip so each shot keeps its own lighting. **Place At Camera** puts
  the selected light where you are looking from, aimed where you are looking. A clip with no
  lights still uses the built-in sun and ambient exactly as before, and Add ▸ Copy Default
  Lighting drops those in as editable entries to start from.
  `Code/RigControl/RigLight.cs`, `Editor/RigControlEditor/RigLightsPanel.cs`
- **A light can travel with the clip, if you want it to.** Lights are workspace by default —
  the window only, never in a game. Tick **Export With Clip** and `RigAnimPlayerComponent`
  spawns that light beside the model when the clip plays, which is how a lamp authored with
  the pose it lights gets to the game with it. An exported .vmdl still cannot carry a light —
  the format is bone channels — and the export dialog now says so instead of dropping them
  quietly. `Code/RigControl/RigAnimPlayerComponent.cs`
- **Capture a pose the animation graph is already holding, straight into a clip.** The engine
  knows how to sit, aim and carry; Marionette only ever knew how to pose bones by hand, so a
  starting pose that already existed had to be rebuilt a bone at a time. `rig_capture_pose
  <model> <output.riganim> [params] [props] [frame]` in the editor console runs the model's own
  graph in a scratch scene, waits out the blend, and writes the pose it lands in as keyframes.
  It captures into an existing clip rather than replacing it, so several poses can be laid down
  at different frames, and it takes a list of reference props with offsets so the clip opens
  with the furniture already placed. `Editor/RigControlEditor/RigPoseCapture.cs`
- **Save a shot with a camera.** The framing you find by flying the viewport around is the
  framing you want the final shot to have, and until now there was no way to get back to it.
  There is now a **Cameras** tab (View ▸ Cameras): drop a camera where you are looking from,
  with field of view and clip planes, saved on the clip. Like lights, cameras stay in the
  window unless you tick **Export With Clip**. When you do, `RigAnimPlayerComponent` spawns them
  next to the model when the clip plays (switched off, because the game decides which shot is
  live), and the export writes them to a `.vdmx` shot file beside the animation.
  `Code/RigControl/RigCamera.cs`, `Editor/RigControlEditor/RigCamerasPanel.cs`
- **A posed bone stops at props instead of going through them.** Drag a hand onto a desk
  and it rests on the surface. The **Collide** checkbox in the viewport strip turns it off for
  poses that need to reach inside something, and it remembers your choice.
  `Editor/RigControlEditor/RigViewport.cs`
- **Select All and Delete All Keyframes** in the timeline's menu, for starting a clip over
  without deleting its tracks. `Editor/RigControlEditor/RigTimeline.cs`
- **Save your panel layout in Effigy.** View ▸ Set Default Panel View keeps the arrangement
  you like, and Reset Default Panel View puts it back after a stray drag.
  `Editor/EffigyEditor/EffigyWindow.cs`

### Improved
- **Dense imports load and simplify much faster.** The OBJ reader no longer spends its time
  parsing numbers, and the decimator does far less work per step, so a million-vertex mesh
  gets through import in a fraction of the time. `Editor/Effigy/ObjWriter.cs`,
  `Editor/Effigy/Decimate.cs`
- **Sculpting and moving a dense model is smooth.** A sculpt stroke updates the mesh in place
  instead of rebuilding it every dab, and dragging a part with Transform moves it on the GPU
  instead of recomputing the model every frame. `Editor/EffigyEditor/EffigyPreview.cs`,
  `Editor/EffigyEditor/EffigyViewport.Sculpting.cs`, `Editor/Effigy/Brush.cs`
- **The README shows how to put your playermodel on a player in your own scene**, not just the
  test scene Make Player writes. `README.md`
- **The house tutorial is written for your first time using CAD.** It explains what CAD is,
  shows you how to turn the camera, then tells you exactly what to click and type at every
  step, and what you just made. `Editor/EffigyEditor/EffigyTutorial.cs`,
  `Editor/EffigyEditor/EffigyTutorialPanel.cs`

### Fixed
- **The house tutorial no longer skips the door.** The door step ticked itself off as soon
  as the second window was drilled. It now waits for a third opening.
  `Editor/EffigyEditor/EffigyTutorial.cs`

## v379386 — 2026-09-10

### Improved
- **Dense imports are workable now.** Hovering, picking, dragging and sculpting a several-hundred-
  thousand-face mesh used to stall the viewport for a fraction of a second at a time, and got worse
  the denser the model. Every one of those is now under a millisecond at a million faces, and does
  not grow meaningfully with face count.
- **Moving the cursor over an imported part no longer freezes it.** Working out which flat surface
  a face belongs to did three full passes over the whole mesh before it looked at anything local,
  and it did them again for every face the cursor moved onto — so the tool locked up while the
  mouse was moving and recovered when it stopped. That groundwork is computed once per part now.
  On a 240k-face body a newly hovered face went from 237ms to under a tenth of a millisecond.
  `Effigy/Sketch/SurfaceIndex.cs`
- **Sculpting a dense mesh keeps up with the brush.** Each dab rebuilt the whole mesh's vertex
  adjacency and refitted every box in the pick tree, whether or not the brush went near them. A dab
  on a million-face body went from 148ms to 0.3ms. `Effigy/Brush.cs`, `Effigy/MeshBVH.cs`
- **Parts load and rebuild faster.** The pick tree builds about five times quicker, smooth-normal
  generation about four times, and the routines that derive mesh adjacency between four and ten
  times — with a fraction of the memory churn, so the garbage collector stops interrupting.
  `Effigy/MeshBVH.cs`, `Effigy/MeshNormals.cs`, `Effigy/PolyMesh.cs`
- **Selecting a big part no longer costs a frame.** The translate handle re-measured every vertex of
  everything selected on every frame it was drawn. It is measured once per part now.
  `Editor/EffigyEditor/EffigyViewport.BodyDrag.cs`
- **Dragging a part updates it in place.** Moving a body with the Transform handle, or typing
  into its fields, now rewrites the model already on screen instead of rebuilding it every
  frame, so dragging a dense part keeps up. `Editor/EffigyEditor/EffigyPreview.cs`

### Fixed
- **The package was shipping 434MB of somebody else's modelling scratch.** Every revision carried
  the test models, imported meshes and half-finished part studios that happened to be sitting in
  the project folder — gun models among them — because an s&box publish ships what is in the
  project directory and no setting or ignore file reaches it. This revision is 10MB. If you have
  Geppetto installed, updating reclaims most of that.
- **Publishing now says what is in the box.** The publish step lists the manifest by folder,
  biggest first, and refuses outright if anything under `Assets/` is not the package's own
  content. It was previously possible to see only a file count, which is how 459MB went out
  several times without anyone noticing. `GeppettoPublish`

- **Deleting parts of an imported mesh makes it faster.** Every delete, undo and redo on an import
  re-read the whole OBJ, parts you had already deleted included — so taking a 900k-face Meshy
  import down to 20k faces still cost 1.3 seconds per delete, and an undo cost the same. The file is
  only read again when it changes on disk now, so a delete costs what is left: the last deletes of
  that trim take a few milliseconds, and undo takes 2ms. `Effigy/Features/ImportFeature.cs`
- **Painting no longer draws the selection over the part.** A part picked in the Parts list stayed
  lit in amber wireframe while you painted it, hiding the colour you were laying down. Paint,
  sculpt, material and weight brushes now show the bare surface; the selection comes back when you
  finish. `Editor/EffigyEditor/EffigyViewport.Selection.cs`
- **Rigging an imported mesh made every compile fail.** Import an OBJ that carries vertex colour —
  which a Meshy export, a scan or anything part-segmented does — add a bone, and the .vmdl compiled
  to nothing but the orange error model. The DMX we wrote stored colour as fractions where the
  format wants four bytes 0-255, so the engine rejected the whole file, reporting only
  "Couldn't load DMX file". Unpainted parts were unaffected, which is why it went unnoticed.
  `Effigy/DmxWriter.cs`
- **A model compiled from an OBJ could arrive lying on its side.** ModelDoc's OBJ importer
  turns the mesh as it reads it, and the correction for that only existed in one of the four
  places a .vmdl was written — so a model built anywhere else compiled with its axes swapped,
  and its collision sat at an angle to it. The .vmdl is built in one place now, and the
  correction is worked out from the mesh file rather than copied. `Effigy/VmdlDocument.cs`
- **A rigged model's collision stayed where the model used to be.** Pose a bone and the mesh swung
  away from a collision box lying flat in the bind pose — solid where the part was, air where it
  is. Rigged exports carried one static shape welded to the model root, because the shapes had no
  bone to hang off. They do now: a rigged part exports one convex hull per body, each parented to
  the bone that drives it, taken from the same Parts-list assignment its skin weights come from.
  Bodies you never assigned go to the bone nearest their centre, which is the bone they were being
  weighted to anyway. `VmdlPhysics`, `CollisionBuilder`
- **Collision on a rigged model needs Model Physics in the scene, not Model Collider.** A Model
  Collider builds one static shape and never looks at the skeleton again, so it cannot follow a
  pose no matter what the model carries. Add a **Model Physics** component beside the Skinned
  Model Renderer and turn **Motion Enabled** off — that drives the physics from the animation, and
  the per-bone hulls above are what it drives.
- **Deleting a group of selected parts removed only the one you right-clicked.** Select several
  imported parts in the Parts list, right-click one and choose Delete, and only that one went —
  the rest of the selection was ignored. Delete now removes every selected part in one undo step,
  and removing every remaining piece of an import removes the import itself rather than leaving an
  empty row. `Editor/EffigyEditor/EffigyWindow.cs`

### Added
- **Make a playermodel out of anything you have modelled.** A character you can walk around in,
  driven by the animations s&box already ships — no animating, no rigging to somebody else's
  proportions. **File → Make Player** compiles the model and writes a test scene beside it with a
  floor, a light and a player wearing it: open the scene, press Play, walk around. **File → Compile
  Playermodel** does just the model, for dropping into a scene of your own.
- **Your model keeps its own shape.** Effigy slides the built-in character's skeleton *inside* your
  model rather than reshaping your model to match its body, so the animations only say how far each
  joint bends — never how long your arms are. A stocky robot stays stocky and still walks. The
  earlier approach stretched every model onto the same silhouette.
- **Help → Start Playermodel Tutorial** walks the whole thing through in the editor, in plain
  English, with an example robot to practise on — or start it on a model of your own. Nine steps:
  stand it right, name the bones, hang them off the right parents, wiggle it, walk in it.
- **The example robot comes pre-named.** Every part is already called what its bone should be
  called, so the lesson is about which bone hangs off which rather than about typing names.
- **The bone names are written down**, in the README and in the tutorial: `pelvis` up the spine to
  `head`, `clavicle` out to `hand`, `thigh` down to `foot`. If your rig already uses the built-in
  character's own names instead, those work too — nothing to rename.
- **The console names any bone the animations did not recognise.** Almost always a typo, and the
  only place you would ever be told: an unrecognised bone is not an error, it just holds still.
- Hips need to sit around 31 units off the floor with the feet at zero — that one measurement comes
  from the walk, and a model far from it will float or sink. Everything else about the proportions
  is yours. `HumanoidSample`, `EffigyPlayermodelExport`, `EffigyPlayerScene`

- **Transform gives you move, rotate and scale handles in the viewport.** Press Transform and a
  set of arrows appears on the parts it will move. Drag them and the parts move, so you no longer
  type three numbers into Translate to find out where they land.
- **W, E and R switch between the arrows, rotate rings and a scale handle** while a Transform is
  open. These are the same keys that switch a bone's handle.
- Rotate and scale work around the handle, not the world origin. A part far from the middle turns
  in place instead of swinging across the scene.
- Dragging a handle fills in the Transform's own Translate, Rotation and Scale fields as you go,
  like typing them. Undo works as usual, and nothing is added to the tree.
- Pick no bodies and the handle sits on the whole model, because a Transform with nothing picked
  moves the whole model.

- **Subdivide is on the Rig bar now, under Mesh.** A mesh too coarse to bend is a rigging problem
  and it is found while rigging — you drag the arm and the elbow creases into a hinge. The fix was
  only reachable through the Sculpt workspace, which reads as "you are about to sculpt" when you
  are not. Select a part, press Subdivide, and it adds loops. It arrives set to All Faces, which
  adds density and leaves every vertex exactly where it is, rather than the whole-body smoothing
  the Sculpt bar defaults to — you have already bound bones to that silhouette and do not want it
  moving.   Same feature, same tree row, same undo; pick faces instead of a part to densify just the
  joint.
- **Remesh reduces a dense import to a triangle budget.** An imported part — a Meshy generation, a
  scan, a sculpt somebody else exported — arrives at hundreds of thousands of triangles, and until
  now nothing could take any away: you could not subdivide it, sculpt it, weight-paint it or compile
  it. Remesh is Subdivide's opposite, on the Sculpt bar and on the Rig bar's Mesh stage. Give it a
  part and a budget as a percentage or a triangle count, and it keeps the silhouette, open borders
  and material seams while it takes the count down. It returns triangles, so it is for imports
  rather than for a quad cage you built with sketches. `Decimate`, `RemeshFeature`
- **Marionette animates whole parts, not just bones.** A door, a lever, a magazine, a light
  switch's toggle — things that move as one object rather than as a joint in a skeleton. Drag a
  reference prop in the viewport and it takes a lane on the timeline like any bone: same
  keyframes, same easing modes, same dragging, marquee select, copy/paste, undo. Press `K` with a
  prop selected to hold it in place across a span. Part lanes are marked green in the gutter so
  they are not mistaken for a bone gone missing from the skeleton.
- **Parts play back in game.** `RigAnimPlayerComponent` has a **Parts** list — part name on the
  left, the GameObject it drives on the right — so the hand and the thing it is opening run off
  one clip and one clock. Leave it empty for a bones-only clip; an unwired part is simply not
  driven, so a clip still plays in a scene that only hooked up some of them. A .vmdl export is
  bone channels only and says so plainly if you try to export a parts-only clip.
  `RigAnimDocument`, `RigAnimPlayerComponent`, `RigViewport`
- **A clip can animate several objects at once, each with its own skeleton.** A reference prop that
  is a rigged model now draws bone handles like the main model does, so the hand, the weapon in it
  and that weapon's bolt are all posed in one clip on one playhead — rather than three clips that
  have to be kept in step by hand. Handle sizes come from each object's own bounds, so a magazine
  does not get the dots of the character holding it.
- **Tracks are named `object/bone`,** so two objects are each allowed a bone called `root`. The main
  model's bones keep their bare names, which is what every clip you have already made contains —
  those read back exactly as before. `RigTrackName`
- **A clip holds objects, and an object has parts.** A door, a weapon, a fridge — the things an
  animation is about besides the character. Move the object and everything in it goes with it; move
  a part and it moves within the object. Both record to the timeline exactly as a bone does: drag
  one and there is a key at the playhead.
- **An Objects tree, under the bone tree.** Each object is one row with its parts folded up
  underneath it, so a forty-part import does not bury the arm you are posing. Clicking a row
  selects that thing in the viewport, and picking it in the viewport marks its row and its timeline
  lane — one selection, wherever you touch it. `RigObjectsPanel`
- **A part can follow another part.** The eyes follow the head: move the head and they come along,
  and they can still be moved on their own. Right-click a part → Follow → Pick in Viewport, then
  click the head. The tree nests followers under what they follow. Nothing jumps when you set it,
  and it plays the same in game. `RigObjectPart.ParentPart`
- **Marionette imports OBJ files, split into their parts.** File → Import OBJ. One object, with one
  part per `o`/`g` group in the file, so a door exported with its handle and hinge kept separate
  arrives as one door you place and three pieces you animate — rather than one welded lump that can
  only move as one. Nothing has to be compiled into a `.vmdl` first, which was previously the price
  of posing against a mesh at all.
- **An imported mesh is copied beside the clip,** into a `.meshes` folder named after it, so the
  clip still works when the original file is moved or the project is copied to another machine.
  Import before saving the clip and it points at the file where it is, and says so.
  `RigObjMeshes`, `ReferenceProp.ObjSource`

## v379213 — 2026-09-09

### Fixed
- **Hovering a dense imported mesh dragged the viewport to a crawl.** The face, edge, body and
  bone pickers each re-scanned every triangle in the part on every frame the cursor was over the
  canvas — on a 60k-face import out of Meshy that was about 13ms and 25MB of garbage per frame,
  so the whole frame budget went on deciding what the cursor was on. Parts past a few thousand
  faces now get a pick tree, built once per rebuild: the same pick costs microseconds and
  allocates nothing. Small parts are untouched — they were never the problem, and a tree would
  have cost more to build than the scan it replaced. `MeshRaycast`, `MeshBVH`
- **Assign Body stayed grey after you picked a part.** Clicking the mesh counted as empty space
  and dropped the bone selection. A part click now keeps the bone. Select a bone, select a part,
  press Assign — that pins them. With no part selected it still arms click-to-assign in the
  viewport.
- **Clicking a bone in the viewport now selects it.** Bones sit inside the mesh, so a click used
  to hit the part instead. Assign, Paint Weights and Parent all need that selection, and they
  stayed grey. Clicking the visible bone selects it even through the solid.

### Improved
- **Bone from Part is on the Rig bar**, not only the part right-click menu. Select a part and
  press it. In the Rig workspace, right-clicking the solid itself opens the same part menu
  (Make a bone from this part, Assign to the selected bone).

### Added
- **Import splits a file into its parts.** An OBJ that kept its objects separate — brows, lids,
  hair, a visor — now arrives as one part per object instead of one welded lump, named after
  whatever the exporter called it. That is the difference between a feature tree you can hide,
  re-material and weight a piece at a time and one solid you cannot take apart. A file with a
  single object, or none marked at all, reads exactly as before: one part, named after the
  feature. `ObjReader.ReadPieces`, `ImportFeature`
- **Citizen size reference is a button on the tool row.** Right-hand end, labelled Citizen.
  Same switch as Edit → Settings → Reference — either one turns the stand-in on or off.
- **Import a mesh as a body.** Sketch stage → Import, pick a Wavefront OBJ. The triangles stay
  beside the document (`model.import/`), not in the `.effigy` text — same answer sculpt already
  had for megabytes of per-vertex data. Paint, weight paint and auto-skin then work on it like
  any other part. FBX and GLB are refused with a remedy, not parsed. `ImportFeature`,
  `ImportSidecar`, `ObjReader`
- **Parent bones to each other.** Right-click a bone in the Rig tree → Parent to. Hang trigger
  and mag off root and they follow it in Marionette. Same pose, new parent. A bone cannot parent
  to something that already hangs off it.
- **A first rigging tutorial.** Help → Start Rigging Tutorial. Two boxes (a post and a sign),
  a bone from each, pose the sign, compile. The smallest loop that still needs a skeleton.
- **A hand tutorial.** Help → Start Hand Tutorial. Palm, a three-joint index, the other digits,
  sculpt, paint, then bones parented so a finger curls. The four workspaces on one model.
- **Named variables.** View → Variables, then type `#thickness` in any dimension. Change it once
  and every feature that refers to it moves. Cycles are refused rather than looping.
- **Section view.** View → Section View clips the preview through the origin along +X so a
  shelled interior is visible. Nothing is cut in the history.
- **Weight painting.** Rig workspace → Weights → Paint Weights. Pick a bone in the Rig tree and
  drag. The heat map is a texture atlas, not vertex colour — paint already taught that lesson.
  Skin weights stay per-vertex for the compiler. `WeightRamp`, `WeightPaintSession`
- **Paint Falloff and an Erase button** on the paint bar. Ctrl still inverts for one stroke.
- **Revolve can spin about a line you drew in the sketch.** Set Axis to "A line of the sketch"
  and pick the line from the new Axis line box — construction lines included, which is what the
  dashed centreline of a lathe profile is. `RevolveFeature.AxisLineId`
- The axis follows that line when you move it, so dragging the centreline moves the bore with it.
- **Bones from the shape of your parts.** Right-click a part and pick **Make a bone from this
  part**: it measures the part and adds a bone down its longest axis, already pinned to it.
  `BoneFromBody`, `EffigyWindow.MakeBonesFromBodies`
- The same from the feature tree — right-click the feature that built the parts and get one bone
  per part it made, so a patterned row of eight fingers is one click instead of eight.
- The bone takes the part's name, and a part is named after the feature that made it. Call the
  extrude `index_finger` and that is what the bone is called.
- It is rolled to match the part, so the flat of the bone runs with the flat of the part and there
  is no arbitrary twist to undo on every one.
- Select a bone first and the new ones hang off it, pointing away from it — which is also what
  decides which end of the part is the root, so place the palm bone before the fingers.
- A part with no long axis, like a sphere or a cube, is skipped and named in the console rather
  than given a bone pointing nowhere in particular.
- **Pose the rig and watch it bend.** A Pose button in the Rig panel — press it, drag a bone, and
  the mesh deforms with it, so a bad weight shows up as a crease instead of a number in a panel.
  Reset Pose (or toggling Pose off) returns to the bind pose; nothing is saved. Built on the same
  weights the export uses, so what you see is what compiles. `SkinBinder.Deform`,
  `EffigyViewport.PosePreview.cs`
- **Assign bones from the Parts list.** Right-click a part and pick **Assign to bone** — the same
  pinning the rig panel does, reachable from the body's side. An assigned part shows its bone in
  blue on the row. `EffigyPartsPanel`, `EffigyWindow.OnBodyBoneAssigned`
- **An eraser.** Hold Ctrl while you paint and the brush takes paint off instead of putting
  it on, back to the surface underneath. `PaintStroke.cs`, `PaintReplay.cs`
- The brush ring turns red while Ctrl is held, so you can see which one you are about to
  do before you press rather than after.
- Erasing uses the brush you already have -- the same size, strength and falloff -- so a
  soft eraser fades out at its edge and a Constant one takes a hard bite.
- An erase is a stroke like any other: one Ctrl+Z, saved into the .effigy, and still there
  after you edit a feature underneath it and the part rebuilds.
- Ctrl is read once, when you press. Letting go halfway through does not turn the back half
  of the mark into paint.
- **Drag a box to select in a sketch.** Press on empty space in the sketcher and drag: left to
  right takes only what is completely inside the box, right to left takes anything the box
  touches, the same two boxes Onshape has. `EffigyViewport.Constraints.cs`
- The box adds to what is already selected, so you can build a selection up out of several
  boxes. Clicking empty space still clears it.
- **A mirror tool.** Select the geometry, arm Mirror, and click the line to reflect it across.
  `SketchEdit.Mirror`, `EffigyViewport.SketchMirror.cs`
- The copy is held symmetric to the original rather than just pasted, so dragging one half
  moves the other.
- A point already sitting on the mirror line is shared rather than doubled, so a half profile
  drawn against the line closes into one region when it is mirrored.
- The selection stays lit while a tool that uses it is armed, so Mirror and Offset are no
  longer aimed at something invisible.

## v368962 — 2026-09-09

### Added
- **Bones can be placed inside a model, not just on its skin.** The bone tool has a Middle/Surface
  choice, and Middle is the default: a click measures how much material is under the cursor and
  puts the joint halfway through it. `EffigyViewport.Rig.cs`
- So a spine runs down the middle of a chest instead of down the front of it, which is where a
  joint has to be for the weights around it to make sense.
- While placing, the run of material being measured is drawn with its thickness, so you can see
  where the joint will land before you click rather than finding out afterwards.
- A **Y=0** toggle beside it forces every placed joint onto the mirror plane exactly. Mirror
  reflects across that plane, so a chain placed with this on mirrors cleanly.
- Both settings are remembered between sessions.
- **Planes you place yourself.** A Plane button beside Sketch, so "which plane?" is no longer
  answered only by Top, Front, Right or a face that already exists. `PlaneFeature.cs`
- Build one off a global plane, off a face of a part, or off another plane, then push it along
  its normal with Offset and lean it over with Angle.
- **Drag a plane instead of typing its offset.** While a plane's dialog is open an arrow stands
  on it: pull the arrow and the plane slides, with everything drawn on it following as it goes.
  `EffigyViewport.Planes.cs`
- The arrow points the way the plane MOVES rather than the way it faces, so it still follows the
  cursor on a plane you have leaned over. The Offset field counts along under the drag.
- A plane built from a face rides that face: make the part taller and everything drawn on the
  plane moves up with it, instead of being left where the part used to end.
- Planes stack, so three ribs ten apart can each be "ten further on" rather than three heights
  to keep in step -- change the bottom one and the rest follow.
- Sketch on one the same way you sketch on anything else: press Sketch, click the plane. A boss
  built through a plane that came off a part joins that part rather than starting a new one.
- Planes are drawn in the viewport with their names beside them, and each has an eye in the
  feature tree for when a document has more of them than you want to look at.
- While a plane's dialog is open its own X and Y axes are drawn on it, so the Tilt about
  dropdown is something you read off the model rather than find by trying both.
- A **material brush**. Press Material in the Paint workspace and drag on the model to
  lay a material onto faces, instead of picking them one at a time. The material is
  whatever is selected in the Materials browser, which the Paint workspace already opens
  -- click one there, brush it on, click another and keep going without leaving the model.
  `MaterialBrushSession.cs`, `EffigyViewport.MaterialBrush.cs`
- The brush outlines the faces it is about to take, because a material belongs to a whole
  face: on a coarse box a small ring still paints an entire side. Subdivide first if you
  want the edge to follow the brush.
- It makes the same edit dropping a material makes, so one Ctrl+Z is one dab, the slot is
  reused rather than multiplied, and a slot the brush swept the last face off is retired
  instead of being left named on nothing.
- Paint is a texture atlas now, not per-vertex colour. A stroke is stamped into a 1024x1024
  canvas whose resolution has nothing to do with how many vertices the part has, so a bare
  box paints at brush resolution without a Subdivide. `PaintCanvas.cs`, `PaintReplay.cs`,
  `PaintSession.cs`, `PaintFeature.cs`
- A painted part exports its own material. On Compile .vmdl the canvas is written to a PNG and
  wrapped in a .vmat, then bound to the part's material slot -- so the compiled model samples
  the paint like any other texture, and the vertex-colour workaround (`vertex_color.vmat`) is
  gone. `PaintMaterial.cs`
- Entering Paint on a part whose UVs will not hold paint inserts a **UV Project** in
  **Unwrap** mode above it automatically, then carries on. It is an ordinary feature in the
  tree, so it rolls back and undoes with one Ctrl+Z, and the prompt says when it was added.
  `EffigyWindow.cs`
- The **Blend** choice on the paint bar stays, carried for documents saved before the
  switch. With a texture the paint covers, so the tint/replace distinction that choice used
  to make is no longer drawn. A face you dropped a material on still keeps that material.
  `PaintFeature.cs`, `EffigyPaintBar.cs`
- A **Falloff** dropdown on the sculpt bar, beside Radius and Strength. Falloff is how the
  brush fades from its centre to its edge: **Smooth** for a soft mound, **Sharp** for a hard
  crease, **Linear** for an even fade, **Constant** to move the whole disc at once. The choice
  was always in the kernel and nowhere reachable -- Sharp versus Smooth is the difference
  between a crease and a mound. `EffigySculptBar.cs`
- Hold **Ctrl** while sculpting to invert the brush. Draw carves in instead of pushing out,
  Inflate deflates, and Grab drags the opposite way. The brush ring turns red with a minus in
  the middle while inverted, so you can see which way the stroke will go before you click --
  and the Strength box never changes on its own. `SculptSession.cs`,
  `EffigyViewport.Sculpting.cs`
- Sculpting hotkeys. **1–6** arm the six brushes in the order they appear on the bar, **X**
  mirrors and **M** masks as before, and **[** and **]** shrink and grow the brush -- hold
  either to keep resizing. The toolbar's ticks and the bar's numbers follow the keys.
  `EffigyViewport.Sculpting.cs`
- Rebind any Effigy tool key. Settings has a **Hotkeys** section: every key the tool registers
  -- the sketch tools, the sculpt brushes and their mirror/mask/radius keys, paint symmetry,
  the note eraser and hide, the bone drag modes, the rig keys, undo/redo/save -- is listed with
  its current key. Click one, press the new key, and it takes effect immediately; Reset puts one
  back to its default. The keys live in the engine's own shortcut store, so a change here also
  shows up in the editor's Editor Keybinds page, and the other way round. `EffigySettingsWindow.cs`
- See the UVs. A **UV checker** view toggle draws the model with a checker pattern instead of its
  own materials, so stretching and seams in the UVs show on the surface instead of waiting for a
  paint or a bake to smudge them. It is in Settings, under View. `EffigyWindow.cs`,
  `EffigySettingsWindow.cs`
- An unwrap says what it did. The UV Project feature's panel reports the result of an **Unwrap**
  -- how many charts, how many faces, and the texel density -- instead of discarding it, and warns
  when the result still will not hold a bake. Box and planar projection stay silent, because their
  overlap is what makes them tile. `SolidFeatures.cs`, `EffigyFeatureDialog.cs`

### Improved
- The tool row scrolls left and right when the buttons no longer fit. Mouse wheel pans it
  (Shift for a bigger jump); arrows appear at the ends when there is more to see. The
  tutorial still pans a highlighted button into view.
- The settings window folds up. Each section -- Grid, Snapping, Reference, Lighting,
  Appearance, Normal map bake -- is now a header you click to collapse or expand, and the
  window remembers which ones you had open next time. `EffigySettingsWindow.cs`
- Unpainted paint texels bake over a grey base rather than white, so one dab no longer turns
  the compiled model into a white brick. Preview and export use the same base.
- The material brush works on a studio with more than one body. It raycasts each and paints
  the nearest.

### Fixed
- Compile no longer binds the paint atlas to slot 0 blindly. It binds the slots the painted
  faces actually wear, and isolates a second painted body onto its own slot.
- Paint on two bodies at once is an error with a remedy, not a silent no-op.
- The dead Blend combo is off the paint bar. The field stays on the feature so old documents
  still load; it does not change the fallback any more.
- A plane's dialog no longer puts the face pull arrow on the model. A plane can be built from a
  face, so the arrow appeared -- and dragging it did nothing, because a plane has no distance for
  it to write. The plane's own offset arrow is the handle there now.
- Switching workspaces now puts away what you were doing. The note pen (grease pencil) used
- A plane's dialog no longer puts the face pull arrow on the model. A plane can be built from a
  face, so the arrow appeared -- and dragging it did nothing, because a plane has no distance for
  it to write. The plane's own offset arrow is the handle there now.
- Switching workspaces now puts away what you were doing. The note pen (grease pencil) used
  to stay armed across the switch, so the next thing you drew also scribbled notes; and a
  running soft-bone preview kept the bones sagging and swinging in the workspace you switched
  to. Leaving a workspace now disarms the pen and stops the preview, the same way it already
  finished a sketch, sculpt or paint. `EffigyWindow.Workspaces.cs`
- Paint is visible. It never was: the colours were written into the mesh's vertex COLOR
  stream, and the material everything rendered with does not read that stream at all --
  `complex.shader`'s model tint is a per-draw constant and its tint mask is a texture, so
  the paint was packed into the vertex buffer correctly and thrown away by the shader. A
  painted part looked exactly like an unpainted one, in the viewport and in the compiled
  model. The paint is a texture on an ordinary material now, so the shader that ignored
  vertex colour is out of the picture entirely.
  `PaintCanvas.cs`, `EffigyPreview.cs`, `EffigyViewport.Painting.cs`
- A material dropped on a face still wins on a painted part -- paint takes the slots that
  had nothing, not the ones you chose.
- Paint is no longer limited by the mesh. Paint was vertex colour, and a bare box has eight
  vertices, all at the corners -- a stroke landed on corners and spread across whole faces,
  nowhere near the cursor, and the only fix was to Subdivide first. It is a texture atlas
  now, so the brush resolves paint as finely as the canvas whatever the mesh. `PaintReplay.cs`
- A held brush no longer keeps darkening. Each dab composited straight onto the result, so
  holding the button still ticked the colour up like a sculpt brush; a stroke is now
  composited once from its own coverage, so the same spot re-stamped over and over stays
  the same mark. `PaintReplay.cs`, `PaintSession.cs`
- Dragging a multi-bone selection in Marionette no longer moves one bone twice as far as
  the rest. A group drag is applied to the selected bones that nothing above them is
  carrying -- but it only checked each bone's immediate parent, so selecting a bone and
  its *grandchild* while the bone between them stayed unselected let the grandchild
  through: it was transformed directly and carried by its grandparent at the same time.
  It reads as the gizmo misbehaving rather than as the selection being misread, which is
  why it survived. The rule now walks the whole chain. `BoneSelection.cs`
- Materials from the browser bind to something that exists. Most of the engine's own
  content ships compiled, and the asset browser names those `.vmat_c` -- so dropping one
  on a face, or using it for the whole part, wrote a reference nothing resolves and the
  face came back in the bright red missing-material shader. The source path is what goes
  in the document now. The browser's "bound" badge and the one-slot-per-material rule were
  wrong in the same way and by the same cause: a part wearing `oak.vmat` did not recognise
  `oak.vmat_c` as the material it already had, so it took a second slot for it.
  `MaterialDrop.cs`, `EffigyMaterialsPanel.cs`
- A compiled model arrives wearing a material instead of the bright red missing-material
  shader. Every material slot the mesh actually uses is now named in the model's remap
  list -- the ones you dropped a material on point at that material, and the rest point at
  `materials/default.vmat`. They used to point at nothing: the mesh calls an unbound slot
  `material_0`, no asset answers to that name, and red is what the engine shows for a
  material it cannot find. Since the geometry, UVs and skinning were all fine, the first
  thing anyone saw after their first export was a broken-looking model that was not broken.
  `VmdlMaterials.cs`
- A painted part compiles to its paint. With vertex colour the only material that read it
  was a shader nobody else used, and the whole thing needed a fallback to look right; now
  the paint is an ordinary texture on an ordinary material, so it survives the compile the
  way any texture does.
- A compiled static model samples its texture the right way up. OBJ's UV origin is the
  bottom-left and Effigy's is the top-left, so the exporter was writing V unflipped and a
  painted (or otherwise textured) static model came out upside down. V is flipped on the way
  out now, the same way the FBX writer already does. `ObjWriter.cs`

### Known Issues
- Animation clips have to be added again every time you open the tool. File → Animation
  Clips… builds the list that Compile .vmdl bakes in, and that list is not written to the
  .effigy -- close Effigy and it is empty next time, with nothing said about it. Saving it
  would mean a model file naming a .riganim that names a model, which is a loop the loader
  has to be taught to break, so it is a real decision rather than an oversight. Until it is
  made: add the clips in the same sitting you compile in. `EffigyWindow.cs`

## v367690 — 2026-09-05

### Added
- Soft bones are reachable. Select a bone, tick **Soft** in the Rig panel, and set its
  stiffness, damping, weight and cone. The solver behind them shipped a while ago and
  has been tested since, but nothing in the editor could ever put softness on a bone --
  the rig problems list would even warn about a soft bone with a zero cone that no
  amount of clicking could create. Soft bones draw blue in the viewport so you can see
  which ones are simulated. `EffigyRigPanel.cs`
- **Preview** on the Rig bar runs the solver live. Gravity pulls the soft bones off
  their pose so you can watch them sag and settle while you tune the numbers, and
  dragging a bone with the pose gizmo makes everything soft below it swing behind the
  drag. **Rest** puts them back when you want to judge a fresh value.
  `EffigyViewport.SoftPreview.cs`
- Rigs are saved in the .effigy file. Bones, their bind pose, their softness and which
  body is pinned to which bone all survive a save and reopen. They did not before --
  the skeleton lived on the rig panel, which no file format had ever heard of, so
  placing bones and reopening the part lost every one of them silently. A part with no
  rig is written exactly as it was before, byte for byte, and still opens in older
  builds. `StudioDocument.cs`, `PartStudio.cs`
- A workspace bar across the top: **CAD**, **Sculpt**, **Paint**, **Rig**. Effigy had
  grown four toolsets that all took turns on one tool bar, and the only thing that ever
  said which was showing was a word written small at the right-hand end. Now the part of
  the pipeline you are in is a control rather than something you infer: clicking Sculpt
  gets you sculpting, the way clicking Extrude gets you an extrude. Sketching still
  counts as CAD — you opened it from there and you finish it back there.
  `EffigyWorkspaceBar.cs`, `EffigyWindow.Workspaces.cs`
- Rigging has its own tool bar, so it works like every other part of the tool. Add Bone,
  Delete, Assign Body and Mirror sit on Bones and Bind stages instead of being buttons
  in a side panel — they were the only toolset that lived somewhere else. The Rig panel
  keeps its buttons; they run the same actions.
- Each workspace opens the panels it needs and puts the rest away. Paint brings up the
  material browser, Rig brings up the skeleton tree and gives it the whole right-hand
  side. Whatever you rearrange while you are in a workspace is what you come back to, so
  the layouts are a starting point rather than something that undoes your own arranging.
- A tool that cannot run yet is dimmed and says why when you hover it, instead of looking
  live and doing nothing. Assign Body, Mirror and Delete all need a bone selected.
  `EffigyStageBar.cs`
- The tool bar marks which tools will use what you have selected. Click a face and
  Fillet, Chamfer, Draft, Hole, Face Material, Extrude and Move Face each pick up a
  green mark down their left edge; click an edge and only the two blends do. It marks
  what applies rather than dimming what does not, because a tool that ignores your
  selection is not unavailable — you can still press Primitive with a face selected,
  and always could. `EffigyStageBar.cs`
- **Boolean**, on the Solid stage: union, subtract or intersect two bodies. The engine's boolean
  has been installed and working for a while -- Extrude's Remove and Hole both cut with it -- but
  it could only ever be reached by drawing a profile or drilling a hole. There was no way to point
  at two solids you already had and make them one, which is the first thing anybody tries in a
  modeller. Pick the tool body, pick the operation from the button's dropdown, and the tool is
  consumed by the cut the way a cutting tool should be; **Keep tool bodies** leaves it if you want
  to reuse it. `BooleanFeature.cs`
- A Boolean refuses rather than guesses. A body cannot be its own tool, an unpicked tool is an
  error instead of quietly meaning "every body", and a subtract that removes everything or a union
  of solids that never touch each say so rather than leaving you with a part that vanished.
- Painting. Press **Paint** and brush colour straight onto the model. The paint
  composes over whatever material the part already wears, so a part with a dropped
  material keeps that material everywhere you did not brush. Strokes are saved in the
  .effigy file and replayed whenever the model rebuilds, so paint follows the part
  through later edits instead of smearing when something upstream changes.
  `PaintFeature.cs`, `PaintSession.cs`, `PaintReplay.cs`
- Bones can be scaled in Marionette. The viewport's drag mode cycles Rotate, Move and
  Scale, and **E** still flips between the first two. The scale is one number rather
  than three, because that is all a Source 2 bone carries -- it shows in the Inspector
  and in the readout, is keyed like any other channel, and `RigAnimPlayerComponent`
  applies it at runtime. `RigViewport.cs`, `RigInspectorPanel.cs`
- Shift-click builds a selection of bones instead of replacing it. A group gets one
  gizmo at its centre, and dragging it moves every top-most bone in the group together
  while their children follow through the hierarchy the way they always do.
- A **Handle Size** slider on the rig bar. The bone dots and the areas you click to
  grab them both scale with it, so a dense hand rig can be shrunk until its fingers
  stop overlapping and a whole-body rig can be grown until it is easy to hit. The size
  is remembered between sessions.

### Improved
- The built-in Effigy tutorial builds a house rather than a lamp, and takes five steps
  to do it: box walls, a wedge roof, holes drilled for the windows and the door, then
  the export. The lamp asked you to sketch, revolve, shell, subdivide, unwrap and
  sculpt before you had made anything you could look at -- which is the whole tool,
  taught in the order the tool is written rather than the order somebody learning it
  can follow. Every step of the house is a shape you can see arrive.
  `EffigyTutorial.cs`, `EffigyTutorialPanel.cs`
- Only one thing can own a click in the viewport now. Sketching, sculpting, painting and
  the bone tool each used to shut down its own hand-kept list of the others on the way in,
  the lists disagreed, and nothing at all closed a paint before letting you place a bone —
  so both could be armed and one click tried to do two things. Every way in goes through
  one place. `EffigyWindow.Workspaces.cs`
- The pull handle is now a single arrow, pointing the way the face faces. It used to
  be three, and on one face two of them did nothing when dragged — correctly, since
  sliding a flat face within its own plane does not change the solid, but an arrow
  that does nothing reads as broken. Sliding a wall is Move Face's Translate mode.
  `EffigyViewport.FaceDrag.cs`

### Fixed
- Bones can be clicked in the viewport. They never could: every bone registered its
  click target into the same shared slot, so the hit test could tell you the cursor was
  over *a* bone but not over *which* one, and the click went nowhere. Each bone now has
  its own, the way the Marionette rig viewport has always done it. `EffigyViewport.cs`
- A bone's hit target is the bone you can see. The drawing sized itself off the bone's
  length while the target was a fixed radius, so the two agreed at exactly one bone
  length and drifted apart in both directions from there -- on a long bone most of what
  you could see did nothing, on a short one the target stuck out past the end. Both now
  come from the same number.
- The pose gizmo stops vanishing when you click the bone it belongs to. A selected bone
  had no hit target at all, so a click anywhere off the gizmo's arrows counted as
  clicking empty space and threw the selection away -- the gizmo was not failing to
  appear, it was being dismissed by the click aimed at it.
- Hovering a bone highlights the bone, rather than putting a blob at one end of it.
- Picking a bone in the Rig tree shows up in the viewport straight away -- it turns
  yellow and gets its pose gizmo. The selection had always worked; the viewport only
  repaints when asked and nobody was asking, so nothing on screen said so until you
  happened to move the mouse over the model.
- In the Rig workspace the model's faces are no longer selectable or highlighted, so a
  click meant for a bone cannot land on the wall of triangles behind it. The origin
  handle, the lamps and the face-drag arrow step aside there too.
- A rig edit marks the document unsaved. Placing bones, renaming one, or changing
  softness left the title bar showing no changes, so closing the window closed it
  cleanly without asking and the whole rig went with it. Harmless while a rig only
  lived in the window and there was nothing to save it into; a way to lose work as
  soon as rigs went into the file. `EffigyWindow.cs`
- Undo works on soft bones. Making a bone soft and pressing Ctrl+Z left it soft, and
  changing a stiffness twice in a row lost the first value -- the undo system compared
  two rigs without looking at their softness, so every soft edit looked to it like
  nothing had happened. `EffigyWindow.cs`
- An oversized fillet or chamfer is refused again, across the whole range where it
  should be. On a 2-unit cube any radius above 1.0 has eaten more of every face than
  the face had; between 1.0 and about 1.25 the part came back quietly self-intersecting
  instead of saying so, because the old check measured the volume of the finished body
  and a part folded exactly through its own middle still encloses a positive one. The
  check is now per-face and per-edge — an edge that has been shrunk past its own length
  and turned around — so it catches the fold where it happens rather than hoping it
  shows up in the total. The suggested radius the error offers is fixed by the same
  change, and no longer proposes a size inside the broken band. `EdgeBlend.cs`

### Known Issues
- Paint is as fine as the mesh it lands on. Vertex colours live one per vertex, so a
  bare box paints as a few colour blobs; add a Subdivide (or Sculpt) above the Paint
  feature and the same brush is as fine as the mesh.
- A painted model writes its colours into the DMX (the field names were read out of the
  compiler's own binary), and the whole chain up to that file is now checked on every test
  run — the strokes replay, they survive an edit to the feature underneath them, and they come
  back identical after a save and reopen. What is still unchecked is the far side of the
  compile: nobody has looked at how the engine's shader composites those colours. Check a
  painted model renders its paint before relying on it.
- Paint tints the material rather than covering it, which is what the engine's standard
  material does for free; paint that fully replaces the colour under it needs a shader.

## v367420 — 2026-09-04

### Added
- Faces of a part can be extruded. Click a face of anything on screen —
  a primitive, a boolean, something twenty features old — press Extrude,
  and it pulls. There is no sketch involved and none is asked for. Taper,
  Up to next, Through all, a second distance and New body all work from a
  face exactly as they do from a sketch profile. `SketchFeatures.cs`
- A plain pull is done by MOVING the face rather than by growing a boss on
  top of it, so the part stays a clean single solid you can still Shell
  afterwards. `FaceMove.cs`
- Move Face, a new tool on the Detail stage. Offset pushes each picked face
  along its own normal, so picking both sides of a wall makes it thicker or
  thinner. Translate moves them together along one direction, so the wall
  SLIDES and keeps its thickness — material added on one side and taken from
  the other. `FaceMove.cs`, `SolidFeatures.cs`
- Both refuse rather than guess: a face that is not flat, a face moving while
  a flat neighbour stays put, or a move far enough to turn the part inside
  out, each say which of those it was and what to do instead.
- The distance can be dragged instead of typed. Open Extrude or Move Face,
  pick the faces, and a set of arrows appears on them — turned to the face
  rather than to the world, so the blue one is always straight out. Pull it
  and the solid grows under the cursor, with the number in the panel
  counting up as it goes. It only ever sets the open tool's distance: no
  feature is created by dragging, and the arrows are gone the moment the
  tool is closed. `EffigyViewport.FaceDrag.cs`, `EffigyFeatureDialog.cs`
- The Profiles box in the Extrude panel picks faces. Press Extrude with
  nothing selected and the faces of your part are live straight away —
  hover one, click it, and that is your profile. Click it again to take it
  back off. No sketch has to exist and none is asked for. Sketch regions are
  still pickable in the same box at the same time, and a sketch in front of a
  face gets the click. `EffigyFeatureDialog.cs`, `EffigyViewport.Sketching.cs`
- Right-click a face of a part and every tool that can use that face is on the
  menu — Sketch, Fillet, Chamfer, Shell, Draft, Hole, Subdivide and Face
  Material — each opening already pointed at the face you clicked. Picking one
  is the same as selecting the face and pressing the button on the bar, so
  there is nothing new to learn. The menu's material entries are still there,
  under their own heading. `EffigyWindow.cs`, `EffigyViewport.Selection.cs`

### Improved
- The panel no longer says "No sketch yet — add a Sketch first" at a part
  that has faces you can point at. That message was painted whenever the
  document had no sketches, which is the normal state of a part built out of
  primitives — so it sent you off to draw a rectangle in order to pull the
  rectangle you were already pointing at. It now says what you can click, and
  the box stops being red once a face answers it. Revolve, Sweep and Loft
  still ask for a sketch, because a face is not something they can use.
  `EffigyFeatureDialog.cs`
- The line under the viewport that tells you what your selection is good for
  now asks the tools rather than reciting a list somebody typed. It had already
  drifted once — Subdivide learned to take faces and the sentence had to be
  edited by hand to admit it — and a tool that starts accepting a face now
  says so there by itself. `Feature.cs`, `EffigyWindow.cs`
- Subdivide asks which part you mean instead of taking the whole document. It
  read an empty selection as "everything", so one click could quadruple the
  triangle count of every part you had — including the ones off screen, and
  including a cage you were about to sculpt. Click a part in the Parts list and
  you get that part entire, or pick faces in the viewport and you get those.
  Subdividing a whole part is still there and still the one that smooths; it
  just will not guess which part.
- The grease pencil's colour picker is folded into the pen button itself
  instead of sitting next to it as a second control — click the pen to draw or
  put it down, open its dropdown to change colour. The eraser now works like
  the sketch Cut tool: hold the left button and drag through the notes you
  want gone, rather than clicking each one in turn. `EffigyWindow.cs`,
  `EffigyStageBar.cs`, `EffigyViewport.Notes.cs`
- The Edit menu is shorter and reads in groups instead of as one list of
  fifteen. The five sculpt-mask commands are behind a single "Sculpt Mask"
  submenu — they only do anything while a Sculpt feature is open, so they no
  longer sit in front of everyone else. The three "Normal Map:" entries were
  toggles whose only feedback was a status line that had already scrolled
  away; they move to Edit > Settings under "Normal map bake" as switches and a
  size dropdown you can actually read, and they are remembered between
  sessions now. `EffigyWindow.cs`, `EffigySettingsWindow.cs`

### Fixed
- Chamfered and filleted parts measured smaller than they are. The little
  triangles that cap each corner were being built inside-out, so they
  subtracted from the enclosed volume instead of adding to it — a chamfered
  1-unit box measured 0.811 against a true 0.883. Nothing looked wrong,
  because nothing was wrong to look at: the mesh was closed, valid and
  correctly shaped, and only the numbers taken off it were out. Those numbers
  are what collision hulls and physics are built from. `EdgeBlend.cs`

### Known Issues
- An oversized fillet or chamfer is not always refused any more. The check
  that catches "the blends have met through the middle" measures enclosed
  volume, and some of what it used to catch it was catching only because of
  the inside-out corners fixed above. On a 2-unit cube a fillet radius
  between about 1.0 and 1.25 now builds a part that is quietly
  self-intersecting instead of saying so. Below and above that band it still
  refuses correctly. `EdgeBlend.cs`
- A compiled model arrives with no material on it. Open one in Marionette, or
  drop it in a scene, and it renders in the bright red missing-material shader
  until you assign one by hand. The geometry, the UVs and the skinning are all
  fine — it is only the material reference that does not survive the compile —
  but the first thing anyone sees after their first export is a broken-looking
  model, which reads as the exporter having failed.

## v367389 — 2026-09-04

### Fixed
- A new feature added while the rollback bar sat at the end of the tree was
  never evaluated. It appeared in the tree, it was saved to the file, and it did
  nothing — the bar landed exactly on it rather than below it. Sketching on a
  face is where this showed: the plane is worked out when the sketch feature
  runs, so a sketch that never ran stayed on the global XY plane, and the face
  outline drawn on it collapsed to a single line lying flat through the model.
  Materials dropped on a face could go the same way.
- Exporting no longer overwrites the last thing you exported. Every part studio
  compiled to `models/effigy/export.vmdl` — one name for the whole project — so
  compiling the spatula replaced the grill, and anything already placed in a
  scene changed shape without a word. The exported `.vmdl`, `.obj`, `.dmx` and
  `.smd` now take the document's own name, and an unsaved studio is asked for
  one instead of being given a name that collides with the next.

### Added
- A grid switch and a spacing dropdown at the right-hand end of the sketch tool
  row, shown while a sketch is open. Both were already in Edit → Settings, which
  is the right home for setting up how the tool behaves and the wrong one for
  changing paper mid-drawing. They are the same two values, not a second copy —
  change one and the other follows.
- **View → Console** docks the editor's own console inside Effigy, along the
  bottom. It is the real one, not a copy — the same level filters, term filter,
  stack traces and command entry — so compile failures and anything you log
  show up without leaving the part you are working on.

### Improved
- Hovering a face lights up the whole face, whatever shape or size it is. A
  wall that a cut or a boolean left as many flat pieces used to light up one of
  those pieces — a triangle in the middle of it, a different triangle if you
  moved the mouse a hand's width — while the sketch grid covered the whole
  wall, so the highlight and the paper disagreed on screen at once. The
  highlight, the edge picker, the sketch outline, the material you drop on a
  face and the edges Fillet takes from one now all mean the same face.
- The seams inside such a wall are no longer offered as edges to pick. They are
  not edges of the part — rounding one does nothing — and on a heavily cut wall
  one was always within a few pixels of the cursor, which made the face
  underneath very hard to click at all.
- The grid on the face you are sketching on holds up at any size. It used to
  draw nothing at all once a face was large enough to want more lines than the
  cap allows; it now widens the spacing until the lines fit. It also thins out
  as the lines close up on screen instead of filling the face with solid
  colour, and fades away as the face turns edge-on, the way the reference
  planes already did.
- A `.effigy` part studio shows the model it builds in the asset browser, and
  in the inspector's preview panel, instead of the generic document icon every
  unrecognised file gets. It is the real thing, turning on the spot, wearing
  the materials you dropped on it. A studio the current build cannot read keeps
  the plain icon rather than putting an error in your console while you scroll
  a folder.
- Shipping is one command. `tools/ship.sh -m "what changed"` syncs, commits,
  tests, pushes, publishes the package, stamps this file with the revision that
  created, and prints the changelist text ready to paste. The paste is the only
  step left by hand, because the engine's package API can read changelists and
  has no method that writes one.

## v367360 — 2026-09-04

### Fixed
- Building against Geppetto no longer floods your compile with warnings. The
  editor assembly was compiling its own copy of four kernel files the game
  assembly already provides, so `Vec2`, `Xform`, `Skeleton` and `SoftBone` each
  existed twice — 1857 CS0436 warnings, and two types that read identically in
  source but will not substitute for each other across the game/editor line.
  Both assemblies now compile clean.

## v367356 — 2026-09-04

### Added
- Select first, then pick the tool. Click a face or a part in the viewport and
  the next feature you add starts already pointed at it, instead of making you
  choose again in the dialog. A face selection also tells the tool which part
  you meant, so filleting the thing you just clicked no longer rounds every
  part in the studio.
- Fillet and chamfer can round the edges you pick, not just every sharp edge on
  the part. Click near an edge in the viewport to add it, click again to drop
  it; leave the list empty and it behaves exactly as it did before. Picked
  edges are stored on the part, so they survive a save and a rebuild.
- Viewport lighting. Full bright is the default so faces stay readable while
  you model (Edit → Settings → Full bright); turn it off for a studio sun that
  matches a game scene. View → Add Point Light drops a lamp you can drag, and
  Delete removes the selected one. Lamps are viewport-only — they never export.
  (`EffigyViewport.Lights.cs`, `EffigySettingsWindow`)
- Double-clicking a `.effigy` file in the asset browser opens it in Effigy.
  Part studios now show up there like any other asset.
  (`EffigyPartStudioAsset`)

## v367329 — 2026-09-04

### Added
- Soft bones. A bone can carry stiffness, damping, weight and a cone, and the
  solver turns an animated pose into one with lag and swing in it. Written for
  the VR case where a controller reports a wrist and everything above it is
  invention — welded rigidly, the elbow pivots about the hand and reads as
  broken even though the hand is right. (`Effigy/Rig/SoftBone.cs`)
- Games can run the soft-bone solver at runtime, not just the editor. A
  four-file subset of the kernel ships to game assemblies — the arithmetic on
  `Vec3` and `Xform` and nothing that touches the filesystem, which the game
  sandbox would refuse anyway. (`Code/Effigy`)
- Animation clips bake into the compiled model, so what Effigy makes can be
  handed to AnimGraph. Author clips in Marionette, add them through
  File → Animation Clips…, and they are carried in on the next Compile .vmdl.
  Bones match by name and a mismatch is reported rather than silently dropping
  the clip. (`DmxAnimWriter`, `VmdlAnimation`)
- Copy and paste poses in Marionette. Copy takes every selected key, or the
  pose at the playhead, by bone name — and the clipboard outlives the clip, so
  you can copy idle's rest pose, open fire, and paste instead of re-posing.
- Play interaction clips in game without compiling a model.
  `RigAnimPlayerComponent` plays a `.riganim` on the character you already
  have. Playback runs to the last keyed frame rather than the full 900-frame
  canvas, and `NormalizedTime` is a 0..1 clock to tween a door or a lever
  against.
- Grease-pencil notes: annotations drawn over a part, stored on the document
  beside materials and hidden bodies so they survive a reopen. Deliberately
  outside the feature list, so no exporter can reach them — notes cannot appear
  in OBJ, DMX or the compiled vmdl. (`Effigy/Note.cs`, `Effigy/NoteSession.cs`)

### Fixed
- Exported animation no longer crumples the model. Every bone was written a
  quarter-turn out: the exporter built a bone's basis from the tool's own
  (Right, Forward, Up) naming, while an `Xform`'s columns are where the unit
  axes land, and the DMX writer read them back as the latter. Positions were
  correct in each parent's true frame, so the extra turn on a parent threw its
  children rather than simply tilting the model. (`ToXform`)

### Removed
- The "Marionette" menu this package used to add to your editor. Its only two
  entries rebuilt example clips belonging to Geppetto's own repo and meant
  nothing to anyone who installed the library to pose a model. Both are still
  there as the console commands `rig_build_sample` and `rig_build_wave`.

## v1 — 2026-09-02 (version 367036)

### Added
- First public release: two editor tools sharing one goal — make a usable,
  rigged, animated model without leaving the editor.
- **Effigy**, a parametric CAD modeller. Sketch on a plane or on the face of a
  solid, then extrude, revolve, sweep, loft, shell, bevel, mirror, pattern or
  subdivide it. Booleans cut for real through s&box's own PolygonMesh. The
  sketcher does lines, arcs, circles, ellipses and splines, finds closed
  regions rather than making you declare them, and edits in place with trim,
  extend, fillet and offset. A Levenberg–Marquardt solver handles seventeen
  constraint kinds and reports degrees of freedom, so an under-constrained
  sketch tells you what is loose instead of misbehaving. Everything sits in an
  ordered feature history with rollback and incremental rebuild, so changing a
  dimension near the bottom rebuilds what is above it.
- Rig and export from the same tool: a skeleton, auto-weighting smoothed across
  mesh adjacency, and a real skinned `.vmdl`. Sculpt and normal-bake are in
  there too, so detail can go onto a clean low-poly cage instead of into the
  topology.
- **Marionette**, a control-rig animator. Click a bone in the viewport and drag
  to rotate — the skeleton draws x-ray, so bones buried in the mesh stay
  clickable. Key it, move the playhead, pose again. One timeline lane per bone
  with the real interpolation curve drawn between keys, three easing modes, and
  undo in labelled steps where one drag is one step. Two-bone IK solves in
  closed form with rotation limits, so dragging a hand lets the elbow and
  shoulder follow without bending backwards. There is a first-person view
  framed off the model's own camera bone, reference props to pose against, and
  prop-attach events that spawn a model on a bone for a frame range.
- Clips save as `.riganim` and rigs as `.ctrlrig`, kept separate so several
  clips can share one rig. Constraints bake into keyframes rather than
  re-solving at playback, so a clip plays identically in game.

<!--
REVISIONS WITH NO CHANGELIST, and why - so a gap in the numbering reads as a
decision rather than an oversight. Each of these published real work; none of
it is visible to somebody who installed the package.

  367362  CHANGELOG restructured to match the changelist form's boxes.
  367359  tools/changelist.sh added; publish.sh waits for the version line.
  367358  Test samples write beside the suite instead of into the working
          directory, which had put 46 sample meshes into 367356's package.
  367334  Geppetto became its own repository; kernel, tests and tooling
          absorbed into it.
  367328  Wizard publish, same content as 367329.
-->
