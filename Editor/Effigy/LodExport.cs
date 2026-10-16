using System;
using System.Collections.Generic;
using System.IO;

namespace Effigy;

/// <summary>
/// Levels of detail for an export: the decimated copies of a model, written beside it, and the
/// switch distances the .vmdl's LODGroupList uses. One place for the editor's File ▸ Export with
/// LODs and the script's <c>export lods=</c>, static and skinned alike.
///
/// WHAT EACH LEVEL KEEPS. <see cref="Decimate"/> carries the skin weights (blended along each
/// collapse), the shape keys and the vertex colours, holds open borders, material borders and UV
/// seams (<see cref="Decimate.Options.PreserveUVSeams"/>), so a level is the same model with fewer
/// triangles: the same bones pull it, the same morphs move it and its texture does not smear at the
/// islands' edges.
///
/// TRIANGULATED THE BAKE'S WAY. A level is triangles already, but the full-detail mesh is not, and
/// static levels go out through OBJ with <c>triangulate: true</c> (NormalBake.FaceTriangles) like the
/// editor's main static compile, so every level is shaded across the triangles its normal map was
/// baked on. Skinned levels go through DMX, which triangulates that way by default.
/// </summary>
public static class LodExport
{
	/// <summary>The editor's pair: half and a quarter of the triangles.</summary>
	public static readonly float[] DefaultRatios = { 0.5f, 0.25f };

	/// <summary>Below this many triangles a model's levels save next to nothing.</summary>
	public const int MinimumTriangles = 64;

	/// <summary>
	/// Where level <paramref name="level"/> (1 = the first decimated copy) takes over, in units:
	/// 6, then 15, then doubling, times the model's size - far enough that the coarser mesh is a few
	/// pixels' difference. The editor's distances, unchanged.
	/// </summary>
	public static float DefaultDistance( float diagonal, int level )
	{
		var size = MathF.Max( diagonal, 1f );
		return size * (level <= 1 ? 6f : level == 2 ? 15f : 15f * MathF.Pow( 2f, level - 2 ));
	}

	/// <summary>One written level.</summary>
	public sealed class Level
	{
		/// <summary>1 for the first decimated copy.</summary>
		public int Index;
		public float Ratio;
		public PolyMesh Mesh;
		public int Triangles;

		/// <summary>False when the decimation ran out of legal collapses before the ratio: the level
		/// is as small as it can get without breaking the mesh, and bigger than asked.</summary>
		public bool ReachedTarget;
		public float SwitchDistance;

		/// <summary>The files written for this level, on disk.</summary>
		public List<string> Files = new();

		/// <summary>The mesh file the .vmdl names, under the asset folder.</summary>
		public string AssetPath;
	}

	/// <summary>The decimated copy at <paramref name="ratio"/> of the triangles, seams held.</summary>
	public static Decimate.Result Reduce( PolyMesh mesh, float ratio ) =>
		Decimate.Run( mesh, new Decimate.Options { Ratio = ratio } );

	/// <summary>
	/// The levels as meshes, without writing anything. <paramref name="distances"/> may be null
	/// (the defaults) or one rising distance per ratio.
	/// </summary>
	public static List<Level> Build( PolyMesh mesh, IReadOnlyList<float> ratios, IReadOnlyList<float> distances = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		ratios ??= DefaultRatios;

		if ( distances is not null && distances.Count != ratios.Count )
			throw new ArgumentException( $"{distances.Count} distance(s) for {ratios.Count} level(s): give one per level", nameof( distances ) );

		var levels = new List<Level>();

		for ( var i = 0; i < ratios.Count; i++ )
		{
			var result = Reduce( mesh, ratios[i] );
			levels.Add( new Level
			{
				Index = i + 1,
				Ratio = ratios[i],
				Mesh = result.Mesh,
				Triangles = result.ToTriangles,
				ReachedTarget = result.ReachedTarget,
				SwitchDistance = distances?[i] ?? DefaultDistance( mesh.BoundsDiagonal, i + 1 ),
			} );
		}

		return levels;
	}

	/// <summary>
	/// Static levels: <c>{name}_lod1.obj</c>, <c>{name}_lod2.obj</c>… in <paramref name="dir"/>,
	/// triangulated the bake's way. <paramref name="assetFolder"/> is the folder's asset path
	/// (e.g. models/effigy), for the .vmdl.
	/// </summary>
	public static List<Level> WriteStatic( PolyMesh mesh, string dir, string assetFolder, string name,
		Func<int, string> materialName, IReadOnlyList<float> ratios = null, IReadOnlyList<float> distances = null )
	{
		var levels = Build( mesh, ratios, distances );

		foreach ( var level in levels )
		{
			var lodName = $"{name}_lod{level.Index}";
			var path = Path.Combine( dir, lodName + ".obj" );
			ObjWriter.WriteFile( level.Mesh, path, lodName, materialName: materialName, triangulate: true );
			level.Files.Add( path );
			level.AssetPath = $"{assetFolder}/{lodName}.obj";
		}

		return levels;
	}

	/// <summary>
	/// Skinned levels: <c>{name}_lod1.dmx</c>… (and an .smd beside each when <paramref name="smd"/>),
	/// every one bound to the same <paramref name="skeleton"/>, its weights the decimation's blend of
	/// the full mesh's and its shape keys carried along. The mesh must carry its skin
	/// (<see cref="PolyMesh.Skin"/>); a level without one would compile as a rigid model.
	/// <paramref name="wrinkles"/> writes each level's own wrinkle values (WrinkleMaps) with its keys.
	/// </summary>
	public static List<Level> WriteSkinned( PolyMesh mesh, Skeleton skeleton, string dir, string assetFolder, string name,
		Func<int, string> materialName, IReadOnlyList<float> ratios = null, IReadOnlyList<float> distances = null, bool smd = true,
		bool wrinkles = false )
	{
		if ( skeleton is null || skeleton.Count == 0 )
			throw new InvalidOperationException( "skinned levels of detail need the skeleton the model is bound to" );

		if ( !mesh.IsRigged )
			throw new InvalidOperationException( "skinned levels of detail need the mesh's skin weights: bind it before exporting" );

		var levels = Build( mesh, ratios, distances );

		foreach ( var level in levels )
		{
			var lodName = $"{name}_lod{level.Index}";
			var dmxPath = Path.Combine( dir, lodName + ".dmx" );
			DmxWriter.WriteFile( level.Mesh, dmxPath, skeleton, materialName: materialName, modelName: lodName,
				wrinkles: wrinkles && level.Mesh.HasMorphs ? WrinkleMaps.All( level.Mesh ) : null );
			level.Files.Add( dmxPath );

			if ( smd )
			{
				var smdPath = Path.Combine( dir, lodName + ".smd" );
				SmdWriter.WriteFile( level.Mesh, smdPath, skeleton, materialName: materialName );
				level.Files.Add( smdPath );
			}

			level.AssetPath = $"{assetFolder}/{lodName}.dmx";
		}

		return levels;
	}

	/// <summary>The .vmdl's mesh list and switch distances: the full model first, at zero.</summary>
	public static (List<string> Files, List<float> Distances) VmdlLists( string fullAssetPath, IEnumerable<Level> levels )
	{
		var files = new List<string> { fullAssetPath };
		var distances = new List<float> { 0f };

		foreach ( var level in levels )
		{
			files.Add( level.AssetPath );
			distances.Add( level.SwitchDistance );
		}

		return (files, distances);
	}

	/// <summary>A line saying what each level came to, for the log: "1840 tris from 210, 920 tris from 525".</summary>
	public static string Describe( IEnumerable<Level> levels )
	{
		var parts = new List<string>();

		foreach ( var level in levels )
		{
			var note = level.ReachedTarget ? "" : " (as far as it would go)";
			parts.Add( $"{level.Triangles} tris from {level.SwitchDistance.ToString( "0.#", System.Globalization.CultureInfo.InvariantCulture )}{note}" );
		}

		return string.Join( ", ", parts );
	}
}
