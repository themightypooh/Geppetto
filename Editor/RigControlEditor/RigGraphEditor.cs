using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// The Graph tab: the selected track's channels as curves over time, with its keys on them and
/// tangent handles on the keys that curve.
///
/// WHY A TIMING TOOL NEEDS ONE. The timeline says WHEN there is a key and how the segment leaving
/// it eases; it cannot say what the value does in between, and that in-between is where motion
/// reads as weighty or floaty. A bump in a curve is an overshoot you can see before you play it; a
/// flat stretch is a hold; the slope is the speed. Cubic and Monotone keys (see RigCurve) also have
/// tangents, and a tangent is a slope, so it can only be set somewhere slopes are drawn.
///
/// THE CURVES ARE SAMPLED FROM BoneTrack.Evaluate - the function playback runs - not redrawn from
/// the keys, so what is drawn is what plays, whatever the mode.
///
/// ROTATION IS SHOWN AS PITCH / YAW / ROLL, unwrapped along the curve so it does not jump at 180.
/// It is stored as a quaternion and curved as one; a rotation handle is converted both ways at the
/// key it belongs to, so dragging the yaw handle changes the yaw's slope and leaves the others.
///
/// Editing here is direct: drag a key up or down to change that channel at that key, drag a handle
/// to bend its tangent (Shift breaks In from Out), right-click a key for auto or flat tangents and
/// its mode. Click anywhere else to move the playhead.
/// </summary>
internal sealed class RigGraphEditor : Widget
{
	/// <summary>The track to show - the timeline's selection. Pulled on every paint, so the graph
	/// follows the selection without the timeline having to announce it.</summary>
	public Func<BoneTrack> TrackSource { get; set; }

	public Func<float> Playhead { get; set; }
	public Action<float> Scrubbed { get; set; }

	/// <summary>Raised on every step of a drag: the document has changed, nothing is recorded yet.</summary>
	public Action Changing { get; set; }

	/// <summary>Raised once an edit is finished, with its undo label.</summary>
	public Action<string> Edited { get; set; }

	private readonly GraphCanvas _canvas;
	private readonly Checkbox _position;
	private readonly Checkbox _rotation;
	private readonly Checkbox _scale;

	public RigGraphEditor( Widget parent ) : base( parent )
	{
		Name = "Graph";
		WindowTitle = "Graph";
		SetWindowIcon( "show_chart" );

		Layout = Layout.Column();

		var bar = Layout.AddRow();
		bar.Margin = new Sandbox.UI.Margin( 6, 4 );
		bar.Spacing = 8;

		_position = new Checkbox( "Position" ) { Value = true, ToolTip = "X red, Y green, Z blue - the same colours as the gizmo" };
		_rotation = new Checkbox( "Rotation" ) { Value = true, ToolTip = "Pitch, yaw and roll in degrees - dashed, in the axis colours" };
		_scale = new Checkbox( "Scale / FOV" ) { Value = false, ToolTip = "Uniform scale - or, on a camera's row, its field of view" };

		foreach ( var box in new[] { _position, _rotation, _scale } )
		{
			box.Toggled = () => _canvas?.Update();
			bar.Add( box );
		}

		bar.AddStretchCell();

		bar.Add( new Button( "Frame", "fit_screen" ) { ToolTip = "Fit the view to the selected track", Clicked = () => _canvas.FitNext() } );

		_canvas = new GraphCanvas( this );
		Layout.Add( _canvas, 1 );
	}

	/// <summary>Repaint - the document or the playhead changed.</summary>
	public void Refresh() => _canvas?.Update();

	private BoneTrack _shown;

	/// <summary>
	/// Follow the timeline's selection. Clicking a key there raises nothing the window hears, so the
	/// graph looks once a frame and repaints only when the track changed. Guarded the way
	/// RigTimeline.Frame is, for the same reason: the event can reach a widget already closed.
	/// </summary>
	[EditorEvent.Frame]
	public void Frame()
	{
		if ( !IsValid || !_canvas.IsValid() )
			return;

		try
		{
			var track = TrackSource?.Invoke();

			if ( track == _shown )
				return;

			_shown = track;
			_canvas.Update();
		}
		catch ( Exception )
		{
			// A dead tick must not spam the console; the next live one repaints.
		}
	}

	internal bool ShowPosition => _position.Value;
	internal bool ShowRotation => _rotation.Value;
	internal bool ShowScale => _scale.Value;

	/// <summary>The value channels the graph draws.</summary>
	internal enum Channel { X, Y, Z, Pitch, Yaw, Roll, Scale }

	internal static bool IsRotation( Channel c ) => c is Channel.Pitch or Channel.Yaw or Channel.Roll;

	internal static Color ColorOf( Channel c ) => c switch
	{
		Channel.X or Channel.Pitch => Theme.Red,
		Channel.Y or Channel.Yaw => Theme.Green,
		Channel.Z or Channel.Roll => Theme.Blue,
		_ => Theme.Yellow
	};

	internal static float ValueOf( Transform t, Channel c ) => c switch
	{
		Channel.X => t.Position.x,
		Channel.Y => t.Position.y,
		Channel.Z => t.Position.z,
		Channel.Pitch => t.Rotation.Angles().pitch,
		Channel.Yaw => t.Rotation.Angles().yaw,
		Channel.Roll => t.Rotation.Angles().roll,
		_ => t.Scale.x
	};

	/// <summary>A transform with one channel set. Scale is uniform on a bone (Source 2 bones scale
	/// in one number) and the field of view on a camera, which lives in Scale.x.</summary>
	internal static Transform WithValue( Transform t, Channel c, float value, bool camera )
	{
		switch ( c )
		{
			case Channel.X: return new Transform( t.Position.WithX( value ), t.Rotation, t.Scale );
			case Channel.Y: return new Transform( t.Position.WithY( value ), t.Rotation, t.Scale );
			case Channel.Z: return new Transform( t.Position.WithZ( value ), t.Rotation, t.Scale );
			case Channel.Scale: return new Transform( t.Position, t.Rotation, new Vector3( value ) );
		}

		var angles = t.Rotation.Angles();

		angles = c switch
		{
			Channel.Pitch => angles.WithPitch( value ),
			Channel.Yaw => angles.WithYaw( value ),
			_ => angles.WithRoll( value )
		};

		// On the key's own quaternion sign - its tangents were drawn against it. See SetKeyframe.
		return new Transform( t.Position, BoneTrack.SameHemisphere( t.Rotation, angles.ToRotation() ), t.Scale );
	}

	/// <summary>Bring an angle within 180 of another, so a curve through 179 and -179 is a step of
	/// two degrees and not a jump of 358.</summary>
	internal static float Unwrap( float angle, float near )
	{
		while ( angle - near > 180f ) angle -= 360f;
		while ( angle - near < -180f ) angle += 360f;
		return angle;
	}

	// --- tangents in display units ---------------------------------------------------------------

	/// <summary>A key's tangent on one side, as RigCurve stores it: its own, or the automatic one.</summary>
	internal static float[] TangentOf( BoneTrack track, int index, bool outgoing )
	{
		var key = track.Keyframes[index];
		var own = outgoing ? key.TangentOut : key.TangentIn;

		if ( own is { Count: RigCurve.Channels } )
			return own.ToArray();

		// The automatic tangent of the segment it belongs to: leaving, the key's own mode; arriving,
		// the previous key's.
		var mode = outgoing ? key.Interpolation : index > 0 ? track.Keyframes[index - 1].Interpolation : key.Interpolation;
		return track.AutoTangent( index, mode == KeyInterpolation.Monotone );
	}

	/// <summary>One display channel's slope (value per frame) from a ten-channel tangent at a key.</summary>
	internal static float SlopeOf( Transform key, float[] tangent, Channel c )
	{
		switch ( c )
		{
			case Channel.X: return tangent[0];
			case Channel.Y: return tangent[1];
			case Channel.Z: return tangent[2];
			case Channel.Scale: return tangent[7];
		}

		// Euler rate by central difference along the quaternion tangent.
		const float h = 0.25f;
		var q = key.Rotation;
		var ahead = Nudge( q, tangent, h ).Angles();
		var behind = Nudge( q, tangent, -h ).Angles();

		var (a, b) = c switch
		{
			Channel.Pitch => (ahead.pitch, behind.pitch),
			Channel.Yaw => (ahead.yaw, behind.yaw),
			_ => (ahead.roll, behind.roll)
		};

		return (Unwrap( a, b ) - b) / (2f * h);
	}

	/// <summary>Set one display channel's slope in a ten-channel tangent, leaving the others as
	/// they read. A rotation channel is set by finding the quaternion rate whose Euler rates are the
	/// old ones with this one replaced.</summary>
	internal static void SetSlope( Transform key, float[] tangent, Channel c, float slope )
	{
		switch ( c )
		{
			case Channel.X: tangent[0] = slope; return;
			case Channel.Y: tangent[1] = slope; return;
			case Channel.Z: tangent[2] = slope; return;
			case Channel.Scale: tangent[7] = tangent[8] = tangent[9] = slope; return;
		}

		var rates = new Angles( SlopeOf( key, tangent, Channel.Pitch ), SlopeOf( key, tangent, Channel.Yaw ), SlopeOf( key, tangent, Channel.Roll ) );

		rates = c switch
		{
			Channel.Pitch => rates.WithPitch( slope ),
			Channel.Yaw => rates.WithYaw( slope ),
			_ => rates.WithRoll( slope )
		};

		const float h = 0.25f;
		var e = key.Rotation.Angles();
		var q = key.Rotation;
		var ahead = Aligned( q, (e + rates * h).ToRotation() );
		var behind = Aligned( q, (e - rates * h).ToRotation() );

		tangent[3] = (ahead.x - behind.x) / (2f * h);
		tangent[4] = (ahead.y - behind.y) / (2f * h);
		tangent[5] = (ahead.z - behind.z) / (2f * h);
		tangent[6] = (ahead.w - behind.w) / (2f * h);
	}

	private static Rotation Nudge( Rotation q, float[] tangent, float h )
	{
		var v = new Rotation( q.x + tangent[3] * h, q.y + tangent[4] * h, q.z + tangent[5] * h, q.w + tangent[6] * h );
		var length = MathF.Sqrt( v.x * v.x + v.y * v.y + v.z * v.z + v.w * v.w );
		return length < 1e-6f ? q : new Rotation( v.x / length, v.y / length, v.z / length, v.w / length );
	}

	private static Rotation Aligned( Rotation reference, Rotation q ) =>
		reference.x * q.x + reference.y * q.y + reference.z * q.z + reference.w * q.w >= 0f
			? q
			: new Rotation( -q.x, -q.y, -q.z, -q.w );

	// --- canvas ----------------------------------------------------------------------------------

	private sealed class GraphCanvas : Widget
	{
		private readonly RigGraphEditor _owner;

		private BoneTrack _track;
		private float _f0, _f1 = 30f, _v0 = -10f, _v1 = 10f;
		private bool _fit = true;

		private (int Key, Channel Channel)? _selected;
		private DragKind _drag;
		private bool _breakTangent;
		private Vector2 _panFrom;
		private (float F0, float F1, float V0, float V1) _panView;

		private enum DragKind { None, Key, HandleIn, HandleOut, Pan, Scrub }

		private const float HandlePixels = 40f;
		private const float Pad = 24f;

		public GraphCanvas( RigGraphEditor owner ) : base( owner )
		{
			_owner = owner;
			MouseTracking = true;
			FocusMode = FocusMode.Click;
			MinimumSize = new Vector2( 200, 120 );
		}

		public void FitNext()
		{
			_fit = true;
			Update();
		}

		private IEnumerable<Channel> Visible()
		{
			if ( _owner.ShowPosition ) { yield return Channel.X; yield return Channel.Y; yield return Channel.Z; }
			if ( _owner.ShowRotation ) { yield return Channel.Pitch; yield return Channel.Yaw; yield return Channel.Roll; }
			if ( _owner.ShowScale ) yield return Channel.Scale;
		}

		private float X( float frame ) => Pad + (frame - _f0) / MathF.Max( _f1 - _f0, 1e-3f ) * (Width - Pad * 2f);
		private float Y( float value ) => Height - Pad - (value - _v0) / MathF.Max( _v1 - _v0, 1e-3f ) * (Height - Pad * 2f);
		private float Frame( float x ) => _f0 + (x - Pad) / MathF.Max( Width - Pad * 2f, 1f ) * (_f1 - _f0);
		private float Value( float y ) => _v0 + (Height - Pad - y) / MathF.Max( Height - Pad * 2f, 1f ) * (_v1 - _v0);

		/// <summary>A channel's value at every key, rotation unwrapped key to key the same way the
		/// sampled curve is, so a key dot sits on its curve.</summary>
		private List<float> KeyValues( Channel c )
		{
			var values = new List<float>();

			for ( var i = 0; i < _track.Keyframes.Count; i++ )
			{
				var v = ValueOf( _track.Keyframes[i].Local, c );

				if ( IsRotation( c ) )
					v = Unwrap( v, i == 0 ? v : SampleAt( c, _track.Keyframes[i].Frame - 0.5f, values[^1], _track.Keyframes[i - 1].Frame ) );

				values.Add( v );
			}

			return values;
		}

		/// <summary>A rotation channel at a frame, unwrapped by walking from a known value at a
		/// nearby frame - one step per frame, so it never loses track of which turn it is on.</summary>
		private float SampleAt( Channel c, float frame, float knownValue, float knownFrame )
		{
			var value = knownValue;
			var steps = Math.Max( 1, (int)MathF.Ceiling( MathF.Abs( frame - knownFrame ) ) );

			for ( var s = 1; s <= steps; s++ )
			{
				var f = knownFrame + (frame - knownFrame) * s / steps;
				value = Unwrap( ValueOf( _track.Evaluate( f ), c ), value );
			}

			return value;
		}

		private void Fit()
		{
			_fit = false;

			if ( _track is null || _track.Keyframes.Count == 0 )
				return;

			_f0 = _track.Keyframes[0].Frame;
			_f1 = MathF.Max( _track.Keyframes[^1].Frame, _f0 + 10f );

			var min = float.MaxValue;
			var max = float.MinValue;

			foreach ( var c in Visible() )
			{
				foreach ( var (_, v) in Samples( c ) )
				{
					min = MathF.Min( min, v );
					max = MathF.Max( max, v );
				}
			}

			if ( min > max )
			{
				min = -1f;
				max = 1f;
			}

			var margin = MathF.Max( (max - min) * 0.1f, 0.5f );
			var span = _f1 - _f0;

			_v0 = min - margin;
			_v1 = max + margin;
			_f0 -= span * 0.05f;
			_f1 += span * 0.05f;
		}

		/// <summary>The curve, sampled about every three pixels across the view and unwrapped as it
		/// goes.</summary>
		private IEnumerable<(float Frame, float Value)> Samples( Channel c )
		{
			if ( _track is null || _track.Keyframes.Count == 0 )
				yield break;

			var count = Math.Clamp( (int)(Width / 3f), 16, 600 );

			// Rotation starts from the turn the first key is on, walked to the left edge, so the curve
			// and the key dots (KeyValues) agree on which 360 they are drawn in however it is panned.
			float? last = IsRotation( c )
				? SampleAt( c, _f0, ValueOf( _track.Keyframes[0].Local, c ), _track.Keyframes[0].Frame )
				: null;

			for ( var i = 0; i <= count; i++ )
			{
				var f = _f0 + (_f1 - _f0) * i / count;
				var v = ValueOf( _track.Evaluate( f ), c );

				if ( IsRotation( c ) && last is { } previous )
					v = Unwrap( v, previous );

				last = v;
				yield return (f, v);
			}
		}

		protected override void OnPaint()
		{
			var track = _owner.TrackSource?.Invoke();

			if ( track != _track )
			{
				_track = track;
				_selected = null;
				_fit = true;
			}

			Paint.Antialiasing = true;
			Paint.ClearPen();
			Paint.SetBrush( Theme.ControlBackground );
			Paint.DrawRect( LocalRect );

			if ( _track is null || _track.Keyframes.Count == 0 )
			{
				Paint.SetPen( Theme.TextControl.WithAlpha( 0.5f ) );
				Paint.DrawText( LocalRect, "Select a keyed track on the Timeline to see its curves.", TextFlag.Center );
				return;
			}

			if ( _fit )
				Fit();

			PaintGrid();

			var camera = _track.Target == TrackTarget.Camera;

			foreach ( var c in Visible() )
			{
				Paint.SetPen( ColorOf( c ).WithAlpha( IsRotation( c ) ? 0.6f : 0.85f ), IsRotation( c ) ? 1.25f : 1.75f );

				Vector2? previous = null;

				foreach ( var (f, v) in Samples( c ) )
				{
					var point = new Vector2( X( f ), Y( v ) );

					if ( previous is { } p )
						Paint.DrawLine( p, point );

					previous = point;
				}

				PaintKeys( c );
			}

			PaintHandles();

			// The playhead, the same yellow as the timeline's.
			var playhead = _owner.Playhead?.Invoke() ?? 0f;
			Paint.SetPen( Theme.Yellow.WithAlpha( 0.8f ), 1f );
			Paint.DrawLine( new Vector2( X( playhead ), 0f ), new Vector2( X( playhead ), Height ) );

			Paint.SetPen( Theme.TextControl.WithAlpha( 0.7f ) );
			Paint.SetDefaultFont( 8 );
			Paint.DrawText( new Rect( 8f, 4f, Width - 16f, 16f ),
				$"{_track.DisplayName}{(camera && _owner.ShowScale ? "   (scale is the field of view)" : "")}", TextFlag.LeftTop );
		}

		private void PaintGrid()
		{
			Paint.SetDefaultFont( 7 );

			var valueStep = NiceStep( (_v1 - _v0) / 6f );

			for ( var v = MathF.Ceiling( _v0 / valueStep ) * valueStep; v <= _v1; v += valueStep )
			{
				var y = Y( v );
				Paint.SetPen( Theme.TextControl.WithAlpha( MathF.Abs( v ) < valueStep * 0.01f ? 0.3f : 0.08f ) );
				Paint.DrawLine( new Vector2( 0f, y ), new Vector2( Width, y ) );
				Paint.SetPen( Theme.TextControl.WithAlpha( 0.4f ) );
				Paint.DrawText( new Rect( 2f, y - 12f, 60f, 12f ), $"{v:0.##}", TextFlag.LeftBottom );
			}

			var frameStep = MathF.Max( 1f, NiceStep( (_f1 - _f0) / 8f ) );

			for ( var f = MathF.Ceiling( _f0 / frameStep ) * frameStep; f <= _f1; f += frameStep )
			{
				var x = X( f );
				Paint.SetPen( Theme.TextControl.WithAlpha( 0.08f ) );
				Paint.DrawLine( new Vector2( x, 0f ), new Vector2( x, Height ) );
				Paint.SetPen( Theme.TextControl.WithAlpha( 0.4f ) );
				Paint.DrawText( new Rect( x + 2f, Height - 14f, 40f, 12f ), $"{(int)f}", TextFlag.LeftBottom );
			}
		}

		private static float NiceStep( float raw )
		{
			if ( raw <= 0f )
				return 1f;

			var magnitude = MathF.Pow( 10f, MathF.Floor( MathF.Log10( raw ) ) );
			var normal = raw / magnitude;
			return (normal < 1.5f ? 1f : normal < 3.5f ? 2f : normal < 7.5f ? 5f : 10f) * magnitude;
		}

		private void PaintKeys( Channel c )
		{
			var values = KeyValues( c );

			for ( var i = 0; i < values.Count; i++ )
			{
				var center = new Vector2( X( _track.Keyframes[i].Frame ), Y( values[i] ) );
				var selected = _selected is { } s && s.Key == i && s.Channel == c;
				var r = selected ? 4.5f : 3.5f;

				Paint.ClearPen();
				Paint.SetBrush( selected ? Color.White : ColorOf( c ) );
				Paint.DrawCircle( new Rect( center.x - r, center.y - r, r * 2f, r * 2f ) );
			}
		}

		/// <summary>Whether the side of a key has a tangent that does anything - the segment it
		/// belongs to has to be a curve through keys.</summary>
		private bool HasHandle( int index, bool outgoing ) => outgoing
			? index + 1 < _track.Keyframes.Count && _track.Keyframes[index].IsCubic
			: index > 0 && _track.Keyframes[index - 1].IsCubic;

		private (Vector2 Key, Vector2 In, Vector2 Out)? HandlePoints()
		{
			if ( _selected is not { } sel || _track is null || sel.Key >= _track.Keyframes.Count )
				return null;

			var key = _track.Keyframes[sel.Key];
			var value = KeyValues( sel.Channel )[sel.Key];
			var center = new Vector2( X( key.Frame ), Y( value ) );

			Vector2 Along( bool outgoing )
			{
				var slope = SlopeOf( key.Local, TangentOf( _track, sel.Key, outgoing ), sel.Channel );

				// One frame along the slope in screen space, scaled to a fixed handle length.
				var direction = new Vector2( X( key.Frame + 1f ) - X( key.Frame ), Y( value + slope ) - Y( value ) );
				var length = direction.Length;
				return length < 1e-4f ? center : center + direction / length * HandlePixels * (outgoing ? 1f : -1f);
			}

			return (center, Along( false ), Along( true ));
		}

		private void PaintHandles()
		{
			if ( HandlePoints() is not { } h || _selected is not { } sel )
				return;

			Paint.SetPen( Color.White.WithAlpha( 0.7f ), 1f );

			foreach ( var (point, outgoing) in new[] { (h.In, false), (h.Out, true) } )
			{
				if ( !HasHandle( sel.Key, outgoing ) )
					continue;

				Paint.SetPen( Color.White.WithAlpha( 0.7f ), 1f );
				Paint.DrawLine( h.Key, point );
				Paint.ClearPen();
				Paint.SetBrush( Theme.Yellow );
				Paint.DrawRect( new Rect( point.x - 3f, point.y - 3f, 6f, 6f ), 1f );
			}
		}

		private (int Key, Channel Channel)? HitKey( Vector2 position )
		{
			if ( _track is null )
				return null;

			(int, Channel)? best = null;
			var bestDistance = 7f;

			foreach ( var c in Visible() )
			{
				var values = KeyValues( c );

				for ( var i = 0; i < values.Count; i++ )
				{
					var d = (new Vector2( X( _track.Keyframes[i].Frame ), Y( values[i] ) ) - position).Length;

					if ( d < bestDistance )
					{
						bestDistance = d;
						best = (i, c);
					}
				}
			}

			return best;
		}

		protected override void OnMousePress( MouseEvent e )
		{
			if ( _track is null )
				return;

			if ( e.MiddleMouseButton || (e.LeftMouseButton && e.HasAlt) )
			{
				_drag = DragKind.Pan;
				_panFrom = e.LocalPosition;
				_panView = (_f0, _f1, _v0, _v1);
				e.Accepted = true;
				return;
			}

			if ( !e.LeftMouseButton )
				return;

			if ( HandlePoints() is { } h && _selected is { } sel )
			{
				if ( HasHandle( sel.Key, true ) && (h.Out - e.LocalPosition).Length < 7f )
				{
					BeginHandle( DragKind.HandleOut, e.HasShift );
					e.Accepted = true;
					return;
				}

				if ( HasHandle( sel.Key, false ) && (h.In - e.LocalPosition).Length < 7f )
				{
					BeginHandle( DragKind.HandleIn, e.HasShift );
					e.Accepted = true;
					return;
				}
			}

			if ( HitKey( e.LocalPosition ) is { } hit )
			{
				_selected = hit;
				_drag = DragKind.Key;
				Update();
				e.Accepted = true;
				return;
			}

			_selected = null;
			_drag = DragKind.Scrub;
			_owner.Scrubbed?.Invoke( MathF.Max( Frame( e.LocalPosition.x ), 0f ) );
			Update();
			e.Accepted = true;
		}

		/// <summary>Grabbing a handle turns an automatic tangent into the key's own, starting from
		/// exactly where the automatic one was, so the curve does not jump on the click.</summary>
		private void BeginHandle( DragKind kind, bool breakTangent )
		{
			var sel = _selected.Value;
			var key = _track.Keyframes[sel.Key];

			key.TangentIn ??= TangentOf( _track, sel.Key, false ).ToList();
			key.TangentOut ??= TangentOf( _track, sel.Key, true ).ToList();

			_drag = kind;
			_breakTangent = breakTangent;
		}

		protected override void OnMouseMove( MouseEvent e )
		{
			// The wheel's zoom anchor. MouseTracking is on, so this reports without a button held.
			_lastMouse = e.LocalPosition;

			switch ( _drag )
			{
				case DragKind.Pan:
				{
					var df = (e.LocalPosition.x - _panFrom.x) / MathF.Max( Width - Pad * 2f, 1f ) * (_panView.F1 - _panView.F0);
					var dv = (e.LocalPosition.y - _panFrom.y) / MathF.Max( Height - Pad * 2f, 1f ) * (_panView.V1 - _panView.V0);
					(_f0, _f1, _v0, _v1) = (_panView.F0 - df, _panView.F1 - df, _panView.V0 + dv, _panView.V1 + dv);
					Update();
					break;
				}

				case DragKind.Scrub:
					_owner.Scrubbed?.Invoke( MathF.Max( Frame( e.LocalPosition.x ), 0f ) );
					Update();
					break;

				case DragKind.Key when _selected is { } sel:
				{
					var key = _track.Keyframes[sel.Key];
					var value = Value( e.LocalPosition.y );
					key.Local = WithValue( key.Local, sel.Channel, value, _track.Target == TrackTarget.Camera );
					_owner.Changing?.Invoke();
					Update();
					break;
				}

				case DragKind.HandleIn or DragKind.HandleOut when _selected is { } sel:
				{
					var key = _track.Keyframes[sel.Key];
					var outgoing = _drag == DragKind.HandleOut;
					var keyValue = KeyValues( sel.Channel )[sel.Key];

					var df = Frame( e.LocalPosition.x ) - key.Frame;

					// A handle dragged through to the wrong side of its key is held one pixel short
					// of vertical rather than inverting the slope.
					var minimum = (_f1 - _f0) / MathF.Max( Width, 1f );
					df = outgoing ? MathF.Max( df, minimum ) : MathF.Min( df, -minimum );

					var slope = (Value( e.LocalPosition.y ) - keyValue) / df;

					var tangentIn = key.TangentIn.ToArray();
					var tangentOut = key.TangentOut.ToArray();

					if ( outgoing || !_breakTangent )
						SetSlope( key.Local, tangentOut, sel.Channel, slope );

					if ( !outgoing || !_breakTangent )
						SetSlope( key.Local, tangentIn, sel.Channel, slope );

					key.TangentIn = tangentIn.ToList();
					key.TangentOut = tangentOut.ToList();

					_owner.Changing?.Invoke();
					Update();
					break;
				}
			}
		}

		protected override void OnMouseReleased( MouseEvent e )
		{
			var finished = _drag;
			_drag = DragKind.None;

			if ( finished == DragKind.Key )
				_owner.Edited?.Invoke( "Move Key in Graph" );
			else if ( finished is DragKind.HandleIn or DragKind.HandleOut )
				_owner.Edited?.Invoke( "Bend Tangent" );
		}

		protected override void OnMouseWheel( WheelEvent e )
		{
			var factor = e.Delta > 0 ? 1f / 1.15f : 1.15f;
			var anchorF = Frame( _lastMouse.x );
			var anchorV = Value( _lastMouse.y );

			// Ctrl zooms values only, Shift time only; plain zooms both about the cursor.
			if ( !e.HasCtrl )
			{
				_f0 = anchorF + (_f0 - anchorF) * factor;
				_f1 = anchorF + (_f1 - anchorF) * factor;
			}

			if ( !e.HasShift )
			{
				_v0 = anchorV + (_v0 - anchorV) * factor;
				_v1 = anchorV + (_v1 - anchorV) * factor;
			}

			Update();
			e.Accept();
		}

		private Vector2 _lastMouse;

		protected override void OnContextMenu( ContextMenuEvent e )
		{
			if ( _track is null || HitKey( e.LocalPosition ) is not { } hit )
				return;

			_selected = hit;
			Update();

			var key = _track.Keyframes[hit.Key];
			var menu = new Menu( this );
			menu.AddHeading( $"{_track.DisplayName} — frame {key.Frame}" );

			menu.AddOption( "Auto Tangents", "auto_fix_high", () =>
			{
				key.TangentIn = null;
				key.TangentOut = null;
				Done( "Auto Tangents" );
			} ).StatusTip = "Let the neighbouring keys set this key's slopes again";

			menu.AddOption( "Flat Tangents", "horizontal_rule", () =>
			{
				key.TangentIn = Enumerable.Repeat( 0f, RigCurve.Channels ).ToList();
				key.TangentOut = Enumerable.Repeat( 0f, RigCurve.Channels ).ToList();
				Done( "Flat Tangents" );
			} ).StatusTip = "Arrive and leave level - a hold that eases in and out, without changing the mode";

			menu.AddSeparator();

			foreach ( var mode in new[] { KeyInterpolation.Cubic, KeyInterpolation.Monotone, KeyInterpolation.Smooth, KeyInterpolation.Linear, KeyInterpolation.Stepped } )
			{
				var option = menu.AddOption( mode.ToString(), null, () =>
				{
					key.Interpolation = mode;
					Done( "Set Interpolation" );
				} );

				option.Checkable = true;
				option.Checked = key.Interpolation == mode;
			}

			menu.OpenAtCursor();
			e.Accepted = true;
		}

		private void Done( string label )
		{
			_owner.Changing?.Invoke();
			_owner.Edited?.Invoke( label );
			Update();
		}

	}
}
