using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Where a mesh meets a plane, as line segments.
///
/// This exists for one job: showing the FOOTPRINT another body leaves on a face. Effigy does not
/// union bodies, so an extrude standing on a slab is two separate solids that happen to overlap,
/// and the slab's top face is one uninterrupted rectangle with no idea anything is sitting on it.
/// Outlining only that rectangle makes a face with a block on it look completely clear.
///
/// Two cases, and both turn up constantly because they are the two ways bodies get stacked:
/// a solid that PASSES THROUGH the plane, whose faces cross it and produce a chord each; and one
/// that SITS ON it, whose bottom face lies in the plane and produces its own outline. Handling
/// only the first would miss every part built by sketching on a face, which is most of them.
/// </summary>
public static class MeshSection
{
	/// <summary>
	/// Every segment where <paramref name="mesh"/> meets the plane through
	/// <paramref name="planeOrigin"/> with <paramref name="planeNormal"/>.
	/// </summary>
	public static List<(Vec3 A, Vec3 B)> CrossSection( PolyMesh mesh, Vec3 planeOrigin, Vec3 planeNormal,
		float tolerance = 1e-4f )
	{
		var segments = new List<(Vec3, Vec3)>();

		if ( mesh is null || mesh.Faces.Count == 0 )
			return segments;

		var normal = planeNormal.Normal;

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			var distances = new float[face.Count];
			var above = false;
			var below = false;

			for ( var i = 0; i < face.Count; i++ )
			{
				distances[i] = Vec3.Dot( mesh.Positions[face.Indices[i]] - planeOrigin, normal );

				if ( distances[i] > tolerance )
					above = true;
				else if ( distances[i] < -tolerance )
					below = true;
			}

			var coplanar = !above && !below;

			// Sitting flat ON the plane: the face's own edges ARE the footprint.
			if ( coplanar )
			{
				for ( var i = 0; i < face.Count; i++ )
				{
					segments.Add( (mesh.Positions[face.Indices[i]],
						mesh.Positions[face.Indices[(i + 1) % face.Count]]) );
				}

				continue;
			}

			// STRICTLY BOTH SIDES, or this face does not cross the plane - it only touches it.
			// A block standing on a slab touches with all four of its side walls, each along its
			// bottom edge, and every one of those edges is already an edge of the coplanar bottom
			// face. Without this the whole footprint gets drawn twice.
			if ( !above || !below )
				continue;

			// Passing through it: collect where the face's edges cross, which for a convex face is
			// exactly two points and therefore one chord.
			var crossings = new List<Vec3>( 2 );

			for ( var i = 0; i < face.Count; i++ )
			{
				var a = mesh.Positions[face.Indices[i]];
				var b = mesh.Positions[face.Indices[(i + 1) % face.Count]];
				var da = distances[i];
				var db = distances[(i + 1) % face.Count];

				// A corner sitting on the plane is reported once, by the edge that arrives at it -
				// counting it twice would leave a zero-length segment behind.
				if ( MathF.Abs( da ) <= tolerance )
				{
					crossings.Add( a );
					continue;
				}

				if ( MathF.Abs( db ) <= tolerance || da * db > 0f )
					continue;

				crossings.Add( a + (b - a) * (da / (da - db)) );
			}

			for ( var i = 0; i + 1 < crossings.Count; i += 2 )
			{
				if ( (crossings[i + 1] - crossings[i]).LengthSquared > tolerance * tolerance )
					segments.Add( (crossings[i], crossings[i + 1]) );
			}
		}

		return segments;
	}
}
