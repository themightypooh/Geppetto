using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Which material slots a painted body's atlas should bind to.
///
/// THE BUG THIS EXISTS FOR. Compile used to write the authored .vmat onto
/// <c>MaterialNames[0]</c> unconditionally. Slot 0 is where faces start, so that is usually right,
/// and that is why it shipped. It is wrong when every face has been dropped onto another slot
/// (the atlas binds to a slot nothing samples) and wrong when slot 0 already carries a material
/// the user chose (that name is overwritten). Bind the slots the painted faces actually wear.
/// </summary>
public static class PaintBind
{
	/// <summary>Every material slot a face of this mesh is on, sorted.</summary>
	public static SortedSet<int> SlotsOn( PolyMesh mesh )
	{
		var slots = new SortedSet<int>();

		if ( mesh?.Faces is null )
			return slots;

		foreach ( var face in mesh.Faces )
			slots.Add( face.Material );

		return slots;
	}

	/// <summary>
	/// The slots this painted body should bind its atlas to: the slots its faces wear.
	///
	/// Does not invent a slot. Does not write slot 0 unless a face is actually on it. A second
	/// painted body that shares a slot with this one is the caller's problem — isolate faces onto
	/// a fresh slot before asking, or accept that two atlases cannot share a name.
	/// </summary>
	public static List<int> SlotsToBind( PolyMesh mesh )
	{
		var slots = new List<int>();

		foreach ( var slot in SlotsOn( mesh ) )
			slots.Add( slot );

		return slots;
	}

	/// <summary>
	/// Whether merging <paramref name="source"/> into <paramref name="target"/> would drop an
	/// atlas. Paint is one canvas per body; a merge can show one, so the target keeps its own and
	/// only adopts the source's when it has none. Two painted bodies is the case this names.
	/// </summary>
	public static bool MergeDropsPaint( PolyMesh target, PolyMesh source ) =>
		target?.Paint is not null && source?.Paint is not null;

	/// <summary>A free slot no face on <paramref name="used"/> wears and no name occupies.</summary>
	public static int NextFreeSlot( IReadOnlyDictionary<int, string> names, IEnumerable<int> used )
	{
		var taken = new HashSet<int>();

		if ( used is not null )
		{
			foreach ( var slot in used )
				taken.Add( slot );
		}

		if ( names is not null )
		{
			foreach ( var slot in names.Keys )
				taken.Add( slot );
		}

		// Slot 0 is the default every face starts on; MaterialDrop never allocates it either.
		for ( var slot = 1; slot < 64; slot++ )
		{
			if ( !taken.Contains( slot ) )
				return slot;
		}

		return -1;
	}

	/// <summary>Move every face of <paramref name="mesh"/> onto <paramref name="slot"/>.</summary>
	public static void AssignAllFaces( PolyMesh mesh, int slot )
	{
		if ( mesh?.Faces is null )
			return;

		foreach ( var face in mesh.Faces )
			face.Material = slot;
	}
}
