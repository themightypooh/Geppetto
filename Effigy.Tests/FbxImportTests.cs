using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Import reads FBX: a file the kernel wrote comes back where it left, and a file someone else
/// wrote - several meshes, a parent chain, Y up, centimetres - lands where that tool showed it.
///
/// Everything here is checked numerically against the source mesh, corner by corner, because
/// the failures FBX import is known for all look plausible: a model 2.54× too big, lying on its
/// back, inside out, or with its texture upside down still renders as a model.
/// </summary>
public static class FbxImportTests
{
	const float Tolerance = 1e-3f;

	public static void Run()
	{
		Report.Section( "fbx import: the kernel's own export comes back the same" );
		TestBinaryRoundTrip();
		TestAsciiRoundTrip();
		TestDeclaredUnitsScaleThePoints();

		Report.Section( "fbx import: another tool's file lands where that tool showed it" );
		TestSeveralMeshesWithNodeTransforms();
		TestMirroredNodeKeepsFacesOutward();
		TestWrappedAsciiArraysRead();
		TestCollapsedCornersAreSkipped();

		Report.Section( "fbx import: skeleton and skin" );
		TestSkeletonAndWeightsRoundTrip();
		TestSkinWaitsForARig();
		TestMismatchedRigIsReported();
		TestScriptImportsAnFbx();

		Report.Section( "fbx import: refusals say what to do" );
		TestGarbageFbxFailsUsefully();
		TestGltfIsRefusedByName();
		TestOldFbxIsRefused();
		TestFbxSidecarKeepsTheFile();
	}

	// --- the kernel's own export ------------------------------------------------------------------

	/// <summary>A box off the origin with two material slots, UVs and vertex colours.</summary>
	static PolyMesh SampleMesh()
	{
		var mesh = Primitives.Box( 4, 6, 8 );

		for ( var i = 0; i < mesh.Positions.Count; i++ )
			mesh.Positions[i] += new Vec3( 3, -2, 5 );

		for ( var f = 0; f < mesh.FaceCount; f++ )
		{
			var face = mesh.Faces[f];
			face.Material = f % 2 == 0 ? 0 : 3;

			for ( var k = 0; k < face.Count; k++ )
				face.UVs[k] = new Vec2( 0.1f * k + 0.05f * f, 0.2f + 0.1f * k );
		}

		mesh.VertexColors = mesh.Positions.Select( ( p, i ) => new Vec4( i / 8f, 0.5f, 1f - i / 8f, 1f ) ).ToArray();
		return mesh;
	}

	static void TestBinaryRoundTrip()
	{
		var source = SampleMesh();
		var path = TempPath( "roundtrip.fbx" );

		try
		{
			FbxWriter.WriteBinaryFile( source, path );
			var studio = BuildImport( path, out var import );

			Report.Check( "a binary FBX the kernel wrote imports without complaint",
				import.Error is null && import.Warning is null, import.Error ?? import.Warning ?? "" );

			var body = studio.Bodies.FirstOrDefault( b => b.Id.StartsWith( import.Id, StringComparison.Ordinal ) );
			Report.Check( "and becomes one body", studio.Bodies.Count == 1 && body is not null, $"{studio.Bodies.Count} bodies" );

			if ( body is not null )
				CompareMeshes( "binary", source, body.Mesh );
		}
		finally
		{
			File.Delete( path );
		}
	}

	static void TestAsciiRoundTrip()
	{
		var source = SampleMesh();
		var result = FbxImport.Read( Encoding.UTF8.GetBytes( FbxWriter.Write( source ) ) );

		Report.Check( "the engine-path ASCII FBX reads as one piece", result.Pieces.Count == 1, $"{result.Pieces.Count}" );
		Report.Check( "and its undeclared inches are read as inches, said in a note",
			Math.Abs( result.AppliedScale - 1 ) < 1e-9 && result.Notes.Any( n => n.Contains( "inches" ) ),
			$"scale {result.AppliedScale}; notes: {string.Join( " | ", result.Notes )}" );

		if ( result.Pieces.Count == 1 )
			CompareMeshes( "ascii", source, result.Pieces[0].Mesh );
	}

	/// <summary>
	/// UnitScaleFactor is centimetres per unit. A file declaring metres (100) has its points 100/2.54
	/// times larger in inches - the exact inverse of how the writer declares its own 2.54.
	/// </summary>
	static void TestDeclaredUnitsScaleThePoints()
	{
		var source = Primitives.Box( 1, 1, 1 );
		var path = TempPath( "metres.fbx" );

		try
		{
			FbxWriter.WriteBinaryFile( source, path, unitScale: 100 );
			var result = FbxImport.Read( File.ReadAllBytes( path ) );
			var size = Bounds( result.Pieces[0].Mesh );
			var expected = 100f / 2.54f;

			Report.Check( "a file declaring metres arrives 39.37× larger in inches",
				MathF.Abs( size.x - expected ) < 1e-2f && MathF.Abs( size.z - expected ) < 1e-2f,
				$"size {size}, expected {expected:0.###}" );
		}
		finally
		{
			File.Delete( path );
		}
	}

	// --- another tool's file ----------------------------------------------------------------------

	/// <summary>
	/// Two meshes, Y up, centimetres, one under a parent with a turn: what Blender's default export
	/// and most DCC files look like. Written by hand so the expected numbers can be worked out by
	/// hand - see the comments on each check.
	/// </summary>
	const string TwoMeshes = """
		; FBX 7.4.0 project file
		FBXHeaderExtension:  {
			FBXHeaderVersion: 1003
			FBXVersion: 7400
			Creator: "Hand written"
		}
		GlobalSettings:  {
			Version: 1000
			Properties70:  {
				P: "UpAxis", "int", "Integer", "",1
				P: "UpAxisSign", "int", "Integer", "",1
				P: "FrontAxis", "int", "Integer", "",2
				P: "FrontAxisSign", "int", "Integer", "",1
				P: "CoordAxis", "int", "Integer", "",0
				P: "CoordAxisSign", "int", "Integer", "",1
				P: "UnitScaleFactor", "double", "Number", "",2.54
			}
		}
		Objects:  {
			Geometry: 100, "Geometry::plate", "Mesh" {
				Vertices: *12 {
					a: 0,0,0,1,0,0,
					1,1,0,0,1,0
				}
				PolygonVertexIndex: *4 {
					a: 0,1,2,-4
				}
				LayerElementUV: 0 {
					Version: 101
					Name: "UVMap"
					MappingInformationType: "ByPolygonVertex"
					ReferenceInformationType: "IndexToDirect"
					UV: *8 {
						a: 0,0,1,0,1,1,0,1
					}
					UVIndex: *4 {
						a: 0,1,2,3
					}
				}
				LayerElementMaterial: 0 {
					Version: 101
					Name: ""
					MappingInformationType: "AllSame"
					ReferenceInformationType: "IndexToDirect"
					Materials: *1 {
						a: 0
					}
				}
			}
			Geometry: 101, "Geometry::fin", "Mesh" {
				Vertices: *12 {
					a: 0,0,0,1,0,0,1,1,0,0,1,0
				}
				PolygonVertexIndex: *4 {
					a: 0,1,2,-4
				}
				LayerElementMaterial: 0 {
					Version: 101
					Name: ""
					MappingInformationType: "AllSame"
					ReferenceInformationType: "IndexToDirect"
					Materials: *1 {
						a: 0
					}
				}
			}
			Model: 200, "Model::Plate", "Mesh" {
				Version: 232
				Properties70:  {
					P: "Lcl Translation", "Lcl Translation", "", "A",10,0,0
				}
			}
			Model: 201, "Model::Holder", "Null" {
				Version: 232
				Properties70:  {
					P: "Lcl Translation", "Lcl Translation", "", "A",0,5,0
				}
			}
			Model: 202, "Model::Fin", "Mesh" {
				Version: 232
				Properties70:  {
					P: "Lcl Translation", "Lcl Translation", "", "A",1,0,0
					P: "Lcl Rotation", "Lcl Rotation", "", "A",0,90,0
				}
			}
			Material: 300, "Material::Skin", "" {
				Version: 102
			}
			Material: 301, "Material::Cloth", "" {
				Version: 102
			}
		}
		Connections:  {
			C: "OO",200,0
			C: "OO",100,200
			C: "OO",300,200
			C: "OO",201,0
			C: "OO",202,201
			C: "OO",101,202
			C: "OO",301,202
		}
		""";

	static void TestSeveralMeshesWithNodeTransforms()
	{
		var result = FbxImport.Read( Encoding.UTF8.GetBytes( TwoMeshes ) );

		Report.Check( "two meshes become two pieces, named after their nodes",
			result.Pieces.Count == 2 && result.Pieces[0].Name == "Plate" && result.Pieces[1].Name == "Fin",
			string.Join( ", ", result.Pieces.Select( p => p.Name ) ) );

		if ( result.Pieces.Count != 2 )
			return;

		var plate = result.Pieces[0].Mesh;
		var fin = result.Pieces[1].Mesh;

		// Y up → Z up is (x, y, z) → (x, -z, y); 2.54 cm per unit is one inch per unit.
		// Plate corner (1,1,0) moved by (10,0,0) is (11,1,0) in the file → (11, 0, 1).
		Report.Check( "a node's translation and the Y-up turn place the plate",
			HasPoint( plate, new Vec3( 10, 0, 0 ) ) && HasPoint( plate, new Vec3( 11, 0, 1 ) ),
			string.Join( " ", plate.Positions ) );

		// Fin: +90° about Y takes (1,0,0) to (0,0,-1); then its own (1,0,0) and the parent's (0,5,0):
		// (1,5,-1) in the file → (1, 1, 5). And (0,1,0) → (1,6,0) → (1, 0, 6).
		Report.Check( "a parent chain and a rotation place the fin",
			HasPoint( fin, new Vec3( 1, 1, 5 ) ) && HasPoint( fin, new Vec3( 1, 0, 6 ) ),
			string.Join( " ", fin.Positions ) );

		Report.Check( "each material name takes its own slot, in the order the file uses them",
			plate.Faces.All( f => f.Material == 0 ) && fin.Faces.All( f => f.Material == 1 )
				&& result.MaterialSlots.GetValueOrDefault( 0 ) == "Skin" && result.MaterialSlots.GetValueOrDefault( 1 ) == "Cloth",
			string.Join( ", ", result.MaterialSlots.Select( kv => $"{kv.Key}={kv.Value}" ) ) );

		// FBX v runs up, Effigy's runs down: file (1,1) at the far corner is Effigy (1,0).
		var far = plate.Faces[0].Indices.ToList().FindIndex( i => (plate.Positions[i] - new Vec3( 11, 0, 1 )).Length < Tolerance );
		Report.Check( "UVs come in with V flipped to Effigy's top-down",
			far >= 0 && (plate.Faces[0].UVs[far] - new Vec2( 1, 0 )).Length < Tolerance,
			far >= 0 ? plate.Faces[0].UVs[far].ToString() : "corner not found" );

		Report.Check( "the Null parent is not mistaken for a skeleton", result.Skeleton is null,
			$"{result.Skeleton?.Count ?? 0} bones" );
	}

	/// <summary>A node scaled by -1 in X mirrors the box. Without reversing the winding every face
	/// would point in and the volume would come out negative.</summary>
	static void TestMirroredNodeKeepsFacesOutward()
	{
		// Skeletonless: a rigged write is placed by its skin's bind matrices and would never see
		// the node's scale at all.
		var source = Primitives.Box( 2, 3, 4 );
		var text = FbxWriter.Write( source, skeletonless: true ).Replace( "\"Lcl Scaling\", \"\", \"A\",1,1,1", "\"Lcl Scaling\", \"\", \"A\",-1,1,1" );
		var result = FbxImport.Read( Encoding.UTF8.GetBytes( text ) );
		var mesh = result.Pieces[0].Mesh;

		Report.Check( "the mirrored file really was mirrored",
			text.Contains( "\"A\",-1,1,1" ) && mesh.Positions.Zip( source.Positions ).Any( q => (q.First - q.Second).Length > Tolerance ),
			"" );
		Report.Check( "a mirrored node keeps its faces pointing out",
			MathF.Sign( mesh.SignedVolume() ) == MathF.Sign( source.SignedVolume() ) && MathF.Abs( mesh.SignedVolume() - source.SignedVolume() ) < 1e-2f,
			$"volume {source.SignedVolume():0.###} -> {mesh.SignedVolume():0.###}" );
		Report.Check( "and its file normals agree with its faces", result.InwardPieces == 0, $"{result.InwardPieces} inward" );
	}

	static void TestWrappedAsciiArraysRead()
	{
		// The plate's Vertices are wrapped over two lines in TwoMeshes; this checks the count.
		var scene = FbxScene.Read( Encoding.UTF8.GetBytes( TwoMeshes ) );

		Report.Check( "an ASCII array wrapped over several lines reads whole",
			scene.Meshes.Count == 2 && scene.Meshes[0].Points.Length == 4,
			$"{scene.Meshes.FirstOrDefault()?.Points.Length ?? 0} points" );
	}

	/// <summary>A quad written with one corner repeated (a collapsed edge) comes in as the quad,
	/// not as a five-corner face with a zero-length edge.</summary>
	static void TestCollapsedCornersAreSkipped()
	{
		var text = TwoMeshes.Replace( "a: 0,1,2,-4", "a: 0,1,1,2,-4" );
		var result = FbxImport.Read( Encoding.UTF8.GetBytes( text ) );

		Report.Check( "a repeated corner is skipped and the polygon kept",
			result.Pieces.Count == 2 && result.Pieces.All( p => p.Mesh.FaceCount == 1 && p.Mesh.Faces[0].Count == 4 ),
			string.Join( ", ", result.Pieces.Select( p => $"{p.Name}: {string.Join( "/", p.Mesh.Faces.Select( f => f.Count ) )}" ) ) );
	}

	// --- skeleton and skin ------------------------------------------------------------------------

	static (PolyMesh Mesh, Skeleton Skeleton) RiggedColumn()
	{
		var skeleton = new Skeleton();
		skeleton.AddBoneFromPoints( "hip", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 10 ) );
		skeleton.AddBoneFromPoints( "knee", 0, new Vec3( 0, 0, 10 ), new Vec3( 1, 0, 20 ) );

		var mesh = Primitives.Box( 4, 4, 20 );

		for ( var i = 0; i < mesh.Positions.Count; i++ )
			mesh.Positions[i] += new Vec3( 0, 0, 10 );

		mesh.Skin = new SkinWeights();

		foreach ( var p in mesh.Positions )
		{
			var t = Math.Clamp( p.z / 20f, 0f, 1f );
			mesh.Skin.Vertices.Add( t <= 0f
				? new[] { new BoneWeight( 0, 1f ) }
				: new[] { new BoneWeight( 0, 1f - t ), new BoneWeight( 1, t ) } );
		}

		return (mesh, skeleton);
	}

	static void TestSkeletonAndWeightsRoundTrip()
	{
		var (source, skeleton) = RiggedColumn();
		var path = TempPath( "rigged.fbx" );

		try
		{
			FbxWriter.WriteBinaryFile( source, path, skeleton );
			var result = FbxImport.Read( File.ReadAllBytes( path ) );
			var read = result.Skeleton;

			Report.Check( "the file's skeleton comes back with its names and parents",
				read is { Count: 2 } && read.Bones[0].Name == "hip" && read.Bones[1].Name == "knee" && read.Bones[1].Parent == 0,
				read is null ? "no skeleton" : string.Join( ", ", read.Bones.Select( b => $"{b.Name}<{b.Parent}" ) ) );

			if ( read is { Count: 2 } )
			{
				var worst = 0f;

				for ( var i = 0; i < 2; i++ )
				{
					worst = MathF.Max( worst, (read.HeadWorld( i ) - skeleton.HeadWorld( i )).Length );
					worst = MathF.Max( worst, (read.TailWorld( i ) - skeleton.TailWorld( i )).Length );
				}

				Report.Check( "and every bone's head and tail where it was", worst < Tolerance, $"worst {worst:0.#####} in" );
			}

			// Through the feature: install the rig, rebuild, and the body carries the file's weights.
			var studio = BuildImport( path, out var import );
			var installed = ImportFeature.InstallSkeleton( studio, import, out var message );
			studio.Rebuild();
			var body = studio.Bodies.FirstOrDefault();

			Report.Check( "an empty rig takes the file's skeleton", installed == 2 && studio.Rig.Count == 2, message );
			Report.Check( "and the imported body carries the file's skin, unwarned",
				body is { Mesh.IsRigged: true } && import.Warning is null, import.Warning ?? (body?.Mesh.IsRigged == true ? "" : "no skin") );

			if ( body is { Mesh.IsRigged: true } )
			{
				var worst = 0f;

				for ( var v = 0; v < body.Mesh.VertexCount; v++ )
				{
					var match = Nearest( source, body.Mesh.Positions[v] );
					worst = MathF.Max( worst, WeightDistance( source.Skin[match], body.Mesh.Skin[v], skeleton, studio.Rig ) );
				}

				Report.Check( "every vertex's weights match the source", worst < 1e-3f, $"worst {worst:0.#####}" );
			}

			var again = ImportFeature.InstallSkeleton( studio, import, out var refusal );
			Report.Check( "a rig that already has bones is kept, and the message says how to replace it",
				again == 0 && studio.Rig.Count == 2 && refusal.Contains( "Delete those bones" ), refusal );
		}
		finally
		{
			File.Delete( path );
		}
	}

	static void TestSkinWaitsForARig()
	{
		var (source, skeleton) = RiggedColumn();
		var path = TempPath( "skin-no-rig.fbx" );

		try
		{
			FbxWriter.WriteBinaryFile( source, path, skeleton );
			var studio = BuildImport( path, out var import );
			var body = studio.Bodies.FirstOrDefault();

			Report.Check( "with no rig the geometry still comes in", body is { Mesh.FaceCount: > 0 }, $"{studio.Bodies.Count} bodies" );
			Report.Check( "and the skipped skin is a warning that says how to take the skeleton",
				import.Warning is not null && import.Diagnostic?.Remedies.Any( r => r.Contains( "skeleton" ) ) == true,
				import.Warning ?? "no warning" );
			Report.Check( "the feature still knows the file's skeleton", import.SourceSkeleton?.Count == 2, $"{import.SourceSkeleton?.Count}" );
		}
		finally
		{
			File.Delete( path );
		}
	}

	static void TestMismatchedRigIsReported()
	{
		var (source, skeleton) = RiggedColumn();
		var path = TempPath( "skin-other-rig.fbx" );

		try
		{
			FbxWriter.WriteBinaryFile( source, path, skeleton );
			var studio = new PartStudio();
			studio.Rig.AddBoneFromPoints( "spine", -1, Vec3.Zero, new Vec3( 0, 0, 10 ) );
			var import = studio.Add( new ImportFeature { Name = "Leg" } );
			import.BindSource( path );
			studio.Rebuild();
			var body = studio.Bodies.FirstOrDefault();

			Report.Check( "a rig without the file's bones leaves the weights off rather than half-applying them",
				body is { Mesh.IsRigged: false }, body?.Mesh.IsRigged == true ? "weights applied" : "" );
			Report.Check( "and the warning names the missing bones",
				import.Diagnostic?.Cause?.Contains( "hip" ) == true && import.Diagnostic.Cause.Contains( "knee" ),
				import.Diagnostic?.Cause ?? "no warning" );
		}
		finally
		{
			File.Delete( path );
		}
	}

	/// <summary>`add Import source=…` in a build script: the path is the script's, and the file's
	/// skeleton becomes the rig the way picking it in the editor does.</summary>
	static void TestScriptImportsAnFbx()
	{
		var (source, skeleton) = RiggedColumn();
		var dir = Path.Combine( Path.GetTempPath(), "effigy-fbx-script-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( dir );

		try
		{
			FbxWriter.WriteBinaryFile( source, Path.Combine( dir, "leg.fbx" ), skeleton );
			var studio = new PartStudio();
			var result = BuildScript.Run( studio, "add Import name=Leg source=leg.fbx", dir );

			Report.Check( "a build script imports an FBX by a path relative to the script",
				result.Ok && studio.Bodies.Count == 1, result.ToString() );
			Report.Check( "and takes its skeleton and skin",
				studio.Rig.Count == 2 && studio.Bodies.FirstOrDefault()?.Mesh.IsRigged == true
					&& result.Log.Any( l => l.Contains( "2 bones" ) ),
				result.ToString() );
		}
		finally
		{
			Directory.Delete( dir, true );
		}
	}

	// --- refusals ---------------------------------------------------------------------------------

	static void TestGarbageFbxFailsUsefully()
	{
		var path = TempPath( "broken.fbx" );
		File.WriteAllText( path, "not an fbx at all" );

		try
		{
			var studio = BuildImport( path, out var import );
			Report.Check( "a .fbx that is not FBX fails, naming FBX",
				import.Error is not null && import.Error.Contains( "FBX" ), import.Error ?? "built anyway" );
			Report.Check( "with a remedy", import.Diagnostic?.Remedies.Count > 0, "" );
		}
		finally
		{
			File.Delete( path );
		}
	}

	static void TestGltfIsRefusedByName()
	{
		var path = TempPath( "model.glb" );
		File.WriteAllText( path, "glTF" );

		try
		{
			BuildImport( path, out var import );
			Report.Check( "a GLB is refused by name, with FBX or OBJ as the way forward",
				import.Error is not null && import.Diagnostic?.Remedies.Any( r => r.Contains( "FBX" ) ) == true,
				import.Error ?? "built anyway" );
		}
		finally
		{
			File.Delete( path );
		}
	}

	static void TestOldFbxIsRefused()
	{
		var bytes = new byte[64];
		Encoding.ASCII.GetBytes( "Kaydara FBX Binary  \0\x1a\0" ).CopyTo( bytes, 0 );
		BitConverter.GetBytes( 6100 ).CopyTo( bytes, 23 );

		var studio = new PartStudio();
		var import = studio.Add( new ImportFeature() );
		import.LoadMesh( bytes );
		studio.Rebuild();

		Report.Check( "an FBX 6 file is refused with the version it needs",
			import.Error is not null && import.Diagnostic?.Cause?.Contains( "7." ) == true, import.Diagnostic?.Cause ?? "built anyway" );
	}

	static void TestFbxSidecarKeepsTheFile()
	{
		var (source, skeleton) = RiggedColumn();
		var dir = Path.Combine( Path.GetTempPath(), "effigy-fbx-sidecar-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( dir );
		var fbxPath = Path.Combine( dir, "leg.fbx" );
		var docPath = Path.Combine( dir, "doc.effigy" );

		try
		{
			FbxWriter.WriteBinaryFile( source, fbxPath, skeleton );
			var studio = BuildImport( fbxPath, out var import );

			// A stale OBJ from when this import pointed somewhere else.
			Directory.CreateDirectory( ImportSidecar.DirectoryFor( docPath ) );
			File.WriteAllText( ImportSidecar.PathFor( docPath, import.Id ), "v 0 0 0\n" );

			ImportSidecar.Save( studio, docPath );
			var sidecar = ImportSidecar.PathFor( docPath, import.Id, File.ReadAllBytes( fbxPath ) );

			Report.Check( "an FBX import's side-car is kept as FBX",
				sidecar.EndsWith( ".fbx", StringComparison.Ordinal ) && File.Exists( sidecar )
					&& !File.Exists( ImportSidecar.PathFor( docPath, import.Id ) ),
				sidecar );

			var reopened = new PartStudio();
			var copy = reopened.Add( new ImportFeature { Name = import.Name } );
			copy.Id = import.Id;
			ImportSidecar.Load( reopened, docPath );
			reopened.Rebuild();

			Report.Check( "and a reopen with the source gone still has the mesh and its skeleton",
				reopened.Bodies.Count == 1 && reopened.Bodies[0].Mesh.FaceCount == source.FaceCount && copy.SourceSkeleton?.Count == 2,
				$"{reopened.Bodies.Count} bodies, {copy.SourceSkeleton?.Count ?? 0} bones; {copy.Error}" );
		}
		finally
		{
			Directory.Delete( dir, true );
		}
	}

	// --- helpers ----------------------------------------------------------------------------------

	static PartStudio BuildImport( string path, out ImportFeature import )
	{
		var studio = new PartStudio();
		import = studio.Add( new ImportFeature { Name = Path.GetFileNameWithoutExtension( path ) } );
		import.BindSource( path );
		studio.Rebuild();
		return studio;
	}

	static string TempPath( string name ) =>
		Path.Combine( Path.GetTempPath(), $"effigy-fbx-{Guid.NewGuid():N}-{name}" );

	/// <summary>
	/// Face by face, corner by corner: position, UV, slot, vertex colour, and the normal Effigy
	/// derives - the writer writes faces in order and does not triangulate by default, so face i of
	/// the import is face i of the source.
	/// </summary>
	static void CompareMeshes( string label, PolyMesh source, PolyMesh imported )
	{
		Report.Check( $"{label}: same vertex and face counts",
			source.VertexCount == imported.VertexCount && source.FaceCount == imported.FaceCount,
			$"{source.VertexCount}v/{source.FaceCount}f -> {imported.VertexCount}v/{imported.FaceCount}f" );

		if ( source.FaceCount != imported.FaceCount )
			return;

		var (srcCorner, srcNormals) = MeshNormals.ComputeCornerNormals( source, MeshNormals.DefaultSmoothingAngleDegrees );
		var (impCorner, impNormals) = MeshNormals.ComputeCornerNormals( imported, MeshNormals.DefaultSmoothingAngleDegrees );

		float position = 0, uv = 0, normal = 0, colour = 0;
		var slots = true;

		for ( var f = 0; f < source.FaceCount; f++ )
		{
			var a = source.Faces[f];
			var b = imported.Faces[f];

			if ( a.Count != b.Count )
			{
				position = float.PositiveInfinity;
				continue;
			}

			slots &= a.Material == b.Material;

			for ( var k = 0; k < a.Count; k++ )
			{
				position = MathF.Max( position, (source.Positions[a.Indices[k]] - imported.Positions[b.Indices[k]]).Length );
				uv = MathF.Max( uv, (a.UVs[k] - b.UVs[k]).Length );
				normal = MathF.Max( normal, (srcNormals[srcCorner[f][k]] - impNormals[impCorner[f][k]]).Length );

				if ( source.HasVertexColors && imported.HasVertexColors )
				{
					var d = source.VertexColors[a.Indices[k]] - imported.VertexColors[b.Indices[k]];
					colour = MathF.Max( colour, MathF.Sqrt( d.x * d.x + d.y * d.y + d.z * d.z + d.w * d.w ) );
				}
				else if ( source.HasVertexColors )
				{
					colour = float.PositiveInfinity;
				}
			}
		}

		Report.Check( $"{label}: every corner's position matches", position < Tolerance, $"worst {position:0.#####} in" );
		Report.Check( $"{label}: every corner's UV matches", uv < Tolerance, $"worst {uv:0.#####}" );
		Report.Check( $"{label}: every corner's normal matches", normal < Tolerance, $"worst {normal:0.#####}" );
		Report.Check( $"{label}: every face keeps its material slot", slots, "" );
		Report.Check( $"{label}: every vertex keeps its colour", colour < Tolerance, $"worst {colour:0.#####}" );
	}

	static bool HasPoint( PolyMesh mesh, Vec3 p ) => mesh.Positions.Any( q => (q - p).Length < Tolerance );

	static int Nearest( PolyMesh mesh, Vec3 p )
	{
		var best = 0;

		for ( var i = 1; i < mesh.VertexCount; i++ )
			if ( (mesh.Positions[i] - p).Length < (mesh.Positions[best] - p).Length )
				best = i;

		return best;
	}

	static Vec3 Bounds( PolyMesh mesh )
	{
		var min = mesh.Positions.Aggregate( ( a, b ) => new Vec3( MathF.Min( a.x, b.x ), MathF.Min( a.y, b.y ), MathF.Min( a.z, b.z ) ) );
		var max = mesh.Positions.Aggregate( ( a, b ) => new Vec3( MathF.Max( a.x, b.x ), MathF.Max( a.y, b.y ), MathF.Max( a.z, b.z ) ) );
		return max - min;
	}

	/// <summary>Largest per-bone weight difference, comparing by bone NAME across two skeletons.</summary>
	static float WeightDistance( BoneWeight[] a, BoneWeight[] b, Skeleton sa, Skeleton sb )
	{
		var byName = new Dictionary<string, float>();

		foreach ( var w in a )
			byName[sa.Bones[w.Bone].Name] = byName.GetValueOrDefault( sa.Bones[w.Bone].Name ) + w.Weight;

		foreach ( var w in b )
			byName[sb.Bones[w.Bone].Name] = byName.GetValueOrDefault( sb.Bones[w.Bone].Name ) - w.Weight;

		return byName.Values.Select( MathF.Abs ).DefaultIfEmpty( 0 ).Max();
	}
}
