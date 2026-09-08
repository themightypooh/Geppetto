using System;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Datum planes: placing a plane yourself, and drawing on it.
///
/// THE TWO HALVES ARE THE FRAME AND THE REFERENCE, and they fail in completely different ways. The
/// frame is arithmetic — offset along a normal, hinge about an axis — and it is wrong loudly: the
/// sketch appears somewhere you can see is not where you asked. The reference is the parametric
/// half, and it is wrong QUIETLY: a plane that stops riding the face it was built from still
/// produces a perfectly good sketch, in the wrong place, on a rebuild nobody was watching. So most
/// of what is here is the second kind — change the thing underneath and check that everything on
/// top moved with it.
/// </summary>
public static class PlaneTests
{
	public static void Run()
	{
		Report.Section( "planes: tilting a frame about its own axis" );
		TestTiltedFrame();

		Report.Section( "planes: an offset plane off a global one" );
		TestOffsetFromGlobalPlane();

		Report.Section( "planes: an angled plane leans what is built on it" );
		TestAngledPlane();

		Report.Section( "planes: which way the offset travels" );
		TestOffsetAxis();

		Report.Section( "planes: a plane built from a face rides that face" );
		TestPlaneRidesItsFace();

		Report.Section( "planes: planes stack" );
		TestChainedPlanes();

		Report.Section( "planes: a sketch on a plane that came off a part stays in that part" );
		TestHostBodyTravels();

		Report.Section( "planes: a reference to a plane that is not running" );
		TestMissingPlaneFails();

		Report.Section( "planes: an incremental rebuild does not lose them" );
		TestPlaneSurvivesIncrementalRebuild();

		Report.Section( "planes: saved and reopened" );
		TestRoundTrip();
	}

	// --- the frame -------------------------------------------------------------------------

	static void TestTiltedFrame()
	{
		var plane = SketchPlane.XY;

		Report.Check( "no angle is no change",
			plane.Tilted( 0f, aboutY: false ).Normal.AlmostEquals( plane.Normal ) );

		foreach ( var aboutY in new[] { false, true } )
		{
			foreach ( var degrees in new[] { -90f, -30f, 15f, 45f, 90f } )
			{
				var tilted = plane.Tilted( degrees, aboutY );

				// Orthonormal or every sketch coordinate on it is skewed, which is the kind of
				// wrong that looks like a modelling mistake rather than a maths one.
				Report.Check( $"tilt {degrees} about {(aboutY ? "Y" : "X")} keeps the axes unit length",
					MathF.Abs( tilted.XAxis.Length - 1f ) < 1e-4f
					&& MathF.Abs( tilted.YAxis.Length - 1f ) < 1e-4f,
					$"{tilted.XAxis.Length} / {tilted.YAxis.Length}" );

				Report.Check( $"tilt {degrees} about {(aboutY ? "Y" : "X")} keeps the axes square",
					MathF.Abs( Vec3.Dot( tilted.XAxis, tilted.YAxis ) ) < 1e-4f,
					$"{Vec3.Dot( tilted.XAxis, tilted.YAxis )}" );

				// The normal moved by exactly the angle asked for.
				var between = MathF.Acos( Math.Clamp( Vec3.Dot( tilted.Normal, plane.Normal ), -1f, 1f ) )
					* 180f / MathF.PI;

				Report.Check( $"tilt {degrees} about {(aboutY ? "Y" : "X")} turns the normal that far",
					MathF.Abs( between - MathF.Abs( degrees ) ) < 1e-3f, $"{between}" );

				// The hinge itself does not move — that is what makes it a hinge.
				var hinge = aboutY ? plane.YAxis : plane.XAxis;
				var after = aboutY ? tilted.YAxis : tilted.XAxis;

				Report.Check( $"tilt {degrees} about {(aboutY ? "Y" : "X")} leaves the hinge axis alone",
					after.AlmostEquals( hinge ), $"{hinge} -> {after}" );

				Report.Check( $"tilt {degrees} about {(aboutY ? "Y" : "X")} pivots where it sits",
					tilted.Origin.AlmostEquals( plane.Origin ), tilted.Origin.ToString() );
			}
		}
	}

	// --- the reference ---------------------------------------------------------------------

	static void TestOffsetFromGlobalPlane()
	{
		var studio = new PartStudio();

		var plane = studio.Add( new PlaneFeature() );
		plane.Name = "Raised";
		plane.Offset.Value = 4f;

		var sketch = studio.Add( new SketchFeature() );
		sketch.PlaneFeatureId = plane.Id;
		sketch.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.SketchFeatureId = sketch.Id;
		extrude.Distance.Value = 2f;

		studio.Rebuild();

		Report.Check( "the plane itself builds and makes nothing",
			plane.Error is null && studio.Bodies.Count == 1, plane.Error ?? $"{studio.Bodies.Count} bodies" );

		var bounds = Bounds( studio );

		Report.Check( "a sketch on a plane 4 up extrudes from 4 up",
			MathF.Abs( bounds.MinZ - 4f ) < 1e-3f && MathF.Abs( bounds.MaxZ - 6f ) < 1e-3f,
			$"z {bounds.MinZ} to {bounds.MaxZ}" );

		// Moving the PLANE moves everything on it, with nothing downstream edited. This is the
		// whole reason a plane is a feature rather than a number typed into each sketch.
		plane.Offset.Value = 10f;
		studio.MarkDirty( plane );
		studio.Rebuild();

		bounds = Bounds( studio );

		Report.Check( "moving the plane carries the solid built on it",
			MathF.Abs( bounds.MinZ - 10f ) < 1e-3f, $"z {bounds.MinZ} to {bounds.MaxZ}" );
	}

	static void TestAngledPlane()
	{
		var studio = new PartStudio();

		var plane = studio.Add( new PlaneFeature() );
		plane.Angle.Value = 45f;
		plane.Hinge.Index = 0;      // its X axis, so the tilt happens in Y/Z

		var sketch = studio.Add( new SketchFeature() );
		sketch.PlaneFeatureId = plane.Id;
		sketch.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.SketchFeatureId = sketch.Id;
		extrude.Distance.Value = 4f;

		studio.Rebuild();

		Report.Check( "an angled plane builds", extrude.Error is null && studio.Bodies.Count == 1,
			extrude.Error ?? $"{studio.Bodies.Count} bodies" );

		// Pulled along a normal 45° off vertical, so the solid leans: the run in Y equals the rise
		// in Z. On the flat plane it would have had no Y extent past the profile at all.
		var bounds = Bounds( studio );
		var runY = bounds.MaxY - bounds.MinY;
		var runZ = bounds.MaxZ - bounds.MinZ;

		Report.Check( "the solid leans by the angle it was given",
			MathF.Abs( runY - runZ ) < 1e-2f && runZ > 2f, $"y {runY}, z {runZ}" );
	}

	/// <summary>
	/// Which direction Offset moves the plane in — the question the viewport's offset handle asks
	/// before it draws its arrow.
	///
	/// IT IS ONLY INTERESTING BECAUSE OF THE ORDER Execute does things in. Offset first, tilt second,
	/// about the origin the offset landed on: so a tilted plane FACES one way and MOVES another, and
	/// the two are the same direction only while the angle is zero. Everything an editor draws along
	/// "the plane's normal" is therefore right in the easy case and quietly wrong in the case the
	/// feature exists for.
	/// </summary>
	static void TestOffsetAxis()
	{
		var studio = new PartStudio();

		var flat = studio.Add( new PlaneFeature() );
		flat.Offset.Value = 3f;

		var leaning = studio.Add( new PlaneFeature() );
		leaning.Offset.Value = 3f;
		leaning.Angle.Value = 40f;
		leaning.Hinge.Index = 0;        // its X axis, so the lean happens in Y/Z

		studio.Rebuild();

		var flatPlane = studio.Planes[flat.Id];
		var leaningPlane = studio.Planes[leaning.Id];

		Report.Check( "a flat plane offsets the way it faces",
			flat.OffsetAxis( flatPlane ).AlmostEquals( flatPlane.Normal ),
			$"{flat.OffsetAxis( flatPlane )} vs {flatPlane.Normal}" );

		Report.Check( "a tilted one offsets the way it faced BEFORE the tilt",
			leaning.OffsetAxis( leaningPlane ).AlmostEquals( new Vec3( 0f, 0f, 1f ) ),
			$"{leaning.OffsetAxis( leaningPlane )}" );

		Report.Check( "which is not the way it faces now",
			!leaning.OffsetAxis( leaningPlane ).AlmostEquals( leaningPlane.Normal ),
			$"{leaningPlane.Normal}" );

		// The claim the handle makes, checked against the rebuild rather than against the arithmetic
		// that produced it: change Offset by five and the origin moves five along this axis and
		// nowhere else. An arrow drawn on it therefore stays under the cursor.
		var before = leaningPlane.Origin;

		leaning.Offset.Value = 8f;
		studio.MarkDirty( leaning );
		studio.Rebuild();

		var travelled = studio.Planes[leaning.Id].Origin - before;
		var expected = leaning.OffsetAxis( studio.Planes[leaning.Id] ) * 5f;

		Report.Check( "and the origin travels along it, five units for five",
			(travelled - expected).Length < 1e-3f, $"moved {travelled}, expected {expected}" );
	}

	static void TestPlaneRidesItsFace()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 4f;
		box.SizeZ.Value = 2f;

		studio.Rebuild();

		var plane = studio.Add( new PlaneFeature() );
		plane.Face = TopFaceOf( studio.Bodies[0] );
		plane.Offset.Value = 3f;

		var sketch = studio.Add( new SketchFeature() );
		sketch.PlaneFeatureId = plane.Id;
		sketch.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.SketchFeatureId = sketch.Id;
		extrude.Distance.Value = 1f;
		extrude.Result.Index = 1;   // New body, so the tab's own extent is readable on its own

		studio.Rebuild();

		Report.Check( "a plane on a face builds", plane.Error is null && extrude.Error is null,
			plane.Error ?? extrude.Error ?? "" );

		var tab = studio.Bodies.Last();

		// The box is centred, so its top face is at +1 and the plane sits 3 above that.
		Report.Check( "the tab starts 3 above the face the plane was taken from",
			MathF.Abs( MinZ( tab ) - 4f ) < 1e-3f, $"{MinZ( tab )}" );

		// THE POINT OF THE WHOLE TYPE. Make the box taller: the face moves, so the plane must move
		// with it, and so must everything drawn on the plane. A plane that stored the world height
		// it happened to be at when it was made would leave the tab buried in the taller box.
		box.SizeZ.Value = 6f;
		studio.MarkDirty( box );
		studio.Rebuild();

		tab = studio.Bodies.Last();

		Report.Check( "make the box taller and the plane rides its top face",
			MathF.Abs( MinZ( tab ) - 6f ) < 1e-3f, $"{MinZ( tab )}" );
	}

	static void TestChainedPlanes()
	{
		var studio = new PartStudio();

		var first = studio.Add( new PlaneFeature() );
		first.Offset.Value = 5f;

		var second = studio.Add( new PlaneFeature() );
		second.BasePlaneId = first.Id;
		second.Offset.Value = 5f;

		var sketch = studio.Add( new SketchFeature() );
		sketch.PlaneFeatureId = second.Id;
		sketch.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.SketchFeatureId = sketch.Id;
		extrude.Distance.Value = 1f;

		studio.Rebuild();

		Report.Check( "a plane built from a plane builds",
			second.Error is null && studio.Bodies.Count == 1, second.Error ?? "" );

		Report.Check( "and the offsets add up",
			MathF.Abs( MinZ( studio.Bodies[0] ) - 10f ) < 1e-3f, $"{MinZ( studio.Bodies[0] )}" );

		// One number at the bottom of the chain moves everything above it. This is what stacking is
		// for, and it is the thing three separately-measured planes cannot do.
		first.Offset.Value = 1f;
		studio.MarkDirty( first );
		studio.Rebuild();

		Report.Check( "editing the plane underneath moves the whole chain",
			MathF.Abs( MinZ( studio.Bodies[0] ) - 6f ) < 1e-3f, $"{MinZ( studio.Bodies[0] )}" );
	}

	static void TestHostBodyTravels()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 4f;
		box.SizeZ.Value = 2f;

		studio.Rebuild();

		var plane = studio.Add( new PlaneFeature() );
		plane.Face = TopFaceOf( studio.Bodies[0] );

		var sketch = studio.Add( new SketchFeature() );
		sketch.PlaneFeatureId = plane.Id;
		sketch.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

		// Result is Auto, which adds to the body the sketch grew out of. A plane in between must not
		// break that chain, or building through a datum plane silently starts a second part every
		// time.
		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.SketchFeatureId = sketch.Id;
		extrude.Distance.Value = 1f;

		studio.Rebuild();

		Report.Check( "a boss built through a plane off a face joins that part",
			studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );

		// And the opposite: a plane off a GLOBAL plane has no host, so Auto starts a new part.
		var loose = studio.Add( new PlaneFeature() );
		loose.Offset.Value = 8f;

		var apart = studio.Add( new SketchFeature() );
		apart.PlaneFeatureId = loose.Id;
		apart.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

		var second = studio.Add( new ExtrudeFeature() );
		second.SketchFeatureId = apart.Id;
		second.Distance.Value = 1f;

		studio.Rebuild();

		Report.Check( "a plane off a global plane starts its own part instead",
			studio.Bodies.Count == 2, $"{studio.Bodies.Count} bodies" );
	}

	static void TestMissingPlaneFails()
	{
		var studio = new PartStudio();

		var plane = studio.Add( new PlaneFeature() );
		plane.Offset.Value = 3f;

		var sketch = studio.Add( new SketchFeature() );
		sketch.PlaneFeatureId = plane.Id;
		sketch.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		studio.Rebuild();
		Report.Check( "the pair builds to start with", sketch.Error is null, sketch.Error ?? "" );

		// Suppressing the plane is the ordinary way to reach this: the sketch is standing on
		// something that is no longer running.
		plane.Suppressed = true;
		studio.MarkDirty( plane );
		studio.Rebuild();

		Report.Check( "a sketch on a suppressed plane fails rather than falling back to Top",
			sketch.Error is not null, "built anyway" );

		Report.Check( "and says what to do about it",
			sketch.Diagnostic is { Remedies.Count: > 0 } d && !string.IsNullOrEmpty( d.Cause ),
			sketch.Error ?? "" );

		// A plane BELOW the sketch has not published anything by the time the sketch runs, which is
		// what makes a cycle unrepresentable rather than something to detect.
		plane.Suppressed = false;
		studio.Move( studio.Features.IndexOf( plane ), studio.Features.Count - 1 );
		studio.Rebuild();

		Report.Check( "a plane below the sketch is not available to it",
			sketch.Error is not null, "built anyway" );
	}

	/// <summary>
	/// Editing something BELOW a plane reuses the plane from the cache rather than re-running it.
	/// The snapshot has to carry the published planes or the sketch above the edit finds nothing to
	/// stand on — a failure that appears on a rebuild which changed nothing the sketch could see,
	/// which is the worst kind to go looking for.
	/// </summary>
	static void TestPlaneSurvivesIncrementalRebuild()
	{
		var studio = new PartStudio();

		var plane = studio.Add( new PlaneFeature() );
		plane.Offset.Value = 3f;

		var sketch = studio.Add( new SketchFeature() );
		sketch.PlaneFeatureId = plane.Id;
		sketch.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.SketchFeatureId = sketch.Id;
		extrude.Distance.Value = 1f;

		var report = studio.Rebuild();
		Report.Check( "the first rebuild is clean", !report.HasErrors, report.ToString() );

		// Only the extrude is dirty, so the plane and the sketch above it come out of the cache.
		extrude.Distance.Value = 2f;
		studio.MarkDirty( extrude );
		report = studio.Rebuild();

		Report.Check( "reusing the cache keeps the plane available",
			!report.HasErrors && report.FeaturesReused > 0, report.ToString() );

		Report.Check( "and the solid is still on it",
			MathF.Abs( MinZ( studio.Bodies[0] ) - 3f ) < 1e-3f, $"{MinZ( studio.Bodies[0] )}" );
	}

	static void TestRoundTrip()
	{
		var studio = new PartStudio();

		var plane = studio.Add( new PlaneFeature() );
		plane.Name = "Roof line";
		plane.Base.Index = 1;
		plane.Offset.Value = 7.5f;
		plane.Angle.Value = 22.5f;
		plane.Hinge.Index = 1;

		var sketch = studio.Add( new SketchFeature() );
		sketch.PlaneFeatureId = plane.Id;
		sketch.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var back = StudioDocument.Read( StudioDocument.Write( studio ) );
		var reopened = back.Features.OfType<PlaneFeature>().Single();

		Report.Check( "a plane comes back with its numbers",
			reopened.Base.Index == 1 && reopened.Offset.Value == 7.5f
			&& reopened.Angle.Value == 22.5f && reopened.Hinge.Index == 1,
			$"{reopened.Base.Index} / {reopened.Offset.Value} / {reopened.Angle.Value} / {reopened.Hinge.Index}" );

		Report.Check( "and the sketch still knows which plane it is on",
			back.Features.OfType<SketchFeature>().Single().PlaneFeatureId == reopened.Id,
			back.Features.OfType<SketchFeature>().Single().PlaneFeatureId );

		// Both must still agree after a rebuild, which is what the id is for.
		back.Rebuild();

		Report.Check( "and the reopened document rebuilds clean",
			back.Features.All( f => f.Error is null ),
			back.Features.FirstOrDefault( f => f.Error is not null )?.Error ?? "" );
	}

	// --- helpers ---------------------------------------------------------------------------

	static FaceRef TopFaceOf( Body body )
	{
		var mesh = body.Mesh;

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( mesh.FaceNormal( mesh.Faces[i] ).Normal.z > 0.99f )
				return FacePlane.Capture( body, i, mesh.FaceCentroid( mesh.Faces[i] ) );
		}

		throw new InvalidOperationException( "no top face" );
	}

	static float MinZ( Body body ) => body.Mesh.Positions.Min( p => p.z );

	static (float MinY, float MaxY, float MinZ, float MaxZ) Bounds( PartStudio studio )
	{
		var points = studio.Bodies.SelectMany( b => b.Mesh.Positions ).ToList();

		return (points.Min( p => p.y ), points.Max( p => p.y ),
			points.Min( p => p.z ), points.Max( p => p.z ));
	}
}
