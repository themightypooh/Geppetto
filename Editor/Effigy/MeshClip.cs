using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Keep the half of a mesh on one side of a plane, clipping faces that straddle it.
///
/// THIS IS A VIEW, NOT A FEATURE. Shell builds interiors nobody can look at; a cutaway hides the
/// near half while looking, without writing anything into the history. Faces that cross the plane
/// are clipped in place so the cut reads as a hard line rather than as missing polygons. There is
/// no cap — an open cut is the point of a section view, and capping would pretend the kernel had
/// booleaned a solid it has not.
/// </summary>
public static class MeshClip
{
	/// <summary>
	/// Everything on the positive side of the plane through <paramref name="origin"/> with
	/// <paramref name="normal"/>, including the plane itself. Vertices on the negative side are
	/// dropped; edges that cross emit a new vertex at the intersection.
	/// </summary>
	public static PolyMesh Keep( PolyMesh mesh, Vec3 origin, Vec3 normal, float tolerance = 1e-5f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var n = normal.Normal;

		if ( n.LengthSquared < 0.5f )
			return mesh.Clone();

		var result = new PolyMesh();

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			var clipped = ClipFace( mesh, face, origin, n, tolerance );

			if ( clipped.Count < 3 )
				continue;

			var indices = new int[clipped.Count];
			var uvs = new Vec2[clipped.Count];

			for ( var i = 0; i < clipped.Count; i++ )
			{
				indices[i] = result.AddVertex( clipped[i].Position );
				uvs[i] = clipped[i].Uv;
			}

			result.AddFace( indices, uvs, face.Material );
		}

		return result;
	}

	readonly struct ClipVert
	{
		public readonly Vec3 Position;
		public readonly Vec2 Uv;

		public ClipVert( Vec3 position, Vec2 uv )
		{
			Position = position;
			Uv = uv;
		}
	}

	static List<ClipVert> ClipFace( PolyMesh mesh, Face face, Vec3 origin, Vec3 normal, float tolerance )
	{
		var polygon = new List<ClipVert>( face.Count );

		for ( var i = 0; i < face.Count; i++ )
		{
			var uv = face.UVs is not null && i < face.UVs.Length ? face.UVs[i] : default;
			polygon.Add( new ClipVert( mesh.Positions[face.Indices[i]], uv ) );
		}

		// Sutherland–Hodgman against one plane: walk each edge, emit the intersection when the
		// edge crosses, and keep the vertex when it sits on the kept side.
		var output = new List<ClipVert>( polygon.Count + 2 );

		for ( var i = 0; i < polygon.Count; i++ )
		{
			var current = polygon[i];
			var previous = polygon[( i + polygon.Count - 1 ) % polygon.Count];
			var currentDist = Vec3.Dot( current.Position - origin, normal );
			var previousDist = Vec3.Dot( previous.Position - origin, normal );
			var currentIn = currentDist >= -tolerance;
			var previousIn = previousDist >= -tolerance;

			if ( currentIn )
			{
				if ( !previousIn )
					output.Add( Intersect( previous, current, previousDist, currentDist ) );

				output.Add( current );
			}
			else if ( previousIn )
			{
				output.Add( Intersect( previous, current, previousDist, currentDist ) );
			}
		}

		return output;
	}

	static ClipVert Intersect( ClipVert a, ClipVert b, float da, float db )
	{
		var denom = da - db;

		// Both on the plane, or an edge so close the numbers vanish: pick B rather than divide.
		var t = MathF.Abs( denom ) < 1e-12f ? 1f : da / denom;
		t = Math.Clamp( t, 0f, 1f );

		return new ClipVert( a.Position + ( b.Position - a.Position ) * t, a.Uv + ( b.Uv - a.Uv ) * t );
	}
}
