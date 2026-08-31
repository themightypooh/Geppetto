using System;

namespace Effigy;

// The constraint set. Every one of these is a residual that is zero when the rule holds, plus the
// derivative of that residual against each point it touches.
//
// THE DERIVATIVES ARE THE WHOLE FILE. Get a residual wrong and the solver converges confidently to
// the wrong shape; get a derivative wrong and it converges slowly, or oscillates, or wanders off —
// and none of that looks like a signed error, it looks like "the solver is flaky". They are checked
// two ways in ConstraintTests: against a central finite difference of the residual itself, which
// catches a wrong sign or a swapped term, and by solving a sketch whose answer is known in closed
// form. Add a constraint here and add both.
//
// Sign convention throughout: residuals are (what it is) − (what it should be), and points are
// addressed by index into Sketch.Points.

/// <summary>Two points occupy the same location. Two rows, because x and y are independently
/// wrong.</summary>
public sealed class CoincidentConstraint : IConstraint
{
	public readonly int A, B;

	public CoincidentConstraint( int a, int b )
	{
		A = a;
		B = b;
	}

	public int ResidualCount => 2;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var pa = points[A];
		var pb = points[B];

		output[0] = new ConstraintResult( (double)pa.x - pb.x, new[] { (A, 1.0, 0.0), (B, -1.0, 0.0) } );
		output[1] = new ConstraintResult( (double)pa.y - pb.y, new[] { (A, 0.0, 1.0), (B, 0.0, -1.0) } );
	}
}

/// <summary>The distance between two points equals Value. This is the dimension a user types.</summary>
public sealed class DistanceConstraint : IConstraint
{
	public readonly int A, B;
	public readonly double Value;

	public DistanceConstraint( int a, int b, double value )
	{
		A = a;
		B = b;
		Value = value;
	}

	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var dx = (double)points[A].x - points[B].x;
		var dy = (double)points[A].y - points[B].y;
		var d = Math.Sqrt( dx * dx + dy * dy );

		// Coincident points have no direction to separate along and the true derivative is
		// undefined. Picking +X keeps the Jacobian finite and the next step breaks the tie; leaving
		// it as 0/0 would put a NaN into H and take the whole solve with it.
		if ( d < 1e-12 )
		{
			output[0] = new ConstraintResult( -Value, new[] { (A, 1.0, 0.0), (B, -1.0, 0.0) } );
			return;
		}

		var inv = 1.0 / d;

		output[0] = new ConstraintResult( d - Value, new[]
		{
			(A,  dx * inv,  dy * inv),
			(B, -dx * inv, -dy * inv)
		} );
	}
}

/// <summary>The segment between two points is horizontal: Δy = 0.</summary>
public sealed class HorizontalConstraint : IConstraint
{
	public readonly int Start, End;

	public HorizontalConstraint( int start, int end )
	{
		Start = start;
		End = end;
	}

	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var dy = (double)points[Start].y - points[End].y;

		output[0] = new ConstraintResult( dy, new[] { (Start, 0.0, 1.0), (End, 0.0, -1.0) } );
	}
}

/// <summary>The segment between two points is vertical: Δx = 0.</summary>
public sealed class VerticalConstraint : IConstraint
{
	public readonly int Start, End;

	public VerticalConstraint( int start, int end )
	{
		Start = start;
		End = end;
	}

	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var dx = (double)points[Start].x - points[End].x;

		output[0] = new ConstraintResult( dx, new[] { (Start, 1.0, 0.0), (End, -1.0, 0.0) } );
	}
}

/// <summary>Two segments are the same length, without saying what that length is.</summary>
public sealed class EqualLengthConstraint : IConstraint
{
	public readonly int A0, A1, B0, B1;

	public EqualLengthConstraint( int a0, int a1, int b0, int b1 )
	{
		A0 = a0;
		A1 = a1;
		B0 = b0;
		B1 = b1;
	}

	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var (da, dxa, dya) = Delta( points[A0], points[A1] );
		var (db, dxb, dyb) = Delta( points[B0], points[B1] );

		// A degenerate segment has no length gradient. Zeroing its columns leaves the other
		// segment free to move toward it, which is the useful half of the answer.
		var invA = da > 1e-12 ? 1.0 / da : 0.0;
		var invB = db > 1e-12 ? 1.0 / db : 0.0;

		output[0] = new ConstraintResult( da - db, new[]
		{
			(A0,  dxa * invA,  dya * invA),
			(A1, -dxa * invA, -dya * invA),
			(B0, -dxb * invB, -dyb * invB),
			(B1,  dxb * invB,  dyb * invB)
		} );
	}

	static (double Length, double Dx, double Dy) Delta( Vec2 p, Vec2 q )
	{
		var dx = (double)p.x - q.x;
		var dy = (double)p.y - q.y;

		return (Math.Sqrt( dx * dx + dy * dy ), dx, dy);
	}
}

/// <summary>
/// Two segments are parallel, as the 2D cross product of their directions being zero.
///
/// Not the angle between them: an angle needs an atan2 and carries a branch cut, and the cross
/// product is smooth everywhere and zero at exactly the states wanted. It is also sign-blind, so
/// antiparallel counts as parallel — which is what a CAD parallel constraint means.
/// </summary>
public sealed class ParallelConstraint : IConstraint
{
	public readonly int A0, A1, B0, B1;

	public ParallelConstraint( int a0, int a1, int b0, int b1 )
	{
		A0 = a0;
		A1 = a1;
		B0 = b0;
		B1 = b1;
	}

	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var ux = (double)points[A1].x - points[A0].x;
		var uy = (double)points[A1].y - points[A0].y;
		var vx = (double)points[B1].x - points[B0].x;
		var vy = (double)points[B1].y - points[B0].y;

		output[0] = new ConstraintResult( ux * vy - uy * vx, new[]
		{
			(A0, -vy,  vx),
			(A1,  vy, -vx),
			(B0,  uy, -ux),
			(B1, -uy,  ux)
		} );
	}
}

/// <summary>Two segments meet at a right angle, as the dot product of their directions being
/// zero.</summary>
public sealed class PerpendicularConstraint : IConstraint
{
	public readonly int A0, A1, B0, B1;

	public PerpendicularConstraint( int a0, int a1, int b0, int b1 )
	{
		A0 = a0;
		A1 = a1;
		B0 = b0;
		B1 = b1;
	}

	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var ux = (double)points[A1].x - points[A0].x;
		var uy = (double)points[A1].y - points[A0].y;
		var vx = (double)points[B1].x - points[B0].x;
		var vy = (double)points[B1].y - points[B0].y;

		output[0] = new ConstraintResult( ux * vx + uy * vy, new[]
		{
			(A0, -vx, -vy),
			(A1,  vx,  vy),
			(B0, -ux, -uy),
			(B1,  ux,  uy)
		} );
	}
}

/// <summary>
/// Two segments meet at a fixed angle.
///
/// The residual is |u||v| sin(φ − θ), written as cross·cos θ − dot·sin θ so that neither an atan2
/// nor a normalisation appears in it. That matters more than it looks: an angle computed with atan2
/// has a branch cut, and a residual that jumps by 2π somewhere in its domain will send a solver off
/// in the wrong direction the moment a line crosses it. This form is smooth everywhere and zero at
/// exactly the states wanted.
///
/// Parallel and Perpendicular are this at 0 and 90 degrees. They stay separate types because they
/// are what a user asks for, and because neither needs a value stored alongside it.
/// </summary>
public sealed class AngleConstraint : IConstraint
{
	public readonly int A0, A1, B0, B1;
	public readonly double Degrees;

	public AngleConstraint( int a0, int a1, int b0, int b1, double degrees )
	{
		A0 = a0;
		A1 = a1;
		B0 = b0;
		B1 = b1;
		Degrees = degrees;
	}

	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var ux = (double)points[A1].x - points[A0].x;
		var uy = (double)points[A1].y - points[A0].y;
		var vx = (double)points[B1].x - points[B0].x;
		var vy = (double)points[B1].y - points[B0].y;

		var radians = Degrees * Math.PI / 180.0;
		var cos = Math.Cos( radians );
		var sin = Math.Sin( radians );

		var cross = ux * vy - uy * vx;
		var dot = ux * vx + uy * vy;

		// d(residual)/du and /dv, from which the four point derivatives follow by u = A1 - A0.
		var dUx = vy * cos - vx * sin;
		var dUy = -vx * cos - vy * sin;
		var dVx = -uy * cos - ux * sin;
		var dVy = ux * cos - uy * sin;

		output[0] = new ConstraintResult( cross * cos - dot * sin, new[]
		{
			(A0, -dUx, -dUy),
			(A1,  dUx,  dUy),
			(B0, -dVx, -dVy),
			(B1,  dVx,  dVy)
		} );
	}
}

/// <summary>
/// A point lies on the infinite line through two others.
///
/// The residual is the cross product of the line's direction with the vector to the point, which is
/// twice the area of the triangle they make — zero exactly when they are collinear. Not the
/// perpendicular DISTANCE, which would need a division by the line's length and blow up as the two
/// defining points approach each other; the unnormalised form is smooth everywhere and vanishes at
/// the same states.
/// </summary>
public sealed class PointOnLineConstraint : IConstraint
{
	public readonly int Point, A, B;

	public PointOnLineConstraint( int point, int a, int b )
	{
		Point = point;
		A = a;
		B = b;
	}

	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var dx = (double)points[B].x - points[A].x;
		var dy = (double)points[B].y - points[A].y;
		var wx = (double)points[Point].x - points[A].x;
		var wy = (double)points[Point].y - points[A].y;

		output[0] = new ConstraintResult( dx * wy - dy * wx, new[]
		{
			(Point, -dy, dx),
			(A, dy - wy, wx - dx),
			(B, wy, -wx)
		} );
	}
}

/// <summary>
/// Two points mirror each other across the line through two others.
///
/// Two rows, because symmetry is two independent statements and collapsing them into one distance
/// would let the solver satisfy it by putting both points in the same place. Their midpoint has to
/// sit ON the line, and the segment between them has to cross it at a right angle.
/// </summary>
public sealed class SymmetricConstraint : IConstraint
{
	public readonly int P, Q, A, B;

	public SymmetricConstraint( int p, int q, int a, int b )
	{
		P = p;
		Q = q;
		A = a;
		B = b;
	}

	public int ResidualCount => 2;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var dx = (double)points[B].x - points[A].x;
		var dy = (double)points[B].y - points[A].y;

		// Midpoint of P and Q, relative to A.
		var mx = ((double)points[P].x + points[Q].x) * 0.5 - points[A].x;
		var my = ((double)points[P].y + points[Q].y) * 0.5 - points[A].y;

		// Row one: the midpoint is on the line. Each of P and Q moves it by a half.
		output[0] = new ConstraintResult( dx * my - dy * mx, new[]
		{
			(P, -dy * 0.5, dx * 0.5),
			(Q, -dy * 0.5, dx * 0.5),
			(A, dy - my, mx - dx),
			(B, my, -mx)
		} );

		// Row two: PQ is perpendicular to the line.
		var qx = (double)points[Q].x - points[P].x;
		var qy = (double)points[Q].y - points[P].y;

		output[1] = new ConstraintResult( dx * qx + dy * qy, new[]
		{
			(P, -dx, -dy),
			(Q, dx, dy),
			(A, -qx, -qy),
			(B, qx, qy)
		} );
	}
}

/// <summary>
/// A point sits exactly half way between two others. Two rows, because x and y are independently
/// wrong — the same reason Coincident is two.
///
/// Not expressible as two equal distances: that would also be satisfied by the point sitting
/// anywhere on the perpendicular bisector, which is a circle's worth of wrong answers.
/// </summary>
public sealed class MidpointConstraint : IConstraint
{
	public readonly int P, A, B;

	public MidpointConstraint( int p, int a, int b )
	{
		P = p;
		A = a;
		B = b;
	}

	public int ResidualCount => 2;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var mx = ((double)points[A].x + points[B].x) * 0.5;
		var my = ((double)points[A].y + points[B].y) * 0.5;

		output[0] = new ConstraintResult( (double)points[P].x - mx,
			new[] { (P, 1.0, 0.0), (A, -0.5, 0.0), (B, -0.5, 0.0) } );

		output[1] = new ConstraintResult( (double)points[P].y - my,
			new[] { (P, 0.0, 1.0), (A, 0.0, -0.5), (B, 0.0, -0.5) } );
	}
}

/// <summary>
/// A point is nailed to an absolute coordinate — Onshape's "fix".
///
/// THE SOLVER'S PIN IS NOT THIS. SketchSolver.Solve takes a single pinnedPoint and removes its
/// columns from the Jacobian entirely, which is how the sketch gets an absolute frame at all; it
/// can only ever be one point and it is chosen by the caller, not by the user. This is the
/// user-facing version and there can be as many as you like. It works the ordinary way, as two
/// residuals the solver drives to zero, so a fix that fights a dimension shows up honestly as a
/// sketch that will not converge rather than as a silently ignored rule.
/// </summary>
public sealed class FixedConstraint : IConstraint
{
	public readonly int P;
	public readonly double X, Y;

	public FixedConstraint( int p, double x, double y )
	{
		P = p;
		X = x;
		Y = y;
	}

	public int ResidualCount => 2;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		output[0] = new ConstraintResult( (double)points[P].x - X, new[] { (P, 1.0, 0.0) } );
		output[1] = new ConstraintResult( (double)points[P].y - Y, new[] { (P, 0.0, 1.0) } );
	}
}

/// <summary>
/// A line is tangent to a circle or an arc: the distance from the centre to the infinite line
/// equals the radius.
///
/// The circle is given as a centre and a point on its rim rather than as a stored radius, because
/// that is the only form the solver can see. Every unknown here is a point coordinate — a radius
/// held in a float field is invisible to the Jacobian and could not be driven by anything. For a
/// SketchArc the rim point is its Start, which is exactly how SketchArc already defines its radius.
///
/// WRITTEN IN LENGTH-SQUARED, AND THE SCALING IS THE REASON. The obvious residual is
/// cross(d, w)^2 - r^2*|d|^2, which clears the division and is quartic in the coordinates. It is
/// smooth and it is correct, and it is badly scaled: the solver's convergence test is an absolute
/// 1e-6 on the residual norm, so a quartic residual reaching 1e-6 can still be a visibly untangent
/// line. Dividing through by |d|^2 makes both terms an area, so 1e-6 of residual is about 5e-7 of
/// radius on a unit-ish sketch, which is the accuracy the number implies.
///
/// Sign-blind on purpose: the line can arrive at tangency from either side, and forcing a side
/// would mean choosing one at build time from the current configuration and having the constraint
/// mean something different depending on when it was added.
/// </summary>
public sealed class TangentLineArcConstraint : IConstraint
{
	public readonly int A0, A1, Center, Rim;

	public TangentLineArcConstraint( int a0, int a1, int center, int rim )
	{
		A0 = a0;
		A1 = a1;
		Center = center;
		Rim = rim;
	}

	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var dx = (double)points[A1].x - points[A0].x;
		var dy = (double)points[A1].y - points[A0].y;
		var wx = (double)points[Center].x - points[A0].x;
		var wy = (double)points[Center].y - points[A0].y;

		var rx = (double)points[Center].x - points[Rim].x;
		var ry = (double)points[Center].y - points[Rim].y;
		var r2 = rx * rx + ry * ry;

		var d2 = dx * dx + dy * dy;

		// A zero-length line has no direction and no distance-to-line. Reporting the radius as the
		// error with a zero gradient on the line leaves the circle free to shrink toward it, which
		// is the only sensible half of the answer, and keeps a NaN out of H.
		if ( d2 < 1e-18 )
		{
			output[0] = new ConstraintResult( -r2, new[]
			{
				(Center, -2.0 * rx, -2.0 * ry),
				(Rim, 2.0 * rx, 2.0 * ry)
			} );

			return;
		}

		var k = dx * wy - dy * wx;
		var q = k * k / d2;

		var a = 2.0 * k / d2;
		var b = q / d2;

		output[0] = new ConstraintResult( q - r2, new[]
		{
			(A0, a * (dy - wy) + 2.0 * b * dx, a * (wx - dx) + 2.0 * b * dy),
			(A1, a * wy - 2.0 * b * dx, -a * wx - 2.0 * b * dy),
			(Center, -a * dy - 2.0 * rx, a * dx - 2.0 * ry),
			(Rim, 2.0 * rx, 2.0 * ry)
		} );
	}
}

/// <summary>
/// Two circles or arcs are tangent to each other: centre distance equals the sum of the radii
/// (touching outside) or the difference (one nestled inside the other).
///
/// Which of the two is a stored choice rather than something inferred from the current positions.
/// Inferring would make the rule mean whichever one happened to be closer when it was added, and
/// then silently flip meaning the first time a drag carried the circles past each other.
///
/// In plain lengths rather than squares — unlike the line case there is no division to clear here,
/// so the residual is already a distance and 1e-6 of residual is 1e-6 of gap.
/// </summary>
public sealed class TangentArcArcConstraint : IConstraint
{
	public readonly int CenterA, RimA, CenterB, RimB;

	/// <summary>True when one circle sits inside the other and they touch at a single point.</summary>
	public readonly bool Internal;

	public TangentArcArcConstraint( int centerA, int rimA, int centerB, int rimB, bool internalTangency = false )
	{
		CenterA = centerA;
		RimA = rimA;
		CenterB = centerB;
		RimB = rimB;
		Internal = internalTangency;
	}

	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var dx = (double)points[CenterA].x - points[CenterB].x;
		var dy = (double)points[CenterA].y - points[CenterB].y;
		var l = Math.Sqrt( dx * dx + dy * dy );

		var ax = (double)points[CenterA].x - points[RimA].x;
		var ay = (double)points[CenterA].y - points[RimA].y;
		var ra = Math.Sqrt( ax * ax + ay * ay );

		var bx = (double)points[CenterB].x - points[RimB].x;
		var by = (double)points[CenterB].y - points[RimB].y;
		var rb = Math.Sqrt( bx * bx + by * by );

		// Every direction below is a unit vector that does not exist when its length is zero.
		// Zeroing the gradient there leaves the other terms to do the work rather than poisoning
		// the whole step with a NaN, the same tactic DistanceConstraint uses.
		var lx = l > 1e-12 ? dx / l : 0.0;
		var ly = l > 1e-12 ? dy / l : 0.0;
		var uax = ra > 1e-12 ? ax / ra : 0.0;
		var uay = ra > 1e-12 ? ay / ra : 0.0;
		var ubx = rb > 1e-12 ? bx / rb : 0.0;
		var uby = rb > 1e-12 ? by / rb : 0.0;

		if ( !Internal )
		{
			output[0] = new ConstraintResult( l - ra - rb, new[]
			{
				(CenterA, lx - uax, ly - uay),
				(CenterB, -lx - ubx, -ly - uby),
				(RimA, uax, uay),
				(RimB, ubx, uby)
			} );

			return;
		}

		// |ra - rb| is not differentiable where the radii are equal, and that state is a real one:
		// two equal circles are internally tangent exactly when they are the same circle. The sign
		// is taken from the current configuration, which is the standard handling and is stable
		// everywhere except that degenerate point.
		var s = ra >= rb ? 1.0 : -1.0;

		output[0] = new ConstraintResult( l - Math.Abs( ra - rb ), new[]
		{
			(CenterA, lx - s * uax, ly - s * uay),
			(CenterB, -lx + s * ubx, -ly + s * uby),
			(RimA, s * uax, s * uay),
			(RimB, -s * ubx, -s * uby)
		} );
	}
}
