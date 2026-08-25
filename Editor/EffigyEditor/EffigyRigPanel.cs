using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Authoring a skeleton on top of the studio's mesh: place bones by clicking the model, rename
/// or delete them from a tree, and optionally pin a body to a bone so skinning does not rely
/// entirely on nearest-bone weighting.
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

		var header = new Widget( this ) { Layout = Layout.Column() };
		header.Layout.Margin = new Sandbox.UI.Margin( 8, 4 );
		header.Layout.Spacing = 6;

		var toolRow = new Widget( header ) { Layout = Layout.Row() };
		toolRow.Layout.Spacing = 6;

		_addBoneButton = new Button( "Add Bone", "add" ) { Clicked = () => SetBoneToolActive( !_viewport.BoneToolActive ) };
		toolRow.Layout.Add( _addBoneButton, 1 );
		header.Layout.Add( toolRow );

		_assignBodyButton = new Button( "Assign Body", "link" )
		{
			Enabled = false,
			Clicked = ToggleAssignBody,
		};
		header.Layout.Add( _assignBodyButton );

		Layout.Add( header );

		_tree = new TreeView( this );
		_tree.OnSelectionChanged = objs =>
		{
			var index = objs?.FirstOrDefault() is BoneNode node ? Skeleton.IndexOf( node.Value ) : -1;
			_viewport.SelectBone( index );
			OnViewportBoneSelectionChanged( index );
		};
		Layout.Add( _tree, 1 );

		_viewport.RigSkeleton = Skeleton;
		_viewport.BoneSelectionChanged = OnViewportBoneSelectionChanged;

		RebuildTree();
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

	public void Refresh() => RebuildTree();

	// --- bone placement -------------------------------------------------------------------

	/// <summary>
	/// Arm or disarm the click-to-place tool. Each click extends the current chain from the last
	/// point to the new one, parented to the bone that segment just made — Blender's
	/// armature-extrude gesture. Escape closes the current chain (so the next click starts a new
	/// root); Escape again turns the tool off.
	/// </summary>
	private void SetBoneToolActive( bool active )
	{
		if ( active )
		{
			DisarmAssign();
			_viewport.DeselectBone();
			_selectedBone = -1;
			_assignBodyButton.Enabled = false;
			_chainHead = null;
			_chainParent = -1;
			_viewport.PendingBoneHead = null;
			_viewport.BonePointPicked = OnBonePointPicked;
			_viewport.BoneToolEscape = OnBoneToolEscape;
			_viewport.SetPickPrompt(
				"Click the model to place a bone. Click again to extend the chain. "
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

		if ( _bodyBoneMap.TryGetValue( bodyId, out var current ) && current == boneName )
			_bodyBoneMap.Remove( bodyId );
		else
			_bodyBoneMap[bodyId] = boneName;

		_viewport.SelectedBodyIds = BodiesOnBone( boneName );
		RebuildTree();
	}

	private List<string> BodiesOnBone( string boneName ) =>
		_bodyBoneMap.Where( kv => kv.Value == boneName ).Select( kv => kv.Key ).ToList();

	// --- selection sync (viewport <-> tree) -------------------------------------------------

	/// <summary>Called both from the tree's own selection callback and from the viewport when a
	/// bone is clicked in 3D — either way the two stay in step. SelectBone (called from the tree
	/// side) deliberately does not re-invoke this, so there is no feedback loop even though both
	/// paths land here.</summary>
	private void OnViewportBoneSelectionChanged( int index )
	{
		_selectedBone = index;
		_assignBodyButton.Enabled = index >= 0 && index < Skeleton.Count;

		if ( !_assignBodyButton.Enabled )
			DisarmAssign();

		if ( index >= 0 && index < Skeleton.Count && _nodes.TryGetValue( Skeleton.Bones[index].Name, out var node ) )
			_tree.SelectItem( node );
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

				try
				{
					Skeleton.RenameBone( index, name );

					foreach ( var body in BodiesOnBone( oldName ) )
						_bodyBoneMap[body] = name;
				}
				catch ( ArgumentException )
				{
					// Blank or a name already in use — leave the old one rather than crash.
				}
			}

			menu.Close();
			RebuildTree();
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
		Skeleton.RemoveBone( index );

		foreach ( var body in BodiesOnBone( name ) )
			_bodyBoneMap.Remove( body );

		_viewport.DeselectBone();
		_selectedBone = -1;
		_assignBodyButton.Enabled = false;
		DisarmAssign();

		RebuildTree();
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

			Paint.SetPen( Theme.Blue );
			Paint.DrawIcon( item.Rect, "fiber_manual_record", 12, TextFlag.LeftCenter );

			Paint.SetPen( Theme.Text );
			Paint.DrawText( item.Rect.Shrink( 20, 0, bodyCount > 0 ? 62 : 0, 0 ), Value, TextFlag.LeftCenter );

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
			Paint.SetPen( Theme.TextLight.WithAlpha( 0.6f ) );
			Paint.DrawText( item.Rect.Shrink( 4, 0, 0, 0 ), Value, TextFlag.LeftCenter );
		}
	}
}
