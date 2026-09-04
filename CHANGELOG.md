# Changelog

What shipped in each Geppetto package revision on sbox.game.

Add lines to **Unreleased** as you go. When you publish a revision, rename that
heading to the version and paste the same text into the "Change Detail" box in
the upload dialog. The box is the last step before publish and is easy to blow
past, so this file is the record that survives either way.

## Unreleased

### Effigy
- Grease-pencil notes: annotations the modeller draws over a part, stored on the
  document alongside materials and hidden bodies so they survive a reopen.
  Deliberately kept out of the feature list so no exporter can reach them —
  notes cannot appear in OBJ, DMX or the compiled vmdl. Covered by `NoteTests`.
  (`Effigy/Note.cs`, `Effigy/NoteSession.cs`, `PartStudio`, `StudioDocument`)
  - Kernel and tests only so far. No editor UI yet, and not yet mirrored into
    `Editor/Effigy/` — run `tools/sync-kernel.sh` before publishing.

## v1 — 2026-09-02 (version 367036)

First public release. Effigy modelling kernel and editor, Marionette rig control
editor, 130 source files.
