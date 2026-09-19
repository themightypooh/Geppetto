using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// The UV editor: the texture square with every face of the mesh being edited laid on it, its
/// islands pickable and draggable, and a row of the things you do to them.
///
/// WHAT IT SHOWS IS THE EDIT SESSION'S MESH, and everything it does goes through the session as
/// a named operation, so a UV move is an undo step like an extrude and the viewport's overlay
/// keeps up. It is blank outside Edit mode: the UVs on the model are the feature's business.
///
/// STRETCH IS A COLOUR: turn it on and each face is tinted by how much texture it gets against
/// the mesh's average — red where the texels are stretched thin, blue where they are squashed —
/// which is the one thing an unwrap gets wrong that you cannot see on the model until you paint.
/// </summary>
internal sealed class EffigyUVPanel : Widget
{
	/// <summary>Run a named operation the way a menu would, so errors land on the prompt.</summary>
	public Action<string, Action<MeshEditSession>> RunOp { get; set; }

	/// <summary>Something changed the selection or the mesh: the viewport should redraw.</summary>
	public Action Changed { get; set; }

	private readonly UVCanvas _canvas;
	private MeshEditSession _session;

	public EffigyUVPanel( Widget parent ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Margin = new Sandbox.UI.Margin( 6 );
		Layout.Spacing = 4;

		var tools = new Widget( this ) { Layout = Layout.Row() };
		tools.Layout.Spacing = 3;
		tools.Layout.Add( Tool( "Island", "Select the whole island of every selected face", "Island", s => s.SelectUVIslands() ) );
		tools.Layout.Add( Tool( "Turn", "Turn the selected islands a quarter", "Rotate UVs", s => s.RotateUVs( 90f ) ) );
		tools.Layout.Add( Tool( "Flip U", "Mirror the selected islands left to right", "Flip UVs", s => s.ScaleUVs( new Vec2( -1f, 1f ) ) ) );
		tools.Layout.Add( Tool( "Flip V", "Mirror the selected islands top to bottom", "Flip UVs", s => s.ScaleUVs( new Vec2( 1f, -1f ) ) ) );
		tools.Layout.Add( Tool( "Bigger", "Size the selected islands up by a quarter", "Scale UVs", s => s.ScaleUVs( new Vec2( 1.25f, 1.25f ) ) ) );
		tools.Layout.Add( Tool( "Smaller", "Size the selected islands down by a fifth", "Scale UVs", s => s.ScaleUVs( new Vec2( 0.8f, 0.8f ) ) ) );
		tools.Layout.Add( Tool( "Pack", "Lay every island into the square, all at one scale, none overlapping", "Pack UVs", s => s.PackUVs( 0.01f ) ) );
		var stretch = new Button( "Stretch" ) { ToolTip = "Tint each face by how much texture it gets: red is stretched, blue squashed" };
		stretch.Clicked = () => { _canvas.ShowStretch = !_canvas.ShowStretch; stretch.Text = _canvas.ShowStretch ? "Stretch ●" : "Stretch"; _canvas.Update(); };
		tools.Layout.Add( stretch );
		tools.Layout.AddStretchCell();
		Layout.Add( tools );

		_canvas = new UVCanvas( this )
		{
			Pick = PickIsland,
			Dragged = DragIslands,
		};
		Layout.Add( _canvas, 1 );
	}

	private Button Tool( string label, string tip, string op, Action<MeshEditSession> run )
	{
		return new Button( label ) { ToolTip = tip, Clicked = () => { if ( _session is not null ) RunOp?.Invoke( op, run ); } };
	}

	/// <summary>Show this session's UVs, or nothing.</summary>
	public void Bind( MeshEditSession session )
	{
		_session = session;
		_canvas.Session = session;
		_canvas.Update();
	}

	/// <summary>The mesh or selection changed: redraw.</summary>
	public void Refresh() => _canvas.Update();

	private void PickIsland( int face, bool add )
	{
		if ( _session is null )
			return;

		RunOp?.Invoke( "Island", s =>
		{
			if ( s.Mode != EditElement.Face )
				s.SetMode( EditElement.Face );

			if ( face < 0 )
			{
				if ( !add )
					s.ClearSelection();
				return;
			}

			s.SelectFace( face, add ? MeshEditSession.Combine.Toggle : MeshEditSession.Combine.Replace );
			if ( s.SelectedFaces.Count > 0 )
				s.SelectUVIslands();
		} );
		Changed?.Invoke();
	}

	/// <summary>A drag on the square moves the selected islands: previewed while the mouse is
	/// down, one step when it comes up.</summary>
	private void DragIslands( Vec2 delta, bool done )
	{
		if ( _session is null || _session.SelectedFaces.Count == 0 )
			return;

		try
		{
			_session.Preview( "Move UVs", s => s.MoveUVs( delta ) );
			if ( done )
				_session.Accept();
		}
		catch ( InvalidOperationException )
		{
			// Nothing selected, or a preview refused: the drag just does nothing.
		}

		Changed?.Invoke();
	}

	/// <summary>The square itself.</summary>
	private sealed class UVCanvas : Widget
	{
		public MeshEditSession Session;
		public bool ShowStretch;
		public Action<int, bool> Pick;
		public Action<Vec2, bool> Dragged;

		private Vector2 _pressAt;
		private bool _dragging;
		private bool _moved;

		public UVCanvas( Widget parent ) : base( parent )
		{
			MinimumSize = new Vector2( 160, 160 );
		}

		private Rect Square()
		{
			var r = LocalRect.Shrink( 8 );
			var side = MathF.Min( r.Width, r.Height );
			return new Rect( r.Left + (r.Width - side) * 0.5f, r.Top + (r.Height - side) * 0.5f, side, side );
		}

		private Vector2 ToScreen( Vec2 uv, Rect sq ) => new( sq.Left + uv.x * sq.Width, sq.Top + (1f - uv.y) * sq.Height );
		private Vec2 ToUV( Vector2 p, Rect sq ) => new( (p.x - sq.Left) / sq.Width, 1f - (p.y - sq.Top) / sq.Height );

		protected override void OnPaint()
		{
			var sq = Square();
			Paint.ClearPen();
			Paint.SetBrush( Theme.ControlBackground );
			Paint.DrawRect( LocalRect );
			Paint.SetBrush( Color.Black.WithAlpha( 0.55f ) );
			Paint.DrawRect( sq );

			// The quarter grid, faint.
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.12f ), 1f );
			for ( var i = 1; i < 4; i++ )
			{
				var t = i / 4f;
				Paint.DrawLine( new Vector2( sq.Left + sq.Width * t, sq.Top ), new Vector2( sq.Left + sq.Width * t, sq.Bottom ) );
				Paint.DrawLine( new Vector2( sq.Left, sq.Top + sq.Height * t ), new Vector2( sq.Right, sq.Top + sq.Height * t ) );
			}

			Paint.SetPen( Theme.TextControl.WithAlpha( 0.5f ), 1f );
			Paint.ClearBrush();
			Paint.DrawRect( sq );

			if ( Session is null )
			{
				Paint.SetPen( Theme.TextControl.WithAlpha( 0.6f ) );
				Paint.SetDefaultFont();
				Paint.DrawText( sq, "Edit a part to see its UVs here", TextFlag.Center );
				return;
			}

			var mesh = Session.Mesh;
			var stretch = ShowStretch ? UVIslands.Stretch( mesh ) : null;
			var selected = Session.SelectedFaces;

			for ( var f = 0; f < mesh.FaceCount; f++ )
			{
				var face = mesh.Faces[f];
				if ( face.UVs is null || face.UVs.Length < 3 )
					continue;

				var pts = new Vector2[face.UVs.Length];
				for ( var i = 0; i < pts.Length; i++ )
					pts[i] = ToScreen( face.UVs[i], sq );

				var picked = selected.Contains( f );
				if ( stretch is not null )
				{
					// Below one the texels are stretched (red); above, squashed (blue).
					var s = stretch[f];
					var tint = s < 1f ? Color.Lerp( Theme.TextControl, Theme.Red, Math.Clamp( 1f - s, 0f, 1f ) ) : Color.Lerp( Theme.TextControl, Theme.Blue, Math.Clamp( (s - 1f) * 0.5f, 0f, 1f ) );
					Paint.SetBrush( tint.WithAlpha( picked ? 0.75f : 0.45f ) );
				}
				else
				{
					Paint.SetBrush( (picked ? Theme.Primary : Theme.TextControl).WithAlpha( picked ? 0.45f : 0.12f ) );
				}

				Paint.SetPen( (picked ? Theme.Primary : Theme.TextControl).WithAlpha( picked ? 1f : 0.35f ), picked ? 1.5f : 1f );
				Paint.DrawPolygon( pts );
			}
		}

		protected override void OnMousePress( MouseEvent e )
		{
			if ( !e.LeftMouseButton )
				return;

			_pressAt = e.LocalPosition;
			_dragging = true;
			_moved = false;
			e.Accepted = true;
		}

		protected override void OnMouseMove( MouseEvent e )
		{
			if ( !_dragging || Session is null )
				return;

			var sq = Square();
			var delta = ToUV( e.LocalPosition, sq ) - ToUV( _pressAt, sq );
			if ( !_moved && delta.Length < 0.004f )
				return;

			// A drag on a face that is not selected picks it first, then moves it.
			if ( !_moved && FaceAt( _pressAt ) is var under && under >= 0 && !Session.SelectedFaces.Contains( under ) )
				Pick?.Invoke( under, e.HasShift );

			_moved = true;
			Dragged?.Invoke( delta, false );
			Update();
		}

		protected override void OnMouseReleased( MouseEvent e )
		{
			if ( !_dragging )
				return;

			_dragging = false;

			if ( _moved )
			{
				var sq = Square();
				Dragged?.Invoke( ToUV( e.LocalPosition, sq ) - ToUV( _pressAt, sq ), true );
			}
			else
			{
				Pick?.Invoke( FaceAt( e.LocalPosition ), e.HasShift );
			}

			Update();
		}

		/// <summary>The face under a point on the square, or -1. Later faces win, as they draw on top.</summary>
		private int FaceAt( Vector2 p )
		{
			if ( Session is null )
				return -1;

			var sq = Square();
			var uv = ToUV( p, sq );
			var mesh = Session.Mesh;
			for ( var f = mesh.FaceCount - 1; f >= 0; f-- )
			{
				var uvs = mesh.Faces[f].UVs;
				if ( uvs is null || uvs.Length < 3 )
					continue;

				var inside = false;
				for ( int i = 0, j = uvs.Length - 1; i < uvs.Length; j = i++ )
				{
					if ( (uvs[i].y > uv.y) != (uvs[j].y > uv.y)
						&& uv.x < (uvs[j].x - uvs[i].x) * (uv.y - uvs[i].y) / (uvs[j].y - uvs[i].y) + uvs[i].x )
						inside = !inside;
				}

				if ( inside )
					return f;
			}

			return -1;
		}
	}
}
