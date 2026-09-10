using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Remesh, proven on the file that prompted it.
///
/// The feature exists because a Meshy export lands at hundreds of thousands of triangles and
/// nothing in the kernel could take any away. The DecimateTests prove the algorithm on primitives
/// — a sphere, a grid, a material seam — and say nothing about the real input: 46 MB, 450k
/// vertices, 900k triangles, fourteen genus-0 parts, vertex colour baked into the positions. That
/// is the case the feature was written for, and the two big parts (300k and 360k triangles) are the
/// ones that show whether the collapse loop is fast enough to sit in a rebuild rather than hang it.
///
/// WHAT THIS RUNS: read the OBJ with ObjReader.ReadPieces, then Decimate each piece to a budget
/// proportional to its share of a ~2% overall target — the same split the feature would make, done
/// directly rather than through a PartStudio so each part's time is reported on its own. It prints
/// a per-part table (in, out, reached, closed, worst error, time), writes the reduced model back
/// out as a multi-object OBJ that still has its parts, and renders a before/after contact sheet so the shape can be checked without
/// opening the editor.
///
/// Invoked as: Effigy.Tests.exe --remesh [outDir] [sourceObj]
/// </summary>
public static class RemeshGen
{
	/// <summary>The file that made Remesh necessary. A part-segmentation export, closed and genus-0,
	/// so it is the easy case for decimation — the same algorithm on a scan with holes would spend
	/// its budget holding borders instead of reducing triangles.</summary>
	const string DefaultSource =
		@"C:\Users\pooh\Downloads\Meshy_AI_Grinning Scrapmaster_1789007624_part-segmentation\Meshy_AI_Grinning_Scrapmaster_1789007624_part-segmentation.obj";

	/// <summary>2% of ~900k is ~18k triangles — dense enough to weight and pose, sparse enough to
	/// prove the reduction actually happened.</summary>
	const float BudgetFraction = 0.02f;

	public static int Run( string outDir, string sourcePath = null, float? keepPercent = null )
	{
		sourcePath = string.IsNullOrWhiteSpace( sourcePath ) ? DefaultSource : sourcePath;
		Directory.CreateDirectory( outDir );

		if ( !File.Exists( sourcePath ) )
		{
			Console.WriteLine( "remesh: source not found" );
			Console.WriteLine( "  " + sourcePath );
			Console.WriteLine( "  pass a second argument: --remesh <outDir> <sourceObj>" );
			return 1;
		}

		var clock = Stopwatch.StartNew();
		var pieces = ObjReader.ReadPieces( File.ReadAllText( sourcePath ) );
		clock.Stop();

		var total = pieces.Sum( p => Decimate.TriangleCount( p.Mesh ) );

		// The default is the one the feature was proven at; the argument is for the second look you
		// take once you have seen the first — "a bit less than that" is the normal follow-up, and
		// rebuilding the generator to change a const is not the way to answer it.
		var fraction = keepPercent.HasValue
			? Math.Clamp( keepPercent.Value / 100f, 0.0001f, 1f )
			: BudgetFraction;
		var budget = Math.Max( 4, (int)MathF.Round( total * fraction ) );

		Console.WriteLine( $"read {Path.GetFileName( sourcePath )} in {clock.ElapsedMilliseconds} ms" );
		Console.WriteLine( $"  {pieces.Count} pieces, {pieces.Sum( p => p.Mesh.VertexCount ):N0} vertices, "
			+ $"{total:N0} triangles" );
		Console.WriteLine( $"  budget {fraction * 100f:0.###}% of {total:N0} = {budget:N0} triangles, "
			+ "split proportionally to each part's size" );
		Console.WriteLine();

		Console.WriteLine( $"  {"part",-12} {"in",9} {"out",9} {"reached",8} {"closed",7} {"err",10} {"ms",7}" );

		var reduced = new List<ObjReader.ObjPiece>();
		var allReached = true;
		var allClosed = true;
		var totalIn = 0;
		var totalOut = 0;
		var worstRel = 0f;

		foreach ( var piece in pieces )
		{
			var before = Decimate.TriangleCount( piece.Mesh );
			var target = Math.Max( 4, (int)MathF.Round( budget * ( before / (float)total ) ) );
			var diagonal = piece.Mesh.BoundsDiagonal;

			var sw = Stopwatch.StartNew();
			var result = Decimate.Run( piece.Mesh, new Decimate.Options { TargetTriangles = target } );
			sw.Stop();

			var validation = MeshValidator.Validate( result.Mesh );

			// WorstError is squared distance in model units, so it is only meaningful against the
			// model's own size. Dividing by the diagonal squared makes it a dimensionless "how far
			// did the worst collapse move a vertex, as a fraction of the whole model".
			var rel = diagonal > 1e-6f ? result.WorstError / ( diagonal * diagonal ) : 0f;

			reduced.Add( new ObjReader.ObjPiece( piece.Name, result.Mesh ) );
			allReached &= result.ReachedTarget;
			allClosed &= validation.IsClosed;
			totalIn += before;
			totalOut += result.ToTriangles;
			worstRel = MathF.Max( worstRel, rel );

			Console.WriteLine( $"  {piece.Name,-12} {before,9:N0} {result.ToTriangles,9:N0} "
				+ $"{( result.ReachedTarget ? "yes" : "NO" ),8} {( validation.IsClosed ? "yes" : "NO" ),7} "
				+ $"{rel,10:0.###e+0} {sw.ElapsedMilliseconds,7}" );
		}

		Console.WriteLine();
		Console.WriteLine( $"  {totalIn:N0} -> {totalOut:N0} triangles ({totalOut * 100f / totalIn:0.#}%), "
			+ $"worst error {worstRel:0.###e+0} of the bounds diagonal" );

		if ( !allReached )
			Console.WriteLine( "  note: a part stopped before its target — see the table" );
		if ( !allClosed )
			Console.WriteLine( "  note: a part came out open — see the table" );

		// --- the outputs ---------------------------------------------------------------------

		// FOURTEEN OBJECTS, NOT ONE. This used to Combine() the pieces into a single mesh before
		// writing, which threw away the segmentation that was the whole reason for using a
		// part-segmentation export: the file went in as fourteen parts and came back as one lump,
		// with nothing left to rig, hide or budget per part.
		var objPath = Path.Combine( outDir, "remesh_all.obj" );
		ObjWriter.WriteFile( reduced, objPath );

		// AND A .vmdl BESIDE IT, because an OBJ on its own is not something the engine will show
		// you. Making one by hand in ModelDoc is what people did instead, and ModelDoc defaults
		// import_rotation to zero — which for an OBJ is the cyclic axis permutation described in
		// VmdlDocument.ImportRotation, i.e. the character compiles lying on its side. Writing it
		// here means the correction comes along with the mesh.
		var vmdlPath = Path.Combine( outDir, "remesh_all.vmdl" );
		File.WriteAllText( vmdlPath, VmdlDocument.Static( "models/effigy/remesh_all.obj",
			VmdlPhysics.MeshFromRender() ) );

		// Before and after, side by side, for the two parts whose size is the whole point. The
		// caption under each tile carries its vertex/face counts, so the reduction is visible even
		// where the two shapes look the same — which, for a good decimation, is exactly what they
		// should do.
		var big = pieces
			.Zip( reduced, ( original, reduced ) => (original, reduced) )
			.OrderByDescending( t => Decimate.TriangleCount( t.original.Mesh ) )
			.Take( 2 )
			.ToList();

		var tiles = new List<PngPreview.Tile>();

		foreach ( var (original, shrunk) in big )
		{
			tiles.Add( new PngPreview.Tile( original.Mesh, $"{original.Name} in" ) );
			tiles.Add( new PngPreview.Tile( shrunk.Mesh, $"{original.Name} out" ) );
		}

		tiles.Add( new PngPreview.Tile( big[0].reduced.Mesh, "out, wireframe", wireframe: true ) );

		var pngPath = Path.Combine( outDir, "remesh_preview.png" );
		PngPreview.WriteSheet( tiles, pngPath, columns: 2, tileSize: 400 );

		Console.WriteLine();
		Console.WriteLine( $"OBJ {objPath} ({reduced.Count} objects)" );
		Console.WriteLine( "VMDL " + vmdlPath );
		Console.WriteLine( "PNG " + pngPath );
		Console.WriteLine( "DONE" );
		return 0;
	}

}
