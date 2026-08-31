using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Take a mesh apart into the separate solids it actually contains.
///
/// WHY THIS EXISTS. A cut is allowed to sever a part. Drill a slot all the way across a bar and
/// what comes back from the boolean is one PolyMesh holding two blocks that touch nowhere — and
/// every consumer of that mesh treats it as one body, because nothing ever asked. The symptoms are
/// all downstream and none of them look like the cut: the Parts list shows one part where the
/// screen shows two, hiding one hides both, a per-body material paints both, the collision builder
/// wraps one convex hull around the pair and fills in the gap the cut just made, and a mass or
/// volume readout is the sum of two things the user thinks of as separate.
///
/// It is not the boolean's fault and it is not fixable there: "one mesh" is a perfectly good answer
/// to "subtract this", and the question of how many SOLIDS that is belongs to whoever holds the
/// part list. So this is the answer to that question, asked after every cut.
///
/// CONNECTED MEANS SHARING A VERTEX, not sharing an edge, and that is the conservative choice on
/// purpose. Two blocks joined at a single corner are one solid by the shared-vertex rule and two by
/// the shared-edge rule, and the shared-vertex rule is the one that can only ever split things that
/// are genuinely apart. Splitting a part someone thinks of as one part is the failure that matters
/// here: it renames bodies, and every sketch and picked face hanging off the second one moves to an
/// id that did not exist before. Failing to split, by contrast, is exactly the behaviour that
/// shipped before this file existed.
///
/// THE ORDER IS A PROMISE. Pieces come back largest-volume first, ties broken on the minimum corner
/// in x, then y, then z. Every rebuild runs the same features over the same numbers, so the pieces
/// have to come out in the same order or the ids assigned to them shuffle between rebuilds — which
/// is the same silent reattachment FeatureContext.NewBodyId was written to prevent, arriving from
/// the mesh side. Volume alone is not enough: a symmetric part cut down the middle gives two pieces
/// of identical volume, and float addition over two different face orders does not reliably give
/// identical floats, so the corner is what settles it.
/// </summary>
public static class MeshSplit
{
	/// <summary>
	/// The mesh's connected pieces, in the order described above. A mesh that is already one solid
	/// comes back as a single-element list holding a CLONE — never the input — so a caller can
	/// treat one piece and several the same way without wondering who owns what.
	/// </summary>
	public static List<PolyMesh> ConnectedPieces( PolyMesh mesh )
	{
		var pieces = new List<PolyMesh>();

		if ( mesh is null || mesh.FaceCount == 0 )
			return pieces;

		var groups = FaceGroups( mesh );

		foreach ( var group in groups )
			pieces.Add( Extract( mesh, group ) );

		pieces.Sort( Compare );

		return pieces;
	}

	/// <summary>
	/// Whether this mesh is more than one solid, without paying for the extraction.
	///
	/// The common case by a wide margin is one piece, and a caller that only wants to know whether
	/// anything changed should not have to rebuild the mesh to find out.
	/// </summary>
	public static int PieceCount( PolyMesh mesh ) =>
		mesh is null || mesh.FaceCount == 0 ? 0 : FaceGroups( mesh ).Count;

	/// <summary>
	/// Union-find over faces, joined through the vertices they share.
	///
	/// Vertices rather than edges — see the class comment. Positions are compared by INDEX and not
	/// by value: two coincident vertices that no face lists in common are two vertices, and welding
	/// them here would be a topological edit this was not asked to make.
	/// </summary>
	static List<List<int>> FaceGroups( PolyMesh mesh )
	{
		var parent = new int[mesh.FaceCount];

		for ( var i = 0; i < parent.Length; i++ )
			parent[i] = i;

		var firstFaceAt = new Dictionary<int, int>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			foreach ( var vi in mesh.Faces[fi].Indices )
			{
				if ( firstFaceAt.TryGetValue( vi, out var other ) )
					Union( other, fi );
				else
					firstFaceAt[vi] = fi;
			}
		}

		// Keyed by root and built in face order, so the groups themselves are in a stable order
		// before the sort below re-orders them by shape. Two runs over the same mesh produce the
		// same lists in the same order, which is what makes the ids stable.
		var groups = new Dictionary<int, List<int>>();
		var order = new List<int>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var root = Find( fi );

			if ( !groups.TryGetValue( root, out var list ) )
			{
				groups[root] = list = new List<int>();
				order.Add( root );
			}

			list.Add( fi );
		}

		var result = new List<List<int>>( order.Count );

		foreach ( var root in order )
			result.Add( groups[root] );

		return result;

		int Find( int i )
		{
			while ( parent[i] != i )
			{
				parent[i] = parent[parent[i]];
				i = parent[i];
			}

			return i;
		}

		void Union( int a, int b )
		{
			var ra = Find( a );
			var rb = Find( b );

			if ( ra == rb )
				return;

			// Smaller root wins, so the representative of a group does not depend on which face
			// happened to be visited first.
			if ( ra < rb )
				parent[rb] = ra;
			else
				parent[ra] = rb;
		}
	}

	/// <summary>
	/// One group of faces as a mesh of its own, with its vertices renumbered to just the ones it
	/// uses.
	///
	/// Per-corner UVs, the material slot and the skin weights all come across. Skin especially:
	/// dropping it would mean a rigged part silently loses its binding the first time a cut severs
	/// it, which is precisely the failure mode PolyMesh.Skin's own comment describes.
	/// </summary>
	static PolyMesh Extract( PolyMesh mesh, List<int> faces )
	{
		var piece = new PolyMesh();
		var remap = new Dictionary<int, int>();

		var rigged = mesh.IsRigged;

		if ( rigged )
			piece.Skin = new SkinWeights();

		foreach ( var fi in faces )
		{
			var face = mesh.Faces[fi];
			var indices = new int[face.Count];

			for ( var i = 0; i < face.Count; i++ )
			{
				var vi = face.Indices[i];

				if ( !remap.TryGetValue( vi, out var mapped ) )
				{
					mapped = piece.AddVertex( mesh.Positions[vi] );
					remap[vi] = mapped;

					if ( rigged )
						piece.Skin.Vertices.Add( mesh.Skin[vi] );
				}

				indices[i] = mapped;
			}

			piece.AddFace( indices, (Vec2[])face.UVs.Clone(), face.Material );
		}

		return piece;
	}

	/// <summary>Largest first, ties broken on the minimum corner. See the class comment for why the
	/// tiebreak is not optional.</summary>
	static int Compare( PolyMesh a, PolyMesh b )
	{
		var volumeA = MathF.Abs( a.SignedVolume() );
		var volumeB = MathF.Abs( b.SignedVolume() );

		// A relative tolerance, because two halves of a symmetric part differ in the last bits of a
		// number whose size depends on the part. Comparing them exactly would let float noise decide
		// the order, which is the whole thing this is here to prevent.
		var scale = MathF.Max( MathF.Max( volumeA, volumeB ), 1e-6f );

		if ( MathF.Abs( volumeA - volumeB ) > scale * 1e-5f )
			return volumeB.CompareTo( volumeA );

		MinCorner( a, out var cornerA );
		MinCorner( b, out var cornerB );

		if ( cornerA.x != cornerB.x ) return cornerA.x.CompareTo( cornerB.x );
		if ( cornerA.y != cornerB.y ) return cornerA.y.CompareTo( cornerB.y );

		return cornerA.z.CompareTo( cornerB.z );
	}

	static void MinCorner( PolyMesh mesh, out Vec3 min )
	{
		min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );

		foreach ( var p in mesh.Positions )
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
	}
}
