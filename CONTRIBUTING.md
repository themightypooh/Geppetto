# Contributing

Fork, branch, open a pull request. Nothing lands on `main` unless the owner merges it.

## Layout

| Folder | What it is | Compiled by |
|---|---|---|
| `Effigy/` | The modelling kernel. Plain C#, no engine types. **The canonical copy.** | `Effigy.Tests` |
| `Editor/Effigy/` | A mirror of `Effigy/`. **Never edit it by hand.** | s&box editor assembly |
| `Code/Effigy/` | The few kernel files the game assembly also needs (`Vec`, `Xform`, `Rig/Skeleton`, `Rig/SoftBone`). | s&box game assembly |
| `Editor/EffigyEditor/` | Effigy's window, viewport, panels and exporters. | s&box editor assembly |
| `Code/RigControl/` | Marionette's runtime: asset types, playback components, solvers. | s&box game assembly |
| `Editor/RigControlEditor/` | Marionette's window. | s&box editor assembly |
| `Code/Cloth/` | Live garment cloth for clothing made in Effigy. | s&box game assembly |
| `Effigy.Tests/` | The headless kernel suite, plus the generators and CLI behind `tools/effigy.sh`. | `dotnet` |
| `tools/` | Test, sync, ship, publish and changelog scripts. | - |

The kernel has two copies because s&box only compiles `Code/` and `Editor/`, and the kernel
can't live in `Code/`: its writers call `File.WriteAllText`, which the game sandbox forbids.
`Effigy/` stays outside both so it can be tested without the engine.

## Edit the kernel in `Effigy/`, then sync

`tools/sync-kernel.sh` copies `Effigy/` over `Editor/Effigy/`. `tools/test.sh` runs it first, so
testing is usually enough. `KernelSyncTests` fails the suite if the two copies differ, and the
sync script refuses to run if the mirror holds a file the canonical copy doesn't have. That
means somebody wrote it on the wrong side: move it into `Effigy/`.

## Run the tests

You need the **.NET 8 SDK**. Nothing else; s&box is not involved.

```sh
sh tools/test.sh
```

That syncs the mirror, lints the editor sources for a missing `using Editor;` (which the engine
would otherwise report as hundreds of identical errors), then builds and runs `Effigy.Tests`.
It prints `N passed, M failed` and writes sample models and previews to `Effigy.Tests/out/`.
On a Debian-family machine with no SDK it installs one through `apt`; anywhere else, install
.NET 8 yourself first.

Run it before any PR that touches `Effigy/`. Editor code (`Editor/`, `Code/`) can only be
checked by opening the project in s&box; the suite never compiles it.

## Look at what you built

`tools/effigy.sh` drives the kernel from the shell, with no editor:

```sh
tools/effigy.sh render model.effigy out.png
tools/effigy.sh describe model.effigy
```

`render`, `describe`, `measure`, `match`, `script`, `fix`, `parts`, `new` and `wardrobe` are all
listed at the top of the script. See [EFFIGY-API.md](EFFIGY-API.md) for building models from code.

## The changelog

Write `CHANGELOG.md` entries as you go, under **Unreleased**, using the five headings the
sbox.game changelist form asks for: **Added, Improved, Fixed, Removed, Known Issues**. Write them
for someone who installed the package, not for someone working on the repo.
`tools/changelist.sh` prints Unreleased box by box, ready to paste; `tools/changelist.sh <revision>`
prints one that has already shipped.

## Shipping (owner only)

```sh
tools/ship.sh -m "what changed"
```

One command does it all: sync, commit, run the suite, push, publish the s&box package, stamp the
changelog with the new revision, and print the changelist text.

- **Publishing needs the s&box editor open on this project.** The upload is the editor's, driven
  over its MCP pipe (a local socket since s&box 26.10, not `127.0.0.1:7269` any more) through
  `tools/sbox-mcp.py`. If the editor is closed, the push still happens; run
  `tools/publish.sh --commit` later.
- **The changelist is pasted by hand.** The package API can read changelists and can't write
  them, so paste what `ship.sh` prints into sbox.game ▸ the package ▸ Edit changelist.

## Pull requests

- One change per PR.
- **Editor code is unsandboxed.** s&box editor tools can do anything on the machine, so a PR
  that touches `Editor/` gets read line by line before it merges.
- Say what you ran. "The suite passes" and "I clicked through it in the editor" are different
  claims, and both matter.

## Bugs and ideas

Open a GitHub issue.
