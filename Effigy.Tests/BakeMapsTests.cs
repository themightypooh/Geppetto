using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The bake additions: bent normal, object-space normal, position and ID maps; a cage MESH; low/high
/// pairs matched by name and baked apart (exploded); Selected to Active; 16-bit PNG output; and the
/// `bake` verb's new options (several highs, cage body, bits=16, shader=skin).
///
/// Every map is checked against geometry whose answer is known: a sphere's bent normal is its normal,
/// its object-space normal points along its position, a position map decodes back to the points the
/// rays found, an ID map has exactly one colour per material slot.
/// </summary>
public static class BakeMapsTests
{
	public static void Run( string outDir )
	{
		Report.Section( "bake maps: bent normal, object normal, position, ID; cage mesh; by-name exploded bake; 16-bit PNG" );
		var dir = Path.Combine( outDir, "bakemaps" );
		Directory.CreateDirectory( dir );

		TestSphereMaps( dir );
		TestBentNormalInCreases();
		TestIdMap();
		TestCageMesh();
		TestPairsByName();
		TestExplodedBake();
		TestSelectedToActive();
		TestSixteenBit( dir );
		TestVerb( dir );
		TestWriteSetBindsBentNormal( dir );
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );

	static PolyMesh Low( float radius = 1f )
	{
		var low = Primitives.QuadSphere( radius, 6 );
		UVUnwrap.Unwrap( low );
		return low;
	}

	/// <summary>A smooth sphere of the given radius, dense enough that its facets do not matter.</summary>
	static PolyMesh Smooth( float radius = 1f, Vec3 centre = default )
	{
		var high = CatmullClark.Subdivide( Primitives.QuadSphere( 1f, 12 ), 1 );

		for ( var i = 0; i < high.VertexCount; i++ )
			high.Positions[i] = centre + high.Positions[i].Normal * radius;

		return high;
	}

	static Vec3 Decode( BakedMap map, int i ) =>
		new( map.Rgb[i * 3] / 127.5f - 1f, map.Rgb[i * 3 + 1] / 127.5f - 1f, map.Rgb[i * 3 + 2] / 127.5f - 1f );

	static void TestSphereMaps( string dir )
	{
		const int size = 64;
		var low = Low();
		var high = Smooth();
		var set = NormalBake.BakeAll( low, high, size, size,
			BakeMaps.Normal | BakeMaps.BentNormal | BakeMaps.ObjectNormal | BakeMaps.Position,
			new BakeOptions { AoRays = 64, KeepHits = true } );

		foreach ( var (name, map) in set.Maps )
			PngWriter.WriteFile( Path.Combine( dir, $"sphere_{name}.png" ), map );

		var names = set.Maps.Select( m => m.Name ).ToArray();
		Check( "the new maps come out under their names", names.SequenceEqual( new[] { "normal", "bentnormal", "objectnormal", "position" } ), string.Join( ",", names ) );

		var filled = Enumerable.Range( 0, size * size ).Where( i => set.Texels.Hit[i] ).ToList();
		Check( "and the bake fills the sphere's UVs", filled.Count > size * size / 4, $"{filled.Count} texels" );

		// Nothing occludes a convex sphere, so the open directions average back to the normal.
		var bentDot = filled.Average( i => Vec3.Dot( Decode( set.BentNormal, i ).Normal, Decode( set.Normal, i ).Normal ) );
		var bentMin = filled.Min( i => Vec3.Dot( Decode( set.BentNormal, i ).Normal, Decode( set.Normal, i ).Normal ) );
		Check( "a sphere's bent normal is its normal (mean cos ≥ 0.998)", bentDot >= 0.998f && bentMin > 0.98f, $"mean {bentDot:0.0000}, worst {bentMin:0.0000}" );

		// Object space: the normal of a unit sphere at p is p.
		var objDot = filled.Average( i => Vec3.Dot( Decode( set.ObjectNormal, i ).Normal, set.Texels.Point[i].Normal ) );
		Check( "the object-space normal points along the position (mean cos ≥ 0.999)", objDot >= 0.999f, $"{objDot:0.00000}" );

		var extent = set.PositionMax - set.PositionMin;
		Check( "the position map spans the sphere's bounds", MathF.Abs( extent.x - 2f ) < 0.01f && MathF.Abs( extent.z - 2f ) < 0.01f && set.Report.Contains( "position:" ),
			$"{set.PositionMin} .. {set.PositionMax}" );

		var worst = filled.Max( i =>
		{
			var c = new Vec3( set.Position.Rgb[i * 3], set.Position.Rgb[i * 3 + 1], set.Position.Rgb[i * 3 + 2] ) / 255f;
			var p = set.PositionMin + new Vec3( c.x * extent.x, c.y * extent.y, c.z * extent.z );
			return (p - set.Texels.Point[i]).Length;
		} );
		Check( "and decodes back to where each texel's ray landed (within 8-bit steps)", worst < 2f / 255f * 2f * 1.5f, $"worst {worst:0.00000}" );

		var flat = NormalBake.BakeAll( low, high, size, size, BakeMaps.Normal, new BakeOptions { AoRays = 64 } );
		Check( "adding maps leaves the normal map byte-identical", flat.Normal.Rgb.SequenceEqual( set.Normal.Rgb ) );
	}

	/// <summary>A sculpt with real creases: there the bent normal leans out of the crease, on the domes it does not.</summary>
	static void TestBentNormalInCreases()
	{
		const int size = 96;
		var low = Low();
		var high = CatmullClark.Subdivide( Primitives.QuadSphere( 1f, 12 ), 2 );

		for ( var i = 0; i < high.VertexCount; i++ )
			high.Positions[i] = high.Positions[i].Normal;

		SurfaceDetail.Grain( high, 0.01f, 2f, 0.3f, 0.06f, 3 );
		var set = NormalBake.BakeAll( low, high, size, size, BakeMaps.Normal | BakeMaps.BentNormal | BakeMaps.AmbientOcclusion, new BakeOptions { AoRays = 64 } );
		var filled = Enumerable.Range( 0, size * size ).Where( i => set.AmbientOcclusion.Filled[i] ).OrderBy( i => set.AmbientOcclusion.Rgb[i * 3] ).ToList();
		var tenth = filled.Count / 10;

		float Lean( IEnumerable<int> texels ) => texels.Average( i => 1f - Vec3.Dot( Decode( set.BentNormal, i ).Normal, Decode( set.Normal, i ).Normal ) );

		var shut = Lean( filled.Take( tenth ) );
		var open = Lean( filled.Skip( filled.Count - tenth ) );
		Check( "the bent normal leans away from the normal where AO is darkest, not where it is open", shut > open * 2f && shut > 0.002f,
			$"darkest tenth 1-cos {shut:0.00000}, most open {open:0.00000}" );
	}

	static void TestIdMap()
	{
		const int size = 64;
		var low = Low();
		var high = Smooth();

		// Slot 0 above the equator, slot 3 below: an ID map should show exactly two colours.
		foreach ( var f in high.Faces )
			f.Material = f.Indices.Average( v => high.Positions[v].z ) >= 0f ? 0 : 3;

		var set = NormalBake.BakeAll( low, high, size, size, BakeMaps.MaterialId, new BakeOptions { KeepHits = true } );
		var map = set.MaterialId;
		var colours = Enumerable.Range( 0, size * size ).Where( i => map.Filled[i] ).Select( i => (map.Rgb[i * 3], map.Rgb[i * 3 + 1], map.Rgb[i * 3 + 2]) ).Distinct().ToList();
		Check( "the ID map has one colour per material slot", colours.Count == 2 && set.IdColors.Count == 2 && set.IdColors.ContainsKey( 0 ) && set.IdColors.ContainsKey( 3 ),
			string.Join( " ", colours ) );
		Check( "and the two are far apart", Math.Abs( set.IdColors[0].R - set.IdColors[3].R ) + Math.Abs( set.IdColors[0].G - set.IdColors[3].G ) + Math.Abs( set.IdColors[0].B - set.IdColors[3].B ) > 200 );

		var wrong = Enumerable.Range( 0, size * size ).Count( i =>
		{
			if ( !set.Texels.Hit[i] || MathF.Abs( set.Texels.Point[i].z ) < 0.1f )
				return false;

			var want = set.IdColors[set.Texels.Point[i].z > 0f ? 0 : 3];
			return (map.Rgb[i * 3], map.Rgb[i * 3 + 1], map.Rgb[i * 3 + 2]) != want;
		} );
		Check( "each texel carries the colour of the slot its ray hit", wrong == 0, $"{wrong} wrong" );

		var all = Enumerable.Range( 0, size * size ).Select( i => (map.Rgb[i * 3], map.Rgb[i * 3 + 1], map.Rgb[i * 3 + 2]) ).Distinct().Count();
		Check( "padding copies ids instead of blending them into a third colour", all <= 3, $"{all} colours in the whole image (two ids and black)" );
	}

	static PolyMesh Scaled( PolyMesh mesh, float s )
	{
		var copy = BakePairs.Combine( new[] { mesh, new PolyMesh() }, out _ );

		for ( var i = 0; i < copy.VertexCount; i++ )
			copy.Positions[i] *= s;

		return copy;
	}

	static void TestCageMesh()
	{
		const int size = 48;
		var low = Low();

		// The sculpt sits 0.15 OUTSIDE the low body everywhere, so rays must start beyond it.
		var high = Smooth( 1.15f );
		var flush = NormalBake.BakeAll( low, high, size, size, BakeMaps.Normal | BakeMaps.Height, new BakeOptions { CageMesh = Scaled( low, 1f ) } );
		var cage = NormalBake.BakeAll( low, high, size, size, BakeMaps.Normal | BakeMaps.Height, new BakeOptions { CageMesh = Scaled( low, 1.3f ) } );

		Check( "a cage left on the low surface finds little of a sculpt outside it", flush.Hits < flush.Samples * 0.2f, $"{flush.Hits}/{flush.Samples}" );
		Check( "a cage pushed out past the sculpt finds all of it", cage.Hits >= cage.Samples * 0.98f, $"{cage.Hits}/{cage.Samples}" );
		Check( "and measures the sculpt standing 0.15 proud", cage.HeightMin > 0.1f && cage.HeightMax < 0.25f, $"{cage.HeightMin:0.000}..{cage.HeightMax:0.000}" );

		// The rays aim cage → low. A cage pushed out evenly fires along the normal, so the map should
		// agree with the scalar cage of the same width.
		var scalar = NormalBake.BakeAll( low, high, size, size, BakeMaps.Normal, new BakeOptions { Cage = 0.3f } );
		var diff = Enumerable.Range( 0, size * size ).Where( i => scalar.Normal.Filled[i] && cage.Normal.Filled[i] )
			.Average( i => 1f - Vec3.Dot( Decode( scalar.Normal, i ).Normal, Decode( cage.Normal, i ).Normal ) );
		Check( "an evenly inflated cage mesh bakes what a cage distance does", diff < 0.002f, $"mean 1-cos {diff:0.00000}" );

		var bad = Primitives.QuadSphere( 1.3f, 5 );
		var threw = "";

		try { NormalBake.BakeAll( low, high, size, size, BakeMaps.Normal, new BakeOptions { CageMesh = bad } ); }
		catch ( ArgumentException e ) { threw = e.Message; }

		Check( "a cage that is not a copy of the low body is refused with the reason", threw.Contains( "vertices" ) && threw.Contains( "copy of the low body" ), threw );
	}

	static void TestPairsByName()
	{
		var m = Primitives.QuadSphere( 1f, 3 );
		var bodies = new (string, PolyMesh)[]
		{
			("Arm_low", m), ("Arm_high", m), ("arm_HIGH_2", m), ("Arm_cage", Scaled( m, 1.2f )),
			("Leg_low", m), ("Leg.high3", m), ("Orphan_high", m), ("Lonely_low", m), ("Plain", m), ("Arm_highlight", m),
		};

		var pairs = BakePairs.Match( bodies, out var problems );
		var arm = pairs.FirstOrDefault( p => p.Name == "Arm" );
		Check( "_low bodies pair with their _high bodies by base name, case-insensitive, numbered highs too",
			pairs.Count == 2 && arm is { Highs.Count: 2 } && arm.Cage is not null && pairs[1].Highs.Count == 1,
			string.Join( "; ", pairs.Select( p => $"{p.LowName}<-{string.Join( ",", p.Highs.Select( h => h.Name ) )} cage {p.CageName}" ) ) );
		Check( "a body that only looks like a suffix (Arm_highlight) and a plain name are left alone",
			pairs.All( p => p.Highs.All( h => h.Name != "Arm_highlight" ) ) && problems.All( p => !p.Contains( "Plain" ) && !p.Contains( "highlight" ) ) );
		Check( "a high with no low and a low with no high are reported, not dropped quietly",
			problems.Any( p => p.Contains( "Orphan_high" ) ) && problems.Any( p => p.Contains( "Lonely_low" ) ), string.Join( " | ", problems ) );

		var groups = BakePairs.ToGroups( pairs, idByBody: true );
		Check( "two highs become one high mesh per group, with an id per source body",
			groups[0].High.FaceCount == m.FaceCount * 2 && groups[0].HighIds.Distinct().SequenceEqual( new[] { 0, 1 } ) && groups[1].HighIds.All( i => i == 2 ) );
	}

	/// <summary>
	/// Two spheres almost touching, each a low/high pair. Baked as one, each one's AO is darkened by
	/// the other; baked apart (by name) it is not — the point of an exploded bake.
	/// </summary>
	static void TestExplodedBake()
	{
		const int size = 96;
		var offset = new Vec3( 2.1f, 0, 0 );
		var aLow = Low();
		var bLow = Low();

		for ( var i = 0; i < bLow.VertexCount; i++ )
			bLow.Positions[i] += offset;

		// One atlas: A in the left half, B in the right.
		foreach ( var f in aLow.Faces )
			for ( var c = 0; c < f.Count; c++ )
				f.UVs[c] = new Vec2( f.UVs[c].x * 0.5f, f.UVs[c].y );

		foreach ( var f in bLow.Faces )
			for ( var c = 0; c < f.Count; c++ )
				f.UVs[c] = new Vec2( 0.5f + f.UVs[c].x * 0.5f, f.UVs[c].y );

		var aHigh = Smooth();
		var bHigh = Smooth( 1f, offset );
		Check( "the two lows share one atlas without overlapping", NormalBake.Measure( new[] { aLow, bLow } ).CanBake );

		var options = new BakeOptions { AoRays = 64, AoDistance = 0.6f, KeepHits = true };
		var pairs = BakePairs.Match( new[] { ("A_low", aLow), ("A_high", aHigh), ("B_low", bLow), ("B_high", bHigh) }, out _ );
		var apart = NormalBake.BakeGroups( BakePairs.ToGroups( pairs ), size, size, BakeMaps.AmbientOcclusion, options );
		var together = NormalBake.BakeAll( BakePairs.Combine( new[] { aLow, bLow }, out _ ), BakePairs.Combine( new[] { aHigh, bHigh }, out _ ),
			size, size, BakeMaps.AmbientOcclusion, options );

		// Which sphere each texel belongs to is known from the atlas: A on the left half, B on the right.
		Vec3 Own( int i ) => i % size < size / 2 ? Vec3.Zero : offset;
		var hit = Enumerable.Range( 0, size * size ).Where( i => apart.Texels.Hit[i] ).ToList();
		var strayApart = hit.Count( i => MathF.Abs( (apart.Texels.Point[i] - Own( i )).Length - 1f ) > 0.02f );
		var strayTogether = hit.Count( i => together.Texels.Hit[i] && MathF.Abs( (together.Texels.Point[i] - Own( i )).Length - 1f ) > 0.02f );
		Check( "baked apart by name, every texel's ray lands on its own high", apart.Hits >= apart.Samples * 0.98f && strayApart == 0, $"{strayApart} stray of {hit.Count}" );
		Check( "baked together, rays near the gap catch the neighbour instead (the bleed exploding avoids)", strayTogether > 8, $"{strayTogether} stray" );

		// And the AO either side of the gap is not darkened by the other part.
		var facing = hit.Where( i => apart.Texels.Point[i].x > 0.95f && apart.Texels.Point[i].x < 1.15f ).ToList();
		var ao = facing.Count == 0 ? 0f : (float)facing.Average( i => apart.AmbientOcclusion.Rgb[i * 3] ) / 255f;
		Check( "and the faces either side of the gap stay open in the AO", facing.Count > 8 && ao > 0.97f, $"{facing.Count} texels, AO {ao:0.000}" );
	}

	static void TestSelectedToActive()
	{
		var low = Low();
		var pair = BakePairs.SelectedToActive( ("Body", low ), new[] { ("Body_detail", Smooth()), ("Body_cage", Scaled( low, 1.2f )), ("Body", low) }, out var problem );
		Check( "Selected to Active: the active body is the low, a _cage body the cage, the rest highs",
			pair is not null && pair.Highs.Count == 1 && pair.Highs[0].Name == "Body_detail" && pair.CageName == "Body_cage", problem );

		var none = BakePairs.SelectedToActive( ("Body", low), new[] { ("Body", low) }, out var why );
		Check( "with nothing else selected it says how to select", none is null && why.Contains( "Shift+click" ), why );

		var bad = BakePairs.SelectedToActive( ("Body", low), new[] { ("Hi", Smooth()), ("Body_cage", Smooth()) }, out var whyCage );
		Check( "a cage that does not fit is refused, naming it", bad is null && whyCage.Contains( "Body_cage" ), whyCage );
	}

	static void TestSixteenBit( string dir )
	{
		const int size = 48;
		var low = Low();
		var high = CatmullClark.Subdivide( Primitives.QuadSphere( 1f, 12 ), 1 );

		for ( var i = 0; i < high.VertexCount; i++ )
			high.Positions[i] = high.Positions[i].Normal * (1f + 0.02f * MathF.Sin( high.Positions[i].Normal.z * 9f ));

		var eight = NormalBake.BakeAll( low, high, size, size, BakeMaps.Height | BakeMaps.Normal | BakeMaps.AmbientOcclusion );
		var wide = NormalBake.BakeAll( low, high, size, size, BakeMaps.Height | BakeMaps.Normal | BakeMaps.AmbientOcclusion, new BakeOptions { SixteenBit = true } );

		Check( "8-bit stays the default: no 16-bit copy unless asked", eight.Height.Rgb16 is null && eight.Normal.Rgb16 is null );
		Check( "16-bit is kept for height and normal, not for AO", wide.Height.Rgb16 is not null && wide.Normal.Rgb16 is not null && wide.AmbientOcclusion.Rgb16 is null );
		Check( "the 8-bit bytes are the same either way", wide.Height.Rgb.SequenceEqual( eight.Height.Rgb ) && wide.Normal.Rgb.SequenceEqual( eight.Normal.Rgb ) );

		var levels8 = Enumerable.Range( 0, size * size ).Where( i => wide.Height.Filled[i] ).Select( i => wide.Height.Rgb[i * 3] ).Distinct().Count();
		var levels16 = Enumerable.Range( 0, size * size ).Where( i => wide.Height.Filled[i] ).Select( i => wide.Height.Rgb16[i * 3] ).Distinct().Count();
		Check( "a 16-bit height map holds many more levels than 8 bits", levels16 > levels8 * 4, $"{levels16} vs {levels8}" );

		// Exactly where the rays landed; within a few steps in the padding, where each depth averages
		// its own neighbours and rounds on its own every pass.
		int Off( int i ) => Math.Abs( (wide.Height.Rgb16[i] + 128) / 257 - wide.Height.Rgb[i] );
		var onSurface = Enumerable.Range( 0, size * size * 3 ).Where( i => wide.Height.Filled[i / 3] ).Max( Off );
		var padding = Enumerable.Range( 0, size * size * 3 ).Where( i => !wide.Height.Filled[i / 3] ).Max( Off );
		Check( "and it rounds to the 8-bit map: exactly on the surface, within a few steps in the padding", onSurface <= 1 && padding <= 4, $"surface {onSurface}, padding {padding}" );

		var path = Path.Combine( dir, "height16.png" );
		PngWriter.WriteFile( path, wide.Height );
		var bytes = File.ReadAllBytes( path );
		Check( "it is written as a 16-bit PNG", bytes[24] == 16 && bytes[25] == 2, $"depth {bytes[24]}, colour {bytes[25]}" );

		var (rgba, w, h) = PngReader.Read16( bytes );
		var same = w == size && h == size && Enumerable.Range( 0, size * size ).All( i =>
			rgba[i * 4] == wide.Height.Rgb16[i * 3] && rgba[i * 4 + 1] == wide.Height.Rgb16[i * 3 + 1] && rgba[i * 4 + 2] == wide.Height.Rgb16[i * 3 + 2] && rgba[i * 4 + 3] == 65535 );
		Check( "PngReader reads it back sample for sample", same );

		var flipped = Path.Combine( dir, "height16_flipv.png" );
		PngWriter.WriteFile( flipped, wide.Height, flipVertically: true );
		var (back, _, _) = PngReader.Read16( File.ReadAllBytes( flipped ) );
		Check( "and the vertical flip moves 16-bit rows too", back[0] == wide.Height.Rgb16[(size - 1) * size * 3] );

		var (low8, _, _) = PngReader.Read( bytes );
		Check( "the 8-bit reader takes a 16-bit file's high bytes", low8[0] == (byte)(wide.Height.Rgb16[0] >> 8) );

		var plain = Path.Combine( dir, "height8.png" );
		PngWriter.WriteFile( plain, eight.Height );
		var (rgba8, _, _) = PngReader.Read16( File.ReadAllBytes( plain ) );
		Check( "an 8-bit PNG reads as 16-bit widened exactly (x257)", rgba8[0] == eight.Height.Rgb[0] * 257 && File.ReadAllBytes( plain )[24] == 8 );
	}

	static void TestVerb( string dir )
	{
		var script = string.Join( "\n",
			"add Primitive name=Low shape=Sphere radius=10 divisions=6",
			"add UVProject bodies=Low mode=Unwrap",
			"add Primitive name=LowCage shape=Sphere radius=11.5 divisions=6",
			"add Primitive name=High shape=Sphere radius=10 divisions=12",
			"add Subdivide bodies=High levels=1",
			"add Detail bodies=High pass=Grain size=3 depth=0.6",
			"add Primitive name=High2 shape=Sphere radius=2 divisions=4",
			"bake Low High,High2 pairs size=32 maps=normal,height,bentnormal,thickness,id cage=LowCage bits=16 id=body vmat=1 shader=skin preset=Head k=3" );

		var result = BuildScript.Run( new PartStudio(), script, dir );
		Check( "bake takes several highs, a cage body, bits=16, id=body and shader=skin", result.Ok, result.Error );

		var folder = Path.Combine( dir, "pairs" );
		string F( string f ) => Path.Combine( folder, f );
		var want = new[] { "low_normal.png", "low_height.png", "low_bentnormal.png", "low_id.png", "low_transmission.png", "low.vmat" };
		Check( "and writes the maps, the transmission mask and the vmat", want.All( f => File.Exists( F( f ) ) && result.Written.Contains( F( f ) ) ),
			string.Join( ", ", result.Written.Select( Path.GetFileName ) ) );
		Check( "height and normal are 16-bit, the ID map 8-bit",
			File.Exists( F( "low_height.png" ) ) && File.ReadAllBytes( F( "low_height.png" ) )[24] == 16 && File.ReadAllBytes( F( "low_normal.png" ) )[24] == 16
			&& File.ReadAllBytes( F( "low_id.png" ) )[24] == 8 );
		Check( "the log names the pair, the cage mesh and both highs",
			result.Log.Any( l => l.Contains( "Low from High, High2, cage LowCage" ) ) && result.Log.Any( l => l.Contains( "cage mesh" ) ) && result.Log.Any( l => l.StartsWith( "bake: id:" ) ),
			string.Join( " | ", result.Log.Where( l => l.StartsWith( "bake" ) ) ) );

		var vmat = File.Exists( F( "low.vmat" ) ) ? File.ReadAllText( F( "low.vmat" ) ) : "";
		Check( "the skin vmat binds the bent normal and switches it on",
			vmat.Contains( "shaders/skin.shader" ) && vmat.Contains( "F_USE_BENT_NORMALS 1" ) && vmat.Contains( "TextureBentNormal \"pairs/low_bentnormal.png\"" )
			&& vmat.Contains( "TextureTransmissiveMask \"pairs/low_transmission.png\"" ), vmat.Length > 0 ? null : "no vmat" );

		var byName = string.Join( "\n",
			"add Primitive name=Arm_low shape=Sphere radius=10 divisions=6",
			"add UVProject bodies=Arm_low mode=Unwrap",
			"add Primitive name=Arm_high shape=Sphere radius=10 divisions=12",
			"add Primitive name=Stray_high shape=Sphere radius=3 divisions=4",
			"bake * named size=24 maps=normal,ao" );
		var named = BuildScript.Run( new PartStudio(), byName, dir );
		Check( "bake * pairs bodies by name and warns about the stray", named.Ok && File.Exists( Path.Combine( dir, "named", "arm_normal.png" ) )
			&& named.Log.Any( l => l.Contains( "warning" ) && l.Contains( "Stray_high" ) ), named.Error ?? string.Join( " | ", named.Log ) );

		var none = BuildScript.Run( new PartStudio(), "add Primitive name=A shape=Sphere radius=1\nbake * nothing", dir );
		Check( "bake * with no pairs fails and says how to name them", !none.Ok && none.Error.Contains( "_low" ) && none.Error.Contains( "_high" ), none.Error );

		var badCage = BuildScript.Run( new PartStudio(), script.Replace( "cage=LowCage", "cage=High" ), dir );
		Check( "a cage body that does not match the low body is refused", !badCage.Ok && badCage.Error.Contains( "cage=High" ), badCage.Error );

		var badBits = BuildScript.Run( new PartStudio(), script.Replace( "bits=16", "bits=12" ), dir );
		Check( "bits other than 8 or 16 are refused", !badBits.Ok && badBits.Error.Contains( "bits=12" ), badBits.Error );
	}

	static void TestWriteSetBindsBentNormal( string dir )
	{
		var set = NormalBake.BakeAll( Low(), Smooth(), 16, 16, BakeMaps.Normal | BakeMaps.BentNormal | BakeMaps.AmbientOcclusion, new BakeOptions { AoRays = 8 } );
		var folder = Path.Combine( dir, "writeset" );
		var written = BakeMaterial.WriteSet( set, folder, "s", assetsRoot: dir, material: "Skin: Body" );
		var vmat = File.ReadAllText( written[^1] );
		Check( "the editor's skin vmat binds a baked bent normal", vmat.Contains( "F_USE_BENT_NORMALS 1" ) && vmat.Contains( "TextureBentNormal \"writeset/s_bentnormal.png\"" ) );

		var plain = BakeMaterial.WriteSet( NormalBake.BakeAll( Low(), Smooth(), 16, 16, BakeMaps.Normal ), folder, "p", assetsRoot: dir, material: "Skin: Body" );
		Check( "and leaves it off without one", !File.ReadAllText( plain[^1] ).Contains( "F_USE_BENT_NORMALS" ) );
	}
}
