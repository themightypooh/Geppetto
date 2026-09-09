using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// Weight painting in the viewport: rays in, a heat-map texture out.
///
/// NOT VERTEX COLOURS. Paint already proved that a CAD cage cannot carry colour at vertex
/// density, and the material that read a COLOR stream was the workaround the atlas path deleted.
/// Skin weights stay per-vertex — the compiler skins those — but the display is a UV atlas of
/// the same barycentric interpolation the GPU will do at deform time. See WeightRamp.
/// </summary>
internal sealed partial class EffigyViewport
{
	public WeightPaintSession WeightSession { get; private set; }

	public bool IsWeightPainting => WeightSession is not null;

	public Action WeightStrokeFinished { get; set; }

	private MeshHit? _weightCursor;
	private Widget _weightBarOverlay;
	private bool _weightPreviewStale;
	private Texture _weightTexture;
	private Material _weightMaterial;

	public void AddWeightOverlay( Widget bar )
	{
		_weightBarOverlay = bar;
		bar.Position = OverlayMargin + new Vector2( 0f, 46f );
		bar.Visible = false;
	}

	public void BeginWeightPaint( WeightPaintSession session )
	{
		WeightSession = session ?? throw new ArgumentNullException( nameof( session ) );
		UploadWeightRamp();
		_weightPreviewStale = true;
	}

	public void EndWeightPaint()
	{
		if ( WeightSession is { IsStroking: true } )
			WeightSession.EndStroke();

		WeightSession = null;
		_weightCursor = null;
		_weightTexture = null;
		_weightMaterial = null;
	}

	public void RefreshWeightRamp()
	{
		if ( WeightSession is null )
			return;

		UploadWeightRamp();
		_weightPreviewStale = true;
	}

	void UploadWeightRamp()
	{
		var canvas = WeightSession.Ramp( 256 );
		var opaque = PaintMaterial.OpaqueRgba( canvas );

		_weightTexture = Texture.Create( canvas.Width, canvas.Height, ImageFormat.RGBA8888 )
			.WithData( opaque )
			.Finish();

		_weightMaterial = Material.Load( "materials/default.vmat" )?.CreateCopy( "effigy_weights" );
		_weightMaterial?.Set( "g_tColor", _weightTexture );
	}

	void RefreshWeightPreview()
	{
		if ( WeightSession is null || _weightMaterial is null )
			return;

		var model = EffigyPreview.Build( WeightSession.Mesh, _weightMaterial );

		if ( model is null )
			return;

		if ( _renderer is not null )
			_renderer.Model = model;
		else
			SetModel( model, frameCamera: false );
	}

	private void WeightPaintFrame()
	{
		if ( WeightSession is null )
			return;

		_weightCursor = null;
		var stroking = WeightSession.IsStroking;

		if ( _canvasHasCursor )
		{
			var ray = Gizmo.CurrentRay;
			var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
			var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

			_weightCursor = WeightSession.Hover( origin, direction );

			if ( !stroking && Gizmo.WasLeftMousePressed )
			{
				if ( WeightSession.BeginStroke( origin, direction ) )
				{
					stroking = true;
					_weightPreviewStale = true;
				}
			}
			else if ( stroking && Gizmo.IsLeftMouseDown )
			{
				if ( WeightSession.MoveTo( origin, direction ) > 0 )
					_weightPreviewStale = true;
			}
		}

		if ( stroking && !Gizmo.IsLeftMouseDown )
		{
			var edit = WeightSession.EndStroke();

			if ( edit is not null )
				WeightStrokeFinished?.Invoke();

			UploadWeightRamp();
			_weightPreviewStale = true;
		}

		if ( _weightPreviewStale )
		{
			RefreshWeightPreview();
			_weightPreviewStale = false;
		}

		DrawWeightCursor();
	}

	void DrawWeightCursor()
	{
		if ( _weightCursor is not { } hit )
			return;

		var normal = new Vector3( hit.Normal.x, hit.Normal.y, hit.Normal.z ).Normal;
		var centre = new Vector3( hit.Point.x, hit.Point.y, hit.Point.z );
		var reference = MathF.Abs( normal.z ) > 0.9f ? new Vector3( 1f, 0f, 0f ) : new Vector3( 0f, 0f, 1f );
		var right = Vector3.Cross( normal, reference ).Normal;
		var up = Vector3.Cross( normal, right ).Normal;
		var radius = WeightSession.Radius;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1.5f;
		Gizmo.Draw.Color = new Color( 1f, 0.55f, 0.2f, 0.9f );

		var lift = normal * ( radius * 0.01f );
		const int Segments = 40;
		var previous = centre + right * radius + lift;

		for ( var i = 1; i <= Segments; i++ )
		{
			var angle = i / (float)Segments * MathF.PI * 2f;
			var point = centre + ( right * MathF.Cos( angle ) + up * MathF.Sin( angle ) ) * radius + lift;
			Gizmo.Draw.Line( previous, point );
			previous = point;
		}

		Gizmo.Draw.Line( centre, centre + normal * ( radius * 0.35f ) );
	}
}
