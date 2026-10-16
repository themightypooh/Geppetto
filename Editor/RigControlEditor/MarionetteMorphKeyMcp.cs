using Editor;
using Editor.Mcp;
using Effigy;
using Sandbox;
using System;
using System.ComponentModel;
using System.Linq;

namespace Marionette.Tools;

public static partial class MarionetteMcpTools
{
	/// <summary>
	/// Key a shape key's weight over time on a saved clip: a Morph track (Morph Curves in the
	/// timeline), which the player plays and the export writes as a flex channel. Keys are
	/// frame:weight[:ease] - 0 the base shape, 1 the full key. The kernel's ClipKeys.Morph checks the
	/// keys (and runs headless as `effigy.sh key clip morph=Name "..."`); this writes them through
	/// RigAnimDocument.SetMorphKey, the call the timeline makes.
	/// </summary>
	[McpTool( "marionette_key_morph" )]
	public static McpResult KeyMorph(
		[Description( "Asset path of the clip" )] string path,
		[Description( "The shape key (morph) name exactly as the model has it, e.g. sac_swell" )] string morph,
		[Description( "Keys, ';' between them: frame:weight[:ease] - weight 0 (base) to 1 (full); ease Smooth/Linear/Stepped/EaseIn/EaseOut/Cubic/Monotone. e.g. 0:0;12:1:EaseOut;24:0" )] string keys,
		[Description( "Ease for keys that do not name one" )] string ease = "Smooth",
		[Description( "Write the result to disk; false only reports what would change" )] bool save = true )
	{
		if ( Load( path, out var asset, out var doc ) is { } error )
			return McpResult.Text( error );

		if ( !Enum.TryParse<RigEase>( (ease ?? "").Replace( " ", "" ), true, out var defaultEase ) )
			return McpResult.Text( $"ease '{ease}' is not Smooth, Linear, Stepped, EaseIn, EaseOut, Cubic or Monotone." );

		if ( ClipKeys.ParseMorph( keys, defaultEase, out var parseError ) is not { } list )
			return McpResult.Text( $"keys: {parseError}" );

		// The kernel's checks (name, weights outside 0-1, a morph event over the same frames, a name
		// the model lacks), run on a scratch clip carrying this one's morph events.
		var scratch = new RigAnimFile();

		foreach ( var e in doc.MorphEvents.Where( e => e is not null ) )
		{
			scratch.MorphEvents.Add( new RigMorphEvent
			{
				Morph = string.IsNullOrWhiteSpace( e.MorphName ) ? e.Name : e.MorphName,
				Start = e.StartFrame,
				End = e.EndFrame,
				Value = e.Value,
			} );
		}

		var model = doc.SourceModel;
		var shapes = model is null ? null : Enumerable.Range( 0, model.MorphCount ).Select( model.GetMorphName ).ToList();
		var check = ClipKeys.Morph( scratch, morph, list, shapes );

		if ( !check.Ok )
			return McpResult.Text( check.Error );

		var name = morph.Trim();

		foreach ( var k in list )
		{
			doc.SetMorphKey( name, k.Frame, k.Weight );

			if ( doc.FindMorphTrack( name )?.Keyframes.FirstOrDefault( x => x.Frame == k.Frame ) is { } key )
				key.Interpolation = (KeyInterpolation)(int)k.Ease;
		}

		if ( save )
			asset.SaveToDisk( doc );

		var notes = check.Notes.ToList();

		if ( shapes is null )
			notes.Add( "the clip has no model, so the name was not checked against its shape keys" );

		return McpResult.Text( $"keyed {list.Count} weight(s) on morph {name}, frames {list.First().Frame}-{list.Last().Frame}"
			+ (notes.Count > 0 ? "; " + string.Join( "; ", notes ) : "")
			+ (save ? $"; saved {asset.Path}" : " (not saved)") );
	}
}
