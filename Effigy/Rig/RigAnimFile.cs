using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// How a key eases into the next. THE ORDER IS MARIONETTE'S KeyInterpolation AND MUST STAY IT:
/// a .riganim can carry the mode as its ordinal, so a value inserted here instead of appended
/// would silently re-time every clip that used the ones after it.
/// </summary>
public enum RigEase
{
	Smooth,
	Linear,
	Stepped,
	EaseIn,
	EaseOut
}

/// <summary>One bone's local transform: what a keyframe stores and what a track evaluates to.</summary>
public readonly record struct RigPose( Vec3 Position, Quat Rotation, Vec3 Scale )
{
	public static readonly RigPose Identity = new( Vec3.Zero, Quat.Identity, Vec3.One );

	public Xform ToXform() => Rotation.ToXform( Position, Scale );

	public static RigPose FromXform( Xform t ) => new( t.Origin, Quat.FromXform( t ), Quat.ScaleOf( t ) );

	/// <summary>Marionette's Transform.Lerp: position and scale lerped, rotation slerped.</summary>
	public static RigPose Lerp( RigPose a, RigPose b, float t ) =>
		new( Vec3.Lerp( a.Position, b.Position, t ), Quat.Slerp( a.Rotation, b.Rotation, t ), Vec3.Lerp( a.Scale, b.Scale, t ) );
}

public sealed class RigKey
{
	public int Frame;
	public RigPose Local = RigPose.Identity;

	/// <summary>Governs the segment LEAVING this key.</summary>
	public RigEase Ease = RigEase.Smooth;

	/// <summary>
	/// Remap a 0..1 position in this key's outgoing segment. The curves are s&box's
	/// Easing.QuadraticInOut / QuadraticIn / QuadraticOut, written out, because those are what
	/// Marionette's BoneKeyframe.Ease calls.
	/// </summary>
	public static float Apply( RigEase ease, float t ) => ease switch
	{
		RigEase.Smooth => t < 0.5f ? 2f * t * t : 1f - MathF.Pow( -2f * t + 2f, 2f ) / 2f,
		RigEase.EaseIn => t * t,
		RigEase.EaseOut => 1f - (1f - t) * (1f - t),
		RigEase.Stepped => 0f,
		_ => t
	};
}

public sealed class RigTrack
{
	/// <summary>The bone's name, or the qualified name of a part for a part track.</summary>
	public string Name = "";

	/// <summary>True for a whole-part track rather than a skeleton bone.</summary>
	public bool IsPart;

	/// <summary>Sorted by frame.</summary>
	public List<RigKey> Keys = new();

	/// <summary>
	/// The local transform at a frame. A line-for-line port of Marionette's BoneTrack.Evaluate:
	/// held before the first key and after the last, and between two keys eased by the FIRST of
	/// them. Fractional frames are fine - that is how a take is checked between frames.
	/// </summary>
	public RigPose Evaluate( float frame )
	{
		if ( Keys.Count == 0 )
			return RigPose.Identity;

		if ( frame <= Keys[0].Frame )
			return Keys[0].Local;

		if ( frame >= Keys[^1].Frame )
			return Keys[^1].Local;

		for ( var i = 0; i < Keys.Count - 1; i++ )
		{
			var a = Keys[i];
			var b = Keys[i + 1];

			if ( frame < a.Frame || frame > b.Frame )
				continue;

			var span = b.Frame - a.Frame;
			var t = span > 0 ? (frame - a.Frame) / span : 0f;

			return RigPose.Lerp( a.Local, b.Local, RigKey.Apply( a.Ease, t ) );
		}

		return Keys[0].Local;
	}
}

/// <summary>
/// A Marionette .riganim read without the engine, and evaluated the way Marionette plays it.
///
/// WHY THE KERNEL READS ITS OWN. Anything checking a take outside the editor - a sign-off script,
/// an exporter, a CI job - needs the bone poses at arbitrary frames, and the only evaluator was in
/// the game assembly, on s&box's Transform. Every tool that wanted one grew its own copy of the
/// easing code, and copies drift. This is the one copy outside the engine, pinned to Marionette's
/// by a test, so the next tool calls it instead.
///
/// ONLY THE PARTS A POSE NEEDS are read: the tracks, their keys, the frame rate and length, and
/// which model the clip was made on. Props, lights, cameras and events are Marionette's business.
/// </summary>
public sealed class RigAnimFile
{
	public string SourceModel = "";
	public int FrameRate = 30;
	public int FrameCount;
	public List<RigTrack> Tracks = new();

	public IEnumerable<RigTrack> BoneTracks => Tracks.Where( t => !t.IsPart );

	/// <summary>The last frame any track has a key on.</summary>
	public int LastKeyFrame => Tracks.Count == 0 ? 0 : Tracks.Max( t => t.Keys.Count == 0 ? 0 : t.Keys[^1].Frame );

	/// <summary>
	/// The frames worth exporting: up to the last key, but never past the clip's own length.
	/// The same span Marionette's export bakes.
	/// </summary>
	public int LastFrame => Math.Min( LastKeyFrame, Math.Max( FrameCount - 1, 0 ) );

	public RigTrack Find( string boneName ) => Tracks.FirstOrDefault( t => !t.IsPart && t.Name == boneName );

	public static RigAnimFile ReadFile( string path ) => Read( File.ReadAllText( path ) );

	public static RigAnimFile Read( string json )
	{
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;
		var clip = new RigAnimFile
		{
			SourceModel = Str( root, "SourceModel" ) ?? "",
			FrameRate = root.TryGetProperty( "AnimationSpeed", out var speed ) && speed.ValueKind == JsonValueKind.Number ? (int)MathF.Round( speed.GetSingle() ) : 30,
			FrameCount = root.TryGetProperty( "FrameCount", out var count ) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0,
		};

		if ( !root.TryGetProperty( "BoneTracks", out var tracks ) || tracks.ValueKind != JsonValueKind.Array )
			return clip;

		foreach ( var t in tracks.EnumerateArray() )
		{
			var track = new RigTrack
			{
				Name = Str( t, "BoneName" ) ?? "",
				IsPart = t.TryGetProperty( "Target", out var target ) && IsPartTarget( target ),
			};

			if ( t.TryGetProperty( "Keyframes", out var keys ) && keys.ValueKind == JsonValueKind.Array )
			{
				foreach ( var k in keys.EnumerateArray() )
				{
					var key = new RigKey
					{
						Frame = k.TryGetProperty( "Frame", out var f ) ? f.GetInt32() : 0,
						Ease = k.TryGetProperty( "Interpolation", out var ease ) ? ParseEase( ease ) : RigEase.Smooth,
					};

					if ( k.TryGetProperty( "Local", out var local ) )
						key.Local = ParsePose( local );

					track.Keys.Add( key );
				}
			}

			// Stable, so two keys on one frame keep their file order the way List.Sort with
			// Marionette's comparison would (it only sorts when something is out of order).
			track.Keys = track.Keys.OrderBy( k => k.Frame ).ToList();
			clip.Tracks.Add( track );
		}

		return clip;
	}

	/// <summary>
	/// Every bone's local pose at a frame, in skeleton order. A bone with no track - or a track
	/// with no keys - sits at its bind pose, which is what the animator saw.
	/// </summary>
	public Xform[] SampleLocal( Skeleton skeleton, float frame )
	{
		var pose = new Xform[skeleton.Count];

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var track = Find( skeleton.Bones[b].Name );
			pose[b] = track is { Keys.Count: > 0 } ? track.Evaluate( frame ).ToXform() : skeleton.Bones[b].Local;
		}

		return pose;
	}

	/// <summary>Bake to a flat clip at every whole frame from 0 to <see cref="LastFrame"/>.</summary>
	public AnimClip ToClip( Skeleton skeleton, string name = "anim", bool looping = false )
	{
		var clip = new AnimClip { Name = name, FrameRate = FrameRate, Looping = looping };

		for ( var f = 0; f <= LastFrame; f++ )
			clip.AddFrame( SampleLocal( skeleton, f ) );

		return clip;
	}

	/// <summary>Local poses to model space, parents first. Bones must be ordered parent-before-child,
	/// which every skeleton the kernel builds is.</summary>
	public static Xform[] ModelSpace( Skeleton skeleton, IReadOnlyList<Xform> local )
	{
		var world = new Xform[skeleton.Count];

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var parent = skeleton.Bones[b].Parent;
			world[b] = parent >= 0 ? world[parent] * local[b] : local[b];
		}

		return world;
	}

	/// <summary>Model space back to local: the inverse of <see cref="ModelSpace"/>.</summary>
	public static Xform[] LocalSpace( Skeleton skeleton, IReadOnlyList<Xform> world )
	{
		var local = new Xform[skeleton.Count];

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var parent = skeleton.Bones[b].Parent;
			local[b] = parent >= 0 ? world[parent].Inverse * world[b] : world[b];
		}

		return local;
	}

	// --- parsing ---------------------------------------------------------------------------------

	static string Str( JsonElement e, string name ) =>
		e.TryGetProperty( name, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	static bool IsPartTarget( JsonElement target ) => target.ValueKind switch
	{
		JsonValueKind.String => string.Equals( target.GetString(), "Part", StringComparison.OrdinalIgnoreCase ),
		JsonValueKind.Number => target.GetInt32() == 1,
		_ => false
	};

	static RigEase ParseEase( JsonElement e )
	{
		if ( e.ValueKind == JsonValueKind.Number )
			return (RigEase)Math.Clamp( e.GetInt32(), 0, 4 );

		var text = (e.GetString() ?? "").Replace( " ", "" );
		return Enum.TryParse<RigEase>( text, true, out var ease ) ? ease : RigEase.Smooth;
	}

	static RigPose ParsePose( JsonElement local )
	{
		var p = Floats( Str( local, "Position" ), 3 ) ?? new float[] { 0, 0, 0 };
		var r = Floats( Str( local, "Rotation" ), 4 ) ?? new float[] { 0, 0, 0, 1 };
		var s = Floats( Str( local, "Scale" ), 3 ) ?? new float[] { 1, 1, 1 };
		return new RigPose( new Vec3( p[0], p[1], p[2] ), new Quat( r[0], r[1], r[2], r[3] ), new Vec3( s[0], s[1], s[2] ) );
	}

	static float[] Floats( string text, int count )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return null;

		var parts = text.Split( ',' );

		if ( parts.Length < count )
			return null;

		var values = new float[count];

		for ( var i = 0; i < count; i++ )
			values[i] = float.Parse( parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture );

		return values;
	}
}
