using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Shared anatomical placement and material ownership for sewn-on garment details.</summary>
public abstract class GarmentDetailFeature : Feature, IDetailMaterials
{
	public override GeometryKind Accepts => GeometryKind.Body;
	public readonly BodySelectionParam Bodies = new( "On" );
	public readonly ChoiceParam Spot = new( "Where", DetailSpots.Names );
	public readonly ChoiceParam Side = new( "Side", DetailSpots.SideNames );
	public readonly FloatParam Across = new( "Across", 0f, -20f, 20f, "in" );
	public readonly FloatParam Up = new( "Up", 0f, -20f, 20f, "in" );
	public readonly FloatParam Angle = new( "Angle", 0f, -180f, 180f, "deg" );
	protected readonly List<DetailMaterial> Materials = new();
	public IReadOnlyList<DetailMaterial> DetailMaterials => Materials;
	protected IParam[] Placement => new IParam[] { Bodies, Spot, Side, Across, Up, Angle };
	protected static int NextSlot( FeatureContext ctx ) => ctx.Bodies.SelectMany( b => b.Mesh.Faces )
		.Select( f => f.Material ).Concat( ctx.ShellSlots ).DefaultIfEmpty( 0 ).Max() + 1;
	protected int Metal( FeatureContext ctx, string finish )
	{
		var slot = NextSlot( ctx );
		Materials.Add( Effigy.DetailMaterials.Metal( Id + "_metal", slot, finish ) );
		ctx.ShellSlots.Add( slot );
		return slot;
	}
	protected int Dark( FeatureContext ctx )
	{
		var slot = NextSlot( ctx );
		Materials.Add( Effigy.DetailMaterials.Dark( Id + "_dark", slot ) );
		ctx.ShellSlots.Add( slot );
		return slot;
	}
	protected List<Body> Targets( FeatureContext ctx )
	{
		Materials.Clear();
		if ( ctx.Rig is not { Count: > 0 } )
			Fail( "This detail needs a rig", "Named places are found from the bones.", "Add a rigged Wearer and a Garment first" );
		var targets = ctx.Bodies.Where( b => Bodies.Matches( b ) &&
			(Bodies.BodyIds.Count > 0 || (b.IsGarment && !b.IsGarmentDetail)) ).ToList();
		if ( targets.Count == 0 )
			Fail( "No garment to decorate", "The selected garment is missing, or no garment has been made yet.", "Add a Garment first, then select it" );
		return targets;
	}
	/// <summary>
	/// Weight the detail to its garment and add it - and say so when a garment worn over that one
	/// already covers it. That garment was made before the detail, so it made no room for it: a
	/// wallet chain on jeans added after the jacket sits inside the jacket's hem.
	/// </summary>
	protected void AddDetail( FeatureContext ctx, Body host, PolyMesh mesh, string name )
	{
		if ( host.Mesh.IsRigged ) SkinBinder.TransferFrom( mesh, host.Mesh );
		foreach ( var over in ctx.Bodies.Where( b => b.IsGarment && !b.IsGarmentDetail && b != host && !b.IsReference ) )
		{
			var sheet = DetailEdges.Sheet( over.Mesh );
			var tree = MeshBVH.Build( sheet );
			var inside = mesh.Positions.Count( p => tree.NearestSurface( sheet, p, 3f ) is { } hit && Vec3.Dot( p - hit.Point, hit.Normal ) < -0.05f );
			if ( inside * 10 > mesh.VertexCount )
			{
				Warn( $"Part of this is hidden under {over.Name}",
					$"{over.Name} is worn over {host.Name} and comes before this detail in the history, so it makes no room for it and the detail clips through.",
					$"Drag this detail above {over.Name} in the history", $"Or put it on {over.Name}", "Or move it with Where, Across and Up" );
				break;
			}
		}
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), name, mesh ) { IsGarment = true, IsGarmentDetail = true, Layer = host.Layer } );
	}
	protected void Build( FeatureContext ctx, List<Body> targets,
		Action<PolyMesh, DetailSurface, SurfaceFrame, Body, int> make )
	{
		var map = new BodyRegions.Map( ctx.Rig );
		var made = 0;
		foreach ( var target in targets )
		{
			var sheet = DetailEdges.Sheet( target.Mesh );
			var surface = new DetailSurface( sheet );
			var mesh = new PolyMesh();
			foreach ( var side in DetailSpots.Sides( Side.Index ) )
				if ( DetailSpots.Find( surface, map, Spot.Value, side, Across.Clamped, Up.Clamped ) is { } f )
					make( mesh, surface, f.Turned( Angle.Clamped ), target, side );
			if ( mesh.FaceCount == 0 ) continue;
			AddDetail( ctx, target, mesh, $"{Name ?? TypeName} on {target.Name}" );
			made++;
		}
		if ( made == 0 )
			Fail( "The garment does not reach that place", $"No cloth was found at {Spot.Value}.", "Choose a covered body part, or change On" );
	}
	protected static void Panel( PolyMesh mesh, DetailSurface surface, SurfaceFrame f, float w, float h,
		Func<float, float, float> raise, int slot, bool image = false ) =>
		DetailShapes.Panel( mesh, surface.Grid( f, w, h, 12, 12 ), w, h, raise, ( x, y ) => slot, image ? 0f : 1f / 12f );
	protected static int ClothSlot( Body body ) => body.Mesh.Faces.GroupBy( f => f.Material )
		.OrderByDescending( g => g.Count() ).First().Key;
}

public sealed class PocketFeature : GarmentDetailFeature
{
	public override string TypeName => "Pocket";
	public readonly ChoiceParam Style = new( "Style", new[] { "Patch", "Cargo", "Zipper" } );
	public readonly FloatParam Width = new( "Width", 4.5f, 1f, 12f, "in" );
	public readonly FloatParam Height = new( "Height", 5.5f, 0.5f, 12f, "in" );
	public readonly FloatParam Depth = new( "Bellows depth", 1f, 0.1f, 3f, "in" );
	public readonly ChoiceParam Fastener = new( "Fastener", new[] { "Snap", "Button", "None" } );
	public readonly ChoiceParam Finish = new( "Finish", Effigy.DetailMaterials.Finishes );
	public override IReadOnlyList<IParam> Parameters => Placement.Concat( new IParam[] { Style, Width, Height, Depth, Fastener, Finish } ).ToArray();
	protected override void Execute( FeatureContext ctx )
	{
		var targets = Targets( ctx );
		var metal = Metal( ctx, Finish.Value );
		var dark = Dark( ctx );
		Build( ctx, targets, ( mesh, surface, f, body, side ) =>
		{
			var w = Width.Clamped;
			var h = Height.Clamped;
			var cloth = ClothSlot( body );
			if ( Style.Index == 2 )
			{
				Panel( mesh, surface, f, w, 0.8f, ( x, y ) => MathF.Abs( x ) < w * 0.48f ? 0.05f : 0f, dark );
				for ( var sign = -1; sign <= 1; sign += 2 )
					Panel( mesh, surface, surface.Offset( f, 0, sign * 0.3f ), w + 0.2f, 0.3f, ( x, y ) => 0.1f, cloth );
				for ( var x = -w * 0.45f; x < w * 0.45f; x += 0.13f )
					DetailShapes.Place( mesh, DetailShapes.Snap( 0.055f, metal ), surface.Offset( f, x, 0 ), 0.08f );
				DetailShapes.Place( mesh, DetailShapes.ZipPull( 0.8f, metal ), surface.Offset( f, w * 0.35f, 0 ), 0.12f );
				return;
			}
			var depth = Style.Index == 1 ? Depth.Clamped : 0.08f;
			Panel( mesh, surface, f, w, h, ( x, y ) =>
			{
				var edge = -DetailShapes.RoundedRectDistance( x, y, w * 0.5f, h * 0.5f, 0.35f );
				var rise = DetailShapes.Smooth( 0f, Style.Index == 1 ? 0.6f : 0.15f, edge );
				// Two recessed pleats give the cargo its folded bellows rather than a swollen pouch.
				var pleat = Style.Index == 1 ? 1f - 0.3f * MathF.Exp( -MathF.Pow( (MathF.Abs( x ) - w * 0.3f) / 0.12f, 2 ) ) : 1f;
				return rise * depth * pleat;
			}, cloth );
			if ( Style.Index != 1 ) return;
			var flap = surface.Offset( f, 0, h * 0.5f - 0.5f );
			Panel( mesh, surface, flap, w + 0.3f, 1.8f, ( x, y ) =>
				DetailShapes.RoundedRectDistance( x, y, (w + 0.3f) * 0.5f, 0.9f, 0.3f ) < -0.03f ? depth + 0.08f : 0f, cloth );
			if ( Fastener.Index != 2 )
				DetailShapes.Place( mesh, Fastener.Index == 1 ? DetailShapes.Button( 0.35f, metal ) : DetailShapes.Snap( 0.3f, metal ),
					surface.Offset( flap, 0, -0.45f ), depth + 0.12f );
		} );
	}
}

public sealed class HardwareFeature : GarmentDetailFeature
{
	public override string TypeName => "Hardware";
	public readonly ChoiceParam Style = new( "Style", new[] { "Pyramid studs", "Cone spikes", "Dome studs", "Grommets", "Safety pins", "Chain", "Strap and buckle", "Port", "Cable" } );
	public readonly ChoiceParam Finish = new( "Finish", Effigy.DetailMaterials.Finishes );
	public readonly ChoiceParam Layout = new( "Layout", new[] { "At body part", "All openings", "Hem (lowest)", "Collar (highest)", "Cuffs (the rest)" } );
	public readonly IntParam Count = new( "Count", 5, 1, 64 );
	public readonly FloatParam Size = new( "Size", 0.5f, 0.15f, 2f, "in" );
	public readonly FloatParam Height = new( "Spike height", 0.75f, 0.1f, 3f, "in" );
	public readonly FloatParam Spacing = new( "Spacing", 0.8f, 0.2f, 3f, "in" );
	public readonly FloatParam Span = new( "Span", 7f, 1f, 20f, "in" );
	public readonly FloatParam Sag = new( "Sag", 2f, 0f, 8f, "in" );
	public override IReadOnlyList<IParam> Parameters => Placement.Concat( new IParam[] { Style, Finish, Layout, Count, Size, Height, Spacing, Span, Sag } ).ToArray();
	protected override void Execute( FeatureContext ctx )
	{
		var targets = Targets( ctx );
		var metal = Metal( ctx, Finish.Value );
		var dark = Dark( ctx );
		PolyMesh Part() => Style.Index switch
		{
			1 => DetailShapes.Cone( Size.Clamped * 0.5f, Height.Clamped, metal ),
			2 => DetailShapes.Dome( Size.Clamped * 0.5f, Size.Clamped * 0.3f, metal ),
			3 => DetailShapes.Grommet( Size.Clamped * 0.5f, Size.Clamped * 0.27f, metal, dark ),
			4 => DetailShapes.SafetyPin( Size.Clamped * 3f, metal ),
			7 => DetailShapes.Port( Size.Clamped * 0.5f, metal, dark ),
			_ => DetailShapes.Pyramid( Size.Clamped, Height.Clamped * 0.4f, metal ),
		};
		if ( Layout.Index > 0 && Style.Index > 4 )
			Fail( "Choose At body part for this hardware", "Chains, straps, ports and cables need a named body part.", "Change Layout" );
		if ( Layout.Index > 0 )
		{
			var made = 0;
			foreach ( var body in targets )
			{
				var mesh = new PolyMesh();
				foreach ( var edge in DetailEdges.Openings( body.Mesh, Layout.Index - 1 ) )
				{
					var path = DetailEdges.Inset( edge, Size.Clamped * 0.6f, 0.04f );
					var at = DetailShapes.Resample( path.Select( p => p.Point ).ToList(), Spacing.Clamped, true, out var points );
					for ( var k = 0; k < Math.Min( points.Count, 256 ); k++ )
						DetailShapes.Place( mesh, Part(), new SurfaceFrame( points[k], path[at[k]].Normal, path[at[k]].Inward ) );
				}
				if ( mesh.FaceCount == 0 ) continue;
				AddDetail( ctx, body, mesh, $"{Name ?? TypeName} on {body.Name}" );
				made++;
			}
			if ( made == 0 ) Fail( "No openings found", "This garment has no selected open edge.", "Try All openings" );
			return;
		}
		Build( ctx, targets, ( mesh, surface, f, body, side ) =>
		{
			if ( Style.Index == 5 || Style.Index == 8 )
			{
				var path = new List<Vec3>();
				for ( var k = 0; k <= 48; k++ )
				{
					var t = k / 48f;
					var frame = surface.Offset( f, (t - 0.5f) * Span.Clamped, -4f * Sag.Clamped * t * (1f - t) );
					path.Add( frame.Point + frame.Normal * (0.12f + MathF.Sin( t * MathF.PI ) * 0.3f) );
				}
				if ( Style.Index == 8 ) DetailShapes.Tube( mesh, path, Size.Clamped * 0.25f, 8, false, dark );
				else
				{
					DetailShapes.Resample( path, Size.Clamped * 0.65f, false, out var links );
					for ( var k = 0; k < links.Count; k++ )
					{
						var tangent = (links[Math.Min( k + 1, links.Count - 1 )] - links[Math.Max( 0, k - 1 )]).Normal;
						var cross = Vec3.Cross( f.Normal, tangent ).Normal;
						DetailShapes.Tube( mesh, DetailShapes.Loop( links[k], tangent, k % 2 == 0 ? cross : f.Normal,
							Size.Clamped * 0.5f, Size.Clamped * 0.3f, 12 ), Size.Clamped * 0.08f, 5, true, metal );
					}
				}
				return;
			}
			if ( Style.Index == 6 )
			{
				var map = new BodyRegions.Map( ctx.Rig );
				if ( DetailSpots.Axis( map, Spot.Value, side, out var c, out var axis ) )
				{
					c += axis * -Up.Clamped;
					var lower = surface.Ring( c - axis * Size.Clamped * 0.5f, axis, 64 );
					var upper = surface.Ring( c + axis * Size.Clamped * 0.5f, axis, 64 );
					// Partial rings have no angle correspondence: never bridge missing samples.
					if ( lower.Count == 64 && upper.Count == 64 ) DetailShapes.Band( mesh, lower, upper, 0.03f, 0.06f, dark );
					else return;
				}
				DetailShapes.Place( mesh, DetailShapes.Buckle( Size.Clamped * 1.3f, Size.Clamped, metal ), f, 0.13f );
				return;
			}
			for ( var k = 0; k < Count.Clamped; k++ )
				DetailShapes.Place( mesh, Part(), surface.Offset( f, (k - (Count.Clamped - 1) * 0.5f) * Spacing.Clamped, 0 ), 0.04f );
		} );
	}
}

public sealed class GlowFeature : GarmentDetailFeature
{
	public override string TypeName => "Glow";
	public readonly ChoiceParam Style = new( "Style", new[] { "LED panel", "Light strip", "EL wire", "Armour plate", "Reflective tape" } );
	public readonly ChoiceParam Pattern = new( "Pattern", Effigy.DetailMaterials.Patterns, 1 );
	public readonly StringParam Text = new( "Text", "EFFIGY" );
	public readonly StringParam Colour = new( "Colour", "#00ddff" );
	public readonly FloatParam Width = new( "Width", 7f, 0.5f, 16f, "in" );
	public readonly FloatParam Height = new( "Height", 3f, 0.3f, 12f, "in" );
	public readonly FloatParam Speed = new( "Scroll speed", 0.15f, -2f, 2f );
	public readonly FloatParam Brightness = new( "Brightness", 4f, 0f, 16f );
	public readonly ChoiceParam Openings = new( "Wire along", new[] { "Body part", "All openings", "Hem (lowest)", "Collar (highest)", "Cuffs (the rest)" } );
	public override IReadOnlyList<IParam> Parameters => Placement.Concat( new IParam[] { Style, Pattern, Text, Colour, Width, Height, Speed, Brightness, Openings } ).ToArray();

	/// <summary>The LED cyan is a light's colour. Left at it, an armour plate is gunmetal and bright tape
	/// silver-white; any colour picked on purpose is kept.</summary>
	string SurfaceColour => Colour.Value?.Trim().ToLowerInvariant() != "#00ddff" ? Colour.Value
		: Style.Index == 3 ? "#4a4f57" : Style.Index == 4 ? "#d8dde0" : Colour.Value;
	protected override void Execute( FeatureContext ctx )
	{
		var targets = Targets( ctx );
		var dark = Dark( ctx );
		var slot = NextSlot( ctx );
		ctx.ShellSlots.Add( slot );
		Materials.Add( new DetailMaterial
		{
			Kind = Style.Index == 3 || Style.Index == 4 ? "hard" : "glow", Key = Id + "_face", Slot = slot,
			Colour = FurMaterial.ParseHex( SurfaceColour, new Vec3( 0, 0.8f, 1 ) ),
			Metalness = Style.Index == 3 ? 0.7f : 0f, Roughness = Style.Index == 4 ? 0.2f : 0.4f,
			Pattern = Style.Index == 1 || Style.Index == 2 ? "Solid" : Pattern.Value,
			Text = Text.Value, Speed = Speed.Clamped, Brightness = Brightness.Clamped, Aspect = Width.Clamped / Height.Clamped,
		} );
		if ( Style.Index == 2 && Openings.Index > 0 )
		{
			var made = 0;
			foreach ( var body in targets )
			{
				var mesh = new PolyMesh();
				foreach ( var edge in DetailEdges.Openings( body.Mesh, Openings.Index - 1 ) )
					DetailShapes.Tube( mesh, DetailEdges.Inset( edge, 0.12f, 0.08f ).Select( p => p.Point ).ToList(), 0.045f, 6, true, slot );
				if ( mesh.FaceCount == 0 ) continue;
				AddDetail( ctx, body, mesh, $"EL wire on {body.Name}" );
				made++;
			}
			if ( made == 0 ) Fail( "No openings found", "No edge is available for the wire.", "Try All openings" );
			return;
		}
		Build( ctx, targets, ( mesh, surface, f, body, side ) =>
		{
			var w = Width.Clamped;
			var h = Style.Index == 1 ? 0.4f : Height.Clamped;
			if ( Style.Index == 2 )
			{
				var path = Enumerable.Range( 0, 49 ).Select( k => surface.Offset( f, (k / 48f - 0.5f) * w, 0 ) )
					.Select( p => p.Point + p.Normal * 0.08f ).ToList();
				DetailShapes.Tube( mesh, path, 0.045f, 6, false, slot );
				return;
			}
			Panel( mesh, surface, f, w + 0.25f, h + 0.25f, ( x, y ) =>
				DetailShapes.RoundedRectDistance( x, y, (w + 0.25f) * 0.5f, (h + 0.25f) * 0.5f, 0.2f ) < 0 ? 0.1f : 0, dark );
			Panel( mesh, surface, f, w, h, ( x, y ) =>
			{
				var edge = -DetailShapes.RoundedRectDistance( x, y, w * 0.5f, h * 0.5f, 0.18f );
				return edge > 0 ? (Style.Index == 3 ? 0.12f + 0.25f * DetailShapes.Smooth( 0, 0.3f, edge ) : 0.14f) : 0;
			}, slot, true );
		} );
	}
}
