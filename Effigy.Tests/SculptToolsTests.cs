using System;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The sculpt tools added to reach Blender's set (each also scored against Blender itself by
/// Effigy.Tests --parity): the new brushes, cloth brushes, auto-masks, mesh filters and alphas,
/// checked here for the behaviour that defines them.
/// </summary>
public static class SculptToolsTests
{
	public static void Run()
	{
		Report.Section( "sculpt tools: new brushes, cloth, auto-masks, mesh filters, alphas" );
		TestLayerCaps();
		TestCrease();
		TestKelvinlet();
		TestPose();
		TestCloth();
		TestAutoMasks();
		TestFilters();
		TestAlpha();
		TestSession();
		TestPngRoundTrip();
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );

	static PolyMesh Sheet() => Primitives.Plane( 10, 10, 100, 100 );

	static BrushStroke Line( BrushKind kind, float strength, float radius = 1.5f, int passes = 1 )
	{
		var s = new BrushStroke { Kind = kind };
		for ( var p = 0; p < passes; p++ )
			for ( var i = 0; i <= 20; i++ )
				s.Samples.Add( new BrushSample( new Vec3( -3f + 0.3f * i, 0, 0 ), new Vec3( 0, 0, 1 ), radius, strength ) );
		return s;
	}

	static void TestLayerCaps()
	{
		var m = Sheet();
		Brush.Apply( m, Line( BrushKind.Layer, 0.3f, passes: 6 ) );
		var top = m.Positions.Max( p => p.z );
		Check( "Layer rises to its height and no higher however often it passes", top > 0.2f && top <= 0.3f + 1e-3f, $"{top:0.000}" );
	}

	static void TestCrease()
	{
		var m = Sheet();
		var before = m.Positions.ToArray();
		Brush.Apply( m, Line( BrushKind.Crease, 0.05f ) );
		float At( float y ) { var i = before.Select( ( p, k ) => (p, k) ).Where( t => MathF.Abs( t.p.x ) < 0.06f ).OrderBy( t => MathF.Abs( t.p.y - y ) ).First().k; return m.Positions[i].z; }
		var side = before.Select( ( p, k ) => (p, k) ).Where( t => MathF.Abs( t.p.x ) < 0.06f && MathF.Abs( t.p.y - 0.6f ) < 0.06f ).Select( t => m.Positions[t.k].y - t.p.y ).First();
		Check( "Crease cuts a groove along the stroke", At( 0f ) < -0.05f && At( 0f ) < At( 1.0f ) );
		Check( "and pinches the sides in toward it", side < -0.01f, $"{side:0.000}" );
	}

	static void TestKelvinlet()
	{
		Check( "the elastic falloff is 1 at the grab point", MathF.Abs( Brush.Kelvinlet( 0f, 1.5f ) - 1f ) < 1e-5f );
		Check( "it reaches past the radius", Brush.Kelvinlet( 1.5f, 1.5f ) > 0.35f );
		Check( "and fades to almost nothing by four radii", Brush.Kelvinlet( 6f, 1.5f ) < 0.06f );
		var falling = Enumerable.Range( 0, 20 ).Select( i => Brush.Kelvinlet( i * 0.3f, 1.5f ) ).ToArray();
		Check( "falling all the way", falling.Zip( falling.Skip( 1 ) ).All( t => t.Second <= t.First + 1e-6f ) );
	}

	static void TestPose()
	{
		var m = Sheet();
		var before = m.Positions.ToArray();
		var s = new BrushStroke { Kind = BrushKind.Pose };
		s.Samples.Add( new BrushSample( Vec3.Zero, new Vec3( 0, 0, 1 ), 1.5f, 1f, new Vec3( 2f, 0, 0 ) ) );
		Brush.Apply( m, s );
		float Rise( float x ) { var i = before.Select( ( p, k ) => (p, k) ).OrderBy( t => (t.p - new Vec3( x, 0, 0 )).LengthSquared ).First().k; return m.Positions[i].z - before[i].z; }
		Check( "Pose turns the region about the brush point: one side up, the other down", Rise( 1f ) > 0.3f && Rise( -1f ) < -0.3f, $"{Rise( 1f ):0.00} / {Rise( -1f ):0.00}" );
		Check( "and leaves what's past the region", MathF.Abs( Rise( 4f ) ) < 1e-5f );
	}

	static void TestCloth()
	{
		var m = Sheet();
		var lengths = m.Faces.SelectMany( f => f.Indices.Select( ( a, k ) => (a, b: f.Indices[(k + 1) % f.Indices.Length]) ) ).Distinct().ToArray();
		float Stretch() => lengths.Max( e => MathF.Abs( (m.Positions[e.a] - m.Positions[e.b]).Length - 0.1f ) / 0.1f );
		var s = new BrushStroke { Kind = BrushKind.ClothGrab };
		s.Samples.Add( new BrushSample( Vec3.Zero, new Vec3( 0, 0, 1 ), 1.5f, 1f, new Vec3( 0, 0, 1.2f ) ) );
		Brush.Apply( m, s );
		Check( "a cloth grab moves the fabric", m.Positions.Max( p => p.z ) > 0.3f, $"{m.Positions.Max( p => p.z ):0.00}" );
		Check( "keeping its edges near their length (it's fabric, not clay)", Stretch() < 0.5f, $"worst stretch {Stretch():0.00}" );
		var e = Sheet();
		var se = new BrushStroke { Kind = BrushKind.ClothExpand };
		se.Samples.Add( new BrushSample( Vec3.Zero, new Vec3( 0, 0, 1 ), 1.5f, 0.3f ) );
		Brush.Apply( e, se );
		Check( "growing the fabric buckles it out of the plane", e.Positions.Max( p => MathF.Abs( p.z ) ) > 1e-3f );
	}

	static void TestAutoMasks()
	{
		var a = Primitives.Plane( 4, 4, 8, 8 );
		var b = Primitives.Plane( 4, 4, 8, 8 );
		var both = new PolyMesh( a.Positions.Concat( b.Positions.Select( p => p + new Vec3( 0, 0, 1 ) ) ),
			a.Faces.Select( f => new Face( (int[])f.Indices.Clone(), null, 0 ) ).Concat( b.Faces.Select( f => new Face( f.Indices.Select( i => i + a.VertexCount ).ToArray(), null, 0 ) ) ) );
		var topo = AutoMask.Topology( both, AutoMask.SeedAt( both, new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ) ) );
		Check( "topology masking keeps only the piece the stroke started on", topo.Take( a.VertexCount ).All( v => v == 0f ) && topo.Skip( a.VertexCount ).All( v => v == 1f ) );
		var edge = AutoMask.BoundaryEdges( a, 2 );
		Check( "boundary masking holds the open edge", a.Positions.Select( ( p, i ) => (p, i) ).Where( t => MathF.Abs( t.p.x ) > 1.99f ).All( t => edge[t.i] == 0f ) && edge.Max() == 1f );
		var bumpy = Primitives.Plane( 10, 10, 60, 60 );
		for ( var i = 0; i < bumpy.VertexCount; i++ ) { var p = bumpy.Positions[i]; bumpy.Positions[i] = new Vec3( p.x, p.y, 0.3f * MathF.Sin( p.x * 2f ) ); }
		var cav = AutoMask.Cavity( bumpy );
		var valley = bumpy.Positions.Select( ( p, i ) => (p, i) ).Where( t => t.p.z < -0.28f && MathF.Abs( t.p.x ) < 4f ).Average( t => cav[t.i] );
		var peak = bumpy.Positions.Select( ( p, i ) => (p, i) ).Where( t => t.p.z > 0.28f && MathF.Abs( t.p.x ) < 4f ).Average( t => cav[t.i] );
		Check( "cavity masking works the valleys, not the peaks", valley > 0.7f && peak < 0.3f, $"valleys {valley:0.00}, peaks {peak:0.00}" );
	}

	static void TestFilters()
	{
		var m = Sheet();
		MeshFilter.Apply( m, MeshFilterKind.Inflate, 0.25f );
		Check( "the inflate filter moves every vertex out by its strength", m.Positions.All( p => MathF.Abs( MathF.Abs( p.z ) - 0.25f ) < 1e-3f ) );
		var bumpy = Sheet();
		for ( var i = 0; i < bumpy.VertexCount; i++ ) { var p = bumpy.Positions[i]; bumpy.Positions[i] = new Vec3( p.x, p.y, 0.1f * MathF.Sin( p.x * 9f ) ); }
		bool Inside( Vec3 p ) => MathF.Abs( p.x ) < 4.5f && MathF.Abs( p.y ) < 4.5f;
		var before = bumpy.Positions.Where( Inside ).Max( p => p.z );
		var flat = Sheet().Positions;
		MeshFilter.Apply( bumpy, MeshFilterKind.Smooth, 0.5f, 4 );
		var after = bumpy.Positions.Where( Inside ).Max( p => p.z );
		Check( "the smooth filter settles bumps", after < before * 0.8f, $"{before:0.000} -> {after:0.000}" );
		// Away from the open edge, where a vertex normal has too few faces to be trusted.
		var slide = bumpy.Positions.Select( ( p, i ) => (d: MathF.Abs( p.y - flat[i].y ), p: flat[i]) ).Where( t => Inside( t.p ) ).OrderByDescending( t => t.d ).First();
		Check( "moving along the normal, not sliding across the surface", slide.d < 1e-4f, $"{slide.d:0.00000} at {slide.p}" );
	}

	static void TestAlpha()
	{
		// The image's left third black: a dab moves nothing there.
		var alpha = new BrushAlpha { Width = 4, Height = 2, Values = new[] { 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f } };
		var m = Sheet();
		var s = new BrushStroke { Kind = BrushKind.Draw, Alpha = alpha };
		s.Samples.Add( new BrushSample( Vec3.Zero, new Vec3( 0, 0, 1 ), 1.5f, 0.2f ) );
		Brush.Apply( m, s );
		var left = m.Positions.Where( p => p.x < -0.8f && p.x > -1.4f && MathF.Abs( p.y ) < 0.3f ).Max( p => p.z );
		var right = m.Positions.Where( p => p.x > 0.8f && p.x < 1.4f && MathF.Abs( p.y ) < 0.3f ).Max( p => p.z );
		Check( "a brush alpha holds back the strength where the image is dark", left < 0.02f && right > 0.03f, $"left {left:0.000}, right {right:0.000}" );
	}

	static void TestSession()
	{
		// Two sheets, one 0.5 in over the other: separate pieces.
		var lower = Primitives.Plane( 4, 4, 8, 8 );
		var upper = Primitives.Plane( 4, 4, 8, 8 );
		var cage = new PolyMesh( lower.Positions.Concat( upper.Positions.Select( p => p + new Vec3( 0, 0, 0.5f ) ) ),
			lower.Faces.Select( f => new Face( (int[])f.Indices.Clone(), null, 0 ) ).Concat( upper.Faces.Select( f => new Face( f.Indices.Select( i => i + lower.VertexCount ).ToArray(), null, 0 ) ) ) );
		var session = new SculptSession( new MultiresSculpt( cage ) ) { Brush = BrushKind.Draw, Radius = 1.5f, Strength = 0.1f, AutoTopology = true };
		var start = session.Sculpt.Evaluate( 0 ).Positions.ToArray();
		session.BeginStroke( new Vec3( 0, 0, 5 ), new Vec3( 0, 0, -1 ) );
		session.MoveTo( new Vec3( 0.4f, 0, 5 ), new Vec3( 0, 0, -1 ) );
		session.EndStroke();
		var now = session.Sculpt.Evaluate( 0 ).Positions;
		var lowerMoved = Enumerable.Range( 0, lower.VertexCount ).Max( i => (now[i] - start[i]).Length );
		var upperMoved = Enumerable.Range( lower.VertexCount, upper.VertexCount ).Max( i => (now[i] - start[i]).Length );
		Check( "with topology auto-masking a stroke moves only the sheet it started on", upperMoved > 0.05f && lowerMoved < 1e-5f, $"upper {upperMoved:0.000}, lower {lowerMoved:0.000}" );

		var edit = session.ApplyFilter( MeshFilterKind.Inflate, 0.2f );
		var inflated = session.Sculpt.Evaluate( 0 ).Positions.ToArray();
		Check( "a mesh filter runs on the level as an edit", edit is not null && Enumerable.Range( 0, inflated.Length ).All( i => MathF.Abs( (inflated[i] - now[i]).Length - 0.2f ) < 1e-3f ) );
		session.Undo();
		var undone = session.Sculpt.Evaluate( 0 ).Positions;
		Check( "and undoes like a stroke", Enumerable.Range( 0, undone.Count ).All( i => (undone[i] - now[i]).Length < 1e-5f ) );
	}

	static void TestPngRoundTrip()
	{
		// PngWriter's output read back: every filter path the writer uses, RGB.
		const int w = 37, h = 23;
		var rgb = new byte[w * h * 3];
		for ( var i = 0; i < w * h; i++ ) { rgb[i * 3] = (byte)(i * 7); rgb[i * 3 + 1] = (byte)(i * 13 + 5); rgb[i * 3 + 2] = (byte)(255 - i); }
		var (rgba, rw, rh) = PngReader.Read( PngWriter.ToBytes( rgb, w, h ) );
		var same = rw == w && rh == h && Enumerable.Range( 0, w * h ).All( i => rgba[i * 4] == rgb[i * 3] && rgba[i * 4 + 1] == rgb[i * 3 + 1] && rgba[i * 4 + 2] == rgb[i * 3 + 2] && rgba[i * 4 + 3] == 255 );
		Check( "a PNG Effigy writes reads back pixel for pixel", same );
		var alpha = BrushAlpha.FromPng( PngWriter.ToBytes( rgb, w, h ) );
		Check( "and loads as a brush alpha of the same size", alpha.Width == w && alpha.Height == h && alpha.Values.All( v => v >= 0f && v <= 1f ) );
	}
}
