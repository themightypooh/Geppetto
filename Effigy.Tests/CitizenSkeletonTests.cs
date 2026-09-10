using System;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The citizen skeleton table, checked against the things that would make it silently wrong.
///
/// WHY THIS IS WORTH TESTING AT ALL, given it is a generated table nobody edits by hand. A wrong
/// bind pose here does not fail anything - it builds, it exports, it loads, and the first sign of
/// trouble is a playermodel whose arms are in the wrong place while it walks. The table came out
/// of a converter with a coordinate-system change in it (citizen.fbx is Y-up centimetres, s&amp;box
/// is Z-up inches), and a permutation mistake in that conversion is exactly the kind of thing that
/// looks plausible until it animates. So the model-space positions are pinned against numbers the
/// ENGINE produced - Model.GetBoneTransform, read through EffigySkeletonImport - rather than
/// against the converter's own output, which would only prove it agrees with itself.
/// </summary>
public static class CitizenSkeletonTests
{
	public static void Run()
	{
		Report.Section( "citizen skeleton: the shape of the hierarchy" );
		TestShape();

		Report.Section( "citizen skeleton: the bind pose the engine reports" );
		TestBindPose();

		Report.Section( "citizen skeleton: the bind pose ORIENTATIONS the engine reports" );
		TestOrientation();

		Report.Section( "citizen skeleton: the bones a playermodel has to carry" );
		TestNames();
	}

	static void TestShape()
	{
		var s = CitizenSkeleton.Build();

		Report.Check( "citizen has 95 bones", s.Count == 95 );

		var ordered = true;

		for ( var i = 0; i < s.Count; i++ )
		{
			if ( s.Bones[i].Parent >= i )
				ordered = false;
		}

		Report.Check( "parents always sit at a lower index", ordered );

		var roots = s.Bones.Where( b => b.Parent < 0 ).Select( b => b.Name ).ToList();

		Report.Check( "there are exactly two roots", roots.Count == 2 );
		Report.Check( "the body hangs off pelvis", roots.Contains( "pelvis" ) );
		Report.Check( "and the IK targets off root_IK", roots.Contains( "root_IK" ) );

		Report.Check( "no bone is named twice",
			s.Bones.Select( b => b.Name ).Distinct().Count() == s.Count );
	}

	/// <summary>
	/// Model-space positions, against what the engine itself reported for these bones. Half an
	/// inch of tolerance would hide a coordinate mistake, so this is tight enough that only float
	/// noise fits through.
	/// </summary>
	static void TestBindPose()
	{
		var s = CitizenSkeleton.Build();

		void At( string name, float x, float y, float z )
		{
			var i = s.IndexOf( name );

			if ( i < 0 )
			{
				Report.Check( $"{name} exists", false );
				return;
			}

			var o = s.WorldBind( i ).Origin;
			var d = MathF.Max( MathF.Abs( o.x - x ), MathF.Max( MathF.Abs( o.y - y ), MathF.Abs( o.z - z ) ) );

			Report.Check( $"{name} stands where the engine puts it ({d:0.#####} in out)", d < 0.001f );
		}

		// Straight off Model.GetBoneTransform, via effigy_skeleton_probe.
		At( "pelvis", 1.2666f, 0f, 31.0736f );
		At( "spine_0", 1.2641f, -0.0001f, 34.9954f );
		At( "spine_1", 1.2635f, -0.0001f, 40.6059f );
		At( "spine_2", 1.2660f, -0.0001f, 46.2163f );
		At( "neck_0", 1.1761f, -0.0001f, 52.0364f );
		At( "head", 0.9982f, -0.0001f, 56.5437f );

		// The pose the arms are actually in, which is what a T-posed model has to be bent into.
		// Citizen is A-posed: the hand sits far BELOW the shoulder, not out level with it.
		var shoulder = s.WorldBind( s.IndexOf( "arm_upper_L" ) ).Origin;
		var hand = s.WorldBind( s.IndexOf( "hand_L" ) ).Origin;

		Report.Check( "citizen is A-posed, not T-posed - the hand hangs below the shoulder",
			shoulder.z - hand.z > 10f );
	}

	/// <summary>
	/// THE GAP THAT LET THE BUG THROUGH. <see cref="TestBindPose"/> pins positions, and positions
	/// are roll-independent, so the original table carried a transposed pelvis and 180-degree
	/// thigh flips through every check until something animated. These are the WORLD bind frames
	/// citizen's animations are authored against, read off the engine via
	/// <c>effigy_skeleton_probe ... local</c> and hard-coded here so a regeneration that only
	/// gets positions right again fails.
	/// </summary>
	static void TestOrientation()
	{
		var s = CitizenSkeleton.Build();

		void Axis( string bone, char axis, float x, float y, float z )
		{
			var i = s.IndexOf( bone );

			if ( i < 0 )
			{
				Report.Check( $"{bone} exists", false );
				return;
			}

			var w = s.WorldBind( i );
			var a = axis == 'X' ? w.X : axis == 'Y' ? w.Y : w.Z;
			var d = MathF.Max( MathF.Abs( a.x - x ), MathF.Max( MathF.Abs( a.y - y ), MathF.Abs( a.z - z ) ) );

			Report.Check( $"{bone} +{axis} is ({x},{y},{z}) ({d:0.####} out)", d < 0.001f );
		}

		// Citizen's bones run +X down the bone. The pelvis is the root: +X up, +Y forward, +Z left.
		Axis( "pelvis", 'X', 0f, 0f, 1f );
		Axis( "pelvis", 'Y', 1f, 0f, 0f );
		Axis( "pelvis", 'Z', 0f, 1f, 0f );

		// The spine climbs +Z, so the spine bones' +X points up.
		Axis( "spine_0", 'X', 0f, 0f, 1f );

		// The legs hang down, so their +X points -Z.
		Axis( "leg_upper_R", 'X', 0f, 0f, -1f );
		Axis( "leg_upper_L", 'X', 0f, 0f, -1f );
	}

	static void TestNames()
	{
		var s = CitizenSkeleton.Build();

		foreach ( var name in new[]
			{
				"pelvis", "spine_0", "spine_1", "spine_2", "neck_0", "head",
				"clavicle_L", "arm_upper_L", "arm_lower_L", "hand_L",
				"leg_upper_L", "leg_lower_L", "ankle_L", "ball_L",
				"finger_index_0_L", "finger_thumb_0_L", "hold_L", "eye_L",
			} )
		{
			Report.Check( $"'{name}' is present", s.IndexOf( name ) >= 0 );
		}

		// Citizen has three fingers and a thumb. A model with a pinky has nowhere to put it, and
		// that is a mapping decision rather than a bug - pinning it here so the decision is made
		// against a fact rather than an assumption.
		Report.Check( "citizen has no pinky", s.IndexOf( "finger_pinky_0_L" ) < 0 );
	}
}
