# Effigy UI punch list — po's, dictated in one pass, August 2026

**Read this first if you are picking this up cold.** Everything below is a direct request from po,
captured as close to verbatim as possible because a session ended mid-work before and the context
would otherwise be lost. None of it has been built or verified yet unless marked DONE. This
environment has no .NET-with-s&box GUI and no way to render or screenshot anything — see
`HANDOFF.md` for what CAN be verified here (the kernel, headlessly, via `tools/test.sh`) versus
what needs po's machine. Where an item is UI/widget work, it is written but unverified until po
runs it.

---

## 1. Sketch toolbar does not actually replace the feature toolbar

**Reported symptom:** entering sketch mode is supposed to swap the feature tool strip for the
sketch tool strip, in the same slot, above the viewport. It does not — both are visible /
the feature strip stays where it was the whole time instead of being replaced.

Status: **not yet investigated in this session.** Next step is reading `EffigyWindow`'s
`ShowSketchTools`/sketch-mode-entry path and `EffigyViewport`'s toolbar-hosting code together,
since this branch went through a full merge with `main` for the toolbar system and the two sides
built genuinely different toolbar architectures (see `ONSHAPE-WORKFLOW.md`'s status note at the
top — main's `EffigyToolStrip` won the merge). This is exactly the kind of seam a merge leaves
behind. Suspect the visibility toggle references the wrong strip object, or the sketch strip is
being added without hiding the feature one.

## 2. Sketch toolbar icons need to be better custom icons

Current icons are placeholders / generic. Every sketch tool (line, rectangle, circle, arc, polygon,
slot, point, construction toggle, profile inspector, finish sketch) needs a hand-drawn icon in the
same style as `EffigyIcons` (main's drawn-icon system — chosen over font icon names specifically
because s&box ships classic Material Icons, not Symbols, and font-name lookups were rendering
blank). Not started.

## 3. All tool buttons ~40% bigger, evenly spaced

Applies to every tool strip: feature tools, sketch tools, history (undo/redo) group. Current sizing
predates this request. Not started. This is a small, mechanical, low-risk change once the button
widget is located — should be done together with item 1 so both toolbar fixes land in one pass.

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

## 5. Plane grid: either remove it, or (better) add a Settings tab with a checkbox to toggle it

po's preferred version: a Settings entry in the very top toolbar (menu bar area) with a checkbox
for "show plane grid". Not started.

## 6. Same Settings menu: color palette dropdown selector

Move/add the existing palette switching (currently a View-menu submenu, see `EffigyPalette` in
`EffigyWindow.cs`) into this new Settings tab, as a dropdown rather than a checkable submenu list.
**Must include one dark-mode option with good contrast** — `OnshapeDark` may already qualify, judge
against po's actual monitor once it's running, not by RGB values alone.

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

## 10. End-to-end confirmation: cube → sketch on its face → extrude from that face-sketch

This is the "get it on video" milestone po wants to demo. The KERNEL SIDE of this is done and
proven this session:
- `PrimitiveFeature` makes the cube.
- `SketchFeature.Face` (a `FaceRef`) lets a sketch attach to a body's face, resolved by geometry
  (body id + point + normal) rather than a fragile face index — see `CAD-REFERENCE.md` for why,
  including the design flaw the tests caught and the fix.
- `FaceSketchTests.TestBossOnTopOfBox` and `TestReferenceSurvivesUpstreamEdit` prove this builds
  correctly and that the sketch's plane follows the face if the body underneath changes.

**What's missing is 100% UI**: there is no way in the editor yet to click an existing body's face
and have that become a `SketchFeature.Face` reference. The plane-selector box exists
(`EffigyPlaneSelector`); a face-of-a-solid selector does not. This is the highest-value UI item on
this list because the kernel work behind it is already done and tested — it just needs a selection
box wired up the same way `EffigyRegionSelector`/`EffigyBodySelector` were built earlier (see
"Pick bodies in the viewport" commit) before being dropped in the merge for review-size reasons.
**Recommend building this next**, before items 1-3 and 5-9, since it's pure payoff on top of
already-tested kernel code.

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

Status: **not yet investigated in this session** — next step after writing this doc down. Need to
clone/read that repo's `Code/` directory (public GitHub, should be reachable the same way
Solvespace/FreeCAD were this session) and identify: what decal/paint mechanism it uses (render
target? vertex colors? projected decals?), how accurate its raycast-to-UV or raycast-to-triangle
mapping is, and what would need to change to (a) fix the aim-accuracy problem po described and
(b) lift it out of an in-game panel into an editor tool window. **This is significant enough new
scope that it may deserve its own doc once investigated — a Effigy-Paint sibling to
CAD-REFERENCE.md.**

---

## Priority order, as recommended by reading po's message

1. Write this doc (done, this commit).
2. Investigate wargame repo — cheap to do here (just reading), high excitement value, and needs to
   happen before context is lost.
3. Face-of-solid sketch selector (item 10) — kernel already proven, pure UI payoff, unblocks the
   demo po explicitly wants on video.
4. Fix sketch-toolbar-not-swapping (item 1) — likely a small, findable bug, and every other toolbar
   item (2, 3) is easier to do correctly once this is fixed rather than before.
5. Toolbar icons + sizing (items 2, 3) — bundle with item 4 since they touch the same files.
6. Settings tab: grid toggle + palette dropdown (items 5, 6).
7. Hide affordances for planes/origin, resizeable plane corners (items 7, 8) — more novel
   interaction work, higher risk, lower urgency than the demo path.
8. Face-hover-selects / right-click-edit-menu / tree-click-selects (item 9).
9. Bones design doc (item 12) — can start once 1-8 stop consuming all available time; genuinely
   independent of the rest.

Item 4 (screenshots) is not schedulable — it happens on po's machine, whenever they're at it next.
