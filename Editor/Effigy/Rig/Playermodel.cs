using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// The engine-free half of making a playermodel: bind a rigged studio, fit citizen's skeleton INTO
/// it, and write the .vmdl that points citizen's animation graph at the result.
///
/// IN THE KERNEL so the editor's File > Make Player and a headless generator run the same steps.
/// The editor adds only what needs the engine - resolving the Assets folder and compiling.
///
/// WHY FIT AND NOT SNAP. Snapping the mesh onto citizen's joints (<see cref="SkeletonRetarget.To"/>)
/// gives any model citizen's proportions and stretches the skin across every joint where the two
/// disagree - the Gearhead came out long-limbed and pinched. Fitting keeps the model's look and
/// still walks, because each fitted bone keeps citizen's ORIENTATION convention, pinned against
/// the engine in CitizenSkeletonTests: a bone with the right position and the wrong roll walks
/// into a knot, and nothing in the compiler says a word about it.
/// </summary>
public static class Playermodel
{
	public sealed class Result
	{
		/// <summary>The mesh, weighted to <see cref="Skeleton"/> and in its depth-first order.</summary>
		public PolyMesh Mesh;

		/// <summary>Citizen's bones, fitted to the mesh, depth-first - the order the DMX writer needs.</summary>
		public Skeleton Skeleton;

		/// <summary>Source bones citizen's animations have no name for; whatever they carry rides along.</summary>
		public IReadOnlyList<string> Unmapped;

		public int VerticesStranded;
	}

	/// <summary>
	/// Bind, fit, spread twist weight and order - everything between a rebuilt studio and the DMX.
	/// No pivot is applied: a playermodel's origin is between its feet, because that is where the
	/// engine stands a player and where citizen's clips are authored from.
	/// </summary>
	public static Result Build( PartStudio studio )
	{
		ArgumentNullException.ThrowIfNull( studio );

		var (mesh, ranges) = studio.ToMeshWithBodies();
		var sourceSkeleton = studio.Rig;
		var weights = SkinBinder.BindBodies( mesh, ranges, studio.BodyBoneMap, sourceSkeleton );
		weights = SkinBinder.SmoothWeights( mesh, weights );

		if ( studio.WeightPaint is not null )
			studio.WeightPaint.Apply( mesh, weights, sourceSkeleton, out _ );

		mesh.Skin = weights;

		var fit = SkeletonRetarget.Fit( mesh, sourceSkeleton, CitizenSkeleton.Build(),
			CitizenBoneMap.Playermodel(), CitizenBoneMap.UnrealStyleRideAlong(), CitizenBoneMap.ChainAims() );

		TwistWeights.Spread( fit.Mesh, fit.Skeleton );

		var (ordered, oldToNew) = SkeletonOrder.DepthFirst( fit.Skeleton );
		SkeletonOrder.Remap( fit.Mesh, oldToNew );

		return new Result
		{
			Mesh = fit.Mesh,
			Skeleton = ordered,
			Unmapped = fit.Unmapped.ToList(),
			VerticesStranded = fit.VerticesStranded,
		};
	}

	/// <summary>How much a <see cref="Check"/> finding matters.</summary>
	public enum Severity
	{
		/// <summary>Worth knowing. The model will work.</summary>
		Note,

		/// <summary>It will export and run, but something will look wrong in game.</summary>
		Warning,

		/// <summary>It will not work as a playermodel.</summary>
		Problem,
	}

	public readonly struct Finding
	{
		public readonly Severity Severity;

		/// <summary>What is wrong, in one line.</summary>
		public readonly string Problem;

		/// <summary>What to do about it. The whole point of the check - a finding you cannot act on
		/// is just bad news.</summary>
		public readonly string Remedy;

		public Finding( Severity severity, string problem, string remedy )
		{
			Severity = severity;
			Problem = problem;
			Remedy = remedy;
		}

		public override string ToString() => $"{Severity}: {Problem}. {Remedy}";
	}

	/// <summary>
	/// Everything that makes a model fail as a PLAYERMODEL rather than as a mesh, checked against
	/// the model the exporter will actually write.
	///
	/// WHY THIS IS SEPARATE FROM MeshValidator. A mesh can be perfectly valid - closed, manifold,
	/// wound outward - and still be useless as a playermodel: weighted to six bones where the
	/// exporter writes four, standing a foot tall, or with a hand that no bone moves. None of that
	/// is a mesh problem, and none of it is visible until the model is in the game standing wrong.
	///
	/// Run on the RESULT of <see cref="Build"/>, not on the studio, because that is what ships:
	/// binding, fitting and twist spreading all change the weights, and checking before them would
	/// pass a model that fails after.
	/// </summary>
	public static List<Finding> Check( Result result, int maxInfluences = 4 )
	{
		ArgumentNullException.ThrowIfNull( result );

		var findings = new List<Finding>();
		var mesh = result.Mesh;
		var skeleton = result.Skeleton;

		if ( mesh is null || mesh.FaceCount == 0 )
		{
			findings.Add( new Finding( Severity.Problem, "The model has no faces",
				"Add a body, or check that the features above are not all suppressed." ) );
			return findings;
		}

		if ( skeleton is null || skeleton.Count == 0 )
		{
			findings.Add( new Finding( Severity.Problem, "The model has no skeleton",
				"Build a rig in the Rig workspace before exporting as a playermodel." ) );
			return findings;
		}

		// --- weights -------------------------------------------------------------------------

		if ( !mesh.IsRigged )
		{
			findings.Add( new Finding( Severity.Problem, "The model carries no skin weights",
				"Bind the bodies to the rig; without weights every vertex moves with the root bone." ) );
		}
		else
		{
			var overWeighted = 0;
			var unweighted = 0;
			var offSum = 0;
			var outOfRange = 0;
			var used = new HashSet<int>();

			for ( var v = 0; v < mesh.VertexCount && v < mesh.Skin.Count; v++ )
			{
				var w = mesh.Skin[v];

				if ( w.Length == 0 )
				{
					unweighted++;
					continue;
				}

				if ( w.Length > maxInfluences )
					overWeighted++;

				var sum = 0f;

				foreach ( var bw in w )
				{
					sum += bw.Weight;

					if ( bw.Bone < 0 || bw.Bone >= skeleton.Count )
						outOfRange++;
					else
						used.Add( bw.Bone );
				}

				if ( MathF.Abs( sum - 1f ) > 1e-3f )
					offSum++;
			}

			if ( unweighted > 0 )
				findings.Add( new Finding( Severity.Problem,
					$"{unweighted:N0} vertex(es) have no skin weights",
					"They will collapse onto the model's origin when it animates. Re-bind, or use Copy weights on the body they belong to." ) );

			if ( outOfRange > 0 )
				findings.Add( new Finding( Severity.Problem,
					$"{outOfRange:N0} weight(s) name a bone that is not in the skeleton",
					"The rig changed after the weights were made. Re-bind the bodies." ) );

			if ( overWeighted > 0 )
				findings.Add( new Finding( Severity.Warning,
					$"{overWeighted:N0} vertex(es) are weighted to more than {maxInfluences} bones",
					$"The exporter keeps only the strongest {maxInfluences}, so those vertices will deform differently in game than they do here. Run Fix weights." ) );

			if ( offSum > 0 )
				findings.Add( new Finding( Severity.Warning,
					$"{offSum:N0} vertex(es) have weights that do not sum to 1",
					"They will deform too much or too little. Run Fix weights." ) );

			var unused = skeleton.Count - used.Count;

			if ( unused > 0 )
				findings.Add( new Finding( Severity.Note,
					$"{unused:N0} of {skeleton.Count:N0} bone(s) move nothing",
					"Normal for helper and attachment bones; worth a look if a limb is among them." ) );
		}

		// --- what the animation graph expects ------------------------------------------------

		if ( result.Unmapped is { Count: > 0 } )
			findings.Add( new Finding( Severity.Note,
				$"{result.Unmapped.Count} bone(s) have no citizen equivalent: {string.Join( ", ", result.Unmapped.Take( 6 ) )}{(result.Unmapped.Count > 6 ? ", ..." : "")}",
				"They ride along and animate with their parent. Fine for props and accessories." ) );

		if ( result.VerticesStranded > 0 )
			findings.Add( new Finding( Severity.Warning,
				$"{result.VerticesStranded:N0} vertex(es) were stranded when the skeleton was fitted",
				"They are weighted to a bone that citizen has no match for, so they will not follow the animation." ) );

		// --- scale and stance ----------------------------------------------------------------

		var bounds = MeshExtent( mesh );
		var height = bounds.Max.z - bounds.Min.z;

		// Citizen stands about 72 units - the engine's inches. A model far off that walks through
		// the floor or floats, because the animation's stride is authored at citizen's size.
		if ( height < 30f || height > 140f )
			findings.Add( new Finding( Severity.Warning,
				$"The model is {height:0.#} units tall; citizen is about 72",
				"Scale it to match, or the animations will not reach the ground properly." ) );

		if ( MathF.Abs( bounds.Min.z ) > MathF.Max( height * 0.05f, 1f ) )
			findings.Add( new Finding( Severity.Warning,
				$"The model's feet are at z = {bounds.Min.z:0.#}, not 0",
				"A playermodel's origin sits between its feet. Move it down so the lowest point is on z = 0, or it will float or sink." ) );

		// --- the mesh itself, only where it matters for a skinned model ----------------------

		var degenerate = 0;

		foreach ( var face in mesh.Faces )
			if ( mesh.FaceArea( face, mesh.FaceNormal( face ) ) <= 1e-9f )
				degenerate++;

		if ( degenerate > 0 )
			findings.Add( new Finding( Severity.Warning,
				$"{degenerate:N0} face(s) have no area",
				"They bake as holes and can break the normal calculation. Merge by distance, then delete what is left." ) );

		findings.Sort( ( a, b ) => b.Severity.CompareTo( a.Severity ) );
		return findings;
	}

	static (Vec3 Min, Vec3 Max) MeshExtent( PolyMesh mesh )
	{
		var min = mesh.Positions[0];
		var max = min;

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		return (min, max);
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
	public static string Vmdl( string meshFilename, Skeleton skeleton ) =>
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
		+ Prefabs( "AnimConstraintList", "citizen_animconstraintlist" )
		+ "\t\t\t{\n"
		+ "\t\t\t\t_class = \"AnimationList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ PrefabLine( "citizen_animationlist" )
		+ PrefabLine( "citizen_animationlist_unicycle" )
		+ PrefabLine( "citizen_animationlist_debug" )
		+ PrefabLine( "citizen_animationlist_visemes" )
		+ PrefabLine( "citizen_animationlist_menu" )
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
		+ Prefabs( "AttachmentList", "citizen_attachmentlist" )
		+ Prefabs( "PoseParamList", "citizen_poseparamlist" )
		+ Prefabs( "WeightListList", "citizen_weightlistlist" )
		+ Prefabs( "IKData", "citizen_ikdata" )
		+ Prefabs( "GameDataList", "citizen_gamedatalist" )
		+ Prefabs( "HitboxSetList", "citizen_hitboxsetlist" )
		+ Prefabs( "PhysicsJointList", "citizen_physicsjointlist" )
		+ Prefabs( "PhysicsShapeList", "citizen_physicsshapelist" )
		+ "\t\t]\n"
		+ "\t\tmodel_archetype = \"\"\n"
		+ "\t\tprimary_associated_entity = \"\"\n"
		+ "\t\tanim_graph_name = \"models/citizen/citizen.vanmgrph\"\n"
		+ "\t\tbase_model_name = \"\"\n"
		+ "\t}\n"
		+ "}\n";

	static string PrefabLine( string prefab ) =>
		$"\t\t\t\t\t{{ \"_class\" = \"Prefab\" target_file = \"models/citizen/prefabs/{prefab}.vmdl_prefab\" }},\n";

	static string Prefabs( string listClass, string prefab ) =>
		"\t\t\t{\n"
		+ $"\t\t\t\t_class = \"{listClass}\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ PrefabLine( prefab )
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n";

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
