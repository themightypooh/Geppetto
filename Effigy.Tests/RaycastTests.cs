using System;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Ray-mesh intersection: the geometry behind clicking a face of an existing solid in the
/// viewport, which item 10 on the UI punch list needs and does not have a selector for yet.
///
/// Verified against a box, because a box's six faces and known face normals make the expected
/// answer to "what did this ray hit" checkable by hand.
/// </summary>
public static class RaycastTests
{
	public static void Run()
	{
		Report.Section( "raycast: straight down onto a box hits the correct face" );
		TestBoxFaces();

		Report.Section( "raycast: nearest face wins, not just any hit face" );
		TestNearestFaceWins();

		Report.Section( "raycast: misses report nothing" );
		TestMisses();

		Report.Section( "raycast: across several bodies, the nearest one wins" );
		TestMultiBody();

		Report.Section( "raycast: a point on a face names its nearest edge" );
		TestClosestEdge();

		Report.Section( "raycast: every crossing along the ray, not just the first" );
		TestAllHits();

		Report.Section( "raycast: the run of material a bone is placed in the middle of" );
		TestFirstSolidSpan();
	}

	static void TestAllHits()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var hits = MeshRaycast.AllHits( box, new Vec3( 0, 0, 5 ), new Vec3( 0, 0, -1 ) );

		// Straight down the middle: in through the top, out through the bottom, and nothing else.
		// The four side faces are parallel to the ray and must not be counted - a ray grazing a
		// face it never enters is the failure that would put an "exit" on the skin.
		Report.Check( "a ray down the axis of a box crosses exactly twice", hits.Count == 2,
			$"{hits.Count} hits" );

		if ( hits.Count != 2 )
			return;

		Report.Check( "they come back nearest first",
			hits[0].Distance < hits[1].Distance, $"{hits[0].Distance} then {hits[1].Distance}" );

		Report.Check( "the near one is the top face",
			hits[0].Point.AlmostEquals( new Vec3( 0, 0, 1 ) ), hits[0].Point.ToString() );

		// THE BACK FACE HAS TO BE IN HERE. Every exit points away from the ray by definition, so a
		// front-face-only scan finds entries and never an exit, and the span below is always null.
		Report.Check( "the far one is the bottom face, facing away from the ray",
			hits[1].Point.AlmostEquals( new Vec3( 0, 0, -1 ) )
			&& Vec3.Dot( new Vec3( 0, 0, -1 ), hits[1].Normal ) > 0f,
			$"{hits[1].Point} normal {hits[1].Normal}" );

		Report.Check( "a ray that misses crosses nothing",
			MeshRaycast.AllHits( box, new Vec3( 9, 9, 5 ), new Vec3( 0, 0, -1 ) ).Count == 0 );
	}

	static void TestFirstSolidSpan()
	{
		var box = Primitives.Box( 2, 4, 2 );   // 4 deep in y, so the answer is not a coincidence of a cube

		var span = MeshRaycast.FirstSolidSpan( box, new Vec3( 0, 10, 0 ), new Vec3( 0, -1, 0 ) );

		Report.Check( "a ray through a box reports a run", span is not null );

		if ( span is not { } through )
			return;

		Report.Check( "entering at the near face and leaving at the far one",
			through.Entry.AlmostEquals( new Vec3( 0, 2, 0 ) ) && through.Exit.AlmostEquals( new Vec3( 0, -2, 0 ) ),
			$"{through.Entry} to {through.Exit}" );

		Report.Check( "the midpoint is the middle of the material, not its surface",
			through.Midpoint.AlmostEquals( Vec3.Zero ), through.Midpoint.ToString() );

		Report.Check( "and the thickness is how much of it the ray crossed",
			MathF.Abs( through.Thickness - 4f ) < 1e-4f, $"{through.Thickness}" );

		// OFF-CENTRE STAYS OFF-CENTRE IN THE TWO AXES YOU AIMED WITH. Only the depth along the ray
		// is decided for you; a click near the top of a part still places near the top of it.
		var high = MeshRaycast.FirstSolidSpan( box, new Vec3( 0.5f, 10, 0.75f ), new Vec3( 0, -1, 0 ) );

		Report.Check( "the two axes the click named are left alone",
			high is { } h && MathF.Abs( h.Midpoint.x - 0.5f ) < 1e-4f
			&& MathF.Abs( h.Midpoint.z - 0.75f ) < 1e-4f,
			high?.Midpoint.ToString() ?? "no run" );

		Report.Check( "a ray that misses reports no run",
			MeshRaycast.FirstSolidSpan( box, new Vec3( 9, 10, 0 ), new Vec3( 0, -1, 0 ) ) is null );

		TestSpanStopsAtTheFirstGap();
		TestSpanAcrossBodies();
	}

	/// <summary>
	/// The concave case, which is the whole reason the span stops at the FIRST exit rather than
	/// running to the last hit.
	///
	/// Two boxes with a gap between them, as one mesh — the shape of a model's two legs seen from
	/// the front. Entry to the last hit would put the "middle" in the gap, which is outside the
	/// model and beside both legs. Entry to the first exit is the middle of the near leg.
	/// </summary>
	static void TestSpanStopsAtTheFirstGap()
	{
		var pair = Primitives.Box( 2, 2, 2 );   // -1..1 in y
		MeshTransform.Append( pair,
			MeshTransform.Transformed( Primitives.Box( 2, 2, 2 ), Xform.Translate( new Vec3( 0, -10, 0 ) ) ) );

		var span = MeshRaycast.FirstSolidSpan( pair, new Vec3( 0, 10, 0 ), new Vec3( 0, -1, 0 ) );

		Report.Check( "a ray crossing two lumps reports a run", span is not null );

		if ( span is not { } near )
			return;

		Report.Check( "the run is the near lump, not the whole spread",
			near.Midpoint.AlmostEquals( Vec3.Zero ), near.Midpoint.ToString() );

		Report.Check( "so the midpoint is in material rather than in the gap between them",
			MeshRaycast.PointInsideSolid( pair, near.Midpoint ) );
	}

	/// <summary>
	/// The two-step the bone tool performs: Raycast names the body, and the run is measured inside
	/// that one mesh.
	///
	/// A run measured across every body at once would enter the near one and leave the far one and
	/// put the midpoint in the air between them, which is why FirstSolidSpan takes a mesh rather
	/// than a body list. This is the composition that has to hold, so it is the composition that is
	/// tested rather than either half alone.
	/// </summary>
	static void TestSpanAcrossBodies()
	{
		var near = new Body( "near", "Near", Primitives.Box( 2, 2, 2 ) );
		var far = new Body( "far", "Far",
			MeshTransform.Transformed( Primitives.Box( 2, 2, 2 ), Xform.Translate( new Vec3( 0, -20, 0 ) ) ) );

		var origin = new Vec3( 0, 10, 0 );
		var direction = new Vec3( 0, -1, 0 );
		var hit = MeshRaycast.Raycast( new[] { near, far }, origin, direction );

		Report.Check( "the click lands on the near body", hit is { Body.Id: "near" },
			hit?.Body.Id ?? "no hit" );

		if ( hit is not { } landed )
			return;

		var span = MeshRaycast.FirstSolidSpan( landed.Body.Mesh, origin, direction );

		Report.Check( "and the run is measured inside that body, not between the two",
			span is { } s && s.Midpoint.AlmostEquals( Vec3.Zero ), span?.Midpoint.ToString() ?? "no run" );

		// Halfway between the two boxes is y = -10, which is the answer a body-blind measurement
		// would give and is nowhere near either of them.
		Report.Check( "so the midpoint is in the near box rather than in the gap",
			span is { } inside && MeshRaycast.PointInsideSolid( landed.Body.Mesh, inside.Midpoint ) );
	}

	static void TestBoxFaces()
	{
		// A 2x2x2 box centred on the origin: faces at +-1 on each axis.
		var box = Primitives.Box( 2, 2, 2 );

		var cases = new (string Name, Vec3 Origin, Vec3 Dir, Vec3 ExpectedPoint, Vec3 ExpectedNormal)[]
		{
			("top",    new Vec3( 0, 0, 5 ),  new Vec3( 0, 0, -1 ), new Vec3( 0, 0, 1 ),  new Vec3( 0, 0, 1 )),
			("bottom", new Vec3( 0, 0, -5 ), new Vec3( 0, 0, 1 ),  new Vec3( 0, 0, -1 ), new Vec3( 0, 0, -1 )),
			("+x",     new Vec3( 5, 0, 0 ),  new Vec3( -1, 0, 0 ), new Vec3( 1, 0, 0 ),  new Vec3( 1, 0, 0 )),
			("-x",     new Vec3( -5, 0, 0 ), new Vec3( 1, 0, 0 ),  new Vec3( -1, 0, 0 ), new Vec3( -1, 0, 0 )),
			("+y",     new Vec3( 0, 5, 0 ),  new Vec3( 0, -1, 0 ), new Vec3( 0, 1, 0 ),  new Vec3( 0, 1, 0 )),
			("-y",     new Vec3( 0, -5, 0 ), new Vec3( 0, 1, 0 ),  new Vec3( 0, -1, 0 ), new Vec3( 0, -1, 0 )),
		};

		foreach ( var (name, origin, dir, expectedPoint, expectedNormal) in cases )
		{
			var hit = MeshRaycast.Raycast( box, origin, dir );

			Report.Check( $"ray at the {name} face hits something", hit is not null );

			if ( hit is not { } h )
				continue;

			Report.Check( $"{name} face: hit point is where the face actually is",
				h.Point.AlmostEquals( expectedPoint, 1e-3f ), h.Point.ToString() );

			Report.Check( $"{name} face: hit normal points outward correctly",
				h.Normal.AlmostEquals( expectedNormal, 1e-3f ), h.Normal.ToString() );

			Report.Check( $"{name} face: reported distance matches the actual travel",
				MathF.Abs( h.Distance - (origin - expectedPoint).Length ) < 1e-3f, $"{h.Distance}" );
		}
	}

	static void TestNearestFaceWins()
	{
		// A ray from well outside the box, straight through it, must hit the NEAR face - not the
		// far one, and not whichever triangle happens to be listed first.
		var box = Primitives.Box( 2, 2, 2 );
		var hit = MeshRaycast.Raycast( box, new Vec3( 0, 0, 10 ), new Vec3( 0, 0, -1 ) );

		Report.Check( "a ray through the whole box hits the near face, not the far one",
			hit is not null && hit.Value.Point.z > 0f, hit?.Point.ToString() ?? "no hit" );
	}

	static void TestMisses()
	{
		var box = Primitives.Box( 2, 2, 2 );

		var behind = MeshRaycast.Raycast( box, new Vec3( 0, 0, 5 ), new Vec3( 0, 0, 1 ) );
		Report.Check( "a ray pointing away from the mesh reports no hit", behind is null );

		var beside = MeshRaycast.Raycast( box, new Vec3( 10, 10, 10 ), new Vec3( 0, 0, -1 ) );
		Report.Check( "a ray that passes beside the mesh entirely reports no hit", beside is null );

		var nothing = MeshRaycast.Raycast( (PolyMesh)null, Vec3.Zero, new Vec3( 0, 0, 1 ) );
		Report.Check( "a null mesh reports no hit rather than throwing", nothing is null );
	}

	static void TestMultiBody()
	{
		// Two boxes stacked along Z, ray fired from the SAME side as "near" so the names actually
		// describe distance from the ray origin - the first version of this test named them by
		// world position instead and shot the ray from the far body's own side, which made the
		// mislabelled body win for the right reason and read as a bug.
		var near = new Body( "near", "Near", Primitives.Box( 1, 1, 1 ) );   // faces at +-0.5
		var far = new Body( "far", "Far",
			MeshTransform.Transformed( Primitives.Box( 1, 1, 1 ), Xform.Translate( new Vec3( 0, 0, 10 ) ) ) );  // 9.5..10.5

		// Fired from BELOW both boxes, travelling +Z: hits "near" first (its underside at -0.5),
		// then would go on to hit "far" if "near" were not there.
		var result = MeshRaycast.Raycast( new[] { near, far }, new Vec3( 0, 0, -20 ), new Vec3( 0, 0, 1 ) );

		Report.Check( "the nearer body's face wins over the farther one",
			result is { Body.Id: "near" }, result?.Body.Id ?? "no hit" );

		var onlyFar = MeshRaycast.Raycast( new[] { far }, new Vec3( 0, 0, -20 ), new Vec3( 0, 0, 1 ) );

		Report.Check( "with only the far body present, that one is hit instead",
			onlyFar is { Body.Id: "far" }, onlyFar?.Body.Id ?? "no hit" );
	}

	static void TestClosestEdge()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var top = MeshRaycast.Raycast( box, new Vec3( 0, 0, 5 ), new Vec3( 0, 0, -1 ) );

		Report.Check( "the top face is there to ask about", top is not null );

		if ( top is not { } hit )
			return;

		var nearEdge = new Vec3( 0.95f, 0f, 1f );
		var found = MeshRaycast.ClosestEdge( box, hit.FaceIndex, nearEdge, out var key, out var closest, out var distance );

		Report.Check( "a point near a rim names an edge", found );
		Report.Check( "that edge is the +x rim of the top face",
			MathF.Abs( box.Positions[key.A].x - 1f ) < 1e-3f && MathF.Abs( box.Positions[key.B].x - 1f ) < 1e-3f,
			$"{box.Positions[key.A]} — {box.Positions[key.B]}" );
		Report.Check( "the closest point sits on that rim", MathF.Abs( closest.x - 1f ) < 1e-3f, closest.ToString() );
		Report.Check( "and the distance is the leftover to the rim", distance < 0.1f, $"{distance}" );

		var centre = MeshRaycast.ClosestEdge( box, hit.FaceIndex, new Vec3( 0, 0, 1 ), out _, out _, out var midDistance );

		Report.Check( "the face centre still has a nearest edge", centre );
		Report.Check( "but it is much further away than a rim click", midDistance > distance + 0.5f,
			$"centre {midDistance}, rim {distance}" );
	}
}
