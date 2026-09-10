using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The example humanoid, and the whole playermodel pipeline run over it without an editor.
///
/// WHAT THESE ARE GUARDING, and it is not the sample's looks. A tutorial that hands the reader a
/// model and then tells them to make bones from its parts is making two promises the reader cannot
/// check: that every part can be measured into a bone at all, and that the bones they end up with
/// are names the animations recognise. Both are silent when broken. A part that is slightly wider
/// than it is long yields a bone across the limb, which looks almost right in the viewport and
/// walks like a broken umbrella; a misspelled name produces a model that compiles, loads and holds
/// one limb stiff. Neither shows up as an error anywhere.
///
/// So the suite does the lesson. It derives the bones the way the editor's Bone from Part derives
/// them - same measurement, same anchor rule - walks the same chain list the tutorial's checks read,
/// binds, and fits citizen in. If the sample ever drifts out of tune with the measurement, this
/// fails here rather than in front of somebody following the tutorial.
/// </summary>
public static class PlayermodelSampleTests
{
	public static void Run()
	{
		Report.Section( "humanoid sample: it builds, and every part is measurable" );
		TestSampleBuilds();

		Report.Section( "humanoid sample: the lesson, run headlessly onto citizen" );
		TestLessonFits();

		Report.Section( "playermodel map: every name resolves to a real citizen bone" );
		TestMapResolves();
	}

	/// <summary>
	/// One body per part, each named after its bone, and each with a long axis to measure a bone
	/// along.
	///
	/// MEASURABILITY IS THE POINT. <see cref="BoneFromBody.TryDerive"/> refuses a body with no
	/// dominant axis, and the editor SKIPS such a body with a warning rather than failing - so a
	/// cube-shaped torso in the sample would leave the reader with a rig that is missing a bone and
	/// a console line they never scrolled to.
	/// </summary>
	static void TestSampleBuilds()
	{
		var studio = HumanoidSample.Build();

		Report.Check( "it builds with no errors", !studio.Rebuild().HasErrors );

		var expected = HumanoidSample.Parts.Count;

		Report.Check( $"one body per part ({expected})", studio.Bodies.Count == expected,
			$"{studio.Bodies.Count} bodies" );

		var names = studio.Bodies.Select( b => b.Name ).ToHashSet( StringComparer.Ordinal );

		foreach ( var part in HumanoidSample.Parts )
			Report.Check( $"a body called {part.Name}", names.Contains( part.Name ) );

		foreach ( var body in studio.Bodies )
		{
			Report.Check( $"{body.Name} has an axis to measure a bone along",
				BoneFromBody.TryDerive( body.Mesh, out _, out _, out _, null ) );
		}

		// The soles on the floor, which is the first step's own check and the thing that decides
		// whether citizen's walk plants the feet or leaves them hovering.
		var lowest = studio.Bodies.SelectMany( b => b.Mesh.Positions ).Min( p => p.z );

		Report.Check( "it stands on the floor", MathF.Abs( lowest ) < 0.001f, $"lowest z {lowest:0.###}" );

		// Citizen's pelvis sits at 31.07. The sample's hip part is centred at 31.5 so the measured
		// bone spans 28 to 35 - close enough that the walk's hip height needs no correction, which
		// is the one proportion the fit cannot fix for you.
		var pelvis = studio.Bodies.Single( b => b.Name == "pelvis" );
		var pelvisCentre = pelvis.Mesh.Positions.Average( p => p.z );

		Report.Check( "its hips are near citizen's hip height",
			MathF.Abs( pelvisCentre - CitizenSkeleton.Build().HeadWorld( 0 ).z ) < 1.5f,
			$"sample {pelvisCentre:0.##}, citizen {CitizenSkeleton.Build().HeadWorld( 0 ).z:0.##}" );
	}

	/// <summary>
	/// The lesson from end to end: derive the bones along <see cref="HumanoidSample.Chains"/>, bind
	/// the bodies to them, and fit citizen in.
	///
	/// THE ANCHOR RULE IS THE PART WORTH COPYING EXACTLY. A principal axis is a line and not an
	/// arrow, so a derived bone only knows which end is the root because the caller hands over the
	/// parent's tail - that is what makes a thigh point DOWN from the hip rather than up from the
	/// knee. The editor does this in MakeBonesFromBodies; if this test did it any other way it would
	/// be checking a pipeline nobody runs.
	/// </summary>
	static void TestLessonFits()
	{
		var studio = HumanoidSample.Build();
		var rig = studio.Rig;
		var bodyByName = studio.Bodies.ToDictionary( b => b.Name, StringComparer.Ordinal );
		var skipped = new List<string>();

		foreach ( var chain in HumanoidSample.Chains )
		{
			for ( var i = 0; i < chain.Length; i++ )
			{
				var name = chain[i];

				// The first name in a chain is a bone an earlier chain already made - the spine gives
				// the arms their `chest` and the legs their `pelvis`. Remaking it would give the model
				// three pelvises.
				if ( rig.IndexOf( name ) >= 0 )
					continue;

				var parent = i == 0 ? -1 : rig.IndexOf( chain[i - 1] );
				Vec3? anchor = parent >= 0 ? rig.TailWorld( parent ) : null;

				if ( !BoneFromBody.TryDerive( bodyByName[name].Mesh, out var head, out var tail, out var up, anchor ) )
				{
					skipped.Add( name );
					continue;
				}

				rig.AddBoneFromPoints( name, parent, head, tail, up );
				studio.BodyBoneMap[bodyByName[name].Id] = name;
			}
		}

		Report.Check( "every bone in the chains was measurable", skipped.Count == 0,
			string.Join( ", ", skipped ) );

		var boneCount = HumanoidSample.Chains.SelectMany( c => c ).Distinct( StringComparer.Ordinal ).Count();

		Report.Check( $"the rig has {boneCount} bones", rig.Count == boneCount, $"{rig.Count}" );

		// Legs point down, which the anchor is the only thing that can decide. Checked rather than
		// assumed because the consequence of getting it wrong is a knee that bends backwards, and
		// nothing upstream of a running animation would mention it.
		foreach ( var side in new[] { "L", "R" } )
		{
			var thigh = rig.IndexOf( $"thigh_{side}" );

			Report.Check( $"thigh_{side} points downward",
				rig.TailWorld( thigh ).z < rig.HeadWorld( thigh ).z,
				$"head {rig.HeadWorld( thigh ).z:0.##} tail {rig.TailWorld( thigh ).z:0.##}" );
		}

		var (mesh, ranges) = studio.ToMeshWithBodies();
		var weights = SkinBinder.BindBodies( mesh, ranges, studio.BodyBoneMap, rig );
		weights = SkinBinder.SmoothWeights( mesh, weights );
		mesh.Skin = weights;

		// Where the left hand is BEFORE the fit, so it can be compared with where the fitted bone
		// lands. The whole promise of Fit is that the skeleton moves and the mesh does not.
		var handBefore = rig.HeadWorld( rig.IndexOf( "hand_L" ) );

		var fit = SkeletonRetarget.Fit( mesh, rig, CitizenSkeleton.Build(),
			CitizenBoneMap.Playermodel(), CitizenBoneMap.UnrealStyleRideAlong(), CitizenBoneMap.ChainAims() );

		Report.Check( "no bone was left without an animation name", fit.Unmapped.Count == 0,
			string.Join( ", ", fit.Unmapped ) );

		Report.Check( "no vertex was stranded", fit.VerticesStranded == 0, $"{fit.VerticesStranded}" );

		var fittedHand = fit.Skeleton.HeadWorld( fit.Skeleton.IndexOf( "hand_L" ) );
		var drift = (fittedHand - handBefore).Length;

		Report.Check( "citizen's hand lands on the sample's hand", drift < 0.5f,
			$"{drift:0.###} away" );

		// The mesh is the same mesh. Fit is allowed to move it only through weights that could not
		// be placed, and there are none of those here.
		var moved = 0;

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			if ( (fit.Mesh.Positions[i] - mesh.Positions[i]).Length > 1e-4f )
				moved++;
		}

		Report.Check( "the mesh did not move", moved == 0, $"{moved} vertices moved" );
	}

	/// <summary>
	/// Every value in <see cref="CitizenBoneMap.Playermodel"/> names a bone citizen actually has, and
	/// both naming conventions are in there.
	///
	/// A MAP ENTRY POINTING AT NOTHING IS COUNTED AS UNMAPPED by the fit, so a typo on the TARGET
	/// side of the map reads as "your rig used a name the animations do not know" - which sends the
	/// reader hunting a mistake in their own bone names that is actually in here.
	/// </summary>
	static void TestMapResolves()
	{
		var citizen = CitizenSkeleton.Build();
		var map = CitizenBoneMap.Playermodel();
		var bad = map.Where( e => citizen.IndexOf( e.Value ) < 0 ).Select( e => $"{e.Key}->{e.Value}" ).ToList();

		Report.Check( "every target is a real citizen bone", bad.Count == 0, string.Join( ", ", bad ) );

		foreach ( var name in new[] { "upperarm_L", "thigh_R", "spine_01", "chest", "foot_L" } )
			Report.Check( $"the tutorial's {name} resolves", map.ContainsKey( name ) );

		// And citizen's own spelling, which is the half UnrealStyle alone does not cover.
		foreach ( var name in new[] { "arm_upper_L", "leg_upper_R", "spine_0", "ankle_L" } )
			Report.Check( $"citizen's own {name} resolves to itself", map.TryGetValue( name, out var to ) && to == name );
	}
}
