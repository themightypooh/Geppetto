using System;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Checks on Bevel — the same "looks right, is wrong" trap as Catmull-Clark, since a corner cut
/// with the winding backwards still renders as a plausible chamfer right up until the lighting (or
/// the enclosed volume) says otherwise.
/// </summary>
public static class BevelTests
{
	public static void Run()
	{
		Section( "bevel keeps a solid closed and manifold" );
		TestStaysClosed();

		Section( "bevel winds outward" );
		TestWindingStaysOutward();

		Section( "bevel shrinks enclosed volume, monotonically with width" );
		TestVolumeShrinks();

		Section( "angle threshold selects only the edges it should" );
		TestAngleThreshold();

		Section( "a single edge only cuts its own two corners" );
		TestPartialBevel();

		Section( "an unreachable angle threshold is a no-op" );
		TestUnreachableThresholdIsANoOp();

		Section( "zero width is a no-op" );
		TestZeroWidth();
	}

	static float Volume( PolyMesh m ) =>
		m.Faces.Sum( f => Vec3.Dot( m.FaceCentroid( f ), m.FaceNormal( f ) ) * m.FaceArea( f ) ) / 3f;

	static void TestStaysClosed()
	{
		var cube = Primitives.Box( 2, 2, 2 );
		var beveled = Bevel.Apply( cube, 0.2f, 15f );

		var validation = MeshValidator.Validate( beveled );
		Check( "valid mesh", validation.IsValid, validation.ToString() );
		Check( "still closed (no boundary edges)", validation.IsClosed, validation.ToString() );
		Check( "Euler characteristic still 2", MeshValidator.EulerCharacteristic( beveled ) == 2,
			$"got {MeshValidator.EulerCharacteristic( beveled )}" );

		// 6 shrunk faces + 12 edge bridges + 8 vertex caps, for a fully-beveled cube.
		Check( "face count matches the hand count", beveled.FaceCount == 6 + 12 + 8, $"{beveled.FaceCount} faces" );
		Check( "no vertex is left unused", beveled.Positions.Count == beveled.Faces.SelectMany( f => f.Indices ).Distinct().Count() );
	}

	static void TestWindingStaysOutward()
	{
		var cube = Primitives.Box( 2, 2, 2 );
		var beveled = Bevel.Apply( cube, 0.2f, 15f );

		Check( "enclosed volume is positive", Volume( beveled ) > 0f, $"volume {Volume( beveled )}" );
	}

	static void TestVolumeShrinks()
	{
		var cube = Primitives.Box( 2, 2, 2 );
		var original = Volume( cube );

		var small = Volume( Bevel.Apply( cube, 0.05f, 15f ) );
		var big = Volume( Bevel.Apply( cube, 0.3f, 15f ) );

		Check( "a small bevel loses only a little volume", small < original && small > original * 0.9f,
			$"original {original}, small-bevel {small}" );
		Check( "a bigger bevel loses more volume than a smaller one", big < small, $"small {small}, big {big}" );
		Check( "volume stays positive even for a generous bevel", big > 0f, $"big-bevel volume {big}" );
	}

	static void TestAngleThreshold()
	{
		// A cylinder's cap-to-side edges are genuinely sharp (90°); the seams between adjacent side
		// quads are flat (0°, coplanar within each ring segment's tessellation — actually adjacent
		// side quads meet at a real angle too, so use the boundary between top-cap and side wall,
		// which is unambiguously 90°, versus a threshold set above that).
		var cylinder = Primitives.Cylinder( 0.5f, 1f, 16 );

		var untouched = Bevel.Apply( cylinder, 0.05f, 179f );
		Check( "an unreachable threshold changes nothing",
			untouched.FaceCount == cylinder.FaceCount && untouched.VertexCount == cylinder.VertexCount );

		var beveled = Bevel.Apply( cylinder, 0.05f, 45f );
		Check( "a reachable threshold does cut something", beveled.FaceCount > cylinder.FaceCount );

		var validation = MeshValidator.Validate( beveled );
		Check( "still valid and closed", validation.IsValid && validation.IsClosed, validation.ToString() );
	}

	static void TestPartialBevel()
	{
		// The wedge's two "ridge" edges — slope-to-base and slope-to-back — meet at 135°; every
		// other edge on it is a plain 90° box corner. A 110° threshold selects only those two, so
		// this is a genuine mixed selection: some corners of some faces cut, their neighbours on
		// the very same vertex left alone. That is the case CutCorner's "only one side moved" path
		// exists for, and the cube tests above never exercise it because every cube edge is 90°.
		var wedge = Primitives.Wedge( 1, 1, 1 );
		var beveled = Bevel.Apply( wedge, 0.1f, 110f );

		var validation = MeshValidator.Validate( beveled );
		Check( "still valid and closed", validation.IsValid && validation.IsClosed, validation.ToString() );

		// 5 shrunk originals + 2 ridge bridge quads + 6 degenerate-or-not bridges for the other six
		// unselected edges each triangle end touches (0-1, 1-4, 4-0 and their +Y-end counterparts,
		// each disagreeing with its shrunk neighbour on at least one side) + 4 vertex caps (one per
		// ridge endpoint) — hand-counted from the geometry once the bridging pass covers every edge,
		// not just the selected ones.
		Check( "face count matches the hand count", beveled.FaceCount == 5 + 2 + 6 + 4, $"{beveled.FaceCount} faces" );

		// The end-cap triangles' vertex POSITIONS survive untouched (neither of their own edges is
		// ever selected) — but their INDEX labels do not: RemoveUnusedVertices renumbers by
		// first-use order across the whole mesh, so checking against the original [0,1,4]/[2,3,5]
		// index triples (as opposed to positions) would be checking a renumbering artefact.
		bool TriangleAt( Vec3 a, Vec3 b, Vec3 c ) =>
			beveled.Faces.Any( f => f.Count == 3
				&& new[] { a, b, c }.All( p => f.Indices.Any( i => beveled.Positions[i].AlmostEquals( p ) ) ) );

		Check( "the -Y end cap survives completely untouched",
			TriangleAt( new Vec3( -0.5f, -0.5f, -0.5f ), new Vec3( 0.5f, -0.5f, -0.5f ), new Vec3( -0.5f, -0.5f, 0.5f ) ) );
		Check( "the +Y end cap survives completely untouched",
			TriangleAt( new Vec3( 0.5f, 0.5f, -0.5f ), new Vec3( -0.5f, 0.5f, -0.5f ), new Vec3( -0.5f, 0.5f, 0.5f ) ) );

		Check( "volume shrinks but stays positive", Volume( beveled ) > 0f && Volume( beveled ) < Volume( wedge ),
			$"original {Volume( wedge )}, beveled {Volume( beveled )}" );
	}

	static void TestUnreachableThresholdIsANoOp()
	{
		var cube = Primitives.Box( 2, 2, 2 );
		var untouched = Bevel.Apply( cube, 0.2f, 179f );

		Check( "an edge nothing selects leaves the cube alone",
			untouched.FaceCount == cube.FaceCount && untouched.VertexCount == cube.VertexCount );

		var validation = MeshValidator.Validate( untouched );
		Check( "still valid and closed", validation.IsValid && validation.IsClosed, validation.ToString() );
	}

	static void TestZeroWidth()
	{
		var cube = Primitives.Box( 2, 2, 2 );
		var beveled = Bevel.Apply( cube, 0f, 15f );

		Check( "zero width returns the mesh unchanged", beveled.FaceCount == cube.FaceCount && beveled.VertexCount == cube.VertexCount );
	}
}
