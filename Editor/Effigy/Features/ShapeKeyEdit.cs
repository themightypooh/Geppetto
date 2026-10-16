using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Sculpting into a shape key that is already finished.
///
/// A key is a run of history: <see cref="ShapeKeyFeature"/>, the features that shape it,
/// <see cref="ShapeKeyEndFeature"/>. Everything between the two is the key, so editing a finished key
/// is putting more features into that run, just above its End - not a second key, and not a change
/// to the body: End still turns all the movement since Shape Key into the key's offsets and puts
/// the body back.
///
/// To see what you are sculpting on, <see cref="Reopen"/> moves the rollback bar to just above the
/// End: the model shows the key fully on, and whatever is added at the bar lands inside the key.
/// <see cref="Close"/> puts the bar back at the end. Scripts skip the bar and use
/// <see cref="AddToKey(PartStudio, string, Feature, out string)"/> (`add … inkey=name`).
/// </summary>
public static class ShapeKeyEdit
{
	/// <summary>One key in the history. End is null, and EndIndex -1, while it is still being recorded.</summary>
	public readonly record struct Run( IShapeKeyStart Start, ShapeKeyEndFeature End, int StartIndex, int EndIndex )
	{
		public bool Finished => End is not null;
	}

	/// <summary>Every key in the history, in order; with <paramref name="body"/>, only the keys on it.</summary>
	public static List<Run> Runs( PartStudio studio, Body body = null )
	{
		var runs = new List<Run>();
		var features = studio?.Features;

		if ( features is null )
			return runs;

		for ( var i = 0; i < features.Count; i++ )
		{
			if ( features[i] is not IShapeKeyStart start || (body is not null && !start.KeyBodies.Matches( body )) )
				continue;

			var end = -1;

			for ( var j = i + 1; j < features.Count; j++ )
			{
				if ( features[j] is ShapeKeyEndFeature ) { end = j; break; }
				if ( features[j] is IShapeKeyStart ) break;
			}

			runs.Add( new( start, end < 0 ? null : (ShapeKeyEndFeature)features[end], i, end ) );
		}

		return runs;
	}

	/// <summary>The finished key being edited: the rollback bar sits inside its run, so its End is
	/// not evaluated and features added at the bar go into it. Null when none is.</summary>
	public static Run? Editing( PartStudio studio, Body body = null )
	{
		if ( studio is null || studio.RollbackIndex >= studio.Features.Count )
			return null;

		foreach ( var run in Runs( studio, body ) )
		{
			if ( run.Finished && studio.RollbackIndex > run.StartIndex && studio.RollbackIndex <= run.EndIndex )
				return run;
		}

		return null;
	}

	/// <summary>
	/// Open a finished key for more sculpting: move the rollback bar to just above its End Shape Key.
	/// Refuses, saying why, when the key is still recording (nothing to reopen) or another key is open.
	/// </summary>
	public static bool Reopen( PartStudio studio, IShapeKeyStart start, out string why )
	{
		why = null;
		var runs = Runs( studio );
		var index = runs.FindIndex( r => ReferenceEquals( r.Start, start ) );

		if ( index < 0 )
		{
			why = "That shape key is no longer in the history.";
			return false;
		}

		var run = runs[index];

		if ( !run.Finished )
		{
			why = $"'{start.KeyName}' is still recording: sculpt into it now, and press Finish key when it is done.";
			return false;
		}

		if ( runs.FirstOrDefault( r => !r.Finished && r.StartIndex < studio.EffectiveCount ) is { Start: { } open } )
		{
			why = $"'{open.KeyName}' is still recording. Finish it first: keys are shaped one at a time.";
			return false;
		}

		studio.RollbackIndex = run.EndIndex;
		return true;
	}

	/// <summary>Stop editing a reopened key: the bar goes back to the end and the key's End runs
	/// again. Returns the key that was being edited, or null when none was.</summary>
	public static IShapeKeyStart Close( PartStudio studio )
	{
		if ( Editing( studio ) is not { } run )
			return null;

		studio.RollbackIndex = int.MaxValue;
		return run.Start;
	}

	/// <summary>
	/// Put <paramref name="feature"/> into a finished key, just above its End Shape Key, so what it
	/// does is added to the key. The last key of that name is used, as it is the one End keeps.
	/// </summary>
	public static bool AddToKey( PartStudio studio, string keyName, Feature feature, out string why )
	{
		why = null;
		var runs = Runs( studio ).Where( r => string.Equals( r.Start.KeyName, keyName?.Trim(), StringComparison.Ordinal ) ).ToList();

		if ( runs.Count == 0 )
		{
			var names = Runs( studio ).Select( r => r.Start.KeyName ).Distinct().ToList();
			why = $"No shape key called '{keyName}'. " + (names.Count == 0 ? "There are no shape keys yet: add ShapeKey … ShapeKeyEnd first."
				: $"Keys: {string.Join( ", ", names )}.");
			return false;
		}

		return AddToKey( studio, runs[^1].Start, feature, out why );
	}

	/// <inheritdoc cref="AddToKey(PartStudio, string, Feature, out string)"/>
	public static bool AddToKey( PartStudio studio, IShapeKeyStart start, Feature feature, out string why )
	{
		why = null;

		if ( feature is null )
		{
			why = "There is no feature to add to the key.";
			return false;
		}

		if ( feature is IShapeKeyStart or ShapeKeyEndFeature )
		{
			why = "A shape key cannot hold another Shape Key or End Shape Key. Add the sculpt, stroke or detail that shapes it.";
			return false;
		}

		var run = Runs( studio ).FirstOrDefault( r => ReferenceEquals( r.Start, start ) );

		if ( run.Start is null )
		{
			why = "That shape key is no longer in the history.";
			return false;
		}

		if ( !run.Finished )
		{
			why = $"'{start.KeyName}' is still recording: add the feature after it, before End Shape Key.";
			return false;
		}

		studio.Insert( run.EndIndex, feature );

		// A bar at or below the End moves with it, so the new feature is evaluated whenever the End was.
		if ( studio.RollbackIndex >= run.EndIndex && studio.RollbackIndex < int.MaxValue )
			studio.RollbackIndex++;

		return true;
	}
}
