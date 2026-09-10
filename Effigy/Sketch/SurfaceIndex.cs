using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The mesh-wide groundwork <see cref="FaceSurface.FromFace(SurfaceIndex, int)"/> needs, computed
/// once for a mesh instead of once per face.
///
/// WHY THIS WAS SPLIT OUT. Growing the surface under the cursor is a flood fill that visits the
/// surface and stops - a few dozen faces on a real part. Everything it needs in order to START,
/// though, is global: the weld tolerance comes from the mesh's bounds, the weld map compares every
/// vertex against its neighbours, and the adjacency it walks is built from every face. So the
/// local answer was costing three full passes over the mesh, and FromFace was called once per face
/// the cursor moved onto.
///
/// On a part out of the sketcher that is invisible. On a 500k-face import it is roughly a second
/// of work per newly hovered face, repeated, with the viewport frozen for each one - the tool
/// looks hung while the mouse is moving and recovers the moment it stops. The surface cache above
/// it did not help, because a cache keyed on the face you just moved onto misses by construction.
///
/// Held by whoever owns the mesh and dropped when the mesh instance changes. Bodies are rebuilt
/// from scratch on every edit, so a new PolyMesh IS the invalidation signal - the same bargain the
/// viewport's pick trees make.
/// </summary>
public sealed class SurfaceIndex
{
	public readonly PolyMesh Mesh;

	/// <summary>Vertex index to the index of the first vertex sharing its position. See
	/// <see cref="FaceSurface"/> on why the surface walk is welded rather than indexed.</summary>
	public readonly int[] Weld;

	/// <summary>Plane-distance tolerance, scaled to the part's size.</summary>
	public readonly float Tolerance;

	// Welded edge to the faces using it, in compressed sparse row form: the dictionary gives an
	// edge its slot, and that slot's faces are _items[_offsets[slot].._offsets[slot + 1]].
	//
	// A Dictionary<EdgeKey, List<int>> is the obvious shape and allocates a List plus its backing
	// array for every edge in the mesh - about two million objects on a dense import, all of them
	// holding two ints. This holds the same answer in three arrays.
	readonly Dictionary<EdgeKey, int> _slots;
	readonly int[] _offsets;
	readonly int[] _items;

	SurfaceIndex( PolyMesh mesh, int[] weld, float tolerance,
		Dictionary<EdgeKey, int> slots, int[] offsets, int[] items )
	{
		Mesh = mesh;
		Weld = weld;
		Tolerance = tolerance;
		_slots = slots;
		_offsets = offsets;
		_items = items;
	}

	/// <summary>The faces sharing a welded edge, as a view into the flat array. Empty if the edge
	/// is not one of the mesh's.</summary>
	public ReadOnlySpan<int> FacesOn( EdgeKey key )
	{
		if ( !_slots.TryGetValue( key, out var slot ) )
			return ReadOnlySpan<int>.Empty;

		return new ReadOnlySpan<int>( _items, _offsets[slot], _offsets[slot + 1] - _offsets[slot] );
	}

	/// <summary>The welded key for a face's corner-th edge.</summary>
	public EdgeKey EdgeAt( Face face, int corner ) => new(
		Weld[face.Indices[corner]],
		Weld[face.Indices[corner + 1 == face.Count ? 0 : corner + 1]] );

	public static SurfaceIndex Build( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		// Scaled to the part, for the reason every other tolerance in the sketcher is: a constant
		// generous on a 100-unit block silently welds every vertex of a 0.1-unit one.
		var tolerance = MathF.Max( mesh.BoundsDiagonal * 1e-4f, 1e-5f );
		var weld = BuildWeld( mesh, tolerance );

		// Pass one: give each distinct welded edge a slot, and count its faces.
		//
		// The slot each corner resolved to is REMEMBERED rather than looked up again below. The
		// scatter pass needs the same answer for the same corners, and re-deriving it costs a
		// second welded-key construction and a second dictionary probe for every corner in the
		// mesh - four million of each on a dense import, which was most of this function. An int
		// per corner is a few megabytes to not do that.
		var cornerCount = 0;

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			var face = mesh.Faces[i];

			if ( face.Count >= 3 )
				cornerCount += face.Count;
		}

		var slots = new Dictionary<EdgeKey, int>( cornerCount );
		var cornerSlots = new int[cornerCount];
		var counts = new List<int>( cornerCount );
		var corner = 0;

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			var face = mesh.Faces[i];

			if ( face.Count < 3 )
				continue;

			for ( var c = 0; c < face.Count; c++ )
			{
				var key = new EdgeKey(
					weld[face.Indices[c]],
					weld[face.Indices[c + 1 == face.Count ? 0 : c + 1]] );

				if ( slots.TryGetValue( key, out var slot ) )
				{
					counts[slot]++;
				}
				else
				{
					slot = counts.Count;
					slots[key] = slot;
					counts.Add( 1 );
				}

				cornerSlots[corner++] = slot;
			}
		}

		var offsets = new int[counts.Count + 1];

		for ( var s = 0; s < counts.Count; s++ )
			offsets[s + 1] = offsets[s] + counts[s];

		// Pass two: scatter, straight off the remembered slots. No hashing here at all.
		var items = new int[offsets[counts.Count]];
		var cursor = new int[counts.Count];
		Array.Copy( offsets, cursor, counts.Count );
		corner = 0;

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			var face = mesh.Faces[i];

			if ( face.Count < 3 )
				continue;

			for ( var c = 0; c < face.Count; c++ )
				items[cursor[cornerSlots[corner++]]++] = i;
		}

		return new SurfaceIndex( mesh, weld, tolerance, slots, offsets, items );
	}

	/// <summary>
	/// Vertex index to the index of the first vertex sharing its position, within
	/// <paramref name="tolerance"/>.
	///
	/// A HASH GRID, AS BEFORE, BUT PROBED ONCE INSTEAD OF TWENTY-SEVEN TIMES.
	///
	/// The previous version made cells exactly one tolerance across, so a pair straddling a
	/// boundary could be in any of the 26 neighbours and all 27 had to be looked up - 27 hashes of
	/// a (int, int, int) tuple into a Dictionary of List buckets, per vertex. On a million-vertex
	/// import that is 27 million probes and a couple of million List allocations, and it was two
	/// seconds of the first hover.
	///
	/// Cells are four tolerances across here, and each vertex looks up only the cells its own
	/// tolerance box actually touches. A box one tolerance wide inside a cell four wide is fully
	/// contained unless it is near a face, so the overwhelming majority of vertices probe exactly
	/// one cell and the worst case is eight. This is EXACT, not an approximation: the box is what
	/// decides, so any cell that could hold a match is still visited.
	///
	/// The table itself is open-addressed over flat arrays with the cell coordinates packed into a
	/// long, so there are no per-cell List objects and the whole structure is four allocations.
	///
	/// WHICH VERTEX OF A CLUSTER WINS can differ from the old version's answer when a vertex sits
	/// within tolerance of two clusters that are not within tolerance of each other. That was
	/// already decided by iteration order rather than by anything meaningful, and the case this is
	/// actually for - a boolean leaving exactly coincident vertices behind - has no ambiguity in
	/// it at all.
	/// </summary>
	static int[] BuildWeld( PolyMesh mesh, float tolerance )
	{
		var count = mesh.Positions.Count;
		var weld = new int[count];

		if ( count == 0 )
			return weld;

		var cell = MathF.Max( tolerance, 1e-6f ) * 4f;
		var inverseCell = 1f / cell;
		var toleranceSquared = tolerance * tolerance;

		// Open addressing needs headroom or it degenerates into a linear scan; a power-of-two
		// table at least twice the vertex count keeps the load factor under a half, which is where
		// linear probing still behaves.
		var capacity = 1;
		while ( capacity < count * 2 ) capacity <<= 1;
		var mask = capacity - 1;

		var keys = new long[capacity];
		var heads = new int[capacity];
		var next = new int[count];

		Array.Fill( heads, -1 );

		for ( var i = 0; i < count; i++ )
		{
			var p = mesh.Positions[i];
			weld[i] = i;

			// The cells the tolerance box around p touches. Equal bounds - the common case - means
			// one cell on that axis.
			var minX = (int)MathF.Floor( (p.x - tolerance) * inverseCell );
			var maxX = (int)MathF.Floor( (p.x + tolerance) * inverseCell );
			var minY = (int)MathF.Floor( (p.y - tolerance) * inverseCell );
			var maxY = (int)MathF.Floor( (p.y + tolerance) * inverseCell );
			var minZ = (int)MathF.Floor( (p.z - tolerance) * inverseCell );
			var maxZ = (int)MathF.Floor( (p.z + tolerance) * inverseCell );

			var matched = false;

			for ( var cx = minX; cx <= maxX && !matched; cx++ )
			{
				for ( var cy = minY; cy <= maxY && !matched; cy++ )
				{
					for ( var cz = minZ; cz <= maxZ && !matched; cz++ )
					{
						var slot = Find( keys, heads, Pack( cx, cy, cz ), mask );

						if ( slot < 0 )
							continue;

						for ( var other = heads[slot]; other >= 0; other = next[other] )
						{
							if ( (mesh.Positions[other] - p).LengthSquared > toleranceSquared )
								continue;

							weld[i] = weld[other];
							matched = true;
							break;
						}
					}
				}
			}

			if ( matched )
				continue;

			// Unmatched, so it becomes a representative and joins its own cell's chain.
			var own = Insert( keys, heads, Pack(
				(int)MathF.Floor( p.x * inverseCell ),
				(int)MathF.Floor( p.y * inverseCell ),
				(int)MathF.Floor( p.z * inverseCell ) ), mask );

			next[i] = heads[own];
			heads[own] = i;
		}

		return weld;
	}

	/// <summary>
	/// Cell coordinates packed into one long, biased so negative coordinates keep their order and
	/// stay distinct. 21 bits an axis covers a grid four million cells across, which at four
	/// tolerances a cell is far more range than a model can use.
	/// </summary>
	static long Pack( int x, int y, int z )
	{
		var px = (long)(x + 1048576) & 0x1FFFFF;
		var py = (long)(y + 1048576) & 0x1FFFFF;
		var pz = (long)(z + 1048576) & 0x1FFFFF;

		// Non-zero, so a cell at the origin does not hash to slot zero and pile up there.
		return px | (py << 21) | (pz << 42) | 1L << 63;
	}

	/// <summary>Slot holding this key, or -1. Linear probing; an empty key is the terminator, and
	/// Pack never returns zero so zero means empty.</summary>
	static int Find( long[] keys, int[] heads, long key, int mask )
	{
		for ( var slot = Hash( key ) & mask; ; slot = (slot + 1) & mask )
		{
			var k = keys[slot];

			if ( k == key ) return slot;
			if ( k == 0 ) return -1;
		}
	}

	/// <summary>Slot for this key, claiming an empty one if it is new. The table is sized so it
	/// can never fill.</summary>
	static int Insert( long[] keys, int[] heads, long key, int mask )
	{
		for ( var slot = Hash( key ) & mask; ; slot = (slot + 1) & mask )
		{
			var k = keys[slot];

			if ( k == key ) return slot;

			if ( k == 0 )
			{
				keys[slot] = key;
				return slot;
			}
		}
	}

	/// <summary>Fibonacci hashing. The packed key's low bits are the x coordinate, so using them
	/// directly would put a whole row of cells in consecutive slots and turn linear probing into a
	/// scan; multiplying by the golden-ratio constant spreads them.</summary>
	static int Hash( long key ) => (int)(((ulong)key * 11400714819323198485UL) >> 40) & int.MaxValue;
}
