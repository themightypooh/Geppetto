using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

internal enum ViewCubeButton { None, Home, Left, Right, Up, Down, RollLeft, RollRight }

/// <summary>
/// The view cube's geometry, hit testing and drawing — everything about it except the camera and
/// the mouse, which live in EffigyViewport.OrientationGizmo.cs.
///
/// ENGINE-FREE ON PURPOSE: it touches only Vector2, Vector3, Rotation, Color and Rect, so the
/// headless render compiles this exact file against small stand-ins for those and draws the cube
/// from any angle and hover state to a picture. What it shows is what the editor draws.
///
/// All positions are in RENDER pixels — logical pixels times the DPI scale — the space the
/// overlay draws in.
/// </summary>
internal static class EffigyViewCube
{
	/// <summary>
	/// One clickable piece of the chamfered cube: the view it names (a face, edge or corner
	/// direction, not normalised), its outline on the unit cube in order round, and — faces only —
	/// its label and the way the label reads.
	/// </summary>
	public sealed record Piece( Vector3 Direction, Vector3[] Outline, string Label, Vector3 TextRight, Vector3 TextUp, bool IsFace );

	/// <summary>How far back from each edge the bevel cuts, in unit-cube terms. Thin: an edge is
	/// a strip you can aim at, not a band that eats the faces.</summary>
	public const float EdgeCut = 0.2f;

	/// <summary>How far back from each corner the corner cut reaches along the three edges, in
	/// unit-cube terms. Bigger than the edge cut on purpose — the corners are the iso views and
	/// want a decent target — so the two are set separately rather than sharing one size.</summary>
	public const float CornerCut = 0.68f;

	/// <summary>Half the cube's edge, in logical pixels.</summary>
	public const float HalfPixels = 30f;

	/// <summary>Space between the cube's centre and the canvas's top and right edges, in logical
	/// pixels — room for the arrows round it and a margin past them.</summary>
	public const float Inset = 116f;

	/// <summary>Size of a button round the cube, in logical pixels.</summary>
	public const float ButtonPixels = 26f;

	/// <summary>
	/// The chamfered cube as 26 solid pieces — 6 faces, 12 edge bevels, 8 corner triangles — each
	/// one a view to click: a face looks straight down an axis, an edge from 45° between two, a
	/// corner is that corner's isometric view. A closed convex solid, so the pieces facing you
	/// tile its outline without overlapping and the one under the cursor is the one you meant.
	/// </summary>
	public static Piece[] Pieces
	{
		get
		{
			// Rebuilt whenever the cut sizes it was built with are not the ones in the code now.
			// A plain static readonly would do on a cold start, but s&box's hot reload carries
			// static fields across from the old assembly — the editor kept drawing the first
			// cube it ever built, whatever the sizes said after that.
			if ( _pieces is null || _builtEdgeCut != EdgeCut || _builtCornerCut != CornerCut )
			{
				_pieces = BuildPieces();
				_builtEdgeCut = EdgeCut;
				_builtCornerCut = CornerCut;
			}

			return _pieces;
		}
	}

	private static Piece[] _pieces;
	private static float _builtEdgeCut, _builtCornerCut;

	/// <summary>
	/// The cube as the solid inside 26 planes — 6 faces, 12 edge bevels, 8 corner cuts — each
	/// piece being the polygon one plane leaves on the solid. Built this way, rather than by
	/// listing corners by hand, because the edge and corner cuts are different depths: the corner
	/// cut trims the ends off the edge bevels, and working that out by hand is where mistakes live.
	/// </summary>
	private static Piece[] BuildPieces()
	{
		// +x forward/back, +y left, +z up. Front looks at XZ from -y, Right at YZ from +x. Right
		// and Up are the way each label reads when you look straight at that face.
		var faces = new (Vector3 N, Vector3 Right, Vector3 Up, string Label)[]
		{
			(new Vector3( 0, 0, 1 ), new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ), "TOP"),
			(new Vector3( 0, 0, -1 ), new Vector3( 1, 0, 0 ), new Vector3( 0, -1, 0 ), "BOTTOM"),
			(new Vector3( 0, -1, 0 ), new Vector3( 1, 0, 0 ), new Vector3( 0, 0, 1 ), "FRONT"),
			(new Vector3( 0, 1, 0 ), new Vector3( -1, 0, 0 ), new Vector3( 0, 0, 1 ), "BACK"),
			(new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ), new Vector3( 0, 0, 1 ), "RIGHT"),
			(new Vector3( -1, 0, 0 ), new Vector3( 0, -1, 0 ), new Vector3( 0, 0, 1 ), "LEFT"),
		};

		// Each plane as (direction, offset): the solid is every point with dot(direction, p) <= offset.
		// An edge plane passes through the points EdgeCut back from the edge on both faces; a
		// corner plane through the points CornerCut back from the corner along its three edges.
		var planes = new List<(Vector3 Dir, float Offset, int Face)>();

		for ( var f = 0; f < faces.Length; f++ )
			planes.Add( (faces[f].N, 1f, f) );

		var axes = new[] { new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ), new Vector3( 0, 0, 1 ) };

		for ( var i = 0; i < 3; i++ )
		{
			for ( var j = i + 1; j < 3; j++ )
			{
				foreach ( var si in new[] { 1f, -1f } )
				{
					foreach ( var sj in new[] { 1f, -1f } )
						planes.Add( (axes[i] * si + axes[j] * sj, 2f - EdgeCut, -1) );
				}
			}
		}

		foreach ( var sx in new[] { 1f, -1f } )
		{
			foreach ( var sy in new[] { 1f, -1f } )
			{
				foreach ( var sz in new[] { 1f, -1f } )
					planes.Add( (new Vector3( sx, sy, sz ), 3f - CornerCut, -1) );
			}
		}

		// Every vertex of the solid is where three planes meet and inside all the others.
		var vertices = new List<Vector3>();

		for ( var a = 0; a < planes.Count; a++ )
		{
			for ( var b = a + 1; b < planes.Count; b++ )
			{
				for ( var c = b + 1; c < planes.Count; c++ )
				{
					if ( !Meet( planes[a], planes[b], planes[c], out var v ) )
						continue;

					var inside = true;

					foreach ( var (dir, offset, _) in planes )
					{
						if ( Vector3.Dot( dir, v ) > offset + 1e-4f )
						{
							inside = false;
							break;
						}
					}

					if ( inside && !vertices.Exists( w => w.Distance( v ) < 1e-4f ) )
						vertices.Add( v );
				}
			}
		}

		var pieces = new List<Piece>();

		foreach ( var (dir, offset, face) in planes )
		{
			var on = vertices.FindAll( v => MathF.Abs( Vector3.Dot( dir, v ) - offset ) < 1e-4f );

			if ( on.Count < 3 )
				continue;

			// Round the plane in angle order about the polygon's middle.
			var middle = new Vector3( 0, 0, 0 );

			foreach ( var v in on )
				middle = middle + v;

			middle = middle * (1f / on.Count);

			var n = dir.Normal;
			var u = (on[0] - middle).Normal;
			var w = Vector3.Cross( n, u );
			on.Sort( ( p, q ) => MathF.Atan2( Vector3.Dot( p - middle, w ), Vector3.Dot( p - middle, u ) )
				.CompareTo( MathF.Atan2( Vector3.Dot( q - middle, w ), Vector3.Dot( q - middle, u ) ) ) );

			pieces.Add( face >= 0
				? new Piece( dir, on.ToArray(), faces[face].Label, faces[face].Right, faces[face].Up, true )
				: new Piece( dir, on.ToArray(), null, default, default, false ) );
		}

		return pieces.ToArray();
	}

	/// <summary>Where three planes meet, by Cramer's rule; false when two are parallel.</summary>
	private static bool Meet( (Vector3 Dir, float Offset, int Face) p, (Vector3 Dir, float Offset, int Face) q, (Vector3 Dir, float Offset, int Face) r, out Vector3 point )
	{
		point = default;

		var qr = Vector3.Cross( q.Dir, r.Dir );
		var det = Vector3.Dot( p.Dir, qr );

		if ( MathF.Abs( det ) < 1e-5f )
			return false;

		point = (qr * p.Offset + Vector3.Cross( r.Dir, p.Dir ) * q.Offset + Vector3.Cross( p.Dir, q.Dir ) * r.Offset) * (1f / det);
		return true;
	}

	/// <summary>The buttons round the cube, as offsets from its centre in logical pixels. The
	/// arrows turn the view a step the way Onshape's do; the curved pair roll it.</summary>
	public static readonly (ViewCubeButton Button, Vector2 Offset, string Icon)[] Buttons =
	{
		(ViewCubeButton.Left, new Vector2( -76f, 0f ), "chevron_left"),
		(ViewCubeButton.Right, new Vector2( 76f, 0f ), "chevron_right"),
		(ViewCubeButton.Up, new Vector2( 0f, -76f ), "expand_less"),
		(ViewCubeButton.Down, new Vector2( 0f, 76f ), "expand_more"),
		(ViewCubeButton.RollLeft, new Vector2( -62f, -62f ), "rotate_left"),
		(ViewCubeButton.RollRight, new Vector2( 62f, -62f ), "rotate_right"),
		(ViewCubeButton.Home, new Vector2( -62f, 62f ), "home"),
	};

	// --- geometry -----------------------------------------------------------------------------

	public static Vector2 Project( Vector3 p, Rotation r, Vector2 centre, float dpi ) =>
		centre + new Vector2( Vector3.Dot( p, r.Right ), -Vector3.Dot( p, r.Up ) ) * (HalfPixels * dpi);

	/// <summary>How square a piece is to the camera, 0 edge-on to 1 face-on; negative faces away.</summary>
	public static float Facing( Piece piece, Rotation r ) => -Vector3.Dot( piece.Direction.Normal, r.Forward );

	public static Vector2[] Outline( Piece piece, Rotation r, Vector2 centre, float dpi )
	{
		var points = new Vector2[piece.Outline.Length];

		for ( var i = 0; i < points.Length; i++ )
			points[i] = Project( piece.Outline[i], r, centre, dpi );

		return points;
	}

	/// <summary>The view direction under a render pixel, or null: the piece facing you whose
	/// outline holds the point.</summary>
	public static Vector3? DirectionAt( Vector2 p, Rotation r, Vector2 centre, float dpi )
	{
		foreach ( var piece in Pieces )
		{
			if ( Facing( piece, r ) < 0.02f )
				continue;

			if ( InsideConvex( Outline( piece, r, centre, dpi ), p ) )
				return piece.Direction;
		}

		return null;
	}

	/// <summary>Whether a render pixel is over the cube itself — anywhere within its outline from
	/// this angle, which a sphere round it approximates closely enough for hover.</summary>
	public static bool OverBody( Vector2 p, Vector2 centre, float dpi ) =>
		(p - centre).Length <= HalfPixels * dpi * 1.8f;

	public static Rect ButtonRect( Vector2 offset, Vector2 centre, float dpi )
	{
		var c = centre + offset * dpi;
		var half = ButtonPixels * 0.5f * dpi;
		return new Rect( c.x - half, c.y - half, half * 2f, half * 2f );
	}

	public static ViewCubeButton ButtonAt( Vector2 p, Vector2 centre, float dpi )
	{
		foreach ( var (button, offset, _) in Buttons )
		{
			if ( ButtonRect( offset, centre, dpi ).IsInside( p ) )
				return button;
		}

		return ViewCubeButton.None;
	}

	/// <summary>Inside a convex polygon, whichever way round it winds: on the same side of every edge.</summary>
	public static bool InsideConvex( Vector2[] poly, Vector2 p )
	{
		var sign = 0;

		for ( var i = 0; i < poly.Length; i++ )
		{
			var a = poly[i];
			var b = poly[(i + 1) % poly.Length];
			var cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

			if ( MathF.Abs( cross ) < 1e-6f )
				continue;

			var side = cross > 0f ? 1 : -1;

			if ( sign == 0 )
				sign = side;
			else if ( side != sign )
				return false;
		}

		return true;
	}

	// --- drawing ------------------------------------------------------------------------------

	/// <summary>Everything the cube needs to draw one frame.</summary>
	public readonly record struct State( Rotation Camera, Vector2 Centre, float Dpi, Vector3? Hover, ViewCubeButton HoverButton, bool Hovered, Color Accent );

	public static void Draw( IOverlayCanvas draw, State state )
	{
		var r = state.Camera;
		var s = state.Dpi;
		var centre = state.Centre;

		if ( state.Hovered )
			draw.Circle( centre, 92f * s, Color.Black.WithAlpha( 0.06f ) );

		// Lit from up and to the left of the camera, so the light turns with the view the way a
		// studio light over your shoulder would: the top always reads as the top, the faces on
		// either side get different shades, and each bevel catches its own.
		var light = (r.Up * 0.75f - r.Right * 0.35f - r.Forward * 0.55f).Normal;
		var surface = new Color( 0.88f, 0.90f, 0.93f );

		foreach ( var piece in Pieces )
		{
			var facing = Facing( piece, r );

			if ( facing <= 0.001f )
				continue;

			// Ambient plus diffuse, never below mid-grey: dark enough to model the shape, light
			// enough that a face turned away from the light still reads against the viewport.
			var lit = 0.58f + 0.42f * MathF.Max( 0f, Vector3.Dot( piece.Direction.Normal, light ) );
			var shade = new Color( surface.r * lit, surface.g * lit, surface.b * lit );

			if ( state.Hover is { } hover && piece.Direction.Distance( hover ) < 1e-3f )
				shade = Color.Lerp( shade, state.Accent, 0.8f );

			var points = Outline( piece, r, centre, s );
			draw.Polygon( points, shade );

			// Each piece outlined in a darker version of its own shade: a crease where two pieces
			// meet, never a light or dark line pasted over the top.
			var outline = Color.Lerp( shade, Color.Black, 0.22f );

			for ( var i = 0; i < points.Length; i++ )
				draw.Line( points[i], points[(i + 1) % points.Length], 1f * s, outline );

			// Faded out as the face turns edge-on, where the strokes crowd into a smear.
			if ( piece.IsFace && facing >= 0.2f )
				FaceLabel( draw, piece, state, new Color( 0.10f, 0.11f, 0.14f, Math.Clamp( (facing - 0.2f) * 2f, 0f, 1f ) ) );
		}

		Axes( draw, state );

		foreach ( var (button, offset, icon) in Buttons )
		{
			var rect = ButtonRect( offset, centre, s );
			var hovered = state.HoverButton == button;

			if ( hovered )
				draw.Circle( rect.Center, rect.Width * 0.5f, state.Accent );

			draw.Icon( icon, rect, 22f * s, Color.White.WithAlpha( hovered ? 1f : state.Hovered ? 0.8f : 0.45f ) );
		}
	}

	/// <summary>
	/// A small X/Y/Z triad off the cube's bottom-right, in the scene view's own axis colours,
	/// turning with the camera. Kept apart from the cube rather than hung off a corner: stuck to
	/// the cube, whichever axis points away from you vanishes behind it.
	/// </summary>
	private static void Axes( IOverlayCanvas draw, State state )
	{
		var r = state.Camera;
		var s = state.Dpi;
		var origin = state.Centre + new Vector2( 62f, 62f ) * s;
		var length = 18f * s;
		var axes = new (Vector3 Dir, Color Color, string Name)[]
		{
			(new Vector3( 1, 0, 0 ), Color.FromBytes( 226, 84, 84 ), "X"),
			(new Vector3( 0, 1, 0 ), Color.FromBytes( 130, 200, 80 ), "Y"),
			(new Vector3( 0, 0, 1 ), Color.FromBytes( 74, 140, 226 ), "Z"),
		};

		// Back to front, so the axis pointing at you is drawn last.
		Array.Sort( axes, ( a, b ) => Vector3.Dot( b.Dir, r.Forward ).CompareTo( Vector3.Dot( a.Dir, r.Forward ) ) );

		foreach ( var (dir, color, name) in axes )
		{
			var screen = new Vector2( Vector3.Dot( dir, r.Right ), -Vector3.Dot( dir, r.Up ) );

			// Pointing straight at or away from you: a dot, so the axis is still there and named.
			if ( screen.Length < 0.15f )
			{
				draw.Circle( origin, 3f * s, color );
				draw.Text( name, origin + new Vector2( 8f, -8f ) * s, 10f * s, color );
				continue;
			}

			var tip = origin + screen * length;
			draw.Line( origin, tip, 2f * s, color );
			draw.Text( name, origin + screen * (length + 8f * s), 10f * s, color );
		}
	}

	/// <summary>
	/// A face's name, lettered ON the face: each letter is a few strokes laid out in the face's own
	/// plane and projected with the cube, so it tilts and foreshortens with the face like print on
	/// a box. The overlay's text is always upright and flat to the screen.
	/// </summary>
	private static void FaceLabel( IOverlayCanvas draw, Piece piece, State state, Color color )
	{
		const float spacing = 0.28f;

		var width = 0f;

		foreach ( var ch in piece.Label )
			width += LetterWidth( ch ) + spacing;

		width -= spacing;

		// Letter height in face units (the face is 2 across), shrunk so the longest word fits.
		var height = MathF.Min( 0.4f, 1.5f * (1f - EdgeCut) / width );
		var x = -width * height * 0.5f;
		var y = -height * 0.5f;

		// A stroke a tenth of the letter's height, or the letters fill in and turn to blobs.
		var thickness = MathF.Max( 1f, height * HalfPixels * state.Dpi * 0.11f );

		foreach ( var ch in piece.Label )
		{
			if ( StrokeFont.TryGetValue( ch, out var strokes ) )
			{
				foreach ( var stroke in strokes )
				{
					for ( var i = 0; i + 1 < stroke.Length; i++ )
					{
						var a = piece.Direction + piece.TextRight * (x + stroke[i].x * height) + piece.TextUp * (y + stroke[i].y * height);
						var b = piece.Direction + piece.TextRight * (x + stroke[i + 1].x * height) + piece.TextUp * (y + stroke[i + 1].y * height);
						draw.Line( Project( a, state.Camera, state.Centre, state.Dpi ), Project( b, state.Camera, state.Centre, state.Dpi ), thickness, color );
					}
				}
			}

			x += (LetterWidth( ch ) + spacing) * height;
		}
	}

	private static float LetterWidth( char ch ) => ch switch { 'M' => 0.7f, 'I' => 0.3f, _ => 0.6f };

	/// <summary>Just the capitals the six labels use, as polylines in a box one unit tall.</summary>
	private static readonly Dictionary<char, Vector2[][]> StrokeFont = new()
	{
		['T'] = new[] { L( 0, 1, 0.6f, 1 ), L( 0.3f, 1, 0.3f, 0 ) },
		['O'] = new[] { L( 0.1f, 0, 0.5f, 0, 0.6f, 0.15f, 0.6f, 0.85f, 0.5f, 1, 0.1f, 1, 0, 0.85f, 0, 0.15f, 0.1f, 0 ) },
		['P'] = new[] { L( 0, 0, 0, 1, 0.45f, 1, 0.6f, 0.86f, 0.6f, 0.64f, 0.45f, 0.5f, 0, 0.5f ) },
		['B'] = new[] { L( 0, 0, 0, 1, 0.45f, 1, 0.57f, 0.88f, 0.57f, 0.62f, 0.45f, 0.52f, 0, 0.52f ), L( 0.45f, 0.52f, 0.6f, 0.4f, 0.6f, 0.12f, 0.47f, 0, 0, 0 ) },
		['M'] = new[] { L( 0, 0, 0, 1, 0.35f, 0.45f, 0.7f, 1, 0.7f, 0 ) },
		['F'] = new[] { L( 0.6f, 1, 0, 1, 0, 0 ), L( 0, 0.52f, 0.45f, 0.52f ) },
		['R'] = new[] { L( 0, 0, 0, 1, 0.45f, 1, 0.6f, 0.86f, 0.6f, 0.64f, 0.45f, 0.5f, 0, 0.5f ), L( 0.3f, 0.5f, 0.6f, 0 ) },
		['N'] = new[] { L( 0, 0, 0, 1, 0.6f, 0, 0.6f, 1 ) },
		['A'] = new[] { L( 0, 0, 0.3f, 1, 0.6f, 0 ), L( 0.12f, 0.4f, 0.48f, 0.4f ) },
		['C'] = new[] { L( 0.6f, 0.85f, 0.5f, 1, 0.1f, 1, 0, 0.85f, 0, 0.15f, 0.1f, 0, 0.5f, 0, 0.6f, 0.15f ) },
		['K'] = new[] { L( 0, 0, 0, 1 ), L( 0.6f, 1, 0, 0.4f ), L( 0.2f, 0.6f, 0.6f, 0 ) },
		['I'] = new[] { L( 0, 1, 0.3f, 1 ), L( 0.15f, 1, 0.15f, 0 ), L( 0, 0, 0.3f, 0 ) },
		['G'] = new[] { L( 0.6f, 0.85f, 0.5f, 1, 0.1f, 1, 0, 0.85f, 0, 0.15f, 0.1f, 0, 0.5f, 0, 0.6f, 0.15f, 0.6f, 0.45f, 0.35f, 0.45f ) },
		['H'] = new[] { L( 0, 0, 0, 1 ), L( 0.6f, 0, 0.6f, 1 ), L( 0, 0.5f, 0.6f, 0.5f ) },
		['L'] = new[] { L( 0, 1, 0, 0, 0.55f, 0 ) },
		['E'] = new[] { L( 0.6f, 1, 0, 1, 0, 0, 0.6f, 0 ), L( 0, 0.52f, 0.45f, 0.52f ) },
	};

	private static Vector2[] L( params float[] xy )
	{
		var points = new Vector2[xy.Length / 2];

		for ( var i = 0; i < points.Length; i++ )
			points[i] = new Vector2( xy[i * 2], xy[i * 2 + 1] );

		return points;
	}
}
