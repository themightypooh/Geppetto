using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Applying an <see cref="Xform"/> to a mesh.
///
/// Split out of Xform.cs so that file is pure arithmetic on Vec3 and Xform with no mesh types in
/// it. That matters for exactly one reason: the game assembly's sandbox forbids the filesystem, so
/// only part of this kernel can ever ship to it, and the smaller that part's dependencies are the
/// more of it a game can use. Xform pulling in PolyMesh dragged half the kernel across that line
/// for the sake of two functions that live here.
/// </summary>
public static class MeshTransform
{
	/// <summary>
	/// Transform a mesh in place, reversing face winding when the transform flips handedness.
	///
	/// Forgetting the reversal is the single most common mirror bug: the mirrored half renders
	/// black or lit from inside, and the model reads as fine in wireframe. There is a test for it.
	/// </summary>
	public static void Apply( PolyMesh mesh, Xform xform )
	{
		for ( var i = 0; i < mesh.Positions.Count; i++ )
			mesh.Positions[i] = xform.TransformPoint( mesh.Positions[i] );

		if ( !xform.FlipsWinding )
			return;

		foreach ( var f in mesh.Faces )
		{
			Array.Reverse( f.Indices );
			Array.Reverse( f.UVs );
		}
	}

	public static PolyMesh Transformed( PolyMesh mesh, Xform xform )
	{
		var copy = mesh.Clone();
		Apply( copy, xform );
		return copy;
	}

	/// <summary>
	/// Merge `source` into `target`, offsetting indices. Does not weld — two bodies combined this
	/// way stay topologically separate, which is correct for a pattern and is why Validate reports
	/// them as one mesh with several shells rather than as non-manifold.
	/// </summary>
	public static void Append( PolyMesh target, PolyMesh source )
	{
		var offset = target.Positions.Count;

		// Weights have to be reconciled BEFORE the position lists merge, because both sides are
		// padded against their own current vertex count. Merging an unrigged body into a rigged one
		// is normal — a rig usually arrives after some of the model does — so the unrigged side is
		// padded with empty influences rather than treated as an error.
		if ( target.Skin is not null || source.Skin is not null )
		{
			target.Skin ??= new SkinWeights( target.Positions.Count );

			while ( target.Skin.Count < target.Positions.Count )
				target.Skin.Vertices.Add( new[] { new BoneWeight( 0, 1f ) } );

			// An unrigged body merged into a rigged one gets bound to the FIRST BONE rather than
			// left empty. Empty influences pass IsRigged, fail Validate, and export as "no links",
			// which studiomdl reads as the parent bone column - so the body silently ends up rigged
			// to whatever bone 0 happens to be, discovered much later and somewhere else. Binding it
			// explicitly is the same outcome, stated out loud, and it keeps the partition of unity
			// that everything downstream assumes.
			var unrigged = new[] { new BoneWeight( 0, 1f ) };

			for ( var i = 0; i < source.Positions.Count; i++ )
			{
				target.Skin.Vertices.Add( source.Skin is not null && i < source.Skin.Count && source.Skin[i].Length > 0
					? (BoneWeight[])source.Skin[i].Clone()
					: unrigged );
			}
		}

		target.Positions.AddRange( source.Positions );

		// Vertex colours merge the way the skin does: pad whichever side lacks them with transparent,
		// so an unpainted body merged into a painted one simply carries no paint of its own. Done
		// after positions are appended but keyed off the pre-merge offset, which is what keeps the
		// colours parallel to the positions they describe.
		if ( target.VertexColors is not null || source.VertexColors is not null )
		{
			target.VertexColors ??= new Vec4[offset];

			var merged = new Vec4[offset + source.Positions.Count];

			for ( var i = 0; i < offset; i++ )
				merged[i] = i < target.VertexColors.Length ? target.VertexColors[i] : Vec4.Zero;

			for ( var i = 0; i < source.Positions.Count; i++ )
				merged[offset + i] = source.VertexColors is not null && i < source.VertexColors.Length
					? source.VertexColors[i]
					: Vec4.Zero;

			target.VertexColors = merged;
		}

		// Paint is one atlas per body, not per vertex, so it cannot be padded the way colours are. A
		// merged mesh can show one body's atlas, so the target keeps its own and only adopts the
		// source's when it has none. TWO painted bodies is the case this drops, and it is named by
		// PaintBind.MergeDropsPaint so a caller can say so rather than discovering it on compile.
		if ( target.Paint is null && source.Paint is not null )
			target.Paint = source.Paint;

		foreach ( var f in source.Faces )
		{
			var indices = new int[f.Count];

			for ( var i = 0; i < f.Count; i++ )
				indices[i] = f.Indices[i] + offset;

			target.AddFace( indices, (Vec2[])f.UVs.Clone(), f.Material );
		}
	}
}

/// <summary>
/// Merge vertices that sit within a distance of each other, Blender's Merge by Distance.
///
/// The half of the mesh-authoring workflow a vertex count spends forever without: an import or a
/// mirror seam that left every face owning its own copy of every corner reads as valid from outside
/// but is topologically a pile of loose quads — Catmull-Clark sees all boundary, a weight brush
/// smears, and nothing is actually connected. This collapses coincident vertices back to one index,
/// drops the faces the weld squeezes flat, and hands back a mesh whose connectivity means what its
/// shape says.
///
/// Grouping is union-find over a spatial hash (cells the size of the tolerance, searching the 27
/// surrounding cells), so the result is the same regardless of vertex order. A welded vertex keeps
/// the lowest original index's position, skin weights and colour. Duplicate faces the weld makes
/// coincident are left for the caller to notice, not silently removed.
/// </summary>
public static class MeshWeld
{
	public static PolyMesh Weld( PolyMesh mesh, float tolerance )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( tolerance <= 0f || mesh.VertexCount < 2 )
			return mesh.Clone();

		var parent = new int[mesh.VertexCount];
		var rank = new byte[mesh.VertexCount];

		for ( var i = 0; i < parent.Length; i++ )
			parent[i] = i;

		// Spatial hash, cells the size of the tolerance.
		var cells = new Dictionary<(long, long, long), List<int>>();

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var key = Cell( mesh.Positions[v], tolerance );

			if ( !cells.TryGetValue( key, out var list ) )
				cells[key] = list = new List<int>( 4 );

			list.Add( v );
		}

		var toleranceSquared = tolerance * tolerance;

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var p = mesh.Positions[v];
			var cell = Cell( p, tolerance );

			// Two points within the tolerance can straddle a cell boundary by at most one cell, so
			// the 27-neighbourhood covers every candidate pair.
			for ( var dx = -1; dx <= 1; dx++ )
			for ( var dy = -1; dy <= 1; dy++ )
			for ( var dz = -1; dz <= 1; dz++ )
			{
				var key = (cell.Item1 + dx, cell.Item2 + dy, cell.Item3 + dz);

				if ( !cells.TryGetValue( key, out var list ) )
					continue;

				foreach ( var u in list )
				{
					if ( u <= v )
						continue;

					if ( (mesh.Positions[u] - p).LengthSquared <= toleranceSquared )
						Union( parent, rank, v, u );
				}
			}
		}

		// Representative of each component is its lowest index, so the output order is deterministic.
		var representative = new int[mesh.VertexCount];
		var hasRepresentative = new bool[mesh.VertexCount];

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var root = Find( parent, v );

			if ( !hasRepresentative[root] || v < representative[root] )
			{
				representative[root] = v;
				hasRepresentative[root] = true;
			}
		}

		var compact = new int[mesh.VertexCount];
		Array.Fill( compact, -1 );
		var survivors = new List<int>();

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var rep = representative[Find( parent, v )];

			if ( compact[rep] >= 0 )
				continue;

			compact[rep] = survivors.Count;
			survivors.Add( rep );
		}

		var remap = new int[mesh.VertexCount];

		for ( var v = 0; v < mesh.VertexCount; v++ )
			remap[v] = compact[representative[Find( parent, v )]];

		var result = new PolyMesh();

		foreach ( var s in survivors )
			result.AddVertex( mesh.Positions[s] );

		if ( mesh.IsRigged )
		{
			var skin = new SkinWeights();

			foreach ( var s in survivors )
				skin.Vertices.Add( (BoneWeight[])mesh.Skin[s].Clone() );

			result.Skin = skin;
		}

		if ( mesh.VertexColors is not null )
		{
			var colors = new Vec4[survivors.Count];

			for ( var i = 0; i < survivors.Count; i++ )
				colors[i] = mesh.VertexColors[survivors[i]];

			result.VertexColors = colors;
		}

		if ( mesh.Paint is not null )
			result.Paint = mesh.Paint.Clone();

		foreach ( var f in mesh.Faces )
		{
			var indices = new int[f.Count];

			for ( var i = 0; i < f.Count; i++ )
				indices[i] = remap[f.Indices[i]];

			if ( DistinctCount( indices ) < 3 )
				continue;

			result.AddFace( indices, (Vec2[])f.UVs.Clone(), f.Material );
		}

		return result;
	}

	static (long, long, long) Cell( Vec3 p, float tolerance )
	{
		var scale = 1f / tolerance;
		return (
			(long)MathF.Floor( p.x * scale ),
			(long)MathF.Floor( p.y * scale ),
			(long)MathF.Floor( p.z * scale ));
	}

	static int DistinctCount( int[] values )
	{
		var count = 0;

		for ( var i = 0; i < values.Length; i++ )
		{
			var seen = false;

			for ( var j = 0; j < i; j++ )
			{
				if ( values[i] == values[j] )
				{
					seen = true;
					break;
				}
			}

			if ( !seen )
				count++;
		}

		return count;
	}

	static int Find( int[] parent, int v )
	{
		while ( parent[v] != v )
		{
			parent[v] = parent[parent[v]];
			v = parent[v];
		}

		return v;
	}

	static void Union( int[] parent, byte[] rank, int a, int b )
	{
		a = Find( parent, a );
		b = Find( parent, b );

		if ( a == b )
			return;

		if ( rank[a] < rank[b] )
		{
			parent[a] = b;
		}
		else if ( rank[a] > rank[b] )
		{
			parent[b] = a;
		}
		else
		{
			parent[b] = a;
			rank[a]++;
		}
	}
}

/// <summary>
/// The live modifiers a mesh edit carries — Blender's modifier stack, cut down to the four that
/// modelling a character actually leans on. They run on the way out of the edit, so the mesh you
/// edit stays the simple cage and the body downstream gets the result: model half a head and see
/// the whole one, block a limb out in a few quads and see it subdivided, draw a garment as a sheet
/// and see its thickness.
///
/// Always in one order — mirror, array, subdivide, solidify — the order that gives the answer
/// people expect: the mirror joins before it is smoothed, and the thickness follows the smoothed
/// surface rather than the cage.
/// </summary>
public static class MeshModifiers
{
	public static PolyMesh Apply( PolyMesh mesh, bool mirrorX, int arrayCount, float arrayGap, int subdivide, float solidify, float seam = 1e-3f )
	{
		var result = mirrorX ? MirrorX( mesh, seam ) : mesh.Clone();

		if ( arrayCount > 1 )
			result = ArrayX( result, arrayCount, arrayGap );

		if ( subdivide > 0 )
			result = CatmullClark.Subdivide( result, Math.Min( subdivide, 3 ) );

		if ( solidify > 0f )
			result = MeshSolidify.Solidify( result, solidify );

		return result;
	}

	/// <summary>
	/// The mesh plus its mirror image across x = 0. Vertices on the plane (within
	/// <paramref name="seam"/>) are shared by both halves, so a half-model joins up down the middle
	/// instead of leaving a crack. Skin weights are copied as they are, not swapped left for right:
	/// mirror, then rig.
	/// </summary>
	public static PolyMesh MirrorX( PolyMesh mesh, float seam = 1e-3f )
	{
		var result = mesh.Clone();
		var n = mesh.VertexCount;
		var map = new int[n];
		var colours = result.VertexColors is null ? null : new List<Vec4>( result.VertexColors );

		for ( var v = 0; v < n; v++ )
		{
			var p = mesh.Positions[v];

			if ( MathF.Abs( p.x ) <= seam )
			{
				map[v] = v;
				continue;
			}

			map[v] = result.Positions.Count;
			result.Positions.Add( new Vec3( -p.x, p.y, p.z ) );

			if ( result.Skin is not null && result.Skin.Count > v )
				result.Skin.Vertices.Add( result.Skin[v] );

			colours?.Add( colours[v] );
		}

		if ( colours is not null )
			result.VertexColors = colours.ToArray();

		foreach ( var face in mesh.Faces )
		{
			var count = face.Indices.Length;
			var idx = new int[count];
			var uvs = new Vec2[count];

			// Mirroring flips handedness, so each face is wound backwards to keep facing out.
			for ( var i = 0; i < count; i++ )
			{
				idx[i] = map[face.Indices[count - 1 - i]];
				uvs[i] = face.UVs[count - 1 - i];
			}

			result.AddFace( idx, uvs, face.Material );
		}

		return result;
	}

	/// <summary>The mesh repeated <paramref name="count"/> times along X, each copy one width plus
	/// <paramref name="gap"/> further on.</summary>
	public static PolyMesh ArrayX( PolyMesh mesh, int count, float gap )
	{
		if ( mesh.VertexCount == 0 || count < 2 )
			return mesh.Clone();

		var minX = float.MaxValue;
		var maxX = float.MinValue;
		foreach ( var p in mesh.Positions )
		{
			minX = MathF.Min( minX, p.x );
			maxX = MathF.Max( maxX, p.x );
		}

		var stride = maxX - minX + gap;
		var result = mesh.Clone();
		var colours = result.VertexColors is null ? null : new List<Vec4>( result.VertexColors );
		var n = mesh.VertexCount;

		for ( var k = 1; k < count; k++ )
		{
			var start = result.Positions.Count;

			for ( var v = 0; v < n; v++ )
			{
				result.Positions.Add( mesh.Positions[v] + new Vec3( stride * k, 0f, 0f ) );

				if ( result.Skin is not null && mesh.Skin is not null && mesh.Skin.Count > v )
					result.Skin.Vertices.Add( mesh.Skin[v] );

				if ( colours is not null && mesh.VertexColors is not null )
					colours.Add( mesh.VertexColors[v] );
			}

			foreach ( var face in mesh.Faces )
			{
				var idx = new int[face.Indices.Length];
				for ( var i = 0; i < idx.Length; i++ )
					idx[i] = face.Indices[i] + start;

				result.AddFace( idx, (Vec2[])face.UVs.Clone(), face.Material );
			}
		}

		if ( colours is not null )
			result.VertexColors = colours.ToArray();

		return result;
	}
}
