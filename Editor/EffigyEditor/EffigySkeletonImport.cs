using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

// Effigy.Skeleton, not Sandbox.Skeleton - the engine has a Skeleton type of its own, and this file
// reads one of those into one of these, so the ambiguity is the whole subject.
using Skeleton = Effigy.Skeleton;

namespace Marionette.EditorTools;

/// <summary>
/// Read a compiled model's skeleton into an Effigy <see cref="Skeleton"/>.
///
/// WHY THIS EXISTS. Every rig Effigy has made so far was invented in the tool - BoneFromBody names
/// the bones after the bodies they came from, and the bind pose is wherever the user dropped the
/// handles. That is right for a prop and useless for a PLAYERMODEL, because citizen's animations
/// bind by bone NAME against citizen's bind pose. A model that is not literally `pelvis`,
/// `spine_0`, `arm_upper_L` in citizen's hierarchy plays those animations as spaghetti. So the
/// first thing a playermodel path needs is the ability to start from somebody else's skeleton
/// rather than from nothing.
///
/// THE BIND POSE IS COPIED, NOT RE-DERIVED, and that is the whole correctness argument here.
/// Effigy's own bone placement goes through AddBoneFromPoints, which aims a bone +Y head-to-tail
/// and picks the roll about that aim - fine when a human is drawing a chain, wrong here. A
/// compiled bone's LocalTransform carries an orientation that its animation data assumes, and
/// re-deriving axes from head and tail would silently rotate every bone about its own length. So
/// this goes through <see cref="Skeleton.AddBone"/> with the local basis verbatim, and Length is
/// filled in afterwards purely as a tail for the viewport and for auto-weighting to measure to.
///
/// `LocalTransform` IS NOT LOCAL. Despite the name, BoneCollection.Bone.LocalTransform is in
/// MODEL space - it matches Model.GetBoneTransform for that bone exactly, on every one of
/// citizen's 95 bones. Effigy's Bone.Local is relative to the parent, which is what makes a chain
/// behave when a parent moves, so each bone is rebased against its parent's world bind on the way
/// in. Taking the name at face value put citizen's head 99 inches out sideways, because walking
/// the parent chain then compounds a transform that was already absolute.
///
/// TOPOLOGICAL ORDER IS NOT ASSUMED. Skeleton.AddBone refuses a parent that does not exist yet,
/// which is what keeps its list acyclic - so this walks the model's tree from the roots outward
/// rather than iterating 0..BoneCount and hoping the engine's order agrees.
///
/// PROTOTYPE. Nothing calls this from the UI yet; `effigy_skeleton_probe` in
/// <see cref="EffigySkeletonProbe"/> drives it from the console to answer the one question that
/// decides whether the whole playermodel idea is viable - does citizen's bind pose survive the
/// round trip.
/// </summary>
public static class EffigySkeletonImport
{
	/// <summary>What an import produced, and everything questionable about it. The notes are the
	/// point as much as the skeleton is: an import that quietly drops a bone is worse than one
	/// that refuses, because the model still builds and only the animation looks wrong.</summary>
	public sealed class Result
	{
		public Skeleton Skeleton = new();

		/// <summary>Effigy bone index, by the name the model used.</summary>
		public Dictionary<string, int> ByName = new();

		/// <summary>Things worth telling the user - non-unit scale, bones with no children to take
		/// a length from, several roots. None of these fail the import.</summary>
		public List<string> Notes = new();

		public int Roots;
	}

	/// <summary>
	/// Import every bone of <paramref name="model"/>.
	///
	/// <paramref name="defaultLength"/> is what a bone with no children gets, since a tail has to
	/// come from somewhere and a leaf has nothing to point at. Inches, like everything else in the
	/// engine's units.
	/// </summary>
	public static Result FromModel( Model model, float defaultLength = 1f )
	{
		ArgumentNullException.ThrowIfNull( model );

		var result = new Result();
		var bones = model.Bones?.AllBones?.ToList() ?? new List<BoneCollection.Bone>();

		if ( bones.Count == 0 )
		{
			result.Notes.Add( "The model has no bones - it is a static mesh, not a rig." );
			return result;
		}

		// Roots first, then each root's subtree, so a parent is always added before its children.
		foreach ( var root in bones.Where( b => b.Parent is null ) )
		{
			result.Roots++;
			AddSubtree( result, root, -1, defaultLength );
		}

		if ( result.Skeleton.Count != bones.Count )
		{
			result.Notes.Add( $"{bones.Count - result.Skeleton.Count} of {bones.Count} bones were "
				+ "not reachable from any root - the model's bone tree is not a tree." );
		}

		if ( result.Roots > 1 )
		{
			result.Notes.Add( $"{result.Roots} root bones. Retargeting tools assume one; see "
				+ "RigDiagnostics for the same complaint about rigs built in the tool." );
		}

		SetLengths( result, defaultLength );

		return result;
	}

	static void AddSubtree( Result result, BoneCollection.Bone bone, int parent, float defaultLength )
	{
		var name = bone.Name;

		if ( string.IsNullOrWhiteSpace( name ) )
		{
			result.Notes.Add( $"A bone under index {parent} has no name and was skipped - every "
				+ "format Effigy writes keys on the name." );
			return;
		}

		if ( result.ByName.ContainsKey( name ) )
		{
			result.Notes.Add( $"Two bones are both called '{name}'; the second was skipped." );
			return;
		}

		var modelSpace = bone.LocalTransform;

		if ( !modelSpace.Scale.AlmostEqual( 1f ) )
		{
			result.Notes.Add( $"Bone '{name}' has scale {modelSpace.Scale} in its bind pose. It is "
				+ "carried through in the basis, but scaled bind poses skin badly." );
		}

		// Rebase into the parent's frame - see `LocalTransform` IS NOT LOCAL above. The parent is
		// always already added, because this walks the tree from the roots outward.
		var world = ToXform( modelSpace );
		var local = parent < 0 ? world : result.Skeleton.WorldBind( parent ).Inverse * world;

		var index = result.Skeleton.AddBone( name, parent, local, defaultLength );
		result.ByName[name] = index;

		foreach ( var child in bone.Children )
			AddSubtree( result, child, index, defaultLength );
	}

	/// <summary>
	/// A bone's tail, once the whole hierarchy exists.
	///
	/// A bone's length is the distance to its first child's head, which is the convention every
	/// tool that draws bones uses and what auto-weighting wants - a bone is a segment to measure
	/// distance to, not a point. Leaves have nothing to measure to and keep the default. This runs
	/// as a second pass because a child's local position is only meaningful after it is added.
	/// </summary>
	static void SetLengths( Result result, float defaultLength )
	{
		var firstChild = new int[result.Skeleton.Count];
		Array.Fill( firstChild, -1 );

		for ( var i = result.Skeleton.Count - 1; i >= 0; i-- )
		{
			var parent = result.Skeleton.Bones[i].Parent;

			if ( parent >= 0 )
				firstChild[parent] = i;
		}

		var leaves = 0;

		for ( var i = 0; i < result.Skeleton.Count; i++ )
		{
			if ( firstChild[i] < 0 )
			{
				leaves++;
				continue;
			}

			var reach = result.Skeleton.Bones[firstChild[i]].Local.Origin.Length;

			result.Skeleton.Bones[i].Length = reach > 1e-4f ? reach : defaultLength;
		}

		if ( leaves > 0 )
			result.Notes.Add( $"{leaves} leaf bones kept the default length of {defaultLength}." );
	}

	/// <summary>
	/// An engine Transform as Effigy's basis-and-origin form. The rotation's three axes become the
	/// three basis vectors, scaled - which is exactly what TransformPoint then reproduces, so a
	/// uniformly or non-uniformly scaled bind pose survives rather than being quietly dropped.
	/// </summary>
	public static Xform ToXform( Transform t )
	{
		var scale = t.Scale;

		return new Xform(
			ToVec( t.Rotation.Forward * scale.x ),
			ToVec( t.Rotation.Left * scale.y ),
			ToVec( t.Rotation.Up * scale.z ),
			ToVec( t.Position ) );
	}

	public static Vec3 ToVec( Vector3 v ) => new( v.x, v.y, v.z );

	public static Vector3 ToVector3( Vec3 v ) => new( v.x, v.y, v.z );
}
