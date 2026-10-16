using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// A cheap fingerprint of a skeleton - names, parents, bind pose and lengths - so a feature built
/// from the rig can tell the rig changed under it. The rig is not a feature (see
/// <see cref="PartStudio.Rig"/>), so moving a bone dirties nothing; a feature that reads the rig
/// asks this in <see cref="Feature.IsStale"/> instead.
/// </summary>
public static class RigFingerprint
{
	public static long Of( Skeleton skeleton )
	{
		if ( skeleton is null )
			return 0;

		unchecked
		{
			long hash = 1469598103934665603L;
			void Mix( long v ) => hash = (hash ^ v) * 1099511628211L;
			void MixF( float f ) => Mix( BitConverter.SingleToInt32Bits( f ) );
			void MixV( Vec3 v ) { MixF( v.x ); MixF( v.y ); MixF( v.z ); }

			Mix( skeleton.Count );

			foreach ( var bone in skeleton.Bones )
			{
				Mix( StringComparer.Ordinal.GetHashCode( bone.Name ?? "" ) );
				Mix( bone.Parent );
				MixV( bone.Local.X );
				MixV( bone.Local.Y );
				MixV( bone.Local.Z );
				MixV( bone.Local.Origin );
				MixF( bone.Length );
			}

			return hash;
		}
	}
}

/// <summary>
/// Body from Bones as a step in the history: a closed, all-quad body grown along the rig - a cube
/// per joint, a tube per bone, smoothed (see <see cref="SkinBlockout"/>). The block-out a
/// character starts from, on the bones it will be rigged to.
///
/// PARAMETRIC, unlike the one-shot import it replaces: move a bone, or change a thickness here,
/// and the body rebuilds. Radius per bone is text so a script and the dialog write the same thing:
/// <c>spine_1 4; arm_upper_* 1.6; * 1.2</c> - a bone name, a name ending in * for every bone that
/// starts that way, or * alone for every bone not otherwise named. A bone with no radius takes a
/// fifth of its length.
/// </summary>
public sealed class BodyFromBonesFeature : Feature
{
	public override string TypeName => "Body from Bones";

	/// <summary>Thickness per bone: "name radius; prefix* radius; * radius". Empty = a fifth of each
	/// bone's length.</summary>
	public readonly StringParam Radii = new( "Radii", "" );

	/// <summary>How many times the blocky skin is smoothed. 0 keeps the cubes and tubes.</summary>
	public readonly IntParam Levels = new( "Smooth levels", 2, 0, 4 );

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Radii, Levels, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Material };

	Skeleton _rig;
	long _built;

	public override bool IsStale => _rig is not null && RigFingerprint.Of( _rig ) != _built;

	protected override void Execute( FeatureContext ctx )
	{
		_rig = ctx.Rig;
		_built = RigFingerprint.Of( ctx.Rig );

		if ( ctx.Rig is null || ctx.Rig.Count == 0 )
		{
			Fail(
				"Body from Bones needs a rig",
				"The body is grown along the bones, and there are none yet.",
				"Add a template rig: Rig ▸ Template (Human, Biped, Quadruped), or `rig human` in a script",
				"Or place bones with Add Bone in the Rig workspace" );
		}

		var radii = ParseRadii( Radii.Value, ctx.Rig, out var problem );

		if ( problem is not null )
			FailOn( "Radii", "A radius could not be read", problem,
				"Write each as a bone name and a number: spine_1 4; arm_upper_* 1.6; * 1.2" );

		PolyMesh mesh;

		try
		{
			mesh = SkinBlockout.Build( ctx.Rig, radii is null ? null : i => radii[i] ?? DefaultRadius( ctx.Rig, i ), Levels.Clamped );
		}
		catch ( InvalidOperationException e )
		{
			Fail( "Body from Bones could not build", e.Message,
				"Give the bones some length, or check no joint has more than six bones leaving it" );
			return;
		}

		foreach ( var face in mesh.Faces )
			face.Material = Material.Clamped;

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? "Body", mesh ) );
		Warning = $"{mesh.FaceCount:N0} quads over {ctx.Rig.Count} bones. It has no UVs yet: add a UV project (Unwrap) before baking.";
	}

	static float DefaultRadius( Skeleton rig, int bone )
	{
		if ( !rig.TryBodySpan( bone, out var head, out var tail ) )
			return MathF.Max( rig.Bones[bone].Length * 0.2f, 0.05f );

		return MathF.Max( (tail - head).Length * 0.2f, 0.05f );
	}

	/// <summary>Per-bone radius from the text, null for bones it does not mention; null overall
	/// when the text is empty. <paramref name="problem"/> names an unreadable entry or a name that
	/// matches no bone, with the bones listed.</summary>
	public static float?[] ParseRadii( string text, Skeleton rig, out string problem )
	{
		problem = null;

		if ( string.IsNullOrWhiteSpace( text ) )
			return null;

		var result = new float?[rig.Count];
		float? fallback = null;
		var exact = new bool[rig.Count];
		var prefixLength = new int[rig.Count];

		foreach ( var raw in text.Split( new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries ) )
		{
			var entry = raw.Trim();

			if ( entry.Length == 0 )
				continue;

			var words = entry.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );

			if ( words.Length != 2 || !float.TryParse( words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var r ) || !(r > 0f) )
			{
				problem = $"'{entry}' is not a bone name and a positive radius.";
				return null;
			}

			var name = words[0];

			if ( name == "*" )
			{
				fallback = r;
				continue;
			}

			if ( name.EndsWith( '*' ) )
			{
				var prefix = name[..^1];
				var any = false;

				for ( var i = 0; i < rig.Count; i++ )
				{
					if ( exact[i] || !rig.Bones[i].Name.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) || prefix.Length < prefixLength[i] )
						continue;

					result[i] = r;
					prefixLength[i] = prefix.Length;
					any = true;
				}

				if ( !any )
				{
					problem = $"No bone starts with '{prefix}'. Bones: {string.Join( ", ", rig.Bones.Select( b => b.Name ).Take( 40 ) )}.";
					return null;
				}

				continue;
			}

			var index = rig.IndexOf( name );

			if ( index < 0 )
			{
				problem = $"No bone called '{name}'. Bones: {string.Join( ", ", rig.Bones.Select( b => b.Name ).Take( 40 ) )}.";
				return null;
			}

			result[index] = r;
			exact[index] = true;
		}

		if ( fallback is { } all )
		{
			for ( var i = 0; i < rig.Count; i++ )
				result[i] ??= all;
		}

		return result;
	}
}

/// <summary>
/// Automatic Weights: skin weights for the chosen bodies from the rig, so they bend with the bones
/// without anyone painting. The weights ride the mesh (<see cref="PolyMesh.Skin"/>), so export,
/// the pose preview and every later feature carry them; a later Subdivide blends them.
///
/// METHODS. Smooth is distance to each bone segment, then averaged over the surface so joints
/// bend instead of hinging. Envelope is the distance part alone - softer through space, blind to
/// the surface. Rigid gives each vertex wholly to its nearest bone: right for machinery. Heat is
/// the bone-heat solve (weights diffused over the surface from each bone); it is a hook another
/// build fills in through <see cref="HeatBinder"/>, and until then it refuses with the reason.
///
/// Helper bones - twist, IK, aim, hold - take no weight directly. With Twist on, each limb's
/// weight is then shared onto its twist bones by position along the limb
/// (<see cref="TwistWeights.Spread"/>). The result is kept to <see cref="MaxInfluences"/> bones per
/// vertex, strongest first, summing to one.
/// </summary>
public sealed class BindFeature : Feature
{
	public override string TypeName => "Automatic Weights";

	public override GeometryKind Accepts => GeometryKind.Body;

	public static readonly string[] MethodNames = { "Smooth", "Envelope", "Rigid", "Heat" };

	/// <summary>Bone heat: (mesh, deform skeleton) → weights indexed into that skeleton. Null until
	/// a build provides one; then Method = Heat uses it.</summary>
	public static Func<PolyMesh, Skeleton, SkinWeights> HeatBinder;

	/// <summary>Bodies to weight. Empty = every body that is not a reference.</summary>
	public readonly BodySelectionParam Bodies = new( "Bodies" );

	public readonly ChoiceParam Method = new( "Method", MethodNames );

	/// <summary>How quickly a bone's pull fades with distance. 1 reaches across the model, 4 is
	/// nearly rigid.</summary>
	public readonly FloatParam Falloff = new( "Falloff", 2f, 0.5f, 8f );

	/// <summary>Passes of averaging over the surface (Smooth only).</summary>
	public readonly IntParam Smoothing = new( "Smoothing", 3, 0, 20 );

	/// <summary>Share each limb's weight onto its twist bones.</summary>
	public readonly BoolParam Twist = new( "Use twist bones", true );

	/// <summary>Most bones one vertex may follow. Engines take four.</summary>
	public readonly IntParam MaxInfluences = new( "Bones per vertex", 4, 1, 8 );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Method, Falloff, Smoothing, Twist, MaxInfluences };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Falloff, MaxInfluences };

	Skeleton _rig;
	long _built;

	public override bool IsStale => _rig is not null && RigFingerprint.Of( _rig ) != _built;

	/// <summary>Vertices weighted by the last rebuild, over every body.</summary>
	public int WeightedVertices { get; private set; }

	protected override void Execute( FeatureContext ctx )
	{
		_rig = ctx.Rig;
		_built = RigFingerprint.Of( ctx.Rig );
		WeightedVertices = 0;

		if ( ctx.Rig is null || ctx.Rig.Count == 0 )
		{
			Fail(
				"Automatic Weights needs a rig",
				"Weights say how much each bone moves each vertex, and there are no bones yet.",
				"Add a template rig: Rig ▸ Template, or `rig human` in a script",
				"Or place bones with Add Bone in the Rig workspace" );
		}

		var targets = ctx.Bodies.Where( b => !b.IsReference && Bodies.Matches( b ) && b.Mesh is { VertexCount: > 0 } ).ToList();

		if ( targets.Count == 0 )
		{
			Fail(
				"Nothing to weight",
				Bodies.BodyIds.Count == 0 ? "There are no bodies before this in the history." : "None of the chosen bodies is in the model any more.",
				"Pick the bodies to weight, or move this step below the step that makes them" );
		}

		var method = Method.Value;

		if ( method == "Heat" && HeatBinder is null )
		{
			FailOn( "Method",
				"Heat weights are not available in this build",
				"Bone heat needs the surface solve, which has not been added yet.",
				"Use Smooth: distance to the bones, averaged over the surface" );
		}

		var deform = DeformSkeleton( ctx.Rig, out var map );

		if ( deform.Count == 0 )
		{
			Fail( "Every bone is a helper",
				"Twist, IK, aim and hold bones take no weight of their own, and the rig has nothing else.",
				"Add the limb bones those helpers belong to" );
		}

		var twists = Twist.Value && ctx.Rig.Bones.Any( b => RigBuild.IsTwistBone( b.Name ) );

		foreach ( var body in targets )
		{
			var mesh = body.Mesh;
			var weights = method switch
			{
				"Rigid" => SkinBinder.BindRigid( mesh, deform ),
				"Heat" => HeatBinder( mesh, deform ),
				_ => SkinBinder.BindSmooth( mesh, deform, Falloff.Clamped ),
			};

			if ( weights is null || weights.Count != mesh.VertexCount )
				Fail( "The weights did not come back for every vertex", $"{body.Name}: {weights?.Count ?? 0} of {mesh.VertexCount}.", "Use Smooth" );

			if ( method == "Smooth" && Smoothing.Clamped > 0 )
				weights = SkinBinder.SmoothWeights( mesh, weights, Smoothing.Clamped );

			// Back to the full rig's bone numbers.
			for ( var v = 0; v < weights.Count; v++ )
				weights[v] = weights[v].Select( w => new BoneWeight( map[w.Bone], w.Weight ) ).ToArray();

			mesh.Skin = weights;

			if ( twists )
				TwistWeights.Spread( mesh, ctx.Rig );

			for ( var v = 0; v < mesh.Skin.Count; v++ )
				mesh.Skin[v] = SkinWeights.Prune( mesh.Skin[v], MaxInfluences.Clamped );

			WeightedVertices += mesh.VertexCount;
		}

		Warning = $"{WeightedVertices:N0} vertices on {targets.Count} bod{(targets.Count == 1 ? "y" : "ies")} weighted to {deform.Count} bones ({method})"
			+ (twists ? ", shared onto twist bones." : ".");
	}

	/// <summary>Helpers that should take no weight directly: twist stations, IK targets, aim
	/// matrices, hold points, clothing attachments. Face bones (jaw, lids) DO take weight.</summary>
	public static bool IsHelper( string name )
	{
		if ( RigBuild.IsTwistBone( name ) )
			return true;

		var n = (name ?? "").ToLowerInvariant();
		return n.Contains( "helper" ) || n.Contains( "_ik" ) || n.StartsWith( "ik_" ) || n.Contains( "aim_" )
			|| n.StartsWith( "hold" ) || n.Contains( "_hold" ) || n.Contains( "clothing" );
	}

	/// <summary>
	/// What the binders measure distance to: one segment per bone that takes weight, head to the
	/// end of the limb it actually spans (<see cref="Skeleton.TryBodySpan"/>). That matters for an
	/// imported rig - citizen's bones run down +X with a one-inch +Y tail, and measuring to that
	/// inch hands an elbow's vertices to the wrong bone. The segments are flat roots: only their
	/// ends are read. <paramref name="map"/> takes a segment's index back to the rig's.
	/// </summary>
	public static Skeleton DeformSkeleton( Skeleton rig, out int[] map )
	{
		var deform = new Skeleton();
		var back = new List<int>();

		for ( var i = 0; i < rig.Count; i++ )
		{
			if ( IsHelper( rig.Bones[i].Name ) )
				continue;

			if ( !rig.TryBodySpan( i, out var head, out var tail ) )
			{
				head = rig.HeadWorld( i );
				tail = rig.TailWorld( i );
			}

			if ( (tail - head).Length < 1e-4f )
				tail = head + rig.WorldBind( i ).Y.Normal * 0.01f;

			deform.AddBoneFromPoints( rig.Bones[i].Name, -1, head, tail );
			back.Add( i );
		}

		map = back.ToArray();
		return deform;
	}
}
