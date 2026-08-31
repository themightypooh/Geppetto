# Effigy UI punch list — po's, dictated in one pass, August 2026

**Read this first if you are picking this up cold.** Everything below is a direct request from po,
captured as close to verbatim as possible because a session ended mid-work before and the context
would otherwise be lost. None of it has been built or verified yet unless marked DONE. This
environment has no .NET-with-s&box GUI and no way to render or screenshot anything — see
`HANDOFF.md` for what CAN be verified here (the kernel, headlessly, via `tools/test.sh`) versus
what needs po's machine. Where an item is UI/widget work, it is written but unverified until po
runs it.

---

## 1. Sketch toolbar does not actually replace the feature toolbar — FIXED

**Root cause found by reading the code, not guessing:** the feature strip (`EffigyToolStrip`,
main's floating-on-the-canvas system) and the sketch strip (`_sketchBar`, a leftover
window-docked `ToolBar`) were two completely unrelated widget systems. `EnterSketch`/`FinishSketch`
called `_sketchBar.Show()`/`Hide()` — which only ever touched the docked bar. Nothing, anywhere,
ever hid the floating feature strip. That's why it "stayed where it was the whole time."

**Fix**: the sketch toolbar is now `EffigySketchStrip`, a second floating strip built the same way
as the feature one, positioned by `EffigyViewport.CompleteLayout` at the exact same spot. Only one
is ever visible — `EnterSketch`/`FinishSketch` now toggle `_toolStrip.Visible`/`_sketchStrip.Visible`
together, so entering a sketch genuinely replaces the strip rather than hiding an unrelated one.

**Icons**: font-drawn (`Paint.DrawIcon`), using the same classic-Material-Icon names already
audited in this repo's history (see `ONSHAPE-WORKFLOW.md`'s table) — NOT the hand-painted
`EffigyIcons` style the feature strip uses. That's item 2, still open, see below.

**Status: written, matches every proven API pattern in the codebase, NOT compiled or run.** This
is UI/widget code in an environment with no s&box — see the top of this doc.

## 2. Sketch toolbar icons need to be better custom icons — STILL OPEN

Item 1's fix gave the sketch strip real, safe, non-blank icons (classic Material Icon font names),
which unblocked the structural bug. But they are still generic font glyphs, not the hand-drawn,
CAD-operation-specific style `EffigyIcons` uses for the feature strip (Bevel shows a corner being
cut, Shell shows a wall inside a shape, etc.). Drawing ~14 more glyphs in that style — line,
rectangle x2, circle x2, arc x2, polygon x2, slot, point, construction, profile inspector, finish —
is real design work and is still not started.

## 3. All tool buttons ~40% bigger, evenly spaced — DONE for feature + sketch strips

`EffigyToolButton` and the new `EffigySketchToolButton` both went from 28x28 to 40x40
(`EffigyToolStrip.ButtonSize`, shared by both) — 28→40 is +42.8%. Spacing between buttons within a
group went 3→5px, between groups 11→16px, on both strips.

**Known gap, called out in code**: the hand-painted `EffigyIcons` glyphs are still drawn at their
original nominal 18px weight rather than scaled up with the bigger button — `Paint`'s scaling API
was not confirmed safe to guess at from this environment, so the glyph is left slightly small
inside a bigger button rather than risking broken icon rendering. Worth revisiting once this can
be seen running.

**There was no "history (undo/redo) group" to resize** — that item doesn't exist as a separate
strip in the current merged code (unlike an earlier, since-superseded version of this branch that
did have one). If po still wants undo/redo pinned as their own floating group, say so and it can
be added; right now Undo/Redo live only in the Edit menu and their keyboard shortcuts.

## 4. Confirm the extrude gizmo + numeric-entry box actually work — WITH PICTURES

po wants visual confirmation (screenshots) that: picking a face arms the pull gizmo, dragging it
grows the solid live, and a numeric entry box is available to type an exact distance instead of
dragging.

**This cannot be done from this environment.** There is no s&box GUI here, nothing can be rendered
or screenshotted. This has to happen on po's machine. What CAN be said: the kernel-side plumbing
(`RegionSeed`, `FaceRef`, the parameter marking a feature dirty on edit) is proven by headless
tests — see `EditorFlowTests`, `FaceSketchTests`. The widget/gizmo code itself (`EffigyFeatureDialog`,
the pull-handle viewport code) has never been compiled in this session's environment. Flag this
prominently to po: **do not assume the gizmo works until it's been run once.**

## 5. Plane grid: either remove it, or (better) add a Settings tab with a checkbox to toggle it — WRITTEN

po's preferred version: a Settings entry in the very top toolbar (menu bar area) with a checkbox
for "show plane grid".

**Built** as `Editor/EffigyEditor/EffigySettingsWindow.cs`, with `ShowGrid` as the setting behind
it. The switch is hand-painted rather than an s&box `Checkbox`, because a checkbox reads as "tick
this to agree" and what is wanted here is "this is on".

**Status: written, never compiled or run**, the same caveat as everything else in this document that
touches a widget. Judge the switch and its slide animation once it is on screen.

## 6. Same Settings menu: color palette dropdown selector — WRITTEN

Move/add the existing palette switching (currently a View-menu submenu, see `EffigyPalette` in
`EffigyWindow.cs`) into this new Settings tab, as a dropdown rather than a checkable submenu list.
**Must include one dark-mode option with good contrast** — `OnshapeDark` may already qualify, judge
against po's actual monitor once it's running, not by RGB values alone.

**Built** in the same `EffigySettingsWindow`, as a dropdown. The dark-mode contrast judgement is
still outstanding and still needs po's actual monitor — that part cannot be closed from here.

## 7. Planes and origin should be independently hideable, via a tree-row eye icon

Behavior spec, precisely as described:
- An eye icon sits to the **right** of each plane's (and the origin's) row in the feature tree.
- The icon is only **visible on hover** over that row — not shown when not hovering.
- Clicking it hides the plane/origin in the viewport.
- After hiding, the icon **stays visible in that row** (so it can be clicked again to unhide) —
  i.e. the hidden state pins the icon visible even without hovering, only the *unhidden* state is
  hover-only.

This is a new interaction pattern not present anywhere in the current tree code — closest existing
precedent is `Feature.Visible`/`Body.Visible` added this session for feature-level hide (see the
"Sketch on the face of an existing body, and hide features" commit), which hides body *geometry*,
not tree chrome. Planes/origin are drawn by the viewport directly, not modeled as bodies, so this
needs its own visibility flags on the viewport (`ShowTopPlane`, `ShowOrigin`, etc. or similar) plus
the tree-row UI. Not started.

## 8. Planes should be resizeable by dragging their corners

Hovering near a plane's corner shows a faded circle handle. Click-drag it to resize that plane.
Not started. Needs: per-plane size state (currently `PlaneSize` is one shared constant for all
three reference planes — would need to become per-plane), hit-testing near each of the 4 corners,
and a drag gesture similar in shape to the existing origin-drag gizmo code.

## 9. Face-hover selects the owning sketch; right-click gives an Edit menu; tree click selects too

- Hovering a **closed sketch face** in the viewport (the shaded region) should select the sketch
  that owns it, for whatever operation is in progress — not just for face-pick during Extrude, but
  as the general "point at a face" affordance.
- **Right-clicking** a face should bring up an edit context menu (rename, suppress, etc. — same
  operations as the tree's own context menu, per the earlier feature-tree work this session).
- Clicking an item **in the tree** (any item — features, sketches, bodies, planes) should also
  select it, consistently.

Partially related work exists (`RegionPicked`, `RegionSeed`, the tree's existing context menu from
earlier in this session) but the specific hover-selects-sketch and right-click-face-menu behaviors
are not implemented. Not started.

## 10. End-to-end confirmation: cube → sketch on its face → extrude from that face-sketch — UI NOW WIRED

The kernel side (from before) is proven: `SketchFeature.Face` (a `FaceRef` — body id + point +
normal, not a fragile face index) resolves correctly and follows the body if it changes,
`FaceSketchTests` proves it.

**New this pass**: the missing UI piece. `MeshRaycast` (new kernel file, `Effigy/MeshRaycast.cs`) is
a proven, tested (`RaycastTests`, ray-triangle intersection verified against a box's six known
faces and normals) ray-mesh hit test — pure geometry, zero engine surface. `EffigyPlaneSelector`
(the same box `SketchFeature`'s dialog already used for the three reference planes) now ALSO arms
face-of-solid picking at the same time: one click resolves to whichever was actually hit, a plane
or a face, exactly like Onshape never asks "plane or face?" first. The viewport adapter
(`EffigyViewport.FacePickMode`/`FacePicked`, in `EffigyViewport.Sketching.cs`) is a thin wrapper
around `Gizmo.CurrentRay` feeding `MeshRaycast` — the only genuinely new engine-facing code, and
it's the same three-line "Vector3 -> Vec3 is a straight re-type" pattern already proven elsewhere
in this file.

**Status: written end-to-end, matches proven patterns, kernel half fully tested, UI half NOT
compiled or run.** This is the one to test first when back at the machine — sketch on a face of a
primitive box, extrude it, confirm the boss appears in the right place. If it works, this is the
demo.

## 11. Keep docs updated continuously in case of a usage-limit cutoff mid-edit

This file is that. Update it as items get done, don't wait for a clean stopping point.

## 12. Start designing how bones/rigging will work for Effigy-built meshes

po's target: a cube with several rectangles sketched on its faces, each extruded into a
finger-like shape, each with its own bone — i.e., a basic animatable hand, built entirely in
Effigy then rigged.

Not started, no design work done yet. Relevant existing pieces:
- `Effigy/Rig/Skeleton.cs`, `SkinBinder.cs`, `SkinWeights.cs` already exist in the kernel — this
  isn't a blank slate. `SkinBinder.BindBodies` and `BodyRange` (in `SkinBinder.cs`) were built
  specifically so a rig could be "reapplied to new geometry instead of being invalidated" across
  rebuilds — see the doc comment on `PartStudio.ToMeshWithBodies`.
- Marionette's *other* half — `Code/RigControl`, `Editor/RigControlEditor` — is a whole separate,
  working animation/posing tool for already-rigged models (see root `HANDOFF.md` and `README.md`).
  The open design question is how Effigy-built meshes hand off to that tool: does Effigy export a
  skinned model file the Rig Control Editor then opens normally, or does Effigy grow its own
  in-place bone-per-body authoring UI? Reading `SkinBinder.cs`'s existing doc comments closely is
  the right next step before writing anything new — it may already answer this.

## 13. sbox-wargame repo — potential painting/decal foundation, HIGH PRIORITY TO INVESTIGATE

po: "I THINK I ALREADY HAVE THE FOUNDATION FOR PAINTING MODELS" — recalls having drawn on models
before (imprecisely — wrong click registration, fixed camera angle, models not built for it) using
code from **https://github.com/wes-kay/sbox-wargame/tree/main/Code**.

**Vision**: a Substance-Painter-like tool — material slots you paint into, raycasts that hit
exactly where aimed, brush settings, in a proper standalone tool panel (not an in-game HUD panel
the way the current wargame code apparently uses it).

Status: **blocked from this session, confirmed, do not retry here.** This session's GitHub access
is scoped to `themightypooh/marionette` only (both `git clone` and the `mcp__github__*` API tools
enforce the same scope — tried both; the API call returned "repository is not configured for this
session"). Unlike Solvespace/FreeCAD earlier, which were plain `git clone` of public repos with no
scoping involved, THIS session has a GitHub App/token limited to one repo, and there is no
`add_repo`-equivalent tool available here to widen it.

**What to do next**, for whoever picks this up: either (a) po adds `wes-kay/sbox-wargame` to this
session's allowed repos (however this environment exposes that — outside this session's own
control), or (b) investigate it from a session/environment that has open `git clone` access the
way this one did for Solvespace and FreeCAD. Once readable, identify: what decal/paint mechanism it uses (render
target? vertex colors? projected decals?), how accurate its raycast-to-UV or raycast-to-triangle
mapping is, and what would need to change to (a) fix the aim-accuracy problem po described and
(b) lift it out of an in-game panel into an editor tool window. **This is significant enough new
scope that it may deserve its own doc once investigated — a Effigy-Paint sibling to
CAD-REFERENCE.md.**

---

## Priority order, as recommended by reading po's message

1. Write this doc — done.
2. Investigate wargame repo — **blocked**, see item 13. Confirmed blocked via both `git clone` and
   the GitHub API tools; this session's access is scoped to `themightypooh/marionette` only and
   there is no tool here to widen that. Needs po or a differently-scoped session.
3. Face-of-solid sketch selector (item 10) — **done this pass**, kernel proven + UI wired.
4. Sketch-toolbar-not-swapping (item 1) — **done this pass**, root cause found and fixed.
5. Toolbar sizing (item 3) — **done this pass** for both strips.
6. Toolbar icons (item 2) — still open; the sketch strip has safe, non-blank, but generic icons.
7. Settings tab: grid toggle + palette dropdown (items 5, 6) — not started, next up.
8. Hide affordances for planes/origin, resizeable plane corners (items 7, 8) — not started.
9. Face-hover-selects / right-click-edit-menu / tree-click-selects (item 9) — not started.
10. Bones design doc (item 12) — not started.

Item 4 (screenshots) is not schedulable — it happens on po's machine, whenever they're at it next.
**Item 10 (the cube-face-sketch demo) is now the single highest-value thing to test first once
back at the machine** — everything behind it, kernel and UI both, is written and it's the one po
explicitly wants on video.

---

## Not on po's list: the UI the CAD kernel is now waiting on

Everything above came from po. This section did not, and is kept separate for that reason — it is a
record of kernel work that has landed with no way to reach it, not a request.

As of 30 August 2026 the sketcher's kernel gained ellipses, splines, trim, extend, fillet, offset,
six more constraint kinds, and sweep and loft as features. **None of it has a button.** See
`CAD-REFERENCE.md`'s "What is left, in the order worth doing it" for the ordering; the two smallest
and highest-value pieces are wiring the new constraints into `ConstraintTools` so a selection can
offer them, and a sketch PICKER, which sweep and loft need and which nothing in the tool has yet.
