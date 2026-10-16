using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Marionette tool work after the headless sim: clip layering (ClipLayer, the kernel twin of
/// RigClipLayer) and the keyed IK weight (RigTrackKind.IkWeight).
/// </summary>
public static class MarionetteLayerTests
{
	public static void Run()
	{
		Section( "Marionette: clip layering" );
		TestLayerFrame();
		TestAdditiveLoop();
		TestOneShotOffsetAndRate();
		TestOverlayMaskAndBind();
		TestLayerNotes();

		Section( "Marionette: keyed IK weight" );
		TestIkWeightBlends();
		TestIkWeightLintAndBake();

		Section( "Marionette: spline IK" );
		TestSplineIk();
		TestSplineTwist();

		Section( "Marionette: live soft chains" );
		var same = Marionette.RigSpringPresets.All.Length == ChainSpring.Presets.Length
			&& Marionette.RigSpringPresets.All.Zip( ChainSpring.Presets ).All( p => p.First.Stiffness == p.Second.Stiffness
				&& p.First.Damping == p.Second.Damping && p.First.Weight == p.Second.Weight && p.First.MaxAngle == p.Second.MaxAngle && p.First.Rope == p.Second.Rope );
		Check( "the player's spring presets are the bake's, in the same order", same );

		Section( "Marionette: keys from targets" );
		TestClipKeys();
	}

	static void TestClipKeys()
	{
		var d = SimRig.Tentacle( fingers: false );
		var s = d.Skeleton;
		var hand = d.BindWorld[s.IndexOf( "cable0_hand" )].Origin;
		var up = hand + new Vec3( 0, 0, 5 );
		var text = $"0:{hand.x},{hand.y},{hand.z};20:{up.x},{up.y},{up.z}:Linear:w=0.5".Replace( "E", "e" );
		var targets = ClipKeys.Parse( text, RigEase.Smooth, out var error );
		Check( "targets parse: frame, position, ease and weight", targets is { Count: 2 } && targets[1].Ease == RigEase.Linear && targets[1].Weight == 0.5f, error );
		Check( "and a bad one says why", ClipKeys.Parse( "x:1,2", RigEase.Smooth, out var bad ) is null && bad.Contains( "frame:x,y,z" ) );

		var clip = new RigAnimFile { FrameRate = 30 };
		var goals = ClipKeys.Goals( clip, "cable0_hand", targets );
		Check( "goal mode keys the goal and the weight", goals.Ok && clip.IkGoalTracks.Single().Keys.Count == 2 && Math.Abs( clip.IkWeightAt( "cable0_hand", 20 ) - 0.5f ) < 1e-5f );

		var fk = new RigAnimFile { FrameRate = 30 };
		var ik = new RigIk { Name = "c0", EndBone = "cable0_hand", ChainLength = 6, Iterations = 20, Fabrik = true };
		var solved = ClipKeys.Solve( fk, s, ik, targets );
		var reached = (new MotionLint( s, fk ).Pose( 20 )[s.IndexOf( "cable0_hand" )].Origin - up).Length;
		Check( "fk mode writes the chain so the hand is on the target", solved.Ok && solved.Keys == 12 && reached < 0.05f, $"{solved} miss {reached:0.###}" );
	}

	// --- helpers ---------------------------------------------------------------------------------

	static RigKey Key( int frame, RigPose pose, RigEase ease = RigEase.Monotone ) => new() { Frame = frame, Local = pose, Ease = ease };

	static RigTrack Track( string name, RigTrackKind kind, params RigKey[] keys ) => new() { Name = name, Kind = kind, Keys = keys.ToList() };

	static RigPose Bind( Skeleton s, string bone ) => RigPose.FromXform( s.Bones[s.IndexOf( bone )].Local );

	static RigPose Moved( RigPose p, Vec3 by ) => new( p.Position + by, p.Rotation, p.Scale );

	static RigPose Turned( RigPose p, Vec3 axis, float degrees ) => new( p.Position, p.Rotation * Quat.FromAxisAngle( axis, degrees * MathF.PI / 180f ), p.Scale );

	static RigPose Scaled( RigPose p, float by ) => new( p.Position, p.Rotation, p.Scale * by );

	static RigPose Weight( float w ) => new( Vec3.Zero, Quat.Identity, new Vec3( w, w, w ) );

	static float Degrees( Quat a, Quat b )
	{
		var dot = MathF.Abs( a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w );
		return 2f * MathF.Acos( Math.Clamp( dot, 0f, 1f ) ) * 180f / MathF.PI;
	}

	static bool Near( RigPose a, RigPose b, float tolerance = 0.03f ) =>
		(a.Position - b.Position).Length < tolerance && Degrees( a.Rotation, b.Rotation ) < 0.5f && (a.Scale - b.Scale).Length < 0.01f;

	/// <summary>The heap rising over 60 frames - the base every layer test sums onto.</summary>
	static RigAnimFile Rising( Skeleton s )
	{
		var heap = Bind( s, "heap" );
		var clip = new RigAnimFile { FrameRate = 30, FrameCount = 61 };
		clip.Tracks.Add( Track( "heap", RigTrackKind.Bone, Key( 0, heap ), Key( 60, Moved( heap, new Vec3( 0, 0, 6 ) ) ) ) );
		return clip;
	}

	/// <summary>A 20-frame pulse: the heap turns 10° and swells 4 %, and is back where it began.</summary>
	static RigAnimFile Pulse( Skeleton s )
	{
		var heap = Bind( s, "heap" );
		var clip = new RigAnimFile { FrameRate = 30, FrameCount = 21 };
		clip.Tracks.Add( Track( "heap", RigTrackKind.Bone, Key( 0, heap ), Key( 10, Scaled( Turned( heap, new Vec3( 0, 0, 1 ), 10f ), 1.04f ) ), Key( 20, heap ) ) );
		return clip;
	}

	// --- layering ----------------------------------------------------------------------------------

	static void TestLayerFrame()
	{
		Check( "a looping layer repeats, before its offset too",
			ClipLayer.LayerFrame( 25, 0, 1f, 20, true ) == 5f && ClipLayer.LayerFrame( 3, 10, 1f, 20, true ) == 13f );
		Check( "a one-shot layer plays only inside its span",
			ClipLayer.LayerFrame( 9, 10, 1f, 20, false ) is null && ClipLayer.LayerFrame( 31, 10, 1f, 20, false ) is null && ClipLayer.LayerFrame( 20, 10, 1f, 20, false ) == 10f );
		Check( "rate speeds the layer up", ClipLayer.LayerFrame( 5, 0, 2f, 20, false ) == 10f );
	}

	static void TestAdditiveLoop()
	{
		var d = SimRig.Tentacle( fingers: false );
		var s = d.Skeleton;
		var clip = Rising( s );
		var baseTrack = Track( "heap", RigTrackKind.Bone, clip.Find( "heap" ).Keys.Select( k => Key( k.Frame, k.Local, k.Ease ) ).ToArray() );
		var pulse = Pulse( s );

		var result = ClipLayer.Merge( clip, pulse, new ClipLayer.Settings { Mode = RigLayerMode.Additive, Loop = true }, s );
		Check( "an additive loop merges onto the base", result.Ok && result.Tracks == 1, result.Error );

		var heap = clip.Find( "heap" );
		var reference = pulse.Tracks[0].Keys[0].Local;
		var expected = ClipLayer.Compose( baseTrack.Evaluate( 10 ), reference, pulse.Tracks[0].Evaluate( 10 ), 1f, RigLayerMode.Additive );
		Check( "frame 10 is the base plus the pulse's peak", Near( heap.Evaluate( 10 ), expected ), $"{heap.Evaluate( 10 )} vs {expected}" );
		Check( "the base's rise is kept under it", MathF.Abs( heap.Evaluate( 30 ).Position.z - baseTrack.Evaluate( 30 ).Position.z ) < 0.03f );
		Check( "and the pulse repeats every 20 frames over the whole clip",
			Degrees( heap.Evaluate( 50 ).Rotation, baseTrack.Evaluate( 50 ).Rotation ) > 9f && Degrees( heap.Evaluate( 40 ).Rotation, baseTrack.Evaluate( 40 ).Rotation ) < 0.5f );
		Check( "scale multiplies, so a 4 % swell swells", MathF.Abs( heap.Evaluate( 10 ).Scale.x - 1.04f ) < 0.005f, heap.Evaluate( 10 ).Scale.ToString() );
		Check( "the merged track is reduced below a key per frame", heap.Keys.Count < 61, heap.Keys.Count.ToString() );
	}

	static void TestOneShotOffsetAndRate()
	{
		var d = SimRig.Tentacle( fingers: false );
		var s = d.Skeleton;
		var clip = Rising( s );
		var before = Track( "heap", RigTrackKind.Bone, clip.Find( "heap" ).Keys.Select( k => Key( k.Frame, k.Local, k.Ease ) ).ToArray() );

		var result = ClipLayer.Merge( clip, Pulse( s ), new ClipLayer.Settings { Offset = 30, Rate = 2f }, s );
		var heap = clip.Find( "heap" );
		Check( "a one-shot layer at frame 30 leaves frames before it alone",
			result.Ok && Enumerable.Range( 0, 30 ).All( f => Near( heap.Evaluate( f ), before.Evaluate( f ) ) ) );
		Check( "and at rate 2 peaks 5 frames in", Degrees( heap.Evaluate( 35 ).Rotation, before.Evaluate( 35 ).Rotation ) > 9f );
		Check( "and is over by frame 40", Enumerable.Range( 41, 20 ).All( f => Near( heap.Evaluate( f ), before.Evaluate( f ) ) ) );

		// A layer longer than the base lengthens it.
		var shortBase = new RigAnimFile { FrameRate = 30 };
		shortBase.Tracks.Add( Track( "heap", RigTrackKind.Bone, Key( 0, Bind( s, "heap" ) ), Key( 10, Bind( s, "heap" ) ) ) );
		ClipLayer.Merge( shortBase, Pulse( s ), new ClipLayer.Settings { Offset = 5 }, s );
		Check( "a one-shot layer past the base's end extends it", shortBase.LastKeyFrame == 25, shortBase.LastKeyFrame.ToString() );
	}

	static void TestOverlayMaskAndBind()
	{
		var d = SimRig.Tentacle( fingers: false );
		var s = d.Skeleton;
		var clip = Rising( s );
		var bone = Bind( s, "cable2_03" );
		var layer = new RigAnimFile { FrameRate = 30 };
		layer.Tracks.Add( Track( "cable2_03", RigTrackKind.Bone, Key( 0, Turned( bone, new Vec3( 1, 0, 0 ), 40f ) ), Key( 20, Turned( bone, new Vec3( 1, 0, 0 ), 40f ) ) ) );
		layer.Tracks.Add( Track( "heap", RigTrackKind.Bone, Key( 0, Moved( Bind( s, "heap" ), new Vec3( 0, 50, 0 ) ) ), Key( 20, Moved( Bind( s, "heap" ), new Vec3( 0, 50, 0 ) ) ) ) );

		var result = ClipLayer.Merge( clip, layer, new ClipLayer.Settings { Mode = RigLayerMode.Overlay, Weight = 0.5f, Loop = true, Mask = { "cable2_*" } }, s );
		Check( "the mask keeps the layer to the bones it names", result.Ok && result.Tracks == 1 && clip.Find( "heap" ).Keys.Count == 2, $"{result.Tracks} tracks" );

		var turned = clip.Find( "cable2_03" );
		Check( "an overlay at half weight goes half way from the rest pose (a bone the base never keyed)",
			turned is not null && MathF.Abs( Degrees( turned.Evaluate( 7 ).Rotation, bone.Rotation ) - 20f ) < 0.6f,
			turned is null ? "no track" : Degrees( turned.Evaluate( 7 ).Rotation, bone.Rotation ).ToString( "0.##" ) );
	}

	static void TestLayerNotes()
	{
		var d = SimRig.Tentacle( fingers: false );
		var s = d.Skeleton;
		var clip = Rising( s );
		var layer = new RigAnimFile { FrameRate = 30 };
		layer.Tracks.Add( Track( "cable0_hand", RigTrackKind.IkGoal, Key( 0, RigPose.Identity ), Key( 10, Moved( RigPose.Identity, new Vec3( 1, 0, 0 ) ) ) ) );
		var result = ClipLayer.Merge( clip, layer, new ClipLayer.Settings(), s );
		Check( "an additive goal with nothing under it is skipped and says so", result.Ok && result.Tracks == 0 && result.Notes.Any( n => n.Contains( "cable0_hand" ) ), string.Join( "; ", result.Notes ) );

		var overlay = ClipLayer.Merge( clip, layer, new ClipLayer.Settings { Mode = RigLayerMode.Overlay }, s );
		Check( "an overlay goal with nothing under it becomes the clip's goal", overlay.Ok && clip.IkGoalTracks.Count() == 1 );

		var empty = ClipLayer.Merge( clip, Pulse( s ), new ClipLayer.Settings { Mask = { "nothing_*" } }, s );
		Check( "a mask that matches nothing is an error, not a silent no-op", !empty.Ok && empty.Error.Contains( "mask" ) );
	}

	// --- keyed IK weight ---------------------------------------------------------------------------

	/// <summary>Cable 0 reaching up 6 units with a FABRIK goal, as MarionetteSimTests' Reach.</summary>
	static RigAnimFile Reach( ModelDump d, Vec3 offset )
	{
		var hand = d.BindWorld[d.Skeleton.IndexOf( "cable0_hand" )].Origin;
		var clip = new RigAnimFile { FrameRate = 30, FrameCount = 61 };
		clip.Tracks.Add( Track( "cable0_hand", RigTrackKind.IkGoal,
			Key( 0, new RigPose( hand + offset, Quat.Identity, Vec3.One ) ), Key( 60, new RigPose( hand + offset, Quat.Identity, Vec3.One ) ) ) );
		clip.Ik = new List<RigIk> { new() { Name = "c0", EndBone = "cable0_hand", ChainLength = 6, Fabrik = true, Iterations = 20, GoalRotation = false, Live = true } };
		return clip;
	}

	static void TestIkWeightBlends()
	{
		Check( "the track kinds keep their ordinals, IkWeight appended", (int)RigTrackKind.Camera == 3 && (int)RigTrackKind.IkWeight == 4 );

		var d = SimRig.Tentacle( fingers: false );
		var s = d.Skeleton;
		var handIndex = s.IndexOf( "cable0_hand" );
		var rest = d.BindWorld[handIndex].Origin;
		var offset = new Vec3( 0, 0, 6 );
		var goal = rest + offset;

		Vec3 HandAt( RigAnimFile clip, float frame ) => new MotionLint( s, clip ).Pose( frame )[handIndex].Origin;

		var full = Reach( d, offset );
		Check( "with no weight track the hand reaches its goal", (HandAt( full, 30 ) - goal).Length < 0.1f );

		var weighted = Reach( d, offset );
		weighted.Tracks.Add( Track( "cable0_hand", RigTrackKind.IkWeight, Key( 0, Weight( 1f ), RigEase.Linear ), Key( 20, Weight( 1f ), RigEase.Linear ), Key( 40, Weight( 0f ) ) ) );
		Check( "keyed 1, it still reaches", (HandAt( weighted, 10 ) - goal).Length < 0.1f );
		Check( "keyed 0, the chain plays its keys (here the rest pose)", (HandAt( weighted, 50 ) - rest).Length < 0.1f, $"{(HandAt( weighted, 50 ) - rest).Length:0.###}" );

		var mid = HandAt( weighted, 30 );
		Check( "half way between, the hand is between the two", (mid - goal).Length > 0.5f && (mid - rest).Length > 0.5f && (mid - rest).Length < offset.Length,
			$"{(mid - goal).Length:0.##} from goal, {(mid - rest).Length:0.##} from rest" );
		Check( "IkWeightAt reads the key's scale.x, 1 with no track", MathF.Abs( weighted.IkWeightAt( "cable0_hand", 30 ) - 0.5f ) < 1e-4f && full.IkWeightAt( "cable0_hand", 30 ) == 1f );

		var json = RigAnimWriter.Write( weighted, "{}" );
		var back = RigAnimFile.Read( json );
		Check( "a weight track round-trips by name", json.Contains( "\"IkWeight\"" ) && back.Tracks.Count( t => t.Kind == RigTrackKind.IkWeight ) == 1 );
		Check( "and an ordinal 4 reads as a weight track",
			RigAnimFile.Read( "{\"BoneTracks\":[{\"BoneName\":\"x\",\"Target\":4,\"Keyframes\":[]}]}" ).Tracks[0].Kind == RigTrackKind.IkWeight );
	}

	// --- spline IK ----------------------------------------------------------------------------------

	static int[] Chain( Skeleton s, string end, int length )
	{
		var chain = new int[length + 1];
		chain[^1] = s.IndexOf( end );

		for ( var k = length - 1; k >= 0; k-- )
			chain[k] = s.Bones[chain[k + 1]].Parent;

		return chain;
	}

	/// <summary>Degrees each joint bends: the angle between one bone's direction and the next's.</summary>
	static float[] Bends( Xform[] pose, int[] chain ) =>
		Enumerable.Range( 1, chain.Length - 2 ).Select( k =>
		{
			var a = (pose[chain[k]].Origin - pose[chain[k - 1]].Origin).Normal;
			var b = (pose[chain[k + 1]].Origin - pose[chain[k]].Origin).Normal;
			return MathF.Acos( Math.Clamp( Vec3.Dot( a, b ), -1f, 1f ) ) * 180f / MathF.PI;
		} ).ToArray();

	static void TestSplineIk()
	{
		var d = SimRig.Tentacle( fingers: false );
		var s = d.Skeleton;
		var chain = Chain( s, "cable0_hand", 8 );
		var bind = d.BindWorld;
		var rest = bind[chain[^1]].Origin;
		var root = bind[chain[0]].Origin;

		// Pulled in and up: a goal well inside reach, so the chain has to bend.
		var goal = new RigPose( root + (rest - root) * 0.55f + new Vec3( 0, 0, 10 ), Quat.Identity, Vec3.One );

		RigIk Ik( bool spline, bool fabrik = false ) => new() { Name = "c0", EndBone = "cable0_hand", ChainLength = 8, Iterations = 20, GoalRotation = false, Spline = spline, Fabrik = fabrik };

		var splined = ChainIk.Apply( s, bind, Ik( true ), goal );
		var ccd = ChainIk.Apply( s, bind, Ik( false ), goal );
		Check( "spline IK puts the end on the goal", splined is not null && (splined[chain[^1]].Origin - goal.Position).Length < 0.05f,
			splined is null ? "null" : (splined[chain[^1]].Origin - goal.Position).Length.ToString( "0.###" ) );

		var lengthsKept = Enumerable.Range( 0, chain.Length - 1 ).All( k =>
			MathF.Abs( (splined[chain[k + 1]].Origin - splined[chain[k]].Origin).Length - (bind[chain[k + 1]].Origin - bind[chain[k]].Origin).Length ) < 1e-3f );
		Check( "and keeps every bone's length", lengthsKept );

		var sb = Bends( splined, chain );
		var cb = Bends( ccd, chain );
		Check( "its bend is spread along the chain - no joint takes much more than its share",
			sb.Max() <= sb.Average() * 2.5f + 1f, $"max {sb.Max():0.#}° mean {sb.Average():0.#}° ({string.Join( " ", sb.Select( x => x.ToString( "0" ) ) )})" );
		Check( "where CCD kinks harder at one joint", cb.Max() > sb.Max(), $"CCD max {cb.Max():0.#}° vs spline {sb.Max():0.#}°" );

		var far = new RigPose( root + (rest - root).Normal * 500f, Quat.Identity, Vec3.One );
		var straight = ChainIk.Apply( s, bind, Ik( true ), far );
		Check( "out of reach it lies straight toward the goal, as FABRIK does", Bends( straight, chain ).Max() < 0.5f );

		var json = "{\"IkConstraints\":[{\"TargetBone\":\"a\",\"Solver\":\"Spline\"},{\"TargetBone\":\"b\",\"Solver\":2},{\"TargetBone\":\"c\",\"Solver\":\"Fabrik\"}]}";
		var read = RigIk.ReadRig( json );
		Check( "a .ctrlrig's Spline solver reads by name and by ordinal", read[0].Spline && read[1].Spline && !read[2].Spline && read[2].Fabrik && !read[0].Fabrik );
	}

	static void TestSplineTwist()
	{
		var d = SimRig.Tentacle( fingers: false );
		var s = d.Skeleton;
		var chain = Chain( s, "cable0_hand", 6 );
		var bind = d.BindWorld;
		var rest = bind[chain[^1]].Origin;
		var root = bind[chain[0]].Origin;
		var at = root + (rest - root) * 0.7f + new Vec3( 0, 0, 6 );

		RigIk Ik() => new() { Name = "c0", EndBone = "cable0_hand", ChainLength = 6, Iterations = 20, GoalRotation = true, Spline = true };

		// Same place, the hand's bind rotation, then the same turned 90° about the last bone.
		var plain = ChainIk.Apply( s, bind, Ik(), new RigPose( at, Quat.FromXform( bind[chain[^1]] ), Vec3.One ) );
		var axis = (plain[chain[^1]].Origin - plain[chain[^2]].Origin).Normal;
		var turnedGoal = Quat.FromAxisAngle( axis, MathF.PI / 2f ) * Quat.FromXform( bind[chain[^1]] );
		var twisted = ChainIk.Apply( s, bind, Ik(), new RigPose( at, turnedGoal, Vec3.One ) );

		float Turn( int k ) => Degrees( Quat.FromXform( plain[chain[k]] ), Quat.FromXform( twisted[chain[k]] ) );

		var n = chain.Length - 1;
		var shares = Enumerable.Range( 0, n ).Select( Turn ).ToArray();
		Check( "a turned goal's twist is spread down the chain, (k+1)/(n+1) of it per bone",
			Enumerable.Range( 0, n ).All( k => MathF.Abs( shares[k] - 90f * (k + 1) / (n + 1) ) < 4f ),
			string.Join( " ", shares.Select( x => x.ToString( "0.#" ) ) ) );
		Check( "and the hand takes the goal's rotation", Degrees( Quat.FromXform( twisted[chain[^1]] ), turnedGoal ) < 0.5f );
		Check( "without moving any joint", Enumerable.Range( 0, n + 1 ).All( k => (plain[chain[k]].Origin - twisted[chain[k]].Origin).Length < 1e-3f ) );
	}

	static void TestIkWeightLintAndBake()
	{
		var d = SimRig.Tentacle( fingers: false );
		var s = d.Skeleton;

		var far = Reach( d, new Vec3( 0, 0, 200 ) );
		Check( "an unreachable goal at full weight is flagged", new MotionLint( s, far ).Run().Of( "reach" ).Any() );

		far.Tracks.Add( Track( "cable0_hand", RigTrackKind.IkWeight, Key( 0, Weight( 0f ) ), Key( 60, Weight( 0f ) ) ) );
		Check( "the same goal weighted off is not - the hand is free there", !new MotionLint( s, far ).Run().Of( "reach" ).Any() );

		var held = Reach( d, new Vec3( 0, 0, 6 ) );
		Check( "a held chain's bones are skipped by the secondary bake", ClipBake.LiveIkBones( held, s ).Contains( "cable0_09" ) );

		held.Tracks.Add( Track( "cable0_hand", RigTrackKind.IkWeight, Key( 0, Weight( 1f ) ), Key( 30, Weight( 0f ) ) ) );
		Check( "one whose weight lets go is baked (keys show where it is free)", !ClipBake.LiveIkBones( held, s ).Contains( "cable0_09" ) );
	}
}
