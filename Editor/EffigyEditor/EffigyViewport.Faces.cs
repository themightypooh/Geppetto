using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Finished sketches, and the faces they close.
///
/// Sketch geometry used to exist only while you were inside the sketch: SketchFrame returned
/// immediately when ActiveSketch was null, so leaving a sketch made every line and point you had
/// just drawn vanish. The sketch was still there in the tree and still feeding the extrude above
/// it — there was simply nothing on screen to show for it, which made the viewport disagree with
/// the feature list about what the model was built from.
///
/// So every sketch in the studio draws all the time, dimmed, with its closed regions shaded. A
/// shaded region IS the face: hover it and it lights up, click it while a feature is asking and
/// that feature gets built from that region alone.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>
	/// Every sketch in the studio, paired with the feature id that owns it and its closed regions.
	/// Refreshed by the window after each rebuild — the viewport never reaches into the studio.
	///
	/// The profiles are CACHED here rather than re-found per frame. ProfileFinder.Find walks the
	/// whole curve graph and allocates as it goes; calling it once to draw and once to hit-test,
	/// for every sketch, on every frame, is a graph walk per sketch per frame for a result that
	/// only changes on rebuild. The window pushes a fresh set whenever the studio rebuilds, which
	/// includes every sketch edit.
	/// </summary>
	private readonly List<(string FeatureId, Sketch Sketch, List<Profile> Profiles)> _visibleSketches = new();

	/// <summary>While true, closed regions are pickable and highlight on hover. Set by the feature
	/// dialog when its region selection box is armed.</summary>
	public bool RegionPickMode { get; set; }

	/// <summary>Fires with the owning sketch feature's id and a point inside the picked region.
	/// The point is the click itself, which is inside the region by construction.</summary>
	public Action<string, Vec2> RegionPicked { get; set; }

	/// <summary>The region under the cursor this frame, if any.</summary>
	private (string FeatureId, Sketch Sketch, Profile Profile)? _hoveredRegion;

	/// <summary>Regions already chosen by some feature, so the ones in use read differently from
	/// the ones going spare. Keyed by feature id and seed point.</summary>
	private readonly List<(string FeatureId, Vec2 Seed)> _selectedRegions = new();

	private static readonly Color FinishedCurveColor = new( 0.42f, 0.55f, 0.68f, 0.75f );
	private static readonly Color FinishedPointColor = new( 0.70f, 0.76f, 0.82f, 0.55f );
	private static readonly Color FinishedRegionColor = new( 0.30f, 0.55f, 0.85f, 0.07f );
	private static readonly Color PickableRegionColor = new( 0.30f, 0.62f, 0.95f, 0.16f );
	private static readonly Color HoveredRegionColor = new( 0.35f, 0.75f, 1f, 0.34f );
	private static readonly Color SelectedRegionColor = new( 0.30f, 0.85f, 0.55f, 0.24f );

	/// <summary>Replace the set of sketches drawn in the viewport.</summary>
	public void SetVisibleSketches( IEnumerable<(string FeatureId, Sketch Sketch)> sketches )
	{
		_visibleSketches.Clear();

		if ( sketches is null )
			return;

		foreach ( var (featureId, sketch) in sketches )
		{
			if ( sketch is null )
				continue;

			_visibleSketches.Add( (featureId, sketch, ProfileFinder.Find( sketch ).Profiles) );
		}
	}

	/// <summary>Tell the viewport which regions are already spoken for, so they can be drawn as
	/// taken rather than as free.</summary>
	public void SetSelectedRegions( IEnumerable<(string FeatureId, Vec2 Seed)> regions )
	{
		_selectedRegions.Clear();

		if ( regions is not null )
			_selectedRegions.AddRange( regions );
	}

	/// <summary>
	/// Draw every finished sketch, and resolve what the cursor is over.
	///
	/// Called from OnPreFrame unconditionally — this is the pass that runs whether or not a sketch
	/// is open. The sketch currently being edited is skipped here and drawn by SketchFrame's own
	/// brighter path instead, so it does not get painted twice.
	/// </summary>
	private void DrawFinishedSketches()
	{
		_hoveredRegion = null;

		if ( _visibleSketches.Count == 0 )
			return;

		Gizmo.Draw.IgnoreDepth = true;

		// Resolve hover first so the region can be drawn in its hovered colour in the same pass.
		if ( RegionPickMode && _canvasHasCursor )
			_hoveredRegion = HitTestRegion();

		foreach ( var (featureId, sketch, profiles) in _visibleSketches )
		{
			if ( ReferenceEquals( sketch, ActiveSketch ) )
				continue;

			DrawFinishedSketch( featureId, sketch, profiles );
		}

		Gizmo.Draw.IgnoreDepth = false;

		// The click is taken here rather than in SketchFrame: picking a face happens from outside
		// sketch mode, where SketchFrame has already returned.
		if ( RegionPickMode && _hoveredRegion is { } hit && Gizmo.WasLeftMousePressed
			&& CursorToPlane( hit.Sketch.Plane, out var seed ) )
		{
			RegionPicked?.Invoke( hit.FeatureId, seed );
		}
	}

	private void DrawFinishedSketch( string featureId, Sketch sketch, List<Profile> profiles )
	{
		// Regions first, so the edges draw over their own fill.
		foreach ( var profile in profiles )
		{
			Gizmo.Draw.Color = RegionColorFor( featureId, profile );
			DrawRegionFan( sketch.Plane, profile.Outer );
		}

		Gizmo.Draw.LineThickness = 1.5f;

		foreach ( var curve in sketch.Curves )
		{
			Gizmo.Draw.Color = curve.Construction
				? SketchConstructionColor.WithAlpha( 0.35f )
				: FinishedCurveColor;

			var pts = curve.Tessellate( sketch, sketch.Tolerance );

			for ( var i = 0; i < pts.Count - 1; i++ )
			{
				// Construction geometry stays dashed when it is not the active sketch either.
				if ( curve.Construction && i % 2 == 1 )
					continue;

				Gizmo.Draw.Line( PlaneToWorld( sketch.Plane, pts[i] ), PlaneToWorld( sketch.Plane, pts[i + 1] ) );
			}
		}

		Gizmo.Draw.Color = FinishedPointColor;

		foreach ( var p in sketch.Points )
			Gizmo.Draw.SolidSphere( PlaneToWorld( sketch.Plane, p ), 0.3f, 6, 6 );

		Gizmo.Draw.LineThickness = 1f;
	}

	/// <summary>
	/// Which colour a region draws in: hovered, already taken by a feature, free and pickable, or
	/// just sitting there while nothing is asking for a face.
	/// </summary>
	private Color RegionColorFor( string featureId, Profile profile )
	{
		if ( _hoveredRegion is { } hovered
			&& hovered.FeatureId == featureId
			&& ReferenceEquals( hovered.Profile, profile ) )
			return HoveredRegionColor;

		if ( _selectedRegions.Any( r => r.FeatureId == featureId && profile.Contains( r.Seed ) ) )
			return SelectedRegionColor;

		return RegionPickMode ? PickableRegionColor : FinishedRegionColor;
	}

	/// <summary>
	/// Which region the cursor is inside, across every visible sketch.
	///
	/// Nearest plane wins when two sketches overlap on screen: a click has to mean one face, and
	/// the one in front is the one the user can see. Ties inside a single sketch go to the
	/// SMALLEST region, so a small face sitting inside a larger one stays reachable rather than
	/// being permanently covered by its parent.
	/// </summary>
	private (string FeatureId, Sketch Sketch, Profile Profile)? HitTestRegion()
	{
		(string FeatureId, Sketch Sketch, Profile Profile)? best = null;
		var bestArea = float.MaxValue;
		var bestDistance = float.MaxValue;

		foreach ( var (featureId, sketch, profiles) in _visibleSketches )
		{
			if ( !CursorToPlane( sketch.Plane, out var uv ) )
				continue;

			var distance = (PlaneToWorld( sketch.Plane, uv ) - Gizmo.CurrentRay.Position).Length;

			foreach ( var profile in profiles )
			{
				if ( !profile.Contains( uv ) )
					continue;

				// A plane meaningfully nearer the camera wins outright; within the same plane it
				// comes down to which region is smaller.
				var nearer = distance < bestDistance - 0.01f;
				var sameDepth = MathF.Abs( distance - bestDistance ) <= 0.01f;

				if ( !nearer && !(sameDepth && profile.Area < bestArea) )
					continue;

				best = (featureId, sketch, profile);
				bestArea = profile.Area;
				bestDistance = distance;
			}
		}

		return best;
	}

	/// <summary>Fan-fill a loop on a given plane. The active sketch's own version delegates here.</summary>
	private void DrawRegionFan( SketchPlane plane, List<Vec2> loop )
	{
		if ( loop.Count < 3 )
			return;

		var centre = loop[0];

		for ( var i = 1; i < loop.Count - 1; i++ )
		{
			Gizmo.Draw.SolidTriangle( new Triangle(
				PlaneToWorld( plane, centre ),
				PlaneToWorld( plane, loop[i] ),
				PlaneToWorld( plane, loop[i + 1] ) ) );
		}
	}
}
