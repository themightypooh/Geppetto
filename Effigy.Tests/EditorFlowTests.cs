using System;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// The editor's workflows, replayed headlessly against the real kernel.
///
/// WHY THIS EXISTS. The editor cannot be compiled outside s&box, so its logic used to be verified
/// by reading it — and reading it missed a bug that made every parameter edit a no-op, which in
/// turn looked like "sketches only work on the Top plane" and "the extrude controls do nothing".
/// Everything the editor does to the studio is ordinary kernel calls in a particular ORDER, and
/// that order is exactly what was wrong. This replays those sequences so the next one is caught
/// here rather than by hand at the far end of a compile.
///
/// These are not UI tests. They assert nothing about widgets. They assert that the sequence of
/// kernel calls an editor action performs produces the geometry the user was promised.
/// </summary>
public static class EditorFlowTests
{
	public static void Run()
	{
		Report.Section( "editor flow: sketch on a plane, then extrude it" );
		TestSketchThenExtrudeOnEveryPlane();

		Report.Section( "editor flow: changing a plane after the fact moves the solid" );
		TestChangingPlaneMovesTheSolid();

		Report.Section( "editor flow: undo restores parameter VALUES, not just the feature list" );
		TestUndoRestoresValues();

		Report.Section( "editor flow: the rollback bar" );
		TestRollback();

		Report.Section( "editor flow: the feature tree as a parametric tree" );
		TestTreeOperations();
	}

	// --- what the editor does, named the way the editor names it ---------------------------

	/// <summary>
	/// The editor's OnFeatureEdited: mark the edited feature dirty, then rebuild.
	///
	/// The mark is the whole thing. PartStudio re-runs only from the first dirty feature and
	/// Rebuild() clears the mark on its way out, so a rebuild with nothing marked reuses the entire
	/// cache and re-executes nothing at all.
	/// </summary>
	static RebuildReport EditAndRebuild( PartStudio studio, Feature edited )
	{
		studio.MarkDirty( edited );
		return studio.Rebuild();
	}

	static (Vec3 Min, Vec3 Max) Bounds( PolyMesh mesh )
	{
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		return (min, max);
	}

	// --- the flows --------------------------------------------------------------------------

	/// <summary>
	/// Press Sketch, pick a plane, draw a rectangle, finish, press Extrude, type a distance.
	/// Run for all three planes, because "it only works on Top" was the reported symptom.
	/// </summary>
	static void TestSketchThenExtrudeOnEveryPlane()
	{
		var planes = new[] { ("Top (XY)", 0), ("Front (XZ)", 1), ("Right (YZ)", 2) };

		foreach ( var (name, index) in planes )
		{
			var studio = new PartStudio();

			// Press Sketch. The feature is added before a plane is chosen, exactly as the toolbar
			// button does it.
			var sketch = studio.Add( new SketchFeature() );
			studio.Rebuild();

			// Pick a plane in the viewport. This is the edit that was being discarded.
			sketch.Plane.Index = index;
			EditAndRebuild( studio, sketch );

			var expected = index switch { 0 => SketchPlane.XY, 1 => SketchPlane.XZ, _ => SketchPlane.YZ };

			Check( $"{name}: the sketch actually moved to the chosen plane",
				sketch.Sketch.Plane.Normal.AlmostEquals( expected.Normal ),
				$"normal {sketch.Sketch.Plane.Normal}, expected {expected.Normal}" );

			// Draw a 2x3 rectangle, then press Extrude and type 4.
			sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 3 ) );
			EditAndRebuild( studio, sketch );

			var extrude = studio.Add( new ExtrudeFeature() );
			extrude.Distance.Value = 4f;
			var report = EditAndRebuild( studio, extrude );

			Check( $"{name}: it builds one body with no errors",
				!report.HasErrors && studio.Bodies.Count == 1,
				$"{studio.Bodies.Count} bodies, {report}" );

			if ( studio.Bodies.Count != 1 )
				continue;

			var (min, max) = Bounds( studio.Bodies[0].Mesh );
			var span = max - min;

			Check( $"{name}: it grew 4 along that plane's normal",
				MathF.Abs( MathF.Abs( Vec3.Dot( span, expected.Normal ) ) - 4f ) < 1e-3f,
				$"got {MathF.Abs( Vec3.Dot( span, expected.Normal ) )}" );
		}
	}

	/// <summary>
	/// The exact thing that looked broken: build on Top, then switch the sketch to Front and check
	/// the SOLID follows. Everything downstream has to re-run, not just the sketch.
	/// </summary>
	static void TestChangingPlaneMovesTheSolid()
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 3 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 4f;
		studio.Rebuild();

		var onTop = Bounds( studio.Bodies[0].Mesh );

		Check( "starts out growing along +Z",
			MathF.Abs( onTop.Max.z - onTop.Min.z - 4f ) < 1e-3f, $"{onTop.Max.z - onTop.Min.z}" );

		// Switch the sketch to Front. The extrude is DOWNSTREAM and must rebuild too.
		sketch.Plane.Index = 1;
		EditAndRebuild( studio, sketch );

		var onFront = Bounds( studio.Bodies[0].Mesh );

		Check( "after switching to Front, the solid grows along Y instead",
			MathF.Abs( onFront.Max.y - onFront.Min.y - 4f ) < 1e-3f, $"{onFront.Max.y - onFront.Min.y}" );

		Check( "and it is no longer 4 deep in Z",
			MathF.Abs( onFront.Max.z - onFront.Min.z - 3f ) < 1e-3f,
			$"{onFront.Max.z - onFront.Min.z}, expected the profile's own 3" );
	}

	/// <summary>
	/// Undo has to put NUMBERS back. Snapshotting the feature list alone keeps the same Feature
	/// objects, so it restores membership and order while every edited value survives untouched -
	/// which is undo appearing to do nothing at all.
	/// </summary>
	static void TestUndoRestoresValues()
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 1f;
		studio.Rebuild();

		// The editor's RecordUndo: capture every parameter's value, keyed by the parameter object.
		var snapshot = studio.Features
			.SelectMany( f => f.Parameters )
			.OfType<FloatParam>()
			.ToDictionary( p => p, p => p.Value );

		var listOnly = studio.Features.ToList();   // the old, broken snapshot

		extrude.Distance.Value = 9f;
		EditAndRebuild( studio, extrude );

		Check( "the edit took effect", MathF.Abs( extrude.Distance.Value - 9f ) < 1e-6f );

		// Restoring the LIST alone changes nothing, because the objects in it are the same ones.
		studio.Features = listOnly.ToList();
		studio.MarkAllDirty();
		studio.Rebuild();

		Check( "restoring only the feature list leaves the edited value behind",
			MathF.Abs( extrude.Distance.Value - 9f ) < 1e-6f, $"{extrude.Distance.Value}" );

		// Restoring values is what actually undoes it.
		foreach ( var (param, value) in snapshot )
			param.Value = value;

		studio.MarkAllDirty();
		studio.Rebuild();

		Check( "restoring parameter values undoes the edit",
			MathF.Abs( extrude.Distance.Value - 1f ) < 1e-6f, $"{extrude.Distance.Value}" );
	}

	/// <summary>Roll to here / roll to end, which is one int on the studio.</summary>
	static void TestRollback()
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		studio.Add( new SubdivideFeature() ).Levels.Value = 1;
		studio.Rebuild();

		var full = studio.Bodies[0].Mesh.FaceCount;

		// Roll to the Subdivide: everything from it down stops being evaluated.
		studio.RollbackIndex = 2;
		studio.Rebuild();

		var rolled = studio.Bodies[0].Mesh.FaceCount;

		Check( "rolling back past Subdivide leaves the un-subdivided body",
			rolled < full, $"{rolled} faces rolled back vs {full} at the end" );

		// And rolling forward puts it back exactly.
		studio.RollbackIndex = int.MaxValue;
		studio.Rebuild();

		Check( "rolling to the end restores the full result",
			studio.Bodies[0].Mesh.FaceCount == full, $"{studio.Bodies[0].Mesh.FaceCount} vs {full}" );
	}

	/// <summary>
	/// Reorder, suppress, delete, rename — the operations that make a feature list a parametric
	/// history rather than a list of labels. The reported complaint was that tree entries did not
	/// seem to be holding their data; these check the kernel side of every one of them.
	/// </summary>
	static void TestTreeOperations()
	{
		// --- suppress -------------------------------------------------------------------------
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		var subdivide = studio.Add( new SubdivideFeature() );
		subdivide.Levels.Value = 1;
		studio.Rebuild();

		var subdivided = studio.Bodies[0].Mesh.FaceCount;

		subdivide.Suppressed = true;
		EditAndRebuild( studio, subdivide );

		var suppressed = studio.Bodies[0].Mesh.FaceCount;

		Check( "suppressing a feature takes its effect out", suppressed < subdivided,
			$"{suppressed} vs {subdivided}" );

		subdivide.Suppressed = false;
		EditAndRebuild( studio, subdivide );

		Check( "un-suppressing puts it back exactly",
			studio.Bodies[0].Mesh.FaceCount == subdivided,
			$"{studio.Bodies[0].Mesh.FaceCount} vs {subdivided}" );

		// --- rename ---------------------------------------------------------------------------
		// Bodies take the name of the feature that made them, so a rename has to survive a rebuild.
		var extrude = studio.Features.OfType<ExtrudeFeature>().First();
		extrude.Name = "Boss";
		EditAndRebuild( studio, extrude );

		Check( "a renamed feature names the body it produces",
			studio.Bodies[0].Name == "Boss", studio.Bodies[0].Name );

		// --- reorder --------------------------------------------------------------------------
		// Subdivide BEFORE Extrude is a different model: it would subdivide nothing, then extrude.
		// Whatever it produces, the point is that reordering re-runs and changes the result.
		var before = studio.Bodies[0].Mesh.FaceCount;
		var subIndex = studio.Features.IndexOf( subdivide );
		var extIndex = studio.Features.IndexOf( extrude );

		studio.Move( subIndex, extIndex );
		var report = studio.Rebuild();

		Check( "reordering re-runs the tree rather than reusing the old cache",
			studio.Bodies.Count == 0 || studio.Bodies[0].Mesh.FaceCount != before || report.HasErrors,
			$"{studio.Bodies.Count} bodies, {studio.Bodies.FirstOrDefault()?.Mesh.FaceCount} faces, {report}" );

		// Put it back and confirm the original result returns - reorder has to be reversible or
		// dragging in the tree is a one-way trip.
		studio.Move( studio.Features.IndexOf( subdivide ), studio.Features.Count - 1 );
		studio.Rebuild();

		Check( "moving it back restores the original result",
			studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.FaceCount == before,
			$"{studio.Bodies.Count} bodies, {studio.Bodies.FirstOrDefault()?.Mesh.FaceCount} vs {before}" );

		// --- delete ---------------------------------------------------------------------------
		// Deleting the sketch has to break the extrude that consumes it, loudly.
		studio.Remove( sketch );
		var afterDelete = studio.Rebuild();

		Check( "deleting an upstream sketch makes its consumer report an error",
			afterDelete.HasErrors, afterDelete.ToString() );
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
