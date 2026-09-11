using Sandbox;

namespace Marionette;

/// <summary>
/// One camera shot in a Marionette clip. See <see cref="RigAnimDocument.Cameras"/>.
///
/// A CAMERA IS A SHOT, NOT A LIGHT. It does not light anything and it is not a viewport default -
/// it is where you looked from when a moment read right, saved so the same framing can be played
/// back or exported later. That is the whole reason an animation tool has cameras: the framing you
/// find by flying the viewport is the framing you want the final shot to have, and writing the
/// numbers down is the only way to get back to it.
///
/// A CAMERA IS WORKSPACE UNTIL YOU SAY OTHERWISE. Like a light, it starts as viewport-only. Tick
/// Export With Clip and it becomes part of the shot: RigAnimPlayerComponent spawns a camera at it
/// when the clip plays, and the export writes it out alongside the animation.
///
/// The fields are the ones the engine's own camera component takes, under the same names, so what
/// you set here is what the scene gets and there is no translation layer to be wrong about.
/// </summary>
public sealed class RigCamera
{
	[Property] public string Name { get; set; } = "camera";

	/// <summary>Off keeps the camera in the list but stops it being drawn in the viewport or
	/// spawned at runtime - the way to try a shot out without deleting it.</summary>
	[Property] public bool Enabled { get; set; } = true;

	/// <summary>
	/// Whether this camera is part of the shot or only part of the workspace.
	///
	/// OFF BY DEFAULT: a camera you dropped to look at an elbow from has no business turning up in
	/// somebody's game. On, and RigAnimPlayerComponent spawns it beside the model when the clip
	/// plays, and the export bakes it into the shot file.
	/// </summary>
	[Property, Title( "Export With Clip" )] public bool Export { get; set; }

	[Property, Group( "Placement" )] public Vector3 Position { get; set; } = new( 96f, -96f, 72f );

	/// <summary>Which way it looks. Forward is where the camera points; the rest of the frustum
	/// hangs off that.</summary>
	[Property, Group( "Placement" ), Title( "Direction" )] public Angles Rotation { get; set; }

	/// <summary>Vertical field of view in degrees. The default matches the viewport's own, so a
	/// camera placed "at the camera" reads back exactly as it looked when you were there.</summary>
	[Property, Group( "Lens" ), Title( "Field Of View" ), Range( 1f, 179f, 1f, false )]
	public float FieldOfView { get; set; } = 80f;

	/// <summary>Near clipping plane, in inches. Small so a camera can sit close to the model.</summary>
	[Property, Group( "Lens" ), Title( "Near Plane" )] public float ZNear { get; set; } = 8f;

	/// <summary>Far clipping plane, in inches.</summary>
	[Property, Group( "Lens" ), Title( "Far Plane" )] public float ZFar { get; set; } = 4096f;

	/// <summary>
	/// Position and rotation as one transform, for the viewport and the runtime.
	///
	/// A METHOD, NOT A PROPERTY, on purpose: this document's format serializes every public
	/// property including computed ones, and a derived value stored in a file is a second copy of
	/// two other fields, waiting to disagree with them.
	/// </summary>
	public Transform World => new( Position, Rotation.ToRotation() );
}
