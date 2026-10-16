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

		Section( "UV tools: unwrap the selected faces only, and Organic from Edit mode" );
		TestUnwrapSelected();
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

	static Vec2[][] CopyUVs( PolyMesh mesh ) => mesh.Faces.Select( f => (Vec2[])f.UVs.Clone() ).ToArray();

	static void TestUnwrapSelected()
	{
		var session = new MeshEditSession( Primitives.Cylinder( 1f, 3f, 16 ) );
		session.Unwrap();
		var before = CopyUVs( session.Mesh );
		Check( "the whole cylinder unwraps first", NormalBake.Measure( session.Mesh ).CanBake, NormalBake.Measure( session.Mesh ).Problem );

		// Pick the wall's first four quads and re-unwrap just those.
		var mesh = session.Mesh;
		var wall = Enumerable.Range( 0, mesh.FaceCount ).Where( f => mesh.Faces[f].Count == 4 ).Take( 4 ).ToList();
		var (oldMin, oldMax) = UVIslands.Bounds( mesh, wall );
		session.SetMode( EditElement.Face );
		foreach ( var f in wall )
			session.SelectFace( f, MeshEditSession.Combine.Add );
		var report = session.Unwrap();

		var untouched = true;
		var changed = false;
		for ( var f = 0; f < session.Mesh.FaceCount; f++ )
		{
			var same = session.Mesh.Faces[f].UVs.Zip( before[f], ( a, b ) => (a - b).Length < 1e-7f ).All( x => x );
			if ( wall.Contains( f ) )
				changed |= !same;
			else
				untouched &= same;
		}

		Check( "unwrapping a selection leaves every other face's UVs exactly as they were", untouched );
		Check( "and does lay out the selected faces", changed && report.Faces == wall.Count, report.ToString() );

		var (newMin, newMax) = UVIslands.Bounds( session.Mesh, wall );
		Check( "the new islands land inside the box the old ones filled",
			newMin.x >= oldMin.x - 1e-4f && newMin.y >= oldMin.y - 1e-4f && newMax.x <= oldMax.x + 1e-4f && newMax.y <= oldMax.y + 1e-4f,
			$"old {oldMin}..{oldMax} new {newMin}..{newMax}" );
		Check( "four flat-ish neighbouring quads come out as one island", UVIslands.Find( Sub( session.Mesh, wall ) ).Count == 1 );
		Check( "it is one undo step", session.LastLabel == "Unwrap" );

		// Organic, from the session, on the whole mesh.
		session.ClearSelection();
		var organic = session.Unwrap( 66f, 0.01f, UnwrapMode.Organic );
		Check( "Organic unwrap runs from Edit mode", organic.Faces == session.Mesh.FaceCount && session.LastLabel == "Organic unwrap", organic.ToString() );
		Check( "and leaves UVs a bake can use", NormalBake.Measure( session.Mesh ).CanBake, NormalBake.Measure( session.Mesh ).Problem );

		var refused = false;
		try { UVUnwrap.UnwrapFaces( session.Mesh, new int[0] ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "unwrapping no faces is refused with a reason", refused );
	}

	/// <summary>Just these faces, sharing the mesh's vertices.</summary>
	static PolyMesh Sub( PolyMesh mesh, IEnumerable<int> faces )
	{
		var piece = new PolyMesh { Positions = mesh.Positions };
		foreach ( var f in faces )
			piece.Faces.Add( mesh.Faces[f] );
		return piece;
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
