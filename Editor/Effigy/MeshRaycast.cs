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
/// Ray-mesh intersection, for clicking a face of a solid in the viewport.
///
/// PURE GEOMETRY, NO ENGINE SURFACE — which is why it lives here rather than in the editor. The
/// only thing the viewport contributes is the ray itself (Gizmo.CurrentRay, converted to Vec3);
/// everything about deciding which triangle it hit is ordinary math that can be built and proven
/// without s&box anywhere near it.
///
/// Faces are triangulated the same way EffigyPreview builds the render mesh — a fan from corner 0
/// — so a click hits exactly the triangle that would actually be drawn there. A different
/// triangulation would occasionally pick a face whose fan diagonal put the real geometry on the
/// other side of the click.
/// </summary>
public static class MeshRaycast
{
	/// <summary>
	/// The nearest face of <paramref name="mesh"/> that <paramref name="origin"/> + t *
	/// <paramref name="direction"/> hits, for t > 0. Null if nothing is hit.
	/// </summary>
	public static MeshHit? Raycast( PolyMesh mesh, Vec3 origin, Vec3 direction )
	{
		if ( mesh is null )
			return null;

		var dir = direction.Normal;

		MeshHit? best = null;

		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 )
				continue;

			var p0 = mesh.Positions[face.Indices[0]];

			for ( var c = 2; c < face.Count; c++ )
			{
				var p1 = mesh.Positions[face.Indices[c - 1]];
				var p2 = mesh.Positions[face.Indices[c]];

				if ( !TriangleHit( origin, dir, p0, p1, p2, out var t, out var point ) )
					continue;

				if ( best is { } current && t >= current.Distance )
					continue;

				var normal = mesh.FaceNormal( face );
				best = new MeshHit( point, fi, normal, t );
			}
		}

		return best;
	}

	/// <summary>
	/// Nearest hit across several bodies at once, with the winning body reported alongside it —
	/// what a click in a multi-body studio actually needs.
	/// </summary>
	public static (Body Body, MeshHit Hit)? Raycast( IEnumerable<Body> bodies, Vec3 origin, Vec3 direction )
	{
		(Body Body, MeshHit Hit)? best = null;

		if ( bodies is null )
			return null;

		foreach ( var body in bodies )
		{
			if ( body?.Mesh is null )
				continue;

			var hit = Raycast( body.Mesh, origin, direction );

			if ( hit is not { } h )
				continue;

			if ( best is { } current && h.Distance >= current.Hit.Distance )
				continue;

			best = (body, h);
		}

		return best;
	}

	/// <summary>
	/// Möller–Trumbore. Returns the ray parameter and world point on a hit with t > 0; a
	/// back-facing triangle counts too, since a click through a thin wall should still register
	/// something rather than nothing.
	/// </summary>
	static bool TriangleHit( Vec3 origin, Vec3 dir, Vec3 a, Vec3 b, Vec3 c, out float t, out Vec3 point )
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
