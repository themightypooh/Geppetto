using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Checking the SHAPE of what the kernel builds, not just its numbers.
///
/// This exists because of one bug. Bevel flung corners fifteen thousand units out on a model twenty
/// across, and every numeric oracle in this suite said the result was fine — it was finite, closed,
/// manifold and Euler-correct, because a vertex in the wrong PLACE breaks none of those. A render
/// caught it, by accident, once. RenderCheck turns that render into three numbers a test can fail
/// on, and these are the tests.
///
/// The last section is the one that makes the rest worth anything: it damages good models in the
/// three ways this is meant to notice, and fails if a check stays quiet. A test oracle nobody has
/// watched fail is an oracle nobody knows the sensitivity of.
/// </summary>
public static class RenderTests
{
	public static void Run()
	{
		Report.Section( "renders: a solid fills the frame fitted to it" );
		TestCoverage();

		Report.Section( "renders: nothing shows through a closed solid" );
		TestParity();

		Report.Section( "renders: one body is one silhouette" );
		TestComponents();

		Report.Section( "renders: subdivision keeps the shape it was given" );
		TestSubdivided();

		Report.Section( "renders: the checks fire on damage the numbers miss" );
		TestCatchesDamage();
	}

	/// <summary>
	/// THE BEVEL CASE. Frame the view on the model's own bounds and ask how much of it the model
	/// fills. Every real solid fills a third of it or more; the floor here is 0.05, which is six
	/// times below the worst honest case (a wedge, seen corner-on) and four orders of magnitude
	/// above what one misplaced vertex produces.
	/// </summary>
	static void TestCoverage()
	{
		foreach ( var (name, mesh) in Models() )
		{
			var worst = Worst( mesh, v => v.Coverage );

			Report.Check( $"{name} fills its frame", worst > 0.05f,
				$"worst coverage {worst:0.00000} — the view stretched to {Extent( mesh ):0.##} units" );
		}
	}

	/// <summary>
	/// You cannot see into a closed shape, so every pixel with a face pointing at the camera must
	/// have one pointing away behind it. Measured at exactly 1.0000 on every closed solid here bar
	/// the bevelled box, which loses four ten-thousandths to rasteriser slivers on its chamfers.
	/// </summary>
	static void TestParity()
	{
		foreach ( var (name, mesh) in Models() )
		{
			var worst = Worst( mesh, v => v.Parity );

			Report.Check( $"{name} is opaque from every angle", worst > 0.99f,
				$"worst parity {worst:0.0000} — {(1f - worst) * 100f:0.##}% of the silhouette sees straight through" );
		}
	}

	static void TestComponents()
	{
		foreach ( var (name, mesh) in Models() )
		{
			var most = 0;

			foreach ( var direction in RenderCheck.Directions )
				most = Math.Max( most, RenderCheck.Render( mesh, direction ).Components );

			Report.Check( $"{name} renders as one piece", most == 1, $"{most} islands" );
		}
	}

	/// <summary>
	/// Break three good models in three specific ways and require the matching check to notice.
	///
	/// Each damage is chosen to be INVISIBLE to the rest of the suite: none of them changes the
	/// vertex count, the face count, the Euler characteristic or whether the mesh is closed. That is
	/// the point — these are the mistakes the numbers cannot see.
	/// </summary>
	static void TestCatchesDamage()
	{
		// ONE. A vertex thrown a thousand diameters out, which is what Bevel was doing.
		var flung = Primitives.Box( 4f, 3f, 2f );
		var before = Worst( flung, v => v.Coverage );

		flung.Positions[0] = new Vec3( 15000f, 0f, 0f );

		var after = Worst( flung, v => v.Coverage );

		Report.Check( "a healthy box passes the coverage check", before > 0.05f, $"{before:0.0000}" );

		Report.Check( "and one vertex flung 15000 units out fails it", after <= 0.05f,
			$"coverage {after:0.000000}, which the check let through" );

		Report.Check( "with the framed extent naming the culprit", Extent( flung ) > 1000f,
			$"{Extent( flung ):0.#}" );

		// The damage above is genuinely invisible to the oracles this suite has always used: same
		// vertices, same faces, still closed. Only the render sees it.
		Report.Check( "while the vertex and face counts are untouched",
			flung.VertexCount == 8 && flung.FaceCount == 6 );

		// TWO. One face wound backwards. The mesh stays closed and Euler-correct — nothing about
		// counting says a normal points the wrong way — and it renders as a hole through the solid.
		var flipped = Primitives.Box( 4f, 3f, 2f );
		var parityBefore = Worst( flipped, v => v.Parity );

		flipped.Faces[0].Indices = flipped.Faces[0].Indices.Reverse().ToArray();

		var parityAfter = Worst( flipped, v => v.Parity );

		Report.Check( "a healthy box is opaque", parityBefore > 0.99f, $"{parityBefore:0.0000}" );

		Report.Check( "and a single reversed face makes it see-through", parityAfter <= 0.99f,
			$"parity {parityAfter:0.0000}, which the check let through" );

		Report.Check( "though it still has the same faces and vertices",
			flipped.VertexCount == 8 && flipped.FaceCount == 6 );

		// THREE. A second body off on its own. Coverage survives it — a fragment nearby barely moves
		// the bounds — so this is the case the island count is for and the other two checks are not.
		var split = Primitives.Box( 4f, 3f, 2f );
		var stray = Primitives.Box( 1f, 1f, 1f );

		var offset = split.Positions.Count;

		foreach ( var p in stray.Positions )
			split.Positions.Add( p + new Vec3( 6f, 0f, 0f ) );

		foreach ( var f in stray.Faces )
			split.AddFace( f.Indices.Select( i => i + offset ).ToArray() );

		var islands = 0;

		foreach ( var direction in RenderCheck.Directions )
			islands = Math.Max( islands, RenderCheck.Render( split, direction ).Components );

		Report.Check( "a detached fragment shows up as a second island", islands > 1,
			$"{islands} islands" );

		Report.Check( "and coverage alone would not have caught it",
			Worst( split, v => v.Coverage ) > 0.05f,
			"coverage caught it too, so this case is not proving what it claims" );
	}

	// --- helpers ------------------------------------------------------------------------------

	static float Worst( PolyMesh mesh, Func<RenderCheck.View, float> metric ) =>
		RenderCheck.Directions.Min( d => metric( RenderCheck.Render( mesh, d ) ) );

	/// <summary>
	/// Every closed primitive, subdivided twice, through all three checks.
	///
	/// Subdivision is the operation with the most to lose here and the least numeric evidence about
	/// it: it multiplies the vertex count by sixteen and every count-based oracle keeps agreeing
	/// with itself the whole way, because a Catmull-Clark step is exactly as closed and exactly as
	/// Euler-correct whether or not the limit surface it converges on is the right shape. A cage
	/// that subdivides into a knot passes every existing test in this suite.
	/// </summary>
	static void TestSubdivided()
	{
		foreach ( var (name, mesh) in Program.Closed() )
		{
			var dense = CatmullClark.Subdivide( mesh, 2 );

			Report.Check( $"{name} subdivided fills its frame",
				Worst( dense, v => v.Coverage ) > 0.05f,
				$"coverage {Worst( dense, v => v.Coverage ):0.00000}" );

			Report.Check( $"{name} subdivided stays opaque",
				Worst( dense, v => v.Parity ) > 0.99f,
				$"parity {Worst( dense, v => v.Parity ):0.0000}" );

			// SHAPE HELD, not just validity. A subdivided solid sits inside its cage and shrinks
			// toward the limit surface, so its coverage should stay in the same neighbourhood rather
			// than wandering; a cage that folds through itself does not.
			var cage = Worst( mesh, v => v.Coverage );
			var after = Worst( dense, v => v.Coverage );

			Report.Check( $"{name} subdivided keeps roughly the silhouette of its cage",
				after > cage * 0.6f && after < cage * 1.4f,
				$"cage {cage:0.000} -> subdivided {after:0.000}" );
		}
	}

	/// <summary>
	/// Rebuild and refuse to hand back a mesh from a studio that failed.
	///
	/// Without this a broken fixture yields an EMPTY mesh, and an empty mesh fails all three render
	/// checks at once with numbers that describe nothing — zero coverage, zero islands, a frame
	/// stretched to zero units. Every one of those reads as a rendering fault rather than as
	/// "the model was never built", which is a long way to walk to find a typo in a test.
	/// </summary>
	static PolyMesh Built( PartStudio studio )
	{
		var report = studio.Rebuild();

		if ( report.HasErrors )
			throw new InvalidOperationException( $"render fixture failed to build: {report}" );

		return studio.ToMesh();
	}

	static float Extent( PolyMesh mesh ) =>
		RenderCheck.Directions.Max( d => RenderCheck.Render( mesh, d ).FramedExtent );

	/// <summary>One of each shape the kernel can make, including the two that used to be
	/// impossible — a profile with a hole, and a bevel.</summary>
	static IEnumerable<(string, PolyMesh)> Models()
	{
		yield return ("box", Primitives.Box( 4f, 3f, 2f ));
		yield return ("cylinder", Primitives.Cylinder( 2f, 5f, 24 ));
		yield return ("sphere", Primitives.QuadSphere( 2f, 2 ));
		yield return ("wedge", Primitives.Wedge( 4f, 3f, 2f ));
		yield return ("tube", Primitives.Tube( 2f, 1f, 4f, 24 ));

		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 6f; box.SizeY.Value = 6f; box.SizeZ.Value = 3f;
		studio.Add( new ChamferFeature() );
		yield return ("bevelled box", Built( studio ));

		var washer = new PartStudio();
		var sketch = washer.Add( new SketchFeature() );
		sketch.Sketch.AddCircle( new Vec2( 0, 0 ), 4f );
		sketch.Sketch.AddCircle( new Vec2( 0, 0 ), 2f );
		var pull = washer.Add( new ExtrudeFeature() );
		pull.Distance.Value = 2f;
		yield return ("washer (hole)", Built( washer ));

		var shelled = new PartStudio();
		var plate = shelled.Add( new PrimitiveFeature() );
		plate.SizeX.Value = 6f; plate.SizeY.Value = 6f; plate.SizeZ.Value = 6f;
		shelled.Add( new ShellFeature() );
		yield return ("shelled cube (closed)", Built( shelled ));

		// ENTIRELY ON ONE SIDE OF THE AXIS. The default axis is X through the sketch origin, and the
		// first version of this rectangle spanned y -1..1 — straddling it. A profile that crosses its
		// own axis of revolution is not a solid, and the render check reported an empty mesh, which
		// was the fixture being wrong before the code was.
		var revolved = new PartStudio();
		var profile = revolved.Add( new SketchFeature() );
		profile.Sketch.AddRectangle( new Vec2( 2, 1 ), new Vec2( 4, 3 ) );
		revolved.Add( new RevolveFeature() );
		yield return ("revolved ring", Built( revolved ));
	}
}
