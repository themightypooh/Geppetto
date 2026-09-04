# Changelog

What shipped in each Geppetto package revision on sbox.game.

Add lines to **Unreleased** as you go. When you publish a revision, rename that
heading to the version and paste the same text into the "Change Detail" box in
the upload dialog. The box is the last step before publish and is easy to blow
past, so this file is the record that survives either way.

## Unreleased

### Tooling
- The editor assembly no longer compiles its own copy of the four kernel files
  the game assembly already provides, so `Vec2` and friends are one type again
  instead of two that look identical. That was 1857 compiler warnings hiding
  every warning worth reading.
- `tools/changelist.sh` prints the notes below as lines ready to paste into
  the changelist form on sbox.game. The site is the only place a changelist can
  be made — the editor's API can read them and not write them — so this cuts the
  retyping rather than the step.
- `tools/ship.sh` now updates the s&box package too, so one command covers the
  repo and the library instead of leaving the package as a thing to remember
  afterwards. It needs the editor open on Geppetto — if it isn't, the push
  still happens and it tells you to run `tools/publish.sh --commit` later.
  `--no-publish` skips it.

### Effigy
- Select first, then pick the tool. Click a face or a part in the viewport and
  the next feature you add starts already pointed at it, instead of making you
  choose again in the dialog. A face selection also tells the tool which part
  you meant, so filleting the thing you just clicked no longer rounds every
  part in the studio.
- Fillet and chamfer can round the edges you pick, not just every sharp edge on
  the part. Click near an edge in the viewport to add it, click again to drop
  it; leave the list empty and it behaves exactly as it did before. Picked
  edges are stored on the part, so they survive a save and a rebuild.
- Double-clicking a `.effigy` file in the asset browser opens it in Effigy.
  Part studios now show up there like any other asset.
- Viewport lighting: full bright is the default so faces stay readable while
  you model (Edit → Settings → Full bright). Turn it off for the studio sun
  that matches a game scene. View → Add Point Light (or the same button in
  Settings) drops a lamp you can drag; Delete removes the selected one. Lamps
  are viewport-only — they never export. (`EffigyViewport.Lights.cs`,
  `EffigySettingsWindow`)
- Grease-pencil notes: annotations the modeller draws over a part, stored on the
  document alongside materials and hidden bodies so they survive a reopen.
  Deliberately kept out of the feature list so no exporter can reach them —
  notes cannot appear in OBJ, DMX or the compiled vmdl. Covered by `NoteTests`.
  (`Effigy/Note.cs`, `Effigy/NoteSession.cs`, `PartStudio`, `StudioDocument`)

## v1 — 2026-09-02 (version 367036)

First public release. Effigy modelling kernel and editor, Marionette rig control
editor, 130 source files.
