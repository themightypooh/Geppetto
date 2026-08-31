# Brief: tell people exactly why a feature failed, and how to get what they wanted

**A temporary task doc**, same deal as `FBX-EXPORT-BRIEF.md`: the repo runs on four docs on purpose
(commit `1a2bd4f`). Fold the outcome into `WHAT-IS-BUILT.md` / `WHAT-IS-LEFT.md` and **delete this
file** when the work lands.

Read `HANDOFF.md` first, and run `./tools/test.sh` before touching anything (1555 checks, ~25s).

---

## 1. What po asked for, and why it is not a nice-to-have

> *"i hate how in onshape if i try to do something like a fillet or any feature and its not able to,
> it doesnt tell me exactly why/what's wrong. like if a fillet is too far or much or whatever it'll
> just show red and not work. i want effigy to give detail in what you're trying to do wrong, how to
> fix/get around it to continue to do what you were trying to do"*

Three requirements, and the third is the one that is easy to drop:

1. **What is wrong** — named, not "invalid input".
2. **Why**, with the actual numbers from this model.
3. **What to do about it** — concrete enough to act on without guessing.

This is the natural home for it. The repo's standing rule is already *"a mesh can be closed,
manifold, Euler-correct and valid while being visibly wrong"*, and every hard bug in its history
passed most of its checks. A diagnostics layer is that rule turned into a feature.

---

## 2. Effigy is currently WORSE than the Onshape behaviour po is complaining about

Onshape at least goes red. Measured on a 2×2×2 cube, `EdgeBlend.Fillet( cube, r, 15f, 4 )`:

| radius | volume (was 8) | faces | Euler | validator |
|---|---|---|---|---|
| 0.2 | 7.34 | 62 | 2 | valid, closed |
| 0.5 | 4.44 | 62 | 2 | valid, closed |
| 0.75 | 1.03 | 62 | 2 | valid, closed |
| 0.80 | 0.30 | 62 | 2 | valid, closed |
| **0.85** | **−0.43** | 62 | 2 | valid, closed |
| 1.5 | −7.55 | 62 | 2 | valid, closed |
| 3.0 | 44.53 | 62 | 2 | valid, closed |

**The solid turns inside out somewhere between r=0.80 and r=0.85, and nothing anywhere says a
word.** Same face count, same vertex count, same Euler characteristic, `valid, closed` at every
radius including 3.0. No error, no warning, no red. The feature reports success and hands the tree
below it an inverted body.

Note the practical limit (≈0.82) is well under the limit you would derive from setbacks alone
(s < 1, since the cube's edges are 2 long and each end eats s). The extra loss is the arcs from
**opposite** sides of the body meeting through the middle. That gap between the two numbers is why
section 4 prescribes two checks rather than one.

Reproduce with a scratch probe in `Effigy.Tests` — that table came from one, then it was deleted.

---

## 3. What exists today

| Piece | Where | State |
|---|---|---|
| `Feature.Error` (string) | [Feature.cs:230](Effigy/Features/Feature.cs:230) | set from `e.Message` in `Feature.Run`'s catch |
| `Feature.Warning` (string) | [Feature.cs:241](Effigy/Features/Feature.cs:241) | set by hand by a few features |
| Errors carried across rebuilds | [PartStudio.cs:215](Effigy/Features/PartStudio.cs:215) | correct — a reused feature's error is still an error |
| `RebuildReport.Errors/Warnings` | [PartStudio.cs:9](Effigy/Features/PartStudio.cs:9) | `List<(FeatureId, Message)>` |
| The entire UI | [EffigyFeatureDialog.cs:410](Editor/EffigyEditor/EffigyFeatureDialog.cs:410) | **one `Editor.Label`**, red or yellow, one string |

The Error/Warning split is already right and worth preserving: an error means there is no geometry
and the tree below stands on nothing; a warning means there IS geometry and you should look at it.
Collapsing the second into the first is a bug this repo already fixed once.

**The house style also already exists in places.** This is the bar — from `SketchFeatures.cs:257`:

> *"This cut does not reach into the part — it clears the part by 0.35 along Z, so there is nothing
> to take away. A profile drawn on a face extrudes into that face by default; check Flip direction,
> or increase the distance."*

Named problem, measured cause, two remedies. That message would satisfy po's request as-is. The
work is making every failure look like that one, structurally rather than by luck — most of the
other 40-odd throws are `"Direction cannot be zero"` and `"No bodies selected"`.

---

## 4. The design

### 4.1 A structured diagnostic in the kernel

New file `Effigy/FeatureDiagnostic.cs`:

```csharp
public enum DiagnosticSeverity { Warning, Error }

public sealed class FeatureDiagnostic
{
    public DiagnosticSeverity Severity;
    public string Problem;              // one line: what went wrong
    public string Cause;                // why, WITH THIS MODEL'S NUMBERS
    public List<string> Remedies = new(); // concrete actions, most likely first
    public string ParameterLabel;       // which control to highlight, or null
}

/// Thrown by a feature that knows exactly why it cannot proceed.
public sealed class FeatureException : Exception
{
    public readonly FeatureDiagnostic Diagnostic;
}
```

Structure rather than one long string, because the three parts need different treatment: the UI
styles them differently, `ParameterLabel` lets the dialog put the red ring on the control that is
actually wrong, and remedies want to be a list rather than prose glued together with semicolons.

On `Feature`, add `public FeatureDiagnostic Diagnostic { get; internal set; }` and set it in
`Run`'s catch — a `FeatureException` supplies its own; any other exception becomes a diagnostic
with `Problem = e.Message` and no cause or remedies, which is exactly today's behaviour and keeps
every existing throw working untouched.

**Keep `Error` and `Warning` as strings.** They are read from `PartStudio`, `RebuildReport`,
`EffigyCutDiagnostic`, the feature tree's paint code and several tests. Set them alongside the
diagnostic (`Error = diagnostic.Problem`) and nothing has to change at once.

Add protected helpers on `Feature` so a failure is one call and the shape is impossible to get
wrong:

```csharp
protected static void Fail( string problem, string cause, params string[] remedies );
protected void Warn( string problem, string cause, params string[] remedies );
```

### 4.2 Two tiers of check, because one cannot do it

Section 2 is the argument for this. A pre-flight check knows the numbers and can say *"the largest
radius that fits is 0.31"*; it cannot see two fillets colliding through the middle of a part. A
post-flight check catches anything at all but can only say *"that came out wrong"*.

**Pre-flight — cheap, exact, gives the number.** For `EdgeBlend` this falls out of machinery that
is already there: run passes 1 and 2 only (corner cuts and shrunk faces — no bridges, no caps) and
compare each shrunk face's signed area against the original's. A face that has collapsed or flipped
sign is one the setback was too big for, and it names the face and the edge.

To report *"largest radius that fits: X"*, bisect on the size with that same pass as the predicate —
about twelve iterations of a pass that does no allocation beyond a position list. Free at authoring
speed, and it turns a refusal into a number the user can type.

**Post-flight — catches the rest.** After the blend:

- **Signed volume must stay positive.** This is the check that catches the r=0.85 case in the
  table, where the pre-flight is still perfectly happy. Hard error.
- **Warn when the blend removed more than half the solid.** At r=0.8 the cube is down to 0.30 of
  its 8. Still positive, still legal, and almost never what anyone meant.

There is no `SignedVolume` in the kernel — but there are **eleven** private copies of `Volume()` in
the test project (`BranchTests`, `CoplanarMergeTests`, `DocumentTests`, `EdgeBlendTests`,
`FeatureTests`, `HoleTests`, `ShellTests`, `SketchTests`, `SweepLoftTests`, `TaperTests`,
`TerminationTests`), plus an inline one in `Program.TestWinding`. Promote it to
`PolyMesh.SignedVolume` as the first commit of this work and delete the eleven: a quantity the
kernel is about to refuse geometry over should not exist only in tests, and eleven copies is one
divergence away from two features disagreeing about which way is out.

### 4.3 The silent degradations that must start speaking

These are all in `EdgeBlend` and all currently invisible. Every one is a warning, not an error —
geometry came out, it is just not what was asked for:

| Where | What happens now | What it should say |
|---|---|---|
| `Setbacks`, `MaxSetback` clamp | shallow edge's setback silently clamped to 12× | "N edges are too shallow for this radius; their blend is narrower than asked" |
| `CutCorner`, `MaxCornerOffset` clamp | corner snapped to a fallback point | "N corners were too shallow to cut cleanly and were squared off" |
| `Arc` centre disagreement | that edge silently falls back to a **flat** quad | "N edges could not be rounded and were chamfered instead" |
| `WalkFacesAroundVertex` returns null | vertex silently left uncapped | "N corners sit on a boundary or non-manifold vertex and were left sharp" |
| `SelectEdges` selects nothing | whole feature is a no-op, reports success | error: "No edge on this body is sharper than 15°; the sharpest is 4.2°" |
| `size <= 0` | returns a clone, reports success | already guarded by the param's min, but say so |

That last one is worth calling out separately: **a feature that did nothing must never report
success.** It is the single most confusing state in a parametric tree, and it is what po means by
"it just doesn't work".

---

## 5. Order of work

**1. The mechanism** (§4.1) plus the dialog (§6). Nothing is visible until the UI can show three
parts, so build the pipe first and prove it with one hand-written diagnostic.

**2. Fillet and Chamfer** (§4.2, §4.3) — the case po named, the worst offender by the table in §2,
and the one that exercises pre-flight, post-flight and warnings all three. Do this second and the
pattern for everything else is established by example.

**3. The boolean.** `MeshBoolean.Apply` already has the best refusals in the repo (see its
`Name(op)` messages) but the engine's own failure comes back as *"the engine's boolean rejected
these two solids"*, which is a dead end. `EffigyBooleanProbe` and `effigy_dump_tree` know more —
whether the two bodies' bounds even overlap, whether either is open — and that belongs in the
message.

**4. The sweep of the other 40 throws.** `grep -n "throw new InvalidOperationException"
Effigy/Features/*.cs`. Most are one line and say nothing. Rank them by how likely someone is to hit
one; `"No bodies selected"` on every one of Mirror/Pattern/Transform is the most common and the
easiest to improve ("This studio has no bodies yet — add a Primitive or extrude a sketch first").

**5. Shell and Draft.** `ShellOperation` refuses openings that meet at a vertex and self-intersecting
offsets; both currently refuse quietly-ish. Same treatment.

---

## 6. The UI

`EffigyFeatureDialog` builds one `Editor.Label` at [line 171](Editor/EffigyEditor/EffigyFeatureDialog.cs:171)
and sets its text and colour at [line 410](Editor/EffigyEditor/EffigyFeatureDialog.cs:410). That is
the whole thing. It needs to become a small panel:

- **Problem** in the severity colour (`Theme.Red` / `Theme.Yellow`), bold.
- **Cause** underneath in `Theme.TextLight`, wrapped — this is where the numbers are, so it must
  not be truncated to one line the way a `Label` in a horizontal layout will be.
- **Remedies** as a short bulleted list. If a remedy is mechanical ("reduce radius to 0.31"), make
  it a button that does it. That is the difference between telling someone the answer and handing
  it to them, and it is the part of po's request that says *"to continue to do what you were trying
  to do"*.
- Highlight the control named by `ParameterLabel`.

The feature tree turns a row's icon red on error and does nothing else —
[EffigyWindow.cs:3093](Editor/EffigyEditor/EffigyWindow.cs:3093), `FeatureNode.OnPaint`. **That red
icon is the exact behaviour po is complaining about in Onshape**, so it is worth fixing in the same
pass: give the row a tooltip carrying the problem line, so a broken feature is readable without
opening it. Warnings get no indicator at all today and should get a yellow one.

**Do not guess the widget API.** `HANDOFF.md`'s first rule applies with force here — read the
shipped Base Editor Library under
`C:\Program Files (x86)\Steam\steamapps\common\sbox\addons\tools\Code`. Nearly every bug in this
project's history came from inferring a `Editor.*` API from its parameter names. `Editor.Label`
wrapping behaviour in particular is worth reading rather than assuming.

---

## 7. How to test it

The kernel half is fully headless, so most of this is ordinary suite work:

- **Every diagnostic a feature can produce gets a test that triggers it** and asserts on the
  structure, not the prose — severity, that `Cause` contains a number, that `Remedies` is
  non-empty. Asserting exact strings makes the messages unmaintainable.
- **The oversized fillet gets the table from §2 as a test.** `Fillet(cube, 0.85f, 15f, 4)` must now
  be an error; `Fillet(cube, 0.2f, 15f, 4)` must not. That is the regression this whole brief
  exists to prevent, and it currently passes silently.
- **A no-op must be an error.** `Fillet(cube, 0.1f, 179f, 4)` selects no edges;
  `TestUnreachableThresholdIsANoOp` in `EdgeBlendTests` currently asserts it comes back unchanged.
  That test's expectation changes — the geometry stays unchanged, and the feature now says so.
- **The reflection sweep already exists and is the right hook.** `AllFeaturesTests.TestEmptyStudioErrors`
  ([line 249](Effigy.Tests/AllFeaturesTests.cs:249)) walks every `Feature` subclass, adds it to an
  empty studio, and asserts the resulting error is longer than 15 characters and is not
  `"Object reference not set"`. That is a low bar deliberately set low. Raise it as the work lands:
  the diagnostic must have a `Cause`, and `Remedies` must be non-empty. Every new feature is then
  held to it for free, which is the only way a rule like this survives contact with the next
  twenty features.

---

## 8. The rule to write into the docs when this lands

Worth stating in `WHAT-IS-BUILT.md` as a standing rule beside the existing one about meshes:

> **A feature that cannot do what was asked says what it was asked, what stopped it, with this
> model's numbers, and what would work instead. A feature that did nothing is never a success.**
