using System.Linq;
using System.Collections.Generic;
using System;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The unwrapper, judged by the only thing that matters about it: can a bake use what it produced?
///
/// `NormalBake.Measure` is the acceptance test, not a proxy for one. Before this existed it
/// correctly refused every model the tool could make — box and planar projection both overlap by
/// construction — so the sculpt pipeline could not pay off on anything but a hand-UV'd plane.
/// Every check here that matters ends in CanBake.
/// </summary>
public static class UnwrapTests
{
	public static void Run()
	{
		Section( "unwrap: UVs a bake can actually use" );
		TestTheDefaultProjectionCannotBakeAndTheUnwrapCan();
		TestEveryPrimitiveUnwrapsIntoTheSquare();
		TestChartsFollowCurvatureRatherThanFaces();
		TestIslandsKeepTheirGutter();
		TestUnwrappingIsDeterministic();
		TestUniformDensityAcrossCharts();
		TestABakeThroughAnUnwrapIsCorrect();
		TestSeamsCutChartsTheAngleWouldHaveKeptWhole();
	}

	static void TestTheDefaultProjectionCannotBakeAndTheUnwrapCan()
	{
		// The whole reason this file exists, stated as one comparison.
		var projected = Primitives.Box( 2, 2, 2 );
		UVProjection.BoxProject( projected );

		var unwrapped = Primitives.Box( 2, 2, 2 );
		var report = UVUnwrap.Unwrap( unwrapped );

		var before = NormalBake.Measure( projected );
		var after = NormalBake.Measure( unwrapped );

		Check( "box projection cannot carry a bake", !before.CanBake, "it could" );
		Check( "the unwrap can", after.CanBake, after.Problem );
		Check( "with nothing claimed twice", after.OverlappingTexels == 0, $"{after.OverlappingTexels}" );
		Check( "and nothing outside the square", after.FacesOutsideTheSquare == 0,
			$"{after.FacesOutsideTheSquare} faces" );
		Check( "a box comes out as six charts, one per side", report.Charts == 6, report.ToString() );
	}

	static void TestEveryPrimitiveUnwrapsIntoTheSquare()
	{
		// A sweep rather than one shape: the charting has to survive caps, seams and curvature, and
		// a packer that only ever sees six flat squares is not a packer.
		var shapes = new (string Name, PolyMesh Mesh)[]
		{
			("box", Primitives.Box( 2, 2, 2 )),
			("cylinder", Primitives.Cylinder( 0.5f, 1f, 16 )),
			("quadsphere", Primitives.QuadSphere( 0.5f, 4 )),
			("wedge", Primitives.Wedge( 1, 1, 1 )),
			("tube", Primitives.Tube( 0.5f, 0.3f, 1f, 16 )),
			("plane", Primitives.Plane( 2, 2, 4, 4 )),
		};

		foreach ( var (name, mesh) in shapes )
		{
			var report = UVUnwrap.Unwrap( mesh );
			var coverage = NormalBake.Measure( mesh, 256 );

			Check( $"{name} unwraps to something bakeable", coverage.CanBake,
				coverage.Problem ?? "" );
			Check( $"{name} covers a usable share of the square", coverage.CoveredFraction > 0.2f,
				$"{coverage.CoveredFraction:P0} in {report.Charts} charts" );
			Check( $"{name} left no face behind", report.SkippedFaces == 0, report.ToString() );
		}
	}

	static void TestChartsFollowCurvatureRatherThanFaces()
	{
		// Comparing each face against the chart's RUNNING AVERAGE rather than against its seed is
		// what lets a cylinder wall go all the way round as one island. Against the seed it would cap
		// at the tolerance and come out as a fan of narrow strips - one seam becomes sixteen.
		var cylinder = Primitives.Cylinder( 0.5f, 1f, 16 );
		var report = UVUnwrap.Unwrap( cylinder );

		Check( "a 16-sided cylinder is a handful of charts, not sixteen", report.Charts <= 6,
			report.ToString() );

		// And the tolerance still has to SPLIT something: a box's sides must not merge into one
		// chart, or the unwrap is just a projection with extra steps.
		var box = Primitives.Box( 2, 2, 2 );
		var boxReport = UVUnwrap.Unwrap( box );

		Check( "but a box still splits at its corners", boxReport.Charts == 6, boxReport.ToString() );
	}

	static void TestIslandsKeepTheirGutter()
	{
		// The bake bleeds islands outward so seams do not glow under mipmapping. Without a gutter
		// that bleed runs into the neighbouring island and paints one surface's normals onto another.
		var tight = Primitives.Box( 2, 2, 2 );
		UVUnwrap.Unwrap( tight, margin: 0f );

		var spaced = Primitives.Box( 2, 2, 2 );
		UVUnwrap.Unwrap( spaced, margin: 0.05f );

		var tightCoverage = NormalBake.Measure( tight, 256 );
		var spacedCoverage = NormalBake.Measure( spaced, 256 );

		Check( "a margin costs coverage", spacedCoverage.CoveredFraction < tightCoverage.CoveredFraction,
			$"{tightCoverage.CoveredFraction:P0} became {spacedCoverage.CoveredFraction:P0}" );
		Check( "and both are still bakeable", tightCoverage.CanBake && spacedCoverage.CanBake );
	}

	static void TestUnwrappingIsDeterministic()
	{
		// A chart layout that shuffled between runs would move every texel in the map for no reason,
		// and make a re-bake a different file every time.
		var a = Primitives.QuadSphere( 0.5f, 4 );
		var b = Primitives.QuadSphere( 0.5f, 4 );

		UVUnwrap.Unwrap( a );
		UVUnwrap.Unwrap( b );

		var same = a.FaceCount == b.FaceCount;

		for ( var f = 0; same && f < a.FaceCount; f++ )
		{
			var x = a.Faces[f].UVs;
			var y = b.Faces[f].UVs;

			same = x.Length == y.Length;

			for ( var c = 0; same && c < x.Length; c++ )
				same = MathF.Abs( x[c].x - y[c].x ) < 1e-6f && MathF.Abs( x[c].y - y[c].y ) < 1e-6f;
		}

		Check( "unwrapping the same mesh twice gives the same UVs", same );
	}

	static void TestUniformDensityAcrossCharts()
	{
		// ONE SCALE FOR EVERY CHART. Fitting each chart to its own slot wastes no square and gives a
		// tiny bevel the same texel budget as a whole side, so the bake resolves the bevel
		// beautifully and the side not at all.
		var mesh = Primitives.Box( 4, 1, 1 );
		UVUnwrap.Unwrap( mesh, margin: 0f );

		// The 4x1 sides should be four times the UV area of the 1x1 ends, because they are four
		// times the surface.
		var big = 0f;
		var small = float.MaxValue;

		foreach ( var face in mesh.Faces )
		{
			var area = UvArea( face );

			big = MathF.Max( big, area );
			small = MathF.Min( small, area );
		}

		Check( "UV area follows world area rather than the packing", big / small > 3f && big / small < 5f,
			$"largest face is {big / small:0.##}x the smallest, expected about 4" );
	}

	static void TestABakeThroughAnUnwrapIsCorrect()
	{
		// End to end, and the point of the whole exercise: a sculpted solid, unwrapped, baked. Before
		// the unwrapper the only bakeable fixture in the suite was a hand-UV'd plane.
		//
		// A QUADSPHERE CAGE, NOT A BOX, and the reason is worth knowing. Catmull-Clark pulls a cube's
		// corners a very long way in - on a 2x2x2 box the cage and its own subdivision are 2.6 units
		// apart - so rays fired from the flat face towards its edges leave the sculpt entirely and
		// find nothing. A box cage therefore bakes only its middle, however large the search range.
		// That is a property of the cage, not a bug in the bake: a bake wants a cage that HUGS its
		// sculpt, and a coarse box does not. A quadsphere barely moves under subdivision, which is
		// what a sculpting cage should look like.
		var cage = Primitives.QuadSphere( 1f, 4 );
		var report = UVUnwrap.Unwrap( cage );

		var sculpt = new MultiresSculpt( cage );
		sculpt.AddLevel();
		sculpt.AddLevel();

		var mesh = sculpt.Evaluate( 2 );
		var moved = 0;

		// A dome pushed out along +Z, big enough to cover a good share of the top.
		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			var r = MathF.Sqrt( p.x * p.x + p.y * p.y );

			if ( p.z < 0f || r >= 0.7f )
				continue;

			var t = 1f - r / 0.7f;
			mesh.Positions[i] = p + p.Normal * (0.25f * t * t * (3f - 2f * t));
			moved++;
		}

		sculpt.Record( 2, mesh );

		Check( "the fixture got sculpted over a real area", moved > 30, $"{moved} vertices" );

		var coverage = NormalBake.Measure( cage );
		var map = NormalBake.Bake( cage, sculpt.Evaluate( 2 ), 256 );

		Check( "the unwrapped cage is bakeable", coverage.CanBake, coverage.Problem ?? "" );
		// 45% of the square, which is what shelf-packing five irregular charts costs. Recorded as a
		// number rather than a hope: it is the texture budget, and the two things that would raise it
		// are rotating a chart 90 degrees when it packs better on its side, and a real bin packer.
		Check( "and the bake filled the islands", map.FilledCount > 256 * 256 * 0.4f,
			$"{map.FilledCount} of {256 * 256} texels, {report.Charts} charts" );

		// The map has to carry the dome. Measured as the largest lean away from the cage's own
		// normal: a map that came out flat would pass both checks above, and flat is exactly what a
		// bake produces when its rays all miss.
		var tilt = 0f;
		var leaning = 0;

		for ( var y = 0; y < 256; y++ )
		{
			for ( var x = 0; x < 256; x++ )
			{
				if ( !map.Filled[y * 256 + x] )
					continue;

				var n = map.NormalAt( x, y );
				var lean = MathF.Sqrt( n.x * n.x + n.y * n.y );

				tilt = MathF.Max( tilt, lean );

				if ( lean > 0.15f )
					leaning++;
			}
		}

		Check( "the dome is in the map rather than the map being flat", tilt > 0.3f,
			$"largest lean {tilt:0.###}" );
		Check( "and it covers an area rather than a speck", leaning > 300,
			$"{leaning} texels lean past 0.15" );
	}

	static float UvArea( Face face )
	{
		var area = 0f;

		for ( var i = 1; i < face.UVs.Length - 1; i++ )
		{
			var a = face.UVs[0];
			var b = face.UVs[i];
			var c = face.UVs[i + 1];

			area += MathF.Abs( (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y) ) * 0.5f;
		}

		return area;
	}

	/// <summary>
	/// A marked seam has to beat the angle rule, or it is decoration. A flat grid is the case that
	/// proves it: every face is coplanar, so the angle rule makes exactly ONE chart and any second
	/// island can only have come from the seam.
	///
	/// The seam has to cut the whole way across. One edge in the middle of a grid changes nothing,
	/// because the flood fill simply walks around it — which is true of seams in any tool, and worth
	/// a check of its own.
	/// </summary>
	static void TestSeamsCutChartsTheAngleWouldHaveKeptWhole()
	{
		Section( "unwrap: a marked seam cuts where the angle would not" );

		// 4x4 quads, 8 inches across: coplanar, so charting alone gives one island.
		PolyMesh Grid() => Primitives.Plane( 8f, 8f, 4, 4 );

		var plain = Grid();
		var plainReport = UVUnwrap.Unwrap( plain );
		Check( "a flat grid is one island to start with", plainReport.Charts == 1, $"{plainReport.Charts}" );

		// Every edge lying on the y = 0 line: a straight cut from one side of the grid to the other.
		HashSet<EdgeKey> AcrossTheMiddle( PolyMesh m )
		{
			var seams = new HashSet<EdgeKey>();

			foreach ( var key in m.BuildEdgeFaces().Keys )
				if ( MathF.Abs( m.Positions[key.A].y ) < 1e-3f && MathF.Abs( m.Positions[key.B].y ) < 1e-3f )
					seams.Add( key );

			return seams;
		}

		var cut = Grid();
		var seams = AcrossTheMiddle( cut );
		Check( "the cut runs the full width", seams.Count == 4, $"{seams.Count} edges" );

		var cutReport = UVUnwrap.Unwrap( cut, seams: seams );
		Check( "the seam splits it in two", cutReport.Charts == 2, $"{plainReport.Charts} -> {cutReport.Charts}" );
		Check( "and no face was lost to it", cutReport.Faces == plainReport.Faces,
			$"{plainReport.Faces} vs {cutReport.Faces}" );
		Check( "no vertex moved - a seam is a UV cut, not a mesh cut",
			cut.VertexCount == plain.VertexCount && cut.FaceCount == plain.FaceCount );

		// A vertex on the cut now carries a different UV in each half. That IS the seam.
		var onSeam = seams.First().A;
		var uvs = new HashSet<Vec2>();
		foreach ( var face in cut.Faces )
			for ( var c = 0; c < face.Indices.Length; c++ )
				if ( face.Indices[c] == onSeam )
					uvs.Add( face.UVs[c] );
		Check( "a vertex on the seam carries more than one UV", uvs.Count > 1, $"{uvs.Count}" );

		// Same seam, same layout: the whole map must not shuffle between runs.
		var again = Grid();
		UVUnwrap.Unwrap( again, seams: AcrossTheMiddle( again ) );
		var same = true;
		for ( var f = 0; f < again.FaceCount && same; f++ )
			for ( var c = 0; c < again.Faces[f].UVs.Length; c++ )
				if ( !again.Faces[f].UVs[c].Equals( cut.Faces[f].UVs[c] ) ) { same = false; break; }
		Check( "seamed unwrapping is deterministic", same );

		// One edge in the middle is not a cut: the fill goes round it.
		var nicked = Grid();
		var nickReport = UVUnwrap.Unwrap( nicked, seams: new HashSet<EdgeKey> { seams.First() } );
		Check( "a seam that does not reach across changes nothing", nickReport.Charts == 1, $"{nickReport.Charts}" );

		// And a seam on an edge the angle already split is equally a no-op.
		var box = Primitives.Box( 2, 2, 2 );
		var boxPlain = UVUnwrap.Unwrap( box );
		var boxSeamed = Primitives.Box( 2, 2, 2 );
		var corner = new HashSet<EdgeKey>();
		foreach ( var key in boxSeamed.BuildEdgeFaces().Keys ) { corner.Add( key ); break; }
		var boxReport = UVUnwrap.Unwrap( boxSeamed, seams: corner );
		Check( "a seam on an already-split edge changes nothing",
			boxReport.Charts == boxPlain.Charts, $"{boxPlain.Charts} vs {boxReport.Charts}" );
	}
}
