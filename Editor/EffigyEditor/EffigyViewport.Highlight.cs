using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Text;

namespace Marionette.EditorTools;

/// <summary>
/// The lit-up part: a whole body drawn in the selection colour.
///
/// A CACHED MODEL RATHER THAN GIZMO LINES, and that is the entire reason this file exists.
/// DrawBodyHighlight — still here, still right for what it does — walks every face of the body
/// EVERY FRAME, allocating two lists per face, triangulating it, and issuing a SolidTriangle plus
/// one Line per edge. On a CAD body of two hundred quads that is nothing. On the thing that made
/// Remesh necessary — a Meshy part-segmentation whose big parts are three hundred thousand
/// triangles each — it is roughly a million immediate-mode draw calls per frame, and what you see
/// when you click that part in the Parts list is not a highlight, it is the viewport dying. The
/// selection was working the whole time; nothing on screen could say so.
///
/// So the highlight for a WHOLE BODY is built once, when the selection changes, and handed to the
/// renderer as a model: one draw call, no per-frame CPU, and the cost is the same whether the part
/// is a box or a scan.
///
/// IT IS A SHELL, pushed a hair out along its own normals. The alternative — drawing it at exactly
/// the part's own coordinates — z-fights with the preview across the whole surface, which reads as
/// the highlight flickering rather than as the part being selected. The push is a fraction of the
/// body's own size, so it holds at any scale: Effigy's units are dimensionless and a body is as
/// likely to be one unit across as three hundred.
///
/// FACE and edge highlights stay on the gizmo path. A face is one polygon and an edge is one line;
/// they are cheap by nature, and they change with the cursor rather than with a selection, which is
/// the opposite of what a cache is for.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>How far the shell is pushed out along the surface normal, as a fraction of the
	/// body's bounding-sphere radius. Large enough to clear the depth buffer at any distance the
	/// camera can get to, small enough that the shell never separates visibly from the part.</summary>
	private const float HighlightShellFraction = 0.01f;

	private GameObject _highlightObject;
	private ModelRenderer _highlightRenderer;
	private Material _highlightMaterial;
	private Color _highlightMaterialColor;

	/// <summary>What the cached shell was built from. Rebuild when it moves — not per frame, and
	/// not per repaint.</summary>
	private string _highlightKey;

	/// <summary>
	/// Show these bodies as selected, or none of them.
	///
	/// Called every frame from the idle-selection draw, and does nothing at all on the frames where
	/// the answer has not changed — which is almost all of them. The key carries the body ids AND
	/// their sizes, so a rebuild that replaces a part's mesh under the same id (which is exactly
	/// what Remesh does) is noticed.
	/// </summary>
	private void SyncBodyHighlight( IReadOnlyList<Body> bodies, Color color )
	{
		var key = HighlightKey( bodies, color );

		if ( key == _highlightKey )
			return;

		_highlightKey = key;

		if ( bodies is null || bodies.Count == 0 )
		{
			ClearBodyHighlight();
			return;
		}

		var material = HighlightMaterial( color );
		var shell = BuildHighlightShell( bodies );
		var model = shell is null || material is null ? null : EffigyPreview.Build( shell, material );

		if ( model is null )
		{
			// SAID OUT LOUD. A null here used to mean the shell silently did not appear, which is
			// indistinguishable from the selection never having happened — and that is precisely the
			// confusion this feature has already cost more than it is worth.
			Log.Warning( "[Effigy] the selection highlight could not be built - the part is selected, "
				+ "but nothing will light up." );

			ClearBodyHighlight();
			return;
		}

		using var scope = _canvas.Scene.Push();

		if ( !_highlightObject.IsValid() )
		{
			_highlightObject = new GameObject( true, "effigy_highlight" );
			_highlightRenderer = _highlightObject.GetOrAddComponent<ModelRenderer>( false );
		}

		_highlightRenderer.Model = model;
		_highlightRenderer.Enabled = true;
	}

	/// <summary>Drop the shell and the object holding it. Called when the selection empties, and
	/// when the viewport is torn down.</summary>
	private void ClearBodyHighlight()
	{
		_highlightObject?.Destroy();
		_highlightObject = null;
		_highlightRenderer = null;
	}

	private static string HighlightKey( IReadOnlyList<Body> bodies, Color color )
	{
		if ( bodies is null || bodies.Count == 0 )
			return "";

		var builder = new StringBuilder();

		builder.Append( color.r ).Append( ',' ).Append( color.g ).Append( ',' ).Append( color.b );

		foreach ( var body in bodies )
		{
			if ( body?.Mesh is not { } mesh )
				continue;

			// Id, then the two counts. The id alone is not enough: Remesh replaces a body's mesh
			// and keeps its id, and a shell left over from before the reduce is a highlight of a
			// part that no longer exists at those coordinates.
			builder.Append( '|' ).Append( body.Id )
				.Append( ':' ).Append( mesh.VertexCount )
				.Append( ',' ).Append( mesh.FaceCount );
		}

		return builder.ToString();
	}

	/// <summary>
	/// One mesh over every selected body, with each vertex pushed out along its own normal.
	///
	/// The push is per BODY rather than over the merged bounds, so selecting a hand and a torso
	/// together does not give the hand a shell thick enough to swallow it.
	/// </summary>
	private static PolyMesh BuildHighlightShell( IReadOnlyList<Body> bodies )
	{
		PolyMesh merged = null;

		foreach ( var body in bodies )
		{
			if ( body?.Mesh is not { } mesh || mesh.FaceCount == 0 || mesh.VertexCount == 0 )
				continue;

			var inflated = Inflated( mesh );

			// One body is the ordinary case by a mile — a Parts-list click selects one row — and
			// Append on a three-hundred-thousand-triangle mesh is a second copy of it for nothing.
			if ( merged is null )
			{
				merged = inflated;
				continue;
			}

			MeshTransform.Append( merged, inflated );
		}

		return merged is null || merged.FaceCount == 0 ? null : merged;
	}

	/// <summary>
	/// The mesh with every position moved out along the average of the face normals meeting there.
	/// A vertex whose faces cancel out — a degenerate sliver, a seam in a mesh that was never welded
	/// — is left where it is rather than shot off in whatever direction the arithmetic landed on.
	///
	/// NOT PolyMesh.Clone: the faces are shared with the body this came from, because nothing here
	/// or downstream writes to a Face — the shell only moves POSITIONS, and the merge below adds new
	/// Face objects rather than editing the ones it was given. Cloning them instead would copy an
	/// indices array and a UV array per face, three hundred thousand times, on the click that is
	/// supposed to make selection feel instant.
	/// </summary>
	private static PolyMesh Inflated( PolyMesh mesh )
	{
		var normals = new Vec3[mesh.VertexCount];
		var min = mesh.Positions[0];
		var max = min;

		for ( var i = 1; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];

			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		var distance = (max - min).Length * 0.5f * HighlightShellFraction;

		// WHICH WAY IS OUT. Newell gives the normal of a face wound counter-clockwise; a mesh wound
		// the other way — which an imported OBJ is free to be — hands back inward normals, and an
		// inward shell is a shell hidden INSIDE the part. That failure looks exactly like the
		// highlight not working at all, which is the trap this whole file was written to get out of,
		// so the winding is measured rather than assumed: the signed volume of a closed mesh is
		// positive when its faces face out.
		if ( SignedVolume( mesh ) < 0f )
			distance = -distance;

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			// Newell, so a face that is not planar still gets a normal that means something, and
			// so the accumulation is area-weighted for free — a big face should have more say in
			// where a shared corner goes than a sliver does.
			var normal = Vec3.Zero;

			for ( var i = 0; i < face.Count; i++ )
			{
				var a = mesh.Positions[face.Indices[i]];
				var b = mesh.Positions[face.Indices[(i + 1) % face.Count]];

				normal = new Vec3(
					normal.x + (a.y - b.y) * (a.z + b.z),
					normal.y + (a.z - b.z) * (a.x + b.x),
					normal.z + (a.x - b.x) * (a.y + b.y) );
			}

			for ( var i = 0; i < face.Count; i++ )
			{
				var v = face.Indices[i];
				normals[v] = normals[v] + normal;
			}
		}

		var positions = new List<Vec3>( mesh.VertexCount );

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var n = normals[i];
			var length = n.Length;

			positions.Add( length < 1e-9f
				? mesh.Positions[i]
				: mesh.Positions[i] + n * (distance / length) );
		}

		// A fresh Faces LIST holding the same Face objects: the merge appends to this list, and it
		// must not be the body's own.
		return new PolyMesh { Positions = positions, Faces = new List<Face>( mesh.Faces ) };
	}

	/// <summary>Six times the enclosed volume, by the divergence theorem — only the SIGN is wanted,
	/// so the sixth is not worth dividing out. Fanned from each face's first corner, which is exact
	/// for the volume of a closed surface whether or not the face is convex.</summary>
	private static float SignedVolume( PolyMesh mesh )
	{
		var total = 0f;

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			var a = mesh.Positions[face.Indices[0]];

			for ( var i = 1; i < face.Count - 1; i++ )
			{
				var b = mesh.Positions[face.Indices[i]];
				var c = mesh.Positions[face.Indices[i + 1]];

				total += a.x * (b.y * c.z - b.z * c.y)
					- a.y * (b.x * c.z - b.z * c.x)
					+ a.z * (b.x * c.y - b.y * c.x);
			}
		}

		return total;
	}

	/// <summary>
	/// The flat colour the shell wears — default.vmat with a one-pixel texture bound, the same
	/// trick the weight and paint previews use to put a generated image on a part without shipping
	/// a shader of their own.
	/// </summary>
	private Material HighlightMaterial( Color color )
	{
		if ( _highlightMaterial is not null && _highlightMaterialColor == color )
			return _highlightMaterial;

		var pixel = new byte[]
		{
			(byte)(color.r * 255f), (byte)(color.g * 255f), (byte)(color.b * 255f), 255,
		};

		var texture = Texture.Create( 1, 1, ImageFormat.RGBA8888 ).WithData( pixel ).Finish();

		_highlightMaterial = Material.Load( "materials/default.vmat" )?.CreateCopy( "effigy_highlight" );
		_highlightMaterial?.Set( "g_tColor", texture );
		_highlightMaterialColor = color;

		return _highlightMaterial;
	}
}
