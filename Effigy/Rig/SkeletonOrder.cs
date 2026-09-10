using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Put a skeleton into the order an exporter's hierarchy walks it.
///
/// WHY THIS EXISTS. A DMX says what a bone is three times: the dag hierarchy, written by recursing
/// from each root; `jointList`, which is what a vertex's blend indices point into; and
/// `baseStates`, the bind pose the skin is measured against. <see cref="DmxWriter"/> writes the
/// last two in skeleton order and the first depth-first, and those are only the same order if the
/// skeleton HAPPENS to be stored depth-first.
///
/// When they disagree, nothing complains. The model compiles, the bone names, parents and
/// positions all come back correct, and it stands in its bind pose looking perfect - because at
/// bind, every bone's skinning transform is the identity no matter which bind matrix you paired it
/// with. The moment anything animates, each bone is being measured against another bone's bind
/// pose, and the mesh collapses onto its own skeleton. It reads as catastrophically broken weights
/// on a model whose weights are fine.
///
/// So rather than trusting a coincidence, the skeleton is rewritten depth-first and the weights
/// come with it. Then all three orderings are the same one and the question cannot be got wrong.
/// </summary>
public static class SkeletonOrder
{
	/// <summary>
	/// A copy of <paramref name="skeleton"/> ordered depth-first from its roots, and the map from
	/// old bone index to new. Roots keep their relative order, as do siblings, so this is a
	/// reshuffle rather than a re-rig - every bone keeps its parent, its local transform and its
	/// name.
	/// </summary>
	public static (Skeleton Ordered, int[] OldToNew) DepthFirst( Skeleton skeleton )
	{
		ArgumentNullException.ThrowIfNull( skeleton );

		var children = new List<int>[skeleton.Count];

		for ( var b = 0; b < skeleton.Count; b++ )
			children[b] = new List<int>();

		var roots = new List<int>();

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			if ( skeleton.Bones[b].Parent < 0 )
				roots.Add( b );
			else
				children[skeleton.Bones[b].Parent].Add( b );
		}

		var order = new List<int>( skeleton.Count );

		void Visit( int b )
		{
			order.Add( b );

			foreach ( var c in children[b] )
				Visit( c );
		}

		foreach ( var r in roots )
			Visit( r );

		if ( order.Count != skeleton.Count )
			throw new InvalidOperationException(
				$"Only {order.Count} of {skeleton.Count} bones are reachable from a root - the hierarchy has a cycle" );

		var oldToNew = new int[skeleton.Count];

		for ( var i = 0; i < order.Count; i++ )
			oldToNew[order[i]] = i;

		var ordered = new Skeleton();

		foreach ( var b in order )
		{
			var bone = skeleton.Bones[b];
			ordered.AddBone( bone.Name, bone.Parent < 0 ? -1 : oldToNew[bone.Parent], bone.Local, bone.Length );
		}

		return (ordered, oldToNew);
	}

	/// <summary>Rewrite a mesh's blend indices through <paramref name="oldToNew"/>, in place.</summary>
	public static void Remap( PolyMesh mesh, int[] oldToNew )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( oldToNew );

		if ( !mesh.IsRigged )
			return;

		for ( var v = 0; v < mesh.Skin.Count; v++ )
		{
			mesh.Skin[v] = mesh.Skin[v]
				.Select( w => new BoneWeight(
					w.Bone >= 0 && w.Bone < oldToNew.Length ? oldToNew[w.Bone] : w.Bone, w.Weight ) )
				.ToArray();
		}
	}
}
