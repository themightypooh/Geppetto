using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The export gaps closed together: LODs triangulated the bake's way, UV seams held through
/// decimation, skinned LODs, FBX blendshapes and per-vertex wrinkle data. Everything here is
/// checked on numbers read back out of the written files, not on the writer's own arrays.
/// </summary>
public static class ExportGapsTests
{
	public static void Run()
	{
		Section( "export gaps: decimation holds UV seams" );
		TestSeamStaysOnItsLine();
		TestSeamUVsFollowTheSurface();
		TestSeamCornersArePinned();
		TestShapeKeysRideTheDecimation();

		Section( "export gaps: levels of detail" );
		TestScriptStaticLodsAreTriangulated();
		TestSkinnedLevelsKeepTheirWeights();
		TestScriptSkinnedLods();
		TestSkinnedLodVmdl();
	}

	// --- UV-seam-aware decimation ----------------------------------------------------------------

	/// <summary>Planar mapping of a 20 x 20 sheet split down x = 0 into two UV islands: the left half
	/// lands in u 0..0.45, the right in 0.55..1. Exact and linear, so any UV the decimation produces
	/// can be checked against where its vertex actually is.</summary>
	static Vec2 SheetUV( Vec3 p, bool right ) =>
		right ? new Vec2( 0.55f + 0.45f * p.x / 10f, (p.y + 10f) / 20f ) : new Vec2( 0.45f * (p.x + 10f) / 10f, (p.y + 10f) / 20f );

	/// <summary>The two-island sheet, <paramref name="n"/> quads a side, its seam vertices shared
	/// (one position, two UVs). <paramref name="bump"/> lifts it into a dome so collapses cost something.</summary>
	internal static PolyMesh SeamSheet( int n = 24, float bump = 0f )
	{
		var mesh = new PolyMesh();
		var index = new int[n + 1, n + 1];

		for ( var j = 0; j <= n; j++ )
		{
			for ( var i = 0; i <= n; i++ )
			{
				var x = -10f + 20f * i / n;
				var y = -10f + 20f * j / n;
				var z = bump * MathF.Cos( x * 0.15f ) * MathF.Cos( y * 0.15f );
				index[i, j] = mesh.AddVertex( new Vec3( x, y, z ) );
			}
		}

		for ( var j = 0; j < n; j++ )
		{
			for ( var i = 0; i < n; i++ )
			{
				var ids = new[] { index[i, j], index[i + 1, j], index[i + 1, j + 1], index[i, j + 1] };
				var right = i >= n / 2;
				var uvs = ids.Select( v => SheetUV( mesh.Positions[v], right ) ).ToArray();
				mesh.AddFace( ids, uvs );
			}
		}

		return mesh;
	}

	/// <summary>Vertices that carry more than one UV - the seam - in a decimated mesh.</summary>
	static HashSet<int> SeamVertices( PolyMesh mesh )
	{
		var uvs = new Dictionary<int, List<Vec2>>();

		foreach ( var f in mesh.Faces )
		{
			for ( var c = 0; c < f.Count; c++ )
			{
				if ( !uvs.TryGetValue( f.Indices[c], out var list ) )
					uvs[f.Indices[c]] = list = new List<Vec2>();

				if ( !list.Any( u => MathF.Abs( u.x - f.UVs[c].x ) < 1e-5f && MathF.Abs( u.y - f.UVs[c].y ) < 1e-5f ) )
					list.Add( f.UVs[c] );
			}
		}

		return uvs.Where( kv => kv.Value.Count > 1 ).Select( kv => kv.Key ).ToHashSet();
	}

	static void TestSeamStaysOnItsLine()
	{
		var sheet = SeamSheet( 24, bump: 3f );
		var result = Decimate.Run( sheet, new Decimate.Options { Ratio = 0.25f } );
		var seam = SeamVertices( result.Mesh );
		var off = seam.Select( v => MathF.Abs( result.Mesh.Positions[v].x ) ).DefaultIfEmpty( 0f ).Max();

		Check( "a two-island sheet still decimates to a quarter", result.ToTriangles <= Decimate.TriangleCount( sheet ) / 4 + 2,
			$"{result.FromTriangles} -> {result.ToTriangles}" );
		Check( "the seam is still there", seam.Count >= 2, $"{seam.Count} seam vertices" );
		Check( "every seam vertex stays on the seam line (x = 0)", off < 1e-4f, $"worst {off}" );

		// Every surviving seam vertex has its two sides' UVs; the islands still meet nowhere else.
		var stray = SeamVertices( result.Mesh ).Count( v => MathF.Abs( result.Mesh.Positions[v].x ) > 1e-4f );
		Check( "no vertex off the seam picked up a second UV", stray == 0, $"{stray}" );
	}

	static void TestSeamUVsFollowTheSurface()
	{
		// Flat, so the planar mapping is exact everywhere and the only error is the decimation's.
		var sheet = SeamSheet( 24 );
		var held = Decimate.Run( sheet, new Decimate.Options { Ratio = 0.2f } ).Mesh;
		var loose = Decimate.Run( sheet, new Decimate.Options { Ratio = 0.2f, PreserveUVSeams = false } ).Mesh;

		static float WorstUV( PolyMesh m )
		{
			var worst = 0f;

			foreach ( var f in m.Faces )
			{
				var centre = f.Indices.Aggregate( Vec3.Zero, ( s, v ) => s + m.Positions[v] ) / f.Count;

				for ( var c = 0; c < f.Count; c++ )
				{
					var want = SheetUV( m.Positions[f.Indices[c]], centre.x > 0f );
					worst = MathF.Max( worst, MathF.Max( MathF.Abs( want.x - f.UVs[c].x ), MathF.Abs( want.y - f.UVs[c].y ) ) );
				}
			}

			return worst;
		}

		var heldError = WorstUV( held );
		var looseError = WorstUV( loose );

		Check( "with seams held, every corner's UV is where its vertex is on its own island", heldError < 1e-4f, $"worst {heldError}" );
		Check( "(without, the islands smear: the old behaviour is measurably wrong)", looseError > 0.01f, $"worst {looseError}" );
		Check( "both islands keep their UV extents", held.Faces.SelectMany( f => f.UVs ).Max( u => u.x ) > 0.999f
			&& held.Faces.SelectMany( f => f.UVs ).Min( u => u.x ) < 0.001f
			&& held.Faces.SelectMany( f => f.UVs ).Where( u => u.x < 0.5f ).Max( u => u.x ) > 0.449f
			&& held.Faces.SelectMany( f => f.UVs ).Where( u => u.x > 0.5f ).Min( u => u.x ) < 0.551f );
	}

	static void TestSeamCornersArePinned()
	{
		// Where the seam meets the open border (0, ±10) three constrained edges meet: those two
		// points must survive exactly, as must the sheet's four corners.
		var reduced = Decimate.Run( SeamSheet( 24, bump: 2f ), new Decimate.Options { Ratio = 0.1f } ).Mesh;

		bool Has( float x, float y ) => reduced.Positions.Any( p => MathF.Abs( p.x - x ) < 1e-5f && MathF.Abs( p.y - y ) < 1e-5f );

		Check( "the seam's two ends on the border survive", Has( 0f, -10f ) && Has( 0f, 10f ) );
		Check( "and so do the sheet's corners", Has( -10f, -10f ) && Has( 10f, -10f ) && Has( 10f, 10f ) && Has( -10f, 10f ) );
		var v = MeshValidator.Validate( reduced );
		Check( "the reduced sheet is still a valid mesh", v.IsValid, v.ToString() );
	}

	static void TestShapeKeysRideTheDecimation()
	{
		// A dome key: the decimated mesh's key must still put each surviving vertex where the
		// full mesh's key put that point (the key is a smooth function of position here).
		var sheet = SeamSheet( 20 );
		sheet.AddMorph( "lift", sheet.Positions.Select( p => p + new Vec3( 0, 0, 2f - 0.01f * (p.x * p.x + p.y * p.y) ) ).ToList() );

		var reduced = Decimate.Run( sheet, new Decimate.Options { Ratio = 0.3f } ).Mesh;
		var morph = reduced.ValidMorphs.FirstOrDefault();

		Check( "the shape key comes through decimation", morph is { Name: "lift" } && morph.Deltas.Length == reduced.VertexCount );

		if ( morph is null )
			return;

		var worst = Enumerable.Range( 0, reduced.VertexCount ).Max( i =>
		{
			var p = reduced.Positions[i];
			return MathF.Abs( morph.Deltas[i].z - (2f - 0.01f * (p.x * p.x + p.y * p.y)) );
		} );

		Check( "and still says how far each surviving vertex moves", worst < 0.15f, $"worst {worst}" );
	}

	// --- levels of detail ------------------------------------------------------------------------

	static string ExportDir( string what )
	{
		var dir = Path.Combine( Path.GetTempPath(), "effigy-export-gaps", what + "-" + Guid.NewGuid().ToString( "N" )[..6] );
		Directory.CreateDirectory( dir );
		return dir;
	}

	static int Count( string text, string what )
	{
		var n = 0;
		for ( var i = text.IndexOf( what, StringComparison.Ordinal ); i >= 0; i = text.IndexOf( what, i + what.Length, StringComparison.Ordinal ) )
			n++;
		return n;
	}

	/// <summary>Every `f` line of an OBJ has three corners.</summary>
	static bool AllTriangles( string obj ) =>
		obj.Split( '\n' ).Where( l => l.StartsWith( "f " ) ).All( l => l.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Length == 4 );

	static void TestScriptStaticLodsAreTriangulated()
	{
		var dir = ExportDir( "static" );
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Primitive name=Ball shape=Sphere radius=10 divisions=8\nadd UVProject bodies=Ball mode=Unwrap\nexport models/test/ball lods=1", dir );
		var folder = Path.Combine( dir, "models/test/ball" );
		string Text( string f ) => File.Exists( Path.Combine( folder, f ) ) ? File.ReadAllText( Path.Combine( folder, f ) ) : "";

		Check( "export lods=1 writes the model and two levels", r.Ok && Text( "ball.obj" ).Length > 0 && Text( "ball_lod1.obj" ).Length > 0 && Text( "ball_lod2.obj" ).Length > 0, r.ToString() );
		Check( "the full-detail OBJ is triangulated", Text( "ball.obj" ).Length > 0 && AllTriangles( Text( "ball.obj" ) ) );
		Check( "and so is every level", AllTriangles( Text( "ball_lod1.obj" ) ) && AllTriangles( Text( "ball_lod2.obj" ) ) );

		// The bake's split: as many triangles, corner for corner, as NormalBake.FaceTriangles makes.
		var source = studio.ToMesh();
		var bakeTriangles = Enumerable.Range( 0, source.FaceCount ).Sum( f => NormalBake.FaceTriangles( source, f ).Count );
		Check( "split the bake's way (same triangle count as NormalBake.FaceTriangles)", Count( Text( "ball.obj" ), "\nf " ) == bakeTriangles,
			$"{Count( Text( "ball.obj" ), "\nf " )} vs {bakeTriangles}" );

		var vmdl = Text( "ball.vmdl" );
		Check( "the vmdl switches three levels", Count( vmdl, "_class = \"LODGroup\"" ) == 3 && vmdl.Contains( "ball_lod2.obj" ), Count( vmdl, "LODGroup\"" ).ToString() );
	}

	/// <summary>A tall quad sphere bound to two bones by height: the top half to "upper", the bottom
	/// to "lower", blended linearly across a band - so the right weight is a function of z alone.</summary>
	internal static (PolyMesh Mesh, Skeleton Skeleton) RiggedSphere()
	{
		var mesh = Primitives.QuadSphere( 10f, 12 );
		var skeleton = new Skeleton();
		var lower = skeleton.AddBoneFromPoints( "lower", -1, new Vec3( 0, 0, -10 ), new Vec3( 0, 0, 0 ) );
		skeleton.AddBoneFromPoints( "upper", lower, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 10 ) );

		var skin = new SkinWeights();

		foreach ( var p in mesh.Positions )
		{
			var w = UpperWeight( p.z );
			skin.Vertices.Add( w <= 0f ? new[] { new BoneWeight( 0, 1f ) } : w >= 1f ? new[] { new BoneWeight( 1, 1f ) }
				: new[] { new BoneWeight( 0, 1f - w ), new BoneWeight( 1, w ) } );
		}

		mesh.Skin = skin;
		return (mesh, skeleton);
	}

	static float UpperWeight( float z ) => Math.Clamp( (z + 4f) / 8f, 0f, 1f );

	static void TestSkinnedLevelsKeepTheirWeights()
	{
		var (mesh, _) = RiggedSphere();
		var levels = LodExport.Build( mesh, LodExport.DefaultRatios );

		Check( "two skinned levels, each smaller than the last", levels.Count == 2 && levels[0].Triangles < Decimate.TriangleCount( mesh ) && levels[1].Triangles < levels[0].Triangles,
			string.Join( ",", levels.Select( l => l.Triangles ) ) );

		foreach ( var level in levels )
		{
			var m = level.Mesh;
			var problems = m.IsRigged ? m.Skin.Validate( m.VertexCount, 2 ) : new List<string> { "no skin" };
			Check( $"level {level.Index} carries a valid skin", problems.Count == 0, string.Join( "; ", problems.Take( 3 ) ) );

			// Each weight is the blend of its two collapsed ends, so it tracks the height ramp.
			var worst = 0f;
			for ( var i = 0; i < m.VertexCount; i++ )
			{
				var upper = m.Skin[i].Where( b => b.Bone == 1 ).Sum( b => b.Weight );
				worst = MathF.Max( worst, MathF.Abs( upper - UpperWeight( m.Positions[i].z ) ) );
			}

			Check( $"level {level.Index}'s weights still follow the bones (within 0.2 of the ramp)", worst < 0.2f, $"worst {worst}" );
		}

		Check( "the levels switch at 6 and 15 times the model's size", MathF.Abs( levels[0].SwitchDistance - 6f * mesh.BoundsDiagonal ) < 1e-3f
			&& MathF.Abs( levels[1].SwitchDistance - 15f * mesh.BoundsDiagonal ) < 1e-3f );
	}

	static void TestScriptSkinnedLods()
	{
		var studio = new PartStudio();
		var dir = ExportDir( "skinned" );
		var r = BuildScript.Run( studio, "add Primitive name=Leg shape=Sphere radius=10 divisions=8 position=0,0,10\nadd UVProject bodies=Leg mode=Unwrap" );
		var hip = studio.Rig.AddBoneFromPoints( "hip", -1, new Vec3( 0, 0, 20 ), new Vec3( 0, 0, 10 ) );
		studio.Rig.AddBoneFromPoints( "knee", hip, new Vec3( 0, 0, 10 ), new Vec3( 0, 0, 0 ) );
		r = BuildScript.Run( studio, "export models/test/leg lods=0.5,0.25 loddistance=200,500", dir );

		var folder = Path.Combine( dir, "models/test/leg" );
		string Text( string f ) => File.Exists( Path.Combine( folder, f ) ) ? File.ReadAllText( Path.Combine( folder, f ) ) : "";

		Check( "a skinned export with lods= writes skinned levels", r.Ok && Text( "leg_lod1.dmx" ).Length > 0 && Text( "leg_lod2.dmx" ).Length > 0
			&& Text( "leg_lod1.smd" ).Length > 0, r.ToString() );

		foreach ( var f in new[] { "leg.dmx", "leg_lod1.dmx", "leg_lod2.dmx" } )
		{
			var dmx = Text( f );
			Check( $"{f} parses and carries the weights and both bones", DmxGrammarTests.Parse( dmx, out var e ) is not null
				&& dmx.Contains( "blendweights$0" ) && dmx.Contains( "\"hip\"" ) && dmx.Contains( "\"knee\"" ), e );
		}

		Check( "each level is lighter than the one before", Text( "leg_lod1.dmx" ).Length < Text( "leg.dmx" ).Length && Text( "leg_lod2.dmx" ).Length < Text( "leg_lod1.dmx" ).Length );

		var vmdl = Text( "leg.vmdl" );
		Check( "the vmdl lists the three skinned meshes in LOD groups at the given distances", Count( vmdl, "_class = \"LODGroup\"" ) == 3
			&& vmdl.Contains( "models/test/leg/leg_lod2.dmx" ) && vmdl.Contains( "switch_threshold = 200.0" ) && vmdl.Contains( "switch_threshold = 500.0" ),
			Count( vmdl, "LODGroup\"" ).ToString() );
		Check( "and marks the bones once, for every level", Count( vmdl, "\"hip\"" ) >= 1 && vmdl.Contains( "BoneMarkupList" ) && Count( vmdl, "BoneMarkupList" ) == 1 );
		Check( "the log says what each level came to", r.Log.Any( l => l.Contains( "skinned LODs" ) && l.Contains( "tris from 200" ) ), string.Join( " | ", r.Log ) );
	}

	static void TestSkinnedLodVmdl()
	{
		var (_, skeleton) = RiggedSphere();
		var vmdl = VmdlDocument.SkinnedWithLods( new[] { "m/a.dmx", "m/a_lod1.dmx" }, new[] { 0f, 120f }, skeleton );
		Check( "SkinnedWithLods: two meshes, two groups, no OBJ import turn", Count( vmdl, "RenderMeshFile" ) == 2 && Count( vmdl, "\"LODGroup\"" ) == 2
			&& !vmdl.Contains( "-90.0" ) && vmdl.Contains( "Body_LOD1" ) );

		var refused = false;
		try { VmdlDocument.SkinnedWithLods( new[] { "m/a.dmx" }, new[] { 0f, 1f }, skeleton ); } catch ( ArgumentException ) { refused = true; }
		Check( "and refuses a distance list that does not match", refused );
	}
}
