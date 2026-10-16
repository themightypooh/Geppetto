using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Marionette.EditorTools;

public sealed partial class EffigyWindow
{
	private void AddGarmentDetailTools( EffigyStage stage )
	{
		void Tool( string label, string tip, ToolKind kind ) => stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Shell, Label = label, Tip = tip,
			Clicked = () => AddFeature( NewFeature( kind, -1 ) ),
		} );
		Tool( "Pocket", "Sew a patch, cargo or zipper pocket onto the selected garment. Choose a body part and side; it follows the cloth when the fit changes.", ToolKind.Pocket );
		Tool( "Hardware", "Add studs, spikes, eyelets, safety pins, chains, straps, buckles, ports or cables. Place rows on a body part or around an opening.", ToolKind.Hardware );
		Tool( "Wear", "Fade or dirty a body part or hem, or cut real rips and holes with loose threads. Add cuts before Fur; small cuts need a fine mesh.", ToolKind.Wear );
		Tool( "Glow", "Sew on an LED panel, light strip or EL wire, or add an armour plate or bright tape. LED text and patterns can scroll in game without a script.", ToolKind.Glow );
	}

	/// <summary>Only owned slots are rebound; deleting or suppressing a detail unbinds its files.</summary>
	private void SyncDetailMaterials()
	{
		if ( _studio is null ) return;
		var live = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var feature in _studio.Features.OfType<GarmentDetailFeature>() )
		{
			if ( feature.Suppressed || feature.Error is not null ) continue;
			foreach ( var m in feature.DetailMaterials )
			{
				try
				{
					var tag = new string( m.Key.Select( c => char.IsLetterOrDigit( c ) ? c : '_' ).ToArray() );
					var folder = EffigyAssetFolder.ResolveAssetFolder( "models/effigy/details" );
					Directory.CreateDirectory( folder );
					var rel = $"models/effigy/details/{tag}";
					var files = new List<string>();
					bool Write( string suffix, byte[] bytes )
					{
						var path = rel + suffix;
						if ( !WriteIfChanged( Path.Combine( folder, tag + suffix ), bytes ) ) return false;
						files.Add( path );
						return true;
					}
					string source;
					if ( m.Kind == "glow" )
					{
						var recipeFile = Path.Combine( folder, tag + ".recipe" );
						var uScale = 1f;
						// Maps are small; the recipe keeps unrelated model edits from rebaking them.
						if ( !File.Exists( recipeFile ) || File.ReadAllText( recipeFile ) != m.Recipe
							|| !File.Exists( Path.Combine( folder, tag + "_color.png" ) ) || !File.Exists( Path.Combine( folder, tag + "_mask.png" ) ) )
						{
							var maps = DetailMaterials.GlowMaps( m );
							Write( "_color.png", PngWriter.ToBytesRgba( maps.Color, maps.Width, maps.Height ) );
							Write( "_mask.png", PngWriter.ToBytesRgba( maps.Mask, maps.Width, maps.Height ) );
							uScale = maps.UScale;
							File.WriteAllText( Path.Combine( folder, tag + ".scale" ), uScale.ToString( System.Globalization.CultureInfo.InvariantCulture ) );
							File.WriteAllText( recipeFile, m.Recipe );
						}
						else if ( File.Exists( Path.Combine( folder, tag + ".scale" ) ) )
							float.TryParse( File.ReadAllText( Path.Combine( folder, tag + ".scale" ) ), System.Globalization.NumberStyles.Float,
								System.Globalization.CultureInfo.InvariantCulture, out uScale );
						source = DetailMaterials.GlowVmat( m, rel + "_color.png", rel + "_mask.png", uScale );
					}
					else if ( m.Kind == "worn" || m.Kind == "dirt" )
					{
						var fabric = _studio.Features.OfType<FabricFeature>().LastOrDefault( f => !f.Suppressed && f.Error is null && f.ResolvedSlots.Contains( m.SourceSlot ) );
						var colour = FurMaterial.ParseHex( fabric?.Colour.Value ?? "#5a6e8c", new Vec3( 0.35f, 0.43f, 0.55f ) );
						var accent = FurMaterial.ParseHex( fabric?.Accent.Value ?? "#e8e2d4", new Vec3( 0.91f, 0.89f, 0.83f ) );
						Vec3 Tint( Vec3 c ) => m.Kind == "worn" ? DetailMaterials.Worn( c ) : DetailMaterials.Dirty( c );
						var preset = fabric?.PresetName ?? "Jersey";
						var recipe = m.Recipe + FormattableString.Invariant( $"|{preset}|{colour}|{accent}|{fabric?.Scale.Clamped}|{fabric?.Wear.Clamped}" );
						var recipeFile = Path.Combine( folder, tag + ".recipe" );
						if ( !File.Exists( recipeFile ) || File.ReadAllText( recipeFile ) != recipe
							|| new[] { "_color.png", "_normal.png", "_rough.png" }.Any( s => !File.Exists( Path.Combine( folder, tag + s ) ) ) )
						{
							var maps = FabricMaterial.Maps( preset, Tint( colour ), Tint( accent ), fabric?.Scale.Clamped ?? 6f, 0.8f,
								fabric is null ? 17 : fabric.Id.GetHashCode() & 0xffff );
							Write( "_color.png", PngWriter.ToBytesRgba( maps.Color, FabricMaterial.Size, FabricMaterial.Size ) );
							Write( "_normal.png", PngWriter.ToBytesRgba( maps.Normal, FabricMaterial.Size, FabricMaterial.Size ) );
							Write( "_rough.png", PngWriter.ToBytesRgba( maps.Roughness, FabricMaterial.Size, FabricMaterial.Size ) );
							File.WriteAllText( recipeFile, recipe );
						}
						source = FabricMaterial.VmatSource( preset, rel + "_color.png", rel + "_normal.png", rel + "_rough.png" );
					}
					else source = DetailMaterials.HardVmat( m );
					Write( ".vmat", Encoding.UTF8.GetBytes( source ) );
					foreach ( var path in files.OrderBy( p => p.EndsWith( ".vmat" ) ? 1 : 0 ) )
					{
						var asset = AssetSystem.FindByPath( path ) ?? AssetSystem.RegisterFile( Path.Combine( folder, Path.GetFileName( path ) ) );
						asset?.Compile( true );
					}
					live.Add( rel + ".vmat" );
					if ( !_studio.MaterialNames.TryGetValue( m.Slot, out var bound ) || bound != rel + ".vmat" )
					{
						_studio.MaterialNames[m.Slot] = rel + ".vmat";
						_livePreview = null;
					}
				}
				catch ( Exception e )
				{
					ReportFailure( $"Can't write {feature.Name ?? feature.TypeName}'s detail material: {e.Message}" );
				}
			}
		}
		foreach ( var kv in _studio.MaterialNames.ToList() )
			if ( (kv.Value ?? "").Replace( '\\', '/' ).StartsWith( "models/effigy/details/", StringComparison.OrdinalIgnoreCase ) && !live.Contains( kv.Value ) )
				_studio.MaterialNames.Remove( kv.Key );
	}
}
