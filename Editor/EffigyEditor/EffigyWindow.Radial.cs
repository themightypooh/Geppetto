using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// What the radial menu offers where you right-click (the menu itself: EffigyRadialMenu and
/// EffigyViewport.Radial.cs). Each pie is the handful of things most often done to what was under
/// the cursor, in fixed slots so the hand learns them, and ends with "More…" — the full list menu
/// that used to open here, so nothing that was reachable by right-click stopped being reachable.
/// </summary>
public sealed partial class EffigyWindow
{
	private void WireRadial()
	{
		_viewport.ContextRadialRequested = OpenContextRadial;
		_viewport.SketchRadialRequested = OpenSketchRadial;
	}

	/// <summary>A right-click on a face, or on nothing.</summary>
	private void OpenContextRadial( EffigyFaceHit? hit )
	{
		if ( _viewport is null || _studio is null )
			return;

		if ( hit is { } face && face.Body is not null )
		{
			// The Rig workspace's right-click is the part's bone menu, which is a short list already.
			if ( CurrentWorkspace == EffigyWorkspace.Rig )
			{
				OpenFaceMenu( face );
				return;
			}

			if ( InObjectMode )
			{
				OpenObjectRadial( face );
				return;
			}

			OpenFaceRadial( face );
			return;
		}

		OpenEmptyRadial();
	}

	/// <summary>A face of a part in the CAD workspace: the tools that take a face, both sides of
	/// the wall, and the material and full menu behind More.</summary>
	private void OpenFaceRadial( EffigyFaceHit hit )
	{
		var slots = new List<(EffigyRadialMenu.Item, Action)>();

		// Room for six tools: two slots are held for Both sides and More.
		foreach ( var tool in ToolsAccepting( GeometryKind.Face ).Take( 6 ) )
		{
			var kind = tool.Kind;
			var entry = _featureTools.TryGetValue( kind, out var e ) ? e : null;

			slots.Add( (new EffigyRadialMenu.Item( tool.Label, tool.MenuIcon ?? "build",
				entry?.Enabled ?? true, entry?.DisabledReason, FaceHint( kind ) ), () => UseFaceWith( hit, kind )) );
		}

		var face = hit;
		slots.Add( (new EffigyRadialMenu.Item( "Both sides", "flip", Hint: "this face + the one across" ), () => SelectBothSides( face )) );
		slots.Add( (new EffigyRadialMenu.Item( "Material & more…", "more_horiz", Hint: "the full face menu" ), () => OpenFaceMenu( face )) );

		_viewport.OpenRadial( $"Face of {hit.Body.Name ?? "part"}", slots );
	}

	/// <summary>What a face tool does to the face, in the few words the middle of the wheel holds.
	/// Kept here rather than cut down from the tool's own tooltip, which is a sentence.</summary>
	private static string FaceHint( ToolKind kind ) => kind switch
	{
		ToolKind.Sketch => "draw on this face",
		ToolKind.Extrude => "pull this face out",
		ToolKind.MoveFace => "slide this face",
		ToolKind.Hole => "drill into this face",
		ToolKind.Draft => "tilt this face",
		ToolKind.DeleteFace => "remove, heal the gap",
		ToolKind.FaceMaterial => "paint this face",
		ToolKind.Shell => "hollow, open here",
		ToolKind.Plane => "a plane on this face",
		_ => null,
	};

	/// <summary>A body in Model's Object mode: the transform keys, duplicate, edit, hide, delete.</summary>
	private void OpenObjectRadial( EffigyFaceHit hit )
	{
		var body = hit.Body;

		BuildObjectModeTools();

		// The same selection rule the list menu has: right-clicking a body selects it, Ctrl keeps
		// the selection so a second body can be added to it.
		if ( !_viewport.IdleBodyIds.Contains( body.Id ) )
		{
			var ids = Editor.Application.IsKeyDown( KeyCode.Control )
				? _viewport.IdleBodyIds.Append( body.Id ).ToList()
				: new List<string> { body.Id };

			_viewport.SelectBodies( ids );
		}

		UpdateObjectChecks();

		(EffigyRadialMenu.Item, Action) Slot( EffigyStageTool tool, string icon, string hint )
		{
			var run = tool?.Clicked;
			return (new EffigyRadialMenu.Item( tool?.Label ?? "?", icon, tool?.Enabled ?? false, tool?.DisabledReason, hint ), () => run?.Invoke());
		}

		_viewport.OpenRadial( body.Name ?? "Body", new[]
		{
			Slot( _objMoveTool, "open_with", "G · drag the handle" ),
			Slot( _objRotateTool, "rotate_right", "R · turn it" ),
			Slot( _objScaleTool, "open_in_full", "S · size it" ),
			Slot( _objDuplicateTool, "content_copy", "a copy beside it" ),
			Slot( _objDeleteTool, "delete", "Ctrl+Z brings it back" ),
			(new EffigyRadialMenu.Item( "More…", "more_horiz", Hint: "the full object menu" ), () => OpenObjectMenu( body )),
			Slot( _objHideTool, "visibility_off", "still exports" ),
			Slot( _objEditTool, "edit", "Tab · verts, edges, faces" ),
		} );
	}

	/// <summary>
	/// Empty space. In the CAD workspace the ways to start a part; everywhere the views and undo.
	/// This is the click that used to answer "No menu here" — there is always something to do.
	/// </summary>
	private void OpenEmptyRadial()
	{
		var slots = new List<(EffigyRadialMenu.Item, Action)>();

		if ( CurrentWorkspace == EffigyWorkspace.Cad && !_viewport.IsSketching )
		{
			slots.Add( (CreateSlot( ToolKind.Sketch, "Sketch", "edit", "draw on a plane" ), () => AddFeature( NewFeature( ToolKind.Sketch, -1 ) )) );
			slots.Add( (CreateSlot( ToolKind.Primitive, "Box", "check_box_outline_blank", "start from a block" ), () => AddFeature( NewFeature( ToolKind.Primitive, -1 ) )) );
			slots.Add( (CreateSlot( ToolKind.Import, "Import", "file_open", ImportFormats ), () => ImportObj()) );
		}
		else
		{
			slots.Add( (new EffigyRadialMenu.Item( "Top", "crop_square", Hint: "look straight down" ), () => _viewport.SetStandardView( EffigyViewport.StandardView.Top )) );
			slots.Add( (new EffigyRadialMenu.Item( "Right", "crop_portrait", Hint: "look from the right" ), () => _viewport.SetStandardView( EffigyViewport.StandardView.Right )) );
			slots.Add( (new EffigyRadialMenu.Item( "Front", "crop_portrait", Hint: "look from the front" ), () => _viewport.SetStandardView( EffigyViewport.StandardView.Front )) );
		}

		slots.Add( (new EffigyRadialMenu.Item( "Fit view", "fit_screen", Hint: "frame the whole part" ), () => _viewport.FrameCamera()) );
		slots.Add( (new EffigyRadialMenu.Item( "Redo", "redo", Hint: "Ctrl+Y" ), Redo) );
		slots.Add( (new EffigyRadialMenu.Item( "Undo", "undo", Hint: "Ctrl+Z" ), Undo) );
		slots.Add( (new EffigyRadialMenu.Item( "Home view", "home", Hint: "the starting angle" ), () => _viewport.SetStandardView( EffigyViewport.StandardView.Isometric )) );

		_viewport.OpenRadial( null, slots );
	}

	/// <summary>A creation tool as a slot, greyed with its own reason when the strip greys it.</summary>
	private EffigyRadialMenu.Item CreateSlot( ToolKind kind, string label, string icon, string hint )
	{
		var entry = _featureTools.TryGetValue( kind, out var e ) ? e : null;
		return new EffigyRadialMenu.Item( label, icon, entry?.Enabled ?? true, entry?.DisabledReason, hint );
	}

	/// <summary>
	/// Inside a sketch, with nothing half-drawn: the drawing tools, Constrain for the selection,
	/// and Finish. Right-click used to stand the tool down here; Select is on the pie for that.
	/// </summary>
	private void OpenSketchRadial()
	{
		var hasSelection = _viewport.HasSketchSelection;

		(EffigyRadialMenu.Item, Action) Tool( SketchToolKind kind, string label, string icon, string hint ) =>
			(new EffigyRadialMenu.Item( label, icon, Hint: hint ), () => ArmSketchTool( kind ));

		_viewport.OpenRadial( ActiveSketchFeature()?.Name ?? "Sketch", new[]
		{
			Tool( SketchToolKind.Line, "Line", "horizontal_rule", "click, click, chain" ),
			Tool( SketchToolKind.Rectangle, "Rectangle", "crop_square", "two corners" ),
			Tool( SketchToolKind.Circle, "Circle", "radio_button_unchecked", "centre, then size" ),
			Tool( SketchToolKind.Arc, "Arc", "architecture", "centre, start, end" ),
			(new EffigyRadialMenu.Item( "Finish sketch", "check", Hint: "back to the part" ), FinishSketch),
			(new EffigyRadialMenu.Item( "Constrain…", "link", hasSelection,
				"Pick lines or points with Select first", "rules for the selection" ), OpenSketchConstraintMenu),
			Tool( SketchToolKind.Trim, "Trim", "content_cut", "click a piece to cut it" ),
			Tool( SketchToolKind.Select, "Select", "near_me", "pick, drag, box-select" ),
		} );
	}
}
