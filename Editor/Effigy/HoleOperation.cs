using System;

namespace Effigy;

/// <summary>The shape of a hole. Named rather than a bare index, because these are also the
/// dropdown a user reads and reordering them is a live possibility.</summary>
public enum HoleStyle
{
	Simple,
	Counterbore,
	Countersink,
}

/// <summary>
/// The tool solid a hole cuts with.
///
/// A HOLE IS NOT A NEW CAPABILITY, IT IS A SHAPE. Holes already work as inner loops of a profile,
/// and cuts already work through <see cref="MeshBoolean"/>. What was missing was the convenience:
/// nobody wants to draw two concentric circles and extrude them to get a counterbore when the
/// numbers they actually have are "M6 clearance, 10mm head, 6 deep".
///
/// So this builds the NEGATIVE — the shape of the void — and the feature hands it to the boolean as
/// a tool. Everything about where it goes comes from the face that was picked.
/// </summary>
public static class HoleOperation
{
	/// <summary>
	/// Build the tool solid for one hole, drilled from <paramref name="at"/> along
	/// <paramref name="into"/>.
	///
	/// <paramref name="depth"/> of zero or less means "through everything", which is built as a
	/// cylinder long enough to leave the body either side — a through hole that stops exactly at the
	/// far surface is a coplanar-face boolean, and those are the ones that go wrong.
	/// </summary>
	public static PolyMesh Build( HoleStyle style, Vec3 at, Vec3 into, float diameter, float depth,
		float headDiameter, float headDepth, float sinkAngleDegrees, float through, int segments = 24 )
	{
		if ( diameter <= 0f )
			throw new InvalidOperationException( "A hole needs a diameter greater than zero." );

		if ( into.LengthSquared < 1e-12f )
			throw new InvalidOperationException( "A hole needs a direction to drill along." );

		segments = Math.Clamp( segments, 6, 256 );

		var direction = into.Normal;
		var blind = depth > 0f;

		// Through holes are built long, and even a blind one starts a whisker proud of the surface.
		// A tool whose end cap sits exactly ON the face it enters gives the boolean two coplanar
		// faces to resolve, which is the case that produces slivers rather than a clean mouth.
		var overshoot = MathF.Max( through, diameter ) * 0.05f + 1e-3f;
		var length = blind ? depth + overshoot : MathF.Max( through, diameter ) * 2f;

		// Built about +Z at the origin, then carried to the face. Primitives.Cylinder centres itself
		// on the origin, so the shaft is shifted to start at the mouth and run inward.
		var shaft = Primitives.Cylinder( diameter * 0.5f, length, segments );
		MeshTransform.Apply( shaft, Xform.Translate( new Vec3( 0, 0, length * 0.5f - overshoot ) ) );

		var tool = shaft;

		switch ( style )
		{
			case HoleStyle.Counterbore:
			{
				if ( headDiameter <= diameter )
					throw new InvalidOperationException(
						$"A counterbore's head ({headDiameter}) has to be wider than its shaft ({diameter})." );

				if ( headDepth <= 0f )
					throw new InvalidOperationException( "A counterbore needs a head depth greater than zero." );

				var head = Primitives.Cylinder( headDiameter * 0.5f, headDepth + overshoot, segments );
				MeshTransform.Apply( head, Xform.Translate( new Vec3( 0, 0, (headDepth + overshoot) * 0.5f - overshoot ) ) );

				tool = Combine( shaft, head );
				break;
			}

			case HoleStyle.Countersink:
			{
				if ( headDiameter <= diameter )
					throw new InvalidOperationException(
						$"A countersink's head ({headDiameter}) has to be wider than its shaft ({diameter})." );

				var angle = Math.Clamp( sinkAngleDegrees, 1f, 179f );

				// The cone's depth follows from its two diameters and the included angle, which is
				// how a countersink is actually specified — you are given 90 degrees and a head size,
				// never a depth.
				var half = angle * 0.5f * MathF.PI / 180f;
				var sinkDepth = (headDiameter - diameter) * 0.5f / MathF.Tan( half );

				tool = Combine( shaft, Cone( headDiameter * 0.5f, diameter * 0.5f, sinkDepth, overshoot, segments ) );

				break;
			}
		}

		// +Z onto the drilling direction, then into place.
		MeshTransform.Apply( tool, Align( direction ) );
		MeshTransform.Apply( tool, Xform.Translate( at ) );

		return tool;
	}

	/// <summary>
	/// A truncated cone, wide end at the mouth. Built by hand rather than revolved: it is two rings
	/// and a wall, and going through a sketch and a revolve to get it would drag a profile, an axis
	/// and a winding question into something with four numbers in it.
	///
	/// THE OVERSHOOT CONTINUES THE TAPER RATHER THAN SITTING ON TOP OF IT. A countersink's head
	/// diameter is specified AT THE SURFACE, and the tool has to start proud of that surface so the
	/// boolean is not resolving two coplanar faces. Lifting the wide ring straight up would put the
	/// head diameter above the material and leave the hole narrower than asked for at the only place
	/// anybody measures it. So the ring above the surface is the cone extrapolated along its own
	/// slope, which is wider still and entirely outside the part.
	/// </summary>
	static PolyMesh Cone( float mouthRadius, float bottomRadius, float depth, float overshoot, int segments )
	{
		var mesh = new PolyMesh();
		var top = -overshoot;
		var bottom = depth;

		// r = mouthRadius at z = 0, bottomRadius at z = depth. Walked back to z = -overshoot.
		var slope = depth > 1e-9f ? (mouthRadius - bottomRadius) / depth : 0f;
		var topRadius = mouthRadius + overshoot * slope;

		for ( var i = 0; i < segments; i++ )
		{
			var a = i / (float)segments * MathF.PI * 2f;
			mesh.AddVertex( new Vec3( MathF.Cos( a ) * topRadius, MathF.Sin( a ) * topRadius, top ) );
		}

		for ( var i = 0; i < segments; i++ )
		{
			var a = i / (float)segments * MathF.PI * 2f;
			mesh.AddVertex( new Vec3( MathF.Cos( a ) * bottomRadius, MathF.Sin( a ) * bottomRadius, bottom ) );
		}

		// Wall quads, wound so the outside faces out — the winding is what a boolean reads to know
		// which side is solid, and getting it backwards makes the tool a void full of material.
		for ( var i = 0; i < segments; i++ )
		{
			var next = (i + 1) % segments;

			mesh.AddFace( new[] { i, next, segments + next, segments + i } );
		}

		var cap = new int[segments];
		var floor = new int[segments];

		for ( var i = 0; i < segments; i++ )
		{
			cap[i] = segments - 1 - i;
			floor[i] = segments + i;
		}

		mesh.AddFace( cap );
		mesh.AddFace( floor );

		return mesh;
	}

	/// <summary>
	/// Two tool solids as one mesh.
	///
	/// NOT A UNION, and it does not need to be. This is the TOOL, and a boolean subtract takes away
	/// everything inside it; two overlapping shafts sharing an interface still enclose exactly the
	/// void that should go. Running a real union here would mean needing a boolean provider to build
	/// the argument to the boolean.
	/// </summary>
	static PolyMesh Combine( PolyMesh a, PolyMesh b )
	{
		var result = a.Clone();
		var offset = result.VertexCount;

		foreach ( var p in b.Positions )
			result.Positions.Add( p );

		foreach ( var face in b.Faces )
		{
			var indices = new int[face.Count];

			for ( var i = 0; i < face.Count; i++ )
				indices[i] = face.Indices[i] + offset;

			result.AddFace( indices, (Vec2[])face.UVs.Clone(), face.Material );
		}

		return result;
	}

	/// <summary>The rotation taking +Z onto <paramref name="direction"/>.</summary>
	static Xform Align( Vec3 direction )
	{
		var z = new Vec3( 0, 0, 1 );
		var dot = Math.Clamp( Vec3.Dot( z, direction ), -1f, 1f );

		if ( dot > 0.999999f )
			return Xform.Identity;

		// Exactly opposite: the cross product is zero and gives no axis, so any perpendicular will do.
		if ( dot < -0.999999f )
			return Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI );

		return Xform.Rotate( Vec3.Cross( z, direction ).Normal, MathF.Acos( dot ) );
	}
}
