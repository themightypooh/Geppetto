using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Effigy;

/// <summary>
/// Wrinkle maps: creases that appear on the skin as a shape key squashes it, and smooth out as one
/// stretches it - s&amp;box's F_WRINKLE, on the skin and complex shaders.
///
/// HOW THE ENGINE DRIVES IT (realtime-flesh-shading.md §5, read from skinning_cs.shader): each morph
/// carries a per-vertex wrinkle value in −1…1; the compute skinning pass sums value × morph weight
/// into the vertex's wrinkle, and the material blends toward TextureSquish{Color,Normal,
/// AmbientOcclusion} where it is negative and TextureStretch{…} where it is positive, by its size.
/// Runtime Mesh.AddMorph takes only position and normal deltas, so the values have to come through
/// a compiled model: the DMX export (<see cref="DmxMorphs"/>).
///
/// WHERE THE VALUES COME FROM. <see cref="SkinStrain.StrainField"/> on the key: how much the skin
/// round each vertex grows or shrinks when the key is fully on. Squashed by
/// <see cref="FullStrain"/> or more is −1; stretched by as much is +1. A key that only moves a part
/// without changing its shape gives 0 everywhere, and nothing wrinkles.
///
/// THE TEXTURES. <see cref="Bake"/> lays folds onto the squish normal and AO maps where the keys
/// squash the skin, running across the squash (SkinStrain.Phase follows it), at
/// <see cref="Settings.Wavelength"/> apart. The stretch maps are flat skin: a stretched patch loses its
/// fine creases. Colour stays the material's own on both sides. <see cref="EnableInVmat"/> turns the
/// feature on in an Effigy-written vmat.
/// </summary>
public static class WrinkleMaps
{
	/// <summary>The strain that counts as a full wrinkle: 25% shorter (or longer) skin.</summary>
	public const float FullStrain = 0.25f;

	/// <summary>The default texture size of the squish maps.</summary>
	public const int DefaultSize = 1024;

	// --- per-vertex values -----------------------------------------------------------------------

	/// <summary>
	/// The key's wrinkle value per vertex: −1 squashed (by <paramref name="fullStrain"/> or more), +1
	/// stretched, 0 where the skin keeps its size. Whichever of squash or stretch is larger at a
	/// vertex decides it, so a sheared patch does not cancel to nothing.
	/// </summary>
	public static float[] VertexValues( PolyMesh mesh, MeshMorph morph, float fullStrain = FullStrain, int smooth = 1 ) =>
		Strain( mesh, morph, fullStrain, smooth ).Values;

	/// <summary>Every valid shape key's values, by name - what <see cref="DmxWriter"/>'s wrinkles takes.</summary>
	public static Dictionary<string, float[]> All( PolyMesh mesh, float fullStrain = FullStrain )
	{
		var all = new Dictionary<string, float[]>();

		foreach ( var morph in mesh.ValidMorphs )
			all[morph.Name] = VertexValues( mesh, morph, fullStrain );

		return all;
	}

	static (float[] Values, float[] Squash, Vec3[] SquashDir) Strain( PolyMesh mesh, MeshMorph morph, float fullStrain, int smooth )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( morph?.Deltas is null || morph.Deltas.Length != mesh.VertexCount )
			throw new ArgumentException( "the shape key does not fit the mesh", nameof( morph ) );

		var n = mesh.VertexCount;
		var nb = MeshLoops.Neighbours( n, mesh.Faces );
		var shaped = mesh.Positions.Select( ( p, i ) => p + morph.Deltas[i] ).ToList();
		var (stretch, _, squeeze, squeezeDir) = SkinStrain.StrainField( mesh.Faces, mesh.Positions, shaped, nb, smooth );
		var full = MathF.Max( fullStrain, 1e-4f );

		var values = new float[n];
		var squash = new float[n];

		for ( var v = 0; v < n; v++ )
		{
			// An isolated vertex has no edges: its stretch stays 0 and its squeeze 1, both "no change".
			var grew = MathF.Max( 0f, stretch[v] - 1f );
			var shrank = MathF.Max( 0f, 1f - squeeze[v] );
			squash[v] = Math.Clamp( shrank / full, 0f, 1f );
			values[v] = grew >= shrank ? Math.Clamp( grew / full, 0f, 1f ) : -squash[v];
		}

		return (values, squash, squeezeDir);
	}

	// --- the squish textures ---------------------------------------------------------------------

	public sealed class Settings
	{
		/// <summary>Size of the squish maps, square.</summary>
		public int Size = DefaultSize;

		/// <summary>Distance between folds, in model units. Zero: a fortieth of the model's size.</summary>
		public float Wavelength;

		/// <summary>Fold height as a share of the wavelength.</summary>
		public float Depth = 0.25f;

		/// <summary>How dark a fully squashed crease's valley is in the AO map (0 none, 1 black).</summary>
		public float Occlusion = 0.55f;

		/// <summary>Texels of padding round each island.</summary>
		public int Padding = 4;
	}

	/// <summary>A bake's result. Null maps when the mesh has no UVs (or UVs covering no area).</summary>
	public sealed class Result
	{
		public BakedMap SquishNormal;
		public BakedMap SquishAo;

		/// <summary>Texels any key squashes, out of the texels the UVs cover.</summary>
		public int Squashed, Covered;
	}

	/// <summary>
	/// The squish normal and AO maps for every shape key on <paramref name="mesh"/>, on its UVs: at
	/// each point, the key that squashes it most decides how deep its folds are and which way they
	/// run. The normal map is in the bake's tangent frame and green convention (NormalBake), so it
	/// reads like the model's own normal map.
	/// </summary>
	public static Result Bake( PolyMesh mesh, Settings settings = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		settings ??= new Settings();

		var size = Math.Max( 4, settings.Size );
		var result = new Result();

		if ( !mesh.Faces.Exists( f => f.UVs is not null && f.UVs.Length == f.Count && f.Count >= 3 ) )
			return result;

		var n = mesh.VertexCount;
		var squash = new float[n];
		var dir = new Vec3[n];

		foreach ( var morph in mesh.ValidMorphs )
		{
			var s = Strain( mesh, morph, FullStrain, 1 );

			for ( var v = 0; v < n; v++ )
			{
				if ( s.Squash[v] > squash[v] )
				{
					squash[v] = s.Squash[v];
					dir[v] = s.SquashDir[v];
				}
			}
		}

		var wavelength = settings.Wavelength > 0f ? settings.Wavelength : MathF.Max( mesh.BoundsDiagonal / 40f, 1e-3f );
		var mask = squash.Select( c => c > 0.02f ).ToArray();
		var phase = SkinStrain.Phase( mesh.Positions, dir, mask, MeshLoops.Neighbours( n, mesh.Faces ) );

		// Height of the folds per texel, in units of the fold depth, and which texels the UVs cover.
		var height = new float[size * size];
		var weight = new float[size * size];
		var covered = new bool[size * size];
		double area3 = 0, areaUV = 0;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			foreach ( var (a, b, c) in NormalBake.FaceTriangles( mesh, fi ) )
			{
				int ia = face.Indices[a], ib = face.Indices[b], ic = face.Indices[c];
				Vec2 ua = face.UVs[a], ub = face.UVs[b], uc = face.UVs[c];

				area3 += Vec3.Cross( mesh.Positions[ib] - mesh.Positions[ia], mesh.Positions[ic] - mesh.Positions[ia] ).Length * 0.5;
				areaUV += MathF.Abs( (ub.x - ua.x) * (uc.y - ua.y) - (uc.x - ua.x) * (ub.y - ua.y) ) * 0.5;

				NormalBake.Rasterise( ua, ub, uc, size, size, ( x, y, wa, wb, wc ) =>
				{
					var i = y * size + x;
					var c3 = squash[ia] * wa + squash[ib] * wb + squash[ic] * wc;
					var phi = phase[ia] * wa + phase[ib] * wb + phase[ic] * wc;
					covered[i] = true;

					if ( c3 <= 0f )
						return;

					height[i] = c3 * 0.5f * (1f - MathF.Cos( 2f * MathF.PI * phi / wavelength ));
					weight[i] = c3;
				} );
			}
		}

		// UVs that are all there but cover no area (every corner at 0,0) are no UVs at all.
		if ( areaUV < 1e-10 )
			return result;

		// One texel's width on the model, so the slope is in model units like the fold depth.
		var texel = (float)Math.Sqrt( area3 / areaUV ) / size;
		var depth = wavelength * settings.Depth;

		var normal = new BakedMap( size, size );
		var ao = new BakedMap( size, size );

		float H( int x, int y, int i ) =>
			x < 0 || y < 0 || x >= size || y >= size || !covered[y * size + x] ? height[i] : height[y * size + x];

		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				var i = y * size + x;
				var sx = (H( x + 1, y, i ) - H( x - 1, y, i )) * depth / (2f * texel);
				var sy = (H( x, y + 1, i ) - H( x, y - 1, i )) * depth / (2f * texel);

				// The bake frame: x along +u, y along +v; green written as −y like NormalBake.Encode.
				var nrm = new Vec3( -sx, -sy, 1f ).Normal;
				var o = i * 3;
				normal.Rgb[o] = Byte( nrm.x );
				normal.Rgb[o + 1] = Byte( -nrm.y );
				normal.Rgb[o + 2] = Byte( nrm.z );

				// The valley between two folds is where the light does not reach.
				var fold = weight[i] > 0f ? height[i] / weight[i] : 1f;
				var shade = (byte)Math.Clamp( MathF.Round( (1f - settings.Occlusion * weight[i] * (1f - fold)) * 255f ), 0f, 255f );
				ao.Rgb[o] = ao.Rgb[o + 1] = ao.Rgb[o + 2] = shade;

				normal.Filled[i] = ao.Filled[i] = covered[i];

				if ( covered[i] )
				{
					result.Covered++;
					if ( weight[i] > 0.02f ) result.Squashed++;
				}
			}
		}

		NormalBake.Dilate( normal, settings.Padding );
		NormalBake.Dilate( ao, settings.Padding );

		result.SquishNormal = normal;
		result.SquishAo = ao;
		return result;
	}

	static byte Byte( float v ) => (byte)Math.Clamp( MathF.Round( (v + 1f) * 127.5f ), 0f, 255f );

	// --- the material ----------------------------------------------------------------------------

	/// <summary>A flat tangent-space normal, as a constant texture: a stretched patch's smooth skin.</summary>
	public const string FlatNormal = "[0.501961 0.501961 1.000000 0.000000]";

	/// <summary>Whether <paramref name="vmat"/> already has wrinkle maps on.</summary>
	public static bool IsOn( string vmat ) => vmat?.Contains( "F_WRINKLE 1" ) == true;

	/// <summary>
	/// The vmat with F_WRINKLE on and its six textures set: the squish normal and AO given, flat
	/// skin for the stretch normal, the material's own AO for the stretch AO, and the material's own
	/// colour on both sides. Null when it is not Effigy's to change or its shader has no wrinkle
	/// feature (only skin and complex do); unchanged when it is already on.
	///
	/// The F_WRINKLE combo and the Texture{Squish,Stretch}{Color,Normal,AmbientOcclusion} names are
	/// from the skin shader's schema (realtime-flesh-shading.md §5); a written vmat has not yet been
	/// opened in the material editor, which is the open in-engine check.
	/// </summary>
	public static string EnableInVmat( string vmat, string squishNormal, string squishAo )
	{
		if ( vmat is null || !vmat.Contains( "AUTO-GENERATED" ) )
			return null;

		if ( IsOn( vmat ) )
			return vmat;

		var shader = vmat.IndexOf( "shader \"", StringComparison.Ordinal );

		if ( shader < 0 )
			return null;

		var lineEnd = vmat.IndexOf( '\n', shader );

		if ( lineEnd < 0 )
			return null;

		var shaderLine = vmat[shader..lineEnd];

		if ( !shaderLine.Contains( "skin.shader" ) && !shaderLine.Contains( "complex" ) )
			return null;

		var close = vmat.LastIndexOf( '}' );

		if ( close < lineEnd )
			return null;

		var nl = lineEnd > 0 && vmat[lineEnd - 1] == '\r' ? "\r\n" : "\n";
		var color = Value( vmat, "TextureColor" ) ?? "materials/default/default_color.tga";
		var occlusion = Value( vmat, "TextureAmbientOcclusion" ) ?? "[1.000000 1.000000 1.000000 0.000000]";

		var textures = $"{nl}\t//---- Wrinkle maps ----{nl}"
			+ $"\tTextureSquishColor \"{color}\"{nl}"
			+ $"\tTextureSquishNormal \"{squishNormal ?? FlatNormal}\"{nl}"
			+ $"\tTextureSquishAmbientOcclusion \"{squishAo ?? occlusion}\"{nl}"
			+ $"\tTextureStretchColor \"{color}\"{nl}"
			+ $"\tTextureStretchNormal \"{FlatNormal}\"{nl}"
			+ $"\tTextureStretchAmbientOcclusion \"{occlusion}\"{nl}";

		// Back to front, so the first insert does not move the second's position.
		var text = vmat.Insert( close, textures );
		return text.Insert( lineEnd + 1, $"{nl}\t//---- Wrinkle ----{nl}\tF_WRINKLE 1{nl}" );
	}

	static string Value( string vmat, string key )
	{
		var m = Regex.Match( vmat, $@"^\s*{key}\s+""([^""]*)""", RegexOptions.Multiline );
		return m.Success ? m.Groups[1].Value : null;
	}

	// --- one call for an export --------------------------------------------------------------------

	/// <summary>What <see cref="WriteForExport"/> did.</summary>
	public sealed class ExportResult
	{
		/// <summary>Per shape key, the per-vertex values for the DMX (empty when the mesh has no keys).</summary>
		public Dictionary<string, float[]> Values = new();
		public List<string> Written = new();
		public List<string> MaterialsOn = new();

		/// <summary>Plain-language notes on anything that did not happen, with what to do about it.</summary>
		public List<string> Notes = new();
	}

	/// <summary>
	/// Everything a skinned export needs for wrinkle maps: the per-vertex values, the squish maps as
	/// <c>{name}_squish_normal.png</c> / <c>{name}_squish_ao.png</c> in <paramref name="folder"/>, and
	/// F_WRINKLE on each Effigy material the mesh uses. <paramref name="materialPath"/> turns a slot
	/// into the vmat's path on disk, or null when it has none there.
	/// </summary>
	public static ExportResult WriteForExport( PolyMesh mesh, string folder, string assetFolder, string name,
		Func<int, string> materialPath, Settings settings = null )
	{
		var result = new ExportResult();

		if ( !mesh.HasMorphs )
		{
			result.Notes.Add( "Wrinkle maps follow shape keys, and this model has none. Add a shape key (View ▸ Shape Keys) that squashes or stretches the skin." );
			return result;
		}

		result.Values = All( mesh );

		if ( result.Values.Values.All( v => v.All( x => MathF.Abs( x ) < 0.02f ) ) )
			result.Notes.Add( "No shape key squashes or stretches the skin (they only move it), so nothing will wrinkle." );

		var baked = Bake( mesh, settings );
		string normalAsset = null, aoAsset = null;

		if ( baked.SquishNormal is null )
		{
			result.Notes.Add( "The model has no UVs, so the wrinkle textures could not be baked: unwrap it (UV Project) and export again. The per-vertex wrinkle data was still written." );
		}
		else
		{
			Directory.CreateDirectory( folder );
			var normalPath = Path.Combine( folder, $"{name}_squish_normal.png" );
			var aoPath = Path.Combine( folder, $"{name}_squish_ao.png" );
			PngWriter.WriteFile( normalPath, baked.SquishNormal );
			PngWriter.WriteFile( aoPath, baked.SquishAo );
			result.Written.Add( normalPath );
			result.Written.Add( aoPath );
			normalAsset = $"{assetFolder}/{name}_squish_normal.png";
			aoAsset = $"{assetFolder}/{name}_squish_ao.png";
		}

		foreach ( var slot in mesh.Faces.Select( f => f.Material ).Distinct().OrderBy( s => s ) )
		{
			var path = materialPath?.Invoke( slot );

			if ( path is null || !File.Exists( path ) )
			{
				result.Notes.Add( $"Material slot {slot} has no .vmat Effigy can find, so it shows no wrinkles. Give it an Effigy material (Flesh Material, a bake's Skin or Complex material) and export again." );
				continue;
			}

			var text = File.ReadAllText( path );
			var on = EnableInVmat( text, normalAsset, aoAsset );

			if ( on is null )
			{
				result.Notes.Add( $"{Path.GetFileName( path )} was not written by Effigy (or its shader has no wrinkle maps), so it was left alone. Set F_WRINKLE and the Squish/Stretch textures in it by hand, or use an Effigy skin or complex material." );
				continue;
			}

			if ( on != text )
			{
				File.WriteAllText( path, on );
				result.Written.Add( path );
			}

			result.MaterialsOn.Add( path );
		}

		return result;
	}
}
