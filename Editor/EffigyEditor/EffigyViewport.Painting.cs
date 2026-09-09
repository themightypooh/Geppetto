using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// Paint mode in the viewport: rays in, a live texture out.
///
/// AS THIN AS EffigyViewport.Sculpting.cs, AND FOR THE SAME REASON. Everything with a decision in it —
/// where the cursor sits, whether the pointer has travelled far enough to earn a sample, the dab
/// itself, the falloff — lives in <see cref="PaintSession"/> and <see cref="PaintReplay"/> in the
/// kernel, where a test can see it. This file converts Vector3 to Vec3, calls four methods, uploads
/// the canvas's dirty rect to a texture, and draws a ring.
///
/// CTRL ERASES, and it is the same stroke machinery — the session is told which kind the stroke is
/// at the press and the kernel does the rest. Nothing here knows how an erase composites; this file
/// reads one modifier and picks a ring colour.
///
/// THE PAINT IS A TEXTURE, NOT VERTEX COLOURS. The session holds a live canvas whose dirty rect is
/// exactly what a dab touched, so a mouse-move re-uploads only that region rather than the whole
/// 1024² image — the four-megabytes-per-dab cost the old paint project paid. The canvas is baked
/// opaque over white before it reaches the GPU, because an ordinary material's colour map has nothing
/// sensible to show in a transparent texel.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>The live paint, or null when not in paint mode.</summary>
	public PaintSession PaintSession { get; private set; }

	public bool IsPainting => PaintSession is not null;

	/// <summary>Raised after a stroke commits, carrying the committed stroke so the window can append
	/// it to the feature. Not raised per sample — a stroke is one edit.</summary>
	public Action<PaintStroke> PaintStrokeFinished { get; set; }

	/// <summary>Where the brush ring is drawn this frame, or null when the cursor is off the model.</summary>
	private MeshHit? _paintCursor;

	/// <summary>The floating bars that belong to a brush — the colour one and the material one.
	/// A list rather than a field because there are two now and both have to be kept off the
	/// canvas hit test: a click meant for a control must never also land a dab.</summary>
	private readonly List<Widget> _paintBarOverlays = new();

	private bool _paintPreviewStale;

	/// <summary>The bar's Erase mode at the moment the current stroke began, so Ctrl can invert
	/// it for one mark without leaving the mode flipped afterwards.</summary>
	private bool _paintEraseMode;

	// The dynamic texture and its material, held for the life of a paint session and re-uploaded in
	// place. Held rather than recreated per dab — a fresh texture + material copy per mouse-move is
	// exactly the garbage a held drag would make thousands of.
	private Texture _paintTexture;
	private Material _paintMaterial;

	public void AddPaintOverlay( Widget bar )
	{
		_paintBarOverlays.Add( bar );
		bar.Position = OverlayMargin + new Vector2( 0f, 46f );
		bar.Visible = false;
	}

	public void BeginPaint( PaintSession session )
	{
		PaintSession = session ?? throw new ArgumentNullException( nameof( session ) );

		CreatePaintTexture( session );
		_paintPreviewStale = true;
	}

	public void EndPaint()
	{
		// A stroke left running when the mode ends would be a half-finished mark nobody committed.
		// Cancel rather than commit, the same choice sculpt makes.
		if ( PaintSession is { IsStroking: true } )
			PaintSession.CancelStroke();

		PaintSession = null;
		_paintCursor = null;

		_paintMaterial = null;
		_paintTexture = null;
	}

	/// <summary>Create the texture and material the whole session will draw with. The canvas is baked
	/// opaque over white in full, so the material's colour map is opaque from the first frame — which
	/// is also why the canvas's dirty rect is cleared afterwards, since that full bake IS the upload.</summary>
	private void CreatePaintTexture( PaintSession session )
	{
		var res = session.Resolution;

		var opaque = new byte[res * res * 4];
		session.Canvas.BakeOpaque( opaque, PaintMaterial.DefaultBaseR, PaintMaterial.DefaultBaseG, PaintMaterial.DefaultBaseB );

		_paintTexture = Texture.Create( res, res, ImageFormat.RGBA8888 )
			.WithDynamicUsage()
			.WithData( opaque )
			.Finish();

		// The paint is the surface colour, so the base is any ordinary lit material — its own colour
		// map is overridden with the atlas below. default.vmat is the placeholder the rest of the
		// preview already falls back to.
		_paintMaterial = Material.Load( "materials/default.vmat" )?.CreateCopy( "effigy_paint" );
		_paintMaterial?.Set( "g_tColor", _paintTexture );

		session.Canvas.ClearDirty();
	}

	/// <summary>Upload whatever the canvas's dirty rect names, then push the painted surface into the
	/// viewport. The dirty rect is what makes a mouse-move cheap: only the texels a dab touched are
	/// baked and uploaded, not the whole image.</summary>
	public void RefreshPaintPreview()
	{
		if ( PaintSession is null || _paintMaterial is null || _paintTexture is null )
			return;

		var canvas = PaintSession.Canvas;

		if ( canvas.HasDirty )
		{
			var w = canvas.MaxX - canvas.MinX + 1;
			var h = canvas.MaxY - canvas.MinY + 1;
			var sub = new byte[w * h * 4];

			canvas.BakeOpaque( sub, PaintMaterial.DefaultBaseR, PaintMaterial.DefaultBaseG, PaintMaterial.DefaultBaseB,
				canvas.MinX, canvas.MinY, w, h );
			_paintTexture.Update( sub, canvas.MinX, canvas.MinY, w, h );

			canvas.ClearDirty();
		}

		var model = EffigyPreview.Build( PaintSession.Mesh, _paintMaterial );

		if ( model is null )
			return;

		// Swapped rather than SetModel, for the same reason sculpt swaps: SetModel destroys and
		// rebuilds the GameObject, which is fine once and not fine on every rebuild.
		if ( _renderer is not null )
			_renderer.Model = model;
		else
			SetModel( model, frameCamera: false );
	}

	private void PaintFrame()
	{
		if ( PaintSession is null )
			return;

		_paintCursor = null;

		var stroking = PaintSession.IsStroking;

		// The pointer leaving the canvas does NOT end a stroke, the same rule sculpt keeps: dragging
		// off the model and back on is ordinary, and ending there would make the tool drop the gesture.
		if ( _canvasHasCursor )
		{
			var ray = Gizmo.CurrentRay;
			var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
			var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

			_paintCursor = PaintSession.Hover( origin, direction );

			if ( !stroking && Gizmo.WasLeftMousePressed )
			{
				// The bar's Erase toggle is the mode. Ctrl inverts it for this stroke only, the
				// same "read once at the press" rule sculpt uses, so releasing mid-drag cannot
				// split one mark into two kinds.
				_paintEraseMode = PaintSession.Erasing;
				var hold = Editor.Application.IsKeyDown( KeyCode.Control );
				PaintSession.Erasing = hold ? !_paintEraseMode : _paintEraseMode;

				if ( PaintSession.BeginStroke( origin, direction ) )
				{
					stroking = true;
					_paintPreviewStale = true;
				}
			}
			else if ( stroking && Gizmo.IsLeftMouseDown )
			{
				if ( PaintSession.MoveTo( origin, direction ) > 0 )
					_paintPreviewStale = true;
			}
		}

		// Released. Same inference as sculpt: the end of a stroke is the frame the button is no
		// longer down.
		if ( stroking && !Gizmo.IsLeftMouseDown )
		{
			var stroke = PaintSession.EndStroke();

			if ( stroke is not null )
				PaintStrokeFinished?.Invoke( stroke );

			PaintSession.Erasing = _paintEraseMode;
		}

		if ( _paintPreviewStale )
		{
			RefreshPaintPreview();
			_paintPreviewStale = false;
		}

		DrawPaintCursor();
	}

	/// <summary>The brush ring on the surface, lying in the surface's own plane — the same footprint
	/// the dab will paint, and the same reason sculpt draws it flat rather than camera-facing.</summary>
	private void DrawPaintCursor()
	{
		if ( _paintCursor is not { } hit )
			return;

		var normal = new Vector3( hit.Normal.x, hit.Normal.y, hit.Normal.z ).Normal;
		var centre = new Vector3( hit.Point.x, hit.Point.y, hit.Point.z );

		var reference = MathF.Abs( normal.z ) > 0.9f ? new Vector3( 1f, 0f, 0f ) : new Vector3( 0f, 0f, 1f );
		var right = Vector3.Cross( normal, reference ).Normal;
		var up = Vector3.Cross( normal, right ).Normal;

		var radius = PaintSession.Radius;

		// While a stroke runs the ring shows what THAT stroke is; between strokes it shows what the
		// next one would be, read live off the modifier. Reading only the session flag would make the
		// ring tell the truth one stroke late — the "visible after the click rather than before it"
		// complaint, which is the whole reason the ring is coloured at all.
		var erasing = PaintSession.IsStroking
			? PaintSession.Erasing
			: Editor.Application.IsKeyDown( KeyCode.Control );

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1.5f;
		Gizmo.Draw.Color = erasing ? PaintEraseCursorColor : PaintCursorColor;

		var lift = normal * (radius * 0.01f);
		const int Segments = 40;
		var previous = centre + right * radius + lift;

		for ( var i = 1; i <= Segments; i++ )
		{
			var angle = i / (float)Segments * MathF.PI * 2f;
			var point = centre + (right * MathF.Cos( angle ) + up * MathF.Sin( angle )) * radius + lift;

			Gizmo.Draw.Line( previous, point );
			previous = point;
		}

		Gizmo.Draw.Line( centre, centre + normal * (radius * 0.35f) );
	}

	/// <summary>A paint ring is a paint ring — a colour distinct from sculpt's blue, so the two
	/// modes are never confused when a brush is armed.</summary>
	private static readonly Color PaintCursorColor = new( 1f, 0.45f, 0.75f, 0.9f );

	/// <summary>Erasing, shown while Ctrl is held. The same red the note eraser and the inverted
	/// sculpt brush use, because it means the same thing in all three: this stroke takes away.</summary>
	private static readonly Color PaintEraseCursorColor = new( 1f, 0.35f, 0.32f, 0.9f );
}
