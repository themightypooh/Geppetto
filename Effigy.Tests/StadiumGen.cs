using System;
using System.Collections.Generic;
using System.IO;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// A huge futuristic stadium, built the way the pipeline says to build one: a PartStudio of
/// ordinary features that a person could keep editing afterwards, exported as OBJ, DMX, VMDL and
/// an .effigy document.
///
/// The seating is ONE continuous structure, not a set of separate buildings. In plan it is a
/// horseshoe — three flat straight sections (back, left, right) joined directly at two square
/// seams, with the fourth side (the front) left entirely open. Each section is a single wedge, so
/// the bowl rises in a clean straight slope from the field edge up to the outer rim, and the two
/// corners are the sharp angled seams where a wedge meets its neighbour — no smoothed curve.
///
/// Over the bowl floats a matching cantilevered canopy: three thin slabs tilted so they lift
/// toward the field. A narrow crown band trims the bowl's outer rim, two slim pylons frame the
/// open front, and the field is a flat slab in the middle.
///
/// Invoked as: Effigy.Tests.exe --stadium [outDir]
/// </summary>
public static class StadiumGen
{
	// The bowl. W is the field's half-width, B its back half-depth, F its front half-depth; D is
	// how deep the seating ramp runs and H how high it rises.
	const float W = 160f;
	const float B = 200f;
	const float F = 260f;
	const float D = 130f;
	const float H = 170f;

	// The canopy: one thin slab per section, cantilevered inward past the field edge and tilted up
	// toward the field, floating a little above the bowl.
	const float RoofThick = 7f;
	const float RoofOverhang = 45f;
	const float RoofRise = 42f;
	const float RoofGap = 14f;

	const float CrownThick = 5f;
	const float CrownHeight = 8f;

	// Material slots: 0 seating, 1 canopy, 2 field, 3 accent (crown band and pylons).
	static readonly string[] Materials =
	{
		"models/effigy/stadium_bowl.vmat",
		"models/effigy/stadium_roof.vmat",
		"models/effigy/stadium_field.vmat",
		"models/effigy/stadium_light.vmat",
	};

	public static int Run( string outDir )
	{
		Directory.CreateDirectory( outDir );
		WriteMaterials( outDir );

		var studio = new PartStudio();

		for ( var slot = 0; slot < Materials.Length; slot++ )
			studio.MaterialNames[slot] = Materials[slot];

		BuildBowl( studio );
		BuildCanopy( studio );
		AddCrowns( studio );
		AddPylons( studio );
		AddField( studio );

		var report = studio.Rebuild();
		PrintErrors( studio, report, "stadium" );

		if ( report.HasErrors )
			return 1;

		var mesh = studio.ToMesh();
		Console.WriteLine( $"  {studio.Bodies.Count} bodies, {mesh.VertexCount} verts, {mesh.FaceCount} faces" );

		foreach ( var b in studio.Bodies )
			Console.WriteLine( $"    {b.Name,-14} {b.Mesh.VertexCount,5}v {b.Mesh.FaceCount,4}f" );

		var obj = Path.Combine( outDir, "stadium.obj" );
		var dmx = Path.Combine( outDir, "stadium.dmx" );
		var vmdl = Path.Combine( outDir, "stadium.vmdl" );
		var doc = Path.Combine( outDir, "stadium.effigy" );

		ObjWriter.WriteFile( mesh, obj, "stadium" );
		DmxWriter.WriteFile( mesh, dmx, modelName: "stadium", materialName: studio.NameForSlot );
		File.WriteAllText( vmdl, StaticVmdl( "models/effigy/stadium.dmx", studio, mesh ) );
		StudioDocument.WriteFile( studio, doc );

		Console.WriteLine( "OBJ    " + obj );
		Console.WriteLine( "DMX    " + dmx );
		Console.WriteLine( "VMDL   " + vmdl );
		Console.WriteLine( "EFFIGY " + doc );

		var plan = TopView( mesh );

		PngPreview.WriteSheet( new[]
		{
			new PngPreview.Tile( mesh, "stadium" ),
			new PngPreview.Tile( mesh, "wireframe", wireframe: true ),
			new PngPreview.Tile( plan, "plan (horseshoe)" ),
			new PngPreview.Tile( plan, "plan wireframe", wireframe: true ),
			new PngPreview.Tile( Filtered( studio, b => b.Name.Contains( "stand" ) ), "seating only" ),
			new PngPreview.Tile( Filtered( studio, b => b.Name.Contains( "roof" ) ), "canopy", wireframe: true ),
		}, Path.Combine( outDir, "stadium_preview.png" ), columns: 3, tileSize: 420 );

		Console.WriteLine( "DONE" );
		return 0;
	}

	// --- geometry ---------------------------------------------------------------------------

	/// <summary>Three wedges, one per flat side, meeting at two square corners. Each rises from
	/// the field edge (the low edge) up to the outer rim (the high edge).</summary>
	static void BuildBowl( PartStudio studio )
	{
		var sideLength = F + B + D;
		var sideY = (F - (B + D)) / 2f;

		// Back stand: the slope runs along Y, rising toward -Y (the outer back). The wedge's own
		// high side is -X, so turn it a quarter turn about Z.
		AddWedge( studio, "back stand", D, W * 2f, H, 90f, new Vec3( 0, -(B + D / 2f), H / 2f ), 0 );

		// Left stand: the wedge's native slope already rises toward -X (outward), so no rotation.
		AddWedge( studio, "left stand", D, sideLength, H, 0f, new Vec3( -(W + D / 2f), sideY, H / 2f ), 0 );

		// Right stand: mirrored, so the slope rises toward +X.
		AddWedge( studio, "right stand", D, sideLength, H, 180f, new Vec3( W + D / 2f, sideY, H / 2f ), 0 );
	}

	/// <summary>Three tilted slabs matching the bowl's three sides, each lifting toward the field.</summary>
	static void BuildCanopy( PartStudio studio )
	{
		var depth = D + RoofOverhang;
		var sideLength = F + B + D + RoofOverhang;
		var sideY = (F + RoofOverhang - (B + D)) / 2f;

		var (backAngle, backCos) = Tilt( RoofRise, depth );
		var (sideAngle, sideCos) = Tilt( RoofRise, depth );

		var lift = H + RoofGap;
		var halfRise = RoofRise / 2f;

		AddSlab( studio, "back roof", new Vec3( W * 2f, depth, RoofThick ),
			new Vec3( 1, 0, 0 ), backAngle,
			new Vec3( 0, -(B + D) + depth / 2f * backCos, lift + halfRise ), 1 );

		AddSlab( studio, "left roof", new Vec3( depth, sideLength, RoofThick ),
			new Vec3( 0, 1, 0 ), -sideAngle,
			new Vec3( -(W + D) + depth / 2f * sideCos, sideY, lift + halfRise ), 1 );

		AddSlab( studio, "right roof", new Vec3( depth, sideLength, RoofThick ),
			new Vec3( 0, 1, 0 ), sideAngle,
			new Vec3( W + D - depth / 2f * sideCos, sideY, lift + halfRise ), 1 );
	}

	/// <summary>A thin contrast band along the bowl's outer rim, the accent that reads as
	/// "futuristic" rather than plain concrete.</summary>
	static void AddCrowns( PartStudio studio )
	{
		var sideLength = F + B + D;
		var sideY = (F - (B + D)) / 2f;

		AddBox( studio, "back crown", new Vec3( W * 2f, CrownThick, CrownHeight ), new Vec3( 0, -(B + D), H + 6f ), 3 );
		AddBox( studio, "left crown", new Vec3( CrownThick, sideLength, CrownHeight ), new Vec3( -(W + D), sideY, H + 6f ), 3 );
		AddBox( studio, "right crown", new Vec3( CrownThick, sideLength, CrownHeight ), new Vec3( W + D, sideY, H + 6f ), 3 );
	}

	/// <summary>Two slim pylons framing the open front, marking the entrance rather than closing it.</summary>
	static void AddPylons( PartStudio studio )
	{
		var height = H + RoofGap + RoofRise + 12f;

		AddBox( studio, "left pylon", new Vec3( 10, 10, height ), new Vec3( -W, F + 8f, height / 2f ), 3 );
		AddBox( studio, "right pylon", new Vec3( 10, 10, height ), new Vec3( W, F + 8f, height / 2f ), 3 );
	}

	/// <summary>The pitch — the flat open area the horseshoe wraps around.</summary>
	static void AddField( PartStudio studio )
	{
		AddBox( studio, "field", new Vec3( W * 2f, F + B, 4f ), new Vec3( 0, (F - B) / 2f, 1f ), 2 );
	}

	/// <summary>A plain box, placed with the primitive's own Position (no rotation).</summary>
	static void AddBox( PartStudio studio, string name, Vec3 size, Vec3 position, int material )
	{
		var box = studio.Add( new PrimitiveFeature() );
		box.Name = name;
		box.Shape.Index = 0; // Box
		box.SizeX.Value = size.x;
		box.SizeY.Value = size.y;
		box.SizeZ.Value = size.z;
		box.Position.Value = position;
		box.Material.Value = material;
	}

	/// <summary>A thin box, tilted about <paramref name="axis"/> and then moved into place.</summary>
	static void AddSlab( PartStudio studio, string name, Vec3 size, Vec3 axis, float angleDeg,
		Vec3 translate, int material )
	{
		var box = studio.Add( new PrimitiveFeature() );
		box.Name = name;
		box.Shape.Index = 0; // Box
		box.SizeX.Value = size.x;
		box.SizeY.Value = size.y;
		box.SizeZ.Value = size.z;
		box.Material.Value = material;

		var xf = studio.Add( new TransformFeature() );
		xf.Name = name + " place";
		xf.Bodies.BodyIds.Add( box.Id + "b0" );
		xf.RotationAxis.Value = axis;
		xf.RotationAngle.Value = angleDeg;
		xf.Translate.Value = translate;
	}

	/// <summary>A wedge ramp, turned about Z and moved into place. The wedge's slope runs along X
	/// (low at +X, high at -X) with its long axis on Y.</summary>
	static void AddWedge( PartStudio studio, string name, float depth, float length, float height,
		float rotateDeg, Vec3 translate, int material )
	{
		var w = studio.Add( new PrimitiveFeature() );
		w.Name = name;
		w.Shape.Index = 3; // Wedge
		w.SizeX.Value = depth;
		w.SizeY.Value = length;
		w.SizeZ.Value = height;
		w.Material.Value = material;

		var xf = studio.Add( new TransformFeature() );
		xf.Name = name + " place";
		xf.Bodies.BodyIds.Add( w.Id + "b0" );
		xf.RotationAxis.Value = new Vec3( 0, 0, 1 );
		xf.RotationAngle.Value = rotateDeg;
		xf.Translate.Value = translate;
	}

	/// <summary>The tilt that lifts one edge of a slab by <paramref name="rise"/> across a slab of
	/// <paramref name="depth"/> — angle and the cosine the placement math needs.</summary>
	static (float AngleDeg, float Cos) Tilt( float rise, float depth )
	{
		var sin = Math.Clamp( rise / depth, -1f, 1f );
		var angle = MathF.Asin( sin ) * (180f / MathF.PI);
		return (angle, MathF.Cos( angle * MathF.PI / 180f ));
	}

	// --- export helpers --------------------------------------------------------------------

	/// <summary>The bodies whose name passes the predicate, merged into one mesh. Preview only.</summary>
	static PolyMesh Filtered( PartStudio studio, Func<Body, bool> keep )
	{
		var mesh = new PolyMesh();

		foreach ( var b in studio.Bodies )
		{
			if ( keep( b ) )
				MeshTransform.Append( mesh, b.Mesh );
		}

		return mesh;
	}

	/// <summary>A copy turned on its side so the camera sees the plan — the shape that shows the
	/// horseshoe. Preview only.</summary>
	static PolyMesh TopView( PolyMesh mesh )
	{
		var turned = mesh.Clone();
		var view = Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI / 2f );

		for ( var i = 0; i < turned.Positions.Count; i++ )
			turned.Positions[i] = view.TransformPoint( turned.Positions[i] );

		return turned;
	}

	static void PrintErrors( PartStudio studio, RebuildReport report, string when )
	{
		Console.WriteLine( $"rebuild {when}: {report}" );

		foreach ( var f in studio.Features )
		{
			if ( f.Error is not null )
				Console.WriteLine( $"  ERROR {f.Name}: {f.Error}" );
		}

		foreach ( var ( id, msg ) in report.Errors )
			Console.WriteLine( $"  report {id}: {msg}" );
	}

	static string StaticVmdl( string meshFilename, PartStudio studio, PolyMesh mesh ) =>
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
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"PhysicsShapeList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{\n"
		+ "\t\t\t\t\t\t_class = \"PhysicsMeshFromRender\"\n"
		+ "\t\t\t\t\t\tparent_bone = \"\"\n"
		+ "\t\t\t\t\t\tsurface_prop = \"concrete\"\n"
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

	// --- materials --------------------------------------------------------------------------

	/// <summary>A flat-colour skin per slot, so the compiled model is self-contained rather than
	/// pointing at nothing.</summary>
	static void WriteMaterials( string outDir )
	{
		WriteFlatColor( outDir, "stadium_bowl", 96, 100, 108, 0.9f );
		WriteFlatColor( outDir, "stadium_roof", 24, 26, 30, 0.4f );
		WriteFlatColor( outDir, "stadium_field", 22, 120, 44, 0.8f );
		WriteFlatColor( outDir, "stadium_light", 28, 220, 230, 0.2f );
	}

	static void WriteFlatColor( string outDir, string name, byte r, byte g, byte b, float roughness )
	{
		const int n = 256;
		var px = new byte[n * n * 3];

		for ( var i = 0; i < n * n; i++ )
		{
			px[i * 3] = r;
			px[i * 3 + 1] = g;
			px[i * 3 + 2] = b;
		}

		PngWriter.WriteFile( Path.Combine( outDir, name + ".png" ), px, n, n );
		File.WriteAllText( Path.Combine( outDir, name + ".vmat" ), Vmat( "models/effigy/" + name + ".png", roughness ) );
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
}
