using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The per-texel bake set in NormalBake.BakeAll — normal, AO, curvature, cavity, height, thickness —
/// plus the explicit cage, supersampling, the vmat it feeds, and the build script's `bake` verb.
///
/// The fixture is the ordinary organic case: a coarse unwrapped sphere as the cage, and a sphere
/// subdivided and given skin grain (SurfaceDetail.Grain) as the sculpt. The grain's creases are a
/// few sculpt edges wide, so they are exactly what a per-vertex bake smears and a per-texel one
/// should keep.
/// </summary>
public static class BakeSetTests
{
	public static void Run( string outDir )
	{
		Report.Section( "bake set: per-texel AO, curvature, cavity, height, thickness; Mikk frame; cage; vmat; bake verb" );
		var dir = Path.Combine( outDir, "bakeset" );
		Directory.CreateDirectory( dir );

		TestGrainSphere( dir );
		TestSelfBakeIsFlat();
		TestCageWindow();
		TestVmat();
		TestVerb( dir );
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );

	static PolyMesh Cage()
	{
		var low = Primitives.QuadSphere( 1f, 6 );
		UVUnwrap.Unwrap( low );
		return low;
	}

	static PolyMesh GrainSphere()
	{
		var high = CatmullClark.Subdivide( Primitives.QuadSphere( 1f, 12 ), 2 );

		// Back onto the unit sphere first: subdivision pulls a sphere in, and the grain should be
		// the only thing the bake finds.
		for ( var i = 0; i < high.VertexCount; i++ )
			high.Positions[i] = high.Positions[i].Normal;

		SurfaceDetail.Grain( high, 0.01f, 2f, 0.3f, 0.06f, 3 );
		return high;
	}

	static void TestGrainSphere( string dir )
	{
		const int size = 96;
		var low = Cage();
		var high = GrainSphere();
		var coverage = NormalBake.Measure( low, size );
		Check( "the cage's unwrap can carry a bake", coverage.CanBake, coverage.Problem );

		var set = NormalBake.BakeAll( low, high, size, size, BakeMaps.All, new BakeOptions { Supersample = 2, AoRays = 64 } );

		foreach ( var (name, map) in set.Maps )
			PngWriter.WriteFile( Path.Combine( dir, $"sphere_{name}.png" ), map );

		var names = set.Maps.Select( m => m.Name ).ToArray();
		Check( "every map asked for is baked", names.SequenceEqual( new[] { "normal", "ao", "curvature", "cavity", "height", "thickness" } ), string.Join( ",", names ) );
		Check( "and fills the cage's UVs", set.Maps.All( m => m.Map.FilledCount >= coverage.CoveredTexels * 0.9f ),
			string.Join( ", ", set.Maps.Select( m => $"{m.Name} {m.Map.FilledCount}" ) ) + $" of {coverage.CoveredTexels}" );
		Check( "2x2 supersampling fires four rays a texel", set.Samples >= coverage.CoveredTexels * 3.5f, $"{set.Samples} rays, {coverage.CoveredTexels} texels" );
		Check( "and nearly all of them find the sculpt", set.Hits >= set.Samples * 0.98f, $"{set.Hits}/{set.Samples}" );
		Check( "the height range is measured and reported", set.HeightMax > set.HeightMin && set.HeightRange >= MathF.Max( MathF.Abs( set.HeightMin ), set.HeightMax ) - 1e-6f
			&& set.Report.Contains( "height" ), $"{set.HeightMin:0.0000}..{set.HeightMax:0.0000}, +-{set.HeightRange:0.0000}" );

		// Which texels are crease and which are dome, decided WITHOUT the new bakers: the sculpt's
		// own per-vertex cavity, carried over by the old per-vertex path. Then the per-texel AO is
		// held to it.
		var vertexCavity = SurfaceDetail.Cavity( high, 4 );
		var lo = vertexCavity.Min();
		var hi = vertexCavity.Max();
		var classifier = NormalBake.BakeScalar( low, high, size, size, vertexCavity, lo, hi, new BakeOptions { Padding = 0 } );
		var filled = Enumerable.Range( 0, size * size ).Where( i => classifier.Filled[i] && set.AmbientOcclusion.Filled[i] ).ToList();
		var byCavity = filled.OrderBy( i => classifier.Rgb[i * 3] ).ToList();
		var tenth = byCavity.Count / 10;
		var domes = byCavity.Take( tenth ).ToList();
		var creases = byCavity.Skip( byCavity.Count - tenth ).ToList();

		float Mean( BakedMap map, List<int> texels ) => (float)texels.Average( i => map.Rgb[i * 3] ) / 255f;

		var aoCrease = Mean( set.AmbientOcclusion, creases );
		var aoDome = Mean( set.AmbientOcclusion, domes );
		Check( "AO is darker in the creases than on the domes", aoCrease < aoDome - 0.05f, $"crease {aoCrease:0.000}, dome {aoDome:0.000}" );
		Check( "and the domes are nearly open", aoDome > 0.85f, $"{aoDome:0.000}" );

		var cavCrease = Mean( set.Cavity, creases );
		var cavDome = Mean( set.Cavity, domes );
		Check( "cavity is darker in the creases too", cavCrease < cavDome - 0.1f, $"crease {cavCrease:0.000}, dome {cavDome:0.000}" );

		var curvCrease = Mean( set.Curvature, creases );
		var curvDome = Mean( set.Curvature, domes );
		Check( "curvature reads concave in the creases, convex on the domes", curvCrease < 0.5f && curvDome > 0.5f, $"crease {curvCrease:0.000}, dome {curvDome:0.000}" );

		var hCrease = Mean( set.Height, creases );
		var hDome = Mean( set.Height, domes );
		Check( "height is lower in the creases", hCrease < hDome, $"crease {hCrease:0.000}, dome {hDome:0.000}" );

		var tilted = 0;
		var zSum = 0f;

		foreach ( var i in filled )
		{
			var n = set.Normal.NormalAt( i % size, i / size );
			zSum += n.z;
			if ( n.z < MathF.Cos( 10f * MathF.PI / 180f ) ) tilted++;
		}

		Check( "the normal map carries the grain (some texels tilt past 10 degrees)", tilted > filled.Count / 50, $"{tilted} of {filled.Count}" );
		Check( "but stays a tangent-space map (mostly facing out)", zSum / filled.Count > 0.9f, $"mean z {zSum / filled.Count:0.000}" );
		Check( "a sphere 2 across is thick behind every texel", Mean( set.Thickness, filled ) > 0.6f, $"{Mean( set.Thickness, filled ):0.000}" );
	}

	/// <summary>
	/// The cage baked onto itself must come out flat: the sculpt's normal IS the cage's normal, so
	/// anything else is the frame disagreeing with itself — the Mikk tangents, their sign, or the
	/// inversion.
	/// </summary>
	static void TestSelfBakeIsFlat()
	{
		const int size = 64;
		var low = Cage();
		var map = NormalBake.Bake( low, low.Clone(), size );
		var worst = 0f;
		var count = 0;

		for ( var i = 0; i < size * size; i++ )
		{
			if ( !map.Filled[i] ) continue;
			count++;
			var n = map.NormalAt( i % size, i / size );
			worst = MathF.Max( worst, MathF.Acos( Math.Clamp( n.z, -1f, 1f ) ) * 180f / MathF.PI );
		}

		Check( "a cage baked onto itself is flat everywhere", count > 0 && worst < 6f, $"worst {worst:0.0} degrees over {count} texels" );
	}

	/// <summary>A flat cage at z = 0 under two sheets, one at 0.1 and one at 1: the cage decides which is baked.</summary>
	static void TestCageWindow()
	{
		const int size = 16;
		var low = Sheet( 0f );
		var near = Sheet( 0.1f );
		var far = Sheet( 1f );
		var both = new PolyMesh( near.Positions.Concat( far.Positions ),
			near.Faces.Select( f => new Face( (int[])f.Indices.Clone(), null, 0 ) )
				.Concat( far.Faces.Select( f => new Face( f.Indices.Select( i => i + near.VertexCount ).ToArray(), null, 0 ) ) ) );

		float Height( float cage )
		{
			var set = NormalBake.BakeAll( low, both, size, size, BakeMaps.Height, new BakeOptions { Cage = cage, MaxDistance = 0.05f } );
			return set.Hits == 0 ? float.NaN : set.HeightMax;
		}

		var tight = Height( 0.3f );
		var loose = Height( 1.5f );
		var short_ = Height( 0.05f );
		Check( "a 0.3 cage bakes the near sheet", MathF.Abs( tight - 0.1f ) < 1e-3f, $"{tight:0.0000}" );
		Check( "a 1.5 cage reaches the far one first", MathF.Abs( loose - 1f ) < 1e-3f, $"{loose:0.0000}" );
		Check( "a cage below the detail finds nothing", float.IsNaN( short_ ), $"{short_}" );
	}

	static PolyMesh Sheet( float z )
	{
		var m = Primitives.Plane( 2f, 2f, 4, 4 );

		for ( var i = 0; i < m.VertexCount; i++ )
			m.Positions[i] = new Vec3( m.Positions[i].x, m.Positions[i].y, z );

		foreach ( var face in m.Faces )
			face.UVs = face.Indices.Select( v => new Vec2( m.Positions[v].x * 0.5f + 0.5f, m.Positions[v].y * 0.5f + 0.5f ) ).ToArray();

		return m;
	}

	static void TestVmat()
	{
		var vmat = BakeMaterial.VmatSource( "materials/a/a_color.png", "materials/a/a_normal.png", "materials/a/a_rough.png", "materials/a/a_ao.png" );
		Check( "the vmat binds complex", vmat.Contains( "shader \"complex.vfx\"" ) );
		Check( "and names each map under the shader's own input",
			vmat.Contains( "TextureColor \"materials/a/a_color.png\"" ) && vmat.Contains( "TextureNormal \"materials/a/a_normal.png\"" )
			&& vmat.Contains( "TextureRoughness \"materials/a/a_rough.png\"" ) && vmat.Contains( "TextureAmbientOcclusion \"materials/a/a_ao.png\"" ) );
		Check( "with complex's AO controls, not an invented one",
			vmat.Contains( "g_flAmbientOcclusionDirectDiffuse" ) && vmat.Contains( "g_flAmbientOcclusionDirectSpecular" ) && !vmat.Contains( "g_flAmbientOcclusionStrength" ) );
		Check( "braces balance", vmat.Count( c => c == '{' ) == 1 && vmat.Count( c => c == '}' ) == 1 );

		var bare = BakeMaterial.VmatSource();
		Check( "a missing map falls back to the engine default", bare.Contains( "materials/default/default_color.tga" ) && bare.Contains( "materials/default/default_ao.tga" ) );
	}

	static void TestVerb( string dir )
	{
		var script = string.Join( "\n",
			"add Primitive name=Low shape=Sphere radius=10 divisions=6",
			"add UVProject bodies=Low mode=Unwrap",
			"add Primitive name=High shape=Sphere radius=10 divisions=12",
			"add Subdivide bodies=High levels=1",
			"add Detail bodies=High pass=Grain size=3 depth=0.6",
			"bake Low High verb size=48 maps=normal,ao,curvature,height,cavity cage=1.5 ss=2 vmat=1" );

		var result = BuildScript.Run( new PartStudio(), script, dir );
		Check( "the bake verb runs", result.Ok, result.Error );

		var expected = new[] { "low_normal.png", "low_ao.png", "low_curvature.png", "low_cavity.png", "low_height.png", "low.vmat" }
			.Select( f => Path.Combine( dir, "verb", f ) ).ToArray();
		Check( "and writes the maps and the vmat", expected.All( File.Exists ) && expected.All( result.Written.Contains ),
			string.Join( ", ", result.Written.Select( Path.GetFileName ) ) );
		Check( "and logs what it wrote and the height range",
			result.Log.Count( l => l.StartsWith( "baked " ) ) == 5 && result.Log.Any( l => l.Contains( "height:" ) ) && result.Log.Any( l => l.StartsWith( "wrote " ) && l.EndsWith( ")" ) ),
			string.Join( " | ", result.Log.Where( l => l.StartsWith( "bake" ) || l.StartsWith( "wrote" ) ) ) );

		var vmat = File.Exists( expected[^1] ) ? File.ReadAllText( expected[^1] ) : "";
		Check( "the vmat points at the baked normal and AO by asset path",
			vmat.Contains( "TextureNormal \"verb/low_normal.png\"" ) && vmat.Contains( "TextureAmbientOcclusion \"verb/low_ao.png\"" ), vmat.Length > 0 ? null : "no vmat" );

		var bad = BuildScript.Run( new PartStudio(), script.Replace( "maps=normal,ao,curvature,height,cavity", "maps=normal,shine" ), dir );
		Check( "an unknown map is refused by name", !bad.Ok && bad.Error.Contains( "shine" ), bad.Error );

		var unwrapped = BuildScript.Run( new PartStudio(), script.Replace( "bake Low High", "bake High Low" ), dir );
		Check( "a low body that cannot carry a bake is refused with the reason", !unwrapped.Ok && unwrapped.Error.Contains( "cannot carry a bake" ), unwrapped.Error );
	}
}
