using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Correspondences from common humanoid bone namings onto <see cref="CitizenSkeleton"/>.
///
/// WHY A PRESET RATHER THAN A GUESS. <see cref="SkeletonRetarget"/> refuses to infer the map,
/// because a wrong inference looks like bad weights rather than a bad guess. But the naming most
/// rigs arrive in is not a mystery - Unreal's skeleton and everything that copies it use
/// `upperarm_L`, `thigh_L`, `spine_01`, and that convention covers a large fraction of anything
/// somebody drags in. So the guessing happens ONCE, here, in the open, where the decisions can be
/// read and argued with.
///
/// TWO PLACES CITIZEN CANNOT TAKE WHAT IT IS OFFERED, both recorded rather than hidden:
///
/// A PINKY HAS NOWHERE TO GO. Citizen's hand is index, middle, ring and thumb - there is no
/// `finger_pinky_*`. A five-fingered mesh's pinky is mapped onto the ring chain, so it follows the
/// nearest finger instead of going rigid. Several source bones naming one target is a case
/// SkeletonRetarget handles by summing, so this needs nothing special of the caller.
///
/// A BROW HAS NOWHERE TO GO EITHER. Citizen carries eyelids (`face_lid_upper_L`) and eyes, but no
/// brow. Brow bones map onto `head`, which makes them rigid rather than wrong - the alternative is
/// stranding whatever they were weighted to.
/// </summary>
public static class CitizenBoneMap
{
	/// <summary>
	/// Unreal-style humanoid naming onto citizen's, for both sides.
	///
	/// Keys are SOURCE bone names, as <see cref="SkeletonRetarget.To"/> wants them. A source
	/// skeleton missing some of these is fine; entries with no matching bone are simply never
	/// consulted.
	/// </summary>
	/// <summary>
	/// Source bones from <see cref="UnrealStyle"/> that are bound to a citizen bone but must NOT
	/// snap onto it — pass this as <c>rideAlong</c> to <see cref="SkeletonRetarget.To"/>.
	///
	/// Each of these is a bone citizen does not really have. The pinky is bound to the ring finger
	/// so it still bends with the hand; the brow to the head; the eyelids to citizen's, which sit
	/// on a differently shaped skull five inches away. Left to snap, every one of them drags its
	/// geometry across the model — measured at 9.1 inches for the eyelids on the Gearhead, which
	/// looked like the face had exploded.
	/// </summary>
	public static HashSet<string> UnrealStyleRideAlong()
	{
		var set = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var side in new[] { "L", "R" } )
		{
			for ( var j = 1; j <= 3; j++ )
				set.Add( $"pinky_0{j}_{side}" );

			set.Add( $"brow_{side}" );
			set.Add( $"lid_{side}" );
			set.Add( $"lidup_{side}" );
			set.Add( $"lidlo_{side}" );
			set.Add( $"iris_{side}" );
			set.Add( $"pupil_{side}" );
			set.Add( $"thumb_03_{side}" );
		}

		return set;
	}

	/// <summary>
	/// Which bone each of citizen's bones points at, for <see cref="SkeletonRetarget.Fit"/> - the
	/// direction a bone runs is what gets swung onto the source's limb.
	///
	/// NAMED, NOT DERIVED, because the derivation that works on a clean chain is wrong on citizen.
	/// spine_2's children are the neck AND both clavicles; the hand's furthest child is an IK rule
	/// bone forty inches away. The hand points at the middle of its knuckles, so no one finger's
	/// splay tilts it.
	///
	/// A bone missing from here keeps its parent's swing: the head, the fingertips, the balls of
	/// the feet, every twist, helper and IK bone. That is the right answer for all of them - they
	/// hang rigidly off their parent, or the anim graph drives them.
	/// </summary>
	public static Dictionary<string, string[]> ChainAims()
	{
		var aims = new Dictionary<string, string[]>( StringComparer.OrdinalIgnoreCase )
		{
			["pelvis"] = new[] { "spine_0" },
			["spine_0"] = new[] { "spine_1" },
			["spine_1"] = new[] { "spine_2" },
			["spine_2"] = new[] { "neck_0" },
			["neck_0"] = new[] { "head" },
		};

		foreach ( var side in new[] { "L", "R" } )
		{
			aims[$"clavicle_{side}"] = new[] { $"arm_upper_{side}" };
			aims[$"arm_upper_{side}"] = new[] { $"arm_lower_{side}" };
			aims[$"arm_lower_{side}"] = new[] { $"hand_{side}" };
			aims[$"hand_{side}"] = new[] { $"finger_index_0_{side}", $"finger_middle_0_{side}", $"finger_ring_0_{side}" };

			foreach ( var finger in new[] { "index", "middle", "ring" } )
			{
				aims[$"finger_{finger}_meta_{side}"] = new[] { $"finger_{finger}_0_{side}" };
				aims[$"finger_{finger}_0_{side}"] = new[] { $"finger_{finger}_1_{side}" };
				aims[$"finger_{finger}_1_{side}"] = new[] { $"finger_{finger}_2_{side}" };
			}

			aims[$"finger_thumb_0_{side}"] = new[] { $"finger_thumb_1_{side}" };
			aims[$"finger_thumb_1_{side}"] = new[] { $"finger_thumb_2_{side}" };

			aims[$"leg_upper_{side}"] = new[] { $"leg_lower_{side}" };
			aims[$"leg_lower_{side}"] = new[] { $"ankle_{side}" };
			aims[$"ankle_{side}"] = new[] { $"ball_{side}" };
		}

		return aims;
	}

	/// <summary>
	/// Whether a bone of a FITTED citizen should ignore the translation in citizen's clips - the
	/// `ignore_Translation` flag on the model's BoneMarkup.
	///
	/// A clip carries every bone's local translation, which is citizen's bone lengths. Play it on a
	/// skeleton fitted into a differently proportioned body and each bone is pulled back to
	/// citizen's length, stretching the skin between them exactly as a snapping retarget does. With
	/// the flag, the bone keeps its bind translation - the fitted one - and takes only the clip's
	/// rotation. Citizen's own markup already does this to its twist, helper and aim bones, which
	/// is how it is known what the flag means.
	///
	/// NOT EVERYTHING. The pelvis keeps the clip's translation: that is the body's height and the
	/// bob in the walk. root_IK and its targets keep theirs, because an IK target's translation is
	/// the clip saying where the hand or foot goes, and so do the ikrule bones that carry one hand's
	/// placement relative to the other.
	/// </summary>
	public static bool KeepsOwnLength( Skeleton skeleton, int bone )
	{
		ArgumentNullException.ThrowIfNull( skeleton );

		var name = skeleton.Bones[bone].Name;

		if ( name.StartsWith( "aim_matrix", StringComparison.OrdinalIgnoreCase ) )
			return true;

		if ( name.Contains( "ikrule", StringComparison.OrdinalIgnoreCase ) || name == "pelvis" )
			return false;

		for ( var p = skeleton.Bones[bone].Parent; p >= 0; p = skeleton.Bones[p].Parent )
		{
			if ( skeleton.Bones[p].Name == "pelvis" )
				return true;
		}

		return false;
	}

	public static Dictionary<string, string> UnrealStyle()
	{
		var map = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			["pelvis"] = "pelvis",
			["spine_01"] = "spine_0",
			["spine_02"] = "spine_1",
			["spine_03"] = "spine_2",
			["chest"] = "spine_2",
			["neck"] = "neck_0",
			["neck_01"] = "neck_0",
			["head"] = "head",
		};

		foreach ( var side in new[] { "L", "R" } )
		{
			map[$"clavicle_{side}"] = $"clavicle_{side}";
			map[$"upperarm_{side}"] = $"arm_upper_{side}";
			map[$"forearm_{side}"] = $"arm_lower_{side}";
			map[$"hand_{side}"] = $"hand_{side}";

			map[$"thigh_{side}"] = $"leg_upper_{side}";
			map[$"calf_{side}"] = $"leg_lower_{side}";
			map[$"foot_{side}"] = $"ankle_{side}";
			map[$"ball_{side}"] = $"ball_{side}";
			map[$"toe_{side}"] = $"ball_{side}";

			// Source numbering starts at 01, citizen's at 0.
			foreach ( var finger in new[] { "index", "middle", "ring" } )
			{
				for ( var j = 1; j <= 3; j++ )
					map[$"{finger}_0{j}_{side}"] = $"finger_{finger}_{j - 1}_{side}";
			}

			// See the note above: citizen has no pinky, so it rides the ring finger.
			for ( var j = 1; j <= 3; j++ )
				map[$"pinky_0{j}_{side}"] = $"finger_ring_{j - 1}_{side}";

			// And citizen's thumb is two bones where most rigs have two or three; a third folds
			// onto the tip rather than being stranded.
			map[$"thumb_01_{side}"] = $"finger_thumb_0_{side}";
			map[$"thumb_02_{side}"] = $"finger_thumb_1_{side}";
			map[$"thumb_03_{side}"] = $"finger_thumb_2_{side}";

			map[$"lid_{side}"] = $"face_lid_upper_{side}";
			map[$"lidup_{side}"] = $"face_lid_upper_{side}";
			map[$"lidlo_{side}"] = $"face_lid_lower_{side}";
			map[$"eye_{side}"] = $"eye_{side}";
			map[$"ear_{side}"] = $"ear_{side}";

			// An eye built as a stack - eyeball, iris, pupil - is three bones where citizen has
			// one. The inner two ride the eye rather than snapping onto it, or the iris and pupil
			// both land on the eyeball's centre and disappear inside it.
			map[$"iris_{side}"] = $"eye_{side}";
			map[$"pupil_{side}"] = $"eye_{side}";

			// No citizen brow. Rigid on the head beats stranded.
			map[$"brow_{side}"] = "head";
		}

		return map;
	}

	/// <summary>
	/// The map a playermodel export should use: <see cref="UnrealStyle"/>, plus every one of
	/// citizen's own bone names standing for itself.
	///
	/// WHY BOTH. A rig arrives named one of two ways and neither is wrong. Somebody following the
	/// tutorial names bones `upperarm_L` and `thigh_L`, because that is the convention the tutorial
	/// teaches and the one most tools produce. Somebody who imported citizen's skeleton, or who read
	/// the bone list out of the engine, has `arm_upper_L` and `leg_upper_L` already - and under
	/// UnrealStyle alone every one of those bones is UNMAPPED, which strands the geometry weighted
	/// to it. An identity entry costs nothing and removes a whole class of "it compiled and came out
	/// inside out".
	///
	/// UNREAL'S SPELLING WINS A COLLISION, because the identity entries are added first and the
	/// Unreal ones overwrite them. Only `head`, `hand_L` and `hand_R` are spelled the same in both
	/// conventions, and all three mean the same bone, so there is no collision that matters - but
	/// the order is fixed rather than incidental, so a future name added to either side cannot
	/// quietly change which target an existing rig resolves to.
	/// </summary>
	public static Dictionary<string, string> Playermodel()
	{
		var map = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var bone in CitizenSkeleton.Build().Bones )
			map[bone.Name] = bone.Name;

		foreach ( var pair in UnrealStyle() )
			map[pair.Key] = pair.Value;

		return map;
	}
}
