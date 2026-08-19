using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Picking a body in the viewport.
///
/// Eight of Effigy's features take a BodySelectionParam — Shell, Bevel, UV Project, Transform,
/// Mirror, and the three patterns — and every one of them rendered as a disabled label reading
/// "All bodies" that you could not click. The parameter existed, the kernel honoured it, and there
/// was no way to put anything in it.
///
/// So bodies pick the same way planes and faces do: the tool asks, the candidates light up, you
/// click one. Selection is by body ID, which is what BodySelectionParam stores and what stays
/// stable across a rebuild (PartStudio seeds its id counter for exactly this reason).
///
/// HIT-TESTING IS BY BOUNDING BOX, not by mesh. Gizmo.Hitbox.BBox is proven in this file's sibling
/// for the reference planes; per-triangle picking is not, and a body's box is an unambiguous
/// target for choosing which of two or three solids you meant. It is the wrong tool for picking a
/// FACE of a body, which is a different job this does not claim to do.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>While true the bodies are pickable and highlight on hover. Set by the feature
	/// dialog when its body selection box is armed.</summary>
	public bool BodyPickMode { get; set; }

	/// <summary>Fires with the clicked body's id.</summary>
	public Action<string> BodyPicked { get; set; }

	private readonly List<(string Id, string Name, BBox Bounds)> _visibleBodies = new();

	/// <summary>Body ids the open feature has already chosen, drawn as taken.</summary>
	private readonly List<string> _selectedBodies = new();

	private string _hoveredBody;

	private static readonly Color BodyPickableColor = new( 0.35f, 0.70f, 1f, 0.30f );
	private static readonly Color BodyHoverColor = new( 0.45f, 0.85f, 1f, 0.95f );
	private static readonly Color BodySelectedColor = new( 0.30f, 0.85f, 0.55f, 0.85f );

	/// <summary>Replace the set of bodies that can be picked. Pushed by the window after each
	/// rebuild, from PartStudio.Bodies.</summary>
	public void SetVisibleBodies( IEnumerable<Body> bodies )
	{
		_visibleBodies.Clear();

		if ( bodies is null )
			return;

		foreach ( var body in bodies )
		{
			if ( body?.Mesh is null || body.Mesh.VertexCount == 0 )
				continue;

			_visibleBodies.Add( (body.Id, body.Name, BoundsOf( body.Mesh )) );
		}
	}

	/// <summary>Which bodies the open feature has selected, so they read as chosen.</summary>
	public void SetSelectedBodies( IEnumerable<string> bodyIds )
	{
		_selectedBodies.Clear();

		if ( bodyIds is not null )
			_selectedBodies.AddRange( bodyIds );
	}

	/// <summary>
	/// Outline every pickable body, resolve the hover, and take the click.
	///
	/// Only runs while something is asking. Outside pick mode the bodies are just the model and
	/// boxing them up would be noise drawn over the geometry you are trying to look at.
	/// </summary>
	private void BodyPickFrame()
	{
		_hoveredBody = null;

		if ( !BodyPickMode || _visibleBodies.Count == 0 )
			return;

		// Resolve hover across all bodies first: the smallest box under the cursor wins, so a small
		// body sitting inside or in front of a larger one stays reachable.
		var bestVolume = float.MaxValue;

		foreach ( var (id, _, bounds) in _visibleBodies )
		{
			using var scope = Gizmo.Scope( $"body-pick-{id}", new Transform( bounds.Center ) );

			Gizmo.Hitbox.DepthBias = 0.01f;
			Gizmo.Hitbox.BBox( BBox.FromPositionAndSize( Vector3.Zero, bounds.Size ) );

			if ( !Gizmo.IsHovered )
				continue;

			var volume = bounds.Size.x * bounds.Size.y * bounds.Size.z;

			if ( volume >= bestVolume )
				continue;

			bestVolume = volume;
			_hoveredBody = id;
		}

		Gizmo.Draw.IgnoreDepth = true;

		foreach ( var (id, _, bounds) in _visibleBodies )
		{
			var selected = _selectedBodies.Contains( id );

			Gizmo.Draw.Color = id == _hoveredBody
				? BodyHoverColor
				: selected ? BodySelectedColor : BodyPickableColor;

			Gizmo.Draw.LineThickness = id == _hoveredBody || selected ? 2f : 1f;
			DrawBoxOutline( bounds );
		}

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;

		if ( _hoveredBody is not null && Gizmo.WasLeftMousePressed )
			BodyPicked?.Invoke( _hoveredBody );
	}

	/// <summary>The twelve edges of a box. Gizmo.Draw has no wire-box primitive, and a solid one
	/// would hide the body it is meant to be pointing at.</summary>
	private static void DrawBoxOutline( BBox box )
	{
		// Derived from Center and Size rather than read off Mins/Maxs: those two are the BBox
		// members already used elsewhere in this repo against a real engine build, and this is not
		// the file to find out that the other pair is spelled differently.
		var half = box.Size * 0.5f;
		var min = box.Center - half;
		var max = box.Center + half;

		var corners = new[]
		{
			new Vector3( min.x, min.y, min.z ),
			new Vector3( max.x, min.y, min.z ),
			new Vector3( max.x, max.y, min.z ),
			new Vector3( min.x, max.y, min.z ),
			new Vector3( min.x, min.y, max.z ),
			new Vector3( max.x, min.y, max.z ),
			new Vector3( max.x, max.y, max.z ),
			new Vector3( min.x, max.y, max.z ),
		};

		// Bottom ring, top ring, then the four uprights joining them.
		for ( var i = 0; i < 4; i++ )
		{
			Gizmo.Draw.Line( corners[i], corners[(i + 1) % 4] );
			Gizmo.Draw.Line( corners[i + 4], corners[(i + 1) % 4 + 4] );
			Gizmo.Draw.Line( corners[i], corners[i + 4] );
		}
	}

	private static BBox BoundsOf( PolyMesh mesh )
	{
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			var v = new Vector3( p.x, p.y, p.z );
			min = Vector3.Min( min, v );
			max = Vector3.Max( max, v );
		}

		return new BBox( min, max );
	}
}
