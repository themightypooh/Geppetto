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

When you publish a revision, rename **Unreleased** to the version, run
`tools/changelist.sh`, and paste each block into its box on the site.

## Unreleased

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

### Improved
- Publishing this package is one command. `tools/ship.sh` syncs, commits, tests,
  pushes and then updates the s&box package, instead of leaving the package as a
  thing to remember afterwards. It needs the editor open on Geppetto — if it
  isn't, the push still happens and it says to run `tools/publish.sh --commit`
  later. `--no-publish` skips it.
- `tools/changelist.sh` prints these notes as the boxes the changelist form
  asks for. The site is the only place a changelist can be made — the editor's
  API can read them and not write them — so this cuts the retyping, not the
  step.

### Fixed
- The editor assembly no longer compiles its own copy of the four kernel files
  the game assembly already provides, so `Vec2` and friends are one type again
  instead of two that look identical to read and refuse to substitute for each
  other. That was 1857 compiler warnings hiding every warning worth reading.
- The test suite writes its samples beside itself rather than into whatever
  directory it was launched from, which had quietly put 46 sample meshes into
  the published package.

## v1 — 2026-09-02 (version 367036)

First public release. Effigy modelling kernel and editor, Marionette rig control
editor, 130 source files.
