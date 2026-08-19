using System;

namespace Effigy;

/// <summary>
/// One residual row produced by a constraint evaluation, plus its Jacobian against the free
/// variables. The solver stacks these into r and J and runs a damped Gauss-Newton step.
/// </summary>
public readonly struct ConstraintResult
{
	public readonly double Residual;
	/// <summary>∂r/∂x for every free variable the constraint touches, in (pointIndex, ∂r/∂x, ∂r/∂y) form.</summary>
	public readonly (int Point, double Gx, double Gy)[] Jacobian;

	public ConstraintResult( double residual, (int Point, double Gx, double Gy)[] jacobian )
	{
		Residual = residual;
		Jacobian = jacobian;
	}
}

/// <summary>
/// Runtime constraint evaluator. One type, one method — the solver is generic over any set of
/// these. Adding a new constraint kind is one new class and zero solver changes.
/// </summary>
public interface IConstraint
{
	/// <summary>How many residual rows this constraint contributes (usually 1; Coincident is 2).</summary>
	int ResidualCount { get; }

	/// <summary>
	/// Evaluate at the given point array. Writes ResidualCount entries into <paramref name="output"/>
	/// starting at index 0 of the span. Points that are pinned (e.g. index 0) still appear in the
	/// Jacobian so the residual is correct; the solver drops their columns.
	/// </summary>
	void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output );
}
