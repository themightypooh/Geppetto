using Editor;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>Which drawing a feature-tool button paints. One per creation tool.</summary>
internal enum EffigyIcon
{
	Sketch,
	Primitive,
	Extrude,
	Revolve,
	Bevel,
	Shell,
	Subdivide,
	Mirror,
	LinearPattern,
	CircularPattern,
	Transform,
	UVProject,
	FaceMaterial,

	// --- sketch tools -------------------------------------------------------------------------
	// These were Material Icon NAMES until now, and generic ones: show_chart (a zigzag line chart)
	// for Line, cached (two refresh arrows) for Arc, crop_square for Rectangle. They said nothing
	// about the operation, and half of them said something actively misleading.
	SelectTool,
	LineTool,
	RectangleTool,
	RectangleCentreTool,
	CircleTool,
	CircleThreePointTool,
	ArcTool,
	ArcThreePointTool,
	PolygonTool,
	PolygonCircumscribedTool,
	SlotTool,
	PointTool,
	ConstructionTool,
	ProfileInspectorTool,
	FinishSketchTool,
}

/// <summary>
/// Painted icons for the feature-creation strip, drawn rather than looked up in a font.
///
/// Same reasoning as RigIconButton (see Editor/RigControlEditor): s&box ships CLASSIC Material
/// Icons, not the newer Material Symbols, so a name from the Symbols set silently renders as
/// nothing — and the strip was leaning on generic names like "square", "flip" and "call_made"
/// that, where they resolved at all, said nothing about the CAD operation behind them. A drawn
/// glyph can show the actual operation: Bevel cuts a corner off a square, Shell puts a wall
/// inside one, Mirror reflects a solid shape into an outlined one.
///
/// Every icon is drawn around <c>center</c> inside a nominal 18x18 box, so they all read at the
/// same weight, then scaled up as one by the <c>scale</c> argument to fit the button drawing it.
/// </summary>
internal static class EffigyIcons
{
	/// <summary>Stroke width every outline uses, so no icon looks heavier than its neighbours.</summary>
	private const float Stroke = 1.6f;

	// --- the pencil's own colours -------------------------------------------------------------
	//
	// The ONLY icon that does not draw entirely in the colour it is handed. Sketch is the tool
	// every part starts with and the only button in the strip carrying a text label, so it is the
	// one worth making findable at a glance rather than another grey glyph in a row of grey
	// glyphs. A yellow #2 is about as legible as a small object gets.
	//
	// Chosen against a dark viewport: the graphite is a mid grey rather than near-black, because a
	// true graphite point disappears into the background exactly where the icon needs to read.

	private static readonly Color PencilBody = new( 0.96f, 0.76f, 0.15f );
	private static readonly Color PencilFerrule = new( 0.74f, 0.77f, 0.80f );
	private static readonly Color PencilEraser = new( 0.91f, 0.56f, 0.58f );
	private static readonly Color PencilWood = new( 0.87f, 0.68f, 0.44f );
	private static readonly Color PencilGraphite = new( 0.45f, 0.47f, 0.50f );

	/// <summary>Multiplier applied to every coordinate, radius and pen width for the icon being
	/// drawn right now. Every glyph is authored against the nominal 18x18 box, so one factor set
	/// here at the top of Draw is enough to resize all of them together — the strip's buttons grew
	/// past the size the glyphs were drawn for and a fixed-size glyph in a big button reads as a
	/// mistake. Painting only ever happens on the editor UI thread, so a plain static is safe.</summary>
	private static float _scale = 1f;

	public static void Draw( EffigyIcon icon, Vector2 center, Color color, float scale = 1f )
	{
		Editor.Paint.Antialiasing = true;
		_scale = scale;

		switch ( icon )
		{
			case EffigyIcon.Sketch: PaintSketch( center, color ); return;
			case EffigyIcon.Primitive: PaintPrimitive( center, color ); return;
			case EffigyIcon.Extrude: PaintExtrude( center, color ); return;
			case EffigyIcon.Revolve: PaintRevolve( center, color ); return;
			case EffigyIcon.Bevel: PaintBevel( center, color ); return;
			case EffigyIcon.Shell: PaintShell( center, color ); return;
			case EffigyIcon.Subdivide: PaintSubdivide( center, color ); return;
			case EffigyIcon.Mirror: PaintMirror( center, color ); return;
			case EffigyIcon.LinearPattern: PaintLinearPattern( center, color ); return;
			case EffigyIcon.CircularPattern: PaintCircularPattern( center, color ); return;
			case EffigyIcon.Transform: PaintTransform( center, color ); return;
			case EffigyIcon.UVProject: PaintUVProject( center, color ); return;
			case EffigyIcon.FaceMaterial: PaintFaceMaterial( center, color ); return;

			case EffigyIcon.SelectTool: PaintSelectTool( center, color ); return;
			case EffigyIcon.LineTool: PaintLineTool( center, color ); return;
			case EffigyIcon.RectangleTool: PaintRectangleTool( center, color ); return;
			case EffigyIcon.RectangleCentreTool: PaintRectangleCentreTool( center, color ); return;
			case EffigyIcon.CircleTool: PaintCircleTool( center, color ); return;
			case EffigyIcon.CircleThreePointTool: PaintCircleThreePointTool( center, color ); return;
			case EffigyIcon.ArcTool: PaintArcTool( center, color ); return;
			case EffigyIcon.ArcThreePointTool: PaintArcThreePointTool( center, color ); return;
			case EffigyIcon.PolygonTool: PaintPolygonTool( center, color ); return;
			case EffigyIcon.PolygonCircumscribedTool: PaintPolygonCircumscribedTool( center, color ); return;
			case EffigyIcon.SlotTool: PaintSlotTool( center, color ); return;
			case EffigyIcon.PointTool: PaintPointTool( center, color ); return;
			case EffigyIcon.ConstructionTool: PaintConstructionTool( center, color ); return;
			case EffigyIcon.ProfileInspectorTool: PaintProfileInspectorTool( center, color ); return;
			case EffigyIcon.FinishSketchTool: PaintFinishSketchTool( center, color ); return;
		}
	}

	// --- drawing helpers --------------------------------------------------------------------

	private static void Stroked( Color color, float width = Stroke )
	{
		Editor.Paint.ClearBrush();
		Editor.Paint.SetPen( color, width * _scale );
	}

	private static void Filled( Color color )
	{
		Editor.Paint.ClearPen();
		Editor.Paint.SetBrush( color );
	}

	/// <summary>Closed outline through the given points — DrawPolygon fills, so an outlined shape
	/// has to be walked as lines.</summary>
	private static void Outline( params Vector2[] points )
	{
		for ( var i = 0; i < points.Length; i++ )
			Editor.Paint.DrawLine( points[i], points[(i + 1) % points.Length] );
	}

	/// <summary>An arc as a polyline. There is no arc primitive in Paint, and approximating with
	/// segments is exact enough at icon size.</summary>
	private static void Arc( Vector2 center, float radius, float fromDegrees, float toDegrees, int segments = 14 )
	{
		var previous = Vector2.Zero;

		for ( var i = 0; i <= segments; i++ )
		{
			var t = fromDegrees + (toDegrees - fromDegrees) * (i / (float)segments);
			var radians = t * MathF.PI / 180f;
			var point = center + new Vector2( MathF.Cos( radians ) * radius, MathF.Sin( radians ) * radius ) * _scale;

			if ( i > 0 )
				Editor.Paint.DrawLine( previous, point );

			previous = point;
		}
	}

	/// <summary>A solid triangular arrow head, pointing along <paramref name="direction"/>.</summary>
	private static void ArrowHead( Vector2 tip, Vector2 direction, Color color, float size = 3.4f )
	{
		var d = direction.Normal;
		var side = new Vector2( -d.y, d.x );

		size *= _scale;

		Filled( color );
		Editor.Paint.DrawPolygon(
			tip,
			tip - d * size + side * size * 0.62f,
			tip - d * size - side * size * 0.62f );
	}

	private static Vector2 At( Vector2 center, float x, float y ) => center + new Vector2( x, y ) * _scale;

	/// <summary>A rect in the same nominal icon space At() uses, for the glyphs that need
	/// DrawRect/DrawCircle rather than a walked outline.</summary>
	private static Rect Box( Vector2 center, float x, float y, float width, float height )
		=> new Rect( center.x + x * _scale, center.y + y * _scale, width * _scale, height * _scale );

	// --- the icons --------------------------------------------------------------------------

	/// <summary>
	/// A pencil drawing on a sheet, its point resting ON the paper's top edge.
	///
	/// The pencil used to be a plain parallelogram - blunt at both ends, with one of its corners
	/// landing on the paper line. A pencil reads as a pencil because of the cone at the end, and
	/// the mark reads as DRAWING because that cone touches the paper rather than hovering above
	/// it or crossing through it. So: a solid tapered point that ends exactly on the line, an
	/// outlined barrel behind it, and a band where the ferrule would be.
	///
	/// Every coordinate is derived from the tip and the pencil's axis, laid out along a 45 degree
	/// diagonal, so the point cannot drift off the paper if the proportions are adjusted.
	///
	/// The paper keeps the colour it is handed; the pencil does not (see PencilBody and friends).
	/// </summary>
	private static void PaintSketch( Vector2 c, Color color )
	{
		// Paper: a single flat horizontal line, matching the compact reference glyph. Everything
		// else is placed against PaperY.
		const float PaperY = 4.8f;

		// How thick the pencil is, across the barrel.
		const float BarrelWidth = 1.7f;

		Stroked( color, Stroke );
		Editor.Paint.DrawLine( At( c, -6.6f, PaperY ), At( c, 6.4f, PaperY ) );

		// The sharpened cone, sitting on the paper. Solid, because at 27px a hollow cone is a
		// smudge - the filled wedge is what makes it read as sharpened. Its base is exactly as
		// wide as the barrel's stroke, so the two meet without a step.
		Filled( PencilWood );
		Editor.Paint.DrawPolygon(
			At( c, -4.4f, PaperY ),
			At( c, -1.678f, 3.28f ),
			At( c, -2.88f, 2.078f ) );

		// The exposed lead, the outer 40% of that cone. Drawn over the wood rather than beside it,
		// so the two always agree about where the point is.
		Filled( PencilGraphite );
		Editor.Paint.DrawPolygon(
			At( c, -4.4f, PaperY ),
			At( c, -3.257f, 4.161f ),
			At( c, -3.761f, 3.657f ) );

		// Barrel: ONE STROKED LINE, not an outlined shape. A pencil this slim has a body 1.7 units
		// across, and two outline strokes inside that merge into a blob - the line IS the barrel,
		// and it is the only way to get a thin pencil that still reads at icon size. The ferrule
		// and eraser are further stretches of the same line, which is also why they cannot drift
		// out of alignment with it.
		Stroked( PencilBody, BarrelWidth );
		Editor.Paint.DrawLine( At( c, -2.279f, 2.679f ), At( c, 4.156f, -3.756f ) );

		Stroked( PencilFerrule, BarrelWidth );
		Editor.Paint.DrawLine( At( c, 4.156f, -3.756f ), At( c, 4.948f, -4.548f ) );

		Stroked( PencilEraser, BarrelWidth );
		Editor.Paint.DrawLine( At( c, 4.948f, -4.548f ), At( c, 5.853f, -5.453f ) );
	}

	/// <summary>An isometric cube — the generic "a solid body" mark.</summary>
	private static void PaintPrimitive( Vector2 c, Color color )
	{
		Stroked( color );
		Outline(
			At( c, 0, -7.5f ), At( c, 7, -3.6f ), At( c, 7, 3.6f ),
			At( c, 0, 7.5f ), At( c, -7, 3.6f ), At( c, -7, -3.6f ) );

		// The three edges meeting at the near corner are what make it read as a cube rather than
		// a hexagon.
		Editor.Paint.DrawLine( At( c, 0, 0 ), At( c, 0, 7.5f ) );
		Editor.Paint.DrawLine( At( c, 0, 0 ), At( c, 7, -3.6f ) );
		Editor.Paint.DrawLine( At( c, 0, 0 ), At( c, -7, -3.6f ) );
	}

	/// <summary>A flat profile with an arrow pulling it upward into a solid.</summary>
	private static void PaintExtrude( Vector2 c, Color color )
	{
		Stroked( color );
		Outline( At( c, 0, 2.4f ), At( c, 7, 5.6f ), At( c, 0, 8.8f ), At( c, -7, 5.6f ) );

		Editor.Paint.DrawLine( At( c, 0, 1.8f ), At( c, 0, -5f ) );
		ArrowHead( At( c, 0, -8f ), new Vector2( 0, -1 ), color );
	}

	/// <summary>A profile swept around an axis: the axis line, the swept arc, and its direction.</summary>
	private static void PaintRevolve( Vector2 c, Color color )
	{
		// Axis.
		Stroked( color.WithAlpha( 0.55f ) );
		Editor.Paint.DrawLine( At( c, -7.5f, -8f ), At( c, -7.5f, 8f ) );

		// Profile standing off the axis.
		Stroked( color );
		Outline( At( c, -3.6f, -5f ), At( c, 0.4f, -5f ), At( c, 0.4f, 5f ), At( c, -3.6f, 5f ) );

		// The sweep, coming round the back of the axis.
		Arc( At( c, -1.6f, 0 ), 7.4f, -70f, 70f );
		ArrowHead( At( c, 0.9f, 7f ), new Vector2( -0.55f, 0.83f ), color );
	}

	/// <summary>A square with one corner chamfered away, the cut edge drawn heaviest.</summary>
	private static void PaintBevel( Vector2 c, Color color )
	{
		Stroked( color );
		Outline(
			At( c, -7, -7 ), At( c, 1.6f, -7 ), At( c, 7, -1.6f ),
			At( c, 7, 7 ), At( c, -7, 7 ) );

		// The chamfer itself, redrawn thicker — it is the whole operation.
		Stroked( color, Stroke + 1f );
		Editor.Paint.DrawLine( At( c, 1.6f, -7 ), At( c, 7, -1.6f ) );

		// Ghost of the corner that was removed.
		Stroked( color.WithAlpha( 0.35f ), 1f );
		Editor.Paint.DrawLine( At( c, 1.6f, -7 ), At( c, 7, -7 ) );
		Editor.Paint.DrawLine( At( c, 7, -7 ), At( c, 7, -1.6f ) );
	}

	/// <summary>A solid hollowed out to a wall thickness — outer box, inner void.</summary>
	private static void PaintShell( Vector2 c, Color color )
	{
		Stroked( color );
		Outline( At( c, -7, -7 ), At( c, 7, -7 ), At( c, 7, 7 ), At( c, -7, 7 ) );

		Stroked( color.WithAlpha( 0.75f ) );
		Outline( At( c, -3.4f, -3.4f ), At( c, 3.4f, -3.4f ), At( c, 3.4f, 3.4f ), At( c, -3.4f, 3.4f ) );

		// Wall hatching on the left, so the ring reads as material rather than a second box.
		Stroked( color.WithAlpha( 0.5f ), 1f );
		Editor.Paint.DrawLine( At( c, -7, -1.4f ), At( c, -3.4f, -1.4f ) );
		Editor.Paint.DrawLine( At( c, -7, 1.4f ), At( c, -3.4f, 1.4f ) );
	}

	/// <summary>A face split into quarters, corners rounding off — subdivision toward the limit
	/// surface.</summary>
	private static void PaintSubdivide( Vector2 c, Color color )
	{
		Stroked( color );
		Editor.Paint.DrawRect( Box( c, -7, -7, 14, 14 ), 4.5f * _scale );

		Stroked( color.WithAlpha( 0.7f ), 1.3f );
		Editor.Paint.DrawLine( At( c, 0, -6.4f ), At( c, 0, 6.4f ) );
		Editor.Paint.DrawLine( At( c, -6.4f, 0 ), At( c, 6.4f, 0 ) );

		Filled( color );
		Editor.Paint.DrawCircle( Box( c, -1.3f, -1.3f, 2.6f, 2.6f ) );
	}

	/// <summary>A solid shape and its reflection across a dashed mirror line.</summary>
	private static void PaintMirror( Vector2 c, Color color )
	{
		// Mirror plane, dashed.
		Stroked( color.WithAlpha( 0.5f ), 1.2f );
		for ( var y = -8f; y < 8f; y += 3.6f )
			Editor.Paint.DrawLine( At( c, 0, y ), At( c, 0, y + 2.1f ) );

		// Source: solid.
		Filled( color );
		Editor.Paint.DrawPolygon( At( c, -2.4f, -5.6f ), At( c, -8, 0 ), At( c, -2.4f, 5.6f ) );

		// Reflection: outlined, so the two are not mistaken for a pattern.
		Stroked( color );
		Outline( At( c, 2.4f, -5.6f ), At( c, 8, 0 ), At( c, 2.4f, 5.6f ) );
	}

	/// <summary>One body copied along a direction — first solid, copies outlined and fading.</summary>
	private static void PaintLinearPattern( Vector2 c, Color color )
	{
		Filled( color );
		Editor.Paint.DrawRect( Box( c, -8.4f, -3f, 6f, 6f ), 1.2f * _scale );

		Stroked( color.WithAlpha( 0.8f ) );
		Editor.Paint.DrawRect( Box( c, -1.2f, -3f, 6f, 6f ), 1.2f * _scale );

		Stroked( color.WithAlpha( 0.45f ) );
		Editor.Paint.DrawRect( Box( c, 6f, -3f, 6f, 6f ), 1.2f * _scale );
	}

	/// <summary>One body copied around an axis — copies sitting on a dashed circle.</summary>
	private static void PaintCircularPattern( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.45f ), 1.2f );
		for ( var a = 0f; a < 360f; a += 30f )
			Arc( c, 6.6f, a, a + 16f, 3 );

		// Three instances round the circle, the first solid like the linear pattern's source.
		var angles = new[] { -90f, 30f, 150f };

		for ( var i = 0; i < angles.Length; i++ )
		{
			var radians = angles[i] * MathF.PI / 180f;
			var box = Box( c,
				MathF.Cos( radians ) * 6.6f - 2.6f, MathF.Sin( radians ) * 6.6f - 2.6f,
				5.2f, 5.2f );

			if ( i == 0 )
			{
				Filled( color );
				Editor.Paint.DrawRect( box, 1f * _scale );
			}
			else
			{
				Stroked( color.WithAlpha( 0.85f ), 1.4f );
				Editor.Paint.DrawRect( box, 1f * _scale );
			}
		}
	}

	/// <summary>Move/rotate/scale — a body with four-way translation arrows through it.</summary>
	private static void PaintTransform( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.6f ) );
		Outline( At( c, -4, -4 ), At( c, 4, -4 ), At( c, 4, 4 ), At( c, -4, 4 ) );

		Stroked( color, 1.4f );
		Editor.Paint.DrawLine( At( c, 0, -5.4f ), At( c, 0, 5.4f ) );
		Editor.Paint.DrawLine( At( c, -5.4f, 0 ), At( c, 5.4f, 0 ) );

		ArrowHead( At( c, 0, -8.2f ), new Vector2( 0, -1 ), color, 3f );
		ArrowHead( At( c, 0, 8.2f ), new Vector2( 0, 1 ), color, 3f );
		ArrowHead( At( c, -8.2f, 0 ), new Vector2( -1, 0 ), color, 3f );
		ArrowHead( At( c, 8.2f, 0 ), new Vector2( 1, 0 ), color, 3f );
	}

	/// <summary>A UV grid with one texel lit, and the projection arriving from off-surface.</summary>
	private static void PaintUVProject( Vector2 c, Color color )
	{
		Stroked( color );
		Outline( At( c, -7, -2 ), At( c, 7, -2 ), At( c, 7, 8 ), At( c, -7, 8 ) );

		Stroked( color.WithAlpha( 0.6f ), 1.1f );
		Editor.Paint.DrawLine( At( c, -2.4f, -2 ), At( c, -2.4f, 8 ) );
		Editor.Paint.DrawLine( At( c, 2.4f, -2 ), At( c, 2.4f, 8 ) );
		Editor.Paint.DrawLine( At( c, -7, 3 ), At( c, 7, 3 ) );

		// One lit texel, so the grid reads as a texture rather than a wireframe.
		Filled( color.WithAlpha( 0.8f ) );
		Editor.Paint.DrawRect( Box( c, -6.3f, -1.3f, 3.8f, 3.6f ), 0.6f * _scale );

		// The projection coming down onto it.
		Stroked( color, 1.4f );
		Editor.Paint.DrawLine( At( c, 0, -8.4f ), At( c, 0, -4.4f ) );
		ArrowHead( At( c, 0, -2.6f ), new Vector2( 0, 1 ), color, 3f );
	}

	/// <summary>A cube with ONE of its three visible faces filled — the operation is "this face,
	/// not that one", so what the glyph has to show is faces being told apart. A paint pot or a
	/// swatch would say "material" without saying "per face", which is the whole distinction.</summary>
	private static void PaintFaceMaterial( Vector2 c, Color color )
	{
		// Isometric cube: top rhombus, then the two visible side quads.
		var top = At( c, 0, -8 );
		var right = At( c, 8, -3.5f );
		var bottom = At( c, 0, 1 );
		var left = At( c, -8, -3.5f );

		// The lit face, filled. DrawPolygon fills, which is exactly what is wanted here and is why
		// the other faces are walked as lines instead.
		Filled( color.WithAlpha( 0.85f ) );
		Editor.Paint.DrawPolygon( top, right, bottom, left );

		Stroked( color );
		Outline( top, right, bottom, left );

		// The two side faces, left plain so the filled top reads as the odd one out.
		var lowLeft = At( c, -8, 5.5f );
		var lowMid = At( c, 0, 10 );
		var lowRight = At( c, 8, 5.5f );

		Outline( left, bottom, lowMid, lowLeft );
		Outline( bottom, right, lowRight, lowMid );
	}

	// --- sketch tools ---------------------------------------------------------------------------
	//
	// One rule for the whole row: SHOW THE SHAPE THE TOOL MAKES, AND SHOW HOW IT IS PLACED.
	//
	// The second half is what earns its keep. Every family behind a chevron draws the identical
	// shape and differs only in which points you click — a corner rectangle and a centre rectangle
	// are the same rectangle — so the shape alone cannot tell them apart. The shape is the body of
	// the glyph and the click points are accent dots on it, which makes the pair legible side by
	// side without either needing a label.
	//
	// The dots are annotation and must never outweigh the shape. They were half again this size to
	// begin with, which looked right on a large preview and swallowed the geometry at the size these
	// are actually seen at.

	/// <summary>The colour of a click point. Deliberately the one warm accent in a monochrome row,
	/// so "this is where you press" reads before anything else does.</summary>
	private static readonly Color ClickColor = new( 1f, 0.77f, 0.24f, 1f );

	/// <summary>An end that does not join up. Warm rather than red — this is information, not an
	/// error, and a sketch mid-draw is full of them.</summary>
	private static readonly Color LooseEndColor = new( 1f, 0.48f, 0.36f, 1f );

	/// <summary>A guide line — a radius, a diagonal, a centre line. Something the tool uses to place
	/// the shape rather than part of the shape itself.</summary>
	private static Color GuideColor( Color color ) => color.WithAlpha( 0.35f );

	/// <summary>A filled dot in the nominal icon space. DrawRect with a corner radius of half its
	/// own size, since Paint has no circle of its own and this is exact.</summary>
	private static void Dot( Vector2 center, float radius, Color color )
	{
		Filled( color );
		Editor.Paint.DrawRect( Box( center, -radius, -radius, radius * 2f, radius * 2f ), radius * _scale );
	}

	private static void ClickDot( Vector2 p, float radius = 1.8f ) => Dot( p, radius, ClickColor );

	/// <summary>The corners of a regular hexagon, for the two polygon tools.</summary>
	private static Vector2[] Hexagon( Vector2 c, float radius, float rotationDegrees )
	{
		var points = new Vector2[6];

		for ( var i = 0; i < 6; i++ )
		{
			var a = (rotationDegrees + i * 60f) * MathF.PI / 180f;
			points[i] = At( c, MathF.Cos( a ) * radius, MathF.Sin( a ) * radius );
		}

		return points;
	}

	/// <summary>A cursor with a point caught under it. Select drags sketch POINTS, which is what the
	/// dot says and a bare arrow would not.</summary>
	private static void PaintSelectTool( Vector2 c, Color color )
	{
		Filled( color );
		Editor.Paint.DrawPolygon(
			At( c, -4, -8 ), At( c, -4, 4 ), At( c, -1, 1 ), At( c, 1.5f, 6 ),
			At( c, 4, 5 ), At( c, 1.5f, 0.2f ), At( c, 5, -0.5f ) );

		ClickDot( At( c, 5, 5 ), 2.2f );
	}

	private static void PaintLineTool( Vector2 c, Color color )
	{
		Stroked( color, 1.8f );
		Editor.Paint.DrawLine( At( c, -6.5f, 6 ), At( c, 6.5f, -6 ) );

		ClickDot( At( c, -6.5f, 6 ) );
		ClickDot( At( c, 6.5f, -6 ) );
	}

	/// <summary>Two opposite corners marked: click one, then the other.</summary>
	private static void PaintRectangleTool( Vector2 c, Color color )
	{
		Stroked( color );
		Outline( At( c, -6.5f, -5 ), At( c, 6.5f, -5 ), At( c, 6.5f, 5 ), At( c, -6.5f, 5 ) );

		ClickDot( At( c, -6.5f, -5 ) );
		ClickDot( At( c, 6.5f, 5 ) );
	}

	/// <summary>The same rectangle, marked at its CENTRE instead — with the half-diagonal it is
	/// dragged out along.</summary>
	private static void PaintRectangleCentreTool( Vector2 c, Color color )
	{
		Stroked( color );
		Outline( At( c, -6.5f, -5 ), At( c, 6.5f, -5 ), At( c, 6.5f, 5 ), At( c, -6.5f, 5 ) );

		Stroked( GuideColor( color ), 1f );
		Editor.Paint.DrawLine( c, At( c, 6.5f, 5 ) );

		ClickDot( c, 1.9f );
	}

	private static void PaintCircleTool( Vector2 c, Color color )
	{
		Stroked( color );
		Arc( c, 6.2f, 0, 360, 28 );

		Stroked( GuideColor( color ), 1f );
		Editor.Paint.DrawLine( c, At( c, 6.2f, 0 ) );

		ClickDot( c, 1.9f );
	}

	/// <summary>The same circle with three points ON the rim and no centre — which is precisely the
	/// difference between the two ways of placing it.</summary>
	private static void PaintCircleThreePointTool( Vector2 c, Color color )
	{
		Stroked( color );
		Arc( c, 6.2f, 0, 360, 28 );

		foreach ( var degrees in new[] { -90f, 30f, 150f } )
		{
			var a = degrees * MathF.PI / 180f;
			ClickDot( At( c, MathF.Cos( a ) * 6.2f, MathF.Sin( a ) * 6.2f ) );
		}
	}

	/// <summary>
	/// An arc standing on its centre, with both radii drawn.
	///
	/// It was drawn small and off to one side first, and at the size these are actually used it read
	/// as a tick mark rather than a curve. An arc has to span the box to look like an arc.
	/// </summary>
	private static void PaintArcTool( Vector2 c, Color color )
	{
		var hub = At( c, 0, 5.5f );

		Stroked( color, 1.9f );
		Arc( hub, 10.5f, 180, 360, 20 );

		Stroked( GuideColor( color ), 1f );
		Editor.Paint.DrawLine( hub, At( c, -10.5f, 5.5f ) );
		Editor.Paint.DrawLine( hub, At( c, 10.5f, 5.5f ) );

		ClickDot( hub, 1.9f );
	}

	/// <summary>The same arc with no centre at all, marked instead at both ends and the point it
	/// passes through.</summary>
	private static void PaintArcThreePointTool( Vector2 c, Color color )
	{
		var hub = At( c, 0, 5.5f );

		Stroked( color, 1.9f );
		Arc( hub, 10.5f, 180, 360, 20 );

		ClickDot( At( c, -10.5f, 5.5f ) );
		ClickDot( At( c, 0, -5f ) );
		ClickDot( At( c, 10.5f, 5.5f ) );
	}

	/// <summary>
	/// A polygon with its corners ON the circle.
	///
	/// The circle is drawn brighter than a guide normally would be, because where it sits relative
	/// to the polygon IS the whole difference between this and the circumscribed version. Draw it at
	/// guide strength and the two glyphs become the same hexagon.
	/// </summary>
	private static void PaintPolygonTool( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.6f ), 1.2f );
		Arc( c, 7f, 0, 360, 28 );

		Stroked( color, 1.7f );
		Outline( Hexagon( c, 7f, -90f ) );

		ClickDot( c, 1.7f );
	}

	/// <summary>Edges on the circle instead, so it sits visibly inside the polygon — the apothem is
	/// 0.866 of the radius, a gap wide enough to read small.</summary>
	private static void PaintPolygonCircumscribedTool( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );
		Outline( Hexagon( c, 7.6f, -90f ) );

		Stroked( color.WithAlpha( 0.6f ), 1.2f );
		Arc( c, 6.6f, 0, 360, 28 );

		ClickDot( c, 1.7f );
	}

	/// <summary>A slot, with the centre line you actually click marked at both ends.</summary>
	private static void PaintSlotTool( Vector2 c, Color color )
	{
		const float r = 4.6f;

		Stroked( color );
		Editor.Paint.DrawLine( At( c, -3, -r ), At( c, 3, -r ) );
		Editor.Paint.DrawLine( At( c, -3, r ), At( c, 3, r ) );
		Arc( At( c, 3, 0 ), r, -90, 90, 12 );
		Arc( At( c, -3, 0 ), r, 90, 270, 12 );

		Stroked( GuideColor( color ), 1f );
		Editor.Paint.DrawLine( At( c, -3, 0 ), At( c, 3, 0 ) );

		ClickDot( At( c, -3, 0 ) );
		ClickDot( At( c, 3, 0 ) );
	}

	/// <summary>Crosshairs around a point. The gap at the centre is what stops it reading as a plus
	/// sign.</summary>
	private static void PaintPointTool( Vector2 c, Color color )
	{
		Stroked( color, 1.4f );
		Editor.Paint.DrawLine( At( c, -7, 0 ), At( c, -2.5f, 0 ) );
		Editor.Paint.DrawLine( At( c, 2.5f, 0 ), At( c, 7, 0 ) );
		Editor.Paint.DrawLine( At( c, 0, -7 ), At( c, 0, -2.5f ) );
		Editor.Paint.DrawLine( At( c, 0, 2.5f ), At( c, 0, 7 ) );

		ClickDot( c, 2.2f );
	}

	/// <summary>A dashed line: geometry that guides and never becomes part of a profile. Dashed
	/// because that is how construction geometry is drawn in the viewport, so the button and the
	/// thing it makes look like each other.</summary>
	private static void PaintConstructionTool( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );

		foreach ( var (from, to) in new[] { (0f, 0.22f), (0.39f, 0.61f), (0.78f, 1f) } )
		{
			Editor.Paint.DrawLine(
				At( c, -7 + 14 * from, 6 - 12 * from ),
				At( c, -7 + 14 * to, 6 - 12 * to ) );
		}
	}

	/// <summary>
	/// A shaded region with a gap in its outline, and the two loose ends called out.
	///
	/// This is exactly what the inspector shows: which regions closed, and where a chain did not.
	/// Drawn first as a stub with a dot on it, which at the size these are seen reads as a box with
	/// a speck in the corner and says nothing. The fill has to be solid enough to read as shading and
	/// the gap has to be a real hole in the outline.
	/// </summary>
	private static void PaintProfileInspectorTool( Vector2 c, Color color )
	{
		Filled( color.WithAlpha( 0.41f ) );
		Editor.Paint.DrawPolygon( At( c, -6.5f, -5 ), At( c, 6.5f, -5 ), At( c, 6.5f, 5 ), At( c, -6.5f, 5 ) );

		// Walked as an open polyline rather than an outline, because the gap is the point.
		Stroked( color, 1.7f );
		Editor.Paint.DrawLine( At( c, 6.5f, -1.6f ), At( c, 6.5f, -5 ) );
		Editor.Paint.DrawLine( At( c, 6.5f, -5 ), At( c, -6.5f, -5 ) );
		Editor.Paint.DrawLine( At( c, -6.5f, -5 ), At( c, -6.5f, 5 ) );
		Editor.Paint.DrawLine( At( c, -6.5f, 5 ), At( c, 6.5f, 5 ) );
		Editor.Paint.DrawLine( At( c, 6.5f, 5 ), At( c, 6.5f, 1.6f ) );

		Dot( At( c, 6.5f, -1.6f ), 1.9f, LooseEndColor );
		Dot( At( c, 6.5f, 1.6f ), 1.9f, LooseEndColor );
	}

	/// <summary>A plain tick. The one glyph in the row that must not be clever: it ends the mode,
	/// and the confirm colour it is painted in already carries the meaning.</summary>
	private static void PaintFinishSketchTool( Vector2 c, Color color )
	{
		Stroked( color, 2.2f );
		Editor.Paint.DrawLine( At( c, -6, 0.5f ), At( c, -1.5f, 5 ) );
		Editor.Paint.DrawLine( At( c, -1.5f, 5 ), At( c, 6.5f, -5 ) );
	}
}
