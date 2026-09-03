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
	/// One clip to compile into the model: what it will be called, the animation file it comes
	/// from, and whether it loops.
	///
	/// <paramref name="SourceFilename"/> is a path the COMPILER resolves, so it is relative to the
	/// game's content root the same way a RenderMeshFile's filename is — "models/thing/wave.dmx",
	/// not a path on disk. A path that does not resolve is not an error at compile time; the clip
	/// is simply not there afterwards.
	/// </summary>
	public readonly record struct ClipEntry( string Name, string SourceFilename, bool Looping = false );

	/// <summary>
	/// The AnimationList node, indented to sit among a RootNode's children.
	///
	/// It is only ever wanted on a SKINNED model. A static export has no bones for a bind pose to be
	/// about, and adding one there would be a node that means nothing.
	/// </summary>
	public static string BindPoseList() => AnimationList();

	/// <summary>
	/// The same node, carrying clips as well as the bind pose.
	///
	/// THE BIND POSE STAYS EVEN WHEN THERE ARE CLIPS. It is tempting to read "a non-static model
	/// needs an AnimBindPose or morph targets and IK break" as a requirement that clips satisfy
	/// too — they do not; the bind pose is what the model is when nothing is playing, and citizen's
	/// own animation list carries one alongside several hundred clips.
	///
	/// Clips come after it in the list, in the order given. ModelDoc also allows `Folder` nodes for
	/// grouping, which nothing here writes: a folder changes only what the ModelDoc UI looks like,
	/// and every extra node shape is one more thing to have got wrong.
	/// </summary>
	public static string AnimationList( params ClipEntry[] clips )
	{
		var sb = new StringBuilder();

		sb.Append( "\t\t\t{\n" );
		sb.Append( "\t\t\t\t_class = \"AnimationList\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );
		sb.Append( BindPose() );

		if ( clips is not null )
		{
			foreach ( var clip in clips )
				sb.Append( AnimFile( clip ) );
		}

		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t\tdefault_root_bone_name = \"\"\n" );
		sb.Append( "\t\t\t},\n" );

		return sb.ToString();
	}

	/// <summary>The AnimBindPose child, on its own. See the class header for where every field came
	/// from.</summary>
	static string BindPose() =>
		"\t\t\t\t\t{\n"
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
		+ "\t\t\t\t\t},\n";

	/// <summary>
	/// One AnimFile child — a clip the compiler reads out of an external animation file.
	///
	/// COPIED, NOT GUESSED, from `citizen_animationlist.vmdl_prefab`, which ships as source at
	/// `addons/citizen/Assets/models/citizen/prefabs/` and carries several hundred of these. Same
	/// rule as the bind pose above and for the same reason: a KV3 node with fields missing is not
	/// a node with them at their defaults, and the compiler's defaults are not documented anywhere
	/// this project can read.
	///
	/// THE THREE FIELDS THAT LOOK LIKE PLACEHOLDERS ARE NOT. `start_frame` and `end_frame` at -1
	/// mean "the whole file" — a real frame range here would trim the clip. `framerate` at -1.0
	/// means "whatever the source says", which is what a baked DMX carries in its own time values,
	/// and setting a number here resamples the clip instead of describing it.
	///
	/// `fade_in_time` and `fade_out_time` are 0.05 rather than the bind pose's 0.2 because that is
	/// what citizen's clips use; they are blend times AnimGraph reads when a transition does not
	/// specify its own, so they are a default rather than a constant.
	/// </summary>
	public static string AnimFile( ClipEntry clip ) =>
		"\t\t\t\t\t{\n"
		+ "\t\t\t\t\t\t_class = \"AnimFile\"\n"
		+ $"\t\t\t\t\t\tname = \"{clip.Name}\"\n"
		+ "\t\t\t\t\t\tactivity_name = \"\"\n"
		+ "\t\t\t\t\t\tactivity_weight = 1\n"
		+ "\t\t\t\t\t\tweight_list_name = \"\"\n"
		+ "\t\t\t\t\t\tfade_in_time = 0.05\n"
		+ "\t\t\t\t\t\tfade_out_time = 0.05\n"
		+ $"\t\t\t\t\t\tlooping = {(clip.Looping ? "true" : "false")}\n"
		+ "\t\t\t\t\t\tdelta = false\n"
		+ "\t\t\t\t\t\tworldSpace = false\n"
		+ "\t\t\t\t\t\thidden = false\n"
		+ "\t\t\t\t\t\tanim_markup_ordered = false\n"
		+ "\t\t\t\t\t\tdisable_compression = false\n"
		+ "\t\t\t\t\t\tdisable_interpolation = false\n"
		+ "\t\t\t\t\t\tenable_scale = false\n"
		+ $"\t\t\t\t\t\tsource_filename = \"{clip.SourceFilename}\"\n"
		+ "\t\t\t\t\t\tstart_frame = -1\n"
		+ "\t\t\t\t\t\tend_frame = -1\n"
		+ "\t\t\t\t\t\tframerate = -1.0\n"
		+ "\t\t\t\t\t\ttake = 0\n"
		+ "\t\t\t\t\t\treverse = false\n"
		+ "\t\t\t\t\t},\n";

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
