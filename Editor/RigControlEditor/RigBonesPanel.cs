using Editor;
using Sandbox;
using Marionette;
using System;
using System.Linq;

namespace Marionette.Editor;

/// <summary>
/// The BonesObject tab - header fields (Model / Asset Path / Rig Asset Path / Animation Speed)
/// over a real tree of the rig's bones, matching the reference video: pelvis's chain and a
/// separate root_IK utility chain (aim_matrix/foot_IK_target/hand_IK_attach) both as top-level
/// roots, each bone shown with its actual children rather than a flat list.
/// </summary>
internal sealed class RigBonesPanel : Widget
{
	private readonly RigViewport _viewport;
	private TreeView _tree;
	private ControlSheet _header;
	private Editor.Label _assetPathValue;

	public Action Edited { get; set; }

	private RigAnimDocument _anim;
	private Asset _asset;

	public RigBonesPanel( Widget parent, RigViewport viewport ) : base( parent )
	{
		Name = "BonesObject";
		WindowTitle = "BonesObject";
		SetWindowIcon( "polyline" );

		_viewport = viewport;

		Layout = Layout.Column();

		Layout.Add( RigHelpBox.Create( this,
			"Which model this clip poses, where the clip file lives, and a live tree of every " +
			"bone in the model.",
			new[]
			{
				RigHelpBox.S( "Model",
					"The skinned model this clip is authored against. Set it here (or drag a " +
					"model in) to load it into the viewport - nothing will show up there until " +
					"this is set." ),

				RigHelpBox.S( "Asset Path",
					"Read-only - shows where this .riganim file lives on disk once it's been " +
					"saved." ),

				RigHelpBox.S( "Rig Asset Path",
					"Points at a separate Control Rig (.ctrlrig) asset holding this model's " +
					"IK/Limit constraints - its own file rather than living inside this clip, " +
					"because several clips can share one rig. Drag a .ctrlrig asset onto this " +
					"field, or click the folder icon to browse for one. Optional - only needed if " +
					"you're using the Constraints tab. Don't have one yet? Right-click in the " +
					"Assets panel -> New -> Control Rig." ),

				RigHelpBox.S( "Animation Speed",
					"Frames per second this clip plays at, used by the Timeline's transport here " +
					"and by RigAnimPlayerComponent when it plays this clip back in-game." ),

				RigHelpBox.S( "Bone tree",
					"Every bone in Model, arranged the way it's actually rigged. Click a bone here " +
					"to select it, same as clicking its dot in the viewport - either way brings up " +
					"its rotate ring so you can pose it." ),
			} ) );

		// Its own row, outside the ControlSheet entirely - AddLayout( Layout.Row() ) rendered as
		// a plain, unstyled label with no value visible next to it (confirmed by report: it read
		// as missing altogether, not just differently styled from the other rows). A persistent
		// widget built once here, with only its text updated on rebuild, sidesteps that.
		var assetPathRow = new Widget( this ) { Layout = Layout.Row() };
		assetPathRow.Layout.Margin = new Sandbox.UI.Margin( 8, 4 );
		assetPathRow.Layout.Spacing = 8;
		assetPathRow.Layout.Add( new Editor.Label( "Asset Path" ) { FixedWidth = 110 } );
		_assetPathValue = assetPathRow.Layout.Add( new Editor.Label( "" ) { Enabled = false }, 1 );
		Layout.Add( assetPathRow );

		_header = new ControlSheet();
		Layout.Add( _header );

		_tree = new TreeView( this );
		_tree.OnSelectionChanged = objs =>
		{
			if ( objs?.FirstOrDefault() is string bone )
				_viewport.Select( bone );
		};
		Layout.Add( _tree, 1 );

		Rebuild();
	}

	public void SetAsset( Asset asset, RigAnimDocument anim )
	{
		_asset = asset;
		_anim = anim;
		RebuildHeader();
		Rebuild();
	}

	private void RebuildHeader()
	{
		_header.Clear( true );

		_assetPathValue.Text = _asset is null ? "(not saved yet - Ctrl+S)" : $"{_asset.Name}   {_asset.Path}";

		if ( _anim is null )
			return;

		var serialized = EditorTypeLibrary.GetSerializedObject( _anim );
		serialized.OnPropertyChanged += _ =>
		{
			Rebuild();
			Edited?.Invoke();
		};

		if ( serialized.TryGetProperty( nameof( RigAnimDocument.SourceModel ), out var model ) )
			_header.AddRow( model );

		if ( serialized.TryGetProperty( nameof( RigAnimDocument.RigAsset ), out var rigAsset ) )
			_header.AddRow( rigAsset );

		if ( serialized.TryGetProperty( nameof( RigAnimDocument.AnimationSpeed ), out var speed ) )
			_header.AddRow( speed );
	}

	public void Rebuild()
	{
		_tree.Clear();

		foreach ( var root in _viewport.RootBoneNames() )
		{
			var node = _tree.AddItem( new BoneTreeNode( root, _viewport ) );
			_tree.Open( node );
		}
	}

	private sealed class BoneTreeNode : TreeNode<string>
	{
		private readonly RigViewport _viewport;

		public BoneTreeNode( string bone, RigViewport viewport ) : base( bone )
		{
			_viewport = viewport;
		}

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			Paint.SetPen( Theme.Blue );
			Paint.DrawIcon( item.Rect, "fiber_manual_record", 12, TextFlag.LeftCenter );

			Paint.SetPen( Theme.Text );
			Paint.DrawText( item.Rect.Shrink( 20, 0, 0, 0 ), Value, TextFlag.LeftCenter );
		}

		protected override void BuildChildren()
		{
			Clear();

			AddItems( _viewport.ChildBoneNames( Value ).Select( c => new BoneTreeNode( c, _viewport ) ) );
		}
	}
}
