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

		Report.Section( "face sketching: the face's own outline, to draw and to snap to" );
		TestReferenceOutline();

		Report.Section( "face sketching: the outline follows the face it came from" );
		TestReferenceOutlineFollowsFace();

		Report.Section( "face sketching: snapping onto the face's corners and edges" );
		TestSnapToReference();

		Report.Section( "face sketching: Use - the outline becomes real sketch geometry" );
		TestUseReference();
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

		// Kept as its own body ON PURPOSE. The default now merges a face-attached extrude into the
		// body it grows from, which is what anyone building a part wants — but it also means the
		// boss stops being separately measurable, and what these tests are about is where the
		// SKETCH landed. Merging has its own tests; this one wants the boss on its own.
		boss.Result.Index = 1;

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

		var riding = studio.Add( new ExtrudeFeature() );
		riding.Distance.Value = 1f;
		riding.Result.Index = 1; // separate body, so the boss can be measured on its own
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

	// --- the face's outline, as reference geometry ---------------------------------------------

	/// <summary>A box with a sketch on its top face, rebuilt and ready to be asked what is
	/// underneath that sketch.</summary>
	static (PartStudio Studio, SketchFeature Sketch) BoxWithFaceSketch( float sizeX, float sizeY, float sizeZ )
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = sizeX;
		box.SizeY.Value = sizeY;
		box.SizeZ.Value = sizeZ;
		studio.Rebuild();

		var sketch = studio.Add( new SketchFeature() );
		sketch.Face = TopFaceOf( studio );
		studio.Rebuild();

		return (studio, sketch);
	}

	static (float Width, float Height) Extent( SketchReference reference )
	{
		var minX = reference.Points.Min( p => p.x );
		var maxX = reference.Points.Max( p => p.x );
		var minY = reference.Points.Min( p => p.y );
		var maxY = reference.Points.Max( p => p.y );

		return (maxX - minX, maxY - minY);
	}

	static void TestReferenceOutline()
	{
		var (studio, sketch) = BoxWithFaceSketch( 4f, 6f, 2f );
		var reference = SketchReference.FromFace( studio.Bodies, sketch.Face.Value, sketch.Sketch.Plane );

		Report.Check( "the top face of a box gives four corners and four edges",
			reference.Points.Count == 4 && reference.Edges.Count == 4,
			$"{reference.Points.Count} points, {reference.Edges.Count} edges" );

		var (width, height) = Extent( reference );

		// 4 x 6, in whichever order the plane's axes happen to fall - the axes come from the normal
		// alone (FromPointAndNormal) and this test is not the place to pin down which is which.
		Report.Check( "and they are the size of the face",
			MathF.Abs( MathF.Min( width, height ) - 4f ) < 1e-3f
			&& MathF.Abs( MathF.Max( width, height ) - 6f ) < 1e-3f,
			$"{width} x {height}" );

		// EVERY corner is on the sketch plane, not merely near it. The outline is drawn and clicked
		// in plane coordinates, so a projection that quietly kept some of the face's depth would put
		// the snap targets somewhere the cursor can never reach.
		var onPlane = reference.Points.All( p =>
		{
			var world = sketch.Sketch.Plane.ToWorld( p );
			return MathF.Abs( Vec3.Dot( world - sketch.Sketch.Plane.Origin, sketch.Sketch.Plane.Normal ) ) < 1e-4f;
		} );

		Report.Check( "every corner lies in the sketch plane", onPlane );

		// A sketch on one of the three global planes has nothing underneath it to reference, and
		// must not invent any.
		var plain = new PartStudio();
		plain.Add( new PrimitiveFeature() );
		var loose = plain.Add( new SketchFeature() );
		plain.Rebuild();

		Report.Check( "a sketch on a global plane has no reference geometry",
			loose.Face is null );

		// THE SEAM RULE. A face that has been through a boolean is several coplanar faces, and the
		// cuts between them are not edges of anything - outlining each face separately would draw
		// lines across the middle of a flat surface and offer them as snap targets. Here the top of
		// the box is split into two triangles by hand, which is exactly that situation.
		var split = Primitives.Box( 4f, 4f, 2f );
		var topIndex = split.Faces.FindIndex( f => split.FaceNormal( f ).z > 0.99f );
		var top = split.Faces[topIndex];
		var corners = top.Indices;

		split.Faces.RemoveAt( topIndex );
		split.AddFace( new[] { corners[0], corners[1], corners[2] } );
		split.AddFace( new[] { corners[0], corners[2], corners[3] } );

		var body = new Body( "split", "Split", split );
		var seamFace = split.Faces.FindIndex( f => split.FaceNormal( f ).z > 0.99f );
		var seamRef = FacePlane.Capture( body, seamFace, split.FaceCentroid( split.Faces[seamFace] ) );

		FacePlane.TryResolve( new[] { body }, seamRef, out var seamPlane );

		var seam = SketchReference.FromFace( new[] { body }, seamRef, seamPlane );

		Report.Check( "a top face split into two triangles still outlines as one square",
			seam.Points.Count == 4 && seam.Edges.Count == 4,
			$"{seam.Points.Count} points, {seam.Edges.Count} edges" );
	}

	/// <summary>
	/// The outline is rebuilt from the model rather than stored, so it must move when the face
	/// does. This is the same property TestReferenceSurvivesUpstreamEdit checks for the plane, and
	/// it matters more here: a snap target left behind at the old size is worse than none at all,
	/// because it looks exactly like a correct one.
	/// </summary>
	static void TestReferenceOutlineFollowsFace()
	{
		var (studio, sketch) = BoxWithFaceSketch( 4f, 4f, 2f );

		var before = SketchReference.FromFace( studio.Bodies, sketch.Face.Value, sketch.Sketch.Plane );
		var (beforeWidth, _) = Extent( before );

		var box = studio.Features.OfType<PrimitiveFeature>().First();
		box.SizeX.Value = 10f;
		studio.MarkDirty( box );
		studio.Rebuild();

		var after = SketchReference.FromFace( studio.Bodies, sketch.Face.Value, sketch.Sketch.Plane );
		var (afterWidth, afterHeight) = Extent( after );

		Report.Check( "the outline was 4 units across before the box grew",
			MathF.Abs( beforeWidth - 4f ) < 1e-3f, $"{beforeWidth}" );

		Report.Check( "and 10 after",
			MathF.Abs( MathF.Max( afterWidth, afterHeight ) - 10f ) < 1e-3f,
			$"{afterWidth} x {afterHeight}" );
	}

	/// <summary>
	/// Drawing against the outline: a click near a corner of the face lands ON that corner, a click
	/// near one of its edges lands ON that edge, and neither is allowed to outrank the sketch's own
	/// points - closing a profile is the snap everything downstream depends on.
	/// </summary>
	static void TestSnapToReference()
	{
		var (studio, sketch) = BoxWithFaceSketch( 4f, 4f, 2f );
		var reference = SketchReference.FromFace( studio.Bodies, sketch.Face.Value, sketch.Sketch.Plane );

		if ( reference.Points.Count != 4 )
		{
			Report.Check( "the outline is there to snap to", false, $"{reference.Points.Count} points" );
			return;
		}

		// Screen-space tolerances, the way the viewport supplies them: a ~4 unit part framed in a
		// ~700px viewport, 12px of reach.
		var upp = 4f * 1.6f / 700f;

		var snapper = new SketchSnapper
		{
			PointRadius = 12f * upp,
			AlignmentRadius = 7f * upp,
			GridStep = 0f,
			Reference = reference,
			ReferencePointRadius = 12f * upp,
			ReferenceEdgeRadius = 12f * upp,
		};

		var corner = reference.Points[0];
		var nearCorner = corner + new Vec2( 3f * upp, 3f * upp );

		var onCorner = snapper.Snap( new Sketch(), nearCorner, Array.Empty<Vec2>(), false );

		Report.Check( "a click near a corner of the face lands exactly on it",
			onCorner.ReferencePointIndex == 0
			&& (onCorner.Point - corner).Length < 1e-5f,
			$"reference point {onCorner.ReferencePointIndex}, off by {(onCorner.Point - corner).Length}" );

		// Mid-edge, nudged off it. The landing point must be on the segment and nowhere near either
		// end, or the edge snap is really just a second corner snap.
		var (a, b) = reference.Segment( 0 );
		var middle = (a + b) * 0.5f;
		var normal = new Vec2( -(b - a).Normal.y, (b - a).Normal.x );

		var onEdge = snapper.Snap( new Sketch(), middle + normal * (4f * upp), Array.Empty<Vec2>(), false );

		Report.Check( "a click near an edge of the face lands on the edge",
			onEdge.ReferenceEdgeIndex == 0
			&& (onEdge.Point - SketchSnapper.ClosestOnSegment( a, b, onEdge.Point )).Length < 1e-5f,
			$"reference edge {onEdge.ReferenceEdgeIndex}, point {onEdge.Point}" );

		Report.Check( "and slides along it rather than jumping to an end",
			(onEdge.Point - middle).Length < 1e-3f, $"{(onEdge.Point - middle).Length} from the middle" );

		// A point already in the sketch, sitting slightly further away than the face's corner. The
		// sketch's own point still wins: this is the snap that closes a chain, and a corner of the
		// scenery underneath must never take it.
		var withPoint = new Sketch();
		withPoint.AddPoint( corner + new Vec2( 6f * upp, 0f ) );

		var contested = snapper.Snap( withPoint, corner + new Vec2( 5f * upp, 0f ), Array.Empty<Vec2>(), false );

		Report.Check( "the sketch's own point outranks a face corner when it is nearer",
			contested.SnappedPointIndex == 0 && contested.ReferencePointIndex < 0,
			$"sketch point {contested.SnappedPointIndex}, reference point {contested.ReferencePointIndex}" );

		// A LINE IN PROGRESS, aimed near an edge of the face. The line tool turns InferenceAxis into
		// a real Vertical/Horizontal constraint on the line it commits, so a snap that reports one
		// while landing somewhere that does not satisfy it attaches a rule the geometry breaks - and
		// the solver then drags the point off the edge it was just placed on.
		var start = middle + new Vec2( 0f, 20f * upp );
		var aimed = snapper.Snap( new Sketch(), middle + normal * (3f * upp), new[] { start }, lineInProgress: true );

		Report.Check( "a snap onto an edge claims no axis lock it does not satisfy",
			aimed.ReferenceEdgeIndex >= 0 && aimed.InferenceAxis == 0,
			$"edge {aimed.ReferenceEdgeIndex}, inference {aimed.InferenceAxis}" );

		// And the first corner of a half-drawn shape is not stolen by an edge running past it. This
		// is how a rectangle closes on a face - the pending corner has to beat the block's outline.
		var pendingCorner = corner + new Vec2( 2f * upp, 2f * upp );
		var closing = snapper.Snap( new Sketch(), pendingCorner + new Vec2( upp, upp ),
			new[] { pendingCorner }, lineInProgress: false );

		Report.Check( "a pending corner outranks the face's edge running past it",
			closing.ReferenceEdgeIndex < 0
			&& (closing.Point - pendingCorner).Length < 1e-5f,
			$"edge {closing.ReferenceEdgeIndex}, landed {(closing.Point - pendingCorner).Length} away" );

		// And with reference snapping off, the outline is inert - the same click grid-snaps as if
		// no face were underneath at all.
		snapper.ReferencePointRadius = 0f;
		snapper.ReferenceEdgeRadius = 0f;

		var ignored = snapper.Snap( new Sketch(), nearCorner, Array.Empty<Vec2>(), false );

		Report.Check( "turning reference snapping off makes the outline inert",
			ignored.ReferencePointIndex < 0 && ignored.ReferenceEdgeIndex < 0
			&& (ignored.Point - nearCorner).Length < 1e-5f,
			$"landed at {ignored.Point}, clicked at {nearCorner}" );
	}

	/// <summary>
	/// Use: turning the face's outline into geometry the sketch actually owns.
	///
	/// THIS IS THE THING THE OUTLINE ALONE CANNOT DO. Drawing one line across a face and expecting
	/// it to split in two is the natural move and it produces nothing, because the sketch contains
	/// one open line and no boundary — the face's edges are scenery until something copies them in.
	/// Onshape makes you press Use for exactly this reason. Every check below is that workflow.
	/// </summary>
	static void TestUseReference()
	{
		var (studio, sketch) = BoxWithFaceSketch( 4f, 4f, 2f );
		var reference = SketchReference.FromFace( studio.Bodies, sketch.Face.Value, sketch.Sketch.Plane );

		// The failure that sends you looking for a Use tool: a lone line across the face closes
		// nothing, because there is no boundary for it to close against.
		var lonely = new Sketch();
		lonely.Add( new SketchLine( lonely.AddPoint( new Vec2( -3f, 0f ) ), lonely.AddPoint( new Vec2( 3f, 0f ) ) ) );

		Report.Check( "a line drawn across a bare face closes no region at all",
			ProfileFinder.Find( lonely ).Profiles.Count == 0,
			$"{ProfileFinder.Find( lonely ).Profiles.Count} profiles" );

		// Use the whole outline, then the same line splits it in two.
		var used = new Sketch();
		var added = reference.UseAll( used );

		Report.Check( "Use all brings the four edges in", added == 4 && used.Curves.Count == 4,
			$"{added} added, {used.Curves.Count} curves" );

		Report.Check( "and they weld into four shared corners, not eight loose ends",
			used.Points.Count == 4, $"{used.Points.Count} points" );

		var whole = ProfileFinder.Find( used );

		Report.Check( "the outline on its own is one closed region",
			whole.Profiles.Count == 1 && whole.Warnings.Count == 0,
			$"{whole.Profiles.Count} profiles, {whole.Warnings.Count} warnings" );

		// CORNER TO CORNER, which is the case that works with nothing but Use. The diagonal's ends
		// are two corners the outline already owns, so those points reach degree three and the
		// half-edge walk splits the square into two triangles.
		var diagonal = new Sketch();
		reference.UseAll( diagonal );

		var corners = diagonal.Points.ToList();
		var far = 1;

		for ( var i = 1; i < corners.Count; i++ )
		{
			if ( (corners[i] - corners[0]).Length > (corners[far] - corners[0]).Length )
				far = i;
		}

		diagonal.Add( new SketchLine( 0, far ) );

		var split = ProfileFinder.Find( diagonal );

		Report.Check( "a diagonal between two used corners splits the face into two regions",
			split.Profiles.Count == 2, $"{split.Profiles.Count} profiles" );

		if ( split.Profiles.Count == 2 )
		{
			var total = split.Profiles.Sum( p => p.Area );

			Report.Check( "and the two halves add up to the whole face",
				MathF.Abs( total - 16f ) < 1e-2f, $"{total} vs 16" );
		}

		// MID-EDGE TO MID-EDGE, which is the move anyone actually reaches for. The line's ends sit
		// ON two opposite edges but are not endpoints OF them. The integer walk still prunes them
		// as dangling — coincidence is identity — and RecoverCutRegions imprints the T-junctions
		// on a copy so the two halves show up as faces. The sketch itself is not rewritten.
		var across = new Sketch();
		reference.UseAll( across );

		var lowX = across.Points.Min( p => p.x );
		var highX = across.Points.Max( p => p.x );
		var midY = across.Points.Average( p => p.y );

		across.Add( new SketchLine(
			SketchSnapper.PointIndex( across, new Vec2( lowX, midY ) ),
			SketchSnapper.PointIndex( across, new Vec2( highX, midY ) ) ) );

		var curvesBefore = across.Curves.Count;
		var crossed = ProfileFinder.Find( across );

		Report.Check( "a line across the middle splits the face into two regions",
			crossed.Profiles.Count == 2 && crossed.Warnings.Count == 0,
			$"{crossed.Profiles.Count} profiles, {crossed.Warnings.Count} warnings: "
			+ string.Join( " | ", crossed.Warnings ) );

		Report.Check( "without rewriting the sketch",
			across.Curves.Count == curvesBefore, $"{across.Curves.Count} curves, were {curvesBefore}" );

		if ( crossed.Profiles.Count == 2 )
		{
			Report.Check( "and the two halves add up to the whole face",
				MathF.Abs( crossed.Profiles.Sum( p => p.Area ) - 16f ) < 1e-2f,
				$"{crossed.Profiles.Sum( p => p.Area )} vs 16" );
		}

		// --- one edge at a time, and the guards ---------------------------------------------

		var single = new Sketch();

		Report.Check( "Use on one edge adds exactly that line",
			reference.UseEdge( single, 0 ) is not null && single.Curves.Count == 1,
			$"{single.Curves.Count} curves" );

		// USING THE SAME EDGE TWICE MUST NOT DOUBLE IT. Two curves between one pair of points is the
		// branching case ProfileFinder refuses, so a second click on an edge already used would
		// quietly destroy the profile being built rather than doing nothing.
		Report.Check( "using the same edge again is refused rather than doubled",
			reference.UseEdge( single, 0 ) is null && single.Curves.Count == 1,
			$"{single.Curves.Count} curves" );

		Report.Check( "and running Use all afterwards tops up the rest",
			reference.UseAll( single ) == 3 && single.Curves.Count == 4,
			$"{single.Curves.Count} curves" );

		Report.Check( "an out-of-range edge is refused rather than throwing",
			reference.UseEdge( single, 99 ) is null && reference.UseEdge( single, -1 ) is null );

		// A sketch on a global plane has an empty reference, so Use is a no-op rather than an error.
		Report.Check( "Use on an empty reference adds nothing",
			new SketchReference().UseAll( new Sketch() ) == 0 );
	}
}
