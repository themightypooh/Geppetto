using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Assigning a material by pointing at one face, which is what the editor's right-click menu does.
///
/// The drawing and the raycast are the editor's problem. What is testable — and what actually goes
/// wrong — is the bookkeeping underneath: a face has to leave the assignment it was in before it
/// joins another, an assignment that loses its last face must not be left in the tree failing, a
/// second click on the same slot must reuse the feature the first one made rather than stacking a
/// new one, and all of it has to land above the rollback bar so the result is visible.
/// </summary>
public static class FaceMenuTests
{
	public static void Run()
	{
		Report.Section( "face menu: one click puts one face on a slot" );
		TestFirstAssignment();

		Report.Section( "face menu: clicking again moves the face rather than stacking" );
		TestReassignment();

		Report.Section( "face menu: back to the default slot" );
		TestClearing();

		Report.Section( "face menu: several faces share one assignment" );
		TestReuse();

		Report.Section( "face menu: while rolled back" );
		TestRollback();

		Report.Section( "face menu: what the menu offers" );
		TestUsedSlots();
	}

	static void TestFirstAssignment()
	{
		var studio = Boxed( out var body );

		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var changed = Assign( studio, body, top, 3 );

		Report.Check( "the edit reports that it did something", changed );

		Report.Check( "it made exactly one assignment",
			studio.Features.OfType<FaceMaterialFeature>().Count() == 1 );

		var report = studio.Rebuild();

		Report.Check( "and it builds", !report.HasErrors, report.ToString() );

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "the face that was pointed at is on the slot",
			mesh.Faces.Count( f => f.Material == 3 ) == 1
			&& mesh.FaceNormal( mesh.Faces.First( f => f.Material == 3 ) ).z > 0.99f );

		Report.Check( "and nothing else moved off the default",
			mesh.Faces.Count( f => f.Material == 0 ) == mesh.FaceCount - 1 );

		// THE POINT OF EDITING THE HISTORY RATHER THAN THE MESH. A slot written straight onto
		// Body.Mesh would survive exactly until the next parameter change.
		var box = studio.Features.OfType<PrimitiveFeature>().Single();
		box.SizeZ.Value = 9f;
		studio.MarkDirty( box );
		studio.Rebuild();

		var rebuilt = studio.Bodies.Single().Mesh;
		var painted = rebuilt.Faces.Where( f => f.Material == 3 ).ToList();

		Report.Check( "it survives the box changing shape", painted.Count == 1 );

		Report.Check( "and rides the face to its new height",
			painted.Count == 1 && MathF.Abs( rebuilt.FaceCentroid( painted[0] ).z - 4.5f ) < 1e-3f,
			painted.Count == 1 ? $"at z {rebuilt.FaceCentroid( painted[0] ).z}" : "" );
	}

	static void TestReassignment()
	{
		var studio = Boxed( out var body );

		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		Assign( studio, body, top, 3 );
		studio.Rebuild();

		body = studio.Bodies.Single();
		top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		Assign( studio, body, top, 5 );
		studio.Rebuild();

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "the face ends up on the slot it was last given",
			mesh.Faces.Count( f => f.Material == 5 ) == 1 );

		Report.Check( "and is no longer on the one it left",
			mesh.Faces.Count( f => f.Material == 3 ) == 0 );

		// The assignment it left had only that one face, so it is gone rather than sitting in the
		// tree with nothing to do. An empty one is an ERROR when it runs, and this one emptied
		// through no fault of the user's.
		Report.Check( "the assignment it emptied was removed with it",
			studio.Features.OfType<FaceMaterialFeature>().Count() == 1,
			$"{studio.Features.OfType<FaceMaterialFeature>().Count()} assignments left" );

		Report.Check( "so nothing in the tree is failing", !studio.Rebuild().HasErrors );

		// FOUR CLICKS, ONE FEATURE. Without detaching first, each click would leave the face in the
		// assignment before it and the tree would grow one per click — invisible on screen, because
		// the last one wins, and all of them saved to the file.
		for ( var slot = 1; slot <= 4; slot++ )
		{
			var current = studio.Bodies.Single();
			Assign( studio, current, FaceIndexFacing( current.Mesh, new Vec3( 0, 0, 1 ) ), slot );
			studio.Rebuild();
		}

		Report.Check( "repeated clicks on one face leave one assignment behind",
			studio.Features.OfType<FaceMaterialFeature>().Count() == 1,
			$"{studio.Features.OfType<FaceMaterialFeature>().Count()} assignments after four clicks" );

		Report.Check( "holding exactly the one face",
			studio.Features.OfType<FaceMaterialFeature>().Single().Faces.Count == 1 );
	}

	static void TestClearing()
	{
		var studio = Boxed( out var body );

		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );
		var side = FaceIndexFacing( body.Mesh, new Vec3( 1, 0, 0 ) );

		Assign( studio, body, top, 2 );
		studio.Rebuild();

		body = studio.Bodies.Single();
		Assign( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 1, 0, 0 ) ), 2 );
		studio.Rebuild();

		Report.Check( "two faces on one slot share one assignment",
			studio.Features.OfType<FaceMaterialFeature>().Count() == 1 );

		Report.Check( "with both faces in it",
			studio.Features.OfType<FaceMaterialFeature>().Single().Faces.Count == 2 );

		// Slot 0 is the absence of an assignment, not an assignment to zero.
		body = studio.Bodies.Single();
		var changed = Assign( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) ), 0 );
		studio.Rebuild();

		Report.Check( "putting a face back on the default is a change", changed );

		Report.Check( "it does not create an assignment for slot 0",
			studio.Features.OfType<FaceMaterialFeature>().All( f => f.Material.Clamped != 0 ) );

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "that face is back on the default", mesh.Faces.Count( f => f.Material == 2 ) == 1 );

		Report.Check( "and the other one is untouched",
			mesh.FaceNormal( mesh.Faces.First( f => f.Material == 2 ) ).x > 0.99f );

		// Clearing the LAST face of an assignment takes the assignment with it, same as moving it.
		body = studio.Bodies.Single();
		Assign( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 1, 0, 0 ) ), 0 );

		Report.Check( "clearing the last face removes the assignment",
			!studio.Features.OfType<FaceMaterialFeature>().Any() );

		Report.Check( "leaving a tree that builds clean", !studio.Rebuild().HasErrors );

		Report.Check( "and a model back on one material",
			studio.Bodies.Single().Mesh.Faces.All( f => f.Material == 0 ) );

		// Clearing a face that was never assigned changes nothing, and has to SAY so — the editor
		// skips the rebuild on a false, and a rebuild per no-op click is a rebuild per click.
		body = studio.Bodies.Single();

		Report.Check( "clearing an unassigned face reports no change",
			!Assign( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) ), 0 ) );
	}

	static void TestReuse()
	{
		// A suppressed assignment is not one to add faces to: it is switched off, so the face would
		// go on the slot and nothing would happen.
		var studio = Boxed( out var body );

		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		Assign( studio, body, top, 6 );
		studio.Rebuild();

		var first = studio.Features.OfType<FaceMaterialFeature>().Single();
		first.Suppressed = true;
		studio.MarkDirty( first );
		studio.Rebuild();

		body = studio.Bodies.Single();
		Assign( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 1, 0, 0 ) ), 6 );

		Report.Check( "a suppressed assignment is not reused",
			studio.Features.OfType<FaceMaterialFeature>().Count() == 2 );

		Report.Check( "the new one is live", 
			studio.Features.OfType<FaceMaterialFeature>().Last().Suppressed == false );

		studio.Rebuild();

		Report.Check( "and only the newly clicked face is painted",
			studio.Bodies.Single().Mesh.Faces.Count( f => f.Material == 6 ) == 1 );
	}

	static void TestRollback()
	{
		// Rolled back two features. A new assignment must land AT the bar, not past it — appended it
		// would not be evaluated, and the click would do nothing with nothing to explain why.
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 4f;

		var bevel = studio.Add( new BevelFeature() );
		var subdivide = studio.Add( new SubdivideFeature() );

		studio.RollbackIndex = 1;
		studio.Rebuild();

		var body = studio.Bodies.Single();
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		Assign( studio, body, top, 4 );

		var assignment = studio.Features.OfType<FaceMaterialFeature>().Single();

		Report.Check( "the new assignment goes above the bar",
			studio.Features.IndexOf( assignment ) == 1,
			$"at index {studio.Features.IndexOf( assignment )}" );

		Report.Check( "and the bar moved down past it", studio.RollbackIndex == 2 );

		Report.Check( "so it is evaluated", studio.EffectiveCount == 2 );

		studio.Rebuild();

		Report.Check( "and the face is actually painted",
			studio.Bodies.Single().Mesh.Faces.Count( f => f.Material == 4 ) == 1 );

		Report.Check( "the features below the bar are still there, in order",
			studio.Features[2] == bevel && studio.Features[3] == subdivide );

		// An assignment BELOW the bar is not one to reuse: adding to it would put the face somewhere
		// that is not running.
		studio.RollbackIndex = 1;
		studio.Rebuild();

		body = studio.Bodies.Single();
		Assign( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 1, 0, 0 ) ), 4 );

		Report.Check( "an assignment below the bar is not reused",
			studio.Features.OfType<FaceMaterialFeature>().Count() == 2,
			$"{studio.Features.OfType<FaceMaterialFeature>().Count()} assignments" );

		studio.Rebuild();

		Report.Check( "and the click that made it is visible",
			studio.Bodies.Single().Mesh.Faces.Count( f => f.Material == 4 ) == 1 );
	}

	static void TestUsedSlots()
	{
		var studio = Boxed( out var body );

		Report.Check( "a fresh studio uses no slots", !FaceMaterialEdit.UsedSlots( studio ).Any() );

		Assign( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) ), 12 );

		Report.Check( "an assignment puts its slot in the list",
			FaceMaterialEdit.UsedSlots( studio ).SequenceEqual( new[] { 12 } ),
			string.Join( ", ", FaceMaterialEdit.UsedSlots( studio ) ) );

		// A NAMED slot counts too, even with nothing on it. Someone who named slot 40 in the file
		// must be able to find it again on the menu, and the name is the only trace of it.
		studio.MaterialNames[40] = "anodised";

		Report.Check( "so does a slot that only has a name",
			FaceMaterialEdit.UsedSlots( studio ).SequenceEqual( new[] { 12, 40 } ),
			string.Join( ", ", FaceMaterialEdit.UsedSlots( studio ) ) );

		Report.Check( "and the list comes back in order, without repeats",
			FaceMaterialEdit.UsedSlots( studio ).Count() == 2 );
	}

	// --- helpers ------------------------------------------------------------------------------

	/// <summary>What the right-click menu does, minus the raycast: capture the face the cursor is
	/// over, then hand it to the edit.</summary>
	static bool Assign( PartStudio studio, Body body, int faceIndex, int slot )
	{
		var reference = FacePlane.Capture( body, faceIndex, body.Mesh.FaceCentroid( body.Mesh.Faces[faceIndex] ) );

		return FaceMaterialEdit.Assign( studio, body.Id, faceIndex, reference, slot );
	}

	static PartStudio Boxed( out Body body )
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 3f;
		box.SizeZ.Value = 2f;

		studio.Rebuild();
		body = studio.Bodies.Single();

		return studio;
	}

	static int FaceIndexFacing( PolyMesh mesh, Vec3 direction )
	{
		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( Vec3.Dot( mesh.FaceNormal( mesh.Faces[i] ), direction.Normal ) > 0.99f )
				return i;
		}

		return -1;
	}
}
