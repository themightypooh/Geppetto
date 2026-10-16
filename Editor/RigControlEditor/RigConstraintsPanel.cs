using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
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

	/// <summary>The open clip - where each live constraint's goal space is kept (IkGoalAnchors),
	/// since the space belongs with the goal's keys rather than with the shared rig.</summary>
	private RigAnimDocument _anim;

	private readonly Widget _list;

	public Action Edited { get; set; }

	/// <summary>The timeline's playhead - where Key Weight keys. Set by the window.</summary>
	public Func<float> Playhead { get; set; }

	/// <summary>A key was written to the clip (IK weight): the window records undo and re-poses.</summary>
	public Action Keyed { get; set; }

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

				RigHelpBox.S( "Max Stretch: cables and tentacles",
					"Above 1, a chain reaching for a goal past its own length stretches to get there, " +
					"by up to that much (1.5 = half as long again). The joints slide apart and the skin " +
					"between them stretches; the bones keep their thickness. A goal in reach never " +
					"stretches anything." ),

				RigHelpBox.S( "Goal follows: a hand that stays on the pipe",
					"A live constraint's goal is keyed with the model by default, so it travels with the " +
					"character. Goal Follows keys it on one of the clip's objects or props instead - move " +
					"the pipe and the hand comes with it, and in game it clamps whatever the Rig Anim " +
					"Player's Parts binds under that name. World plants it where the clip started, so it " +
					"stays put while game code moves the creature. Set per clip; the keys are converted, " +
					"so nothing jumps." ),

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

	public void SetClip( RigAnimDocument anim )
	{
		_anim = anim;
		Rebuild();
	}

	private void OpenAddMenu()
	{
		if ( _rig is null )
			return;

		var menu = new Menu( this );
		menu.AddOption( "IK Constraint", "swap_calls", () => Add( new IkConstraint { Name = UniqueName( "IK" ), Live = true } ) );
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

		if ( ik.Live )
		{
			body.Add( GoalSpaceRow( ik ) );
			body.Add( WeightRow( ik ) );
		}

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

	/// <summary>
	/// "Goal Follows": what this clip keys the constraint's goal against - the model, the world, or
	/// one of the clip's objects, parts or reference props. See IkGoalSpace.
	/// </summary>
	private Widget GoalSpaceRow( IkConstraint ik )
	{
		var row = new Widget { Layout = Layout.Row() };
		row.Layout.Spacing = 4;
		row.Layout.Add( new Editor.Label( "Goal Follows" ) { ToolTip = "What this clip keys the goal against - see the help above" } );

		var anchor = _anim?.FindIkGoalAnchor( ik.TargetBone );
		var current = anchor?.Space switch
		{
			IkGoalSpace.World => "World (planted)",
			IkGoalSpace.Object => anchor.Object,
			_ => "Model",
		};

		var button = new Button( current, "push_pin" ) { Enabled = _anim is not null };
		button.ToolTip = _anim is null ? "Open a clip - the goal's space is saved with its keys" : "Model, World, or an object of this clip";
		button.Clicked = () => OpenGoalSpaceMenu( ik );
		row.Layout.Add( button, 1 );

		return row;
	}

	/// <summary>
	/// "Weight Here": key this constraint's IK/FK blend at the playhead - 1 holds the end on its
	/// goal, 0 hands the chain back to its keyframes (a released cable flying on its baked sim). The
	/// keys are an ordinary track on the timeline ("(IK weight)"), eased and copied like any other.
	/// See TrackTarget.IkWeight.
	/// </summary>
	private Widget WeightRow( IkConstraint ik )
	{
		var row = new Widget { Layout = Layout.Row() };
		row.Layout.Spacing = 4;

		// Not the value at the playhead: the panel is not rebuilt on every scrub, and a number that
		// goes stale reads as a bug. The curve is on the timeline row.
		var keys = _anim?.FindIkWeightTrack( ik.TargetBone )?.Keyframes.Count ?? 0;

		row.Layout.Add( new Editor.Label( keys > 0 ? $"IK Weight: {keys} key(s), see timeline" : "IK Weight: 1 (not keyed)" )
		{
			ToolTip = "The IK/FK blend this clip keys for the constraint, multiplied into its Weight. 1 = held on the goal, 0 = the chain plays its own keys"
		}, 1 );

		void Key( string label, string icon, float value, string tip )
		{
			var button = new Button( label, icon ) { Enabled = _anim is not null, ToolTip = tip };
			button.Clicked = () => KeyWeight( ik, value );
			row.Layout.Add( button );
		}

		Key( "Key 1", "lock", 1f, "Key full IK at the playhead - the end holds its goal" );
		Key( "Key 0", "lock_open", 0f, "Key no IK at the playhead - the chain plays its keyframes (free, flying, on its baked sim)" );

		var more = new Button( "", "more_horiz" ) { Enabled = _anim is not null, ToolTip = "Key a part weight, or clear the weight track" };
		more.Clicked = () =>
		{
			var menu = new Menu( this );

			foreach ( var w in new[] { 0.25f, 0.5f, 0.75f } )
				menu.AddOption( $"Key {w:0.##}", "tune", () => KeyWeight( ik, w ) );

			menu.AddSeparator();
			menu.AddOption( "Clear Weight Track", "delete", () =>
			{
				if ( _anim.BoneTracks.RemoveAll( t => t.Target == TrackTarget.IkWeight && t.BoneName == ik.TargetBone ) == 0 )
					Log.Info( $"[Marionette] {ik.Name}: no weight keys to clear - it is at full weight" );
				else
					Keyed?.Invoke();

				Rebuild();
			} );

			menu.OpenAtCursor();
		};
		row.Layout.Add( more );

		return row;
	}

	private void KeyWeight( IkConstraint ik, float value )
	{
		if ( _anim is null || string.IsNullOrWhiteSpace( ik.TargetBone ) )
			return;

		var frame = (int)MathF.Round( Playhead?.Invoke() ?? 0f );
		_anim.SetIkWeightKey( ik.TargetBone, frame, value );
		Log.Info( $"[Marionette] {ik.Name}: IK weight {value:0.##} keyed at frame {frame}" );
		Keyed?.Invoke();
		Rebuild();
	}

	private void OpenGoalSpaceMenu( IkConstraint ik )
	{
		if ( _anim is null || string.IsNullOrWhiteSpace( ik.TargetBone ) )
			return;

		var anchor = _anim.FindIkGoalAnchor( ik.TargetBone );
		var menu = new Menu( this );

		void Option( string label, string icon, IkGoalSpace space, string objectName, string tip )
		{
			var option = menu.AddOption( label, icon, () => SetGoalSpace( ik, space, objectName ) );
			option.Checkable = true;
			option.Checked = (anchor?.Space ?? IkGoalSpace.Model) == space && (space != IkGoalSpace.Object || anchor.Object == objectName);
			option.StatusTip = tip;
		}

		Option( "Model", "accessibility_new", IkGoalSpace.Model, null, "Keyed with the character - the goal travels with it" );
		Option( "World (planted)", "public", IkGoalSpace.World, null, "Planted where the clip started - stays put while the GameObject moves in game" );

		// Only things the goal can be placed on: an object, its parts, a reference prop - and none
		// that follow a bone, which would put the goal on the arm it is solving.
		var candidates = new List<string>();

		foreach ( var obj in _anim.Objects ?? new List<RigObject>() )
		{
			if ( obj is null || string.IsNullOrWhiteSpace( obj.Name ) || !string.IsNullOrWhiteSpace( obj.FollowBone ) )
				continue;

			candidates.Add( obj.Name );
			candidates.AddRange( (obj.Parts ?? new List<RigObjectPart>() )
				.Where( p => p is not null && !string.IsNullOrWhiteSpace( p.Name ) )
				.Select( p => RigTrackName.Qualify( obj.Name, p.Name ) ) );
		}

		candidates.AddRange( (_anim.ReferenceProps ?? new List<ReferenceProp>())
			.Where( p => p is not null && !string.IsNullOrWhiteSpace( p.Name ) && string.IsNullOrWhiteSpace( p.FollowBone ) )
			.Select( p => p.Name ) );

		menu.AddSeparator();

		if ( candidates.Count == 0 )
			menu.AddOption( "No objects or props in this clip - add one in Objects", "info", null ).Enabled = false;

		foreach ( var name in candidates.Distinct() )
			Option( RigTrackName.Display( name ), "category", IkGoalSpace.Object, name, $"Keyed on {name}: the goal follows it, and in game the object bound as \"{name}\" in the player's Parts" );

		menu.OpenAtCursor();
	}

	private void SetGoalSpace( IkConstraint ik, IkGoalSpace space, string objectName )
	{
		if ( !_anim.SetIkGoalSpace( ik.TargetBone, space, objectName ) )
		{
			Log.Warning( $"[Marionette] {ik.Name}: cannot key its goal on \"{objectName}\" - nothing in this clip by that name can be placed (an object following a bone cannot)" );
			return;
		}

		Rebuild();
		Edited?.Invoke();
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
			row.Layout.Add( new Button( "Reset Handle", "settings_backup_restore" ) { Clicked = resetHandle } );

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
