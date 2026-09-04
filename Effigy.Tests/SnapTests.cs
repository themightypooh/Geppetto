using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Drawing a closed rectangle with the line tool, at every part size anyone would model at.
///
/// This is the bug that cost a night: closed sketches not registering as closed. The tolerances
/// were fixed sketch-unit constants, so on a small part every existing point sat inside the snap
/// radius of every new click. Corners collapsed onto one another, the line tool built a
/// zero-length SketchLine, ProfileFinder linked that one curve into the adjacency map twice at the
/// same point, and a perfectly good corner reported as joining three curves - "branching sketches
/// are not supported yet", no closed region, nothing to extrude.
///
/// The whole failure is reproducible with no engine in sight, which is why it belongs here.
/// </summary>
public static class SnapTests
{
	public static void Run()
	{
		Report.Section( "snapping: a drawn rectangle closes at any part size" );
		TestRectangleClosesAtEveryScale();

		Report.Section( "snapping: the fixed-tolerance version this replaced" );
		TestFixedTolerancesFailOnSmallParts();

		Report.Section( "snapping: tolerance to a sloppy closing click" );
		TestClickTolerance();

		Report.Section( "snapping: automatic grid step" );
		TestAutoGridStep();

		Report.Section( "snapping: point reuse and inference" );
		TestReuseAndInference();

		Report.Section( "snapping: a reported axis lock is true of the point that comes back" );
		TestInferenceIsTrue();

		Report.Section( "snapping: existing sketch curves and their midpoints" );
		TestSnapToExistingCurves();
	}

	/// <summary>How many sketch units a pixel covers with a part of this size framed in a ~700px
	/// viewport. The editor computes this from the camera; here it stands in for "the user framed
	/// the part and started drawing".</summary>
	static float UnitsPerPixel( float partSize ) => partSize * 1.6f / 700f;

	/// <summary>
	/// Draw a rectangle the way the line tool does: four corner clicks plus a fifth that lands
	/// near the first to close it. Returns the sketch so ProfileFinder can judge it.
	/// </summary>
	static Sketch DrawRectangle( float size, SketchSnapper snapper, float closingErrorPixels, float unitsPerPixel )
	{
		var sketch = new Sketch();
		var pending = new List<Vec2>();
		var error = closingErrorPixels * unitsPerPixel;

		var clicks = new[]
		{
			new Vec2( 0, 0 ),
			new Vec2( size, 0 ),
			new Vec2( size, size ),
			new Vec2( 0, size ),
			new Vec2( error, error ),      // aiming at the start point and missing by a few pixels
		};

		foreach ( var raw in clicks )
		{
			var lineInProgress = pending.Count == 1;
			var result = snapper.Snap( sketch, raw, pending, lineInProgress );

			pending.Add( result.Point );

			if ( pending.Count < 2 )
				continue;

			var from = SketchSnapper.PointIndex( sketch, pending[0] );
			var to = SketchSnapper.PointIndex( sketch, pending[1] );

			// The line tool's degenerate guard: a zero-length line is not geometry, and feeding one
			// to ProfileFinder is what turned a corner into a "branching" point.
			if ( from != to )
				sketch.Add( new SketchLine( from, to ) );

			var last = pending[1];
			pending.Clear();
			pending.Add( last );
		}

		return sketch;
	}

	static SketchSnapper ScreenSpace( float unitsPerPixel ) => new()
	{
		PointRadius = 12f * unitsPerPixel,
		AlignmentRadius = 7f * unitsPerPixel,
		GridStep = SketchSnapper.AutoGridStep( unitsPerPixel ),
	};

	static void TestRectangleClosesAtEveryScale()
	{
		foreach ( var size in new[] { 1000f, 100f, 20f, 10f, 5f, 2f, 1f, 0.5f, 0.1f, 0.01f } )
		{
			var upp = UnitsPerPixel( size );
			var sketch = DrawRectangle( size, ScreenSpace( upp ), closingErrorPixels: 4f, upp );
			var found = ProfileFinder.Find( sketch );

			var closed = found.Profiles.Count == 1
				&& found.Warnings.Count == 0
				&& sketch.Points.Count == 4
				&& sketch.Curves.Count == 4;

			Report.Check( $"a {size} unit rectangle closes",
				closed,
				$"{sketch.Points.Count} pts, {sketch.Curves.Count} curves, "
				+ $"{found.Profiles.Count} profiles, {found.Warnings.Count} warnings" );

			if ( found.Profiles.Count == 1 )
			{
				Report.Check( $"a {size} unit rectangle has the right area",
					MathF.Abs( found.Profiles[0].Area - size * size ) < size * size * 1e-3f,
					$"{found.Profiles[0].Area} vs {size * size}" );
			}
		}
	}

	/// <summary>
	/// The old behaviour, kept as a test so the regression is a failing check rather than a night
	/// of clicking. Fixed 4 / 1 / 0.25 tolerances in sketch units, at every scale.
	/// </summary>
	static void TestFixedTolerancesFailOnSmallParts()
	{
		SketchSnapper Fixed() => new() { PointRadius = 4f, AlignmentRadius = 1f, GridStep = 0.25f };

		var brokenBelow = new List<float>();

		foreach ( var size in new[] { 100f, 20f, 10f, 5f, 2f, 1f, 0.5f } )
		{
			var upp = UnitsPerPixel( size );
			var sketch = DrawRectangle( size, Fixed(), closingErrorPixels: 4f, upp );
			var found = ProfileFinder.Find( sketch );

			if ( found.Profiles.Count != 1 || found.Warnings.Count > 0 || sketch.Points.Count != 4 )
				brokenBelow.Add( size );
		}

		Report.Check( "fixed tolerances did break on small parts - this is the bug that was fixed",
			brokenBelow.Count > 0,
			brokenBelow.Count > 0 ? $"broken at sizes: {string.Join( ", ", brokenBelow )}" : "nothing broke" );

		// And the screen-space version survives every one of those same sizes.
		var survived = new[] { 100f, 20f, 10f, 5f, 2f, 1f, 0.5f }.All( size =>
		{
			var upp = UnitsPerPixel( size );
			var sketch = DrawRectangle( size, ScreenSpace( upp ), 4f, upp );
			return ProfileFinder.Find( sketch ).Profiles.Count == 1;
		} );

		Report.Check( "screen-space tolerances survive all of them", survived );
	}

	/// <summary>The closing click is a human aiming at a drawn dot. It has to forgive a few pixels
	/// and refuse a wild miss, at any zoom.</summary>
	static void TestClickTolerance()
	{
		const float size = 1f;
		var upp = UnitsPerPixel( size );

		foreach ( var missPixels in new[] { 0f, 2f, 5f, 8f } )
		{
			var sketch = DrawRectangle( size, ScreenSpace( upp ), missPixels, upp );

			Report.Check( $"a {missPixels}px miss still closes the profile",
				ProfileFinder.Find( sketch ).Profiles.Count == 1,
				$"{sketch.Points.Count} points" );
		}

		// Well outside the 12px radius, it correctly does NOT close - snapping that far would drag
		// clicks the user did not mean.
		var wild = DrawRectangle( size, ScreenSpace( upp ), 40f, upp );

		Report.Check( "a 40px miss does not silently snap closed",
			ProfileFinder.Find( wild ).Profiles.Count == 0,
			$"{ProfileFinder.Find( wild ).Profiles.Count} profiles" );
	}

	static void TestAutoGridStep()
	{
		// The step must stay in the same ballpark on screen however far in you are zoomed.
		foreach ( var upp in new[] { 100f, 1f, 0.01f, 0.0001f } )
		{
			var step = SketchSnapper.AutoGridStep( upp );
			var onScreenPixels = step / upp;

			Report.Check( $"at {upp} units/px the grid is a sane size on screen",
				onScreenPixels >= 7f && onScreenPixels <= 28f, $"{onScreenPixels}px" );

			var mantissa = step / MathF.Pow( 10f, MathF.Floor( MathF.Log10( step ) ) );

			Report.Check( $"at {upp} units/px the step is a 1/2/5 number",
				MathF.Abs( mantissa - 1f ) < 1e-3f || MathF.Abs( mantissa - 2f ) < 1e-3f
				|| MathF.Abs( mantissa - 5f ) < 1e-3f, $"mantissa {mantissa}" );
		}

		Report.Check( "a nonsense zoom yields no grid rather than a NaN one",
			SketchSnapper.AutoGridStep( 0f ) == 0f );
	}

	static void TestReuseAndInference()
	{
		var sketch = new Sketch();
		var a = SketchSnapper.PointIndex( sketch, new Vec2( 1, 1 ) );
		var b = SketchSnapper.PointIndex( sketch, new Vec2( 1, 1 ) );

		Report.Check( "the same coordinate reuses its point", a == b && sketch.Points.Count == 1 );

		var c = SketchSnapper.PointIndex( sketch, new Vec2( 2, 1 ) );

		Report.Check( "a different coordinate makes a new one", c != a && sketch.Points.Count == 2 );

		// A second line click level with the first reports a horizontal lock, which is what the
		// sketcher turns into a Horizontal constraint.
		var snapper = new SketchSnapper { PointRadius = 0.01f, AlignmentRadius = 0.5f, GridStep = 0f };
		var start = new Vec2( 0, 0 );

		var level = snapper.Snap( new Sketch(), new Vec2( 5f, 0.2f ), new[] { start }, lineInProgress: true );

		Report.Check( "a near-level second click locks to horizontal",
			(level.InferenceAxis & 2) != 0 && MathF.Abs( level.Point.y ) < 1e-6f,
			$"axis {level.InferenceAxis}, y {level.Point.y}" );

		var upright = snapper.Snap( new Sketch(), new Vec2( 0.2f, 5f ), new[] { start }, lineInProgress: true );

		Report.Check( "a near-upright second click locks to vertical",
			(upright.InferenceAxis & 1) != 0 && MathF.Abs( upright.Point.x ) < 1e-6f,
			$"axis {upright.InferenceAxis}, x {upright.Point.x}" );

		// Landing on a committed point reports its index, which is what draws the snap ring.
		var withPoint = new Sketch();
		withPoint.AddPoint( new Vec2( 3, 3 ) );

		var onto = snapper.Snap( withPoint, new Vec2( 3.005f, 3.005f ), Array.Empty<Vec2>(), false );

		Report.Check( "landing on a committed point reports its index",
			onto.SnappedPointIndex == 0 && onto.Point.x == 3f && onto.Point.y == 3f,
			$"index {onto.SnappedPointIndex}, point {onto.Point}" );
	}

	/// <summary>
	/// InferenceAxis says one thing and one thing only: THIS POINT SHARES A COORDINATE WITH THE
	/// START OF THE LINE BEING DRAWN.
	///
	/// It is not advisory. The line tool reads bit 1 and adds a real Vertical constraint to the line
	/// it commits (EffigyViewport.Sketching.cs, the SketchToolKind.Line and LineMidpoint cases), and
	/// the sketcher draws its guide through pending[0] on the strength of the same bit. A bit that is
	/// true of something ELSE - of a committed point the cursor happened to land on, of some
	/// unrelated corner the alignment pass lined up with - is not a weaker version of that claim. It
	/// is a false one, and the cost is a rule the geometry breaks: the solver enforces it on the next
	/// rebuild and drags the point off wherever it was put.
	///
	/// Every check here failed before the bits were verified against pending[0].
	/// </summary>
	static void TestInferenceIsTrue()
	{
		const float size = 10f;
		var upp = UnitsPerPixel( size );
		var start = new Vec2( 0f, 0f );

		// --- the two false positives -------------------------------------------------------

		// A committed point sitting a few pixels OFF the vertical through the line's start. The
		// near-vertical aim locks x, and then the point pass overrides it and wins - the click lands
		// on the point, which is right, and the line through it is not vertical, which the bit used
		// to keep insisting it was.
		var offAxis = new Sketch();
		offAxis.AddPoint( new Vec2( 4f * upp, 5f ) );

		var ontoPoint = ScreenSpace( upp )
			.Snap( offAxis, new Vec2( 6f * upp, 5f ), new[] { start }, lineInProgress: true );

		Report.Check( "snapping onto an off-axis point drops the vertical lock",
			ontoPoint.SnappedPointIndex == 0 && (ontoPoint.InferenceAxis & 1) == 0,
			$"index {ontoPoint.SnappedPointIndex}, axis {ontoPoint.InferenceAxis}, x {ontoPoint.Point.x}" );

		// Lining up with the x of SOME OTHER point. A useful snap - it is how you get a corner
		// directly above another one - and it says nothing whatever about the line from the start
		// point to here being vertical, because that other x is not the start point's x.
		var elsewhere = new Sketch();
		elsewhere.AddPoint( new Vec2( 3f, 7f ) );

		var linedUp = ScreenSpace( upp )
			.Snap( elsewhere, new Vec2( 3f + 2f * upp, 2f ), new[] { start }, lineInProgress: true );

		Report.Check( "lining up with an unrelated point's x is not a vertical line",
			(linedUp.InferenceAxis & 1) == 0,
			$"axis {linedUp.InferenceAxis}, x {linedUp.Point.x} vs start {start.x}" );

		Report.Check( "and the snap itself still happens - only the claim was dropped",
			MathF.Abs( linedUp.Point.x - 3f ) < 1e-4f, $"x {linedUp.Point.x}" );

		// --- the true positives, which must survive ----------------------------------------

		var real = ScreenSpace( upp )
			.Snap( new Sketch(), new Vec2( 3f * upp, 5f ), new[] { start }, lineInProgress: true );

		Report.Check( "a genuinely near-vertical click still locks and still reports it",
			(real.InferenceAxis & 1) != 0 && MathF.Abs( real.Point.x - start.x ) < 1e-6f,
			$"axis {real.InferenceAxis}, x {real.Point.x}" );

		// THE GRID USED TO CANCEL THE LOCK. The line pass puts the cursor exactly on the start's x;
		// rounding then puts it on the nearest grid line, which is the same number only when the
		// start happens to sit on the grid. It usually does - it was grid-snapped too - so this hid
		// until the start came from somewhere else, a committed corner or a face's vertex.
		var offGrid = new Vec2( 0.07f, 0f );

		var throughGrid = ScreenSpace( upp )
			.Snap( new Sketch(), offGrid + new Vec2( 3f * upp, 5f ), new[] { offGrid }, lineInProgress: true );

		Report.Check( "an off-grid start still gives a truly vertical line",
			(throughGrid.InferenceAxis & 1) != 0
			&& MathF.Abs( throughGrid.Point.x - offGrid.x ) < 1e-6f,
			$"axis {throughGrid.InferenceAxis}, x {throughGrid.Point.x} vs start {offGrid.x}" );

		// --- the postcondition, swept ------------------------------------------------------

		// The property itself rather than three examples of it: wherever the cursor is, whatever it
		// lands on, a reported bit is true of the point that came back. Run over a sketch with
		// enough in it that every pass gets a chance to fire.
		var busy = new Sketch();
		busy.AddPoint( new Vec2( 0f, 0f ) );
		busy.AddPoint( new Vec2( 3f, 7f ) );
		busy.AddPoint( new Vec2( 4f * upp, 5f ) );
		busy.AddPoint( new Vec2( -2.5f, 1.25f ) );

		var pendingStart = new Vec2( 0.07f, 0.13f );
		var lies = 0;
		var locks = 0;

		for ( var ix = -40; ix <= 40; ix++ )
		{
			for ( var iy = -40; iy <= 40; iy++ )
			{
				var raw = new Vec2( ix * 0.19f, iy * 0.17f );
				var result = ScreenSpace( upp ).Snap( busy, raw, new[] { pendingStart }, lineInProgress: true );

				if ( result.InferenceAxis == 0 )
					continue;

				locks++;

				if ( (result.InferenceAxis & 1) != 0 && MathF.Abs( result.Point.x - pendingStart.x ) > 1e-4f )
					lies++;
				else if ( (result.InferenceAxis & 2) != 0 && MathF.Abs( result.Point.y - pendingStart.y ) > 1e-4f )
					lies++;
			}
		}

		Report.Check( "over 6561 clicks, every reported lock is true of the point returned",
			lies == 0, $"{lies} false locks out of {locks} reported" );

		// And the sweep is worth something: it has to have found locks to report on.
		Report.Check( "the sweep did exercise the inference passes", locks > 100, $"{locks} locks" );
	}

	/// <summary>
	/// Drawing a line to an edge that is already there has to land ON that edge, not next to it.
	/// Corners still win, midpoints are a discrete target, and a curve snap is off when the
	/// radius is zero so existing tests that never set it keep their old answers.
	/// </summary>
	static void TestSnapToExistingCurves()
	{
		var sketch = new Sketch();
		sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 10, 10 ) );

		var snapper = new SketchSnapper
		{
			PointRadius = 0.5f,
			AlignmentRadius = 0.2f,
			GridStep = 0f,
			CurveRadius = 0.5f,
		};

		var mid = snapper.Snap( sketch, new Vec2( 5.1f, 0.15f ), Array.Empty<Vec2>(), false );

		Report.Check( "a click near the middle of an edge lands on the midpoint",
			MathF.Abs( mid.Point.x - 5f ) < 1e-4f && MathF.Abs( mid.Point.y ) < 1e-4f,
			$"landed at {mid.Point}" );

		Report.Check( "and names the curve, not a corner",
			mid.SnappedCurveIndex >= 0 && mid.SnappedPointIndex < 0,
			$"curve {mid.SnappedCurveIndex}, point {mid.SnappedPointIndex}" );

		var along = snapper.Snap( sketch, new Vec2( 3.2f, 0.2f ), Array.Empty<Vec2>(), false );

		Report.Check( "a click along an edge, off the midpoint, still lands on the edge",
			MathF.Abs( along.Point.y ) < 1e-4f && along.Point.x > 2.5f && along.Point.x < 4f,
			$"landed at {along.Point}" );

		Report.Check( "and still names the curve",
			along.SnappedCurveIndex >= 0, $"curve {along.SnappedCurveIndex}" );

		var corner = snapper.Snap( sketch, new Vec2( 0.1f, 0.1f ), Array.Empty<Vec2>(), false );

		Report.Check( "a click near a corner still prefers the corner",
			corner.SnappedPointIndex >= 0 && MathF.Abs( corner.Point.x ) < 1e-4f && MathF.Abs( corner.Point.y ) < 1e-4f,
			$"index {corner.SnappedPointIndex}, point {corner.Point}" );

		var start = new Vec2( 5f, 0f );
		var sameEdge = snapper.Snap( sketch, new Vec2( 7f, 0.1f ), new[] { start }, lineInProgress: true );

		Report.Check( "the far click of a line does not snap back onto the edge it started on",
			sameEdge.SnappedCurveIndex < 0 || MathF.Abs( sameEdge.Point.y ) > 1e-3f,
			$"curve {sameEdge.SnappedCurveIndex}, point {sameEdge.Point}" );

		var opposite = snapper.Snap( sketch, new Vec2( 5.1f, 9.85f ), new[] { start }, lineInProgress: true );

		Report.Check( "but it does snap onto the opposite edge",
			opposite.SnappedCurveIndex >= 0 && MathF.Abs( opposite.Point.y - 10f ) < 1e-4f,
			$"curve {opposite.SnappedCurveIndex}, point {opposite.Point}" );

		// A diagonal, so alignment with the endpoints cannot fake an on-curve landing. An
		// axis-aligned edge sits on the same y as its corners, and the alignment pass would
		// pull a near-edge click onto that y whether CurveRadius was set or not.
		var diagonal = new Sketch();
		diagonal.Add( new SketchLine( diagonal.AddPoint( 0, 0 ), diagonal.AddPoint( 10, 5 ) ) );

		var off = new Vec2( 5f, 3f );
		var blind = new SketchSnapper { PointRadius = 0.4f, AlignmentRadius = 0.2f, GridStep = 0f, CurveRadius = 0f };
		var missed = blind.Snap( diagonal, off, Array.Empty<Vec2>(), false );

		Report.Check( "with CurveRadius zero, a near-curve click is not pulled onto the curve",
			missed.SnappedCurveIndex < 0 && MathF.Abs( missed.Point.y - 3f ) < 1e-4f,
			$"curve {missed.SnappedCurveIndex}, point {missed.Point}" );

		var seeing = new SketchSnapper { PointRadius = 0.4f, AlignmentRadius = 0.2f, GridStep = 0f, CurveRadius = 0.6f };
		var pulled = seeing.Snap( diagonal, off, Array.Empty<Vec2>(), false );
		var onLine = SketchSnapper.ClosestOnSegment( new Vec2( 0, 0 ), new Vec2( 10, 5 ), off );

		Report.Check( "with CurveRadius set, the same click lands on the diagonal",
			pulled.SnappedCurveIndex >= 0 && (pulled.Point - onLine).LengthSquared < 1e-6f,
			$"curve {pulled.SnappedCurveIndex}, point {pulled.Point}, expected {onLine}" );
	}
}
