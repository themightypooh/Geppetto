using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Feature.Accepts against what Feature.ApplyGeometrySelection actually does.
///
/// WHY THIS IS A TEST RATHER THAN A READING. Accepts exists to collapse three copies of one fact —
/// the type switch that consumes a selection, the prose the editor paints under the viewport, and
/// the per-dialog pick-mode flags. Collapsing them is only worth anything if the surviving copy is
/// TRUE, and a declaration that quietly disagrees with the code beneath it is worse than the three
/// copies were: the editor would dim a button that works, or offer one that ignores you.
///
/// So nothing here reads the declaration and nods at it. Every feature type is found by reflection,
/// handed a selection of exactly one KIND, and watched: if applying faces changes the feature, it
/// had better say Face, and if it says Face something had better change. Forgetting to declare is
/// not an available mistake, and neither is declaring something that is not true.
///
/// One asymmetry is deliberate and is asserted rather than worked around: a FACE selection also
/// names its face's BODY, so a body-only tool legitimately reacts to a face click by taking the
/// part. That is why the fingerprint below is split in two — the body-selection half and everything
/// else — instead of being one lump that cannot tell those apart.
/// </summary>
public static class AcceptsTests
{
	public static void Run()
	{
		Report.Section( "accepts: the declaration matches what the feature consumes" );
		TestEveryFeatureAgreesWithItself();

		Report.Section( "accepts: a body selection stays a body selection" );
		TestBodyOnlySelection();

		Report.Section( "accepts: the tools a face is worth offering" );
		TestFaceConsumers();
	}

	/// <summary>
	/// For every feature: apply one kind of pick at a time, and require the declaration to predict
	/// whether anything moved.
	/// </summary>
	static void TestEveryFeatureAgreesWithItself()
	{
		var fixture = Fixture( out var body, out var sketchId );
		var faces = new[] { FaceOf( body, v => v.z > 0.99f ) };
		var edges = EdgesOf( body );

		foreach ( var type in FeatureTypes() )
		{
			var declared = Create( type ).Accepts;

			// --- faces --------------------------------------------------------------------------
			var feature = Create( type );
			var before = Fingerprint( feature );
			feature.ApplyGeometrySelection( faces, Array.Empty<string>(), fixture.Bodies );
			var after = Fingerprint( feature );

			Report.Check( $"{type.Name}: face pick is consumed exactly as declared",
				(after.Geometry != before.Geometry) == declared.HasFlag( GeometryKind.Face ),
				$"declared {declared}, geometry {before.Geometry} -> {after.Geometry}" );

			// A face names its body, so a body tool takes the part it was pointed at. Onshape does
			// the same thing and it is why clicking a face then Fillet fillets that part.
			Report.Check( $"{type.Name}: the body behind a picked face lands where declared",
				(after.Bodies != before.Bodies) == declared.HasFlag( GeometryKind.Body ),
				$"declared {declared}, bodies {before.Bodies} -> {after.Bodies}" );

			// --- edges --------------------------------------------------------------------------
			feature = Create( type );
			before = Fingerprint( feature );
			feature.ApplyGeometrySelection( Array.Empty<FaceRef>(), Array.Empty<string>(),
				fixture.Bodies, edges );
			after = Fingerprint( feature );

			Report.Check( $"{type.Name}: edge pick is consumed exactly as declared",
				(after.Geometry != before.Geometry) == declared.HasFlag( GeometryKind.Edge ),
				$"declared {declared}, geometry {before.Geometry} -> {after.Geometry}" );

			// --- a sketch, and one region of it -------------------------------------------------
			feature = Create( type );
			before = Fingerprint( feature );
			feature.ApplyGeometrySelection( Array.Empty<FaceRef>(), Array.Empty<string>(),
				fixture.Bodies, null, sketchId, new[] { new Vec2( 0.5f, 0.5f ) } );
			after = Fingerprint( feature );

			Report.Check( $"{type.Name}: sketch pick is consumed exactly as declared",
				(after.Geometry != before.Geometry) == declared.HasFlag( GeometryKind.SketchRegion ),
				$"declared {declared}, geometry {before.Geometry} -> {after.Geometry}" );
		}
	}

	/// <summary>
	/// A part clicked in the Parts list is a body and nothing else. Nothing may invent a face out of
	/// it — the tools that need one still have to ask.
	/// </summary>
	static void TestBodyOnlySelection()
	{
		var fixture = Fixture( out var body, out _ );

		foreach ( var type in FeatureTypes() )
		{
			var declared = Create( type ).Accepts;
			var feature = Create( type );
			var before = Fingerprint( feature );

			feature.ApplyGeometrySelection( Array.Empty<FaceRef>(), new[] { body.Id }, fixture.Bodies );

			var after = Fingerprint( feature );

			Report.Check( $"{type.Name}: takes a picked part only if it declares Body",
				(after.Bodies != before.Bodies) == declared.HasFlag( GeometryKind.Body ),
				$"declared {declared}, bodies {before.Bodies} -> {after.Bodies}" );

			Report.Check( $"{type.Name}: a part alone is not a face",
				after.Geometry == before.Geometry,
				$"geometry {before.Geometry} -> {after.Geometry}" );
		}
	}

	/// <summary>
	/// The list the editor's hint line and the right-click face menu are generated FROM, pinned
	/// here so a tool cannot fall off it silently — which is the exact complaint that started this:
	/// a face was selected and the tool that wanted it was not on the list.
	/// </summary>
	static void TestFaceConsumers()
	{
		var consumers = FeatureTypes()
			.Where( t => Create( t ).Accepts.HasFlag( GeometryKind.Face ) )
			.Select( t => t.Name )
			.ToList();

		foreach ( var expected in new[]
		{
			nameof( SketchFeature ), nameof( DraftFeature ), nameof( HoleFeature ),
			nameof( FaceMaterialFeature ), nameof( SubdivideFeature ), nameof( ShellFeature ),
			nameof( FilletFeature ), nameof( ChamferFeature ),

			// Extrude joined this list when a mesh face became a profile it can pull. It used to be
			// pinned here as deliberately ABSENT, with a note saying that the day it changed should
			// be a day something said so out loud — this is that, kept rather than deleted.
			nameof( ExtrudeFeature ), nameof( MoveFaceFeature ),
		} )
		{
			Report.Check( $"{expected} is offered a face", consumers.Contains( expected ),
				string.Join( ", ", consumers ) );
		}

		// Revolve is the one that is deliberately NOT here. Spinning a mesh face about an axis is a
		// real operation and nothing has built it, so saying it takes a face would be a lie of the
		// exact kind Accepts exists to stop. This is what says so when that changes.
		Report.Check( "revolve does not claim a face it cannot use yet",
			!consumers.Contains( nameof( RevolveFeature ) ), string.Join( ", ", consumers ) );
	}

	// --- the fingerprint --------------------------------------------------------------------------

	/// <summary>
	/// What a feature is holding, in two halves: the bodies it has been pointed at, and every other
	/// piece of picked geometry on it.
	///
	/// BY REFLECTION over the public fields rather than a switch naming the ones known today. A
	/// switch here would be a fourth copy of the fact this whole exercise exists to have one of, and
	/// it would go stale the same way. Counts are enough — every consuming path fills something that
	/// started empty or sets something that started null.
	/// </summary>
	readonly struct Snapshot
	{
		public Snapshot( string bodies, string geometry )
		{
			Bodies = bodies;
			Geometry = geometry;
		}

		public string Bodies { get; }
		public string Geometry { get; }
	}

	static Snapshot Fingerprint( Feature feature )
	{
		var bodies = string.Join( "/", feature.Parameters
			.OfType<BodySelectionParam>()
			.Select( p => $"{p.Label}:{p.BodyIds.Count}" ) );

		var geometry = new List<string>();

		foreach ( var field in feature.GetType().GetFields( BindingFlags.Public | BindingFlags.Instance ) )
		{
			var value = field.GetValue( feature );

			switch ( value )
			{
				case null:
					geometry.Add( $"{field.Name}:null" );
					break;

				// The body lists are the other half of the snapshot and must not be counted twice.
				case BodySelectionParam:
					break;

				case FaceRef:
				case EdgeRef:
					geometry.Add( $"{field.Name}:set" );
					break;

				case string text:
					geometry.Add( $"{field.Name}:{text}" );
					break;

				case ICollection collection:
					geometry.Add( $"{field.Name}:{collection.Count}" );
					break;
			}
		}

		return new Snapshot( bodies, string.Join( "/", geometry ) );
	}

	// --- the fixture ------------------------------------------------------------------------------

	/// <summary>A box to pick faces and edges off, and a closed sketch to pick as a profile.</summary>
	static PartStudio Fixture( out Body body, out string sketchId )
	{
		var studio = new PartStudio();

		studio.Add( new PrimitiveFeature() );

		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0.25f, 0.25f ), new Vec2( 0.75f, 0.75f ) );
		sketchId = sketch.Id;

		studio.Rebuild();
		body = studio.Bodies[0];

		return studio;
	}

	static FaceRef FaceOf( Body body, Func<Vec3, bool> normal )
	{
		var mesh = body.Mesh;

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( normal( mesh.FaceNormal( mesh.Faces[i] ).Normal ) )
				return FacePlane.Capture( body, i, mesh.FaceCentroid( mesh.Faces[i] ) );
		}

		throw new InvalidOperationException( "the fixture box has no face pointing that way" );
	}

	static EdgeRef[] EdgesOf( Body body )
	{
		var face = FaceOf( body, v => v.z > 0.99f );

		if ( !FacePlane.TryResolveFace( new[] { body }, face, out var resolved, out var index ) )
			throw new InvalidOperationException( "the fixture's top face did not resolve" );

		return FacePlane.CaptureBoundary( resolved, index ).ToArray();
	}

	static List<Type> FeatureTypes() => typeof( Feature ).Assembly
		.GetTypes()
		.Where( t => t.IsSubclassOf( typeof( Feature ) ) && !t.IsAbstract )
		.OrderBy( t => t.Name, StringComparer.Ordinal )
		.ToList();

	static Feature Create( Type t ) => (Feature)Activator.CreateInstance( t );
}
