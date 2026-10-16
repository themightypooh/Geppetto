using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Delete faces of a solid and close the opening they leave, when that opening is flat.
///
/// This is the direct edit after push-pull: a boss, or the walls of a hole, come off and the
/// surface they sat on becomes continuous again. The faces are dropped, and each boundary loop
/// they leave is capped with one face wound against the faces that remain. Where that cap lies in
/// the same plane as the wall around it, <see cref="CoplanarMerge"/> welds them into one face, so
/// the wall is a wall again rather than a wall with a patch in it.
///
/// IT REFUSES RATHER THAN GUESSING. An opening that is not flat cannot be closed with one face
/// without inventing a surface, and extending the neighbours until they meet is a different
/// operation. A mesh that is already open is refused too: capping every boundary would also fill
/// holes that were already there. A delete that severs a piece, or that removes the part, is
/// refused for the same reason — the part list is where a part is deleted.
/// </summary>
public static class FaceDelete
{
	/// <summary>
	/// Remove <paramref name="faces"/> and close each flat opening they leave. The input mesh is
	/// not modified. Throws <see cref="InvalidOperationException"/> when the opening cannot be
	/// closed exactly.
	/// </summary>
	public static PolyMesh Remove( PolyMesh mesh, IReadOnlyList<int> faces )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( faces is null || faces.Count == 0 )
			throw new InvalidOperationException( "No faces were given to delete." );

		var before = MeshValidator.Validate( mesh );

		if ( !before.IsValid || !before.IsClosed )
		{
			throw new InvalidOperationException(
				"Delete face closes a hole in a solid, and this mesh is already open or invalid." );
		}

		var doomed = new HashSet<int>();

		foreach ( var face in faces )
		{
			if ( face < 0 || face >= mesh.Faces.Count )
				throw new InvalidOperationException( $"Face {face} is not on this mesh." );

			doomed.Add( face );
		}

		if ( doomed.Count >= mesh.Faces.Count )
		{
			throw new InvalidOperationException(
				"Deleting those faces removes the part. Delete the part instead." );
		}

		var result = mesh.Clone();
		var editable = EditableMesh.FromPolyMesh( result );
		editable.DeleteFaces( doomed );
		result = editable.ToPolyMesh();

		var rings = BoundaryRings( result );

		if ( rings.Count == 0 )
		{
			throw new InvalidOperationException(
				"Deleting those faces takes a whole piece away. Delete that part instead." );
		}

		foreach ( var ring in rings )
			Cap( result, ring );

		var after = MeshValidator.Validate( result );

		if ( !after.IsValid || !after.IsClosed )
		{
			throw new InvalidOperationException(
				"Deleting those faces does not leave an opening that closes." );
		}

		var beforeVolume = mesh.SignedVolume();
		var afterVolume = result.SignedVolume();

		if ( beforeVolume > 1e-4f && afterVolume <= 1e-4f
			|| beforeVolume < -1e-4f && afterVolume >= -1e-4f )
		{
			throw new InvalidOperationException(
				"Closing that opening turned the part inside out, or emptied it." );
		}

		var beforePieces = MeshSplit.PieceCount( mesh );
		var afterPieces = MeshSplit.PieceCount( result );

		if ( afterPieces > beforePieces )
		{
			throw new InvalidOperationException(
				"Deleting those faces leaves a separate piece behind. Include the lid as well." );
		}

		if ( afterPieces < beforePieces )
		{
			throw new InvalidOperationException(
				"Deleting those faces takes a whole piece away. Delete that part instead." );
		}

		var merged = result.Clone();
		CoplanarMerge.Merge( merged );
		var mergedCheck = MeshValidator.Validate( merged );
		var mergedVolume = merged.SignedVolume();

		if ( mergedCheck.IsValid && mergedCheck.IsClosed
			&& MathF.Abs( mergedVolume - afterVolume ) <= MathF.Max( 1e-3f, MathF.Abs( afterVolume ) * 1e-4f )
			&& MeshSplit.PieceCount( merged ) == afterPieces )
		{
			result = merged;
		}

		return MeshEditSession.RemoveUnusedVertices( result );
	}

	/// <summary>One face across a boundary ring, wound against the faces that still use those
	/// edges, and only when the ring is flat.</summary>
	static void Cap( PolyMesh mesh, List<int> ring )
	{
		if ( ring.Count < 3 )
		{
			throw new InvalidOperationException(
				"That opening has fewer than three corners, so it cannot be closed." );
		}

		var raw = Vec3.Zero;

		for ( var i = 0; i < ring.Count; i++ )
		{
			var a = mesh.Positions[ring[i]];
			var b = mesh.Positions[ring[(i + 1) % ring.Count]];

			raw += new Vec3(
				(a.y - b.y) * (a.z + b.z),
				(a.z - b.z) * (a.x + b.x),
				(a.x - b.x) * (a.y + b.y) );
		}

		var length = raw.Length;

		if ( length < 1e-8f )
		{
			throw new InvalidOperationException(
				"That opening has no area, so it cannot be closed." );
		}

		var normal = raw / length;
		var origin = mesh.Positions[ring[0]];
		var reach = 0f;

		for ( var i = 0; i < ring.Count; i++ )
		{
			for ( var j = i + 1; j < ring.Count; j++ )
				reach = MathF.Max( reach, (mesh.Positions[ring[i]] - mesh.Positions[ring[j]]).Length );
		}

		var tolerance = MathF.Max( 1e-3f, reach * 1e-5f );

		foreach ( var index in ring )
		{
			var distance = MathF.Abs( Vec3.Dot( mesh.Positions[index] - origin, normal ) );

			if ( distance > tolerance )
			{
				throw new InvalidOperationException(
					"That opening is not flat, so it cannot be closed with one face." );
			}
		}

		var material = 0;
		var best = float.NegativeInfinity;
		var edges = mesh.BuildEdgeFaces();

		for ( var i = 0; i < ring.Count; i++ )
		{
			var key = new EdgeKey( ring[i], ring[(i + 1) % ring.Count] );

			if ( !edges.TryGetValue( key, out var owners ) || owners.Count == 0 )
				continue;

			var owner = mesh.Faces[owners[0]];
			var dot = Vec3.Dot( mesh.FaceNormal( owner ), normal );

			if ( dot > best )
			{
				best = dot;
				material = owner.Material;
			}
		}

		mesh.AddFace( ring.ToArray(), null, material );
	}

	/// <summary>
	/// Boundary loops, each wound the opposite way from the face that still owns the edge — the
	/// same direction a cap has to face to agree with that neighbour.
	/// </summary>
	static List<List<int>> BoundaryRings( PolyMesh mesh )
	{
		var next = new Dictionary<int, int>();

		foreach ( var (key, owners) in mesh.BuildEdgeFaces() )
		{
			if ( owners.Count != 1 )
				continue;

			var face = mesh.Faces[owners[0]];
			var forward = RunsForward( face, key.A, key.B );
			var from = forward ? key.B : key.A;

			if ( next.ContainsKey( from ) )
			{
				throw new InvalidOperationException(
					"Those faces meet another opening at a corner, so there is no single hole to close." );
			}

			next[from] = forward ? key.A : key.B;
		}

		if ( next.Count == 0 )
			return new List<List<int>>();

		var incoming = new Dictionary<int, int>();

		foreach ( var to in next.Values )
		{
			incoming.TryGetValue( to, out var count );
			incoming[to] = count + 1;
		}

		if ( incoming.Count != next.Count )
		{
			throw new InvalidOperationException(
				"Those faces meet another opening at a corner, so there is no single hole to close." );
		}

		foreach ( var (vertex, count) in incoming )
		{
			if ( count != 1 || !next.ContainsKey( vertex ) )
			{
				throw new InvalidOperationException(
					"Those faces meet another opening at a corner, so there is no single hole to close." );
			}
		}

		var rings = new List<List<int>>();
		var seen = new HashSet<int>();

		foreach ( var start in next.Keys )
		{
			if ( !seen.Add( start ) )
				continue;

			var ring = new List<int>();
			var at = start;

			do
			{
				ring.Add( at );

				if ( !next.TryGetValue( at, out var nxt ) || ring.Count > next.Count )
				{
					throw new InvalidOperationException(
						"Those faces meet another opening at a corner, so there is no single hole to close." );
				}

				at = nxt;

				if ( at != start && !seen.Add( at ) )
				{
					throw new InvalidOperationException(
						"Those faces meet another opening at a corner, so there is no single hole to close." );
				}
			}
			while ( at != start );

			rings.Add( ring );
		}

		return rings;
	}

	static bool RunsForward( Face face, int a, int b )
	{
		var indices = face.Indices;

		for ( var i = 0; i < indices.Length; i++ )
		{
			if ( indices[i] == a && indices[(i + 1) % indices.Length] == b )
				return true;
		}

		return false;
	}
}
