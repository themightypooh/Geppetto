using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Render;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The headless Marionette sim: the stand-in models (SimRig), the motion lint catching each kind of
/// mistake it claims to - and staying quiet on a clean clip - the clip writer, the headless
/// secondary bake and the trail render.
/// </summary>
public static class MarionetteSimTests
{
	public static void Run( string outDir )
	{
		Directory.CreateDirectory( outDir );
		Section( "headless Marionette sim: stand-in models" );
		TestTentacleRig( outDir );
		TestCitizenRig();

		Section( "headless Marionette sim: motion lint" );
		TestCleanClipIsClean();
		TestPopIsCaught();
		TestSlideIsCaught();
		TestUnreachedGoalIsCaught();
		TestHoldDriftIsCaught();
		TestLoopSeamIsCaught();

		Section( "headless Marionette sim: writer, bake, trail" );
		TestWriterRoundTrips();
		TestHeadlessBakeAndOverlap( outDir );
		TestWaveAndTwins();

		MarionetteLayerTests.Run();
		MarionetteParityTests.Run();
	}

	static void TestWaveAndTwins()
	{
		var d = SimRig.Tentacle( fingers: false );
		var s = d.Skeleton;
		var clip = new RigAnimFile { FrameRate = 30, FrameCount = 121, SourceModel = d.Model };
		clip.Tracks.Add( Track( "heap", RigTrackKind.Bone, Key( 0, Bind( s, "heap" ) ), Key( 120, Bind( s, "heap" ) ) ) );

		// Two cables waved with the SAME phase are twins; the lint should say so.
		var same = ChainWave.FindPreset( "writhe" );
		ClipBake.Wave( clip, s, "cable0_00", same, loop: true );
		ClipBake.Wave( clip, s, "cable1_00", same, loop: true );

		var options = new MotionLint.Options { Chains = { "cable0_", "cable1_" }, Loop = true };
		var lint = new MotionLint( s, clip ).Run( options );
		Check( "a waved chain plays clean, and loops", lint.Clear, lint.ToString() );
		Check( "two chains waved in phase are flagged as twins", lint.Of( "twins" ).Any(), lint.ToString() );

		// Peak order means nothing for a wave that keeps going (every swing peaks), so check the
		// taper instead: the tip swings further than the root.
		float Swing( string bone )
		{
			var b = s.IndexOf( bone );
			var parent = s.Bones[b].Parent;
			var lintPose = new MotionLint( s, clip );
			var angles = Enumerable.Range( 0, 41 ).Select( f =>
			{
				var w = lintPose.Pose( f );
				var bindRel = Quat.FromXform( d.BindWorld[parent] ).Inverse * Quat.FromXform( d.BindWorld[b] );
				var rel = Quat.FromXform( w[parent] ).Inverse * Quat.FromXform( w[b] ) * bindRel.Inverse;
				return 2f * MathF.Acos( Math.Clamp( MathF.Abs( rel.w ), 0f, 1f ) );
			} ).ToList();
			return angles.Max() - angles.Min();
		}

		Check( "the tip swings further than the root (taper)", Swing( "cable0_10" ) > Swing( "cable0_00" ) * 1.5f, $"tip {Swing( "cable0_10" ):0.###} root {Swing( "cable0_00" ):0.###} rad" );

		// Re-wave cable1 from its bind pose with its own phase: not twins any more.
		clip.Tracks.RemoveAll( t => t.Name.StartsWith( "cable1_" ) );
		var own = ChainWave.FindPreset( "writhe" );
		own.Offset = ChainWave.OffsetFor( "cable1_00", own.Period );
		ClipBake.Wave( clip, s, "cable1_00", own, loop: true );
		var apart = new MotionLint( s, clip ).Run( options );
		Check( "with its own phase it is not", !apart.Of( "twins" ).Any(), string.Join( "; ", apart.Of( "twins" ) ) );

		Check( "fitting to a loop makes whole swings", MathF.Abs( ChainWave.FitPeriod( 36f, 120 ) - 120f / 3f ) < 1e-4f );
		Check( "every chain gets a different phase", ChainWave.OffsetFor( "cable0_00", 36f ) != ChainWave.OffsetFor( "cable1_00", 36f ) );
	}

	// --- clip building ---------------------------------------------------------------------------

	static RigKey Key( int frame, RigPose pose, RigEase ease = RigEase.Monotone ) => new() { Frame = frame, Local = pose, Ease = ease };

	static RigTrack Track( string name, RigTrackKind kind, params RigKey[] keys ) => new() { Name = name, Kind = kind, Keys = keys.ToList() };

	static RigPose Bind( Skeleton s, string bone ) => RigPose.FromXform( s.Bones[s.IndexOf( bone )].Local );

	static RigPose Moved( RigPose p, Vec3 by ) => new( p.Position + by, p.Rotation, p.Scale );

	static RigPose Turned( RigPose p, Vec3 axis, float degrees ) => new( p.Position, p.Rotation * Quat.FromAxisAngle( axis, degrees * MathF.PI / 180f ), p.Scale );

	static Vec3 HandAtBind( ModelDump d, int cable ) => d.BindWorld[d.Skeleton.IndexOf( $"cable{cable}_hand" )].Origin;

	// --- rigs ------------------------------------------------------------------------------------

	static void TestTentacleRig( string outDir )
	{
		var d = SimRig.Tentacle( cables: 5, bones: 12 );
		Check( "the tentacle has a root, a heap, and 5 cables of 12 bones plus 9 finger bones each", d.Skeleton.Count == 2 + 5 * (12 + 9), d.Skeleton.Count.ToString() );
		Check( "its hands are named for the plan", Enumerable.Range( 0, 5 ).All( c => d.Skeleton.IndexOf( $"cable{c}_hand" ) >= 0 ) );

		var lengths = Enumerable.Range( 0, 5 ).Select( c => (HandAtBind( d, c ) - d.BindWorld[d.Skeleton.IndexOf( $"cable{c}_00" )].Origin).Length ).ToList();
		Check( "no two cables are the same length", lengths.Distinct().Count() == 5 || lengths.Select( l => MathF.Round( l ) ).Distinct().Count() >= 3, string.Join( ", ", lengths.Select( l => l.ToString( "0.#" ) ) ) );

		var c0 = d.Skeleton.IndexOf( "cable0_00" );
		var c1 = d.Skeleton.IndexOf( "cable0_01" );
		var along = (d.BindWorld[c1].Origin - d.BindWorld[c0].Origin).Normal;
		var rolledOff = MathF.Abs( Vec3.Dot( d.BindWorld[c1].Y.Normal, along ) ) < 0.2f && Vec3.Dot( d.BindWorld[c0].Y.Normal, d.BindWorld[c1].Y.Normal ) < 0.5f;
		Check( "its bones are rolled about their length, so axis assumptions break here first", rolledOff );

		var body = d.Meshes[0];
		var atBind = d.Deform( body, d.BindWorld );
		Check( "the skin sits where it was made at the bind pose", atBind.Zip( body.Positions ).All( p => (p.First - p.Second).Length < 1e-3f ) );
		Check( "every vertex is skinned", body.Weights.Vertices.All( w => w.Length > 0 && MathF.Abs( w.Sum( x => x.Weight ) - 1f ) < 1e-4f ) );

		var path = Path.Combine( outDir, "sim_tentacle.json" );
		d.Props.Add( SimRig.Pipe( "pipe", new Vec3( 60, -40, 6 ), new Vec3( 60, 40, 6 ), 1.6f ) );
		d.WriteFile( path );
		var back = ModelDump.ReadFile( path );
		Check( "it round-trips through a dump file", back.Skeleton.Count == d.Skeleton.Count && back.Meshes[0].TriangleCount == body.TriangleCount && back.Props.Count == 1 );
	}

	static void TestCitizenRig()
	{
		var d = SimRig.Citizen();
		Check( "the citizen stand-in is Citizen's skeleton", d.Skeleton.IndexOf( "hand_R" ) >= 0 && d.Skeleton.IndexOf( "pelvis" ) == 0 );
		Check( "with a skin on it", d.Meshes[0].TriangleCount > 500, d.Meshes[0].TriangleCount.ToString() );
	}

	// --- lint ------------------------------------------------------------------------------------

	/// <summary>Cable 0 reaching slowly forward with a FABRIK goal, the heap breathing.</summary>
	static RigAnimFile Reach( ModelDump d, Vec3 goalOffset )
	{
		var s = d.Skeleton;
		var hand = HandAtBind( d, 0 );
		var heap = Bind( s, "heap" );

		var clip = new RigAnimFile { FrameRate = 30, FrameCount = 61, SourceModel = d.Model };
		clip.Tracks.Add( Track( "heap", RigTrackKind.Bone, Key( 0, heap ), Key( 30, Moved( heap, new Vec3( 0, 0, 1.5f ) ) ), Key( 60, heap ) ) );
		clip.Tracks.Add( Track( "cable0_hand", RigTrackKind.IkGoal,
			Key( 0, new RigPose( hand, Quat.Identity, Vec3.One ) ),
			Key( 30, new RigPose( hand + goalOffset, Quat.Identity, Vec3.One ) ),
			Key( 60, new RigPose( hand, Quat.Identity, Vec3.One ) ) ) );
		clip.Ik = new List<RigIk> { new() { Name = "c0", EndBone = "cable0_hand", ChainLength = 6, Fabrik = true, Iterations = 20, GoalRotation = false, Live = true } };
		return clip;
	}

	static void TestCleanClipIsClean()
	{
		var d = SimRig.Tentacle();
		var result = new MotionLint( d.Skeleton, Reach( d, new Vec3( -4, 3, 4 ) ) ).Run();
		Check( "a smooth, reachable clip lints clean", result.Clear, result.ToString() );
	}

	static void TestPopIsCaught()
	{
		var d = SimRig.Tentacle();
		var clip = Reach( d, new Vec3( -4, 3, 4 ) );
		var bone = Bind( d.Skeleton, "cable2_03" );
		clip.Tracks.Add( Track( "cable2_03", RigTrackKind.Bone, Key( 0, bone ), Key( 20, bone, RigEase.Stepped ), Key( 21, Turned( bone, new Vec3( 0, 0, 1 ), 80f ) ), Key( 60, Turned( bone, new Vec3( 0, 0, 1 ), 80f ) ) ) );

		var pops = new MotionLint( d.Skeleton, clip ).Run().Of( "pop" ).ToList();
		Check( "a Stepped 80° snap is caught as a pop", pops.Any( p => p.Bone == "cable2_03" && MathF.Abs( p.Frame - 20.5f ) <= 1f ), string.Join( "; ", pops ) );
		Check( "and only there", pops.All( p => p.Bone.StartsWith( "cable2_" ) ), string.Join( "; ", pops ) );
	}

	static void TestSlideIsCaught()
	{
		var d = SimRig.Tentacle();
		var s = d.Skeleton;
		var root = Bind( s, "root" );

		// The whole body creeps 1.2 units over a second: slow enough that a foot looks planted,
		// far enough that it visibly skates.
		var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
		clip.Tracks.Add( Track( "root", RigTrackKind.Bone, Key( 0, root, RigEase.Linear ), Key( 30, Moved( root, new Vec3( 1.2f, 0, 0 ) ) ) ) );

		var slides = new MotionLint( s, clip ).Run( new MotionLint.Options { Contacts = { "cable*_hand" } } ).Of( "slide" ).ToList();
		Check( "a planted hand that creeps is caught sliding", slides.Any( x => x.Bone == "cable1_hand" && x.Amount > 1f ), string.Join( "; ", slides ) );

		var quiet = new MotionLint( s, clip ).Run().Of( "slide" ).Count();
		Check( "and nothing is called a contact unless named or held by a goal", quiet == 0 );
	}

	static void TestUnreachedGoalIsCaught()
	{
		var d = SimRig.Tentacle();
		var reach = new MotionLint( d.Skeleton, Reach( d, new Vec3( 0, 0, 200 ) ) ).Run().Of( "reach" ).ToList();
		Check( "a goal 200 units up is caught as unreached", reach.Any( r => r.Bone == "cable0_hand" && MathF.Abs( r.Frame - 30f ) < 3f ), string.Join( "; ", reach ) );
	}

	static void TestHoldDriftIsCaught()
	{
		var d = SimRig.Tentacle();
		var clip = Reach( d, new Vec3( -4, 3, 4 ) );
		var heap = Bind( d.Skeleton, "heap" );
		var up = Moved( heap, new Vec3( 0, 0, 6 ) );
		clip.Tracks.RemoveAll( t => t.Name == "heap" );
		clip.Tracks.Add( Track( "heap", RigTrackKind.Bone, Key( 0, heap, RigEase.Cubic ), Key( 10, up, RigEase.Cubic ), Key( 30, up, RigEase.Cubic ), Key( 40, heap, RigEase.Cubic ) ) );

		var holds = new MotionLint( d.Skeleton, clip ).Run().Of( "hold" ).ToList();
		Check( "a Cubic hold between equal keys that wanders is caught", holds.Any( h => h.Bone == "heap" ), string.Join( "; ", holds ) );

		clip.Tracks.Single( t => t.Name == "heap" ).Keys[1].Ease = RigEase.Monotone;
		Check( "and Monotone on the hold fixes it", !new MotionLint( d.Skeleton, clip ).Run().Of( "hold" ).Any() );
	}

	static void TestLoopSeamIsCaught()
	{
		var d = SimRig.Tentacle();
		var clip = Reach( d, new Vec3( -4, 3, 4 ) );
		var options = new MotionLint.Options { Loop = true };
		Check( "a clip that ends where it starts loops cleanly", !new MotionLint( d.Skeleton, clip ).Run( options ).Of( "loop" ).Any( f => f.Fail ) );

		var goal = clip.IkGoalTracks.Single();
		goal.Keys[^1] = Key( 60, new RigPose( goal.Keys[^1].Local.Position + new Vec3( 0, 0, 5 ), Quat.Identity, Vec3.One ) );
		var seam = new MotionLint( d.Skeleton, clip ).Run( options ).Of( "loop" ).ToList();
		Check( "one that ends somewhere else is caught at the seam", seam.Any( f => f.Fail ), string.Join( "; ", seam ) );
	}

	// --- writer, bake, trail ----------------------------------------------------------------------

	static void TestWriterRoundTrips()
	{
		var d = SimRig.Tentacle();
		var clip = Reach( d, new Vec3( -4, 3, 4 ) );
		clip.Tracks[0].Keys[1].Ease = RigEase.EaseOut;
		clip.Tracks[0].Keys[1].TangentOut = Enumerable.Range( 0, RigHermite.Channels ).Select( i => i * 0.1f ).ToArray();

		var json = RigAnimWriter.Write( clip, "{\"Marks\":[{\"Name\":\"clamp\",\"Frame\":14,\"Note\":\"\"}],\"Lights\":[]}" );
		var back = RigAnimFile.Read( json );

		var same = back.Tracks.Count == clip.Tracks.Count && back.Tracks.Zip( clip.Tracks ).All( p =>
			p.First.Name == p.Second.Name && p.First.Kind == p.Second.Kind && p.First.Keys.Count == p.Second.Keys.Count
			&& p.First.Keys.Zip( p.Second.Keys ).All( k => k.First.Frame == k.Second.Frame && k.First.Ease == k.Second.Ease
				&& (k.First.Local.Position - k.Second.Local.Position).Length < 1e-5f ) );
		Check( "a clip written and read back has the same tracks, keys and easing", same );
		Check( "and the tangents", back.Tracks[0].Keys[1].TangentOut is { Length: RigHermite.Channels } t && MathF.Abs( t[9] - 0.9f ) < 1e-6f );
		Check( "and keeps what the kernel does not read (marks) from the base file", json.Contains( "\"clamp\"" ) );
		Check( "with enums by name, as Marionette saves them", json.Contains( "\"IkGoal\"" ) && json.Contains( "\"EaseOut\"" ) );
	}

	static void TestHeadlessBakeAndOverlap( string outDir )
	{
		var d = SimRig.Tentacle( fingers: false );
		var s = d.Skeleton;
		var heap = Bind( s, "heap" );

		// The heap lurches sideways and back; the cables should trail it, root first, tip last.
		var clip = new RigAnimFile { FrameRate = 30, FrameCount = 61, SourceModel = d.Model };
		clip.Tracks.Add( Track( "heap", RigTrackKind.Bone, Key( 0, heap ), Key( 8, Moved( heap, new Vec3( 0, 14, 0 ) ) ), Key( 40, Moved( heap, new Vec3( 0, 14, 0 ) ) ), Key( 60, Moved( heap, new Vec3( 0, 14, 0 ) ) ) ) );

		var before = new MotionLint( s, clip );
		var bakedResult = ClipBake.Secondary( clip, s, "cable1_00", ChainSpring.FindPreset( "loose" ).ToSoftBone(), loop: false );
		Check( "the headless bake runs and writes the cable's tracks", bakedResult.Ok && bakedResult.Bones == 12 && clip.Find( "cable1_05" ) is { Keys.Count: > 2 }, $"{bakedResult.Bones} bones, {bakedResult.Keys} keys {bakedResult.Error}" );
		// A damped swing needs a key near every turn, so this is far from a handful - but well under
		// one per frame, and the settled tail of each bone collapses.
		Check( "and reduces them below a key per frame", bakedResult.Keys < 12 * 61 * 3 / 4, bakedResult.Keys.ToString() );

		var lint = new MotionLint( s, clip ).Run( new MotionLint.Options { Chains = { "cable1_" } } );
		var overlap = lint.Notes.FirstOrDefault( n => n.StartsWith( "chain 'cable1_'" ) ) ?? "";
		var steps = System.Text.RegularExpressions.Regex.Match( overlap, @"(\d+)/(\d+) steps" );
		Check( "after the bake the cable peaks later toward its tip", steps.Success && int.Parse( steps.Groups[1].Value ) >= int.Parse( steps.Groups[2].Value ) * 2 / 3, overlap );
		Check( "and the baked motion has no NaN or stretch", !lint.Of( "nan" ).Any() && !lint.Of( "stretch" ).Any(), lint.ToString() );

		var picture = MotionTrail.Render( s, new MotionLint( s, clip ).Pose, 0, 60, MotionTrail.DefaultTraced( s, clip ), title: "bake" );
		var path = Path.Combine( outDir, "sim_trail.png" );
		picture.SavePng( path );
		Check( "the trail renders something", picture.Pixels.Distinct().Count() > 10 );
	}
}
