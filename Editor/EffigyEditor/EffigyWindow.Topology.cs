using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

// ============================================================================
//  Mesh ▸ Topology report — the `topology` build-script verb, from the menu.
//
//  The numbers a modeller checks before rigging (what the faces are, where the
//  poles sit, how even the edges and quads are, open or non-manifold edges,
//  left-right symmetry, loops round each joint), each against its target. The
//  full report goes to the console; the status bar says how many lines missed.
// ============================================================================

public sealed partial class EffigyWindow
{
	private EffigyStageTool _objTopologyTool;

	/// <summary>The Mesh menu's report tile, built once beside the other object tools.</summary>
	private EffigyStageTool TopologyTool() => _objTopologyTool ??= new EffigyStageTool
	{
		Icon = EffigyIcon.SelectFace,
		Label = "Topology report",
		Tip = "Check how the selected body is built: quads and triangles, poles, edge evenness, open edges, symmetry and loops at each joint, each against its target. The report goes to the console",
		Clicked = ReportTopology,
	};

	/// <summary>
	/// Report on the selected bodies, or every visible one when nothing is selected. A line that
	/// misses its target starts with "!" in the console, and the status bar counts them.
	/// </summary>
	private void ReportTopology()
	{
		if ( _studio is null )
			return;

		var ids = _viewport?.IdleBodyIds ?? (IReadOnlyList<string>)Array.Empty<string>();
		var bodies = _studio.Bodies
			.Where( b => b.Mesh is { FaceCount: > 0 } && (ids.Count > 0 ? ids.Contains( b.Id ) : b.Visible) )
			.ToList();

		if ( bodies.Count == 0 )
		{
			Refuse( _studio.Bodies.Count == 0 ? "Add a body first: there is nothing to report on." : "The selected body has no faces to report on." );
			return;
		}

		var rig = _studio.Rig is { Count: > 0 } ? _studio.Rig : null;
		var missed = 0;

		foreach ( var body in bodies )
		{
			var text = TopologyReport.Measure( body.Mesh, body.Name ?? body.Id, rig ).Describe();
			missed += text.Split( '\n' ).Count( l => l.StartsWith( "!" ) );
			Log.Info( $"[Effigy] {text}" );
		}

		var what = bodies.Count == 1 ? bodies[0].Name ?? bodies[0].Id : $"{bodies.Count} bodies";
		SetPrompt( missed == 0
			? $"Topology of {what}: every check meets its target. The report is in the console."
			: $"Topology of {what}: {missed} check{(missed == 1 ? "" : "s")} miss their target (marked ! in the console)." );
	}
}
