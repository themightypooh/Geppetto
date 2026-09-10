using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Checks for the one operation in the kernel that DESTROYS information on purpose, which makes it
/// the hardest one to eyeball. A decimation that is subtly wrong still returns a smaller mesh that
/// still looks like the model — the damage is a hole somewhere on the back, a handful of inverted
/// triangles, or a manifold quietly turned non-manifold, and none of those show up in a thumbnail.
/// So almost everything here is a topology or a volume check rather than a look at the result.
/// </summary>
public static class DecimateTests
{
	public static void Run()
	{
		Section( "decimate: the target is met and the mesh survives it" );
		TestSphereHitsItsTarget();
		TestSphereStaysClosedAndManifold();
		TestNothingTurnsInsideOut();
		TestShapeIsKept();
		TestPercentageAndCountAgree();
		TestFloorIsFourTriangles();

		Section( "decimate: what it refuses to lose" );
		TestOpenMeshKeepsItsBorder();
		TestMaterialSeamSurvives();
		TestUnweldedMeshStillReduces();
		TestWeldOffLeavesAnUnweldedMeshAlone();

		Section( "decimate: what rides along" );
		TestVertexColoursSurvive();
		TestSkinWeightsSurviveAndStillSumToOne();

		Section( "decimate: the feature" );
		TestFeatureReducesTheBody();
		TestFeatureWarnsAboutQuads();
		TestFeatureCostPrediction();
	}

	static PolyMesh Sphere() => Primitives.QuadSphere( 1f, 16 );

	// --- the target ---------------------------------------------------------------------------

	static void TestSphereHitsItsTarget()
	{
		var sphere = Sphere();
		var before = Decimate.TriangleCount( sphere );
		var result = Decimate.Run( sphere, new Decimate.Options { TargetTriangles = 400 } );

		Check( "a quadsphere is dense enough to be worth reducing", before > 1500, $"{before}" );
		Check( "it reaches the target", result.ReachedTarget, $"{result.ToTriangles} of 400" );
		Check( "and stops at it rather than past it", result.ToTriangles <= 400 && result.ToTriangles > 380,
			$"{result.ToTriangles}" );
		Check( "the source count is reported", result.FromTriangles == before,
			$"{result.FromTriangles} vs {before}" );
	}

	static void TestSphereStaysClosedAndManifold()
	{
		var reduced = Decimate.ToTriangles( Sphere(), 300 );
		var validation = MeshValidator.Validate( reduced );

		Check( "the reduced sphere is valid", validation.IsValid, validation.ToString() );
		Check( "and still closed", validation.IsClosed, validation.ToString() );
		Check( "and still a sphere topologically", MeshValidator.EulerCharacteristic( reduced ) == 2,
			$"X = {MeshValidator.EulerCharacteristic( reduced )}" );
	}

	static void TestNothingTurnsInsideOut()
	{
		// Signed volume is the check that sees an inverted mesh, and a per-face pass is the one that
		// sees a handful of inverted faces in a mesh whose total is still positive.
		var reduced = Decimate.ToTriangles( Sphere(), 250 );
		var centre = Vec3.Zero;
		var inward = 0;

		foreach ( var f in reduced.Faces )
		{
			if ( Vec3.Dot( reduced.FaceNormal( f ), reduced.FaceCentroid( f ) - centre ) < 0 )
				inward++;
		}

		Check( "the volume stayed positive", reduced.SignedVolume() > 0, $"{reduced.SignedVolume():0.####}" );
		Check( "no face points into the solid", inward == 0, $"{inward} inverted" );
	}

	static void TestShapeIsKept()
	{
		var sphere = Sphere();
		var reduced = Decimate.ToTriangles( sphere, 400 );

		// A unit-radius sphere at 400 triangles is a visibly faceted thing, so this is not asking
		// for accuracy — it is asking that the surface did not collapse toward the middle, which is
		// what a decimation with the error term wrong does.
		var far = 0;

		foreach ( var p in reduced.Positions )
		{
			if ( MathF.Abs( p.Length - 1f ) > 0.06f )
				far++;
		}

		Check( "every vertex is still on the sphere", far == 0, $"{far} off it" );

		var ratio = reduced.SignedVolume() / sphere.SignedVolume();
		Check( "the volume is within 5%", MathF.Abs( ratio - 1f ) < 0.05f, $"{ratio:0.###}" );
	}

	static void TestPercentageAndCountAgree()
	{
		var sphere = Sphere();
		var count = Decimate.TriangleCount( sphere );

		var byRatio = Decimate.ToRatio( sphere, 0.25f );
		var byCount = Decimate.ToTriangles( sphere, (int)MathF.Round( count * 0.25f ) );

		Check( "a quarter by ratio and a quarter by count are the same size",
			byRatio.FaceCount == byCount.FaceCount, $"{byRatio.FaceCount} vs {byCount.FaceCount}" );
	}

	static void TestFloorIsFourTriangles()
	{
		// A target of zero means "as small as you can", not "nothing". Four is the smallest closed
		// surface there is, and asking for less than a tetrahedron is asking for a mesh that cannot
		// exist.
		var result = Decimate.Run( Sphere(), new Decimate.Options { TargetTriangles = 1 } );

		Check( "it never goes below four triangles", result.ToTriangles >= 4, $"{result.ToTriangles}" );
		Check( "and what is left is still valid", MeshValidator.Validate( result.Mesh ).IsValid );
	}

	// --- what it protects ----------------------------------------------------------------------

	static void TestOpenMeshKeepsItsBorder()
	{
		// A flat grid: every interior vertex is redundant and every border vertex is the border.
		// A decimation with no boundary constraint eats the corners first, because a corner is the
		// cheapest vertex on a plane — its quadric only knows about one plane, and it is on it.
		var grid = Primitives.Plane( 2f, 2f, 10, 10 );
		var reduced = Decimate.Run( grid, new Decimate.Options { TargetTriangles = 8 } ).Mesh;

		var corners = 0;

		foreach ( var p in reduced.Positions )
		{
			if ( MathF.Abs( MathF.Abs( p.x ) - 1f ) < 1e-3f && MathF.Abs( MathF.Abs( p.y ) - 1f ) < 1e-3f )
				corners++;
		}

		Check( "all four corners of the grid are still there", corners == 4, $"{corners}" );

		var off = 0;

		foreach ( var p in reduced.Positions )
		{
			var onEdge = MathF.Abs( MathF.Abs( p.x ) - 1f ) < 1e-3f || MathF.Abs( MathF.Abs( p.y ) - 1f ) < 1e-3f;
			var inside = MathF.Abs( p.x ) < 1f - 1e-3f && MathF.Abs( p.y ) < 1f - 1e-3f;

			if ( !onEdge && !inside )
				off++;
		}

		Check( "nothing wandered outside the original square", off == 0, $"{off} outside" );

		var loose = Decimate.Run( grid, new Decimate.Options
		{
			TargetTriangles = 8,
			PreserveBoundary = false,
		} ).Mesh;

		Check( "and the protection is what did it — off, the border moves",
			loose.Positions.Count( p => MathF.Abs( MathF.Abs( p.x ) - 1f ) < 1e-3f
				&& MathF.Abs( MathF.Abs( p.y ) - 1f ) < 1e-3f ) < 4 );
	}

	static void TestMaterialSeamSurvives()
	{
		// Two materials on one closed surface. The line between them is not geometry — nothing in
		// the positions marks it — so only the seam constraint keeps it where it was.
		var sphere = Sphere();

		foreach ( var f in sphere.Faces )
			f.Material = sphere.FaceCentroid( f ).z > 0 ? 1 : 0;

		var reduced = Decimate.ToTriangles( sphere, 300 );

		var top = reduced.Faces.Count( f => f.Material == 1 );
		var bottom = reduced.Faces.Count( f => f.Material == 0 );

		Check( "both materials are still on the part", top > 0 && bottom > 0, $"{top} / {bottom}" );

		// Every vertex shared by a face of each material should still be near the equator, which is
		// where the seam was.
		var byVertex = new Dictionary<int, HashSet<int>>();

		foreach ( var f in reduced.Faces )
		{
			foreach ( var i in f.Indices )
			{
				if ( !byVertex.TryGetValue( i, out var set ) )
					byVertex[i] = set = new HashSet<int>();

				set.Add( f.Material );
			}
		}

		var strayed = byVertex.Count( kv => kv.Value.Count > 1
			&& MathF.Abs( reduced.Positions[kv.Key].z ) > 0.25f );

		Check( "the seam stayed on the equator", strayed == 0, $"{strayed} strayed" );
	}

	static void TestUnweldedMeshStillReduces()
	{
		var soup = Explode( Sphere() );
		var before = Decimate.TriangleCount( soup );
		var result = Decimate.Run( soup, new Decimate.Options { TargetTriangles = 300 } );

		Check( "the exploded mesh really is unwelded",
			soup.VertexCount == before * 3, $"{soup.VertexCount} for {before} triangles" );
		Check( "welding first lets it reduce", result.ToTriangles <= 300, $"{result.ToTriangles}" );
		Check( "and it reports what it merged", result.Welded > 0, $"{result.Welded}" );
		Check( "the welded result is closed", MeshValidator.Validate( result.Mesh ).IsClosed );
	}

	static void TestWeldOffLeavesAnUnweldedMeshAlone()
	{
		// Not a bug being pinned as behaviour — it is the reason Weld defaults to on. Every edge of
		// a triangle soup is a border, so with borders held there is nothing legal to collapse.
		var soup = Explode( Sphere() );
		var result = Decimate.Run( soup, new Decimate.Options { TargetTriangles = 300, Weld = false } );

		Check( "without welding, a soup cannot be reduced",
			result.ToTriangles == result.FromTriangles, $"{result.ToTriangles}" );
		Check( "and it says so rather than claiming success", !result.ReachedTarget );
	}

	/// <summary>Every triangle given its own three vertices — what plenty of exporters write.</summary>
	static PolyMesh Explode( PolyMesh mesh )
	{
		var soup = new PolyMesh();

		foreach ( var f in mesh.Faces )
		{
			var corners = f.Indices.Select( i => mesh.Positions[i] ).ToList();

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				var a = soup.AddVertex( corners[ia] );
				var b = soup.AddVertex( corners[ib] );
				var c = soup.AddVertex( corners[ic] );

				soup.AddFace( new[] { a, b, c }, null, f.Material );
			}
		}

		return soup;
	}

	// --- what rides along ------------------------------------------------------------------------

	static void TestVertexColoursSurvive()
	{
		var sphere = Sphere();
		var colors = new Vec4[sphere.VertexCount];

		for ( var i = 0; i < colors.Length; i++ )
			colors[i] = new Vec4( sphere.Positions[i].z > 0 ? 1f : 0f, 0f, 0f, 1f );

		sphere.VertexColors = colors;

		var reduced = Decimate.ToTriangles( sphere, 300 );

		Check( "colour comes through", reduced.HasVertexColors );
		Check( "one per vertex", reduced.VertexColors.Length == reduced.VertexCount,
			$"{reduced.VertexColors.Length} for {reduced.VertexCount}" );

		// The blend is along the edge, so a vertex well inside a region should still be that
		// region's colour rather than a smear of both.
		var wrong = 0;

		for ( var i = 0; i < reduced.VertexCount; i++ )
		{
			var p = reduced.Positions[i];

			if ( p.z > 0.5f && reduced.VertexColors[i].x < 0.5f ) wrong++;
			if ( p.z < -0.5f && reduced.VertexColors[i].x > 0.5f ) wrong++;
		}

		Check( "and it did not smear across the model", wrong == 0, $"{wrong} wrong" );
	}

	static void TestSkinWeightsSurviveAndStillSumToOne()
	{
		var sphere = Sphere();
		var skin = new SkinWeights();

		foreach ( var p in sphere.Positions )
		{
			var t = Math.Clamp( p.z + 0.5f, 0f, 1f );
			skin.Vertices.Add( new[] { new BoneWeight( 0, 1f - t ), new BoneWeight( 1, t ) } );
		}

		sphere.Skin = skin;

		var reduced = Decimate.ToTriangles( sphere, 300 );

		Check( "the rig comes through", reduced.IsRigged );

		var offBy = 0;

		foreach ( var weights in reduced.Skin.Vertices )
		{
			if ( MathF.Abs( weights.Sum( w => w.Weight ) - 1f ) > 1e-3f )
				offBy++;
		}

		Check( "every vertex is still a partition of unity", offBy == 0, $"{offBy} off" );
	}

	// --- the feature -----------------------------------------------------------------------------

	static void TestFeatureReducesTheBody()
	{
		var studio = new PartStudio();

		var sphere = studio.Add( new PrimitiveFeature() );
		sphere.Name = "Blob";
		sphere.Shape.Index = 2;
		sphere.SizeX.Value = 2f;
		sphere.SizeY.Value = 2f;
		sphere.SizeZ.Value = 2f;

		var subdivide = studio.Add( new SubdivideFeature() );
		subdivide.Name = "Density";
		subdivide.Levels.Value = 3;

		var report = studio.Rebuild();
		Check( "the dense build is clean", !report.HasErrors, report.ToString() );

		var dense = Decimate.TriangleCount( studio.ToMesh() );

		var remesh = studio.Add( new RemeshFeature() );
		remesh.Name = "Budget";
		remesh.Target.Index = 1;
		remesh.Triangles.Value = 500;

		report = studio.Rebuild();
		Check( "the remesh rebuild is clean", !report.HasErrors, report.ToString() );

		var reduced = Decimate.TriangleCount( studio.ToMesh() );

		Check( "the feature reduced the part", reduced < dense, $"{dense} -> {reduced}" );
		Check( "to the budget it was given", reduced <= 500, $"{reduced}" );
		Check( "and the result is still closed", MeshValidator.Validate( studio.ToMesh() ).IsClosed );

		// The whole reason it is a feature: roll it back and the dense mesh is still there to
		// re-target. Suppressing is the cheapest way to ask that question.
		remesh.Suppressed = true;
		studio.MarkDirty( 0 );
		studio.Rebuild();

		Check( "suppressing it gives the dense mesh back",
			Decimate.TriangleCount( studio.ToMesh() ) == dense,
			$"{Decimate.TriangleCount( studio.ToMesh() )} vs {dense}" );
	}

	static void TestFeatureWarnsAboutQuads()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.Name = "Slab";
		box.Shape.Index = 0;

		var subdivide = studio.Add( new SubdivideFeature() );
		subdivide.Name = "Density";
		subdivide.Levels.Value = 3;
		subdivide.AllFaces.Value = true;

		var remesh = studio.Add( new RemeshFeature() );
		remesh.Name = "Budget";
		remesh.Percent.Value = 20f;

		studio.Rebuild();

		Check( "a remesh over a quad cage warns", remesh.Warning is not null, remesh.Warning ?? "silent" );
		Check( "and it is a warning, not a failure", remesh.Error is null, remesh.Error );
	}

	static void TestFeatureCostPrediction()
	{
		var body = new Body( "b0", "Blob", Sphere() );
		var bodies = new List<Body> { body };

		var remesh = new RemeshFeature();
		remesh.Percent.Value = 10f;

		var (from, to) = remesh.PredictCost( bodies );

		Check( "the prediction knows what came in",
			from == Decimate.TriangleCount( body.Mesh ), $"{from}" );
		Check( "and what a tenth of it is", to == (int)MathF.Round( from * 0.1f ), $"{to} of {from}" );

		remesh.Target.Index = 1;
		remesh.Triangles.Value = 128;

		(from, to) = remesh.PredictCost( bodies );
		Check( "a count target predicts the count", to == 128, $"{to}" );
	}
}
