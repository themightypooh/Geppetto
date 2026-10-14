using System;
using System.Collections.Generic;

namespace Effigy.Render;

/// <summary>A pinhole camera: where it is, where it looks, and how wide.</summary>
public sealed class EyeCamera
{
	public Vec3 Position;
	public Vec3 Forward = new( 1, 0, 0 );
	public Vec3 Up = new( 0, 0, 1 );

	/// <summary>Horizontal field of view in degrees, the way s&box's camera measures it.</summary>
	public float FieldOfView = 80f;

	/// <summary>Nothing nearer than this is drawn; geometry crossing it is cut, not dropped.</summary>
	public float Near = 0.5f;

	public static EyeCamera LookAt( Vec3 eye, Vec3 target, float fieldOfView = 80f ) =>
		new() { Position = eye, Forward = (target - eye).Normal, FieldOfView = fieldOfView };

	/// <summary>A camera sitting on a transform: s&box's convention, x forward, z up.</summary>
	public static EyeCamera At( Xform transform, float fieldOfView = 80f ) =>
		new() { Position = transform.Origin, Forward = transform.X.Normal, Up = transform.Z.Normal, FieldOfView = fieldOfView };
}

/// <summary>Triangles to draw in one colour: a mesh part, a prop, a posed glove.</summary>
public readonly record struct EyeItem( IReadOnlyList<Vec3> Positions, IReadOnlyList<int> Triangles, int Colour );

/// <summary>
/// A perspective render from a camera: what the player would see.
///
/// <see cref="ModelRender"/> is orthographic on purpose - a drawing you can measure off. This is
/// the other question, the one a first-person take is judged on: does the hand read from the eye,
/// is the switch framed, does a sleeve swallow the screen. It answers that without a game running,
/// so review stills can come out of the same headless run as the clash report.
///
/// Flat-shaded per triangle in each item's colour, lit from the camera with a key above it, and
/// depth-tested with 1/z so overlapping parts sort correctly at any distance. Back faces draw
/// darker rather than not at all: a sleeve seen from inside is something to see, not hide.
/// </summary>
public static class EyeRender
{
	const int Background = 0x121418;

	/// <summary>Distinct colours for parts by index, the same palette the orthographic views use
	/// first, so a part keeps its colour between the two.</summary>
	public static int PartColour( int index ) => Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];

	static readonly int[] Palette =
	{
		0x8fa3b8, 0xd4a35c, 0x7bbf8f, 0xc77b7b, 0x9a8fd0, 0x67b8c6, 0xc9a2d6, 0xb5b56a,
	};

	public static Raster Render( IEnumerable<EyeItem> items, EyeCamera camera, int width, int height, int background = Background )
	{
		var raster = new Raster( width, height, background );
		var forward = camera.Forward.Normal;
		var right = Vec3.Cross( forward, camera.Up ).Normal;

		if ( right.LengthSquared < 0.5f )
			right = Vec3.Cross( forward, MathF.Abs( forward.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ) ).Normal;

		var up = Vec3.Cross( right, forward ).Normal;
		var focal = width * 0.5f / MathF.Tan( Math.Clamp( camera.FieldOfView, 1f, 179f ) * MathF.PI / 360f );
		var near = MathF.Max( camera.Near, 1e-4f );

		// A key from above the camera's right shoulder, plus the camera itself as a headlight, so
		// a surface facing the eye is never black.
		var key = (up * 0.7f + right * 0.3f - forward * 0.6f).Normal;

		Vec3 View( Vec3 p )
		{
			var d = p - camera.Position;
			return new Vec3( Vec3.Dot( d, right ), Vec3.Dot( d, up ), Vec3.Dot( d, forward ) );
		}

		Vec2 Screen( Vec3 v ) => new( width * 0.5f + v.x / v.z * focal, height * 0.5f - v.y / v.z * focal );

		Span<Vec3> poly = stackalloc Vec3[4];

		foreach ( var item in items )
		{
			if ( item.Positions is null || item.Triangles is null )
				continue;

			var view = new Vec3[item.Positions.Count];
			for ( var i = 0; i < view.Length; i++ )
				view[i] = View( item.Positions[i] );

			for ( var t = 0; t + 2 < item.Triangles.Count; t += 3 )
			{
				var a = view[item.Triangles[t]];
				var b = view[item.Triangles[t + 1]];
				var c = view[item.Triangles[t + 2]];

				if ( a.z < near && b.z < near && c.z < near )
					continue;

				// Shade in view space: the normal against the direction to the eye.
				var n = Vec3.Cross( b - a, c - a ).Normal;
				var toEye = (-(a + b + c) / 3f).Normal;
				var facing = Vec3.Dot( n, toEye ) > 0f;
				var ns = facing ? n : -n;
				var keyView = new Vec3( Vec3.Dot( key, right ), Vec3.Dot( key, up ), Vec3.Dot( key, forward ) );
				var shade = 0.22f + 0.5f * MathF.Max( 0f, Vec3.Dot( ns, toEye ) ) + 0.35f * MathF.Max( 0f, Vec3.Dot( ns, keyView ) );
				shade = MathF.Min( shade, 1.05f ) * (facing ? 1f : 0.45f);

				var colour = Raster.Rgb(
					(int)(((item.Colour >> 16) & 0xFF) * shade),
					(int)(((item.Colour >> 8) & 0xFF) * shade),
					(int)((item.Colour & 0xFF) * shade) );

				// Cut against the near plane, which leaves a triangle or a quad.
				var count = ClipNear( a, b, c, near, poly );

				for ( var k = 1; k + 1 < count; k++ )
				{
					var p0 = poly[0];
					var p1 = poly[k];
					var p2 = poly[k + 1];
					raster.Triangle( Screen( p0 ), Screen( p1 ), Screen( p2 ), -1f / p0.z, -1f / p1.z, -1f / p2.z, colour );
				}
			}
		}

		return raster;
	}

	/// <summary>Sutherland-Hodgman against z = near, for one triangle.</summary>
	static int ClipNear( Vec3 a, Vec3 b, Vec3 c, float near, Span<Vec3> output )
	{
		Span<Vec3> input = stackalloc Vec3[] { a, b, c };
		var count = 0;

		for ( var i = 0; i < 3; i++ )
		{
			var p = input[i];
			var q = input[(i + 1) % 3];
			var pIn = p.z >= near;
			var qIn = q.z >= near;

			if ( pIn )
				output[count++] = p;

			if ( pIn != qIn )
				output[count++] = Vec3.Lerp( p, q, (near - p.z) / (q.z - p.z) );
		}

		return count;
	}

	/// <summary>
	/// Images in a grid, each with an optional label in its corner: a take's frames side by side.
	/// Tiles may differ in size; each cell is as big as the largest.
	/// </summary>
	public static Raster Sheet( IReadOnlyList<Raster> tiles, int columns, IReadOnlyList<string> labels = null, int background = Background )
	{
		if ( tiles is null || tiles.Count == 0 )
			return new Raster( 1, 1, background );

		columns = Math.Clamp( columns, 1, tiles.Count );
		var rows = (tiles.Count + columns - 1) / columns;
		var cellW = 0;
		var cellH = 0;

		foreach ( var t in tiles )
		{
			cellW = Math.Max( cellW, t.Width );
			cellH = Math.Max( cellH, t.Height );
		}

		const int gap = 2;
		var sheet = new Raster( columns * cellW + (columns - 1) * gap, rows * cellH + (rows - 1) * gap, 0x2a2e35 );

		for ( var i = 0; i < tiles.Count; i++ )
		{
			var tile = tiles[i];
			var ox = (i % columns) * (cellW + gap);
			var oy = (i / columns) * (cellH + gap);

			sheet.Rect( ox, oy, cellW, cellH, background );

			for ( var y = 0; y < tile.Height; y++ )
				Array.Copy( tile.Pixels, y * tile.Width, sheet.Pixels, (oy + y) * sheet.Width + ox, tile.Width );

			if ( labels is not null && i < labels.Count && !string.IsNullOrEmpty( labels[i] ) )
			{
				sheet.Rect( ox, oy, Raster.TextWidth( labels[i], 1 ) + 6, 11, 0x000000, 0.55f );
				sheet.Text( ox + 3, oy + 2, labels[i], 0xe6e9ee, 1 );
			}
		}

		return sheet;
	}
}
