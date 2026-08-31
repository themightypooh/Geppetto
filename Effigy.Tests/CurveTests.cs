using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The two curve types added after line, arc and circle: ellipse and spline.
///
/// Both are tested the same way, and it is not by comparing tessellations against golden numbers —
/// a golden polyline locks in whatever the sampler did on the day, including its bugs, and breaks
/// on every legitimate change to the step count. What is asserted instead is the PROPERTIES the
/// curve is supposed to have: an ellipse's points satisfy the ellipse equation, a spline actually
/// passes through the points it was given, a closed curve closes, and both survive a save and a
/// load and turn into a region an extrude can consume.
/// </summary>
public static class CurveTests
{
	public static void Run()
	{
		Report.Section( "ellipse" );
		TestEllipse();

		Report.Section( "spline" );
		TestSpline();

		Report.Section( "new curves reach the rest of the tool" );
		TestDownstream();
	}

	static void TestEllipse()
	{
		// Deliberately rotated and not axis-aligned. An axis-aligned ellipse would pass even if the
		// rotation were dropped entirely, which is the one thing storing the major axis as a point
		// is there to carry.
		var sketch = new Sketch();
		var ellipse = sketch.AddEllipse( new Vec2( 1f, 2f ), new Vec2( 4f, 6f ), 2f );

		var centre = sketch.Points[ellipse.Center];
		var major = ellipse.MajorRadius( sketch );

		Report.Check( "the major radius comes from the major-axis point", MathF.Abs( major - 5f ) < 1e-4f,
			$"{major:0.####}, wanted 5" );

		var points = ellipse.Tessellate( sketch, 0.01f );

		Report.Check( "it tessellates to a closed ring",
			(points[0] - points[^1]).Length < 1e-4f && points.Count > 8,
			$"{points.Count} points, ends {(points[0] - points[^1]).Length:0.#####} apart" );

		// Every sample must satisfy (u/a)^2 + (v/b)^2 = 1 in the ellipse's OWN frame, where u and v
		// are measured along and across the major axis. That is the definition, and it is the check
		// that a rotation applied in the wrong direction fails.
		var axis = sketch.Points[ellipse.MajorPoint] - centre;
		var ux = axis.x / major;
		var uy = axis.y / major;

		var worst = 0f;

		foreach ( var p in points )
		{
			var dx = p.x - centre.x;
			var dy = p.y - centre.y;

			var u = dx * ux + dy * uy;
			var v = -dx * uy + dy * ux;

			worst = MathF.Max( worst, MathF.Abs( u * u / (major * major) + v * v / (2f * 2f) - 1f ) );
		}

		Report.Check( "and every sample sits on the ellipse, in its own rotated frame", worst < 1e-3f,
			$"worst deviation {worst:0.######}" );

		// A long thin ellipse is where a sampler that took its step count from the MAJOR radius
		// goes wrong: the ends are the sharpest part of the curve and get the fewest samples.
		var thin = new Sketch();
		var needle = thin.AddEllipse( new Vec2( 0f, 0f ), new Vec2( 40f, 0f ), 1f );
		var needlePoints = needle.Tessellate( thin, 0.01f );

		var tipError = 0f;

		foreach ( var p in needlePoints )
		{
			var u = p.x / 40f;
			var v = p.y / 1f;
			tipError = MathF.Max( tipError, MathF.Abs( u * u + v * v - 1f ) );
		}

		Report.Check( "a long thin ellipse is sampled from its sharpest curvature, not its longest axis",
			tipError < 1e-3f, $"{needlePoints.Count} points, worst deviation {tipError:0.######}" );

		var degenerate = new Sketch();
		var flat = degenerate.AddEllipse( new Vec2( 0f, 0f ), new Vec2( 0f, 0f ), 0f );

		Report.Check( "a zero-sized ellipse tessellates to something harmless rather than dividing by zero",
			flat.Tessellate( degenerate, 0.01f ).All( p => float.IsFinite( p.x ) && float.IsFinite( p.y ) ) );
	}

	static void TestSpline()
	{
		// Unevenly spaced ON PURPOSE. Even spacing is the case uniform Catmull-Rom also gets right,
		// so it would not distinguish the centripetal parameterisation from the thing it replaced.
		var sketch = new Sketch();
		var spline = sketch.AddSpline( false,
			new Vec2( 0f, 0f ),
			new Vec2( 1f, 3f ),
			new Vec2( 1.2f, 3.1f ),
			new Vec2( 6f, 4f ),
			new Vec2( 8f, 0f ) );

		var points = spline.Tessellate( sketch, 0.01f );

		Report.Check( "a spline starts and ends on its first and last point",
			(points[0] - sketch.Points[spline.Points[0]]).Length < 1e-5f &&
			(points[^1] - sketch.Points[spline.Points[^1]]).Length < 1e-5f );

		// INTERPOLATION IS THE WHOLE CLAIM. Every authored point must appear on the curve, or a
		// dimension attached to one measures nothing.
		var missed = 0f;
		var missedAt = -1;

		for ( var i = 0; i < spline.Points.Count; i++ )
		{
			var knot = sketch.Points[spline.Points[i]];
			var nearest = points.Min( p => (p - knot).Length );

			if ( nearest > missed )
			{
				missed = nearest;
				missedAt = i;
			}
		}

		Report.Check( "and passes through every point it was given", missed < 1e-3f,
			$"point {missedAt} missed by {missed:0.######}" );

		// The centripetal claim: no loop between two points. A uniform Catmull-Rom through the
		// cluster above overshoots and doubles back, which shows up as the polyline reversing
		// direction relative to the chord it is meant to be following.
		var reversals = 0;

		for ( var i = 1; i < points.Count - 1; i++ )
		{
			var a = points[i] - points[i - 1];
			var b = points[i + 1] - points[i];

			if ( a.x * b.x + a.y * b.y < 0f )
				reversals++;
		}

		Report.Check( "with no cusp from the uneven spacing", reversals == 0,
			$"{reversals} direction reversals" );

		var closed = new Sketch();
		var ring = closed.AddSpline( true,
			new Vec2( 0f, 0f ), new Vec2( 3f, 0.5f ), new Vec2( 4f, 3f ), new Vec2( 0.5f, 2.5f ) );

		var ringPoints = ring.Tessellate( closed, 0.01f );

		Report.Check( "a closed spline is closed and reports itself as a region",
			ring.IsClosed && (ringPoints[0] - ringPoints[^1]).Length < 1e-4f,
			$"ends {(ringPoints[0] - ringPoints[^1]).Length:0.#####} apart" );

		Report.Check( "an open spline offers its two ends to a loop walk, a closed one does not",
			spline.Endpoints == (spline.Points[0], spline.Points[^1]) && ring.Endpoints == (-1, -1) );

		var pair = new Sketch();
		var straight = pair.AddSpline( false, new Vec2( 0f, 0f ), new Vec2( 2f, 1f ) );

		Report.Check( "a two-point spline is a straight line rather than a division by zero",
			straight.Tessellate( pair, 0.01f ).Count == 2 );
	}

	static void TestDownstream()
	{
		// A closed curve has to become a region without loop finding knowing what kind it is. That
		// is what moving the type switch out of Profile and onto the curve was for.
		var sketch = new Sketch();
		sketch.AddEllipse( new Vec2( 0f, 0f ), new Vec2( 4f, 0f ), 2f );

		var profile = ProfileFinder.Find( sketch );

		Report.Check( "an ellipse alone is one closed region", profile.Profiles.Count == 1,
			$"{profile.Profiles.Count} regions, warnings: {string.Join( "; ", profile.Warnings )}" );

		var ringSketch = new Sketch();
		ringSketch.AddSpline( true,
			new Vec2( 0f, 0f ), new Vec2( 4f, 0f ), new Vec2( 4f, 4f ), new Vec2( 0f, 4f ) );

		Report.Check( "and a closed spline is too", ProfileFinder.Find( ringSketch ).Profiles.Count == 1 );

		// An ellipse inside a rectangle is a hole, which only works if the nesting logic sees the
		// ellipse as an ordinary loop.
		var plate = new Sketch();
		plate.AddRectangle( new Vec2( -5f, -5f ), new Vec2( 5f, 5f ) );
		plate.AddEllipse( new Vec2( 0f, 0f ), new Vec2( 2f, 0f ), 1f );

		var plateProfile = ProfileFinder.Find( plate );

		Report.Check( "an ellipse inside a rectangle is a hole in it",
			plateProfile.Profiles.Count == 1 && plateProfile.Profiles[0].Holes.Count == 1,
			$"{plateProfile.Profiles.Count} regions, {plateProfile.Profiles.FirstOrDefault()?.Holes.Count ?? 0} holes" );

		// ROUND-TRIP. A spline is the first variable-length record in the format, so its point
		// count is the thing most likely to be read back wrong.
		var studio = new PartStudio();
		var feature = studio.Add( new SketchFeature() );

		feature.Sketch.AddEllipse( new Vec2( 1f, 2f ), new Vec2( 4f, 6f ), 2.5f );
		var saved = feature.Sketch.AddSpline( false,
			new Vec2( 0f, 0f ), new Vec2( 1f, 2f ), new Vec2( 3f, 1f ) );
		saved.Construction = true;

		var back = ((SketchFeature)StudioDocument.Read( StudioDocument.Write( studio ) ).Features[0]).Sketch;

		var backEllipse = back.Curves.OfType<SketchEllipse>().FirstOrDefault();
		var backSpline = back.Curves.OfType<SketchSpline>().FirstOrDefault();

		Report.Check( "an ellipse survives a save and a load",
			backEllipse is not null && MathF.Abs( backEllipse.MinorRadius - 2.5f ) < 1e-5f,
			backEllipse is null ? "no ellipse came back" : $"minor {backEllipse.MinorRadius}" );

		Report.Check( "a spline comes back with all of its points, in order",
			backSpline is not null && backSpline.Points.SequenceEqual( saved.Points ),
			backSpline is null ? "no spline came back" : string.Join( ", ", backSpline.Points ) );

		Report.Check( "and the id and construction flag that follow a variable-length record survive it",
			backSpline is not null && backSpline.Id == saved.Id && backSpline.Construction,
			backSpline is null ? "no spline came back" : $"id {backSpline.Id}, construction {backSpline.Construction}" );

		// The end of the line: a region made from new curve types extrudes into a real solid.
		var solid = new PartStudio();
		var sketchFeature = solid.Add( new SketchFeature() );
		sketchFeature.Sketch.AddEllipse( new Vec2( 0f, 0f ), new Vec2( 3f, 0f ), 2f );

		var extrude = solid.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 2f;

		solid.Rebuild();

		var body = solid.Bodies.FirstOrDefault();

		Report.Check( "an ellipse extrudes into a closed solid",
			body is not null && MeshValidator.Validate( body.Mesh ).IsClosed,
			body is null ? string.Join( "; ", solid.Features.Select( f => f.Error ).Where( e => e != null ) )
				: MeshValidator.Validate( body.Mesh ).ToString() );
	}
}
