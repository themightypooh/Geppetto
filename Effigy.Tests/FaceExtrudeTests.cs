using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Extruding a FACE of a part that already exists — the thing that was missing.
///
/// The complaint, in full: select a face of a part built out of primitives, press Extrude, and be
/// told "no sketch yet — add a Sketch first". A dozen bodies on screen, a face lit up under the
/// cursor, and the tool sending you off to draw a rectangle in order to pull the rectangle you were
/// already pointing at. Nothing about that was a bug: Extrude resolved a Sketch, pulled Profiles out
/// of it, and had no path by which a mesh face could be an input at all.
///
/// A face IS a profile — a closed planar loop is the only thing the prism builder ever needed — so
/// everything Extrude already does works from one: taper, termination, second distance, Result.
///
/// THE ONE ASYMMETRY WORTH KNOWING, and the reason this file measures topology as well as volume: a
/// whole face pulled straight out and ADDED is done as a MOVE, not as a prism merged on. Emit's
/// append path does not cut the interface between two meshes, so a prism would leave the original
/// face buried inside the solid as a coincident double surface — non-manifold, and Shell would
/// rightly refuse the part afterwards. So the plain pull is checked for being a clean closed solid,
/// not merely for being the right size.
/// </summary>
public static class FaceExtrudeTests
{
	public static void Run()
	{
		Report.Section( "face extrude: a primitive with no sketch in the document" );
		TestPullsAFaceOfABox();
		TestTheResultIsCleanEnoughToShell();
		TestPushingInwards();

		Report.Section( "face extrude: the settings that make it a prism instead" );
		TestTaperBuildsAPrism();
		TestNewBodyLeavesTheOriginalAlone();
		TestUpToNextMeasuresAgainstTheModel();

		Report.Section( "face extrude: it stays a feature" );
		TestEditingTheDistanceRebuilds();
		TestAFaceBeatsASketch();
	}

	/// <summary>The whole complaint, as one test: box, pick its top, extrude, no sketch anywhere in
	/// the document.</summary>
	static void TestPullsAFaceOfABox()
	{
		var studio = BoxStudio( out var body );
		var top = FaceFacing( body.Mesh, new Vec3( 0, 0, 1 ) );
		var area = body.Mesh.FaceArea( body.Mesh.Faces[top] );
		var before = body.Mesh.SignedVolume();

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Faces.Add( Capture( body, top ) );
		extrude.Distance.Value = 0.5f;

		var report = studio.Rebuild();

		Report.Check( "it builds with no sketch in the tree at all",
			!report.HasErrors, report.ToString() );

		Report.Check( "there is still exactly one part", studio.Bodies.Count == 1,
			$"{studio.Bodies.Count} bodies" );

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "and it grew by exactly the face area times the distance",
			Near( mesh.SignedVolume() - before, area * 0.5f ),
			$"{mesh.SignedVolume() - before:0.#####}, wanted {area * 0.5f:0.#####}" );
	}

	/// <summary>
	/// THE TOPOLOGY CHECK, and the reason a plain pull is a move rather than a prism.
	///
	/// A merged prism would leave the original top face buried inside the solid: still closed, still
	/// valid to a face-count test, and non-manifold along the interface — which Shell refuses. If
	/// this ever regresses to building a prism, this is the test that says so, and it says it in the
	/// terms the user would eventually hit.
	/// </summary>
	static void TestTheResultIsCleanEnoughToShell()
	{
		var studio = BoxStudio( out var body );
		var top = FaceFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Faces.Add( Capture( body, top ) );
		extrude.Distance.Value = 0.5f;
		studio.Rebuild();

		var mesh = studio.Bodies.Single().Mesh;
		var validation = MeshValidator.Validate( mesh );

		Report.Check( "the pulled part is a valid closed manifold solid",
			validation.IsValid && validation.IsClosed, validation.ToString() );

		Report.Check( "no face is buried inside it — the face count is a box's",
			mesh.Faces.Count == 6, $"{mesh.Faces.Count} faces" );

		// The consequence, spelled out: a buried coincident face makes this throw.
		var shell = studio.Add( new ShellFeature() );
		shell.Thickness.Value = 0.1f;
		var report = studio.Rebuild();

		Report.Check( "and Shell accepts it afterwards", !report.HasErrors, report.ToString() );
	}

	static void TestPushingInwards()
	{
		var studio = BoxStudio( out var body );
		var top = FaceFacing( body.Mesh, new Vec3( 0, 0, 1 ) );
		var before = body.Mesh.SignedVolume();

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Faces.Add( Capture( body, top ) );
		extrude.Distance.Value = 0.3f;
		extrude.Flip.Value = true;

		var report = studio.Rebuild();

		Report.Check( "flipping pushes the face into the solid", !report.HasErrors, report.ToString() );

		Report.Check( "and takes material away rather than adding it",
			Near( studio.Bodies.Single().Mesh.SignedVolume() - before, -0.3f ),
			$"{studio.Bodies.Single().Mesh.SignedVolume() - before:0.#####}, wanted -0.3" );
	}

	// --- the cases that are genuinely a prism -----------------------------------------------------

	static void TestTaperBuildsAPrism()
	{
		var studio = BoxStudio( out var body );
		var top = FaceFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Faces.Add( Capture( body, top ) );
		extrude.Distance.Value = 0.5f;
		extrude.Taper.Value = 10f;

		var report = studio.Rebuild();

		Report.Check( "a tapered pull builds", !report.HasErrors, report.ToString() );

		var mesh = studio.Bodies.Single().Mesh;

		// A merged prism, so the mesh gained the boss's own faces rather than moving the box's.
		Report.Check( "and it merged a solid on rather than moving the face",
			mesh.Faces.Count > 6, $"{mesh.Faces.Count} faces" );

		Report.Check( "the part reaches the height it was asked for",
			Near( Highest( mesh ), 1f ), $"{Highest( mesh ):0.####}, wanted 1" );
	}

	static void TestNewBodyLeavesTheOriginalAlone()
	{
		var studio = BoxStudio( out var body );
		var top = FaceFacing( body.Mesh, new Vec3( 0, 0, 1 ) );
		var before = body.Mesh.SignedVolume();

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Faces.Add( Capture( body, top ) );
		extrude.Distance.Value = 0.5f;
		extrude.Result.Index = 1; // New body

		var report = studio.Rebuild();

		Report.Check( "a face can be pulled into a body of its own", !report.HasErrors, report.ToString() );

		Report.Check( "which makes two parts", studio.Bodies.Count == 2,
			$"{studio.Bodies.Count} bodies" );

		Report.Check( "and the part it came from is untouched",
			Near( studio.Bodies[0].Mesh.SignedVolume(), before ),
			$"{studio.Bodies[0].Mesh.SignedVolume():0.####}, was {before:0.####}" );

		Report.Check( "while the new one is the prism that was asked for",
			Near( studio.Bodies[1].Mesh.SignedVolume(), 0.5f ),
			$"{studio.Bodies[1].Mesh.SignedVolume():0.####}, wanted 0.5" );
	}

	/// <summary>Up to next asks the MODEL how far to go, and a face profile can ask it the same way a
	/// sketch profile does — which is the half of the refactor that would otherwise go untested.
	/// </summary>
	static void TestUpToNextMeasuresAgainstTheModel()
	{
		var studio = new PartStudio();

		var lower = studio.Add( new PrimitiveFeature() );
		lower.SizeX.Value = lower.SizeY.Value = lower.SizeZ.Value = 1f;

		// A second box floating two units above the first, as the thing to stop at.
		var upper = studio.Add( new PrimitiveFeature() );
		upper.SizeX.Value = upper.SizeY.Value = upper.SizeZ.Value = 1f;
		upper.Position.Value = new Vec3( 0, 0, 2f );

		studio.Rebuild();

		var body = studio.Bodies[0];
		var top = FaceFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Faces.Add( Capture( body, top ) );
		extrude.Termination.Index = 1; // Up to next
		extrude.Result.Index = 1;      // its own body, so the measurement is easy to read

		var report = studio.Rebuild();

		Report.Check( "up to next builds from a face", !report.HasErrors, report.ToString() );

		var prism = studio.Bodies.Last().Mesh;

		// Top of the lower box is z = 0.5; bottom of the upper box is z = 1.5. So exactly 1.0.
		Report.Check( "and it stops at the first thing in the way",
			Near( Highest( prism ), 1.5f ), $"reached {Highest( prism ):0.####}, wanted 1.5" );
	}

	// --- still a feature ---------------------------------------------------------------------------

	static void TestEditingTheDistanceRebuilds()
	{
		var studio = BoxStudio( out var body );
		var top = FaceFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Faces.Add( Capture( body, top ) );
		extrude.Distance.Value = 0.5f;
		studio.Rebuild();

		Report.Check( "the first build reaches 1.0", Near( Highest( studio.Bodies.Single().Mesh ), 1f ),
			$"{Highest( studio.Bodies.Single().Mesh ):0.####}" );

		extrude.Distance.Value = 1.5f;
		studio.MarkDirty( extrude );
		studio.Rebuild();

		Report.Check( "and changing the number afterwards moves it again",
			Near( Highest( studio.Bodies.Single().Mesh ), 2f ),
			$"{Highest( studio.Bodies.Single().Mesh ):0.####}, wanted 2" );
	}

	/// <summary>A picked face is the more specific answer, so it wins over a sketch that also
	/// happens to be in the tree.</summary>
	static void TestAFaceBeatsASketch()
	{
		var studio = BoxStudio( out var body );

		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 2f, 2f ), new Vec2( 3f, 3f ) );
		studio.Rebuild();

		body = studio.Bodies[0];
		var top = FaceFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Faces.Add( Capture( body, top ) );
		extrude.Distance.Value = 0.5f;
		studio.Rebuild();

		Report.Check( "the face is what got pulled, not the sketch beside it",
			studio.Bodies.Count == 1 && Near( Highest( studio.Bodies[0].Mesh ), 1f ),
			$"{studio.Bodies.Count} bodies, top at {Highest( studio.Bodies[0].Mesh ):0.####}" );
	}

	// --- fixtures ----------------------------------------------------------------------------------

	static bool Near( float a, float b ) => MathF.Abs( a - b ) < 1e-4f;

	static PartStudio BoxStudio( out Body body )
	{
		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature() );
		studio.Rebuild();
		body = studio.Bodies.Single();

		return studio;
	}

	static FaceRef Capture( Body body, int face ) =>
		FacePlane.Capture( body, face, body.Mesh.FaceCentroid( body.Mesh.Faces[face] ) );

	static int FaceFacing( PolyMesh mesh, Vec3 direction )
	{
		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( Vec3.Dot( mesh.FaceNormal( mesh.Faces[i] ), direction.Normal ) > 0.999f )
				return i;
		}

		throw new InvalidOperationException( "no face pointing that way" );
	}

	static float Highest( PolyMesh mesh )
	{
		var z = float.MinValue;

		foreach ( var p in mesh.Positions )
			z = MathF.Max( z, p.z );

		return z;
	}
}
