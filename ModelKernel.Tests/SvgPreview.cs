using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ModelKernel;

namespace ModelKernel.Tests;

/// <summary>
/// Renders a mesh to a shaded SVG so kernel output can be looked at rather than only asserted
/// about.
///
/// A test suite proves the numbers are right; it does not show that a slot looks like a slot.
/// Painter's algorithm with flat shading is enough to catch the whole class of "the maths passed
/// and the shape is still wrong", and SVG means no image library and no renderer in the loop.
/// </summary>
public static class SvgPreview
{
	public static void Write( PolyMesh mesh, string path, string title, int size = 460, bool wireframe = false )
	{
		File.WriteAllText( path, Render( mesh, title, size, wireframe ) );
	}

	public static string Render( PolyMesh mesh, string title, int size = 460, bool wireframe = false )
	{
		var c = CultureInfo.InvariantCulture;

		// A three-quarter view: rotate around Z then tilt down, so nothing lands edge-on and the
		// silhouette actually reads.
		var yaw = 35f * MathF.PI / 180f;
		var pitch = 24f * MathF.PI / 180f;

		var view = Xform.Rotate( new Vec3( 1, 0, 0 ), -pitch ) * Xform.Rotate( new Vec3( 0, 0, 1 ), yaw );

		// Positions go through `view` into VIEW space, and Screen() reads x and z from that — so
		// view-space Y is depth, and the viewer sits at -Y looking toward +Y. `camera` is therefore
		// a constant axis in view space, NOT a transformed world direction: transforming it mixes
		// the two spaces and the backface test becomes almost arbitrary.
		//
		// depth = dot(p, camera) = -p.y, so far geometry has the SMALLER depth and ascending order
		// paints back to front.
		var camera = new Vec3( 0, -1, 0 );
		var light = new Vec3( -0.4f, -0.75f, 0.53f ).Normal;

		var projected = mesh.Positions.Select( p => view.TransformPoint( p ) ).ToList();

		if ( projected.Count == 0 )
			return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}\" height=\"{size}\"></svg>";

		// Fit the model to the viewport with a margin, preserving aspect.
		var minX = projected.Min( p => p.x );
		var maxX = projected.Max( p => p.x );
		var minZ = projected.Min( p => p.z );
		var maxZ = projected.Max( p => p.z );

		var span = MathF.Max( maxX - minX, maxZ - minZ );

		if ( span < 1e-6f )
			span = 1f;

		var scale = (size - 60) / span;
		var cx = (minX + maxX) * 0.5f;
		var cz = (minZ + maxZ) * 0.5f;

		// SVG's Y axis points down, so the world Z term is negated to keep up looking up.
		Vec2 Screen( Vec3 p ) => new(
			size * 0.5f + (p.x - cx) * scale,
			size * 0.5f - (p.z - cz) * scale );

		// Painter's algorithm, back to front by centroid depth. With backface culling on a convex
		// solid the order is irrelevant; it only matters for shapes like the torus that can occlude
		// themselves.
		var order = Enumerable.Range( 0, mesh.FaceCount )
			.Select( fi => (
				Index: fi,
				Depth: mesh.Faces[fi].Indices.Average( i => Vec3.Dot( projected[i], camera ) )) )
			.OrderBy( x => x.Depth )
			.ToList();

		var sb = new StringBuilder();
		sb.Append( c, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" width=\"{size}\" height=\"{size}\">\n" );
		sb.Append( "<rect width=\"100%\" height=\"100%\" fill=\"#14161a\"/>\n" );

		var drawn = 0;

		foreach ( var (fi, _) in order )
		{
			var face = mesh.Faces[fi];
			var normal = mesh.FaceNormal( face );
			var viewNormal = view.TransformDirection( normal );

			// Backface cull: keep faces whose view-space normal points back toward the viewer at
			// -Y, which is dot(normal, camera) > 0. Every mesh the kernel produces is a closed
			// solid with outward normals, so this is safe — and it doubles as a visual check on
			// the winding tests, because an inside-out solid renders as a hole.
			if ( Vec3.Dot( viewNormal, camera ) <= 0.01f )
				continue;

			var lambert = MathF.Max( 0f, Vec3.Dot( normal, light ) );
			var shade = 0.22f + 0.78f * lambert * lambert;

			var r = (int)(shade * 118 + 26);
			var g = (int)(shade * 168 + 30);
			var b = (int)(shade * 208 + 38);

			var points = string.Join( " ", face.Indices.Select( i =>
			{
				var s = Screen( projected[i] );
				return string.Format( c, "{0:0.##},{1:0.##}", s.x, s.y );
			} ) );

			var stroke = wireframe ? "#0b0c0e" : $"rgb({r},{g},{b})";
			var width = wireframe ? "0.7" : "0.35";

			sb.Append( c, $"<polygon points=\"{points}\" fill=\"rgb({r},{g},{b})\" stroke=\"{stroke}\" stroke-width=\"{width}\"/>\n" );
			drawn++;
		}

		sb.Append( c, $"<text x=\"16\" y=\"{size - 30}\" fill=\"#8b98a8\" font-family=\"ui-monospace,monospace\" font-size=\"13\">{Escape( title )}</text>\n" );
		sb.Append( c, $"<text x=\"16\" y=\"{size - 13}\" fill=\"#5d6874\" font-family=\"ui-monospace,monospace\" font-size=\"11\">{mesh.VertexCount} verts · {mesh.FaceCount} faces · {drawn} drawn</text>\n" );
		sb.Append( "</svg>\n" );

		return sb.ToString();
	}

	static string Escape( string s ) =>
		s.Replace( "&", "&amp;" ).Replace( "<", "&lt;" ).Replace( ">", "&gt;" );
}
