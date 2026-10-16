using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Effigy.Tests;

/// <summary>
/// Effigy's sculpt brushes measured against Blender's (Essentials sculpt brushes, Blender 5.2), the
/// scoreboard for "on par". Blender is the reference oracle, run through tools/blender_oracle.py: the
/// same stroke on the same mesh in both, and the results compared. Behaviour only — nothing of
/// Blender's code is used; each Effigy brush is written from what its result is measured to be.
///
///     dotnet run -c Release -- --parity [outDir] [brush filter]
///
/// Per brush: SHAPE is the correlation of the two displacement fields (1 = same shape), GAIN the
/// strength scale that best fits Effigy's result to Blender's (calibration), REST the error left
/// after that scale. Blender references are cached in outDir/refs, so each is made once.
/// </summary>
public static class Parity
{
	/// <summary>A Blender brush and the Effigy brush meant to match it (null: Effigy has none yet).</summary>
	public sealed record Case( string Blender, BrushKind? Effigy, float Strength = 0.5f, bool Invert = false, string Kind = "stroke", float EffigySign = 1f, BrushFalloff Falloff = BrushFalloff.Smooth,
		string Label = null, string Mesh = "surface", Dictionary<string, object> Auto = null, Func<PolyMesh, List<Vec3>, float[]> EffigyMask = null, MeshFilterKind? Filter = null, string BlenderFilter = null, bool Alpha = false )
	{
		public string Name => Label ?? Blender;
		public string File => Name.Replace( '/', '_' ).Replace( ' ', '_' ).Replace( '+', 'p' );
	}

	/// <summary>Every Essentials sculpt brush that shapes geometry, in the order they matter for creature work.</summary>
	public static readonly Case[] Cases =
	{
		new( "Draw", BrushKind.Draw ),
		new( "Clay Strips", BrushKind.ClayStrips ),
		new( "Clay", BrushKind.Clay ),
		new( "Crease Sharp", BrushKind.Crease, Falloff: BrushFalloff.Sharp ),
		new( "Crease Polish", BrushKind.Crease ),
		new( "Layer", BrushKind.Layer ),
		new( "Inflate/Deflate", BrushKind.Inflate ),
		new( "Blob", BrushKind.Blob ),
		new( "Draw Sharp", BrushKind.DrawSharp, EffigySign: -1f ),
		new( "Smooth", BrushKind.Smooth ),
		new( "Flatten/Contrast", BrushKind.Flatten ),
		new( "Fill/Deepen", BrushKind.Fill ),
		new( "Scrape/Fill", BrushKind.Scrape ),
		new( "Scrape Multiplane", BrushKind.MultiplaneScrape ),
		new( "Pinch/Magnify", BrushKind.Pinch ),
		new( "Grab", BrushKind.Grab, Kind: "drag" ),
		new( "Elastic Grab", BrushKind.ElasticGrab, Kind: "drag" ),
		new( "Snake Hook", BrushKind.SnakeHook, Kind: "drag" ),
		new( "Elastic Snake Hook", BrushKind.ElasticGrab, Kind: "drag" ),
		new( "Thumb", BrushKind.Thumb, Kind: "drag" ),
		new( "Nudge", BrushKind.Slide ),
		new( "Pose", BrushKind.Pose, Kind: "drag" ),
		new( "Boundary", BrushKind.Boundary, Kind: "edge" ),
		new( "Twist", BrushKind.Twist, Kind: "arc" ),
		new( "Relax Slide", BrushKind.Slide ),
		new( "Relax Pinch", BrushKind.Pinch ),
		new( "Plateau", BrushKind.Plateau ),
		new( "Pull", BrushKind.Grab, Kind: "drag" ),
		new( "Sharpen", null, Strength: 1f ),
		new( "Clay Thumb", BrushKind.ClayThumb ),
		new( "Pinch Folds Cloth", BrushKind.ClothPinchFolds ),
		new( "Grab Cloth", BrushKind.ClothGrab, Kind: "drag" ),
		new( "Inflate Cloth", BrushKind.ClothInflate ),
		new( "Push Cloth", BrushKind.ClothPush ),
		new( "Drag Cloth", BrushKind.ClothDrag, Kind: "drag" ),
		new( "Expand/Contract Cloth", BrushKind.ClothExpand ),
		new( "Bend/Twist Cloth", BrushKind.ClothTwist, Kind: "drag" ),
		// Auto-masking: a Draw stroke with one option on, Effigy's AutoMask as the brush mask.
		new( "Draw", BrushKind.Draw, Label: "Draw +topology", Mesh: "layered", Auto: new() { ["use_automasking_topology"] = true },
			EffigyMask: ( m, path ) => AutoMask.Topology( m, AutoMask.SeedAt( m, path[0], new Vec3( 0, 0, 1 ) ) ) ),
		new( "Draw", BrushKind.Draw, Label: "Draw +boundary", Kind: "nearEdge", Auto: new() { ["use_automasking_boundary_edges"] = true, ["boundary_edges_propagation_steps"] = 3 },
			EffigyMask: ( m, path ) => AutoMask.BoundaryEdges( m, 3 ) ),
		new( "Draw", BrushKind.Draw, Label: "Draw +cavity", Auto: new() { ["use_automasking_cavity"] = true },
			EffigyMask: ( m, path ) => AutoMask.Cavity( m ) ),
		new( "Draw", BrushKind.Draw, Label: "Draw +cavity inverted", Auto: new() { ["use_automasking_cavity"] = true, ["use_automasking_cavity_inverted"] = true },
			EffigyMask: ( m, path ) => AutoMask.Cavity( m, inverted: true ) ),
		// A brush alpha: one Draw dab carrying an asymmetric stripe image, in Area Plane mapping.
		new( "Draw", BrushKind.Draw, Label: "Draw +alpha", Kind: "dot", Alpha: true ),
		new( "Clay Strips", BrushKind.ClayStrips, Label: "Clay Strips +alpha", Alpha: true ),
		// Mesh filters: the whole mesh at once, strength 0.5, one iteration.
		new( "Draw", null, Label: "Filter Smooth", Kind: "filter", Filter: MeshFilterKind.Smooth, BlenderFilter: "SMOOTH" ),
		new( "Draw", null, Label: "Filter Inflate", Kind: "filter", Filter: MeshFilterKind.Inflate, BlenderFilter: "INFLATE" ),
		new( "Draw", null, Label: "Filter Scale", Kind: "filter", Filter: MeshFilterKind.Scale, BlenderFilter: "SCALE" ),
		new( "Draw", null, Label: "Filter Sphere", Kind: "filter", Filter: MeshFilterKind.Sphere, BlenderFilter: "SPHERE" ),
		new( "Draw", null, Label: "Filter Relax", Kind: "filter", Filter: MeshFilterKind.Relax, BlenderFilter: "RELAX" ),
		new( "Draw", null, Label: "Filter Surface Smooth", Kind: "filter", Filter: MeshFilterKind.SurfaceSmooth, BlenderFilter: "SURFACE_SMOOTH" ),
		new( "Draw", null, Label: "Filter Sharpen", Kind: "filter", Filter: MeshFilterKind.Sharpen, BlenderFilter: "SHARPEN" ),
		new( "Draw", null, Label: "Filter Enhance Details", Kind: "filter", Filter: MeshFilterKind.EnhanceDetails, BlenderFilter: "ENHANCE_DETAILS" ),
	};

	const float Radius = 1.5f;

	/// <summary>The test surface: a 10 in sheet at 0.1 in spacing, gently bumpy so smoothing and flattening brushes have work to do.</summary>
	/// <summary>The test meshes: "surface", or "layered" (the surface with a separate 4 in sheet
	/// floating 0.6 in over its middle, for topology masking).</summary>
	public static PolyMesh Surface( string kind )
	{
		var m = Surface();
		if ( kind != "layered" ) return m;
		var top = Primitives.Plane( 4, 4, 40, 40 );
		var offset = m.VertexCount;
		var positions = m.Positions.Concat( top.Positions.Select( p => p + new Vec3( 0, 0, 0.6f ) ) ).ToList();
		var faces = m.Faces.Select( f => new Face( (int[])f.Indices.Clone(), null, 0 ) ).Concat( top.Faces.Select( f => new Face( f.Indices.Select( i => i + offset ).ToArray(), null, 0 ) ) ).ToList();
		return new PolyMesh( positions, faces );
	}

	public static PolyMesh Surface()
	{
		var m = Primitives.Plane( 10, 10, 100, 100 );
		for ( var i = 0; i < m.VertexCount; i++ )
		{
			var p = m.Positions[i];
			m.Positions[i] = new Vec3( p.x, p.y, Height( p.x, p.y ) );
		}
		return m;
	}

	static float Height( float x, float y ) => 0.12f * MathF.Sin( x * 2.1f ) * MathF.Cos( y * 1.7f ) + 0.05f * MathF.Sin( x * 5.3f + y * 4.1f );

	/// <summary>The stroke: across the sheet's middle along x, on the surface.</summary>
	public static List<Vec3> StrokePath( Case c )
	{
		var path = BasePath( c );
		// On the layered mesh the stroke runs over the floating sheet.
		return c.Mesh == "layered" ? path.Where( p => MathF.Abs( p.x ) <= 1.5f ).Select( p => new Vec3( p.x, p.y, 0.6f ) ).ToList() : path;
	}

	static List<Vec3> BasePath( Case c ) => c.Kind switch
	{
		"drag" => new List<Vec3> { new( -1, 0, Height( -1, 0 ) ), new( 1.5f, 0, Height( 1.5f, 0 ) + 1.2f ) },
		// From the sheet's open edge inward.
		// Along the sheet, half an inch in from its open edge at y = -5.
		"dot" => new List<Vec3> { new( 0, 0, Height( 0, 0 ) ) },
		"nearEdge" => Enumerable.Range( 0, 25 ).Select( i => { var x = -3f + 0.25f * i; return new Vec3( x, -4.6f, Height( x, -4.6f ) ); } ).ToList(),
		"edge" => new List<Vec3> { new( -4.95f, 0, Height( -4.95f, 0 ) ), new( -2.5f, 0, Height( -2.5f, 0 ) ) },
		// A quarter turn around the start, 1 in out: a twist rotates by how far the drag swings round.
		"arc" => new[] { new Vec3( -1, 0, 0 ) }.Concat( Enumerable.Range( 0, 10 ).Select( i => { var a = i / 9f * MathF.PI * 0.5f; var x = -1f + MathF.Cos( a ); var y = MathF.Sin( a ); return new Vec3( x, y, Height( x, y ) ); } ) ).Select( p => new Vec3( p.x, p.y, Height( p.x, p.y ) ) ).ToList(),
		_ => Enumerable.Range( 0, 25 ).Select( i => { var x = -3f + 0.25f * i; return new Vec3( x, 0, Height( x, 0 ) ); } ).ToList(),
	};

	public static int Run( string outDir, string filter )
	{
		Directory.CreateDirectory( Path.Combine( outDir, "refs" ) );
		var blender = Which( "blender" );
		var oracle = Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "../../../../tools/blender_oracle.py" ) );
		var meshes = new Dictionary<string, (PolyMesh Mesh, string Path)>();
		(PolyMesh Mesh, string Path) MeshFor( Case c )
		{
			if ( meshes.TryGetValue( c.Mesh, out var have ) ) return have;
			var m = Surface( c.Mesh );
			var path = Path.Combine( outDir, c.Mesh + ".obj" );
			ObjWriter.WriteFile( m, path, c.Mesh );
			return meshes[c.Mesh] = (m, path);
		}
		var rows = new List<string>();
		Console.WriteLine( $"{"brush",-24} {"effigy",-16} {"shape",6} {"gain",8} {"best",6}  {"@str",-7} {"scale",7}  blender peak / moved" );
		var picked = Cases.Where( c => string.IsNullOrEmpty( filter ) || c.Name.Contains( filter, StringComparison.OrdinalIgnoreCase ) ).ToArray();
		string RefPath( Case c ) => Path.Combine( outDir, "refs", c.File + ".obj" );
		// Every missing reference in ONE Blender launch.
		var missing = picked.Where( c => !File.Exists( RefPath( c ) ) ).ToArray();
		if ( missing.Length > 0 && blender is not null )
			MakeReferences( blender, oracle, missing.Select( c => (c, RefPath( c ), MeshFor( c ).Path) ).ToArray(), Path.Combine( outDir, "refs", "batch.json" ) );
		foreach ( var c in picked )
		{
			var refPath = RefPath( c );
			var surface = MeshFor( c ).Mesh;
			var reference = ReadPositions( refPath );
			if ( reference is null || reference.Length != surface.VertexCount ) { Console.WriteLine( $"{c.Name,-24} reference failed (see {refPath}.log)" ); continue; }
			var dB = reference.Select( ( p, i ) => p - surface.Positions[i] ).ToArray();
			var peakB = dB.Max( d => d.Length );
			var movedB = dB.Count( d => d.Length > 1e-4f );
			string line;
			// SHAPE and GAIN are measured with Effigy at Blender's own strength: GAIN ≈ 1 means the same
			// number does the same amount (calibrated). BEST is the best shape over a strength ladder, in
			// case the shape changes with strength; SCALE the strength multiplier that would bring GAIN to 1.
			Func<float, Vec3[]> run;
			string what;
			if ( c.Filter is { } fk )
			{
				what = fk.ToString();
				run = st => { var mesh = surface.Clone(); MeshFilter.Apply( mesh, fk, st ); return mesh.Positions.ToArray(); };
			}
			else if ( c.Effigy is { } kind )
			{
				what = kind.ToString();
				run = st => { var mesh = surface.Clone(); Brush.Apply( mesh, EffigyStroke( c, kind, st * c.EffigySign ), mask: c.EffigyMask?.Invoke( mesh, StrokePath( c ) ) ); return mesh.Positions.ToArray(); };
			}
			else
			{
				line = $"{c.Name,-24} {"MISSING",-16} {"",6} {"",8} {"",6}  {"",-7} {"",7}  {peakB:0.000} / {movedB}";
				Console.WriteLine( line );
				rows.Add( line );
				continue;
			}
			Vec3[] Delta( Vec3[] p ) => p.Select( ( q, i ) => q - surface.Positions[i] ).ToArray();
			var atB = run( c.Strength );
			var same = Compare( dB, Delta( atB ) );
			File.WriteAllLines( Path.Combine( outDir, "refs", c.File + ".effigy.txt" ), atB.Select( p => FormattableString.Invariant( $"v {p.x} {p.y} {p.z}" ) ) );
			var best = (Shape: -2f, Strength: 0f);
			foreach ( var st in new[] { 0.005f, 0.01f, 0.02f, 0.05f, 0.1f, 0.2f, 0.5f, 1f, 2f } )
			{
				var r = Compare( dB, Delta( run( st ) ) );
				if ( r.Shape > best.Shape + 1e-3f ) best = (r.Shape, st);
			}
			// The multiplier on strength that brings GAIN to 1: bisection in log space (GAIN falls as strength rises).
			var scale = float.NaN;
			if ( same.Gain > 0f )
			{
				double lo = Math.Log( 1e-3 ), hi = Math.Log( 1e3 );
				for ( var it = 0; it < 22; it++ )
				{
					var mid = 0.5 * (lo + hi);
					var g = Compare( dB, Delta( run( c.Strength * (float)Math.Exp( mid ) ) ) ).Gain;
					if ( g > 1f ) lo = mid; else hi = mid;
				}
				scale = (float)Math.Exp( 0.5 * (lo + hi) );
				if ( scale > 999f || scale < 1.01e-3f ) scale = float.NaN;
			}
			line = $"{c.Name,-24} {what,-16} {same.Shape,6:0.00} {same.Gain,8:0.000} {best.Shape,6:0.00}  @{best.Strength,-6} {scale,7:0.000}  {peakB:0.000} / {movedB}";
			Console.WriteLine( line );
			rows.Add( line );
		}
		File.WriteAllLines( Path.Combine( outDir, "parity.txt" ), rows );
		return 0;
	}

	/// <summary>Effigy's version of the same stroke: dabs at Blender's default spacing (10% of the diameter).</summary>
	public static BrushStroke EffigyStroke( Case c, BrushKind kind, float strength = 1f )
	{
		var stroke = new BrushStroke { Kind = kind, Falloff = c.Falloff, Alpha = c.Alpha ? TestAlpha : null };
		var path = StrokePath( c );
		if ( c.Kind == "dot" )
		{
			stroke.Samples.Add( new BrushSample( path[0], new Vec3( 0, 0, 1 ), Radius, strength ) );
			return stroke;
		}
		if ( c.Kind == "arc" )
		{
			stroke.Samples.Add( new BrushSample( path[0], new Vec3( 0, 0, 1 ), Radius, strength ) );
			return stroke;
		}
		if ( c.Kind is "drag" or "edge" )
		{
			var drag = path[^1] - path[0];
			stroke.Samples.Add( new BrushSample( path[0], new Vec3( 0, 0, 1 ), Radius, strength, new Vec3( drag.x, drag.y, 0 ) ) );
			return stroke;
		}
		var spacing = 0.1f * 2f * Radius;
		var total = 0f;
		for ( var i = 1; i < path.Count; i++ ) total += (path[i] - path[i - 1]).Length;
		for ( var d = 0f; d <= total + 1e-4f; d += spacing )
		{
			var at = d; var k = 1;
			while ( k < path.Count - 1 && at > (path[k] - path[k - 1]).Length ) { at -= (path[k] - path[k - 1]).Length; k++; }
			var p = Vec3.Lerp( path[k - 1], path[k], Math.Clamp( at / MathF.Max( (path[k] - path[k - 1]).Length, 1e-6f ), 0f, 1f ) );
			stroke.Samples.Add( new BrushSample( p, new Vec3( 0, 0, 1 ), Radius, strength * (c.Invert ? -1f : 1f) ) );
		}
		return stroke;
	}

	/// <summary>The test alpha: four stripes across (bright, dim), brightening from top to bottom, so a
	/// flipped or turned mapping can't score well. Written as a PNG for Blender and used as is by Effigy.</summary>
	public static readonly BrushAlpha TestAlpha = MakeAlpha();

	static BrushAlpha MakeAlpha()
	{
		const int size = 128;
		var values = new float[size * size];
		for ( var y = 0; y < size; y++ )
			for ( var x = 0; x < size; x++ )
			{
				var u = x / (size - 1f); var v = y / (size - 1f);
				values[y * size + x] = ((u * 4f) % 1f < 0.5f ? 1f : 0.2f) * (0.3f + 0.7f * v);
			}
		return new BrushAlpha { Values = values, Width = size, Height = size };
	}

	static string AlphaPng( string dir )
	{
		var path = Path.Combine( dir, "alpha.png" );
		var rgb = new byte[TestAlpha.Width * TestAlpha.Height * 3];
		for ( var i = 0; i < TestAlpha.Values.Length; i++ ) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = (byte)Math.Round( TestAlpha.Values[i] * 255f );
		PngWriter.WriteFile( path, rgb, TestAlpha.Width, TestAlpha.Height );
		return path;
	}

	/// <summary>Shape correlation, best-fit gain and the relative error left after it.</summary>
	public static (float Shape, float Gain, float Residual) Compare( Vec3[] reference, Vec3[] result )
	{
		double rr = 0, ee = 0, re = 0;
		for ( var i = 0; i < reference.Length; i++ )
		{
			rr += Vec3.Dot( reference[i], reference[i] );
			ee += Vec3.Dot( result[i], result[i] );
			re += Vec3.Dot( reference[i], result[i] );
		}
		if ( rr < 1e-12 || ee < 1e-12 ) return (0f, 0f, 1f);
		var shape = (float)(re / Math.Sqrt( rr * ee ));
		var gain = (float)(re / ee);
		double left = 0;
		for ( var i = 0; i < reference.Length; i++ ) { var d = reference[i] - result[i] * gain; left += Vec3.Dot( d, d ); }
		return (shape, gain, (float)Math.Sqrt( left / rr ));
	}

	static void MakeReferences( string blender, string oracle, (Case Case, string Out, string Mesh)[] jobs, string batchPath )
	{
		var list = jobs.Select( j =>
		{
			var c = j.Case;
			var path = StrokePath( c );
			var strokes = c.Kind == "filter" ? Array.Empty<Vec3[]>() : c.Kind is "drag" or "edge" ? new[] { new[] { path[0], path[0], path[^1] } } : new[] { path.ToArray() };
			if ( File.Exists( j.Out + ".log" ) ) File.Delete( j.Out + ".log" );
			return new Dictionary<string, object>
			{
				["mesh"] = j.Mesh, ["out"] = j.Out, ["brush"] = c.Blender, ["radius"] = Radius, ["automask"] = c.Auto ?? new Dictionary<string, object>(),
				["texture"] = c.Alpha ? new Dictionary<string, object> { ["image"] = AlphaPng( Path.GetDirectoryName( j.Out ) ), ["map_mode"] = "AREA_PLANE" } : null,
				["filter"] = c.BlenderFilter is null ? null : new Dictionary<string, object> { ["type"] = c.BlenderFilter, ["strength"] = 0.5f, ["iterations"] = 1 },
				["strength"] = c.Strength, ["invert"] = c.Invert,
				["strokes"] = strokes.Select( st => st.Select( p => new[] { p.x, p.y, p.z } ).ToArray() ).ToArray(),
			};
		} ).ToArray();
		File.WriteAllText( batchPath, JsonSerializer.Serialize( new Dictionary<string, object> { ["jobs"] = list } ) );
		var psi = new ProcessStartInfo( blender, $"--factory-startup --window-geometry 0 0 640 480 --python \"{oracle}\" -- \"{batchPath}\"" ) { RedirectStandardOutput = true, RedirectStandardError = true };
		using var proc = Process.Start( psi );
		proc.StandardOutput.ReadToEndAsync();
		proc.StandardError.ReadToEndAsync();
		if ( !proc.WaitForExit( 60_000 + 15_000 * jobs.Length ) ) { try { proc.Kill( true ); } catch { } }
	}

	static Vec3[] ReadPositions( string path )
	{
		if ( !File.Exists( path ) ) return null;
		return File.ReadLines( path ).Where( l => l.StartsWith( "v " ) ).Select( l =>
		{
			var s = l.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
			return new Vec3( float.Parse( s[1], CultureInfo.InvariantCulture ), float.Parse( s[2], CultureInfo.InvariantCulture ), float.Parse( s[3], CultureInfo.InvariantCulture ) );
		} ).ToArray();
	}

	static string Which( string exe ) =>
		(Environment.GetEnvironmentVariable( "PATH" ) ?? "").Split( ':' ).Select( d => Path.Combine( d, exe ) ).FirstOrDefault( File.Exists );
}
