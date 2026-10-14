using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The Cubic and Monotone interpolation modes, the track kinds Marionette added alongside them
/// (IK goals, cameras), and IK on chains of any length.
///
/// PARITY IS CHECKED, NOT ASSUMED. Marionette's RigCurve is compiled into this project beside the
/// kernel's RigHermite, and the two are run on the same keys: a clip has to curve the same in the
/// editor, in game and in a headless take check, and two copies of one formula drift unless
/// something compares them.
/// </summary>
public static class RigCurveTests
{
	public static void Run()
	{
		Section( "riganim: cubic and monotone curves" );
		TestOrdinals();
		TestParityWithMarionette();
		TestCurvesPassThroughKeys();
		TestMonotoneDoesNotOvershoot();
		TestCubicCarriesVelocityThroughAKey();
		TestOwnTangents();
		TestRotationHemisphere();

		Section( "riganim: track kinds and IK goals" );
		TestTrackKinds();
		TestRigConstraintsRead();

		Section( "IK: chains of any length" );
		TestChainOfOneAims();
		TestChainOfTwoIsTheTwoBoneSolve();
		TestLongChainReaches();
		TestWeightAndGoalRotation();
		TestClipSolvesItsGoals();

		Section( "riganim: scale in the export" );
		TestScaleIsBakedApart();
		TestScaleChannelIsWrittenOnlyWhenUsed();
	}

	// --- helpers ---------------------------------------------------------------------------------

	static string Key( int frame, Vec3 p, Quat q, string ease, string extra = "" ) => string.Format( CultureInfo.InvariantCulture,
		"{{\"Frame\":{0},\"Local\":{{\"Position\":\"{1},{2},{3}\",\"Scale\":\"1,1,1\",\"Rotation\":\"{4},{5},{6},{7}\"}},\"Interpolation\":\"{8}\"{9}}}",
		frame, p.x, p.y, p.z, q.x, q.y, q.z, q.w, ease, extra );

	static string Clip( params (string Bone, string Target, string[] Keys)[] tracks )
	{
		var sb = new StringBuilder( "{\"SourceModel\":\"models/test.vmdl\",\"AnimationSpeed\":30,\"FrameCount\":60,\"BoneTracks\":[" );
		sb.Append( string.Join( ",", tracks.Select( t => $"{{\"BoneName\":\"{t.Bone}\",\"Target\":\"{t.Target}\",\"Keyframes\":[{string.Join( ",", t.Keys )}]}}" ) ) );
		return sb.Append( "]}" ).ToString();
	}

	/// <summary>A one-channel track along x: values at frames, all in one mode.</summary>
	static RigTrack Line( string ease, params (int Frame, float X)[] keys ) =>
		RigAnimFile.Read( Clip( ("bone", "Bone", keys.Select( k => Key( k.Frame, new Vec3( k.X, 0, 0 ), Quat.Identity, ease ) ).ToArray()) ) ).Find( "bone" );

	static bool Near( float a, float b, float tolerance = 1e-4f ) => MathF.Abs( a - b ) <= tolerance;

	// --- curves ----------------------------------------------------------------------------------

	static void TestOrdinals()
	{
		Check( "Cubic and Monotone are appended after Marionette's first five", (int)RigEase.Cubic == 5 && (int)RigEase.Monotone == 6 );
		Check( "the track kinds keep Marionette's ordinals", (int)RigTrackKind.Bone == 0 && (int)RigTrackKind.Part == 1 && (int)RigTrackKind.IkGoal == 2 && (int)RigTrackKind.Camera == 3 );

		var json = Clip( ("bone", "Bone", new[] { Key( 0, Vec3.Zero, Quat.Identity, "Monotone" ), Key( 10, Vec3.One, Quat.Identity, "Cubic" ) }) )
			.Replace( "\"Interpolation\":\"Cubic\"", "\"Interpolation\":5" );
		var keys = RigAnimFile.Read( json ).Find( "bone" ).Keys;
		Check( "both read by name and by ordinal", keys[0].Ease == RigEase.Monotone && keys[1].Ease == RigEase.Cubic );
	}

	static void TestParityWithMarionette()
	{
		var random = new Random( 7 );
		float R() => (float)(random.NextDouble() * 20 - 10);

		float[] Channels()
		{
			var q = Quat.FromAxisAngle( new Vec3( R(), R(), R() ).Normal, R() ).Normalized;
			// A stored rotation can carry either sign, which is exactly what the alignment is for.
			var sign = random.Next( 2 ) == 0 ? 1f : -1f;
			return new[] { R(), R(), R(), q.x * sign, q.y * sign, q.z * sign, q.w * sign, 1 + R() * 0.05f, 1, 1 };
		}

		float[] MaybeTangent() => random.Next( 3 ) == 0 ? Enumerable.Range( 0, 10 ).Select( _ => R() * 0.1f ).ToArray() : Array.Empty<float>();

		var worst = 0f;

		for ( var trial = 0; trial < 400; trial++ )
		{
			var monotone = trial % 2 == 0;
			var (p, a, b, n) = (Channels(), Channels(), Channels(), Channels());
			var (outA, inB) = (MaybeTangent(), MaybeTangent());
			float? fp = random.Next( 4 ) == 0 ? null : -3 - random.Next( 6 );
			float? fn = random.Next( 4 ) == 0 ? null : 17 + random.Next( 6 );
			var frame = (float)random.NextDouble() * 12f;

			var kernel = new float[10];
			var marionette = new float[10];

			RigHermite.Evaluate( monotone, frame, 0, a, outA, 12, b, inB, fp, p, fn, n, kernel );
			Marionette.RigCurve.Evaluate( monotone, frame, 0, a, outA, 12, b, inB, fp, p, fn, n, marionette );

			for ( var c = 0; c < 10; c++ )
				worst = MathF.Max( worst, MathF.Abs( kernel[c] - marionette[c] ) );
		}

		Check( "the kernel's curve is Marionette's to the bit", worst == 0f, $"worst difference {worst}" );
	}

	static void TestCurvesPassThroughKeys()
	{
		foreach ( var ease in new[] { "Cubic", "Monotone" } )
		{
			var track = Line( ease, (0, 0), (10, 4), (20, -2), (30, 7) );
			var through = new[] { (0, 0f), (10, 4f), (20, -2f), (30, 7f) }.All( k => Near( track.Evaluate( k.Item1 ).Position.x, k.Item2 ) );
			Check( $"{ease} passes through every key", through );
		}
	}

	static void TestMonotoneDoesNotOvershoot()
	{
		// A hold, a rise, a hold. Catmull-Rom dips below the first hold and over the second; the
		// monotone curve stays inside both.
		var cubic = Line( "Cubic", (0, 0), (10, 0), (20, 10), (30, 10) );
		var monotone = Line( "Monotone", (0, 0), (10, 0), (20, 10), (30, 10) );

		var cubicRange = Enumerable.Range( 0, 301 ).Select( i => cubic.Evaluate( i * 0.1f ).Position.x ).ToList();
		var monoRange = Enumerable.Range( 0, 301 ).Select( i => monotone.Evaluate( i * 0.1f ).Position.x ).ToList();

		Check( "Cubic is free to overshoot a hold", cubicRange.Min() < -0.01f || cubicRange.Max() > 10.01f, $"{cubicRange.Min()}..{cubicRange.Max()}" );
		Check( "Monotone never leaves the range of its keys", monoRange.Min() >= -1e-5f && monoRange.Max() <= 10f + 1e-5f, $"{monoRange.Min()}..{monoRange.Max()}" );

		var rising = true;
		for ( var i = 1; i < monoRange.Count; i++ )
			rising &= monoRange[i] >= monoRange[i - 1] - 1e-5f;
		Check( "and keys that only rise give a curve that only rises", rising );
		Check( "and a hold stays held", Enumerable.Range( 0, 100 ).All( i => Near( monotone.Evaluate( i * 0.1f ).Position.x, 0f, 1e-5f ) ) );
	}

	static void TestCubicCarriesVelocityThroughAKey()
	{
		var track = Line( "Cubic", (0, 0), (10, 5), (25, 6), (30, 12) );
		var h = 0.01f;
		var before = (track.Evaluate( 10 ).Position.x - track.Evaluate( 10 - h ).Position.x) / h;
		var after = (track.Evaluate( 10 + h ).Position.x - track.Evaluate( 10 ).Position.x) / h;
		Check( "a cubic curve has one velocity either side of a key", Near( before, after, 0.02f ), $"{before} vs {after}" );

		var linear = Line( "Linear", (0, 0), (10, 5), (25, 6) );
		var lb = (linear.Evaluate( 10 ).Position.x - linear.Evaluate( 10 - h ).Position.x) / h;
		var la = (linear.Evaluate( 10 + h ).Position.x - linear.Evaluate( 10 ).Position.x) / h;
		Check( "where linear keys change speed at the key", !Near( lb, la, 0.1f ) );
	}

	static void TestOwnTangents()
	{
		var flat = "," + "\"TangentOut\":[0,0,0,0,0,0,0,0,0,0]";
		var steep = "," + "\"TangentIn\":[2,0,0,0,0,0,0,0,0,0]";
		var json = Clip( ("bone", "Bone", new[] { Key( 0, Vec3.Zero, Quat.Identity, "Cubic", flat ), Key( 10, new Vec3( 10, 0, 0 ), Quat.Identity, "Cubic", steep ) }) );
		var track = RigAnimFile.Read( json ).Find( "bone" );

		Check( "a key's tangents are read", track.Keys[0].TangentOut is { Length: 10 } && track.Keys[1].TangentIn is { Length: 10 } );

		var h = 0.01f;
		var start = (track.Evaluate( h ).Position.x - track.Evaluate( 0 ).Position.x) / h;
		var end = (track.Evaluate( 10 ).Position.x - track.Evaluate( 10 - h ).Position.x) / h;
		Check( "a flat tangent leaves the key level", Near( start, 0f, 0.05f ), start.ToString() );
		Check( "and a steep one arrives at its slope", Near( end, 2f, 0.05f ), end.ToString() );

		var wrong = Clip( ("bone", "Bone", new[] { Key( 0, Vec3.Zero, Quat.Identity, "Cubic", ",\"TangentOut\":[1,2,3]" ), Key( 10, Vec3.One, Quat.Identity, "Cubic" ) }) );
		Check( "a tangent of the wrong length is read as automatic", RigAnimFile.Read( wrong ).Find( "bone" ).Keys[0].TangentOut is null );
	}

	static void TestRotationHemisphere()
	{
		var a = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), 0.2f );
		var b = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), 1.2f );
		var c = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), 2.0f );
		var negB = new Quat( -b.x, -b.y, -b.z, -b.w );

		RigTrack Turn( Quat middle ) => RigAnimFile.Read( Clip( ("bone", "Bone", new[]
		{
			Key( 0, Vec3.Zero, a, "Cubic" ), Key( 10, Vec3.Zero, middle, "Cubic" ), Key( 20, Vec3.Zero, c, "Cubic" )
		}) ) ).Find( "bone" );

		var plain = Turn( b );
		var flipped = Turn( negB );
		var same = Enumerable.Range( 0, 21 ).All( f =>
		{
			var x = plain.Evaluate( f ).Rotation.Rotate( new Vec3( 1, 0, 0 ) );
			var y = flipped.Evaluate( f ).Rotation.Rotate( new Vec3( 1, 0, 0 ) );
			return (x - y).Length < 1e-4f;
		} );

		Check( "a key stored as -q curves exactly as one stored as q", same );

		var r = plain.Evaluate( 5 ).Rotation;
		Check( "and the curved rotation stays unit length", Near( r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w, 1f, 1e-5f ) );
	}

	// --- kinds -----------------------------------------------------------------------------------

	static void TestTrackKinds()
	{
		var json = Clip(
			("hand", "Bone", new[] { Key( 0, Vec3.Zero, Quat.Identity, "Linear" ) }),
			("hand", "IkGoal", new[] { Key( 0, new Vec3( 1, 2, 3 ), Quat.Identity, "Linear" ) }),
			("shot", "Camera", new[] { Key( 0, Vec3.Zero, Quat.Identity, "Linear" ) }),
			("hand", "SomethingNewer", new[] { Key( 0, new Vec3( 9, 9, 9 ), Quat.Identity, "Linear" ) }) );
		var clip = RigAnimFile.Read( json );

		Check( "an IK goal is not read as a bone of the same name", clip.Find( "hand" ).Keys[0].Local.Position.AlmostEquals( Vec3.Zero ) );
		Check( "goals and cameras are told apart", clip.IkGoalTracks.Count() == 1 && clip.Tracks.Count( t => t.Kind == RigTrackKind.Camera ) == 1 );
		Check( "and a kind from a newer Marionette is not a bone either", clip.BoneTracks.Count() == 1 );
	}

	static void TestRigConstraintsRead()
	{
		var rig = RigIk.ReadRig( "{\"IkConstraints\":[{\"Name\":\"arm\",\"TargetBone\":\"hand_R\",\"Weight\":0.5,\"Enabled\":true,\"ChainLength\":3,\"PoleDirection\":\"0,1,0\",\"Iterations\":4},"
			+ "{\"Name\":\"old\",\"TargetBone\":\"hand_L\",\"Live\":false,\"GoalRotation\":false}]}" );

		Check( "a .ctrlrig's constraints are read in order", rig.Count == 2 && rig[0].EndBone == "hand_R" && rig[0].ChainLength == 3 && rig[0].Iterations == 4 && Near( rig[0].Weight, 0.5f ) && rig[0].Pole.AlmostEquals( new Vec3( 0, 1, 0 ) ) );
		Check( "a rig from before live IK reads as baked, as Marionette loads it", !rig[0].Live && rig[0].GoalRotation );
		Check( "and the switches read when they are set", !rig[1].Live && !rig[1].GoalRotation && rig[1].ChainLength == 2 );
		Check( "a live constraint reads as live", RigIk.ReadRig( "{\"IkConstraints\":[{\"TargetBone\":\"a\",\"Live\":true}]}" )[0].Live );
	}

	// --- IK --------------------------------------------------------------------------------------

	/// <summary>Four 10-unit bones along +x, then a short tip.</summary>
	static Skeleton Chain()
	{
		var s = new Skeleton();
		var b0 = s.AddBone( "b0", -1, Xform.Identity );
		var b1 = s.AddBone( "b1", b0, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
		var b2 = s.AddBone( "b2", b1, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
		var b3 = s.AddBone( "b3", b2, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
		var end = s.AddBone( "end", b3, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
		s.AddBone( "tip", end, Xform.Translate( new Vec3( 2, 0, 0 ) ) );
		return s;
	}

	static RigPose Goal( Vec3 at ) => new( at, Quat.Identity, Vec3.One );

	static float Length( Xform[] w, Skeleton s, string bone ) =>
		(w[s.IndexOf( bone )].Origin - w[s.Bones[s.IndexOf( bone )].Parent].Origin).Length;

	static void TestChainOfOneAims()
	{
		var s = Chain();
		var world = SoftSolver.BindPose( s );
		var solved = ChainIk.Apply( s, world, new RigIk { EndBone = "end", ChainLength = 1, GoalRotation = false }, Goal( new Vec3( 30, 10, 0 ) ) );
		var end = solved[s.IndexOf( "end" )].Origin;
		var aim = (end - world[s.IndexOf( "b3" )].Origin).Normal;

		Check( "a chain of one turns only the bone above the end", solved[s.IndexOf( "b2" )].Origin.AlmostEquals( world[s.IndexOf( "b2" )].Origin ) && solved[s.IndexOf( "b3" )].Origin.AlmostEquals( world[s.IndexOf( "b3" )].Origin ) );
		Check( "aiming the end at the goal", aim.AlmostEquals( new Vec3( 0, 1, 0 ), 1e-4f ), aim.ToString() );
		Check( "without stretching it", Near( Length( solved, s, "end" ), 10f, 1e-3f ) );
	}

	static void TestChainOfTwoIsTheTwoBoneSolve()
	{
		var s = Chain();
		var world = SoftSolver.BindPose( s );
		var target = new Vec3( 32, 8, 0 );
		var pole = new Vec3( 0, 0, 1 );
		var chain = ChainIk.Apply( s, world, new RigIk { EndBone = "end", ChainLength = 2, Pole = pole, GoalRotation = false }, Goal( target ) );
		var two = TwoBoneIk.Apply( s, world, s.IndexOf( "end" ), target, pole );

		var same = Enumerable.Range( 0, s.Count ).All( b => chain[b].Origin.AlmostEquals( two[b].Origin, 1e-4f ) && chain[b].X.AlmostEquals( two[b].X, 1e-4f ) );
		Check( "a chain of two is exactly the two-bone solve", same );
	}

	static void TestLongChainReaches()
	{
		var s = Chain();
		var world = SoftSolver.BindPose( s );
		var target = new Vec3( 15, 20, 5 );
		var ik = new RigIk { EndBone = "end", ChainLength = 4, Iterations = 30, GoalRotation = false };
		var solved = ChainIk.Apply( s, world, ik, Goal( target ) );

		Check( "a chain of four reaches a goal two bones could not", (solved[s.IndexOf( "end" )].Origin - target).Length < 0.05f, solved[s.IndexOf( "end" )].Origin.ToString() );
		Check( "keeping every bone its length", new[] { "b1", "b2", "b3", "end" }.All( b => Near( Length( solved, s, b ), 10f, 1e-3f ) ) );
		Check( "and the root where it was", solved[0].Origin.AlmostEquals( world[0].Origin ) );
		Check( "and the bone below the end carried with it", Near( Length( solved, s, "tip" ), 2f, 1e-3f ) );

		var few = ChainIk.Apply( s, world, new RigIk { EndBone = "end", ChainLength = 4, Iterations = 1, GoalRotation = false }, Goal( target ) );
		var many = solved;
		Check( "Iterations are honoured: one pass lands further off than thirty",
			(few[s.IndexOf( "end" )].Origin - target).Length > (many[s.IndexOf( "end" )].Origin - target).Length );

		Check( "a chain longer than the skeleton is refused", ChainIk.Apply( s, world, new RigIk { EndBone = "end", ChainLength = 9 }, Goal( target ) ) is null );
	}

	static void TestWeightAndGoalRotation()
	{
		var s = Chain();
		var world = SoftSolver.BindPose( s );
		var target = new Vec3( 32, 8, 0 );

		var none = ChainIk.Apply( s, world, new RigIk { EndBone = "end", Weight = 0f, GoalRotation = false }, Goal( target ) );
		Check( "weight zero leaves the pose as it was", Enumerable.Range( 0, s.Count ).All( b => none[b].Origin.AlmostEquals( world[b].Origin, 1e-4f ) ) );

		var turned = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), MathF.PI / 2 );
		var solved = ChainIk.Apply( s, world, new RigIk { EndBone = "end" }, new RigPose( target, turned, Vec3.One ) );
		var forward = solved[s.IndexOf( "end" )].X.Normal;
		Check( "with GoalRotation the end takes the goal's rotation", forward.AlmostEquals( new Vec3( 0, 1, 0 ), 1e-4f ), forward.ToString() );

		var kept = ChainIk.Apply( s, world, new RigIk { EndBone = "end", GoalRotation = false }, new RigPose( target, turned, Vec3.One ) );
		Check( "without it the end keeps its own", kept[s.IndexOf( "end" )].X.Normal.AlmostEquals( new Vec3( 1, 0, 0 ), 1e-4f ) );

		Check( "a disabled constraint does nothing", ChainIk.Apply( s, world, new RigIk { EndBone = "end", Enabled = false }, Goal( target ) ) is null );
	}

	static void TestClipSolvesItsGoals()
	{
		var s = Chain();
		var json = Clip( ("end", "IkGoal", new[] { Key( 0, new Vec3( 30, 10, 0 ), Quat.Identity, "Linear" ), Key( 10, new Vec3( 32, 8, 0 ), Quat.Identity, "Linear" ) }) );
		var clip = RigAnimFile.Read( json );

		var world = RigAnimFile.ModelSpace( s, clip.SampleLocal( s, 10 ) );
		Check( "a clip's IK goal is solved when it is sampled", (world[s.IndexOf( "end" )].Origin - new Vec3( 32, 8, 0 )).Length < 1e-3f, world[s.IndexOf( "end" )].Origin.ToString() );

		clip.Ik = new List<RigIk> { new() { EndBone = "end", Live = false } };
		var baked = RigAnimFile.ModelSpace( s, clip.SampleLocal( s, 10 ) );
		Check( "unless the rig says the constraint is not live", baked[s.IndexOf( "end" )].Origin.AlmostEquals( new Vec3( 40, 0, 0 ) ), baked[s.IndexOf( "end" )].Origin.ToString() );
	}

	// --- scale -----------------------------------------------------------------------------------

	static string ScaledKey( int frame, float scale ) => string.Format( CultureInfo.InvariantCulture,
		"{{\"Frame\":{0},\"Local\":{{\"Position\":\"0,0,0\",\"Scale\":\"{1},{1},{1}\",\"Rotation\":\"0,0,0,1\"}},\"Interpolation\":\"Linear\"}}", frame, scale );

	static void TestScaleIsBakedApart()
	{
		var s = new Skeleton();
		s.AddBone( "root", -1, Xform.Identity );
		s.AddBone( "bulb", 0, Xform.Translate( new Vec3( 1, 0, 0 ) ) );

		var clip = RigAnimFile.Read( Clip( ("bulb", "Bone", new[] { ScaledKey( 0, 1f ), ScaledKey( 10, 2f ) }) ) ).ToClip( s );

		Check( "a scaled bone's scale is baked into the clip's scales", clip.Scales is not null && Near( clip.ScaleAt( 10, 1 ), 2f ) && Near( clip.ScaleAt( 5, 1 ), 1.5f ), clip.Scales is null ? "no scales" : clip.ScaleAt( 10, 1 ).ToString() );
		Check( "and its rotation is written unscaled", Near( clip.Frames[10][1].X.Length, 1f ) );
		Check( "a bone that never scales stays at 1", !clip.Scaled( 0 ) && clip.Scaled( 1 ) );

		var still = RigAnimFile.Read( Clip( ("bulb", "Bone", new[] { ScaledKey( 0, 1f ), ScaledKey( 10, 1f ) }) ) ).ToClip( s );
		Check( "and a clip that scales nothing carries no scales at all", still.Scales is null );
	}

	static void TestScaleChannelIsWrittenOnlyWhenUsed()
	{
		var s = new Skeleton();
		s.AddBone( "root", -1, Xform.Identity );
		s.AddBone( "bulb", 0, Xform.Translate( new Vec3( 1, 0, 0 ) ) );

		var scaled = RigAnimFile.Read( Clip( ("bulb", "Bone", new[] { ScaledKey( 0, 1f ), ScaledKey( 10, 2f ) }) ) ).ToClip( s );
		var text = DmxAnimWriter.Write( s, scaled );
		var root = DmxGrammarTests.Parse( text, out var error );

		Check( "a clip with a scale channel still parses", root is not null, error );

		if ( root is null )
			return;

		var channels = DmxGrammarTests.FindAll( root, "DmeChannel" );
		var scale = channels.FirstOrDefault( c => c.Name == "bulb_s" );

		Check( "the scaled bone gets a scale channel", scale is not null && scale.Values.GetValueOrDefault( "toAttribute" ) == "scale" );
		Check( "on the same transform as its other two", scale is not null && scale.Values.GetValueOrDefault( "toElement" ) == channels.First( c => c.Name == "bulb_p" ).Values.GetValueOrDefault( "toElement" ) );
		Check( "and the bone that does not scale gets none", channels.All( c => c.Name != "root_s" ) && channels.Count == 5 );

		var plain = RigAnimFile.Read( Clip( ("bulb", "Bone", new[] { ScaledKey( 0, 1f ), ScaledKey( 10, 1f ) }) ) ).ToClip( s );
		Check( "a clip that scales nothing writes no scale channel", !DmxAnimWriter.Write( s, plain ).Contains( "_s\"" ) );
	}
}
