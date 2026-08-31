using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The PhysicsShapeList node, which is the step that had been missing since CollisionBuilder was
/// written: the shapes were right and the .vmdl carried none of them.
///
/// WHAT THESE CAN AND CANNOT CHECK. Whether the ENGINE reads a key is not decidable here, and it was
/// not guessed either — every key was probed by writing a .vmdl, compiling it, and reading the
/// compiled model's physics bounds back (VmdlPhysics' own comment records what each probe answered).
/// What is left for a headless test is everything downstream of that: the right key for the right
/// shape, the right NUMBER against it, and text a KV3 parser can actually read.
///
/// The number is not a formality. `dimensions` is a full size and CollisionShape.Size is a
/// half-extent, so the one line that doubles it is the difference between a part's collision and
/// half of it — and a wrong one compiles, loads, and is only ever noticed by someone walking
/// through a wall.
/// </summary>
public static class VmdlPhysicsTests
{
	/// <summary>
	/// A studio whose collision has a known, easily-measured answer: a 4-cube at the origin and a
	/// 2-cube out at x = 10, which together span 13 x 4 x 4.
	///
	/// Shared by the headless checks and by the sample file, so the number the compiler is asked to
	/// agree with is the same number the tests assert.
	/// </summary>
	internal static PartStudio TwoBoxes()
	{
		var studio = new PartStudio();

		var a = studio.Add( new PrimitiveFeature() );
		a.SizeX.Value = 4f;
		a.SizeY.Value = 4f;
		a.SizeZ.Value = 4f;

		var b = studio.Add( new PrimitiveFeature() );
		b.SizeX.Value = 2f;
		b.SizeY.Value = 2f;
		b.SizeZ.Value = 2f;
		b.Position.Value = new Vec3( 10, 0, 0 );

		studio.Rebuild();
		return studio;
	}

	public static void Run()
	{
		Section( "vmdl physics: each shape's own keys and numbers" );
		TestBox();
		TestSphere();
		TestCylinder();
		TestHull();

		Section( "vmdl physics: the node, and what it does with nothing" );
		TestNodeShape();
		TestNothingIsNothing();

		Section( "vmdl physics: numbers a KV3 parser can read" );
		TestNumberFormatting();

		Section( "vmdl physics: a studio's collision, end to end" );
		TestFromAStudio();
	}

	static void TestBox()
	{
		var text = VmdlPhysics.ShapeList( new[]
		{
			new CollisionShape
			{
				Kind = CollisionKind.Box,
				Position = new Vec3( 1, 2, 3 ),
				Size = new Vec3( 2, 3, 4 ),
			}
		} );

		Check( "a box is a PhysicsShapeBox", text.Contains( "_class = \"PhysicsShapeBox\"" ) );

		// THE DOUBLING. Size is half-extents, dimensions is the full size. Halved collision compiles
		// and loads and is invisible until something falls through it.
		Check( "dimensions is the FULL size, not the half-extents",
			text.Contains( "dimensions = [ 4.0, 6.0, 8.0 ]" ), Line( text, "dimensions" ) );

		// `origin`, and not the three keys that compile and are ignored.
		Check( "and it is placed by origin", text.Contains( "origin = [ 1.0, 2.0, 3.0 ]" ),
			Line( text, "origin" ) );
		Check( "not by center, translation or position, which the engine ignores on a box",
			!text.Contains( "center =" ) && !text.Contains( "translation =" ) && !text.Contains( "position =" ) );
	}

	static void TestSphere()
	{
		var text = VmdlPhysics.ShapeList( new[]
		{
			new CollisionShape
			{
				Kind = CollisionKind.Sphere,
				Position = new Vec3( 0, 0, 5 ),
				Size = new Vec3( 1.5f, 0, 0 ),
			}
		} );

		Check( "a sphere is a PhysicsShapeSphere", text.Contains( "_class = \"PhysicsShapeSphere\"" ) );
		Check( "with its radius", text.Contains( "radius = 1.5" ), Line( text, "radius" ) );

		// The one shape whose placement key is NOT the box's. Probed both ways round.
		Check( "and placed by center, which is the sphere's key and not the box's",
			text.Contains( "center = [ 0.0, 0.0, 5.0 ]" ), Line( text, "center" ) );
		Check( "and not by origin", !text.Contains( "origin =" ) );
	}

	static void TestCylinder()
	{
		var text = VmdlPhysics.ShapeList( new[]
		{
			new CollisionShape
			{
				Kind = CollisionKind.Cylinder,
				Position = new Vec3( 0, 0, 10 ),
				Size = new Vec3( 2f, 0f, 4f ),
			}
		} );

		Check( "a cylinder is a PhysicsShapeCylinder", text.Contains( "_class = \"PhysicsShapeCylinder\"" ) );
		Check( "with its radius", text.Contains( "radius = 2.0" ), Line( text, "radius" ) );

		// Size.z is the HALF height, so the two points sit one half-height either side of the centre.
		// Using the whole height for each would double the cylinder without changing anything a
		// compile could complain about.
		Check( "and its ends one half-height either side of the centre",
			text.Contains( "point0 = [ 0.0, 0.0, 6.0 ]" ) && text.Contains( "point1 = [ 0.0, 0.0, 14.0 ]" ),
			$"{Line( text, "point0" )} / {Line( text, "point1" )}" );
	}

	static void TestHull()
	{
		var cube = new List<Vec3>();

		foreach ( var x in new[] { -1f, 1f } )
		foreach ( var y in new[] { -1f, 1f } )
		foreach ( var z in new[] { -1f, 1f } )
			cube.Add( new Vec3( x, y, z ) );

		var text = VmdlPhysics.ShapeList( new[]
		{
			new CollisionShape { Kind = CollisionKind.Hull, Points = cube }
		} );

		Check( "a hull is a PhysicsShapeHull", text.Contains( "_class = \"PhysicsShapeHull\"" ) );
		Check( "carrying every one of its points", CountOf( text, "[ " ) >= 8,
			$"{CountOf( text, "[ " )} vectors" );
		Check( "under hull_vertices", text.Contains( "hull_vertices = " ) );

		// The points go in as they are. The probe that settled this wrote a cube offset along x and
		// measured 20 across, so nothing is being re-centred underneath.
		Check( "in model space, not re-centred", text.Contains( "[ -1.0, -1.0, -1.0 ]" )
			&& text.Contains( "[ 1.0, 1.0, 1.0 ]" ) );

		// A "hull" of three points is a triangle, which is not a solid. Better nothing than a
		// degenerate shape the physics engine has to decide what to do with.
		var flat = VmdlPhysics.ShapeList( new[]
		{
			new CollisionShape
			{
				Kind = CollisionKind.Hull,
				Points = new List<Vec3> { Vec3.Zero, new( 1, 0, 0 ), new( 0, 1, 0 ) },
			}
		} );

		Check( "and a hull of fewer than four points is dropped rather than written",
			flat.Length == 0, flat );
	}

	static void TestNodeShape()
	{
		var text = VmdlPhysics.ShapeList( new[]
		{
			new CollisionShape { Kind = CollisionKind.Box, Size = new Vec3( 1, 1, 1 ) },
			new CollisionShape { Kind = CollisionKind.Box, Position = new Vec3( 4, 0, 0 ), Size = new Vec3( 1, 1, 1 ) },
		} );

		Check( "one PhysicsShapeList holds them all", CountOf( text, "PhysicsShapeList" ) == 1,
			$"{CountOf( text, "PhysicsShapeList" )}" );
		Check( "with a child per shape", CountOf( text, "_class = \"PhysicsShapeBox\"" ) == 2 );

		// KV3 IS BRACE-SENSITIVE AND THE COMPILER'S MESSAGE FOR AN UNBALANCED ONE IS NOT KIND. This
		// is the same reason DmxGrammarTests parses its output rather than searching it.
		Check( "braces balance", CountOf( text, "{" ) == CountOf( text, "}" ),
			$"{CountOf( text, "{" )} open, {CountOf( text, "}" )} close" );
		Check( "and brackets balance", CountOf( text, "[" ) == CountOf( text, "]" ),
			$"{CountOf( text, "[" )} open, {CountOf( text, "]" )} close" );

		// It is spliced between a RootNode's other children, so it has to end the way they do.
		Check( "the node is a complete child entry, comma and all",
			text.TrimEnd( '\n' ).EndsWith( "}," ), text[^8..] );

		Check( "every shape names a surface and a collision tag",
			CountOf( text, "surface_prop" ) == 2 && CountOf( text, "collision_tags" ) == 2 );
	}

	static void TestNothingIsNothing()
	{
		// EMPTY, NOT AN EMPTY LIST. A PhysicsShapeList with no children is a model that says it has
		// collision and has none, which reads as a physics bug rather than as a missing step.
		Check( "no shapes writes no node", VmdlPhysics.ShapeList( new CollisionShape[0] ).Length == 0 );
		Check( "and neither does null", VmdlPhysics.ShapeList( null ).Length == 0 );

		var fallback = VmdlPhysics.MeshFromRender();

		Check( "the fallback node is the one this project already ships",
			fallback.Contains( "_class = \"PhysicsMeshFromRender\"" )
			&& fallback.Contains( "_class = \"PhysicsShapeList\"" ) );
	}

	/// <summary>
	/// The two ways a number can be written that KV3 will not read: with a comma for a decimal
	/// point, and in exponent form. Both are what .NET does by default under the right conditions,
	/// and neither fails loudly.
	/// </summary>
	static void TestNumberFormatting()
	{
		var text = VmdlPhysics.ShapeList( new[]
		{
			new CollisionShape
			{
				Kind = CollisionKind.Box,
				Position = new Vec3( 0.0000015f, -0f, 12345.75f ),
				Size = new Vec3( 0.5f, 1f, 2f ),
			}
		} );

		Check( "no exponent notation anywhere", !text.Contains( "E" ) && !text.Contains( "e+" ),
			Line( text, "origin" ) );
		Check( "every number has a decimal point", text.Contains( "12345.75" ) && text.Contains( "1.0" ) );
		Check( "and negative zero is written as zero", !text.Contains( "-0.0" ), Line( text, "origin" ) );

		// A machine with a comma decimal separator writes [ 1,5, 0,0, 0,0 ], which a KV3 parser reads
		// as six integers. Nothing about that fails at the point it happens.
		var culture = Thread.CurrentThread.CurrentCulture;

		try
		{
			Thread.CurrentThread.CurrentCulture = new CultureInfo( "de-DE" );

			var german = VmdlPhysics.ShapeList( new[]
			{
				new CollisionShape { Kind = CollisionKind.Box, Size = new Vec3( 0.75f, 1f, 1f ) }
			} );

			Check( "and a comma-decimal machine still writes a dot",
				german.Contains( "1.5" ) && !german.Contains( "1,5" ), Line( german, "dimensions" ) );
		}
		finally
		{
			Thread.CurrentThread.CurrentCulture = culture;
		}
	}

	/// <summary>
	/// The whole path: a studio, its collision, its node. Two boxes drawn apart come out as two
	/// PhysicsShapeBoxes in the right places and at the right size — which is the claim
	/// CollisionBuilder has been making since it was written and could not previously deliver on.
	/// </summary>
	static void TestFromAStudio()
	{
		var studio = TwoBoxes();
		var report = CollisionBuilder.Build( studio );

		Check( "the studio's collision is read from the history", report.FromHistory, report.Reason );

		var text = VmdlPhysics.ShapeList( report.Shapes );

		Check( "and comes out as two boxes", CountOf( text, "_class = \"PhysicsShapeBox\"" ) == 2,
			$"{CountOf( text, "_class = \"PhysicsShapeBox\"" )}" );
		Check( "the first at the origin, four across",
			text.Contains( "dimensions = [ 4.0, 4.0, 4.0 ]" ) && text.Contains( "origin = [ 0.0, 0.0, 0.0 ]" ) );
		Check( "the second out at x = 10, two across",
			text.Contains( "dimensions = [ 2.0, 2.0, 2.0 ]" ) && text.Contains( "origin = [ 10.0, 0.0, 0.0 ]" ),
			Line( text, "origin" ) );

		// And the fallback path still produces something writable: a subdivide spoils the history,
		// so this becomes hulls, and hulls still have to make a node.
		studio.Add( new SubdivideFeature() ).Levels.Value = 1;
		studio.Rebuild();

		var hulled = CollisionBuilder.Build( studio );
		var hullText = VmdlPhysics.ShapeList( hulled.Shapes );

		Check( "a spoiled history still writes shapes, as hulls", !hulled.FromHistory
			&& hullText.Contains( "_class = \"PhysicsShapeHull\"" ), hulled.ToString() );
	}

	/// <summary>
	/// A complete .vmdl carrying this kernel's own collision output, written for the same reason the
	/// sample DMX and the sample normal map are: the verdict that matters is somewhere else.
	///
	/// The checks above prove the text says what it should. Only the engine can say whether the
	/// engine agrees, and the way to ask is:
	///
	///     copy out/sample_physics.{obj,vmdl} into Assets/models/effigy_probe/
	///     register_external_assets, asset_compile, then kit_validate that folder
	///
	/// TwoBoxes spans 13 x 4 x 4 — a 4-cube at the origin and a 2-cube whose far face is at x = 11 —
	/// so the answer is one number and it is not a matter of opinion. BOTH bounds have to read it:
	/// PhysicsBounds alone says the shapes are right, and Bounds alone says the mesh is where it was
	/// drawn, and the pair of them agreeing is the only thing that says the collision is on the
	/// model rather than beside it. Run on 2026-08-31 it gave 13 x 4 x 4 for both.
	///
	/// The failures each look like something specific. Halved boxes read 11 x 4 x 4 (a `dimensions`
	/// taken as half-extents). Shapes stacked at the origin read 4 x 4 x 4 (a placement key the
	/// engine ignores). And 13 x 13 x 4 is the mesh and the shapes at ninety degrees to each other,
	/// which is what the import_rotation below is there to stop.
	/// </summary>
	internal static void WriteSample( string outDir )
	{
		var studio = TwoBoxes();
		var report = CollisionBuilder.Build( studio );
		var physics = VmdlPhysics.ShapeList( report.Shapes );

		ObjWriter.WriteFile( studio.ToMesh(), Path.Combine( outDir, "sample_physics.obj" ), "sample_physics" );

		var vmdl =
			"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n"
			+ "{\n\trootNode = \n\t{\n\t\t_class = \"RootNode\"\n\t\tchildren = \n\t\t[\n"
			+ "\t\t\t{\n\t\t\t\t_class = \"RenderMeshList\"\n\t\t\t\tchildren = \n\t\t\t\t[\n"
			+ "\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"RenderMeshFile\"\n\t\t\t\t\t\tname = \"Body_LOD0\"\n"
			+ "\t\t\t\t\t\tchildren = \n\t\t\t\t\t\t[\n\t\t\t\t\t\t]\n"
			+ "\t\t\t\t\t\tfilename = \"models/effigy_probe/sample_physics.obj\"\n"
			+ "\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n"
			// The -90 yaw the editor's own BuildVmdl writes, and for the reason given there:
			// ModelDoc's OBJ importer turns the mesh a quarter turn, and the physics shapes are in
			// the file's own coordinates. Without it this sample compiles with its collision at
			// ninety degrees to its mesh, and the bounds read 13 x 13 x 4 instead of 13 x 4 x 4.
			+ "\t\t\t\t\t\timport_rotation = [ 0.0, -90.0, 0.0 ]\n"
			+ "\t\t\t\t\t\timport_scale = 1.0\n"
			+ "\t\t\t\t\t\talign_origin_x_type = \"None\"\n"
			+ "\t\t\t\t\t\talign_origin_y_type = \"None\"\n"
			+ "\t\t\t\t\t\talign_origin_z_type = \"None\"\n"
			+ "\t\t\t\t\t\tparent_bone = \"\"\n\t\t\t\t\t},\n\t\t\t\t]\n\t\t\t},\n"
			+ physics
			+ "\t\t]\n\t\tmodel_archetype = \"\"\n\t\tprimary_associated_entity = \"\"\n"
			+ "\t\tanim_graph_name = \"\"\n\t\tbase_model_name = \"\"\n\t}\n}\n";

		File.WriteAllText( Path.Combine( outDir, "sample_physics.vmdl" ), vmdl );

		Check( $"wrote {outDir}/sample_physics.vmdl - compile it and its physics bounds should read 13 x 4 x 4",
			physics.Length > 0 && report.FromHistory, report.ToString() );
	}

	// --- helpers ----------------------------------------------------------------------------------

	static int CountOf( string text, string needle )
	{
		var count = 0;
		var at = 0;

		while ( (at = text.IndexOf( needle, at, StringComparison.Ordinal )) >= 0 )
		{
			count++;
			at += needle.Length;
		}

		return count;
	}

	/// <summary>The first line naming a key, so a failure prints what was actually written.</summary>
	static string Line( string text, string key )
	{
		foreach ( var line in text.Split( '\n' ) )
		{
			if ( line.Contains( key + " =", StringComparison.Ordinal ) )
				return line.Trim();
		}

		return $"no '{key}' line";
	}
}
