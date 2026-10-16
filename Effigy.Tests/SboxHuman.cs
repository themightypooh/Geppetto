using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The s&amp;box human (citizen_human_male / _female), read from the source FBX the citizen addon
/// ships, as a wearer: one welded skin, its real skeleton and its real skin weights, in engine
/// model space. What the headless clothing runs dress, instead of a body made up from bones.
///
/// ENGINE SPACE. The FBX is Y-up, Z-forward, in centimetres; s&amp;box is Z-up, X-forward, Y-left,
/// in inches. The axes are a rotation of each other (x' = z, y' = x, z' = y - no mirror), and the
/// height comes out at 68.9 in for the male, which is the size the model is in the engine.
///
/// THE BODY ONLY. Eyes and mouth are separate shells inside the head and the underwear is cloth;
/// head, chest, hands, legs and feet are the skin, and are welded where the meshes meet.
/// </summary>
public static class SboxHuman
{
	public const string MaleRelative = "addons/citizen/Assets/models/citizen_human/citizen_human_male_REF.fbx";
	public const string FemaleRelative = "addons/citizen/Assets/models/citizen_human/citizen_human_female_REF.fbx";

	/// <summary>The s&amp;box install: $SBOX_PATH, or the usual Steam library places.</summary>
	public static string Find( bool female = false )
	{
		var relative = female ? FemaleRelative : MaleRelative;
		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		var roots = new[]
		{
			Environment.GetEnvironmentVariable( "SBOX_PATH" ),
			Path.Combine( home, "games/steamlibrary/steamapps/common/sbox" ),
			Path.Combine( home, ".local/share/Steam/steamapps/common/sbox" ),
			Path.Combine( home, ".steam/steam/steamapps/common/sbox" ),
			@"C:\Program Files (x86)\Steam\steamapps\common\sbox",
		};

		return roots.Where( r => !string.IsNullOrEmpty( r ) )
			.Select( r => Path.Combine( r, relative ) )
			.FirstOrDefault( File.Exists );
	}

	static readonly string[] Skin = { "head", "chest", "hands", "legs", "feet" };

	const float Inch = 1f / 2.54f;

	static Vec3 Engine( Vec3 v ) => new( v.z, v.x, v.y );

	public static (PolyMesh Body, Skeleton Rig) Load( string path )
	{
		var scene = FbxScene.ReadFile( path );

		// The skeleton, parents first.
		var rig = new Skeleton();
		var index = new Dictionary<string, int>();
		var world = new Dictionary<string, Xform>();

		foreach ( var (name, bind) in scene.BoneBind )
			world[name] = new Xform( Engine( bind.X ).Normal, Engine( bind.Y ).Normal, Engine( bind.Z ).Normal, Engine( bind.Origin ) * Inch );

		void Add( string name )
		{
			if ( index.ContainsKey( name ) )
				return;

			var parent = scene.BoneParent.GetValueOrDefault( name );

			if ( parent is not null && world.ContainsKey( parent ) )
				Add( parent );

			var p = parent is not null && index.TryGetValue( parent, out var pi ) ? pi : -1;
			var local = p >= 0 ? world[parent].Inverse * world[name] : world[name];
			index[name] = rig.Bones.Count;
			rig.Bones.Add( new Bone( name, p, local, 1f ) );
		}

		foreach ( var name in world.Keys )
			Add( name );

		// A bone's length is the way to its first child, which is what a rig drawn in Effigy has.
		foreach ( var (name, i) in index )
		{
			var child = rig.Bones.FindIndex( b => b.Parent == i );

			if ( child >= 0 )
				rig.Bones[i].Length = MathF.Max( 0.25f, (world[rig.Bones[child].Name].Origin - world[name].Origin).Length );
		}

		// The skin, welded across the meshes by position, carrying its weights.
		var body = new PolyMesh();
		var weights = new SkinWeights();
		var weld = new Dictionary<(long, long, long), int>();

		foreach ( var mesh in scene.Meshes.Where( m => Skin.Any( s => m.Name.EndsWith( "_" + s, StringComparison.OrdinalIgnoreCase ) ) ) )
		{
			var remap = new int[mesh.Points.Length];

			for ( var i = 0; i < mesh.Points.Length; i++ )
			{
				var p = Engine( mesh.Points[i] ) * Inch;
				var key = ((long)MathF.Round( p.x * 1000f ), (long)MathF.Round( p.y * 1000f ), (long)MathF.Round( p.z * 1000f ));

				if ( !weld.TryGetValue( key, out var at ) )
				{
					at = body.AddVertex( p );
					weld[key] = at;

					var w = mesh.Weights?[i]?
						.Where( x => index.ContainsKey( x.Bone ) && x.Weight > 0f )
						.Select( x => new BoneWeight( index[x.Bone], x.Weight ) )
						.ToArray() ?? Array.Empty<BoneWeight>();

					var total = w.Sum( x => x.Weight );
					weights.Vertices.Add( total > 0f ? w.Select( x => new BoneWeight( x.Bone, x.Weight / total ) ).ToArray() : w );
				}

				remap[i] = at;
			}

			foreach ( var poly in mesh.Polygons )
			{
				var corners = poly.Select( c => remap[c] ).ToArray();

				if ( corners.Distinct().Count() == corners.Length && corners.Length >= 3 )
					body.AddFace( corners );
			}
		}

		body.Skin = weights;
		return (body, rig);
	}
}
