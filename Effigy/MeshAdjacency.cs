using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Vertex-to-face adjacency in compressed sparse row form: one flat array of face indices, and an
/// offset per vertex saying where its run starts.
///
/// WHY THIS EXISTS ALONGSIDE <see cref="PolyMesh.BuildVertexFaces"/>, which answers the same
/// question. That one returns a <c>List&lt;int&gt;[]</c>, so a mesh with 240k vertices allocates
/// 240k List objects and 240k backing arrays to hold four ints each - about 90 MB of garbage for
/// 4 MB of answer, and the GC pause that goes with it lands in the middle of a viewport rebuild.
/// Two flat arrays hold the same data with two allocations.
///
/// The list version is kept because a dozen callers use it and most of them run once on a shape
/// small enough not to care. This one is for the paths that run per rebuild or per stroke.
///
/// NOT A HALF-EDGE STRUCTURE, and for the reason PolyMesh gives: this is derived on demand and
/// thrown away, so it cannot go stale. Build it, use it, drop it.
/// </summary>
public sealed class VertexFaces
{
	/// <summary>Where vertex v's run starts. Length is VertexCount + 1, so a run is
	/// Offsets[v]..Offsets[v+1] and the last entry needs no special case.</summary>
	public readonly int[] Offsets;

	/// <summary>Face indices, grouped by vertex.</summary>
	public readonly int[] Items;

	VertexFaces( int[] offsets, int[] items )
	{
		Offsets = offsets;
		Items = items;
	}

	public int VertexCount => Offsets.Length - 1;

	/// <summary>How many faces touch this vertex. Its valence, for a manifold interior vertex.</summary>
	public int CountAt( int vertex ) => Offsets[vertex + 1] - Offsets[vertex];

	/// <summary>The faces touching a vertex, as a view into <see cref="Items"/>. No allocation.</summary>
	public ReadOnlySpan<int> this[int vertex] =>
		new( Items, Offsets[vertex], Offsets[vertex + 1] - Offsets[vertex] );

	/// <summary>
	/// Counting sort in two passes: count each vertex's faces, prefix-sum that into offsets, then
	/// scatter. Linear, and allocates exactly the two arrays it returns.
	/// </summary>
	public static VertexFaces Build( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var vertexCount = mesh.Positions.Count;
		var offsets = new int[vertexCount + 1];
		var faces = mesh.Faces;

		// Pass one: how many faces per vertex, counted into offsets[v + 1] so the prefix sum
		// below turns the counts into starts in place.
		for ( var fi = 0; fi < faces.Count; fi++ )
		{
			var indices = faces[fi].Indices;

			for ( var i = 0; i < indices.Length; i++ )
			{
				// A malformed face can list the same vertex twice, and counting it twice would
				// inflate the valence - which is what skews the Catmull-Clark vertex rule and the
				// area-weighted normal. BuildVertexFaces spends a LINQ Distinct() per face on
				// this; a scan of the corners before this one is the same answer without the
				// enumerator, and faces have three or four corners.
				if ( Repeats( indices, i ) )
					continue;

				offsets[indices[i] + 1]++;
			}
		}

		for ( var v = 0; v < vertexCount; v++ )
			offsets[v + 1] += offsets[v];

		// Pass two: scatter. cursor walks a copy of the starts so offsets survives intact.
		var items = new int[offsets[vertexCount]];
		var cursor = new int[vertexCount];
		Array.Copy( offsets, cursor, vertexCount );

		for ( var fi = 0; fi < faces.Count; fi++ )
		{
			var indices = faces[fi].Indices;

			for ( var i = 0; i < indices.Length; i++ )
			{
				if ( Repeats( indices, i ) )
					continue;

				items[cursor[indices[i]]++] = fi;
			}
		}

		return new VertexFaces( offsets, items );
	}

	/// <summary>Whether this corner's vertex already appeared earlier in the same face.</summary>
	static bool Repeats( int[] indices, int corner )
	{
		var v = indices[corner];

		for ( var j = 0; j < corner; j++ )
		{
			if ( indices[j] == v )
				return true;
		}

		return false;
	}
}
