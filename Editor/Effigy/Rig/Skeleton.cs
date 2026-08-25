using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// One bone: a name, a parent, and where it sits in the bind pose.
///
/// Local is relative to the parent, which is what every skeletal format stores and what makes a
/// chain behave when a parent moves. World bind transforms are derived by walking up the parents
/// rather than stored, so there is exactly one source of truth and no chance of the two drifting
/// apart after an edit.
///
/// Length exists because a bone needs a tail as well as a head. The head is the transform's
/// origin; the tail is Length along the bone's own +Y. That is Blender's convention, and it is
/// what auto-weighting needs — a bone is a SEGMENT to measure distance to, not a point.
/// </summary>
public sealed class Bone
{
	public string Name;

	/// <summary>Index into Skeleton.Bones, or -1 for a root. Always less than this bone's own
	/// index — see Skeleton.AddBone.</summary>
	public int Parent;

	/// <summary>Bind pose relative to the parent.</summary>
	public Xform Local;

	public float Length;

	public Bone( string name, int parent, Xform local, float length )
	{
		Name = name;
		Parent = parent;
		Local = local;
		Length = length;
	}

	public Bone Clone() => new( Name, Parent, Local, Length );
}

/// <summary>
/// A bone hierarchy with a bind pose. Engine-free, like everything else in here — the s&box and
/// Godot sides convert at the boundary.
///
/// BONES ARE STORED IN TOPOLOGICAL ORDER: a bone's parent always has a lower index. AddBone
/// enforces it by refusing a parent that does not exist yet, which makes cycles unrepresentable
/// rather than merely invalid, and means WorldBind can never loop forever. It also happens to be
/// what SMD's node block wants, but that is a consequence, not the reason.
/// </summary>
public sealed class Skeleton
{
	public List<Bone> Bones = new();

	public int Count => Bones.Count;

	/// <summary>
	/// Add a bone under an existing parent (-1 for a root). Returns its index.
	///
	/// Throws if the parent does not already exist. That is the constraint that keeps the list
	/// topologically ordered and the hierarchy acyclic.
	/// </summary>
	public int AddBone( string name, int parent, Xform local, float length = 1f )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "A bone needs a name — every consuming format keys on it" );

		if ( parent < -1 || parent >= Bones.Count )
			throw new ArgumentOutOfRangeException( nameof( parent ),
				$"Parent {parent} does not exist yet. Add parents before children." );

		if ( IndexOf( name ) >= 0 )
			throw new ArgumentException( $"A bone called '{name}' already exists" );

		Bones.Add( new Bone( name, parent, local, length ) );
		return Bones.Count - 1;
	}

	/// <summary>
	/// Add a bone from a head and tail point in WORLD bind space, which is what drawing a bone
	/// chain in a viewport produces.
	///
	/// The bone's +Y is aimed head→tail; the other two axes are any stable perpendicular pair,
	/// since nothing here has a concept of roll yet. If roll ever matters — it will, the moment
	/// anyone hand-authors a twist — this is the function that grows a parameter, and nothing else
	/// has to change.
	/// </summary>
	public int AddBoneFromPoints( string name, int parent, Vec3 head, Vec3 tail )
	{
		var along = tail - head;
		var length = along.Length;

		if ( length < 1e-6f )
			throw new ArgumentException( $"Bone '{name}' has zero length — head and tail are the same point" );

		var y = along / length;

		// Any axis not parallel to y works as a seed; picking the one y leans on least keeps the
		// cross product well-conditioned.
		var seed = MathF.Abs( y.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 0, 1 );
		var x = Vec3.Cross( seed, y ).Normal;
		var z = Vec3.Cross( x, y );

		var world = new Xform( x, y, z, head );
		var local = parent < 0 ? world : WorldBind( parent ).Inverse * world;

		return AddBone( name, parent, local, length );
	}

	public int IndexOf( string name )
	{
		for ( var i = 0; i < Bones.Count; i++ )
		{
			if ( Bones[i].Name == name )
				return i;
		}

		return -1;
	}

	/// <summary>Bind transform in world space, from walking up the parents. Cheap enough to call
	/// per bone; the chains here are tens of bones, not thousands.</summary>
	public Xform WorldBind( int index )
	{
		var x = Bones[index].Local;
		var p = Bones[index].Parent;

		while ( p >= 0 )
		{
			x = Bones[p].Local * x;
			p = Bones[p].Parent;
		}

		return x;
	}

	public Vec3 HeadWorld( int index ) => WorldBind( index ).Origin;

	/// <summary>Tail is Length along the bone's own +Y — see Bone.</summary>
	public Vec3 TailWorld( int index )
	{
		var w = WorldBind( index );
		return w.TransformPoint( new Vec3( 0, Bones[index].Length, 0 ) );
	}

	public IEnumerable<int> Children( int index )
	{
		for ( var i = index + 1; i < Bones.Count; i++ )
		{
			if ( Bones[i].Parent == index )
				yield return i;
		}
	}

	public Skeleton Clone()
	{
		var s = new Skeleton();

		foreach ( var b in Bones )
			s.Bones.Add( b.Clone() );

		return s;
	}

	/// <summary>
	/// Remove a bone, reparenting its direct children to ITS parent so deleting one from the
	/// middle of a chain does not orphan everything past it — a mis-click while placing bones is
	/// the common case this exists for.
	///
	/// Every surviving bone keeps its WORLD bind transform; only the stored Local of the removed
	/// bone's children changes, recomputed against their new parent. Topological order (parent
	/// index &lt; child index) is preserved because indices only ever shift down to fill the gap.
	/// </summary>
	public void RemoveBone( int index )
	{
		if ( index < 0 || index >= Bones.Count )
			throw new ArgumentOutOfRangeException( nameof( index ) );

		var removedParent = Bones[index].Parent;

		// Captured before anything is rebuilt, so reparenting reads world transforms rather than
		// composing through a partially-rebuilt list.
		var worlds = new Xform[Bones.Count];
		for ( var i = 0; i < Bones.Count; i++ )
			worlds[i] = WorldBind( i );

		var newBones = new List<Bone>( Bones.Count - 1 );
		var oldToNew = new int[Bones.Count];

		for ( var i = 0; i < Bones.Count; i++ )
		{
			if ( i == index )
				continue;

			var bone = Bones[i];
			var oldParent = bone.Parent;

			// A direct child of the removed bone re-parents to what the removed bone's parent
			// was; everything else keeps its parent unchanged.
			var newParent = oldParent == index ? removedParent : oldParent;

			// newParent, when not -1, is always an index already visited — it is either less
			// than `index`, or it is `removedParent` which is itself less than `index` — so its
			// mapping exists by now.
			var mappedParent = newParent < 0 ? -1 : oldToNew[newParent];

			var local = mappedParent < 0 ? worlds[i] : worlds[mappedParent].Inverse * worlds[i];

			newBones.Add( new Bone( bone.Name, mappedParent, local, bone.Length ) );
			oldToNew[i] = newBones.Count - 1;
		}

		Bones = newBones;
	}

	/// <summary>Rename a bone in place, with the same validation AddBone applies to a new one.</summary>
	public void RenameBone( int index, string name )
	{
		if ( index < 0 || index >= Bones.Count )
			throw new ArgumentOutOfRangeException( nameof( index ) );

		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "A bone needs a name — every consuming format keys on it" );

		var existing = IndexOf( name );

		if ( existing >= 0 && existing != index )
			throw new ArgumentException( $"A bone called '{name}' already exists" );

		Bones[index].Name = name;
	}

	/// <summary>
	/// A single root bone at the origin. Every static model exported as a skinned format needs
	/// one — a mesh with no bones at all is not something SMD can express, so "static" is really
	/// "everything weighted to one root".
	/// </summary>
	public static Skeleton SingleRoot( string name = "root" )
	{
		var s = new Skeleton();
		s.AddBone( name, -1, Xform.Identity );
		return s;
	}

	public override string ToString() => $"Skeleton, {Bones.Count} bones";
}
