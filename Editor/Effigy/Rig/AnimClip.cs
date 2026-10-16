using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A baked animation: one local-to-parent pose per bone per frame.
///
/// BAKED, NOT KEYED, and that is the whole design rather than a simplification. Every tool that
/// authors animation against this kernel keeps its own curve model — Marionette's `.riganim` has
/// keyframes, five easing modes and IK solves layered over each other — and none of that survives
/// a model compiler anyway: ModelDoc resamples to a fixed rate on import regardless of what the
/// source file thought it was doing. Sampling to a flat frame list at the boundary means the
/// kernel never has to know what a curve is, and the authoring side keeps exactly one job — say
/// where every bone is on frame N.
///
/// POSES ARE LOCAL TO THE PARENT, matching <see cref="Bone.Local"/> and every skeletal format
/// worth naming. A world-space pose list would need the skeleton to mean anything at all, and
/// would come apart the moment a parent moved — which is the normal case, not the edge one.
///
/// A BONE THAT IS NEVER POSED STILL GETS A FRAME. There is no "no value here" entry, because a
/// hole would have to be filled by whoever reads it and every reader would fill it differently.
/// The sampler writes the bind pose for an untouched bone, which is what the animator saw in the
/// viewport and therefore what they meant.
/// </summary>
public sealed class AnimClip
{
	/// <summary>What the clip is called inside the compiled model — the name AnimGraph and
	/// <c>SetAnimParameter</c> end up seeing, so it is not decorative.</summary>
	public string Name = "anim";

	/// <summary>Frames per second the frames below are sampled at.</summary>
	public float FrameRate = 30f;

	public bool Looping;

	/// <summary>Frames[frame][bone], the bone index being its position in
	/// <see cref="Skeleton.Bones"/>. Every frame is the same length as the skeleton.</summary>
	public List<Xform[]> Frames = new();

	public int FrameCount => Frames.Count;

	/// <summary>
	/// Scales[frame][bone]: each bone's uniform scale, or null - the usual case - when nothing in
	/// the clip scales. Kept apart from <see cref="Frames"/> because a pose's rotation goes into the
	/// file as a quaternion read off the basis, and a scale folded into that basis would come out as
	/// a skew. Uniform because a Source 2 bone scales in one number.
	/// </summary>
	public List<float[]> Scales;

	/// <summary>The bones whose scale is anything but 1 on some frame - the ones a scale channel is
	/// worth writing for.</summary>
	public bool Scaled( int bone )
	{
		if ( Scales is null )
			return false;

		foreach ( var frame in Scales )
		{
			if ( frame is not null && MathF.Abs( frame[bone] - 1f ) > 1e-5f )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Length in seconds.
	///
	/// N frames span N-1 intervals, not N — a two-frame clip at 30fps lasts a thirtieth of a
	/// second, not a fifteenth. Getting this wrong stretches every clip by one frame, which reads
	/// as animation that drifts slowly out of sync with anything it was timed against rather than
	/// as an obvious bug.
	/// </summary>
	public float Duration => FrameCount <= 1 ? 0f : (FrameCount - 1) / MathF.Max( FrameRate, 0.0001f );

	/// <summary>The time in seconds at which a given frame lands.</summary>
	public float TimeOf( int frame ) => frame / MathF.Max( FrameRate, 0.0001f );

	public void AddFrame( Xform[] pose )
	{
		if ( pose is null )
			throw new ArgumentNullException( nameof( pose ) );

		Frames.Add( pose );
		Scales?.Add( null );
	}

	/// <summary>A frame with its bones' uniform scales. The first scaled frame starts the
	/// <see cref="Scales"/> list, back-filling earlier frames at 1.</summary>
	public void AddFrame( Xform[] pose, float[] scales )
	{
		if ( pose is null )
			throw new ArgumentNullException( nameof( pose ) );

		if ( scales is null )
		{
			AddFrame( pose );
			return;
		}

		if ( Scales is null )
		{
			Scales = new List<float[]>();

			for ( var f = 0; f < Frames.Count; f++ )
				Scales.Add( null );
		}

		Frames.Add( pose );
		Scales.Add( scales );
	}

	/// <summary>A bone's scale on a frame - 1 where the clip does not scale.</summary>
	public float ScaleAt( int frame, int bone ) => Scales?[frame] is { } s ? s[bone] : 1f;

	/// <summary>
	/// Morph (flex controller) values per frame, by morph name - one value per frame in
	/// <see cref="Frames"/>, 0 where the clip does not drive it. Empty for a clip that drives none,
	/// which then exports exactly as before. Written by DmxAnimWriter as flex channels.
	/// </summary>
	public SortedDictionary<string, List<float>> Morphs = new( StringComparer.Ordinal );

	/// <summary>The morph values of the frame just added with AddFrame. A morph first named here is
	/// back-filled with 0 for the frames before it; one not named is 0 on this frame.</summary>
	public void SetMorphs( IReadOnlyDictionary<string, float> values )
	{
		var frame = FrameCount - 1;

		if ( frame < 0 )
			return;

		foreach ( var (name, _) in values ?? new Dictionary<string, float>() )
		{
			if ( !string.IsNullOrWhiteSpace( name ) && !Morphs.ContainsKey( name ) )
				Morphs[name] = new List<float>();
		}

		foreach ( var (name, list) in Morphs )
		{
			while ( list.Count < frame )
				list.Add( 0f );

			var value = values is not null && values.TryGetValue( name, out var v ) ? v : 0f;

			if ( list.Count == frame )
				list.Add( value );
			else
				list[frame] = value;
		}
	}

	/// <summary>A morph's value at a frame; 0 where it is not driven.</summary>
	public float MorphAt( string name, int frame ) =>
		Morphs.TryGetValue( name, out var list ) && frame >= 0 && frame < list.Count ? list[frame] : 0f;

	/// <summary>
	/// Check the clip against the skeleton it claims to animate, and say what is wrong rather than
	/// leaving it to be discovered as a model that will not load.
	///
	/// Returns null when the clip is writable. A ragged frame list is the mistake worth catching
	/// here: it comes from a sampler that grew a bone mid-loop, and downstream it is an index walk
	/// off the end of an array in the middle of writing a file.
	/// </summary>
	public string Validate( Skeleton skeleton )
	{
		if ( skeleton is null || skeleton.Count == 0 )
			return "a clip needs a skeleton with at least one bone";

		if ( FrameCount == 0 )
			return "a clip needs at least one frame";

		if ( FrameRate <= 0f )
			return $"frame rate must be positive, not {FrameRate}";

		for ( var f = 0; f < Frames.Count; f++ )
		{
			if ( Frames[f] is null )
				return $"frame {f} is null";

			if ( Frames[f].Length != skeleton.Count )
				return $"frame {f} has {Frames[f].Length} pose(s) for a skeleton of {skeleton.Count} bone(s)";

			if ( Scales?[f] is { } scales && scales.Length != skeleton.Count )
				return $"frame {f} has {scales.Length} scale(s) for a skeleton of {skeleton.Count} bone(s)";
		}

		return null;
	}
}
