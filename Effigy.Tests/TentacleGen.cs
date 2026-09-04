using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// A tentacle, built the way the pipeline says to build one: a lofted CAD cage, a multires sculpt
/// on top of it for the suckers, then a bone chain skinned to the result.
///
/// It stands straight up the +Z axis, so it is a rig test as much as a model - a straight chain
/// with no bend in the bind pose is the one that shows immediately whether the weights fall off
/// along the length or bunch at a joint.
///
/// The suckers are sculpted rather than modelled. They are surface detail on a smooth tube: as
/// geometry in the cage they would multiply its face count for no parametric gain, and every
/// downstream edit to the taper would have to drag them along.
///
/// Invoked as: Effigy.Tests.exe --tentacle [outDir]
/// </summary>
public static class TentacleGen
{
	/// <summary>Overall length, in s&amp;box units (inches). Six feet of arm.</summary>
	const float Height = 72f;

	const float BaseRadius = 10f;
	const float TipRadius = 2f;

	/// <summary>
	/// Cross-sections up the length. Each one is a Sketch feature the loft skins through.
	///
	/// It is 40 rather than the dozen the taper needs because the sculpt is what sets this number,
	/// not the shape. A dozen sections put quads on the cage seven units tall and two wide, and a
	/// round sucker brushed onto a grid that anisotropic comes out a rectangle - the subdivision
	/// inherits the cage's proportions however deep it goes.
	/// </summary>
	const int Sections = 40;

	/// <summary>Points each section is resampled to - the loop count around the tube. Chosen with
	/// <see cref="Sections"/> so the cage's quads are about square at the base.</summary>
	const int Segments = 24;

	/// <summary>
	/// Multires levels the sculpt adds over the cage. Each one is 4x the faces; 2 over a cage this
	/// dense is about a dozen vertices across a sucker, which is what it takes for one to read as a
	/// cup rather than a dimple.
	/// </summary>
	const int SculptLevels = 2;

	const int BoneCount = 8;

	public static int Run( string outDir )
	{
		Directory.CreateDirectory( outDir );

		var studio = new PartStudio();
		studio.MaterialNames[0] = "models/effigy/tentacle.vmat";

		var loft = BuildCage( studio );

		var uv = studio.Add( new UVProjectFeature() );
		uv.Name = "tentacle_uv";
		uv.Bodies.BodyIds.Add( loft.Id + "b0" );
		uv.Mode.Index = 0; // Box
		uv.Scale.Value = 32f;

		var sculpt = studio.Add( new SculptFeature() );
		sculpt.Name = "suckers";
		sculpt.Bodies.BodyIds.Add( loft.Id + "b0" );

		// The sculpt needs a cage before it has anything to add levels to, and the cage does not
		// exist until the loft has run. So: build once, sculpt, build again.
		var report = studio.Rebuild();
		PrintErrors( studio, report, "cage" );

		if ( report.HasErrors )
			return 1;

		var cage = studio.Bodies[0].Mesh.Clone();
		Console.WriteLine( $"cage {cage.VertexCount}v {cage.FaceCount}f" );

		Sculpt( sculpt.Sculpt );

		report = studio.Rebuild();
		PrintErrors( studio, report, "sculpt" );

		if ( report.HasErrors )
			return 1;

		var mesh = studio.ToMesh();
		Console.WriteLine( $"sculpted {mesh.VertexCount}v {mesh.FaceCount}f, {MeshValidator.Validate( mesh )}" );

		var skeleton = BuildSkeleton();
		mesh.Skin = SkinBinder.SmoothWeights(
			mesh,
			SkinBinder.BindSmooth( mesh, skeleton ),
			iterations: 2,
			strength: 0.4f );

		Console.WriteLine( $"bones {skeleton.Count}, rigged {mesh.IsRigged}" );

		// The rig leaves here through DmxWriter and then through the engine's compiler, which
		// reports a bad one as a failure in a tool nobody was looking at. Ask before that.
		foreach ( var problem in RigDiagnostics.Check( skeleton, mesh ) )
			Console.WriteLine( $"  rig {problem}" );

		var dmx = Path.Combine( outDir, "tentacle.dmx" );
		var vmdl = Path.Combine( outDir, "tentacle.vmdl" );
		var doc = Path.Combine( outDir, "tentacle.effigy" );
		var obj = Path.Combine( outDir, "tentacle.obj" );

		DmxWriter.WriteFile( mesh, dmx, skeleton, modelName: "tentacle",
			materialName: studio.NameForSlot );
		File.WriteAllText( vmdl, SkinnedVmdl( "models/effigy/tentacle.dmx", skeleton, studio, mesh ) );
		ObjWriter.WriteFile( mesh, obj, "tentacle" );
		WriteSkin( outDir );

		StudioDocument.WriteFile( studio, doc );
		var blobs = SculptSidecar.Save( studio, doc );

		// Feature ids are new on every run, so yesterday's blobs are orphans in the same folder.
		// Pruning is destructive and deliberately not part of saving; here the document is the whole
		// reason that folder exists, so it is safe and the side-car stays one sculpt big.
		SculptSidecar.Prune( studio, doc );

		Reopen( doc, mesh.VertexCount );

		PngPreview.WriteSheet( new[]
		{
			new PngPreview.Tile( cage, "cage" ),
			new PngPreview.Tile( cage, "cage wire", wireframe: true ),
			new PngPreview.Tile( Facing( mesh ), "sculpted" ),
			new PngPreview.Tile( Facing( Slice( mesh, Height * 0.05f, Height * 0.3f ) ), "suckers, close up" ),
			new PngPreview.Tile( BoneCage( skeleton ), "bones", wireframe: true ),
		}, Path.Combine( outDir, "tentacle_preview.png" ), columns: 4, tileSize: 460 );

		Console.WriteLine( "DMX " + dmx );
		Console.WriteLine( "VMDL " + vmdl );
		Console.WriteLine( $"EFFIGY {doc} ({blobs} sculpt blob(s))" );
		Console.WriteLine( "DONE" );
		return 0;
	}

	/// <summary>Radius at a height fraction. A fast taper near the tip, a slight swell low down,
	/// so it reads as an arm rather than as a cone.</summary>
	static float RadiusAt( float t )
	{
		var taper = TipRadius + ( BaseRadius - TipRadius ) * MathF.Pow( 1f - t, 0.70f );
		return taper * ( 1f + 0.10f * MathF.Sin( t * MathF.PI ) );
	}

	static LoftFeature BuildCage( PartStudio studio )
	{
		var sections = new List<string>( Sections );

		for ( var i = 0; i < Sections; i++ )
		{
			var t = i / (float)( Sections - 1 );

			var s = studio.Add( new SketchFeature() );
			s.Name = $"section_{i:00}";
			s.Plane.Index = 0; // Top (XY), so the loft runs up +Z
			s.PlaneOffset.Value = t * Height;
			s.Sketch.AddCircle( Vec2.Zero, RadiusAt( t ) );
			sections.Add( s.Id );
		}

		var loft = studio.Add( new LoftFeature() );
		loft.Name = "tentacle";
		loft.Sections = sections;
		loft.Segments.Value = Segments;
		loft.Result.Index = 1; // New body
		loft.Material.Value = 0;
		return loft;
	}

	/// <summary>
	/// The sculpt: two staggered rows of suckers up the front, a few muscle bulges, and a smooth
	/// pass over the tip.
	///
	/// Every stroke is one dab - a BrushStroke with a single sample. The editor's session turns a
	/// mouse drag into a run of them; nothing here needs a drag, and one dab per sucker is what
	/// makes each sucker independently placed rather than smeared along a path.
	/// </summary>
	static void Sculpt( MultiresSculpt sculpt )
	{
		for ( var i = 0; i < SculptLevels; i++ )
			sculpt.AddLevel();

		var level = sculpt.TopLevel;
		Console.WriteLine( $"sculpt level {level}, {sculpt.Cost( level ).Vertices}v" );

		var suckers = 0;
		var moved = 0;
		var side = 1f;

		// Stops where the arm is thinner than the brush is wide, which is well short of the tip: a
		// dab wider than the tube reaches round the far side and tears it rather than denting it.
		for ( var z = Height * 0.06f; RadiusAt( z / Height ) > 2.4f; )
		{
			var t = z / Height;
			var r = RadiusAt( t );
			var size = Math.Clamp( r * 0.62f, 0.9f, 3.4f );

			// +-24 degrees off the front, alternating, which is the double row an octopus has.
			var angle = side * 24f * MathF.PI / 180f;
			var n = new Vec3( MathF.Cos( angle ), MathF.Sin( angle ), 0f );
			var p = n * r + new Vec3( 0, 0, z );

			// A sucker STANDS OUT of the arm - it is a collar with a hole in it, not a dent. So:
			// a pad pushed proud of the surface, then a pit punched through its middle deep enough
			// to end below the skin. Only the second dab is a hollow, and it is what turns the pad
			// into a ring.
			//
			// The pit uses CONSTANT falloff, which moves everything inside the radius equally and
			// so leaves a floor with a wall around it. A smooth falloff there sinks a soft dimple
			// and the sucker reads as a thumbprint.
			// ORDER MATTERS, and it is the order a thumb would work in: build the pad, round it
			// off, and only then push the hole in. Smoothing after the pit is what fills the pit
			// back in - a wide smooth pass does not know the difference between the stair on a
			// wall and the wall.
			//
			// The pit is aimed at the TOP of the pad, not at where the skin used to be. A brush
			// takes the vertices within a radius of a point in space, and the pad has just moved
			// them a pad's height away from that point: aim the pit where the skin was and it
			// reaches nothing, which looks exactly like the dab having no effect.
			var pad = size * 0.55f;
			var crown = p + n * pad;

			moved += Dab( sculpt, level, BrushKind.Draw, p, n, size, pad, BrushFalloff.Smooth );
			moved += Dab( sculpt, level, BrushKind.Smooth, crown, n, size * 1.15f, 0.35f, BrushFalloff.Sharp );
			moved += Dab( sculpt, level, BrushKind.Draw, crown, n, size * 0.45f, -size * 0.85f, BrushFalloff.Constant );
			moved += Dab( sculpt, level, BrushKind.Smooth, crown, n, size * 0.60f, 0.18f, BrushFalloff.Sharp );

			suckers++;
			side = -side;
			z += MathF.Max( 1.6f, size * 0.95f );
		}

		// Muscle: a few soft inflations down the back, off the sucker rows, so the silhouette is
		// not a clean revolve.
		for ( var i = 0; i < 6; i++ )
		{
			var t = 0.08f + i * 0.14f;
			var r = RadiusAt( t );
			var angle = MathF.PI + ( i % 2 == 0 ? 0.6f : -0.6f );
			var n = new Vec3( MathF.Cos( angle ), MathF.Sin( angle ), 0f );
			moved += Dab( sculpt, level, BrushKind.Inflate, n * r + new Vec3( 0, 0, t * Height ), n,
				r * 1.6f, r * 0.09f, BrushFalloff.Smooth );
		}

		// The tip takes the taper's last section badly - smooth it back to a point.
		moved += Dab( sculpt, level, BrushKind.Smooth, new Vec3( 0, 0, Height ), new Vec3( 0, 0, 1 ),
			TipRadius * 6f, 0.7f, BrushFalloff.Smooth );

		Console.WriteLine( $"  {suckers} suckers, {moved} vertices moved, revision {sculpt.Revision}" );
	}

	static int Dab( MultiresSculpt sculpt, int level, BrushKind kind, Vec3 position, Vec3 normal,
		float radius, float strength, BrushFalloff falloff )
	{
		var stroke = new BrushStroke { Kind = kind, Falloff = falloff };
		stroke.Samples.Add( new BrushSample( position, normal, radius, strength ) );
		return sculpt.Stroke( level, stroke ).Count;
	}

	/// <summary>A chain up the axis. Bone 0 sits at the base and is the root.</summary>
	static Skeleton BuildSkeleton()
	{
		var s = new Skeleton();
		var parent = -1;

		for ( var i = 0; i < BoneCount; i++ )
		{
			var head = new Vec3( 0, 0, Height * i / BoneCount );
			var tail = new Vec3( 0, 0, Height * ( i + 1 ) / BoneCount );
			parent = s.AddBoneFromPoints( $"tentacle_{i + 1:00}", parent, head, tail );
		}

		return s;
	}

	/// <summary>
	/// Read the document back the way the editor opens it, and check the model it rebuilds is the
	/// one just exported. A sculpt lives in a side-car keyed on feature ids, which is the part of
	/// saving that can silently not happen - the .effigy would open to a smooth cone, and nothing
	/// anywhere would say why.
	/// </summary>
	static void Reopen( string doc, int expectedVertices )
	{
		var studio = StudioDocument.ReadFile( doc );
		var loaded = SculptSidecar.Load( studio, doc );
		var report = studio.Rebuild();
		var mesh = studio.ToMesh();
		var ok = !report.HasErrors && loaded == 1 && mesh.VertexCount == expectedVertices;

		Console.WriteLine( $"reopened: {loaded} blob(s), {mesh.VertexCount}v, {report} - "
			+ ( ok ? "matches" : "DOES NOT MATCH" ) );
	}

	/// <summary>
	/// A skin for it: mottled flesh, generated rather than painted, with a vmat beside the model
	/// pointing at it. Next to the .vmdl rather than under materials/ so the whole model is one
	/// folder, which is what a generated asset wants to be.
	/// </summary>
	static void WriteSkin( string outDir )
	{
		const int n = 512;
		var pixels = new byte[n * n * 3];

		for ( var y = 0; y < n; y++ )
		for ( var x = 0; x < n; x++ )
		{
			var i = ( y * n + x ) * 3;
			var u = x / (float)n;
			var v = y / (float)n;

			// Big soft blotches, fine speckle over the top, and a wash down one end so the tube
			// does not read as one flat colour under a static light.
			var blotch = Noise( u * 5f, v * 5f );
			var speckle = Noise( u * 40f, v * 40f );
			var shade = 0.60f + 0.30f * blotch + 0.10f * speckle - 0.14f * v;

			pixels[i] = Byte( shade * 0.86f );
			pixels[i + 1] = Byte( shade * 0.46f );
			pixels[i + 2] = Byte( shade * 0.50f );
		}

		PngWriter.WriteFile( Path.Combine( outDir, "tentacle_color.png" ), pixels, n, n );
		File.WriteAllText( Path.Combine( outDir, "tentacle.vmat" ), Vmat() );
	}

	static string Vmat() =>
		"Layer0\n{\n"
		+ "\tshader \"shaders/complex.shader\"\n"
		+ "\tg_flModelTintAmount \"0.000000\"\n"
		+ "\tg_vColorTint \"[1.000000 1.000000 1.000000 1.000000]\"\n"
		+ "\tTextureColor \"models/effigy/tentacle_color.png\"\n"
		+ "\tTextureNormal \"materials/default/default_normal.tga\"\n"
		+ "\tTextureRoughness \"materials/default/default_rough.tga\"\n"
		+ "\tTextureAmbientOcclusion \"materials/default/default_ao.tga\"\n"
		+ "\tg_flMetalness \"0.000000\"\n"
		+ "\tg_flRoughness \"0.320000\"\n"
		+ "}\n";

	static byte Byte( float v ) => (byte)( Math.Clamp( v, 0f, 1f ) * 255f );

	/// <summary>Value noise, the same one TreeGen uses for bark - enough for a mottle.</summary>
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
			var h = x * 374761393 + y * 668265263;
			h = ( h ^ ( h >> 13 ) ) * 1274126177;
			return ( h & 0x7fffffff ) / 2147483647f;
		}
	}

	/// <summary>
	/// A copy turned about Z so the sucker rows face the preview's fixed camera. Pictures only -
	/// the model itself keeps its suckers on +X.
	/// </summary>
	static PolyMesh Facing( PolyMesh mesh )
	{
		var turned = mesh.Clone();
		var spin = Xform.Rotate( new Vec3( 0, 0, 1 ), -125f * MathF.PI / 180f );

		for ( var i = 0; i < turned.Positions.Count; i++ )
			turned.Positions[i] = spin.TransformPoint( turned.Positions[i] );

		return turned;
	}

	/// <summary>
	/// The faces whose centre sits in a height band, as a mesh of their own. Preview only - it
	/// keeps no topology beyond the faces themselves, so it is a picture, not a model.
	/// </summary>
	static PolyMesh Slice( PolyMesh mesh, float zMin, float zMax )
	{
		var slice = new PolyMesh();

		foreach ( var face in mesh.Faces )
		{
			var centre = Vec3.Zero;

			foreach ( var vi in face.Indices )
				centre += mesh.Positions[vi];

			centre /= face.Indices.Length;

			if ( centre.z < zMin || centre.z > zMax )
				continue;

			slice.AddFace( face.Indices.Select( vi => slice.AddVertex( mesh.Positions[vi] ) ).ToArray() );
		}

		return slice;
	}

	/// <summary>The skeleton as a mesh, purely so the preview sheet can show it.</summary>
	static PolyMesh BoneCage( Skeleton skeleton )
	{
		var mesh = new PolyMesh();

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			var head = skeleton.HeadWorld( i );
			var tail = skeleton.TailWorld( i );
			var w = ( tail - head ).Length * 0.18f;
			var a = mesh.AddVertex( head + new Vec3( -w, 0, 0 ) );
			var b = mesh.AddVertex( head + new Vec3( w, 0, 0 ) );
			var c = mesh.AddVertex( tail + new Vec3( 0, w, 0 ) );
			var d = mesh.AddVertex( tail + new Vec3( 0, -w, 0 ) );
			mesh.AddFace( new[] { a, b, c, d } );
		}

		return mesh;
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
		+ "\t\t\t\t\t\tsurface_prop = \"flesh\"\n"
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
