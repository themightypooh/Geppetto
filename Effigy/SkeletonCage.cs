using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// An all-quad cage from a stick figure: joints with a thickness each, joined by limbs. The B-Mesh
/// (Ji, Liu &amp; Wang 2010) and SQM (Bærentzen, Misztal &amp; Wełnicka 2012) construction, the one
/// a skin modifier is built on.
///
/// JOINTS ARE HULLS, NOT CUBES. Where three or more limbs meet, the limb directions are points on
/// the joint's sphere and their convex hull is the joint's polyhedron (SQM's "branch node
/// polyhedron"). Directions that leave part of the sphere open — a T-junction is flat, three legs
/// of a tripod all point down — get extra points there, so the hull always wraps the joint. The
/// hull is triangles; one Catmull-Clark split turns each into three quads, and the quads round a
/// limb's own hull corner are taken out. What is left is a hole bounded by an even ring of
/// vertices (twice the corner's valence) for that limb to attach to; the quads round the extra
/// points stay and close the joint. Any number of limbs at a joint works, which is what a hand,
/// a tree fork or a spider's body needs.
///
/// LIMBS ARE TUBES OF RINGS, aligned by parallel transport: the first ring's frame is carried along
/// the limb, turned by the smallest rotation at every bend, so a long chain arrives untwisted, and
/// the last ring is matched to the joint it enters at the rotation that twists least. A limb between
/// two joints whose rings differ in size changes over with a row of three-to-one quads, still quads.
/// Free ends are capped by a smaller ring and a quad fan.
///
/// ALWAYS QUADS, CLOSED AND MANIFOLD, by construction: every edge is shared by exactly two faces
/// wound the same way. A stick figure with a loop in it has no tube order and is refused.
///
/// <see cref="Options.Fit"/> is B-Mesh's "evolution" step: after subdivision every vertex is pulled
/// onto the sphere-swept surface (the smooth union of round cones along the limbs), so the radii
/// typed are the radii you get, not what a smoothed cage shrinks to.
/// </summary>
public static class SkeletonCage
{
	/// <summary>A stick figure: joints (position and radius) and limbs between them.</summary>
	public sealed class Graph
	{
		public readonly List<Vec3> Nodes = new();
		public readonly List<float> Radii = new();
		public readonly List<(int A, int B)> Edges = new();

		/// <summary>A joint, or the existing one within <paramref name="weld"/> of <paramref name="p"/>
		/// (which keeps the larger radius). Welding is how chains branch: start a new chain where an
		/// old one has a joint.</summary>
		public int AddNode( Vec3 p, float radius, float weld = 1e-2f )
		{
			for ( var i = 0; i < Nodes.Count; i++ )
			{
				if ( (Nodes[i] - p).LengthSquared <= weld * weld )
				{
					Radii[i] = MathF.Max( Radii[i], radius );
					return i;
				}
			}

			Nodes.Add( p );
			Radii.Add( radius );
			return Nodes.Count - 1;
		}

		/// <summary>A limb between two joints. A joint to itself, or a limb already there, is ignored.</summary>
		public void AddEdge( int a, int b )
		{
			if ( a == b )
				return;

			foreach ( var (x, y) in Edges )
				if ( (x == a && y == b) || (x == b && y == a) )
					return;

			Edges.Add( (a, b) );
		}
	}

	public sealed class Options
	{
		/// <summary>Sides of a limb that touches no branching joint (a lone stick, a chain). Even, 4 or more.
		/// Limbs leaving a branching joint take their ring size from the joint.</summary>
		public int Sides = 8;

		/// <summary>Distance between rings along a limb, or 0 for twice the limb's radius.</summary>
		public float RingSpacing = 0f;

		/// <summary>Catmull-Clark levels after the cage is built.</summary>
		public int SmoothLevels = 0;

		/// <summary>Pull the subdivided surface onto the sphere-swept shape (B-Mesh evolution).</summary>
		public bool Fit = false;

		/// <summary>Fillet where limbs meet when fitting, in model units; negative for a third of the
		/// mean radius.</summary>
		public float FitBlend = -1f;
	}

	/// <summary>What the cage was built from, for reports and tests.</summary>
	public sealed class Report
	{
		public int BranchJoints;
		public int MaxBranches;
		public int Transitions;
		public int ExtraJointPoints;
	}

	public static PolyMesh Build( Graph graph, Options options = null ) => Build( graph, options, out _ );

	public static PolyMesh Build( Graph graph, Options options, out Report report )
	{
		options ??= new Options();
		report = new Report();

		if ( graph is null || graph.Edges.Count == 0 )
			throw new InvalidOperationException( "There are no limbs to build a body on. Give the stick figure at least one limb between two joints." );

		if ( options.Sides < 4 || options.Sides % 2 != 0 )
			throw new InvalidOperationException( $"Sides is {options.Sides}; a quad tube needs an even number of sides, 4 or more. Use 4, 6, 8 or 12." );

		var n = graph.Nodes.Count;
		for ( var i = 0; i < n; i++ )
		{
			if ( !(graph.Radii[i] > 0f) || !float.IsFinite( graph.Radii[i] ) )
				throw new InvalidOperationException( $"Joint {i + 1} has radius {graph.Radii[i]}; every joint needs a thickness above zero." );
		}

		var adj = new List<int>[n];
		for ( var i = 0; i < n; i++ )
			adj[i] = new List<int>();

		// A loop has no first and last ring to bridge between, so it is refused, with the limb
		// that closes it named.
		var parent = Enumerable.Range( 0, n ).ToArray();
		int Find( int x ) { while ( parent[x] != x ) x = parent[x] = parent[parent[x]]; return x; }

		foreach ( var (a, b) in graph.Edges )
		{
			if ( (graph.Nodes[a] - graph.Nodes[b]).LengthSquared < 1e-10f )
				throw new InvalidOperationException( $"Joints {a + 1} and {b + 1} are in the same place, so the limb between them has no length. Move one, or weld them." );

			var ra = Find( a );
			var rb = Find( b );
			if ( ra == rb )
				throw new InvalidOperationException( $"The limb from joint {a + 1} to joint {b + 1} closes a loop. A skin is built along branches, not round rings: remove one limb of the loop." );

			parent[ra] = rb;
			adj[a].Add( b );
			adj[b].Add( a );
		}

		var builder = new Builder( graph, options, adj, report );
		var cage = builder.Run();

		if ( options.SmoothLevels > 0 )
			cage = CatmullClark.Subdivide( cage, options.SmoothLevels );

		if ( options.Fit )
			FitToSweep( cage, graph, options );

		return cage;
	}

	/// <summary>
	/// B-Mesh's evolution, done directly: each vertex walks along the field gradient to the zero of
	/// the smooth union of round cones (one per limb). Newton steps on a distance field converge
	/// in two or three; five is the budget.
	/// </summary>
	static void FitToSweep( PolyMesh mesh, Graph graph, Options options )
	{
		var leaves = graph.Edges.Select( e => (Sdf)new SdfRoundCone( graph.Nodes[e.A], graph.Radii[e.A], graph.Nodes[e.B], graph.Radii[e.B] ) ).ToArray();
		var mean = graph.Radii.Average();
		var k = options.FitBlend >= 0f ? options.FitBlend : mean / 3f;
		Sdf field = leaves.Length == 1 ? leaves[0] : new SdfBlend( k, leaves );
		field.Prepare( 0f );
		var h = MathF.Max( graph.Radii.Min() * 0.01f, 1e-4f );

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			for ( var step = 0; step < 5; step++ )
			{
				var d = field.Eval( p );
				if ( MathF.Abs( d ) < h * 0.1f )
					break;

				var g = new Vec3(
					field.Eval( p + new Vec3( h, 0, 0 ) ) - field.Eval( p - new Vec3( h, 0, 0 ) ),
					field.Eval( p + new Vec3( 0, h, 0 ) ) - field.Eval( p - new Vec3( 0, h, 0 ) ),
					field.Eval( p + new Vec3( 0, 0, h ) ) - field.Eval( p - new Vec3( 0, 0, h ) ) ).Normal;

				if ( g.LengthSquared < 0.5f )
					break;

				p -= g * d;
			}

			mesh.Positions[i] = p;
		}
	}

	sealed class Builder
	{
		readonly Graph _g;
		readonly Options _o;
		readonly List<int>[] _adj;
		readonly Report _report;
		readonly List<Vec3> _pos = new();
		readonly List<int[]> _faces = new();

		/// <summary>The ring a branching joint leaves for each of its limbs, keyed (joint, neighbour),
		/// counter-clockwise round the direction from the joint to the neighbour.</summary>
		readonly Dictionary<(int, int), int[]> _jointRing = new();

		public Builder( Graph g, Options o, List<int>[] adj, Report report )
		{
			_g = g;
			_o = o;
			_adj = adj;
			_report = report;
		}

		public PolyMesh Run()
		{
			for ( var i = 0; i < _g.Nodes.Count; i++ )
			{
				if ( _adj[i].Count >= 3 )
					BuildJoint( i );
			}

			// Chains: maximal runs through two-limb joints, each walked once, from a branching joint
			// where there is one so a free end is always the far end.
			var done = new HashSet<(int, int)>();
			for ( var pass = 0; pass < 2; pass++ )
			{
				for ( var s = 0; s < _g.Nodes.Count; s++ )
				{
					var degree = _adj[s].Count;
					if ( degree == 2 || degree == 0 || (pass == 0 && degree < 3) )
						continue;

					foreach ( var first in _adj[s] )
					{
						if ( done.Contains( (s, first) ) )
							continue;

						var chain = new List<int> { s, first };
						done.Add( (s, first) );
						done.Add( (first, s) );
						var prev = s;
						var cur = first;
						while ( _adj[cur].Count == 2 )
						{
							var next = _adj[cur][0] == prev ? _adj[cur][1] : _adj[cur][0];
							done.Add( (cur, next) );
							done.Add( (next, cur) );
							chain.Add( next );
							prev = cur;
							cur = next;
						}

						BuildChain( chain );
					}
				}
			}

			// Compact: a limb's own hull corner is never used.
			var used = new int[_pos.Count];
			Array.Fill( used, -1 );
			var mesh = new PolyMesh();
			foreach ( var f in _faces )
			{
				var idx = new int[f.Length];
				for ( var i = 0; i < f.Length; i++ )
				{
					if ( used[f[i]] < 0 )
					{
						used[f[i]] = mesh.Positions.Count;
						mesh.Positions.Add( _pos[f[i]] );
					}

					idx[i] = used[f[i]];
				}

				mesh.AddFace( idx );
			}

			return mesh;
		}

		int Add( Vec3 p )
		{
			_pos.Add( p );
			return _pos.Count - 1;
		}

		// ---- joints ----

		void BuildJoint( int node )
		{
			var c = _g.Nodes[node];
			var r = _g.Radii[node];
			var nbrs = _adj[node];
			var k = nbrs.Count;
			var dirs = nbrs.Select( b => (_g.Nodes[b] - c).Normal ).ToList();

			SeparateCloseDirections( dirs );

			var points = new List<Vec3>( dirs );
			List<ConvexHull.HullFace> hull = null;
			for ( var attempt = 0; attempt < 12; attempt++ )
			{
				var built = ConvexHull.Build( points, 1e-6f );
				if ( built is { } h )
				{
					// The hull numbers its own compacted points; put the faces back on ours.
					var back = h.Points.Select( q => points.IndexOf( q ) ).ToArray();
					var faces = h.Faces.Select( f => new ConvexHull.HullFace( back[f.A], back[f.B], back[f.C] ) ).ToList();
					if ( UsesAll( faces, points.Count ) && MinFaceDistance( points, faces ) >= 0.25f )
					{
						hull = faces;
						break;
					}
				}

				points.Add( FarthestCandidate( points, dirs ) );
			}

			if ( hull is null )
				throw new InvalidOperationException( $"The joint at {Fmt( c )} has {k} limbs leaving it in directions too close together to wrap. Spread the limbs apart, or move one to its own joint." );

			_report.BranchJoints++;
			_report.MaxBranches = Math.Max( _report.MaxBranches, k );
			_report.ExtraJointPoints += points.Count - k;

			// One Catmull-Clark split of the hull: corners, edge points and face points, all on the
			// joint's sphere.
			var corner = new int[points.Count];
			for ( var i = 0; i < points.Count; i++ )
				corner[i] = i < k ? -1 : Add( c + points[i] * r );

			var edgePoint = new Dictionary<EdgeKey, int>();
			int EdgePoint( int a, int b )
			{
				var key = new EdgeKey( a, b );
				if ( !edgePoint.TryGetValue( key, out var v ) )
					edgePoint[key] = v = Add( c + (points[a] + points[b]).Normal * r );
				return v;
			}

			var facePoint = new int[hull.Count];
			for ( var f = 0; f < hull.Count; f++ )
			{
				var h = hull[f];
				facePoint[f] = Add( c + (points[h.A] + points[h.B] + points[h.C]).Normal * r );
			}

			for ( var f = 0; f < hull.Count; f++ )
			{
				var t = new[] { hull[f].A, hull[f].B, hull[f].C };
				for ( var i = 0; i < 3; i++ )
				{
					var x = t[i];
					if ( x < k )
						continue;

					var next = t[(i + 1) % 3];
					var prev = t[(i + 2) % 3];
					_faces.Add( new[] { corner[x], EdgePoint( x, next ), facePoint[f], EdgePoint( prev, x ) } );
				}
			}

			// Each limb's ring: walk the fan round its corner. Face (v, x1, x2) is wound outward,
			// so e(v,x1), F, e(v,x2) runs counter-clockwise round the limb's direction.
			for ( var v = 0; v < k; v++ )
			{
				var fan = new Dictionary<int, (int Next, int Face)>();
				for ( var f = 0; f < hull.Count; f++ )
				{
					var t = new[] { hull[f].A, hull[f].B, hull[f].C };
					var at = Array.IndexOf( t, v );
					if ( at < 0 )
						continue;

					fan[t[(at + 1) % 3]] = (t[(at + 2) % 3], f);
				}

				var ring = new List<int>();
				var start = fan.Keys.First();
				var x1 = start;
				do
				{
					var (x2, face) = fan[x1];
					ring.Add( EdgePoint( v, x1 ) );
					ring.Add( facePoint[face] );
					x1 = x2;
				}
				while ( x1 != start && ring.Count <= 2 * fan.Count );

				_jointRing[(node, nbrs[v])] = ring.ToArray();
			}
		}

		/// <summary>Two limbs leaving a joint almost along each other would share a hull corner.
		/// Nudge them to at least eight degrees apart.</summary>
		static void SeparateCloseDirections( List<Vec3> dirs )
		{
			var minCos = MathF.Cos( 8f * MathF.PI / 180f );
			for ( var pass = 0; pass < 8; pass++ )
			{
				var moved = false;
				for ( var i = 0; i < dirs.Count; i++ )
				{
					for ( var j = i + 1; j < dirs.Count; j++ )
					{
						if ( Vec3.Dot( dirs[i], dirs[j] ) <= minCos )
							continue;

						var w = (dirs[i] - dirs[j]).Normal;
						if ( w.LengthSquared < 0.5f )
							w = AnyPerpendicular( dirs[i] );

						dirs[i] = (dirs[i] + w * 0.08f).Normal;
						dirs[j] = (dirs[j] - w * 0.08f).Normal;
						moved = true;
					}
				}

				if ( !moved )
					return;
			}
		}

		static bool UsesAll( List<ConvexHull.HullFace> faces, int count )
		{
			var seen = new bool[count];
			foreach ( var f in faces )
			{
				seen[f.A] = true;
				seen[f.B] = true;
				seen[f.C] = true;
			}

			return seen.All( s => s );
		}

		static float MinFaceDistance( List<Vec3> p, List<ConvexHull.HullFace> faces )
		{
			var min = float.MaxValue;
			foreach ( var f in faces )
			{
				var nrm = Vec3.Cross( p[f.B] - p[f.A], p[f.C] - p[f.A] ).Normal;
				min = MathF.Min( min, Vec3.Dot( nrm, p[f.A] ) );
			}

			return min;
		}

		/// <summary>
		/// The direction most open on the joint's sphere. The candidates are tried in a fixed order
		/// — the normal of the plane the limbs best lie in first, then away from them, then the axes,
		/// then a spread — so a mirrored skeleton gets mirrored joints.
		/// </summary>
		static Vec3 FarthestCandidate( List<Vec3> points, List<Vec3> dirs )
		{
			var candidates = new List<Vec3>();
			var normal = LeastSpreadAxis( dirs );
			candidates.Add( normal );
			candidates.Add( -normal );
			var mean = dirs.Aggregate( Vec3.Zero, ( a, b ) => a + b );
			if ( mean.LengthSquared > 1e-6f )
				candidates.Add( -mean.Normal );
			candidates.AddRange( new[] { new Vec3( 0, 0, 1 ), new Vec3( 0, 0, -1 ), new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, -1, 0 ) } );
			const int spread = 96;
			for ( var i = 0; i < spread; i++ )
			{
				var y = 1f - 2f * (i + 0.5f) / spread;
				var rr = MathF.Sqrt( 1f - y * y );
				var phi = i * 2.399963f;
				candidates.Add( new Vec3( rr * MathF.Cos( phi ), rr * MathF.Sin( phi ), y ) );
			}

			var best = candidates[0];
			var bestScore = float.MaxValue;
			foreach ( var cand in candidates )
			{
				var nearest = points.Max( q => Vec3.Dot( q, cand ) );
				if ( nearest < bestScore - 1e-4f )
				{
					bestScore = nearest;
					best = cand;
				}
			}

			return best;
		}

		/// <summary>The axis the directions spread least along: the smallest eigenvector of their
		/// scatter, by power iteration on its complement.</summary>
		static Vec3 LeastSpreadAxis( List<Vec3> dirs )
		{
			float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
			foreach ( var d in dirs )
			{
				xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
				yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
			}

			var tr = xx + yy + zz;
			var v = new Vec3( 0.31f, 0.47f, 0.83f ).Normal;
			for ( var i = 0; i < 64; i++ )
			{
				var m = new Vec3(
					(tr - xx) * v.x - xy * v.y - xz * v.z,
					-xy * v.x + (tr - yy) * v.y - yz * v.z,
					-xz * v.x - yz * v.y + (tr - zz) * v.z );
				if ( m.LengthSquared < 1e-12f )
					break;
				v = m.Normal;
			}

			// Snap to a world axis when it is one, so symmetric rigs stay symmetric to the bit.
			foreach ( var axis in new[] { new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ) } )
			{
				var d = Vec3.Dot( v, axis );
				if ( MathF.Abs( d ) > 0.9999f )
					return axis * MathF.Sign( d );
			}

			return v;
		}

		// ---- limbs ----

		void BuildChain( List<int> chain )
		{
			var m = chain.Count - 1;
			var c0 = chain[0];
			var cm = chain[m];

			int[] startRing = _adj[c0].Count >= 3 ? _jointRing[(c0, chain[1])] : null;
			int[] endRing = _adj[cm].Count >= 3 ? Reverse( _jointRing[(cm, chain[m - 1])] ) : null;
			var count = startRing?.Length ?? endRing?.Length ?? _o.Sides;

			var t0 = Dir( c0, chain[1] );
			Vec3 u;
			int[] current;

			if ( startRing is not null )
			{
				// Start from the ring vertex nearest a fixed direction, not wherever the hull walk
				// began, so a mirrored limb gets mirrored rings.
				// The tube's frame is that fixed direction too, exactly, so the rings that follow do
				// not inherit the hull's slight unevenness.
				u = ReferencePerpendicular( t0 );
				current = StartNear( startRing, u, t0 );
			}
			else
			{
				u = ReferencePerpendicular( t0 );
				current = Ring( _g.Nodes[c0], t0, u, _g.Radii[c0], count );
				Cap( Reverse( current ), _g.Nodes[c0], -t0, Perp( u, t0 ), _g.Radii[c0] );
			}

			var tPrev = t0;
			for ( var j = 0; j < m; j++ )
			{
				var a = chain[j];
				var b = chain[j + 1];
				var t = Dir( a, b );

				// Parallel transport through the bend at a two-limb joint, with its ring on the
				// bisecting plane.
				if ( j > 0 )
				{
					var mid = (tPrev + t).Normal;
					if ( mid.LengthSquared < 0.5f )
						mid = t;
					u = Transport( u, tPrev, mid );
					var ring = Ring( _g.Nodes[a], mid, u, _g.Radii[a], count );
					Bridge( current, ring );
					current = ring;
					u = Transport( u, mid, t );
				}
				else
				{
					u = Transport( u, tPrev, t );
				}

				var len = (_g.Nodes[b] - _g.Nodes[a]).Length;
				var offA = j == 0 && startRing is not null ? Extent( startRing, _g.Nodes[a], t ) : 0f;
				var offB = j == m - 1 && endRing is not null ? Extent( endRing, _g.Nodes[b], -t ) : 0f;
				var free = len - offA - offB;
				var ra = _g.Radii[a];
				var rb = _g.Radii[b];
				var spacing = _o.RingSpacing > 0f ? _o.RingSpacing : (ra + rb);
				var inner = free > 0f ? Math.Clamp( (int)MathF.Ceiling( free / spacing ) - 1, 0, 64 ) : 0;

				for ( var i = 1; i <= inner; i++ )
				{
					var s = offA + free * i / (inner + 1);
					var ring = Ring( _g.Nodes[a] + t * s, t, u, ra + (rb - ra) * (s / len), count );
					Bridge( current, ring );
					current = ring;
				}

				tPrev = t;
			}

			if ( endRing is not null )
			{
				var aligned = Align( current, endRing, tPrev );
				if ( aligned.Length != current.Length )
					_report.Transitions++;
				Bridge( current, aligned );
			}
			else
			{
				var ring = Ring( _g.Nodes[cm], tPrev, u, _g.Radii[cm], count );
				Bridge( current, ring );
				Cap( ring, _g.Nodes[cm], tPrev, Perp( u, tPrev ), _g.Radii[cm] );
			}
		}

		/// <summary>A ring of <paramref name="count"/> vertices round <paramref name="axis"/>, the first
		/// along <paramref name="u"/>, counter-clockwise round the axis.</summary>
		int[] Ring( Vec3 center, Vec3 axis, Vec3 u, float radius, int count )
		{
			var x = Perp( u, axis ).Normal;
			if ( x.LengthSquared < 0.5f )
				x = AnyPerpendicular( axis );
			var y = Vec3.Cross( axis, x );
			var ring = new int[count];
			for ( var i = 0; i < count; i++ )
			{
				var a = 2f * MathF.PI * i / count;
				ring[i] = Add( center + (x * MathF.Cos( a ) + y * MathF.Sin( a )) * radius );
			}

			return ring;
		}

		/// <summary>
		/// Quads between two rings, both counter-clockwise round the direction from the first to the
		/// second and starting side by side. Rings of different (even) sizes change over with
		/// three-to-one quads spread evenly round the row.
		/// </summary>
		void Bridge( int[] p, int[] q )
		{
			var np = p.Length;
			var nq = q.Length;
			if ( np == nq )
			{
				for ( var i = 0; i < np; i++ )
					_faces.Add( new[] { p[i], p[(i + 1) % np], q[(i + 1) % nq], q[i] } );
				return;
			}

			var doubles = Math.Abs( np - nq ) / 2;
			var steps = Math.Min( np, nq ) + doubles;
			int pi = 0, qi = 0;
			for ( var s = 0; s < steps; s++ )
			{
				var isDouble = (s + 1) * doubles / steps > s * doubles / steps;
				if ( isDouble && np > nq )
				{
					_faces.Add( new[] { p[pi % np], p[(pi + 1) % np], p[(pi + 2) % np], q[qi % nq] } );
					pi += 2;
				}
				else if ( isDouble )
				{
					_faces.Add( new[] { p[pi % np], q[(qi + 2) % nq], q[(qi + 1) % nq], q[qi % nq] } );
					qi += 2;
				}
				else
				{
					_faces.Add( new[] { p[pi % np], p[(pi + 1) % np], q[(qi + 1) % nq], q[qi % nq] } );
					pi++;
					qi++;
				}
			}
		}

		/// <summary>Close a free end: a smaller ring a little further out, then a quad fan (or one quad
		/// on a four-sided limb). <paramref name="ring"/> runs counter-clockwise round
		/// <paramref name="outward"/>.</summary>
		void Cap( int[] ring, Vec3 center, Vec3 outward, Vec3 u, float radius )
		{
			var first = Perp( _pos[ring[0]] - center, outward );
			var tipRing = Ring( center + outward * (radius * 0.55f), outward, first.LengthSquared > 1e-8f ? first : u, radius * 0.8f, ring.Length );
			Bridge( ring, tipRing );

			if ( tipRing.Length == 4 )
			{
				_faces.Add( tipRing );
				return;
			}

			var tip = Add( center + outward * radius );
			for ( var i = 0; i < tipRing.Length; i += 2 )
				_faces.Add( new[] { tipRing[i], tipRing[i + 1], tipRing[(i + 2) % tipRing.Length], tip } );
		}

		/// <summary>The joint ring <paramref name="target"/> turned so its first vertex sits beside
		/// <paramref name="from"/>'s first, measured round <paramref name="axis"/>: the turn that
		/// twists least.</summary>
		int[] Align( int[] from, int[] target, Vec3 axis )
		{
			var cf = Centroid( from );
			var ct = Centroid( target );
			var df = from.Select( v => Perp( _pos[v] - cf, axis ).Normal ).ToArray();
			var dt = target.Select( v => Perp( _pos[v] - ct, axis ).Normal ).ToArray();

			var best = 0;
			var bestCost = float.MaxValue;
			for ( var off = 0; off < target.Length; off++ )
			{
				float cost;
				if ( from.Length == target.Length )
				{
					cost = 0f;
					for ( var i = 0; i < from.Length; i++ )
						cost += (df[i] - dt[(i + off) % target.Length]).LengthSquared;
				}
				else
				{
					cost = (df[0] - dt[off]).LengthSquared;
				}

				if ( cost < bestCost - 1e-6f )
				{
					bestCost = cost;
					best = off;
				}
			}

			var aligned = new int[target.Length];
			for ( var i = 0; i < target.Length; i++ )
				aligned[i] = target[(i + best) % target.Length];
			return aligned;
		}

		int[] StartNear( int[] ring, Vec3 direction, Vec3 axis )
		{
			var c = Centroid( ring );
			var best = 0;
			var bestDot = float.MinValue;
			for ( var i = 0; i < ring.Length; i++ )
			{
				var d = Vec3.Dot( Perp( _pos[ring[i]] - c, axis ).Normal, direction );
				if ( d > bestDot + 1e-5f )
				{
					bestDot = d;
					best = i;
				}
			}

			var turned = new int[ring.Length];
			for ( var i = 0; i < ring.Length; i++ )
				turned[i] = ring[(i + best) % ring.Length];
			return turned;
		}

		float Extent( int[] ring, Vec3 node, Vec3 t ) => ring.Max( v => Vec3.Dot( _pos[v] - node, t ) );

		Vec3 Centroid( int[] ring ) => ring.Aggregate( Vec3.Zero, ( a, v ) => a + _pos[v] ) / ring.Length;

		Vec3 Dir( int a, int b ) => (_g.Nodes[b] - _g.Nodes[a]).Normal;

		static int[] Reverse( int[] ring )
		{
			var r = (int[])ring.Clone();
			Array.Reverse( r );
			return r;
		}
	}

	static Vec3 Perp( Vec3 v, Vec3 axis ) => v - axis * Vec3.Dot( v, axis );

	/// <summary>The smallest rotation that takes <paramref name="from"/> to <paramref name="to"/>,
	/// applied to <paramref name="v"/> (Rodrigues). Parallel transport, one bend at a time.</summary>
	static Vec3 Transport( Vec3 v, Vec3 from, Vec3 to )
	{
		var axis = Vec3.Cross( from, to );
		var s = axis.Length;
		var c = Vec3.Dot( from, to );
		if ( s < 1e-7f )
			return c > 0f ? v : -v;

		var k = axis / s;
		return v * c + Vec3.Cross( k, v ) * s + k * (Vec3.Dot( k, v ) * (1f - c));
	}

	static Vec3 AnyPerpendicular( Vec3 axis )
	{
		var helper = MathF.Abs( axis.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
		return Vec3.Cross( axis, helper ).Normal;
	}

	/// <summary>A stable first direction for a free limb: world up laid flat on the ring, or world
	/// forward for a limb that points up.</summary>
	static Vec3 ReferencePerpendicular( Vec3 axis )
	{
		var p = Perp( new Vec3( 0, 0, 1 ), axis );
		if ( p.LengthSquared < 0.01f )
			p = Perp( new Vec3( 1, 0, 0 ), axis );
		return p.Normal;
	}

	static string Fmt( Vec3 p ) => $"{p.x:0.##},{p.y:0.##},{p.z:0.##}";
}
