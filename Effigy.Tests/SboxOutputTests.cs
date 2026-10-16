using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// What Effigy hands s&amp;box is what s&amp;box will shade: the normal map's green convention, the
/// bake's triangulation and tangents on the way out, vertex colour that survives as data, and the
/// four-bone weight cap in every writer.
///
/// THE ENGINE IS EMULATED, NOT TRUSTED TO AGREE. The checks below rebuild the tangent frame the way
/// s&amp;box's own C# builds it (PolygonMesh.CalcTriangleTangentSpace / CalcTangentAndFlipFromBasis in
/// sbox-public) and decode a texel the way core/shaders/common/utils/normal.hlsl does (negate y,
/// then T·x + V·y + N·z with V = cross(N, T)·w). A map that comes back as the sculpt's normal through
/// that path is right in the engine; one that comes back mirrored is the inverted-green bug.
///
/// The tangents are pinned against MikkTSpace itself: reference tangents from Blender's
/// mesh.calc_tangents() (which IS mikktspace.c) over the same triangulated fixture.
/// </summary>
public static class SboxOutputTests
{
	public static void Run( string outDir )
	{
		Report.Section( "s&box output: green channel, triangulation, tangents, vertex colour, weight cap" );
		var dir = Path.Combine( outDir, "sbox" );
		Directory.CreateDirectory( dir );

		TestBakedDomeShadesAsABumpInTheEngine();
		TestFabricNormalsFollowTheWeaveInTheEngine();
		TestExportTangentsMatchMikkTSpace( dir );
		TestExportsCarryTheBakesTriangles();
		TestDmxCarriesTangents();
		TestFbxCarriesTangentsAndColour();
		TestPackedMasksSurviveExport();
		TestPerVertexFleshSaysTheGameIgnoresIt();
		TestFbxCapsWeightsAtFour();
		TestSkinVmatIsFacepunchs();
		TestTransmissionFollowsThickness();
		TestBakeSetWritesMapsAndVmat( dir );
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );

	// --- the engine's side, ported ------------------------------------------------------------------

	/// <summary>
	/// s&amp;box's per-corner tangent for one triangle, ported line for line from
	/// PolygonMesh.CalcTriangleTangentSpace + CalcTangentAndFlipFromBasis (sbox-public,
	/// engine/Sandbox.Engine/Scene/Components/Mesh/PolygonMesh.cs).
	/// </summary>
	internal static Vec4 EngineTangent( Vec3 p0, Vec3 p1, Vec3 p2, Vec2 t0, Vec2 t1, Vec2 t2, Vec3 normal )
	{
		float ds1 = t1.x - t0.x, dt1 = t1.y - t0.y;
		float ds2 = t2.x - t0.x, dt2 = t2.y - t0.y;
		float sx = 0, sy = 0, sz = 0, tx = 0, ty = 0, tz = 0;

		void Axis( float a1, float a2, ref float s, ref float t )
		{
			var e1 = new Vec3( a1, ds1, dt1 );
			var e2 = new Vec3( a2, ds2, dt2 );
			var c = Vec3.Cross( e1, e2 );

			if ( MathF.Abs( c.x ) > 1e-12f )
			{
				s += -c.y / c.x;
				t += -c.z / c.x;
			}
		}

		Axis( p1.x - p0.x, p2.x - p0.x, ref sx, ref tx );
		Axis( p1.y - p0.y, p2.y - p0.y, ref sy, ref ty );
		Axis( p1.z - p0.z, p2.z - p0.z, ref sz, ref tz );

		var tangentU = new Vec3( sx, sy, sz );
		var tangentV = new Vec3( tx, ty, tz );
		var leftHanded = Vec3.Dot( Vec3.Cross( tangentU, tangentV ), normal ) < 0f;
		var fromU = Vec3.Cross( Vec3.Cross( normal, tangentU ), normal ).Normal;
		var fromV = (leftHanded ? Vec3.Cross( normal, tangentV ) : Vec3.Cross( tangentV, normal )).Normal;
		var t = (fromU + fromV).Normal;
		return new Vec4( t.x, t.y, t.z, leftHanded ? -1f : 1f );
	}

	/// <summary>normal.hlsl TransformNormal, with the vertex shader's V = cross(N, T)·w.</summary>
	internal static Vec3 EngineDecode( (byte R, byte G, byte B) texel, Vec3 n, Vec4 tangent )
	{
		var ts = new Vec3( texel.R / 127.5f - 1f, texel.G / 127.5f - 1f, texel.B / 127.5f - 1f );
		var t = new Vec3( tangent.x, tangent.y, tangent.z );
		var v = Vec3.Cross( n, t ) * tangent.w;
		ts = new Vec3( ts.x, -ts.y, ts.z );
		return (t.Normal * ts.x + v.Normal * ts.y + n.Normal * ts.z).Normal;
	}

	// --- 1. green ------------------------------------------------------------------------------------

	static float DomeHeight( float x, float y )
	{
		var r = MathF.Sqrt( x * x + y * y );
		if ( r >= 0.6f ) return 0f;
		var t = 1f - r / 0.6f;
		return 0.2f * t * t * (3f - 2f * t);
	}

	static Vec3 DomeNormal( float x, float y )
	{
		const float h = 1e-3f;
		var dx = (DomeHeight( x + h, y ) - DomeHeight( x - h, y )) / (2f * h);
		var dy = (DomeHeight( x, y + h ) - DomeHeight( x, y - h )) / (2f * h);
		return new Vec3( -dx, -dy, 1f ).Normal;
	}

	/// <summary>A flat 2×2 cage at z = 0 (u along +x, v along +y) and a dense copy with a dome.</summary>
	static (PolyMesh Low, PolyMesh High) DomeFixture()
	{
		var low = Primitives.Plane( 2f, 2f, 4, 4 );
		var high = Primitives.Plane( 2f, 2f, 96, 96 );

		for ( var i = 0; i < high.VertexCount; i++ )
		{
			var p = high.Positions[i];
			high.Positions[i] = new Vec3( p.x, p.y, DomeHeight( p.x, p.y ) );
		}

		return (low, high);
	}

	static void TestBakedDomeShadesAsABumpInTheEngine()
	{
		const int size = 64;
		var (low, high) = DomeFixture();
		var map = NormalBake.Bake( low, high, size );
		var dx = NormalBake.Bake( low, high, size, new BakeOptions { FlipGreen = true } );

		// The engine's frame on this cage: every triangle of a flat plane gives the same one.
		var f = low.Faces[0];
		var frame = EngineTangent( low.Positions[f.Indices[0]], low.Positions[f.Indices[1]], low.Positions[f.Indices[2]],
			f.UVs[0], f.UVs[1], f.UVs[2], new Vec3( 0, 0, 1 ) );
		Check( "the ported engine frame on the fixture is right-handed, T along +u", frame.w > 0f && frame.x > 0.99f, frame.ToString() );

		float worst = 0f, worstDx = 0f;
		var flank = 0;

		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				// Texel centre to the plane: u = x/size → world x, v = y/size → world y.
				var wx = ((x + 0.5f) / size - 0.5f) * 2f;
				var wy = ((y + 0.5f) / size - 0.5f) * 2f;
				var r = MathF.Sqrt( wx * wx + wy * wy );

				// The dome's flank, away from its rim and its flat top, where the lean is large.
				if ( r < 0.15f || r > 0.5f )
					continue;

				flank++;
				var truth = DomeNormal( wx, wy );
				var shaded = EngineDecode( map.At( x, y ), new Vec3( 0, 0, 1 ), frame );
				var shadedDx = EngineDecode( dx.At( x, y ), new Vec3( 0, 0, 1 ), frame );
				worst = MathF.Max( worst, Degrees( shaded, truth ) );
				worstDx = MathF.Max( worstDx, Degrees( shadedDx, truth ) );
			}
		}

		Check( "a default bake, decoded the way s&box's shader decodes it, is the sculpt's normal (a bump)",
			flank > 100 && worst < 4f, $"worst {worst:0.0}° over {flank} flank texels" );
		Check( "and green=dx would light it as a dent in s&box (the old default)", worstDx > 15f, $"worst {worstDx:0.0}°" );

		// The plain-language version: a texel nearer the top of the image than the dome's peak
		// (smaller v) leans towards the top, and +green is up the image.
		var above = map.At( size / 2, size / 2 - 12 );
		var below = map.At( size / 2, size / 2 + 12 );
		Check( "the flank higher in the image is greener than mid, the lower one less green",
			above.G > 140 && below.G < 116, $"above G {above.G}, below G {below.G}" );
	}

	static float Degrees( Vec3 a, Vec3 b ) => MathF.Acos( Math.Clamp( Vec3.Dot( a.Normal, b.Normal ), -1f, 1f ) ) * 180f / MathF.PI;

	static void TestFabricNormalsFollowTheWeaveInTheEngine()
	{
		// One colour for both threads, so the albedo is base × shade and shade gives the height
		// back: shade = 0.82 + 0.18·h (FabricMaterial.Maps).
		var c = new Vec3( 0.5f, 0.5f, 0.5f );
		var (color, normal, _) = FabricMaterial.Maps( "Denim", c, c, 6f, 0.2f );
		var size = (int)MathF.Sqrt( color.Length / 4 );
		float H( int x, int y ) { x = (x + size) % size; y = (y + size) % size; return (color[(y * size + x) * 4] / 255f / 0.5f - 0.82f) / 0.18f; }

		// On a flat plane with s&box's frame: T = +u, V = cross(N, T) = +dP/dv (down the image).
		var frame = new Vec4( 1, 0, 0, 1 );
		var n = new Vec3( 0, 0, 1 );
		double agree = 0, total = 0;

		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				var i = (y * size + x) * 4;
				var world = EngineDecode( (normal[i], normal[i + 1], normal[i + 2]), n, frame );

				// World x is +u, world y is +v (down the image). The normal leans away from uphill.
				var gx = H( x + 1, y ) - H( x - 1, y );
				var gy = H( x, y + 1 ) - H( x, y - 1 );
				var dot = -(world.x * gx + world.y * gy);

				if ( MathF.Abs( gx ) + MathF.Abs( gy ) < 1e-3f )
					continue;

				total++;

				if ( dot > 0f )
					agree++;
			}
		}

		Check( "fabric normals, shaded the s&box way, lean away from uphill on the weave",
			total > 100 && agree / total > 0.95, $"{agree / Math.Max( 1, total ):P1} of {total} texels" );
	}

	// --- 2. tangents and triangulation ----------------------------------------------------------------

	/// <summary>
	/// A lumpy unwrapped sphere with non-planar quads, and its −x half's UVs mirrored, so the
	/// fixture has seams, mirrored islands (w = −1) and skewed frames.
	/// </summary>
	internal static PolyMesh MikkFixture()
	{
		var mesh = Primitives.QuadSphere( 1f, 3 );

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			mesh.Positions[i] = p * (1f + 0.08f * MathF.Sin( 3f * p.x + 2f * p.y + p.z ));
		}

		UVUnwrap.Unwrap( mesh );

		foreach ( var face in mesh.Faces )
		{
			var cx = 0f;
			foreach ( var vi in face.Indices ) cx += mesh.Positions[vi].x;

			if ( cx < 0f )
			{
				for ( var c = 0; c < face.Count; c++ )
					face.UVs[c] = new Vec2( 1f - face.UVs[c].x, face.UVs[c].y );
			}
		}

		return mesh;
	}

	static void TestExportTangentsMatchMikkTSpace( string dir )
	{
		var mesh = MikkFixture();

		// The same triangles the reference was taken from: written out each run, so the reference
		// can be regenerated with tools/mikk_reference.py if the fixture ever changes.
		var objPath = Path.Combine( dir, "mikk_fixture.obj" );
		File.WriteAllText( objPath, ObjWriter.Write( mesh, "mikk", triangulate: true ) );

		var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, MeshNormals.DefaultSmoothingAngleDegrees );
		var ours = NormalBake.ExportTangents( mesh, cornerNormals, normals );

		var reference = MikkReference.Tangents;
		var triangles = new List<(int Face, int A, int B, int C)>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			foreach ( var (a, b, c) in NormalBake.FaceTriangles( mesh, fi ) )
				triangles.Add( (fi, a, b, c) );

		Check( "the Mikk reference covers the fixture's triangles", reference.Length == triangles.Count * 3,
			$"{reference.Length} reference corners, {triangles.Count * 3} ours" );

		if ( reference.Length != triangles.Count * 3 )
			return;

		var ourTriangles = triangles.SelectMany( t => new[] { mesh.Faces[t.Face].Indices[t.A], mesh.Faces[t.Face].Indices[t.B], mesh.Faces[t.Face].Indices[t.C] } );
		Check( "and was taken on the triangles FaceTriangles makes today (else rerun tools/mikk_reference.py)",
			ourTriangles.SequenceEqual( MikkReference.Triangles ) );

		var worst = 0f;
		var worstClean = 0f;
		var clean = 0;
		var sum = 0f;
		var signWrong = 0;
		var mirrored = 0;

		for ( var k = 0; k < triangles.Count; k++ )
		{
			var (fi, a, b, c) = triangles[k];
			var corners = new[] { a, b, c };

			for ( var j = 0; j < 3; j++ )
			{
				var mine = ours[fi][corners[j]];
				var theirs = reference[k * 3 + j];

				// Blender's v runs up (OBJ), Effigy's down, so Mikk's bitangent sign is the opposite
				// of ours on every corner; the tangent (+u) is the same vector.
				if ( MathF.Sign( mine.w ) != -MathF.Sign( theirs.w ) )
					signWrong++;

				if ( mine.w < 0f )
					mirrored++;

				var angle = Degrees( new Vec3( mine.x, mine.y, mine.z ), new Vec3( theirs.x, theirs.y, theirs.z ) );
				worst = MathF.Max( worst, angle );
				sum += angle;

				if ( MikkReference.Clean[k * 3 + j] )
				{
					clean++;
					worstClean = MathF.Max( worstClean, angle );
				}
			}
		}

		var n = triangles.Count * 3;
		File.WriteAllLines( Path.Combine( dir, "mikk_ours.txt" ), triangles.SelectMany( t => new[] { t.A, t.B, t.C }.Select( c => ours[t.Face][c] ) )
			.Select( v => string.Format( CultureInfo.InvariantCulture, "{0} {1} {2} {3}", v.x, v.y, v.z, v.w ) ) );
		Check( "the fixture has mirrored corners to test the sign on", mirrored > n / 4, $"{mirrored} of {n}" );
		Check( "every bitangent sign agrees with MikkTSpace (through the v flip)", signWrong == 0, $"{signWrong} of {n} wrong" );
		Check( "tangents match MikkTSpace: mean under 1°", sum / n < 1f, $"mean {sum / n:0.00}°, worst {worst:0.00}° anywhere" );

		// Where Blender handed Mikk exactly Effigy's welding (see tools/mikk_reference.py), the two
		// agree to under 2°. Measured 2026-10-16: 1.39° worst, on the two corners of one vertex, and
		// everything else under 0.21°. That remainder is mikktspace's own refinement over the plain
		// angle-weighted average (a Python port of the plain rule lands on exactly Effigy's numbers
		// there). The unclean corners, up to 6.7°, are Blender's compressed custom normals splitting a
		// group Effigy keeps whole, not this code.
		Check( "and on corners Mikk welded as Effigy does, they agree to under 2°",
			clean > n / 2 && worstClean < 2f, $"worst {worstClean:0.000}° over {clean} of {n} corners" );
	}

	static PolyMesh NonPlanarQuads()
	{
		var mesh = Primitives.Plane( 2f, 2f, 3, 3 );

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			mesh.Positions[i] = new Vec3( p.x, p.y, 0.3f * MathF.Sin( 2.1f * p.x + 1.3f ) * MathF.Cos( 1.7f * p.y ) );
		}

		return mesh;
	}

	static void TestExportsCarryTheBakesTriangles()
	{
		var mesh = NonPlanarQuads();
		var expected = new List<int[]>();
		var corner = 0;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			foreach ( var (a, b, c) in NormalBake.FaceTriangles( mesh, fi ) )
				expected.Add( new[] { corner + a, corner + b, corner + c } );

			corner += mesh.Faces[fi].Count;
		}

		var root = DmxGrammarTests.Parse( DmxWriter.Write( mesh ), out var error );
		Check( "a triangulated DMX parses", root is not null, error );

		if ( root is null )
			return;

		var faces = DmxGrammarTests.FindAll( root, "DmeFaceSet" ).SelectMany( s => s.Array( "faces" ) ).Select( i => int.Parse( i.Value ) ).ToList();
		var written = new List<int[]>();
		var run = new List<int>();

		foreach ( var i in faces )
		{
			if ( i == -1 ) { written.Add( run.ToArray() ); run.Clear(); }
			else run.Add( i );
		}

		Check( "the DMX faces are exactly the bake's triangles, in corner terms",
			written.Count == expected.Count && written.Zip( expected ).All( p => p.First.SequenceEqual( p.Second ) ),
			$"{written.Count} faces written, {expected.Count} bake triangles" );

		var quads = DmxGrammarTests.Parse( DmxWriter.Write( mesh, triangulate: false ), out _ );
		var quadRuns = DmxGrammarTests.FindAll( quads, "DmeFaceSet" ).SelectMany( s => s.Array( "faces" ) ).Count( i => i.Value == "-1" );
		Check( "and triangulate: false still ships the quads", quadRuns == mesh.FaceCount, $"{quadRuns} faces" );

		var obj = ObjWriter.Write( mesh, triangulate: true );
		var objFaces = obj.Split( '\n' ).Where( l => l.StartsWith( "f " ) ).ToList();
		Check( "OBJ with triangulate on writes the bake's triangles", objFaces.Count == expected.Count && objFaces.All( l => l.Split( ' ' ).Length == 4 ),
			$"{objFaces.Count} faces" );
		Check( "OBJ by default still writes quads (it round-trips models)", ObjWriter.Write( mesh ).Split( '\n' ).Count( l => l.StartsWith( "f " ) ) == mesh.FaceCount );

		// The point of all of it: the bake's triangles ARE the export's, so a self-bake of a
		// non-planar cage, shaded across the exported triangles with the exported tangents, is flat.
		var map = NormalBake.Bake( mesh, mesh.Clone(), 64 );
		var worst = 0f;

		for ( var i = 0; i < 64 * 64; i++ )
		{
			if ( !map.Filled[i] ) continue;
			worst = MathF.Max( worst, MathF.Acos( Math.Clamp( map.NormalAt( i % 64, i / 64 ).z, -1f, 1f ) ) * 180f / MathF.PI );
		}

		// 6°, the same allowance BakeSetTests.TestSelfBakeIsFlat gives: what is left is the sculpt's
		// smoothed normal against the cage's corner normals (two averaging rules), not the split.
		Check( "a non-planar quad cage baked onto itself is flat", worst < 6f, $"worst {worst:0.0}°" );
	}

	static void TestDmxCarriesTangents()
	{
		var mesh = MikkFixture();
		var root = DmxGrammarTests.Parse( DmxWriter.Write( mesh ), out var error );
		Check( "a DMX with tangents parses", root is not null, error );

		if ( root is null )
			return;

		var data = DmxGrammarTests.FindFirst( root, "DmeVertexData" );
		var format = data.Array( "vertexFormat" ).Select( f => f.Value ).ToList();
		Check( "vertexFormat declares tangent$0", format.Contains( "tangent$0" ), string.Join( ", ", format ) );

		var tangents = data.Array( "tangent$0" ).Select( t => t.Value.Split( ' ' ).Select( v => float.Parse( v, CultureInfo.InvariantCulture ) ).ToArray() ).ToList();
		var corners = data.Array( "position$0Indices" ).Count;
		Check( "one tangent per face corner, with its own index array", tangents.Count == corners && data.Array( "tangent$0Indices" ).Count == corners,
			$"{tangents.Count} tangents, {data.Array( "tangent$0Indices" ).Count} indices, {corners} corners" );

		var (cn, normals) = MeshNormals.ComputeCornerNormals( mesh, MeshNormals.DefaultSmoothingAngleDegrees );
		var expected = NormalBake.ExportTangents( mesh, cn, normals ).SelectMany( f => f ).ToList();
		var match = tangents.Count == expected.Count && tangents.Zip( expected ).All( p =>
			p.First.Length == 4 && MathF.Abs( p.First[0] - p.Second.x ) < 1e-4f && MathF.Abs( p.First[1] - p.Second.y ) < 1e-4f
			&& MathF.Abs( p.First[2] - p.Second.z ) < 1e-4f && p.First[3] == p.Second.w );
		Check( "and they are the bake's tangents with ±1 sign", match && tangents.All( t => MathF.Abs( t[3] ) == 1f ) );

		var bare = Primitives.Box( 1, 1, 1 );
		foreach ( var f in bare.Faces ) f.UVs = null;
		var bareRoot = DmxGrammarTests.Parse( DmxWriter.Write( bare ), out _ );
		Check( "a mesh with no UVs writes no tangents rather than made-up ones",
			bareRoot is not null && !DmxGrammarTests.FindFirst( bareRoot, "DmeVertexData" ).Array( "vertexFormat" ).Any( f => f.Value == "tangent$0" ) );
	}

	static void TestFbxCarriesTangentsAndColour()
	{
		var mesh = MikkFixture();
		mesh.VertexColors = mesh.Positions.Select( p => new Vec4( 0.25f, 0.5f, 0.75f, 0.1f ) ).ToArray();
		var text = FbxWriter.Write( mesh );

		Check( "FBX writes a tangent and a binormal layer, and lists them in the Layer",
			text.Contains( "LayerElementTangent: 0" ) && text.Contains( "LayerElementBinormal: 0" )
			&& text.Contains( "Type: \"LayerElementTangent\"" ) && text.Contains( "Type: \"LayerElementBinormal\"" ) );
		Check( "FBX writes vertex colour as a colour layer, alpha as stored",
			text.Contains( "LayerElementColor: 0" ) && text.Contains( "Type: \"LayerElementColor\"" ) && text.Contains( "0.25,0.5,0.75,0.1" ) );

		var quads = FbxWriter.Write( NonPlanarQuads() );
		var tris = FbxWriter.Write( NonPlanarQuads(), triangulate: true );
		Check( "FBX keeps quads by default (Blender) and triangulates on request (engine)",
			CountPolygons( quads ) == 9 && CountPolygons( tris ) == 18, $"{CountPolygons( quads )} / {CountPolygons( tris )}" );

		// Binary FBX goes through the same arrays; it must still build with the new layers.
		var path = Path.Combine( Path.GetTempPath(), $"effigy_tangents_{Environment.ProcessId}.fbx" );
		FbxWriter.WriteBinaryFile( mesh, path );
		Check( "the binary FBX with tangents and colour is written", new FileInfo( path ).Length > 1000 );
		File.Delete( path );
	}

	static int CountPolygons( string fbx )
	{
		var start = fbx.IndexOf( "PolygonVertexIndex", StringComparison.Ordinal );
		var a = fbx.IndexOf( "a: ", start, StringComparison.Ordinal ) + 3;
		var end = fbx.IndexOf( '}', a );
		return fbx[a..end].Split( ',' ).Count( v => v.Trim().StartsWith( '-' ) );
	}

	// --- 3. vertex colour ------------------------------------------------------------------------------

	static void TestPackedMasksSurviveExport()
	{
		var mesh = Primitives.QuadSphere( 4f, 4 );
		var corruption = mesh.Positions.Select( p => p.x > 0 ? 1f : 0f ).ToArray();
		FleshMaterial.PackMasks( mesh, corruption );

		var root = DmxGrammarTests.Parse( DmxWriter.Write( mesh ), out var error );
		var colours = root is null ? new List<DmxGrammarTests.Item>() : DmxGrammarTests.FindFirst( root, "DmeVertexData" ).Array( "color$0" );
		var lost = 0;

		for ( var v = 0; v < mesh.VertexCount && v < colours.Count; v++ )
		{
			var bytes = colours[v].Value.Split( ' ' ).Select( int.Parse ).ToArray();
			var c = mesh.VertexColors[v];
			var want = new[] { c.x, c.y, c.z, c.w }.Select( f => (int)MathF.Round( Math.Clamp( f, 0f, 1f ) * 255f ) ).ToArray();

			if ( !bytes.SequenceEqual( want ) )
				lost++;
		}

		Check( "packed masks (R corruption, G wet, B veins, A AO) reach the DMX byte for byte",
			root is not null && colours.Count == mesh.VertexCount && lost == 0, error ?? $"{lost} vertices changed" );
		Check( "the AO in alpha is really there (not all 255)", colours.Any( c => !c.Value.EndsWith( " 255" ) ) );
	}

	static void TestPerVertexFleshSaysTheGameIgnoresIt()
	{
		var studio = new PartStudio();
		var result = BuildScript.Run( studio,
			"add Primitive name=Sac shape=Sphere radius=3 segments=12\n" +
			"add FleshMaterial bodies=Sac preset=wet_flesh pervertex=1\n", Path.GetTempPath() );
		var feature = studio.Features.OfType<FleshMaterialFeature>().FirstOrDefault();
		Check( "Per vertex flesh warns that s&box does not show vertex colour",
			result.Ok && feature?.Warning is { } w && w.Contains( "s&box" ), feature?.Warning ?? result.ToString() );

		feature.PerVertex.Value = false;
		studio.MarkDirty( feature );
		studio.Rebuild();
		Check( "and baking textures instead does not", feature.Warning is null || !feature.Warning.Contains( "vertex colour" ), feature.Warning );
	}

	// --- 4. weights ------------------------------------------------------------------------------------

	static void TestFbxCapsWeightsAtFour()
	{
		var skeleton = new Skeleton();
		var root = skeleton.AddBone( "root", -1, Xform.Identity, 1f );
		for ( var i = 1; i < 6; i++ )
			skeleton.AddBone( $"b{i}", root, Xform.Translate( new Vec3( i, 0, 0 ) ), 1f );

		var mesh = Primitives.Box( 1, 1, 1 );
		var weights = new BoneWeight[] { new( 0, 0.3f ), new( 1, 0.25f ), new( 2, 0.2f ), new( 3, 0.15f ), new( 4, 0.06f ), new( 5, 0.04f ) };
		mesh.Skin = SkinWeights.AllTo( mesh.VertexCount, 0 );

		for ( var v = 0; v < mesh.VertexCount; v++ )
			mesh.Skin[v] = weights;

		var text = FbxWriter.Write( mesh, skeleton );
		var perVertex = new Dictionary<int, List<float>>();
		var at = 0;

		while ( (at = text.IndexOf( "Deformer: ", at + 1, StringComparison.Ordinal )) >= 0 )
		{
			// The header line says what the deformer is; the Skin deformer's block would otherwise
			// run on into the first cluster's arrays and count them twice.
			var header = text.Substring( at, text.IndexOf( '\n', at ) - at );

			if ( !header.Contains( "\"Cluster\"" ) )
				continue;

			var next = text.IndexOf( "Deformer: ", at + 1, StringComparison.Ordinal );
			var block = text.Substring( at, (next < 0 ? text.Length : next) - at );

			var idx = Numbers( block, "Indexes" );
			var w = Numbers( block, "Weights" );

			for ( var k = 0; k < idx.Count && k < w.Count; k++ )
			{
				var v = (int)idx[k];
				if ( !perVertex.TryGetValue( v, out var list ) ) perVertex[v] = list = new List<float>();
				list.Add( (float)w[k] );
			}
		}

		Check( "every FBX vertex carries at most four influences", perVertex.Count == mesh.VertexCount && perVertex.Values.All( l => l.Count <= 4 ),
			$"{perVertex.Count} vertices, max {perVertex.Values.DefaultIfEmpty( new List<float>() ).Max( l => l.Count )} influences" );
		Check( "and they are renormalised to one", perVertex.Values.All( l => MathF.Abs( l.Sum() - 1f ) < 1e-3f ),
			$"sums {string.Join( ", ", perVertex.Values.Select( l => l.Sum().ToString( "0.###" ) ).Distinct() )}" );
		Check( "keeping the four strongest (bone 4 and 5 dropped)", perVertex.Count > 0 && perVertex[0].Count == 4
			&& MathF.Abs( perVertex[0].Max() - 0.3f / 0.9f ) < 1e-3f );
	}

	static List<double> Numbers( string block, string name )
	{
		var start = block.IndexOf( name + ": *", StringComparison.Ordinal );
		if ( start < 0 ) return new List<double>();
		var a = block.IndexOf( "a: ", start, StringComparison.Ordinal ) + 3;
		var end = block.IndexOf( '}', a );
		return block[a..end].Split( ',' ).Select( s => s.Trim() ).Where( s => s.Length > 0 ).Select( s => double.Parse( s, CultureInfo.InvariantCulture ) ).ToList();
	}

	// --- 5. skin.vfx -------------------------------------------------------------------------------

	/// <summary>The keys Facepunch's body_mode_dark.vmat sets (sbox-public citizen_human), in its order.</summary>
	static readonly string[] FacepunchBodyKeys =
	{
		"g_flAmbientOcclusionDirectDiffuse", "g_flAmbientOcclusionDirectSpecular", "g_vAmbientOcclusionColorBleed",
		"TextureAmbientOcclusion", "g_flModelTintAmount", "g_vColorTint", "TextureColor", "g_flCurvatureScale",
		"g_bFogEnabled", "g_vAmbientNormalSoftness", "TextureDiffuseFalloff", "g_flDiffuseNormalBlur",
		"TextureNormal", "TextureRoughness", "g_flSecondSpecularLobeGlossiness", "g_flSecondSpecularLobeRatio",
		"g_vTexCoordOffset", "g_vTexCoordScale", "g_flTransmissionFalloff", "g_vTransmissionColor", "TextureTransmissiveMask",
	};

	static Dictionary<string, string> Keys( string vmat ) => vmat.Split( '\n' )
		.Select( l => l.Trim() ).Where( l => l.Length > 0 && !l.StartsWith( "//" ) && l.Contains( ' ' ) )
		.Select( l => (Key: l[..l.IndexOf( ' ' )], Value: l[(l.IndexOf( ' ' ) + 1)..].Trim( '"' )) )
		.GroupBy( p => p.Key ).ToDictionary( g => g.Key, g => g.First().Value );

	static void TestSkinVmatIsFacepunchs()
	{
		var body = SkinMaterial.VmatSource( SkinMaterial.Body(), "c.png", "n.png", "r.png", "ao.png" );
		var keys = Keys( body );

		Check( "skin vmat uses Facepunch's shader line", keys.GetValueOrDefault( "shader" ) == "shaders/skin.shader", keys.GetValueOrDefault( "shader" ) );
		var missing = FacepunchBodyKeys.Where( k => !keys.ContainsKey( k ) ).ToList();
		Check( "and sets every key Facepunch's human body sets", missing.Count == 0, string.Join( ", ", missing ) );
		Check( "with the body's numbers (curvature 0.463, blur 2, lobe 0.008/0.392, falloff 0.8)",
			keys["g_flCurvatureScale"] == "0.463" && keys["g_flDiffuseNormalBlur"] == "2.000" && keys["g_flSecondSpecularLobeRatio"] == "0.008"
			&& keys["g_flSecondSpecularLobeGlossiness"] == "0.392" && keys["g_flTransmissionFalloff"] == "0.800"
			&& keys["g_vAmbientOcclusionColorBleed"] == "[0.400000 0.149020 0.129412 0.000000]"
			&& keys["g_vTransmissionColor"] == "[0.749020 0.388235 0.007843 0.000000]" );
		Check( "the maps go where they were given", keys["TextureColor"] == "c.png" && keys["TextureNormal"] == "n.png"
			&& keys["TextureRoughness"] == "r.png" && keys["TextureAmbientOcclusion"] == "ao.png" );
		Check( "no transmission mask means black, as on Facepunch's bodies",
			keys["TextureTransmissiveMask"] == "[0.000000 0.000000 0.000000 0.000000]" );
		Check( "no bent-normal map means the flag stays off rather than pointing at nothing",
			!keys.ContainsKey( "F_USE_BENT_NORMALS" ) && !keys.ContainsKey( "TextureBentNormal" ) );
		var bent = Keys( SkinMaterial.VmatSource( SkinMaterial.Body(), bentNormal: "b.png" ) );
		Check( "and a bent-normal map switches it on", bent.GetValueOrDefault( "F_USE_BENT_NORMALS" ) == "1" && bent.GetValueOrDefault( "TextureBentNormal" ) == "b.png" );

		var mouth = Keys( SkinMaterial.VmatSource( SkinMaterial.Mouth(), "c.png" ) );
		Check( "the mouth is Facepunch's wet recipe: flat 0.552 roughness, lobe 0.263 at 0.3, curvature 0.569, morphs",
			mouth["TextureRoughness"] == "[0.552000 0.552000 0.552000 0.000000]" && mouth["g_flSecondSpecularLobeRatio"] == "0.263"
			&& mouth["g_flSecondSpecularLobeGlossiness"] == "0.300" && mouth["g_flCurvatureScale"] == "0.569"
			&& mouth.GetValueOrDefault( "F_MORPH_SUPPORTED" ) == "1" && mouth["g_vTransmissionColor"] == "[0.000000 0.000000 0.000000 0.000000]" );
		Check( "every preset name resolves", SkinMaterial.Presets.All( n => SkinMaterial.Preset( n ).Name == n ) );
	}

	static void TestTransmissionFollowsThickness()
	{
		// d'Eon/Jimenez red transmittance, the table in realtime-flesh-shading.md §3.1.
		var table = new[] { (0.25f, 0.64f), (0.5f, 0.50f), (1f, 0.31f), (2f, 0.09f), (3f, 0.03f) };
		Check( "the skin profile's red transmittance matches the published table",
			table.All( p => MathF.Abs( SkinMaterial.SkinTransmittanceRed( p.Item1 ) - p.Item2 ) < 0.015f ),
			string.Join( ", ", table.Select( p => $"{p.Item1}mm {SkinMaterial.SkinTransmittanceRed( p.Item1 ):0.00}" ) ) );

		float Mask( float thickness )
		{
			var low = Primitives.Plane( 2f, 2f, 2, 2 );
			for ( var i = 0; i < low.VertexCount; i++ ) low.Positions[i] = new Vec3( low.Positions[i].x, low.Positions[i].y, thickness / 2f );
			var high = Primitives.Box( 2f, 2f, thickness );
			var set = NormalBake.BakeAll( low, high, 16, 16, BakeMaps.Thickness, new BakeOptions { ThicknessRays = 16, ThicknessDistance = 1f } );
			var mask = SkinMaterial.TransmissionMask( set );
			return mask.At( 8, 8 ).R / 255f;
		}

		// A 0.04 in membrane is about 1 mm: skin passes roughly a third of the red. A 2 in slab passes nothing.
		var thin = Mask( 0.04f );
		var thick = Mask( 2f );
		Check( "a 1 mm membrane transmits, a 2 in slab does not", thin > 0.15f && thin < 0.45f && thick < 0.01f, $"thin {thin:0.00}, thick {thick:0.00}" );
	}

	/// <summary>What the editor's Bake button writes (BakeMaterial.WriteSet): every map, the transmission mask, the vmat.</summary>
	static void TestBakeSetWritesMapsAndVmat( string dir )
	{
		var (low, high) = DomeFixture();
		var set = NormalBake.BakeAll( low, high, 32, 32, BakeMaps.All, new BakeOptions { AoRays = 8, ThicknessRays = 8 } );
		var folder = Path.Combine( dir, "bakeset_ui" );
		if ( Directory.Exists( folder ) ) Directory.Delete( folder, true );

		var written = BakeMaterial.WriteSet( set, folder, "dome", assetsRoot: dir, material: "Skin: Wet flesh" );
		var names = written.Select( Path.GetFileName ).ToList();
		var want = new[] { "dome_normal.png", "dome_ao.png", "dome_curvature.png", "dome_cavity.png", "dome_height.png", "dome_thickness.png", "dome_transmission.png", "dome.vmat" };
		Check( "a full bake writes every map, the transmission mask and the vmat", want.All( names.Contains ) && written.All( File.Exists ), string.Join( ", ", names ) );

		var vmat = Keys( File.ReadAllText( Path.Combine( folder, "dome.vmat" ) ) );
		Check( "the skin vmat binds the bake by asset path", vmat.GetValueOrDefault( "shader" ) == "shaders/skin.shader"
			&& vmat.GetValueOrDefault( "TextureNormal" ) == "bakeset_ui/dome_normal.png"
			&& vmat.GetValueOrDefault( "TextureAmbientOcclusion" ) == "bakeset_ui/dome_ao.png"
			&& vmat.GetValueOrDefault( "TextureTransmissiveMask" ) == "bakeset_ui/dome_transmission.png",
			string.Join( "; ", vmat.Select( p => $"{p.Key}={p.Value}" ) ) );

		var complex = BakeMaterial.WriteSet( set, folder, "plain", material: "Complex" );
		Check( "Complex writes the complex vmat", File.ReadAllText( complex[^1] ).Contains( "complex.vfx" ) && File.ReadAllText( complex[^1] ).Contains( "plain_normal.png" ) );
		Check( "None writes maps only", BakeMaterial.WriteSet( set, folder, "bare" ).All( p => p.EndsWith( ".png" ) ) );
	}
}
