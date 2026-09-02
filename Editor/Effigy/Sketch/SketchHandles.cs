using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Which part of a curve a handle drives.</summary>
public enum CurveHandleKind
{
	/// <summary>The middle of a line. Dragging it moves the whole line.</summary>
	LineMiddle,

	/// <summary>The middle of an arc. Dragging it changes how far the arc bulges, with both of its
	/// endpoints staying where they are.</summary>
	ArcBulge,

	/// <summary>A point on a circle's rim. Dragging it changes the radius.</summary>
	CircleRim,

	/// <summary>The end of an ellipse's minor axis. The major axis is already an ordinary sketch
	/// point and needs no handle of its own.</summary>
	EllipseMinor,
}

/// <summary>One grab point on a curve, and where it currently sits on the plane.</summary>
public readonly struct CurveHandle
{
	public readonly string CurveId;
	public readonly CurveHandleKind Kind;
	public readonly Vec2 At;

	public CurveHandle( string curveId, CurveHandleKind kind, Vec2 at )
	{
		CurveId = curveId;
		Kind = kind;
		At = at;
	}
}

/// <summary>
/// Handles that sit ON a curve rather than at its ends, and what dragging one does to it.
///
/// WHY THESE EXIST. Every point in a sketch is draggable, but not everything about a curve is a
/// point. A circle's radius is a float on the curve; so is an ellipse's minor axis. An arc's bulge
/// is a point — its centre — but that point is nowhere near the arc and moving it moves the two
/// endpoints' distance from it as well, which is not what "make this arc rounder" means. So there
/// were parts of a sketch that could be drawn and dimensioned and never again touched by hand.
///
/// WHY IT IS IN THE KERNEL. It is sketch maths — a circumcentre, an axis projection, a midpoint —
/// with no engine surface at all, and the viewport half of it is a hit test and a colour. Down here
/// it is covered by HandleTests instead of being verified by reading it in a file that cannot be
/// compiled outside s&amp;box. SketchSnapper is here for the same reason and for the same history.
///
/// ONE HANDLE PER CURVE, at its middle. Two would need a rule for which is which, and the middle is
/// the one place on any of these curves that is unambiguous and never coincides with an endpoint
/// the user is already able to grab.
/// </summary>
public static class SketchHandles
{
	/// <summary>Below this a curve is too small or too degenerate to be worth a handle: the grip
	/// would sit on top of the points it is meant to be distinct from.</summary>
	const float Epsilon = 1e-6f;

	/// <summary>
	/// Every handle in the sketch, in curve order.
	///
	/// A curve that has nothing a handle could drive contributes none — a spline's shape is its own
	/// points and they are all draggable already, so it is deliberately absent rather than given a
	/// grip that would fight the points underneath it.
	/// </summary>
	public static List<CurveHandle> For( Sketch sketch )
	{
		var handles = new List<CurveHandle>();

		if ( sketch is null )
			return handles;

		foreach ( var curve in sketch.Curves )
		{
			if ( At( sketch, curve ) is { } at )
				handles.Add( new CurveHandle( curve.Id, KindOf( curve ), at ) );
		}

		return handles;
	}

	/// <summary>Where one curve's handle sits, or null when it has none — including the degenerate
	/// cases, where a handle would land exactly on an endpoint.</summary>
	public static Vec2? At( Sketch sketch, SketchCurve curve )
	{
		switch ( curve )
		{
			case SketchLine line:
			{
				if ( line.Start == line.End )
					return null;

				var a = sketch.Points[line.Start];
				var b = sketch.Points[line.End];

				if ( (b - a).Length < Epsilon )
					return null;

				return (a + b) / 2f;
			}

			case SketchArc arc:
			{
				var c = sketch.Points[arc.Center];
				var s = sketch.Points[arc.Start];
				var radius = (s - c).Length;

				if ( radius < Epsilon )
					return null;

				var e = sketch.Points[arc.End];
				var a0 = MathF.Atan2( s.y - c.y, s.x - c.x );
				var sweep = SketchArc.Sweep( a0, MathF.Atan2( e.y - c.y, e.x - c.x ), arc.Clockwise );
				var mid = a0 + sweep * 0.5f;

				return new Vec2( c.x + MathF.Cos( mid ) * radius, c.y + MathF.Sin( mid ) * radius );
			}

			case SketchCircle circle:
			{
				if ( circle.Radius < Epsilon )
					return null;

				// A circle has no orientation to hang the grip off, so it goes at angle zero. Any
				// choice is arbitrary; this one is at least the same one every time, which is what
				// a grip has to be to be findable.
				var c = sketch.Points[circle.Center];

				return new Vec2( c.x + circle.Radius, c.y );
			}

			case SketchEllipse ellipse:
			{
				var c = sketch.Points[ellipse.Center];
				var m = sketch.Points[ellipse.MajorPoint];
				var axis = m - c;
				var major = axis.Length;
				var minor = MathF.Abs( ellipse.MinorRadius );

				if ( major < Epsilon || minor < Epsilon )
					return null;

				// A quarter turn from the major axis, which is where the minor axis is by
				// definition. Taking it from the point rather than from a stored angle is the same
				// reason SketchEllipse keeps the major axis as a point at all.
				var u = axis / major;

				return new Vec2( c.x - u.y * minor, c.y + u.x * minor );
			}
		}

		return null;
	}

	static CurveHandleKind KindOf( SketchCurve curve ) => curve switch
	{
		SketchLine => CurveHandleKind.LineMiddle,
		SketchArc => CurveHandleKind.ArcBulge,
		SketchCircle => CurveHandleKind.CircleRim,
		SketchEllipse => CurveHandleKind.EllipseMinor,
		_ => CurveHandleKind.LineMiddle,
	};

	/// <summary>
	/// Drag one curve's handle to <paramref name="target"/>. Returns whether anything changed, so
	/// the caller can leave undo and the rebuild alone when a drag asks for something the geometry
	/// refuses — a zero radius, an arc bulged flat onto its own chord.
	///
	/// A LINE'S POINTS ARE SHARED, and moving the line moves them, so a neighbour that meets it at
	/// a corner follows along. That is not a side effect to be worked around, it is what a shared
	/// point means here (see SketchCurve) and it is what dragging a wall of a closed profile should
	/// do: the profile stays closed.
	/// </summary>
	public static bool Drag( Sketch sketch, string curveId, CurveHandleKind kind, Vec2 target )
	{
		if ( sketch is null || curveId is null )
			return false;

		var curve = sketch.Curves.Find( c => c.Id == curveId );

		if ( curve is null )
			return false;

		switch ( curve )
		{
			case SketchLine line when kind == CurveHandleKind.LineMiddle:
			{
				if ( line.Start == line.End )
					return false;

				var a = sketch.Points[line.Start];
				var b = sketch.Points[line.End];
				var delta = target - (a + b) / 2f;

				if ( delta.Length < Epsilon )
					return false;

				sketch.Points[line.Start] = a + delta;
				sketch.Points[line.End] = b + delta;

				return true;
			}

			case SketchArc arc when kind == CurveHandleKind.ArcBulge:
			{
				var s = sketch.Points[arc.Start];
				var e = sketch.Points[arc.End];

				if ( Circumcentre( s, e, target ) is not { } centre )
					return false;

				if ( (centre - sketch.Points[arc.Center]).Length < Epsilon )
					return false;

				sketch.Points[arc.Center] = centre;

				// The centre alone does not say which way round the arc goes: the same circle
				// through the same two endpoints is two arcs, and the one the user is dragging is
				// whichever contains their cursor. Without setting this, pulling the bulge across
				// the chord turns the arc inside out and it snaps to the long way round.
				arc.Clockwise = !Contains( centre, s, e, target, clockwise: false );

				return true;
			}

			case SketchCircle circle when kind == CurveHandleKind.CircleRim:
			{
				var radius = (target - sketch.Points[circle.Center]).Length;

				if ( radius < Epsilon || MathF.Abs( radius - circle.Radius ) < Epsilon )
					return false;

				circle.Radius = radius;

				return true;
			}

			case SketchEllipse ellipse when kind == CurveHandleKind.EllipseMinor:
			{
				var c = sketch.Points[ellipse.Center];
				var axis = sketch.Points[ellipse.MajorPoint] - c;
				var major = axis.Length;

				if ( major < Epsilon )
					return false;

				// Only the component across the major axis counts. Dragging the grip along the
				// major axis is asking for nothing, and taking the raw distance instead would grow
				// the minor radius on a gesture that never left the axis.
				var u = axis / major;
				var d = target - c;
				var minor = MathF.Abs( d.x * -u.y + d.y * u.x );

				if ( minor < Epsilon || MathF.Abs( minor - ellipse.MinorRadius ) < Epsilon )
					return false;

				ellipse.MinorRadius = minor;

				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The point to hold still when the sketch is re-solved during this drag.
	///
	/// The solver needs one point pinned or the whole sketch is free to slide (see SketchSolver),
	/// and the honest pin for a handle drag is a point the drag is deliberately NOT moving: an
	/// arc's endpoints stay put while it bulges, and a circle and an ellipse both turn about their
	/// centre. A line moves as a whole, so either end will do and the start is taken.
	/// </summary>
	public static int Pin( Sketch sketch, string curveId, CurveHandleKind kind )
	{
		var curve = sketch?.Curves.Find( c => c.Id == curveId );

		return curve switch
		{
			SketchLine line when kind == CurveHandleKind.LineMiddle => line.Start,
			SketchArc arc when kind == CurveHandleKind.ArcBulge => arc.Start,
			SketchCircle circle when kind == CurveHandleKind.CircleRim => circle.Center,
			SketchEllipse ellipse when kind == CurveHandleKind.EllipseMinor => ellipse.Center,
			_ => 0,
		};
	}

	/// <summary>
	/// The centre of the circle through three points, or null when they are collinear and there is
	/// no such circle.
	///
	/// Solved with the first point moved to the origin rather than in absolute coordinates. The
	/// textbook form squares the coordinates themselves, so a sketch drawn a thousand units from
	/// the origin loses most of its precision to the subtraction of two nearly equal large numbers
	/// - on an arc whose radius is small compared to where it sits, which is the ordinary case in a
	/// part built away from the origin.
	/// </summary>
	public static Vec2? Circumcentre( Vec2 a, Vec2 b, Vec2 c )
	{
		var u = b - a;
		var v = c - a;

		var det = u.x * v.y - u.y * v.x;

		// Collinear, or so nearly so that the centre would be flung to infinity. Scaled by the
		// triangle's own size, because a determinant is an area and an absolute threshold would
		// call every small arc degenerate.
		var scale = MathF.Max( u.LengthSquared, v.LengthSquared );

		if ( scale < Epsilon || MathF.Abs( det ) < 1e-7f * scale )
			return null;

		var uu = u.LengthSquared;
		var vv = v.LengthSquared;

		return new Vec2(
			a.x + (uu * v.y - vv * u.y) / (2f * det),
			a.y + (vv * u.x - uu * v.x) / (2f * det) );
	}

	/// <summary>Whether sweeping from <paramref name="start"/> to <paramref name="end"/> about
	/// <paramref name="centre"/> in the given direction passes <paramref name="point"/>.</summary>
	static bool Contains( Vec2 centre, Vec2 start, Vec2 end, Vec2 point, bool clockwise )
	{
		var a0 = MathF.Atan2( start.y - centre.y, start.x - centre.x );
		var sweep = SketchArc.Sweep( a0, MathF.Atan2( end.y - centre.y, end.x - centre.x ), clockwise );
		var toPoint = SketchArc.Sweep( a0, MathF.Atan2( point.y - centre.y, point.x - centre.x ), clockwise );

		return MathF.Abs( toPoint ) <= MathF.Abs( sweep );
	}
}
