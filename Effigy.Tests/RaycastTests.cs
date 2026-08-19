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
}
