# Work order

**For the agent finishing this** — Grok, as of 31 August 2026 — and for whoever follows. Phase one
first, then everything after it, in the order that will actually unblock things.

This is a routing document, not a replacement for the other four. It says *what to do next, in what
order, and how to know it worked*. The reasoning behind each item lives where it already lives, and
every entry names that place rather than restating it.

Read [HANDOFF.md](HANDOFF.md) first — it is how not to waste the session. Then this.

---

## The two rules that decide whether this goes well

**1. A mesh can be closed, manifold, Euler-correct and valid while being visibly wrong.** Every hard
bug in this repo's history passed most of its checks. Measure enclosed volume, covered area, or
boundary-edge count. "It renders and looks fine" has been wrong four separate times, and the fillet
that silently inverted a cube stayed `valid, closed`, Euler 2, same face count, at every radius.

**2. "Written" is not "working".** Two words are used precisely in [WHAT-IS-LEFT.md](WHAT-IS-LEFT.md)
and they are used precisely here: **written** means it compiles and has never been run; **not
started** means no code exists. A large fraction of the editor is in the first state. Do not report
a written thing as done because you read it and it looked right.

Run the suite before touching anything:

```sh
export PATH="/c/Program Files/dotnet:$PATH" && ./tools/test.sh
```

~25 seconds, **1713 checks, 0 failing** as of this writing. `dotnet` is installed but not on `PATH`
in Git Bash, hence the prefix — without it the script tries `apt-get` and dies. The script syncs the
editor's kernel mirror first (see HANDOFF.md, "The two kernel copies"): **never edit
`Editor/Effigy/` by hand**, it is generated from `Effigy/`.

---

## The working tree is not clean, and you should know what is in it

Diagnostics have landed (kernel + dialog + tests; `DIAGNOSTICS-BRIEF.md` is deleted). Uncommitted
paths under `Assets/`, `Code/TamagotchiTeardownKit.cs`, `tools/tama_gen.py`, `tools/_unit_*` are
po's own work on something else. Leave them alone.

---

# Phase one — the CAD stage

**Done means:** a person can open Effigy, draw, model a real part with the tools the kernel already
supports, be told clearly when a feature refuses, and hand a clean quad cage to the next stage. The
kernel is ~93% there. **The editor is the smaller-finished half and the larger remaining one.**

## A. The sitting — do this first, it costs an hour and it re-prices everything else

Everything in [WHAT-IS-LEFT.md §0](WHAT-IS-LEFT.md) is written, compiles, and **has never been seen
on screen**, because the environment that produced it had no s&box. That is no longer true. Until
this is done, every estimate below is a guess.

1. **Cube → sketch on its face → extrude.** The single highest-value item, and the one po wants on
   video. The kernel half is fully tested (`FaceSketchTests`, `RaycastTests`, `EditorFlowTests`); the
   UI half has only ever been compiled.
2. **The extrude gizmo and numeric entry.** Do not assume the gizmo works until it has been run once.
3. **Sketch toolbar swapping** — ten seconds, enter a sketch and watch the strip.
4. **Sweep and Loft buttons**, and their two hand-drawn glyphs, which were drawn against a nominal
   18×18 box and never rendered.
5. **The six new constraints** — Diameter, Midpoint, Concentric, Fix, Tangent, TangentArcs. Confirm
   each appears, applies, and leaves a mark that can be clicked to delete it.
6. **Settings window, plane corner resize** — judge them.

**Method:** the editor's own MCP server takes the pictures (`camera_screenshot`,
`editor_camera_screenshot`) and `editor_status` reports the compile — see HANDOFF.md. Write what you
find into WHAT-IS-BUILT.md as *verified by* rather than leaving it in WHAT-IS-LEFT.md as *written*.

## B. ~~Finish the diagnostics~~ — **done** (kernel + tests; dialog written, not seen)

`Fillet(cube, 0.85)` is an error. A too-thick shell offers a thickness that fits. A boolean that
misses names the gap; an open solid names the boundary. A selection naming a missing body is an
error, not a silent no-op. `DiagnosticTests` plus the raised empty-studio bar are in the suite
(**1609 checks**). The dialog shows problem / cause / remedy-as-button and the tree has a tooltip
plus a yellow warning icon — **written against `Editor.Label.WordWrap` and `TreeNode.GetTooltip`,
not judged on screen.** That sitting is WHAT-IS-LEFT.md §0 item 7.

## C. The rest of phase one, in priority order

| # | Item | Where | State |
|---|---|---|---|
| 1 | **Boolean past the one case that works** | §1.1 | one hole in one box is a proven path, not an envelope |
| 2 | **The six sketch tools with no UI** | §2.3 | trim, extend, offset, fillet, ellipse, spline — kernel done and tested, no way to click them |
| 3 | **Revolve's axis picked in the viewport** | §2.7 | Vec3-only, defaults through the sketch origin, so the first press reliably errors — reads *broken*, not unfinished |
| 4 | **Hide affordance for planes and origin** | §2.4 | po's spec verbatim; nothing written at all |
| 5 | **Hover a sketch face → select its sketch** | §2.5 | po's spec; nothing implements it |
| 6 | **Draft on existing faces** | §1.4 | genuinely absent; `Taper` only covers a face being *made* |
| 7 | **A hole feature** (counterbore/countersink) | §1.5 | convenience, not capability |
| 8 | **Collision from the primitive history** | §1.3 | nothing exists; bookkeeping, not geometry |
| 9 | **Sketch-strip icons + glyph scaling** | §2.6 | ~14 glyphs of real design work; safe but generic today |

Section numbers are [WHAT-IS-LEFT.md](WHAT-IS-LEFT.md), where each has its method written out.

**On item 1, the one with a real chance of changing the others:** `MeshHoleRepair` declines any
boundary loop it cannot place in exactly one coplanar face, so a cut through a **curved** face, a cut
**meeting an edge**, overlapping cuts, and a cut that **splits the body in two** are all unexercised
and at least one will fail. Build each in the editor, run `effigy_dump_tree`, and reproduce any
failure as a hand-built fixture in `HoleTests` — `TestBoundaryLoopRepair` is the template and needs
no engine. **Do not measure this by eye:** all four bugs already fixed in the boolean produced
closed, manifold, Euler-correct, valid meshes.

---

# After phase one

Ordered by what unblocks the pipeline, which is not the same as what is most interesting.

## D. The sculpt stage — phase two

Steps 1–4 are built — see [WHAT-IS-BUILT.md](WHAT-IS-BUILT.md). Next is **step 5, multires levels**.
[WHAT-IS-LEFT.md §4](WHAT-IS-LEFT.md) is still the plan. Steps 5–6 are pure kernel and verifiable
headlessly; **step 7, the editor, is the long pole**.

The CAD side owes it exactly one thing that is not already done: **non-overlapping UVs on the cage**,
which nothing currently checks. Quad-dominant output is no longer a risk — the boolean returns
n-gons rather than triangle soup, and sweep and loft are quad-only by construction.

## E. Rigging

1. **Weight painting** — not started, and the only item on the phase-two list with no progress at
   all.
2. **`AnimBindPose`** — unverified, *not* missing. A guessed KV3 shape risks breaking a compile that
   currently works, so this wants a real editor session against `citizen.vmdl`, not a guess.
3. **Rig-panel reporting** — zero-length bones, missing mapped bones, failed `SkinWeights.Validate()`
   should surface as warnings.
4. **Effigy → Rig Control convenience action** — integration sugar, explicitly not a priority.

Non-goals, so they are not re-proposed: no Effigy timeline or duplicate FK/IK, no vertex-index rig
storage, no SMD import back into the parametric document, no heat-diffusion solver.

## F. Shader Forge

All five phases are built and live preview is in. What remains is contact with the compiler:

1. Run `dotnet run --project ShaderForge.Tests -- out`, then `shaderforge_probe` in the console.
2. **Generate one shader and get it compiling.** No test can judge the HLSL — the suite checks
   structure only. Expect field-name corrections; the likeliest failures are `Material::From( i )`
   (`pixel_arms.shader` uses `Material::Init()`), `m.Emission`, `m.Opacity`, `g_flTime`.
3. Per-slot material override is written but unproven; the preview panel's fallback message says
   which case you are in.
4. Delete the stale `Assets/shaders/custom/wind.shad` from an early run.

## G. Rig Control papercuts

Four, each small and each real: the example clip's wrist never rotates (the IK solver keeps the end
bone's orientation); the tutorial's settle step only checks that *a* key exists after frame 21; the
reference-prop step ticks the moment a model is assigned; the tutorial panel's layout has never been
judged at various dock sizes.

---

## Keeping the docs true

Four docs, on purpose. When something lands:

- It moves **out of** WHAT-IS-LEFT.md and **into** WHAT-IS-BUILT.md, and the entry there names *how
  it was verified* — "1713 checks" and "I looked at it in the editor" are different claims and the
  difference has repeatedly been the difference between right and wrong.
- A temporary brief (`DIAGNOSTICS-BRIEF.md`, `FBX-EXPORT-BRIEF.md`) is folded in and **deleted**.
- This file is a work order. When phase one is done, delete section A–C rather than leaving a
  finished list to be re-read as an outstanding one.
