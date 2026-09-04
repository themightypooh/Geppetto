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

When you publish a revision, rename **Unreleased** to its version, then run
`tools/changelist.sh <version>` and paste each block into its box on the site.
`tools/changelist.sh` with no argument prints Unreleased.

NOT EVERY REVISION EARNS A CHANGELIST. A publish that moved only tests, build
scripts or repo layout changed nothing an installed user can feel, and a
changelist saying so is noise on the package page. Those revisions are listed
below the sections, named, so it is clear they were considered rather than
forgotten.

## Unreleased

### Fixed
- Exporting no longer overwrites the last thing you exported. Every part studio
  compiled to `models/effigy/export.vmdl` — one name for the whole project — so
  compiling the spatula replaced the grill, and anything already placed in a
  scene changed shape without a word. The exported `.vmdl`, `.obj`, `.dmx` and
  `.smd` now take the document's own name, and an unsaved studio is asked for
  one instead of being given a name that collides with the next.
- A `.effigy` part studio no longer fills your console with "Tried to load
  ... but couldn't load from data" whenever the asset browser looks at it.
  Nothing was ever broken behind it — claiming the extension tells the engine
  the file is a serialised resource, so the browser kept trying to read a part
  studio as one — but it repeated forever, because a failed thumbnail is never
  cached and so is attempted again every time the tile scrolls back into view.

### Improved
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
