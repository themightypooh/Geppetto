using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Effigy.Tests;

/// <summary>Small pilot-style clothing check; never writes to the podracer project.</summary>
public static class GarmentDetailDemo
{
	public static int Run( string folder )
	{
		var fbx = SboxHuman.Find();
		if ( fbx is null ) { Console.Error.WriteLine( "Set SBOX_PATH to generate the pilot clothing check." ); return 1; }
		Directory.CreateDirectory( folder );
		var (skin, rig) = SboxHuman.Load( fbx );
		var studio = new PartStudio { Rig = rig };
		var wearer = studio.Add( new WearerFeature { Name = "Wearer" } );
		wearer.LoadMesh( Encoding.UTF8.GetBytes( ObjWriter.Write( skin, "human" ) ) );
		var shirt = studio.Add( new GarmentFeature { Name = "Shirt" } );
		shirt.Length.Value = 0.8f; shirt.Sleeve.Value = 1f; shirt.Drape.Value = false;
		var jacket = studio.Add( new GarmentFeature { Name = "Cropped racing jacket" } );
		jacket.Length.Value = 0.5f; jacket.Sleeve.Value = 1f; jacket.Sleeves.Index = 2;
		jacket.Fit.Index = 2; jacket.Fabric.Index = 2; jacket.Drape.Value = false;
		var cloth = studio.Add( new FabricFeature() ); cloth.Preset.Index = Array.IndexOf( FabricMaterial.Presets, "Leather" );
		cloth.Colour.Value = "#201e25"; cloth.Bodies.BodyIds.Add( jacket.Id + "b0" );
		var result = studio.Rebuild();
		if ( result.HasErrors ) { Console.Error.WriteLine( string.Join( "; ", result.Errors.Select( e => e.Message ) ) ); return 1; }
		var baseline = studio.ToMesh();
		var pocket = studio.Add( new PocketFeature { Name = "Chest cargo" } );
		pocket.Style.Index = 1; pocket.Width.Value = 3.5f; pocket.Height.Value = 4f; pocket.Depth.Value = 0.4f;
		pocket.Bodies.BodyIds.Add( jacket.Id + "b0" );
		var studs = studio.Add( new HardwareFeature { Name = "Collar studs" } );
		studs.Layout.Index = 3; studs.Size.Value = 0.35f; studs.Spacing.Value = 0.7f;
		studs.Bodies.BodyIds.Add( jacket.Id + "b0" );
		var glow = studio.Add( new GlowFeature { Name = "Pilot display" } );
		glow.Side.Index = 1; glow.Text.Value = "PILOT 07"; glow.Width.Value = 3.2f; glow.Height.Value = 1.5f;
		glow.Bodies.BodyIds.Add( jacket.Id + "b0" );
		result = studio.Rebuild();
		if ( result.HasErrors ) { Console.Error.WriteLine( string.Join( "; ", result.Errors.Select( e => e.Message ) ) ); return 1; }
		foreach ( var body in studio.Bodies.Where( b => b.IsGarmentDetail ) )
			Report.Check( $"{body.Name} has weights", body.Mesh.IsRigged && body.Mesh.Skin.Count == body.Mesh.VertexCount );
		var assets = Path.Combine( folder, "Assets" );
		Directory.CreateDirectory( Path.Combine( assets, "materials" ) );
		void Text( string rel, string content ) => File.WriteAllText( Path.Combine( assets, rel ), content );
		void Png( string rel, byte[] bytes, int w, int h ) => File.WriteAllBytes( Path.Combine( assets, rel ), PngWriter.ToBytesRgba( bytes, w, h ) );
		var maps = FabricMaterial.Maps( "Leather", new Vec3( 0.08f, 0.07f, 0.1f ), new Vec3( 0.15f, 0.13f, 0.15f ), 6f, 0.2f, 17 );
		Png( "materials/jacket_color.png", maps.Color, FabricMaterial.Size, FabricMaterial.Size );
		Png( "materials/jacket_normal.png", maps.Normal, FabricMaterial.Size, FabricMaterial.Size );
		Png( "materials/jacket_rough.png", maps.Roughness, FabricMaterial.Size, FabricMaterial.Size );
		Text( "materials/jacket.vmat", FabricMaterial.VmatSource( "Leather", "materials/jacket_color.png", "materials/jacket_normal.png", "materials/jacket_rough.png" ) );
		foreach ( var slot in cloth.ResolvedSlots ) studio.MaterialNames[slot] = "materials/jacket.vmat";
		Write( "pilot_before", baseline );
		foreach ( var m in studio.Features.OfType<IDetailMaterials>().SelectMany( d => d.DetailMaterials ) )
		{
			var rel = "materials/" + m.Key;
			var source = DetailMaterials.HardVmat( m );
			if ( m.Kind == "glow" )
			{
				var glowMaps = DetailMaterials.GlowMaps( m );
				Png( rel + "_color.png", glowMaps.Color, glowMaps.Width, glowMaps.Height );
				Png( rel + "_mask.png", glowMaps.Mask, glowMaps.Width, glowMaps.Height );
				source = DetailMaterials.GlowVmat( m, rel + "_color.png", rel + "_mask.png", glowMaps.UScale );
			}
			Text( rel + ".vmat", source ); studio.MaterialNames[m.Slot] = rel + ".vmat";
		}
		Write( "pilot_details" );
		var document = Path.Combine( folder, "pilot_details.effigy" );
		File.WriteAllText( document, StudioDocument.Write( studio ) );
		ImportSidecar.Save( studio, document );
		Console.WriteLine( $"Pilot-style geometry and material check: {folder}" );
		return Report.Failed == 0 ? 0 : 1;

		void Write( string name, PolyMesh savedMesh = null )
		{
			var mesh = savedMesh ?? studio.ToMesh();
			File.WriteAllText( Path.Combine( folder, name + ".obj" ), ObjWriter.Write( mesh, name,
				materialName: slot => studio.MaterialNames.TryGetValue( slot, out var path ) ? path : "material_" + slot ) );
		}
	}
}
