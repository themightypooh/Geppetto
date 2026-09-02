using Editor;
using Effigy;
using Sandbox;
using System.IO;

namespace Marionette.EditorTools;

/// <summary>
/// First look at the Effigy kernel (see ../WHAT-IS-BUILT.md) from inside the editor. Builds a
/// fixed demo body — a subdivided box — and drops it in the project as an OBJ wrapped in a
/// compiled .vmdl, so it's an actual placeable prop rather than just a browser preview, ahead of
/// real feature-tree editor integration (see ../WHAT-IS-LEFT.md).
/// </summary>
/// <remarks>
/// The .vmdl text is the real modeldoc29 RenderMeshFile node shape, copied out of Citizen's own
/// <c>citizen_rendermeshlist.vmdl_prefab</c> (Steam\...\sbox\addons\citizen\assets\models\citizen\
/// prefabs\) rather than guessed — that file ships as source where citizen.vmdl itself does not.
/// Trimmed to one folderless RenderMeshFile node; LODs, bodygroups and material markup are all
/// Citizen-specific and not needed for a single static mesh.
/// </remarks>
public static class EffigyTool
{
	[Menu( "Editor", "Marionette/Effigy: Export Demo Box", "view_in_ar" )]
	public static void ExportDemoBox()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 4f;
		box.SizeZ.Value = 4f;

		studio.Add( new SubdivideFeature() ).Levels.Value = 2;

		var report = studio.Rebuild();
		if ( report.HasErrors )
		{
			Log.Warning( $"[Effigy] rebuild had errors: {string.Join( "; ", report.Errors )}" );
			return;
		}

		var folder = KitConfig.ResolveAssetFolder( "models/effigy" );
		Directory.CreateDirectory( folder );

		var objPath = Path.Combine( folder, "demo_box.obj" );
		ObjWriter.WriteFile( studio.Bodies[0].Mesh, objPath, "demo_box" );

		var vmdlPath = Path.Combine( folder, "demo_box.vmdl" );
		File.WriteAllText( vmdlPath, BuildVmdl( "models/effigy/demo_box.obj" ) );

		var result = ExternalAssetTools.Register( folder );
		Log.Info( $"[Effigy] wrote {objPath} and {vmdlPath} - {result.Registered} registered, {result.AlreadyKnown} already known" );

		var asset = AssetSystem.FindByPath( "models/effigy/demo_box.vmdl" );
		if ( asset is null )
		{
			Log.Warning( "[Effigy] demo_box.vmdl was written but the asset system couldn't find it to compile - try re-running the menu command." );
			return;
		}

		asset.Compile( true );
		Log.Info( asset.IsCompileFailed
			? "[Effigy] demo_box.vmdl compile FAILED - check the compile log."
			: "[Effigy] demo_box.vmdl compiled - should be placeable in the scene now." );
	}

	static string BuildVmdl( string meshFilename ) =>
		"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n" +
		"{\n" +
		"\trootNode = \n" +
		"\t{\n" +
		"\t\t_class = \"RootNode\"\n" +
		"\t\tchildren = \n" +
		"\t\t[\n" +
		"\t\t\t{\n" +
		"\t\t\t\t_class = \"RenderMeshList\"\n" +
		"\t\t\t\tchildren = \n" +
		"\t\t\t\t[\n" +
		"\t\t\t\t\t{\n" +
		"\t\t\t\t\t\t_class = \"RenderMeshFile\"\n" +
		"\t\t\t\t\t\tname = \"Body_LOD0\"\n" +
		"\t\t\t\t\t\tchildren = \n" +
		"\t\t\t\t\t\t[\n" +
		"\t\t\t\t\t\t]\n" +
		$"\t\t\t\t\t\tfilename = \"{meshFilename}\"\n" +
		"\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n" +
		"\t\t\t\t\t\timport_rotation = [ -90.0, -90.0, 0.0 ]\n" +
		"\t\t\t\t\t\timport_scale = 1.0\n" +
		"\t\t\t\t\t\talign_origin_x_type = \"None\"\n" +
		"\t\t\t\t\t\talign_origin_y_type = \"None\"\n" +
		"\t\t\t\t\t\talign_origin_z_type = \"None\"\n" +
		"\t\t\t\t\t\tparent_bone = \"\"\n" +
		"\t\t\t\t\t},\n" +
		"\t\t\t\t]\n" +
		"\t\t\t},\n" +
		"\t\t]\n" +
		"\t\tmodel_archetype = \"\"\n" +
		"\t\tprimary_associated_entity = \"\"\n" +
		"\t\tanim_graph_name = \"\"\n" +
		"\t\tbase_model_name = \"\"\n" +
		"\t}\n" +
		"}\n";
}
