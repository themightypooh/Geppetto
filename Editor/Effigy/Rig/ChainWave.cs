using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A wave travelling down a chain, laid over a clip: each bone swings by a sine whose phase lags
/// its depth below the first, so a bend rolls from root to tip - a tentacle writhing, a tail
/// sweeping, a cable's idle sway. Like <see cref="ChainSpring"/> it never asks which axis a bone
/// points along: the swing axis is worked out in model space from where each bone's child sits, and
/// applied as a turn about the bone's head on top of whatever the clip already does.
/// </summary>
public static class ChainWave
{
	public enum Plane
	{
		/// <summary>Swing side to side: about the model's up, as seen along the bone.</summary>
		Side,

		/// <summary>Swing up and down: about the horizontal across the bone.</summary>
		Up
	}

	public sealed class Settings
	{
		/// <summary>Peak swing per bone, degrees.</summary>
		public float Amplitude = 12f;

		/// <summary>Frames for one full swing.</summary>
		public float Period = 40f;

		/// <summary>Bones per wavelength: how far down the chain one swing has travelled when the root
		/// starts the next. Large is a stiff whole-chain sway, small a tight wriggle.</summary>
		public float Wavelength = 8f;

		/// <summary>0 swings every bone the same; 1 keeps the root still and gives the tip it all.</summary>
		public float Taper = 0.6f;

		public Plane Swing = Plane.Side;

		/// <summary>Frames to shift the wave by - give each of several chains its own so they never
		/// move as twins.</summary>
		public float Offset;
	}

	/// <summary>
	/// The clip's frames (<paramref name="local"/>, parent space) with the wave added to every bone in
	/// <paramref name="bones"/>, which must run root to tip (skeleton order). Other bones keep their
	/// locals and follow a waved parent.
	/// </summary>
	public static List<Xform[]> Apply( Skeleton skeleton, IReadOnlyList<Xform[]> local, IReadOnlyList<int> bones, Settings settings )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( local is null ) throw new ArgumentNullException( nameof( local ) );

		settings ??= new Settings();

		var depth = new Dictionary<int, int>();

		for ( var k = 0; k < bones.Count; k++ )
			depth[bones[k]] = k;

		var firstChild = new int[skeleton.Count];
		Array.Fill( firstChild, -1 );

		for ( var i = skeleton.Count - 1; i >= 0; i-- )
		{
			if ( skeleton.Bones[i].Parent >= 0 )
				firstChild[skeleton.Bones[i].Parent] = i;
		}

		var result = new List<Xform[]>( local.Count );
		var last = Math.Max( bones.Count - 1, 1 );
		var up = new Vec3( 0, 0, 1 );

		for ( var f = 0; f < local.Count; f++ )
		{
			var pose = local[f];
			var world = new Xform[skeleton.Count];

			for ( var i = 0; i < skeleton.Count; i++ )
			{
				var parent = skeleton.Bones[i].Parent;
				var rest = parent >= 0 ? world[parent] * pose[i] : pose[i];
				world[i] = rest;

				if ( !depth.TryGetValue( i, out var d ) )
					continue;

				var offset = firstChild[i] >= 0 ? pose[firstChild[i]].Origin : pose[i].Inverse.TransformDirection( pose[i].Origin );

				if ( offset.LengthSquared < 1e-10f )
					continue;

				var along = (rest.TransformPoint( offset ) - rest.Origin).Normal;
				var axis = settings.Swing == Plane.Side
					? (up - along * Vec3.Dot( up, along )).Normal
					: Vec3.Cross( along, up ).Normal;

				if ( axis.LengthSquared < 1e-8f )
					axis = Vec3.Cross( along, new Vec3( 1, 0, 0 ) ).Normal;

				var gain = (1f - settings.Taper) + settings.Taper * d / last;
				var phase = 2f * MathF.PI * ((f + settings.Offset) / MathF.Max( settings.Period, 1f ) - d / MathF.Max( settings.Wavelength, 0.01f ));
				var angle = settings.Amplitude * gain * MathF.Sin( phase ) * MathF.PI / 180f;

				world[i] = TwoBoneIk.About( rest.Origin, Xform.Rotate( axis, angle ) ) * rest;
			}

			var waved = (Xform[])pose.Clone();

			foreach ( var i in bones )
			{
				var parent = skeleton.Bones[i].Parent;
				waved[i] = parent >= 0 ? world[parent].Inverse * world[i] : world[i];
			}

			result.Add( waved );
		}

		return result;
	}

	/// <summary>Named waves, shared by Marionette's menu, its MCP tool and the headless sim.</summary>
	public static readonly (string Key, string Name, Settings Settings)[] Presets =
	{
		("sway", "Gentle sway", new Settings { Amplitude = 8f, Period = 60f, Wavelength = 12f, Taper = 0.7f }),
		("writhe", "Writhe", new Settings { Amplitude = 15f, Period = 36f, Wavelength = 6f, Taper = 0.5f }),
		("wriggle", "Tight wriggle", new Settings { Amplitude = 10f, Period = 18f, Wavelength = 3f, Taper = 0.3f }),
		("undulate", "Undulate (up and down)", new Settings { Amplitude = 12f, Period = 40f, Wavelength = 8f, Taper = 0.6f, Swing = Plane.Up }),
	};

	/// <summary>A copy of a preset by key or index, or null.</summary>
	public static Settings FindPreset( string keyOrIndex )
	{
		var i = int.TryParse( keyOrIndex, out var n ) ? n : Array.FindIndex( Presets, p => string.Equals( p.Key, keyOrIndex, StringComparison.OrdinalIgnoreCase ) );

		if ( i < 0 || i >= Presets.Length )
			return null;

		var s = Presets[i].Settings;
		return new Settings { Amplitude = s.Amplitude, Period = s.Period, Wavelength = s.Wavelength, Taper = s.Taper, Swing = s.Swing, Offset = s.Offset };
	}

	/// <summary>A phase offset that differs from chain to chain but is the same every time for one
	/// chain - so five cables waved one after another never move as twins, and re-baking one gives
	/// the same result.</summary>
	public static float OffsetFor( string chainRoot, float period )
	{
		// Numbered chains (cable0, cable1, tentacle_3...) are spread by the golden ratio on their
		// number, which keeps any run of neighbours far apart in phase; anything else by a hash.
		var digits = new string( (chainRoot ?? "").SkipWhile( c => !char.IsDigit( c ) ).TakeWhile( char.IsDigit ).ToArray() );
		double fraction;

		if ( digits.Length > 0 && int.TryParse( digits, out var n ) )
			fraction = (n + 1) * 0.6180339887 % 1.0;
		else
		{
			var hash = 17;

			foreach ( var c in chainRoot ?? "" )
				hash = unchecked(hash * 31 + c);

			fraction = (hash & 0x7fffffff) % 1000 / 1000.0;
		}

		return (float)fraction * MathF.Max( period, 1f );
	}

	/// <summary>The period nearest <paramref name="period"/> that fits a whole number of swings into
	/// <paramref name="frames"/> - so a looping clip's wave meets itself at the seam.</summary>
	public static float FitPeriod( float period, int frames )
	{
		if ( frames < 1 || period <= 0f )
			return period;

		var cycles = MathF.Max( MathF.Round( frames / period ), 1f );
		return frames / cycles;
	}
}
