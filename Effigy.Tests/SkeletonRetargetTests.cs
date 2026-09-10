using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Moving a skinned mesh onto somebody else's skeleton.
///
/// WHAT THESE ARE GUARDING. A retarget has no natural failure - a wrong one produces a mesh that
/// builds, exports and loads, and only looks wrong once something animates it, by which point the
/// suspicion falls on the weights or the anim graph rather than on the transform that moved it.
/// So the properties worth pinning are the ones that hold regardless of the rig: retargeting onto
/// the same skeleton must change nothing, a rigidly-weighted vertex must land exactly where its
/// bone went, and weight that cannot be placed must be counted rather than quietly dropped.
/// </summary>
public static class SkeletonRetargetTests
{
	public static void Run()
	{
		Report.Section( "retarget: onto the same skeleton, nothing moves" );
		TestIdentity();

		Report.Section( "retarget: a rigid vertex follows its bone exactly" );
		TestRigidFollow();

		Report.Section( "retarget: weight that cannot be placed is reported" );
		TestUnmapped();

		Report.Section( "retarget: a roll-only disagreement moves nothing" );
		TestRollIsNotAdopted();

		Report.Section( "retarget: the citizen bone map resolves" );
		TestCitizenMapResolves();

		Report.Section( "fit: citizen fitted into citizen is citizen" );
		TestFitIdentity();

		Report.Section( "fit: the skeleton moves into the mesh, the mesh stays put" );
		TestFitMovesTheSkeleton();

		Report.Section( "fit: which bones keep their own length under citizen's clips" );
		TestKeepsOwnLength();
	}

	/// <summary>
	/// The fit's no-op. Every bone lands on itself, every aim already points where it did, so the
	/// fitted skeleton has to come back as citizen to the last few decimals - anything else is a
	/// fit that bends a model that needed no bending.
	/// </summary>
	static void TestFitIdentity()
	{
		var citizen = CitizenSkeleton.Build();
		var hand = citizen.IndexOf( "hand_L" );
		var mesh = Triangle( new[] { new BoneWeight( hand, 1f ) } );
		var map = citizen.Bones.ToDictionary( b => b.Name, b => b.Name );

		var fit = SkeletonRetarget.Fit( mesh, citizen, citizen, map, null, CitizenBoneMap.ChainAims() );

		var worstAt = 0f;
		var worstAxis = 0f;

		for ( var b = 0; b < citizen.Count; b++ )
		{
			var was = citizen.WorldBind( b );
			var now = fit.Skeleton.WorldBind( b );

			worstAt = MathF.Max( worstAt, (was.Origin - now.Origin).Length );
			worstAxis = MathF.Max( worstAxis, MathF.Max( (was.X - now.X).Length,
				MathF.Max( (was.Y - now.Y).Length, (was.Z - now.Z).Length ) ) );
		}

		Report.Check( $"every bone stays where it was ({worstAt:0.######} in)", worstAt < 1e-3f );
		Report.Check( $"and faces the way it did ({worstAxis:0.######})", worstAxis < 1e-3f );
		Report.Check( "the mesh did not move", fit.Mesh.Positions[1].AlmostEquals( mesh.Positions[1] ) );
		Report.Check( "and is still on the hand", fit.Mesh.Skin[0].Length == 1 && fit.Mesh.Skin[0][0].Bone == hand );
	}

	/// <summary>
	/// A citizen-style target - +X down the bone, the limb lying along +X, 5in long, a twist
	/// halfway down it - fitted into a source whose limb stands straight up and is twice as long.
	/// Everything the fit promises is checkable here by hand.
	/// </summary>
	static void TestFitMovesTheSkeleton()
	{
		var to = new Skeleton();
		to.AddBone( "root", -1, Xform.Identity );
		to.AddBone( "twist", 0, Xform.Translate( new Vec3( 2.5f, 0, 0 ) ) );
		to.AddBone( "tip", 0, Xform.Translate( new Vec3( 5, 0, 0 ) ) );

		var from = TwoBone( new Vec3( 0, 0, 10 ) );
		var mesh = Triangle( new[] { new BoneWeight( 1, 1f ) } );
		var before = mesh.Positions.ToList();

		var map = new Dictionary<string, string> { ["root"] = "root", ["tip"] = "tip" };
		var aims = new Dictionary<string, string[]> { ["root"] = new[] { "tip" } };

		var fit = SkeletonRetarget.Fit( mesh, from, to, map, null, aims );
		var s = fit.Skeleton;

		Report.Check( $"the tip stands on the source's joint (got {s.WorldBind( 2 ).Origin})",
			s.WorldBind( 2 ).Origin.AlmostEquals( new Vec3( 0, 0, 10 ) ) );

		Report.Check( $"the root's +X now runs up the source limb (got {s.WorldBind( 0 ).X})",
			s.WorldBind( 0 ).X.AlmostEquals( new Vec3( 0, 0, 1 ) ) );

		Report.Check( $"the tip keeps the target's convention at the source's length (local {s.Bones[2].Local.Origin})",
			s.Bones[2].Local.Origin.AlmostEquals( new Vec3( 10, 0, 0 ) ) );

		Report.Check( $"an unplaced bone is carried and stretched with its parent (got {s.WorldBind( 1 ).Origin})",
			s.WorldBind( 1 ).Origin.AlmostEquals( new Vec3( 0, 0, 5 ) ) );

		var worst = 0f;

		for ( var i = 0; i < before.Count; i++ )
			worst = MathF.Max( worst, (fit.Mesh.Positions[i] - before[i]).Length );

		Report.Check( $"the mesh did not move ({worst:0.######} in)", worst < 1e-6f );
		Report.Check( "and hangs off the target's tip", fit.Mesh.Skin[0].Length == 1 && fit.Mesh.Skin[0][0].Bone == 2 );
		Report.Check( "the target itself was not modified", to.WorldBind( 2 ).Origin.AlmostEquals( new Vec3( 5, 0, 0 ) ) );
	}

	static void TestKeepsOwnLength()
	{
		var c = CitizenSkeleton.Build();
		bool Keeps( string n ) => CitizenBoneMap.KeepsOwnLength( c, c.IndexOf( n ) );

		Report.Check( "the pelvis takes the clip's translation - that is the walk's height and bob", !Keeps( "pelvis" ) );
		Report.Check( "a limb keeps its own length", Keeps( "arm_lower_L" ) && Keeps( "leg_upper_R" ) && Keeps( "spine_1" ) );
		Report.Check( "so does a fingertip", Keeps( "finger_index_2_R" ) );
		Report.Check( "IK targets take the clip's", !Keeps( "root_IK" ) && !Keeps( "foot_L_IK_target" ) && !Keeps( "hand_R_IK_target" ) );
		Report.Check( "so does the two-handed ik rule", !Keeps( "hand_R_to_L_ikrule" ) );

		// Citizen's own markup ignores translation on these; a fitted citizen must not disagree.
		var citizensOwn = new[] { "aim_matrix_01", "aim_matrix_02b", "arm_upper_L_twist0", "arm_lower_R_twist1",
			"leg_upper_L_twist1", "arm_elbow_helper_L", "leg_knee_helper_R", "neck_clothing" };
		var disagree = citizensOwn.Where( n => !Keeps( n ) ).ToList();

		Report.Check( "agrees with citizen's own markup wherever citizen ignores it"
			+ (disagree.Count > 0 ? " - not: " + string.Join( ", ", disagree ) : ""), disagree.Count == 0 );
	}

	/// <summary>A skeleton with one root and one child offset along +Z.</summary>
	static Skeleton TwoBone( Vec3 childAt )
	{
		var s = new Skeleton();
		s.AddBone( "root", -1, Xform.Identity );
		s.AddBone( "tip", 0, Xform.Translate( childAt ) );
		return s;
	}

	static PolyMesh Triangle( BoneWeight[] weights )
	{
		var m = new PolyMesh();
		m.Positions.Add( new Vec3( 0, 0, 0 ) );
		m.Positions.Add( new Vec3( 1, 0, 0 ) );
		m.Positions.Add( new Vec3( 0, 1, 0 ) );
		m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
		m.Skin = new SkinWeights();
		for ( var i = 0; i < 3; i++ )
			m.Skin.Vertices.Add( weights );
		return m;
	}

	static void TestIdentity()
	{
		var skeleton = CitizenSkeleton.Build();
		var mesh = Triangle( new[] { new BoneWeight( skeleton.IndexOf( "hand_L" ), 1f ) } );
		var before = mesh.Positions.ToList();

		var map = skeleton.Bones.ToDictionary( b => b.Name, b => b.Name );
		var result = SkeletonRetarget.To( mesh, skeleton, skeleton, map );

		var worst = 0f;

		for ( var i = 0; i < before.Count; i++ )
			worst = MathF.Max( worst, (result.Mesh.Positions[i] - before[i]).Length );

		Report.Check( $"citizen onto citizen leaves every vertex put ({worst:0.######} in)", worst < 1e-4f );
		Report.Check( "and nothing was stranded", result.VerticesStranded == 0 );
		Report.Check( "and nothing was unmapped", result.Unmapped.Count == 0 );
		Report.Check( "the source mesh was not modified", mesh.Positions[1].AlmostEquals( before[1] ) );
	}

	/// <summary>
	/// One bone, one target, full weight. The vertex must end up displaced by exactly the gap
	/// between the two bind poses - no blending, no averaging, nothing to hide an error behind.
	/// </summary>
	static void TestRigidFollow()
	{
		var from = TwoBone( new Vec3( 0, 0, 10 ) );
		var to = TwoBone( new Vec3( 0, 0, 25 ) );

		var mesh = Triangle( new[] { new BoneWeight( 1, 1f ) } );
		var map = new Dictionary<string, string> { ["root"] = "root", ["tip"] = "tip" };

		var result = SkeletonRetarget.To( mesh, from, to, map );

		var moved = result.Mesh.Positions[0] - new Vec3( 0, 0, 0 );

		Report.Check( $"the vertex moved by the 15in the bone moved (got {moved.z:0.###})",
			MathF.Abs( moved.z - 15f ) < 1e-4f && MathF.Abs( moved.x ) < 1e-4f );

		Report.Check( "and is still weighted to the target's tip",
			result.Mesh.Skin[0].Length == 1 && result.Mesh.Skin[0][0].Bone == 1 );

		Report.Check( "the target's unused bones are counted", result.TargetBonesUnused == 1 );
	}

	static void TestUnmapped()
	{
		var from = TwoBone( new Vec3( 0, 0, 10 ) );
		var to = TwoBone( new Vec3( 0, 0, 25 ) );

		// Half the weight is on a bone the map does not place.
		var mesh = Triangle( new[] { new BoneWeight( 0, 0.5f ), new BoneWeight( 1, 0.5f ) } );
		var map = new Dictionary<string, string> { ["tip"] = "tip" };

		var result = SkeletonRetarget.To( mesh, from, to, map );

		Report.Check( "the unplaced bone is named", result.Unmapped.Contains( "root" ) );
		Report.Check( "every vertex was renormalised", result.VerticesRenormalised == 3 );
		Report.Check( "nothing was stranded", result.VerticesStranded == 0 );

		var w = result.Mesh.Skin[0];

		Report.Check( $"and what is left sums to 1 (got {w.Sum( x => x.Weight ):0.###})",
			MathF.Abs( w.Sum( x => x.Weight ) - 1f ) < 1e-4f );

		// With nothing placeable at all, the vertex cannot be moved and has to say so.
		var stranded = SkeletonRetarget.To( Triangle( new[] { new BoneWeight( 0, 1f ) } ),
			from, to, new Dictionary<string, string> { ["tip"] = "tip" } );

		Report.Check( "a vertex with no placeable weight is counted as stranded",
			stranded.VerticesStranded == 3 );
	}

	/// <summary>
	/// Every target the map names has to exist. Fifty-odd hand-written entries is exactly the size
	/// where a typo survives review and then shows up as one finger that will not bend.
	/// </summary>
	static void TestCitizenMapResolves()
	{
		var citizen = CitizenSkeleton.Build();
		var map = CitizenBoneMap.UnrealStyle();
		var missing = map.Where( kv => citizen.IndexOf( kv.Value ) < 0 ).Select( kv => $"{kv.Key} -> {kv.Value}" ).ToList();

		Report.Check( $"every mapped target is a real citizen bone ({map.Count} entries)"
			+ (missing.Count > 0 ? " - missing: " + string.Join( ", ", missing ) : ""), missing.Count == 0 );

		// The Gearhead is the model this was written against; if its naming stops resolving, the
		// map has drifted away from the thing it was for.
		var gearhead = new[]
		{
			"pelvis", "spine_01", "spine_02", "chest", "neck", "head",
			"clavicle_L", "upperarm_L", "forearm_L", "hand_L",
			"thigh_L", "calf_L", "foot_L", "toe_L",
			"index_01_L", "middle_02_L", "ring_03_L", "pinky_01_L",
			"thumb_01_L", "thumb_02_L", "brow_L", "lid_L",
		};

		var unplaced = gearhead.Where( n => !map.ContainsKey( n ) ).ToList();

		Report.Check( "every Gearhead bone name is placed"
			+ (unplaced.Count > 0 ? " - missing: " + string.Join( ", ", unplaced ) : ""), unplaced.Count == 0 );

		Report.Check( "the pinky rides the ring finger", map["pinky_02_L"] == "finger_ring_1_L" );
		Report.Check( "the brow goes rigid on the head", map["brow_R"] == "head" );
	}

	/// <summary>
	/// THE ONE THAT COST A DAY. Two skeletons can put every joint in the same place and still
	/// disagree about each bone's roll, because aiming a bone head to tail leaves it free to spin
	/// about its own length and every rig picks that spin by a convention of its own - citizen's
	/// out of its fbx, an Effigy bone's out of whichever seed axis it leaned on least.
	///
	/// Nothing about that disagreement is real, so nothing should move. Taking the target's frame
	/// wholesale instead spins each limb about its own length by a different arbitrary angle,
	/// which does not read as a convention mismatch - it reads as the mesh having been shredded,
	/// and it sends you looking at the weights.
	/// </summary>
	static void TestRollIsNotAdopted()
	{
		var from = new Skeleton();
		from.AddBone( "root", -1, Xform.Identity );
		from.AddBone( "limb", 0, new Xform(
			new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 0, 4, 0 ) ) );

		// Same joint, same direction down the bone, a quarter turn of roll about it.
		var to = new Skeleton();
		to.AddBone( "root", -1, Xform.Identity );
		to.AddBone( "limb", 0, new Xform(
			new Vec3( 0, 0, -1 ), new Vec3( 0, 1, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 4, 0 ) ) );

		var mesh = Triangle( new[] { new BoneWeight( 1, 1f ) } );
		var before = mesh.Positions.ToList();

		var map = new Dictionary<string, string> { ["root"] = "root", ["limb"] = "limb" };
		var result = SkeletonRetarget.To( mesh, from, to, map );

		var worst = 0f;

		for ( var i = 0; i < before.Count; i++ )
			worst = MathF.Max( worst, (result.Mesh.Positions[i] - before[i]).Length );

		Report.Check( $"a bone rolled 90 degrees under the mesh moves it nowhere ({worst:0.######} in)",
			worst < 1e-4f );
	}
}
