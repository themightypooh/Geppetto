using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Verification for UV projection.
///
/// The check that matters is HANDEDNESS. Getting one of the six box-projection directions backwards
/// mirrors the texture on that side of the model — text reads backwards and nothing else looks
/// wrong enough to notice in a grey-box render. It is caught here by comparing the signed area of
/// each face's UV polygon: every face must wind the same way in UV space as it does in 3D, so all
/// six signs have to agree.
/// </summary>
public static class UVTests
{
	public static void Run()
	{
		Section( "UV projection" );
		TestBoxProjection();
		TestPlanarProjection();
		TestInTheTree();
	}

	static void TestBoxProjection()
	{
		var box = Primitives.Box( 4, 4, 4 );
		UVProjection.BoxProject( box, 1f );

		Check( "every UV is finite", box.Faces.All( f => f.UVs.All( uv => float.IsFinite( uv.x ) && float.IsFinite( uv.y ) ) ) );

		// A 4-wide face projected at 1 unit per tile spans exactly 4 tiles in each direction.
		var worstSpan = 0f;

		foreach ( var face in box.Faces )
		{
			var spanU = face.UVs.Max( uv => uv.x ) - face.UVs.Min( uv => uv.x );
			var spanV = face.UVs.Max( uv => uv.y ) - face.UVs.Min( uv => uv.y );
			worstSpan = MathF.Max( worstSpan, MathF.Max( MathF.Abs( spanU - 4f ), MathF.Abs( spanV - 4f ) ) );
		}

		Check( "a 4-unit face spans exactly 4 tiles at scale 1", worstSpan < 1e-4f, $"worst {worstSpan:0.#####}" );

		// THE HANDEDNESS TEST. Signed UV area must have the same sign on all six faces; a mirrored
		// face flips it. This is the check that catches a single wrong axis pair.
		var signs = box.Faces.Select( f => MathF.Sign( SignedUVArea( f ) ) ).Distinct().ToList();
		Check( "all six directions agree on handedness — no mirrored faces", signs.Count == 1,
			$"signs: {string.Join( ",", signs )}" );

		Check( "and none is degenerate", box.Faces.All( f => MathF.Abs( SignedUVArea( f ) ) > 1e-6f ) );

		// Scale is units per tile, so doubling it should halve the span.
		var scaled = Primitives.Box( 4, 4, 4 );
		UVProjection.BoxProject( scaled, 2f );
		var scaledSpan = scaled.Faces[0].UVs.Max( uv => uv.x ) - scaled.Faces[0].UVs.Min( uv => uv.x );
		Check( "doubling the scale halves the tiling", Near( scaledSpan, 2f ), $"{scaledSpan}" );

		// Per-corner storage is what lets a projection seam exist at all: one corner position belongs
		// to three faces that each want a different UV for it.
		//
		// THIS MUST NOT BE TESTED ON A CUBE. Every corner of a cube has all three coordinates equal,
		// so at (2,2,2) the +X face reads (y,z) = (2,2), +Y reads (z,x) = (2,2) and +Z reads
		// (x,y) = (2,2) — the three projections coincide by symmetry, and a cube is genuinely
		// seamless at its corners. An unequal box is needed to see a seam at all.
		var oblong = Primitives.Box( 4, 6, 8 );
		UVProjection.BoxProject( oblong, 1f );

		var corner = oblong.Faces[0].Indices[0];
		var uvsAtCorner = new List<Vec2>();

		foreach ( var face in oblong.Faces )
		{
			for ( var i = 0; i < face.Count; i++ )
			{
				if ( face.Indices[i] == corner )
					uvsAtCorner.Add( face.UVs[i] );
			}
		}

		Check( "on an unequal box, one corner carries different UVs per face — a seam",
			uvsAtCorner.Select( uv => (MathF.Round( uv.x, 3 ), MathF.Round( uv.y, 3 )) ).Distinct().Count() > 1,
			string.Join( " ", uvsAtCorner ) );

		Check( "while a cube is seamless at its corners, by symmetry",
			box.Faces.SelectMany( f => f.Indices.Select( ( vi, i ) => (vi, f.UVs[i]) ) )
				.Where( x => x.vi == box.Faces[0].Indices[0] )
				.Select( x => (MathF.Round( x.Item2.x, 3 ), MathF.Round( x.Item2.y, 3 )) )
				.Distinct().Count() == 1 );

		// Projection must be a pure function of the geometry.
		var again = Primitives.Box( 4, 4, 4 );
		UVProjection.BoxProject( again, 1f );
		Check( "projection is deterministic",
			again.Faces.Zip( box.Faces ).All( pair => pair.First.UVs.Zip( pair.Second.UVs ).All( uv => Near( uv.First.x, uv.Second.x ) && Near( uv.First.y, uv.Second.y ) ) ) );

		// Curved and bevelled shapes must not produce anything degenerate.
		foreach ( var (name, mesh) in new (string, PolyMesh)[]
		{
			("cylinder", Primitives.Cylinder( 1f, 2f, 16 )),
			("quadsphere", Primitives.QuadSphere( 1f, 3 )),
			("chamfered box", EdgeBlend.Chamfer( Primitives.Box( 2, 2, 2 ), 0.2f, 15f ))
		} )
		{
			UVProjection.BoxProject( mesh, 1f );
			Check( $"{name}: projects without degenerate UVs",
				mesh.Faces.All( f => f.UVs.All( uv => float.IsFinite( uv.x ) && float.IsFinite( uv.y ) )
					&& MathF.Abs( SignedUVArea( f ) ) > 1e-9f ) );
		}

		// UVs are per corner, so subdivision keeps them local and the seams survive - already true
		// generally, but worth pinning for projected UVs specifically since they create the seams.
		var projected = Primitives.Box( 4, 4, 4 );
		UVProjection.BoxProject( projected, 1f );
		var subdivided = CatmullClark.Subdivide( projected, 2 );
		Check( "projected UVs survive subdivision",
			subdivided.Faces.All( f => f.UVs.All( uv => float.IsFinite( uv.x ) ) ) );

		var subSigns = subdivided.Faces.Select( f => MathF.Sign( SignedUVArea( f ) ) ).Distinct().ToList();
		Check( "and stay consistently wound afterwards", subSigns.Count == 1, $"signs: {string.Join( ",", subSigns )}" );
	}

	static void TestPlanarProjection()
	{
		var box = Primitives.Box( 2, 2, 2 );
		UVProjection.PlanarProject( box, new Vec3( 0, 0, 1 ), 1f );

		Check( "planar: every UV is finite", box.Faces.All( f => f.UVs.All( uv => float.IsFinite( uv.x ) ) ) );

		// Faces perpendicular to the direction collapse to a line. That is inherent to projecting,
		// not a bug, and is exactly why box projection is the default.
		var collapsed = box.Faces.Count( f => MathF.Abs( SignedUVArea( f ) ) < 1e-6f );
		Check( "planar: faces edge-on to the direction collapse, as projection must", collapsed == 4,
			$"{collapsed} collapsed" );

		var threw = false;
		try { UVProjection.PlanarProject( box, Vec3.Zero ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "planar: a zero direction is refused", threw );

		threw = false;
		try { UVProjection.BoxProject( box, 0f ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "a zero scale is refused", threw );
	}

	static void TestInTheTree()
	{
		var studio = new PartStudio();

		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( -2, -2 ), new Vec2( 2, 2 ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 4f;

		var project = studio.Add( new UVProjectFeature() );
		project.Scale.Value = 2f;

		studio.Rebuild();

		Check( "UV feature runs", project.Error is null, project.Error );

		var mesh = studio.Bodies[0].Mesh;
		Check( "and every face is mapped",
			mesh.Faces.All( f => f.UVs.All( uv => float.IsFinite( uv.x ) && float.IsFinite( uv.y ) ) ) );

		var signs = mesh.Faces.Select( f => MathF.Sign( SignedUVArea( f ) ) ).Distinct().ToList();
		Check( "consistently wound", signs.Count == 1, $"signs: {string.Join( ",", signs )}" );

		// The dialog changes shape with the mode, the way every other feature's does.
		Check( "box mode hides the direction field", project.Parameters.All( p => p.Label != "Direction" ) );
		project.Mode.Index = 1;
		Check( "planar mode shows it", project.Parameters.Any( p => p.Label == "Direction" ) );

		// Resizing upstream must re-project rather than leaving stale UVs behind.
		project.Mode.Index = 0;
		sketch.Sketch.Curves.Clear();
		sketch.Sketch.Points.Clear();
		sketch.Sketch.AddRectangle( new Vec2( -4, -2 ), new Vec2( 4, 2 ) );
		studio.MarkDirty( sketch );
		studio.Rebuild();

		var widest = studio.Bodies[0].Mesh.Faces.Max( f => f.UVs.Max( uv => uv.x ) - f.UVs.Min( uv => uv.x ) );
		Check( "widening the model re-tiles rather than stretching", Near( widest, 4f, 1e-3f ), $"{widest:0.###}" );
	}

	/// <summary>Signed area of a face's UV polygon by the shoelace formula. Its sign says which way
	/// the face winds in texture space; a mirrored projection flips it.</summary>
	static float SignedUVArea( Face face )
	{
		var total = 0f;

		for ( var i = 0; i < face.Count; i++ )
		{
			var a = face.UVs[i];
			var b = face.UVs[(i + 1) % face.Count];
			total += a.x * b.y - b.x * a.y;
		}

		return total * 0.5f;
	}

	static bool Near( float a, float b, float tolerance = 1e-4f ) => MathF.Abs( a - b ) < tolerance;

	static void Section( string title ) => Report.Section( title );
	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
