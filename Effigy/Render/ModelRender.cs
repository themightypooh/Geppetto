using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Render;

/// <summary>A named orthographic view: where the camera looks from, and which way is up on screen.</summary>
public sealed class RenderView
{
	public string Name;
	public Vec3 Forward;
	public Vec3 Up;

	/// <summary>Front looks at the model's face (+x is forward in this engine), so the model's left
	/// (+y) is on the viewer's right — the way a person in front of you appears. Side is from the
	/// model's right, with its front pointing right on screen. Top has forward at the top of the
	/// image. Iso is the three-quarter view from the front-right, a little above.</summary>
	public static readonly RenderView Front = new() { Name = "front", Forward = new Vec3( -1, 0, 0 ), Up = new Vec3( 0, 0, 1 ) };
	public static readonly RenderView Back = new() { Name = "back", Forward = new Vec3( 1, 0, 0 ), Up = new Vec3( 0, 0, 1 ) };
	public static readonly RenderView Side = new() { Name = "side", Forward = new Vec3( 0, 1, 0 ), Up = new Vec3( 0, 0, 1 ) };
	public static readonly RenderView Left = new() { Name = "left", Forward = new Vec3( 0, -1, 0 ), Up = new Vec3( 0, 0, 1 ) };
	public static readonly RenderView Top = new() { Name = "top", Forward = new Vec3( 0, 0, -1 ), Up = new Vec3( 1, 0, 0 ) };
	public static readonly RenderView Iso = new() { Name = "iso", Forward = new Vec3( -1f, 0.8f, -0.55f ).Normal, Up = new Vec3( 0, 0, 1 ) };

	public static readonly RenderView[] All = { Front, Side, Top, Iso, Back, Left };

	/// <summary>A view from <paramref name="yawDegrees"/> round the model (0 is the front, growing
	/// toward the model's left), <paramref name="elevationDegrees"/> above level. The turntable.</summary>
	public static RenderView Turn( float yawDegrees, float elevationDegrees = 18f )
	{
		var yaw = yawDegrees * MathF.PI / 180f;
		var pitch = elevationDegrees * MathF.PI / 180f;
		var eye = new Vec3( MathF.Cos( yaw ) * MathF.Cos( pitch ), MathF.Sin( yaw ) * MathF.Cos( pitch ), MathF.Sin( pitch ) );
		return new RenderView { Name = $"turn {yawDegrees:0}", Forward = -eye, Up = new Vec3( 0, 0, 1 ) };
	}

	/// <summary>A view by name: one of <see cref="All"/>, or `turn45`, `turn-30e5` — a yaw round the
	/// model and, after the e, an elevation in degrees (18 when left out). Null for nothing known.</summary>
	public static RenderView Named( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return null;

		name = name.Trim();

		var fixedView = All.FirstOrDefault( v => string.Equals( v.Name, name, StringComparison.OrdinalIgnoreCase ) );

		if ( fixedView is not null )
			return fixedView;

		var m = System.Text.RegularExpressions.Regex.Match( name, @"^turn\s*(-?\d+(?:\.\d+)?)(?:\s*e\s*(-?\d+(?:\.\d+)?))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase );

		if ( !m.Success )
			return null;

		var yaw = float.Parse( m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture );
		var elevation = m.Groups[2].Success ? float.Parse( m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture ) : 18f;
		return Turn( yaw, elevation );
	}

	/// <summary>The screen axes: right, up, and forward (depth grows along it).</summary>
	public (Vec3 Right, Vec3 Up, Vec3 Forward) Axes()
	{
		var f = Forward.Normal;
		var right = Vec3.Cross( f, Up );

		if ( right.LengthSquared < 1e-8f )
			right = Vec3.Cross( f, new Vec3( 1, 0, 0 ) );

		right = right.Normal;
		var up = Vec3.Cross( right, f ).Normal;
		return (right, up, f);
	}
}

public sealed class RenderOptions
{
	public bool Wireframe = true;
	public bool Grid = true;
	public bool Axes = true;
	public bool Labels = true;
	public bool ColourPerBody = true;
	public bool Bones = false;

	/// <summary>Empty space round the model, as a fraction of the frame.</summary>
	public float Margin = 0.10f;

	/// <summary>Units per pixel. Zero fits the frame to the model; set it to compare renders at
	/// one scale, or to show the model at the size it really is next to a reference.</summary>
	public float UnitsPerPixel = 0f;

	/// <summary>Draw the frame round this body's bounds instead of everything's — a close-up.</summary>
	public string FocusBodyId;

	/// <summary>Bodies drawn lit; the rest faint. Null draws them all the same.</summary>
	public HashSet<string> Highlight;
}

/// <summary>
/// Draws a studio's bodies into a <see cref="Raster"/>, from named views, with a ground grid,
/// the axes, a label per body and the wire on top. This is how an agent looks at what it built.
///
/// ORTHOGRAPHIC, FITTED TO THE MODEL. A perspective camera is nicer to look at and worse to
/// reason from: two things the same size look different sizes, and a picture you cannot measure
/// off is a picture that only says "something is there". Every view here is a drawing you can
/// hold a ruler to, and the grid says what the ruler reads.
/// </summary>
public static class ModelRender
{
	const int Background = 0x121418;
	const int GridColour = 0x24282e;
	const int GridMajor = 0x33383f;
	const int WireColour = 0x0c0e11;
	const int LabelColour = 0x9aa2ae;


	/// <summary>Body colours, from a fixed palette by index so the same body keeps its colour from
	/// view to view and render to render.</summary>
	static readonly int[] Palette =
	{
		0x8fa3b8, 0xd4a35c, 0x7bbf8f, 0xc77b7b, 0x9a8fd0, 0x67b8c6, 0xc9a2d6, 0xb5b56a,
	};

	public static Raster Render( IReadOnlyList<Body> bodies, RenderView view, int size, RenderOptions options = null, Skeleton rig = null, bool[] silhouette = null )
	{
		options ??= new RenderOptions();
		var raster = new Raster( size, size, Background );
		var (right, up, forward) = view.Axes();

		// Three lights that ride with the camera: a key over the viewer's right shoulder, a dimmer
		// fill from the left, and a rim from behind-above so a silhouette edge catches. Form reads
		// from the gradient between them; one light leaves a curved surface reading as flat.
		var key = (forward * -0.6f + up * 0.55f + right * 0.55f).Normal;
		var fill = (forward * -0.5f + up * 0.1f - right * 0.8f).Normal;
		var rim = (forward * 0.6f + up * 0.7f - right * 0.2f).Normal;

		var visible = (bodies ?? Array.Empty<Body>()).Where( b => b?.Mesh is not null && b.Visible && b.Mesh.Positions.Count > 0 ).ToList();

		// --- the frame: fit the model's projected bounds, or a fixed scale about its centre ---
		var frameBodies = options.FocusBodyId is not null
			? visible.Where( b => b.Id == options.FocusBodyId ).ToList()
			: visible;

		if ( frameBodies.Count == 0 )
			frameBodies = visible;

		var minX = float.MaxValue; var maxX = float.MinValue;
		var minY = float.MaxValue; var maxY = float.MinValue;
		var worldMin = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var worldMax = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var body in frameBodies )
		{
			foreach ( var p in body.Mesh.Positions )
			{
				var sx = Vec3.Dot( p, right );
				var sy = Vec3.Dot( p, up );
				minX = MathF.Min( minX, sx ); maxX = MathF.Max( maxX, sx );
				minY = MathF.Min( minY, sy ); maxY = MathF.Max( maxY, sy );
				worldMin = new Vec3( MathF.Min( worldMin.x, p.x ), MathF.Min( worldMin.y, p.y ), MathF.Min( worldMin.z, p.z ) );
				worldMax = new Vec3( MathF.Max( worldMax.x, p.x ), MathF.Max( worldMax.y, p.y ), MathF.Max( worldMax.z, p.z ) );
			}
		}

		if ( frameBodies.Count == 0 )
		{
			minX = minY = -10f; maxX = maxY = 10f;
			worldMin = new Vec3( -10, -10, 0 ); worldMax = new Vec3( 10, 10, 10 );
		}

		var span = MathF.Max( MathF.Max( maxX - minX, maxY - minY ), 1e-3f );
		var scale = options.UnitsPerPixel > 0f
			? 1f / options.UnitsPerPixel
			: size * (1f - options.Margin * 2f) / span;

		var cx = (minX + maxX) * 0.5f;
		var cy = (minY + maxY) * 0.5f;

		Vec2 Screen( Vec3 p ) => new(
			size * 0.5f + (Vec3.Dot( p, right ) - cx) * scale,
			size * 0.5f - (Vec3.Dot( p, up ) - cy) * scale );

		float DepthOf( Vec3 p ) => Vec3.Dot( p, forward );

		// --- ground grid, behind everything: a line every step that reads as about 40px ---
		if ( options.Grid )
		{
			var step = GridStep( scale );
			var extent = MathF.Max( MathF.Max( MathF.Abs( worldMin.x ), MathF.Abs( worldMax.x ) ), MathF.Max( MathF.Abs( worldMin.y ), MathF.Abs( worldMax.y ) ) );
			extent = MathF.Max( extent, span * 0.5f ) + step * 2f;
			var lines = (int)MathF.Min( MathF.Ceiling( extent / step ), 200 );

			for ( var i = -lines; i <= lines; i++ )
			{
				var major = i % 5 == 0;
				var colour = major ? GridMajor : GridColour;
				var a = Screen( new Vec3( i * step, -lines * step, 0 ) );
				var b = Screen( new Vec3( i * step, lines * step, 0 ) );
				raster.Line( a.x, a.y, b.x, b.y, colour );
				a = Screen( new Vec3( -lines * step, i * step, 0 ) );
				b = Screen( new Vec3( lines * step, i * step, 0 ) );
				raster.Line( a.x, a.y, b.x, b.y, colour );
			}

			if ( options.Labels )
				raster.Text( 6, size - 12, $"grid {Trim( step )} u", LabelColour, 1 );
		}

		if ( options.Axes )
		{
			var o = Screen( Vec3.Zero );
			var length = GridStep( scale );
			var ax = Screen( new Vec3( length, 0, 0 ) );
			var ay = Screen( new Vec3( 0, length, 0 ) );
			var az = Screen( new Vec3( 0, 0, length ) );
			raster.Line( o.x, o.y, ax.x, ax.y, 0xe5625a );
			raster.Line( o.x, o.y, ay.x, ay.y, 0x3fbf8a );
			raster.Line( o.x, o.y, az.x, az.y, 0x5b93ff );
		}

		// --- the bodies, depth tested ---
		for ( var bi = 0; bi < visible.Count; bi++ )
		{
			var body = visible[bi];
			var mesh = body.Mesh;
			var baseColour = options.ColourPerBody ? Palette[bi % Palette.Length] : Palette[0];
			var dim = options.Highlight is not null && !options.Highlight.Contains( body.Id );

			var projected = new Vec2[mesh.Positions.Count];
			var depth = new float[mesh.Positions.Count];

			for ( var i = 0; i < mesh.Positions.Count; i++ )
			{
				projected[i] = Screen( mesh.Positions[i] );
				depth[i] = DepthOf( mesh.Positions[i] );
			}

			foreach ( var face in mesh.Faces )
			{
				if ( face.Indices.Length < 3 )
					continue;

				var normal = mesh.FaceNormal( face );

				// Both sides are drawn — an open mesh's back wall still says where the mesh is —
				// but a face turned away is darkened, so a flipped face reads as the dark patch it
				// would be in the engine.
				var facing = Vec3.Dot( normal, forward ) < 0f;
				var n = facing ? normal : -normal;
				var shade = 0.24f
					+ 0.56f * MathF.Max( 0f, Vec3.Dot( n, key ) )
					+ 0.20f * MathF.Max( 0f, Vec3.Dot( n, fill ) )
					+ 0.16f * MathF.Pow( MathF.Max( 0f, Vec3.Dot( n, rim ) ), 3f );
				shade = MathF.Min( shade, 1.05f );

				if ( !facing )
					shade *= 0.45f;

				if ( dim )
					shade *= 0.35f;

				var colour = Raster.Rgb(
					(int)(((baseColour >> 16) & 0xFF) * shade),
					(int)(((baseColour >> 8) & 0xFF) * shade),
					(int)((baseColour & 0xFF) * shade) );

				for ( var k = 1; k + 1 < face.Indices.Length; k++ )
				{
					var a = face.Indices[0];
					var b = face.Indices[k];
					var c = face.Indices[k + 1];
					raster.Triangle( projected[a], projected[b], projected[c], depth[a], depth[b], depth[c], colour, silhouette );
				}
			}

			if ( options.Wireframe && mesh.Faces.Count < 60000 )
			{
				var bias = span * 0.002f;

				foreach ( var face in mesh.Faces )
				{
					for ( var k = 0; k < face.Indices.Length; k++ )
					{
						var a = face.Indices[k];
						var b = face.Indices[(k + 1) % face.Indices.Length];
						raster.Line( projected[a].x, projected[a].y, projected[b].x, projected[b].y, WireColour, 0.75f, depth[a], depth[b], bias );
					}
				}
			}
		}

		if ( options.Bones && rig is { Count: > 0 } )
		{
			for ( var i = 0; i < rig.Count; i++ )
			{
				var head = Screen( rig.HeadWorld( i ) );
				var tail = Screen( rig.TailWorld( i ) );
				raster.Line( head.x, head.y, tail.x, tail.y, 0xf0a13a );
				raster.Rect( (int)head.x - 1, (int)head.y - 1, 3, 3, 0xffffff );
			}
		}

		// --- labels: the body's name at its centre, and the view's name ---
		if ( options.Labels )
		{
			foreach ( var body in visible )
			{
				var centre = Vec3.Zero;
				foreach ( var p in body.Mesh.Positions )
					centre += p;
				centre /= body.Mesh.Positions.Count;

				var at = Screen( centre );
				var text = body.Name ?? body.Id;
				var w = Raster.TextWidth( text, 1 );
				raster.Rect( (int)at.x - w / 2 - 2, (int)at.y - 4, w + 4, 9, 0x000000, 0.55f );
				raster.Text( (int)at.x - w / 2, (int)at.y - 3, text, 0xe7e9ee, 1 );
			}

			raster.Text( 6, 6, view.Name, LabelColour, 2 );
			raster.Text( 6, 20, $"{Trim( worldMax.x - worldMin.x )} x {Trim( worldMax.y - worldMin.y )} x {Trim( worldMax.z - worldMin.z )} u", LabelColour, 1 );
		}

		return raster;
	}

	/// <summary>
	/// A picture by name: "sheet" (front, side, top, iso), "turn" (eight views round the model at
	/// 45°, a turntable; turn4, turn12, turn16, turn24), any single view, or "turn45" / "turn-30e5"
	/// for one view from a yaw and an elevation. One entry for the CLI, the script and the
	/// editor tool, so they cannot disagree about what a name means.
	/// </summary>
	public static Raster Named( IReadOnlyList<Body> bodies, string name, int size, RenderOptions options = null, Skeleton rig = null )
	{
		name = (name ?? "sheet").Trim().ToLowerInvariant();

		if ( name == "sheet" )
			return Sheet( bodies, size / 2, options, rig );

		// "turn" and "turn12" are turntables; "turn45" or "turn-30e5" is one view from that angle.
		if ( name is "turn" or "turn4" or "turn8" or "turn12" or "turn16" or "turn24" )
			return Turntable( bodies, size / 4, name == "turn" ? 8 : int.Parse( name[4..] ), options, rig );

		var view = RenderView.Named( name );

		if ( view is null )
			throw new ArgumentException( $"No view called '{name}'. Views: sheet, turn, turn12, {string.Join( ", ", RenderView.All.Select( v => v.Name ) )}." );

		return Render( bodies, view, size, options, rig );
	}

	/// <summary>Views round the model, four to a row: the turntable. Elevated 18°, so the top reads too.</summary>
	public static Raster Turntable( IReadOnlyList<Body> bodies, int tile, int steps, RenderOptions options = null, Skeleton rig = null )
	{
		var columns = Math.Min( 4, steps );
		var rows = (steps + columns - 1) / columns;
		var sheet = new Raster( tile * columns, tile * rows, Background );

		for ( var i = 0; i < steps; i++ )
		{
			var one = Render( bodies, RenderView.Turn( 360f * i / steps ), tile, options, rig );
			var ox = (i % columns) * tile;
			var oy = (i / columns) * tile;

			for ( var y = 0; y < tile; y++ )
				Array.Copy( one.Pixels, y * tile, sheet.Pixels, (oy + y) * sheet.Width + ox, tile );
		}

		return sheet;
	}

	/// <summary>The four views on one sheet: front, side, top, iso.</summary>
	public static Raster Sheet( IReadOnlyList<Body> bodies, int tile, RenderOptions options = null, Skeleton rig = null )
	{
		var views = new[] { RenderView.Front, RenderView.Side, RenderView.Top, RenderView.Iso };
		var sheet = new Raster( tile * 2, tile * 2, Background );

		for ( var i = 0; i < views.Length; i++ )
		{
			var one = Render( bodies, views[i], tile, options, rig );
			var ox = (i % 2) * tile;
			var oy = (i / 2) * tile;

			for ( var y = 0; y < tile; y++ )
				Array.Copy( one.Pixels, y * tile, sheet.Pixels, (oy + y) * sheet.Width + ox, tile );
		}

		// Hairlines between the tiles.
		for ( var i = 0; i < sheet.Width; i++ )
		{
			sheet.Plot( i, tile, 0x2a2e35 );
			sheet.Plot( tile, i, 0x2a2e35 );
		}

		return sheet;
	}

	/// <summary>
	/// The silhouette from a view, fitted to the model's own bounds with no margin, as a bool per
	/// pixel. What <see cref="ReferenceMatch"/> compares against a reference drawing.
	/// </summary>
	public static bool[] Silhouette( IReadOnlyList<Body> bodies, RenderView view, int size )
	{
		var mask = new bool[size * size];
		Render( bodies, view, size, new RenderOptions { Wireframe = false, Grid = false, Axes = false, Labels = false, Margin = 0f }, null, mask );
		return mask;
	}

	static float GridStep( float scale )
	{
		// A grid line about every 40 pixels, snapped to 1, 2, 5 × 10^n units.
		var raw = 40f / MathF.Max( scale, 1e-6f );
		var magnitude = MathF.Pow( 10f, MathF.Floor( MathF.Log10( raw ) ) );
		var mantissa = raw / magnitude;
		var snapped = mantissa < 1.5f ? 1f : mantissa < 3.5f ? 2f : mantissa < 7.5f ? 5f : 10f;
		return snapped * magnitude;
	}

	static string Trim( float v ) => v >= 100f ? $"{v:0}" : v >= 10f ? $"{v:0.#}" : $"{v:0.##}";
}
