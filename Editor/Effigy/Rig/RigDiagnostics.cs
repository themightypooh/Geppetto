using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

public enum RigSeverity
{
	Warning,
	Error,
}

/// <summary>One thing wrong with a rig, in the shape a panel can show.</summary>
public sealed class RigProblem
{
	public readonly RigSeverity Severity;
	public readonly string Problem;
	public readonly string Cause;
	public readonly string Remedy;

	/// <summary>The bone it is about, or -1 for something about the rig as a whole. A panel uses
	/// this to select the offending bone when the row is clicked.</summary>
	public readonly int Bone;

	public RigProblem( RigSeverity severity, string problem, string cause, string remedy, int bone = -1 )
	{
		Severity = severity;
		Problem = problem;
		Cause = cause;
		Remedy = remedy;
		Bone = bone;
	}

	public override string ToString() => $"{Severity}: {Problem} - {Cause}";
}

/// <summary>
/// Everything wrong with a rig, found before the exporter finds it.
///
/// WHY BEFORE. A rig leaves this tool through DmxWriter or SmdWriter and then through the engine's
/// compiler, and each of those reports what IT could not do rather than what is wrong: an unweighted
/// vertex arrives as a vertex that does not move, a zero-length bone as a bone with no orientation,
/// a bone mapped to a body that no longer exists as nothing at all. Every one of those is knowable
/// here, where the numbers that caused it are still to hand.
///
/// SEVERITY IS THE SAME DISTINCTION FEATURES MAKE. An error means the rig will not work; a warning
/// means it will, and you should look at it. Collapsing the two is how a model with one stray
/// unweighted vertex ends up indistinguishable from one with no weights at all.
///
/// SOME OF THIS OVERLAPS WITH WHAT Skeleton ALREADY REFUSES, and that is deliberate rather than
/// redundant. AddBone will not take a duplicate name or a bad parent, AddBoneFromPoints will not
/// make a zero-length bone, and RenameBone will not rename onto a collision - so a rig built through
/// that API cannot reach most of these states. What can: the Bone fields are public and get written
/// directly, a skeleton read back off disk has been through none of those constructors, and
/// RemoveBone re-indexes everything around it. A check that only fires on a file somebody else wrote
/// is still worth having; it just should not be mistaken for the first line of defence.
/// </summary>
public static class RigDiagnostics
{
	/// <summary>A bone shorter than this has no direction anything can read off it.</summary>
	public const float MinimumBoneLength = 1e-4f;

	/// <summary>
	/// Check a skeleton, and optionally the mesh it deforms and the body-to-bone map that assigned
	/// it. Everything is optional except the skeleton, so a panel can report on what it has.
	/// </summary>
	public static List<RigProblem> Check( Skeleton skeleton, PolyMesh mesh = null,
		IReadOnlyDictionary<string, string> bodyBoneMap = null, IReadOnlyCollection<string> bodyIds = null )
	{
		var problems = new List<RigProblem>();

		if ( skeleton is null || skeleton.Count == 0 )
		{
			problems.Add( new RigProblem( RigSeverity.Error,
				"This model has no skeleton",
				"Nothing has been rigged yet, so there are no bones to weight against.",
				"Add bones in the rig panel, or export it as a static model" ) );

			return problems;
		}

		CheckBones( skeleton, problems );
		CheckMap( skeleton, bodyBoneMap, bodyIds, problems );
		CheckWeights( skeleton, mesh, problems );

		return problems;
	}

	static void CheckBones( Skeleton skeleton, List<RigProblem> problems )
	{
		var names = new Dictionary<string, int>( StringComparer.Ordinal );

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			var bone = skeleton.Bones[i];

			// A ZERO-LENGTH BONE HAS NO ORIENTATION. Every exporter here writes a bone as a head and
			// a direction, and a direction of nothing comes out as an arbitrary one - so the bone
			// twists to somewhere unrelated the first time anything drives it, and the model looks
			// like the animation is broken rather than the rig.
			//
			// AddBoneFromPoints already refuses to make one, so this covers the paths that do not go
			// through it: a length handed in directly, and a skeleton read back off disk.
			if ( bone.Length < MinimumBoneLength )
			{
				problems.Add( new RigProblem( RigSeverity.Error,
					$"Bone '{bone.Name}' has no length",
					$"Its length is {bone.Length:0.#####}, so it has no direction for anything to read off it.",
					"Drag its tail away from its head, or delete it", i ) );
			}

			if ( string.IsNullOrWhiteSpace( bone.Name ) )
			{
				problems.Add( new RigProblem( RigSeverity.Error,
					$"Bone {i} has no name",
					"Exporters and every rig asset refer to bones by name, so an unnamed one cannot be addressed.",
					"Give it a name", i ) );
			}
			else if ( names.TryGetValue( bone.Name, out var first ) )
			{
				// Two bones with one name is not a cosmetic problem: a .ctrlrig, an animation and the
				// engine's own lookups all address bones by name, and every one of them will find the
				// first and silently never drive the second.
				problems.Add( new RigProblem( RigSeverity.Error,
					$"Two bones are called '{bone.Name}'",
					$"Bones {first} and {i} share a name, and everything that drives a rig addresses "
					+ "bones by name - so one of them can never be reached.",
					"Rename one of them", i ) );
			}
			else
			{
				names[bone.Name] = i;
			}

			if ( bone.Parent >= skeleton.Count || bone.Parent < -1 )
			{
				problems.Add( new RigProblem( RigSeverity.Error,
					$"Bone '{bone.Name}' has no parent to hang from",
					$"Its parent is {bone.Parent}, and this skeleton has {skeleton.Count} bones.",
					"Re-parent it, or make it a root", i ) );

				continue;
			}

			// A bone that is its own ancestor makes WorldBind recurse for ever. Cheap to find here
			// and impossible to diagnose from the stack overflow it otherwise causes.
			var walked = 0;

			for ( var p = bone.Parent; p >= 0; p = skeleton.Bones[p].Parent )
			{
				if ( p == i || ++walked > skeleton.Count )
				{
					problems.Add( new RigProblem( RigSeverity.Error,
						$"Bone '{bone.Name}' is its own ancestor",
						"Its parent chain loops back to itself, so it has no world position at all.",
						"Re-parent it to a bone above it in the tree", i ) );

					break;
				}
			}
		}

		var roots = 0;

		foreach ( var bone in skeleton.Bones )
		{
			if ( bone.Parent < 0 )
				roots++;
		}

		if ( roots == 0 )
		{
			problems.Add( new RigProblem( RigSeverity.Error,
				"This skeleton has no root",
				"Every bone has a parent, so the whole thing is a loop.",
				"Make one bone a root" ) );
		}
		else if ( roots > 1 )
		{
			// Not an error - the exporters handle it - but engines and retargeting tools routinely
			// assume one, and it is nearly always an accident.
			problems.Add( new RigProblem( RigSeverity.Warning,
				$"This skeleton has {roots} roots",
				"Most engines and every retargeting tool assume a single root, and several roots are "
				+ "usually a bone that lost its parent rather than a decision.",
				"Parent the strays to the main root" ) );
		}
	}

	static void CheckMap( Skeleton skeleton, IReadOnlyDictionary<string, string> bodyBoneMap,
		IReadOnlyCollection<string> bodyIds, List<RigProblem> problems )
	{
		if ( bodyBoneMap is null )
			return;

		foreach ( var (bodyId, boneName) in bodyBoneMap )
		{
			// THE FAILURE THIS EXISTS FOR: a rebuild changed the tree, the body is gone, and its
			// assignment survives in the map pointing at nothing. Nothing complains, the part simply
			// stops following its bone, and it reads as the weighting being wrong.
			if ( bodyIds is not null && !bodyIds.Contains( bodyId ) )
			{
				problems.Add( new RigProblem( RigSeverity.Warning,
					$"A bone assignment points at a part that is gone",
					$"Body '{bodyId}' was assigned to bone '{boneName}', and the model no longer has it.",
					"Assign the bone again on the current model" ) );

				continue;
			}

			if ( skeleton.IndexOf( boneName ) < 0 )
			{
				problems.Add( new RigProblem( RigSeverity.Error,
					$"A part is assigned to a bone that does not exist",
					$"Body '{bodyId}' names bone '{boneName}', which is not in this skeleton.",
					"Assign it to a bone that exists, or add that bone back" ) );
			}
		}
	}

	static void CheckWeights( Skeleton skeleton, PolyMesh mesh, List<RigProblem> problems )
	{
		if ( mesh is null )
			return;

		if ( mesh.Skin is null )
		{
			problems.Add( new RigProblem( RigSeverity.Warning,
				"This model has bones but no weights",
				$"The skeleton has {skeleton.Count} bones and nothing is bound to them, so the mesh "
				+ "will not deform at all.",
				"Bind the parts to bones in the rig panel" ) );

			return;
		}

		// Handed straight through rather than reworded. SkinWeights.Validate already names the
		// vertex and the number, and paraphrasing it would lose exactly the part worth reading.
		var errors = mesh.Skin.Validate( mesh.VertexCount, skeleton.Count );

		if ( errors.Count == 0 )
			return;

		const int show = 8;

		for ( var i = 0; i < errors.Count && i < show; i++ )
		{
			problems.Add( new RigProblem( RigSeverity.Error,
				"The weights do not describe this mesh",
				errors[i],
				"Re-bind the parts, or fix the weights by hand" ) );
		}

		if ( errors.Count > show )
		{
			problems.Add( new RigProblem( RigSeverity.Error,
				$"and {errors.Count - show} more weight problems",
				$"{errors.Count} in total. They are almost always one cause rather than {errors.Count}.",
				"Fix the first one and check again" ) );
		}
	}

	/// <summary>The worst severity present, or null when the rig is clean - what a panel colours its
	/// header with.</summary>
	public static RigSeverity? Worst( IReadOnlyList<RigProblem> problems )
	{
		RigSeverity? worst = null;

		foreach ( var problem in problems )
		{
			if ( problem.Severity == RigSeverity.Error )
				return RigSeverity.Error;

			worst = RigSeverity.Warning;
		}

		return worst;
	}
}
