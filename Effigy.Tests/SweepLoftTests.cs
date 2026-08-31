using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Sweep and loft, checked against volumes that can be written down in advance.
///
/// VOLUME IS THE TEST THAT CATCHES THE REAL FAILURES. Both features can produce a mesh that is
/// closed, manifold, has the expected face count and is still wrong — twisted between sections,
/// inside out, or collapsed at a turn. A prism swept along a straight path has to enclose exactly
/// area times length, and a loft between two squares has to enclose the frustum formula. Neither
/// number survives a twist.
///
/// The SIGN of the volume is asserted too, not just its size. A solid built with its rings stitched
/// the wrong way round encloses the same magnitude with every normal pointing inward, which every
/// closed-and-manifold check in this repo passes happily and which renders as a hole.
/// </summary>
public static class SweepLoftTests
{
	public static void Run()
	{
		Report.Section( "sweep" );
		TestSweep();

		Report.Section( "loft" );
		TestLoft();
	}

	static void TestSweep()
	{
		// A 2x2 square swept 5 units straight up is a prism of volume 20, whatever route the code
		// takes to get there.
		var studio = new PartStudio();

		var profile = studio.Add( new SketchFeature() );
		profile.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var path = studio.Add( new SketchFeature() );
		path.Plane.Index = 1; // Front (XZ), so the path climbs out of the profile's plane
		var bottom = path.Sketch.AddPoint( 0f, 0f );
		var top = path.Sketch.AddPoint( 0f, 5f );
		path.Sketch.Add( new SketchLine( bottom, top ) );

		var sweep = studio.Add( new SweepFeature() );
		sweep.SketchFeatureId = profile.Id;
		sweep.PathSketchId = path.Id;

		var report = studio.Rebuild();

		Report.Check( "a sweep builds", !report.HasErrors,
			string.Join( "; ", studio.Features.Where( f => f.Error is not null ).Select( f => f.Error ) ) );

		var body = studio.Bodies.LastOrDefault();

		Report.Check( "into a closed solid",
			body is not null && MeshValidator.Validate( body.Mesh ).IsClosed,
			body is null ? "no body" : MeshValidator.Validate( body.Mesh ).ToString() );

		if ( body is null )
			return;

		var volume = Volume( body.Mesh );

		Report.Check( "of exactly area times length", MathF.Abs( volume - 20f ) < 0.01f,
			$"{volume:0.####}, wanted 20" );

		Report.Check( "with its faces pointing outward rather than in", volume > 0f,
			$"signed volume {volume:0.####}" );

		// A TURN IS WHERE A NAIVE FRAME BREAKS. The profile has to stay perpendicular to the path
		// through the corner; if it keeps its original orientation the solid pinches or folds, and
		// a fold shows up as a mesh that is no longer manifold.
		var bent = new PartStudio();

		var bentProfile = bent.Add( new SketchFeature() );
		bentProfile.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

		var bentPath = bent.Add( new SketchFeature() );
		bentPath.Plane.Index = 1;
		var a = bentPath.Sketch.AddPoint( 0f, 0f );
		var b = bentPath.Sketch.AddPoint( 0f, 4f );
		var c = bentPath.Sketch.AddPoint( 4f, 4f );
		bentPath.Sketch.Add( new SketchLine( a, b ) );
		bentPath.Sketch.Add( new SketchLine( b, c ) );

		var bentSweep = bent.Add( new SweepFeature() );
		bentSweep.SketchFeatureId = bentProfile.Id;
		bentSweep.PathSketchId = bentPath.Id;

		bent.Rebuild();

		var bentBody = bent.Bodies.LastOrDefault();

		Report.Check( "a sweep round a corner stays a closed manifold solid",
			bentBody is not null && MeshValidator.Validate( bentBody.Mesh ).IsClosed,
			bentBody is null ? "no body" : MeshValidator.Validate( bentBody.Mesh ).ToString() );

		Report.Check( "and does not fold itself inside out at the turn",
			bentBody is not null && Volume( bentBody.Mesh ) > 0f,
			bentBody is null ? "no body" : $"{Volume( bentBody.Mesh ):0.####}" );

		// THE ROLES ARE INFERRED FROM THE SKETCHES, not from their order. With nothing configured,
		// the closed sketch has to be taken as the profile even though the open one was drawn last.
		var guessed = new PartStudio();

		var guessedProfile = guessed.Add( new SketchFeature() );
		guessedProfile.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var guessedPath = guessed.Add( new SketchFeature() );
		guessedPath.Plane.Index = 1;
		guessedPath.Sketch.Add( new SketchLine(
			guessedPath.Sketch.AddPoint( 0f, 0f ), guessedPath.Sketch.AddPoint( 0f, 5f ) ) );

		guessed.Add( new SweepFeature() );
		guessed.Rebuild();

		Report.Check( "an unconfigured sweep works out which sketch is the profile and which is the path",
			guessed.Bodies.Count > 0 && MathF.Abs( Volume( guessed.Bodies[^1].Mesh ) - 20f ) < 0.01f,
			guessed.Features.Last().Error ?? $"{(guessed.Bodies.Count > 0 ? Volume( guessed.Bodies[^1].Mesh ) : 0):0.###}" );

		// A closed path has no ends, so the solid must not be capped — a capped torus has two
		// internal walls and stops being manifold.
		var ring = new PartStudio();

		var ringProfile = ring.Add( new SketchFeature() );
		ringProfile.Sketch.AddCircle( new Vec2( 0f, 0f ), 0.4f );

		var ringPath = ring.Add( new SketchFeature() );
		ringPath.Plane.Index = 1;
		ringPath.Sketch.AddCircle( new Vec2( 0f, 0f ), 3f );

		var ringSweep = ring.Add( new SweepFeature() );
		ringSweep.SketchFeatureId = ringProfile.Id;
		ringSweep.PathSketchId = ringPath.Id;

		ring.Rebuild();

		var torus = ring.Bodies.LastOrDefault();

		Report.Check( "a sweep round a closed path is a ring, left uncapped",
			torus is not null && MeshValidator.Validate( torus.Mesh ).IsClosed,
			torus is null ? ring.Features.Last().Error : MeshValidator.Validate( torus.Mesh ).ToString() );

		// Pappus: a torus encloses 2*pi^2*R*r^2. Loose tolerance because both circles are
		// tessellated, and a tessellated ring always falls slightly inside the true one.
		if ( torus is not null )
		{
			var expected = 2f * MathF.PI * MathF.PI * 3f * 0.4f * 0.4f;
			var got = Volume( torus.Mesh );

			Report.Check( "enclosing about what Pappus says it should", MathF.Abs( got - expected ) < expected * 0.05f,
				$"{got:0.####} against {expected:0.####}" );
		}
	}

	static void TestLoft()
	{
		// Two identical 2x2 squares 5 apart. A loft between them is the same prism the sweep made,
		// so the same 20 applies — and a twist between the sections would lose volume to shear.
		var studio = new PartStudio();

		var lower = studio.Add( new SketchFeature() );
		lower.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var upper = studio.Add( new SketchFeature() );
		upper.PlaneOffset.Value = 5f;
		upper.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var loft = studio.Add( new LoftFeature() );
		loft.Sections.Add( lower.Id );
		loft.Sections.Add( upper.Id );
		loft.Segments.Value = 40;

		var report = studio.Rebuild();

		Report.Check( "a loft builds", !report.HasErrors,
			string.Join( "; ", studio.Features.Where( f => f.Error is not null ).Select( f => f.Error ) ) );

		var body = studio.Bodies.LastOrDefault();

		Report.Check( "into a closed solid",
			body is not null && MeshValidator.Validate( body.Mesh ).IsClosed,
			body is null ? "no body" : MeshValidator.Validate( body.Mesh ).ToString() );

		if ( body is null )
			return;

		var volume = Volume( body.Mesh );

		Report.Check( "of the volume of the prism it is", MathF.Abs( volume - 20f ) < 0.05f,
			$"{volume:0.####}, wanted 20" );

		Report.Check( "with its faces pointing outward", volume > 0f, $"signed volume {volume:0.####}" );

		// THE TWIST TEST. The upper square is drawn starting from a different corner and wound the
		// other way. Nothing about the SHAPE changed, so the volume must not change either — and it
		// will, badly, if the sections are skinned point-0-to-point-0 without being aligned first.
		var twisted = new PartStudio();

		var flat = twisted.Add( new SketchFeature() );
		flat.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var turned = twisted.Add( new SketchFeature() );
		turned.PlaneOffset.Value = 5f;

		// Same square, started at the opposite corner and wound clockwise.
		turned.Sketch.AddPolygon(
			new Vec2( 1f, 1f ), new Vec2( 1f, -1f ), new Vec2( -1f, -1f ), new Vec2( -1f, 1f ) );

		var twistedLoft = twisted.Add( new LoftFeature() );
		twistedLoft.Sections.Add( flat.Id );
		twistedLoft.Sections.Add( turned.Id );
		twistedLoft.Segments.Value = 40;

		twisted.Rebuild();

		var twistedBody = twisted.Bodies.LastOrDefault();
		var twistedVolume = twistedBody is null ? 0f : Volume( twistedBody.Mesh );

		Report.Check( "a section drawn from another corner and wound the other way does not twist the loft",
			MathF.Abs( twistedVolume - 20f ) < 0.05f, $"{twistedVolume:0.####}, wanted 20" );

		// A FRUSTUM HAS A FORMULA: h/3 * (A1 + A2 + sqrt(A1*A2)). A loft that interpolated linearly
		// in the wrong place would pass a "closed solid" check and miss this by a wide margin.
		var cone = new PartStudio();

		var wide = cone.Add( new SketchFeature() );
		wide.Sketch.AddRectangle( new Vec2( -2f, -2f ), new Vec2( 2f, 2f ) );

		var narrow = cone.Add( new SketchFeature() );
		narrow.PlaneOffset.Value = 6f;
		narrow.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var coneLoft = cone.Add( new LoftFeature() );
		coneLoft.Sections.Add( wide.Id );
		coneLoft.Sections.Add( narrow.Id );
		coneLoft.Segments.Value = 40;

		cone.Rebuild();

		var frustum = cone.Bodies.LastOrDefault();
		var expected = 6f / 3f * (16f + 4f + MathF.Sqrt( 16f * 4f ));

		Report.Check( "a loft between different sizes matches the frustum formula",
			frustum is not null && MathF.Abs( Volume( frustum.Mesh ) - expected ) < 0.1f,
			frustum is null ? "no body" : $"{Volume( frustum.Mesh ):0.####}, wanted {expected:0.####}" );

		// Three sections, to prove the stack is not hard-wired to two.
		var stack = new PartStudio();

		var one = stack.Add( new SketchFeature() );
		one.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var two = stack.Add( new SketchFeature() );
		two.PlaneOffset.Value = 2f;
		two.Sketch.AddRectangle( new Vec2( -2f, -2f ), new Vec2( 2f, 2f ) );

		var three = stack.Add( new SketchFeature() );
		three.PlaneOffset.Value = 4f;
		three.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 1f ) );

		var stackLoft = stack.Add( new LoftFeature() );
		stackLoft.Sections.Add( one.Id );
		stackLoft.Sections.Add( two.Id );
		stackLoft.Sections.Add( three.Id );
		stackLoft.Segments.Value = 40;

		stack.Rebuild();

		var stacked = stack.Bodies.LastOrDefault();

		// Two frustums back to back, each 2 tall between areas 4 and 16.
		var half = 2f / 3f * (4f + 16f + MathF.Sqrt( 4f * 16f ));

		Report.Check( "three sections loft as two frustums back to back",
			stacked is not null && MathF.Abs( Volume( stacked.Mesh ) - half * 2f ) < 0.2f,
			stacked is null ? "no body" : $"{Volume( stacked.Mesh ):0.####}, wanted {half * 2f:0.####}" );

		Report.Check( "a loft with only one section says so rather than building nothing",
			OneSectionFails(), "it built something" );
	}

	static bool OneSectionFails()
	{
		var studio = new PartStudio();

		var only = studio.Add( new SketchFeature() );
		only.Sketch.AddRectangle( new Vec2( 0f, 0f ), new Vec2( 1f, 1f ) );

		var loft = studio.Add( new LoftFeature() );
		loft.Sections.Add( only.Id );

		studio.Rebuild();

		return loft.Error is not null;
	}

	/// <summary>
	/// Signed volume by the divergence theorem, summed over a fan of each face. Positive when the
	/// faces wind so their normals point out of the solid.
	/// </summary>
	static float Volume( PolyMesh mesh ) => mesh.SignedVolume();
}
