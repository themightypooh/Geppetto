using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Closing a mouth that crosses ANY number of faces, coplanar or not — the cut through a curved
/// surface, and the second cut into a face a first one already took apart.
///
/// WHAT THE OTHER TWO REPAIRS LEAVE. `MeshHoleRepair` wants one coplanar face containing the whole
/// loop. `MeshHoleRepairSpan` wants exactly two, with the loop crossing their shared edge once.
/// Both are right to be that narrow — see their own comments — and both walk away from the two
/// cases that actually turn up on a real part:
///
/// - **A cut through a curved face.** The wall of a cylinder is a ring of flat quads, so the mouth
///   of a hole drilled into it lies across several of them and is not planar at all. There is no
///   loop normal, no containing face, and nothing for a planar test to be run in.
/// - **A second cut through a face the first one already repaired.** The single-face repair splices
///   a hole in by triangulating, so the surface it fixed is now a fan. The next mouth to land there
///   crosses a dozen coplanar triangles, which is neither one face nor two.
///
/// THE GENERALISATION IS PER-FACE, NOT PER-LOOP. Each face the mouth crosses gets the piece of the
/// loop that lies in it — an ARC, a chord with both ends on that face's own boundary — and the face
/// is then re-partitioned by its arcs into the regions they cut it into. A face may get more than
/// one arc: the middle quad of a cylinder wall has the hole passing through it top and bottom, and
/// the strip between those two arcs is the hole rather than material.
///
/// WHICH REGIONS ARE MATERIAL IS READ OFF THE WALL, NOT GUESSED. Every loop edge is used by exactly
/// one face already — the tunnel wall — and two faces sharing an edge traverse it in opposite
/// directions. So the surface's own boundary has to run along each arc the OTHER way from the wall,
/// and a region that traverses one of its arcs the wall's way is on the void side of it. That rule
/// needs no plane, no containment test and no knowledge of which side the solid is on, which is
/// exactly why it survives the curved case where every planar argument stops working.
///
/// IT REFUSES THE SAME WAY THE OTHERS DO, and one refusal is worth naming: an arc endpoint has to
/// already be a vertex. A mouth that crosses from one face to the next somewhere in the middle of a
/// shared edge would need that crossing invented, and inventing it means splitting a neighbouring
/// face this was not asked to touch. Everything it does build is checked before it is kept — see
/// CloseCurvedLoops — because a repair with this much machinery in it must not be able to leave the
/// mesh worse than it found it.
/// </summary>
public static class MeshHoleRepairCurved
{
	/// <summary>How far off a face's plane a loop vertex may sit and still count as lying in it. A
	/// float-drift tolerance: the mouth is exactly on the surface in exact arithmetic.</summary>
	const float PlaneTolerance = 1e-3f;

	/// <summary>
	/// Close every boundary loop that lies across two or more faces, and return how many.
	///
	/// Run LAST, so it only ever sees what the single-face and span repairs declined.
	///
	/// EVERY REPAIR IS CHECKED AND ROLLED BACK IF IT DID NOT HELP. The other two repairs are simple
	/// enough to argue about; this one splits polygons by chords and decides materiality from a
	/// winding, and the failure mode of getting that wrong is a mesh that is closed, manifold and
	/// inside out. So the mesh is measured before and after each loop, and a repair that did not
	/// strictly reduce the open boundary — or that introduced a non-manifold edge — is undone.
	/// </summary>
	public static int CloseCurvedLoops( PolyMesh mesh )
	{
		if ( mesh is null || mesh.FaceCount == 0 )
			return 0;

		var closed = 0;

		while ( true )
		{
			var loops = BoundaryLoops( mesh );
			var progressed = false;

			foreach ( var loop in loops )
			{
				if ( loop.Count < 3 )
					continue;

				var before = MeshValidator.Validate( mesh );
				var snapshot = mesh.Faces;

				// The face list is replaced wholesale rather than edited, so undoing is putting the
				// old list back. Positions are never touched - nothing here adds a vertex.
				mesh.Faces = CloneFaces( snapshot );

				if ( !TryCloseAcrossFaces( mesh, loop ) )
				{
					mesh.Faces = snapshot;
					continue;
				}

				var after = MeshValidator.Validate( mesh );

				if ( after.BoundaryEdges >= before.BoundaryEdges
					|| after.NonManifoldEdges > before.NonManifoldEdges
					|| !after.IsValid )
				{
					mesh.Faces = snapshot;
					continue;
				}

				closed++;
				progressed = true;
				break;
			}

			if ( !progressed )
				return closed;
		}
	}

	static List<Face> CloneFaces( List<Face> faces )
	{
		var copy = new List<Face>( faces.Count );

		foreach ( var f in faces )
			copy.Add( new Face( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material ) );

		return copy;
	}

	// --- the repair -------------------------------------------------------------------------------

	static bool TryCloseAcrossFaces( PolyMesh mesh, List<int> loop )
	{
		// The direction the SURFACE has to walk the mouth: against the wall, because two faces
		// sharing an edge traverse it opposite ways. Everything below is expressed in this order.
		if ( !OrderAgainstTheWall( mesh, loop, out var ordered ) )
			return false;

		var hosts = FindHostFaces( mesh, ordered );

		// Fewer than two is the single-face case, which MeshHoleRepair does properly and which must
		// not be re-done here: it wants a hole spliced in, not a boundary notched.
		if ( hosts.Count < 2 )
			return false;

		if ( !BuildArcs( mesh, ordered, hosts, out var arcsByFace ) )
			return false;

		// Every face's replacement is built before any of them is written. A half-applied repair is
		// worse than none, and with several faces in play that is a real possibility.
		var replacements = new Dictionary<int, List<Face>>();

		foreach ( var (faceIndex, arcs) in arcsByFace )
		{
			if ( !RepartitionFace( mesh, faceIndex, arcs, out var regions ) )
				return false;

			replacements[faceIndex] = regions;
		}

		// Rebuilt in index order so the surviving faces keep their relative order, which keeps
		// face-index-based references as stable as a repair can leave them.
		var rebuilt = new List<Face>( mesh.FaceCount + replacements.Count );

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			if ( replacements.TryGetValue( fi, out var regions ) )
				rebuilt.AddRange( regions );
			else
				rebuilt.Add( mesh.Faces[fi] );
		}

		mesh.Faces = rebuilt;
		return true;
	}

	/// <summary>
	/// Put the loop in the order the surface has to walk it, or refuse.
	///
	/// Each loop edge is used by exactly one face — the wall — and that face traverses it one way or
	/// the other. A well-formed mouth is consistent about which: the wall is a strip and its edges
	/// all run the same way round it. An inconsistent loop is two openings that met, or a walk that
	/// went the wrong way at a fork, and neither is something to guess at.
	/// </summary>
	static bool OrderAgainstTheWall( PolyMesh mesh, List<int> loop, out List<int> ordered )
	{
		ordered = null;

		var edgeFaces = mesh.BuildEdgeFaces();
		var forward = 0;
		var backward = 0;

		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = loop[i];
			var b = loop[(i + 1) % loop.Count];

			if ( !edgeFaces.TryGetValue( new EdgeKey( a, b ), out var faces ) || faces.Count != 1 )
				return false;

			var wall = mesh.Faces[faces[0]];

			if ( !Traverses( wall, a, b, out var wallForward ) )
				return false;

			if ( wallForward )
				forward++;
			else
				backward++;
		}

		if ( forward > 0 && backward > 0 )
			return false;

		// The wall walks it one way, so the surface walks it the other.
		ordered = new List<int>( loop );

		if ( forward > 0 )
			ordered.Reverse();

		return true;
	}

	/// <summary>Whether the face contains the directed edge a→b, b→a, or neither.</summary>
	static bool Traverses( Face face, int a, int b, out bool forward )
	{
		forward = false;

		for ( var i = 0; i < face.Count; i++ )
		{
			var x = face.Indices[i];
			var y = face.Indices[(i + 1) % face.Count];

			if ( x == a && y == b )
			{
				forward = true;
				return true;
			}

			if ( x == b && y == a )
				return true;
		}

		return false;
	}

	/// <summary>
	/// The faces the mouth crosses: every face that carries at least one of the loop's vertices in
	/// its own plane and inside its own outline.
	///
	/// A WALL IS A FACE THAT USES ONE OF THE LOOP'S EDGES, and that is the test rather than the one
	/// the other two repairs use. They exclude any face that lists a loop VERTEX among its corners,
	/// which is correct for them and too strong here: where the mouth crosses from one face to the
	/// next, the crossing point is often a corner of both — a shaft driven through a ridge breaks
	/// that ridge exactly at the two points the mouth meets it, and by the vertex rule both roof
	/// panels would be dismissed as walls and the mouth left open. The edge rule says the same thing
	/// about a genuine wall (it owns the loop's edges, which is what makes them boundary edges at
	/// all) without dismissing the surface the mouth is in.
	/// </summary>
	static List<int> FindHostFaces( PolyMesh mesh, List<int> loop )
	{
		var hosts = new List<int>();
		var loopEdges = new HashSet<EdgeKey>();

		for ( var i = 0; i < loop.Count; i++ )
			loopEdges.Add( new EdgeKey( loop[i], loop[(i + 1) % loop.Count] ) );

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 )
				continue;

			var isWall = false;

			for ( var i = 0; i < face.Count; i++ )
			{
				if ( loopEdges.Contains( new EdgeKey( face.Indices[i], face.Indices[(i + 1) % face.Count] ) ) )
				{
					isWall = true;
					break;
				}
			}

			if ( isWall )
				continue;

			var normal = mesh.FaceNormal( face );

			if ( normal.LengthSquared < 1e-20f )
				continue;

			Basis( normal, out var u, out var v );

			var plane = Vec3.Dot( mesh.Positions[face.Indices[0]], normal );
			var outline = Polygon( mesh, face, u, v );
			var carries = false;

			foreach ( var index in loop )
			{
				if ( MathF.Abs( Vec3.Dot( mesh.Positions[index], normal ) - plane ) > PlaneTolerance )
					continue;

				var p = Flatten( mesh.Positions[index], u, v );

				if ( PointInPolygon( outline, p ) || OnBoundary( outline, p ) )
				{
					carries = true;
					break;
				}
			}

			if ( carries )
				hosts.Add( fi );
		}

		return hosts;
	}

	/// <summary>
	/// Cut the loop into one arc per crossing of one face, and check that the arcs between them
	/// account for the whole loop.
	///
	/// An arc is a maximal run of loop vertices lying in one face, whose two ends lie ON that face's
	/// boundary. THE COMPLETENESS CHECK IS THE POINT: a loop edge belonging to no face's arc is a
	/// piece of mouth crossing something this repair cannot see, and closing the rest of it would
	/// weld a surface shut around an opening that is still there.
	/// </summary>
	static bool BuildArcs( PolyMesh mesh, List<int> loop, List<int> hosts,
		out Dictionary<int, List<List<int>>> arcsByFace )
	{
		arcsByFace = new Dictionary<int, List<List<int>>>();

		// Which face each loop vertex is inside, and which faces' boundaries it sits on. Both are
		// needed: an arc runs through the inside and ends on the boundary.
		var inside = new Dictionary<int, HashSet<int>>();
		var on = new Dictionary<int, HashSet<int>>();

		foreach ( var fi in hosts )
		{
			var face = mesh.Faces[fi];
			var normal = mesh.FaceNormal( face );

			Basis( normal, out var u, out var v );

			var plane = Vec3.Dot( mesh.Positions[face.Indices[0]], normal );
			var outline = Polygon( mesh, face, u, v );

			for ( var i = 0; i < loop.Count; i++ )
			{
				if ( MathF.Abs( Vec3.Dot( mesh.Positions[loop[i]], normal ) - plane ) > PlaneTolerance )
					continue;

				var p = Flatten( mesh.Positions[loop[i]], u, v );

				if ( OnBoundary( outline, p ) )
					Add( on, i, fi );
				else if ( PointInPolygon( outline, p ) )
					Add( inside, i, fi );
			}
		}

		// A vertex strictly inside two faces means two surfaces overlap, and which one owns the
		// mouth stops being answerable.
		foreach ( var (_, faces) in inside )
		{
			if ( faces.Count > 1 )
				return false;
		}

		// Every loop EDGE has to lie in exactly one face, and that face is what the arcs are built
		// from. Working per edge rather than per vertex is what makes the completeness check mean
		// something: a vertex can sit on a boundary shared by two faces without telling you which
		// side the mouth went.
		var edgeFace = new int[loop.Count];

		for ( var i = 0; i < loop.Count; i++ )
		{
			var j = (i + 1) % loop.Count;

			// Faces holding BOTH endpoints, split by whether the edge actually goes into the face
			// or merely runs along its outline. An edge with both ends on boundaries is the second
			// kind - it can sit on a shared edge two faces both own - so a face the edge cuts INTO
			// wins over one it only touches.
			var cutting = new List<int>();
			var touching = new List<int>();

			foreach ( var fi in hosts )
			{
				if ( !LiesIn( inside, on, i, fi ) || !LiesIn( inside, on, j, fi ) )
					continue;

				if ( Has( inside, i, fi ) || Has( inside, j, fi ) )
					cutting.Add( fi );
				else
					touching.Add( fi );
			}

			var candidates = cutting.Count > 0 ? cutting : touching;

			// Exactly one, or there is no answer. Two faces the mouth cuts into along the same edge
			// is two surfaces overlapping, and picking one would seal the wrong side.
			if ( candidates.Count != 1 )
				return false;

			var owner = candidates[0];

			edgeFace[i] = owner;
		}

		// Runs of consecutive edges owned by one face are one arc. Start the walk at a change of
		// owner so a run is never split across the wrap.
		var start = -1;

		for ( var i = 0; i < loop.Count; i++ )
		{
			if ( edgeFace[i] != edgeFace[(i + loop.Count - 1) % loop.Count] )
			{
				start = i;
				break;
			}
		}

		// No change of owner anywhere means one face owns the whole loop, which is the single-face
		// case and not this one.
		if ( start < 0 )
			return false;

		var index = start;

		for ( var consumed = 0; consumed < loop.Count; )
		{
			var owner = edgeFace[index];
			var arc = new List<int> { loop[index] };

			while ( consumed < loop.Count && edgeFace[index] == owner )
			{
				index = (index + 1) % loop.Count;
				arc.Add( loop[index] );
				consumed++;
			}

			// Both ends of an arc must sit on the face's own boundary, or there is nothing to splice
			// it into.
			if ( !Has( on, IndexOf( loop, arc[0] ), owner ) || !Has( on, IndexOf( loop, arc[^1] ), owner ) )
				return false;

			if ( !arcsByFace.TryGetValue( owner, out var list ) )
				arcsByFace[owner] = list = new List<List<int>>();

			list.Add( arc );
		}

		return arcsByFace.Count >= 2;

		static void Add( Dictionary<int, HashSet<int>> map, int key, int value )
		{
			if ( !map.TryGetValue( key, out var set ) )
				map[key] = set = new HashSet<int>();

			set.Add( value );
		}

		static bool Has( Dictionary<int, HashSet<int>> map, int key, int value ) =>
			key >= 0 && map.TryGetValue( key, out var set ) && set.Contains( value );

		static bool LiesIn( Dictionary<int, HashSet<int>> inside, Dictionary<int, HashSet<int>> on, int key, int face ) =>
			Has( inside, key, face ) || Has( on, key, face );
	}

	static int IndexOf( List<int> loop, int vertex ) => loop.IndexOf( vertex );

	/// <summary>
	/// One face, cut into the regions its arcs make of it, with the void ones dropped.
	///
	/// The arcs are chords: both ends lie on the face's outline, and they do not cross each other
	/// because the loop they came from does not cross itself. Cutting a polygon by non-crossing
	/// chords is a recursion — split by one chord into two halves, hand each half the chords whose
	/// ends it still holds.
	///
	/// MATERIALITY IS DECIDED ON THE FINISHED REGIONS, NOT CARRIED DOWN THE RECURSION, and that
	/// distinction is the whole correctness of this function. It is tempting to mark the half that
	/// walks a chord the wall's way as void and let its children inherit that — and it is wrong,
	/// because that half is not a region yet. On the middle face of a mouth that crosses three
	/// strips, the first chord cuts off the top strip and leaves "the mouth plus the bottom strip"
	/// as one lump; the second chord then separates them, and the bottom strip is material despite
	/// having come out of the lump. So every finished ring is asked directly: which arcs are on YOUR
	/// boundary, and which way do you walk them. All forwards is material, and anything else is
	/// hole.
	/// </summary>
	static bool RepartitionFace( PolyMesh mesh, int faceIndex, List<List<int>> arcs, out List<Face> regions )
	{
		regions = null;

		var face = mesh.Faces[faceIndex];
		var normal = mesh.FaceNormal( face );

		if ( normal.LengthSquared < 1e-20f )
			return false;

		Basis( normal, out var u, out var v );

		if ( !AugmentedBoundary( mesh, face, arcs, u, v, out var boundary ) )
			return false;

		var open = new List<(List<int> Ring, List<List<int>> Arcs)>
		{
			(boundary, arcs)
		};

		var done = new List<List<int>>();

		while ( open.Count > 0 )
		{
			var (ring, remaining) = open[^1];
			open.RemoveAt( open.Count - 1 );

			if ( remaining.Count == 0 )
			{
				done.Add( ring );
				continue;
			}

			var arc = remaining[0];
			var rest = remaining.GetRange( 1, remaining.Count - 1 );

			var from = ring.IndexOf( arc[0] );
			var to = ring.IndexOf( arc[^1] );

			if ( from < 0 || to < 0 || from == to )
				return false;

			// The half that runs from the arc's start round to its end and comes back ALONG the arc
			// backwards, and the half that closes by walking it forwards. Which of them is material
			// is settled at the end, on the finished rings - see the comment above.
			var back = Slice( ring, from, to );
			AppendInterior( back, arc, reversed: true );

			var forwards = Slice( ring, to, from );
			AppendInterior( forwards, arc, reversed: false );

			var backArcs = Distribute( rest, back );
			var forwardArcs = Distribute( rest, forwards );

			// A chord whose ends do not both land in one of the two halves means the arcs cross,
			// which a simple loop cannot do - so it is a fault in the arcs rather than a shape.
			if ( backArcs.Count + forwardArcs.Count != rest.Count )
				return false;

			open.Add( (back, backArcs) );
			open.Add( (forwards, forwardArcs) );
		}

		regions = new List<Face>();

		foreach ( var ring in done )
		{
			if ( ring.Count < 3 )
				return false;

			var seen = new HashSet<int>();

			foreach ( var index in ring )
			{
				if ( !seen.Add( index ) )
					return false;
			}

			if ( !IsMaterial( ring, arcs, out var material ) )
				return false;

			if ( !material )
				continue;

			regions.Add( new Face( ring.ToArray(), CarriedUVs( mesh, face, ring, u, v ), face.Material ) );
		}

		// Every region dropped means the whole face was inside the mouth, which cannot be: a face
		// the mouth crosses has material on at least one side of the crossing.
		return regions.Count > 0;
	}

	/// <summary>
	/// Whether a finished region is material: it walks every arc on its own boundary the way the
	/// SURFACE walks the mouth, rather than the way the wall does.
	///
	/// A region with no arc on its boundary at all is a fault rather than an answer — a face split by
	/// chords has every piece touching one — so it refuses instead of guessing.
	/// </summary>
	static bool IsMaterial( List<int> ring, List<List<int>> arcs, out bool material )
	{
		material = true;

		var found = false;

		foreach ( var arc in arcs )
		{
			var at = ring.IndexOf( arc[0] );

			if ( at < 0 )
				continue;

			var next = ring[(at + 1) % ring.Count];
			var previous = ring[(at + ring.Count - 1) % ring.Count];

			if ( next == arc[1] )
			{
				found = true;
				continue;
			}

			if ( previous == arc[1] )
			{
				found = true;
				material = false;
			}
		}

		return found;
	}

	/// <summary>The chords whose two ends both appear in this ring.</summary>
	static List<List<int>> Distribute( List<List<int>> arcs, List<int> ring )
	{
		var kept = new List<List<int>>();

		foreach ( var arc in arcs )
		{
			if ( ring.Contains( arc[0] ) && ring.Contains( arc[^1] ) )
				kept.Add( arc );
		}

		return kept;
	}

	/// <summary>The run of a cyclic ring from one position to another, inclusive of both.</summary>
	static List<int> Slice( List<int> ring, int from, int to )
	{
		var slice = new List<int>();
		var i = from;

		while ( true )
		{
			slice.Add( ring[i] );

			if ( i == to )
				return slice;

			i = (i + 1) % ring.Count;

			if ( slice.Count > ring.Count )
				return slice;
		}
	}

	/// <summary>The arc's inside, in either direction. The ends are already on the ring.</summary>
	static void AppendInterior( List<int> ring, List<int> arc, bool reversed )
	{
		if ( reversed )
		{
			for ( var i = arc.Count - 2; i >= 1; i-- )
				ring.Add( arc[i] );
		}
		else
		{
			for ( var i = 1; i < arc.Count - 1; i++ )
				ring.Add( arc[i] );
		}
	}

	/// <summary>
	/// The face's own boundary with every arc endpoint inserted into the edge it sits on, sorted
	/// along that edge.
	///
	/// The endpoints are vertices that already exist and already lie on the outline — that is the
	/// condition this repair refuses without — so nothing is invented here. What it produces is the
	/// ring the chord recursion indexes into.
	/// </summary>
	static bool AugmentedBoundary( PolyMesh mesh, Face face, List<List<int>> arcs, Vec3 u, Vec3 v,
		out List<int> boundary )
	{
		boundary = new List<int>( face.Count + arcs.Count * 2 );

		var ends = new List<int>();

		foreach ( var arc in arcs )
		{
			ends.Add( arc[0] );
			ends.Add( arc[^1] );
		}

		for ( var i = 0; i < face.Count; i++ )
		{
			var a = face.Indices[i];
			var b = face.Indices[(i + 1) % face.Count];

			boundary.Add( a );

			var aFlat = Flatten( mesh.Positions[a], u, v );
			var bFlat = Flatten( mesh.Positions[b], u, v );
			var onEdge = new List<(float T, int Index)>();

			foreach ( var end in ends )
			{
				if ( end == a || end == b || boundary.Contains( end ) )
					continue;

				var p = Flatten( mesh.Positions[end], u, v );

				if ( !OnSegment( aFlat, bFlat, p ) )
					continue;

				var along = bFlat - aFlat;
				var t = ((p.x - aFlat.x) * along.x + (p.y - aFlat.y) * along.y) / MathF.Max( along.LengthSquared, 1e-12f );

				onEdge.Add( (t, end) );
			}

			onEdge.Sort( ( x, y ) => x.T.CompareTo( y.T ) );

			foreach ( var (_, index) in onEdge )
				boundary.Add( index );
		}

		// Every endpoint has to have found a home, or the ring the recursion indexes into does not
		// contain the chord it is about to split by.
		foreach ( var end in ends )
		{
			if ( !boundary.Contains( end ) )
				return false;
		}

		return true;
	}

	/// <summary>
	/// UVs for a rebuilt region: the face's own corners keep theirs, and the mouth's vertices get
	/// theirs from the same planar map the corners already follow. Same rule and same reason as
	/// MeshHoleRepairSpan.NewUVs — a repair must not smear the texture across the surface it fixed.
	/// </summary>
	static Vec2[] CarriedUVs( PolyMesh mesh, Face face, List<int> ring, Vec3 u, Vec3 v )
	{
		var known = new Dictionary<int, Vec2>();

		for ( var i = 0; i < face.Count; i++ )
			known[face.Indices[i]] = face.UVs[i];

		var uvs = new Vec2[ring.Count];
		var origin = Flatten( mesh.Positions[face.Indices[0]], u, v );
		var originUv = face.UVs[0];

		var scaleU = 0f;
		var scaleV = 0f;

		for ( var i = 1; i < face.Count; i++ )
		{
			var p = Flatten( mesh.Positions[face.Indices[i]], u, v ) - origin;
			var duv = new Vec2( face.UVs[i].x - originUv.x, face.UVs[i].y - originUv.y );

			if ( MathF.Abs( p.x ) > 1e-6f && scaleU == 0f )
				scaleU = duv.x / p.x;

			if ( MathF.Abs( p.y ) > 1e-6f && scaleV == 0f )
				scaleV = duv.y / p.y;
		}

		for ( var i = 0; i < ring.Count; i++ )
		{
			if ( known.TryGetValue( ring[i], out var existing ) )
			{
				uvs[i] = existing;
				continue;
			}

			var p = Flatten( mesh.Positions[ring[i]], u, v ) - origin;
			uvs[i] = new Vec2( originUv.x + p.x * scaleU, originUv.y + p.y * scaleV );
		}

		return uvs;
	}

	// --- shared geometry, kept here so this file stands alone -------------------------------------

	static List<List<int>> BoundaryLoops( PolyMesh mesh )
	{
		var atVertex = new Dictionary<int, List<int>>();

		foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count != 1 )
				continue;

			Link( key.A, key.B );
			Link( key.B, key.A );
		}

		var loops = new List<List<int>>();
		var used = new HashSet<EdgeKey>();

		foreach ( var start in atVertex.Keys )
		{
			if ( atVertex[start].Count != 2 )
				continue;

			var loop = new List<int>();
			var current = start;
			var previous = -1;

			while ( true )
			{
				loop.Add( current );

				var next = -1;

				foreach ( var candidate in atVertex[current] )
				{
					if ( candidate == previous || used.Contains( new EdgeKey( current, candidate ) ) )
						continue;

					next = candidate;
					break;
				}

				if ( next < 0 )
					break;

				used.Add( new EdgeKey( current, next ) );
				previous = current;
				current = next;

				if ( current == start )
					break;
			}

			if ( loop.Count >= 3 && current == start )
				loops.Add( loop );
		}

		return loops;

		void Link( int from, int to )
		{
			if ( !atVertex.TryGetValue( from, out var list ) )
			{
				list = new List<int>( 2 );
				atVertex[from] = list;
			}

			list.Add( to );
		}
	}

	static void Basis( Vec3 normal, out Vec3 u, out Vec3 v )
	{
		var n = normal.Normal;
		var seed = MathF.Abs( n.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );

		u = Vec3.Cross( seed, n ).Normal;
		v = Vec3.Cross( n, u ).Normal;
	}

	static Vec2 Flatten( Vec3 p, Vec3 u, Vec3 v ) => new( Vec3.Dot( p, u ), Vec3.Dot( p, v ) );

	static List<Vec2> Polygon( PolyMesh mesh, Face face, Vec3 u, Vec3 v )
	{
		var polygon = new List<Vec2>( face.Count );

		foreach ( var index in face.Indices )
			polygon.Add( Flatten( mesh.Positions[index], u, v ) );

		return polygon;
	}

	static bool OnBoundary( List<Vec2> polygon, Vec2 p )
	{
		for ( var i = 0; i < polygon.Count; i++ )
		{
			if ( OnSegment( polygon[i], polygon[(i + 1) % polygon.Count], p ) )
				return true;
		}

		return false;
	}

	static bool OnSegment( Vec2 a, Vec2 b, Vec2 p )
	{
		var along = b - a;
		var length = along.Length;

		if ( length < 1e-9f )
			return (p - a).Length < 1e-5f;

		var cross = along.x * (p.y - a.y) - along.y * (p.x - a.x);

		if ( MathF.Abs( cross ) / length > 1e-4f )
			return false;

		var t = ((p.x - a.x) * along.x + (p.y - a.y) * along.y) / (length * length);
		return t >= -1e-5f && t <= 1f + 1e-5f;
	}

	static bool PointInPolygon( List<Vec2> polygon, Vec2 point )
	{
		var inside = false;

		for ( int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++ )
		{
			var a = polygon[i];
			var b = polygon[j];

			if ( a.y > point.y != b.y > point.y
				&& point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x )
			{
				inside = !inside;
			}
		}

		return inside;
	}
}
