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

	/// <summary>The driven value, for the kinds that carry one. Only Distance uses it today.</summary>
	public float Value;

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
		Value = Value
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
		var sweep = a1 - a0;

		// Normalise the sweep into the requested direction. A zero sweep means the endpoints
		// coincide, which is a full circle rather than nothing.
		if ( Clockwise )
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

	public Sketch Clone() => new()
	{
		Plane = Plane.Clone(),
		Points = new List<Vec2>( Points ),
		Curves = Curves.Select( c => c.Clone() ).ToList(),
		Constraints = Constraints.Select( c => c.Clone() ).ToList(),
		Tolerance = Tolerance
	};
}
