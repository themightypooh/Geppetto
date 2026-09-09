using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Named values in expressions, and a variable used by more than one feature.
///
/// Done means: a variable named once, used in four features, changed once, and all four move.
/// </summary>
public static class VariableTests
{
	public static void Run()
	{
		Section( "variables: #name in an expression" );
		TestResolver();
		TestUnknownIsRefused();
		TestCycleIsRefused();

		Section( "variables: a change moves every feature that refers to it" );
		TestOneVariableFourFeatures();

		Section( "variables: the document round-trips the table and the expressions" );
		TestDocumentRoundTrip();
	}

	static void TestResolver()
	{
		float? Resolve( string name ) => name == "thickness" ? 2f : null;

		var parsed = Expression.TryEvaluate( "#thickness / 2", null, Resolve, out var value );

		Check( "#thickness / 2 evaluates against the table", parsed && Almost( value, 1f ),
			parsed ? $"{value}" : "refused" );

		Check( "a bare name is still a constant, not a variable",
			Expression.TryEvaluate( "pi", null, Resolve, out var pi ) && Almost( pi, MathF.PI ) );
	}

	static void TestUnknownIsRefused()
	{
		var parsed = Expression.TryEvaluate( "#nope", null, _ => null, out _ );

		Check( "an unknown #name is refused, so the field holds the last good value", !parsed );
	}

	static void TestCycleIsRefused()
	{
		var table = new List<StudioVariable>
		{
			new( "a", "#b + 1", 4f ),
			new( "b", "#a + 1", 5f ),
		};

		Check( "a cycle does not resolve", !VariableResolver.TryResolve( table, "a", out _ ) );

		VariableResolver.EvaluateAll( table );

		Check( "and EvaluateAll keeps the last good values rather than looping",
			Almost( table[0].Value, 4f ) && Almost( table[1].Value, 5f ),
			$"{table[0].Value}, {table[1].Value}" );
	}

	static void TestOneVariableFourFeatures()
	{
		var studio = new PartStudio();
		studio.SetVariable( "t", "2" );

		for ( var i = 0; i < 4; i++ )
		{
			var box = studio.Add( new PrimitiveFeature() );
			box.SizeX.Expr = "#t";
			box.SizeY.Value = 1f;
			box.SizeZ.Value = 1f;
		}

		studio.Rebuild();

		Check( "all four boxes take the variable's value",
			studio.Bodies.Count == 4
			&& studio.Bodies.TrueForAll( b => Almost( Extent( b.Mesh, 0 ), 2f ) ),
			$"{studio.Bodies.Count} bodies" );

		studio.SetVariable( "t", "4" );
		studio.Rebuild();

		Check( "changing it once moves all four",
			studio.Bodies.TrueForAll( b => Almost( Extent( b.Mesh, 0 ), 4f ) ) );
	}

	static void TestDocumentRoundTrip()
	{
		var studio = new PartStudio();
		studio.SetVariable( "thickness", "2" );
		studio.SetVariable( "gap", "#thickness / 2" );

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Expr = "#thickness";
		box.SizeY.Expr = "#gap";
		box.SizeZ.Value = 1f;

		var back = StudioDocument.Read( StudioDocument.Write( studio ) );
		back.Rebuild();

		Check( "both variables come back",
			back.Variables.Count == 2, $"{back.Variables.Count}" );

		Check( "the expression survived, not the evaluated number",
			back.Features[0] is PrimitiveFeature p
			&& p.SizeX.Expr == "#thickness"
			&& p.SizeY.Expr == "#gap",
			back.Features[0] is PrimitiveFeature q ? $"{q.SizeX.Expr}, {q.SizeY.Expr}" : "not a box" );

		Check( "and rebuild still follows the table",
			back.Bodies.Count == 1
			&& Almost( Extent( back.Bodies[0].Mesh, 0 ), 2f )
			&& Almost( Extent( back.Bodies[0].Mesh, 1 ), 1f ) );
	}

	static float Extent( PolyMesh mesh, int axis )
	{
		var min = float.MaxValue;
		var max = float.MinValue;

		foreach ( var p in mesh.Positions )
		{
			var v = axis == 0 ? p.x : axis == 1 ? p.y : p.z;
			min = MathF.Min( min, v );
			max = MathF.Max( max, v );
		}

		return max - min;
	}

	static bool Almost( float a, float b ) => MathF.Abs( a - b ) < 1e-3f;
}
