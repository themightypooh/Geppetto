using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
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
	private readonly List<(StudioVariable Variable, LineEdit Name, LineEdit Expr, Editor.Label Value)> _rows = new();

	public Action Changing { get; set; }
	public Action Changed { get; set; }

	/// <summary>A rename or expression the panel would not take, as a sentence. The window shows it
	/// as a refusal; the panel never drops an edit without saying so.</summary>
	public Action<string> Refused { get; set; }

	/// <summary>Something the panel did that the user should know about but did not refuse - a
	/// rename that rewrote dimensions, a removal that left dimensions pointing at nothing.</summary>
	public Action<string> Noted { get; set; }

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

	public void Rebuild() => Rebuild( force: false );

	/// <param name="force">Rebuild the rows even mid-edit - a rename rewrote other rows' text.</param>
	private void Rebuild( bool force )
	{
		if ( _list is null || !_list.IsValid() )
			return;

		// THE WINDOW REBUILDS THIS PANEL ON EVERY CHANGE, including the keystroke still being typed.
		// Throwing the rows away then takes the box out from under the caret and the second
		// character goes nowhere. While a box here has focus and the table is the same table, only
		// the "= value" labels are refreshed.
		if ( !force && _studio is not null
			&& _rows.Any( r => ( r.Name.IsValid() && r.Name.IsFocused ) || ( r.Expr.IsValid() && r.Expr.IsFocused ) )
			&& _rows.Select( r => r.Variable ).SequenceEqual( _studio.Variables ) )
		{
			VariableResolver.EvaluateAll( _studio.Variables );

			foreach ( var row in _rows )
			{
				if ( row.Value.IsValid() )
					row.Value.Text = ValueText( row.Variable );
			}

			return;
		}

		_list.Layout.Clear( true );
		_rows.Clear();

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

			// RENAME COMMITS ON ENTER OR FOCUS LOSS, not per keystroke. Every keystroke of "wall" on
			// the way from "thickness" would otherwise be a rename - "w", "wa" - and the empty box in
			// between is an illegal name. EditingFinished covers both Enter and clicking away.
			var name = new LineEdit( variable.Name ?? "", _list );
			name.ToolTip = "The name you type after # in a dimension.";
			name.EditingFinished += () => Rename( variable, name );
			row.Add( name, 1 );

			var value = new Editor.Label( ValueText( variable ) )
			{
				Color = Theme.TextControl.WithAlpha( 0.55f ),
			};

			// The expression applies live so the model follows the typing; what is wrong with it is
			// said once the user is done typing, not on every half-finished "1/".
			var expr = new LineEdit( variable.Expr ?? "", _list );
			expr.ToolTip = "A number or an expression. Other variables are #name.";
			expr.TextEdited += text => SetExpr( variable, text, value );
			expr.EditingFinished += () => ExplainExpr( variable );
			row.Add( expr, 2 );

			row.Add( value );
			_rows.Add( (variable, name, expr, value) );

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

	private string ValueText( StudioVariable variable )
	{
		if ( _studio is null || variable is null )
			return "";

		var why = VariableResolver.Explain( _studio.Variables, variable.Expr, variable.Name );

		return why is null
			? $"= {Expression.Format( variable.Value )}"
			: $"= {Expression.Format( variable.Value )} (held)";
	}

	private void Rename( StudioVariable variable, LineEdit box )
	{
		if ( _studio is null || variable is null || box is null || !box.IsValid() )
			return;

		var text = ( box.Text ?? "" ).Trim();
		var old = variable.Name ?? "";

		// Unchanged is not a refusal - focus leaving the box without an edit lands here too.
		if ( text == old )
			return;

		if ( !VariableResolver.IsLegalName( text ) )
		{
			box.Text = old;
			Refused?.Invoke( $"Can't rename '#{old}' to '{text}': a name starts with a letter or _ and has only letters, digits and _. Kept '#{old}'." );
			return;
		}

		var clash = _studio.Variables.FirstOrDefault( v => v != variable
			&& string.Equals( v.Name, text, StringComparison.OrdinalIgnoreCase ) );

		if ( clash is not null )
		{
			box.Text = old;
			Refused?.Invoke( $"Can't rename '#{old}' to '#{text}': '#{clash.Name}' already exists (names ignore case). Pick another name." );
			return;
		}

		Changing?.Invoke();
		variable.Name = text;

		// Everything that said #old now says #new - other variables and every dimension - so the
		// rename moves nothing and freezes nothing.
		var rewritten = 0;

		foreach ( var other in _studio.Variables )
		{
			if ( other == variable || string.IsNullOrWhiteSpace( other.Expr ) )
				continue;

			var expr = VariableResolver.RenameReferences( other.Expr, old, text );

			if ( expr == other.Expr )
				continue;

			other.Expr = expr;
			rewritten++;
		}

		foreach ( var feature in _studio.Features )
			rewritten += feature?.RenameVariableReferences( old, text ) ?? 0;

		_studio.MarkAllDirty();
		Changed?.Invoke();

		if ( rewritten > 0 )
			Noted?.Invoke( $"Renamed '#{old}' to '#{text}' and updated {rewritten} expression{( rewritten == 1 ? "" : "s" )} that used it." );

		Rebuild( force: true );
	}

	private void SetExpr( StudioVariable variable, string text, Editor.Label value )
	{
		if ( _studio is null || variable is null )
			return;

		Changing?.Invoke();
		variable.Expr = text ?? "";
		_studio.MarkAllDirty();
		VariableResolver.EvaluateAll( _studio.Variables );
		Changed?.Invoke();

		if ( value is not null && value.IsValid() )
			value.Text = ValueText( variable );
	}

	private void ExplainExpr( StudioVariable variable )
	{
		if ( _studio is null || variable is null )
			return;

		var why = VariableResolver.Explain( _studio.Variables, variable.Expr, variable.Name );

		if ( why is null )
			return;

		Refused?.Invoke( $"Can't evaluate '#{variable.Name}': {why}. It keeps its last good value, {Expression.Format( variable.Value )}, until you fix it." );
	}

	private void Remove( StudioVariable variable )
	{
		if ( _studio is null || variable is null )
			return;

		var name = variable.Name ?? "";

		// Removing is allowed - it is the user's table - but the dimensions still pointing at it
		// hold their last value from here on, and that is said by name.
		var users = new List<string>();

		foreach ( var feature in _studio.Features )
		{
			if ( feature is null )
				continue;

			foreach ( var label in feature.ParametersReferencing( name ) )
				users.Add( $"{feature.Name} › {label}" );
		}

		foreach ( var other in _studio.Variables )
		{
			if ( other != variable && VariableResolver.ReferencedNames( other.Expr )
				.Exists( n => string.Equals( n, name, StringComparison.OrdinalIgnoreCase ) ) )
				users.Add( $"#{other.Name}" );
		}

		Changing?.Invoke();
		_studio.RemoveVariable( name );
		Changed?.Invoke();
		Rebuild();

		if ( users.Count > 0 )
		{
			var list = string.Join( ", ", users.Take( 4 ) ) + ( users.Count > 4 ? $" and {users.Count - 4} more" : "" );
			Noted?.Invoke( $"Removed '#{name}'. Still using it, now held at their last value: {list}. Retype those or add '#{name}' back." );
		}
	}
}
