using System;

namespace Effigy;

/// <summary>
/// The .vmdl ModelDoc reads, built in one place.
///
/// WHY THIS IS A FILE AND NOT A STRING LITERAL IN THE EXPORTER. There were four copies of this
/// template — EffigyWindow.BuildVmdl, EffigyWindow.BuildSkinnedVmdl, TreeGen and TentacleGen —
/// and only the first one carried the import_rotation correction below. The other three said
/// [0, 0, 0], which is right for DMX and catastrophically wrong for OBJ, and nothing about a
/// copied literal tells you which case you are in. Anything outside the editor window that wanted
/// a model — the remesh harness, a generator — had no way to ask for a correct one, so it
/// hand-made a vmdl in ModelDoc instead and got the rotation wrong the same way.
///
/// THE ROTATION IS DERIVED FROM THE MESH FILE, not passed in, because that is the actual rule:
/// it is the OBJ importer specifically that turns the mesh, so the extension decides. See
/// <see cref="ImportRotation"/>. A caller cannot get it wrong by forgetting to pass it.
/// </summary>
public static class VmdlDocument
{
	const string Header =
		"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} "
		+ "format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n";

	/// <summary>
	/// What to put in import_rotation for a mesh file, as the three numbers of the kv3 array.
	///
	/// ModelDoc's OBJ importer does not land the mesh in the coordinates the file gives it. It
	/// reads the file as Y-up (the OBJ convention) and then turns it another quarter turn, so the
	/// whole thing arrives cyclically permuted:
	///
	///     engine.x = obj.z    engine.y = obj.x    engine.z = obj.y
	///
	/// The kernel is Z-up — its sketch planes are named "Top (XY)", "Front (XZ)", "Right (YZ)" —
	/// so this is TWO errors stacked. A bare -90 yaw undoes the extra turn and leaves the Y-up
	/// reading in place, landing the mesh at (obj.x, -obj.z, obj.y): a part drawn lying flat comes
	/// out standing on its side. [-90, -90, 0] is the full inverse of the permutation above and
	/// puts the mesh back in the coordinates the file was written in.
	///
	/// MEASURED. A two-box part whose OBJ bounds are 155 x 159 x 84 compiled to 84 x 155 x 159 at
	/// rotation zero — the permutation, read straight off the numbers — and to 155 x 159 x 84 at
	/// [-90, -90, 0], with the bar still pointing along +x and the raised lip still on top, so this
	/// is the identity and not some other transform that happens to share its bounds.
	///
	/// This matters most for collision. Physics shapes are written in kernel coordinates and
	/// import_rotation does not touch them, so the mesh has to arrive in kernel coordinates too,
	/// or the collision sits at an angle to the model it belongs to.
	///
	/// DMX AND SMD MUST NOT GET THIS. Only the OBJ importer turns the mesh; the DMX importer lands
	/// it where the file says. Applying the correction there would break the rigged path, which is
	/// why this is decided per file rather than set once.
	/// </summary>
	public static string ImportRotation( string meshFilename ) =>
		meshFilename is not null
		&& meshFilename.EndsWith( ".obj", StringComparison.OrdinalIgnoreCase )
			? "-90.0, -90.0, 0.0"
			: "0.0, 0.0, 0.0";

	/// <summary>The RenderMeshList node — one RenderMeshFile pointing at the mesh on disk.</summary>
	static string RenderMeshList( string meshFilename ) =>
		"\t\t\t{\n"
		+ "\t\t\t\t_class = \"RenderMeshList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{\n"
		+ "\t\t\t\t\t\t_class = \"RenderMeshFile\"\n"
		+ "\t\t\t\t\t\tname = \"Body_LOD0\"\n"
		+ "\t\t\t\t\t\tchildren = \n"
		+ "\t\t\t\t\t\t[\n"
		+ "\t\t\t\t\t\t]\n"
		+ $"\t\t\t\t\t\tfilename = \"{meshFilename}\"\n"
		+ "\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n"
		+ $"\t\t\t\t\t\timport_rotation = [ {ImportRotation( meshFilename )} ]\n"
		+ "\t\t\t\t\t\timport_scale = 1.0\n"
		+ "\t\t\t\t\t\talign_origin_x_type = \"None\"\n"
		+ "\t\t\t\t\t\talign_origin_y_type = \"None\"\n"
		+ "\t\t\t\t\t\talign_origin_z_type = \"None\"\n"
		+ "\t\t\t\t\t\tparent_bone = \"\"\n"
		+ "\t\t\t\t\t},\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n";

	static string Wrap( string children, string animGraph = "" ) =>
		Header
		+ "{\n"
		+ "\trootNode = \n"
		+ "\t{\n"
		+ "\t\t_class = \"RootNode\"\n"
		+ "\t\tchildren = \n"
		+ "\t\t[\n"
		+ children
		+ "\t\t]\n"
		+ "\t\tmodel_archetype = \"\"\n"
		+ "\t\tprimary_associated_entity = \"\"\n"
		+ $"\t\tanim_graph_name = \"{animGraph}\"\n"
		+ "\t\tbase_model_name = \"\"\n"
		+ "\t}\n"
		+ "}\n";

	/// <summary>
	/// A static model: one mesh file, whatever PhysicsShapeList and MaterialGroupList the caller
	/// built, and no skeleton.
	///
	/// MATERIALS MATTER AS MUCH AS COLLISION. An omitted MaterialGroupList is what ModelDoc fills
	/// in with use_global_default = true and materials/default.vmat — a part that rendered in the
	/// viewport with the materials that were dropped on it compiles as a blank grey prop. Pass an
	/// empty list with the global default off to leave the mesh's own names in place.
	/// </summary>
	public static string Static( string meshFilename, string physics = "", string materials = "" ) =>
		Wrap( materials + RenderMeshList( meshFilename ) + physics );

	/// <summary>
	/// Citizen's animation graph. A skinned model that names this, and carries citizen's bones
	/// under citizen's names in citizen's bind pose, is a playermodel - the graph drives it exactly
	/// as it drives citizen. Nothing validates the pairing: naming the graph on a model with its
	/// own skeleton compiles and then animates into a knot, which is why
	/// <see cref="SkeletonRetarget"/> exists.
	/// </summary>
	public const string CitizenAnimGraph = "models/citizen/citizen.vanmgrph";

	/// <summary>
	/// A skinned model: the mesh file carries the bone hierarchy, bind pose and per-vertex weights,
	/// so this adds the bone markup and the animation list on top of the static shape.
	/// </summary>
	/// <param name="meshFilename">The mesh on disk, project-relative. Its extension decides the
	/// import rotation — see <see cref="ImportRotation"/>.</param>
	/// <param name="skeleton">The bones the mesh file carries, for the bone markup node.</param>
	/// <param name="physics">The PhysicsShapeList node, or empty for no collision.</param>
	/// <param name="materials">The MaterialGroupList node.</param>
	/// <param name="animations">The AnimationList node. Null or empty means the bind pose alone —
	/// <see cref="VmdlAnimation.AnimationList"/> with no clips is byte-identical to
	/// <see cref="VmdlAnimation.BindPoseList"/>, and a test holds that, so the no-clips path is
	/// unchanged rather than merely equivalent.</param>
	/// <param name="animGraph">An animation graph to drive the model, project-relative — see
	/// <see cref="CitizenAnimGraph"/>. Empty, the default, leaves the model animated only by
	/// whatever clips it carries, which is what every Effigy export wanted before playermodels.</param>
	public static string Skinned( string meshFilename, Skeleton skeleton, string physics = "",
		string materials = "", string animations = null, string animGraph = "" ) =>
		Wrap( materials
			+ RenderMeshList( meshFilename )
			+ VmdlAnimation.BoneMarkupList( skeleton )
			+ (string.IsNullOrEmpty( animations ) ? VmdlAnimation.BindPoseList() : animations)
			+ physics, animGraph );
}
