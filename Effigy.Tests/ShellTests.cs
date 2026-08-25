using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

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

		Section( "shell: regressions from review" );
		TestReviewRegressions();

		Section( "shell: thicker than the shape itself" );
		TestTooThick();
	}

	/// <summary>
	/// Shelling a shape by more than it is thick.
	///
	/// This used to produce a closed mesh with its inner surface passed through itself: negative
	/// volume in places, entirely normal from outside, and nothing said a word. It is refused now,
	/// by two checks that catch different failures — see RefuseIfFolded for why one is not enough.
	/// </summary>
	static void TestTooThick()
	{
		// A 4x4x1 plate, shelled by 0.6. The top and bottom walls pass straight through each other.
		//
		// NOTE WHAT DOES NOT HAPPEN HERE: neither face inverts. Each is translated inward by 0.6 and
		// a translation preserves a normal, so a per-face check sees nothing wrong. The surface as a
		// whole is what turns inside out, and only the enclosed volume sees that.
		var plate = Primitives.Box( 4, 4, 1 );
		var message = Refused( () => ShellOperation.Shell( plate, 0.6f ) );

		Check( "shelling a plate by more than half its thickness is refused", message is not null,
			"it produced a mesh" );

		Check( "and the message names the thickness that failed",
			message is not null && message.Contains( "0.6" ), message ?? "" );

		// Exactly half is the boundary: the two walls meet with no cavity between them. Zero
		// volume is as useless as negative and is refused the same way.
		Check( "so is shelling it by exactly half", Refused( () => ShellOperation.Shell( plate, 0.5f ) ) is not null,
			"it produced a mesh" );

		// Just under has to still work, or the guard is too eager and the parameter is unusable near
		// the values anyone would actually pick.
		var thin = ShellOperation.Shell( plate, 0.45f );

		Check( "while just under it still shells", thin is not null && thin.FaceCount > 0 );

		Check( "into a valid closed mesh",
			MeshValidator.Validate( thin ) is { IsValid: true, IsClosed: true } );

		// A CYLINDER SHELLED PAST ITS RADIUS is the other failure, and the one the per-face check is
		// there for: the side faces invert around the axis rather than the surface flipping bodily.
		var cylinder = Primitives.Cylinder( 0.5f, 4f, 16 );

		Check( "shelling a cylinder by more than its radius is refused",
			Refused( () => ShellOperation.Shell( cylinder, 0.8f ) ) is not null, "it produced a mesh" );

		Check( "and a sane wall on the same cylinder still works",
			ShellOperation.Shell( cylinder, 0.1f ) is { FaceCount: > 0 } );

		// An opened shell skips the volume check, since its inner surface is genuinely not closed.
		// It must still shell normally rather than being caught by the remaining per-face test.
		var room = ShellOperation.Shell( Primitives.Box( 4, 4, 4 ), 0.3f, new[] { 0 } );

		Check( "opening a face still shells normally", room is not null && room.FaceCount > 0 );
	}

	static string Refused( Func<PolyMesh> action )
	{
		try
		{
			action();
			return null;
		}
		catch ( InvalidOperationException e )
		{
			return e.Message;
		}
	}

	/// <summary>
	/// Every one of these is a bug that shipped and was caught by review rather than by the suite.
	/// They are kept together deliberately: the tests above check that shell is right, and these
	/// check the specific ways it was wrong.
	/// </summary>
	static void TestReviewRegressions()
	{
		// 1. Openings that pinch to a point produced a non-manifold mesh - an edge shared by four
		//    faces - because four rim quads all contained the same outer-to-inner edge.
		var subdivided = CatmullClark.Subdivide( Primitives.Box( 2, 2, 2 ), 1 );
		var threw = false;

		try { ShellOperation.Shell( subdivided, 0.05f, new[] { 0, 2 } ); }
		catch ( InvalidOperationException ) { threw = true; }

		Check( "an opening pinched at a vertex is refused, not returned broken", threw );

		// ...but openings that share an edge are perfectly legal and must still work.
		var adjacent = ShellOperation.Shell( subdivided, 0.05f, new[] { 0, 1 } );
		Check( "two openings sharing an edge still shell cleanly",
			MeshValidator.Validate( adjacent ).IsValid && MeshValidator.Validate( adjacent ).IsClosed,
			MeshValidator.Validate( adjacent ).ToString() );

		// 2. Opened faces were still constraining the vertex solve, so the wall was pulled back by
		//    the thickness and the rim came out as a 45-degree chamfer instead of a flat band.
		var box = Primitives.Box( 2, 2, 2 );
		var opened = ShellOperation.Shell( box, 0.1f, new[] { 0 } );
		var openedNormal = box.FaceNormal( box.Faces[0] );
		var worstDrift = 0f;

		foreach ( var vi in box.Faces[0].Indices )
		{
			// A vertex on the rim must stay exactly in the opened face's plane, not retreat from it.
			var outer = box.Positions[vi];
			var inner = opened.Positions[vi + box.VertexCount];
			worstDrift = MathF.Max( worstDrift, MathF.Abs( Vec3.Dot( openedNormal, outer - inner ) ) );
		}

		Check( "the rim stays flush with the opened face", worstDrift < 1e-5f, $"drifted {worstDrift:0.######}" );
		Check( "so an opened box encloses exactly 1.844", Near( Volume( opened ), 1.844f, 1e-3f ),
			$"{Volume( opened ):0.####}" );

		// 3. The rank-deficient fallback divided by the LARGEST cosine where it needed the smallest,
		//    making walls too thin wherever one flat face was split into several coplanar polygons -
		//    which subdivision does to every face.
		var split = ShellOperation.Shell( subdivided, 0.1f, null, out var approximated );
		Check( "coplanar-split faces still give exact thickness",
			WorstPlaneError( subdivided, split, 0.1f ) < 1e-4f,
			$"worst {WorstPlaneError( subdivided, split, 0.1f ):0.#######}" );
		Check( "and need no approximation at all", approximated == 0, $"{approximated} approximated" );

		// 3b. On a CURVED surface there is no point exactly `thickness` from every neighbouring face
		//     plane, because the neighbours disagree. The normal equations return a least-squares
		//     compromise regardless and used to report it as exact, making the approximated count
		//     meaningless. It is now measured against the residual.
		var curved = CatmullClark.Subdivide( Primitives.Box( 2, 2, 2 ), 2 );
		ShellOperation.Shell( curved, 0.1f, null, out var curvedApprox );

		Check( "a curved surface honestly reports vertices it cannot solve exactly", curvedApprox > 0,
			$"{curvedApprox} approximated" );
		Check( "but the answer stays the least-squares one, so the error stays small",
			WorstPlaneError( curved, ShellOperation.Shell( curved, 0.1f ), 0.1f ) < 1e-3f,
			$"worst {WorstPlaneError( curved, ShellOperation.Shell( curved, 0.1f ), 0.1f ):0.#######}" );

		// 4. Shelling silently threw away a rig, because the result rebuilt the vertex list without
		//    rebuilding the weights alongside it.
		var rigged = Primitives.Box( 2, 2, 2 );
		var skeleton = new Skeleton();
		skeleton.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -1 ), new Vec3( 0, 0, 1 ) );
		rigged.Skin = SkinBinder.BindRigid( rigged, skeleton );

		var riggedShell = ShellOperation.Shell( rigged, 0.1f );
		Check( "a rigged body survives shelling", riggedShell.IsRigged );
		Check( "with valid weights", riggedShell.Skin.Validate( riggedShell.VertexCount, skeleton.Count ).Count == 0 );
		Check( "and the inner surface inherits the outer's weights",
			Enumerable.Range( 0, rigged.VertexCount ).All( i =>
				riggedShell.Skin[i].Length == riggedShell.Skin[i + rigged.VertexCount].Length
				&& riggedShell.Skin[i][0].Bone == riggedShell.Skin[i + rigged.VertexCount][0].Bone ) );

		// 5. The feature mutated bodies in place, so a throw partway left earlier bodies shelled -
		//    breaking Feature.Run's promise that a failed feature changes nothing.
		var studio = new PartStudio();

		var cylinder = studio.Add( new PrimitiveFeature() );
		cylinder.Shape.Index = 1;
		cylinder.Segments.Value = 16;

		var boxFeature = studio.Add( new PrimitiveFeature() );

		var shell = studio.Add( new ShellFeature() );
		shell.Thickness.Value = 0.05f;
		shell.OpenFaces.Add( 7 );   // valid on the 18-face cylinder, out of range on the 6-face box

		studio.Rebuild();

		Check( "a shell that fails partway records an error", shell.Error is not null, shell.Error );
		Check( "and leaves every body untouched",
			studio.Bodies.All( b => b.Mesh.FaceCount is 18 or 6 ),
			string.Join( ",", studio.Bodies.Select( b => b.Mesh.FaceCount ) ) );

		// 6. Flipped rim quads had their indices reversed but not their UVs, so they mapped mirrored
		//    relative to the unflipped quads beside them.
		var rimFaces = opened.Faces.Skip( 10 ).ToList();   // 5 outer + 5 inner, then the rim
		var outerV = new HashSet<float>();

		foreach ( var face in rimFaces )
		{
			for ( var i = 0; i < face.Count; i++ )
			{
				if ( face.Indices[i] < box.VertexCount )
					outerV.Add( MathF.Round( face.UVs[i].y, 3 ) );
			}
		}

		Check( "every rim quad maps its outer edge to the same UV row", outerV.Count == 1,
			string.Join( ",", outerV ) );
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
			("extrusion", ExtrudedRectangle( 4f, 2f, 2f ))
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

		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( -2, -2 ), new Vec2( 2, 2 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
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
		sketch.Sketch.Curves.Clear();
		sketch.Sketch.Points.Clear();
		sketch.Sketch.AddRectangle( new Vec2( -3, -2 ), new Vec2( 3, 2 ) );
		studio.MarkDirty( sketch );
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
