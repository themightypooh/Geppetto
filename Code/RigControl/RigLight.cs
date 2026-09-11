using Sandbox;

namespace Marionette;

/// <summary>What a light is. Named rather than a bool pair, because the fields each kind uses
/// are different and the sheet hides the ones that do not apply.</summary>
public enum RigLightKind
{
	/// <summary>Parallel rays from a direction, like the sun. Position is ignored.</summary>
	Directional,

	/// <summary>A bulb: light in every direction from a point, fading out at Range.</summary>
	Point,

	/// <summary>A cone aimed from a point, which is what a practical lamp or a studio key is.</summary>
	Spot,

	/// <summary>Flat fill from every direction at once. Not a light you can aim - it is the
	/// floor under the others, so nothing in shadow is pure black.</summary>
	Ambient
}

/// <summary>
/// One light in the Marionette viewport. See <see cref="RigAnimDocument.Lights"/>.
///
/// LIGHTING TO WORK BY UNLESS YOU SAY OTHERWISE. A light starts as workspace - this window and
/// nowhere else - and only leaves it if <see cref="Export"/> is ticked, at which point
/// RigAnimPlayerComponent spawns it with the clip. Neither route puts it in a compiled .vmdl,
/// which has nowhere for one. See RigAnimDocument.Lights.
///
/// The fields are deliberately the ones the engine's own light components take, under the same
/// names, so what you set here is what the scene gets and there is no translation layer to be
/// wrong about.
/// </summary>
public sealed class RigLight
{
	[Property] public string Name { get; set; } = "light";

	[Property] public RigLightKind Kind { get; set; } = RigLightKind.Directional;

	/// <summary>Off leaves the light in the list but stops it lighting anything - which is how
	/// you find out what one light is contributing, and the reason this is a toggle rather than
	/// asking you to delete it and set it up again.</summary>
	[Property] public bool Enabled { get; set; } = true;

	/// <summary>
	/// Whether this light is part of the shot or only part of the workspace.
	///
	/// OFF BY DEFAULT: a light you added to see an elbow by has no business turning up in
	/// somebody's game. On, and <see cref="RigAnimPlayerComponent"/> spawns it beside the model
	/// when the clip plays — a desk lamp authored with the pose it lights, travelling with it.
	///
	/// It cannot go into an exported .vmdl whatever this says. A .vmdl animation is bone
	/// channels; there is nowhere in the format for a light to live. Export tells you so rather
	/// than dropping it quietly, and the player component is the route that does carry it.
	/// </summary>
	[Property, Title( "Export With Clip" )] public bool Export { get; set; }

	[Property, Group( "Colour" )] public Color Color { get; set; } = Color.White;

	/// <summary>Multiplies the colour. Separate from it so a light can be made stronger without
	/// going white, and so a colour picked once survives being dimmed.</summary>
	[Property, Group( "Colour" ), Range( 0f, 8f, 0.05f, false )]
	public float Brightness { get; set; } = 1f;

	/// <summary>Where the light is. Ignored by Directional, which has no position, and by
	/// Ambient, which has neither.</summary>
	[Property, Group( "Placement" )]
	[ShowIf( nameof( Kind ), RigLightKind.Point ), ShowIf( nameof( Kind ), RigLightKind.Spot )]
	public Vector3 Position { get; set; } = new( -64f, -64f, 96f );

	/// <summary>Which way it points. Ignored by Point and Ambient, which shine every way at
	/// once. The default is the old fixed viewport sun, so a clip that copies the defaults in
	/// starts from exactly what it was already showing.</summary>
	[Property, Group( "Placement" ), Title( "Direction" )]
	[ShowIf( nameof( Kind ), RigLightKind.Directional ), ShowIf( nameof( Kind ), RigLightKind.Spot )]
	public Angles Rotation { get; set; } = new( 45f, 45f, 0f );

	/// <summary>How far the light reaches before it has faded to nothing.</summary>
	[Property, Group( "Falloff" )]
	[ShowIf( nameof( Kind ), RigLightKind.Point ), ShowIf( nameof( Kind ), RigLightKind.Spot )]
	public float Range { get; set; } = 512f;

	/// <summary>The full-brightness core of a spot's cone, in degrees from its axis.</summary>
	[Property, Group( "Falloff" ), Title( "Cone Inner" ), Range( 0f, 90f, 1f, false )]
	[ShowIf( nameof( Kind ), RigLightKind.Spot )]
	public float ConeInner { get; set; } = 25f;

	/// <summary>Where the cone has faded out entirely. Below Cone Inner it is clamped up to it -
	/// an outer smaller than the inner is a light with a hard edge and no core, which reads as
	/// the spot being broken.</summary>
	[Property, Group( "Falloff" ), Title( "Cone Outer" ), Range( 0f, 90f, 1f, false )]
	[ShowIf( nameof( Kind ), RigLightKind.Spot )]
	public float ConeOuter { get; set; } = 45f;

	/// <summary>
	/// Whether this light casts shadows.
	///
	/// On by default because shadow is most of what makes a pose readable - an arm in front of a
	/// chest is a silhouette without one. Worth turning off on a fill light, which is there to
	/// lift the shadows the key made rather than to add a second set of its own.
	/// </summary>
	[Property]
	[ShowIf( nameof( Kind ), RigLightKind.Directional ), ShowIf( nameof( Kind ), RigLightKind.Point ), ShowIf( nameof( Kind ), RigLightKind.Spot )]
	public bool Shadows { get; set; } = true;

	/// <summary>
	/// Colour and brightness as the one value a light component wants.
	///
	/// A METHOD, NOT A PROPERTY, on purpose: this document's format serializes every public
	/// property including computed ones - ReferenceProp's LocalTransform and AllModels are both
	/// written into every clip on disk - and a derived value stored in a file is a second copy
	/// of two other fields, waiting to disagree with them.
	/// </summary>
	public Color Tint() => Color * Brightness;
}
