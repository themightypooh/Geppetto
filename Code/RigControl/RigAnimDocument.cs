using Sandbox;
using System.Collections.Generic;
using System.Linq;

namespace Marionette;

// A keyframed clip authored against a RigDocument: a diamond-per-bone timeline of FK poses,
// plus AnimEvents (prop attach/detach) and morph triggers layered over the same frame range.
// Replaces the older RigEventsAsset, which only carried events and had nothing to actually pose
// or play back - this is the real shape of the tool (see RigDocument for the rig/constraint half).
[AssetType( Name = "Rig Animation", Extension = "riganim", Category = "Midnight AM" )]
public sealed class RigAnimDocument : GameResource
{
	[Property, Group( "Source" )] public Model SourceModel { get; set; }
	[Property, Group( "Source" ), Title( "Rig Asset Path" )] public RigDocument RigAsset { get; set; }
	[Property, Group( "Source" ), Title( "Animation Speed" )] public int AnimationSpeed { get; set; } = 30;
	[Property, Group( "Source" ), Title( "Frame Count" )] public int FrameCount { get; set; } = 30;

	[Property] public List<BoneTrack> BoneTracks { get; set; } = new();
	[Property] public List<RigEvent> Events { get; set; } = new();
	[Property] public List<MorphEvent> MorphEvents { get; set; } = new();

	public BoneTrack FindTrack( string bone ) => BoneTracks.FirstOrDefault( t => t.BoneName == bone );

	public BoneTrack GetOrAddTrack( string bone )
	{
		var track = FindTrack( bone );

		if ( track is null )
		{
			track = new BoneTrack { BoneName = bone };
			BoneTracks.Add( track );
		}

		return track;
	}
}

public sealed class BoneTrack
{
	[Property] public string BoneName { get; set; } = "";
	[Property] public List<BoneKeyframe> Keyframes { get; set; } = new();

	/// <summary>The bone's local (parent-space) transform at a given frame, interpolated between
	/// the keyframes either side of it. Constant before the first key and after the last.
	///
	/// The easing between two keys is set by the FIRST of them - a key governs the segment leaving
	/// it. See KeyInterpolation for why the default isn't linear.</summary>
	public Transform Evaluate( float frame )
	{
		if ( Keyframes.Count == 0 )
			return Transform.Zero;

		EnsureSorted();

		if ( frame <= Keyframes[0].Frame )
			return Keyframes[0].Local;

		if ( frame >= Keyframes[^1].Frame )
			return Keyframes[^1].Local;

		for ( var i = 0; i < Keyframes.Count - 1; i++ )
		{
			var a = Keyframes[i];
			var b = Keyframes[i + 1];

			if ( frame < a.Frame || frame > b.Frame )
				continue;

			var span = b.Frame - a.Frame;
			var t = span > 0 ? (frame - a.Frame) / span : 0f;

			return Transform.Lerp( a.Local, b.Local, a.Ease( t ), true );
		}

		return Keyframes[0].Local;
	}

	/// <summary>Evaluate walks the list in order, so it has to be in order. This used to be an
	/// OrderBy().ToList() inside Evaluate itself - a sort and an allocation per bone per frame,
	/// which at ~95 bones and 60fps is thousands of throwaway lists a second. Keyframes are
	/// almost always already sorted (they're inserted in place), so this scans first and only
	/// sorts - in place, no allocation - when something actually moved.</summary>
	private void EnsureSorted()
	{
		for ( var i = 1; i < Keyframes.Count; i++ )
		{
			if ( Keyframes[i - 1].Frame <= Keyframes[i].Frame )
				continue;

			Keyframes.Sort( ( x, y ) => x.Frame.CompareTo( y.Frame ) );
			return;
		}
	}

	public void SetKeyframe( int frame, Transform local )
	{
		var existing = Keyframes.FirstOrDefault( k => k.Frame == frame );

		if ( existing is not null )
		{
			existing.Local = local;
			return;
		}

		// Inserted in place rather than appended, so the list stays sorted and EnsureSorted's
		// scan stays the fast path.
		var index = Keyframes.FindIndex( k => k.Frame > frame );

		if ( index < 0 )
			Keyframes.Add( new BoneKeyframe { Frame = frame, Local = local } );
		else
			Keyframes.Insert( index, new BoneKeyframe { Frame = frame, Local = local } );
	}
}

/// <summary>
/// How a keyframe eases into the next one.
///
/// Smooth is the default, deliberately. Straight linear interpolation moves a bone at a constant
/// speed and then stops dead - no acceleration, no settle - which reads as robotic no matter how
/// good the poses either side of it are. It's the most common reason hand-keyed animation looks
/// amateur, and it isn't fixable by posing harder.
/// </summary>
public enum KeyInterpolation
{
	/// <summary>Ease out of this key and into the next. The sane default for body motion.</summary>
	Smooth,

	/// <summary>Constant speed. Right for mechanical motion, and for a straight pass between two
	/// breakdowns you intend to smooth later.</summary>
	Linear,

	/// <summary>Hold this pose until the next key, then snap. For pops, blinks, and anything that
	/// should read as instant.</summary>
	Stepped
}

public sealed class BoneKeyframe
{
	[Property] public int Frame { get; set; }
	[Property] public Transform Local { get; set; } = Transform.Zero;

	/// <summary>Governs the segment LEAVING this key, not arriving at it.</summary>
	[Property] public KeyInterpolation Interpolation { get; set; } = KeyInterpolation.Smooth;

	/// <summary>Remap a 0..1 position within this key's outgoing segment.</summary>
	public float Ease( float t ) => Interpolation switch
	{
		// Smoothstep: zero velocity at both ends, so the bone accelerates away and settles in.
		KeyInterpolation.Smooth => t * t * (3f - 2f * t),
		KeyInterpolation.Stepped => 0f,
		_ => t
	};
}

public sealed class MorphEvent
{
	[Property] public string Name { get; set; } = "morph";
	[Property, Title( "Start Frame" )] public int StartFrame { get; set; }
	[Property, Title( "End Frame" )] public int EndFrame { get; set; } = 1;
	[Property, Title( "Morph Name" )] public string MorphName { get; set; } = "";
	[Property] public float Value { get; set; } = 1f;

	public bool IsActive( float frame ) => frame >= StartFrame && frame <= EndFrame;
}
