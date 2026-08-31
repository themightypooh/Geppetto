using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The six sketch tools whose kernel half was finished long before their UI: ellipse, spline, trim,
/// extend, fillet and offset.
///
/// THEY LIVE IN THEIR OWN FILE AND INTERCEPT BEFORE THE EXISTING SWITCHES, deliberately. Twelve
/// drawing tools already share the state machine in EffigyViewport.Sketching.cs and all twelve work.
/// Threading six more cases through its click, preview and prompt switches means editing three
/// places in code that is known-good, blind, to add tools that are not. So instead there are three
/// one-line hooks at the top of those three methods, and everything else is here: if a hook returns
/// true the new tool owned the interaction and the old machine never sees it.
///
/// TWO KINDS OF TOOL, and the split is what makes them simple:
///
/// - **Ellipse and spline DRAW.** They collect clicks in _pending exactly like line and arc do.
/// - **Trim, extend, fillet and offset EDIT.** They act on geometry that is already there, so they
///   pick a curve or a point under the cursor rather than placing anything. The kernel does the
///   work and hands back a reason when it will not — SketchEdit was written that way, and every
///   refusal here is its words rather than ours.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>Points placed so far by the spline tool, which is the one tool with no fixed count.</summary>
	private readonly List<Vec2> _splinePoints = new();

	/// <summary>How close, in SCREEN PIXELS, a click has to be to a point for the fillet tool to
	/// take it as the corner. Same treatment as every other pick in this viewport, and for the same
	/// reason: a sketch can be one unit across or a thousand.</summary>
	private const float CornerPickPixels = 10f;

	/// <summary>True when this tool is one of the six here rather than one of the original twelve.</summary>
	private static bool IsNewSketchTool( SketchToolKind tool ) => tool
		is SketchToolKind.Ellipse or SketchToolKind.Spline or SketchToolKind.Trim
		or SketchToolKind.Extend or SketchToolKind.Fillet or SketchToolKind.Offset;

	// --- clicks ---------------------------------------------------------------------------------

	/// <summary>
	/// Handle a click for one of the six. Returns true when it owned the click, which is what keeps
	/// the original switch from also seeing it.
	///
	/// The caller has already pushed the point onto _pending, so a tool that does not want it there
	/// clears it.
	/// </summary>
	private bool HandleNewSketchToolClick( Vec2 p )
	{
		if ( ActiveSketch is null || !IsNewSketchTool( SketchTool ) )
			return false;

		switch ( SketchTool )
		{
			case SketchToolKind.Ellipse when _pending.Count == 3:
			{
				var centre = _pending[0];
				var majorEnd = _pending[1];

				// The third click sets the MINOR radius by how far it sits off the major axis, so the
				// ellipse follows the cursor instead of needing a fourth number typed at it.
				var minor = PerpendicularDistance( centre, majorEnd, _pending[2] );

				if ( (majorEnd - centre).Length > 1e-4f && minor > 1e-4f )
					Track( new SketchEllipse( PointIndex( centre ), PointIndex( majorEnd ), minor ) );

				_pending.Clear();
				Edited();
				return true;
			}

			case SketchToolKind.Ellipse:
				return true;

			case SketchToolKind.Spline:
			{
				// A spline has no fixed number of points, so the click never commits - Enter does.
				// See HandleSketchToolKey.
				_splinePoints.Add( p );
				_pending.Clear();
				PushPrompt();
				return true;
			}

			case SketchToolKind.Trim:
				_pending.Clear();
				ApplyTrim( p );
				return true;

			case SketchToolKind.Extend:
				_pending.Clear();
				ApplyExtend( p );
				return true;

			case SketchToolKind.Fillet when _pending.Count == 1:
			{
				// The first click names the corner. It has to BE a point - a fillet is defined on one,
				// and guessing the nearest would round a corner nobody pointed at.
				if ( NearestPoint( p ) is not { } corner )
				{
					_pending.Clear();
					SetSketchPrompt( "Fillet - click nearer a corner point of the sketch." );
					return true;
				}

				_filletCorner = corner;
				PushPrompt();
				return true;
			}

			case SketchToolKind.Fillet:
			{
				var radius = (p - ActiveSketch.Points[_filletCorner]).Length;

				if ( !SketchEdit.Fillet( ActiveSketch, _filletCorner, radius, out var error ) )
					SetSketchPrompt( $"Fillet - {error}" );
				else
					Edited();

				_filletCorner = -1;
				_pending.Clear();
				return true;
			}

			case SketchToolKind.Offset when _pending.Count == 1:
			{
				var id = CurveUnderCursor();

				if ( id is null )
				{
					_pending.Clear();
					SetSketchPrompt( "Offset - click on a curve to offset." );
					return true;
				}

				_offsetCurveId = id;
				PushPrompt();
				return true;
			}

			case SketchToolKind.Offset:
			{
				ApplyOffset( p );
				_offsetCurveId = null;
				_pending.Clear();
				return true;
			}
		}

		return false;
	}

	private int _filletCorner = -1;
	private string _offsetCurveId;

	// --- the four edits -------------------------------------------------------------------------

	private void ApplyTrim( Vec2 p )
	{
		if ( CurveUnderCursor() is not { } id || FindCurve( id ) is not { } curve )
		{
			SetSketchPrompt( "Trim - click on the piece of a curve you want gone." );
			return;
		}

		if ( !SketchEdit.Trim( ActiveSketch, curve, p, out var error ) )
		{
			SetSketchPrompt( $"Trim - {error}" );
			return;
		}

		Edited();
	}

	private void ApplyExtend( Vec2 p )
	{
		if ( CurveUnderCursor() is not { } id || FindCurve( id ) is not { } curve )
		{
			SetSketchPrompt( "Extend - click on the end of a curve you want stretched." );
			return;
		}

		// WHICH END was clicked, by tessellating and comparing against both ends. A tool that always
		// extended the start would be right half the time, which is worse than being wrong: it would
		// look like the tool sometimes not working.
		var points = curve.Tessellate( ActiveSketch, ActiveSketch.Tolerance );

		if ( points.Count < 2 )
		{
			SetSketchPrompt( "Extend - that curve has no ends to stretch." );
			return;
		}

		var atStart = (p - points[0]).Length < (p - points[^1]).Length;

		if ( !SketchEdit.Extend( ActiveSketch, curve, atStart, out var error ) )
		{
			SetSketchPrompt( $"Extend - {error}" );
			return;
		}

		Edited();
	}

	private void ApplyOffset( Vec2 p )
	{
		if ( FindCurve( _offsetCurveId ) is not { } curve )
			return;

		// The whole selection when there is one, so offsetting a closed profile is one gesture rather
		// than one per side. The clicked curve alone otherwise.
		var chain = HasSketchSelection && SketchSelection.Curves.Count > 0
			? SketchSelection.Curves.Select( FindCurve ).Where( c => c is not null ).ToList()
			: new List<SketchCurve> { curve };

		var distance = SignedOffset( curve, p );

		if ( !SketchEdit.Offset( ActiveSketch, chain, distance, out var created, out var error ) )
		{
			SetSketchPrompt( $"Offset - {error}" );
			return;
		}

		// The kernel returns what it made rather than adding it, so construction mode is applied here
		// the way Track applies it to everything else drawn.
		foreach ( var made in created )
			made.Construction = ConstructionMode;

		Edited();
	}

	/// <summary>
	/// How far the click sits from the curve, and WHICH SIDE it is on.
	///
	/// The sign is the whole of "which way does it go", and taking a magnitude would make offset a
	/// coin flip that lands on the wrong side half the time.
	/// </summary>
	private float SignedOffset( SketchCurve curve, Vec2 p )
	{
		var points = curve.Tessellate( ActiveSketch, ActiveSketch.Tolerance );

		if ( points.Count < 2 )
			return 0f;

		var best = float.MaxValue;
		var sign = 1f;

		for ( var i = 0; i < points.Count - 1; i++ )
		{
			var a = points[i];
			var b = points[i + 1];
			var along = b - a;
			var lengthSquared = along.LengthSquared;

			if ( lengthSquared < 1e-12f )
				continue;

			var t = Math.Clamp( ((p - a).x * along.x + (p - a).y * along.y) / lengthSquared, 0f, 1f );
			var closest = new Vec2( a.x + along.x * t, a.y + along.y * t );
			var distance = (p - closest).Length;

			if ( distance >= best )
				continue;

			best = distance;
			sign = along.x * (p.y - a.y) - along.y * (p.x - a.x) < 0f ? -1f : 1f;
		}

		return best == float.MaxValue ? 0f : best * sign;
	}

	private SketchCurve FindCurve( string id ) =>
		id is null ? null : ActiveSketch.Curves.FirstOrDefault( c => c.Id == id );

	/// <summary>The sketch point nearest the click, within reach, or null.</summary>
	private int? NearestPoint( Vec2 p )
	{
		var reach = UnitsPerPixel() * CornerPickPixels;
		var best = reach;
		int? found = null;

		for ( var i = 0; i < ActiveSketch.Points.Count; i++ )
		{
			var d = (ActiveSketch.Points[i] - p).Length;

			if ( d >= best )
				continue;

			best = d;
			found = i;
		}

		return found;
	}

	private static float PerpendicularDistance( Vec2 a, Vec2 b, Vec2 p )
	{
		var along = b - a;
		var length = along.Length;

		if ( length < 1e-9f )
			return (p - a).Length;

		return MathF.Abs( along.x * (p.y - a.y) - along.y * (p.x - a.x) ) / length;
	}

	// --- the spline's own ending ------------------------------------------------------------------

	/// <summary>
	/// Enter finishes a spline, and Escape abandons it.
	///
	/// It is the one tool here with no fixed number of clicks, so it needs a way to say "that is all
	/// of them". Enter is what every other tool in this editor uses to commit a typed number, which
	/// makes it the least surprising key for the job.
	/// </summary>
	public bool HandleSketchToolKey( KeyEvent e )
	{
		if ( ActiveSketch is null || SketchTool != SketchToolKind.Spline || _splinePoints.Count == 0 )
			return false;

		switch ( e.Key )
		{
			case KeyCode.Return:
			case KeyCode.Enter:
				CommitSpline();
				e.Accepted = true;
				return true;

			case KeyCode.Escape:
				_splinePoints.Clear();
				PushPrompt();
				e.Accepted = true;
				return true;
		}

		return false;
	}

	private void CommitSpline()
	{
		if ( _splinePoints.Count >= 2 )
		{
			SketchEditing?.Invoke();

			var indices = new List<int>( _splinePoints.Count );

			foreach ( var p in _splinePoints )
				indices.Add( PointIndex( p ) );

			Track( new SketchSpline( indices ) );
			Edited();
		}

		_splinePoints.Clear();
		PushPrompt();
	}

	// --- preview ----------------------------------------------------------------------------------

	/// <summary>Draw the six tools' own previews. True means the original preview switch should not
	/// also run.</summary>
	private bool DrawNewSketchToolPreview()
	{
		if ( ActiveSketch is null || !IsNewSketchTool( SketchTool ) )
			return false;

		switch ( SketchTool )
		{
			case SketchToolKind.Ellipse when _pending.Count == 1:
				Gizmo.Draw.Line( PlaneToWorld( _pending[0] ), PlaneToWorld( _cursorOnPlane ) );
				LiveLength( _pending[0], _cursorOnPlane );
				break;

			case SketchToolKind.Ellipse when _pending.Count == 2:
				DrawEllipsePreview( _pending[0], _pending[1],
					PerpendicularDistance( _pending[0], _pending[1], _cursorOnPlane ) );
				break;

			case SketchToolKind.Spline:
			{
				// The placed points and the leg to the cursor, so the shape is visible before Enter.
				for ( var i = 0; i < _splinePoints.Count - 1; i++ )
					Gizmo.Draw.Line( PlaneToWorld( _splinePoints[i] ), PlaneToWorld( _splinePoints[i + 1] ) );

				if ( _splinePoints.Count > 0 )
					Gizmo.Draw.Line( PlaneToWorld( _splinePoints[^1] ), PlaneToWorld( _cursorOnPlane ) );

				break;
			}

			case SketchToolKind.Fillet when _filletCorner >= 0 && _filletCorner < ActiveSketch.Points.Count:
			{
				var corner = ActiveSketch.Points[_filletCorner];

				DrawCirclePreview( corner, (corner - _cursorOnPlane).Length );
				LiveRadius( corner, _cursorOnPlane );
				break;
			}

			case SketchToolKind.Offset when _offsetCurveId is not null:
			{
				if ( FindCurve( _offsetCurveId ) is { } curve )
					LiveLength( ClosestOn( curve, _cursorOnPlane ), _cursorOnPlane );

				break;
			}
		}

		return true;
	}

	private Vec2 ClosestOn( SketchCurve curve, Vec2 p )
	{
		var points = curve.Tessellate( ActiveSketch, ActiveSketch.Tolerance );
		var best = float.MaxValue;
		var found = p;

		foreach ( var q in points )
		{
			var d = (q - p).Length;

			if ( d >= best )
				continue;

			best = d;
			found = q;
		}

		return found;
	}

	/// <summary>An ellipse as a polyline. There is no ellipse primitive to draw with, and at preview
	/// resolution the segments are invisible.</summary>
	private void DrawEllipsePreview( Vec2 centre, Vec2 majorEnd, float minorRadius )
	{
		var major = majorEnd - centre;
		var majorLength = major.Length;

		if ( majorLength < 1e-6f )
			return;

		var axis = new Vec2( major.x / majorLength, major.y / majorLength );
		var perpendicular = new Vec2( -axis.y, axis.x );

		const int Segments = 48;
		var previous = Vector3.Zero;

		for ( var i = 0; i <= Segments; i++ )
		{
			var angle = i / (float)Segments * MathF.PI * 2f;
			var offset = new Vec2(
				axis.x * MathF.Cos( angle ) * majorLength + perpendicular.x * MathF.Sin( angle ) * minorRadius,
				axis.y * MathF.Cos( angle ) * majorLength + perpendicular.y * MathF.Sin( angle ) * minorRadius );

			var point = PlaneToWorld( new Vec2( centre.x + offset.x, centre.y + offset.y ) );

			if ( i > 0 )
				Gizmo.Draw.Line( previous, point );

			previous = point;
		}
	}

	// --- prompts ------------------------------------------------------------------------------------

	/// <summary>What the six tools say they want next, or null when the active tool is not one of
	/// them and the original prompt switch should answer.</summary>
	private string NewSketchToolPrompt()
	{
		if ( !IsNewSketchTool( SketchTool ) )
			return null;

		return SketchTool switch
		{
			SketchToolKind.Ellipse when _pending.Count == 0 => "Ellipse - click the centre",
			SketchToolKind.Ellipse when _pending.Count == 1 => "Ellipse - click the end of the long axis",
			SketchToolKind.Ellipse => "Ellipse - click to set how far it bulges the other way",

			SketchToolKind.Spline when _splinePoints.Count == 0 => "Spline - click to place points",
			SketchToolKind.Spline when _splinePoints.Count == 1 => "Spline - click another point (Enter finishes)",
			SketchToolKind.Spline => $"Spline - {_splinePoints.Count} points, Enter finishes, Escape abandons",

			SketchToolKind.Trim => "Trim - click the piece of a curve you want gone",
			SketchToolKind.Extend => "Extend - click the end of a curve you want stretched",

			SketchToolKind.Fillet when _filletCorner < 0 => "Fillet - click a corner point",
			SketchToolKind.Fillet => "Fillet - click to set the radius",

			SketchToolKind.Offset when _offsetCurveId is null => "Offset - click the curve to offset",
			SketchToolKind.Offset => "Offset - click which side, and how far",

			_ => null,
		};
	}

	/// <summary>Say something immediately, without waiting for the next PushPrompt. Used to carry a
	/// refusal from the kernel straight to the status line.</summary>
	private void SetSketchPrompt( string text ) => SketchPromptChanged?.Invoke( text );

	/// <summary>Drop whatever the six tools were part-way through. Called when the tool changes, so
	/// a half-placed spline does not reappear on a tool that has nothing to do with it.</summary>
	private void ResetNewSketchTools()
	{
		_splinePoints.Clear();
		_filletCorner = -1;
		_offsetCurveId = null;
	}
}
