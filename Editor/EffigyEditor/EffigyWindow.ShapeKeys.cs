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
// first two in (a Sculpt, so you are straight into shaping it), Finish the third.
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
	private List<(ShapeKeyFeature Start, ShapeKeyEndFeature End)> ShapeKeyRuns( Body body )
	{
		var runs = new List<(ShapeKeyFeature, ShapeKeyEndFeature)>();
		var features = _studio.Features;

		for ( var i = 0; i < features.Count; i++ )
		{
			if ( features[i] is not ShapeKeyFeature start || !start.Bodies.Matches( body ) )
				continue;

			ShapeKeyEndFeature end = null;

			for ( var j = i + 1; j < features.Count; j++ )
			{
				if ( features[j] is ShapeKeyEndFeature e ) { end = e; break; }
				if ( features[j] is ShapeKeyFeature ) break;
			}

			runs.Add( (start, end) );
		}

		return runs;
	}

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

		foreach ( var (start, end) in ShapeKeyRuns( body ) )
			rows.Add( new( start.KeyName, start.Id, end is null, Weight( body, start.KeyName ) ) );

		// Keys that came in with the mesh (an imported model's morphs) rather than from this history.
		foreach ( var morph in body.Mesh?.ValidMorphs ?? Enumerable.Empty<MeshMorph>() )
		{
			if ( rows.All( r => r.Name != morph.Name ) )
				rows.Add( new( morph.Name, null, false, Weight( body, morph.Name ) ) );
		}

		var recording = rows.FirstOrDefault( r => r.Recording );
		var status = recording.Name is not null
			? $"Recording '{recording.Name}'. Sculpt, edit or add detail, then press Finish key."
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

		if ( open.Start is null )
		{
			Refuse( "No key is recording on this body." );
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
