using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The feature dialog — Onshape's, as closely as this toolkit allows.
///
/// It is a dialog rather than a passive property sheet, and the difference is the whole point.
/// A property sheet shows whatever is selected and every edit is immediately permanent. A feature
/// dialog is *modal to one feature*: it has a name you can type over, a green tick that commits
/// and a red cross that puts everything back, and it goes red the moment the feature will not
/// build. That accept/cancel pair is what makes it safe to drag a parameter and see what happens,
/// which is most of what using a parametric modeller consists of.
///
/// It lives above the feature tree in the left column because that is where Onshape puts it, and
/// because the tree is the thing you want to keep looking at while a feature is open.
/// </summary>
internal sealed class EffigyFeatureDialog : Widget
{
	private readonly EffigyViewport _viewport;

	private Feature _feature;

	/// <summary>True when the dialog was opened on a feature that was created for it. Cancelling
	/// then deletes the feature; cancelling an edit only restores its parameters.</summary>
	private bool _isNew;

	/// <summary>Parameter values as they were when the dialog opened, for Cancel.</summary>
	private readonly Dictionary<IParam, object> _snapshot = new();

	/// <summary>
	/// Whether a sketch plane has actually been chosen.
	///
	/// This lives on the dialog rather than on the selection box because Rebuild() destroys and
	/// recreates every row. Held on the widget, it was reset to its default on the very next
	/// rebuild — and picking a plane triggers a rebuild — so the plane went in, the tree updated,
	/// and the box redrew itself empty a frame later.
	/// </summary>
	private bool _planeChosen;

	/// <summary>Features whose sketch plane has actually been picked, so reopening one remembers.
	/// Identity-keyed and never cleared: a studio's worth of features is a few dozen objects.</summary>
	private readonly HashSet<Feature> _planeChosenFor = new();

	// --- widgets ---
	private Widget _header;
	private LineEdit _nameEdit;
	private IconButton _acceptButton;
	private Editor.Label _statusLabel;
	private Widget _body;
	private readonly List<Widget> _rows = new();

	/// <summary>
	/// One per editable row: pushes the parameter's CURRENT value back into that row's widgets.
	///
	/// This is what keeps the numeric field honest while the face is being dragged in the viewport.
	/// The alternative - rebuilding the dialog every frame of a drag - destroys and recreates every
	/// widget in it, which throws away focus and any half-typed expression.
	/// </summary>
	private readonly List<Action> _valueRefreshers = new();

	/// <summary>
	/// Whether the feature would refuse to build right now.
	///
	/// Onshape's rule, and it is a workflow rule rather than a cosmetic one: "The title is red if
	/// you have not completely filled out the dialog, or if the information entered has resulted
	/// in an error. This prevents you from committing a broken feature." Two ways to be broken -
	/// the kernel threw (Feature.Error), or the dialog is still waiting on a selection the user
	/// has not made, which no rebuild can report because the feature never ran.
	/// </summary>
	private bool IsBroken => _feature is not null
		&& (_feature.Error is not null
			|| (_feature is SketchFeature && !_planeChosen)
			|| (_isNew && _feature is SketchConsumingFeature { RegionSeed: null })
			|| (_isNew && BodySelectionOf( _feature ) is { BodyIds.Count: 0 }));

	/// <summary>Fires when the tick is pressed. The window rebuilds and closes the dialog.</summary>
	public Action<Feature> Accepted { get; set; }

	/// <summary>Fires when the cross is pressed, after parameters have been put back. Carries the
	/// feature so the window can delete it when it was newly created.</summary>
	public Action<Feature, bool> Cancelled { get; set; }

	/// <summary>Any parameter edit — the studio rebuilds live, as Onshape does.</summary>
	public Action Edited { get; set; }

	/// <summary>The name was typed over, so the tree needs redrawing.</summary>
	public Action Renamed { get; set; }

	/// <summary>Fires when a Sketch feature's dialog wants the viewport to enter sketch mode.</summary>
	public Action<SketchFeature> SketchRequested { get; set; }

	/// <summary>Shift+Enter accepted and wants another feature of the same kind appended.</summary>
	public Action<Feature> RepeatRequested { get; set; }

	public Feature Feature => _feature;
	public bool IsOpen => _feature is not null;

	public EffigyFeatureDialog( Widget parent, EffigyViewport viewport ) : base( parent )
	{
		_viewport = viewport;

		Name = "FeatureDialog";
		Layout = Layout.Column();
		Visible = false;

		BuildHeader();

		_body = new Widget( this ) { Layout = Layout.Column() };
		Layout.Add( _body );
	}

	// --- header -------------------------------------------------------------------------------

	private void BuildHeader()
	{
		_header = new Widget( this ) { Layout = Layout.Row() };
		_header.Layout.Margin = new Sandbox.UI.Margin( 8, 5 );
		_header.Layout.Spacing = 4;

		// The name is editable in place, which is how a feature gets renamed in Onshape - there is
		// no separate rename command anywhere in the UI.
		_nameEdit = new LineEdit( "", _header );
		_nameEdit.TextEdited += OnNameEdited;
		_header.Layout.Add( _nameEdit, 1 );

		_acceptButton = new IconButton( "check", Accept )
		{
			ToolTip = "Accept (Enter) - commit this feature",
			IconSize = 16,
			Background = Color.Transparent,
		};

		_header.Layout.Add( _acceptButton );

		_header.Layout.Add( new IconButton( "close", Cancel )
		{
			ToolTip = "Cancel (Escape) - discard changes",
			IconSize = 16,
			Background = Color.Transparent,
		} );

		Layout.Add( _header );

		// The reason the tick is refusing, in words. A greyed-out tick with no explanation is the
		// most frustrating possible version of "this feature is not finished".
		_statusLabel = new Editor.Label( "" ) { Color = Theme.Red };
		_statusLabel.Visible = false;
		Layout.Add( _statusLabel );
	}

	/// <summary>
	/// Re-read the feature's build state and show it. Called by the window after every rebuild,
	/// because Feature.Error is only meaningful once the studio has actually tried to run it.
	/// </summary>
	public void RefreshState()
	{
		if ( _feature is null )
			return;

		var broken = IsBroken;

		if ( _acceptButton.IsValid() )
			_acceptButton.Enabled = !broken;

		if ( _statusLabel.IsValid() )
		{
			_statusLabel.Text = _feature.Error ?? WaitingOn();
			_statusLabel.Visible = broken;
		}

		Update();
	}

	/// <summary>What the feature is still waiting for, when it has not failed but is not finished
	/// either. Empty when nothing is outstanding.</summary>
	private string WaitingOn()
	{
		if ( _feature is SketchFeature && !_planeChosen )
			return "Pick a sketch plane to continue";

		if ( _isNew && _feature is SketchConsumingFeature { RegionSeed: null } )
			return "Select a face in the viewport to extrude";

		if ( _isNew && BodySelectionOf( _feature ) is { BodyIds.Count: 0 } )
			return "Select a body in the viewport";

		return "";
	}

	private void OnNameEdited( string text )
	{
		if ( _feature is null )
			return;

		_feature.Name = string.IsNullOrWhiteSpace( text ) ? null : text;
		Renamed?.Invoke();
	}

	/// <summary>
	/// The title strip: feature colour on the left, error state in the text.
	///
	/// Red-when-broken is doing real work rather than decoration. A freshly created Sketch has no
	/// plane and an Extrude has no profile, so both start invalid — the red name is the tool
	/// saying "this needs something from you before the tick will do anything", which is exactly
	/// what the empty selection box below it is waiting for.
	/// </summary>
	protected override void OnPaint()
	{
		var broken = IsBroken;

		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( LocalRect );

		// A red spine down the open edge, so "this will not build" is legible from across the
		// screen rather than only by reading the status line.
		if ( broken )
		{
			Paint.SetBrush( Theme.Red );
			Paint.DrawRect( new Rect( 0f, 0f, 2f, Height ) );
		}

		Paint.ClearBrush();
		Paint.SetPen( broken ? Theme.Red.WithAlpha( 0.5f ) : Theme.WindowBackground );
		Paint.DrawLine( new Vector2( 0f, Height - 1f ), new Vector2( Width, Height - 1f ) );
	}

	/// <summary>
	/// Enter commits, Escape cancels, Shift+Enter commits and reopens the same tool for another
	/// one. All three are Onshape's, and the third is the one that makes adding six of something
	/// bearable.
	/// </summary>
	protected override void OnKeyPress( KeyEvent e )
	{
		if ( _feature is null )
		{
			base.OnKeyPress( e );
			return;
		}

		switch ( e.Key )
		{
			// KeyCode.Enter and KeyCode.Shift are the only two enum members in this tool that are
			// NOT already used somewhere in RigControlEditor, so they are the two things here that
			// a first compile will confirm or reject. If either is named differently in this
			// engine build, it is a one-word fix on each and nothing else in the file depends on
			// them. (KeyCode.Escape below is proven - RigTimeline and RigViewport both use it.)
			case KeyCode.Enter:
				if ( Editor.Application.IsKeyDown( KeyCode.Shift ) )
					AcceptAndRepeat();
				else
					Accept();

				e.Accepted = true;
				return;

			case KeyCode.Escape:
				Cancel();
				e.Accepted = true;
				return;
		}

		base.OnKeyPress( e );
	}

	// --- open / close -------------------------------------------------------------------------

	public void Open( Feature feature, bool isNew )
	{
		_feature = feature;
		_isNew = isNew;

		// Whether a plane was ever actually picked, remembered per feature rather than inferred
		// from isNew. Inferring it meant reopening a sketch you had abandoned without choosing a
		// plane showed "Top (XY)" in the box as though you had picked it - the exact lie the
		// selection box exists to avoid, just deferred to the second time you looked at it.
		_planeChosen = _planeChosenFor.Contains( feature );

		TakeSnapshot();

		_nameEdit.Text = feature.Name ?? feature.TypeName;

		// A freshly created Extrude is waiting for a face, so arm the picking immediately rather
		// than making the user find and click the selection box first. This is the whole "press
		// the button, then choose a face" flow - the button IS the start of the selection.
		if ( isNew && feature is SketchConsumingFeature { RegionSeed: null } )
			_viewport.RegionPickMode = true;

		// Same for the eight features that act on bodies rather than on a sketch.
		if ( isNew && BodySelectionOf( feature ) is { BodyIds.Count: 0 } )
			_viewport.BodyPickMode = true;

		Rebuild();

		Visible = true;

		RefreshState();
	}

	public new void Close()
	{
		_feature = null;
		_snapshot.Clear();
		ClearRows();
		Visible = false;

		// Explicitly, not just via the selectors' OnDestroyed: widget destruction can be deferred,
		// and a frame with picking still armed is a frame where a stray click lands on a face for a
		// feature that is no longer open.
		_viewport.PlanePickMode = false;
		_viewport.RegionPickMode = false;
		_viewport.BodyPickMode = false;
		_viewport.ClearPullTarget();
	}

	private void Accept()
	{
		if ( _feature is null )
			return;

		// Onshape will not let a broken feature be committed, and neither will this. Refreshing on
		// the way out is what puts the reason on screen when the tick was hit rather than ignored.
		if ( IsBroken )
		{
			RefreshState();
			return;
		}

		var feature = _feature;
		Close();
		Accepted?.Invoke( feature );
	}

	/// <summary>Shift+Enter: commit, then open a fresh one of the same kind. Onshape's, and the
	/// difference between placing six holes and placing one hole six times.</summary>
	private void AcceptAndRepeat()
	{
		if ( _feature is null || IsBroken )
			return;

		var type = _feature.GetType();

		Accept();

		// Every feature is constructed parameterless by the toolbar already (see EffigyWindow's
		// CreateOption factories), so this cannot hit a feature type it does not know how to make.
		if ( Activator.CreateInstance( type ) is Feature repeat )
			RepeatRequested?.Invoke( repeat );
	}

	private void Cancel()
	{
		if ( _feature is null )
			return;

		var feature = _feature;
		var wasNew = _isNew;

		RestoreSnapshot();
		Close();

		Cancelled?.Invoke( feature, wasNew );
	}

	// --- snapshot -----------------------------------------------------------------------------

	// Boxed values keyed by the parameter object. Parameters ARE the storage in this kernel (see
	// Feature.cs) - there is no separate model to copy - so a cancel has to put the numbers back
	// by hand.
	private void TakeSnapshot()
	{
		_snapshot.Clear();

		if ( _feature is null )
			return;

		foreach ( var p in _feature.Parameters )
		{
			switch ( p )
			{
				case FloatParam f: _snapshot[p] = f.Value; break;
				case IntParam i: _snapshot[p] = i.Value; break;
				case BoolParam b: _snapshot[p] = b.Value; break;
				case Vec3Param v: _snapshot[p] = v.Value; break;
				case ChoiceParam c: _snapshot[p] = c.Index; break;
			}
		}
	}

	private void RestoreSnapshot()
	{
		foreach ( var (p, value) in _snapshot )
		{
			switch ( p )
			{
				case FloatParam f when value is float fv: f.Value = fv; break;
				case IntParam i when value is int iv: i.Value = iv; break;
				case BoolParam b when value is bool bv: b.Value = bv; break;
				case Vec3Param v when value is Vec3 vv: v.Value = vv; break;
				case ChoiceParam c when value is int ci: c.Index = ci; break;
			}
		}
	}

	// --- body ---------------------------------------------------------------------------------

	private void ClearRows()
	{
		foreach ( var w in _rows )
			w.Destroy();

		_rows.Clear();
		_valueRefreshers.Clear();
	}

	/// <summary>Re-read every parameter into its row. Called while the viewport is driving a value,
	/// so the typed field tracks the gizmo instead of showing a stale number.</summary>
	public void RefreshValues()
	{
		foreach ( var refresh in _valueRefreshers )
			refresh();
	}

	/// <summary>Rebuild the parameter rows. Called on open and whenever a choice changes the set of
	/// parameters — PrimitiveFeature.Parameters returns a different list per shape, so switching
	/// Box to Cylinder has to redraw the dialog, exactly as it does in Onshape.</summary>
	public void Rebuild()
	{
		ClearRows();

		if ( _feature is null )
			return;

		// A sketch's plane is picked in the viewport, not chosen from a dropdown, so it gets a
		// selection box instead of the generic ChoiceParam row.
		if ( _feature is SketchFeature sketch )
		{
			AddRow( new EffigyPlaneSelector( _body, _viewport, sketch.Plane, OnPlaneChanged, _planeChosen ) );
			AddRow( BuildFloatRow( sketch.PlaneOffset ) );
			AddRow( BuildSketchButtonRow( sketch ) );
			return;
		}

		// Extrude and Revolve pick a FACE, not a dropdown entry. The kernel's Sketch ChoiceParam
		// only ever held a single empty string - it was never populated with real sketch ids - so
		// the selection box supersedes it rather than sitting next to it.
		if ( _feature is SketchConsumingFeature consumer )
		{
			AddRow( new EffigyRegionSelector( _body, _viewport, consumer, OnRegionCleared ) );

			foreach ( var param in _feature.Parameters )
			{
				if ( ReferenceEquals( param, consumer.Sketch ) )
					continue;

				AddRow( BuildParamRow( param ) );
			}

			return;
		}

		foreach ( var param in _feature.Parameters )
			AddRow( BuildParamRow( param ) );
	}

	/// <summary>The region selection was cleared back to "every closed region".</summary>
	private void OnRegionCleared()
	{
		Edited?.Invoke();
		Rebuild();
	}

	/// <summary>
	/// A plane was picked, so the sketch is fully specified — drop straight into sketch mode.
	///
	/// Onshape has no "now start sketching" step: choosing the plane IS entering the sketch, and
	/// the sketch toolbar appears at that moment. Making it a second button meant the sketch tools
	/// were reachable only by finding a button below the fold of a dialog that looked finished.
	/// </summary>
	private void OnPlaneChanged()
	{
		_planeChosen = true;

		if ( _feature is not null )
			_planeChosenFor.Add( _feature );

		Edited?.Invoke();
		Rebuild();

		if ( _feature is SketchFeature sketch )
			SketchRequested?.Invoke( sketch );
	}

	private void AddRow( Widget row )
	{
		if ( row is null )
			return;

		_body.Layout.Add( row );
		_rows.Add( row );
	}

	private Widget BuildSketchButtonRow( SketchFeature sketch )
	{
		var row = new Widget( _body ) { Layout = Layout.Row() };
		row.Layout.Margin = new Sandbox.UI.Margin( 8, 6 );
		row.Layout.Spacing = 6;

		var count = sketch.Sketch.Curves.Count;

		// Picking a plane enters the sketch on its own; this is only for getting back INTO a sketch
		// you have already left, which Onshape does by double-clicking it in the tree.
		row.Layout.Add( new Button( count == 0 ? "Edit sketch" : $"Edit sketch ({count} curves)", "edit" )
		{
			Enabled = _planeChosen,
			Clicked = () => SketchRequested?.Invoke( sketch ),
		}, 1 );

		return row;
	}

	// --- parameter rows -------------------------------------------------------------------------

	private Widget BuildParamRow( IParam param )
	{
		switch ( param )
		{
			case FloatParam fp: return BuildFloatRow( fp );
			case IntParam ip: return BuildIntRow( ip );
			case BoolParam bp: return BuildBoolRow( bp );
			case ChoiceParam cp: return BuildChoiceRow( cp );
			case Vec3Param vp: return BuildVec3Row( vp );
			case BodySelectionParam bs: return BuildBodySelectionRow( bs );
			default: return null;
		}
	}

	private Widget NewRow( out Layout layout, bool column = false )
	{
		var row = new Widget( _body ) { Layout = column ? Layout.Column() : Layout.Row() };
		row.Layout.Margin = new Sandbox.UI.Margin( 8, 3 );
		row.Layout.Spacing = 6;
		layout = row.Layout;
		return row;
	}

	/// <summary>
	/// Whether a parameter's own bounds make a slider worth showing next to the field.
	///
	/// Most of Effigy's lengths declare min 0.0001 and no maximum at all (BasicFeatures.cs), and
	/// the version this replaces invented a -9999..9999 range for them. A slider spanning five
	/// orders of magnitude at 0.1 per step cannot be aimed at a value; it only looks like a
	/// control. Bevel's 0..180 angle threshold and Subdivide's 0..6 levels are real ranges, and
	/// those are the ones worth dragging.
	/// </summary>
	private static bool Draggable( float min, float max ) =>
		min > float.MinValue && max < float.MaxValue && max - min <= 1024f;

	/// <summary>Effigy's lengths are dimensionless, so FloatParam's "u" is decoration rather than
	/// a unit. Real units - "deg" - still earn their label.</summary>
	private static bool ShowUnit( string unit ) => !string.IsNullOrEmpty( unit ) && unit != "u";

	private Widget BuildFloatRow( FloatParam fp )
	{
		var row = NewRow( out var layout );
		layout.Add( new Editor.Label( fp.Label ) { FixedWidth = 110 } );

		var draggable = Draggable( fp.Min, fp.Max );

		var field = new EffigyNumericField( row, fp.Clamped, fp.Unit )
		{
			Min = fp.Min,
			Max = fp.Max,
		};

		FloatSlider slider = null;

		if ( draggable )
		{
			slider = new FloatSlider( row )
			{
				Minimum = fp.Min,
				Maximum = fp.Max,
				Step = fp.Unit == "deg" ? 1f : 0.01f,
				Value = fp.Clamped,
			};

			slider.OnValueEdited = () =>
			{
				fp.Value = slider.Value;

				// SetValue rather than assigning through the field's text, so pushing the slider
				// does not echo back out of the field as another edit.
				field.SetValue( slider.Value );
				Edited?.Invoke();
			};
		}

		field.ValueEdited = v =>
		{
			fp.Value = v;

			if ( slider.IsValid() )
				slider.Value = v;

			Edited?.Invoke();
		};

		if ( draggable )
		{
			field.FixedWidth = 96;
			layout.Add( field );
			layout.Add( slider, 1 );
		}
		else
		{
			layout.Add( field, 1 );
		}

		if ( ShowUnit( fp.Unit ) )
			layout.Add( new Editor.Label( fp.Unit ) { FixedWidth = 26 } );

		_valueRefreshers.Add( () =>
		{
			if ( field.IsValid() )
				field.SetValue( fp.Clamped );

			if ( slider.IsValid() )
				slider.Value = fp.Clamped;
		} );

		return row;
	}

	private Widget BuildIntRow( IntParam ip )
	{
		var row = NewRow( out var layout );
		layout.Add( new Editor.Label( ip.Label ) { FixedWidth = 110 } );

		var draggable = Draggable( ip.Min, ip.Max );

		var field = new EffigyNumericField( row, ip.Clamped )
		{
			Min = ip.Min,
			Max = ip.Max,
			Integer = true,
		};

		FloatSlider slider = null;

		if ( draggable )
		{
			slider = new FloatSlider( row )
			{
				Minimum = ip.Min,
				Maximum = ip.Max,
				Step = 1f,
				Value = ip.Clamped,
			};

			slider.OnValueEdited = () =>
			{
				ip.Value = (int)slider.Value;
				field.SetValue( ip.Value );
				Edited?.Invoke();
			};
		}

		field.ValueEdited = v =>
		{
			ip.Value = (int)v;

			if ( slider.IsValid() )
				slider.Value = ip.Value;

			Edited?.Invoke();
		};

		if ( draggable )
		{
			field.FixedWidth = 96;
			layout.Add( field );
			layout.Add( slider, 1 );
		}
		else
		{
			layout.Add( field, 1 );
		}

		_valueRefreshers.Add( () =>
		{
			if ( field.IsValid() )
				field.SetValue( ip.Clamped );

			if ( slider.IsValid() )
				slider.Value = ip.Clamped;
		} );

		return row;
	}

	private Widget BuildBoolRow( BoolParam bp )
	{
		var row = NewRow( out var layout );
		layout.Add( new Editor.Label( "" ) { FixedWidth = 110 } );

		var toggle = new Checkbox( bp.Label ) { Value = bp.Value };

		toggle.Toggled = () =>
		{
			bp.Value = toggle.Value;
			Edited?.Invoke();
		};

		layout.Add( toggle, 1 );
		return row;
	}

	private Widget BuildChoiceRow( ChoiceParam cp )
	{
		var row = NewRow( out var layout );
		layout.Add( new Editor.Label( cp.Label ) { FixedWidth = 110 } );

		var combo = new ComboBox( row ) { CurrentIndex = cp.Index };

		for ( var i = 0; i < cp.Options.Length; i++ )
		{
			var idx = i;

			combo.AddItem( cp.Options[i], "", () =>
			{
				if ( cp.Index == idx )
					return;

				cp.Index = idx;
				Edited?.Invoke();

				// Which parameters exist can depend on this choice, so the dialog redraws itself.
				Rebuild();
			}, "", idx == cp.Index, true );
		}

		layout.Add( combo, 1 );
		return row;
	}

	private Widget BuildVec3Row( Vec3Param vp )
	{
		var row = NewRow( out var layout, column: true );
		layout.Add( new Editor.Label( vp.Label ) );

		var sub = new Widget( row ) { Layout = Layout.Row() };
		sub.Layout.Spacing = 4;

		AddAxis( sub, "X", Theme.Red, vp.Value.x, v => vp.Value = new Vec3( v, vp.Value.y, vp.Value.z ) );
		AddAxis( sub, "Y", Theme.Green, vp.Value.y, v => vp.Value = new Vec3( vp.Value.x, v, vp.Value.z ) );
		AddAxis( sub, "Z", Theme.Blue, vp.Value.z, v => vp.Value = new Vec3( vp.Value.x, vp.Value.y, v ) );

		layout.Add( sub );
		return row;
	}

	/// <summary>
	/// One axis of a Vec3, as a typed field.
	///
	/// These were unbounded FloatSliders — constructed with no Minimum or Maximum at all, so they
	/// took whatever the widget's default range is and a direction vector of (0,0,1) was not
	/// reliably expressible. Axis letters carry the viewport's own X/Y/Z colours so the field and
	/// the gizmo agree about which one is which.
	/// </summary>
	private void AddAxis( Widget parent, string label, Color colour, float value, Action<float> set )
	{
		var field = new EffigyNumericField( parent, value );

		field.ValueEdited = v =>
		{
			set( v );
			Edited?.Invoke();
		};

		parent.Layout.Add( new Editor.Label( label ) { FixedWidth = 12, Color = colour } );
		parent.Layout.Add( field, 1 );
	}

	/// <summary>
	/// Which bodies the feature acts on.
	///
	/// This was a disabled label reading "All bodies" - the parameter existed and the kernel
	/// honoured it, and there was no way to put anything in it. Now it is a selection box like the
	/// plane and face ones: click it, and the bodies light up in the viewport to be chosen.
	/// </summary>
	private Widget BuildBodySelectionRow( BodySelectionParam bs )
	{
		var row = NewRow( out var layout );
		layout.Add( new Editor.Label( bs.Label ) { FixedWidth = 110 } );
		layout.Add( new EffigyBodySelector( _body, _viewport, bs, OnBodySelectionCleared ), 1 );
		return row;
	}

	/// <summary>The body selection was cleared back to "every body".</summary>
	private void OnBodySelectionCleared()
	{
		Edited?.Invoke();
		Rebuild();
	}

	/// <summary>The feature's body selection, if it has one. Detected from the parameter list
	/// rather than by type, so any future feature that takes bodies gets this for free.</summary>
	public static BodySelectionParam BodySelectionOf( Feature feature ) =>
		feature?.Parameters.OfType<BodySelectionParam>().FirstOrDefault();
}

/// <summary>
/// Onshape's selection box: a bordered field that is empty until you click it and then pick
/// something in the graphics area.
///
/// This is the affordance a dropdown cannot give you. A dropdown says "the plane is one of these
/// three names"; a selection box says "point at the plane you mean", which is both how CAD users
/// expect to choose a plane and the only version that extends to picking a face later — the
/// dropdown has nothing to list once planes stop being a fixed set of three.
/// </summary>
internal sealed class EffigyPlaneSelector : Widget
{
	private readonly EffigyViewport _viewport;
	private readonly ChoiceParam _plane;
	private readonly Action _changed;

	/// <summary>True while waiting for a viewport click. The box goes accent-coloured and the
	/// three reference planes become pickable.</summary>
	private bool _armed;

	/// <summary>Whether a plane has actually been chosen. A fresh Sketch has Index 0 by default,
	/// which is a value but not a choice — showing "Top (XY)" in the box before the user picked
	/// anything would be a lie, and would hide that the feature is waiting on them.</summary>
	private bool _chosen;

	public EffigyPlaneSelector( Widget parent, EffigyViewport viewport, ChoiceParam plane, Action changed, bool chosen )
		: base( parent )
	{
		_viewport = viewport;
		_plane = plane;
		_changed = changed;
		_chosen = chosen;

		Layout = Layout.Row();
		Layout.Margin = new Sandbox.UI.Margin( 8, 3 );
		Layout.Spacing = 6;

		FixedHeight = 46f;
		Cursor = CursorShape.Finger;
	}

	protected override void OnPaint()
	{
		var label = new Rect( 0f, 0f, Width, 16f );

		Paint.SetPen( Theme.TextControl.WithAlpha( 0.6f ) );
		Paint.SetDefaultFont( 8 );
		Paint.DrawText( label.Shrink( 8f, 2f, 0f, 0f ), "Sketch plane", TextFlag.LeftTop );

		var box = new Rect( 8f, 18f, Width - 16f, 22f );

		Paint.ClearPen();
		Paint.SetBrush( _armed ? Theme.Blue.WithAlpha( 0.18f ) : Theme.ControlBackground );
		Paint.DrawRect( box, 2f );

		Paint.ClearBrush();
		Paint.SetPen( _armed ? Theme.Blue : (_chosen ? Theme.TextControl.WithAlpha( 0.35f ) : Theme.Red.WithAlpha( 0.6f )) );
		Paint.DrawRect( box, 2f );

		Paint.SetDefaultFont( 9 );

		if ( _armed )
		{
			Paint.SetPen( Theme.Blue );
			Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ), "Pick a plane in the viewport", TextFlag.LeftCenter );
		}
		else if ( _chosen )
		{
			Paint.SetPen( Theme.TextControl );
			Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ), _plane.Value, TextFlag.LeftCenter );
		}
		else
		{
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.45f ) );
			Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ), "Select a plane", TextFlag.LeftCenter );
		}
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( e.LeftMouseButton )
			Toggle();
	}

	private void Toggle()
	{
		if ( _armed )
		{
			Disarm();
			return;
		}

		_armed = true;
		_viewport.PlanePickMode = true;
		_viewport.PlanePicked = OnPicked;
		Update();
	}

	private void Disarm()
	{
		_armed = false;
		_viewport.PlanePickMode = false;
		_viewport.PlanePicked = null;
		Update();
	}

	private void OnPicked( int index )
	{
		_plane.Index = index;
		_chosen = true;
		_viewport.IgnoreNextSketchClick();

		Disarm();
		_changed?.Invoke();
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		// Leaving pick mode armed would make the planes stay clickable after the dialog closed.
		if ( _armed )
			Disarm();
	}
}

/// <summary>
/// The face selection box for Extrude and Revolve.
///
/// Same affordance as the plane selector above it, for the same reason: you point at the region
/// you mean in the graphics area rather than choosing it from a list. There is no useful list to
/// choose from anyway — closed regions are re-found from the curve graph on every rebuild and have
/// no names or stable order, which is exactly why the selection is stored as a point inside the
/// region (SketchConsumingFeature.RegionSeed) rather than as an index.
///
/// Unset means every closed region in the sketch, which is what these features have always done
/// and stays the default.
/// </summary>
internal sealed class EffigyRegionSelector : Widget
{
	private readonly EffigyViewport _viewport;
	private readonly SketchConsumingFeature _feature;
	private readonly Action _cleared;

	/// <summary>True while waiting for a viewport click, with every closed region lit up.</summary>
	private bool _armed;

	public EffigyRegionSelector( Widget parent, EffigyViewport viewport, SketchConsumingFeature feature, Action cleared )
		: base( parent )
	{
		_viewport = viewport;
		_feature = feature;
		_cleared = cleared;

		// Reflect picking that was armed for us when the dialog opened, rather than starting
		// unarmed and disagreeing with the lit-up faces in the viewport.
		_armed = viewport.RegionPickMode;

		Layout = Layout.Row();
		Layout.Margin = new Sandbox.UI.Margin( 8, 3 );
		Layout.Spacing = 6;

		FixedHeight = 46f;
		Cursor = CursorShape.Finger;

		// Only offered once there is something to clear. A permanently visible "use all" next to
		// an empty box would read as a second, competing choice.
		if ( feature.RegionSeed is not null )
		{
			Layout.AddStretchCell();

			Layout.Add( new Button( "Use all", "select_all" )
			{
				ToolTip = "Build from every closed region in the sketch",
				Clicked = Clear,
			} );
		}
	}

	protected override void OnPaint()
	{
		var label = new Rect( 0f, 0f, Width, 16f );

		Paint.SetPen( Theme.TextControl.WithAlpha( 0.6f ) );
		Paint.SetDefaultFont( 8 );
		Paint.DrawText( label.Shrink( 8f, 2f, 0f, 0f ), "Region", TextFlag.LeftTop );

		var box = new Rect( 8f, 18f, Width - 96f, 22f );

		Paint.ClearPen();
		Paint.SetBrush( _armed ? Theme.Blue.WithAlpha( 0.18f ) : Theme.ControlBackground );
		Paint.DrawRect( box, 2f );

		Paint.ClearBrush();
		Paint.SetPen( _armed ? Theme.Blue : Theme.TextControl.WithAlpha( 0.35f ) );
		Paint.DrawRect( box, 2f );

		Paint.SetDefaultFont( 9 );

		if ( _armed )
		{
			Paint.SetPen( Theme.Blue );
			Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ), "Click a shaded face in the viewport", TextFlag.LeftCenter );
			return;
		}

		// Unset is a legitimate, useful state here - unlike the plane box, which is genuinely
		// waiting on you - so it reads as a value rather than as a warning.
		Paint.SetPen( Theme.TextControl );
		Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ),
			_feature.RegionSeed is null ? "All closed regions" : "1 face selected", TextFlag.LeftCenter );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( e.LeftMouseButton )
			Toggle();
	}

	private void Toggle()
	{
		if ( _armed )
		{
			Disarm();
			return;
		}

		_armed = true;
		_viewport.RegionPickMode = true;
		Update();
	}

	private void Disarm()
	{
		_armed = false;
		_viewport.RegionPickMode = false;
		Update();
	}

	private void Clear()
	{
		_feature.RegionSeed = null;
		Disarm();
		_cleared?.Invoke();
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		// Leaving pick mode armed would keep every face lit and clickable after the dialog closed.
		if ( _armed )
			Disarm();
	}
}

/// <summary>
/// The body selection box, for the eight features that act on solids rather than on a sketch.
///
/// Same shape as the plane and region selectors: a bordered field that is empty until you click it
/// and then pick in the graphics area. Multi-select, because Mirror and the patterns genuinely take
/// several — clicking a body that is already in takes it back out.
///
/// Empty means every body, which is BodySelectionParam's own documented default. So an unset box is
/// a value rather than a warning, and only a NEW feature treats it as something still outstanding.
/// </summary>
internal sealed class EffigyBodySelector : Widget
{
	private readonly EffigyViewport _viewport;
	private readonly BodySelectionParam _param;
	private readonly Action _cleared;

	private bool _armed;

	public EffigyBodySelector( Widget parent, EffigyViewport viewport, BodySelectionParam param, Action cleared )
		: base( parent )
	{
		_viewport = viewport;
		_param = param;
		_cleared = cleared;

		// Reflect picking that was armed for us when the dialog opened.
		_armed = viewport.BodyPickMode;

		Layout = Layout.Row();
		Layout.Spacing = 6;

		FixedHeight = 26f;
		Cursor = CursorShape.Finger;

		if ( param.BodyIds.Count > 0 )
		{
			Layout.AddStretchCell();

			Layout.Add( new Button( "All", "select_all" )
			{
				ToolTip = "Act on every body",
				Clicked = Clear,
			} );
		}
	}

	protected override void OnPaint()
	{
		var box = new Rect( 0f, 2f, Width - (_param.BodyIds.Count > 0 ? 60f : 0f), 22f );

		Paint.ClearPen();
		Paint.SetBrush( _armed ? Theme.Blue.WithAlpha( 0.18f ) : Theme.ControlBackground );
		Paint.DrawRect( box, 2f );

		Paint.ClearBrush();
		Paint.SetPen( _armed ? Theme.Blue : Theme.TextControl.WithAlpha( 0.35f ) );
		Paint.DrawRect( box, 2f );

		Paint.SetDefaultFont( 9 );

		if ( _armed )
		{
			Paint.SetPen( Theme.Blue );
			Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ), "Click bodies in the viewport", TextFlag.LeftCenter );
			return;
		}

		Paint.SetPen( Theme.TextControl );

		var count = _param.BodyIds.Count;

		Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ),
			count == 0 ? "All bodies" : count == 1 ? "1 body" : $"{count} bodies", TextFlag.LeftCenter );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( e.LeftMouseButton )
			Toggle();
	}

	private void Toggle()
	{
		if ( _armed )
		{
			Disarm();
			return;
		}

		_armed = true;
		_viewport.BodyPickMode = true;
		Update();
	}

	private void Disarm()
	{
		_armed = false;
		_viewport.BodyPickMode = false;
		Update();
	}

	private void Clear()
	{
		_param.BodyIds.Clear();
		Disarm();
		_cleared?.Invoke();
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		if ( _armed )
			Disarm();
	}
}
