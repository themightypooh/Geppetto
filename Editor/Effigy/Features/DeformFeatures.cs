using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

// Whole-body deforms that stay in the history: proportions and poses changed without resculpting,
// and changed back by editing a number. Positions only, so topology, UVs and rigs survive them.

/// <summary>
/// Twist, bend, taper or stretch the selected bodies along an axis. A horn gets its spiral, a tail
/// its curl, a limb its taper, all as numbers that rebuild.
/// </summary>
public sealed class SimpleDeformFeature : Feature
{
	public override string TypeName => "Simple deform";

	public override GeometryKind Accepts => GeometryKind.Body;

	public static readonly string[] Modes = { "Twist", "Bend", "Taper", "Stretch" };
	public static readonly string[] Directions = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };
	public static readonly string[] Pivots = { "Body centre", "Body base", "Point" };

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly ChoiceParam Mode = new( "Mode", Modes );
	public readonly ChoiceParam Axis = new( "Axis", new[] { "X", "Y", "Z" }, 2 );

	/// <summary>Twist and Bend: the total turn across the band.</summary>
	public readonly FloatParam Angle = new( "Angle", 45f, -3600f, 3600f, "deg" );

	/// <summary>Taper: how much wider the far end of the band gets (0.5 = half as wide again, -0.5 = half
	/// as wide). Stretch: how much longer the band gets.</summary>
	public readonly FloatParam Factor = new( "Factor", 0.5f, -10f, 10f );

	public readonly ChoiceParam BendToward = new( "Bend toward", Directions );
	public readonly ChoiceParam Pivot = new( "Pivot", Pivots );
	public readonly Vec3Param PivotPoint = new( "Pivot point", Vec3.Zero );

	/// <summary>The band the deform acts over, as fractions of the body's length along the axis.</summary>
	public readonly FloatParam Lower = new( "Lower limit", 0f, 0f, 1f );
	public readonly FloatParam Upper = new( "Upper limit", 1f, 0f, 1f );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Mode, Axis, Angle, Factor, BendToward, Pivot, PivotPoint, Lower, Upper };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Pivot, PivotPoint, Lower, Upper };

	internal static Vec3 AxisVector( int index ) => index switch
	{
		0 => new Vec3( 1, 0, 0 ),
		1 => new Vec3( 0, 1, 0 ),
		_ => new Vec3( 0, 0, 1 ),
	};

	internal static Vec3 DirectionVector( int index ) => AxisVector( index / 2 ) * (index % 2 == 0 ? 1f : -1f);

	protected override void Execute( FeatureContext ctx )
	{
		var mode = (SimpleDeformMode)Math.Clamp( Mode.Index, 0, 3 );
		var axis = AxisVector( Axis.Index );

		if ( Upper.Clamped <= Lower.Clamped )
		{
			FailOn( "Upper limit", Math.Min( 1f, Lower.Clamped + 0.5f ),
				"The band is empty",
				$"The upper limit ({Upper.Clamped:0.###}) has to be above the lower one ({Lower.Clamped:0.###}); the deform acts between them.",
				"Raise Upper limit, or lower Lower limit" );
		}

		var toward = DirectionVector( BendToward.Index );

		if ( mode == SimpleDeformMode.Bend && MathF.Abs( Vec3.Dot( toward, axis ) ) > 0.5f )
		{
			FailOn( "Bend toward",
				"A bend can't curl toward its own axis",
				$"The axis is {Axis.Value} and Bend toward is {BendToward.Value}, which points along it.",
				"Pick a Bend toward direction across the axis" );
		}

		if ( mode == SimpleDeformMode.Stretch && Factor.Value <= -0.999f )
		{
			FailOn( "Factor", -0.5f,
				"A stretch can't shrink a body to nothing",
				$"Factor {Factor.Value:0.###} would make the band {1f + Factor.Value:0.###} times as long.",
				"Use a Factor above -1 (-0.5 halves the length)" );
		}

		var settings = new List<(Body Body, SimpleDeformSettings Settings)>();

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var (lo, hi) = ModelMeasure.Bounds( body.Mesh );
			var centre = (lo + hi) * 0.5f;
			var pivot = Pivot.Index switch
			{
				1 => centre - axis * Vec3.Dot( centre - lo, axis ),
				2 => PivotPoint.Value,
				_ => centre,
			};

			var min = Vec3.Dot( lo - pivot, axis );
			var max = Vec3.Dot( hi - pivot, axis );

			if ( max - min <= 1e-6f )
			{
				FailOn( "Axis",
					$"'{body.Name ?? body.Id}' is flat along {Axis.Value}",
					"It has no length along the deform axis for the deform to spread over.",
					"Pick the axis the body is long along" );
			}

			settings.Add( (body, new SimpleDeformSettings
			{
				Mode = mode,
				Axis = axis,
				Toward = toward,
				Pivot = pivot,
				Angle = Angle.Value * MathF.PI / 180f,
				Factor = Factor.Value,
				Lower = min + (max - min) * Lower.Clamped,
				Upper = min + (max - min) * Upper.Clamped,
			}) );
		}

		var moved = 0;

		foreach ( var (body, s) in settings )
			moved += SimpleDeform.Apply( body.Mesh, s );

		if ( moved == 0 )
		{
			var what = mode is SimpleDeformMode.Twist or SimpleDeformMode.Bend ? "Angle" : "Factor";
			Warn(
				"This deform changes nothing",
				$"{Mode.Value} with {what} 0 leaves every vertex where it was.",
				$"Set {what} to something other than 0" );
		}
	}
}

/// <summary>
/// A cage of control points around the selected bodies; moving a point drags the shape near it.
/// Broad proportion changes — a longer snout, a heavier jaw, a pot belly — without resculpting.
/// </summary>
public sealed class LatticeFeature : Feature
{
	public override string TypeName => "Lattice";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly IntParam PointsX = new( "Points across X", 3, 2, 12 );
	public readonly IntParam PointsY = new( "Points across Y", 3, 2, 12 );
	public readonly IntParam PointsZ = new( "Points across Z", 3, 2, 12 );
	public readonly ChoiceParam Interpolation = new( "Interpolation", new[] { "Smooth", "Linear" } );

	/// <summary>How far the cage stands off the bodies, as a share of their size on each side.</summary>
	public readonly FloatParam Margin = new( "Margin", 0.05f, 0f, 2f );

	/// <summary>Moved control points: "i,j,k: dx,dy,dz; ..." — see LatticeCage.TryParseOffsets.</summary>
	public readonly StringParam Offsets = new( "Point offsets", "" );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, PointsX, PointsY, PointsZ, Interpolation, Margin, Offsets };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Interpolation, Margin };

	/// <summary>The cage this feature builds for these bodies, offsets included — what the editor
	/// draws. Null and a reason when the offsets can't be read.</summary>
	public LatticeCage BuildCage( IEnumerable<Body> bodies, out string why )
	{
		why = null;
		var list = bodies.Where( b => b?.Mesh is { VertexCount: > 0 } ).ToList();

		if ( list.Count == 0 )
		{
			why = "There is no body with vertices for the cage to go round.";
			return null;
		}

		var (lo, hi) = ModelMeasure.Bounds( list );
		var size = hi - lo;
		var largest = MathF.Max( size.x, MathF.Max( size.y, size.z ) );

		// A flat body (a sheet) still gets a cage with some depth, or that side has no room to move in.
		var pad = new Vec3(
			MathF.Max( size.x * Margin.Clamped, largest * 0.01f ),
			MathF.Max( size.y * Margin.Clamped, largest * 0.01f ),
			MathF.Max( size.z * Margin.Clamped, largest * 0.01f ) );

		var cage = new LatticeCage( lo - pad, hi + pad, PointsX.Clamped, PointsY.Clamped, PointsZ.Clamped );
		return cage.TryParseOffsets( Offsets.Value, out why ) ? cage : null;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );
		var cage = BuildCage( targets, out var why );

		if ( cage is null )
		{
			FailOn( "Point offsets",
				"The point offsets can't be read",
				why,
				"Write each moved point as i,j,k: dx,dy,dz, separated by ;",
				$"Indices count from 0; this cage is {PointsX.Clamped} × {PointsY.Clamped} × {PointsZ.Clamped}" );
		}

		if ( cage.IsIdentity )
		{
			Warn(
				"The lattice hasn't been moved yet",
				"Every control point is where the cage put it, so the bodies are unchanged.",
				"Move a point in Point offsets, e.g. 1,1,2: 0,0,1 lifts the middle of the top" );
			return;
		}

		var smooth = Interpolation.Index == 0;

		foreach ( var body in targets )
			cage.Apply( body.Mesh, smooth );
	}
}
