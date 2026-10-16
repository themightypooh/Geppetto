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

	/// <summary>
	/// How far a layout bends the surface: the area-weighted mean and the worst triangle, over
	/// <paramref name="faces"/> (all faces when null). 1 is perfect; see <see cref="TriangleStretch"/>
	/// for what one number means.
	///
	/// WHY NOT <see cref="Stretch"/>. That compares AREAS, so it cannot see the failure a planar
	/// projection actually makes: a face tilted away from the projection plane is squashed in one
	/// direction only, and a squashed face can keep almost exactly its share of area while every
	/// texel on it is a thin sliver. This looks at the two directions separately, which is the
	/// only way to see shear and squash at all.
	///
	/// Density is the layout's OWN average (total UV area over total surface area), so a layout
	/// is not punished for the scale it was packed at - only for being uneven.
	/// </summary>
	public static (float Mean, float Max) Distortion( PolyMesh mesh, IEnumerable<int> faces = null )
	{
		var list = new List<int>();

		if ( faces is null )
		{
			for ( var f = 0; f < mesh.FaceCount; f++ )
				list.Add( f );
		}
		else
		{
			list.AddRange( faces );
		}

		var area3 = 0.0;
		var areaUV = 0.0;

		foreach ( var f in list )
		{
			var face = mesh.Faces[f];

			if ( face.UVs is null || face.UVs.Length != face.Count )
				continue;

			for ( var i = 1; i + 1 < face.Count; i++ )
			{
				var p0 = mesh.Positions[face.Indices[0]];
				area3 += 0.5 * Vec3.Cross( mesh.Positions[face.Indices[i]] - p0, mesh.Positions[face.Indices[i + 1]] - p0 ).Length;
				areaUV += MathF.Abs( Vec2.Cross( face.UVs[i] - face.UVs[0], face.UVs[i + 1] - face.UVs[0] ) ) * 0.5;
			}
		}

		if ( area3 <= 0 || areaUV <= 0 )
			return (0f, 0f);

		var density = Math.Sqrt( areaUV / area3 );
		var sum = 0.0;
		var weight = 0.0;
		var worst = 0.0;

		foreach ( var f in list )
		{
			var face = mesh.Faces[f];

			if ( face.UVs is null || face.UVs.Length != face.Count )
				continue;

			for ( var i = 1; i + 1 < face.Count; i++ )
			{
				var p0 = mesh.Positions[face.Indices[0]];
				var p1 = mesh.Positions[face.Indices[i]];
				var p2 = mesh.Positions[face.Indices[i + 1]];
				var s = TriangleStretch( p0, p1, p2, face.UVs[0], face.UVs[i], face.UVs[i + 1], density );

				if ( s < 0 )
					continue;

				var a = 0.5 * Vec3.Cross( p1 - p0, p2 - p0 ).Length;
				sum += s * a;
				weight += a;
				worst = Math.Max( worst, s );
			}
		}

		return weight > 0 ? ((float)(sum / weight), (float)worst) : (0f, 0f);
	}

	/// <summary>
	/// One triangle's stretch: the singular values of the map from the surface to UV space give
	/// how many UV units one unit of surface becomes in the most- and least-stretched directions.
	/// Divided by the layout's density, a perfect triangle has both at 1. The number is the worse of
	/// the two ways it can go wrong - too many texels in some direction (largest over density) or
	/// too few (density over smallest) - so 2 means some direction across the triangle gets half or
	/// double what it should. Capped at 1000 for a triangle the layout collapsed to a line.
	///
	/// Returns -1 for a triangle with no surface area, which has no directions to stretch.
	/// </summary>
	internal static float TriangleStretch( Vec3 p0, Vec3 p1, Vec3 p2, Vec2 t0, Vec2 t1, Vec2 t2, double density )
	{
		var e1 = p1 - p0;
		var e2 = p2 - p0;
		var n = Vec3.Cross( e1, e2 );

		if ( n.LengthSquared < 1e-24f || e1.LengthSquared < 1e-24f )
			return -1f;

		var x = e1.Normal;
		var y = Vec3.Cross( n.Normal, x );

		double q1x = e1.Length, q2x = Vec3.Dot( e2, x ), q2y = Vec3.Dot( e2, y );

		if ( q2y <= 1e-12 )
			return -1f;

		double u1x = t1.x - t0.x, u1y = t1.y - t0.y, u2x = t2.x - t0.x, u2y = t2.y - t0.y;

		// J = [t1 - t0, t2 - t0] * inverse([q1, q2]).
		var j1x = u1x / q1x;
		var j1y = u1y / q1x;
		var j2x = -u1x * q2x / (q1x * q2y) + u2x / q2y;
		var j2y = -u1y * q2x / (q1x * q2y) + u2y / q2y;

		var e = j1x * j1x + j1y * j1y + j2x * j2x + j2y * j2y;
		var det = Math.Abs( j1x * j2y - j2x * j1y );
		var s1 = Math.Sqrt( (e + Math.Sqrt( Math.Max( 0, e * e - 4 * det * det ) )) * 0.5 );
		var s2 = s1 > 0 ? det / s1 : 0;

		if ( s2 <= 1e-12 * density )
			return 1000f;

		return (float)Math.Min( 1000.0, Math.Max( s1 / density, density / s2 ) );
	}
}
