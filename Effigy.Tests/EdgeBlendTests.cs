using System;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Checks on EdgeBlend — the same "looks right, is wrong" trap as Catmull-Clark, since a corner cut
/// with the winding backwards still renders as a plausible chamfer right up until the lighting (or
/// the enclosed volume) says otherwise.
/// </summary>
public static class EdgeBlendTests
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
		TestPartialChamfer();

		Section( "an unreachable angle threshold is a no-op" );
		TestUnreachableThresholdIsANoOp();

		Section( "zero width is a no-op" );
		TestZeroWidth();

		Section( "bevel carries skin weights" );
		TestRigSurvives();

		Section( "fillet: one segment is the chamfer, exactly" );
		TestOneSegmentIsTheChamfer();

		Section( "fillet: the strip is an arc, not a chord" );
		TestArcIsRound();

		Section( "fillet: the solid survives being rounded" );
		TestFilletStaysClosed();

		Section( "fillet: radius is a radius, not a setback" );
		TestRadiusIsARadius();

		Section( "fillet carries skin weights" );
		TestFilletRigSurvives();

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
		// THE DEFECT IS BUILT BY HAND NOW, and that is the point of this arrangement.
		//
		// The corner came free once: ear clipping a holed cap left corners turning a full 180, and
		// the annulus below was full of them. Holed caps are no longer clipped - they come back as
		// n-gons, however many holes they have - so the shape that used to carry the defect stopped
		// carrying it, and a guard that quietly stops guarding is worth less than no guard at all.
		// Splitting an edge of a box gives the same 180 turn, exactly, and cannot drift with a
		// tessellation or a cap pass ever again.
		//
		// The annulus stays as well. It is the shape the bug was found on, it is still worth
		// chamfering, and a thin ring is where a chamfer has least room to work.
		var tube = Extruded( s =>
		{
			s.AddCircle( new Vec2( 0, 0 ), 10f );
			s.AddCircle( new Vec2( 0, 0 ), 8.4f );
		} );

		var split = WithSplitEdge( Primitives.Box( 8f, 5f, 3f ) );

		Check( "the hand-built fixture really does have a collinear corner",
			StraightestCorner( split ) < 1e-6f, $"straightest |sin| = {StraightestCorner( split ):E3}" );

		Check( "and it is still a closed, valid mesh to start from",
			MeshValidator.Validate( split ) is { IsValid: true, IsClosed: true } );

		foreach ( var (name, mesh) in new[] { ("annulus", tube), ("split-edge box", split) } )
		{
			var before = mesh.Positions.Max( p => p.Length );

			foreach ( var width in new[] { 0.05f, 0.22f, 0.5f } )
			{
				var chamfered = EdgeBlend.Chamfer( mesh, width, 20f );
				var after = chamfered.Positions.Max( p => p.Length );

				// Generous: a chamfer can only move a corner outward by a small multiple of its own
				// width, so anything past the original radius plus a few widths is the runaway case.
				var limit = before + width * 20f;

				Check( $"{name} width {width}: no corner escapes the model", after <= limit,
					$"furthest vertex {after:0.##}, allowed {limit:0.##} (was {before:0.##} before)" );

				var validation = MeshValidator.Validate( chamfered );
				Check( $"{name} width {width}: still closed and manifold",
					validation is { IsValid: true, IsClosed: true }, validation.ToString() );
			}
		}
	}

	/// <summary>
	/// Split one edge of a mesh by putting a vertex at its midpoint, giving both faces that share
	/// it a corner that turns exactly 180 degrees.
	///
	/// Inserted into BOTH faces, which is the whole difficulty: adding it to one leaves the other
	/// with an edge running past a vertex it does not mention, which is a T-junction rather than a
	/// collinear corner, and the mesh stops being manifold.
	/// </summary>
	static PolyMesh WithSplitEdge( PolyMesh mesh )
	{
		var face = mesh.Faces[0];
		var a = face.Indices[0];
		var b = face.Indices[1];

		var index = mesh.Positions.Count;

		mesh.Positions.Add( (mesh.Positions[a] + mesh.Positions[b]) * 0.5f );

		foreach ( var f in mesh.Faces )
		{
			for ( var i = 0; i < f.Indices.Length; i++ )
			{
				var p = f.Indices[i];
				var q = f.Indices[(i + 1) % f.Indices.Length];

				// Either way round: the two faces sharing an edge walk it in opposite directions.
				if ( (p != a || q != b) && (p != b || q != a) )
					continue;

				var indices = f.Indices.ToList();
				var uvs = f.UVs.ToList();

				indices.Insert( i + 1, index );
				uvs.Insert( i + 1, (f.UVs[i] + f.UVs[(i + 1) % f.UVs.Length]) * 0.5f );

				f.Indices = indices.ToArray();
				f.UVs = uvs.ToArray();

				break;
			}
		}

		return mesh;
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
	/// EdgeBlend.IntersectCoplanarLines divides by.</summary>
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

	static float Volume( PolyMesh m ) => m.SignedVolume();

	static void TestStaysClosed()
	{
		var cube = Primitives.Box( 2, 2, 2 );
		var chamfered = EdgeBlend.Chamfer( cube, 0.2f, 15f );

		var validation = MeshValidator.Validate( chamfered );
		Check( "valid mesh", validation.IsValid, validation.ToString() );
		Check( "still closed (no boundary edges)", validation.IsClosed, validation.ToString() );
		Check( "Euler characteristic still 2", MeshValidator.EulerCharacteristic( chamfered ) == 2,
			$"got {MeshValidator.EulerCharacteristic( chamfered )}" );

		// 6 shrunk faces + 12 edge bridges + 8 vertex caps, for a fully-chamfered cube.
		Check( "face count matches the hand count", chamfered.FaceCount == 6 + 12 + 8, $"{chamfered.FaceCount} faces" );
		Check( "no vertex is left unused", chamfered.Positions.Count == chamfered.Faces.SelectMany( f => f.Indices ).Distinct().Count() );
	}

	static void TestWindingStaysOutward()
	{
		var cube = Primitives.Box( 2, 2, 2 );
		var chamfered = EdgeBlend.Chamfer( cube, 0.2f, 15f );

		Check( "enclosed volume is positive", Volume( chamfered ) > 0f, $"volume {Volume( chamfered )}" );
	}

	static void TestVolumeShrinks()
	{
		var cube = Primitives.Box( 2, 2, 2 );
		var original = Volume( cube );

		var small = Volume( EdgeBlend.Chamfer( cube, 0.05f, 15f ) );
		var big = Volume( EdgeBlend.Chamfer( cube, 0.3f, 15f ) );

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

		var untouched = EdgeBlend.Chamfer( cylinder, 0.05f, 179f );
		Check( "an unreachable threshold changes nothing",
			untouched.FaceCount == cylinder.FaceCount && untouched.VertexCount == cylinder.VertexCount );

		var chamfered = EdgeBlend.Chamfer( cylinder, 0.05f, 45f );
		Check( "a reachable threshold does cut something", chamfered.FaceCount > cylinder.FaceCount );

		var validation = MeshValidator.Validate( chamfered );
		Check( "still valid and closed", validation.IsValid && validation.IsClosed, validation.ToString() );
	}

	static void TestPartialChamfer()
	{
		// The wedge's two "ridge" edges — slope-to-base and slope-to-back — meet at 135°; every
		// other edge on it is a plain 90° box corner. A 110° threshold selects only those two, so
		// this is a genuine mixed selection: some corners of some faces cut, their neighbours on
		// the very same vertex left alone. That is the case CutCorner's "only one side moved" path
		// exists for, and the cube tests above never exercise it because every cube edge is 90°.
		var wedge = Primitives.Wedge( 1, 1, 1 );
		var chamfered = EdgeBlend.Chamfer( wedge, 0.1f, 110f );

		var validation = MeshValidator.Validate( chamfered );
		Check( "still valid and closed", validation.IsValid && validation.IsClosed, validation.ToString() );

		// 5 shrunk originals + 2 ridge bridge quads + 6 degenerate-or-not bridges for the other six
		// unselected edges each triangle end touches (0-1, 1-4, 4-0 and their +Y-end counterparts,
		// each disagreeing with its shrunk neighbour on at least one side) + 4 vertex caps (one per
		// ridge endpoint) — hand-counted from the geometry once the bridging pass covers every edge,
		// not just the selected ones.
		Check( "face count matches the hand count", chamfered.FaceCount == 5 + 2 + 6 + 4, $"{chamfered.FaceCount} faces" );

		// The end-cap triangles' vertex POSITIONS survive untouched (neither of their own edges is
		// ever selected) — but their INDEX labels do not: RemoveUnusedVertices renumbers by
		// first-use order across the whole mesh, so checking against the original [0,1,4]/[2,3,5]
		// index triples (as opposed to positions) would be checking a renumbering artefact.
		bool TriangleAt( Vec3 a, Vec3 b, Vec3 c ) =>
			chamfered.Faces.Any( f => f.Count == 3
				&& new[] { a, b, c }.All( p => f.Indices.Any( i => chamfered.Positions[i].AlmostEquals( p ) ) ) );

		Check( "the -Y end cap survives completely untouched",
			TriangleAt( new Vec3( -0.5f, -0.5f, -0.5f ), new Vec3( 0.5f, -0.5f, -0.5f ), new Vec3( -0.5f, -0.5f, 0.5f ) ) );
		Check( "the +Y end cap survives completely untouched",
			TriangleAt( new Vec3( 0.5f, 0.5f, -0.5f ), new Vec3( -0.5f, 0.5f, -0.5f ), new Vec3( -0.5f, 0.5f, 0.5f ) ) );

		Check( "volume shrinks but stays positive", Volume( chamfered ) > 0f && Volume( chamfered ) < Volume( wedge ),
			$"original {Volume( wedge )}, chamfered {Volume( chamfered )}" );
	}

	static void TestUnreachableThresholdIsANoOp()
	{
		var cube = Primitives.Box( 2, 2, 2 );
		var untouched = EdgeBlend.Chamfer( cube, 0.2f, 179f );

		Check( "an edge nothing selects leaves the cube alone",
			untouched.FaceCount == cube.FaceCount && untouched.VertexCount == cube.VertexCount );

		var validation = MeshValidator.Validate( untouched );
		Check( "still valid and closed", validation.IsValid && validation.IsClosed, validation.ToString() );
	}

	static void TestZeroWidth()
	{
		var cube = Primitives.Box( 2, 2, 2 );
		var chamfered = EdgeBlend.Chamfer( cube, 0f, 15f );

		Check( "zero width returns the mesh unchanged", chamfered.FaceCount == cube.FaceCount && chamfered.VertexCount == cube.VertexCount );
	}

	/// <summary>
	/// EdgeBlend rebuilds the vertex list, so it has to rebuild the weights beside it — the same
	/// contract Shell and the old uniform bevel were both fixed to honour. Every new corner point is
	/// a single cut of one original vertex (see the class comment on EdgeBlend), so unlike a smoothing
	/// operation it inherits that vertex's weights outright rather than blending several.
	/// </summary>
	static void TestRigSurvives()
	{
		var skeleton = new Skeleton();
		skeleton.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -1 ), new Vec3( 0, 0, 0 ) );
		skeleton.AddBoneFromPoints( "tip", 0, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ) );

		var mesh = Primitives.Box( 2, 2, 2 );
		mesh.Skin = SkinBinder.BindSmooth( mesh, skeleton );

		var chamfered = EdgeBlend.Chamfer( mesh, 0.2f, 15f );

		Check( "a rigged body survives bevelling", chamfered.IsRigged );
		Check( "with one weight set per new vertex", chamfered.Skin.Count == chamfered.VertexCount,
			$"{chamfered.Skin.Count} vs {chamfered.VertexCount}" );
		Check( "and every set still valid", chamfered.Skin.Validate( chamfered.VertexCount, skeleton.Count ).Count == 0 );

		// An untouched-face corner keeps the source vertex's own index too, so its weights should
		// match exactly - not an average, and not bone 0.
		var sourceWeights = mesh.Skin[mesh.Faces[0].Indices[0]];
		Check( "corners inherit their source vertex's weights",
			chamfered.Skin[0].Length == sourceWeights.Length && chamfered.Skin[0][0].Bone == sourceWeights[0].Bone );
	}

	// --- fillets ---------------------------------------------------------------------------------

	/// <summary>
	/// A one-segment arc IS the chord, so a one-segment fillet has to be the chamfer — same faces,
	/// same vertices, same volume, not merely similar.
	///
	/// This is the test that keeps the two operations one implementation. The moment the fillet path
	/// drifts into computing its corners differently, this fails, and it fails on the cheapest case
	/// rather than on some rounded solid where the difference is a fraction of a percent nobody
	/// notices.
	/// </summary>
	static void TestOneSegmentIsTheChamfer()
	{
		var cube = Primitives.Box( 2, 2, 2 );

		var chamfered = EdgeBlend.Chamfer( cube, 0.2f, 15f );
		var filleted = EdgeBlend.Fillet( cube, 0.2f, 15f, 1 );

		Check( "same face count", chamfered.FaceCount == filleted.FaceCount,
			$"{chamfered.FaceCount} vs {filleted.FaceCount}" );
		Check( "same vertex count", chamfered.VertexCount == filleted.VertexCount,
			$"{chamfered.VertexCount} vs {filleted.VertexCount}" );
		Check( "same volume", Close( Volume( chamfered ), Volume( filleted ) ),
			$"{Volume( chamfered )} vs {Volume( filleted )}" );
	}

	/// <summary>
	/// THE ONE THAT ACTUALLY CHECKS IT IS ROUND. Every other property here — closed, manifold,
	/// Euler, a plausible face count — is satisfied just as well by a chamfer cut into n flat
	/// strips, which is what a slerp that quietly degraded to a lerp would produce.
	///
	/// A cube's rounded edge is a quarter cylinder, so every point on that strip sits exactly one
	/// radius from the edge's own axis. Measured against the axis rather than against neighbouring
	/// points, because evenly spaced is not the same as circular.
	/// </summary>
	static void TestArcIsRound()
	{
		const float radius = 0.3f;

		var cube = Primitives.Box( 2, 2, 2 );
		var filleted = EdgeBlend.Fillet( cube, radius, 15f, 6 );

		// The +x/+z edge of a 2x2x2 box runs along y at (1, ., 1); its fillet axis is the parallel
		// line one radius in from both faces.
		var axis = new Vec3( 1f - radius, 0f, 1f - radius );

		var worst = 0f;
		var found = 0;

		foreach ( var p in filleted.Positions )
		{
			// Only the points that belong to this edge's strip. The strip is a quad strip between
			// two rails, so every one of its vertices sits ON a rail — at |y| = 1 - setback, which
			// on a cube is 1 - radius. That plus being out past the axis on both faces picks this
			// edge's arc and nothing else: a neighbouring edge's arc points share a rail plane but
			// swing away in the axis they round about.
			if ( MathF.Abs( MathF.Abs( p.y ) - (1f - radius) ) > 1e-3f )
				continue;

			if ( p.x < axis.x - 1e-3f || p.z < axis.z - 1e-3f )
				continue;

			found++;

			var offset = new Vec3( p.x - axis.x, 0f, p.z - axis.z );
			worst = MathF.Max( worst, MathF.Abs( offset.Length - radius ) );
		}

		Check( "the strip has the points a 6-segment arc needs", found >= 7, $"{found} points" );
		Check( "and every one is exactly one radius from the axis", worst < 1e-4f,
			$"worst deviation {worst}" );

		// A chord cuts the corner; an arc bulges out to meet it. So the same setback must remove
		// less material rounded than flat — and still remove some.
		var chamfered = EdgeBlend.Chamfer( cube, radius, 15f );

		Check( "a fillet keeps more material than the chamfer it replaces",
			Volume( filleted ) > Volume( chamfered ), $"{Volume( filleted )} vs {Volume( chamfered )}" );
		Check( "and still less than the sharp solid", Volume( filleted ) < Volume( cube ),
			$"{Volume( filleted )} vs {Volume( cube )}" );
	}

	static void TestFilletStaysClosed()
	{
		const int segments = 4;

		var cube = Primitives.Box( 2, 2, 2 );
		var filleted = EdgeBlend.Fillet( cube, 0.25f, 15f, segments );

		var validation = MeshValidator.Validate( filleted );
		Check( "valid mesh", validation.IsValid, validation.ToString() );
		Check( "still closed (no boundary edges)", validation.IsClosed, validation.ToString() );
		Check( "Euler characteristic still 2", MeshValidator.EulerCharacteristic( filleted ) == 2,
			$"got {MeshValidator.EulerCharacteristic( filleted )}" );

		// 6 shrunk faces + 12 edges each cut into `segments` strips + 8 vertex caps. The caps do not
		// multiply: a corner is still one face, it just has more corners now that the arcs run into
		// it — which is the whole T-junction question, answered by counting.
		Check( "face count matches the hand count",
			filleted.FaceCount == 6 + 12 * segments + 8, $"{filleted.FaceCount} faces" );
		Check( "no vertex is left unused",
			filleted.Positions.Count == filleted.Faces.SelectMany( f => f.Indices ).Distinct().Count() );

		// Each cap sits where three rounded edges meet, so it has one corner per face plus the
		// segments-1 arc points each of the three edges brings into it.
		var caps = filleted.Faces.Count( f => f.Count == 3 * segments );
		Check( "and the eight caps carry the arcs that run into them", caps == 8, $"{caps} such faces" );
	}

	/// <summary>
	/// The radius is the arc's radius, and the setback follows from the angle — r/tan(φ/2). On a
	/// cube's 90° edges those are the same number, which is exactly why this needs a shape whose
	/// edges are not 90°: a wedge opens at 45° along its slope, where a setback equal to the radius
	/// would be wrong by a factor of 2.4.
	/// </summary>
	static void TestRadiusIsARadius()
	{
		var wedge = Primitives.Wedge( 2, 2, 2 );

		var filleted = EdgeBlend.Fillet( wedge, 0.15f, 15f, 4 );
		var validation = MeshValidator.Validate( filleted );

		Check( "a wedge fillets into a valid solid", validation.IsValid, validation.ToString() );
		Check( "and stays closed", validation.IsClosed, validation.ToString() );
		Check( "Euler characteristic still 2", MeshValidator.EulerCharacteristic( filleted ) == 2,
			$"got {MeshValidator.EulerCharacteristic( filleted )}" );

		// The shallow edge has to set back FURTHER than the radius to hold the same arc. Measuring
		// it directly: the chamfer that produces the same tangent points is the one whose distance
		// is that setback, and it is not 0.15.
		var sameSetback = EdgeBlend.Chamfer( wedge, 0.15f, 15f );

		Check( "so a fillet is not a chamfer of the same number",
			!Close( Volume( filleted ), Volume( sameSetback ), 1e-3f ),
			$"{Volume( filleted )} vs {Volume( sameSetback )}" );
	}

	static void TestFilletRigSurvives()
	{
		var skeleton = new Skeleton();
		skeleton.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -1 ), new Vec3( 0, 0, 0 ) );
		skeleton.AddBoneFromPoints( "tip", 0, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ) );

		var mesh = Primitives.Box( 2, 2, 2 );
		mesh.Skin = SkinBinder.BindSmooth( mesh, skeleton );

		var filleted = EdgeBlend.Fillet( mesh, 0.2f, 15f, 4 );

		// The arc points are new vertices invented after the corner pass, which is exactly where a
		// parallel weight list gets forgotten — the count is what catches it.
		Check( "a rigged body survives filleting", filleted.IsRigged );
		Check( "with one weight set per new vertex", filleted.Skin.Count == filleted.VertexCount,
			$"{filleted.Skin.Count} vs {filleted.VertexCount}" );
		Check( "and every set still valid",
			filleted.Skin.Validate( filleted.VertexCount, skeleton.Count ).Count == 0 );
	}

	static bool Close( float a, float b, float eps = 1e-4f ) => MathF.Abs( a - b ) <= eps;

	static void Section( string title ) => Report.Section( title );
	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
