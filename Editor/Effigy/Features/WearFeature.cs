using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Local face-slot wear, or real cuts through both walls with thread geometry.</summary>
public sealed class WearFeature : GarmentDetailFeature
{
	public override string TypeName => "Wear";
	public readonly ChoiceParam Style = new( "Style", new[] { "Abrasion", "Dirt", "Rip", "Hole" } );
	public readonly ChoiceParam Area = new( "Area", new[] { "Body part", "All openings", "Hem (lowest)", "Collar (highest)", "Cuffs (the rest)" } );
	public readonly FloatParam Width = new( "Width", 3f, 0.5f, 12f, "in" );
	public readonly FloatParam Height = new( "Height", 2f, 0.2f, 12f, "in" );
	public readonly FloatParam Amount = new( "Amount", 0.8f, 0f, 1f );
	public readonly IntParam Seed = new( "Seed", 17, 0, 65535 );
	public readonly BoolParam Fray = new( "Loose threads", true );
	public override IReadOnlyList<IParam> Parameters => Placement.Concat( new IParam[] { Style, Area, Width, Height, Amount, Fray, Seed } ).ToArray();
	public int ChangedFaces { get; private set; }
	protected override void Execute( FeatureContext ctx )
	{
		var targets = Targets( ctx );
		ChangedFaces = 0;
		var map = new BodyRegions.Map( ctx.Rig );
		var rng = new Random( Seed.Clamped );
		var variants = new Dictionary<int, int>();
		int Variant( int source )
		{
			if ( variants.TryGetValue( source, out var existing ) ) return existing;
			var slot = NextSlot( ctx );
			ctx.ShellSlots.Add( slot );
			Materials.Add( new DetailMaterial { Kind = Style.Index == 1 ? "dirt" : "worn", Slot = slot,
				SourceSlot = source, Key = Id + "_wear_" + source } );
			return variants[source] = slot;
		}
		foreach ( var body in targets )
		{
			var mesh = body.Mesh;
			var sheet = MeshSolidify.OuterSheet( mesh, out var offset );
			var thick = sheet is not null;
			sheet ??= mesh;
			var surface = new DetailSurface( sheet );
			var frames = DetailSpots.Sides( Side.Index ).Select( s => DetailSpots.Find( surface, map, Spot.Value, s, Across.Clamped, Up.Clamped ) )
				.Where( f => f.HasValue ).Select( f => f.Value.Turned( Angle.Clamped ) ).ToList();
			var edges = Area.Index > 0 ? DetailEdges.Openings( mesh, Area.Index - 1 ).SelectMany( e => e.Points ).ToList() : new List<Vec3>();
			var picked = new HashSet<int>();
			for ( var fi = 0; fi < sheet.FaceCount; fi++ )
			{
				var face = sheet.Faces[fi];
				if ( ctx.ShellSlots.Contains( face.Material ) ) continue;
				var p = sheet.FaceCentroid( face );
				var strength = 0f;
				if ( Area.Index > 0 && edges.Count > 0 )
					strength = 1f - edges.Min( e => (e - p).Length ) / Width.Clamped;
				else if ( Area.Index == 0 )
					foreach ( var f in frames )
					{
						var d = p - f.Point;
						if ( MathF.Abs( Vec3.Dot( d, f.Normal ) ) > 2f || Vec3.Dot( sheet.FaceNormal( face ), f.Normal ) < 0f ) continue;
						var x = Vec3.Dot( d, f.Right ) / (Width.Clamped * 0.5f);
						var y = Vec3.Dot( d, f.Up ) / ((Style.Index == 2 ? MathF.Min( Height.Clamped, 0.6f ) : Height.Clamped) * 0.5f);
						strength = MathF.Max( strength, 1f - MathF.Sqrt( x * x + y * y ) );
					}
				if ( strength <= 0f || Amount.Clamped == 0f ) continue;
				if ( Style.Index < 2 && rng.NextDouble() > Amount.Clamped * MathF.Min( 1f, strength * 3f ) ) continue;
				if ( Style.Index >= 2 && rng.NextDouble() > Amount.Clamped ) continue;
				picked.Add( fi );
			}
			if ( picked.Count == 0 ) continue;
			ChangedFaces += picked.Count;
			if ( Style.Index < 2 )
			{
				foreach ( var fi in picked )
				{
					var slot = Variant( sheet.Faces[fi].Material );
					mesh.Faces[thick ? fi * 2 : fi].Material = slot;
				}
				continue;
			}
			// Never discard shells a later feature already grew. Cutting should precede Fur.
			if ( thick && mesh.VertexCount != offset * 2 )
				Fail( "Cut the cloth before growing fur", "This garment already has extra shell geometry.", "Move Wear above Fur in the feature tree" );
			if ( picked.Count == sheet.FaceCount )
				Fail( "The cut would remove the whole garment", "The selected area covers every face.", "Reduce Width or Height" );
			var threads = new PolyMesh();
			if ( Fray.Value )
			{
				var edgeFaces = sheet.BuildEdgeFaces();
				foreach ( var fi in picked )
				{
					var face = sheet.Faces[fi];
					for ( var k = 0; k < face.Count; k++ )
					{
						var a = face.Indices[k]; var b = face.Indices[(k + 1) % face.Count];
						if ( !edgeFaces[new EdgeKey( a, b )].Any( other => !picked.Contains( other ) ) ) continue;
						var slot = Variant( face.Material );
						for ( var t = 0; t < 3; t++ )
						{
							var p = Vec3.Lerp( sheet.Positions[a], sheet.Positions[b], (t + 0.5f) / 3f );
							var end = p + (sheet.FaceCentroid( face ) - p).Normal * (0.12f + (float)rng.NextDouble() * 0.35f);
							DetailShapes.Tube( threads, new[] { p, end + new Vec3( 0, 0, -0.1f ) }, 0.012f, 3, false, slot );
						}
					}
				}
			}
			// Rebuild the cut sheet and its rim, compacting orphaned vertices so OuterSheet remains
			// readable by the next Trim/Fur/Clothing check. Reuse the original inner wall positions.
			var cut = new PolyMesh();
			var remap = new Dictionary<int, int>();
			foreach ( var fi in Enumerable.Range( 0, sheet.FaceCount ).Where( i => !picked.Contains( i ) ) )
			{
				var f = sheet.Faces[fi];
				cut.AddFace( f.Indices.Select( v => remap.TryGetValue( v, out var id ) ? id : remap[v] = cut.AddVertex( thick ? mesh.Positions[v] : sheet.Positions[v] ) ).ToArray(),
					(Vec2[])f.UVs.Clone(), f.Material );
			}
			if ( sheet.IsRigged )
			{
				cut.Skin = new SkinWeights();
				foreach ( var v in remap.OrderBy( kv => kv.Value ).Select( kv => kv.Key ) ) cut.Skin.Vertices.Add( (BoneWeight[])sheet.Skin[v].Clone() );
			}
			var thickness = thick ? Enumerable.Range( 0, offset ).Average( v => (mesh.Positions[v + offset] - mesh.Positions[v]).Length ) : 0f;
			body.Mesh = thick ? MeshSolidify.Solidify( cut, thickness, smooth: true ) : cut;
			if ( threads.FaceCount > 0 )
			{
				if ( mesh.IsRigged ) SkinBinder.TransferFrom( threads, mesh );
				ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"Fray on {body.Name}", threads ) { IsGarment = true, IsGarmentDetail = true } );
			}
		}
		if ( ChangedFaces == 0 && Amount.Clamped > 0f )
			Fail( "No cloth faces in that area", "The garment may not reach that place, or its faces are larger than the cut.", "Choose a covered place, enlarge the area, or subdivide the garment before Wear" );
	}
}
