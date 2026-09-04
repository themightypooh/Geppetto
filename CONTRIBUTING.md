# Contributing

You do not need write access. Fork the repo, make a branch, open a pull request.
Nothing lands on `main` unless the owner merges it.

## Run it

Clone this repo and open `geppetto.sbproj` in s&box. The tools show up under
**Tools**. Or copy `` into your own project's `Libraries/`.

## Kernel tests

```sh
export PATH="/c/Program Files/dotnet:$PATH" && ./tools/test.sh
```

Do this before a PR that touches `Effigy/`.

## Do not edit `Editor/Effigy/`

That folder is a **mirror** of `Effigy/`. Edit the canonical copy, then run
`tools/sync-kernel.sh` (or just `tools/test.sh`, which syncs first).
`KernelSyncTests` fails the suite if they diverge.

## Shipping

One command, and it is the whole thing:

```
tools/ship.sh -m "what changed"
```

That syncs the kernel mirror, commits, runs the suite, pushes, publishes the
s&box package, stamps `CHANGELOG.md`'s **Unreleased** section with the revision
the publish created, and pushes that too.

Two things to know:

- **The s&box editor has to be open on Geppetto**, because the package upload is
  the editor's, driven over its MCP bridge on `127.0.0.1:7269`. If it is not
  open, the git push still happens and the script says to run
  `tools/publish.sh --commit` later.
- **The changelist is the one manual step.** The engine's package API can read
  changelists and has no method that writes one, so no script outside a browser
  can post one. `ship.sh` finishes by printing the finished text, box by box —
  paste it into sbox.game → the package → Edit changelist, assigned to the
  revision it names.

Write `CHANGELOG.md` entries as you go, under the five headings the changelist
form uses: **Added, Improved, Fixed, Removed, Known Issues**. Write them for
whoever installed the package, not for whoever works on the repo.
`tools/changelist.sh` prints Unreleased; `tools/changelist.sh 367356` prints a
revision already published, for the site's "Assign to a revision" list.

## Pull requests

- One change per PR. "Fix sketch mode" is a PR. "Rewrite Effigy" is not.
- **Editor code is unsandboxed.** s&box editor tools can do anything on the
  machine. PRs that touch an `Editor/` folder get read before they merge.
- Say what you ran. "2099 checks pass" and "I clicked it in the editor" are
  different claims; both matter here.

## Bugs and ideas

Open a GitHub Issue. If you have the private working tree, start at
[docs/dev/HANDOFF.md](docs/dev/HANDOFF.md).
