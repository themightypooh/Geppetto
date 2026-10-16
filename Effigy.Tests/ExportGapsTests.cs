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

		Section( "export gaps: FBX blendshapes" );
		TestFbxBlendShapesRoundTrip();
		TestFbxRiggedBlendShapes();
		TestFbxWithoutKeysIsUnchanged();

		Section( "export gaps: wrinkle maps" );
		TestWrinkleValuesFollowTheStrain();
		TestWrinkleDataInTheDmx();
		TestSquishMapsHaveFolds();
		TestWrinklesInTheVmat();
		TestScriptWrinkles();
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

	// --- FBX blendshapes ---------------------------------------------------------------------------

	/// <summary>A sphere with two keys: "swell" pushes the top half out, "pinch" draws the middle in.</summary>
	internal static PolyMesh KeyedSphere( PolyMesh mesh = null )
	{
		mesh ??= Primitives.QuadSphere( 10f, 8 );
		mesh.AddMorph( "swell", mesh.Positions.Select( p => p.z > 0 ? p * 1.3f : p ).ToList() );
		mesh.AddMorph( "pinch", mesh.Positions.Select( p => MathF.Abs( p.z ) < 4f ? new Vec3( p.x * 0.7f, p.y * 0.7f, p.z ) : p ).ToList() );
		return mesh;
	}

	static float WorstShapeError( FbxScene.Mesh read, PolyMesh mesh )
	{
		var worst = 0f;

		foreach ( var morph in mesh.ValidMorphs )
		{
			var shape = read.Shapes.FirstOrDefault( s => s.Name == morph.Name );

			if ( shape is null )
				return float.MaxValue;

			var dense = shape.Dense( read.Points.Length );

			for ( var i = 0; i < morph.Deltas.Length; i++ )
				worst = MathF.Max( worst, (dense[i] - morph.Deltas[i]).Length );
		}

		return worst;
	}

	static void TestFbxBlendShapesRoundTrip()
	{
		var mesh = KeyedSphere();
		var path = Path.Combine( ExportDir( "fbx" ), "keyed.fbx" );
		FbxWriter.WriteBinaryFile( mesh, path, modelName: "keyed" );

		var scene = FbxScene.ReadFile( path );
		var read = scene.Meshes.FirstOrDefault();

		Check( "a keyed mesh's FBX reads back with its two blendshapes, by name", read is not null
			&& read.Shapes.Select( s => s.Name ).SequenceEqual( new[] { "swell", "pinch" } ),
			read is null ? "no mesh" : string.Join( ",", read.Shapes.Select( s => s.Name ) ) );

		if ( read is null )
			return;

		var worst = WorstShapeError( read, mesh );
		Check( "every vertex's offset comes back as the key had it", worst < 1e-4f, $"worst {worst}" );

		var swell = read.Shapes.First( s => s.Name == "swell" );
		var moved = mesh.ValidMorphs.First().Deltas.Count( d => d.Length > 1e-5f );
		Check( "sparse: only the points the key moves are listed", swell.Indexes.Length < read.Points.Length && swell.Indexes.Length >= moved,
			$"{swell.Indexes.Length} of {read.Points.Length}, {moved} moved" );
		Check( "each channel reaches its shape at 100 and is saved off", swell.FullWeight == 100 && swell.DeformPercent == 0 );
		Check( "the normal offsets come too, and turn the swollen top", swell.NormalOffsets.Length == swell.Indexes.Length && swell.NormalOffsets.Any( n => n.Length > 1e-3f ) );

		var ascii = FbxWriter.Write( mesh, skeletonless: true );
		Check( "Definitions count the shapes: 3 geometries and 3 deformers", ascii.Contains( "ObjectType: \"Geometry\", {\n\t\tCount: 3" )
			&& ascii.Contains( "ObjectType: \"Deformer\", {\n\t\tCount: 3" ), Snippet( ascii, "ObjectType: \"Geometry\"" ) );
	}

	static string Snippet( string text, string at )
	{
		var i = text.IndexOf( at, StringComparison.Ordinal );
		return i < 0 ? "(missing)" : text.Substring( i, Math.Min( 80, text.Length - i ) ).Replace( "\n", "\\n" );
	}

	static void TestFbxRiggedBlendShapes()
	{
		var (mesh, skeleton) = RiggedSphere();
		KeyedSphere( mesh );
		var path = Path.Combine( ExportDir( "fbx-rigged" ), "rigged.fbx" );
		FbxWriter.WriteBinaryFile( mesh, path, skeleton, modelName: "rigged" );

		var read = FbxScene.ReadFile( path ).Meshes.FirstOrDefault();
		Check( "a skinned FBX carries the skin and the blendshapes together", read is { Weights: not null } && read.Shapes.Count == 2 );

		if ( read is not null )
		{
			var worst = WorstShapeError( read, mesh );
			Check( "and its offsets survive the bind-pose placement", worst < 1e-4f, $"worst {worst}" );
		}
	}

	static void TestFbxWithoutKeysIsUnchanged()
	{
		var ascii = FbxWriter.Write( Primitives.QuadSphere( 10f, 4 ) );
		Check( "a mesh with no shape keys writes no blendshape at all", !ascii.Contains( "BlendShape" ) && !ascii.Contains( "\"Shape\"" )
			&& ascii.Contains( "ObjectType: \"Geometry\", {\n\t\tCount: 1" ) );
	}

	// --- wrinkle maps ------------------------------------------------------------------------------

	static void TestWrinkleValuesFollowTheStrain()
	{
		var sheet = SeamSheet( 20 );
		var squash = new MeshMorph { Name = "squash", Deltas = sheet.Positions.Select( p => p.x > 2f ? new Vec3( -(p.x - 2f) * 0.4f, 0, 0 ) : Vec3.Zero ).ToArray() };
		var stretch = new MeshMorph { Name = "stretch", Deltas = sheet.Positions.Select( p => p.x > 2f ? new Vec3( (p.x - 2f) * 0.15f, 0, 0 ) : Vec3.Zero ).ToArray() };
		var slide = new MeshMorph { Name = "slide", Deltas = sheet.Positions.Select( _ => new Vec3( 0, 0, 3f ) ).ToArray() };

		float Mean( float[] values, Func<Vec3, bool> where ) =>
			Enumerable.Range( 0, sheet.VertexCount ).Where( i => where( sheet.Positions[i] ) ).Average( i => values[i] );

		var sq = WrinkleMaps.VertexValues( sheet, squash );
		var st = WrinkleMaps.VertexValues( sheet, stretch );
		var sl = WrinkleMaps.VertexValues( sheet, slide );

		// 40% shorter is past the 25% that counts as full; 15% longer is 0.6 of full.
		Check( "a squashed patch is -1 (squish maps)", Mean( sq, p => p.x > 5f ) < -0.95f, $"{Mean( sq, p => p.x > 5f )}" );
		Check( "a stretched patch is positive, in proportion (0.6 for 15%)", MathF.Abs( Mean( st, p => p.x > 5f ) - 0.6f ) < 0.08f, $"{Mean( st, p => p.x > 5f )}" );
		Check( "the untouched half stays 0", MathF.Abs( Mean( sq, p => p.x < -3f ) ) < 0.02f && MathF.Abs( Mean( st, p => p.x < -3f ) ) < 0.02f );
		Check( "a key that only moves the skin wrinkles nothing", sl.All( v => MathF.Abs( v ) < 1e-4f ) );
		Check( "every value is within -1..1", sq.Concat( st ).All( v => v >= -1f && v <= 1f ) );
	}

	static void TestWrinkleDataInTheDmx()
	{
		var mesh = KeyedSphere();
		var wrinkles = WrinkleMaps.All( mesh );
		var plain = DmxWriter.Write( mesh );
		var dmx = DmxWriter.Write( mesh, wrinkles: wrinkles );

		Check( "without wrinkles the DMX is as before (no wrinkle stream, scales 0)", !plain.Contains( "wrinkle$0" ) && plain.Contains( "\"wrinkleScales\" \"float_array\"" ) );
		Check( "with them, each key's delta state lists wrinkle$0 and its indices", Count( dmx, "\"wrinkle$0\"" ) == 4 && Count( dmx, "\"wrinkle$0Indices\"" ) == 2,
			$"{Count( dmx, "\"wrinkle$0\"" )} / {Count( dmx, "\"wrinkle$0Indices\"" )}" );
		Check( "and it still parses as KeyValues2", DmxGrammarTests.Parse( dmx, out var e ) is not null, e );

		// Read the pinch key's values back and compare with what was computed.
		var at = dmx.IndexOf( "\"DmeVertexDeltaData\"", dmx.IndexOf( "\"pinch\"", StringComparison.Ordinal ) - 200, StringComparison.Ordinal );
		var block = dmx[dmx.IndexOf( "\"wrinkle$0\" \"float_array\"", at, StringComparison.Ordinal )..];
		var values = Numbers( block );
		var indices = Numbers( block[block.IndexOf( "\"wrinkle$0Indices\"", StringComparison.Ordinal )..] ).Select( v => (int)v ).ToList();
		var pinch = wrinkles["pinch"];
		var worst = indices.Count == values.Count && indices.Count > 0
			? indices.Select( ( v, k ) => MathF.Abs( pinch[v] - values[k] ) ).Max() : float.MaxValue;

		Check( "the pinch key's values come back as computed, and its pinched waist is negative", worst < 1e-4f && values.Min() < -0.5f,
			$"{indices.Count} indices, {values.Count} values, worst {worst}" );
		var scales = System.Text.RegularExpressions.Regex.Matches( dmx, "\"wrinkleScales\" \"float_array\"\\s*\\[\\s*\"([^\"]*)\"" ).Select( m => m.Groups[1].Value ).ToList();
		var plainScales = System.Text.RegularExpressions.Regex.Matches( plain, "\"wrinkleScales\" \"float_array\"\\s*\\[\\s*\"([^\"]*)\"" ).Select( m => m.Groups[1].Value ).ToList();
		Check( "keys with wrinkle data get wrinkleScales 1 (0 without)", scales.Count == 2 && scales.All( v => v == "1" ) && plainScales.Count == 2 && plainScales.All( v => v == "0" ),
			$"{string.Join( ",", scales )} / {string.Join( ",", plainScales )}" );
	}

	/// <summary>The numbers of the first array in <paramref name="text"/>: the quoted values between its [ and ].</summary>
	static List<float> Numbers( string text )
	{
		var open = text.IndexOf( '[' );
		var close = text.IndexOf( ']', open );
		return text[(open + 1)..close].Split( new[] { ',', '\n', '\t', ' ', '"' }, StringSplitOptions.RemoveEmptyEntries )
			.Select( t => float.Parse( t, System.Globalization.CultureInfo.InvariantCulture ) ).ToList();
	}

	static void TestSquishMapsHaveFolds()
	{
		var mesh = KeyedSphere();
		var baked = WrinkleMaps.Bake( mesh, new WrinkleMaps.Settings { Size = 128 } );

		Check( "the squish maps bake on the sphere's UVs", baked.SquishNormal is not null && baked.SquishAo is not null && baked.Covered > 128 * 128 / 4, $"{baked.Covered} texels" );

		if ( baked.SquishNormal is null )
			return;

		var bent = 0;
		var dark = 0;

		for ( var i = 0; i < 128 * 128; i++ )
		{
			if ( Math.Abs( baked.SquishNormal.Rgb[i * 3] - 128 ) > 6 || Math.Abs( baked.SquishNormal.Rgb[i * 3 + 1] - 128 ) > 6 ) bent++;
			if ( baked.SquishAo.Rgb[i * 3] < 200 ) dark++;
		}

		Check( "the pinched waist folds: some texels' normals tilt", baked.Squashed > 0 && bent > 50, $"{baked.Squashed} squashed, {bent} tilted" );
		Check( "and the valleys darken in the AO", dark > 20, $"{dark}" );
		Check( "where nothing is squashed the skin stays flat", bent < baked.Covered, $"{bent} of {baked.Covered}" );

		var none = WrinkleMaps.Bake( new PolyMesh( Primitives.QuadSphere( 5f, 4 ).Positions, Primitives.QuadSphere( 5f, 4 ).Faces.Select( f => new Face( f.Indices, null, 0 ) ).ToList() ) );
		Check( "a mesh with no UVs bakes nothing (and says so in an export)", none.SquishNormal is null );
	}

	static void TestWrinklesInTheVmat()
	{
		var skin = SkinMaterial.VmatSource( SkinMaterial.Head(), color: "models/x/head_color.png", ambientOcclusion: "models/x/head_ao.png" );
		var on = WrinkleMaps.EnableInVmat( skin, "models/x/head_squish_normal.png", "models/x/head_squish_ao.png" );

		Check( "F_WRINKLE goes on an Effigy skin vmat, under its shader line", on is not null && on.Contains( "F_WRINKLE 1" ) && on.IndexOf( "F_WRINKLE" ) > on.IndexOf( "shader \"" ) && on.IndexOf( "F_WRINKLE" ) < on.IndexOf( "//---- Ambient" ) );
		Check( "with all six textures: squish maps given, the material's colour on both sides, flat stretched skin",
			on is not null && on.Contains( "TextureSquishNormal \"models/x/head_squish_normal.png\"" ) && on.Contains( "TextureSquishAmbientOcclusion \"models/x/head_squish_ao.png\"" )
			&& on.Contains( "TextureSquishColor \"models/x/head_color.png\"" ) && on.Contains( "TextureStretchColor \"models/x/head_color.png\"" )
			&& on.Contains( $"TextureStretchNormal \"{WrinkleMaps.FlatNormal}\"" ) && on.Contains( "TextureStretchAmbientOcclusion \"models/x/head_ao.png\"" ) );
		Check( "inside the layer's braces", on is not null && on.TrimEnd().EndsWith( "}" ) && on.IndexOf( "TextureStretchNormal" ) < on.LastIndexOf( '}' ) && Count( on, "{" ) == Count( on, "}" ) );
		Check( "turning it on twice changes nothing", on is not null && WrinkleMaps.EnableInVmat( on, "a", "b" ) == on );

		var complex = FleshMaterial.VmatSource( "models/x/c.png", "models/x/r.png" );
		Check( "it goes on a complex vmat too", WrinkleMaps.EnableInVmat( complex, "n", "a" )?.Contains( "F_WRINKLE 1" ) == true );
		Check( "never on a vmat Effigy did not write", WrinkleMaps.EnableInVmat( "Layer0\n{\n\tshader \"shaders/skin.shader\"\n}\n", "n", "a" ) is null );
		Check( "nor on a shader with no wrinkle maps (fur)", WrinkleMaps.EnableInVmat( "// AUTO-GENERATED\nLayer0\n{\n\tshader \"shaders/fur.shader\"\n}\n", "n", "a" ) is null );
	}

	static void TestScriptWrinkles()
	{
		var root = ExportDir( "wrinkles" );
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Primitive name=Sac shape=Sphere radius=10 divisions=10 position=0,0,10\nadd UVProject bodies=Sac mode=Unwrap\n"
			+ "add ShapeKey key=squeeze bodies=Sac\nadd Stroke bodies=Sac brush=Pinch radius=6 strength=1 points=0,-10,10;0,-9,10\nadd ShapeKeyEnd\n" );
		studio.Rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 20 ) );

		Directory.CreateDirectory( Path.Combine( root, "materials" ) );
		File.WriteAllText( Path.Combine( root, "materials", "sac.vmat" ), SkinMaterial.VmatSource( SkinMaterial.WetFlesh() ) );
		studio.MaterialNames[0] = "materials/sac.vmat";

		r = BuildScript.Run( studio, "export models/test/sac wrinkles=1 lods=1", root );
		var folder = Path.Combine( root, "models/test/sac" );
		string Text( string f ) => File.Exists( Path.Combine( folder, f ) ) ? File.ReadAllText( Path.Combine( folder, f ) ) : "";
		var vmat = File.ReadAllText( Path.Combine( root, "materials", "sac.vmat" ) );

		Check( "export wrinkles=1 writes the wrinkle stream into the skinned dmx", r.Ok && Text( "sac.dmx" ).Contains( "\"wrinkle$0\"" ), r.ToString() );
		Check( "and into each skinned level", Text( "sac_lod1.dmx" ).Contains( "\"wrinkle$0\"" ) && Text( "sac_lod2.dmx" ).Contains( "\"wrinkle$0\"" ) );
		Check( "bakes the squish maps beside it", File.Exists( Path.Combine( folder, "sac_squish_normal.png" ) ) && File.Exists( Path.Combine( folder, "sac_squish_ao.png" ) ) );
		Check( "and turns wrinkles on in the model's Effigy material, pointing at them", vmat.Contains( "F_WRINKLE 1" ) && vmat.Contains( "models/test/sac/sac_squish_normal.png" ) );
		Check( "the log says so", r.Log.Any( l => l.Contains( "wrinkle maps for 1 shape key" ) ), string.Join( " | ", r.Log ) );

		var refused = BuildScript.Run( new PartStudio(), "add Primitive name=B shape=Box\nexport m/x wrinkles=1", ExportDir( "bad" ) );
		Check( "wrinkles=1 on a static export is refused with the reason", !refused.Ok && refused.Error.Contains( "skinned export" ), refused.Error );

		var noKeys = new PartStudio();
		BuildScript.Run( noKeys, "add Primitive name=B shape=Sphere radius=5 divisions=6" );
		noKeys.Rig.AddBoneFromPoints( "root", -1, Vec3.Zero, new Vec3( 0, 0, 5 ) );
		var keyless = BuildScript.Run( noKeys, "export m/y wrinkles=1", ExportDir( "nokeys" ) );
		Check( "and on a model with no shape keys, with what to do", !keyless.Ok && keyless.Error.Contains( "shape key" ), keyless.Error );
	}
}
