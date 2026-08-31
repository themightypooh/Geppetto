using System;
using System.Collections.Generic;
using System.Linq;

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
/// Holes ARE supported, by WithHoles below — inner loops are spliced into the outer one along a
/// bridge and the whole thing is ear clipped as a single ring. This comment said the opposite for
/// some time after that landed, which is the failure mode this repo keeps hitting: a stated
/// limitation outliving the limitation itself.
/// </summary>
public static class Triangulate
{
	/// <summary>
	/// Triangulate a polygon given in plane coordinates. Returns index triples INTO THE INPUT
	/// LIST, wound the same way the input is, so a caller can map them straight back onto its own
	/// vertices without worrying about which way the face faces.
	/// </summary>
	/// <summary>
	/// Triangulate a loop that has already had its holes SPLICED IN along bridges — one boundary
	/// that runs out to an inner ring, round it, and back along the same seam.
	///
	/// WHY THIS IS NOT Polygon(). Polygon assumes a SIMPLE polygon: no repeated vertex, no edge
	/// travelled twice. A bridged loop breaks both, and Polygon does not fail on one - it returns
	/// overlapping triangles that cover more area than the outline encloses, which renders as a
	/// hole that has been filled in. WithHoles never hit this because it builds its own bridges and
	/// finishes through ClipRing, the clipper that tolerates them; Polygon is simply the wrong door
	/// and nothing had ever knocked on it with a bridged loop before.
	///
	/// What knocks now is s&box's mesh boolean. A half-edge face is one closed loop, so a cut that
	/// leaves a hole in a face can only hand it back bridged, and that is the shape a cut arrives
	/// in — see EffigyMeshBoolean.
	///
	/// Winding follows the input, the same contract Polygon gives.
	/// </summary>
	public static List<(int A, int B, int C)> BridgedLoop( IReadOnlyList<Vec2> points )
	{
		if ( points is null || points.Count < 3 )
			return new List<(int, int, int)>();

		// WELDING IS THE WHOLE TRICK, and getting it wrong looks like success. ClipRing tolerates a
		// bridge only when the seam's two visits are THE SAME INDEX - that identity is what lets its
		// ear test recognise the doubled edge instead of measuring a zero-area corner and rejecting
		// every candidate. Handed a ring whose visits are two different indices at identical
		// positions, it finds no ear at all and falls back to a fan, which returns a plausible pile
		// of triangles covering the hole and everything else besides.
		//
		// WithHoles never meets this because it splices indices into a shared point list and the
		// repeat is literal. A loop arriving from outside - the shape s&box's boolean returns - has
		// to be put into that same form first.
		var welded = new List<Vec2>( points.Count );
		var ring = new List<int>( points.Count );
		var representative = new List<int>( points.Count );

		for ( var i = 0; i < points.Count; i++ )
		{
			var index = -1;

			for ( var j = 0; j < welded.Count; j++ )
			{
				if ( MathF.Abs( welded[j].x - points[i].x ) > WeldTolerance
					|| MathF.Abs( welded[j].y - points[i].y ) > WeldTolerance )
					continue;

				index = j;
				break;
			}

			if ( index < 0 )
			{
				index = welded.Count;
				welded.Add( points[i] );

				// Which position in the CALLER'S list this welded point stands for, so the triples
				// come back indexed the way the caller handed its loop in.
				representative.Add( i );
			}

			ring.Add( index );
		}

		var triangles = ClipRing( welded, ring, reversed: RingSignedArea( welded, ring ) < 0f );

		for ( var i = 0; i < triangles.Count; i++ )
		{
			var (a, b, c) = triangles[i];
			triangles[i] = (representative[a], representative[b], representative[c]);
		}

		return triangles;
	}

	/// <summary>Two points this close together in plane units are the same point. Loose enough for
	/// a seam the engine reports twice, far below anything anyone draws.</summary>
	const float WeldTolerance = 1e-5f;

	/// <summary>Signed area of a ring of indices, rather than of a bare point list.</summary>
	static float RingSignedArea( IReadOnlyList<Vec2> points, IReadOnlyList<int> ring )
	{
		var sum = 0f;

		for ( var i = 0; i < ring.Count; i++ )
		{
			var a = points[ring[i]];
			var b = points[ring[(i + 1) % ring.Count]];
			sum += a.x * b.y - b.x * a.y;
		}

		return sum * 0.5f;
	}

	/// <summary>The 3D form of <see cref="BridgedLoop"/>, flattened onto the loop's own Newell
	/// normal exactly as <see cref="Face"/> does.</summary>
	public static List<(int A, int B, int C)> BridgedFace( IReadOnlyList<Vec3> positions )
	{
		if ( positions is null || positions.Count < 3 )
			return new List<(int, int, int)>();

		return BridgedLoop( Flatten( positions ) );
	}

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

		return ClipRing( points, indices, reversed );
	}

	/// <summary>
	/// Ear-clip a ring of indices that is already walked the right way round.
	///
	/// Shared by Polygon and WithHoles, which differ only in the ring they arrive with: a plain
	/// polygon's is its own points, a holed one's has each hole spliced in along a bridge. Once the
	/// ring exists there is one algorithm, and having one copy of it is what keeps a holed cap and
	/// a plain one from ever disagreeing about winding.
	/// </summary>
	static List<(int A, int B, int C)> ClipRing( IReadOnlyList<Vec2> points, List<int> indices, bool reversed )
	{
		var triangles = new List<(int, int, int)>( Math.Max( indices.Count - 2, 0 ) );

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
	/// Triangulate a polygon that has holes in it — a plate with bolt holes, a washer, a flange.
	///
	/// THE TRICK IS THAT THERE IS NO TRICK. Ear clipping only works on a SIMPLE polygon, so each
	/// hole is spliced into the outer loop along a "bridge": a segment from an outer vertex to a
	/// hole vertex, walked out and back, which turns a ring-with-a-hole into one boundary that
	/// happens to visit the bridge twice. After that it is an ordinary ear clip, and the doubled
	/// bridge edge takes care of itself because IsEar already refuses a zero-area corner.
	///
	/// This is why a holed profile never needed a boolean. Capping around a hole is a 2D
	/// triangulation problem and it always was; the old refusal called it "really the same problem
	/// as a boolean subtract", which is what kept a rectangle with a circle in it unbuildable long
	/// after ear clipping arrived.
	///
	/// BRIDGE CHOICE. The textbook answer is Eberly's: take the hole's rightmost vertex, cast a ray
	/// and find a visible outer vertex. This takes the shortest bridge that is actually valid
	/// instead — no crossing of any edge, and a midpoint genuinely inside the material — which is
	/// more work per hole and far less to get subtly wrong. Sketch profiles have tens of points, not
	/// thousands, so the cost does not matter and the certainty does.
	///
	/// Returns triples indexing a CONCATENATED list: the outer loop's points first, then each hole's
	/// in the order given. The caller adds its vertices in that same order and maps straight across.
	/// Empty when a hole cannot be bridged, which the caller should report rather than ignore.
	/// </summary>
	public static List<(int A, int B, int C)> WithHoles( IReadOnlyList<Vec2> outer,
		IReadOnlyList<IReadOnlyList<Vec2>> holes )
	{
		if ( outer is null || outer.Count < 3 )
			return new List<(int, int, int)>();

		if ( holes is null || holes.Count == 0 )
			return Polygon( outer );

		// One flat list, in the order the caller was promised.
		var points = new List<Vec2>( outer );
		var holeRings = new List<List<int>>();
		var offset = outer.Count;

		foreach ( var hole in holes )
		{
			if ( hole is null || hole.Count < 3 )
			{
				// A degenerate hole is skipped rather than fatal, but its points still go into the
				// list: the caller's vertex layout is fixed and must not shift underneath it.
				points.AddRange( hole ?? Array.Empty<Vec2>() );
				offset = points.Count;
				continue;
			}

			var ring = new List<int>( hole.Count );

			for ( var i = 0; i < hole.Count; i++ )
				ring.Add( offset + i );

			points.AddRange( hole );
			offset = points.Count;

			// A hole is walked the OPPOSITE way to the outer loop. That is what makes the spliced
			// boundary keep the material on one side the whole way round.
			if ( SignedArea( hole ) > 0f == SignedArea( outer ) > 0f )
				ring.Reverse();

			holeRings.Add( ring );
		}

		var merged = new List<int>( outer.Count );

		for ( var i = 0; i < outer.Count; i++ )
			merged.Add( i );

		// Rightmost hole first, which is the conventional order and keeps the result deterministic
		// rather than depending on the order the profile finder happened to discover loops in.
		holeRings.Sort( ( a, b ) => b.Max( i => points[i].x ).CompareTo( a.Max( i => points[i].x ) ) );

		foreach ( var hole in holeRings )
		{
			if ( !Bridge( points, merged, hole ) )
				return new List<(int, int, int)>();
		}

		return ClipRing( points, merged, reversed: SignedArea( outer ) < 0f );
	}

	/// <summary>
	/// Splice one hole into the working boundary along the shortest valid bridge.
	///
	/// Valid means three things, and all three are needed: the segment crosses no edge of the
	/// boundary as it currently stands (including holes merged before this one), it crosses no edge
	/// of the hole itself, and its midpoint is inside the material. The last is what rules out a
	/// bridge that runs cleanly around the outside of everything and crosses nothing at all.
	/// </summary>
	static bool Bridge( List<Vec2> points, List<int> boundary, List<int> hole )
	{
		var bestOuter = -1;
		var bestHole = -1;
		var bestLength = float.MaxValue;

		for ( var i = 0; i < boundary.Count; i++ )
		{
			for ( var j = 0; j < hole.Count; j++ )
			{
				var a = points[boundary[i]];
				var b = points[hole[j]];
				var length = (b - a).LengthSquared;

				if ( length >= bestLength || length < 1e-12f )
					continue;

				if ( !BridgeIsClear( points, boundary, hole, boundary[i], hole[j] ) )
					continue;

				bestLength = length;
				bestOuter = i;
				bestHole = j;
			}
		}

		if ( bestOuter < 0 )
			return false;

		// ...outer[i], hole[j], hole[j+1] ... all the way round ... hole[j], outer[i], outer[i+1]...
		// The two repeated indices are the bridge, walked out and back.
		var spliced = new List<int>( boundary.Count + hole.Count + 2 );

		for ( var i = 0; i <= bestOuter; i++ )
			spliced.Add( boundary[i] );

		for ( var j = 0; j < hole.Count; j++ )
			spliced.Add( hole[(bestHole + j) % hole.Count] );

		spliced.Add( hole[bestHole] );
		spliced.Add( boundary[bestOuter] );

		for ( var i = bestOuter + 1; i < boundary.Count; i++ )
			spliced.Add( boundary[i] );

		boundary.Clear();
		boundary.AddRange( spliced );

		return true;
	}

	static bool BridgeIsClear( List<Vec2> points, List<int> boundary, List<int> hole, int from, int to )
	{
		var a = points[from];
		var b = points[to];

		if ( Crosses( points, boundary, a, b, from, to ) || Crosses( points, hole, a, b, from, to ) )
			return false;

		// Inside the material: within the boundary, and not down the hole it is bridging to.
		var mid = (a + b) * 0.5f;

		return Contains( points, boundary, mid ) && !Contains( points, hole, mid );
	}

	/// <summary>Whether a segment properly crosses any edge of a ring. Edges touching either end of
	/// the segment are skipped by INDEX — a bridge necessarily meets the two edges at each of its
	/// own endpoints, and that is not a crossing.</summary>
	static bool Crosses( List<Vec2> points, List<int> ring, Vec2 a, Vec2 b, int from, int to )
	{
		for ( var i = 0; i < ring.Count; i++ )
		{
			var p = ring[i];
			var q = ring[(i + 1) % ring.Count];

			if ( p == from || q == from || p == to || q == to )
				continue;

			if ( SegmentsCross( a, b, points[p], points[q] ) )
				return true;
		}

		return false;
	}

	/// <summary>Strict crossing: the two segments meet at a point interior to both. Touching at an
	/// endpoint does not count, which is what makes this usable on a ring where consecutive edges
	/// share a vertex.</summary>
	static bool SegmentsCross( Vec2 a, Vec2 b, Vec2 c, Vec2 d )
	{
		var d1 = Vec2.Cross( b - a, c - a );
		var d2 = Vec2.Cross( b - a, d - a );
		var d3 = Vec2.Cross( d - c, a - c );
		var d4 = Vec2.Cross( d - c, b - c );

		return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f))
			&& ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
	}

	/// <summary>Even-odd point-in-ring test.</summary>
	static bool Contains( List<Vec2> points, List<int> ring, Vec2 p )
	{
		var inside = false;

		for ( int i = 0, j = ring.Count - 1; i < ring.Count; j = i++ )
		{
			var a = points[ring[i]];
			var b = points[ring[j]];

			if ( a.y > p.y != b.y > p.y
				&& p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x )
			{
				inside = !inside;
			}
		}

		return inside;
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

		return Polygon( Flatten( positions ) );
	}

	/// <summary>
	/// Drop a planar 3D loop onto its own plane, keeping the order and the winding.
	///
	/// Shared by Face and BridgedFace rather than written twice: the two differ only in which
	/// clipper they hand the result to, and a second copy of this projection would be a second
	/// place for the seed-axis choice below to drift.
	/// </summary>
	static List<Vec2> Flatten( IReadOnlyList<Vec3> positions )
	{
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

		return flat;
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
