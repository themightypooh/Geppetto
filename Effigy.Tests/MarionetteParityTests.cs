using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The 2026-10-16 Blender-parity work on Marionette (docs/progress/marionette-parity.md): Limit
/// constraints everywhere, soft-chain collision and rope, shape-key curves, rigging verbs.
/// </summary>
public static class MarionetteParityTests
{
	public static void Run()
	{
		Section( "Marionette: limit constraints everywhere" );
		TestLimitAngles();
		TestLimitRead();
		TestLimitOnRead();
		TestLimitInsideIk();

		Section( "Marionette: soft chains collide, ropes drape" );
		TestSoftGround();
		TestSoftCapsule();
		TestRopeDrapes();
		TestRopePinned();
		TestSoftLimits();
		TestSoftFromRig();

		Section( "Marionette: keyed morph curves" );
		TestMorphCurves();

		Section( "Effigy: shape keys" );
		TestShapeKeyFeatures();
		TestShapeKeyMeshOps();
		TestShapeKeyExport();
	}

	const string Ball = "add Primitive name=Sac shape=Sphere radius=10 divisions=10\n";

	static (PartStudio Studio, BuildScript.Result Result) Run( string script )
	{
		var studio = new PartStudio();
		return (studio, BuildScript.Run( studio, script ));
	}

	static void TestShapeKeyFeatures()
	{
		var (plain, _) = Run( Ball );
		var (keyed, r) = Run( Ball + "add ShapeKey name=Swell key=sac_swell bodies=Sac\n"
			+ "add Stroke bodies=Sac brush=Inflate radius=6 strength=1 points=0,0,10;0,0,9\n"
			+ "add ShapeKeyEnd\n" );
		var mesh = keyed.ToMesh();
		var basePositions = plain.ToMesh().Positions;
		var morph = mesh.ValidMorphs.FirstOrDefault();

		Check( "Shape Key + a sculpt Stroke + End Shape Key makes a morph", r.Ok && morph?.Name == "sac_swell", r.ToString() );
		Check( "the body itself keeps its base shape", mesh.Positions.Zip( basePositions ).All( p => (p.First - p.Second).Length < 1e-4f ) );
		Check( "and the key holds the stroke's movement", morph is not null && morph.Deltas.Max( d => d.Length ) > 0.2f, $"{morph?.Deltas.Max( d => d.Length ):0.###}" );

		var (detail, rd) = Run( Ball + "add ShapeKey key=pox bodies=Sac\nadd Detail bodies=Sac pass=Pustules size=2 depth=1\nadd ShapeKeyEnd\n" );
		Check( "Surface Detail can shape a key too", rd.Ok && detail.ToMesh().ValidMorphs.Any( m => m.Name == "pox" && m.Deltas.Any( d => d.Length > 0.01f ) ), rd.ToString() );

		var (two, r2) = Run( Ball + "add ShapeKey key=a bodies=Sac\nadd Stroke bodies=Sac brush=Inflate radius=6 strength=1 points=0,0,10;0,0,9\nadd ShapeKeyEnd\n"
			+ "add ShapeKey key=b bodies=Sac\nadd Stroke bodies=Sac brush=Inflate radius=6 strength=1 points=0,0,-10;0,0,-9\nadd ShapeKeyEnd\n" );
		Check( "two keys on one body", r2.Ok && two.ToMesh().ValidMorphs.Select( m => m.Name ).SequenceEqual( new[] { "a", "b" } ), r2.ToString() );

		var (open, ro) = Run( Ball + "add ShapeKey key=open bodies=Sac\nadd Stroke bodies=Sac brush=Inflate radius=6 strength=1 points=0,0,10;0,0,9\n" );
		Check( "a key never ended warns on its Shape Key", open.Features.OfType<ShapeKeyFeature>().Single().Warning?.Contains( "still open" ) == true && !open.ToMesh().HasMorphs );

		var (_, re) = Run( Ball + "add ShapeKeyEnd\n" );
		Check( "End Shape Key with nothing open says so", re.ToString().Contains( "No shape key is open" ), re.ToString() );

		var (_, rx) = Run( "add Primitive name=Box shape=Box\nadd ShapeKey key=grow bodies=Box\nadd EditOp bodies=Box op=Extrude_individual select=normal:up distance=2\nadd ShapeKeyEnd\n" );
		Check( "a key that changes the vertex count is refused, by name", rx.ToString().Contains( "vertex count" ), rx.ToString() );

		var (_, rn) = Run( Ball + "add ShapeKey key=\"bad name\" bodies=Sac\nadd ShapeKeyEnd\n" );
		Check( "a key name with a space is refused", rn.ToString().Contains( "spaces" ), rn.ToString() );

		var (survives, rs) = Run( Ball + "add ShapeKey key=s bodies=Sac\nadd Stroke bodies=Sac brush=Inflate radius=6 strength=1 points=0,0,10;0,0,9\nadd ShapeKeyEnd\n"
			+ "add Detail bodies=Sac pass=Grain size=3 depth=0.2\n" );
		Check( "a key survives features after it that rebuild the mesh", rs.Ok && survives.ToMesh().ValidMorphs.Any( m => m.Name == "s" ), rs.ToString() );
	}

	static void TestShapeKeyMeshOps()
	{
		var m = new PolyMesh( new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ) }, new[] { new Face( new[] { 0, 1, 2 }, new Vec2[3], 0 ) } );
		m.AddMorph( "lift", new[] { new Vec3( 0, 0, 1 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ) } );
		Check( "AddMorph stores offsets from the mesh", (m.Morphs[0].Deltas[0] - new Vec3( 0, 0, 1 )).Length < 1e-6f && m.Morphs[0].Deltas[1].Length < 1e-6f );
		Check( "Clone keeps it", m.Clone().ValidMorphs.Single().Deltas[0].z == 1f );

		var turned = MeshTransform.Transformed( m, Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI / 2 ) * Xform.Translate( new Vec3( 5, 0, 0 ) ) );
		Check( "a transform turns the offsets and does not move them", (turned.Morphs[0].Deltas[0] - new Vec3( 0, -1, 0 )).Length < 1e-5f, $"{turned.Morphs[0].Deltas[0]}" );

		var other = new PolyMesh( new[] { new Vec3( 3, 0, 0 ), new Vec3( 4, 0, 0 ), new Vec3( 3, 1, 0 ) }, new[] { new Face( new[] { 0, 1, 2 }, new Vec2[3], 0 ) } );
		var merged = m.Clone();
		MeshTransform.Append( merged, other );
		Check( "Append pads a morph with no offset on the merged body", merged.ValidMorphs.Single().Deltas.Length == 6 && merged.Morphs[0].Deltas[4].Length == 0f );
		Check( "a stale morph (vertex count changed) is not exported", !new PolyMesh { Positions = { Vec3.Zero }, Morphs = { new MeshMorph { Name = "x", Deltas = new Vec3[3] } } }.HasMorphs );
	}

	static void TestShapeKeyExport()
	{
		var (studio, _) = Run( Ball + "add ShapeKey key=sac_swell bodies=Sac\nadd Stroke bodies=Sac brush=Inflate radius=6 strength=1 points=0,0,10;0,0,9\nadd ShapeKeyEnd\n" );
		var mesh = studio.ToMesh();
		var dmx = DmxWriter.Write( mesh );
		Check( "the DMX carries the key as a delta state", dmx.Contains( "\"DmeVertexDeltaData\"" ) && dmx.Contains( "\"deltaStates\"" ) && dmx.Contains( "\"position$0Indices\"" ) );
		Check( "and a combination operator control named for it", dmx.Contains( "\"combinationOperator\" \"DmeCombinationOperator\"" ) && dmx.Contains( "\"DmeCombinationInputControl\"" ) && dmx.Contains( "\"sac_swell\"" ) );
		Check( "it still parses as KeyValues2", DmxGrammarTests.Parse( dmx, out var e ) is not null, e );

		var none = new PolyMesh( mesh.Positions, mesh.Faces );
		Check( "a mesh without keys writes neither", !DmxWriter.Write( none ).Contains( "delta" ) && !DmxWriter.Write( none ).Contains( "combinationOperator" ) );

		var vmat = "// THIS FILE IS AUTO-GENERATED by Effigy\nLayer0\n{\n\tshader \"shaders/complex.shader\"\n\tg_flMetalness \"0\"\n}\n";
		var on = VmatMorph.Enable( vmat );
		Check( "F_MORPH_SUPPORTED goes on an Effigy vmat, under its shader line", on is not null && on.Contains( "F_MORPH_SUPPORTED 1" ) && on.IndexOf( "F_MORPH" ) > on.IndexOf( "shader \"" ) && VmatMorph.Enable( on ) == on );
		Check( "and never on a vmat Effigy did not write", VmatMorph.Enable( "Layer0\n{\n\tshader \"shaders/complex.shader\"\n}\n" ) is null );
	}

	static void TestMorphCurves()
	{
		var json = "{\"AnimationSpeed\":30,\"FrameCount\":31,\"BoneTracks\":[{\"BoneName\":\"gape\",\"Target\":\"Morph\",\"Keyframes\":["
			+ "{\"Frame\":0,\"Interpolation\":\"Linear\",\"Local\":{\"Position\":\"0,0,0\",\"Rotation\":\"0,0,0,1\",\"Scale\":\"0,0,0\"}},"
			+ "{\"Frame\":20,\"Interpolation\":\"Linear\",\"Local\":{\"Position\":\"0,0,0\",\"Rotation\":\"0,0,0,1\",\"Scale\":\"1,1,1\"}}]},"
			+ "{\"BoneName\":\"root\",\"Target\":\"Bone\",\"Keyframes\":[{\"Frame\":0,\"Local\":{\"Position\":\"0,0,0\",\"Rotation\":\"0,0,0,1\",\"Scale\":\"1,1,1\"}},"
			+ "{\"Frame\":30,\"Local\":{\"Position\":\"0,0,0\",\"Rotation\":\"0,0,0,1\",\"Scale\":\"1,1,1\"}}]}],"
			+ "\"MorphEvents\":[{\"Name\":\"swell\",\"MorphName\":\"\",\"StartFrame\":10,\"EndFrame\":20,\"Value\":0.8,\"FadeIn\":0,\"FadeOut\":0},"
			+ "{\"Name\":\"x\",\"MorphName\":\"gape\",\"StartFrame\":25,\"EndFrame\":26,\"Value\":0.3}]}";
		var clip = RigAnimFile.Read( json );

		Check( "a Morph track reads as one", clip.Tracks.Any( t => t.Kind == RigTrackKind.Morph && t.Name == "gape" ) );
		Check( "its curve gives the value between keys", MathF.Abs( clip.MorphWeightsAt( 10 )["gape"] - 0.5f ) < 1e-4f );
		Check( "a morph event (no Morph Name: its Name) holds its value", MathF.Abs( clip.MorphWeightsAt( 15 )["swell"] - 0.8f ) < 1e-4f && !clip.MorphWeightsAt( 5 ).ContainsKey( "swell" ) );
		Check( "an event overrides the curve while active, as in Marionette", MathF.Abs( clip.MorphWeightsAt( 25 )["gape"] - 0.3f ) < 1e-4f );

		var s = new Skeleton();
		s.AddBone( "root", -1, Xform.Identity );
		var baked = clip.ToClip( s );
		Check( "the baked clip carries every morph, one value per frame", baked.Morphs.Count == 2 && baked.Morphs.All( m => m.Value.Count == baked.FrameCount )
			&& MathF.Abs( baked.MorphAt( "gape", 20 ) - 1f ) < 1e-4f && baked.MorphAt( "swell", 2 ) == 0f, $"{baked.Morphs.Count} morphs, {baked.FrameCount} frames" );

		var dmx = DmxAnimWriter.Write( s, baked );
		Check( "the DMX writes a flex channel per morph (Facepunch's layout)", dmx.Contains( "\"gape_flex_channel\"" ) && dmx.Contains( "\"toAttribute\" \"string\" \"flexWeight\"" )
			&& dmx.Contains( "\"flexWeight\" \"float\" \"0\"" ) && dmx.Contains( "\"swell_flex_channel\"" ) );

		Check( "and still parses as KeyValues2", DmxGrammarTests.Parse( dmx, out var grammar ) is not null, grammar );

		var plain = new RigAnimFile { FrameRate = 30 };
		plain.Tracks.Add( Track( "root", RigTrackKind.Bone, Key( 0, RigPose.Identity ), Key( 5, RigPose.Identity ) ) );
		Check( "a clip with no morphs writes no flex channel", !DmxAnimWriter.Write( s, plain.ToClip( s ) ).Contains( "flex" ) );

		var written = RigAnimFile.Read( RigAnimWriter.Write( clip, json ) );
		Check( "the writer keeps the morph track", written.Tracks.Any( t => t.Kind == RigTrackKind.Morph && t.Keys.Count == 2 ) );
	}

	/// <summary>A body bone standing up the Z axis 40 tall, and a cable of <paramref name="bones"/>
	/// bones of 8 laid out along +X from (<paramref name="x"/>, 0, 40). Rotations identity.</summary>
	static (Skeleton S, int Body, int[] Cable) CableRig( int bones = 6, float x = 5f, float dir = 1f )
	{
		var s = new Skeleton();
		var root = s.AddBone( "root", -1, Xform.Identity );
		var body = s.AddBone( "body", root, Xform.Identity );
		s.AddBone( "body_top", body, Xform.Translate( new Vec3( 0, 0, 40 ) ) );
		var cable = new int[bones];
		var parent = s.AddBone( "anchor", root, Xform.Translate( new Vec3( x, 0, 40 ) ) );

		for ( var k = 0; k < bones; k++ )
			parent = cable[k] = s.AddBone( $"c{k}", parent, Xform.Translate( k == 0 ? Vec3.Zero : new Vec3( 8 * dir, 0, 0 ) ) );

		s.AddBone( "c_tip", parent, Xform.Translate( new Vec3( 8 * dir, 0, 0 ) ) );
		return (s, body, cable);
	}

	static List<Xform[]> Still( Skeleton s, int frames ) =>
		Enumerable.Range( 0, frames ).Select( _ => s.Bones.Select( b => b.Local ).ToArray() ).ToList();

	static Dictionary<int, SoftBone> Soft( IEnumerable<int> bones, ChainSpring.Preset preset ) => bones.ToDictionary( b => b, _ => preset.ToSoftBone() );

	/// <summary>The cable and its tip, as a bake's subtree would be - a bone left out of the soft set
	/// on a rope's line is taken for a live IK bone and pins the rope.</summary>
	static int[] WithTip( Skeleton s, int[] cable ) => cable.Append( s.IndexOf( "c_tip" ) ).ToArray();

	static void TestSoftGround()
	{
		var (s, _, cable) = CableRig();
		var heavy = ChainSpring.FindPreset( "rope" );
		var free = ChainSpring.Bake( s, Still( s, 90 ), Soft( WithTip( s, cable ), heavy ), 30 );
		var floored = ChainSpring.Bake( s, Still( s, 90 ), Soft( WithTip( s, cable ), heavy ), 30, options: new ChainSpring.Options { Ground = 20f, Thickness = 1f, Ropes = { new( cable[0] ) } } );
		var freeRope = ChainSpring.Bake( s, Still( s, 90 ), Soft( WithTip( s, cable ), heavy ), 30, options: new ChainSpring.Options { Ropes = { new( cable[0] ) } } );

		float Lowest( Xform[] local ) => RigAnimFile.ModelSpace( s, local ).Skip( cable[0] ).Min( x => x.Origin.z );

		Check( "with no floor a hanging rope falls past z=20", Lowest( freeRope[^1] ) < 15f, $"lowest {Lowest( freeRope[^1] ):0.#}" );
		Check( "with a floor at 20 every joint stays above it (thickness 1)", Enumerable.Range( 0, 90 ).All( f => Lowest( floored[f] ) >= 20.5f ),
			$"lowest {Enumerable.Range( 0, 90 ).Min( f => Lowest( floored[f] ) ):0.##}" );
		Check( "and lies on it, not hovering", Lowest( floored[^1] ) < 23f, $"lowest {Lowest( floored[^1] ):0.##}" );
		Check( "the plain spring bake is unchanged by an empty Options", free.Zip( ChainSpring.Bake( s, Still( s, 90 ), Soft( WithTip( s, cable ), heavy ), 30, options: new ChainSpring.Options() ) )
			.All( p => p.First.Zip( p.Second ).All( q => (q.First.Origin - q.Second.Origin).Length < 1e-5f && (q.First.X - q.Second.X).Length < 1e-5f ) ) );
	}

	static void TestSoftCapsule()
	{
		// The cable starts at x=5 beside a body of radius 8 around the Z axis and falls along it.
		var (s, body, cable) = CableRig( bones: 4, x: 9.5f );
		var rope = ChainSpring.FindPreset( "rope" );
		var options = new ChainSpring.Options { Thickness = 1f, Ropes = { new( cable[0] ) }, Capsules = { new( body, 8f ) } };
		var hit = ChainSpring.Bake( s, Still( s, 120 ), Soft( WithTip( s, cable ), rope ), 30, options: options );
		var miss = ChainSpring.Bake( s, Still( s, 120 ), Soft( WithTip( s, cable ), rope ), 30, options: new ChainSpring.Options { Ropes = { new( cable[0] ) } } );

		// Joints below the top bone (its tail hangs against the body by design).
		float Closest( Xform[] local ) => RigAnimFile.ModelSpace( s, local ).Skip( cable[2] ).Take( cable.Length - 1 )
			.Where( x => x.Origin.z is >= 0 and <= 40 )
			.Select( x => MathF.Sqrt( x.Origin.x * x.Origin.x + x.Origin.y * x.Origin.y ) ).DefaultIfEmpty( 99f ).Min();

		Check( "without the capsule the swinging rope passes inside the body's radius", miss.Min( Closest ) < 8.5f, $"{miss.Min( Closest ):0.##}" );
		Check( "with it every joint stays outside radius + thickness", Enumerable.Range( 0, 120 ).All( f => Closest( hit[f] ) >= 8.9f ),
			$"closest {Enumerable.Range( 0, 120 ).Min( f => Closest( hit[f] ) ):0.##}" );

		// Springs too, not just ropes: a cable whose keyed pose points into the body.
		var (s2, body2, cable2) = CableRig( bones: 3, x: 20f, dir: -1f );
		var loose = ChainSpring.FindPreset( "loose" );
		var spring = ChainSpring.Bake( s2, Still( s2, 120 ), Soft( WithTip( s2, cable2 ), loose ), 30,
			options: new ChainSpring.Options { Thickness = 1f, Capsules = { new( body2, 8f ) } } );
		s = s2;
		cable = cable2;
		Check( "a spring chain's tails stay outside it too", Enumerable.Range( 0, 120 ).All( f => Closest( spring[f] ) >= 8.85f ),
			$"closest {Enumerable.Range( 0, 120 ).Min( f => Closest( spring[f] ) ):0.##}" );
	}

	static void TestRopeDrapes()
	{
		var (s, _, cable) = CableRig( bones: 6 );
		var length = 6 * 8f;
		var rope = ChainSpring.Bake( s, Still( s, 150 ), Soft( WithTip( s, cable ), ChainSpring.FindPreset( "rope" ) ), 30,
			options: new ChainSpring.Options { Ropes = { new( cable[0] ) } } );
		var spring = ChainSpring.Bake( s, Still( s, 150 ), Soft( WithTip( s, cable ), ChainSpring.FindPreset( "loose" ) ), 30 );

		var world = RigAnimFile.ModelSpace( s, rope[^1] );
		var tip = world[s.IndexOf( "c_tip" )].Origin;
		var springTip = RigAnimFile.ModelSpace( s, spring[^1] )[s.IndexOf( "c_tip" )].Origin;

		Check( "a rope hangs: its tip ends near a full length below the root", tip.z < 40f - length * 0.9f, $"tip z {tip.z:0.#}" );
		Check( "where the loose spring (60° cone) holds it out", springTip.z > tip.z + 10f, $"spring tip z {springTip.z:0.#}" );

		var lengths = Enumerable.Range( 0, cable.Length ).Select( k => (world[k + 1 < cable.Length ? cable[k + 1] : s.IndexOf( "c_tip" )].Origin - world[cable[k]].Origin).Length );
		Check( "and every bone keeps its length", lengths.All( l => MathF.Abs( l - 8f ) < 1e-3f ) );
	}

	static void TestRopePinned()
	{
		// The cable's c4 head is pinned where the clip puts it: 20 along, level with the root -
		// closer than the 32 of rope between them, so the rope has to sag.
		var (s, _, cable) = CableRig( bones: 6 );
		var frames = Still( s, 150 );
		var pin = cable[4];

		foreach ( var f in frames )
		{
			// Fold c0 so the animated c4 lands at x=5+20: turn c0..c3 into a zigzag via c0 and c2.
			f[cable[1]] = Xform.Translate( new Vec3( 8, 0, 0 ) ) * Xform.Rotate( new Vec3( 0, 0, 1 ), 1.2f );
			f[cable[3]] = Xform.Translate( new Vec3( 8, 0, 0 ) ) * Xform.Rotate( new Vec3( 0, 0, 1 ), -1.2f );
		}

		var animatedPin = RigAnimFile.ModelSpace( s, frames[0] )[pin].Origin;
		var soft = Soft( cable.Take( 4 ), ChainSpring.FindPreset( "rope" ) );
		var baked = ChainSpring.Bake( s, frames, soft, 30, options: new ChainSpring.Options { Ropes = { new( cable[0], pin ) } } );
		var world = RigAnimFile.ModelSpace( s, baked[^1] );
		var sag = cable.Take( 4 ).Skip( 1 ).Min( b => world[b].Origin.z );

		Check( "a rope pinned at both ends sags between them", sag < 40f - 8f, $"lowest joint z {sag:0.#}" );
		Check( "and its pinned end stays where the clip put it", (world[pin].Origin - animatedPin).Length < 0.75f, $"off by {(world[pin].Origin - animatedPin).Length:0.##}" );
		Check( "the pinned bone keeps the clip's rotation", (world[pin].X - RigAnimFile.ModelSpace( s, frames[0] )[pin].X).Length < 1e-3f );
	}

	static void TestSoftLimits()
	{
		var (s, _, cable) = CableRig( bones: 4 );
		var limits = new List<RigLimit> { new() { Bone = "c1", Enabled = true, Min = new Vec3( -10, -10, -10 ), Max = new Vec3( 10, 10, 10 ) } };
		var free = ChainSpring.Bake( s, Still( s, 60 ), Soft( cable, ChainSpring.FindPreset( "heavy" ) ), 30 );
		var held = ChainSpring.Bake( s, Still( s, 60 ), Soft( cable, ChainSpring.FindPreset( "heavy" ) ), 30, options: new ChainSpring.Options { Limits = limits } );

		Check( "a swung bone breaks a 10° limit without it", !Inside( limits[0], Quat.FromXform( free[^1][cable[1]] ) ) );
		Check( "and stays inside it with the rig's limits", Enumerable.Range( 0, 60 ).All( f => Inside( limits[0], Quat.FromXform( held[f][cable[1]] ) ) ) );
	}

	static void TestSoftFromRig()
	{
		var json = "{\"SoftColliders\":[{\"Bone\":\"body\",\"Radius\":8,\"Enabled\":true},{\"Bone\":\"off\",\"Radius\":3,\"Enabled\":false}],"
			+ "\"Ground\":true,\"GroundHeight\":2.5,\"SoftThickness\":1.5}";
		var c = RigSoftCollision.ReadRig( json );
		Check( "the rig's soft colliders, floor and thickness read", c.Capsules.Count == 1 && c.Capsules[0] == ("body", 8f) && c.Ground == 2.5f && c.Thickness == 1.5f );
		Check( "a rig without them collides with nothing", !RigSoftCollision.ReadRig( "{}" ).Any );

		var clip = new RigAnimFile();
		clip.UseRig( json );
		Check( "UseRig takes them too", clip.Collision.Any );

		var same = Marionette.RigSpringPresets.All.Length == ChainSpring.Presets.Length
			&& Marionette.RigSpringPresets.All.Zip( ChainSpring.Presets ).All( p => p.First.Rope == p.Second.Rope );
		Check( "the player's rope preset is the bake's", same && ChainSpring.Presets[^1].Rope );

		// The headless bake on the tentacle stand-in: a rope pinned at an IK hand bakes the IK chain too.
		var d = SimRig.Tentacle( cables: 1, bones: 8, fingers: false );
		var s = d.Skeleton;
		var hand = d.BindWorld[s.IndexOf( "cable0_hand" )].Origin;
		var anim = new RigAnimFile { FrameRate = 30, FrameCount = 41 };
		anim.Tracks.Add( Track( "heap", RigTrackKind.Bone, Key( 0, Bind( s, "heap" ) ), Key( 40, Bind( s, "heap" ) with { Position = Bind( s, "heap" ).Position + new Vec3( 10, 0, 0 ) } ) ) );
		anim.Tracks.Add( Track( "cable0_hand", RigTrackKind.IkGoal, Key( 0, new RigPose( hand, Quat.Identity, Vec3.One ) ) ) );
		anim.Ik = new List<RigIk> { new() { EndBone = "cable0_hand", ChainLength = 4, Fabrik = true, GoalRotation = false, Live = true } };
		anim.Collision = new RigSoftCollision { Ground = 0f };

		var plain = ClipBake.Secondary( anim, s, "cable0_00", ChainSpring.FindPreset( "loose" ).ToSoftBone(), false );
		Check( "a spring bake skips the IK chain", plain.Ok && plain.SkippedForIk.Count == 4, $"{plain.SkippedForIk.Count}" );

		var pinned = ClipBake.Secondary( anim, s, "cable0_00", ChainSpring.FindPreset( "rope" ).ToSoftBone(), false, rope: true, pin: "cable0_hand" );
		Check( "a rope pinned at the hand bakes its IK chain too", pinned.Ok && pinned.SkippedForIk.Count == 0, pinned.Error ?? string.Join( ",", pinned.SkippedForIk ) );

		var reached = (RigAnimFile.ModelSpace( s, anim.SampleLocal( s, 40 ) )[s.IndexOf( "cable0_hand" )].Origin - hand).Length;
		Check( "and the hand is still on its goal at playback", reached < 0.05f, $"{reached:0.###}" );
		Check( "a pin off the line is refused", !ClipBake.Secondary( anim, s, "cable0_00", ChainSpring.FindPreset( "rope" ).ToSoftBone(), false, rope: true, pin: "heap" ).Ok );
	}

	static RigKey Key( int frame, RigPose pose, RigEase ease = RigEase.Monotone ) => new() { Frame = frame, Local = pose, Ease = ease };

	static RigTrack Track( string name, RigTrackKind kind, params RigKey[] keys ) => new() { Name = name, Kind = kind, Keys = keys.ToList() };

	static RigPose Bind( Skeleton s, string bone ) => RigPose.FromXform( s.Bones[s.IndexOf( bone )].Local );

	static float AngleBetween( Quat a, Quat b ) =>
		2f * MathF.Acos( Math.Clamp( MathF.Abs( a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w ), 0f, 1f ) ) * 180f / MathF.PI;

	/// <summary>A limit of ±<paramref name="range"/>° about the bone's own bind angles.</summary>
	static RigLimit AroundBind( Skeleton s, string bone, float range )
	{
		var (p, y, r) = RigLimit.ToAngles( Quat.FromXform( s.Bones[s.IndexOf( bone )].Local ) );
		return new RigLimit { Bone = bone, Enabled = true, Min = new Vec3( p - range, y - range, r - range ), Max = new Vec3( p + range, y + range, r + range ) };
	}

	static bool Inside( RigLimit l, Quat q, float slack = 0.05f )
	{
		var (p, y, r) = RigLimit.ToAngles( q );
		return p >= l.Min.x - slack && p <= l.Max.x + slack && y >= l.Min.y - slack && y <= l.Max.y + slack && r >= l.Min.z - slack && r <= l.Max.z + slack;
	}

	static void TestLimitAngles()
	{
		var rng = new Random( 7 );
		var worst = 0f;

		for ( var i = 0; i < 200; i++ )
		{
			var (p, y, r) = (rng.NextSingle() * 170f - 85f, rng.NextSingle() * 340f - 170f, rng.NextSingle() * 340f - 170f);
			var back = RigLimit.ToAngles( Quat.FromAngles( p, y, r ) );
			worst = MathF.Max( worst, MathF.Max( MathF.Abs( back.Pitch - p ), MathF.Max( MathF.Abs( back.Yaw - y ), MathF.Abs( back.Roll - r ) ) ) );
		}

		Check( "ToAngles inverts FromAngles (s&box's Angles()/From)", worst < 0.01f, $"worst {worst:0.####}°" );

		var limit = new List<RigLimit> { new() { Bone = "b", Enabled = true, Min = new Vec3( -10, -20, -30 ), Max = new Vec3( 10, 20, 30 ) } };
		var clamped = RigLimit.ToAngles( RigLimit.Clamp( limit, "b", Quat.FromAngles( 40, -50, 5 ) ) );
		Check( "a rotation past the limit is clamped per axis", MathF.Abs( clamped.Pitch - 10 ) < 0.01f && MathF.Abs( clamped.Yaw + 20 ) < 0.01f && MathF.Abs( clamped.Roll - 5 ) < 0.01f,
			$"{clamped}" );

		var inside = Quat.FromAngles( 5, 5, 5 );
		Check( "one inside it is untouched", AngleBetween( RigLimit.Clamp( limit, "b", inside ), inside ) < 0.1f );
		Check( "another bone is untouched", AngleBetween( RigLimit.Clamp( limit, "c", Quat.FromAngles( 40, 0, 0 ) ), Quat.FromAngles( 40, 0, 0 ) ) < 0.1f );

		limit[0].Weight = 0.5f;
		var soft = RigLimit.ToAngles( RigLimit.Clamp( limit, "b", Quat.FromAngles( 40, 0, 0 ) ) );
		Check( "weight 0.5 is a soft stop halfway", soft.Pitch > 20 && soft.Pitch < 30, $"pitch {soft.Pitch:0.##}" );

		limit[0].Enabled = false;
		Check( "a disabled limit does nothing", AngleBetween( RigLimit.Clamp( limit, "b", Quat.FromAngles( 40, 0, 0 ) ), Quat.FromAngles( 40, 0, 0 ) ) < 0.1f );
	}

	static void TestLimitRead()
	{
		var json = "{\"IkConstraints\":[{\"TargetBone\":\"hand\",\"Live\":true}],\"LimitConstraints\":[{\"Name\":\"elbow\",\"TargetBone\":\"arm_lower\",\"Weight\":0.75,\"Enabled\":true,"
			+ "\"MinAngles\":\"-10,-20,-30\",\"MaxAngles\":\"10,20,140\"},{\"TargetBone\":\"x\"}]}";
		var limits = RigLimit.ReadRig( json );
		Check( "limits read from a .ctrlrig", limits.Count == 2 && limits[0].Bone == "arm_lower" && limits[0].Weight == 0.75f && limits[0].Enabled
			&& limits[0].Min.y == -20 && limits[0].Max.z == 140 );
		Check( "one without Enabled reads as off, as in Marionette", !limits[1].Enabled );

		var clip = new RigAnimFile();
		clip.UseRig( json );
		Check( "UseRig takes both the IK and the limits", clip.Ik.Count == 1 && clip.Limits.Count == 2 );
	}

	static void TestLimitOnRead()
	{
		var d = SimRig.Tentacle( cables: 1, bones: 6, fingers: false );
		var s = d.Skeleton;
		var bone = "cable0_02";
		var bind = Bind( s, bone );
		var bent = bind with { Rotation = Quat.FromAxisAngle( new Vec3( 0, 1, 0 ), 1.2f ) * bind.Rotation };

		var clip = new RigAnimFile { FrameRate = 30, FrameCount = 11 };
		clip.Tracks.Add( Track( bone, RigTrackKind.Bone, Key( 0, bind ), Key( 10, bent ) ) );

		var free = clip.SampleLocal( s, 10 )[s.IndexOf( bone )];
		var limit = AroundBind( s, bone, 15 );
		clip.Limits = new List<RigLimit> { limit };
		var held = clip.SampleLocal( s, 10 )[s.IndexOf( bone )];

		Check( "a keyed bone is clamped as the clip is read (sim, take, bake)", !Inside( limit, Quat.FromXform( free ) ) && Inside( limit, Quat.FromXform( held ) ) );
		Check( "and the clamp keeps the key's position", (held.Origin - free.Origin).Length < 1e-4f );

		var baked = clip.ToClip( s );
		Check( "the baked export clip is clamped the same way", AngleBetween( Quat.FromXform( baked.Frames[10][s.IndexOf( bone )] ), Quat.FromXform( held ) ) < 0.01f );

		// A limit that the bind pose itself breaks: an unkeyed bone sits at its bind, clamped.
		var tight = new RigLimit { Bone = "cable0_04", Enabled = true, Min = new Vec3( 0, 0, 0 ), Max = new Vec3( 0, 0, 0 ) };
		clip.Limits = new List<RigLimit> { tight };
		var unkeyed = clip.SampleLocal( s, 0 )[s.IndexOf( "cable0_04" )];
		Check( "an unkeyed bone sits at its bind pose, clamped", Inside( tight, Quat.FromXform( unkeyed ) ) );
	}

	static void TestLimitInsideIk()
	{
		var d = SimRig.Tentacle( cables: 1, bones: 6, fingers: false );
		var s = d.Skeleton;
		var world = d.BindWorld;
		var hand = s.IndexOf( "cable0_hand" );
		var top = s.Bones[hand].Parent;

		// A goal well off to the side, in reach: the free solve bends the chain hard.
		var root = world[s.IndexOf( "cable0_00" )].Origin;
		var reach = (world[hand].Origin - root).Length;
		var goal = root + new Vec3( reach * 0.35f, reach * 0.45f, reach * 0.3f );

		foreach ( var (label, fabrik, spline, length) in new[] { ("CCD", false, false, 4), ("FABRIK", true, false, 4), ("Spline", false, true, 4), ("two-bone", false, false, 2) } )
		{
			var ik = new RigIk { EndBone = "cable0_hand", ChainLength = length, Iterations = 20, GoalRotation = false, Fabrik = fabrik, Spline = spline };
			var pose = new RigPose( goal, Quat.Identity, Vec3.One );
			var free = ChainIk.Apply( s, world, ik, pose );

			// Limit every chain bone above the end to ±12° of bind.
			var chain = new List<int> { hand };

			for ( var k = 0; k < length; k++ )
				chain.Insert( 0, s.Bones[chain[0]].Parent );

			var limits = chain.Take( length ).Select( b => AroundBind( s, s.Bones[b].Name, 12 ) ).ToList();
			var held = ChainIk.Apply( s, world, ik, pose, limits );
			var local = RigAnimFile.LocalSpace( s, held );
			var freeLocal = RigAnimFile.LocalSpace( s, free );

			var broke = chain.Take( length ).Any( b => !Inside( limits.First( l => l.Bone == s.Bones[b].Name ), Quat.FromXform( freeLocal[b] ) ) );
			var kept = chain.Take( length ).All( b => Inside( limits.First( l => l.Bone == s.Bones[b].Name ), Quat.FromXform( local[b] ) ) );
			var freeMiss = (free[hand].Origin - goal).Length;
			var heldMiss = (held[hand].Origin - goal).Length;

			Check( $"{label}: the solve stays inside the chain's limits", broke && kept, $"free broke {broke}, held kept {kept}" );
			Check( $"{label}: and still reaches as far as they let it", heldMiss >= freeMiss - 0.01f && heldMiss < (world[hand].Origin - goal).Length,
				$"miss {heldMiss:0.##} (free {freeMiss:0.##}, unsolved {(world[hand].Origin - goal).Length:0.##})" );
		}

		var plain = new RigIk { EndBone = "cable0_hand", ChainLength = 4, Iterations = 20, GoalRotation = false, Fabrik = true };
		var near = world[hand].Origin + new Vec3( 0.5f, 0.5f, 0 );
		var loose = new List<RigLimit> { new() { Bone = "cable0_03", Enabled = true, Min = new Vec3( -179, -179, -179 ), Max = new Vec3( 179, 179, 179 ) } };
		var a = ChainIk.Apply( s, world, plain, new RigPose( near, Quat.Identity, Vec3.One ) );
		var b2 = ChainIk.Apply( s, world, plain, new RigPose( near, Quat.Identity, Vec3.One ), loose );
		Check( "a limit the solve never touches changes nothing", a.Zip( b2 ).All( p => (p.First.Origin - p.Second.Origin).Length < 1e-5f ) );

		// The clip path: SampleLocal's live IK honours clip.Limits.
		var clip = new RigAnimFile { FrameRate = 30, FrameCount = 2 };
		clip.Tracks.Add( Track( "cable0_hand", RigTrackKind.IkGoal, Key( 0, new RigPose( goal, Quat.Identity, Vec3.One ) ) ) );
		clip.Ik = new List<RigIk> { new() { EndBone = "cable0_hand", ChainLength = 4, Iterations = 20, GoalRotation = false, Fabrik = true, Live = true } };
		var lim = AroundBind( s, s.Bones[top].Name, 5 );
		clip.Limits = new List<RigLimit> { lim };
		Check( "a clip's live IK stays inside the rig's limits", Inside( lim, Quat.FromXform( clip.SampleLocal( s, 0 )[top] ) ) );
	}
}
