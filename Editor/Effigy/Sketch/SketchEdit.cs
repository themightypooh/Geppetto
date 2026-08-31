using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Where two curves cross, and how far along each of them the crossing is.</summary>
public readonly struct CurveHit
{
	public readonly Vec2 Point;

	/// <summary>Position along the first curve, 0 at its start and 1 at its end.</summary>
	public readonly float TA;

	/// <summary>Position along the second curve, on the same scale.</summary>
	public readonly float TB;

	public CurveHit( Vec2 point, float ta, float tb )
	{
		Point = point;
		TA = ta;
		TB = tb;
	}
}

/// <summary>
/// Where sketch curves cross each other.
///
/// ANALYTIC FOR THE PAIRS THAT HAVE A CLOSED FORM, sampled for the rest. Line/line, line/circle and
/// circle/circle are exact, and arcs are their circle with the hits outside the sweep thrown away.
/// Splines and ellipses have no closed form worth carrying, so they fall back to walking their
/// tessellations — which is as accurate as the tessellation tolerance and no more, and is marked as
/// such by <see cref="CurveHit"/> parameters that come from segment indices rather than from
/// geometry.
///
/// Why exactness matters here at all: trim moves a real endpoint onto one of these, and a fillet
/// puts an arc tangent to two lines through one. A sampled intersection would leave a sketch whose
/// corners are a tessellation-tolerance away from meeting, and the loop walk matches endpoints by
/// index rather than position — so a curve trimmed to nearly the right place is a curve that
/// silently stops closing a region.
/// </summary>
public static class SketchIntersect
{
	const float Eps = 1e-6f;

	/// <summary>Every place two curves cross, ordered along the first of them.</summary>
	public static List<CurveHit> Between( Sketch sketch, SketchCurve a, SketchCurve b )
	{
		if ( ReferenceEquals( a, b ) )
			return new List<CurveHit>();

		var hits = Compute( sketch, a, b );

		hits.Sort( ( x, y ) => x.TA.CompareTo( y.TA ) );

		return hits;
	}

	static List<CurveHit> Compute( Sketch sketch, SketchCurve a, SketchCurve b )
	{
		if ( a is SketchLine la && b is SketchLine lb )
			return LineLine( sketch, la, lb );

		if ( a is SketchLine line && IsCircular( b ) )
			return LineCircular( sketch, line, b, flip: false );

		if ( IsCircular( a ) && b is SketchLine other )
			return LineCircular( sketch, other, a, flip: true );

		if ( IsCircular( a ) && IsCircular( b ) )
			return CircularCircular( sketch, a, b );

		return Sampled( sketch, a, b );
	}

	/// <summary>A circle, or an arc — anything whose geometry is a centre and a radius.</summary>
	public static bool IsCircular( SketchCurve curve ) => curve is SketchCircle or SketchArc;

	/// <summary>The centre and radius behind a circular curve.</summary>
	public static (Vec2 Centre, float Radius) CircleOf( Sketch sketch, SketchCurve curve ) => curve switch
	{
		SketchCircle c => (sketch.Points[c.Center], c.Radius),
		SketchArc arc => (sketch.Points[arc.Center], arc.Radius( sketch )),
		_ => throw new InvalidOperationException( $"{curve.GetType().Name} is not a circle or an arc" )
	};

	static List<CurveHit> LineLine( Sketch sketch, SketchLine a, SketchLine b )
	{
		var result = new List<CurveHit>();

		var p = sketch.Points[a.Start];
		var d = sketch.Points[a.End] - p;
		var q = sketch.Points[b.Start];
		var e = sketch.Points[b.End] - q;

		var denom = Vec2.Cross( d, e );

		// Parallel, including collinear. Collinear overlap is a real state and has no single
		// crossing point, so it is reported as no crossing rather than as an arbitrary one.
		if ( MathF.Abs( denom ) < Eps )
			return result;

		var t = Vec2.Cross( q - p, e ) / denom;
		var u = Vec2.Cross( q - p, d ) / denom;

		if ( t is < -Eps or > 1f + Eps || u is < -Eps or > 1f + Eps )
			return result;

		result.Add( new CurveHit( p + d * t, t, u ) );

		return result;
	}

	static List<CurveHit> LineCircular( Sketch sketch, SketchLine line, SketchCurve circular, bool flip )
	{
		var result = new List<CurveHit>();

		var (centre, radius) = CircleOf( sketch, circular );

		if ( radius < Eps )
			return result;

		var p = sketch.Points[line.Start];
		var d = sketch.Points[line.End] - p;
		var lengthSq = d.LengthSquared;

		if ( lengthSq < Eps * Eps )
			return result;

		// |p + t d - c|^2 = r^2, expanded into a quadratic in t.
		var f = p - centre;
		var bq = 2f * Vec2.Dot( f, d );
		var cq = f.LengthSquared - radius * radius;

		var disc = bq * bq - 4f * lengthSq * cq;

		if ( disc < 0f )
			return result;

		var root = MathF.Sqrt( disc );

		foreach ( var t in new[] { (-bq - root) / (2f * lengthSq), (-bq + root) / (2f * lengthSq) } )
		{
			if ( t is < -Eps or > 1f + Eps )
				continue;

			var point = p + d * t;

			if ( !OnCurve( sketch, circular, point, out var tc ) )
				continue;

			result.Add( flip ? new CurveHit( point, tc, t ) : new CurveHit( point, t, tc ) );
		}

		return result;
	}

	static List<CurveHit> CircularCircular( Sketch sketch, SketchCurve a, SketchCurve b )
	{
		var result = new List<CurveHit>();

		var (ca, ra) = CircleOf( sketch, a );
		var (cb, rb) = CircleOf( sketch, b );

		var delta = cb - ca;
		var dist = delta.Length;

		// Concentric, or too far apart, or one swallowed by the other.
		if ( dist < Eps || dist > ra + rb + Eps || dist < MathF.Abs( ra - rb ) - Eps )
			return result;

		var x = (dist * dist + ra * ra - rb * rb) / (2f * dist);
		var hSq = ra * ra - x * x;
		var h = hSq > 0f ? MathF.Sqrt( hSq ) : 0f;

		var along = delta / dist;
		var across = new Vec2( -along.y, along.x );
		var mid = ca + along * x;

		foreach ( var point in h < Eps
			? new[] { mid }
			: new[] { mid + across * h, mid - across * h } )
		{
			if ( !OnCurve( sketch, a, point, out var ta ) )
				continue;

			if ( !OnCurve( sketch, b, point, out var tb ) )
				continue;

			result.Add( new CurveHit( point, ta, tb ) );
		}

		return result;
	}

	/// <summary>
	/// Crossings found by walking two tessellations against each other. The fallback for curves
	/// with no closed form, and only as accurate as the tolerance they were sampled at.
	/// </summary>
	static List<CurveHit> Sampled( Sketch sketch, SketchCurve a, SketchCurve b )
	{
		var result = new List<CurveHit>();

		var pa = a.Tessellate( sketch, sketch.Tolerance );
		var pb = b.Tessellate( sketch, sketch.Tolerance );

		for ( var i = 0; i + 1 < pa.Count; i++ )
		{
			for ( var j = 0; j + 1 < pb.Count; j++ )
			{
				var p = pa[i];
				var d = pa[i + 1] - p;
				var q = pb[j];
				var e = pb[j + 1] - q;

				var denom = Vec2.Cross( d, e );

				if ( MathF.Abs( denom ) < Eps )
					continue;

				var t = Vec2.Cross( q - p, e ) / denom;
				var u = Vec2.Cross( q - p, d ) / denom;

				if ( t is < 0f or > 1f || u is < 0f or > 1f )
					continue;

				result.Add( new CurveHit( p + d * t,
					(i + t) / (pa.Count - 1),
					(j + u) / (pb.Count - 1) ) );
			}
		}

		return result;
	}

	/// <summary>
	/// Whether a point that is already known to be on a curve's underlying circle is on the curve
	/// ITSELF — inside an arc's sweep rather than on the part of the circle it does not cover — and
	/// if so, how far along it is.
	/// </summary>
	public static bool OnCurve( Sketch sketch, SketchCurve curve, Vec2 point, out float t )
	{
		t = 0f;

		switch ( curve )
		{
			case SketchLine line:
			{
				var p = sketch.Points[line.Start];
				var d = sketch.Points[line.End] - p;

				if ( d.LengthSquared < Eps * Eps )
					return false;

				t = Vec2.Dot( point - p, d ) / d.LengthSquared;

				return t is >= -Eps and <= 1f + Eps;
			}

			case SketchCircle circle:
			{
				var c = sketch.Points[circle.Center];
				var angle = MathF.Atan2( point.y - c.y, point.x - c.x );

				if ( angle < 0f )
					angle += MathF.Tau;

				t = angle / MathF.Tau;

				return true;
			}

			case SketchArc arc:
			{
				var c = sketch.Points[arc.Center];
				var start = MathF.Atan2( sketch.Points[arc.Start].y - c.y, sketch.Points[arc.Start].x - c.x );
				var here = MathF.Atan2( point.y - c.y, point.x - c.x );

				var sweep = ArcSweep( sketch, arc );

				if ( MathF.Abs( sweep ) < Eps )
					return false;

				var offset = here - start;

				// Bring the offset into the same turn direction as the sweep before comparing, or a
				// hit just past the start reads as a hit just short of a full turn.
				if ( sweep > 0f )
				{
					while ( offset < 0f ) offset += MathF.Tau;
					while ( offset > MathF.Tau ) offset -= MathF.Tau;
				}
				else
				{
					while ( offset > 0f ) offset -= MathF.Tau;
					while ( offset < -MathF.Tau ) offset += MathF.Tau;
				}

				t = offset / sweep;

				return t is >= -Eps and <= 1f + Eps;
			}

			default:
			{
				// No closed form: find the nearest tessellated segment and report where on it the
				// point landed.
				var pts = curve.Tessellate( sketch, sketch.Tolerance );
				var best = float.MaxValue;

				for ( var i = 0; i + 1 < pts.Count; i++ )
				{
					var p = pts[i];
					var d = pts[i + 1] - p;

					if ( d.LengthSquared < Eps * Eps )
						continue;

					var u = Math.Clamp( Vec2.Dot( point - p, d ) / d.LengthSquared, 0f, 1f );
					var distance = (p + d * u - point).Length;

					if ( distance >= best )
						continue;

					best = distance;
					t = (i + u) / (pts.Count - 1);
				}

				return best < MathF.Max( sketch.Tolerance * 4f, 1e-3f );
			}
		}
	}

	/// <summary>An arc's signed sweep in radians, positive counter-clockwise.</summary>
	public static float ArcSweep( Sketch sketch, SketchArc arc )
	{
		var c = sketch.Points[arc.Center];
		var a0 = MathF.Atan2( sketch.Points[arc.Start].y - c.y, sketch.Points[arc.Start].x - c.x );
		var a1 = MathF.Atan2( sketch.Points[arc.End].y - c.y, sketch.Points[arc.End].x - c.x );

		var sweep = a1 - a0;

		if ( arc.Clockwise )
		{
			while ( sweep > 0f ) sweep -= MathF.Tau;
			while ( sweep <= -MathF.Tau ) sweep += MathF.Tau;
			if ( MathF.Abs( sweep ) < 1e-6f ) sweep = -MathF.Tau;
		}
		else
		{
			while ( sweep < 0f ) sweep += MathF.Tau;
			while ( sweep >= MathF.Tau ) sweep -= MathF.Tau;
			if ( MathF.Abs( sweep ) < 1e-6f ) sweep = MathF.Tau;
		}

		return sweep;
	}
}

/// <summary>
/// The edits a sketcher needs that are not "draw another curve": trim, extend, fillet and offset.
///
/// WHY THESE ARE EDITS AND NOT FEATURES. Everything in Features/ is parametric — it re-runs on
/// rebuild and its inputs stay editable. These are not: they change the curve list in place, the
/// way dragging a point does, and the undo stack is what takes them back. Onshape draws the same
/// line, and the reason is that a parametric trim has to name the thing it trimmed against, which
/// means every one of these would need a persistent reference to a curve that a later edit can
/// delete. That is a large amount of machinery to make "cut this bit off" survive a rebuild, and
/// it buys very little, because the sketch itself is the thing being edited.
///
/// EVERY OPERATION RETURNS FALSE AND A REASON RATHER THAN THROWING. A trim that hits nothing and a
/// fillet too big for its corner are things a user does constantly by accident, not exceptional
/// states, and the editor turns the reason into a status line.
/// </summary>
public static class SketchEdit
{
	const float Eps = 1e-5f;

	/// <summary>
	/// Round a corner where two lines meet, replacing the sharp join with a tangent arc.
	///
	/// The corner is named by the POINT the two lines share, which is the only unambiguous way to
	/// say which corner — two lines can meet at either end, and shared points are how this sketch
	/// stores a join in the first place.
	///
	/// Both lines keep their far ends and are shortened to the tangent points; the arc is a new
	/// curve between them and the corner point itself is left in the sketch, orphaned. Leaving it
	/// is deliberate: removing a point renumbers every index above it, and every curve and every
	/// constraint in the sketch is stored as an index. An orphan point costs two floats and is
	/// invisible; a renumber is a silent corruption of every rule in the sketch.
	/// </summary>
	public static bool Fillet( Sketch sketch, int corner, float radius, out string error )
	{
		error = null;

		if ( radius <= 0f )
		{
			error = "A fillet needs a radius greater than zero.";
			return false;
		}

		if ( corner < 0 || corner >= sketch.Points.Count )
		{
			error = "That corner is not a point in this sketch.";
			return false;
		}

		var lines = sketch.Curves
			.OfType<SketchLine>()
			.Where( l => !l.Construction && (l.Start == corner || l.End == corner) )
			.ToList();

		if ( lines.Count != 2 )
		{
			error = lines.Count < 2
				? "A fillet needs two lines meeting at the corner."
				: $"{lines.Count} lines meet at that corner, so which two to round is ambiguous.";

			return false;
		}

		var c = sketch.Points[corner];

		// Direction AWAY from the corner along each line, and the far end each keeps.
		var farA = lines[0].Start == corner ? lines[0].End : lines[0].Start;
		var farB = lines[1].Start == corner ? lines[1].End : lines[1].Start;

		var dirA = (sketch.Points[farA] - c);
		var dirB = (sketch.Points[farB] - c);

		var lenA = dirA.Length;
		var lenB = dirB.Length;

		if ( lenA < Eps || lenB < Eps )
		{
			error = "One of the lines at that corner has no length.";
			return false;
		}

		var ua = dirA / lenA;
		var ub = dirB / lenB;

		var cos = Math.Clamp( Vec2.Dot( ua, ub ), -1f, 1f );
		var angle = MathF.Acos( cos );

		if ( angle < 1e-3f || MathF.Abs( angle - MathF.PI ) < 1e-3f )
		{
			error = angle < 1e-3f
				? "Those two lines fold back on each other, so there is no corner to round."
				: "Those two lines are straight through the corner, so there is nothing to round.";

			return false;
		}

		// Distance from the corner to each tangent point, and from the corner to the arc centre.
		// Standard corner-rounding: the tangent length is r/tan(half), the centre sits r/sin(half)
		// along the bisector.
		var half = angle * 0.5f;
		var tangent = radius / MathF.Tan( half );

		if ( tangent > lenA - Eps || tangent > lenB - Eps )
		{
			error = $"A radius of {radius} needs {tangent:0.###} of line on each side, and only " +
				$"{MathF.Min( lenA, lenB ):0.###} is available.";

			return false;
		}

		var pointA = c + ua * tangent;
		var pointB = c + ub * tangent;

		var bisector = (ua + ub);

		if ( bisector.Length < Eps )
		{
			error = "Those two lines are straight through the corner, so there is nothing to round.";
			return false;
		}

		var centre = c + bisector.Normal * (radius / MathF.Sin( half ));

		var indexA = sketch.AddPoint( pointA );
		var indexB = sketch.AddPoint( pointB );
		var indexC = sketch.AddPoint( centre );

		// Pull each line off the corner and onto its tangent point.
		if ( lines[0].Start == corner ) lines[0].Start = indexA; else lines[0].End = indexA;
		if ( lines[1].Start == corner ) lines[1].Start = indexB; else lines[1].End = indexB;

		// The arc runs from A to B the short way. Which way that is depends on which side of the
		// bisector the corner sits, and the cross product is what says so.
		var clockwise = Vec2.Cross( pointA - centre, pointB - centre ) < 0f;

		sketch.Add( new SketchArc( indexC, indexA, indexB, clockwise ) );

		return true;
	}

	/// <summary>
	/// Cut the piece of a curve that the pick point sits on, back to wherever it crosses something
	/// else. A curve crossing nothing is removed outright, which is what a trim of an untouched
	/// line means.
	///
	/// Lines, arcs and circles only. A trimmed circle becomes an arc, which is the one case here
	/// that changes a curve's type rather than its extent.
	/// </summary>
	public static bool Trim( Sketch sketch, SketchCurve curve, Vec2 pick, out string error )
	{
		error = null;

		if ( curve is not (SketchLine or SketchArc or SketchCircle) )
		{
			error = $"Trimming a {curve.GetType().Name} is not supported.";
			return false;
		}

		if ( !SketchIntersect.OnCurve( sketch, curve, pick, out var pickT ) && curve is not SketchCircle )
		{
			error = "That point is not on the curve.";
			return false;
		}

		var cuts = new List<float>();

		foreach ( var other in sketch.Curves )
		{
			if ( ReferenceEquals( other, curve ) )
				continue;

			foreach ( var hit in SketchIntersect.Between( sketch, curve, other ) )
			{
				if ( hit.TA is > Eps and < 1f - Eps || curve is SketchCircle )
					cuts.Add( hit.TA );
			}
		}

		cuts.Sort();

		if ( cuts.Count == 0 )
		{
			sketch.Curves.Remove( curve );
			return true;
		}

		if ( curve is SketchCircle circle )
			return TrimCircle( sketch, circle, cuts, pickT, out error );

		// The piece under the pick runs between the two cuts either side of it, with the curve's
		// own ends standing in where there is no cut on that side.
		var lower = 0f;
		var upper = 1f;

		foreach ( var cut in cuts )
		{
			if ( cut <= pickT && cut > lower ) lower = cut;
			if ( cut >= pickT && cut < upper ) upper = cut;
		}

		var removesStart = lower <= Eps;
		var removesEnd = upper >= 1f - Eps;

		if ( removesStart && removesEnd )
		{
			sketch.Curves.Remove( curve );
			return true;
		}

		if ( removesStart )
			return MoveEnd( sketch, curve, atStart: true, PointAt( sketch, curve, upper ), out error );

		if ( removesEnd )
			return MoveEnd( sketch, curve, atStart: false, PointAt( sketch, curve, lower ), out error );

		// A cut out of the middle leaves two pieces, so the curve is shortened to the first and a
		// copy carries the second.
		var tailStart = PointAt( sketch, curve, upper );
		var tailEnd = EndPoint( sketch, curve, atStart: false );

		if ( !MoveEnd( sketch, curve, atStart: false, PointAt( sketch, curve, lower ), out error ) )
			return false;

		switch ( curve )
		{
			case SketchLine:
				sketch.Add( new SketchLine( sketch.AddPoint( tailStart ), sketch.AddPoint( tailEnd ) ) );
				break;

			case SketchArc arc:
				sketch.Add( new SketchArc( arc.Center,
					sketch.AddPoint( tailStart ), sketch.AddPoint( tailEnd ), arc.Clockwise ) );
				break;
		}

		return true;
	}

	/// <summary>
	/// A circle has no ends, so trimming it is different in kind: the piece under the pick runs
	/// between the cut before it and the cut after it, WRAPPING past 1 back to 0, and what is left
	/// is a single arc going the other way round.
	/// </summary>
	static bool TrimCircle( Sketch sketch, SketchCircle circle, List<float> cuts, float pickT, out string error )
	{
		error = null;

		if ( cuts.Count < 2 )
		{
			error = "A circle needs to be crossed in two places before a piece of it can be trimmed.";
			return false;
		}

		// The cut at or before the pick, and the one after it, both wrapping.
		var before = cuts.Where( c => c <= pickT ).DefaultIfEmpty( cuts[^1] ).Max();
		var after = cuts.Where( c => c >= pickT ).DefaultIfEmpty( cuts[0] ).Min();

		if ( MathF.Abs( before - after ) < Eps )
		{
			error = "That piece of the circle is too small to trim.";
			return false;
		}

		var centre = sketch.Points[circle.Center];

		Vec2 On( float t )
		{
			var angle = t * MathF.Tau;
			return new Vec2( centre.x + MathF.Cos( angle ) * circle.Radius,
				centre.y + MathF.Sin( angle ) * circle.Radius );
		}

		// What survives runs from the cut AFTER the pick round to the cut BEFORE it.
		var keepStart = sketch.AddPoint( On( after ) );
		var keepEnd = sketch.AddPoint( On( before ) );

		sketch.Curves.Remove( circle );
		sketch.Add( new SketchArc( circle.Center, keepStart, keepEnd ) );

		return true;
	}

	/// <summary>
	/// Stretch a line or an arc past one of its ends until it runs into something. Extends to the
	/// NEAREST crossing, which is what makes repeated extends walk outward one curve at a time
	/// rather than leaping to the far side of the sketch.
	/// </summary>
	public static bool Extend( Sketch sketch, SketchCurve curve, bool atStart, out string error )
	{
		error = null;

		switch ( curve )
		{
			case SketchLine line:
			{
				var from = sketch.Points[atStart ? line.Start : line.End];
				var toward = sketch.Points[atStart ? line.End : line.Start];
				var direction = from - toward;

				if ( direction.Length < Eps )
				{
					error = "That line has no length, so there is no direction to extend it in.";
					return false;
				}

				var unit = direction.Normal;
				var best = float.MaxValue;
				var found = Vec2.Zero;

				// A long probe standing in for the infinite ray. Bounded rather than infinite
				// because every intersection routine here works on bounded curves, and the bound
				// only has to beat the size of the sketch. Built once, outside the loop — its two
				// points then stay in the list for the same reason a fillet's corner does, since
				// removing them would renumber every index above them.
				var probe = new SketchLine(
					sketch.AddPoint( from ),
					sketch.AddPoint( from + unit * ProbeLength( sketch ) ) );

				foreach ( var other in sketch.Curves )
				{
					if ( ReferenceEquals( other, curve ) )
						continue;

					foreach ( var hit in SketchIntersect.Between( sketch, probe, other ) )
					{
						var distance = (hit.Point - from).Length;

						if ( distance <= Eps || distance >= best )
							continue;

						best = distance;
						found = hit.Point;
					}
				}

				if ( best == float.MaxValue )
				{
					error = "Nothing lies beyond that end to extend to.";
					return false;
				}

				return MoveEnd( sketch, curve, atStart, found, out error );
			}

			case SketchArc arc:
			{
				// An arc extends along its own circle, so what it can reach is wherever that circle
				// crosses something — and the nearest such crossing in the sweep direction.
				var (centre, radius) = SketchIntersect.CircleOf( sketch, arc );
				var probe = new SketchCircle( arc.Center, radius );

				var sweep = SketchIntersect.ArcSweep( sketch, arc );
				var fromAngle = Angle( sketch.Points[atStart ? arc.Start : arc.End] - centre );
				var outward = atStart ? -MathF.Sign( sweep ) : MathF.Sign( sweep );

				var best = float.MaxValue;
				var found = Vec2.Zero;

				foreach ( var other in sketch.Curves )
				{
					if ( ReferenceEquals( other, curve ) )
						continue;

					foreach ( var hit in SketchIntersect.Between( sketch, probe, other ) )
					{
						var step = (Angle( hit.Point - centre ) - fromAngle) * outward;

						while ( step < 0f ) step += MathF.Tau;
						while ( step > MathF.Tau ) step -= MathF.Tau;

						if ( step <= Eps || step >= best )
							continue;

						best = step;
						found = hit.Point;
					}
				}

				if ( best == float.MaxValue )
				{
					error = "Nothing lies beyond that end to extend to.";
					return false;
				}

				return MoveEnd( sketch, curve, atStart, found, out error );
			}

			default:
				error = $"Extending a {curve.GetType().Name} is not supported.";
				return false;
		}
	}

	/// <summary>
	/// Copy a chain of lines and arcs a fixed distance to one side.
	///
	/// Positive is to the LEFT of the direction each curve is travelling, which makes the sign mean
	/// something consistent along a chain rather than depending on each curve's own winding.
	///
	/// CORNERS ARE CLOSED BY EXTENDING THE NEIGHBOURS TO THEIR CROSSING, not by inserting an arc.
	/// On an outside corner the two offset curves fall short of each other and on an inside corner
	/// they overshoot; moving the shared end onto the crossing fixes both, and it is what a CAD
	/// offset does by default. Where the two do not cross at all — which happens when the offset is
	/// larger than the feature it is going round — the joint is left open and reported.
	/// </summary>
	public static bool Offset( Sketch sketch, IReadOnlyList<SketchCurve> chain, float distance,
		out List<SketchCurve> created, out string error )
	{
		created = new List<SketchCurve>();
		error = null;

		if ( chain.Count == 0 )
		{
			error = "Nothing was selected to offset.";
			return false;
		}

		if ( MathF.Abs( distance ) < Eps )
		{
			error = "An offset of zero would just copy the curves on top of themselves.";
			return false;
		}

		foreach ( var curve in chain )
		{
			if ( curve is not (SketchLine or SketchArc) )
			{
				error = $"Offsetting a {curve.GetType().Name} is not supported.";
				return false;
			}
		}

		// Every offset curve gets its own fresh points; the joints are stitched afterwards by
		// moving those points, which is why they cannot be shared with the originals.
		var ends = new List<(int Start, int End)>();

		foreach ( var curve in chain )
		{
			switch ( curve )
			{
				case SketchLine line:
				{
					var a = sketch.Points[line.Start];
					var b = sketch.Points[line.End];
					var direction = b - a;

					if ( direction.Length < Eps )
					{
						error = "One of the lines has no length.";
						return false;
					}

					var left = new Vec2( -direction.Normal.y, direction.Normal.x ) * distance;

					var start = sketch.AddPoint( a + left );
					var end = sketch.AddPoint( b + left );

					created.Add( sketch.Add( new SketchLine( start, end ) ) );
					ends.Add( (start, end) );
					break;
				}

				case SketchArc arc:
				{
					var (centre, radius) = SketchIntersect.CircleOf( sketch, arc );

					// Travelling counter-clockwise puts the centre on the left, so a positive
					// offset moves toward it and the radius shrinks. Clockwise is the mirror.
					var sweep = SketchIntersect.ArcSweep( sketch, arc );
					var moved = sweep > 0f ? radius - distance : radius + distance;

					if ( moved < Eps )
					{
						error = $"An offset of {distance} collapses an arc of radius {radius:0.###}.";
						return false;
					}

					var start = sketch.AddPoint( centre + (sketch.Points[arc.Start] - centre).Normal * moved );
					var end = sketch.AddPoint( centre + (sketch.Points[arc.End] - centre).Normal * moved );

					created.Add( sketch.Add( new SketchArc( arc.Center, start, end, arc.Clockwise ) ) );
					ends.Add( (start, end) );
					break;
				}
			}
		}

		var openJoints = 0;

		for ( var i = 0; i + 1 < created.Count; i++ )
		{
			// Only stitch a joint the originals actually shared. Two curves selected together but
			// not touching are two separate offsets, and dragging their ends together would be an
			// invention rather than an offset.
			if ( !Touching( chain[i], chain[i + 1] ) )
				continue;

			if ( !Meet( sketch, created[i], created[i + 1], out var join ) )
			{
				openJoints++;
				continue;
			}

			sketch.Points[ends[i].End] = join;
			sketch.Points[ends[i + 1].Start] = join;
		}

		if ( openJoints > 0 )
			error = $"{openJoints} corner(s) left open: the offset is wider than the feature it turns.";

		return true;
	}

	static bool Touching( SketchCurve a, SketchCurve b )
	{
		var (a0, a1) = a.Endpoints;
		var (b0, b1) = b.Endpoints;

		return a1 == b0 || a1 == b1 || a0 == b0 || a0 == b1;
	}

	/// <summary>Where two offset curves cross, taking the crossing nearest their shared corner when
	/// there is more than one — a circle and a line cross twice and only one of them is the joint
	/// being repaired.</summary>
	static bool Meet( Sketch sketch, SketchCurve a, SketchCurve b, out Vec2 point )
	{
		point = Vec2.Zero;

		// Bounded intersection first, which is the inside-corner case where the two overshoot and
		// genuinely cross.
		var hits = SketchIntersect.Between( sketch, a, b );

		if ( hits.Count > 0 )
		{
			point = hits[0].Point;
			return true;
		}

		// Outside corner: the two fall short, so they only meet once extended. Only lines can be
		// extended without ambiguity here, so an arc joint that falls short is reported open.
		if ( a is not SketchLine la || b is not SketchLine lb )
			return false;

		var p = sketch.Points[la.Start];
		var d = sketch.Points[la.End] - p;
		var q = sketch.Points[lb.Start];
		var e = sketch.Points[lb.End] - q;

		var denom = Vec2.Cross( d, e );

		if ( MathF.Abs( denom ) < Eps )
			return false;

		point = p + d * (Vec2.Cross( q - p, e ) / denom);

		return true;
	}

	static float ProbeLength( Sketch sketch )
	{
		var extent = 1f;

		foreach ( var p in sketch.Points )
			extent = MathF.Max( extent, MathF.Max( MathF.Abs( p.x ), MathF.Abs( p.y ) ) );

		return extent * 4f + 10f;
	}

	static float Angle( Vec2 v )
	{
		var a = MathF.Atan2( v.y, v.x );

		return a < 0f ? a + MathF.Tau : a;
	}

	static Vec2 PointAt( Sketch sketch, SketchCurve curve, float t ) => curve switch
	{
		SketchLine line => sketch.Points[line.Start] + (sketch.Points[line.End] - sketch.Points[line.Start]) * t,
		SketchArc arc => ArcPoint( sketch, arc, t ),
		_ => throw new InvalidOperationException( $"{curve.GetType().Name} has no point-at" )
	};

	static Vec2 ArcPoint( Sketch sketch, SketchArc arc, float t )
	{
		var centre = sketch.Points[arc.Center];
		var radius = arc.Radius( sketch );
		var start = MathF.Atan2( sketch.Points[arc.Start].y - centre.y, sketch.Points[arc.Start].x - centre.x );
		var angle = start + SketchIntersect.ArcSweep( sketch, arc ) * t;

		return new Vec2( centre.x + MathF.Cos( angle ) * radius, centre.y + MathF.Sin( angle ) * radius );
	}

	static Vec2 EndPoint( Sketch sketch, SketchCurve curve, bool atStart )
	{
		var (a, b) = curve.Endpoints;

		return sketch.Points[atStart ? a : b];
	}

	/// <summary>
	/// Move one end of a curve to a new position, by moving the POINT it references rather than by
	/// repointing the curve at a new index. That keeps every constraint attached to that end
	/// attached, which is the difference between trimming a line and quietly deleting the rules
	/// that were holding it.
	/// </summary>
	static bool MoveEnd( Sketch sketch, SketchCurve curve, bool atStart, Vec2 to, out string error )
	{
		error = null;

		var (a, b) = curve.Endpoints;
		var index = atStart ? a : b;

		if ( index < 0 || index >= sketch.Points.Count )
		{
			error = "That curve has no end to move.";
			return false;
		}

		sketch.Points[index] = to;

		return true;
	}
}
