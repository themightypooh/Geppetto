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
