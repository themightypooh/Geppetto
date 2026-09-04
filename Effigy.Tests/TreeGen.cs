using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Recreate the Blender oak in Effigy: recursive tapered lofts, leaf cards, branch bones.
/// Bark is a tiled material on the wood — not displaced into the mesh.
///
/// Invoked as: Effigy.Tests.exe --tree [outDir]
/// </summary>
public static class TreeGen
{
	const float Inches = 39.37007874f;
	const int Seed = 11;

	public static int Run( string outDir )
	{
		Directory.CreateDirectory( outDir );
		WriteBarkAndLeafTextures( outDir );

		var rng = new Random( Seed );
		var ( segs, leaves ) = GrowOak( rng );
		Console.WriteLine( $"effigy oak: {segs.Count} limbs, {leaves.Count} leaves" );

		var studio = new PartStudio();
		studio.MaterialNames[0] = "materials/trees/bark_oak.vmat";
		studio.MaterialNames[1] = "materials/trees/leaf_oak.vmat";

		foreach ( var seg in segs )
			AddLimb( studio, seg );

		var uv = studio.Add( new UVProjectFeature() );
		uv.Name = "bark_uv";
		uv.Mode.Index = 0; // Box
		uv.Scale.Value = 14f;

		var subdiv = studio.Add( new SubdivideFeature() );
		subdiv.Name = "wood_subdiv";
		subdiv.Levels.Value = 1;

		foreach ( var leaf in leaves )
			AddLeaf( studio, leaf, rng );

		var report = studio.Rebuild();
		PrintErrors( studio, report, "oak" );
		if ( report.HasErrors )
			return 1;

		Console.WriteLine( $"  bodies {studio.Bodies.Count} features {studio.Features.Count}" );

		var ( mesh, ranges ) = studio.ToMeshWithBodies();
		Console.WriteLine( $"  mesh {mesh.VertexCount} verts {mesh.FaceCount} faces" );

		var skeleton = BuildSkeleton( segs );
		Console.WriteLine( $"  bones {skeleton.Count}" );

		var bodyToBone = new Dictionary<string, string>();
		foreach ( var b in studio.Bodies )
		{
			if ( skeleton.IndexOf( b.Name ) >= 0 )
				bodyToBone[b.Id] = b.Name;
			else if ( b.Name.StartsWith( "leaf_", StringComparison.Ordinal )
				&& TryHostBone( b.Name, leaves, out var host )
				&& skeleton.IndexOf( host ) >= 0 )
				bodyToBone[b.Id] = host;
		}

		mesh.Skin = SkinBinder.SmoothWeights(
			mesh,
			SkinBinder.BindBodies( mesh, ranges, bodyToBone, skeleton ),
			iterations: 2,
			strength: 0.4f );

		var dmx = Path.Combine( outDir, "oak_effigy.dmx" );
		var vmdl = Path.Combine( outDir, "oak_effigy.vmdl" );
		var effigy = Path.Combine( outDir, "oak_effigy.effigy" );

		DmxWriter.WriteFile( mesh, dmx, skeleton, modelName: "oak_effigy",
			materialName: studio.NameForSlot );
		File.WriteAllText( vmdl, SkinnedVmdl( "models/trees/oak_effigy.dmx", skeleton, studio, mesh ) );
		StudioDocument.WriteFile( studio, effigy );

		Console.WriteLine( "DMX " + dmx );
		Console.WriteLine( "VMDL " + vmdl );
		Console.WriteLine( "DONE" );
		return 0;
	}

	sealed class Seg
	{
		public string Name;
		public string Parent;
		public Vec3 Head, Tail;
		public float RadiusHead, RadiusTail;
	}

	sealed class Leaf
	{
		public string Name;
		public string Host;
		public Vec3 Position;
		public Vec3 Normal;
	}

	static (List<Seg> segs, List<Leaf> leaves) GrowOak( Random rng )
	{
		var segs = new List<Seg>();
		var leaves = new List<Leaf>();
		var n = 0;
		var leafN = 0;

		string Next( string prefix )
		{
			n++;
			return $"{prefix}_{n:00}";
		}

		void Grow( string parent, Vec3 origin, Vec3 direction, float length, float radius, int depth, int maxDepth )
		{
			direction = direction.Normal;
			var wobbleAmt = depth > 0 ? 0.35f : 0.12f;
			direction = ( direction + new Vec3( Rand( rng, -0.18f, 0.18f ), Rand( rng, -0.18f, 0.18f ), Rand( rng, -0.06f, 0.10f ) ) * wobbleAmt ).Normal;
			var tail = origin + direction * length;
			var name = Next( depth == 0 ? "trunk" : ( depth < 2 ? "limb" : "twig" ) );
			segs.Add( new Seg
			{
				Name = name,
				Parent = parent,
				Head = origin,
				Tail = tail,
				RadiusHead = radius,
				RadiusTail = radius * Rand( rng, 0.62f, 0.78f ),
			} );

			if ( depth >= maxDepth || radius < 0.018f * Inches )
			{
				Orthonormal( direction, out var x, out var y );
				var count = rng.Next( 5, 9 );
				for ( var i = 0; i < count; i++ )
				{
					leafN++;
					var jitter = x * Rand( rng, -0.11f, 0.11f ) * Inches
						+ y * Rand( rng, -0.11f, 0.11f ) * Inches
						+ direction * Rand( rng, -0.04f, 0.10f ) * Inches;
					leaves.Add( new Leaf
					{
						Name = $"leaf_{leafN:000}",
						Host = name,
						Position = tail + jitter,
						Normal = ( direction + x * Rand( rng, -0.5f, 0.5f ) + y * Rand( rng, -0.5f, 0.5f ) ).Normal,
					} );
				}
				return;
			}

			var childCount = depth == 0 ? 4 : ( depth == 1 ? 3 : 2 );
			Orthonormal( direction, out var fx, out _ );
			var baseYaw = Rand( rng, 0f, 360f );
			for ( var i = 0; i < childCount; i++ )
			{
				var yaw = baseYaw + 360f / childCount * i + Rand( rng, -18f, 18f );
				var pitch = depth == 0 ? Rand( rng, 22f, 42f ) : Rand( rng, 18f, 38f );
				var outward = Rotate( fx, direction, yaw );
				var childDir = ( direction * MathF.Cos( pitch * MathF.PI / 180f ) + outward * MathF.Sin( pitch * MathF.PI / 180f ) ).Normal;
				var fork = Vec3.Lerp( origin, tail, Rand( rng, 0.72f, 0.96f ) );
				Grow( name, fork, childDir, length * Rand( rng, 0.52f, 0.74f ), radius * Rand( rng, 0.48f, 0.66f ), depth + 1, maxDepth );
			}

			if ( depth < 2 )
			{
				var leader = ( direction + new Vec3( Rand( rng, -0.08f, 0.08f ), Rand( rng, -0.08f, 0.08f ), 0.18f ) ).Normal;
				Grow( name, tail, leader, length * Rand( rng, 0.55f, 0.70f ), radius * Rand( rng, 0.55f, 0.68f ), depth + 1, maxDepth );
			}
		}

		Grow( null, Vec3.Zero, new Vec3( 0.08f, -0.04f, 1f ), 1.15f * Inches, 0.22f * Inches, 0, 4 );
		return (segs, leaves);
	}

	static void AddLimb( PartStudio studio, Seg seg )
	{
		var length = ( seg.Tail - seg.Head ).Length;
		if ( length < 0.05f )
			return;

		var s0 = studio.Add( new SketchFeature() );
		s0.Name = seg.Name + "_base";
		s0.Plane.Index = 2; // YZ, loft along +X
		s0.Sketch.AddCircle( Vec2.Zero, MathF.Max( seg.RadiusHead, 0.08f ) );

		var s1 = studio.Add( new SketchFeature() );
		s1.Name = seg.Name + "_tip";
		s1.Plane.Index = 2;
		s1.PlaneOffset.Value = length;
		s1.Sketch.AddCircle( Vec2.Zero, MathF.Max( seg.RadiusTail, 0.06f ) );

		var loft = studio.Add( new LoftFeature() );
		loft.Name = seg.Name;
		loft.Sections = new List<string> { s0.Id, s1.Id };
		loft.Segments.Value = 10;
		loft.Result.Index = 1;
		loft.Material.Value = 0;

		var xf = studio.Add( new TransformFeature() );
		xf.Name = seg.Name + "_place";
		xf.Bodies.BodyIds.Add( loft.Id + "b0" );
		AimPlusX( xf, seg.Head, seg.Tail );
	}

	static void AddLeaf( PartStudio studio, Leaf leaf, Random rng )
	{
		var box = studio.Add( new PrimitiveFeature() );
		box.Name = leaf.Name;
		box.Shape.Index = 0;
		box.SizeX.Value = Rand( rng, 2.6f, 4.2f );
		box.SizeY.Value = Rand( rng, 1.2f, 2.0f );
		box.SizeZ.Value = 0.06f;
		box.Material.Value = 1;

		var xf = studio.Add( new TransformFeature() );
		xf.Name = leaf.Name + "_orient";
		xf.Bodies.BodyIds.Add( box.Id + "b0" );
		AimPlusX( xf, leaf.Position, leaf.Position + leaf.Normal );
		xf.RotationAngle.Value += Rand( rng, -25f, 25f );
	}

	static void AimPlusX( TransformFeature xf, Vec3 head, Vec3 tail )
	{
		var dir = ( tail - head ).Normal;
		var from = new Vec3( 1, 0, 0 );
		var axis = Vec3.Cross( from, dir );
		var dot = Vec3.Dot( from, dir );

		if ( axis.LengthSquared < 1e-10f )
		{
			xf.RotationAxis.Value = new Vec3( 0, 0, 1 );
			xf.RotationAngle.Value = dot < 0f ? 180f : 0f;
		}
		else
		{
			xf.RotationAxis.Value = axis.Normal;
			xf.RotationAngle.Value = MathF.Atan2( axis.Length, dot ) * ( 180f / MathF.PI );
		}

		xf.Translate.Value = head;
	}

	static Skeleton BuildSkeleton( List<Seg> segs )
	{
		var s = new Skeleton();
		var index = new Dictionary<string, int>();

		foreach ( var seg in segs )
		{
			var parent = -1;
			if ( !string.IsNullOrEmpty( seg.Parent ) && index.TryGetValue( seg.Parent, out var p ) )
				parent = p;

			var i = s.AddBoneFromPoints( seg.Name, parent, seg.Head, seg.Tail );
			index[seg.Name] = i;
		}

		return s;
	}

	static bool TryHostBone( string leafName, List<Leaf> leaves, out string host )
	{
		host = null;
		foreach ( var leaf in leaves )
		{
			if ( leaf.Name == leafName )
			{
				host = leaf.Host;
				return true;
			}
		}
		return false;
	}

	static float Rand( Random rng, float a, float b ) => a + (float)rng.NextDouble() * ( b - a );

	static void Orthonormal( Vec3 d, out Vec3 x, out Vec3 y )
	{
		d = d.Normal;
		var helper = MathF.Abs( d.z ) < 0.92f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		x = Vec3.Cross( d, helper ).Normal;
		y = Vec3.Cross( d, x ).Normal;
	}

	static Vec3 Rotate( Vec3 v, Vec3 axis, float degrees ) =>
		Xform.Rotate( axis, degrees * MathF.PI / 180f ).TransformDirection( v );

	static void WriteBarkAndLeafTextures( string outDir )
	{
		const int n = 512;
		var bark = new byte[n * n * 3];
		var leaf = new byte[n * n * 3];

		for ( var y = 0; y < n; y++ )
		for ( var x = 0; x < n; x++ )
		{
			var i = ( y * n + x ) * 3;
			var u = x / (float)n;
			var v = y / (float)n;

			// vertical grain + a few dark fissures
			var grain = 0.55f
				+ 0.18f * Noise( u * 6f, v * 28f )
				+ 0.10f * Noise( u * 18f, v * 64f )
				+ 0.08f * MathF.Sin( v * 40f + Noise( u * 4f, v * 4f ) * 3f );
			var crack = MathF.Abs( Noise( u * 3f, v * 12f ) );
			if ( crack > 0.72f )
				grain *= 0.45f;
			grain = Math.Clamp( grain, 0.12f, 0.85f );
			bark[i] = (byte)( grain * 92 );
			bark[i + 1] = (byte)( grain * 58 );
			bark[i + 2] = (byte)( grain * 32 );

			var dx = u - 0.5f;
			var dy = v - 0.5f;
			var r = MathF.Sqrt( dx * dx * 1.6f + dy * dy );
			var inside = r < 0.48f;
			var midrib = MathF.Abs( dx ) < 0.03f && r < 0.46f;
			float g = 0.18f, ge = 0.42f, gb = 0.10f;
			if ( inside )
			{
				var vein = 0.08f * Noise( u * 14f, v * 10f );
				g = 0.22f + vein;
				ge = 0.48f + 0.10f * Noise( u * 8f, v * 8f );
				gb = 0.12f;
				if ( midrib ) { g = 0.16f; ge = 0.32f; gb = 0.08f; }
			}
			else { g = 0.05f; ge = 0.12f; gb = 0.04f; }
			leaf[i] = (byte)( Math.Clamp( g, 0, 1 ) * 255 );
			leaf[i + 1] = (byte)( Math.Clamp( ge, 0, 1 ) * 255 );
			leaf[i + 2] = (byte)( Math.Clamp( gb, 0, 1 ) * 255 );
		}

		PngWriter.WriteFile( Path.Combine( outDir, "bark_oak.png" ), bark, n, n );
		PngWriter.WriteFile( Path.Combine( outDir, "leaf_oak.png" ), leaf, n, n );
		File.WriteAllText( Path.Combine( outDir, "bark_oak.vmat" ), Vmat( "materials/trees/bark_oak.png", 0.88f ) );
		File.WriteAllText( Path.Combine( outDir, "leaf_oak.vmat" ), Vmat( "materials/trees/leaf_oak.png", 0.72f ) );
	}

	static string Vmat( string color, float rough ) =>
		"Layer0\n{\n"
		+ "\tshader \"shaders/complex.shader\"\n"
		+ "\tg_flModelTintAmount \"0.000000\"\n"
		+ "\tg_vColorTint \"[1.000000 1.000000 1.000000 1.000000]\"\n"
		+ $"\tTextureColor \"{color}\"\n"
		+ "\tTextureNormal \"materials/default/default_normal.tga\"\n"
		+ "\tTextureRoughness \"materials/default/default_rough.tga\"\n"
		+ "\tTextureAmbientOcclusion \"materials/default/default_ao.tga\"\n"
		+ "\tg_flMetalness \"0.000000\"\n"
		+ $"\tg_flRoughness \"{rough:0.000000}\"\n"
		+ "}\n";

	static float Noise( float x, float y )
	{
		var x0 = (int)MathF.Floor( x );
		var y0 = (int)MathF.Floor( y );
		var fx = x - x0;
		var fy = y - y0;
		fx = fx * fx * ( 3f - 2f * fx );
		fy = fy * fy * ( 3f - 2f * fy );
		var a = Hash( x0, y0 );
		var b = Hash( x0 + 1, y0 );
		var c = Hash( x0, y0 + 1 );
		var d = Hash( x0 + 1, y0 + 1 );
		return a + ( b - a ) * fx + ( c - a ) * fy + ( a - b - c + d ) * fx * fy;
	}

	static float Hash( int x, int y )
	{
		unchecked
		{
			var n = x * 374761393 + y * 668265263;
			n = ( n ^ ( n >> 13 ) ) * 1274126177;
			return ( n & 0x7fffffff ) / 2147483647f;
		}
	}

	static void PrintErrors( PartStudio studio, RebuildReport report, string when )
	{
		Console.WriteLine( $"rebuild {when}: {report}" );
		foreach ( var f in studio.Features.Where( f => f.Error is not null ) )
			Console.WriteLine( $"  ERROR {f.Name}: {f.Error}" );
		foreach ( var ( id, msg ) in report.Errors )
			Console.WriteLine( $"  report {id}: {msg}" );
	}

	static string SkinnedVmdl( string meshFilename, Skeleton skeleton, PartStudio studio, PolyMesh mesh ) =>
		"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n"
		+ "{\n"
		+ "\trootNode = \n"
		+ "\t{\n"
		+ "\t\t_class = \"RootNode\"\n"
		+ "\t\tchildren = \n"
		+ "\t\t[\n"
		+ VmdlMaterials.GroupList( studio, mesh )
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"RenderMeshList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{\n"
		+ "\t\t\t\t\t\t_class = \"RenderMeshFile\"\n"
		+ "\t\t\t\t\t\tname = \"Body_LOD0\"\n"
		+ "\t\t\t\t\t\tchildren = \n"
		+ "\t\t\t\t\t\t[\n"
		+ "\t\t\t\t\t\t]\n"
		+ $"\t\t\t\t\t\tfilename = \"{meshFilename}\"\n"
		+ "\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n"
		+ "\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]\n"
		+ "\t\t\t\t\t\timport_scale = 1.0\n"
		+ "\t\t\t\t\t\talign_origin_x_type = \"None\"\n"
		+ "\t\t\t\t\t\talign_origin_y_type = \"None\"\n"
		+ "\t\t\t\t\t\talign_origin_z_type = \"None\"\n"
		+ "\t\t\t\t\t\tparent_bone = \"\"\n"
		+ "\t\t\t\t\t},\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ VmdlAnimation.BoneMarkupList( skeleton )
		+ VmdlAnimation.BindPoseList()
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"PhysicsShapeList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{\n"
		+ "\t\t\t\t\t\t_class = \"PhysicsMeshFromRender\"\n"
		+ "\t\t\t\t\t\tparent_bone = \"\"\n"
		+ "\t\t\t\t\t\tsurface_prop = \"wood\"\n"
		+ "\t\t\t\t\t\tcollision_tags = \"solid\"\n"
		+ "\t\t\t\t\t},\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t]\n"
		+ "\t\tmodel_archetype = \"\"\n"
		+ "\t\tprimary_associated_entity = \"\"\n"
		+ "\t\tanim_graph_name = \"\"\n"
		+ "\t\tbase_model_name = \"\"\n"
		+ "\t}\n"
		+ "}\n";
}
