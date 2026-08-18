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
/// Constraints are not here yet — this is the geometry and topology layer, deliberately shipped
/// before the solver so the sketch-to-extrude loop works end to end while the solver is being
/// built. Typed coordinates go in, profiles come out. The solver's job when it lands is to let
/// those coordinates be derived rather than typed; nothing in this file has to change for it.
/// </summary>
public sealed class Sketch
{
	public SketchPlane Plane = SketchPlane.XY;
	public List<Vec2> Points = new();
	public List<SketchCurve> Curves = new();

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
		Tolerance = Tolerance
	};
}
