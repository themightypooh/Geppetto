using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The conformal flatten and the organic seam rule, judged on the three kinds of surface they were
/// built for: a quad-sphere (closed, convex, nowhere flat), a Catmull-Clark skin body (closed, with
/// limbs - tubes that have to be opened), and a SurfaceNets blob (closed, dense, irregular, made of
/// several lobes blended into each other).
///
/// Each is unwrapped three ways - the old planar flatten, the angle rule with LSCM, and the organic
/// rule - and every result must bake (NormalBake.Measure, no texel claimed twice). The conformal
/// ones must then be clearly LESS STRETCHED than the planar one; that is the whole reason they
/// exist, so it is asserted with the numbers rather than assumed.
///
/// Numbers measured when this was written (mean / max stretch, charts), for the next person who
/// changes the unwrap to compare against:
///
///   quad-sphere   planar 1.36 / 3.55,   4 charts | angle+LSCM 1.10 / 1.27,   4 | organic 1.24 / 1.78,  2
///   skin body     planar 1.29 / 116,  118 charts | angle+LSCM 1.04 / 2.56, 195 | organic 1.20 / 4.70, 44
///   SDF blob      planar 1.59 / 544,    6 charts | angle+LSCM 1.09 / 1.33,   6 | organic 1.28 / 1.77,  2
///
/// Organic trades a little of LSCM's stretch for far fewer islands - which is the point of it: a
/// seam is a line a texture artist has to paint across, and the planar maximums are faces turned
/// nearly edge-on to their chart, squashed to almost nothing.
/// </summary>
public static class OrganicUnwrapTests
{
	public static void Run()
	{
		Section( "unwrap: conformal flatten and organic seams" );
		TestAFlatChartIsLaidOutExactly();
		TestOrganicShapes();
		TestATubeIsOpenedAlongItsLength();
		TestASlitSeamOpensInsideAChart();
		TestOrganicIsDeterministic();
		TestTheFeatureOffersOrganic();
	}

	static void TestAFlatChartIsLaidOutExactly()
	{
		// LSCM on a flat chart is an isometry: a plane must come out with every face at its true
		// shape, so stretch is 1 to rounding. If this drifts, the coefficients are wrong.
		var plane = Primitives.Plane( 2, 1, 8, 4 );
		var report = UVUnwrap.Unwrap( plane );

		Check( "a flat plane unwraps as one chart", report.Charts == 1, report.ToString() );
		Check( "with no stretch at all", report.MeanStretch < 1.001f && report.MaxStretch < 1.001f, report.ToString() );
	}

	static (string Name, Func<PolyMesh> Make)[] Shapes() => new (string, Func<PolyMesh>)[]
	{
		("quad-sphere", () => Primitives.QuadSphere( 0.5f, 6 )),
		("skin body", () => SkinBlockout.Build( CitizenSkeleton.Build() )),
		("SDF blob", () => Blob()),
	};

	/// <summary>Two blended spheres, one smaller and offset: the shape of a lung lobe.</summary>
	static PolyMesh Lobe()
	{
		var field = new SdfBlend( 0.4f,
			new SdfSphere( new Vec3( 0, 0, 0 ), 1f ),
			new SdfSphere( new Vec3( 0.3f, 0.2f, 1.2f ), 0.7f ) );

		return SurfaceNets.Mesh( field, 0.08f ).Mesh;
	}

	/// <summary>Three blended lobes - a heart, a lump of anything grown from spheres.</summary>
	static PolyMesh Blob()
	{
		var field = new SdfBlend( 0.25f,
			new SdfSphere( new Vec3( 0, 0, 0 ), 1f ),
			new SdfSphere( new Vec3( 1.1f, 0.2f, 0.3f ), 0.7f ),
			new SdfSphere( new Vec3( -0.4f, 0.9f, -0.5f ), 0.6f ) );

		return SurfaceNets.Mesh( field, 0.12f ).Mesh;
	}

	static void TestOrganicShapes()
	{
		foreach ( var (name, make) in Shapes() )
		{
			var planarMesh = make();
			var planar = UVUnwrap.Unwrap( planarMesh, flatten: UnwrapFlatten.Planar );

			var angleMesh = make();
			var angle = UVUnwrap.Unwrap( angleMesh );

			var organicMesh = make();
			var organic = UVUnwrap.Unwrap( organicMesh, mode: UnwrapMode.Organic );

			var angleCoverage = NormalBake.Measure( angleMesh );
			var organicCoverage = NormalBake.Measure( organicMesh );

			Console.WriteLine( $"    {name}: planar {planar}\n      angle {angle}\n      organic {organic}" );

			Check( $"{name}: the angle unwrap with LSCM bakes", angleCoverage.CanBake, angleCoverage.Problem );
			Check( $"{name}: the organic unwrap bakes", organicCoverage.CanBake, organicCoverage.Problem );
			Check( $"{name}: nothing claimed twice", organicCoverage.OverlappingTexels == 0 && angleCoverage.OverlappingTexels == 0,
				$"{angleCoverage.OverlappingTexels} / {organicCoverage.OverlappingTexels}" );
			Check( $"{name}: no face skipped", organic.SkippedFaces == 0 && angle.SkippedFaces == 0, organic.ToString() );

			// CLEARLY lower, not just lower: a fifth of the way from the planar number down to a
			// perfect 1 at the very least.
			Check( $"{name}: LSCM stretches less than planar on the same charts",
				angle.MeanStretch - 1f < (planar.MeanStretch - 1f) * 0.8f,
				$"{angle.MeanStretch:0.00} vs planar {planar.MeanStretch:0.00}" );
			Check( $"{name}: organic stretches less than planar",
				organic.MeanStretch - 1f < (planar.MeanStretch - 1f) * 0.8f,
				$"{organic.MeanStretch:0.00} vs planar {planar.MeanStretch:0.00}" );
			Check( $"{name}: organic cuts fewer islands than the angle rule",
				organic.Charts < angle.Charts, $"{organic.Charts} vs {angle.Charts}" );
			Check( $"{name}: and still covers a usable share of the square", organicCoverage.CoveredFraction > 0.3f,
				$"{organicCoverage.CoveredFraction:P0}" );
		}

		// The headline the mode exists for: a blob is one to three islands, not dozens.
		var sphere = Primitives.QuadSphere( 0.5f, 6 );
		var sphereReport = UVUnwrap.Unwrap( sphere, mode: UnwrapMode.Organic );
		Check( "a sphere is one to three islands", sphereReport.Charts is >= 1 and <= 3, sphereReport.ToString() );

		var blob = Blob();
		var blobReport = UVUnwrap.Unwrap( blob, mode: UnwrapMode.Organic );
		Check( "a three-lobed SDF blob is one to three islands", blobReport.Charts is >= 1 and <= 3, blobReport.ToString() );

		var lobe = Lobe();
		var lobeReport = UVUnwrap.Unwrap( lobe, mode: UnwrapMode.Organic );
		Check( "a lung lobe is one to three islands", lobeReport.Charts is >= 1 and <= 3, lobeReport.ToString() );

		// A whole body is not a blob - it has a head, hands and feet sticking out of it - but the
		// rule still has to beat the angle rule by a long way, or it is not cutting "few, long seams".
		var body = SkinBlockout.Build( CitizenSkeleton.Build() );
		var bodyReport = UVUnwrap.Unwrap( body, mode: UnwrapMode.Organic );
		Check( "a skin body is a few dozen islands at most", bodyReport.Charts <= 50, bodyReport.ToString() );
	}

	static void TestATubeIsOpenedAlongItsLength()
	{
		// An open-ended tube laid out uncut is an annulus, which squeezes one end exponentially. The
		// organic rule must cut it lengthwise instead: one island, little stretch.
		var tube = Primitives.Cylinder( 0.3f, 2f, 24 );
		var walls = new HashSet<int>();

		for ( var f = 0; f < tube.FaceCount; f++ )
		{
			if ( MathF.Abs( tube.FaceNormal( tube.Faces[f] ).z ) < 0.5f )
				walls.Add( f );
		}

		// Keep only the wall, so it is a tube with two open ends.
		var open = new PolyMesh { Positions = tube.Positions.ToList() };

		foreach ( var f in walls )
			open.Faces.Add( tube.Faces[f] );

		var report = UVUnwrap.Unwrap( open, mode: UnwrapMode.Organic );
		var coverage = NormalBake.Measure( open );

		Check( "an open tube unrolls into one island", report.Charts == 1, report.ToString() );
		Check( "with almost no stretch", report.MeanStretch < 1.05f, report.ToString() );
		Check( "and bakes", coverage.CanBake, coverage.Problem );
	}

	static void TestASlitSeamOpensInsideAChart()
	{
		// A seam that does not separate a chart still has to open: the UVs are per corner, so the
		// two sides of a slit must be allowed different UVs. Cut a plane halfway across its middle
		// and the faces on either side of the slit must disagree about the UVs along it.
		var plane = Primitives.Plane( 2, 2, 4, 4 );
		var seams = new HashSet<EdgeKey>();
		var edges = plane.BuildEdgeFaces();

		foreach ( var (key, faces) in edges )
		{
			var a = plane.Positions[key.A];
			var b = plane.Positions[key.B];

			// Interior edges on the x = 0 line, from the middle to one edge.
			if ( faces.Count == 2 && MathF.Abs( a.x ) < 1e-4f && MathF.Abs( b.x ) < 1e-4f && MathF.Min( a.y, b.y ) >= -1e-4f )
				seams.Add( key );
		}

		var report = UVUnwrap.Unwrap( plane, seams: seams );
		var coverage = NormalBake.Measure( plane );
		var islands = UVIslands.Find( plane );

		Check( "the slit plane is still one island", islands.Count == 1 && report.Charts == 1, report.ToString() );
		Check( "and still bakes", coverage.CanBake, coverage.Problem );
		Check( "the slit was found", seams.Count >= 1, $"{seams.Count}" );
	}

	static void TestOrganicIsDeterministic()
	{
		var a = Blob();
		var b = Blob();

		UVUnwrap.Unwrap( a, mode: UnwrapMode.Organic );
		UVUnwrap.Unwrap( b, mode: UnwrapMode.Organic );

		var same = a.FaceCount == b.FaceCount;

		for ( var f = 0; same && f < a.FaceCount; f++ )
		{
			for ( var c = 0; same && c < a.Faces[f].UVs.Length; c++ )
				same = MathF.Abs( a.Faces[f].UVs[c].x - b.Faces[f].UVs[c].x ) < 1e-6f
					&& MathF.Abs( a.Faces[f].UVs[c].y - b.Faces[f].UVs[c].y ) < 1e-6f;
		}

		Check( "an organic unwrap of the same mesh is the same twice", same );
	}

	static void TestTheFeatureOffersOrganic()
	{
		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature() );

		var project = studio.Add( new UVProjectFeature() );
		project.Mode.Index = Array.IndexOf( project.Mode.Options, "Organic" );
		studio.Rebuild();

		Check( "the UV project feature has an Organic mode", project.Mode.Value == "Organic", project.Mode.Value );
		Check( "it runs", project.Error is null, project.Error );
		Check( "and reports its stretch", project.LastUnwrap is { MeanStretch: > 0f }, project.LastUnwrap?.ToString() );
		Check( "and its coverage bakes", project.LastCoverage?.CanBake == true, project.LastCoverage?.Problem );
		Check( "and hides the chart angle it ignores", !project.Parameters.Contains( project.ChartAngle ) );
	}
}
