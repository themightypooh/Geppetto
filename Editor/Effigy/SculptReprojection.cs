using System;

namespace Effigy;

/// <summary>What a reprojection managed, so the caller can say so rather than guess.</summary>
public sealed class ReprojectionReport
{
	public readonly int Vertices;
	public readonly int Hit;
	public readonly float MaxDistance;

	public ReprojectionReport( int vertices, int hit, float maxDistance )
	{
		Vertices = vertices;
		Hit = hit;
		MaxDistance = maxDistance;
	}

	/// <summary>How much of the new surface found the old one. Below about a half means the two
	/// shapes have little to do with each other and the result is not worth keeping.</summary>
	public float Coverage => Vertices == 0 ? 0f : (float)Hit / Vertices;

	public override string ToString() =>
		$"{Hit} of {Vertices} vertices found the old surface ({Coverage:P0}), searching {MaxDistance:0.###} either side";
}

/// <summary>
/// Moving a sculpt onto a cage it was not made on.
///
/// THE LAST RESORT, AND IT IS MEANT TO BE. Deltas are per vertex, so a cage whose topology changed
/// has no vertex to put them on and <see cref="MultiresSculpt.SetCage"/> refuses. That refusal is
/// right nearly always: the usual cause is an upstream edit the user did not mean, and undoing it
/// brings the sculpt back exactly. This is for the other case — the edit WAS meant, the old sculpt
/// is worth more than nothing, and the honest offer is an approximation clearly labelled as one.
///
/// The method is the plan's: build the new cage's levels empty, then for each vertex of the top
/// level fire a ray along its own normal and move it onto the old sculpted surface. What comes back
/// is the old shape resampled at the new cage's density.
///
/// WHAT IS LOST, stated up front so it is not discovered later:
///
/// - Detail finer than the new cage can carry. Resampling cannot invent vertices.
/// - The level structure. Everything lands in the top level as one displacement, so going back down
///   to L1 afterwards no longer shows the coarse shape with the detail riding it — there is no
///   coarse shape any more, just the top level and the cage. Sculpting at a lower level still works;
///   it just starts from a flat lower level.
/// - Anything the rays missed. A new cage that reaches somewhere the old surface never was leaves
///   those vertices where they are, which is the cage's own shape and the right answer.
/// </summary>
public static class SculptReprojection
{
	/// <summary>
	/// Resample <paramref name="old"/>'s sculpted surface onto <paramref name="newCage"/>.
	///
	/// <paramref name="levels"/> defaults to the old sculpt's, which keeps the cost the user already
	/// chose. <paramref name="maxDistance"/> of zero derives a search range from the cage's size.
	/// </summary>
	public static MultiresSculpt Reproject( MultiresSculpt old, PolyMesh newCage, out ReprojectionReport report,
		int levels = -1, float maxDistance = 0f )
	{
		if ( old is null )
			throw new ArgumentNullException( nameof( old ) );

		if ( newCage is null )
			throw new ArgumentNullException( nameof( newCage ) );

		if ( levels < 0 )
			levels = old.TopLevel;

		if ( levels < 0 )
			throw new ArgumentOutOfRangeException( nameof( levels ) );

		var source = old.Evaluate( old.TopLevel );
		var bvh = MeshBVH.Build( source );

		var result = new MultiresSculpt( newCage );

		for ( var i = 0; i < levels; i++ )
			result.AddLevel();

		var target = result.Rest( levels );
		var normals = target.ComputeVertexNormals();
		var reach = maxDistance > 0f ? maxDistance : DefaultReach( newCage );
		var hits = 0;

		for ( var i = 0; i < target.VertexCount; i++ )
		{
			var normal = normals[i];

			if ( normal.LengthSquared < 0.5f )
				continue;

			// Fired from outside inward, like the bake, so the nearest hit is the surface facing
			// this vertex rather than whatever is behind it. A vertex that finds nothing is left
			// exactly where the new cage put it, which is the correct answer for a part of the model
			// the old sculpt never covered.
			var hit = bvh.Raycast( source, target.Positions[i] + normal * reach, -normal );

			if ( hit is null || hit.Value.Distance > reach * 2f )
				continue;

			target.Positions[i] = hit.Value.Point;
			hits++;
		}

		result.Record( levels, target );
		result.ViewLevel = levels;

		report = new ReprojectionReport( target.VertexCount, hits, reach );
		return result;
	}

	/// <summary>A tenth of the cage's diagonal, the same figure the bake uses and for the same
	/// reason: far enough for ordinary relief, near enough that a ray rarely reaches an unrelated
	/// part of the model.</summary>
	static float DefaultReach( PolyMesh cage )
	{
		var diagonal = cage.BoundsDiagonal;
		return diagonal > 1e-6f ? diagonal * 0.1f : 1f;
	}
}
