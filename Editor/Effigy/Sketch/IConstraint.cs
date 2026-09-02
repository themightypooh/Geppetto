using System;

namespace Effigy;

/// <summary>
/// One residual row produced by a constraint evaluation, plus its derivative against the points it
/// touches. The solver stacks these into r and J and takes a damped Gauss-Newton step.
/// </summary>
public readonly struct ConstraintResult
{
	/// <summary>How far this rule is from being satisfied. Zero means satisfied.</summary>
	public readonly double Residual;

	/// <summary>∂r/∂x and ∂r/∂y for every point the constraint touches.</summary>
	public readonly (int Point, double Gx, double Gy)[] Jacobian;

	public ConstraintResult( double residual, (int Point, double Gx, double Gy)[] jacobian )
	{
		Residual = residual;
		Jacobian = jacobian;
	}
}

/// <summary>
/// A constraint the solver can evaluate. One method, and the solver never switches on a kind — so
/// adding a rule is one new class here and no change at all to SketchSolver.
///
/// Note what this is NOT: it is not the stored form. SketchConstraint is what a sketch holds and
/// what a file round-trips; this is what it becomes for the duration of a solve. Keeping them apart
/// is what lets the stored form stay a plain record of point indices while the evaluated form
/// carries derivatives.
/// </summary>
public interface IConstraint
{
	/// <summary>How many residual rows this contributes. One for most; Coincident is two, because
	/// x and y are independently wrong.</summary>
	int ResidualCount { get; }

	/// <summary>
	/// Evaluate at the given positions, writing ResidualCount entries into <paramref name="output"/>.
	///
	/// Pinned points still appear in the Jacobian — the residual would be wrong without them — and
	/// the solver drops their columns when it assembles J.
	/// </summary>
	void Evaluate( ReadOnlySpan<Vec2> points, Span<ConstraintResult> output );
}
