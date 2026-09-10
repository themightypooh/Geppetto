using System;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Spreading a limb's weight onto the twist bones that share its length.
///
/// WHAT THIS IS GUARDING. Wrong twist weights are invisible until something drives the twist
/// bones, and nothing does until the model carries citizen's constraint list - so the damage
/// surfaces one change after the mistake, looking like a bug in whatever landed last. The
/// property worth pinning is the one that failed: the parent owns the FAR end of the bone, and no
/// twist may ever sit beyond it.
/// </summary>
public static class TwistWeightsTests
{
	public static void Run()
	{
		Report.Section( "twist weights: the parent owns the far end of the bone" );
		TestStationOrder();

		Report.Section( "twist weights: citizen's limbs spread without inverting" );
		TestCitizen();
	}

	/// <summary>
	/// THE ONE THAT MADE A STICK FIGURE. A skeleton built from a table of transforms never sets
	/// Bone.Length, so every bone reports the default 1. Placing the parent's station at that
	/// length put it an inch down a seven inch forearm - before the twist at 3.9 - so everything
	/// past the elbow went to a bone driven at a fraction of the joint's roll, and the limb
	/// collapsed onto its own axis.
	/// </summary>
	static void TestStationOrder()
	{
		var s = new Skeleton();
		s.AddBone( "arm_lower_L", -1, Xform.Identity );
		s.AddBone( "arm_lower_L_twist0", 0, Xform.Identity );
		s.AddBone( "arm_lower_L_twist1", 0, Xform.Translate( new Vec3( 0, 3.9f, 0 ) ) );
		s.AddBone( "hand_L", 0, Xform.Translate( new Vec3( 0, 7.7f, 0 ) ) );

		// Three vertices: at the elbow, midway, and out at the wrist.
		var mesh = new PolyMesh();
		mesh.Positions.Add( new Vec3( 0, 0.2f, 0 ) );
		mesh.Positions.Add( new Vec3( 0, 3.9f, 0 ) );
		mesh.Positions.Add( new Vec3( 0, 7.5f, 0 ) );
		mesh.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
		mesh.Skin = new SkinWeights();

		for ( var i = 0; i < 3; i++ )
			mesh.Skin.Vertices.Add( new[] { new BoneWeight( 0, 1f ) } );

		TwistWeights.Spread( mesh, s );

		string Owner( int v ) => s.Bones[mesh.Skin[v].OrderByDescending( w => w.Weight ).First().Bone].Name;

		Report.Check( $"at the elbow the weight goes to twist0 (got {Owner( 0 )})",
			Owner( 0 ) == "arm_lower_L_twist0" );
		Report.Check( $"midway it goes to twist1 (got {Owner( 1 )})",
			Owner( 1 ) == "arm_lower_L_twist1" );
		Report.Check( $"at the wrist it stays on the bone itself (got {Owner( 2 )})",
			Owner( 2 ) == "arm_lower_L", "the parent's station was placed short - see Extent" );

		foreach ( var v in Enumerable.Range( 0, 3 ) )
		{
			var total = mesh.Skin[v].Sum( w => w.Weight );
			Report.Check( $"vertex {v} still sums to 1 ({total:0.####})", MathF.Abs( total - 1f ) < 1e-4f );
		}
	}

	/// <summary>The real skeleton, where the lengths are all the default and the geometry has to
	/// supply the answer instead.</summary>
	static void TestCitizen()
	{
		var citizen = CitizenSkeleton.Build();
		var mesh = new PolyMesh();
		var bones = new[] { "arm_lower_L", "arm_upper_R", "leg_upper_L", "leg_lower_R" };

		// One vertex out at each limb's far end, where the parent must still own it.
		foreach ( var name in bones )
		{
			var b = citizen.IndexOf( name );
			var far = citizen.WorldBind( b ).Origin + citizen.WorldBind( b ).Y.Normal * 6f;
			mesh.Positions.Add( far );
		}

		mesh.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
		mesh.Skin = new SkinWeights();

		foreach ( var name in bones )
			mesh.Skin.Vertices.Add( new[] { new BoneWeight( citizen.IndexOf( name ), 1f ) } );

		TwistWeights.Spread( mesh, citizen );

		for ( var i = 0; i < bones.Length; i++ )
		{
			var owner = citizen.Bones[mesh.Skin[i].OrderByDescending( w => w.Weight ).First().Bone].Name;

			Report.Check( $"{bones[i]} still owns its own far end (got {owner})",
				!owner.Contains( "twist", StringComparison.Ordinal ) || owner.StartsWith( bones[i] ),
				"a twist took geometry beyond the end of its parent" );
		}
	}
}
