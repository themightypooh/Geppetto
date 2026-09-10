using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Move a skinned mesh from one skeleton onto another.
///
/// WHAT THIS IS FOR. A playermodel is a mesh bound to CITIZEN'S skeleton, in citizen's bind pose,
/// because that is what citizen's animations were authored against and they bind by bone name. A
/// model rigged in Effigy - or imported from anywhere else - has its own bones with its own names
/// in its own pose. Renaming the bones is the obvious move and it is wrong: the animation supplies
/// a rotation relative to the bind pose it was authored against, so feeding it a different bind
/// pose bends every joint by the difference. Citizen stands in an A-pose with its arms ~40 degrees
/// below horizontal; hand a T-posed rig citizen's walk cycle and the arms go through the floor.
///
/// TWO WAYS TO MAKE THE TWO AGREE, and they are not interchangeable. <see cref="To"/> moves the
/// MESH onto the target's skeleton, so the model takes on the target's proportions.
/// <see cref="Fit"/> moves the target's SKELETON into the mesh, so the model keeps its own. Which
/// one a model wants depends on whether it is meant to look like itself.
///
/// For <see cref="To"/>: for each source bone this builds the transform that takes its bind pose
/// to the target bone's bind pose, and applies that to the vertices it influences, blended by the
/// same weights that will skin it afterwards. The result is the same mesh standing in the target's
/// pose, bound to the target's bones - which is exactly the situation the target's animations
/// expect. It is the operation a DCC tool calls "bind to a new skeleton", written out.
///
/// LINEAR BLEND, AND ITS ONE KNOWN FAILURE. A vertex influenced by several bones is transformed by
/// each and averaged. That is the same maths skinning itself uses, so the result is consistent
/// with how the model will actually deform - and it inherits the same candy-wrapper pinching where
/// two bones differ by a large twist. The joints that move most here are shoulders and elbows,
/// which is where it will show first if it shows at all.
///
/// TWO KINDS OF CORRESPONDENCE, AND CONFLATING THEM TEARS THE MESH. Mapping `upperarm_L` onto
/// `arm_upper_L` means "these are the same bone": the source bone's bind position SNAPS onto the
/// target's, which is how the model takes on the target's proportions, and is the whole point.
/// Mapping a brow bone onto `head` means something else entirely - citizen has no brow, and the
/// geometry should simply ride along with the head where it already is. Snapping it would drag the
/// brow into the middle of the skull. Same for a fifth finger onto a four-fingered hand.
///
/// So a source bone can be named as RIDE-ALONG: its weight is rebound to the target bone, but its
/// geometry moves by the nearest true correspondence above it instead of by its own. The cost of
/// getting this wrong is not subtle - it was long spikes radiating out of the Gearhead's face and
/// fingers stretched onto the wrong knuckles.
///
/// THE MAP IS THE CALLER'S PROBLEM, deliberately. Guessing that `upperarm_L` means `arm_upper_L`
/// is a heuristic that will be wrong on somebody's rig, silently, in a way that looks like bad
/// weights rather than a bad guess. So the correspondence is supplied and this reports exactly
/// what it could not place.
/// </summary>
public static class SkeletonRetarget
{
	/// <summary>What a retarget produced, and everything that did not survive it cleanly.</summary>
	public sealed class Result
	{
		/// <summary>The mesh, skinned to the target's bones - moved into the target's bind pose by
		/// <see cref="To"/>, left exactly where it was by <see cref="Fit"/>.</summary>
		public PolyMesh Mesh;

		/// <summary>The skeleton <see cref="Mesh"/> is bound to: the target itself after
		/// <see cref="To"/>, the target fitted into the mesh after <see cref="Fit"/>. Export this,
		/// not the target you passed in.</summary>
		public Skeleton Skeleton;

		/// <summary>Source bone names with no entry in the map, or an entry naming a bone the
		/// target does not have. Their weight had nowhere to go.</summary>
		public List<string> Unmapped = new();

		/// <summary>Vertices that lost some weight because of <see cref="Unmapped"/>, and were
		/// renormalised over whatever was left.</summary>
		public int VerticesRenormalised;

		/// <summary>Vertices with no mapped influence at all. These could not be moved and were
		/// left where they were - the one failure that shows up as geometry in the wrong
		/// place.</summary>
		public int VerticesStranded;

		/// <summary>Target bones nothing was weighted onto. Expected rather than alarming: citizen
		/// carries twists, helpers and IK targets that the anim graph drives and no mesh is bound
		/// to.</summary>
		public int TargetBonesUnused;
	}

	/// <summary>
	/// Retarget <paramref name="mesh"/> from <paramref name="from"/> onto <paramref name="to"/>.
	///
	/// <paramref name="map"/> is keyed by SOURCE bone name and names a target bone. Several source
	/// bones may name the same target - a five-fingered hand onto citizen's four is exactly that -
	/// and their weights are summed on the way through.
	///
	/// <paramref name="rideAlong"/> names source bones that have no true counterpart and should
	/// keep their place rather than snapping onto the bone they are bound to - see the note above.
	///
	/// <paramref name="adoptRoll"/> takes the target bone's frame whole instead of keeping the
	/// source's roll - see <see cref="Land"/> for why that is not the default. Kept as a switch
	/// because the two only differ once something animates, and the only way to know which a given
	/// pair of skeletons wants is to bake both and look.
	///
	/// The mesh is not modified; the result carries a clone.
	/// </summary>
	public static Result To( PolyMesh mesh, Skeleton from, Skeleton to, IReadOnlyDictionary<string, string> map,
		IReadOnlyCollection<string> rideAlong = null, bool adoptRoll = false )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( from );
		ArgumentNullException.ThrowIfNull( to );
		ArgumentNullException.ThrowIfNull( map );

		if ( !mesh.IsRigged )
			throw new ArgumentException( "The mesh has no skin weights - there is nothing to retarget.", nameof( mesh ) );

		var result = new Result { Mesh = mesh.Clone(), Skeleton = to };

		// Per source bone: where it maps to, and the transform from its bind pose to that one.
		var target = Resolve( from, to, map, result );
		var delta = new Xform[from.Count];

		for ( var b = 0; b < from.Count; b++ )
		{
			var t = target[b];

			if ( t < 0 )
				continue;

			// Source bind pose to target bind pose, both in model space. Undo where the source
			// bone stands, then stand where the target one does - but with the source's own roll
			// carried across rather than the target's. See Land.
			delta[b] = (adoptRoll ? to.WorldBind( t )
				: Land( from.WorldBind( b ), from.BoneDirection( b ), to.WorldBind( t ), to.BoneDirection( t ) ))
				* from.WorldBind( b ).Inverse;
		}

		// Ride-along bones keep the weight they were given but borrow their MOVEMENT from the
		// nearest ancestor that is a real correspondence, so their geometry stays where it sits
		// relative to that bone instead of collapsing onto the one it is now bound to. Walked
		// child-last so a chain of them (a whole pinky) all reach the same real ancestor.
		if ( rideAlong is { Count: > 0 } )
		{
			// Its own set, case-insensitively, because the map is matched that way and a caller
			// handing this in as IReadOnlyCollection would otherwise get LINQ's ordinal Contains
			// no matter what comparer their set was built with - a silent miss that looks exactly
			// like a missing entry.
			var passengers = new HashSet<string>( rideAlong, StringComparer.OrdinalIgnoreCase );
			var passenger = new bool[from.Count];

			for ( var b = 0; b < from.Count; b++ )
				passenger[b] = target[b] >= 0 && passengers.Contains( from.Bones[b].Name );

			for ( var b = 0; b < from.Count; b++ )
			{
				if ( !passenger[b] )
					continue;

				var anchor = from.Bones[b].Parent;

				while ( anchor >= 0 && (passenger[anchor] || target[anchor] < 0) )
					anchor = from.Bones[anchor].Parent;

				if ( anchor >= 0 )
					delta[b] = delta[anchor];
				else
					result.Unmapped.Add( $"{from.Bones[b].Name} rides along but has no anchored ancestor" );
			}
		}

		Rebind( result, from.Count, to.Count, target, delta );
		return result;
	}

	/// <summary>
	/// The other way round from <see cref="To"/>: move the TARGET skeleton into the mesh and leave
	/// the mesh exactly where it is.
	///
	/// WHY THIS EXISTS. <see cref="To"/> snaps every joint of the mesh onto the target's joints, so
	/// the model takes on the target's proportions. On a body that is a different shape that is a
	/// mangle: each bone's geometry moves rigidly to where the target's bone starts - citizen's
	/// upper arm is 10in against the Gearhead's 7.7 - and the skin bridges every gap by stretching.
	/// The Gearhead came out long-limbed and pinched at the joints with nothing wrong in its
	/// weights.
	///
	/// So here the bones move instead. Every target bone with a source counterpart stands on that
	/// bone's head; the ones with none - twists, helpers, IK - ride their parent, turned and
	/// stretched with it. Each bone keeps the TARGET's frame convention, swung by the shortest arc
	/// onto the direction the source limb actually runs: citizen's +X still runs down the bone and
	/// its roll is still citizen's. That is what lets citizen's clips drive it. A clip is local
	/// rotations in citizen's convention, so a bone whose axes mean what citizen's mean ends up
	/// pointing where citizen's would, and the mesh hanging off it follows. A T-posed arm on an
	/// A-posed skeleton is just a larger swing at the shoulder.
	///
	/// WHICH WAY A BONE RUNS is named by <paramref name="aims"/> - target bone name to the target
	/// bones it points at, averaged - not guessed. "Furthest child" is wrong on citizen twice over:
	/// spine_2's children include both clavicles, and the hand's furthest child is an IK rule bone
	/// forty inches away. A bone with no aim, or none that landed anywhere, keeps its parent's
	/// swing, which is right for a head, a fingertip, or anything rigid on its parent.
	///
	/// THE CLIPS STILL CARRY CITIZEN'S LENGTHS. Local translation is part of every clip, and a
	/// fitted bone played at citizen's translations is pulled straight back to citizen's
	/// proportions. The model that wears this must mark those bones ignore_Translation - see
	/// <see cref="CitizenBoneMap.KeepsOwnLength"/>.
	///
	/// Weights are rebound by the same map as <see cref="To"/>. Ride-along bones place a target
	/// bone only where no true correspondence does - the Gearhead's lids hinge where its own eyelid
	/// bones are because nothing else claims citizen's.
	/// </summary>
	public static Result Fit( PolyMesh mesh, Skeleton from, Skeleton to, IReadOnlyDictionary<string, string> map,
		IReadOnlyCollection<string> rideAlong, IReadOnlyDictionary<string, string[]> aims )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( from );
		ArgumentNullException.ThrowIfNull( to );
		ArgumentNullException.ThrowIfNull( map );
		ArgumentNullException.ThrowIfNull( aims );

		if ( !mesh.IsRigged )
			throw new ArgumentException( "The mesh has no skin weights - there is nothing to retarget.", nameof( mesh ) );

		var result = new Result { Mesh = mesh.Clone() };
		var target = Resolve( from, to, map, result );
		var passengers = new HashSet<string>( rideAlong ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase );

		// Which source bone's head each target bone stands on. True correspondences first, then
		// passengers for whatever is still free; within each, the bone nearest the root wins, so
		// `chest` and `spine_03` both naming spine_2 cannot fight over it.
		var placedBy = new int[to.Count];
		Array.Fill( placedBy, -1 );

		foreach ( var passes in new[] { false, true } )
		{
			for ( var b = 0; b < from.Count; b++ )
			{
				var t = target[b];

				if ( t >= 0 && placedBy[t] < 0 && passengers.Contains( from.Bones[b].Name ) == passes )
					placedBy[t] = b;
			}
		}

		var bind = new Xform[to.Count];
		var at = new Vec3[to.Count];

		for ( var t = 0; t < to.Count; t++ )
		{
			bind[t] = to.WorldBind( t );

			if ( placedBy[t] >= 0 )
				at[t] = from.WorldBind( placedBy[t] ).Origin;
		}

		// Parents before children, which the skeleton guarantees: an unplaced bone needs its
		// parent's final swing and stretch, and an aim only ever reads bones placed up front.
		var swing = new Xform[to.Count];
		var stretch = new float[to.Count];
		var fitted = new Xform[to.Count];

		for ( var t = 0; t < to.Count; t++ )
		{
			var parent = to.Bones[t].Parent;

			swing[t] = parent < 0 ? Xform.Identity : swing[parent];
			stretch[t] = parent < 0 ? 1f : stretch[parent];

			// Nothing stands here: carry the target's own offset from the parent, turned and
			// scaled the way the parent was, so a twist halfway down a limb stays halfway down it.
			if ( placedBy[t] < 0 )
			{
				at[t] = parent < 0 ? bind[t].Origin
					: at[parent] + swing[parent].TransformDirection( bind[t].Origin - bind[parent].Origin ) * stretch[parent];
			}

			if ( aims.TryGetValue( to.Bones[t].Name, out var aimNames ) )
			{
				var landed = aimNames.Select( to.IndexOf ).Where( a => a >= 0 && placedBy[a] >= 0 ).ToList();

				if ( landed.Count > 0 )
				{
					var was = Centroid( landed.Select( a => bind[a].Origin ) ) - bind[t].Origin;
					var now = Centroid( landed.Select( a => at[a] ) ) - at[t];

					if ( was.Length > 1e-4f && now.Length > 1e-4f )
					{
						swing[t] = Arc( was.Normal, now.Normal );
						stretch[t] = now.Length / was.Length;
					}
				}
			}

			var turned = swing[t] * new Xform( bind[t].X, bind[t].Y, bind[t].Z, Vec3.Zero );
			fitted[t] = new Xform( turned.X, turned.Y, turned.Z, at[t] );
		}

		var skeleton = to.Clone();

		for ( var t = 0; t < to.Count; t++ )
		{
			var parent = to.Bones[t].Parent;
			skeleton.Bones[t].Local = parent < 0 ? fitted[t] : fitted[parent].Inverse * fitted[t];
		}

		result.Skeleton = skeleton;

		Rebind( result, from.Count, to.Count, target, delta: null );
		return result;
	}

	/// <summary>Which target bone each source bone's weight goes to, or -1, with every miss
	/// recorded on <paramref name="result"/>.</summary>
	static int[] Resolve( Skeleton from, Skeleton to, IReadOnlyDictionary<string, string> map, Result result )
	{
		var target = new int[from.Count];

		for ( var b = 0; b < from.Count; b++ )
		{
			var name = from.Bones[b].Name;

			if ( !map.TryGetValue( name, out var wanted ) || string.IsNullOrEmpty( wanted ) )
			{
				target[b] = -1;
				result.Unmapped.Add( name );
				continue;
			}

			var t = to.IndexOf( wanted );

			if ( t < 0 )
			{
				target[b] = -1;
				result.Unmapped.Add( $"{name} -> {wanted} (the target has no such bone)" );
				continue;
			}

			target[b] = t;
		}

		return target;
	}

	/// <summary>
	/// Put every vertex's weight onto the target bones, and move it by <paramref name="delta"/>
	/// if there is one - <see cref="To"/> moves the mesh, <see cref="Fit"/> passes null and only
	/// renames what each vertex hangs off.
	/// </summary>
	static void Rebind( Result result, int fromCount, int toCount, int[] target, Xform[] delta )
	{
		var used = new bool[toCount];
		var skin = result.Mesh.Skin;

		for ( var v = 0; v < result.Mesh.VertexCount; v++ )
		{
			var weights = skin[v];
			var kept = 0f;

			foreach ( var w in weights )
			{
				if ( w.Bone >= 0 && w.Bone < fromCount && target[w.Bone] >= 0 )
					kept += w.Weight;
			}

			if ( kept <= 1e-6f )
			{
				// Nothing this vertex is attached to went anywhere. Moving it by an arbitrary bone
				// would be a guess; leaving it is at least honest, and the count says how bad it is.
				result.VerticesStranded++;
				skin[v] = Array.Empty<BoneWeight>();
				continue;
			}

			if ( kept < 0.999f )
				result.VerticesRenormalised++;

			// The vertex goes where its bones take it, averaged - the same blend that will skin it.
			var p = result.Mesh.Positions[v];
			var moved = Vec3.Zero;
			var merged = new Dictionary<int, float>();

			foreach ( var w in weights )
			{
				if ( w.Bone < 0 || w.Bone >= fromCount || target[w.Bone] < 0 )
					continue;

				var share = w.Weight / kept;

				if ( delta is not null )
					moved += delta[w.Bone].TransformPoint( p ) * share;

				var t = target[w.Bone];
				merged[t] = merged.TryGetValue( t, out var had ) ? had + share : share;
				used[t] = true;
			}

			if ( delta is not null )
				result.Mesh.Positions[v] = moved;

			skin[v] = merged.Select( kv => new BoneWeight( kv.Key, kv.Value ) )
				.OrderByDescending( w => w.Weight )
				.ToArray();
		}

		result.TargetBonesUnused = used.Count( u => !u );
		result.Unmapped = result.Unmapped.Distinct().ToList();
	}

	static Vec3 Centroid( IEnumerable<Vec3> points )
	{
		var sum = Vec3.Zero;
		var n = 0;

		foreach ( var p in points )
		{
			sum += p;
			n++;
		}

		return n == 0 ? sum : sum / n;
	}

	/// <summary>The shortest rotation taking unit <paramref name="from"/> onto unit
	/// <paramref name="to"/>. Antiparallel has no shortest one, so it turns about any fixed
	/// perpendicular - stable, and no worse than every other choice.</summary>
	static Xform Arc( Vec3 from, Vec3 to )
	{
		var axis = Vec3.Cross( from, to );
		var sin = axis.Length;
		var cos = Vec3.Dot( from, to );

		if ( sin >= 1e-6f )
			return Xform.Rotate( axis, MathF.Atan2( sin, cos ) );

		if ( cos > 0f )
			return Xform.Identity;

		var seed = MathF.Abs( from.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
		return Xform.Rotate( Vec3.Cross( from, seed ), MathF.PI );
	}

	/// <summary>
	/// Where a source bone should stand on the target skeleton: the target bone's position and
	/// direction, and the SOURCE bone's roll about that direction.
	///
	/// WHY NOT JUST TAKE THE TARGET'S FRAME. Aiming a bone head to tail pins two of its axes and
	/// leaves it free to spin about its own length, so every rig picks that roll by a convention
	/// of its own - citizen's came out of its fbx, a bone built by <see cref="Skeleton.AddBone"/>
	/// from head and tail points gets whatever <c>PerpendicularAxes</c> seeded. Two skeletons
	/// agreeing perfectly on where every joint is can still disagree about roll on every single
	/// bone, by any angle up to 180 degrees. Adopting the target's roll then spins each limb's
	/// geometry about its own length by a different arbitrary amount, which does not look like a
	/// convention mismatch - it looks like the mesh has been through a shredder.
	///
	/// So the twist is dropped and only the swing is kept: the shortest rotation that takes the
	/// source bone's direction onto the target's, applied to the source's own cross-axes. Nothing
	/// is lost by it. The skin binds against the target's real bind pose either way, and at rest
	/// pose equals bind, so the vertex stays exactly where this puts it - the delta is ours to
	/// choose, and the choice that moves geometry least is the right one.
	///
	/// DIRECTION COMES FROM GEOMETRY, NOT THE FRAME. <paramref name="sourceDir"/> and
	/// <paramref name="targetDir"/> are the bones' head-to-child directions, not their +Y axes -
	/// a frame's +Y is only the bone direction for an Effigy-built skeleton, and citizen is
	/// +X-down-the-bone. Swinging .Y onto .Y across that convention boundary spins the mesh a
	/// quarter turn about every limb.
	/// </summary>
	static Xform Land( Xform source, Vec3 sourceDir, Xform target, Vec3 targetDir )
	{
		var from = sourceDir.Normal;
		var to = targetDir.Normal;

		var axis = Vec3.Cross( from, to );
		var sin = axis.Length;
		var cos = Vec3.Dot( from, to );

		// Antiparallel has no shortest rotation - every half turn about a perpendicular axis is
		// equally short - so take the one about the source's own X, which is perpendicular by
		// construction and keeps the choice stable.
		var swing = sin < 1e-6f
			? (cos > 0f ? Xform.Identity : Xform.Rotate( source.X, MathF.PI ))
			: Xform.Rotate( axis, MathF.Atan2( sin, cos ) );

		// Swing the WHOLE source frame, not just its cross-axes with the target direction bolted
		// into the +Y slot. Slotting the direction into +Y assumed the target's bone direction IS
		// its +Y, which is true of an Effigy skeleton and false of citizen's +X-down-the-bone - on
		// the identity retarget that single-slot form stopped being the source frame and moved the
		// mesh. Applying the swing to every axis keeps the source's roll and collapses to the
		// source frame (and so an identity delta) exactly when the two directions already agree.
		return new Xform(
			swing.TransformDirection( source.X.Normal ),
			swing.TransformDirection( source.Y.Normal ),
			swing.TransformDirection( source.Z.Normal ),
			target.Origin );
	}
}
