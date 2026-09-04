using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// Grease-pencil notes in the viewport: rays in, scribbles out.
///
/// AS THIN AS EffigyViewport.Sculpting.cs, AND FOR THE SAME REASON. Every decision — where a sample
/// lands in depth, whether the pointer travelled far enough to earn one, which note an erase click
/// is aiming at, what undo means — is in <see cref="NoteSession"/> in the kernel, where NoteTests
/// runs it without an engine. This file turns Vector3 into Vec3, calls four methods, and draws
/// lines.
///
/// NOTES DRAW WHETHER OR NOT THE TOOL IS ARMED, which is the one behaviour here that is not
/// borrowed from sculpting. A sculpt brush ring is feedback about a mode you are in; a note is a
/// message you left yourself, and a message that is only visible while you are holding the pen is
/// not a message. So <see cref="DrawNotes"/> runs from the ordinary frame and <see cref="ShowNotes"/>
/// is what turns them off — one toggle, for when the handwriting is in front of the thing you are
/// trying to look at.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>The live note session, or null when the pen is not armed. Drawing does not need
	/// it — see the class header — so this being null does not hide anything.</summary>
	public NoteSession NoteSession { get; private set; }

	public bool IsNoting => NoteSession is not null;

	/// <summary>Whether notes are painted at all. Off hides them everywhere, including while the
	/// pen is armed, because "let me see the model for a second" is a thing you want without
	/// putting the pen down.</summary>
	public bool ShowNotes = true;

	/// <summary>The eraser rather than the pen. A mode on the session's owner rather than on the
	/// session, because it changes what a click MEANS rather than what a stroke does.</summary>
	public bool NoteErasing;

	/// <summary>The notes to paint. Set to the studio's own list, so a note appears the moment a
	/// stroke commits with nothing to copy or invalidate.</summary>
	public IReadOnlyList<Note> Notes { get; set; } = Array.Empty<Note>();

	/// <summary>Raised after a stroke commits or a note is erased, so the window can mark the
	/// document dirty. Not raised per sample — a stroke is one edit, the same rule sculpting
	/// follows.</summary>
	public Action NoteChanged { get; set; }

	/// <summary>Raised when the pen is clicked on a note that already exists, carrying that note, so
	/// the window can put a text box in front of it. The viewport has no idea how to prompt for a
	/// string, and should not learn.</summary>
	public Action<Note> NoteTextRequested { get; set; }

	/// <summary>
	/// Roughly how big the part is, in world units, so the things drawn AROUND a note — the caption's
	/// offset, the dot on a degenerate note — scale with the model the way NoteSession.ScaleTo makes
	/// the stroke itself scale. Set by the window from the studio's bounds; one unit is the default
	/// primitive, which is the right guess for an empty studio.
	/// </summary>
	public float PartSize { get; set; } = 1f;

	/// <summary>Where the pen would put a point this frame, or null when there is nowhere to put
	/// one.</summary>
	private Vec3? _noteCursor;

	/// <summary>The note the eraser is over, so it can be shown before it is destroyed. An erase
	/// with no preview is a click that deletes something you had not identified yet.</summary>
	private Note _noteHovered;

	/// <summary>Guards against the click that opened the caption box immediately starting a stroke
	/// underneath it.</summary>
	private RealTimeSince _sinceNoteText;

	public void BeginNotes( NoteSession session )
	{
		NoteSession = session ?? throw new ArgumentNullException( nameof( session ) );
		ShowNotes = true;
	}

	public void EndNotes()
	{
		// A stroke left running when the mode ends would hold a note nobody will ever commit.
		// Cancel rather than commit: leaving the mode is not a way to finish a stroke.
		NoteSession?.CancelStroke();

		NoteSession = null;
		_noteCursor = null;
		_noteHovered = null;
		NoteErasing = false;
	}

	// --- input ---------------------------------------------------------------------------------

	private void NoteFrame()
	{
		if ( NoteSession is null )
			return;

		_noteCursor = null;
		_noteHovered = null;

		var stroking = NoteSession.IsStroking;

		// The pointer leaving the canvas does not end a stroke, only stops new samples — same rule
		// as a sculpt stroke, and for the same reason: dragging off the model and back is ordinary.
		if ( _canvasHasCursor )
		{
			var ray = Gizmo.CurrentRay;
			var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
			var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

			if ( NoteErasing )
			{
				_noteHovered = NoteSession.Pick( origin, direction );

				// HELD, NOT JUST PRESSED — the same shape as the sketch Cut tool: hold the button and
				// drag through what you want gone, rather than one click per note. A click still works,
				// it is just a drag that happens to end where it started.
				if ( Gizmo.IsLeftMouseDown && NoteSession.Erase( origin, direction ) )
					NoteChanged?.Invoke();
			}
			else
			{
				_noteCursor = NoteSession.Project( origin, direction );

				if ( !stroking && Gizmo.WasLeftMousePressed )
				{
					// A press on an existing note is a request to caption it rather than the start
					// of a stroke drawn on top of it. Writing on your own handwriting is the one
					// place a scribble tool has an unambiguous second meaning, and it saves the
					// caption needing a mode of its own.
					var under = NoteSession.Pick( origin, direction );

					if ( under is not null )
					{
						NoteTextRequested?.Invoke( under );
						_sinceNoteText = 0f;
					}
					else if ( _sinceNoteText > 0.25f && NoteSession.BeginStroke( origin, direction ) )
					{
						stroking = true;
					}
				}
				else if ( stroking && Gizmo.IsLeftMouseDown )
				{
					NoteSession.MoveTo( origin, direction );
				}
			}
		}

		// Released. There is no WasLeftMouseReleased in the Gizmo input this editor uses, so the end
		// of a stroke is the frame the button is no longer down — which is the same thing and needs
		// no API that might not be there.
		if ( stroking && !Gizmo.IsLeftMouseDown )
		{
			if ( NoteSession.EndStroke() is not null )
				NoteChanged?.Invoke();
		}

		DrawNoteCursor();
	}

	/// <summary>
	/// E for the eraser, H to hide the notes. Undo is NOT here — it is Ctrl+Z like everywhere else,
	/// routed to the session by EffigyWindow.Undo.
	///
	/// Letters only, for the reason HandleSculptKey gives at length: nothing in this editor names a
	/// KeyCode outside letters, Escape, Enter, Delete and Backspace, so anything else here would be
	/// a guess at an enum member that may not exist.
	/// </summary>
	public bool HandleNoteKey( KeyEvent e )
	{
		if ( NoteSession is null )
			return false;

		switch ( e.Key )
		{
			case KeyCode.E:
				NoteErasing = !NoteErasing;
				break;

			case KeyCode.H:
				ShowNotes = !ShowNotes;
				break;

			default:
				return false;
		}

		e.Accepted = true;
		Update();

		return true;
	}

	// --- drawing -------------------------------------------------------------------------------

	/// <summary>
	/// Every committed note, plus the stroke in progress.
	///
	/// IgnoreDepth, so a note is never buried by the part it is about. A note behind the model that
	/// you cannot read is worse than useless: you know something is written there and you have to
	/// orbit to find out what. Annotation in every tool that has it floats over the geometry, and
	/// the lift NoteSession applies is what keeps it from looking detached.
	/// </summary>
	private void DrawNotes()
	{
		if ( !ShowNotes )
			return;

		Gizmo.Draw.IgnoreDepth = true;

		// THE SESSION'S LIST WINS WHILE THE PEN IS ARMED, and that is not belt-and-braces - it is
		// the fix for a stroke that vanished the instant you let go of the mouse. The stroke in
		// progress is drawn from Working, so it was visible on the way down; on release it moved
		// into the studio's list, and Notes still pointed at the empty default because the only
		// thing that ever assigned it was RefreshNotes, which only runs on a rebuild. Drawing a
		// note therefore did not itself make the note appear.
		//
		// Reading through the session removes the dependency on anybody remembering to assign
		// anything: while armed, what is drawn IS what the session is writing into.
		foreach ( var note in NoteSession?.Notes ?? Notes )
			DrawNote( note, hovered: ReferenceEquals( note, _noteHovered ) );

		// The stroke being drawn, which is not in the list until it commits.
		if ( NoteSession?.Working is { } working )
			DrawNote( working, hovered: false );

		// Put both back. DrawNote leaves the thickness wherever the last note wanted it, and every
		// gizmo drawn after this in the frame would inherit it — which is how one fat note turns
		// the origin handle and the reference planes fat too.
		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;
	}

	private void DrawNote( Note note, bool hovered )
	{
		if ( note is null || note.Points.Count == 0 )
			return;

		var swatch = NotePalette.At( note.Color );
		var colour = new Color( swatch.R, swatch.G, swatch.B, 1f );

		// The eraser's target goes red and heavy whatever colour it was drawn in, so "this is the
		// one that is about to go" survives the note already being red.
		Gizmo.Draw.Color = hovered ? NoteEraseColor : colour;

		// Width is already a pixel thickness, so it goes straight in. It used to be multiplied by
		// 3.5 here, which was compensating for a default that had been written as though it were a
		// world distance.
		Gizmo.Draw.LineThickness = MathF.Max( note.Width, 0.5f ) * (hovered ? 1.6f : 1f);

		var previous = ToVector( note.Points[0] );

		for ( var i = 1; i < note.Points.Count; i++ )
		{
			var point = ToVector( note.Points[i] );

			Gizmo.Draw.Line( previous, point );
			previous = point;
		}

		// A single-point note cannot happen through the pen — EndStroke drops those — but a
		// hand-edited document can carry one, and a dot beats drawing nothing at all. The radius is
		// a world distance where Width is pixels, so it cannot be Width: it is sized off the note
		// itself, which is the only length this method has to hand.
		if ( note.Points.Count == 1 )
			Gizmo.Draw.SolidSphere( previous, NoteDotRadius, 8, 8 );

		if ( string.IsNullOrWhiteSpace( note.Text ) )
			return;

		// Lifted off the anchor so the words sit beside the mark rather than across it. In world
		// units on +z, which is up in this editor's Source-convention space.
		var anchor = ToVector( note.Anchor ) + Vector3.Up * (PartSize * NoteTextLiftFraction);

		Gizmo.Draw.Color = hovered ? NoteEraseColor : colour;
		Gizmo.Draw.WorldText( note.Text, new Transform( anchor ), "Roboto", NoteTextSize, TextFlag.Center );

		// A leader from the words back to the mark. Without it a caption on a busy part reads as
		// belonging to whatever it happens to be floating over.
		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.Color = (hovered ? NoteEraseColor : colour).WithAlpha( 0.4f );
		Gizmo.Draw.Line( ToVector( note.Anchor ), anchor );
	}

	/// <summary>Where the next point would land. Small and unfilled — this is the only cursor in the
	/// tool that has to not obscure the thing it is pointing at, because you are aiming it at your
	/// own handwriting.</summary>
	private void DrawNoteCursor()
	{
		if ( NoteErasing )
		{
			// Nothing of its own to draw: the hovered note is already painted red and fat by
			// DrawNote, which says more than a ring around it would.
			return;
		}

		if ( _noteCursor is not { } point || NoteSession is null )
			return;

		var swatch = NotePalette.At( NoteSession.Color );

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = new Color( swatch.R, swatch.G, swatch.B, 0.9f );
		Gizmo.Draw.SolidSphere( ToVector( point ), NoteDotRadius * 1.6f, 10, 10 );
		Gizmo.Draw.IgnoreDepth = false;
	}

	private static Vector3 ToVector( Vec3 v ) => new( v.x, v.y, v.z );

	/// <summary>How far above its anchor a caption floats, as a fraction of the part's size. A
	/// constant here would sit on top of a one-unit box and a mile above a room — the same mistake
	/// NoteSession.ScaleTo exists to undo.</summary>
	private const float NoteTextLiftFraction = 0.12f;

	/// <summary>Radius of the dot drawn for a degenerate one-point note, in world units, as a
	/// fraction of the part.</summary>
	private float NoteDotRadius => MathF.Max( PartSize * 0.006f, 1e-4f );

	private const float NoteTextSize = 11f;

	/// <summary>The colour of "this is what the eraser will take". The same red the rest of this
	/// editor uses for a destructive action.</summary>
	private static readonly Color NoteEraseColor = new( 1f, 0.35f, 0.32f, 1f );
}
