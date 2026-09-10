using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// The objects in the clip that are not the main model, each with its parts folded up underneath.
///
/// WHY IT IS A SECOND TREE AND NOT MORE ROWS IN THE BONE TREE. A bone tree is one skeleton, deep
/// and long; the objects list is several shallow things. Mixed together, an imported door with
/// forty parts buries the arm you are actually posing, and there is no way to get it back. Two
/// trees means either can be collapsed out of the way on its own, which is the only reason a dense
/// scene stays workable.
///
/// THIS IS THE ONLY PLACE OBJECTS ARE MANAGED. They used to be editable through a generic list row
/// in the BonesObject property sheet as well, which opened a floating serialized-list editor over
/// the viewport - a second editing surface for the same thing, and the worst thing on screen.
/// Everything that editor did is here instead: the buttons along the top, a right-click on a row,
/// and the Inspector for the numbers.
///
/// COLLAPSED BY DEFAULT, because the object is what you place and the parts are what you open once
/// it is where it belongs.
///
/// Selection runs both ways: clicking a row selects that thing in the viewport, and picking
/// something in the viewport (or on its timeline lane) marks its row here.
/// </summary>
internal sealed class RigObjectsPanel : Widget
{
	private readonly RigViewport _viewport;
	private readonly TreeView _tree;
	private readonly Editor.Label _empty;
	private readonly Editor.Label _unnamed;
	private readonly IconButton _delete;

	private readonly Dictionary<string, RigObjectNode> _nodes = new();

	private RigAnimDocument _anim;

	/// <summary>The names the tree was last built from. A rebuild clears the tree, which collapses
	/// every object somebody had opened - so it only happens when this changes, not every time a
	/// bone is clicked and the panel above asks for a refresh.</summary>
	private string _shape;

	/// <summary>Set while the tree's selection is being moved to match the viewport, so doing that
	/// cannot bounce back as a click and re-select the thing in the viewport.</summary>
	private bool _syncing;

	/// <summary>Import OBJ lives on the window, which owns the file dialog and the copy beside the
	/// clip. The button here is the same action as File → Import OBJ.</summary>
	public Action ImportRequested { get; set; }

	/// <summary>
	/// Raised after this panel has changed the document, with the undo label.
	///
	/// The panel edits the lists and the window does everything else - rebuilds the viewport's
	/// copies, refreshes the timeline, records the undo step - the same shape every other panel in
	/// this window reports itself with.
	/// </summary>
	public Action<string> Changed { get; set; }

	public RigObjectsPanel( Widget parent, RigViewport viewport ) : base( parent )
	{
		_viewport = viewport;

		Layout = Layout.Column();

		var heading = Layout.AddRow();
		heading.Margin = new Sandbox.UI.Margin( 8, 6, 8, 2 );
		heading.Spacing = 4;
		heading.Add( new Editor.Label( "Objects" ) { ToolTip = "Everything in this clip that is not the main model. Click one to select it; each object and each of its parts keys to the timeline exactly as a bone does. Right-click a row to rename, hide, duplicate or delete it." }, 1 );

		heading.Add( new Button( "Import OBJ", "file_upload" )
		{
			Clicked = () => ImportRequested?.Invoke(),
			ToolTip = "Load a Wavefront OBJ as one object, one part per o/g group in the file - each part moves and keys on its own",
		} );

		heading.Add( new Button( "Add Model", "add" )
		{
			Clicked = () => PickModel( null ),
			ToolTip = "Add a compiled model (.vmdl) as an object. Its bones, if it has any, pose alongside the main model's.",
		} );

		_delete = heading.Add( new IconButton( "delete", () => Delete( _viewport.SelectedReferencePropName ) )
		{
			IconSize = 16,
			Background = Color.Transparent,
			ToolTip = "Delete the selected object, part or prop, and its keyframes. Ctrl+Z brings both back.",
		} );

		_empty = new Editor.Label( "Nothing yet - Import OBJ or Add Model" ) { Enabled = false };
		_empty.ToolTip = "An imported OBJ arrives as one object with a part per o/g group in the file";
		Layout.Add( _empty );

		// Said out loud rather than skipped silently. A nameless object has no track to key under,
		// so it cannot be shown - but "it isn't there" reads as a bug unless something says why.
		_unnamed = new Editor.Label( "" ) { Enabled = false, Visible = false };
		Layout.Add( _unnamed );

		_tree = new TreeView( this );
		_tree.OnSelectionChanged = selected =>
		{
			if ( _syncing )
				return;

			if ( selected?.FirstOrDefault() is RigObjectNode node )
				_viewport.SelectReferenceProp( node.Key );

			UpdateButtons();
		};

		Layout.Add( _tree, 1 );

		Rebuild();
	}

	public void SetDocument( RigAnimDocument anim )
	{
		_anim = anim;

		// A different document - or the same one restored by undo - holds different objects even
		// when the names match, and the rows paint visibility off the objects themselves.
		_shape = null;

		Rebuild();
	}

	public void Rebuild()
	{
		var objects = _anim?.Objects?.Where( o => o is not null && !string.IsNullOrWhiteSpace( o.Name ) ).ToList()
			?? new List<RigObject>();

		// Reference props are listed here too, as objects with no parts. They are posed and keyed
		// through the same selection, so leaving them out of the one list that shows what is in the
		// viewport would only raise the question of where they went.
		var props = _anim?.ReferenceProps?.Where( p => p is not null && !string.IsNullOrWhiteSpace( p.Name ) ).ToList()
			?? new List<ReferenceProp>();

		var unnamed = _anim?.Objects?.Count( o => o is not null && string.IsNullOrWhiteSpace( o.Name ) ) ?? 0;

		_unnamed.Visible = unnamed > 0;
		_unnamed.Text = unnamed == 1
			? "1 object has no name, so it has nothing to key under and is not shown"
			: $"{unnamed} objects have no name, so they have nothing to key under and are not shown";

		_empty.Visible = objects.Count == 0 && props.Count == 0;
		_tree.Visible = !_empty.Visible;

		var shape = string.Join( "\n", objects.Select( o => $"{o.Name}:{string.Join( ",", (o.Parts ?? new()).Select( p => $"{p?.Name}>{p?.ParentPart}" ) )}" ) )
			+ "\n|" + string.Join( "\n", props.Select( p => p.Name ) );

		if ( shape != _shape )
		{
			_shape = shape;
			BuildTree( objects, props );
		}
		else
		{
			// Same rows - only something they paint (visibility, a part count) can have changed.
			_tree.Update();
		}

		ShowSelection( _viewport.SelectedReferencePropName );
	}

	private void BuildTree( List<RigObject> objects, List<ReferenceProp> props )
	{
		_tree.Clear();
		_nodes.Clear();

		foreach ( var owner in objects )
		{
			var node = new RigObjectNode( this, owner.Name, owner.Name, RowKind.Object, null,
				() => owner.Visible, () => owner.Parts?.Count ?? 0 );

			_tree.AddItem( node );
			_nodes[node.Key] = node;

			AddFollowers( owner, node, null );
		}

		foreach ( var prop in props )
		{
			// Two props of the same name share one track and one row - the first one wins, which
			// is also the one the viewport selects.
			if ( _nodes.ContainsKey( prop.Name ) )
				continue;

			var node = new RigObjectNode( this, prop.Name, prop.Name, RowKind.Prop, null, () => prop.Visible, () => 0 );

			_tree.AddItem( node );
			_nodes[node.Key] = node;
		}
	}

	/// <summary>
	/// The parts that follow <paramref name="leader"/> - or only the object, when it is null - as
	/// rows under <paramref name="under"/>, each with its own followers beneath it.
	///
	/// NESTED, so the tree reads as what moves what: the eyes sit under the head. A flat list would
	/// leave following as something you had to remember rather than something you can see.
	/// </summary>
	private void AddFollowers( RigObject owner, RigObjectNode under, RigObjectPart leader )
	{
		foreach ( var part in owner.Parts ?? new List<RigObjectPart>() )
		{
			if ( part is null || string.IsNullOrWhiteSpace( part.Name ) || owner.ParentOf( part ) != leader )
				continue;

			var row = new RigObjectNode( this, RigTrackName.Qualify( owner.Name, part.Name ), part.Name, RowKind.Part, under,
				() => IsShown( owner, part ), () => owner.Parts.Count( p => p is not null && owner.ParentOf( p ) == part ) );

			under.AddItem( row );
			_nodes[row.Key] = row;

			AddFollowers( owner, row, part );
		}
	}

	/// <summary>Showing only when everything it follows is showing too - hiding the head hides the
	/// eyes, and the row should say so.</summary>
	private static bool IsShown( RigObject owner, RigObjectPart part )
	{
		if ( !owner.Visible )
			return false;

		for ( var p = part; p is not null; p = owner.ParentOf( p ) )
		{
			if ( !p.Visible )
				return false;
		}

		return true;
	}

	/// <summary>Marks the row for whatever the viewport has selected - or no row, when that is a
	/// bone or nothing. Everything above a part is opened so the row can actually be seen.</summary>
	public void ShowSelection( string key )
	{
		_syncing = true;

		try
		{
			if ( !string.IsNullOrEmpty( key ) && _nodes.TryGetValue( key, out var node ) )
			{
				for ( var up = node.Owner; up is not null; up = up.Owner )
					_tree.Open( up );

				_tree.SelectItem( node, false, true );
				_tree.ScrollTo( node );
			}
			else
			{
				_tree.SelectItems( Array.Empty<object>(), false, true );
			}
		}
		finally
		{
			_syncing = false;
		}

		UpdateButtons();
	}

	private void UpdateButtons()
	{
		_delete.Enabled = Resolve( _viewport.SelectedReferencePropName ).Exists;
	}

	// --- what a row means ---------------------------------------------------------------------

	private enum RowKind { Object, Part, Prop }

	/// <summary>The thing a track name means in this document. Exactly one of the three is set for
	/// a name that exists; a part carries its object as well, since a part is edited within it.</summary>
	private readonly record struct Target( RigObject Object, RigObjectPart Part, ReferenceProp Prop )
	{
		public bool Exists => Part is not null || Object is not null || Prop is not null;

		public bool Visible => Part?.Visible ?? Object?.Visible ?? Prop?.Visible ?? false;

		public string Kind => Part is not null ? "part" : Object is not null ? "object" : "prop";
	}

	private Target Resolve( string key )
	{
		if ( _anim is null || string.IsNullOrEmpty( key ) )
			return default;

		var (owner, part) = _anim.FindPartTarget( key );

		if ( part is not null )
			return new Target( owner, part, null );

		// FindPartTarget hands back the owner for "door/nonsense" too - only an exact name is the
		// object itself.
		if ( owner is not null && owner.Name == key )
			return new Target( owner, null, null );

		if ( _anim.ReferenceProps?.FirstOrDefault( p => p?.Name == key ) is { } prop )
			return new Target( null, null, prop );

		return default;
	}

	/// <summary>Records the edit through the window, then selects what it produced - after, not
	/// before, because the viewport only knows about a new thing once the window has rebuilt it.</summary>
	private void Commit( string label, string select )
	{
		Changed?.Invoke( label );

		if ( select is not null )
			_viewport.SelectReferenceProp( select );
	}

	// --- the row menu -------------------------------------------------------------------------

	private void OpenMenu( string key )
	{
		var target = Resolve( key );

		if ( !target.Exists )
			return;

		var menu = new Menu( this );

		menu.AddHeading( RigTrackName.Display( key ) );

		menu.AddOption( "Select", "my_location", () => _viewport.SelectReferenceProp( key ) )
			.StatusTip = "Select it in the viewport, with its numbers in the Inspector";

		menu.AddSeparator();

		menu.AddOption( "Rename", "edit", () => BeginRename( key ) )
			.StatusTip = "Its keyframes follow the new name";

		var visible = target.Visible;

		menu.AddOption( visible ? "Hide" : "Show", visible ? "visibility_off" : "visibility", () => SetVisible( key, !visible ) )
			.StatusTip = "Hidden, not deleted - its placement and keyframes stay";

		menu.AddOption( "Duplicate", "content_copy", () => Duplicate( key ) )
			.StatusTip = "A copy in the same place. Keyframes stay with the original.";

		if ( target.Part is not null )
			AddFollowMenu( menu, key, target.Object, target.Part );

		if ( target.Object is not null && target.Part is null )
		{
			menu.AddOption( "Add Part from Model...", "add", () => PickModel( target.Object ) )
				.StatusTip = "Add a compiled model to this object as another part";
		}

		menu.AddSeparator();

		menu.AddOption( "Delete", "delete", () => Delete( key ) )
			.StatusTip = "Remove it and its keyframes. Ctrl+Z brings both back.";

		menu.OpenAtCursor();
	}

	// --- following ----------------------------------------------------------------------------

	/// <summary>Follow → pick it in the viewport, stop following, or choose from the object's other
	/// parts by name. The pick comes first because an import's part names (mesh_6, mesh_13) say
	/// nothing about which is the head; the list is there for parts that have been renamed.</summary>
	private void AddFollowMenu( Menu menu, string key, RigObject owner, RigObjectPart part )
	{
		var leader = owner.ParentOf( part );
		var follow = menu.AddMenu( leader is null ? "Follow" : $"Follow ({leader.Name})", "link" );

		follow.AddOption( "Pick in Viewport...", "ads_click", () => BeginPickLeader( key ) )
			.StatusTip = "Then click the part it should follow - the head, for the eyes";

		var none = follow.AddOption( $"Only {owner.Name}", "link_off", () => SetFollow( key, null ) );
		none.Enabled = leader is not null;
		none.StatusTip = "Stop following another part - it stays where it is";

		var candidates = (owner.Parts ?? new List<RigObjectPart>())
			.Where( p => p is not null && !string.IsNullOrWhiteSpace( p.Name ) && !Follows( owner, p, part ) )
			.ToList();

		if ( candidates.Count > 0 )
			follow.AddSeparator();

		foreach ( var candidate in candidates )
		{
			var option = follow.AddOption( candidate.Name, null, () => SetFollow( key, candidate ) );
			option.Checkable = true;
			option.Checked = candidate == leader;
		}
	}

	/// <summary>Whether <paramref name="candidate"/> is <paramref name="part"/> or follows it,
	/// however far down - either way it cannot be what <paramref name="part"/> follows.</summary>
	private static bool Follows( RigObject owner, RigObjectPart candidate, RigObjectPart part )
	{
		for ( var up = candidate; up is not null; up = owner.ParentOf( up ) )
		{
			if ( up == part )
				return true;
		}

		return false;
	}

	/// <summary>Selects the part, then asks the viewport for the next thing clicked. Clicking the
	/// part's own object means follow only the object.</summary>
	private void BeginPickLeader( string key )
	{
		var target = Resolve( key );

		if ( target.Part is null )
			return;

		_viewport.SelectReferenceProp( key );

		_viewport.PickMovable( $"Click what {target.Part.Name} should follow - click empty space to cancel", picked =>
		{
			// Re-resolved: the document can have been undone under the question while it was open.
			var now = Resolve( key );
			var chosen = Resolve( picked );

			if ( now.Part is null )
				return;

			if ( chosen.Part is null && chosen.Object is not null && chosen.Object == now.Object )
			{
				SetFollow( key, null );
				return;
			}

			if ( chosen.Part is null || chosen.Object != now.Object )
			{
				RigStatusBar.Show( $"{RigTrackName.Display( picked )} is not part of {now.Object.Name} - a part can only follow another part of its own object" );
				return;
			}

			SetFollow( key, chosen.Part );
		} );
	}

	private void SetFollow( string key, RigObjectPart leader )
	{
		var target = Resolve( key );

		if ( target.Part is null )
			return;

		var owner = target.Object;
		var part = target.Part;

		if ( leader is not null && (owner.Parts?.Contains( leader ) != true || Follows( owner, leader, part )) )
		{
			RigStatusBar.Show( leader == part
				? $"{part.Name} cannot follow itself"
				: $"{leader.Name} already follows {part.Name}, so {part.Name} cannot follow it back" );
			return;
		}

		if ( owner.ParentOf( part ) == leader )
			return;

		Rehome( owner, part, leader );

		Commit( leader is null ? $"Unfollow {part.Name}" : $"{part.Name} Follows {leader.Name}", key );

		RigStatusBar.Show( leader is null
			? $"{part.Name} follows only {owner.Name} now"
			: $"{part.Name} follows {leader.Name} - move {leader.Name} and it comes along. It can still be moved on its own." );
	}

	/// <summary>
	/// Makes a part follow <paramref name="leader"/> (null: only its object) WITHOUT MOVING IT.
	///
	/// A part is stored relative to what it follows, so changing that changes what its numbers
	/// mean. Left alone they would now be read against the head instead of the object, and the eyes
	/// would jump somewhere else the instant you told them to follow. So the placement and every
	/// keyframe are re-expressed against the new leader, frame by frame: at each key the part is
	/// exactly where it was. Between keys it now rides along with the head - which is the point.
	///
	/// Each key is converted from its OWN value, not from BoneTrack.Evaluate at its frame - on a
	/// Stepped key Evaluate hands back the previous key's value.
	/// </summary>
	private void Rehome( RigObject owner, RigObjectPart part, RigObjectPart leader )
	{
		var oldLeader = owner.ParentOf( part );

		Transform Into( Transform local, float? frame )
		{
			var inObject = oldLeader is null ? local : _anim.PartInObject( owner, oldLeader, frame ).ToWorld( local );

			return leader is null ? inObject : _anim.PartInObject( owner, leader, frame ).ToLocal( inObject );
		}

		if ( _anim.FindPartTrack( RigTrackName.Qualify( owner.Name, part.Name ) ) is { } track )
		{
			foreach ( var key in track.Keyframes )
				key.Local = Into( key.Local, key.Frame );
		}

		var placed = Into( part.LocalTransform, null );

		part.Position = placed.Position;
		part.Rotation = placed.Rotation.Angles();
		part.Scale = placed.Scale.x;

		part.ParentPart = leader?.Name ?? "";
	}

	/// <summary>Rename in place, the same one-field popup Effigy's bone tree renames with.</summary>
	private void BeginRename( string key )
	{
		var target = Resolve( key );

		if ( !target.Exists )
			return;

		var current = target.Part?.Name ?? target.Object?.Name ?? target.Prop?.Name;

		var menu = new Menu( this );
		var edit = new LineEdit( current, menu ) { FixedWidth = 190 };

		edit.ReturnPressed += () =>
		{
			var wanted = edit.Text;
			menu.Close();
			Rename( key, wanted );
		};

		menu.AddWidget( edit );
		menu.OpenAtCursor();

		edit.Focus();
		edit.SelectAll();
	}

	private void Rename( string key, string wanted )
	{
		var target = Resolve( key );

		if ( !target.Exists )
			return;

		var name = CleanName( wanted );

		if ( string.IsNullOrEmpty( name ) )
			return;

		string newKey;

		if ( target.Part is not null )
		{
			if ( name == target.Part.Name )
				return;

			if ( target.Object.FindPart( name ) is not null )
			{
				RigStatusBar.Show( $"{target.Object.Name} already has a part called {name}" );
				return;
			}

			// Followers name what they follow, so they have to hear about the rename too.
			foreach ( var sibling in target.Object.Parts )
			{
				if ( sibling is not null && sibling.ParentPart == target.Part.Name )
					sibling.ParentPart = name;
			}

			target.Part.Name = name;
			newKey = RigTrackName.Qualify( target.Object.Name, name );
		}
		else
		{
			if ( name == key )
				return;

			if ( NameTaken( _anim, name ) )
			{
				RigStatusBar.Show( $"Something in this clip is already called {name} - names are what keyframes are stored under, so they have to be unique" );
				return;
			}

			if ( target.Object is not null )
				target.Object.Name = name;
			else
				target.Prop.Name = name;

			newKey = name;
		}

		// THE KEYFRAMES GO WITH IT. A track is found by name, so renaming the thing without its
		// tracks would leave the animation on a lane that belongs to nothing - which looks exactly
		// like the rename deleting it.
		RenameTracks( key, newKey );

		Commit( $"Rename {RigTrackName.Display( key )}", newKey );

		// The one thing a rename here cannot reach: a scene that already plays this clip binds its
		// GameObjects by name, in RigAnimPlayerComponent.Parts.
		RigStatusBar.Show( target.Part is null
			? $"Renamed to {name}. A scene playing this clip binds it by name - update RigAnimPlayerComponent's Parts there too."
			: $"Renamed to {name}" );
	}

	/// <summary>
	/// Moves every track a thing owns onto its new name: its own whole-part track, and every track
	/// underneath it - its parts, and the bones of anything skinned inside it.
	///
	/// THE MAIN MODEL'S BONES CANNOT BE CAUGHT BY THIS. Their names are bare and never contain the
	/// separator, and an exact match is only taken on a whole-part track - so an object that happens
	/// to share a bone's name still leaves that bone's track alone.
	/// </summary>
	private void RenameTracks( string oldKey, string newKey )
	{
		var prefix = oldKey + RigTrackName.Separator;

		foreach ( var track in _anim.BoneTracks )
		{
			if ( track.Target == TrackTarget.Part && track.BoneName == oldKey )
				track.BoneName = newKey;
			else if ( track.BoneName?.StartsWith( prefix, StringComparison.Ordinal ) == true )
				track.BoneName = newKey + track.BoneName[oldKey.Length..];
		}
	}

	private int RemoveTracks( string key )
	{
		var prefix = key + RigTrackName.Separator;

		return _anim.BoneTracks.RemoveAll( t =>
			(t.Target == TrackTarget.Part && t.BoneName == key)
			|| t.BoneName?.StartsWith( prefix, StringComparison.Ordinal ) == true );
	}

	private void SetVisible( string key, bool visible )
	{
		var target = Resolve( key );

		if ( !target.Exists )
			return;

		if ( target.Part is not null )
			target.Part.Visible = visible;
		else if ( target.Object is not null )
			target.Object.Visible = visible;
		else
			target.Prop.Visible = visible;

		Commit( $"{(visible ? "Show" : "Hide")} {RigTrackName.Display( key )}", null );
	}

	/// <summary>A copy in the same place, under a name nothing else has. Placement only: two
	/// objects playing the same keyframes is a different request from "another one of these", and
	/// Copy/Paste on the timeline already does it.</summary>
	private void Duplicate( string key )
	{
		var target = Resolve( key );

		if ( !target.Exists )
			return;

		string newKey;

		if ( target.Part is not null )
		{
			var copy = RigSnapshot.Clone( target.Part );
			copy.Name = UniquePartName( target.Object, target.Part.Name );

			target.Object.Parts.Insert( target.Object.Parts.IndexOf( target.Part ) + 1, copy );
			newKey = RigTrackName.Qualify( target.Object.Name, copy.Name );
		}
		else if ( target.Object is not null )
		{
			var copy = RigSnapshot.Clone( target.Object );
			copy.Name = UniqueObjectName( _anim, target.Object.Name );

			_anim.Objects.Insert( _anim.Objects.IndexOf( target.Object ) + 1, copy );
			newKey = copy.Name;
		}
		else
		{
			var copy = RigSnapshot.Clone( target.Prop );
			copy.Name = UniqueObjectName( _anim, target.Prop.Name );

			_anim.ReferenceProps.Insert( _anim.ReferenceProps.IndexOf( target.Prop ) + 1, copy );
			newKey = copy.Name;
		}

		Commit( $"Duplicate {RigTrackName.Display( key )}", newKey );

		RigStatusBar.Show( $"Duplicated as {RigTrackName.Display( newKey )} - in the same place, with no keyframes of its own yet" );
	}

	private void Delete( string key )
	{
		var target = Resolve( key );

		if ( !target.Exists )
			return;

		if ( target.Part is not null )
		{
			// Anything following it moves up to follow what it followed, staying put. Done before
			// the removal, while the chain being converted out of still exists.
			var leader = target.Object.ParentOf( target.Part );

			foreach ( var follower in target.Object.Parts.Where( p => p is not null && target.Object.ParentOf( p ) == target.Part ).ToList() )
				Rehome( target.Object, follower, leader );

			target.Object.Parts.Remove( target.Part );
		}
		else if ( target.Object is not null )
			_anim.Objects.Remove( target.Object );
		else
			_anim.ReferenceProps.Remove( target.Prop );

		// Its keyframes go too. Left behind they are lanes on the timeline driving nothing, and a
		// later object that happened to take the same name would inherit someone else's animation.
		var removed = RemoveTracks( key );

		Commit( $"Delete {RigTrackName.Display( key )}", null );

		RigStatusBar.Show( removed > 0
			? $"Deleted {RigTrackName.Display( key )} and {removed} track{(removed == 1 ? "" : "s")} - Ctrl+Z brings them back"
			: $"Deleted {RigTrackName.Display( key )} - Ctrl+Z brings it back" );
	}

	/// <summary>A compiled model as a new object with one part, or as another part of
	/// <paramref name="into"/>.</summary>
	private void PickModel( RigObject into )
	{
		if ( _anim is null )
			return;

		var picker = AssetPicker.Create( this, AssetType.Model, new AssetPicker.PickerOptions() );
		picker.Title = into is null ? "Add Model" : $"Add Part to {into.Name}";
		picker.OnAssetPicked = assets =>
		{
			if ( assets.FirstOrDefault() is not { } asset )
				return;

			var model = Model.Load( asset.Path );

			if ( model is null || model.IsError )
			{
				RigStatusBar.Show( $"{asset.Name} did not load as a model" );
				return;
			}

			AddModel( model, asset.Name, into );
		};

		picker.Show();
	}

	private void AddModel( Model model, string assetName, RigObject into )
	{
		var stem = CleanName( Path.GetFileNameWithoutExtension( assetName ?? "" ) );

		if ( string.IsNullOrEmpty( stem ) )
			stem = "model";

		// Re-resolved rather than trusted: the picker is modal-less, and the object it was opened
		// for can have been deleted or undone away while it was up.
		if ( into is not null && _anim.Objects?.Contains( into ) != true )
			into = null;

		if ( into is null )
		{
			var owner = new RigObject { Name = UniqueObjectName( _anim, stem ) };
			owner.Parts.Add( new RigObjectPart { Name = stem, Model = model } );

			_anim.Objects ??= new List<RigObject>();
			_anim.Objects.Add( owner );

			Commit( $"Add {owner.Name}", owner.Name );
			RigStatusBar.Show( $"Added {owner.Name} - drag it in the viewport, then press K to key it" );
			return;
		}

		into.Parts ??= new List<RigObjectPart>();

		var part = new RigObjectPart { Name = UniquePartName( into, stem ), Model = model };
		into.Parts.Add( part );

		var key = RigTrackName.Qualify( into.Name, part.Name );

		Commit( $"Add {RigTrackName.Display( key )}", key );
	}

	// --- names --------------------------------------------------------------------------------

	/// <summary>Trimmed, with the separator taken out - a slash in a name would make it read as an
	/// object and a part, and its track would be found under something else.</summary>
	private static string CleanName( string wanted ) =>
		(wanted ?? "").Trim().Replace( RigTrackName.Separator, '_' );

	/// <summary>Whether an object or a reference prop already has this name. They share one track
	/// namespace, so they share one set of names.</summary>
	private static bool NameTaken( RigAnimDocument anim, string name ) =>
		(anim.Objects?.Any( o => o?.Name == name ) ?? false)
		|| (anim.ReferenceProps?.Any( p => p?.Name == name ) ?? false);

	/// <summary>An object name nothing else is using - including the reference props, which share
	/// the same track namespace. Names are the identity a track is stored under, so two objects
	/// called "door" would share one track and move as one.</summary>
	public static string UniqueObjectName( RigAnimDocument anim, string wanted )
	{
		var taken = new HashSet<string>();

		foreach ( var existing in anim.Objects ?? new List<RigObject>() )
		{
			if ( existing is not null )
				taken.Add( existing.Name );
		}

		foreach ( var prop in anim.ReferenceProps ?? new List<ReferenceProp>() )
		{
			if ( prop is not null )
				taken.Add( prop.Name );
		}

		return Unique( wanted, taken );
	}

	private static string UniquePartName( RigObject owner, string wanted ) =>
		Unique( wanted, (owner.Parts ?? new List<RigObjectPart>()).Where( p => p is not null ).Select( p => p.Name ).ToHashSet() );

	/// <summary>A name not in <paramref name="taken"/>, recorded there so the next call cannot
	/// hand out the same one.</summary>
	public static string Unique( string wanted, HashSet<string> taken )
	{
		var name = string.IsNullOrWhiteSpace( wanted ) ? "part" : wanted.Trim();

		if ( taken.Add( name ) )
			return name;

		for ( var n = 2; ; n++ )
		{
			var candidate = $"{name}_{n}";

			if ( taken.Add( candidate ) )
				return candidate;
		}
	}

	/// <summary>One row: an object, a part of one, or a reference prop. Carries the track name
	/// rather than the label, since that is what selection, keying and the timeline all key off.</summary>
	private sealed class RigObjectNode : TreeNode<string>
	{
		private readonly RigObjectsPanel _panel;
		private readonly string _label;
		private readonly RowKind _kind;
		private readonly Func<bool> _visible;
		private readonly Func<int> _parts;

		public string Key => Value;

		/// <summary>The object row a part row sits under, so selecting the part from the viewport
		/// can open it.</summary>
		public RigObjectNode Owner { get; }

		public RigObjectNode( RigObjectsPanel panel, string key, string label, RowKind kind, RigObjectNode owner,
			Func<bool> visible, Func<int> parts ) : base( key )
		{
			_panel = panel;
			_label = label;
			_kind = kind;
			_visible = visible;
			_parts = parts;
			Owner = owner;
		}

		public override void OnActivated() => _panel.BeginRename( Key );

		public override bool OnContextMenu()
		{
			_panel.OpenMenu( Key );
			return true;
		}

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			// Hidden rows are dimmed rather than removed - hidden is a state you come back from,
			// and the row is how you come back.
			var visible = _visible();
			var alpha = visible ? 1f : 0.4f;

			// Green for objects, matching their handles in the viewport; parts dimmer and marked
			// with a smaller glyph, so the hierarchy reads without having to expand it.
			var (icon, size, tint) = _kind switch
			{
				RowKind.Part => ("chevron_right", 11, Theme.Green.WithAlpha( 0.6f )),
				RowKind.Prop => ("view_in_ar", 13, Theme.Green.WithAlpha( 0.8f )),
				_ => ("widgets", 13, Theme.Green),
			};

			Paint.SetPen( tint.WithAlpha( tint.a * alpha ) );
			Paint.DrawIcon( item.Rect, icon, size, TextFlag.LeftCenter );

			Paint.SetPen( Theme.Text.WithAlpha( alpha ) );
			Paint.DrawText( item.Rect.Shrink( 20, 0, 40, 0 ), _label, TextFlag.LeftCenter );

			var right = item.Rect.Shrink( 0, 0, 8, 0 );

			if ( !visible )
			{
				Paint.SetPen( Theme.TextControl.WithAlpha( 0.5f ) );
				Paint.DrawIcon( right, "visibility_off", 12, TextFlag.RightCenter );
				right = right.Shrink( 0, 0, 16, 0 );
			}

			var parts = _parts();

			if ( parts <= 0 )
				return;

			// How many parts are folded away in here. Without it a collapsed object with forty
			// parts looks exactly like one with none.
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.5f ) );
			Paint.DrawText( right, $"{parts}", TextFlag.RightCenter );
		}
	}
}
