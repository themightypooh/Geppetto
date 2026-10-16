using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

// ============================================================================
// Paint ▸ Bake Selected to Active and Paint ▸ Bake by Name: baking any detailed body onto any body
// with UVs, from the selection or from _low / _high names. Same kernel path as the `bake` script
// verb (NormalBake.BakeGroups through BakePairs), same maps, size and material as the sculpt's own
// Bake (Edit ▸ Settings ▸ Bake), same files (BakeMaterial.WriteSet).
// ============================================================================

public sealed partial class EffigyWindow
{
	private const string BakeSixteenBitCookie = "Effigy.BakeSixteenBit";
	private const string BakeCageCookie = "Effigy.BakeCage";

	/// <summary>Settings ▸ Bake: write height and the normal maps as 16-bit PNGs (BakeOptions.SixteenBit).</summary>
	private bool _bakeSixteenBit;

	/// <summary>Settings ▸ Bake: how far out the rays start, in model units; 0 measures it (BakeOptions.Cage).</summary>
	private float _bakeCage;

	/// <summary>
	/// Bake the selected bodies onto the active one — the body clicked LAST (Shift+click adds to the
	/// selection, so the one added last is active). A selected body named …_cage is the cage, not a
	/// source of detail (BakePairs.SelectedToActive).
	/// </summary>
	private void BakeSelectedToActive()
	{
		if ( _viewport is null || _studio is null )
			return;

		var ids = _viewport.IdleBodyIds.ToList();

		if ( ids.Count < 2 )
		{
			Refuse( "Select the detailed body, then Shift+click the body to bake onto, so it is selected last." );
			return;
		}

		var bodies = ids.Select( id => _studio.Bodies.FirstOrDefault( b => b.Id == id ) ).Where( b => b?.Mesh is not null ).ToList();
		var active = _studio.Bodies.FirstOrDefault( b => b.Id == ids[^1] );

		if ( active?.Mesh is null )
		{
			Refuse( "The body selected last has no mesh to bake onto." );
			return;
		}

		var pair = BakePairs.SelectedToActive( (BakeBodyName( active ), active.Mesh),
			bodies.Where( b => b.Id != active.Id ).Select( b => (BakeBodyName( b ), b.Mesh) ), out var problem );

		if ( pair is null )
		{
			Refuse( $"Cannot bake: {problem}" );
			return;
		}

		RunPairBake( new List<BakePairs.Pair> { pair }, BakeBodyName( active ) );
	}

	/// <summary>
	/// Bake every body named X_low from the X_high bodies of the same name (X_high_2 … too), with
	/// X_cage as its cage when there is one. Each pair only sees its own detail, so parts sitting
	/// close together do not darken or catch each other; all of them land in one set of maps.
	/// </summary>
	private void BakeByName()
	{
		if ( _studio is null )
			return;

		var pairs = BakePairs.Match( _studio.Bodies.Select( b => (BakeBodyName( b ), b.Mesh) ), out var problems );

		foreach ( var p in problems )
			Log.Warning( $"[Effigy] bake by name: {p}" );

		if ( pairs.Count == 0 )
		{
			Refuse( "No bodies are named as bake pairs. Name the body that gets the texture X_low and the detailed one X_high "
				+ "(X_cage for a cage), then bake again." + (problems.Count > 0 ? " " + problems[0] : "") );
			return;
		}

		if ( problems.Count > 0 )
		{
			_viewport?.ShowCallout( "bake-pairs", EffigyCalloutKind.Refusal,
				$"{problems.Count} body name(s) did not pair up and are left out: {problems[0]}"
				+ (problems.Count > 1 ? " The rest are in the console." : ""), lifetime: RefusalSeconds );
		}

		RunPairBake( pairs, pairs.Count == 1 ? pairs[0].Name : "bake" );
	}

	private static string BakeBodyName( Body body ) => body.Name ?? body.Id;

	/// <summary>
	/// Check the UVs, ask where, bake, write. The UVs of every low body are checked as ONE atlas
	/// before anything is written: a bake over overlapping UVs does not fail, it writes a map that
	/// is wrong wherever two faces shared a texel.
	/// </summary>
	private void RunPairBake( List<BakePairs.Pair> pairs, string defaultName )
	{
		var coverage = NormalBake.Measure( pairs.Select( p => p.Low ) );

		if ( !coverage.CanBake )
		{
			Refuse( $"Cannot bake onto {string.Join( ", ", pairs.Select( p => p.LowName ) )}: {coverage.Problem} "
				+ (pairs.Count > 1 ? "Bodies baked by name share one texture, so their UVs must not overlap each other either. " : "")
				+ "Unwrap first: Paint ▸ UV Project, mode Unwrap." );
			return;
		}

		var fd = new FileDialog( null )
		{
			Title = "Bake maps to...",
			DefaultSuffix = ".png",
			Directory = Project.Current?.GetAssetsPath() ?? "",
		};

		fd.SelectFile( $"{defaultName}_normal.png" );
		fd.SetFindFile();
		fd.SetModeSave();
		fd.SetNameFilter( "PNG image (*.png)" );

		if ( !fd.Execute() )
			return;

		try
		{
			// The chosen file names the set: "Arm_normal.png" bakes Arm_normal.png, Arm_ao.png, ...
			var folder = Path.GetDirectoryName( fd.SelectedFile ) ?? "";
			var name = Path.GetFileNameWithoutExtension( fd.SelectedFile );

			if ( name.EndsWith( "_normal", StringComparison.OrdinalIgnoreCase ) )
				name = name[..^"_normal".Length];

			var options = new BakeOptions { FlipGreen = _bakeFlipGreen, SixteenBit = _bakeSixteenBit, Cage = _bakeCage };
			var set = NormalBake.BakeGroups( BakePairs.ToGroups( pairs ), _bakeSize, _bakeSize, _bakeMaps | BakeMaps.Normal, options );
			var material = BakeMaterial.MaterialChoices[Math.Clamp( _bakeMaterial, 0, BakeMaterial.MaterialChoices.Length - 1 )];
			var written = BakeMaterial.WriteSet( set, folder, name, _bakeFlipV, Project.Current?.GetAssetsPath(), material );

			var convention = $"{(_bakeFlipGreen ? "DirectX" : "OpenGL (s&box)")} green, v = 0 at the "
				+ $"{(_bakeFlipV ? "bottom" : "top")}" + (_bakeSixteenBit ? ", 16-bit height and normals" : "");
			var what = string.Join( "; ", pairs.Select( p => $"{p.LowName} from {string.Join( ", ", p.Highs.Select( h => h.Name ) )}"
				+ (p.Cage is not null ? $" (cage {p.CageName})" : "") ) );

			SetPrompt( $"Baked {written.Count} file(s) at {_bakeSize}×{_bakeSize} to {folder}: {what} — {set.Normal.FilledCount:N0} texels hit, "
				+ convention + ". " + set.Report.Replace( "\n", "; " ) );

			Log.Info( $"[Effigy] baked {string.Join( ", ", written.Select( Path.GetFileName ) )} to {folder}: {what} ({convention})" );

			// A bake where most rays found nothing still writes files - flat ones. Say so, with the fix.
			if ( set.Samples > 0 && set.Hits < set.Samples / 2 )
			{
				_viewport?.ShowCallout( "bake-misses", EffigyCalloutKind.Refusal,
					$"Only {(float)set.Hits / set.Samples:P0} of the bake's rays found the detailed body, so much of the map is flat. "
					+ "Make sure the bodies overlap in place, or set a Cage distance in Edit ▸ Settings ▸ Bake.", lifetime: FailureSeconds );
			}
		}
		catch ( Exception e )
		{
			// Writing a file is the one place failing quietly is unforgivable, same as Save.
			Log.Error( $"[Effigy] could not bake to {fd.SelectedFile}: {e.Message}" );
			ReportFailure( $"Bake failed: {e.Message}" );
		}
	}
}
