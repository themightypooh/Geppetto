using System;
using System.Collections.Generic;
using System.Linq;
using ModelKernel;

namespace ModelKernel.Tests;

/// <summary>
/// Verification for bevel.
///
/// The anchor here is an exact closed-form volume. Chamfering a cube of side s by distance d cuts a
/// triangular prism from each of the 12 edges and leaves a tetrahedral remnant at each of the 8
/// corners, giving — for s = 2 —
///
///     V(d) = 8 - 12d^2 + (16/3)d^3
///
/// which is worth a sanity check at its limit: at d = 1 the inset squares shrink to points and the
/// solid becomes the octahedron with vertices at the face centres. The formula gives
/// 8 - 12 + 16/3 = 4/3, and that octahedron's volume is exactly 4/3. A formula that lands on a
/// known solid at its boundary is a formula worth testing against.
/// </summary>
public static class BevelTests
{
	public static void Run()
	{
		Section( "bevel: structure" );
		TestStructure();

		Section( "bevel: the inset is exact" );
		TestExactness();

		Section( "bevel: volume matches the closed form" );
		TestVolume();

		Section( "bevel: other shapes and the tree" );
		TestShapesAndFeature();

		Section( "bevel: refusals" );
		TestRefusals();
	}

	/// <summary>Volume of a 2x2x2 cube chamfered by d. See the class note.</summary>
	static float ChamferedCubeVolume( float d ) => 8f - 12f * d * d + 16f / 3f * d * d * d;

	// --- structure ----------------------------------------------------------------------

	static void TestStructure()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var bevelled = BevelOperation.Bevel( box, 0.2f );

		var validation = MeshValidator.Validate( bevelled );
		Check( "bevelled box is valid", validation.IsValid, validation.ToString() );
		Check( "bevelled box is closed", validation.IsClosed, validation.ToString() );
		Check( "Euler characteristic is still 2", MeshValidator.EulerCharacteristic( bevelled ) == 2,
			$"{MeshValidator.EulerCharacteristic( bevelled )}" );

		// 6 inset faces + 12 edge strips + 8 corner caps.
		Check( "a box becomes exactly 26 faces", bevelled.FaceCount == 26, $"{bevelled.FaceCount}" );

		// Every new vertex comes from exactly one face corner: 6 faces x 4 corners.
		Check( "and exactly 24 vertices", bevelled.VertexCount == 24, $"{bevelled.VertexCount}" );

		var quads = bevelled.Faces.Count( f => f.Count == 4 );
		var triangles = bevelled.Faces.Count( f => f.Count == 3 );

		Check( "18 quads: 6 inset faces plus 12 edge strips", quads == 18, $"{quads}" );
		Check( "8 triangles, one per corner", triangles == 8, $"{triangles}" );

		// Worth stating plainly: bevel is the one operation here that introduces triangles, because
		// a valence-3 vertex caps with a triangle. They subdivide into 3 quads on the first level.
		Check( "so a bevelled mesh is not all quads, by construction", quads + triangles == bevelled.FaceCount );

		var subdivided = CatmullClark.Subdivide( bevelled, 2 );
		Check( "a bevelled box subdivides cleanly", MeshValidator.EulerCharacteristic( subdivided ) == 2 );
		Check( "and is all quads afterwards", subdivided.Faces.All( f => f.Count == 4 ) );
		Check( "and still encloses positive volume", Volume( subdivided ) > 0f, $"{Volume( subdivided ):0.####}" );
	}

	// --- exactness ----------------------------------------------------------------------

	static void TestExactness()
	{
		const float d = 0.2f;

		foreach ( var (name, mesh) in Cases() )
		{
			var bevelled = BevelOperation.Bevel( mesh, d );

			// 1. An inset face must stay in its original plane. Insetting happens WITHIN the face, so
			//    any drift off the plane means the corner solve leaked into the normal direction.
			var worstPlane = 0f;
			var cursor = 0;

			for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			{
				var face = mesh.Faces[fi];
				var normal = mesh.FaceNormal( face );
				var origin = mesh.Positions[face.Indices[0]];

				for ( var i = 0; i < face.Count; i++ )
					worstPlane = MathF.Max( worstPlane, MathF.Abs( Vec3.Dot( bevelled.Positions[cursor + i] - origin, normal ) ) );

				cursor += face.Count;
			}

			Check( $"{name}: inset faces stay exactly in their original planes", worstPlane < 1e-4f,
				$"drifted {worstPlane:0.#######}" );

			// 2. THE DEFINING PROPERTY: each inset corner sits exactly `d` from both of the original
			//    edges that met there. Everything else about bevel follows from this.
			var worstEdge = 0f;
			cursor = 0;

			for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			{
				var face = mesh.Faces[fi];
				var n = face.Count;

				for ( var i = 0; i < n; i++ )
				{
					var v = mesh.Positions[face.Indices[i]];
					var prev = mesh.Positions[face.Indices[(i - 1 + n) % n]];
					var next = mesh.Positions[face.Indices[(i + 1) % n]];
					var p = bevelled.Positions[cursor + i];

					worstEdge = MathF.Max( worstEdge, MathF.Abs( DistanceToLine( p, prev, v ) - d ) );
					worstEdge = MathF.Max( worstEdge, MathF.Abs( DistanceToLine( p, v, next ) - d ) );
				}

				cursor += n;
			}

			Check( $"{name}: every inset corner is exactly {d} from both its edges", worstEdge < 1e-4f,
				$"worst error {worstEdge:0.#######}" );
		}
	}

	// --- volume -------------------------------------------------------------------------

	static void TestVolume()
	{
		foreach ( var d in new[] { 0.1f, 0.25f, 0.5f, 0.9f } )
		{
			var bevelled = BevelOperation.Bevel( Primitives.Box( 2, 2, 2 ), d );
			var expected = ChamferedCubeVolume( d );

			Check( $"d={d}: volume is {expected:0.####}", Near( Volume( bevelled ), expected, 1e-3f ),
				$"{Volume( bevelled ):0.#####} vs {expected:0.#####}" );
		}

		// The limit case, as an independent anchor on the formula: at d just under 1 the solid is
		// nearly the octahedron with vertices at the cube's face centres, whose volume is 4/3.
		var nearLimit = BevelOperation.Bevel( Primitives.Box( 2, 2, 2 ), 0.999f );
		Check( "at the limit it approaches the octahedron's 4/3",
			MathF.Abs( Volume( nearLimit ) - 4f / 3f ) < 0.02f, $"{Volume( nearLimit ):0.#####}" );

		// Monotonic, and never inverted.
		var previous = float.MaxValue;
		var monotonic = true;

		for ( var d = 0.05f; d < 0.9f; d += 0.05f )
		{
			var v = Volume( BevelOperation.Bevel( Primitives.Box( 2, 2, 2 ), d ) );

			if ( v >= previous || v <= 0f )
				monotonic = false;

			previous = v;
		}

		Check( "volume falls monotonically and stays positive as the bevel grows", monotonic );
	}

	// --- other shapes -------------------------------------------------------------------

	static void TestShapesAndFeature()
	{
		foreach ( var (name, mesh) in Cases() )
		{
			var bevelled = BevelOperation.Bevel( mesh, 0.15f );
			var validation = MeshValidator.Validate( bevelled );

			Check( $"{name}: bevels to a valid closed solid", validation.IsValid && validation.IsClosed,
				validation.ToString() );
			Check( $"{name}: keeps positive volume", Volume( bevelled ) > 0f, $"{Volume( bevelled ):0.####}" );
			Check( $"{name}: is smaller than the original", Volume( bevelled ) < Volume( mesh ) );
		}

		// In the tree, on top of a sketch — the whole point being that both stay editable.
		var studio = new PartStudio();

		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( -1, -1 ), new Vec2( 1, 1 ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 2f;

		var bevel = studio.Add( new BevelFeature() );
		bevel.Distance.Value = 0.2f;

		studio.Rebuild();

		Check( "bevel feature runs", bevel.Error is null, bevel.Error );
		Check( "and produces the chamfered-cube volume",
			Near( Volume( studio.Bodies[0].Mesh ), ChamferedCubeVolume( 0.2f ), 1e-3f ),
			$"{Volume( studio.Bodies[0].Mesh ):0.#####}" );

		bevel.Distance.Value = 0.4f;
		studio.MarkDirty( bevel );
		studio.Rebuild();

		Check( "changing the distance later re-bevels from the clean cage",
			Near( Volume( studio.Bodies[0].Mesh ), ChamferedCubeVolume( 0.4f ), 1e-3f ),
			$"{Volume( studio.Bodies[0].Mesh ):0.#####}" );

		// That last check is the argument for parametric in one line: a destructive modeller would
		// have bevelled the already-bevelled result and produced something else entirely.
	}

	// --- refusals -----------------------------------------------------------------------

	static void TestRefusals()
	{
		var box = Primitives.Box( 2, 2, 2 );

		var threw = false;
		try { BevelOperation.Bevel( box, 0f ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "zero distance is refused", threw );

		// A 2-wide face can take just under 1 before its inset edges cross. Note that checking the
		// face NORMAL cannot catch this on a rectangle: over-insetting point-reflects the face, and
		// a 180-degree rotation preserves orientation. It is caught per edge instead.
		threw = false;
		try { BevelOperation.Bevel( box, 1.5f ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "a bevel larger than the feature is refused, not returned self-intersecting", threw );

		threw = false;
		try { BevelOperation.Bevel( box, 1.0f ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "and exactly at the collapse point too", threw );

		Check( "while just under it still works",
			MeshValidator.Validate( BevelOperation.Bevel( box, 0.995f ) ).IsClosed );

		threw = false;
		try { BevelOperation.Bevel( Primitives.Plane( 2, 2, 2, 2 ), 0.1f ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "an open mesh is refused", threw );

		// A shell is two separate closed surfaces, which is a legitimate thing to bevel.
		var shelled = ShellOperation.Shell( box, 0.2f );
		var bevelledShell = BevelOperation.Bevel( shelled, 0.05f );
		Check( "a hollow solid bevels on both surfaces",
			MeshValidator.Validate( bevelledShell ).IsClosed,
			MeshValidator.Validate( bevelledShell ).ToString() );
		Check( "and stays two shells", MeshValidator.EulerCharacteristic( bevelledShell ) == 4,
			$"{MeshValidator.EulerCharacteristic( bevelledShell )}" );
	}

	// --- helpers ------------------------------------------------------------------------

	static IEnumerable<(string Name, PolyMesh Mesh)> Cases()
	{
		yield return ("box", Primitives.Box( 2, 2, 2 ));
		yield return ("cylinder", Primitives.Cylinder( 1f, 2f, 12 ));
		yield return ("wedge", Primitives.Wedge( 2, 2, 2 ));
		yield return ("extrusion", ExtrudedRectangle( 3f, 2f, 2f ));
	}

	/// <summary>Perpendicular distance from a point to the infinite line through a and b.</summary>
	static float DistanceToLine( Vec3 p, Vec3 a, Vec3 b )
	{
		var direction = (b - a).Normal;
		var offset = p - a;
		return (offset - direction * Vec3.Dot( offset, direction )).Length;
	}


	/// <summary>
	/// Build a solid by extruding a rectangle, through the public feature path.
	///
	/// The sketcher owns extrude now, so these tests go through the tree rather than calling a
	/// mesh builder directly. That is the better test anyway: it exercises the path a user takes.
	/// </summary>
	static PolyMesh ExtrudedRectangle( float width, float depth, float height )
	{
		var studio = new PartStudio();

		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( -width / 2f, -depth / 2f ), new Vec2( width / 2f, depth / 2f ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = height;
		studio.Rebuild();

		return studio.Bodies[0].Mesh;
	}

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
