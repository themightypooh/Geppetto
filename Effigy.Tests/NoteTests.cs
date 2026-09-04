using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Grease-pencil notes.
///
/// THE FIRST SECTION IS THE POINT OF THE FILE. A note is annotation, and the promise it makes is
/// that it can never end up in an exported model — not in the OBJ, not in the DMX the rigged path
/// writes, not in the collision hull. That promise is currently kept by construction, because notes
/// hang off PartStudio rather than off the feature list, so no writer has to remember to skip them.
/// Construction is the strongest way to keep it and the easiest to undo by accident: one future
/// change that walks "everything in the studio" instead of "everything in Features" would quietly
/// weld somebody's handwriting onto their model. These tests are what would notice.
///
/// The rest is the ordinary business of a stroke tool — where samples land, what undo means, the
/// round trip through the document — verified in the kernel, where it can run without an engine.
/// </summary>
public static class NoteTests
{
	public static void Run()
	{
		Report.Section( "notes: nothing about them reaches an exported mesh" );
		TestNotesNeverExport();

		Report.Section( "notes: they survive the document round trip" );
		TestRoundTrip();

		Report.Section( "notes: a rebuild leaves them alone" );
		TestRebuildKeepsNotes();

		Report.Section( "notes: strokes land where the ray points" );
		TestProjection();

		Report.Section( "notes: sampling, committing and erasing" );
		TestStrokes();

		Report.Section( "notes: undo and redo" );
		TestUndo();

		Report.Section( "notes: the stroke scales to the part, not to a guess" );
		TestScaleTo();
	}

	/// <summary>A studio with one body in it, so there is a surface to draw on and a mesh to
	/// compare.</summary>
	static PartStudio Boxed()
	{
		var studio = new PartStudio();

		studio.Add( new PrimitiveFeature() );
		studio.Rebuild();

		return studio;
	}

	// --- the guarantee -------------------------------------------------------------------------

	static void TestNotesNeverExport()
	{
		var studio = Boxed();

		var before = studio.ToMesh();
		var beforeVertices = before.VertexCount;
		var beforeFaces = before.FaceCount;
		var beforeObj = ObjText( before );

		// Notes drawn all over the part, including one running straight through the middle of it,
		// which is the case a naive "merge everything" would show up in most obviously.
		studio.Notes.Add( new Note
		{
			Text = "this face is proud",
			Color = 1,
			Points = { new Vec3( -50, -50, -50 ), new Vec3( 0, 0, 0 ), new Vec3( 50, 50, 50 ) },
		} );

		studio.Notes.Add( new Note
		{
			Text = "ask about the hinge",
			Color = 3,
			Points = { new Vec3( 10, 0, 0 ), new Vec3( 10, 0, 20 ) },
		} );

		studio.Rebuild();

		var after = studio.ToMesh();

		Report.Check( "ToMesh has the same vertex count with notes as without",
			after.VertexCount == beforeVertices, $"{beforeVertices} -> {after.VertexCount}" );

		Report.Check( "ToMesh has the same face count with notes as without",
			after.FaceCount == beforeFaces, $"{beforeFaces} -> {after.FaceCount}" );

		Report.Check( "ToVisibleMesh has the same vertex count too",
			studio.ToVisibleMesh().VertexCount == beforeVertices );

		var (withBodies, ranges) = studio.ToMeshWithBodies();

		Report.Check( "the rigged path's mesh is unchanged as well",
			withBodies.VertexCount == beforeVertices );

		Report.Check( "no extra body range appears for a note",
			ranges.Count == studio.Bodies.Count, $"{ranges.Count} ranges for {studio.Bodies.Count} bodies" );

		// The written file, not just the counts. This is the artifact that actually ships, and it
		// is the one place a stray vertex would be visible to the person who opened it.
		var afterObj = ObjText( after );

		Report.Check( "the exported OBJ is byte-for-byte what it was before the notes",
			afterObj == beforeObj, "the OBJ changed when notes were added" );

		Report.Check( "no note's text appears anywhere in the OBJ",
			!afterObj.Contains( "hinge", StringComparison.OrdinalIgnoreCase )
			&& !afterObj.Contains( "proud", StringComparison.OrdinalIgnoreCase ) );

		// Notes are not features, which is the structural reason for all of the above. Asserted
		// directly so a failure says WHY rather than only that a count moved.
		Report.Check( "notes are not in the feature list",
			studio.Features.Count == 1, $"{studio.Features.Count} features" );

		Report.Check( "a rebuild does not evaluate a note",
			studio.Rebuild().FeaturesEvaluated <= studio.Features.Count );
	}

	static string ObjText( PolyMesh mesh )
	{
		var path = Path.Combine( Path.GetTempPath(), $"effigy_note_{Guid.NewGuid():N}.obj" );

		try
		{
			ObjWriter.WriteFile( mesh, path, "effigy_export" );

			return File.ReadAllText( path );
		}
		finally
		{
			try { File.Delete( path ); } catch { /* a temp file that will not delete is not a test failure */ }
		}
	}

	// --- the document --------------------------------------------------------------------------

	static void TestRoundTrip()
	{
		var studio = Boxed();

		studio.Notes.Add( new Note
		{
			Text = "chamfer this before it ships",
			Color = 2,
			Width = 0.75f,
			Points = { new Vec3( 1.5f, -2.25f, 3f ), new Vec3( 4f, 0f, 3f ), new Vec3( 7.125f, 2f, 3f ) },
		} );

		studio.Notes.Add( new Note
		{
			Color = 0,
			Points = { new Vec3( 0, 0, 0 ), new Vec3( 1, 1, 1 ) },
		} );

		var back = StudioDocument.Read( StudioDocument.Write( studio ) );

		Report.Check( "both notes come back", back.Notes.Count == 2, $"{back.Notes.Count}" );

		if ( back.Notes.Count != 2 )
			return;

		var a = back.Notes[0];

		Report.Check( "the caption survives", a.Text == "chamfer this before it ships", a.Text );
		Report.Check( "the colour survives", a.Color == 2, $"{a.Color}" );
		Report.Check( "the width survives", Math.Abs( a.Width - 0.75f ) < 1e-6f, $"{a.Width}" );
		Report.Check( "every point survives, in order",
			a.Points.Count == 3 && Same( a.Points[0], new Vec3( 1.5f, -2.25f, 3f ) )
			&& Same( a.Points[2], new Vec3( 7.125f, 2f, 3f ) ) );

		Report.Check( "a note with no caption comes back with none",
			string.IsNullOrEmpty( back.Notes[1].Text ), back.Notes[1].Text );

		// Written twice, identical both times — the rule the whole format follows, so a diff
		// between two saves means something changed.
		//
		// The rollback index is levelled first because Read clamps an unrolled int.MaxValue down to
		// the feature count, so the two differ on that line for reasons that have nothing to do
		// with notes. Levelling it rather than comparing a substring keeps this a whole-file
		// comparison, which is what would catch a note block written in a different order.
		studio.RollbackIndex = studio.Features.Count;
		back.RollbackIndex = back.Features.Count;

		Report.Check( "writing the reloaded studio gives the same bytes",
			StudioDocument.Write( back ) == StudioDocument.Write( studio ) );

		// A document from before notes existed still loads, and loads with none.
		var old = StudioDocument.Read( "effigy 1\nrollback 2147483647\nfeature PrimitiveFeature\n\tid a\n\tsuppressed 0\n\tvisible 1\nend\n" );

		Report.Check( "a document with no notes in it loads with an empty list",
			old.Notes is { Count: 0 } );

		// The features still parse after a note block, which is the thing a mis-counted block
		// terminator would break.
		Report.Check( "the features are still there alongside the notes",
			back.Features.Count == studio.Features.Count );

		var unterminated = false;

		try
		{
			StudioDocument.Read( "effigy 1\nnote 0 0.4\n\tp 0 0 0\n" );
		}
		catch ( InvalidDataException )
		{
			unterminated = true;
		}

		Report.Check( "an unterminated note block is refused rather than half-read", unterminated );
	}

	static void TestRebuildKeepsNotes()
	{
		var studio = Boxed();

		studio.Notes.Add( new Note { Text = "keep me", Points = { Vec3.Zero, new Vec3( 1, 0, 0 ) } } );

		// The edits that throw the most away: a new feature, a full dirty, a rollback.
		studio.Add( new PrimitiveFeature() );
		studio.MarkAllDirty();
		studio.Rebuild();
		studio.RollbackIndex = 1;
		studio.Rebuild();

		Report.Check( "the note is still there after rebuilds and a rollback",
			studio.Notes.Count == 1 && studio.Notes[0].Text == "keep me" );

		// Rolled back past the feature the note was drawn over, the note stays — it is not owned by
		// a feature and has no reason to disappear with one.
		Report.Check( "rollback does not hide notes", studio.Notes.Count == 1 );
	}

	// --- the session ---------------------------------------------------------------------------

	static void TestProjection()
	{
		var studio = Boxed();
		var session = new NoteSession( studio.Notes ) { Pivot = Vec3.Zero };

		session.SetBodies( studio.Bodies );

		// Straight at the part from far out on +x. PrimitiveFeature's default box straddles the
		// origin, so this must land on its near face rather than sailing through.
		var origin = new Vec3( 500, 0, 0 );
		var toward = new Vec3( -1, 0, 0 );
		var onSurface = session.Project( origin, toward );

		Report.Check( "a ray at the part lands on it", onSurface is not null );

		if ( onSurface is { } surface )
		{
			var hit = MeshRaycast.Raycast( studio.Bodies, origin, toward );

			Report.Check( "the sample sits in front of the face, not in it",
				hit is not null && surface.x > hit.Value.Hit.Point.x - 1e-3f,
				$"sample x {surface.x}" );

			Report.Check( "and only just in front of it — one Lift, not a Lift per axis",
				hit is not null && Math.Abs( (surface - hit.Value.Hit.Point).Length - session.Lift ) < 1e-3f );
		}

		// A ray that misses everything still draws, on the plane through the pivot. This is the
		// case a sculpt brush refuses and a note must not: writing beside a part is the job.
		var missOrigin = new Vec3( 500, 400, 0 );
		var missed = session.Project( missOrigin, toward );

		Report.Check( "a ray past the part still gives a point", missed is not null );

		if ( missed is { } air )
		{
			Report.Check( "and that point is at the pivot's depth along the ray",
				Math.Abs( air.x ) < 1e-2f, $"x {air.x}" );
		}

		Report.Check( "a degenerate direction gives nothing rather than a NaN",
			session.Project( origin, Vec3.Zero ) is null );
	}

	static void TestStrokes()
	{
		var studio = Boxed();
		var session = new NoteSession( studio.Notes ) { Pivot = Vec3.Zero, Color = 4, Width = 0.9f };

		session.SetBodies( studio.Bodies );

		var origin = new Vec3( 500, 0, 0 );

		Report.Check( "a stroke starts", session.BeginStroke( origin, new Vec3( -1, 0, 0 ) ) );
		Report.Check( "and it is running", session.IsStroking );

		// The same ray again: no travel, so no sample. This is what keeps a stationary mouse from
		// filling the file with a thousand copies of one point.
		Report.Check( "a repeat of the same ray adds nothing",
			!session.MoveTo( origin, new Vec3( -1, 0, 0 ) ) );

		// Sweep across the face. Steps far enough apart to clear Spacing.
		for ( var i = 1; i <= 8; i++ )
			session.MoveTo( origin, new Vec3( -50, i * 2f, 0 ) );

		var points = session.Working.Points.Count;

		Report.Check( "a sweep adds samples", points > 2, $"{points} points" );

		var committed = session.EndStroke();

		Report.Check( "the stroke commits", committed is not null );
		Report.Check( "and lands in the studio's own list",
			studio.Notes.Count == 1 && ReferenceEquals( studio.Notes[0], committed ) );
		Report.Check( "wearing the session's colour and width",
			committed is { Color: 4 } && Math.Abs( committed.Width - 0.9f ) < 1e-6f );
		Report.Check( "the session is no longer stroking", !session.IsStroking );

		// A click that never moved is not a note. Committing those leaves invisible pins nobody can
		// find to erase.
		session.BeginStroke( origin, new Vec3( -1, 0, 0 ) );

		Report.Check( "a click with no travel commits nothing", session.EndStroke() is null );
		Report.Check( "and leaves the list alone", studio.Notes.Count == 1 );

		// Cancelling mid-stroke throws the working note away rather than committing it.
		session.BeginStroke( origin, new Vec3( -1, 0, 0 ) );
		session.MoveTo( origin, new Vec3( -50, 6f, 0 ) );
		session.CancelStroke();

		Report.Check( "a cancelled stroke is not committed", studio.Notes.Count == 1 );
		Report.Check( "and is not left running", !session.IsStroking );

		// Erasing: aim down the ray the stroke was drawn along.
		Report.Check( "a note under the cursor is picked",
			session.Pick( origin, new Vec3( -50, 4f, 0 ) ) is not null );

		Report.Check( "a ray nowhere near one picks nothing",
			session.Pick( new Vec3( 0, 0, 900 ), new Vec3( 0, 1, 0 ) ) is null );

		Report.Check( "erasing removes it", session.Erase( origin, new Vec3( -50, 4f, 0 ) ) );
		Report.Check( "and the list is empty again", studio.Notes.Count == 0 );
		Report.Check( "erasing nothing is a no-op rather than a throw",
			!session.Erase( new Vec3( 0, 0, 900 ), new Vec3( 0, 1, 0 ) ) );
	}

	static void TestUndo()
	{
		var studio = Boxed();
		var session = new NoteSession( studio.Notes ) { Pivot = Vec3.Zero };

		session.SetBodies( studio.Bodies );

		Report.Check( "nothing to undo on a fresh session", !session.CanUndo );

		var first = Draw( session, 2f );
		var second = Draw( session, 8f );

		Report.Check( "two strokes are in", studio.Notes.Count == 2 );
		Report.Check( "and undo is offered", session.CanUndo );

		session.Undo();

		Report.Check( "undo takes the last stroke back",
			studio.Notes.Count == 1 && ReferenceEquals( studio.Notes[0], first ) );

		session.Redo();

		Report.Check( "redo puts it back",
			studio.Notes.Count == 2 && ReferenceEquals( studio.Notes[1], second ) );

		// Undo an ERASE, which is the direction that goes wrong when undo and redo are written as
		// two switches that have to agree.
		session.Remove( first );

		Report.Check( "the first stroke is gone", studio.Notes.Count == 1 );

		session.Undo();

		Report.Check( "undoing an erase restores it", studio.Notes.Count == 2 );
		Report.Check( "at the index it was erased from",
			ReferenceEquals( studio.Notes[0], first ), "it came back in the wrong place" );

		// A caption is undoable too.
		session.SetText( second, "wrong" );
		session.SetText( second, "right" );

		Report.Check( "the caption is set", second.Text == "right" );

		session.Undo();

		Report.Check( "undo takes the caption back one step", second.Text == "wrong", second.Text );

		session.Undo();

		Report.Check( "and again to no caption", string.IsNullOrEmpty( second.Text ), second.Text );

		// A new edit closes off the redo branch, which is what everyone expects and what a stack
		// that is only ever pushed to gets wrong.
		session.Redo();
		Draw( session, 14f );

		Report.Check( "a new stroke clears the redo stack", !session.CanRedo );

		while ( session.CanUndo )
			session.Undo();

		Report.Check( "undoing everything empties the list", studio.Notes.Count == 0,
			$"{studio.Notes.Count} left" );
	}

	static Note Draw( NoteSession session, float y )
	{
		var origin = new Vec3( 500, 0, 0 );

		session.BeginStroke( origin, new Vec3( -50, y, 0 ) );

		for ( var i = 1; i <= 4; i++ )
			session.MoveTo( origin, new Vec3( -50, y + i * 2f, 0 ) );

		return session.EndStroke();
	}

	/// <summary>
	/// The distances a stroke uses track the size of the model.
	///
	/// THIS IS A REGRESSION TEST FOR A REAL BUG. Spacing, Lift and PickRadius shipped as constants
	/// tuned for a part tens of units across. Effigy's units are dimensionless and a default
	/// primitive is ONE unit across (PolyMesh.BoundsDiagonal says so), so on an ordinary box
	/// PickRadius was wider than the whole model: every press after the first note landed on that
	/// note and opened its caption box instead of starting a stroke. The tool looked like it had
	/// stopped drawing.
	///
	/// The bar is relational rather than exact - the fractions are a judgement call and may be
	/// retuned - so this asserts the PROPERTIES that made the constants wrong, not the numbers.
	/// </summary>
	static void TestScaleTo()
	{
		var session = new NoteSession( new List<Note>() );

		session.ScaleTo( 1f );

		var spacing = session.Spacing;
		var lift = session.Lift;
		var pick = session.PickRadius;

		// The failure that started it: a pick radius you cannot get outside of on a default part.
		Report.Check( "pick radius is well inside a one-unit part", pick < 0.25f, $"{pick}" );

		Report.Check( "a one-unit part gets many samples across it", 1f / spacing > 20f,
			$"spacing {spacing} gives {1f / spacing:0} samples" );

		Report.Check( "but not absurdly many", 1f / spacing < 400f, $"spacing {spacing}" );

		Report.Check( "the lift is small enough to read as on the surface", lift < 0.02f, $"{lift}" );

		Report.Check( "and big enough to be off it", lift > 0f, $"{lift}" );

		// Ordering the three have to keep whatever the fractions become: you must be able to aim at
		// a stroke more loosely than you sampled it, and the lift must not dominate the spacing.
		Report.Check( "pick radius is looser than the sampling", pick > spacing, $"{pick} vs {spacing}" );
		Report.Check( "the lift is smaller than the sampling", lift < spacing, $"{lift} vs {spacing}" );

		// A room-sized part: everything scales with it rather than staying put.
		session.ScaleTo( 200f );

		Report.Check( "a 200-unit part scales the spacing up", session.Spacing > spacing * 100f,
			$"{session.Spacing}" );
		Report.Check( "and the pick radius with it", session.PickRadius > pick * 100f,
			$"{session.PickRadius}" );
		Report.Check( "keeping the same ordering", session.PickRadius > session.Spacing
			&& session.Lift < session.Spacing );

		// An empty studio has no bounds to measure, and still has to be drawable.
		session.ScaleTo( 0f );

		Report.Check( "an empty studio falls back rather than collapsing to zero",
			session.Spacing > 0f && session.PickRadius > 0f, $"spacing {session.Spacing}" );

		session.ScaleTo( -5f );

		Report.Check( "and a nonsense diagonal does too",
			session.Spacing > 0f && session.PickRadius > 0f, $"spacing {session.Spacing}" );

		// Width is pixels, not world units, so it is the one thing ScaleTo must NOT touch - a note
		// that gets thinner as the part gets bigger is a note you cannot read.
		var thickness = session.Width;

		session.ScaleTo( 500f );

		Report.Check( "width is a screen thickness and does not scale with the model",
			Math.Abs( session.Width - thickness ) < 1e-6f, $"{thickness} -> {session.Width}" );
	}

	static bool Same( Vec3 a, Vec3 b ) => (a - b).Length < 1e-4f;
}
