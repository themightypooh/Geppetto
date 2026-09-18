using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// Sculpt mode in the viewport: rays in, brush strokes out.
///
/// DELIBERATELY THE THINNEST PART OF THE SCULPT TOOL. Everything with a decision in it — where the
/// cursor sits on the surface, whether the pointer has travelled far enough to earn a sample, how a
/// fast drag gets filled in, what a stroke commits, undo — lives in <see cref="SculptSession"/> in
/// the kernel, where a test can see it. The editor cannot be compiled outside s&amp;box, and reading
/// editor code is how a bug that made every parameter edit a no-op survived long enough to look
/// like three unrelated UI faults. So this file converts Vector3 to Vec3, calls four methods, and
/// draws a circle.
///
/// Anything that starts to look like logic here belongs one floor down.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>The live sculpt, or null when not in sculpt mode.</summary>
	public SculptSession SculptSession { get; private set; }

	public bool IsSculpting => SculptSession is not null;

	/// <summary>Raised after a stroke commits, so the window can mark the feature dirty and
	/// rebuild. Not raised per sample — a stroke is one edit.</summary>
	public Action SculptStrokeFinished { get; set; }

	/// <summary>The mesh the preview was last built from, so a frame that changed nothing does not
	/// rebuild a model.</summary>
	private bool _sculptPreviewStale;

	/// <summary>The preview model for the sculpt surface, kept across dabs so a stroke re-uploads
	/// vertex data in place instead of rebuilding the Model and its buffers every dab — the same
	/// bargain the window's LivePreview makes for a Transform drag. Sculpting a dense mesh without
	/// it allocates a fresh vertex buffer and Model several times a second, which the GC pays for.</summary>
	private EffigyPreview.LivePreview _sculptPreview;

	/// <summary>Where the brush ring is drawn this frame, or null when the cursor is off the
	/// model.</summary>
	private MeshHit? _sculptCursor;

	// The floating number bar, held for the same reason the result strip is: the frame loop has to
	// keep camera drags out of it, or dragging the radius slider also flies the view.
	private Widget _sculptBarOverlay;

	/// <summary>
	/// Put the sculpt number bar on the canvas.
	///
	/// The brushes themselves are stages on the tool bar now. This is the one sculpt control that
	/// stayed floating, because it is about the STROKE - radius, strength, the level you are on -
	/// rather than about which tool is armed, and it wants to be near the thing being brushed.
	/// </summary>
	public void AddSculptOverlay( Widget bar )
	{
		_sculptBarOverlay = bar;
		bar.Position = OverlayMargin + new Vector2( 0f, 46f );
		bar.Visible = false;
	}

	public void BeginSculpt( SculptSession session )
	{
		SculptSession = session ?? throw new ArgumentNullException( nameof( session ) );
		_sculptPreviewStale = true;
		_sculptPreview = null;
	}

	public void EndSculpt()
	{
		// A stroke left running when the mode ends would hold a working mesh nobody will ever
		// commit. Cancel rather than commit: leaving the mode is not a way to finish a stroke.
		if ( SculptSession is { IsStroking: true } )
			SculptSession.CancelStroke();

		SculptSession = null;
		_sculptCursor = null;
		_sculptPreview = null;
	}

	/// <summary>Push the sculpted surface into the viewport, replacing the model in place.</summary>
	public void RefreshSculptPreview()
	{
		if ( SculptSession is null )
			return;

		var mesh = SculptSession.DisplayMesh;

		if ( mesh is null || mesh.FaceCount == 0 || mesh.VertexCount == 0 )
			return;

		// Only positions change while a stroke runs — a level change rebuilds, but within a level
		// the face layout is stable — so the live preview can rewrite its vertex buffers in place.
		// Mid-stroke it rewrites positions only and reuses the normals it already has, skipping the
		// O(vertices) normal fan pass that would otherwise run every dab on a dense import.
		var stroking = SculptSession.IsStroking;

		if ( _sculptPreview is not null
			&& _renderer?.Model == _sculptPreview.Model
			&& (stroking
				? _sculptPreview.TryUpdatePositions( mesh )
				: _sculptPreview.TryUpdate( mesh, _ => null, MeshNormals.DefaultSmoothingAngleDegrees )) )
			return;

		_sculptPreview = EffigyPreview.LivePreview.Build( mesh, _ => null, MeshNormals.DefaultSmoothingAngleDegrees );

		if ( _sculptPreview?.Model is null )
			return;

		// The renderer's model is swapped rather than SetModel called: SetModel destroys and rebuilds
		// the GameObject, which is fine once per feature edit and not fine several times a second
		// during a stroke.
		if ( _renderer is not null )
			_renderer.Model = _sculptPreview.Model;
		else
			SetModel( _sculptPreview.Model, frameCamera: false );
	}

	private void SculptFrame()
	{
		if ( SculptSession is null )
			return;

		_sculptCursor = null;

		var stroking = SculptSession.IsStroking;

		// The pointer leaving the canvas does NOT end a stroke — see SculptSession.MoveTo on why
		// dragging off the model and back has to keep working. It only stops new samples.
		if ( _canvasHasCursor )
		{
			var ray = Gizmo.CurrentRay;
			var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
			var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

			_sculptCursor = SculptSession.Hover( origin, direction );

			if ( !stroking && Gizmo.WasLeftMousePressed )
			{
				// Invert is read ONCE, when the stroke starts. Reading the modifier every frame
				// would reverse the brush halfway through a gesture the moment Ctrl is released.
				SculptSession.Inverted = Editor.Application.IsKeyDown( KeyCode.Control );

				if ( SculptSession.BeginStroke( origin, direction ) )
				{
					stroking = true;
					_sculptPreviewStale = true;
				}
			}
			else if ( stroking && Gizmo.IsLeftMouseDown )
			{
				if ( SculptSession.MoveTo( origin, direction ) > 0 )
					_sculptPreviewStale = true;
			}
		}

		// Released. There is no WasLeftMouseReleased in the Gizmo input this editor uses, so the
		// end of a stroke is the frame the button is no longer down — which is the same thing and
		// needs no API that might not be there.
		if ( stroking && !Gizmo.IsLeftMouseDown )
		{
			SculptSession.EndStroke();
			_sculptPreviewStale = true;
			SculptStrokeFinished?.Invoke();
		}

		if ( _sculptPreviewStale )
		{
			RefreshSculptPreview();
			_sculptPreviewStale = false;
		}

		DrawBrushCursor();
	}

	/// <summary>
	/// The ring on the surface, lying in the surface's own plane rather than facing the camera.
	///
	/// A camera-facing ring is easier to draw and lies about what the brush will do: the radius is
	/// in world units along the surface, so on a face turned away from the viewer a screen-facing
	/// circle covers far more of the model than it claims. Drawn flat on the surface it reads as the
	/// footprint it actually is.
	/// </summary>
	private void DrawBrushCursor()
	{
		if ( _sculptCursor is not { } hit )
			return;

		var normal = new Vector3( hit.Normal.x, hit.Normal.y, hit.Normal.z ).Normal;
		var centre = new Vector3( hit.Point.x, hit.Point.y, hit.Point.z );

		// Any two perpendiculars will do; the ring has no orientation to get wrong.
		var reference = MathF.Abs( normal.z ) > 0.9f ? new Vector3( 1f, 0f, 0f ) : new Vector3( 0f, 0f, 1f );
		var right = Vector3.Cross( normal, reference ).Normal;
		var up = Vector3.Cross( normal, right ).Normal;

		var radius = SculptSession.Radius;

		// Invert is a geometry thing; masking has its own erase direction and never reads Inverted.
		var inverted = SculptSession.Inverted && !SculptSession.Masking;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1.5f;
		Gizmo.Draw.Color = SculptSession.Masking ? MaskCursorColor : inverted ? BrushInvertedColor : BrushCursorColor;

		// Lifted off the surface by a whisker so it is not z-fighting the face it sits on.
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

		// A stub along the normal, so the brush reads as sitting ON the surface rather than floating
		// somewhere near it — the one thing a flat ring on a curved model is genuinely ambiguous
		// about.
		Gizmo.Draw.Line( centre, centre + normal * (radius * 0.35f) );

		// A minus in the middle, so the inverted state is visible BEFORE the click, not after it —
		// a carved stroke that reads as a draw until it bites is a control that lies.
		if ( inverted )
		{
			var half = radius * 0.35f;
			Gizmo.Draw.Line( centre + lift - right * half, centre + lift + right * half );
		}
	}

	/// <summary>Ordinary brush: the same blue the rest of this editor uses for "you can act here".
	/// </summary>
	private static readonly Color BrushCursorColor = new( 0.35f, 0.75f, 1f, 0.9f );

	/// <summary>Masking is a different job and gets a different colour, or a stroke that protects
	/// looks exactly like one that sculpts.</summary>
	private static readonly Color MaskCursorColor = new( 1f, 0.85f, 0.3f, 0.9f );

	/// <summary>Ctrl-inverted brush: carving, so it gets the warm "removing" red rather than the
	/// "you can act here" blue — the state has to read before the click, not after.</summary>
	private static readonly Color BrushInvertedColor = new( 1f, 0.45f, 0.4f, 0.9f );
}

/// <summary>
/// Edit mode in the viewport: rays in, picks and drags out.
///
/// Kept in this file beside sculpting because it is built the same way and for the same reason:
/// everything that decides what an edit DOES is in <see cref="MeshEditSession"/>, in the kernel,
/// where the headless suite drives it. This half turns the cursor into a vertex, edge or face,
/// draws the selection, hosts the move handle and keeps the preview in step.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>The live edit, or null outside Edit mode.</summary>
	public MeshEditSession MeshEditSession { get; private set; }

	public bool IsMeshEditing => MeshEditSession is not null;

	/// <summary>Raised when a pick or a drag changed the session, so the window can refresh its bar
	/// and prompt. Not raised per drag sample.</summary>
	public Action MeshEditChanged { get; set; }

	/// <summary>Enter (13) and Escape (27) while editing; true when the window used the key.</summary>
	public Func<char, bool> MeshEditKeyPressed { get; set; }

	private EffigyPreview.LivePreview _meshEditPreview;
	private int _meshEditPreviewRevision = -1;

	/// <summary>
	/// The edit's live modifiers (mirror, array, subdivide, solidify), or null. When set, the model
	/// shown is the cage run through them — what the body will be — while the wire, the picking and
	/// every tool still work on the cage, as Blender shows modifiers in edit mode.
	/// </summary>
	public Func<PolyMesh, PolyMesh> MeshEditModifiers
	{
		get => _meshEditModifiers;
		set
		{
			_meshEditModifiers = value;
			_meshEditPreviewRevision = -1;
		}
	}

	private Func<PolyMesh, PolyMesh> _meshEditModifiers;
	private MeshBVH _meshEditTree;
	private int _meshEditTreeRevision = -1;

	private (EditElement Kind, int Index, EdgeKey Edge)? _meshEditHover;

	private bool _meshEditDragging;
	private Vector3 _meshEditDragDelta;

	private Widget _meshEditBarOverlay;

	public void AddMeshEditOverlay( Widget bar )
	{
		_meshEditBarOverlay = bar;
		bar.Visible = false;
		PlaceMeshEditBar();
	}

	/// <summary>
	/// Edit mode's status line runs the full width of the viewport's bottom edge, like a status
	/// bar, rather than floating over the model where it covered what you were selecting. Placed
	/// every frame because the viewport is the one that knows its own size.
	/// </summary>
	private void PlaceMeshEditBar()
	{
		if ( _meshEditBarOverlay?.IsValid() != true )
			return;

		var size = _canvas.Size;
		if ( MathF.Abs( _meshEditBarOverlay.Width - size.x ) > 0.5f )
			_meshEditBarOverlay.FixedWidth = size.x;

		var at = new Vector2( 0f, size.y - _meshEditBarOverlay.Height );
		if ( (_meshEditBarOverlay.Position - at).Length > 0.5f )
			_meshEditBarOverlay.Position = at;
	}

	public void BeginMeshEdit( MeshEditSession session )
	{
		MeshEditSession = session ?? throw new ArgumentNullException( nameof( session ) );
		_meshEditPreview = null;
		_meshEditPreviewRevision = -1;
		_meshEditTree = null;
		_meshEditTreeRevision = -1;
		_meshEditHover = null;
		RefreshMeshEditPreview();
	}

	public void EndMeshEdit()
	{
		if ( MeshEditSession is { IsDragging: true } )
			MeshEditSession.EndDrag( keep: true );

		MeshEditSession = null;

		// Do not keep the finished session's mesh alive through the wire cache.
		_meshEdgeCache = null;
		_meshEdgeCacheOwner = null;
		_meshEdgeCacheRevision = -1;
		_meshEditPreview = null;
		_meshEditTree = null;
		_meshEditHover = null;
		_meshEditDragging = false;
		_meshBoxing = false;
		MeshKnifeArmed = false;
		MeshLoopCutArmed = false;
	}

	/// <summary>Push the working mesh into the viewport. Positions only while dragging — the face
	/// layout cannot change mid-drag — and a full rebuild otherwise.</summary>
	public void RefreshMeshEditPreview()
	{
		if ( MeshEditSession is not { } session )
			return;

		if ( session.Revision == _meshEditPreviewRevision && _meshEditPreview is not null && _renderer?.Model == _meshEditPreview.Model )
			return;

		var mesh = session.Mesh;
		_meshEditPreviewRevision = session.Revision;

		if ( mesh is null || mesh.FaceCount == 0 )
			return;

		// A modifier's output keeps its topology while only the cage's positions move, so the fast
		// positions-only upload below still holds during a drag.
		if ( _meshEditModifiers is not null && !session.IsRetopologizing )
			mesh = _meshEditModifiers( mesh );

		if ( _meshEditPreview is not null
			&& _renderer?.Model == _meshEditPreview.Model
			&& (session.IsDragging
				? _meshEditPreview.TryUpdatePositions( mesh )
				: _meshEditPreview.TryUpdate( mesh, _ => null, MeshNormals.DefaultSmoothingAngleDegrees )) )
			return;

		_meshEditPreview = EffigyPreview.LivePreview.Build( mesh, _ => null, MeshNormals.DefaultSmoothingAngleDegrees );

		if ( _meshEditPreview?.Model is null )
			return;

		if ( _renderer is not null )
			_renderer.Model = _meshEditPreview.Model;
		else
			SetModel( _meshEditPreview.Model, frameCamera: false );
	}

	private MeshBVH MeshEditTree()
	{
		var session = MeshEditSession;

		if ( _meshEditTree is null || _meshEditTreeRevision != session.Revision )
		{
			// Refit is enough while only positions move; a new face layout needs a new tree.
			if ( _meshEditTree is not null && session.IsDragging )
				_meshEditTree.Refit( session.Mesh );
			else
				_meshEditTree = MeshBVH.Build( session.Mesh );

			_meshEditTreeRevision = session.Revision;
		}

		return _meshEditTree;
	}

	private void MeshEditFrame()
	{
		if ( MeshEditSession is not { } session )
			return;

		PlaceMeshEditBar();

		var mesh = session.Mesh;
		_meshEditHover = null;

		// The move handle first: Gizmo.Control registers a hitbox, and a click that grabs an arrow
		// must not also select the face behind it.
		MeshEditHandleFrame( session );

		if ( MeshKnifeArmed )
		{
			MeshKnifeFrame( session );
			RefreshMeshEditPreview();
			DrawMeshEditOverlay( session );
			return;
		}

		if ( MeshLoopCutArmed )
		{
			MeshLoopCutFrame( session );
			RefreshMeshEditPreview();
			DrawMeshEditOverlay( session );
			return;
		}

		if ( _retopoStroke is not null )
		{
			RetopoStrokeFrame( session );
			RefreshMeshEditPreview();
			DrawMeshEditOverlay( session );
			return;
		}

		if ( MeshCircle && _canvasHasCursor && !_meshEditDragging && !session.IsPreviewing )
		{
			MeshCircleFrame( session );
			RefreshMeshEditPreview();
			DrawMeshEditOverlay( session );
			return;
		}

		if ( _canvasHasCursor && !_meshEditDragging && !session.IsPreviewing )
		{
			var ray = Gizmo.CurrentRay;
			var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
			var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

			var surfaceHit = MeshEditTree().Raycast( mesh, origin, direction );

			if ( surfaceHit is { } hit && hit.FaceIndex >= 0 && hit.FaceIndex < mesh.FaceCount )
			{
				var resolved = Resolve( session, hit );

				if ( !session.IsRetopologizing || IsRetopoElement( session, resolved ) )
					_meshEditHover = resolved;
			}

			if ( session.IsRetopologizing && surfaceHit is { } over )
				DrawPolyBuildGhost( session, over.Point );

			if ( Gizmo.WasLeftMousePressed && !Gizmo.HasHovered )
			{
				var shift = Editor.Application.IsKeyDown( KeyCode.Shift );
				var ctrl = Editor.Application.IsKeyDown( KeyCode.Control );
				var alt = Editor.Application.IsKeyDown( KeyCode.Alt );
				var how = shift ? MeshEditSession.Combine.Toggle : ctrl ? MeshEditSession.Combine.Remove : MeshEditSession.Combine.Replace;

				if ( session.IsRetopologizing && ctrl )
				{
					// Poly Build: Ctrl+click places, as in Blender. A plain click still selects,
					// which is how you pick an open edge to grow a new strip from.
					if ( surfaceHit is { } place )
					{
						if ( RetopoStrip )
							_retopoStroke = new List<Vec3> { place.Point };
						else
							MeshPolyBuild?.Invoke( place.Point, shift, PolyBuildWeld( place.Point ) );
					}
				}
				else if ( _meshEditHover is { } h )
				{
					switch ( h.Kind )
					{
						case EditElement.Vertex: session.SelectVertex( h.Index, how ); break;
						case EditElement.Edge when alt && Editor.Application.IsKeyDown( KeyCode.Control ): session.SelectEdgeRing( h.Edge, shift ? MeshEditSession.Combine.Add : MeshEditSession.Combine.Replace ); break;
						case EditElement.Edge when alt: session.SelectEdgeLoop( h.Edge, shift ? MeshEditSession.Combine.Add : how ); break;
						case EditElement.Edge: session.SelectEdge( h.Edge, how ); break;
						case EditElement.Face: session.SelectFace( h.Index, how ); break;
					}
				}
				else
				{
					// Empty space: start a box. A click that never becomes a box clears on release.
					_meshBoxFrom = CameraTangent( ray.Forward );
					_meshBoxTo = _meshBoxFrom;
					_meshLasso.Clear();
					_meshLasso.Add( _meshBoxFrom );
					_meshBoxHow = shift ? MeshEditSession.Combine.Add : ctrl ? MeshEditSession.Combine.Remove : MeshEditSession.Combine.Replace;
					_meshBoxing = true;
				}

				MeshEditChanged?.Invoke();
			}
		}

		MeshBoxFrame( session );

		RefreshMeshEditPreview();
		DrawMeshEditOverlay( session );
	}

	/// <summary>The element of the session's pick mode nearest the hit, on the face the ray hit —
	/// so a vertex or edge behind the model can never be picked through it.</summary>
	private static (EditElement, int, EdgeKey) Resolve( MeshEditSession session, MeshHit hit )
	{
		var mesh = session.Mesh;
		var face = mesh.Faces[hit.FaceIndex];

		switch ( session.Mode )
		{
			case EditElement.Vertex:
			{
				var best = face.Indices[0];
				var bestD = float.MaxValue;

				foreach ( var v in face.Indices )
				{
					var d = (mesh.Positions[v] - hit.Point).LengthSquared;
					if ( d < bestD )
					{
						bestD = d;
						best = v;
					}
				}

				return (EditElement.Vertex, best, default);
			}

			case EditElement.Edge:
				return (EditElement.Edge, -1, NearestEdgeOnFace( mesh, hit.FaceIndex, hit.Point ));

			default:
				return (EditElement.Face, hit.FaceIndex, default);
		}
	}

	/// <summary>The edge of one face nearest a point on it. Shared by edge picking and the loop cut
	/// preview, so the edge the cursor reads as being on is the same in both.</summary>
	private static EdgeKey NearestEdgeOnFace( PolyMesh mesh, int faceIndex, Vec3 point )
	{
		var idx = mesh.Faces[faceIndex].Indices;
		var n = idx.Length;
		var best = new EdgeKey( idx[0], idx[1 % n] );
		var bestD = float.MaxValue;

		for ( var i = 0; i < n; i++ )
		{
			var a = mesh.Positions[idx[i]];
			var b = mesh.Positions[idx[(i + 1) % n]];
			var d = SegmentDistanceSquared( point, a, b );

			if ( d < bestD )
			{
				bestD = d;
				best = new EdgeKey( idx[i], idx[(i + 1) % n] );
			}
		}

		return best;
	}

	private static float SegmentDistanceSquared( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var t = ab.LengthSquared < 1e-12f ? 0f : Math.Clamp( Vec3.Dot( p - a, ab ) / ab.LengthSquared, 0f, 1f );
		return (a + ab * t - p).LengthSquared;
	}

	/// <summary>
	/// Three arrows at the selection's centre. The whole drag is one step in the session, however
	/// many frames it spans — BeginDrag on the first movement, Drag with the running total, EndDrag
	/// on release.
	/// </summary>
	private void MeshEditHandleFrame( MeshEditSession session )
	{
		if ( session.IsPreviewing || MeshKnifeArmed || session.AffectedVertices().Count == 0 )
		{
			FinishMeshEditDrag( session );
			return;
		}

		var centre = session.IsDragging
			? _meshEditDragAnchor + _meshEditDragDelta
			: ToVector( session.SelectionCentre() );

		using ( Gizmo.Scope( "mesh-edit-move", new Transform( centre ) ) )
		{
			Gizmo.Hitbox.DepthBias = 0.01f;

			switch ( MeshHandleMode )
			{
				case BodyDragMode.Rotate:
				{
					if ( !Gizmo.Control.Rotate( "mesh-edit-rot", Rotation.Identity, out var rotation ) )
						break;

					if ( !StartMeshEditDrag( session, centre ) )
						return;

					// The handle reports the whole turn since the grab, the same as the Transform
					// handle reads it, so this is absolute rather than accumulated.
					var w = rotation.w.Clamp( -1f, 1f );
					var sin = MathF.Sqrt( MathF.Max( 0f, 1f - w * w ) );

					if ( sin > 1e-6f )
					{
						var axis = new Vec3( rotation.x, rotation.y, rotation.z ) / sin;
						session.DragRotate( ToVec( _meshEditDragAnchor ), axis, 2f * MathF.Acos( w ).RadianToDegree() );
					}

					return;
				}

				case BodyDragMode.Scale:
				{
					if ( !Gizmo.Control.Scale( "mesh-edit-scl", 1f, out var scale ) )
						break;

					if ( !StartMeshEditDrag( session, centre ) )
						return;

					var f = scale.Clamp( 0.01f, 100f );
					session.DragScale( ToVec( _meshEditDragAnchor ), new Vec3( f, f, f ) );
					return;
				}

				default:
				{
					if ( !Gizmo.Control.Position( "mesh-edit-pos", Vector3.Zero, out var delta, Rotation.Identity ) )
						break;

					if ( !StartMeshEditDrag( session, centre ) )
						return;

					_meshEditDragDelta += delta;
					session.Drag( new Vec3( _meshEditDragDelta.x, _meshEditDragDelta.y, _meshEditDragDelta.z ) );
					return;
				}
			}
		}

		if ( _meshEditDragging && Gizmo.IsLeftMouseDown )
			return;

		FinishMeshEditDrag( session );
	}

	private Vector3 _meshEditDragAnchor;

	/// <summary>Which handle Edit mode shows: move, rotate or scale. G / R / S, as in Blender.</summary>
	public BodyDragMode MeshHandleMode { get; set; } = BodyDragMode.Move;

	/// <summary>See through the model: pick and box-select what is behind the front faces too.</summary>
	public bool MeshXray { get; set; }

	private static Vec3 ToVec( Vector3 v ) => new( v.x, v.y, v.z );

	/// <summary>Which way is right on screen, in the model's own space. Vertex slide resolves its
	/// rail against this, so the edge running across the view is the one that slides.</summary>
	public Vec3 CameraRight => ToVec( _camera.WorldRotation.Right );

	// --- loop cut --------------------------------------------------------------------------------

	/// <summary>
	/// Loop cut armed: the loop that WOULD be cut is drawn under the cursor as you move over the
	/// model, and a click places it. This is the gesture Blender made standard, and the reason is
	/// that a loop cut is chosen by where it lands, not by which edge seeds it — picking a seed edge
	/// first and finding out afterwards is the same operation with the feedback removed.
	/// </summary>
	public bool MeshLoopCutArmed
	{
		get => _meshLoopCutArmed;
		set
		{
			_meshLoopCutArmed = value;
			_meshLoopCutPreview = null;
			_meshLoopCutEdge = null;
		}
	}

	/// <summary>An edge was chosen to cut across.</summary>
	public Action<EdgeKey> MeshLoopCutPlaced { get; set; }

	private bool _meshLoopCutArmed;
	private List<Vec3> _meshLoopCutPreview;
	private EdgeKey? _meshLoopCutEdge;

	private void MeshLoopCutFrame( MeshEditSession session )
	{
		_meshLoopCutPreview = null;
		_meshLoopCutEdge = null;

		if ( _canvasHasCursor )
		{
			var ray = Gizmo.CurrentRay;
			var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
			var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

			if ( MeshEditTree().Raycast( session.Mesh, origin, direction ) is { } hit
				&& hit.FaceIndex >= 0 && hit.FaceIndex < session.Mesh.FaceCount )
			{
				// The cut runs ACROSS the edge nearest the cursor, so the loop follows the face's
				// other axis - which is exactly the loop that appears under the mouse in Blender.
				var edge = NearestEdgeOnFace( session.Mesh, hit.FaceIndex, hit.Point );
				var points = session.LoopCutPreview( edge, 0.5f );

				if ( points is { Count: > 1 } )
				{
					_meshLoopCutPreview = points;
					_meshLoopCutEdge = edge;
				}
			}
		}

		DrawLoopCutPreview();

		if ( Gizmo.WasLeftMousePressed && !Gizmo.HasHovered && _meshLoopCutEdge is { } chosen )
		{
			MeshLoopCutArmed = false;
			MeshLoopCutPlaced?.Invoke( chosen );
		}
	}

	private void DrawLoopCutPreview()
	{
		if ( _meshLoopCutPreview is not { Count: > 1 } points )
			return;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 3f;
		Gizmo.Draw.Color = MeshLoopCutColor;

		// Closed: the ring comes back round, so the last point joins the first.
		for ( var i = 0; i < points.Count; i++ )
			Gizmo.Draw.Line( ToVector( points[i] ), ToVector( points[(i + 1) % points.Count] ) );
	}

	/// <summary>Yellow, so the loop reads as a thing about to happen rather than a thing selected.</summary>
	private static readonly Color MeshLoopCutColor = new( 1f, 0.85f, 0.2f, 1f );

	// --- knife -----------------------------------------------------------------------------------

	/// <summary>Knife armed: each click on the model is a point, and each point after the first
	/// cuts from the one before. Enter or Escape puts it down.</summary>
	public bool MeshKnifeArmed
	{
		get => _meshKnifeArmed;
		set
		{
			_meshKnifeArmed = value;
			_meshKnifeLast = null;
		}
	}

	/// <summary>The knife is being used as Bisect: one line, and the cut runs the whole way
	/// through the model rather than stopping at the line's ends.</summary>
	public bool MeshKnifeBisect { get; set; }

	private bool _meshKnifeArmed;
	private Vec3? _meshKnifeLast;

	/// <summary>A cut is asked for: from, to, the view direction, through everything (X-ray).</summary>
	public Action<Vec3, Vec3, Vec3, bool> MeshKnifeCut { get; set; }

	/// <summary>A bisect line was drawn: from, to, the view direction.</summary>
	public Action<Vec3, Vec3, Vec3> MeshBisectCut { get; set; }

	private void MeshKnifeFrame( MeshEditSession session )
	{
		Vec3? cursor = null;

		if ( _canvasHasCursor )
		{
			var ray = Gizmo.CurrentRay;
			var origin = ToVec( ray.Position );
			var direction = ToVec( ray.Forward );

			if ( MeshEditTree().Raycast( session.Mesh, origin, direction ) is { } hit )
				cursor = hit.Point;

			if ( Gizmo.WasLeftMousePressed && cursor is { } p )
			{
				if ( _meshKnifeLast is { } from )
				{
					if ( MeshKnifeBisect )
					{
						MeshBisectCut?.Invoke( from, p, direction );
						MeshKnifeArmed = false;
						MeshKnifeBisect = false;
						return;
					}

					MeshKnifeCut?.Invoke( from, p, direction, MeshXray );
				}

				_meshKnifeLast = p;
			}
		}

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 2f;
		Gizmo.Draw.Color = KnifeColor;

		if ( _meshKnifeLast is { } last )
		{
			DrawVertexDot( last, MathF.Max( session.Mesh.BoundsDiagonal, 1e-3f ) * 0.006f, KnifeColor );

			if ( cursor is { } c )
				Gizmo.Draw.Line( ToVector( last ), ToVector( c ) );
		}

		if ( cursor is { } at )
			DrawVertexDot( at, MathF.Max( session.Mesh.BoundsDiagonal, 1e-3f ) * 0.004f, KnifeColor );
	}

	private static readonly Color KnifeColor = new( 1f, 0.85f, 0.3f, 1f );

	// --- box select ------------------------------------------------------------------------------

	private bool _meshBoxing;
	private Vector2 _meshBoxFrom, _meshBoxTo;
	private readonly List<Vector2> _meshLasso = new();

	/// <summary>Drag on empty space draws a free lasso rather than a box.</summary>
	public bool MeshLasso { get; set; }
	private MeshEditSession.Combine _meshBoxHow;

	/// <summary>
	/// A direction as the camera sees it: its right and up over its forward, i.e. where it lands on
	/// a screen one unit in front of the eye. Two corner rays in these terms bound the box, and any
	/// point's direction in the same terms says whether it is inside — no pixel projection needed.
	/// </summary>
	private Vector2 CameraTangent( Vector3 direction )
	{
		var r = _camera.WorldRotation;
		var z = Vector3.Dot( direction, r.Forward );
		if ( z <= 1e-5f )
			return new Vector2( float.NaN, float.NaN );
		return new Vector2( Vector3.Dot( direction, r.Right ) / z, Vector3.Dot( direction, r.Up ) / z );
	}

	private void MeshBoxFrame( MeshEditSession session )
	{
		if ( !_meshBoxing )
			return;

		if ( _canvasHasCursor )
		{
			_meshBoxTo = CameraTangent( Gizmo.CurrentRay.Forward );

			// A lasso point every time the cursor has moved a little — enough to follow a curve,
			// few enough that the inside test stays cheap.
			if ( MeshLasso && !float.IsNaN( _meshBoxTo.x ) && (_meshLasso.Count == 0 || (_meshLasso[^1] - _meshBoxTo).Length > 0.004f) )
				_meshLasso.Add( _meshBoxTo );
		}

		var lasso = MeshLasso && _meshLasso.Count >= 3;
		var min = new Vector2( MathF.Min( _meshBoxFrom.x, _meshBoxTo.x ), MathF.Min( _meshBoxFrom.y, _meshBoxTo.y ) );
		var max = new Vector2( MathF.Max( _meshBoxFrom.x, _meshBoxTo.x ), MathF.Max( _meshBoxFrom.y, _meshBoxTo.y ) );

		if ( lasso )
		{
			foreach ( var q in _meshLasso )
			{
				min = new Vector2( MathF.Min( min.x, q.x ), MathF.Min( min.y, q.y ) );
				max = new Vector2( MathF.Max( max.x, q.x ), MathF.Max( max.y, q.y ) );
			}

			DrawMeshLasso();
		}
		else
		{
			DrawMeshBox( min, max );
		}

		if ( Gizmo.IsLeftMouseDown )
			return;

		_meshBoxing = false;

		// Barely moved: that was a click on nothing, which clears (unless a modifier said otherwise).
		if ( (max - min).Length < 0.004f )
		{
			if ( _meshBoxHow == MeshEditSession.Combine.Replace )
				session.ClearSelection();

			MeshEditChanged?.Invoke();
			return;
		}

		if ( _meshBoxHow == MeshEditSession.Combine.Replace )
			session.ClearSelection();

		SelectInRegion( session, t => t.x >= min.x && t.x <= max.x && t.y >= min.y && t.y <= max.y && (!lasso || InsideLasso( t )),
			_meshBoxHow == MeshEditSession.Combine.Remove ? MeshEditSession.Combine.Remove : MeshEditSession.Combine.Add );

		MeshEditChanged?.Invoke();
	}

	/// <summary>
	/// Add (or remove) every element whose screen position passes <paramref name="inside"/> and that
	/// the eye can see — unless X-ray is on. Shared by box, lasso and circle select.
	/// </summary>
	private void SelectInRegion( MeshEditSession session, Func<Vector2, bool> inside, MeshEditSession.Combine how )
	{
		var mesh = session.Mesh;
		var eye = _camera.WorldPosition;
		var tree = MeshEditTree();
		var eps = MathF.Max( mesh.BoundsDiagonal, 1e-3f ) * 1e-3f;

		bool Inside( Vec3 p )
		{
			var t = CameraTangent( ToVector( p ) - eye );
			return !float.IsNaN( t.x ) && inside( t );
		}

		// Visible when the first thing the eye's ray meets on the way to the point is the point.
		bool Visible( Vec3 p )
		{
			if ( MeshXray )
				return true;

			var to = p - ToVec( eye );
			var d = to.Length;
			return tree.Raycast( mesh, ToVec( eye ), to / d ) is not { } hit || hit.Distance >= d - eps;
		}

		var picked = new List<int>();
		var edges = new List<EdgeKey>();

		switch ( session.Mode )
		{
			case EditElement.Vertex:
				for ( var i = 0; i < mesh.VertexCount; i++ )
					if ( Inside( mesh.Positions[i] ) && Visible( mesh.Positions[i] ) )
						picked.Add( i );
				break;

			case EditElement.Edge:
				foreach ( var key in MeshEditEdges( session ) )
				{
					var a = mesh.Positions[key.A];
					var b = mesh.Positions[key.B];
					if ( Inside( a ) && Inside( b ) && Visible( (a + b) * 0.5f ) )
						edges.Add( key );
				}
				break;

			default:
				for ( var f = 0; f < mesh.FaceCount; f++ )
				{
					var c = mesh.FaceCentroid( mesh.Faces[f] );
					if ( Inside( c ) && Visible( c + mesh.FaceNormal( mesh.Faces[f] ) * eps ) )
						picked.Add( f );
				}
				break;
		}

		foreach ( var i in picked )
		{
			if ( session.Mode == EditElement.Vertex )
				session.SelectVertex( i, how );
			else
				session.SelectFace( i, how );
		}

		foreach ( var e in edges )
			session.SelectEdge( e, how );
	}

	// --- poly build ------------------------------------------------------------------------------

	/// <summary>Ctrl+click while retopologizing: where on the surface, whether Shift was held (close
	/// a triangle on the third corner), and the weld distance for this spot.</summary>
	public Action<Vec3, bool, float> MeshPolyBuild { get; set; }

	/// <summary>
	/// The weld distance at a point: a fixed share of its distance from the eye, so the reach feels
	/// the same on screen whether you are zoomed in on an eye or out on a whole body.
	/// </summary>
	public float PolyBuildWeld( Vec3 at ) => (ToVector( at ) - _camera.WorldPosition).Length * 0.025f;

	/// <summary>
	/// The face the next Ctrl+click would make, drawn under the cursor: green when the click is
	/// fine, red when it would be refused (twisted, facing into the body, or on top of a face you
	/// already drew). Corners that would weld to a vertex already there get a ring.
	/// </summary>
	private void DrawPolyBuildGhost( MeshEditSession session, Vec3 at )
	{
		var plan = session.PlanPolyBuild( at, PolyBuildWeld( at ) );

		if ( plan.Corners.Length == 0 )
			return;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 2f;
		Gizmo.Draw.Color = plan.IsValid ? new Color( 0.36f, 0.79f, 0.65f, 0.95f ) : new Color( 0.89f, 0.29f, 0.29f, 0.95f );

		if ( plan.Corners.Length > 1 )
		{
			for ( var i = 0; i < plan.Corners.Length; i++ )
				Gizmo.Draw.Line( ToVector( plan.Corners[i] ), ToVector( plan.Corners[(i + 1) % plan.Corners.Length] ) );
		}

		// A lone drop, or the corner this click adds, is marked where it will land.
		var ring = PolyBuildWeld( at );

		for ( var i = 0; i < plan.Corners.Length; i++ )
		{
			var placedNow = plan.Extends ? i >= 2 : plan.ClosesFace ? i == plan.ClickedCorner : true;
			if ( !placedNow )
				continue;

			var welds = plan.Vertices[i] >= 0;
			Gizmo.Draw.Color = welds ? new Color( 1f, 0.78f, 0.3f, 1f ) : Gizmo.Draw.Color;
			DrawScreenRing( ToVector( plan.Corners[i] ), welds ? ring : ring * 0.35f );
		}
	}

	private void DrawScreenRing( Vector3 centre, float radius )
	{
		var r = _camera.WorldRotation;
		const int Segments = 20;

		for ( var i = 0; i < Segments; i++ )
		{
			var a0 = i * MathF.Tau / Segments;
			var a1 = (i + 1) * MathF.Tau / Segments;
			Gizmo.Draw.Line(
				centre + (r.Right * MathF.Cos( a0 ) + r.Up * MathF.Sin( a0 )) * radius,
				centre + (r.Right * MathF.Cos( a1 ) + r.Up * MathF.Sin( a1 )) * radius );
		}
	}

	// --- strip brush -----------------------------------------------------------------------------

	/// <summary>With the strip brush on, Ctrl+drag lays a whole strip of quads along the stroke
	/// instead of Ctrl+click laying one.</summary>
	public bool RetopoStrip { get; set; }

	/// <summary>A finished stroke: its points on the surface, the strip width (0 = the selected
	/// edge's), and the weld distance.</summary>
	public Action<List<Vec3>, float, float> MeshPolyStroke { get; set; }

	private List<Vec3> _retopoStroke;

	private void RetopoStrokeFrame( MeshEditSession session )
	{
		var ray = Gizmo.CurrentRay;
		var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
		var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );
		var weld = PolyBuildWeld( _retopoStroke[^1] );

		if ( _canvasHasCursor && MeshEditTree().Raycast( session.Mesh, origin, direction ) is { } hit
			&& (hit.Point - _retopoStroke[^1]).Length > weld * 0.5f )
			_retopoStroke.Add( hit.Point );

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 3f;
		Gizmo.Draw.Color = RetopoOpenColor;

		for ( var i = 0; i + 1 < _retopoStroke.Count; i++ )
			Gizmo.Draw.Line( ToVector( _retopoStroke[i] ), ToVector( _retopoStroke[i + 1] ) );

		if ( Gizmo.IsLeftMouseDown )
			return;

		var stroke = _retopoStroke;
		_retopoStroke = null;

		// No edge to carry on from: a width a fixed share of the view, like the weld's reach.
		var width = session.PlanPolyBuild( stroke[0] ).Extends ? 0f : weld * 6f;
		MeshPolyStroke?.Invoke( stroke, width, weld );
	}

	// --- circle select ---------------------------------------------------------------------------

	/// <summary>
	/// Circle select, Blender's C: a brush that selects whatever it passes over while the mouse is
	/// held. Ctrl held erases instead. [ and ] change its size. The quickest way to pick an irregular
	/// patch — a neckline, the area round an eye — without a lasso's one-shot outline.
	/// </summary>
	public bool MeshCircle { get; set; }

	/// <summary>The brush's radius, in the camera's tangent plane (0.1 is a tenth of the screen's
	/// half-height at a 90 degree field of view), so it stays the same size on screen as you zoom.</summary>
	public float MeshCircleRadius { get; set; } = 0.06f;

	private bool _meshCirclePainting;

	private void MeshCircleFrame( MeshEditSession session )
	{
		var centre = CameraTangent( Gizmo.CurrentRay.Forward );

		if ( float.IsNaN( centre.x ) )
			return;

		DrawMeshCircle( centre );

		if ( Gizmo.WasLeftMousePressed && !Gizmo.HasHovered )
			_meshCirclePainting = true;

		if ( !Gizmo.IsLeftMouseDown )
		{
			_meshCirclePainting = false;
			return;
		}

		if ( !_meshCirclePainting )
			return;

		var how = Editor.Application.IsKeyDown( KeyCode.Control ) ? MeshEditSession.Combine.Remove : MeshEditSession.Combine.Add;
		var r2 = MeshCircleRadius * MeshCircleRadius;
		var before = session.SelectionRevision;

		SelectInRegion( session, t => (t - centre).LengthSquared <= r2, how );

		if ( session.SelectionRevision != before )
			MeshEditChanged?.Invoke();
	}

	private void DrawMeshCircle( Vector2 centre )
	{
		var r = _camera.WorldRotation;
		var eye = _camera.WorldPosition;
		var depth = MathF.Max( _camera.ZNear * 4f, 1f );

		Vector3 At( float angle ) => eye + (r.Forward
			+ r.Right * (centre.x + MathF.Cos( angle ) * MeshCircleRadius)
			+ r.Up * (centre.y + MathF.Sin( angle ) * MeshCircleRadius)) * depth;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.Color = new Color( 0.35f, 0.62f, 1f, 0.9f );

		const int Segments = 48;
		for ( var i = 0; i < Segments; i++ )
			Gizmo.Draw.Line( At( i * MathF.Tau / Segments ), At( (i + 1) * MathF.Tau / Segments ) );
	}

	/// <summary>Even-odd point in polygon, in the camera's tangent plane.</summary>
	private bool InsideLasso( Vector2 p )
	{
		var inside = false;

		for ( int i = 0, j = _meshLasso.Count - 1; i < _meshLasso.Count; j = i++ )
		{
			var a = _meshLasso[i];
			var b = _meshLasso[j];

			if ( (a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x )
				inside = !inside;
		}

		return inside;
	}

	private void DrawMeshLasso()
	{
		var r = _camera.WorldRotation;
		var eye = _camera.WorldPosition;
		var depth = MathF.Max( _camera.ZNear * 4f, 1f );

		Vector3 At( Vector2 q ) => eye + (r.Forward + r.Right * q.x + r.Up * q.y) * depth;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.Color = new Color( 0.35f, 0.62f, 1f, 0.9f );

		for ( var i = 0; i < _meshLasso.Count; i++ )
			Gizmo.Draw.Line( At( _meshLasso[i] ), At( _meshLasso[(i + 1) % _meshLasso.Count] ) );
	}

	/// <summary>The box, drawn a short way in front of the eye so it sits over everything.</summary>
	private void DrawMeshBox( Vector2 min, Vector2 max )
	{
		if ( float.IsNaN( min.x ) || float.IsNaN( max.x ) )
			return;

		var r = _camera.WorldRotation;
		var eye = _camera.WorldPosition;
		var depth = MathF.Max( _camera.ZNear * 4f, 1f );

		Vector3 At( float x, float y ) => eye + (r.Forward + r.Right * x + r.Up * y) * depth;

		var a = At( min.x, min.y );
		var b = At( max.x, min.y );
		var c = At( max.x, max.y );
		var d = At( min.x, max.y );

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.Color = new Color( 0.35f, 0.62f, 1f, 0.9f );
		Gizmo.Draw.Line( a, b );
		Gizmo.Draw.Line( b, c );
		Gizmo.Draw.Line( c, d );
		Gizmo.Draw.Line( d, a );
	}

	private bool StartMeshEditDrag( MeshEditSession session, Vector3 centre )
	{
		if ( _meshEditDragging )
			return true;

		if ( !session.BeginDrag() )
			return false;

		_meshEditDragging = true;
		_meshEditDragAnchor = centre;
		_meshEditDragDelta = Vector3.Zero;
		return true;
	}

	private void FinishMeshEditDrag( MeshEditSession session )
	{
		if ( !_meshEditDragging )
			return;

		_meshEditDragging = false;
		_meshEditDragDelta = Vector3.Zero;
		session.EndDrag( keep: true );
		MeshEditChanged?.Invoke();
	}


	/// <summary>
	/// The mesh's unique edges, held from one frame to the next. <see cref="PolyMesh.BuildEdgeFaces"/>
	/// builds a dictionary of lists over the whole mesh, and the overlay was calling it every frame —
	/// on a 4000-face mesh that is ~8000 keys and ~8000 lists allocated per frame, and it was doing it
	/// again for every frame of a box select. The edge list only changes when the topology does, so
	/// it is cached against <see cref="MeshEditSession.TopologyRevision"/>, which a drag does not bump.
	/// </summary>
	private EdgeKey[] MeshEditEdges( MeshEditSession session )
	{
		if ( _meshEdgeCache is not null && _meshEdgeCacheOwner == session && _meshEdgeCacheRevision == session.TopologyRevision )
			return _meshEdgeCache;

		var keys = session.Mesh.BuildEdgeFaces().Keys;
		var edges = new EdgeKey[keys.Count];
		keys.CopyTo( edges, 0 );

		_meshEdgeCache = edges;
		_meshEdgeCacheOwner = session;
		_meshEdgeCacheRevision = session.TopologyRevision;
		return edges;
	}

	/// <summary>The new mesh's edges while retopologizing, each flagged open (one face) or not.
	/// Cached against the topology like <see cref="MeshEditEdges"/>.</summary>
	private (EdgeKey Key, bool Open)[] RetopoEdges( MeshEditSession session )
	{
		if ( _retopoEdgeCache is not null && _retopoEdgeCacheOwner == session && _retopoEdgeCacheRevision == session.TopologyRevision )
			return _retopoEdgeCache;

		var mesh = session.Mesh;
		var start = session.RetopoStart;
		var counts = new Dictionary<EdgeKey, int>();

		foreach ( var face in mesh.Faces )
		{
			var idx = face.Indices;
			if ( idx[0] < start )
				continue;

			for ( var i = 0; i < idx.Length; i++ )
			{
				var key = new EdgeKey( idx[i], idx[(i + 1) % idx.Length] );
				counts[key] = counts.TryGetValue( key, out var n ) ? n + 1 : 1;
			}
		}

		var edges = new (EdgeKey, bool)[counts.Count];
		var at = 0;
		foreach ( var pair in counts )
			edges[at++] = (pair.Key, pair.Value == 1);

		_retopoEdgeCache = edges;
		_retopoEdgeCacheOwner = session;
		_retopoEdgeCacheRevision = session.TopologyRevision;
		return edges;
	}

	private (EdgeKey Key, bool Open)[] _retopoEdgeCache;
	private MeshEditSession _retopoEdgeCacheOwner;
	private int _retopoEdgeCacheRevision = -1;

	/// <summary>While retopologizing, hovering and clicking only reach the new mesh — the sculpt
	/// is the surface you draw on, not something to pick.</summary>
	private static bool IsRetopoElement( MeshEditSession session, (EditElement Kind, int Index, EdgeKey Edge) element ) => element.Kind switch
	{
		EditElement.Vertex => element.Index >= session.RetopoStart,
		EditElement.Edge => element.Edge.A >= session.RetopoStart && element.Edge.B >= session.RetopoStart,
		_ => element.Index < session.Mesh.FaceCount && session.Mesh.Faces[element.Index].Indices[0] >= session.RetopoStart,
	};

	private EdgeKey[] _meshEdgeCache;
	private MeshEditSession _meshEdgeCacheOwner;
	private int _meshEdgeCacheRevision = -1;

	/// <summary>Wire, selection and hover. The wire is skipped on dense meshes, where drawing every
	/// edge every frame would cost more than it tells you.</summary>
	private void DrawMeshEditOverlay( MeshEditSession session )
	{
		var mesh = session.Mesh;
		var size = MathF.Max( mesh.BoundsDiagonal, 1e-3f );

		Gizmo.Draw.IgnoreDepth = MeshXray;

		// The pivot, once moved off the origin: a small cross with its up axis, so it is clear
		// what Spin will turn round.
		if ( session.Pivot.LengthSquared > 0f )
		{
			var at = ToVector( session.Pivot );
			var r = size * 0.02f;
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 2f;
			Gizmo.Draw.Color = MeshSelectedColor;
			Gizmo.Draw.Line( at - Vector3.Forward * r, at + Vector3.Forward * r );
			Gizmo.Draw.Line( at - Vector3.Left * r, at + Vector3.Left * r );
			Gizmo.Draw.Line( at - Vector3.Up * r * 3f, at + Vector3.Up * r * 3f );
			Gizmo.Draw.IgnoreDepth = MeshXray;
		}

		if ( session.IsRetopologizing )
		{
			// Only the new mesh is wired: the sculpt under it is what you are tracing, and on a
			// dense one its wire is a grey fog over exactly what you need to see. Open borders are
			// brighter — they are where the next strip grows from.
			Gizmo.Draw.LineThickness = 1.5f;

			foreach ( var (key, open) in RetopoEdges( session ) )
			{
				Gizmo.Draw.Color = open ? RetopoOpenColor : RetopoWireColor;
				Gizmo.Draw.Line( ToVector( mesh.Positions[key.A] ), ToVector( mesh.Positions[key.B] ) );
			}

			for ( var v = session.RetopoStart; v < mesh.VertexCount; v++ )
				DrawVertexDot( mesh.Positions[v], size * 0.003f, RetopoWireColor );
		}
		else if ( mesh.FaceCount <= 4000 )
		{
			Gizmo.Draw.LineThickness = 1f;
			Gizmo.Draw.Color = MeshWireColor;

			foreach ( var key in MeshEditEdges( session ) )
				Gizmo.Draw.Line( ToVector( mesh.Positions[key.A] ), ToVector( mesh.Positions[key.B] ) );
		}

		// Seams under the selection, so selecting an edge to unmark still reads as selected. Thicker
		// than the wire and drawn through the model: a seam on the far side is still a seam, and
		// hunting for one you cannot see is how a texture ends up cut in the wrong place.
		if ( session.Seams.Count > 0 )
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 3f;
			Gizmo.Draw.Color = MeshSeamColor;

			foreach ( var seam in session.Seams )
			{
				if ( seam.A >= mesh.VertexCount || seam.B >= mesh.VertexCount )
					continue;

				Gizmo.Draw.Line( ToVector( mesh.Positions[seam.A] ), ToVector( mesh.Positions[seam.B] ) );
			}
		}

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 2.5f;
		Gizmo.Draw.Color = MeshSelectedColor;

		foreach ( var f in session.SelectedFaces )
		{
			if ( f < 0 || f >= mesh.FaceCount )
				continue;

			DrawFaceOutline( mesh, mesh.Faces[f] );
		}

		foreach ( var e in session.SelectedEdges )
			Gizmo.Draw.Line( ToVector( mesh.Positions[e.A] ), ToVector( mesh.Positions[e.B] ) );

		foreach ( var v in session.SelectedVertices )
			DrawVertexDot( mesh.Positions[v], size * 0.006f, MeshSelectedColor );

		if ( _meshEditHover is not { } h )
			return;

		Gizmo.Draw.Color = MeshHoverColor;
		Gizmo.Draw.LineThickness = 2f;

		switch ( h.Kind )
		{
			case EditElement.Vertex:
				DrawVertexDot( mesh.Positions[h.Index], size * 0.008f, MeshHoverColor );
				break;
			case EditElement.Edge:
				Gizmo.Draw.Line( ToVector( mesh.Positions[h.Edge.A] ), ToVector( mesh.Positions[h.Edge.B] ) );
				break;
			case EditElement.Face:
				DrawFaceOutline( mesh, mesh.Faces[h.Index] );
				break;
		}
	}

	private static void DrawFaceOutline( PolyMesh mesh, Effigy.Face face )
	{
		var n = face.Indices.Length;
		for ( var i = 0; i < n; i++ )
			Gizmo.Draw.Line( ToVector( mesh.Positions[face.Indices[i]] ), ToVector( mesh.Positions[face.Indices[(i + 1) % n]] ) );
	}

	private static void DrawVertexDot( Vec3 p, float radius, Color color )
	{
		using ( Gizmo.Scope( "mesh-edit-vertex", new Transform( ToVector( p ) ) ) )
		{
			Gizmo.Draw.Color = color;
			Gizmo.Draw.SolidSphere( 0f, radius, 6, 6 );
		}
	}

	private static readonly Color MeshWireColor = new( 0.1f, 0.11f, 0.13f, 0.55f );

	/// <summary>Teal, the new mesh while retopologizing; the brighter one marks its open borders.</summary>
	private static readonly Color RetopoWireColor = new( 0.11f, 0.62f, 0.46f, 1f );
	private static readonly Color RetopoOpenColor = new( 0.62f, 0.88f, 0.8f, 1f );

	/// <summary>Orange, as every other "this is selected" in the tool.</summary>
	private static readonly Color MeshSelectedColor = new( 0.94f, 0.63f, 0.23f, 1f );

	private static readonly Color MeshHoverColor = new( 1f, 1f, 1f, 0.75f );

	/// <summary>Red, the colour a marked seam is in every tool that has them.</summary>
	private static readonly Color MeshSeamColor = new( 0.93f, 0.26f, 0.21f, 1f );
}
