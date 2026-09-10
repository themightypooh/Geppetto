using Editor;
using Effigy;
using Sandbox;
using System;
using System.IO;
using System.Linq;

using Skeleton = Effigy.Skeleton;

namespace Marionette.EditorTools;

/// <summary>
/// Turn a rigged Effigy model into a playermodel — a model that carries citizen's 95 bones in
/// citizen's bind pose and names citizen's animation graph, so the graph drives it the way it
/// drives citizen.
///
///     effigy_playermodel models/effigy/gearhead_rigged.effigy            -> gearhead_rigged_citizen
///     effigy_playermodel models/effigy/gearhead_rigged.effigy gearhead_citizen
///
/// WHAT IT DOES, in order, on the way from the studio to the two files it writes:
///
///   1. Loads the .effigy and rebuilds it, the same way the rigged-export button does.
///   2. Binds each body to the bone the rig panel assigned it (BodyBoneMap), smooths the weights,
///      and applies any painted layer on top.
///   3. Fits <see cref="CitizenSkeleton"/> INTO the mesh through
///      <see cref="CitizenBoneMap.Playermodel"/> — citizen's bones, by citizen's names, standing
///      on the model's own joints. The mesh does not move and keeps its proportions.
///   4. Spreads limb weight onto the fitted twist bones, reorders depth-first, and remaps the
///      weights to match.
///   5. Writes a DMX (the mesh + the fitted bind pose + weights) and a .vmdl that names citizen's
///      animation graph and the citizen prefabs, with body bones marked to ignore the clips'
///      translation so citizen's bone lengths never reach it.
///
/// WHY FIT AND NOT SNAP. Snapping the mesh onto citizen's joints (<see cref="SkeletonRetarget.To"/>)
/// gives any model citizen's proportions and stretches the skin across every joint where the two
/// disagree — the Gearhead came out long-limbed and pinched. Fitting keeps the model's look and
/// still walks, because each fitted bone keeps citizen's ORIENTATION convention, pinned against
/// the engine in CitizenSkeletonTests: a bone with the right position and the wrong roll walks
/// into a knot, and nothing in the compiler says a word about it.
/// </summary>
public static class EffigyPlayermodelExport
{
	[ConCmd( "effigy_playermodel" )]
	public static void Run( string source = "", string outName = "" )
	{
		if ( string.IsNullOrWhiteSpace( source ) )
		{
			Log.Error( "[pm] usage: effigy_playermodel models/effigy/<name>.effigy [outName]" );
			return;
		}

		var root = EffigyAssetFolder.AssetsRoot();

		if ( root is null )
		{
			Log.Error( "[pm] could not resolve the project's Assets folder" );
			return;
		}

		var absSource = Path.GetFullPath( Path.Combine( root, source.Replace( '/', Path.DirectorySeparatorChar ) ) );

		if ( !File.Exists( absSource ) )
		{
			Log.Error( $"[pm] no file at {absSource}" );
			return;
		}

		PartStudio studio;

		try
		{
			studio = StudioDocument.ReadFile( absSource );
		}
		catch ( Exception e )
		{
			Log.Error( $"[pm] could not load {source}: {e.Message}" );
			return;
		}

		var report = studio.Rebuild();

		if ( report.HasErrors || studio.Bodies.Count == 0 )
		{
			Log.Error( $"[pm] the studio does not build - {report}" );
			return;
		}

		var name = string.IsNullOrWhiteSpace( outName )
			? Path.GetFileNameWithoutExtension( absSource ) + "_citizen"
			: outName.Trim();

		Export( studio, name );
	}

	/// <summary>
	/// Write and compile the playermodel for an already-rebuilt studio, and return the compiled
	/// asset path (<c>models/effigy/name.vmdl</c>), or null if nothing usable came out.
	///
	/// TAKES A STUDIO RATHER THAN A PATH, so the two callers can each do their own half. The console
	/// command loads a file; File -> Compile Playermodel hands over the document that is already
	/// open, unsaved edits included. Before the split the menu item would have had to save first,
	/// which is a surprising thing for a compile to do.
	///
	/// NO PIVOT IS APPLIED, unlike <c>CompileVmdl</c>. A playermodel's origin has to be between its
	/// feet, because that is where the engine stands a player and where citizen's clips are authored
	/// from; honouring a pivot the modeller set for some other export would sink or float the whole
	/// character.
	/// </summary>
	public static string Export( PartStudio studio, string name )
	{
		ArgumentNullException.ThrowIfNull( studio );

		if ( string.IsNullOrWhiteSpace( name ) )
		{
			Log.Error( "[pm] no name to write the playermodel under" );
			return null;
		}

		if ( studio.Bodies.Count == 0 )
		{
			Log.Error( "[pm] the studio has no bodies" );
			return null;
		}

		// WARNED, NOT REFUSED. A rigless studio still compiles into a citizen-skeleton model - every
		// vertex lands on one bone and the result is a statue that slides around - and saying so is
		// more use than a refusal to somebody who is halfway through rigging.
		if ( studio.Rig.Count == 0 )
			Log.Warning( "[pm] the studio has no rig - assign bones in the Rig panel, or this will "
				+ "compile into a model that animates as one rigid lump" );

		// Mesh and weights, exactly as the rigged export builds them.
		var (mesh, ranges) = studio.ToMeshWithBodies();
		var sourceSkeleton = studio.Rig;
		var weights = SkinBinder.BindBodies( mesh, ranges, studio.BodyBoneMap, sourceSkeleton );
		weights = SkinBinder.SmoothWeights( mesh, weights );

		if ( studio.WeightPaint is not null )
			studio.WeightPaint.Apply( mesh, weights, sourceSkeleton, out _ );

		mesh.Skin = weights;

		// Fit citizen's skeleton INTO the mesh rather than squashing the mesh onto citizen - the
		// model keeps its own proportions. Then spread twist weight along the fitted limbs and put
		// the skeleton in the order the DMX writer needs (depth-first), remapping the weights.
		var citizen = CitizenSkeleton.Build();
		var fit = SkeletonRetarget.Fit( mesh, sourceSkeleton, citizen,
			CitizenBoneMap.Playermodel(), CitizenBoneMap.UnrealStyleRideAlong(), CitizenBoneMap.ChainAims() );

		// THE ONE DIAGNOSTIC WORTH READING. An unmapped bone is a name citizen's animations have
		// never heard of, so whatever is weighted to it rides along with its parent instead of being
		// animated. Nearly always a typo or a convention mismatch, and nothing else in the pipeline
		// mentions it - the model compiles, loads and walks with one limb held stiff.
		if ( fit.Unmapped.Count > 0 )
			Log.Warning( $"[pm] {fit.Unmapped.Count} bone(s) have no animation name and will just "
				+ $"ride along with their parent: {string.Join( ", ", fit.Unmapped.Take( 12 ) )}" );

		if ( fit.VerticesStranded > 0 )
			Log.Warning( $"[pm] {fit.VerticesStranded} vertex/vertices had no placeable weight and were left where they were" );

		TwistWeights.Spread( fit.Mesh, fit.Skeleton );

		var (ordered, oldToNew) = SkeletonOrder.DepthFirst( fit.Skeleton );
		SkeletonOrder.Remap( fit.Mesh, oldToNew );

		var folder = EffigyAssetFolder.ResolveAssetFolder( "models/effigy" );
		Directory.CreateDirectory( folder );

		var dmxPath = Path.Combine( folder, $"{name}.dmx" );
		DmxWriter.WriteFile( fit.Mesh, dmxPath, ordered, materialName: studio.NameForSlot, modelName: name );

		var vmdlPath = Path.Combine( folder, $"{name}.vmdl" );
		File.WriteAllText( vmdlPath, PlayermodelVmdl( $"models/effigy/{name}.dmx", ordered ) );

		EffigyAssetFolder.Register( folder );

		var assetPath = $"models/effigy/{name}.vmdl";
		var asset = AssetSystem.FindByPath( assetPath );

		if ( asset is null )
		{
			Log.Warning( $"[pm] wrote {name}.dmx and {name}.vmdl but the asset system could not find the .vmdl" );
			return null;
		}

		asset.Compile( true );

		if ( asset.IsCompileFailed )
		{
			Log.Warning( $"[pm] {name}.vmdl compile FAILED - the compiler's output above says why. "
				+ "The .dmx is on disk either way." );
			return null;
		}

		Log.Info( $"[pm] {name}.vmdl compiled - {ordered.Count} citizen bones fitted to {fit.Mesh.VertexCount} vertices" );

		return assetPath;
	}

	/// <summary>
	/// The .vmdl that makes a citizen-skeleton DMX into a playermodel: citizen's animation graph,
	/// plus the prefabs citizen ships that hang the animation list, pose params, hitboxes, IK data
	/// and physics off a body. Read back from a playermodel the Model Editor produced, so it is
	/// the compiler's own spelling rather than a guess.
	///
	/// EVERY PREFAB BUT ONE. The bone markup is written here instead of included, because it is
	/// what keeps the fitted proportions: citizen's clips carry citizen's bone lengths, and without
	/// ignore_Translation on the body bones the graph drags every joint back to citizen's and the
	/// skin stretches between them. Citizen's own prefab marks the same bones with it off, and two
	/// markups naming one bone is a fight the compiler would settle without saying who won.
	/// </summary>
	static string PlayermodelVmdl( string meshFilename, Skeleton skeleton ) =>
		"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->\n"
		+ "{\n"
		+ "\trootNode = \n"
		+ "\t{\n"
		+ "\t\t_class = \"RootNode\"\n"
		+ "\t\tchildren = \n"
		+ "\t\t[\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"RenderMeshList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{\n"
		+ "\t\t\t\t\t\t_class = \"RenderMeshFile\"\n"
		+ "\t\t\t\t\t\tname = \"Body_LOD0\"\n"
		+ $"\t\t\t\t\t\tfilename = \"{meshFilename}\"\n"
		+ "\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n"
		+ "\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]\n"
		// CENTIMETRES IN, INCHES OUT. Citizen's clips, hitboxes and attachments are authored in
		// centimetres and citizen.vmdl scales the whole model by 0.3937 on the way through - clips
		// included. Leave the modifier out and every translation a clip carries lands 2.54 times too
		// big: with snapped bones that was stilt legs and a giraffe neck, with fitted ones it is the
		// pelvis riding four feet up and the legs locked straight reaching for IK targets that far
		// away. So the mesh, which is already in inches, goes in at 2.54 and comes out at 1.
		+ "\t\t\t\t\t\timport_scale = 2.54\n"
		+ "\t\t\t\t\t\talign_origin_x_type = \"None\"\n"
		+ "\t\t\t\t\t\talign_origin_y_type = \"None\"\n"
		+ "\t\t\t\t\t\talign_origin_z_type = \"None\"\n"
		+ "\t\t\t\t\t\tparent_bone = \"\"\n"
		+ "\t\t\t\t\t},\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"ModelModifierList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{\n"
		+ "\t\t\t\t\t\t_class = \"ModelModifier_ScaleAndMirror\"\n"
		+ "\t\t\t\t\t\tscale = 0.3937\n"
		+ "\t\t\t\t\t\tmirror_x = false\n"
		+ "\t\t\t\t\t\tmirror_y = false\n"
		+ "\t\t\t\t\t\tmirror_z = false\n"
		+ "\t\t\t\t\t\tflip_bone_forward = false\n"
		+ "\t\t\t\t\t\tswap_left_and_right_bones = false\n"
		+ "\t\t\t\t\t},\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"AnimConstraintList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_animconstraintlist.vmdl_prefab\" },\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"AnimationList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_animationlist.vmdl_prefab\" },\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_animationlist_unicycle.vmdl_prefab\" },\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_animationlist_debug.vmdl_prefab\" },\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_animationlist_visemes.vmdl_prefab\" },\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_animationlist_menu.vmdl_prefab\" },\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t\tdefault_root_bone_name = \"pelvis\"\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"BoneMarkupList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ BoneMarkup( skeleton )
		+ "\t\t\t\t]\n"
		+ "\t\t\t\tbone_cull_type = \"None\"\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"AttachmentList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_attachmentlist.vmdl_prefab\" },\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"PoseParamList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_poseparamlist.vmdl_prefab\" },\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"WeightListList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_weightlistlist.vmdl_prefab\" },\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"IKData\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_ikdata.vmdl_prefab\" },\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"GameDataList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_gamedatalist.vmdl_prefab\" },\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"HitboxSetList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_hitboxsetlist.vmdl_prefab\" },\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"PhysicsJointList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_physicsjointlist.vmdl_prefab\" },\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"PhysicsShapeList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/citizen_physicsshapelist.vmdl_prefab\" },\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n"
		+ "\t\t]\n"
		+ "\t\tmodel_archetype = \"\"\n"
		+ "\t\tprimary_associated_entity = \"\"\n"
		+ "\t\tanim_graph_name = \"models/citizen/citizen.vanmgrph\"\n"
		+ "\t\tbase_model_name = \"\"\n"
		+ "\t}\n"
		+ "}\n";

	/// <summary>
	/// One BoneMarkup per bone. Every bone is kept - the compiler discards bones nothing is
	/// weighted to, and the graph writes to twists, helpers and IK targets that carry no weight by
	/// design - and each says whether it keeps its own length; see
	/// <see cref="CitizenBoneMap.KeepsOwnLength"/>.
	/// </summary>
	static string BoneMarkup( Skeleton skeleton ) => string.Concat( Enumerable.Range( 0, skeleton.Count ).Select( b =>
		$"\t\t\t\t\t{{ \"_class\" = \"BoneMarkup\" target_bone = \"{skeleton.Bones[b].Name}\" "
		+ $"ignore_Translation = {(CitizenBoneMap.KeepsOwnLength( skeleton, b ) ? "true" : "false")} "
		+ "ignore_rotation = false do_not_discard = true },\n" ) );
}
