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

	private sealed class PlacedLight
	{
		public GameObject Object;
		public PointLight Light;
	}

	/// <summary>
	/// Drop a point light in front of whatever is on screen, select it, and switch out of full
	/// bright so the lamp actually lights the part. Adding a light you cannot see is how this
	/// would look broken.
	/// </summary>
	public void AddPointLight()
	{
		using var scope = _canvas.Scene.Push();

		var go = new GameObject( true, "effigy_light" );
		var light = go.GetOrAddComponent<PointLight>( false );

		go.WorldPosition = DefaultLightPosition();
		light.Radius = DefaultLightRadius();
		light.LightColor = PlacedLightColor;
		light.Shadows = true;
		light.Enabled = true;

		_lights.Add( new PlacedLight { Object = go, Light = light } );
		_selectedLight = _lights.Count - 1;
		_draggingLight = false;

		DeselectOrigin();
		DeselectBone();

		// Full bright would swallow the lamp. Flip it off here rather than asking the caller to
		// remember — the Settings switch and the View menu both land in this method.
		if ( _fullBright )
			_fullBright = false;

		ApplyLighting();
		LightingChanged?.Invoke();
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
			Gizmo.Draw.LineSphere( new Sphere( Vector3.Zero, placed.Light.Radius ), 4 );
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

}
