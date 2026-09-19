using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// The numbers a sculpt needs on screen: brush radius, strength, and which level is being worked.
///
/// WHY A SECOND ROW RATHER THAN MORE BUTTONS. The strips above are icons, and radius and strength
/// are values you type as often as you nudge — the same argument EffigyNumericField was built on for
/// feature dialogs. They also want a readout that says what the level costs, because levels are
/// exponential and the jump from 4 to 6 is sixteen times the vertices; a level control with no
/// number beside it is a way to hang the editor politely.
///
/// It sits under the sculpt strip the way the result strip sits under the feature strip: orthogonal
/// to the tool buttons rather than competing with them for the same spot.
/// </summary>
internal sealed class EffigySculptBar : Widget
{
	public const float BarHeight = 28f;

	private readonly EffigyNumericField _radius;
	private readonly EffigyNumericField _strength;
	private readonly ComboBox _falloff;
	private readonly Editor.Label _level;

	private SculptSession _session;

	/// <summary>Set while Refresh is writing the controls, so a ComboBox firing its own selection
	/// callback on AddItem does not read back as the user having chosen something.</summary>
	private bool _refreshing;

	/// <summary>Raised when something here changed a value the viewport should redraw for.</summary>
	public Action Changed { get; set; }

	public EffigySculptBar( Widget parent ) : base( parent )
	{
		// The same two flags every floating widget in this tool sets — a plain Widget paints the
		// system background, which is a white slab on the 3D view.
		TranslucentBackground = true;
		NoSystemBackground = true;

		Visible = false;
		FixedHeight = BarHeight;
		// Wide enough for the Falloff dropdown that joined the two stroke controls; the row does not
		// wrap, so a short bar clips the last thing on it rather than pushing it onto a second line.
		FixedWidth = 640f;

		Layout = Layout.Row();
		Layout.Spacing = 8;
		Layout.Margin = new Sandbox.UI.Margin( 0 );

		Layout.Add( new Editor.Label( "Radius" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_radius = new EffigyNumericField( this, 0.25f, "u" )
		{
			Min = 1e-4f,
			ValueEdited = OnRadiusEdited,
			FixedWidth = 90f,
		};

		Layout.Add( _radius );

		Layout.Add( new Editor.Label( "Strength" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_strength = new EffigyNumericField( this, 0.05f )
		{
			ValueEdited = OnStrengthEdited,
			FixedWidth = 90f,
		};

		Layout.Add( _strength );

		Layout.Add( new Editor.Label( "Falloff" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_falloff = new ComboBox( this )
		{
			MinimumWidth = 90f,
			ToolTip = "How the brush fades from its centre to its edge. Smooth is a soft mound; "
				+ "Sharp stays hard to the rim, for a crease; Linear fades evenly; Constant moves "
				+ "the whole disc by the same amount.",
		};

		Layout.Add( _falloff );

		_level = new Editor.Label( "" ) { Color = Theme.TextControl.WithAlpha( 0.75f ) };
		Layout.Add( _level, 1 );
	}

	/// <summary>The viewport's background, so the gaps between controls disappear into the 3D view.
	/// A floating strip cannot simply decline to paint: a rect a widget leaves alone keeps
	/// whatever the previous frame put there.</summary>
	public Color GapColor { get; set; } = Theme.ControlBackground;

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}

	public void Bind( SculptSession session )
	{
		_session = session;
		Visible = session is not null;

		if ( session is not null )
			Refresh();
	}

	/// <summary>Pull the readouts back into step after something else changed them — the X and M
	/// shortcuts, or a level button.</summary>
	public void Refresh()
	{
		if ( _session is null )
			return;

		_radius.SetValue( _session.Radius );
		_strength.SetValue( _session.Strength );

		RefreshFalloff();

		var (vertices, faces) = _session.Cost( _session.Level );
		var top = _session.Sculpt.TopLevel;

		// The cost is stated outright, in the same spirit as the viewport's own "N units" readout:
		// this is the only place the price of the next level is visible before it is paid.
		var text = $"Level {_session.Level} of {top} · {vertices:N0} verts / {faces:N0} faces";

		// The view is not the model — see SculptFeature. Saying so here is what stops "I dropped to
		// L1 to work coarsely" turning into "my export lost the detail".
		if ( _session.Level < top )
			text += $" · showing {_session.Level}, model builds at {top}";

		if ( _session.Mirror != MirrorAxis.None )
			text += " · mirrored";

		if ( _session.Masking )
			text += _session.Erasing ? " · unmasking" : " · masking";
		else if ( _session.ActiveMask is not null )
			text += $" · {_session.ActiveMask.ProtectedFraction:P0} held";

		_level.Text = text;
	}

	/// <summary>
	/// Rebuilt rather than re-selected, because ComboBox has no "set index without telling anyone"
	/// and AddItem takes the selected flag. The guard is what keeps that from writing the value
	/// back onto the session and firing Changed for a control nobody touched.
	/// </summary>
	private void RefreshFalloff()
	{
		if ( _falloff is null )
			return;

		_refreshing = true;

		try
		{
			_falloff.Clear();

			foreach ( var falloff in Enum.GetValues<BrushFalloff>() )
			{
				var kind = falloff;

				_falloff.AddItem( kind.ToString(), onSelected: () => OnFalloffPicked( kind ),
					selected: kind == _session.Falloff );
			}
		}
		finally
		{
			_refreshing = false;
		}
	}

	private void OnFalloffPicked( BrushFalloff kind )
	{
		if ( _refreshing || _session is null || _session.Falloff == kind )
			return;

		// A brush setting, not a document edit: it redraws the viewport through Changed and must
		// not mark the feature tree unsaved, the way Radius and Strength do not.
		_session.Falloff = kind;
		Changed?.Invoke();
	}

	private void OnRadiusEdited( float value )
	{
		if ( _session is null )
			return;

		// Clamped rather than refused: a zero radius makes BeginStroke throw, and a tool that throws
		// because somebody cleared a box is not a tool.
		_session.Radius = MathF.Max( value, 1e-4f );
		Changed?.Invoke();
	}

	private void OnStrengthEdited( float value )
	{
		if ( _session is null )
			return;

		_session.Strength = value;
		Changed?.Invoke();
	}
}

/// <summary>
/// Edit mode's floating bar: what is selected, and the operation you are tuning.
///
/// THE OPERATION STAYS OPEN UNTIL DONE. Blender's redo panel folds into a corner and vanishes the
/// moment you click anything else; here the last operation's number sits in plain view and updates
/// the mesh live — every edit re-runs the operation from the state before it through
/// <see cref="MeshEditSession.Preview"/> — until Done (Enter) or Cancel (Escape). Either way it is
/// one undo step.
/// </summary>
internal sealed class EffigyMeshEditBar : Widget
{
	public const float BarHeight = 28f;

	private readonly Editor.Label _info;
	private readonly Editor.Label _hints;
	private readonly Editor.Label _operation;
	private readonly EffigyNumericField _amount;
	private readonly Button _done;
	private readonly Button _cancel;

	private MeshEditSession _session;
	private Action<float> _amountChanged;

	/// <summary>Done was pressed. The window accepts the preview.</summary>
	public Action Accepted { get; set; }

	/// <summary>Cancel was pressed. The window cancels the preview.</summary>
	public Action Cancelled { get; set; }

	public EffigyMeshEditBar( Widget parent ) : base( parent )
	{
		TranslucentBackground = true;
		NoSystemBackground = true;

		Visible = false;
		FixedHeight = BarHeight;
		FixedWidth = 700f;

		Layout = Layout.Row();
		Layout.Spacing = 8;
		Layout.Margin = new Sandbox.UI.Margin( 8, 0, 8, 0 );

		_operation = new Editor.Label( "" ) { Color = Theme.Text };
		Layout.Add( _operation );

		_amount = new EffigyNumericField( this, 0.1f, "u" )
		{
			ValueEdited = v => _amountChanged?.Invoke( v ),
			FixedWidth = 90f,
		};
		Layout.Add( _amount );

		_done = new Button( "Done" ) { Clicked = () => Accepted?.Invoke(), ToolTip = "Keep this (Enter)" };
		_cancel = new Button( "Cancel" ) { Clicked = () => Cancelled?.Invoke(), ToolTip = "Put the mesh back as it was (Escape)" };
		Layout.Add( _done );
		Layout.Add( _cancel );

		_info = new Editor.Label( "" ) { Color = Theme.TextControl.WithAlpha( 0.85f ) };
		Layout.Add( _info, 1 );

		// What the mouse does here, at the right-hand end — so nobody has to know that the right
		// button is a menu or that Space finds any tool.
		_hints = new Editor.Label( "" ) { Color = Theme.TextControl.WithAlpha( 0.5f ) };
		Layout.Add( _hints );

		ShowOperation( null, 0f, null );
	}

	public Color GapColor { get; set; } = Theme.ControlBackground;

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}

	public void Bind( MeshEditSession session )
	{
		_session = session;
		Visible = session is not null;

		if ( session is null )
			ShowOperation( null, 0f, null );

		Refresh();
	}

	/// <summary>Show an operation's number, or hide it with a null name.</summary>
	public void ShowOperation( string name, float value, Action<float> changed )
	{
		_amountChanged = changed;

		var open = name is not null;
		_operation.Visible = open;
		_amount.Visible = open;
		_done.Visible = open;
		_cancel.Visible = open;

		if ( open )
		{
			_operation.Text = name;
			_amount.SetValue( value );
		}
	}

	public void Refresh()
	{
		if ( _session is not { } s )
			return;

		var (what, count) = s.Mode switch
		{
			EditElement.Vertex => ("vertices", s.SelectedVertices.Count),
			EditElement.Edge => ("edges", s.SelectedEdges.Count),
			_ => ("faces", s.SelectedFaces.Count),
		};

		var text = count == 0
			? $"Nothing selected — click {what} to pick them"
			: $"{count} {what} selected";

		_hints.Text = s.IsRetopologizing
			? "Ctrl+click places · Ctrl+drag lays a strip (Strip brush) · Right-click: menu · Space: search"
			: "Click: select · Shift: add · Drag: box · Alt+click: loop · Right-click: menu · Space: search";

		text += $" · {s.Mesh.VertexCount:N0} verts / {s.Mesh.FaceCount:N0} faces";

		if ( s.MirrorX )
			text += " · mirror X";

		if ( s.Separated.Count > 0 )
			text += $" · {s.Separated.Count} piece(s) split off";

		if ( s.SoftRadius > 0f )
			text += $" · soft {s.SoftRadius:0.##}{(s.SoftConnected ? " connected" : "")}{(s.SoftShape != MeshEditSession.SoftFalloff.Smooth ? $" {s.SoftShape}" : "")}";

		if ( s.SnapTarget is not null )
			text += " · snapping to surface";

		var check = MeshValidator.Validate( s.Mesh );
		if ( check.BoundaryEdges > 0 )
			text += $" · {check.BoundaryEdges} open edges";
		if ( check.NonManifoldEdges > 0 )
			text += $" · {check.NonManifoldEdges} non-manifold";

		_info.Text = text;
	}
}
