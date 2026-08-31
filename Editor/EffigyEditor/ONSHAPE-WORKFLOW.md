# Onshape's workflow, and what Effigy does about it

Notes taken from Onshape's own documentation, and the record of which parts of its workflow this
editor now implements. Written because the previous pass built ~3,400 lines of editor against an
*impression* of Onshape rather than against its docs, and several of the resulting doc comments
described behaviour that was never actually wired up.

> **Status after the main merge.** This document was written against a branch that was later
> reconciled with `main`, which had independently built its own tool strip (`EffigyToolStrip`,
> custom-drawn icons), committed-sketch display, and sketch selection for Extrude — and, unlike
> that branch, had actually been compiled and run. Where the two overlapped, main's version won.
>
> Still in: numeric fields and the expression evaluator, the parameter-edit fix, value-snapshot
> undo, screen-space snapping, the degree/plane tests, `RegionSeed` in the kernel.
>
> **Deferred, not delivered:** the rollback bar UI, the face-pull gizmo, region and body selection
> boxes, the parts list, the feature context menu, and the sketch tools moving into the floating
> strip. They exist in this branch's history (`7fcb45f`, `c67e544`) and are meant to be re-applied
> onto the merged base deliberately, one at a time. Sections below that describe them describe
> INTENT, not current behaviour.
>
> **Since delivered:** the rollback bar, the parts list, the feature context menu, the sketch tools
> in the floating strip, and the body selection box. Still outstanding from that list: the face-pull
> gizmo and the region selection box.
>
> **Still unbuilt, and worth naming because the solver changed what is possible:** there is no way to
> ADD a constraint in the editor. `SketchSolver` is in the kernel and runs on every rebuild, but the
> only constraints reaching it are the ones inference records while drawing. A constraint tool and a
> dimension tool are what make the solver reachable by a user.

**Sources.** Onshape help — Feature Basics, Sketch Basics, Feature and Part Lists, Numeric Fields,
Dialogs, Keyboard Shortcuts and Hotkeys, View Navigation and the View Cube. Fetched via search
summaries rather than directly: the environment this was written in blocks outbound HTTP to
`cad.onshape.com`. Anything below stated as Onshape behaviour came from those docs; anything
stated as a design decision for Effigy is marked as such.

---

## 1. The Part Studio loop

Onshape: a Part Studio holds an ordered **Feature list** — the parametric history — plus three
default planes (Top, Front, Right) and an Origin. You sketch on a plane, then apply features to
turn sketches into solids. The list also carries a **Parts list** at the bottom.

Effigy: matches. `PartStudio` is the feature list, `EffigyFeatureTreePanel` draws it with the
Default geometry node, and the Parts list is now populated from `_studio.Bodies` instead of being
a permanently blank strip.

## 2. The feature dialog

Onshape:

- A dialog, not a property sheet. Editable name, a **checkmark** to accept, an **x** to reject.
- *"The title is red if you have not completely filled out the dialog, or if the information
  entered has resulted in an error. This prevents you from committing a broken feature."*
- **Enter accepts, Escape cancels, Shift+Enter accepts and reopens the dialog** for another one.
- Live preview while you fill it in.

Effigy now: red spine plus a status line naming the reason, the tick **disabled** while the feature
is broken, `Accept()` refusing to commit, and Enter / Escape / Shift+Enter wired.
`EffigyFeatureDialog.IsBroken` is the single predicate — `Feature.Error` from the kernel, or a
Sketch whose plane has not been chosen.

Before this, the dialog's own class comment claimed it "goes red the moment the feature will not
build". `OnPaint` drew a background rectangle and one divider line. Nothing was red, and Accept
committed regardless.

## 3. Numeric fields — the big one

Onshape: numeric fields accept *"integers, decimals, parameter expressions, and trigonometric
functions"*. Operators `^ * / + -`; functions `ceil, floor, round, exp, sqrt, abs, max, min, log`.
Units convert into the field's default unit. After a field is accepted the **evaluated result** is
shown; when it is active again the **original expression** comes back.

Effigy before: every dimension was a `FloatSlider`. You could not type `4`. Most `FloatParam`s
declare `min 0.0001` and *no maximum*, so the dialog invented a `-9999..9999` range at `0.1` per
step — about a hundred thousand steps of travel to hit a value with.

Effigy now: `EffigyNumericField` + `EffigyExpression`, a hand-written recursive-descent evaluator
(no dependency — the kernel's whole premise is that it has none).

- `1/8` → `0.125`, `sqrt(2)*10`, `2^-1`, `max(3,7)`, `pi`, `1e3` all evaluate.
- Precedence follows every calculator: `-2^2` is `-4`, `2^3^2` is `512`, `2^-1` is `0.5`.
- Trig is in **degrees** — `sin(30)` is `0.5`. Onshape's trig takes a unit-carrying angle and
  cannot be ambiguous; with no unit system here, degrees is what a CAD field means.
- **Lengths are dimensionless** and reject unit suffixes. `5mm` fails rather than silently storing
  5, because the kernel has no millimetre. Angle fields (`unit: "deg"`) accept `deg`, `rad`, `°`.
- **No implicit multiplication.** `2pi` is rejected as a typo rather than read as `2*pi`.
- A slider is shown *alongside* the field only when the parameter declares finite bounds within
  1024 — Bevel's `0..180` threshold, Subdivide's `0..6` levels. Unbounded lengths get the field
  alone, which is what Onshape shows.

**Divergence, deliberate:** Onshape swaps the text between expression and result on focus change.
That needs `LineEdit` focus events, which are not proven against this editor's API, so the
evaluated result is shown continuously in a label beside the field instead — `1/8` sits there
reading `= 0.125`.

The grammar was verified before commit by transliterating it to Python and running 43 cases,
including every input that must be *rejected*. That is the only part of this change that could be
verified at all; see "Verification" below.

## 4. Sketching

Onshape:

- Pick a plane; the sketch dialog opens and the toolbar swaps to sketch tools.
- **Onshape does NOT rotate the view when you pick a plane.** Automating that is a standing
  request on their forum, not shipped behaviour. `N` looks normal to the plane; pressing `N` again
  flips to the inverse-normal view.
- Documented sketch keys: `l` line, `c` circle, `q` construction, `n` normal-to, `d` dimension,
  `u` use/convert, Escape to drop the tool.
- Closed regions **shade**; unshaded means the profile is open and will not extrude.
- Automatic inferencing "wakes up" as you approach existing geometry — horizontal, vertical,
  midpoint, parallel, coincident.

Effigy now: `N` / `L` / `C` / `Q` wired as window shortcuts. `ViewNormalToSketchPlane` flips on the
second press. Region shading and endpoint-degree diagnostics were already there and are good.
Inferencing covers horizontal/vertical against existing points and the active line.

`LookAtSketchPlane()` previously existed, fully written, and was **called from nowhere** — while
`EnterSketch`'s comment claimed it pointed the camera at the plane. It is now
`ViewNormalToSketchPlane`, bound to `N`, and deliberately *not* called on sketch entry, per the
Onshape behaviour above.

Spline, trim, extend and offset **have a kernel behind them now** — `SketchSolver`'s neighbours in
`Effigy/Sketch/SketchEdit.cs`, plus `SketchSpline` and `SketchEllipse` — and the buttons are still
absent. They were absent-rather-than-dead when there was nothing underneath them; now they are the
highest-value sketch tools to build, because the hard half of each is done and tested:

- **Trim** wants a click on the piece to remove; the kernel takes the curve and a pick point.
- **Extend** wants a click on the end to stretch; the kernel takes the curve and which end.
- **Offset** wants a chain and a distance, and reports corners it could not close.
- **Fillet** wants the corner point and a radius. It refuses a radius too big for its arms rather
  than clamping, so the editor has a message to show rather than a silently different result.
- **Spline** wants click-to-place points, and **ellipse** a centre, a major-axis point and a minor
  radius — both are ordinary sketch points, so dragging and dimensioning them already works.

Dimensions and constraints are no longer the gap they were: the solver has **sixteen** constraint
kinds, and `ConstraintTools` plus the right-click menu over a sketch selection is built.

But the gap there is wider than "a menu entry". `ConstraintTools` — the KERNEL side, the thing that
turns a selection into the constraints it allows — still only offers the original eleven. Tangent,
arc-to-arc tangent, diameter, midpoint, concentric and fix are solvable and tested but unreachable
from any selection, so wiring them is kernel work before it is editor work, and it is small: one
case each in `ConstraintTools`, then one menu entry each.

Sweep and loft also have no toolbar entry and no way to pick a second sketch, which is the one piece
of UI they need that nothing else in the tool has: a sketch PICKER, not just "the most recent one".

## 5. The rollback bar

Onshape: right-click a feature → **Roll to here**; right-click the bar → **Roll to end**. Evaluate
only the features above the bar, so you can go back and work on the model as it was at that point.

Effigy: **not wired yet** (see the status note at the top). The kernel has always supported it —
`PartStudio.RollbackIndex`, with the snapshot cache behind it, and a class comment calling rollback
one of the *"two things that make this parametric rather than a pile of bakes"*. The editor
referenced it exactly zero times. Rolled-back features draw dimmed with the bar ruled above the
first of them.

**Divergence:** Onshape's bar is dragged between rows. A `TreeView` gives no row to drag a bar
into, so the bar is moved by the context-menu command and by the Edit menu, and the panel shows a
"Rolled back — N of M features active" readout.

## 6. Feature list interaction

Onshape: right-click gives rename, suppress, roll-to-here, delete; features are reorderable; the
Parts list sits below with per-part hide/show and rename.

Effigy now: right-click on the panel opens that menu. **Caveat:** the handler is on the panel
widget, not on the tree rows, because `TreeNode` has no context-menu hook proven against this
editor's API — so you select a feature first, then right-click. Every command is also in the Edit
menu.

Per-part hide/show is **not** implemented rather than stubbed: the viewport previews one merged
mesh (`PartStudio.ToMesh`), so there is nothing per-body to hide yet.

## 7. Toolbars

Onshape: tools sit directly above the graphics area, grouped, with a chevron on any button that
has variants under it.

Effigy before: `Editor.ToolBar` docked to the top of the **window**, next to File/Edit/View, one
continuous strip of twelve undifferentiated glyphs — window furniture rather than tools. The sketch
row was a *second* strip below it spending a top-level button on every single variant, fourteen
wide.

Effigy now: main's `EffigyToolStrip` — a strip of squares FLOATING on the 3D canvas at its top-left,
with every icon hand-drawn in `EffigyIcons` rather than looked up by name. That last part matters:
s&box ships classic `MaterialIcons-Regular.ttf`, so a Material Symbols name renders as nothing at
all, and drawing them removes the guesswork permanently.

The sketch tools are still an `Editor.ToolBar` docked to the window, so they sit alongside the
feature strip rather than replacing it. Moving them into the floating strip needs about ten more
drawn icons and is deferred.

## 8. Views

Onshape: the view cube in the top-right corner opens a list — Top, Bottom, Front, Back, Left,
Right, Isometric, Dimetric, Trimetric.

Effigy: the corner indicator is a **text label, not clickable**. The seven orientations are in the
View menu instead. Making the cube itself clickable is still open.

---

## Verification status — read this before trusting any of it

**None of the editor code in this change has been compiled.** There is no .NET SDK and no s&box in
the environment it was written in, and outbound network is blocked. First compile will be on a
machine that has the engine.

What *was* done to keep the risk down, in the spirit of the root `HANDOFF.md` ("read the shipped
source before writing against any API"):

- Every editor API call was checked against `Editor/RigControlEditor/`, which is verified working.
  `EffigyToolButton` is modelled directly on `RigIconButton` — `FixedWidth`/`FixedHeight`,
  `MouseTracking` + `IsUnderMouse`, `Paint.Antialiasing`, `e.Accepted`.
- `Theme.TextLight` was replaced throughout with `Theme.TextControl.WithAlpha(...)`. `TextLight`
  appears nowhere in the proven corpus; `TextControl` is its most-used member.
- **Icon names were audited against classic Material Icons.** `RigIconButton`'s own class comment
  records that s&box ships `MaterialIcons-Regular.ttf`, *not* Material Symbols, and that a Symbols
  name "silently renders as nothing". `square`, `circle`, `hexagon`, `pentagon`, `rule`,
  `restart_alt`, `download`, `arrow_up` and `arrow_down` were all Symbols-only or wrong and have
  been swapped for classic equivalents.

### Known-unverified API, in one list

| Symbol | Where | Note |
|---|---|---|
| `KeyCode.Enter` | `EffigyFeatureDialog.OnKeyPress` | added here; one-word fix if named differently |
| `KeyCode.Shift` | `EffigyFeatureDialog.OnKeyPress` | added here; only Shift+Enter depends on it |
| `Gizmo.CurrentRay` | `EffigyViewport.Sketching.CursorToPlane` | **pre-existing**; the whole sketcher rests on it |
| `Gizmo.Draw.LineThickness` | sketch + plane rendering | pre-existing |
| `Gizmo.Draw.SolidTriangle` | region shading, plane highlight | pre-existing |
| `Gizmo.Draw.WorldText` | axis labels | pre-existing |

`TreeNode` context menus and `LineEdit` focus events were both wanted and both avoided for the same
reason — no proven usage to copy. Each is noted at its call site.

---

## The two kernel copies

`Effigy/` is canonical. `Editor/Effigy/` is a **mirror** and must never be hand-edited.

s&box compiles `Code/` into the game assembly and `Editor/` into the editor assembly, and nothing
else — a top-level `Effigy/` is invisible to it. The kernel cannot live in `Code/` either, because
`ObjWriter` and `SmdWriter` call `File.WriteAllText` and the game assembly's sandbox whitelist does
not allow it. Hence the mirror.

Run `tools/sync-kernel.sh` after any kernel edit. `KernelSyncTests` fails the test run when the two
diverge — the mirror was committed with a stray blank line in `SolidFeatures.cs`, a diff of no
consequence and proof that nothing was checking.
