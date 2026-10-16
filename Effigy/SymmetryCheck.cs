using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// How far from mirror-symmetric a body is — the build script's `symmetry` measure, on either
/// centre plane, for the editor's symmetry badge.
///
/// TWO PLANES BECAUSE EFFIGY HAS TWO CONVENTIONS. The engine's characters face +X, so their left
/// and right are ±Y, and that is the plane the script has always measured. The editor's mirror
/// tools (Mirror X, Symmetrize, the sculpt brushes' X symmetry) mirror across X = 0. A badge that
/// only knew one of them would call every model built the other way lopsided, so it measures both
/// and reports the plane the body is closer to being mirrored across.
///
/// About the world centre plane, not the body's own middle: that is the plane the mirror tools
/// work about, so a body sitting off-centre reads as off — which is what Symmetrize would do to it.
/// </summary>
public static class SymmetryCheck
{
	/// <summary>Under this share of the width the badge calls the body symmetric.</summary>
	public const float Symmetric = 0.01f;

	/// <summary>Over this share it is visibly lopsided (0.05 per the script measure's own scale).</summary>
	public const float Lopsided = 0.05f;

	/// <summary>One reading: the error on each plane, and the plane it is closest to mirrored across.</summary>
	public readonly struct Reading
	{
		public readonly float ErrorX, ErrorY;

		public Reading( float x, float y ) { ErrorX = x; ErrorY = y; }

		/// <summary>'X' for the X = 0 plane (the editor's mirror tools), 'Y' for Y = 0 (the engine's left-right).</summary>
		public char Axis => ErrorX <= ErrorY ? 'X' : 'Y';

		public float Error => MathF.Min( ErrorX, ErrorY );

		/// <summary>0 symmetric, 1 a little off, 2 lopsided — the badge's green, yellow and red.</summary>
		public int Level => Error < Symmetric ? 0 : Error < Lopsided ? 1 : 2;

		/// <summary>The badge's short form: "Symmetric X" or "Off 2.4% X".</summary>
		public string Short => Level == 0 ? $"Symmetric {Axis}" : $"Off {Error * 100f:0.#}% {Axis}";

		public string Describe() =>
			$"Mirrored across {Axis} = 0 to within {Error * 100f:0.#}% of its width"
			+ $" (across X = 0: {ErrorX * 100f:0.#}%, across Y = 0: {ErrorY * 100f:0.#}%)."
			+ (Level == 0 ? "" : Axis == 'X'
				? " Symmetrize (Edit mode) makes the -X half a mirror of the +X half."
				: " The editor's Symmetrize mirrors across X; this body is closer to symmetric across Y.");
	}

	/// <summary>Both planes. Dense meshes are sampled (every vertex still goes in the lookup grid),
	/// so the badge stays cheap to refresh; <paramref name="maxSamples"/> int.MaxValue measures all.</summary>
	public static Reading Measure( PolyMesh mesh, int maxSamples = 40000 ) =>
		new( Error( mesh, 0, maxSamples ), Error( mesh, 1, maxSamples ) );

	/// <summary>
	/// Each vertex mirrored across the <paramref name="axis"/> = 0 plane (0 = X, 1 = Y, 2 = Z) and
	/// matched to its nearest vertex, averaged, as a fraction of the mesh's extent along that axis.
	/// 0 is a perfect mirror; 0.05 is a visibly lopsided character. A vertex with nothing within a
	/// grid cell of its mirror is charged two cells (1/12 of the width), so a body far off the plane
	/// reads about 0.083 rather than its true distance — still well past <see cref="Lopsided"/>.
	/// </summary>
	public static float Error( PolyMesh mesh, int axis, int maxSamples = int.MaxValue )
	{
		if ( mesh is null || mesh.Positions.Count == 0 )
			return 0f;

		var (min, max) = ModelMeasure.Bounds( mesh );
		var width = MathF.Max( Get( max, axis ) - Get( min, axis ), 1e-6f );

		// A grid over the vertices so this is not quadratic on a dense mesh.
		var cell = width / 24f;
		var grid = new Dictionary<(int, int, int), List<int>>();

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			var key = ((int)MathF.Floor( p.x / cell ), (int)MathF.Floor( p.y / cell ), (int)MathF.Floor( p.z / cell ));
			if ( !grid.TryGetValue( key, out var list ) ) grid[key] = list = new List<int>();
			list.Add( i );
		}

		var step = Math.Max( 1, (mesh.Positions.Count + Math.Max( 1, maxSamples ) - 1) / Math.Max( 1, maxSamples ) );
		var total = 0f;
		var count = 0;

		for ( var i = 0; i < mesh.Positions.Count; i += step )
		{
			var p = mesh.Positions[i];
			var m = axis switch { 0 => new Vec3( -p.x, p.y, p.z ), 1 => new Vec3( p.x, -p.y, p.z ), _ => new Vec3( p.x, p.y, -p.z ) };
			var best = float.MaxValue;
			var kx = (int)MathF.Floor( m.x / cell ); var ky = (int)MathF.Floor( m.y / cell ); var kz = (int)MathF.Floor( m.z / cell );

			for ( var dx = -1; dx <= 1; dx++ )
				for ( var dy = -1; dy <= 1; dy++ )
					for ( var dz = -1; dz <= 1; dz++ )
					{
						if ( !grid.TryGetValue( (kx + dx, ky + dy, kz + dz), out var list ) )
							continue;

						foreach ( var j in list )
						{
							var d = (mesh.Positions[j] - m).Length;
							if ( d < best ) best = d;
						}
					}

			total += best == float.MaxValue ? cell * 2f : best;
			count++;
		}

		return total / count / width;
	}

	static float Get( Vec3 v, int axis ) => axis switch { 0 => v.x, 1 => v.y, _ => v.z };
}
