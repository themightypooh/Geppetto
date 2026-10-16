using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The animation gaps closed on 2026-10-16 (marionette.md §6): root motion extraction, Damped Track /
/// Look At / Copy Rotation / Copy Location constraints, multi-point spline IK and F-curve modifiers -
/// the kernel twins the headless sim and the export run.
/// </summary>
public static class MarionetteGapTests
{
	public static void Run()
	{
		Section( "Marionette: root motion" );
		TestRootMotionKeepsEveryPose();
		TestRootMotionInPlace();
		TestRootMotionYawAndFull();
		TestRootMotionFootSlide();
		TestRootMotionRefusals();
		TestRootMotionFileAndVmdl();
		TestRootMotionBesideTheHips();
	}

	// --- root motion ---------------------------------------------------------------------------

	/// <summary>root → pelvis → foot_L / foot_R, the feet hanging 30 below the hips.</summary>
	static Skeleton Walker()
	{
		var s = new Skeleton();
		var root = s.AddBone( "root", -1, Xform.Identity );
		var pelvis = s.AddBone( "pelvis", root, Xform.Translate( new Vec3( 0, 0, 36 ) ) );
		s.AddBone( "foot_L", pelvis, Xform.Translate( new Vec3( 0, 6, -32 ) ) );
		s.AddBone( "foot_R", pelvis, Xform.Translate( new Vec3( 0, -6, -32 ) ) );
		return s;
	}

	const int Span = 30;

	/// <summary>The pelvis walks +x 60 units over 30 frames with a surge, a sway and a bob, optionally
	/// turning; each foot is planted for half the cycle (in model space) and swings for the other.</summary>
	static RigAnimFile Walk( Skeleton s, float turnDegrees = 0f )
	{
		var clip = new RigAnimFile { FrameRate = 30, FrameCount = Span + 1 };
		var pelvis = new RigTrack { Name = "pelvis" };
		var left = new RigTrack { Name = "foot_L" };
		var right = new RigTrack { Name = "foot_R" };

		Vec3 Foot( int f, float side, int phase )
		{
			// Planted for frames [phase, phase+15), then swings 60 forward to the next plant.
			var plantX = phase == 0 ? 0f : 30f;
			var cycle = (int)MathF.Floor( (f - phase) / (float)Span );
			var t = f - phase - cycle * Span;
			var x = plantX + 60f * cycle + (t < 15 ? 0f : 60f * ((t - 15) / 15f));
			return new Vec3( x, side, t < 15 ? 4 : 4 + 6 * MathF.Sin( MathF.PI * (t - 15) / 15f ) );
		}

		for ( var f = 0; f <= Span; f++ )
		{
			var u = f / (float)Span;
			var hip = new Vec3( 60f * u + 3f * MathF.Sin( 2f * MathF.PI * f / 15f ), 2f * MathF.Sin( 2f * MathF.PI * u ), 36f + 1.5f * MathF.Cos( 4f * MathF.PI * u ) );
			var turn = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), turnDegrees * u * MathF.PI / 180f );
			var hipWorld = turn.ToXform( hip );
			ClipKeys.Set( pelvis, f, RigPose.FromXform( hipWorld ), RigEase.Linear );
			ClipKeys.Set( left, f, RigPose.FromXform( hipWorld.Inverse * Xform.Translate( Foot( f, 6, 0 ) ) ), RigEase.Linear );
			ClipKeys.Set( right, f, RigPose.FromXform( hipWorld.Inverse * Xform.Translate( Foot( f, -6, 15 ) ) ), RigEase.Linear );
		}

		clip.Tracks.AddRange( new[] { pelvis, left, right } );
		return clip;
	}

	static Xform[] World( Skeleton s, RigAnimFile clip, float f ) => RigAnimFile.ModelSpace( s, clip.SampleLocal( s, f ) );

	static void TestRootMotionKeepsEveryPose()
	{
		var s = Walker();
		var before = Walk( s );
		var clip = Walk( s );
		var result = RootMotion.Extract( clip, s, new RootMotionSettings { Bone = "root", Source = "pelvis", Mode = RootMotionMode.XY } );

		Check( "root motion extracts from the hips onto the root", result.Ok, result.Error );
		Check( "and measures the whole-clip travel", (result.Travel - new Vec3( 60, 0, 0 )).Length < 0.01f, result.Travel.ToString() );

		var worst = 0f;

		for ( var f = 0f; f <= Span; f += 0.5f )
		{
			var a = World( s, before, f );
			var b = World( s, clip, f );

			for ( var k = 1; k < s.Count; k++ )
				worst = MathF.Max( worst, (a[k].Origin - b[k].Origin).Length + (a[k].X - b[k].X).Length );
		}

		Check( "every body bone stays exactly where it was in the model's space", worst < 2e-3f, $"worst {worst}" );

		var rootEnd = World( s, clip, Span )[s.IndexOf( "root" )].Origin;
		Check( "the root now carries the travel", (rootEnd - new Vec3( 60, 0, 0 )).Length < 0.01f, rootEnd.ToString() );
		Check( "the clip is marked for root motion", clip.RootMotion is { Bone: "root", Mode: RootMotionMode.XY } );
		Check( "the root's keys are reduced on a straight line", clip.Find( "root" ).Keys.Count < 8, $"{clip.Find( "root" ).Keys.Count} keys" );

		var cycle = RootMotion.CycleOf( clip, s ).Origin;
		Check( "one cycle's travel is what a looping player adds at the wrap", (cycle - new Vec3( 60, 0, 0 )).Length < 0.01f, cycle.ToString() );
	}

	static void TestRootMotionInPlace()
	{
		var s = Walker();
		var pelvis = s.IndexOf( "pelvis" );

		var linear = Walk( s );
		RootMotion.Extract( linear, s, new RootMotionSettings { Bone = "root", Source = "pelvis", Linear = true } );
		linear.PlayInPlace = true;

		var exact = Walk( s );
		RootMotion.Extract( exact, s, new RootMotionSettings { Bone = "root", Source = "pelvis", Linear = false } );
		exact.PlayInPlace = true;

		var exactDrift = 0f;
		var linearRange = 0f;
		var bobRange = (Min: 99f, Max: -99f);

		for ( var f = 0; f <= Span; f++ )
		{
			var e = World( s, exact, f )[pelvis].Origin;
			exactDrift = MathF.Max( exactDrift, MathF.Sqrt( e.x * e.x + e.y * e.y ) );

			var l = World( s, linear, f )[pelvis].Origin;
			linearRange = MathF.Max( linearRange, MathF.Abs( l.x ) );
			bobRange = (MathF.Min( bobRange.Min, l.z ), MathF.Max( bobRange.Max, l.z ));
		}

		Check( "exact: played in place the hips never leave their spot on the ground", exactDrift < 0.01f, $"{exactDrift}" );
		Check( "linear: the step-to-step surge stays in the body (about ±3)", linearRange > 2.5f && linearRange < 3.5f, $"{linearRange}" );
		Check( "XY keeps the hips' bob", bobRange.Max - bobRange.Min > 2.9f, $"{bobRange}" );

		var first = World( s, linear, 0 );
		var last = World( s, linear, Span );
		var seam = Enumerable.Range( 0, s.Count ).Max( k => (first[k].Origin - last[k].Origin).Length );
		Check( "linear in place is loop-safe: the last frame is the first again", seam < 0.01f, $"seam {seam}" );
	}

	static void TestRootMotionYawAndFull()
	{
		var s = Walker();
		var pelvis = s.IndexOf( "pelvis" );
		var turning = Walk( s, 90f );
		var r = RootMotion.Extract( turning, s, new RootMotionSettings { Bone = "root", Source = "pelvis", Mode = RootMotionMode.XYYaw, Linear = false } );

		Check( "XY + Yaw measures the turn", MathF.Abs( r.Yaw - 90f ) < 0.1f, $"{r.Yaw}" );

		var rootX = World( s, turning, Span )[s.IndexOf( "root" )].X;
		Check( "and the root turns with the walk", (rootX - new Vec3( 0, 1, 0 )).Length < 1e-3f, rootX.ToString() );

		turning.PlayInPlace = true;
		var hipX = World( s, turning, Span )[pelvis].X;
		Check( "played in place the hips keep facing forward", (hipX - new Vec3( 1, 0, 0 )).Length < 1e-3f, hipX.ToString() );

		var flat = Walk( s );
		RootMotion.Extract( flat, s, new RootMotionSettings { Bone = "root", Source = "pelvis", Mode = RootMotionMode.Full, Linear = false } );
		flat.PlayInPlace = true;
		var z = Enumerable.Range( 0, Span + 1 ).Select( f => World( s, flat, f )[pelvis].Origin.z ).ToList();
		Check( "Full takes the height too: in place the hips hold their first height", z.Max() - z.Min() < 0.01f, $"{z.Min()}..{z.Max()}" );
	}

	static void TestRootMotionFootSlide()
	{
		var s = Walker();
		var clip = Walk( s );
		RootMotion.Extract( clip, s, new RootMotionSettings { Bone = "root", Source = "pelvis" } );
		var report = RootMotion.SlideReport( clip, s, null );
		var left = report.FirstOrDefault( r => r.Bone == "foot_L" );

		Check( "the slide report finds both feet", report.Count == 2 && report.All( r => r.Spans >= 1 ), string.Join( "; ", report ) );
		Check( "a planted foot holds with root motion applied", left.WithRootMotion < 0.25f, $"{left.WithRootMotion}" );
		Check( "and skates by the stride when it is thrown away", left.InPlace > 20f, $"{left.InPlace}" );
		Check( "the report leaves the clip's playback as it was", !clip.PlayInPlace );
	}

	static void TestRootMotionRefusals()
	{
		var s = Walker();
		var clip = Walk( s );
		var tracks = clip.Tracks.Count;

		var missing = RootMotion.Extract( clip, s, new RootMotionSettings { Bone = "root", Source = "hips" } );
		Check( "a missing source bone is refused with a remedy", !missing.Ok && missing.Error.Contains( "hips" ) && missing.Error.Contains( "pelvis" ) );

		var upside = RootMotion.Extract( clip, s, new RootMotionSettings { Bone = "foot_L", Source = "pelvis" } );
		Check( "a root that hangs under the hips is refused", !upside.Ok && upside.Error.Contains( "is under" ) );
		Check( "and a refusal leaves the clip untouched", clip.Tracks.Count == tracks && clip.RootMotion is null );

		var still = new RigAnimFile();
		Check( "a clip with no motion is refused", !RootMotion.Extract( still, s, new RootMotionSettings { Source = "pelvis" } ).Ok );

		var already = Walk( s );
		var same = RootMotion.Extract( already, s, new RootMotionSettings { Bone = "pelvis", Source = "pelvis" } );
		Check( "travel already on the root just marks the clip, and says so", same.Ok && same.Notes.Count == 1 && already.RootMotion is not null && already.Find( "root" ) is null );
	}

	static void TestRootMotionFileAndVmdl()
	{
		var s = Walker();
		var clip = Walk( s );
		RootMotion.Extract( clip, s, new RootMotionSettings { Bone = "root", Source = "pelvis", Mode = RootMotionMode.XYYaw, Linear = false } );
		var back = RigAnimFile.Read( RigAnimWriter.Write( clip ) );

		Check( "root motion is written to and read from the .riganim",
			back.RootMotion is { Bone: "root", Source: "pelvis", Mode: RootMotionMode.XYYaw, Linear: false, Enabled: true } );
		Check( "and its mode reads by ordinal too",
			RigAnimFile.Read( "{\"RootMotion\":{\"Bone\":\"root\",\"Mode\":2}}" ).RootMotion?.Mode == RootMotionMode.Full );
		Check( "a clip without it reads none", RigAnimFile.Read( "{}" ).RootMotion is null );

		var file = VmdlAnimation.AnimFile( new VmdlAnimation.ClipEntry( "walk", "walk.dmx", true, back.RootMotion ) );
		Check( "the vmdl clip carries ModelDoc's ExtractMotion on the root bone",
			file.Contains( "_class = \"ExtractMotion\"" ) && file.Contains( "root_bone_name = \"root\"" ) && file.Contains( "motion_type = \"Single\"" ) );
		Check( "XY + Yaw extracts x, y and the turn, not the height",
			file.Contains( "extract_tx = true" ) && file.Contains( "extract_ty = true" ) && file.Contains( "extract_tz = false" ) && file.Contains( "extract_rz = true" ) && file.Contains( "linear = false" ) );
		Check( "the children list sits inside the AnimFile, after its name",
			file.IndexOf( "name = \"walk\"", StringComparison.Ordinal ) < file.IndexOf( "children", StringComparison.Ordinal )
			&& file.IndexOf( "children", StringComparison.Ordinal ) < file.IndexOf( "activity_name", StringComparison.Ordinal ) );
		Check( "a clip without root motion writes no children at all",
			!VmdlAnimation.AnimFile( new VmdlAnimation.ClipEntry( "c", "c.dmx" ) ).Contains( "children" ) );
	}

	/// <summary>Citizen's layout: pelvis and root_IK are both top bones.</summary>
	static void TestRootMotionBesideTheHips()
	{
		var s = new Skeleton();
		var pelvis = s.AddBone( "pelvis", -1, Xform.Translate( new Vec3( 0, 0, 36 ) ) );
		s.AddBone( "foot_L", pelvis, Xform.Translate( new Vec3( 0, 6, -32 ) ) );
		s.AddBone( "foot_R", pelvis, Xform.Translate( new Vec3( 0, -6, -32 ) ) );
		var root = s.AddBone( "root_IK", -1, Xform.Identity );
		s.AddBone( "foot_L_IK_target", root, Xform.Translate( new Vec3( 0, 6, 4 ) ) );
		var clip = Walk( s );
		var r = RootMotion.Extract( clip, s, new RootMotionSettings { Bone = "root_IK", Source = "pelvis", Linear = false } );

		Check( "beside the hips (Citizen): extraction works and says how", r.Ok && r.Notes.Count == 1, r.ToString() );
		var hip = World( s, clip, Span )[pelvis].Origin;
		Check( "the hips are keyed in place", MathF.Abs( hip.x ) < 0.01f && MathF.Abs( hip.y ) < 0.01f, hip.ToString() );
		Check( "and root_IK carries the travel", (World( s, clip, Span )[root].Origin - new Vec3( 60, 0, 0 )).Length < 0.01f );
		Check( "its children keep their place in the model", World( s, clip, Span )[s.IndexOf( "foot_L_IK_target" )].Origin.AlmostEquals( new Vec3( 0, 6, 4 ), 1e-3f ) );
	}
}
