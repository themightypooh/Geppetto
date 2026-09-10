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
		TestPiecesSurviveAWrite();

		Report.Section( "import: deleting a part takes one piece, not the whole file" );
		TestDeletingOnePieceKeepsTheRest();
		TestRemovedPieceSurvivesASave();
		TestAnUnchangedSourceIsNotReadAgain();
	}

	/// <summary>
	/// Several parts in, the same several parts out.
	///
	/// The failure this pins down had nothing to do with the reader: RemeshGen decimated fourteen
	/// segmented parts perfectly and then merged them into one mesh to write the file, so what came
	/// back was one lump and the segmentation — the reason for using that export at all — was gone.
	/// OBJ numbers v/vt/vn across the whole FILE rather than per object, so a multi-object write is
	/// exactly where offsets go wrong, and an off-by-one there reads back as parts wearing each
	/// other's geometry rather than as a parse error.
	/// </summary>
	static void TestPiecesSurviveAWrite()
	{
		var pieces = ObjReader.ReadPieces( ThreeObjects );
		var round = ObjReader.ReadPieces( ObjWriter.Write( pieces ) );

		Report.Check( "a multi-object write comes back as the same number of parts",
			round.Count == pieces.Count, $"{pieces.Count} in, {round.Count} out" );

		for ( var i = 0; i < Math.Min( pieces.Count, round.Count ); i++ )
		{
			Report.Check( $"part {i} keeps its name",
				round[i].Name == pieces[i].Name, $"'{pieces[i].Name}' -> '{round[i].Name}'" );

			Report.Check( $"part {i} keeps its geometry",
				round[i].Mesh.VertexCount == pieces[i].Mesh.VertexCount
				&& round[i].Mesh.FaceCount == pieces[i].Mesh.FaceCount,
				$"{pieces[i].Mesh.VertexCount}v/{pieces[i].Mesh.FaceCount}f -> "
					+ $"{round[i].Mesh.VertexCount}v/{round[i].Mesh.FaceCount}f" );

			// The positions themselves, not just the counts. Two parts of the same size are what a
			// bad offset swaps, and counts alone would call that a pass.
			var moved = 0;

			for ( var v = 0; v < Math.Min( round[i].Mesh.VertexCount, pieces[i].Mesh.VertexCount ); v++ )
			{
				if ( (round[i].Mesh.Positions[v] - pieces[i].Mesh.Positions[v]).Length > 1e-4f )
					moved++;
			}

			Report.Check( $"part {i} is where it was", moved == 0, $"{moved} vertices moved" );
		}
	}

	/// <summary>Deleting one part of a multi-object import removes that piece and leaves the rest,
	/// instead of the whole import going. The point of the whole split: an imported character's
	/// brows are separate precisely so they can be thrown away one at a time.</summary>
	static void TestDeletingOnePieceKeepsTheRest()
	{
		var studio = StudioFromObj( ThreeObjects, out var import );
		studio.Rebuild();

		Report.Check( "every piece of a three-piece import can be removed as a piece",
			import.CanRemovePiece( studio.Bodies[0].Id )
			&& import.CanRemovePiece( studio.Bodies[1].Id )
			&& import.CanRemovePiece( studio.Bodies[2].Id ) );

		import.RemovePiece( studio.Bodies[0].Id );
		studio.MarkDirty( import );
		studio.Rebuild();

		Report.Check( "removing one piece leaves the other two",
			studio.Bodies.Count == 2, $"{studio.Bodies.Count} bodies" );
		Report.Check( "and the survivors keep their ids rather than shifting",
			studio.Bodies[0].Id == import.Id + "b1" && studio.Bodies[1].Id == import.Id + "b2",
			string.Join( " | ", studio.Bodies.Select( b => b.Id ) ) );

		import.RemovePiece( studio.Bodies[0].Id );
		studio.MarkDirty( import );
		studio.Rebuild();

		Report.Check( "the last remaining piece is no longer removable as a piece",
			studio.Bodies.Count == 1 && !import.CanRemovePiece( studio.Bodies[0].Id ),
			$"{studio.Bodies.Count} bodies" );
	}

	/// <summary>A deleted piece has to survive a save and load, or the part comes back the next
	/// time the document is opened. The removal lives in the document; the OBJ sidecar keeps every
	/// piece, so the deleted one is skipped on the way back in.</summary>
	static void TestRemovedPieceSurvivesASave()
	{
		var dir = Path.Combine( Path.GetTempPath(), $"effigy-import-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( dir );

		try
		{
			var path = Path.Combine( dir, "model" + StudioDocument.Extension );
			var studio = StudioFromObj( ThreeObjects, out var import );
			studio.Rebuild();

			import.RemovePiece( studio.Bodies[1].Id );
			studio.MarkDirty( import );
			studio.Rebuild();

			StudioDocument.WriteFile( studio, path );
			ImportSidecar.Save( studio, path );

			var back = StudioDocument.ReadFile( path );
			ImportSidecar.Load( back, path );
			back.Rebuild();

			var reloaded = back.Features.OfType<ImportFeature>().Single();

			Report.Check( "the removed piece is recorded in the document",
				reloaded.RemovedPieces.Count == 1 && reloaded.RemovedPieces[0] == 1,
				string.Join( ",", reloaded.RemovedPieces ) );
			Report.Check( "and it stays gone after a save and load",
				back.Bodies.Count == 2, $"{back.Bodies.Count} bodies" );
		}
		finally
		{
			Directory.Delete( dir, recursive: true );
		}
	}

	/// <summary>Deleting a piece re-runs the import, and that must not read the file again. It used
	/// to: every rebuild parsed the whole source, so trimming a 900k-face import cost 1.3s per delete
	/// however little of it was left. Proven without a stopwatch — the file is swapped for a comment
	/// of the same size and write time, which only a re-read could notice.</summary>
	static void TestAnUnchangedSourceIsNotReadAgain()
	{
		var dir = Path.Combine( Path.GetTempPath(), $"effigy-import-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( dir );

		try
		{
			var path = Path.Combine( dir, "model.obj" );
			File.WriteAllText( path, ThreeObjects );

			var studio = new PartStudio();
			var import = studio.Add( new ImportFeature() );
			import.BindSource( path );
			studio.Rebuild();

			var size = (int)new FileInfo( path ).Length;
			var written = File.GetLastWriteTimeUtc( path );
			File.WriteAllBytes( path, Enumerable.Repeat( (byte)'#', size ).ToArray() );
			File.SetLastWriteTimeUtc( path, written );

			import.RemovePiece( studio.Bodies[0].Id );
			studio.MarkDirty( import );
			var report = studio.Rebuild();

			Report.Check( "deleting a piece publishes the pieces already parsed, not the file again",
				!report.HasErrors && studio.Bodies.Count == 2, $"{studio.Bodies.Count} bodies; {report}" );

			// Exporting over the same path changes its size and write time, and has to be picked up.
			var box = Primitives.Box( 1f, 1f, 1f );
			File.WriteAllText( path, ObjWriter.Write( new[]
			{
				new ObjReader.ObjPiece( "a", box ), new ObjReader.ObjPiece( "b", box ),
				new ObjReader.ObjPiece( "c", box ), new ObjReader.ObjPiece( "d", box ),
			} ) );

			studio.MarkDirty( import );
			report = studio.Rebuild();

			Report.Check( "a source exported over the same path is read again",
				!report.HasErrors && studio.Bodies.Count == 3, $"{studio.Bodies.Count} bodies; {report}" );
		}
		finally
		{
			Directory.Delete( dir, recursive: true );
		}
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
