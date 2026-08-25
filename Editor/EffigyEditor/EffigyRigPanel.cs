using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Authoring a skeleton on top of the studio's mesh: place bones by clicking the model —
/// branching from a selected bone rather than only ever chaining in a straight line, which is
/// what a spine growing into two arms and two legs needs — rename or delete them from a tree,
/// mirror one side of a rig onto the other, and optionally pin a body to a bone so skinning does
/// not rely entirely on nearest-bone weighting.
///
/// This is the panel EffigyWindow has referenced since the day the rigged export path was
/// written — HasBones, Skeleton, BodyBoneMap and Refresh() are its contract with the window,
/// which reads them at compile time to decide between the rigged (DMX) and static (OBJ) export
/// paths. It simply never had a body until now.
///
/// The Skeleton lives here, not in the viewport. The viewport only draws it (EffigyViewport.cs),
/// poses it by dragging (also EffigyViewport.cs) and reports bone-placement clicks
/// (EffigyViewport.Rig.cs) — same division BodyPickMode already uses between the viewport and
/// EffigyFeatureDialog. One assignment of RigSkeleton in the constructor is enough because the
/// Skeleton is mutated in place; nothing here ever replaces the instance.
/// </summary>
internal sealed class EffigyRigPanel : Widget
{
	private readonly EffigyViewport _viewport;
	private PartStudio _studio;

	private readonly TreeView _tree;

	/// <summary>Keyed by bone NAME rather than index — TreeNode&lt;T&gt; needs a reference type
	/// for T, and a name survives exactly as long as the bone does, which an index does not (a
	/// delete shifts every later index).</summary>
	private readonly Dictionary<string, BoneNode> _nodes = new();

	private Button _addBoneButton;
	private Button _assignBodyButton;
	private Button _mirrorButton;

	// --- numeric inspector ------------------------------------------------------------------

	private Widget _inspector;
	private Editor.Label _inspectorName;
	private EffigyNumericField _headX, _headY, _headZ;
	private EffigyNumericField _tailX, _tailY, _tailZ;
	private Editor.Label _bodyListHeader;
	private Widget _bodyList;

	/// <summary>True while RefreshInspector is pushing values into the six fields — on a selection
	/// change, mainly. EffigyNumericField.SetValue does not fire ValueEdited on its own, but
	/// without this guard a stray re-entrant call would still read _selectedBone mid-refresh and
	/// write a half-updated bone back into the skeleton.</summary>
	private bool _editingInspector;

	public Skeleton Skeleton { get; } = new();

	public bool HasBones => Skeleton.Count > 0;

	private readonly Dictionary<string, string> _bodyBoneMap = new();

	/// <summary>Body id -> bone name. Keyed by name rather than index because SkinBinder.BindBodies
	/// takes it that way, and because a bone's index is not stable across a delete — its name is
	/// what a rename or a rebuild has to chase, not a slot in a list.</summary>
	public IReadOnlyDictionary<string, string> BodyBoneMap => _bodyBoneMap;

	// --- bone-placement chain state ------------------------------------------------------

	private int _selectedBone = -1;
	private Vec3? _chainHead;
	private int _chainParent = -1;
	private bool _assigningBody;

	public EffigyRigPanel( Widget parent, PartStudio studio, EffigyViewport viewport ) : base( parent )
	{
		_studio = studio;
		_viewport = viewport;

		Name = "Rig";
		WindowTitle = "Rig";
		SetWindowIcon( "account_tree" );

		Layout = Layout.Column();

		// Margin(8,4) + Spacing 8 matches the Parts and Features panel headers in this same
		// window — the outer rhythm should feel identical even though this header stacks two
		// button rows instead of one label row.
		var header = new Widget( this ) { Layout = Layout.Column() };
		header.Layout.Margin = new Sandbox.UI.Margin( 8, 4 );
		header.Layout.Spacing = 8;

		var toolRow = new Widget( header ) { Layout = Layout.Row() };
		toolRow.Layout.Spacing = 6;

		_addBoneButton = new Button( "Add Bone", "add" )
		{
			ToolTip = "Click the model to place a bone. Click again to extend a chain from it — "
				+ "select a bone first to branch a new chain from ITS tail instead of starting a new root.",
			Clicked = () => SetBoneToolActive( !_viewport.BoneToolActive ),
		};
		toolRow.Layout.Add( _addBoneButton, 1 );
		header.Layout.Add( toolRow );

		var secondRow = new Widget( header ) { Layout = Layout.Row() };
		secondRow.Layout.Spacing = 6;

		_assignBodyButton = new Button( "Assign Body", "link" )
		{
			Enabled = false,
			ToolTip = "Select a bone, then click bodies in the viewport to pin them to it. "
				+ "Optional — unassigned bodies still skin, to whichever bone is nearest.",
			Clicked = ToggleAssignBody,
		};
		secondRow.Layout.Add( _assignBodyButton, 1 );

		_mirrorButton = new Button( "Mirror", "flip" )
		{
			Enabled = false,
			ToolTip = "Mirror the selected bone (and everything under it) across Y=0 — this "
				+ "project's left/right axis — onto the same parent it already hangs from.",
			Clicked = MirrorSelectedBone,
		};
		secondRow.Layout.Add( _mirrorButton, 1 );

		header.Layout.Add( secondRow );

		Layout.Add( header );

		_tree = new TreeView( this );
		_tree.OnSelectionChanged = objs =>
		{
			var index = objs?.FirstOrDefault() is BoneNode node ? Skeleton.IndexOf( node.Value ) : -1;
			_viewport.SelectBone( index );
			OnViewportBoneSelectionChanged( index );
		};
		Layout.Add( _tree, 1 );

		BuildInspector();
		Layout.Add( _inspector );

		_viewport.RigSkeleton = Skeleton;
		_viewport.BoneSelectionChanged = OnViewportBoneSelectionChanged;
		_viewport.BonePosed = OnBonePosed;

		Refresh();
	}

	/// <summary>A different studio entirely — New Studio or Load Document — not a rebuild of the
	/// same one. The old skeleton was authored against the old mesh and BodyBoneMap's ids belong
	/// to bodies that no longer exist, so both are cleared rather than carried into a model they
	/// were never placed on.</summary>
	public void SetStudio( PartStudio studio )
	{
		_studio = studio;

		SetBoneToolActive( false );
		DisarmAssign();

		Skeleton.Bones.Clear();
		_bodyBoneMap.Clear();
		_selectedBone = -1;
		_viewport.DeselectBone();

		Refresh();
	}

	/// <summary>
	/// Fired right before a bone is placed, deleted, renamed, or mirrored — the same "before"
	/// moment EffigyViewport.SketchEditing exists for on the sketch side, wired to the window's own
	/// RecordUndo the same way. Deliberately NOT fired from the numeric inspector's fields: those
	/// fire on every keystroke that still parses, and putting one undo step per character on the
	/// stack is exactly what RecordUndo's own doc comment says a parameter drag must not do either.
	/// </summary>
	public Action RigChanging { get; set; }

	/// <summary>
	/// Replace the skeleton and body-bone map wholesale — the undo/redo restore path. Unlike
	/// SetStudio, the mesh underneath hasn't changed, only the rig; still clears any in-progress
	/// placement or body-assign tool state, since neither survives meaningfully across a jump to a
	/// different point in history.
	/// </summary>
	public void RestoreRig( Skeleton snapshot, IReadOnlyDictionary<string, string> bodyBoneMap )
	{
		SetBoneToolActive( false );
		DisarmAssign();

		Skeleton.Bones.Clear();
		Skeleton.Bones.AddRange( snapshot.Bones.Select( b => b.Clone() ) );

		_bodyBoneMap.Clear();

		foreach ( var (body, bone) in bodyBoneMap )
			_bodyBoneMap[body] = bone;

		_viewport.DeselectBone();
		_selectedBone = -1;

		Refresh();
	}

	public void Refresh()
	{
		RebuildTree();
		RefreshInspector();
	}

	// --- bone placement -------------------------------------------------------------------

	/// <summary>
	/// Arm or disarm the click-to-place tool. Each click extends the current chain from the last
	/// point to the new one, parented to the bone that segment just made — Blender's
	/// armature-extrude gesture.
	///
	/// If a bone was selected when the tool was armed, the chain starts from THAT bone's tail,
	/// parented to it, instead of starting fresh — the same "extrude from the selected tip"
	/// gesture Blender uses, and the only way this tool can build anything but one straight chain.
	/// Without it there would be no way to grow a spine into two arms and two legs: every new
	/// chain would have to start over as its own disconnected root.
	///
	/// Escape closes the current chain (so the next click starts a new, unparented root); Escape
	/// again turns the tool off.
	/// </summary>
	private void SetBoneToolActive( bool active )
	{
		if ( active )
		{
			DisarmAssign();

			var branchFrom = _selectedBone;

			_viewport.DeselectBone();
			_selectedBone = -1;
			_assignBodyButton.Enabled = false;
			_mirrorButton.Enabled = false;

			if ( branchFrom >= 0 && branchFrom < Skeleton.Count )
			{
				_chainHead = Skeleton.TailWorld( branchFrom );
				_chainParent = branchFrom;
				_viewport.PendingBoneHead = _chainHead;
			}
			else
			{
				_chainHead = null;
				_chainParent = -1;
				_viewport.PendingBoneHead = null;
			}

			_viewport.BonePointPicked = OnBonePointPicked;
			_viewport.BoneToolEscape = OnBoneToolEscape;
			_viewport.SetPickPrompt( branchFrom >= 0
				? $"Click to extend a new bone from '{Skeleton.Bones[branchFrom].Name}'. "
					+ "Escape to end the chain, Escape again to stop."
				: "Click the model to place a bone. Click again to extend the chain. "
					+ "Escape to end the chain, Escape again to stop." );
		}
		else
		{
			_viewport.BonePointPicked = null;
			_viewport.BoneToolEscape = null;
			_viewport.PendingBoneHead = null;
			_chainHead = null;
			_chainParent = -1;
			_viewport.SetPickPrompt( "" );
		}

		_viewport.BoneToolActive = active;
		_addBoneButton.Text = active ? "Placing… (Esc to stop)" : "Add Bone";
	}

	private void OnBonePointPicked( Vec3 point )
	{
		if ( _chainHead is not { } head )
		{
			_chainHead = point;
			_viewport.PendingBoneHead = point;
			return;
		}

		// A double-click landing on the same spot would otherwise hit AddBoneFromPoints' zero-
		// length guard and throw — worth swallowing quietly rather than teaching the tool to crash
		// on a mis-click.
		if ( (point - head).Length < 0.01f )
			return;

		RigChanging?.Invoke();
		var index = Skeleton.AddBoneFromPoints( NextBoneName(), _chainParent, head, point );

		_chainHead = point;
		_chainParent = index;
		_viewport.PendingBoneHead = point;

		RebuildTree();
	}

	private void OnBoneToolEscape()
	{
		if ( _chainHead is not null )
		{
			_chainHead = null;
			_chainParent = -1;
			_viewport.PendingBoneHead = null;
			return;
		}

		SetBoneToolActive( false );
	}

	private string NextBoneName()
	{
		var n = Skeleton.Count + 1;
		while ( Skeleton.IndexOf( $"bone_{n}" ) >= 0 )
			n++;
		return $"bone_{n}";
	}

	// --- body assignment -------------------------------------------------------------------

	/// <summary>
	/// Arm or disarm assigning bodies to the selected bone. While armed, clicking a body toggles
	/// it onto (or off of) the selected bone — same "click to add or remove" feel
	/// EffigyFeatureDialog's body picker already has, reusing the same BodyPickMode the viewport
	/// exposes for it.
	///
	/// Optional: BindBodies falls back to nearest-bone rigid weighting, smoothed across mesh
	/// adjacency, for anything left unassigned. A skeleton with no assignments at all still
	/// exports and skins — this just lets a specific part be pinned to a specific bone rather than
	/// left to distance.
	/// </summary>
	private void ToggleAssignBody()
	{
		if ( _assigningBody )
		{
			DisarmAssign();
			return;
		}

		if ( _selectedBone < 0 || _selectedBone >= Skeleton.Count )
			return;

		_assigningBody = true;

		// EffigyFeatureDialog is the only other thing that ever set this list, so it has to be
		// refreshed here rather than assumed — otherwise a click hits whatever bodies a feature
		// dialog last armed against, or nothing at all if none has run yet this session.
		_viewport.SetPickableBodies( _studio?.Bodies );

		_viewport.BodyPickMode = true;
		_viewport.BodyPicked = OnBodyPicked;
		_viewport.SelectedBodyIds = BodiesOnBone( Skeleton.Bones[_selectedBone].Name );
		_viewport.SetPickPrompt(
			$"Click bodies to assign to '{Skeleton.Bones[_selectedBone].Name}'. Click again to unassign." );
		_assignBodyButton.Text = "Done Assigning";
	}

	private void DisarmAssign()
	{
		if ( !_assigningBody )
			return;

		_assigningBody = false;
		_viewport.BodyPickMode = false;
		_viewport.BodyPicked = null;
		_viewport.SelectedBodyIds = null;
		_viewport.SetPickPrompt( "" );
		_assignBodyButton.Text = "Assign Body";
	}

	private void OnBodyPicked( string bodyId )
	{
		if ( string.IsNullOrEmpty( bodyId ) || _selectedBone < 0 || _selectedBone >= Skeleton.Count )
			return;

		var boneName = Skeleton.Bones[_selectedBone].Name;

		RigChanging?.Invoke();

		if ( _bodyBoneMap.TryGetValue( bodyId, out var current ) && current == boneName )
			_bodyBoneMap.Remove( bodyId );
		else
			_bodyBoneMap[bodyId] = boneName;

		_viewport.SelectedBodyIds = BodiesOnBone( boneName );
		RebuildTree();
		RefreshBodyList();
	}

	private List<string> BodiesOnBone( string boneName ) =>
		_bodyBoneMap.Where( kv => kv.Value == boneName ).Select( kv => kv.Key ).ToList();

	// --- mirroring -----------------------------------------------------------------------

	/// <summary>
	/// Mirror the selected bone and everything beneath it across Y=0 — this tool's own left/right
	/// axis (EffigyViewport's header comment: "+x forward, +y left, +z up") — grafted onto the
	/// same parent the original hangs from. That default covers the ordinary case, an arm or a leg
	/// mirrored onto the spine bone it already shares, without asking first; nothing stops running
	/// it again on a different selection for a less usual split.
	/// </summary>
	private void MirrorSelectedBone()
	{
		if ( _selectedBone < 0 || _selectedBone >= Skeleton.Count )
			return;

		var parent = Skeleton.Bones[_selectedBone].Parent;

		RigChanging?.Invoke();
		var newRoot = Skeleton.MirrorSubtree( _selectedBone, new Vec3( 0, 1, 0 ), parent );

		RebuildTree();
		_viewport.SelectBone( newRoot );
		OnViewportBoneSelectionChanged( newRoot );
	}

	// --- numeric inspector -----------------------------------------------------------------

	/// <summary>
	/// Head and tail as typeable X/Y/Z, the same "type it rather than eyeball it off a drag"
	/// escape hatch the CAD side's EffigyNumericField exists for — placing a bone is a raycast
	/// against the mesh, which is precise about WHAT it hit and nowhere near precise enough for a
	/// joint that needs to land at, say, exactly X=0 on a spine.
	/// </summary>
	private void BuildInspector()
	{
		_inspector = new Widget( this ) { Layout = Layout.Column(), Visible = false };
		_inspector.Layout.Margin = new Sandbox.UI.Margin( 8, 6 );
		_inspector.Layout.Spacing = 4;

		_inspectorName = new Editor.Label( "" ) { Color = Theme.TextControl };
		_inspector.Layout.Add( _inspectorName );

		var headRow = new Widget( _inspector ) { Layout = Layout.Row() };
		headRow.Layout.Spacing = 4;
		headRow.Layout.Add( new Editor.Label( "Head" ) { FixedWidth = 36 } );
		_headX = AddVectorField( headRow, OnHeadFieldEdited );
		_headY = AddVectorField( headRow, OnHeadFieldEdited );
		_headZ = AddVectorField( headRow, OnHeadFieldEdited );
		_inspector.Layout.Add( headRow );

		var tailRow = new Widget( _inspector ) { Layout = Layout.Row() };
		tailRow.Layout.Spacing = 4;
		tailRow.Layout.Add( new Editor.Label( "Tail" ) { FixedWidth = 36 } );
		_tailX = AddVectorField( tailRow, OnTailFieldEdited );
		_tailY = AddVectorField( tailRow, OnTailFieldEdited );
		_tailZ = AddVectorField( tailRow, OnTailFieldEdited );
		_inspector.Layout.Add( tailRow );

		// A count on the tree row is enough to notice a bone has bodies; fixing a WRONG one from
		// there means re-arming Assign Body and hunting for it in the viewport. Naming each one
		// here, with its own remove button, is the actual undo-a-mistake path.
		_bodyListHeader = new Editor.Label( "" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) };
		_inspector.Layout.Add( _bodyListHeader );

		_bodyList = new Widget( _inspector ) { Layout = Layout.Column() };
		_bodyList.Layout.Spacing = 2;
		_inspector.Layout.Add( _bodyList );
	}

	private static EffigyNumericField AddVectorField( Widget row, Action<float> edited )
	{
		var field = new EffigyNumericField( row, 0f ) { FixedWidth = 60, ValueEdited = edited };
		row.Layout.Add( field );
		return field;
	}

	/// <summary>Reload all six fields from the skeleton — on a selection change, a rename, or a
	/// live pose drag (OnBonePosed). Never called after a field's own edit: head and tail are
	/// independent, so nothing else needs to move, and re-reading the field just typed into would
	/// fight the cursor and reformat a mid-keystroke expression back to a plain number.</summary>
	private void RefreshInspector()
	{
		if ( _selectedBone < 0 || _selectedBone >= Skeleton.Count )
		{
			_inspector.Visible = false;
			return;
		}

		_editingInspector = true;

		_inspector.Visible = true;
		_inspectorName.Text = Skeleton.Bones[_selectedBone].Name;

		var head = Skeleton.HeadWorld( _selectedBone );
		_headX.SetValue( head.x );
		_headY.SetValue( head.y );
		_headZ.SetValue( head.z );

		var tail = Skeleton.TailWorld( _selectedBone );
		_tailX.SetValue( tail.x );
		_tailY.SetValue( tail.y );
		_tailZ.SetValue( tail.z );

		_editingInspector = false;

		RefreshBodyList();
	}

	/// <summary>Named rows for every body assigned to the selected bone, each with its own remove
	/// button — the count on the tree row says a bone has assignments, this is what lets a wrong
	/// one be found and undone without re-arming Assign Body and hunting in the viewport.</summary>
	private void RefreshBodyList()
	{
		_bodyList.Layout.Clear( true );

		if ( _selectedBone < 0 || _selectedBone >= Skeleton.Count )
		{
			_bodyListHeader.Visible = false;
			return;
		}

		var boneName = Skeleton.Bones[_selectedBone].Name;
		var bodies = BodiesOnBone( boneName );

		_bodyListHeader.Visible = bodies.Count > 0;
		_bodyListHeader.Text = bodies.Count == 0 ? "" : "Assigned bodies";

		foreach ( var bodyId in bodies )
		{
			var row = new Widget( _bodyList ) { Layout = Layout.Row() };
			row.Layout.Spacing = 4;

			var name = _studio?.Bodies.FirstOrDefault( b => b.Id == bodyId )?.Name ?? bodyId;
			row.Layout.Add( new Editor.Label( name ) { Color = Theme.TextLight }, 1 );

			// IconSize 16 matches every other IconButton in this window (the feature dialog's
			// Accept/Cancel, the tree eye's own icon) rather than inventing a smaller one here.
			row.Layout.Add( new IconButton( "close", () => UnassignBody( bodyId ) )
			{
				IconSize = 16,
				Background = Color.Transparent,
				ToolTip = $"Unassign from '{boneName}'",
			} );

			_bodyList.Layout.Add( row );
		}
	}

	private void UnassignBody( string bodyId )
	{
		if ( _selectedBone < 0 || _selectedBone >= Skeleton.Count )
			return;

		RigChanging?.Invoke();
		_bodyBoneMap.Remove( bodyId );

		// Keep the viewport's highlight honest if Assign Body is still armed — otherwise the body
		// just removed stays lit as if it were still assigned.
		if ( _assigningBody )
			_viewport.SelectedBodyIds = BodiesOnBone( Skeleton.Bones[_selectedBone].Name );

		RebuildTree();
		RefreshBodyList();
	}

	private void OnHeadFieldEdited( float _ )
	{
		if ( _editingInspector || _selectedBone < 0 || _selectedBone >= Skeleton.Count )
			return;

		var head = new Vec3( _headX.Value, _headY.Value, _headZ.Value );
		var tail = Skeleton.TailWorld( _selectedBone );

		ApplyHeadTailEdit( head, tail );
	}

	private void OnTailFieldEdited( float _ )
	{
		if ( _editingInspector || _selectedBone < 0 || _selectedBone >= Skeleton.Count )
			return;

		var head = Skeleton.HeadWorld( _selectedBone );
		var tail = new Vec3( _tailX.Value, _tailY.Value, _tailZ.Value );

		ApplyHeadTailEdit( head, tail );
	}

	/// <summary>
	/// SetHeadTail throws on a zero-length bone, which a field mid-edit passes through constantly
	/// — typing "-1.5" one character at a time crosses zero if the tail happens to share that
	/// axis's value. Swallowed the same way EffigyNumericField itself treats an unparseable
	/// string: the model just stops agreeing with the field until the text makes sense again,
	/// rather than an exception reaching the user over a keystroke.
	///
	/// Deliberately does NOT call RefreshInspector after a successful edit: head and tail are
	/// independent (moving one never changes the other's stored value), so the other five fields
	/// have nothing new to show, and re-reading the field just typed into would reformat whatever
	/// expression is mid-keystroke back to a plain number — the same trap BuildFloatRow's own
	/// comment warns about on the CAD side.
	/// </summary>
	private void ApplyHeadTailEdit( Vec3 head, Vec3 tail )
	{
		try
		{
			Skeleton.SetHeadTail( _selectedBone, head, tail );
		}
		catch ( ArgumentException )
		{
			// Momentarily zero-length mid-edit — leave the model alone until the text means
			// something again, same as EffigyNumericField's own "?" readout for unparsed text.
		}
	}

	private void OnBonePosed( int index )
	{
		if ( index == _selectedBone )
			RefreshInspector();
	}

	// --- selection sync (viewport <-> tree) -------------------------------------------------

	/// <summary>Called both from the tree's own selection callback and from the viewport when a
	/// bone is clicked in 3D — either way the two stay in step. SelectBone (called from the tree
	/// side) deliberately does not re-invoke this, so there is no feedback loop even though both
	/// paths land here.</summary>
	private void OnViewportBoneSelectionChanged( int index )
	{
		_selectedBone = index;
		_assignBodyButton.Enabled = index >= 0 && index < Skeleton.Count;
		_mirrorButton.Enabled = _assignBodyButton.Enabled;

		if ( !_assignBodyButton.Enabled )
			DisarmAssign();

		if ( index >= 0 && index < Skeleton.Count && _nodes.TryGetValue( Skeleton.Bones[index].Name, out var node ) )
			_tree.SelectItem( node );

		RefreshInspector();
	}

	// --- rename / delete ---------------------------------------------------------------------

	/// <summary>Rename in place, the same one-field popup every tree in this tool renames with.
	/// Bodies already assigned to the bone follow the rename — BindBodies keys BodyBoneMap by
	/// name, so leaving the old name behind would point those bodies at a bone that no longer
	/// exists and throw at export time.</summary>
	private void BeginRename( int index )
	{
		if ( index < 0 || index >= Skeleton.Count )
			return;

		var menu = new Menu( this );
		var edit = new LineEdit( Skeleton.Bones[index].Name, menu ) { FixedWidth = 190 };

		edit.ReturnPressed += () =>
		{
			var name = edit.Text?.Trim();

			if ( !string.IsNullOrWhiteSpace( name ) )
			{
				var oldName = Skeleton.Bones[index].Name;

				if ( name != oldName )
				{
					try
					{
						RigChanging?.Invoke();
						Skeleton.RenameBone( index, name );

						foreach ( var body in BodiesOnBone( oldName ) )
							_bodyBoneMap[body] = name;
					}
					catch ( ArgumentException )
					{
						// Blank or a name already in use — leave the old one rather than crash.
					}
				}
			}

			menu.Close();
			RebuildTree();
			RefreshInspector();
		};

		menu.AddWidget( edit );
		menu.OpenAtCursor();

		edit.Focus();
		edit.SelectAll();
	}

	/// <summary>Delete a bone. Its children re-parent to its own parent (Skeleton.RemoveBone), and
	/// anything that was assigned to it falls back to BindBodies' nearest-bone default rather than
	/// naming a bone that is no longer there.</summary>
	private void DeleteBone( int index )
	{
		if ( index < 0 || index >= Skeleton.Count )
			return;

		var name = Skeleton.Bones[index].Name;

		RigChanging?.Invoke();
		Skeleton.RemoveBone( index );

		foreach ( var body in BodiesOnBone( name ) )
			_bodyBoneMap.Remove( body );

		_viewport.DeselectBone();
		_selectedBone = -1;
		_assignBodyButton.Enabled = false;
		_mirrorButton.Enabled = false;
		DisarmAssign();

		RebuildTree();
		RefreshInspector();
	}

	private void OpenBoneMenu( int index )
	{
		if ( index < 0 || index >= Skeleton.Count )
			return;

		var menu = new Menu( this );
		menu.AddOption( "Rename", "edit", () => BeginRename( index ) );
		menu.AddSeparator();
		menu.AddOption( "Delete", "delete", () => DeleteBone( index ) );
		menu.OpenAtCursor();
	}

	// --- tree ---------------------------------------------------------------------------------

	private void RebuildTree()
	{
		_tree.Clear();
		_nodes.Clear();

		if ( Skeleton.Count == 0 )
		{
			_tree.AddItem( new EmptyRigNode() );
			return;
		}

		for ( var i = 0; i < Skeleton.Count; i++ )
		{
			if ( Skeleton.Bones[i].Parent != -1 )
				continue;

			var node = _tree.AddItem( new BoneNode( this, Skeleton.Bones[i].Name ) );
			_tree.Open( node );
		}

		if ( _selectedBone >= 0 && _selectedBone < Skeleton.Count
			&& _nodes.TryGetValue( Skeleton.Bones[_selectedBone].Name, out var selected ) )
			_tree.SelectItem( selected );
	}

	private sealed class BoneNode : TreeNode<string>
	{
		private readonly EffigyRigPanel _panel;

		public BoneNode( EffigyRigPanel panel, string name ) : base( name )
		{
			_panel = panel;
			_panel._nodes[name] = this;
		}

		public override void OnActivated() => _panel.BeginRename( _panel.Skeleton.IndexOf( Value ) );

		public override bool OnContextMenu()
		{
			_panel.OpenBoneMenu( _panel.Skeleton.IndexOf( Value ) );
			return true;
		}

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			var bodyCount = _panel._bodyBoneMap.Values.Count( v => v == Value );

			// Icon size 14 and a 22px left margin for the label — Effigy's own Features and Parts
			// trees both use this pairing (RigControlEditor's separate bone tree uses a smaller
			// 12/20 that has no business bleeding into this window), and this tree sits in docks
			// right next to those two.
			Paint.SetPen( Theme.Blue );
			Paint.DrawIcon( item.Rect, "fiber_manual_record", 14, TextFlag.LeftCenter );

			Paint.SetPen( Theme.Text );
			Paint.DrawText( item.Rect.Shrink( 22, 0, bodyCount > 0 ? 62 : 0, 0 ), Value, TextFlag.LeftCenter );

			if ( bodyCount == 0 )
				return;

			Paint.SetPen( Theme.TextLight.WithAlpha( 0.6f ) );
			Paint.DrawText( item.Rect.Shrink( 0, 0, 8, 0 ), $"{bodyCount} bod{(bodyCount == 1 ? "y" : "ies")}",
				TextFlag.RightCenter );
		}

		protected override void BuildChildren()
		{
			Clear();

			var index = _panel.Skeleton.IndexOf( Value );
			if ( index < 0 )
				return;

			AddItems( _panel.Skeleton.Children( index )
				.Select( c => new BoneNode( _panel, _panel.Skeleton.Bones[c].Name ) ) );
		}
	}

	/// <summary>Shown instead of an empty tree, same reasoning as EffigyPartsPanel's — a blank
	/// panel reads as broken rather than as "nothing here yet".</summary>
	private sealed class EmptyRigNode : TreeNode<string>
	{
		public EmptyRigNode() : base( "No bones yet — click Add Bone" ) { }

		public override void OnPaint( VirtualWidget item )
		{
			// 8px left margin, matching EmptyPartsNode's empty-state row in the same window.
			Paint.SetPen( Theme.TextLight.WithAlpha( 0.6f ) );
			Paint.DrawText( item.Rect.Shrink( 8, 0, 0, 0 ), Value, TextFlag.LeftCenter );
		}
	}
}
