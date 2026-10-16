using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// What the headless Clothing run (effigy clothing) found wrong, pinned so it stays fixed:
/// a solidified garment with no openings, a pinch next to a pinch splitting a whole fan, the hips
/// measured front to back, and the trim and fur that all of those broke.
/// </summary>
public static class ClothingTabTests
{
	public static void Run()
	{
		Report.Section( "clothing: a solidified garment still has its openings" );
		TestOuterSheet();

		Report.Section( "clothing: a pinch beside a pinch splits cleanly" );
		TestPinchNextToPinch();

		Report.Section( "clothing: the hips run top to bottom" );
		TestHipSpan();

		Report.Section( "clothing: a default T-shirt on the s&box human, against a real size M" );
		TestDefaultTShirt();
	}

	static PolyMesh Tube( int rings, int segments )
	{
		var mesh = new PolyMesh();

		for ( var r = 0; r <= rings; r++ )
			for ( var s = 0; s < segments; s++ )
			{
				var a = s / (float)segments * MathF.PI * 2f;
				mesh.AddVertex( new Vec3( MathF.Cos( a ) * 5f, MathF.Sin( a ) * 5f, r * 2f ) );
			}

		for ( var r = 0; r < rings; r++ )
			for ( var s = 0; s < segments; s++ )
			{
				var n = (s + 1) % segments;
				mesh.AddFace( new[] { r * segments + s, r * segments + n, (r + 1) * segments + n, (r + 1) * segments + s } );
			}

		return mesh;
	}

	static void TestOuterSheet()
	{
		var tube = Tube( 4, 12 );
		var solid = MeshSolidify.Solidify( tube, 0.2f );

		Report.Check( "the solid has no boundary of its own", GarmentTrim.OrderedBoundaryLoops( solid ).Count == 0 );

		var sheet = MeshSolidify.OuterSheet( solid, out var offset );

		Report.Check( "the outer sheet is found", sheet is not null );
		Report.Check( "it has the tube's vertices and faces", sheet?.VertexCount == tube.VertexCount && sheet.FaceCount == tube.FaceCount,
			$"{sheet?.VertexCount} v {sheet?.FaceCount} f" );
		Report.Check( "and both of its openings", sheet is not null && GarmentTrim.OrderedBoundaryLoops( sheet ).Count == 2 );
		Report.Check( "on the far wall", sheet is not null && sheet.Positions[0].AlmostEquals( solid.Positions[offset], 1e-5f ) );

		// Shells appended afterwards, the way FurShells.Grow adds them, must not hide the layout.
		var grown = solid.Clone();
		var start = grown.VertexCount;
		for ( var i = 0; i < 4; i++ ) grown.AddVertex( new Vec3( i, 0, 30 ) );
		grown.AddFace( new[] { start, start + 1, start + 2, start + 3 }, null, 5 );

		Report.Check( "still found with shells appended", MeshSolidify.OuterSheet( grown, out _ )?.FaceCount == tube.FaceCount );
		Report.Check( "not found on a mesh that was never solidified", MeshSolidify.OuterSheet( tube, out _ ) is null );

		var report = GarmentCheck.Run( solid, new List<PolyMesh>() );
		Report.Check( "Check counts the openings of a solidified garment", report.Openings == 2, $"{report.Openings}" );
	}

	/// <summary>
	/// Two fans meeting at vertex 0 (a pinch), and vertex 5 next to it inside the second fan. The
	/// pinch is split first; vertex 5 then saw its face's corner as the copy, which the stale edge
	/// map did not know, split a fan that was whole, and the rim put an edge on four faces.
	/// </summary>
	static void TestPinchNextToPinch()
	{
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( 0, 0, 0 ) );
		mesh.AddVertex( new Vec3( -2, 1, 0 ) );
		mesh.AddVertex( new Vec3( -2, 0, 0 ) );
		mesh.AddVertex( new Vec3( -2, -1, 0 ) );
		mesh.AddVertex( new Vec3( 2, -1, 0 ) );
		mesh.AddVertex( new Vec3( 2, 0, 0 ) );
		mesh.AddVertex( new Vec3( 2, 1, 0 ) );
		mesh.AddFace( new[] { 0, 1, 2 } );
		mesh.AddFace( new[] { 0, 2, 3 } );
		mesh.AddFace( new[] { 0, 4, 5 } );
		mesh.AddFace( new[] { 0, 5, 6 } );

		var solid = MeshSolidify.Solidify( mesh, 0.1f );
		var crowded = solid.BuildEdgeFaces().Count( kv => kv.Value.Count > 2 );

		Report.Check( "one copy for the pinch, none for its neighbour", solid.VertexCount == 2 * (mesh.VertexCount + 1), $"{solid.VertexCount} v" );
		Report.Check( "no edge on more than two faces", crowded == 0, $"{crowded}" );
	}

	static void TestHipSpan()
	{
		var rig = CitizenSkeleton.Build();
		var map = new BodyRegions.Map( rig );

		Report.Check( "Citizen has a hips chain", map.Chain( BodyRegion.Hips, 0, out var from, out var to ) );
		Report.Check( "it runs down, not forward", from.z - to.z > 3f && MathF.Abs( to.x - from.x ) < from.z - to.z,
			$"{from} -> {to}" );

		var hip = rig.HeadWorld( rig.IndexOf( "leg_upper_L" ) );
		var front = map.Locate( new Vec3( hip.x + 1f, hip.y - 1f, hip.z + 3f ) );
		Report.Check( "the front of the hip, above the thigh, is hips", front.Region == BodyRegion.Hips, $"{front}" );

		var crotch = map.Locate( new Vec3( hip.x, 0f, hip.z - 0.5f ) );
		Report.Check( "between the legs is the far end of the hips", crotch.Region == BodyRegion.Hips && crotch.T > 0.92f, $"{crotch}" );
	}

	/// <summary>
	/// A default T-shirt on the s&amp;box human, measured against a real one. Skipped (and said so)
	/// on a machine without s&amp;box: the human comes from the citizen addon's source FBX, and a
	/// body grown from bones is not one - its tubes run through each other, which is a different
	/// test. See SboxHuman and TeeMeasure.
	/// </summary>
	static void TestDefaultTShirt()
	{
		var fbx = SboxHuman.Find();

		if ( fbx is null )
		{
			Console.WriteLine( "  (skipped: no s&box install - set SBOX_PATH to dress the s&box human)" );
			return;
		}

		var (skin, rig) = SboxHuman.Load( fbx );

		// Through OBJ, the way a compiled model comes in through Wearer: a surface, no weights.
		var studio = new PartStudio { Rig = rig.Clone() };
		var wearer = studio.Add( new WearerFeature { Name = "human" } );
		wearer.LoadMesh( System.Text.Encoding.UTF8.GetBytes( ObjWriter.Write( skin, "human" ) ) );

		var garment = studio.Add( new GarmentFeature() );
		var (length, sleeve) = GarmentRecipe.Defaults( 0 );
		garment.Length.Value = length;
		garment.Sleeve.Value = sleeve;
		var trim = studio.Add( new TrimFeature() );
		var fur = studio.Add( new FurFeature() );
		fur.Coverage.Index = 1;

		var rebuild = studio.Rebuild();
		Report.Check( "garment, trim and fur build", !rebuild.HasErrors, string.Join( "; ", rebuild.Errors.Select( e => e.Message ) ) );

		var shirt = studio.Bodies.FirstOrDefault( b => b.FeatureId == garment.Id );
		var frill = studio.Bodies.FirstOrDefault( b => b.FeatureId == trim.Id );
		var worn = studio.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).ToList();

		if ( shirt is null || frill is null )
		{
			Report.Check( "a shirt and a frill came out", false );
			return;
		}

		var sheet = MeshSolidify.OuterSheet( shirt.Mesh, out var offset );
		Report.Check( "the shirt is solidified and its sheet is readable", sheet is not null );

		var report = GarmentCheck.Run( shirt.Mesh, worn );
		Report.Check( "four openings: hem, collar, two sleeves", report.Openings == 4, $"{report.Openings}" );

		// Against a real size M (Gildan 5000; a German regular-fit chart for the neck), the size
		// this 37.5-inch chest takes. Within the tolerances the spec sheets give, or near them.
		var m = TeeMeasure.Measure( shirt.Mesh, worn[0], rig );
		Report.Check( "chest within 1.5 in of a size M's 40", MathF.Abs( m.Chest - 40f ) <= 1.5f, $"{m.Chest:0.0}" );
		Report.Check( "body length within 1.5 in of a size M's 29", MathF.Abs( m.BodyLength - 29f ) <= 1.5f, $"{m.BodyLength:0.0}" );
		Report.Check( "neck opening within 1.5 in of a size M's 6.5 across", MathF.Abs( m.NeckWidth - 6.5f ) <= 1.5f, $"{m.NeckWidth:0.0}" );
		Report.Check( "front drop within an inch of 3", MathF.Abs( m.FrontDrop - 3f ) <= 1f, $"{m.FrontDrop:0.0}" );
		Report.Check( "sleeve length within 1.5 in of a size M's 17", MathF.Abs( m.SleeveLengthCB - 17f ) <= 1.5f, $"{m.SleeveLengthCB:0.0}" );
		Report.Check( "no edge on four faces", report.Structure.Count == 0, string.Join( "; ", report.Structure ) );

		// The cloth of the frill, without the fur the Fur feature grew on it too: shells droop by
		// design, and Check in the editor leaves them out the same way.
		var frillCloth = new PolyMesh();
		var remap = new Dictionary<int, int>();
		foreach ( var f in frill.Mesh.Faces.Where( f => f.Material != fur.ResolvedSlot ) )
			frillCloth.AddFace( f.Indices.Select( vi => remap.TryGetValue( vi, out var to ) ? to : remap[vi] = frillCloth.AddVertex( frill.Mesh.Positions[vi] ) ).ToArray(), f.UVs, f.Material );
		frillCloth.Skin = new SkinWeights();
		foreach ( var vi in remap.OrderBy( kv => kv.Value ).Select( kv => kv.Key ) )
			frillCloth.Skin.Vertices.Add( frill.Mesh.Skin?[vi] ?? Array.Empty<BoneWeight>() );
		var frillReport = GarmentCheck.Run( frillCloth, worn );
		Report.Check( "the frill has UVs", frillReport.HasUVs );
		Report.Check( "the frill is off the body", frillReport.Clipping == 0, $"{frillReport.Clipping}, deepest {frillReport.DeepestClip:0.##}"
			+ (frillReport.ClipPoints.Count > 0 ? $", e.g. at {string.Join( " ", frillReport.ClipPoints.Where( ( _, i ) => i % Math.Max( 1, frillReport.ClipPoints.Count / 4 ) == 0 ).Take( 4 ) )}" : "") );

		Report.Check( "fur grew on the openings", fur.ShellFaces > 0, $"{fur.ShellFaces}" );

		// Every shell is grown off a far-wall face, so every shell vertex is further out than the
		// near wall: none of it inside the shirt, which is where the inner wall's fur went.
		var shellVerts = shirt.Mesh.Faces.Where( f => f.Material == fur.ResolvedSlot ).SelectMany( f => f.Indices ).Distinct().ToList();
		Report.Check( "fur is on the outside of the shirt", shellVerts.Count > 0 && offset > 0 && shellVerts.All( vi => vi >= 2 * offset ),
			$"{shellVerts.Count} shell vertices" );
	}
}
