# Paint: what is left to do

Repo: **Geppetto** (`C:/Users/pooh/Documents/s&box projects/geppetto`) — an s&box editor package
containing **Effigy** (parametric CAD modeller) and **Marionette** (rig animator).

## Before you start

**The paint rework is in the working tree and is not committed.** `PAINT-TEXTURE-TASK.md` has
landed — paint is a texture atlas, the coverage buffer stops a held brush darkening, entering Paint
auto-inserts a UV Project, and a painted part authors its own PNG + `.vmat` on compile. Everything
below is written against the working tree, not against HEAD. Read `PAINT-TEXTURE-TASK.md` first for
what was replaced and why; the "do not reintroduce these" list in it still binds.

These are independent of each other unless a task says otherwise. Do them one at a time, one commit
each. Repo conventions from `SCULPT-POLISH-TASK.md` apply to all of them: kernel mirroring between
`Effigy/` and `Editor/Effigy/` via `tools/sync-kernel.sh`, `sh tools/test.sh` ending `0 failed`,
prose comments that say *why*, no `TODO`s, CHANGELOG entries under the existing headings, and
`s&amp;box` in XML doc comments.

The editor's MCP bridge is at `http://127.0.0.1:7269/mcp` (see `.mcp.json`). Start with
`editor_status` and confirm it says `Project: geppetto` — there is more than one s&box editor on
this machine and the bridge belongs to whichever bound the port.

**The order below is roughly by value.** 1 and 2 are what a user hits in the first five minutes.

1. An eraser
2. What the unpainted part of the atlas exports as — and the Blend choice, which is the same question
3. Compile binds the atlas to slot 0 blindly
4. Paint one body at a time
5. The paint bar is missing Falloff, and has no eyedropper
6. The canvas resolution is a `const`
7. Retire the vertex-colour fallback

---

## 1. An eraser

**You can paint and you cannot unpaint.** There is no erase anywhere in the paint tool — no mode on
the bar, no modifier, nothing in `PaintSession`. The only way to remove a mark is Ctrl+Z, which
takes the whole stroke and every stroke after it. Painting over with another colour is not erasing:
the atlas covers, so there is no "back to how it was", only "now it is white instead".

Grease-pencil notes already have this (`NoteSession.Erase`) and the shape of the answer is there.

**Where it goes.** An erase is a stroke, not a canvas edit — the same rule the whole feature runs
on. `PaintStroke` replays into `PaintCanvas` on every rebuild, so an erase that scrubbed the canvas
directly would be undone by the next rebuild, silently. Give the stroke an erase flag and have
`PaintReplay.Composite` subtract coverage from the alpha rather than compositing colour into it, so
strokes and erases replay in the order they were made and land the same way every time.

**The file format.** `PaintStroke` serialises through `StudioDocument`. Adding a flag is a format
change — a document written by a build that has it must still open in one that does not, and the
absence of the flag must read as "not an erase". Follow the "null until first use" idiom `Strokes`
already follows, so a document with no erases serialises exactly as it does today.

**Do not** add an erase that only works within the live session. A stroke you cannot save is a
stroke that vanishes when the document is reopened, and paint that comes back after you erased it
is worse than no eraser at all.

Acceptance: paint a mark, erase part of it, and the erased part shows the surface underneath.
Save → close → reopen and the erase is still there. Edit a feature below the Paint feature and the
erase survives the rebuild. Each erase stroke is one Ctrl+Z.

---

## 2. What the unpainted part of the atlas exports as — and the Blend choice

These are one question, so they are one task.

**What happens now.** `PaintMaterial.OpaqueRgba( canvas )` composites the canvas over a base colour
whose default is white, and `EffigyWindow.AuthorPaint` calls it with that default. So every texel of
the painted slot that was never touched by a brush exports as **white**. Paint one dot on a plain
part and the compiled model is a white part with a dot on it — which is not what the viewport showed
while painting, because the live preview draws the canvas over the preview material.

**And `PaintFeature.Blend` is carried but never read.** Its own comment says why: telling tint from
replace through a texture needs a shader that combines the base material with the atlas, and nothing
shipped provides one. So the bar still shows a Blend combo that changes nothing, and the CHANGELOG
already admits the distinction "is no longer drawn". A control that does nothing is a bug report
waiting to be filed.

**Pick one and commit to it.** Both are defensible; what is not defensible is the current state,
where the control implies a choice the export does not make.

- **Replace only.** Accept that paint covers, take the Blend combo off the paint bar (keep the
  param on the feature so old documents still load — that is what it is already for), and say so
  in the prompt or the panel: painting a body takes its surface. Then fix the base colour, because
  white is a guess — sample it from whatever the slot was bound to, or from the preview material,
  so an unpainted texel exports as what the part already looked like.
- **Make tint real.** Write the atlas with its alpha intact and a material that layers it over the
  base, so unpainted texels show the material underneath and Blend means something again. This is
  the bigger job and it is a shader question: find out what `complex.vfx` can be given before
  designing around it, and write down what you found rather than assuming.

**Do not** leave a dead control on the bar. Whichever you choose, the UI has to agree with the
export by the end of this task.

Acceptance: paint one small mark on a part with a material on it, compile, and the result is
explicable without reading the source. The Blend control either does what it says or is gone. Tests
cover whatever the base colour rule turns out to be.

---

## 3. Compile binds the atlas to slot 0 blindly

`EffigyWindow.AuthorPaint` ends with `_studio.MaterialNames[0] = relVmat;` — literal zero,
unconditional, without looking at which slots the painted body's faces are actually on.

Slot 0 is where faces start, so this is usually right and that is why it has not bitten. It is wrong
in two ways that are ordinary to reach:

- **Every face carries a dropped material.** `MaterialDrop.ReleaseVacatedSlot` retires a slot
  nothing wears any more, so slot 0 can end up owned by no face at all. The atlas is then written,
  named, and bound to a slot nothing samples — the PNG and the `.vmat` land on disk and the compiled
  model shows no paint, with nothing said.
- **Something is already bound to slot 0.** The face menu can put a material on the slot a face is
  already on. That name is overwritten here, silently, on compile.

Bind the atlas to the slot (or slots) the painted body's faces actually use, and do not overwrite a
name the user chose — the comment above `AuthorPaint` already claims "paint takes the slot that had
nothing", so make the code say what the comment says. If the painted faces span several slots,
decide whether that is one atlas bound to each or a refusal, and write down which and why.

**Do not break** `VmdlMaterials`: every material slot the mesh uses must name a real asset or the
compiled model renders in the bright red missing-material shader, and paths are stored as **source**
paths (`.vmat`, never `.vmat_c`). `VmdlMaterialsTests` covers both.

Acceptance: a headless test paints a body whose faces are not on slot 0 and asserts the atlas is
bound where the faces are. A material the user bound by hand survives a compile of a painted part.

---

## 4. Paint one body at a time

`PaintFeature.Execute` only replays when `targets.Count == 1`, and its comment says so plainly:
one stroke list, one atlas. That is a real design decision and the reasons hold. What follows from
it is not obvious to a user, and two of the consequences are silent:

- `MeshTransform` merges a painted body into an unpainted one by keeping the target's atlas and
  adopting the source's only when it has none — so **a second painted body's atlas is dropped on
  merge**, without a word.
- `AuthorPaint` writes one `{name}_paint.png` per compile, so two painted bodies could not both
  export anyway.

A part with two bodies is completely ordinary. Either make it work — an atlas per painted body,
each bound to its own slot, which is most of the work in tasks 2 and 3 done properly — or make the
refusal loud: say at the door that paint is one body, and say on merge or on compile when a second
body's paint is being dropped rather than dropping it quietly.

**Read `NEXT-TASKS.md` task 1b before starting.** The material brush refuses multi-body documents
for a different reason (one BVH over one mesh) and the fix there is a session per body. If both are
being done, do them together and share the raycast-the-nearest-hit approach rather than writing it
twice.

Acceptance: paint two bodies. Either both survive save, rebuild and compile with their own atlas
and slot, or the tool said clearly what it was going to do before it did it. Tests either way.

---

## 5. The paint bar is missing Falloff, and has no eyedropper

Two small ones on `EffigyPaintBar.cs`. Together they are an afternoon.

**a. Falloff.** `PaintSession.Falloff` exists, is read by `PaintReplay.StampDab`, and defaults to
`BrushFalloff.Smooth` — and there is no way to change it, because the paint bar carries a swatch,
Radius, Strength and Blend and nothing else. The sculpt bar got a Falloff dropdown this release
(Smooth / Sharp / Linear / Constant) with the reasoning already written down. Put the same control
on the paint bar. A hard-edged paint mark is not reachable today and the kernel has supported it the
whole time.

**b. Alt-click to sample the colour under the cursor.** Every paint tool has an eyedropper. Read the
canvas texel the ray hit and load it into the swatch. The material brush has the identical gap and
it is `NEXT-TASKS.md` task 1a — same gesture, same modifier, so decide the interaction once and make
both tools use it, whichever is done first.

Acceptance: a sharp-falloff paint stroke has a hard edge and a smooth one does not, visibly and in a
test of the coverage buffer. Alt-click on a painted texel loads that colour; alt-click on an
unpainted one says so rather than loading white.

---

## 6. The canvas resolution is a `const`

`PaintFeature.Resolution` is `public const int 1024`. That is the right default and the wrong only
option: 1024 texels across a matchbox is enormous and across a 4000-unit part is four texels per
inch. Nothing in Settings mentions paint at all.

Make it a parameter on the feature rather than a global setting — it belongs to the document, the
same way the sculpt cage's level does, and a part with one small painted detail and one big painted
wall wants two answers.

**The traps, both already handled elsewhere and both easy to break here:**

- The replay cache is keyed on the topology id and `AtlasId` (see `PaintFeature`), not on the
  resolution. Change the resolution and the cache must miss, or the model keeps serving the old
  canvas at the old size.
- `PaintSession` is constructed with a resolution and `EffigyViewport.Painting` creates its texture
  at `res`. Changing the number mid-session means both have to be rebuilt, not just the canvas.
- Strokes are resolution-independent by construction — they are points and radii, replayed. So a
  resolution change must re-replay and lose nothing. There is a test to write here.

Acceptance: change a Paint feature's resolution, and the paint is the same paint at the new
resolution — not scaled, not cleared, not stale. Save and reopen keeps the setting.

---

## 7. Retire the vertex-colour fallback

The CHANGELOG says "the vertex-colour workaround (`vertex_color.vmat`) is gone". It is not:

- `VmdlMaterials.PaintedMaterial` is still `materials/default/vertex_color.vmat`, and
  `FallbackFor` still returns it for a mesh with `HasVertexColors`.
- `EffigyPreview` still branches on `mesh.HasVertexColors` to pick `PaintedPreviewMaterial`.

Nothing writes `VertexColors` any more except `MeshTransform`'s merge padding (which only preserves
what it is given) and two tests that set the array by hand. So both branches are unreachable through
the tool, and they are the exact code path the rework was meant to delete.

Delete it, or — if `VertexColors` is being kept on `PolyMesh` for the exporters on purpose — say so
in the comments and make the CHANGELOG line true instead. `VmdlMaterialsTests` asserts the old
behaviour in three places and will need to move with it; do not weaken those tests into passing,
change what they assert.

Acceptance: a grep for `vertex_color` returns either nothing or something a comment justifies. The
CHANGELOG line and the code agree. `sh tools/test.sh` ends `0 failed`.

---

## Already specced elsewhere — do not rewrite these

- **`STICKER-TASK.md` is unblocked.** It was gated on "painting a plain box puts colour under the
  cursor at brush resolution", which is now true. A sticker is a dab whose brush is an image, and it
  composites into the same canvas. It is the biggest single thing paint could gain next; it is also
  the largest, so it is not in the numbered list above.
- **`NEXT-TASKS.md` task 2 (show the UV layout)** matters more now than when it was written. A bad
  unwrap is something the user now sees smeared across their model, and entering Paint inserts an
  unwrap automatically, so the checker overlay is the only way to find out what it did.
- **`NEXT-TASKS.md` task 3 (the tutorial's paint step)** is unblocked for the same reason it was
  gated: painting a plain box works without extra setup now.
