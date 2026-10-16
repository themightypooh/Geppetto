using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Flesh going out as s&amp;box flesh: Flesh Material on the skin shader (presets pick it, the vmat
/// carries the AO bleed, wet lobe, transmission and morph switch, every key is one the shipped
/// skin.shader_c knows), the wall-thickness bake behind the transmission mask, the 4-weight cap the
/// GPU skinning reads, and corrective morphs that put back what linear blend skinning squashes.
/// </summary>
public static class SkinShaderTests
{
	public static void Run( string outDir )
	{
		Section( "flesh material on the skin shader" );
		TestPresetsPickTheShader();
		TestSkinVmat();
		TestKeysAreTheShippedShaders();

		Section( "flesh material: wall thickness drives the light-through mask" );
		TestThickness();
		TestTransmissionMask();
		TestScriptWritesSkin( Path.Combine( outDir, "flesh-skin" ) );

		Section( "skin weights: 4 bones a vertex, and saying what the cap costs" );
		TestCapLoss();
		TestDmxKeepsFour();

		Section( "corrective morphs put back the volume a bent joint loses" );
		TestCorrectiveRestoresVolume();
		TestCorrectiveScriptAndExport( Path.Combine( outDir, "corrective" ) );
	}

	// --- shader choice ------------------------------------------------------------------------------

	static void TestPresetsPickTheShader()
	{
		var skin = new[] { "Wet flesh", "Raw meat", "Lung", "Gut mucosa", "Gum", "Wet gore", "Fat", "Infected skin" };
		var complex = new[] { "Bone", "Tooth", "Oil black", "Black spike", "Entity ribbon" };
		Check( "living tissue presets go out on skin",
			skin.All( n => FleshMaterial.Preset( n ).Shader == FleshShader.Skin ),
			string.Join( ", ", skin.Where( n => FleshMaterial.Preset( n ).Shader != FleshShader.Skin ) ) );
		Check( "bone, enamel, oil, spikes and the glowing ribbon stay on complex",
			complex.All( n => FleshMaterial.Preset( n ).Shader == FleshShader.Complex ) );
		Check( "every preset is one or the other", FleshMaterial.Presets.Length == skin.Length + complex.Length );
		Check( "lung and fat pass light further than raw meat",
			FleshMaterial.Preset( "Lung" ).LightThrough > FleshMaterial.Preset( "Raw meat" ).LightThrough
			&& FleshMaterial.Preset( "Fat" ).LightThrough > FleshMaterial.Preset( "Raw meat" ).LightThrough );
	}

	static Dictionary<string, string> Keys( string vmat ) => vmat.Split( '\n' )
		.Select( l => l.Trim() )
		.Where( l => l.Length > 0 && !l.StartsWith( "//" ) && l != "{" && l != "}" && l != "Layer0" )
		.Select( l => { var i = l.IndexOf( ' ' ); return i < 0 ? (l, "") : (l[..i], l[(i + 1)..].Trim( '"' )); } )
		.GroupBy( kv => kv.Item1 ).ToDictionary( g => g.Key, g => g.First().Item2 );

	static void TestSkinVmat()
	{
		var wet = FleshMaterial.SkinVmatSource( "c.png", "r.png", FleshMaterial.Preset( "Wet flesh" ), "ao.png", "t.png" );
		var k = Keys( wet );
		Check( "the skin vmat names the skin shader", k.GetValueOrDefault( "shader" ) == "shaders/skin.shader", k.GetValueOrDefault( "shader" ) );
		Check( "it carries the colour, roughness, AO and light-through maps",
			k.GetValueOrDefault( "TextureColor" ) == "c.png" && k.GetValueOrDefault( "TextureRoughness" ) == "r.png"
			&& k.GetValueOrDefault( "TextureAmbientOcclusion" ) == "ao.png" && k.GetValueOrDefault( "TextureTransmissiveMask" ) == "t.png" );
		Check( "Facepunch's red AO bleed and diffuse warp",
			k.GetValueOrDefault( "g_vAmbientOcclusionColorBleed" ) == "[0.400000 0.149020 0.129412 0.000000]"
			&& k.GetValueOrDefault( "TextureDiffuseFalloff" ) == "materials/default/default_skin_diffusewarp.tga" );
		Check( "wet flesh gets the mouth's second lobe", k.GetValueOrDefault( "g_flSecondSpecularLobeRatio" ) == "0.263"
			&& k.GetValueOrDefault( "g_flSecondSpecularLobeGlossiness" ) == "0.300" );
		Check( "no morph switch without morphs", !k.ContainsKey( "F_MORPH_SUPPORTED" ) );

		var gum = Keys( FleshMaterial.SkinVmatSource( "c.png", "r.png", FleshMaterial.Preset( "Gum" ), morphs: true ) );
		Check( "gum gets the mouth's tighter curvature", gum.GetValueOrDefault( "g_flCurvatureScale" ) == "0.569" && gum.GetValueOrDefault( "g_flDiffuseNormalBlur" ) == "0.435" );
		Check( "and morphs turn on F_MORPH_SUPPORTED", gum.GetValueOrDefault( "F_MORPH_SUPPORTED" ) == "1" );
		Check( "no light-through map writes black, not nothing", gum.GetValueOrDefault( "TextureTransmissiveMask" ) == "[0.000000 0.000000 0.000000 0.000000]" );

		var fat = Keys( FleshMaterial.SkinVmatSource( "c.png", "r.png", FleshMaterial.Preset( "Fat" ) ) );
		Check( "fat's light comes through in its own scatter colour, not skin orange",
			fat.GetValueOrDefault( "g_vTransmissionColor" ) != "[0.749020 0.388235 0.007843 0.000000]" );
		Check( "dry fat keeps the dry-skin lobe", fat.GetValueOrDefault( "g_flSecondSpecularLobeRatio" ) == "0.008" );
	}

	/// <summary>
	/// Every key the writer emits, checked against the strings in the shipped skin.shader_c. Three are
	/// not in it as text and are Facepunch's own (their human vmats set them): F_MORPH_SUPPORTED lives in
	/// vr_common_features.fxc, and the AO bleed and the texcoord pair sit in compressed variants.
	/// </summary>
	static void TestKeysAreTheShippedShaders()
	{
		var shader = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ),
			"games/steamlibrary/steamapps/common/sbox/core/shaders/skin.shader_c" );
		if ( !File.Exists( shader ) )
		{
			Check( "skin.shader_c keys (skipped - no s&box install here)", true );
			return;
		}

		var bytes = File.ReadAllBytes( shader );
		var text = new string( bytes.Select( b => b >= 32 && b < 127 ? (char)b : '\n' ).ToArray() );
		var known = new HashSet<string> { "shader", "F_MORPH_SUPPORTED", "g_vAmbientOcclusionColorBleed", "g_vTexCoordOffset", "g_vTexCoordScale" };
		var vmat = FleshMaterial.SkinVmatSource( "c.png", "r.png", FleshMaterial.Preset( "Gum" ), "ao.png", "t.png", morphs: true );
		var missing = Keys( vmat ).Keys.Where( key => !known.Contains( key ) && !text.Contains( key ) ).ToList();
		Check( "every skin vmat key is in the shipped skin.shader_c", missing.Count == 0, string.Join( ", ", missing ) );
	}

	// --- thickness ----------------------------------------------------------------------------------

	/// <summary>A ball 4 in across, hollow, its wall <paramref name="wall"/> thick, unwrapped.</summary>
	static PolyMesh HollowBall( float wall )
	{
		var outer = Primitives.QuadSphere( 2f, 12 );
		var inner = Primitives.QuadSphere( 2f - wall, 12 );
		var mesh = outer.Clone();
		var offset = mesh.VertexCount;
		mesh.Positions.AddRange( inner.Positions );
		foreach ( var f in inner.Faces )
			mesh.Faces.Add( new Face( f.Indices.Select( i => i + offset ).Reverse().ToArray(), material: f.Material ) );
		UVUnwrap.Unwrap( mesh );
		return mesh;
	}

	static PolyMesh SolidBall()
	{
		var mesh = Primitives.QuadSphere( 2f, 12 );
		UVUnwrap.Unwrap( mesh );
		return mesh;
	}

	static void TestThickness()
	{
		var solid = Primitives.QuadSphere( 2f, 12 );
		var t = FleshMaterial.Thickness( solid );
		var mean = t.Average();
		Check( "a solid 4 in ball measures 4 in through", MathF.Abs( mean - 4f ) < 0.1f, $"mean {mean:0.000}" );

		var hollow = HollowBall( 0.04f );
		var th = FleshMaterial.Thickness( hollow );
		var outerMean = th.Take( Primitives.QuadSphere( 2f, 12 ).VertexCount ).Average();
		Check( "a 0.04 in wall measures 0.04 in", MathF.Abs( outerMean - 0.04f ) < 0.01f, $"mean {outerMean:0.0000}" );

		var sheet = Primitives.Plane( 4f, 4f, 4, 4 );
		Check( "an open sheet has no far wall (-1, no guess)", FleshMaterial.Thickness( sheet ).All( v => v < 0f ) );
	}

	static void TestTransmissionMask()
	{
		float MeanMask( PolyMesh mesh, float k )
		{
			var maps = FleshMaterial.BakeSkinMaps( mesh, 64, k );
			var lit = 0.0; var n = 0;
			for ( var i = 0; i < maps.Transmission.Filled.Length; i++ )
			{
				if ( !maps.Transmission.Filled[i] ) continue;
				lit += maps.Transmission.Rgb[i * 3] / 255.0; n++;
			}
			return (float)(lit / Math.Max( 1, n ));
		}

		var thin = MeanMask( HollowBall( 0.04f ), 1f );
		var thick = MeanMask( SolidBall(), 1f );
		Check( "a 1 mm wall passes about a third of the red", thin > 0.15f && thin < 0.45f, $"{thin:0.00}" );
		Check( "a solid 4 in ball passes nothing", thick < 0.01f, $"{thick:0.000}" );
		var gel = MeanMask( HollowBall( 0.12f ), 3f );
		var skin = MeanMask( HollowBall( 0.12f ), 1f );
		Check( "Light through 3 lets a 3 mm wall glow that skin would not", gel > 0.15f && skin < 0.06f, $"k3 {gel:0.00}, k1 {skin:0.00}" );

		var ao = FleshMaterial.BakeSkinMaps( SolidBall(), 32, 0f );
		Check( "Light through 0 bakes occlusion only", ao.Ao is not null && ao.Transmission is null );
	}

	static void TestScriptWritesSkin( string dir )
	{
		if ( Directory.Exists( dir ) ) Directory.Delete( dir, true );
		var folder = dir.Replace( '\\', '/' );

		var studio = new PartStudio();
		var result = BuildScript.Run( studio,
			"add Primitive name=Sac shape=Sphere radius=3 segments=24\n" +
			$"add FleshMaterial bodies=Sac preset=wet_flesh resolution=128 folder={folder}\n", dir );
		var feature = studio.Features.OfType<FleshMaterialFeature>().FirstOrDefault();
		var names = feature?.Written.Select( Path.GetFileName ).ToList() ?? new List<string>();
		Check( "a script's wet flesh writes colour, roughness, AO, light-through and a skin vmat",
			result.Ok && new[] { "flesh_sac_color.png", "flesh_sac_rough.png", "flesh_sac_ao.png", "flesh_sac_transmission.png", "flesh_sac.vmat" }.All( names.Contains ),
			string.Join( ", ", names ) + " " + result );
		var vmat = File.Exists( Path.Combine( dir, "flesh_sac.vmat" ) ) ? File.ReadAllText( Path.Combine( dir, "flesh_sac.vmat" ) ) : "";
		Check( "and the vmat is skin with the maps named", vmat.Contains( "shaders/skin.shader" ) && vmat.Contains( "flesh_sac_transmission.png" ) && vmat.Contains( "flesh_sac_ao.png" ) );

		var studio2 = new PartStudio();
		BuildScript.Run( studio2,
			"add Primitive name=Sac shape=Sphere radius=3 segments=24\n" +
			$"add FleshMaterial bodies=Sac preset=wet_flesh shader=complex resolution=64 folder={folder}/c\n", dir );
		var complex = File.Exists( Path.Combine( dir, "c", "flesh_sac.vmat" ) ) ? File.ReadAllText( Path.Combine( dir, "c", "flesh_sac.vmat" ) ) : "";
		Check( "shader=complex overrides the preset", complex.Contains( "complex.vfx" ) && !File.Exists( Path.Combine( dir, "c", "flesh_sac_transmission.png" ) ) );

		var studio3 = new PartStudio();
		BuildScript.Run( studio3,
			"add Primitive name=Rib shape=Sphere radius=3 segments=24\n" +
			$"add FleshMaterial bodies=Rib preset=entity_ribbon shader=skin resolution=64 folder={folder}/r\n", dir );
		var ribbon = studio3.Features.OfType<FleshMaterialFeature>().First();
		Check( "skin on the glowing ribbon warns that the glow is dropped", ribbon.Warning?.Contains( "glow" ) == true, ribbon.Warning );
	}

	// --- weights ------------------------------------------------------------------------------------

	static void TestCapLoss()
	{
		var w = new SkinWeights( 3 );
		w.Vertices[0] = Enumerable.Range( 0, 6 ).Select( b => new BoneWeight( b, 1f / 6f ) ).ToArray();
		w.Vertices[1] = new[] { new BoneWeight( 0, 0.4f ), new BoneWeight( 1, 0.3f ), new BoneWeight( 2, 0.2f ), new BoneWeight( 3, 0.09f ), new BoneWeight( 4, 0.01f ) };
		w.Vertices[2] = new[] { new BoneWeight( 0, 1f ) };
		var (over, worst, at) = w.CapLoss( 4 );
		Check( "two vertices are over 4 bones; the six-way one loses a third", over == 2 && MathF.Abs( worst - 2f / 6f ) < 1e-4f && at == 0, $"{over} over, worst {worst:0.000} at {at}" );
		Check( "and the export says so", w.CapWarning( 4 )?.Contains( "vertex 0" ) == true );

		w.Vertices[0] = new[] { new BoneWeight( 0, 1f ) };
		Check( "a 1% fifth bone is under the 5% anyone would see", w.CapWarning( 4 ) is null );
	}

	static void TestDmxKeepsFour()
	{
		var skeleton = new Skeleton();
		var root = skeleton.AddBone( "root", -1, Xform.Identity, 1f );
		for ( var i = 1; i < 6; i++ ) skeleton.AddBone( $"b{i}", root, Xform.Translate( new Vec3( 0, i, 0 ) ), 1f );
		var mesh = Primitives.Box( 1f, 1f, 1f );
		mesh.Skin = new SkinWeights( mesh.VertexCount );
		for ( var v = 0; v < mesh.VertexCount; v++ )
			mesh.Skin.Vertices[v] = Enumerable.Range( 0, 6 ).Select( b => new BoneWeight( b, (b + 1) / 21f ) ).ToArray();

		var doc = DmxGrammarTests.Parse( DmxWriter.Write( mesh, skeleton ), out var error );
		var data = doc is null ? null : DmxGrammarTests.FindAll( doc, "DmeVertexData" ).FirstOrDefault( n => n.Array( "blendweights$0" ).Count > 0 );
		var weights = data?.Array( "blendweights$0" ).Select( x => float.Parse( x.Value, System.Globalization.CultureInfo.InvariantCulture ) ).ToList() ?? new List<float>();
		var sums = Enumerable.Range( 0, weights.Count / 4 ).Select( v => weights.Skip( v * 4 ).Take( 4 ).Sum() ).ToList();
		Check( "a six-bone vertex goes out as 4 weights summing to 1, strongest kept",
			weights.Count == mesh.VertexCount * 4 && sums.All( x => MathF.Abs( x - 1f ) < 1e-3f ) && MathF.Abs( weights[0] - 6f / 18f ) < 1e-3f,
			error ?? $"{weights.Count} weights, first {(weights.Count > 0 ? weights[0] : -1):0.000}" );
	}

	// --- corrective morphs --------------------------------------------------------------------------

	/// <summary>A tube of radius 1 along +Y, 0 to 8, a ring every 0.25 in; "upper" 0–4 and "lower" 4–8,
	/// weights ramping across y 3..5 (1 radius either side of the knee).</summary>
	static (PolyMesh, Skeleton) Limb()
	{
		var rings = new List<List<Vec3>>();
		for ( var y = 0f; y <= 8.001f; y += 0.25f )
			rings.Add( Enumerable.Range( 0, 24 ).Select( k => { var a = k * MathF.PI * 2f / 24f; return new Vec3( MathF.Cos( a ), y, MathF.Sin( a ) ); } ).ToList() );
		var mesh = Skinner.Skin( rings, true, 0 );

		var skeleton = new Skeleton();
		var upper = skeleton.AddBoneFromPoints( "upper", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 4, 0 ) );
		skeleton.AddBoneFromPoints( "lower", upper, new Vec3( 0, 4, 0 ), new Vec3( 0, 8, 0 ) );

		mesh.Skin = new SkinWeights( mesh.VertexCount );
		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var t = Math.Clamp( (mesh.Positions[v].y - 3f) / 2f, 0f, 1f );
			mesh.Skin.Vertices[v] = t <= 0f ? new[] { new BoneWeight( 0, 1f ) } : t >= 1f ? new[] { new BoneWeight( 1, 1f ) }
				: new[] { new BoneWeight( 0, 1f - t ), new BoneWeight( 1, t ) };
		}
		return (mesh, skeleton);
	}

	/// <summary>The knee ring's cross-section after bending, as a fraction of the rest one: its two
	/// half-widths (in the bend plane, and along the hinge) over the radius, multiplied.</summary>
	static float RingArea( PolyMesh mesh, Vec3[] bent, Vec3 joint, Vec3 axis )
	{
		var ring = Enumerable.Range( 0, mesh.VertexCount ).Where( v => MathF.Abs( mesh.Positions[v].y - 4f ) < 1e-3f ).ToList();
		var inPlane = ring.Max( v => { var o = bent[v] - joint; return (o - axis * Vec3.Dot( o, axis )).Length; } );
		var along = ring.Max( v => MathF.Abs( Vec3.Dot( bent[v] - joint, axis ) ) );
		return inPlane * along;
	}

	static void TestCorrectiveRestoresVolume()
	{
		var (mesh, skeleton) = Limb();
		var axis = CorrectiveMorph.AutoAxis( skeleton, 1 );
		Check( "a straight limb along Y bends about Z (folds toward the front, +X)", MathF.Abs( MathF.Abs( axis.z ) - 1f ) < 1e-4f, axis.ToString() );

		var joint = new Vec3( 0, 4, 0 );
		foreach ( var angle in new[] { 90f, 140f } )
		{
			var plain = RingArea( mesh, CorrectiveMorph.Bend( mesh, skeleton, "lower", angle, axis ), joint, axis );
			var shape = new CorrectiveShape { Bone = "lower", Angle = angle };
			var deltas = CorrectiveMorph.Deltas( mesh, skeleton, shape );
			var fixedArea = RingArea( mesh, CorrectiveMorph.Bend( mesh, skeleton, "lower", angle, axis, deltas ), joint, axis );
			var expect = MathF.Cos( angle * MathF.PI / 360f );
			Check( $"{angle:0}°: plain skinning keeps cos(A/2) = {expect:0.00} of the knee ring", MathF.Abs( plain - expect ) < 0.03f, $"{plain:0.000}" );
			Check( $"{angle:0}°: with the corrective shape it keeps all of it", MathF.Abs( fixedArea - 1f ) < 0.02f, $"{fixedArea:0.000}" );
		}

		var d = CorrectiveMorph.Deltas( mesh, skeleton, new CorrectiveShape { Bone = "lower", Angle = 90f } );
		var outside = Enumerable.Range( 0, mesh.VertexCount ).Where( v => mesh.Positions[v].y < 3f || mesh.Positions[v].y > 5f ).All( v => d[v].LengthSquared == 0f );
		var alongHinge = Enumerable.Range( 0, mesh.VertexCount ).All( v => MathF.Abs( Vec3.Dot( d[v], axis ) ) < 1e-5f );
		Check( "the shape stays in the blend zone and never moves along the hinge", outside && alongHinge );
		Check( "drive: 0 straight, 1 at its angle, about 0.39 at 60° for a 90° shape",
			CorrectiveMorph.WeightAt( 0f, 90f ) == 0f && MathF.Abs( CorrectiveMorph.WeightAt( 90f, 90f ) - 1f ) < 1e-5f
			&& MathF.Abs( CorrectiveMorph.WeightAt( 60f, 90f ) - 0.39f ) < 0.02f, $"{CorrectiveMorph.WeightAt( 60f, 90f ):0.000}" );
	}

	static void TestCorrectiveScriptAndExport( string dir )
	{
		if ( Directory.Exists( dir ) ) Directory.Delete( dir, true );
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Primitive name=Leg shape=Cylinder radius=2 height=20 segments=16 position=0,0,10\nadd Subdivide levels=2" );
		var hip = studio.Rig.AddBoneFromPoints( "hip", -1, new Vec3( 0, 0, 20 ), new Vec3( 0, 0, 10 ) );
		studio.Rig.AddBoneFromPoints( "knee", hip, new Vec3( 0, 0, 10 ), new Vec3( 0, 0, 0 ) );

		var bad = BuildScript.Run( studio, "corrective bone=nope angle=90" );
		Check( "a bone that isn't there is refused, naming the bones", !bad.Ok && bad.ToString().Contains( "knee" ), bad.ToString() );

		var add = BuildScript.Run( studio, "corrective bone=knee angle=90" );
		Check( "corrective adds a shape to the rig", r.Ok && add.Ok && studio.Correctives.Count == 1 && studio.Correctives[0].MorphName == "knee_bend90", add.ToString() );

		var back = StudioDocument.Read( StudioDocument.Write( studio ) );
		Check( "it is saved with the rig", back.Correctives.Count == 1 && back.Correctives[0].Bone == "knee" && back.Correctives[0].Angle == 90f );

		var exp = BuildScript.Run( studio, "export models/test/leg", dir );
		var dmxPath = Path.Combine( dir, "models/test/leg/leg.dmx" );
		var dmx = File.Exists( dmxPath ) ? File.ReadAllText( dmxPath ) : "";
		Check( "the export carries it as a morph in the DMX", exp.Ok && dmx.Contains( "\"knee_bend90\"" ) && dmx.Contains( "\"deltaStates\"" ), exp.ToString() );
		Check( "and logs how many vertices it moves", exp.ToString().Contains( "corrective shape knee_bend90:" ) && !exp.ToString().Contains( "nothing to correct" ), exp.ToString() );

		var rm = BuildScript.Run( studio, "corrective remove bone=knee" );
		Check( "corrective remove takes it off", rm.Ok && studio.Correctives.Count == 0 );
	}
}
