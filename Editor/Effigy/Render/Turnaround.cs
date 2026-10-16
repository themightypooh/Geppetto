using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy.Render;

/// <summary>What a turnaround renders and how.</summary>
public sealed class TurnaroundOptions
{
	/// <summary>True: plain clay, the form only. False: what the materials make it look like — Flesh
	/// Material maps or vertex materials, painted vertex colours, else clay.</summary>
	public bool Clay = true;

	/// <summary>Each view's square size in pixels.</summary>
	public int Size = 512;

	/// <summary>Degrees the camera looks down from level.</summary>
	public float Elevation = 12f;

	/// <summary>Shading samples per output pixel along each side. 1 is enough: edges come from
	/// <see cref="Coverage"/>. Raise it for strands thinner than a pixel, or for a smoother occlusion.</summary>
	public int Supersample = 1;
	/// <summary>Antialiasing samples per pixel (<see cref="ShadeScene.Coverage"/>): 16 is clean, 64 for a final.</summary>
	public int Coverage = 16;

	/// <summary>Only this body, by id. Null renders every visible body.</summary>
	public string FocusBodyId;

	/// <summary>A material preset for bodies with no Flesh Material, in material mode. Null leaves them clay.</summary>
	public FleshStack Fallback;

	/// <summary>Printed on each tile before the view's name. Null prints the view alone.</summary>
	public string Label;

	/// <summary>The views, by name: front, quarter, side, back (the default four), left, top.</summary>
	public IReadOnlyList<string> Views = Turnaround.DefaultViews;
}

/// <summary>A rendered turnaround: each view, the labelled sheet, and what was drawn how.</summary>
public sealed class TurnaroundResult
{
	public bool Ok;
	public string Error;
	public readonly List<(string View, Raster Image)> Views = new();
	public Raster Sheet;
	public readonly List<string> Log = new();
	public readonly List<string> Written = new();
}

/// <summary>
/// Render Turnaround: the model lit and shaded from the front, a front quarter, the side and the
/// back, saved as one labelled sheet plus a picture per view. The editor's File ▸ Render
/// Turnaround and the build script's `turnaround` both call this; the script's
/// `render … shade=` builds its scene here too, so the three cannot drift apart.
///
/// The cameras follow the `shade` command's: the model's front is +X, the quarter view stands
/// front-right (+X, −Y) and the side view looks from −Y. Framing is ShadedRender.Sheet's — the
/// whole model's bounding sphere fills each tile, the floor under its lowest point, and the studio
/// three-light set turned with each camera so the key always comes from its front-right.
/// </summary>
public static class Turnaround
{
	public static readonly IReadOnlyList<string> DefaultViews = new[] { "front", "quarter", "side", "back" };

	/// <summary>Camera yaw round the model, degrees, 0 looking at the front from +X.</summary>
	public static bool TryYaw( string view, out float yaw, out float elevation )
	{
		elevation = float.NaN;
		yaw = 0f;

		switch ( view?.Trim().ToLowerInvariant() )
		{
			case "front": yaw = 0f; return true;
			case "quarter": yaw = -45f; return true;
			case "side": case "right": yaw = -90f; return true;
			case "back": yaw = 180f; return true;
			case "left": yaw = 90f; return true;
			case "top": yaw = 0f; elevation = 89f; return true;
			default: return false;
		}
	}

	/// <summary>
	/// The model as a shade scene: each visible body (or the focused one) in clay, or in what its
	/// materials make it look like. <paramref name="log"/> gets one line per body saying which.
	/// Meshes are the studio's own, not copies: render before the next rebuild.
	/// </summary>
	public static ShadeScene Scene( PartStudio studio, bool clay, string focusBodyId, FleshStack fallback, List<string> log )
	{
		var scene = new ShadeScene();

		if ( studio is null )
			return scene;

		var flesh = studio.Features.OfType<FleshMaterialFeature>().Where( x => !x.Suppressed ).ToList();

		foreach ( var body in studio.Bodies )
		{
			if ( body.Mesh is null || body.Mesh.Faces.Count == 0 || !body.Visible ) continue;
			if ( focusBodyId is not null && body.Id != focusBodyId ) continue;
			var name = body.Name ?? body.Id;

			if ( clay ) { scene.Add( body.Mesh, ShadeMaterial.Clay() ); continue; }

			var owner = flesh.LastOrDefault( x => x.Maps.ContainsKey( body.Id ) || x.VertexMaterials.ContainsKey( body.Id ) );
			if ( owner is not null && owner.VertexMaterials.TryGetValue( body.Id, out var vm ) )
			{
				var coloured = body.Mesh.Clone();
				coloured.VertexColors = vm.Colour;
				scene.Add( coloured, FleshMaterial.Shade( owner.Stack, vertex: vm ) );
				log?.Add( $"{name}: {owner.PresetName} (per vertex)" );
			}
			else if ( owner is not null && owner.Maps.TryGetValue( body.Id, out var maps ) )
			{
				scene.Add( body.Mesh, FleshMaterial.Shade( owner.Stack, maps ) );
				log?.Add( $"{name}: {owner.PresetName} ({maps.Size}² maps)" );
			}
			else if ( body.Mesh.Paint is { } paint )
			{
				// Painted layers: colour, roughness, metallic and the normal from painted height,
				// exactly the images `paintmaps` and the editor's compile write.
				scene.Add( body.Mesh, PaintTextureSet.Shade( paint ) );
				log?.Add( $"{name}: its paint ({paint.Width}² maps)" );
			}
			else if ( fallback is not null )
			{
				var v = FleshMaterial.BakeVertices( body.Mesh, fallback );
				var coloured = body.Mesh.Clone();
				coloured.VertexColors = v.Colour;
				scene.Add( coloured, FleshMaterial.Shade( fallback, vertex: v ) );
				log?.Add( $"{name}: {fallback.Name} (preset, per vertex)" );
			}
			else if ( body.Mesh.HasVertexColors )
			{
				scene.Add( body.Mesh, new ShadeMaterial { Roughness = 0.35f, Coat = 0.3f } );
				log?.Add( $"{name}: its vertex colours" );
			}
			else
			{
				scene.Add( body.Mesh, ShadeMaterial.Clay() );
				log?.Add( $"{name}: clay (no Flesh Material on it; add one, or pass preset=)" );
			}
		}

		return scene;
	}

	/// <summary>Render the views and lay them out as one labelled sheet. Writes nothing.</summary>
	public static TurnaroundResult Render( PartStudio studio, TurnaroundOptions options = null )
	{
		options ??= new TurnaroundOptions();
		var result = new TurnaroundResult();

		var views = (options.Views ?? DefaultViews).Where( v => !string.IsNullOrWhiteSpace( v ) ).ToList();
		var unknown = views.Where( v => !TryYaw( v, out _, out _ ) ).ToList();

		if ( views.Count == 0 || unknown.Count > 0 )
			return Fail( result, views.Count == 0
				? "No views asked for. Use front, quarter, side, back, left or top."
				: $"No view called '{unknown[0]}'. Use front, quarter, side, back, left or top." );

		if ( options.Size < 16 || options.Size > 4096 )
			return Fail( result, $"A view {options.Size} pixels wide can't be rendered: use a size from 16 to 4096." );

		if ( studio is null )
			return Fail( result, "No model is open to render." );

		var focus = options.FocusBodyId is null ? null : studio.Bodies.FirstOrDefault( b => b.Id == options.FocusBodyId );

		if ( options.FocusBodyId is not null && focus is null )
			return Fail( result, $"There is no body '{options.FocusBodyId}' to render. Pick a body that is in the model, or render the whole model." );

		var scene = Scene( studio, options.Clay, options.FocusBodyId, options.Fallback, result.Log );
		scene.Coverage = options.Coverage;

		if ( scene.Items.Count == 0 )
			return Fail( result, focus is null
				? "Nothing to render: no visible body has faces. Add a body, or show the hidden ones."
				: $"{focus.Name ?? focus.Id} has no faces to render." );

		// Framing: ShadedRender.Sheet's, so a turnaround and a script `render shade=` sheet match.
		var bmin = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var bmax = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var (mesh, _) in scene.Items )
			foreach ( var p in mesh.Positions )
			{
				bmin = new Vec3( MathF.Min( bmin.x, p.x ), MathF.Min( bmin.y, p.y ), MathF.Min( bmin.z, p.z ) );
				bmax = new Vec3( MathF.Max( bmax.x, p.x ), MathF.Max( bmax.y, p.y ), MathF.Max( bmax.z, p.z ) );
			}

		var centre = (bmin + bmax) * 0.5f;
		var radius = MathF.Max( 1e-3f, (bmax - bmin).Length * 0.5f );
		const float fov = 30f;
		var dist = radius / MathF.Sin( fov * MathF.PI / 360f ) * 1.08f;
		scene.FloorHeight = bmin.z;
		scene.AoRadius = radius * 0.08f;

		foreach ( var view in views )
		{
			TryYaw( view, out var yaw, out var up );
			var y = yaw * MathF.PI / 180f;
			var e = (float.IsNaN( up ) ? options.Elevation : up) * MathF.PI / 180f;
			var dir = new Vec3( MathF.Cos( y ) * MathF.Cos( e ), MathF.Sin( y ) * MathF.Cos( e ), MathF.Sin( e ) );
			var camera = new ShadeCamera( centre + dir * dist, centre, fov );

			scene.Lights.Clear();
			scene.StudioLights( -dir );

			result.Views.Add( (view.Trim().ToLowerInvariant(), ShadedRender.Render( scene, camera, options.Size, options.Size, options.Supersample )) );
		}

		scene.Lights.Clear();
		result.Sheet = Sheet( result.Views, options.Label );
		result.Ok = true;
		result.Log.Add( $"rendered {(options.Clay ? "clay" : "materials")}, {result.Views.Count} views at {options.Size}²" );
		return result;
	}

	/// <summary>
	/// Render and save: <c>name_turnaround.png</c> (the sheet) and <c>name_&lt;view&gt;.png</c> per view,
	/// in <paramref name="folder"/>, which is made if missing. A folder that can't be written says so.
	/// </summary>
	public static TurnaroundResult Write( PartStudio studio, string folder, string name, TurnaroundOptions options = null )
	{
		if ( string.IsNullOrWhiteSpace( folder ) )
			return Fail( new TurnaroundResult(), "No folder to save the turnaround in. Choose one." );

		name = SafeName( name );
		var result = Render( studio, options );

		if ( !result.Ok )
			return result;

		try
		{
			Directory.CreateDirectory( folder );

			var sheet = Path.Combine( folder, $"{name}_turnaround.png" );
			result.Sheet.SavePng( sheet );
			result.Written.Add( sheet );

			foreach ( var (view, image) in result.Views )
			{
				var path = Path.Combine( folder, $"{name}_{view}.png" );
				image.SavePng( path );
				result.Written.Add( path );
			}
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException )
		{
			result.Ok = false;
			result.Error = $"The turnaround rendered but couldn't be saved in {folder} ({e.Message}). Choose a folder you can write to.";
			return result;
		}

		result.Log.Add( $"saved {result.Written[0]}" );
		return result;
	}

	/// <summary>The views in one row, each labelled in its corner, on the dark sheet colour.</summary>
	public static Raster Sheet( IReadOnlyList<(string View, Raster Image)> views, string label = null, int gap = 4 )
	{
		var w = views.Max( t => t.Image.Width );
		var h = views.Max( t => t.Image.Height );
		var sheet = new Raster( views.Count * (w + gap) + gap, h + gap * 2, Raster.Rgb( 0x20, 0x20, 0x20 ) );

		for ( var i = 0; i < views.Count; i++ )
		{
			var (view, img) = views[i];
			var ox = gap + i * (w + gap);
			for ( var row = 0; row < img.Height; row++ )
				Array.Copy( img.Pixels, row * img.Width, sheet.Pixels, (gap + row) * sheet.Width + ox, img.Width );

			var text = string.IsNullOrEmpty( label ) ? view : $"{label} - {view}";
			sheet.Rect( ox, gap, Raster.TextWidth( text ) + 12, 22, Raster.Rgb( 0, 0, 0 ), 0.6f );
			sheet.Text( ox + 6, gap + 4, text, Raster.Rgb( 255, 255, 255 ) );
		}

		return sheet;
	}

	/// <summary>A file-name-safe version of a model name; "model" when there is nothing left.</summary>
	public static string SafeName( string name )
	{
		var invalid = Path.GetInvalidFileNameChars();
		var safe = new string( (name ?? "").Trim().Select( c => invalid.Contains( c ) || c == ' ' ? '_' : c ).ToArray() );
		return string.IsNullOrEmpty( safe ) ? "model" : safe;
	}

	static TurnaroundResult Fail( TurnaroundResult result, string why )
	{
		result.Ok = false;
		result.Error = why;
		return result;
	}
}
