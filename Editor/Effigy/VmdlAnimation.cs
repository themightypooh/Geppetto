using System.Text;

namespace Effigy;

/// <summary>
/// The AnimationList node a skinned .vmdl needs, and the bind pose inside it.
///
/// WHAT THIS CLOSES. ModelDoc's own documentation says a model that is not fully static needs at
/// least an `AnimBindPose` node or morph targets and IK data silently break — and this project's
/// skinned export has never had one, because nothing here had seen the node's real KV3 and a
/// guessed one risks breaking a compile that currently works. Copied from a real file, not a guess.
///
/// COPIED, NOT GUESSED. `first_person_arms_preview.vmdl` ships as source at
/// `sbox\addons\citizen\Assets\models\first_person\` and carries exactly this: an `AnimationList`
/// with `default_root_bone_name`, holding one `AnimBindPose` named `bindPose`. Citizen's own
/// `citizen_animationlist.vmdl_prefab` has the same node with the same fields, under the name
/// `bindPose_internal`, alongside a note of its own worth knowing: the compiled bind pose picks up
/// a little inaccuracy in bone orientations unless compression is disabled, and the INTERNAL one is
/// exact. Nothing here needs that distinction — one bind pose and no clips — so this writes the
/// plain one under the plain name.
///
/// EVERY FIELD IS WRITTEN, including the ones that look like defaults. A KV3 node with fields
/// missing is not the same thing as a node with them at their defaults: the compiler's defaults are
/// not documented anywhere this project can read, and the file that is known to work has all of
/// them. Copying it whole costs sixteen lines and removes the question.
/// </summary>
public static class VmdlAnimation
{
	/// <summary>
	/// The AnimationList node, indented to sit among a RootNode's children.
	///
	/// It is only ever wanted on a SKINNED model. A static export has no bones for a bind pose to be
	/// about, and adding one there would be a node that means nothing.
	/// </summary>
	public static string BindPoseList() =>
		"\t\t\t{\n"
		+ "\t\t\t\t_class = \"AnimationList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{\n"
		+ "\t\t\t\t\t\t_class = \"AnimBindPose\"\n"
		+ "\t\t\t\t\t\tname = \"bindPose\"\n"
		+ "\t\t\t\t\t\tactivity_name = \"\"\n"
		+ "\t\t\t\t\t\tactivity_weight = 1\n"
		+ "\t\t\t\t\t\tweight_list_name = \"\"\n"
		+ "\t\t\t\t\t\tfade_in_time = 0.2\n"
		+ "\t\t\t\t\t\tfade_out_time = 0.2\n"
		+ "\t\t\t\t\t\tlooping = false\n"
		+ "\t\t\t\t\t\tdelta = false\n"
		+ "\t\t\t\t\t\tworldSpace = false\n"
		+ "\t\t\t\t\t\thidden = false\n"
		+ "\t\t\t\t\t\tanim_markup_ordered = false\n"
		+ "\t\t\t\t\t\tdisable_compression = false\n"
		+ "\t\t\t\t\t\tdisable_interpolation = false\n"
		+ "\t\t\t\t\t\tenable_scale = false\n"
		+ "\t\t\t\t\t\tframe_count = 1\n"
		+ "\t\t\t\t\t\tframe_rate = 30\n"
		+ "\t\t\t\t\t},\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t\tdefault_root_bone_name = \"\"\n"
		+ "\t\t\t},\n";

	/// <summary>
	/// Every bone marked do_not_discard, so ModelDoc keeps bones this export gives it no other
	/// reason to keep.
	///
	/// CONFIRMED TWICE, and the second time is the one worth recording. ModelDoc prunes any bone that
	/// is neither weighted by the mesh nor animated by anything imported — first seen on
	/// `Assets/models/first_person/fp_arms.vmdl`, where arm_root and the two upper arms kept vanishing
	/// (27 bones in the FBX, 24 in the compiled model) until a BoneMarkupList fixed it. Then measured
	/// again from the other end: a two-bone sample .vmdl written WITHOUT this node compiled fine, and
	/// the loaded model reported exactly one bone. `root` survived, `child` did not.
	///
	/// So this is not belt and braces. Effigy's export has no AnimationList of clips, so nothing is
	/// ever "animated by anything imported", and every bone leans entirely on being weighted.
	/// SkinBinder's nearest-vertex fallback gives most bones at least one vertex, and a short helper
	/// joint that never ends up nearest to anything has nothing else keeping it alive.
	///
	/// Marking every bone rather than working out which ones are at risk costs a line each and
	/// removes the question. `bone_cull_type = "None"` on the list says the same thing again at the
	/// list level, which is what fp_arms does.
	///
	/// IN THE KERNEL RATHER THAN THE EDITOR because the sample .vmdl the suite writes has to carry
	/// the same node the editor writes, or compiling the sample answers a question about the sample.
	/// </summary>
	public static string BoneMarkupList( Skeleton skeleton )
	{
		if ( skeleton is null || skeleton.Count == 0 )
			return "";

		var sb = new StringBuilder();

		sb.Append( "\t\t\t{\n" );
		sb.Append( "\t\t\t\t_class = \"BoneMarkupList\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );

		foreach ( var bone in skeleton.Bones )
		{
			sb.Append( "\t\t\t\t\t{\n" );
			sb.Append( "\t\t\t\t\t\t_class = \"BoneMarkup\"\n" );
			sb.Append( $"\t\t\t\t\t\ttarget_bone = \"{bone.Name}\"\n" );
			sb.Append( "\t\t\t\t\t\tignore_Translation = false\n" );
			sb.Append( "\t\t\t\t\t\tignore_rotation = false\n" );
			sb.Append( "\t\t\t\t\t\tdo_not_discard = true\n" );
			sb.Append( "\t\t\t\t\t},\n" );
		}

		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t\tbone_cull_type = \"None\"\n" );
		sb.Append( "\t\t\t},\n" );

		return sb.ToString();
	}
}
