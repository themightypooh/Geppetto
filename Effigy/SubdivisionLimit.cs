using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Where a cage's vertices end up on the Catmull-Clark LIMIT surface, and the inverse: a cage
/// whose limit surface passes through given points.
///
/// The problem this answers is the one every subdivided block-out has: subdivision shrinks. A leg
/// drawn from the floor to the hip, subdivided, ends short of the floor; a torso drawn 10 wide is
/// 9.6 wide once smoothed. The cage is a control polygon, not the surface, so an outline that the
/// cage matches is an outline the finished model misses. Fitting moves the cage OUTWARD by exactly
/// as much as subdivision will pull it back in, so the outline is honoured by what gets rendered.
///
/// THE LIMIT POINT is the closed-form position a vertex converges to under infinite subdivision.
/// After one Catmull-Clark step every face is a quad, and there the formula is the standard one
/// (Halstead, Kass and DeRose 1993): for a vertex of valence n,
///
///     limit = (n² v + 4 Σ edge neighbours + Σ diagonal neighbours) / (n (n + 5))
///
/// which for n = 4 is the familiar 1/36 [1 4 1; 4 16 4; 1 4 1] stencil. Border vertices follow
/// the cubic B-spline curve rule the subdivider uses, (a + 4v + b) / 6; a vertex where three or
/// more borders meet is pinned there too, so it is its own limit. Creases are not modelled: this
/// is for smooth block-outs.
///
/// THE FIT is the linear system L(P) = T solved by the plain fixed-point step P ← P + (T − L(P)).
/// The limit operator is an averaging one — every row is a positive weighting summing to one, with
/// the vertex itself weighted most — so its eigenvalues sit in (0, 1] and the error shrinks every
/// pass. Smooth targets converge in a handful of passes; a checkerboard-like target is the slow
/// case (about 8/9 per pass) and never comes from an outline.
/// </summary>
public static class SubdivisionLimit
{
	/// <summary>The limit-surface position of each of <paramref name="cage"/>'s vertices, indexed as
	/// the cage is. A vertex no face uses is returned where it is.</summary>
	public static Vec3[] LimitPositions( PolyMesh cage )
	{
		if ( cage is null )
			throw new ArgumentNullException( nameof( cage ) );

		// SubdivideOnce's layout is a contract: the originals come first, in order. So after one step
		// vertex i of the result is still cage vertex i, now in an all-quad neighbourhood.
		var (quads, _) = CatmullClark.SubdivideWithMap( cage );
		return QuadLimit( quads, cage.Positions.Count );
	}

	/// <summary>Limit positions of the first <paramref name="count"/> vertices of an all-quad mesh.</summary>
	static Vec3[] QuadLimit( PolyMesh mesh, int count )
	{
		var result = new Vec3[count];
		var vertexFaces = mesh.BuildVertexFaces();
		var edgeFaces = mesh.BuildEdgeFaces();

		for ( var v = 0; v < count; v++ )
		{
			var p = mesh.Positions[v];
			var faces = vertexFaces[v];

			if ( faces is null || faces.Count == 0 )
			{
				result[v] = p;
				continue;
			}

			// Border edges at this vertex: an edge only one face uses.
			var border = new List<int>( 2 );
			var edgeSum = Vec3.Zero;
			var diagonalSum = Vec3.Zero;

			foreach ( var fi in faces )
			{
				var f = mesh.Faces[fi].Indices;
				var k = Array.IndexOf( f, v );
				var next = f[(k + 1) % f.Length];
				var prev = f[(k + f.Length - 1) % f.Length];

				// Each edge neighbour is shared by the two faces either side of its edge, so half from
				// each face counts it once round an interior vertex.
				edgeSum += (mesh.Positions[next] + mesh.Positions[prev]) * 0.5f;
				diagonalSum += mesh.Positions[f[(k + 2) % f.Length]];

				foreach ( var other in new[] { next, prev } )
				{
					if ( edgeFaces.TryGetValue( new EdgeKey( v, other ), out var users ) && users.Count == 1 && !border.Contains( other ) )
						border.Add( other );
				}
			}

			if ( border.Count == 2 )
			{
				result[v] = (mesh.Positions[border[0]] + p * 4f + mesh.Positions[border[1]]) / 6f;
				continue;
			}

			if ( border.Count > 2 )
			{
				result[v] = p;
				continue;
			}

			float n = faces.Count;
			result[v] = (p * (n * n) + edgeSum * 4f + diagonalSum) / (n * (n + 5f));
		}

		return result;
	}

	/// <summary>
	/// Move <paramref name="cage"/>'s vertices so that its Catmull-Clark limit surface passes
	/// through where they are now. Returns the largest remaining miss, in model units.
	/// </summary>
	/// <param name="tolerance">Stop once every vertex's limit is within this of its target.</param>
	public static float FitToLimit( PolyMesh cage, int maxPasses = 400, float tolerance = 1e-4f )
	{
		var targets = cage.Positions.ToArray();
		return FitToLimit( cage, targets, maxPasses, tolerance );
	}

	/// <inheritdoc cref="FitToLimit(PolyMesh, int, float)"/>
	/// <param name="targets">Where each vertex's limit point should be, indexed as the cage is.</param>
	public static float FitToLimit( PolyMesh cage, IReadOnlyList<Vec3> targets, int maxPasses = 400, float tolerance = 1e-4f )
	{
		if ( cage is null )
			throw new ArgumentNullException( nameof( cage ) );

		if ( targets is null || targets.Count != cage.Positions.Count )
			throw new ArgumentException( "One target per cage vertex.", nameof( targets ) );

		var worst = float.MaxValue;
		var best = cage.Positions.ToArray();
		var bestMiss = float.MaxValue;

		for ( var pass = 0; pass < Math.Max( 1, maxPasses ); pass++ )
		{
			var limit = LimitPositions( cage );
			worst = 0f;

			for ( var i = 0; i < limit.Length; i++ )
				worst = MathF.Max( worst, (targets[i] - limit[i]).Length );

			if ( worst < bestMiss )
			{
				bestMiss = worst;
				cage.Positions.CopyTo( best );
			}
			else if ( worst > bestMiss * 1.5f )
			{
				// Diverging: a cage that is not an ordinary smooth surface (a pinched pole, a fold).
				// Keep the best pass rather than walk further off.
				break;
			}

			if ( worst <= tolerance )
				return worst;

			for ( var i = 0; i < limit.Length; i++ )
				cage.Positions[i] += targets[i] - limit[i];
		}

		for ( var i = 0; i < best.Length; i++ )
			cage.Positions[i] = best[i];

		return bestMiss;
	}
}
