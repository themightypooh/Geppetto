using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Texture painting v2: surface channels beyond colour, layers that composite with blend modes and
/// opacity, fill layers, layer masks (painted and generated), and the texture set they export as.
///
/// Every number here is worked by hand from the W3C compositing formula or from the fill values, so
/// a regression in the arithmetic shows up as a wrong byte, not as "looks a bit off".
/// </summary>
public static class PaintLayerTests
{
	public static void Run()
	{
		Section( "paint layers: blend modes follow the compositing formula" );
		TestBlendModesOnOpaqueBackdrop();
		TestNormalIsSourceOver();
		TestLayerOverNothingIsItself();
		TestCompositeLeavesInputsAlone();

		Section( "paint layers: layers stack in tree order" );
		TestLayerOrder();
		TestMultiplyLayer();
		TestEmptyLayerPassesThrough();

		Section( "paint layers: masks confine a layer" );
		TestPaintedMaskHides();
		TestMaterialIdMask();
		TestGradientMask();
		TestShape();
		TestBakedGeneratorsRun();

		Section( "paint channels: roughness, metallic and height land on their own maps" );
		TestRoughnessStroke();
		TestFillChannels();
		TestHeightToNormal();
		TestVmatBindsChannels();
		TestScriptWritesTextureSet();

		Section( "paint layers: save and load" );
		TestChannelsRoundTrip();
		TestOldDocumentReadsAsColour();
		TestUnknownChannelIsDropped();

		Section( "paint layers: no silent no-ops" );
		TestOpacityZeroWarns();
		TestMissingSlotWarns();
		TestFillWithNothingWarns();
		TestMaskOnlyLayerWarns();
		TestHasUVs();
	}

	// --- blend math --------------------------------------------------------------------------

	static void TestBlendModesOnOpaqueBackdrop()
	{
		// Backdrop 128 (0.502), source 128, full alpha.
		(byte, byte, byte, byte) Over( PaintBlendMode mode, byte b, byte s ) => PaintLayers.Over( b, b, b, 255, s, s, s, 1f, mode );

		Check( "Multiply 0.5 × 0.5 = 0.25", Over( PaintBlendMode.Multiply, 128, 128 ).Item1 == 64, $"{Over( PaintBlendMode.Multiply, 128, 128 )}" );
		Check( "Screen 0.5, 0.5 = 0.75", Over( PaintBlendMode.Screen, 128, 128 ).Item1 == 192, $"{Over( PaintBlendMode.Screen, 128, 128 )}" );
		Check( "Add 0.5 + 0.5 clips to 1", Over( PaintBlendMode.Add, 128, 128 ).Item1 == 255 );
		Check( "Add 0.2 + 0.3 = 0.5", Over( PaintBlendMode.Add, 51, 77 ).Item1 == 128, $"{Over( PaintBlendMode.Add, 51, 77 )}" );

		// Overlay is decided by the BACKDROP: dark backdrop multiplies (2·cb·cs), light backdrop screens.
		var darkOverlay = Over( PaintBlendMode.Overlay, 64, 128 ).Item1;   // 2 × 0.251 × 0.502 = 0.252
		var lightOverlay = Over( PaintBlendMode.Overlay, 192, 128 ).Item1; // screen(0.502, 0.506) = 0.754
		Check( "Overlay on a dark backdrop multiplies", darkOverlay == 64, $"{darkOverlay}" );
		Check( "Overlay on a light backdrop screens", lightOverlay == 192, $"{lightOverlay}" );

		// Half opacity: Multiply at αs 0.5 over opaque white with 0.502 grey = lerp(1, 0.502, 0.5) = 0.751.
		var half = PaintLayers.Over( 255, 255, 255, 255, 128, 128, 128, 0.5f, PaintBlendMode.Multiply );
		Check( "half-opacity Multiply goes halfway", half.Item1 == 192 && half.Item4 == 255, $"{half}" );
	}

	static void TestNormalIsSourceOver()
	{
		var ok = true;
		var rng = new Random( 7 );

		for ( var n = 0; n < 500 && ok; n++ )
		{
			var d = new byte[4];
			var s = new byte[3];
			rng.NextBytes( d );
			rng.NextBytes( s );
			var a = (float)rng.NextDouble();

			ok = PaintLayers.Over( d[0], d[1], d[2], d[3], s[0], s[1], s[2], a, PaintBlendMode.Normal )
				== PaintCanvas.SourceOver( d[0], d[1], d[2], d[3], s[0], s[1], s[2], a );
		}

		Check( "Normal is bit-for-bit the canvas's source-over", ok );
	}

	static void TestLayerOverNothingIsItself()
	{
		foreach ( var mode in Enum.GetValues<PaintBlendMode>() )
		{
			var r = PaintLayers.Over( 0, 0, 0, 0, 200, 100, 50, 1f, mode );
			Check( $"{mode} over a transparent backdrop is the layer's own colour", r == (200, 100, 50, 255), $"{r}" );
		}
	}

	static void TestCompositeLeavesInputsAlone()
	{
		var below = PaintLayers.Solid( 4, 4, 10, 20, 30 );
		var layer = PaintLayers.Solid( 4, 4, 200, 200, 200 );
		var before = (byte[])below.Rgba.Clone();
		var result = PaintLayers.Composite( below, layer, PaintBlendMode.Normal, 0.5f, null );

		Check( "compositing makes a new canvas", !ReferenceEquals( result, below ) );
		Check( "and leaves the layer below untouched", below.Rgba.SequenceEqual( before ) );

		var bigger = PaintLayers.Composite( PaintLayers.Solid( 2, 2, 10, 20, 30 ), layer, PaintBlendMode.Normal, 0f, null );
		Check( "a smaller backdrop is resampled to the layer's size", bigger.Width == 4 && bigger.Rgba[0] == 10 && bigger.Rgba[3] == 255 );
	}

	// --- stacking in a studio ----------------------------------------------------------------

	static PartStudio Box()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 1f;
		var uv = studio.Add( new UVProjectFeature() );
		uv.Mode.Index = Array.IndexOf( uv.Mode.Options, "Unwrap" );
		return studio;
	}

	static PaintFeature FillLayer( PartStudio studio, Vec3 colour, float opacity = 1f, PaintBlendMode mode = PaintBlendMode.Normal )
	{
		var paint = studio.Add( new PaintFeature() );
		paint.Resolution.Value = 64;
		paint.Fill.Value = true;
		paint.FillColor.Value = colour;
		paint.Opacity.Value = opacity;
		paint.Mode.Index = (int)mode;
		return paint;
	}

	static byte[] Texel( PaintCanvas c, int x, int y ) => c.Rgba.Skip( (y * c.Width + x) * 4 ).Take( 4 ).ToArray();

	static string Show( byte[] t ) => $"({string.Join( ",", t )})";

	static void TestLayerOrder()
	{
		var red = new Vec3( 1, 0, 0 );
		var blue = new Vec3( 0, 0, 1 );

		var a = Box();
		FillLayer( a, red );
		FillLayer( a, blue, 0.25f );
		a.Rebuild();
		var redThenBlue = Texel( a.Bodies[0].Mesh.Paint, 10, 10 );

		var b = Box();
		FillLayer( b, blue );
		FillLayer( b, red, 0.25f );
		b.Rebuild();
		var blueThenRed = Texel( b.Bodies[0].Mesh.Paint, 10, 10 );

		Check( "blue at 25% over red is (191,0,64)", redThenBlue.SequenceEqual( new byte[] { 191, 0, 64, 255 } ), Show( redThenBlue ) );
		Check( "swapping the layers swaps the result", blueThenRed.SequenceEqual( new byte[] { 64, 0, 191, 255 } ), Show( blueThenRed ) );
	}

	static void TestMultiplyLayer()
	{
		var s = Box();
		FillLayer( s, new Vec3( 200 / 255f, 200 / 255f, 200 / 255f ) );
		FillLayer( s, new Vec3( 128 / 255f, 64 / 255f, 1f ), 1f, PaintBlendMode.Multiply );
		s.Rebuild();
		var t = Texel( s.Bodies[0].Mesh.Paint, 5, 5 );

		Check( "a Multiply layer darkens the layer below it (200·128, 200·64, 200·255)",
			t.SequenceEqual( new byte[] { 100, 50, 200, 255 } ), Show( t ) );
	}

	static void TestEmptyLayerPassesThrough()
	{
		var s = Box();
		var bottom = FillLayer( s, new Vec3( 1, 0, 0 ) );
		var empty = s.Add( new PaintFeature() );
		s.Rebuild();

		Check( "a layer with nothing on it hands the paint below straight through",
			ReferenceEquals( s.Bodies[0].Mesh.Paint, bottom.Canvas ), empty.Diagnostic?.Problem );
		Check( "and says nothing about it — every layer starts empty", empty.Diagnostic is null, empty.Diagnostic?.Problem );
	}

	// --- masks -------------------------------------------------------------------------------

	/// <summary>A stroke straight down onto the top of the unit box.</summary>
	static PaintStroke TopStroke( PaintChannel channel, float value, float radius = 0.3f )
	{
		var stroke = new PaintStroke { Channel = channel, R = value, G = value, B = value, Radius = radius, Falloff = BrushFalloff.Constant };
		stroke.Path.Add( new PaintStrokePoint( new Vec3( 0, 0, 0.5f ), new Vec3( 0, 0, 1 ) ) );
		return stroke;
	}

	/// <summary>The texel at the centre of the top face: the stroke's centre in UV space.</summary>
	static (int X, int Y) TopCentre( PolyMesh mesh, int size )
	{
		var top = mesh.Faces.First( f => Vec3.Dot( mesh.FaceNormal( f ), new Vec3( 0, 0, 1 ) ) > 0.9f );
		var uv = new Vec2( top.UVs.Average( u => u.x ), top.UVs.Average( u => u.y ) );
		return ((int)(uv.x * size), (int)(uv.y * size));
	}

	static void TestPaintedMaskHides()
	{
		var s = Box();
		var fill = FillLayer( s, new Vec3( 1, 0, 0 ) );
		fill.AddStroke( TopStroke( PaintChannel.Mask, 0f ) );
		s.Rebuild();

		var mesh = s.Bodies[0].Mesh;
		var (x, y) = TopCentre( mesh, 64 );
		var hidden = Texel( mesh.Paint, x, y );
		var shown = Texel( mesh.Paint, 0, 0 );

		Check( "black mask paint hides the fill under the stroke", hidden[3] == 0, Show( hidden ) );
		Check( "and the fill shows everywhere else", shown.SequenceEqual( new byte[] { 255, 0, 0, 255 } ), Show( shown ) );
		Check( "mask paint never becomes colour on the body", mesh.Paint.RoughnessMap is null && mesh.Paint.HeightMap is null );

		// White paint over the generator's hidden region shows it again.
		var t = Box();
		var gen = FillLayer( t, new Vec3( 0, 1, 0 ) );
		gen.InvertMask.Value = true; // nothing shows...
		gen.AddStroke( TopStroke( PaintChannel.Mask, 0f ) ); // ...except where black paint, inverted, is white.
		t.Rebuild();
		var m2 = t.Bodies[0].Mesh;
		var (x2, y2) = TopCentre( m2, 64 );
		Check( "Invert Mask turns hidden into shown", Texel( m2.Paint, x2, y2 )[1] == 255 && Texel( m2.Paint, 0, 0 )[3] == 0,
			$"{Show( Texel( m2.Paint, x2, y2 ) )} / {Show( Texel( m2.Paint, 0, 0 ) )}" );
	}

	static void TestMaterialIdMask()
	{
		var mesh = Primitives.Box( 1f, 1f, 1f );
		UVUnwrapped( mesh );
		var top = mesh.Faces.First( f => Vec3.Dot( mesh.FaceNormal( f ), new Vec3( 0, 0, 1 ) ) > 0.9f );
		top.Material = 3;

		var raw = PaintMasks.Generate( mesh, 64, PaintMaskGenerator.MaterialId, 3 );
		var (x, y) = TopCentre( mesh, 64 );
		var other = mesh.Faces.First( f => f.Material != 3 );
		var ou = new Vec2( other.UVs.Average( u => u.x ), other.UVs.Average( u => u.y ) );

		Check( "Material ID is 1 on the slot's faces", raw[y * 64 + x] == 1f, $"{raw[y * 64 + x]}" );
		Check( "and 0 on the others", raw[(int)(ou.y * 64) * 64 + (int)(ou.x * 64)] == 0f );
	}

	static void UVUnwrapped( PolyMesh mesh )
	{
		var s = new PartStudio();
		var box = s.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 1f;
		var uv = s.Add( new UVProjectFeature() );
		uv.Mode.Index = Array.IndexOf( uv.Mode.Options, "Unwrap" );
		s.Rebuild();
		var src = s.Bodies[0].Mesh;
		mesh.Positions = new List<Vec3>( src.Positions );
		mesh.Faces = src.Faces.Select( f => new Face( f.Indices.ToArray(), f.UVs?.ToArray(), f.Material ) ).ToList();
	}

	static void TestGradientMask()
	{
		var mesh = Primitives.Box( 1f, 1f, 1f );
		UVUnwrapped( mesh );
		var raw = PaintMasks.Generate( mesh, 64, PaintMaskGenerator.Gradient, 0 );
		var (x, y) = TopCentre( mesh, 64 );
		var bottom = mesh.Faces.First( f => Vec3.Dot( mesh.FaceNormal( f ), new Vec3( 0, 0, -1 ) ) > 0.9f );
		var bu = new Vec2( bottom.UVs.Average( u => u.x ), bottom.UVs.Average( u => u.y ) );

		Check( "Gradient is 1 on the top face", MathF.Abs( raw[y * 64 + x] - 1f ) < 1e-4f, $"{raw[y * 64 + x]}" );
		Check( "and 0 on the bottom face", raw[(int)(bu.y * 64) * 64 + (int)(bu.x * 64)] < 1e-4f );
		Check( "every value is 0..1", raw.All( v => v >= 0f && v <= 1f ) );
	}

	static void TestShape()
	{
		var hard = PaintLayers.Shape( new[] { 0.2f, 0.5f, 0.8f }, 0.5f, 0f );
		Check( "softness 0 is a hard switch at the level", hard.SequenceEqual( new[] { 0f, 1f, 1f } ), string.Join( ",", hard ) );

		var soft = PaintLayers.Shape( new[] { 0.2f, 0.5f, 0.8f }, 0.5f, 0.4f );
		Check( "a soft mask is half-shown at its level and fully either side of the ramp",
			soft[0] == 0f && MathF.Abs( soft[1] - 0.5f ) < 1e-5f && soft[2] == 1f, string.Join( ",", soft ) );
	}

	static void TestBakedGeneratorsRun()
	{
		var mesh = Primitives.Box( 1f, 1f, 1f );
		UVUnwrapped( mesh );

		foreach ( var gen in new[] { PaintMaskGenerator.EdgeWear, PaintMaskGenerator.Dirt } )
		{
			var raw = PaintMasks.Generate( mesh, 32, gen, 0 );
			Check( $"{gen} gives one 0..1 value per texel", raw.Length == 32 * 32 && raw.All( v => v >= 0f && v <= 1f ) );
		}

		// A larger request is baked at the cap and upsampled, not baked at full size.
		var big = PaintMasks.Generate( mesh, PaintMasks.MaxBakeSize * 2, PaintMaskGenerator.EdgeWear, 0 );
		Check( "a generator above the bake cap still returns the requested size", big.Length == PaintMasks.MaxBakeSize * PaintMasks.MaxBakeSize * 4 );
	}

	// --- channels ----------------------------------------------------------------------------

	static void TestRoughnessStroke()
	{
		var s = Box();
		var paint = s.Add( new PaintFeature() );
		paint.Resolution.Value = 64;
		s.Rebuild();

		var session = new PaintSession( s.Bodies[0].Mesh, 64 ) { Radius = 0.3f, Falloff = BrushFalloff.Constant };
		session.Channel = PaintChannel.Roughness;
		session.Value = 0.2f;
		session.BeginStroke( new Vec3( 0, 0, 5 ), new Vec3( 0, 0, -1 ) );
		var stroke = session.EndStroke();

		Check( "a stroke made on Roughness records its channel", stroke.Channel == PaintChannel.Roughness );
		Check( "and paints its value as a grey", stroke.R == 0.2f && stroke.G == 0.2f && stroke.B == 0.2f );

		paint.AddStroke( stroke );
		s.Rebuild();

		var mesh = s.Bodies[0].Mesh;
		var (x, y) = TopCentre( mesh, 64 );
		var rough = mesh.Paint.RoughnessMap;

		Check( "the body's paint carries a roughness map", rough is not null );
		Check( "roughness 0.2 lands as 51", rough is not null && Texel( rough, x, y ).SequenceEqual( new byte[] { 51, 51, 51, 255 } ),
			rough is null ? "" : Show( Texel( rough, x, y ) ) );
		Check( "the colour map stays unpainted", mesh.Paint.Rgba.Where( ( b, i ) => i % 4 == 3 ).All( a => a == 0 ) );
		Check( "the live canvas shows the roughness channel", session.Canvas.Rgba[(y * 64 + x) * 4] == 51 );

		var images = PaintTextureSet.Images( mesh.Paint );
		var roughImage = images.FirstOrDefault( i => i.Suffix == "rough" );
		Check( "the texture set has a colour and a roughness image, and no metal or normal",
			images.Select( i => i.Suffix ).SequenceEqual( new[] { "color", "rough" } ), string.Join( ",", images.Select( i => i.Suffix ) ) );
		Check( "the exported roughness is 51 where painted and the 0.6 base (153) elsewhere",
			roughImage is not null && roughImage.Rgba[(y * 64 + x) * 4] == 51 && roughImage.Rgba[0] == 153,
			roughImage is null ? "" : $"{roughImage.Rgba[(y * 64 + x) * 4]} / {roughImage.Rgba[0]}" );

		// Switching channel back shows the colour canvas again, and the session's preview is the base.
		session.Channel = PaintChannel.Color;
		var preview = new byte[4];
		session.BakePreview( preview, x, y, 1, 1 );
		Check( "the colour preview under a roughness stroke is the plain base", preview[0] == PaintMaterial.DefaultBaseR, Show( preview ) );
	}

	static void TestFillChannels()
	{
		var s = Box();
		var fill = FillLayer( s, new Vec3( 0, 0, 0 ) );
		fill.FillUseColor.Value = false;
		fill.FillUseMetalness.Value = true;
		fill.FillMetalness.Value = 1f;
		fill.FillUseRoughness.Value = true;
		fill.FillRoughness.Value = 0.3f;
		s.Rebuild();

		var paint = s.Bodies[0].Mesh.Paint;
		Check( "a fill writes only the channels it uses", paint.MetalnessMap is not null && paint.RoughnessMap is not null && paint.HeightMap is null );
		Check( "metallic 1 fills as 255", paint.MetalnessMap?.Rgba[0] == 255 );
		// 76.5 rounds to even, the same rounding every paint byte uses (PaintReplay.ToByte).
		Check( "roughness 0.3 fills as 76", paint.RoughnessMap?.Rgba[0] == 76, $"{paint.RoughnessMap?.Rgba[0]}" );
		Check( "and the colour map stays empty", paint.Rgba[3] == 0 );
	}

	static void TestHeightToNormal()
	{
		var flat = new PaintCanvas( 8, 8 );
		var n = PaintTextureSet.HeightToNormal( flat );
		Check( "unpainted height is a flat normal (128,128,255)", n[0] == 128 && n[1] == 128 && n[2] == 255, $"({n[0]},{n[1]},{n[2]})" );

		// Height rising down the image (+v): the surface tilts toward −v, which s&box's flipped green
		// stores ABOVE 128 — the same sign NormalBake writes for a baked slope the same way.
		var ramp = new PaintCanvas( 8, 8 );

		for ( var y = 0; y < 8; y++ )
		for ( var x = 0; x < 8; x++ )
			ramp.Blend( x, y, (byte)(y * 30), (byte)(y * 30), (byte)(y * 30), 1f );

		var r = PaintTextureSet.HeightToNormal( ramp );
		var i = (4 * 8 + 4) * 4;
		Check( "a slope along v leans green, not red", r[i] == 128 && r[i + 1] > 128 && r[i + 2] < 255, $"({r[i]},{r[i + 1]},{r[i + 2]})" );

		var rampU = new PaintCanvas( 8, 8 );

		for ( var y = 0; y < 8; y++ )
		for ( var x = 0; x < 8; x++ )
			rampU.Blend( x, y, (byte)(x * 30), (byte)(x * 30), (byte)(x * 30), 1f );

		var u = PaintTextureSet.HeightToNormal( rampU );
		Check( "a slope rising along +u leans the normal toward −u (red below 128)", u[i] < 128 && u[i + 1] == 128, $"({u[i]},{u[i + 1]},{u[i + 2]})" );
	}

	static void TestVmatBindsChannels()
	{
		var all = PaintTextureSet.VmatSource( new Dictionary<string, string>
		{
			["color"] = "models/effigy/a_color.png",
			["rough"] = "models/effigy/a_rough.png",
			["metal"] = "models/effigy/a_metal.png",
			["normal"] = "models/effigy/a_normal.png",
		} );

		Check( "the vmat names the colour image", all.Contains( "TextureColor \"models/effigy/a_color.png\"" ) );
		Check( "the vmat names the roughness image", all.Contains( "TextureRoughness \"models/effigy/a_rough.png\"" ) );
		Check( "the vmat names the normal image", all.Contains( "TextureNormal \"models/effigy/a_normal.png\"" ) );
		Check( "a metal image turns on the metalness texture", all.Contains( "F_METALNESS_TEXTURE 1" ) && all.Contains( "TextureMetalness \"models/effigy/a_metal.png\"" ) );
		Check( "the braces balance", all.Count( c => c == '{' ) == 1 && all.Count( c => c == '}' ) == 1 );

		var colourOnly = PaintTextureSet.VmatSource( new Dictionary<string, string> { ["color"] = "c.png" } );
		Check( "colour only keeps the engine defaults",
			colourOnly.Contains( "materials/default/default_rough.tga" ) && colourOnly.Contains( "materials/default/default_normal.tga" )
			&& colourOnly.Contains( "g_flMetalness \"0.000\"" ) && !colourOnly.Contains( "F_METALNESS_TEXTURE" ) );
	}

	// --- document ----------------------------------------------------------------------------

	static void TestChannelsRoundTrip()
	{
		var s = Box();
		var layer = FillLayer( s, new Vec3( 0.1f, 0.2f, 0.3f ), 0.4f, PaintBlendMode.Overlay );
		layer.MaskGenerator.Index = (int)PaintMaskGenerator.Gradient;
		layer.MaskLevel.Value = 0.7f;
		layer.InvertMask.Value = true;
		layer.FillUseHeight.Value = true;
		layer.FillHeight.Value = 0.9f;
		layer.AddStroke( TopStroke( PaintChannel.Color, 1f ) );
		layer.AddStroke( TopStroke( PaintChannel.Roughness, 0.25f ) );
		var erase = TopStroke( PaintChannel.Height, 0f );
		erase.Erase = true;
		layer.AddStroke( erase );
		layer.AddStroke( TopStroke( PaintChannel.Mask, 0f ) );

		var text = StudioDocument.Write( s );
		var back = StudioDocument.Read( text ).Features.OfType<PaintFeature>().Single();

		Check( "every stroke comes back", back.Strokes?.Count == 4 );
		Check( "each stroke keeps its channel, in order",
			back.Strokes is not null && back.Strokes.Select( x => x.Channel ).SequenceEqual(
				new[] { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Height, PaintChannel.Mask } ),
			back.Strokes is null ? "" : string.Join( ",", back.Strokes.Select( x => x.Channel ) ) );
		Check( "an erase on a channel stays an erase on that channel", back.Strokes?[2].Erase == true );
		Check( "the layer settings come back",
			back.BlendMode == PaintBlendMode.Overlay && back.Opacity.Value == 0.4f && back.Fill.Value
			&& back.Generator == PaintMaskGenerator.Gradient && back.MaskLevel.Value == 0.7f && back.InvertMask.Value
			&& back.FillUseHeight.Value && back.FillHeight.Value == 0.9f
			&& back.FillColor.Value.x == 0.1f && back.FillColor.Value.z == 0.3f );
		Check( "saving the loaded document gives the same text", StudioDocument.Write( StudioDocument.Read( text ) ) == text );
		Check( "a colour stroke is written with no tag", text.Contains( "\tstroke Strokes 1 " ) );
		Check( "a roughness stroke is tagged", text.Contains( "\tstroke Strokes @rough " ) );

		// And it composites the same after loading.
		var studio = StudioDocument.Read( text );
		studio.Rebuild();
		s.Rebuild();
		Check( "a loaded layer stack composites to the same pixels",
			studio.Bodies[0].Mesh.Paint.Rgba.SequenceEqual( s.Bodies[0].Mesh.Paint.Rgba )
			&& studio.Bodies[0].Mesh.Paint.HeightMap.Rgba.SequenceEqual( s.Bodies[0].Mesh.Paint.HeightMap.Rgba ) );
	}

	static void TestOldDocumentReadsAsColour()
	{
		var s = Box();
		var layer = s.Add( new PaintFeature() );
		layer.AddStroke( TopStroke( PaintChannel.Color, 0.5f ) );
		var text = StudioDocument.Write( s );

		// An old document: no channel tag, a legacy Blend of Replace (index 1).
		var old = text.Replace( "\tparam Blend 0", "\tparam Blend 1" );
		var back = StudioDocument.Read( old ).Features.OfType<PaintFeature>().Single();

		Check( "an untagged stroke reads as colour", back.Strokes.Single().Channel == PaintChannel.Color );
		Check( "a legacy Blend of Replace does not become Multiply", back.BlendMode == PaintBlendMode.Normal && back.Blend.Index == 1,
			$"{back.BlendMode} / {back.Blend.Index}" );
	}

	static void TestUnknownChannelIsDropped()
	{
		var s = Box();
		var layer = s.Add( new PaintFeature() );
		layer.AddStroke( TopStroke( PaintChannel.Roughness, 0.5f ) );
		layer.AddStroke( TopStroke( PaintChannel.Color, 0.5f ) );
		var text = StudioDocument.Write( s ).Replace( "@rough", "@sheen" );
		var back = StudioDocument.Read( text ).Features.OfType<PaintFeature>().Single();

		Check( "a stroke on a channel this build does not know is dropped, not painted as colour",
			back.Strokes.Count == 1 && back.Strokes[0].Channel == PaintChannel.Color, $"{back.Strokes.Count}" );
	}

	// --- diagnostics -------------------------------------------------------------------------

	static void TestOpacityZeroWarns()
	{
		var s = Box();
		var layer = FillLayer( s, new Vec3( 1, 0, 0 ), 0f );
		s.Rebuild();

		Check( "a layer at opacity 0 warns", layer.Diagnostic?.Severity == DiagnosticSeverity.Warning && layer.Diagnostic.Problem.Contains( "invisible" ),
			layer.Diagnostic?.Problem );
		Check( "and its remedy names the control", layer.Diagnostic?.Remedies?.Any( r => r.Contains( "Opacity" ) ) == true );
	}

	static void TestMissingSlotWarns()
	{
		var s = Box();
		var layer = FillLayer( s, new Vec3( 1, 0, 0 ) );
		layer.MaskGenerator.Index = (int)PaintMaskGenerator.MaterialId;
		layer.MaskSlot.Value = 5;
		s.Rebuild();

		Check( "a Material ID mask on a slot nothing wears warns, naming the slot",
			layer.Diagnostic?.Severity == DiagnosticSeverity.Warning && layer.Diagnostic.Cause.Contains( "slot 5" ), layer.Diagnostic?.Cause );

		layer.MaskSlot.Value = 0;
		s.MarkDirty( layer );
		s.Rebuild();
		Check( "on the slot the faces wear it builds clean, and the fill shows", layer.Diagnostic is null && s.Bodies[0].Mesh.Paint.Rgba.Any( b => b == 255 ),
			layer.Diagnostic?.Problem );
	}

	static void TestFillWithNothingWarns()
	{
		var s = Box();
		var layer = FillLayer( s, new Vec3( 1, 0, 0 ) );
		layer.FillUseColor.Value = false;
		s.Rebuild();

		Check( "a fill with every channel off warns", layer.Diagnostic?.Problem?.Contains( "fills nothing" ) == true, layer.Diagnostic?.Problem );
	}

	static void TestMaskOnlyLayerWarns()
	{
		var s = Box();
		var layer = s.Add( new PaintFeature() );
		layer.Resolution.Value = 64;
		layer.AddStroke( TopStroke( PaintChannel.Mask, 1f ) );
		s.Rebuild();

		Check( "a layer with only mask paint says there is nothing to show", layer.Diagnostic?.Problem?.Contains( "only has mask paint" ) == true,
			layer.Diagnostic?.Problem );
	}

	static void TestHasUVs()
	{
		var mesh = Primitives.Box( 1f, 1f, 1f );
		Check( "a primitive box has UVs", PaintMasks.HasUVs( mesh ) );

		foreach ( var f in mesh.Faces )
			f.UVs = null;

		Check( "a mesh with none is caught before a fill or a generator runs", !PaintMasks.HasUVs( mesh ) );
	}

	static void TestScriptWritesTextureSet()
	{
		var dir = Path.Combine( Path.GetTempPath(), $"effigy-paintmaps-{Guid.NewGuid():N}" );
		var studio = new PartStudio();

		var result = BuildScript.Run( studio,
			"add Primitive name=Torso shape=Box sizex=1 sizey=1 sizez=1\n"
			+ "add UVProject bodies=Torso mode=Unwrap\n"
			+ "add Paint name=Rust bodies=Torso resolution=64 fill=1 color=0.5,0.25,0.1 use_roughness=1 roughness=0.9 use_metallic=1 metallic=0.2 mask_generator=gradient\n"
			+ "paintmaps Torso tex name=torso_paint\n", dir );

		Check( "a script can add a fill layer with a mask and write its texture set", result.Ok, result.ToString() );

		var expected = new[] { "torso_paint.png", "torso_paint_rough.png", "torso_paint_metal.png", "torso_paint.vmat" };
		Check( "paintmaps writes colour, roughness, metallic and the vmat (no normal: no height painted)",
			expected.All( f => File.Exists( Path.Combine( dir, "tex", f ) ) ) && !File.Exists( Path.Combine( dir, "tex", "torso_paint_normal.png" ) ),
			string.Join( ",", Directory.Exists( Path.Combine( dir, "tex" ) ) ? Directory.GetFiles( Path.Combine( dir, "tex" ) ).Select( Path.GetFileName ) : Array.Empty<string>() ) );

		var vmat = File.Exists( Path.Combine( dir, "tex", "torso_paint.vmat" ) ) ? File.ReadAllText( Path.Combine( dir, "tex", "torso_paint.vmat" ) ) : "";
		Check( "the vmat names the images relative to the assets root",
			vmat.Contains( "TextureRoughness \"tex/torso_paint_rough.png\"" ) && vmat.Contains( "TextureMetalness \"tex/torso_paint_metal.png\"" ), vmat );

		var refused = BuildScript.Run( new PartStudio(), "add Primitive name=Bare shape=Box\npaintmaps Bare tex\n", dir );
		Check( "paintmaps on an unpainted body says why and how to fix it", !refused.Ok && refused.Error.Contains( "no paint" ) && refused.Error.Contains( "add Paint" ),
			refused.Error );

		try { Directory.Delete( dir, true ); } catch ( IOException ) { }
	}
}
