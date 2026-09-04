using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Drawing, erasing and undoing grease-pencil notes.
///
/// SAME SPLIT AS SculptSession, FOR THE SAME REASON. Everything with a decision in it lives here,
/// in the kernel, where a test can run it without an engine: where a sample lands in depth, whether
/// the pointer has moved far enough to earn one, which note an erase click is aiming at, what undo
/// means. The editor half converts Vector3 to Vec3 and draws lines. Sculpting learned this the hard
/// way — a bug that made every parameter edit a no-op read as three unrelated UI faults, because
/// the logic was somewhere no test could reach.
///
/// THE SESSION DOES NOT OWN THE LIST. It is handed <see cref="PartStudio.Notes"/> and edits it in
/// place, so a note exists in the document the moment the stroke ends and there is no commit step
/// to forget. That also means undo here is undo of the document, which is what the user means by
/// it.
/// </summary>
public sealed class NoteSession
{
	/// <summary>The document's own list, edited in place.</summary>
	private readonly List<Note> _notes;

	/// <summary>What the ray is tested against, refreshed by the editor after every rebuild. Notes
	/// stick to nothing, but they still LAND on the surface under the cursor, which is the
	/// difference between drawing on the model and drawing in front of it.</summary>
	private List<Body> _bodies = new();

	/// <summary>The point the pivot sits at, used as the depth of the fallback plane. Reads as
	/// "the middle of the part" for the purposes of drawing in mid-air.</summary>
	public Vec3 Pivot;

	private Note _working;

	/// <summary>Origin and normal of the plane a mid-air stroke is drawn on, fixed at the moment
	/// the stroke begins.
	///
	/// FIXED, not recomputed per sample, and that is the whole trick. Recomputing it against the
	/// current ray each sample puts every point at the same distance from the camera, which sounds
	/// identical and is not: on an orbiting or even slightly drifting view the stroke bends into a
	/// shell around the viewer. One plane per stroke means what you drew is flat, which is what a
	/// note written in the air is supposed to be.</summary>
	private Vec3 _planeOrigin, _planeNormal;

	private readonly Stack<NoteEdit> _done = new();
	private readonly Stack<NoteEdit> _undone = new();

	public NoteSession( List<Note> notes )
	{
		_notes = notes ?? throw new ArgumentNullException( nameof( notes ) );
	}

	/// <summary>Colour and thickness the next stroke is drawn with. Live settings rather than
	/// per-note arguments, because that is how the toolbar presents them.</summary>
	public int Color;

	/// <summary>Line thickness in SCREEN PIXELS, not world units - see Note.Width. Unlike the three
	/// distances below it therefore does not scale with the model, which is right: a note is
	/// something you read, and handwriting that gets thinner as you zoom out is handwriting you
	/// cannot read.</summary>
	public float Width = 2f;

	/// <summary>
	/// How far apart two samples have to be, in world units, before the second is kept.
	///
	/// A mouse reports far more positions than a stroke needs, and keeping them all costs both the
	/// file and the draw loop for a line nobody can see the difference in. Big enough to thin a
	/// slow careful stroke, small enough that a tight circle still reads as round.
	///
	/// The value here is only the fallback for an empty studio - see <see cref="ScaleTo"/>, which
	/// is what actually sets it.
	/// </summary>
	public float Spacing = 0.015f;

	/// <summary>How far off the surface a sample sits, in world units. Without it a stroke drawn on
	/// a face z-fights the face and reads as a dotted line that flickers when the camera moves.
	/// Set by <see cref="ScaleTo"/>.</summary>
	public float Lift = 0.006f;

	/// <summary>How near the cursor has to be for a note to be picked or erased, in world units.
	/// Generous relative to the stroke: a thin ribbon is a small target and an erase that misses is
	/// more annoying than one that is easy to aim. Set by <see cref="ScaleTo"/>.</summary>
	public float PickRadius = 0.05f;

	/// <summary>
	/// Size the three distances above to the model, rather than to a number somebody guessed.
	///
	/// EFFIGY'S UNITS ARE DIMENSIONLESS - PolyMesh.BoundsDiagonal says so at length: a default
	/// primitive is one unit across and a room is hundreds. A constant that feels right on one of
	/// those is unusable on the other, and this class shipped with three of them tuned for a part
	/// tens of units wide. On a default one-unit box that made Spacing a quarter of the whole part
	/// (four samples across a face), Lift a visible fraction of it (handwriting floating off the
	/// model), and PickRadius wider than the entire model - so every click after the first note
	/// landed on that note and opened its caption box instead of drawing.
	///
	/// The fractions are the sculpt brush's rule (SculptSession.SuggestedRadius) applied to a
	/// different job: a fraction of the diagonal, with a fallback for the empty studio where there
	/// is no diagonal to take a fraction of.
	/// </summary>
	public void ScaleTo( float diagonal )
	{
		// An empty studio still has to be drawable - a note on nothing is a legitimate first act -
		// and it has no bounds to measure. One unit is the default primitive, which is the part
		// about to be made.
		if ( !(diagonal > 1e-6f) )
			diagonal = 1f;

		// ~65 samples across the part. Fine enough that a tight circle reads as round, coarse
		// enough that a slow hand does not write a thousand points into the document.
		Spacing = diagonal / 65f;

		// Just off the surface. Big enough to beat depth precision, small enough that the note
		// still reads as being ON the face rather than hovering over it.
		Lift = diagonal / 160f;

		// A comfortably bigger target than the line is thick, and still a small fraction of the
		// part, so notes on opposite sides of a model are never both under the cursor.
		PickRadius = diagonal / 20f;
	}

	public bool IsStroking => _working is not null;

	public bool CanUndo => _done.Count > 0;

	public bool CanRedo => _undone.Count > 0;

	public IReadOnlyList<Note> Notes => _notes;

	/// <summary>The stroke being drawn right now, so the editor can draw it before it is
	/// committed. Null between strokes.</summary>
	public Note Working => _working;

	public void SetBodies( IEnumerable<Body> bodies ) =>
		_bodies = bodies?.Where( b => b?.Mesh is not null ).ToList() ?? new List<Body>();

	// --- where a sample lands ------------------------------------------------------------------

	/// <summary>
	/// Depth for a ray: on the surface if it hits one, otherwise on the stroke's plane.
	///
	/// The lift is along the SURFACE normal rather than back along the ray, so a stroke keeps the
	/// same clearance from the face when the camera swings round. Backing it along the ray instead
	/// looks identical from where you drew it and sinks into the model from anywhere else.
	/// </summary>
	public Vec3? Project( Vec3 origin, Vec3 direction )
	{
		var dir = direction.Normal;

		if ( dir.LengthSquared < 0.5f )
			return null;

		if ( MeshRaycast.Raycast( _bodies, origin, dir ) is { } surface )
			return surface.Hit.Point + surface.Hit.Normal.Normal * Lift;

		var normal = IsStroking ? _planeNormal : -dir;
		var planeOrigin = IsStroking ? _planeOrigin : Pivot;
		var facing = Vec3.Dot( dir, normal );

		// Edge-on to the plane there is no answer, and inventing one puts a point at infinity. This
		// only happens mid-stroke on a plane the view has since rotated almost parallel to, and
		// dropping the sample is right: the stroke pauses rather than shooting off.
		if ( MathF.Abs( facing ) < 1e-4f )
			return null;

		var t = Vec3.Dot( planeOrigin - origin, normal ) / facing;

		return t > 0f ? origin + dir * t : null;
	}

	// --- drawing -------------------------------------------------------------------------------

	/// <summary>
	/// Start a stroke. Returns false only when the ray is unusable — unlike a sculpt stroke, a note
	/// does NOT need to hit the model, because writing in the space beside a part is most of what
	/// this is for.
	/// </summary>
	public bool BeginStroke( Vec3 origin, Vec3 direction )
	{
		if ( IsStroking )
			throw new InvalidOperationException( "A stroke is already running; end it before starting another." );

		var dir = direction.Normal;

		if ( dir.LengthSquared < 0.5f )
			return false;

		// Set BEFORE the first Project, so that call already has the plane it will keep. The plane
		// passes through whatever is under the cursor when the stroke starts — the surface if there
		// is one, the pivot's depth if not — so a stroke that begins on the model and wanders off
		// its edge carries on at the depth it was at rather than jumping back to the pivot.
		_planeNormal = -dir;
		_planeOrigin = MeshRaycast.Raycast( _bodies, origin, dir ) is { } hit ? hit.Hit.Point : Pivot;
		_working = new Note { Color = Color, Width = Width };

		if ( Project( origin, dir ) is { } point )
			_working.Points.Add( point );

		return true;
	}

	/// <summary>Add a sample if the pointer has travelled far enough. Returns whether it did, so
	/// the editor can skip a redraw that would change nothing.</summary>
	public bool MoveTo( Vec3 origin, Vec3 direction )
	{
		if ( _working is null || Project( origin, direction ) is not { } point )
			return false;

		if ( _working.Points.Count > 0 && (point - _working.Points[^1]).Length < Spacing )
			return false;

		_working.Points.Add( point );

		return true;
	}

	/// <summary>
	/// Commit the stroke, or drop it if there is nothing in it. Returns the committed note, or null.
	///
	/// A CLICK IS NOT A STROKE. Every left-press in note mode begins one, including the click that
	/// was reaching for something else, and committing a one-point mark for each of those leaves a
	/// scatter of near-invisible pins the user cannot see well enough to erase. Two points is the
	/// shortest thing that is visibly a line and was therefore visibly intended.
	/// </summary>
	public Note EndStroke()
	{
		var stroke = _working;

		_working = null;

		if ( stroke is null || stroke.Points.Count < 2 )
			return null;

		_notes.Add( stroke );
		_done.Push( NoteEdit.Added( stroke, _notes.Count - 1 ) );
		_undone.Clear();

		return stroke;
	}

	public void CancelStroke() => _working = null;

	// --- picking and erasing -------------------------------------------------------------------

	/// <summary>The note under the cursor, or null. Ties break towards the nearest, so a scribble
	/// crossing another is picked where you are actually pointing.</summary>
	public Note Pick( Vec3 origin, Vec3 direction )
	{
		Note best = null;
		var bestDistance = PickRadius;

		foreach ( var note in _notes )
		{
			var d = note.DistanceToRay( origin, direction );

			if ( d < bestDistance )
			{
				best = note;
				bestDistance = d;
			}
		}

		return best;
	}

	public bool Erase( Vec3 origin, Vec3 direction ) => Remove( Pick( origin, direction ) );

	public bool Remove( Note note )
	{
		if ( note is null )
			return false;

		var index = _notes.IndexOf( note );

		if ( index < 0 )
			return false;

		_notes.RemoveAt( index );
		_done.Push( NoteEdit.Removed( note, index ) );
		_undone.Clear();

		return true;
	}

	/// <summary>
	/// Retype a note's caption. Recorded as an edit like any other, because the thing people most
	/// want back after a typo is what they typed before it.
	/// </summary>
	public bool SetText( Note note, string text )
	{
		if ( note is null || note.Text == text )
			return false;

		_done.Push( NoteEdit.Retitled( note, note.Text ) );
		_undone.Clear();
		note.Text = text;

		return true;
	}

	// --- undo ----------------------------------------------------------------------------------

	public bool Undo() => Step( _done, _undone );

	public bool Redo() => Step( _undone, _done );

	private bool Step( Stack<NoteEdit> from, Stack<NoteEdit> to )
	{
		if ( from.Count == 0 )
			return false;

		// A stroke left running would be committed on top of a document that has just moved under
		// it, so undo cancels it first. Same rule as leaving sculpt mode mid-stroke.
		CancelStroke();

		to.Push( from.Pop().Apply( _notes ) );

		return true;
	}

	/// <summary>Forget the history without touching the notes. For leaving note mode: the marks
	/// stay in the document, the ability to undo them from a session that has ended does not.
	/// </summary>
	public void ClearHistory()
	{
		_done.Clear();
		_undone.Clear();
	}
}

/// <summary>
/// One reversible change to the note list.
///
/// APPLY RETURNS ITS OWN INVERSE, so undo and redo are the same code path pushing onto opposite
/// stacks rather than two switch statements that have to agree with each other. The second of those
/// is where the asymmetry bug lives in every implementation that has one.
/// </summary>
internal sealed class NoteEdit
{
	private enum Kind { Add, Remove, Text }

	private readonly Kind _kind;
	private readonly Note _note;
	private readonly int _index;
	private readonly string _text;

	private NoteEdit( Kind kind, Note note, int index, string text )
	{
		_kind = kind;
		_note = note;
		_index = index;
		_text = text;
	}

	public static NoteEdit Added( Note note, int index ) => new( Kind.Add, note, index, null );

	public static NoteEdit Removed( Note note, int index ) => new( Kind.Remove, note, index, null );

	public static NoteEdit Retitled( Note note, string previous ) => new( Kind.Text, note, -1, previous );

	public NoteEdit Apply( List<Note> notes )
	{
		switch ( _kind )
		{
			case Kind.Add:
			{
				// By identity, not by the stored index: an erase since this note was drawn has
				// shifted everything after it, and removing at the remembered slot would delete
				// somebody else's note.
				var at = notes.IndexOf( _note );

				if ( at >= 0 )
					notes.RemoveAt( at );

				return Removed( _note, at < 0 ? _index : at );
			}

			case Kind.Remove:
			{
				var at = Math.Clamp( _index, 0, notes.Count );

				notes.Insert( at, _note );

				return Added( _note, at );
			}

			default:
			{
				var previous = _note.Text;

				_note.Text = _text;

				return Retitled( _note, previous );
			}
		}
	}
}
