using System;

namespace Effigy.Tests;

/// <summary>Shared pass/fail tally, so the geometry checks and the feature-tree checks report into
/// one place and one exit code.</summary>
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
