using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// One point on a paint stroke's path: where the brush touched, and which way the surface faced
/// there.
///
/// WHY THE NORMAL IS STORED BESIDE THE POSITION. A paint dab is a sphere in object space, and a
/// sphere at a point on a thin wall reaches both sides of the wall — paint it from one side and the
/// far face gets painted too, which reads as paint on the wrong surface rather than as thin
/// geometry. Recording the normal lets the replay reject faces that point away from the brush, the
/// same guard NormalBake gives the bake with its MaxDistance, and the same failure when it is
/// skipped.
/// </summary>
public readonly struct PaintStrokePoint
{
	public readonly Vec3 Position;
	public readonly Vec3 Normal;

	public PaintStrokePoint( Vec3 position, Vec3 normal )
	{
		Position = position;
		Normal = normal;
	}
}

/// <summary>
/// One stroke of paint, stored as a PATH rather than as a list of dabs.
///
/// WHY A PATH. A dab list at ten floats a dab grows without bound and bloats the hand-written text
/// format; a stroke stores its settings once and a handful of path points, then regenerates its dabs
/// deterministically along the path on replay, by the same spacing rule SculptSession already uses
/// to fill in a fast drag. That smallness is what lets paint live in the .effigy text beside the
/// feature tree instead of in a binary side-car — and what makes it diff, which for somebody's model
/// matters more than it sounds.
/// </summary>
public sealed class PaintStroke
{
	/// <summary>Brush colour, straight RGBA in 0..1.</summary>
	public float R = 1f;
	public float G = 1f;
	public float B = 1f;
	public float A = 1f;

	/// <summary>Brush radius, in world units.</summary>
	public float Radius = 0.1f;

	/// <summary>How hard the stroke presses, 0..1.</summary>
	public float Strength = 1f;

	/// <summary>Reuses <see cref="BrushFalloff"/> — Smooth / Linear / Sharp / Constant. Deliberately
	/// not a second enum: the replay weights dabs through <see cref="Brush.Falloff"/> and two copies
	/// of the same list is the thing that drifts.</summary>
	public BrushFalloff Falloff = BrushFalloff.Smooth;

	/// <summary>Distance between dabs along the path, as a fraction of <see cref="Radius"/>.</summary>
	public float Spacing = 0.5f;

	/// <summary>
	/// Whether this stroke TAKES paint away rather than laying it down.
	///
	/// AN ERASE IS A STROKE, NOT AN EDIT TO THE CANVAS. The canvas is a derived artifact replayed
	/// from this list on every rebuild, so an erase that scrubbed texels directly would be undone by
	/// the next rebuild — the paint would come back on its own, which is worse than having no eraser
	/// at all. Recorded here it sits in the log in the order the hand made it, and colour blending
	/// does not commute, so that order is the whole point.
	///
	/// THE COLOUR FIELDS ARE STILL CARRIED AND STILL MEAN NOTHING HERE. An erase subtracts its
	/// coverage from the canvas's alpha and never reads R/G/B. They stay rather than moving to a
	/// second type so that one writer and one reader serve both kinds — see StudioDocument, where an
	/// erase is a different LINE KIND rather than a ninth header field, because appending a field
	/// would shift the path every older document parses from.
	/// </summary>
	public bool Erase;

	/// <summary>The path, in the order it was painted, each point carrying a position and the
	/// surface normal there. Order is the whole point: strokes are a log and colour blending does
	/// not commute.</summary>
	public readonly List<PaintStrokePoint> Path = new();
}
