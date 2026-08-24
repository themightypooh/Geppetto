using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Extruding profiles with holes in them — a plate with bolt holes, a washer, a bracket.
///
/// This was refused for a long time on the reasoning that capping around a hole was "really the
/// same problem as a boolean subtract, and better solved once, there". That reasoning was wrong, and
/// wrong in a way worth remembering: capping is a 2D triangulation problem and never needed CSG at
/// all. Ear clipping had been sitting in the kernel for a while by the time anyone noticed.
///
/// The tests lean on two things a filled-in cap cannot fake. VOLUME, because a cap over the hole
/// adds exactly the hole's area times the height. And EULER CHARACTERISTIC, because a plate with n
/// holes is genus n and reads X = 2 - 2n, which nothing but a genuinely open hole produces — a small
/// enough hole could hide inside a volume tolerance, and it cannot hide from a vertex count.
/// </summary>
public static class HoleTests
{
	public static void Run()
	{
		Report.Section( "holes: a plate with four bolt holes" );
		TestBoltHoles();

		Report.Section( "holes: the walls face into the hole" );
		TestHoleWalls();

		Report.Section( "holes: awkward outers and awkward holes" );
		TestAwkward();

		Report.Section( "holes: a loop inside a hole is an island, not a hole" );
		TestIsland();

		Report.Section( "holes: profiles without them are untouched" );
		TestNoRegression();
	}

	static void TestBoltHoles()
	{
		// A 10x10 plate, 1 deep, with four r=0.5 holes near the corners.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );

		sketch.Sketch.AddRectangle( new Vec2( -5, -5 ), new Vec2( 5, 5 ) );

		var holes = new List<SketchCircle>();

		foreach ( var centre in new[] { (-3f, -3f), (3f, -3f), (3f, 3f), (-3f, 3f) } )
			holes.Add( sketch.Sketch.AddCircle( new Vec2( centre.Item1, centre.Item2 ), 0.5f ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 1f;

		var report = studio.Rebuild();

		Report.Check( "it builds", !report.HasErrors, report.ToString() );

		if ( report.HasErrors )
			return;

		var plate = studio.Bodies.Single().Mesh;

		var holeArea = holes.Sum( h => TessellatedArea( sketch.Sketch, h ) );
		var expected = (100f - holeArea) * 1f;

		Report.Check( "volume is the plate minus all four holes",
			MathF.Abs( Volume( plate ) - expected ) < 0.05f,
			$"{Volume( plate ):0.####}, expected {expected:0.####}" );

		// Genus 4: X = 2 - 2g = -6. This is the check a filled cap cannot survive.
		var x = MeshValidator.EulerCharacteristic( plate );

		Report.Check( "four holes make it genus 4, so X = -6", x == -6, $"X = {x}" );

		var validation = MeshValidator.Validate( plate );

		Report.Check( "the mesh is valid", validation.IsValid, validation.ToString() );
		Report.Check( "and closed", validation.IsClosed, $"{validation.BoundaryEdges} boundary edges" );

		// Positive volume is the winding check: an inside-out solid looks entirely normal in
		// wireframe and measures negative.
		Report.Check( "it winds outward", Volume( plate ) > 0f, $"{Volume( plate ):0.####}" );

		// Subdivision is where a bad cap shows up as a lumpy surface rather than a wrong number, so
		// the topology has to survive it even though a triangulated cap is not the ideal input.
		var subdivided = CatmullClark.Subdivide( plate, 1 );

		Report.Check( "it still subdivides to a valid mesh",
			MeshValidator.Validate( subdivided ).IsValid );

		Report.Check( "keeping its genus", MeshValidator.EulerCharacteristic( subdivided ) == -6,
			$"X = {MeshValidator.EulerCharacteristic( subdivided )}" );
	}

	static void TestHoleWalls()
	{
		// The wall of a hole faces INWARD — the material is outside it, so the outward-facing
		// surface normal points at the hole's axis. This falls out of ProfileFinder handing holes
		// back wound the opposite way to the outer loop, with no sign handling in the extrude at
		// all, which is exactly the kind of thing that is true by accident until someone checks.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( -4, -4 ), new Vec2( 4, 4 ) );
		sketch.Sketch.AddCircle( new Vec2( 0, 0 ), 1.5f );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		studio.Rebuild();

		var mesh = studio.Bodies.Single().Mesh;
		var wrong = 0;
		var checkedFaces = 0;

		foreach ( var face in mesh.Faces )
		{
			var normal = mesh.FaceNormal( face );

			// Side walls only: caps point along Z.
			if ( MathF.Abs( normal.z ) > 0.1f )
				continue;

			var centroid = mesh.FaceCentroid( face );
			var outward = new Vec3( centroid.x, centroid.y, 0f );

			// Inside the hole's radius means it is one of the hole's walls.
			if ( outward.Length > 2f )
				continue;

			checkedFaces++;

			// Pointing at the axis: the normal opposes the direction from the axis to the face.
			if ( Vec3.Dot( normal, outward.Normal ) > -0.5f )
				wrong++;
		}

		Report.Check( "the hole has walls", checkedFaces > 8, $"{checkedFaces} wall faces found" );

		Report.Check( "and every one of them faces into the hole", wrong == 0,
			$"{wrong} of {checkedFaces} faced outward" );

		// The outer wall must still face away, which is the other half of the same question.
		var outerWrong = mesh.Faces
			.Where( f => MathF.Abs( mesh.FaceNormal( f ).z ) < 0.1f )
			.Where( f => new Vec3( mesh.FaceCentroid( f ).x, mesh.FaceCentroid( f ).y, 0f ).Length > 2f )
			.Count( f => Vec3.Dot( mesh.FaceNormal( f ),
				new Vec3( mesh.FaceCentroid( f ).x, mesh.FaceCentroid( f ).y, 0f ).Normal ) < 0.5f );

		Report.Check( "while the outer walls still face outward", outerWrong == 0,
			$"{outerWrong} faced inward" );
	}

	static void TestAwkward()
	{
		// AN L-SHAPED PLATE WITH A HOLE IN THE SHORT ARM. The bridge from the outer loop to the hole
		// has to avoid the notch, which a naive "nearest vertex" bridge would cut straight across.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );

		sketch.Sketch.AddPolygon(
			new Vec2( 0, 0 ), new Vec2( 6, 0 ), new Vec2( 6, 2 ),
			new Vec2( 2, 2 ), new Vec2( 2, 6 ), new Vec2( 0, 6 ) );

		var hole = sketch.Sketch.AddCircle( new Vec2( 4.5f, 1f ), 0.5f );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		var report = studio.Rebuild();

		Report.Check( "a hole in a concave plate builds", !report.HasErrors, report.ToString() );

		if ( !report.HasErrors )
		{
			var mesh = studio.Bodies.Single().Mesh;

			// The L is 20 units of area: 6x2 plus 2x4.
			var expected = 20f - TessellatedArea( sketch.Sketch, hole );

			Report.Check( "with the L's area minus the hole",
				MathF.Abs( Volume( mesh ) - expected ) < 0.05f,
				$"{Volume( mesh ):0.####}, expected {expected:0.####}" );

			Report.Check( "and it is genus 1", MeshValidator.EulerCharacteristic( mesh ) == 0,
				$"X = {MeshValidator.EulerCharacteristic( mesh )}" );

			Report.Check( "closed and valid", MeshValidator.Validate( mesh ) is { IsValid: true, IsClosed: true } );
		}

		// A SQUARE HOLE, so the hole is not always the smooth case. Its corners are the vertices a
		// bridge is most likely to pick and most likely to graze.
		var square = new PartStudio();
		var ss = square.Add( new SketchFeature() );
		ss.Sketch.AddRectangle( new Vec2( -3, -3 ), new Vec2( 3, 3 ) );
		ss.Sketch.AddRectangle( new Vec2( -1, -1 ), new Vec2( 1, 1 ) );

		square.Add( new ExtrudeFeature() ).Distance.Value = 2f;
		var squareReport = square.Rebuild();

		Report.Check( "a square hole in a square plate builds", !squareReport.HasErrors, squareReport.ToString() );

		if ( !squareReport.HasErrors )
		{
			var mesh = square.Bodies.Single().Mesh;

			// 36 minus 4, times 2. A clean number, and the case where a filled cap would read 72.
			Report.Check( "with exactly the right volume",
				MathF.Abs( Volume( mesh ) - 64f ) < 1e-2f, $"{Volume( mesh ):0.####}, expected 64" );

			Report.Check( "and it is genus 1", MeshValidator.EulerCharacteristic( mesh ) == 0,
				$"X = {MeshValidator.EulerCharacteristic( mesh )}" );
		}

		// A hole close enough to the edge that the bridge is very short, which is where a
		// tolerance-based validity test would be tempted to accept a degenerate bridge.
		var tight = new PartStudio();
		var ts = tight.Add( new SketchFeature() );
		ts.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 4, 4 ) );
		ts.Sketch.AddCircle( new Vec2( 0.55f, 2f ), 0.5f );

		tight.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		var tightReport = tight.Rebuild();

		Report.Check( "a hole almost touching the edge still builds", !tightReport.HasErrors,
			tightReport.ToString() );

		if ( !tightReport.HasErrors )
		{
			Report.Check( "and is still genus 1",
				MeshValidator.EulerCharacteristic( tight.Bodies.Single().Mesh ) == 0 );
		}
	}

	static void TestIsland()
	{
		// A loop inside a hole is not a hole — it is solid again, and ProfileFinder already knows
		// that ("a loop inside an odd number of other loops is a hole"). So a ring with a disc in
		// the middle of it is TWO profiles and therefore two bodies, and the disc must not be
		// treated as a hole in the ring.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );

		sketch.Sketch.AddRectangle( new Vec2( -6, -6 ), new Vec2( 6, 6 ) );
		var middle = sketch.Sketch.AddCircle( new Vec2( 0, 0 ), 3f );
		var island = sketch.Sketch.AddCircle( new Vec2( 0, 0 ), 1f );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 1f;

		// Two separate solids from one sketch, so they stay separate rather than merging.
		extrude.Result.Index = 1;

		var report = studio.Rebuild();

		Report.Check( "it builds", !report.HasErrors, report.ToString() );

		if ( report.HasErrors )
			return;

		Report.Check( "a ring and an island make two bodies", studio.Bodies.Count == 2,
			$"{studio.Bodies.Count} bodies" );

		var total = studio.Bodies.Sum( b => Volume( b.Mesh ) );
		var ringArea = 144f - TessellatedArea( sketch.Sketch, middle );
		var islandArea = TessellatedArea( sketch.Sketch, island );

		Report.Check( "whose volumes are the ring and the disc",
			MathF.Abs( total - (ringArea + islandArea) ) < 0.05f,
			$"{total:0.####}, expected {ringArea + islandArea:0.####}" );

		// The ring is genus 1; the island is a plain disc at genus 0. Getting this backwards would
		// mean the island had been treated as a hole in the ring.
		var genus = studio.Bodies.Select( b => MeshValidator.EulerCharacteristic( b.Mesh ) ).OrderBy( v => v ).ToList();

		Report.Check( "the ring is genus 1 and the island genus 0",
			genus.SequenceEqual( new[] { 0, 2 } ), string.Join( ", ", genus ) );
	}

	static void TestNoRegression()
	{
		// The n-gon cap is a deliberate choice this kernel argues for at length — Catmull-Clark
		// turns one into n clean quads. Holed profiles cannot have one, and everything else still
		// must, so this pins the shape of an ordinary extrude against the change.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 3, 2 ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		studio.Rebuild();

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "a plain extrude is still 6 faces: four walls and two n-gon caps",
			mesh.FaceCount == 6, $"{mesh.FaceCount} faces" );

		Report.Check( "with the caps still whole quads, not triangulated",
			mesh.Faces.Count( f => f.Count == 4 ) == 6, string.Join( ", ", mesh.Faces.Select( f => f.Count ) ) );

		Report.Check( "and the volume unchanged at 6",
			MathF.Abs( Volume( mesh ) - 6f ) < 1e-3f, $"{Volume( mesh ):0.####}" );

		// A hexagon keeps its 6-gon caps too — the n-gon path is about any simple loop, not just
		// four-sided ones.
		var hex = new PartStudio();
		var hs = hex.Add( new SketchFeature() );
		var corners = new Vec2[6];

		for ( var i = 0; i < 6; i++ )
		{
			var angle = i * MathF.PI / 3f;
			corners[i] = new Vec2( MathF.Cos( angle ), MathF.Sin( angle ) );
		}

		hs.Sketch.AddPolygon( corners );
		hex.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		hex.Rebuild();

		var hexMesh = hex.Bodies.Single().Mesh;

		Report.Check( "a hexagonal profile still caps with two 6-gons",
			hexMesh.Faces.Count( f => f.Count == 6 ) == 2,
			string.Join( ", ", hexMesh.Faces.Select( f => f.Count ) ) );
	}

	// --- helpers ------------------------------------------------------------------------------

	static float TessellatedArea( Sketch sketch, SketchCurve curve )
	{
		var points = curve.Tessellate( sketch, sketch.Tolerance );
		var n = points.Count - 1;
		var sum = 0f;

		for ( var i = 0; i < n; i++ )
		{
			var a = points[i];
			var b = points[(i + 1) % n];
			sum += a.x * b.y - b.x * a.y;
		}

		return MathF.Abs( sum * 0.5f );
	}

	static float Volume( PolyMesh mesh )
	{
		var acc = 0f;

		foreach ( var f in mesh.Faces )
			acc += Vec3.Dot( mesh.FaceCentroid( f ), mesh.FaceNormal( f ) ) * mesh.FaceArea( f );

		return acc / 3f;
	}
}
