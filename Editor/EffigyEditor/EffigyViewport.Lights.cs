using Editor;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// Viewport lighting: a full-bright modelling light, the studio sun used to judge materials,
/// and point lights you can drop in to see how a part reads under a lamp.
///
/// LIGHTS ARE VIEWPORT SCENERY, not features. They never enter the studio, never export, and
/// vanish when the window closes. A lamp that survived into a compiled vmdl would be a lighting
/// setup leaking into a mesh, which is the wrong file.
/// </summary>
/// <summary>What kind of lamp a placed light is. Point is the original and still the default:
/// it needs no aiming, so it is the one you can drop and drag without thinking about facing.</summary>
internal enum EffigyLightKind
{
	Point,
	Spot,
	Sun,
}

/// <summary>
/// A named arrangement of lamps, placed in one click.
///
/// WHY PRESETS AND NOT JUST "ADD LIGHT". Placing one lamp tells you very little: a single source
/// leaves half the part black, which is exactly the problem full bright exists to avoid. The
/// arrangements that actually show a shape are all several lamps in a known relationship, and
/// building one by hand means placing three lights and dragging each into position past the
/// geometry you are trying to look at. These are the standard ones, sized to whatever is on
/// screen.
/// </summary>
internal enum EffigyLightRig
{
	/// <summary>Key, fill and rim — the default portrait setup. Shows form without flattening it.</summary>
	ThreePoint,

	/// <summary>One bright source behind and above, so the silhouette lights up and the front goes
	/// dark. The setup for judging an outline rather than a surface.</summary>
	Rim,

	/// <summary>A single lamp overhead. Reads like a workbench, and it is the light that makes
	/// horizontal detail — panel lines, engraving — most legible.</summary>
	TopDown,

	/// <summary>One key light and nothing else. The harshest of the four, and the one that shows
	/// exactly where your normals are wrong.</summary>
	KeyOnly,
}

internal sealed partial class EffigyViewport
{
	/// <summary>
	/// Even light from every side, so a face is never in shadow while you model.
	///
	/// The studio rig this replaced is still there — one sun, a dim ambient — and it is the right
	/// light for judging a material against a game scene. It is the wrong light for modelling:
	/// the back of a part goes black and a face you are about to sketch on disappears. Full bright
	/// is the default; studio is the setting you turn on when you want to see the lighting.
	/// </summary>
	public bool FullBright
	{
		get => _fullBright;
		set
		{
			if ( _fullBright == value )
				return;

			_fullBright = value;
			ApplyLighting();
			LightingChanged?.Invoke();
		}
	}

	/// <summary>How many point lights are currently in the viewport.</summary>
	public int PlacedLightCount => _lights.Count;

	/// <summary>True while a placed light is selected (showing its move gizmo).</summary>
	public bool LightSelected => _selectedLight >= 0 && _selectedLight < _lights.Count;

	/// <summary>Fires when the mode or the set of lights changes, so Settings can reprint.</summary>
	public Action LightingChanged { get; set; }

	private bool _fullBright = true;

	private DirectionalLight _sun;
	private AmbientLight _ambient;

	private readonly List<PlacedLight> _lights = new();
	private int _selectedLight = -1;
	private bool _draggingLight;
	private Vector3 _lightDragStart;
	private Vector3 _lightDragDelta;

	/// <summary>Warm white, a little over 1 so it actually reads on a PBR material. Colour is
	/// the part you judge; intensity is just enough to see it.</summary>
	private static readonly Color PlacedLightColor = new Color( 1f, 0.96f, 0.88f ) * 4f;

	/// <summary>Colours for the three roles a rig lamp plays. The key is the warm one you judge
	/// the material under, the fill is cool and weak so the shadow side stays readable rather than
	/// going black, and the rim is bright and neutral because its whole job is an edge.</summary>
	private static readonly Color KeyColor = new Color( 1f, 0.95f, 0.86f ) * 5f;
	private static readonly Color FillColor = new Color( 0.82f, 0.88f, 1f ) * 1.6f;
	private static readonly Color RimColor = new Color( 1f, 0.98f, 0.94f ) * 7f;

	/// <summary>Typed as the abstract <see cref="Light"/> rather than PointLight, because a spot
	/// and a sun are not point lights and everything done to a lamp here — enable it, colour it —
	/// is on the base. Reach is kept alongside rather than read back off the component: only two
	/// of the three kinds HAVE a Radius, and the gizmo needs a number for all of them.</summary>
	private sealed class PlacedLight
	{
		public GameObject Object;
		public Light Light;
		public EffigyLightKind Kind;
		public float Reach;
	}

	/// <summary>Drop a point light. Kept by name because the View menu asks for exactly this, and a
	/// point light is still the sane default — it is the one kind with nothing to aim.</summary>
	public void AddPointLight() => AddLight( EffigyLightKind.Point );

	/// <summary>
	/// Drop a lamp in front of whatever is on screen, select it, and switch out of full bright so
	/// it actually lights the part. Adding a light you cannot see is how this would look broken.
	///
	/// Spots and suns are AIMED AT THE PART on the way in rather than left pointing along +x. A
	/// directional light's position means nothing to the renderer — only its rotation does — but
	/// it is still placed off to the side, so its gizmo has somewhere to be that is not inside
	/// the mesh.
	/// </summary>
	public void AddLight( EffigyLightKind kind )
	{
		using var scope = _canvas.Scene.Push();

		Spawn( kind, DefaultLightPosition(), CurrentFocus(), PlacedLightColor );

		_selectedLight = _lights.Count - 1;
		_draggingLight = false;

		DeselectOrigin();
		DeselectBone();

		// Full bright would swallow the lamp. Flip it off here rather than asking the caller to
		// remember — every route that adds a light lands in this method.
		if ( _fullBright )
			_fullBright = false;

		ApplyLighting();
		LightingChanged?.Invoke();
	}

	/// <summary>
	/// Replace whatever is placed with a named arrangement, sized to the part on screen.
	///
	/// REPLACE RATHER THAN ADD. A rig is a complete answer to "how is this lit"; dropping a second
	/// one on top of the first gives you six lamps in two conflicting setups and no way to tell
	/// which belongs to which. Clearing first makes picking a different rig one click that always
	/// lands somewhere predictable.
	/// </summary>
	public void ApplyRig( EffigyLightRig rig )
	{
		using var scope = _canvas.Scene.Push();

		foreach ( var placed in _lights )
			placed.Object?.Destroy();

		_lights.Clear();

		var center = CurrentFocus();
		var r = LightSceneRadius();
		var cam = _camera.WorldRotation;

		// POSITIONS ARE IN CAMERA SPACE, not world space. A rig anchored to world axes is a
		// different rig depending on which way you happen to be orbiting — the key light ends up
		// behind the part about half the time. Relative to the camera, "key up and to the right"
		// means the same thing from every angle, and that is what makes these worth one click.
		var right = cam.Right;
		var up = cam.Up;
		var forward = cam.Forward;

		switch ( rig )
		{
			case EffigyLightRig.ThreePoint:
				Spawn( EffigyLightKind.Spot, center + right * r * 1.6f + up * r * 1.2f - forward * r * 1.4f,
					center, KeyColor );
				Spawn( EffigyLightKind.Point, center - right * r * 1.8f + up * r * 0.3f - forward * r * 1.1f,
					center, FillColor );
				Spawn( EffigyLightKind.Spot, center - right * r * 0.7f + up * r * 1.5f + forward * r * 1.9f,
					center, RimColor );
				break;

			case EffigyLightRig.Rim:
				Spawn( EffigyLightKind.Spot, center + right * r * 1.1f + up * r * 1.3f + forward * r * 2f,
					center, RimColor );
				Spawn( EffigyLightKind.Point, center - right * r * 1.5f + up * r * 0.4f - forward * r * 1.3f,
					center, FillColor * 0.5f );
				break;

			case EffigyLightRig.TopDown:
				// Straight down in WORLD terms, deliberately breaking the camera-space rule above:
				// "overhead" means overhead, and a top light that tilts as you orbit is not one.
				Spawn( EffigyLightKind.Spot, center + Vector3.Up * r * 2.6f, center, KeyColor );
				break;

			case EffigyLightRig.KeyOnly:
				Spawn( EffigyLightKind.Spot, center + right * r * 1.6f + up * r * 1.2f - forward * r * 1.4f,
					center, KeyColor );
				break;
		}

		_selectedLight = -1;
		_draggingLight = false;

		if ( _fullBright )
			_fullBright = false;

		ApplyLighting();
		LightingChanged?.Invoke();
	}

	/// <summary>Build one lamp and add it to the list. Deliberately does not touch selection, full
	/// bright or the event: a rig fires those once for the whole set rather than once per lamp.</summary>
	private void Spawn( EffigyLightKind kind, Vector3 position, Vector3 aimAt, Color color )
	{
		var go = new GameObject( true, "effigy_light" );
		var reach = DefaultLightRadius();

		go.WorldPosition = position;

		// Aiming is harmless on a point light and essential on the other two, so it is done for
		// all three rather than guarded per kind. A zero-length direction would give a NaN
		// rotation, so a lamp sitting exactly on the focus keeps identity instead.
		var toTarget = aimAt - position;

		if ( !toTarget.IsNearlyZero() )
			go.WorldRotation = Rotation.LookAt( toTarget.Normal );

		Light light = kind switch
		{
			EffigyLightKind.Spot => BuildSpot( go, reach ),
			EffigyLightKind.Sun => go.GetOrAddComponent<DirectionalLight>( false ),
			_ => BuildPoint( go, reach ),
		};

		light.LightColor = color;
		light.Shadows = true;
		light.Enabled = true;

		_lights.Add( new PlacedLight { Object = go, Light = light, Kind = kind, Reach = reach } );
	}

	private static Light BuildPoint( GameObject go, float reach )
	{
		var light = go.GetOrAddComponent<PointLight>( false );
		light.Radius = reach;
		return light;
	}

	private static Light BuildSpot( GameObject go, float reach )
	{
		var light = go.GetOrAddComponent<SpotLight>( false );
		light.Radius = reach;

		// Wide enough to cover a part framed in the viewport from the distance the rigs place it
		// at. A tight cone reads as a spotlight EFFECT rather than as lighting, which is not what
		// these are for.
		light.ConeInner = 25f;
		light.ConeOuter = 45f;
		return light;
	}

	/// <summary>Remove the selected light, or do nothing if none is selected.</summary>
	public void RemoveSelectedLight()
	{
		if ( !LightSelected )
			return;

		using var scope = _canvas.Scene.Push();

		_lights[_selectedLight].Object?.Destroy();
		_lights.RemoveAt( _selectedLight );
		_selectedLight = -1;
		_draggingLight = false;

		LightingChanged?.Invoke();
	}

	/// <summary>Remove every placed light.</summary>
	public void ClearLights()
	{
		if ( _lights.Count == 0 )
			return;

		using var scope = _canvas.Scene.Push();

		foreach ( var placed in _lights )
			placed.Object?.Destroy();

		_lights.Clear();
		_selectedLight = -1;
		_draggingLight = false;

		LightingChanged?.Invoke();
	}

	public void DeselectLight()
	{
		if ( _selectedLight < 0 )
			return;

		_selectedLight = -1;
		_draggingLight = false;
	}

	/// <summary>
	/// Apply whichever lighting the setting asks for.
	///
	/// Full bright: sun off, ambient white, placed lights off. Studio: the runtime sun and
	/// ambient from <see cref="BuildRuntimeLighting"/>, placed lights on. The cubemap and the
	/// tonemapper stay in both — they are why a material here matches a material in game, and
	/// full bright is about the key light, not about throwing the rest of the rig away.
	/// </summary>
	private void ApplyLighting()
	{
		if ( _sun.IsValid() )
			_sun.Enabled = !_fullBright;

		if ( _ambient.IsValid() )
		{
			_ambient.Color = _fullBright
				? Color.White
				: new Color( 0.237f, 0.237f, 0.237f, 1f );
			_ambient.Enabled = true;
		}

		foreach ( var placed in _lights )
		{
			if ( placed.Light.IsValid() )
				placed.Light.Enabled = !_fullBright;
		}
	}

	private Vector3 DefaultLightPosition()
	{
		var center = CurrentFocus();
		var radius = LightSceneRadius();
		var cam = _camera.WorldRotation;

		// Off to the camera's right and up, slightly in front of the part, so the bulb is not
		// born inside the mesh and is the first thing you see after adding it.
		return center + cam.Right * radius + cam.Up * (radius * 0.55f) + cam.Forward * (-radius * 0.15f);
	}

	private float DefaultLightRadius()
	{
		// Reach past the part with room to spare. A radius that ends on the surface lights one
		// face and looks like the lamp did nothing.
		return Math.Clamp( LightSceneRadius() * 8f, 64f, 2048f );
	}

	private float LightSceneRadius()
	{
		if ( _renderer.IsValid() && _renderer.Model is { } model )
			return MathF.Max( model.Bounds.Size.Length * 0.5f, 8f );

		return 32f;
	}

	/// <summary>
	/// Draw each placed light as a bulb you can click, and a move gizmo on the selected one.
	/// Hidden while sketching or picking — same reason the origin is, it would steal the click.
	/// </summary>
	private void DrawViewportLights()
	{
		for ( var i = 0; i < _lights.Count; i++ )
		{
			var placed = _lights[i];

			if ( !placed.Object.IsValid() || !placed.Light.IsValid() )
				continue;

			DrawOneLight( i, placed );
		}
	}

	private void DrawOneLight( int index, PlacedLight placed )
	{
		var position = placed.Object.WorldPosition;
		var radius = WorldRadiusAt( position, 4.5f );
		var selected = index == _selectedLight;
		var live = !_fullBright;

		using var scope = Gizmo.Scope( $"light-{index}", new Transform( position ) );

		if ( selected )
		{
			using ( Gizmo.Scope( "light-move", new Transform( Vector3.Zero ) ) )
			{
				Gizmo.Hitbox.DepthBias = 0.01f;

				if ( Gizmo.Control.Position( $"light-{index}-pos", Vector3.Zero, out var displacement, Rotation.Identity ) )
				{
					if ( !_draggingLight )
					{
						_draggingLight = true;
						_lightDragStart = position;
						_lightDragDelta = Vector3.Zero;
					}

					_lightDragDelta += displacement;
					placed.Object.WorldPosition = _lightDragStart + _lightDragDelta;
				}
				else if ( _draggingLight && _selectedLight == index )
				{
					_draggingLight = false;
				}
			}

			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = new Color( 1f, 0.72f, 0.22f, 1f );
			Gizmo.Draw.SolidSphere( 0f, radius * 1.35f, 12, 12 );
			Gizmo.Draw.Color = new Color( 1f, 0.72f, 0.22f, 0.35f );

			// A sun has no radius to draw — its reach is the whole scene — so the falloff sphere
			// would be a lie. It gets the aim line below instead, which is the only thing about a
			// directional light you can actually move.
			if ( placed.Kind != EffigyLightKind.Sun )
				Gizmo.Draw.LineSphere( new Sphere( Vector3.Zero, placed.Reach ), 4 );

			DrawLightAim( placed );

			Gizmo.Draw.IgnoreDepth = false;

			return;
		}

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = new Color( 1f, 0.72f, 0.22f, live ? 0.9f : 0.35f );
		Gizmo.Draw.SolidSphere( 0f, radius, 10, 10 );
		Gizmo.Draw.IgnoreDepth = false;

		Gizmo.Hitbox.DepthBias = 0.01f;
		Gizmo.Hitbox.Sphere( new Sphere( Vector3.Zero, radius * 2.6f ) );

		if ( !Gizmo.IsHovered )
			return;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = new Color( 1f, 0.72f, 0.22f, 0.35f );
		Gizmo.Draw.SolidSphere( 0f, radius * 2.6f, 10, 10 );
		Gizmo.Draw.IgnoreDepth = false;

		if ( Gizmo.WasLeftMousePressed )
		{
			_selectedLight = index;
			_draggingLight = false;
			DeselectOrigin();
			DeselectBone();
		}
	}

	/// <summary>
	/// A short line out of the bulb along the way it faces, for the two kinds where facing is the
	/// whole setting.
	///
	/// A point light is left alone: it throws light every way, so a line out of one side of it
	/// would claim a direction it does not have. Drawn inside the caller's gizmo scope, so the
	/// coordinates are local to the lamp and the direction is simply the object's own forward
	/// brought back out of world space.
	/// </summary>
	private void DrawLightAim( PlacedLight placed )
	{
		if ( placed.Kind == EffigyLightKind.Point || !placed.Object.IsValid() )
			return;

		var length = WorldRadiusAt( placed.Object.WorldPosition, 4.5f ) * 6f;
		var direction = placed.Object.WorldRotation.Forward;

		Gizmo.Draw.Color = new Color( 1f, 0.72f, 0.22f, 0.55f );
		Gizmo.Draw.Line( Vector3.Zero, direction * length );
	}

}
