using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Where a cutting stroke went through a curve.</summary>
public readonly struct CutCrossing
{
	/// <summary>The curve the stroke crossed.</summary>
	public readonly SketchCurve Curve;

	/// <summary>Where it crossed. Near enough rather than exact — all this has to do is say WHICH
	/// PIECE of the curve was meant, and <see cref="SketchEdit.Trim"/> re-finds the real
	/// intersections either side of it for itself.</summary>
	public readonly Vec2 At;

	/// <summary>How far along the stroke segment the crossing sits, 0 at its start and 1 at its
	/// end. Crossings are ordered by this, so a stroke cuts what it reached first, first.</summary>
	public readonly float Along;

	public CutCrossing( SketchCurve curve, Vec2 at, float along )
	{
		Curve = curve;
		At = at;
		Along = along;
	}
}

/// <summary>
/// The cut stroke: hold the button, drag a line across the sketch, and whatever the line goes
/// through is cut where it was crossed.
///
/// ONE SEGMENT OF THE STROKE AT A TIME, which is what makes this kernel work rather than a gesture.
/// The editor samples the cursor as it moves and hands down the piece of path travelled since the
/// last sample; this finds what that piece crossed and cuts it. So there is no notion of a "stroke"
/// down here at all — a stroke is a sequence of these — and everything that can go wrong is in one
/// function a test can hold both ends of.
///
/// WHAT "CUT" MEANS IS TRIM'S ANSWER, NOT A SECOND ONE. Crossing a curve removes the piece the
/// stroke went through, back to wherever that curve runs into something else — <see
/// cref="SketchEdit.Trim"/>, the same call the Trim tool's single click makes. That is what keeps
/// the two tools agreeing with each other, and it is also why the simple case looks like plain
/// deletion: swiping across a rectangle's edge takes the whole edge, because its corners are where
/// it meets its neighbours, and swiping across a lone line takes the whole line, because a curve
/// that crosses nothing has no piece smaller than itself.
///
/// SPLINES AND ELLIPSES GO WHOLE. Trim refuses them — there is no closed form to cut them at — and
/// the alternative to removing them outright is a cut tool that silently does nothing when it is
/// dragged through one. That reads as the tool being broken rather than as the shape being special,
/// so they are removed and the editor says how many curves went.
/// </summary>
public static class SketchCut
{
	const float Eps = 1e-9f;

	/// <summary>
	/// Every curve the segment from <paramref name="from"/> to <paramref name="to"/> goes through,
	/// in the order the stroke reached them.
	///
	/// AT MOST ONE CROSSING PER CURVE, the first along the stroke. A segment can cross the same
	/// circle twice, and cutting at both in one pass is not possible anyway: the first cut replaces
	/// the circle with an arc, and the second crossing then names a curve the sketch no longer has.
	/// Reporting only the first is the honest version of that, and the next sample of the stroke
	/// picks up whatever is left.
	/// </summary>
	public static List<CutCrossing> Crossings( Sketch sketch, Vec2 from, Vec2 to )
	{
		var found = new List<CutCrossing>();

		if ( sketch is null || (to - from).LengthSquared < Eps )
			return found;

		foreach ( var curve in sketch.Curves )
		{
			var points = curve.Tessellate( sketch, sketch.Tolerance );

			// Tessellated rather than analytic, unlike everything in SketchIntersect, and it costs
			// nothing here: the point this produces is only ever handed to Trim as "which piece",
			// and Trim computes the actual cuts from the real geometry. A tessellation-accurate
			// pick point picks exactly the same piece an exact one would.
			var best = float.MaxValue;
			var at = Vec2.Zero;

			for ( var i = 0; i + 1 < points.Count; i++ )
			{
				if ( !SegmentCross( from, to, points[i], points[i + 1], out var hit, out var along ) )
					continue;

				if ( along >= best )
					continue;

				best = along;
				at = hit;
			}

			if ( best < float.MaxValue )
				found.Add( new CutCrossing( curve, at, best ) );
		}

		found.Sort( ( a, b ) => a.Along.CompareTo( b.Along ) );

		return found;
	}

	/// <summary>Find what one segment of the stroke crossed and cut it. Returns how many curves
	/// were changed, which is what the editor turns into "3 pieces" on the status line.</summary>
	public static int Cut( Sketch sketch, Vec2 from, Vec2 to ) =>
		Apply( sketch, Crossings( sketch, from, to ) );

	/// <summary>
	/// Cut at each crossing, in stroke order.
	///
	/// Separate from finding them so the editor can look before it leaps — it takes its undo
	/// snapshot only once a stroke has actually found something, and a stroke swept over empty
	/// space must not become a Ctrl+Z that restores an identical sketch.
	/// </summary>
	public static int Apply( Sketch sketch, IReadOnlyList<CutCrossing> crossings )
	{
		var cut = 0;

		if ( sketch is null || crossings is null )
			return cut;

		foreach ( var crossing in crossings )
		{
			// An earlier cut in the same stroke can have taken this curve already: trimming a circle
			// replaces it with an arc, and trimming a curve that crosses nothing removes it outright.
			// A crossing naming a curve the sketch no longer holds is stale, not wrong.
			if ( !sketch.Curves.Contains( crossing.Curve ) )
				continue;

			if ( crossing.Curve is SketchLine or SketchArc or SketchCircle )
			{
				// Trim's refusals are all "that point is not on the curve" and its kin, which cannot
				// happen for a point that came out of an intersection with the curve — so a false
				// here is a bug rather than a thing to report, and the count is what the editor
				// reads to tell "nothing was under the stroke" from "something was".
				if ( SketchEdit.Trim( sketch, crossing.Curve, crossing.At, out _ ) )
					cut++;

				continue;
			}

			sketch.Curves.Remove( crossing.Curve );
			cut++;
		}

		return cut;
	}

	/// <summary>
	/// Where two segments cross, and how far along the FIRST of them.
	///
	/// Parallel reports nothing, collinear overlap included. A stroke drawn ALONG a line rather than
	/// across it has not gone through anything, and picking one of the infinitely many shared points
	/// as "the crossing" would make dragging down an edge quietly eat it.
	/// </summary>
	static bool SegmentCross( Vec2 a, Vec2 b, Vec2 c, Vec2 d, out Vec2 at, out float along )
	{
		at = Vec2.Zero;
		along = 0f;

		var ab = b - a;
		var cd = d - c;

		var denom = Vec2.Cross( ab, cd );

		if ( MathF.Abs( denom ) < Eps )
			return false;

		var t = Vec2.Cross( c - a, cd ) / denom;
		var u = Vec2.Cross( c - a, ab ) / denom;

		if ( t is < 0f or > 1f || u is < 0f or > 1f )
			return false;

		at = a + ab * t;
		along = t;

		return true;
	}
}
