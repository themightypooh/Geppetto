using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The flat SURFACE a face belongs to, rather than the one n-gon a raycast happened to land on.
///
/// WHY THIS EXISTS. A Face is a unit of mesh storage; a surface is what somebody looking at the
/// model calls "that face". They agree on a primitive and stop agreeing the moment anything cuts
/// the model: a boolean routinely returns one wall as dozens of coplanar triangles and quads that
/// share a plane, a normal and a material, and CoplanarMerge's own header records eighty-eight of
/// them on a real part. Everything that pointed at Faces[i] then pointed at one eighty-eighth of a
/// wall — the hover highlight lit a triangle, the edge picker offered the triangulation seams as
/// though they were edges of the part, and the sketch grid that DID work on the whole wall
/// disagreed with both of them on screen at the same time.
///
/// CoplanarMerge fixes this in the mesh where it can, and REFUSES where it cannot — a group whose
/// boundary does not chain into clean loops is left fragmented on purpose, because a wrong merge
/// is a self-intersecting face that passes every validator. So fragments survive into the viewport
/// by design, and the viewport needs its own answer rather than a promise that they never happen.
/// This is that answer, and it is read-only: nothing here touches the mesh.
///
/// THE IDENTITY IS CoplanarMerge'S, DELIBERATELY. Same plane, same facing, same material slot,
/// reachable across shared edges. Copying the rule rather than inventing a looser one is what
/// keeps "what lit up when I hovered" and "what got painted when I clicked" the same set of
/// triangles — two nearly-identical rules that drift apart show up as a highlight covering more
/// than the click does, which is the kind of bug nobody can describe.
///
/// EDGE-CONNECTED, WHICH A PLANE TEST ALONE IS NOT. Two towers standing on a slab have coplanar
/// tops; a rule that only asked "is it in this plane" would hand back both of them as one surface
/// and outline a face the cursor is nowhere near. Reachability is what makes the answer local.
/// </summary>
public sealed class FaceSurface
{
	public readonly PolyMesh Mesh;

	/// <summary>The face this was grown from — always a member, whatever else joins it.</summary>
	public readonly int Seed;

	/// <summary>Outward normal of the surface, taken from the seed face.</summary>
	public readonly Vec3 Normal;

	/// <summary>A point on the surface: the seed face's centroid.</summary>
	public readonly Vec3 Origin;

	/// <summary>Every face of the surface, in ascending index order so two identical calls hand
	/// back an identically-ordered list. Anything holding a position in this list across a frame
	/// is otherwise holding a number that means something else next frame.</summary>
	public readonly List<int> Faces = new();

	/// <summary>
	/// The surface's silhouette: the edges used exactly ONCE across its faces, as mesh vertex
	/// index pairs. That is precisely the outline including the rim of any hole through it, and it
	/// excludes every interior seam for free — an edge between two fragments of one surface is
	/// used twice and drops out.
	/// </summary>
	public readonly List<(int A, int B)> Boundary = new();

	FaceSurface( PolyMesh mesh, int seed, Vec3 normal, Vec3 origin )
	{
		Mesh = mesh;
		Seed = seed;
		Normal = normal;
		Origin = origin;
	}

	public bool IsEmpty => Faces.Count == 0;

	/// <summary>Whether a face index is part of this surface. Linear over a list that is one face
	/// long in the ordinary case and a few dozen at worst.</summary>
	public bool Contains( int faceIndex ) => Faces.Contains( faceIndex );

	/// <summary>The two endpoints of a boundary edge, in model space. Bounds-checked to a
	/// zero-length segment rather than throwing: this is drawn every frame from indices a rebuild
	/// underneath could have invalidated, and a viewport that throws once per frame is worse than
	/// one that briefly draws nothing.</summary>
	public (Vec3 A, Vec3 B) Segment( int index )
	{
		if ( Mesh is null || index < 0 || index >= Boundary.Count )
			return (Vec3.Zero, Vec3.Zero);

		var (a, b) = Boundary[index];

		if ( a < 0 || a >= Mesh.Positions.Count || b < 0 || b >= Mesh.Positions.Count )
			return (Vec3.Zero, Vec3.Zero);

		return (Mesh.Positions[a], Mesh.Positions[b]);
	}

	/// <summary>
	/// The nearest boundary edge to a point, which is the one an edge pick should offer.
	///
	/// SEAMS ARE NOT CANDIDATES, and that is the whole reason this exists rather than a scan over
	/// the hit face's own edges. A seam is an edge of the mesh but not an edge of the part: it sits
	/// in the middle of a flat surface, filleting along it does nothing, and offering it lit up in
	/// pick blue is a promise the model cannot keep.
	/// </summary>
	public bool TryClosestEdge( Vec3 point, out EdgeKey key, out float distance )
	{
		key = default;
		distance = float.MaxValue;

		var found = false;

		for ( var i = 0; i < Boundary.Count; i++ )
		{
			var (a, b) = Segment( i );
			var ab = b - a;
			var lengthSquared = ab.LengthSquared;

			if ( lengthSquared < 1e-20f )
				continue;

			var t = Vec3.Dot( point - a, ab ) / lengthSquared;

			t = t < 0f ? 0f : t > 1f ? 1f : t;

			var d = (a + ab * t - point).Length;

			if ( d >= distance )
				continue;

			distance = d;
			key = new EdgeKey( Boundary[i].A, Boundary[i].B );
			found = true;
		}

		return found;
	}

	/// <summary>Cosine limit for "these two faces face the same way". CoplanarMerge's number, and
	/// signed rather than absolute for CoplanarMerge's reason: the two sides of a zero-thickness
	/// sliver are never one surface.</summary>
	const float NormalTolerance = 0.9995f;

	/// <summary>
	/// Grow the surface containing <paramref name="faceIndex"/>.
	///
	/// Never fails: a degenerate or out-of-range seed comes back empty, and a seed whose
	/// neighbours all disagree comes back as itself alone. That matters more than it sounds —
	/// every caller here is a draw call, and "no surface" has to mean "draw the one face" rather
	/// than "draw nothing on the thing under the cursor".
	/// </summary>
	public static FaceSurface FromFace( PolyMesh mesh, int faceIndex )
	{
		if ( mesh is null || faceIndex < 0 || faceIndex >= mesh.Faces.Count )
			return new FaceSurface( mesh, -1, new Vec3( 0, 0, 1 ), Vec3.Zero );

		var seed = mesh.Faces[faceIndex];

		if ( seed.Count < 3 )
			return new FaceSurface( mesh, faceIndex, new Vec3( 0, 0, 1 ), Vec3.Zero );

		var normal = mesh.FaceNormal( seed );
		var origin = mesh.FaceCentroid( seed );
		var surface = new FaceSurface( mesh, faceIndex, normal, origin );

		// Scaled to the part, for the reason every other tolerance in the sketcher is: a constant
		// generous on a 100-unit block silently welds every vertex of a 0.1-unit one.
		var tolerance = MathF.Max( mesh.BoundsDiagonal * 1e-4f, 1e-5f );

		// WELDED, NOT INDEXED. A boolean leaves coincident vertices behind routinely, and two
		// fragments meeting along a seam described by two different index pairs each count their
		// half of it once — so the seam survives into the outline as a pair of lines drawn on top
		// of each other, and the flood fill never crosses it. Position is what they agree about.
		var weld = Weld( mesh, tolerance );
		var edgeFaces = new Dictionary<EdgeKey, List<int>>();

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			var face = mesh.Faces[i];

			if ( face.Count < 3 )
				continue;

			for ( var c = 0; c < face.Count; c++ )
			{
				var key = WeldedEdge( weld, face, c );

				if ( !edgeFaces.TryGetValue( key, out var list ) )
					edgeFaces[key] = list = new List<int>();

				list.Add( i );
			}
		}

		var members = new HashSet<int> { faceIndex };
		var queue = new Queue<int>();

		queue.Enqueue( faceIndex );

		while ( queue.Count > 0 )
		{
			var current = mesh.Faces[queue.Dequeue()];

			for ( var c = 0; c < current.Count; c++ )
			{
				if ( !edgeFaces.TryGetValue( WeldedEdge( weld, current, c ), out var touching ) )
					continue;

				foreach ( var candidate in touching )
				{
					if ( members.Contains( candidate ) )
						continue;

					if ( !SameSurface( mesh, mesh.Faces[candidate], seed, normal, origin, tolerance ) )
						continue;

					members.Add( candidate );
					queue.Enqueue( candidate );
				}
			}
		}

		foreach ( var member in members )
			surface.Faces.Add( member );

		surface.Faces.Sort();
		surface.BuildBoundary( weld );

		return surface;
	}

	/// <summary>Whether a face joins the surface: same material slot, same facing, and every corner
	/// in the seed's plane. Flatness is measured against the SEED rather than against each
	/// neighbour in turn, so a barely-curved tessellation cannot creep around a cylinder one
	/// tolerance at a time.</summary>
	static bool SameSurface( PolyMesh mesh, Face candidate, Face seed, Vec3 normal, Vec3 origin,
		float tolerance )
	{
		if ( candidate.Count < 3 || candidate.Material != seed.Material )
			return false;

		if ( Vec3.Dot( mesh.FaceNormal( candidate ), normal ) < NormalTolerance )
			return false;

		for ( var c = 0; c < candidate.Count; c++ )
		{
			if ( MathF.Abs( Vec3.Dot( mesh.Positions[candidate.Indices[c]] - origin, normal ) ) > tolerance )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Collect the edges used once across the surface's faces.
	///
	/// Counted on WELDED keys and reported as the mesh indices that produced them, so a caller can
	/// read positions straight out of the mesh without knowing welding happened. Faces are walked
	/// in index order rather than over the dictionary, because dictionary order is not promised and
	/// an outline whose edges renumber between two identical calls makes every index a caller is
	/// holding meaningless.
	/// </summary>
	void BuildBoundary( int[] weld )
	{
		var uses = new Dictionary<EdgeKey, int>();
		var first = new Dictionary<EdgeKey, (int A, int B)>();

		foreach ( var index in Faces )
		{
			var face = Mesh.Faces[index];

			for ( var c = 0; c < face.Count; c++ )
			{
				var key = WeldedEdge( weld, face, c );

				uses[key] = uses.TryGetValue( key, out var n ) ? n + 1 : 1;

				if ( !first.ContainsKey( key ) )
					first[key] = (face.Indices[c], face.Indices[(c + 1) % face.Count]);
			}
		}

		var taken = new HashSet<EdgeKey>();

		foreach ( var index in Faces )
		{
			var face = Mesh.Faces[index];

			for ( var c = 0; c < face.Count; c++ )
			{
				var key = WeldedEdge( weld, face, c );

				if ( uses[key] != 1 || !taken.Add( key ) )
					continue;

				var (a, b) = first[key];

				if ( a != b )
					Boundary.Add( (a, b) );
			}
		}
	}

	static EdgeKey WeldedEdge( int[] weld, Face face, int corner ) => new(
		weld[face.Indices[corner]],
		weld[face.Indices[(corner + 1) % face.Count]] );

	/// <summary>
	/// Vertex index to the index of the first vertex sharing its position, within
	/// <paramref name="tolerance"/>.
	///
	/// Hashed on a grid one tolerance across rather than compared against everything, because this
	/// runs for the face under the cursor and a quadratic pass over a subdivided mesh is a frame.
	/// Neighbouring cells are checked too, so a pair straddling a cell boundary still welds.
	/// </summary>
	static int[] Weld( PolyMesh mesh, float tolerance )
	{
		var weld = new int[mesh.Positions.Count];
		var cells = new Dictionary<(int X, int Y, int Z), List<int>>();
		var cell = MathF.Max( tolerance, 1e-6f );

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			var key = ((int)MathF.Floor( p.x / cell ), (int)MathF.Floor( p.y / cell ),
				(int)MathF.Floor( p.z / cell ));

			weld[i] = i;

			var matched = false;

			for ( var dx = -1; dx <= 1 && !matched; dx++ )
			{
				for ( var dy = -1; dy <= 1 && !matched; dy++ )
				{
					for ( var dz = -1; dz <= 1 && !matched; dz++ )
					{
						if ( !cells.TryGetValue( (key.Item1 + dx, key.Item2 + dy, key.Item3 + dz),
							out var bucket ) )
							continue;

						foreach ( var other in bucket )
						{
							if ( (mesh.Positions[other] - p).LengthSquared > tolerance * tolerance )
								continue;

							weld[i] = weld[other];
							matched = true;
							break;
						}
					}
				}
			}

			if ( matched )
				continue;

			if ( !cells.TryGetValue( key, out var list ) )
				cells[key] = list = new List<int>();

			list.Add( i );
		}

		return weld;
	}
}
