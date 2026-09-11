using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Quadric error metric decimation — the tool that takes triangles AWAY.
///
/// Everything else in this kernel adds density: Catmull-Clark multiplies it by four a level, the
/// sculpt subdivides to get somewhere to put detail, and a boolean splits faces to make the cut.
/// Nothing removed any, which was survivable while every body started as a primitive and stopped
/// being survivable the moment <see cref="ImportFeature"/> landed. A generated mesh — Meshy, a
/// photogrammetry scan, a sculpt somebody else finished — arrives at hundreds of thousands of
/// triangles because the generator had no reason to care, and at that size the mesh is not a cage:
/// you cannot subdivide it, you cannot sculpt on it, weight painting is a slideshow, and the
/// exporter writes a model nothing will load. The import is the shape you wanted and the wrong
/// number of triangles, and until now the tool had no answer to the second half.
///
/// GARLAND AND HECKBERT, 1997, and deliberately nothing cleverer. The cost of collapsing an edge is
/// the squared distance from the surviving vertex to the planes of every triangle the two endpoints
/// touched, which is a quadratic form and therefore a symmetric 4x4 matrix you can ADD — that is
/// the whole trick. Merge two vertices and their error matrices sum, so a vertex that has already
/// absorbed forty of its neighbours still remembers all forty original planes at the cost of one
/// matrix. Greedy, cheap per step, and it keeps silhouettes because a vertex on a crease has a
/// quadric that is expensive to move in two directions at once.
///
/// WHAT COMES OUT IS TRIANGLES. Even where quads went in. That is a real loss — see PolyMesh's note
/// on why every primitive here is quad-dominant, and the rig's on quads deforming where triangles
/// pinch — and it is why this is not a general "make it simpler" button to reach for on a CAD part.
/// Quad retopology is a different and much larger problem, and the honest thing is to say so rather
/// than to ship a triangle soup that claims to be a cage. Use this on the dense import it was
/// written for; build the cage the CAD way.
///
/// THINGS IT REFUSES TO DO, each of which is a way a naive collapse loop wrecks a mesh:
///
/// - <b>Punch through the surface.</b> The link condition (see <see cref="LinkConditionHolds"/>)
///   rejects a collapse that would weld two parts of the mesh that were only near each other, which
///   is what turns a manifold into a non-manifold and what makes the result unexportable.
/// - <b>Turn a triangle inside out.</b> Moving a vertex can flip a neighbour it was not part of the
///   decision about. Every surviving triangle is checked against its own old normal first.
/// - <b>Eat the border.</b> A boundary edge — one triangle, not two — and a material seam both get
///   a constraint plane standing perpendicular to the surface, so sliding along the border is cheap
///   and moving off it is not. An open part keeps its opening the shape it was.
///
/// It is a Feature, not a post-process, for the reason <see cref="SubdivideFeature"/> is: the
/// history has to stay rollable. Roll above the Remesh and the dense import is still there to
/// re-target at a different budget.
/// </summary>
public static class Decimate
{
	/// <summary>What to aim for and what to protect. Defaults are the ones a dense import wants.</summary>
	public sealed class Options
	{
		/// <summary>
		/// Triangles to stop at. Zero means "use <see cref="Ratio"/> instead", which is the default
		/// because a budget is only meaningful once you know what you started with.
		/// </summary>
		public int TargetTriangles;

		/// <summary>Fraction of the input triangle count to keep, when <see cref="TargetTriangles"/>
		/// is zero. 0.05 means "one twentieth of what came in".</summary>
		public float Ratio = 0.5f;

		/// <summary>
		/// Weld vertices that share a position before starting.
		///
		/// ON BY DEFAULT BECAUSE AN UNWELDED MESH CANNOT BE DECIMATED AT ALL, and looks fine until
		/// you try. Plenty of exporters write every triangle with its own three vertices; every edge
		/// in that mesh has exactly one triangle, so every edge is a boundary, every collapse is
		/// refused, and the operation returns the mesh unchanged with no error to explain itself.
		/// Welding first is not a favour, it is the difference between working and quietly not.
		///
		/// It merges by POSITION only, so per-corner UVs and material seams survive it — that is
		/// what per-corner UVs are for.
		/// </summary>
		public bool Weld = true;

		/// <summary>
		/// How close two positions have to be to weld, as a fraction of the mesh's bounds diagonal.
		/// A fraction rather than a distance because Effigy's units are dimensionless — see
		/// <see cref="PolyMesh.BoundsDiagonal"/>.
		/// </summary>
		public float WeldTolerance = 1e-5f;

		/// <summary>
		/// Hold open borders and material seams in place. Off, a decimated shell loses the shape of
		/// its opening and a two-material part smears the line between them.
		/// </summary>
		public bool PreserveBoundary = true;

		/// <summary>
		/// Stop early if the cheapest collapse left costs more than this, even with the target
		/// unmet. The unit is squared distance in model units, so it scales with the model and is
		/// off by default: a target the user typed is a target, and silently stopping short of it
		/// is worse than the extra error for the case this was written for.
		/// </summary>
		public float MaxError = float.MaxValue;

		/// <summary>
		/// How much a surviving triangle's normal may turn before the collapse is refused, as a
		/// cosine. 0.2 lets a face rotate up to about 78 degrees, which sounds generous and is —
		/// it is a check for INVERSION, not for accuracy, and the quadric is what keeps the surface
		/// where it was. Tightening it toward 1 mostly stops the decimation early on a bumpy mesh.
		/// </summary>
		public float FlipThreshold = 0.2f;
	}

	/// <summary>What a run did, so a caller can report it rather than diff two meshes to find out.</summary>
	public readonly struct Result
	{
		public readonly PolyMesh Mesh;
		public readonly int FromTriangles;
		public readonly int ToTriangles;
		public readonly int Welded;

		/// <summary>The cost of the most expensive collapse taken. Squared distance in model units;
		/// compare it against the bounds diagonal to know whether it mattered.</summary>
		public readonly float WorstError;

		/// <summary>False when it ran out of legal collapses before reaching the target — the mesh
		/// is as small as this algorithm can make it without breaking it.</summary>
		public readonly bool ReachedTarget;

		public Result( PolyMesh mesh, int from, int to, int welded, float worstError, bool reachedTarget )
		{
			Mesh = mesh;
			FromTriangles = from;
			ToTriangles = to;
			Welded = welded;
			WorstError = worstError;
			ReachedTarget = reachedTarget;
		}
	}

	/// <summary>The count of triangles a mesh of n-gons will decimate as. Faces are not triangles —
	/// a quad cage of 500 faces is 1000 triangles — and a target typed against the wrong number is
	/// off by a factor of two, so the UI asks this rather than counting faces.</summary>
	public static int TriangleCount( PolyMesh mesh )
	{
		var count = 0;

		foreach ( var f in mesh.Faces )
			count += Math.Max( 0, f.Count - 2 );

		return count;
	}

	/// <summary>Reduce to a triangle count. The short form of <see cref="Run"/> for callers with
	/// nothing to protect and a number in mind.</summary>
	public static PolyMesh ToTriangles( PolyMesh mesh, int targetTriangles ) =>
		Run( mesh, new Options { TargetTriangles = targetTriangles } ).Mesh;

	/// <summary>Reduce to a fraction of what came in.</summary>
	public static PolyMesh ToRatio( PolyMesh mesh, float ratio ) =>
		Run( mesh, new Options { Ratio = ratio } ).Mesh;

	public static Result Run( PolyMesh mesh, Options options = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		options ??= new Options();

		var work = new Work( mesh, options );
		var from = work.LiveTriangles;
		var target = Target( from, options );

		var reached = work.Collapse( target );

		return new Result( work.Build(), from, work.LiveTriangles, work.Welded, work.WorstError, reached );
	}

	/// <summary>
	/// The triangle count to stop at. Never below 4 — three triangles cannot enclose anything, and
	/// a target of zero asked for literally nothing rather than for "as small as possible".
	/// </summary>
	static int Target( int from, Options options )
	{
		var target = options.TargetTriangles > 0
			? options.TargetTriangles
			: (int)MathF.Round( from * Math.Clamp( options.Ratio, 0f, 1f ) );

		return Math.Clamp( target, 4, from );
	}

	// --- the quadric --------------------------------------------------------------------------

	/// <summary>
	/// A symmetric 4x4 in its ten distinct entries, in doubles.
	///
	/// DOUBLES ON PURPOSE. The entries are sums of products of coordinates, so on a model a
	/// thousand units across they are already around 10^6 before anything is added, and a vertex at
	/// the end of a long collapse chain has summed a few hundred of them. In floats the error term
	/// — which is a difference of large numbers that ought to cancel — comes out as noise, and the
	/// symptom is a decimation that mysteriously prefers to eat the middle of flat regions.
	/// </summary>
	struct Quadric
	{
		public double A00, A01, A02, A03, A11, A12, A13, A22, A23, A33;

		/// <summary>The fundamental error quadric of a plane, weighted. Outer product of
		/// (a,b,c,d) with itself, which measures squared distance to that plane.</summary>
		public static Quadric Plane( double a, double b, double c, double d, double weight )
		{
			return new Quadric
			{
				A00 = a * a * weight,
				A01 = a * b * weight,
				A02 = a * c * weight,
				A03 = a * d * weight,
				A11 = b * b * weight,
				A12 = b * c * weight,
				A13 = b * d * weight,
				A22 = c * c * weight,
				A23 = c * d * weight,
				A33 = d * d * weight,
			};
		}

		public void Add( in Quadric q )
		{
			A00 += q.A00; A01 += q.A01; A02 += q.A02; A03 += q.A03;
			A11 += q.A11; A12 += q.A12; A13 += q.A13;
			A22 += q.A22; A23 += q.A23;
			A33 += q.A33;
		}

		/// <summary>v^T Q v — the squared distance to all the planes this quadric remembers.
		/// Clamped at zero: it is a sum of squares and can only go negative through rounding.</summary>
		public readonly double Error( double x, double y, double z )
		{
			var e = A00 * x * x + 2 * A01 * x * y + 2 * A02 * x * z + 2 * A03 * x
				+ A11 * y * y + 2 * A12 * y * z + 2 * A13 * y
				+ A22 * z * z + 2 * A23 * z
				+ A33;

			return e < 0 ? 0 : e;
		}

		/// <summary>
		/// The point where the error is smallest, by solving the 3x3 gradient system. False when the
		/// matrix is singular, which is the common case rather than the exotic one: it happens
		/// wherever the planes do not pin the vertex in all three directions at once — anywhere flat,
		/// and along any straight crease. The caller falls back to the endpoints there, which is
		/// correct, because on a flat region every point on the edge is equally good.
		/// </summary>
		public readonly bool Solve( out double x, out double y, out double z )
		{
			// Cofactors of the upper-left 3x3.
			var c00 = A11 * A22 - A12 * A12;
			var c01 = A02 * A12 - A01 * A22;
			var c02 = A01 * A12 - A02 * A11;

			var det = A00 * c00 + A01 * c01 + A02 * c02;

			// Scale-relative, because the entries grow with the square of the model's size and a
			// fixed epsilon would call a large model singular and a small one well conditioned.
			var scale = Math.Abs( A00 ) + Math.Abs( A11 ) + Math.Abs( A22 ) + 1e-30;

			if ( Math.Abs( det ) < 1e-12 * scale * scale * scale )
			{
				x = y = z = 0;
				return false;
			}

			var c11 = A00 * A22 - A02 * A02;
			var c12 = A01 * A02 - A00 * A12;
			var c22 = A00 * A11 - A01 * A01;

			var inv = 1.0 / det;

			// v = -A^-1 b, with b the (A03, A13, A23) column.
			x = -inv * (c00 * A03 + c01 * A13 + c02 * A23);
			y = -inv * (c01 * A03 + c11 * A13 + c12 * A23);
			z = -inv * (c02 * A03 + c12 * A13 + c22 * A23);

			return true;
		}
	}

	// --- the heap -----------------------------------------------------------------------------

	readonly struct Candidate
	{
		public readonly double Cost;
		public readonly int A, B;

		// The version of each endpoint when this cost was computed. A vertex bumps its version the
		// moment it absorbs another, so an entry whose stored versions are behind is stale — its
		// cost is no longer offered — and is dropped on pop rather than re-priced, because the fresh
		// price has already been pushed by the collapse that bumped the version. This is the other
		// half of the lazy-deletion trade: without it every stale pop is recomputed and re-pushed,
		// which was growing the heap to several times the number of live edges.
		public readonly int VersionA, VersionB;

		public Candidate( double cost, int a, int b, int versionA, int versionB )
		{
			Cost = cost;
			A = a;
			B = b;
			VersionA = versionA;
			VersionB = versionB;
		}
	}

	/// <summary>
	/// A 4-ary min-heap over candidate edges.
	///
	/// Rolled here rather than taken from <c>PriorityQueue</c> so the kernel keeps compiling as
	/// loose .cs files in whatever runtime it is pasted into — see the note in Effigy.Tests.csproj
	/// about the kernel having no dependencies.
	///
	/// FOUR-ARY, NOT BINARY, because this heap is the single hottest object in a decimation: a dense
	/// import pushes and pops it tens of millions of times. A d-ary heap trades comparisons against
	/// levels — four children halves the depth at the cost of two more comparisons a level — and the
	/// expensive half of a sift is the swap, which moves the whole candidate. Halving the swaps is
	/// worth the extra compares.
	///
	/// STALE ENTRIES ARE LEFT IN IT. A collapse changes the cost of every edge around the surviving
	/// vertex, and rewriting those in place would need a handle per edge and a decrease-key. The
	/// fresh price is simply pushed; the stale one is recognised on pop by its version stamp and
	/// dropped (see Candidate.VersionA), so the heap grows by a bounded multiple rather than needing
	/// a book-keeping structure of its own.
	/// </summary>
	sealed class Heap
	{
		readonly List<Candidate> _items;

		public Heap( int capacity ) => _items = new List<Candidate>( capacity );

		public int Count => _items.Count;

		public void Push( Candidate c )
		{
			_items.Add( c );

			var i = _items.Count - 1;

			while ( i > 0 )
			{
				var parent = (i - 1) >> 2;

				if ( _items[parent].Cost <= _items[i].Cost )
					break;

				Swap( parent, i );
				i = parent;
			}
		}

		public Candidate Pop()
		{
			var top = _items[0];
			var last = _items.Count - 1;

			_items[0] = _items[last];
			_items.RemoveAt( last );

			var i = 0;

			while ( true )
			{
				var first = (i << 2) + 1;

				if ( first >= _items.Count )
					break;

				var end = first + 4 < _items.Count ? first + 4 : _items.Count;
				var smallest = i;

				for ( var c = first; c < end; c++ )
				{
					if ( _items[c].Cost < _items[smallest].Cost )
						smallest = c;
				}

				if ( smallest == i )
					break;

				Swap( smallest, i );
				i = smallest;
			}

			return top;
		}

		void Swap( int a, int b )
		{
			var t = _items[a];
			_items[a] = _items[b];
			_items[b] = t;
		}
	}

	// --- the working mesh ---------------------------------------------------------------------

	/// <summary>
	/// The mesh flattened into the form the collapse loop can edit: triangles in a flat array,
	/// vertices with adjacency, and nothing that has to stay consistent with a PolyMesh in between.
	///
	/// PolyMesh derives adjacency on demand and rebuilds it every time, which is exactly right for
	/// the operations it was written for and exactly wrong for this one — half a million collapses
	/// each needing the ring around two vertices. So the mesh is unpacked once, chewed, and packed
	/// back up. See PolyMesh's own note on why it is not a half-edge structure; this is the profile
	/// that note said would be the moment to reconsider, and the answer was to keep the working
	/// copy local to the one algorithm that needs it rather than to change the mesh everything else
	/// depends on.
	/// </summary>
	sealed class Work
	{
		readonly Options _options;

		// Triangles, three vertex indices each. Dead triangles keep their slot and are skipped;
		// compacting mid-run would invalidate every index in every adjacency list.
		readonly List<int> _tri = new();
		readonly List<int> _triMaterial = new();
		readonly List<bool> _triAlive = new();

		// Per corner, the UV of the source face's corner. Parallel to _tri, so corner 3*t+i.
		readonly List<Vec2> _triUV = new();

		readonly List<Vec3> _pos = new();
		readonly List<bool> _alive = new();
		readonly List<Quadric> _quadric = new();
		readonly List<List<int>> _vertTris = new();

		/// <summary>A vertex on an open border or a material seam. Its position is held: see
		/// <see cref="Options.PreserveBoundary"/>.</summary>
		readonly List<bool> _locked = new();

		Vec4[] _colors;
		SkinWeights _skin;
		PaintCanvas _paint;

		public int LiveTriangles;
		public int Welded;
		public float WorstError;

		// Allocation-free "set" membership over vertices: _mark[v] records the stamp of the most
		// recent pass that touched v, and _stamp is bumped per logical set. The collapse loop asks
		// "is v in this link / this opposite set / already seen this round" several million times,
		// and the HashSet version of the same questions was the dominant cost on a dense import —
		// tens of millions of small allocations for the GC to chew through.
		readonly int[] _mark;
		int _stamp;

		// Bumped whenever a vertex absorbs another, so a heap candidate can tell "my price is still
		// the one on offer" from "the surviving vertex moved on". See Candidate.VersionA.
		readonly int[] _version;

		// The (at most two) vertices opposite a shared edge, collected during the link condition.
		readonly List<int> _opposite = new();

		public Work( PolyMesh mesh, Options options )
		{
			_options = options;

			Unpack( mesh );
			BuildQuadrics();

			_mark = new int[_pos.Count];
			_version = new int[_pos.Count];
		}

		int NextStamp()
		{
			_stamp++;

			// Practically unreachable — one run bumps this a handful of times per collapse — but a
			// wrapped stamp would silently collide with a still-marked vertex and fail the link
			// condition. Reset the whole board rather than let that happen.
			if ( _stamp == int.MaxValue )
			{
				Array.Clear( _mark, 0, _mark.Length );
				_stamp = 1;
			}

			return _stamp;
		}

		// --- unpacking ------------------------------------------------------------------------

		void Unpack( PolyMesh mesh )
		{
			var remap = Weld( mesh );

			_colors = mesh.HasVertexColors ? new Vec4[_pos.Count] : null;
			_skin = mesh.IsRigged ? new SkinWeights( _pos.Count ) : null;
			_paint = mesh.Paint;

			// The first source vertex to land on a welded position is the one whose colour and
			// weights the merged vertex takes. Averaging them would be defensible and is not
			// obviously better: coincident vertices in a real file are a seam, and the two sides of
			// a seam usually agree about everything except the UV, which is per corner and survives.
			var claimed = new bool[_pos.Count];

			for ( var i = 0; i < mesh.Positions.Count; i++ )
			{
				var to = remap[i];

				if ( claimed[to] )
					continue;

				claimed[to] = true;

				if ( _colors is not null )
					_colors[to] = mesh.VertexColors[i];

				if ( _skin is not null )
					_skin[to] = mesh.Skin[i];
			}

			for ( var i = 0; i < _pos.Count; i++ )
			{
				_alive.Add( true );
				_locked.Add( false );
				_quadric.Add( default );
				_vertTris.Add( new List<int>() );
			}

			foreach ( var face in mesh.Faces )
			{
				if ( face.Count < 3 )
					continue;

				var corners = new Vec3[face.Count];

				for ( var i = 0; i < face.Count; i++ )
					corners[i] = mesh.Positions[face.Indices[i]];

				foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
				{
					var a = remap[face.Indices[ia]];
					var b = remap[face.Indices[ib]];
					var c = remap[face.Indices[ic]];

					// Welding can make a sliver face degenerate. It contributed no area before
					// either; carrying it would only give the link condition something to trip on.
					if ( a == b || b == c || a == c )
						continue;

					AddTriangle( a, b, c, face.Material, face.UVs[ia], face.UVs[ib], face.UVs[ic] );
				}
			}
		}

		/// <summary>
		/// Source vertex index to working vertex index, filling <see cref="_pos"/> as it goes.
		/// Identity when welding is off.
		/// </summary>
		int[] Weld( PolyMesh mesh )
		{
			var remap = new int[mesh.Positions.Count];

			if ( !_options.Weld )
			{
				for ( var i = 0; i < remap.Length; i++ )
				{
					remap[i] = i;
					_pos.Add( mesh.Positions[i] );
				}

				return remap;
			}

			// Quantised to a grid rather than compared pairwise — an O(n^2) weld on 450k vertices
			// is not a slow weld, it is a weld that never finishes. The grid means two points
			// either side of a cell boundary are not welded, which is the standard and acceptable
			// failure: the tolerance is a hundred-thousandth of the model and the vertices this is
			// aimed at are bit-identical duplicates, not near misses.
			var tolerance = MathF.Max( mesh.BoundsDiagonal * _options.WeldTolerance, 1e-9f );
			var inverse = 1f / tolerance;
			var seen = new Dictionary<(long, long, long), int>( mesh.Positions.Count );

			for ( var i = 0; i < mesh.Positions.Count; i++ )
			{
				var p = mesh.Positions[i];
				var key = (
					(long)MathF.Round( p.x * inverse ),
					(long)MathF.Round( p.y * inverse ),
					(long)MathF.Round( p.z * inverse ));

				if ( seen.TryGetValue( key, out var existing ) )
				{
					remap[i] = existing;
					Welded++;
					continue;
				}

				remap[i] = _pos.Count;
				seen[key] = _pos.Count;
				_pos.Add( p );
			}

			return remap;
		}

		void AddTriangle( int a, int b, int c, int material, Vec2 ua, Vec2 ub, Vec2 uc )
		{
			var t = _triAlive.Count;

			_tri.Add( a ); _tri.Add( b ); _tri.Add( c );
			_triUV.Add( ua ); _triUV.Add( ub ); _triUV.Add( uc );
			_triMaterial.Add( material );
			_triAlive.Add( true );

			_vertTris[a].Add( t );
			_vertTris[b].Add( t );
			_vertTris[c].Add( t );

			LiveTriangles++;
		}

		// --- quadrics -------------------------------------------------------------------------

		void BuildQuadrics()
		{
			var quadrics = new Quadric[_pos.Count];

			for ( var t = 0; t < _triAlive.Count; t++ )
			{
				var a = _tri[3 * t];
				var b = _tri[3 * t + 1];
				var c = _tri[3 * t + 2];

				var normal = Vec3.Cross( _pos[b] - _pos[a], _pos[c] - _pos[a] );
				var length = normal.Length;

				if ( length < 1e-20f )
					continue;

				var n = normal / length;
				var d = -Vec3.Dot( n, _pos[a] );

				// Area weighting — half the cross product's length — so a big flat face counts for
				// more than a sliver, exactly as it does in ComputeVertexNormals and for the same
				// reason: a fan of slivers around a vertex should not outvote the surface it sits on.
				var q = Quadric.Plane( n.x, n.y, n.z, d, length * 0.5f );

				quadrics[a].Add( q );
				quadrics[b].Add( q );
				quadrics[c].Add( q );
			}

			if ( _options.PreserveBoundary )
				AddConstraintPlanes( quadrics );

			for ( var i = 0; i < quadrics.Length; i++ )
				_quadric[i] = quadrics[i];
		}

		/// <summary>
		/// A wall standing on every border, perpendicular to the surface.
		///
		/// A boundary vertex is held by the triangles on ONE side of it and by nothing on the other,
		/// so its quadric says the cheapest thing it can do is slide off the edge — and it does,
		/// which is how a decimated shell ends up with a ragged opening and a decimated two-material
		/// part ends up with the seam wandering. The fix is Garland and Heckbert's own: for each
		/// border edge, add the plane containing that edge and perpendicular to its triangle,
		/// weighted heavily. Moving along the border stays free; leaving it becomes expensive.
		///
		/// A MATERIAL CHANGE IS A BORDER for this purpose, and so is a non-manifold edge. The first
		/// because the line between two materials is a thing somebody chose and the mesh is the only
		/// place it is recorded; the second because there is no sensible collapse across it and it is
		/// cheaper to freeze it than to reason about it.
		/// </summary>
		void AddConstraintPlanes( Quadric[] quadrics )
		{
			var edges = new Dictionary<EdgeKey, (int Count, int Triangle, int Material, bool Mixed)>();

			for ( var t = 0; t < _triAlive.Count; t++ )
			{
				for ( var i = 0; i < 3; i++ )
				{
					var key = new EdgeKey( _tri[3 * t + i], _tri[3 * t + (i + 1) % 3] );

					if ( edges.TryGetValue( key, out var e ) )
					{
						edges[key] = (e.Count + 1, e.Triangle, e.Material,
							e.Mixed || e.Material != _triMaterial[t]);
					}
					else
					{
						edges[key] = (1, t, _triMaterial[t], false);
					}
				}
			}

			foreach ( var (key, e) in edges )
			{
				if ( e.Count == 2 && !e.Mixed )
					continue;

				var a = _pos[key.A];
				var b = _pos[key.B];
				var along = b - a;

				if ( along.LengthSquared < 1e-20f )
					continue;

				var t = e.Triangle;
				var face = Vec3.Cross(
					_pos[_tri[3 * t + 1]] - _pos[_tri[3 * t]],
					_pos[_tri[3 * t + 2]] - _pos[_tri[3 * t]] );

				if ( face.LengthSquared < 1e-20f )
					continue;

				// Perpendicular to the surface AND containing the edge: cross the edge direction
				// with the face normal.
				var n = Vec3.Cross( along, face ).Normal;
				var d = -Vec3.Dot( n, a );

				// Heavy enough that the border wins any argument with the surface, and finite so a
				// border vertex can still slide ALONG the border, which is the whole point.
				var weight = along.Length * 1000f;
				var wall = Quadric.Plane( n.x, n.y, n.z, d, weight );

				quadrics[key.A].Add( wall );
				quadrics[key.B].Add( wall );

				_locked[key.A] = true;
				_locked[key.B] = true;
			}
		}

		// --- the collapse loop ------------------------------------------------------------------

		/// <summary>Runs until the target is met or nothing legal is left. True if it got there.</summary>
		public bool Collapse( int target )
		{
			// Upper bound on the number of edges in a closed triangle mesh: three corners a triangle
			// shared between two — so 1.5x the triangle count. Pre-sizing keeps the heap's backing
			// array from doubling and copying itself several times over a long run.
			var heap = new Heap( LiveTriangles + LiveTriangles / 2 );

			foreach ( var key in LiveEdges() )
			{
				if ( TryCost( key.A, key.B, out var cost, out _ ) )
					heap.Push( new Candidate( cost, key.A, key.B, _version[key.A], _version[key.B] ) );
			}

			while ( LiveTriangles > target && heap.Count > 0 )
			{
				var top = heap.Pop();

				if ( !_alive[top.A] || !_alive[top.B] )
					continue;

				// A stale entry: one of its endpoints has absorbed a vertex since this price was
				// taken. Its fresh price is already in the heap — the collapse that bumped the
				// version re-priced every edge touching the survivor — so the stale one is dropped
				// rather than recomputed and re-pushed.
				if ( _version[top.A] != top.VersionA || _version[top.B] != top.VersionB )
					continue;

				if ( !TryCost( top.A, top.B, out var cost, out var to ) )
					continue;

				if ( cost > _options.MaxError )
					break;

				if ( !Apply( top.A, top.B, to ) )
					continue;

				if ( cost > WorstError )
					WorstError = (float)cost;

				// Every edge now touching the surviving vertex has a fresh cost. Pushed without an
				// allocation: a stamp marks which neighbours have already been pushed this round.
				var stamp = NextStamp();
				var versionA = _version[top.A];

				foreach ( var t in _vertTris[top.A] )
				{
					if ( !_triAlive[t] )
						continue;

					for ( var i = 0; i < 3; i++ )
					{
						var n = _tri[3 * t + i];

						if ( n == top.A || _mark[n] == stamp )
							continue;

						_mark[n] = stamp;

						if ( TryCost( top.A, n, out var updated, out _ ) )
							heap.Push( new Candidate( updated, top.A, n, versionA, _version[n] ) );
					}
				}
			}

			return LiveTriangles <= target;
		}

		IEnumerable<EdgeKey> LiveEdges()
		{
			var seen = new HashSet<EdgeKey>();

			for ( var t = 0; t < _triAlive.Count; t++ )
			{
				if ( !_triAlive[t] )
					continue;

				for ( var i = 0; i < 3; i++ )
				{
					var key = new EdgeKey( _tri[3 * t + i], _tri[3 * t + (i + 1) % 3] );

					if ( seen.Add( key ) )
						yield return key;
				}
			}
		}

		/// <summary>
		/// Where the merged vertex would go and what it would cost, or false if this edge must not
		/// collapse at all.
		///
		/// The position is not always the quadric's minimum. A vertex on a border is pinned to one
		/// of the two endpoints, because the optimum of a sum of quadrics can sit anywhere and
		/// "anywhere" is how an opening drifts. Interior edges get the true minimum, and fall back
		/// to the better endpoint or the midpoint where the system is singular — which, on anything
		/// flat, it always is.
		/// </summary>
		bool TryCost( int a, int b, out double cost, out Vec3 to )
		{
			cost = 0;
			to = Vec3.Zero;

			if ( a == b || !_alive[a] || !_alive[b] )
				return false;

			var lockedA = _locked[a];
			var lockedB = _locked[b];

			// Both ends on a border, but the edge between them is not one: collapsing it pulls two
			// separate stretches of border together through the middle of the surface. Refused
			// outright rather than priced, because there is no position that makes it acceptable.
			if ( lockedA && lockedB && !IsConstrainedEdge( a, b ) )
				return false;

			var q = _quadric[a];
			q.Add( _quadric[b] );

			if ( lockedA != lockedB )
			{
				// One end is held. The merged vertex goes there; the free one comes to it.
				to = lockedA ? _pos[a] : _pos[b];
			}
			else if ( !q.Solve( out var x, out var y, out var z ) )
			{
				to = Cheapest( q, _pos[a], _pos[b], Vec3.Lerp( _pos[a], _pos[b], 0.5f ) );
			}
			else
			{
				to = new Vec3( (float)x, (float)y, (float)z );
			}

			cost = q.Error( to.x, to.y, to.z );
			return true;
		}

		static Vec3 Cheapest( in Quadric q, Vec3 a, Vec3 b, Vec3 mid )
		{
			var ea = q.Error( a.x, a.y, a.z );
			var eb = q.Error( b.x, b.y, b.z );
			var em = q.Error( mid.x, mid.y, mid.z );

			if ( ea <= eb && ea <= em ) return a;
			return eb <= em ? b : mid;
		}

		/// <summary>Whether the edge itself is a border — one live triangle, or two with different
		/// materials. Asked only of edges whose ends are both locked, which is rare.</summary>
		bool IsConstrainedEdge( int a, int b )
		{
			var count = 0;
			var material = -1;

			foreach ( var t in _vertTris[a] )
			{
				if ( !_triAlive[t] || !Uses( t, b ) )
					continue;

				count++;

				if ( material >= 0 && material != _triMaterial[t] )
					return true;

				material = _triMaterial[t];
			}

			return count != 2;
		}

		bool Uses( int t, int v ) =>
			_tri[3 * t] == v || _tri[3 * t + 1] == v || _tri[3 * t + 2] == v;

		/// <summary>
		/// The link condition: an edge is safe to collapse when the only vertices adjacent to BOTH
		/// its endpoints are the ones opposite it in the triangles that share it.
		///
		/// This is the whole of topological safety in one test, and it is not optional. Without it a
		/// collapse can weld two sheets of the mesh that merely passed close to each other, or pinch
		/// a tube into a figure of eight — a mesh that still has the right triangle count, still
		/// renders, and is non-manifold, so the exporter, the boolean and the physics hull all fail
		/// on it later with nothing pointing back here. Dey, Edelsbrunner, Guha and Nekhayev proved
		/// it necessary and sufficient for a simplicial complex; the cost is one small set
		/// intersection per candidate.
		///
		/// The sets here are stamp arrays, not HashSets: this runs once per collapse and the HashSet
		/// version of it — three allocations a call, tens of millions over a dense import — was the
		/// single largest cost in the whole decimation.
		/// </summary>
		bool LinkConditionHolds( int a, int b )
		{
			// The vertices opposite the edge in the triangles that share it — at most two on a
			// manifold, so a small reused list rather than a set.
			_opposite.Clear();

			foreach ( var t in _vertTris[a] )
			{
				if ( !_triAlive[t] || !Uses( t, b ) )
					continue;

				for ( var i = 0; i < 3; i++ )
				{
					var v = _tri[3 * t + i];

					if ( v != a && v != b )
						_opposite.Add( v );
				}
			}

			// The link of a — every neighbour, opposite or not.
			var linkA = NextStamp();

			foreach ( var t in _vertTris[a] )
			{
				if ( !_triAlive[t] )
					continue;

				for ( var i = 0; i < 3; i++ )
				{
					var v = _tri[3 * t + i];

					if ( v != a )
						_mark[v] = linkA;
				}
			}

			// A neighbour of b that is in a's link but not opposite the edge closes a triangle
			// around the collapse, and the link condition fails.
			foreach ( var t in _vertTris[b] )
			{
				if ( !_triAlive[t] )
					continue;

				for ( var i = 0; i < 3; i++ )
				{
					var v = _tri[3 * t + i];

					if ( v == a || v == b || _mark[v] != linkA )
						continue;

					if ( !_opposite.Contains( v ) )
						return false;
				}
			}

			return true;
		}

		/// <summary>Would any triangle that survives this collapse be turned inside out by it?</summary>
		bool WouldFlip( int a, int b, Vec3 to ) =>
			Flips( a, b, to ) || Flips( b, a, to );

		bool Flips( int v, int other, Vec3 to )
		{
			foreach ( var t in _vertTris[v] )
			{
				if ( !_triAlive[t] )
					continue;

				// A triangle using both ends is about to disappear, so its normal is nobody's
				// business.
				if ( Uses( t, v ) && Uses( t, other ) )
					continue;

				var p0 = _tri[3 * t] == v ? to : _pos[_tri[3 * t]];
				var p1 = _tri[3 * t + 1] == v ? to : _pos[_tri[3 * t + 1]];
				var p2 = _tri[3 * t + 2] == v ? to : _pos[_tri[3 * t + 2]];

				var before = Vec3.Cross(
					_pos[_tri[3 * t + 1]] - _pos[_tri[3 * t]],
					_pos[_tri[3 * t + 2]] - _pos[_tri[3 * t]] );

				var after = Vec3.Cross( p1 - p0, p2 - p0 );

				// Collapsed to nothing. Not a flip, but not a triangle either, and letting it
				// through leaves a zero-area face for the next pass to divide by.
				if ( after.LengthSquared < 1e-20f )
					return true;

				if ( before.LengthSquared < 1e-20f )
					continue;

				if ( Vec3.Dot( before.Normal, after.Normal ) < _options.FlipThreshold )
					return true;
			}

			return false;
		}

		/// <summary>
		/// Would this collapse remove surface rather than simplify it?
		///
		/// A collapse is supposed to be a trade: two triangles die and the rest of the ring closes
		/// up around the merged vertex. Where there is no rest of the ring, the same operation is
		/// just a delete — the triangles vanish and leave a hole, and the triangle count goes down,
		/// so nothing upstream notices that the answer is a mesh with a bite out of it.
		///
		/// FOUND BY THE UNWELDED CASE. A triangle soup is nothing but isolated triangles, and every
		/// one of them satisfies the link condition perfectly: collapse any edge and the third
		/// vertex is the only shared neighbour, exactly as the condition requires. It is a valid
		/// collapse of a valid complex, and it eats the model one triangle at a time. Welding is the
		/// real fix and is on by default; this is what makes turning it off honest rather than
		/// destructive.
		/// </summary>
		bool WouldDelete( int a, int b ) =>
			Deletes( a, b ) && Deletes( b, a );

		bool Deletes( int v, int other )
		{
			foreach ( var t in _vertTris[v] )
			{
				if ( _triAlive[t] && !(Uses( t, v ) && Uses( t, other )) )
					return false;
			}

			return true;
		}

		/// <summary>Do it, if the checks allow. B is merged into A, which keeps A's slot and
		/// therefore A's adjacency list — the cheaper of the two directions.</summary>
		bool Apply( int a, int b, Vec3 to )
		{
			if ( WouldDelete( a, b ) || !LinkConditionHolds( a, b ) || WouldFlip( a, b, to ) )
				return false;

			var from = _pos[a];
			var t = Along( from, _pos[b], to );

			// Kill the triangles that used the whole edge — they have collapsed to a line.
			foreach ( var tri in _vertTris[a] )
			{
				if ( !_triAlive[tri] || !Uses( tri, b ) )
					continue;

				_triAlive[tri] = false;
				LiveTriangles--;
			}

			// Everything else that touched B now touches A.
			foreach ( var tri in _vertTris[b] )
			{
				if ( !_triAlive[tri] )
					continue;

				for ( var i = 0; i < 3; i++ )
				{
					if ( _tri[3 * tri + i] == b )
						_tri[3 * tri + i] = a;
				}

				_vertTris[a].Add( tri );
			}

			_pos[a] = to;
			_alive[b] = false;
			_locked[a] = _locked[a] || _locked[b];
			_version[a]++;

			var q = _quadric[a];
			q.Add( _quadric[b] );
			_quadric[a] = q;

			if ( _colors is not null )
				_colors[a] = Vec4.Lerp( _colors[a], _colors[b], t );

			if ( _skin is not null )
			{
				_skin[a] = SkinWeights.Blend( new[]
				{
					(_skin[a], 1f - t),
					(_skin[b], t),
				} );
			}

			_vertTris[b] = null;

			// The dead entries left behind by the loop above would otherwise accumulate until a
			// vertex that has absorbed a thousand neighbours carries a thousand dead triangles and
			// every neighbourhood walk over it costs a thousand steps. Compacted when the list is
			// mostly rubbish rather than every time, so the amortised cost stays flat.
			//
			// The count pass and the write pass are separate on purpose: the write compacts in place,
			// so running it and then NOT trimming (because the list was less than half dead) leaves
			// the live entries duplicated at the front of a list that kept its old tail. That was
			// inflating the survivor's list by up to 2x and making every later walk over it pay for
			// the phantom triangles.
			var live = _vertTris[a];

			if ( live.Count > 16 )
			{
				var kept = 0;

				for ( var i = 0; i < live.Count; i++ )
				{
					if ( _triAlive[live[i]] )
						kept++;
				}

				if ( kept * 2 < live.Count )
				{
					var w = 0;

					for ( var i = 0; i < live.Count; i++ )
					{
						if ( _triAlive[live[i]] )
							live[w++] = live[i];
					}

					live.RemoveRange( w, live.Count - w );
				}
			}

			return true;
		}

		/// <summary>Where the merged position sits along the original edge, 0 at A and 1 at B.
		/// The blend factor for everything carried per vertex.</summary>
		static float Along( Vec3 a, Vec3 b, Vec3 to )
		{
			var edge = b - a;
			var lengthSquared = edge.LengthSquared;

			if ( lengthSquared < 1e-20f )
				return 0.5f;

			return Math.Clamp( Vec3.Dot( to - a, edge ) / lengthSquared, 0f, 1f );
		}

		// --- packing back up --------------------------------------------------------------------

		public PolyMesh Build()
		{
			var mesh = new PolyMesh();
			var remap = new int[_pos.Count];

			for ( var i = 0; i < remap.Length; i++ )
				remap[i] = -1;

			var colors = _colors is null ? null : new List<Vec4>();
			var skin = _skin is null ? null : new SkinWeights();

			for ( var t = 0; t < _triAlive.Count; t++ )
			{
				if ( !_triAlive[t] )
					continue;

				var indices = new int[3];
				var uvs = new Vec2[3];

				for ( var i = 0; i < 3; i++ )
				{
					var v = _tri[3 * t + i];

					if ( remap[v] < 0 )
					{
						remap[v] = mesh.AddVertex( _pos[v] );
						colors?.Add( _colors[v] );
						skin?.Vertices.Add( _skin[v] );
					}

					indices[i] = remap[v];
					uvs[i] = _triUV[3 * t + i];
				}

				mesh.AddFace( indices, uvs, _triMaterial[t] );
			}

			if ( colors is not null )
				mesh.VertexColors = colors.ToArray();

			mesh.Skin = skin;
			mesh.Paint = _paint;

			return mesh;
		}
	}
}
