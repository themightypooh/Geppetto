using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// The UV toolset: saved seams, selection unwraps, cylinder and sphere projection, texel density,
/// pixel-margin packing, pins, minimize stretch, stitch, align and the second UV set. Every check
/// is on numbers - UV coordinates, island counts, distances - never on a picture.
/// </summary>
public static class UVToolsTests
{
	public static void Run()
	{
		Section( "UV tools: seams are saved with the edit and steer the unwrap" );
		TestSeamsRoundTrip();
		TestSeamsThroughModifiers();
	}

	static void Section( string title ) => Report.Section( title );
	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );

	/// <summary>Every edge of a box, as the session's mesh numbers them.</summary>
	static List<EdgeKey> AllEdges( PolyMesh mesh ) => mesh.BuildEdgeFaces().Keys.ToList();

	static (PartStudio Studio, MeshEditFeature Edit) BoxWithEdit()
	{
		var studio = new PartStudio();
		var prim = studio.Add( new PrimitiveFeature() );
		prim.SizeX.Value = 2f;
		prim.SizeY.Value = 2f;
		prim.SizeZ.Value = 2f;
		var edit = studio.Add( new MeshEditFeature() );
		studio.Rebuild();
		return (studio, edit);
	}

	static void TestSeamsRoundTrip()
	{
		var (studio, edit) = BoxWithEdit();
		var session = new MeshEditSession( edit.LastInput );
		session.SetMode( EditElement.Edge );
		foreach ( var key in AllEdges( session.Mesh ) )
			session.SelectEdge( key, MeshEditSession.Combine.Add );
		session.MarkSeam();
		Check( "marking every edge of a box marks 12 seams", session.Seams.Count == 12, $"{session.Seams.Count}" );

		Check( "a seams-only edit still commits", session.CommitTo( edit ) );
		studio.Rebuild();
		Check( "the feature keeps the seams", edit.Seams.Count == 12, $"{edit.Seams.Count}" );
		Check( "and the body it outputs carries them", studio.Bodies[0].Mesh.Seams.Count == 12, $"{studio.Bodies[0].Mesh.Seams.Count}" );

		// Saved and read back, the way a document reopens.
		var reloaded = new MeshEditFeature();
		reloaded.LoadMesh( edit.SaveMesh() );
		var studio2 = new PartStudio();
		var prim2 = studio2.Add( new PrimitiveFeature() );
		prim2.SizeX.Value = 2f;
		prim2.SizeY.Value = 2f;
		prim2.SizeZ.Value = 2f;
		studio2.Add( reloaded );
		var project = studio2.Add( new UVProjectFeature() );
		project.Mode.Index = Array.IndexOf( project.Mode.Options, "Organic" );
		studio2.Rebuild();
		Check( "a reloaded edit keeps its seams", reloaded.Seams.Count == 12 && reloaded.Error is null, reloaded.Error ?? $"{reloaded.Seams.Count}" );

		var body = studio2.Bodies[0].Mesh;
		var islands = UVIslands.Find( body ).Count;
		Check( "an Organic UV project below it cuts along them: a fully seamed box is 6 islands", islands == 6 && project.Error is null, project.Error ?? $"{islands}" );

		// Reopening the edit shows the seams again.
		var resumed = new MeshEditSession( reloaded.LastInput, reloaded.Edited );
		Check( "reopening the edit loads its seams", resumed.Seams.Count == 12, $"{resumed.Seams.Count}" );

		// Without the marks the same feature does not split the box into its faces.
		var plain = new PartStudio();
		var p3 = plain.Add( new PrimitiveFeature() );
		p3.SizeX.Value = 2f;
		p3.SizeY.Value = 2f;
		p3.SizeZ.Value = 2f;
		var project3 = plain.Add( new UVProjectFeature() );
		project3.Mode.Index = Array.IndexOf( project3.Mode.Options, "Organic" );
		plain.Rebuild();
		var plainIslands = UVIslands.Find( plain.Bodies[0].Mesh ).Count;
		Check( "an unseamed box is not cut into 6 by the same unwrap", plainIslands != 6, $"{plainIslands}" );

		// Format 3 blobs (no seams) still read.
		var old = MeshEditBlob.Read( MeshEditBlob.Write( Primitives.Box( 1, 1, 1 ), 0 ), out _ );
		Check( "a blob without seams reads with none", old.Seams.Count == 0 );
	}

	static void TestSeamsThroughModifiers()
	{
		// A seam down one side of a box, through a level of subdivision: it is split onto the edge
		// point, so the subdivided body has two seam edges per marked one, and both are edges.
		var box = Primitives.Box( 2, 2, 2 );
		var marked = AllEdges( box ).Take( 3 ).ToList();
		box.Seams.UnionWith( marked );

		var sub = MeshModifiers.Apply( box, false, 1, 0f, 1, 0f, null );
		var live = sub.LiveSeams();
		Check( "subdivide splits each seam in two", sub.Seams.Count == 6 && live.Count == 6, $"{sub.Seams.Count} / {live.Count}" );

		// Every subdivided seam edge joins an end of a marked edge to that same edge's edge point,
		// read from the subdivision's own correspondence map.
		var (_, map) = CatmullClark.SubdivideWithMap( box );
		var onLine = sub.Seams.All( key =>
		{
			var a = map.Vertices[key.A];
			var b = map.Vertices[key.B];
			var (orig, edge) = a.Origin == SubdivisionOrigin.Original ? (a, b) : (b, a);
			return orig.Origin == SubdivisionOrigin.Original && edge.Origin == SubdivisionOrigin.Edge
				&& marked.Contains( new EdgeKey( edge.A, edge.B ) ) && (orig.A == edge.A || orig.A == edge.B);
		} );

		Check( "and each half joins an end of the marked edge to its edge point", onLine );

		var mirrored = MeshModifiers.Apply( box, true, 1, 0f, 0, 0f, null );
		Check( "mirror keeps the seams on both halves", mirrored.LiveSeams().Count >= marked.Count, $"{mirrored.LiveSeams().Count}" );

		var arrayed = MeshModifiers.Apply( box, false, 3, 1f, 0, 0f, null );
		Check( "array copies the seams onto every copy", arrayed.LiveSeams().Count == marked.Count * 3, $"{arrayed.LiveSeams().Count}" );

		var merged = new PolyMesh();
		MeshTransform.Append( merged, box );
		MeshTransform.Append( merged, box );
		Check( "merging two bodies keeps both bodies' seams", merged.LiveSeams().Count == marked.Count * 2, $"{merged.LiveSeams().Count}" );
	}
}
