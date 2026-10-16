using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Puts an existing dense sculpt's detail onto a <see cref="MultiresSculpt"/> built from its
/// cage — ZBrush's ProjectAll, Blender's Multires "reshape" from a shrinkwrapped copy. The dense
/// sculpt need not share vertex order with our subdivision: each vertex of the level is moved onto
/// the target surface along its own normal (both ways, nearest hit), falling back to the closest
/// point. Once recorded, the detail rides any cage edit with the same topology, which is how a
/// sculptor opens a jaw or drags a face: move the low level, keep the skin.
/// </summary>
public static class DetailProjection
{
	/// <summary>
	/// Projects the evaluated <paramref name="level"/> of <paramref name="sculpt"/> onto
	/// <paramref name="target"/> and records it. A vertex whose hit is farther than
	/// <paramref name="maxDistance"/> stays where it is. Returns the mean projection distance.
	/// </summary>
	public static float Capture( MultiresSculpt sculpt, int level, PolyMesh target, float maxDistance, Func<int, bool> skip = null )
	{
		var mesh = sculpt.Evaluate( level );
		var bvh = MeshBVH.Build( target );
		var normals = mesh.ComputeVertexNormals();
		var total = 0f; var count = 0;
		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			if ( skip is not null && skip( i ) ) continue;
			var p = mesh.Positions[i];
			var n = normals[i];
			Vec3? best = null;
			if ( n.LengthSquared > 1e-12f && bvh.TryProjectSurface( target, p, n, maxDistance, 0f, out var hit, positive: true, negative: true ) )
				best = hit;
			if ( best is null && bvh.NearestSurface( target, p, maxDistance ) is { } near )
				best = near.Point;
			if ( best is not { } q ) continue;
			total += (q - p).Length; count++;
			mesh.Positions[i] = q;
		}
		sculpt.Record( level, mesh );
		return count > 0 ? total / count : 0f;
	}
}
