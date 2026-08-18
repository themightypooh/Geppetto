using System;
using System.Collections.Generic;

namespace ModelKernel;

/// <summary>
/// Turning a sketch into a solid — extrude and revolve.
///
/// This is the other way into the modeller. Primitives cover the shapes you can describe with three
/// numbers; a sketch covers everything else, and "draw the outline, pull it up" is the move every
/// CAD package opens with because it is the one people already understand.
///
/// QUAD WALLS, ALWAYS. Every side face is a quad by construction, which is what the subdivision
/// stage needs. The caps are n-gons — PolyMesh holds those natively and Catmull-Clark turns an
/// n-gon into n quads on the first level, so a cap costs nothing extra and is not triangulated on
/// the way in.
///
/// UVs ARE REAL, NOT PLACEHOLDER. Walls get arc length along the profile against height, so a
/// texture runs around the outside without stretching at the corners. Caps reuse the sketch's own
/// 2D coordinates, which is a planar projection that is exactly right for a flat face. These are
/// the UVs the eventual normal-map bake depends on, so they have to mean something now.
/// </summary>
public static class SketchSolids
{
	/// <summary>
	/// Sweep a profile along its plane's normal.
	///
	/// `symmetric` splits the distance either side of the sketch plane, which is what you want when
	/// the sketch sits on a mirror plane — the usual case for anything symmetrical.
	/// </summary>
	public static PolyMesh Extrude( Sketch sketch, float distance, bool symmetric = false, int material = 0 )
	{
		if ( sketch is null )
			throw new ArgumentNullException( nameof( sketch ) );

		var errors = sketch.Outer.Validate();

		if ( errors.Count > 0 )
			throw new InvalidOperationException( "Cannot extrude: " + string.Join( "; ", errors ) );

		if ( MathF.Abs( distance ) < 1e-6f )
			throw new InvalidOperationException( "Extrude distance is zero" );

		// A profile drawn clockwise is not a mistake, so the winding is normalised here rather than
		// demanded of the caller. Without this the solid comes out inside-out.
		var profile = sketch.Outer.AsCounterClockwise();

		// A negative distance is just the other direction; fold the sign into the offsets so the
		// face winding logic below only ever deals with "low ring, then high ring".
		var low = symmetric ? -MathF.Abs( distance ) * 0.5f : MathF.Min( 0f, distance );
		var high = symmetric ? MathF.Abs( distance ) * 0.5f : MathF.Max( 0f, distance );

		var plane = sketch.Plane;
		var normal = plane.Normal;
		var n = profile.Count;

		var mesh = new PolyMesh();

		for ( var i = 0; i < n; i++ )
			mesh.AddVertex( plane.ToWorld( profile.Points[i] ) + normal * low );

		for ( var i = 0; i < n; i++ )
			mesh.AddVertex( plane.ToWorld( profile.Points[i] ) + normal * high );

		var perimeter = CumulativeLengths( profile, out var totalLength );
		var height = high - low;

		// --- walls ---------------------------------------------------------------------
		for ( var i = 0; i < n; i++ )
		{
			var j = (i + 1) % n;

			// [low_i, low_j, high_j, high_i] winds so the face normal points away from the solid:
			// the edge cross the sweep direction is the outward direction for a CCW profile.
			var u0 = perimeter[i] / totalLength;
			var u1 = (i == n - 1 ? totalLength : perimeter[j]) / totalLength;

			mesh.AddFace(
				new[] { i, j, n + j, n + i },
				new[]
				{
					new Vec2( u0, 0f ),
					new Vec2( u1, 0f ),
					new Vec2( u1, height ),
					new Vec2( u0, height )
				},
				material );
		}

		// --- caps ----------------------------------------------------------------------
		// The low cap faces backwards along the normal, so its corners run the other way round.
		var lowIndices = new int[n];
		var lowUVs = new Vec2[n];

		for ( var i = 0; i < n; i++ )
		{
			lowIndices[i] = n - 1 - i;
			lowUVs[i] = profile.Points[n - 1 - i];
		}

		mesh.AddFace( lowIndices, lowUVs, material );

		var highIndices = new int[n];
		var highUVs = new Vec2[n];

		for ( var i = 0; i < n; i++ )
		{
			highIndices[i] = n + i;
			highUVs[i] = profile.Points[i];
		}

		mesh.AddFace( highIndices, highUVs, material );

		return mesh;
	}

	/// <summary>
	/// Spin a profile around an axis lying in the sketch plane — how you make anything turned on a
	/// lathe: a bottle, a chess piece, a pipe fitting, a lamp base.
	///
	/// The axis is given in SKETCH coordinates, because that is where you can see it. A full 360°
	/// revolve closes back on itself and produces a closed solid; a partial one leaves the two end
	/// caps open, which is correct — a half-revolve of a closed profile is a half-open shape, and
	/// silently capping it would be inventing geometry the user did not ask for.
	/// </summary>
	public static PolyMesh Revolve( Sketch sketch, Vec2 axisPoint, Vec2 axisDirection, float degrees = 360f, int segments = 16, int material = 0 )
	{
		if ( sketch is null )
			throw new ArgumentNullException( nameof( sketch ) );

		var errors = sketch.Outer.Validate();

		if ( errors.Count > 0 )
			throw new InvalidOperationException( "Cannot revolve: " + string.Join( "; ", errors ) );

		if ( axisDirection.LengthSquared < 1e-12f )
			throw new InvalidOperationException( "Revolve axis has no direction" );

		segments = Math.Max( 3, segments );

		var full = MathF.Abs( degrees ) >= 359.999f;
		var profile = sketch.Outer.AsCounterClockwise();
		var n = profile.Count;

		var plane = sketch.Plane;
		var worldAxisPoint = plane.ToWorld( axisPoint );
		var worldAxis = (plane.U * axisDirection.x + plane.V * axisDirection.y).Normal;

		// A profile crossing the axis would sweep through itself and produce inverted geometry, so
		// it is refused rather than quietly producing a mess.
		var side = 0;

		foreach ( var p in profile.Points )
		{
			var d = Vec2.Cross( axisDirection, p - axisPoint );

			if ( MathF.Abs( d ) < 1e-6f )
				continue;

			var s = d > 0f ? 1 : -1;

			if ( side == 0 ) side = s;
			else if ( s != side )
				throw new InvalidOperationException( "The profile crosses the revolve axis — it must sit wholly on one side" );
		}

		var mesh = new PolyMesh();
		var ringCount = full ? segments : segments + 1;
		var step = degrees * MathF.PI / 180f / segments;

		// WHICH WAY THE SWEEP GOES DECIDES THE WINDING, and unlike an extrude it is not knowable up
		// front: it depends on the axis, on which side of it the profile sits, and on the sign of
		// the angle. Sweeping against the profile's normal produces a solid that is inside-out —
		// topologically perfect, every Euler check passing, and rendered as a black hole.
		//
		// So measure it once. The sweep direction at a point is the rotation's tangent there; if
		// that runs against the plane normal, every face below is wound the other way round.
		var centroid = Vec2.Zero;

		foreach ( var p in profile.Points )
			centroid += p;

		centroid /= profile.Count;

		var sweep = Vec3.Cross( worldAxis, plane.ToWorld( centroid ) - worldAxisPoint );
		var flip = Vec3.Dot( sweep, plane.Normal ) * (degrees < 0f ? -1f : 1f) < 0f;

		for ( var r = 0; r < ringCount; r++ )
		{
			var rot = Xform.RotateAbout( worldAxisPoint, worldAxis, step * r );

			foreach ( var p in profile.Points )
				mesh.AddVertex( rot.TransformPoint( plane.ToWorld( p ) ) );
		}

		var perimeter = CumulativeLengths( profile, out var totalLength );

		for ( var r = 0; r < segments; r++ )
		{
			var ringA = r * n;
			var ringB = ((r + 1) % ringCount) * n;

			var v0 = r / (float)segments;
			var v1 = (r + 1) / (float)segments;

			for ( var i = 0; i < n; i++ )
			{
				var j = (i + 1) % n;
				var u0 = perimeter[i] / totalLength;
				var u1 = (i == n - 1 ? totalLength : perimeter[j]) / totalLength;

				mesh.AddFace(
					flip
						? new[] { ringA + i, ringB + i, ringB + j, ringA + j }
						: new[] { ringA + i, ringA + j, ringB + j, ringB + i },
					flip
						? new[] { new Vec2( u0, v0 ), new Vec2( u0, v1 ), new Vec2( u1, v1 ), new Vec2( u1, v0 ) }
						: new[] { new Vec2( u0, v0 ), new Vec2( u1, v0 ), new Vec2( u1, v1 ), new Vec2( u0, v1 ) },
					material );
			}
		}

		// A partial revolve needs its two ends closed, or it is a shell rather than a solid.
		if ( !full )
		{
			var endBase = (ringCount - 1) * n;

			// The first ring faces backwards along the sweep and the last faces forwards, so they
			// are wound opposite each other — and both invert with `flip`, exactly like the walls.
			AddCap( mesh, profile, 0, !flip, material );
			AddCap( mesh, profile, endBase, flip, material );
		}

		return mesh;
	}

	/// <summary>One end cap. `reversed` winds it the other way round, which is what makes the two
	/// ends of a sweep face away from each other instead of both the same way.</summary>
	static void AddCap( PolyMesh mesh, Profile profile, int baseIndex, bool reversed, int material )
	{
		var n = profile.Count;
		var indices = new int[n];
		var uvs = new Vec2[n];

		for ( var i = 0; i < n; i++ )
		{
			var source = reversed ? n - 1 - i : i;
			indices[i] = baseIndex + source;
			uvs[i] = profile.Points[source];
		}

		mesh.AddFace( indices, uvs, material );
	}

	/// <summary>Distance along the profile to each point, for wall UVs that do not stretch at
	/// unevenly spaced corners.</summary>
	static float[] CumulativeLengths( Profile profile, out float total )
	{
		var n = profile.Count;
		var lengths = new float[n];
		var running = 0f;

		for ( var i = 0; i < n; i++ )
		{
			lengths[i] = running;
			running += (profile.Points[(i + 1) % n] - profile.Points[i]).Length;
		}

		total = running > 1e-9f ? running : 1f;
		return lengths;
	}
}
