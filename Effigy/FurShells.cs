using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Fur the way s&box draws it: stacked shells for <c>shaders/fur.shader</c>.
///
/// THE SHADER GROWS NOTHING. It is a shell shader (sbox/core/shaders/fur.shader): the mesh must
/// already hold the copies of the surface, stepped outward, and each copy's mesh vertex colour RED
/// says how high it is - 0 at the root layer, 1 at the tips. The pixel shader keeps a pixel where the
/// strand noise is above that height, so low shells are nearly solid and high ones are only the
/// strand tips; the vertex shader sways a vertex by red times the wind, so the roots stay put. This
/// is what Facepunch's own cardigan, Santa and teddy-bear clothing ships.
///
/// RED RIDES THE EXPORT ALREADY. <see cref="PolyMesh.VertexColors"/> goes out through
/// <see cref="Vec4.Tint"/>, which maps (h, 1, 1, 1) to a colour of (h, 1, 1) - red exactly h, as bytes
/// (see DmxText.Color for why bytes).
/// </summary>
public static class FurShells
{
	public sealed class Options
	{
		public float Length = 1f;
		public int Layers = 12;

		/// <summary>0..1 - how much gravity bends the tips down.</summary>
		public float Droop = 0.3f;

		public int MaterialSlot;
	}

	/// <summary>
	/// Add shells over <paramref name="faces"/> of <paramref name="mesh"/>, in place. Returns how many
	/// faces were added.
	/// </summary>
	public static int Grow( PolyMesh mesh, IReadOnlyCollection<int> faces, Options o )
	{
		if ( faces.Count == 0 )
			return 0;

		var layers = Math.Clamp( o.Layers, 2, 64 );
		var normals = mesh.ComputeVertexNormals();
		var baseCount = mesh.VertexCount;

		// Everything already there keeps whatever colour it had; untouched vertices read as "no colour"
		// (w = 0), which Tint turns into white, which the Complex shader ignores anyway.
		var colors = new List<Vec4>( mesh.HasVertexColors ? mesh.VertexColors : new Vec4[baseCount] );

		var used = new SortedSet<int>();

		foreach ( var fi in faces )
		{
			foreach ( var vi in mesh.Faces[fi].Indices )
				used.Add( vi );
		}

		var sourceFaces = faces.Select( fi => mesh.Faces[fi] ).ToList();
		var added = 0;

		for ( var layer = 0; layer < layers; layer++ )
		{
			var h = layer / (float)(layers - 1);

			// The root layer lifts a hair off the cloth so it does not fight it for depth.
			var lift = o.Length * (0.03f + 0.97f * h);
			var sag = o.Length * Math.Clamp( o.Droop, 0f, 1f ) * h * h;
			var map = new Dictionary<int, int>( used.Count );

			foreach ( var vi in used )
			{
				var p = mesh.Positions[vi] + normals[vi] * lift + new Vec3( 0, 0, -sag );
				map[vi] = mesh.AddVertex( p );
				colors.Add( new Vec4( h, 1f, 1f, 1f ) );

				if ( mesh.Skin is not null && vi < mesh.Skin.Count )
					mesh.Skin.Vertices.Add( (BoneWeight[])mesh.Skin[vi].Clone() );
			}

			foreach ( var f in sourceFaces )
			{
				var indices = f.Indices.Select( vi => map[vi] ).ToArray();
				mesh.AddFace( indices, (Vec2[])f.UVs?.Clone(), o.MaterialSlot );
				added++;
			}
		}

		mesh.VertexColors = colors.ToArray();
		return added;
	}

	/// <summary>Faces within <paramref name="width"/> of an opening - a collar, a cuff, a hem.</summary>
	public static List<int> TrimFaces( PolyMesh mesh, float width )
	{
		var boundary = GarmentFit.BoundaryVertices( mesh );
		var weights = GarmentFit.FlareWeights( mesh, boundary, width );
		var faces = new List<int>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			if ( mesh.Faces[fi].Indices.Any( vi => weights[vi] > 0f ) )
				faces.Add( fi );
		}

		return faces;
	}

	/// <summary>
	/// The outward-facing faces of a solidified garment: the ones whose normal points away from the
	/// garment's middle. Fur on the inside wall of a sleeve would be invisible and double the cost.
	/// </summary>
	public static List<int> OutwardFaces( PolyMesh mesh, IEnumerable<int> faces )
	{
		var centre = Vec3.Zero;

		foreach ( var p in mesh.Positions )
			centre += p;

		centre /= Math.Max( 1, mesh.VertexCount );

		return faces.Where( fi =>
		{
			var f = mesh.Faces[fi];
			var c = mesh.FaceCentroid( f );
			var axis = new Vec3( c.x - centre.x, c.y - centre.y, 0 );
			return Vec3.Dot( mesh.FaceNormal( f ), axis ) >= 0 || axis.LengthSquared < 1e-6f;
		} ).ToList();
	}
}

/// <summary>
/// The textures and .vmat for a fur feature. Text and bytes only, so the headless suite can judge it;
/// the editor writes the files and binds the slot.
/// </summary>
public static class FurMaterial
{
	public const int NoiseSize = 256;

	/// <summary>
	/// Strand noise: mostly dark with bright specks, a few pixels across, so the tip shells keep thin
	/// strands. <paramref name="clump"/> 0..1 groups neighbouring strands into tufts.
	/// </summary>
	public static byte[] NoiseRgba( int seed, float clump )
	{
		var size = NoiseSize;
		var rgba = new byte[size * size * 4];
		clump = Math.Clamp( clump, 0f, 1f );

		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				var strand = Hash( x, y, seed );
				var tuft = Value( x / 16f, y / 16f, seed + 7 );
				var v = strand * (1f - clump) + strand * tuft * clump * 1.6f;
				var b = (byte)Math.Clamp( (int)(v * 255f), 0, 255 );
				var o = (y * size + x) * 4;
				rgba[o] = b;
				rgba[o + 1] = b;
				rgba[o + 2] = b;
				rgba[o + 3] = 255;
			}
		}

		return rgba;
	}

	/// <summary>A flat colour swatch for the fur's base colour.</summary>
	public static byte[] ColorRgba( Vec3 color, int size = 16 )
	{
		var rgba = new byte[size * size * 4];

		for ( var i = 0; i < size * size; i++ )
		{
			rgba[i * 4] = Byte( color.x );
			rgba[i * 4 + 1] = Byte( color.y );
			rgba[i * 4 + 2] = Byte( color.z );
			rgba[i * 4 + 3] = 255;
		}

		return rgba;
	}

	public static string VmatSource( string colorPng, string noisePng, float density, float darkRoots,
		Vec3 rim, float wind )
	{
		static string F( float v ) => v.ToString( "0.000", CultureInfo.InvariantCulture );

		return
			"// THIS FILE IS AUTO-GENERATED by an Effigy Fur feature. Edit the feature, not this file.\n" +
			"Layer0\n" +
			"{\n" +
			"\tshader \"shaders/fur.shader\"\n" +
			"\tF_NEW_TEXTURE_PACKING 1\n" +
			$"\tTextureBaseColor \"{colorPng}\"\n" +
			$"\tTextureFurNoise \"{noisePng}\"\n" +
			$"\tg_flNoiseTiling \"{F( density )}\"\n" +
			$"\tg_flNoiseAlbedoMultiply \"{F( darkRoots )}\"\n" +
			$"\tg_flNoiseAOAmount \"{F( darkRoots * 0.6f )}\"\n" +
			"\tg_flMinClipFudge \"0.010\"\n" +
			$"\tg_vRimColour \"[{F( rim.x )} {F( rim.y )} {F( rim.z )}]\"\n" +
			"\tg_flRimPower \"2.000\"\n" +
			"\tg_flRimFudge \"0.300\"\n" +
			$"\tg_flWind \"{F( wind )}\"\n" +
			"\tg_flWindFreq \"0.360\"\n" +
			"\tg_flWindNoise \"0.360\"\n" +
			"}\n";
	}

	/// <summary>"#rrggbb" to 0..1 colour, or the fallback when it does not parse.</summary>
	public static Vec3 ParseHex( string hex, Vec3 fallback )
	{
		hex = (hex ?? "").Trim().TrimStart( '#' );

		if ( hex.Length != 6 || !int.TryParse( hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v ) )
			return fallback;

		return new Vec3( ((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f );
	}

	static byte Byte( float v ) => (byte)Math.Clamp( (int)MathF.Round( v * 255f ), 0, 255 );

	static float Hash( int x, int y, int seed )
	{
		unchecked
		{
			var h = (uint)(x * 374761393 + y * 668265263 + seed * 144665);
			h = (h ^ (h >> 13)) * 1274126177u;
			h ^= h >> 16;
			return (h & 0xFFFFFF) / (float)0xFFFFFF;
		}
	}

	static float Value( float x, float y, int seed )
	{
		var ix = (int)MathF.Floor( x );
		var iy = (int)MathF.Floor( y );
		var fx = x - ix;
		var fy = y - iy;
		fx = fx * fx * (3 - 2 * fx);
		fy = fy * fy * (3 - 2 * fy);
		var a = Hash( ix & 15, iy & 15, seed );
		var b = Hash( (ix + 1) & 15, iy & 15, seed );
		var c = Hash( ix & 15, (iy + 1) & 15, seed );
		var d = Hash( (ix + 1) & 15, (iy + 1) & 15, seed );
		return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
	}
}
