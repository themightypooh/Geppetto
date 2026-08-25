using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Extrudes that ask the model how far to go rather than being told.
///
/// "Up to face" sits next to "cut" in every CAD tool and reads like it must need a boolean. It does
/// not: both of these are questions about DISTANCE, answered by a raycast, and the solid they
/// produce is an ordinary prism. What a boolean would add is trimming the new solid against the
/// target SURFACE, which is a different thing and is the limitation these tests pin down.
/// </summary>
public static class TerminationTests
{
	public static void Run()
	{
		Report.Section( "termination: up to next stops at the first thing in the way" );
		TestUpToNext();

		Report.Section( "termination: a target that is not parallel" );
		TestAngledTarget();

		Report.Section( "termination: through all clears everything" );
		TestThroughAll();

		Report.Section( "termination: when there is nothing to measure against" );
		TestNothingThere();
	}

	static void TestUpToNext()
	{
		// A plate floating at z = 5, and a post grown up from the origin plane to meet it.
		var studio = new PartStudio();

		var plate = studio.Add( new PrimitiveFeature() );
		plate.SizeX.Value = 10f;
		plate.SizeY.Value = 10f;
		plate.SizeZ.Value = 2f;
		plate.Position.Value = new Vec3( 0f, 0f, 6f ); // spans z 5..7

		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( -1, -1 ), new Vec2( 1, 1 ) );

		var post = studio.Add( new ExtrudeFeature() );
		post.Termination.Index = 1; // Up to next
		post.Result.Index = 1; // its own body, so it can be measured

		var report = studio.Rebuild();

		Report.Check( "it builds", !report.HasErrors, report.ToString() );

		if ( report.HasErrors )
			return;

		var body = studio.Bodies.First( b => b.FeatureId == post.Id ).Mesh;
		var top = body.Positions.Max( p => p.z );

		Report.Check( "the post stops exactly at the plate's underside",
			MathF.Abs( top - 5f ) < 1e-3f, $"reached {top:0.####}, the plate starts at 5" );

		Report.Check( "and starts at the sketch plane",
			MathF.Abs( body.Positions.Min( p => p.z ) ) < 1e-4f );

		Report.Check( "with the volume that implies", MathF.Abs( Volume( body ) - 20f ) < 1e-2f,
			$"{Volume( body ):0.####}, expected 4 x 5" );

		Report.Check( "and no warning, since the target is parallel", post.Warning is null, post.Warning );

		// MOVE THE PLATE AND THE POST FOLLOWS. This is the whole point of measuring rather than
		// typing: the distance is a consequence of the model, not a number that goes stale.
		plate.Position.Value = new Vec3( 0f, 0f, 9f ); // now spans z 8..10
		studio.MarkDirty( plate );
		studio.Rebuild();

		var moved = studio.Bodies.First( b => b.FeatureId == post.Id ).Mesh;

		Report.Check( "moving the plate moves where the post stops",
			MathF.Abs( moved.Positions.Max( p => p.z ) - 8f ) < 1e-3f,
			$"reached {moved.Positions.Max( p => p.z ):0.####}, the plate now starts at 8" );

		// The NEAREST obstruction wins, not the furthest — a solid has to stop at the first thing in
		// the way, and anything beyond it is already hidden behind that.
		//
		// INSERTED ABOVE THE EXTRUDE, not appended. A feature only sees what runs before it, so a
		// block added at the end of the tree is not in the way as far as the extrude is concerned —
		// which is the history model working correctly and caught this test out first time.
		var second = new PrimitiveFeature();
		second.SizeX.Value = 10f;
		second.SizeY.Value = 10f;
		second.SizeZ.Value = 2f;
		second.Position.Value = new Vec3( 0f, 0f, 4f ); // spans z 3..5, closer than the plate

		studio.Insert( studio.Features.IndexOf( post ), second );
		studio.Rebuild();

		var stopped = studio.Bodies.First( b => b.FeatureId == post.Id ).Mesh;

		Report.Check( "a nearer obstruction takes precedence",
			MathF.Abs( stopped.Positions.Max( p => p.z ) - 3f ) < 1e-3f,
			$"reached {stopped.Positions.Max( p => p.z ):0.####}, the nearer block starts at 3" );
	}

	static void TestAngledTarget()
	{
		// A block overhead, TILTED, so the face above the profile slopes. A flat cap cannot meet it,
		// and the interesting question is whether the feature says so or quietly leaves a gap.
		//
		// A Wedge primitive was the obvious choice and is the wrong one: it presents a flat underside
		// to a profile below it, so every ray comes back the same distance and there is nothing to
		// warn about. The slope has to actually face the sketch.
		var studio = new PartStudio();

		var slab = studio.Add( new PrimitiveFeature() );
		slab.SizeX.Value = 12f;
		slab.SizeY.Value = 12f;
		slab.SizeZ.Value = 2f;
		slab.Position.Value = new Vec3( 0f, 0f, 8f );

		var tilt = studio.Add( new TransformFeature() );
		tilt.RotationAxis.Value = new Vec3( 1f, 0f, 0f );
		tilt.RotationAngle.Value = 15f;

		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( -1.5f, -1.5f ), new Vec2( 1.5f, 1.5f ) );

		var post = studio.Add( new ExtrudeFeature() );
		post.Termination.Index = 1;
		post.Result.Index = 1;

		var report = studio.Rebuild();

		Report.Check( "it still builds against a sloped face", !report.HasErrors, report.ToString() );

		if ( report.HasErrors )
			return;

		Report.Check( "and warns that the face is not parallel", post.Warning is not null,
			"no warning — the gap would be silent" );

		Report.Check( "naming both distances so the gap is a number",
			post.Warning is not null && post.Warning.Contains( "between" ), post.Warning ?? "" );

		// It stops at the NEAREST point, which is what keeps it from pushing into the target.
		var body = studio.Bodies.First( b => b.FeatureId == post.Id ).Mesh;
		var top = body.Positions.Max( p => p.z );
		var slabBody = studio.Bodies.First( b => b.FeatureId == slab.Id ).Mesh;

		Report.Check( "and stops short of the target rather than through it",
			top <= slabBody.Positions.Max( p => p.z ) + 1e-3f, $"post reaches {top:0.###}" );
	}

	static void TestThroughAll()
	{
		var studio = new PartStudio();

		var block = studio.Add( new PrimitiveFeature() );
		block.SizeX.Value = 6f;
		block.SizeY.Value = 6f;
		block.SizeZ.Value = 4f;
		block.Position.Value = new Vec3( 0f, 0f, 5f ); // spans z 3..7

		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( -1, -1 ), new Vec2( 1, 1 ) );

		var bar = studio.Add( new ExtrudeFeature() );
		bar.Termination.Index = 2; // Through all
		bar.Result.Index = 1;

		var report = studio.Rebuild();

		Report.Check( "it builds", !report.HasErrors, report.ToString() );

		if ( report.HasErrors )
			return;

		var body = studio.Bodies.First( b => b.FeatureId == bar.Id ).Mesh;
		var top = body.Positions.Max( p => p.z );

		Report.Check( "it reaches past everything in the way", top > 7f,
			$"reached {top:0.###}, the block ends at 7" );

		// NOT EXACTLY ON THE FAR SURFACE. A prism ending flush with a face leaves two coplanar faces
		// touching, which is the case every downstream operation finds hardest — and the one a
		// boolean would have to resolve. Clearing it outright costs nothing.
		Report.Check( "and clears it rather than stopping flush with it", top > 7.1f,
			$"reached {top:0.###}" );

		// It has to follow the model too, same as up to next.
		block.SizeZ.Value = 10f; // now spans z 0..10
		studio.MarkDirty( block );
		studio.Rebuild();

		Report.Check( "growing the block makes it reach further",
			studio.Bodies.First( b => b.FeatureId == bar.Id ).Mesh.Positions.Max( p => p.z ) > 10f );
	}

	static void TestNothingThere()
	{
		// Up to next with an empty studio: there is nothing to measure against, and inventing a
		// distance would be worse than saying so.
		var empty = new PartStudio();
		var sketch = empty.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );

		var lonely = empty.Add( new ExtrudeFeature() );
		lonely.Termination.Index = 1;

		empty.Rebuild();

		Report.Check( "up to next with nothing in the studio is an error",
			lonely.Error is not null, "it built something" );

		Report.Check( "and says what is missing",
			lonely.Error is not null && lonely.Error.Contains( "nothing" ), lonely.Error ?? "" );

		// A body that exists but sits BEHIND the profile is not something to stop at either.
		var behind = new PartStudio();

		var block = behind.Add( new PrimitiveFeature() );
		block.SizeX.Value = block.SizeY.Value = block.SizeZ.Value = 2f;
		block.Position.Value = new Vec3( 0f, 0f, -8f );

		var above = behind.Add( new SketchFeature() );
		above.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

		var upward = behind.Add( new ExtrudeFeature() );
		upward.Termination.Index = 1;

		behind.Rebuild();

		Report.Check( "a body behind the profile does not count as being in the way",
			upward.Error is not null, "it measured against something behind it" );

		Report.Check( "and the message suggests flipping",
			upward.Error is not null && upward.Error.Contains( "flip" ), upward.Error ?? "" );

		// Flipping is exactly what fixes it, which is the check that the advice is worth taking.
		upward.Flip.Value = true;
		behind.MarkDirty( upward );
		behind.Rebuild();

		Report.Check( "and flipping it does fix it", upward.Error is null, upward.Error );
	}

	// --- helpers ------------------------------------------------------------------------------

	static float Volume( PolyMesh mesh )
	{
		var acc = 0f;

		foreach ( var f in mesh.Faces )
			acc += Vec3.Dot( mesh.FaceCentroid( f ), mesh.FaceNormal( f ) ) * mesh.FaceArea( f );

		return acc / 3f;
	}
}
