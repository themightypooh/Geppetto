using System;
using System.Collections.Generic;
using System.Linq;

namespace ModelKernel;

/// <summary>
/// A closed region found in a sketch: an outer boundary, counter-clockwise, plus any loops nested
/// inside it.
///
/// Points are in plane coordinates and the outer loop does NOT repeat its first point at the end.
/// </summary>
public sealed class Profile
{
	public List<Vec2> Outer = new();
	public List<List<Vec2>> Holes = new();

	public bool HasHoles => Holes.Count > 0;

	/// <summary>Shoelace area of the outer loop, minus the holes.</summary>
	public float Area => MathF.Abs( ProfileFinder.SignedArea( Outer ) )
		- Holes.Sum( h => MathF.Abs( ProfileFinder.SignedArea( h ) ) );
}

public sealed class ProfileResult
{
	public List<Profile> Profiles = new();

	/// <summary>Chains that never closed. Usually a sketch mid-draw rather than a mistake, so this
	/// is reported rather than thrown.</summary>
	public int OpenChains;

	public List<string> Warnings = new();
}

/// <summary>
/// Turns a sketch's curves into closed regions that Extrude and Revolve can consume.
///
/// HOW IT WORKS: curves reference shared point indices, so the sketch is already a graph — points
/// are nodes, curves are edges. A closed region is a cycle. Because coincident corners share an
/// index rather than merely sitting at the same coordinates, the walk is exact integer bookkeeping
/// with no floating-point matching anywhere in it.
///
/// KNOWN LIMIT: only points of degree 2 are followed. A point where three or more curves meet — a
/// line drawn across a rectangle, say — is ambiguous without full planar face traversal, and is
/// reported as a warning instead of guessed at. That covers rectangles, polygons, circles, slots
/// and rounded profiles, which is very nearly every sketch anyone draws. Proper face traversal
/// (sort half-edges by angle at each vertex, always take the next one clockwise) is the upgrade
/// path and does not change this file's interface.
/// </summary>
public static class ProfileFinder
{
	public static ProfileResult Find( Sketch sketch )
	{
		var result = new ProfileResult();
		var loops = new List<List<Vec2>>();

		// A circle closes on its own and never participates in the graph walk.
		foreach ( var circle in sketch.Curves.OfType<SketchCircle>().Where( c => !c.Construction ) )
		{
			var pts = circle.Tessellate( sketch, sketch.Tolerance );
			pts.RemoveAt( pts.Count - 1 ); // drop the repeated closing point

			// A circle at or below the sketch tolerance tessellates to fewer than three points and
			// is not a region at all. Walked loops are guarded by their own Count >= 3 check; this
			// path skipped it, and a two-point "loop" extruded into faces with two corners.
			if ( pts.Count < 3 )
			{
				result.Warnings.Add(
					$"A circle of radius {circle.Radius} is too small to form a region at the sketch tolerance of {sketch.Tolerance}" );
				continue;
			}

			loops.Add( pts );
		}

		var edges = sketch.Curves
			.Where( c => !c.Construction && c is not SketchCircle )
			.ToList();

		// point index -> the curves touching it, by their two ends only. An arc's centre is not a
		// connection point, which is why this uses explicit ends rather than PointRefs.
		var adjacency = new Dictionary<int, List<SketchCurve>>();

		void Link( int point, SketchCurve curve )
		{
			if ( !adjacency.TryGetValue( point, out var list ) )
				adjacency[point] = list = new List<SketchCurve>();

			list.Add( curve );
		}

		foreach ( var curve in edges )
		{
			var (a, b) = Ends( curve );
			Link( a, curve );
			Link( b, curve );
		}

		foreach ( var (point, curves) in adjacency )
		{
			if ( curves.Count > 2 )
			{
				result.Warnings.Add(
					$"point {point} joins {curves.Count} curves; branching sketches are not supported yet" );
			}
		}

		var visited = new HashSet<SketchCurve>();

		foreach ( var start in edges )
		{
			if ( visited.Contains( start ) )
				continue;

			var loop = WalkLoop( sketch, start, adjacency, visited, out var closed );

			if ( closed && loop.Count >= 3 )
			{
				loops.Add( loop );
				continue;
			}

			if ( closed )
				continue;

			// Consume the rest of the same open chain before moving on, or the untouched half gets
			// seeded separately and one polyline is reported as two.
			WalkLoop( sketch, start, adjacency, visited, out _, reverse: true );
			result.OpenChains++;
		}

		// Nesting: a loop inside an odd number of other loops is a hole.
		var depths = new int[loops.Count];

		for ( var i = 0; i < loops.Count; i++)
		{
			for ( var j = 0; j < loops.Count; j++ )
			{
				if ( i != j && Contains( loops[j], loops[i][0] ) )
					depths[i]++;
			}
		}

		for ( var i = 0; i < loops.Count; i++ )
		{
			if ( depths[i] % 2 != 0 )
				continue;

			var profile = new Profile { Outer = Orient( loops[i], counterClockwise: true ) };

			// Immediate children only: nested one level deeper AND geometrically inside this one.
			for ( var j = 0; j < loops.Count; j++ )
			{
				if ( j != i && depths[j] == depths[i] + 1 && Contains( loops[i], loops[j][0] ) )
					profile.Holes.Add( Orient( loops[j], counterClockwise: false ) );
			}

			result.Profiles.Add( profile );
		}

		return result;
	}

	static (int A, int B) Ends( SketchCurve curve ) => curve switch
	{
		SketchLine l => (l.Start, l.End),
		SketchArc a => (a.Start, a.End),
		_ => throw new InvalidOperationException( $"{curve.GetType().Name} has no endpoints" )
	};

	/// <summary>
	/// Follow curves end to end until we return to where we started, or run out.
	///
	/// `reverse` walks out of the seed curve's other end. It exists because a walk only ever goes
	/// one way: seeded from the MIDDLE of an open polyline, the forward walk consumes one half and
	/// the untouched other half is then picked up as a second seed, so one chain gets counted as
	/// two. Walking both ways from the seed consumes the whole chain at once.
	/// </summary>
	static List<Vec2> WalkLoop(
		Sketch sketch,
		SketchCurve start,
		Dictionary<int, List<SketchCurve>> adjacency,
		HashSet<SketchCurve> visited,
		out bool closed,
		bool reverse = false )
	{
		var points = new List<Vec2>();
		var (startA, startB) = Ends( start );
		var firstPoint = reverse ? startB : startA;

		var current = start;
		var entryPoint = firstPoint;
		closed = false;

		while ( true )
		{
			visited.Add( current );

			var (a, b) = Ends( current );
			var exitPoint = entryPoint == a ? b : a;

			var tess = current.Tessellate( sketch, sketch.Tolerance );

			// Tessellation always runs start->end; reverse it when the walk crosses the other way.
			if ( entryPoint != a )
				tess.Reverse();

			// Drop the first point of every curve after the first: consecutive curves share their
			// joining point, and keeping both would leave a zero-length segment in the loop.
			for ( var i = points.Count == 0 ? 0 : 1; i < tess.Count; i++ )
				points.Add( tess[i] );

			if ( exitPoint == firstPoint )
			{
				closed = true;

				// The walk ended back at the start point, which is already the loop's first entry.
				if ( points.Count > 1 )
					points.RemoveAt( points.Count - 1 );

				return points;
			}

			if ( !adjacency.TryGetValue( exitPoint, out var candidates ) )
				return points;

			var next = candidates.FirstOrDefault( c => c != current && !visited.Contains( c ) );

			if ( next is null )
				return points;

			current = next;
			entryPoint = exitPoint;
		}
	}

	/// <summary>Shoelace formula. Positive is counter-clockwise.</summary>
	public static float SignedArea( List<Vec2> loop )
	{
		var area = 0f;

		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = loop[i];
			var b = loop[(i + 1) % loop.Count];
			area += a.x * b.y - b.x * a.y;
		}

		return area * 0.5f;
	}

	static List<Vec2> Orient( List<Vec2> loop, bool counterClockwise )
	{
		var ccw = SignedArea( loop ) > 0f;

		if ( ccw != counterClockwise )
		{
			var copy = new List<Vec2>( loop );
			copy.Reverse();
			return copy;
		}

		return new List<Vec2>( loop );
	}

	/// <summary>Ray casting. Points exactly on the boundary are undefined, which is fine — this is
	/// only ever asked about a vertex of a different loop.</summary>
	public static bool Contains( List<Vec2> loop, Vec2 p )
	{
		var inside = false;

		for ( int i = 0, j = loop.Count - 1; i < loop.Count; j = i++ )
		{
			var a = loop[i];
			var b = loop[j];

			if ( a.y > p.y != b.y > p.y &&
				 p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x )
			{
				inside = !inside;
			}
		}

		return inside;
	}
}
