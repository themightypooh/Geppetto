using Editor;
using Marionette;
using Sandbox;
using System;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// The Constraints tab - IK and Limit constraints on the RigDocument (.ctrlrig), matching the
/// reference video's "[IK] IK 0" / "[Limit] limitrot_hand" entries: Name/TargetBone/Weight/
/// Enabled plus type-specific fields, an On/Off toggle, reorder, and Delete.
///
/// Constraints live on the rig, not the clip, so editing here affects every .riganim authored
/// against this .ctrlrig - the same reason RigDocument is its own asset in the first place.
///
/// This tab is full CRUD - add/edit/reorder/enable/delete both constraint types, all of it saved
/// to the .ctrlrig - and both types now actually solve, in RigConstraintSolver:
///
///   - Limit clamps a bone's parent-space rotation, both while dragging and while scrubbing.
///   - IK is a closed-form two-bone solve (law of cosines), run when the constraint's Target Bone
///     is dragged. Chains longer than two bones would need FABRIK or CCD; the arm/leg case has an
///     exact answer and doesn't benefit from iterating.
///
/// Both act at author time and bake into ordinary FK keyframes - see RigConstraintSolver for why
/// that trade was made over live per-frame constraint evaluation.
/// </summary>
internal sealed class RigConstraintsPanel : Widget
{
	private RigDocument _rig;
	private readonly Widget _list;

	public Action Edited { get; set; }

	public RigConstraintsPanel( Widget parent ) : base( parent )
	{
		Name = "Constraints";
		WindowTitle = "Constraints";
		SetWindowIcon( "link" );

		Layout = Layout.Column();

		Layout.Add( RigHelpBox.Create( this,
			"A list of IK and Limit constraints, saved to this clip's Control Rig (.ctrlrig) asset " +
			"rather than the clip itself - every clip that points at the same rig shares them.",
			new[]
			{
				RigHelpBox.S( "IK Constraint",
					"Solves a chain of bones (starting at Target Bone, going up Chain Length " +
					"parents) to reach toward a target, bending through Pole Direction, instead of " +
					"you rotating each joint by hand. Use when an arm, leg, or tail needs to reach " +
					"naturally." ),

				RigHelpBox.S( "Limit Constraint",
					"Clamps a bone's rotation to stay between Min Angles and Max Angles. Use when " +
					"a joint shouldn't be able to bend past a certain point (an elbow that " +
					"shouldn't bend backward, for example)." ),

				RigHelpBox.S( "How to use it",
					"Click Add Constraint, pick a type, type the bone's exact name into Target " +
					"Bone, fill in its fields, then use the On/Off toggle, the down-arrow to " +
					"reorder, or Delete on the row under each one." ),

				RigHelpBox.S( "IK: drag the end, not the chain",
					"An IK constraint's Target Bone is the END of the chain - the hand, not the " +
					"shoulder. Drag that bone and the two bones above it rotate to follow. Switch " +
					"the toolbar to Move mode for IK; positioning the hand is the whole point. " +
					"Pole Direction decides which way the elbow or knee points." ),

				RigHelpBox.S( "Constraints bake into keyframes",
					"Posing through a constraint writes ordinary keyframes for every bone it " +
					"moved, so clips play back identically everywhere with no solver involved. " +
					"The trade: changing a constraint later does not retroactively change poses " +
					"already authored - re-drag the bone to solve it again." ),
			} ) );

		var bar = Layout.AddRow();
		bar.Margin = 4;
		bar.Add( new Editor.Label( "Constraints" ), 1 );
		bar.Add( new Button( "Add Constraint", "add" ) { Clicked = OpenAddMenu } );

		var scroll = Layout.Add( new ScrollArea( this ), 1 );
		scroll.VerticalScrollbarMode = ScrollbarMode.Auto;
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;

		_list = new Widget( this ) { Layout = Layout.Column() };
		_list.Layout.Margin = 4;
		_list.Layout.Spacing = 4;
		scroll.Canvas = _list;

		Rebuild();
	}

	public void SetRig( RigDocument rig )
	{
		_rig = rig;
		Rebuild();
	}

	private void OpenAddMenu()
	{
		if ( _rig is null )
			return;

		var menu = new Menu( this );
		menu.AddOption( "IK Constraint", "swap_calls", () => Add( new IkConstraint { Name = UniqueName( "IK" ) } ) );
		menu.AddOption( "Limit Constraint", "block", () => Add( new LimitConstraint { Name = UniqueName( "Limit" ) } ) );
		menu.OpenAtCursor();
	}

	private string UniqueName( string prefix )
	{
		var index = 0;
		var existing = _rig.IkConstraints.Select( c => c.Name ).Concat( _rig.LimitConstraints.Select( c => c.Name ) ).ToHashSet();

		while ( existing.Contains( $"{prefix} {index}" ) )
			index++;

		return $"{prefix} {index}";
	}

	private void Add( IkConstraint ik )
	{
		_rig.IkConstraints.Add( ik );
		Rebuild();
		Edited?.Invoke();
	}

	private void Add( LimitConstraint limit )
	{
		_rig.LimitConstraints.Add( limit );
		Rebuild();
		Edited?.Invoke();
	}

	public void Rebuild()
	{
		_list.Layout.Clear( true );

		if ( _rig is null )
		{
			_list.Layout.Add( new Editor.Label( "Open a Rig Animation asset with a Rig Asset Path set to edit its constraints." ) { Enabled = false } );
			return;
		}

		foreach ( var ik in _rig.IkConstraints.ToList() )
			_list.Layout.Add( BuildIk( ik ) );

		foreach ( var limit in _rig.LimitConstraints.ToList() )
			_list.Layout.Add( BuildLimit( limit ) );

		if ( _rig.IkConstraints.Count == 0 && _rig.LimitConstraints.Count == 0 )
			_list.Layout.Add( new Editor.Label( "No constraints yet - Add Constraint above" ) { Enabled = false } );
	}

	private Widget Section( string badge, Color badgeColor, string name, out Layout body )
	{
		var panel = new Widget( _list ) { Layout = Layout.Column() };
		panel.Layout.Margin = 6;
		panel.Layout.Spacing = 4;

		var header = panel.Layout.AddRow();
		header.Spacing = 6;

		var chip = new Editor.Label( badge ) { Color = badgeColor };
		chip.SetStyles( "font-weight: bold;" );
		header.Add( chip );
		header.Add( new Editor.Label( name ) { Enabled = false }, 1 );

		body = panel.Layout.AddColumn();

		return panel;
	}

	private Widget BuildIk( IkConstraint ik )
	{
		var panel = Section( "[IK]", Theme.Blue, ik.Name, out var body );

		var serialized = EditorTypeLibrary.GetSerializedObject( ik );
		serialized.OnPropertyChanged += _ => Edited?.Invoke();

		var sheet = new ControlSheet();
		sheet.AddObject( serialized );
		body.Add( sheet );

		body.Add( ToggleRow(
			() => ik.Enabled, v => { ik.Enabled = v; Edited?.Invoke(); },
			() => Reorder( _rig.IkConstraints, ik ),
			() =>
			{
				_rig.IkConstraints.Remove( ik );
				Rebuild();
				Edited?.Invoke();
			},
			resetHandle: () => { ik.PoleDirection = Vector3.Up; Rebuild(); Edited?.Invoke(); } ) );

		return panel;
	}

	private Widget BuildLimit( LimitConstraint limit )
	{
		var panel = Section( "[Limit]", Theme.Yellow, limit.Name, out var body );

		var serialized = EditorTypeLibrary.GetSerializedObject( limit );
		serialized.OnPropertyChanged += _ => Edited?.Invoke();

		var sheet = new ControlSheet();
		sheet.AddObject( serialized );
		body.Add( sheet );

		body.Add( ToggleRow(
			() => limit.Enabled, v => { limit.Enabled = v; Edited?.Invoke(); },
			() => Reorder( _rig.LimitConstraints, limit ),
			() =>
			{
				_rig.LimitConstraints.Remove( limit );
				Rebuild();
				Edited?.Invoke();
			} ) );

		return panel;
	}

	private Widget ToggleRow( Func<bool> getEnabled, Action<bool> setEnabled, Action reorder, Action delete, Action resetHandle = null )
	{
		var row = new Widget { Layout = Layout.Row() };
		row.Layout.Spacing = 4;

		var toggle = new Button( getEnabled() ? "On" : "Off", getEnabled() ? "toggle_on" : "toggle_off" );
		toggle.Clicked = () =>
		{
			setEnabled( !getEnabled() );
			toggle.Text = getEnabled() ? "On" : "Off";
			toggle.Icon = getEnabled() ? "toggle_on" : "toggle_off";
		};
		row.Layout.Add( toggle );

		row.Layout.Add( new Button( "", "swap_vert" ) { Clicked = () => { reorder(); Rebuild(); }, ToolTip = "Move down" } );

		if ( resetHandle is not null )
			row.Layout.Add( new Button( "Reset Handle", "restart_alt" ) { Clicked = resetHandle } );

		row.Layout.AddStretchCell();
		row.Layout.Add( new Button( "Delete", "delete" ) { Clicked = delete } );

		return row;
	}

	private static void Reorder<T>( System.Collections.Generic.List<T> list, T item )
	{
		var index = list.IndexOf( item );
		if ( index < 0 || index >= list.Count - 1 )
			return;

		(list[index], list[index + 1]) = (list[index + 1], list[index]);
	}
}
