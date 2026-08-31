using System;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Checks on the feature tree — the parametric spine.
///
/// The properties that matter here are not geometric, they are about history: that editing a
/// feature rebuilds what depends on it and reuses what does not, that rollback shows the model as
/// it was, and that a broken feature does not take the rest of the tree down with it. Those are
/// exactly the behaviours that quietly stop working as features are added, so they are asserted
/// rather than assumed.
/// </summary>
public static class FeatureTests
{
	public static void Run()
	{
		Section( "feature tree rebuilds" );
		TestBasicRebuild();

		Section( "rollback shows the model as it was" );
		TestRollback();

		Section( "incremental rebuild reuses upstream work" );
		TestIncremental();

		Section( "suppressed and failed features" );
		TestSuppressAndErrors();

		Section( "transforms" );
		TestXform();

		Section( "mirror reverses winding" );
		TestMirrorWinding();

		Section( "patterns" );
		TestPatterns();

		Section( "subdivide as a feature" );
		TestSubdivideFeature();

		Section( "parameter dialogs follow the shape" );
		TestParameterVisibility();

		Section( "parts-list name and hide survive a rebuild" );
		TestBodyPresentation();
	}

	static float Volume( PolyMesh m ) => m.SignedVolume();

	static PartStudio StudioWithBox( float size = 2f )
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.Shape.Index = 0;
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = size;
		return studio;
	}

	static void TestBasicRebuild()
	{
		var studio = StudioWithBox();
		var report = studio.Rebuild();

		Check( "one feature evaluated", report.FeaturesEvaluated == 1, report.ToString() );
		Check( "one body produced", studio.Bodies.Count == 1 );
		Check( "no errors", !report.HasErrors );
		Check( "body is a 6-face box", studio.Bodies[0].Mesh.FaceCount == 6 );
		Check( "feature got a default name", studio.Features[0].Name == "Box 1", studio.Features[0].Name );

		// Changing a parameter and rebuilding must change the geometry — the whole point.
		var box = (PrimitiveFeature)studio.Features[0];
		box.SizeX.Value = 4f;
		studio.MarkDirty( box );
		studio.Rebuild();

		var width = studio.Bodies[0].Mesh.Positions.Max( p => p.x ) - studio.Bodies[0].Mesh.Positions.Min( p => p.x );
		Check( "editing a parameter changes the model", MathF.Abs( width - 4f ) < 1e-4f, $"width {width}" );
	}

	static void TestRollback()
	{
		var studio = StudioWithBox();
		studio.Add( new PrimitiveFeature() ).Position.Value = new Vec3( 5, 0, 0 );
		studio.Add( new PrimitiveFeature() ).Position.Value = new Vec3( 10, 0, 0 );

		studio.Rebuild();
		Check( "three features give three bodies", studio.Bodies.Count == 3, $"{studio.Bodies.Count}" );

		studio.RollbackIndex = 2;
		studio.MarkAllDirty();
		studio.Rebuild();
		Check( "rollback to 2 gives two bodies", studio.Bodies.Count == 2, $"{studio.Bodies.Count}" );

		studio.RollbackIndex = 0;
		studio.MarkAllDirty();
		studio.Rebuild();
		Check( "rollback to 0 gives nothing", studio.Bodies.Count == 0, $"{studio.Bodies.Count}" );

		studio.RollbackIndex = int.MaxValue;
		studio.MarkAllDirty();
		studio.Rebuild();
		Check( "rolling forward restores everything", studio.Bodies.Count == 3, $"{studio.Bodies.Count}" );
	}

	static void TestIncremental()
	{
		var studio = new PartStudio();

		for ( var i = 0; i < 6; i++ )
			studio.Add( new PrimitiveFeature() ).Position.Value = new Vec3( i * 3, 0, 0 );

		var first = studio.Rebuild();
		Check( "first rebuild evaluates everything", first.FeaturesEvaluated == 6, first.ToString() );
		Check( "first rebuild reuses nothing", first.FeaturesReused == 0, first.ToString() );

		// Touch feature 4 (index 3). Features 0-2 are upstream and must be reused; 3-5 re-run.
		studio.MarkDirty( 3 );
		var second = studio.Rebuild();

		Check( "editing feature 4 reuses the first three", second.FeaturesReused == 3, second.ToString() );
		Check( "editing feature 4 re-runs three", second.FeaturesEvaluated == 3, second.ToString() );
		Check( "result is still six bodies", studio.Bodies.Count == 6, $"{studio.Bodies.Count}" );

		// A rebuild with nothing dirty should do no work at all.
		var third = studio.Rebuild();
		Check( "clean rebuild evaluates nothing", third.FeaturesEvaluated == 0, third.ToString() );
		Check( "clean rebuild still yields the model", studio.Bodies.Count == 6, $"{studio.Bodies.Count}" );

		// Body ids must not be reissued when a rebuild starts mid-tree, or selections break.
		var ids = studio.Bodies.Select( b => b.Id ).ToList();
		Check( "body ids stay unique", ids.Distinct().Count() == ids.Count, string.Join( ",", ids ) );

		// Reordering invalidates from the earlier position.
		studio.Move( 5, 1 );
		var fourth = studio.Rebuild();
		Check( "reorder re-runs from the earlier index", fourth.FeaturesEvaluated == 5, fourth.ToString() );
	}

	static void TestSuppressAndErrors()
	{
		var studio = StudioWithBox();
		var second = studio.Add( new PrimitiveFeature() );
		second.Position.Value = new Vec3( 5, 0, 0 );

		second.Suppressed = true;
		studio.MarkAllDirty();
		var report = studio.Rebuild();

		Check( "suppressed feature produces no body", studio.Bodies.Count == 1, $"{studio.Bodies.Count}" );
		Check( "suppressed feature counted", report.FeaturesSuppressed == 1, report.ToString() );

		second.Suppressed = false;
		studio.MarkAllDirty();
		studio.Rebuild();
		Check( "unsuppressing brings it back", studio.Bodies.Count == 2, $"{studio.Bodies.Count}" );

		// A broken feature must record an error and let the rest of the tree carry on, so an
		// upstream mistake does not cascade into every downstream feature also reporting failure.
		var bad = studio.Add( new PrimitiveFeature() );
		bad.Shape.Index = 4;             // Tube
		bad.InnerRadius.Value = 10f;     // larger than the outer radius
		bad.Radius.Value = 1f;

		var third = studio.Add( new PrimitiveFeature() );
		third.Position.Value = new Vec3( -5, 0, 0 );

		studio.MarkAllDirty();
		var withError = studio.Rebuild();

		Check( "broken feature reports an error", withError.HasErrors, withError.ToString() );
		Check( "error names the parameter", bad.Error is not null && bad.Error.Contains( "Inner radius" ), bad.Error );
		Check( "later features still run", studio.Bodies.Count == 3, $"{studio.Bodies.Count}" );

		// Fixing it clears the error.
		bad.InnerRadius.Value = 0.5f;
		studio.MarkAllDirty();
		var fixedReport = studio.Rebuild();

		Check( "fixing clears the error", !fixedReport.HasErrors, fixedReport.ToString() );
		Check( "and the body appears", studio.Bodies.Count == 4, $"{studio.Bodies.Count}" );
	}

	static void TestXform()
	{
		var p = new Vec3( 1, 2, 3 );

		var moved = Xform.Translate( new Vec3( 10, 0, 0 ) ).TransformPoint( p );
		Check( "translate", moved.AlmostEquals( new Vec3( 11, 2, 3 ) ), moved.ToString() );

		// 90 degrees about Z takes +X to +Y.
		var rotated = Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2f ).TransformPoint( new Vec3( 1, 0, 0 ) );
		Check( "rotate 90 about Z", rotated.AlmostEquals( new Vec3( 0, 1, 0 ) ), rotated.ToString() );

		// Rotation about an offset axis leaves the axis itself fixed.
		var about = Xform.RotateAbout( new Vec3( 5, 0, 0 ), new Vec3( 0, 0, 1 ), MathF.PI );
		var onAxis = about.TransformPoint( new Vec3( 5, 0, 2 ) );
		Check( "points on the axis stay put", onAxis.AlmostEquals( new Vec3( 5, 0, 2 ) ), onAxis.ToString() );

		var swung = about.TransformPoint( new Vec3( 6, 0, 0 ) );
		Check( "half turn about offset axis", swung.AlmostEquals( new Vec3( 4, 0, 0 ) ), swung.ToString() );

		// Determinant signs: rotation preserves handedness, mirror does not.
		Check( "rotation keeps handedness", !Xform.Rotate( new Vec3( 0, 0, 1 ), 1.1f ).FlipsWinding );
		Check( "mirror flips handedness", Xform.Mirror( Vec3.Zero, new Vec3( 1, 0, 0 ) ).FlipsWinding );
		Check( "negative scale flips handedness", Xform.Scale( new Vec3( -1, 1, 1 ) ).FlipsWinding );
		Check( "double negative scale does not", !Xform.Scale( new Vec3( -1, -1, 1 ) ).FlipsWinding );

		// Mirroring twice in the same plane is the identity.
		var m = Xform.Mirror( new Vec3( 2, 0, 0 ), new Vec3( 1, 0, 0 ) );
		var there = m.TransformPoint( new Vec3( 5, 1, 1 ) );
		var back = m.TransformPoint( there );
		Check( "mirror is its own inverse", back.AlmostEquals( new Vec3( 5, 1, 1 ) ), back.ToString() );
		Check( "mirror reflects across the plane", there.AlmostEquals( new Vec3( -1, 1, 1 ) ), there.ToString() );
	}

	static void TestMirrorWinding()
	{
		// The bug this guards against: a mirrored solid whose faces all point inward. It renders
		// black or lit from inside and looks fine in wireframe, so it survives visual review.
		var studio = StudioWithBox();
		var mirror = studio.Add( new MirrorFeature() );
		mirror.PlanePoint.Value = new Vec3( 5, 0, 0 );
		mirror.PlaneNormal.Value = new Vec3( 1, 0, 0 );

		studio.Rebuild();

		Check( "mirror adds a body", studio.Bodies.Count == 2, $"{studio.Bodies.Count}" );

		var originalVolume = Volume( studio.Bodies[0].Mesh );
		var mirroredVolume = Volume( studio.Bodies[1].Mesh );

		Check( "original volume positive", originalVolume > 0, $"{originalVolume:0.###}" );
		Check( "MIRRORED volume also positive", mirroredVolume > 0, $"{mirroredVolume:0.###}" );
		Check( "mirrored volume matches original",
			MathF.Abs( mirroredVolume - originalVolume ) < 1e-3f,
			$"{originalVolume:0.###} vs {mirroredVolume:0.###}" );

		// And it landed in the right place: reflected about x=5, a box centred at 0 goes to 10.
		var centre = studio.Bodies[1].Mesh.Positions.Aggregate( Vec3.Zero, ( a, b ) => a + b )
			/ studio.Bodies[1].Mesh.VertexCount;
		Check( "mirrored body is across the plane", MathF.Abs( centre.x - 10f ) < 1e-4f, centre.ToString() );

		// Still a valid closed solid afterwards.
		var v = MeshValidator.Validate( studio.Bodies[1].Mesh );
		Check( "mirrored body still closed and valid", v.IsValid && v.IsClosed, v.ToString() );

		mirror.KeepOriginal.Value = false;
		studio.MarkAllDirty();
		studio.Rebuild();
		Check( "keep-original off drops the source", studio.Bodies.Count == 1, $"{studio.Bodies.Count}" );
	}

	static void TestPatterns()
	{
		var studio = StudioWithBox( 1f );
		var linear = studio.Add( new LinearPatternFeature() );
		linear.Count.Value = 5;
		linear.Spacing.Value = 2f;
		linear.Direction.Value = new Vec3( 1, 0, 0 );

		studio.Rebuild();
		Check( "linear pattern of 5 gives 5 bodies", studio.Bodies.Count == 5, $"{studio.Bodies.Count}" );

		var xs = studio.Bodies
			.Select( b => b.Mesh.Positions.Aggregate( Vec3.Zero, ( a, p ) => a + p ).x / b.Mesh.VertexCount )
			.OrderBy( x => x ).ToList();

		Check( "instances are evenly spaced",
			MathF.Abs( xs[1] - xs[0] - 2f ) < 1e-4f && MathF.Abs( xs[4] - xs[0] - 8f ) < 1e-4f,
			string.Join( ", ", xs.Select( x => x.ToString( "0.##" ) ) ) );

		linear.Merge.Value = true;
		studio.MarkAllDirty();
		studio.Rebuild();
		Check( "merge collapses to one body", studio.Bodies.Count == 1, $"{studio.Bodies.Count}" );
		Check( "merged body has all the faces", studio.Bodies[0].Mesh.FaceCount == 30,
			$"{studio.Bodies[0].Mesh.FaceCount}" );

		// Circular, full turn: 4 instances 90 degrees apart, and none coincident with the first.
		var circ = new PartStudio();
		var b = circ.Add( new PrimitiveFeature() );
		b.SizeX.Value = b.SizeY.Value = b.SizeZ.Value = 1f;
		b.Position.Value = new Vec3( 5, 0, 0 );

		var pattern = circ.Add( new CircularPatternFeature() );
		pattern.Count.Value = 4;
		pattern.TotalAngle.Value = 360f;

		circ.Rebuild();
		Check( "circular pattern of 4 gives 4 bodies", circ.Bodies.Count == 4, $"{circ.Bodies.Count}" );

		var centres = circ.Bodies
			.Select( x => x.Mesh.Positions.Aggregate( Vec3.Zero, ( a, p ) => a + p ) / x.Mesh.VertexCount )
			.ToList();

		Check( "all instances sit on the same radius",
			centres.All( c => MathF.Abs( new Vec3( c.x, c.y, 0 ).Length - 5f ) < 1e-3f ),
			string.Join( " ", centres.Select( c => c.ToString() ) ) );

		Check( "a full turn does not duplicate the original",
			centres.All( c => centres.Count( o => o.AlmostEquals( c, 1e-3f ) ) == 1 ) );

		// A partial sweep spans the arc inclusive, so the last instance lands exactly on the angle.
		pattern.TotalAngle.Value = 90f;
		circ.MarkAllDirty();
		circ.Rebuild();

		var last = circ.Bodies.Last().Mesh.Positions.Aggregate( Vec3.Zero, ( a, p ) => a + p )
			/ circ.Bodies.Last().Mesh.VertexCount;

		Check( "90 degree sweep ends on the axis", MathF.Abs( last.y - 5f ) < 1e-3f, last.ToString() );

		// Circular merge shares the linear pattern's compounding trap — each copy must come from
		// the original mesh, not from the mesh as it stands after the previous append. Unguarded
		// this doubles instead of incrementing: 6, 12, 24, 48 faces rather than 6, 12, 18, 24.
		pattern.TotalAngle.Value = 360f;
		pattern.Merge.Value = true;
		circ.MarkAllDirty();
		circ.Rebuild();

		Check( "circular merge collapses to one body", circ.Bodies.Count == 1, $"{circ.Bodies.Count}" );
		Check( "circular merge has 4 boxes' worth of faces", circ.Bodies[0].Mesh.FaceCount == 24,
			$"{circ.Bodies[0].Mesh.FaceCount}" );
	}

	static void TestSubdivideFeature()
	{
		var studio = StudioWithBox();
		var subdiv = studio.Add( new SubdivideFeature() );
		subdiv.Levels.Value = 2;

		studio.Rebuild();

		Check( "subdivide feature ran", studio.Bodies[0].Mesh.FaceCount == 96,
			$"{studio.Bodies[0].Mesh.FaceCount}" );

		var predicted = subdiv.PredictCost( new[] { new Body( "x", "x", Primitives.Box( 2, 2, 2 ) ) } );
		Check( "cost prediction matches", predicted.Faces == 96, $"{predicted.Faces}" );

		// The cage has to stay reachable: roll back above the subdivide and the low-poly is there.
		// This is what the whole CAD-then-sculpt pipeline stands on.
		studio.RollbackIndex = 1;
		studio.MarkAllDirty();
		studio.Rebuild();

		Check( "rolling back exposes the cage again", studio.Bodies[0].Mesh.FaceCount == 6,
			$"{studio.Bodies[0].Mesh.FaceCount}" );

		// Editing the cage and rolling forward must give a different dense mesh, not a stale one.
		var box = (PrimitiveFeature)studio.Features[0];
		box.SizeX.Value = 10f;
		studio.RollbackIndex = int.MaxValue;
		studio.MarkAllDirty();
		studio.Rebuild();

		var width = studio.Bodies[0].Mesh.Positions.Max( p => p.x ) - studio.Bodies[0].Mesh.Positions.Min( p => p.x );
		Check( "editing the cage changes the subdivided result", width > 6f, $"width {width:0.##}" );
		Check( "and it is still dense", studio.Bodies[0].Mesh.FaceCount == 96,
			$"{studio.Bodies[0].Mesh.FaceCount}" );
	}

	static void TestParameterVisibility()
	{
		// Onshape's dialogs show only the fields the chosen shape has. Copying that behaviour is
		// most of why a tool with many features still feels simple.
		var f = new PrimitiveFeature();

		f.Shape.Index = 0; // Box
		var boxParams = f.Parameters.Select( p => p.Label ).ToList();
		Check( "box dialog shows three lengths",
			boxParams.Contains( "Width" ) && boxParams.Contains( "Depth" ) && boxParams.Contains( "Height" ) );
		Check( "box dialog hides radius", !boxParams.Contains( "Radius" ), string.Join( ", ", boxParams ) );

		f.Shape.Index = 1; // Cylinder
		var cylParams = f.Parameters.Select( p => p.Label ).ToList();
		Check( "cylinder dialog shows radius and segments",
			cylParams.Contains( "Radius" ) && cylParams.Contains( "Segments" ) );
		Check( "cylinder dialog hides width", !cylParams.Contains( "Width" ), string.Join( ", ", cylParams ) );

		f.Shape.Index = 4; // Tube
		Check( "tube dialog adds inner radius",
			f.Parameters.Any( p => p.Label == "Inner radius" ) );

		Check( "type name follows the shape", f.TypeName == "Tube", f.TypeName );
	}

	static void TestBodyPresentation()
	{
		var studio = StudioWithBox();
		studio.Features[0].Id = "box";
		studio.Features[0].Name = "Box 1";
		studio.Rebuild();

		Check( "a body starts with the feature's name",
			studio.Bodies[0].Name == "Box 1", studio.Bodies[0].Name );
		Check( "and is visible", studio.Bodies[0].Visible );

		var id = studio.Bodies[0].Id;

		studio.BodyNames[id] = "Housing";
		studio.HiddenBodyIds.Add( id );

		// No MarkDirty: the cache still has the default name and visible flag. The override has
		// to be reapplied on a reuse or a Parts-list rename would last until the next edit.
		studio.Rebuild();

		Check( "a renamed part keeps the name across a cached rebuild",
			studio.Bodies[0].Name == "Housing", studio.Bodies[0].Name );
		Check( "a hidden part stays hidden across a cached rebuild",
			!studio.Bodies[0].Visible );

		studio.MarkAllDirty();
		studio.Rebuild();

		Check( "and both survive a full rebuild",
			studio.Bodies[0].Name == "Housing" && !studio.Bodies[0].Visible,
			$"{studio.Bodies[0].Name} visible={studio.Bodies[0].Visible}" );

		studio.BodyNames.Remove( id );
		studio.HiddenBodyIds.Remove( id );
		studio.Rebuild();

		Check( "clearing the override restores the feature name",
			studio.Bodies[0].Name == "Box 1", studio.Bodies[0].Name );
		Check( "and showing it again draws it", studio.Bodies[0].Visible );
	}
}
