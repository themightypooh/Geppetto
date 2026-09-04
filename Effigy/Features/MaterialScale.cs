using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// How big a material is, in world units, per slot.
///
/// THE PROBLEM THIS SOLVES. Extrude caps take sketch coordinates straight through as UVs, so one
/// texture repeat covers one unit — and a unit is an inch. Drop a floor tile on a diner floor and
/// you get two hundred repeats across the room, which reads as noise rather than as tile. Nothing
/// short of adding a UV Project feature could change that, and a UV Project is a whole-body,
/// material-blind operation buried in the tree: the wrong instrument for "this one material is the
/// wrong size".
///
/// ON THE SLOT, NOT THE FACE. s&amp;box's own mesh editor keeps TextureScale per face, because its
/// faces carry a material directly and there is nothing else for it to hang on. Effigy has the slot
/// indirection and <see cref="MaterialDrop"/> spends its whole existence keeping one material on one
/// slot, so the same fact belongs where the material name already lives: slot 2 is tile at 48 units
/// everywhere in the document, or it is nothing. It also means the number survives a rebuild without
/// being a feature, exactly as MaterialNames does and for the same reason — bodies are remade from
/// scratch every time, and a number written onto a face would be gone.
///
/// The cost, said out loud: changing a slot's scale changes it everywhere that material is used.
/// That is almost always the point. When it is not, the escape hatches are a second slot, or the
/// per-body UVProjectFeature, which was always the tool for "these faces, differently".
///
/// UNITS PER TILE, not units per texel. s&amp;box says 0.25 and means a quarter of a unit per texel;
/// UVProjectFeature.Scale already says "Units per tile" and means the world size of one full repeat.
/// Two vocabularies for one idea inside one program is worse than disagreeing with the engine, so
/// this follows the one Effigy already had.
///
/// WHAT IT DOES NOT FIX. Extrude SIDES are parameterised 0..1 around the perimeter and 0..1 up the
/// height, so their UVs are not in units at all and a units-per-tile number cannot be literally true
/// there — it comes out as "this many repeats across the whole side". <see cref="Fit"/> is the
/// honest answer on such a face, because it measures the UVs it actually finds rather than assuming
/// what they mean. The real fix is to make sides project in world units like the caps do, which
/// changes the mapping on every existing model and is therefore its own job.
/// </summary>
public static class MaterialScale
{
	/// <summary>One tile per unit: the mapping the features produce, left alone.</summary>
	public static readonly Vec2 Unscaled = new( 1f, 1f );

	/// <summary>
	/// The size one repeat of a slot's material covers, or <see cref="Unscaled"/> when nobody has
	/// said.
	///
	/// Absence means one-to-one rather than "unset", which is what lets every document written
	/// before this existed keep rendering exactly as it did.
	/// </summary>
	public static Vec2 ScaleFor( PartStudio studio, int slot )
	{
		if ( studio is null || !studio.MaterialScales.TryGetValue( slot, out var scale ) )
			return Unscaled;

		return Sanitise( scale );
	}

	/// <summary>
	/// Set a slot's size, and report whether anything changed.
	///
	/// A scale back at 1:1 REMOVES the entry rather than storing it. The document writes one line
	/// per stored scale, and a line saying "this slot is the size it would have been anyway" is a
	/// diff for nothing — the same reason StudioDocument only writes an origin that has moved.
	/// </summary>
	public static bool SetScale( PartStudio studio, int slot, Vec2 scale )
	{
		if ( studio is null || slot < 0 || slot > MaterialDrop.HighestSlot )
			return false;

		var wanted = Sanitise( scale );

		if ( Same( ScaleFor( studio, slot ), wanted ) )
			return false;

		if ( Same( wanted, Unscaled ) )
			return studio.MaterialScales.Remove( slot );

		studio.MaterialScales[slot] = wanted;

		return true;
	}

	/// <summary>
	/// Divide every scaled slot's UVs through the model, in place.
	///
	/// CALLED ONCE, AT THE END OF THE REBUILD, and it has to be: a feature's UVs are the input to
	/// this, so running it mid-tree would let the next feature inherit already-scaled UVs and the
	/// one after that scale them again. PartStudio.Rebuild replaces Bodies wholesale and the
	/// incremental cache holds clones taken inside the feature loop, so what this touches is never
	/// what the next rebuild reads back — which is the property that keeps it from compounding.
	///
	/// A slot at 1:1 is skipped rather than divided by one, so a document with no scales set does no
	/// float work per corner.
	/// </summary>
	public static void Apply( PartStudio studio )
	{
		if ( studio is null || studio.MaterialScales.Count == 0 )
			return;

		foreach ( var body in studio.Bodies ?? Enumerable.Empty<Body>() )
			Apply( body?.Mesh, studio.MaterialScales );
	}

	/// <summary>The same divide over one mesh, for tests and for anything holding a mesh rather than
	/// a studio.</summary>
	public static void Apply( PolyMesh mesh, IReadOnlyDictionary<int, Vec2> scales )
	{
		if ( mesh is null || scales is null || scales.Count == 0 )
			return;

		// Sanitised once per SLOT rather than once per corner: a stored zero would otherwise divide
		// every UV on the slot into infinity, and the check has the same answer every time.
		var clean = new Dictionary<int, Vec2>( scales.Count );

		foreach ( var (slot, scale) in scales )
		{
			var value = Sanitise( scale );

			if ( !Same( value, Unscaled ) )
				clean[slot] = value;
		}

		if ( clean.Count == 0 )
			return;

		foreach ( var face in mesh.Faces )
		{
			if ( face.UVs is null || !clean.TryGetValue( face.Material, out var scale ) )
				continue;

			for ( var i = 0; i < face.UVs.Length; i++ )
				face.UVs[i] = new Vec2( face.UVs[i].x / scale.x, face.UVs[i].y / scale.y );
		}
	}

	/// <summary>
	/// The scale that makes a material repeat <paramref name="repeats"/> times across one face —
	/// the mesh editor's Fit button, and the only answer that is right on a face whose UVs are not
	/// in units.
	///
	/// MEASURED FROM THE BUILT MESH, WHICH IS ALREADY SCALED. <see cref="Apply"/> has run by the
	/// time anything can point at a face, so the extent read here is the extent AFTER the slot's
	/// current scale was divided out. <paramref name="current"/> multiplies it back, so fitting the
	/// same face twice lands on the same number instead of walking the scale down by a factor of
	/// itself every time.
	///
	/// A face with no extent on an axis — a side seen edge-on in UV space, a degenerate cap — keeps
	/// whatever it had on that axis rather than dividing by zero.
	/// </summary>
	public static Vec2 Fit( PolyMesh mesh, int faceIndex, Vec2 current, float repeats = 1f )
	{
		var scale = Sanitise( current );

		if ( mesh is null || faceIndex < 0 || faceIndex >= mesh.Faces.Count || repeats <= 0f )
			return scale;

		var face = mesh.Faces[faceIndex];

		if ( face.UVs is null || face.UVs.Length == 0 )
			return scale;

		var minU = float.MaxValue;
		var minV = float.MaxValue;
		var maxU = float.MinValue;
		var maxV = float.MinValue;

		foreach ( var uv in face.UVs )
		{
			minU = MathF.Min( minU, uv.x );
			minV = MathF.Min( minV, uv.y );
			maxU = MathF.Max( maxU, uv.x );
			maxV = MathF.Max( maxV, uv.y );
		}

		var spanU = (maxU - minU) * scale.x;
		var spanV = (maxV - minV) * scale.y;

		return new Vec2(
			spanU > 1e-6f ? spanU / repeats : scale.x,
			spanV > 1e-6f ? spanV / repeats : scale.y );
	}

	/// <summary>
	/// A scale that can be divided by: positive and finite on both axes.
	///
	/// A zero or a NaN reaches here from a document somebody edited by hand and from a field
	/// mid-keystroke, and either one turns every UV on the slot into infinity — which renders as
	/// nothing at all, with no clue where it came from. Falling back per axis rather than wholesale
	/// keeps a usable u when only v is broken.
	/// </summary>
	public static Vec2 Sanitise( Vec2 scale ) =>
		new( Usable( scale.x ) ? scale.x : 1f, Usable( scale.y ) ? scale.y : 1f );

	private static bool Usable( float value ) =>
		float.IsFinite( value ) && value > 1e-6f;

	private static bool Same( Vec2 a, Vec2 b ) =>
		MathF.Abs( a.x - b.x ) < 1e-6f && MathF.Abs( a.y - b.y ) < 1e-6f;
}
