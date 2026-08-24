using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Checks on the sketch layer and the features that consume it.
///
/// The interesting failures here are orientation ones. A prism built from a clockwise loop, or a
/// revolve whose rings wind the wrong way, produces a solid that looks perfectly normal in
/// wireframe and renders inside-out. Every solid-producing test therefore asserts on enclosed
/// volume, which is signed and catches exactly that.
/// </summary>
public static class SketchTests
{
	public static void Run()
	{
		Section( "sketch planes" );
		TestPlanes();

		Section( "arc tessellation" );
		TestArcs();

		Section( "finding closed regions" );
		TestProfiles();

		Section( "extrude" );
		TestExtrude();

		Section( "revolve" );
		TestRevolve();

		Section( "sketches in the feature tree" );
		TestSketchInTree();

		Section( "picking one region out of a sketch" );
		TestRegionSelection();

		Section( "each sketch plane builds where it should" );
		TestPlaneOrientation();

		Section( "editing a parameter actually re-runs the feature" );
		TestIncrementalRebuildSeesEdits();
	}

	/// <summary>
	/// A rectangle sketched on each of the three planes, extruded, and checked for where the solid
	/// actually landed.
	///
	/// The symptom this pins: sketches appearing to work only on Top, with Front and Right
	/// behaving as though they were Top. That turned out to be an editor bug rather than a kernel
	/// one - the plane change never reached the feature - but nothing here asserted the kernel's
	/// half of it either, so a real regression in SketchPlane would have looked identical.
	///
	/// Expected, from SketchPlane's own axes:
	///   XY  X=(1,0,0) Y=(0,1,0) N=(0,0,1)  -> u along world x, v along world y, grows +z
	///   XZ  X=(1,0,0) Y=(0,0,1) N=(0,-1,0) -> u along world x, v along world z, grows -y
	///   YZ  X=(0,1,0) Y=(0,0,1) N=(1,0,0)  -> u along world y, v along world z, grows +x
	/// </summary>
	static void TestPlaneOrientation()
	{
		var cases = new[]
		{
			("Top (XY)", 0, new Vec3( 0, 0, 1 )),
			("Front (XZ)", 1, new Vec3( 0, -1, 0 )),
			("Right (YZ)", 2, new Vec3( 1, 0, 0 )),
		};

		foreach ( var (name, index, normal) in cases )
		{
			var studio = new PartStudio();
			var sketch = studio.Add( new SketchFeature() );
			sketch.Plane.Index = index;

			// 2 wide in the plane's u, 3 in its v.
			sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 3 ) );

			var extrude = studio.Add( new ExtrudeFeature() );
			extrude.Distance.Value = 1f;

			var report = studio.Rebuild();

			Check( $"{name} builds", !report.HasErrors && studio.Bodies.Count == 1, report.ToString() );

			if ( studio.Bodies.Count != 1 )
				continue;

			var (min, max) = Bounds( studio.Bodies[0].Mesh );
			var plane = index switch { 0 => SketchPlane.XY, 1 => SketchPlane.XZ, _ => SketchPlane.YZ };

			// The extrusion has to run along the plane's normal and nowhere else.
			var span = max - min;
			var along = MathF.Abs( Vec3.Dot( span, normal ) );

			Check( $"{name} extrudes 1 unit along its own normal",
				MathF.Abs( along - 1f ) < 1e-3f, $"got {along}" );

			// And the profile has to keep its 2x3 proportions in the plane's own axes.
			var u = MathF.Abs( Vec3.Dot( span, plane.XAxis ) );
			var v = MathF.Abs( Vec3.Dot( span, plane.YAxis ) );

			Check( $"{name} keeps the profile 2 x 3 in plane axes",
				MathF.Abs( u - 2f ) < 1e-3f && MathF.Abs( v - 3f ) < 1e-3f, $"got {u} x {v}" );
		}
	}

	/// <summary>
	/// The contract the editor leans on: change a parameter, mark the feature dirty, rebuild, and
	/// the change is actually reflected.
	///
	/// PartStudio only re-runs from the first dirty feature and Rebuild() ends by clearing the
	/// dirty mark, so a rebuild with nothing marked reuses the entire cache and re-executes
	/// nothing. The editor was doing exactly that on every parameter edit, which silently threw
	/// away every change made through a feature dialog.
	/// </summary>
	static void TestIncrementalRebuildSeesEdits()
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 1f;
		studio.Rebuild();

		var before = Volume( studio.Bodies[0].Mesh );

		// Edit WITHOUT marking dirty - the stale path the editor was taking.
		extrude.Distance.Value = 5f;
		studio.Rebuild();

		Check( "an unmarked edit is NOT picked up (documents why MarkDirty is required)",
			MathF.Abs( Volume( studio.Bodies[0].Mesh ) - before ) < 1e-3f,
			$"{Volume( studio.Bodies[0].Mesh )} vs {before}" );

		// Now the correct path.
		studio.MarkDirty( extrude );
		studio.Rebuild();

		Check( "a marked edit IS picked up",
			MathF.Abs( Volume( studio.Bodies[0].Mesh ) - before * 5f ) < 1e-2f,
			$"{Volume( studio.Bodies[0].Mesh )}, expected {before * 5f}" );
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

	/// <summary>
	/// A sketch with two separate rectangles in it, and an Extrude pointed at one of them.
	///
	/// This is what the viewport's face picking rests on: RegionSeed is a POINT inside the wanted
	/// region rather than an index into the profile list, because profiles are re-found from the
	/// curve graph every rebuild and carry no stable order. These checks are the ones that would
	/// catch that going wrong - a seed selecting the wrong region, or silently selecting all of
	/// them.
	/// </summary>
	static void TestRegionSelection()
	{
		static PartStudio TwoSquares( out ExtrudeFeature extrude )
		{
			var studio = new PartStudio();
			var sketch = studio.Add( new SketchFeature() );

			// 2x2 at the origin, and a 1x1 well clear of it.
			sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );
			sketch.Sketch.AddRectangle( new Vec2( 5, 5 ), new Vec2( 6, 6 ) );

			extrude = studio.Add( new ExtrudeFeature() );
			extrude.Distance.Value = 1f;

			return studio;
		}

		// No seed - every region, which is the behaviour that existed before faces were pickable
		// and has to keep working.
		var studio = TwoSquares( out var all );
		var report = studio.Rebuild();

		Check( "no seed extrudes every region", studio.Bodies.Count == 2 && !report.HasErrors,
			$"{studio.Bodies.Count} bodies, {report}" );

		// Seeded inside the big square.
		studio = TwoSquares( out var big );
		big.RegionSeed = new Vec2( 1f, 1f );
		report = studio.Rebuild();

		Check( "a seed picks exactly one region", studio.Bodies.Count == 1 && !report.HasErrors,
			$"{studio.Bodies.Count} bodies, {report}" );
		Check( "the seed picks the region it is inside",
			MathF.Abs( Volume( studio.Bodies[0].Mesh ) - 4f ) < 1e-3f,
			$"volume {Volume( studio.Bodies[0].Mesh )}, expected 4" );

		// Seeded inside the small square - same sketch, different face.
		studio = TwoSquares( out var small );
		small.RegionSeed = new Vec2( 5.5f, 5.5f );
		studio.Rebuild();

		Check( "a different seed picks the other region",
			studio.Bodies.Count == 1 && MathF.Abs( Volume( studio.Bodies[0].Mesh ) - 1f ) < 1e-3f,
			$"{studio.Bodies.Count} bodies, volume {Volume( studio.Bodies[0].Mesh )}, expected 1" );

		// A seed in empty space is an error, not a silent fallback to every region. Falling back
		// would quietly extrude the whole sketch when the face you picked stopped existing.
		studio = TwoSquares( out var gone );
		gone.RegionSeed = new Vec2( 100f, 100f );
		report = studio.Rebuild();

		Check( "a seed that matches nothing reports an error", report.HasErrors, report.ToString() );
		Check( "and produces no body from that feature", studio.Bodies.Count == 0,
			$"{studio.Bodies.Count} bodies" );
	}

	/// <summary>Shoelace area of a curve as the extrude actually tessellates it. Comparing against
	/// pi r squared instead would be comparing against a circle the mesh never contained.</summary>
	static float TessellatedArea( Sketch sketch, SketchCurve curve )
	{
		var points = curve.Tessellate( sketch, sketch.Tolerance );

		// Tessellate returns both endpoints, so the closing point repeats the first and is dropped
		// rather than counted twice.
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

	static float Volume( PolyMesh m ) =>
		m.Faces.Sum( f => Vec3.Dot( m.FaceCentroid( f ), m.FaceNormal( f ) ) * m.FaceArea( f ) ) / 3f;

	static void TestPlanes()
	{
		var plane = SketchPlane.XY;
		Check( "XY normal is +Z", plane.Normal.AlmostEquals( new Vec3( 0, 0, 1 ) ), plane.Normal.ToString() );
		Check( "XZ normal is -Y", SketchPlane.XZ.Normal.AlmostEquals( new Vec3( 0, -1, 0 ) ),
			SketchPlane.XZ.Normal.ToString() );
		Check( "YZ normal is +X", SketchPlane.YZ.Normal.AlmostEquals( new Vec3( 1, 0, 0 ) ),
			SketchPlane.YZ.Normal.ToString() );

		// Plane coordinates must survive a trip through world space and back, on every plane, or
		// sketches drawn on anything but XY end up subtly displaced.
		foreach ( var (name, p) in new[] { ("XY", SketchPlane.XY), ("XZ", SketchPlane.XZ), ("YZ", SketchPlane.YZ) } )
		{
			var original = new Vec2( 3.5f, -1.25f );
			var back = p.ToPlane( p.ToWorld( original ) );
			Check( $"{name} round-trips plane coords",
				MathF.Abs( back.x - original.x ) < 1e-4f && MathF.Abs( back.y - original.y ) < 1e-4f,
				back.ToString() );
		}

		var offset = SketchPlane.XY.Offset( 5f );
		Check( "offset plane moves along its normal", offset.Origin.AlmostEquals( new Vec3( 0, 0, 5 ) ),
			offset.Origin.ToString() );
		Check( "offset plane keeps its normal", offset.Normal.AlmostEquals( new Vec3( 0, 0, 1 ) ) );
	}

	static void TestArcs()
	{
		// Segment count comes from the allowed sagitta, so a tighter tolerance must give more
		// segments and a bigger radius must too.
		var coarse = SketchArc.SegmentsForArc( 1f, MathF.Tau, 0.1f );
		var fine = SketchArc.SegmentsForArc( 1f, MathF.Tau, 0.001f );
		var big = SketchArc.SegmentsForArc( 100f, MathF.Tau, 0.1f );

		Check( "tighter tolerance gives more segments", fine > coarse, $"{coarse} -> {fine}" );
		Check( "bigger radius gives more segments", big > coarse, $"{coarse} -> {big}" );
		Check( "segment count stays bounded", fine <= 4096, $"{fine}" );

		var sketch = new Sketch { Tolerance = 0.01f };
		var c = sketch.AddPoint( 0, 0 );
		var s = sketch.AddPoint( 1, 0 );
		var e = sketch.AddPoint( 0, 1 );
		var arc = sketch.Add( new SketchArc( c, s, e ) );

		var pts = arc.Tessellate( sketch, sketch.Tolerance );

		Check( "arc starts exactly on its start point",
			MathF.Abs( pts[0].x - 1f ) < 1e-5f && MathF.Abs( pts[0].y ) < 1e-5f, pts[0].ToString() );
		Check( "arc ends exactly on its end point",
			MathF.Abs( pts[^1].x ) < 1e-5f && MathF.Abs( pts[^1].y - 1f ) < 1e-5f, pts[^1].ToString() );

		var offRadius = pts.Count( p => MathF.Abs( MathF.Sqrt( p.x * p.x + p.y * p.y ) - 1f ) > 1e-3f );
		Check( "arc samples sit on the radius", offRadius == 0, $"{offRadius} off" );

		// A quarter turn counter-clockwise from +X to +Y must not go the long way round.
		Check( "arc takes the short way", pts.All( p => p.x >= -1e-4f && p.y >= -1e-4f ) );
	}

	static void TestProfiles()
	{
		var rect = new Sketch();
		rect.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 3 ) );
		var found = ProfileFinder.Find( rect );

		Check( "rectangle gives one profile", found.Profiles.Count == 1, $"{found.Profiles.Count}" );
		Check( "rectangle has four corners", found.Profiles[0].Outer.Count == 4,
			$"{found.Profiles[0].Outer.Count}" );
		Check( "rectangle outer loop is counter-clockwise",
			ProfileFinder.SignedArea( found.Profiles[0].Outer ) > 0 );
		Check( "rectangle area is 6", MathF.Abs( found.Profiles[0].Area - 6f ) < 1e-4f,
			$"{found.Profiles[0].Area}" );
		Check( "rectangle has no open chains", found.OpenChains == 0 );

		// A clockwise-drawn rectangle must come back oriented the same way as a counter-clockwise
		// one, or extruding it would produce an inside-out solid.
		var cw = new Sketch();
		cw.AddPolygon( new Vec2( 0, 0 ), new Vec2( 0, 3 ), new Vec2( 2, 3 ), new Vec2( 2, 0 ) );
		var cwFound = ProfileFinder.Find( cw );
		Check( "clockwise input is re-oriented",
			ProfileFinder.SignedArea( cwFound.Profiles[0].Outer ) > 0 );

		var circle = new Sketch();
		circle.AddCircle( new Vec2( 0, 0 ), 2f );
		var circleFound = ProfileFinder.Find( circle );
		Check( "circle gives one profile", circleFound.Profiles.Count == 1, $"{circleFound.Profiles.Count}" );
		Check( "circle area is about pi r squared",
			MathF.Abs( circleFound.Profiles[0].Area - MathF.PI * 4f ) < 0.1f,
			$"{circleFound.Profiles[0].Area}" );

		// Nesting detection: a circle inside a rectangle is a hole, not a second region.
		var withHole = new Sketch();
		withHole.AddRectangle( new Vec2( -5, -5 ), new Vec2( 5, 5 ) );
		withHole.AddCircle( new Vec2( 0, 0 ), 1f );
		var holeFound = ProfileFinder.Find( withHole );

		Check( "nested loop is one profile, not two", holeFound.Profiles.Count == 1,
			$"{holeFound.Profiles.Count}" );
		Check( "nested loop is detected as a hole", holeFound.Profiles[0].HasHoles );
		Check( "hole is wound opposite to the outer loop",
			ProfileFinder.SignedArea( holeFound.Profiles[0].Holes[0] ) < 0 );
		Check( "hole is subtracted from the area",
			MathF.Abs( holeFound.Profiles[0].Area - (100f - MathF.PI) ) < 0.1f,
			$"{holeFound.Profiles[0].Area}" );

		// Two separate shapes are two profiles.
		var two = new Sketch();
		two.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );
		two.AddRectangle( new Vec2( 5, 5 ), new Vec2( 6, 6 ) );
		Check( "disjoint shapes give two profiles", ProfileFinder.Find( two ).Profiles.Count == 2 );

		// An unclosed chain is reported rather than silently treated as a region.
		// A revolve past a full turn swept over itself and welded the overlap, producing a mesh with
		// edges shared by four or more faces and no error at all.
		var over = new PartStudio();
		var os = over.Add( new SketchFeature() );
		os.Sketch.AddRectangle( new Vec2( 3, -0.5f ), new Vec2( 5, 0.5f ) );
		var overRevolve = over.Add( new RevolveFeature() );
		overRevolve.AxisDirection.Value = new Vec3( 0, 1, 0 );
		overRevolve.Angle.Value = 720f;
		over.Rebuild();

		Check( "a revolve past a full turn is refused", overRevolve.Error is not null, overRevolve.Error );

		// A circle at or below the sketch tolerance tessellates to fewer than three points. Walked
		// loops are guarded; the circle path was not, and produced faces with two corners.
		var tiny = new Sketch { Tolerance = 0.1f };
		var tc = tiny.AddPoint( 0, 0 );
		tiny.Add( new SketchCircle( tc, 0.01f ) );
		var tinyResult = ProfileFinder.Find( tiny );

		Check( "a circle too small to be a region is reported, not extruded",
			tinyResult.Profiles.Count == 0 && tinyResult.Warnings.Count > 0,
			$"{tinyResult.Profiles.Count} profiles, {tinyResult.Warnings.Count} warnings" );

		// An open chain seeded from its MIDDLE used to be counted twice: the walk goes one way, and
		// the untouched half was then picked up as a fresh seed.
		var middle = new Sketch();
		var m0 = middle.AddPoint( 0, 0 );
		var m1 = middle.AddPoint( 1, 0 );
		var m2 = middle.AddPoint( 2, 0 );
		var m3 = middle.AddPoint( 3, 0 );
		middle.Add( new SketchLine( m1, m2 ) );   // the middle segment is seeded first
		middle.Add( new SketchLine( m0, m1 ) );
		middle.Add( new SketchLine( m2, m3 ) );

		Check( "an open chain seeded from its middle counts once",
			ProfileFinder.Find( middle ).OpenChains == 1,
			$"{ProfileFinder.Find( middle ).OpenChains}" );

		var open = new Sketch();
		var p0 = open.AddPoint( 0, 0 );
		var p1 = open.AddPoint( 1, 0 );
		var p2 = open.AddPoint( 1, 1 );
		open.Add( new SketchLine( p0, p1 ) );
		open.Add( new SketchLine( p1, p2 ) );
		var openFound = ProfileFinder.Find( open );

		Check( "open chain gives no profile", openFound.Profiles.Count == 0, $"{openFound.Profiles.Count}" );
		Check( "open chain is counted", openFound.OpenChains == 1, $"{openFound.OpenChains}" );

		// Construction geometry guides but never forms a region.
		var construction = new Sketch();
		foreach ( var line in construction.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) ) )
			line.Construction = true;

		Check( "construction geometry forms no profile",
			ProfileFinder.Find( construction ).Profiles.Count == 0 );

		// Rounded rectangle: lines and arcs in one loop, which is the real test of the walker
		// stitching different curve types together.
		var slot = new Sketch();
		var a0 = slot.AddPoint( 0, 0 );
		var a1 = slot.AddPoint( 4, 0 );
		var a2 = slot.AddPoint( 4, 2 );
		var a3 = slot.AddPoint( 0, 2 );
		var c0 = slot.AddPoint( 4, 1 );
		var c1 = slot.AddPoint( 0, 1 );
		slot.Add( new SketchLine( a0, a1 ) );
		slot.Add( new SketchArc( c0, a1, a2 ) );
		slot.Add( new SketchLine( a2, a3 ) );
		slot.Add( new SketchArc( c1, a3, a0 ) );

		var slotFound = ProfileFinder.Find( slot );
		Check( "lines and arcs stitch into one loop", slotFound.Profiles.Count == 1,
			$"{slotFound.Profiles.Count}, {slotFound.OpenChains} open" );
		Check( "stitched loop has no duplicated joins",
			slotFound.Profiles.Count == 1 && NoDuplicatePoints( slotFound.Profiles[0].Outer ) );
	}

	static bool NoDuplicatePoints( List<Vec2> loop )
	{
		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = loop[i];
			var b = loop[(i + 1) % loop.Count];

			if ( MathF.Abs( a.x - b.x ) < 1e-6f && MathF.Abs( a.y - b.y ) < 1e-6f )
				return false;
		}

		return true;
	}

	static PartStudio ExtrudedRectangle( float w, float h, float d, bool symmetric = false )
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( w, h ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = d;
		extrude.Symmetric.Value = symmetric;

		studio.Rebuild();
		return studio;
	}

	static void TestExtrude()
	{
		var studio = ExtrudedRectangle( 2, 3, 4 );
		Check( "extrude produces a body", studio.Bodies.Count == 1, $"{studio.Bodies.Count}" );

		var mesh = studio.Bodies[0].Mesh;
		Check( "extruded rectangle has 6 faces", mesh.FaceCount == 6, $"{mesh.FaceCount}" );

		var v = MeshValidator.Validate( mesh );
		Check( "extrusion is valid and closed", v.IsValid && v.IsClosed, v.ToString() );
		Check( "extrusion has X = 2", MeshValidator.EulerCharacteristic( mesh ) == 2 );

		var vol = Volume( mesh );
		Check( "extruded volume is 2x3x4 = 24", MathF.Abs( vol - 24f ) < 1e-3f, $"{vol:0.####}" );

		// A negative distance must still give a solid the right way out, not an inside-out one.
		var negative = ExtrudedRectangle( 2, 3, -4 );
		var negVol = Volume( negative.Bodies[0].Mesh );
		Check( "NEGATIVE distance still winds outward", negVol > 0, $"{negVol:0.####}" );
		Check( "negative distance gives the same volume", MathF.Abs( negVol - 24f ) < 1e-3f, $"{negVol:0.####}" );

		// Symmetric straddles the sketch plane.
		var sym = ExtrudedRectangle( 2, 2, 4, symmetric: true );
		var zs = sym.Bodies[0].Mesh.Positions.Select( p => p.z ).ToList();
		Check( "symmetric extrude straddles the plane",
			MathF.Abs( zs.Min() + 2f ) < 1e-4f && MathF.Abs( zs.Max() - 2f ) < 1e-4f,
			$"{zs.Min()}..{zs.Max()}" );

		// A circle extrudes to a cylinder, and the cap arrives as one n-gon rather than a fan.
		var circleStudio = new PartStudio();
		var cs = circleStudio.Add( new SketchFeature() );
		cs.Sketch.AddCircle( new Vec2( 0, 0 ), 1f );
		circleStudio.Add( new ExtrudeFeature() ).Distance.Value = 2f;
		circleStudio.Rebuild();

		var cyl = circleStudio.Bodies[0].Mesh;
		var caps = cyl.Faces.Count( f => f.Count > 4 );
		Check( "circular extrude caps are n-gons, not fans", caps == 2, $"{caps}" );

		// An inscribed polygon is always slightly SMALLER than the circle it samples, so the right
		// assertion is "just under pi r squared h", not "equal to it". Demanding equality here
		// would only be satisfiable by over-tessellating.
		var cylVolume = Volume( cyl );
		var trueVolume = MathF.PI * 2f;
		Check( "cylinder volume is just under pi r squared h",
			cylVolume < trueVolume && cylVolume > trueVolume * 0.97f,
			$"{cylVolume:0.####} vs {trueVolume:0.####}" );

		// On a non-default plane the solid must be built in the right orientation too.
		var front = new PartStudio();
		var fs = front.Add( new SketchFeature() );
		fs.Plane.Index = 1; // XZ
		fs.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );
		front.Add( new ExtrudeFeature() ).Distance.Value = 2f;
		front.Rebuild();

		Check( "extrude on XZ still winds outward", Volume( front.Bodies[0].Mesh ) > 0,
			$"{Volume( front.Bodies[0].Mesh ):0.####}" );
		Check( "extrude on XZ has volume 8",
			MathF.Abs( Volume( front.Bodies[0].Mesh ) - 8f ) < 1e-3f );

		// A plate with a hole in it. This used to be refused outright on the grounds that capping
		// around a hole was "the same problem as a boolean subtract"; it is a 2D triangulation
		// problem, and it builds now.
		var holed = new PartStudio();
		var hs = holed.Add( new SketchFeature() );
		hs.Sketch.AddRectangle( new Vec2( -5, -5 ), new Vec2( 5, 5 ) );
		var holeCircle = hs.Sketch.AddCircle( new Vec2( 0, 0 ), 1f );
		var holedExtrude = holed.Add( new ExtrudeFeature() );
		holedExtrude.Distance.Value = 2f;
		holed.Rebuild();

		Check( "a profile with a hole extrudes", holedExtrude.Error is null, holedExtrude.Error );

		if ( holedExtrude.Error is null )
		{
			var plate = holed.Bodies[0].Mesh;

			// 100 minus the circle, times 2 deep. The circle is tessellated, so the comparison is
			// against the polygon's area rather than pi r squared — otherwise the tolerance would
			// have to be loose enough to also pass a plate with no hole at all.
			var holeArea = TessellatedArea( hs.Sketch, holeCircle );
			var expected = (100f - holeArea) * 2f;

			Check( "and its volume is the plate minus the hole",
				MathF.Abs( Volume( plate ) - expected ) < 0.05f,
				$"{Volume( plate ):0.####}, expected {expected:0.####}" );

			// GENUS 1, the same as the Tube primitive. A cap that quietly filled the hole in could
			// slip past a volume check with a small enough hole; it cannot slip past this.
			var x = MeshValidator.EulerCharacteristic( plate );
			Check( "a plate with one hole is genus 1, so X = 0", x == 0, $"X = {x}" );

			var validation = MeshValidator.Validate( plate );
			Check( "it is a valid closed mesh", validation.IsValid && validation.IsClosed, validation.ToString() );
		}

		// Revolve has no answer for a hole and says so itself, now that the shared refusal is gone.
		var revolvedHole = new PartStudio();
		var rhs = revolvedHole.Add( new SketchFeature() );
		rhs.Sketch.AddRectangle( new Vec2( 2, 2 ), new Vec2( 6, 6 ) );
		rhs.Sketch.AddCircle( new Vec2( 4, 4 ), 1f );
		var holedRevolve = revolvedHole.Add( new RevolveFeature() );
		revolvedHole.Rebuild();

		Check( "revolving a holed profile still reports an error", holedRevolve.Error is not null );
		Check( "and points at the feature that can do it",
			holedRevolve.Error?.Contains( "Extrude" ) == true, holedRevolve.Error );

		// Two disjoint regions extrude to two bodies.
		var twoStudio = new PartStudio();
		var ts = twoStudio.Add( new SketchFeature() );
		ts.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );
		ts.Sketch.AddRectangle( new Vec2( 5, 5 ), new Vec2( 6, 6 ) );
		twoStudio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		twoStudio.Rebuild();
		Check( "two regions extrude to two bodies", twoStudio.Bodies.Count == 2, $"{twoStudio.Bodies.Count}" );
	}

	static void TestRevolve()
	{
		// A square offset from the axis sweeps a torus — genus 1, so Euler characteristic 0. If
		// the rings failed to close, this reads 2 and the seam is open.
		//
		// The profile sits entirely ABOVE the axis (y from 1 to 2, axis along y=0). A profile
		// straddling the axis is invalid and separately tested below.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 3, 1f ), new Vec2( 4, 2f ) );

		var revolve = studio.Add( new RevolveFeature() );
		revolve.AxisPoint.Value = Vec3.Zero;
		revolve.AxisDirection.Value = new Vec3( 1, 0, 0 ); // sketch-space +X
		revolve.Angle.Value = 360f;
		revolve.Segments.Value = 24;

		studio.Rebuild();

		Check( "revolve produces a body", studio.Bodies.Count == 1 && revolve.Error is null, revolve.Error );

		var torus = studio.Bodies[0].Mesh;
		var tv = MeshValidator.Validate( torus );

		Check( "revolved torus is valid", tv.IsValid, tv.ToString() );
		Check( "revolved torus is CLOSED (the seam welded)", tv.IsClosed, tv.ToString() );
		Check( "revolved torus has X = 0", MeshValidator.EulerCharacteristic( torus ) == 0,
			$"{MeshValidator.EulerCharacteristic( torus )}" );
		Check( "revolved torus winds outward", Volume( torus ) > 0, $"{Volume( torus ):0.####}" );

		// Pappus: volume = area * 2 pi * centroid radius. The 1x1 profile's centroid is 1.5 from
		// the axis, so 1 * 2pi * 1.5.
		var expected = 1f * MathF.Tau * 1.5f;
		Check( "torus volume matches Pappus' theorem",
			MathF.Abs( Volume( torus ) - expected ) < expected * 0.02f,
			$"{Volume( torus ):0.###} vs {expected:0.###}" );

		// A profile touching the axis must collapse to a closed solid rather than leaving a hole.
		var sphereish = new PartStudio();
		var ss = sphereish.Add( new SketchFeature() );
		ss.Sketch.AddPolygon( new Vec2( 0, 0 ), new Vec2( 2, 0 ), new Vec2( 0, 2 ) );

		var sphereRevolve = sphereish.Add( new RevolveFeature() );
		sphereRevolve.AxisPoint.Value = Vec3.Zero;
		sphereRevolve.AxisDirection.Value = new Vec3( 0, 1, 0 ); // sketch-space +Y
		sphereRevolve.Angle.Value = 360f;
		sphereish.Rebuild();

		Check( "profile on the axis revolves without error",
			sphereRevolve.Error is null, sphereRevolve.Error );

		var cone = sphereish.Bodies[0].Mesh;
		var cv = MeshValidator.Validate( cone );

		Check( "on-axis profile gives a valid solid", cv.IsValid, cv.ToString() );
		Check( "on-axis profile gives a CLOSED solid", cv.IsClosed, cv.ToString() );
		Check( "on-axis solid has X = 2", MeshValidator.EulerCharacteristic( cone ) == 2,
			$"{MeshValidator.EulerCharacteristic( cone )}" );
		Check( "on-axis solid winds outward", Volume( cone ) > 0, $"{Volume( cone ):0.####}" );

		// Partial revolutions get capped at both ends.
		var partial = new PartStudio();
		var ps = partial.Add( new SketchFeature() );
		ps.Sketch.AddRectangle( new Vec2( 3, 1f ), new Vec2( 4, 2f ) );

		var partialRevolve = partial.Add( new RevolveFeature() );
		partialRevolve.AxisDirection.Value = new Vec3( 1, 0, 0 );
		partialRevolve.Angle.Value = 90f;
		partial.Rebuild();

		var wedge = partial.Bodies[0].Mesh;
		var pv = MeshValidator.Validate( wedge );

		Check( "partial revolve is capped and closed", pv.IsValid && pv.IsClosed, pv.ToString() );
		Check( "partial revolve winds outward", Volume( wedge ) > 0, $"{Volume( wedge ):0.####}" );
		Check( "quarter turn is a quarter of the volume",
			MathF.Abs( Volume( wedge ) - expected / 4f ) < expected * 0.02f,
			$"{Volume( wedge ):0.###} vs {expected / 4f:0.###}" );

		// A profile straddling the axis must be refused. Left to run it produces a mesh where every
		// face exists twice with opposite winding: it encloses zero volume, welds vertices that
		// should be distinct, and looks entirely plausible until measured.
		var crossing = new PartStudio();
		var xs = crossing.Add( new SketchFeature() );
		xs.Sketch.AddRectangle( new Vec2( 3, -0.5f ), new Vec2( 4, 0.5f ) );

		var crossingRevolve = crossing.Add( new RevolveFeature() );
		crossingRevolve.AxisDirection.Value = new Vec3( 1, 0, 0 );
		crossing.Rebuild();

		Check( "profile crossing the axis is refused", crossingRevolve.Error is not null );
		Check( "and the error says why",
			crossingRevolve.Error?.Contains( "crosses the axis" ) == true, crossingRevolve.Error );

		// Touching the axis is fine — only crossing it is not.
		Check( "touching the axis is still allowed", sphereRevolve.Error is null, sphereRevolve.Error );
	}

	static void TestSketchInTree()
	{
		var studio = new PartStudio();
		var sketchFeature = studio.Add( new SketchFeature() );
		sketchFeature.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 2f;
		var subdiv = studio.Add( new SubdivideFeature() );
		subdiv.Levels.Value = 2;

		studio.Rebuild();

		Check( "sketch, extrude and subdivide chain together",
			studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.FaceCount == 96,
			$"{studio.Bodies.Count} bodies, {studio.Bodies[0].Mesh.FaceCount} faces" );

		// The whole point of referencing the sketch by feature id: editing the sketch and
		// rebuilding must feed through, with nothing to re-wire.
		sketchFeature.Sketch = new Sketch();
		sketchFeature.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 10, 2 ) );
		studio.MarkDirty( sketchFeature );
		studio.Rebuild();

		var width = studio.Bodies[0].Mesh.Positions.Max( p => p.x ) - studio.Bodies[0].Mesh.Positions.Min( p => p.x );
		Check( "editing the sketch changes the solid", width > 6f, $"width {width:0.##}" );

		// Rolling back above the extrude leaves the sketch with nothing built from it.
		studio.RollbackIndex = 1;
		studio.MarkAllDirty();
		studio.Rebuild();
		Check( "rollback above extrude leaves no bodies", studio.Bodies.Count == 0, $"{studio.Bodies.Count}" );

		// An extrude with no sketch at all explains itself rather than throwing something opaque.
		var orphan = new PartStudio();
		var lonely = orphan.Add( new ExtrudeFeature() );
		orphan.Rebuild();
		Check( "extrude with no sketch reports a clear error",
			lonely.Error?.Contains( "no sketch" ) == true, lonely.Error );
	}
}
