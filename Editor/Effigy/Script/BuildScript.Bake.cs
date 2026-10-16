using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

public static partial class BuildScript
{
	const string BakeUsage = "bake Low High[,High2…] folder | bake * folder — [size=1024] "
		+ "[maps=normal,ao,curvature,cavity,height,thickness,bentnormal,objectnormal,position,id|all|everything] "
		+ "[cage=0|distance|CageBody] [distance=0] [ss=1] [bits=8|16] [id=material|body] [green=gl|dx] [flipv=0] "
		+ "[vmat=0|1] [shader=complex|skin] [preset=Body|Head|Mouth|Wet flesh] [k=1] [color=] [rough=] [metal=] [name=] [assets=]";

	/// <summary>
	/// `bake`: high detail onto a low body's UVs, as PNG maps and optionally a .vmat.
	///
	///   bake Low High folder        one low body from one or more high bodies (High,High2 - combined).
	///   bake * folder               by name: every X_low from its X_high bodies (X_high_2 … too) and
	///                               X_cage if there is one. Each pair only sees its own highs - the
	///                               exploded bake - and all of them land in one set of maps, so lows
	///                               sharing an atlas come out as one texture.
	///   cage=                       a number pushes the rays' start out that far; a body name uses
	///                               that body as the cage mesh (a pushed-out copy of the low body).
	///   bits=16                     height, normal, bent normal, object normal and position as 16-bit PNGs.
	///   id=body                     the ID map colours by high body instead of material slot.
	///   shader=skin preset= k=      the vmat is s&amp;box's skin shader with a preset; k is translucency
	///                               (1 real skin, 3-10 gel-like flesh) for the transmission mask, which
	///                               is written when thickness is baked. A baked bent normal is bound.
	/// </summary>
	static bool Bake( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count < 2 || (args[0] != "*" && args.Count < 3) )
			return Fail( r, "bake needs the low body, the high body and a folder, or * and a folder to pair bodies by name: " + BakeUsage );

		var byName = args[0] == "*";
		var opts = Pairs( args.Skip( byName ? 2 : 3 ) );
		var dir = Resolve( baseDir, args[byName ? 1 : 2] );
		var size = (int)Number( opts.GetValueOrDefault( "size", "1024" ) );

		if ( size < 1 || size > 8192 )
			return Fail( r, $"size={size}: a map is 1 to 8192 texels a side." );

		if ( !BakeMapList( opts.GetValueOrDefault( "maps", "normal,ao" ), out var maps, out var whyMaps ) )
			return Fail( r, whyMaps );

		var bits = (int)Number( opts.GetValueOrDefault( "bits", "8" ) );

		if ( bits != 8 && bits != 16 )
			return Fail( r, $"bits={bits}: a bake is written at 8 or 16 bits." );

		var idBy = opts.GetValueOrDefault( "id", "material" ).Trim().ToLowerInvariant();

		if ( idBy is not ("material" or "body") )
			return Fail( r, $"id={idBy}: the ID map colours by material (each material slot) or body (each high body)." );

		var shader = opts.GetValueOrDefault( "shader", "complex" ).Trim().ToLowerInvariant();

		if ( shader is not ("complex" or "skin") )
			return Fail( r, $"shader={shader}: the bake's vmat is complex or skin." );

		List<BakePairs.Pair> pairs;
		string name;
		var cageDistance = 0f;

		if ( byName )
		{
			pairs = BakePairs.Match( studio.Bodies.Select( b => (b.Name ?? b.Id, b.Mesh) ), out var problems );

			foreach ( var p in problems )
				r.Log.Add( $"bake: warning: {p}" );

			if ( pairs.Count == 0 )
				return Fail( r, "bake *: no low body has a high body to bake from. Name them as pairs - Arm_low and Arm_high (Arm_high_2, Arm_cage …) - "
					+ $"or name the bodies: bake Low High {args[1]}. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}."
					+ (problems.Count > 0 ? " " + string.Join( " ", problems ) : "") );

			name = Safe( opts.GetValueOrDefault( "name", pairs.Count == 1 ? pairs[0].Name : "bake" ) );

			if ( opts.TryGetValue( "cage", out var cageText ) )
			{
				if ( !TryNumber( cageText, out cageDistance ) )
					return Fail( r, $"cage={cageText}: in a by-name bake each pair's cage is its X_cage body, so cage= takes only a distance." );
			}
		}
		else
		{
			var low = ModelMeasure.FindBody( studio, args[0] );

			if ( low?.Mesh is null )
				return Fail( r, $"No body called '{args[0]}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}." );

			var pair = new BakePairs.Pair { Name = low.Name ?? low.Id, LowName = args[0], Low = low.Mesh };

			foreach ( var highName in args[1].Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
			{
				var high = ModelMeasure.FindBody( studio, highName.Trim() );

				if ( high?.Mesh is null )
					return Fail( r, $"No body called '{highName.Trim()}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}." );

				pair.Highs.Add( (highName.Trim(), high.Mesh) );
			}

			if ( opts.TryGetValue( "cage", out var cageText ) && !TryNumber( cageText, out cageDistance ) )
			{
				var cage = ModelMeasure.FindBody( studio, cageText );

				if ( cage?.Mesh is null )
					return Fail( r, $"cage={cageText}: not a distance and no body has that name." );

				if ( NormalBake.CageMismatch( low.Mesh, cage.Mesh ) is { } why )
					return Fail( r, $"cage={cageText}: the cage {why} Make it from a copy of {args[0]} pushed outwards." );

				pair.Cage = cage.Mesh;
				pair.CageName = cageText;
			}

			pairs = new List<BakePairs.Pair> { pair };
			name = Safe( opts.GetValueOrDefault( "name", low.Name ?? low.Id ) );
		}

		// Refused up front, with the reason: an overlapping bake does not fail, it writes a map that
		// is wrong wherever two faces share texels. A by-name bake is checked as ONE atlas.
		var coverage = NormalBake.Measure( pairs.Select( p => p.Low ) );

		if ( !coverage.CanBake )
		{
			var lows = string.Join( ", ", pairs.Select( p => p.LowName ) );
			return Fail( r, $"{lows} cannot carry a bake: {coverage.Problem} "
				+ (pairs.Count > 1 ? "Low bodies baked by name share one texture, so their UVs must not overlap each other either. " : "")
				+ $"Unwrap it first: add UVProject bodies={pairs[0].LowName} mode=Unwrap" );
		}

		var options = new BakeOptions
		{
			Cage = cageDistance,
			MaxDistance = opts.TryGetValue( "distance", out var distance ) ? Number( distance ) : 0f,
			Supersample = (int)Number( opts.GetValueOrDefault( "ss", "1" ) ),
			FlipGreen = opts.GetValueOrDefault( "green", "gl" ).Equals( "dx", StringComparison.OrdinalIgnoreCase ),
			AoRays = (int)Number( opts.GetValueOrDefault( "rays", "64" ) ),
			AoDistance = opts.TryGetValue( "aodistance", out var aoDistance ) ? Number( aoDistance ) : 0f,
			Padding = (int)Number( opts.GetValueOrDefault( "padding", "4" ) ),
			SixteenBit = bits == 16,
		};

		Directory.CreateDirectory( dir );
		var flipV = Truthy( opts.GetValueOrDefault( "flipv", "0" ) );
		var set = NormalBake.BakeGroups( BakePairs.ToGroups( pairs, idBy == "body" ), size, size, maps, options );
		var written = new Dictionary<string, string>();

		foreach ( var (mapName, map) in set.Maps )
		{
			var path = Path.Combine( dir, $"{name}_{mapName}.png" );
			PngWriter.WriteFile( path, map, flipV );
			written[mapName] = path;
			r.Written.Add( path );
			r.Log.Add( $"baked {mapName} {map.Width}x{map.Height}{(map.Rgb16 is not null ? " 16-bit" : "")} to {path}" );
		}

		foreach ( var p in pairs )
			r.Log.Add( $"bake: {p.LowName} from {string.Join( ", ", p.Highs.Select( h => h.Name ) )}" + (p.Cage is not null ? $", cage {p.CageName}" : "") );

		foreach ( var line in set.Report.Split( '\n' ) )
			r.Log.Add( $"bake: {line}" );

		r.Log.Add( $"bake: {(options.FlipGreen ? "DirectX" : "OpenGL")} green, v = 0 at the {(flipV ? "bottom" : "top")}, "
			+ (pairs.Any( p => p.Cage is not null ) ? "cage mesh" : options.Cage > 0f ? $"cage {N( options.Cage )}" : "no cage")
			+ $", {options.Supersample}x{options.Supersample} samples per texel" + (pairs.Count > 1 ? $", {pairs.Count} pairs baked apart" : "") );

		if ( !Truthy( opts.GetValueOrDefault( "vmat", "0" ) ) )
			return true;

		// Texture paths in a .vmat are relative to the assets root, which is the base folder unless
		// assets= says otherwise — the same rule export uses.
		var root = opts.TryGetValue( "assets", out var assets ) ? assets : baseDir;
		string Asset( string path ) => path is null ? null : Path.GetRelativePath( root, path ).Replace( '\\', '/' );
		string Map( string map ) => written.TryGetValue( map, out var path ) ? Asset( path ) : null;
		var vmatPath = Path.Combine( dir, name + ".vmat" );

		if ( shader == "skin" )
		{
			var preset = SkinMaterial.Preset( opts.GetValueOrDefault( "preset", "Body" ) );
			var k = opts.TryGetValue( "k", out var kText ) ? Number( kText ) : 1f;

			if ( k <= 0f )
				return Fail( r, $"k={kText}: translucency is above 0 (1 is real skin, 3-10 gel-like flesh)." );

			// Transmission only where the preset transmits and thickness was baked: the mask is built
			// from the unquantised thickness, never from the 8-bit grey map.
			string transmission = null;

			if ( set.ThicknessUnits is not null && preset.TransmissionColor.x + preset.TransmissionColor.y + preset.TransmissionColor.z > 0f )
			{
				var path = Path.Combine( dir, $"{name}_transmission.png" );
				PngWriter.WriteFile( path, SkinMaterial.TransmissionMask( set, k, padding: options.Padding ), flipV );
				r.Written.Add( path );
				transmission = Asset( path );
			}

			File.WriteAllText( vmatPath, SkinMaterial.VmatSource( preset, opts.GetValueOrDefault( "color" ), Map( "normal" ),
				opts.GetValueOrDefault( "rough" ), Map( "ao" ), transmission, Map( "bentnormal" ) ) );
			r.Written.Add( vmatPath );
			r.Log.Add( $"wrote {vmatPath} (skin {preset.Name}: normal {Map( "normal" ) ?? "default"}, ao {Map( "ao" ) ?? "default"}, "
				+ $"bent normal {Map( "bentnormal" ) ?? "off"}, transmission {transmission ?? "none"}, k {N( k )})" );
		}
		else
		{
			File.WriteAllText( vmatPath, BakeMaterial.VmatSource(
				color: opts.GetValueOrDefault( "color" ),
				normal: Map( "normal" ),
				roughness: opts.GetValueOrDefault( "rough" ),
				ambientOcclusion: Map( "ao" ),
				metalness: opts.TryGetValue( "metal", out var metal ) ? Number( metal ) : 0f ) );

			r.Written.Add( vmatPath );
			r.Log.Add( $"wrote {vmatPath} (complex: normal {Map( "normal" ) ?? "default"}, "
				+ $"ao {Map( "ao" ) ?? "default"}, colour {opts.GetValueOrDefault( "color", "default" )}, rough {opts.GetValueOrDefault( "rough", "default" )})" );
		}

		if ( Asset( dir ).StartsWith( "..", StringComparison.Ordinal ) )
			r.Log.Add( $"bake: note: {dir} is outside the assets root {root}, so the vmat's texture paths will not resolve; pass assets=<root>" );

		return true;
	}
}
