using System;
using System.Collections.Generic;
using System.Globalization;

namespace Effigy;

/// <summary>
/// The evaluator behind Onshape's numeric fields.
///
/// Onshape's numeric fields "accept integers, decimals, parameter expressions, and trigonometric
/// functions", with the documented operator set ^ * / + - and the documented function set ceil,
/// floor, round, exp, sqrt, abs, max, min and log. Typing `1/8` and getting 0.125 is not a
/// nicety - a slider cannot express a fraction, a tapped hole size, or "half of the last one",
/// and those are most of what a dimension actually is.
///
/// Deliberately hand-written rather than pulled from a package: the kernel's whole point is
/// that it has no dependencies. It lives in the kernel rather than the editor because it has no
/// engine surface whatsoever - which also means it can be compiled and exercised directly, and
/// ExpressionTests does exactly that.
///
/// UNITS. Effigy's lengths are dimensionless (see EffigyViewport.FrameCamera - a default Box is
/// one unit on a side), so length fields take bare numbers and reject unit suffixes rather than
/// inventing a millimetre that nothing downstream honours. Angle fields are real, because
/// FloatParam.Unit already carries "deg", so those accept `deg`, `rad` and `°`.
///
/// TRIG IS IN DEGREES. sin(30) is 0.5. Onshape's trig takes a unit-carrying angle and cannot be
/// ambiguous; with no unit system here, degrees is what someone typing into a CAD field means.
/// </summary>
public static class Expression
{
	/// <summary>
	/// Evaluate an expression. False when it is not a well-formed expression at all, which is the
	/// signal for the field to hold its last good value rather than clobbering the parameter with
	/// a NaN halfway through someone typing "1/".
	/// </summary>
	/// <param name="text">What the user typed.</param>
	/// <param name="unit">The parameter's unit - "deg" for an angle, null for a bare number.</param>
	/// <param name="value">The result, in the parameter's own unit.</param>
	public static bool TryEvaluate( string text, string unit, out float value )
	{
		value = 0f;

		if ( string.IsNullOrWhiteSpace( text ) )
			return false;

		try
		{
			var parser = new Parser( text, unit );
			var result = parser.ParseExpression();

			parser.SkipSpace();

			// Trailing junk means the whole string was not an expression. "2 3" is a typo, not a 2.
			if ( !parser.AtEnd )
				return false;

			if ( float.IsNaN( result ) || float.IsInfinity( result ) )
				return false;

			value = result;
			return true;
		}
		catch ( FormatException )
		{
			return false;
		}
	}

	/// <summary>How a committed value is written back into the field. Onshape shows the evaluated
	/// result once a numeric field is accepted; trailing zeros on an integral value read as noise
	/// in a dimension, so 4 stays 4 rather than becoming 4.000.</summary>
	public static string Format( float value )
	{
		if ( MathF.Abs( value - MathF.Round( value ) ) < 1e-6f )
			return ((int)MathF.Round( value )).ToString( CultureInfo.InvariantCulture );

		return value.ToString( "0.####", CultureInfo.InvariantCulture );
	}

	// --- parser -------------------------------------------------------------------------------

	/// <summary>
	/// Recursive descent over
	///
	///   expression := term (('+'|'-') term)*
	///   term       := unary (('*'|'/') unary)*
	///   unary      := ('-'|'+') unary | power
	///   power      := atom ('^' unary)?
	///   atom       := number unit? | name '(' args ')' | name | '(' expression ')'
	///
	/// The unary/power split is what makes -2^2 evaluate to -4 and 2^-1 to 0.5, which is the
	/// convention every calculator and every CAD field uses. Getting it wrong is silent: the
	/// expression still evaluates, to the wrong number.
	/// </summary>
	sealed class Parser
	{
		readonly string _text;
		readonly string _unit;
		int _pos;

		public Parser( string text, string unit )
		{
			_text = text;
			_unit = unit;
		}

		public bool AtEnd => _pos >= _text.Length;

		public void SkipSpace()
		{
			while ( _pos < _text.Length && char.IsWhiteSpace( _text[_pos] ) )
				_pos++;
		}

		char Peek()
		{
			SkipSpace();
			return _pos < _text.Length ? _text[_pos] : '\0';
		}

		bool Take( char c )
		{
			if ( Peek() != c )
				return false;

			_pos++;
			return true;
		}

		public float ParseExpression()
		{
			var left = ParseTerm();

			while ( true )
			{
				if ( Take( '+' ) ) left += ParseTerm();
				else if ( Take( '-' ) ) left -= ParseTerm();
				else return left;
			}
		}

		float ParseTerm()
		{
			var left = ParseUnary();

			while ( true )
			{
				if ( Take( '*' ) )
				{
					left *= ParseUnary();
				}
				else if ( Take( '/' ) )
				{
					var divisor = ParseUnary();

					// Not an exception: a field reading "1/0" mid-type is a half-finished thought,
					// and NaN out is how TryEvaluate knows to keep the previous value.
					if ( divisor == 0f )
						return float.NaN;

					left /= divisor;
				}
				else
				{
					return left;
				}
			}
		}

		float ParseUnary()
		{
			if ( Take( '-' ) )
				return -ParseUnary();

			if ( Take( '+' ) )
				return ParseUnary();

			return ParsePower();
		}

		float ParsePower()
		{
			var b = ParseAtom();

			// Right-associative, and the exponent goes through unary so 2^-1 parses.
			if ( Take( '^' ) )
				return MathF.Pow( b, ParseUnary() );

			return b;
		}

		float ParseAtom()
		{
			SkipSpace();

			if ( AtEnd )
				throw new FormatException( "unexpected end of expression" );

			if ( Take( '(' ) )
			{
				var inner = ParseExpression();

				if ( !Take( ')' ) )
					throw new FormatException( "unclosed bracket" );

				return inner;
			}

			var c = _text[_pos];

			if ( char.IsDigit( c ) || c == '.' )
				return ParseNumber();

			if ( char.IsLetter( c ) || c == '_' )
				return ParseName();

			throw new FormatException( $"unexpected '{c}'" );
		}

		float ParseNumber()
		{
			var start = _pos;

			while ( _pos < _text.Length && (char.IsDigit( _text[_pos] ) || _text[_pos] == '.') )
				_pos++;

			// Exponent form, but only when it really is one - `2e` is a 2 times the constant e,
			// and `2early` is a typo. Both have to stay out of the number.
			if ( _pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E') )
			{
				var save = _pos;
				var probe = _pos + 1;

				if ( probe < _text.Length && (_text[probe] == '+' || _text[probe] == '-') )
					probe++;

				if ( probe < _text.Length && char.IsDigit( _text[probe] ) )
				{
					_pos = probe;

					while ( _pos < _text.Length && char.IsDigit( _text[_pos] ) )
						_pos++;
				}
				else
				{
					_pos = save;
				}
			}

			var span = _text.Substring( start, _pos - start );

			if ( !float.TryParse( span, NumberStyles.Float, CultureInfo.InvariantCulture, out var number ) )
				throw new FormatException( $"'{span}' is not a number" );

			return ApplyUnitSuffix( number );
		}

		/// <summary>
		/// A unit written straight after a number, converted into the field's own unit.
		///
		/// Length fields reject suffixes outright rather than silently ignoring them: the kernel is
		/// dimensionless, so accepting `5mm` and storing 5 would be a worse lie than refusing it.
		/// </summary>
		float ApplyUnitSuffix( float number )
		{
			if ( _pos < _text.Length && _text[_pos] == '°' )
			{
				_pos++;

				if ( _unit != "deg" )
					throw new FormatException( "this field has no angle unit" );

				return number;
			}

			var start = _pos;

			while ( _pos < _text.Length && char.IsLetter( _text[_pos] ) )
				_pos++;

			if ( _pos == start )
				return number;

			var suffix = _text.Substring( start, _pos - start ).ToLowerInvariant();

			// Not a unit - put the letters back and let the trailing-junk check in TryEvaluate
			// reject the whole string. There is no implicit multiplication, so `2pi` is a typo
			// rather than 2*pi; failing loudly beats guessing which one was meant.
			if ( !IsUnit( suffix ) )
			{
				_pos = start;
				return number;
			}

			if ( _unit != "deg" )
				throw new FormatException( $"'{suffix}' is an angle unit and this field is a plain number" );

			return suffix == "rad" ? number * (180f / MathF.PI) : number;
		}

		static bool IsUnit( string s ) => s is "deg" or "degree" or "degrees" or "rad" or "radian" or "radians";

		float ParseName()
		{
			var start = _pos;

			while ( _pos < _text.Length && (char.IsLetterOrDigit( _text[_pos] ) || _text[_pos] == '_') )
				_pos++;

			var name = _text.Substring( start, _pos - start ).ToLowerInvariant();

			if ( Peek() == '(' )
			{
				_pos++; // the '(' that Peek found
				var args = new List<float>();

				if ( Peek() != ')' )
				{
					args.Add( ParseExpression() );

					while ( Take( ',' ) )
						args.Add( ParseExpression() );
				}

				if ( !Take( ')' ) )
					throw new FormatException( $"unclosed bracket after {name}" );

				return Call( name, args );
			}

			return name switch
			{
				"pi" => MathF.PI,
				"tau" => MathF.Tau,
				"e" => MathF.E,
				_ => throw new FormatException( $"unknown name '{name}'" ),
			};
		}

		static float Call( string name, List<float> a )
		{
			float One() => a.Count == 1 ? a[0] : throw new FormatException( $"{name} takes one argument" );
			float Two( int i ) => a.Count == 2 ? a[i] : throw new FormatException( $"{name} takes two arguments" );

			const float ToRad = MathF.PI / 180f;
			const float ToDeg = 180f / MathF.PI;

			return name switch
			{
				"sqrt" => MathF.Sqrt( One() ),
				"abs" => MathF.Abs( One() ),
				"floor" => MathF.Floor( One() ),
				"ceil" => MathF.Ceiling( One() ),
				"round" => MathF.Round( One() ),
				"sign" => MathF.Sign( One() ),
				"exp" => MathF.Exp( One() ),
				"log" => MathF.Log( One() ),
				"log10" => MathF.Log10( One() ),

				// Degrees in, degrees out - see the class remarks.
				"sin" => MathF.Sin( One() * ToRad ),
				"cos" => MathF.Cos( One() * ToRad ),
				"tan" => MathF.Tan( One() * ToRad ),
				"asin" => MathF.Asin( One() ) * ToDeg,
				"acos" => MathF.Acos( One() ) * ToDeg,
				"atan" => MathF.Atan( One() ) * ToDeg,

				"min" => MathF.Min( Two( 0 ), Two( 1 ) ),
				"max" => MathF.Max( Two( 0 ), Two( 1 ) ),
				"pow" => MathF.Pow( Two( 0 ), Two( 1 ) ),
				"atan2" => MathF.Atan2( Two( 0 ), Two( 1 ) ) * ToDeg,

				_ => throw new FormatException( $"unknown function '{name}'" ),
			};
		}
	}
}
