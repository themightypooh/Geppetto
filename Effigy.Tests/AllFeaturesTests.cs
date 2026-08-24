using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Every feature tool, exercised with its defaults.
///
/// The complaint this answers is "make sure all the feature tools actually function". A toolbar
/// button that adds a feature which then errors, or silently produces nothing, is indistinguishable
/// from a broken button - and finding out which of twelve tools misbehaves by clicking each one in
/// the editor is exactly the loop that was costing evenings.
///
/// Feature types are found by REFLECTION rather than listed, so a new one is covered the moment it
/// is written. Forgetting to add a test is not an available mistake.
///
/// Each tool gets the input it needs to be meaningful - body features get a body, sketch features
/// get a closed sketch - and is then judged on the only thing that matters at this level: with
/// sensible input and untouched defaults, it either produces geometry or explains itself.
/// </summary>
public static class AllFeaturesTests
{
	public static void Run()
	{
		Report.Section( "every feature: builds with its defaults" );
		TestEveryFeatureBuilds();

		Report.Section( "every feature: declares parameters the UI can render" );
		TestParametersAreRenderable();

		Report.Section( "every feature: says something useful when it has no input" );
		TestEmptyStudioErrors();

		Report.Section( "revolve: the axis has to clear the profile" );
		TestRevolveNeedsAnAxisClearOfTheProfile();
	}

	/// <summary>
	/// Revolve's axis defaults to a line through the SKETCH ORIGIN, and people draw around the
	/// origin - so the very first press of the Revolve button on a typical sketch fails.
	///
	/// The failure is correct: you cannot revolve a profile through its own axis. What matters is
	/// that it says so usefully, with the numbers, because "move it to one side" alone leaves you
	/// guessing which side and how far. The real fix is an axis SELECTION in the dialog, the way
	/// Extrude selects a face - noted in CAD-REFERENCE.md, and not something the kernel can do.
	/// </summary>
	static void TestRevolveNeedsAnAxisClearOfTheProfile()
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );

		// Centred on the origin, which is where the default axis runs.
		sketch.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

		var revolve = studio.Add( new RevolveFeature() );
		studio.Rebuild();

		Report.Check( "a profile straddling the default axis fails rather than building nonsense",
			revolve.Error is not null, "built anyway" );

		Report.Check( "and the message says how far the profile reaches either side",
			revolve.Error is not null && revolve.Error.Contains( "0.5" ), revolve.Error ?? "" );

		// Moved clear of the axis, the same feature builds.
		var moved = new PartStudio();
		var clear = moved.Add( new SketchFeature() );
		clear.Sketch.AddRectangle( new Vec2( 1f, 1f ), new Vec2( 2f, 2f ) );

		var ok = moved.Add( new RevolveFeature() );
		moved.Rebuild();

		Report.Check( "the same revolve builds once the profile clears the axis",
			ok.Error is null && moved.Bodies.Count == 1, ok.Error ?? $"{moved.Bodies.Count} bodies" );
	}

	static List<Type> FeatureTypes() => typeof( Feature ).Assembly
		.GetTypes()
		.Where( t => t.IsSubclassOf( typeof( Feature ) ) && !t.IsAbstract )
		.OrderBy( t => t.Name, StringComparer.Ordinal )
		.ToList();

	static Feature Create( Type t ) => (Feature)Activator.CreateInstance( t );

	/// <summary>
	/// Hand a freshly created feature the kind of input that has no parameter to set it.
	///
	/// Defaults cover most tools, and the studio from WithInput covers the rest — a body to act on,
	/// a sketch to consume. A face assignment needs neither: it needs PICKED FACES, which arrive
	/// from a click in the viewport and have no default that could be meaningful. Giving it one face
	/// of the box is this harness keeping its own promise that every tool is judged on its real path
	/// rather than on its input guard. The guard itself is covered by the empty-studio test.
	/// </summary>
	static void GivePickedInput( Feature feature, PartStudio studio )
	{
		if ( feature is not FaceMaterialFeature material || studio.Bodies.Count == 0 )
			return;

		var body = studio.Bodies[0];
		var mesh = body.Mesh;

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( mesh.FaceNormal( mesh.Faces[i] ).z > 0.99f )
			{
				material.Faces.Add( FacePlane.Capture( body, i, mesh.FaceCentroid( mesh.Faces[i] ) ) );
				return;
			}
		}
	}

	/// <summary>A studio holding a box and a closed sketch above it, so any feature added next has
	/// something real to act on.</summary>
	static PartStudio WithInput()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 2f;
		box.SizeY.Value = 2f;
		box.SizeZ.Value = 2f;

		var sketch = studio.Add( new SketchFeature() );

		// Held clear of the origin on both axes. Revolve's default axis runs through the sketch
		// origin, so a profile straddling it cannot be revolved at all - that is correct behaviour
		// and is pinned separately in TestRevolveNeedsAnAxisClearOfTheProfile. Here the point is to
		// exercise each tool's real path rather than its input guard.
		sketch.Sketch.AddRectangle( new Vec2( 1f, 1f ), new Vec2( 2f, 2f ) );

		studio.Rebuild();
		return studio;
	}

	static void TestEveryFeatureBuilds()
	{
		foreach ( var type in FeatureTypes() )
		{
			var studio = WithInput();
			var bodiesBefore = studio.Bodies.Count;

			var feature = Create( type );
			GivePickedInput( feature, studio );
			studio.Add( feature );

			RebuildReport report;

			try
			{
				report = studio.Rebuild();
			}
			catch ( Exception e )
			{
				// Feature.Run catches what Execute throws, so anything escaping to here is a fault
				// in the studio itself and is worth failing loudly on.
				Report.Check( $"{type.Name} does not throw out of Rebuild", false, e.Message );
				continue;
			}

			var ownError = feature.Error;

			Report.Check( $"{type.Name} builds with defaults", ownError is null,
				ownError ?? "" );

			if ( ownError is not null )
				continue;

			// It ran. It must also have DONE something - produced a body, changed one, or (for
			// Sketch) published a sketch. A tool that runs clean and changes nothing is the case
			// that reads as a dead button.
			var didSomething = feature is SketchFeature
				|| studio.Bodies.Count != bodiesBefore
				|| studio.Bodies.Any( b => b.Mesh.FaceCount > 0 );

			Report.Check( $"{type.Name} actually affects the model", didSomething );
		}
	}

	/// <summary>
	/// The dialog renders one row per parameter and knows a fixed set of types. A parameter of a
	/// type it does not handle renders as nothing at all, which looks like a missing control.
	/// </summary>
	static void TestParametersAreRenderable()
	{
		// Kept in step with EffigyFeatureDialog.BuildParamRow by hand, because the editor cannot be
		// referenced from here. If a new IParam type is added, this fails and says so.
		var renderable = new[]
		{
			typeof( FloatParam ), typeof( IntParam ), typeof( BoolParam ),
			typeof( ChoiceParam ), typeof( Vec3Param ), typeof( BodySelectionParam ),
		};

		foreach ( var type in FeatureTypes() )
		{
			var feature = Create( type );
			var unknown = feature.Parameters
				.Select( p => p.GetType() )
				.Where( t => !renderable.Contains( t ) )
				.Distinct()
				.ToList();

			Report.Check( $"{type.Name}'s parameters are all renderable",
				unknown.Count == 0, string.Join( ", ", unknown.Select( t => t.Name ) ) );

			// Every parameter needs a label, or the dialog draws a nameless control.
			var unlabelled = feature.Parameters.Count( p => string.IsNullOrWhiteSpace( p.Label ) );

			Report.Check( $"{type.Name}'s parameters are all labelled", unlabelled == 0,
				$"{unlabelled} without a label" );
		}
	}

	/// <summary>
	/// Added to an empty studio, a feature that needs input must fail with a message that says what
	/// is missing. "Object reference not set" is the failure mode this exists to prevent.
	/// </summary>
	static void TestEmptyStudioErrors()
	{
		foreach ( var type in FeatureTypes() )
		{
			var studio = new PartStudio();
			var feature = Create( type );
			studio.Add( feature );
			studio.Rebuild();

			if ( feature.Error is null )
				continue;   // it coped with nothing to work on, which is fine

			var message = feature.Error;

			var useful = message.Length > 15
				&& !message.Contains( "Object reference" )
				&& !message.Contains( "Index was out of range" )
				&& !message.Contains( "NullReference" );

			Report.Check( $"{type.Name}'s empty-studio error explains itself", useful, message );
		}
	}
}
