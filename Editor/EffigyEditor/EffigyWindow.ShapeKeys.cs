using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

// ============================================================================
// The Shape Keys dock: the selected body's keys read off the feature history, and the clicks
// that write it. A key is Shape Key → the features that shape it → End Shape Key; + puts the
// first two in (a Sculpt, so you are straight into shaping it), Finish the third. Edit re-opens a
// finished key: the bar goes just above its End and a Sculpt goes in there (ShapeKeyEdit).
// ============================================================================

public sealed partial class EffigyWindow
{
	private EffigyShapeKeysPanel _shapeKeysPanel;
	private Option _shapeKeysDockOption;

	/// <summary>Preview strength per (body id, key name). View only: never saved, never exported.</summary>
	private readonly Dictionary<(string Body, string Key), float> _shapeKeyPreview = new();

	private void BuildShapeKeysPanel()
	{
		_shapeKeysPanel = new EffigyShapeKeysPanel( this )
		{
			AddClicked = AddShapeKey,
			FinishClicked = FinishShapeKey,
			RemoveClicked = RemoveShapeKey,
			EditClicked = EditShapeKey,
			RenameCommitted = RenameShapeKey,
			WeightChanged = PreviewShapeKey,
		};
	}

	/// <summary>The body the panel is about: the one selected, or the only one there is.</summary>
	private Body ShapeKeyBody()
	{
		if ( _studio is null )
			return null;

		var ids = _viewport?.IdleBodyIds ?? (IReadOnlyList<string>)Array.Empty<string>();

		if ( ids.Count == 1 )
			return _studio.Bodies.FirstOrDefault( b => b.Id == ids[0] );

		return _studio.Bodies.Count == 1 ? _studio.Bodies[0] : null;
	}

	/// <summary>Each Shape Key on the body, with the End Shape Key that closes it (null while recording).</summary>
	private List<(ShapeKeyFeature Start, ShapeKeyEndFeature End)> ShapeKeyRuns( Body body ) =>
		ShapeKeyEdit.Runs( _studio, body ).Select( r => (r.Start, r.End) ).ToList();

	private void RefreshShapeKeys()
	{
		if ( _shapeKeysPanel is null || !_shapeKeysPanel.IsValid() )
			return;

		var body = ShapeKeyBody();

		if ( body is null )
		{
			_shapeKeysPanel.Show( null,
				_studio?.Bodies.Count > 1 ? "Click a body to see its shape keys." : "Make a body first: shape keys move its vertices.",
				Array.Empty<EffigyShapeKeysPanel.KeyRow>(), canAdd: false );
			return;
		}

		var rows = new List<EffigyShapeKeysPanel.KeyRow>();
		var editing = ShapeKeyEdit.Editing( _studio, body )?.Start;

		foreach ( var (start, end) in ShapeKeyRuns( body ) )
			rows.Add( new( start.KeyName, start.Id, end is null, Weight( body, start.KeyName ), ReferenceEquals( start, editing ) ) );

		// Keys that came in with the mesh (an imported model's morphs) rather than from this history.
		foreach ( var morph in body.Mesh?.ValidMorphs ?? Enumerable.Empty<MeshMorph>() )
		{
			if ( rows.All( r => r.Name != morph.Name ) )
				rows.Add( new( morph.Name, null, false, Weight( body, morph.Name ) ) );
		}

		var recording = rows.FirstOrDefault( r => r.Recording );
		var edited = rows.FirstOrDefault( r => r.Editing );
		var status = recording.Name is not null
			? $"Recording '{recording.Name}'. Sculpt, edit or add detail, then press Finish key."
			: edited.Name is not null
			? $"Editing '{edited.Name}'. The model shows it fully on; what you sculpt now is added to it. Press Finish key when done."
			: rows.Count == 0 ? "No shape keys yet. + New key starts one and opens Sculpt." : null;

		_shapeKeysPanel.Show( body.Name, status, rows, canAdd: true );
	}

	private float Weight( Body body, string key ) => _shapeKeyPreview.GetValueOrDefault( (body.Id, key) );

	private void AddShapeKey()
	{
		var body = ShapeKeyBody();

		if ( body is null )
		{
			Refuse( "Click the body the shape key is for, then press + New key." );
			return;
		}

		if ( ShapeKeyRuns( body ).Any( r => r.End is null ) )
		{
			Refuse( "A key is already recording on this body. Press Finish key first: keys are recorded one at a time." );
			return;
		}

		if ( ShapeKeyEdit.Editing( _studio ) is { } editing )
		{
			Refuse( $"'{editing.Start.KeyName}' is open for editing. Press Finish key first: keys are shaped one at a time." );
			return;
		}

		var taken = new HashSet<string>( ShapeKeyRuns( body ).Select( r => r.Start.KeyName ) );
		var n = 1;
		while ( taken.Contains( $"key_{n}" ) )
			n++;

		var key = new ShapeKeyFeature { Name = $"Shape key {n}" };
		key.Key.Value = $"key_{n}";
		key.Bodies.BodyIds.Add( body.Id );

		RecordUndo();
		InsertAtRollback( key );

		// Straight into shaping it. The Sculpt goes in at the bar, below the key.
		if ( NewFeature( ToolKind.Sculpt, -1 ) is SculptFeature sculpt )
		{
			sculpt.Bodies.BodyIds.Clear();
			sculpt.Bodies.BodyIds.Add( body.Id );
			AddFeature( sculpt );
		}
		else
		{
			RebuildStudio();
		}

		SetPrompt( $"Recording '{key.KeyName}': sculpt the shape, then press Finish key in Shape Keys." );
	}

	private void FinishShapeKey()
	{
		var body = ShapeKeyBody();
		var open = body is null ? default : ShapeKeyRuns( body ).FirstOrDefault( r => r.End is null );

		if ( open.Start is null && ShapeKeyEdit.Editing( _studio ) is { } editing )
		{
			// A re-opened key: its End is already there, just past the bar. Accept the sculpt and put
			// the bar back so the End runs again with the new work in the key.
			if ( _dialog?.Feature is not null )
				_dialog.AcceptNow();

			RecordUndo();
			_rollbackBeforeEdit = null;
			ShapeKeyEdit.Close( _studio );
			RebuildStudio();

			SetPrompt( $"'{editing.Start.KeyName}' is updated. Drag its slider in Shape Keys to see it." );
			return;
		}

		if ( open.Start is null )
		{
			Refuse( "No key is recording or being edited on this body." );
			return;
		}

		// The sculpt (or whatever is open) is part of the key: accept it first, or the key ends
		// before the strokes are in it.
		if ( _dialog?.Feature is not null )
			_dialog.AcceptNow();

		RecordUndo();
		InsertAtRollback( new ShapeKeyEndFeature { Name = $"End {open.Start.KeyName}" } );
		RebuildStudio();

		SetPrompt( $"'{open.Start.KeyName}' is a shape key now. Drag its slider in Shape Keys to see it." );
	}

	/// <summary>Re-open a finished key to sculpt more into it: the bar goes just above its End
	/// Shape Key (so the model shows the key fully on) and a Sculpt goes in there. Finish key puts
	/// the bar back.</summary>
	private void EditShapeKey( string startId )
	{
		if ( _studio.Features.FirstOrDefault( f => f.Id == startId ) is not ShapeKeyFeature key )
		{
			Refuse( "That shape key is no longer in the history." );
			return;
		}

		var body = ShapeKeyBody();

		if ( body is null )
		{
			Refuse( "Click the body the shape key is on, then press Edit on the key." );
			return;
		}

		if ( _dialog?.Feature is not null )
			_dialog.AcceptNow();

		RecordUndo();

		// An explicit move of the bar, like Roll back to here: closing a dialog must not undo it.
		var bar = _studio.RollbackIndex;
		_rollbackBeforeEdit = null;

		if ( !ShapeKeyEdit.Reopen( _studio, key, out var why ) )
		{
			_studio.RollbackIndex = bar;
			Refuse( why );
			return;
		}

		// Straight into shaping it. The Sculpt goes in at the bar, just above the key's End.
		if ( NewFeature( ToolKind.Sculpt, -1 ) is SculptFeature sculpt )
		{
			sculpt.Bodies.BodyIds.Clear();
			sculpt.Bodies.BodyIds.Add( body.Id );
			AddFeature( sculpt );
		}
		else
		{
			RebuildStudio();
		}

		SetPrompt( $"Editing '{key.KeyName}': sculpt more into it, then press Finish key in Shape Keys." );
	}

	/// <summary>Model ▸ Mesh ▸ Edit shape key: edit the selected body's only finished key, or open
	/// Shape Keys to pick one.</summary>
	private void EditShapeKeyFromMenu()
	{
		var body = ShapeKeyBody();
		var finished = body is null ? new List<(ShapeKeyFeature Start, ShapeKeyEndFeature End)>() : ShapeKeyRuns( body ).Where( r => r.End is not null ).ToList();

		if ( finished.Count == 1 )
		{
			EditShapeKey( finished[0].Start.Id );
			return;
		}

		DockManager.SetDockState( "Shape Keys", true );
		SyncDockChecks();

		Refuse( body is null
			? "Click the body whose shape key you want to edit, then press Edit on the key in Shape Keys."
			: finished.Count == 0
				? $"{body.Name} has no finished shape keys to edit. + New key in Shape Keys starts one."
				: $"{body.Name} has {finished.Count} shape keys. Press Edit on the one to sculpt into, in Shape Keys." );
	}

	private void RemoveShapeKey( string startId )
	{
		var start = _studio.Features.FirstOrDefault( f => f.Id == startId );
		var from = start is null ? -1 : _studio.Features.IndexOf( start );

		if ( from < 0 )
			return;

		var to = _studio.Features.Count - 1;

		for ( var j = from + 1; j < _studio.Features.Count; j++ )
		{
			if ( _studio.Features[j] is ShapeKeyEndFeature ) { to = j; break; }
			if ( _studio.Features[j] is ShapeKeyFeature ) { to = j - 1; break; }
		}

		if ( _dialog?.Feature is { } editing && _studio.Features.IndexOf( editing ) is var at && at >= from && at <= to )
			_dialog.AcceptNow();

		RecordUndo();

		var doomed = _studio.Features.Skip( from ).Take( to - from + 1 ).ToList();
		foreach ( var feature in doomed )
			_studio.Remove( feature );

		RebuildStudio();
		SetPrompt( $"Removed the shape key and the {doomed.Count - 1} feature{(doomed.Count == 2 ? "" : "s")} that made it. Ctrl+Z brings it back." );
	}

	private void RenameShapeKey( string startId, string name )
	{
		if ( _studio.Features.FirstOrDefault( f => f.Id == startId ) is not ShapeKeyFeature key )
			return;

		var clean = string.Join( "_", name.Replace( "\"", "" ).Split( ' ', StringSplitOptions.RemoveEmptyEntries ) );

		if ( clean.Length == 0 || clean == key.KeyName )
			return;

		RecordUndo();
		key.Key.Value = clean;
		RebuildStudio();
	}

	private void PreviewShapeKey( string key, float weight )
	{
		if ( ShapeKeyBody() is not { } body )
			return;

		_shapeKeyPreview[(body.Id, key)] = weight;
		RefreshPreview();
	}

	/// <summary>The body's mesh with its previewed keys applied, or the mesh itself when none are.</summary>
	private PolyMesh WithShapeKeyPreview( Body body )
	{
		var mesh = body.Mesh;

		if ( mesh is null || _shapeKeyPreview.Count == 0 || !mesh.HasMorphs )
			return mesh;

		PolyMesh shown = null;

		foreach ( var morph in mesh.ValidMorphs )
		{
			var w = _shapeKeyPreview.GetValueOrDefault( (body.Id, morph.Name) );

			if ( w <= 0f )
				continue;

			shown ??= mesh.Clone();

			for ( var i = 0; i < morph.Deltas.Length; i++ )
				shown.Positions[i] += morph.Deltas[i] * w;
		}

		return shown ?? mesh;
	}

	private bool AnyShapeKeyPreview => _shapeKeyPreview.Values.Any( w => w > 0f );
}
