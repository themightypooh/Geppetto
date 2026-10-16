using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Fewer triangles, same shape — or, in Quad mode, a new quad mesh over the same shape.
/// <see cref="SubdivideFeature"/>'s opposite number.
///
/// THE FEATURE THE IMPORTER CREATED THE NEED FOR. Everything the tool built itself arrived at a
/// sensible density because a primitive is a handful of quads and you add detail deliberately. A
/// mesh that came from somewhere else did not: a Meshy generation, a scan, a sculpt exported by
/// whoever made it, all land somewhere between a hundred thousand and a million triangles because
/// nothing upstream had a reason to stop. That mesh is the right SHAPE and the wrong SIZE, and at
/// that size it is not a model you can work on — the pick tree keeps hover usable and nothing keeps
/// subdivision, weight painting, the boolean or the compiler usable. Remesh is the step that turns
/// an import into a part.
///
/// IT COMES BACK AS TRIANGLES. Always, including where quads went in — see <see cref="Decimate"/>
/// for why, and for why the answer is not to pretend otherwise. So this belongs on an import and
/// not on a CAD body: rolling a Remesh over a quad cage you built with sketches and extrudes throws
/// away the topology that made it a cage, and the feature says so rather than letting you find out
/// two workspaces later. The refusal is a warning rather than an error because there are real
/// reasons to want it anyway — a collision hull, a distant LOD — and being told is enough.
///
/// THE TARGET IS IN TRIANGLES, NOT FACES, and that is a distinction the dialog has to keep making
/// because the tree everywhere else counts faces. A 500-face quad cage is 1000 triangles; typing
/// 500 into a triangle budget and getting half of what you meant is the kind of confusion that
/// looks like the algorithm being bad at its job.
///
/// QUAD MODE IS THE OTHER ANSWER to the same import, and the one a part that will be skinned or
/// subdivided wants: <see cref="QuadRemesh"/> lays a new all-quad mesh over the surface at a face
/// budget, the ZRemesher-style topology a pro organic asset ships with. Its target is in FACES,
/// because a quad is the unit there, and it is a separate number from the triangle budget rather
/// than the same field re-labelled, so switching modes back and forth does not quietly change what
/// either one asks for. It throws away UVs, materials and skin weights — the vertices are new, so
/// there is nothing to carry them on — and the feature says so when there were any.
/// </summary>
public sealed class RemeshFeature : Feature
{
	public override string TypeName => "Remesh";

	/// <summary>A part, and nothing finer. There is no per-face form of this: the quadrics are
	/// summed over the whole surface and a boundary drawn through the middle of one would be
	/// preserved as a border, which is the opposite of what picking a region would mean.</summary>
	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );

	/// <summary>
	/// Decimate or Quad. DECIMATE FIRST, and the default, because that is what every document
	/// saved before Quad existed meant: the field is found by reflection on load, and a missing one
	/// keeps the value it was constructed with.
	/// </summary>
	public readonly ChoiceParam Mode = new( "Mode", new[] { "Decimate", "Quad", "Voxel" } );

	public const int ModeDecimate = 0;
	public const int ModeQuad = 1;
	public const int ModeVoxel = 2;

	/// <summary>Voxel mode: the edge length of the new skin, in inches. Detail smaller than this is gone.</summary>
	public readonly FloatParam Voxel = new( "Voxel", 0.25f, 0.01f, 50f, "in" );

	/// <summary>Voxel mode: melt every picked part into one skin where they overlap (Dynamesh), rather
	/// than re-skinning each on its own.</summary>
	public readonly BoolParam Fuse = new( "Fuse parts", true );

	/// <summary>Voxel mode, fused: the radius of the fillet where parts meet, in inches. 0 is a crisp seam.</summary>
	public readonly FloatParam Fillet = new( "Fillet", 0f, 0f, 50f, "in" );

	/// <summary>Voxel mode: Taubin smoothing passes over the new skin, to take off the voxel stair-steps.</summary>
	public readonly IntParam Smooth = new( "Smooth", 2, 0, 20 );

	const long MaxVoxels = 60_000_000;

	/// <summary>Quads to aim for, per body, in Quad mode. A target rather than a promise — the
	/// lattice decides where faces fall, and the count lands within about ten percent.</summary>
	public readonly IntParam Faces = new( "Faces", 4000, 16, 400_000 ) { Slider = false };

	/// <summary>Quad mode only: remesh the positive-X half and mirror it, so the topology is
	/// exactly symmetric — the version a character wants, and one a mirrored cage already has.</summary>
	public readonly BoolParam SymmetryX = new( "Symmetry X", false );

	/// <summary>
	/// Which of the two numbers below is the one being read.
	///
	/// PERCENTAGE FIRST because it is the one you can answer without knowing anything. An import
	/// whose size you have not looked at has no obvious triangle budget, and "keep a tenth" is a
	/// decision you can make on the spot and correct once. A count is what you switch to when the
	/// budget is coming from somewhere else — a platform limit, an LOD ladder, a collision hull.
	/// </summary>
	public readonly ChoiceParam Target = new( "Target", new[] { "Percentage", "Triangle count" } );

	/// <summary>Fraction of the incoming triangles to keep, as a percentage. Read when
	/// <see cref="Target"/> is Percentage.</summary>
	public readonly FloatParam Percent = new( "Keep %", 10f, 0.1f, 100f );

	/// <summary>
	/// Triangles to stop at, per body. PER BODY rather than for the selection as a whole: splitting
	/// one budget across parts means deciding what a part is worth, and the honest version of that
	/// decision is to run the feature twice with two numbers rather than to have it guessed.
	/// </summary>
	public readonly IntParam Triangles = new( "Triangles", 5000, 4, 4_000_000 ) { Slider = false };

	/// <summary>Hold open borders and the lines between materials. See
	/// <see cref="Decimate.Options.PreserveBoundary"/> — off, a shell's opening goes ragged.</summary>
	public readonly BoolParam HoldBorders = new( "Hold borders", true );

	/// <summary>Merge vertices that share a position first. See
	/// <see cref="Decimate.Options.Weld"/> — off, an unwelded export cannot be reduced at all.</summary>
	public readonly BoolParam Weld = new( "Weld first", true );

	public override IReadOnlyList<IParam> Parameters => Mode.Index switch
	{
		ModeQuad => new IParam[] { Bodies, Mode, Faces, SymmetryX },
		ModeVoxel => new IParam[] { Bodies, Mode, Voxel, Fuse, Fillet, Smooth },
		_ => new IParam[] { Bodies, Mode, Target, Percent, Triangles, HoldBorders, Weld },
	};

	/// <summary>The two protections are answered once, if ever. The target is the whole
	/// question.</summary>
	public override IReadOnlyList<IParam> AdvancedParameters => Mode.Index switch
	{
		ModeQuad => Array.Empty<IParam>(),
		ModeVoxel => new IParam[] { Smooth },
		_ => new IParam[] { HoldBorders, Weld },
	};

	/// <summary>
	/// What this will cost, for a dialog that can say "412,908 → 41,290" before you press anything.
	///
	/// The same question <see cref="SubdivideFeature.PredictCost"/> answers, and needed more here:
	/// subdivision's cost is arithmetic anybody can do in their head, and a percentage of a number
	/// you have never seen is not.
	///
	/// In Quad mode the second number is the face target, which is what the result will be counted
	/// in; the first stays the incoming triangles, because that is what the import is counted in.
	/// </summary>
	public (int From, int To) PredictCost( IEnumerable<Body> bodies )
	{
		var from = 0;
		var to = 0;

		foreach ( var body in bodies.Where( Bodies.Matches ) )
		{
			var count = Decimate.TriangleCount( body.Mesh );

			from += count;
			to += Mode.Index == ModeQuad ? Faces.Clamped : Math.Clamp( TargetFor( count ), 4, count );
		}

		return (from, to);
	}

	int TargetFor( int triangles ) =>
		Target.Index == 1
			? Triangles.Clamped
			: (int)MathF.Round( triangles * Math.Clamp( Percent.Clamped, 0f, 100f ) / 100f );

	protected override void Execute( FeatureContext ctx )
	{
		var targets = ctx.Bodies.Where( Bodies.Matches ).ToList();

		if ( targets.Count == 0 )
		{
			Fail(
				"Nothing to remesh",
				"The bodies this feature names are not in the model at this point in the tree.",
				"Pick a part and try again",
				"Or clear the selection to remesh every part" );
		}

		if ( Mode.Index == ModeQuad )
		{
			ExecuteQuad( targets );
			return;
		}

		if ( Mode.Index == ModeVoxel )
		{
			ExecuteVoxel( ctx, targets );
			return;
		}

		var missed = new List<string>();
		var quads = 0;

		foreach ( var body in targets )
		{
			var before = Decimate.TriangleCount( body.Mesh );

			if ( before <= 4 )
				continue;

			// Counted before the reduce, since afterwards everything is triangles and the question
			// cannot be asked any more.
			if ( body.Mesh.Faces.Any( f => f.Count > 3 ) )
				quads++;

			var result = Decimate.Run( body.Mesh, new Decimate.Options
			{
				TargetTriangles = TargetFor( before ),
				Weld = Weld.Value,
				PreserveBoundary = HoldBorders.Value,
			} );

			body.Mesh = result.Mesh;

			if ( !result.ReachedTarget )
				missed.Add( $"{body.Name} stopped at {result.ToTriangles:N0}" );
		}

		// BOTH OF THESE ARE WARNINGS, and the model is built either way. See the class note: a
		// remesh that could not reach its budget still produced a smaller mesh, and a remesh over a
		// quad cage is a thing somebody may have meant.
		if ( missed.Count > 0 )
		{
			Warn(
				missed.Count == 1
					? "The target was not reached"
					: $"{missed.Count} parts did not reach the target",
				"There were no collapses left that keep the surface manifold and the right way out: "
					+ string.Join( ", ", missed ) + ".",
				"Take what it gave you — this is as small as the shape goes without tearing",
				"Or turn off Hold borders, if the part is open and its opening does not matter" );
		}
		else if ( quads > 0 )
		{
			Warn(
				quads == 1 ? "The part came back as triangles" : $"{quads} parts came back as triangles",
				"Remesh reduces a triangle mesh, and the part it ran on had quads in it. Quads are "
					+ "what subdivision and skinning want, so a cage that had them has lost them.",
				"Suppress this feature if the part was a cage rather than an import",
				"Keep it if the triangles are what you wanted — a collision hull, or a distant LOD" );
		}
	}

	/// <summary>
	/// Voxel mode, ZBrush's Dynamesh and Blender's voxel remesh: each part becomes a distance field,
	/// the fields are unioned (filleted by <see cref="Fillet"/>), and SurfaceNets lays one even quad
	/// skin over the result at <see cref="Voxel"/> spacing. It is what lets a sculpt grow: a horn
	/// pulled out of a head has stretched faces, and a voxel pass gives it fresh even ones; a part
	/// pushed into another melts into it. Material slots survive (each face takes its part's slot);
	/// UVs and weights do not, and the feature says so.
	/// </summary>
	void ExecuteVoxel( FeatureContext ctx, List<Body> targets )
	{
		var voxel = Voxel.Clamped;
		var groups = Fuse.Value ? new List<List<Body>> { targets } : targets.Select( b => new List<Body> { b } ).ToList();
		var open = new List<string>();
		var lost = new List<string>();

		foreach ( var group in groups )
		{
			var parts = group.Where( b => b.Mesh is not null && b.Mesh.FaceCount > 0 ).ToList();
			if ( parts.Count == 0 ) continue;

			var leaves = new List<Sdf>();
			foreach ( var body in parts )
			{
				if ( IsOpen( body.Mesh ) ) open.Add( body.Name ?? body.Id );
				if ( body.Mesh.Faces.Any( f => f.UVs.Any( uv => uv.x != 0f || uv.y != 0f ) ) || body.Mesh.IsRigged ) lost.Add( body.Name ?? body.Id );
				leaves.Add( new SdfMesh( body.Mesh, voxel * 3f + Fillet.Clamped * 2f ) { Material = body.Mesh.Faces[0].Material } );
			}

			Sdf field = leaves.Count == 1 ? leaves[0] : new SdfBlend( Fillet.Clamped, leaves.ToArray() );
			var size = field.Max - field.Min + Vec3.One * voxel * 4f;
			var voxels = (long)(size.x / voxel + 1) * (long)(size.y / voxel + 1) * (long)(size.z / voxel + 1);

			if ( voxels > MaxVoxels )
			{
				var fits = MathF.Cbrt( size.x * size.y * size.z / MaxVoxels );
				FailOn( "Voxel", MathF.Ceiling( fits * 100f ) / 100f,
					"The voxel is too small for the size of the part",
					$"Meshing would sample {voxels:N0} voxels; the limit is {MaxVoxels:N0}.",
					$"Set Voxel to {fits:0.##} or more" );
			}

			var result = SurfaceNets.Mesh( field, voxel );
			if ( result.Mesh.FaceCount == 0 ) continue;
			if ( Smooth.Clamped > 0 ) MeshSmooth.Taubin( result.Mesh, Smooth.Clamped );

			foreach ( var face in result.Mesh.Faces )
				face.Material = result.Nearest[face.Indices[0]]?.Material ?? leaves[0].Material;

			parts[0].Mesh = result.Mesh;
			foreach ( var gone in parts.Skip( 1 ) ) ctx.Bodies.Remove( gone );
		}

		if ( open.Count > 0 )
		{
			Warn(
				open.Count == 1 ? $"{open[0]} is open" : $"{open.Count} parts are open",
				"A voxel remesh reads a part as a solid, and an open surface has no inside, so its skin may "
					+ "come out lumpy or holed: " + string.Join( ", ", open ) + ".",
				"Close the holes upstream (Fill holes)",
				"Or give a sheet thickness first (Shell)" );
		}
		else if ( lost.Count > 0 )
		{
			Warn(
				lost.Count == 1 ? $"{lost[0]} lost its UVs or rig" : $"{lost.Count} parts lost their UVs or rig",
				"The voxel skin is new vertices, so UVs and weights do not come across: " + string.Join( ", ", lost ) + ".",
				"Unwrap and weight after this feature" );
		}
	}

	/// <summary>True when some edge is used by only one face: the surface has a hole.</summary>
	static bool IsOpen( PolyMesh mesh )
	{
		var uses = new Dictionary<EdgeKey, int>();
		foreach ( var f in mesh.Faces )
			for ( var i = 0; i < f.Count; i++ )
			{
				var k = new EdgeKey( f.Indices[i], f.Indices[(i + 1) % f.Count] );
				uses[k] = uses.GetValueOrDefault( k ) + 1;
			}
		return uses.Values.Any( u => u == 1 );
	}

	/// <summary>
	/// Quad mode: every target body replaced by a quad remesh of itself.
	///
	/// WHAT IT LOSES IS SAID, not discovered. A body that came in with UVs, more than one material
	/// or a rig comes out with none of them, and that is a warning rather than a refusal because an
	/// import that is about to be retopologised usually has nothing worth keeping on it — but a
	/// part somebody already unwrapped does, and finding that out at export is too late.
	/// </summary>
	void ExecuteQuad( List<Body> targets )
	{
		var lost = new List<string>();
		var empty = new List<string>();

		foreach ( var body in targets )
		{
			if ( Decimate.TriangleCount( body.Mesh ) <= 4 )
				continue;

			var mesh = body.Mesh;
			var hadUVs = mesh.Faces.Any( f => f.UVs.Any( uv => uv.x != 0f || uv.y != 0f ) );
			var hadMaterials = mesh.Faces.Select( f => f.Material ).Distinct().Count() > 1;

			var result = QuadRemesh.Run( mesh, new QuadRemesh.Options
			{
				TargetFaces = Faces.Clamped,
				SymmetryX = SymmetryX.Value,
			} );

			if ( result.Mesh.FaceCount == 0 )
			{
				empty.Add( body.Name );
				continue;
			}

			var material = mesh.FaceCount > 0 ? mesh.Faces[0].Material : 0;

			foreach ( var f in result.Mesh.Faces )
				f.Material = material;

			body.Mesh = result.Mesh;

			if ( hadUVs || hadMaterials || mesh.IsRigged )
				lost.Add( body.Name );
		}

		if ( empty.Count > 0 )
		{
			Warn(
				empty.Count == 1 ? $"{empty[0]} could not be remeshed" : $"{empty.Count} parts could not be remeshed",
				"The surface had no area to lay a lattice on, so the part was left as it was: "
					+ string.Join( ", ", empty ) + ".",
				"Check the part is a surface and not a line of points",
				"Or switch Mode back to Decimate" );
		}
		else if ( lost.Count > 0 )
		{
			Warn(
				lost.Count == 1 ? $"{lost[0]} lost its UVs, materials or rig" : $"{lost.Count} parts lost their UVs, materials or rig",
				"A quad remesh builds new vertices, so nothing that lived on the old ones comes across. "
					+ "Everything is on the first material now, with no UVs and no weights: "
					+ string.Join( ", ", lost ) + ".",
				"Unwrap and weight the new mesh after this feature",
				"Or suppress it if those were the point of the part" );
		}
	}
}
