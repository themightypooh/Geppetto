using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Where a ray hit a mesh: the point, the face it hit, and that face's normal.</summary>
public readonly struct MeshHit
{
	public readonly Vec3 Point;
	public readonly int FaceIndex;
	public readonly Vec3 Normal;
	public readonly float Distance;

	public MeshHit( Vec3 point, int faceIndex, Vec3 normal, float distance )
	{
		Point = point;
		FaceIndex = faceIndex;
		Normal = normal;
		Distance = distance;
	}
}

/// <summary>
/// A run of solid the ray passes through: where it went in, where it came out, and the point
/// halfway between them.
///
/// THE MIDPOINT IS WHERE A JOINT GOES. A bone belongs on the medial line of the limb it drives, and
/// a click can only ever name a surface — so a spine placed by clicking lands on the skin of the
/// chest, and every weight it drives is computed from a line running down the front of the model
/// rather than through the middle of it. Halfway between the front and the back of the material you
/// pointed at is that line, for the price of one more ray query.
/// </summary>
public readonly struct SolidSpan
{
	/// <summary>Where the ray entered the material — the same point a click already gives you.</summary>
	public readonly Vec3 Entry;

	/// <summary>Where it left again.</summary>
	public readonly Vec3 Exit;

	public SolidSpan( Vec3 entry, Vec3 exit )
	{
		Entry = entry;
		Exit = exit;
	}

	public Vec3 Midpoint => (Entry + Exit) * 0.5f;

	/// <summary>How much material the ray crossed. Worth showing: it is the number that says
	/// whether the midpoint is meaningfully different from the surface.</summary>
	public float Thickness => (Exit - Entry).Length;
}

/// <summary>
/// Ray-mesh intersection, for clicking a face of a solid in the viewport.
///
/// PURE GEOMETRY, NO ENGINE SURFACE — which is why it lives here rather than in the editor. The
/// only thing the viewport contributes is the ray itself (Gizmo.CurrentRay, converted to Vec3);
/// everything about deciding which triangle it hit is ordinary math that can be built and proven
/// without s&amp;box anywhere near it.
///
/// Faces are triangulated the same way EffigyPreview builds the render mesh — by Triangulate.Face
/// — so a click hits exactly the triangle that would actually be drawn there. A different
/// triangulation would occasionally pick a face whose diagonal put the real geometry on the other
/// side of the click, and a fan over a concave cap would let clicks land in the notch it wrongly
/// filled in.
/// </summary>
public static class MeshRaycast
{
	/// <summary>
	/// The nearest face of <paramref name="mesh"/> that <paramref name="origin"/> + t *
	/// <paramref name="direction"/> hits, for t > 0. Null if nothing is hit.
	/// </summary>
	public static MeshHit? Raycast( PolyMesh mesh, Vec3 origin, Vec3 direction ) =>
		Raycast( mesh, origin, direction, null );

	/// <summary>
	/// The same nearest-hit answer, with a <see cref="MeshBVH"/> doing the culling.
	///
	/// WHY THE OVERLOAD RATHER THAN A CACHE IN HERE: the linear scan below is O(faces), and the
	/// viewport runs this every frame the face picker is armed. On a 60k-face import that is
	/// ~13ms and ~25MB of garbage PER FRAME, which is the whole frame budget spent on deciding
	/// what the cursor is over. A tree turns it into a handful of triangle tests. The kernel does
	/// not own the cache because it does not know when the mesh stops changing - the caller does,
	/// which is the same reason <see cref="Brush.Apply"/> takes its bvh rather than building one.
	///
	/// A bvh built on different topology is ignored rather than trusted, so a caller holding a
	/// stale tree gets a slow answer instead of a wrong one.
	/// </summary>
	public static MeshHit? Raycast( PolyMesh mesh, Vec3 origin, Vec3 direction, MeshBVH bvh )
	{
		if ( mesh is null )
			return null;

		if ( bvh is not null && !bvh.IsEmpty && bvh.FaceCount == mesh.FaceCount )
			return bvh.Raycast( mesh, origin, direction );

		var dir = direction.Normal;

		MeshHit? best = null;

		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			if ( !HitFace( mesh, fi, origin, dir, out var t, out var point ) )
				continue;

			if ( best is { } current && t >= current.Distance )
				continue;

			best = new MeshHit( point, fi, mesh.FaceNormal( mesh.Faces[fi] ), t );
		}

		return best;
	}

	/// <summary>
	/// Every face the ray crosses, nearest first — not just the one you can see.
	///
	/// The nearest hit is what a CLICK wants: you pointed at a surface and that surface is the
	/// answer. Placing something INSIDE a solid is the opposite question — where does the material
	/// begin and end along this line — and it cannot be answered by the front face alone.
	///
	/// Back faces count, and have to. The exit is by definition a face pointing away from the ray,
	/// so a front-face-only scan finds every entry and no exit at all.
	/// </summary>
	public static List<MeshHit> AllHits( PolyMesh mesh, Vec3 origin, Vec3 direction )
	{
		var hits = new List<MeshHit>();

		if ( mesh is null )
			return hits;

		var dir = direction.Normal;

		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			if ( HitFace( mesh, fi, origin, dir, out var t, out var point ) )
				hits.Add( new MeshHit( point, fi, mesh.FaceNormal( mesh.Faces[fi] ), t ) );
		}

		hits.Sort( ( a, b ) => a.Distance.CompareTo( b.Distance ) );

		return hits;
	}

	/// <summary>
	/// Nearest triangle of one face. Shared by the linear scan and the BVH so a click cannot
	/// disagree with a stroke sample about which triangle was there.
	/// </summary>
	public static bool HitFace( PolyMesh mesh, int faceIndex, Vec3 origin, Vec3 dir, out float t, out Vec3 point )
	{
		t = 0f;
		point = default;

		if ( mesh is null || faceIndex < 0 || faceIndex >= mesh.Faces.Count )
			return false;

		var face = mesh.Faces[faceIndex];

		if ( face.Count < 3 )
			return false;

		// A TRIANGLE IS ITS OWN TRIANGULATION, and the general path below allocates a corner list
		// and a triangle list to rediscover that. Every mesh that arrives from outside the tool is
		// triangles - an OBJ out of Blender, Meshy or a scan - so this is not a micro-optimisation
		// on a rare case, it is the case, and it is what keeps a picking ray off the heap.
		if ( face.Count == 3 )
		{
			if ( !TriangleHit( origin, dir,
				mesh.Positions[face.Indices[0]],
				mesh.Positions[face.Indices[1]],
				mesh.Positions[face.Indices[2]], out var tri, out var triPoint ) )
				return false;

			t = tri;
			point = triPoint;
			return true;
		}

		var corners = new List<Vec3>( face.Count );

		for ( var c = 0; c < face.Count; c++ )
			corners.Add( mesh.Positions[face.Indices[c]] );

		var hit = false;
		var bestT = float.MaxValue;
		var bestP = default( Vec3 );

		foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
		{
			if ( !TriangleHit( origin, dir, corners[ia], corners[ib], corners[ic], out var cand, out var p ) )
				continue;

			if ( cand >= bestT )
				continue;

			bestT = cand;
			bestP = p;
			hit = true;
		}

		if ( !hit )
			return false;

		t = bestT;
		point = bestP;
		return true;
	}

	/// <summary>
	/// The edge of this face nearest <paramref name="point"/>, and how far the point sits from
	/// that segment.
	///
	/// A click on a solid is a face hit first. Whether it was meant as an EDGE is a question of
	/// how close the hit landed to a boundary — the viewport compares this distance to a
	/// screen-pixel threshold, so a click in the middle of a face stays a face and a click near
	/// a corner becomes the edge.
	/// </summary>
	public static bool ClosestEdge( PolyMesh mesh, int faceIndex, Vec3 point, out EdgeKey key,
		out Vec3 closest, out float distance )
	{
		key = default;
		closest = default;
		distance = float.MaxValue;

		if ( mesh is null || faceIndex < 0 || faceIndex >= mesh.Faces.Count )
			return false;

		var face = mesh.Faces[faceIndex];

		if ( face.Count < 2 )
			return false;

		var found = false;

		for ( var i = 0; i < face.Count; i++ )
		{
			var a = mesh.Positions[face.Indices[i]];
			var b = mesh.Positions[face.Indices[(i + 1) % face.Count]];
			var ab = b - a;
			var lengthSq = ab.LengthSquared;

			if ( lengthSq < 1e-20f )
				continue;

			var t = Vec3.Dot( point - a, ab ) / lengthSq;

			if ( t < 0f )
				t = 0f;
			else if ( t > 1f )
				t = 1f;

			var on = a + ab * t;
			var d = (on - point).Length;

			if ( d >= distance )
				continue;

			distance = d;
			closest = on;
			key = new EdgeKey( face.Indices[i], face.Indices[(i + 1) % face.Count] );
			found = true;
		}

		return found;
	}

	/// <summary>
	/// Nearest hit across several bodies at once, with the winning body reported alongside it —
	/// what a click in a multi-body studio actually needs.
	/// </summary>
	/// <summary>
	/// The nearest face of any of <paramref name="bodies"/> that YOU CAN ACTUALLY SEE.
	///
	/// Effigy does not union bodies - two overlapping extrudes are two separate closed solids, and
	/// the faces of one that fall inside the other are still there, still hit by a ray, and quite
	/// invisible. Picking one is how you end up sketching on a plane buried inside your part,
	/// which is exactly as confusing as it sounds: the highlight paints a rectangle straight
	/// through the model and the sketch lands somewhere you never pointed at.
	///
	/// So a hit is discarded when the surface it landed on is inside another solid. Sorting the
	/// candidates first means the common case - nothing overlapping - costs one containment test.
	/// </summary>
	public static (Body Body, MeshHit Hit)? Raycast( IEnumerable<Body> bodies, Vec3 origin, Vec3 direction ) =>
		Raycast( bodies, origin, direction, null );

	/// <summary>
	/// The same pick, with the caller supplying a <see cref="MeshBVH"/> per mesh. See the note on
	/// <see cref="Raycast(PolyMesh, Vec3, Vec3, MeshBVH)"/> for why the tree is passed in rather
	/// than built here. Returning null from <paramref name="bvhFor"/> falls back to the scan, so a
	/// cache that has not warmed up yet is slow rather than wrong.
	/// </summary>
	public static (Body Body, MeshHit Hit)? Raycast( IEnumerable<Body> bodies, Vec3 origin, Vec3 direction,
		Func<PolyMesh, MeshBVH> bvhFor )
	{
		if ( bodies is null )
			return null;

		var list = new List<Body>();

		foreach ( var body in bodies )
		{
			if ( body?.Mesh is not null )
				list.Add( body );
		}

		var candidates = new List<(Body Body, MeshHit Hit)>( list.Count );

		foreach ( var body in list )
		{
			if ( Raycast( body.Mesh, origin, direction, bvhFor?.Invoke( body.Mesh ) ) is { } hit )
				candidates.Add( (body, hit) );
		}

		candidates.Sort( ( a, b ) => a.Hit.Distance.CompareTo( b.Hit.Distance ) );

		var dir = direction.Normal;

		foreach ( var candidate in candidates )
		{
			// Step back off the surface along the ray, so the test point is in the space the ray
			// travelled through rather than exactly on the boundary, where inside/outside is a
			// coin flip. Scaled by the distance travelled, since a sketch can be a unit across or
			// a thousand.
			var epsilon = 1e-4f * (1f + candidate.Hit.Distance);
			var probe = candidate.Hit.Point - dir * epsilon;
			var buried = false;

			foreach ( var other in list )
			{
				if ( ReferenceEquals( other, candidate.Body ) )
					continue;

				if ( PointInsideSolid( other.Mesh, probe ) )
				{
					buried = true;
					break;
				}
			}

			if ( !buried )
				return candidate;
		}

		return null;
	}

	/// <summary>
	/// The first run of material the ray passes through, or null when it passes through none.
	///
	/// THE FIRST RUN, NOT THE WHOLE SPREAD. Entry to the LAST hit would be the middle of everything
	/// the ray crosses, and on anything concave that is a point in mid-air — look between a model's
	/// two legs and the "middle" is the gap. Entry to the first exit is always inside material,
	/// which is the property worth having: a joint slightly off the medial line can be nudged, and a
	/// joint floating in space beside the model cannot be told from one that is correct.
	///
	/// The consequence to know about is a SHELLED model, where the first run is the thickness of the
	/// near wall rather than the hollow it encloses — so a bone lands inside that wall. Still inside
	/// the part, still better than on its skin, and the preview draws the run it measured so it is
	/// visible before the click rather than surprising afterwards.
	///
	/// Entry is the first face pointing back at the ray and exit the first one after it pointing
	/// away, rather than simply the first two hits. A mesh with a stray inward-facing face — which a
	/// boolean can leave behind — otherwise pairs an entry with an entry and reports a run that
	/// starts and ends on the same side of the material.
	///
	/// ONE MESH, deliberately. Across several bodies a run would enter the near one and leave the
	/// far one, putting its midpoint in the air between them — so the caller picks the body first
	/// (Raycast over the bodies already answers that, buried surfaces and all) and measures inside
	/// the one it names. Which is also the cheaper order: the caller doing the picking already has
	/// the hit, and this way nothing casts the same ray twice.
	/// </summary>
	public static SolidSpan? FirstSolidSpan( PolyMesh mesh, Vec3 origin, Vec3 direction )
	{
		var dir = direction.Normal;
		var hits = AllHits( mesh, origin, dir );
		var entry = -1;

		for ( var i = 0; i < hits.Count; i++ )
		{
			if ( Vec3.Dot( dir, hits[i].Normal ) < 0f )
			{
				entry = i;
				break;
			}
		}

		if ( entry < 0 )
			return null;

		for ( var i = entry + 1; i < hits.Count; i++ )
		{
			if ( Vec3.Dot( dir, hits[i].Normal ) > 0f )
				return new SolidSpan( hits[entry].Point, hits[i].Point );
		}

		return null;
	}

	/// <summary>
	/// Is a point inside a closed mesh? Crossing count along an arbitrary ray: odd is inside.
	///
	/// The direction is a fixed lopsided one rather than an axis, because an axis-aligned ray from
	/// a point on a box lands exactly along edges and coplanar faces, and every such ray is a
	/// coin-flip on whether a crossing gets counted once, twice or not at all.
	/// </summary>
	public static bool PointInsideSolid( PolyMesh mesh, Vec3 point )
	{
		if ( mesh is null || mesh.Faces.Count == 0 )
			return false;

		var direction = new Vec3( 0.5773f, 0.5771f, 0.5775f ).Normal;
		var crossings = 0;

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			var corners = new List<Vec3>( face.Count );

			for ( var c = 0; c < face.Count; c++ )
				corners.Add( mesh.Positions[face.Indices[c]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				if ( TriangleHit( point, direction, corners[ia], corners[ib], corners[ic], out _, out _ ) )
					crossings++;
			}
		}

		return (crossings & 1) == 1;
	}

	/// <summary>
	/// Möller–Trumbore. Returns the ray parameter and world point on a hit with t > 0; a
	/// back-facing triangle counts too, since a click through a thin wall should still register
	/// something rather than nothing.
	/// </summary>
	public static bool TriangleHit( Vec3 origin, Vec3 dir, Vec3 a, Vec3 b, Vec3 c, out float t, out Vec3 point )
	{
		t = 0f;
		point = default;

		const float eps = 1e-7f;

		var edge1 = b - a;
		var edge2 = c - a;
		var h = Vec3.Cross( dir, edge2 );
		var det = Vec3.Dot( edge1, h );

		if ( MathF.Abs( det ) < eps )
			return false;

		var invDet = 1f / det;
		var s = origin - a;
		var u = invDet * Vec3.Dot( s, h );

		if ( u < -eps || u > 1f + eps )
			return false;

		var q = Vec3.Cross( s, edge1 );
		var v = invDet * Vec3.Dot( dir, q );

		if ( v < -eps || u + v > 1f + eps )
			return false;

		var candidate = invDet * Vec3.Dot( edge2, q );

		if ( candidate <= eps )
			return false;

		t = candidate;
		point = origin + dir * t;
		return true;
	}
}
