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

	// --- widgets ---
	private Widget _header;
	private LineEdit _nameEdit;
	private Widget _body;
	private readonly List<Widget> _rows = new();

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

		// An existing sketch already has its plane; a brand new one is waiting for you to pick.
		_planeChosen = !isNew;

		TakeSnapshot();

		_nameEdit.Text = feature.Name ?? feature.TypeName;

		Rebuild();

		Visible = true;
	}

	public new void Close()
	{
		_feature = null;
		_snapshot.Clear();
		ClearRows();
		Visible = false;

		_viewport.PlanePickMode = false;
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

	private Widget BuildFloatRow( FloatParam fp )
	{
		var row = NewRow( out var layout );
		layout.Add( new Editor.Label( fp.Label ) { FixedWidth = 110 } );

		var slider = new FloatSlider( row )
		{
			Minimum = Math.Max( fp.Min, -9999f ),
			Maximum = Math.Min( fp.Max, 9999f ),
			Step = fp.Unit == "deg" ? 5f : 0.1f,
			Value = fp.Clamped,
		};

		slider.OnValueEdited = () =>
		{
			fp.Value = slider.Value;
			Edited?.Invoke();
		};

		layout.Add( slider, 1 );

		if ( !string.IsNullOrEmpty( fp.Unit ) )
			layout.Add( new Editor.Label( fp.Unit ) { FixedWidth = 26 } );

		return row;
	}

	private Widget BuildIntRow( IntParam ip )
	{
		var row = NewRow( out var layout );
		layout.Add( new Editor.Label( ip.Label ) { FixedWidth = 110 } );

		var slider = new FloatSlider( row )
		{
			Minimum = ip.Min,
			Maximum = ip.Max,
			Step = 1f,
			Value = ip.Clamped,
		};

		slider.OnValueEdited = () =>
		{
			ip.Value = (int)slider.Value;
			Edited?.Invoke();
		};

		layout.Add( slider, 1 );
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

		AddAxis( sub, "X", vp.Value.x, v => vp.Value = new Vec3( v, vp.Value.y, vp.Value.z ) );
		AddAxis( sub, "Y", vp.Value.y, v => vp.Value = new Vec3( vp.Value.x, v, vp.Value.z ) );
		AddAxis( sub, "Z", vp.Value.z, v => vp.Value = new Vec3( vp.Value.x, vp.Value.y, v ) );

		layout.Add( sub );
		return row;
	}

	private void AddAxis( Widget parent, string label, float value, Action<float> set )
	{
		var slider = new FloatSlider( parent ) { Step = 0.1f, Value = value };

		slider.OnValueEdited = () =>
		{
			set( slider.Value );
			Edited?.Invoke();
		};

		parent.Layout.Add( new Editor.Label( label ) { FixedWidth = 12 } );
		parent.Layout.Add( slider, 1 );
	}

	private Widget BuildBodySelectionRow( BodySelectionParam bs )
	{
		var row = NewRow( out var layout );
		layout.Add( new Editor.Label( bs.Label ) { FixedWidth = 110 } );
		layout.Add( new Editor.Label( "All bodies" ) { Enabled = false }, 1 );
		return row;
	}
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
