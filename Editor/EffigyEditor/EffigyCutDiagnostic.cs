using Editor;
using Effigy;
using Sandbox;
using System.Linq;
using System.Text;

namespace Marionette.EditorTools;

/// <summary>
/// What the open document actually asked for, printed to the console.
///
/// WHY THIS EXISTS. "The cut is not cutting" has at least five causes that look identical in the
/// viewport - Result never set to Remove, the sketch not attached to anything, the profile not
/// overlapping the part, the boolean refusing, or the boolean succeeding and the result not being
/// drawn - and telling them apart by describing symptoms across a chat window costs a round trip
/// each time. Every one of them is a fact the document already knows.
///
/// It reads and prints. It changes nothing, so it is safe to run at any point, including on a
/// document that is mid-error.
/// </summary>
public static class EffigyCutDiagnostic
{
	[ConCmd( "effigy_dump_tree" )]
	public static void DumpTree()
	{
		if ( EffigyWindow.Current is not { } window )
		{
			Log.Warning( "[effigy] no Effigy window is open" );
			return;
		}

		var studio = window.DiagnosticStudio;

		if ( studio is null )
		{
			Log.Warning( "[effigy] the window has no part studio" );
			return;
		}

		Log.Info( $"[effigy] boolean provider : {MeshBoolean.Provider?.GetType().Name ?? "NONE"}" );
		Log.Info( $"[effigy] boolean calls    : {EffigyMeshBoolean.CallCount} "
			+ $"(last: {EffigyMeshBoolean.LastOutcome ?? "never called"})" );
		Log.Info( $"[effigy] tolerance welds  : {EffigyMeshBoolean.WeldCount} "
			+ "(vertices exact equality would have left apart, each one a seam that reads as open)" );
		Log.Info( $"[effigy] bodies           : {studio.Bodies.Count}" );

		foreach ( var body in studio.Bodies )
		{
			// Validation, not just a count. A cut that produced a closed solid and a cut whose
			// opening was left unfaced have very different face counts and look identical from
			// outside, and IsClosed is what separates them.
			var validation = body.Mesh is null ? "no mesh" : MeshValidator.Validate( body.Mesh ).ToString();

			Log.Info( $"[effigy]   body {body.Id} '{body.Name}' "
				+ $"{body.Mesh?.FaceCount ?? 0} faces, {body.Mesh?.VertexCount ?? 0} verts, "
				+ $"visible={body.Visible}, {validation}" );

			// The size of the biggest face, which is how a hole that was FILLED IN shows up: a top
			// face that still has its full corner count means nothing was cut out of it.
			if ( body.Mesh is { FaceCount: > 0 } mesh )
			{
				var largest = 0;
				var bridged = 0;

				foreach ( var face in mesh.Faces )
				{
					largest = System.Math.Max( largest, face.Count );

					// A face that visits the same vertex twice is a BRIDGED face: the loop runs out
					// to an inner boundary and back along the same seam, which is the only way a
					// half-edge mesh can express a hole. Counting them is what separates "the hole
					// arrived and something later filled it in" from "the hole never arrived".
					if ( face.Indices.Distinct().Count() != face.Count )
						bridged++;
				}

				// The number that would have caught the fragmented-wall defect on its first day.
				// Everything else on this line can look perfect while one flat face is 88 pieces;
				// see CoplanarMerge.LargestFragmentedSurface.
				var fragmented = CoplanarMerge.LargestFragmentedSurface( mesh );

				Log.Info( $"[effigy]     largest face: {largest} corners, bridged faces: {bridged}, "
					+ $"worst fragmented surface: {fragmented} face(s)" );
			}
		}

		Log.Info( "[effigy] features:" );

		for ( var i = 0; i < studio.Features.Count; i++ )
		{
			var feature = studio.Features[i];
			var line = new StringBuilder();

			line.Append( $"[effigy]   {i,2}. {feature.GetType().Name,-24} '{feature.Name ?? "-"}'" );

			if ( feature.Suppressed )
				line.Append( " SUPPRESSED" );

			// The whole point of the dump. Result reads "Auto" until something sets it, and an
			// extrude on Auto adds - which is indistinguishable from a cut that failed.
			if ( feature is SketchConsumingFeature consumer )
			{
				line.Append( $" | Result={consumer.Result.Index}:{ResultName( consumer.Result.Index )}" );
				line.Append( $" | Sketch='{consumer.Sketch.Value}'" );
			}

			if ( feature is SketchFeature sketch )
			{
				line.Append( sketch.Face is { } face
					? $" | on face of body {face.BodyId}"
					: " | on a global plane (NOT attached to a body)" );

				line.Append( $" | {sketch.Sketch?.Curves.Count ?? 0} curves" );
			}

			if ( feature.Error is { Length: > 0 } error )
				line.Append( $"\n[effigy]       ERROR: {error}" );

			Log.Info( line.ToString() );
		}

		Log.Info( "[effigy] --- what to look for ---" );
		Log.Info( "[effigy] An Extrude that should cut must read Result=3:Remove. Auto never removes." );
		Log.Info( "[effigy] If Result=3 and boolean calls is 0, the cut errored before reaching the engine "
			+ "- the feature's ERROR line says why." );
	}

	/// <summary>
	/// Re-run the feature tree from the top, and say what the bodies became.
	///
	/// THE ONE THING IN THIS FILE THAT WRITES. Everything else here reads, and that is the right
	/// default - but a body holds the mesh its last rebuild produced, so after a change to the
	/// boolean adapter the document on screen is still showing the OLD answer and every dump of it
	/// agrees. Reopening the document or nudging a parameter also works; this is the version that
	/// does not need a hand on the mouse, which is what makes a fix verifiable from a console.
	///
	/// It goes through the same PartStudio.Rebuild the editor uses, so there is no second path that
	/// could succeed where the real one fails.
	/// </summary>
	[ConCmd( "effigy_rebuild" )]
	public static void Rebuild()
	{
		if ( EffigyWindow.Current?.DiagnosticStudio is not { } studio )
		{
			Log.Warning( "[effigy] no Effigy window is open" );
			return;
		}

		var before = EffigyMeshBoolean.CallCount;

		// MarkAllDirty first, or this does nothing at all. Rebuild is incremental - it restores the
		// snapshot taken after the last clean feature and carries on from there - so with nothing
		// dirty it reuses every cached body and reports success without running one feature. That
		// is right for editing and exactly wrong here, where the whole point is to re-run the
		// geometry against code that has since changed underneath it.
		studio.MarkAllDirty();

		var report = studio.Rebuild();

		Log.Info( $"[effigy] rebuilt - {EffigyMeshBoolean.CallCount - before} boolean call(s), "
			+ $"{(report.HasErrors ? "WITH ERRORS" : "no errors")}" );

		foreach ( var body in studio.Bodies )
		{
			Log.Info( $"[effigy]   body {body.Id} '{body.Name}' {body.Mesh?.FaceCount ?? 0} faces, "
				+ $"{body.Mesh?.VertexCount ?? 0} verts, "
				+ $"{(body.Mesh is null ? "no mesh" : MeshValidator.Validate( body.Mesh ).ToString())}" );
		}
	}

	/// <summary>
	/// Write what the studio currently holds to an OBJ, so it can be opened somewhere that is not
	/// this viewport.
	///
	/// THE POINT IS TO REMOVE THE RENDERER FROM THE QUESTION. "The mesh has a hole in it and the
	/// screen does not show one" has two halves and they need different fixes; an OBJ in Blender
	/// answers which half is wrong in about ten seconds, where staring at the viewport answers
	/// nothing. Same writer the real export uses, so this is not a third path that could differ.
	/// </summary>
	[ConCmd( "effigy_dump_obj" )]
	public static void DumpObj()
	{
		if ( EffigyWindow.Current?.DiagnosticStudio is not { } studio )
		{
			Log.Warning( "[effigy] no Effigy window is open" );
			return;
		}

		var mesh = studio.ToVisibleMesh();

		if ( mesh is null || mesh.FaceCount == 0 )
		{
			Log.Warning( "[effigy] nothing visible to write" );
			return;
		}

		var path = System.IO.Path.Combine( System.IO.Path.GetTempPath(), "effigy_dump.obj" );

		ObjWriter.WriteFile( mesh, path, "effigy_dump" );

		Log.Info( $"[effigy] wrote {mesh.FaceCount} faces / {mesh.VertexCount} verts to {path}" );
		Log.Info( "[effigy] open that in Blender. A hole there means the cut is real and the "
			+ "VIEWPORT is what is wrong; no hole means the cut never made it into the geometry." );
	}

	static string ResultName( int index ) => index switch
	{
		0 => "Auto",
		1 => "New body",
		2 => "Add",
		3 => "Remove",
		_ => "?"
	};
}
