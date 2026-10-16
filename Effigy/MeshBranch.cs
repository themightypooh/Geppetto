using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Grow one closed body out of another as a single mesh: an arm out of a torso, a horn out of a
/// skull, a toe out of a foot.
///
/// How a modeller does it by hand, and so how it is done here: pick a patch of the parent's quads
/// where the limb meets it, delete them, open the limb's base, and bridge the two holes with one
/// band of quads. For the bridge to be all quads the hole has to have exactly as many edges as the
/// limb has sides, so an N-sided limb takes an a × b patch of parent quads with 2(a + b) = N — a
/// 16-sided arm takes a 4 × 4 patch, N/4 faces each way, N/2 along each pair of opposite sides.
/// The split between a and b is chosen to keep the patch about square in world units, so a limb
/// on a torso with tall rings does not get a slot.
///
/// THE PATCH IS WALKED ON THE QUAD GRID, not gathered by distance. A disc of nearest faces has a
/// ragged border whose edge count depends on where it happened to land; walking a rectangle from
/// face to face across opposite edges always gives a border of exactly 2(a + b). The walk refuses
/// (and says why) when it runs into a non-quad or onto faces it already took — a cap, a pole, a
/// patch wider than the body is round.
///
/// THE BRIDGE PAIRS THE LOOPS BACKWARDS, and has to. The parent's remaining faces walk the hole one
/// way round; the limb's side faces walk its open base the other way round when both are seen
/// from outside. Pairing hole vertex i with limb vertex s − i keeps every bridge quad wound the
/// same way as its neighbours; the offset s is the turn that makes the rungs shortest.
/// </summary>
public static class MeshBranch
{
	public sealed class Result
	{
		/// <summary>The joined mesh, or null when <see cref="Problem"/> says why not.</summary>
		public PolyMesh Mesh;

		public string Problem;
		public string Cause;

		/// <summary>The patch taken out of the parent: faces round, faces along.</summary>
		public int PatchA, PatchB;

		/// <summary>The hole's perimeter over the limb base's. Far from 1, the bridge flares.</summary>
		public float FootprintRatio = 1f;

		/// <summary>The limb's base sits inside the parent: the bridge folds back through it.</summary>
		public bool StartsInside;
	}

	/// <param name="parent">Closed, quad where the limb meets it.</param>
	/// <param name="child">The limb, closed.</param>
	/// <param name="childBaseFaces">The limb faces to open at its base — its near cap. Their border
	/// must be one loop of an even number of edges.</param>
	public static Result Join( PolyMesh parent, PolyMesh child, IReadOnlyCollection<int> childBaseFaces )
	{
		var result = new Result();

		if ( parent is null || child is null || childBaseFaces is null || childBaseFaces.Count == 0 )
			return Refuse( result, "Nothing to join.", "Both bodies and the limb's base faces are needed." );

		// The limb's open base.
		var childLoop = BorderLoop( child, new HashSet<int>( childBaseFaces ), out var childWhy );

		if ( childLoop is null )
			return Refuse( result, "The limb's base is not one open ring.", childWhy );

		var n = childLoop.Count;

		if ( n < 4 || n % 2 != 0 )
			return Refuse( result, $"The limb has {n} sides; joining needs an even number, 4 or more.",
				"Its base ring has to match a rectangle of the parent's quads, whose border always has an even number of edges." );

		var baseCentre = Vec3.Zero;
		foreach ( var v in childLoop )
			baseCentre += child.Positions[v];
		baseCentre /= n;

		var basePerimeter = Perimeter( child, childLoop );

		// The parent quad nearest the limb's base is the middle of the patch.
		var seed = -1;
		var seedDistance = float.MaxValue;

		for ( var fi = 0; fi < parent.Faces.Count; fi++ )
		{
			if ( parent.Faces[fi].Count != 4 )
				continue;

			var d = (parent.FaceCentroid( parent.Faces[fi] ) - baseCentre).LengthSquared;

			if ( d < seedDistance )
			{
				seedDistance = d;
				seed = fi;
			}
		}

		if ( seed < 0 )
			return Refuse( result, "The parent has no quads to open.", "Joining replaces a patch of the parent's four-sided faces." );

		var edgeFaces = parent.BuildEdgeFaces();

		// a faces along the seed's first edge, b along its second, a + b = N/2, as square as can be.
		var sf = parent.Faces[seed].Indices;
		var su = MathF.Max( (parent.Positions[sf[1]] - parent.Positions[sf[0]]).Length, 1e-6f );
		var sv = MathF.Max( (parent.Positions[sf[3]] - parent.Positions[sf[0]]).Length, 1e-6f );
		var half = n / 2;
		var bestA = 1;
		var bestScore = float.MaxValue;

		for ( var a = 1; a < half; a++ )
		{
			var score = MathF.Abs( MathF.Log( a * su / ((half - a) * sv) ) );

			// Ties go to the more even split, so a square-faced parent gets an a = b patch.
			if ( score < bestScore - 1e-4f || (MathF.Abs( score - bestScore ) <= 1e-4f && Math.Abs( 2 * a - half ) < Math.Abs( 2 * bestA - half ) ) )
			{
				bestScore = score;
				bestA = a;
			}
		}

		var patchA = bestA;
		var patchB = half - bestA;
		result.PatchA = patchA;
		result.PatchB = patchB;

		var patch = WalkPatch( parent, edgeFaces, seed, patchA, patchB, out var patchWhy );

		if ( patch is null )
			return Refuse( result, $"There is no clean {patchA} × {patchB} patch of quads where the limb meets the body.", patchWhy );

		var hole = BorderLoop( parent, patch, out var holeWhy );

		if ( hole is null || hole.Count != n )
			return Refuse( result, "The opening in the body does not match the limb.",
				hole is null ? holeWhy : $"The opening has {hole.Count} edges and the limb {n} sides." );

		result.FootprintRatio = Perimeter( parent, hole ) / MathF.Max( basePerimeter, 1e-6f );

		// Shortest rungs: hole vertex i meets limb vertex (s - i) mod n.
		var bestS = 0;
		var bestCost = float.MaxValue;

		for ( var s = 0; s < n; s++ )
		{
			var cost = 0f;

			for ( var i = 0; i < n; i++ )
				cost += (parent.Positions[hole[i]] - child.Positions[childLoop[Mod( s - i, n )]]).LengthSquared;

			if ( cost < bestCost )
			{
				bestCost = cost;
				bestS = s;
			}
		}

		// Inside or outside: the rungs should leave the parent along its outward normal.
		var outward = 0f;

		foreach ( var fi in patch )
		{
			var face = parent.Faces[fi];
			outward += Vec3.Dot( parent.FaceNormal( face ), baseCentre - parent.FaceCentroid( face ) );
		}

		result.StartsInside = outward < 0f;

		// Build: the parent minus the patch, the limb minus its base, and the band between.
		var mesh = new PolyMesh();
		mesh.Positions.AddRange( parent.Positions );

		for ( var fi = 0; fi < parent.Faces.Count; fi++ )
		{
			if ( patch.Contains( fi ) )
				continue;

			var f = parent.Faces[fi];
			mesh.AddFace( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material );
		}

		var offset = mesh.Positions.Count;
		mesh.Positions.AddRange( child.Positions );
		var baseSet = new HashSet<int>( childBaseFaces );
		var bridgeMaterial = 0;

		for ( var fi = 0; fi < child.Faces.Count; fi++ )
		{
			var f = child.Faces[fi];

			if ( baseSet.Contains( fi ) )
			{
				bridgeMaterial = f.Material;
				continue;
			}

			mesh.AddFace( f.Indices.Select( i => i + offset ).ToArray(), (Vec2[])f.UVs.Clone(), f.Material );
		}

		for ( var i = 0; i < n; i++ )
		{
			var l0 = hole[i];
			var l1 = hole[(i + 1) % n];
			var c0 = childLoop[Mod( bestS - i, n )] + offset;
			var c1 = childLoop[Mod( bestS - i - 1, n )] + offset;
			var u0 = i / (float)n;
			var u1 = (i + 1) / (float)n;

			mesh.AddFace( new[] { l1, l0, c0, c1 },
				new[] { new Vec2( u1, 0 ), new Vec2( u0, 0 ), new Vec2( u0, 1 ), new Vec2( u1, 1 ) },
				bridgeMaterial );
		}

		result.Mesh = MeshEditSession.RemoveUnusedVertices( mesh );
		return result;
	}

	static Result Refuse( Result r, string problem, string cause )
	{
		r.Problem = problem;
		r.Cause = cause;
		return r;
	}

	static int Mod( int a, int n ) => ((a % n) + n) % n;

	static float Perimeter( PolyMesh mesh, List<int> loop )
	{
		var total = 0f;

		for ( var i = 0; i < loop.Count; i++ )
			total += (mesh.Positions[loop[(i + 1) % loop.Count]] - mesh.Positions[loop[i]]).Length;

		return total;
	}

	/// <summary>
	/// The single border loop left when <paramref name="removed"/> is taken out of a closed mesh,
	/// in the direction the remaining faces walk it. Null, with the reason, when it is not one loop.
	/// </summary>
	static List<int> BorderLoop( PolyMesh mesh, HashSet<int> removed, out string why )
	{
		why = null;
		var count = new Dictionary<EdgeKey, int>();

		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			if ( removed.Contains( fi ) )
				continue;

			var f = mesh.Faces[fi].Indices;

			for ( var k = 0; k < f.Length; k++ )
			{
				var key = new EdgeKey( f[k], f[(k + 1) % f.Length] );
				count[key] = count.TryGetValue( key, out var c ) ? c + 1 : 1;
			}
		}

		var next = new Dictionary<int, int>();

		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			if ( removed.Contains( fi ) )
				continue;

			var f = mesh.Faces[fi].Indices;

			for ( var k = 0; k < f.Length; k++ )
			{
				var a = f[k];
				var b = f[(k + 1) % f.Length];

				if ( count[new EdgeKey( a, b )] != 1 )
					continue;

				if ( next.ContainsKey( a ) )
				{
					why = $"Vertex {a} sits on the border twice: the opening pinches to a point.";
					return null;
				}

				next[a] = b;
			}
		}

		if ( next.Count == 0 )
		{
			why = "Taking those faces out leaves no opening.";
			return null;
		}

		var loop = new List<int>();
		var start = next.Keys.Min();
		var at = start;

		do
		{
			loop.Add( at );

			if ( !next.TryGetValue( at, out at ) || loop.Count > next.Count )
			{
				why = "The opening's border does not close on itself.";
				return null;
			}
		}
		while ( at != start );

		if ( loop.Count != next.Count )
		{
			why = $"Taking those faces out leaves {next.Count - loop.Count + 1} or more separate openings, not one.";
			return null;
		}

		return loop;
	}

	/// <summary>
	/// An a × b rectangle of quads centred on <paramref name="seed"/>, found by stepping across
	/// opposite edges. A frame is (face, k): corner k is the rectangle's local origin, corner k+1 is
	/// one step along a, corner k+3 one step along b.
	/// </summary>
	static HashSet<int> WalkPatch( PolyMesh mesh, Dictionary<EdgeKey, List<int>> edgeFaces, int seed, int a, int b, out string why )
	{
		why = null;
		var frame = (Face: seed, K: 0);

		for ( var i = 0; i < (a - 1) / 2; i++ )
			if ( !Step( mesh, edgeFaces, ref frame, 2, out why ) ) return null;

		for ( var i = 0; i < (b - 1) / 2; i++ )
			if ( !Step( mesh, edgeFaces, ref frame, 3, out why ) ) return null;

		var patch = new HashSet<int>();
		var row = frame;

		for ( var j = 0; j < b; j++ )
		{
			var at = row;

			for ( var i = 0; i < a; i++ )
			{
				if ( !patch.Add( at.Face ) )
				{
					why = $"The {a} × {b} patch wraps round onto itself: the body is fewer than {Math.Max( a, b )} faces round there.";
					return null;
				}

				if ( i + 1 < a && !Step( mesh, edgeFaces, ref at, 0, out why ) )
					return null;
			}

			if ( j + 1 < b && !Step( mesh, edgeFaces, ref row, 1, out why ) )
				return null;
		}

		return patch;
	}

	/// <summary>Move a frame one face: 0 is +a, 1 is +b, 2 is −a, 3 is −b.</summary>
	static bool Step( PolyMesh mesh, Dictionary<EdgeKey, List<int>> edgeFaces, ref (int Face, int K) frame, int direction, out string why )
	{
		why = null;
		var f = mesh.Faces[frame.Face].Indices;
		var origin = frame.K;
		int C( int i ) => f[(origin + i) % 4];

		// The edge crossed, as this face walks it, and which corner of the neighbour becomes its origin.
		var (from, to) = direction switch
		{
			0 => (C( 1 ), C( 2 )),
			1 => (C( 2 ), C( 3 )),
			2 => (C( 3 ), C( 0 )),
			_ => (C( 0 ), C( 1 )),
		};

		if ( !edgeFaces.TryGetValue( new EdgeKey( from, to ), out var users ) || users.Count != 2 )
		{
			why = "The patch runs off the edge of the body's surface.";
			return false;
		}

		var g = users[0] == frame.Face ? users[1] : users[0];
		var gi = mesh.Faces[g].Indices;

		if ( gi.Length != 4 )
		{
			why = $"The patch runs into a {gi.Length}-sided face (a cap or a pole), which a rectangle of quads cannot include.";
			return false;
		}

		// The neighbour walks the shared edge the other way: gi[j] = to, gi[j+1] = from.
		var j = Array.IndexOf( gi, to );

		if ( j < 0 || gi[(j + 1) % 4] != from )
		{
			why = "The surface's faces are not wound consistently there.";
			return false;
		}

		var k = direction switch
		{
			0 => j + 1,
			1 => j,
			2 => j - 1,
			_ => j - 2,
		};

		frame = (g, Mod( k, 4 ));
		return true;
	}
}
