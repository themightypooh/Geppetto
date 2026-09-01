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
	/// True when this region is the intersection of two or more other profiles, not a loop of the
	/// curve graph. Two overlapping rectangles are three pickable faces: each whole, and the lens
	/// in the middle. The lens is this, and it is how a click in the overlap names the overlap
	/// rather than whichever whole happened to be smaller.
	///
	/// Features that build every region (no <c>RegionSeed</c>) skip these, or the lens would be
	/// extruded on top of the two wholes that already contain it.
	/// </summary>
	public bool FromOverlap;

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
///
/// OVERLAPPING LOOPS ARE THREE FACES, NOT TWO WHOLES. Two rectangles that cross without sharing a
/// vertex are two cycles of the integer graph, and the lens between them is not a cycle of either.
/// Nesting used to read that as a hole whenever one loop's first vertex sat inside the other, which
/// ate the overlap and the part that stuck out. Crossing is not nesting: a hole is strictly inside
/// and does not cross. The lens is recovered by imprinting those crossings on a copy
/// (<see cref="SketchArrangement"/>) and keeping any arrangement face that sits inside two or more
/// originals. The originals stay pickable as wholes; the lens is pickable as itself. The source
/// sketch is not mutated — coincidence-as-identity is still the editing model.
///
/// TWO SKETCH FEATURES ON THE SAME PLANE ARE THE SAME PROBLEM. The lens between them is not a
/// cycle of either graph. Pass the other sketches in; they are overlaid in this plane's
/// coordinates, imprinted, and any arrangement face that sits in this sketch AND in at least one
/// other is kept as an overlap. Exclusive faces of the neighbours are not — this sketch does not
/// own them.
/// </summary>
public static class ProfileFinder
{
	public static ProfileResult Find( Sketch sketch ) => Find( sketch, null );

	public static ProfileResult Find( Sketch sketch, IEnumerable<Sketch> neighbors )
	{
		var result = new ProfileResult();
		var loops = CollectLoops( sketch, result );

		NestInto( result, loops );
		AddOverlapRegions( sketch, neighbors, result );

		return result;
	}

	/// <summary>
	/// Every closed loop the unsplit sketch already knows about: closed curves (circles, ellipses,
	/// closed splines) plus the faces of the integer graph. Overlap lenses are not in this list;
	/// those come from the arrangement pass afterwards.
	/// </summary>
	static List<List<Vec2>> CollectLoops( Sketch sketch, ProfileResult result )
	{
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

		loops.AddRange( FindFaces( sketch, edges, result ) );

		return loops;
	}

	/// <summary>
	/// Nesting: a loop inside an odd number of other loops is a hole. Crossing is not inside —
	/// two overlapping rectangles are two outers, not one with the other cut out of it.
	/// </summary>
	static void NestInto( ProfileResult result, List<List<Vec2>> loops )
	{
		var depths = new int[loops.Count];

		for ( var i = 0; i < loops.Count; i++ )
		{
			for ( var j = 0; j < loops.Count; j++ )
			{
				if ( i != j && StrictlyInside( loops[j], loops[i] ) )
					depths[i]++;
			}
		}

		for ( var i = 0; i < loops.Count; i++ )
		{
			if ( depths[i] % 2 != 0 )
				continue;

			var profile = new Profile { Outer = Orient( loops[i], counterClockwise: true ) };

			// Immediate children only: nested one level deeper AND strictly inside this one.
			for ( var j = 0; j < loops.Count; j++ )
			{
				if ( j != i && depths[j] == depths[i] + 1 && StrictlyInside( loops[i], loops[j] ) )
					profile.Holes.Add( Orient( loops[j], counterClockwise: false ) );
			}

			result.Profiles.Add( profile );
		}
	}

	/// <summary>
	/// The lens (and any n-way overlap) as its own profile, so a click in the middle names the
	/// middle. Originals are left in place: clicking the part that belongs to only one loop still
	/// picks that whole, which is the thing people already could pick.
	///
	/// Neighbours are other sketches on the same plane. Their outers count toward "sits in two
	/// or more", but a face that misses this sketch entirely is not added — exclusive faces of
	/// a neighbour belong to that neighbour.
	///
	/// Skipped when fewer than two outers exist across host and neighbours, or when no pair of
	/// them even overlap in bounds — a hole is not an overlap, and two disjoint squares must
	/// not pay for an imprint.
	/// </summary>
	static void AddOverlapRegions( Sketch sketch, IEnumerable<Sketch> neighbors, ProfileResult result )
	{
		var hostOuters = result.Profiles.Where( p => !p.FromOverlap ).ToList();
		var allOuters = new List<Profile>( hostOuters );

		if ( neighbors is not null )
		{
			foreach ( var guest in neighbors )
			{
				if ( guest is null || ReferenceEquals( guest, sketch )
					|| !SketchArrangement.Coplanar( sketch.Plane, guest.Plane ) )
					continue;

				foreach ( var profile in Originals( guest ) )
					allOuters.Add( ProjectProfile( profile, guest.Plane, sketch.Plane ) );
			}
		}

		if ( allOuters.Count < 2 || !AnyBoundsOverlap( allOuters ) )
			return;

		var working = SketchArrangement.ImprintCrossings( SketchArrangement.Overlay( sketch, neighbors ) );

		if ( ReferenceEquals( working, sketch ) )
			return;

		var discarded = new ProfileResult();
		var faces = CollectLoops( working, discarded );

		foreach ( var face in faces )
		{
			if ( face.Count < 3 )
				continue;

			var loop = Orient( face, counterClockwise: true );
			var seed = InteriorPoint( loop );
			var hostHits = 0;
			var allHits = 0;

			foreach ( var outer in hostOuters )
			{
				if ( outer.Contains( seed ) )
					hostHits++;
			}

			if ( hostHits == 0 )
				continue;

			allHits = hostHits;

			for ( var i = hostOuters.Count; i < allOuters.Count; i++ )
			{
				if ( allOuters[i].Contains( seed ) )
					allHits++;
			}

			if ( allHits < 2 )
				continue;

			result.Profiles.Add( new Profile { Outer = loop, FromOverlap = true } );
		}
	}

	/// <summary>This sketch's own closed regions, without overlap extras and without looking at
	/// neighbours — the outers that a neighbour contributes to a combined arrangement.</summary>
	static List<Profile> Originals( Sketch sketch )
	{
		var result = new ProfileResult();
		NestInto( result, CollectLoops( sketch, result ) );
		return result.Profiles;
	}

	static Profile ProjectProfile( Profile profile, SketchPlane from, SketchPlane to ) => new()
	{
		Outer = ProjectLoop( profile.Outer, from, to ),
		Holes = profile.Holes.Select( h => ProjectLoop( h, from, to ) ).ToList()
	};

	static List<Vec2> ProjectLoop( List<Vec2> loop, SketchPlane from, SketchPlane to )
	{
		if ( from.Origin.AlmostEquals( to.Origin )
			&& from.XAxis.AlmostEquals( to.XAxis )
			&& from.YAxis.AlmostEquals( to.YAxis ) )
			return loop;

		var projected = new List<Vec2>( loop.Count );

		foreach ( var p in loop )
			projected.Add( to.ToPlane( from.ToWorld( p ) ) );

		return projected;
	}

	static bool AnyBoundsOverlap( List<Profile> outers )
	{
		for ( var i = 0; i < outers.Count; i++ )
		{
			var a = Bounds( outers[i].Outer );

			for ( var j = i + 1; j < outers.Count; j++ )
			{
				var b = Bounds( outers[j].Outer );

				if ( a.min.x <= b.max.x && a.max.x >= b.min.x
					&& a.min.y <= b.max.y && a.max.y >= b.min.y )
					return true;
			}
		}

		return false;
	}

	static (Vec2 min, Vec2 max) Bounds( List<Vec2> loop )
	{
		var min = new Vec2( float.MaxValue, float.MaxValue );
		var max = new Vec2( float.MinValue, float.MinValue );

		foreach ( var p in loop )
		{
			min = new Vec2( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ) );
			max = new Vec2( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ) );
		}

		return (min, max);
	}

	/// <summary>
	/// Inner is a hole in outer only when it sits entirely inside and the two do not cross.
	///
	/// EVERY VERTEX, not one interior point. The centroid of a rectangle with a circle in the
	/// middle sits inside that circle, so a single-point test flipped nesting and produced no
	/// outer at all. An overlapping neighbour can have one vertex inside without being a hole;
	/// requiring every vertex rejects that, and <see cref="Crosses"/> rejects the rest.
	/// </summary>
	static bool StrictlyInside( List<Vec2> outer, List<Vec2> inner )
	{
		if ( inner.Count == 0 || Crosses( outer, inner ) )
			return false;

		foreach ( var p in inner )
		{
			if ( !Contains( outer, p ) )
				return false;
		}

		return true;
	}

	/// <summary>
	/// A point strictly inside the polygon, not a vertex. Nesting used to probe <c>loop[0]</c>,
	/// which for an overlapping neighbour is often on another loop's boundary, where ray-casting
	/// is undefined.
	///
	/// The area centroid is the first choice: it sits well inside a convex face (the lens) and
	/// inside a shallow crescent (one circle not the other). Stepping off an edge is the fallback
	/// for a C-shape whose centroid has fallen out of the polygon — and it is a fallback because
	/// an inset off the dent of a crescent can land in the lens, which would then be counted as
	/// inside both originals.
	/// </summary>
	static Vec2 InteriorPoint( List<Vec2> loop )
	{
		if ( loop.Count == 0 )
			return Vec2.Zero;

		var area2 = 0f;
		var cx = 0f;
		var cy = 0f;

		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = loop[i];
			var b = loop[(i + 1) % loop.Count];
			var cross = a.x * b.y - b.x * a.y;
			area2 += cross;
			cx += (a.x + b.x) * cross;
			cy += (a.y + b.y) * cross;
		}

		if ( MathF.Abs( area2 ) > 1e-12f )
		{
			var centroid = new Vec2( cx / ( 3f * area2 ), cy / ( 3f * area2 ) );

			if ( Contains( loop, centroid ) )
				return centroid;
		}

		var sign = area2 >= 0f ? 1f : -1f;

		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = loop[i];
			var b = loop[(i + 1) % loop.Count];
			var edge = b - a;
			var length = edge.Length;

			if ( length < 1e-8f )
				continue;

			var inset = MathF.Min( 1e-3f, length * 0.1f );
			var left = new Vec2( -edge.y, edge.x ) / length;
			var probe = new Vec2(
				(a.x + b.x) * 0.5f + left.x * sign * inset,
				(a.y + b.y) * 0.5f + left.y * sign * inset );

			if ( Contains( loop, probe ) )
				return probe;
		}

		return loop[0];
	}

	/// <summary>Proper edge crossings, ignoring vertices the two loops already share.</summary>
	static bool Crosses( List<Vec2> a, List<Vec2> b )
	{
		const float eps = 1e-5f;

		for ( var i = 0; i < a.Count; i++ )
		{
			var a0 = a[i];
			var a1 = a[(i + 1) % a.Count];
			var da = a1 - a0;

			for ( var j = 0; j < b.Count; j++ )
			{
				var b0 = b[j];
				var b1 = b[(j + 1) % b.Count];
				var db = b1 - b0;
				var denom = Vec2.Cross( da, db );

				if ( MathF.Abs( denom ) < eps )
					continue;

				var t = Vec2.Cross( b0 - a0, db ) / denom;
				var u = Vec2.Cross( b0 - a0, da ) / denom;

				if ( t is > eps and < 1f - eps && u is > eps and < 1f - eps )
					return true;
			}
		}

		return false;
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
