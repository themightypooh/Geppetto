using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A mesh's solid as a voxel grid, and distances measured through it — the half of geodesic
/// voxel binding that is not weighting.
///
/// SOLID = NOT REACHABLE FROM OUTSIDE. Cells a triangle passes through are marked as surface; a
/// flood from the padded corner through every non-surface cell finds the outside; everything else
/// is solid. That needs no consistent normals, no manifold and no single shell, and overlapping
/// parts simply union. An open mesh lets the flood in, which leaves its solid as the shell of
/// surface cells — distances then run along the skin, which is still the right answer for a
/// garment or a cape.
/// </summary>
public sealed class VoxelGrid
{
	public readonly int Nx, Ny, Nz;
	public readonly float Cell;
	public readonly Vec3 Origin;

	readonly int[] _solidIndex;   // per cell: index among solid cells, or -1
	readonly int[] _solidCells;   // per solid index: the cell

	public int SolidCount => _solidCells.Length;

	VoxelGrid( int nx, int ny, int nz, float cell, Vec3 origin, int[] solidIndex, int[] solidCells )
	{
		Nx = nx;
		Ny = ny;
		Nz = nz;
		Cell = cell;
		Origin = origin;
		_solidIndex = solidIndex;
		_solidCells = solidCells;
	}

	/// <summary>Voxelise with <paramref name="resolution"/> cells along the longest side.</summary>
	public static VoxelGrid Build( PolyMesh mesh, int resolution )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( resolution < 2 ) throw new ArgumentOutOfRangeException( nameof( resolution ) );

		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		var size = max - min;
		var longest = MathF.Max( size.x, MathF.Max( size.y, size.z ) );
		var cell = longest > 1e-9f ? longest / resolution : 1f;

		// Two cells of padding all round, so the outside is one connected region the flood can find.
		var origin = min - new Vec3( 2 * cell, 2 * cell, 2 * cell );
		var nx = (int)MathF.Ceiling( size.x / cell ) + 5;
		var ny = (int)MathF.Ceiling( size.y / cell ) + 5;
		var nz = (int)MathF.Ceiling( size.z / cell ) + 5;
		var total = nx * ny * nz;
		var surface = new bool[total];

		foreach ( var (a, b, c) in MeshLaplacian.TrianglesOf( mesh ) )
		{
			var pa = mesh.Positions[a];
			var pb = mesh.Positions[b];
			var pc = mesh.Positions[c];
			var longestEdge = MathF.Max( (pb - pa).Length, MathF.Max( (pc - pb).Length, (pa - pc).Length ) );

			// Samples closer than a third of a cell: no cell a triangle crosses is skipped.
			var steps = Math.Max( 1, (int)MathF.Ceiling( longestEdge / (cell / 3f) ) );

			for ( var i = 0; i <= steps; i++ )
			{
				for ( var j = 0; j <= steps - i; j++ )
				{
					var u = i / (float)steps;
					var w = j / (float)steps;
					var p = pa + (pb - pa) * u + (pc - pa) * w;
					var (x, y, z) = CellOf( p, origin, cell );

					if ( x >= 0 && y >= 0 && z >= 0 && x < nx && y < ny && z < nz )
						surface[(z * ny + y) * nx + x] = true;
				}
			}
		}

		// Flood the outside, 6-connected, from the padded corner.
		var outside = new bool[total];
		var queue = new Queue<int>();
		outside[0] = true;
		queue.Enqueue( 0 );

		while ( queue.Count > 0 )
		{
			var id = queue.Dequeue();
			var x = id % nx;
			var y = id / nx % ny;
			var z = id / (nx * ny);

			Visit( x - 1, y, z ); Visit( x + 1, y, z );
			Visit( x, y - 1, z ); Visit( x, y + 1, z );
			Visit( x, y, z - 1 ); Visit( x, y, z + 1 );
		}

		var solidIndex = new int[total];
		var solid = new List<int>();

		for ( var id = 0; id < total; id++ )
		{
			if ( outside[id] )
			{
				solidIndex[id] = -1;
				continue;
			}

			solidIndex[id] = solid.Count;
			solid.Add( id );
		}

		return new VoxelGrid( nx, ny, nz, cell, origin, solidIndex, solid.ToArray() );

		void Visit( int x, int y, int z )
		{
			if ( x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz )
				return;

			var id = (z * ny + y) * nx + x;

			if ( outside[id] || surface[id] )
				return;

			outside[id] = true;
			queue.Enqueue( id );
		}
	}

	static (int, int, int) CellOf( Vec3 p, Vec3 origin, float cell ) =>
		((int)MathF.Floor( (p.x - origin.x) / cell ), (int)MathF.Floor( (p.y - origin.y) / cell ), (int)MathF.Floor( (p.z - origin.z) / cell ));

	public bool IsSolid( int cell ) => cell >= 0 && cell < _solidIndex.Length && _solidIndex[cell] >= 0;

	/// <summary>Index of a solid cell among the solid cells — the index into a <see cref="GeodesicFrom"/> field.</summary>
	public int SolidIndex( int cell ) => _solidIndex[cell];

	public Vec3 Centre( int cell )
	{
		var x = cell % Nx;
		var y = cell / Nx % Ny;
		var z = cell / (Nx * Ny);
		return Origin + new Vec3( (x + 0.5f) * Cell, (y + 0.5f) * Cell, (z + 0.5f) * Cell );
	}

	/// <summary>The solid cell containing <paramref name="p"/>, or the nearest within two cells; -1 if none.</summary>
	public int SolidCellNear( Vec3 p )
	{
		var (cx, cy, cz) = CellOf( p, Origin, Cell );
		var best = -1;
		var bestD = float.MaxValue;

		for ( var r = 0; r <= 2 && best < 0; r++ )
		{
			for ( var z = cz - r; z <= cz + r; z++ )
				for ( var y = cy - r; y <= cy + r; y++ )
					for ( var x = cx - r; x <= cx + r; x++ )
					{
						if ( x < 0 || y < 0 || z < 0 || x >= Nx || y >= Ny || z >= Nz )
							continue;

						var id = (z * Ny + y) * Nx + x;

						if ( _solidIndex[id] < 0 )
							continue;

						var d = (Centre( id ) - p).LengthSquared;

						if ( d < bestD )
						{
							bestD = d;
							best = id;
						}
					}
		}

		return best;
	}

	/// <summary>
	/// Shortest distance through solid cells from the segment head→tail to every solid cell
	/// (Dijkstra over the 26-neighbourhood; diagonal steps cost √2 and √3 cells). Indexed by
	/// <see cref="SolidIndex"/>; infinity where the solid is not connected to the bone.
	///
	/// A bone that lies outside the solid entirely — a tail bone hanging past the tip — enters it at
	/// the solid cell nearest the segment, starting from the straight-line distance to it.
	/// </summary>
	public float[] GeodesicFrom( Vec3 head, Vec3 tail )
	{
		var dist = new float[_solidCells.Length];
		Array.Fill( dist, float.PositiveInfinity );
		var queue = new PriorityQueue<int, float>();
		var length = (tail - head).Length;
		var steps = Math.Max( 1, (int)MathF.Ceiling( length / (Cell * 0.5f) ) );
		var seeded = false;

		for ( var i = 0; i <= steps; i++ )
		{
			var p = Vec3.Lerp( head, tail, i / (float)steps );
			var (x, y, z) = CellOf( p, Origin, Cell );

			if ( x < 0 || y < 0 || z < 0 || x >= Nx || y >= Ny || z >= Nz )
				continue;

			var id = (z * Ny + y) * Nx + x;
			var s = _solidIndex[id];

			if ( s < 0 )
				continue;

			var d = (Centre( id ) - p).Length;

			if ( d < dist[s] )
			{
				dist[s] = d;
				queue.Enqueue( s, d );
				seeded = true;
			}
		}

		if ( !seeded && _solidCells.Length > 0 )
		{
			var best = 0;
			var bestD = float.MaxValue;

			for ( var s = 0; s < _solidCells.Length; s++ )
			{
				var c = Centre( _solidCells[s] );
				var d = (c - Closest( c, head, tail )).Length;
				if ( d < bestD ) { bestD = d; best = s; }
			}

			dist[best] = bestD;
			queue.Enqueue( best, bestD );
		}

		var diag2 = Cell * MathF.Sqrt( 2f );
		var diag3 = Cell * MathF.Sqrt( 3f );

		while ( queue.TryDequeue( out var s, out var d ) )
		{
			if ( d > dist[s] )
				continue;

			var id = _solidCells[s];
			var x = id % Nx;
			var y = id / Nx % Ny;
			var z = id / (Nx * Ny);

			for ( var dz = -1; dz <= 1; dz++ )
				for ( var dy = -1; dy <= 1; dy++ )
					for ( var dx = -1; dx <= 1; dx++ )
					{
						var axes = (dx != 0 ? 1 : 0) + (dy != 0 ? 1 : 0) + (dz != 0 ? 1 : 0);

						if ( axes == 0 )
							continue;

						int ux = x + dx, uy = y + dy, uz = z + dz;

						if ( ux < 0 || uy < 0 || uz < 0 || ux >= Nx || uy >= Ny || uz >= Nz )
							continue;

						var t = _solidIndex[(uz * Ny + uy) * Nx + ux];

						if ( t < 0 )
							continue;

						var nd = d + (axes == 1 ? Cell : axes == 2 ? diag2 : diag3);

						if ( nd < dist[t] )
						{
							dist[t] = nd;
							queue.Enqueue( t, nd );
						}
					}
		}

		return dist;
	}

	static Vec3 Closest( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var l2 = ab.LengthSquared;
		if ( l2 < 1e-12f ) return a;
		return a + ab * Math.Clamp( Vec3.Dot( p - a, ab ) / l2, 0f, 1f );
	}
}
