using System;
using System.Linq;
using System.Text;

namespace Effigy.Tests;

public static class PaddingTests
{
	/// <summary>Padding needs actual shoulders, not just the generic box fixture's spine.</summary>
	internal static GarmentFeature AddGarment( PartStudio studio )
	{
		studio.Rig = CitizenSkeleton.Build();
		var wearer = studio.Add( new WearerFeature() );
		wearer.LoadMesh( Encoding.UTF8.GetBytes( ObjWriter.Write( SkinBlockout.Build( studio.Rig ) ) ) );
		var garment = studio.Add( new GarmentFeature() );
		garment.Bodies.BodyIds.Add( wearer.Id + "b0" );
		garment.Sleeve.Value = 1f;
		garment.Length.Value = 0.8f;
		garment.Drape.Value = false;
		studio.Rebuild();
		return garment;
	}

	public static void Run()
	{
		Report.Section( "padding: named regions thicken only the outer wall" );
		var studio = new PartStudio();
		var garment = AddGarment( studio );
		var baseline = studio.Bodies.Single( b => b.FeatureId == garment.Id ).Mesh.Clone();
		var sheet = MeshSolidify.OuterSheet( baseline, out var offset );
		Report.Check( "fixture has a thickened garment", garment.Error is null && sheet is not null, garment.Error );
		if ( sheet is null ) return;
		var pad = studio.Add( new PaddingFeature() );
		foreach ( var region in new[] { "Shoulder yoke", "Shoulder pad", "Chest", "Whole garment" } )
		{
			pad.Where.Index = Array.IndexOf( PaddingFeature.RegionNames, region );
			studio.MarkAllDirty();
			studio.Rebuild();
			var mesh = studio.Bodies.Single( b => b.FeatureId == garment.Id ).Mesh;
			Report.Check( $"{region} moves cloth", pad.Error is null && pad.PaddedVertices > 0, pad.Error );
			Report.Check( $"{region} leaves the lining unchanged", Enumerable.Range( 0, offset )
				.All( v => mesh.Positions[v].AlmostEquals( baseline.Positions[v], 1e-5f ) ) );
			Report.Check( $"{region} stays within the requested thickness", Enumerable.Range( offset, offset )
				.All( v => (mesh.Positions[v] - baseline.Positions[v]).Length <= pad.Thickness.Clamped + 1e-4f ) );
		}
		pad.Side.Index = 1;
		pad.Quilt.Value = 1f;
		var field = pad.Field( sheet, new BodyRegions.Map( studio.Rig ), out var weights );
		Report.Check( "left-only padding leaves the right unpadded", Enumerable.Range( 0, sheet.VertexCount )
			.Where( v => sheet.Positions[v].y < 0f ).All( v => field[v] == 0f ) );
		Report.Check( "quilting varies the loft", weights.Where( w => w > 0f ).Distinct().Take( 2 ).Count() == 2 );
		pad.Suppressed = true;
		studio.MarkAllDirty(); studio.Rebuild();
		Report.Check( "suppression restores the unpadded garment", studio.Bodies.Single( b => b.FeatureId == garment.Id ).Mesh.Positions.SequenceEqual( baseline.Positions ) );
	}
}
