using System;
using System.Collections.Generic;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Collision, and the convex hull under it.
///
/// A HULL THAT IS SLIGHTLY TOO SMALL IS THE FAILURE THAT MATTERS. Too big and things bump into thin
/// air, which is visible the first time anyone plays; too small and they sink into the wall, which
/// looks like a physics glitch and gets blamed on the engine. So the checks here are about
/// containment: every point of the part inside the hull, and the hull's volume never less than the
/// part's.
/// </summary>
public static class CollisionTests
{
	public static void Run()
	{
		Section( "collision: the convex hull" );
		TestAHullOfABoxIsTheBox();
		TestEveryPointIsInsideItsHull();
		TestAConcavePartGetsABiggerHull();
		TestADegenerateCloudIsRefusedRatherThanMangled();

		Section( "collision: read from the history where it can be" );
		TestPrimitivesComeBackAsPrimitives();
		TestPatternsAndMirrorsCopyTheShapes();
		TestAnythingElseFallsBackToHulls();
		TestARotatedTransformSpoilsItRatherThanBeingIgnored();
		TestASuppressedFeatureIsNotInTheCollision();
	}

	static void TestAHullOfABoxIsTheBox()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var hull = ConvexHull.Build( box.Positions );

		Check( "a box has a hull", hull is not null );
		Check( "with its eight corners and no more", hull!.Value.Points.Count == 8,
			$"{hull.Value.Points.Count} points" );

		// Six quads triangulated is twelve triangles, and a hull that came back with more has kept
		// coplanar splits it should have merged - harmless, but it means the horizon logic is
		// re-adding faces it should have deleted.
		Check( "and twelve triangles", hull.Value.Faces.Count == 12, $"{hull.Value.Faces.Count} faces" );

		var mesh = ConvexHull.ToMesh( box.Positions );

		Check( "the hull mesh encloses the same volume as the box",
			MathF.Abs( MathF.Abs( mesh.SignedVolume() ) - 8f ) < 1e-3f,
			$"{MathF.Abs( mesh.SignedVolume() ):0.####}, wanted 8" );
	}

	static void TestEveryPointIsInsideItsHull()
	{
		// The containment check, on something with curvature and a lot of interior points.
		var sphere = CatmullClark.Subdivide( Primitives.QuadSphere( 1f, 4 ), 1 );
		var hull = ConvexHull.Build( sphere.Positions );

		Check( "a subdivided sphere has a hull", hull is not null );

		var outside = 0;
		var worst = 0f;
		var scale = sphere.BoundsDiagonal;

		foreach ( var p in sphere.Positions )
		{
			foreach ( var face in hull!.Value.Faces )
			{
				var a = hull.Value.Points[face.A];
				var normal = Vec3.Cross( hull.Value.Points[face.B] - a, hull.Value.Points[face.C] - a );

				if ( normal.LengthSquared < 1e-20f )
					continue;

				var distance = Vec3.Dot( normal.Normal, p - a );

				if ( distance > 1e-4f * scale )
				{
					outside++;
					worst = MathF.Max( worst, distance );
					break;
				}
			}
		}

		Check( "every vertex of the part is inside its own hull", outside == 0,
			$"{outside} outside, worst by {worst:0.#####}" );
		// A sphere has no interior vertices - every one of them is ON the hull - so dropping interior
		// points has to be shown on a cloud that actually has some.
		var withInside = new List<Vec3>( Primitives.Box( 2, 2, 2 ).Positions )
		{
			Vec3.Zero,
			new( 0.3f, -0.2f, 0.1f ),
		};

		var trimmed = ConvexHull.Build( withInside );

		Check( "and interior points are dropped rather than kept",
			trimmed is not null && trimmed.Value.Points.Count == 8,
			$"{trimmed?.Points.Count ?? 0} of {withInside.Count}" );
	}

	static void TestAConcavePartGetsABiggerHull()
	{
		// A hull fills a concavity in. That is the approximation, and it has to be stated as an
		// over-estimate rather than discovered as a gap: bigger than the part, never smaller.
		var tube = Primitives.Tube( 1f, 0.6f, 2f, 24 );
		var hull = ConvexHull.ToMesh( tube.Positions );

		Check( "a tube has a hull", hull is not null );

		var partVolume = MathF.Abs( tube.SignedVolume() );
		var hullVolume = MathF.Abs( hull.SignedVolume() );

		Check( "which fills the bore in, so it is bigger", hullVolume > partVolume * 1.2f,
			$"part {partVolume:0.###}, hull {hullVolume:0.###}" );
		Check( "and never smaller than the part", hullVolume >= partVolume - 1e-4f );
	}

	static void TestADegenerateCloudIsRefusedRatherThanMangled()
	{
		// A flat cloud has no volume to enclose, and a hull of one is three coincident triangles.
		// Returning null makes the caller decide; returning that would make the decision look taken.
		var flat = new List<Vec3>
		{
			new( 0, 0, 0 ), new( 1, 0, 0 ), new( 1, 1, 0 ), new( 0, 1, 0 ), new( 0.5f, 0.5f, 0 ),
		};

		Check( "a flat cloud has no hull", ConvexHull.Build( flat ) is null );

		var line = new List<Vec3> { new( 0, 0, 0 ), new( 1, 0, 0 ), new( 2, 0, 0 ), new( 3, 0, 0 ) };

		Check( "nor does a collinear one", ConvexHull.Build( line ) is null );
		Check( "nor three points", ConvexHull.Build( new List<Vec3> { Vec3.Zero, new( 1, 0, 0 ), new( 0, 1, 0 ) } ) is null );
	}

	static PartStudio Box( float size, Vec3 at )
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );

		box.SizeX.Value = size;
		box.SizeY.Value = size;
		box.SizeZ.Value = size;
		box.Position.Value = at;

		return studio;
	}

	static void TestPrimitivesComeBackAsPrimitives()
	{
		// The whole point: a model somebody built out of boxes IS its own collision, exactly, and
		// hulling it would throw that away for no reason.
		var studio = Box( 2f, new Vec3( 1, 0, 0 ) );

		var second = studio.Add( new PrimitiveFeature() );
		second.Shape.Index = 1; // Cylinder
		second.Radius.Value = 0.5f;
		second.SizeZ.Value = 3f;
		second.Position.Value = new Vec3( -2, 0, 0 );

		studio.Rebuild();

		var report = CollisionBuilder.Build( studio );

		Check( "two primitives give two shapes from the history",
			report.FromHistory && report.Shapes.Count == 2, report.ToString() );

		Check( "the box is a box, at its own size and place",
			report.Shapes[0].Kind == CollisionKind.Box
			&& MathF.Abs( report.Shapes[0].Size.x - 1f ) < 1e-4f
			&& MathF.Abs( report.Shapes[0].Position.x - 1f ) < 1e-4f,
			report.Shapes[0].ToString() );

		Check( "and the cylinder is a cylinder, with its radius and half-height",
			report.Shapes[1].Kind == CollisionKind.Cylinder
			&& MathF.Abs( report.Shapes[1].Size.x - 0.5f ) < 1e-4f
			&& MathF.Abs( report.Shapes[1].Size.z - 1.5f ) < 1e-4f,
			report.Shapes[1].ToString() );
	}

	static void TestPatternsAndMirrorsCopyTheShapes()
	{
		var studio = Box( 1f, new Vec3( 2, 0, 0 ) );

		var mirror = studio.Add( new MirrorFeature() );
		mirror.PlaneNormal.Value = new Vec3( 1, 0, 0 );
		mirror.PlanePoint.Value = Vec3.Zero;

		studio.Rebuild();

		var report = CollisionBuilder.Build( studio );

		Check( "a mirror doubles the shapes", report.FromHistory && report.Shapes.Count == 2,
			report.ToString() );
		Check( "and puts the copy on the other side",
			MathF.Abs( report.Shapes[1].Position.x + 2f ) < 1e-4f,
			$"copy at x {report.Shapes[1].Position.x:0.###}" );

		// Keep original off means the mirror REPLACES what it reflected. Collision left on the half
		// that is no longer there would be an invisible wall.
		mirror.KeepOriginal.Value = false;
		studio.MarkDirty( mirror );
		studio.Rebuild();

		var replaced = CollisionBuilder.Build( studio );

		Check( "and dropping the original drops its collision too",
			replaced.Shapes.Count == 1 && MathF.Abs( replaced.Shapes[0].Position.x + 2f ) < 1e-4f,
			replaced.ToString() );
	}

	static void TestAnythingElseFallsBackToHulls()
	{
		// A fillet is not describable as a primitive, so the history stops being a description of the
		// shape and the fallback takes over - saying which feature spoiled it.
		var studio = Box( 2f, Vec3.Zero );
		studio.Add( new FilletFeature() ).Radius.Value = 0.2f;
		studio.Rebuild();

		var report = CollisionBuilder.Build( studio );

		Check( "a filleted box is hulled rather than described", !report.FromHistory, report.ToString() );
		Check( "and the report names what spoiled it",
			report.Reason is not null && report.Reason.Contains( "Fillet" ), report.Reason ?? "" );
		Check( "one hull for the one body",
			report.Shapes.Count == 1 && report.Shapes[0].Kind == CollisionKind.Hull,
			report.ToString() );
		Check( "with real points on it", report.Shapes[0].Points is { Count: > 3 },
			$"{report.Shapes[0].Points?.Count ?? 0} points" );
	}

	static void TestARotatedTransformSpoilsItRatherThanBeingIgnored()
	{
		// A CollisionShape has a position and a size but no orientation. Following the move and
		// dropping the turn would leave a box square while the part it belongs to is at forty
		// degrees, which shows up as something bouncing off thin air.
		var studio = Box( 2f, Vec3.Zero );

		var moved = studio.Add( new TransformFeature() );
		moved.Translate.Value = new Vec3( 0, 0, 5 );

		studio.Rebuild();

		var slid = CollisionBuilder.Build( studio );

		Check( "a plain move is followed", slid.FromHistory
			&& MathF.Abs( slid.Shapes[0].Position.z - 5f ) < 1e-4f, slid.ToString() );

		moved.RotationAngle.Value = 40f;
		studio.MarkDirty( moved );
		studio.Rebuild();

		var turned = CollisionBuilder.Build( studio );

		Check( "a rotation is not", !turned.FromHistory, turned.ToString() );
		Check( "and it says so rather than dropping the rotation",
			turned.Reason is not null && turned.Reason.Contains( "Transform" ), turned.Reason ?? "" );
	}

	static void TestASuppressedFeatureIsNotInTheCollision()
	{
		var studio = Box( 2f, Vec3.Zero );

		var second = studio.Add( new PrimitiveFeature() );
		second.Position.Value = new Vec3( 5, 0, 0 );

		studio.Rebuild();

		Check( "both primitives are in the collision",
			CollisionBuilder.Build( studio ).Shapes.Count == 2 );

		second.Suppressed = true;
		studio.MarkDirty( second );
		studio.Rebuild();

		Check( "suppressing one takes its collision with it",
			CollisionBuilder.Build( studio ).Shapes.Count == 1 );
	}
}
