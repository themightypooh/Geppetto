using System;
using System.Collections.Generic;
using System.Linq;

namespace ModelKernel;

/// <summary>
/// Hollow a solid to a wall thickness, optionally opening chosen faces.
///
/// This is the operation that makes blocking out a room painless. Hammer's answer is a "hollow"
/// button that emits six overlapping brushes which leak at the seams; the answer here is one
/// number, and the walls meet exactly.
///
/// THE MATHS, BECAUSE THE OBVIOUS VERSION IS WRONG. The tempting implementation moves every vertex
/// along its own normal by the thickness. That is incorrect at any corner, and quietly so. A box
/// corner's area-weighted normal is (1,1,1)/sqrt(3); moving it 0.1 along that leaves each wall
/// 0.1/sqrt(3) = 0.058 thick. The model looks right and measures wrong, which is the worst kind of
/// bug to ship.
///
/// Thickness is a property of PLANES, not of vertices. So for each vertex this solves for the point
/// that sits exactly `thickness` from every face plane meeting there:
///
///     for each adjacent face i:   dot( f_i, p' ) = dot( f_i, p ) - thickness
///
/// which is a small overdetermined linear system, solved in the least-squares sense via the normal
/// equations. At a box corner it returns exactly (t, t, t) — the wall is t thick on all three
/// faces, and the vertex has moved t*sqrt(3), not t. There is a test that measures plane-to-plane
/// distance directly rather than trusting the vertex positions.
///
/// WHAT THIS DOES NOT DO, stated plainly rather than discovered later:
///
///   - No self-intersection handling. Shell a shape by more than its own thinnest feature and the
///     inner surface passes through itself. Detecting that needs the offset surface computed
///     properly, which is a different and much larger algorithm.
///   - Faces with no adjacent-plane rank-3 solution (a flat sheet, a vertex where every normal is
///     coplanar) fall back to a scaled vertex-normal offset. That fallback is exact for symmetric
///     cases and approximate otherwise, and it is reported by the returned diagnostics.
/// </summary>
public static class ShellOperation
{
	/// <summary>Faces whose normals differ by less than this are treated as one plane, so a
	/// smoothly tessellated surface does not stack near-identical constraints into the solve and
	/// make it ill-conditioned.</summary>
	const double DistinctPlaneTolerance = 1e-4;

	/// <summary>
	/// Hollow `mesh`, leaving walls `thickness` thick. Faces listed in `openFaces` are removed from
	/// both surfaces and their rim is bridged, which is how you get a room you can see into.
	/// </summary>
	public static PolyMesh Shell( PolyMesh mesh, float thickness, IEnumerable<int> openFaces = null )
		=> Shell( mesh, thickness, openFaces, out _ );

	/// <summary>As Shell, and reports how many vertices needed the approximate fallback — zero on
	/// every shape this kernel generates, and worth surfacing if it ever is not.</summary>
	public static PolyMesh Shell( PolyMesh mesh, float thickness, IEnumerable<int> openFaces, out int approximatedVertices )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( thickness <= 0f )
			throw new InvalidOperationException( "Shell thickness must be positive" );

		var validation = MeshValidator.Validate( mesh );

		if ( !validation.IsValid )
			throw new InvalidOperationException( "Cannot shell an invalid mesh: " + validation );

		if ( !validation.IsClosed )
			throw new InvalidOperationException( "Cannot shell an open mesh — it has no inside to hollow" );

		var open = new HashSet<int>( openFaces ?? Enumerable.Empty<int>() );

		foreach ( var fi in open )
		{
			if ( fi < 0 || fi >= mesh.FaceCount )
				throw new ArgumentOutOfRangeException( nameof( openFaces ), $"face {fi} does not exist" );
		}

		if ( open.Count == mesh.FaceCount )
			throw new InvalidOperationException( "Cannot open every face — there would be nothing left" );

		var faceNormals = new Vec3[mesh.FaceCount];

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			faceNormals[fi] = mesh.FaceNormal( mesh.Faces[fi] );

		var vertexFaces = mesh.BuildVertexFaces();
		var vertexNormals = mesh.ComputeVertexNormals();

		// --- inner surface positions -------------------------------------------------------
		var inner = new Vec3[mesh.VertexCount];
		approximatedVertices = 0;

		for ( var vi = 0; vi < mesh.VertexCount; vi++ )
		{
			var planes = DistinctNormals( vertexFaces[vi], faceNormals );

			if ( TrySolveOffset( mesh.Positions[vi], planes, thickness, out inner[vi] ) )
				continue;

			// Rank-deficient: every plane here is parallel or the normals are coplanar. Fall back to
			// the vertex normal, corrected by the angle to the faces so the wall is still `thickness`
			// where it can be. Exact when the corner is symmetric, approximate otherwise.
			approximatedVertices++;

			var n = vertexNormals[vi];
			var cos = planes.Count > 0 ? planes.Max( f => Vec3.Dot( n, f ) ) : 1f;

			if ( cos < 1e-4f )
				cos = 1f;

			inner[vi] = mesh.Positions[vi] - n * (thickness / cos);
		}

		// --- assemble ----------------------------------------------------------------------
		// Layout: [0 .. V) the outer surface, [V .. 2V) the inner one.
		var result = new PolyMesh();

		foreach ( var p in mesh.Positions )
			result.AddVertex( p );

		foreach ( var p in inner )
			result.AddVertex( p );

		var offset = mesh.VertexCount;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			if ( open.Contains( fi ) )
				continue;

			var f = mesh.Faces[fi];

			result.AddFace( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material );

			// The inner surface is the same sheet turned inside out: same connectivity, reversed
			// winding, so its normals face the cavity instead of the world.
			var n = f.Count;
			var innerIndices = new int[n];
			var innerUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				innerIndices[i] = f.Indices[n - 1 - i] + offset;
				innerUVs[i] = f.UVs[n - 1 - i];
			}

			result.AddFace( innerIndices, innerUVs, f.Material );
		}

		// --- rim, where faces were opened --------------------------------------------------
		if ( open.Count > 0 )
			BridgeOpening( mesh, result, open, faceNormals, offset );

		return result;
	}

	/// <summary>
	/// Close the gap between the two surfaces around every opened face, so the result is a solid
	/// with a hole in it rather than two loose sheets.
	/// </summary>
	static void BridgeOpening( PolyMesh mesh, PolyMesh result, HashSet<int> open, Vec3[] faceNormals, int offset )
	{
		foreach ( var (edge, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count != 2 )
				continue;

			var keptCount = faces.Count( fi => !open.Contains( fi ) );

			// Only edges on the border of the opening need bridging: one side survived, one did not.
			if ( keptCount != 1 )
				continue;

			var removed = faces.First( open.Contains );

			var a = edge.A;
			var b = edge.B;

			var quad = new[] { a, b, b + offset, a + offset };

			// The rim faces the way the removed face did. Rather than deriving the winding from the
			// kept face's traversal order — which depends on the mesh's own conventions and is easy
			// to get backwards — build it, measure it, and flip it if it points inward. Same posture
			// as the revolve sweep check, and for the same reason: an inverted face is invisible in
			// wireframe.
			var normal = NewellNormal( result, quad );

			if ( Vec3.Dot( normal, faceNormals[removed] ) < 0f )
				Array.Reverse( quad );

			result.AddFace( quad, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) },
				mesh.Faces[removed].Material );
		}
	}

	/// <summary>
	/// The vertex offset solve: find p' with dot(f_i, p') = dot(f_i, p) - thickness for every
	/// adjacent plane, in the least-squares sense.
	///
	/// Built as the normal equations (A^T A) p' = A^T b and solved by Cramer's rule. Done in double
	/// precision — the kernel is float everywhere else, but a 3x3 determinant of nearly-parallel
	/// normals loses far too many digits in single, and this is the one place the answer is a
	/// difference of similar quantities.
	///
	/// Returns false when the system is rank-deficient, meaning the adjacent planes do not pin the
	/// point down in all three axes. The caller decides what to do about it rather than getting a
	/// silently wrong answer.
	/// </summary>
	static bool TrySolveOffset( Vec3 p, List<Vec3> planes, float thickness, out Vec3 solved )
	{
		solved = p;

		if ( planes.Count < 3 )
			return false;

		// A^T A, symmetric, and A^T b.
		var m = new double[3, 3];
		var rhs = new double[3];

		foreach ( var f in planes )
		{
			double fx = f.x, fy = f.y, fz = f.z;
			var target = fx * p.x + fy * p.y + fz * p.z - thickness;

			m[0, 0] += fx * fx; m[0, 1] += fx * fy; m[0, 2] += fx * fz;
			m[1, 0] += fy * fx; m[1, 1] += fy * fy; m[1, 2] += fy * fz;
			m[2, 0] += fz * fx; m[2, 1] += fz * fy; m[2, 2] += fz * fz;

			rhs[0] += fx * target;
			rhs[1] += fy * target;
			rhs[2] += fz * target;
		}

		var det = Determinant( m );

		// A^T A has determinant 1 only when the normals span all three axes well. Scale the
		// threshold by the number of planes, since each contributes to the magnitude.
		if ( Math.Abs( det ) < 1e-9 * planes.Count )
			return false;

		var x = Determinant( Replace( m, 0, rhs ) ) / det;
		var y = Determinant( Replace( m, 1, rhs ) ) / det;
		var z = Determinant( Replace( m, 2, rhs ) ) / det;

		if ( double.IsNaN( x ) || double.IsNaN( y ) || double.IsNaN( z ) )
			return false;

		solved = new Vec3( (float)x, (float)y, (float)z );
		return true;
	}

	static double Determinant( double[,] m ) =>
		m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
		- m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
		+ m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

	static double[,] Replace( double[,] m, int column, double[] with )
	{
		var copy = (double[,])m.Clone();

		for ( var r = 0; r < 3; r++ )
			copy[r, column] = with[r];

		return copy;
	}

	/// <summary>Adjacent face normals with near-duplicates collapsed. Two coplanar faces impose the
	/// same constraint twice, which biases a least-squares fit toward whichever plane happens to be
	/// split into more polygons.</summary>
	static List<Vec3> DistinctNormals( List<int> faces, Vec3[] faceNormals )
	{
		var distinct = new List<Vec3>( faces.Count );

		foreach ( var fi in faces )
		{
			var n = faceNormals[fi];
			var seen = false;

			foreach ( var existing in distinct )
			{
				if ( 1.0 - Vec3.Dot( existing, n ) < DistinctPlaneTolerance )
				{
					seen = true;
					break;
				}
			}

			if ( !seen )
				distinct.Add( n );
		}

		return distinct;
	}

	static Vec3 NewellNormal( PolyMesh mesh, int[] indices )
	{
		var n = Vec3.Zero;

		for ( var i = 0; i < indices.Length; i++ )
		{
			var a = mesh.Positions[indices[i]];
			var b = mesh.Positions[indices[(i + 1) % indices.Length]];

			n += new Vec3(
				(a.y - b.y) * (a.z + b.z),
				(a.z - b.z) * (a.x + b.x),
				(a.x - b.x) * (a.y + b.y) );
		}

		return n.Normal;
	}
}
