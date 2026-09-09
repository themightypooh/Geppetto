using Editor;
using Effigy;
using Sandbox;
using System;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The document's named values: name, expression, evaluated result.
///
/// A dock with a list and three columns is enough. No dependency graph. Changing a value dirties
/// the whole tree because any feature might refer to it.
/// </summary>
internal sealed class EffigyVariablesPanel : Widget
{
	private PartStudio _studio;
	private readonly Widget _list;

	public Action Changing { get; set; }
	public Action Changed { get; set; }

	public EffigyVariablesPanel( Widget parent, PartStudio studio ) : base( parent )
	{
		_studio = studio;
		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 6;

		var header = Layout.AddRow();
		header.Add( new Editor.Label( "Variables" ) { Color = Theme.TextControl } );
		header.AddStretchCell();

		var add = new Button( "Add", this );
		add.Clicked = AddVariable;
		header.Add( add );

		_list = new Widget( this );
		_list.Layout = Layout.Column();
		_list.Layout.Spacing = 4;
		Layout.Add( _list, 1 );

		Rebuild();
	}

	public void Bind( PartStudio studio )
	{
		_studio = studio;
		Rebuild();
	}

	public void Rebuild()
	{
		if ( _list is null || !_list.IsValid() )
			return;

		_list.Layout.Clear( true );

		if ( _studio is null || _studio.Variables.Count == 0 )
		{
			_list.Layout.Add( new Editor.Label( "No variables. Add one, then type #name in a dimension." )
			{
				Color = Theme.TextControl.WithAlpha( 0.55f ),
			} );
			return;
		}

		VariableResolver.EvaluateAll( _studio.Variables );

		foreach ( var variable in _studio.Variables.ToList() )
		{
			var row = _list.Layout.AddRow();
			row.Spacing = 6;

			var name = new LineEdit( variable.Name ?? "", _list );
			name.ToolTip = "The name you type after # in a dimension.";
			name.TextEdited += text => Rename( variable, text );
			row.Add( name, 1 );

			var expr = new LineEdit( variable.Expr ?? "", _list );
			expr.ToolTip = "A number or an expression. Other variables are #name.";
			expr.TextEdited += text => SetExpr( variable, text );
			row.Add( expr, 2 );

			row.Add( new Editor.Label( $"= {Expression.Format( variable.Value )}" )
			{
				Color = Theme.TextControl.WithAlpha( 0.55f ),
			} );

			var remove = new Button( "×", _list );
			remove.Clicked = () => Remove( variable );
			row.Add( remove );
		}
	}

	private void AddVariable()
	{
		if ( _studio is null )
			return;

		Changing?.Invoke();

		var n = 1;
		var name = "thickness";

		while ( _studio.Variables.Any( v => string.Equals( v.Name, name, StringComparison.OrdinalIgnoreCase ) ) )
			name = $"var{n++}";

		_studio.SetVariable( name, "1" );
		Changed?.Invoke();
		Rebuild();
	}

	private void Rename( StudioVariable variable, string text )
	{
		if ( _studio is null || variable is null )
			return;

		if ( !VariableResolver.IsLegalName( text ) )
			return;

		Changing?.Invoke();
		variable.Name = text;
		_studio.MarkAllDirty();
		Changed?.Invoke();
	}

	private void SetExpr( StudioVariable variable, string text )
	{
		if ( _studio is null || variable is null )
			return;

		Changing?.Invoke();
		variable.Expr = text ?? "";
		_studio.MarkAllDirty();
		Changed?.Invoke();
	}

	private void Remove( StudioVariable variable )
	{
		if ( _studio is null || variable is null )
			return;

		Changing?.Invoke();
		_studio.RemoveVariable( variable.Name );
		Changed?.Invoke();
		Rebuild();
	}
}
