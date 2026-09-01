using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Dropping a material onto a face.
///
/// THE PROBLEM THIS SOLVES. Faces carry a slot number, not a material — see FaceMaterialEdit for
/// why that has to stay true — and PartStudio.MaterialNames maps the number to a name. Every
/// existing way in names a slot you have already chosen: the Materials panel browses FOR slot 5,
/// the face menu browses for the slot the face is already on. Dragging a material out of a browser
/// and letting go over a face names no slot at all. It says "this face, this material" and leaves
/// the number entirely to us.
///
/// So this is the half that was missing: turn a material into the slot that should carry it, then
/// do the ordinary face assignment with it. The rule is one slot per material, reused —
/// <see cref="SlotFor"/> hands back the slot that already carries the material if there is one, so
/// dropping the same material on thirty faces produces one slot and one assignment feature rather
/// than thirty of each. Only a material nobody has used yet takes a fresh slot.
///
/// It edits the HISTORY, never the mesh, exactly as FaceMaterialEdit does, and for the same reason:
/// bodies are remade from scratch on every rebuild.
/// </summary>
public static class MaterialDrop
{
	/// <summary>The highest slot a face can be on — FaceMaterialFeature.Material clamps to 0..63,
	/// so a slot past this could be stored and would never come back.</summary>
	public const int HighestSlot = 63;

	/// <summary>
	/// Which slot should carry <paramref name="material"/>, or -1 when there is nowhere to put it.
	///
	/// Three answers, in order:
	///
	/// 1. THE SLOT ALREADY CARRYING IT. Checked first and by name, so a second drop of the same
	///    material joins the first rather than opening a second slot that renders identically. The
	///    lowest such slot wins if a document somehow named two, purely so the answer is stable.
	///
	/// 2. THE LOWEST SLOT NOBODY IS USING, counting from 1. Used means named OR painted on — a slot
	///    with an assignment feature and no name is the result of the face menu's "put this face on
	///    slot 3", and taking it here would silently repaint those faces with the dropped material.
	///
	/// 3. NOTHING, when all 63 are spoken for.
	///
	/// SLOT 0 IS NEVER ALLOCATED, though it is returned by rule 1 if somebody has named it. It is
	/// the slot every face starts on and the one the viewport pointedly does not tint: handing it to
	/// a drop would paint the whole part instead of the one face under the cursor. Naming slot 0
	/// remains something you do deliberately, from the Materials panel, where the consequence is on
	/// screen next to it.
	/// </summary>
	public static int SlotFor( PartStudio studio, string material )
	{
		if ( studio is null )
			return -1;

		if ( Normalise( material ) is null )
			return -1;

		if ( SlotCarrying( studio, material ) is var carrying && carrying >= 0 )
			return carrying;

		var taken = new HashSet<int>( FaceMaterialEdit.UsedSlots( studio ) );

		for ( var slot = 1; slot <= HighestSlot; slot++ )
		{
			if ( !taken.Contains( slot ) )
				return slot;
		}

		return -1;
	}

	/// <summary>
	/// The slot already carrying <paramref name="material"/>, or -1 if no slot does.
	///
	/// Rule 1 of <see cref="SlotFor"/>, on its own, because a browser asking "does this part already
	/// use this material, and where" must not be answered with the free slot SlotFor would hand back
	/// — that would badge every material in the project with the same number and claim the document
	/// uses all of them.
	///
	/// The LOWEST such slot if a document somehow named two, purely so the answer is stable, and
	/// matched through <see cref="Normalise"/> so a slot named with backslashes still recognises the
	/// asset a picker hands over with forward ones.
	/// </summary>
	public static int SlotCarrying( PartStudio studio, string material )
	{
		if ( studio is null )
			return -1;

		var wanted = Normalise( material );

		if ( wanted is null )
			return -1;

		foreach ( var (slot, name) in studio.MaterialNames.OrderBy( kv => kv.Key ) )
		{
			if ( Normalise( name ) == wanted )
				return slot;
		}

		return -1;
	}

	/// <summary>
	/// Put <paramref name="material"/> on one face, and report whether anything changed.
	///
	/// The face is identified the way the right-click menu identifies it — the body and face index
	/// a raycast just returned, plus the FaceRef captured at the hit point, which is the half that
	/// survives a rebuild. <paramref name="slot"/> comes back so the caller can say which slot it
	/// landed on, because that number is the only thing on screen afterwards that explains where the
	/// material went; it is -1 when nothing was done.
	///
	/// Call Rebuild afterwards. Deliberately not done here, for the same reason FaceMaterialEdit
	/// does not: a caller dropping onto several faces should pay for one rebuild, not one each.
	/// </summary>
	public static bool Drop( PartStudio studio, string bodyId, int faceIndex, FaceRef reference,
		string material, out int slot )
	{
		slot = -1;

		if ( studio is null )
			return false;

		var name = material?.Trim();

		if ( string.IsNullOrWhiteSpace( name ) )
			return false;

		slot = SlotFor( studio, name );

		if ( slot < 0 )
			return false;

		// The NAME first, then the face. Both are edits and either can be the only one: dropping a
		// material the document has never seen names a fresh slot and moves the face onto it, while
		// dropping it onto a second face names nothing new and only moves the face.
		//
		// Compared through Normalise, not by string equality, so re-dropping the same asset spelled
		// with backslashes does not rewrite the name to the other spelling. The stored value would
		// still resolve to the same material, but the document would come back dirty, an undo step
		// would appear, and every open control would refresh — for a change nobody made.
		var named = false;

		if ( !studio.MaterialNames.TryGetValue( slot, out var existing ) || Normalise( existing ) != Normalise( name ) )
		{
			studio.MaterialNames[slot] = name;
			named = true;
		}

		// Whether the face is ALREADY on this slot, asked before Assign rather than inferred from
		// what it returns. Assign detaches before it attaches, so putting a face back where it
		// already was reports a change every time — true of the mechanism, wrong as an answer, and
		// the reason the right-click menu checks the same thing before calling it. Here it is not
		// an optimisation: dropping a material onto the face already wearing it is the ordinary way
		// to MISS by a few pixels, and reporting it as an edit puts a do-nothing step on the undo
		// stack that then has to be pressed through.
		var moved = FaceSlot( studio, bodyId, faceIndex ) != slot
			&& FaceMaterialEdit.Assign( studio, bodyId, faceIndex, reference, slot );

		return named || moved;
	}

	/// <summary>
	/// The slot a face is on right now, or -1 if the body or face cannot be found.
	///
	/// Read off the BUILT mesh rather than worked out from the assignments in the tree, because the
	/// mesh is where they have all already been applied in order — including a later assignment
	/// overriding an earlier one on the same face, which reading the features would have to redo.
	/// </summary>
	private static int FaceSlot( PartStudio studio, string bodyId, int faceIndex )
	{
		var body = studio?.Bodies?.FirstOrDefault( b => b?.Id == bodyId );

		if ( body?.Mesh is not { } mesh || faceIndex < 0 || faceIndex >= mesh.Faces.Count )
			return -1;

		return mesh.Faces[faceIndex].Material;
	}

	/// <summary>
	/// A material path reduced to something two spellings of the same asset agree on.
	///
	/// Separators and case, because a path typed by hand, one from an asset picker and one from a
	/// drag can differ in both while naming one file, and a document that disagrees with itself
	/// about that grows a second slot for a material it already has.
	///
	/// Public because the Materials dock has to key a lookup of every material in the project by the
	/// same rule this file matches slots with. It could have asked <see cref="SlotCarrying"/> once
	/// per material instead, and that is a scan of the whole project against the whole slot table on
	/// every rebuild — which includes every tick of a dragged parameter. Exporting the rule lets it
	/// build the index once and walk the handful of named slots instead. What must not happen is a
	/// second copy of the rule over there: the two would agree until one of them learned about
	/// trailing slashes.
	/// </summary>
	public static string Normalise( string path ) =>
		string.IsNullOrWhiteSpace( path ) ? null : path.Trim().Replace( '\\', '/' ).ToLowerInvariant();
}
