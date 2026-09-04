using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Dropping a material onto a face.
///
/// The other half of <see cref="EffigyMaterialBrowser"/>, and the reason the browser is worth
/// having: a material list you can only look at is a material list you may as well have opened in
/// the asset browser. The gesture is the feature.
///
/// It is deliberately NOT a mode. Nothing has to be armed, no tool has to be selected, and the
/// viewport does not care what it was doing a moment ago — the same reasoning as the right-click
/// face menu in EffigyViewport.FaceMenu.cs, which this shares its raycast with. You point at a
/// face, you let go, that face wears the material.
///
/// WHERE THE SLOT COMES FROM. Nowhere on screen. A face carries a slot number and the drop names
/// only a material, so something has to choose the number; Effigy.MaterialDrop does, and does it in
/// the kernel where MaterialDropTests can hold the choice to account. This file's job stops at
/// turning a drop position into a face.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>Raised when a material is dropped on a face: the face, and the material's relative
	/// path. As with the context menu, the viewport resolves what was pointed at and stops — what
	/// it MEANS for the document is the window's business, because the window owns the studio, the
	/// undo stack and the rebuild.</summary>
	public Action<EffigyFaceHit, string> MaterialDropped { get; set; }

	/// <summary>The face the drag is currently over, held between hover events so the frame loop
	/// can light it up. Null when the drag is over nothing pickable, or over nothing at all.</summary>
	private EffigyFaceHit? _dropHit;

	/// <summary>The material the current drag carries, or null when it carries something that is
	/// not one. Kept only so the highlight can be dropped the moment the drag leaves.</summary>
	private string _dropMaterial;

	/// <summary>Green rather than the pick blue or the selected amber, because it is neither: those
	/// two say "this is what your click will choose", and this says "letting go here changes the
	/// model". A third meaning gets a third colour.</summary>
	private static readonly Color MaterialDropColor = new( 0.35f, 0.9f, 0.5f, 1f );

	/// <summary>Turn drops on. Called from the constructor — a widget that has not asked for them
	/// never sees a drag at all, and the failure is silent in exactly the way that costs an hour.
	/// </summary>
	private void EnableMaterialDrops() => AcceptDrops = true;

	/// <summary>Light up the face the drag is over. Called from OnPreFrame with the rest of the
	/// per-frame drawing, because Gizmo.Draw only means anything inside the scene's own frame and a
	/// drag event is nowhere near one.</summary>
	private void MaterialDropFrame()
	{
		TellCameraItsSize();
		ProbeDropFrame();

		if ( _dropHit is { } hit )
			DrawFace( hit.Body, hit.FaceIndex, MaterialDropColor );
	}

	private float _lastFrameProbe;

	/// <summary>The same geometry as ReportDropGeometry, but from INSIDE the frame. Whether those
	/// two disagree is the question — a camera that only knows its viewport while it is rendering
	/// cannot be asked for a ray from a Qt event handler.</summary>
	private void ProbeDropFrame()
	{
		if ( !_probeDrop || RealTime.Now - _lastFrameProbe < 1f )
			return;

		_lastFrameProbe = RealTime.Now;

		var canvas = _canvas.IsValid() ? _canvas.Size : Vector2.Zero;

		// The ray through the MIDDLE of the canvas, which must come out parallel to the way the
		// camera is facing. If it does not, ScreenPixelToRay is not usable here at all and every
		// drop has been aiming at nothing — which no amount of care about the position could fix.
		// Where the gizmo thinks the cursor is, and the ray it built from it. That ray is the one
		// every other pick in this viewport uses and they are all accurate, so it is the reference
		// to calibrate against: whichever FOV convention reproduces it is the right one.
		// THE DECIDING MEASUREMENT. Two opposite corners of the canvas must give two clearly
		// different rays. A spread of exactly 1 means they are the same ray — the pixel is being
		// ignored, every drop is cast through the middle of the view, and only the face in the
		// middle can ever be hit.
		var topLeft = _camera.ScreenPixelToRay( new Vector2( 4f, 4f ) * DpiScale );
		var bottomRight = _camera.ScreenPixelToRay( (canvas - new Vector2( 4f, 4f )) * DpiScale );

		Log.Info( $"[effigy-drop] IN FRAME: canvas={canvas}"
			+ $" viewport={_camera.ScreenRect.Size} customSize={_camera.CustomSize}"
			+ $" cornerSpread={Vector3.Dot( topLeft.Forward, bottomRight.Forward ):0.#####}"
			+ " (1 = the pixel is ignored and only one face is reachable)" );
	}

	public override void OnDragHover( DragEvent ev )
	{
		base.OnDragHover( ev );

		_dropHit = null;
		_dropMaterial = MaterialFromDrag( ev.Data );

		ProbeDrop( ev, _dropMaterial );

		if ( _dropMaterial is null || !DropsAllowed )
		{
			ev.Action = DropAction.Ignore;
			return;
		}

		// Copy whether or not a face is under the cursor. Refusing the drag over empty space would
		// flick the cursor between "yes" and "no" as you cross the model's silhouette on the way to
		// the face you are aiming at, which reads as the window rejecting the material rather than
		// as you not being on a face yet. What actually stops a miss is the hit test in OnDragDrop.
		ev.Action = DropAction.Copy;

		if ( TryPickFaceAt( ev.LocalPosition, out var hit ) )
			_dropHit = hit;
	}

	public override void OnDragDrop( DragEvent ev )
	{
		base.OnDragDrop( ev );

		var material = MaterialFromDrag( ev.Data );

		_dropHit = null;
		_dropMaterial = null;

		if ( material is null || !DropsAllowed )
			return;

		// Picked again from the drop position rather than trusting the last hover. The two are
		// usually the same face, but a drop can arrive without a hover in front of it — dropping
		// straight in from another window is one way — and acting on a face resolved from a stale
		// position would paint whatever the cursor last passed over.
		if ( !TryPickFaceAt( ev.LocalPosition, out var hit ) )
			return;

		MaterialDropped?.Invoke( hit, material );
	}

	public override void OnDragLeave()
	{
		base.OnDragLeave();

		_dropHit = null;
		_dropMaterial = null;
	}

	/// <summary>
	/// Whether a drop can land right now.
	///
	/// The same list the right-click menu refuses on, and for the same reason: while a sketch is
	/// open or a dialog has armed a pick, the bodies are not what you are pointing at, and painting
	/// one mid-pick would edit a body the feature being configured may not even be allowed to
	/// touch. Sculpting is in the list too — a drop there would land on the cage.
	/// </summary>
	private bool DropsAllowed =>
		!IsSketching && !PlanePickMode && !SketchPickMode && !FacePickMode && !EdgePickMode && !BodyPickMode && !BoneToolActive;

	/// <summary>
	/// The face under a point in THIS widget, for a drag that has no cursor ray to borrow.
	///
	/// Everywhere else in the viewport aims with Gizmo.CurrentRay, which is built once a frame from
	/// where the mouse is hovering. A drag is not a hover: the canvas does not report itself under
	/// the mouse while one is in progress, so CaptureCursorRay holds whatever ray was current when
	/// the drag began — the far side of the screen, usually the browser you dragged out of. The ray
	/// has to be built from the position the drag event carries instead.
	///
	/// THE PIXEL SPACE IS MEASURED, NOT ASSUMED, and that is the whole point of this function's
	/// shape. It multiplied the widget-local position by DpiScale at first, copying the scene
	/// viewport, whose comment says the camera renders at physical pixels while Qt reports logical
	/// ones. That is true THERE because that widget sets `CustomSize = Renderer.Size * DpiScale`
	/// itself; this one never sets CustomSize at all, so whatever pixel space its camera ended up in
	/// was a guess — and a guess that is wrong by any factor at all does not miss by a little. It
	/// scales the position AWAY FROM THE TOP-LEFT CORNER, so drops near the corner still land near
	/// where you aimed and everything further out walks off the model entirely: exactly one region
	/// of the viewport appears to accept materials and the rest silently does nothing.
	///
	/// So ask the camera. ScreenRect is the viewport it actually renders into, whatever set it, and
	/// the same fraction across the canvas is the same fraction across that. No DpiScale, no
	/// CustomSize, nothing that has to stay in agreement with a widget we do not own.
	/// </summary>
	private bool TryPickFaceAt( Vector2 position, out EffigyFaceHit hit )
	{
		hit = default;

		if ( !_canvas.IsValid() || !_camera.IsValid() )
			return false;

		if ( !TryCanvasPixel( position, out var pixel ) )
			return false;

		var ray = _camera.ScreenPixelToRay( pixel );

		// Vector3 -> Vec3 is a straight re-type, not a transform: the kernel's axes and s&box's
		// line up exactly. Same conversion CaptureCursorRay does.
		return TryPickFace(
			new Vec3( ray.Position.x, ray.Position.y, ray.Position.z ),
			new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z ),
			out hit );
	}

	/// <summary>
	/// Turn a dragged payload into a material path, or null.
	///
	/// Text first, because that is where both the editor's own asset list and
	/// <see cref="EffigyMaterialBrowser"/> put the relative path, and because a multi-selection
	/// arrives as one line per asset — the first is the one under the cursor.
	///
	/// Then RESOLVED THROUGH THE ASSET SYSTEM rather than believed. Two things fall out of that.
	/// A model or a sound dragged onto a face is refused instead of being written into MaterialNames
	/// as a material that will never load, which is a document you cannot tell is broken by looking
	/// at it. And the path stored is the asset's own RelativePath, so a drag and a pick through the
	/// browse button in EffigyMaterialSlot write the same spelling for the same file — which is what
	/// lets MaterialDrop.SlotFor recognise a material it has already given a slot to.
	/// </summary>
	private static string MaterialFromDrag( DragData data )
	{
		if ( data is null )
			return null;

		foreach ( var candidate in DraggedPaths( data ) )
		{
			var path = candidate?.Trim();

			if ( string.IsNullOrWhiteSpace( path ) )
				continue;

			if ( AssetSystem.FindByPath( path ) is { } asset )
			{
				if ( asset.AssetType == AssetType.Material )
					return asset.RelativePath;

				continue;
			}

			// Not something the asset system knows. Accepted only when it is plainly a relative
			// material path — a document that arrived from another machine and refers to a material
			// this one has not compiled yet is a real case, and refusing it would make the drop
			// silently do nothing. An ABSOLUTE path is refused whatever it ends in: it is true on
			// one machine, and storing it would export a material nobody else can resolve.
			if ( !Path.IsPathRooted( path ) && path.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) )
				return path.Replace( '\\', '/' );
		}

		return null;
	}

	/// <summary>
	/// Tell the camera how big it is, every frame.
	///
	/// CustomSize's own documentation says the camera size is "screen size or render target size"
	/// when this is null — and for a camera living in a SceneRenderingWidget it is neither: its
	/// ScreenRect comes back 0x0. Everything that renders works anyway, because rendering is handed
	/// the target's dimensions directly; everything that asks the camera to turn a PIXEL into
	/// something does not, because there is no pixel space to divide by.
	///
	/// ScreenPixelToRay is the one that mattered here. With a zero ScreenRect it returns the same
	/// centre ray for every pixel — the probe below measures exactly that, by casting through two
	/// opposite corners and comparing — so every material drop, wherever it was released, was cast
	/// through the middle of the view and hit whichever face happened to be there. One face would
	/// take materials and the rest silently would not.
	///
	/// The scene viewport sets this on its own camera for the same reason, in the same place: once a
	/// frame, from the frame tick, because the dock can be resized between any two of them.
	/// </summary>
	private void TellCameraItsSize()
	{
		if ( !_camera.IsValid() || !_canvas.IsValid() )
			return;

		var size = _canvas.Size * DpiScale;

		if ( size.x >= 1f && size.y >= 1f )
			_camera.CustomSize = size;
	}

	/// <summary>
	/// A ray through a pixel of the canvas, built by hand.
	///
	/// CameraComponent.ScreenPixelToRay cannot do this here, and quietly: this camera renders into a
	/// SceneRenderingWidget rather than the game's screen, so its ScreenRect is 0x0, and the pixel it
	/// is handed divides out to nothing. It returns the camera's own centre ray for EVERY pixel —
	/// top-left and bottom-right corners come back identical, which the probe below measures. That
	/// is why the drop only ever hit one face: every release, anywhere in the viewport, was cast
	/// through the middle of the view.
	///
	/// So: an ordinary pinhole, out of THE CAMERA'S OWN PROJECTION MATRIX rather than out of its
	/// FieldOfView. Whether that angle is the horizontal or the vertical one is a convention nothing
	/// in the API states, and getting it wrong scales every ray by the aspect ratio — which on a
	/// nearly square dock is a 5% error that looks like imprecision rather than like a bug, and on a
	/// wide one is badly wrong. The projection matrix has already resolved it: M11 and M22 are the
	/// reciprocals of the half-angle tangents it is actually rendering with, so dividing by them
	/// needs no aspect, no field of view and no convention.
	/// </summary>
	private Ray RayFrom( Vector2 pixel, Vector2 size )
	{
		var rotation = _camera.WorldRotation;
		var origin = _camera.WorldPosition;

		if ( size.x < 1f || size.y < 1f )
			return new Ray( origin, rotation.Forward );

		var projection = _camera.ProjectionMatrix;

		// A projection that has not been set up yet would divide the ray out to the horizon.
		if ( MathF.Abs( projection.M11 ) < 1e-6f || MathF.Abs( projection.M22 ) < 1e-6f )
			return new Ray( origin, rotation.Forward );

		// +1 at the right edge and +1 at the TOP, which is the flip that matters: pixels count down
		// the screen and the camera's up axis counts up it.
		var x = (pixel.x / size.x) * 2f - 1f;
		var y = 1f - (pixel.y / size.y) * 2f;

		var direction = rotation.Forward
			+ rotation.Right * (x / projection.M11)
			+ rotation.Up * (y / projection.M22);

		return new Ray( origin, direction.Normal );
	}

	/// <summary>
	/// A position in this widget, turned into a pixel in the camera's own viewport.
	///
	/// Separate from the pick so the probe below can report the numbers without casting a ray, which
	/// is the difference between "the drop missed" and "the drop was aimed somewhere else".
	/// </summary>
	private bool TryCanvasPixel( Vector2 position, out Vector2 pixel )
	{
		pixel = default;

		var local = position - _canvas.Position;
		var size = _canvas.Size;

		if ( size.x < 1f || size.y < 1f )
			return false;

		// Outside the 3D canvas entirely — over a tool strip, or past an edge. A ray built from here
		// still resolves to something, which is exactly the problem: it would paint a face while you
		// were letting go over a button.
		if ( local.x < 0f || local.y < 0f || local.x > size.x || local.y > size.y )
			return false;

		var viewport = _camera.ScreenRect.Size;

		// A camera with no viewport yet — the first frame, or a dock being dragged — would divide the
		// position by nothing and aim at the corner.
		if ( viewport.x < 1f || viewport.y < 1f )
			return false;

		pixel = new Vector2( viewport.x * (local.x / size.x), viewport.y * (local.y / size.y) );

		return true;
	}

	// --- MATERIAL DROP PROBE ----------------------------------------------------------------------
	//
	// For the one question that costs an afternoon: when a drop does nothing, was it aimed at the
	// wrong place or was it never allowed to act? Those look identical on screen — no highlight, no
	// change — and they have nothing to do with each other.
	//
	// WHAT EACH FIELD RULES OUT, in the order the chain breaks:
	//
	//   material     What the drag carries, after the asset system has vouched for it. "none" means
	//                nothing else below matters: it is being refused as not-a-material.
	//   allowed      DropsAllowed. False means a sketch is open or a dialog has armed a pick, and
	//                the drop is being declined on purpose.
	//   local        Where the event says the cursor is, in this widget.
	//   canvas       The 3D canvas's size. local should be inside it.
	//   viewport     What the camera thinks it renders into. IF THESE TWO DIFFER, every ray is
	//                built through that ratio - which is the bug this probe was written for.
	//   dpi          What the old code multiplied by. Kept only so a report can say whether the
	//                ratio above happens to equal it.
	//   pixel        Where the ray is actually aimed.
	//   hit          The face it found, or none. A miss with sane numbers above means you are
	//                genuinely off the model.

	private static bool _probeDrop;

	/// <summary>
	/// Turn the material drop probe on or off: `effigy_probe_drop 1`.
	///
	/// Reports the geometry IMMEDIATELY as well as arming the per-hover line, because the one number
	/// that matters — whether the camera's viewport matches the canvas it is drawn in — does not
	/// need a drag to be true, and needing one made the question impossible to ask from a console.
	/// </summary>
	[ConCmd( "effigy_probe_drop" )]
	public static void SetDropProbe( int on )
	{
		_probeDrop = on != 0;

		if ( !_probeDrop )
		{
			Log.Info( "[effigy-drop] off" );
			return;
		}

		Log.Info( "[effigy-drop] on — drag a material over the viewport for a line per move" );

		if ( EffigyWindow.Current?.DiagnosticViewport is not { } viewport )
		{
			Log.Info( "[effigy-drop] no Effigy window open, so no canvas to measure" );
			return;
		}

		viewport.ReportDropGeometry();
	}

	/// <summary>The canvas, the camera's viewport, and the ratio between them. If that ratio is not
	/// 1:1 every drop ray is aimed through it, which is the whole bug this was written to find.
	/// </summary>
	internal void ReportDropGeometry()
	{
		var canvas = _canvas.IsValid() ? _canvas.Size : Vector2.Zero;
		var viewport = _camera.IsValid() ? _camera.ScreenRect.Size : Vector2.Zero;

		var ratio = canvas.x > 0f && canvas.y > 0f
			? $"{viewport.x / canvas.x:0.###} x {viewport.y / canvas.y:0.###}"
			: "unknown";

		Log.Info( $"[effigy-drop] canvas={canvas} viewport={viewport} ratio={ratio} dpi={DpiScale}"
			+ $" — a ratio of 1 x 1 means the canvas and the camera agree and the old DpiScale"
			+ $" multiply was the fault; anything else is the ratio every ray was skewed by." );
	}

	private float _lastDropProbe;

	/// <summary>Called from the hover, rate-limited: a drag fires these as fast as the mouse moves
	/// and an unthrottled line per event is a console you cannot read.</summary>
	private void ProbeDrop( DragEvent ev, string material )
	{
		if ( !_probeDrop || RealTime.Now - _lastDropProbe < 0.25f )
			return;

		_lastDropProbe = RealTime.Now;

		var canvas = _canvas.IsValid() ? _canvas.Size : Vector2.Zero;
		var viewport = _camera.IsValid() ? _camera.ScreenRect.Size : Vector2.Zero;
		var pixel = TryCanvasPixel( ev.LocalPosition, out var p ) ? p.ToString() : "outside";
		var face = TryPickFaceAt( ev.LocalPosition, out var hit ) ? $"{hit.Body?.Name}#{hit.FaceIndex}" : "none";

		Log.Info( $"[effigy-drop] material={material ?? "none"} allowed={DropsAllowed}"
			+ $" local={ev.LocalPosition} canvas={canvas} viewport={viewport} dpi={DpiScale}"
			+ $" pixel={pixel} hit={face}" );
	}

	private static IEnumerable<string> DraggedPaths( DragData data )
	{
		if ( !string.IsNullOrWhiteSpace( data.Text ) )
		{
			foreach ( var line in data.Text.Split( '\n' ) )
				yield return line;
		}

		if ( data.Files is { } files )
		{
			foreach ( var file in files )
				yield return file;
		}
	}
}
