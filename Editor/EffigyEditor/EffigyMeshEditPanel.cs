using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Effigy;
using Sandbox;

namespace Marionette.EditorTools;

/// <summary>
/// The left dock for the whole Model workspace, in place of the CAD feature tree: the bodies in
/// the scene, a live check of the mesh, and the history.
///
/// THE FEATURE TREE WAS THE WRONG THING TO LOOK AT HERE. Origin, the three planes and a "Mesh edit
/// 1" row say nothing while you are pushing vertices about; what does is which body you are in,
/// whether the mesh has just grown a hole, and what the last few steps were. The tree comes back
/// the moment you leave the workspace, and still records the whole edit as one step.
///
/// TWO FACES, ONE PANEL. While a mesh is being edited, <see cref="Refresh"/> reads the edit
/// session: the mesh under the cursor, the edit's own undo steps. In Object mode, with nothing
/// open, <see cref="RefreshObject"/> reads the studio instead: the bodies as built, the selected
/// one's check, and the features that built them. Same three headings either way, so leaving an
/// edit does not swap the dock for a different-looking thing — it was the reason the CAD tree
/// coming back between edits made Model still read as CAD.
/// </summary>
internal sealed class EffigyMeshEditPanel : Widget
{
	private readonly Widget _content;

	/// <summary>Run a named operation the same way a menu would, so errors land on the prompt.</summary>
	public Action<string, Action<MeshEditSession>> RunOp { get; set; }

	/// <summary>Undo back to (and including) the step at this index in the history.</summary>
	public Action<int> UndoTo { get; set; }

	/// <summary>Object mode: a body row was clicked. Selects it, the way the Parts list does.</summary>
	public Action<string> SelectBody { get; set; }

	/// <summary>Object mode: a body row was right-clicked. Opens the Object menu on it.</summary>
	public Action<string> BodyMenu { get; set; }

	/// <summary>Object mode: a history row was clicked. Opens that feature to edit.</summary>
	public Action<Feature> OpenFeature { get; set; }

	/// <summary>Object mode: run a named body-level tool, from a fix button under the check.</summary>
	public Action<string> RunTool { get; set; }

	public EffigyMeshEditPanel( Widget parent ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Margin = new Sandbox.UI.Margin( 0 );

		_content = new Widget( this ) { Layout = Layout.Column() };
		_content.Layout.Margin = new Sandbox.UI.Margin( 10, 6, 10, 6 );
		_content.Layout.Spacing = 3;

		Layout.Add( _content, 1 );
		Visible = false;
	}

	/// <summary>Rebuild from the session. Cheap enough to run on every change: a handful of rows.</summary>
	public void Refresh( MeshEditSession session, IReadOnlyList<Body> bodies, string editingBodyId )
	{
		_content.Layout.Clear( true );

		if ( session is null )
			return;

		Heading( "Scene" );

		foreach ( var body in bodies )
		{
			var editing = body.Id == editingBodyId;
			var faces = editing ? session.Mesh.FaceCount : body.Mesh?.FaceCount ?? 0;
			Row( body.Name, editing ? "editing" : $"{faces:N0} faces", editing ? Theme.Text : Theme.TextControl.WithAlpha( 0.7f ) );
		}

		if ( session.Separated.Count > 0 )
			Row( $"{session.Separated.Count} new piece(s)", "added when you leave", Theme.Blue );

		var selVerts = session.SelectedVertices.Count;
		var selFaces = session.SelectedFaces.Count;
		var selEdges = session.SelectedEdges.Count;

		if ( selVerts > 0 || selFaces > 0 || selEdges > 0 )
		{
			Gap();
			Heading( "Quick Tools" );

			if ( selVerts == 2 )
				Action( "Connect across face (J)", "Connect", s => s.ConnectVertices() );

			if ( selVerts >= 3 || selEdges > 0 )
			{
				Action( "LoopTools: Circle", "Circle", s => s.LoopCircle() );
				Action( "LoopTools: Space", "Space", s => s.LoopSpace() );
				Action( "Flatten to plane", "Flatten", s => s.FlattenFaces() );
			}

			if ( selFaces > 0 )
			{
				Action( "Triangulate (Ctrl+T)", "Triangulate", s => s.TriangulateFaces() );
				Action( "Poke faces (Alt+P)", "Poke", s => s.PokeFaces() );
				if ( selFaces > 1 )
					Action( "Tris to quads (Alt+J)", "Quads", s => s.TrisToQuads() );
			}

			Action( "Relax surface", "Relax", s => s.Relax() );
			Action( "Hide selected (H)", "Hide", s => s.Hide() );
			Action( "Hide everything else (Shift+H)", "Hide others", s => s.Hide( unselected: true ) );
		}

		if ( session.HasHiddenFaces )
		{
			Gap();
			Action( $"Show {session.HiddenFaces.Count} hidden faces (Alt+H)", "Unhide", s => s.Unhide() );
		}

		Gap();
		Heading( "History" );

		var history = session.UndoLabels;

		if ( history.Count == 0 )
		{
			Row( "Nothing yet", "", Theme.TextControl.WithAlpha( 0.5f ) );
		}
		else
		{
			// The latest ten, newest at the bottom like a log. A click undoes back to before it.
			for ( var i = Math.Max( 0, history.Count - 10 ); i < history.Count; i++ )
			{
				var index = i;
				var button = new Button( history[i] )
				{
					ToolTip = "Undo back to before this step",
					Clicked = () => UndoTo?.Invoke( index ),
				};
				button.SetStyles( "text-align: left; padding: 2px 6px;" );
				_content.Layout.Add( button );
			}
		}

		_content.Layout.AddStretchCell();
	}

	/// <summary>
	/// Rebuild for Object mode: no session, so everything comes from the studio.
	///
	/// The mesh check is not here: it is the badge in the viewport header (EffigyWindow.ModelChrome.cs),
	/// in both modes, so the dock is an outliner and a history and nothing else.
	/// </summary>
	public void RefreshObject( IReadOnlyList<Body> bodies, IReadOnlyList<string> selectedIds,
		IReadOnlyList<Feature> features, int rollback )
	{
		_content.Layout.Clear( true );

		bodies ??= Array.Empty<Body>();
		selectedIds ??= Array.Empty<string>();
		features ??= Array.Empty<Feature>();

		Heading( "Scene" );

		if ( bodies.Count == 0 )
		{
			Row( "Nothing yet", "", Theme.TextControl.WithAlpha( 0.5f ) );
			Note( "Add ▸ Cube puts a body here, or press Edit to start a mesh from nothing." );
		}

		foreach ( var body in bodies )
		{
			var selected = selectedIds.Contains( body.Id );
			var faces = body.Mesh?.FaceCount ?? 0;
			var tag = !body.Visible ? "hidden" : $"{faces:N0} faces";

			_content.Layout.Add( new SceneRow( _content, body.Name ?? body.Id, tag, selected, !body.Visible )
			{
				Clicked = () => SelectBody?.Invoke( body.Id ),
				MenuRequested = () => BodyMenu?.Invoke( body.Id ),
			} );
		}

		Gap();
		Heading( "History" );

		// Origin and the datum planes are the CAD tree's own furniture, not features, so this is
		// already only what was DONE — newest at the bottom, like the edit's own log.
		var steps = features;

		if ( steps.Count == 0 )
		{
			Row( "Nothing yet", "", Theme.TextControl.WithAlpha( 0.5f ) );
		}
		else
		{
			for ( var i = Math.Max( 0, steps.Count - 12 ); i < steps.Count; i++ )
			{
				var feature = steps[i];
				var index = i;
				var rolledBack = index >= rollback;
				var label = string.IsNullOrEmpty( feature.Name ) ? feature.TypeName : feature.Name;

				if ( feature.Error is not null )
					label += "  ✕";
				else if ( feature.Suppressed )
					label += "  (off)";

				var button = new Button( label )
				{
					ToolTip = feature.Error ?? (rolledBack ? "Rolled back — not running. Click to edit it" : "Click to edit this step"),
					Clicked = () => OpenFeature?.Invoke( feature ),
				};

				button.SetStyles( "text-align: left; padding: 2px 6px;"
					+ (feature.Error is not null ? " color: #e5625a;" : rolledBack || feature.Suppressed ? " opacity: 0.45;" : "") );

				_content.Layout.Add( button );
			}
		}

		_content.Layout.AddStretchCell();
	}

	private void Tool( string label, string tool )
	{
		var button = new Button( label ) { Clicked = () => RunTool?.Invoke( tool ) };
		_content.Layout.Add( button );
	}

	private void Note( string text )
	{
		var label = new Editor.Label( text ) { Color = Theme.TextControl.WithAlpha( 0.6f ), WordWrap = true };
		_content.Layout.Add( label );
	}

	/// <summary>A body in the Scene list: name, a tag on the right, lit when selected.</summary>
	private sealed class SceneRow : Widget
	{
		private readonly string _name, _tag;
		private readonly bool _selected, _hidden;

		public Action Clicked;
		public Action MenuRequested;

		public SceneRow( Widget parent, string name, string tag, bool selected, bool hidden ) : base( parent )
		{
			_name = name;
			_tag = tag;
			_selected = selected;
			_hidden = hidden;

			FixedHeight = 22f;
			Cursor = CursorShape.Finger;
		}

		protected override void OnPaint()
		{
			var rect = new Rect( 0f, 0f, Width, Height );

			if ( _selected )
			{
				Paint.ClearPen();
				Paint.SetBrush( Theme.Blue.WithAlpha( 0.18f ) );
				Paint.DrawRect( rect, 3f );
			}

			var color = _hidden ? Theme.TextControl.WithAlpha( 0.4f ) : _selected ? Theme.Text : Theme.TextControl;

			Paint.ClearBrush();
			Paint.SetPen( color );
			Paint.SetDefaultFont( 9 );
			Paint.DrawText( rect.Shrink( 6f, 0f, 0f, 0f ), _name, TextFlag.LeftCenter );

			Paint.SetPen( color.WithAlpha( 0.7f ) );
			Paint.SetDefaultFont( 8 );
			Paint.DrawText( rect.Shrink( 0f, 0f, 6f, 0f ), _tag, TextFlag.RightCenter );
		}

		protected override void OnMousePress( MouseEvent e )
		{
			base.OnMousePress( e );

			if ( e.LeftMouseButton )
				Clicked?.Invoke();
			else if ( e.RightMouseButton )
				MenuRequested?.Invoke();
		}
	}

	private void Heading( string text )
	{
		var label = new Editor.Label( text.ToUpperInvariant() ) { Color = Theme.TextControl.WithAlpha( 0.6f ) };
		label.SetStyles( "font-size: 10px; font-weight: 600; letter-spacing: 1px;" );
		_content.Layout.Add( label );
	}

	private void Row( string name, string value, Color color )
	{
		var row = new Widget( _content ) { Layout = Layout.Row() };
		row.Layout.Add( new Editor.Label( name ) { Color = color }, 1 );
		row.Layout.Add( new Editor.Label( value ) { Color = color.WithAlpha( 0.75f ) } );
		_content.Layout.Add( row );
	}

	private void Action( string label, string op, Action<MeshEditSession> run )
	{
		var button = new Button( label ) { Clicked = () => RunOp?.Invoke( op, run ) };
		_content.Layout.Add( button );
	}

	private void Gap() => _content.Layout.AddSpacingCell( 8 );
}
