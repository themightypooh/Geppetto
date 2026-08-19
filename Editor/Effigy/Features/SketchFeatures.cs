using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Holds a sketch and publishes it for later features to consume. Produces no geometry itself,
/// exactly like Onshape's Sketch feature.
///
/// Downstream features reference this by feature Id rather than holding the Sketch object, so
/// editing the sketch and rebuilding flows through automatically — there is no second reference to
/// keep in step.
/// </summary>
public sealed class SketchFeature : Feature
{
	public override string TypeName => "Sketch";

	public Sketch Sketch = new();

	public readonly ChoiceParam Plane = new( "Plane", new[] { "Top (XY)", "Front (XZ)", "Right (YZ)" } );
	public readonly FloatParam PlaneOffset = new( "Offset", 0f, unit: "u" );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Plane, PlaneOffset };

	protected override void Execute( FeatureContext ctx )
	{
		var basePlane = Plane.Index switch
		{
			0 => SketchPlane.XY,
			1 => SketchPlane.XZ,
			2 => SketchPlane.YZ,
			_ => SketchPlane.XY
		};

		Sketch.Plane = PlaneOffset.Value == 0f ? basePlane : basePlane.Offset( PlaneOffset.Value );
		ctx.Sketches[Id] = Sketch;
	}
}

/// <summary>Shared plumbing for the features that turn a sketch profile into a solid.</summary>
public abstract class SketchConsumingFeature : Feature
{
	public readonly ChoiceParam Sketch = new( "Sketch", new[] { "" } );

	/// <summary>Feature id of the SketchFeature to consume. Empty means "the most recent one",
	/// which is what you want while there is only one sketch in the tree.</summary>
	public string SketchFeatureId = "";

	/// <summary>
	/// Which closed region of the sketch to build from, as a point inside it in plane coordinates.
	/// Null means every region, which is the old behaviour and stays the default.
	///
	/// A POINT RATHER THAN AN INDEX, deliberately. Profiles have no identity of their own — they
	/// are re-found from the curve graph on every rebuild, and their order is whatever order the
	/// walk happens to discover them in. "Region 2" would silently come to mean a different face
	/// the moment a curve was added upstream. A point inside the region is stable under every edit
	/// that does not destroy the region itself, and it is also exactly what a click in the viewport
	/// already gives us.
	/// </summary>
	public Vec2? RegionSeed;

	protected Sketch ResolveSketch( FeatureContext ctx )
	{
		if ( ctx.Sketches.Count == 0 )
			throw new InvalidOperationException( "There is no sketch to use — add a Sketch feature first" );

		if ( string.IsNullOrEmpty( SketchFeatureId ) )
			return ctx.Sketches.Values.Last();

		if ( !ctx.Sketches.TryGetValue( SketchFeatureId, out var sketch ) )
			throw new InvalidOperationException( $"Sketch '{SketchFeatureId}' is not available at this point in the tree" );

		return sketch;
	}

	/// <summary>
	/// The regions this feature builds from: every closed region in the sketch, or just the one
	/// under RegionSeed when a face has been picked.
	///
	/// Instance rather than static because of the seed. A missing seed region throws by name
	/// rather than falling back to "all regions" — silently extruding the whole sketch because the
	/// face you picked stopped existing is exactly the kind of thing you notice three features
	/// later.
	/// </summary>
	protected List<Profile> ResolveProfiles( Sketch sketch )
	{
		var all = FindProfiles( sketch );

		if ( RegionSeed is not { } seed )
			return all;

		var picked = all.Where( p => p.Contains( seed ) ).ToList();

		if ( picked.Count == 0 )
		{
			throw new InvalidOperationException(
				"The selected region no longer exists — the sketch changed underneath it. "
				+ "Pick a region again, or clear the selection to use every closed region." );
		}

		return picked;
	}

	static List<Profile> FindProfiles( Sketch sketch )
	{
		var found = ProfileFinder.Find( sketch );

		// ProfileFinder reports what it could not make sense of - a point where three curves meet,
		// for instance, which it will not guess at. Discarding those left a branching sketch
		// extruding one arbitrary sub-loop and looking like it had worked. If nothing closed at all
		// the message below is better; otherwise the warning is the most useful thing available.
		if ( found.Warnings.Count > 0 && found.Profiles.Count > 0 )
			throw new InvalidOperationException( string.Join( "; ", found.Warnings ) );

		if ( found.Profiles.Count == 0 )
		{
			throw new InvalidOperationException( found.OpenChains > 0
				? "The sketch has no closed region — its curves do not join up"
				: "The sketch has no closed region" );
		}

		// Holes need the cap to be triangulated around them, which is really the same problem as
		// a boolean subtract and is better solved once, there. Until then this refuses clearly
		// rather than producing a cap that renders with the hole filled in.
		var holed = found.Profiles.FirstOrDefault( p => p.HasHoles );

		if ( holed is not null )
		{
			throw new InvalidOperationException(
				$"Profiles with holes are not supported yet ({holed.Holes.Count} inner loop(s)). "
				+ "Use the Tube primitive, or extrude the outer and inner shapes separately." );
		}

		return found.Profiles;
	}
}

/// <summary>
/// Extrudes sketch profiles into solids along the sketch plane's normal. Onshape's Extrude.
///
/// Caps are emitted as single n-gons rather than triangle fans. Catmull-Clark turns an n-gon into
/// n clean quads, so a hexagonal boss subdivides properly; a fan would leave a high-valence hub in
/// the middle of every face that puckers the moment anyone sculpts near it.
/// </summary>
public sealed class ExtrudeFeature : SketchConsumingFeature
{
	public override string TypeName => "Extrude";

	public readonly FloatParam Distance = new( "Distance", 1f, unit: "u" );
	public readonly BoolParam Symmetric = new( "Symmetric", false );
	public readonly BoolParam Flip = new( "Flip direction", false );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Sketch, Distance, Symmetric, Flip, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var sketch = ResolveSketch( ctx );
		var profiles = ResolveProfiles( sketch );

		if ( MathF.Abs( Distance.Value ) < 1e-6f )
			throw new InvalidOperationException( "Distance cannot be zero" );

		var distance = Flip.Value ? -Distance.Value : Distance.Value;
		var near = Symmetric.Value ? -distance * 0.5f : 0f;
		var far = Symmetric.Value ? distance * 0.5f : distance;

		foreach ( var profile in profiles )
		{
			var mesh = BuildPrism( sketch.Plane, profile.Outer, near, far, Material.Clamped );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
		}
	}

	/// <summary>
	/// Outer loop arrives counter-clockwise in plane coordinates, which fixes every winding
	/// question: the far cap keeps that order, the near cap reverses, and the side quads run
	/// bottom edge then up. Verified by the enclosed-volume test rather than by inspection.
	/// </summary>
	static PolyMesh BuildPrism( SketchPlane plane, List<Vec2> loop, float near, float far, int material )
	{
		var n = loop.Count;
		var mesh = new PolyMesh();

		// A negative extrusion puts the "far" cap behind the "near" one and flips the solid inside
		// out. Ordering them here means the rest of the function never has to think about sign.
		var (low, high) = near <= far ? (near, far) : (far, near);

		foreach ( var p in loop )
			mesh.AddVertex( plane.ToWorld( p ) + plane.Normal * low );

		foreach ( var p in loop )
			mesh.AddVertex( plane.ToWorld( p ) + plane.Normal * high );

		// Side walls. Cumulative perimeter drives U so the texture does not stretch on long edges.
		var perimeter = 0f;
		var distances = new float[n + 1];

		for ( var i = 0; i < n; i++ )
		{
			var a = loop[i];
			var b = loop[(i + 1) % n];
			distances[i] = perimeter;
			perimeter += MathF.Sqrt( (b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y) );
		}

		distances[n] = perimeter;

		for ( var i = 0; i < n; i++ )
		{
			var j = (i + 1) % n;
			var u0 = perimeter > 0f ? distances[i] / perimeter : 0f;
			var u1 = perimeter > 0f ? distances[i + 1] / perimeter : 1f;

			mesh.AddFace(
				new[] { i, j, n + j, n + i },
				new[] { new Vec2( u0, 0 ), new Vec2( u1, 0 ), new Vec2( u1, 1 ), new Vec2( u0, 1 ) },
				material );
		}

		// Caps use plane coordinates directly as UVs, so a face keeps the proportions it was drawn
		// with instead of being squashed into a unit square.
		var topIndices = new int[n];
		var topUVs = new Vec2[n];

		for ( var i = 0; i < n; i++ )
		{
			topIndices[i] = n + i;
			topUVs[i] = loop[i];
		}

		mesh.AddFace( topIndices, topUVs, material );

		var bottomIndices = new int[n];
		var bottomUVs = new Vec2[n];

		for ( var i = 0; i < n; i++ )
		{
			bottomIndices[i] = n - 1 - i;
			bottomUVs[i] = loop[n - 1 - i];
		}

		mesh.AddFace( bottomIndices, bottomUVs, material );

		return mesh;
	}
}

/// <summary>
/// Revolves sketch profiles about an axis lying in the sketch plane. Onshape's Revolve.
///
/// Points sitting ON the axis are the awkward case — every revolved copy of them lands in the same
/// place. Rather than special-casing that, construction runs through the vertex welder, so those
/// copies collapse to one vertex, the quad next to them degenerates to a triangle, and a profile
/// touching the axis produces a proper closed solid. A full revolution closes the same way: the
/// last ring welds onto the first.
/// </summary>
public sealed class RevolveFeature : SketchConsumingFeature
{
	public override string TypeName => "Revolve";

	public readonly Vec3Param AxisPoint = new( "Axis through (sketch coords)", Vec3.Zero );
	public readonly Vec3Param AxisDirection = new( "Axis direction (sketch coords)", new Vec3( 1, 0, 0 ) );
	public readonly FloatParam Angle = new( "Angle", 360f, unit: "deg" );
	public readonly IntParam Segments = new( "Segments", 24, 3, 512 );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Sketch, AxisPoint, AxisDirection, Angle, Segments, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var sketch = ResolveSketch( ctx );
		var profiles = ResolveProfiles( sketch );

		// PAST A FULL TURN THE SWEEP OVERLAPS ITSELF, and the overlap welds: the result comes back
		// with edges shared by four or more faces and no error reported. Only exactly +-360 is
		// treated as a full revolution, so 720 was never going to mean "twice round" - it meant
		// "a broken mesh, quietly".
		if ( MathF.Abs( Angle.Value ) > 360f + 1e-3f )
			throw new InvalidOperationException(
				$"A revolve cannot exceed a full turn ({Angle.Value} degrees) — past 360 the sweep passes through itself." );

		if ( MathF.Abs( Angle.Value ) < 1e-4f )
			throw new InvalidOperationException( "Angle cannot be zero" );

		var plane = sketch.Plane;

		// The axis is authored in sketch coordinates and lifted into world space, so it moves with
		// the plane like everything else in the sketch.
		var axisOrigin = plane.ToWorld( new Vec2( AxisPoint.Value.x, AxisPoint.Value.y ) );
		var axisDir = plane.XAxis * AxisDirection.Value.x + plane.YAxis * AxisDirection.Value.y;

		if ( axisDir.LengthSquared < 1e-12f )
			throw new InvalidOperationException( "Axis direction cannot be zero" );

		var full = MathF.Abs( MathF.Abs( Angle.Value ) - 360f ) < 1e-3f;

		foreach ( var profile in profiles )
		{
			RejectIfCrossingAxis( profile.Outer );

			var mesh = BuildRevolve( plane, profile.Outer, axisOrigin, axisDir,
				Angle.Value, Segments.Clamped, full, Material.Clamped );

			OrientOutward( mesh );

			ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
		}
	}

	/// <summary>
	/// A profile straddling the axis is rejected, the way Onshape rejects it.
	///
	/// It is not merely unsupported, it is meaningless: each half sweeps the same solid, so every
	/// face is generated twice with opposite winding. The result passes a casual look, encloses
	/// zero volume, and welds vertices that should have stayed apart. Catching it here gives the
	/// user the real reason instead of a mesh that is quietly nonsense.
	/// </summary>
	void RejectIfCrossingAxis( List<Vec2> loop )
	{
		var a = new Vec2( AxisPoint.Value.x, AxisPoint.Value.y );
		var d = new Vec2( AxisDirection.Value.x, AxisDirection.Value.y );
		var length = MathF.Sqrt( d.x * d.x + d.y * d.y );

		if ( length < 1e-9f )
			return;

		var minSide = float.MaxValue;
		var maxSide = float.MinValue;

		foreach ( var p in loop )
		{
			// 2D cross product: signed perpendicular distance from the axis line.
			var side = (d.x * (p.y - a.y) - d.y * (p.x - a.x)) / length;
			minSide = MathF.Min( minSide, side );
			maxSide = MathF.Max( maxSide, side );
		}

		const float eps = 1e-5f;

		if ( minSide < -eps && maxSide > eps )
		{
			throw new InvalidOperationException(
				"The profile crosses the axis of revolution. Move it fully to one side of the axis." );
		}
	}

	/// <summary>
	/// Flip every face if the finished solid encloses negative volume.
	///
	/// Whether a sweep comes out inside-out depends on the axis direction, the sign of the angle,
	/// and which side of the axis the profile sits on. Enumerating those cases invites getting one
	/// of them wrong silently; measuring the result instead is one cheap pass and is correct for
	/// all of them. Safe because a revolve is always closed — wrapped when full, capped when not.
	/// </summary>
	static void OrientOutward( PolyMesh mesh )
	{
		var volume = 0f;

		foreach ( var f in mesh.Faces )
			volume += Vec3.Dot( mesh.FaceCentroid( f ), mesh.FaceNormal( f ) ) * mesh.FaceArea( f );

		if ( volume >= 0f )
			return;

		foreach ( var f in mesh.Faces )
		{
			Array.Reverse( f.Indices );
			Array.Reverse( f.UVs );
		}
	}

	static PolyMesh BuildRevolve(
		SketchPlane plane, List<Vec2> loop, Vec3 axisOrigin, Vec3 axisDir,
		float angleDegrees, int segments, bool full, int material )
	{
		var n = loop.Count;
		var mesh = new PolyMesh();
		var weld = new VertexWelder( mesh );

		var rings = full ? segments : segments;
		var step = angleDegrees / segments * MathF.PI / 180f;

		// ring[k][i] is loop point i rotated by k steps. A full turn reuses ring 0 as the last
		// ring, which the welder achieves on its own by landing on identical positions.
		var ring = new int[rings + 1][];

		for ( var k = 0; k <= rings; k++ )
		{
			ring[k] = new int[n];
			var xform = Xform.RotateAbout( axisOrigin, axisDir, step * k );

			for ( var i = 0; i < n; i++ )
				ring[k][i] = weld.Add( xform.TransformPoint( plane.ToWorld( loop[i] ) ) );
		}

		for ( var k = 0; k < rings; k++ )
		{
			for ( var i = 0; i < n; i++ )
			{
				var j = (i + 1) % n;

				var quad = new[] { ring[k][i], ring[k][j], ring[k + 1][j], ring[k + 1][i] };

				var uvs = new[]
				{
					new Vec2( k / (float)rings, i / (float)n ),
					new Vec2( k / (float)rings, (i + 1) / (float)n ),
					new Vec2( (k + 1) / (float)rings, (i + 1) / (float)n ),
					new Vec2( (k + 1) / (float)rings, i / (float)n )
				};

				AddNonDegenerate( mesh, quad, uvs, material );
			}
		}

		// A partial revolution is open at both ends and needs capping; a full one is already
		// closed, and adding caps would leave two faces buried inside the solid.
		if ( !full )
		{
			var startCap = new int[n];
			var startUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				startCap[i] = ring[0][n - 1 - i];
				startUVs[i] = loop[n - 1 - i];
			}

			AddNonDegenerate( mesh, startCap, startUVs, material );

			var endCap = new int[n];
			var endUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				endCap[i] = ring[rings][i];
				endUVs[i] = loop[i];
			}

			AddNonDegenerate( mesh, endCap, endUVs, material );
		}

		return mesh;
	}

	/// <summary>
	/// Add a face, dropping repeated indices first and skipping it entirely if fewer than three
	/// remain.
	///
	/// This is what makes a profile touching the axis work. Those points weld to a single vertex,
	/// so the quad beside them arrives as (a, a, b, c) — collapsing it gives the triangle the
	/// geometry actually wants, and a fully degenerate face disappears instead of becoming a
	/// zero-area sliver that breaks normals downstream.
	/// </summary>
	static void AddNonDegenerate( PolyMesh mesh, int[] indices, Vec2[] uvs, int material )
	{
		var keptIndices = new List<int>( indices.Length );
		var keptUVs = new List<Vec2>( indices.Length );

		for ( var i = 0; i < indices.Length; i++ )
		{
			// Compare against the previous kept corner, and wrap for the last one, so runs of
			// duplicates collapse whether they are adjacent or straddle the end of the face.
			if ( keptIndices.Count > 0 && keptIndices[^1] == indices[i] )
				continue;

			keptIndices.Add( indices[i] );
			keptUVs.Add( uvs[i] );
		}

		while ( keptIndices.Count > 1 && keptIndices[0] == keptIndices[^1] )
		{
			keptIndices.RemoveAt( keptIndices.Count - 1 );
			keptUVs.RemoveAt( keptUVs.Count - 1 );
		}

		if ( keptIndices.Count < 3 )
			return;

		mesh.AddFace( keptIndices.ToArray(), keptUVs.ToArray(), material );
	}
}
