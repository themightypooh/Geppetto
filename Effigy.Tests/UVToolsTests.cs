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

		Section( "UV tools: cylinder and sphere projection" );
		TestCylinderProjection();
		TestSphereProjection();

		Section( "UV tools: texel density" );
		TestTexelDensity();
	}

	static void TestTexelDensity()
	{
		// A 4 x 4 plane filling the unit square on a 1024 texture: 1024 px over 4 units = 256 px/unit.
		var plane = Primitives.Plane( 4, 4, 2, 2 );
		UVProjection.PlanarProject( plane, new Vec3( 0, 0, 1 ), 4f );
		var ppu = UVDensity.PixelsPerUnit( plane, Enumerable.Range( 0, plane.FaceCount ), 1024 );
		Check( "a 4-unit plane on a whole 1024 texture is 256 px per unit", MathF.Abs( ppu - 256f ) < 0.01f, $"{ppu}" );
		Check( "and 512 on a 2048", MathF.Abs( UVDensity.PixelsPerUnit( plane, Enumerable.Range( 0, plane.FaceCount ), 2048 ) - 512f ) < 0.02f );

		// Two boxes of different size in one mesh, unwrapped together: their islands differ in
		// surface but share one density, because the unwrap packs at one scale.
		var mesh = Primitives.Box( 2, 2, 2 );
		var big = Primitives.Box( 6, 6, 6 );
		for ( var i = 0; i < big.Positions.Count; i++ ) big.Positions[i] += new Vec3( 10, 0, 0 );
		MeshTransform.Append( mesh, big );
		UVUnwrap.Unwrap( mesh );
		var report = UVDensity.Measure( mesh, 1024 );
		Check( "an unwrap gives every island the same density", report.Islands.Count >= 2 && report.Max - report.Min < 0.02f * report.Max, report.ToString() );

		// Size only the small box's islands up to 3x the density.
		var target = report.PixelsPerUnit * 3f;
		var small = Enumerable.Range( 0, 6 ).ToList();
		var session = new MeshEditSession( mesh );
		session.SetMode( EditElement.Face );
		foreach ( var f in small )
			session.SelectFace( f, MeshEditSession.Combine.Add );
		var scaled = session.SetTexelDensity( target, 1024 );
		var after = UVDensity.Measure( session.Mesh, 1024 );
		var smallIslands = after.Islands.Where( i => UVIslands.Find( session.Mesh ).Islands[i.Index].All( f => f < 6 ) ).ToList();
		var bigIslands = after.Islands.Where( i => UVIslands.Find( session.Mesh ).Islands[i.Index].All( f => f >= 6 ) ).ToList();
		Check( "Set texel density hits the target on every selected island", scaled > 0 && smallIslands.Count > 0 && smallIslands.All( i => MathF.Abs( i.PixelsPerUnit - target ) < 0.01f * target ),
			string.Join( ", ", smallIslands.Select( i => i.PixelsPerUnit.ToString( "0.##" ) ) ) + $" want {target:0.##}" );
		Check( "and leaves the unselected islands' density alone", bigIslands.All( i => MathF.Abs( i.PixelsPerUnit - report.PixelsPerUnit ) < 0.02f * report.PixelsPerUnit ) );
		Check( "the session reads the selection's density", MathF.Abs( session.TexelDensity( 1024 ).PixelsPerUnit - target ) < 0.01f * target, session.TexelDensity( 1024 ).ToString() );
		Check( "one undo step", session.LastLabel == "Set texel density" );

		var refused = false;
		try { session.SetTexelDensity( 0f, 1024 ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "a density of zero is refused with a reason", refused );
	}

	static float SignedArea( Vec2[] uv )
	{
		var a = 0f;
		for ( var i = 0; i < uv.Length; i++ )
		{
			var p = uv[i];
			var q = uv[(i + 1) % uv.Length];
			a += p.x * q.y - q.x * p.y;
		}

		return a * 0.5f;
	}

	static void TestCylinderProjection()
	{
		const int segments = 16;
		const float radius = 2f;
		const float height = 6f;
		var cyl = Primitives.Cylinder( radius, height, segments );
		UVProjection.CylinderProject( cyl, new Vec3( 0, 0, 1 ) );

		var wall = Enumerable.Range( 0, cyl.FaceCount ).Where( f => MathF.Abs( cyl.FaceNormal( cyl.Faces[f] ).z ) < 0.1f ).ToList();
		Check( "a 16-sided cylinder has 16 wall quads", wall.Count == segments, $"{wall.Count}" );

		// Each wall quad spans exactly 1/16 of U - including the one across the wrap.
		var worstU = wall.Max( f => MathF.Abs( cyl.Faces[f].UVs.Max( u => u.x ) - cyl.Faces[f].UVs.Min( u => u.x ) - 1f / segments ) );
		Check( "every wall quad spans one sixteenth of U, the wrap-seam quad too", worstU < 1e-4f, $"worst {worstU:0.######}" );

		// U is the angle: the vertex at angle t sits at U = t / 2pi + 0.5 (mod 1).
		var angleOk = true;
		foreach ( var f in wall )
		{
			var face = cyl.Faces[f];
			for ( var i = 0; i < face.Count; i++ )
			{
				var p = cyl.Positions[face.Indices[i]];
				var want = MathF.Atan2( p.y, p.x ) / MathF.Tau + 0.5f;
				var got = face.UVs[i].x;
				var d = MathF.Abs( got - want );
				angleOk &= MathF.Min( d, MathF.Abs( d - 1f ) ) < 1e-4f;
			}
		}

		Check( "U is the angle round the axis", angleOk );

		// V is height over the circumference: the 6-unit wall spans 6 / (2 pi 2).
		var spanV = wall.Max( f => cyl.Faces[f].UVs.Max( u => u.y ) ) - wall.Min( f => cyl.Faces[f].UVs.Min( u => u.y ) );
		Check( "V runs along the axis with square texels at the radius", MathF.Abs( spanV - height / (MathF.Tau * radius) ) < 1e-3f, $"{spanV}" );

		// A wall quad's texels are square: its U span times the circumference matches its 3D width.
		var f0 = cyl.Faces[wall[0]];
		var du = (f0.UVs.Max( u => u.x ) - f0.UVs.Min( u => u.x )) * MathF.Tau * radius;
		var chord = 2f * radius * MathF.Sin( MathF.PI / segments );
		Check( "and along the wall a unit is a unit", MathF.Abs( du - chord ) / chord < 0.02f, $"{du} vs {chord}" );

		var signs = wall.Select( f => MathF.Sign( SignedArea( cyl.Faces[f].UVs ) ) ).Distinct().ToList();
		Check( "every wall quad winds the same way in UV as on the surface, none mirrored", signs.Count == 1 && signs[0] > 0, string.Join( ",", signs ) );

		// The session form acts on the selection and is one undo step.
		var session = new MeshEditSession( Primitives.Cylinder( radius, height, segments ) );
		var before = CopyUVs( session.Mesh );
		session.SetMode( EditElement.Face );
		session.SelectFace( wall[0] );
		session.ProjectCylinder();
		var others = Enumerable.Range( 0, session.Mesh.FaceCount ).Where( f => f != wall[0] )
			.All( f => session.Mesh.Faces[f].UVs.Zip( before[f], ( a, b ) => (a - b).Length < 1e-7f ).All( x => x ) );
		Check( "Edit mode projects just the selected faces", others && session.LastLabel == "Cylinder projection" );
		Check( "and picks the long axis of what is selected by default", UVProjection.LongestAxis( Primitives.Cylinder( 1, 10, 8 ) ).z == 1f );

		var refused = false;
		try { UVProjection.CylinderProject( Primitives.Box( 1, 1, 1 ), Vec3.Zero ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "an axis with no length is refused with a reason", refused );

		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature() );
		var project = studio.Add( new UVProjectFeature() );
		project.Mode.Index = Array.IndexOf( project.Mode.Options, "Cylinder" );
		studio.Rebuild();
		Check( "the UV Project feature has a Cylinder mode that runs", project.Mode.Value == "Cylinder" && project.Error is null, project.Error );
		project.Direction.Value = Vec3.Zero;
		studio.MarkDirty( project );
		studio.Rebuild();
		Check( "and refuses a zero axis with a diagnostic", project.Error is not null );
	}

	static void TestSphereProjection()
	{
		var ball = Primitives.QuadSphere( 3f, 8 );
		UVProjection.SphereProject( ball, new Vec3( 0, 0, 1 ) );

		// V is latitude: a vertex at height z on a radius-3 ball sits at V = asin(z/3)/pi + 0.5,
		// for every face that is not touching a pole.
		var latOk = true;
		var uSpanOk = true;
		foreach ( var face in ball.Faces )
		{
			for ( var i = 0; i < face.Count; i++ )
			{
				var p = ball.Positions[face.Indices[i]];
				var want = MathF.Asin( Math.Clamp( p.z / p.Length, -1f, 1f ) ) / MathF.PI + 0.5f;
				latOk &= MathF.Abs( face.UVs[i].y - want ) < 1e-4f;
			}

			uSpanOk &= face.UVs.Max( u => u.x ) - face.UVs.Min( u => u.x ) < 0.5f;
		}

		Check( "sphere V is latitude, 0 at the bottom pole and 1 at the top", latOk );
		Check( "no face is stretched backwards across the wrap", uSpanOk );
		var vs = ball.Faces.SelectMany( f => f.UVs ).Select( u => u.y ).ToList();
		Check( "and V covers the globe", vs.Min() >= -1e-5f && vs.Max() <= 1f + 1e-5f && vs.Max() - vs.Min() > 0.9f, $"{vs.Min()}..{vs.Max()}" );

		var front = ball.Faces.Where( f => SignedArea( f.UVs ) > 1e-7f ).Count();
		var back = ball.Faces.Where( f => SignedArea( f.UVs ) < -1e-7f ).Count();
		Check( "every face reads the right way round from outside", back == 0 && front > 0, $"{front} / {back}" );

		var session = new MeshEditSession( Primitives.QuadSphere( 3f, 8 ) );
		session.ProjectSphere();
		Check( "Edit mode's Sphere projection is one undo step", session.LastLabel == "Sphere projection" && session.UndoCount == 1 );
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
