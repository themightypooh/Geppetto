using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Per-vertex orthonormal basis plus a local length, derived from the mesh, never stored.
///
/// Deltas live in this frame so a sculpt rides a parametric edit: if they were world-space and
/// the cage grew 20% taller, every bump would sit where it used to and slide off the surface.
/// Normal is <see cref="PolyMesh.ComputeVertexNormals"/>. Tangent is the direction to the
/// lowest-indexed adjacent vertex, orthonormalised against the normal. The unused length of
/// that edge is Scale — capture and apply divide and multiply by it so a uniform resize keeps
/// the bump the same size relative to the cage.
/// </summary>
public readonly struct SculptFrame
{
	public readonly Vec3 Normal;
	public readonly Vec3 Tangent;
	public readonly Vec3 Bitangent;
	public readonly float Scale;

	public SculptFrame( Vec3 normal, Vec3 tangent, Vec3 bitangent, float scale )
	{
		Normal = normal;
		Tangent = tangent;
		Bitangent = bitangent;
		Scale = scale;
	}

	public Vec3 FromWorld( Vec3 world )
	{
		var s = Scale > 1e-12f ? Scale : 1f;
		return new Vec3(
			Vec3.Dot( world, Normal ) / s,
			Vec3.Dot( world, Tangent ) / s,
			Vec3.Dot( world, Bitangent ) / s );
	}

	public Vec3 ToWorld( Vec3 delta )
	{
		var s = Scale > 1e-12f ? Scale : 1f;
		return (Normal * delta.x + Tangent * delta.y + Bitangent * delta.z) * s;
	}
}

/// <summary>One frame per vertex, built from the mesh alone so it cannot drift out of sync.</summary>
public sealed class SculptFrames
{
	public readonly SculptFrame[] At;

	public SculptFrames( SculptFrame[] at )
	{
		At = at;
	}

	public int Count => At.Length;

	public static SculptFrames Build( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var normals = mesh.ComputeVertexNormals();
		var vertexEdges = mesh.BuildVertexEdges();
		var frames = new SculptFrame[mesh.VertexCount];

		for ( var vi = 0; vi < mesh.VertexCount; vi++ )
		{
			var n = normals[vi];

			if ( n.LengthSquared < 0.5f )
				n = new Vec3( 0, 0, 1 );

			var neighbour = LowestIndexedNeighbour( vi, vertexEdges[vi] );
			var toNeighbour = neighbour >= 0
				? mesh.Positions[neighbour] - mesh.Positions[vi]
				: Vec3.Zero;
			var scale = toNeighbour.Length;

			if ( scale < 1e-12f )
				scale = 1f;

			var t = OrthonormalTangent( n, toNeighbour, vi, vertexEdges[vi], mesh );
			var b = Vec3.Cross( n, t ).Normal;

			// Cross of two unit orthogonal vectors is already unit; if n and t somehow weren't,
			// rebuild t from n and b so the stored basis is orthonormal rather than "close".
			if ( b.LengthSquared < 0.5f )
			{
				t = FallbackTangent( n );
				b = Vec3.Cross( n, t ).Normal;
			}
			else
			{
				t = Vec3.Cross( b, n ).Normal;
			}

			frames[vi] = new SculptFrame( n, t, b, scale );
		}

		return new SculptFrames( frames );
	}

	static int LowestIndexedNeighbour( int vi, List<EdgeKey> edges )
	{
		var best = int.MaxValue;

		foreach ( var key in edges )
		{
			var other = key.A == vi ? key.B : key.A;

			if ( other < best )
				best = other;
		}

		return best == int.MaxValue ? -1 : best;
	}

	static Vec3 OrthonormalTangent( Vec3 n, Vec3 toNeighbour, int vi, List<EdgeKey> edges, PolyMesh mesh )
	{
		var t = (toNeighbour - n * Vec3.Dot( toNeighbour, n )).Normal;

		if ( t.LengthSquared >= 0.5f )
			return t;

		// The closest neighbour sat along the normal. Try the rest, lowest index first, so the
		// fallback is still a function of the mesh alone.
		var candidates = new List<int>( edges.Count );

		foreach ( var key in edges )
			candidates.Add( key.A == vi ? key.B : key.A );

		candidates.Sort();

		foreach ( var other in candidates )
		{
			var to = mesh.Positions[other] - mesh.Positions[vi];
			t = (to - n * Vec3.Dot( to, n )).Normal;

			if ( t.LengthSquared >= 0.5f )
				return t;
		}

		return FallbackTangent( n );
	}

	static Vec3 FallbackTangent( Vec3 n )
	{
		var ax = MathF.Abs( n.x );
		var ay = MathF.Abs( n.y );
		var az = MathF.Abs( n.z );
		var seed = ax < ay && ax < az ? new Vec3( 1, 0, 0 )
			: ay < az ? new Vec3( 0, 1, 0 )
			: new Vec3( 0, 0, 1 );
		var t = Vec3.Cross( n, seed ).Normal;
		return t.LengthSquared >= 0.5f ? t : new Vec3( 1, 0, 0 );
	}
}
