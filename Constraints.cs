using System;

namespace Effigy;

/// <summary>Two points occupy the same location. Two residual rows (Δx, Δy).</summary>
public sealed class CoincidentConstraint : IConstraint
{
	public readonly int A, B;
	public CoincidentConstraint( int a, int b ) { A = a; B = b; }
	public int ResidualCount => 2;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var pa = points[A];
		var pb = points[B];
		output[0] = new ConstraintResult( pa.x - pb.x, new[] {
			(A, 1.0, 0.0), (B, -1.0, 0.0)
		} );
		output[1] = new ConstraintResult( pa.y - pb.y, new[] {
			(A, 0.0, 1.0), (B, 0.0, -1.0)
		} );
	}
}

/// <summary>Euclidean distance between two points equals Value.</summary>
public sealed class DistanceConstraint : IConstraint
{
	public readonly int A, B;
	public readonly double Value;
	public DistanceConstraint( int a, int b, double value ) { A = a; B = b; Value = value; }
	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var pa = points[A];
		var pb = points[B];
		var dx = (double)pa.x - pb.x;
		var dy = (double)pa.y - pb.y;
		var d = Math.Sqrt( dx * dx + dy * dy );
		// Degenerate: both points coincide. Push them apart along +X so the Jacobian is defined.
		if ( d < 1e-12 )
		{
			output[0] = new ConstraintResult( -Value, new[] {
				(A, 1.0, 0.0), (B, -1.0, 0.0)
			} );
			return;
		}
		var inv = 1.0 / d;
		output[0] = new ConstraintResult( d - Value, new[] {
			(A,  dx * inv,  dy * inv),
			(B, -dx * inv, -dy * inv)
		} );
	}
}

/// <summary>Line segment between Start and End is horizontal (Δy = 0).</summary>
public sealed class HorizontalConstraint : IConstraint
{
	public readonly int Start, End;
	public HorizontalConstraint( int start, int end ) { Start = start; End = end; }
	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var dy = (double)points[Start].y - points[End].y;
		output[0] = new ConstraintResult( dy, new[] {
			(Start, 0.0, 1.0), (End, 0.0, -1.0)
		} );
	}
}

/// <summary>Line segment between Start and End is vertical (Δx = 0).</summary>
public sealed class VerticalConstraint : IConstraint
{
	public readonly int Start, End;
	public VerticalConstraint( int start, int end ) { Start = start; End = end; }
	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var dx = (double)points[Start].x - points[End].x;
		output[0] = new ConstraintResult( dx, new[] {
			(Start, 1.0, 0.0), (End, -1.0, 0.0)
		} );
	}
}

/// <summary>Two segments have equal length.</summary>
public sealed class EqualLengthConstraint : IConstraint
{
	public readonly int A0, A1, B0, B1;
	public EqualLengthConstraint( int a0, int a1, int b0, int b1 )
	{
		A0 = a0; A1 = a1; B0 = b0; B1 = b1;
	}
	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		static (double d, double dx, double dy) Len( Vec2 p, Vec2 q )
		{
			var dx = (double)p.x - q.x;
			var dy = (double)p.y - q.y;
			var d = Math.Sqrt( dx * dx + dy * dy );
			return (d, dx, dy);
		}

		var (da, dxa, dya) = Len( points[A0], points[A1] );
		var (db, dxb, dyb) = Len( points[B0], points[B1] );

		// ∂(|A0A1| − |B0B1|) / ∂point
		var invA = da > 1e-12 ? 1.0 / da : 0.0;
		var invB = db > 1e-12 ? 1.0 / db : 0.0;

		output[0] = new ConstraintResult( da - db, new[] {
			(A0,  dxa * invA,  dya * invA),
			(A1, -dxa * invA, -dya * invA),
			(B0, -dxb * invB, -dyb * invB),
			(B1,  dxb * invB,  dyb * invB),
		} );
	}
}

/// <summary>Two segments are parallel (2D cross of direction vectors = 0).</summary>
public sealed class ParallelConstraint : IConstraint
{
	public readonly int A0, A1, B0, B1;
	public ParallelConstraint( int a0, int a1, int b0, int b1 )
	{
		A0 = a0; A1 = a1; B0 = b0; B1 = b1;
	}
	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		// u = A1−A0, v = B1−B0; residual = ux*vy − uy*vx
		var ux = (double)points[A1].x - points[A0].x;
		var uy = (double)points[A1].y - points[A0].y;
		var vx = (double)points[B1].x - points[B0].x;
		var vy = (double)points[B1].y - points[B0].y;
		var r = ux * vy - uy * vx;

		// ∂r/∂A0x = −vy, ∂r/∂A0y = vx, ∂r/∂A1x = vy, ∂r/∂A1y = −vx
		// ∂r/∂B0x = uy,  ∂r/∂B0y = −ux, ∂r/∂B1x = −uy, ∂r/∂B1y = ux
		output[0] = new ConstraintResult( r, new[] {
			(A0, -vy,  vx),
			(A1,  vy, -vx),
			(B0,  uy, -ux),
			(B1, -uy,  ux),
		} );
	}
}

/// <summary>Two segments are perpendicular (dot of direction vectors = 0).</summary>
public sealed class PerpendicularConstraint : IConstraint
{
	public readonly int A0, A1, B0, B1;
	public PerpendicularConstraint( int a0, int a1, int b0, int b1 )
	{
		A0 = a0; A1 = a1; B0 = b0; B1 = b1;
	}
	public int ResidualCount => 1;

	public void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output )
	{
		var ux = (double)points[A1].x - points[A0].x;
		var uy = (double)points[A1].y - points[A0].y;
		var vx = (double)points[B1].x - points[B0].x;
		var vy = (double)points[B1].y - points[B0].y;
		var r = ux * vx + uy * vy;

		// ∂r/∂A0 = −v, ∂r/∂A1 = v, ∂r/∂B0 = −u, ∂r/∂B1 = u
		output[0] = new ConstraintResult( r, new[] {
			(A0, -vx, -vy),
			(A1,  vx,  vy),
			(B0, -ux, -uy),
			(B1,  ux,  uy),
		} );
	}
}
