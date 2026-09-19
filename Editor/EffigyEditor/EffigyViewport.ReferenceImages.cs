using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// Reference images: pictures stood in the viewport to model against — a front, a side and a
/// top drawing of the character, the way every character starts in Blender.
///
/// ONE QUAD PER PICTURE, DRAWN BOTH WAYS. Each image is a plane model with the picture as its
/// colour map, placed in the plane its view names and pushed back by its depth so it sits behind
/// the model in that ortho view. A second copy is wound the other way so the picture is there
/// from behind too; a one-sided plane vanishes the moment the camera swings round it.
///
/// THE DOCUMENT OWNS THE LIST. This file only draws what <see cref="PartStudio.ReferenceImages"/>
/// says, rebuilt when the window says the list changed. Opacity goes through the renderer's
/// tint; whether the material honours it is the material's business, and an opaque picture
/// behind the model is still a picture behind the model.
/// </summary>
internal sealed partial class EffigyViewport
{
	private readonly List<GameObject> _referenceImageObjects = new();
	private int _referenceImagesRevision = -1;

	/// <summary>Draw this list of pictures, replacing whatever was drawn before.</summary>
	public void SetReferenceImages( IReadOnlyList<ReferenceImage> images )
	{
		ClearReferenceImages();

		if ( images is null )
			return;

		foreach ( var image in images )
		{
			if ( image is null || !image.Visible || string.IsNullOrWhiteSpace( image.Path ) )
				continue;

			var texture = Texture.Load( image.Path );

			if ( texture is null || texture.IsError )
			{
				Log.Warning( $"[Effigy] reference image '{image.Path}' could not be loaded. Put it under the project's Assets folder and name it from there." );
				continue;
			}

			var aspect = texture.Height > 0 ? texture.Width / (float)texture.Height : 1f;
			var height = MathF.Max( image.Height, 0.01f );
			var width = height * aspect;

			// The plane's own axes: right and up on the picture, and which way it faces.
			var (right, up, facing) = image.View switch
			{
				"side" => (new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 0, -1, 0 )),
				"top" => (new Vec3( 0, -1, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 )),
				_ => (new Vec3( 0, -1, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 1, 0, 0 )),
			};

			// Pushed away from the viewer of that view — behind the model — and slid in-plane.
			var centre = facing * -image.Depth + right * image.Offset.x + up * image.Offset.y;
			var mesh = new PolyMesh();
			var hw = width * 0.5f;
			var hh = height * 0.5f;
			mesh.Positions.Add( centre - right * hw - up * hh );
			mesh.Positions.Add( centre + right * hw - up * hh );
			mesh.Positions.Add( centre + right * hw + up * hh );
			mesh.Positions.Add( centre - right * hw + up * hh );

			// Front: wound to face the viewer; back: the same corners the other way round. UVs put
			// the picture's top at the plane's top, with v running down as textures do.
			mesh.AddFace( new[] { 0, 1, 2, 3 }, new[] { new Vec2( 0, 1 ), new Vec2( 1, 1 ), new Vec2( 1, 0 ), new Vec2( 0, 0 ) } );
			mesh.AddFace( new[] { 3, 2, 1, 0 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) } );

			if ( Vec3.Dot( mesh.FaceNormal( mesh.Faces[0] ), facing ) < 0f )
			{
				// The first face should be the one that faces the view; swap if the axes wound it away.
				(mesh.Faces[0], mesh.Faces[1]) = (mesh.Faces[1], mesh.Faces[0]);
			}

			var material = Material.Load( "materials/default.vmat" )?.CreateCopy( $"effigy_reference_{_referenceImageObjects.Count}" );
			material?.Set( "g_tColor", texture );

			var model = EffigyPreview.Build( mesh, material );

			if ( model is null )
				continue;

			var go = new GameObject( true, $"effigy_reference_image_{_referenceImageObjects.Count}" );
			var renderer = go.GetOrAddComponent<ModelRenderer>( false );
			renderer.Model = model;
			renderer.Tint = Color.White.WithAlpha( Math.Clamp( image.Opacity, 0.05f, 1f ) );
			renderer.Enabled = true;
			go.WorldPosition = OriginPosition;

			_referenceImageObjects.Add( go );
		}
	}

	/// <summary>Take every picture down.</summary>
	public void ClearReferenceImages()
	{
		foreach ( var go in _referenceImageObjects )
		{
			if ( go.IsValid() )
				go.Destroy();
		}

		_referenceImageObjects.Clear();
	}

	/// <summary>The pictures follow the origin like the size reference does, so a moved frame
	/// does not leave them measuring against nothing.</summary>
	private void PlaceReferenceImages()
	{
		foreach ( var go in _referenceImageObjects )
		{
			if ( go.IsValid() )
				go.WorldPosition = OriginPosition;
		}
	}
}
