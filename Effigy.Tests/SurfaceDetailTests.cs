using System;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The secondary-detail passes in SurfaceDetail: seams between parts and the grown-seam profile,
/// gravity sag folds, stamps that make room before they rise, surface Poisson sampling, grain,
/// cavity and ambient occlusion.
/// </summary>
public static class SurfaceDetailTests
{
	public static void Run()
	{
		Report.Section( "surface detail: seams, grown seams, sag folds, stamps, poisson, grain, cavity, AO" );
		TestSeams();
		TestGrownSeam();
		TestSagFolds();
		TestStamps();
		TestPoisson();
		TestGrain();
		TestCavity();
		TestAmbientOcclusion();
		TestVertexBakes();
		TestAnchoredFolds();
		TestBusyMask();
		TestClusters();
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );

	// A 20 x 20 in sheet at 0.25 in, two parts split at y = 0. "Up" is +y, so the y > 0 part is on top.
	static PolyMesh Sheet() => Primitives.Plane( 20, 20, 80, 80 );
	static int[] Halves( PolyMesh m ) => m.Positions.Select( p => p.y > 0f ? 0 : 1 ).ToArray();

	static void TestSeams()
	{
		var m = Sheet();
		var f = SurfaceDetail.Seams( m, Halves( m ), 4f, up: new Vec3( 0, 1, 0 ) );
		var probe = m.Positions.Select( ( p, i ) => (p, i) ).Where( t => MathF.Abs( t.p.x ) < 0.2f && MathF.Abs( t.p.y - 2f ) < 0.1f ).Select( t => t.i ).First();
		Check( "a vertex 2 in from the seam measures about 2 in", MathF.Abs( f.Distance[probe] - 2f ) < 0.25f, $"{f.Distance[probe]:0.00}" );
		Check( "it knows which part is across the seam", f.Other[probe] == 1 );
		var lowTop = f.Top.Where( ( t, i ) => t && m.Positions[i].y < -0.5f && f.Other[i] >= 0 ).Count();
		Check( "the part higher up is on top", f.Top[probe] && lowTop == 0, $"probe {f.Top[probe]}, {lowTop} low vertices on top" );
		var far = m.Positions.Select( ( p, i ) => (p, i) ).First( t => t.p.y > 8f ).i;
		Check( "past the reach there is no seam", f.Other[far] < 0 );
		var none = SurfaceDetail.Seams( m, Halves( m ), 4f, want: ( a, b ) => false );
		Check( "unwanted pairs get no seam", none.Other.All( o => o < 0 ) );
	}

	static void TestGrownSeam()
	{
		var m = Sheet();
		var f = SurfaceDetail.Seams( m, Halves( m ), 6f, up: new Vec3( 0, 1, 0 ) );
		var o = new SeamOptions { Variation = 0f, Up = new Vec3( 0, 1, 0 ) };
		var before = m.Positions.ToArray();
		SurfaceDetail.GrownSeam( m, f, o );
		float At( float y ) { var i = before.Select( ( p, k ) => (p, k) ).Where( t => MathF.Abs( t.p.x ) < 0.2f ).OrderBy( t => MathF.Abs( t.p.y - y ) ).First().k; return m.Positions[i].z - before[i].z; }
		Check( "the seam is creased in", At( 0.12f ) < -0.3f && At( -0.12f ) < -0.3f, $"{At( 0.12f ):0.00} / {At( -0.12f ):0.00}" );
		Check( "the upper part rolls over it", At( o.RollOffset ) > 0.2f, $"{At( o.RollOffset ):0.00}" );
		Check( "the lower part gets only a small lip", At( -o.RollOffset * 0.6f ) < At( o.RollOffset ) * 0.5f );
		Check( "far from the seam nothing moves", MathF.Abs( At( 8f ) ) < 1e-4f && MathF.Abs( At( -8f ) ) < 1e-4f );
	}

	static void TestSagFolds()
	{
		// A wall facing +x (the sheet stood up: y becomes z). Its skin hangs in folds.
		var wall = Sheet();
		for ( var i = 0; i < wall.VertexCount; i++ ) { var p = wall.Positions[i]; wall.Positions[i] = new Vec3( p.z, p.x, p.y ); }
		var before = wall.Positions.ToArray();
		var o = new FoldOptions { Wander = 0f, Break = 0f };
		SurfaceDetail.SagFolds( wall, Enumerable.Repeat( 1f, wall.VertexCount ).ToArray(), o );
		var moved = wall.Positions.Select( ( p, i ) => (p - before[i]).Length ).ToArray();
		Check( "a loose wall folds", moved.Max() > o.Amplitude * 0.6f, $"{moved.Max():0.00}" );
		// Crests repeat at the spacing: sample the offset down a vertical line.
		var line = before.Select( ( p, i ) => (p, i) ).Where( t => MathF.Abs( t.p.y ) < 0.13f ).OrderBy( t => t.p.z ).Select( t => wall.Positions[t.i].x - before[t.i].x ).ToArray();
		var peaks = Enumerable.Range( 1, line.Length - 2 ).Count( k => line[k] > line[k - 1] && line[k] >= line[k + 1] && line[k] > o.Amplitude * 0.5f );
		var expected = 20f / o.Spacing;
		Check( "about one fold per spacing", MathF.Abs( peaks - expected ) <= 2f, $"{peaks} folds, expected ~{expected:0}" );
		var floor = Sheet();
		var fb = floor.Positions.ToArray();
		SurfaceDetail.SagFolds( floor, Enumerable.Repeat( 1f, floor.VertexCount ).ToArray(), o );
		Check( "skin facing straight up doesn't fold", floor.Positions.Select( ( p, i ) => (p - fb[i]).Length ).Max() < 1e-4f );
		var tight = Sheet();
		var tb = tight.Positions.ToArray();
		SurfaceDetail.SagFolds( tight, new float[tight.VertexCount], o );
		Check( "tight skin doesn't fold", tight.Positions.Select( ( p, i ) => (p - tb[i]).Length ).Max() < 1e-6f );
	}

	static void TestStamps()
	{
		var m = Sheet();
		var before = m.Positions.ToArray();
		SurfaceDetail.Stamps( m, new[] { new SurfaceStamp( Vec3.Zero, 1.5f, 0.6f ) } );
		var centre = before.Select( ( p, i ) => (p, i) ).OrderBy( t => t.p.Length ).First().i;
		Check( "a stamp rises at its centre", MathF.Abs( m.Positions[centre].z - 0.6f ) < 0.06f, $"{m.Positions[centre].z:0.00}" );
		var ring = before.Select( ( p, i ) => (p, i) ).Where( t => MathF.Abs( t.p.Length - 1.65f ) < 0.15f ).Select( t => t.i ).ToArray();
		Check( "the skin round its base is pushed outward", ring.Average( i => m.Positions[i].Length - before[i].Length ) > 0.05f );
		Check( "beyond twice its radius nothing moves", before.Select( ( p, i ) => (p, i) ).Where( t => t.p.Length > 3.2f ).All( t => (m.Positions[t.i] - t.p).Length < 1e-6f ) );
	}

	static void TestPoisson()
	{
		var m = Sheet();
		var pts = SurfaceDetail.PoissonOnSurface( m, 2f, v => m.Positions[v].x > 0f, 3 );
		var minGap = float.MaxValue;
		for ( var a = 0; a < pts.Count; a++ )
			for ( var b = a + 1; b < pts.Count; b++ )
				minGap = MathF.Min( minGap, (m.Positions[pts[a]] - m.Positions[pts[b]]).Length );
		Check( "points keep their spacing", minGap >= 2f - 1e-4f, $"{minGap:0.00}" );
		Check( "points stay in the mask", pts.All( v => m.Positions[v].x > 0f ) );
		Check( "and fill it (10 x 20 in at 2 in apart)", pts.Count > 20, $"{pts.Count}" );
	}

	static void TestGrain()
	{
		var m = Sheet();
		var before = m.Positions.ToArray();
		SurfaceDetail.Grain( m, 0.02f, 1.5f, 0.6f, 0.01f, 5, v => before[v].x > 0f ? 1f : 0f );
		var moved = m.Positions.Select( ( p, i ) => MathF.Abs( p.z - before[i].z ) ).ToArray();
		Check( "grain moves skin a little", moved.Where( ( _, i ) => before[i].x > 0.5f ).Max() > 0.004f );
		Check( "within lumps plus crease", moved.Max() <= 0.02f * 1.2f + 0.01f + 1e-4f, $"{moved.Max():0.0000}" );
		Check( "and only where the mask allows", moved.Where( ( _, i ) => before[i].x < 0f ).All( d => d < 1e-6f ) );
	}

	static void TestCavity()
	{
		var m = Sheet();
		// A crease: a line of vertices along x = 0 pressed 0.3 in down.
		for ( var i = 0; i < m.VertexCount; i++ )
			if ( MathF.Abs( m.Positions[i].x ) < 0.13f ) m.Positions[i] -= new Vec3( 0, 0, 0.3f );
		var c = SurfaceDetail.Cavity( m, 6 );
		var inCrease = Enumerable.Range( 0, m.VertexCount ).Where( i => MathF.Abs( m.Positions[i].x ) < 0.13f && MathF.Abs( m.Positions[i].y ) < 8f ).Average( i => c[i] );
		var flat = Enumerable.Range( 0, m.VertexCount ).Where( i => MathF.Abs( m.Positions[i].x - 6f ) < 0.13f && MathF.Abs( m.Positions[i].y ) < 8f ).Average( i => c[i] );
		Check( "a crease reads as cavity", inCrease > 0.1f, $"{inCrease:0.000}" );
		Check( "flat skin reads as none", MathF.Abs( flat ) < 0.01f, $"{flat:0.000}" );
	}

	static void TestAmbientOcclusion()
	{
		// A floor under a low roof: shut in under it, open beyond it.
		var floor = Primitives.Plane( 20, 20, 40, 40 );
		var roof = Primitives.Plane( 8, 8, 8, 8 );
		var both = new PolyMesh( floor.Positions.Concat( roof.Positions.Select( p => p + new Vec3( 0, 0, 1.5f ) ) ),
			floor.Faces.Select( f => new Face( (int[])f.Indices.Clone(), null, 0 ) ).Concat( roof.Faces.Select( f => new Face( f.Indices.Select( i => i + floor.VertexCount ).Reverse().ToArray(), null, 0 ) ) ) );
		var ao = SurfaceDetail.AmbientOcclusion( both, 48, 6f, 2 );
		var under = Enumerable.Range( 0, floor.VertexCount ).Where( i => floor.Positions[i].Length < 1f ).Average( i => ao[i] );
		var open = Enumerable.Range( 0, floor.VertexCount ).Where( i => floor.Positions[i].Length > 9f ).Average( i => ao[i] );
		Check( "skin under a roof is occluded", under > 0.6f, $"{under:0.00}" );
		Check( "open skin is not", open < 0.15f, $"{open:0.00}" );
	}

	static void TestVertexBakes()
	{
		// A UV'd cage under a denser sculpt 0.2 in above it, red on its -x half and blue on +x.
		var cage = Primitives.Plane( 10, 10, 4, 4 );
		var sculpt = Primitives.Plane( 10, 10, 40, 40 );
		for ( var i = 0; i < sculpt.VertexCount; i++ ) sculpt.Positions[i] += new Vec3( 0, 0, 0.2f );
		sculpt.VertexColors = sculpt.Positions.Select( p => p.x < 0f ? new Vec4( 1, 0, 0, 1 ) : new Vec4( 0, 0, 1, 1 ) ).ToArray();
		var map = NormalBake.BakeVertexColours( cage, sculpt, 64, 64, new BakeOptions { MaxDistance = 1f } );
		Check( "a vertex-colour bake lands on the cage's texels", map.FilledCount > 64 * 64 * 0.9f, $"{map.FilledCount} of {64 * 64}" );
		var (lr, _, lb) = map.At( 8, 32 );
		var (rr, _, rb) = map.At( 56, 32 );
		Check( "the sculpt's colours come through on the right side of the map", lr > 200 && lb < 50 && rb > 200 && rr < 50, $"left {lr}/{lb}, right {rr}/{rb}" );
		var ramp = sculpt.Positions.Select( p => p.x ).ToArray();
		var grey = NormalBake.BakeScalar( cage, sculpt, 64, 64, ramp, -5f, 5f, new BakeOptions { MaxDistance = 1f } );
		Check( "a scalar bakes as a greyscale ramp", grey.At( 4, 32 ).R < 40 && grey.At( 60, 32 ).R > 215 && MathF.Abs( grey.At( 32, 32 ).R - 128 ) < 20, $"{grey.At( 4, 32 ).R} .. {grey.At( 32, 32 ).R} .. {grey.At( 60, 32 ).R}" );
	}

	static void TestAnchoredFolds()
	{
		// The sheet stood up as a wall (y becomes z); the anchor is its top edge region at z > 0.
		var wall = Sheet();
		for ( var i = 0; i < wall.VertexCount; i++ ) { var p = wall.Positions[i]; wall.Positions[i] = new Vec3( p.z, p.x, p.y ); }
		var before = wall.Positions.ToArray();
		var dist = before.Select( p => MathF.Max( 0f, -p.z ) ).ToArray();
		var o = new AnchoredFoldOptions { Variation = 0f };
		SurfaceDetail.AnchoredFolds( wall, dist, v => before[v].z <= 0f, o );
		Check( "nothing above the anchor folds", before.Select( ( p, i ) => (p, i) ).Where( t => t.p.z > 0.1f ).All( t => (wall.Positions[t.i] - t.p).Length < 1e-6f ) );
		var line = before.Select( ( p, i ) => (p, i) ).Where( t => MathF.Abs( t.p.y ) < 0.13f && t.p.z <= 0f ).OrderByDescending( t => t.p.z ).Select( t => wall.Positions[t.i].x - before[t.i].x ).ToArray();
		var peaks = Enumerable.Range( 1, line.Length - 2 ).Where( k => line[k] > line[k - 1] && line[k] >= line[k + 1] && line[k] > 0.05f ).ToArray();
		Check( "it hangs that many folds", peaks.Length == o.Count, $"{peaks.Length}" );
		Check( "the first is the deepest, the later ones shallower", peaks.Length >= 2 && line[peaks[0]] > line[peaks[^1]] * 1.5f );
		Check( "and they spread out as they go", peaks.Length >= 3 && peaks[2] - peaks[1] > peaks[1] - peaks[0] );
		var far = before.Select( ( p, i ) => (p, i) ).Where( t => t.p.z < -9.8f ).All( t => MathF.Abs( wall.Positions[t.i].x - t.p.x ) < 0.05f );
		Check( "past the last fold the skin is left alone", far );
	}

	static void TestBusyMask()
	{
		var m = Primitives.Plane( 60, 60, 120, 120 );
		var mask = SurfaceDetail.BusyMask( m, 0.4f, 0.08f, 3 );
		var busy = mask.Count( x => x > 0.5f ) / (float)mask.Length;
		Check( "the busy mask covers about the share asked", MathF.Abs( busy - 0.4f ) < 0.06f, $"{busy:0.00}" );
		Check( "and leaves the rest quiet", mask.Count( x => x < 0.01f ) / (float)mask.Length > 0.45f );
	}

	static void TestClusters()
	{
		var m = Sheet();
		var stamps = SurfaceDetail.Clusters( m, v => true, 3, 6, 2f, 0.3f, 1.2f, 4 );
		Check( "clusters make the stamps asked for", stamps.Count == 18, $"{stamps.Count}" );
		Check( "sizes stay in range", stamps.All( s => s.Radius >= 0.3f * 0.6f - 1e-4f && s.Radius <= 1.2f + 1e-4f ) );
		var groups = stamps.Select( a => stamps.Count( b => (a.Center - b.Center).Length <= 4f ) ).Min();
		Check( "each stamp has company (they cluster)", groups >= 4, $"min neighbours {groups}" );
	}
}
