using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>What is selected in a sketch right now: some points, some curves, or both.</summary>
public sealed class SketchSelection
{
	public List<int> Points = new();
	public List<string> Curves = new();

	public bool IsEmpty => Points.Count == 0 && Curves.Count == 0;

	public SketchSelection() { }

	public SketchSelection( IEnumerable<int> points, IEnumerable<string> curves = null )
	{
		if ( points is not null )
			Points.AddRange( points );

		if ( curves is not null )
			Curves.AddRange( curves );
	}
}

/// <summary>
/// One constraint the user could apply to what they have selected, ready to add.
///
/// It carries a MEASURED value for the kinds that take one, so a dimension box opens showing what
/// the sketch currently is rather than a zero. Typing over it is how a dimension gets driven; leaving
/// it is how a dimension locks what is already there, which is most of them.
/// </summary>
public sealed class ConstraintOffer
{
	public SketchConstraintKind Kind;

	/// <summary>What to put on the button.</summary>
	public string Label;

	/// <summary>One line saying what it will do to the geometry, for a tooltip or status bar.</summary>
	public string Hint;

	/// <summary>Whether this one takes a number — a dimension rather than a plain rule.</summary>
	public bool NeedsValue;

	/// <summary>The measured value as things stand. Zero for the kinds that take no value.</summary>
	public float Value;

	/// <summary>"" for a length, "deg" for an angle. What a numeric field should show.</summary>
	public string Unit = "";

	/// <summary>The constraint itself, indices resolved and Value filled in.</summary>
	public SketchConstraint Constraint;
}


/// <summary>
/// One mark to draw on the sketch, saying that a rule holds here.
///
/// The kernel decides WHERE and WHAT, and stops there. It does not decide how far off the geometry
/// the mark sits, because that is a screen distance — a sketch can be one unit across or a thousand,
/// and a glyph pushed a fixed number of sketch units away is either buried in the line or somewhere
/// off in the next county. So this gives an anchor on the geometry and a UNIT direction to leave
/// along, and the viewport scales it by its own pixels-to-units.
/// </summary>
public sealed class ConstraintMarker
{
	/// <summary>The rule this marks. Clicking the glyph is how a rule gets deleted, so the caller
	/// needs the object itself, not a copy of its contents.</summary>
	public SketchConstraint Constraint;

	public SketchConstraintKind Kind;

	/// <summary>Where on the geometry the mark belongs.</summary>
	public Vec2 Anchor;

	/// <summary>Unit direction to push the glyph clear of the geometry. Zero when there is no
	/// meaningful "off to one side" — a coincidence is a point, and a point has no sides.</summary>
	public Vec2 Away;

	/// <summary>What to write. Short and ASCII-safe on purpose: this is drawn with the viewport's
	/// screen-text call in whatever font the editor has, and a glyph that renders as a box says
	/// less than nothing.</summary>
	public string Label;

	/// <summary>Whether the label is a NUMBER the user chose rather than a rule they named. Worth
	/// telling apart on screen — a dimension is a thing you double-click to change, a rule is a
	/// thing you delete.</summary>
	public bool IsDimension;
}

/// <summary>What happened when a constraint was applied.</summary>
public sealed class ApplyResult
{
	/// <summary>It went on and the sketch still solves.</summary>
	public bool Applied;

	/// <summary>Why not, when it did not. Null on success.</summary>
	public string Message;

	/// <summary>The solve it produced — where the degrees of freedom left come from.</summary>
	public SolveResult Solve;
}

/// <summary>
/// The layer between "what the user has selected" and "which constraints that allows".
///
/// The solver has been able to satisfy eleven kinds of constraint for some time, and there has been
/// no way to add one in the editor — so in practice it only ever ran on what the drawing inference
/// put there. This is the missing half: given a selection, what can be applied, what would it do,
/// and what is the sketch measuring right now.
///
/// It lives in the kernel rather than in the editor because it is all rules and no drawing, and the
/// rules are where the mistakes are: which point of three is the one that lies on the line, whether
/// two arcs can be made equal, whether the thing being offered is already there. None of that needs
/// a viewport to get wrong, so none of it needs a viewport to test.
/// </summary>
public static class ConstraintTools
{
	/// <summary>
	/// Everything applicable to this selection, in the order a toolbar should show it.
	///
	/// Nothing the sketch ALREADY HAS is offered. Adding a second copy of a constraint is the
	/// classic way to end up with a sketch that solves fine and reports redundancy, and then to
	/// wonder why the next dimension appears to do nothing — SolveResult.RedundantConstraints exists
	/// to diagnose exactly that, and not offering the duplicate in the first place is better.
	/// </summary>
	public static List<ConstraintOffer> Offers( Sketch sketch, SketchSelection selection )
	{
		var offers = new List<ConstraintOffer>();

		if ( sketch is null || selection is null || selection.IsEmpty )
			return offers;

		var lines = new List<SketchLine>();
		var arcs = new List<SketchArc>();
		var circles = new List<SketchCircle>();

		foreach ( var id in selection.Curves.Distinct() )
		{
			var curve = sketch.Curves.FirstOrDefault( c => c.Id == id );

			switch ( curve )
			{
				case SketchLine line: lines.Add( line ); break;
				case SketchArc arc: arcs.Add( arc ); break;
				case SketchCircle circle: circles.Add( circle ); break;
			}
		}

		var points = selection.Points.Distinct().Where( p => p >= 0 && p < sketch.Points.Count ).ToList();

		// --- one line ---------------------------------------------------------------------------
		if ( lines.Count == 1 && points.Count == 0 && arcs.Count == 0 && circles.Count == 0 )
		{
			var line = lines[0];

			Add( offers, sketch, SketchConstraintKind.Horizontal, "Horizontal",
				"Lay this line level.", new SketchConstraint( SketchConstraintKind.Horizontal, line.Start, line.End ) );

			Add( offers, sketch, SketchConstraintKind.Vertical, "Vertical",
				"Stand this line upright.", new SketchConstraint( SketchConstraintKind.Vertical, line.Start, line.End ) );

			AddValued( offers, sketch, SketchConstraintKind.Distance, "Length",
				"Drive this line's length.", Length( sketch, line.Start, line.End ), "",
				new SketchConstraint( SketchConstraintKind.Distance, line.Start, line.End ) );
		}

		// --- two lines --------------------------------------------------------------------------
		if ( lines.Count == 2 && points.Count == 0 )
		{
			var a = lines[0];
			var b = lines[1];

			Add( offers, sketch, SketchConstraintKind.Parallel, "Parallel",
				"Make these two lines run the same way.",
				new SketchConstraint( SketchConstraintKind.Parallel, a.Start, a.End, b.Start, b.End ) );

			Add( offers, sketch, SketchConstraintKind.Perpendicular, "Perpendicular",
				"Set a right angle between these two lines.",
				new SketchConstraint( SketchConstraintKind.Perpendicular, a.Start, a.End, b.Start, b.End ) );

			Add( offers, sketch, SketchConstraintKind.EqualLength, "Equal length",
				"Make these two lines the same length.",
				new SketchConstraint( SketchConstraintKind.EqualLength, a.Start, a.End, b.Start, b.End ) );

			AddValued( offers, sketch, SketchConstraintKind.Angle, "Angle",
				"Drive the angle between these two lines.",
				AngleBetween( sketch, a, b ), "deg",
				new SketchConstraint( SketchConstraintKind.Angle, a.Start, a.End, b.Start, b.End ) );
		}

		// --- arcs -------------------------------------------------------------------------------
		//
		// ARCS ONLY, NOT CIRCLES. An arc's radius is the distance between two of its points, so the
		// solver can drive it. A circle stores its radius as a plain float and contributes only its
		// centre to the solve — there is no second point for a radius constraint to act on, so a
		// circle's radius is an EDIT rather than a constraint, and offering one here would be
		// offering something that silently does nothing.
		if ( arcs.Count == 1 && points.Count == 0 && lines.Count == 0 )
		{
			var arc = arcs[0];

			AddValued( offers, sketch, SketchConstraintKind.Radius, "Radius",
				"Drive this arc's radius.", Length( sketch, arc.Center, arc.Start ), "",
				new SketchConstraint( SketchConstraintKind.Radius, arc.Center, arc.Start ) );
		}

		if ( arcs.Count == 2 && points.Count == 0 && lines.Count == 0 )
		{
			var a = arcs[0];
			var b = arcs[1];

			Add( offers, sketch, SketchConstraintKind.EqualLength, "Equal radius",
				"Make these two arcs the same size.",
				new SketchConstraint( SketchConstraintKind.EqualLength, a.Center, a.Start, b.Center, b.Start ) );
		}

		// --- two points -------------------------------------------------------------------------
		if ( points.Count == 2 && lines.Count == 0 && arcs.Count == 0 )
		{
			Add( offers, sketch, SketchConstraintKind.Coincident, "Coincident",
				"Bring these two points together.",
				new SketchConstraint( SketchConstraintKind.Coincident, points[0], points[1] ) );

			AddValued( offers, sketch, SketchConstraintKind.Distance, "Distance",
				"Drive the distance between these two points.",
				Length( sketch, points[0], points[1] ), "",
				new SketchConstraint( SketchConstraintKind.Distance, points[0], points[1] ) );

			Add( offers, sketch, SketchConstraintKind.Horizontal, "Horizontal",
				"Line these two points up level.",
				new SketchConstraint( SketchConstraintKind.Horizontal, points[0], points[1] ) );

			Add( offers, sketch, SketchConstraintKind.Vertical, "Vertical",
				"Line these two points up one above the other.",
				new SketchConstraint( SketchConstraintKind.Vertical, points[0], points[1] ) );
		}

		// --- a point and a line -------------------------------------------------------------------
		//
		// UNAMBIGUOUS BY CONSTRUCTION. Three loose points would also describe this, and then which of
		// the three is the one that has to lie on the other two is a guess dressed up as a
		// convention. Selecting the point and the line says it outright.
		if ( points.Count == 1 && lines.Count == 1 && arcs.Count == 0 )
		{
			var line = lines[0];

			Add( offers, sketch, SketchConstraintKind.PointOnLine, "Point on line",
				"Put this point on that line.",
				new SketchConstraint( SketchConstraintKind.PointOnLine, points[0], line.Start, line.End ) );
		}

		// --- two points and a line ------------------------------------------------------------
		if ( points.Count == 2 && lines.Count == 1 && arcs.Count == 0 )
		{
			var line = lines[0];

			Add( offers, sketch, SketchConstraintKind.Symmetric, "Symmetric",
				"Mirror these two points about that line.",
				new SketchConstraint( SketchConstraintKind.Symmetric, points[0], points[1], line.Start, line.End ) );
		}

		return offers;
	}

	/// <summary>
	/// Apply a constraint and re-solve, putting everything back if it cannot be met.
	///
	/// THE SOLVER KEEPS ITS CLOSEST FIT ON A FAILED SOLVE, which is right while a sketch is being
	/// dragged through contradictory states mid-edit and wrong for a deliberate request. The user
	/// asked for one specific rule; if it cannot hold, leaving it on the sketch means every later
	/// solve carries a contradiction they never see, and leaving the half-converged POSITIONS means
	/// their geometry moved to nearly satisfy a rule that was then reported as failed.
	///
	/// So a failure restores the point positions from before the attempt, exactly, rather than
	/// removing the rule and solving again — solving again converges to *a* valid answer, which is
	/// not necessarily the one that was on screen a moment ago.
	///
	/// The pin is the caller's, because it decides what visibly stays put: the editor pins whatever
	/// the user just selected, so the sketch resolves around their attention rather than around
	/// point 0, which is wherever they happened to click first.
	/// </summary>
	public static ApplyResult ApplyAndSolve( Sketch sketch, ConstraintOffer offer, int pinnedPoint = 0 )
	{
		if ( sketch is null || offer?.Constraint is null )
			return new ApplyResult { Message = "Nothing to apply." };

		if ( Has( sketch, offer.Constraint ) )
			return new ApplyResult { Message = $"The sketch already says {offer.Label.ToLowerInvariant()}." };

		var before = new List<Vec2>( sketch.Points );

		offer.Constraint.Value = offer.Value;
		sketch.Constraints.Add( offer.Constraint );

		var solve = SketchSolver.Solve( sketch, pinnedPoint );

		if ( solve.Converged )
			return new ApplyResult { Applied = true, Solve = solve };

		sketch.Constraints.Remove( offer.Constraint );

		for ( var i = 0; i < sketch.Points.Count && i < before.Count; i++ )
			sketch.Points[i] = before[i];

		return new ApplyResult
		{
			Solve = solve,
			Message = $"{offer.Label} could not be satisfied — it contradicts something already on the sketch.",
		};
	}

	/// <summary>
	/// Add a constraint to the sketch, unless it is already there.
	///
	/// Returns whether it landed, so a caller can tell "done" from "you already have that" without
	/// comparing constraint lists itself. Does NOT solve — ApplyAndSolve is the one to reach for
	/// when the result has to be checked.
	/// </summary>
	public static bool Apply( Sketch sketch, ConstraintOffer offer )
	{
		if ( sketch is null || offer?.Constraint is null )
			return false;

		if ( Has( sketch, offer.Constraint ) )
			return false;

		offer.Constraint.Value = offer.Value;
		sketch.Constraints.Add( offer.Constraint );

		return true;
	}

	/// <summary>Every constraint that mentions this point — what a UI shows when one is selected,
	/// and what it offers to delete.</summary>
	public static List<SketchConstraint> Touching( Sketch sketch, int point )
	{
		var found = new List<SketchConstraint>();

		if ( sketch is null )
			return found;

		foreach ( var c in sketch.Constraints )
		{
			if ( c.PointA == point || c.PointB == point || c.PointC == point || c.PointD == point )
				found.Add( c );
		}

		return found;
	}

	/// <summary>Every constraint that mentions any point of this curve, including the old
	/// curve-id form that Horizontal and Vertical were stored in before the solver existed.</summary>
	public static List<SketchConstraint> Touching( Sketch sketch, SketchCurve curve )
	{
		var found = new List<SketchConstraint>();

		if ( sketch is null || curve is null )
			return found;

		var points = curve.PointRefs.ToHashSet();

		foreach ( var c in sketch.Constraints )
		{
			if ( c.CurveId == curve.Id )
			{
				found.Add( c );
				continue;
			}

			if ( points.Contains( c.PointA ) || points.Contains( c.PointB )
				|| points.Contains( c.PointC ) || points.Contains( c.PointD ) )
				found.Add( c );
		}

		return found;
	}

	/// <summary>Whether the sketch already says this. Order-insensitive where the constraint is:
	/// "A parallel to B" and "B parallel to A" are one rule, and offering the second is how a
	/// sketch acquires redundancy that nobody meant.</summary>
	public static bool Has( Sketch sketch, SketchConstraint candidate )
	{
		if ( sketch is null || candidate is null )
			return false;

		foreach ( var existing in sketch.Constraints )
		{
			if ( existing.Kind != candidate.Kind )
				continue;

			if ( Same( existing, candidate ) )
				return true;
		}

		return false;
	}


	// --- marks on the sketch -----------------------------------------------------------------

	/// <summary>
	/// Every mark to draw for a sketch's constraints.
	///
	/// A rule that relates two segments gets a mark on EACH of them, which is the only way to read
	/// "these two are parallel" off a drawing — one glyph in the middle would leave you guessing
	/// which pair it meant on a sketch with six lines in it.
	///
	/// A constraint whose points no longer exist yields nothing and does not throw. That state is
	/// ordinary rather than exceptional: deleting a curve leaves rules behind referring to points
	/// that went with it, and Build() already drops them at solve time for the same reason.
	/// </summary>
	public static List<ConstraintMarker> Markers( Sketch sketch )
	{
		var markers = new List<ConstraintMarker>();

		if ( sketch is null )
			return markers;

		foreach ( var c in sketch.Constraints )
			AddMarkers( sketch, c, markers );

		return markers;
	}

	static void AddMarkers( Sketch sketch, SketchConstraint c, List<ConstraintMarker> markers )
	{
		// The old curve-id form, from before there was a solver: resolve it to the line's endpoints
		// so it marks up the same as everything else.
		var a = c.PointA;
		var b = c.PointB;

		if ( a < 0 && c.CurveId is not null )
		{
			if ( sketch.Curves.FirstOrDefault( x => x.Id == c.CurveId ) is not SketchLine line )
				return;

			a = line.Start;
			b = line.End;
		}

		switch ( c.Kind )
		{
			case SketchConstraintKind.Horizontal:
				OnSegment( sketch, c, a, b, "H", markers );
				return;

			case SketchConstraintKind.Vertical:
				OnSegment( sketch, c, a, b, "V", markers );
				return;

			case SketchConstraintKind.Coincident:
				if ( !InRange( sketch, a ) )
					return;

				markers.Add( new ConstraintMarker
				{
					Constraint = c,
					Kind = c.Kind,
					Anchor = sketch.Points[a],
					Away = Vec2.Zero,
					Label = "\u2022",
				} );
				return;

			case SketchConstraintKind.Distance:
				OnSegment( sketch, c, a, b, Number( c.Value ), markers, dimension: true );
				return;

			case SketchConstraintKind.Radius:
				OnSegment( sketch, c, a, b, $"R {Number( c.Value )}", markers, dimension: true );
				return;

			case SketchConstraintKind.EqualLength:
				OnSegment( sketch, c, c.PointA, c.PointB, "=", markers );
				OnSegment( sketch, c, c.PointC, c.PointD, "=", markers );
				return;

			case SketchConstraintKind.Parallel:
				OnSegment( sketch, c, c.PointA, c.PointB, "//", markers );
				OnSegment( sketch, c, c.PointC, c.PointD, "//", markers );
				return;

			case SketchConstraintKind.Perpendicular:
				// "90" rather than the perpendicular sign. This is drawn in whatever font the editor
				// has and U+22A5 is not something to bet the readability of a glyph on; a right angle
				// written as a number is unmistakable and is Latin-1.
				OnSegment( sketch, c, c.PointA, c.PointB, "90\u00b0", markers );
				OnSegment( sketch, c, c.PointC, c.PointD, "90\u00b0", markers );
				return;

			case SketchConstraintKind.Angle:
				AtCrossing( sketch, c, markers );
				return;

			case SketchConstraintKind.PointOnLine:
				if ( !InRange( sketch, c.PointA ) )
					return;

				markers.Add( new ConstraintMarker
				{
					Constraint = c,
					Kind = c.Kind,
					Anchor = sketch.Points[c.PointA],
					Away = Vec2.Zero,
					Label = "ON",
				} );
				return;

			case SketchConstraintKind.Symmetric:
				// The two points being mirrored, not the mirror. Marking the axis would say "this
				// line is involved in a symmetry" without saying what is symmetric about it.
				foreach ( var point in new[] { c.PointA, c.PointB } )
				{
					if ( !InRange( sketch, point ) )
						continue;

					markers.Add( new ConstraintMarker
					{
						Constraint = c,
						Kind = c.Kind,
						Anchor = sketch.Points[point],
						Away = Vec2.Zero,
						Label = "><",
					} );
				}
				return;
		}
	}

	/// <summary>A mark at a segment's midpoint, pushed off to its left. Left every time rather than
	/// "away from the other line", so two marks on the same line never land on top of each other and
	/// the glyph does not jump sides when the geometry is dragged past straight.</summary>
	static void OnSegment( Sketch sketch, SketchConstraint c, int a, int b, string label,
		List<ConstraintMarker> markers, bool dimension = false )
	{
		if ( !InRange( sketch, a ) || !InRange( sketch, b ) )
			return;

		var from = sketch.Points[a];
		var to = sketch.Points[b];
		var along = to - from;

		markers.Add( new ConstraintMarker
		{
			Constraint = c,
			Kind = c.Kind,
			Anchor = (from + to) * 0.5f,
			Away = along.Length < 1e-9f ? Vec2.Zero : new Vec2( -along.y, along.x ).Normal,
			Label = label,
			IsDimension = dimension,
		} );
	}

	/// <summary>
	/// An angle is marked where its two lines actually cross, which is where a person looks for it.
	///
	/// Two lines that meet at a corner have that crossing at the corner; two that do not touch have
	/// it out in space where the extended lines would meet, which is still the right place — that is
	/// what the angle between them means. Only genuinely parallel lines have no crossing at all, and
	/// then the mark falls back to sitting between the two midpoints.
	/// </summary>
	static void AtCrossing( Sketch sketch, SketchConstraint c, List<ConstraintMarker> markers )
	{
		if ( !InRange( sketch, c.PointA ) || !InRange( sketch, c.PointB )
			|| !InRange( sketch, c.PointC ) || !InRange( sketch, c.PointD ) )
			return;

		var p = sketch.Points[c.PointA];
		var r = sketch.Points[c.PointB] - p;
		var q = sketch.Points[c.PointC];
		var s = sketch.Points[c.PointD] - q;

		var denominator = Vec2.Cross( r, s );

		var anchor = MathF.Abs( denominator ) < 1e-9f
			? ((p + sketch.Points[c.PointB]) * 0.5f + (q + sketch.Points[c.PointD]) * 0.5f) * 0.5f
			: p + r * (Vec2.Cross( q - p, s ) / denominator);

		markers.Add( new ConstraintMarker
		{
			Constraint = c,
			Kind = c.Kind,
			Anchor = anchor,
			Away = Vec2.Zero,
			Label = $"{Number( c.Value )}\u00b0",
			IsDimension = true,
		} );
	}

	static bool InRange( Sketch sketch, int point ) => point >= 0 && point < sketch.Points.Count;

	/// <summary>Enough digits to be useful without becoming noise. Sketch units are dimensionless,
	/// so there is no sensible fixed precision — it scales to the value, the same way the viewport's
	/// live readouts do.</summary>
	static string Number( float value ) =>
		MathF.Abs( value ) >= 100f ? value.ToString( "F1" )
		: MathF.Abs( value ) >= 10f ? value.ToString( "F2" )
		: value.ToString( "F3" );

	// --- the equality that matters ---------------------------------------------------------------

	static bool Same( SketchConstraint a, SketchConstraint b )
	{
		// The pair kinds relate two SEGMENTS, so each segment's endpoints may be given either way
		// round and the two segments may be given in either order.
		if ( a.PointD >= 0 || b.PointD >= 0 )
		{
			// Symmetric is the exception among the four-point kinds: the first pair are the points
			// being mirrored and the second is the mirror. Swapping the pairs means something else.
			if ( a.Kind == SketchConstraintKind.Symmetric )
				return SamePair( a.PointA, a.PointB, b.PointA, b.PointB )
					&& SamePair( a.PointC, a.PointD, b.PointC, b.PointD );

			var direct = SamePair( a.PointA, a.PointB, b.PointA, b.PointB )
				&& SamePair( a.PointC, a.PointD, b.PointC, b.PointD );

			var swapped = SamePair( a.PointA, a.PointB, b.PointC, b.PointD )
				&& SamePair( a.PointC, a.PointD, b.PointA, b.PointB );

			return direct || swapped;
		}

		if ( a.PointC >= 0 || b.PointC >= 0 )
		{
			// Point-on-line: the point is fixed, the line's two ends are interchangeable.
			return a.PointA == b.PointA && SamePair( a.PointB, a.PointC, b.PointB, b.PointC );
		}

		if ( a.PointA >= 0 && b.PointA >= 0 )
			return SamePair( a.PointA, a.PointB, b.PointA, b.PointB );

		return a.CurveId is not null && a.CurveId == b.CurveId;
	}

	static bool SamePair( int a0, int a1, int b0, int b1 ) =>
		(a0 == b0 && a1 == b1) || (a0 == b1 && a1 == b0);

	// --- measuring what is there now ------------------------------------------------------------

	static float Length( Sketch sketch, int a, int b ) =>
		(sketch.Points[b] - sketch.Points[a]).Length;

	/// <summary>
	/// The angle between two lines, in degrees, as a dimension should read it.
	///
	/// Folded into 0..180 and taken between the UNDIRECTED lines, because which end of a line was
	/// drawn first is not something the user chose and should not decide whether their angle reads
	/// 30 or 150. AngleConstraint's residual works on the directed vectors, so the constraint it
	/// gets stored with means exactly what was measured here.
	/// </summary>
	static float AngleBetween( Sketch sketch, SketchLine a, SketchLine b )
	{
		var u = sketch.Points[a.End] - sketch.Points[a.Start];
		var v = sketch.Points[b.End] - sketch.Points[b.Start];

		if ( u.Length < 1e-6f || v.Length < 1e-6f )
			return 0f;

		var dot = Vec2.Dot( u.Normal, v.Normal );
		var cross = Vec2.Cross( u.Normal, v.Normal );

		var degrees = MathF.Atan2( cross, dot ) * (180f / MathF.PI );

		if ( degrees < 0f )
			degrees += 360f;

		return degrees;
	}

	// --- building offers -------------------------------------------------------------------------

	static void Add( List<ConstraintOffer> offers, Sketch sketch, SketchConstraintKind kind,
		string label, string hint, SketchConstraint constraint )
	{
		if ( Has( sketch, constraint ) )
			return;

		offers.Add( new ConstraintOffer
		{
			Kind = kind,
			Label = label,
			Hint = hint,
			Constraint = constraint,
		} );
	}

	static void AddValued( List<ConstraintOffer> offers, Sketch sketch, SketchConstraintKind kind,
		string label, string hint, float value, string unit, SketchConstraint constraint )
	{
		if ( Has( sketch, constraint ) )
			return;

		constraint.Value = value;

		offers.Add( new ConstraintOffer
		{
			Kind = kind,
			Label = label,
			Hint = hint,
			NeedsValue = true,
			Value = value,
			Unit = unit,
			Constraint = constraint,
		} );
	}
}
