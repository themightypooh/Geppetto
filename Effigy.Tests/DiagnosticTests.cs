using System;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// A feature that cannot do what was asked says what it was asked, what stopped it, with this
/// model's numbers, and what would work instead. A feature that did nothing is never a success.
/// </summary>
public static class DiagnosticTests
{
	public static void Run()
	{
		Section( "diagnostics: the mechanism" );
		TestPlainExceptionBecomesADiagnostic();
		TestFeatureExceptionKeepsItsStructure();

		Section( "diagnostics: signed volume lives on the mesh" );
		TestSignedVolume();

		Section( "diagnostics: an oversized fillet is an error, not a silent invert" );
		TestOversizedFillet();
		TestFilletsPastTheHalfWidthAllRefuse();
		TestFilletJustUnderTheHalfWidthStillBuilds();
		TestSmallFilletStillBuilds();
		TestGenerousFilletWarnsItTookHalf();

		Section( "diagnostics: a no-op is an error" );
		TestUnreachableThresholdIsAnError();

		Section( "diagnostics: empty-studio refusals have a cause and a remedy" );
		TestEmptyFilletHasACause();

		Section( "diagnostics: a missing body is an error, not a silent no-op" );
		TestMissingBodySelectionIsAnError();

		Section( "diagnostics: an oversized chamfer is an error too" );
		TestOversizedChamfer();

		Section( "diagnostics: a too-thick shell names a thickness that fits" );
		TestTooThickShell();

		Section( "diagnostics: a boolean that cannot cut says why" );
		TestBooleanMisses();
		TestBooleanOpenSolid();
	}

	static void TestPlainExceptionBecomesADiagnostic()
	{
		var studio = new PartStudio();
		var tube = studio.Add( new PrimitiveFeature() );
		tube.Shape.Index = 4;
		tube.InnerRadius.Value = 10f;
		tube.Radius.Value = 1f;
		studio.Rebuild();

		Check( "a thrown failure becomes a diagnostic", tube.Diagnostic is not null );
		Check( "and it is an error", tube.Diagnostic is { Severity: DiagnosticSeverity.Error } );
		Check( "the problem line names the parameter",
			tube.Diagnostic?.Problem.Contains( "Inner radius" ) == true, tube.Diagnostic?.Problem );
		Check( "the cause has this model's numbers",
			HasNumber( tube.Diagnostic?.Cause ), tube.Diagnostic?.Cause );
		Check( "and there is a remedy",
			tube.Diagnostic is { Remedies.Count: > 0 }, string.Join( "; ", tube.Diagnostic?.Remedies ?? Enumerable.Empty<string>() ) );
	}

	static void TestFeatureExceptionKeepsItsStructure()
	{
		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature() );
		var fillet = studio.Add( new FilletFeature() );
		fillet.Radius.Value = 0.85f;
		studio.Rebuild();

		Check( "the diagnostic is the one the feature threw, not a wrap of its message",
			fillet.Diagnostic is not null && fillet.Error == fillet.Diagnostic.Problem,
			fillet.Error ?? "no error" );
	}

	static void TestSignedVolume()
	{
		var box = Primitives.Box( 2, 2, 2 );
		Check( "a 2x2x2 box encloses 8", Close( box.SignedVolume(), 8f ), $"{box.SignedVolume()}" );
		Check( "and the sign is positive", box.SignedVolume() > 0f );
	}

	// THE NUMBERS IN THE THREE BLEND TESTS BELOW MOVED, and it is worth knowing why before moving
	// them again. Every one of them turns on the ENCLOSED VOLUME of a blended box, and that number
	// used to be wrong: EdgeBlend emitted its vertex caps wound inwards, so the corner triangles
	// subtracted their own contribution instead of adding it (see the note in EdgeBlend.Finish). A
	// chamfered unit box measured 0.811 against a true 0.883.
	//
	// So these radii were chosen against a ruler that read low, and once the ruler was fixed a
	// 0.85 fillet on a 2-unit cube stopped being "inside out" and became what it always actually
	// was: a very rounded cube, and a perfectly good solid. The sizes here are re-picked against
	// the corrected volumes, and every assertion they make is unchanged.

	static void TestOversizedFillet()
	{
		var studio = StudioWithBox();
		var fillet = studio.Add( new FilletFeature() );
		fillet.Radius.Value = 1.3f;
		fillet.AngleThreshold.Value = 15f;
		fillet.Segments.Value = 4;
		studio.Rebuild();

		Check( "Fillet(cube, 1.3) is an error", fillet.Error is not null, "built anyway" );
		Check( "it is structured", fillet.Diagnostic is { Severity: DiagnosticSeverity.Error } );
		Check( "the cause has a number from this model",
			HasNumber( fillet.Diagnostic?.Cause ), fillet.Diagnostic?.Cause );
		Check( "and there is a remedy",
			fillet.Diagnostic is { Remedies.Count: > 0 } );
		Check( "the body is not handed downstream inverted",
			studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.SignedVolume() > 0f,
			$"{studio.Bodies[0].Mesh.SignedVolume()}" );
		Check( "a suggested radius is offered",
			fillet.Diagnostic?.SuggestedValue is > 0f and < 1.3f,
			$"{fillet.Diagnostic?.SuggestedValue}" );
	}

	// THE BAND THE VOLUME GUARD COULD NOT SEE, and the reason this test exists as a sweep rather
	// than as one more radius: the failure was not at a point, it was over an interval, and any
	// single radius picked out of it would have been a magic number nobody could re-derive.
	//
	// On a 2-unit cube the opposite fillets meet exactly at radius 1.0 — each eats r off both ends
	// of a 2-long edge — so every radius above that has consumed more of every face than the face
	// had. ResultVolume <= 0 caught it only from about 1.25 up, because between 1.0 and 1.25 the
	// part is inside out and STILL ENCLOSES A POSITIVE VOLUME. Forty-eight faces pointed into their
	// own body and the mesh was closed, manifold, Euler-correct and valid the whole way: rule 1 of
	// the work order, in the wild, and the reason the fix is a per-edge test in CountCollapsed
	// rather than a tighter number on the volume.
	static void TestFilletsPastTheHalfWidthAllRefuse()
	{
		// Deliberately walks THROUGH the old guard's blind spot and out the far side.
		foreach ( var radius in new[] { 1.05f, 1.1f, 1.15f, 1.2f, 1.24f, 1.3f } )
		{
			var studio = StudioWithBox();
			var fillet = studio.Add( new FilletFeature() );
			fillet.Radius.Value = radius;
			fillet.AngleThreshold.Value = 15f;
			fillet.Segments.Value = 4;
			studio.Rebuild();

			Check( $"Fillet(cube, {radius:0.##}) is refused — it is past the half-width",
				fillet.Error is not null, "built anyway" );

			// The point of refusing. A part that goes downstream with faces pointing into itself
			// is the failure this whole guard exists to prevent, and it is invisible to every
			// validator the kernel owns.
			Check( $"and nothing inside out reaches the body list at {radius:0.##}",
				studio.Bodies.Count == 1 && !PointsInward( studio.Bodies[0].Mesh ),
				"a face points into its own body" );

			Check( $"and the remedy at {radius:0.##} is a radius that actually fits",
				fillet.Diagnostic?.SuggestedValue is { } fit && fit > 0f && fit < 1f,
				$"{fillet.Diagnostic?.SuggestedValue}" );
		}
	}

	// Just below the meeting point is a very rounded cube and a perfectly good solid, so the guard
	// must not have simply become stricter everywhere — a test that only asserts refusals would
	// pass just as well if the feature had stopped working altogether.
	static void TestFilletJustUnderTheHalfWidthStillBuilds()
	{
		var studio = StudioWithBox();
		var fillet = studio.Add( new FilletFeature() );
		fillet.Radius.Value = 0.95f;
		fillet.AngleThreshold.Value = 15f;
		fillet.Segments.Value = 4;
		studio.Rebuild();

		Check( "Fillet(cube, 0.95) still builds", fillet.Error is null, fillet.Error );
		Check( "and it encloses what a 0.95-rounded 2-cube should",
			studio.Bodies[0].Mesh.SignedVolume() > 3f && studio.Bodies[0].Mesh.SignedVolume() < 3.2f,
			$"{studio.Bodies[0].Mesh.SignedVolume()}" );
		Check( "and no face points into its own body",
			!PointsInward( studio.Bodies[0].Mesh ), "a face points inward" );
	}

	/// <summary>
	/// Does any face point back into the solid?
	///
	/// Compares each face normal against the direction from the mesh's own centroid out to that
	/// face. That is only sound for a roughly convex body, which a blended box is, and it is the
	/// measurement that made the bug visible in the first place — every other check the kernel owns
	/// said the mesh was fine.
	/// </summary>
	static bool PointsInward( PolyMesh mesh )
	{
		var centre = Vec3.Zero;

		for ( var i = 0; i < mesh.Positions.Count; i++ )
			centre += mesh.Positions[i];

		centre /= System.Math.Max( 1, mesh.Positions.Count );

		foreach ( var face in mesh.Faces )
		{
			if ( Vec3.Dot( mesh.FaceNormal( face ), mesh.FaceCentroid( face ) - centre ) < 0f )
				return true;
		}

		return false;
	}

	static void TestSmallFilletStillBuilds()
	{
		var studio = StudioWithBox();
		var fillet = studio.Add( new FilletFeature() );
		fillet.Radius.Value = 0.2f;
		fillet.AngleThreshold.Value = 15f;
		fillet.Segments.Value = 4;
		studio.Rebuild();

		Check( "Fillet(cube, 0.2) is not an error", fillet.Error is null, fillet.Error );
		Check( "and the solid stayed a solid",
			studio.Bodies[0].Mesh.SignedVolume() > 0f, $"{studio.Bodies[0].Mesh.SignedVolume()}" );
		Check( "and it actually rounded something",
			studio.Bodies[0].Mesh.FaceCount > 6, $"{studio.Bodies[0].Mesh.FaceCount} faces" );
	}

	static void TestGenerousFilletWarnsItTookHalf()
	{
		var studio = StudioWithBox();
		var fillet = studio.Add( new FilletFeature() );
		fillet.Radius.Value = 0.9f;
		fillet.AngleThreshold.Value = 15f;
		fillet.Segments.Value = 4;
		studio.Rebuild();

		Check( "Fillet(cube, 0.9) still builds", fillet.Error is null, fillet.Error );
		Check( "but warns that more than half the solid is gone",
			fillet.Warning is not null && fillet.Diagnostic is { Severity: DiagnosticSeverity.Warning },
			fillet.Warning ?? "no warning" );
		Check( "the cause names the volumes",
			HasNumber( fillet.Diagnostic?.Cause ), fillet.Diagnostic?.Cause );
	}

	static void TestUnreachableThresholdIsAnError()
	{
		var cube = Primitives.Box( 2, 2, 2 );
		var untouched = EdgeBlend.Fillet( cube, 0.1f, 179f, 4 );

		Check( "the kernel still leaves the cube alone",
			untouched.FaceCount == cube.FaceCount && untouched.VertexCount == cube.VertexCount );

		var studio = StudioWithBox();
		var fillet = studio.Add( new FilletFeature() );
		fillet.Radius.Value = 0.1f;
		fillet.AngleThreshold.Value = 179f;
		fillet.Segments.Value = 4;
		studio.Rebuild();

		Check( "the feature reports the no-op as an error", fillet.Error is not null, "reported success" );
		Check( "naming the sharpest edge",
			HasNumber( fillet.Diagnostic?.Cause ) && fillet.Diagnostic?.Cause.Contains( "°" ) == true,
			fillet.Diagnostic?.Cause );
		Check( "and the geometry stays unchanged",
			studio.Bodies[0].Mesh.FaceCount == 6, $"{studio.Bodies[0].Mesh.FaceCount} faces" );
	}

	static void TestEmptyFilletHasACause()
	{
		var studio = new PartStudio();
		var fillet = studio.Add( new FilletFeature() );
		studio.Rebuild();

		Check( "an empty-studio fillet is an error", fillet.Error is not null );
		Check( "with a cause", !string.IsNullOrEmpty( fillet.Diagnostic?.Cause ), fillet.Error );
		Check( "and a remedy", fillet.Diagnostic is { Remedies.Count: > 0 } );
	}

	static void TestMissingBodySelectionIsAnError()
	{
		var studio = StudioWithBox();
		var subdivide = studio.Add( new SubdivideFeature() );
		subdivide.Bodies.BodyIds.Add( "body-that-was-deleted" );
		studio.Rebuild();

		Check( "it is an error", subdivide.Error is not null, "reported success" );
		Check( "with a cause", !string.IsNullOrEmpty( subdivide.Diagnostic?.Cause ), subdivide.Error );
		Check( "and a remedy", subdivide.Diagnostic is { Remedies.Count: > 0 } );
		Check( "and the box is left as a box",
			studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.FaceCount == 6,
			$"{studio.Bodies[0].Mesh.FaceCount} faces" );
	}

	static void TestOversizedChamfer()
	{
		var studio = StudioWithBox();
		var chamfer = studio.Add( new ChamferFeature() );
		chamfer.Width.Value = 1.3f;
		chamfer.AngleThreshold.Value = 15f;
		studio.Rebuild();

		Check( "Chamfer(cube, 1.3) is an error", chamfer.Error is not null, "built anyway" );
		Check( "the body is not handed downstream inverted",
			studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.SignedVolume() > 0f,
			$"{studio.Bodies[0].Mesh.SignedVolume()}" );
		Check( "a suggested distance is offered",
			chamfer.Diagnostic?.SuggestedValue is > 0f and < 1.3f,
			$"{chamfer.Diagnostic?.SuggestedValue}" );
	}

	static void TestTooThickShell()
	{
		var studio = new PartStudio();
		var plate = studio.Add( new PrimitiveFeature() );
		plate.SizeX.Value = plate.SizeY.Value = 4f;
		plate.SizeZ.Value = 1f;
		var shell = studio.Add( new ShellFeature() );
		shell.Thickness.Value = 0.6f;
		studio.Rebuild();

		Check( "shelling a 1-thick plate by 0.6 is an error", shell.Error is not null, "built anyway" );
		Check( "the cause has this model's numbers",
			HasNumber( shell.Diagnostic?.Cause ), shell.Diagnostic?.Cause );
		Check( "a suggested thickness is offered",
			shell.Diagnostic?.SuggestedValue is > 0f and < 0.5f,
			$"{shell.Diagnostic?.SuggestedValue}" );
		Check( "the first remedy names that number",
			shell.Diagnostic is { Remedies.Count: > 0 }
			&& HasNumber( shell.Diagnostic.Remedies[0] ),
			string.Join( "; ", shell.Diagnostic?.Remedies ?? Enumerable.Empty<string>() ) );
	}

	static void TestBooleanMisses()
	{
		var previous = MeshBoolean.Provider;

		try
		{
			MeshBoolean.Provider = new RefusingBoolean();

			var target = Primitives.Box( 2, 2, 2 );
			var tool = MeshTransform.Transformed( Primitives.Box( 2, 2, 2 ), Xform.Translate( new Vec3( 10, 0, 0 ) ) );

			var diagnostic = Caught( () => MeshBoolean.Apply( BooleanOp.Subtract, target, tool ) );

			Check( "a pair that misses is an error", diagnostic is { Severity: DiagnosticSeverity.Error } );
			Check( "naming the gap along X",
				diagnostic?.Cause?.Contains( "X" ) == true && HasNumber( diagnostic.Cause ),
				diagnostic?.Cause );
			Check( "with a remedy", diagnostic is { Remedies.Count: > 0 } );
		}
		finally
		{
			MeshBoolean.Provider = previous;
		}
	}

	static void TestBooleanOpenSolid()
	{
		var previous = MeshBoolean.Provider;

		try
		{
			MeshBoolean.Provider = new RefusingBoolean();

			var diagnostic = Caught( () =>
				MeshBoolean.Apply( BooleanOp.Subtract, Primitives.Plane( 2, 2 ), Primitives.Box( 1, 1, 1 ) ) );

			Check( "cutting with an open mesh is an error", diagnostic is { Severity: DiagnosticSeverity.Error } );
			Check( "naming the boundary",
				HasNumber( diagnostic?.Cause ) && diagnostic?.Cause.Contains( "boundary" ) == true,
				diagnostic?.Cause );
			Check( "with a remedy", diagnostic is { Remedies.Count: > 0 } );
		}
		finally
		{
			MeshBoolean.Provider = previous;
		}
	}

	static FeatureDiagnostic Caught( Action act )
	{
		try
		{
			act();
			return null;
		}
		catch ( FeatureException e )
		{
			return e.Diagnostic;
		}
	}

	sealed class RefusingBoolean : IMeshBoolean
	{
		public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
		{
			result = null;
			error = "engine said no";
			return false;
		}
	}

	static PartStudio StudioWithBox()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.Shape.Index = 0;
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;
		studio.Rebuild();
		return studio;
	}

	static bool HasNumber( string text )
	{
		if ( string.IsNullOrEmpty( text ) )
			return false;

		foreach ( var c in text )
		{
			if ( char.IsDigit( c ) )
				return true;
		}

		return false;
	}

	static bool Close( float a, float b, float eps = 1e-3f ) => MathF.Abs( a - b ) <= eps;

	static void Section( string title ) => Report.Section( title );
	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
