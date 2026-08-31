using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

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

	/// <summary>
	/// Whether a point in plane coordinates falls inside this region — within the outer loop and
	/// not down any of its holes.
	///
	/// This is what turns a click in the viewport into a face selection, and what lets a feature
	/// re-find the face it was pointed at after the sketch was edited.
	/// </summary>
	public bool Contains( Vec2 p ) =>
		ProfileFinder.Contains( Outer, p ) && !Holes.Any( h => ProfileFinder.Contains( h, p ) );

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
/// BRANCHING IS HANDLED BY PLANAR FACE TRAVERSAL. Only points where exactly two curves met used to
/// be followed, so a line drawn across a rectangle — which is how anyone divides a shape — was
/// reported as "not supported yet" rather than split into the two regions it plainly is.
///
/// The upgrade is the one this comment used to describe as the upgrade path, and it works out
/// exactly as advertised. Every curve becomes two directed HALF-EDGES. At each point the outgoing
/// half-edges are sorted by the direction they actually leave in, and the rule for walking a face
/// is: arrive along h, take h's reverse, and leave along whichever half-edge sits immediately
/// CLOCKWISE of it. Follow that and you trace one face and come back where you started, every time,
/// because each half-edge belongs to exactly one face.
///
/// The faces that come out counter-clockwise are regions. Each connected piece of the sketch also
/// produces one clockwise face — the infinite one outside it — and those are dropped, which is the
/// whole of the special-case handling.
///
/// THE ANGLE HAS TO BE THE TANGENT, not the direction of the straight line to the far end. Where an
/// arc and a line leave the same point, the straight-line direction can order them the wrong way
/// round, and the wrong order picks the wrong face. Tessellating and taking the first segment gets
/// the tangent for free and reuses the sampling everything else already agrees on.
/// </summary>
public static class ProfileFinder
{
	public static ProfileResult Find( Sketch sketch )
	{
		var result = new ProfileResult();
		var loops = new List<List<Vec2>>();

		// A closed curve — circle, ellipse, closed spline — is a region on its own and never
		// participates in the graph walk.
		foreach ( var closed in sketch.Curves.Where( c => c.IsClosed && !c.Construction ) )
		{
			var pts = closed.Tessellate( sketch, sketch.Tolerance );
			pts.RemoveAt( pts.Count - 1 ); // drop the repeated closing point

			// A circle at or below the sketch tolerance tessellates to fewer than three points and
			// is not a region at all. Walked loops are guarded by their own Count >= 3 check; this
			// path skipped it, and a two-point "loop" extruded into faces with two corners.
			if ( pts.Count < 3 )
			{
				result.Warnings.Add(
					$"A closed {closed.GetType().Name} is too small to form a region at the sketch tolerance of {sketch.Tolerance}" );
				continue;
			}

			loops.Add( pts );
		}

		var edges = sketch.Curves
			.Where( c => !c.Construction && !c.IsClosed )
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

		loops.AddRange( FindFaces( sketch, edges, result ) );

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

	/// <summary>One direction along one curve. Two of these per curve, and each belongs to exactly
	/// one face, which is what makes the traversal terminate and cover everything.</summary>
	sealed class HalfEdge
	{
		public SketchCurve Curve;
		public int From, To;

		/// <summary>The direction it actually LEAVES From in, as an angle. The tangent, not the
		/// bearing of the far endpoint — see the class comment for why that distinction decides
		/// which face an arc belongs to.</summary>
		public float Angle;

		public HalfEdge Twin;
		public bool Used;

		/// <summary>Tessellated points from From to To inclusive.</summary>
		public List<Vec2> Points;
	}

	/// <summary>
	/// Every bounded face of the sketch's curve graph, as loops of points.
	///
	/// Dangling curves are pruned first. A curve with a free end encloses nothing, and in a face
	/// traversal it is worse than useless: the walk runs out along it and back, leaving a zero-width
	/// spur in the middle of an otherwise good region. Pruning repeats, because removing one dangling
	/// curve can leave the next one dangling — a whole tail retracts one curve at a time.
	/// </summary>
	static List<List<Vec2>> FindFaces( Sketch sketch, List<SketchCurve> edges, ProfileResult result )
	{
		var faces = new List<List<Vec2>>();
		var live = new List<SketchCurve>();
		var pruned = new List<SketchCurve>();

		foreach ( var curve in edges )
		{
			var (a, b) = Ends( curve );

			// A curve whose two ends are the same point is a closed loop of one curve, which the
			// traversal has no way to walk — its twin leaves the same vertex it arrives at. A closed
			// arc should have been drawn as a circle.
			if ( a == b )
			{
				result.Warnings.Add( $"a curve starting and ending at point {a} was skipped; draw a full circle instead" );
				continue;
			}

			live.Add( curve );
		}

		while ( true )
		{
			var degree = new Dictionary<int, int>();

			foreach ( var curve in live )
			{
				var (a, b) = Ends( curve );
				degree[a] = degree.GetValueOrDefault( a ) + 1;
				degree[b] = degree.GetValueOrDefault( b ) + 1;
			}

			var dangling = live.Where( c =>
			{
				var (a, b) = Ends( c );
				return degree[a] < 2 || degree[b] < 2;
			} ).ToList();

			if ( dangling.Count == 0 )
				break;

			foreach ( var curve in dangling )
			{
				live.Remove( curve );
				pruned.Add( curve );
			}
		}

		var chains = CountChains( pruned );
		result.OpenChains += chains;

		// SAID OUT LOUD, not just dropped. A dangling curve encloses nothing and cannot be part of a
		// region, but it is still geometry somebody drew — and building the good regions while
		// silently discarding it is the failure mode that matters here: it looks like it worked. The
		// caller turns this into "built from N regions; ignored: ...".
		if ( chains > 0 )
		{
			result.Warnings.Add( chains == 1
				? $"{pruned.Count} curve(s) form an open chain that does not enclose anything"
				: $"{pruned.Count} curve(s) form {chains} open chains that do not enclose anything" );
		}

		if ( live.Count == 0 )
			return faces;

		// --- build the half-edges and sort them around each point --------------------------------

		var outgoing = new Dictionary<int, List<HalfEdge>>();
		var all = new List<HalfEdge>( live.Count * 2 );

		foreach ( var curve in live )
		{
			var (a, b) = Ends( curve );
			var forwardPoints = curve.Tessellate( sketch, sketch.Tolerance );
			var backwardPoints = new List<Vec2>( forwardPoints );
			backwardPoints.Reverse();

			var forward = new HalfEdge { Curve = curve, From = a, To = b, Points = forwardPoints };
			var backward = new HalfEdge { Curve = curve, From = b, To = a, Points = backwardPoints };

			forward.Twin = backward;
			backward.Twin = forward;

			forward.Angle = LeavingAngle( forwardPoints );
			backward.Angle = LeavingAngle( backwardPoints );

			all.Add( forward );
			all.Add( backward );

			Add( outgoing, a, forward );
			Add( outgoing, b, backward );
		}

		foreach ( var list in outgoing.Values )
			list.Sort( ( x, y ) => x.Angle.CompareTo( y.Angle ) );

		// --- walk every face ---------------------------------------------------------------------

		foreach ( var seed in all )
		{
			if ( seed.Used )
				continue;

			var loop = new List<Vec2>();
			var current = seed;

			// Each half-edge is used once, so a walk can never be longer than the total. The guard is
			// for a graph the sort could not order consistently rather than for the normal case.
			for ( var guard = all.Count + 1; guard > 0; guard-- )
			{
				current.Used = true;

				// Consecutive half-edges share their joining point, so every curve after the first
				// drops its opening point.
				for ( var i = loop.Count == 0 ? 0 : 1; i < current.Points.Count; i++ )
					loop.Add( current.Points[i] );

				var next = NextAroundFace( outgoing, current );

				if ( next is null || ReferenceEquals( next, seed ) )
					break;

				current = next;
			}

			// The walk finishes back on its first point, which is already the loop's first entry.
			if ( loop.Count > 1 )
				loop.RemoveAt( loop.Count - 1 );

			// Counter-clockwise means a bounded face. Every connected piece of the sketch also
			// produces exactly one clockwise face, the infinite one around it, and that is the one
			// thing here with nothing to contribute.
			if ( loop.Count >= 3 && SignedArea( loop ) > 0f )
				faces.Add( loop );
		}

		return faces;
	}

	/// <summary>
	/// The next half-edge around the same face.
	///
	/// Arrive along h at its far point, turn round onto h's twin, and leave along whichever outgoing
	/// half-edge sits immediately CLOCKWISE of the twin. Taking the clockwise neighbour is what makes
	/// bounded faces come out counter-clockwise; taking the other one traces them the other way and
	/// every region arrives inside out.
	/// </summary>
	static HalfEdge NextAroundFace( Dictionary<int, List<HalfEdge>> outgoing, HalfEdge h )
	{
		if ( !outgoing.TryGetValue( h.To, out var around ) || around.Count == 0 )
			return null;

		var index = around.IndexOf( h.Twin );

		if ( index < 0 )
			return null;

		return around[(index - 1 + around.Count) % around.Count];
	}

	/// <summary>The direction a tessellated curve sets off in, as an angle. Uses the first segment
	/// long enough to have a direction, so a curve that starts with a hair-thin step still reports
	/// where it is actually going.</summary>
	static float LeavingAngle( List<Vec2> points )
	{
		var from = points[0];

		for ( var i = 1; i < points.Count; i++ )
		{
			var delta = points[i] - from;

			if ( delta.LengthSquared > 1e-16f )
				return MathF.Atan2( delta.y, delta.x );
		}

		return 0f;
	}

	static void Add( Dictionary<int, List<HalfEdge>> map, int point, HalfEdge edge )
	{
		if ( !map.TryGetValue( point, out var list ) )
			map[point] = list = new List<HalfEdge>();

		list.Add( edge );
	}

	/// <summary>How many separate open chains a set of pruned curves forms, so "its curves do not
	/// join up" can be said about the right number of them.</summary>
	static int CountChains( List<SketchCurve> pruned )
	{
		if ( pruned.Count == 0 )
			return 0;

		var remaining = new HashSet<SketchCurve>( pruned );
		var chains = 0;

		while ( remaining.Count > 0 )
		{
			var seed = remaining.First();
			var queue = new Queue<SketchCurve>();
			queue.Enqueue( seed );
			remaining.Remove( seed );

			var touched = new HashSet<int>();

			while ( queue.Count > 0 )
			{
				var (a, b) = Ends( queue.Dequeue() );
				touched.Add( a );
				touched.Add( b );

				foreach ( var candidate in remaining.ToList() )
				{
					var (ca, cb) = Ends( candidate );

					if ( !touched.Contains( ca ) && !touched.Contains( cb ) )
						continue;

					remaining.Remove( candidate );
					queue.Enqueue( candidate );
				}
			}

			chains++;
		}

		return chains;
	}

	static (int A, int B) Ends( SketchCurve curve ) => curve.Endpoints;

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
