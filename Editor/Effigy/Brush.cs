using System;
using System.Collections.Generic;

namespace Effigy;

public enum BrushKind
{
	Smooth,
	Draw,
	Inflate,
	Grab,
	Flatten,
	Pinch
}

public enum BrushFalloff
{
	Smooth,
	Linear,
	Sharp,
	Constant
}

/// <summary>One sample on a stroke. The editor produces these; the kernel never learns what a mouse is.</summary>
public readonly struct BrushSample
{
	public readonly Vec3 Position;
	public readonly Vec3 Normal;
	public readonly Vec3 Direction;
	public readonly float Radius;
	public readonly float Strength;

	public BrushSample( Vec3 position, Vec3 normal, float radius, float strength, Vec3 direction = default )
	{
		Position = position;
		Normal = normal;
		Direction = direction;
		Radius = radius;
		Strength = strength;
	}
}

/// <summary>A list of samples plus the brush that consumes them.</summary>
public sealed class BrushStroke
{
	public BrushKind Kind;
	public BrushFalloff Falloff = BrushFalloff.Smooth;
	public bool MirrorX;
	public readonly List<BrushSample> Samples = new();
}

/// <summary>
/// Per-stroke undo: the original position of every vertex the stroke actually moved.
/// A naive undo snapshots the whole mesh; this stores only the working set.
/// </summary>
public sealed class BrushUndo
{
	readonly Dictionary<int, Vec3> _previous = new();

	public int Count => _previous.Count;

	internal void Remember( int vertex, Vec3 position ) => _previous.TryAdd( vertex, position );

	public void Restore( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		foreach ( var (vertex, position) in _previous )
			mesh.Positions[vertex] = position;
	}
}

/// <summary>
/// Brushes as pure functions over a mesh and a stroke. Spatial queries go through
/// <see cref="MeshBVH"/>; the kernel does not know about a cursor.
/// </summary>
public static class Brush
{
	public static BrushUndo Apply( PolyMesh mesh, BrushStroke stroke, SculptFrames frames, float[] mask = null, MeshBVH bvh = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( stroke is null )
			throw new ArgumentNullException( nameof( stroke ) );

		if ( frames is null )
			throw new ArgumentNullException( nameof( frames ) );

		if ( frames.Count != mesh.VertexCount )
			throw new ArgumentException( $"frames ({frames.Count}) and mesh ({mesh.VertexCount}) disagree" );

		if ( mask is not null && mask.Length != mesh.VertexCount )
			throw new ArgumentException( $"mask ({mask.Length}) and mesh ({mesh.VertexCount}) disagree" );

		bvh ??= MeshBVH.Build( mesh );

		var neighbors = mesh.BuildVertexEdges();
		var found = new List<int>();
		var undo = new BrushUndo();

		foreach ( var sample in stroke.Samples )
		{
			ApplySample( mesh, stroke, frames, mask, bvh, neighbors, found, undo, sample );

			if ( !stroke.MirrorX )
				continue;

			ApplySample( mesh, stroke, frames, mask, bvh, neighbors, found, undo, MirrorX( sample ) );
		}

		return undo;
	}

	static void ApplySample(
		PolyMesh mesh, BrushStroke stroke, SculptFrames frames, float[] mask,
		MeshBVH bvh, List<EdgeKey>[] neighbors, List<int> found, BrushUndo undo, BrushSample sample )
	{
		if ( sample.Radius <= 0f )
			return;

		bvh.VerticesInRadius( mesh, sample.Position, sample.Radius, found );

		var n = sample.Normal.LengthSquared >= 0.5f ? sample.Normal.Normal : new Vec3( 0, 0, 1 );
		var planePoint = Vec3.Zero;
		var planeCount = 0;

		if ( stroke.Kind == BrushKind.Flatten )
		{
			foreach ( var vi in found )
			{
				planePoint += mesh.Positions[vi];
				planeCount++;
			}

			if ( planeCount > 0 )
				planePoint /= planeCount;
			else
				planePoint = sample.Position;
		}

		foreach ( var vi in found )
		{
			var pos = mesh.Positions[vi];
			var dist = (pos - sample.Position).Length;
			var t = dist / sample.Radius;
			var weight = Falloff( t, stroke.Falloff ) * sample.Strength * (mask is null ? 1f : mask[vi]);

			if ( MathF.Abs( weight ) < 1e-8f )
				continue;

			Vec3 next;

			switch ( stroke.Kind )
			{
				case BrushKind.Smooth:
					next = Vec3.Lerp( pos, NeighbourAverage( mesh, neighbors[vi], vi ), Math.Clamp( weight, 0f, 1f ) );
					break;

				case BrushKind.Draw:
					next = pos + n * weight;
					break;

				case BrushKind.Inflate:
					next = pos + frames.At[vi].Normal * weight;
					break;

				case BrushKind.Grab:
					next = pos + sample.Direction * weight;
					break;

				case BrushKind.Flatten:
					var d = Vec3.Dot( pos - planePoint, n );
					next = pos - n * (d * Math.Clamp( weight, 0f, 1f ));
					break;

				case BrushKind.Pinch:
					var along = Vec3.Dot( pos - sample.Position, n );
					var closest = sample.Position + n * along;
					next = Vec3.Lerp( pos, closest, Math.Clamp( weight, 0f, 1f ) );
					break;

				default:
					continue;
			}

			if ( next.AlmostEquals( pos, 1e-8f ) )
				continue;

			undo.Remember( vi, pos );
			mesh.Positions[vi] = next;
		}

		bvh.Refit( mesh );
	}

	public static float Falloff( float t, BrushFalloff kind )
	{
		t = Math.Clamp( t, 0f, 1f );

		return kind switch
		{
			BrushFalloff.Constant => 1f,
			BrushFalloff.Linear => 1f - t,
			BrushFalloff.Sharp => (1f - t) * (1f - t),
			_ => 1f - t * t * (3f - 2f * t)
		};
	}

	static Vec3 NeighbourAverage( PolyMesh mesh, List<EdgeKey> edges, int vi )
	{
		if ( edges.Count == 0 )
			return mesh.Positions[vi];

		var sum = Vec3.Zero;

		foreach ( var key in edges )
			sum += mesh.Positions[key.A == vi ? key.B : key.A];

		return sum / edges.Count;
	}

	static BrushSample MirrorX( BrushSample s ) =>
		new(
			new Vec3( -s.Position.x, s.Position.y, s.Position.z ),
			new Vec3( -s.Normal.x, s.Normal.y, s.Normal.z ),
			s.Radius,
			s.Strength,
			new Vec3( -s.Direction.x, s.Direction.y, s.Direction.z ) );
}
