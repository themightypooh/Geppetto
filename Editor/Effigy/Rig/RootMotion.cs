using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>Which part of the body's travel becomes root motion. THE ORDER IS MARIONETTE'S
/// RootMotionMode AND MUST STAY IT - a .riganim may carry it as an ordinal.</summary>
public enum RootMotionMode
{
	/// <summary>Travel along the ground only (x and y). Bob and turn stay in the body.</summary>
	XY,

	/// <summary>Ground travel and the turn about the up axis - a walk that curves.</summary>
	XYYaw,

	/// <summary>Travel in x, y and z (a jump, a climb) and the turn about up.</summary>
	Full
}

/// <summary>
/// A clip's root motion: which bone carries the travel, which bone it was taken from, and how.
/// Saved on the clip as Marionette's RigAnimDocument.RootMotion; null when the clip has none.
/// </summary>
public sealed class RootMotionSettings
{
	/// <summary>The bone the travel is moved onto and the game reads it from - the top of the
	/// skeleton ("root"). ModelDoc's ExtractMotion names it as root_bone_name.</summary>
	public string Bone = "root";

	/// <summary>The bone whose travel is extracted - the hips or pelvis. Empty, or the same as
	/// <see cref="Bone"/>, means the travel is already keyed on the root.</summary>
	public string Source = "";

	public RootMotionMode Mode = RootMotionMode.XY;

	/// <summary>A straight, even-speed line from the first frame to the last instead of following
	/// the hips frame by frame: the step-to-step surge stays in the body and the root glides. What
	/// ModelDoc's "linear" does, and what a looping walk wants.</summary>
	public bool Linear = true;

	public bool Enabled = true;

	public bool Translates( int axis ) => axis < 2 || Mode == RootMotionMode.Full;
	public bool Turns => Mode != RootMotionMode.XY;

	/// <summary>The clip's "RootMotion" object, or null when it has none (or it is switched off).</summary>
	public static RootMotionSettings Read( JsonElement root )
	{
		if ( !root.TryGetProperty( "RootMotion", out var e ) || e.ValueKind != JsonValueKind.Object )
			return null;

		var s = new RootMotionSettings
		{
			Bone = Str( e, "Bone" ) ?? "root",
			Source = Str( e, "Source" ) ?? "",
			Linear = !e.TryGetProperty( "Linear", out var lin ) || lin.ValueKind != JsonValueKind.False,
			Enabled = !e.TryGetProperty( "Enabled", out var en ) || en.ValueKind != JsonValueKind.False,
		};

		if ( e.TryGetProperty( "Mode", out var m ) )
		{
			if ( m.ValueKind == JsonValueKind.Number && m.TryGetInt32( out var i ) && i >= 0 && i <= (int)RootMotionMode.Full )
				s.Mode = (RootMotionMode)i;
			else if ( m.ValueKind == JsonValueKind.String && Enum.TryParse<RootMotionMode>( m.GetString(), true, out var mode ) )
				s.Mode = mode;
		}

		return s;
	}

	static string Str( JsonElement e, string name ) =>
		e.TryGetProperty( name, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Root motion: the body's travel moved off the hips onto the root bone, so the game can move the
/// character by it and the feet stay planted, instead of the clip walking off and snapping back.
///
/// EXTRACTION KEEPS EVERY POSE. The root is keyed to carry the travel (<see cref="MotionOf"/> of the
/// source bone: ground travel, plus the turn about up and the height when the mode asks), and every
/// direct child of the root is keyed back by the inverse, so in the model's space the clip looks
/// exactly as it did. What changes is where the travel lives: on the root, where ModelDoc's
/// ExtractMotion (<see cref="VmdlAnimation.AnimFile"/>) and Marionette's player read it from.
///
/// IN PLACE (<see cref="InPlace"/>) is the clip as the character sees it while the game moves it:
/// the root's travel taken back out. That is what the body does on the spot, and what a loop seam is
/// judged on.
///
/// LOOP-SAFE. The travel is measured from frame 0, so the root starts where it was; Linear makes the
/// travel an even line, so whatever the hips do around it is the same at both ends of a loop; and a
/// player that wraps adds one whole cycle's travel (<see cref="CycleOf"/>) rather than snapping back.
/// Marionette's twin is RigRootMotion (Code/RigControl).
/// </summary>
public static class RootMotion
{
	public sealed class Result
	{
		public string Error;
		public bool Ok => Error is null;
		public int Keys;
		public int Frames;

		/// <summary>Whole-clip travel in model space, and the turn in degrees.</summary>
		public Vec3 Travel;
		public float Yaw;
		public List<string> Notes = new();

		public override string ToString() => Error ?? string.Create( CultureInfo.InvariantCulture,
			$"root motion: {Travel.Length:0.##} units ({Travel.x:0.##}, {Travel.y:0.##}, {Travel.z:0.##}), turn {Yaw:0.#}° over {Frames} frame(s), {Keys} key(s)" )
			+ (Notes.Count > 0 ? "; " + string.Join( "; ", Notes ) : "");
	}

	/// <summary>
	/// The motion between two model-space transforms of one bone, as a model-space transform that
	/// carries <paramref name="start"/> toward <paramref name="now"/>: the ground offset (and height,
	/// for Full), and the turn about up (for XY + Yaw and Full) pivoting on where the bone started.
	/// Identity when nothing moved. Axis-agnostic: the turn is the twist of the bone's rotation
	/// change about up, so no bone axis is assumed to be forward.
	/// </summary>
	public static Xform MotionOf( Xform start, Xform now, RootMotionMode mode, float? yawOverride = null, Vec3? travelOverride = null )
	{
		var d = travelOverride ?? now.Origin - start.Origin;

		if ( mode != RootMotionMode.Full )
			d = new Vec3( d.x, d.y, 0 );

		var yaw = mode == RootMotionMode.XY ? 0f : yawOverride ?? YawBetween( start, now );
		var pivot = start.Origin;

		return Xform.Translate( pivot + d ) * Xform.Rotate( new Vec3( 0, 0, 1 ), yaw ) * Xform.Translate( -pivot );
	}

	/// <summary>The turn about +z (radians, -π..π) taking <paramref name="a"/>'s rotation to
	/// <paramref name="b"/>'s: the twist part of the change.</summary>
	public static float YawBetween( Xform a, Xform b )
	{
		var q = (Quat.FromXform( b ) * Quat.FromXform( a ).Inverse).Normalized;
		var angle = 2f * MathF.Atan2( q.z, q.w );
		return Wrap( angle );
	}

	static float Wrap( float a )
	{
		while ( a > MathF.PI ) a -= 2f * MathF.PI;
		while ( a < -MathF.PI ) a += 2f * MathF.PI;
		return a;
	}

	/// <summary>
	/// Extract <paramref name="settings"/>' root motion into the clip in place: the root keyed with
	/// the source's travel, the root's direct children keyed back so every pose holds, frames 0 to
	/// <see cref="RigAnimFile.LastFrame"/>. Sets <see cref="RigAnimFile.RootMotion"/>. On failure the
	/// clip is untouched and the result says why.
	/// </summary>
	public static Result Extract( RigAnimFile clip, Skeleton skeleton, RootMotionSettings settings, bool reduce = true )
	{
		if ( clip is null || skeleton is null || settings is null )
			return new Result { Error = "no clip, skeleton or settings" };

		var saved = clip.RootMotion;
		clip.RootMotion = null;     // sample the clip as keyed, never in place

		var frames = Enumerable.Range( 0, Math.Max( clip.LastFrame, 0 ) + 1 ).Select( f => clip.SampleLocal( skeleton, f ) ).ToList();
		var result = Plan( frames, skeleton, settings, out var keyed );

		if ( !result.Ok || keyed.Count == 0 )
		{
			clip.RootMotion = result.Ok ? settings : saved;
			return result;
		}

		var last = frames.Count - 1;

		foreach ( var (bone, locals) in keyed )
		{
			var track = TrackFor( clip, skeleton.Bones[bone].Name );
			track.Keys.RemoveAll( k => k.Frame >= 0 && k.Frame <= last );

			for ( var f = 0; f <= last; f++ )
				ClipKeys.Set( track, f, RigPose.FromXform( locals[f] ), RigEase.Linear );

			if ( reduce )
				result.Keys -= ClipBake.Reduce( track, 0, last, 0.01f, 0.1f );
		}

		if ( saved is not null && saved.Bone != settings.Bone )
			result.Notes.Add( $"replaces the root motion that was on '{saved.Bone}'" );

		clip.RootMotion = settings;
		return result;
	}

	/// <summary>
	/// The extraction on sampled frames (local poses, frame 0 first - what a clip plays, IK solved):
	/// <paramref name="keyed"/> gets the new local of the root and of each of its direct children on
	/// every frame. Empty when the travel is already on the root. Shared by <see cref="Extract"/> and
	/// Marionette's timeline, which samples the open clip its own way.
	/// </summary>
	public static Result Plan( IReadOnlyList<Xform[]> frames, Skeleton skeleton, RootMotionSettings settings, out Dictionary<int, Xform[]> keyed )
	{
		var result = new Result();
		keyed = new Dictionary<int, Xform[]>();

		if ( frames is null || skeleton is null || settings is null ) { result.Error = "no clip, skeleton or settings"; return result; }

		var root = skeleton.IndexOf( settings.Bone ?? "" );

		if ( root < 0 ) { result.Error = $"the model has no bone '{settings.Bone}' to carry root motion - pick its top bone (Root Bone)"; return result; }

		var sourceName = string.IsNullOrEmpty( settings.Source ) ? settings.Bone : settings.Source;
		var source = skeleton.IndexOf( sourceName );

		if ( source < 0 ) { result.Error = $"the model has no bone '{sourceName}' to take the travel from - pick the hips or pelvis (Travel From)"; return result; }

		// Two layouts. The usual one has the hips under the root: the root takes the travel and the
		// hips ride it, so no pose changes. Citizen's has them side by side - pelvis and root_IK are
		// both top bones, and ModelDoc extracts from root_IK - so the root takes the travel and the
		// hips are keyed in place: the body walks on the spot, the root walks away.
		var beside = source != root && !IsUnder( skeleton, source, root );

		if ( beside && IsUnder( skeleton, root, source ) )
		{
			result.Error = $"'{settings.Bone}' is under '{sourceName}', so it cannot carry it - pick the skeleton's top bone (or a top-level root bone beside the hips)";
			return result;
		}

		var last = frames.Count - 1;

		if ( last < 1 ) { result.Error = "the clip has no motion - key at least two frames"; return result; }

		var worlds = frames.Select( f => RigAnimFile.ModelSpace( skeleton, f ) ).ToArray();
		var path = worlds.Select( w => w[source] ).ToArray();
		var motion = Motions( path, settings );
		var travel = path[last].Origin - path[0].Origin;

		result.Frames = last + 1;
		result.Travel = settings.Mode == RootMotionMode.Full ? travel : new Vec3( travel.x, travel.y, 0 );
		result.Yaw = settings.Turns ? Unwrapped( path )[last] * 180f / MathF.PI : 0f;

		if ( source == root )
		{
			result.Notes.Add( $"the travel is already keyed on '{settings.Bone}' - nothing to move, the clip is marked for root motion" );
			return result;
		}

		var parent = skeleton.Bones[root].Parent;
		var children = Enumerable.Range( 0, skeleton.Count ).Where( b => skeleton.Bones[b].Parent == root ).ToList();
		var sourceParent = skeleton.Bones[source].Parent;

		keyed[root] = new Xform[last + 1];

		foreach ( var c in children )
			keyed[c] = new Xform[last + 1];

		if ( beside )
		{
			keyed[source] = new Xform[last + 1];
			result.Notes.Add( $"'{sourceName}' is not under '{settings.Bone}', so it is now keyed in place and '{settings.Bone}' carries the travel (Citizen's layout)" );
		}

		for ( var f = 0; f <= last; f++ )
		{
			var w = worlds[f];
			var carried = motion[f] * w[root];

			keyed[root][f] = parent >= 0 ? w[parent].Inverse * carried : carried;

			foreach ( var c in children )
				keyed[c][f] = carried.Inverse * w[c];

			if ( beside )
			{
				var still = motion[f].Inverse * w[source];
				keyed[source][f] = sourceParent >= 0 ? w[sourceParent].Inverse * still : still;
			}
		}

		result.Keys = keyed.Count * (last + 1);
		return result;
	}

	/// <summary>The motion transform at every frame of a source bone's model-space path: measured from
	/// frame 0, followed exactly or as an even line to the last frame (<see cref="RootMotionSettings.Linear"/>).</summary>
	public static Xform[] Motions( Xform[] path, RootMotionSettings settings )
	{
		var n = path.Length - 1;
		var yaw = settings.Turns ? Unwrapped( path ) : new float[path.Length];
		var result = new Xform[path.Length];

		for ( var f = 0; f <= n; f++ )
		{
			if ( settings.Linear && n > 0 )
			{
				var t = f / (float)n;
				result[f] = MotionOf( path[0], path[f], settings.Mode, yaw[n] * t, (path[n].Origin - path[0].Origin) * t );
			}
			else
				result[f] = MotionOf( path[0], path[f], settings.Mode, yaw[f] );
		}

		return result;
	}

	/// <summary>The yaw of each frame against frame 0, unwrapped so a full turn keeps counting.</summary>
	static float[] Unwrapped( Xform[] path )
	{
		var yaw = new float[path.Length];

		for ( var f = 1; f < path.Length; f++ )
			yaw[f] = yaw[f - 1] + YawBetween( path[f - 1], path[f] );

		return yaw;
	}

	/// <summary>
	/// The root's motion at a frame of a clip already carrying root motion: what the travel on
	/// <see cref="RootMotionSettings.Bone"/> is, measured from frame 0. Identity without root motion.
	/// </summary>
	public static Xform MotionAt( RigAnimFile clip, Skeleton skeleton, Xform[] world, float frame )
	{
		if ( clip?.RootMotion is not { Enabled: true } settings )
			return Xform.Identity;

		var root = skeleton.IndexOf( settings.Bone );

		if ( root < 0 )
			return Xform.Identity;

		var start = RootAt( clip, skeleton, root, 0f );
		return MotionOf( start, world[root], settings.Mode );
	}

	/// <summary>The travel of one whole cycle (last frame against first) - what a looping player adds
	/// at each wrap so the character keeps going instead of snapping back.</summary>
	public static Xform CycleOf( RigAnimFile clip, Skeleton skeleton )
	{
		if ( clip?.RootMotion is not { Enabled: true } settings || skeleton.IndexOf( settings.Bone ) is var root && root < 0 )
			return Xform.Identity;

		return MotionOf( RootAt( clip, skeleton, root, 0f ), RootAt( clip, skeleton, root, clip.LastFrame ), settings.Mode );
	}

	/// <summary>The root bone's model-space transform from its own keys under its bind-pose parents.</summary>
	static Xform RootAt( RigAnimFile clip, Skeleton skeleton, int root, float frame )
	{
		var bind = RigAnimFile.ModelSpace( skeleton, skeleton.Bones.Select( b => b.Local ).ToArray() );
		var parent = skeleton.Bones[root].Parent;
		var local = clip.Find( skeleton.Bones[root].Name ) is { Keys.Count: > 0 } t ? t.Evaluate( frame ).ToXform() : skeleton.Bones[root].Local;
		return parent >= 0 ? bind[parent] * local : local;
	}

	/// <summary>
	/// A local pose with the root's travel taken out - the clip as it plays on a character the game
	/// moves by that travel. Unchanged without root motion.
	/// </summary>
	public static Xform[] InPlace( RigAnimFile clip, Skeleton skeleton, Xform[] local, float frame )
	{
		if ( clip?.RootMotion is not { Enabled: true } settings || skeleton.IndexOf( settings.Bone ) is var root && root < 0 )
			return local;

		var world = RigAnimFile.ModelSpace( skeleton, local );
		var motion = MotionAt( clip, skeleton, world, frame );
		var parent = skeleton.Bones[root].Parent;
		var placed = motion.Inverse * world[root];
		var copy = local.ToArray();
		copy[root] = parent >= 0 ? world[parent].Inverse * placed : placed;
		return copy;
	}

	/// <summary>One contact's slide, with and without root motion. See <see cref="SlideReport"/>.</summary>
	public readonly record struct Slide( string Bone, int Spans, float InPlace, float WithRootMotion );

	/// <summary>
	/// How far each contact (a foot) slides while it is planted, two ways: played in place with the
	/// root motion thrown away (the character stands still, so the feet skate by the stride), and
	/// with it applied (the world pose; planted feet should hold). Planted spans are found on the
	/// world pose - where the foot really stands still. Contacts are globs; empty means bones named
	/// like feet, toes or ankles.
	/// </summary>
	public static List<Slide> SlideReport( RigAnimFile clip, Skeleton skeleton, IReadOnlyList<string> contacts, float plantSpeed = 0.2f, int substeps = 2 )
	{
		var report = new List<Slide>();

		if ( clip?.RootMotion is not { Enabled: true } )
			return report;

		var globs = contacts is { Count: > 0 } ? contacts : new[] { "*foot*", "*Foot*", "*toe*", "*ankle*", "*Ankle*" };
		var bones = Enumerable.Range( 0, skeleton.Count ).Where( b => MotionLint.Matches( skeleton.Bones[b].Name, globs ) ).ToList();
		var steps = Math.Max( substeps, 1 );
		var count = clip.LastFrame * steps + 1;
		var world = new Xform[count][];
		var still = new Xform[count][];
		var inPlaceWas = clip.PlayInPlace;
		clip.PlayInPlace = false;

		for ( var i = 0; i < count; i++ )
		{
			var local = clip.SampleLocal( skeleton, i / (float)steps );
			world[i] = RigAnimFile.ModelSpace( skeleton, local );
			still[i] = RigAnimFile.ModelSpace( skeleton, InPlace( clip, skeleton, local, i / (float)steps ) );
		}

		clip.PlayInPlace = inPlaceWas;

		foreach ( var b in bones )
		{
			var spans = 0;
			float inPlace = 0f, with = 0f;
			var i = 0;

			while ( i < count - 1 )
			{
				if ( (world[i + 1][b].Origin - world[i][b].Origin).Length * steps > plantSpeed )
				{
					i++;
					continue;
				}

				var start = i;

				while ( i < count - 1 && (world[i + 1][b].Origin - world[i][b].Origin).Length * steps <= plantSpeed )
					i++;

				if ( i - start < steps )
					continue;

				spans++;

				for ( var j = start; j <= i; j++ )
				{
					with = MathF.Max( with, (world[j][b].Origin - world[start][b].Origin).Length );
					inPlace = MathF.Max( inPlace, (still[j][b].Origin - still[start][b].Origin).Length );
				}
			}

			report.Add( new Slide( skeleton.Bones[b].Name, spans, inPlace, with ) );
		}

		return report;
	}

	static bool IsUnder( Skeleton skeleton, int bone, int ancestor )
	{
		for ( var b = skeleton.Bones[bone].Parent; b >= 0; b = skeleton.Bones[b].Parent )
			if ( b == ancestor )
				return true;

		return false;
	}

	static RigTrack TrackFor( RigAnimFile clip, string bone )
	{
		if ( clip.Find( bone ) is { } track )
			return track;

		track = new RigTrack { Name = bone, Kind = RigTrackKind.Bone };
		clip.Tracks.Add( track );
		return track;
	}
}
