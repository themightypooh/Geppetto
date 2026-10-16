using Effigy;
using Marionette.EditorTools;
using Sandbox;
using System;
using System.Linq;

using Skeleton = Effigy.Skeleton;

namespace Marionette.Tools;

/// <summary>
/// Extract Root Motion on the open clip: the hips' travel moved onto the skeleton's top bone, every
/// pose kept, and the clip marked (RigAnimDocument.RootMotion) so the player, the export's ModelDoc
/// ExtractMotion and Effigy's sim read it. The maths is the kernel's (Effigy.RootMotion.Plan) on the
/// clip sampled the way the export samples it, live IK included.
/// </summary>
internal static class RigRootMotionTool
{
	public sealed class Result
	{
		public bool Ok;
		public string Message;
	}

	/// <summary>The clip's root motion as the kernel's settings, or null when it has none - what the
	/// export hands VmdlAnimation.ClipEntry.</summary>
	public static RootMotionSettings ToKernel( RigRootMotion motion ) =>
		motion is not { Enabled: true } || string.IsNullOrWhiteSpace( motion.Bone ) ? null : new RootMotionSettings
		{
			Bone = motion.Bone,
			Source = motion.Source ?? "",
			Mode = (Effigy.RootMotionMode)(int)motion.Mode,
			Linear = motion.Linear,
		};

	/// <summary>The skeleton's top bone above <paramref name="bone"/> - what carries root motion.</summary>
	public static string TopBoneAbove( Skeleton skeleton, string bone )
	{
		var b = skeleton.IndexOf( bone ?? "" );

		if ( b < 0 )
			return null;

		while ( skeleton.Bones[b].Parent >= 0 )
			b = skeleton.Bones[b].Parent;

		return skeleton.Bones[b].Name;
	}

	/// <summary>Extract <paramref name="source"/>'s travel onto the top bone above it. Nothing
	/// changes on a refusal; the message says why and what to do.</summary>
	public static Result Extract( RigAnimDocument doc, string source, RootMotionMode mode, bool linear )
	{
		if ( doc?.SourceModel is null )
			return Fail( "Set a Model in the Scene tab first - root motion needs the skeleton." );

		var skeleton = RigAnimExport.SkeletonFromModel( doc.SourceModel );

		if ( TopBoneAbove( skeleton, source ) is not { } root )
			return Fail( $"The model has no bone '{source}'." );

		// Citizen's layout: the pelvis is itself a top bone and root_IK sits beside it.
		if ( root == source )
			root = skeleton.Bones.Where( b => b.Parent < 0 && b.Name != source && b.Name.Contains( "root", StringComparison.OrdinalIgnoreCase ) ).Select( b => b.Name ).FirstOrDefault();

		if ( root is null )
			return Fail( $"'{source}' is the model's top bone and there is no root bone beside it to carry its travel - add a root bone above it in Effigy's Rig tab." );

		var span = EffigyAnimExport.FrameSpan( doc );

		if ( span < 1 )
			return Fail( "The clip has no motion - key at least two frames first." );

		var clip = EffigyAnimExport.ToClip( doc, skeleton, "root", false, out _, out _ );
		var settings = new RootMotionSettings { Bone = root, Source = source, Mode = (Effigy.RootMotionMode)(int)mode, Linear = linear };
		var plan = RootMotion.Plan( clip.Frames.Take( span + 1 ).ToList(), skeleton, settings, out var keyed );

		if ( !plan.Ok )
			return Fail( char.ToUpperInvariant( plan.Error[0] ) + plan.Error[1..] + "." );

		var keys = 0;

		foreach ( var (bone, locals) in keyed )
		{
			var track = doc.GetOrAddTrack( skeleton.Bones[bone].Name );

			// The clip's own scale is kept: the sampled pose is rotation and position only.
			var hadKeys = track.Keyframes.Count > 0;
			var scales = Enumerable.Range( 0, span + 1 ).Select( f => hadKeys ? track.Evaluate( f ).Scale : Vector3.One ).ToArray();

			keys += RigKeyOps.Bake( track, 0, span, f => RigSecondaryBake.ToTransform( locals[f], scales[f] ), mode: KeyInterpolation.Linear );
			keys -= RigKeyOps.Reduce( track.Keyframes.Where( k => k.Frame <= span ).Select( k => (track, k) ).ToList(), 0.01f, 0.1f );
		}

		doc.RootMotion = new RigRootMotion { Enabled = true, Bone = root, Source = source, Mode = mode, Linear = linear };

		return new Result
		{
			Ok = true,
			Message = $"root motion: {plan.Travel.Length:0.#} units of travel{(settings.Turns ? $" and a {plan.Yaw:0.#}° turn" : "")} moved from '{source}' onto '{root}', {keys} key(s), frames 0-{span}. "
				+ "Set the player's Root Motion to Move GameObject (or Report Only) to play it in place; the export adds ModelDoc's Extract Motion.",
		};
	}

	static Result Fail( string message ) => new() { Ok = false, Message = message };
}
