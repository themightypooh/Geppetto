using Editor;
using System.Collections.Generic;
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
	Sweep,
	Loft,
	Chamfer,
	Fillet,
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

	// --- sculpt tools -------------------------------------------------------------------------
	// The brushes are drawn as what they DO to a surface rather than as tool shapes: a row of six
	// identical brush heads distinguished by a tiny badge is six ways to pick the wrong one. Every
	// glyph here is a surface line and what the brush does to it.
	Sculpt,
	SculptDraw,
	SculptSmooth,
	SculptInflate,
	SculptGrab,
	SculptFlatten,
	SculptPinch,
	SculptMask,
	SculptLevelDown,
	SculptLevelUp,
	SculptBake,

	// --- solid tools that act on picked faces ---------------------------------------------------
	Draft,
	Hole,

	// --- the six sketch tools whose kernel half was finished first ------------------------------
	EllipseTool,
	SplineTool,
	TrimTool,
	ExtendTool,
	SketchFilletTool,
	OffsetTool,
}

/// <summary>
/// Painted icons for the feature-creation strip, drawn rather than looked up in a font.
///
/// Same reasoning as RigIconButton (see Editor/RigControlEditor): s&box ships CLASSIC Material
/// Icons, not the newer Material Symbols, so a name from the Symbols set silently renders as
/// nothing — and the strip was leaning on generic names like "square", "flip" and "call_made"
/// that, where they resolved at all, said nothing about the CAD operation behind them. A drawn
/// glyph can show the actual operation: Chamfer cuts a corner off a square, Shell puts a wall
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
			case EffigyIcon.Sweep: PaintSweep( center, color ); return;
			case EffigyIcon.Loft: PaintLoft( center, color ); return;
			case EffigyIcon.Chamfer: PaintChamfer( center, color ); return;
			case EffigyIcon.Fillet: PaintFillet( center, color ); return;
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

			case EffigyIcon.Sculpt: PaintSculpt( center, color ); return;
			case EffigyIcon.SculptDraw: PaintSculptDraw( center, color ); return;
			case EffigyIcon.SculptSmooth: PaintSculptSmooth( center, color ); return;
			case EffigyIcon.SculptInflate: PaintSculptInflate( center, color ); return;
			case EffigyIcon.SculptGrab: PaintSculptGrab( center, color ); return;
			case EffigyIcon.SculptFlatten: PaintSculptFlatten( center, color ); return;
			case EffigyIcon.SculptPinch: PaintSculptPinch( center, color ); return;
			case EffigyIcon.SculptMask: PaintSculptMask( center, color ); return;
			case EffigyIcon.SculptLevelDown: PaintSculptLevelDown( center, color ); return;
			case EffigyIcon.SculptLevelUp: PaintSculptLevelUp( center, color ); return;
			case EffigyIcon.SculptBake: PaintSculptBake( center, color ); return;

			case EffigyIcon.Draft: PaintDraft( center, color ); return;
			case EffigyIcon.Hole: PaintHole( center, color ); return;

			case EffigyIcon.EllipseTool: PaintEllipseTool( center, color ); return;
			case EffigyIcon.SplineTool: PaintSplineTool( center, color ); return;
			case EffigyIcon.TrimTool: PaintTrimTool( center, color ); return;
			case EffigyIcon.ExtendTool: PaintExtendTool( center, color ); return;
			case EffigyIcon.SketchFilletTool: PaintSketchFilletTool( center, color ); return;
			case EffigyIcon.OffsetTool: PaintOffsetTool( center, color ); return;
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

	/// <summary>
	/// A profile lying flat, and the solid pulled UP off it.
	///
	/// The old glyph had the profile on top with the arrow pointing down, which reads as something
	/// falling rather than as something being drawn out — and at toolbar size the whole thing came
	/// out looking like a plumb bob. Arrow and profile now agree about which way an extrude goes.
	/// </summary>
	private static void PaintExtrude( Vector2 c, Color color )
	{
		// The sketch, in plan, dimmed: it is what the operation starts FROM, not what it makes.
		Stroked( color.WithAlpha( 0.55f ), 1.5f );
		Outline( At( c, -7.5f, 6.5f ), At( c, 0, 9.4f ), At( c, 7.5f, 6.5f ), At( c, 0, 3.6f ) );

		Stroked( color, 2.6f );
		Editor.Paint.DrawLine( At( c, 0, 6.5f ), At( c, 0, -4.2f ) );

		ArrowHead( At( c, 0, -8.6f ), new Vector2( 0, -1 ), color, 4.2f );
	}

	/// <summary>
	/// A sketch sitting on an axis, and the spin that turns it into a solid.
	///
	/// Extrude is a straight arrow off a profile. This is the same grammar bent into a C — axis,
	/// profile, curved arrow — which is what every CAD tool draws for Revolve and what the last
	/// version threw out. That version drew a vase in section and hoped the silhouette would
	/// carry it; at toolbar size it was a lumpy outline with a dashed line through its face.
	/// Fill the profile (same weight as Chamfer and Shell) and let the arrow be the operation.
	/// </summary>
	private static void PaintRevolve( Vector2 c, Color color )
	{
		const float AxisX = -3.6f;

		Stroked( color.WithAlpha( 0.5f ), 1.35f );

		for ( var y = -8.6f; y < 8.6f; y += 3.8f )
			Editor.Paint.DrawLine( At( c, AxisX, y ), At( c, AxisX, MathF.Min( y + 2.2f, 8.6f ) ) );

		Filled( color.WithAlpha( 0.22f ) );
		Editor.Paint.DrawPolygon(
			At( c, AxisX, -5.2f ), At( c, 3.4f, -5.2f ),
			At( c, 3.4f, 5.2f ), At( c, AxisX, 5.2f ) );

		Stroked( color, 1.55f );
		Editor.Paint.DrawLine( At( c, AxisX, -5.2f ), At( c, 3.4f, -5.2f ) );
		Editor.Paint.DrawLine( At( c, 3.4f, -5.2f ), At( c, 3.4f, 5.2f ) );
		Editor.Paint.DrawLine( At( c, 3.4f, 5.2f ), At( c, AxisX, 5.2f ) );

		const float Radius = 8.4f;
		const float From = -80f;
		const float To = 85f;
		var hub = At( c, AxisX, 0 );

		Stroked( color, 2.1f );
		Arc( hub, Radius, From, To, 22 );

		var end = To * MathF.PI / 180f;
		var tip = hub + new Vector2( MathF.Cos( end ), MathF.Sin( end ) ) * Radius * _scale;
		var tangent = new Vector2( -MathF.Sin( end ), MathF.Cos( end ) );

		ArrowHead( tip, tangent, color, 3.6f );
	}

	/// <summary>
	/// A profile carried along a path, with the path drawn as the thing that shapes it.
	///
	/// Sweep and Extrude are the same sentence with a different verb — a profile, and where it goes
	/// — so they are drawn with the same grammar: the starting profile dim because it is what the
	/// operation begins FROM rather than what it makes, and an arrow for the operation itself. The
	/// difference between them is the whole point, so here the path is a curve and the solid
	/// follows it instead of standing straight up.
	/// </summary>
	private static void PaintSweep( Vector2 c, Color color )
	{
		// Hub, radius and extent of the path. Everything else is derived from these, so the glyph
		// stays consistent with itself if the arc is retuned.
		const float HubX = -7f;
		const float HubY = -7.5f;
		const float Radius = 12f;

		// Half the profile's width, so the band either side of the path IS the solid.
		const float Half = 2.6f;

		const float From = 0f;
		const float To = 90f;
		const float ArrowAt = To + 10f;

		var hub = At( c, HubX, HubY );

		// The swept solid: the path offset either side of itself. Faint rather than outlined at
		// full weight, so the path stays the strongest line in the glyph.
		Stroked( color.WithAlpha( 0.32f ), 2f );
		Arc( hub, Radius - Half, From, To, 18 );
		Arc( hub, Radius + Half, From, To, 18 );

		Stroked( color, 1.7f );
		Arc( hub, Radius, From, ArrowAt, 20 );

		// Where it starts, and where it arrives.
		SweepStation( hub, Radius, Half, From, color.WithAlpha( 0.55f ), 1.4f );
		SweepStation( hub, Radius, Half, To, color, 1.7f );

		var end = ArrowAt * MathF.PI / 180f;
		var tip = hub + new Vector2( MathF.Cos( end ), MathF.Sin( end ) ) * Radius * _scale;

		ArrowHead( tip, new Vector2( -MathF.Sin( end ), MathF.Cos( end ) ), color, 3.6f );
	}

	/// <summary>The profile at one station of a sweep: a diamond spanning the swept band, drawn
	/// ACROSS the path rather than lying flat, because a sweep takes its profile perpendicular to
	/// where it is going — see SweepFeature.</summary>
	private static void SweepStation( Vector2 hub, float radius, float half, float degrees, Color color, float width )
	{
		var angle = degrees * MathF.PI / 180f;
		var radial = new Vector2( MathF.Cos( angle ), MathF.Sin( angle ) );
		var tangent = new Vector2( -radial.y, radial.x );
		var centre = hub + radial * radius * _scale;

		Stroked( color, width );
		Outline(
			centre + radial * half * _scale,
			centre + tangent * half * 0.62f * _scale,
			centre - radial * half * _scale,
			centre - tangent * half * 0.62f * _scale );
	}

	/// <summary>
	/// Two sections, and the skin ruled between them.
	///
	/// The sections are drawn as flat diamonds — the same plan-view profile Extrude and Sweep use,
	/// so a closed sketch reads the same way everywhere on this strip — one small and one large, so
	/// what lies between them has to be a loft rather than an extrusion. The sides are STRAIGHT,
	/// which is what the kernel actually does: neighbouring sections joined by a ruled surface, not
	/// a spline smoothly through them.
	/// </summary>
	private static void PaintLoft( Vector2 c, Color color )
	{
		const float TopY = -7f;
		const float TopHalf = 3.4f;
		const float BottomY = 6.6f;
		const float BottomHalf = 7.6f;

		// The skin, as a tint between the two sections — same weight as Chamfer and Shell, so it
		// reads as material rather than as two more lines.
		Filled( color.WithAlpha( 0.22f ) );
		Editor.Paint.DrawPolygon(
			At( c, -TopHalf, TopY ), At( c, TopHalf, TopY ),
			At( c, BottomHalf, BottomY ), At( c, -BottomHalf, BottomY ) );

		Stroked( color, 1.7f );
		Editor.Paint.DrawLine( At( c, -TopHalf, TopY ), At( c, -BottomHalf, BottomY ) );
		Editor.Paint.DrawLine( At( c, TopHalf, TopY ), At( c, BottomHalf, BottomY ) );

		LoftSection( c, TopY, TopHalf, TopHalf * 0.46f, color );
		LoftSection( c, BottomY, BottomHalf, BottomHalf * 0.32f, color );
	}

	/// <summary>One loft section, in plan: a diamond as wide as the section and shallow enough to
	/// read as lying flat, rather than as the top and bottom edges of a trapezium.</summary>
	private static void LoftSection( Vector2 c, float y, float half, float depth, Color color )
	{
		Stroked( color, 1.5f );
		Outline( At( c, -half, y ), At( c, 0, y - depth ), At( c, half, y ), At( c, 0, y + depth ) );
	}

	/// <summary>
	/// A solid block with its corner cut away, the cut face called out.
	///
	/// The old glyph was an outlined square with a small nick in one corner, which at toolbar size
	/// is a page icon and nothing else. Two changes fix it: fill the body, so it reads as a solid
	/// rather than as a sheet, and cut deep enough that the chamfer is a face rather than a nick.
	/// The faint lines show the corner that was removed.
	/// </summary>
	private static void PaintChamfer( Vector2 c, Color color )
	{
		Filled( color.WithAlpha( 0.22f ) );
		Editor.Paint.DrawPolygon(
			At( c, -7, -1.5f ), At( c, -1.5f, -7 ), At( c, 7, -7 ), At( c, 7, 7 ), At( c, -7, 7 ) );

		Stroked( color, 1.5f );
		Outline( At( c, -7, -1.5f ), At( c, -1.5f, -7 ), At( c, 7, -7 ), At( c, 7, 7 ), At( c, -7, 7 ) );

		// The cut face, in the same amber the sketch pencil draws with — the accent on this strip
		// means "the thing this operation did".
		Stroked( ClickColor, 2.8f );
		Editor.Paint.DrawLine( At( c, -7, -1.5f ), At( c, -1.5f, -7 ) );

		Stroked( color.WithAlpha( 0.3f ), 1f );
		Editor.Paint.DrawLine( At( c, -7, -1.5f ), At( c, -7, -7 ) );
		Editor.Paint.DrawLine( At( c, -7, -7 ), At( c, -1.5f, -7 ) );
	}

	/// <summary>
	/// The chamfer's twin, and deliberately so: the same solid, the same corner gone, the same
	/// ghost of the corner that was removed — the ONLY difference is that the accent is an arc
	/// instead of a straight line.
	///
	/// That is the whole point. These two sit next to each other on the strip and the thing a
	/// person needs to tell apart at 40px is round versus flat, which a shared body makes obvious
	/// and two unrelated drawings would bury.
	/// </summary>
	private static void PaintFillet( Vector2 c, Color color )
	{
		// The arc's centre is the inner corner of the cut, so it runs from (-7,-1.5) to (-1.5,-7)
		// exactly where the chamfer's straight cut does.
		var arc = ArcPoints( At( c, -1.5f, -1.5f ), 5.5f, 180f, 270f, 10 );

		var body = new List<Vector2>( arc );
		body.Add( At( c, 7, -7 ) );
		body.Add( At( c, 7, 7 ) );
		body.Add( At( c, -7, 7 ) );

		Filled( color.WithAlpha( 0.22f ) );
		Editor.Paint.DrawPolygon( body.ToArray() );

		Stroked( color, 1.5f );
		Outline( body.ToArray() );

		Stroked( ClickColor, 2.8f );
		Arc( At( c, -1.5f, -1.5f ), 5.5f, 180f, 270f, 10 );

		Stroked( color.WithAlpha( 0.3f ), 1f );
		Editor.Paint.DrawLine( At( c, -7, -1.5f ), At( c, -7, -7 ) );
		Editor.Paint.DrawLine( At( c, -7, -7 ), At( c, -1.5f, -7 ) );
	}

	/// <summary>The points Arc walks, for a glyph that needs the arc as part of a filled outline
	/// rather than as a stroke. Same maths, so the fill and the stroke cannot drift apart.</summary>
	private static List<Vector2> ArcPoints( Vector2 center, float radius,
		float fromDegrees, float toDegrees, int segments )
	{
		var points = new List<Vector2>( segments + 1 );

		for ( var i = 0; i <= segments; i++ )
		{
			var t = fromDegrees + (toDegrees - fromDegrees) * (i / (float)segments);
			var radians = t * MathF.PI / 180f;

			points.Add( center + new Vector2( MathF.Cos( radians ) * radius, MathF.Sin( radians ) * radius ) * _scale );
		}

		return points;
	}

	/// <summary>
	/// A hollowed solid in section: material on three sides, opening at the top.
	///
	/// A square inside a square is a frame, a border, a picture — it was never going to say
	/// "hollowed to a wall thickness". THE WALL IS THE OBJECT, so the wall is what gets filled and
	/// the void is what gets left out, which is how a section drawing says it.
	/// </summary>
	private static void PaintShell( Vector2 c, Color color )
	{
		Filled( color.WithAlpha( 0.9f ) );
		Editor.Paint.DrawPolygon(
			At( c, -7.8f, -7f ), At( c, -3.6f, -7f ), At( c, -3.6f, 3.2f ),
			At( c, 3.6f, 3.2f ), At( c, 3.6f, -7f ), At( c, 7.8f, -7f ),
			At( c, 7.8f, 7.4f ), At( c, -7.8f, 7.4f ) );

		// The opening, as a faint lid line, so the U reads as a container rather than as a letter.
		Stroked( color.WithAlpha( 0.45f ), 1.1f );
		Editor.Paint.DrawLine( At( c, -3.6f, -7f ), At( c, 3.6f, -7f ) );
	}

	/// <summary>
	/// A quad split into four, with one of those four split again — subdivision, drawn literally.
	///
	/// The old glyph was a rounded square with a cross and a dot in the middle, which is the
	/// universal "add" icon and was read as one. Showing one quadrant DENSER than its neighbours is
	/// what the operation actually does, and the density is carried by a tint as well as by lines so
	/// it survives being small — at twenty-four pixels a 4x4 of hairlines is a grey smear.
	/// </summary>
	private static void PaintSubdivide( Vector2 c, Color color )
	{
		Stroked( color, 1.6f );
		Outline( At( c, -8, -8 ), At( c, 8, -8 ), At( c, 8, 8 ), At( c, -8, 8 ) );

		Filled( color.WithAlpha( 0.3f ) );
		Editor.Paint.DrawPolygon( At( c, -8, -8 ), At( c, 0, -8 ), At( c, 0, 0 ), At( c, -8, 0 ) );

		Stroked( color.WithAlpha( 0.9f ), 1.5f );
		Editor.Paint.DrawLine( At( c, 0, -8 ), At( c, 0, 8 ) );
		Editor.Paint.DrawLine( At( c, -8, 0 ), At( c, 8, 0 ) );

		Stroked( color.WithAlpha( 0.85f ), 1.2f );
		Editor.Paint.DrawLine( At( c, -4, -8 ), At( c, -4, 0 ) );
		Editor.Paint.DrawLine( At( c, -8, -4 ), At( c, 0, -4 ) );
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

	/// <summary>
	/// Copies stepped around an axis, on a ring that can actually be seen.
	///
	/// The old glyph drew the ring as twelve dashes, and at toolbar size twelve dashes are a faint
	/// smudge — which left three small squares floating with nothing to explain them. A solid thin
	/// ring and a dot at the centre cost less ink and say more, and the copies are smaller than they
	/// were so they sit ON the ring instead of swallowing it.
	/// </summary>
	private static void PaintCircularPattern( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.7f ), 1.4f );
		Arc( c, 6.6f, 0f, 360f, 40 );

		Filled( color.WithAlpha( 0.8f ) );
		Editor.Paint.DrawRect( Box( c, -1.3f, -1.3f, 2.6f, 2.6f ), 1.3f * _scale );

		var angles = new[] { -90f, 30f, 150f };

		for ( var i = 0; i < angles.Length; i++ )
		{
			var radians = angles[i] * MathF.PI / 180f;

			var box = Box( c,
				MathF.Cos( radians ) * 6.6f - 2.5f,
				MathF.Sin( radians ) * 6.6f - 2.5f, 5f, 5f );

			// One filled, the rest outlined — the same "this is the original, these are the copies"
			// grammar the linear pattern uses, so the pair read as a family.
			if ( i == 0 )
			{
				Filled( color );
				Editor.Paint.DrawRect( box, 1f * _scale );
			}
			else
			{
				Stroked( color.WithAlpha( 0.9f ), 1.5f );
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

	// --- sculpt tools ---------------------------------------------------------------------------
	//
	// EVERY ONE OF THESE IS A SURFACE AND WHAT HAPPENS TO IT. The obvious way to draw six brushes is
	// six brush heads with a small badge each, which at 27px is six identical blobs. Drawing the
	// EFFECT instead means the row can be read at a glance without learning it: a bump rising, a
	// ripple flattening, a peak dragged sideways.
	//
	// The surface runs across the lower half so every glyph shares a baseline and the row reads as
	// one family.

	/// <summary>A surface with a bump pushed up out of it, and the brush's ring resting on the
	/// bump. The feature-strip glyph, so it says "sculpting" rather than any one brush.</summary>
	private static void PaintSculpt( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );
		SurfaceWithBump( c, 4.5f );

		// The brush ring, seen at a slight angle so it reads as sitting ON the surface.
		Stroked( color.WithAlpha( 0.75f ), 1.3f );
		Arc( At( c, 0, -3.4f ), 5.6f, 0f, 360f, 20 );
	}

	/// <summary>A bump, and an arrow pushing outward from it: draw adds material along the normal.</summary>
	private static void PaintSculptDraw( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );
		SurfaceWithBump( c, 4f );

		Stroked( color, 1.4f );
		Editor.Paint.DrawLine( At( c, 0, -1.5f ), At( c, 0, -7f ) );
		ArrowHead( At( c, 0, -8f ), new Vector2( 0, -1 ), color );
	}

	/// <summary>A ripple above, the same surface calmed below. Smooth is the one brush whose whole
	/// meaning is the difference between two lines.</summary>
	private static void PaintSculptSmooth( Vector2 c, Color color )
	{
		// Rippled.
		Stroked( color.WithAlpha( 0.85f ), 1.5f );
		var previous = At( c, -8.5f, -4f );

		for ( var i = 1; i <= 24; i++ )
		{
			var t = i / 24f;
			var x = -8.5f + 17f * t;
			var y = -4f + MathF.Sin( t * MathF.PI * 3f ) * 2.6f;
			var point = At( c, x, y );

			Editor.Paint.DrawLine( previous, point );
			previous = point;
		}

		// Calmed.
		Stroked( color, 1.8f );
		Editor.Paint.DrawLine( At( c, -8.5f, 5f ), At( c, 8.5f, 5f ) );

		Stroked( color.WithAlpha( 0.6f ), 1.2f );
		Editor.Paint.DrawLine( At( c, 0, -0.5f ), At( c, 0, 2.2f ) );
		ArrowHead( At( c, 0, 3.4f ), new Vector2( 0, 1 ), color.WithAlpha( 0.6f ), 2.8f );
	}

	/// <summary>A closed shape with arrows pushing out all round it — inflate acts everywhere at
	/// once, which is what tells it apart from draw.</summary>
	private static void PaintSculptInflate( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );
		Arc( c, 4.6f, 0f, 360f, 20 );

		for ( var i = 0; i < 4; i++ )
		{
			var radians = (45f + i * 90f) * MathF.PI / 180f;
			var dir = new Vector2( MathF.Cos( radians ), MathF.Sin( radians ) );

			Stroked( color.WithAlpha( 0.9f ), 1.3f );
			Editor.Paint.DrawLine( c + dir * 5.8f * _scale, c + dir * 8f * _scale );
			ArrowHead( c + dir * 9.2f * _scale, dir, color, 2.8f );
		}
	}

	/// <summary>A surface dragged sideways into a lean, with the pull shown as an arrow. Grab moves
	/// what it holds rather than adding to it, so nothing here points along the normal.</summary>
	private static void PaintSculptGrab( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );

		// A peak that leans right, rather than a symmetric bump.
		Editor.Paint.DrawLine( At( c, -8.5f, 5f ), At( c, -2.5f, 5f ) );
		Editor.Paint.DrawLine( At( c, -2.5f, 5f ), At( c, 2.5f, -3f ) );
		Editor.Paint.DrawLine( At( c, 2.5f, -3f ), At( c, 5.5f, 5f ) );
		Editor.Paint.DrawLine( At( c, 5.5f, 5f ), At( c, 8.5f, 5f ) );

		Stroked( color.WithAlpha( 0.9f ), 1.4f );
		Editor.Paint.DrawLine( At( c, -2f, -6f ), At( c, 3.5f, -6f ) );
		ArrowHead( At( c, 5f, -6f ), new Vector2( 1, 0 ), color );
	}

	/// <summary>A bump with a straight edge laid across it — flatten is a plane meeting a surface.
	/// </summary>
	private static void PaintSculptFlatten( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.65f ), 1.5f );
		SurfaceWithBump( c, 5.5f );

		// The plane it is being cut back to.
		Stroked( color, 2f );
		Editor.Paint.DrawLine( At( c, -8.5f, -2.5f ), At( c, 8.5f, -2.5f ) );
	}

	/// <summary>Two arrows squeezing towards one ridge. Pinch gathers a surface rather than moving
	/// it, so both arrows point inward at the same line.</summary>
	private static void PaintSculptPinch( Vector2 c, Color color )
	{
		Stroked( color, 1.8f );
		Editor.Paint.DrawLine( At( c, 0, -7.5f ), At( c, 0, 7.5f ) );

		Stroked( color.WithAlpha( 0.9f ), 1.4f );
		Editor.Paint.DrawLine( At( c, -8f, 0 ), At( c, -3.5f, 0 ) );
		ArrowHead( At( c, -2.2f, 0 ), new Vector2( 1, 0 ), color );

		Editor.Paint.DrawLine( At( c, 8f, 0 ), At( c, 3.5f, 0 ) );
		ArrowHead( At( c, 2.2f, 0 ), new Vector2( -1, 0 ), color );
	}

	/// <summary>A patch of the surface hatched off. Masking protects rather than shapes, so this is
	/// the one sculpt glyph that is not a deformation.</summary>
	private static void PaintSculptMask( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.85f ), 1.5f );
		Outline( At( c, -8, -6.5f ), At( c, 8, -6.5f ), At( c, 8, 6.5f ), At( c, -8, 6.5f ) );

		// Hatching, the universal "held back" texture.
		Stroked( color.WithAlpha( 0.7f ), 1.2f );

		for ( var x = -6f; x <= 8f; x += 3.4f )
		{
			var top = MathF.Max( x - 13f, -8f );
			var bottom = MathF.Min( x, 8f );

			Editor.Paint.DrawLine( At( c, bottom, -6.5f ), At( c, top, 6.5f ) );
		}
	}

	/// <summary>A coarse grid with a chevron down: fewer, bigger faces.</summary>
	private static void PaintSculptLevelDown( Vector2 c, Color color ) => PaintSculptLevel( c, color, 2, down: true );

	/// <summary>A fine grid with a chevron up: four times the faces, which is the whole cost.
	/// </summary>
	private static void PaintSculptLevelUp( Vector2 c, Color color ) => PaintSculptLevel( c, color, 4, down: false );

	private static void PaintSculptLevel( Vector2 c, Color color, int divisions, bool down )
	{
		const float Half = 6.5f;

		Stroked( color.WithAlpha( 0.9f ), 1.4f );
		Outline( At( c, -Half, -Half - 1.5f ), At( c, Half, -Half - 1.5f ),
			At( c, Half, Half - 1.5f ), At( c, -Half, Half - 1.5f ) );

		Stroked( color.WithAlpha( 0.65f ), 1f );

		for ( var i = 1; i < divisions; i++ )
		{
			var t = -Half + i * (Half * 2f / divisions);

			Editor.Paint.DrawLine( At( c, t, -Half - 1.5f ), At( c, t, Half - 1.5f ) );
			Editor.Paint.DrawLine( At( c, -Half, t - 1.5f ), At( c, Half, t - 1.5f ) );
		}

		// The chevron, below the grid so the two never overlap at strip size.
		Stroked( color, 1.8f );

		if ( down )
		{
			Editor.Paint.DrawLine( At( c, -3.5f, 6f ), At( c, 0, 9f ) );
			Editor.Paint.DrawLine( At( c, 0, 9f ), At( c, 3.5f, 6f ) );
		}
		else
		{
			Editor.Paint.DrawLine( At( c, -3.5f, 9f ), At( c, 0, 6f ) );
			Editor.Paint.DrawLine( At( c, 0, 6f ), At( c, 3.5f, 9f ) );
		}
	}

	/// <summary>A dense surface collapsing into a flat square: the sculpt becoming a texture, which
	/// is the whole point of the pipeline and the one operation here that produces a file.</summary>
	private static void PaintSculptBake( Vector2 c, Color color )
	{
		// The sculpted surface, up top.
		Stroked( color.WithAlpha( 0.85f ), 1.5f );
		var previous = At( c, -8.5f, -5f );

		for ( var i = 1; i <= 20; i++ )
		{
			var t = i / 20f;
			var x = -8.5f + 17f * t;
			var y = -5f + MathF.Sin( t * MathF.PI * 2f ) * 2.2f;
			var point = At( c, x, y );

			Editor.Paint.DrawLine( previous, point );
			previous = point;
		}

		// Into the map.
		Stroked( color.WithAlpha( 0.7f ), 1.2f );
		Editor.Paint.DrawLine( At( c, 0, -1f ), At( c, 0, 1.6f ) );
		ArrowHead( At( c, 0, 2.8f ), new Vector2( 0, 1 ), color.WithAlpha( 0.7f ), 2.8f );

		Stroked( color, 1.6f );
		Outline( At( c, -7, 4f ), At( c, 7, 4f ), At( c, 7, 9f ), At( c, -7, 9f ) );

		Filled( color.WithAlpha( 0.3f ) );
		Editor.Paint.DrawRect( Box( c, -7, 4f, 14f, 5f ) );
	}

	/// <summary>The shared baseline: a flat surface with one smooth bump in the middle of it. Every
	/// brush glyph starts from this so the row reads as one family acting on one thing.</summary>
	private static void SurfaceWithBump( Vector2 c, float height )
	{
		var previous = At( c, -8.5f, 5f );

		for ( var i = 1; i <= 24; i++ )
		{
			var t = i / 24f;
			var x = -8.5f + 17f * t;

			// A raised cosine, flat at both ends so it meets the surface without a corner.
			var bump = 0.5f * (1f + MathF.Cos( MathF.Max( MathF.Min( x / 5.5f, 1f ), -1f ) * MathF.PI ));
			var point = At( c, x, 5f - bump * height );

			Editor.Paint.DrawLine( previous, point );
			previous = point;
		}
	}

	/// <summary>
	/// A wall leaning off vertical, with the vertical it leans from left dashed beside it.
	///
	/// The angle IS the operation, so the glyph is the angle. Drawing a moulded part instead would
	/// say "moulding" and leave you guessing which of the six tools on the strip does the leaning.
	/// </summary>
	private static void PaintDraft( Vector2 c, Color color )
	{
		// The parting line the taper is measured from.
		Stroked( color.WithAlpha( 0.45f ), 1.1f );
		for ( var x = -9f; x < 9f; x += 3.4f )
			Editor.Paint.DrawLine( At( c, x, 0 ), At( c, x + 2f, 0 ) );

		// The tapered wall: narrow at the top, wide at the bottom, closed as an outline.
		Stroked( color, 1.7f );
		Outline( At( c, -3.4f, -8 ), At( c, 3.4f, -8 ), At( c, 6.6f, 8 ), At( c, -6.6f, 8 ) );

		// The vertical it is leaning away from, so the lean reads as deliberate rather than as a
		// wonky rectangle.
		Stroked( color.WithAlpha( 0.5f ), 1f );
		Editor.Paint.DrawLine( At( c, 3.4f, -8 ), At( c, 3.4f, 8 ) );
	}

	/// <summary>
	/// A counterbore in section: a wide mouth stepping down to a narrow shaft, through a plate.
	///
	/// Drawn as a SECTION rather than as a circle on a surface, because a circle is what every other
	/// round thing on this strip already looks like from above - and the step is the whole reason
	/// this is a feature rather than a sketched circle.
	/// </summary>
	private static void PaintHole( Vector2 c, Color color )
	{
		// The plate, in section.
		Stroked( color.WithAlpha( 0.8f ), 1.5f );
		Editor.Paint.DrawLine( At( c, -9, -6 ), At( c, -3.2f, -6 ) );
		Editor.Paint.DrawLine( At( c, 3.2f, -6 ), At( c, 9, -6 ) );
		Editor.Paint.DrawLine( At( c, -9, 7 ), At( c, 9, 7 ) );
		Editor.Paint.DrawLine( At( c, -9, -6 ), At( c, -9, 7 ) );
		Editor.Paint.DrawLine( At( c, 9, -6 ), At( c, 9, 7 ) );

		// The bore: wide at the mouth, stepping in to the shaft.
		Stroked( color, 1.7f );
		Editor.Paint.DrawLine( At( c, -3.2f, -6 ), At( c, -3.2f, -1 ) );
		Editor.Paint.DrawLine( At( c, -3.2f, -1 ), At( c, -1.4f, -1 ) );
		Editor.Paint.DrawLine( At( c, -1.4f, -1 ), At( c, -1.4f, 7 ) );

		Editor.Paint.DrawLine( At( c, 3.2f, -6 ), At( c, 3.2f, -1 ) );
		Editor.Paint.DrawLine( At( c, 3.2f, -1 ), At( c, 1.4f, -1 ) );
		Editor.Paint.DrawLine( At( c, 1.4f, -1 ), At( c, 1.4f, 7 ) );

		// The void itself, so the shape reads as absence rather than as a post.
		Filled( color.WithAlpha( 0.18f ) );
		Editor.Paint.DrawPolygon(
			At( c, -3.2f, -6 ), At( c, 3.2f, -6 ), At( c, 3.2f, -1 ), At( c, 1.4f, -1 ),
			At( c, 1.4f, 7 ), At( c, -1.4f, 7 ), At( c, -1.4f, -1 ), At( c, -3.2f, -1 ) );
	}
	// --- the six sketch tools -------------------------------------------------------------------
	//
	// The four EDIT tools all show the same thing: a curve, and what the tool does to it, with the
	// part being removed or added drawn faintly. A trim that showed only the result would be
	// indistinguishable from a plain line at 27 pixels.

	/// <summary>An ellipse, with its long axis marked so it is not mistaken for a circle.</summary>
	private static void PaintEllipseTool( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );

		var previous = Vector2.Zero;

		for ( var i = 0; i <= 40; i++ )
		{
			var a = i / 40f * MathF.PI * 2f;
			var point = At( c, MathF.Cos( a ) * 8.6f, MathF.Sin( a ) * 5f );

			if ( i > 0 )
				Editor.Paint.DrawLine( previous, point );

			previous = point;
		}

		Stroked( color.WithAlpha( 0.5f ), 1.1f );
		Editor.Paint.DrawLine( At( c, -8.6f, 0 ), At( c, 8.6f, 0 ) );
	}

	/// <summary>A curve through its control points, which is what a spline IS - the points are the
	/// thing you place and the curve is what follows.</summary>
	private static void PaintSplineTool( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );

		var previous = Vector2.Zero;

		// A cubic-ish wiggle through the three dots below.
		for ( var i = 0; i <= 32; i++ )
		{
			var t = i / 32f;
			var x = -8.5f + 17f * t;
			var y = MathF.Sin( t * MathF.PI * 1.6f + 0.4f ) * 5.2f - 1f;
			var point = At( c, x, y );

			if ( i > 0 )
				Editor.Paint.DrawLine( previous, point );

			previous = point;
		}

		Filled( color );

		foreach ( var t in new[] { 0f, 0.5f, 1f } )
		{
			var x = -8.5f + 17f * t;
			var y = MathF.Sin( t * MathF.PI * 1.6f + 0.4f ) * 5.2f - 1f;

			Editor.Paint.DrawRect( Box( c, x - 1.5f, y - 1.5f, 3f, 3f ), 1.5f * _scale );
		}
	}

	/// <summary>Two crossing lines with the stub past the crossing drawn faintly - the piece that
	/// goes. Trim is defined by what it removes, so that is what the glyph shows.</summary>
	private static void PaintTrimTool( Vector2 c, Color color )
	{
		// The cutting line.
		Stroked( color.WithAlpha( 0.55f ), 1.3f );
		Editor.Paint.DrawLine( At( c, 2.5f, -8.5f ), At( c, 2.5f, 8.5f ) );

		// The part that stays.
		Stroked( color, 1.9f );
		Editor.Paint.DrawLine( At( c, -8.5f, 3f ), At( c, 2.5f, 0f ) );

		// The part that goes, dashed.
		Stroked( color.WithAlpha( 0.35f ), 1.5f );
		for ( var t = 0f; t < 1f; t += 0.28f )
		{
			var a = At( c, 2.5f + 6f * t, -1.6f * t );
			var b = At( c, 2.5f + 6f * (t + 0.16f), -1.6f * (t + 0.16f) );

			Editor.Paint.DrawLine( a, b );
		}
	}

	/// <summary>A line reaching a boundary, with the new length drawn faintly and an arrow head -
	/// the mirror of Trim, and drawn as its mirror so the pair reads as a pair.</summary>
	private static void PaintExtendTool( Vector2 c, Color color )
	{
		// The boundary it reaches to.
		Stroked( color.WithAlpha( 0.55f ), 1.3f );
		Editor.Paint.DrawLine( At( c, 6.5f, -8.5f ), At( c, 6.5f, 8.5f ) );

		// What is there now.
		Stroked( color, 1.9f );
		Editor.Paint.DrawLine( At( c, -8.5f, 3f ), At( c, -1f, 1f ) );

		// Where it is going.
		Stroked( color.WithAlpha( 0.4f ), 1.4f );
		Editor.Paint.DrawLine( At( c, -1f, 1f ), At( c, 5f, -0.6f ) );
		ArrowHead( At( c, 6.4f, -1f ), new Vector2( 1f, -0.26f ), color.WithAlpha( 0.75f ), 3f );
	}

	/// <summary>A rounded corner with the square one it replaces dashed behind it.</summary>
	private static void PaintSketchFilletTool( Vector2 c, Color color )
	{
		// The corner that was.
		Stroked( color.WithAlpha( 0.35f ), 1.3f );
		Editor.Paint.DrawLine( At( c, -8, -7 ), At( c, 7, -7 ) );
		Editor.Paint.DrawLine( At( c, 7, -7 ), At( c, 7, 8 ) );

		// The corner that is.
		Stroked( color, 1.9f );
		Editor.Paint.DrawLine( At( c, -8, -7 ), At( c, 0f, -7 ) );
		Arc( At( c, 0f, 0f ), 7f, -90f, 0f, 12 );
		Editor.Paint.DrawLine( At( c, 7, 0f ), At( c, 7, 8 ) );
	}

	/// <summary>A shape and a second one running parallel outside it, which is the whole of what an
	/// offset is - the same curve, held away at a distance.</summary>
	private static void PaintOffsetTool( Vector2 c, Color color )
	{
		// The original.
		Stroked( color, 1.8f );
		Editor.Paint.DrawLine( At( c, -6, 6 ), At( c, -6, -2 ) );
		Arc( At( c, -1.5f, -2f ), 4.5f, 180f, 270f, 10 );
		Editor.Paint.DrawLine( At( c, -1.5f, -6.5f ), At( c, 5, -6.5f ) );

		// Its offset, outside and parallel.
		Stroked( color.WithAlpha( 0.55f ), 1.4f );
		Editor.Paint.DrawLine( At( c, -9.5f, 6 ), At( c, -9.5f, -2 ) );
		Arc( At( c, -1.5f, -2f ), 8f, 180f, 270f, 12 );
		Editor.Paint.DrawLine( At( c, -1.5f, -10f ), At( c, 5, -10f ) );
	}
}
