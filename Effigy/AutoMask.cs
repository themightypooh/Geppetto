using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Automatic masks for a sculpt stroke, computed from the mesh rather than painted, the way
/// Blender's auto-masking options work: each is a per-vertex factor 0..1 that multiplies into the
/// brush's mask (Brush.Apply's <c>mask</c>), and they combine by multiplying. Measured against
/// Blender's own (Effigy.Tests --parity).
/// </summary>
public static class AutoMask
{
	/// <summary>Only the piece of the mesh the stroke started on: 1 on the connected part holding
	/// <paramref name="seed"/>, 0 elsewhere — so a brush can't reach through into a separate piece next to it.</summary>
	public static float[] Topology( PolyMesh mesh, int seed )
	{
		var n = mesh.VertexCount;
		var mask = new float[n];
		if ( seed < 0 || seed >= n ) return mask;
		var nb = MeshLoops.Neighbours( n, mesh.Faces );
		var stack = new Stack<int>();
		stack.Push( seed );
		mask[seed] = 1f;
		while ( stack.Count > 0 )
		{
			var v = stack.Pop();
			foreach ( var u in nb[v] )
				if ( mask[u] == 0f ) { mask[u] = 1f; stack.Push( u ); }
		}
		return mask;
	}

	/// <summary>The vertex nearest <paramref name="point"/> that faces the way the stroke looks
	/// (<paramref name="normal"/>): where a stroke's Topology mask should start.</summary>
	public static int SeedAt( PolyMesh mesh, Vec3 point, Vec3 normal )
	{
		var best = -1; var bd = float.MaxValue;
		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var d = mesh.Positions[v] - point;
			// Prefer what lies on the viewer's side of the point: a sheet over another wins.
			var score = d.LengthSquared - 0.5f * Vec3.Dot( d, normal );
			if ( score < bd ) { bd = score; best = v; }
		}
		return best;
	}

	/// <summary>Hold open edges still: 0 on the mesh boundary, rising to 1 over
	/// <paramref name="steps"/> rings of vertices in from it.</summary>
	public static float[] BoundaryEdges( PolyMesh mesh, int steps = 1 )
	{
		var n = mesh.VertexCount;
		var uses = new Dictionary<(int, int), int>();
		foreach ( var f in mesh.Faces )
			for ( var k = 0; k < f.Indices.Length; k++ )
			{
				int a = f.Indices[k], b = f.Indices[(k + 1) % f.Indices.Length];
				var key = a < b ? (a, b) : (b, a);
				uses[key] = uses.GetValueOrDefault( key ) + 1;
			}
		var ring = Enumerable.Repeat( int.MaxValue, n ).ToArray();
		var queue = new Queue<int>();
		foreach ( var ((a, b), u) in uses )
			if ( u == 1 )
				foreach ( var v in new[] { a, b } )
					if ( ring[v] != 0 ) { ring[v] = 0; queue.Enqueue( v ); }
		var nb = MeshLoops.Neighbours( n, mesh.Faces );
		while ( queue.Count > 0 )
		{
			var v = queue.Dequeue();
			if ( ring[v] >= steps ) continue;
			foreach ( var u in nb[v] )
				if ( ring[u] > ring[v] + 1 ) { ring[u] = ring[v] + 1; queue.Enqueue( u ); }
		}
		var steps1 = Math.Max( steps, 1 );
		return ring.Select( r => r == int.MaxValue ? 1f : Math.Clamp( r / (float)steps1, 0f, 1f ) ).ToArray();
	}

	/// <summary>
	/// Work only in the crevices (or, <paramref name="inverted"/>, only on the ridges), as Blender's
	/// cavity auto-masking does: 1 in cavities, 0 on peaks, a sharp switch around the mean. <paramref name="factor"/> is how strongly a cavity counts,
	/// <paramref name="blur"/> how many times the mask is smoothed over the surface.
	/// </summary>
	public static float[] Cavity( PolyMesh mesh, float factor = 0.5f, int blur = 2, bool inverted = false )
	{
		var cav = SurfaceDetail.Cavity( mesh, 6 );
		// In units of the mesh's own cavity spread, so the factor means the same on any scale.
		var spread = MathF.Sqrt( cav.Select( c => c * c ).DefaultIfEmpty( 0f ).Average() ) + 1e-9f;
		var mask = cav.Select( c => Math.Clamp( 0.5f + c / spread * factor * 1.6f, 0f, 1f ) ).ToArray();
		var nb = MeshLoops.Neighbours( mesh.VertexCount, mesh.Faces );
		for ( var it = 0; it < blur; it++ )
		{
			var next = new float[mask.Length];
			for ( var v = 0; v < mask.Length; v++ )
			{
				var sum = mask[v]; var c = 1;
				foreach ( var u in nb[v] ) { sum += mask[u]; c++; }
				next[v] = sum / c;
			}
			mask = next;
		}
		return inverted ? mask.Select( m => 1f - m ).ToArray() : mask;
	}

	/// <summary>Only surface facing within <paramref name="limitDegrees"/> of the stroke's starting
	/// normal, fading out over <paramref name="falloffDegrees"/> more.</summary>
	public static float[] StartNormal( PolyMesh mesh, Vec3 normal, float limitDegrees = 20f, float falloffDegrees = 25f )
	{
		var normals = SurfaceDetail.VertexNormals( mesh );
		var n = normal.Normal;
		return normals.Select( v =>
		{
			var angle = MathF.Acos( Math.Clamp( Vec3.Dot( v, n ), -1f, 1f ) ) * 180f / MathF.PI;
			var t = Math.Clamp( (angle - limitDegrees) / MathF.Max( falloffDegrees, 1e-3f ), 0f, 1f );
			return 1f - t * t * (3f - 2f * t);
		} ).ToArray();
	}

	/// <summary>Masks combined (multiplied), any of them null skipped.</summary>
	public static float[] Combine( params float[][] masks )
	{
		var live = masks.Where( m => m is not null ).ToArray();
		if ( live.Length == 0 ) return null;
		var result = (float[])live[0].Clone();
		foreach ( var m in live.Skip( 1 ) )
			for ( var v = 0; v < result.Length; v++ ) result[v] *= m[v];
		return result;
	}
}
