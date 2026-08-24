using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Building a part up out of several extrudes, and getting one part out of it.
///
/// The behaviour this replaces: every extrude made its own body, so three bosses on a block listed
/// as four separate parts. That is not what "I built this out of four extrudes" means to anyone,
/// and it made the parts list useless on exactly the models it was there for.
///
/// The rule is the sketch's attachment, not proximity: a sketch drawn ON A FACE of a body adds to
/// that body, a sketch on a global plane starts a new one. It needs no parameter set either way,
/// and it cannot pick the wrong body, because the answer was decided when the sketch was placed.
///
/// What merging does NOT do is cut the interface — see SketchConsumingFeature.Emit. The tests below
/// therefore assert on total enclosed volume and body count, and deliberately not on manifoldness,
/// because the merged mesh is not manifold along the join and pretending otherwise here would
/// enshrine an expectation the code does not meet.
/// </summary>
public static class MergeTests
{
	public static void Run()
	{
		Report.Section( "merge: extruding off a face builds up one part" );
		TestBossesMergeIn();

		Report.Section( "merge: what starts a new part instead" );
		TestNewBodyCases();

		Report.Section( "merge: the explicit settings" );
		TestExplicitResult();

		Report.Section( "merge: identity survives it" );
		TestIdentity();
	}

	static void TestBossesMergeIn()
	{
		// A 6x4x2 block, then three separate 1x1x1 bosses off its top face. One part, and its
		// volume is the block plus all three: 48 + 3.
		var studio = new PartStudio();

		var block = studio.Add( new PrimitiveFeature() );
		block.SizeX.Value = 6f;
		block.SizeY.Value = 4f;
		block.SizeZ.Value = 2f;
		studio.Rebuild();

		var blockId = studio.Bodies.Single().Id;

		// Captured ONCE, before any boss exists, and reused — which is also what actually happens
		// when someone draws three sketches on the same face. Re-capturing inside the loop finds the
		// highest +Z face instead, which after the first boss is that boss's own top, and the three
		// quietly stack into a tower. The reference resolving to the right face while a boss stands
		// on it is half of what is being tested.
		var blockTop = TopFaceOf( studio, blockId );

		for ( var i = 0; i < 3; i++ )
		{
			var sketch = studio.Add( new SketchFeature() );
			sketch.Face = blockTop;

			var x = -2f + i * 2f;
			sketch.Sketch.AddRectangle( new Vec2( x, -0.5f ), new Vec2( x + 1f, 0.5f ) );

			studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
			studio.Rebuild();
		}

		var report = studio.Rebuild();

		Report.Check( "it builds", !report.HasErrors, report.ToString() );

		Report.Check( "three bosses off the same block leave ONE part, not four",
			studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );

		var volume = EnclosedVolume( studio.Bodies.Single().Mesh );

		Report.Check( "and that part measures the block plus all three bosses",
			MathF.Abs( volume - 51f ) < 1e-2f, $"enclosed volume {volume:0.####}, expected 48 + 3" );

		// Volume alone would pass with a boss merged in at the wrong height, so check the part now
		// reaches exactly one unit above the block's top face.
		var top = studio.Bodies.Single().Mesh.Positions.Max( p => p.z );

		Report.Check( "the part stands a unit proud of the block's top face",
			MathF.Abs( top - 2f ) < 1e-3f, $"top at {top}, block top is 1" );

		// A boss on a boss: the second sketch attaches to a face of the merged body, which by then
		// is the same body id it always was. This is the case that breaks if merging invalidates
		// the face references built on it.
		var stacked = studio.Add( new SketchFeature() );
		stacked.Face = TopFaceOf( studio, blockId );
		stacked.Sketch.AddRectangle( new Vec2( -0.25f, -0.25f ), new Vec2( 0.25f, 0.25f ) );
		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;

		var stackedReport = studio.Rebuild();

		Report.Check( "building on top of what was already merged still works",
			!stackedReport.HasErrors, stackedReport.ToString() );

		Report.Check( "and is still one part", studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );
	}

	static void TestNewBodyCases()
	{
		// A sketch on a global plane is not attached to anything, so it starts its own part even
		// with a body already in the studio. "Until a new sketch is extruded off the mass" — this is
		// the other half of that.
		var studio = new PartStudio();

		var block = studio.Add( new PrimitiveFeature() );
		block.SizeX.Value = block.SizeY.Value = block.SizeZ.Value = 2f;

		var loose = studio.Add( new SketchFeature() );
		loose.PlaneOffset.Value = 10f;
		loose.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;

		var report = studio.Rebuild();

		Report.Check( "a sketch on a global plane starts its own part",
			!report.HasErrors && studio.Bodies.Count == 2, $"{studio.Bodies.Count} bodies" );

		// And moving a sketch back off a face must stop it merging. The attachment is republished
		// on every rebuild, so a stale one would keep merging into a body it no longer touches.
		var studio2 = new PartStudio();
		var host = studio2.Add( new PrimitiveFeature() );
		host.SizeX.Value = host.SizeY.Value = host.SizeZ.Value = 2f;
		studio2.Rebuild();

		var attached = studio2.Add( new SketchFeature() );
		attached.Face = TopFaceOf( studio2, studio2.Bodies[0].Id );
		attached.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );
		studio2.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		studio2.Rebuild();

		Report.Check( "attached, it merges", studio2.Bodies.Count == 1, $"{studio2.Bodies.Count} bodies" );

		attached.Face = null;
		attached.PlaneOffset.Value = 5f;
		studio2.MarkDirty( attached );
		studio2.Rebuild();

		Report.Check( "moved back onto a plane, it stops merging",
			studio2.Bodies.Count == 2, $"{studio2.Bodies.Count} bodies" );
	}

	static void TestExplicitResult()
	{
		var studio = new PartStudio();

		var block = studio.Add( new PrimitiveFeature() );
		block.SizeX.Value = block.SizeY.Value = block.SizeZ.Value = 2f;
		studio.Rebuild();

		var sketch = studio.Add( new SketchFeature() );
		sketch.Face = TopFaceOf( studio, studio.Bodies[0].Id );
		sketch.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

		var boss = studio.Add( new ExtrudeFeature() );
		boss.Distance.Value = 1f;
		boss.Result.Index = 1; // New body

		studio.Rebuild();

		Report.Check( "New body overrides the attachment and keeps them apart",
			studio.Bodies.Count == 2, $"{studio.Bodies.Count} bodies" );

		boss.Result.Index = 0; // back to Auto
		studio.MarkDirty( boss );
		studio.Rebuild();

		Report.Check( "and Auto merges it again", studio.Bodies.Count == 1,
			$"{studio.Bodies.Count} bodies" );

		// Explicit Add with a sketch on a global plane: one body in the studio is unambiguous, so
		// it is used. This is the "sketch over the top of the only part" case.
		var single = new PartStudio();
		var only = single.Add( new PrimitiveFeature() );
		only.SizeX.Value = only.SizeY.Value = only.SizeZ.Value = 2f;

		var overSketch = single.Add( new SketchFeature() );
		overSketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );

		var adding = single.Add( new ExtrudeFeature() );
		adding.Distance.Value = 3f;
		adding.Result.Index = 2; // Add

		var singleReport = single.Rebuild();

		Report.Check( "Add with one body in the studio uses that body",
			!singleReport.HasErrors && single.Bodies.Count == 1, singleReport.ToString() );

		// Two bodies and no attachment: there is no way to tell which was meant, and guessing is
		// how a boss silently lands on the wrong part. It has to say so.
		var ambiguous = new PartStudio();
		var a = ambiguous.Add( new PrimitiveFeature() );
		a.SizeX.Value = a.SizeY.Value = a.SizeZ.Value = 2f;
		var b = ambiguous.Add( new PrimitiveFeature() );
		b.SizeX.Value = b.SizeY.Value = b.SizeZ.Value = 1f;
		b.Position.Value = new Vec3( 8f, 0f, 0f );

		ambiguous.Add( new SketchFeature() ).Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );

		var guessing = ambiguous.Add( new ExtrudeFeature() );
		guessing.Distance.Value = 1f;
		guessing.Result.Index = 2; // Add

		var ambiguousReport = ambiguous.Rebuild();

		Report.Check( "Add with two bodies and no attachment refuses rather than guessing",
			ambiguousReport.HasErrors, "it picked one" );

		Report.Check( "and the error says what to do about it",
			guessing.Error is not null && guessing.Error.Contains( "which" ),
			guessing.Error ?? "no error" );
	}

	static void TestIdentity()
	{
		// Merging must not change the host body's id. Everything built on that body — every face
		// reference, every body selection — is holding the id, and a merge that renamed it would
		// break all of them at once, which is the exact failure feature-derived ids were introduced
		// to stop.
		var studio = new PartStudio();

		var block = studio.Add( new PrimitiveFeature() );
		block.SizeX.Value = 4f;
		block.SizeY.Value = 4f;
		block.SizeZ.Value = 2f;
		studio.Rebuild();

		var idBefore = studio.Bodies.Single().Id;
		var featureBefore = studio.Bodies.Single().FeatureId;

		var sketch = studio.Add( new SketchFeature() );
		sketch.Face = TopFaceOf( studio, idBefore );
		sketch.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );
		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		studio.Rebuild();

		var merged = studio.Bodies.Single();

		Report.Check( "the merged part keeps the host's id", merged.Id == idBefore,
			$"{idBefore} became {merged.Id}" );

		Report.Check( "and still names the feature that first made it",
			merged.FeatureId == featureBefore, $"{featureBefore} became {merged.FeatureId}" );

		// A body selection made before the merge still matches afterwards, which is the practical
		// consequence of the id holding.
		var selection = new BodySelectionParam( "Bodies" );
		selection.BodyIds.Add( idBefore );

		Report.Check( "so a selection made before the merge still matches the part",
			selection.Matches( merged ) );

		// Rebuilding twice must not merge twice. Bodies are rebuilt from scratch each time, but a
		// merge that appended into a cached mesh rather than a fresh one would double the volume on
		// every rebuild — and would look completely normal in the viewport.
		var first = EnclosedVolume( studio.Bodies.Single().Mesh );

		studio.MarkDirty( 0 );
		studio.Rebuild();
		studio.MarkDirty( 0 );
		studio.Rebuild();

		var third = EnclosedVolume( studio.Bodies.Single().Mesh );

		Report.Check( "rebuilding repeatedly does not merge the same boss again and again",
			MathF.Abs( third - first ) < 1e-3f, $"{first:0.####} became {third:0.####}" );

		// Same check on the incremental path: edit the LAST feature so everything above it is
		// restored from the snapshot cache rather than re-run.
		var boss = (ExtrudeFeature)studio.Features.Last();
		boss.Distance.Value = 2f;
		studio.MarkDirty( boss );
		studio.Rebuild();

		var taller = EnclosedVolume( studio.Bodies.Single().Mesh );

		Report.Check( "and an incremental rebuild resumes with the attachment intact",
			studio.Bodies.Count == 1 && MathF.Abs( taller - 34f ) < 1e-2f,
			$"{studio.Bodies.Count} bodies, volume {taller:0.####}, expected 32 + 2" );
	}

	// --- helpers ------------------------------------------------------------------------------

	static FaceRef TopFaceOf( PartStudio studio, string bodyId )
	{
		var body = studio.Bodies.First( b => b.Id == bodyId );
		var mesh = body.Mesh;

		var top = mesh.Faces
			.Select( f => (Face: f, Normal: mesh.FaceNormal( f ), Centroid: mesh.FaceCentroid( f )) )
			.Where( t => t.Normal.z > 0.99f )
			.OrderByDescending( t => t.Centroid.z )
			.First();

		return FacePlane.Capture( body, mesh.Faces.IndexOf( top.Face ), top.Centroid );
	}

	static float EnclosedVolume( PolyMesh mesh )
	{
		var acc = 0f;

		foreach ( var f in mesh.Faces )
			acc += Vec3.Dot( mesh.FaceCentroid( f ), mesh.FaceNormal( f ) ) * mesh.FaceArea( f );

		return acc / 3f;
	}
}
