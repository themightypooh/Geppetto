using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Clean-ups by name, so a script or an agent can leave a mesh tidy without reading the check:
/// `loose`, `doubles`, `holes`, `normals`, or `all`. Each is an Edit-mode operation run headlessly
/// on the whole mesh and written back in place.
/// </summary>
public static class MeshFix
{
	public static readonly string[] Names = { "loose", "doubles", "holes", "normals" };

	/// <summary>Apply the named fixes to a mesh, in the order that makes sense: loose vertices
	/// first, merge doubles before filling holes (a hairline gap is a double, not a hole), fix normals last. Returns
	/// what was done, one line each.</summary>
	public static List<string> Apply( PolyMesh mesh, IEnumerable<string> fixes, float doublesTolerance = 0.001f )
	{
		var done = new List<string>();

		if ( mesh is null || mesh.Positions.Count == 0 )
			return done;

		var wanted = new HashSet<string>( (fixes ?? Array.Empty<string>()).Select( f => f.Trim().ToLowerInvariant() ) );

		if ( wanted.Contains( "all" ) )
			wanted.UnionWith( Names );

		var session = new MeshEditSession( mesh );

		// Loose first: a merge compacts the mesh too, so running it first would hide the loose
		// vertices inside the doubles count and leave DeleteLoose nothing to do (it throws on that).
		if ( wanted.Contains( "loose" ) )
		{
			var used = new HashSet<int>();
			foreach ( var f in session.Mesh.Faces )
				used.UnionWith( f.Indices );

			var before = session.Mesh.VertexCount;
			if ( used.Count < before )
				session.DeleteLoose();

			done.Add( $"loose: removed {before - session.Mesh.VertexCount} unused vertices" );
		}

		if ( wanted.Contains( "doubles" ) )
		{
			var before = session.Mesh.VertexCount;
			session.MergeByDistance( doublesTolerance );
			done.Add( $"doubles: merged {before - session.Mesh.VertexCount} vertices within {doublesTolerance}" );
		}

		if ( wanted.Contains( "holes" ) )
		{
			var closed = session.FillHoles( 6 );
			done.Add( $"holes: closed {closed} (up to six sides; bigger openings were left)" );
		}

		if ( wanted.Contains( "normals" ) )
		{
			session.ClearSelection();
			session.RecalculateNormals();
			done.Add( "normals: made every piece consistent and outward" );
		}

		var unknown = wanted.Where( w => w != "all" && !Names.Contains( w ) ).ToList();

		if ( unknown.Count > 0 )
			done.Add( $"ignored: {string.Join( ", ", unknown )} (fixes are {string.Join( ", ", Names )}, or all)" );

		// Write back in place: the body keeps its id and everything holding it.
		var result = session.Mesh;
		mesh.Positions.Clear();
		mesh.Positions.AddRange( result.Positions );
		mesh.Faces.Clear();
		mesh.Faces.AddRange( result.Faces );
		CopyOptional( result, mesh );

		return done;
	}

	static void CopyOptional( PolyMesh from, PolyMesh to )
	{
		to.Skin = from.Skin;
		to.VertexColors = from.HasVertexColors ? from.VertexColors : null;
	}
}
