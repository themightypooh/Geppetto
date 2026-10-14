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
	EaseOut,

	/// <summary>Hermite through the keys, Catmull-Rom tangents. See <see cref="RigHermite"/>.</summary>
	Cubic,

	/// <summary>Hermite through the keys, Fritsch-Carlson tangents - never overshoots.</summary>
	Monotone
}

/// <summary>What a track drives. THE ORDER IS MARIONETTE'S TrackTarget AND MUST STAY IT, for the
/// same reason as <see cref="RigEase"/>.</summary>
public enum RigTrackKind
{
	Bone,
	Part,

	/// <summary>An IK constraint's goal, in model space, named for the constraint's end bone.</summary>
	IkGoal,

	/// <summary>A camera's shot; the key's scale carries the field of view.</summary>
	Camera
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

	/// <summary>The key's own Cubic/Monotone tangents over <see cref="RigHermite"/>'s ten channels,
	/// or null for automatic. In arrives, Out leaves.</summary>
	public float[] TangentIn, TangentOut;

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

		// Cubic and Monotone curve through the neighbouring keys rather than remapping t, and
		// RigTrack.Evaluate sends them to RigHermite before this is reached - as Marionette does.
		_ => t
	};
}

public sealed class RigTrack
{
	/// <summary>The bone's name, or the qualified name of a part for a part track.</summary>
	public string Name = "";

	/// <summary>Bone, whole part, IK goal or camera.</summary>
	public RigTrackKind Kind;

	/// <summary>True for a whole-part track rather than a skeleton bone.</summary>
	public bool IsPart => Kind == RigTrackKind.Part;

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

			if ( a.Ease is RigEase.Cubic or RigEase.Monotone )
				return EvaluateCubic( i, frame );

			var span = b.Frame - a.Frame;
			var t = span > 0 ? (frame - a.Frame) / span : 0f;

			return RigPose.Lerp( a.Local, b.Local, RigKey.Apply( a.Ease, t ) );
		}

		return Keys[0].Local;
	}

	RigPose EvaluateCubic( int i, float frame )
	{
		var a = Keys[i];
		var b = Keys[i + 1];
		var hasPrev = i > 0;
		var hasNext = i + 2 < Keys.Count;

		Span<float> va = stackalloc float[RigHermite.Channels];
		Span<float> vb = stackalloc float[RigHermite.Channels];
		Span<float> vp = stackalloc float[RigHermite.Channels];
		Span<float> vn = stackalloc float[RigHermite.Channels];
		Span<float> result = stackalloc float[RigHermite.Channels];

		RigHermite.Pack( a.Local, va );
		RigHermite.Pack( b.Local, vb );

		if ( hasPrev )
			RigHermite.Pack( Keys[i - 1].Local, vp );

		if ( hasNext )
			RigHermite.Pack( Keys[i + 2].Local, vn );

		RigHermite.Evaluate( a.Ease == RigEase.Monotone, frame,
			a.Frame, va, Tangent( a.TangentOut ),
			b.Frame, vb, Tangent( b.TangentIn ),
			hasPrev ? Keys[i - 1].Frame : null, vp,
			hasNext ? Keys[i + 2].Frame : null, vn,
			result );

		return RigHermite.Unpack( result );
	}

	static ReadOnlySpan<float> Tangent( float[] t ) => t is { Length: RigHermite.Channels } ? t : ReadOnlySpan<float>.Empty;
}

/// <summary>
/// Hermite segments for the Cubic and Monotone modes. A copy of Marionette's RigCurve - the kernel
/// cannot reference the game assembly - and Effigy.Tests compiles that file beside this one to keep
/// the two in agreement. RigCurve's header has the reasoning; in brief: a transform is ten channels
/// (position xyz, rotation xyzw, scale xyz), each curved on its own with neighbouring rotations
/// flipped onto one hemisphere and renormalised after; tangents are value per frame, automatic
/// ones Catmull-Rom (Cubic) or Fritsch-Carlson held to the alpha,beta &lt;= 3 box (Monotone).
/// </summary>
public static class RigHermite
{
	public const int Channels = 10;

	const int RotationStart = 3;

	public static void Pack( RigPose pose, Span<float> into )
	{
		into[0] = pose.Position.x; into[1] = pose.Position.y; into[2] = pose.Position.z;
		into[3] = pose.Rotation.x; into[4] = pose.Rotation.y; into[5] = pose.Rotation.z; into[6] = pose.Rotation.w;
		into[7] = pose.Scale.x; into[8] = pose.Scale.y; into[9] = pose.Scale.z;
	}

	public static RigPose Unpack( ReadOnlySpan<float> v ) =>
		new( new Vec3( v[0], v[1], v[2] ), new Quat( v[3], v[4], v[5], v[6] ), new Vec3( v[7], v[8], v[9] ) );

	public static void Evaluate( bool monotone, float frame,
		float fa, ReadOnlySpan<float> va, ReadOnlySpan<float> outA,
		float fb, ReadOnlySpan<float> vb, ReadOnlySpan<float> inB,
		float? fp, ReadOnlySpan<float> vp,
		float? fn, ReadOnlySpan<float> vn,
		Span<float> result )
	{
		Span<float> a = stackalloc float[Channels];
		Span<float> b = stackalloc float[Channels];
		Span<float> p = stackalloc float[Channels];
		Span<float> n = stackalloc float[Channels];

		va.CopyTo( a );
		vb.CopyTo( b );

		var signB = Align( a, b );

		if ( fp is not null )
		{
			vp.CopyTo( p );
			Align( a, p );
		}

		if ( fn is not null )
		{
			vn.CopyTo( n );
			Align( b, n );
		}

		var span = fb - fa;
		var t = span > 0f ? Math.Clamp( (frame - fa) / span, 0f, 1f ) : 0f;

		var t2 = t * t;
		var t3 = t2 * t;
		var h00 = 2f * t3 - 3f * t2 + 1f;
		var h10 = t3 - 2f * t2 + t;
		var h01 = -2f * t3 + 3f * t2;
		var h11 = t3 - t2;

		for ( var c = 0; c < Channels; c++ )
		{
			var rotation = c >= RotationStart && c < RotationStart + 4;

			var mA = outA.Length == Channels
				? outA[c]
				: Slope( monotone, fp, fp is null ? 0f : p[c], fa, a[c], fb, b[c] );

			var mB = inB.Length == Channels
				? inB[c] * (rotation ? signB : 1f)
				: Slope( monotone, fa, a[c], fb, b[c], fn, fn is null ? 0f : n[c] );

			result[c] = h00 * a[c] + h10 * span * mA + h01 * b[c] + h11 * span * mB;
		}

		Normalise( result );
	}

	public static float Slope( bool monotone, float? f0, float v0, float f1, float v1, float? f2, float v2 )
	{
		float? before = f0 is { } a && f1 - a > 0f ? (v1 - v0) / (f1 - a) : null;
		float? after = f2 is { } b && b - f1 > 0f ? (v2 - v1) / (b - f1) : null;

		if ( before is null && after is null )
			return 0f;

		if ( before is null )
			return after.Value;

		if ( after is null )
			return before.Value;

		if ( !monotone )
			return (v2 - v0) / (f2.Value - f0.Value);

		var d0 = before.Value;
		var d1 = after.Value;

		if ( d0 * d1 <= 0f )
			return 0f;

		var m = (d0 + d1) * 0.5f;
		var limit = 3f * MathF.Min( MathF.Abs( d0 ), MathF.Abs( d1 ) );

		return MathF.Sign( m ) * MathF.Min( MathF.Abs( m ), limit );
	}

	public static float Align( ReadOnlySpan<float> reference, Span<float> v )
	{
		var dot = 0f;

		for ( var c = RotationStart; c < RotationStart + 4; c++ )
			dot += reference[c] * v[c];

		if ( dot >= 0f )
			return 1f;

		for ( var c = RotationStart; c < RotationStart + 4; c++ )
			v[c] = -v[c];

		return -1f;
	}

	public static void Normalise( Span<float> v )
	{
		var length = 0f;

		for ( var c = RotationStart; c < RotationStart + 4; c++ )
			length += v[c] * v[c];

		length = MathF.Sqrt( length );

		if ( length < 1e-6f )
		{
			v[RotationStart] = v[RotationStart + 1] = v[RotationStart + 2] = 0f;
			v[RotationStart + 3] = 1f;
			return;
		}

		for ( var c = RotationStart; c < RotationStart + 4; c++ )
			v[c] /= length;
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

	public IEnumerable<RigTrack> BoneTracks => Tracks.Where( t => t.Kind == RigTrackKind.Bone );

	/// <summary>The keyed IK goals. They only move bones when <see cref="Ik"/> says how - see
	/// <see cref="SampleLocal"/>.</summary>
	public IEnumerable<RigTrack> IkGoalTracks => Tracks.Where( t => t.Kind == RigTrackKind.IkGoal && t.Keys.Count > 0 );

	/// <summary>
	/// The rig's IK constraints (<see cref="RigIk.ReadRig"/> on the clip's .ctrlrig), which say how
	/// to solve the clip's goal tracks. Null means the rig was not read: a goal is then solved the
	/// way a new Marionette constraint would be - two bones, pole up, full weight - because ignoring
	/// it would check a pose the clip never plays.
	/// </summary>
	public List<RigIk> Ik;

	/// <summary>The .ctrlrig the clip names, as written in the file - empty when there is none.</summary>
	public string RigAsset = "";

	/// <summary>The last frame any track has a key on.</summary>
	public int LastKeyFrame => Tracks.Count == 0 ? 0 : Tracks.Max( t => t.Keys.Count == 0 ? 0 : t.Keys[^1].Frame );

	/// <summary>
	/// The frames worth exporting: up to the last key, but never past the clip's own length.
	/// The same span Marionette's export bakes.
	/// </summary>
	public int LastFrame => Math.Min( LastKeyFrame, Math.Max( FrameCount - 1, 0 ) );

	public RigTrack Find( string boneName ) => Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == boneName );

	public static RigAnimFile ReadFile( string path ) => Read( File.ReadAllText( path ) );

	public static RigAnimFile Read( string json )
	{
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;
		var clip = new RigAnimFile
		{
			SourceModel = Str( root, "SourceModel" ) ?? "",
			RigAsset = Str( root, "RigAsset" ) ?? "",
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
				Kind = t.TryGetProperty( "Target", out var target ) ? ParseKind( target ) : RigTrackKind.Bone,
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

					key.TangentIn = ParseTangent( k, "TangentIn" );
					key.TangentOut = ParseTangent( k, "TangentOut" );

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

		return SolveIk( skeleton, pose, frame );
	}

	/// <summary>
	/// The clip's live IK goals, solved onto a local pose - Marionette's playback order: the
	/// keyframes first, then each constraint in rig order toward its goal at this frame. Returns the
	/// pose unchanged when the clip keys no goals, which is every clip that bakes its IK.
	/// </summary>
	public Xform[] SolveIk( Skeleton skeleton, Xform[] local, float frame )
	{
		var goals = IkGoalTracks.ToList();

		if ( goals.Count == 0 )
			return local;

		var constraints = Ik is not null
			? Ik.Where( c => c.Enabled && c.Live ).ToList()
			: goals.Select( g => new RigIk { Name = g.Name, EndBone = g.Name } ).ToList();

		var world = ModelSpace( skeleton, local );
		var moved = false;

		foreach ( var ik in constraints )
		{
			if ( goals.FirstOrDefault( g => g.Name == ik.EndBone ) is not { } goal )
				continue;

			if ( ChainIk.Apply( skeleton, world, ik, goal.Evaluate( frame ) ) is { } solved )
			{
				world = solved;
				moved = true;
			}
		}

		return moved ? LocalSpace( skeleton, world ) : local;
	}

	/// <summary>
	/// Bake to a flat clip at every whole frame from 0 to <see cref="LastFrame"/>, live IK solved.
	///
	/// Scale is split out of each pose into <see cref="AnimClip.Scales"/> - a bone's uniform scale,
	/// the mean of its three axes - and the rotation written unscaled, because the DMX writer reads
	/// the quaternion off the basis and a scaled basis is not a rotation. A clip that never scales
	/// a bone gets no Scales at all, and bakes exactly as it did.
	/// </summary>
	public AnimClip ToClip( Skeleton skeleton, string name = "anim", bool looping = false )
	{
		var clip = new AnimClip { Name = name, FrameRate = FrameRate, Looping = looping };

		for ( var f = 0; f <= LastFrame; f++ )
		{
			var pose = SampleLocal( skeleton, f );
			float[] scales = null;

			for ( var b = 0; b < pose.Length; b++ )
			{
				var s = Quat.ScaleOf( pose[b] );
				var uniform = (s.x + s.y + s.z) / 3f;

				if ( MathF.Abs( uniform - 1f ) <= 1e-5f )
					continue;

				scales ??= Enumerable.Repeat( 1f, pose.Length ).ToArray();
				scales[b] = uniform;
				pose[b] = Quat.FromXform( pose[b] ).ToXform( pose[b].Origin );
			}

			clip.AddFrame( pose, scales );
		}

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

	/// <summary>A kind this reader does not know - a newer Marionette's - is not a bone. Reading it as
	/// one would pose a skeleton bone from whatever the new kind stores.</summary>
	static RigTrackKind ParseKind( JsonElement target )
	{
		if ( target.ValueKind == JsonValueKind.Number )
		{
			var n = target.GetInt32();
			return n >= 0 && n <= (int)RigTrackKind.Camera ? (RigTrackKind)n : (RigTrackKind)(-1);
		}

		if ( target.ValueKind == JsonValueKind.String )
			return Enum.TryParse<RigTrackKind>( target.GetString(), true, out var kind ) ? kind : (RigTrackKind)(-1);

		return RigTrackKind.Bone;
	}

	static float[] ParseTangent( JsonElement key, string name )
	{
		if ( !key.TryGetProperty( name, out var e ) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != RigHermite.Channels )
			return null;

		return e.EnumerateArray().Select( v => v.GetSingle() ).ToArray();
	}

	static RigEase ParseEase( JsonElement e )
	{
		if ( e.ValueKind == JsonValueKind.Number )
			return (RigEase)Math.Clamp( e.GetInt32(), 0, (int)RigEase.Monotone );

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
