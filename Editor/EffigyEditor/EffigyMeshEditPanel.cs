using System;
using System.Collections.Generic;
using Editor;
using Effigy;
using Sandbox;

namespace Marionette.EditorTools;

/// <summary>
/// The left dock while editing a mesh, in place of the CAD feature tree: the bodies in the scene,
/// a live check of the mesh, and the edit's own history.
///
/// THE FEATURE TREE WAS THE WRONG THING TO LOOK AT HERE. Origin, the three planes and a "Mesh edit
/// 1" row say nothing while you are pushing vertices about; what does is which body you are in,
/// whether the mesh has just grown a hole, and what the last few steps were. The tree comes back
/// the moment you leave, and still records the whole edit as one step.
/// </summary>
internal sealed class EffigyMeshEditPanel : Widget
{
	private readonly Widget _content;

	/// <summary>Run a named operation the same way a menu would, so errors land on the prompt.</summary>
	public Action<string, Action<MeshEditSession>> RunOp { get; set; }

	/// <summary>Undo back to (and including) the step at this index in the history.</summary>
	public Action<int> UndoTo { get; set; }

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

		Gap();
		Heading( "Mesh check" );

		var mesh = session.Mesh;
		var check = MeshValidator.Validate( mesh );

		var used = new HashSet<int>();
		foreach ( var face in mesh.Faces )
			used.UnionWith( face.Indices );
		var loose = mesh.VertexCount - used.Count;

		Row( "Vertices", $"{mesh.VertexCount:N0}", Theme.TextControl );
		Row( "Faces", $"{mesh.FaceCount:N0}", Theme.TextControl );
		Row( "Open edges", $"{check.BoundaryEdges:N0}", check.BoundaryEdges > 0 ? Theme.Yellow : Theme.Green );
		Row( "Non-manifold", $"{check.NonManifoldEdges:N0}", check.NonManifoldEdges > 0 ? Theme.Red : Theme.Green );
		Row( "Loose vertices", $"{loose:N0}", loose > 0 ? Theme.Yellow : Theme.Green );

		// Each problem comes with the one-click way to look at it or fix it.
		if ( check.BoundaryEdges > 0 || check.NonManifoldEdges > 0 )
			Action( "Show open and non-manifold edges", "Non-manifold", s => s.SelectNonManifold() );

		if ( loose > 0 )
			Action( $"Remove {loose:N0} loose vertices", "Delete loose", s => s.DeleteLoose() );

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
