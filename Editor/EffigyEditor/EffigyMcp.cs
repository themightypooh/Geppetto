using Editor;
using Editor.Mcp;
using Effigy;
using Effigy.Render;
using Sandbox;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Effigy for an agent connected to the editor: see the open studio, measure it, and build in
/// it with the same script the headless CLI runs. The window is the one the person has open,
/// so every change lands in their history as an undo step and shows on their screen.
///
/// The picture comes from the kernel's own renderer (Effigy.Render), not the viewport — the
/// bridge cannot capture an editor tool window, and a fitted orthographic drawing with a grid
/// is the more useful thing to reason from anyway.
/// </summary>
[McpToolset( "effigy", "Effigy, the modelling tool: look at the open studio, measure it, match it to a drawing, and build in it with a script." )]
public static class EffigyMcpTools
{
	static EffigyWindow Window => EffigyWindow.Current is { IsValid: true } w ? w : null;

	static PartStudio Studio => Window?.DiagnosticStudio;

	const string NotOpen = "Effigy is not open. Open it from the editor's Tools menu (or open a .effigy asset) and try again.";

	/// <summary>
	/// A picture of the open Effigy studio: the four views on one sheet (front, side, top, iso),
	/// or one view. Orthographic, fitted to the model, with a ground grid whose step is printed,
	/// the axes (x red forward, y green left, z blue up), the wire, and each body's name. Look at
	/// it after every change.
	/// </summary>
	[McpTool( "effigy_snapshot", Hints = McpToolHints.ReadOnly )]
	public static McpResult Snapshot(
		[Description( "sheet (front/side/top/iso), turn (eight views round the model), turn12, or front, side, top, iso, back, left" )] string view = "sheet",
		[Description( "Image size in pixels" )] int size = 512,
		[Description( "Draw the wireframe over the shading" )] bool wire = true,
		[Description( "Draw the rig's bones" )] bool bones = false,
		[Description( "Frame this body only (by name), for a close-up" )] string focus = null )
	{
		if ( Studio is not { } studio )
			return McpResult.Text( NotOpen );

		size = Math.Clamp( size, 64, 2048 );
		var options = new Effigy.Render.RenderOptions
		{
			Wireframe = wire,
			Bones = bones,
			FocusBodyId = focus is null ? null : ModelMeasure.FindBody( studio, focus )?.Id,
		};

		Raster raster;

		try
		{
			raster = ModelRender.Named( studio.Bodies, view, size, options, studio.Rig );
		}
		catch ( ArgumentException e )
		{
			return McpResult.Text( e.Message );
		}

		return McpResult.Image( raster.ToPng(), "image/png" ).WithText( ModelMeasure.Describe( studio ) );
	}

	/// <summary>The open studio in words: overall size, where it stands, a line per body with its
	/// size, centre and triangle count, the rig, and the history with anything that failed.</summary>
	[McpTool( "effigy_describe", Hints = McpToolHints.ReadOnly )]
	public static string Describe() => Studio is { } studio ? ModelMeasure.Describe( studio ) : NotOpen;

	/// <summary>
	/// What is at a place, or the distance between two. A place is a landmark — a body name, or
	/// a body's side (Torso.top, Hand_L.front, .bottom .back .left .right .min .max), a bone name
	/// (its head; .tail and .mid too), origin, ground — or a point x,y,z, or landmark+dx,dy,dz.
	/// </summary>
	[McpTool( "effigy_measure", Hints = McpToolHints.ReadOnly )]
	public static string Measure(
		[Description( "A landmark or point, e.g. Torso.top or 0,0,40 or @Head.front+2,0,0" )] string at,
		[Description( "A second landmark: reports the distance from the first" )] string to = null )
	{
		if ( Studio is not { } studio )
			return NotOpen;

		if ( !BuildScript.Point( studio, at, out var p, out var why ) )
			return why;

		if ( string.IsNullOrWhiteSpace( to ) )
			return ModelMeasure.At( studio, p );

		if ( !BuildScript.Point( studio, to, out var q, out var why2 ) )
			return why2;

		var d = q - p;
		return $"{at} ({p.x:0.##}, {p.y:0.##}, {p.z:0.##}) to {to} ({q.x:0.##}, {q.y:0.##}, {q.z:0.##}): {d.Length:0.###} (dx {d.x:0.##}, dy {d.y:0.##}, dz {d.z:0.##})";
	}

	/// <summary>Left–right symmetry of each body, as a percentage of its width. 0 is a mirror.</summary>
	[McpTool( "effigy_symmetry", Hints = McpToolHints.ReadOnly )]
	public static string Symmetry()
	{
		if ( Studio is not { } studio )
			return NotOpen;

		return string.Join( "\n", studio.Bodies.Where( b => b?.Mesh is not null )
			.Select( b => $"{b.Name ?? b.Id}: {ModelMeasure.SymmetryError( b.Mesh ) * 100f:0.#}% of width" ) );
	}

	/// <summary>
	/// Run a build script on the open studio, as one undo step. Lines like
	/// `add Primitive name=Torso shape=Box sizex=20 sizey=12 sizez=30 position=@ground+0,0,15`,
	/// `add Part part=Cog size=4 count=10 position=@Torso.front`, `add Spline points=…;… radius=1`,
	/// `set Torso sizez=32`, `fix Torso all`, `describe`, `measure`, `distance`, `symmetry`. Call
	/// effigy_help for the feature types and their parameters. A feature that fails stops the
	/// script and says why and what to change.
	/// </summary>
	[McpTool( "effigy_run" )]
	public static string Run( [Description( "The script, one command per line" )] string script )
	{
		if ( Window is not { } window || Studio is null )
			return NotOpen;

		return window.RunBuildScript( script );
	}

	/// <summary>The feature types a script can `add`, and for one type, its parameters and what
	/// each takes. Parts and their dials come from effigy_parts.</summary>
	[McpTool( "effigy_help", Hints = McpToolHints.ReadOnly )]
	public static string Help( [Description( "A feature type to describe, e.g. Spline, Part, Profile. Empty lists them all" )] string type = null )
	{
		if ( string.IsNullOrWhiteSpace( type ) )
			return "Feature types: " + string.Join( ", ", BuildScript.FeatureTypeNames() )
				+ "\nScript commands: add TYPE key=value…, set NAME key=value…, remove NAME, fix BODY|all loose|doubles|holes|normals|all, describe, measure PLACE, distance A B, symmetry, save PATH, echo…"
				+ "\nPoints: x,y,z, a landmark (Torso.top, spine.tail, ground, origin), or @landmark+dx,dy,dz. Body params take body names; list fields take a;b;c.";

		return BuildScript.Describe( type );
	}

	/// <summary>The part library — cog, bolt, hex bolt, rivet, knob, hinge, panel, pipe elbow,
	/// strap, buckle — and what Size, Length, Thickness, Count and Angle mean for each.</summary>
	[McpTool( "effigy_parts", Hints = McpToolHints.ReadOnly )]
	public static string Parts() =>
		string.Join( "\n", PartLibrary.Names.Select( n => $"{n}: {PartLibrary.Uses( n )}" ) )
		+ "\nAdd one with: add Part part=Cog size=4 thickness=1.5 count=10 length=1 position=@Torso.front rotationaxis=0,1,0 rotation=90";

	/// <summary>
	/// Compare the model's silhouette from a view against a reference drawing (a PNG: dark ink on
	/// light paper, a cut-out on transparency, or a light model on a dark ground). Returns the
	/// overlap, whether the proportions agree, where the model is wider or narrower, and a diff
	/// picture: orange is model only, blue is drawing only.
	/// </summary>
	[McpTool( "effigy_match", Hints = McpToolHints.ReadOnly )]
	public static McpResult Match(
		[Description( "front, side, top, iso, back or left" )] string view,
		[Description( "Path to the reference PNG" )] string referencePath )
	{
		if ( Studio is not { } studio )
			return McpResult.Text( NotOpen );

		var named = RenderView.Named( view );

		if ( named is null )
			return McpResult.Text( $"No view called '{view}'." );

		if ( !File.Exists( referencePath ) )
			return McpResult.Text( $"No file at {referencePath}." );

		const int size = 256;
		var (w, h, rgb, alpha) = Raster.LoadPng( referencePath );
		var reference = ReferenceMatch.ReferenceSilhouette( w, h, rgb, alpha, size, out var aspect );
		var result = ReferenceMatch.Compare( studio.Bodies, named, reference, aspect, size );

		return McpResult.Image( result.Diff.ToPng(), "image/png" ).WithText( result.Report );
	}

	/// <summary>Clean a body (or all): loose, doubles, holes, normals, or all. Recorded as a Mesh
	/// edit in the history, so it undoes.</summary>
	[McpTool( "effigy_fix" )]
	public static string Fix(
		[Description( "A body name, or all" )] string body,
		[Description( "Comma-separated: loose, doubles, holes, normals, all" )] string fixes = "all" )
	{
		if ( Window is not { } window || Studio is null )
			return NotOpen;

		return window.RunBuildScript( $"fix {body} {fixes.Replace( ',', ' ' )}" );
	}

	/// <summary>Open a .effigy document in the editor's Effigy window.</summary>
	[McpTool( "effigy_open" )]
	public static string Open( [Description( "Path to the .effigy file" )] string path )
	{
		if ( Window is not { } window )
			return NotOpen;

		if ( !File.Exists( path ) )
			return $"No file at {path}.";

		window.OpenDocumentPath( path );
		return $"Opened {path}.\n" + ModelMeasure.Describe( Studio );
	}

	/// <summary>Save the open studio: to its own path, or to a new one.</summary>
	[McpTool( "effigy_save" )]
	public static string Save( [Description( "Where to save. Empty saves to the document's own path" )] string path = null )
	{
		if ( Window is not { } window || Studio is null )
			return NotOpen;

		return window.SaveDocumentTo( path );
	}
}
