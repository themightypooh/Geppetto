using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Marked UV seams (<see cref="PolyMesh.Seams"/>) carried through the operations that renumber a
/// mesh, and the seams a layout already HAS read back from its UVs.
///
/// A seam is a pair of vertex indices, so anything that rebuilds the vertex list has to say where
/// each marked edge went or the mark lands on whatever edge inherits the numbers. Subdivision is
/// the one the live modifiers need: Catmull-Clark keeps the original vertices at their indices and
/// puts edge point i (edges sorted by (A, B)) at V + i, so a seam a-b becomes a-e and e-b.
/// </summary>
public static class UVSeams
{
	/// <summary>The seams of <paramref name="before"/> after one level of Catmull-Clark: each
	/// marked edge split at its edge point. Keys that are not edges of <paramref name="before"/>
	/// are dropped.</summary>
	public static HashSet<EdgeKey> Subdivided( PolyMesh before, IEnumerable<EdgeKey> seams )
	{
		var result = new HashSet<EdgeKey>();
		if ( before is null || seams is null )
			return result;

		// The same order CatmullClark.SubdivideOnce gives its edge points.
		var edges = new List<EdgeKey>( before.BuildEdgeFaces().Keys );
		edges.Sort( ( a, b ) =>
		{
			var cmp = a.A.CompareTo( b.A );
			return cmp != 0 ? cmp : a.B.CompareTo( b.B );
		} );

		var index = new Dictionary<EdgeKey, int>( edges.Count );
		for ( var i = 0; i < edges.Count; i++ )
			index[edges[i]] = i;

		var v = before.VertexCount;
		foreach ( var key in seams )
		{
			if ( !index.TryGetValue( key, out var e ) )
				continue;

			result.Add( new EdgeKey( key.A, v + e ) );
			result.Add( new EdgeKey( v + e, key.B ) );
		}

		return result;
	}

	/// <summary>
	/// Every edge where the two faces on it disagree about the UVs at either end - where the layout
	/// is already cut, whether a seam was marked there or the unwrap chose it. Boundary edges are
	/// not included: nothing is on the other side to disagree.
	/// </summary>
	public static HashSet<EdgeKey> FromLayout( PolyMesh mesh, IReadOnlyCollection<int> faces = null )
	{
		var result = new HashSet<EdgeKey>();
		if ( mesh is null )
			return result;

		HashSet<int> within = faces is null ? null : new HashSet<int>( faces );

		foreach ( var (key, owners) in mesh.BuildEdgeFaces() )
		{
			for ( var a = 0; a < owners.Count; a++ )
			{
				for ( var b = a + 1; b < owners.Count; b++ )
				{
					if ( within is not null && (!within.Contains( owners[a] ) || !within.Contains( owners[b] )) )
						continue;

					if ( !Agree( mesh, owners[a], owners[b], key ) )
						result.Add( key );
				}
			}
		}

		return result;
	}

	/// <summary>Whether two faces sharing <paramref name="key"/> give both its ends the same UV.</summary>
	public static bool Agree( PolyMesh mesh, int fa, int fb, EdgeKey key )
	{
		return Same( CornerUV( mesh.Faces[fa], key.A ), CornerUV( mesh.Faces[fb], key.A ) )
			&& Same( CornerUV( mesh.Faces[fa], key.B ), CornerUV( mesh.Faces[fb], key.B ) );
	}

	static Vec2 CornerUV( Face face, int vertex )
	{
		for ( var i = 0; i < face.Count; i++ )
			if ( face.Indices[i] == vertex )
				return face.UVs[i];

		return new Vec2( float.NaN, float.NaN );
	}

	static bool Same( Vec2 a, Vec2 b ) => MathF.Abs( a.x - b.x ) < 1e-5f && MathF.Abs( a.y - b.y ) < 1e-5f;
}
