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
	private Widget _statusSlot;
	private Widget _body;
	private readonly List<Widget> _rows = new();
	private readonly Dictionary<string, HighlightBox> _paramHighlights = new();

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

	/// <summary>The material a slot currently carries, or null. The studio owns MaterialNames, so
	/// the window supplies this the same way it supplies the sketch names.</summary>
	public Func<int, string> MaterialLookup { get; set; }

	/// <summary>A material was picked for a slot from inside this dialog. It is a studio edit, not a
	/// parameter edit — it changes what slot 3 means everywhere, not what this feature does — so it
	/// goes straight to the window rather than through the dialog's own accept/cancel. Cancelling a
	/// face-material feature you were in the middle of does not un-pick the material, and should
	/// not: the slot outlives the feature.</summary>
	public Action<int, string> MaterialChanged { get; set; }

	/// <summary>Raised with the feature the dialog just opened on, before any auto-arm reads
	/// the viewport's pick list. The pick list is relative to the feature being edited, so the
	/// window has to rebuild it against THIS feature right now — reading a list that was built
	/// for whatever the dialog was open on before is how a brand new Extrude sees zero sketches.</summary>
	public Action<Feature> OpenedForFeature { get; set; }

	/// <summary>The selection box currently in the dialog, if any. Only one kind exists per
	/// feature — a plane picker, or a sketch picker — so a single reference is enough for
	/// Escape to stand it down and for a brand new feature to auto-arm it.</summary>
	private IArmableSelection _activeArmable;

	/// <summary>The material row on a face-material dialog, and the slot it was built for. Kept
	/// because the row belongs to a SLOT while the dialog belongs to a FEATURE, and the feature's
	/// slot number is itself editable a row above — typing 3 over 1 has to move the row onto slot 3
	/// or it would keep offering to repaint the slot you just left.</summary>
	private Widget _materialRow;
	private int _materialRowSlot = -1;

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

			// Green, like every confirm in the tool - see EffigyToolStrip.ConfirmColor.
			Foreground = EffigyToolStrip.ConfirmColor,
		} );

		_header.Layout.Add( new IconButton( "close", Cancel )
		{
			ToolTip = "Cancel (discard changes)",
			IconSize = 16,
			Background = Color.Transparent,
		} );

		Layout.Add( _header );

		// Why the feature is unhappy, in three parts: problem, cause with this model's numbers,
		// and what to do. Built on open and rebuilt on every live edit, because the numbers change
		// as you drag.
		_statusSlot = new Widget( this ) { Layout = Layout.Column() };
		Layout.Add( _statusSlot );
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

		// A face material with nothing picked cannot do anything at all, so it opens asking - the
		// same reasoning as a brand new sketch opening with its plane box armed.
		if ( _feature is FaceMaterialFeature material )
		{
			if ( material.Faces.Count == 0 )
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
		_viewport.FacePickMode = false;
		_viewport.FacePicked = null;
		_viewport.SketchPickMode = false;
		_viewport.SketchPicked = null;
		_viewport.BodyPickMode = false;
		_viewport.BodyPicked = null;
		_viewport.SelectedBodyIds = null;
		_viewport.SelectedFaces = null;
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
		_paramHighlights.Clear();

		_materialRow = null;
		_materialRowSlot = -1;
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
		SyncMaterialRow();
		RebuildStatus();

		foreach ( var box in _paramHighlights.Values )
			box.Highlighted = false;

		var diagnostic = _feature?.Diagnostic;

		if ( diagnostic?.ParameterLabel is { } label
			&& _paramHighlights.TryGetValue( label, out var highlight ) )
		{
			highlight.Highlighted = true;
			highlight.Color = diagnostic.Severity == DiagnosticSeverity.Error ? Theme.Red : Theme.Yellow;
		}
	}

	void RebuildStatus()
	{
		if ( !_statusSlot.IsValid() )
			return;

		_statusSlot.Layout.Clear( true );

		var diagnostic = _feature?.Diagnostic;

		if ( diagnostic is not null && !string.IsNullOrEmpty( diagnostic.Problem ) )
		{
			FillDiagnostic( diagnostic );
			_statusSlot.Visible = true;
			return;
		}

		var error = _feature?.Error;
		var warning = _feature?.Warning;
		var text = error ?? warning ?? "";

		if ( text.Length == 0 )
		{
			_statusSlot.Visible = false;
			return;
		}

		var label = new Editor.Label( text ) { WordWrap = true };
		label.Color = error is not null ? Theme.Red : Theme.Yellow;
		_statusSlot.Layout.Add( label );
		_statusSlot.Visible = true;
	}

	void FillDiagnostic( FeatureDiagnostic diagnostic )
	{
		_statusSlot.Layout.Margin = new Sandbox.UI.Margin( 8, 4, 8, 6 );
		_statusSlot.Layout.Spacing = 3;

		var severity = diagnostic.Severity == DiagnosticSeverity.Error ? Theme.Red : Theme.Yellow;

		var problem = new Editor.Label( diagnostic.Problem ) { WordWrap = true };
		problem.Color = severity;
		problem.SetStyles( "font-weight: bold;" );
		_statusSlot.Layout.Add( problem );

		if ( !string.IsNullOrEmpty( diagnostic.Cause ) )
		{
			var cause = new Editor.Label( diagnostic.Cause ) { WordWrap = true };
			cause.Color = Theme.TextLight;
			_statusSlot.Layout.Add( cause );
		}

		var firstRemedyIsButton = diagnostic.SuggestedValue is not null
			&& !string.IsNullOrEmpty( diagnostic.ParameterLabel )
			&& diagnostic.Remedies.Count > 0;

		for ( var i = 0; i < diagnostic.Remedies.Count; i++ )
		{
			var remedy = diagnostic.Remedies[i];

			if ( i == 0 && firstRemedyIsButton )
			{
				var captured = diagnostic;
				_statusSlot.Layout.Add( new Button( remedy )
				{
					Clicked = () => ApplySuggested( captured )
				} );
				continue;
			}

			var item = new Editor.Label( "• " + remedy ) { WordWrap = true };
			item.Color = Theme.TextLight;
			_statusSlot.Layout.Add( item );
		}
	}

	void ApplySuggested( FeatureDiagnostic diagnostic )
	{
		if ( _feature is null || diagnostic.SuggestedValue is not float value )
			return;

		foreach ( var p in _feature.Parameters )
		{
			if ( p.Label != diagnostic.ParameterLabel )
				continue;

			switch ( p )
			{
				case FloatParam fp:
					fp.Value = value;
					break;
				case IntParam ip:
					ip.Value = (int)MathF.Round( value );
					break;
				default:
					return;
			}

			Edited?.Invoke();
			RefreshValues();
			return;
		}
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
			// Feed the pickable bodies in fresh every time the dialog rebuilds - the studio may
			// have gained or lost bodies since this feature was last open, and a stale list would
			// let a click resolve against a body that no longer exists.
			_viewport.SetPickableBodies( _pickableBodiesLookup?.Invoke() );

			var faceLabel = sketch.Face is not null ? FaceLabel( sketch ) : null;

			var planeSelector = new EffigyPlaneSelector( _body, _viewport, sketch.Plane, OnPlaneChanged,
				_planeChosen, OnFaceChanged, faceLabel );
			_activeArmable = planeSelector;
			AddRow( planeSelector );
			AddRow( BuildFloatRow( sketch.PlaneOffset ) );
			AddRow( BuildSketchButtonRow( sketch ) );
			return;
		}

		// A face material is a set of picked faces plus a slot. The faces have no IParam - a list of
		// picked geometry has no generic control the way a float or a choice does - so the box comes
		// first and the generic rows follow it.
		if ( _feature is FaceMaterialFeature material )
		{
			_viewport.SetPickableBodies( _pickableBodiesLookup?.Invoke() );

			var faceSelector = new EffigyFaceSetSelector( _body, _viewport, material, OnFaceSetChanged );
			_activeArmable = faceSelector;
			AddRow( faceSelector );

			foreach ( var param in _feature.Parameters )
				AddRow( BuildParamRow( param ) );

			// Under the slot number, because it is the answer to the question the number raises.
			// Picking a slot in a dialog and having no idea what it looks like is the reason this
			// control exists at all.
			AddRow( BuildMaterialRow( material ) );

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

		// A plane click and a face click are mutually exclusive ways to answer the same question,
		// so choosing one clears the other rather than leaving a stale Face reference that
		// ResolveBasePlane would prefer over the plane just picked.
		if ( _feature is SketchFeature clearedFace )
			clearedFace.Face = null;

		Edited?.Invoke();
		Rebuild();

		if ( _feature is SketchFeature sketch )
			SketchRequested?.Invoke( sketch );
	}

	/// <summary>A face of an existing body was picked, so the sketch is fully specified the same
	/// way a plane pick specifies it - straight into sketch mode.</summary>
	private void OnFaceChanged( FaceRef face )
	{
		_planeChosen = true;

		if ( _feature is not SketchFeature sketch )
			return;

		sketch.Face = face;

		Edited?.Invoke();
		Rebuild();

		SketchRequested?.Invoke( sketch );
	}

	/// <summary>Where a chosen face is reported which body it came from. Falls back to the raw id
	/// if the body has since been renamed or removed - a lookup failing here should not make the
	/// dialog throw, only say something slightly less specific.</summary>
	private string FaceLabel( SketchFeature sketch )
	{
		var name = sketch.Face is { } f ? _bodyNameLookup?.Invoke( f.BodyId ) : null;
		return name is not null ? $"Face of {name}" : "Face of an existing part";
	}

	/// <summary>Supplies the bodies that can be clicked for a face - the window owns the studio.</summary>
	public Func<IEnumerable<Body>> PickableBodiesLookup
	{
		get => _pickableBodiesLookup;
		set => _pickableBodiesLookup = value;
	}

	private Func<IEnumerable<Body>> _pickableBodiesLookup;

	/// <summary>Maps a body id to its display name, for the face label. Same shape as
	/// SketchNameLookup.</summary>
	public Func<string, string> BodyNameLookup
	{
		get => _bodyNameLookup;
		set => _bodyNameLookup = value;
	}

	private Func<string, string> _bodyNameLookup;

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

	/// <summary>
	/// The shared slot control, in a container the dialog can re-fill when the slot number changes.
	///
	/// The container is the indirection that matters: EffigyMaterialSlot is built for one slot and
	/// stays on it, which is right in the Materials panel where a row IS a slot, and wrong here
	/// where the slot is a parameter you can type over. Rebuilding the child rather than the whole
	/// dialog keeps the number field's focus and any half-typed expression in the rows above.
	/// </summary>
	private Widget BuildMaterialRow( FaceMaterialFeature material )
	{
		_materialRow = NewRow( out var layout );
		_materialRowSlot = material.Material.Clamped;

		layout.Add( new Editor.Label( "Material" ) { FixedWidth = 110 } );
		layout.Add( NewMaterialSlot( _materialRow, _materialRowSlot, showSlotLabel: false ), 1 );

		return _materialRow;
	}

	private EffigyMaterialSlot NewMaterialSlot( Widget parent, int slot, bool showSlotLabel ) =>
		new( parent, slot, MaterialLookup?.Invoke( slot ), showSlotLabel )
		{
			Changed = ( s, path ) => MaterialChanged?.Invoke( s, path ),
		};

	/// <summary>
	/// Put the material row back on the slot the feature now paints, if either has moved.
	///
	/// Called from RefreshState, which runs after every rebuild — the one moment that covers both
	/// ways this row goes stale: the slot number edited here, and the same slot given a different
	/// material from the Materials panel while this dialog sits open.
	/// </summary>
	private void SyncMaterialRow()
	{
		if ( _feature is not FaceMaterialFeature material || !_materialRow.IsValid() )
			return;

		var slot = material.Material.Clamped;
		var current = MaterialLookup?.Invoke( slot );

		if ( slot == _materialRowSlot )
		{
			foreach ( var child in _materialRow.Children.OfType<EffigyMaterialSlot>() )
				child.Refresh( current );

			return;
		}

		_materialRowSlot = slot;
		_materialRow.Layout.Clear( true );
		_materialRow.Layout.Add( new Editor.Label( "Material" ) { FixedWidth = 110 } );
		_materialRow.Layout.Add( NewMaterialSlot( _materialRow, slot, showSlotLabel: false ), 1 );
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

	private Widget NewRow( out Layout layout, bool column = false, string highlightLabel = null )
	{
		var row = new HighlightBox( _body ) { Layout = column ? Layout.Column() : Layout.Row() };
		row.Layout.Margin = new Sandbox.UI.Margin( 8, 3 );
		row.Layout.Spacing = 6;
		layout = row.Layout;

		if ( highlightLabel is not null )
			_paramHighlights[highlightLabel] = row;

		return row;
	}

	/// <summary>
	/// Whether a parameter's own bounds make a slider worth showing next to the field.
	///
	/// Most of Effigy's lengths declare min 0.0001 and no maximum at all (BasicFeatures.cs), and
	/// the version this replaces invented a -9999..9999 range for them. A slider spanning five
	/// orders of magnitude at 0.1 per step cannot be aimed at a value; it only looks like a
	/// control. Chamfer's 0..180 angle threshold and Subdivide's 0..6 levels are real ranges, and
	/// those are the ones worth dragging.
	/// </summary>
	private static bool Draggable( float min, float max ) =>
		min > float.MinValue && max < float.MaxValue && max - min <= 1024f;

	/// <summary>Effigy's lengths are dimensionless, so FloatParam's "u" is decoration rather than
	/// a unit. Real units - "deg" - still earn their label.</summary>
	private static bool ShowUnit( string unit ) => !string.IsNullOrEmpty( unit ) && unit != "u";

	private Widget BuildFloatRow( FloatParam fp )
	{
		var row = NewRow( out var layout, highlightLabel: fp.Label );
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
		var row = NewRow( out var layout, highlightLabel: ip.Label );
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

			// Ticking "uniform" squares the axes up straight away, off X, rather than waiting for
			// the next edit. A box that says the scale is locked while showing three different
			// numbers is telling you something that is not true.
			if ( toggle.Value && _feature is PrimitiveFeature p && ReferenceEquals( bp, p.UniformScale ) )
			{
				var x = p.Scale.Value.x;

				if ( p.Scale.Value.y != x || p.Scale.Value.z != x )
				{
					p.Scale.Value = new Vec3( x, x, x );

					// The three fields are built widgets holding their own text; a rebuild is the
					// honest way to get them showing the new value.
					Rebuild();
				}
			}

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

	/// <summary>
	/// The "keep the axes equal" flag governing a Vec3, or null where there is none.
	///
	/// Matched by REFERENCE against the parameter it belongs to rather than by its label. A name
	/// test would tie the behaviour to the words on screen, and two features with a "Scale" would
	/// then share a flag only one of them has.
	/// </summary>
	private BoolParam UniformFor( Vec3Param vp ) => _feature switch
	{
		PrimitiveFeature p when ReferenceEquals( vp, p.Scale ) => p.UniformScale,
		_ => null,
	};

	private Widget BuildVec3Row( Vec3Param vp )
	{
		var row = NewRow( out var layout, column: true, highlightLabel: vp.Label );
		layout.Add( new Editor.Label( vp.Label ) );

		var sub = new Widget( row ) { Layout = Layout.Row() };
		sub.Layout.Spacing = 4;

		var uniform = UniformFor( vp );
		var fields = new EffigyNumericField[3];

		// Writes one axis, or all three when the row is locked uniform, and then puts the result
		// into the OTHER two fields. Never into the field the edit came from: that one is either
		// being typed in, where rewriting the text mid-keystroke would fight the cursor, or being
		// dragged, where the handle has already updated it.
		void Set( int axis, float value )
		{
			var current = vp.Value;

			vp.Value = uniform is { Value: true }
				? new Vec3( value, value, value )
				: axis switch
				{
					0 => new Vec3( value, current.y, current.z ),
					1 => new Vec3( current.x, value, current.z ),
					_ => new Vec3( current.x, current.y, value ),
				};

			var now = vp.Value;

			for ( var i = 0; i < 3; i++ )
			{
				if ( i == axis )
					continue;

				fields[i]?.SetValue( i == 0 ? now.x : i == 1 ? now.y : now.z );
			}
		}

		fields[0] = AddAxis( sub, "X", Theme.Red, () => vp.Value.x, v => Set( 0, v ) );
		fields[1] = AddAxis( sub, "Y", Theme.Green, () => vp.Value.y, v => Set( 1, v ) );
		fields[2] = AddAxis( sub, "Z", Theme.Blue, () => vp.Value.z, v => Set( 2, v ) );

		layout.Add( sub );
		return row;
	}

	/// <summary>
	/// One axis of a Vec3: a draggable coloured letter and the field it drives.
	///
	/// The two share the parameter rather than each other. Dragging writes through <paramref
	/// name="set"/> and then pushes the result into the field with SetValue, which deliberately
	/// does NOT fire ValueEdited — otherwise the field would echo the drag straight back out and
	/// the two would drive each other round in a loop. Same reasoning as the paired slider that
	/// EffigyNumericField already documents.
	/// </summary>
	private EffigyNumericField AddAxis( Widget parent, string label, Color colour, Func<float> get, Action<float> set )
	{
		var field = new EffigyNumericField( parent, get() );

		field.ValueEdited = v =>
		{
			set( v );
			Edited?.Invoke();
		};

		var handle = new EffigyAxisHandle( parent, label, colour )
		{
			Value = get,
			Dragged = v =>
			{
				set( v );

				// The typed field has to follow the drag, or the number on screen goes stale the
				// moment you scrub and the next keystroke edits a value nobody is looking at.
				field.SetValue( v );

				Edited?.Invoke();
			},
		};

		parent.Layout.Add( handle );
		parent.Layout.Add( field, 1 );

		return field;
	}

	/// <summary>
	/// Which bodies the feature acts on.
	///
	/// This was a disabled label reading "All bodies", and had been since the parameter was added:
	/// the kernel honoured BodySelectionParam.Matches everywhere, and there was no way to put
	/// anything into it. Eight features carry one — shell, bevel, subdivide, transform, mirror,
	/// linear and circular pattern, UV project — so a single missing control was the difference
	/// between all eight acting on what you meant and all eight acting on everything.
	///
	/// It is now a selection box on the same pattern as the plane and profile ones, because from
	/// the user's side these are one gesture: arm the box, click the thing in the viewport.
	/// </summary>
	private Widget BuildBodySelectionRow( BodySelectionParam bs )
	{
		// Same reasoning as the plane box: refresh the pick list against the studio as it is now,
		// or a click can resolve against a body that has since been rebuilt away.
		_viewport.SetPickableBodies( _pickableBodiesLookup?.Invoke() );

		var selector = new EffigyBodySelector( _body, _viewport, bs, _pickableBodiesLookup, OnBodySelectionChanged );

		// The dialog tracks one armable so Escape can stand it down. A feature with a body
		// selection never also has a plane or profile box, so there is nothing to displace.
		_activeArmable ??= selector;

		return selector;
	}

	/// <summary>A face was added to or removed from a face-material assignment. Same as any other
	/// parameter edit: the feature's inputs changed, so it re-runs.</summary>
	private void OnFaceSetChanged()
	{
		Edited?.Invoke();
		Rebuild();
	}

	/// <summary>A body was added to or removed from a selection: the feature's inputs changed, so
	/// it has to be marked dirty and re-run like any other parameter edit.</summary>
	private void OnBodySelectionChanged()
	{
		Edited?.Invoke();
		Rebuild();
	}
}

/// <summary>A parameter row that can draw a ring when its diagnostic names this control.</summary>
internal sealed class HighlightBox : Widget
{
	public bool Highlighted;
	public Color Color = Theme.Red;

	public HighlightBox( Widget parent ) : base( parent ) { }

	protected override void OnPaint()
	{
		if ( !Highlighted )
			return;

		Paint.SetPen( Color, 1.5f );
		Paint.ClearBrush();
		Paint.DrawRect( LocalRect, 2f );
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

	/// <summary>Set when the sketch is also allowed to sit on a face of an existing solid — only
	/// true for SketchFeature's own dialog. Extrude/Revolve reuse none of this box.</summary>
	private readonly Action<FaceRef> _faceChosen;
	private readonly Action _changed;

	/// <summary>True while waiting for a viewport click. The box goes accent-coloured and the
	/// three reference planes — plus, when offered, every pickable body's faces — become clickable
	/// at once. One click resolves to whichever of the two was actually hit; there is no separate
	/// mode to switch between them, the same way Onshape's plane selection never asks "plane or
	/// face?" before you point at something.</summary>
	private bool _armed;

	/// <summary>Whether a plane OR a face has actually been chosen. A fresh Sketch has Plane.Index
	/// 0 by default, which is a value but not a choice — showing "Top (XY)" in the box before the
	/// user picked anything would be a lie, and would hide that the feature is waiting on them.</summary>
	private bool _chosen;

	/// <summary>What the box currently reads, once something is chosen — "Top (XY)" or "Face of
	/// Box 1".</summary>
	private string _chosenLabel;

	public EffigyPlaneSelector( Widget parent, EffigyViewport viewport, ChoiceParam plane, Action changed,
		bool chosen, Action<FaceRef> faceChosen = null, string chosenFaceLabel = null )
		: base( parent )
	{
		_viewport = viewport;
		_plane = plane;
		_changed = changed;
		_chosen = chosen;
		_faceChosen = faceChosen;
		_chosenLabel = chosenFaceLabel ?? (chosen ? plane.Value : null);

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

			var prompt = _faceChosen is not null
				? "Pick a plane, or click a face of an existing part"
				: "Pick a plane in the viewport";

			Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ), prompt, TextFlag.LeftCenter );
		}
		else if ( _chosen )
		{
			Paint.SetPen( Theme.TextControl );
			Paint.DrawText( box.Shrink( 6f, 0f, 0f, 0f ), _chosenLabel ?? _plane.Value, TextFlag.LeftCenter );
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
		_viewport.PlanePicked = OnPlanePicked;

		// Faces only get wired up for SketchFeature's own box - Extrude and Revolve pass no
		// faceChosen callback and get exactly the old three-plane behaviour.
		if ( _faceChosen is not null )
		{
			_viewport.FacePickMode = true;
			_viewport.FacePicked = OnFacePicked;
		}

		_viewport.SetPickPrompt( _faceChosen is not null
			? "Pick a plane, or click a face of an existing part"
			: "Pick a plane in the viewport" );

		Update();
	}

	public void Disarm()
	{
		if ( !_armed )
			return;

		_armed = false;
		_viewport.PlanePickMode = false;
		_viewport.PlanePicked = null;
		_viewport.FacePickMode = false;
		_viewport.FacePicked = null;
		_viewport.SetPickPrompt( "" );
		Update();
	}

	private void OnPlanePicked( int index )
	{
		_plane.Index = index;
		_chosen = true;
		_chosenLabel = null;
		_viewport.IgnoreNextSketchClick();

		Disarm();
		_changed?.Invoke();
	}

	private void OnFacePicked( FaceRef face )
	{
		_chosen = true;
		_chosenLabel = "Face of an existing part";
		_viewport.IgnoreNextSketchClick();

		Disarm();
		_faceChosen?.Invoke( face );
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		// Leaving pick mode armed would make the planes and faces stay clickable after the dialog
		// closed.
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

/// <summary>
/// Which bodies a feature acts on, chosen by clicking them.
///
/// Multi-select, and that is the whole reason it is not a copy of the plane box. A plane question
/// has exactly one answer and the box closes the moment you give it; a body question has any number
/// of answers, so a click TOGGLES a body and the box stays armed until you dismiss it. Escape or a
/// second click on the box ends the pick.
///
/// Empty means every body — BodySelectionParam.Matches is written that way, and it is the sane
/// default for a studio holding one part. So the empty box reads "All bodies" rather than looking
/// like an unanswered question: unlike the plane box, nothing is being withheld while it is empty.
/// </summary>
internal sealed class EffigyBodySelector : Widget, IArmableSelection
{
	private readonly EffigyViewport _viewport;
	private readonly BodySelectionParam _param;
	private readonly Func<IEnumerable<Body>> _bodies;
	private readonly Action _changed;

	private bool _armed;

	public EffigyBodySelector( Widget parent, EffigyViewport viewport, BodySelectionParam param,
		Func<IEnumerable<Body>> bodies, Action changed ) : base( parent )
	{
		_viewport = viewport;
		_param = param;
		_bodies = bodies;
		_changed = changed;

		Layout = Layout.Row();
		Layout.Margin = new Sandbox.UI.Margin( 8, 3 );
		Layout.Spacing = 6;

		FixedHeight = 46f;
		Cursor = CursorShape.Finger;
	}

	/// <summary>What the box reads: the chosen bodies by name, or a count once there are too many
	/// to fit. Names come from the studio rather than being stored, so a renamed body reads
	/// correctly without the selection knowing anything about it.</summary>
	private string SelectionLabel()
	{
		if ( _param.BodyIds.Count == 0 )
			return "All bodies";

		if ( _param.BodyIds.Count > 3 )
			return $"{_param.BodyIds.Count} bodies";

		var known = _bodies?.Invoke()?.ToList() ?? new List<Body>();
		var names = _param.BodyIds.Select( id =>
			known.FirstOrDefault( b => b.Id == id )?.Name ?? id );

		return string.Join( ", ", names );
	}

	/// <summary>The clear affordance's hit box, and where it is painted. Only live when there is a
	/// selection to clear.</summary>
	private Rect ClearRect() => new( Width - 26f, 18f, 18f, 22f );

	protected override void OnPaint()
	{
		var chosen = _param.BodyIds.Count > 0;

		Paint.SetPen( Theme.TextControl.WithAlpha( 0.7f ) );
		Paint.SetDefaultFont( 8 );
		Paint.DrawText( new Rect( 0f, 0f, Width, 16f ).Shrink( 8f, 2f, 0f, 0f ), _param.Label, TextFlag.LeftTop );

		var box = new Rect( 8f, 18f, Width - 16f, 22f );

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
			Paint.DrawText( box.Shrink( 6f, 0f, 30f, 0f ),
				chosen ? $"{SelectionLabel()} — click to add or remove" : "Click the bodies this acts on",
				TextFlag.LeftCenter );
		}
		else
		{
			// "All bodies" is a real answer, not a blank, so it is drawn dimmed rather than in the
			// red an unanswered required box gets.
			Paint.SetPen( chosen ? Theme.TextControl : Theme.TextControl.WithAlpha( 0.45f ) );
			Paint.DrawText( box.Shrink( 6f, 0f, 30f, 0f ), SelectionLabel(), TextFlag.LeftCenter );
		}

		if ( !chosen )
			return;

		// DrawIcon with a CLASSIC Material Icons name, not a literal glyph and not a Material
		// Symbols name: s&box ships MaterialIcons-Regular.ttf, and a Symbols-only name renders as
		// nothing at all rather than failing (RigIconButton's class comment records this).
		Paint.SetPen( Theme.TextControl.WithAlpha( 0.55f ) );
		Paint.DrawIcon( ClearRect(), "close", 14, TextFlag.Center );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( !e.LeftMouseButton )
			return;

		if ( _param.BodyIds.Count > 0 && ClearRect().IsInside( e.LocalPosition ) )
		{
			_param.BodyIds.Clear();
			Push();
			Update();
			_changed?.Invoke();
			return;
		}

		if ( _armed )
			Disarm();
		else
			Arm();
	}

	public void Arm()
	{
		if ( _armed )
			return;

		_armed = true;
		_viewport.BodyPickMode = true;
		_viewport.BodyPicked = OnBodyPicked;
		Push();
		_viewport.SetPickPrompt( "Click the bodies this feature acts on. Escape when done." );
		Update();
	}

	public void Disarm()
	{
		if ( !_armed )
			return;

		_armed = false;
		_viewport.BodyPickMode = false;
		_viewport.BodyPicked = null;
		_viewport.SelectedBodyIds = null;
		_viewport.SetPickPrompt( "" );
		Update();
	}

	/// <summary>Toggle: clicking a chosen body takes it back out. Removing the last one returns the
	/// feature to acting on everything, which is the same state it started in — there is no way to
	/// select nothing, and a feature that quietly did nothing would be worse than one that does the
	/// default.</summary>
	private void OnBodyPicked( string bodyId )
	{
		if ( string.IsNullOrEmpty( bodyId ) )
			return;

		if ( !_param.BodyIds.Remove( bodyId ) )
			_param.BodyIds.Add( bodyId );

		Push();
		Update();
		_changed?.Invoke();
	}

	/// <summary>Keep the viewport's lit set in step with the parameter, so what is highlighted is
	/// always what is stored.</summary>
	private void Push()
	{
		_viewport.SelectedBodyIds = _armed ? _param.BodyIds.ToList() : null;
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		// Leaving pick mode armed would keep the bodies clickable after the dialog closed.
		if ( _armed )
			Disarm();
	}
}

/// <summary>
/// The faces a material assignment paints, picked in the viewport.
///
/// Multi-select and stays armed, for the same reason the body box does: "which faces" has any
/// number of answers, so a click toggles one and the box waits for the next. Escape ends it.
///
/// It shows a COUNT rather than a list. A face has no name to show — it is "the third face of
/// body2", which is exactly the kind of index-based identity FaceRef exists to avoid — so the
/// useful readout is how many are picked, with the faces themselves lit in the viewport where they
/// can actually be seen.
/// </summary>
internal sealed class EffigyFaceSetSelector : Widget, IArmableSelection
{
	private readonly EffigyViewport _viewport;
	private readonly FaceMaterialFeature _feature;
	private readonly Action _changed;

	private bool _armed;

	public EffigyFaceSetSelector( Widget parent, EffigyViewport viewport, FaceMaterialFeature feature,
		Action changed ) : base( parent )
	{
		_viewport = viewport;
		_feature = feature;
		_changed = changed;

		Layout = Layout.Row();
		Layout.Margin = new Sandbox.UI.Margin( 8, 3 );
		Layout.Spacing = 6;

		FixedHeight = 46f;
		Cursor = CursorShape.Finger;
	}

	private Rect ClearRect() => new( Width - 26f, 18f, 18f, 22f );

	protected override void OnPaint()
	{
		var count = _feature.Faces.Count;

		Paint.SetPen( Theme.TextControl.WithAlpha( 0.7f ) );
		Paint.SetDefaultFont( 8 );
		Paint.DrawText( new Rect( 0f, 0f, Width, 16f ).Shrink( 8f, 2f, 0f, 0f ), "Faces", TextFlag.LeftTop );

		var box = new Rect( 8f, 18f, Width - 16f, 22f );

		Paint.ClearPen();
		Paint.SetBrush( _armed ? Theme.Blue.WithAlpha( 0.18f ) : Theme.ControlBackground );
		Paint.DrawRect( box, 2f );

		Paint.ClearBrush();

		// Red while empty, like the plane box: a face material with no faces is a feature that
		// cannot build, and the dialog's own IsBroken predicate agrees.
		Paint.SetPen( _armed ? Theme.Blue : (count > 0 ? Theme.TextControl.WithAlpha( 0.35f ) : Theme.Red.WithAlpha( 0.6f )) );
		Paint.DrawRect( box, 2f );

		Paint.SetDefaultFont( 9 );

		var label = count switch
		{
			0 when _armed => "Click the faces to paint",
			0 => "No faces picked",
			1 => "1 face",
			_ => $"{count} faces"
		};

		if ( _armed && count > 0 )
			label += " — click to add or remove";

		Paint.SetPen( _armed ? Theme.Blue : (count > 0 ? Theme.TextControl : Theme.TextControl.WithAlpha( 0.45f )) );
		Paint.DrawText( box.Shrink( 6f, 0f, 30f, 0f ), label, TextFlag.LeftCenter );

		if ( count == 0 )
			return;

		Paint.SetPen( Theme.TextControl.WithAlpha( 0.55f ) );
		Paint.DrawIcon( ClearRect(), "close", 14, TextFlag.Center );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( !e.LeftMouseButton )
			return;

		if ( _feature.Faces.Count > 0 && ClearRect().IsInside( e.LocalPosition ) )
		{
			_feature.Faces.Clear();
			Push();
			Update();
			_changed?.Invoke();
			return;
		}

		if ( _armed )
			Disarm();
		else
			Arm();
	}

	public void Arm()
	{
		if ( _armed )
			return;

		_armed = true;
		_viewport.FacePickMode = true;
		_viewport.FacePicked = OnFacePicked;
		Push();
		_viewport.SetPickPrompt( "Click the faces to put on this material slot. Escape when done." );
		Update();
	}

	public void Disarm()
	{
		if ( !_armed )
			return;

		_armed = false;
		_viewport.FacePickMode = false;
		_viewport.FacePicked = null;
		_viewport.SelectedFaces = null;
		_viewport.SetPickPrompt( "" );
		Update();
	}

	/// <summary>
	/// Toggle. Clicking a face already in the set takes it out, which is the only way to correct a
	/// misclick without starting the whole assignment again.
	///
	/// Matching is by RESOLVED FACE, not by comparing stored FaceRefs. Two clicks on the same face
	/// produce two references with slightly different hit points and anchors — they are not equal,
	/// and comparing them would let the same face be added twice and never removed.
	/// </summary>
	private void OnFacePicked( FaceRef face )
	{
		var bodies = _viewport.PickableBodies;

		if ( !FacePlane.TryResolveFace( bodies, face, out var body, out var index ) )
			return;

		for ( var i = 0; i < _feature.Faces.Count; i++ )
		{
			if ( !FacePlane.TryResolveFace( bodies, _feature.Faces[i], out var existing, out var existingIndex ) )
				continue;

			if ( existing.Id != body.Id || existingIndex != index )
				continue;

			_feature.Faces.RemoveAt( i );
			Push();
			Update();
			_changed?.Invoke();
			return;
		}

		_feature.Faces.Add( face );
		Push();
		Update();
		_changed?.Invoke();
	}

	private void Push()
	{
		_viewport.SelectedFaces = _armed ? _feature.Faces.ToList() : null;
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		if ( _armed )
			Disarm();
	}
}
