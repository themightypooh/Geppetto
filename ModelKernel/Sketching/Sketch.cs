using System;
using System.Collections.Generic;

namespace ModelKernel;

/// <summary>
/// The plane a sketch is drawn on: an origin and two orthonormal axes. The sketch works in 2D
/// against these, and only becomes 3D when something extrudes it.
///
/// Storing axes rather than just a normal is deliberate — the U axis IS the sketch's sense of
/// "right", so a profile keeps its orientation when the plane moves. A plane defined by a normal
/// alone has to invent U every time, and the invented one swings around as the normal rotates,
/// which spins the sketch on it for no reason the user can see.
/// </summary>
public readonly struct SketchPlane
{
	public readonly Vec3 Origin, U, V;

	public SketchPlane( Vec3 origin, Vec3 u, Vec3 v )
	{
		Origin = origin;
		U = u.Normal;
		V = v.Normal;
	}

	public Vec3 Normal => Vec3.Cross( U, V ).Normal;

	public Vec3 ToWorld( Vec2 p ) => Origin + U * p.x + V * p.y;

	/// <summary>Project a world point back onto the plane's 2D coordinates. Used when something
	/// picks a point in the viewport and the sketch has to receive it.</summary>
	public Vec2 ToLocal( Vec3 p )
	{
		var d = p - Origin;
		return new Vec2( Vec3.Dot( d, U ), Vec3.Dot( d, V ) );
	}

	public static readonly SketchPlane XY = new( Vec3.Zero, new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ) );
	public static readonly SketchPlane XZ = new( Vec3.Zero, new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) );
	public static readonly SketchPlane YZ = new( Vec3.Zero, new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ) );
}

/// <summary>
/// A closed loop of points in sketch space.
///
/// NO CONSTRAINTS, AND THAT IS THE POINT OF SHIPPING THIS FIRST. A sketch with a real constraint
/// solver — perpendicular, tangent, equal, dimension-driven — is a nonlinear numerical project with
/// under- and over-constrained detection attached. A sketch that is just points you place on a grid
/// and type dimensions into gives the entire sketch-then-extrude workflow for a fraction of the
/// work, and the solver bolts on later WITHOUT changing anything downstream of here: extrude reads
/// points, and a solver would only decide where those points end up.
///
/// Points are stored without repeating the first at the end; the loop is implicitly closed.
/// </summary>
public sealed class Profile
{
	public List<Vec2> Points = new();

	public Profile() { }

	public Profile( IEnumerable<Vec2> points )
	{
		Points.AddRange( points );
	}

	public int Count => Points.Count;

	public Profile Add( float x, float y )
	{
		Points.Add( new Vec2( x, y ) );
		return this;
	}

	/// <summary>
	/// Twice the enclosed area, signed. Positive means counter-clockwise.
	///
	/// The sign is what matters: it decides which way the extruded solid's faces point, and getting
	/// it wrong produces a model that renders inside-out — correct in wireframe, black when lit.
	/// Extrude normalises the winding rather than trusting the caller, because a user drawing a
	/// rectangle clockwise has done nothing wrong.
	/// </summary>
	public float SignedDoubleArea()
	{
		var total = 0f;

		for ( var i = 0; i < Points.Count; i++ )
		{
			var a = Points[i];
			var b = Points[(i + 1) % Points.Count];
			total += a.x * b.y - b.x * a.y;
		}

		return total;
	}

	public float Area => MathF.Abs( SignedDoubleArea() ) * 0.5f;
	public bool IsCounterClockwise => SignedDoubleArea() > 0f;

	/// <summary>A copy wound counter-clockwise, whichever way this one was drawn.</summary>
	public Profile AsCounterClockwise()
	{
		if ( IsCounterClockwise )
			return new Profile( Points );

		var reversed = new List<Vec2>( Points );
		reversed.Reverse();
		return new Profile( reversed );
	}

	/// <summary>
	/// What is wrong with this profile, as data rather than an exception — the same posture as
	/// MeshValidator, because a profile mid-draw is allowed to be briefly nonsense.
	///
	/// Self-intersection is NOT checked. It is the one failure that produces a plausible-looking
	/// mesh with inverted regions, and detecting it properly is a sweep-line algorithm. Worth doing
	/// when sketching gets a real UI; noted here so its absence is a decision rather than an
	/// oversight.
	/// </summary>
	public List<string> Validate()
	{
		var errors = new List<string>();

		if ( Points.Count < 3 )
			errors.Add( $"a profile needs at least 3 points, has {Points.Count}" );

		if ( Points.Count >= 3 && Area < 1e-9f )
			errors.Add( "profile encloses no area — the points may be collinear" );

		for ( var i = 0; i < Points.Count; i++ )
		{
			var a = Points[i];
			var b = Points[(i + 1) % Points.Count];

			if ( (b - a).LengthSquared < 1e-12f )
				errors.Add( $"points {i} and {(i + 1) % Points.Count} are in the same place" );
		}

		return errors;
	}

	/// <summary>A copy with consecutive coincident points removed, including across the closing
	/// seam. Generated profiles hit this at their limits; hand-drawn ones hit it when someone
	/// double-clicks.</summary>
	public Profile WithoutDuplicatePoints( float tolerance = 1e-6f )
	{
		var kept = new List<Vec2>( Points.Count );

		foreach ( var p in Points )
		{
			if ( kept.Count > 0 && (p - kept[^1]).LengthSquared < tolerance * tolerance )
				continue;

			kept.Add( p );
		}

		while ( kept.Count > 1 && (kept[0] - kept[^1]).LengthSquared < tolerance * tolerance )
			kept.RemoveAt( kept.Count - 1 );

		return new Profile( kept );
	}

	public Profile Clone() => new( Points );

	// --- ready-made profiles, so the common cases are not hand-typed ----------------------

	public static Profile Rectangle( float width, float height )
	{
		var hw = width * 0.5f;
		var hh = height * 0.5f;

		return new Profile( new[]
		{
			new Vec2( -hw, -hh ), new Vec2( hw, -hh ), new Vec2( hw, hh ), new Vec2( -hw, hh )
		} );
	}

	public static Profile Circle( float radius, int segments = 16 )
	{
		segments = Math.Max( 3, segments );
		var points = new List<Vec2>( segments );

		for ( var i = 0; i < segments; i++ )
		{
			var a = i / (float)segments * MathF.Tau;
			points.Add( new Vec2( MathF.Cos( a ) * radius, MathF.Sin( a ) * radius ) );
		}

		return new Profile( points );
	}

	/// <summary>A rounded rectangle — the shape almost every prop starts as, and painful to place
	/// by hand.</summary>
	public static Profile RoundedRectangle( float width, float height, float radius, int cornerSegments = 4 )
	{
		var hw = width * 0.5f;
		var hh = height * 0.5f;

		radius = MathF.Min( radius, MathF.Min( hw, hh ) );

		if ( radius <= 1e-6f )
			return Rectangle( width, height );

		cornerSegments = Math.Max( 1, cornerSegments );
		var points = new List<Vec2>();

		// Corner centres, counter-clockwise from bottom-right, each swept a quarter turn.
		var corners = new[]
		{
			(new Vec2( hw - radius, -hh + radius ), -MathF.PI / 2f),
			(new Vec2( hw - radius, hh - radius ), 0f),
			(new Vec2( -hw + radius, hh - radius ), MathF.PI / 2f),
			(new Vec2( -hw + radius, -hh + radius ), MathF.PI )
		};

		foreach ( var (centre, start) in corners )
		{
			for ( var s = 0; s <= cornerSegments; s++ )
			{
				var a = start + s / (float)cornerSegments * (MathF.PI / 2f);
				points.Add( new Vec2( centre.x + MathF.Cos( a ) * radius, centre.y + MathF.Sin( a ) * radius ) );
			}
		}

		// At the maximum radius the four corner centres coincide and each arc ends exactly where the
		// next begins, so the joins double up. Harmless to look at, but a duplicate point is a
		// zero-length edge and a zero-area face downstream, so they come out here.
		return new Profile( points ).WithoutDuplicatePoints();
	}
}

/// <summary>
/// A plane and the profile drawn on it. One outer loop for now — inner loops (holes) need the cap
/// to be triangulated with the hole bridged, which is a real algorithm and not this week's.
/// Subtracting a second solid covers most hole cases in the meantime.
/// </summary>
public sealed class Sketch
{
	public SketchPlane Plane = SketchPlane.XY;
	public Profile Outer = new();

	public Sketch() { }

	public Sketch( SketchPlane plane, Profile outer )
	{
		Plane = plane;
		Outer = outer;
	}

	public Sketch Clone() => new( Plane, Outer.Clone() );
}
