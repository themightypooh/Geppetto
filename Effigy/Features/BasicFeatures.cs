using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Creates a primitive solid. Onshape's own Primitives feature works this way — one dropdown, and
/// the dialog shows only the fields that shape actually has.
///
/// Parameters deliberately changes with Shape rather than showing every field greyed out. That is
/// the behaviour being copied: a box dialog asks for three lengths and nothing else.
/// </summary>
public sealed class PrimitiveFeature : Feature
{
	public override string TypeName => Shape.Value;

	public readonly ChoiceParam Shape = new( "Shape",
		new[] { "Box", "Cylinder", "Sphere", "Wedge", "Tube", "Plane" } );

	public readonly FloatParam SizeX = new( "Width", 1f, 0.0001f, unit: "u" );
	public readonly FloatParam SizeY = new( "Depth", 1f, 0.0001f, unit: "u" );
	public readonly FloatParam SizeZ = new( "Height", 1f, 0.0001f, unit: "u" );
	public readonly FloatParam Radius = new( "Radius", 0.5f, 0.0001f, unit: "u" );
	public readonly FloatParam InnerRadius = new( "Inner radius", 0.3f, 0f, unit: "u" );
	public readonly IntParam Segments = new( "Segments", 16, 3, 512 );
	public readonly IntParam Divisions = new( "Divisions", 4, 1, 64 );
	public readonly Vec3Param Position = new( "Position", Vec3.Zero );

	/// <summary>
	/// Per-axis scale, applied about the primitive's own origin before it is moved into place.
	///
	/// NOT THE SAME THING AS THE SIZE PARAMETERS, and worth having alongside them. Width/Depth/
	/// Height build the shape at a size; this stretches whatever was built, which is the only way
	/// to get an ellipsoid out of a sphere or an oval tube out of a round one — those are defined
	/// by a radius and have no per-axis size to set.
	/// </summary>
	public readonly Vec3Param Scale = new( "Scale", Vec3.One );

	/// <summary>
	/// Whether the dialog keeps the three scale axes equal as you edit one of them.
	///
	/// A UI CONVENIENCE THAT IS PERSISTED, not something the kernel enforces. Scale stays the
	/// single truth about the shape — Execute reads all three axes and never consults this — so a
	/// document always builds exactly what its three numbers say. This only rides along so the
	/// dialog can remember that you were editing that primitive uniformly.
	/// </summary>
	public readonly BoolParam UniformScale = new( "Uniform scale", false );

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	/// <summary>The slot, folded away with the other features' — a primitive is placed and sized,
	/// and painted later from the Materials panel.</summary>
	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Material };

	public override IReadOnlyList<IParam> Parameters => Shape.Value switch
	{
		"Box" => new IParam[] { Shape, SizeX, SizeY, SizeZ, Position, Scale, UniformScale, Material },
		"Cylinder" => new IParam[] { Shape, Radius, SizeZ, Segments, Position, Scale, UniformScale, Material },
		"Sphere" => new IParam[] { Shape, Radius, Divisions, Position, Scale, UniformScale, Material },
		"Wedge" => new IParam[] { Shape, SizeX, SizeY, SizeZ, Position, Scale, UniformScale, Material },
		"Tube" => new IParam[] { Shape, Radius, InnerRadius, SizeZ, Segments, Position, Scale, UniformScale, Material },
		"Plane" => new IParam[] { Shape, SizeX, SizeY, Segments, Position, Scale, UniformScale, Material },
		_ => new IParam[] { Shape }
	};

	protected override void Execute( FeatureContext ctx )
	{
		var mesh = Shape.Value switch
		{
			"Box" => Primitives.Box( SizeX.Clamped, SizeY.Clamped, SizeZ.Clamped, Material.Clamped ),
			"Cylinder" => Primitives.Cylinder( Radius.Clamped, SizeZ.Clamped, Segments.Clamped, Material.Clamped ),
			"Sphere" => Primitives.QuadSphere( Radius.Clamped, Divisions.Clamped, Material.Clamped ),
			"Wedge" => Primitives.Wedge( SizeX.Clamped, SizeY.Clamped, SizeZ.Clamped, Material.Clamped ),
			"Tube" => BuildTube(),
			"Plane" => Primitives.Plane( SizeX.Clamped, SizeY.Clamped, Segments.Clamped, Segments.Clamped, Material.Clamped ),
			_ => throw new FeatureException( new FeatureDiagnostic(
				DiagnosticSeverity.Error,
				$"unknown shape '{Shape.Value}'",
				"The shape dropdown has a value this feature does not build.",
				"Shape",
				remedies: new[] { "Pick Box, Cylinder, Sphere, Wedge, Tube or Plane" } ) )
		};

		var scale = Scale.Value;

		if ( scale.x == 0f || scale.y == 0f || scale.z == 0f )
		{
			FailOn( "Scale",
				"Scale cannot be zero on any axis",
				$"Scale is ({scale.x:0.###}, {scale.y:0.###}, {scale.z:0.###}). A zero axis flattens the solid to nothing.",
				"Set every scale axis to a non-zero value" );
		}

		// SCALE FIRST, ABOUT THE PRIMITIVE'S OWN ORIGIN. Applied after the translate it would
		// multiply the position too, so nudging a scaled box would move it by the scale factor and
		// the number in the Position field would stop meaning where the box is.
		if ( scale.x != 1f || scale.y != 1f || scale.z != 1f )
			MeshTransform.Apply( mesh, Xform.Scale( scale ) );

		if ( Position.Value.LengthSquared > 0f )
			MeshTransform.Apply( mesh, Xform.Translate( Position.Value ) );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}

	PolyMesh BuildTube()
	{
		// Caught here rather than left to Primitives, so the message names the parameter the user
		// can actually see in the dialog.
		if ( InnerRadius.Clamped >= Radius.Clamped )
		{
			FailOn( "Inner radius",
				"Inner radius must be smaller than radius",
				$"Inner radius is {InnerRadius.Clamped:0.###} and radius is {Radius.Clamped:0.###}.",
				"Reduce Inner radius",
				"Increase Radius" );
		}

		return Primitives.Tube( Radius.Clamped, InnerRadius.Clamped, SizeZ.Clamped, Segments.Clamped, Material.Clamped );
	}
}

/// <summary>Move, rotate and scale bodies. Onshape's Transform.</summary>
public sealed class TransformFeature : Feature
{
	public override string TypeName => "Transform";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly Vec3Param Translate = new( "Translate", Vec3.Zero );
	public readonly Vec3Param RotationAxis = new( "Rotation axis", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam RotationAngle = new( "Angle", 0f, unit: "deg" );
	public readonly Vec3Param Scale = new( "Scale", Vec3.One );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Translate, RotationAxis, RotationAngle, Scale };

	protected override void Execute( FeatureContext ctx )
	{
		var scale = Scale.Value;

		if ( scale.x == 0f || scale.y == 0f || scale.z == 0f )
		{
			FailOn( "Scale",
				"Scale cannot be zero on any axis",
				$"Scale is ({scale.x:0.###}, {scale.y:0.###}, {scale.z:0.###}). A zero axis flattens the solid to nothing.",
				"Set every scale axis to a non-zero value" );
		}

		// Scale, then rotate, then translate — the order a user expects, and the one that keeps a
		// rotation about the origin from being skewed by a non-uniform scale applied after it.
		var xform =
			Xform.Translate( Translate.Value )
			* Xform.Rotate( RotationAxis.Value, RotationAngle.Value * MathF.PI / 180f )
			* Xform.Scale( scale );

		foreach ( var body in RequireBodies( ctx, Bodies ) )
			MeshTransform.Apply( body.Mesh, xform );
	}
}

/// <summary>Copies along a direction. Onshape's Linear pattern.</summary>
public sealed class LinearPatternFeature : Feature
{
	public override string TypeName => "Linear pattern";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly Vec3Param Direction = new( "Direction", new Vec3( 1, 0, 0 ) );
	public readonly FloatParam Spacing = new( "Spacing", 1f, unit: "u" );
	public readonly IntParam Count = new( "Instances", 3, 1, 4096 );
	public readonly BoolParam Merge = new( "Merge into one body", false );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Direction, Spacing, Count, Merge };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Direction.Value.LengthSquared < 1e-12f )
		{
			FailOn( "Direction",
				"Direction cannot be zero",
				"A linear pattern copies along a direction, and this one has no length, so there is nowhere to put the copies.",
				"Set Direction to the axis you want the copies to run along" );
		}

		var dir = Direction.Value.Normal;
		var sources = RequireBodies( ctx, Bodies );

		// Instance 0 is the original, so a count of 3 means the original plus two copies — which
		// is what Onshape's instance count means too.
		foreach ( var source in sources )
		{
			// Snapshot BEFORE the loop. With Merge on, the loop appends into source.Mesh, so
			// reading source.Mesh each iteration would copy the copies too and the instance count
			// would double rather than increment: 6, 12, 24, 48 faces instead of 6, 12, 18, 24.
			var original = source.Mesh.Clone();

			for ( var i = 1; i < Count.Clamped; i++ )
			{
				var copy = MeshTransform.Transformed( original, Xform.Translate( dir * (Spacing.Value * i) ) );

				if ( Merge.Value )
					MeshTransform.Append( source.Mesh, copy );
				else
					ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{Name} {i}", copy ) );
			}
		}
	}
}

/// <summary>Copies around an axis. Onshape's Circular pattern.</summary>
public sealed class CircularPatternFeature : Feature
{
	public override string TypeName => "Circular pattern";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly Vec3Param AxisPoint = new( "Axis through", Vec3.Zero );
	public readonly Vec3Param AxisDirection = new( "Axis", new Vec3( 0, 0, 1 ) );
	public readonly IntParam Count = new( "Instances", 4, 1, 4096 );
	public readonly FloatParam TotalAngle = new( "Angle", 360f, unit: "deg" );
	public readonly BoolParam Merge = new( "Merge into one body", false );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, AxisPoint, AxisDirection, Count, TotalAngle, Merge };

	protected override void Execute( FeatureContext ctx )
	{
		if ( AxisDirection.Value.LengthSquared < 1e-12f )
		{
			FailOn( "Axis",
				"Axis cannot be zero",
				"A circular pattern spins around an axis, and this one has no length.",
				"Set Axis to the direction to spin around" );
		}

		var sources = RequireBodies( ctx, Bodies );

		var count = Count.Clamped;

		// A full turn puts instance N back on instance 0, so the step divides by count. A partial
		// sweep spreads the instances across the arc inclusive of both ends instead.
		var full = MathF.Abs( MathF.Abs( TotalAngle.Value ) - 360f ) < 1e-3f;
		var step = count <= 1 ? 0f : TotalAngle.Value / (full ? count : count - 1);

		foreach ( var source in sources )
		{
			// Snapshot before the loop — same compounding trap as the linear pattern.
			var original = source.Mesh.Clone();

			for ( var i = 1; i < count; i++ )
			{
				var xform = Xform.RotateAbout(
					AxisPoint.Value, AxisDirection.Value, step * i * MathF.PI / 180f );

				var copy = MeshTransform.Transformed( original, xform );

				if ( Merge.Value )
					MeshTransform.Append( source.Mesh, copy );
				else
					ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{Name} {i}", copy ) );
			}
		}
	}
}

/// <summary>Reflects bodies in a plane. Onshape's Mirror.</summary>
public sealed class MirrorFeature : Feature
{
	public override string TypeName => "Mirror";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly Vec3Param PlanePoint = new( "Plane through", Vec3.Zero );
	public readonly Vec3Param PlaneNormal = new( "Plane normal", new Vec3( 1, 0, 0 ) );
	public readonly BoolParam KeepOriginal = new( "Keep original", true );
	public readonly BoolParam Merge = new( "Merge into one body", false );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, PlanePoint, PlaneNormal, KeepOriginal, Merge };

	protected override void Execute( FeatureContext ctx )
	{
		if ( PlaneNormal.Value.LengthSquared < 1e-12f )
		{
			FailOn( "Plane normal",
				"Plane normal cannot be zero",
				"A mirror needs a plane to reflect across, and a zero normal does not define one.",
				"Set Plane normal to the direction the mirror should face" );
		}

		var xform = Xform.Mirror( PlanePoint.Value, PlaneNormal.Value );
		var sources = RequireBodies( ctx, Bodies );

		foreach ( var source in sources )
		{
			// MeshTransform.Apply reverses winding for us — the mirror flips handedness, and
			// without that reversal every mirrored face would point into the solid.
			var copy = MeshTransform.Transformed( source.Mesh, xform );

			if ( Merge.Value )
				MeshTransform.Append( source.Mesh, copy );
			else
				ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{Name}", copy ) );
		}

		if ( !KeepOriginal.Value )
		{
			foreach ( var source in sources )
				ctx.Bodies.Remove( source );
		}
	}
}

/// <summary>
/// Catmull-Clark subdivision as a history feature.
///
/// Onshape has no equivalent, and this is the deliberate place where the tool stops being CAD.
/// Putting subdivision IN the tree rather than after it is what keeps the pipeline honest: roll
/// the bar back above this feature and you are editing the low-poly cage, roll it forward and you
/// see the dense surface. The cage is what the sculpt eventually bakes down onto, so it has to
/// stay reachable rather than being consumed by an export step.
/// </summary>
public sealed class SubdivideFeature : Feature
{
	public override string TypeName => "Subdivide";

	/// <summary>
	/// Which faces to subdivide. EMPTY MEANS THE WHOLE BODY, which is both the old behaviour and
	/// the honest default — a subdivision surface is a property of a cage, not of a corner of one.
	///
	/// Picking faces switches the operation from smooth to linear, and the two really are different
	/// operations rather than a flag on one. See CatmullClark.SubdivideFaces: you cannot apply the
	/// limit rules to part of a mesh without moving the vertices the rest of it is standing on. So
	/// this is density where you need it — a face about to be sculpted, a panel about to be bent —
	/// and the whole-body form remains the one that smooths.
	///
	/// Held as FaceRefs, like every other face pick, so the choice survives the rebuild that
	/// recreates the faces it names.
	/// </summary>
	public List<FaceRef> Faces = new();

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly IntParam Levels = new( "Levels", 1, 0, 6 );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Levels };

	/// <summary>What this feature will cost at the current settings, for a UI that warns before
	/// rather than after. Levels are exponential and the jump from 4 to 6 is 16x.</summary>
	public (int Vertices, int Faces) PredictCost( IEnumerable<Body> bodies )
	{
		var v = 0;
		var f = 0;
		var list = bodies as IList<Body> ?? bodies.ToList();

		if ( Faces.Count > 0 )
		{
			// Local subdivision only touches the bodies that were picked on, so the bodies it did
			// not touch still cost exactly what they already are.
			var picked = Resolve( list, out _ );

			foreach ( var body in list )
			{
				var (bv, bf) = picked.TryGetValue( body, out var indices )
					? CatmullClark.PredictLocalCost( body.Mesh, indices, Levels.Clamped )
					: (body.Mesh.VertexCount, body.Mesh.FaceCount);

				v += bv;
				f += bf;
			}

			return (v, f);
		}

		foreach ( var body in list.Where( Bodies.Matches ) )
		{
			var (bv, bf) = CatmullClark.PredictCost( body.Mesh, Levels.Clamped );
			v += bv;
			f += bf;
		}

		return (v, f);
	}

	/// <summary>Picked faces grouped by the body they landed on. <paramref name="lost"/> counts the
	/// references that no longer resolve — geometry upstream changed under them.</summary>
	Dictionary<Body, List<int>> Resolve( IEnumerable<Body> bodies, out int lost )
	{
		var byBody = new Dictionary<Body, List<int>>();
		lost = 0;

		foreach ( var reference in Faces )
		{
			if ( !FacePlane.TryResolveFace( bodies, reference, out var body, out var faceIndex ) )
			{
				lost++;
				continue;
			}

			if ( !byBody.TryGetValue( body, out var indices ) )
				byBody[body] = indices = new List<int>();

			indices.Add( faceIndex );
		}

		return byBody;
	}

	protected override void Execute( FeatureContext ctx )
	{
		if ( Levels.Clamped == 0 )
			return;

		if ( Faces.Count == 0 )
		{
			foreach ( var body in RequireBodies( ctx, Bodies ) )
				body.Mesh = CatmullClark.Subdivide( body.Mesh, Levels.Clamped );

			return;
		}

		// A picked face already names its body, so the Bodies filter has nothing left to decide and
		// is not consulted here. Two picks on the same body are subdivided in ONE call rather than
		// one call each: the second call would be running against a mesh whose face indices the
		// first has already renumbered.
		var picked = Resolve( ctx.Bodies, out var lost );

		if ( picked.Count == 0 )
		{
			Fail(
				"None of the picked faces are still there",
				"Every face this feature subdivides was removed or replaced by a change further up the tree.",
				"Pick the faces again",
				"Or clear the picks to subdivide the whole body" );
		}

		foreach ( var (body, indices) in picked )
			body.Mesh = CatmullClark.SubdivideFaces( body.Mesh, indices, Levels.Clamped );

		if ( lost > 0 )
		{
			Warn(
				$"{lost} picked {(lost == 1 ? "face is" : "faces are")} no longer there",
				"A change further up the tree removed or replaced them, so they were skipped.",
				"Pick them again if the extra density is still wanted" );
		}
	}
}
