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

		Section( "bevel carries skin weights" );
		TestRigSurvives();

		Section( "bevel stays local on a collinear corner" );
		TestCollinearCornersStayLocal();
	}

	/// <summary>
	/// A bevelled corner must not fly off into space.
	///
	/// THE BUG THIS EXISTS FOR, because every other check in this file passed while it was live:
	/// a corner lands at roughly width/sin(turn) from its vertex, so a corner that is nearly
	/// straight throws it arbitrarily far. Ear clipping a thin annulus — which is what a sketch
	/// with a hole extrudes into — produces collinear corners (turn 180°, sin 1.5e-5), and those
	/// put vertices 15000 units away on a model 20 across.
	///
	/// It stayed invisible because the result is still finite, still closed, still manifold and
	/// still has the right Euler characteristic. Only a render showed it, as the whole model
	/// collapsing to a speck while the view stretched to fit one stray vertex. So the assertion
	/// here is the one nothing else was making: the geometry has to stay near where it started.
	/// </summary>
	static void TestCollinearCornersStayLocal()
	{
		// TWO FIXTURES, BECAUSE THE FIRST ONE STOPPED CARRYING THE DEFECT. The annulus is the shape
		// the bug was found on and is still worth chamfering, but a cap with ONE hole is no longer
		// ear-clipped - it comes back as two n-gons - so its collinear corners are gone. A cap with
		// TWO holes still goes through the clipper, because splitting an n-holed face needs n+1
		// cuts and only the one-hole case is in reach, so that is where the degenerate corner lives
		// now. Both get chamfered; the guard is asserted on the one that still has the corner.
		var tube = Extruded( s =>
		{
			s.AddCircle( new Vec2( 0, 0 ), 10f );
			s.AddCircle( new Vec2( 0, 0 ), 8.4f );
		} );

		var plate = Extruded( s =>
		{
			s.AddRectangle( new Vec2( -20, -10 ), new Vec2( 20, 10 ) );
			s.AddCircle( new Vec2( -10, 0 ), 9f );
			s.AddCircle( new Vec2( 10, 0 ), 9f );
		} );

		// Confirms the mesh really does contain the degenerate corners, so this test cannot quietly
		// stop exercising the fix if the tessellation changes.
		Check( "a clipped two-hole cap really does have a collinear corner",
			StraightestCorner( plate ) < 1e-3f, $"straightest |sin| = {StraightestCorner( plate ):E3}" );

		foreach ( var (name, mesh) in new[] { ("annulus", tube), ("two-hole plate", plate) } )
		{
			var before = mesh.Positions.Max( p => p.Length );

			foreach ( var width in new[] { 0.05f, 0.22f, 0.5f } )
			{
				var beveled = Bevel.Apply( mesh, width, 20f );
				var after = beveled.Positions.Max( p => p.Length );

				// Generous: a chamfer can only move a corner outward by a small multiple of its own
				// width, so anything past the original radius plus a few widths is the runaway case.
				var limit = before + width * 20f;

				Check( $"{name} width {width}: no corner escapes the model", after <= limit,
					$"furthest vertex {after:0.##}, allowed {limit:0.##} (was {before:0.##} before)" );

				var validation = MeshValidator.Validate( beveled );
				Check( $"{name} width {width}: still closed and manifold",
					validation is { IsValid: true, IsClosed: true }, validation.ToString() );
			}
		}
	}

	/// <summary>One solid from one drawn profile, 2.6 deep.</summary>
	static PolyMesh Extruded( Action<Sketch> draw )
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );

		draw( sketch.Sketch );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 2.6f;
		studio.Rebuild();

		return studio.Bodies.Single().Mesh;
	}

	/// <summary>The smallest |sin(turn)| over every corner of every face — the quantity
	/// Bevel.IntersectCoplanarLines divides by.</summary>
	static float StraightestCorner( PolyMesh mesh )
	{
		var smallest = float.MaxValue;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var f = mesh.Faces[fi];
			var n = mesh.FaceNormal( f );

			for ( var i = 0; i < f.Count; i++ )
			{
				var prev = mesh.Positions[f.Indices[(i - 1 + f.Count) % f.Count]];
				var v = mesh.Positions[f.Indices[i]];
				var next = mesh.Positions[f.Indices[(i + 1) % f.Count]];

				var d = MathF.Abs( Vec3.Dot( Vec3.Cross( (v - prev).Normal, (next - v).Normal ), n ) );

				if ( d < smallest )
					smallest = d;
			}
		}

		return smallest;
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

	/// <summary>
	/// Bevel rebuilds the vertex list, so it has to rebuild the weights beside it — the same
	/// contract Shell and the old uniform bevel were both fixed to honour. Every new corner point is
	/// a single cut of one original vertex (see the class comment on Bevel), so unlike a smoothing
	/// operation it inherits that vertex's weights outright rather than blending several.
	/// </summary>
	static void TestRigSurvives()
	{
		var skeleton = new Skeleton();
		skeleton.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -1 ), new Vec3( 0, 0, 0 ) );
		skeleton.AddBoneFromPoints( "tip", 0, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ) );

		var mesh = Primitives.Box( 2, 2, 2 );
		mesh.Skin = SkinBinder.BindSmooth( mesh, skeleton );

		var beveled = Bevel.Apply( mesh, 0.2f, 15f );

		Check( "a rigged body survives bevelling", beveled.IsRigged );
		Check( "with one weight set per new vertex", beveled.Skin.Count == beveled.VertexCount,
			$"{beveled.Skin.Count} vs {beveled.VertexCount}" );
		Check( "and every set still valid", beveled.Skin.Validate( beveled.VertexCount, skeleton.Count ).Count == 0 );

		// An untouched-face corner keeps the source vertex's own index too, so its weights should
		// match exactly - not an average, and not bone 0.
		var sourceWeights = mesh.Skin[mesh.Faces[0].Indices[0]];
		Check( "corners inherit their source vertex's weights",
			beveled.Skin[0].Length == sourceWeights.Length && beveled.Skin[0][0].Bone == sourceWeights[0].Bone );
	}

	static void Section( string title ) => Report.Section( title );
	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
