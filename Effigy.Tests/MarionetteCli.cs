using System;
using System.IO;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>Marionette commands of the effigy CLI that are not the sim itself.</summary>
public static class MarionetteCli
{
	/// <summary>
	/// effigy key clip.riganim bone goal|fk "frame:x,y,z[:p,y,r][:ease][:w=];.." [rig=x.ctrlrig (default: the clip's own, found upward)]
	/// [model=dump.json|tentacle|citizen] [chain=N] [solver=ccd|fabrik|spline] [ease=Smooth] [write=out.riganim]
	/// - ClipKeys on a clip file; writes over the clip unless write= says elsewhere.
	/// </summary>
	public static int Key( string[] a )
	{
		if ( a.Length > 2 && a[2].StartsWith( "morph=" ) )
			return KeyMorph( a );

		if ( a.Length < 5 )
		{
			Console.Error.WriteLine( "effigy key clip.riganim bone goal|fk \"frame:x,y,z[:p,y,r][:ease][:w=];..\" [rig=] [model=dump|tentacle|citizen] [chain=N] [solver=ccd|fabrik|spline] [ease=Smooth] [write=out.riganim]" );
			Console.Error.WriteLine( "effigy key clip.riganim morph=Name \"frame:weight[:ease];..\" [shapes=a,b] [ease=Smooth] [write=out.riganim]" );
			return 1;
		}

		string Opt( string key ) => a.Skip( 5 ).FirstOrDefault( x => x.StartsWith( key + "=" ) )?[(key.Length + 1)..];

		var json = File.ReadAllText( a[1] );
		var clip = RigAnimFile.Read( json );

		// The rig: rig=, else the one the clip names, looked up from the clip's folder upward.
		var rigPath = Opt( "rig" ) ?? FindRig( a[1], clip.RigAsset );

		if ( rigPath is not null )
			clip.UseRig( File.ReadAllText( rigPath ) );

		if ( !Enum.TryParse<RigEase>( Opt( "ease" ) ?? "Smooth", true, out var ease ) ) { Console.Error.WriteLine( "ease: Smooth, Linear, Stepped, EaseIn, EaseOut, Cubic, Monotone" ); return 1; }
		if ( ClipKeys.Parse( a[4], ease, out var error ) is not { } targets ) { Console.Error.WriteLine( $"targets: {error}" ); return 1; }

		ClipKeys.Result result;

		if ( a[3] == "goal" )
		{
			// A goal for a bone the model lacks, or that no live constraint ends at, keys nothing that
			// plays - say so rather than write keys that silently do nothing.
			if ( Opt( "model" ) is { } model )
			{
				var skeleton = (File.Exists( model ) ? ModelDump.ReadFile( model ) : model == "citizen" ? SimRig.Citizen() : SimRig.Tentacle()).Skeleton;

				if ( skeleton.IndexOf( a[2] ) < 0 ) { Console.Error.WriteLine( $"the model has no bone '{a[2]}'" ); return 1; }
			}

			result = ClipKeys.Goals( clip, a[2], targets );

			if ( clip.Ik is null )
				result.Notes.Add( $"no rig= and the clip's rig '{clip.RigAsset}' was not found - cannot check that a live IK constraint ends at {a[2]}; without one these goal keys do nothing" );
		}
		else if ( a[3] == "fk" )
		{
			var modelArg = Opt( "model" ) ?? "tentacle";
			var dump = File.Exists( modelArg ) ? ModelDump.ReadFile( modelArg )
				: modelArg == "citizen" ? SimRig.Citizen() : SimRig.Tentacle();
			var ik = clip.Ik?.FirstOrDefault( c => c.EndBone == a[2] )?.Weighted( 1f ) ?? new RigIk { Name = "key", EndBone = a[2], ChainLength = 4, Iterations = 20 };

			if ( Opt( "chain" ) is { } chain ) ik.ChainLength = int.Parse( chain );
			if ( Opt( "solver" ) is { } solver ) { ik.Fabrik = solver == "fabrik"; ik.Spline = solver == "spline"; }

			result = ClipKeys.Solve( clip, dump.Skeleton, ik, targets );
		}
		else { Console.Error.WriteLine( "mode is goal or fk" ); return 1; }

		if ( !result.Ok ) { Console.Error.WriteLine( result.Error ); return 1; }

		foreach ( var note in result.Notes.Where( n => n.Contains( "do nothing" ) ) )
			Console.Error.WriteLine( $"warning: {note}" );

		var path = Opt( "write" ) ?? a[1];
		File.WriteAllText( path, RigAnimWriter.Write( clip, json ) );
		Console.WriteLine( $"{result} -> {path}" );
		return 0;
	}

	/// <summary>
	/// effigy key clip.riganim morph=Name "frame:weight[:ease];.." [shapes=a,b (the model's shape keys,
	/// to check the name against)] [ease=Smooth] [write=out.riganim] - ClipKeys.Morph: a keyed shape
	/// key curve (a Morph track), the one Marionette's Morph Curves draws and the export writes as a
	/// flex channel.
	/// </summary>
	public static int KeyMorph( string[] a )
	{
		if ( a.Length < 4 )
		{
			Console.Error.WriteLine( "effigy key clip.riganim morph=Name \"frame:weight[:ease];..\" [shapes=a,b] [ease=Smooth] [write=out.riganim]" );
			return 1;
		}

		string Opt( string key ) => a.Skip( 4 ).FirstOrDefault( x => x.StartsWith( key + "=" ) )?[(key.Length + 1)..];

		foreach ( var extra in a.Skip( 4 ).Where( x => !x.StartsWith( "shapes=" ) && !x.StartsWith( "ease=" ) && !x.StartsWith( "write=" ) ) )
		{
			Console.Error.WriteLine( $"key morph has no option '{extra}': shapes=, ease= and write= only" );
			return 1;
		}

		if ( !File.Exists( a[1] ) ) { Console.Error.WriteLine( $"no clip at {a[1]}" ); return 1; }
		if ( !Enum.TryParse<RigEase>( Opt( "ease" ) ?? "Smooth", true, out var ease ) ) { Console.Error.WriteLine( "ease: Smooth, Linear, Stepped, EaseIn, EaseOut, Cubic, Monotone" ); return 1; }
		if ( ClipKeys.ParseMorph( a[3], ease, out var error ) is not { } keys ) { Console.Error.WriteLine( $"keys: {error}" ); return 1; }

		var json = File.ReadAllText( a[1] );
		var clip = RigAnimFile.Read( json );
		var shapes = Opt( "shapes" )?.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		var result = ClipKeys.Morph( clip, a[2]["morph=".Length..], keys, shapes );

		if ( !result.Ok ) { Console.Error.WriteLine( result.Error ); return 1; }

		foreach ( var note in result.Notes )
			Console.Error.WriteLine( $"{(note.Contains( "do nothing" ) ? "warning" : "note")}: {note}" );

		if ( shapes is null )
			Console.Error.WriteLine( "note: not checked against the model's shape keys (pass shapes=a,b to check); the name must match one exactly to animate it" );

		var path = Opt( "write" ) ?? a[1];
		File.WriteAllText( path, RigAnimWriter.Write( clip, json ) );
		Console.WriteLine( $"{result} -> {path}" );
		return 0;
	}

	/// <summary>The .ctrlrig a clip names (an asset path like "animations/x.ctrlrig"), found under the
	/// clip's folder or any folder above it, directly or in an Assets folder. Null when not found.</summary>
	public static string FindRig( string clipPath, string rigAsset )
	{
		if ( string.IsNullOrWhiteSpace( rigAsset ) )
			return null;

		for ( var dir = Path.GetDirectoryName( Path.GetFullPath( clipPath ) ); dir is not null; dir = Path.GetDirectoryName( dir ) )
		{
			foreach ( var candidate in new[] { Path.Combine( dir, rigAsset ), Path.Combine( dir, "Assets", rigAsset ) } )
			{
				if ( File.Exists( candidate ) )
					return candidate;
			}
		}

		return null;
	}
}
