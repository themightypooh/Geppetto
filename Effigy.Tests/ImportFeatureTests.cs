using System;
using System.IO;
using System.Linq;
using System.Text;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Import: a mesh file becomes a body, the triangles stay out of the .effigy text, and a save
/// plus a load still has them.
///
/// The claim this exists to protect is the one the Host handoff made: Effigy is constructive-only,
/// so a sculpt could not be painted or weighted until it was a body. An Import that writes the
/// mesh into the document would "work" and still be the wrong answer — megabytes of vertices in a
/// format whose virtue is being readable. The sidecar tests are the ones that would fail if that
/// happened.
/// </summary>
public static class ImportFeatureTests
{
	public static void Run()
	{
		Report.Section( "import: an OBJ becomes a body" );
		TestLoadsAnObj();
		TestPreservesQuadsAndUvs();
		TestMissingSourceFailsUsefully();
		TestWrongFormatFailsUsefully();
		TestEmptyFileFailsUsefully();

		Report.Section( "import: the mesh stays out of the document" );
		TestDocumentHoldsThePathNotTheMesh();
		TestSidecarCarriesTheMeshAcrossASaveAndLoad();
		TestSavingDoesNotDeleteASidecarItDidNotWrite();

		Report.Section( "import: the body is a body, so the rest of the tool can see it" );
		TestPaintAndSkinSeeTheBody();

		Report.Section( "import: a file with several objects becomes several parts" );
		TestObjectsBecomeSeparateBodies();
		TestSplitRenumbersEachPiece();
		TestOneObjectKeepsTheFeatureName();
		TestFusedReadIsUnchanged();
	}

	/// <summary>Three objects in the file, three rows in the Parts list. The point of the whole
	/// split: a character exported with its brows kept separate has to arrive separate, because a
	/// welded body cannot be hidden, re-materialled or weighted a piece at a time.</summary>
	static void TestObjectsBecomeSeparateBodies()
	{
		var studio = StudioFromObj( ThreeObjects, out _ );
		var report = studio.Rebuild();

		Report.Check( "rebuilds without errors", !report.HasErrors, report.ToString() );
		Report.Check( "three objects become three bodies",
			studio.Bodies.Count == 3, $"{studio.Bodies.Count} bodies" );
		Report.Check( "each body carries the exporter's name, prefixed by the feature's",
			studio.Bodies[0].Name == "Sculpt / head"
			&& studio.Bodies[1].Name == "Sculpt / brow_l"
			&& studio.Bodies[2].Name == "Sculpt / brow_r",
			string.Join( " | ", studio.Bodies.Select( b => b.Name ) ) );
		Report.Check( "and its own body id, so a selection can tell them apart",
			studio.Bodies.Select( b => b.Id ).Distinct().Count() == 3 );
	}

	/// <summary>OBJ indices are global to the FILE. A piece that kept them would index off the end
	/// of its own shorter vertex list, or into somebody else's geometry.</summary>
	static void TestSplitRenumbersEachPiece()
	{
		var studio = StudioFromObj( ThreeObjects, out _ );
		studio.Rebuild();

		var brow = studio.Bodies[1].Mesh;

		Report.Check( "a piece holds only the vertices its own faces use",
			brow.VertexCount == 3, $"{brow.VertexCount} vertices" );
		Report.Check( "its face indexes them locally",
			brow.Faces[0].Indices.SequenceEqual( new[] { 0, 1, 2 } ),
			string.Join( ",", brow.Faces[0].Indices ) );
		Report.Check( "and the positions are the ones the file gave that object",
			MathF.Abs( brow.Positions[0].x - 5f ) < 1e-5f, $"x={brow.Positions[0].x}" );
	}

	/// <summary>The common case must read exactly as it did before the split existed - one object,
	/// or an exporter that wrote no marker at all, is still one row called what the feature is
	/// called.</summary>
	static void TestOneObjectKeepsTheFeatureName()
	{
		var studio = StudioFromObj( "o mesh_node\nv 0 0 0\nv 1 0 0\nv 1 1 0\nf 1 2 3\n", out _ );
		studio.Rebuild();

		Report.Check( "one object is one body", studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );
		Report.Check( "named after the feature, not after the object",
			studio.Bodies[0].Name == "Sculpt", studio.Bodies[0].Name );

		var bare = StudioFromObj( "v 0 0 0\nv 1 0 0\nv 1 1 0\nf 1 2 3\n", out _ );
		bare.Rebuild();

		Report.Check( "a file with no object marker at all is still one body",
			bare.Bodies.Count == 1 && bare.Bodies[0].Name == "Sculpt",
			$"{bare.Bodies.Count} bodies" );
	}

	/// <summary>ObjReader.Read is what the writers' round-trip tests compare against, so the split
	/// must not have changed what it returns.</summary>
	static void TestFusedReadIsUnchanged()
	{
		var fused = ObjReader.Read( ThreeObjects );

		Report.Check( "Read still fuses every object into one mesh",
			fused.VertexCount == 10 && fused.FaceCount == 3,
			$"{fused.VertexCount}v {fused.FaceCount}f" );

		var pieces = ObjReader.ReadPieces( ThreeObjects );

		Report.Check( "and ReadPieces accounts for exactly the same faces",
			pieces.Sum( p => p.Mesh.FaceCount ) == fused.FaceCount,
			$"{pieces.Sum( p => p.Mesh.FaceCount )} vs {fused.FaceCount}" );
	}

	/// <summary>A head and two brows, kept apart the way an exporter keeps them apart.</summary>
	const string ThreeObjects =
		"o head\nv 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 3 4\n" +
		"o brow_l\nv 5 0 0\nv 6 0 0\nv 6 1 0\nf 5 6 7\n" +
		"o brow_r\nv 8 0 0\nv 9 0 0\nv 9 1 0\nf 8 9 10\n";

	static PartStudio StudioFromObj( string obj, out ImportFeature import )
	{
		var studio = new PartStudio();
		import = studio.Add( new ImportFeature() );
		import.Name = "Sculpt";
		import.LoadMesh( Encoding.UTF8.GetBytes( obj ) );
		return studio;
	}

	static void TestLoadsAnObj()
	{
		var box = Primitives.Box( 2f, 3f, 4f );
		var studio = StudioFromMesh( box, out var import );
		var report = studio.Rebuild();

		Report.Check( "rebuilds without errors", !report.HasErrors, report.ToString() );
		Report.Check( "produces one body", studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );
		Report.Check( "with the OBJ's vertex count",
			studio.Bodies[0].Mesh.VertexCount == box.VertexCount,
			$"{box.VertexCount} -> {studio.Bodies[0].Mesh.VertexCount}" );
		Report.Check( "and its face count",
			studio.Bodies[0].Mesh.FaceCount == box.FaceCount,
			$"{box.FaceCount} -> {studio.Bodies[0].Mesh.FaceCount}" );
		Report.Check( "named after the feature",
			studio.Bodies[0].Id == import.Id + "b0", studio.Bodies[0].Id );
	}

	static void TestPreservesQuadsAndUvs()
	{
		var box = Primitives.Box();
		var studio = StudioFromMesh( box, out _ );
		studio.Rebuild();

		var mesh = studio.Bodies[0].Mesh;
		var quads = mesh.Faces.Count( f => f.Count == 4 );

		Report.Check( "a box stays quads, not a triangulation",
			quads == box.FaceCount, $"{quads} quads of {mesh.FaceCount} faces" );

		var src = box.Faces[0].UVs[0];
		var dst = mesh.Faces[0].UVs[0];
		Report.Check( "UVs survive the writer flip and the reader un-flip",
			MathF.Abs( src.x - dst.x ) < 1e-4f && MathF.Abs( src.y - dst.y ) < 1e-4f,
			$"({src.x},{src.y}) -> ({dst.x},{dst.y})" );
	}

	static void TestMissingSourceFailsUsefully()
	{
		var studio = new PartStudio();
		var import = studio.Add( new ImportFeature() );
		import.Source.Value = Path.Combine( Path.GetTempPath(), "effigy-no-such-mesh.obj" );
		studio.Rebuild();

		Report.Check( "a missing file fails rather than producing an empty body",
			import.Error is not null && studio.Bodies.Count == 0, import.Error ?? "built anyway" );

		var diagnostic = import.Diagnostic;
		Report.Check( "and the message names the path, with a cause and a remedy",
			diagnostic is not null
			&& diagnostic.Cause.Contains( "effigy-no-such-mesh.obj" )
			&& diagnostic.Remedies.Count > 0,
			$"{diagnostic?.Cause} | remedies={diagnostic?.Remedies.Count ?? 0}" );
	}

	static void TestWrongFormatFailsUsefully()
	{
		var studio = new PartStudio();
		var import = studio.Add( new ImportFeature() );
		import.Source.Value = Path.Combine( Path.GetTempPath(), "sculpt.fbx" );
		File.WriteAllText( import.Source.Value, "not an obj" );

		try
		{
			studio.Rebuild();
		}
		finally
		{
			File.Delete( import.Source.Value );
		}

		Report.Check( "an FBX is refused by extension, not parsed",
			import.Error is not null && import.Error.Contains( "OBJ" ), import.Error ?? "built anyway" );
		Report.Check( "and the cause names FBX",
			import.Diagnostic is not null && import.Diagnostic.Cause.Contains( ".fbx" ),
			import.Diagnostic?.Cause ?? "" );
	}

	static void TestEmptyFileFailsUsefully()
	{
		var studio = new PartStudio();
		var import = studio.Add( new ImportFeature() );
		import.LoadMesh( Encoding.UTF8.GetBytes( "# empty\n" ) );
		studio.Rebuild();

		Report.Check( "an OBJ with no faces fails",
			import.Error is not null && import.Error.Contains( "empty" ), import.Error ?? "built anyway" );
	}

	static void TestDocumentHoldsThePathNotTheMesh()
	{
		var box = Primitives.Box();
		var studio = StudioFromMesh( box, out var import );
		import.Source.Value = @"C:\sculpts\host.obj";
		studio.Rebuild();

		var text = StudioDocument.Write( studio );

		Report.Check( "the document names the feature", text.Contains( "ImportFeature" ) );
		Report.Check( "and carries the source path", text.Contains( @"C:\sculpts\host.obj" ) );
		Report.Check( "and does not contain the vertex list",
			!text.Contains( "v " ) && !ContainsVertexDump( text ),
			"document looks like it grew a mesh" );

		var back = StudioDocument.Read( text );
		var reloaded = back.Features.OfType<ImportFeature>().Single();
		Report.Check( "the path survives the round trip",
			reloaded.Source.Value == import.Source.Value, reloaded.Source.Value );
	}

	static void TestSidecarCarriesTheMeshAcrossASaveAndLoad()
	{
		var dir = Path.Combine( Path.GetTempPath(), $"effigy-import-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( dir );

		try
		{
			var path = Path.Combine( dir, "model" + StudioDocument.Extension );
			var box = Primitives.Box( 2f, 3f, 4f );
			var studio = StudioFromMesh( box, out var import );
			studio.Rebuild();
			var before = studio.Bodies[0].Mesh.Clone();

			StudioDocument.WriteFile( studio, path );
			var written = ImportSidecar.Save( studio, path );

			Report.Check( "saving writes one OBJ beside the document",
				written == 1 && File.Exists( ImportSidecar.PathFor( path, import.Id ) ),
				$"wrote {written}" );

			var back = StudioDocument.ReadFile( path );
			var loaded = ImportSidecar.Load( back, path );
			var report = back.Rebuild();
			var reloaded = back.Features[0] as ImportFeature;

			Report.Check( "loading hands the OBJ to the feature that owns it",
				loaded == 1 && reloaded is not null );
			Report.Check( "the reloaded document rebuilds without errors",
				!report.HasErrors, report.ToString() );
			Report.Check( "the mesh is on the model again, same vertex count",
				back.Bodies.Count == 1 && back.Bodies[0].Mesh.VertexCount == before.VertexCount,
				back.Bodies.Count == 0 ? "no body" : $"{back.Bodies[0].Mesh.VertexCount} verts" );
			Report.Check( "and the same volume",
				MathF.Abs( back.Bodies[0].Mesh.SignedVolume() - before.SignedVolume() ) < 1e-4f,
				$"{before.SignedVolume()} -> {back.Bodies[0].Mesh.SignedVolume()}" );
		}
		finally
		{
			Directory.Delete( dir, recursive: true );
		}
	}

	static void TestSavingDoesNotDeleteASidecarItDidNotWrite()
	{
		var dir = Path.Combine( Path.GetTempPath(), $"effigy-import-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( dir );

		try
		{
			var path = Path.Combine( dir, "model" + StudioDocument.Extension );
			var studio = StudioFromMesh( Primitives.Box(), out var import );
			studio.Rebuild();
			ImportSidecar.Save( studio, path );

			var stray = Path.Combine( ImportSidecar.DirectoryFor( path ), "deadbeef.obj" );
			File.WriteAllText( stray, "v 0 0 0\n" );

			ImportSidecar.Save( studio, path );

			Report.Check( "saving leaves an OBJ whose feature it does not know about", File.Exists( stray ) );

			var pruned = ImportSidecar.Prune( studio, path );

			Report.Check( "and pruning removes it only when asked",
				pruned == 1 && !File.Exists( stray ) && File.Exists( ImportSidecar.PathFor( path, import.Id ) ),
				$"pruned {pruned}" );
		}
		finally
		{
			Directory.Delete( dir, recursive: true );
		}
	}

	static void TestPaintAndSkinSeeTheBody()
	{
		var studio = StudioFromMesh( Primitives.Box(), out var import );
		studio.Rebuild();

		var paint = studio.Add( new PaintFeature() );
		paint.Bodies.BodyIds.Add( studio.Bodies[0].Id );
		var painted = studio.Rebuild();

		Report.Check( "a Paint on the imported body rebuilds",
			painted.HasErrors == false && paint.Error is null,
			paint.Error ?? painted.ToString() );

		var skeleton = new Skeleton();
		skeleton.AddBone( "root", -1, Xform.Identity, 1f );
		var weights = SkinBinder.BindRigid( studio.Bodies[0].Mesh, skeleton );

		Report.Check( "SkinBinder binds every imported vertex",
			weights.Count == studio.Bodies[0].Mesh.VertexCount, $"{weights.Count} weights" );
		Report.Check( "and the partition of unity holds",
			Enumerable.Range( 0, weights.Count ).All( i =>
			{
				var sum = 0f;
				foreach ( var w in weights[i] ) sum += w.Weight;
				return MathF.Abs( sum - 1f ) < 1e-4f;
			} ) );
	}

	static PartStudio StudioFromMesh( PolyMesh mesh, out ImportFeature import )
	{
		var studio = new PartStudio();
		import = studio.Add( new ImportFeature() );
		import.Name = "Sculpt";
		import.LoadMesh( Encoding.UTF8.GetBytes( ObjWriter.Write( mesh, "sculpt" ) ) );
		return studio;
	}

	/// <summary>A document that serialised the mesh would grow a vertex-per-line dump. The path
	/// line is allowed to contain a letter v.</summary>
	static bool ContainsVertexDump( string text )
	{
		foreach ( var line in text.Split( '\n' ) )
		{
			var t = line.Trim();

			if ( t.StartsWith( "v ", StringComparison.Ordinal ) )
				return true;
		}

		return false;
	}
}
