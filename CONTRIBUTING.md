# Contributing

You do not need write access. Fork the repo, make a branch, open a pull request.
Nothing lands on `main` unless the owner merges it.

## Run it

Clone this repo and open `marionette.sbproj` in s&box. The tools show up under
**Tools**. Or copy the folders listed in the README into your own project.

## Kernel tests

```sh
export PATH="/c/Program Files/dotnet:$PATH" && ./tools/test.sh
```

About 30 seconds. Do this before a PR that touches `Effigy/`.

## Do not edit `Editor/Effigy/`

That folder is a **mirror** of `Effigy/`. Edit the canonical copy, then run
`tools/sync-kernel.sh` (or just `tools/test.sh`, which syncs first).
`KernelSyncTests` fails the suite if they diverge.

## Pull requests

- One change per PR. "Fix sketch mode" is a PR. "Rewrite Effigy" is not.
- **Editor code is unsandboxed.** s&box editor tools can do anything on the
  machine. PRs that touch `Editor/` get read before they merge.
- Say what you ran. "2099 checks pass" and "I clicked it in the editor" are
  different claims; both matter here.

## Bugs and ideas

Open a GitHub Issue. The live list of unfinished work is
[docs/dev/WHAT-IS-LEFT.md](docs/dev/WHAT-IS-LEFT.md). If you want to *work on*
the code, start at [docs/dev/HANDOFF.md](docs/dev/HANDOFF.md).
