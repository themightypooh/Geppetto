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

		_weightStrokeArmed = false;
		ClearCallout( "weight-material" );
		ClearCallout( "weight-locked" );
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

		// No material, no heat map: strokes would still change the weights, blind.
		if ( _weightMaterial is null )
			ShowCallout( "weight-material", EffigyCalloutKind.Error,
				"Can't show weights: materials/default.vmat would not load, so the heat map can't be drawn. Strokes still paint, blind. Check the engine's core content is installed.",
				lifetime: null );
	}

	/// <summary>Raised when the brush's bone changes mid-session, with the new index.</summary>
	public Action<int> WeightBoneChanged { get; set; }

	/// <summary>
	/// Point the brush at another bone without leaving the mode.
	///
	/// Picking a bone in the Rig tree (or in the viewport) while painting is how anyone expects to
	/// move on to the next bone; the session used to keep the one it was started with, so the
	/// heat map, the label and every stroke went on being about the old bone while the tree said
	/// otherwise. A stroke in flight is committed first — switching mid-gesture must not turn
	/// half a stroke into paint for a bone it was never meant for.
	/// </summary>
	public void SetWeightBone( int bone )
	{
		if ( WeightSession is not { } session || bone < 0 || bone >= session.Skeleton.Count || session.Bone == bone )
			return;

		if ( session.IsStroking && session.EndStroke() is not null )
			WeightStrokeFinished?.Invoke();

		session.Bone = bone;
		RefreshWeightRamp();

		(_weightBarOverlay as EffigyWeightBar)?.Refresh();
		SketchPromptChanged?.Invoke( $"Painting {session.Skeleton.Bones[bone].Name}. Drag on the mesh." );
		WeightBoneChanged?.Invoke( bone );
		Update();
	}

	/// <summary>Pressed, and the stroke has not reached the surface yet. See WeightPaintFrame.</summary>
	private bool _weightStrokeArmed;

	/// <summary>The locked-vertices warning has been given for the stroke in flight.</summary>
	private bool _weightLockedWarned;

	/// <summary>
	/// Say — once per stroke — that the brush is over vertices it cannot lower.
	///
	/// A vertex whose only influence is the painted bone has nowhere for removed weight to go: the
	/// weights must still sum to one. So Subtract, and Set below 1, leave it exactly as it was, and
	/// over a patch made of them a stroke does visibly nothing. That is the kernel being right, but
	/// it looks like the brush being broken unless something says why.
	/// </summary>
	private void WarnLockedUnder( Vec3 point )
	{
		if ( _weightLockedWarned || WeightSession is not { } session )
			return;

		// Set to 1 on a vertex that already has all of it is not a stuck brush, it is a finished one.
		var lowering = session.Brush == WeightBrushKind.Subtract
			|| (session.Brush == WeightBrushKind.Set && session.Target < 1f - 1e-4f);

		if ( !lowering )
			return;

		var locked = session.LockedUnder( point );

		if ( locked == 0 )
			return;

		_weightLockedWarned = true;

		var bone = session.Bone >= 0 && session.Bone < session.Skeleton.Count ? session.Skeleton.Bones[session.Bone].Name : "this bone";

		ShowCallout( "weight-locked", EffigyCalloutKind.Refusal,
			$"Can't take weight off {locked} vertices here: {bone} is their only bone, and the weights must add up to 1. "
			+ "Add weight to a neighbouring bone there first (pick it, brush Add) — that takes it from this one.",
			lifetime: 8f );
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

			if ( !stroking && !_weightStrokeArmed && Gizmo.WasLeftMousePressed )
			{
				// BeginStroke throws for a bone-less brush other than Smooth. Asked here instead, so
				// the answer is a sentence rather than an exception.
				if ( WeightSession.Brush != WeightBrushKind.Smooth
					&& (WeightSession.Bone < 0 || WeightSession.Bone >= WeightSession.Skeleton.Count) )
				{
					RefuseOncePerPress( "weight-no-bone", "Can't paint weights: no bone is picked. Pick one in the Rig tree, then drag on the mesh." );
				}
				else
				{
					_weightStrokeArmed = true;
					_weightLockedWarned = false;
				}
			}

			// Armed at the press, begun on the first frame the held button is over the surface — a
			// stroke started just off the silhouette and dragged on paints, as sculpt's does.
			if ( !stroking && _weightStrokeArmed && Gizmo.IsLeftMouseDown )
			{
				if ( WeightSession.BeginStroke( origin, direction ) )
				{
					stroking = true;
					_weightStrokeArmed = false;
					_weightPreviewStale = true;
				}
			}
			else if ( stroking && Gizmo.IsLeftMouseDown )
			{
				if ( WeightSession.MoveTo( origin, direction ) > 0 )
					_weightPreviewStale = true;
			}

			if ( stroking && _weightCursor is { } over )
				WarnLockedUnder( over.Point );
		}

		// Released without ever reaching the model.
		if ( _weightStrokeArmed && !Gizmo.IsLeftMouseDown )
		{
			_weightStrokeArmed = false;
			Refuse( "Missed the model — start or drag the stroke onto the part." );
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
