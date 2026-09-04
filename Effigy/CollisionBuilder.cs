using System;
using System.Collections.Generic;

namespace Effigy;

public enum CollisionKind
{
	Box,
	Sphere,
	Cylinder,
	Hull,
}

/// <summary>One convex piece of a part's physics representation.</summary>
public sealed class CollisionShape
{
	public CollisionKind Kind;

	/// <summary>Centre of the shape, in model space.</summary>
	public Vec3 Position;

	/// <summary>Box: half-extents. Sphere: radius in x. Cylinder: radius in x, half-height in z.</summary>
	public Vec3 Size;

	/// <summary>Hull only: the points on it.</summary>
	public List<Vec3> Points;

	/// <summary>Which body this came from, so a caller can group them.</summary>
	public string BodyId;

	public override string ToString() => Kind switch
	{
		CollisionKind.Box => $"box {Size.x * 2:0.##} x {Size.y * 2:0.##} x {Size.z * 2:0.##}",
		CollisionKind.Sphere => $"sphere r{Size.x:0.##}",
		CollisionKind.Cylinder => $"cylinder r{Size.x:0.##} h{Size.z * 2:0.##}",
		_ => $"hull of {Points?.Count ?? 0} points",
	};
}

/// <summary>What a collision build produced, and how it got there.</summary>
public sealed class CollisionReport
{
	public readonly List<CollisionShape> Shapes;
	public readonly bool FromHistory;
	public readonly string Reason;

	public CollisionReport( List<CollisionShape> shapes, bool fromHistory, string reason )
	{
		Shapes = shapes;
		FromHistory = fromHistory;
		Reason = reason;
	}

	public override string ToString() =>
		$"{Shapes.Count} shape(s), {(FromHistory ? "from the feature history" : "as hulls")}"
		+ (Reason is null ? "" : $" - {Reason}");
}

/// <summary>
/// A part's physics representation, taken from the feature history rather than from the mesh.
///
/// A MODEL KNOWN TO BE A UNION OF N CONVEX PRIMITIVES IS ALREADY ITS OWN COLLISION, so this is
/// bookkeeping rather than geometry. Walking the finished mesh instead would throw away the one
/// thing that makes the answer exact: that somebody typed "box, 2 by 2 by 2" and nothing since has
/// been allowed to disturb it.
///
/// WHAT SPOILS IT is anything that changes a body's shape in a way a primitive cannot describe — an
/// extrude, a revolve, a boolean, a shell, a fillet, a subdivide. The moment one of those is in the
/// tree the history stops being a description of the shape and the fallback takes over: one convex
/// hull per body. Bigger than the part wherever the part is concave, never smaller, always convex,
/// and honest about being an approximation.
///
/// Deliberately all-or-nothing rather than per body. Working out WHICH bodies a boolean touched
/// means tracking body identity through operations that create and destroy them, and a physics
/// representation that is exact for three props and quietly wrong for the fourth is worse than one
/// that is approximate for all four and says so.
/// </summary>
public static class CollisionBuilder
{
	public static CollisionReport Build( PartStudio studio )
	{
		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		if ( TryFromHistory( studio, out var shapes, out var spoiler ) )
			return new CollisionReport( shapes, true, null );

		return new CollisionReport( Hulls( studio ), false,
			spoiler is null ? "there is nothing in the history to read" : $"{spoiler} is in the tree" );
	}

	static bool TryFromHistory( PartStudio studio, out List<CollisionShape> shapes, out string spoiler )
	{
		shapes = new List<CollisionShape>();
		spoiler = null;

		var count = Math.Min( studio.RollbackIndex, studio.Features.Count );

		for ( var i = 0; i < count; i++ )
		{
			var feature = studio.Features[i];

			if ( feature.Suppressed )
				continue;

			switch ( feature )
			{
				case PrimitiveFeature primitive:
					if ( FromPrimitive( primitive ) is { } shape )
						shapes.Add( shape );
					else
						spoiler = primitive.Shape.Value;
					break;

				// A sketch produces no solid, so it cannot spoil anything.
				case SketchFeature:
					continue;

				// These copy what is already there, which is exactly what a shape list can do too.
				case MirrorFeature mirror:
					Mirror( shapes, mirror );
					break;

				case TransformFeature transform:
					// A MOVE CAN BE FOLLOWED; A TURN OR A STRETCH CANNOT. A CollisionShape carries a
					// position and a size, not an orientation, so a rotated box has nowhere to record
					// that it is rotated - and a collision hull sitting square while the part it
					// belongs to is at forty degrees is the kind of wrong that only shows up when
					// something bounces off thin air. So this spoils the decomposition rather than
					// quietly dropping the rotation, and the hulls that replace it are correct.
					if ( MathF.Abs( transform.RotationAngle.Value ) > 1e-4f
						|| (transform.Scale.Value - Vec3.One).LengthSquared > 1e-8f )
					{
						spoiler ??= "a rotated or scaled Transform";
						break;
					}

					Move( shapes, transform );
					break;

				default:
					spoiler ??= feature.TypeName;
					break;
			}

			if ( spoiler is not null )
				return false;
		}

		return shapes.Count > 0;
	}

	static CollisionShape FromPrimitive( PrimitiveFeature primitive )
	{
		var scale = primitive.Scale.Value;
		var position = primitive.Position.Value;

		switch ( primitive.Shape.Value )
		{
			case "Box":
			case "Plane":
				return new CollisionShape
				{
					Kind = CollisionKind.Box,
					Position = position,
					Size = new Vec3(
						primitive.SizeX.Clamped * 0.5f * scale.x,
						primitive.SizeY.Clamped * 0.5f * scale.y,
						primitive.SizeZ.Clamped * 0.5f * scale.z ),
				};

			case "Sphere":
				// A sphere scaled unevenly is an ellipsoid, and there is no ellipsoid collision
				// shape - so it falls back rather than pretending the smallest axis is the radius.
				if ( MathF.Abs( scale.x - scale.y ) > 1e-4f || MathF.Abs( scale.x - scale.z ) > 1e-4f )
					return null;

				return new CollisionShape
				{
					Kind = CollisionKind.Sphere,
					Position = position,
					Size = new Vec3( primitive.Radius.Clamped * scale.x, 0f, 0f ),
				};

			case "Cylinder":
				if ( MathF.Abs( scale.x - scale.y ) > 1e-4f )
					return null;

				return new CollisionShape
				{
					Kind = CollisionKind.Cylinder,
					Position = position,
					Size = new Vec3( primitive.Radius.Clamped * scale.x, 0f,
						primitive.SizeZ.Clamped * 0.5f * scale.z ),
				};

			// A wedge is a prism and a tube has a hole down the middle. Neither is one convex
			// primitive, and a hull of a tube fills its bore in - so they go to the fallback, where
			// at least the approximation is stated.
			default:
				return null;
		}
	}

	static void Mirror( List<CollisionShape> shapes, MirrorFeature mirror )
	{
		var normal = mirror.PlaneNormal.Value;

		if ( normal.LengthSquared < 1e-12f )
			return;

		normal = normal.Normal;

		var origin = mirror.PlanePoint.Value;
		var copies = new List<CollisionShape>( shapes.Count );

		foreach ( var shape in shapes )
		{
			var offset = shape.Position - origin;
			var reflected = origin + offset - normal * (2f * Vec3.Dot( offset, normal ));

			copies.Add( new CollisionShape
			{
				Kind = shape.Kind,
				Position = reflected,
				Size = shape.Size,
				Points = shape.Points,
				BodyId = shape.BodyId,
			} );
		}

		// Keep original off means the mirror REPLACES what it reflected, so the shapes have to go
		// with it. Appending regardless would leave collision on a half of the part that is not there.
		if ( !mirror.KeepOriginal.Value )
			shapes.Clear();

		shapes.AddRange( copies );
	}

	static void Move( List<CollisionShape> shapes, TransformFeature transform )
	{
		var offset = transform.Translate.Value;

		foreach ( var shape in shapes )
			shape.Position += offset;
	}

	/// <summary>One hull per body, which is what an undecomposable model gets.</summary>
	static List<CollisionShape> Hulls( PartStudio studio )
	{
		var shapes = new List<CollisionShape>();

		foreach ( var body in studio.Bodies )
		{
			if ( body.Mesh is not { VertexCount: >= 4 } mesh )
				continue;

			var hull = ConvexHull.Build( mesh.Positions );

			if ( hull is not { } built )
			{
				// Flat, collinear, or a single point: no volume to enclose. A box round it is the
				// only convex answer left and it is at least never smaller than the part.
				shapes.Add( BoxAround( mesh, body.Id ) );
				continue;
			}

			shapes.Add( new CollisionShape
			{
				Kind = CollisionKind.Hull,
				Position = Vec3.Zero,
				Points = built.Points,
				BodyId = body.Id,
			} );
		}

		return shapes;
	}

	static CollisionShape BoxAround( PolyMesh mesh, string bodyId )
	{
		var min = mesh.Positions[0];
		var max = mesh.Positions[0];

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		return new CollisionShape
		{
			Kind = CollisionKind.Box,
			Position = (min + max) * 0.5f,
			Size = (max - min) * 0.5f,
			BodyId = bodyId,
		};
	}
}
