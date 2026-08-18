# ModelKernel

The engine-free half of the modelling tool. Parametric primitives in, subdivision surfaces and an
OBJ out, with no reference to any engine type anywhere in it.

See `../MODELING-HANDOFF.md` for why the tool exists and `../MODELING-HANDOFF-GODOT.md` for the
engine question this folder is designed to keep open.

## Why it's engine-free

The same source is meant to compile under s&box, Godot's C#, or a bare console runner. That is not
tidiness — it is the thing that keeps the engine decision reversible while it is still undecided.
The kernel has its own `Vec3`/`Vec2` for exactly this reason; engine glue converts at the boundary,
which is a handful of lines paid once per engine.

The test project compiles the kernel **from source** rather than referencing a built library, so if
anything in here ever picks up a dependency, the build breaks and says so.

## Running the tests

```
cd ModelKernel.Tests
dotnet run -- out
```

114 checks, and it writes sample `.obj` files to `out/` — one per primitive plus a
2-level-subdivided version of each. Those are the fastest way to see whether something is actually
right: open them in Blender, or drop one into ModelDoc to find out what s&box makes of it.

Exit code is non-zero on failure, so it works as a pre-commit or CI check unchanged.

## What's here

| File | |
|---|---|
| `Vec.cs` | `Vec3`, `Vec2`. Deliberately not the engine's |
| `PolyMesh.cs` | n-gon mesh, adjacency, validation, Euler characteristic |
| `Primitives.cs` | box, plane, cylinder, quad sphere, wedge, tube — all quad-dominant |
| `CatmullClark.cs` | subdivision, boundary rules, cost prediction |
| `ObjWriter.cs` | OBJ export with angle-thresholded normals, plus a reader for round-trip tests |

## Two decisions worth knowing before changing anything

**Quads are a requirement, not a preference.** Catmull-Clark turns clean quads into a clean surface
and triangle soup into a lumpy one, and triangles leave valence-3 vertices that stay extraordinary
at every level and pucker under a sculpt brush. That is why there is no UV sphere here — its pole
fans are triangles. `QuadSphere` costs nothing extra and has no poles.

**UVs are stored per face corner, not per vertex.** A box corner belongs to three faces that each
want a different UV for the same position; per-vertex UVs would force one value and smear the
texture across every seam. It also makes UV subdivision purely local — a face's new UVs are computed
from that face's own corners and nothing else — so seams survive subdivision for free. There is a
test for this.

## What's verified

Not "it renders, looks fine". Subdivision is the classic looks-right-is-wrong case: a vertex rule
that is subtly off still produces a smooth plausible blob, it just converges to the wrong surface.
So the tests check things that fail loudly instead:

- Euler characteristic, before and after subdivision, including the tube at genus 1
- the exact growth laws — `V' = V+E+F`, `F' = total corners`, `E' = 2E + corners`
- winding, via the divergence theorem: enclosed volume must come out positive, and a 2×2×2 box must
  come out at exactly 8
- successive subdivision levels must move points *less* each time, not drift
- an open mesh keeps its boundary, stays planar, and keeps its corners
- a box exports with exactly 6 hard normals; a 16-segment cylinder with at least 16

## Not here yet

Bevel, mirror, array, shell, boolean subtract, planar UV projection, and the whole phase-two sculpt
side — brushes, multires deltas, normal-map bake. See the open questions in the handoff docs; two of
them gate how this connects to an actual editor.
