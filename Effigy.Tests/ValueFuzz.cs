using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Every number on every feature, pushed past where it makes sense, to find the values the
/// editor takes without a word.
///
///   dotnet run -- --fuzz-values [report.md]
///
/// Each feature is built on the same fixture AllFeaturesTests uses, then each of its float and
/// int parameters is set to values a person could type — past the limit, zero, negative, huge,
/// tiny — and the studio rebuilt. Each try lands in one bucket:
///
///   clamped     held at a declared limit; the editor says so in a callout
///   explained   failed with a cause and a remedy; the editor shows it as a callout
///   ok          built something sane
///   UNHELPFUL   failed, but with a bare message and no cause or remedy (a raw exception)
///   SILENT      no error, and nothing usable came out: no bodies, empty or NaN geometry
///   NOOP        no error, but the model is exactly what it was without the feature — the value
///               was quietly ignored (only flagged for features that DO change it at defaults)
///   BROKEN      no error, but a solid came out inside-out, flat, or leaking where it was closed
///   CRASH       an exception escaped Rebuild itself
///   HANG        did not finish within the time limit
///
/// The capitalised ones are the holes: the editor either says nothing or says something useless.
/// </summary>
public static class ValueFuzz
{
	const int TimeoutMs = 4000;

	public static int Run( string reportPath )
	{
		var rows = new List<(string Feature, string Param, string Value, string Outcome, string Detail)>();

		foreach ( var type in AllFeaturesTests.FeatureTypes() )
		{
			var probe = (Feature)Activator.CreateInstance( type );
			var labels = probe.Parameters.Concat( probe.AdvancedParameters )
				.Where( p => p is FloatParam or IntParam )
				.Select( p => p.Label )
				.Distinct()
				.ToList();

			var before = Fingerprint( Baseline( type, withFeature: false ) );
			var atDefaults = Fingerprint( Baseline( type, withFeature: true ) );
			var defaultsChange = before != atDefaults;

			foreach ( var label in labels )
			{
				foreach ( var value in ValuesFor( probe.Parameters.Concat( probe.AdvancedParameters ).First( p => p.Label == label ) ) )
				{
					var (outcome, detail) = Try( type, label, value, before, defaultsChange );
					rows.Add( (type.Name, label, Fmt( value ), outcome, detail) );
				}
			}
		}

		var holes = rows.Where( r => r.Outcome is "UNHELPFUL" or "SILENT" or "CRASH" or "HANG" or "NOOP" or "BROKEN" ).ToList();

		var sb = new StringBuilder();
		sb.AppendLine( "# Value fuzz" );
		sb.AppendLine();

		foreach ( var g in rows.GroupBy( r => r.Outcome ).OrderBy( g => g.Key ) )
			sb.AppendLine( $"- {g.Key}: {g.Count()}" );

		sb.AppendLine();
		sb.AppendLine( "## Holes" );
		sb.AppendLine();
		sb.AppendLine( "| Feature | Parameter | Value | Outcome | Detail |" );
		sb.AppendLine( "|---|---|---|---|---|" );

		foreach ( var r in holes )
			sb.AppendLine( $"| {r.Feature} | {r.Param} | {r.Value} | {r.Outcome} | {r.Detail.Replace( "|", "/" ).Replace( "\n", " " )} |" );

		File.WriteAllText( reportPath, sb.ToString() );
		Console.WriteLine( $"{rows.Count} tries, {holes.Count} holes -> {reportPath}" );

		// Abandoned hangs are still running on the pool; do not wait for them.
		Environment.Exit( 0 );
		return 0;
	}

	static string Fmt( float v ) => float.IsNaN( v ) ? "NaN" : v.ToString( "0.######", System.Globalization.CultureInfo.InvariantCulture );

	/// <summary>The values a person could plausibly type into this box that it should either
	/// refuse, hold at a limit, or explain.</summary>
	static IEnumerable<float> ValuesFor( IParam param )
	{
		var set = new List<float>();

		if ( param is FloatParam f )
		{
			if ( f.Min > float.MinValue ) set.Add( f.Min - MathF.Max( 1f, MathF.Abs( f.Min ) ) );
			if ( f.Max < float.MaxValue ) set.Add( f.Max + MathF.Max( 1f, MathF.Abs( f.Max ) ) );
			set.AddRange( new[] { 0f, -1f, -100f, 1e5f, 1e-6f } );

			if ( f.Value != 0f )
				set.Add( f.Value * 50f );
		}
		else if ( param is IntParam i )
		{
			if ( i.Min > int.MinValue ) set.Add( i.Min - 1 );
			if ( i.Max < int.MaxValue ) set.Add( i.Max + 1 );
			set.AddRange( new[] { 0f, -1f, -100f } );

			// A count with no ceiling is the one most likely to be typed huge.
			if ( i.Max == int.MaxValue )
				set.Add( 5000f );
		}

		return set.Distinct();
	}

	/// <summary>The fixture with or without the feature at its defaults, for comparing against.</summary>
	static PartStudio Baseline( Type type, bool withFeature )
	{
		var studio = AllFeaturesTests.WithInput();
		var feature = (Feature)Activator.CreateInstance( type );
		AllFeaturesTests.GivePickedInput( feature, studio );

		if ( !withFeature )
		{
			studio.Rebuild();
			return studio;
		}

		studio.Add( feature );
		var previous = MeshBoolean.Provider;

		if ( feature is HoleFeature or BooleanFeature )
			MeshBoolean.Provider = new AllFeaturesTests.SweepBoolean();

		try { studio.Rebuild(); }
		finally { MeshBoolean.Provider = previous; }

		return studio;
	}

	/// <summary>Enough of the model to tell two apart: per body, its counts, where its vertices
	/// are to a few decimals, and which materials its faces wear.</summary>
	static string Fingerprint( PartStudio studio )
	{
		var sb = new StringBuilder();

		foreach ( var body in studio.Bodies.OrderBy( b => b.Name ) )
		{
			var m = body.Mesh;
			double sx = 0, sy = 0, sz = 0;

			foreach ( var p in m.Positions )
			{
				sx += p.x;
				sy += p.y;
				sz += p.z * 1.37;
			}

			var materials = m.Faces.Sum( f => (long)(f.Material + 1) );
			sb.Append( $"{body.Name}:{m.VertexCount}/{m.FaceCount}/{sx:0.###}/{sy:0.###}/{sz:0.###}/{materials};" );
		}

		return sb.ToString();
	}

	static (string, string) Try( Type type, string label, float value, string before, bool defaultsChange )
	{
		var studio = AllFeaturesTests.WithInput();
		var feature = (Feature)Activator.CreateInstance( type );
		AllFeaturesTests.GivePickedInput( feature, studio );
		studio.Add( feature );

		var param = feature.Parameters.Concat( feature.AdvancedParameters ).First( p => p.Label == label );
		var clamped = false;

		switch ( param )
		{
			case FloatParam f:
				f.Value = value;
				clamped = f.Clamped != value;
				break;
			case IntParam i:
				i.Value = (int)value;
				clamped = i.Clamped != (int)value;
				break;
		}

		var previousProvider = MeshBoolean.Provider;

		if ( feature is HoleFeature or BooleanFeature )
			MeshBoolean.Provider = new AllFeaturesTests.SweepBoolean();

		try
		{
			var task = Task.Run( () => studio.Rebuild() );

			if ( !task.Wait( TimeoutMs ) )
				return ("HANG", $"over {TimeoutMs} ms");
		}
		catch ( AggregateException e )
		{
			return ("CRASH", e.InnerException?.Message ?? e.Message);
		}
		finally
		{
			MeshBoolean.Provider = previousProvider;
		}

		if ( clamped )
			return ("clamped", "");

		if ( feature.Error is not null )
		{
			var d = feature.Diagnostic;
			var explained = d is not null && !string.IsNullOrEmpty( d.Cause ) && d.Remedies.Count > 0;
			return explained ? ("explained", d.Problem) : ("UNHELPFUL", feature.Error);
		}

		if ( feature.Warning is not null )
			return ("explained", feature.Diagnostic?.Problem ?? feature.Warning);

		if ( feature is SketchFeature or PlaneFeature )
			return ("ok", "");

		if ( studio.Bodies.Count == 0 )
			return ("SILENT", "no bodies");

		foreach ( var body in studio.Bodies )
		{
			if ( body.Mesh.FaceCount == 0 )
				return ("SILENT", $"{body.Name} has no faces");

			// Closed solids should stay closed and the right way out. An open shell or a garment
			// is allowed to be open, so only a closed body is held to it.
			var validation = MeshValidator.Validate( body.Mesh );

			if ( !validation.IsValid )
				return ("BROKEN", $"{body.Name}: {validation}");

			if ( validation.IsClosed )
			{
				var volume = body.Mesh.SignedVolume();

				if ( volume <= 1e-6f )
					return ("BROKEN", $"{body.Name} is inside-out or flat (volume {volume:0.####})");
			}

			foreach ( var p in body.Mesh.Positions )
			{
				if ( float.IsNaN( p.x ) || float.IsNaN( p.y ) || float.IsNaN( p.z ) || float.IsInfinity( p.x ) )
					return ("SILENT", $"{body.Name} has NaN/infinite vertices");
			}
		}

		if ( defaultsChange && feature is not SketchFeature and not PlaneFeature && Fingerprint( studio ) == before )
			return ("NOOP", "model unchanged");

		return ("ok", "");
	}
}
