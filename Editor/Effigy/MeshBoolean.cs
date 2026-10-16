using System;

namespace Effigy;

/// <summary>What a boolean does to the two meshes.</summary>
public enum BooleanOp
{
	/// <summary>Everything inside either one, with the shared interface cut away. Different from
	/// MeshTransform.Append, which combines the meshes and leaves the interface in place.</summary>
	Union,

	/// <summary>The target with the tool's volume taken out of it. This is a cut.</summary>
	Subtract,

	/// <summary>Only the volume both share.</summary>
	Intersect,
}

/// <summary>
/// Something that can actually perform a mesh boolean.
///
/// AN INTERFACE RATHER THAN AN IMPLEMENTATION, and that is a decision this repo made a while ago
/// and wrote down: robust mesh CSG is a decades-old problem — coplanar faces, floating-point
/// robustness, self-intersection — and a half-working one is worse than none, because it fails on
/// the interesting cases and does so by producing a mesh rather than an error. s&amp;box ships
/// PolygonMesh.PerformBoolean, so the plan of record is an engine-backed implementation there, and
/// a portable one only if it is ever genuinely needed.
///
/// The kernel therefore knows what a boolean IS without knowing how to do one. That keeps the
/// engine-free promise intact — nothing in here references an engine type — while letting a cut
/// work wherever a provider has been installed.
/// </summary>
public interface IMeshBoolean
{
	/// <summary>
	/// Apply the operation, or explain why not.
	///
	/// Returning false with a reason rather than throwing, because "this pair of meshes cannot be
	/// booleaned" is an ordinary outcome — two solids that do not touch, a cut that would remove
	/// everything — and the feature turns the reason into its own error message. A provider that
	/// throws is caught and treated the same way, since an engine call failing is not something a
	/// rebuild should die on.
	/// </summary>
	bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error );
}

/// <summary>
/// Where the boolean provider is installed, and the one place features go through to use it.
///
/// A static slot rather than something threaded through FeatureContext: there is exactly one
/// answer per process — the engine's, or none — and the alternative is every feature signature
/// carrying a parameter that is the same value every time.
/// </summary>
public static class MeshBoolean
{
	/// <summary>The installed provider, or null where there is none — a bare console runner, or
	/// the test project. Set once at startup by whatever host knows how to do a boolean.</summary>
	public static IMeshBoolean Provider { get; set; }

	public static bool Available => Provider is not null;

	/// <summary>
	/// What a host wants said when there is no provider, if it knows something more useful than the
	/// kernel does.
	///
	/// The kernel's own message can only say that no boolean is installed, because that is all it
	/// knows. A host knows more — the editor knows the engine HAS one and what is needed to reach
	/// it — and the difference between "unavailable" and "unavailable, here is the next step" is
	/// the difference between a dead end and a task.
	/// </summary>
	public static string UnavailableReason { get; set; }

	/// <summary>
	/// Apply a boolean, throwing with something a user can act on if it cannot be done.
	///
	/// Every failure here ends up as one feature's Error, which is what the dialog turns red over
	/// and what the user reads. So each message says what could not be done AND what to do about it
	/// — "unavailable" on its own leaves someone wondering whether they broke something.
	/// </summary>
	public static PolyMesh Apply( BooleanOp op, PolyMesh target, PolyMesh tool )
	{
		if ( target is null || tool is null )
		{
			throw new FeatureException( new FeatureDiagnostic(
				DiagnosticSeverity.Error,
				"A boolean needs two solids",
				"One of the inputs was missing, so there is nothing to combine.",
				remedies: new[] { "Make sure both the target body and the tool solid exist" } ) );
		}

		if ( Provider is null )
		{
			var problem = UnavailableReason is { Length: > 0 } reason
				? $"{Name( op )} needs a mesh boolean. {reason}"
				: $"{Name( op )} needs a mesh boolean, and none is installed in this build. The kernel does "
					+ "not carry its own — see MeshBoolean for why.";

			throw new FeatureException( new FeatureDiagnostic(
				DiagnosticSeverity.Error,
				problem,
				"This build has no boolean provider, so cuts and unions cannot run.",
				remedies: new[] { "Run this in the s&box editor, where the engine boolean is installed",
					"Or, for the Boolean feature, set Method to Auto or Voxel: the kernel's voxel boolean runs anywhere" } ) );
		}

		bool ok;
		PolyMesh result;
		string error;

		try
		{
			ok = Provider.TryApply( op, target, tool, out result, out error );
		}
		catch ( Exception e )
		{
			// An engine call throwing is a failed boolean, not a failed rebuild. Everything else in
			// the tree should still build and still be on screen while this one feature complains.
			throw DiagnoseBoolean( op, target, tool, e.Message, engineThrew: true );
		}

		if ( !ok )
			throw DiagnoseBoolean( op, target, tool, error, engineThrew: false );

		if ( result is null || result.FaceCount == 0 )
		{
			// An empty result is a real answer to some inputs — cutting a solid with something that
			// swallows it whole — and it is never a useful one, because a body with no faces is
			// indistinguishable from a broken feature everywhere downstream.
			throw new FeatureException( new FeatureDiagnostic(
				DiagnosticSeverity.Error,
				$"{Name( op )} left nothing behind. The cut probably covers the whole part — check the profile and the distance.",
				"The boolean returned a mesh with no faces, which would blank every feature below this one.",
				remedies: new[] { "Reduce the distance so the cut does not swallow the part", "Check the profile sits inside the body" } ) );
		}

		return result;
	}

	/// <summary>
	/// The engine's refusal is a dead end on its own. Before handing it up, say whether the two
	/// bodies even overlap, and whether either is open — those are things we can see without
	/// asking the engine, and they are the usual reasons it says no.
	/// </summary>
	static FeatureException DiagnoseBoolean( BooleanOp op, PolyMesh target, PolyMesh tool, string error, bool engineThrew )
	{
		var targetCheck = MeshValidator.Validate( target );
		var toolCheck = MeshValidator.Validate( tool );
		var targetOpen = !targetCheck.IsClosed;
		var toolOpen = !toolCheck.IsClosed;

		if ( targetOpen || toolOpen )
		{
			var which = targetOpen && toolOpen ? "both solids are open"
				: targetOpen ? $"the target has {targetCheck.BoundaryEdges} boundary edge(s)"
				: $"the tool has {toolCheck.BoundaryEdges} boundary edge(s)";

			return new FeatureException( new FeatureDiagnostic(
				DiagnosticSeverity.Error,
				$"{Name( op )} failed: one of the solids is not closed",
				$"{which}, so there is no inside to {Name( op ).ToLowerInvariant()}. {error ?? ""}".Trim(),
				remedies: new[] { "Close the open mesh first", "Avoid cutting with a surface that has a boundary" } ) );
		}

		if ( BoundsGap( target, tool, out var axis, out var gap ) )
		{
			var how = gap > 1e-6f
				? $"they miss each other by {gap:0.###} along {axis}"
				: $"they only touch along {axis}, enclosing no common volume";

			return new FeatureException( new FeatureDiagnostic(
				DiagnosticSeverity.Error,
				$"{Name( op )} failed: the two solids do not overlap",
				$"{how}. {error ?? ""}".Trim(),
				remedies: new[] { "Move the tool so it cuts into the part", "Increase the distance", "Check Flip direction" } ) );
		}

		return new FeatureException( new FeatureDiagnostic(
			DiagnosticSeverity.Error,
			$"{Name( op )} failed: {error ?? "the solids could not be combined"}",
			engineThrew
				? "The boolean engine threw rather than returning a mesh."
				: "The solids overlap and are closed, so the refusal is in the geometry — self-intersection, or a case the engine cannot cut.",
			remedies: new[] { "Check neither solid self-intersects", "Try a slightly different position or distance" } ) );
	}

	static bool BoundsGap( PolyMesh a, PolyMesh b, out string axis, out float gap )
	{
		axis = "X";
		gap = 0f;

		if ( a.VertexCount == 0 || b.VertexCount == 0 )
			return false;

		Extent( a, out var aMin, out var aMax );
		Extent( b, out var bMin, out var bMax );

		var gapX = SpanGap( aMin.x, aMax.x, bMin.x, bMax.x );
		var gapY = SpanGap( aMin.y, aMax.y, bMin.y, bMax.y );
		var gapZ = SpanGap( aMin.z, aMax.z, bMin.z, bMax.z );
		var worst = MathF.Max( gapX, MathF.Max( gapY, gapZ ) );

		if ( worst < -1e-6f )
			return false;

		axis = worst == gapX ? "X" : worst == gapY ? "Y" : "Z";
		gap = worst;
		return true;
	}

	static float SpanGap( float aMin, float aMax, float bMin, float bMax ) =>
		MathF.Max( bMin - aMax, aMin - bMax );

	static void Extent( PolyMesh mesh, out Vec3 min, out Vec3 max )
	{
		min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
	}

	static string Name( BooleanOp op ) => op switch
	{
		BooleanOp.Union => "Union",
		BooleanOp.Subtract => "Remove",
		BooleanOp.Intersect => "Intersect",
		_ => "Boolean"
	};
}

/// <summary>
/// A boolean the kernel CAN do on its own: both solids become distance fields, the fields are
/// combined (min for union, max for intersect, max with the negated tool for subtract) and a new
/// skin is meshed over the result with surface nets.
///
/// NOT THE EXACT CSG MeshBoolean's header refuses to half-write, and it does not pretend to be.
/// The result is a fresh voxel skin: sharp edges come back rounded by about a voxel, UVs and skin
/// weights do not come across, and the face count follows the voxel size rather than the inputs.
/// For organic work - a horn into a skull, a socket out of a jaw - that is what you wanted anyway
/// (it is Voxel remesh with a sign flip). For hard-surface parts the engine provider is still the
/// right tool, which is why BooleanFeature's Auto method prefers it wherever one is installed.
///
/// What makes it worth having: it runs anywhere the kernel runs - the CLI, the MCP, the test suite -
/// so a build script that says `add Boolean` no longer dies outside the editor.
/// </summary>
public static class VoxelBoolean
{
	/// <summary>The most voxels one boolean will sample. Above this the caller is told the voxel
	/// that fits rather than left waiting.</summary>
	public const long MaxVoxels = 30_000_000;

	/// <summary>Voxels across the longest side when the caller leaves the size to us.</summary>
	public const int AutoResolution = 96;

	/// <summary>The voxel size Auto would pick for these two solids.</summary>
	public static float AutoVoxel( BooleanOp op, PolyMesh target, PolyMesh tool )
	{
		var (min, max) = Region( op, target, tool );
		var size = max - min;
		var longest = MathF.Max( size.x, MathF.Max( size.y, size.z ) );
		var voxel = MathF.Max( longest / AutoResolution, 1e-4f );

		// Never over budget, whatever the proportions.
		var fits = MathF.Cbrt( MathF.Max( size.x, voxel ) * MathF.Max( size.y, voxel ) * MathF.Max( size.z, voxel ) / (MaxVoxels * 0.5f) );
		return MathF.Max( voxel, fits );
	}

	/// <summary>Apply <paramref name="op"/> at <paramref name="voxel"/> spacing (0 = auto). Throws a
	/// FeatureException with the reason when it cannot: an open solid, solids that miss, a voxel
	/// too small for the size.</summary>
	public static PolyMesh Apply( BooleanOp op, PolyMesh target, PolyMesh tool, float voxel = 0f ) =>
		Apply( op, target, tool, voxel, out _ );

	public static PolyMesh Apply( BooleanOp op, PolyMesh target, PolyMesh tool, float voxel, out float used )
	{
		if ( target is null || tool is null || target.FaceCount == 0 || tool.FaceCount == 0 )
			throw Problem( "A boolean needs two solids", "One of the inputs is missing or has no faces.", "Make sure both bodies exist and have faces" );

		foreach ( var (solid, which) in new[] { (target, "target"), (tool, "tool") } )
		{
			var check = MeshValidator.Validate( solid );

			if ( !check.IsClosed )
				throw Problem( $"The voxel boolean needs closed solids, and the {which} is open",
					$"The {which} has {check.BoundaryEdges} boundary edge(s), so it has no inside to combine.",
					"Close it first (fix holes)", "Or give the sheet thickness (Shell)" );
		}

		if ( op != BooleanOp.Union && Gap( target, tool, out var axis, out var gap ) )
			throw Problem( $"{(op == BooleanOp.Subtract ? "Subtract" : "Intersect")} failed: the two solids do not overlap",
				gap > 1e-6f ? $"They miss each other by {gap:0.###} along {axis}, so there is nothing to {(op == BooleanOp.Subtract ? "cut" : "keep")}."
					: $"They only touch along {axis}, enclosing no common volume.",
				"Move the tool so it overlaps the target" );

		used = voxel > 0f ? voxel : AutoVoxel( op, target, tool );

		var (min, max) = Region( op, target, tool );
		var size = max - min + Vec3.One * used * 4f;
		var voxels = (long)(size.x / used + 2) * (long)(size.y / used + 2) * (long)(size.z / used + 2);

		if ( voxels > MaxVoxels )
		{
			var fits = MathF.Cbrt( size.x * size.y * size.z / MaxVoxels );
			throw new FeatureException( new FeatureDiagnostic( DiagnosticSeverity.Error,
				"The voxel is too small for the size of the parts",
				$"The boolean would sample {voxels:N0} voxels; the limit is {MaxVoxels:N0}.",
				"Voxel", MathF.Ceiling( fits * 1000f ) / 1000f,
				new[] { $"Set Voxel to {fits:0.###} or more", "Or leave it at 0 for the automatic size" } ) );
		}

		// The same lattice SurfaceNets will sample: its bounds padded by two voxels.
		var origin = min - Vec3.One * used * 2f;
		var span = max + Vec3.One * used * 2f - origin;
		var nx = Math.Max( 2, (int)MathF.Ceiling( span.x / used ) + 1 );
		var ny = Math.Max( 2, (int)MathF.Ceiling( span.y / used ) + 1 );
		var nz = Math.Max( 2, (int)MathF.Ceiling( span.z / used ) + 1 );

		var targetTree = MeshBVH.Build( target );
		var toolTree = MeshBVH.Build( tool );
		var fa = SignedGrid( target, targetTree, origin, used, nx, ny, nz );
		var fb = SignedGrid( tool, toolTree, origin, used, nx, ny, nz );
		var values = new float[fa.Length];

		for ( var i = 0; i < values.Length; i++ )
			values[i] = op switch
			{
				BooleanOp.Subtract => MathF.Max( fa[i], -fb[i] ),
				BooleanOp.Intersect => MathF.Max( fa[i], fb[i] ),
				_ => MathF.Min( fa[i], fb[i] ),
			};

		var grid = new GridSdf( values, origin, used, nx, ny, nz, min, max );
		var result = SurfaceNets.Mesh( grid, used, -1f, min, max );
		var mesh = result.Mesh;

		if ( mesh.FaceCount == 0 )
			throw Problem( $"{op} left nothing behind",
				op == BooleanOp.Subtract ? "The tool swallows the whole target." : "The solids share no volume at this voxel size.",
				"Check the tool's size and position", "Try a smaller voxel" );

		// A cut's new wall belongs to the part it was cut into, as in every CAD tool: only a union
		// keeps each side's own material, by whichever input surface a face sits on.
		var targetMaterial = target.Faces[0].Material;
		var toolMaterial = tool.Faces[0].Material;

		for ( var f = 0; f < mesh.FaceCount; f++ )
		{
			var face = mesh.Faces[f];
			face.Material = targetMaterial;

			if ( op != BooleanOp.Union || toolMaterial == targetMaterial )
				continue;

			var c = mesh.FaceCentroid( face );
			var da = targetTree.NearestSurface( target, c, used * 4f )?.Distance ?? float.MaxValue;
			var db = toolTree.NearestSurface( tool, c, used * 4f )?.Distance ?? float.MaxValue;
			if ( db < da ) face.Material = toolMaterial;
		}

		return mesh;
	}

	/// <summary>The box the answer lives in: both for a union, the target for a cut, the overlap
	/// for an intersect.</summary>
	static (Vec3 Min, Vec3 Max) Region( BooleanOp op, PolyMesh target, PolyMesh tool )
	{
		var (aMin, aMax) = Bounds( target );
		var (bMin, bMax) = Bounds( tool );

		return op switch
		{
			BooleanOp.Subtract => (aMin, aMax),
			BooleanOp.Intersect => (new Vec3( MathF.Max( aMin.x, bMin.x ), MathF.Max( aMin.y, bMin.y ), MathF.Max( aMin.z, bMin.z ) ),
				new Vec3( MathF.Min( aMax.x, bMax.x ), MathF.Min( aMax.y, bMax.y ), MathF.Min( aMax.z, bMax.z ) )),
			_ => (new Vec3( MathF.Min( aMin.x, bMin.x ), MathF.Min( aMin.y, bMin.y ), MathF.Min( aMin.z, bMin.z ) ),
				new Vec3( MathF.Max( aMax.x, bMax.x ), MathF.Max( aMax.y, bMax.y ), MathF.Max( aMax.z, bMax.z ) )),
		};
	}

	static (Vec3, Vec3) Bounds( PolyMesh mesh )
	{
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		return (min, max);
	}

	static bool Gap( PolyMesh a, PolyMesh b, out string axis, out float gap )
	{
		var (aMin, aMax) = Bounds( a );
		var (bMin, bMax) = Bounds( b );
		var gx = MathF.Max( bMin.x - aMax.x, aMin.x - bMax.x );
		var gy = MathF.Max( bMin.y - aMax.y, aMin.y - bMax.y );
		var gz = MathF.Max( bMin.z - aMax.z, aMin.z - bMax.z );
		gap = MathF.Max( gx, MathF.Max( gy, gz ) );
		axis = gap == gx ? "X" : gap == gy ? "Y" : "Z";
		return gap >= -1e-6f;
	}

	static FeatureException Problem( string problem, string cause, params string[] remedies ) =>
		new( new FeatureDiagnostic( DiagnosticSeverity.Error, problem, cause, remedies: remedies ) );

	/// <summary>
	/// A closed mesh sampled as a signed distance on a lattice.
	///
	/// WHY NOT SdfMesh. SdfMesh signs a point by the normal at its nearest surface point and, past
	/// its band, gives up and answers "outside" (+band). That is fine for a thin part and wrong for
	/// the middle of a thick one: a point deep inside a 10 in box is more than a band from every
	/// face, reads as outside, and the mesher wraps a second, inward-facing skin around the hollow
	/// it imagines. The boolean came out with a third of the volume it should have had.
	///
	/// So the SIGN comes from parity: one ray along +x per lattice column, every crossing counted,
	/// a node inside when an odd number of crossings lie before it. The column is nudged off the
	/// lattice by a fraction of a voxel so it never runs exactly along a face or through an edge.
	/// The MAGNITUDE is the exact nearest-surface distance, but only at nodes beside a sign change -
	/// the only values surface nets interpolates - and a full voxel elsewhere. One ray per column
	/// and a distance query per surface node, instead of three rays per node: 20 s became well under one.
	/// </summary>
	static float[] SignedGrid( PolyMesh mesh, MeshBVH tree, Vec3 origin, float voxel, int nx, int ny, int nz )
	{
		var inside = new bool[(long)nx * ny * nz];
		long Index( int x, int y, int z ) => ((long)x * ny + y) * nz + z;
		var dir = new Vec3( 1, 0, 0 );

		// Parity only counts from OUTSIDE: the ray starts left of the mesh, not of the lattice,
		// which for an intersect or a cut can begin inside the solid.
		var startX = origin.x - voxel;
		foreach ( var p in mesh.Positions ) startX = MathF.Min( startX, p.x - voxel );

		System.Threading.Tasks.Parallel.For( 0, ny, y =>
		{
			var crossings = new System.Collections.Generic.List<float>();

			for ( var z = 0; z < nz; z++ )
			{
				crossings.Clear();
				var from = new Vec3( startX, origin.y + (y + 0.0137f) * voxel, origin.z + (z + 0.0071f) * voxel );

				for ( var guard = 0; guard < 100_000; guard++ )
				{
					if ( tree.Raycast( mesh, from, dir ) is not { } hit )
						break;

					crossings.Add( hit.Point.x );
					from = new Vec3( hit.Point.x + voxel * 1e-4f, from.y, from.z );
				}

				var c = 0;

				for ( var x = 0; x < nx; x++ )
				{
					var px = origin.x + x * voxel;
					while ( c < crossings.Count && crossings[c] < px ) c++;
					inside[Index( x, y, z )] = (c & 1) == 1;
				}
			}
		} );

		var values = new float[inside.Length];

		System.Threading.Tasks.Parallel.For( 0, nx, x =>
		{
			for ( var y = 0; y < ny; y++ )
				for ( var z = 0; z < nz; z++ )
				{
					var i = Index( x, y, z );
					var me = inside[i];
					var edge = (x > 0 && inside[Index( x - 1, y, z )] != me) || (x < nx - 1 && inside[Index( x + 1, y, z )] != me)
						|| (y > 0 && inside[Index( x, y - 1, z )] != me) || (y < ny - 1 && inside[Index( x, y + 1, z )] != me)
						|| (z > 0 && inside[Index( x, y, z - 1 )] != me) || (z < nz - 1 && inside[Index( x, y, z + 1 )] != me);

					var d = voxel;

					if ( edge )
					{
						var p = new Vec3( origin.x + x * voxel, origin.y + y * voxel, origin.z + z * voxel );
						d = MathF.Max( tree.NearestSurface( mesh, p, voxel * 2f )?.Distance ?? voxel, voxel * 1e-3f );
					}

					values[i] = me ? -d : d;
				}
		} );

		return values;
	}

	/// <summary>Lattice values handed to surface nets, which samples exactly the lattice they were
	/// computed on - so Eval is a lookup, not an interpolation.</summary>
	sealed class GridSdf : SdfLeaf
	{
		readonly float[] _values;
		readonly Vec3 _origin, _min, _max;
		readonly float _voxel;
		readonly int _nx, _ny, _nz;

		public GridSdf( float[] values, Vec3 origin, float voxel, int nx, int ny, int nz, Vec3 min, Vec3 max )
		{
			_values = values; _origin = origin; _voxel = voxel; _nx = nx; _ny = ny; _nz = nz; _min = min; _max = max;
		}

		public override Vec3 Min => _min;
		public override Vec3 Max => _max;

		public override float Eval( Vec3 p )
		{
			var x = (int)MathF.Round( (p.x - _origin.x) / _voxel );
			var y = (int)MathF.Round( (p.y - _origin.y) / _voxel );
			var z = (int)MathF.Round( (p.z - _origin.z) / _voxel );

			if ( x < 0 || y < 0 || z < 0 || x >= _nx || y >= _ny || z >= _nz )
				return _voxel;

			return _values[((long)x * _ny + y) * _nz + z];
		}
	}
}
