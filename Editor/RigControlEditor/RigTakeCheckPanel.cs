using Editor;
using Effigy;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Marionette.Tools;

/// <summary>
/// The Take Check tab - play the open clip on the model in the viewport, between frames as well as
/// on them, and say where the skin goes through itself, through its other parts and through the
/// clip's props.
///
/// WHY IN THE ANIMATOR AND NOT ONLY ON THE COMMAND LINE. tools/effigy.sh take does the same check
/// headless, but it wants a model dump and a saved .riganim, and by the time you have made both the
/// moment you were fixing has gone cold. Here the model is the one already loaded, the clip is the
/// one on the timeline - saved or not - and a frame in the report is one click from the playhead.
///
/// THE SAME CHECK, NOT A SECOND ONE. Everything that decides a crossing is TakeAudit in the kernel,
/// fed a ModelDump built by EffigyModelDump and a RigAnimFile read from the clip's own JSON. The
/// panel only gathers the inputs and draws the answer, so a frame flagged here is a frame the
/// command line flags too.
///
/// PROPS THAT MOVE ARE LEFT OUT. The audit holds props still; a reference prop that follows a bone
/// or has keys of its own would be checked where it was placed rather than where it is on the
/// frame, which is a wrong answer dressed as a right one. Those are skipped and named.
///
/// Modelled on RigCamerasPanel: help box, a property sheet, a button row, a scrolling list.
/// </summary>
internal sealed class RigTakeCheckPanel : Widget
{
	private readonly RigViewport _viewport;
	private readonly TakeCheckSettings _settings = new();
	private readonly Button _run;
	private readonly Button _copy;
	private readonly Editor.Label _status;
	private readonly Widget _list;

	private RigAnimDocument _anim;
	private Asset _asset;

	/// <summary>The last run's report as text, for Copy Report.</summary>
	private string _summary = "";

	/// <summary>Bumped by every run and every SetAnim, so a run that finishes after the clip has
	/// changed under it drops its answer rather than showing it against the wrong clip.</summary>
	private int _generation;

	private bool _running;

	/// <summary>Move the timeline's playhead to a frame - a flagged frame is one click from being
	/// looked at. Wired by the window.</summary>
	public Action<float> ScrubTo { get; set; }

	public RigTakeCheckPanel( Widget parent, RigViewport viewport ) : base( parent )
	{
		_viewport = viewport;

		Name = "Take Check";
		WindowTitle = "Take Check";
		SetWindowIcon( "rule" );

		Layout = Layout.Column();

		Layout.Add( RigHelpBox.Create( this,
			"Plays this clip on the model in the viewport and reports where the skin passes " +
			"through itself, through its own other parts, and through the clip's props - " +
			"between frames as well as on them. Click a frame to go to it.",
			new[]
			{
				RigHelpBox.S( "Baseline",
					"Crossings the model already has in its bind pose - a cuff tucked into a " +
					"sleeve - are the model's, not the take's. They are counted once and never " +
					"flag a frame." ),

				RigHelpBox.S( "Substeps",
					"Samples per frame. A finger can go through a button between two keyed frames " +
					"that are both clear; the game plays the in-between, so the check samples it " +
					"too. 4 checks every quarter frame." ),

				RigHelpBox.S( "Depth Tolerance and Allow",
					"Tolerance: a crossing between two meshes only fails when it goes deeper than " +
					"this, in units (0.2-0.4 is about half a centimetre to a centimetre). Allow: " +
					"pairs meant to overlap, as a:b@depth with * wildcards, comma separated - " +
					"glove_*:hard_*/hab_rubber@0.5. A prop's parts are named prop/mesh." ),

				RigHelpBox.S( "What is checked",
					"The model's LOD-0 meshes, skinned with the weights in the .fbx it was built " +
					"from - a model built from anything else cannot move in the check, and says " +
					"so. Props: visible reference props and object parts that hold still. Props " +
					"that follow a bone or have keys are skipped and listed." ),
			} ) );

		var sheet = new ControlSheet();
		sheet.AddObject( EditorTypeLibrary.GetSerializedObject( _settings ) );
		Layout.Add( sheet );

		var bar = Layout.AddRow();
		bar.Margin = 4;
		bar.Spacing = 4;
		_run = bar.Add( new Button( "Run Check", "play_arrow" ) { Clicked = Run, ToolTip = "Check the open clip on the viewport's model" } );
		_copy = bar.Add( new Button( "Copy Report", "content_copy" )
		{
			ToolTip = "Copy the full report to the clipboard as text",
			Clicked = () => { if ( !string.IsNullOrEmpty( _summary ) ) EditorUtility.Clipboard.Copy( _summary ); }
		} );
		_copy.Enabled = false;
		bar.AddStretchCell();

		_status = new Editor.Label( "" ) { WordWrap = true };
		Layout.Add( _status );

		var scroll = Layout.Add( new ScrollArea( this ), 1 );
		scroll.VerticalScrollbarMode = ScrollbarMode.Auto;
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;

		_list = new Widget( this ) { Layout = Layout.Column() };
		_list.Layout.Margin = 4;
		_list.Layout.Spacing = 4;
		scroll.Canvas = _list;

		ShowIdle();
	}

	/// <summary>The clip to check, and the asset it came from - the asset is only used to find
	/// OBJ files beside it, and as a last resort for the clip itself.</summary>
	public void SetAnim( RigAnimDocument anim, Asset asset )
	{
		_anim = anim;
		_asset = asset;
		_generation++;
		_running = false;
		_summary = "";
		_copy.Enabled = false;
		_run.Enabled = true;
		ShowIdle();
	}

	private void ShowIdle()
	{
		_list.Layout.Clear( true );
		_status.Text = "";

		_list.Layout.Add( new Editor.Label( _anim is null
			? "Open a Rig Animation asset to check it."
			: "Not checked yet. Run Check plays the clip on the viewport's model." )
		{ Enabled = false } );

		_list.Layout.AddStretchCell();
	}

	// --- running ---------------------------------------------------------------------------------

	private async void Run()
	{
		if ( _running || _anim is null )
			return;

		var model = _viewport?.Renderer?.Model;

		if ( model is null || model.IsError || model.Bones is null )
		{
			SetStatus( "No model in the viewport to check the clip on.", Theme.Yellow );
			return;
		}

		var generation = ++_generation;
		var notes = new List<string>();

		// EVERYTHING THAT TOUCHES THE ENGINE HAPPENS HERE, ON THE MAIN THREAD: the model's vertex
		// buffers, the document, the asset system. What goes to the worker is plain kernel data -
		// the dump, the clip's JSON, a file path - so nothing on it can reach back into the engine.
		ModelDump dump;
		string json;
		string rigJson = null;
		string fbx;

		try
		{
			dump = EffigyModelDump.FromModel( model );
			dump.Placement = EffigyModelDump.ToXform( _viewport.Renderer.WorldTransform );
			AddProps( dump, notes );

			fbx = EffigyModelDump.SourceFbx( model.ResourcePath );
			json = ClipJson( notes );
			rigJson = RigJson( notes );
		}
		catch ( Exception e )
		{
			SetStatus( $"Could not gather the check's inputs: {e.Message}", Theme.Red );
			return;
		}

		if ( json is null )
		{
			SetStatus( "Could not read the clip - it has never been saved and could not be serialized.", Theme.Red );
			return;
		}

		if ( dump.Meshes.Count == 0 )
		{
			SetStatus( $"{model.ResourcePath} gave back no meshes to check.", Theme.Yellow );
			return;
		}

		var options = _settings.ToOptions( out var allowError );

		if ( allowError is not null )
		{
			SetStatus( allowError, Theme.Red );
			return;
		}

		_running = true;
		_run.Enabled = false;
		_copy.Enabled = false;
		_list.Layout.Clear( true );
		_list.Layout.AddStretchCell();
		_status.Text = $"Running… {dump.Meshes.Count} part(s), {dump.Props.Count} prop(s), {_settings.Substeps} sample(s) a frame.";
		_status.Color = Theme.TextLight;

		Outcome outcome;

		try
		{
			// THE AUDIT IS THE SLOW PART - every check at every substep of every frame - so it runs on a
			// worker, and so does reading the FBX, which for a full body is a second or two on its own.
			outcome = await Task.Run( () => Audit( dump, json, rigJson, fbx, options ) );
		}
		catch ( Exception e )
		{
			outcome = new Outcome { Error = e.Message };
		}

		// Back to the main thread before touching a widget. The editor's synchronization context
		// already resumes there; this makes it explicit rather than an assumption.
		await GameTask.MainThread();

		if ( !this.IsValid() || generation != _generation )
			return;

		_running = false;
		_run.Enabled = true;

		notes.InsertRange( 0, outcome.Notes );
		ShowResult( outcome, notes );
	}

	/// <summary>What the worker hands back.</summary>
	private sealed class Outcome
	{
		public TakeAudit.Result Result;
		public string Error;
		public bool Weighted;
		public List<string> Notes = new();
	}

	/// <summary>The worker's half: weights from the source FBX, then the audit. Kernel only.</summary>
	private static Outcome Audit( ModelDump dump, string json, string rigJson, string fbx, TakeAudit.Options options )
	{
		var outcome = new Outcome();

		if ( fbx is null )
			outcome.Notes.Add( $"{dump.Model} is not built from an .fbx this can find, so it has no skin weights: the skin cannot move, and every frame reads as the bind pose." );
		else
		{
			try
			{
				var fit = dump.AttachWeights( FbxScene.ReadFile( fbx ) );
				outcome.Weighted = true;

				if ( fit.BoneError > 0.05f || fit.WorstVertex > 0.05f )
					outcome.Notes.Add( $"The FBX does not line up closely with the compiled model ({fit}) - check {Path.GetFileName( fbx )} is the model's real source." );
			}
			catch ( Exception e )
			{
				outcome.Notes.Add( $"Could not take weights from {Path.GetFileName( fbx )}: {e.Message}. The skin cannot move in this check." );
			}
		}

		var clip = RigAnimFile.Read( json );

		// The rig says how the clip's live IK goals are solved - chain length, pole, weight. Without
		// it RigAnimFile solves them the way a new constraint would, which is right for most rigs
		// and said so in the report when it happens.
		if ( rigJson is not null )
			clip.UseRig( rigJson );

		if ( options.From is { } from && options.To is { } to && to < from )
			options.To = from;

		outcome.Result = new TakeAudit( dump, clip ).Run( options );
		return outcome;
	}

	/// <summary>
	/// The open clip as the JSON s&amp;box writes for it - the in-memory document, so unsaved edits
	/// are checked, through the same Serialize the asset system saves with. Only if that fails is
	/// the saved file read instead, and the report says so.
	/// </summary>
	/// <summary>The clip's .ctrlrig, read on the main thread like the clip, for its IK constraints.
	/// Null when the clip has no rig - or no live goals, when it does not matter.</summary>
	private string RigJson( List<string> notes )
	{
		if ( _anim?.RigAsset is null || !_anim.IkGoalTracks.Any( t => t.Keyframes.Count > 0 ) )
		{
			if ( _anim?.IkGoalTracks.Any( t => t.Keyframes.Count > 0 ) == true )
				notes.Add( "This clip keys IK goals but names no Control Rig, so they were solved as two-bone chains bending up." );

			return null;
		}

		try
		{
			return _anim.RigAsset.Serialize().ToJsonString();
		}
		catch ( Exception e )
		{
			notes.Add( $"Could not read the Control Rig ({e.Message}); its IK goals were solved as two-bone chains bending up." );
			return null;
		}
	}

	private string ClipJson( List<string> notes )
	{
		try
		{
			return _anim.Serialize().ToJsonString();
		}
		catch ( Exception e )
		{
			var path = _asset?.AbsolutePath;

			if ( path is null || !File.Exists( path ) )
				return null;

			notes.Add( $"Could not serialize the open clip ({e.Message}); checked the SAVED file instead - unsaved edits are not in this report." );
			return File.ReadAllText( path );
		}
	}

	/// <summary>
	/// The clip's still props into the dump, in the model's space: every model of every visible
	/// reference prop, and every visible part of every object - compiled model or OBJ lump. Whatever
	/// cannot be checked honestly is left out and named.
	/// </summary>
	private void AddProps( ModelDump dump, List<string> notes )
	{
		var toModel = dump.Placement.Inverse;
		var skipped = new List<string>();

		foreach ( var prop in _anim.ReferenceProps ?? new List<ReferenceProp>() )
		{
			if ( prop is null )
				continue;

			var name = string.IsNullOrWhiteSpace( prop.Name ) ? "reference" : prop.Name;

			if ( !prop.Visible ) { skipped.Add( $"{name} (hidden)" ); continue; }
			if ( !string.IsNullOrWhiteSpace( prop.FollowBone ) ) { skipped.Add( $"{name} (follows {prop.FollowBone})" ); continue; }
			if ( Keyed( name ) ) { skipped.Add( $"{name} (has keys)" ); continue; }

			var place = toModel * EffigyModelDump.ToXform( prop.LocalTransform );
			var first = true;

			foreach ( var model in prop.AllModels )
			{
				// The primary keeps the prop's own name, so an allowance written against it still
				// matches; extras are told apart by their model.
				var label = first ? name : $"{name}.{model.ResourceName}";
				first = false;

				var dumped = EffigyModelDump.PropFromModel( label, model, place );

				if ( dumped.Meshes.Count == 0 )
					skipped.Add( $"{label} (no meshes)" );
				else
					dump.Props.Add( dumped );
			}
		}

		foreach ( var owner in _anim.Objects ?? new List<RigObject>() )
		{
			if ( owner is null || string.IsNullOrWhiteSpace( owner.Name ) || owner.Parts is null )
				continue;

			if ( !owner.Visible ) { skipped.Add( $"{owner.Name} (hidden)" ); continue; }
			if ( !string.IsNullOrWhiteSpace( owner.FollowBone ) ) { skipped.Add( $"{owner.Name} (follows {owner.FollowBone})" ); continue; }
			if ( Keyed( owner.Name ) ) { skipped.Add( $"{owner.Name} (has keys)" ); continue; }

			var objWorld = owner.LocalTransform;

			foreach ( var part in owner.Parts )
			{
				if ( part is null || string.IsNullOrWhiteSpace( part.Name ) )
					continue;

				var key = RigTrackName.Qualify( owner.Name, part.Name );

				if ( !Visible( owner, part ) ) { skipped.Add( $"{key} (hidden)" ); continue; }
				if ( MovesInObject( owner, part ) ) { skipped.Add( $"{key} (has keys)" ); continue; }

				var place = toModel * EffigyModelDump.ToXform( objWorld.ToWorld( _anim.PartInObject( owner, part, null ) ) );

				if ( part.Model is not null )
				{
					var dumped = EffigyModelDump.PropFromModel( key, part.Model, place );

					if ( dumped.Meshes.Count == 0 ) skipped.Add( $"{key} (no meshes)" );
					else dump.Props.Add( dumped );

					continue;
				}

				var mesh = ObjMesh( owner, part );

				if ( EffigyModelDump.PartFromMesh( string.IsNullOrEmpty( part.ObjPart ) ? part.Name : part.ObjPart, mesh ) is not { } piece )
				{
					skipped.Add( $"{key} (no mesh found)" );
					continue;
				}

				var objProp = new ModelDump.Prop { Name = key, Model = owner.ObjSource ?? "", Transform = place };
				objProp.Meshes.Add( piece );
				dump.Props.Add( objProp );
			}
		}

		if ( skipped.Count > 0 )
			notes.Add( $"Not checked: {string.Join( ", ", skipped )}." );
	}

	private bool Keyed( string trackName ) =>
		_anim.FindPartTrack( trackName ) is { Keyframes.Count: > 0 };

	/// <summary>Whether a part, or anything it follows inside its object, has keys of its own.</summary>
	private bool MovesInObject( RigObject owner, RigObjectPart part )
	{
		for ( var p = part; p is not null; p = owner.ParentOf( p ) )
			if ( Keyed( RigTrackName.Qualify( owner.Name, p.Name ) ) )
				return true;

		return false;
	}

	/// <summary>The viewport's rule: hiding the head hides the eyes.</summary>
	private static bool Visible( RigObject owner, RigObjectPart part )
	{
		for ( var p = part; p is not null; p = owner.ParentOf( p ) )
			if ( !p.Visible )
				return false;

		return true;
	}

	/// <summary>The OBJ lump a part draws, found the way the viewport finds it.</summary>
	private PolyMesh ObjMesh( RigObject owner, RigObjectPart part )
	{
		if ( string.IsNullOrWhiteSpace( owner.ObjSource ) )
			return null;

		var folder = !string.IsNullOrWhiteSpace( _viewport?.DocumentFolder )
			? _viewport.DocumentFolder
			: _asset?.AbsolutePath is { } abs ? Path.GetDirectoryName( abs ) : null;

		var path = RigObjMeshes.Resolve( owner.ObjSource, folder );

		if ( path is null )
			return null;

		// Empty means the whole file: the first lump, the same match RigObjMeshes.Load makes.
		foreach ( var piece in RigObjMeshes.Pieces( path ) )
			if ( string.IsNullOrEmpty( part.ObjPart ) || piece.Name == part.ObjPart )
				return piece.Mesh;

		return null;
	}

	// --- showing ---------------------------------------------------------------------------------

	private void SetStatus( string message, Color color )
	{
		_status.Text = message;
		_status.Color = color;
	}

	private void ShowResult( Outcome outcome, List<string> notes )
	{
		_list.Layout.Clear( true );

		if ( outcome.Result is not { } result )
		{
			SetStatus( $"The check failed: {outcome.Error}", Theme.Red );
			_list.Layout.AddStretchCell();
			return;
		}

		_summary = result.ToString();

		if ( notes.Count > 0 )
			_summary += "\n\n" + string.Join( "\n", notes.Select( n => $"note: {n}" ) );

		_copy.Enabled = true;

		var over = result.Checks.Count( c => !c.Clear );

		SetStatus( result.Clear
			? $"CLEAR - frames {result.From:0.##} to {result.To:0.##}, {result.Samples} samples, {result.Checks.Count} checks."
			: $"CROSSINGS - {over} of {result.Checks.Count} checks go over, frames {result.From:0.##} to {result.To:0.##}, {result.Samples} samples.",
			result.Clear ? Theme.Green : Theme.Red );

		// Warnings first: they say how far to trust everything under them.
		if ( result.MissingBones.Count > 0 )
			_list.Layout.Add( Warning( $"Tracks with no bone in this model, ignored: {string.Join( ", ", result.MissingBones )}. " +
				"The clip may have been made on a different model." ) );

		foreach ( var note in notes )
			_list.Layout.Add( Warning( note ) );

		// Failing checks, worst first; then set-ins; then the clear ones folded away, because on a
		// model with a dozen parts and a few props they are most of the list and none of the news.
		foreach ( var check in result.Checks.Where( c => !c.Clear ).OrderByDescending( c => c.FramesOver.Count ) )
			_list.Layout.Add( Build( check ) );

		foreach ( var check in result.Checks.Where( c => c.Clear && c.Allowed is not null && c.SetInFrame >= 0 ) )
			_list.Layout.Add( Build( check ) );

		var clear = result.Checks.Where( c => c.Clear && !(c.Allowed is not null && c.SetInFrame >= 0) ).ToList();

		if ( clear.Count > 0 )
			_list.Layout.Add( Folded( $"{clear.Count} clear check(s)", () => clear.Select( Build ) ) );

		_list.Layout.AddStretchCell();
	}

	private Widget Warning( string text )
	{
		var label = new Editor.Label( text ) { WordWrap = true, Color = Theme.Yellow };
		return label;
	}

	private Widget Build( TakeAudit.Check check )
	{
		var panel = new Widget( _list ) { Layout = Layout.Column() };
		panel.Layout.Margin = 6;
		panel.Layout.Spacing = 2;

		var setIn = check.Clear && check.Allowed is not null && check.SetInFrame >= 0;

		var header = panel.Layout.AddRow();
		header.Spacing = 6;

		var (tag, color) = setIn ? ("[set-in]", Theme.Blue)
			: check.Clear ? ("[ok]", Theme.Green)
			: check.Allowed is not null ? ("[beyond]", Theme.Red)
			: ("[cross]", Theme.Red);

		var chip = new Editor.Label( tag ) { Color = color };
		chip.SetStyles( "font-weight: bold;" );
		header.Add( chip );
		header.Add( new Editor.Label( check.Name ) { WordWrap = true }, 1 );

		var row = new Widget { Layout = Layout.Row() };
		row.Layout.Spacing = 4;

		if ( !check.Clear )
		{
			row.Layout.Add( FrameButton( $"worst: {check.WorstCrossings} at {check.WorstFrame:0.##}", check.WorstFrame,
				"The sample with the most new crossing triangle pairs" ) );

			if ( check.WorstDepthFrame >= 0 )
				row.Layout.Add( FrameButton( $"deepest: {check.WorstDepth:0.###} at {check.WorstDepthFrame:0.##}", check.WorstDepthFrame,
					"How far the deepest new crossing goes, in units" ) );
		}
		else if ( setIn )
		{
			row.Layout.Add( FrameButton( $"deepest set-in: {check.SetInDepth:0.###} at {check.SetInFrame:0.##}", check.SetInFrame,
				$"Within its allowance {check.Allowed}" ) );
		}

		if ( check.NearestGap < float.MaxValue )
			row.Layout.Add( FrameButton( $"nearest: {check.NearestGap:0.###} at {check.NearestGapFrame:0.##}", check.NearestGapFrame,
				"Closest approach to the prop, in units" ) );

		row.Layout.AddStretchCell();
		panel.Layout.Add( row );

		var extras = new List<string>();

		if ( check.Baseline > 0 ) extras.Add( $"{check.Baseline} already crossing at bind, not counted" );
		if ( check.Allowed is not null && !setIn ) extras.Add( $"allowance {check.Allowed}" );

		if ( extras.Count > 0 )
			panel.Layout.Add( new Editor.Label( string.Join( "; ", extras ) ) { Enabled = false, WordWrap = true } );

		if ( check.FramesOver.Count > 0 )
		{
			var ranges = Ranges( check.FramesOver );
			panel.Layout.Add( Folded( $"{check.FramesOver.Count} sample(s) over, in {ranges.Count} run(s)", () => new[] { RangeGrid( ranges ) } ) );
		}

		return panel;
	}

	/// <summary>A small flat button that moves the playhead.</summary>
	private Widget FrameButton( string text, float frame, string tip )
	{
		return new Button( text )
		{
			ToolTip = $"{tip}. Click to go to frame {frame:0.##}.",
			Clicked = () => ScrubTo?.Invoke( frame )
		};
	}

	/// <summary>A header that opens to whatever <paramref name="build"/> makes - built on first
	/// open, so a long list of clear checks costs nothing until somebody wants it.</summary>
	private Widget Folded( string title, Func<IEnumerable<Widget>> build )
	{
		var box = new Widget( _list ) { Layout = Layout.Column() };
		box.Layout.Spacing = 2;

		var body = new Widget( box ) { Layout = Layout.Column(), Visible = false };
		body.Layout.Spacing = 4;
		var built = false;

		var toggle = new Button( title, "chevron_right" );

		toggle.Clicked = () =>
		{
			if ( !built )
			{
				foreach ( var w in build() )
					body.Layout.Add( w );

				built = true;
			}

			body.Visible = !body.Visible;
			toggle.Icon = body.Visible ? "expand_more" : "chevron_right";
		};

		box.Layout.Add( toggle );
		box.Layout.Add( body );
		return box;
	}

	/// <summary>Runs of samples, a button each, a few to a row. Capped: past a couple of hundred runs
	/// the take has a bigger problem than which frame to look at first.</summary>
	private Widget RangeGrid( List<(float Start, float End)> ranges )
	{
		const int perRow = 5;
		const int cap = 200;

		var grid = new Widget { Layout = Layout.Column() };
		grid.Layout.Spacing = 2;

		Layout row = null;

		for ( var i = 0; i < Math.Min( ranges.Count, cap ); i++ )
		{
			if ( i % perRow == 0 )
			{
				row = grid.Layout.AddRow();
				row.Spacing = 2;
			}

			var (start, end) = ranges[i];
			var text = start == end ? $"{start:0.##}" : $"{start:0.##}-{end:0.##}";
			row.Add( FrameButton( text, start, "First sample of this run" ) );
		}

		row?.AddStretchCell();

		if ( ranges.Count > cap )
			grid.Layout.Add( new Editor.Label( $"… and {ranges.Count - cap} more runs (Copy Report has them all)" ) { Enabled = false } );

		return grid;
	}

	/// <summary>Samples no more than a frame apart, joined - the same runs Result.ToString prints.</summary>
	private static List<(float Start, float End)> Ranges( List<float> frames )
	{
		var runs = new List<(float, float)>();

		if ( frames.Count == 0 )
			return runs;

		var sorted = frames.OrderBy( f => f ).ToList();
		var start = sorted[0];
		var prev = sorted[0];

		for ( var i = 1; i < sorted.Count; i++ )
		{
			if ( sorted[i] - prev <= 1.0001f )
			{
				prev = sorted[i];
				continue;
			}

			runs.Add( (start, prev) );
			start = prev = sorted[i];
		}

		runs.Add( (start, prev) );
		return runs;
	}
}

/// <summary>The Take Check tab's options, as a property sheet. Kept off the document on purpose:
/// how hard to look is a question for this sitting, not something a clip should carry.</summary>
internal sealed class TakeCheckSettings
{
	[Property, Title( "Self" ), Description( "Check each part of the model against itself" )]
	public bool Self { get; set; } = true;

	[Property, Title( "Part Pairs" ), Description( "Check the model's parts against each other - glove against sleeve" )]
	public bool PartPairs { get; set; } = true;

	[Property, Title( "Props" ), Description( "Check the model against the clip's still reference props and object parts" )]
	public bool Props { get; set; } = true;

	[Property, Range( 1, 16 ), Description( "Samples per frame - 4 checks every quarter frame" )]
	public int Substeps { get; set; } = 4;

	[Property, Title( "Depth Tolerance" ), Range( 0f, 2f ), Description( "A crossing between two meshes fails only deeper than this, in units. 0 fails any" )]
	public float DepthTolerance { get; set; }

	[Property, Description( "Pairs meant to overlap: a:b@depth, * wildcards, comma separated" )]
	public string Allow { get; set; } = "";

	[Property, Title( "From Frame" ), Description( "First frame to check" )]
	public int From { get; set; }

	[Property, Title( "To Frame" ), Description( "Last frame to check; -1 for the clip's last key" )]
	public int To { get; set; } = -1;

	public TakeAudit.Options ToOptions( out string error )
	{
		error = null;
		var options = new TakeAudit.Options
		{
			Self = Self,
			PartPairs = PartPairs,
			Props = Props,
			Substeps = Math.Clamp( Substeps, 1, 64 ),
			DepthTolerance = Math.Max( 0f, DepthTolerance ),
			From = Math.Max( 0, From ),
			To = To < 0 ? null : To,
		};

		try
		{
			options.Allow = TakeAudit.Allowance.ParseList( Allow );
		}
		catch ( Exception e )
		{
			error = $"Allow: {e.Message}";
		}

		return options;
	}
}
