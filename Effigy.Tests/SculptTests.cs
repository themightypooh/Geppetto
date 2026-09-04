using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

		Section( "sculpt: multires levels" );
		TestFreshSculptIsTheCage();
		TestAddedLevelsAreTheSubdividedCage();
		TestALevelSubdividesTheDisplacedMeshBelow();
		TestDroppingTheViewKeepsTheHigherLevels();
		TestLowLevelEditCarriesTheHighDetail();
		TestCageEditCarriesEveryLevel();
		TestTopologyChangeIsRefused();
		TestRemoveTopLevelKeepsWhatIsBelow();
		TestRecordRefusesAMeshOfTheWrongSize();
		TestStrokeLandsInTheLevelItWasMadeAt();
		TestStrokeUndoPutsTheLevelBack();
		TestAStrokeFollowsTheSurfaceYouCanSee();
		TestEvaluateIsRepeatable();
		TestTopologyIdIgnoresPositions();
		TestMultiresHasAStopwatch();

		Section( "sculpt: the feature in the tree" );
		TestSculptFeaturePassesTheCageThroughUntouched();
		TestSculptFeatureOutputsTheTopLevel();
		TestAParametricEditCarriesTheSculpt();
		TestATopologyChangeIsRefusedAndTheDeltasKept();

		Section( "sculpt: the tool between the pointer and the mesh" );
		TestHoverFindsTheSurfaceAndMissesPastIt();
		TestAMissedClickStartsNothing();
		TestAClickLeavesAMark();
		TestHoldingStillDoesNotPileUpSamples();
		TestAFastDragFillsTheGapInsteadOfDotting();
		TestOneStrokeIsOneRevisionAndOneUndo();
		TestUndoAndRedoRoundTrip();
		TestTheStrokeLandsAtTheLevelBeingWorkedAt();
		TestDraggingOffTheModelKeepsTheStroke();
		TestCancellingAStrokeLeavesTheModelAlone();
		TestTheDisplayMeshIsCachedUntilSomethingMoves();
		TestRemovingALevelIsUndoable();
		TestPuttingALevelBackOntoAChangedBaseIsRefused();

		Section( "sculpt: masking holds part of the model still" );
		TestAFreshMaskChangesNothing();
		TestAMaskedRegionResistsTheBrush();
		TestInvertSwapsWhatIsHeld();
		TestAMaskStrokeIsUndoable();
		TestHideByMaskDropsOnlyFullyMaskedFaces();
		TestEachLevelHasItsOwnMask();
		TestHidingMaskedGeometryIsAViewOnly();

		Section( "sculpt: reprojecting onto a cage it was not made on" );
		TestReprojectionCarriesTheShapeToANewCage();
		TestReprojectionReportsWhatItManaged();
		TestTheFeatureRefusesUntilAskedToReproject();

		Section( "sculpt: baking the sculpt down onto the cage" );
		TestAnUnsculptedBakeIsFlatAndNotEmpty();
		TestABumpTiltsTheMapTheWayItLeans();
		TestFlippingGreenFlipsOnlyGreen();
		TestPaddingBleedsPastTheIsland();
		TestMirroredUVsBakeTheSameWayUp();
		TestTheBakeIsRepeatable();
		TestUVsAreCheckedBeforeTheyAreTrusted();

		Section( "sculpt: deltas persist beside the document" );
		TestBlobRoundTripsTheDeltas();
		TestAnUntouchedLevelComesBackExact();
		TestBlobCostsSixBytesAVertex();
		TestBlobRefusesTheWrongCage();
		TestBlobRefusesSomethingThatIsNotOne();
		TestTheSidecarCarriesTheSculptAcrossASaveAndLoad();
		TestSavingDoesNotDeleteABlobItDidNotWrite();
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

	static void TestFreshSculptIsTheCage()
	{
		var cage = Primitives.Box( 2, 2, 2 );
		var m = new MultiresSculpt( cage );

		Check( "a new sculpt has only level 0", m.TopLevel == 0 && m.LevelCount == 1,
			$"top {m.TopLevel}, count {m.LevelCount}" );
		Check( "and displays the cage untouched", SamePositions( m.Display(), cage ) );
		Check( "with no detail on it", !m.HasDetail( 0 ) );
	}

	static void TestAddedLevelsAreTheSubdividedCage()
	{
		var cage = Primitives.Box( 2, 2, 2 );
		var m = new MultiresSculpt( cage );
		m.AddLevel();
		m.AddLevel();

		var plain = CatmullClark.Subdivide( cage, 2 );

		Check( "adding two levels puts the view on the top one", m.ViewLevel == 2 && m.TopLevel == 2,
			$"view {m.ViewLevel}, top {m.TopLevel}" );
		Check( "a level with zero deltas is exactly the subdivided cage", SamePositions( m.Display(), plain ),
			$"{m.Display().VertexCount} verts vs {plain.VertexCount}" );

		var (vertices, faces) = m.Cost( 2 );
		Check( "and the predicted cost matches what it actually built",
			vertices == plain.VertexCount && faces == plain.FaceCount,
			$"predicted {vertices}v/{faces}f, built {plain.VertexCount}v/{plain.FaceCount}f" );
	}

	static void TestALevelSubdividesTheDisplacedMeshBelow()
	{
		// The rule the whole class rests on. Subdividing the level below AT REST instead is the
		// obvious implementation, produces a perfectly plausible mesh, and quietly makes every
		// lower-level edit unable to carry detail — so it is checked directly rather than inferred
		// from the behaviour it causes.
		var m = new MultiresSculpt( Primitives.Box( 2, 2, 2 ) );
		m.AddLevel();

		var flat = m.Rest( 1 );
		Bump( m, 1, new Vec3( 0, 0, 1 ), 0.2f );
		var displaced = m.Evaluate( 1 );

		m.AddLevel();

		Check( "level 2's rest surface is the subdivision of the DISPLACED level 1",
			SamePositions( m.Rest( 2 ), CatmullClark.Subdivide( displaced, 1 ) ) );
		Check( "and not of level 1 at rest",
			!SamePositions( m.Rest( 2 ), CatmullClark.Subdivide( flat, 1 ) ) );
		Check( "the new level starts with no detail of its own", !m.HasDetail( 2 ) );
	}

	static void TestDroppingTheViewKeepsTheHigherLevels()
	{
		var m = Levels( Primitives.Box( 2, 2, 2 ), 3 );
		Bump( m, 3, new Vec3( 0, 0, 1 ), 0.05f );

		var atTop = m.Display().Clone();
		var coarse = m.Rest( 1 ).VertexCount;

		m.ViewLevel = 1;

		Check( "dropping the view shows the coarse mesh", m.Display().VertexCount == coarse,
			$"{m.Display().VertexCount} verts, expected {coarse}" );
		Check( "and does not discard the level above it", m.HasDetail( 3 ) && m.TopLevel == 3 );

		m.ViewLevel = 3;

		Check( "returning to level 3 is unchanged", SamePositions( m.Display(), atTop ) );
	}

	static void TestLowLevelEditCarriesTheHighDetail()
	{
		// THE feature. A pore sculpted at L3 has to ride a jaw pulled at L1, rather than being
		// flattened by it or left floating where the surface used to be.
		var m = Levels( Primitives.Box( 2, 2, 2 ), 3 );
		var height = 0.05f;
		var bump = Bump( m, 3, new Vec3( 0, 0, 1 ), height );

		var restBefore = m.Rest( 3 ).Positions[bump];
		var shownBefore = m.Evaluate( 3 ).Positions[bump];

		// Now the coarse edit: lift the top half of level 1.
		var lift = 0.3f;
		var coarse = m.Evaluate( 1 );

		for ( var i = 0; i < coarse.VertexCount; i++ )
		{
			if ( coarse.Positions[i].z > 0.5f )
				coarse.Positions[i] += new Vec3( 0, 0, lift );
		}

		m.Record( 1, coarse );

		var restAfter = m.Rest( 3 );
		var shownAfter = m.Evaluate( 3 );

		Check( "a level-1 edit moves the surface level 3 is written against",
			restAfter.Positions[bump].z > restBefore.z + lift * 0.5f,
			$"rest z {restBefore.z:0.####} → {restAfter.Positions[bump].z:0.####}" );

		var world = shownAfter.Positions[bump] - restAfter.Positions[bump];
		var n = m.FramesFor( 3 ).At[bump].Normal;
		var along = Vec3.Dot( world, n );
		var sideways = (world - n * along).Length;

		Check( "and the level-3 detail rides it rather than being flattened",
			along > height * 0.5f && sideways < height * 0.15f,
			$"along {along:0.####}, sideways {sideways:0.####}, authored {height:0.####}" );

		Check( "so the detail travelled with the surface instead of staying put",
			shownAfter.Positions[bump].z > shownBefore.z + lift * 0.5f,
			$"shown z {shownBefore.z:0.####} → {shownAfter.Positions[bump].z:0.####}" );

		Check( "the level-1 deltas are what changed, and level 3's are untouched",
			m.HasDetail( 1 ) && m.HasDetail( 3 ) );
	}

	static void TestCageEditCarriesEveryLevel()
	{
		// Same property one floor down: the parametric edit is upstream of every level at once.
		var cage = Primitives.Box( 2, 2, 2 );
		var m = Levels( cage, 3 );
		var height = 0.05f;
		var bump = Bump( m, 3, new Vec3( 0, 0, 1 ), height );

		var restBefore = m.Rest( 3 ).Positions[bump].z;

		m.SetCage( MeshTransform.Transformed( cage, Xform.Scale( new Vec3( 1, 1, 1.2f ) ) ) );

		var rest = m.Rest( 3 );
		var shown = m.Evaluate( 3 );
		var world = shown.Positions[bump] - rest.Positions[bump];
		var n = m.FramesFor( 3 ).At[bump].Normal;
		var along = Vec3.Dot( world, n );
		var sideways = (world - n * along).Length;

		Check( "a 20% taller cage moves the level-3 surface", rest.Positions[bump].z > restBefore + 0.05f,
			$"rest z {restBefore:0.####} → {rest.Positions[bump].z:0.####}" );
		Check( "and the sculpt stays on it", along > height * 0.5f && sideways < height * 0.15f,
			$"along {along:0.####}, sideways {sideways:0.####}" );
	}

	static void TestTopologyChangeIsRefused()
	{
		var cage = Primitives.Box( 2, 2, 2 );
		var m = Levels( cage, 2 );
		var bump = Bump( m, 2, new Vec3( 0, 0, 1 ), 0.05f );
		var before = m.Evaluate( 2 ).Positions[bump];

		var different = Primitives.QuadSphere( 1f, 4 );
		var refusedCounts = !m.CanRebase( different, out var whyCounts );

		Check( "a cage with different counts is refused", refusedCounts );
		Check( "and the refusal names both models' numbers",
			whyCounts is not null
			&& whyCounts.Contains( different.VertexCount.ToString() )
			&& whyCounts.Contains( cage.VertexCount.ToString() ),
			whyCounts );

		// Same counts, different wiring — the case a count check alone waves through.
		var rewired = cage.Clone();
		Array.Reverse( rewired.Faces[0].Indices );

		Check( "a cage with the same counts but different faces is refused too",
			!m.CanRebase( rewired, out var whyFaces ) && whyFaces is not null, "accepted" );

		var threw = false;

		try
		{
			m.SetCage( different );
		}
		catch ( InvalidOperationException )
		{
			threw = true;
		}

		Check( "SetCage throws rather than misapplying the deltas", threw );
		Check( "and the sculpt is left exactly as it was",
			m.TopLevel == 2 && m.HasDetail( 2 ) && m.Evaluate( 2 ).Positions[bump].AlmostEquals( before ) );

		Check( "a cage that only moved is accepted",
			m.CanRebase( MeshTransform.Transformed( cage, Xform.Scale( new Vec3( 1, 1, 1.2f ) ) ), out _ ) );
	}

	static void TestRemoveTopLevelKeepsWhatIsBelow()
	{
		var m = Levels( Primitives.Box( 2, 2, 2 ), 2 );
		Bump( m, 1, new Vec3( 0, 0, 1 ), 0.1f );
		Bump( m, 2, new Vec3( 0, 0, 1 ), 0.05f );

		var atOne = m.Evaluate( 1 ).Clone();
		var dropped = m.RemoveTopLevel();

		Check( "removing the top level hands its deltas back", dropped is not null && dropped.Count > 0 );
		Check( "the level below keeps its own sculpt",
			m.TopLevel == 1 && m.HasDetail( 1 ) && SamePositions( m.Evaluate( 1 ), atOne ) );
		Check( "and the view follows it down", m.ViewLevel == 1, $"view {m.ViewLevel}" );

		m.RemoveTopLevel();
		var threw = false;

		try
		{
			m.RemoveTopLevel();
		}
		catch ( InvalidOperationException )
		{
			threw = true;
		}

		Check( "the cage level cannot be removed", threw && m.TopLevel == 0 );
	}

	static void TestRecordRefusesAMeshOfTheWrongSize()
	{
		var m = Levels( Primitives.Box( 2, 2, 2 ), 1 );
		var threw = false;

		try
		{
			m.Record( 1, Primitives.Box( 2, 2, 2 ) );
		}
		catch ( ArgumentException )
		{
			threw = true;
		}

		Check( "recording a mesh with the wrong vertex count is refused", threw );
		Check( "and nothing was stored", !m.HasDetail( 1 ) );
	}

	static void TestStrokeLandsInTheLevelItWasMadeAt()
	{
		var m = Levels( Primitives.QuadSphere( 0.5f, 4 ), 2 );
		var before = m.Evaluate( 2 ).Clone();

		var stroke = new BrushStroke { Kind = BrushKind.Draw };
		stroke.Samples.Add( new BrushSample( new Vec3( 0, 0, 0.5f ), new Vec3( 0, 0, 1 ), 0.3f, 0.05f ) );
		var undo = m.Stroke( 2, stroke );

		Check( "a stroke moved something", undo.Count > 0, $"{undo.Count} vertices" );
		Check( "and it landed in the level it was made at", m.HasDetail( 2 ) && !SamePositions( m.Evaluate( 2 ), before ) );
		Check( "leaving the levels below alone", !m.HasDetail( 0 ) && !m.HasDetail( 1 ) );
	}

	static void TestStrokeUndoPutsTheLevelBack()
	{
		var m = Levels( Primitives.QuadSphere( 0.5f, 4 ), 2 );
		var before = m.Evaluate( 2 ).Clone();

		var stroke = new BrushStroke { Kind = BrushKind.Draw };
		stroke.Samples.Add( new BrushSample( new Vec3( 0, 0, 0.5f ), new Vec3( 0, 0, 1 ), 0.3f, 0.05f ) );
		var undo = m.Stroke( 2, stroke );
		m.Undo( 2, undo );

		Check( "undoing a stroke restores the level", SamePositions( m.Evaluate( 2 ), before ) );
		Check( "and leaves no detail behind", !m.HasDetail( 2 ) );
	}

	static void TestAStrokeFollowsTheSurfaceYouCanSee()
	{
		// Inflate is the one brush that reads the frames, so it is the one that can tell whether a
		// stroke was handed the visible surface or the rest surface underneath it. On a fresh level
		// the two are the same mesh and the question does not arise; this makes them differ first.
		var m = Levels( Primitives.QuadSphere( 0.5f, 4 ), 2 );
		var centre = new Vec3( 0, 0, 0.5f );

		var grab = new BrushStroke { Kind = BrushKind.Grab };
		grab.Samples.Add( new BrushSample( centre, new Vec3( 0, 0, 1 ), 0.35f, 1f, new Vec3( 0.2f, 0, 0 ) ) );
		m.Stroke( 2, grab );

		var restFrames = m.FramesFor( 2 );
		var shown = m.Evaluate( 2 );
		var shownFrames = SculptFrames.Build( shown );

		// Where the two disagree most is the only place the choice is observable.
		var vi = -1;
		var lowest = float.MaxValue;

		for ( var i = 0; i < shown.VertexCount; i++ )
		{
			if ( (shown.Positions[i] - centre).Length > 0.3f )
				continue;

			var d = Vec3.Dot( restFrames.At[i].Normal, shownFrames.At[i].Normal );

			if ( d < lowest )
			{
				lowest = d;
				vi = i;
			}
		}

		Check( "the grab left the visible normals pointing somewhere else than the rest ones",
			vi >= 0 && lowest < 0.99f, $"closest agreement {lowest:0.####}" );

		var before = shown.Positions[vi];
		var inflate = new BrushStroke { Kind = BrushKind.Inflate };
		inflate.Samples.Add( new BrushSample( before, shownFrames.At[vi].Normal, 0.1f, 0.05f ) );
		m.Stroke( 2, inflate );

		var moved = (m.Evaluate( 2 ).Positions[vi] - before).Normal;
		var alongShown = Vec3.Dot( moved, shownFrames.At[vi].Normal );
		var alongRest = Vec3.Dot( moved, restFrames.At[vi].Normal );

		Check( "and the stroke followed the visible surface rather than the rest surface",
			alongShown > alongRest, $"visible {alongShown:0.####}, rest {alongRest:0.####}" );
	}

	static void TestEvaluateIsRepeatable()
	{
		// The cached rest surfaces are an optimisation and must not be observable. A cache that
		// misses an invalidation shows up here and nowhere else until a user loses work.
		var m = Levels( Primitives.Box( 2, 2, 2 ), 3 );
		Bump( m, 2, new Vec3( 0, 0, 1 ), 0.1f );

		var first = m.Evaluate( 3 ).Clone();
		m.ViewLevel = 0;
		m.Display();
		m.ViewLevel = 3;

		Check( "evaluating twice gives the same mesh", SamePositions( m.Evaluate( 3 ), first ) );

		var rebuilt = Levels( Primitives.Box( 2, 2, 2 ), 3 );
		rebuilt.Record( 2, ApplyLayer( rebuilt, 2, m.LayerAt( 2 ) ) );

		Check( "and a fresh sculpt with the same deltas agrees with it",
			SamePositions( rebuilt.Evaluate( 3 ), first ) );
	}

	static void TestTopologyIdIgnoresPositions()
	{
		var cage = Primitives.Box( 2, 2, 2 );
		var moved = MeshTransform.Transformed( cage, Xform.Scale( new Vec3( 1, 1, 1.2f ) ) );
		var rewired = cage.Clone();
		Array.Reverse( rewired.Faces[0].Indices );

		Check( "the topology id ignores a cage that only moved",
			MultiresSculpt.TopologyId( cage ) == MultiresSculpt.TopologyId( moved ) );
		Check( "and changes when the faces do",
			MultiresSculpt.TopologyId( cage ) != MultiresSculpt.TopologyId( rewired ) );
		Check( "and differs between two different primitives",
			MultiresSculpt.TopologyId( cage ) != MultiresSculpt.TopologyId( Primitives.QuadSphere( 1f, 4 ) ) );
	}

	static void TestMultiresHasAStopwatch()
	{
		// Every evaluation walks the stack from the cage up, building frames at each level. The
		// level slider is what makes that cost visible, so the suite watches it from the start.
		var m = Levels( Primitives.QuadSphere( 0.5f, 4 ), 3 );
		Bump( m, 1, new Vec3( 0, 0, 1 ), 0.05f );

		var sw = Stopwatch.StartNew();
		var mesh = m.Evaluate( 3 );
		sw.Stop();

		Check( $"a level-3 evaluation on {mesh.VertexCount} verts finishes in under two seconds",
			sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms" );
	}

	static void TestAFreshMaskChangesNothing()
	{
		// The sense of the values is the trap: Brush multiplies by the mask, so 1 must mean
		// "unprotected". A fresh mask that read 0 would silently stop every brush in the tool.
		var s = Session();
		var mask = s.MaskFor( 1 );

		Check( "a fresh mask protects nothing", !mask.Any && mask.ProtectedFraction < 1e-6f,
			$"{mask.ProtectedFraction:P0} protected" );
		Check( "and is not even handed to the brush", s.ActiveMask is null );

		var before = s.Sculpt.Evaluate( 1 );
		s.BeginStroke( Down(), Into() );
		s.EndStroke();

		Check( "so a stroke under it behaves exactly as an unmasked one",
			!SamePositions( s.Sculpt.Evaluate( 1 ), before ) );
	}

	static void TestAMaskedRegionResistsTheBrush()
	{
		// Two identical strokes, one on a protected patch and one not. The protected one has to move
		// the surface strictly less - not "differently", less.
		var free = Session();
		free.BeginStroke( Down(), Into() );
		free.EndStroke();
		var freeMoved = Moved( free.Sculpt.Evaluate( 1 ), Session().Sculpt.Evaluate( 1 ) );

		var held = Session();
		var mask = held.MaskFor( 1 );
		var mesh = held.Sculpt.Evaluate( 1 );

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			if ( mesh.Positions[i].z > 0.2f )
				mask[i] = 0f;
		}

		Check( "painting the mask registers as protection", mask.Any && held.ActiveMask is not null );

		held.BeginStroke( Down(), Into() );
		held.EndStroke();
		var heldMoved = Moved( held.Sculpt.Evaluate( 1 ), Session().Sculpt.Evaluate( 1 ) );

		Check( "the same stroke over a masked region moves far less",
			heldMoved < freeMoved * 0.2f, $"{heldMoved:0.#####} against {freeMoved:0.#####}" );
	}

	static void TestInvertSwapsWhatIsHeld()
	{
		var s = Session();
		var mask = s.MaskFor( 1 );
		mask[0] = 0f;
		mask[1] = 0.25f;

		var fraction = mask.ProtectedFraction;
		s.InvertMask();

		Check( "invert turns protection into freedom and back",
			mask[0] == 1f && MathF.Abs( mask[1] - 0.75f ) < 1e-6f, $"{mask[0]}, {mask[1]}" );

		s.InvertMask();

		Check( "and inverting twice is where it started",
			mask[0] == 0f && MathF.Abs( mask.ProtectedFraction - fraction ) < 1e-6f );

		s.ClearMask();

		Check( "clearing releases everything", !mask.Any );
	}

	static void TestAMaskStrokeIsUndoable()
	{
		var s = Session();
		s.Masking = true;
		s.Strength = 1f;

		s.BeginStroke( Down(), Into() );
		s.MoveTo( new Vec3( 0.2f, 0, 3 ), Into() );
		var edit = s.EndStroke();

		Check( "a mask stroke commits a mask edit, not a geometry one",
			edit is not null && edit.IsMask && edit.Count > 0,
			edit is null ? "nothing committed" : $"mask {edit.IsMask}, {edit.Count} vertices" );
		Check( "and it painted something", s.MaskFor( 1 ).Any );
		Check( "while leaving the surface alone", !s.Sculpt.HasDetail( 1 ) );

		Check( "undo releases it again", s.Undo() && !s.MaskFor( 1 ).Any );
		Check( "and redo paints it back", s.Redo() && s.MaskFor( 1 ).Any );
	}

	static void TestHideByMaskDropsOnlyFullyMaskedFaces()
	{
		// Dropping a face because ONE corner is masked would eat the boundary of every mask, so the
		// visible edge creeps inward each time it is used.
		var s = Session();
		var mesh = s.Sculpt.Evaluate( 1 );
		var mask = s.MaskFor( 1 );

		var face = mesh.Faces[0];
		mask[face.Indices[0]] = 0f;

		Check( "one masked corner hides nothing", s.HiddenByMask().FaceCount == mesh.FaceCount,
			$"{s.HiddenByMask().FaceCount} of {mesh.FaceCount}" );

		foreach ( var index in face.Indices )
			mask[index] = 0f;

		var hidden = s.HiddenByMask();

		Check( "a fully masked face is dropped", hidden.FaceCount == mesh.FaceCount - 1,
			$"{hidden.FaceCount} of {mesh.FaceCount}" );
		Check( "and the vertices stay put, so indices still mean what they meant",
			hidden.VertexCount == mesh.VertexCount );
	}

	static void TestEachLevelHasItsOwnMask()
	{
		var m = Levels( Primitives.QuadSphere( 0.5f, 4 ), 2 );
		var s = new SculptSession( m ) { Radius = 0.2f, Strength = 0.05f };

		s.Level = 1;
		var coarse = s.MaskFor( 1 );
		coarse[0] = 0f;

		s.Level = 2;
		var fine = s.MaskFor( 2 );

		Check( "a mask is sized to its own level", coarse.Count == m.Rest( 1 ).VertexCount
			&& fine.Count == m.Rest( 2 ).VertexCount && coarse.Count != fine.Count,
			$"{coarse.Count} and {fine.Count}" );
		Check( "and the level above does not inherit what was painted below", !fine.Any );

		s.Level = 1;

		Check( "going back down finds the mask still there", s.MaskFor( 1 ).Any );
	}

	static void TestHidingMaskedGeometryIsAViewOnly()
	{
		// Hide-by-mask is a view, like the level, and must reach the model exactly as far as that one
		// does: nowhere. Hiding half a head to reach inside it must not export half a head.
		var s = Session();
		var full = s.DisplayMesh.FaceCount;
		var mesh = s.Sculpt.Evaluate( 1 );
		var mask = s.MaskFor( 1 );

		foreach ( var index in mesh.Faces[0].Indices )
			mask[index] = 0f;

		Check( "with hiding off, a mask changes nothing on screen", s.DisplayMesh.FaceCount == full,
			$"{s.DisplayMesh.FaceCount} of {full}" );

		s.HideMasked = true;

		Check( "turning it on drops the masked face", s.DisplayMesh.FaceCount == full - 1,
			$"{s.DisplayMesh.FaceCount} of {full}" );
		Check( "and the model underneath is untouched", s.Sculpt.Evaluate( 1 ).FaceCount == full );

		// The display cache is keyed on the sculpt's revision, and painting a mask does not change
		// the sculpt at all - so without the mask's own revision in that key, this stays stale and
		// hide-by-mask looks like it stopped working after the first use.
		foreach ( var index in mesh.Faces[1].Indices )
			mask[index] = 0f;

		Check( "painting more mask updates the view rather than serving a stale mesh",
			s.DisplayMesh.FaceCount == full - 2, $"{s.DisplayMesh.FaceCount} of {full}" );

		s.HideMasked = false;

		Check( "and turning it off brings everything back", s.DisplayMesh.FaceCount == full );
	}

	static void TestReprojectionCarriesTheShapeToANewCage()
	{
		// The last resort, and the point of it: the edit WAS meant, and an approximation of the
		// sculpt beats losing it.
		// Two plane cages of the same size and different tessellation: the same surface, no shared
		// vertex indices, so nothing can be carried across by luck.
		var old = Levels( Primitives.Plane( 2, 2, 4, 4 ), 2 );
		var height = 0.25f;
		Dome( old, 2, radius: 0.6f, height: height );

		var newCage = Primitives.Plane( 2, 2, 6, 6 );

		Check( "the new cage really is a different topology",
			!old.CanRebase( newCage, out _ ), "it could have been rebased" );

		var moved = SculptReprojection.Reproject( old, newCage, out var report );

		Check( "reprojection produces a sculpt on the new cage", moved.TopLevel == 2 && moved.HasDetail( 2 ) );
		Check( "most of the new surface found the old one", report.Coverage > 0.9f, report.ToString() );

		// The dome has to still be there, at about its height, in about the right place.
		var surface = moved.Evaluate( 2 );
		var peak = float.MinValue;
		var peakAt = Vec3.Zero;

		for ( var i = 0; i < surface.VertexCount; i++ )
		{
			if ( surface.Positions[i].z > peak )
			{
				peak = surface.Positions[i].z;
				peakAt = surface.Positions[i];
			}
		}

		Check( "and the sculpted dome came with it, at its height",
			peak > height * 0.9f && peak < height * 1.1f, $"peak {peak:0.###}, authored {height:0.###}" );
		Check( "in the place it was sculpted",
			MathF.Abs( peakAt.x ) < 0.2f && MathF.Abs( peakAt.y ) < 0.2f,
			$"({peakAt.x:0.##}, {peakAt.y:0.##})" );
	}

	static void TestReprojectionReportsWhatItManaged()
	{
		// Coverage is what tells a caller the two shapes had nothing to do with each other, which is
		// the case where the result is not worth keeping.
		var old = Levels( Primitives.Plane( 2, 2, 4, 4 ), 1 );
		Dome( old, 1, radius: 0.6f, height: 0.1f );

		SculptReprojection.Reproject( old, Primitives.Plane( 2, 2, 6, 6 ), out var near );

		// A cage nowhere near the old surface. Note that "search a tiny distance" is NOT the same
		// test: two surfaces that sit on each other still hit at any reach at all, so a small radius
		// on a coincident cage reports most of the mesh found - correctly. Only moving it away makes
		// the shapes genuinely unrelated.
		var elsewhere = Primitives.Plane( 2, 2, 6, 6 );

		for ( var i = 0; i < elsewhere.VertexCount; i++ )
			elsewhere.Positions[i] += new Vec3( 0, 0, 5 );

		SculptReprojection.Reproject( old, elsewhere, out var lost );

		Check( "a cage sitting on the old surface reports high coverage", near.Coverage > 0.9f, near.ToString() );
		Check( "and one nowhere near it reports finding nothing", lost.Coverage < 0.05f, lost.ToString() );
		Check( "which is what tells a caller the result is not worth keeping",
			lost.Coverage < near.Coverage * 0.1f, $"{lost.Coverage:P0} against {near.Coverage:P0}" );
		Check( "the report says what it searched", near.MaxDistance > 0f && near.Vertices > 0 );
	}

	static void TestTheFeatureRefusesUntilAskedToReproject()
	{
		var (studio, box, sculpt) = SculptStudio();
		sculpt.Sculpt.AddLevel();
		Bump( sculpt.Sculpt, 1, new Vec3( 0, 0, 1 ), 0.15f );
		studio.Rebuild();

		box.Shape.Index = 1;
		studio.MarkDirty( box );
		studio.Rebuild();

		Check( "by default a changed cage is still refused", sculpt.Error is not null );
		Check( "and the refusal offers reprojection as a way out",
			sculpt.Diagnostic is not null
			&& sculpt.Diagnostic.Remedies.Exists( r => r.Contains( "Reproject" ) ),
			sculpt.Diagnostic is null ? "no diagnostic" : string.Join( "; ", sculpt.Diagnostic.Remedies ) );

		sculpt.Reproject.Value = true;
		studio.MarkDirty( sculpt );
		studio.Rebuild();

		Check( "asked for it, the model builds", sculpt.Error is null, sculpt.Error ?? "" );
		Check( "the sculpt is on the new cage", sculpt.Sculpt.HasDetail( sculpt.Sculpt.TopLevel ) );
		Check( "and it warns rather than saying nothing, because the deltas are gone",
			sculpt.Warning is not null && sculpt.Diagnostic is not null
			&& sculpt.Diagnostic.Remedies.Count > 0,
			sculpt.Warning ?? "silent" );
	}

	/// <summary>Total distance between two meshes of the same size - how far a stroke moved things.</summary>
	static float Moved( PolyMesh a, PolyMesh b )
	{
		var total = 0f;

		for ( var i = 0; i < a.VertexCount; i++ )
			total += (a.Positions[i] - b.Positions[i]).Length;

		return total;
	}

	static void TestAnUnsculptedBakeIsFlatAndNotEmpty()
	{
		// "Flat" alone proves nothing — a bake whose rays all missed is also flat. The filled count
		// is what separates "the sculpt matches the cage" from "nothing was measured at all".
		var m = BakeFixture();
		var map = NormalBake.Bake( m.Cage, m.Evaluate( 2 ), 64 );

		Check( "an unsculpted bake actually hit the surface", map.FilledCount > 64 * 64 * 0.9f,
			$"{map.FilledCount} of {64 * 64} texels" );

		var worst = 0f;

		for ( var y = 0; y < 64; y++ )
		{
			for ( var x = 0; x < 64; x++ )
				worst = MathF.Max( worst, (map.NormalAt( x, y ) - new Vec3( 0, 0, 1 )).Length );
		}

		Check( "and with nothing sculpted every texel points straight out", worst < 0.02f,
			$"worst deviation {worst:0.####}" );
	}

	static void TestABumpTiltsTheMapTheWayItLeans()
	{
		// The check the whole step exists for, and the one that catches a swapped or flipped tangent:
		// a dome's flanks have to lean OUTWARD, in opposite directions, in the cage's own frame.
		var m = BakeFixture();
		Dome( m, 2, radius: 0.6f, height: 0.2f );

		var map = NormalBake.Bake( m.Cage, m.Evaluate( 2 ), 64 );

		var centre = map.NormalAt( 32, 32 );
		var right = map.NormalAt( Texel( 0.3f ), 32 );
		var left = map.NormalAt( Texel( -0.3f ), 32 );
		var far = map.NormalAt( 32, Texel( 0.3f ) );
		var near = map.NormalAt( 32, Texel( -0.3f ) );

		Check( "the top of the bump still points along the cage normal", centre.z > 0.98f, $"z {centre.z:0.###}" );

		Check( "the +u flank leans towards +u", right.x > 0.05f, $"x {right.x:0.###}" );
		Check( "the -u flank leans the other way", left.x < -0.05f, $"x {left.x:0.###}" );
		Check( "and the two are mirror images, not merely both non-zero",
			MathF.Abs( right.x + left.x ) < 0.05f, $"{right.x:0.###} vs {left.x:0.###}" );

		Check( "the +v flank leans towards +v", far.y > 0.05f, $"y {far.y:0.###}" );
		Check( "the -v flank leans the other way", near.y < -0.05f, $"y {near.y:0.###}" );

		// A map that came out flat would pass every sign test above by accident if the thresholds
		// were loose, so say plainly that the thing is not flat.
		Check( "and the map is a bump rather than a flat sheet", MathF.Abs( right.x ) > 0.1f,
			$"lean {right.x:0.###}" );

		// A map baked from FACE normals passes every check above and is faceted — the one thing a
		// normal map exists to avoid, and invisible in numbers unless something looks for it.
		//
		// AN ABSOLUTE THRESHOLD CANNOT TELL THE TWO APART, which is the trap here: a smooth map of a
		// steep dome has a large step between neighbouring texels too. What separates them is how the
		// step behaves as the map gets finer. A smooth bake samples a continuous function, so doubling
		// the resolution roughly halves the step; a faceted one is stuck to the source geometry and
		// barely improves. Measured on this fixture: 0.086 at 64px, 0.048 at 128, 0.024 at 256.
		var shape = m.Evaluate( 2 );
		var coarse = WorstStep( NormalBake.Bake( m.Cage, shape, 64 ), 64 );
		var fine = WorstStep( NormalBake.Bake( m.Cage, shape, 128 ), 128 );

		Check( "the flank is smooth, not faceted: twice the resolution halves the step",
			fine < coarse * 0.65f, $"{coarse:0.####} at 64px became {fine:0.####} at 128px" );
	}

	static void TestFlippingGreenFlipsOnlyGreen()
	{
		// Two conventions differ only in the sign of Y, and the wrong one lights every dent as a bump
		// while looking entirely plausible in a thumbnail. Which one s&box wants still has to be
		// confirmed on screen; what this pins down is that the switch does what it says.
		var m = BakeFixture();
		Dome( m, 2, radius: 0.6f, height: 0.2f );

		var sculpted = m.Evaluate( 2 );
		var normal = NormalBake.Bake( m.Cage, sculpted, 64 );
		var flipped = NormalBake.Bake( m.Cage, sculpted, 64, new BakeOptions { FlipGreen = true } );

		var x = 32;
		var y = Texel( 0.3f );
		var (r0, g0, b0) = normal.At( x, y );
		var (r1, g1, b1) = flipped.At( x, y );

		Check( "flipping green leaves red and blue alone", r0 == r1 && b0 == b1, $"{r0},{b0} vs {r1},{b1}" );
		Check( "and mirrors green about the midpoint", Math.Abs( (g0 - 128) + (g1 - 128) ) <= 1,
			$"{g0} and {g1}" );
	}

	static void TestPaddingBleedsPastTheIsland()
	{
		// Without a bleed, a shader filtering across the island edge picks up whatever is outside it,
		// and seams glow once mipmaps get involved.
		var m = BakeFixture();
		ScaleUVs( m, 0.5f );          // the island now covers the lower-left quarter
		Ramp( m, 2, 0.25f );          // tilt everything, so an edge texel is visibly not flat

		var sculpted = m.Evaluate( 2 );
		var bare = NormalBake.Bake( m.Cage, sculpted, 64, new BakeOptions { Padding = 0 } );
		var padded = NormalBake.Bake( m.Cage, sculpted, 64, new BakeOptions { Padding = 4 } );

		// Two texels past the island's edge at u = 0.5.
		const int x = 34;
		const int y = 16;

		Check( "outside the island an unpadded bake is left at flat", bare.NormalAt( x, y ).z > 0.999f,
			$"{bare.NormalAt( x, y ).z:0.####}" );
		Check( "and padding carries the island's edge outwards", padded.NormalAt( x, y ).z < 0.999f,
			$"{padded.NormalAt( x, y ).z:0.####}" );
		Check( "without claiming those texels were measured", padded.FilledCount == bare.FilledCount,
			$"{bare.FilledCount} became {padded.FilledCount}" );
	}

	static void TestMirroredUVsBakeTheSameWayUp()
	{
		// Mirrored UVs are ordinary — half a character is usually the other half flipped, and this
		// tool has a Mirror feature. A mirrored island's tangent runs backwards, so the frame's
		// handedness has to be read off the UVs rather than assumed; get it wrong and that island's
		// green channel comes out inverted, which lights every bump on one side of the model as a
		// dent. Nothing else in this file can see it, because an unmirrored fixture never takes the
		// branch.
		// Tilted in both axes on purpose: the y tilt is what the handedness check reads, and the x
		// tilt is what makes "its u runs the other way" a real comparison rather than 0 against 0.
		var m = Levels( MirroredUVPlane(), 2 );
		Ramp( m, 2, 0.25f );
		SlopeAlongY( m, 2, 0.25f );

		var map = NormalBake.Bake( m.Cage, m.Evaluate( 2 ), 64 );

		// Same v, one island each side of the seam at u = 0.5.
		var normal = map.NormalAt( 16, 32 );
		var mirrored = map.NormalAt( 48, 32 );

		Check( "the ramp leans the map in v at all", MathF.Abs( normal.y ) > 0.1f, $"y {normal.y:0.###}" );
		Check( "a mirrored island leans the same way in v as an unmirrored one",
			MathF.Sign( normal.y ) == MathF.Sign( mirrored.y )
			&& MathF.Abs( normal.y - mirrored.y ) < 0.05f,
			$"{normal.y:0.###} vs {mirrored.y:0.###}" );
		Check( "and its u runs the other way, because that is what mirrored means",
			MathF.Sign( normal.x ) != MathF.Sign( mirrored.x ) || MathF.Abs( normal.x ) < 1e-3f,
			$"{normal.x:0.###} vs {mirrored.x:0.###}" );
	}

	/// <summary>
	/// Two quads side by side, the right one's UVs mirrored into its own half of the square. No
	/// overlap, one seam, and opposite UV handedness either side of it.
	/// </summary>
	static PolyMesh MirroredUVPlane()
	{
		var m = new PolyMesh();

		m.AddVertex( new Vec3( -1, -1, 0 ) );
		m.AddVertex( new Vec3( 0, -1, 0 ) );
		m.AddVertex( new Vec3( 1, -1, 0 ) );
		m.AddVertex( new Vec3( -1, 1, 0 ) );
		m.AddVertex( new Vec3( 0, 1, 0 ) );
		m.AddVertex( new Vec3( 1, 1, 0 ) );

		m.AddFace( new[] { 0, 1, 4, 3 }, new[]
		{
			new Vec2( 0f, 0f ), new Vec2( 0.5f, 0f ), new Vec2( 0.5f, 1f ), new Vec2( 0f, 1f )
		} );

		// u DECREASES with x here: the island runs backwards across 0.5 to 1.
		m.AddFace( new[] { 1, 2, 5, 4 }, new[]
		{
			new Vec2( 1f, 0f ), new Vec2( 0.5f, 0f ), new Vec2( 0.5f, 1f ), new Vec2( 1f, 1f )
		} );

		return m;
	}

	/// <summary>Tilt the level along y, so the lean shows up in the green channel.</summary>
	static void SlopeAlongY( MultiresSculpt m, int level, float slope )
	{
		var mesh = m.Evaluate( level );

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			mesh.Positions[i] = new Vec3( p.x, p.y, p.z + p.y * slope );
		}

		m.Record( level, mesh );
	}

	static void TestTheBakeIsRepeatable()
	{
		var m = BakeFixture();
		Dome( m, 2, radius: 0.6f, height: 0.2f );

		var sculpted = m.Evaluate( 2 );
		var a = NormalBake.Bake( m.Cage, sculpted, 48 );
		var b = NormalBake.Bake( m.Cage, sculpted, 48 );

		var same = a.Rgb.Length == b.Rgb.Length;

		for ( var i = 0; same && i < a.Rgb.Length; i++ )
			same = a.Rgb[i] == b.Rgb[i];

		Check( "baking the same pair twice gives the same bytes", same );
	}

	static void TestUVsAreCheckedBeforeTheyAreTrusted()
	{
		// The plan has said since it was written that a bake needs non-overlapping UVs and that
		// nothing checked it. An overlapping bake does not fail: it produces a plausible map that is
		// wrong wherever two faces shared a texel, which is the worst way for this to go wrong.
		var plane = Primitives.Plane( 2, 2, 4, 4 );
		var clean = NormalBake.Measure( plane, 128 );

		Check( "a plane's own UVs can carry a bake", clean.CanBake && clean.Problem is null, clean.Problem );
		Check( "and they cover the square", clean.CoveredFraction > 0.95f, $"{clean.CoveredFraction:P0}" );
		Check( "with nothing claimed twice", clean.OverlappingTexels == 0, $"{clean.OverlappingTexels}" );

		// Every face on the same square: overlap without anything leaving the 0-1 range, so the
		// overlap test is what has to catch it rather than the bounds test.
		var stacked = Primitives.Plane( 2, 2, 2, 2 );

		foreach ( var face in stacked.Faces )
			face.UVs = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };

		var piled = NormalBake.Measure( stacked, 128 );

		Check( "four faces stacked on one square is refused", !piled.CanBake && piled.OverlappingTexels > 0,
			$"{piled.OverlappingTexels} overlapping" );
		Check( "and the refusal counts them", piled.Problem is not null && piled.Problem.Contains( "more" ),
			piled.Problem );

		// The tool's own default. Box projection tiles on purpose, which is right for a wall and
		// wrong for a bake, and saying so here is the point of the check.
		var box = Primitives.Box( 2, 2, 2 );
		UVProjection.BoxProject( box );
		var projected = NormalBake.Measure( box, 128 );

		Check( "and box-projected UVs are named as unbakeable rather than quietly baked",
			!projected.CanBake && projected.Problem is not null, projected.Problem ?? "it accepted them" );
	}

	/// <summary>A flat cage with clean 0-1 UVs and two sculpt levels — the simplest thing a bake can
	/// be judged on, because every answer is known in advance.</summary>
	static MultiresSculpt BakeFixture() => Levels( Primitives.Plane( 2, 2, 4, 4 ), 2 );

	/// <summary>Largest change between neighbouring texels across the middle half of a map's centre row.</summary>
	static float WorstStep( BakedMap map, int res )
	{
		var worst = 0f;

		for ( var x = (int)(0.25f * res); x < (int)(0.75f * res); x++ )
			worst = MathF.Max( worst, (map.NormalAt( x + 1, res / 2 ) - map.NormalAt( x, res / 2 )).Length );

		return worst;
	}

	/// <summary>Texel column (or row) for a world coordinate on the 2-unit fixture plane.</summary>
	static int Texel( float world ) => (int)((world / 2f + 0.5f) * 64);

	/// <summary>Raise a smooth dome at the middle of the fixture, centred on UV (0.5, 0.5).</summary>
	static void Dome( MultiresSculpt m, int level, float radius, float height )
	{
		var mesh = m.Evaluate( level );

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			var r = MathF.Sqrt( p.x * p.x + p.y * p.y );

			if ( r >= radius )
				continue;

			var t = 1f - r / radius;
			mesh.Positions[i] = new Vec3( p.x, p.y, p.z + height * t * t * (3f - 2f * t) );
		}

		m.Record( level, mesh );
	}

	/// <summary>Tilt the whole level, so every baked texel leans and an edge one is visibly not flat.</summary>
	static void Ramp( MultiresSculpt m, int level, float slope )
	{
		var mesh = m.Evaluate( level );

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			mesh.Positions[i] = new Vec3( p.x, p.y, p.z + p.x * slope );
		}

		m.Record( level, mesh );
	}

	/// <summary>Shrink the cage's UVs so the island covers part of the square rather than all of it.</summary>
	static void ScaleUVs( MultiresSculpt m, float scale )
	{
		var cage = m.Cage;

		foreach ( var face in cage.Faces )
		{
			for ( var i = 0; i < face.UVs.Length; i++ )
				face.UVs[i] = new Vec2( face.UVs[i].x * scale, face.UVs[i].y * scale );
		}

		m.SetCage( cage );
	}

	static void TestHoverFindsTheSurfaceAndMissesPastIt()
	{
		var s = Session();

		var hit = s.Hover( new Vec3( 0, 0, 3 ), new Vec3( 0, 0, -1 ) );
		Check( "the cursor finds the surface under the ray",
			hit is not null && hit.Value.Point.z > 0.4f && hit.Value.Point.z < 0.6f,
			hit is null ? "missed" : $"{hit.Value.Point.z:0.###}" );

		Check( "and a ray pointing away finds nothing",
			s.Hover( new Vec3( 0, 0, 3 ), new Vec3( 0, 0, 1 ) ) is null );
	}

	static void TestAMissedClickStartsNothing()
	{
		var s = Session();
		var before = s.Sculpt.Revision;

		Check( "clicking past the model does not begin a stroke",
			!s.BeginStroke( new Vec3( 0, 0, 3 ), new Vec3( 0, 0, 1 ) ) && !s.IsStroking );
		Check( "and changes nothing", s.Sculpt.Revision == before && !s.Sculpt.HasDetail( 1 ) );
	}

	static void TestAClickLeavesAMark()
	{
		// A click that lands and does nothing is the failure mode worth guarding: the first sample
		// has to be applied on the press, not on the first move, or a tap reads as a dead tool.
		var s = Session();
		var before = s.Sculpt.Evaluate( 1 );

		Check( "the press lands on the model", s.BeginStroke( Down(), Into() ) );

		var edit = s.EndStroke();

		Check( "and a single click leaves a mark", edit is not null && edit.Count > 0,
			edit is null ? "nothing was committed" : "0 vertices" );
		Check( "which is recorded at the level", s.Sculpt.HasDetail( 1 ) );
		Check( "and shows on the model", !SamePositions( s.Sculpt.Evaluate( 1 ), before ) );
	}

	static void TestHoldingStillDoesNotPileUpSamples()
	{
		// A pointer reports far faster than a brush needs. Without spacing, holding still would bite
		// harder the longer you hovered, and a slow drag would cut deeper than a quick one for the
		// same gesture.
		var s = Session();
		s.BeginStroke( Down(), Into() );

		var afterPress = s.DisplayMesh.Clone();
		var samples = 0;

		for ( var i = 0; i < 10; i++ )
			samples += s.MoveTo( Down(), Into() );

		Check( "ten reports from a still pointer produce no extra samples", samples == 0, $"{samples} samples" );
		Check( "and the mesh does not creep", SamePositions( s.DisplayMesh, afterPress ) );

		s.EndStroke();
	}

	static void TestAFastDragFillsTheGapInsteadOfDotting()
	{
		var s = Session();
		s.Radius = 0.15f;
		s.BeginStroke( new Vec3( -0.35f, 0, 3 ), Into() );

		var before = s.DisplayMesh.Clone();
		var samples = s.MoveTo( new Vec3( 0.35f, 0, 3 ), Into() );

		Check( "one big jump becomes several samples rather than one", samples > 1, $"{samples} samples" );

		// The point of filling it in: the middle of the path is sculpted too, not just the ends.
		var middle = false;

		for ( var i = 0; i < before.VertexCount; i++ )
		{
			var p = before.Positions[i];

			if ( MathF.Abs( p.x ) < 0.1f && p.z > 0.3f && !p.AlmostEquals( s.DisplayMesh.Positions[i], 1e-5f ) )
				middle = true;
		}

		Check( "and the middle of the drag was sculpted, not skipped over", middle );

		s.EndStroke();
	}

	static void TestOneStrokeIsOneRevisionAndOneUndo()
	{
		// The design this asserts is a performance one: a stroke brushes a working mesh and records
		// once. Recording per sample would be correct and unusably slow, and this is what says which
		// of the two is in the file.
		var s = Session();
		var before = s.Sculpt.Revision;

		s.BeginStroke( new Vec3( -0.3f, 0, 3 ), Into() );
		var samples = s.MoveTo( new Vec3( 0.3f, 0, 3 ), Into() );
		s.EndStroke();

		Check( $"a stroke of {samples} samples is one revision", s.Sculpt.Revision == before + 1,
			$"revision {before} became {s.Sculpt.Revision}" );
		Check( "and one undo step", s.CanUndo && !s.CanRedo );
	}

	static void TestUndoAndRedoRoundTrip()
	{
		var s = Session();
		var before = s.Sculpt.Evaluate( 1 );

		s.BeginStroke( new Vec3( -0.3f, 0, 3 ), Into() );
		s.MoveTo( new Vec3( 0.3f, 0, 3 ), Into() );
		var edit = s.EndStroke();

		var after = s.Sculpt.Evaluate( 1 );

		Check( "the stroke moved a working set rather than the whole level",
			edit.Count > 0 && edit.Count < before.VertexCount,
			$"{edit.Count} of {before.VertexCount} vertices" );

		Check( "undo puts the surface back", s.Undo() && SamePositions( s.Sculpt.Evaluate( 1 ), before ) );
		Check( "and there is nothing left to undo", !s.CanUndo && s.CanRedo );
		Check( "redo puts the stroke back", s.Redo() && SamePositions( s.Sculpt.Evaluate( 1 ), after ) );
	}

	static void TestTheStrokeLandsAtTheLevelBeingWorkedAt()
	{
		var m = Levels( Primitives.QuadSphere( 0.5f, 4 ), 2 );
		var s = new SculptSession( m ) { Radius = 0.2f, Strength = 0.05f };
		s.Level = 1;

		s.BeginStroke( Down(), Into() );
		s.EndStroke();

		Check( "the stroke lands at the level being worked at", m.HasDetail( 1 ) );
		Check( "and not at the one above it", !m.HasDetail( 2 ) );
		Check( "the level and the view are one value, not two that can disagree",
			s.Level == m.ViewLevel && s.Level == 1 );
	}

	static void TestDraggingOffTheModelKeepsTheStroke()
	{
		// Dragging off the silhouette and back on is an ordinary gesture. Ending the stroke there
		// would make the tool feel like it drops what you were doing.
		var s = Session();
		s.BeginStroke( Down(), Into() );

		var samples = s.MoveTo( new Vec3( 0, 0, 3 ), new Vec3( 0, 0, 1 ) );

		Check( "a ray that misses adds nothing", samples == 0 );
		Check( "but the stroke is still running", s.IsStroking );

		s.MoveTo( new Vec3( 0.3f, 0, 3 ), Into() );

		Check( "and it picks up again on the way back", s.EndStroke() is not null );
	}

	static void TestCancellingAStrokeLeavesTheModelAlone()
	{
		var s = Session();
		var before = s.Sculpt.Evaluate( 1 );

		s.BeginStroke( Down(), Into() );
		s.MoveTo( new Vec3( 0.3f, 0, 3 ), Into() );
		s.CancelStroke();

		Check( "cancelling leaves the model as it was",
			!s.IsStroking && SamePositions( s.Sculpt.Evaluate( 1 ), before ) );
		Check( "and leaves nothing on the undo stack", !s.CanUndo );
	}

	static void TestTheDisplayMeshIsCachedUntilSomethingMoves()
	{
		// A viewport asks every frame, and evaluating the level stack is not a per-frame cost.
		var s = Session();
		var first = s.DisplayMesh;

		Check( "asking twice does not evaluate twice", ReferenceEquals( first, s.DisplayMesh ) );

		s.BeginStroke( Down(), Into() );

		Check( "mid-stroke the viewport gets the live working mesh", !ReferenceEquals( first, s.DisplayMesh ) );

		s.EndStroke();

		Check( "and after the stroke it is rebuilt rather than served stale",
			!ReferenceEquals( first, s.DisplayMesh ) && !SamePositions( s.DisplayMesh, first ) );
	}

	static void TestRemovingALevelIsUndoable()
	{
		// REMOVING A LEVEL THROWS AWAY EVERY DELTA ON IT. That is why it sat in the kernel unexposed
		// until the session could hold what it dropped: a destructive button with no way back is one
		// nobody should be given, and this is the check that says it now has one.
		var m = Levels( Primitives.QuadSphere( 0.5f, 4 ), 2 );
		var s = new SculptSession( m ) { Radius = 0.2f, Strength = 0.05f };

		Bump( m, 1, new Vec3( 0, 0, 1 ), 0.1f );
		Bump( m, 2, new Vec3( 0, 0, 1 ), 0.05f );

		var atTop = m.Evaluate( 2 ).Clone();
		var atOne = m.Evaluate( 1 ).Clone();

		Check( "the fine level is removed", s.RemoveTopLevel() && m.TopLevel == 1 );
		Check( "and the coarse one is untouched by it", SamePositions( m.Evaluate( 1 ), atOne ) );

		Check( "undo puts the level back", s.Undo() && m.TopLevel == 2 );
		Check( "with the detail it had", SamePositions( m.Evaluate( 2 ), atTop ) );

		Check( "and redo takes it away again", s.Redo() && m.TopLevel == 1 );
		Check( "which undo can still reverse", s.Undo() && m.TopLevel == 2
			&& SamePositions( m.Evaluate( 2 ), atTop ) );

		Check( "the cage level can never be removed",
			!new SculptSession( new MultiresSculpt( Primitives.Box( 1, 1, 1 ) ) ).RemoveTopLevel() );
	}

	static void TestPuttingALevelBackOntoAChangedBaseIsRefused()
	{
		// The layer only fits if the levels below it are as they were. Sculpting underneath does not
		// change the vertex COUNT, so that case still fits and should still work - but a cage swap
		// that changes the count must be refused rather than land old detail on new vertices.
		var m = Levels( Primitives.QuadSphere( 0.5f, 4 ), 1 );
		Bump( m, 1, new Vec3( 0, 0, 1 ), 0.1f );

		var dropped = m.RemoveTopLevel();

		Bump( m, 0, new Vec3( 0, 0, 1 ), 0.05f );

		var threw = false;

		try
		{
			m.RestoreTopLevel( dropped );
		}
		catch ( ArgumentException )
		{
			threw = true;
		}

		Check( "sculpting the level below does not stop the level going back", !threw
			&& m.TopLevel == 1, "it was refused" );

		var wrongSize = new SculptLayer( new Vec3[3] );
		var refused = false;

		try
		{
			m.RestoreTopLevel( wrongSize );
		}
		catch ( ArgumentException )
		{
			refused = true;
		}

		Check( "but a layer of the wrong size is", refused );
		Check( "and the refusal leaves the sculpt as it found it", m.TopLevel == 1 );
	}

	/// <summary>A one-level sculpt on a sphere, with a session on it.</summary>
	static SculptSession Session() =>
		new( Levels( Primitives.QuadSphere( 0.5f, 4 ), 1 ) ) { Radius = 0.25f, Strength = 0.05f };

	/// <summary>A ray origin above the sphere, and the direction that reaches it.</summary>
	static Vec3 Down() => new( 0, 0, 3 );

	static Vec3 Into() => new( 0, 0, -1 );

	static void TestSculptFeaturePassesTheCageThroughUntouched()
	{
		var (studio, _, sculpt) = SculptStudio();

		Check( "a sculpt feature with nothing sculpted builds cleanly",
			sculpt.Error is null && studio.Bodies.Count == 1, sculpt.Error ?? $"{studio.Bodies.Count} bodies" );
		Check( "and hands the cage through unchanged",
			SamePositions( studio.Bodies[0].Mesh, Primitives.Box( 2, 2, 2 ) ),
			$"{studio.Bodies[0].Mesh.VertexCount} verts" );
		Check( "it got its cage from the body underneath it", sculpt.Sculpt is not null && sculpt.Sculpt.TopLevel == 0 );
	}

	static void TestSculptFeatureOutputsTheTopLevel()
	{
		var (studio, _, sculpt) = SculptStudio();
		sculpt.Sculpt.AddLevel();
		sculpt.Sculpt.AddLevel();
		Bump( sculpt.Sculpt, 2, new Vec3( 0, 0, 1 ), 0.1f );

		// The view is a UI state and must not reach the model — an L1 preview that exported an L1
		// model would lose the sculpt silently, which is the failure worth designing against.
		sculpt.Sculpt.ViewLevel = 1;
		studio.Rebuild();

		Check( "the feature builds the top level, not the level being viewed",
			sculpt.Error is null && studio.Bodies[0].Mesh.VertexCount == sculpt.Sculpt.Rest( 2 ).VertexCount,
			sculpt.Error ?? $"{studio.Bodies[0].Mesh.VertexCount} verts" );
		Check( "and the body is exactly what the sculpt evaluates to",
			SamePositions( studio.Bodies[0].Mesh, sculpt.Sculpt.Evaluate( 2 ) ) );
	}

	static void TestAParametricEditCarriesTheSculpt()
	{
		// The claim the whole pipeline is sold on: change the CAD after sculpting and keep the sculpt.
		var (studio, box, sculpt) = SculptStudio();
		sculpt.Sculpt.AddLevel();
		sculpt.Sculpt.AddLevel();
		var height = 0.1f;
		var bump = Bump( sculpt.Sculpt, 2, new Vec3( 0, 0, 1 ), height );
		studio.Rebuild();

		var before = studio.Bodies[0].Mesh.Positions[bump].z;

		box.SizeZ.Value = 3f;
		studio.MarkDirty( box );
		studio.Rebuild();

		Check( "a taller box rebuilds with no complaint from the sculpt", sculpt.Error is null, sculpt.Error ?? "" );

		var rest = sculpt.Sculpt.Rest( 2 );
		var shown = studio.Bodies[0].Mesh;
		var n = sculpt.Sculpt.FramesFor( 2 ).At[bump].Normal;
		var world = shown.Positions[bump] - rest.Positions[bump];
		var along = Vec3.Dot( world, n );

		Check( "the cage actually moved", shown.Positions[bump].z > before + 0.1f,
			$"z {before:0.####} → {shown.Positions[bump].z:0.####}" );
		Check( "and the sculpt is still on the surface", along > height * 0.5f,
			$"along {along:0.####}, authored {height:0.####}" );
	}

	static void TestATopologyChangeIsRefusedAndTheDeltasKept()
	{
		var (studio, box, sculpt) = SculptStudio();
		sculpt.Sculpt.AddLevel();
		Bump( sculpt.Sculpt, 1, new Vec3( 0, 0, 1 ), 0.1f );
		studio.Rebuild();

		var sculpted = studio.Bodies[0].Mesh.Clone();

		box.Shape.Index = 1; // a cylinder is a different cage, not a moved one
		studio.MarkDirty( box );
		studio.Rebuild();

		Check( "changing the cage's topology is an error, not a silent reshape", sculpt.Error is not null,
			"it accepted it" );
		Check( "and the refusal has a cause and a way out",
			sculpt.Diagnostic is not null
			&& !string.IsNullOrWhiteSpace( sculpt.Diagnostic.Cause )
			&& sculpt.Diagnostic.Remedies.Count > 0,
			sculpt.Diagnostic is null ? "no diagnostic" : sculpt.Diagnostic.Cause );
		// Guarded rather than bare: a regression that restarts the sculpt leaves no level 1 at all,
		// and asking an absent level for its detail would abort the run instead of failing this line.
		Check( "the deltas are kept rather than dropped",
			sculpt.Sculpt.TopLevel >= 1 && sculpt.Sculpt.HasDetail( 1 ),
			$"top level {sculpt.Sculpt.TopLevel}" );

		// The point of keeping them: undoing the upstream edit brings the sculpt back exactly.
		box.Shape.Index = 0;
		studio.MarkDirty( box );
		studio.Rebuild();

		Check( "so undoing the edit restores the sculpt exactly",
			sculpt.Error is null && SamePositions( studio.Bodies[0].Mesh, sculpted ), sculpt.Error ?? "" );
	}

	static void TestBlobRoundTripsTheDeltas()
	{
		var cage = Primitives.Box( 2, 2, 2 );
		var m = Levels( cage, 2 );
		Bump( m, 1, new Vec3( 0, 0, 1 ), 0.15f );
		Bump( m, 2, new Vec3( 1, 0, 0 ), 0.05f );

		var before = m.Evaluate( 2 );
		var bytes = SculptBlob.Write( m );
		var back = SculptBlob.Read( bytes, cage );
		var after = back.Evaluate( 2 );

		Check( "the blob carries every level", back.LevelCount == m.LevelCount,
			$"{m.LevelCount} became {back.LevelCount}" );

		var worst = 0f;

		for ( var i = 0; i < before.VertexCount; i++ )
			worst = MathF.Max( worst, (before.Positions[i] - after.Positions[i]).Length );

		// 16 bits across one level's delta box, on a cage two units across. The quantisation step is
		// far below anything visible; this is the check that says so in numbers rather than in prose.
		Check( "and 16-bit deltas come back within a ten-thousandth of a unit", worst < 1e-4f,
			$"worst {worst:0.#######}" );
	}

	static void TestAnUntouchedLevelComesBackExact()
	{
		// A level nobody sculpted has to round-trip bit-identical, not merely close: a model saved
		// and reloaded twenty times must not drift away from the cage a hundredth at a time.
		var cage = Primitives.Box( 2, 2, 2 );
		var m = Levels( cage, 2 );
		Bump( m, 2, new Vec3( 0, 0, 1 ), 0.1f );

		var back = SculptBlob.Read( SculptBlob.Write( m ), cage );
		var exact = true;

		foreach ( var d in back.LayerAt( 1 ).Deltas )
			exact &= d.x == 0f && d.y == 0f && d.z == 0f;

		Check( "an untouched level comes back exactly zero, not nearly zero", exact );
		Check( "and an untouched cage level too", !back.HasDetail( 0 ) );
	}

	static void TestBlobCostsSixBytesAVertex()
	{
		var cage = Primitives.Box( 2, 2, 2 );
		var m = Levels( cage, 3 );
		Bump( m, 3, new Vec3( 0, 0, 1 ), 0.05f );

		var bytes = SculptBlob.Write( m );
		var vertices = 0;

		for ( var level = 0; level < m.LevelCount; level++ )
			vertices += m.LayerAt( level ).Count;

		Check( "the predicted size is the actual size", SculptBlob.PredictBytes( m ) == bytes.Length,
			$"predicted {SculptBlob.PredictBytes( m )}, wrote {bytes.Length}" );
		Check( $"and {vertices} vertices cost six bytes each plus a small header",
			bytes.Length - vertices * SculptBlob.BytesPerVertex is > 0 and < 256,
			$"{bytes.Length} bytes for {vertices} vertices" );
	}

	static void TestBlobRefusesTheWrongCage()
	{
		var m = Levels( Primitives.Box( 2, 2, 2 ), 1 );
		Bump( m, 1, new Vec3( 0, 0, 1 ), 0.1f );
		var bytes = SculptBlob.Write( m );

		Check( "a blob read against a different cage is refused",
			Throws( () => SculptBlob.Read( bytes, Primitives.QuadSphere( 1f, 4 ) ), out var why ) );
		Check( "and says why rather than producing a mangled model",
			why is not null && why.Contains( "different cage" ), why ?? "" );

		// The case a vertex count alone waves through.
		var rewired = Primitives.Box( 2, 2, 2 );
		Array.Reverse( rewired.Faces[0].Indices );

		Check( "including a cage with the same counts but different faces",
			Throws( () => SculptBlob.Read( bytes, rewired ), out _ ) );

		Check( "and the same cage is accepted",
			!Throws( () => SculptBlob.Read( bytes, Primitives.Box( 2, 2, 2 ) ), out _ ) );
	}

	static void TestBlobRefusesSomethingThatIsNotOne()
	{
		var cage = Primitives.Box( 2, 2, 2 );

		Check( "a short file is refused", Throws( () => SculptBlob.Read( new byte[] { 1, 2, 3 }, cage ), out _ ) );

		var notABlob = new byte[64];
		Check( "so is something the right size that is not a blob",
			Throws( () => SculptBlob.Read( notABlob, cage ), out var why ) && why.Contains( "EFFIGYSC" ), why ?? "" );

		var newer = SculptBlob.Write( new MultiresSculpt( cage ) );
		BitConverter.GetBytes( SculptBlob.Version + 9 ).CopyTo( newer, 8 );

		Check( "and a blob from a newer build is refused by version",
			Throws( () => SculptBlob.Read( newer, cage ), out var version ) && version.Contains( "newer" ),
			version ?? "" );
	}

	static void TestTheSidecarCarriesTheSculptAcrossASaveAndLoad()
	{
		// The end-to-end claim of step 6: close the document, open it, and the sculpt is still there.
		// Field-by-field checks on the feature tree can pass while this fails, because the deltas are
		// deliberately not in the document at all.
		var dir = Path.Combine( Path.GetTempPath(), $"effigy-sculpt-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( dir );

		try
		{
			var path = Path.Combine( dir, "model" + StudioDocument.Extension );
			var (studio, _, sculpt) = SculptStudio();
			sculpt.Sculpt.AddLevel();
			sculpt.Sculpt.AddLevel();
			Bump( sculpt.Sculpt, 2, new Vec3( 0, 0, 1 ), 0.12f );
			studio.Rebuild();

			var before = studio.Bodies[0].Mesh.Clone();

			StudioDocument.WriteFile( studio, path );
			var written = SculptSidecar.Save( studio, path );

			Check( "saving writes one blob beside the document", written == 1
				&& File.Exists( SculptSidecar.PathFor( path, sculpt.Id ) ), $"wrote {written}" );

			var back = StudioDocument.ReadFile( path );
			var loaded = SculptSidecar.Load( back, path );
			var report = back.Rebuild();
			var reloaded = back.Features[1] as SculptFeature;

			Check( "loading hands the blob to the feature that owns it", loaded == 1 && reloaded is not null );
			Check( "the reloaded document rebuilds without errors", !report.HasErrors, report.ToString() );
			Check( "the sculpt is on the model again, to the same vertex",
				SamePositions( back.Bodies[0].Mesh, before ),
				$"{before.VertexCount} verts vs {back.Bodies[0].Mesh.VertexCount}" );
			Check( "and it came back as levels rather than a baked mesh",
				reloaded is not null && reloaded.Sculpt is not null && reloaded.Sculpt.TopLevel == 2
				&& reloaded.Sculpt.HasDetail( 2 ),
				reloaded?.Sculpt is null ? "no sculpt" : $"top {reloaded.Sculpt.TopLevel}" );
		}
		finally
		{
			Directory.Delete( dir, recursive: true );
		}
	}

	static void TestSavingDoesNotDeleteABlobItDidNotWrite()
	{
		var dir = Path.Combine( Path.GetTempPath(), $"effigy-sculpt-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( dir );

		try
		{
			var path = Path.Combine( dir, "model" + StudioDocument.Extension );
			var (studio, _, sculpt) = SculptStudio();
			sculpt.Sculpt.AddLevel();
			Bump( sculpt.Sculpt, 1, new Vec3( 0, 0, 1 ), 0.1f );
			SculptSidecar.Save( studio, path );

			var stray = Path.Combine( SculptSidecar.DirectoryFor( path ), "deadbeef.bin" );
			File.WriteAllBytes( stray, new byte[] { 1, 2, 3 } );

			SculptSidecar.Save( studio, path );

			Check( "saving leaves a blob whose feature it does not know about", File.Exists( stray ) );

			var pruned = SculptSidecar.Prune( studio, path );

			Check( "and pruning removes it only when asked",
				pruned == 1 && !File.Exists( stray ) && File.Exists( SculptSidecar.PathFor( path, sculpt.Id ) ),
				$"pruned {pruned}" );
		}
		finally
		{
			Directory.Delete( dir, recursive: true );
		}
	}

	/// <summary>A 2x2x2 box with a sculpt feature on it, rebuilt once.</summary>
	static (PartStudio Studio, PrimitiveFeature Box, SculptFeature Sculpt) SculptStudio()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 2f;
		box.SizeY.Value = 2f;
		box.SizeZ.Value = 2f;

		var sculpt = studio.Add( new SculptFeature() );
		studio.Rebuild();

		return (studio, box, sculpt);
	}

	static bool Throws( Action action, out string message )
	{
		message = null;

		try
		{
			action();
			return false;
		}
		catch ( Exception e )
		{
			message = e.Message;
			return true;
		}
	}

	/// <summary>A sculpt with `levels` levels above the cage, nothing sculpted yet.</summary>
	static MultiresSculpt Levels( PolyMesh cage, int levels )
	{
		var m = new MultiresSculpt( cage );

		for ( var i = 0; i < levels; i++ )
			m.AddLevel();

		return m;
	}

	/// <summary>Push one vertex out along its own normal and record it, as a brush stroke would.</summary>
	static int Bump( MultiresSculpt m, int level, Vec3 towards, float height )
	{
		var mesh = m.Evaluate( level );
		var frames = m.FramesFor( level );
		var vi = FindMostAligned( mesh, frames, towards );

		mesh.Positions[vi] += frames.At[vi].Normal * height;
		m.Record( level, mesh );

		return vi;
	}

	/// <summary>The level's rest surface with a layer applied — what Record wants handed back.</summary>
	static PolyMesh ApplyLayer( MultiresSculpt m, int level, SculptLayer layer )
	{
		var mesh = m.Rest( level );
		layer.Apply( mesh, m.FramesFor( level ) );
		return mesh;
	}

	static bool SamePositions( PolyMesh a, PolyMesh b, float eps = 1e-4f )
	{
		if ( a.VertexCount != b.VertexCount )
			return false;

		for ( var i = 0; i < a.VertexCount; i++ )
		{
			if ( !a.Positions[i].AlmostEquals( b.Positions[i], eps ) )
				return false;
		}

		return true;
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
