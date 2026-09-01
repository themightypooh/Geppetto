using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A curve in a sketch. Curves reference points by index into Sketch.Points rather than owning
/// their own copies.
///
/// SHARED POINTS ARE THE IMPORTANT DECISION HERE. Two lines meeting at a corner reference the same
/// point index, so:
///   - coincidence is identity, not a constraint that has to be solved and can drift
///   - dragging a corner moves both lines, with no bookkeeping
///   - loop finding is a graph walk over indices instead of a floating-point match
///   - the solver later gets a clean 2-DOF-per-point problem
///
/// Onshape models coincidence as a real constraint between separate points, which is more general
/// — it can be deleted. Trading that away removes an entire class of "my sketch fell apart"
/// failure before the solver exists to cause it.
/// </summary>
public abstract class SketchCurve
{
	public string Id = Guid.NewGuid().ToString( "N" )[..8];

	/// <summary>Construction geometry guides other entities but is not part of any profile — the
	/// blue dashed lines in Onshape. Excluded from loop finding.</summary>
	public bool Construction;

	public abstract IEnumerable<int> PointRefs { get; }

	/// <summary>
	/// This curve closes on itself and is a region all by itself — a circle, an ellipse, a closed
	/// spline. Loop finding takes these straight to a loop rather than walking them.
	/// </summary>
	public virtual bool IsClosed => false;

	/// <summary>
	/// The two points a loop walk enters and leaves this curve by. Meaningless for a closed curve,
	/// which returns (-1, -1) and is filtered out by IsClosed before anything asks.
	///
	/// NOT PointRefs. An arc's centre is a point it references and is emphatically not somewhere a
	/// walk can arrive from, and a spline's interior points are the same. Profile finding used to
	/// switch over the curve types to get at this, which meant every new kind of curve was a
	/// change to loop finding as well as a new class. Asking the curve is what makes a new curve
	/// one file.
	/// </summary>
	public virtual (int A, int B) Endpoints => (-1, -1);

	/// <summary>Sample into a polyline in plane coordinates. Returns points from start to end
	/// INCLUSIVE of both, so consecutive curves in a loop overlap by one point and the walker can
	/// stitch them without special cases.</summary>
	public abstract List<Vec2> Tessellate( Sketch sketch, float tolerance );

	public abstract SketchCurve Clone();
}

public enum SketchConstraintKind
{
	Horizontal,
	Vertical,
	Coincident,
	Distance,
	EqualLength,
	Parallel,
	Perpendicular,

	/// <summary>A fixed angle between two lines, in degrees. Parallel and Perpendicular are the
	/// same rule at 0 and 90, kept separate because a user asking for "parallel" is not asking for
	/// "0 degrees" and should not have to see it that way.</summary>
	Angle,

	/// <summary>A point lies somewhere on the infinite line through two others.</summary>
	PointOnLine,

	/// <summary>Two points mirror each other across the line through two others.</summary>
	Symmetric,

	/// <summary>An arc's radius: the distance from its centre to an endpoint. Stored as its own
	/// kind rather than as a Distance because that is what the user asked for and what a dimension
	/// should read back as, even though it solves identically.</summary>
	Radius,

	/// <summary>An arc's diameter. Radius at twice the value, kept apart for the same reason
	/// Radius is kept apart from Distance — a dimension has to read back as the thing that was
	/// typed.</summary>
	Diameter,

	/// <summary>A point half way between two others.</summary>
	Midpoint,

	/// <summary>Two arcs or circles share a centre. Coincident on the centre points, named for
	/// what the user asked for.</summary>
	Concentric,

	/// <summary>A point nailed to an absolute coordinate. Uses both Value and ValueY.</summary>
	Fixed,

	/// <summary>
	/// A line tangent to an arc or circle. PointA/PointB are the line's ends, PointC the centre and
	/// PointD a point on the rim.
	///
	/// Split from TangentArcs rather than sharing one kind with a discriminator, because the two
	/// read their four point slots completely differently and a stored record that means two
	/// different things depending on a fifth field is the kind of thing that survives review and
	/// then breaks a file three months later. ConstraintTools picks between them from the
	/// selection, which is where that decision belongs.
	/// </summary>
	Tangent,

	/// <summary>Two arcs or circles tangent to each other: centre A, rim A, centre B, rim B.
	/// Value is non-zero for internal tangency — one inside the other.</summary>
	TangentArcs,
}

/// <summary>
/// A persistent geometric rule on a sketch. Storage only — the residual and its derivative live in
/// an IConstraint that Build() produces, so this stays a plain record that a file format or an undo
/// snapshot can round-trip.
///
/// Two addressing forms, for one historical reason. Horizontal and Vertical were stored against a
/// CURVE id before there was a solver, because that was all the inference needed to draw a glyph
/// next to a line. Everything since is stored against POINT indices, which is what the solver
/// actually operates on and what lets one constraint span two curves. Build() resolves the old form
/// through the curve list, so sketches saved before the solver landed still solve.
/// </summary>
public sealed class SketchConstraint
{
	public SketchConstraintKind Kind;

	/// <summary>The old H/V-on-a-curve form. Prefer the point indices below.</summary>
	public string CurveId;

	/// <summary>Point indices this acts on. Two for coincident and distance; four for the ones
	/// that relate a pair of lines. Unused slots stay -1.</summary>
	public int PointA = -1, PointB = -1, PointC = -1, PointD = -1;

	/// <summary>The driven value, for the kinds that carry one: Distance, Radius, Diameter, Angle,
	/// the x of a Fixed, and a non-zero flag for internal tangency on TangentArcs.</summary>
	public float Value;

	/// <summary>The second half of a two-number value. Only Fixed uses it, for the y coordinate —
	/// a fix is the one rule whose value is a position rather than a magnitude.</summary>
	public float ValueY;

	public SketchConstraint( SketchConstraintKind kind, string curveId )
	{
		Kind = kind;
		CurveId = curveId;
	}

	public SketchConstraint( SketchConstraintKind kind, int a, int b, float value = 0f )
	{
		Kind = kind;
		PointA = a;
		PointB = b;
		Value = value;
	}

	public SketchConstraint( SketchConstraintKind kind, int a, int b, int c, float value = 0f )
	{
		Kind = kind;
		PointA = a;
		PointB = b;
		PointC = c;
		Value = value;
	}

	public SketchConstraint( SketchConstraintKind kind, int a0, int a1, int b0, int b1 )
	{
		Kind = kind;
		PointA = a0;
		PointB = a1;
		PointC = b0;
		PointD = b1;
	}

	/// <summary>Build the runtime evaluator. Null when the constraint cannot be resolved — an old
	/// curve-id H/V whose curve has since been deleted, or point indices that no longer exist. The
	/// solver drops those rather than failing the solve, so deleting one line cannot wedge a whole
	/// sketch.</summary>
	public IConstraint Build( Sketch sketch )
	{
		switch ( Kind )
		{
			case SketchConstraintKind.Coincident:
				return Valid( sketch, 2 ) ? new CoincidentConstraint( PointA, PointB ) : null;

			case SketchConstraintKind.Distance:
				return Valid( sketch, 2 ) ? new DistanceConstraint( PointA, PointB, Value ) : null;

			case SketchConstraintKind.Horizontal:
				if ( Valid( sketch, 2 ) )
					return new HorizontalConstraint( PointA, PointB );
				return ResolveLineConstraint( sketch, horizontal: true );

			case SketchConstraintKind.Vertical:
				if ( Valid( sketch, 2 ) )
					return new VerticalConstraint( PointA, PointB );
				return ResolveLineConstraint( sketch, horizontal: false );

			case SketchConstraintKind.EqualLength:
				return Valid( sketch, 4 ) ? new EqualLengthConstraint( PointA, PointB, PointC, PointD ) : null;

			case SketchConstraintKind.Parallel:
				return Valid( sketch, 4 ) ? new ParallelConstraint( PointA, PointB, PointC, PointD ) : null;

			case SketchConstraintKind.Perpendicular:
				return Valid( sketch, 4 ) ? new PerpendicularConstraint( PointA, PointB, PointC, PointD ) : null;

			case SketchConstraintKind.Angle:
				return Valid( sketch, 4 ) ? new AngleConstraint( PointA, PointB, PointC, PointD, Value ) : null;

			case SketchConstraintKind.PointOnLine:
				return Valid( sketch, 3 ) ? new PointOnLineConstraint( PointA, PointB, PointC ) : null;

			case SketchConstraintKind.Symmetric:
				return Valid( sketch, 4 ) ? new SymmetricConstraint( PointA, PointB, PointC, PointD ) : null;

			case SketchConstraintKind.Radius:
				return Valid( sketch, 2 ) ? new DistanceConstraint( PointA, PointB, Value ) : null;

			case SketchConstraintKind.Diameter:
				return Valid( sketch, 2 ) ? new DistanceConstraint( PointA, PointB, Value * 0.5f ) : null;

			case SketchConstraintKind.Midpoint:
				return Valid( sketch, 3 ) ? new MidpointConstraint( PointA, PointB, PointC ) : null;

			case SketchConstraintKind.Concentric:
				return Valid( sketch, 2 ) ? new CoincidentConstraint( PointA, PointB ) : null;

			case SketchConstraintKind.Fixed:
				return Valid( sketch, 1 ) ? new FixedConstraint( PointA, Value, ValueY ) : null;

			case SketchConstraintKind.Tangent:
				return Valid( sketch, 4 ) ? new TangentLineArcConstraint( PointA, PointB, PointC, PointD ) : null;

			case SketchConstraintKind.TangentArcs:
				return Valid( sketch, 4 )
					? new TangentArcArcConstraint( PointA, PointB, PointC, PointD, Value != 0f )
					: null;

			default:
				return null;
		}
	}

	/// <summary>Every index this kind needs is present in the sketch. Checked here rather than in
	/// the solver so a stale constraint is dropped at exactly one place.</summary>
	bool Valid( Sketch sketch, int count )
	{
		var n = sketch.Points.Count;
		var refs = new[] { PointA, PointB, PointC, PointD };

		for ( var i = 0; i < count; i++ )
		{
			if ( refs[i] < 0 || refs[i] >= n )
				return false;
		}

		return true;
	}

	IConstraint ResolveLineConstraint( Sketch sketch, bool horizontal )
	{
		if ( string.IsNullOrEmpty( CurveId ) )
			return null;

		if ( sketch.Curves.Find( c => c.Id == CurveId ) is not SketchLine line )
			return null;

		return horizontal
			? new HorizontalConstraint( line.Start, line.End )
			: new VerticalConstraint( line.Start, line.End );
	}

	public SketchConstraint Clone() => new( Kind, CurveId )
	{
		PointA = PointA,
		PointB = PointB,
		PointC = PointC,
		PointD = PointD,
		Value = Value,
		ValueY = ValueY
	};
}

public sealed class SketchLine : SketchCurve
{
	public int Start, End;

	public SketchLine( int start, int end )
	{
		Start = start;
		End = end;
	}

	public override IEnumerable<int> PointRefs => new[] { Start, End };

	public override (int A, int B) Endpoints => (Start, End);

	public override List<Vec2> Tessellate( Sketch sketch, float tolerance ) =>
		new() { sketch.Points[Start], sketch.Points[End] };

	public override SketchCurve Clone() => new SketchLine( Start, End ) { Id = Id, Construction = Construction };
}

/// <summary>
/// Circular arc from Start to End about Center, going counter-clockwise unless Clockwise is set.
///
/// The radius is taken from the centre-to-start distance and the end point is swept to match, so
/// an end point that does not sit exactly on the radius is tolerated rather than rejected. Without
/// that, every arc would need a solver just to be constructible by hand.
/// </summary>
public sealed class SketchArc : SketchCurve
{
	public int Center, Start, End;
	public bool Clockwise;

	public SketchArc( int center, int start, int end, bool clockwise = false )
	{
		Center = center;
		Start = start;
		End = end;
		Clockwise = clockwise;
	}

	public override IEnumerable<int> PointRefs => new[] { Center, Start, End };

	public override (int A, int B) Endpoints => (Start, End);

	public float Radius( Sketch sketch )
	{
		var c = sketch.Points[Center];
		var s = sketch.Points[Start];
		return new Vec2( s.x - c.x, s.y - c.y ) is var d ? MathF.Sqrt( d.x * d.x + d.y * d.y ) : 0f;
	}

	public override List<Vec2> Tessellate( Sketch sketch, float tolerance )
	{
		var c = sketch.Points[Center];
		var s = sketch.Points[Start];
		var e = sketch.Points[End];

		var radius = MathF.Sqrt( (s.x - c.x) * (s.x - c.x) + (s.y - c.y) * (s.y - c.y) );

		if ( radius < 1e-9f )
			return new List<Vec2> { s, e };

		var a0 = MathF.Atan2( s.y - c.y, s.x - c.x );
		var a1 = MathF.Atan2( e.y - c.y, e.x - c.x );
		var sweep = Sweep( a0, a1, Clockwise );

		var steps = SegmentsForArc( radius, MathF.Abs( sweep ), tolerance );
		var points = new List<Vec2>( steps + 1 );

		for ( var i = 0; i <= steps; i++ )
		{
			var a = a0 + sweep * (i / (float)steps);
			points.Add( new Vec2( c.x + MathF.Cos( a ) * radius, c.y + MathF.Sin( a ) * radius ) );
		}

		// Snap the ends onto the authored points so consecutive curves in a loop share an exact
		// position, whatever the radius fudge above did.
		points[0] = s;
		points[^1] = e;

		return points;
	}

	/// <summary>
	/// How far this arc turns, from its start angle to its end angle, in the requested direction.
	/// Signed: negative clockwise.
	///
	/// A ZERO SWEEP IS A FULL CIRCLE, not nothing. Coincident endpoints are how a whole circle is
	/// spelled as an arc, and the alternative reading - an arc that draws nothing at all - is not
	/// something anybody has ever asked a sketcher for.
	///
	/// Pulled out of Tessellate so the handle that drags an arc's bulge can find the middle of the
	/// same sweep the arc is drawn along. Two normalisations that were meant to agree, sitting in
	/// different files, is how a grip ends up on the far side of its own arc.
	/// </summary>
	public static float Sweep( float startAngle, float endAngle, bool clockwise )
	{
		var sweep = endAngle - startAngle;

		if ( clockwise )
		{
			while ( sweep > 0 ) sweep -= MathF.Tau;
			while ( sweep <= -MathF.Tau ) sweep += MathF.Tau;
			if ( MathF.Abs( sweep ) < 1e-6f ) sweep = -MathF.Tau;
		}
		else
		{
			while ( sweep < 0 ) sweep += MathF.Tau;
			while ( sweep >= MathF.Tau ) sweep -= MathF.Tau;
			if ( MathF.Abs( sweep ) < 1e-6f ) sweep = MathF.Tau;
		}

		return sweep;
	}

	/// <summary>Segment count from the sagitta — the gap between chord and arc. Fixed segment
	/// counts either over-tessellate small arcs or visibly facet large ones; deriving from the
	/// error keeps both right.</summary>
	public static int SegmentsForArc( float radius, float sweep, float tolerance )
	{
		if ( tolerance <= 0f || radius <= 0f )
			return Math.Max( 2, (int)MathF.Ceiling( sweep / 0.2f ) );

		var ratio = Math.Clamp( 1f - tolerance / radius, -1f, 1f );
		var maxAngle = 2f * MathF.Acos( ratio );

		if ( maxAngle < 1e-4f )
			maxAngle = 1e-4f;

		return Math.Clamp( (int)MathF.Ceiling( sweep / maxAngle ), 2, 4096 );
	}

	public override SketchCurve Clone() =>
		new SketchArc( Center, Start, End, Clockwise ) { Id = Id, Construction = Construction };
}

/// <summary>A full circle. Closed on its own, so it forms a loop by itself.</summary>
public sealed class SketchCircle : SketchCurve
{
	public int Center;
	public float Radius;

	public SketchCircle( int center, float radius )
	{
		Center = center;
		Radius = radius;
	}

	public override IEnumerable<int> PointRefs => new[] { Center };

	public override bool IsClosed => true;

	public override List<Vec2> Tessellate( Sketch sketch, float tolerance )
	{
		var c = sketch.Points[Center];
		var steps = SketchArc.SegmentsForArc( Radius, MathF.Tau, tolerance );
		var points = new List<Vec2>( steps + 1 );

		for ( var i = 0; i <= steps; i++ )
		{
			var a = i / (float)steps * MathF.Tau;
			points.Add( new Vec2( c.x + MathF.Cos( a ) * Radius, c.y + MathF.Sin( a ) * Radius ) );
		}

		return points;
	}

	public override SketchCurve Clone() =>
		new SketchCircle( Center, Radius ) { Id = Id, Construction = Construction };
}

/// <summary>
/// An ellipse, given as a centre, a point at the end of its major axis, and a minor radius.
///
/// THE MAJOR AXIS IS A POINT, NOT A NUMBER AND AN ANGLE. Storing it as a rim point means the major
/// radius AND the rotation are both ordinary sketch points the solver can see and drive — a
/// dimension on the major axis is just a Distance, and making an ellipse tangent to something is
/// the same TangentLineArc machinery pointing at a different rim. Stored as a length and an angle,
/// neither would be reachable by any constraint.
///
/// The minor radius stays a float, which is the same compromise SketchCircle.Radius already makes:
/// a second rim point would be more solvable but would also have to be kept perpendicular to the
/// first by a constraint that nothing would stop a user deleting. A circle whose radius cannot be
/// driven has been fine in practice, and this is that trade made twice rather than a new one.
/// </summary>
public sealed class SketchEllipse : SketchCurve
{
	public int Center, MajorPoint;
	public float MinorRadius;

	public SketchEllipse( int center, int majorPoint, float minorRadius )
	{
		Center = center;
		MajorPoint = majorPoint;
		MinorRadius = minorRadius;
	}

	public override IEnumerable<int> PointRefs => new[] { Center, MajorPoint };

	public override bool IsClosed => true;

	/// <summary>Distance from the centre to the major-axis point.</summary>
	public float MajorRadius( Sketch sketch )
	{
		var c = sketch.Points[Center];
		var m = sketch.Points[MajorPoint];

		return MathF.Sqrt( (m.x - c.x) * (m.x - c.x) + (m.y - c.y) * (m.y - c.y) );
	}

	public override List<Vec2> Tessellate( Sketch sketch, float tolerance )
	{
		var c = sketch.Points[Center];
		var m = sketch.Points[MajorPoint];

		var ax = m.x - c.x;
		var ay = m.y - c.y;
		var major = MathF.Sqrt( ax * ax + ay * ay );
		var minor = MathF.Abs( MinorRadius );

		if ( major < 1e-9f || minor < 1e-9f )
			return new List<Vec2> { c, c };

		// Unit vector along the major axis; the minor axis is it turned a quarter turn. Taking the
		// rotation from the point rather than from a stored angle is the whole reason the point
		// exists.
		var ux = ax / major;
		var uy = ay / major;

		// Segment count from the WORST curvature on the ellipse, which is at the ends of the major
		// axis where the effective radius is minor^2/major. Using the major radius instead would
		// under-tessellate exactly the two places that need it most, and a long thin ellipse would
		// come out as a hexagon with pointy ends.
		var sharpest = minor * minor / major;
		var steps = SketchArc.SegmentsForArc( MathF.Max( sharpest, 1e-6f ), MathF.Tau, tolerance );

		var points = new List<Vec2>( steps + 1 );

		for ( var i = 0; i <= steps; i++ )
		{
			var t = i / (float)steps * MathF.Tau;
			var px = MathF.Cos( t ) * major;
			var py = MathF.Sin( t ) * minor;

			points.Add( new Vec2( c.x + px * ux - py * uy, c.y + px * uy + py * ux ) );
		}

		return points;
	}

	public override SketchCurve Clone() =>
		new SketchEllipse( Center, MajorPoint, MinorRadius ) { Id = Id, Construction = Construction };
}

/// <summary>
/// A spline through a list of points — an interpolating Catmull-Rom, not a Bezier or a NURBS.
///
/// INTERPOLATING, BECAUSE A SKETCH POINT THE CURVE MISSES IS A LIE. A B-spline or Bezier's control
/// points sit off the curve, so a dimension on one does not measure the shape and a coincidence
/// with one does not touch it. Every point here is ON the curve, which means every constraint that
/// already exists — coincident, distance, midpoint, point-on-line — means the obvious thing when
/// pointed at a spline point, and the solver needed no changes at all to drive one.
///
/// CENTRIPETAL PARAMETERISATION rather than uniform. Uniform Catmull-Rom overshoots into a visible
/// loop when consecutive points are unevenly spaced, and unevenly spaced is what hand-placed sketch
/// points always are. Centripetal (the exponent of a half below) is the standard fix and provably
/// never self-intersects between two points.
///
/// The ends are handled by reflecting a phantom point outward rather than by duplicating the end
/// point. Duplicating gives a zero-length segment, and the parameterisation divides by its length.
/// </summary>
public sealed class SketchSpline : SketchCurve
{
	public List<int> Points = new();

	/// <summary>Joins its last point back to its first, making it a region on its own.</summary>
	public bool Closed;

	public SketchSpline( IEnumerable<int> points, bool closed = false )
	{
		Points = points.ToList();
		Closed = closed;
	}

	public override IEnumerable<int> PointRefs => Points;

	public override bool IsClosed => Closed;

	public override (int A, int B) Endpoints =>
		Closed || Points.Count < 2 ? (-1, -1) : (Points[0], Points[^1]);

	public override List<Vec2> Tessellate( Sketch sketch, float tolerance )
	{
		var knots = Points.Select( i => sketch.Points[i] ).ToList();

		if ( knots.Count == 0 )
			return new List<Vec2>();

		if ( knots.Count == 1 )
			return new List<Vec2> { knots[0], knots[0] };

		// Two points have no curvature to resolve and are a straight line however they are
		// parameterised. Saying so here keeps the phantom-point logic below off a degenerate case.
		if ( knots.Count == 2 && !Closed )
			return new List<Vec2> { knots[0], knots[1] };

		var output = new List<Vec2>();
		var spans = Closed ? knots.Count : knots.Count - 1;

		for ( var i = 0; i < spans; i++ )
		{
			var p0 = Neighbour( knots, i - 1, i, i + 1 );
			var p1 = knots[Wrap( i, knots.Count )];
			var p2 = knots[Wrap( i + 1, knots.Count )];
			var p3 = Neighbour( knots, i + 2, i + 1, i );

			var steps = StepsFor( p1, p2, tolerance );

			// The last sample of a span is the first of the next, so every span but the final one
			// stops short of its end and lets the next span contribute that point exactly once.
			var last = i == spans - 1 ? steps : steps - 1;

			for ( var s = 0; s <= last; s++ )
				output.Add( Sample( p0, p1, p2, p3, s / (float)steps ) );
		}

		// Snap the ends onto the authored points. The arithmetic above lands on them to within
		// rounding, and a loop walk compares positions, so "within rounding" is not good enough.
		output[0] = knots[Closed ? 0 : 0];
		output[^1] = Closed ? knots[0] : knots[^1];

		return output;
	}

	int Wrap( int i, int count ) => Closed ? ((i % count) + count) % count : Math.Clamp( i, 0, count - 1 );

	/// <summary>
	/// The point outside a span, used to give the span its tangents. On a closed spline it wraps;
	/// on an open one there is nothing beyond the end, so the phantom point is the end reflected
	/// through its neighbour — which continues the curve straight rather than pinning it flat.
	/// </summary>
	Vec2 Neighbour( List<Vec2> knots, int want, int edge, int inward )
	{
		if ( Closed )
			return knots[Wrap( want, knots.Count )];

		if ( want >= 0 && want < knots.Count )
			return knots[want];

		var e = knots[Math.Clamp( edge, 0, knots.Count - 1 )];
		var i2 = knots[Math.Clamp( inward, 0, knots.Count - 1 )];

		return new Vec2( e.x + (e.x - i2.x), e.y + (e.y - i2.y) );
	}

	/// <summary>
	/// Samples per span, from the chord length against the tolerance. A cubic's deviation from its
	/// chord has no closed form worth deriving here, so this is the arc heuristic reused with the
	/// chord standing in for the radius — generous on gentle spans and correct in the direction
	/// that matters on tight ones.
	/// </summary>
	static int StepsFor( Vec2 a, Vec2 b, float tolerance )
	{
		var chord = MathF.Sqrt( (b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y) );

		if ( chord < 1e-9f )
			return 1;

		if ( tolerance <= 0f )
			return 16;

		return Math.Clamp( (int)MathF.Ceiling( chord / MathF.Max( tolerance * 8f, 1e-6f ) ), 4, 256 );
	}

	/// <summary>
	/// Centripetal Catmull-Rom, evaluated by the Barry-Goldman pyramid rather than by a basis
	/// matrix. The matrix form assumes uniform knots and is exactly what this is avoiding.
	/// </summary>
	static Vec2 Sample( Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, float t )
	{
		var t0 = 0f;
		var t1 = t0 + Knot( p0, p1 );
		var t2 = t1 + Knot( p1, p2 );
		var t3 = t2 + Knot( p2, p3 );

		var tt = t1 + (t2 - t1) * t;

		var a1 = Lerp( p0, p1, t0, t1, tt );
		var a2 = Lerp( p1, p2, t1, t2, tt );
		var a3 = Lerp( p2, p3, t2, t3, tt );

		var b1 = Lerp( a1, a2, t0, t2, tt );
		var b2 = Lerp( a2, a3, t1, t3, tt );

		return Lerp( b1, b2, t1, t2, tt );
	}

	/// <summary>Knot spacing: the square root of the chord length, which is what makes this
	/// centripetal rather than uniform. Floored so coincident points cannot divide by zero.</summary>
	static float Knot( Vec2 a, Vec2 b )
	{
		var dx = b.x - a.x;
		var dy = b.y - a.y;

		return MathF.Max( MathF.Sqrt( MathF.Sqrt( dx * dx + dy * dy ) ), 1e-5f );
	}

	static Vec2 Lerp( Vec2 a, Vec2 b, float ta, float tb, float t )
	{
		var span = tb - ta;

		if ( MathF.Abs( span ) < 1e-9f )
			return a;

		var u = (t - ta) / span;

		return new Vec2( a.x + (b.x - a.x) * u, a.y + (b.y - a.y) * u );
	}

	public override SketchCurve Clone() =>
		new SketchSpline( Points, Closed ) { Id = Id, Construction = Construction };
}

/// <summary>
/// A 2D sketch on a plane. The thing Extrude and Revolve consume.
///
/// Geometry and topology; constraints sit alongside as a list of rules rather than being baked into
/// the coordinates. Points always hold a concrete position — SketchSolver moves them to satisfy the
/// constraints, and everything downstream reads the same Points list either way. That is what let
/// the sketch-to-extrude loop ship before the solver did, and why nothing here had to change when
/// the solver landed.
/// </summary>
public sealed class Sketch
{
	public SketchPlane Plane = SketchPlane.XY;
	public List<Vec2> Points = new();
	public List<SketchCurve> Curves = new();
	public List<SketchConstraint> Constraints = new();

	/// <summary>Max deviation when sampling arcs into polylines, in sketch units.</summary>
	public float Tolerance = 0.01f;

	public int AddPoint( Vec2 p )
	{
		Points.Add( p );
		return Points.Count - 1;
	}

	public int AddPoint( float x, float y ) => AddPoint( new Vec2( x, y ) );

	public T Add<T>( T curve ) where T : SketchCurve
	{
		Curves.Add( curve );
		return curve;
	}

	public SketchConstraint AddConstraint( SketchCurve curve, SketchConstraintKind kind )
	{
		var existing = Constraints.FirstOrDefault( c => c.CurveId == curve.Id && c.Kind == kind );
		if ( existing is not null )
			return existing;

		var constraint = new SketchConstraint( kind, curve.Id );
		Constraints.Add( constraint );
		return constraint;
	}

	/// <summary>A constraint between two points — coincident, or a driven distance.</summary>
	public SketchConstraint AddConstraint( SketchConstraintKind kind, int a, int b, float value = 0f )
	{
		var constraint = new SketchConstraint( kind, a, b, value );
		Constraints.Add( constraint );
		return constraint;
	}

	/// <summary>A constraint relating two lines, given as their four endpoints: equal length,
	/// parallel, perpendicular.</summary>
	public SketchConstraint AddConstraint( SketchConstraintKind kind, SketchLine a, SketchLine b )
	{
		var constraint = new SketchConstraint( kind, a.Start, a.End, b.Start, b.End );
		Constraints.Add( constraint );
		return constraint;
	}

	/// <summary>Solve the constraints, moving points to satisfy them. Convenience for
	/// SketchSolver.Solve( this ); a sketch with no constraints is a no-op.</summary>
	public SolveResult Solve() => SketchSolver.Solve( this );

	/// <summary>Line between two new points, the common case when typing coordinates.</summary>
	public SketchLine AddLine( Vec2 a, Vec2 b ) => Add( new SketchLine( AddPoint( a ), AddPoint( b ) ) );

	/// <summary>Closed polygon through the given points, sharing each corner between its two
	/// edges. This is the shape most sketches actually are.</summary>
	public List<SketchLine> AddPolygon( params Vec2[] corners )
	{
		if ( corners.Length < 3 )
			throw new ArgumentException( "A polygon needs at least 3 corners" );

		var indices = corners.Select( AddPoint ).ToList();
		var lines = new List<SketchLine>();

		for ( var i = 0; i < indices.Count; i++ )
			lines.Add( Add( new SketchLine( indices[i], indices[(i + 1) % indices.Count] ) ) );

		return lines;
	}

	public List<SketchLine> AddRectangle( Vec2 min, Vec2 max ) => AddPolygon(
		new Vec2( min.x, min.y ),
		new Vec2( max.x, min.y ),
		new Vec2( max.x, max.y ),
		new Vec2( min.x, max.y ) );

	public SketchCircle AddCircle( Vec2 centre, float radius ) =>
		Add( new SketchCircle( AddPoint( centre ), radius ) );

	/// <summary>Ellipse from a centre, the end of its major axis, and a minor radius.</summary>
	public SketchEllipse AddEllipse( Vec2 centre, Vec2 majorEnd, float minorRadius ) =>
		Add( new SketchEllipse( AddPoint( centre ), AddPoint( majorEnd ), minorRadius ) );

	/// <summary>Spline through the given points, in order. Every point is on the curve.</summary>
	public SketchSpline AddSpline( bool closed, params Vec2[] through )
	{
		if ( through.Length < 2 )
			throw new ArgumentException( "A spline needs at least 2 points" );

		return Add( new SketchSpline( through.Select( AddPoint ).ToList(), closed ) );
	}

	public Sketch Clone() => new()
	{
		Plane = Plane.Clone(),
		Points = new List<Vec2>( Points ),
		Curves = Curves.Select( c => c.Clone() ).ToList(),
		Constraints = Constraints.Select( c => c.Clone() ).ToList(),
		Tolerance = Tolerance
	};
}
