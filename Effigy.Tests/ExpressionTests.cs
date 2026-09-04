using System;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// The numeric-field expression evaluator, run for real.
///
/// These cases were originally checked by transliterating the grammar into another language and
/// exercising it there, because there was no way to compile C# on the machine that wrote it. That
/// is a decent sanity check and it is not the same thing as running the code. Now that the kernel
/// builds here, they are assertions against the actual parser.
///
/// The REJECTIONS matter at least as much as the acceptances. A field that quietly evaluates a
/// typo is worse than one that refuses it, because the wrong number goes into the model and looks
/// deliberate.
/// </summary>
public static class ExpressionTests
{
	public static void Run()
	{
		Report.Section( "expressions: values, operators and functions" );
		TestAccepted();

		Report.Section( "expressions: precedence" );
		TestPrecedence();

		Report.Section( "expressions: units" );
		TestUnits();

		Report.Section( "expressions: things that must be refused" );
		TestRejected();

		Report.Section( "expressions: formatting a value back out" );
		TestFormat();
	}

	static void Ok( string text, float expected, string unit = null )
	{
		var parsed = Expression.TryEvaluate( text, unit, out var value );

		Report.Check( $"\"{text}\"{(unit is null ? "" : $" [{unit}]")} = {expected}",
			parsed && MathF.Abs( value - expected ) < 1e-4f,
			parsed ? $"got {value}" : "refused" );
	}

	static void Refused( string text, string unit = null )
	{
		var parsed = Expression.TryEvaluate( text, unit, out var value );

		Report.Check( $"\"{text}\"{(unit is null ? "" : $" [{unit}]")} is refused",
			!parsed, parsed ? $"accepted as {value}" : null );
	}

	static void TestAccepted()
	{
		Ok( "4", 4f );
		Ok( ".5", 0.5f );
		Ok( " 2 + 3 ", 5f );
		Ok( "10/4", 2.5f );
		Ok( "1/8", 0.125f );                     // the case a slider cannot express
		Ok( "2*(3+4)", 14f );
		Ok( "-(3+4)", -7f );
		Ok( "1e3", 1000f );
		Ok( "sqrt(2)*10", 14.142136f );
		Ok( "max(3,7)", 7f );
		Ok( "min(3,7)", 3f );
		Ok( "round(2.6)", 3f );
		Ok( "ceil(2.1)", 3f );
		Ok( "floor(2.9)", 2f );
		Ok( "abs(0-5)", 5f );
		Ok( "pow(2,10)", 1024f );
		Ok( "2*pi", 6.2831853f );

		// Trig is in DEGREES - with no unit system, that is what someone typing into a CAD field
		// means, and Onshape's own trig cannot be ambiguous because its angles carry units.
		Ok( "sin(30)", 0.5f );
		Ok( "cos(60)", 0.5f );
		Ok( "atan2(1,1)", 45f );
	}

	static void TestPrecedence()
	{
		// Getting these wrong is silent: the expression still evaluates, to the wrong number.
		Ok( "-2^2", -4f );        // unary minus binds looser than the power
		Ok( "2^-1", 0.5f );       // ...but the exponent still takes a sign
		Ok( "2^3^2", 512f );      // right-associative, not (2^3)^2 = 64
		Ok( "1+2*3", 7f );
		Ok( "(1+2)*3", 9f );
	}

	static void TestUnits()
	{
		Ok( "45deg", 45f, "deg" );
		Ok( "1rad", 57.29578f, "deg" );
		Ok( "90°", 90f, "deg" );
		Ok( "45", 45f, "deg" );                  // bare number takes the field's own unit

		// Lengths are dimensionless in this kernel, so a unit on one is refused rather than
		// silently ignored - storing 5 for "5mm" would be a worse lie than refusing it.
		Refused( "5mm" );
		Refused( "45deg" );
		Refused( "90°" );
	}

	static void TestRejected()
	{
		Refused( "" );
		Refused( "   " );
		Refused( "2 3" );          // two numbers is a typo, not a 2
		Refused( "1/" );           // mid-keystroke: hold the last good value
		Refused( "1/0" );
		Refused( "(1+2" );
		Refused( "*3" );
		Refused( "2**3" );
		Refused( "bar" );
		Refused( "foo(2)" );
		Refused( "max(1)" );       // wrong arity
		Refused( "sqrt(-1)" );     // NaN must not reach the model
		Refused( "2e" );           // not scientific notation, and not 2*e either
		Refused( "2pi" );          // no implicit multiplication - a typo, not 2*pi
		Refused( "3sqrt(4)" );
	}

	static void TestFormat()
	{
		Report.Check( "an integral value formats without trailing zeros", Expression.Format( 4f ) == "4",
			Expression.Format( 4f ) );

		Report.Check( "a fractional value keeps its decimals", Expression.Format( 0.125f ) == "0.125",
			Expression.Format( 0.125f ) );

		Report.Check( "a negative value round-trips",
			Expression.TryEvaluate( Expression.Format( -2.5f ), null, out var back )
			&& MathF.Abs( back + 2.5f ) < 1e-6f );

		// Whatever comes out has to go back in, or the field fights the user every time it
		// rewrites itself.
		var roundTripped = true;

		foreach ( var v in new[] { 0f, 1f, -1f, 0.125f, 1234.5f, -0.0625f } )
		{
			if ( !Expression.TryEvaluate( Expression.Format( v ), null, out var r ) || MathF.Abs( r - v ) > 1e-4f )
				roundTripped = false;
		}

		Report.Check( "every formatted value parses back to itself", roundTripped );
	}
}
