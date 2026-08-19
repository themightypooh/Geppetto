using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Where a snapped click landed, and what pulled it there.</summary>
public readonly struct SnapResult
{
	/// <summary>The snapped position, in sketch plane coordinates.</summary>
	public readonly Vec2 Point;

	/// <summary>Index of the existing sketch point the cursor snapped onto, or -1. The UI draws a
	/// ring on it, and it is what makes closing a profile possible rather than a matter of luck.</summary>
	public readonly int SnappedPointIndex;

	/// <summary>Which axes got locked by inference: bit 1 = x held (a vertical line), bit 2 = y
	/// held (a horizontal one). The UI draws a guide per bit and the sketcher turns them into
	/// Vertical/Horizontal constraints.</summary>
	public readonly int InferenceAxis;

	public SnapResult( Vec2 point, int snappedPointIndex, int inferenceAxis )
	{
		Point = point;
		SnappedPointIndex = snappedPointIndex;
		InferenceAxis = inferenceAxis;
	}
}

/// <summary>
/// Turns a raw click on the sketch plane into the point the user meant.
///
/// THIS LIVES IN THE KERNEL ON PURPOSE. It is sketch-domain maths — point reuse, alignment
/// inference, grid rounding — with no engine surface at all, and while it sat inside the editor's
/// viewport file it could not be compiled or tested outside s&box. That is where the bug lived
/// that stopped closed sketches registering as closed: the tolerances were fixed sketch-unit
/// constants, so on a part one unit across every existing point sat inside the snap radius of
/// every new click, corners collapsed onto each other, and the profile silently became a branching
/// mess that ProfileFinder refused. Out here it is covered by SnapTests at five orders of
/// magnitude of part size.
///
/// TOLERANCES ARE IN SKETCH UNITS, and the caller converts. The editor multiplies a pixel count by
/// its units-per-pixel at the sketch plane's depth, so the tolerance is a constant number of
/// pixels at any zoom and any part size. Passing raw world constants is exactly the mistake this
/// class exists to stop being invisible.
/// </summary>
public sealed class SketchSnapper
{
	/// <summary>How close the cursor must be to an existing point to land on it.</summary>
	public float PointRadius;

	/// <summary>How close counts as "lined up" with an existing point or the active line. Smaller
	/// than PointRadius on purpose: alignment should assist a click, not drag it across the sketch.</summary>
	public float AlignmentRadius;

	/// <summary>Grid rounding, or zero for none. See <see cref="AutoGridStep"/>.</summary>
	public float GridStep;

	/// <summary>
	/// A grid step that stays about <paramref name="targetPixels"/> apart on screen, rounded to 1,
	/// 2 or 5 times a power of ten so it is always a number a person would have picked.
	///
	/// A fixed step cannot work when a part may be one unit or a thousand: 0.25 gave a one-unit
	/// part four steps across it.
	/// </summary>
	public static float AutoGridStep( float unitsPerPixel, float targetPixels = 14f )
	{
		var target = unitsPerPixel * targetPixels;

		if ( target <= 0f || float.IsNaN( target ) || float.IsInfinity( target ) )
			return 0f;

		var magnitude = MathF.Pow( 10f, MathF.Floor( MathF.Log10( target ) ) );
		var normalised = target / magnitude;
		var step = normalised < 1.5f ? 1f : normalised < 3.5f ? 2f : normalised < 7.5f ? 5f : 10f;

		return step * magnitude;
	}

	/// <summary>
	/// Reuse an existing point when the coordinate already exists, so shared corners really are
	/// shared and the chain closes.
	///
	/// Sketch.AddPoint deliberately does not do this — it appends unconditionally, because a caller
	/// typing coordinates wants the literal point. Reuse is an input concern, which is here.
	/// </summary>
	public static int PointIndex( Sketch sketch, Vec2 p )
	{
		for ( var i = 0; i < sketch.Points.Count; i++ )
		{
			if ( (sketch.Points[i] - p).LengthSquared < 1e-8f )
				return i;
		}

		return sketch.AddPoint( p );
	}

	/// <summary>
	/// Snap a raw plane hit.
	/// </summary>
	/// <param name="sketch">The sketch being drawn on; its committed points are snap targets.</param>
	/// <param name="raw">The cursor's position on the plane.</param>
	/// <param name="pending">Points clicked for the entity in progress. They are not in the sketch
	/// yet but must still be snap targets — that is what lets a line close back onto its own start
	/// and lets a rectangle share the corner the cursor is visibly over.</param>
	/// <param name="lineInProgress">True when a line has exactly one pending point, which makes
	/// that point the strongest alignment target on the plane.</param>
	public SnapResult Snap( Sketch sketch, Vec2 raw, IReadOnlyList<Vec2> pending, bool lineInProgress )
	{
		pending ??= Array.Empty<Vec2>();

		var inference = 0;

		// The active line is evaluated FIRST, so a near-horizontal or near-vertical second click
		// cannot be swallowed by a less useful grid result.
		if ( lineInProgress && pending.Count == 1 )
		{
			var start = pending[0];
			var dx = MathF.Abs( raw.x - start.x );
			var dy = MathF.Abs( raw.y - start.y );

			if ( dx <= AlignmentRadius && dx <= dy )
			{
				inference = 1;
				raw = new Vec2( start.x, raw.y );
			}
			else if ( dy <= AlignmentRadius )
			{
				inference = 2;
				raw = new Vec2( raw.x, start.y );
			}
		}

		var best = PointRadius * PointRadius;
		var snappedIndex = -1;

		for ( var i = 0; i < pending.Count; i++ )
		{
			var dist = (pending[i] - raw).LengthSquared;

			if ( dist >= best )
				continue;

			best = dist;
			raw = pending[i];
		}

		for ( var i = 0; i < sketch.Points.Count; i++ )
		{
			var dist = (sketch.Points[i] - raw).LengthSquared;

			if ( dist >= best )
				continue;

			best = dist;
			snappedIndex = i;
		}

		// Landing exactly on a committed point beats every other consideration - no grid rounding,
		// no inference, or the snap would be nudged back off the point it just found.
		if ( snappedIndex >= 0 )
			return new SnapResult( sketch.Points[snappedIndex], snappedIndex, inference );

		var snapped = GridStep > 0f
			? new Vec2(
				MathF.Round( raw.x / GridStep ) * GridStep,
				MathF.Round( raw.y / GridStep ) * GridStep )
			: raw;

		// Line up with any existing point on either axis, and with the sketch origin, which is what
		// the zero-initialised targets below mean.
		var xTarget = 0f;
		var yTarget = 0f;
		var xDistance = MathF.Abs( snapped.x );
		var yDistance = MathF.Abs( snapped.y );

		foreach ( var point in sketch.Points )
		{
			var dx = MathF.Abs( snapped.x - point.x );

			if ( dx < xDistance )
			{
				xDistance = dx;
				xTarget = point.x;
			}

			var dy = MathF.Abs( snapped.y - point.y );

			if ( dy < yDistance )
			{
				yDistance = dy;
				yTarget = point.y;
			}
		}

		if ( xDistance <= AlignmentRadius )
		{
			snapped = new Vec2( xTarget, snapped.y );
			inference |= 1;
		}

		if ( yDistance <= AlignmentRadius )
		{
			snapped = new Vec2( snapped.x, yTarget );
			inference |= 2;
		}

		// Finally the active line's own target, which keeps the second click square with the first
		// even when there is no other geometry anywhere near it.
		if ( lineInProgress && pending.Count == 1 && inference == 0 )
		{
			var start = pending[0];
			var dx = MathF.Abs( snapped.x - start.x );
			var dy = MathF.Abs( snapped.y - start.y );

			if ( dx <= AlignmentRadius && dx <= dy )
			{
				snapped = new Vec2( start.x, snapped.y );
				inference |= 1;
			}
			else if ( dy <= AlignmentRadius )
			{
				snapped = new Vec2( snapped.x, start.y );
				inference |= 2;
			}
		}

		return new SnapResult( snapped, -1, inference );
	}
}
