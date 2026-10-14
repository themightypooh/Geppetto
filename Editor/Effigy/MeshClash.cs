using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A mesh as triangles in a box tree, for asking where it crosses another mesh or itself, how
/// deep, and how close two meshes come.
///
/// Its own tree rather than <see cref="MeshBVH"/>, because the questions are two-sided: a crossing
/// test walks two trees against each other, and MeshBVH is built to answer one point or one ray
/// at a time and keeps its nodes private. The triangles are the same fan triangulation every
/// other kernel query uses, and each one remembers the face it came from.
///
/// POSITIONS MOVE, TOPOLOGY DOES NOT. A posed take is the same skin at a hundred poses, so
/// <see cref="Refit"/> takes new positions and only re-boxes the tree - the split order chosen
/// for the bind pose stays good enough for any pose a limb can reach.
/// </summary>
public sealed class TriangleSet
{
	const int LeafSize = 4;

	public readonly int[] Corners;     // 3 per triangle: vertex indices into Positions
	public readonly int[] Welded;      // 3 per triangle: the vertex each corner welds to
	public readonly int[] SourceFace;  // the face each triangle came from, or its own index
	public Vec3[] Positions { get; private set; }

	public int Count => SourceFace.Length;

	// Tree: node bounds, children (-1 for a leaf), and each leaf's range of Order.
	Vec3[] _min, _max;
	int[] _left, _right, _start, _count;
	int[] _order;
	int _nodes;

	TriangleSet( Vec3[] positions, int[] corners, int[] sourceFace, float weld )
	{
		Positions = positions;
		Corners = corners;
		SourceFace = sourceFace;
		Welded = WeldCorners( positions, corners, weld );
		Build();
	}

	/// <summary>Every face of a mesh, or only the ones listed.</summary>
	public static TriangleSet From( PolyMesh mesh, IEnumerable<int> faces = null, float weld = 1e-4f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var corners = new List<int>();
		var source = new List<int>();

		foreach ( var f in faces ?? Enumerable.Range( 0, mesh.FaceCount ) )
		{
			var idx = mesh.Faces[f].Indices;

			for ( var k = 1; k + 1 < idx.Length; k++ )
			{
				corners.Add( idx[0] );
				corners.Add( idx[k] );
				corners.Add( idx[k + 1] );
				source.Add( f );
			}
		}

		return new TriangleSet( mesh.Positions.ToArray(), corners.ToArray(), source.ToArray(), weld );
	}

	/// <summary>A triangle list: three indices into <paramref name="positions"/> per triangle.</summary>
	public static TriangleSet From( IReadOnlyList<Vec3> positions, IReadOnlyList<int> triangles, float weld = 1e-4f )
	{
		if ( positions is null ) throw new ArgumentNullException( nameof( positions ) );
		if ( triangles is null ) throw new ArgumentNullException( nameof( triangles ) );
		if ( triangles.Count % 3 != 0 ) throw new ArgumentException( "triangle indices come in threes", nameof( triangles ) );

		var source = Enumerable.Range( 0, triangles.Count / 3 ).ToArray();
		return new TriangleSet( positions.ToArray(), triangles.ToArray(), source, weld );
	}

	public Vec3 A( int t ) => Positions[Corners[t * 3]];
	public Vec3 B( int t ) => Positions[Corners[t * 3 + 1]];
	public Vec3 C( int t ) => Positions[Corners[t * 3 + 2]];

	/// <summary>Move the vertices, keep the triangles: re-box the tree bottom-up.</summary>
	public void Refit( IReadOnlyList<Vec3> positions )
	{
		if ( positions is null || positions.Count != Positions.Length )
			throw new ArgumentException( $"refit needs {Positions.Length} positions", nameof( positions ) );

		Positions = positions as Vec3[] ?? positions.ToArray();

		// Children always come after their parent in the node arrays, so a reverse sweep sees
		// both children of a node before the node itself.
		for ( var n = _nodes - 1; n >= 0; n-- )
		{
			if ( _left[n] < 0 )
				BoxLeaf( n );
			else
			{
				_min[n] = Min( _min[_left[n]], _min[_right[n]] );
				_max[n] = Max( _max[_left[n]], _max[_right[n]] );
			}
		}
	}

	/// <summary>Do two triangles share a vertex (after welding)? Neighbours touch by
	/// construction, and a self check that counted that would count every edge in the mesh.</summary>
	public bool Adjacent( int s, int t )
	{
		for ( var i = 0; i < 3; i++ )
			for ( var j = 0; j < 3; j++ )
				if ( Welded[s * 3 + i] == Welded[t * 3 + j] )
					return true;

		return false;
	}

	public Vec3 Normal( int t ) => Vec3.Cross( B( t ) - A( t ), C( t ) - A( t ) ).Normal;

	// --- queries: overlapping leaf pairs and nearest triangles -------------------------------

	/// <summary>Every pair of triangles whose boxes overlap, one from each set (or two from this
	/// one when <paramref name="other"/> is this, each unordered pair once).</summary>
	internal void OverlappingPairs( TriangleSet other, Action<int, int> visit )
	{
		if ( _nodes == 0 || other._nodes == 0 )
			return;

		var self = ReferenceEquals( this, other );
		var stack = new Stack<(int, int)>();
		stack.Push( (0, 0) );

		while ( stack.Count > 0 )
		{
			var (a, b) = stack.Pop();

			if ( !BoxesOverlap( _min[a], _max[a], other._min[b], other._max[b] ) )
				continue;

			var leafA = _left[a] < 0;
			var leafB = other._left[b] < 0;

			if ( leafA && leafB )
			{
				for ( var i = 0; i < _count[a]; i++ )
				{
					var s = _order[_start[a] + i];

					for ( var j = 0; j < other._count[b]; j++ )
					{
						var t = other._order[other._start[b] + j];

						// The same leaf against itself: each pair once. Two different leaves are only
						// ever visited in one order (the walk pushes left-right, never right-left), so
						// every pair across them is new.
						if ( self && a == b && j <= i )
							continue;

						visit( s, t );
					}
				}

				continue;
			}

			if ( self && a == b )
			{
				stack.Push( (_left[a], _left[a]) );
				stack.Push( (_right[a], _right[a]) );
				stack.Push( (_left[a], _right[a]) );
				continue;
			}

			// Split whichever side is bigger, so the walk stays balanced.
			if ( leafB || (!leafA && Volume( _min[a], _max[a] ) >= Volume( other._min[b], other._max[b] )) )
			{
				stack.Push( (_left[a], b) );
				stack.Push( (_right[a], b) );
			}
			else
			{
				stack.Push( (a, other._left[b]) );
				stack.Push( (a, other._right[b]) );
			}
		}
	}

	/// <summary>The nearest point of this set to a point, and the triangle it is on.</summary>
	public (Vec3 Point, int Triangle, float Distance) Nearest( Vec3 p, float maxDistance = float.MaxValue )
	{
		var best = maxDistance * maxDistance;
		var bestPoint = p;
		var bestTri = -1;

		if ( _nodes == 0 )
			return (p, -1, float.MaxValue);

		var stack = new Stack<int>();
		stack.Push( 0 );

		while ( stack.Count > 0 )
		{
			var n = stack.Pop();

			if ( BoxDistanceSquared( p, _min[n], _max[n] ) > best )
				continue;

			if ( _left[n] < 0 )
			{
				for ( var i = 0; i < _count[n]; i++ )
				{
					var t = _order[_start[n] + i];
					var q = MeshBVH.ClosestTriangle( p, A( t ), B( t ), C( t ) );
					var d = (q - p).LengthSquared;

					if ( d < best )
					{
						best = d;
						bestPoint = q;
						bestTri = t;
					}
				}

				continue;
			}

			// Nearer child last, so it is popped first and tightens the bound sooner.
			var l = _left[n];
			var r = _right[n];
			var dl = BoxDistanceSquared( p, _min[l], _max[l] );
			var dr = BoxDistanceSquared( p, _min[r], _max[r] );

			if ( dl < dr ) { stack.Push( r ); stack.Push( l ); }
			else { stack.Push( l ); stack.Push( r ); }
		}

		return (bestPoint, bestTri, bestTri < 0 ? float.MaxValue : MathF.Sqrt( best ));
	}

	internal (int A, int B, float Distance, Vec3 PointA, Vec3 PointB) NearestPair( TriangleSet other, float maxDistance )
	{
		var best = maxDistance;
		var result = (-1, -1, float.MaxValue, Vec3.Zero, Vec3.Zero);

		if ( _nodes == 0 || other._nodes == 0 )
			return result;

		var stack = new Stack<(int, int)>();
		stack.Push( (0, 0) );

		while ( stack.Count > 0 )
		{
			var (a, b) = stack.Pop();

			if ( MathF.Sqrt( BoxBoxDistanceSquared( _min[a], _max[a], other._min[b], other._max[b] ) ) > best )
				continue;

			var leafA = _left[a] < 0;
			var leafB = other._left[b] < 0;

			if ( leafA && leafB )
			{
				for ( var i = 0; i < _count[a]; i++ )
				{
					var s = _order[_start[a] + i];

					for ( var j = 0; j < other._count[b]; j++ )
					{
						var t = other._order[other._start[b] + j];
						var d = MeshClash.TriangleDistance( A( s ), B( s ), C( s ), other.A( t ), other.B( t ), other.C( t ), out var pa, out var pb );

						if ( d < best || result.Item1 < 0 && d <= best )
						{
							best = d;
							result = (s, t, d, pa, pb);
						}
					}
				}

				continue;
			}

			if ( leafB || (!leafA && Volume( _min[a], _max[a] ) >= Volume( other._min[b], other._max[b] )) )
			{
				stack.Push( (_left[a], b) );
				stack.Push( (_right[a], b) );
			}
			else
			{
				stack.Push( (a, other._left[b]) );
				stack.Push( (a, other._right[b]) );
			}
		}

		return result;
	}

	// --- build ---------------------------------------------------------------------------------

	void Build()
	{
		var n = Count;
		var capacity = Math.Max( 1, 2 * (n / LeafSize + 1) );
		_min = new Vec3[capacity];
		_max = new Vec3[capacity];
		_left = new int[capacity];
		_right = new int[capacity];
		_start = new int[capacity];
		_count = new int[capacity];
		_order = Enumerable.Range( 0, n ).ToArray();
		_nodes = 0;

		if ( n == 0 )
			return;

		var centroid = new Vec3[n];
		for ( var t = 0; t < n; t++ )
			centroid[t] = (A( t ) + B( t ) + C( t )) / 3f;

		Split( 0, n, centroid );
	}

	int Split( int start, int count, Vec3[] centroid )
	{
		var node = NewNode();
		_start[node] = start;
		_count[node] = count;
		_left[node] = -1;
		_right[node] = -1;
		BoxLeaf( node );

		if ( count <= LeafSize )
			return node;

		// Longest axis of the centroids, split at the median.
		var lo = centroid[_order[start]];
		var hi = lo;
		for ( var i = start + 1; i < start + count; i++ )
		{
			lo = Min( lo, centroid[_order[i]] );
			hi = Max( hi, centroid[_order[i]] );
		}

		var extent = hi - lo;
		var axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
		Array.Sort( _order, start, count, Comparer<int>.Create( ( p, q ) => Axis( centroid[p], axis ).CompareTo( Axis( centroid[q], axis ) ) ) );

		var half = count / 2;
		var l = Split( start, half, centroid );
		var r = Split( start + half, count - half, centroid );
		_left[node] = l;
		_right[node] = r;
		_count[node] = 0;
		_min[node] = Min( _min[l], _min[r] );
		_max[node] = Max( _max[l], _max[r] );
		return node;
	}

	int NewNode()
	{
		if ( _nodes == _min.Length )
		{
			var size = _nodes * 2;
			Array.Resize( ref _min, size );
			Array.Resize( ref _max, size );
			Array.Resize( ref _left, size );
			Array.Resize( ref _right, size );
			Array.Resize( ref _start, size );
			Array.Resize( ref _count, size );
		}

		return _nodes++;
	}

	void BoxLeaf( int node )
	{
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		for ( var i = 0; i < _count[node]; i++ )
		{
			var t = _order[_start[node] + i];
			lo = Min( lo, Min( A( t ), Min( B( t ), C( t ) ) ) );
			hi = Max( hi, Max( A( t ), Max( B( t ), C( t ) ) ) );
		}

		_min[node] = lo;
		_max[node] = hi;
	}

	static int[] WeldCorners( Vec3[] positions, int[] corners, float weld )
	{
		// Split normals and UV seams duplicate a vertex in place; for "do these triangles share a
		// corner" those copies are one vertex. Quantise to the weld size and take the first index
		// seen at each spot.
		var canonical = new int[positions.Length];
		var seen = new Dictionary<(long, long, long), int>();
		var inv = 1f / MathF.Max( weld, 1e-9f );

		for ( var v = 0; v < positions.Length; v++ )
		{
			var p = positions[v];
			var key = ((long)MathF.Round( p.x * inv ), (long)MathF.Round( p.y * inv ), (long)MathF.Round( p.z * inv ));

			if ( !seen.TryGetValue( key, out var first ) )
				seen[key] = first = v;

			canonical[v] = first;
		}

		var welded = new int[corners.Length];
		for ( var i = 0; i < corners.Length; i++ )
			welded[i] = canonical[corners[i]];

		return welded;
	}

	static float Axis( Vec3 v, int axis ) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;
	static Vec3 Min( Vec3 a, Vec3 b ) => new( MathF.Min( a.x, b.x ), MathF.Min( a.y, b.y ), MathF.Min( a.z, b.z ) );
	static Vec3 Max( Vec3 a, Vec3 b ) => new( MathF.Max( a.x, b.x ), MathF.Max( a.y, b.y ), MathF.Max( a.z, b.z ) );
	static float Volume( Vec3 lo, Vec3 hi ) => (hi.x - lo.x) * (hi.y - lo.y) * (hi.z - lo.z);

	static bool BoxesOverlap( Vec3 aLo, Vec3 aHi, Vec3 bLo, Vec3 bHi ) =>
		aLo.x <= bHi.x && bLo.x <= aHi.x && aLo.y <= bHi.y && bLo.y <= aHi.y && aLo.z <= bHi.z && bLo.z <= aHi.z;

	static float BoxDistanceSquared( Vec3 p, Vec3 lo, Vec3 hi )
	{
		var dx = MathF.Max( 0, MathF.Max( lo.x - p.x, p.x - hi.x ) );
		var dy = MathF.Max( 0, MathF.Max( lo.y - p.y, p.y - hi.y ) );
		var dz = MathF.Max( 0, MathF.Max( lo.z - p.z, p.z - hi.z ) );
		return dx * dx + dy * dy + dz * dz;
	}

	static float BoxBoxDistanceSquared( Vec3 aLo, Vec3 aHi, Vec3 bLo, Vec3 bHi )
	{
		var dx = MathF.Max( 0, MathF.Max( aLo.x - bHi.x, bLo.x - aHi.x ) );
		var dy = MathF.Max( 0, MathF.Max( aLo.y - bHi.y, bLo.y - aHi.y ) );
		var dz = MathF.Max( 0, MathF.Max( aLo.z - bHi.z, bLo.z - aHi.z ) );
		return dx * dx + dy * dy + dz * dz;
	}
}

/// <summary>
/// Where two meshes pass through each other, or one through itself, and how close two come.
///
/// The sign-off check for a posed take: a glove through a prop, a sleeve through the glove, a
/// finger through its neighbour. A CROSSING is two triangles that properly intersect - an edge of
/// one passes through the inside of the other. Two surfaces lying flat on each other are contact,
/// not a crossing, and are not counted: a finger resting on a button is what a press is meant to
/// look like.
///
/// DEPTH is how far the deepest crossing vertex sits behind the surface it went through: for each
/// vertex of a crossing triangle, the distance to the nearest point of the other mesh, counted
/// when the vertex is on the back side of that nearest face. "Back side" is the face normal, not
/// an inside/outside parity test, so it works on open shells - a glove or a sleeve is never
/// closed - and a mesh with flipped faces reports nonsense depths, which is a real problem with
/// that mesh worth seeing.
/// </summary>
public static class MeshClash
{
	public readonly record struct Crossing( int TriangleA, int TriangleB, int FaceA, int FaceB );

	public sealed class Report
	{
		/// <summary>Triangle pairs that cross. Each pair once.</summary>
		public int Crossings;

		/// <summary>The pairs themselves, up to the limit asked for.</summary>
		public List<Crossing> Pairs = new();

		/// <summary>How far the first mesh reaches behind the second's surface, at most.</summary>
		public float DepthAIntoB;

		/// <summary>How far the second mesh reaches behind the first's surface. Zero for a self check.</summary>
		public float DepthBIntoA;

		/// <summary>The deepest vertex, whichever mesh it is on.</summary>
		public Vec3 Deepest;

		public bool Clear => Crossings == 0;

		public float Depth => MathF.Max( DepthAIntoB, DepthBIntoA );

		public override string ToString() =>
			Crossings == 0 ? "clear" : Depth > 0f ? $"{Crossings} crossing(s), deepest {Depth:0.###} at {Deepest}" : $"{Crossings} crossing(s)";
	}

	public readonly record struct Gap( float Distance, Vec3 PointA, Vec3 PointB, int TriangleA, int TriangleB )
	{
		public bool Found => TriangleA >= 0;

		public override string ToString() => Found ? $"{Distance:0.###} ({PointA} to {PointB})" : "none in range";
	}

	/// <summary>Where <paramref name="a"/> and <paramref name="b"/> cross.</summary>
	public static Report Between( TriangleSet a, TriangleSet b, int keepPairs = 256 ) =>
		Check( a, b, keepPairs );

	/// <summary>Where a mesh crosses itself, skipping triangles that share a vertex.</summary>
	public static Report Self( TriangleSet a, int keepPairs = 256 ) =>
		Check( a, a, keepPairs );

	/// <summary>The closest the two meshes come. Zero when they cross; nothing found past
	/// <paramref name="maxDistance"/>.</summary>
	public static Gap Nearest( TriangleSet a, TriangleSet b, float maxDistance = float.MaxValue )
	{
		var (s, t, d, pa, pb) = a.NearestPair( b, maxDistance );
		return new Gap( s < 0 ? float.MaxValue : d, pa, pb, s, t );
	}

	static Report Check( TriangleSet a, TriangleSet b, int keepPairs )
	{
		var self = ReferenceEquals( a, b );
		var report = new Report();
		var crossingA = new HashSet<int>();
		var crossingB = new HashSet<int>();

		a.OverlappingPairs( b, ( s, t ) =>
		{
			if ( self && a.Adjacent( s, t ) )
				return;

			if ( !TrianglesCross( a.A( s ), a.B( s ), a.C( s ), b.A( t ), b.B( t ), b.C( t ) ) )
				return;

			report.Crossings++;
			crossingA.Add( s );
			crossingB.Add( t );

			// A self pair is unordered; store it lower index first so the same pair always
			// compares equal, whichever way round the walk met it.
			if ( self && s > t )
				(s, t) = (t, s);

			if ( report.Pairs.Count < keepPairs )
				report.Pairs.Add( new Crossing( s, t, a.SourceFace[s], b.SourceFace[t] ) );
		} );

		if ( report.Crossings == 0 || self )
			return report;

		report.DepthAIntoB = DeepestBehind( a, crossingA, b, out var deepA );
		report.DepthBIntoA = DeepestBehind( b, crossingB, a, out var deepB );
		report.Deepest = report.DepthAIntoB >= report.DepthBIntoA ? deepA : deepB;
		return report;
	}

	static float DeepestBehind( TriangleSet from, HashSet<int> triangles, TriangleSet into, out Vec3 where )
	{
		var deepest = 0f;
		where = Vec3.Zero;
		var done = new HashSet<int>();

		foreach ( var t in triangles )
		{
			for ( var k = 0; k < 3; k++ )
			{
				var v = from.Corners[t * 3 + k];

				if ( !done.Add( v ) )
					continue;

				var p = from.Positions[v];
				var (q, tri, d) = into.Nearest( p );

				if ( tri < 0 || Vec3.Dot( p - q, into.Normal( tri ) ) >= 0f )
					continue;

				if ( d > deepest )
				{
					deepest = d;
					where = p;
				}
			}
		}

		return deepest;
	}

	// --- triangle against triangle ------------------------------------------------------------

	/// <summary>
	/// Do two triangles properly cross? True when an edge of either passes through the other's
	/// interior. Coplanar triangles never cross (they can only touch), and neither do triangles
	/// that meet only at an edge or a corner.
	/// </summary>
	public static bool TrianglesCross( Vec3 a0, Vec3 a1, Vec3 a2, Vec3 b0, Vec3 b1, Vec3 b2 )
	{
		// Cheap rejection first (Möller): if all of one triangle is strictly on one side of the
		// other's plane, nothing can pass through. Most box-overlap pairs end here, before the six
		// segment tests.
		if ( OneSide( b0, b1, b2, a0, a1, a2 ) || OneSide( a0, a1, a2, b0, b1, b2 ) )
			return false;

		return EdgeThrough( a0, a1, b0, b1, b2 ) || EdgeThrough( a1, a2, b0, b1, b2 ) || EdgeThrough( a2, a0, b0, b1, b2 )
			|| EdgeThrough( b0, b1, a0, a1, a2 ) || EdgeThrough( b1, b2, a0, a1, a2 ) || EdgeThrough( b2, b0, a0, a1, a2 );
	}

	/// <summary>Are p, q and r all strictly on the same side of the plane through a, b, c?</summary>
	static bool OneSide( Vec3 a, Vec3 b, Vec3 c, Vec3 p, Vec3 q, Vec3 r )
	{
		var n = Vec3.Cross( b - a, c - a );
		var dp = Vec3.Dot( n, p - a );
		var dq = Vec3.Dot( n, q - a );
		var dr = Vec3.Dot( n, r - a );
		return dp > 0f && dq > 0f && dr > 0f || dp < 0f && dq < 0f && dr < 0f;
	}

	/// <summary>Does the segment p-q pass through the triangle's interior?</summary>
	static bool EdgeThrough( Vec3 p, Vec3 q, Vec3 a, Vec3 b, Vec3 c ) => SegmentHit( p, q, a, b, c, out _ );

	static bool SegmentHit( Vec3 p, Vec3 q, Vec3 a, Vec3 b, Vec3 c, out Vec3 hit )
	{
		// Möller-Trumbore on the segment, with both ends and the triangle's border excluded by a
		// hair: an edge that merely touches, or ends on the surface, is contact.
		hit = default;
		const float eps = 1e-6f;
		var dir = q - p;
		var e1 = b - a;
		var e2 = c - a;
		var h = Vec3.Cross( dir, e2 );
		var det = Vec3.Dot( e1, h );

		var scale = MathF.Max( 1e-12f, e1.Length * e2.Length * dir.Length );
		if ( MathF.Abs( det ) <= scale * 1e-7f )
			return false;

		var inv = 1f / det;
		var s = p - a;
		var u = Vec3.Dot( s, h ) * inv;

		if ( u <= eps || u >= 1f - eps )
			return false;

		var qv = Vec3.Cross( s, e1 );
		var v = Vec3.Dot( dir, qv ) * inv;

		if ( v <= eps || u + v >= 1f - eps )
			return false;

		var t = Vec3.Dot( e2, qv ) * inv;

		if ( t <= eps || t >= 1f - eps )
			return false;

		hit = p + dir * t;
		return true;
	}

	/// <summary>The closest two triangles come, and the two points it happens at.</summary>
	public static float TriangleDistance( Vec3 a0, Vec3 a1, Vec3 a2, Vec3 b0, Vec3 b1, Vec3 b2, out Vec3 pointA, out Vec3 pointB )
	{
		// Crossing triangles are zero apart; say where.
		if ( SegmentHit( a0, a1, b0, b1, b2, out var hit ) || SegmentHit( a1, a2, b0, b1, b2, out hit ) || SegmentHit( a2, a0, b0, b1, b2, out hit )
			|| SegmentHit( b0, b1, a0, a1, a2, out hit ) || SegmentHit( b1, b2, a0, a1, a2, out hit ) || SegmentHit( b2, b0, a0, a1, a2, out hit ) )
		{
			pointA = pointB = hit;
			return 0f;
		}

		// Otherwise the closest pair is a corner against the other face, or an edge against an edge.
		var best = float.MaxValue;
		pointA = pointB = a0;

		void Try( Vec3 pa, Vec3 pb, ref Vec3 outA, ref Vec3 outB )
		{
			var d = (pa - pb).LengthSquared;
			if ( d < best ) { best = d; outA = pa; outB = pb; }
		}

		Try( a0, MeshBVH.ClosestTriangle( a0, b0, b1, b2 ), ref pointA, ref pointB );
		Try( a1, MeshBVH.ClosestTriangle( a1, b0, b1, b2 ), ref pointA, ref pointB );
		Try( a2, MeshBVH.ClosestTriangle( a2, b0, b1, b2 ), ref pointA, ref pointB );
		Try( MeshBVH.ClosestTriangle( b0, a0, a1, a2 ), b0, ref pointA, ref pointB );
		Try( MeshBVH.ClosestTriangle( b1, a0, a1, a2 ), b1, ref pointA, ref pointB );
		Try( MeshBVH.ClosestTriangle( b2, a0, a1, a2 ), b2, ref pointA, ref pointB );

		Span<Vec3> ea = stackalloc Vec3[] { a0, a1, a1, a2, a2, a0 };
		Span<Vec3> eb = stackalloc Vec3[] { b0, b1, b1, b2, b2, b0 };

		for ( var i = 0; i < 6; i += 2 )
			for ( var j = 0; j < 6; j += 2 )
			{
				ClosestSegments( ea[i], ea[i + 1], eb[j], eb[j + 1], out var pa, out var pb );
				Try( pa, pb, ref pointA, ref pointB );
			}

		return MathF.Sqrt( best );
	}

	/// <summary>Closest points between segments p1-q1 and p2-q2 (Ericson, Real-Time Collision
	/// Detection, 5.1.9).</summary>
	public static void ClosestSegments( Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2, out Vec3 c1, out Vec3 c2 )
	{
		var d1 = q1 - p1;
		var d2 = q2 - p2;
		var r = p1 - p2;
		var a = Vec3.Dot( d1, d1 );
		var e = Vec3.Dot( d2, d2 );
		var f = Vec3.Dot( d2, r );
		float s, t;
		const float eps = 1e-12f;

		if ( a <= eps && e <= eps )
		{
			c1 = p1;
			c2 = p2;
			return;
		}

		if ( a <= eps )
		{
			s = 0f;
			t = Math.Clamp( f / e, 0f, 1f );
		}
		else
		{
			var c = Vec3.Dot( d1, r );

			if ( e <= eps )
			{
				t = 0f;
				s = Math.Clamp( -c / a, 0f, 1f );
			}
			else
			{
				var b = Vec3.Dot( d1, d2 );
				var denom = a * e - b * b;
				s = denom > eps ? Math.Clamp( (b * f - c * e) / denom, 0f, 1f ) : 0f;
				t = (b * s + f) / e;

				if ( t < 0f )
				{
					t = 0f;
					s = Math.Clamp( -c / a, 0f, 1f );
				}
				else if ( t > 1f )
				{
					t = 1f;
					s = Math.Clamp( (b - c) / a, 0f, 1f );
				}
			}
		}

		c1 = p1 + d1 * s;
		c2 = p2 + d2 * t;
	}
}
