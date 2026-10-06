using System;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Render;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The tools an agent models with: the renderer draws what is there, the measurer says where
/// things are, the matcher scores a shape against a drawing, the script builds, the parts build,
/// and a fix leaves a clean mesh. Each is checked the way an agent would rely on it.
/// </summary>
public static class AgentToolsTests
{
	public static void Run()
	{
		Section( "agent tools: render, measure, match, script, parts, fix" );
		TestRenderCoversTheModelAndNothingElse();
		TestPngRoundTrip();
		TestDescribeAndLandmarks();
		TestMatchScoresItselfHighAndAStrangerLow();
		TestScriptBuildsWithLandmarks();
		TestScriptStopsWithTheFeaturesOwnDiagnostic();
		TestEveryPartIsABody();
		TestFixCleansAMesh();
	}

	static void TestRenderCoversTheModelAndNothingElse()
	{
		var box = new Body( "b", "Box", Primitives.Box( 10, 10, 10 ) );
		var mask = ModelRender.Silhouette( new[] { box }, RenderView.Front, 64 );
		var covered = mask.Count( m => m );

		// A cube seen face-on, fitted with no margin, fills the frame.
		Check( "a cube face-on fills the fitted frame", covered > 64 * 64 * 0.9f, $"{covered} of {64 * 64}" );

		var raster = ModelRender.Render( new[] { box }, RenderView.Iso, 96, new RenderOptions { Grid = false, Axes = false, Labels = false, Wireframe = false } );
		var lit = raster.Pixels.Count( p => p != 0x121418 );
		Check( "an iso render paints the cube and leaves the ground", lit > 500 && lit < 96 * 96 * 0.8f, $"{lit}" );
	}

	static void TestPngRoundTrip()
	{
		var raster = new Raster( 20, 12, 0x102030 );
		raster.Rect( 3, 3, 5, 4, 0xff8040 );
		raster.Text( 1, 8, "ok", 0xffffff, 1 );

		var bytes = raster.ToPng();
		var (w, h, rgb, alpha) = Raster.DecodePng( bytes );

		Check( "a PNG decodes to its own size", w == 20 && h == 12 );
		Check( "and its own pixels", rgb.SequenceEqual( raster.Pixels ) && alpha.All( a => a == 255 ) );
	}

	static void TestDescribeAndLandmarks()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.Name = "Torso";
		box.SizeX.Value = 10; box.SizeY.Value = 20; box.SizeZ.Value = 30;
		box.Position.Value = new Vec3( 0, 0, 15 );
		studio.Rebuild();

		var words = ModelMeasure.Describe( studio );
		Check( "describe names the body and its size", words.Contains( "Torso" ) && words.Contains( "10 x 20 x 30" ), words );
		Check( "describe says it stands on the ground", words.Contains( "Stands on the ground" ), words );

		Check( "Torso.top is the top centre", ModelMeasure.TryLandmark( studio, "Torso.top", out var top, out _ ) && MathF.Abs( top.z - 30f ) < 1e-3f && MathF.Abs( top.x ) < 1e-3f, $"{top.x},{top.y},{top.z}" );
		Check( "Torso.left is +y", ModelMeasure.TryLandmark( studio, "Torso.left", out var left, out _ ) && MathF.Abs( left.y - 10f ) < 1e-3f );
		Check( "an unknown landmark says what exists", !ModelMeasure.TryLandmark( studio, "Elbow", out _, out var why ) && why.Contains( "Torso" ), why );
		Check( "@landmark+offset resolves", BuildScript.Point( studio, "@Torso.top+0,0,5", out var above, out _ ) && MathF.Abs( above.z - 35f ) < 1e-3f );

		var at = ModelMeasure.At( studio, new Vec3( 0, 0, 15 ) );
		Check( "a point inside the box reads as inside", at.Contains( "inside Torso" ), at );
	}

	static void TestMatchScoresItselfHighAndAStrangerLow()
	{
		var tall = new Body( "t", "Tall", Primitives.Box( 4, 10, 30 ) );
		var wide = new Body( "w", "Wide", Primitives.Box( 4, 30, 10 ) );

		const int size = 128;
		var reference = ModelRender.Silhouette( new[] { tall }, RenderView.Front, size );
		var fitted = ReferenceMatch.Fit( reference, size, size, size, out var aspect );

		var same = ReferenceMatch.Compare( new[] { tall }, RenderView.Front, fitted, aspect, size );
		var other = ReferenceMatch.Compare( new[] { wide }, RenderView.Front, fitted, aspect, size );

		Check( "a shape matches itself", same.Overlap > 0.98f, $"{same.Overlap:0.00}" );
		Check( "a different shape scores lower and says the proportions differ", other.Overlap < same.Overlap && other.Report.Contains( "too wide" ), other.Report );
	}

	static void TestScriptBuildsWithLandmarks()
	{
		var studio = new PartStudio();
		var result = BuildScript.Run( studio, @"
add Primitive name=Torso shape=Box sizex=10 sizey=20 sizez=30 position=0,0,15
add Part name=Cog part=Cog size=4 thickness=1 count=8 length=1 position=@Torso.front+0.5,0,0 rotationaxis=0,1,0 rotation=90
add Spline name=Cable points=@Torso.back;-10,5,20;@Torso.top radius=0.5
add Profile name=Head front=0,3;4,5;9,4 position=@Torso.top
set Torso sizez=32
describe
" );

		Check( "a script with landmarks runs clean", result.Ok, result.ToString() );
		Check( "and builds every body", studio.Bodies.Count == 4, $"{studio.Bodies.Count} bodies" );

		var cog = ModelMeasure.FindBody( studio, "Cog" );
		Check( "the cog sits on the torso's front", cog is not null && ModelMeasure.Bounds( cog.Mesh ).Min.x >= 4.9f, cog is null ? "no cog" : $"{ModelMeasure.Bounds( cog.Mesh ).Min.x}" );

		// Landmarks resolve once, against the model as built when the line ran (see BuildScript), so
		// the head stays where Torso.top was at the time; the later `set` does not drag it along.
		var head = ModelMeasure.FindBody( studio, "Head" );
		Check( "the head sits where the torso's top was when it was added", head is not null && MathF.Abs( ModelMeasure.Bounds( head.Mesh ).Min.z - 30f ) < 0.1f, head is null ? "no head" : $"{ModelMeasure.Bounds( head.Mesh ).Min.z}" );

		var torso = ModelMeasure.FindBody( studio, "Torso" );
		Check( "and set resized the torso", torso is not null && MathF.Abs( ModelMeasure.Bounds( torso.Mesh ).Max.z - 31f ) < 0.1f, torso is null ? "no torso" : $"{ModelMeasure.Bounds( torso.Mesh ).Max.z}" );
	}

	static void TestScriptStopsWithTheFeaturesOwnDiagnostic()
	{
		var studio = new PartStudio();
		var result = BuildScript.Run( studio, "add Primitive name=A shape=Box\nadd CurveDeform bodies=A\nadd Primitive name=B shape=Box" );

		Check( "a failing feature stops the script", !result.Ok && result.ErrorLine == 2, result.ToString() );
		Check( "with the feature's cause and remedy", result.Error.Contains( "Because" ) && result.Error.Contains( "Try" ), result.Error );
		Check( "and nothing after it ran", studio.Bodies.Count( b => b.Name == "B" ) == 0 );

		var typo = BuildScript.Run( new PartStudio(), "add Primitive shpae=Box" );
		Check( "an unknown key lists the real ones", !typo.Ok && typo.Error.Contains( "Shape" ), typo.Error );
	}

	static void TestEveryPartIsABody()
	{
		foreach ( var name in PartLibrary.Names )
		{
			var mesh = PartLibrary.Build( name, 4f, 8f, 1f, 12, 90f );
			var (min, _) = ModelMeasure.Bounds( mesh );
			Check( $"{name} builds and stands on the ground", mesh.FaceCount > 0 && MathF.Abs( min.z ) < 1e-3f, $"{mesh.FaceCount} faces, min z {min.z}" );
		}

		foreach ( var lathed in new[] { "Bolt", "Hex bolt", "Rivet", "Knob", "Cog" } )
		{
			var check = MeshValidator.Validate( PartLibrary.Build( lathed, 4f, 8f, 1f, 12, 90f ) );
			Check( $"{lathed} is a closed manifold", check.IsClosed, $"boundary {check.BoundaryEdges}, non-manifold {check.NonManifoldEdges}" );
		}
	}

	static void TestFixCleansAMesh()
	{
		var mesh = Primitives.Box( 2, 2, 2 );
		mesh.Positions.Add( new Vec3( 50, 50, 50 ) );   // loose
		mesh.Faces.RemoveAt( 0 );                       // a hole

		var done = MeshFix.Apply( mesh, new[] { "all" } );
		var check = MeshValidator.Validate( mesh );

		Check( "fix all removes the loose vertex", mesh.VertexCount == 8, $"{mesh.VertexCount}" );
		Check( "and closes the hole", check.IsClosed, string.Join( " | ", done ) );
	}
}
