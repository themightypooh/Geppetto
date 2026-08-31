using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>What an unwrap produced, so a caller can say so rather than guess.</summary>
public sealed class UnwrapReport
{
	public readonly int Charts;
	public readonly int Faces;
	public readonly int SkippedFaces;
	public readonly float Scale;

	public UnwrapReport( int charts, int faces, int skipped, float scale )
	{
		Charts = charts;
		Faces = faces;
		SkippedFaces = skipped;
		Scale = scale;
	}

	public override string ToString() =>
		$"{Faces} faces in {Charts} chart(s) at {Scale:0.####} units per UV unit"
		+ (SkippedFaces > 0 ? $", {SkippedFaces} degenerate face(s) skipped" : "");
}

/// <summary>
/// UVs that do not overlap, so a bake has somewhere to write.
///
/// WHY THIS HAD TO EXIST. <see cref="UVProjection"/> box- and planar-projects, and BOTH overlap by
/// construction on a closed solid: box projection deliberately maps +X and -X onto the same square,
/// because it is built for tiling a texture across a wall. That is right for a wall and useless for
/// a bake, which needs every face to own its own texels. Until this, `NormalBake.Measure` correctly
/// refused every model the tool could make, and the sculpt pipeline could not pay off on anything
/// but a hand-UV'd plane.
///
/// CHART, FLATTEN, PACK - the three steps every unwrapper has, in their simplest honest form:
///
/// 1. **Chart.** Flood-fill faces into groups whose normals stay within an angle of the group's own
///    running average. On hard-surface CAD that puts each flat side in its own chart and follows a
///    fillet or a cylinder wall around as one piece, which is exactly where the seams should be.
///
/// 2. **Flatten.** Project each chart onto the plane of its average normal. This is the step a real
///    unwrapper does properly (LSCM, ABF) and this one does not: a chart curving more than about a
///    hemisphere flattens with visible distortion. It is bounded by the charting angle rather than
///    left to chance, and hard-surface parts - what this tool builds - rarely produce such a chart.
///
/// 3. **Pack.** One scale for every chart, so texel density is uniform and the bake resolves detail
///    evenly, then shelf-pack the boxes into the unit square with a margin between them. The margin
///    is not decoration: the bake bleeds its islands outward so seams do not glow under mipmapping,
///    and without a gutter that bleed runs into the neighbouring island.
///
/// PER-CORNER UVs MAKE THE SEAMS FREE. A vertex on a chart boundary belongs to faces in two charts
/// and simply gets a different UV in each, which is what a seam IS. Nothing has to be split.
/// </summary>
public static class UVUnwrap
{
	/// <summary>
	/// Unwrap in place.
	///
	/// <paramref name="angleDegrees"/> is how far a face's normal may sit from its chart's average
	/// before it starts a new chart. Sixty-six is a little over a right angle's worth of curvature
	/// either side of the average, which keeps a cylinder wall whole and still splits a box corner.
	///
	/// <paramref name="margin"/> is the gutter between islands as a fraction of the square.
	/// </summary>
	public static UnwrapReport Unwrap( PolyMesh mesh, float angleDegrees = 66f, float margin = 0.01f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		margin = Math.Clamp( margin, 0f, 0.2f );

		var charts = BuildCharts( mesh, angleDegrees );
		var flattened = new List<Chart>( charts.Count );
		var skipped = 0;

		foreach ( var faces in charts )
		{
			var chart = Flatten( mesh, faces );

			if ( chart is null )
			{
				skipped += faces.Count;
				continue;
			}

			flattened.Add( chart );
		}

		var scale = Pack( flattened, margin );

		foreach ( var chart in flattened )
			chart.WriteTo( mesh );

		return new UnwrapReport( flattened.Count, mesh.FaceCount - skipped, skipped, scale );
	}

	// --- 1. charting ------------------------------------------------------------------------

	/// <summary>
	/// Group faces that face roughly the same way and touch each other.
	///
	/// The comparison is against the chart's RUNNING AVERAGE rather than its seed. Comparing against
	/// the seed makes the result depend on which face happened to be first and caps a chart at the
	/// tolerance no matter how gently it curves; comparing against the average lets a cylinder wall
	/// go all the way round, which is one seam instead of sixteen.
	/// </summary>
	static List<List<int>> BuildCharts( PolyMesh mesh, float angleDegrees )
	{
		var limit = MathF.Cos( Math.Clamp( angleDegrees, 1f, 179f ) * MathF.PI / 180f );
		var neighbours = FaceNeighbours( mesh );
		var chartOf = new int[mesh.FaceCount];

		for ( var i = 0; i < chartOf.Length; i++ )
			chartOf[i] = -1;

		var charts = new List<List<int>>();
		var queue = new Queue<int>();

		// Seeded in face order so the same mesh always unwraps the same way. A chart layout that
		// shuffled between runs would move every texel in the map for no reason.
		for ( var seed = 0; seed < mesh.FaceCount; seed++ )
		{
			if ( chartOf[seed] >= 0 )
				continue;

			var index = charts.Count;
			var faces = new List<int> { seed };

			charts.Add( faces );
			chartOf[seed] = index;

			var sum = mesh.FaceNormal( mesh.Faces[seed] );
			var average = Normalised( sum, new Vec3( 0, 0, 1 ) );

			queue.Clear();
			queue.Enqueue( seed );

			while ( queue.Count > 0 )
			{
				foreach ( var next in neighbours[queue.Dequeue()] )
				{
					if ( chartOf[next] >= 0 )
						continue;

					var normal = mesh.FaceNormal( mesh.Faces[next] );

					if ( normal.LengthSquared < 1e-16f || Vec3.Dot( Normalised( normal, average ), average ) < limit )
						continue;

					chartOf[next] = index;
					faces.Add( next );

					sum += Normalised( normal, average );
					average = Normalised( sum, average );

					queue.Enqueue( next );
				}
			}
		}

		return charts;
	}

	/// <summary>Faces sharing an edge. A non-manifold edge joins everything on it, which is what
	/// keeps a chart from leaking through a seam it should have stopped at.</summary>
	static List<int>[] FaceNeighbours( PolyMesh mesh )
	{
		var result = new List<int>[mesh.FaceCount];

		for ( var i = 0; i < result.Length; i++ )
			result[i] = new List<int>();

		foreach ( var (_, faces) in mesh.BuildEdgeFaces() )
		{
			for ( var a = 0; a < faces.Count; a++ )
			{
				for ( var b = a + 1; b < faces.Count; b++ )
				{
					result[faces[a]].Add( faces[b] );
					result[faces[b]].Add( faces[a] );
				}
			}
		}

		return result;
	}

	// --- 2. flattening ----------------------------------------------------------------------

	sealed class Chart
	{
		public readonly List<int> Faces = new();
		public readonly List<Vec2[]> Corners = new();

		public Vec2 Min;
		public Vec2 Size;
		public Vec2 Offset;
		public float Scale = 1f;

		/// <summary>Write the packed UVs onto the mesh. The chart holds them in its own plane's
		/// units until here, so the packer can move it about without touching the mesh.</summary>
		public void WriteTo( PolyMesh mesh )
		{
			for ( var i = 0; i < Faces.Count; i++ )
			{
				var uvs = Corners[i];
				var face = mesh.Faces[Faces[i]];
				var written = new Vec2[uvs.Length];

				for ( var c = 0; c < uvs.Length; c++ )
				{
					written[c] = new Vec2(
						Offset.x + (uvs[c].x - Min.x) * Scale,
						Offset.y + (uvs[c].y - Min.y) * Scale );
				}

				face.UVs = written;
			}
		}
	}

	static Chart Flatten( PolyMesh mesh, List<int> faces )
	{
		var normal = Vec3.Zero;

		foreach ( var index in faces )
			normal += mesh.FaceNormal( mesh.Faces[index] );

		if ( normal.LengthSquared < 1e-16f )
			return null;

		normal = normal.Normal;

		// Any tangent will do, but it has to be a FUNCTION OF THE NORMAL and nothing else, or the
		// same chart flattens differently depending on which face seeded it.
		var seed = MathF.Abs( normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var tangent = Vec3.Cross( seed, normal ).Normal;

		if ( tangent.LengthSquared < 0.5f )
			return null;

		var bitangent = Vec3.Cross( normal, tangent ).Normal;

		var chart = new Chart();
		var min = new Vec2( float.MaxValue, float.MaxValue );
		var max = new Vec2( float.MinValue, float.MinValue );

		foreach ( var index in faces )
		{
			var face = mesh.Faces[index];
			var uvs = new Vec2[face.Count];

			for ( var c = 0; c < face.Count; c++ )
			{
				var p = mesh.Positions[face.Indices[c]];
				uvs[c] = new Vec2( Vec3.Dot( p, tangent ), Vec3.Dot( p, bitangent ) );

				min = new Vec2( MathF.Min( min.x, uvs[c].x ), MathF.Min( min.y, uvs[c].y ) );
				max = new Vec2( MathF.Max( max.x, uvs[c].x ), MathF.Max( max.y, uvs[c].y ) );
			}

			chart.Faces.Add( index );
			chart.Corners.Add( uvs );
		}

		if ( chart.Faces.Count == 0 )
			return null;

		chart.Min = min;
		chart.Size = new Vec2( MathF.Max( max.x - min.x, 0f ), MathF.Max( max.y - min.y, 0f ) );

		return chart;
	}

	// --- 3. packing -------------------------------------------------------------------------

	/// <summary>
	/// Shelf-pack the charts into the unit square and return the scale used.
	///
	/// ONE SCALE FOR EVERYTHING, chosen by binary search for the largest that still fits. Packing
	/// each chart to fill its own slot would waste no space and give a big flat face the same number
	/// of texels as a tiny bevel, so the bake would resolve the bevel beautifully and the face not at
	/// all. Uniform density is the whole reason to pack rather than to fit.
	///
	/// Shelves rather than anything cleverer: charts are sorted tallest first and laid in rows. It
	/// wastes some square, and every alternative worth having needs a real bin packer.
	/// </summary>
	static float Pack( List<Chart> charts, float margin )
	{
		if ( charts.Count == 0 )
			return 1f;

		charts.Sort( ( a, b ) => b.Size.y.CompareTo( a.Size.y ) );

		var low = 0f;
		var high = 1f;

		// An upper bound that certainly fails, so the search below has something to close on.
		foreach ( var chart in charts )
		{
			var largest = MathF.Max( chart.Size.x, chart.Size.y );

			if ( largest > 1e-9f )
				high = MathF.Max( high, 2f / largest );
		}

		var best = 0f;

		for ( var i = 0; i < 40; i++ )
		{
			var mid = (low + high) * 0.5f;

			if ( TryPack( charts, mid, margin, commit: false ) )
			{
				best = mid;
				low = mid;
			}
			else
			{
				high = mid;
			}
		}

		// A pathological set that never fit at any scale still has to come out somewhere inside the
		// square rather than as garbage, so the last resort is a scale small enough to be harmless.
		if ( best <= 0f )
			best = 1e-4f;

		TryPack( charts, best, margin, commit: true );
		return best;
	}

	static bool TryPack( List<Chart> charts, float scale, float margin, bool commit )
	{
		var x = margin;
		var y = margin;
		var shelfHeight = 0f;

		foreach ( var chart in charts )
		{
			var w = chart.Size.x * scale;
			var h = chart.Size.y * scale;

			if ( w > 1f - margin * 2f || h > 1f - margin * 2f )
				return false;

			// New shelf when this one has run out of width.
			if ( x + w + margin > 1f )
			{
				x = margin;
				y += shelfHeight + margin;
				shelfHeight = 0f;
			}

			if ( y + h + margin > 1f )
				return false;

			if ( commit )
			{
				chart.Scale = scale;
				chart.Offset = new Vec2( x, y );
			}

			x += w + margin;
			shelfHeight = MathF.Max( shelfHeight, h );
		}

		return true;
	}

	static Vec3 Normalised( Vec3 v, Vec3 fallback ) => v.LengthSquared > 1e-16f ? v.Normal : fallback;
}
