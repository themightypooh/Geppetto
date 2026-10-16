using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// Radius, strength, falloff and brush kind for weight painting. Same shape as the sculpt and
/// paint bars: values about the stroke, floating near the model.
/// </summary>
internal sealed class EffigyWeightBar : Widget
{
	public const float BarHeight = 28f;

	public const float MinStrength = 0.01f;

	private readonly EffigyNumericField _radius;
	private readonly EffigyNumericField _strength;
	private readonly ComboBox _falloff;
	private readonly ComboBox _kind;
	private readonly Editor.Label _bone;

	private WeightPaintSession _session;
	private bool _refreshing;

	public Action Changed { get; set; }

	public EffigyWeightBar( Widget parent ) : base( parent )
	{
		TranslucentBackground = true;
		NoSystemBackground = true;
		Visible = false;
		FixedHeight = BarHeight;
		FixedWidth = 720f;

		Layout = Layout.Row();
		Layout.Spacing = 8;

		Layout.Add( new Editor.Label( "Radius" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );
		_radius = new EffigyNumericField( this, 0.25f, "u" )
		{
			Label = "Brush radius",
			Min = 1e-4f,
			ValueEdited = v => { if ( _session is not null ) { _session.Radius = MathF.Max( v, 1e-4f ); Changed?.Invoke(); } },
			FixedWidth = 90f,
		};
		Layout.Add( _radius );

		Layout.Add( new Editor.Label( "Strength" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );
		// Floored a whisker above zero: a strength of 0 is a brush that changes nothing, and a
		// stroke that visibly does nothing is the one thing a weight brush must never do.
		_strength = new EffigyNumericField( this, 0.15f )
		{
			Label = "Weight strength",
			Min = MinStrength,
			Max = 1f,
			ValueEdited = v => { if ( _session is not null ) { _session.Strength = Math.Clamp( v, MinStrength, 1f ); Changed?.Invoke(); } },
			FixedWidth = 90f,
		};
		Layout.Add( _strength );

		Layout.Add( new Editor.Label( "Falloff" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );
		_falloff = new ComboBox( this ) { MinimumWidth = 90f };
		Layout.Add( _falloff );

		Layout.Add( new Editor.Label( "Brush" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );
		_kind = new ComboBox( this ) { MinimumWidth = 90f };
		Layout.Add( _kind );

		_bone = new Editor.Label( "" ) { Color = Theme.TextControl.WithAlpha( 0.75f ) };
		Layout.Add( _bone, 1 );
	}

	public Color GapColor { get; set; } = Theme.ControlBackground;

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}

	public void Bind( WeightPaintSession session, string boneName )
	{
		_session = session;
		Visible = session is not null;

		if ( session is not null )
			Refresh( boneName );
	}

	/// <summary>
	/// Pull the controls back into step with the session.
	///
	/// The bone label is read off the SESSION'S bone, not the name passed in. The two used to be
	/// the same thing; once picking another bone in the Rig tree switches what the brush paints
	/// mid-session, the Rig tree's selection and the brush's bone can briefly differ (a click on
	/// empty space deselects in the tree and leaves the brush where it was), and the label has to
	/// say what the NEXT STROKE will paint. <paramref name="boneName"/> is only the fallback for a
	/// session with no bone.
	/// </summary>
	public void Refresh( string boneName = null )
	{
		if ( _session is null )
			return;

		if ( _session.Bone >= 0 && _session.Bone < _session.Skeleton.Count )
			boneName = _session.Skeleton.Bones[_session.Bone].Name;

		_radius.SetValue( _session.Radius );
		_strength.SetValue( _session.Strength );
		RefreshCombo( _falloff, Enum.GetValues<BrushFalloff>(), _session.Falloff, k => _session.Falloff = k );
		RefreshCombo( _kind, Enum.GetValues<WeightBrushKind>(), _session.Brush, k => _session.Brush = k );
		_bone.Text = string.IsNullOrEmpty( boneName )
			? "Pick a bone in the Rig tree"
			: $"Painting {boneName} — ramp is a texture, not vertex colour";
	}

	private void RefreshCombo<T>( ComboBox box, T[] values, T current, Action<T> set ) where T : struct, Enum
	{
		if ( box is null )
			return;

		_refreshing = true;

		try
		{
			box.Clear();

			foreach ( var value in values )
			{
				var kind = value;
				box.AddItem( kind.ToString(), onSelected: () =>
				{
					if ( _refreshing || _session is null )
						return;

					set( kind );
					Changed?.Invoke();
				}, selected: kind.Equals( current ) );
			}
		}
		finally
		{
			_refreshing = false;
		}
	}
}
