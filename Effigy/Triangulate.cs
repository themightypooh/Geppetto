using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Ear clipping for simple polygons — the triangulation every part of the tool that turns an
/// n-gon face into triangles needs.
///
/// WHY THIS EXISTS: everything used to fan from corner 0, on the stated grounds that "Effigy's
/// faces are convex". That is true of primitives and of extrude side walls, and it is NOT true of
/// an extrude cap, which is whatever closed region the user drew. Fanning a concave profile fills
/// its notches in: draw a dart and the extrude comes back as a quadrilateral with the concave
/// corner swallowed. Ear clipping handles any simple polygon, convex or not.
///
/// Holes are still not supported — SketchConsumingFeature refuses profiles with inner loops
/// before they ever reach here.
/// </summary>
public static class Triangulate
{
	/// <summary>
	/// Triangulate a polygon given in plane coordinates. Returns index triples INTO THE INPUT
	/// LIST, wound the same way the input is, so a caller can map them straight back onto its own
	/// vertices without worrying about which way the face faces.
	/// </summary>
	public static List<(int A, int B, int C)> Polygon( IReadOnlyList<Vec2> points )
	{
		var triangles = new List<(int, int, int)>( Math.Max( points.Count - 2, 0 ) );

		if ( points.Count < 3 )
			return triangles;

		// Ear clipping needs a known winding to tell "convex corner" from "reflex corner". Work
		// counter-clockwise internally and let the caller keep whatever winding it had.
		//
		// THAT SECOND HALF IS NOT FREE, and it read as free for as long as nothing exercised it.
		// Walking a clockwise polygon backwards makes every emitted triple counter-clockwise, so
		// the output silently disagreed with the input about which way the surface faces — and a
		// caller mapping those indices back onto its own vertices got a face pointing the wrong
		// way, which under backface culling is an invisible one. It never showed because every
		// caller today feeds counter-clockwise: Face() flattens onto the face's own Newell normal,
		// which is counter-clockwise by construction, and ProfileFinder orients outer loops the
		// same way. Hole loops are clockwise, and they are the next thing to arrive here.
		//
		// So the ring is walked backwards to find the ears, and each triple is emitted back in the
		// caller's own winding.
		var indices = new List<int>( points.Count );
		var reversed = SignedArea( points ) < 0f;

		if ( reversed )
		{
			for ( var i = points.Count - 1; i >= 0; i-- )
				indices.Add( i );
		}
		else
		{
			for ( var i = 0; i < points.Count; i++ )
				indices.Add( i );
		}

		void Emit( int a, int b, int c ) => triangles.Add( reversed ? (c, b, a) : (a, b, c) );

		// Each pass round the remaining ring clips at most one ear, so this cannot run longer than
		// n passes over an n-gon. The guard is for degenerate input - repeated or collinear points
		// can leave a ring with no ear at all, and a silent infinite loop inside a viewport frame
		// is not something anyone gets to debug comfortably.
		var guard = indices.Count * indices.Count + 16;

		while ( indices.Count > 3 && guard-- > 0 )
		{
			var clipped = false;

			for ( var i = 0; i < indices.Count; i++ )
			{
				var prev = indices[(i - 1 + indices.Count) % indices.Count];
				var cur = indices[i];
				var next = indices[(i + 1) % indices.Count];

				if ( !IsEar( points, indices, prev, cur, next ) )
					continue;

				Emit( prev, cur, next );
				indices.RemoveAt( i );
				clipped = true;
				break;
			}

			if ( clipped )
				continue;

			// No ear anywhere: the polygon is self-intersecting or degenerate. Fall back to a fan
			// so the caller still gets a surface rather than nothing at all.
			break;
		}

		if ( indices.Count == 3 )
		{
			Emit( indices[0], indices[1], indices[2] );
			return triangles;
		}

		for ( var i = 2; i < indices.Count; i++ )
			Emit( indices[0], indices[i - 1], indices[i] );

		return triangles;
	}

	/// <summary>
	/// Triangulate a face given as 3D positions, by flattening it onto its own best-fit plane
	/// first. Newell's method rather than a cross product of the first three points: three
	/// consecutive corners of a real face are often nearly collinear, and their cross product is
	/// then numerical noise pointing anywhere.
	/// </summary>
	public static List<(int A, int B, int C)> Face( IReadOnlyList<Vec3> positions )
	{
		if ( positions.Count < 3 )
			return new List<(int, int, int)>();

		var normal = NewellNormal( positions );

		if ( normal.LengthSquared < 1e-20f )
			normal = new Vec3( 0, 0, 1 );

		normal = normal.Normal;

		// Any two axes perpendicular to the normal will do; pick the one furthest from it to seed
		// them so the cross product is well conditioned.
		var seed = MathF.Abs( normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var u = Vec3.Cross( seed, normal ).Normal;
		var v = Vec3.Cross( normal, u );

		var flat = new List<Vec2>( positions.Count );

		foreach ( var p in positions )
			flat.Add( new Vec2( Vec3.Dot( p, u ), Vec3.Dot( p, v ) ) );

		return Polygon( flat );
	}

	static Vec3 NewellNormal( IReadOnlyList<Vec3> points )
	{
		var n = new Vec3( 0, 0, 0 );

		for ( var i = 0; i < points.Count; i++ )
		{
			var a = points[i];
			var b = points[(i + 1) % points.Count];

			n = new Vec3(
				n.x + (a.y - b.y) * (a.z + b.z),
				n.y + (a.z - b.z) * (a.x + b.x),
				n.z + (a.x - b.x) * (a.y + b.y) );
		}

		return n;
	}

	static float SignedArea( IReadOnlyList<Vec2> points )
	{
		var sum = 0f;

		for ( var i = 0; i < points.Count; i++ )
		{
			var a = points[i];
			var b = points[(i + 1) % points.Count];
			sum += a.x * b.y - b.x * a.y;
		}

		return sum * 0.5f;
	}

	/// <summary>A corner is an ear when it turns the same way the polygon does and no other
	/// remaining corner is inside the triangle it would cut off. The second half is the part that
	/// makes this work on concave polygons - without it, ear clipping is just a fan.</summary>
	static bool IsEar( IReadOnlyList<Vec2> points, List<int> ring, int prev, int cur, int next )
	{
		var a = points[prev];
		var b = points[cur];
		var c = points[next];

		var cross = Vec2.Cross( b - a, c - b );

		// Reflex, or a zero-area sliver: not an ear.
		if ( cross <= 1e-12f )
			return false;

		foreach ( var index in ring )
		{
			if ( index == prev || index == cur || index == next )
				continue;

			if ( InsideTriangle( points[index], a, b, c ) )
				return false;
		}

		return true;
	}

	static bool InsideTriangle( Vec2 p, Vec2 a, Vec2 b, Vec2 c )
	{
		// All three edge tests agreeing in sign means inside. Points exactly ON an edge count as
		// inside: clipping an ear through one would leave a T-junction in the surface.
		var ab = Vec2.Cross( b - a, p - a );
		var bc = Vec2.Cross( c - b, p - b );
		var ca = Vec2.Cross( a - c, p - c );

		return ab >= 0f && bc >= 0f && ca >= 0f;
	}
}
