using System;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Sketching on the face of an existing body, and building on top of it.
///
/// This is the "boss on top of the block I just made" workflow, and it needs no boolean at all -
/// which is the useful finding from reading Solvespace and FreeCAD. Neither treats it as a
/// sketching mode; it is a derived plane, and the sketcher is untouched.
///
/// The half that matters most here is the REFERENCE surviving a rebuild. FreeCAD stores "Face6"
/// and that ordering moves when anything upstream changes; a FaceRef is a point and a normal, so
/// it is re-found geometrically and either matches or says it cannot.
/// </summary>
public static class FaceSketchTests
{
	public static void Run()
	{
		Report.Section( "face sketching: the derived plane itself" );
		TestPlaneFromNormal();

		Report.Section( "face sketching: a boss built on top of a box" );
		TestBossOnTopOfBox();

		Report.Section( "face sketching: the reference survives the box changing under it" );
		TestReferenceSurvivesUpstreamEdit();

		Report.Section( "face sketching: a reference that no longer matches anything" );
		TestLostFace();
	}

	static void TestPlaneFromNormal()
	{
		foreach ( var n in new[]
		{
			new Vec3( 0, 0, 1 ), new Vec3( 0, 0, -1 ), new Vec3( 1, 0, 0 ),
			new Vec3( 0, 1, 0 ), new Vec3( 1, 1, 1 ),
		} )
		{
			var plane = FacePlane.FromPointAndNormal( new Vec3( 1, 2, 3 ), n );

			Report.Check( $"plane from normal {n} has that normal",
				plane.Normal.AlmostEquals( n.Normal ), plane.Normal.ToString() );

			// Axes must be a proper orthonormal frame or sketch coordinates skew.
			var ortho = MathF.Abs( Vec3.Dot( plane.XAxis, plane.YAxis ) ) < 1e-4f
				&& MathF.Abs( plane.XAxis.Length - 1f ) < 1e-4f
				&& MathF.Abs( plane.YAxis.Length - 1f ) < 1e-4f;

			Report.Check( $"plane from normal {n} has an orthonormal frame", ortho );

			// Same input, same axes - a sketch must not spin on its own plane between rebuilds.
			var again = FacePlane.FromPointAndNormal( new Vec3( 1, 2, 3 ), n );

			Report.Check( $"plane from normal {n} is deterministic",
				again.XAxis.AlmostEquals( plane.XAxis ) && again.YAxis.AlmostEquals( plane.YAxis ) );
		}
	}

	/// <summary>Find the top face of the first body the way a click would: the face pointing up.</summary>
	static FaceRef TopFaceOf( PartStudio studio )
	{
		var mesh = studio.Bodies[0].Mesh;

		var top = mesh.Faces
			.Select( f => (Face: f, Normal: mesh.FaceNormal( f ), Centroid: mesh.FaceCentroid( f )) )
			.Where( t => t.Normal.z > 0.99f )
			.OrderByDescending( t => t.Centroid.z )
			.First();

		return new FaceRef( studio.Bodies[0].Id, top.Centroid, top.Normal );
	}

	static void TestBossOnTopOfBox()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 4f;
		box.SizeZ.Value = 2f;
		studio.Rebuild();

		var boxTopZ = studio.Bodies[0].Mesh.Positions.Max( p => p.z );

		// Sketch on the top face and extrude a smaller square up from it.
		var sketch = studio.Add( new SketchFeature() );
		sketch.Face = TopFaceOf( studio );
		sketch.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

		var boss = studio.Add( new ExtrudeFeature() );
		boss.Distance.Value = 1f;

		var report = studio.Rebuild();

		Report.Check( "it builds", !report.HasErrors, report.ToString() );
		Report.Check( "there are two bodies - the box and the boss", studio.Bodies.Count == 2,
			$"{studio.Bodies.Count}" );

		if ( studio.Bodies.Count != 2 )
			return;

		var bossMesh = studio.Bodies[1].Mesh;
		var bossLow = bossMesh.Positions.Min( p => p.z );
		var bossHigh = bossMesh.Positions.Max( p => p.z );

		Report.Check( "the boss starts exactly on the box's top face",
			MathF.Abs( bossLow - boxTopZ ) < 1e-3f, $"boss starts at {bossLow}, box top is {boxTopZ}" );

		Report.Check( "and stands 1 unit proud of it",
			MathF.Abs( bossHigh - boxTopZ - 1f ) < 1e-3f, $"{bossHigh - boxTopZ}" );
	}

	/// <summary>
	/// The point of storing geometry rather than an index: make the box taller and the sketch
	/// should follow the face up, without being re-picked.
	/// </summary>
	static void TestReferenceSurvivesUpstreamEdit()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 4f;
		box.SizeZ.Value = 2f;
		studio.Rebuild();

		var sketch = studio.Add( new SketchFeature() );
		sketch.Face = TopFaceOf( studio );
		sketch.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		studio.Rebuild();

		var firstBossLow = studio.Bodies[1].Mesh.Positions.Min( p => p.z );

		// Grow the box. Its top face moves; the sketch must move with it.
		box.SizeZ.Value = 6f;
		studio.MarkDirty( box );
		var report = studio.Rebuild();

		Report.Check( "it still builds after the box changed", !report.HasErrors, report.ToString() );

		if ( report.HasErrors || studio.Bodies.Count < 2 )
			return;

		var newBoxTop = studio.Bodies[0].Mesh.Positions.Max( p => p.z );
		var newBossLow = studio.Bodies[1].Mesh.Positions.Min( p => p.z );

		Report.Check( "the box did get taller", newBoxTop > firstBossLow + 0.5f,
			$"top now {newBoxTop}, boss used to sit at {firstBossLow}" );

		Report.Check( "and the boss moved up with the face it was drawn on",
			MathF.Abs( newBossLow - newBoxTop ) < 1e-3f,
			$"boss at {newBossLow}, face at {newBoxTop}" );
	}

	static void TestLostFace()
	{
		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature() );
		studio.Rebuild();

		var sketch = studio.Add( new SketchFeature() );

		// A reference into a body that does not exist. Scoping the reference to its body is what
		// makes this detectable at all - an unscoped point-and-normal would happily resolve onto
		// whatever else happened to be facing that way.
		sketch.Face = new FaceRef( "body_that_never_existed", new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ) );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );

		var report = studio.Rebuild();

		Report.Check( "a reference matching nothing is a clear error, not a silent fallback",
			report.HasErrors && sketch.Error is not null && sketch.Error.Contains( "gone" ),
			sketch.Error ?? "no error" );
	}
}
