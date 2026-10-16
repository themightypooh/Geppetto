using System;
using System.IO;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Fix buttons in diagnostics: a feature that fails for a missing prerequisite attaches the fix as
/// data (<see cref="RemedyAction"/>), and applying it headlessly makes the failure go away.
/// </summary>
public static class RemedyActionTests
{
	const string Ball = "add Primitive name=Sac shape=Sphere radius=10 divisions=10\n";

	// Edges about 2.4 long: a 0.3-radius brush in the middle of a face touches no vertex.
	const string Coarse = "add Primitive name=Sac shape=Sphere radius=10 divisions=6\n";

	public static void Run()
	{
		Section( "remedy actions: the text form round-trips" );
		TestTextRoundTrip();

		Section( "remedy actions: a stroke finer than the mesh offers Subdivide, and it works" );
		TestStrokeSubdivide();

		Section( "remedy actions: surface detail finer than the mesh offers Subdivide, and it works" );
		TestDetailSubdivide();

		Section( "remedy actions: a bake that had to unwrap offers Unwrap above, and it works" );
		TestFleshUnwrap();

		Section( "remedy actions: a bake with no folder offers Choose folder, and it works" );
		TestFleshFolder();

		Section( "remedy actions: a garment with no rig offers the Rig workspace" );
		TestGarmentRig();

		Section( "remedy actions: the script verb" );
		TestScriptVerb();
	}

	static (PartStudio Studio, BuildScript.Result Result) Script( string text, PartStudio studio = null )
	{
		studio ??= new PartStudio();
		return (studio, BuildScript.Run( studio, text ));
	}

	/// <summary>The coarse ball and a short, small stroke across the middle of its biggest face, so no
	/// vertex is under the brush.</summary>
	static (PartStudio Studio, BuildScript.Result Result) CoarseStroke()
	{
		var (studio, _) = Script( Coarse );
		var mesh = studio.Bodies[0].Mesh;
		Vec3 Centre( Face f ) => f.Indices.Aggregate( Vec3.Zero, ( a, i ) => a + mesh.Positions[i] ) * (1f / f.Indices.Length);
		float Clearance( Face f ) => f.Indices.Min( i => (mesh.Positions[i] - Centre( f )).Length );
		var face = mesh.Faces.OrderByDescending( Clearance ).First();
		var c = Centre( face );
		var along = (mesh.Positions[face.Indices[1]] - mesh.Positions[face.Indices[0]]).Normal * 0.1f;
		string P( Vec3 v ) => FormattableString.Invariant( $"{v.x},{v.y},{v.z}" );

		return Script( $"add Stroke name=Crease bodies=Sac brush=Draw radius=0.3 strength=1 points={P( c - along )};{P( c + along )}\n", studio );
	}

	static void TestTextRoundTrip()
	{
		var a = RemedyAction.InsertFeatureAbove( "Unwrap above | now", "UVProject", new[] { "b1", "b 2" }, ("mode", "Organic"), ("chart angle", "45") );
		var back = RemedyAction.Parse( a.ToText() );

		Check( "insert-above survives text: kind, label, type, bodies and params",
			back.Kind == RemedyKind.InsertFeatureAbove && back.Label == a.Label && back.FeatureType == "UVProject"
			&& back.BodyIds.SequenceEqual( a.BodyIds ) && back.Params["mode"] == "Organic" && back.Params["chart angle"] == "45"
			&& back.ToText() == a.ToText(), a.ToText() );

		var f = RemedyAction.Parse( RemedyAction.ChooseFolder( "Choose folder", "Folder" ).ToText() );
		Check( "choose-folder survives text", f.Kind == RemedyKind.ChooseFolder && f.ParameterLabel == "Folder" && f.Label == "Choose folder" );

		var w = RemedyAction.Parse( RemedyAction.OpenWorkspace( "Open Rig", "Rig" ).ToText() );
		Check( "open-workspace survives text", w.Kind == RemedyKind.OpenWorkspace && w.Workspace == "Rig" );

		var refused = false;
		try { RemedyAction.Parse( "label=x" ); } catch ( FormatException ) { refused = true; }
		Check( "text with no kind is refused, not guessed", refused );
	}

	static void TestStrokeSubdivide()
	{
		var (studio, r) = CoarseStroke();
		var stroke = studio.Features.OfType<StrokeFeature>().Single();
		var action = stroke.Diagnostic?.Actions.FirstOrDefault();

		Check( "a brush smaller than the edges fails", stroke.Error is not null, r.ToString() );
		Check( "and offers Subdivide above on that body",
			action is { Kind: RemedyKind.InsertFeatureAbove, FeatureType: "Subdivide" } && action.BodyIds.SequenceEqual( new[] { studio.Bodies[0].Id } ),
			action?.ToText() ?? "no action" );
		Check( "the action reads back from its text", action is not null && RemedyAction.Parse( action.ToText() ).ToText() == action.ToText() );

		var before = studio.Features.IndexOf( stroke );
		var outcome = FeatureRemedy.Apply( studio, stroke, action );
		studio.Rebuild();

		Check( "applying it puts a Subdivide directly above the stroke",
			outcome.Ok && outcome.Inserted is SubdivideFeature && studio.Features.IndexOf( outcome.Inserted ) == before && studio.Features.IndexOf( stroke ) == before + 1,
			outcome.Message );
		Check( "and the stroke builds with no diagnostic", stroke.Error is null && stroke.Diagnostic is null, stroke.Diagnostic?.Problem );

		var subdivided = new PartStudio();
		Script( Coarse + $"add Subdivide bodies=Sac levels={action.Params["levels"]}\n", subdivided );
		var moved = studio.Bodies[0].Mesh.Positions.Zip( subdivided.Bodies[0].Mesh.Positions ).Max( p => (p.First - p.Second).Length );
		Check( "the stroke now moves the skin", moved > 0.05f, $"moved {moved:0.###}" );

		var reread = StudioDocument.Read( StudioDocument.Write( studio ) );
		reread.Rebuild();
		Check( "the fix is ordinary history: it saves and the stroke still builds",
			reread.Features.OfType<SubdivideFeature>().Count() == 1 && reread.Features.OfType<StrokeFeature>().Single().Diagnostic is null );
	}

	static void TestDetailSubdivide()
	{
		var (studio, r) = Script( Ball + "add Detail name=Pox bodies=Sac pass=Pustules size=1 depth=0.3\n" );
		var detail = studio.Features.OfType<DetailFeature>().Single();
		var action = detail.Diagnostic?.Actions.FirstOrDefault();

		Check( "detail finer than the edges is diagnosed", detail.Diagnostic is not null, r.ToString() );
		Check( "with Subdivide above as the fix", action is { Kind: RemedyKind.InsertFeatureAbove, FeatureType: "Subdivide" }, action?.ToText() ?? "no action" );

		var outcome = FeatureRemedy.Apply( studio, detail, action );
		studio.Rebuild();

		Check( "applying it clears the diagnostic", outcome.Ok && detail.Diagnostic is null && detail.Error is null,
			$"{outcome.Message} | {detail.Diagnostic?.Problem}: {detail.Diagnostic?.Cause}" );
		Check( "and the mesh is now fine enough for the detail",
			StrokeFeature.MeanEdge( studio.Bodies[0].Mesh ) <= 0.5f, $"{StrokeFeature.MeanEdge( studio.Bodies[0].Mesh ):0.###}" );
	}

	static void TestFleshUnwrap()
	{
		var folder = Path.Combine( Path.GetTempPath(), "effigy-remedy-" + Guid.NewGuid().ToString( "N" ) );

		try
		{
			// A blob is a voxel skin with no UVs, so the bake has to unwrap it itself.
			var (studio, r) = Script( "add Blob name=B shapes=\"sphere 0,0,0 4\" voxel=0.5\n"
				+ $"add FleshMaterial name=Meat bodies=B preset=raw_meat resolution=64 folder={folder}\n" );
			var flesh = studio.Features.OfType<FleshMaterialFeature>().Single();
			var action = flesh.Diagnostic?.Actions.FirstOrDefault();

			Check( "a bake on a body with no UVs says it unwrapped", flesh.Warning == "Unwrapped first", $"{flesh.Warning} | {r}" );
			Check( "and offers Unwrap above (UV project, Organic) on that body",
				action is { Kind: RemedyKind.InsertFeatureAbove, FeatureType: "UVProject" } && action.Params["mode"] == "Organic"
				&& action.BodyIds.SequenceEqual( new[] { studio.Bodies[0].Id } ), action?.ToText() ?? "no action" );

			var outcome = FeatureRemedy.Apply( studio, flesh, action );
			studio.Rebuild();

			Check( "applying it inserts a UV project in Organic mode",
				outcome.Inserted is UVProjectFeature { Mode.Value: "Organic" } && studio.Features.IndexOf( outcome.Inserted ) == studio.Features.IndexOf( flesh ) - 1,
				outcome.Message );
			Check( "and the bake no longer has to unwrap", flesh.Diagnostic is null && flesh.Warning is null, flesh.Warning );
		}
		finally
		{
			if ( Directory.Exists( folder ) ) Directory.Delete( folder, true );
		}
	}

	static void TestFleshFolder()
	{
		var folder = Path.Combine( Path.GetTempPath(), "effigy-remedy-" + Guid.NewGuid().ToString( "N" ) );

		try
		{
			var (studio, r) = Script( Ball + "add FleshMaterial name=Meat bodies=Sac preset=raw_meat resolution=64\n" );
			var flesh = studio.Features.OfType<FleshMaterialFeature>().Single();
			var action = flesh.Diagnostic?.Actions.FirstOrDefault();

			Check( "a bake with no folder says nothing is written", flesh.Warning == "No texture files written", $"{flesh.Warning} | {r}" );
			Check( "and offers Choose folder on Folder", action is { Kind: RemedyKind.ChooseFolder, ParameterLabel: "Folder" }, action?.ToText() ?? "no action" );

			var refused = FeatureRemedy.Apply( studio, flesh, action, folder: "  " );
			Check( "cancelling the folder picker changes nothing and says so", !refused.Ok && string.IsNullOrEmpty( flesh.Folder.Value ), refused.Message );

			var outcome = FeatureRemedy.Apply( studio, flesh, action, folder );
			studio.Rebuild();

			Check( "choosing one sets Folder and clears the warning", outcome.Ok && flesh.Folder.Value == folder && flesh.Diagnostic is null, outcome.Message );
			Check( "and the files are written there", flesh.Written.Count > 0 && flesh.Written.All( File.Exists ), string.Join( ", ", flesh.Written ) );
		}
		finally
		{
			if ( Directory.Exists( folder ) ) Directory.Delete( folder, true );
		}
	}

	static void TestGarmentRig()
	{
		var (studio, _) = Script( Ball + "add Garment\n" );
		var garment = studio.Features.Last();
		var action = garment.Diagnostic?.Actions.FirstOrDefault();

		Check( "a garment with no rig fails and offers the Rig workspace",
			garment.Error is not null && action is { Kind: RemedyKind.OpenWorkspace, Workspace: "Rig" }, $"{garment.Error} | {action?.ToText()}" );

		var outcome = FeatureRemedy.Apply( studio, garment, action );
		Check( "a script cannot open a workspace, and says so rather than doing nothing", !outcome.Ok && outcome.Message.Contains( "Rig" ), outcome.Message );
	}

	static void TestScriptVerb()
	{
		var (studio, r) = CoarseStroke();
		Check( "(setup) the stroke fails", !r.Ok );

		var fixedRun = BuildScript.Run( studio, "remedy Crease\n" );
		Check( "remedy Crease presses the fix and reports the stroke builds",
			fixedRun.Ok && studio.Features.OfType<SubdivideFeature>().Any() && fixedRun.Log.Any( l => l.Contains( "builds cleanly" ) ), fixedRun.ToString() );

		var none = BuildScript.Run( studio, "remedy Crease\n" );
		Check( "remedy on a feature with nothing wrong says so", !none.Ok && none.Error.Contains( "no problem" ), none.ToString() );
	}
}
