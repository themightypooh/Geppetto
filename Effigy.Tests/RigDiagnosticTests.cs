using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The rig's own problems, found here rather than by the compiler.
///
/// Every one of these leaves the tool as something else's error message: an unweighted vertex
/// arrives as a vertex that does not move, a zero-length bone as a bone with no orientation, a
/// stale assignment as nothing at all. They are all knowable while the numbers that caused them are
/// still to hand, which is what this is for.
/// </summary>
public static class RigDiagnosticTests
{
	public static void Run()
	{
		Section( "rig: problems named before the exporter finds them" );
		TestACleanRigSaysNothing();
		TestAZeroLengthBoneIsAnError();
		TestDuplicateNamesAreAnError();
		TestACycleIsCaughtRatherThanRecursedInto();
		TestRootCountIsReported();
		TestStaleAndMissingAssignments();
		TestWeightProblemsComeThroughInTheValidatorsOwnWords();
		TestSeverityIsTheFeatureDistinction();
	}

	/// <summary>A two-bone chain with real lengths, which is the shape everything else deviates from.</summary>
	static Skeleton Chain()
	{
		var skeleton = new Skeleton();
		var root = skeleton.AddBoneFromPoints( "root", -1, Vec3.Zero, new Vec3( 0, 0, 1 ) );

		skeleton.AddBoneFromPoints( "spine", root, new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 2 ) );

		return skeleton;
	}

	static void TestACleanRigSaysNothing()
	{
		// The check that keeps the rest honest: a diagnostic that always finds something is one
		// nobody reads.
		var problems = RigDiagnostics.Check( Chain() );

		Check( "a clean two-bone chain reports nothing", problems.Count == 0,
			string.Join( "; ", problems.Select( p => p.Problem ) ) );
		Check( "and has no worst severity to colour a header with",
			RigDiagnostics.Worst( problems ) is null );
	}

	static void TestAZeroLengthBoneIsAnError()
	{
		// A bone written as a head and a direction, with no direction, comes out pointing somewhere
		// arbitrary - and the model reads as the animation being broken rather than the rig.
		//
		// Built through AddBone rather than AddBoneFromPoints, because that one already refuses this
		// outright. The paths that can still produce it are a length passed in directly and a
		// document read off disk, which is exactly what this diagnostic is for.
		var skeleton = Chain();
		skeleton.AddBone( "stub", 0, Xform.Identity, 0f );

		var problems = RigDiagnostics.Check( skeleton );

		Check( "a zero-length bone is found", problems.Any( p => p.Problem.Contains( "no length" ) ),
			string.Join( "; ", problems.Select( p => p.Problem ) ) );
		Check( "as an error rather than a warning",
			problems.First( p => p.Problem.Contains( "no length" ) ).Severity == RigSeverity.Error );
		Check( "and it names which bone, so a panel can select it",
			problems.First( p => p.Problem.Contains( "no length" ) ).Bone == 2 );
	}

	static void TestDuplicateNamesAreAnError()
	{
		// Everything that drives a rig addresses bones by name, so the second one can never be
		// reached - which looks like that bone simply not working.
		//
		// Renamed onto the collision by hand, because AddBone and RenameBone both refuse it. What
		// they cannot guard is the Name field itself, and a document read off disk.
		var skeleton = Chain();
		skeleton.AddBoneFromPoints( "arm", 0, new Vec3( 1, 0, 0 ), new Vec3( 1, 0, 1 ) );
		skeleton.Bones[2].Name = "spine";

		var problems = RigDiagnostics.Check( skeleton );

		Check( "two bones with one name is found",
			problems.Any( p => p.Problem.Contains( "Two bones are called" ) ),
			string.Join( "; ", problems.Select( p => p.Problem ) ) );
	}

	static void TestACycleIsCaughtRatherThanRecursedInto()
	{
		// WorldBind walks parents. A loop there is a stack overflow, which says nothing about which
		// bone caused it - and this is cheap to find while the answer is still nameable.
		var skeleton = Chain();
		skeleton.Bones[0].Parent = 1;

		var problems = RigDiagnostics.Check( skeleton );

		Check( "a parent loop is found rather than recursed into",
			problems.Any( p => p.Problem.Contains( "own ancestor" ) ),
			string.Join( "; ", problems.Select( p => p.Problem ) ) );
		Check( "and the rootless skeleton it implies is named too",
			problems.Any( p => p.Problem.Contains( "no root" ) ) );
	}

	static void TestRootCountIsReported()
	{
		var skeleton = Chain();
		skeleton.AddBoneFromPoints( "stray", -1, new Vec3( 5, 0, 0 ), new Vec3( 5, 0, 1 ) );

		var problems = RigDiagnostics.Check( skeleton );
		var roots = problems.FirstOrDefault( p => p.Problem.Contains( "roots" ) );

		Check( "a second root is reported", roots is not null,
			string.Join( "; ", problems.Select( p => p.Problem ) ) );

		// A WARNING, not an error. The exporters handle several roots; engines and retargeting tools
		// assume one. It builds, and you should look at it - which is exactly what a warning means
		// everywhere else in this kernel.
		Check( "as a warning, because it exports fine and is usually an accident",
			roots.Severity == RigSeverity.Warning );
	}

	static void TestStaleAndMissingAssignments()
	{
		var skeleton = Chain();

		var map = new Dictionary<string, string>
		{
			["body1"] = "spine",
			["body2"] = "elbow",
			["gone"] = "root",
		};

		var problems = RigDiagnostics.Check( skeleton, null, map, new[] { "body1", "body2" } );

		Check( "a part assigned to a bone that does not exist is an error",
			problems.Any( p => p.Problem.Contains( "bone that does not exist" )
				&& p.Severity == RigSeverity.Error ),
			string.Join( "; ", problems.Select( p => p.Problem ) ) );

		// The one that actually happens: a rebuild changed the tree, the body is gone, the assignment
		// survives pointing at nothing, and the part just stops following its bone.
		Check( "an assignment left over from a body that is gone is a warning",
			problems.Any( p => p.Problem.Contains( "part that is gone" )
				&& p.Severity == RigSeverity.Warning ) );

		Check( "and the assignment that is fine says nothing",
			problems.Count( p => p.Problem.Contains( "assign" ) || p.Problem.Contains( "part" ) ) == 2 );
	}

	static void TestWeightProblemsComeThroughInTheValidatorsOwnWords()
	{
		var skeleton = Chain();
		var mesh = Primitives.Box( 1, 1, 1 );

		var noWeights = RigDiagnostics.Check( skeleton, mesh );

		Check( "bones with no weights at all is a warning, not silence",
			noWeights.Any( p => p.Problem.Contains( "no weights" ) && p.Severity == RigSeverity.Warning ),
			string.Join( "; ", noWeights.Select( p => p.Problem ) ) );

		// Weights that exist and are wrong. SkinWeights.Validate already names the vertex and the
		// number; passing its words straight through is the point - a paraphrase loses the part
		// worth reading.
		mesh.Skin = new SkinWeights( mesh.VertexCount );

		for ( var i = 0; i < mesh.VertexCount; i++ )
			mesh.Skin[i] = new[] { new BoneWeight( 0, i == 3 ? 0.5f : 1f ) };

		var problems = RigDiagnostics.Check( skeleton, mesh );

		Check( "weights that do not sum to one are reported",
			problems.Any( p => p.Severity == RigSeverity.Error && p.Cause.Contains( "3" ) ),
			string.Join( "; ", problems.Select( p => p.Cause ) ) );
	}

	static void TestSeverityIsTheFeatureDistinction()
	{
		// The same split features make: an error means it will not work, a warning means it will and
		// you should look. Collapsing them is how one stray unweighted vertex ends up reading the
		// same as no weights at all.
		var clean = RigDiagnostics.Check( Chain() );
		var warned = RigDiagnostics.Check( Chain(), Primitives.Box( 1, 1, 1 ) );

		var broken = Chain();
		broken.AddBone( "stub", 0, Xform.Identity, 0f );

		Check( "clean has no severity", RigDiagnostics.Worst( clean ) is null );
		Check( "a warning-only rig reports Warning", RigDiagnostics.Worst( warned ) == RigSeverity.Warning );
		Check( "and one error outranks any number of warnings",
			RigDiagnostics.Worst( RigDiagnostics.Check( broken, Primitives.Box( 1, 1, 1 ) ) ) == RigSeverity.Error );
	}
}
