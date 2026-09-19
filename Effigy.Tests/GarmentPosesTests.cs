using System;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The test poses, on the citizen skeleton: each has to move the joint it names, in the direction
/// it names, and leave the rest of the body where it was. A pose that turned the wrong bone or
/// the wrong way would still draw something plausible, which is why the directions are pinned.
/// </summary>
public static class GarmentPosesTests
{
	public static void Run()
	{
		Report.Section( "garment poses: sides and chains" );
		TestSides();

		Report.Section( "garment poses: every pose moves what it says" );
		TestArmsUp();
		TestSit();
		TestBend();
		TestStride();
		TestEveryPoseMovesSomething();
	}

	static void TestSides()
	{
		Report.Check( "arm_upper_L is the left", GarmentPoses.SideOf( "arm_upper_L" ) == +1 );
		Report.Check( "arm_upper_R_twist1 is the right", GarmentPoses.SideOf( "arm_upper_R_twist1" ) == -1 );
		Report.Check( "mixamorig:LeftForeArm is the left", GarmentPoses.SideOf( "mixamorig:LeftForeArm" ) == +1 );
		Report.Check( "upperarm_r is the right", GarmentPoses.SideOf( "upperarm_r" ) == -1 );
		Report.Check( "spine_1 is the middle", GarmentPoses.SideOf( "spine_1" ) == 0 );
		Report.Check( "ring_finger is not a side", GarmentPoses.SideOf( "finger_ring_0_L" ) == +1 );
	}

	static GarmentPoses.Pose Pose( string name ) => GarmentPoses.All.First( p => p.Name == name );

	static void TestArmsUp()
	{
		var s = CitizenSkeleton.Build();
		var handBefore = s.HeadWorld( s.IndexOf( "hand_L" ) );
		var headBefore = s.HeadWorld( s.IndexOf( "head" ) );
		var footBefore = s.HeadWorld( s.IndexOf( "ankle_L" ) );

		var turned = GarmentPoses.Apply( s, Pose( "Arms up" ) );

		var hand = s.HeadWorld( s.IndexOf( "hand_L" ) );
		var handR = s.HeadWorld( s.IndexOf( "hand_R" ) );

		Report.Check( "two upper arms turned, nothing else", turned == 2 );
		Report.Check( "the left hand went up", hand.z > handBefore.z + 10f );
		Report.Check( "the right hand went up too (mirrored)", handR.z > handBefore.z + 10f );
		Report.Check( "the head did not move", (s.HeadWorld( s.IndexOf( "head" ) ) - headBefore).Length < 1e-3f );
		Report.Check( "the feet did not move", (s.HeadWorld( s.IndexOf( "ankle_L" ) ) - footBefore).Length < 1e-3f );
	}

	static void TestSit()
	{
		var s = CitizenSkeleton.Build();
		var kneeBefore = s.HeadWorld( s.IndexOf( "leg_lower_L" ) );
		var ankleBefore = s.HeadWorld( s.IndexOf( "ankle_L" ) );

		GarmentPoses.Apply( s, Pose( "Sit" ) );

		var knee = s.HeadWorld( s.IndexOf( "leg_lower_L" ) );
		var ankle = s.HeadWorld( s.IndexOf( "ankle_L" ) );

		Report.Check( "the knee came forward", knee.x > kneeBefore.x + 8f );
		Report.Check( "the knee came up to hip height", knee.z > kneeBefore.z + 8f );
		Report.Check( "the shin hangs down from the knee", ankle.z < knee.z - 5f );
		Report.Check( "the ankle is not still on the floor line", (ankle - ankleBefore).Length > 5f );
	}

	static void TestBend()
	{
		var s = CitizenSkeleton.Build();
		var headBefore = s.HeadWorld( s.IndexOf( "head" ) );
		var pelvisBefore = s.HeadWorld( s.IndexOf( "pelvis" ) );

		GarmentPoses.Apply( s, Pose( "Bend" ) );

		var head = s.HeadWorld( s.IndexOf( "head" ) );

		Report.Check( "bending forward takes the head forward", head.x > headBefore.x + 8f );
		Report.Check( "and down", head.z < headBefore.z - 3f );
		Report.Check( "the pelvis stays", (s.HeadWorld( s.IndexOf( "pelvis" ) ) - pelvisBefore).Length < 1e-3f );
	}

	static void TestStride()
	{
		var s = CitizenSkeleton.Build();
		var l = s.HeadWorld( s.IndexOf( "ankle_L" ) );
		var r = s.HeadWorld( s.IndexOf( "ankle_R" ) );

		GarmentPoses.Apply( s, Pose( "Stride" ) );

		Report.Check( "the left foot is forward", s.HeadWorld( s.IndexOf( "ankle_L" ) ).x > l.x + 5f );
		Report.Check( "the right foot is back", s.HeadWorld( s.IndexOf( "ankle_R" ) ).x < r.x - 3f );
	}

	static void TestEveryPoseMovesSomething()
	{
		foreach ( var pose in GarmentPoses.All )
		{
			var s = CitizenSkeleton.Build();
			var before = Enumerable.Range( 0, s.Count ).Select( s.HeadWorld ).ToArray();
			var turned = GarmentPoses.Apply( s, pose );
			var moved = Enumerable.Range( 0, s.Count ).Count( i => (s.HeadWorld( i ) - before[i]).Length > 0.5f );

			Report.Check( $"{pose.Name} turns bones and moves the skeleton", turned > 0 && moved > 0 );
		}
	}
}
