using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Topology-preserving garment fitting. Source and target use the same coordinate space.</summary>
public static class MeshShrinkwrap
{
	/// <summary>Returns an independent mesh with selected positions fitted to the target.
	/// Weights are 0..1 (null selects everything). Misses remain unchanged. A null direction
	/// selects nearest-surface fitting; otherwise projects along the specified directions.
	/// Offset follows target winding, not a collision guarantee for faces between vertices.</summary>
	public static PolyMesh Apply( PolyMesh source, PolyMesh target, float maxDistance, float offset,
		out int hits, float[] weights = null, Vec3? direction = null,
		bool positive = true, bool negative = false )
	{
		if ( source is null || target is null ) throw new ArgumentNullException();
		if ( !float.IsFinite( maxDistance ) || maxDistance < 0 || !float.IsFinite( offset ) )
			throw new ArgumentException( "Supply a finite nonnegative distance and finite offset" );
		if ( weights is not null )
		{
			if ( weights.Length != source.VertexCount ) throw new ArgumentException( "One weight per source vertex is required" );
			foreach ( var weight in weights )
				if ( !float.IsFinite( weight ) || weight < 0 || weight > 1 )
					throw new ArgumentException( "Weights must be finite and between zero and one" );
		}
		var result = source.Clone();
		var tree = MeshBVH.Build( target );
		hits = 0;
		for ( var i = 0; i < source.VertexCount; i++ )
		{
			var weight = weights?[i] ?? 1f;
			if ( weight == 0 ) continue;
			var point = source.Positions[i];
			Vec3 fitted;
			if ( direction is { } axis )
			{
				if ( !tree.TryProjectSurface( target, point, axis, maxDistance, offset,
					out fitted, positive, negative ) ) continue;
			}
			else
			{
				if ( tree.NearestSurface( target, point, maxDistance ) is not { } hit ) continue;
				fitted = hit.Point + hit.Normal * offset;
			}
			result.Positions[i] = point + (fitted - point) * weight;
			hits++;
		}
		return result;
	}
}

/// <summary>What a reprojection managed, so the caller can say so rather than guess.</summary>
public sealed class ReprojectionReport
{
	public readonly int Vertices;
	public readonly int Hit;
	public readonly float MaxDistance;

	public ReprojectionReport( int vertices, int hit, float maxDistance )
	{
		Vertices = vertices;
		Hit = hit;
		MaxDistance = maxDistance;
	}

	/// <summary>How much of the new surface found the old one. Below about a half means the two
	/// shapes have little to do with each other and the result is not worth keeping.</summary>
	public float Coverage => Vertices == 0 ? 0f : (float)Hit / Vertices;

	public override string ToString() =>
		$"{Hit} of {Vertices} vertices found the old surface ({Coverage:P0}), searching {MaxDistance:0.###} either side";
}

/// <summary>
/// Moving a sculpt onto a cage it was not made on.
///
/// THE LAST RESORT, AND IT IS MEANT TO BE. Deltas are per vertex, so a cage whose topology changed
/// has no vertex to put them on and <see cref="MultiresSculpt.SetCage"/> refuses. That refusal is
/// right nearly always: the usual cause is an upstream edit the user did not mean, and undoing it
/// brings the sculpt back exactly. This is for the other case — the edit WAS meant, the old sculpt
/// is worth more than nothing, and the honest offer is an approximation clearly labelled as one.
///
/// The method is the plan's: build the new cage's levels empty, then for each vertex of the top
/// level fire a ray along its own normal and move it onto the old sculpted surface. What comes back
/// is the old shape resampled at the new cage's density.
///
/// WHAT IS LOST, stated up front so it is not discovered later:
///
/// - Detail finer than the new cage can carry. Resampling cannot invent vertices.
/// - The level structure. Everything lands in the top level as one displacement, so going back down
///   to L1 afterwards no longer shows the coarse shape with the detail riding it — there is no
///   coarse shape any more, just the top level and the cage. Sculpting at a lower level still works;
///   it just starts from a flat lower level.
/// - Anything the rays missed. A new cage that reaches somewhere the old surface never was leaves
///   those vertices where they are, which is the cage's own shape and the right answer.
/// </summary>
public static class SculptReprojection
{
	/// <summary>
	/// Resample <paramref name="old"/>'s sculpted surface onto <paramref name="newCage"/>.
	///
	/// <paramref name="levels"/> defaults to the old sculpt's, which keeps the cost the user already
	/// chose. <paramref name="maxDistance"/> of zero derives a search range from the cage's size.
	/// </summary>
	public static MultiresSculpt Reproject( MultiresSculpt old, PolyMesh newCage, out ReprojectionReport report,
		int levels = -1, float maxDistance = 0f )
	{
		if ( old is null )
			throw new ArgumentNullException( nameof( old ) );

		if ( newCage is null )
			throw new ArgumentNullException( nameof( newCage ) );

		if ( levels < 0 )
			levels = old.TopLevel;

		if ( levels < 0 )
			throw new ArgumentOutOfRangeException( nameof( levels ) );

		var source = old.Evaluate( old.TopLevel );
		var bvh = MeshBVH.Build( source );

		var result = new MultiresSculpt( newCage );

		for ( var i = 0; i < levels; i++ )
			result.AddLevel();

		var target = result.Rest( levels );
		var normals = target.ComputeVertexNormals();
		var reach = maxDistance > 0f ? maxDistance : DefaultReach( newCage );
		var hits = 0;

		for ( var i = 0; i < target.VertexCount; i++ )
		{
			var normal = normals[i];

			if ( normal.LengthSquared < 0.5f )
				continue;

			// Fired from outside inward, like the bake, so the nearest hit is the surface facing
			// this vertex rather than whatever is behind it. A vertex that finds nothing is left
			// exactly where the new cage put it, which is the correct answer for a part of the model
			// the old sculpt never covered.
			var hit = bvh.Raycast( source, target.Positions[i] + normal * reach, -normal );

			if ( hit is null || hit.Value.Distance > reach * 2f )
				continue;

			target.Positions[i] = hit.Value.Point;
			hits++;
		}

		result.Record( levels, target );
		result.ViewLevel = levels;

		report = new ReprojectionReport( target.VertexCount, hits, reach );
		return result;
	}

	/// <summary>A tenth of the cage's diagonal, the same figure the bake uses and for the same
	/// reason: far enough for ordinary relief, near enough that a ray rarely reaches an unrelated
	/// part of the model.</summary>
	static float DefaultReach( PolyMesh cage )
	{
		var diagonal = cage.BoundsDiagonal;
		return diagonal > 1e-6f ? diagonal * 0.1f : 1f;
	}
}

/// <summary>Starting points for fabric. Artistic presets, not measured material data.</summary>
public enum Fabric
{
	Cotton,
	Denim,
	Leather,
	Stretch,
}

/// <summary>
/// Editor cloth: drape a garment under gravity over the body it is fitted to.
///
/// XPBD (extended position-based dynamics). Every edge and quad diagonal is a distance
/// constraint (stretch and shear); the two corners facing each other across every shared edge
/// are a softer one (bend). Each substep predicts positions from velocity and gravity, projects
/// the constraints, pushes anything inside the collider back out to <see cref="Thickness"/>, and
/// derives velocity from how far things actually moved. Fixed steps, a fixed order, no random
/// numbers — the same input drapes the same way every time, which is what lets a slider re-run it.
///
/// Units are the kernel's: inches, and seconds. Gravity is 386 in/s².
///
/// LIMITS, SAID OUT LOUD. Vertices collide with the body; edges and faces do not, so a coarse
/// garment over a sharp corner can let a face pass through between its vertices. There is no
/// self-collision yet — a garment folding onto itself can pass through itself. Both are the
/// next milestones, not quiet omissions.
/// </summary>
public sealed class ClothSim
{
	public float Gravity = 386f;

	/// <summary>How far the fabric is kept off the body. The simulation's thickness, not the
	/// garment's — Solidify gives the final mesh its thickness afterwards.</summary>
	public float Thickness = 0.05f;

	/// <summary>0 is rigid; larger stretches more. XPBD compliance, in inches per unit force.</summary>
	public float StretchCompliance = 1e-7f;
	public float BendCompliance = 1e-4f;

	/// <summary>Fraction of velocity kept per second. Low is heavy, still cloth.</summary>
	public float DampingPerSecond = 0.2f;

	/// <summary>How much of the tangential motion survives touching the body. 0 sticks, 1 slides.</summary>
	public float Friction = 0.6f;

	public int Substeps = 8;
	public int Iterations = 4;

	/// <summary>Stop the cloth passing through itself where it folds. Off is faster and is fine for
	/// a garment that only ever lies flat against a body.</summary>
	public bool SelfCollision = true;

	/// <summary>How far apart two bits of cloth are held. Negative takes twice
	/// <see cref="Thickness"/>, which is the two facing surfaces of a fold.</summary>
	public float SelfDistance = -1f;

	/// <summary>
	/// How far a particle may wander from where it started. 0 is no limit.
	///
	/// A LEASH, NOT A PIN. Draping is for the last inch - a hem falling, a sleeve settling - and a
	/// garment fitted over a closed body never needs more. Over a body with holes in it, a robot
	/// built from struts, nothing catches the cloth and it falls straight through to the floor.
	/// The tether is the long-range attachment every production cloth solver has for that: slack
	/// inside the leash, a wall at the end of it.
	/// </summary>
	public float Tether;

	/// <summary>Round colliders, tested before the meshes. See Capsule.</summary>
	public readonly List<Capsule> Capsules = new();

	readonly Vec3[] _start;
	readonly Vec3[] _x;
	readonly Vec3[] _v;
	readonly float[] _w;
	readonly List<(int A, int B, float Length, bool Bend)> _constraints = new();

	/// <summary>Pairs already held together by a constraint. Self-collision must leave these alone,
	/// or it spends every step fighting the stretch constraints over the length of one edge.</summary>
	readonly HashSet<EdgeKey> _linked = new();

	readonly Dictionary<(int, int, int), List<int>> _bins = new();

	/// <summary>The cloth's own triangles, for face-level self-collision, and the bins they land in.</summary>
	readonly List<(int A, int B, int C)> _triangles = new();

	readonly Dictionary<(int, int, int), List<int>> _triangleBins = new();

	readonly List<(PolyMesh Mesh, MeshBVH Tree)> _colliders = new();

	public ClothSim( PolyMesh cloth, IEnumerable<int> pinned = null )
	{
		if ( cloth is null )
			throw new ArgumentNullException( nameof( cloth ) );

		var n = cloth.VertexCount;
		_x = cloth.Positions.ToArray();
		_start = cloth.Positions.ToArray();
		_v = new Vec3[n];
		_w = new float[n];
		Array.Fill( _w, 1f );

		if ( pinned is not null )
			foreach ( var p in pinned )
				if ( p >= 0 && p < n )
					_w[p] = 0f;

		BuildConstraints( cloth );
	}

	public static ClothSim WithFabric( PolyMesh cloth, IEnumerable<int> pinned, Fabric fabric )
	{
		var sim = new ClothSim( cloth, pinned );

		switch ( fabric )
		{
			case Fabric.Denim:
				sim.BendCompliance = 1e-5f;
				sim.DampingPerSecond = 0.1f;
				sim.Friction = 0.4f;
				break;
			case Fabric.Leather:
				sim.BendCompliance = 1e-6f;
				sim.DampingPerSecond = 0.05f;
				sim.Friction = 0.3f;
				break;
			case Fabric.Stretch:
				sim.StretchCompliance = 1e-4f;
				sim.BendCompliance = 1e-3f;
				sim.Friction = 0.7f;
				break;
		}

		return sim;
	}

	/// <summary>What the cloth lies on. Null for none.</summary>
	public void SetCollider( PolyMesh body ) =>
		SetColliders( body is null ? null : new[] { body } );

	/// <summary>
	/// Every body the cloth lies on. A garment is worn over a whole character, not over one body,
	/// so the solver pushes out of each of them in turn — the deepest push wins, which is what
	/// stops a sleeve being pushed out of the arm and straight back into the chest.
	/// </summary>
	public void SetColliders( IEnumerable<PolyMesh> bodies )
	{
		_colliders.Clear();

		if ( bodies is null )
			return;

		foreach ( var body in bodies )
		{
			if ( body is null || body.FaceCount == 0 )
				continue;

			_colliders.Add( (body, MeshBVH.Build( body )) );
		}
	}

	public IReadOnlyList<Vec3> Positions => _x;

	public int ConstraintCount => _constraints.Count;

	void BuildConstraints( PolyMesh cloth )
	{
		var seen = new HashSet<EdgeKey>();

		void Add( int a, int b, bool bend )
		{
			if ( a == b || !seen.Add( new EdgeKey( a, b ) ) )
				return;

			_constraints.Add( (a, b, (cloth.Positions[a] - cloth.Positions[b]).Length, bend) );
			_linked.Add( new EdgeKey( a, b ) );
		}

		foreach ( var face in cloth.Faces )
		{
			var idx = face.Indices;

			for ( var i = 0; i < idx.Length; i++ )
				Add( idx[i], idx[(i + 1) % idx.Length], false );

			// Shear: the diagonals of a quad, or a fan of them for bigger polygons.
			if ( idx.Length >= 4 )
				for ( var i = 2; i < idx.Length - 1; i++ )
					Add( idx[0], idx[i], false );

			if ( idx.Length == 4 )
				Add( idx[1], idx[3], false );
		}

		// The cloth's own faces as triangles, so a particle can be kept off a FACE and not only off
		// another particle. Fan triangulation is enough: these are collision proxies, not geometry.
		for ( var f = 0; f < cloth.FaceCount; f++ )
		{
			var idx = cloth.Faces[f].Indices;

			for ( var i = 1; i < idx.Length - 1; i++ )
				_triangles.Add( (idx[0], idx[i], idx[i + 1]) );
		}

		// Bend: across every edge shared by two faces, the corners that face each other.
		foreach ( var (key, faces) in cloth.BuildEdgeFaces() )
		{
			if ( faces.Count != 2 )
				continue;

			var a = Opposite( cloth.Faces[faces[0]], key );
			var b = Opposite( cloth.Faces[faces[1]], key );

			if ( a >= 0 && b >= 0 )
				Add( a, b, true );
		}
	}

	/// <summary>The corner of a face furthest round from an edge of it — across the face.</summary>
	static int Opposite( Face face, EdgeKey edge )
	{
		var idx = face.Indices;
		var n = idx.Length;

		for ( var i = 0; i < n; i++ )
		{
			if ( new EdgeKey( idx[i], idx[(i + 1) % n] ).Equals( edge ) )
				return idx[(i + 1 + n / 2) % n];
		}

		return -1;
	}

	/// <summary>Advance by <paramref name="seconds"/> in fixed steps of 1/60 s.</summary>
	public void Run( float seconds )
	{
		var frames = Math.Max( 1, (int)MathF.Round( seconds * 60f ) );

		for ( var f = 0; f < frames; f++ )
			Step( 1f / 60f );
	}

	public void Step( float dt )
	{
		var h = dt / Math.Max( 1, Substeps );
		var keep = MathF.Pow( Math.Clamp( DampingPerSecond, 0f, 1f ), h );
		var gravity = new Vec3( 0f, 0f, -Gravity );
		var predicted = new Vec3[_x.Length];

		for ( var s = 0; s < Math.Max( 1, Substeps ); s++ )
		{
			for ( var i = 0; i < _x.Length; i++ )
			{
				if ( _w[i] == 0f )
				{
					predicted[i] = _x[i];
					continue;
				}

				_v[i] += gravity * h;
				predicted[i] = _x[i] + _v[i] * h;
			}

			for ( var it = 0; it < Math.Max( 1, Iterations ); it++ )
			{
				foreach ( var (a, b, rest, bend) in _constraints )
				{
					var wa = _w[a];
					var wb = _w[b];
					var wsum = wa + wb;

					if ( wsum == 0f )
						continue;

					var d = predicted[a] - predicted[b];
					var len = d.Length;

					if ( len < 1e-9f )
						continue;

					var alpha = (bend ? BendCompliance : StretchCompliance) / (h * h);
					var lambda = -(len - rest) / (wsum + alpha);
					var correction = d * (lambda / len);

					predicted[a] += correction * wa;
					predicted[b] -= correction * wb;
				}

			}

			// Once per substep, after the constraints, not once per iteration. The body query is
			// the dearest thing the solver does - four times over it was ninety percent of a
			// drape on a dense wearer - and the position that has to be off the body is the one
			// the substep ends on, not the ones the constraints pass through on the way.
			Collide( predicted );

			if ( Tether > 0f )
			{
				for ( var i = 0; i < predicted.Length; i++ )
				{
					var d = predicted[i] - _start[i];
					var len = d.Length;

					if ( len > Tether )
						predicted[i] = _start[i] + d * (Tether / len);
				}
			}

			// Once per substep, not per iteration: it is the expensive constraint, and a fold only
			// needs separating once the cheap ones have finished moving it.
			if ( SelfCollision )
				SeparateSelf( predicted );

			for ( var i = 0; i < _x.Length; i++ )
			{
				if ( _w[i] == 0f )
					continue;

				_v[i] = (predicted[i] - _x[i]) / h * keep;
				_x[i] = predicted[i];
			}
		}
	}

	/// <summary>
	/// Keep the cloth off itself. Every particle is dropped into a spatial hash whose cells are the
	/// separation distance, so the only pairs ever tested are the ones in the 27 cells around each
	/// particle — which is what makes this linear in the number of particles rather than quadratic.
	/// A pair closer than the separation is pushed apart along the line between them, split by
	/// weight so a pinned particle does not move.
	///
	/// PARTICLE TO PARTICLE, NOT FACE TO FACE. Two vertices cannot pass through each other; a fine
	/// fold can still pinch a coarse face. Separation is half the push each way per substep rather
	/// than a hard constraint, so a fold relaxes apart over a few steps instead of popping.
	/// </summary>
	void SeparateSelf( Vec3[] p )
	{
		var d = SelfDistance > 0f ? SelfDistance : Thickness * 2f;

		if ( d <= 0f )
			return;

		var cell = 1f / d;
		_bins.Clear();

		for ( var i = 0; i < p.Length; i++ )
		{
			var key = Cell( p[i], cell );

			if ( !_bins.TryGetValue( key, out var bin ) )
				_bins[key] = bin = new List<int>();

			bin.Add( i );
		}

		foreach ( var (key, bin) in _bins )
		{
			for ( var dx = 0; dx <= 1; dx++ )
			for ( var dy = dx == 0 ? 0 : -1; dy <= 1; dy++ )
			for ( var dz = dx == 0 && dy == 0 ? 0 : -1; dz <= 1; dz++ )
			{
				// Half the neighbourhood: every pair of cells is visited once, and (0,0,0) is the
				// cell against itself.
				if ( !_bins.TryGetValue( (key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var other ) )
					continue;

				var same = ReferenceEquals( bin, other );

				for ( var a = 0; a < bin.Count; a++ )
				for ( var b = same ? a + 1 : 0; b < other.Count; b++ )
					Separate( p, bin[a], other[b], d );
			}
		}

		SeparateFromOwnFaces( p, d, cell );
	}

	/// <summary>
	/// Keep every particle off the cloth's own FACES, not just off its other particles.
	///
	/// WHY BOTH. Particle-to-particle separation cannot see a face: where a fine fold lies against a
	/// coarse one, the fine fold's vertices sit in the middle of a big triangle, far from any of its
	/// corners, and slide straight through while every pair-distance stays legal. That was the stated
	/// limit of the first pass, and this is it closed.
	///
	/// Each triangle goes into every cell its bounding box touches, so a triangle larger than a cell
	/// is still found from all of them. A particle skips any triangle it is a corner of, and any that
	/// shares a corner with it — those are its own neighbourhood, held by the stretch constraints,
	/// and pushing against them is the solver fighting itself. The push is split between the particle
	/// and the triangle's corners by barycentric weight, so the heavier side moves less and a pinned
	/// particle does not move at all.
	/// </summary>
	void SeparateFromOwnFaces( Vec3[] p, float d, float cell )
	{
		if ( _triangles.Count == 0 )
			return;

		_triangleBins.Clear();

		for ( var t = 0; t < _triangles.Count; t++ )
		{
			var (ia, ib, ic) = _triangles[t];
			var a = p[ia];
			var b = p[ib];
			var c = p[ic];

			var min = new Vec3( MathF.Min( a.x, MathF.Min( b.x, c.x ) ) - d, MathF.Min( a.y, MathF.Min( b.y, c.y ) ) - d, MathF.Min( a.z, MathF.Min( b.z, c.z ) ) - d );
			var max = new Vec3( MathF.Max( a.x, MathF.Max( b.x, c.x ) ) + d, MathF.Max( a.y, MathF.Max( b.y, c.y ) ) + d, MathF.Max( a.z, MathF.Max( b.z, c.z ) ) + d );

			var lo = Cell( min, cell );
			var hi = Cell( max, cell );

			// A triangle spread over a huge number of cells is a degenerate case that would cost
			// more to bin than to skip; the particle pass still covers its corners.
			if ( (long)(hi.Item1 - lo.Item1 + 1) * (hi.Item2 - lo.Item2 + 1) * (hi.Item3 - lo.Item3 + 1) > 512 )
				continue;

			for ( var x = lo.Item1; x <= hi.Item1; x++ )
			for ( var y = lo.Item2; y <= hi.Item2; y++ )
			for ( var z = lo.Item3; z <= hi.Item3; z++ )
			{
				if ( !_triangleBins.TryGetValue( (x, y, z), out var list ) )
					_triangleBins[(x, y, z)] = list = new List<int>();

				list.Add( t );
			}
		}

		for ( var i = 0; i < p.Length; i++ )
		{
			if ( !_triangleBins.TryGetValue( Cell( p[i], cell ), out var candidates ) )
				continue;

			foreach ( var t in candidates )
				PushOffTriangle( p, i, t, d );
		}
	}

	void PushOffTriangle( Vec3[] p, int i, int t, float d )
	{
		var (ia, ib, ic) = _triangles[t];

		// Its own face, or one next door: held by the constraints already.
		if ( i == ia || i == ib || i == ic
			|| _linked.Contains( new EdgeKey( i, ia ) )
			|| _linked.Contains( new EdgeKey( i, ib ) )
			|| _linked.Contains( new EdgeKey( i, ic ) ) )
			return;

		var a = p[ia];
		var b = p[ib];
		var c = p[ic];
		var closest = MeshBVH.ClosestTriangle( p[i], a, b, c );
		var delta = p[i] - closest;
		var len = delta.Length;

		if ( len >= d )
			return;

		if ( len < 1e-6f )
		{
			// Exactly on the face: push along its normal, which is the only direction that means
			// anything here. Deterministic, because the whole solver has to be.
			var normal = Vec3.Cross( b - a, c - a );

			if ( normal.LengthSquared < 1e-12f )
				return;

			delta = normal / normal.Length;
			len = 1f;
		}
		else
		{
			delta /= len;
		}

		// Barycentric weights say how much of the correction each corner of the triangle takes.
		var (wa, wb, wc) = Barycentric( closest, a, b, c );
		var mass = _w[i] + _w[ia] * wa * wa + _w[ib] * wb * wb + _w[ic] * wc * wc;

		if ( mass <= 0f )
			return;

		var push = (d - len) / mass;

		p[i] += delta * (push * _w[i]);
		p[ia] -= delta * (push * _w[ia] * wa);
		p[ib] -= delta * (push * _w[ib] * wb);
		p[ic] -= delta * (push * _w[ic] * wc);
	}

	/// <summary>Where a point on a triangle sits, as the three corner weights that sum to one.</summary>
	static (float A, float B, float C) Barycentric( Vec3 p, Vec3 a, Vec3 b, Vec3 c )
	{
		var v0 = b - a;
		var v1 = c - a;
		var v2 = p - a;

		var d00 = Vec3.Dot( v0, v0 );
		var d01 = Vec3.Dot( v0, v1 );
		var d11 = Vec3.Dot( v1, v1 );
		var d20 = Vec3.Dot( v2, v0 );
		var d21 = Vec3.Dot( v2, v1 );
		var denom = d00 * d11 - d01 * d01;

		if ( MathF.Abs( denom ) < 1e-12f )
			return (1f, 0f, 0f);

		var v = (d11 * d20 - d01 * d21) / denom;
		var w = (d00 * d21 - d01 * d20) / denom;

		return (1f - v - w, v, w);
	}

	void Separate( Vec3[] p, int i, int j, float d )
	{
		var wi = _w[i];
		var wj = _w[j];
		var sum = wi + wj;

		if ( sum == 0f || _linked.Contains( new EdgeKey( i, j ) ) )
			return;

		var delta = p[i] - p[j];
		var len = delta.Length;

		if ( len >= d )
			return;

		if ( len < 1e-6f )
		{
			// Exactly on top of each other: no direction to push along, so pick one. Deterministic,
			// because the whole solver has to be.
			delta = new Vec3( (i % 3 == 0) ? 1f : 0f, (i % 3 == 1) ? 1f : 0f, (i % 3 == 2) ? 1f : 0f );
			len = 1f;
		}

		var push = delta * ((d - len) / len / sum);
		p[i] += push * wi;
		p[j] -= push * wj;
	}

	static (int, int, int) Cell( Vec3 p, float inverseSize ) =>
		((int)MathF.Floor( p.x * inverseSize ), (int)MathF.Floor( p.y * inverseSize ), (int)MathF.Floor( p.z * inverseSize ));

	/// <summary>
	/// Push every particle that is inside the body, or closer to it than the thickness, back out
	/// along the surface normal — and take away some of its sliding, which is friction.
	/// </summary>
	void Collide( Vec3[] p )
	{
		if ( Capsules.Count > 0 )
		{
			for ( var i = 0; i < p.Length; i++ )
			{
				if ( _w[i] == 0f )
					continue;

				foreach ( var capsule in Capsules )
				{
					if ( !capsule.Push( ref p[i], Thickness, out var normal ) )
						continue;

					// Same friction as the mesh contact: keep only Friction's share of the slide.
					var moved = p[i] - _x[i];
					var slide = moved - normal * Vec3.Dot( moved, normal );
					p[i] -= slide * (1f - Friction);
				}
			}
		}

		if ( _colliders.Count == 0 )
			return;

		var reach = Thickness * 4f + 1e-3f;
		var up = new Vec3( 0f, 0f, 1f );

		for ( var i = 0; i < p.Length; i++ )
		{
			if ( _w[i] == 0f )
				continue;

			// The deepest violation across all the bodies, resolved once. Resolving each body as
			// it is found would let the last body undo the first one's push.
			var worst = Thickness;
			var hitPoint = Vec3.Zero;
			var hitNormal = Vec3.Zero;
			var found = false;

			var near = false;

			foreach ( var (mesh, tree) in _colliders )
			{
				if ( tree.NearestSurface( mesh, p[i], reach ) is not { } hit )
					continue;

				near = true;

				var offset = Vec3.Dot( p[i] - hit.Point, hit.Normal );

				if ( offset >= worst )
					continue;

				worst = offset;
				hitPoint = hit.Point;
				hitNormal = hit.Normal;
				found = true;
			}

			if ( !near )
			{
				// Further than the reach from every surface. Either well clear of the body — the
				// common case, nothing to do — or a particle that tunnelled deep inside one. A ray
				// straight up finds the way out of the second case, which is cloth falling on.
				//
				// ONLY A SHORT WAY UP. A particle tunnels a few thicknesses in a substep, never
				// the height of a character, so its way out is near. Without the cap a body made
				// of struts with air between them - a robot, a skeleton, anything kitbashed - had
				// every particle hanging in a gap "rescued" onto whatever was above it, and a
				// T-shirt on Camhead came out 900 units wide with its hem on top of his head.
				foreach ( var (mesh, tree) in _colliders )
				{
					// A way out within the cap needs a surface within the cap, and the bounded
					// query is far cheaper than an unbounded ray. On a body that is mostly air
					// this is the difference between a drape taking a second and a minute.
					if ( tree.NearestSurface( mesh, p[i], reach * 2f ) is null )
						continue;

					if ( tree.Raycast( mesh, p[i], up ) is { } exit && Vec3.Dot( exit.Normal, up ) > 0f
						&& exit.Point.z - p[i].z <= reach * 2f )
					{
						p[i] = exit.Point + exit.Normal * Thickness;
						break;
					}
				}

				continue;
			}

			if ( !found )
				continue;

			// Out along the normal to the thickness, then keep only Friction's share of the
			// sliding this substep did along the surface.
			var pushed = p[i] + hitNormal * (Thickness - Vec3.Dot( p[i] - hitPoint, hitNormal ));
			var moved = pushed - _x[i];
			var slide = moved - hitNormal * Vec3.Dot( moved, hitNormal );
			p[i] = pushed - slide * (1f - Friction);
		}
	}

	/// <summary>The cloth as a mesh: <paramref name="cloth"/>'s faces, UVs and weights, on the
	/// simulated positions.</summary>
	public PolyMesh Bake( PolyMesh cloth )
	{
		var result = cloth.Clone();

		for ( var i = 0; i < _x.Length; i++ )
			result.Positions[i] = _x[i];

		return result;
	}
}
