using System;

namespace Marionette.ShaderForge.Tests;

/// <summary>Shared pass/fail tally, so every section reports into one place and one exit code.
/// Same shape as Effigy.Tests/Report.cs.</summary>
public static class Report
{
	public static int Passed, Failed;

	public static void Section( string title )
	{
		Console.WriteLine();
		Console.WriteLine( title );
	}

	public static void Check( string what, bool ok, string detail = null )
	{
		if ( ok )
		{
			Passed++;
			Console.WriteLine( $"  ok    {what}" );
		}
		else
		{
			Failed++;
			Console.WriteLine( $"  FAIL  {what}{(detail is null ? "" : $"  [{detail}]")}" );
		}
	}
}
