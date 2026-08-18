using System;
using System.Collections.Generic;
using System.Linq;
using ModelKernel;

namespace ModelKernel.Tests;

/// <summary>
/// Verification for shell.
///
/// THE CENTRAL TEST IS PLANE-TO-PLANE DISTANCE, and the reason is worth stating: it would be easy
/// and wrong to assert that each vertex moved by the thickness. At a box corner the correct answer
/// moves the vertex by thickness*sqrt(3) — asserting otherwise would enshrine the exact bug this
/// implementation exists to avoid. Wall thickness is a property of planes, so the tests measure
/// planes.
/// </summary>
public static class ShellTests
{
	public static void Run()
	{
		Section( "shell: wall thickness is exact" );
		TestThickness();

		Section( "shell: topology and volume" );
		TestTopology();

		Section( "shell: openings" );
		TestOpenings();

		Section( "shell: in the feature tree" );
		TestFeature();
	}

	// --- the important one --------------------------------------------------------------

	/// <summary>
	/// For every face, every one of its vertices must sit exactly `thickness` from the face's own
	/// plane after offsetting. That is the definition of wall thickness, and it holds regardless of
	/// how far any individual vertex travelled.
	/// </summary>
	static float WorstPlaneError( PolyMesh original, PolyMesh shelled, float thickness )
	{
		var worst = 0f;
		var offset = original.VertexCount;

		for ( var fi = 0; fi < original.FaceCount; fi++ )
		{
			var face = original.Faces[fi];
			var n = original.FaceNormal( face );

			foreach ( var vi in face.Indices )
			{
				var outer = original.Positions[vi];
				var inner = shelled.Positions[vi + offset];

				// Distance from the inner point to the outer face's plane, along that plane's normal.
				var distance = Vec3.Dot( n, outer - inner );
				worst = MathF.Max( worst, MathF.Abs( distance - thickness ) );
			}
		}

		return worst;
	}

	static void TestThickness()
	{
		const float t = 0.1f;

		var cases = new (string Name, PolyMesh Mesh)[]
		{
			("box", Primitives.Box( 2, 2, 2 )),
			("cylinder", Primitives.Cylinder( 1f, 2f, 16 )),
			("wedge", Primitives.Wedge( 2, 2, 2 )),
			("extruded rounded rect", SketchSolids.Extrude(
				new Sketch( SketchPlane.XY, Profile.RoundedRectangle( 4, 2, 0.5f, 3 ) ), 2f )),
			("revolved ring", SketchSolids.Revolve(
				new Sketch( SketchPlane.XY, new Profile( Profile.Rectangle( 2f, 1f ).Points.Select( p => new Vec2( p.x + 3f, p.y ) ) ) ),
				Vec2.Zero, new Vec2( 0, 1 ), 360f, 16 ))
		};

		foreach ( var (name, mesh) in cases )
		{
			var shelled = ShellOperation.Shell( mesh, t, null, out var approximated );
			var worst = WorstPlaneError( mesh, shelled, t );

			Check( $"{name}: every wall is exactly {t} thick", worst < 1e-4f, $"worst error {worst:0.#######}" );
			Check( $"{name}: no vertex needed the fallback", approximated == 0, $"{approximated} approximated" );
		}

		// The specific case the naive implementation gets wrong. A box corner's normal is
		// (1,1,1)/sqrt(3), so the correct offset moves it t*sqrt(3) - NOT t.
		var box = Primitives.Box( 2, 2, 2 );
		var boxShell = ShellOperation.Shell( box, t );
		var corner = box.Positions[0];
		var innerCorner = boxShell.Positions[box.VertexCount];
		var travelled = (corner - innerCorner).Length;

		Check( "a box corner moves thickness*sqrt(3), not thickness",
			MathF.Abs( travelled - t * MathF.Sqrt( 3f ) ) < 1e-5f,
			$"moved {travelled:0.#####}, naive would be {t}" );

		Check( "and lands on exactly +-0.9 in every axis",
			MathF.Abs( MathF.Abs( innerCorner.x ) - 0.9f ) < 1e-5f
			&& MathF.Abs( MathF.Abs( innerCorner.y ) - 0.9f ) < 1e-5f
			&& MathF.Abs( MathF.Abs( innerCorner.z ) - 0.9f ) < 1e-5f,
			innerCorner.ToString() );

		// A faceted cylinder is a prism, and its SIDE FACES sit at the apothem r*cos(pi/n), not at
		// r. Offsetting those planes inward by t and reading the resulting vertex radius gives a
		// closed form:
		//
		//     r_inner = ( r*cos(theta) - t ) / cos(theta)  =  r - t/cos(theta)
		//
		// which is strictly LESS than r - t, because a prism's vertices always travel further than
		// its faces. Asserting r - t here would be asserting a misunderstanding of the geometry, so
		// the exact expression is checked instead.
		const int segments = 16;
		var cyl = Primitives.Cylinder( 1f, 2f, segments );
		var cylShell = ShellOperation.Shell( cyl, t );
		var first = cylShell.Positions[cyl.VertexCount];
		var innerRadius = MathF.Sqrt( first.x * first.x + first.y * first.y );

		var cosTheta = MathF.Cos( MathF.PI / segments );
		var predicted = 1f - t / cosTheta;

		Check( "a prism's inner radius matches r - t/cos(pi/n) exactly",
			MathF.Abs( innerRadius - predicted ) < 1e-5f,
			$"{innerRadius:0.######} vs predicted {predicted:0.######}" );

		Check( "which is strictly tighter than a naive r - t", innerRadius < 1f - t,
			$"{innerRadius:0.######} vs {1f - t:0.######}" );
	}

	// --- topology and volume ------------------------------------------------------------

	static void TestTopology()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var shelled = ShellOperation.Shell( box, 0.1f );

		var validation = MeshValidator.Validate( shelled );
		Check( "sealed shell is valid", validation.IsValid, validation.ToString() );
		Check( "sealed shell is closed", validation.IsClosed, validation.ToString() );

		// Two separate closed surfaces, each with Euler characteristic 2.
		Check( "two shells give Euler characteristic 4", MeshValidator.EulerCharacteristic( shelled ) == 4,
			$"{MeshValidator.EulerCharacteristic( shelled )}" );
		Check( "face count doubles", shelled.FaceCount == box.FaceCount * 2 );

		// 2^3 outer minus 1.8^3 inner. The inner surface is wound inside-out, so it subtracts.
		var expected = 8f - 1.8f * 1.8f * 1.8f;
		Check( "enclosed volume is exactly the wall material", Near( Volume( shelled ), expected, 1e-4f ),
			$"{Volume( shelled ):0.######} vs {expected:0.######}" );

		// Shelling twice as thick must remove strictly more material.
		var thicker = ShellOperation.Shell( box, 0.2f );
		var thickerExpected = 8f - 1.6f * 1.6f * 1.6f;
		Check( "thicker walls enclose more material", Volume( thicker ) > Volume( shelled ) );
		Check( "and the amount is exact", Near( Volume( thicker ), thickerExpected, 1e-4f ),
			$"{Volume( thicker ):0.######} vs {thickerExpected:0.######}" );

		var subdivided = CatmullClark.Subdivide( shelled, 1 );
		Check( "a shell subdivides without corrupting topology",
			MeshValidator.EulerCharacteristic( subdivided ) == 4 );
		Check( "and stays positive volume", Volume( subdivided ) > 0f, $"{Volume( subdivided ):0.####}" );

		var threw = false;
		try { ShellOperation.Shell( box, 0f ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "zero thickness is refused", threw );

		threw = false;
		try { ShellOperation.Shell( Primitives.Plane( 1, 1, 2, 2 ), 0.1f ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "an open mesh cannot be shelled", threw );

		threw = false;
		try { ShellOperation.Shell( box, 0.1f, Enumerable.Range( 0, box.FaceCount ) ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "opening every face is refused", threw );
	}

	// --- openings -----------------------------------------------------------------------

	static void TestOpenings()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var open = ShellOperation.Shell( box, 0.1f, new[] { 0 } );

		var validation = MeshValidator.Validate( open );
		Check( "an opened shell is still valid", validation.IsValid, validation.ToString() );
		Check( "an opened shell is still closed — the rim seals it", validation.IsClosed, validation.ToString() );

		// Outer sheet + inner sheet + a rim joining their borders is topologically a sphere.
		Check( "an open box is genus 0", MeshValidator.EulerCharacteristic( open ) == 2,
			$"{MeshValidator.EulerCharacteristic( open )}" );

		// 5 outer + 5 inner + 4 rim quads.
		Check( "faces are 5 + 5 + a 4-quad rim", open.FaceCount == 14, $"{open.FaceCount}" );

		Check( "the rim faces outward, so volume stays positive", Volume( open ) > 0f,
			$"{Volume( open ):0.####}" );
		Check( "and encloses less than the sealed version",
			Volume( open ) < Volume( ShellOperation.Shell( box, 0.1f ) ),
			$"{Volume( open ):0.####}" );

		// Two openings, at opposite ends: a tube. Still one closed surface, and now genus 1.
		var tube = ShellOperation.Shell( box, 0.1f, new[] { 0, 1 } );
		Check( "a shell opened at both ends is closed", MeshValidator.Validate( tube ).IsClosed,
			MeshValidator.Validate( tube ).ToString() );
		Check( "and is genus 1, being a tube", MeshValidator.EulerCharacteristic( tube ) == 0,
			$"{MeshValidator.EulerCharacteristic( tube )}" );
		Check( "with positive volume", Volume( tube ) > 0f, $"{Volume( tube ):0.####}" );
	}

	// --- the tree -----------------------------------------------------------------------

	static void TestFeature()
	{
		var studio = new PartStudio();

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Sketch.Value = new Sketch( SketchPlane.XY, Profile.Rectangle( 4f, 4f ) );
		extrude.Distance.Value = 3f;

		var shell = studio.Add( new ShellFeature() );
		shell.Thickness.Value = 0.25f;

		studio.Rebuild();

		Check( "shell feature runs", shell.Error is null, shell.Error );
		Check( "the room is hollow", Volume( studio.Bodies[0].Mesh ) < 4f * 4f * 3f );
		Check( "and encloses positive volume", Volume( studio.Bodies[0].Mesh ) > 0f );

		// Exact: 4x4x3 outer, 3.5x3.5x2.5 inner.
		var expected = 4f * 4f * 3f - 3.5f * 3.5f * 2.5f;
		Check( "with exactly the right wall material", Near( Volume( studio.Bodies[0].Mesh ), expected, 1e-3f ),
			$"{Volume( studio.Bodies[0].Mesh ):0.####} vs {expected:0.####}" );

		// The whole point of the tree: change the room's size, the walls follow.
		extrude.Sketch.Value.Outer = Profile.Rectangle( 6f, 4f );
		studio.MarkDirty( extrude );
		studio.Rebuild();

		var grown = 6f * 4f * 3f - 5.5f * 3.5f * 2.5f;
		Check( "resizing the room keeps the walls exact", Near( Volume( studio.Bodies[0].Mesh ), grown, 1e-3f ),
			$"{Volume( studio.Bodies[0].Mesh ):0.####} vs {grown:0.####}" );

		// And changing only the thickness must not disturb the outside.
		shell.Thickness.Value = 0.5f;
		studio.MarkDirty( shell );
		studio.Rebuild();

		var thicker = 6f * 4f * 3f - 5f * 3f * 2f;
		Check( "and changing thickness alone is exact too", Near( Volume( studio.Bodies[0].Mesh ), thicker, 1e-3f ),
			$"{Volume( studio.Bodies[0].Mesh ):0.####} vs {thicker:0.####}" );
	}

	// --- helpers ------------------------------------------------------------------------

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

	static void Section( string title ) => Report.Section( title );
	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
