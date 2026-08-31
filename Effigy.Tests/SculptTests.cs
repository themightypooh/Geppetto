using System;
using System.Collections.Generic;
using System.Diagnostics;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Sculpt stage, from the first kernel step that everything else rests on.
///
/// A sculpt is persisted per-vertex data. If two rebuilds of the same cage produce different
/// subdivision vertex layouts, every stored delta lands on the wrong point. Dictionary
/// enumeration is not a contract; this file is.
/// </summary>
public static class SculptTests
{
	public static void Run()
	{
		Section( "sculpt: subdivision correspondence is a contract" );
		TestDocumentedLayout();
		TestSameCageSameMap();
		TestParentsInRange();
		TestEdgeBlockIsSorted();
		TestFaceOrderDoesNotShuffleEdges();
		TestSubdivideAgreesWithMap();

		Section( "sculpt: frames are derived, orthonormal, and stable" );
		TestFramesAreOrthonormal();
		TestSameMeshSameFrames();

		Section( "sculpt: capture then apply is the identity" );
		TestCaptureApplyIdentity();
		TestZeroDeltaIsIdentity();

		Section( "sculpt: frame-space deltas ride a cage edit" );
		TestUniformScaleKeepsRelativeSize();
		TestTallerCageKeepsTheBumpOnTheSurface();

		Section( "sculpt: BVH ray hits match the linear scan" );
		TestBvhRayAgreesWithLinear();
		TestBvhMissesAgree();

		Section( "sculpt: BVH radius query matches brute force" );
		TestBvhRadiusMatchesBruteForce();

		Section( "sculpt: BVH refit after displacement stays correct" );
		TestBvhRefitAfterDisplacement();

		Section( "sculpt: brushes are volume-sane and undoable" );
		TestDrawRaisesVolume();
		TestInflateRaisesVolume();
		TestSmoothReducesCurvature();
		TestGrabZeroStrengthIsIdentity();
		TestGrabMoves();
		TestFlattenPullsTowardPlane();
		TestPinchPullsTowardAxis();
		TestUndoRestoresPositions();
		TestMirrorXIsSymmetric();
		TestBrushHasAStopwatch();
	}

	static void TestDocumentedLayout()
	{
		foreach ( var (name, mesh) in Program.Closed() )
		{
			var (sub, map) = CatmullClark.SubdivideWithMap( mesh );
			var v = mesh.VertexCount;
			var e = mesh.BuildEdgeFaces().Count;
			var f = mesh.FaceCount;

			Check( $"{name}: map length is V+E+F",
				map.OutputVertexCount == v + e + f && sub.VertexCount == map.OutputVertexCount,
				$"{map.OutputVertexCount} map / {sub.VertexCount} mesh, expected {v + e + f}" );

			Check( $"{name}: source counts match the cage",
				map.SourceVertexCount == v && map.SourceEdgeCount == e && map.SourceFaceCount == f,
				$"src {map.SourceVertexCount}v/{map.SourceEdgeCount}e/{map.SourceFaceCount}f" );

			var originals = 0;
			var edges = 0;
			var faces = 0;
			var layout = true;

			for ( var i = 0; i < map.Vertices.Length; i++ )
			{
				var origin = map.Vertices[i].Origin;

				if ( i < v )
					layout &= origin == SubdivisionOrigin.Original;
				else if ( i < v + e )
					layout &= origin == SubdivisionOrigin.Edge;
				else
					layout &= origin == SubdivisionOrigin.Face;

				if ( origin == SubdivisionOrigin.Original ) originals++;
				else if ( origin == SubdivisionOrigin.Edge ) edges++;
				else faces++;
			}

			Check( $"{name}: layout is originals, then edges, then faces", layout );
			Check( $"{name}: block sizes are V, E, F",
				originals == v && edges == e && faces == f,
				$"{originals}/{edges}/{faces}" );
		}
	}

	static void TestSameCageSameMap()
	{
		foreach ( var (name, mesh) in Program.Closed() )
		{
			var a = CatmullClark.SubdivideWithMap( mesh ).Map;
			var b = CatmullClark.SubdivideWithMap( mesh ).Map;

			Check( $"{name}: two subdivisions produce identical maps", MapsEqual( a, b ) );
		}

		var plane = Primitives.Plane( 2, 2, 2, 2 );
		Check( "open plane: two subdivisions produce identical maps",
			MapsEqual(
				CatmullClark.SubdivideWithMap( plane ).Map,
				CatmullClark.SubdivideWithMap( plane ).Map ) );
	}

	static void TestParentsInRange()
	{
		foreach ( var (name, mesh) in Program.Closed() )
		{
			var map = CatmullClark.SubdivideWithMap( mesh ).Map;
			var edges = mesh.BuildEdgeFaces();
			var ok = true;
			var detail = "";

			for ( var i = 0; i < map.Vertices.Length; i++ )
			{
				var vert = map.Vertices[i];

				switch ( vert.Origin )
				{
					case SubdivisionOrigin.Original:
						if ( vert.A != i || vert.A < 0 || vert.A >= map.SourceVertexCount )
						{
							ok = false;
							detail = $"original {i} parent {vert.A}";
						}
						break;

					case SubdivisionOrigin.Edge:
						if ( vert.A < 0 || vert.B < 0
							|| vert.A >= map.SourceVertexCount || vert.B >= map.SourceVertexCount
							|| vert.A >= vert.B
							|| !edges.ContainsKey( new EdgeKey( vert.A, vert.B ) ) )
						{
							ok = false;
							detail = $"edge {i} parent [{vert.A}-{vert.B}]";
						}
						break;

					case SubdivisionOrigin.Face:
						if ( vert.A < 0 || vert.A >= map.SourceFaceCount )
						{
							ok = false;
							detail = $"face {i} parent {vert.A}";
						}
						break;
				}

				if ( !ok )
					break;
			}

			Check( $"{name}: every parent index is in range and names a real element", ok, detail );
		}
	}

	static void TestEdgeBlockIsSorted()
	{
		foreach ( var (name, mesh) in Program.Closed() )
		{
			var map = CatmullClark.SubdivideWithMap( mesh ).Map;
			var start = map.SourceVertexCount;
			var end = start + map.SourceEdgeCount;
			var sorted = true;

			for ( var i = start + 1; i < end; i++ )
			{
				var prev = map.Vertices[i - 1];
				var cur = map.Vertices[i];

				if ( prev.A > cur.A || (prev.A == cur.A && prev.B >= cur.B) )
				{
					sorted = false;
					break;
				}
			}

			Check( $"{name}: edge block is sorted by (A, B)", sorted );
		}
	}

	static void TestFaceOrderDoesNotShuffleEdges()
	{
		// Face points follow face indices, which is correct: a sculpt names them that way. Edge
		// points must not: they are identified by their endpoints, and shuffling faces used to
		// shuffle Dictionary insertion order. After the sort, reversing the face list keeps the
		// edge block identical.
		var box = Primitives.Box( 2, 2, 2 );
		var shuffled = box.Clone();
		shuffled.Faces.Reverse();

		var a = CatmullClark.SubdivideWithMap( box ).Map;
		var b = CatmullClark.SubdivideWithMap( shuffled ).Map;

		Check( "reversed faces: same number of edge points",
			a.SourceEdgeCount == b.SourceEdgeCount );

		var edgesMatch = true;

		for ( var i = 0; i < a.SourceEdgeCount; i++ )
		{
			var va = a.Vertices[a.SourceVertexCount + i];
			var vb = b.Vertices[b.SourceVertexCount + i];

			if ( va.Origin != SubdivisionOrigin.Edge || vb.Origin != SubdivisionOrigin.Edge
				|| va.A != vb.A || va.B != vb.B )
			{
				edgesMatch = false;
				break;
			}
		}

		Check( "reversed faces: edge block is unchanged", edgesMatch );

		// Face points are named by index in the source face list, so reversing the list does not
		// rename them — face-point i is still Faces[i]. Topology that actually changed is a
		// different cage; that is a hash miss later, not a shuffled correspondence here.
		var faceParentsAreIndices = true;

		for ( var i = 0; i < b.SourceFaceCount; i++ )
		{
			var vb = b.Vertices[b.SourceVertexCount + b.SourceEdgeCount + i];

			if ( vb.Origin != SubdivisionOrigin.Face || vb.A != i )
			{
				faceParentsAreIndices = false;
				break;
			}
		}

		Check( "reversed faces: face-point i is still Faces[i]", faceParentsAreIndices );
	}

	static void TestSubdivideAgreesWithMap()
	{
		foreach ( var (name, mesh) in Program.Closed() )
		{
			var plain = CatmullClark.Subdivide( mesh, 1 );
			var mapped = CatmullClark.SubdivideWithMap( mesh ).Mesh;

			Check( $"{name}: Subdivide and SubdivideWithMap agree on vertex count",
				plain.VertexCount == mapped.VertexCount,
				$"{plain.VertexCount} vs {mapped.VertexCount}" );

			Check( $"{name}: Subdivide and SubdivideWithMap agree on face count",
				plain.FaceCount == mapped.FaceCount,
				$"{plain.FaceCount} vs {mapped.FaceCount}" );

			var positions = true;

			for ( var i = 0; i < plain.VertexCount; i++ )
			{
				if ( !plain.Positions[i].AlmostEquals( mapped.Positions[i] ) )
				{
					positions = false;
					break;
				}
			}

			Check( $"{name}: Subdivide and SubdivideWithMap agree on positions", positions );
		}
	}

	static void TestFramesAreOrthonormal()
	{
		foreach ( var (name, mesh) in Program.Closed() )
		{
			var dense = CatmullClark.Subdivide( mesh, 1 );
			var frames = SculptFrames.Build( dense );
			var ok = true;
			var detail = "";

			for ( var i = 0; i < frames.Count; i++ )
			{
				var f = frames.At[i];

				if ( MathF.Abs( f.Normal.Length - 1f ) > 1e-4f
					|| MathF.Abs( f.Tangent.Length - 1f ) > 1e-4f
					|| MathF.Abs( f.Bitangent.Length - 1f ) > 1e-4f )
				{
					ok = false;
					detail = $"vert {i} lengths {f.Normal.Length:0.####}/{f.Tangent.Length:0.####}/{f.Bitangent.Length:0.####}";
					break;
				}

				if ( MathF.Abs( Vec3.Dot( f.Normal, f.Tangent ) ) > 1e-4f
					|| MathF.Abs( Vec3.Dot( f.Normal, f.Bitangent ) ) > 1e-4f
					|| MathF.Abs( Vec3.Dot( f.Tangent, f.Bitangent ) ) > 1e-4f )
				{
					ok = false;
					detail = $"vert {i} not orthogonal";
					break;
				}

				var handed = Vec3.Cross( f.Normal, f.Tangent );

				if ( !handed.AlmostEquals( f.Bitangent, 1e-4f ) )
				{
					ok = false;
					detail = $"vert {i} bitangent is not N×T";
					break;
				}

				if ( f.Scale <= 0f )
				{
					ok = false;
					detail = $"vert {i} scale {f.Scale}";
					break;
				}
			}

			Check( $"{name}: every frame is a right-handed orthonormal basis", ok, detail );
		}
	}

	static void TestSameMeshSameFrames()
	{
		var dense = CatmullClark.Subdivide( Primitives.Box( 2, 2, 2 ), 1 );
		var a = SculptFrames.Build( dense );
		var b = SculptFrames.Build( dense );
		var ok = true;

		for ( var i = 0; i < a.Count; i++ )
		{
			if ( !a.At[i].Normal.AlmostEquals( b.At[i].Normal )
				|| !a.At[i].Tangent.AlmostEquals( b.At[i].Tangent )
				|| MathF.Abs( a.At[i].Scale - b.At[i].Scale ) > 1e-5f )
			{
				ok = false;
				break;
			}
		}

		Check( "building twice on the same mesh is identical", ok );
	}

	static void TestCaptureApplyIdentity()
	{
		var rest = CatmullClark.Subdivide( Primitives.Box( 2, 2, 2 ), 1 );
		var frames = SculptFrames.Build( rest );
		var displaced = rest.Clone();
		var bump = FindMostAligned( rest, frames, new Vec3( 0, 0, 1 ) );
		var height = 0.1f;

		displaced.Positions[bump] += frames.At[bump].Normal * height;

		var layer = SculptLayer.Capture( rest, displaced, frames );
		var round = rest.Clone();
		layer.Apply( round, frames );

		var ok = true;
		var worst = 0f;

		for ( var i = 0; i < rest.VertexCount; i++ )
		{
			var err = (round.Positions[i] - displaced.Positions[i]).Length;

			if ( err > worst )
				worst = err;

			if ( err > 1e-4f )
				ok = false;
		}

		Check( "capture then apply restores the displaced mesh", ok, $"worst {worst:0.#######}" );
		Check( "the bumped vertex actually moved",
			(displaced.Positions[bump] - rest.Positions[bump]).Length > height * 0.5f );
	}

	static void TestZeroDeltaIsIdentity()
	{
		var rest = CatmullClark.Subdivide( Primitives.Box( 2, 2, 2 ), 1 );
		var frames = SculptFrames.Build( rest );
		var layer = SculptLayer.Capture( rest, rest, frames );
		var round = rest.Clone();
		layer.Apply( round, frames );

		var moved = 0;

		for ( var i = 0; i < rest.VertexCount; i++ )
		{
			if ( !round.Positions[i].AlmostEquals( rest.Positions[i], 1e-5f ) )
				moved++;
		}

		Check( "capturing a mesh against itself applies as a no-op", moved == 0, $"{moved} vertices moved" );
	}

	static void TestUniformScaleKeepsRelativeSize()
	{
		var cage = Primitives.Box( 2, 2, 2 );
		var rest = CatmullClark.Subdivide( cage, 1 );
		var frames = SculptFrames.Build( rest );
		var bump = FindMostAligned( rest, frames, new Vec3( 0, 0, 1 ) );
		var height = 0.1f;

		var displaced = rest.Clone();
		displaced.Positions[bump] += frames.At[bump].Normal * height;
		var layer = SculptLayer.Capture( rest, displaced, frames );

		var authored = (displaced.Positions[bump] - rest.Positions[bump]).Length;
		var cageSize = 2f;
		var authoredRelative = authored / cageSize;

		var scaledCage = MeshTransform.Transformed( cage, Xform.Scale( new Vec3( 2, 2, 2 ) ) );
		var scaledRest = CatmullClark.Subdivide( scaledCage, 1 );
		var scaledFrames = SculptFrames.Build( scaledRest );
		var applied = scaledRest.Clone();
		layer.Apply( applied, scaledFrames );

		var world = applied.Positions[bump] - scaledRest.Positions[bump];
		var alongNormal = Vec3.Dot( world, scaledFrames.At[bump].Normal );
		var sideways = (world - scaledFrames.At[bump].Normal * alongNormal).Length;
		var relative = world.Length / 4f;

		Check( "after 2x scale the bump is still along the new normal",
			alongNormal > authored * 0.5f && sideways < authored * 0.1f,
			$"along {alongNormal:0.####}, sideways {sideways:0.####}" );

		Check( "and it is still the same size relative to the cage",
			MathF.Abs( relative - authoredRelative ) < 0.01f,
			$"authored {authoredRelative:0.####}, after scale {relative:0.####}" );
	}

	static void TestTallerCageKeepsTheBumpOnTheSurface()
	{
		// The case the whole representation exists for: a parametric edit that is not a uniform
		// scale. The bump has to ride the surface rather than stay at its old world height.
		var cage = Primitives.Box( 2, 2, 2 );
		var rest = CatmullClark.Subdivide( cage, 1 );
		var frames = SculptFrames.Build( rest );
		var bump = FindMostAligned( rest, frames, new Vec3( 0, 0, 1 ) );
		var height = 0.1f;

		var displaced = rest.Clone();
		displaced.Positions[bump] += frames.At[bump].Normal * height;
		var restZ = rest.Positions[bump].z;
		var layer = SculptLayer.Capture( rest, displaced, frames );

		var taller = MeshTransform.Transformed( cage, Xform.Scale( new Vec3( 1, 1, 1.2f ) ) );
		var tallerRest = CatmullClark.Subdivide( taller, 1 );
		var tallerFrames = SculptFrames.Build( tallerRest );
		var applied = tallerRest.Clone();
		layer.Apply( applied, tallerFrames );

		var world = applied.Positions[bump] - tallerRest.Positions[bump];
		var alongNormal = Vec3.Dot( world, tallerFrames.At[bump].Normal );
		var sideways = (world - tallerFrames.At[bump].Normal * alongNormal).Length;

		Check( "a 20% taller cage still has the bump on the surface",
			alongNormal > height * 0.5f && sideways < height * 0.15f,
			$"along {alongNormal:0.####}, sideways {sideways:0.####}" );

		Check( "and the surface itself actually moved",
			tallerRest.Positions[bump].z > restZ + 0.05f,
			$"z {restZ:0.####} → {tallerRest.Positions[bump].z:0.####}" );
	}

	static void TestBvhRayAgreesWithLinear()
	{
		var rays = new (Vec3 Origin, Vec3 Dir)[]
		{
			(new Vec3( 0, 0, 5 ), new Vec3( 0, 0, -1 )),
			(new Vec3( 5, 0, 0 ), new Vec3( -1, 0, 0 )),
			(new Vec3( 0, 5, 0 ), new Vec3( 0, -1, 0 )),
			(new Vec3( 4, 3, 5 ), new Vec3( -0.4f, -0.3f, -0.5f )),
			(new Vec3( -3, -2, 4 ), new Vec3( 0.3f, 0.2f, -0.8f )),
		};

		foreach ( var (name, mesh) in Program.Closed() )
		{
			foreach ( var dense in new[] { mesh, CatmullClark.Subdivide( mesh, 1 ) } )
			{
				var bvh = MeshBVH.Build( dense );
				var label = dense.FaceCount == mesh.FaceCount ? name : name + " L1";
				var ok = true;
				var detail = "";

				foreach ( var (origin, dir) in rays )
				{
					var linear = MeshRaycast.Raycast( dense, origin, dir );
					var tree = bvh.Raycast( dense, origin, dir );

					if ( linear is null && tree is null )
						continue;

					if ( linear is null || tree is null )
					{
						ok = false;
						detail = $"{origin} → linear {(linear is null ? "miss" : "hit")}, bvh {(tree is null ? "miss" : "hit")}";
						break;
					}

					if ( MathF.Abs( linear.Value.Distance - tree.Value.Distance ) > 1e-4f
						|| !linear.Value.Point.AlmostEquals( tree.Value.Point, 1e-4f ) )
					{
						ok = false;
						detail = $"{origin} face {linear.Value.FaceIndex}/{tree.Value.FaceIndex} t {linear.Value.Distance:0.#####}/{tree.Value.Distance:0.#####}";
						break;
					}
				}

				Check( $"{label}: BVH and linear raycast agree", ok, detail );
			}
		}
	}

	static void TestBvhMissesAgree()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var bvh = MeshBVH.Build( box );
		var origin = new Vec3( 0, 0, 5 );
		var dir = new Vec3( 1, 0, 0 );

		Check( "a ray that misses the box misses in both",
			MeshRaycast.Raycast( box, origin, dir ) is null
			&& bvh.Raycast( box, origin, dir ) is null );

		var empty = MeshBVH.Build( new PolyMesh() );
		Check( "an empty mesh produces an empty tree", empty.IsEmpty );
		Check( "and raycasts nothing", empty.Raycast( new PolyMesh(), new Vec3( 0, 0, 1 ), new Vec3( 0, 0, -1 ) ) is null );
	}

	static void TestBvhRadiusMatchesBruteForce()
	{
		foreach ( var (name, mesh) in Program.Closed() )
		{
			var dense = CatmullClark.Subdivide( mesh, 1 );
			var bvh = MeshBVH.Build( dense );
			var queries = new (Vec3 Point, float Radius)[]
			{
				(Vec3.Zero, 0.25f),
				(Vec3.Zero, 2f),
				(dense.Positions[0], 0.15f),
				(dense.FaceCentroid( dense.Faces[0] ), 0.4f),
			};
			var found = new System.Collections.Generic.List<int>();
			var ok = true;
			var detail = "";

			foreach ( var (point, radius) in queries )
			{
				bvh.VerticesInRadius( dense, point, radius, found );
				found.Sort();

				var brute = new System.Collections.Generic.List<int>();
				var r2 = radius * radius;

				for ( var i = 0; i < dense.VertexCount; i++ )
				{
					if ( (dense.Positions[i] - point).LengthSquared <= r2 )
						brute.Add( i );
				}

				if ( found.Count != brute.Count )
				{
					ok = false;
					detail = $"{point} r={radius}: {found.Count} vs brute {brute.Count}";
					break;
				}

				for ( var i = 0; i < brute.Count; i++ )
				{
					if ( found[i] != brute[i] )
					{
						ok = false;
						detail = $"{point} r={radius}: set mismatch at {i}";
						break;
					}
				}

				if ( !ok )
					break;
			}

			Check( $"{name} L1: radius query is exactly the brute-force set", ok, detail );
		}
	}

	static void TestBvhRefitAfterDisplacement()
	{
		var mesh = CatmullClark.Subdivide( Primitives.Box( 2, 2, 2 ), 2 );
		var bvh = MeshBVH.Build( mesh );
		var frames = SculptFrames.Build( mesh );
		var bump = FindMostAligned( mesh, frames, new Vec3( 0, 0, 1 ) );

		mesh.Positions[bump] += frames.At[bump].Normal * 0.35f;
		bvh.Refit( mesh );

		var origin = new Vec3( 0, 0, 8 );
		var dir = new Vec3( 0, 0, -1 );
		var linear = MeshRaycast.Raycast( mesh, origin, dir );
		var tree = bvh.Raycast( mesh, origin, dir );

		Check( "after a pull, BVH ray still matches linear",
			linear is not null && tree is not null
			&& linear.Value.FaceIndex == tree.Value.FaceIndex
			&& MathF.Abs( linear.Value.Distance - tree.Value.Distance ) < 1e-4f,
			linear is null || tree is null
				? "miss"
				: $"face {linear.Value.FaceIndex}/{tree.Value.FaceIndex}" );

		var point = mesh.Positions[bump];
		var found = new System.Collections.Generic.List<int>();
		bvh.VerticesInRadius( mesh, point, 0.2f, found );
		found.Sort();

		var brute = new System.Collections.Generic.List<int>();
		const float r2 = 0.2f * 0.2f;

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			if ( (mesh.Positions[i] - point).LengthSquared <= r2 )
				brute.Add( i );
		}

		var match = found.Count == brute.Count;

		if ( match )
		{
			for ( var i = 0; i < brute.Count; i++ )
			{
				if ( found[i] != brute[i] )
					match = false;
			}
		}

		Check( "after a pull, radius query still matches brute force", match,
			$"{found.Count} vs {brute.Count}" );
		Check( "the pulled vertex is in its own radius query", found.Contains( bump ) );
	}

	static void TestDrawRaisesVolume()
	{
		var mesh = Sphere();
		var before = mesh.SignedVolume();
		var areaBefore = SurfaceArea( mesh );
		Stroke( mesh, BrushKind.Draw, new Vec3( 0, 0, 0.5f ), strength: 0.08f, radius: 0.35f );
		Check( "draw along +Z increases volume", mesh.SignedVolume() > before * 1.01f,
			$"{before:0.####} → {mesh.SignedVolume():0.####}" );
		Check( "and the surface area stays finite and positive",
			SurfaceArea( mesh ) > areaBefore * 0.5f && float.IsFinite( SurfaceArea( mesh ) ) );
	}

	static void TestInflateRaisesVolume()
	{
		var mesh = Sphere();
		var before = mesh.SignedVolume();
		Stroke( mesh, BrushKind.Inflate, new Vec3( 0, 0, 0.5f ), strength: 0.06f, radius: 0.4f );
		Check( "inflate increases volume", mesh.SignedVolume() > before * 1.005f,
			$"{before:0.####} → {mesh.SignedVolume():0.####}" );
	}

	static void TestSmoothReducesCurvature()
	{
		var mesh = Sphere();
		var frames = SculptFrames.Build( mesh );
		var bump = FindMostAligned( mesh, frames, new Vec3( 0, 0, 1 ) );
		mesh.Positions[bump] += frames.At[bump].Normal * 0.2f;
		var before = LaplacianEnergy( mesh );
		Stroke( mesh, BrushKind.Smooth, mesh.Positions[bump], strength: 0.8f, radius: 0.5f );
		var after = LaplacianEnergy( mesh );
		Check( "smooth strictly reduces Laplacian energy", after < before - 1e-6f,
			$"{before:0.#####} → {after:0.#####}" );
	}

	static void TestGrabZeroStrengthIsIdentity()
	{
		var mesh = Sphere();
		var copy = mesh.Clone();
		var stroke = new BrushStroke { Kind = BrushKind.Grab };
		stroke.Samples.Add( new BrushSample( new Vec3( 0, 0, 0.5f ), new Vec3( 0, 0, 1 ), 0.4f, 0f, new Vec3( 1, 0, 0 ) ) );
		Brush.Apply( mesh, stroke, SculptFrames.Build( mesh ) );
		var moved = 0;

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			if ( !mesh.Positions[i].AlmostEquals( copy.Positions[i], 1e-6f ) )
				moved++;
		}

		Check( "grab at zero strength is the identity", moved == 0, $"{moved} vertices moved" );
	}

	static void TestGrabMoves()
	{
		var mesh = Sphere();
		var before = Centroid( mesh );
		var stroke = new BrushStroke { Kind = BrushKind.Grab };
		stroke.Samples.Add( new BrushSample( new Vec3( 0, 0, 0.5f ), new Vec3( 0, 0, 1 ), 0.4f, 1f, new Vec3( 0.2f, 0, 0 ) ) );
		Brush.Apply( mesh, stroke, SculptFrames.Build( mesh ) );
		Check( "grab translates the working set", Centroid( mesh ).x > before.x + 0.001f,
			$"x {before.x:0.####} → {Centroid( mesh ).x:0.####}" );
	}

	static void TestFlattenPullsTowardPlane()
	{
		var mesh = Sphere();
		var frames = SculptFrames.Build( mesh );
		var bump = FindMostAligned( mesh, frames, new Vec3( 0, 0, 1 ) );
		mesh.Positions[bump] += frames.At[bump].Normal * 0.25f;
		var heightBefore = mesh.Positions[bump].z;
		Stroke( mesh, BrushKind.Flatten, mesh.Positions[bump], strength: 1f, radius: 0.45f );
		Check( "flatten lowers a protruding vertex toward the patch",
			mesh.Positions[bump].z < heightBefore - 0.01f,
			$"z {heightBefore:0.####} → {mesh.Positions[bump].z:0.####}" );
	}

	static void TestPinchPullsTowardAxis()
	{
		var mesh = Sphere();
		var point = new Vec3( 0, 0, 0.5f );
		var before = MeanDistanceToAxis( mesh, point, new Vec3( 0, 0, 1 ), 0.4f );
		Stroke( mesh, BrushKind.Pinch, point, strength: 0.7f, radius: 0.4f );
		var after = MeanDistanceToAxis( mesh, point, new Vec3( 0, 0, 1 ), 0.4f );
		Check( "pinch pulls vertices toward the stroke axis", after < before - 1e-4f,
			$"{before:0.####} → {after:0.####}" );
	}

	static void TestUndoRestoresPositions()
	{
		var mesh = Sphere();
		var copy = mesh.Clone();
		var stroke = new BrushStroke { Kind = BrushKind.Draw };
		stroke.Samples.Add( new BrushSample( new Vec3( 0, 0, 0.5f ), new Vec3( 0, 0, 1 ), 0.3f, 0.1f ) );
		var undo = Brush.Apply( mesh, stroke, SculptFrames.Build( copy ) );
		Check( "a real stroke records a working set", undo.Count > 0, $"{undo.Count} verts" );
		undo.Restore( mesh );
		var moved = 0;

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			if ( !mesh.Positions[i].AlmostEquals( copy.Positions[i], 1e-5f ) )
				moved++;
		}

		Check( "restore puts every affected vertex back", moved == 0, $"{moved} still moved" );
	}

	static void TestMirrorXIsSymmetric()
	{
		var mesh = Sphere();
		var stroke = new BrushStroke { Kind = BrushKind.Draw, MirrorX = true };
		stroke.Samples.Add( new BrushSample( new Vec3( 0.3f, 0, 0.3f ), new Vec3( 0.5f, 0, 0.8f ).Normal, 0.25f, 0.08f ) );
		Brush.Apply( mesh, stroke, SculptFrames.Build( mesh ) );
		Check( "a mirrored stroke leaves a mesh symmetric across X", IsSymmetricX( mesh ) );
	}

	static void TestBrushHasAStopwatch()
	{
		var mesh = CatmullClark.Subdivide( Primitives.QuadSphere( 0.5f, 4 ), 2 );
		var frames = SculptFrames.Build( mesh );
		var bvh = MeshBVH.Build( mesh );
		var stroke = new BrushStroke { Kind = BrushKind.Smooth };

		for ( var i = 0; i < 8; i++ )
			stroke.Samples.Add( new BrushSample( new Vec3( 0, 0, 0.5f ), new Vec3( 0, 0, 1 ), 0.3f, 0.5f ) );

		var sw = Stopwatch.StartNew();
		Brush.Apply( mesh, stroke, frames, bvh: bvh );
		sw.Stop();
		Check( $"8 smooth samples on {mesh.VertexCount} verts finish in under two seconds",
			sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms" );
	}

	static PolyMesh Sphere() => CatmullClark.Subdivide( Primitives.QuadSphere( 0.5f, 4 ), 1 );

	static void Stroke( PolyMesh mesh, BrushKind kind, Vec3 point, float strength, float radius )
	{
		var n = point.LengthSquared > 1e-8f ? point.Normal : new Vec3( 0, 0, 1 );
		var stroke = new BrushStroke { Kind = kind };
		stroke.Samples.Add( new BrushSample( point, n, radius, strength ) );
		Brush.Apply( mesh, stroke, SculptFrames.Build( mesh ) );
	}

	static float SurfaceArea( PolyMesh mesh )
	{
		var a = 0f;

		foreach ( var f in mesh.Faces )
			a += mesh.FaceArea( f );

		return a;
	}

	static float LaplacianEnergy( PolyMesh mesh )
	{
		var edges = mesh.BuildVertexEdges();
		var e = 0f;

		for ( var vi = 0; vi < mesh.VertexCount; vi++ )
		{
			if ( edges[vi].Count == 0 )
				continue;

			var sum = Vec3.Zero;

			foreach ( var key in edges[vi] )
				sum += mesh.Positions[key.A == vi ? key.B : key.A];

			var avg = sum / edges[vi].Count;
			var d = mesh.Positions[vi] - avg;
			e += d.LengthSquared;
		}

		return e;
	}

	static Vec3 Centroid( PolyMesh mesh )
	{
		var s = Vec3.Zero;

		foreach ( var p in mesh.Positions )
			s += p;

		return s / mesh.VertexCount;
	}

	static float MeanDistanceToAxis( PolyMesh mesh, Vec3 origin, Vec3 axis, float radius )
	{
		axis = axis.Normal;
		var r2 = radius * radius;
		var sum = 0f;
		var n = 0;

		foreach ( var p in mesh.Positions )
		{
			if ( (p - origin).LengthSquared > r2 )
				continue;

			var closest = origin + axis * Vec3.Dot( p - origin, axis );
			sum += (p - closest).Length;
			n++;
		}

		return n == 0 ? 0f : sum / n;
	}

	static bool IsSymmetricX( PolyMesh mesh )
	{
		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			var target = new Vec3( -p.x, p.y, p.z );
			var found = false;

			for ( var j = 0; j < mesh.VertexCount; j++ )
			{
				if ( mesh.Positions[j].AlmostEquals( target, 2e-3f ) )
				{
					found = true;
					break;
				}
			}

			if ( !found )
				return false;
		}

		return true;
	}

	static int FindMostAligned( PolyMesh mesh, SculptFrames frames, Vec3 direction )
	{
		var best = 0;
		var bestDot = float.NegativeInfinity;
		var dir = direction.Normal;

		for ( var i = 0; i < frames.Count; i++ )
		{
			var d = Vec3.Dot( frames.At[i].Normal, dir );

			if ( d > bestDot )
			{
				bestDot = d;
				best = i;
			}
		}

		return best;
	}

	static bool MapsEqual( SubdivisionMap a, SubdivisionMap b )
	{
		if ( a.SourceVertexCount != b.SourceVertexCount
			|| a.SourceEdgeCount != b.SourceEdgeCount
			|| a.SourceFaceCount != b.SourceFaceCount
			|| a.Vertices.Length != b.Vertices.Length )
			return false;

		for ( var i = 0; i < a.Vertices.Length; i++ )
		{
			var va = a.Vertices[i];
			var vb = b.Vertices[i];

			if ( va.Origin != vb.Origin || va.A != vb.A || va.B != vb.B )
				return false;
		}

		return true;
	}
}
