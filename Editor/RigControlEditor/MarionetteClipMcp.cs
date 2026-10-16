using Editor;
using Editor.Mcp;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;

namespace Marionette.Tools;

/// <summary>
/// A clip on disk for an agent: read what is in it, and run the timeline's key tools and the
/// secondary bake on it without hand-writing .riganim JSON. Every edit is the same code the
/// Marionette window runs (RigKeyOps, RigSecondaryBake), so an agent and a person get the same
/// result from the same request.
/// </summary>
public static partial class MarionetteMcpTools
{
	/// <summary>
	/// Describe a .riganim: model, rig, frame rate, keyed span, every track (kind, key count, first
	/// and last frame, interpolation modes), marks, morph events, and the rig's IK constraints.
	/// </summary>
	[McpTool( "marionette_clip_info", Hints = McpToolHints.ReadOnly )]
	public static McpResult ClipInfo(
		[Description( "Asset path of the clip, e.g. animations/rigging/haul.riganim" )] string path )
	{
		if ( Load( path, out var asset, out var doc ) is { } error )
			return McpResult.Text( error );

		var text = new StringBuilder();
		text.AppendLine( $"{asset.Path}" );
		text.AppendLine( $"model {doc.SourceModel?.ResourcePath ?? "(none)"}, rig {doc.RigAsset?.ResourcePath ?? "(none)"}, {doc.AnimationSpeed} fps, FrameCount {doc.FrameCount}, keyed to frame {LastKey( doc )}" );

		text.AppendLine( $"tracks ({doc.BoneTracks.Count}):" );

		foreach ( var track in doc.BoneTracks.OrderBy( t => t.Target ).ThenBy( t => t.BoneName ) )
		{
			if ( track.Keyframes.Count == 0 )
			{
				text.AppendLine( $"  {track.Target,-6} {track.BoneName}: no keys" );
				continue;
			}

			var modes = string.Join( "/", track.Keyframes.Select( k => k.Interpolation ).Distinct() );
			text.AppendLine( $"  {track.Target,-6} {track.BoneName}: {track.Keyframes.Count} keys, {track.Keyframes.Min( k => k.Frame )}-{track.Keyframes.Max( k => k.Frame )}, {modes}" );
		}

		if ( doc.Marks.Count > 0 )
			text.AppendLine( "marks: " + string.Join( ", ", doc.MarksInOrder.Select( m => $"{m.Name}@{m.Frame}" ) ) );

		if ( doc.MorphEvents.Count > 0 )
		{
			text.AppendLine( "morphs: " + string.Join( ", ", doc.MorphEvents.Where( m => m is not null )
				.Select( m => $"{(string.IsNullOrWhiteSpace( m.MorphName ) ? m.Name : m.MorphName)}={m.Value:0.##} {m.StartFrame}-{m.EndFrame} fade {m.FadeIn}/{m.FadeOut}" ) ) );
		}

		if ( doc.RigAsset?.IkConstraints is { Count: > 0 } iks )
		{
			text.AppendLine( "IK: " + string.Join( "; ", iks.Where( c => c is not null ).Select( c =>
				$"{c.Name} -> {c.TargetBone}, chain {c.ChainLength}{(c.ChainLength >= 3 ? $" {c.Solver}" : "")}, {(c.Live ? "live" : "baked")}{(c.Enabled ? "" : ", disabled")}" ) ) );
		}

		return McpResult.Text( text.ToString() );
	}

	/// <summary>
	/// Edit a .riganim with Marionette's key tools and save it. Operations:
	/// reverse (play the chosen keys backwards over their span), retime (scale their timing by
	/// factor around the first chosen key), offset_chain (shift each chosen bone by frames × its
	/// depth below the top chosen bone - overlap), shift (move them all by frames), reduce (remove
	/// keys the curve does not need), bake_secondary (spring lag/swing on the first bone in bones
	/// and everything below it, written as keys; preset 0-3 or your own stiffness/damping/weight/
	/// max_angle), wave (a travelling sine down the first bone's chain: wave_preset sway/writhe/
	/// wriggle/undulate, amplitude, period, wavelength, each chain its own phase), make_loop (every
	/// track ends on its first pose at the last keyed frame). Keys are chosen by bones (comma list, prefix* wildcards, empty = every bone
	/// track) and frame range. If the clip is open in Marionette, reopen it to see the result.
	/// </summary>
	[McpTool( "marionette_clip_edit" )]
	public static McpResult ClipEdit(
		[Description( "Asset path of the clip" )] string path,
		[Description( "reverse | retime | offset_chain | shift | reduce | bake_secondary | wave | make_loop | layer" )] string operation,
		[Description( "Bone names, comma separated; 'cable1_*' matches a prefix; empty for every bone track" )] string bones = "",
		[Description( "First frame of keys to touch" )] int from = 0,
		[Description( "Last frame of keys to touch (-1 = end)" )] int to = -1,
		[Description( "retime: time scale (2 = twice as long)" )] float factor = 1f,
		[Description( "offset_chain: frames per bone of depth; shift: frames to move (negative = earlier)" )] int frames = 2,
		[Description( "bake_secondary: 0 loose cable, 1 heavy flesh, 2 springy, 3 stiff, 4 rope (keeps length, drapes; collides with the rig's Soft Colliders/floor like every preset)" )] int preset = 0,
		[Description( "bake_secondary: run the root's first-child line as a rope with the chosen numbers (preset 4 always is)" )] bool rope = false,
		[Description( "bake_secondary rope: a bone on the line whose head holds to the clip, e.g. the cable's IK hand (its IK chain is baked too)" )] string pin = "",
		[Description( "bake_secondary: override spring stiffness (0 = preset)" )] float stiffness = 0f,
		[Description( "bake_secondary: override damping, fraction of speed kept per second; smaller = more damped (0 = preset)" )] float damping = 0f,
		[Description( "bake_secondary: override gravity weight (-1 = preset); layer: layer weight 0-1 (-1 = 1)" )] float weight = -1f,
		[Description( "bake_secondary: override cone limit in degrees (0 = preset)" )] float max_angle = 0f,
		[Description( "bake_secondary / wave: loop seamlessly (warm up the springs / fit the wave's period); layer: repeat the layer over the whole clip" )] bool loop = false,
		[Description( "wave: sway, writhe, wriggle or undulate" )] string wave_preset = "writhe",
		[Description( "wave: override swing in degrees (0 = preset)" )] float amplitude = 0f,
		[Description( "wave: override frames per swing (0 = preset)" )] float period = 0f,
		[Description( "wave: override bones per wavelength (0 = preset)" )] float wavelength = 0f,
		[Description( "layer: asset path of the clip to sum on top; bones = mask (empty = all its tracks)" )] string layer_path = "",
		[Description( "layer: additive (motion away from its first key, added) or overlay (replaces by weight)" )] string layer_mode = "additive",
		[Description( "layer: base frame the layer's frame 0 plays at" )] int offset = 0,
		[Description( "layer: playback rate of the layer (2 = twice as fast)" )] float rate = 1f,
		[Description( "Write the result to disk; false only reports what would change" )] bool save = true )
	{
		if ( Load( path, out var asset, out var doc ) is { } error )
			return McpResult.Text( error );

		var names = (bones ?? "").Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		var last = to < 0 ? int.MaxValue : to;

		bool Wanted( BoneTrack t ) => t.Target == TrackTarget.Bone && (names.Length == 0 || names.Any( n =>
			n.EndsWith( '*' ) ? t.BoneName.StartsWith( n[..^1], StringComparison.Ordinal ) : t.BoneName == n ));

		var keys = doc.BoneTracks.Where( Wanted )
			.SelectMany( t => t.Keyframes.Where( k => k.Frame >= from && k.Frame <= last ).Select( k => (t, k) ) )
			.ToList();

		string message;

		switch ( (operation ?? "").Trim().ToLowerInvariant() )
		{
			case "reverse":
				message = $"reversed {RigKeyOps.Reverse( keys )} key(s)";
				break;

			case "retime":
				message = $"retimed ×{factor:0.###}: {RigKeyOps.Retime( keys, factor )} key(s) moved";
				break;

			case "shift":
				message = $"shifted {RigKeyOps.Offset( keys, _ => frames )} key(s) by {frames}";
				break;

			case "offset_chain":
			{
				var depth = keys.Select( k => k.t.BoneName ).Distinct()
					.ToDictionary( n => n, n => Depth( doc.SourceModel, n ) );
				var top = depth.Values.Where( d => d >= 0 ).DefaultIfEmpty( 0 ).Min();
				var moved = RigKeyOps.Offset( keys, t => depth.TryGetValue( t.BoneName, out var d ) && d >= 0 ? (d - top) * frames : 0 );
				message = $"offset {moved} key(s), {frames} frame(s) per bone of depth: "
					+ string.Join( ", ", depth.OrderBy( p => p.Value ).Select( p => $"{p.Key}+{Math.Max( p.Value - top, 0 ) * frames}" ) );
				break;
			}

			case "reduce":
				message = $"removed {RigKeyOps.Reduce( keys )} key(s)";
				break;

			case "bake_secondary":
			{
				if ( names.Length == 0 || names[0].EndsWith( '*' ) )
					return McpResult.Text( "bake_secondary needs the root bone's exact name first in bones." );

				var picked = RigSecondaryBake.Presets[Math.Clamp( preset, 0, RigSecondaryBake.Presets.Length - 1 )];
				var chosen = picked.ToSoftBone();

				if ( stiffness > 0f ) chosen.Stiffness = stiffness;
				if ( damping > 0f ) chosen.Damping = damping;
				if ( weight >= 0f ) chosen.Weight = weight;
				if ( max_angle > 0f ) chosen.MaxAngle = max_angle;

				var result = RigSecondaryBake.Bake( doc, names[0], chosen, loop, rope: rope || picked.Rope, pin: string.IsNullOrWhiteSpace( pin ) ? null : pin.Trim() );

				if ( !result.Ok )
					return McpResult.Text( result.Message );

				message = result.Message;
				break;
			}

			case "wave":
			{
				if ( names.Length == 0 || names[0].EndsWith( '*' ) )
					return McpResult.Text( "wave needs the chain's root bone's exact name first in bones." );

				var settings = ChainWave.FindPreset( wave_preset ) ?? ChainWave.FindPreset( "writhe" );

				if ( amplitude > 0f ) settings.Amplitude = amplitude;
				if ( period > 0f ) settings.Period = period;
				if ( wavelength > 0f ) settings.Wavelength = wavelength;

				settings.Offset = ChainWave.OffsetFor( names[0], settings.Period );

				var result = RigSecondaryBake.Wave( doc, names[0], settings, loop );

				if ( !result.Ok )
					return McpResult.Text( result.Message );

				message = result.Message;
				break;
			}

			case "layer":
			{
				if ( Load( layer_path, out _, out var top ) is { } layerError )
					return McpResult.Text( $"layer_path: {layerError}" );

				var result = RigClipLayer.MergeLayer( doc, top, new RigClipLayer.Settings
				{
					Mode = (layer_mode ?? "").Trim().ToLowerInvariant() == "overlay" ? RigAnimMode.Overlay : RigAnimMode.Additive,
					Weight = weight < 0f ? 1f : weight,
					Offset = offset,
					Rate = rate,
					Loop = loop,
					Mask = names.ToList(),
				}, RigClipLayer.BindOf( doc.SourceModel ) );

				if ( !result.Ok )
					return McpResult.Text( $"layer: {result.Error}" );

				message = result.ToString();
				break;
			}

			case "make_loop":
				message = $"closed {RigKeyOps.MakeLoop( doc.BoneTracks, LastKey( doc ) )} track(s) at frame {LastKey( doc )}";
				break;

			default:
				return McpResult.Text( $"Unknown operation '{operation}'. Use reverse, retime, offset_chain, shift, reduce, bake_secondary, wave, make_loop or layer." );
		}

		if ( save )
			asset.SaveToDisk( doc );

		return McpResult.Text( $"{message}{(save ? $"; saved {asset.Path}" : " (not saved)")}" );
	}

	static string Load( string path, out Asset asset, out RigAnimDocument doc )
	{
		doc = null;
		asset = string.IsNullOrWhiteSpace( path ) ? null : AssetSystem.FindByPath( path.Trim() );

		if ( asset is null )
			return $"No asset at '{path}'.";

		if ( !asset.TryLoadResource<RigAnimDocument>( out doc ) || doc is null )
			return $"'{path}' is not a Marionette clip (.riganim).";

		return null;
	}

	static int LastKey( RigAnimDocument doc ) =>
		doc.BoneTracks.SelectMany( t => t.Keyframes ).Select( k => k.Frame ).DefaultIfEmpty( 0 ).Max();

	static int Depth( Model model, string bone )
	{
		var b = model?.Bones?.AllBones?.FirstOrDefault( x => x.Name == bone );

		if ( b is null )
			return -1;

		var depth = 0;

		for ( var up = b.Parent; up is not null; up = up.Parent )
			depth++;

		return depth;
	}
}
