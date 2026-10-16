using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Quad remeshing, held to the numbers a pro organic asset was measured at.
///
/// The study of six shipped organic models that prompted this (effigy-gaps.md) found the quad
/// ones at 99-100% quads, 1-5% poles and an edge-length spread (cv) of 0.2-0.5. Those are the bar
/// here, on the two inputs the tool actually gets: a dense SurfaceNets blob, which is what the
/// Blob feature and every SDF creature produce, and a decimated triangle sphere, which is what an
/// import looks like after a Remesh in Decimate mode. Each also has to stay on its source — the
/// Hausdorff distance both ways under 1% of the bounding diagonal — because a quad mesh that is
/// tidy and the wrong shape is no use.
/// </summary>
public static class QuadRemeshTests
{
	public static void Run()
	{
		Section( "quad remesh: a dense SDF blob" );
		TestBlob();

		Section( "quad remesh: a decimated triangle sphere" );
		TestDecimatedSphere();

		Section( "quad remesh: symmetry and repeatability" );
		TestSymmetry();
		TestSameEveryRun();

		Section( "quad remesh: the feature" );
		TestFeatureMode();
	}

	/// <summary>Four primitives blended, so it has necks, a limb and a saddle — the places a
	/// field has to bend — and not just a sphere's worth of curvature.</summary>
	static Sdf Blob() => new SdfBlend( 3f,
		new SdfSphere( new Vec3( 0, 0, 10 ), 6 ),
		new SdfSphere( new Vec3( 6, 2, 14 ), 4 ),
		new SdfCapsule( new Vec3( -4, 0, 6 ), new Vec3( -10, 3, 2 ), 2.5f ),
		new SdfEllipsoid( new Vec3( 2, -3, 4 ), new Vec3( 5, 3, 3 ) ) );

	/// <summary>The same idea, mirrored in X: a body with two arms.</summary>
	static Sdf Symmetric() => new SdfBlend( 3f,
		new SdfSphere( new Vec3( 0, 0, 10 ), 6 ),
		new SdfCapsule( new Vec3( 4, 0, 8 ), new Vec3( 11, 2, 3 ), 2.2f ),
		new SdfCapsule( new Vec3( -4, 0, 8 ), new Vec3( -11, 2, 3 ), 2.2f ),
		new SdfEllipsoid( new Vec3( 0, -2, 3 ), new Vec3( 4, 3, 4 ) ) );

	static void TestBlob()
	{
		// About 50k quads, 100k triangles — the size the speed bar is set at.
		var source = SurfaceNets.Mesh( Blob(), 0.155f ).Mesh;
		var clock = Stopwatch.StartNew();
		var result = QuadRemesh.Run( source, new QuadRemesh.Options { TargetFaces = 4000 } );
		clock.Stop();

		Check( "the blob is a dense input", source.FaceCount > 45_000, $"{source.FaceCount} faces" );
		CheckQuality( "blob", source, result, 4000 );
		Check( "it runs in under 10 s on a 50k-face input", clock.Elapsed.TotalSeconds < 10,
			$"{clock.Elapsed.TotalSeconds:0.00} s for {source.FaceCount} faces" );
	}

	static void TestDecimatedSphere()
	{
		var source = Decimate.ToTriangles( Primitives.QuadSphere( 1f, 24 ), 3000 );
		var result = QuadRemesh.Run( source, new QuadRemesh.Options { TargetFaces = 2000 } );

		Check( "the source is triangles", source.Faces.All( f => f.Count == 3 ), $"{source.FaceCount} faces" );
		CheckQuality( "sphere", source, result, 2000 );
	}

	static void CheckQuality( string name, PolyMesh source, QuadRemesh.Result result, int target )
	{
		var stats = result.Stats;
		var mesh = result.Mesh;
		var validation = MeshValidator.Validate( mesh );
		var diagonal = Diagonal( source );
		var hausdorff = MathF.Max( OneWay( mesh, source ), OneWay( source, mesh ) );

		Check( $"{name}: at least 95% quads", stats.QuadFraction >= 0.95f, stats.ToString() );
		Check( $"{name}: under 5% poles", stats.PoleFraction < 0.05f, stats.ToString() );
		Check( $"{name}: edge-length cv under 0.4", stats.EdgeCv < 0.4f, stats.ToString() );
		Check( $"{name}: Hausdorff distance under 1% of the diagonal", hausdorff < diagonal * 0.01f,
			$"{hausdorff / diagonal:P2} of {diagonal:0.##}" );
		Check( $"{name}: within 20% of the {target}-face target", Math.Abs( stats.Faces - target ) < target / 5, result.ToString() );
		Check( $"{name}: valid and closed", validation.IsValid && validation.IsClosed, validation.ToString() );
		Check( $"{name}: still genus 0", MeshValidator.EulerCharacteristic( mesh ) == 2,
			$"X = {MeshValidator.EulerCharacteristic( mesh )}" );
		Check( $"{name}: wound outward", mesh.SignedVolume() > 0, $"{mesh.SignedVolume():0.###}" );

		Console.WriteLine( $"    {name}: {result}, Hausdorff {hausdorff / diagonal:P2}" );
	}

	static void TestSymmetry()
	{
		var source = SurfaceNets.Mesh( Symmetric(), 0.22f ).Mesh;
		var result = QuadRemesh.Run( source, new QuadRemesh.Options { TargetFaces = 4000, SymmetryX = true } );
		var mesh = result.Mesh;
		var tolerance = Diagonal( source ) * 1e-4f;

		// Every vertex's reflection is a vertex, found through a grid of the positions.
		var grid = new HashSet<(int, int, int)>( mesh.Positions.Select( p => Cell( p, tolerance ) ) );
		var unmatched = mesh.Positions.Count( p => !Near( grid, new Vec3( -p.x, p.y, p.z ), tolerance ) );

		CheckQuality( "mirrored", source, result, 4000 );
		Check( "every vertex has a mirror twin", unmatched == 0, $"{unmatched} of {mesh.VertexCount} unmatched" );
	}

	static (int, int, int) Cell( Vec3 p, float size ) =>
		((int)MathF.Floor( p.x / size ), (int)MathF.Floor( p.y / size ), (int)MathF.Floor( p.z / size ));

	static bool Near( HashSet<(int, int, int)> grid, Vec3 p, float size )
	{
		var (x, y, z) = Cell( p, size );

		for ( var dx = -1; dx <= 1; dx++ )
			for ( var dy = -1; dy <= 1; dy++ )
				for ( var dz = -1; dz <= 1; dz++ )
					if ( grid.Contains( (x + dx, y + dy, z + dz) ) )
						return true;

		return false;
	}

	/// <summary>A feature rebuilds every time anything above it changes, so the same input has to
	/// give the same mesh, or the part shimmers between rebuilds and every saved selection on it
	/// points at the wrong faces.</summary>
	static void TestSameEveryRun()
	{
		var source = Decimate.ToTriangles( Primitives.QuadSphere( 1f, 24 ), 3000 );
		var a = QuadRemesh.Run( source, new QuadRemesh.Options { TargetFaces = 1500 } ).Mesh;
		var b = QuadRemesh.Run( source, new QuadRemesh.Options { TargetFaces = 1500 } ).Mesh;

		var same = a.VertexCount == b.VertexCount && a.FaceCount == b.FaceCount
			&& a.Positions.Zip( b.Positions ).All( p => p.First.Equals( p.Second ) )
			&& a.Faces.Zip( b.Faces ).All( f => f.First.Indices.SequenceEqual( f.Second.Indices ) );

		Check( "two runs give the identical mesh", same, $"{a.FaceCount} vs {b.FaceCount} faces" );
	}

	static void TestFeatureMode()
	{
		var studio = new PartStudio();

		var sphere = studio.Add( new PrimitiveFeature() );
		sphere.Name = "Ball";
		sphere.Shape.Index = 2;
		sphere.SizeX.Value = 2f;
		sphere.SizeY.Value = 2f;
		sphere.SizeZ.Value = 2f;

		var decimate = studio.Add( new RemeshFeature() );
		decimate.Name = "Reduce";
		decimate.Target.Index = 1;
		decimate.Triangles.Value = 2000;

		studio.Rebuild();
		var triangles = studio.ToMesh();

		var remesh = studio.Add( new RemeshFeature() );
		remesh.Name = "Retopo";
		remesh.Mode.Index = RemeshFeature.ModeQuad;
		remesh.Faces.Value = 1200;

		var report = studio.Rebuild();
		var mesh = studio.ToMesh();
		var stats = QuadRemesh.Measure( mesh );

		Check( "the quad remesh rebuild is clean", !report.HasErrors, report.ToString() );
		Check( "it went in as triangles", triangles.Faces.All( f => f.Count == 3 ), $"{triangles.FaceCount}" );
		Check( "and came out as quads", stats.QuadFraction >= 0.95f, stats.ToString() );
		Check( "near the face target", Math.Abs( stats.Faces - 1200 ) < 240, stats.ToString() );
		Check( "the dialog shows the quad controls in Quad mode",
			remesh.Parameters.Contains( remesh.Faces ) && !remesh.Parameters.Contains( remesh.Triangles ) );
		Check( "and the triangle ones in Decimate mode",
			decimate.Parameters.Contains( decimate.Triangles ) && !decimate.Parameters.Contains( decimate.Faces ) );

		var (from, to) = remesh.PredictCost( new[] { new Body( "b", "Ball", triangles ) } );
		Check( "the cost prediction counts quads in Quad mode", to == 1200 && from == triangles.FaceCount, $"{from} -> {to}" );

		remesh.Suppressed = true;
		studio.MarkDirty( 0 );
		studio.Rebuild();
		Check( "suppressing it gives the triangles back", studio.ToMesh().Faces.All( f => f.Count == 3 ) );
	}

	// --- measuring ---------------------------------------------------------------------------

	static float Diagonal( PolyMesh mesh )
	{
		var min = new Vec3( mesh.Positions.Min( p => p.x ), mesh.Positions.Min( p => p.y ), mesh.Positions.Min( p => p.z ) );
		var max = new Vec3( mesh.Positions.Max( p => p.x ), mesh.Positions.Max( p => p.y ), mesh.Positions.Max( p => p.z ) );
		return (max - min).Length;
	}

	static PolyMesh Triangles( PolyMesh mesh )
	{
		var result = new PolyMesh( mesh.Positions, Array.Empty<Face>() );

		foreach ( var f in mesh.Faces )
			for ( var i = 1; i + 1 < f.Count; i++ )
				result.AddFace( new[] { f.Indices[0], f.Indices[i], f.Indices[i + 1] } );

		return result;
	}

	/// <summary>The farthest any vertex or face centre of <paramref name="from"/> is from the
	/// surface of <paramref name="to"/>. Taken both ways it is the Hausdorff distance, sampled.</summary>
	static float OneWay( PolyMesh from, PolyMesh to )
	{
		var target = Triangles( to );
		var tree = MeshBVH.Build( target );
		var samples = from.Positions.Concat( from.Faces.Select( from.FaceCentroid ) ).ToArray();
		var worst = new float[samples.Length];

		System.Threading.Tasks.Parallel.For( 0, samples.Length, i =>
			worst[i] = tree.NearestSurface( target, samples[i], 1e6f ) is { } hit ? hit.Distance : float.MaxValue );

		return worst.Max();
	}
}
