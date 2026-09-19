using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The UV layout as islands — the pieces an unwrap laid out flat — and what can be done to them
/// without unwrapping again: select one, slide it, turn it, size it, flip it, pack them all, and
/// see which faces the unwrap stretched. A UV editor's toolkit, on the corner UVs a
/// <see cref="PolyMesh"/> already carries.
///
/// AN ISLAND IS WHAT IS JOINED IN UV SPACE: two faces are on the same island when they share an
/// edge AND agree about the UVs at both its ends. A seam is exactly where two faces share an edge
/// but not its UVs, so this is the same rule the unwrap cut by, read back from its result.
/// </summary>
public static class UVIslands
{
	/// <summary>Island index per face, and the islands as face lists.</summary>
	public sealed class Layout
	{
		public int[] IslandOfFace;
		public List<List<int>> Islands = new();

		public int Count => Islands.Count;
	}

	/// <summary>Find the islands.</summary>
	public static Layout Find( PolyMesh mesh )
	{
		var layout = new Layout { IslandOfFace = new int[mesh.FaceCount] };
		Array.Fill( layout.IslandOfFace, -1 );

		// Corner UV by (face, vertex), so an edge's two sides can be compared.
		var corner = new Dictionary<(int face, int vertex), Vec2>();
		for ( var f = 0; f < mesh.FaceCount; f++ )
		{
			var face = mesh.Faces[f];
			for ( var i = 0; i < face.Indices.Length; i++ )
				corner[(f, face.Indices[i])] = face.UVs[i];
		}

		var edgeFaces = mesh.BuildEdgeFaces();
		var neighbours = new List<int>[mesh.FaceCount];
		foreach ( var (key, owners) in edgeFaces )
		{
			for ( var a = 0; a < owners.Count; a++ )
			{
				for ( var b = a + 1; b < owners.Count; b++ )
				{
					var fa = owners[a];
					var fb = owners[b];
					if ( !Same( corner[(fa, key.A)], corner[(fb, key.A)] ) || !Same( corner[(fa, key.B)], corner[(fb, key.B)] ) )
						continue;

					(neighbours[fa] ??= new List<int>()).Add( fb );
					(neighbours[fb] ??= new List<int>()).Add( fa );
				}
			}
		}

		var stack = new Stack<int>();
		for ( var f = 0; f < mesh.FaceCount; f++ )
		{
			if ( layout.IslandOfFace[f] >= 0 )
				continue;

			var island = new List<int>();
			var id = layout.Islands.Count;
			layout.Islands.Add( island );
			stack.Push( f );
			layout.IslandOfFace[f] = id;

			while ( stack.Count > 0 )
			{
				var at = stack.Pop();
				island.Add( at );
				if ( neighbours[at] is null )
					continue;

				foreach ( var n in neighbours[at] )
				{
					if ( layout.IslandOfFace[n] >= 0 )
						continue;

					layout.IslandOfFace[n] = id;
					stack.Push( n );
				}
			}
		}

		return layout;
	}

	static bool Same( Vec2 a, Vec2 b ) => MathF.Abs( a.x - b.x ) < 1e-5f && MathF.Abs( a.y - b.y ) < 1e-5f;

	/// <summary>The UV bounds of a set of faces: min and max corner.</summary>
	public static (Vec2 Min, Vec2 Max) Bounds( PolyMesh mesh, IEnumerable<int> faces )
	{
		var min = new Vec2( float.MaxValue, float.MaxValue );
		var max = new Vec2( float.MinValue, float.MinValue );
		foreach ( var f in faces )
		{
			foreach ( var uv in mesh.Faces[f].UVs )
			{
				min = new Vec2( MathF.Min( min.x, uv.x ), MathF.Min( min.y, uv.y ) );
				max = new Vec2( MathF.Max( max.x, uv.x ), MathF.Max( max.y, uv.y ) );
			}
		}

		return min.x > max.x ? (Vec2.Zero, Vec2.Zero) : (min, max);
	}

	/// <summary>Move the faces' UVs by <paramref name="offset"/>.</summary>
	public static void Translate( PolyMesh mesh, IEnumerable<int> faces, Vec2 offset )
	{
		foreach ( var f in faces )
		{
			var uvs = mesh.Faces[f].UVs;
			for ( var i = 0; i < uvs.Length; i++ )
				uvs[i] = uvs[i] + offset;
		}
	}

	/// <summary>Turn the faces' UVs by <paramref name="degrees"/> about <paramref name="pivot"/>
	/// (the bounds' centre when null).</summary>
	public static void Rotate( PolyMesh mesh, IReadOnlyCollection<int> faces, float degrees, Vec2? pivot = null )
	{
		var c = pivot ?? Centre( mesh, faces );
		var r = degrees * MathF.PI / 180f;
		var cos = MathF.Cos( r );
		var sin = MathF.Sin( r );
		foreach ( var f in faces )
		{
			var uvs = mesh.Faces[f].UVs;
			for ( var i = 0; i < uvs.Length; i++ )
			{
				var d = uvs[i] - c;
				uvs[i] = new Vec2( c.x + d.x * cos - d.y * sin, c.y + d.x * sin + d.y * cos );
			}
		}
	}

	/// <summary>Scale the faces' UVs about <paramref name="pivot"/> (the bounds' centre when null).
	/// A negative factor on one axis is a flip.</summary>
	public static void Scale( PolyMesh mesh, IReadOnlyCollection<int> faces, Vec2 factor, Vec2? pivot = null )
	{
		var c = pivot ?? Centre( mesh, faces );
		foreach ( var f in faces )
		{
			var uvs = mesh.Faces[f].UVs;
			for ( var i = 0; i < uvs.Length; i++ )
				uvs[i] = new Vec2( c.x + (uvs[i].x - c.x) * factor.x, c.y + (uvs[i].y - c.y) * factor.y );
		}
	}

	public static Vec2 Centre( PolyMesh mesh, IEnumerable<int> faces )
	{
		var (min, max) = Bounds( mesh, faces );
		return (min + max) * 0.5f;
	}

	/// <summary>
	/// Lay every island into the unit square, all at one scale — the largest that fits, found by
	/// halving — with <paramref name="margin"/> between them (as a fraction of the square). Islands
	/// are laid tallest first in rows, and a tall island is turned on its side if that packs
	/// tighter. Returns the scale used, so a caller can say how much texture the layout gets.
	/// </summary>
	public static float Pack( PolyMesh mesh, float margin = 0.01f, Layout layout = null )
	{
		layout ??= Find( mesh );
		if ( layout.Count == 0 )
			return 1f;

		var boxes = new List<(int island, Vec2 min, Vec2 size)>();
		foreach ( var island in layout.Islands )
		{
			var (min, max) = Bounds( mesh, island );
			boxes.Add( (layout.Islands.IndexOf( island ), min, max - min) );
		}

		float Best( bool turnTall )
		{
			var lo = 0f;
			var hi = 1f;
			// Grow until it no longer fits, then halve in.
			while ( TryPack( boxes, hi, margin, turnTall, null ) && hi < 1e4f )
				hi *= 2f;
			for ( var i = 0; i < 24; i++ )
			{
				var mid = (lo + hi) * 0.5f;
				if ( TryPack( boxes, mid, margin, turnTall, null ) )
					lo = mid;
				else
					hi = mid;
			}

			return lo;
		}

		var straight = Best( false );
		var turned = Best( true );
		var turn = turned > straight;
		var scale = turn ? turned : straight;
		var placed = new Dictionary<int, (Vec2 at, bool turned)>();
		TryPack( boxes, scale, margin, turn, placed );

		foreach ( var (island, min, size) in boxes )
		{
			var faces = layout.Islands[island];
			var (at, wasTurned) = placed[island];
			foreach ( var f in faces )
			{
				var uvs = mesh.Faces[f].UVs;
				for ( var i = 0; i < uvs.Length; i++ )
				{
					var local = (uvs[i] - min) * scale;
					if ( wasTurned )
						local = new Vec2( local.y, local.x );
					uvs[i] = at + local;
				}
			}
		}

		return scale;
	}

	/// <summary>Shelf-pack at one scale. With <paramref name="placed"/> given, records where each
	/// island landed.</summary>
	static bool TryPack( List<(int island, Vec2 min, Vec2 size)> boxes, float scale, float margin, bool turnTall, Dictionary<int, (Vec2, bool)> placed )
	{
		var order = new List<(int island, Vec2 size, bool turned)>();
		foreach ( var (island, _, size) in boxes )
		{
			var s = size * scale;
			var turned = turnTall && s.y > s.x;
			order.Add( (island, turned ? new Vec2( s.y, s.x ) : s, turned) );
		}

		order.Sort( ( a, b ) => b.size.y.CompareTo( a.size.y ) );

		var x = margin;
		var y = margin;
		var rowHeight = 0f;
		foreach ( var (island, size, turned) in order )
		{
			if ( size.x + 2f * margin > 1f || size.y + 2f * margin > 1f )
				return false;

			if ( x + size.x + margin > 1f )
			{
				x = margin;
				y += rowHeight + margin;
				rowHeight = 0f;
			}

			if ( y + size.y + margin > 1f )
				return false;

			placed?.TryAdd( island, (new Vec2( x, y ), turned) );
			x += size.x + margin;
			rowHeight = MathF.Max( rowHeight, size.y );
		}

		return true;
	}

	/// <summary>
	/// How stretched each face's texture is: 1 is the mesh's average texel density, 2 means the
	/// face gets twice the texture per unit of surface (its texels are squashed), 0.5 half (they
	/// are stretched). Faces with no area report 1. What a UV view colours.
	/// </summary>
	public static float[] Stretch( PolyMesh mesh )
	{
		var result = new float[mesh.FaceCount];
		var ratios = new float[mesh.FaceCount];
		var total3 = 0f;
		var totalUV = 0f;
		for ( var f = 0; f < mesh.FaceCount; f++ )
		{
			var face = mesh.Faces[f];
			var area3 = mesh.FaceArea( face );
			var areaUV = 0f;
			for ( var i = 1; i + 1 < face.UVs.Length; i++ )
			{
				var a = face.UVs[0];
				var b = face.UVs[i];
				var c = face.UVs[i + 1];
				areaUV += MathF.Abs( (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y) ) * 0.5f;
			}

			ratios[f] = area3 > 1e-12f ? areaUV / area3 : -1f;
			total3 += area3;
			totalUV += areaUV;
		}

		var mean = total3 > 1e-12f ? totalUV / total3 : 1f;
		for ( var f = 0; f < mesh.FaceCount; f++ )
			result[f] = ratios[f] < 0f || mean <= 1e-12f ? 1f : ratios[f] / mean;

		return result;
	}
}
