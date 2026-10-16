using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// Measure a finished solid. Click an edge for its length, or two faces, edges or vertices for
/// the distance or the angle between them.
///
/// THE NUMBER IS NOT SAVED. It stays on screen until the next measurement, Escape, or the tool
/// being switched off. A measurement that belongs to the document is an annotation, and this is
/// not one — see SolidMeasure.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>View → Measure. While this is on, a click measures instead of selecting.</summary>
	public bool Measuring { get; set; }

	/// <summary>The sentence for the prompt: the number, or what to click next.</summary>
	public Action<string> MeasureStatus { get; set; }

	readonly struct MeasureHeld
	{
		public readonly string BodyId;
		public readonly SolidMeasure.Target Target;

		public MeasureHeld( string bodyId, SolidMeasure.Target target )
		{
			BodyId = bodyId;
			Target = target;
		}
	}

	MeasureHeld? _measurePending;
	SolidMeasure.Reading? _measureReading;

	public bool HasMeasure => _measurePending is not null || _measureReading is not null;

	/// <summary>Drop the number and the first pick. The tool stays armed.</summary>
	public void ClearMeasure()
	{
		_measurePending = null;
		_measureReading = null;
		Update();
	}

	private void MeasureFrame()
	{
		if ( !Measuring )
			return;

		DrawMeasure();

		// Another tool owns the click. It still does that tool's thing, so this is not refused —
		// but the prompt says why Measure is not answering, and what to finish to get it back.
		if ( MeasureBlocker is { } blocker )
		{
			if ( _canvasHasCursor && Gizmo.WasLeftMousePressed )
				MeasureStatus?.Invoke( $"Measure is paused: {blocker}" );

			return;
		}

		if ( !_canvasHasCursor || !Gizmo.WasLeftMousePressed || Gizmo.HasHovered )
			return;

		if ( !TryPickFaceUnderCursor( out var hit ) )
		{
			ClearMeasure();
			MeasureStatus?.Invoke( "Measure: click a face, an edge or a vertex." );
			return;
		}

		var target = MeasureTarget( hit );
		var held = new MeasureHeld( hit.Body.Id, target );

		if ( _measurePending is { } pending && BodyById( pending.BodyId ) is { } first )
		{
			if ( SolidMeasure.Try( first.Mesh, pending.Target, hit.Body.Mesh, target, out var pair ) )
			{
				_measureReading = pair;
				_measurePending = held;
				MeasureStatus?.Invoke( MeasureSentence( pair ) );
				return;
			}

			// The pair has no answer. Keep the first pick — it was fine, it is the second that
			// was not — and say why, rather than quietly starting over from the new one.
			// An edge alone already read out its length; a second pick that cannot be measured
			// against it is still a fresh start, and that one is not a refusal.
			if ( _measureReading is null || pending.Target.What != SolidMeasure.Kind.Edge )
			{
				Refuse( pending.BodyId == hit.Body.Id
					&& pending.Target.What == SolidMeasure.Kind.Face && target.What == SolidMeasure.Kind.Face
					? "Can't measure that: it's the same flat surface as the first pick. Click a different face, an edge or a vertex."
					: "Can't measure between those two picks. Click a different face, edge or vertex — the first pick is kept." );
				return;
			}
		}

		_measurePending = held;

		if ( SolidMeasure.Try( hit.Body.Mesh, target, out var alone ) )
		{
			_measureReading = alone;
			MeasureStatus?.Invoke( MeasureSentence( alone ) + " Click something else to measure against it." );
		}
		else
		{
			MeasureStatus?.Invoke( target.What == SolidMeasure.Kind.Face
				? "Face. Click another face, an edge or a vertex."
				: "Vertex. Click a second point, edge or face." );
		}
	}

	/// <summary>The live tool that owns the click while Measure is on, as a sentence saying what to
	/// finish; null when Measure has the click.</summary>
	private string MeasureBlocker
	{
		get
		{
			if ( IsSketching ) return "a sketch is open. Finish it to measure.";
			if ( IsSplineEditing ) return "a spline is being edited. Finish it to measure.";
			if ( IsSculpting ) return "sculpting. Leave sculpt mode to measure.";
			if ( IsMeshEditing ) return "editing the mesh. Leave mesh edit to measure.";
			if ( IsPainting ) return "painting. Leave paint mode to measure.";
			if ( IsMaterialBrushing ) return "the material brush is on. Turn it off to measure.";
			if ( IsNoting ) return "the notes pen is on. Put it down (Escape) to measure.";
			if ( IsWeightPainting ) return "weight painting. Leave weight paint to measure.";
			if ( BoneToolActive ) return "Add Bone is placing. Press Escape twice to stop placing, then measure.";
			if ( PlanePickMode || SketchPickMode || FacePickMode || EdgePickMode || BodyPickMode )
				return "a dialog is picking. Finish or cancel it (Escape) to measure.";
			return null;
		}
	}

	private SolidMeasure.Target MeasureTarget( EffigyFaceHit hit )
	{
		if ( TryMeasureVertex( hit, out var vertex ) )
			return SolidMeasure.Target.Vertex( vertex );

		if ( TryIdleEdge( hit, out _, out var key ) )
			return SolidMeasure.Target.Edge( key.A, key.B );

		return SolidMeasure.Target.Face( hit.FaceIndex );
	}

	/// <summary>A click on a corner, rather than on the edge or the face it belongs to. Same pixel
	/// test the edge pick uses, a little tighter, so a corner wins when you are actually on it.</summary>
	private bool TryMeasureVertex( EffigyFaceHit hit, out int vertex )
	{
		vertex = -1;

		var mesh = hit.Body?.Mesh;
		var face = hit.FaceIndex;

		if ( mesh is null || face < 0 || face >= mesh.Faces.Count )
			return false;

		var point = hit.Reference.Point;
		var best = float.MaxValue;

		foreach ( var index in mesh.Faces[face].Indices )
		{
			if ( index < 0 || index >= mesh.Positions.Count )
				continue;

			var d = (mesh.Positions[index] - point).Length;

			if ( d < best )
			{
				best = d;
				vertex = index;
			}
		}

		if ( vertex < 0 )
			return false;

		var at = mesh.Positions[vertex];
		return best <= WorldRadiusAt( new Vector3( at.x, at.y, at.z ), 8f );
	}

	private void DrawMeasure()
	{
		if ( _measurePending is { } pending && BodyById( pending.BodyId ) is { } body )
			DrawMeasureHeld( body, pending.Target );

		if ( _measureReading is not { } reading )
			return;

		var eye = _camera.WorldPosition;
		var from = Lift( reading.From, eye );
		var to = Lift( reading.To, eye );

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = DimensionEditColor;
		Gizmo.Draw.LineThickness = 1.5f;

		if ( (reading.To - reading.From).LengthSquared > 1e-8f )
		{
			Gizmo.Draw.Line( from, to );
			DrawDimensionText( (from + to) * 0.5f, MeasureLabel( reading ), DimensionEditColor );
		}
		else
		{
			DrawDimensionText( from, MeasureLabel( reading ), DimensionEditColor );
		}

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;
	}

	private void DrawMeasureHeld( Body body, SolidMeasure.Target target )
	{
		switch ( target.What )
		{
			case SolidMeasure.Kind.Face:
				DrawFace( body, target.Index, FaceHighlightColor );
				break;

			case SolidMeasure.Kind.Edge:
				DrawEdge( body, new EdgeKey( target.A, target.B ), FaceHighlightColor );
				break;

			case SolidMeasure.Kind.Vertex:
				DrawMeasureVertex( body, target.Index );
				break;
		}
	}

	private void DrawMeasureVertex( Body body, int index )
	{
		var mesh = body?.Mesh;

		if ( mesh is null || index < 0 || index >= mesh.Positions.Count )
			return;

		var p = mesh.Positions[index];
		var at = Lift( p, _camera.WorldPosition );
		var r = WorldRadiusAt( at, 6f );

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = FaceHighlightColor;
		Gizmo.Draw.LineThickness = 2f;
		Gizmo.Draw.Line( at - new Vector3( r, 0, 0 ), at + new Vector3( r, 0, 0 ) );
		Gizmo.Draw.Line( at - new Vector3( 0, r, 0 ), at + new Vector3( 0, r, 0 ) );
		Gizmo.Draw.Line( at - new Vector3( 0, 0, r ), at + new Vector3( 0, 0, r ) );
		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;
	}

	private Body BodyById( string id )
	{
		if ( string.IsNullOrEmpty( id ) )
			return null;

		foreach ( var body in _displayBodies )
		{
			if ( body?.Id == id )
				return body;
		}

		return null;
	}

	private static string MeasureLabel( SolidMeasure.Reading reading ) =>
		reading.Degrees ? $"{reading.Value:0.#}°" : $"{FormatLength( reading.Value )} in";

	private static string MeasureSentence( SolidMeasure.Reading reading )
	{
		var label = MeasureLabel( reading );

		return reading.What switch
		{
			"length" => $"Length {label}.",
			"angle" => $"Angle {label}.",
			_ => $"Distance {label}.",
		};
	}
}
