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

	/// <summary>The consumed-sketch id when the dialog opened. It is a plain field on the
	/// feature rather than an IParam, so the generic snapshot cannot see it — Cancel has to put
	/// it back by hand or an abandoned pick would survive.</summary>
	private string _sketchIdSnapshot;

	/// <summary>
	/// Whether a sketch plane has actually been chosen.
	///
	/// This lives on the dialog rather than on the selection box because Rebuild() destroys and
	/// recreates every row. Held on the widget, it was reset to its default on the very next
	/// rebuild — and picking a plane triggers a rebuild — so the plane went in, the tree updated,
	/// and the box redrew itself empty a frame later.
	/// </summary>
	private bool _planeChosen;

	// --- widgets ---
	private Widget _header;
	private LineEdit _nameEdit;
	private Editor.Label _statusLabel;
	private Widget _body;
	private readonly List<Widget> _rows = new();

	/// <summary>
	/// One per editable row: pushes the parameter's CURRENT value back into that row's widgets.
	///
	/// Lets a value be driven from outside the dialog - a viewport gizmo, say - without rebuilding
	/// it, which would destroy every widget and throw away focus and any half-typed expression.
	/// </summary>
	private readonly List<Action> _valueRefreshers = new();

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

	/// <summary>Maps a SketchFeature id to its display name, for the sketch selection box.
	/// The window owns the studio, so it supplies the lookup.</summary>
	public Func<string, string> SketchNameLookup { get; set; }

	/// <summary>Raised with the feature the dialog just opened on, before any auto-arm reads
	/// the viewport's pick list. The pick list is relative to the feature being edited, so the
	/// window has to rebuild it against THIS feature right now — reading a list that was built
	/// for whatever the dialog was open on before is how a brand new Extrude sees zero sketches.</summary>
	public Action<Feature> OpenedForFeature { get; set; }

	/// <summary>The selection box currently in the dialog, if any. Only one kind exists per
	/// feature — a plane picker, or a sketch picker — so a single reference is enough for
	/// Escape to stand it down and for a brand new feature to auto-arm it.</summary>
	private IArmableSelection _activeArmable;

	public Feature Feature => _feature;
	public bool IsOpen => _feature is not null;

	public EffigyFeatureDialog( Widget parent, EffigyViewport viewport ) : base( parent )
	{
		_viewport = viewport;

		// Escape while a pick mode is armed comes through the viewport, which clears its own
		// flags and then tells the box here to repaint itself as disarmed.
		_viewport.PickModeCancelled = () => _activeArmable?.Disarm();

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

		_header.Layout.Add( new IconButton( "check", Accept )
		{
			ToolTip = "Accept (commit this feature)",
			IconSize = 16,
			Background = Color.Transparent,
		} );

		_header.Layout.Add( new IconButton( "close", Cancel )
		{
			ToolTip = "Cancel (discard changes)",
			IconSize = 16,
			Background = Color.Transparent,
		} );

		Layout.Add( _header );

		// Why the feature is unhappy, in words. A feature that quietly built from half its input,
		// or refused entirely, is the thing people spend evenings hunting.
		_statusLabel = new Editor.Label( "" );
		_statusLabel.Visible = false;
		Layout.Add( _statusLabel );
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
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( LocalRect );

		Paint.SetPen( Theme.WindowBackground );
		Paint.DrawLine( new Vector2( 0f, Height - 1f ), new Vector2( Width, Height - 1f ) );
	}

	// --- open / close -------------------------------------------------------------------------

	public void Open( Feature feature, bool isNew )
	{
		_feature = feature;
		_isNew = isNew;

		// The pick list is relative to the feature being edited, so the window rebuilds it for
		// this feature before anything below reads it — an auto-arm decision made against the
		// previous dialog's list is how a brand new Extrude saw zero sketches.
		OpenedForFeature?.Invoke( feature );

		// An existing sketch already has its plane; a brand new one is waiting for you to pick.
		_planeChosen = !isNew;

		TakeSnapshot();

		_nameEdit.Text = feature.Name ?? feature.TypeName;

		Rebuild();

		Visible = true;

		ArmPendingSelection( isNew );
	}

	/// <summary>
	/// A feature opens asking for its input, the way Sketch's plane box arms on a new sketch:
	///  - Sketch: arm the plane picker — it cannot exist without a plane.
	///  - Extrude/Revolve: a single available sketch is assigned outright on a brand new feature
	///    (asking would be theatre); otherwise, with no choice yet, the profile box arms and the
	///    sketches in the viewport are already hoverable and clickable either way.
	/// </summary>
	private void ArmPendingSelection( bool isNew )
	{
		if ( _feature is SketchFeature )
		{
			if ( isNew )
				_activeArmable?.Arm();

			return;
		}

		if ( _feature is SketchConsumingFeature consumer )
		{
			var hasChoice = !string.IsNullOrEmpty( consumer.SketchFeatureId )
				&& SketchNameLookup?.Invoke( consumer.SketchFeatureId ) is not null;

			var count = _viewport.PickableSketches.Count;

			if ( isNew && !hasChoice && count == 1 )
			{
				consumer.SketchFeatureId = _viewport.PickableSketches[0].FeatureId;
				Edited?.Invoke();
				Rebuild();
				return;
			}

			if ( !hasChoice && count > 0 )
				_activeArmable?.Arm();
		}
	}

	public new void Close()
	{
		_feature = null;
		_snapshot.Clear();
		ClearRows();
		Visible = false;

		_activeArmable = null;
		_viewport.PlanePickMode = false;
		_viewport.SketchPickMode = false;
		_viewport.SketchPicked = null;
		_viewport.SetPickPrompt( "" );
	}

	private void Accept()
	{
		if ( _feature is null )
			return;

		var feature = _feature;
		Close();
		Accepted?.Invoke( feature );
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
		_sketchIdSnapshot = null;

		if ( _feature is null )
			return;

		if ( _feature is SketchConsumingFeature consumer )
			_sketchIdSnapshot = consumer.SketchFeatureId;

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
		if ( _feature is SketchConsumingFeature consumer && _sketchIdSnapshot is not null )
			consumer.SketchFeatureId = _sketchIdSnapshot;

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

	/// <summary>Re-read every parameter into its row, for when something outside the dialog is
	/// driving a value.</summary>
	public void RefreshValues()
	{
		foreach ( var refresh in _valueRefreshers )
			refresh();
	}

	/// <summary>Rebuild the parameter rows. Called on open and whenever a choice changes the set of
	/// parameters — PrimitiveFeature.Parameters returns a different list per shape, so switching
	/// Box to Cylinder has to redraw the dialog, exactly as it does in Onshape.</summary>
	/// <summary>
	/// Show the feature's build state: an error in red, or a warning in yellow.
	///
	/// The two are deliberately different. An error means there is no geometry; a warning means
	/// there IS geometry but it was not built from everything you gave it - a stray line the
	/// profile finder would not guess at, say. Collapsing them into one colour is how "it built,
	/// but not from what you think" goes unnoticed.
	/// </summary>
	public void RefreshState()
	{
		if ( !_statusLabel.IsValid() )
			return;

		var error = _feature?.Error;
		var warning = _feature?.Warning;

		_statusLabel.Text = error ?? warning ?? "";
		_statusLabel.Color = error is not null ? Theme.Red : Theme.Yellow;
		_statusLabel.Visible = _statusLabel.Text.Length > 0;
	}

	public void Rebuild()
	{
		ClearRows();
		_activeArmable = null;

		// Sketch picking is live only while a consumer's dialog is open; the consumer branch
		// below turns it back on. Without this default, switching to a Sketch feature would
		// leave the previous dialog's pick handler wired into the viewport.
		_viewport.SketchPickMode = false;
		_viewport.SketchPicked = null;

		if ( _feature is null )
			return;

		// A sketch's plane is picked in the viewport, not chosen from a dropdown, so it gets a
		// selection box instead of the generic ChoiceParam row.
		if ( _feature is SketchFeature sketch )
		{
			var planeSelector = new EffigyPlaneSelector( _body, _viewport, sketch.Plane, OnPlaneChanged, _planeChosen );
			_activeArmable = planeSelector;
			AddRow( planeSelector );
			AddRow( BuildFloatRow( sketch.PlaneOffset ) );
			AddRow( BuildSketchButtonRow( sketch ) );
			return;
		}

		// Extrude/Revolve consume a sketch, and the profile is picked in the viewport the same
		// way a sketch's plane is. The Sketch ChoiceParam is storage plumbing the kernel never
		// reads a choice from, so it is swapped for a selection box instead of a dead dropdown.
		// While this dialog is open the sketches stay live in the viewport — hover highlights,
		// click picks — exactly like the reference planes while their box is armed.
		if ( _feature is SketchConsumingFeature consumer )
		{
			var sketchSelector = new EffigySketchSelector( _body, _viewport, consumer, SketchNameLookup, OnSketchPicked );
			_activeArmable = sketchSelector;
			AddRow( sketchSelector );

			_viewport.SketchPickMode = true;
			_viewport.SketchPicked = sketchSelector.Picked;

			foreach ( var param in _feature.Parameters.Where( p => !ReferenceEquals( p, consumer.Sketch ) ) )
				AddRow( BuildParamRow( param ) );

			return;
		}

		_viewport.SketchPickMode = false;
		_viewport.SketchPicked = null;

		foreach ( var param in _feature.Parameters )
			AddRow( BuildParamRow( param ) );
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

		Edited?.Invoke();
		Rebuild();

		if ( _feature is SketchFeature sketch )
			SketchRequested?.Invoke( sketch );
	}

	/// <summary>A sketch was picked in the viewport, so the consumer's input is chosen. Unlike
	/// a plane pick there is no second stage to drop into — the dialog stays open for the
	/// distance and direction parameters.</summary>
	private void OnSketchPicked()
	{
		Edited?.Invoke();
		Rebuild();
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
		layout.Add( new Editor.Label( "All bodies" ) { Enabled = false }, 1 );
		return row;
	}
}

/// <summary>A selection box the dialog can arm and disarm from the outside — so a brand new
/// feature can start waiting for a pick the moment it is added, and Escape can stand it down.</summary>
internal interface IArmableSelection
{
	void Arm();
	void Disarm();
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
internal sealed class EffigyPlaneSelector : Widget, IArmableSelection
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

		Paint.SetPen( Theme.TextLight );
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

		Arm();
	}

	public void Arm()
	{
		if ( _armed )
			return;

		_armed = true;
		_viewport.PlanePickMode = true;
		_viewport.PlanePicked = OnPicked;
		_viewport.SetPickPrompt( "Pick a plane in the viewport" );
		Update();
	}

	public void Disarm()
	{
		if ( !_armed )
			return;

		_armed = false;
		_viewport.PlanePickMode = false;
		_viewport.PlanePicked = null;
		_viewport.SetPickPrompt( "" );
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
/// The profile selection box for Extrude and Revolve — the same bordered field as the plane
/// selector, but picking one of the committed sketches drawn in the viewport instead of a
/// reference plane. This is the box a brand new Extrude opens with, armed and blue, because the
/// profile is the one thing the tool cannot guess.
/// </summary>
internal sealed class EffigySketchSelector : Widget, IArmableSelection
{
	private readonly EffigyViewport _viewport;
	private readonly SketchConsumingFeature _consumer;
	private readonly Func<string, string> _nameLookup;
	private readonly Action _changed;

	/// <summary>True while waiting for a viewport click. The box goes accent-coloured and the
	/// committed sketches become pickable.</summary>
	private bool _armed;

	public EffigySketchSelector( Widget parent, EffigyViewport viewport, SketchConsumingFeature consumer,
		Func<string, string> nameLookup, Action changed )
		: base( parent )
	{
		_viewport = viewport;
		_consumer = consumer;
		_nameLookup = nameLookup;
		_changed = changed;

		Layout = Layout.Row();
		Layout.Margin = new Sandbox.UI.Margin( 8, 3 );
		Layout.Spacing = 6;

		FixedHeight = 46f;
		Cursor = CursorShape.Finger;
	}

	/// <summary>The viewport click handler for this box's feature. The dialog wires it into the
	/// viewport for as long as it is open on the consumer — sketches stay pickable the whole
	/// time, armed or not, the way planes are while their box is armed.</summary>
	public Action<string> Picked => OnPicked;

	/// <summary>The name of the chosen sketch, or null when nothing valid is chosen. An id the
	/// tree no longer contains (the sketch was deleted) counts as nothing.</summary>
	private string ChosenName()
	{
		if ( string.IsNullOrEmpty( _consumer.SketchFeatureId ) )
			return null;

		return _nameLookup?.Invoke( _consumer.SketchFeatureId );
	}

	protected override void OnPaint()
	{
		var label = new Rect( 0f, 0f, Width, 16f );

		Paint.SetPen( Theme.TextLight );
		Paint.SetDefaultFont( 8 );
		Paint.DrawText( label.Shrink( 8f, 2f, 0f, 0f ), "Profile (sketch)", TextFlag.LeftTop );

		var box = new Rect( 8f, 18f, Width - 16f, 22f );
		var chosen = ChosenName();

		Paint.ClearPen();
		Paint.SetBrush( _armed ? Theme.Blue.WithAlpha( 0.18f ) : Theme.ControlBackground );
		Paint.DrawRect( box, 2f );

		Paint.ClearBrush();
		Paint.SetPen( _armed ? Theme.Blue : (chosen is not null ? Theme.TextControl.WithAlpha( 0.35f ) : Theme.Red.WithAlpha( 0.6f )) );
		Paint.DrawRect( box, 2f );

		Paint.SetDefaultFont( 9 );

		if ( _armed )
		{
			Paint.SetPen( Theme.Blue );
			Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ), "Pick a sketch in the viewport", TextFlag.LeftCenter );
		}
		else if ( chosen is not null )
		{
			Paint.SetPen( Theme.TextControl );
			Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ), chosen, TextFlag.LeftCenter );
		}
		else if ( _viewport.PickableSketches.Count == 0 )
		{
			Paint.SetPen( Theme.Red.WithAlpha( 0.8f ) );
			Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ), "No sketch yet — add a Sketch first", TextFlag.LeftCenter );
		}
		else
		{
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.45f ) );
			Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ), "Select a sketch", TextFlag.LeftCenter );
		}
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( !e.LeftMouseButton )
			return;

		if ( _armed )
		{
			Disarm();
			return;
		}

		// Nothing to pick — an empty red box has no waiting state to show.
		if ( _viewport.PickableSketches.Count == 0 )
			return;

		Arm();
	}

	public void Arm()
	{
		if ( _armed )
			return;

		_armed = true;
		_viewport.SetPickPrompt( "Pick the sketch to pull into a solid" );
		Update();
	}

	public void Disarm()
	{
		if ( !_armed )
			return;

		_armed = false;
		_viewport.SetPickPrompt( "" );
		Update();
	}

	private void OnPicked( string featureId )
	{
		_consumer.SketchFeatureId = featureId;

		Disarm();
		_changed?.Invoke();
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		// Leaving pick mode armed would make the sketches stay clickable after the dialog closed.
		if ( _armed )
			Disarm();
	}
}
