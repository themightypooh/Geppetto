using Editor;
using Sandbox;
using Marionette;
using System;

namespace Marionette.Tools;

/// <summary>
/// The Inspector tab - the selected bone's transform as numbers you can type into.
///
/// WHY THIS EXISTS: the bone gizmo's rings are world-axis aligned, so there is no handle that
/// means "raise the arm". Posing was therefore all feel and no measure - you dragged until it
/// looked right, and nothing could tell you what you had done or ask you to do it again. That
/// also made the tutorial impossible to write: "swing it forward and up" was the best available
/// instruction, because there was no value to name.
///
/// Numbers fix both ends. You can dial a pose in precisely, read back what a drag produced, and
/// copy a value from one side of a rig to the other.
///
/// THE NUMBERS ARE PARENT-SPACE, which is the same space keyframes are stored in and the same
/// space the viewport's corner readout shows. So a bone's rotation reads 0,0,0 at its rest pose
/// rather than at some arbitrary world orientation, and what you see here is literally what gets
/// written to the clip. World-space numbers would have been meaningless for a shoulder.
///
/// THERE IS NO SYNC LAYER HERE, DELIBERATELY. The first version of this panel kept a snapshot
/// object, pushed live values into it on every refresh, wrote them back on every change, and
/// carried two guard flags to stop those two directions fighting. Every bug the panel had came
/// from that: most visibly, typing 63 into a field produced 6, because the write bounced back
/// through a same-frame bone read - which returns the PRE-WRITE value, since overrides only fold
/// in on the scene tick - and overwrote the keystroke.
///
/// BoneTransform's properties now read and write the bone directly, so the sheet is editing the
/// pose rather than editing a copy that something else has to reconcile. That is how the engine's
/// own inspectors work, and it deletes the entire class of bug rather than guarding against it.
/// </summary>
internal sealed class RigInspectorPanel : Widget
{
	private readonly RigViewport _viewport;

	private readonly ControlSheet _sheet;
	private readonly Editor.Label _kindLabel;
	private readonly Editor.Label _boneLabel;
	private readonly BoneTransform _values;

	/// <summary>
	/// The numbers for a selected object, part or prop - supplied by the window, which owns the
	/// document and the keying.
	///
	/// HERE, NOT IN A POPUP. An object's transform used to be edited in a floating list editor
	/// opened from a property row, which covered the viewport and the timeline while you used it.
	/// The Inspector already shows the selection's numbers for a bone; an object is a selection
	/// like any other, so its numbers go in the same place.
	/// </summary>
	public Func<string, MovableTransform> MovableFor { get; set; }

	private MovableTransform _movable;

	/// <summary>Raised after a field edit has already been applied - the window turns this into an
	/// undo step, matching how every other panel here reports itself.</summary>
	public Action Edited { get; set; }

	/// <summary>The bone the sheet was last built for. Rebuilding on every refresh would steal
	/// focus out of the field you're typing into, so it only happens when the subject changes.</summary>
	private string _builtFor;

	public RigInspectorPanel( Widget parent, RigViewport viewport ) : base( parent )
	{
		Name = "Inspector";
		WindowTitle = "Inspector";
		SetWindowIcon( "tune" );

		_viewport = viewport;

		_values = new BoneTransform
		{
			Viewport = viewport,
			Edited = () => Edited?.Invoke()
		};

		Layout = Layout.Column();

		Layout.Add( RigHelpBox.Create( this,
			"The selected bone's pose as numbers, in the same parent-space form the clip stores.",
			new[]
			{
				RigHelpBox.S( "Rotation",
					"Pitch, yaw and roll relative to the parent bone. All zero means the bone is " +
					"sitting exactly in its rest pose. This is the one you want for almost all " +
					"posing - joints rotate, they don't slide." ),

				RigHelpBox.S( "Position",
					"Offset from the parent bone. Changing this pulls the joint away from its " +
					"parent and stretches the skin, so leave it alone unless you're placing a root " +
					"or an IK target." ),

				RigHelpBox.S( "Scale",
					"Uniform size of the bone, 1 = normal. Scaling stretches or squashes the skin " +
					"along the bone - the squash-and-stretch tool. It's carried through keyframes, " +
					"so a bone can grow and shrink over a clip." ),

				RigHelpBox.S( "Objects, parts and props",
					"Select one in the Objects list or the viewport and its numbers show here. A " +
					"part's are relative to its object - or to the part it follows, if it follows " +
					"one - so either can be moved without undoing anything riding on it. An object's are in the world - or relative to Follow Bone, " +
					"if one is set. With Link on, an edit here keys it at the playhead." ),
			} ) );

		var header = new Widget( this ) { Layout = Layout.Row() };
		header.Layout.Margin = new Sandbox.UI.Margin( 8, 4 );
		header.Layout.Spacing = 8;
		_kindLabel = header.Layout.Add( new Editor.Label( "Bone" ) { FixedWidth = 110 } );
		_boneLabel = header.Layout.Add( new Editor.Label( "" ), 1 );
		Layout.Add( header );

		_sheet = new ControlSheet();
		Layout.Add( _sheet );

		Layout.AddStretchCell();

		Refresh();
	}

	/// <summary>
	/// Points the sheet at whichever bone is selected.
	///
	/// This no longer pushes any values - the properties read the bone themselves - so it only has
	/// work to do when the SUBJECT changes. Calling it on every frame of a drag is harmless and
	/// costs nothing.
	/// </summary>
	public void Refresh()
	{
		var bone = _viewport?.SelectedBone;

		if ( !string.IsNullOrEmpty( bone ) )
		{
			RefreshBone( bone );
			return;
		}

		var key = _viewport?.SelectedReferencePropName;

		if ( !string.IsNullOrEmpty( key ) && RefreshMovable( key ) )
			return;

		_kindLabel.Text = "Bone";
		_boneLabel.Text = "(nothing selected)";

		if ( _builtFor is not null )
		{
			_sheet.Clear( true );
			_values.Bone = null;
			_movable = null;
			_builtFor = null;
		}
	}

	/// <summary>An object, part or prop. False when the window has nothing for the name, which
	/// leaves the sheet reading "nothing selected" rather than showing numbers for nothing.</summary>
	private bool RefreshMovable( string key )
	{
		// Prefixed, like the bone subject, so an object and a bone that share a name are still two
		// different things to build the sheet for.
		var subject = "movable:" + key;

		if ( _builtFor != subject || _movable is null )
		{
			var movable = MovableFor?.Invoke( key );

			if ( movable is null )
				return false;

			movable.Edited = () => Edited?.Invoke();

			_movable = movable;
			_values.Bone = null;
			_builtFor = subject;

			_sheet.Clear( true );

			var serialized = EditorTypeLibrary.GetSerializedObject( _movable );

			if ( serialized.TryGetProperty( nameof( MovableTransform.Rotation ), out var rotation ) )
				_sheet.AddRow( rotation );

			if ( serialized.TryGetProperty( nameof( MovableTransform.Position ), out var position ) )
				_sheet.AddRow( position );

			if ( serialized.TryGetProperty( nameof( MovableTransform.Scale ), out var scale ) )
				_sheet.AddRow( scale );

			// A part has no Follow Bone of its own - it follows its object.
			if ( _movable.HasFollowBone && serialized.TryGetProperty( nameof( MovableTransform.FollowBone ), out var follow ) )
				_sheet.AddRow( follow );
		}

		_kindLabel.Text = _movable.Kind;
		_boneLabel.Text = RigTrackName.Display( key );

		return true;
	}

	private void RefreshBone( string bone )
	{
		_kindLabel.Text = "Bone";

		// The bone, with the object it belongs to - the selection is a qualified track name now
		// that a clip can animate several models at once.
		_boneLabel.Text = RigTrackName.Display( bone );

		var subject = "bone:" + bone;

		if ( _builtFor == subject )
			return;

		_values.Bone = bone;
		_movable = null;
		_builtFor = subject;

		_sheet.Clear( true );

		var serialized = EditorTypeLibrary.GetSerializedObject( _values );

		if ( serialized.TryGetProperty( nameof( BoneTransform.Rotation ), out var rotation ) )
			_sheet.AddRow( rotation );

		if ( serialized.TryGetProperty( nameof( BoneTransform.Position ), out var position ) )
			_sheet.AddRow( position );

		if ( serialized.TryGetProperty( nameof( BoneTransform.Scale ), out var scale ) )
			_sheet.AddRow( scale );
	}
}

/// <summary>
/// The selected bone's pose, as live properties rather than a snapshot.
///
/// Reading a property reads the bone; writing one poses the bone. Nothing is cached, so there is
/// nothing to keep in step and no direction for the two to fight in - a drag changes what the
/// fields report simply because the fields report the bone, and typing changes the bone because
/// that is what the setter does.
///
/// Top-level and internal, matching every other type here, rather than nested and private: the
/// type library indexes the assembly's types, and a private nested class is the kind of thing it
/// may reasonably decline to surface.
/// </summary>
internal sealed class BoneTransform
{
	public RigViewport Viewport { get; set; }
	public string Bone { get; set; }
	public Action Edited { get; set; }

	/// <summary>The bone's current parent-space pose, or an identity-ish default when there isn't
	/// one - the sheet is built before a selection exists on first open.</summary>
	private Transform Current =>
		Viewport is not null && !string.IsNullOrEmpty( Bone )
			&& Viewport.TryGetLocalTransform( Bone, out var local )
				? local
				: Transform.Zero;

	[Property]
	public Angles Rotation
	{
		get => Current.Rotation.Angles();
		set
		{
			var current = Current;

			Write( new Transform( current.Position, value.ToRotation(), current.Scale ) );
		}
	}

	[Property]
	public Vector3 Position
	{
		get => Current.Position;
		set
		{
			var current = Current;

			Write( new Transform( value, current.Rotation, current.Scale ) );
		}
	}

	/// <summary>Uniform scale, read off the X axis of the (uniform) scale vector. Uniform because
	/// a bone scale that isn't is how a rig gets skewed past fixing by eye.</summary>
	[Property]
	public float Scale
	{
		get => Current.Scale.x;
		set
		{
			var current = Current;
			var s = value.Clamp( 0.001f, 1000f );

			Write( new Transform( current.Position, current.Rotation, new Vector3( s, s, s ) ) );
		}
	}

	private void Write( Transform local )
	{
		if ( Viewport is null || string.IsNullOrEmpty( Bone ) )
			return;

		Viewport.SetLocalTransform( Bone, local );

		Edited?.Invoke();
	}
}

/// <summary>
/// A selected object, part or prop's transform, as live properties - the same no-snapshot shape
/// as BoneTransform, for the same reason.
///
/// It reads and writes through delegates rather than holding the object, because the window looks
/// the thing up by name on every call. Undo replaces the document's objects with fresh copies, and
/// a sheet still holding the old one would be editing something nothing draws any more.
/// </summary>
internal sealed class MovableTransform
{
	/// <summary>"Object", "Part" or "Prop", for the Inspector's header.</summary>
	public string Kind { get; set; }

	public Func<Transform?> ReadLocal { get; set; }
	public Action<Transform> WriteLocal { get; set; }

	/// <summary>Null for a part, which follows its object rather than a bone.</summary>
	public Func<string> GetFollowBone { get; set; }
	public Action<string> SetFollowBone { get; set; }

	public Action Edited { get; set; }

	public bool HasFollowBone => GetFollowBone is not null && SetFollowBone is not null;

	private Transform Current => ReadLocal?.Invoke() ?? Transform.Zero;

	[Property]
	public Angles Rotation
	{
		get => Current.Rotation.Angles();
		set
		{
			var current = Current;

			Write( new Transform( current.Position, value.ToRotation(), current.Scale ) );
		}
	}

	[Property]
	public Vector3 Position
	{
		get => Current.Position;
		set
		{
			var current = Current;

			Write( new Transform( value, current.Rotation, current.Scale ) );
		}
	}

	/// <summary>Uniform, because that is all an object, part or prop stores.</summary>
	[Property]
	public float Scale
	{
		get => Current.Scale.x;
		set
		{
			var current = Current;
			var s = value.Clamp( 0.001f, 1000f );

			Write( new Transform( current.Position, current.Rotation, new Vector3( s, s, s ) ) );
		}
	}

	/// <summary>A bone of the main model this follows. Empty means it stays where Position puts
	/// it.</summary>
	[Property, Title( "Follow Bone" )]
	public string FollowBone
	{
		get => GetFollowBone?.Invoke() ?? "";
		set
		{
			if ( SetFollowBone is null )
				return;

			SetFollowBone( value ?? "" );
			Edited?.Invoke();
		}
	}

	private void Write( Transform local )
	{
		if ( WriteLocal is null )
			return;

		WriteLocal( local );
		Edited?.Invoke();
	}
}
