using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Effigy.Tests;

public static class GarmentDetailTests
{
	sealed class ClothFixture : Feature
	{
		public PolyMesh Mesh { get; set; } = Cloth();
		public override string TypeName => "Test cloth";
		public override System.Collections.Generic.IReadOnlyList<IParam> Parameters => Array.Empty<IParam>();
		protected override void Execute( FeatureContext ctx ) => ctx.Bodies.Add( new Body( ctx.NewBodyId(), "Cloth", Mesh.Clone() ) { IsGarment = true } );
	}
	public static PolyMesh Cloth()
	{
		var mesh = new PolyMesh();
		const int around = 64, rows = 64;
		for ( var j = 0; j <= rows; j++ )
			for ( var i = 0; i < around; i++ )
			{
				var a = i * 2f * MathF.PI / around;
				mesh.AddVertex( new Vec3( 8f * MathF.Cos( a ), 8f * MathF.Sin( a ), 25f + j * 0.625f ) );
			}
		for ( var j = 0; j < rows; j++ )
			for ( var i = 0; i < around; i++ )
			{
				var next = (i + 1) % around;
				mesh.AddFace( new[] { j * around + i, j * around + next, (j + 1) * around + next, (j + 1) * around + i }, null, 3 );
			}
		return mesh;
	}

	internal static void PrepareInput( GarmentDetailFeature detail, PartStudio studio, bool solid = false )
	{
		studio.Rig = CitizenSkeleton.Build();
		var mesh = Cloth();
		mesh.Skin = SkinBinder.BindSmooth( mesh, studio.Rig );
		if ( solid ) mesh = MeshSolidify.Solidify( mesh, 0.12f, smooth: true );
		studio.Add( new ClothFixture { Mesh = mesh } );
		studio.Rebuild();
		var body = studio.Bodies.Last();
		detail.Bodies.BodyIds.Add( body.Id );
	}

	/// <summary>A jacket over a tee kept every face of the wearer the tee hugged out of its cut,
	/// so the jacket came out full of holes the tee showed through, and Hardware on "the rest"
	/// of the openings studded each hole.</summary>
	static void TestLayeringKeepsOpenings()
	{
		Report.Section( "garment details: a garment over another keeps only its own openings" );
		int Loops( PartStudio studio, GarmentFeature g ) =>
			GarmentTrim.OrderedBoundaryLoops( DetailEdges.Sheet( studio.Bodies.Single( b => b.FeatureId == g.Id ).Mesh ) ).Count;
		var alone = new PartStudio();
		var jacketAlone = PaddingTests.AddGarment( alone );
		var expected = Loops( alone, jacketAlone );
		var layered = new PartStudio();
		var tee = PaddingTests.AddGarment( layered );
		var jacket = layered.Add( new GarmentFeature { Name = "Jacket" } );
		jacket.Bodies.BodyIds.AddRange( tee.Bodies.BodyIds );
		jacket.Sleeve.Value = 1f; jacket.Length.Value = 0.8f; jacket.Fit.Index = 2; jacket.Drape.Value = false;
		layered.Rebuild();
		Report.Check( "the jacket builds over the tee", jacket.Error is null, jacket.Error );
		if ( jacket.Error is not null ) return;
		var got = Loops( layered, jacket );
		Report.Check( $"same openings as on its own ({got} vs {expected})", got == expected );
	}

	public static void Run()
	{
		TestLayeringKeepsOpenings();
		Report.Section( "garment details: surface frames and curved walking" );
		var surface = new DetailSurface( Cloth() );
		var f = new SurfaceFrame( new Vec3( 8, 0, 45 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) );
		Report.Check( "right cross up points out", Vec3.Cross( f.Right, f.Up ).AlmostEquals( f.Normal, 1e-5f ) );
		var moved = surface.Offset( f, 5, 2 );
		Report.Check( "a wide patch wraps the cylinder", MathF.Abs( new Vec2( moved.Point.x, moved.Point.y ).Length - 8 ) < 0.03f && moved.Point.x < 7f );
		Report.Check( "walking up preserves height", MathF.Abs( moved.Point.z - 47 ) < 0.05f );
		Report.Check( "frame stays orthogonal", MathF.Abs( Vec3.Dot( moved.Normal, moved.Up ) ) < 1e-5f );
		var threw = false;
		try { DetailShapes.Resample( new[] { Vec3.Zero, new Vec3( 1, 0, 0 ) }, 0f, false, out _ ); }
		catch ( ArgumentOutOfRangeException ) { threw = true; }
		Report.Check( "zero spacing refuses rather than looping forever", threw );

		Report.Section( "garment details: all pocket, hardware and glow styles" );
		foreach ( var kind in new[] { "pocket", "hardware", "glow" } )
		{
			var count = kind == "pocket" ? 3 : kind == "hardware" ? 9 : 5;
			for ( var style = 0; style < count; style++ )
			{
				GarmentDetailFeature detail = kind == "pocket" ? new PocketFeature() : kind == "hardware" ? new HardwareFeature() : new GlowFeature();
				if ( detail is PocketFeature pocket ) pocket.Style.Index = style;
				if ( detail is HardwareFeature hardware ) hardware.Style.Index = style;
				if ( detail is GlowFeature glow ) glow.Style.Index = style;
				var studio = new PartStudio();
				PrepareInput( detail, studio );
				studio.Add( detail );
				var result = studio.Rebuild();
				var body = studio.Bodies.LastOrDefault( b => b.IsGarmentDetail );
				Report.Check( $"{kind} {style} builds", !result.HasErrors && body?.Mesh.FaceCount > 0, detail.Error ?? "empty" );
				if ( body is null ) continue;
				Report.Check( $"{kind} {style} is valid and finite", MeshValidator.Validate( body.Mesh ).IsValid && body.Mesh.Positions.All( p => float.IsFinite( p.x + p.y + p.z ) ) );
				var before = body.Mesh.Positions.ToArray();
				studio.MarkAllDirty(); studio.Rebuild();
				Report.Check( $"{kind} {style} rebuild is deterministic", before.SequenceEqual( studio.Bodies.Last( b => b.IsGarmentDetail ).Mesh.Positions ) );
				Report.Check( $"{kind} {style} inherits skin weights", body.Mesh.IsRigged && body.Mesh.Skin.Count == body.Mesh.VertexCount );
				var saved = new PartStudio(); saved.Add( detail );
				var reload = StudioDocument.Read( StudioDocument.Write( saved ) );
				Report.Check( $"{kind} {style} parameters reload", reload.Features.Last().GetType() == detail.GetType()
					&& ((GarmentDetailFeature)reload.Features.Last()).Bodies.BodyIds.SequenceEqual( detail.Bodies.BodyIds ) );
			}
		}

		Report.Section( "garment details: openings, ownership, weights and suppression" );
		foreach ( var wire in new[] { false, true } )
		{
			GarmentDetailFeature d = wire ? new GlowFeature() : new HardwareFeature();
			if ( d is GlowFeature g ) { g.Style.Index = 2; g.Openings.Index = 1; }
			if ( d is HardwareFeature h ) h.Layout.Index = 1;
			var s = new PartStudio(); PrepareInput( d, s, true ); s.Add( d ); s.Rebuild();
			Report.Check( $"{(wire ? "wire" : "studs")} finds solidified openings", d.Error is null && s.Bodies.Any( b => b.IsGarmentDetail ), d.Error );
		}
		var owned = new PartStudio(); var studs = new HardwareFeature(); PrepareInput( studs, owned ); owned.Add( studs );
		var fabric = owned.Add( new FabricFeature() ); fabric.Bodies.BodyIds.AddRange( studs.Bodies.BodyIds );
		owned.Rebuild();
		Report.Check( "fabric cannot claim hardware slots", !fabric.ResolvedSlots.Intersect( studs.DetailMaterials.Select( m => m.Slot ) ).Any() );
		studs.Suppressed = true; owned.MarkAllDirty(); owned.Rebuild();
		Report.Check( "suppression removes detail geometry", !owned.Bodies.Any( b => b.IsGarmentDetail ) );
		Report.Check( "detail flag survives a body clone", new Body( "a", "b", Cloth() ) { IsGarment = true, IsGarmentDetail = true }.Clone().IsGarmentDetail );
		var furred = new PartStudio(); var hard = new HardwareFeature(); PrepareInput( hard, furred ); furred.Add( hard );
		var fur = furred.Add( new FurFeature() ); fur.Layers.Value = 2;
		var trim = furred.Add( new TrimFeature() ); furred.Rebuild();
		var hardBody = furred.Bodies.FirstOrDefault( b => b.FeatureId == hard.Id );
		Report.Check( "automatic Fur leaves hardware alone", hardBody is not null && !hardBody.Mesh.Faces.Any( face => face.Material == fur.ResolvedSlot ) );
		Report.Check( "automatic Trim ignores detail bodies", trim.Error is null && furred.Bodies.Count( b => b.FeatureId == trim.Id ) == 1, trim.Error );
		var following = new PartStudio(); var first = new PocketFeature(); PrepareInput( first, following ); first.Bodies.BodyIds.Clear();
		following.Add( first ); var second = following.Add( new GlowFeature() ); second.Side.Index = 1;
		following.Rebuild();
		Report.Check( "defaults decorate cloth, not previously made details", following.Bodies.Count( b => b.IsGarmentDetail ) == 2 );
		Report.Check( "successive features own disjoint slots", !first.DetailMaterials.Select( m => m.Slot ).Intersect( second.DetailMaterials.Select( m => m.Slot ) ).Any() );
		var oldPoint = following.Bodies.First( b => b.IsGarmentDetail ).Mesh.Positions[300];
		var fixture = following.Features.OfType<ClothFixture>().Single();
		for ( var v = 0; v < fixture.Mesh.VertexCount; v++ )
		{
			var p = fixture.Mesh.Positions[v]; fixture.Mesh.Positions[v] = new Vec3( p.x * 1.2f, p.y * 1.2f, p.z );
		}
		following.MarkAllDirty(); following.Rebuild();
		Report.Check( "details follow an upstream fit change", (following.Bodies.First( b => b.IsGarmentDetail ).Mesh.Positions[300] - oldPoint).Length > 0.8f );
		first.Spot.Index = Array.IndexOf( DetailSpots.Names, "Shin" ); following.MarkAllDirty(); following.Rebuild();
		Report.Check( "an uncovered place reports a remedy", first.Error is not null && first.Error.Contains( "does not reach" ) );

		Report.Section( "garment details: wear and cuts through both walls" );
		for ( var style = 0; style < 4; style++ )
		{
			var s = new PartStudio(); var wear = new WearFeature(); PrepareInput( wear, s, true );
			wear.Style.Index = style; wear.Amount.Value = 1f; wear.Width.Value = 5f; wear.Height.Value = 3f;
			s.Add( wear ); s.Rebuild();
			Report.Check( $"wear {style} changes faces", wear.Error is null && wear.ChangedFaces > 0, wear.Error );
			var cloth = s.Bodies.First().Mesh;
			Report.Check( $"wear {style} leaves a valid garment", MeshValidator.Validate( cloth ).IsValid );
			if ( style >= 2 )
			{
				Report.Check( $"cut {style} retains readable outer wall", MeshSolidify.OuterSheet( cloth, out _ ) is not null );
				Report.Check( $"cut {style} closes the rim", cloth.BuildEdgeFaces().Values.All( faces => faces.Count == 2 ) );
				Report.Check( $"cut {style} adds threads", s.Bodies.Any( b => b.IsGarmentDetail ) );
			}
		}
		var untouched = new PartStudio(); var noWear = new WearFeature(); PrepareInput( noWear, untouched, true );
		noWear.Amount.Value = 0f; untouched.Add( noWear ); untouched.Rebuild();
		Report.Check( "zero wear is a genuine no-op", noWear.Error is null && noWear.ChangedFaces == 0 && noWear.DetailMaterials.Count == 0 );

		Report.Section( "garment details: emission maps and culture-independent recipes" );
		foreach ( var pattern in DetailMaterials.Patterns )
		{
			var m = new DetailMaterial { Pattern = pattern, Text = "PILOT 07", Colour = new Vec3( 0, 0.8f, 1 ) };
			var maps = DetailMaterials.GlowMaps( m );
			Report.Check( $"{pattern} maps have matching dimensions", maps.Color.Length == maps.Width * maps.Height * 4 && maps.Mask.Length == maps.Color.Length );
			Report.Check( $"{pattern} lights some pixels", maps.Mask.Where( ( b, i ) => i % 4 == 0 ).Any( b => b > 0 ) );
			Report.Check( $"{pattern} is deterministic", maps.Color.SequenceEqual( DetailMaterials.GlowMaps( m ).Color ) );
		}
		var material = new DetailMaterial { Speed = 0.15f, Brightness = 3.5f };
		var recipe = material.Recipe;
		var culture = CultureInfo.CurrentCulture;
		try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo( "fr-FR" ); Report.Check( "recipes ignore locale", recipe == material.Recipe ); }
		finally { CultureInfo.CurrentCulture = culture; }
		Report.Check( "glow material declares emission and scrolling", DetailMaterials.GlowVmat( material, "color.png", "mask.png", 1f ).Contains( "F_SELF_ILLUM 1" )
			&& DetailMaterials.GlowVmat( material, "color.png", "mask.png", 1f ).Contains( "0.150" ) );
	}
}
