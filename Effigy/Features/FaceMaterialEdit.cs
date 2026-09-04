using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Putting ONE face on a material slot.
///
/// FaceMaterialFeature is built for the other case — a set of faces chosen deliberately in a dialog
/// — and the editor's right-click menu wants the small one: this face, this slot, now. The two
/// differ in bookkeeping rather than in effect, and the bookkeeping is the part worth having in the
/// kernel where it can be tested: which existing assignment to reuse, what to do with the one the
/// face is leaving, and where a new one goes in a tree that may be rolled back.
///
/// It edits the HISTORY, never the mesh. Writing the slot straight onto Body.Mesh would hold until
/// the next rebuild and then silently revert, because bodies are remade from scratch every time.
/// </summary>
public static class FaceMaterialEdit
{
	/// <summary>
	/// Move one face onto <paramref name="slot"/>, and report whether anything changed.
	///
	/// The face is identified by the body and face index it resolved to a moment ago — the caller
	/// has just raycast it — and stored as the FaceRef it captured, which is what survives the
	/// rebuild. Both are needed: the index says which face to take OUT of the assignments that
	/// currently hold it, the reference is what goes IN.
	///
	/// Call Rebuild afterwards. This deliberately does not, so a caller changing several faces pays
	/// for one rebuild rather than one each.
	/// </summary>
	public static bool Assign( PartStudio studio, string bodyId, int faceIndex, FaceRef reference, int slot )
	{
		if ( studio is null || string.IsNullOrEmpty( bodyId ) || faceIndex < 0 )
			return false;

		if ( slot < 0 )
			return false;

		var changed = Detach( studio, bodyId, faceIndex );

		// Slot 0 is the ABSENCE of an assignment rather than an assignment to zero — it is what every
		// face starts on, and what the viewport pointedly does not tint. Detaching has already done
		// the whole job.
		if ( slot == 0 )
			return changed;

		var target = SlotFeature( studio, slot );

		if ( target is null )
		{
			target = new FaceMaterialFeature();
			target.Material.Value = slot;

			// AT THE ROLLBACK BAR, not at the end. Below the bar a feature is not evaluated, so the
			// face would sit there unpainted with nothing on screen to explain why.
			//
			// AND THE BAR SITTING AT EXACTLY Features.Count COUNTS AS "at the bar". A `<` test here
			// sends that case to Add, which appends the new assignment ONTO the bar rather than
			// above it, and EffectiveCount then leaves it out - the face stays unpainted, which is
			// the precise failure this insert exists to prevent. The editor had the same
			// comparison in the same shape and it cost a sketch its plane.
			var at = Math.Min( studio.RollbackIndex, studio.Features.Count );

			if ( at < studio.Features.Count )
				studio.Insert( at, target );
			else
				studio.Add( target );

			// int.MaxValue already means "evaluate everything" and has to stay that way.
			if ( studio.RollbackIndex < studio.Features.Count )
				studio.RollbackIndex = at + 1;
		}

		target.Faces.Add( reference );
		studio.MarkDirty( target );

		return true;
	}

	/// <summary>
	/// Take a face out of every assignment currently holding it, and drop any assignment that just
	/// lost its last face.
	///
	/// Relying on tree order instead — the later feature wins, so why bother — works today and rots:
	/// right-clicking the same face four times would leave four assignments to it, three of them
	/// invisible on screen and all four written to the file.
	/// </summary>
	public static bool Detach( PartStudio studio, string bodyId, int faceIndex )
	{
		if ( studio is null || string.IsNullOrEmpty( bodyId ) || faceIndex < 0 )
			return false;

		var changed = false;
		var emptied = new List<FaceMaterialFeature>();

		// MATCHED ACROSS THE WHOLE SURFACE, because that is what gets painted. An assignment made
		// by clicking one fragment of a wall resolves to whichever fragment it captured, and a
		// later click landing on a different fragment of the same wall is the same face to the
		// person doing it - matching on the index alone left the old assignment in the tree,
		// invisible on screen and still written to the file.
		var surface = FaceSurfaceOf( studio, bodyId, faceIndex );

		foreach ( var feature in studio.Features.OfType<FaceMaterialFeature>().ToList() )
		{
			var removed = false;

			// Matched by RESOLVED FACE, not by comparing the stored references. Two captures of the
			// same face record different hit points and are not equal to one another, which is the
			// same trap the dialog's selection box documents.
			for ( var i = feature.Faces.Count - 1; i >= 0; i-- )
			{
				if ( !FacePlane.TryResolveFace( studio.Bodies, feature.Faces[i], out var body, out var index ) )
					continue;

				if ( body.Id != bodyId )
					continue;

				if ( index != faceIndex && !(surface?.Contains( index ) ?? false) )
					continue;

				feature.Faces.RemoveAt( i );
				removed = true;
			}

			if ( !removed )
				continue;

			changed = true;
			studio.MarkDirty( feature );

			if ( feature.Faces.Count == 0 )
				emptied.Add( feature );
		}

		// An assignment with no faces left FAILS the moment it runs, which is right when the faces
		// went missing under it and wrong here: it only emptied because this edit took its last face,
		// and leaving a red feature in the tree for that would be baffling.
		foreach ( var feature in emptied )
			studio.Remove( feature );

		return changed;
	}

	/// <summary>The surface a body's face belongs to, or null when the body is not in the studio.
	/// Null rather than an empty surface so a caller can tell "nothing to widen to" from "widened
	/// to nothing".</summary>
	static FaceSurface FaceSurfaceOf( PartStudio studio, string bodyId, int faceIndex )
	{
		foreach ( var body in studio.Bodies )
		{
			if ( body?.Mesh is { } mesh && body.Id == bodyId )
				return FaceSurface.FromFace( mesh, faceIndex );
		}

		return null;
	}

	/// <summary>
	/// The live assignment for a slot above the rollback bar, or null.
	///
	/// The LAST one rather than the first, because that is the one that would win anyway — the tree
	/// runs in order and a later assignment overrides an earlier one on any face they share.
	/// Reusing it is what keeps a session of clicking faces from growing a feature per click.
	/// </summary>
	public static FaceMaterialFeature SlotFeature( PartStudio studio, int slot )
	{
		FaceMaterialFeature found = null;

		var limit = Math.Min( studio.EffectiveCount, studio.Features.Count );

		for ( var i = 0; i < limit; i++ )
		{
			if ( studio.Features[i] is FaceMaterialFeature feature
				&& !feature.Suppressed
				&& feature.Material.Clamped == slot )
				found = feature;
		}

		return found;
	}

	/// <summary>Every slot the document has an opinion about — one an assignment uses, or one
	/// somebody has named. What a menu offers on top of this is the menu's business.</summary>
	public static IEnumerable<int> UsedSlots( PartStudio studio )
	{
		var slots = new SortedSet<int>();

		if ( studio is null )
			return slots;

		foreach ( var feature in studio.Features.OfType<FaceMaterialFeature>() )
			slots.Add( feature.Material.Clamped );

		foreach ( var slot in studio.MaterialNames.Keys )
			slots.Add( slot );

		return slots;
	}
}
