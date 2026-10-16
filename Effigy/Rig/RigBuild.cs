using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// The rig edits a build script makes: place one bone, lay a chain along a path, split a bone,
/// add twist bones, mirror one side. The editor's Rig panel places bones by clicking; these are
/// the same skeleton edits with numbers instead of clicks, so an agent or a script can rig a
/// creature without the viewport.
///
/// EVERY BONE IS BUILT FROM A HEAD, A TAIL AND AN UP HINT, through
/// <see cref="Skeleton.AddBoneFromPoints"/>, so a script bone and a clicked bone are the same
/// thing: +Y down the bone, +Z leaning toward the hint. The default hint is world up (+Z), or
/// forward (+X) for a bone that is itself vertical, which is what "roll 0" means here; roll turns
/// the hint about the bone. A chain carries its hint down the path by parallel transport, so a
/// curling tentacle does not flip its roll halfway along.
///
/// Edits that have to put new bones BETWEEN existing ones (subdivide) rebuild the list from
/// world-space records, because a parent's index must stay below its children's. Every surviving
/// bone keeps its world bind pose, its roll and its softness.
/// </summary>
public static class RigBuild
{
	/// <summary>The up hint for "roll 0": world +Z, or +X for a bone that points along Z.</summary>
	public static Vec3 DefaultUp( Vec3 along )
	{
		var len = along.Length;

		if ( len < 1e-6f )
			return new Vec3( 0, 0, 1 );

		return MathF.Abs( along.z / len ) > 0.99f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 0, 1 );
	}

	/// <summary>The up hint for a bone from <paramref name="head"/> to <paramref name="tail"/>:
	/// <paramref name="up"/> (or the default) turned by <paramref name="rollDegrees"/> about the bone,
	/// right-handed — positive roll turns +Z toward -X when the bone points along +Y.</summary>
	public static Vec3 UpHint( Vec3 head, Vec3 tail, float rollDegrees = 0f, Vec3? up = null )
	{
		var along = tail - head;
		var hint = up ?? DefaultUp( along );

		if ( MathF.Abs( rollDegrees ) < 1e-6f || along.Length < 1e-6f )
			return hint;

		return Rotate( hint, along.Normal, rollDegrees * MathF.PI / 180f );
	}

	/// <summary>Rodrigues: <paramref name="v"/> turned by <paramref name="radians"/> about the unit
	/// <paramref name="axis"/>.</summary>
	public static Vec3 Rotate( Vec3 v, Vec3 axis, float radians )
	{
		var c = MathF.Cos( radians );
		var s = MathF.Sin( radians );
		return v * c + Vec3.Cross( axis, v ) * s + axis * (Vec3.Dot( axis, v ) * (1f - c));
	}

	/// <summary>
	/// Add <paramref name="name"/>, or move it if it is already there — so a script can be re-run
	/// with a corrected number and the bone follows instead of the line failing. A changed parent
	/// re-parents first (keeping the world pose of everything else), then head and tail are set.
	/// Returns the bone's index; <paramref name="moved"/> says it already existed.
	/// </summary>
	public static int Bone( Skeleton skeleton, string name, int parent, Vec3 head, Vec3 tail, Vec3 up, out bool moved )
	{
		ArgumentNullException.ThrowIfNull( skeleton );

		var index = skeleton.IndexOf( name );
		moved = index >= 0;

		if ( !moved )
			return skeleton.AddBoneFromPoints( name, parent, head, tail, up );

		var parentName = parent >= 0 ? skeleton.Bones[parent].Name : null;

		if ( skeleton.Bones[index].Parent != parent )
			index = skeleton.SetParent( index, parentName is null ? -1 : skeleton.IndexOf( parentName ) );

		skeleton.SetHeadTail( index, head, tail, up );
		return index;
	}

	/// <summary>
	/// <paramref name="count"/> bones laid end to end along <paramref name="path"/>, evenly spaced in
	/// ARC LENGTH (not by control point), named <c>name_00</c>, <c>name_01</c>… The first hangs off
	/// <paramref name="parent"/>, each of the rest off the one before. Even arc length is what a
	/// tentacle or a tail needs: bones bunched where the path happened to have more points bend
	/// unevenly, and every spline IK and secondary-motion solver assumes even spacing.
	/// Returns the new bones' indices, root first.
	/// </summary>
	public static List<int> Chain( Skeleton skeleton, string name, int parent, IReadOnlyList<Vec3> path, int count, Vec3? up = null )
	{
		ArgumentNullException.ThrowIfNull( skeleton );

		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "A chain needs a name; its bones are called name_00, name_01…" );

		if ( path is null || path.Count < 2 )
			throw new ArgumentException( $"Chain '{name}' needs a path of at least two points." );

		if ( count < 1 || count > 256 )
			throw new ArgumentOutOfRangeException( nameof( count ), $"Chain '{name}': count={count}; use 1 to 256 bones." );

		var names = Enumerable.Range( 0, count ).Select( k => ChainBoneName( name, k, count ) ).ToList();
		var clash = names.FirstOrDefault( n => skeleton.IndexOf( n ) >= 0 );

		if ( clash is not null )
			throw new ArgumentException( $"Chain '{name}': a bone called '{clash}' already exists. Give the chain another name, or remove the old one." );

		var joints = Resample( path, count + 1 );

		if ( joints is null )
			throw new ArgumentException( $"Chain '{name}': the path has no length - its points are all the same." );

		var result = new List<int>( count );
		var hint = up ?? DefaultUp( joints[1] - joints[0] );
		var previous = (joints[1] - joints[0]).Normal;

		for ( var k = 0; k < count; k++ )
		{
			var along = (joints[k + 1] - joints[k]).Normal;

			// Parallel transport: turn the hint by the rotation that takes the previous bone's
			// direction onto this one's, so roll follows the curve instead of snapping to world up.
			hint = Transport( hint, previous, along );
			previous = along;

			parent = skeleton.AddBoneFromPoints( names[k], parent, joints[k], joints[k + 1], hint );
			result.Add( parent );
		}

		return result;
	}

	/// <summary><c>name_00</c>…, two digits until a chain is over a hundred long.</summary>
	public static string ChainBoneName( string name, int k, int count ) =>
		$"{name}_{k.ToString( count > 100 ? "000" : "00" )}";

	/// <summary>
	/// <paramref name="joints"/> points along the polyline at equal arc-length steps, first and last
	/// on its ends. Null when the polyline has no length.
	/// </summary>
	public static List<Vec3> Resample( IReadOnlyList<Vec3> path, int joints )
	{
		var cumulative = new float[path.Count];

		for ( var i = 1; i < path.Count; i++ )
			cumulative[i] = cumulative[i - 1] + (path[i] - path[i - 1]).Length;

		var total = cumulative[^1];

		if ( total < 1e-6f || joints < 2 )
			return null;

		var result = new List<Vec3>( joints );
		var segment = 1;

		for ( var j = 0; j < joints; j++ )
		{
			var s = total * j / (joints - 1);

			while ( segment < path.Count - 1 && cumulative[segment] < s )
				segment++;

			var span = cumulative[segment] - cumulative[segment - 1];
			var t = span < 1e-9f ? 0f : (s - cumulative[segment - 1]) / span;
			result.Add( Vec3.Lerp( path[segment - 1], path[segment], Math.Clamp( t, 0f, 1f ) ) );
		}

		result[0] = path[0];
		result[^1] = path[^1];
		return result;
	}

	static Vec3 Transport( Vec3 hint, Vec3 from, Vec3 to )
	{
		var axis = Vec3.Cross( from, to );
		var sin = axis.Length;
		var cos = Vec3.Dot( from, to );

		if ( sin < 1e-6f )
			return hint;

		return Rotate( hint, axis / sin, MathF.Atan2( sin, cos ) );
	}

	/// <summary>
	/// Split one bone into <paramref name="pieces"/> equal bones end to end. The first keeps the
	/// name (and everything that refers to it — body bindings, constraints, clips); the rest are
	/// <c>name_1</c>, <c>name_2</c>… Children of the original move to the last piece, which is
	/// where the original's tail was. Returns the pieces' indices, first first.
	/// </summary>
	public static List<int> Subdivide( Skeleton skeleton, int bone, int pieces )
	{
		ArgumentNullException.ThrowIfNull( skeleton );

		if ( bone < 0 || bone >= skeleton.Count )
			throw new ArgumentOutOfRangeException( nameof( bone ) );

		if ( pieces < 2 || pieces > 64 )
			throw new ArgumentOutOfRangeException( nameof( pieces ), $"Subdivide: {pieces} pieces; use 2 to 64." );

		var name = skeleton.Bones[bone].Name;
		var head = skeleton.HeadWorld( bone );
		var tail = skeleton.TailWorld( bone );
		var up = skeleton.WorldBind( bone ).Z;
		var newNames = new List<string> { name };

		for ( var k = 1; k < pieces; k++ )
		{
			var candidate = $"{name}_{k}";

			if ( skeleton.IndexOf( candidate ) >= 0 || newNames.Contains( candidate ) )
				throw new ArgumentException( $"Subdivide '{name}': a bone called '{candidate}' already exists. Rename it first." );

			newNames.Add( candidate );
		}

		var records = Records( skeleton );
		var rebuilt = new List<Record>( records.Count + pieces - 1 );

		foreach ( var record in records )
		{
			if ( record.Name != name )
			{
				// The original's children hang from the last piece now.
				rebuilt.Add( record.Parent == name ? record with { Parent = newNames[^1] } : record );
				continue;
			}

			for ( var k = 0; k < pieces; k++ )
			{
				rebuilt.Add( new Record(
					newNames[k],
					k == 0 ? record.Parent : newNames[k - 1],
					Vec3.Lerp( head, tail, (float)k / pieces ),
					Vec3.Lerp( head, tail, (float)(k + 1) / pieces ),
					up,
					k == 0 ? record.Soft : record.Soft?.Clone() ) );
			}
		}

		Rebuild( skeleton, rebuilt );
		return newNames.Select( skeleton.IndexOf ).ToList();
	}

	/// <summary>
	/// Twist bones on <paramref name="bone"/>: <c>bone_twist0</c> at its head and the rest at even
	/// stations down it, each a child of the bone. That is citizen's convention (and what
	/// <see cref="TwistWeights.Spread"/> reads), so a forearm that rolls at the wrist unwinds along
	/// its length instead of pinching one ring of vertices. Returns the new bones' indices.
	/// </summary>
	public static List<int> AddTwist( Skeleton skeleton, int bone, int count = 2 )
	{
		ArgumentNullException.ThrowIfNull( skeleton );

		if ( bone < 0 || bone >= skeleton.Count )
			throw new ArgumentOutOfRangeException( nameof( bone ) );

		if ( count < 1 || count > 8 )
			throw new ArgumentOutOfRangeException( nameof( count ), $"Twist: {count} bones; use 1 to 8." );

		var name = skeleton.Bones[bone].Name;

		if ( IsTwistBone( name ) )
			throw new ArgumentException( $"'{name}' is itself a twist bone. Add twist bones to the limb bone it belongs to." );

		if ( !skeleton.TryBodySpan( bone, out var head, out var tail ) )
		{
			head = skeleton.HeadWorld( bone );
			tail = skeleton.TailWorld( bone );
		}

		var up = skeleton.WorldBind( bone ).Z;
		var result = new List<int>( count );

		for ( var k = 0; k < count; k++ )
		{
			var twist = $"{name}_twist{k}";

			if ( skeleton.IndexOf( twist ) >= 0 )
				throw new ArgumentException( $"'{name}' already has '{twist}'. Twist bones go on once." );
		}

		for ( var k = 0; k < count; k++ )
		{
			var a = Vec3.Lerp( head, tail, (float)k / count );
			var b = Vec3.Lerp( head, tail, (float)(k + 1) / count );
			result.Add( skeleton.AddBoneFromPoints( $"{name}_twist{k}", bone, a, b, up ) );
		}

		return result;
	}

	/// <summary>Citizen's twist naming: <c>parent_twistN</c>.</summary>
	public static bool IsTwistBone( string name )
	{
		var cut = name?.LastIndexOf( "_twist", StringComparison.OrdinalIgnoreCase ) ?? -1;
		return cut > 0 && int.TryParse( name[(cut + 6)..], out _ );
	}

	/// <summary>
	/// Mirror every bone on <paramref name="side"/> ('L' or 'R', from the name's _L/_R suffix) that
	/// has no partner yet across the centre plane y = 0 (left is +y in this project), naming the
	/// copy with the other suffix and hanging it from the mirror of its parent — or the same parent
	/// when that is a centre bone. Roll is mirrored too: the copy's up is the reflection of the
	/// original's. Bones whose partner already exists are left alone and listed in
	/// <paramref name="skipped"/>. Returns the new bones' indices.
	/// </summary>
	public static List<int> MirrorSide( Skeleton skeleton, char side, out List<string> skipped )
	{
		ArgumentNullException.ThrowIfNull( skeleton );

		side = char.ToUpperInvariant( side );

		if ( side is not ('L' or 'R') )
			throw new ArgumentException( $"Mirror side '{side}': use L or R." );

		skipped = new List<string>();
		var added = new List<int>();
		var normal = new Vec3( 0, 1, 0 );
		Vec3 Reflect( Vec3 p ) => p - normal * (2f * Vec3.Dot( p, normal ));

		// Snapshot: the loop adds bones, and only the ones that were there to begin with are sources.
		var sources = Enumerable.Range( 0, skeleton.Count ).Where( i => SideOf( skeleton.Bones[i].Name ) == side ).ToList();

		foreach ( var i in sources )
		{
			var name = skeleton.Bones[i].Name;
			var partner = Skeleton.FlipSideSuffix( name );

			if ( skeleton.IndexOf( partner ) >= 0 )
			{
				skipped.Add( name );
				continue;
			}

			var parent = skeleton.Bones[i].Parent;
			var mirroredParent = -1;

			if ( parent >= 0 )
			{
				var parentName = skeleton.Bones[parent].Name;
				var parentPartner = SideOf( parentName ) == side ? Skeleton.FlipSideSuffix( parentName ) : parentName;
				mirroredParent = skeleton.IndexOf( parentPartner );

				// A sided parent whose mirror is missing (its partner was skipped because it exists
				// under a different name, say) - hang the copy where the original hangs.
				if ( mirroredParent < 0 )
					mirroredParent = parent;
			}

			var world = skeleton.WorldBind( i );
			var copy = skeleton.AddBoneFromPoints( partner, mirroredParent,
				Reflect( world.Origin ), Reflect( skeleton.TailWorld( i ) ), Reflect( world.Z ) );

			skeleton.Bones[copy].Soft = skeleton.Bones[i].Soft?.Clone();
			added.Add( copy );
		}

		return added;
	}

	/// <summary>'L', 'R' or '\0' from a _L/_R suffix (digits may follow the letter).</summary>
	public static char SideOf( string name )
	{
		if ( string.IsNullOrEmpty( name ) || Skeleton.FlipSideSuffix( name ) is null )
			return '\0';

		for ( var i = name.Length - 1; i >= 0; i-- )
		{
			if ( !char.IsDigit( name[i] ) )
				return char.ToUpperInvariant( name[i] );
		}

		return '\0';
	}

	/// <summary>One bone as world-space points, for rebuilding a list in a new order.</summary>
	public sealed record Record( string Name, string Parent, Vec3 Head, Vec3 Tail, Vec3 Up, SoftBone Soft );

	/// <summary>Every bone as a world-space record, in list order.</summary>
	public static List<Record> Records( Skeleton skeleton )
	{
		var list = new List<Record>( skeleton.Count );

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			var bone = skeleton.Bones[i];
			var world = skeleton.WorldBind( i );
			list.Add( new Record( bone.Name, bone.Parent >= 0 ? skeleton.Bones[bone.Parent].Name : null,
				world.Origin, skeleton.TailWorld( i ), world.Z, bone.Soft ) );
		}

		return list;
	}

	/// <summary>Replace <paramref name="skeleton"/>'s bones with <paramref name="records"/>, which
	/// must list every parent before its children. In place, so anything holding the skeleton (the
	/// studio, the rig panel) sees the result.</summary>
	public static void Rebuild( Skeleton skeleton, IReadOnlyList<Record> records )
	{
		skeleton.Bones.Clear();

		foreach ( var r in records )
		{
			var parent = r.Parent is null ? -1 : skeleton.IndexOf( r.Parent );

			if ( r.Parent is not null && parent < 0 )
				throw new InvalidOperationException( $"Rebuilding the rig: '{r.Name}' names parent '{r.Parent}', which comes after it." );

			var index = skeleton.AddBoneFromPoints( r.Name, parent, r.Head, r.Tail, r.Up );
			skeleton.Bones[index].Soft = r.Soft;
		}
	}

	/// <summary>Append every bone of <paramref name="source"/> to <paramref name="target"/>, keeping
	/// their world pose; source roots hang from <paramref name="parent"/> (-1 for roots). Names must
	/// not collide. Returns the index of each source bone in the target.</summary>
	public static int[] Append( Skeleton target, Skeleton source, int parent = -1 )
	{
		var map = new int[source.Count];

		for ( var i = 0; i < source.Count; i++ )
		{
			var bone = source.Bones[i];

			if ( target.IndexOf( bone.Name ) >= 0 )
				throw new ArgumentException( $"A bone called '{bone.Name}' is already in the rig." );

			var p = bone.Parent >= 0 ? map[bone.Parent] : parent;
			var local = p < 0 ? source.WorldBind( i ) : target.WorldBind( p ).Inverse * source.WorldBind( i );
			map[i] = target.AddBone( bone.Name, p, local, bone.Length );
			target.Bones[map[i]].Soft = bone.Soft?.Clone();
		}

		return map;
	}
}
