using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Rendering a mesh in order to ASSERT on it, rather than to look at it.
///
/// WHY THIS EXISTS. Bevel spent a long time flinging corners fifteen thousand units out on a model
/// twenty across, and the whole suite stayed green: the result was finite, closed, manifold and
/// Euler-correct, because a vertex in the wrong place breaks none of those. What found it was a
/// picture — the model had collapsed to a speck, because the view had to stretch to fit one stray
/// point a thousand diameters away.
///
/// A picture is not a test, though. Nobody looks at every contact sheet on every run, and the ones
/// that get looked at get looked at once. So this rasterises the same way PngPreview does and then
/// reduces the image to NUMBERS a test can fail on:
///
///   COVERAGE   — framed on the mesh's OWN bounds, how much of the frame does it fill? This is the
///                exact symptom above. A solid fills a healthy fraction of a view fitted to it; one
///                stray vertex blows the bounds out and everything real collapses toward a pixel.
///
///   COMPONENTS — one connected body must render as ONE connected blob from any angle. Detached
///                fragments and stray geometry show up as extra islands.
///
///   PARITY     — on a closed opaque solid, every pixel covered by a front face must also be covered
///                by a BACK face, because you cannot see into a closed shape. This is the one that
///                earns its keep: a face with a flipped normal leaves the mesh closed, manifold and
///                Euler-correct — every numeric oracle in this suite says it is fine — and renders
///                as a hole straight through the surface.
///
/// None of these are golden images. There are no reference files to regenerate and nothing breaks
/// when a shape legitimately changes; they are invariants that any correct solid satisfies and a
/// broken one does not.
/// </summary>
public static class RenderCheck
{
	/// <summary>One rasterised view, reduced to the three things worth asserting on.</summary>
	public sealed class View
	{
		public int Size;

		/// <summary>Pixels covered by a face pointing toward the camera.</summary>
		public bool[] Front;

		/// <summary>Pixels covered by a face pointing away. On a closed solid this is the far wall.</summary>
		public bool[] Back;

		/// <summary>Fraction of the frame the model fills, with the frame fitted to the model.</summary>
		public float Coverage;

		/// <summary>Connected islands in the silhouette.</summary>
		public int Components;

		/// <summary>Fraction of front-covered pixels that are backed. 1 means nothing shows through.</summary>
		public float Parity;

		/// <summary>How far the view had to stretch: the projected bounds' longer side, in model
		/// units. Reported so a failure can say "it framed 15000 units to show a part 20 across"
		/// rather than only "coverage was 0.000004".</summary>
		public float FramedExtent;

		/// <summary>Pixel count of each island, largest first. A one-pixel second island is a sliver
		/// on the rasteriser's edge; a large one is geometry that came adrift.</summary>
		public List<int> ComponentSizes = new();
	}

	/// <summary>
	/// Orthographic silhouette from one direction, fitted to the mesh's own projected bounds.
	///
	/// FITTED IS THE WHOLE POINT. A fixed camera would show a stray vertex as a dot somewhere off to
	/// the side and the model at its normal size, which is a picture nothing is wrong with. Letting
	/// the frame stretch to contain everything is what turns a misplaced point into a measurable
	/// collapse of the part you meant to look at.
	/// </summary>
	public static View Render( PolyMesh mesh, Vec3 direction, int size = 192 )
	{
		var view = new View
		{
			Size = size,
			Front = new bool[size * size],
			Back = new bool[size * size],
		};

		if ( mesh is null || mesh.Positions.Count == 0 || mesh.Faces.Count == 0 )
			return view;

		var forward = direction.Normal;

		// Any perpendicular will do for "up"; the checks are all rotation-invariant. Picking the
		// axis the view is LEAST aligned with keeps the cross product away from zero.
		var reference = MathF.Abs( forward.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var right = Vec3.Cross( forward, reference ).Normal;
		var up = Vec3.Cross( right, forward ).Normal;

		var flat = new Vec2[mesh.Positions.Count];

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			flat[i] = new Vec2( Vec3.Dot( p, right ), Vec3.Dot( p, up ) );
		}

		var minX = flat.Min( p => p.x );
		var maxX = flat.Max( p => p.x );
		var minY = flat.Min( p => p.y );
		var maxY = flat.Max( p => p.y );

		var extent = MathF.Max( maxX - minX, maxY - minY );

		view.FramedExtent = extent;

		if ( !float.IsFinite( extent ) || extent <= 0f )
			return view;

		// Uniform scale, so the shape is not stretched to fill a square it does not fit.
		var margin = size * 0.04f;
		var scale = (size - margin * 2f) / extent;
		var offsetX = (size - (maxX - minX) * scale) * 0.5f;
		var offsetY = (size - (maxY - minY) * scale) * 0.5f;

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			var normal = mesh.FaceNormal( face );

			// The camera looks ALONG forward, so a face pointing back at it has a normal opposing
			// forward. Exactly edge-on faces (dot 0) contribute to neither and are dropped, which is
			// correct: they are a line, not a surface.
			var facing = Vec3.Dot( normal, forward );

			if ( MathF.Abs( facing ) < 1e-6f )
				continue;

			var target = facing < 0f ? view.Front : view.Back;

			var corners = new List<Vec3>( face.Count );

			foreach ( var index in face.Indices )
				corners.Add( mesh.Positions[index] );

			foreach ( var (a, b, c) in Triangulate.Face( corners ) )
			{
				Fill( target, size,
					Place( flat[face.Indices[a]] ),
					Place( flat[face.Indices[b]] ),
					Place( flat[face.Indices[c]] ) );
			}
		}

		Vec2 Place( Vec2 p ) => new(
			offsetX + (p.x - minX) * scale,
			offsetY + (p.y - minY) * scale );

		Measure( view );

		return view;
	}

	/// <summary>The three metrics, from the two masks.</summary>
	static void Measure( View view )
	{
		var front = 0;
		var backed = 0;

		for ( var i = 0; i < view.Front.Length; i++ )
		{
			if ( !view.Front[i] )
				continue;

			front++;

			if ( view.Back[i] )
				backed++;
		}

		view.Coverage = (float)front / view.Front.Length;
		view.Parity = front == 0 ? 0f : (float)backed / front;
		view.ComponentSizes = ComponentSizes( view.Front, view.Size );

		// ISLANDS BELOW A THOUSANDTH OF THE SILHOUETTE DO NOT COUNT. A bevelled box renders a
		// one-pixel second island where two triangles meet at a shallow angle — a sliver of the
		// rasteriser, not of the model. That is the floor this harness can resolve, so anything
		// under it is noise and saying otherwise would make the check cry wolf on correct geometry.
		var floor = MathF.Max( 2f, front * 0.001f );

		view.Components = view.ComponentSizes.Count( c => c >= floor );
	}

	/// <summary>
	/// Islands in the silhouette, four-connected.
	///
	/// FOUR-connected rather than eight on purpose. Eight-connectivity would join two fragments that
	/// merely touch at a pixel corner, which is exactly the near-miss worth hearing about.
	/// </summary>
	static List<int> ComponentSizes( bool[] mask, int size )
	{
		var seen = new bool[mask.Length];
		var sizes = new List<int>();
		var stack = new Stack<int>();

		for ( var start = 0; start < mask.Length; start++ )
		{
			if ( !mask[start] || seen[start] )
				continue;

			var count = 0;
			stack.Push( start );
			seen[start] = true;

			while ( stack.Count > 0 )
			{
				var at = stack.Pop();
				count++;
				var x = at % size;
				var y = at / size;

				Visit( x - 1, y );
				Visit( x + 1, y );
				Visit( x, y - 1 );
				Visit( x, y + 1 );
			}

			void Visit( int x, int y )
			{
				if ( x < 0 || y < 0 || x >= size || y >= size )
					return;

				var index = y * size + x;

				if ( !mask[index] || seen[index] )
					return;

				seen[index] = true;
				stack.Push( index );
			}

			sizes.Add( count );
		}

		sizes.Sort( ( a, b ) => b.CompareTo( a ) );

		return sizes;
	}

	/// <summary>
	/// Solid triangle by edge functions.
	///
	/// Top-left rule deliberately NOT applied: two triangles sharing an edge both claiming the
	/// boundary pixel is harmless here — the masks are booleans, so double coverage is the same as
	/// coverage — and leaving it out means a shared edge can never fall between them and open a
	/// one-pixel crack that parity would then report as a hole.
	/// </summary>
	static void Fill( bool[] mask, int size, Vec2 a, Vec2 b, Vec2 c )
	{
		var minX = Math.Max( 0, (int)MathF.Floor( MathF.Min( a.x, MathF.Min( b.x, c.x ) ) ) );
		var maxX = Math.Min( size - 1, (int)MathF.Ceiling( MathF.Max( a.x, MathF.Max( b.x, c.x ) ) ) );
		var minY = Math.Max( 0, (int)MathF.Floor( MathF.Min( a.y, MathF.Min( b.y, c.y ) ) ) );
		var maxY = Math.Min( size - 1, (int)MathF.Ceiling( MathF.Max( a.y, MathF.Max( b.y, c.y ) ) ) );

		var area = Edge( a, b, c );

		if ( MathF.Abs( area ) < 1e-9f )
			return;

		var sign = area < 0f ? -1f : 1f;

		for ( var y = minY; y <= maxY; y++ )
		{
			for ( var x = minX; x <= maxX; x++ )
			{
				var p = new Vec2( x + 0.5f, y + 0.5f );

				if ( Edge( a, b, p ) * sign < 0f )
					continue;

				if ( Edge( b, c, p ) * sign < 0f )
					continue;

				if ( Edge( c, a, p ) * sign < 0f )
					continue;

				mask[y * size + x] = true;
			}
		}
	}

	static float Edge( Vec2 a, Vec2 b, Vec2 p ) =>
		(b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

	// --- the directions everything is checked from ---------------------------------------------

	/// <summary>
	/// Six views, none of them down an axis.
	///
	/// AXIS-ALIGNED VIEWS ARE THE WEAKEST ONES AVAILABLE. Almost everything in this kernel is built
	/// from axis-aligned faces, so looking down an axis puts whole faces exactly edge-on where they
	/// contribute nothing, and lands stray geometry exactly behind the part that hides it. An
	/// oblique view has no such coincidences.
	/// </summary>
	public static readonly Vec3[] Directions =
	{
		new( 0.577f, 0.577f, 0.577f ),
		new( -0.577f, 0.577f, 0.577f ),
		new( 0.577f, -0.577f, 0.577f ),
		new( 0.577f, 0.577f, -0.577f ),
		new( -0.577f, -0.577f, 0.577f ),
		new( 0.301f, 0.822f, -0.483f ),
	};
}
