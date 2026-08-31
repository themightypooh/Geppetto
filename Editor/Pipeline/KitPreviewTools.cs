using Editor;
using Editor.Mcp;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Marionette.EditorTools;

/// <summary>
/// Seeing a generated set, instead of inspecting it one asset at a time.
/// </summary>
/// <remarks>
/// A city build produces 60+ models in one go. Checking them individually -- spawn, look, delete,
/// next -- costs more round trips than the build itself, and it's how a bench with its pivot in
/// the wrong place survives three iterations unnoticed. Both tools here answer "what did that
/// build actually produce" in ONE look: a contact sheet for shape and silhouette, a spawned grid
/// for scale and collision.
/// </remarks>
[McpToolset( "kit_view", "See a whole generated set at once - contact sheets and spawned grids" )]
public static class KitPreviewTools
{
	/// <summary>Where sheets land, under the project root.</summary>
	private const string OutputFolder = "kit_sheets";

	/// <summary>
	/// Render every model in a folder into ONE labelled PNG grid. The fastest way to find the
	/// piece that came out wrong.
	/// </summary>
	/// <remarks>
	/// Uses the asset system's own thumbnails, which means the framing is whatever the asset
	/// browser shows rather than a chosen camera angle -- consistent, but not art-directed. They
	/// are generated on demand, so the first sheet for a fresh build is slow and the next is fast.
	/// </remarks>
	/// <param name="folder">Folder relative to Assets, e.g. "models/rp_city".</param>
	/// <param name="filter">Only include assets whose name contains this. Empty takes everything.</param>
	/// <param name="cell">Pixel size of each thumbnail cell.</param>
	/// <param name="columns">Cells per row. 0 picks a roughly square grid.</param>
	/// <param name="output">Output filename. Empty derives one from the folder.</param>
	/// <param name="limit">Stop after this many models, so a huge folder can't produce a gigantic image.</param>
	[McpTool( "kit_contact_sheet" )]
	public static async Task<SheetResult> ContactSheet(
		string folder = "models/rp_city",
		string filter = "",
		[Sandbox.Range( 32, 512 )] int cell = 192,
		[Sandbox.Range( 0, 32 )] int columns = 0,
		string output = "",
		[Sandbox.Range( 1, 400 )] int limit = 120 )
	{
		var assets = ModelAssets( folder, filter ).Take( limit ).ToList();
		if ( assets.Count == 0 )
			throw new Exception( $"No models found under '{folder}'{(string.IsNullOrWhiteSpace( filter ) ? "" : $" matching '{filter}'")}." );

		int cols = columns > 0 ? columns : (int)MathF.Ceiling( MathF.Sqrt( assets.Count ) );
		int rows = (int)MathF.Ceiling( assets.Count / (float)cols );

		// Label strip under each cell. Without it a sheet of 60 grey shapes tells you something is
		// wrong but not which file to go and fix.
		int labelHeight = Math.Max( 14, cell / 10 );
		int cellHeight = cell + labelHeight;

		var sheet = new Bitmap( cols * cell, rows * cellHeight );
		sheet.Clear( new Color( 0.10f, 0.10f, 0.12f ) );
		sheet.SetAntialias( true );

		var placed = new List<string>();
		var missing = new List<string>();

		for ( int i = 0; i < assets.Count; i++ )
		{
			var asset = assets[i];

			int col = i % cols;
			int row = i / cols;
			float x = col * cell;
			float y = row * cellHeight;

			try
			{
				var pixmap = await Thumb( asset );

				if ( pixmap is not null )
				{
					// Pixmap -> Bitmap goes through PNG bytes: Pixmap is the editor's Qt-side image
					// and Bitmap is the Skia canvas we're compositing on, and encoded bytes are the
					// one representation they both speak.
					var bitmap = Bitmap.CreateFromBytes( pixmap.GetPng() );
					sheet.DrawBitmap( bitmap, new Rect( x, y, cell, cell ) );
					placed.Add( asset.Path );
				}
				else
				{
					missing.Add( asset.Path );
				}
			}
			catch ( Exception e )
			{
				missing.Add( $"{asset.Name}: {e.Message}" );
			}

			// Cell border, so pieces with a lot of empty space around them still read as separate.
			sheet.SetPen( new Color( 1f, 1f, 1f, 0.08f ), 1f );
			sheet.DrawRect( new Rect( x, y, cell, cellHeight ) );

			sheet.SetFill( Color.White.WithAlpha( 0.85f ) );
			sheet.DrawText(
				new TextRendering.Scope( asset.Name, Color.White.WithAlpha( 0.85f ), labelHeight * 0.7f ),
				new Rect( x + 2, y + cell, cell - 4, labelHeight ),
				TextFlag.Center );
		}

		var path = OutputPath( output, folder, ".png" );
		File.WriteAllBytes( path, sheet.ToPng() );

		Log.Info( $"[Kit] contact sheet: {placed.Count} models -> {path}" );

		return new SheetResult
		{
			File = path,
			Models = placed.Count,
			Missing = missing.Count,
			Grid = $"{cols} x {rows}",
			Resolution = $"{cols * cell} x {rows * cellHeight}",
			Order = "left to right, top to bottom, alphabetical",
			Errors = missing.Take( 10 ).ToArray(),
		};
	}

	/// <summary>
	/// Spawn every model in a folder into the open scene as a labelled grid, spaced by their own
	/// bounds so nothing overlaps. For judging real scale, proportion and collision -- the things a
	/// thumbnail can't show you.
	/// </summary>
	/// <remarks>
	/// Everything lands under one parent object, so removing the layout is a single delete (or
	/// another call with clear:true). Nothing else in the scene is touched.
	/// </remarks>
	/// <param name="folder">Folder relative to Assets, e.g. "models/rp_city".</param>
	/// <param name="filter">Only include assets whose name contains this. Empty takes everything.</param>
	/// <param name="columns">Pieces per row. 0 picks a roughly square grid.</param>
	/// <param name="padding">Extra inches between pieces, on top of their own size.</param>
	/// <param name="origin">Where the grid starts, as "x,y,z".</param>
	/// <param name="parentName">Name of the parent object everything is spawned under.</param>
	/// <param name="clear">Remove a previous layout with the same parent name first.</param>
	/// <param name="limit">Stop after this many models.</param>
	[McpTool( "kit_layout" )]
	public static LayoutResult Layout(
		string folder = "models/rp_city",
		string filter = "",
		[Sandbox.Range( 0, 32 )] int columns = 0,
		float padding = 24f,
		string origin = "0,0,0",
		string parentName = "KIT LAYOUT",
		bool clear = true,
		[Sandbox.Range( 1, 400 )] int limit = 120 )
	{
		var scene = SceneEditorSession.Active?.Scene
			?? throw new Exception( "No scene open in the editor." );

		var assets = ModelAssets( folder, filter ).Take( limit ).ToList();
		if ( assets.Count == 0 )
			throw new Exception( $"No models found under '{folder}'." );

		if ( clear )
		{
			foreach ( var old in scene.GetAllObjects( false )
				.Where( o => o.Name == parentName ).ToArray() )
			{
				old.Destroy();
			}
		}

		// scene.CreateObject() rather than new GameObject(): GameObject.Scene is read-only, so a
		// bare construction lands in whatever scene is ambient rather than the one we resolved.
		var root = scene.CreateObject();
		root.Name = parentName;
		var start = Vector3.Parse( origin );

		// Load first, so cell size can come from the actual geometry. A grid spaced for a bench and
		// then filled with a city hall is just a pile.
		var loaded = assets
			.Select( a => new { Asset = a, Model = Model.Load( a.Path ) } )
			.Where( x => x.Model is not null && !x.Model.IsError )
			.ToList();

		if ( loaded.Count == 0 )
			throw new Exception( "Every model in that folder failed to load - run kit_validate." );

		float cellSize = loaded.Max( x => MathF.Max( x.Model.Bounds.Size.x, x.Model.Bounds.Size.y ) ) + padding;

		int cols = columns > 0 ? columns : (int)MathF.Ceiling( MathF.Sqrt( loaded.Count ) );
		int rows = (int)MathF.Ceiling( loaded.Count / (float)cols );

		var spawned = new List<string>();

		for ( int i = 0; i < loaded.Count; i++ )
		{
			var entry = loaded[i];

			int col = i % cols;
			int row = i / cols;

			var go = new GameObject( true, entry.Asset.Name ) { Parent = root };

			// Sit each piece ON the grid plane rather than at its own origin: generated pieces have
			// wildly different pivots, and comparing them is impossible when half are buried.
			var mins = entry.Model.Bounds.Mins;
			go.WorldPosition = start + new Vector3( col * cellSize, -row * cellSize, -mins.z );

			var renderer = go.Components.Create<ModelRenderer>();
			renderer.Model = entry.Model;

			spawned.Add( entry.Asset.Name );
		}

		// Enough distance to see the whole grid, from a three-quarter angle looking down.
		float span = MathF.Max( cols, rows ) * cellSize;
		var centre = start + new Vector3( (cols - 1) * cellSize * 0.5f, -(rows - 1) * cellSize * 0.5f, 0f );
		var camera = centre + new Vector3( -span * 0.7f, span * 0.7f, span * 0.7f );

		Log.Info( $"[Kit] layout: {spawned.Count} models under '{parentName}'" );

		return new LayoutResult
		{
			Parent = parentName,
			Spawned = spawned.Count,
			Skipped = assets.Count - loaded.Count,
			Grid = $"{cols} x {rows}",
			CellInches = cellSize,
			CameraPosition = $"{camera.x:0},{camera.y:0},{camera.z:0}",
			CameraAngles = "35,-45,0",
			Note = "Pass those camera values to set_editor_camera, then editor_camera_screenshot.",
		};
	}

	/// <summary>
	/// An asset's preview thumbnail, generated if it doesn't exist yet.
	/// </summary>
	/// <remarks>
	/// <c>Editor.AssetThumbnail.GetAssetThumbAsync</c> is the one that GENERATES a missing
	/// thumbnail and waits for it, and it's internal -- reached by reflection here, the same way
	/// the trailer renderer reaches SceneCamera.RenderToTexture. The public
	/// <see cref="Asset.GetAssetThumb"/> is the fallback, but on its own it returns the asset type
	/// ICON when no preview exists yet, which on a freshly built folder would produce a sheet of
	/// sixty identical placeholder squares.
	/// </remarks>
	private static async Task<Pixmap> Thumb( Asset asset )
	{
		try
		{
			var type = typeof( Asset ).Assembly.GetType( "Editor.AssetThumbnail" );
			var method = type?.GetMethod( "GetAssetThumbAsync",
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
				| System.Reflection.BindingFlags.Static );

			if ( method?.Invoke( null, new object[] { asset } ) is Task task )
			{
				await task;

				if ( task.GetType().GetProperty( "Result" )?.GetValue( task ) is Pixmap generated )
					return generated;
			}
		}
		catch
		{
			// Engine internals moved; the fallback below still produces a usable sheet.
		}

		return asset.GetAssetThumb( true );
	}

	/// <summary>Model assets under a project-relative folder, alphabetical.</summary>
	private static List<Asset> ModelAssets( string folder, string filter )
	{
		var prefix = folder.Replace( '\\', '/' ).TrimEnd( '/' ) + "/";

		return AssetSystem.All
			.Where( a => a?.Path is not null
				&& a.Path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase )
				&& a.Path.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase )
				&& (string.IsNullOrWhiteSpace( filter )
					|| a.Name.Contains( filter, StringComparison.OrdinalIgnoreCase )) )
			.OrderBy( a => a.Path, StringComparer.OrdinalIgnoreCase )
			.ToList();
	}

	private static string OutputPath( string output, string folder, string extension )
	{
		var root = Path.Combine( Project.Current.GetRootPath(), OutputFolder );
		Directory.CreateDirectory( root );

		if ( string.IsNullOrWhiteSpace( output ) )
			output = folder.Replace( '/', '_' ).Replace( '\\', '_' );

		if ( !output.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) )
			output += extension;

		return Path.Combine( root, output );
	}

	/// <summary>What a contact sheet came out as.</summary>
	public class SheetResult
	{
		public string File { get; set; }
		public int Models { get; set; }
		public int Missing { get; set; }
		public string Grid { get; set; }
		public string Resolution { get; set; }
		public string Order { get; set; }
		public string[] Errors { get; set; }
	}

	/// <summary>What a spawned layout put in the scene.</summary>
	public class LayoutResult
	{
		public string Parent { get; set; }
		public int Spawned { get; set; }
		public int Skipped { get; set; }
		public string Grid { get; set; }
		public float CellInches { get; set; }
		public string CameraPosition { get; set; }
		public string CameraAngles { get; set; }
		public string Note { get; set; }
	}
}
