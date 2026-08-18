using System;
using System.Collections.Generic;
using System.Linq;
using ModelKernel;

namespace ModelKernel.Tests;

/// <summary>
/// Verification for sketches and the solids made from them.
///
/// The failure this is really guarding against is winding. An extrude wound the wrong way produces
/// a mesh that is topologically perfect — every Euler check passes, every face is a quad — and
/// renders as a black hole in the scene. Only enclosed volume catches it, so that is what most of
/// these assert. Known shapes with known volumes, checked to the number.
/// </summary>
public static class SketchTests
{
	public static void Run()
	{
		Section( "profiles" );
		TestProfiles();

		Section( "extrude" );
		TestExtrude();

		Section( "revolve" );
		TestRevolve();

		Section( "sketches in the feature tree" );
		TestFeatures();
	}

	// --- profiles -----------------------------------------------------------------------

	static void TestProfiles()
	{
		var rect = Profile.Rectangle( 2f, 3f );
		Check( "rectangle has 4 points", rect.Count == 4 );
		Check( "rectangle area is right", Near( rect.Area, 6f ), $"{rect.Area}" );
		Check( "built-in profiles are counter-clockwise", rect.IsCounterClockwise );

		var backwards = new Profile( rect.Points.AsEnumerable().Reverse() );
		Check( "a clockwise profile is detected", !backwards.IsCounterClockwise );
		Check( "and can be flipped", backwards.AsCounterClockwise().IsCounterClockwise );
		Check( "flipping preserves area", Near( backwards.AsCounterClockwise().Area, 6f ) );

		var circle = Profile.Circle( 1f, 64 );
		// A 64-gon is slightly inside the circle it approximates, so this is close but under pi.
		Check( "circle approaches pi r^2", MathF.Abs( circle.Area - MathF.PI ) < 0.01f, $"{circle.Area}" );

		var rounded = Profile.RoundedRectangle( 4f, 2f, 0.5f, 4 );
		Check( "rounded rectangle is smaller than its bounding box", rounded.Area < 8f && rounded.Area > 7f, $"{rounded.Area}" );
		Check( "a zero radius gives a plain rectangle", Profile.RoundedRectangle( 2f, 2f, 0f ).Count == 4 );
		Check( "the radius is clamped to what fits", Profile.RoundedRectangle( 2f, 2f, 99f ).Validate().Count == 0 );

		Check( "two points is not a profile", new Profile( new[] { new Vec2( 0, 0 ), new Vec2( 1, 1 ) } ).Validate().Count > 0 );
		Check( "collinear points enclose nothing",
			new Profile( new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 2, 0 ) } ).Validate().Count > 0 );
		Check( "duplicate points are caught",
			new Profile( new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 0 ), new Vec2( 0, 1 ) } ).Validate().Count > 0 );

		// The sketch plane has to survive a round trip or nothing drawn on it lands where it looks.
		var plane = new SketchPlane( new Vec3( 3, -1, 2 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ) );
		var local = new Vec2( 1.5f, -2.25f );
		Check( "plane round-trips a point", Near2( plane.ToLocal( plane.ToWorld( local ) ), local ),
			plane.ToLocal( plane.ToWorld( local ) ).ToString() );
		Check( "plane normal is perpendicular to both axes",
			MathF.Abs( Vec3.Dot( plane.Normal, plane.U ) ) < 1e-5f && MathF.Abs( Vec3.Dot( plane.Normal, plane.V ) ) < 1e-5f );
	}

	// --- extrude ------------------------------------------------------------------------

	static void TestExtrude()
	{
		var sketch = new Sketch( SketchPlane.XY, Profile.Rectangle( 2f, 3f ) );
		var mesh = SketchSolids.Extrude( sketch, 4f );

		var validation = MeshValidator.Validate( mesh );
		Check( "extrusion is valid", validation.IsValid, validation.ToString() );
		Check( "extrusion is closed", validation.IsClosed, validation.ToString() );
		Check( "Euler characteristic is 2", MeshValidator.EulerCharacteristic( mesh ) == 2,
			$"{MeshValidator.EulerCharacteristic( mesh )}" );

		// 4 walls + 2 caps.
		Check( "a rectangle extrudes to 6 faces", mesh.FaceCount == 6, $"{mesh.FaceCount}" );
		Check( "every wall is a quad", mesh.Faces.Take( 4 ).All( f => f.Count == 4 ) );

		// THE ONE THAT MATTERS: 2 x 3 x 4 is 24, and a negative result means it is inside-out.
		Check( "enclosed volume is exactly 24", Near( Volume( mesh ), 24f, 1e-3f ), $"{Volume( mesh ):0.####}" );

		// Drawn backwards, the answer must be identical — a user drawing clockwise is not an error.
		var reversed = new Sketch( SketchPlane.XY, new Profile( Profile.Rectangle( 2f, 3f ).Points.AsEnumerable().Reverse() ) );
		Check( "a clockwise sketch extrudes the same way", Near( Volume( SketchSolids.Extrude( reversed, 4f ) ), 24f, 1e-3f ),
			$"{Volume( SketchSolids.Extrude( reversed, 4f ) ):0.####}" );

		// A negative distance is the other direction, not an inverted solid.
		Check( "a negative distance still encloses positive volume", Near( Volume( SketchSolids.Extrude( sketch, -4f ) ), 24f, 1e-3f ),
			$"{Volume( SketchSolids.Extrude( sketch, -4f ) ):0.####}" );

		var sym = SketchSolids.Extrude( sketch, 4f, symmetric: true );
		Check( "symmetric encloses the same volume", Near( Volume( sym ), 24f, 1e-3f ) );

		var zs = sym.Positions.Select( p => p.z ).ToList();
		Check( "symmetric straddles the sketch plane", Near( zs.Min(), -2f ) && Near( zs.Max(), 2f ),
			$"{zs.Min()}..{zs.Max()}" );

		// Extruding on a tilted plane is where a hand-rolled basis usually goes wrong.
		var tilted = new Sketch(
			new SketchPlane( new Vec3( 5, 5, 5 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 0, 1 ) ),
			Profile.Rectangle( 2f, 3f ) );
		Check( "a tilted plane extrudes to the same volume", Near( Volume( SketchSolids.Extrude( tilted, 4f ) ), 24f, 1e-2f ),
			$"{Volume( SketchSolids.Extrude( tilted, 4f ) ):0.####}" );

		// Subdivision is the whole reason walls are quads, so prove the result survives it.
		var subdivided = CatmullClark.Subdivide( mesh, 2 );
		Check( "extrusion subdivides cleanly", MeshValidator.EulerCharacteristic( subdivided ) == 2 );
		Check( "and stays all quads", subdivided.Faces.All( f => f.Count == 4 ) );
		Check( "and stays positive volume", Volume( subdivided ) > 0f, $"{Volume( subdivided ):0.###}" );

		// UVs have to be real, because the normal-map bake will depend on them.
		var wallUVs = mesh.Faces[0].UVs;
		Check( "wall UVs run 0..1 around the profile", wallUVs.Any( uv => uv.x >= 0f ) && wallUVs.All( uv => uv.x <= 1.0001f ) );
		Check( "wall UVs carry height", wallUVs.Any( uv => MathF.Abs( uv.y - 4f ) < 1e-4f ) );

		var threw = false;
		try { SketchSolids.Extrude( sketch, 0f ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "a zero-distance extrude is refused", threw );

		threw = false;
		try { SketchSolids.Extrude( new Sketch( SketchPlane.XY, new Profile() ), 1f ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "an empty profile is refused", threw );
	}

	// --- revolve ------------------------------------------------------------------------

	static void TestRevolve()
	{
		// A 2x1 rectangle centred 3 from the axis, spun fully: Pappus's theorem says the volume is
		// area x the distance the centroid travels. 2 x (2 pi x 3) = 12 pi. A 64-segment revolve
		// approximates that from slightly inside.
		var profile = Profile.Rectangle( 2f, 1f );
		var offset = new Profile( profile.Points.Select( p => new Vec2( p.x + 3f, p.y ) ) );
		var sketch = new Sketch( SketchPlane.XY, offset );

		var mesh = SketchSolids.Revolve( sketch, Vec2.Zero, new Vec2( 0, 1 ), 360f, 64 );
		var validation = MeshValidator.Validate( mesh );

		Check( "full revolve is valid", validation.IsValid, validation.ToString() );
		Check( "full revolve is closed", validation.IsClosed, validation.ToString() );

		// A torus-like ring is genus 1, so V - E + F = 0.
		Check( "a ring is genus 1", MeshValidator.EulerCharacteristic( mesh ) == 0,
			$"{MeshValidator.EulerCharacteristic( mesh )}" );

		var expected = 12f * MathF.PI;
		Check( "volume approaches Pappus's theorem", MathF.Abs( Volume( mesh ) - expected ) < expected * 0.01f,
			$"{Volume( mesh ):0.###} vs {expected:0.###}" );
		Check( "revolve is all quads", mesh.Faces.All( f => f.Count == 4 ) );

		var half = SketchSolids.Revolve( sketch, Vec2.Zero, new Vec2( 0, 1 ), 180f, 32 );
		Check( "a partial revolve is closed by its end caps", MeshValidator.Validate( half ).IsClosed,
			MeshValidator.Validate( half ).ToString() );
		Check( "half the angle is about half the volume", MathF.Abs( Volume( half ) - expected / 2f ) < expected * 0.02f,
			$"{Volume( half ):0.###}" );

		// Crossing the axis sweeps the profile through itself — refused rather than silently wrong.
		var crossing = new Sketch( SketchPlane.XY, Profile.Rectangle( 8f, 1f ) );
		var threw = false;
		try { SketchSolids.Revolve( crossing, Vec2.Zero, new Vec2( 0, 1 ) ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "a profile crossing the axis is refused", threw );
	}

	// --- the tree -----------------------------------------------------------------------

	static void TestFeatures()
	{
		var studio = new PartStudio();

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Sketch.Value = new Sketch( SketchPlane.XY, Profile.Rectangle( 2f, 3f ) );
		extrude.Distance.Value = 4f;

		studio.Rebuild();

		Check( "extrude feature produces a body", studio.Bodies.Count == 1 );
		Check( "with the right volume", Near( Volume( studio.Bodies[0].Mesh ), 24f, 1e-3f ),
			$"{Volume( studio.Bodies[0].Mesh ):0.####}" );
		Check( "named after the feature type", extrude.Name == "Extrude 1", extrude.Name );

		// The point of holding the sketch by reference: edit it and the solid follows on rebuild.
		extrude.Sketch.Value.Outer = Profile.Rectangle( 4f, 3f );
		studio.MarkDirty( extrude );
		studio.Rebuild();

		Check( "editing the sketch changes the solid", Near( Volume( studio.Bodies[0].Mesh ), 48f, 1e-3f ),
			$"{Volume( studio.Bodies[0].Mesh ):0.####}" );

		extrude.Distance.Value = 2f;
		studio.MarkDirty( extrude );
		studio.Rebuild();
		Check( "and so does changing the distance", Near( Volume( studio.Bodies[0].Mesh ), 24f, 1e-3f ) );

		// Sketch solids have to work with everything else in the tree, not sit apart from it.
		var mirror = studio.Add( new MirrorFeature() );
		mirror.PlaneNormal.Value = new Vec3( 1, 0, 0 );
		mirror.PlanePoint.Value = new Vec3( 6, 0, 0 );
		studio.Rebuild();

		Check( "an extrusion can be mirrored", studio.Bodies.Count == 2 );
		Check( "and the mirrored copy is not inside-out", studio.Bodies.All( b => Volume( b.Mesh ) > 0f ),
			string.Join( ", ", studio.Bodies.Select( b => Volume( b.Mesh ).ToString( "0.##" ) ) ) );

		var revolve = new RevolveFeature();
		revolve.Sketch.Value = new Sketch( SketchPlane.XY,
			new Profile( Profile.Rectangle( 2f, 1f ).Points.Select( p => new Vec2( p.x + 3f, p.y ) ) ) );
		revolve.Segments.Value = 32;

		var studio2 = new PartStudio();
		studio2.Add( revolve );
		studio2.Rebuild();

		Check( "revolve feature runs", studio2.Bodies.Count == 1 && revolve.Error is null, revolve.Error );
		Check( "revolve feature encloses volume", Volume( studio2.Bodies[0].Mesh ) > 0f );

		// A broken sketch has to report through the feature, not take the rebuild down.
		var broken = new PartStudio();
		var bad = broken.Add( new ExtrudeFeature() );
		bad.Sketch.Value = new Sketch( SketchPlane.XY, new Profile() );
		var report = broken.Rebuild();

		Check( "a bad sketch records an error rather than throwing", report.HasErrors );
		Check( "and the message names the problem", bad.Error is not null && bad.Error.Contains( "3 points" ), bad.Error );
	}

	// --- helpers ------------------------------------------------------------------------

	/// <summary>Enclosed volume by the divergence theorem. Negative means the mesh is inside-out,
	/// which is the failure that looks perfect in wireframe.</summary>
	static float Volume( PolyMesh mesh )
	{
		var total = 0f;

		foreach ( var f in mesh.Faces )
		{
			for ( var i = 1; i < f.Count - 1; i++ )
			{
				var a = mesh.Positions[f.Indices[0]];
				var b = mesh.Positions[f.Indices[i]];
				var c = mesh.Positions[f.Indices[i + 1]];
				total += Vec3.Dot( a, Vec3.Cross( b, c ) ) / 6f;
			}
		}

		return total;
	}

	static bool Near( float a, float b, float tolerance = 1e-4f ) => MathF.Abs( a - b ) < tolerance;
	static bool Near2( Vec2 a, Vec2 b, float tolerance = 1e-4f ) => (a - b).Length < tolerance;

	static void Section( string title ) => Report.Section( title );
	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
