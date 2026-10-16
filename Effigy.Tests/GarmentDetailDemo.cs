using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Effigy.Tests;

/// <summary>Small pilot-style clothing check; never writes to the podracer project.</summary>
public static class GarmentDetailDemo
{
	/// <summary>A punk outfit on the s&amp;box human wearing every garment detail, for the render sheet.</summary>
	public static int RunPunk( string folder )
	{
		var fbx = SboxHuman.Find();
		if ( fbx is null ) { Console.Error.WriteLine( "Set SBOX_PATH to dress the punk figure." ); return 1; }
		Directory.CreateDirectory( folder );
		var (skin, rig) = SboxHuman.Load( fbx );
		var studio = new PartStudio { Rig = rig };
		var wearer = studio.Add( new WearerFeature { Name = "Wearer" } );
		wearer.LoadMesh( Encoding.UTF8.GetBytes( ObjWriter.Write( skin, "human" ) ) );
		GarmentFeature Garment( string name, string recipe, float length, float sleeve, int fit, bool drape )
		{
			var g = studio.Add( new GarmentFeature { Name = name } );
			g.Recipe.Index = Array.IndexOf( GarmentRecipe.Names, recipe );
			g.Length.Value = length; g.Sleeve.Value = sleeve; g.Fit.Index = fit; g.Drape.Value = drape;
			return g;
		}
		var only = Environment.GetEnvironmentVariable( "PUNK_ONLY" );
		var fitOverride = Environment.GetEnvironmentVariable( "PUNK_FIT" );
		var drapeOn = Environment.GetEnvironmentVariable( "PUNK_DRAPE" ) == "1";
		var tee = only is null || only == "tee" ? Garment( "Band tee", "T-shirt", 0.7f, 0.3f, 1, drapeOn ) : null;
		var jeans = only is null || only == "jeans" ? Garment( "Jeans", "Trousers", 1f, 0f, 1, drapeOn ) : null;
		var jacket = only is null || only == "jacket" ? Garment( "Biker jacket", "Long sleeve", 0.5f, 1f, fitOverride is null ? 2 : int.Parse( fitOverride ), drapeOn ) : null;
		if ( only is not null ) { tee ??= jacket ?? jeans; jeans ??= tee; jacket ??= tee; }
		jacket.Fabric.Index = 2;
		FabricFeature Cloth( GarmentFeature on, string preset, string colour )
		{
			var f = studio.Add( new FabricFeature() );
			f.Preset.Index = Array.IndexOf( FabricMaterial.Presets, preset ); f.Colour.Value = colour;
			f.Bodies.BodyIds.Add( on.Id + "b0" );
			return f;
		}
		var fabrics = new[] { Cloth( tee, "Jersey", "#7a1020" ), Cloth( jeans, "Denim", "#1c2738" ), Cloth( jacket, "Leather", "#141218" ) };
		var result = studio.Rebuild();
		if ( result.HasErrors ) { Console.Error.WriteLine( string.Join( "; ", result.Errors.Select( e => e.Message ) ) ); return 1; }

		foreach ( var b in studio.Bodies.Where( b => b.IsGarment ) )
		{
			var sheet = DetailEdges.Sheet( b.Mesh );
			var loops = GarmentTrim.OrderedBoundaryLoops( sheet );
			Console.WriteLine( $"DBG {b.Name}: outer={sheet != b.Mesh} {loops.Count} loops: " + string.Join( ", ", loops.Select( l => { var c = l.Aggregate( Vec3.Zero, ( s, v ) => s + sheet.Positions[v] ) / l.Count; return $"{l.Count}@({c.x:F0},{c.y:F0},{c.z:F0})"; } ) ) );
		}
		T On<T>( T f, GarmentFeature g, string spot, int side ) where T : GarmentDetailFeature
		{
			f.Bodies.BodyIds.Add( g.Id + "b0" ); f.Spot.Index = Array.IndexOf( DetailSpots.Names, spot ); f.Side.Index = side;
			return studio.Add( f );
		}
		var zip = On( new PocketFeature { Name = "Zip pocket" }, jacket, "Chest", 0 ); zip.Style.Index = 2; zip.Width.Value = 4f; zip.Height.Value = 1.2f;
		var patch = On( new PocketFeature { Name = "Patch pocket" }, jacket, "Belly", 1 ); patch.Style.Index = 0; patch.Width.Value = 4f; patch.Height.Value = 4.5f;
		var cargo = On( new PocketFeature { Name = "Cargo pocket" }, jeans, "Thigh", 2 ); cargo.Style.Index = 1; cargo.Width.Value = 5f; cargo.Height.Value = 6f;
		var collar = On( new HardwareFeature { Name = "Collar studs" }, jacket, "Collar", 2 ); collar.Layout.Index = 3; collar.Style.Index = 0; collar.Size.Value = 0.4f;
		var spikes = On( new HardwareFeature { Name = "Shoulder spikes" }, jacket, "Shoulder", 2 ); spikes.Style.Index = 1; spikes.Count.Value = 5; spikes.Size.Value = 0.5f; spikes.Height.Value = 1f;
		var domes = On( new HardwareFeature { Name = "Cuff domes" }, jacket, "Wrist", 2 ); domes.Style.Index = 2; domes.Layout.Index = 4; domes.Size.Value = 0.35f;
		var eyelets = On( new HardwareFeature { Name = "Eyelets" }, tee, "Belly", 2 ); eyelets.Style.Index = 3; eyelets.Count.Value = 6;
		var pins = On( new HardwareFeature { Name = "Safety pins" }, jeans, "Knee", 0 ); pins.Style.Index = 4; pins.Count.Value = 3;
		var chain = On( new HardwareFeature { Name = "Wallet chain" }, jeans, "Hip", 1 ); chain.Style.Index = 5; chain.Span.Value = 9f; chain.Sag.Value = 3f;
		var strap = On( new HardwareFeature { Name = "Bondage strap" }, jeans, "Thigh", 0 ); strap.Style.Index = 6; strap.Finish.Index = 3;
		var rip = On( new WearFeature { Name = "Knee rip" }, jeans, "Knee", 1 ); rip.Style.Index = 2; rip.Width.Value = 4f; rip.Height.Value = 1.5f;
		var hole = On( new WearFeature { Name = "Shin hole" }, jeans, "Shin", 0 ); hole.Style.Index = 3; hole.Width.Value = 2f; hole.Height.Value = 2f;
		var scuff = On( new WearFeature { Name = "Elbow scuff" }, jacket, "Elbow", 2 ); scuff.Style.Index = 0;
		var dirt = On( new WearFeature { Name = "Hem dirt" }, jeans, "Shin", 2 ); dirt.Style.Index = 1; dirt.Area.Index = 2;
		var led = On( new GlowFeature { Name = "Back LED" }, jacket, "Back", 2 ); led.Style.Index = 0; led.Pattern.Index = 1; led.Text.Value = "NO FUTURE"; led.Colour.Value = "#ff2a6d";
		var el = On( new GlowFeature { Name = "EL piping" }, jacket, "Wrist", 2 ); el.Style.Index = 2; el.Openings.Index = 4; el.Colour.Value = "#05d9e8";
		var strip = On( new GlowFeature { Name = "Light strip" }, jeans, "Shin", 2 ); strip.Style.Index = 1; strip.Colour.Value = "#d1f7ff";
		var plate = On( new GlowFeature { Name = "Shoulder plate" }, jacket, "Upper arm", 0 ); plate.Style.Index = 3;
		var tape = On( new GlowFeature { Name = "Bright tape" }, jacket, "Forearm", 1 ); tape.Style.Index = 4;
		result = studio.Rebuild();
		foreach ( var f in studio.Features.Where( f => f.Error is not null ) ) Console.Error.WriteLine( $"{f.Name}: {f.Error}" );
		if ( result.HasErrors ) { Console.Error.WriteLine( string.Join( "; ", result.Errors.Select( e => e.Message ) ) ); return 1; }

		var assets = Path.Combine( folder, "Assets" );
		Directory.CreateDirectory( Path.Combine( assets, "materials" ) );
		void Text( string rel, string content ) => File.WriteAllText( Path.Combine( assets, rel ), content );
		void Png( string rel, byte[] bytes, int w, int h ) => File.WriteAllBytes( Path.Combine( assets, rel ), PngWriter.ToBytesRgba( bytes, w, h ) );
		void FabricVmat( string rel, string preset, Vec3 colour, Vec3 accent )
		{
			var maps = FabricMaterial.Maps( preset, colour, accent, 6f, 0.4f, 17 );
			Png( rel + "_color.png", maps.Color, FabricMaterial.Size, FabricMaterial.Size );
			Png( rel + "_normal.png", maps.Normal, FabricMaterial.Size, FabricMaterial.Size );
			Png( rel + "_rough.png", maps.Roughness, FabricMaterial.Size, FabricMaterial.Size );
			Text( rel + ".vmat", FabricMaterial.VmatSource( preset, rel + "_color.png", rel + "_normal.png", rel + "_rough.png" ) );
		}
		foreach ( var f in fabrics )
		{
			var rel = "materials/" + f.Id;
			var colour = FurMaterial.ParseHex( f.Colour.Value, new Vec3( 0.3f, 0.3f, 0.3f ) );
			FabricVmat( rel, f.PresetName, colour, colour * 1.4f );
			foreach ( var slot in f.ResolvedSlots ) studio.MaterialNames[slot] = rel + ".vmat";
		}
		foreach ( var m in studio.Features.OfType<IDetailMaterials>().SelectMany( d => d.DetailMaterials ) )
		{
			var rel = "materials/" + new string( m.Key.Select( c => char.IsLetterOrDigit( c ) ? c : '_' ).ToArray() );
			var source = DetailMaterials.HardVmat( m );
			if ( m.Kind == "glow" )
			{
				var glowMaps = DetailMaterials.GlowMaps( m );
				Png( rel + "_color.png", glowMaps.Color, glowMaps.Width, glowMaps.Height );
				Png( rel + "_mask.png", glowMaps.Mask, glowMaps.Width, glowMaps.Height );
				source = DetailMaterials.GlowVmat( m, rel + "_color.png", rel + "_mask.png", glowMaps.UScale );
			}
			else if ( m.Kind is "worn" or "dirt" )
			{
				var fabric = fabrics.First( f => f.ResolvedSlots.Contains( m.SourceSlot ) );
				var colour = FurMaterial.ParseHex( fabric.Colour.Value, new Vec3( 0.3f, 0.3f, 0.3f ) );
				var tint = m.Kind == "worn" ? DetailMaterials.Worn( colour ) : DetailMaterials.Dirty( colour );
				FabricVmat( rel, fabric.PresetName, tint, tint * 1.4f );
				studio.MaterialNames[m.Slot] = rel + ".vmat";
				continue;
			}
			Text( rel + ".vmat", source ); studio.MaterialNames[m.Slot] = rel + ".vmat";
		}
		var mesh = studio.ToMesh();
		File.WriteAllText( Path.Combine( folder, "punk.obj" ), ObjWriter.Write( mesh, "punk",
			materialName: slot => studio.MaterialNames.TryGetValue( slot, out var path ) ? path : "material_" + slot ) );
		File.WriteAllText( Path.Combine( folder, "punk.effigy" ), StudioDocument.Write( studio ) );
		Console.WriteLine( $"punk figure: {folder}/punk.obj, {mesh.VertexCount} verts, {studio.Features.OfType<GarmentDetailFeature>().Count()} details" );
		return 0;
	}

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
