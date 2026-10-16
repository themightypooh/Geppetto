using System;
using System.Collections.Generic;

namespace Effigy;

public static partial class SkinBinder
{
	/// <summary>Bone heat weights, falling back to voxel distance where heat has no answer. See <see cref="AutoWeights"/>.</summary>
	public static SkinWeights BindHeat( PolyMesh mesh, Skeleton skeleton, out BindReport report, AutoWeightOptions options = null ) =>
		AutoWeights.Heat( mesh, skeleton, out report, options );

	/// <summary>Geodesic voxel weights: distance measured through the solid. See <see cref="AutoWeights"/>.</summary>
	public static SkinWeights BindVoxel( PolyMesh mesh, Skeleton skeleton, out BindReport report, AutoWeightOptions options = null ) =>
		AutoWeights.Voxel( mesh, skeleton, out report, options );

	/// <summary>
	/// THE ONE BIND every caller should use — editor preview, export, playermodels, scripts — so
	/// that what is previewed is what ships.
	///
	/// <see cref="BindMethod.Bodies"/> is exactly the long-standing behaviour: BindBodies then
	/// SmoothWeights. The automatic methods weight the whole merged mesh, then lay the parametric
	/// rules over it the way BindBodies does: a body pinned to a bone in
	/// <paramref name="bodyIdToBoneName"/> is rigid to it, and an unpinned body that arrived with
	/// weights of its own (an imported rig, a garment that copied its wearer) keeps them.
	/// </summary>
	public static SkinWeights Bind(
		PolyMesh mesh,
		IReadOnlyList<BodyRange> ranges,
		IReadOnlyDictionary<string, string> bodyIdToBoneName,
		Skeleton skeleton,
		BindMethod method,
		out BindReport report,
		AutoWeightOptions options = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		RequireBones( skeleton );

		ranges ??= Array.Empty<BodyRange>();
		bodyIdToBoneName ??= new Dictionary<string, string>();

		if ( method == BindMethod.Bodies )
		{
			report = new BindReport { Requested = method, Used = method, OtherVertices = mesh.VertexCount };
			return SmoothWeights( mesh, BindBodies( mesh, ranges, bodyIdToBoneName, skeleton ) );
		}

		SkinWeights weights;

		switch ( method )
		{
			case BindMethod.Heat:
				weights = AutoWeights.Heat( mesh, skeleton, out report, options );
				break;

			case BindMethod.Voxel:
				weights = AutoWeights.Voxel( mesh, skeleton, out report, options );
				break;

			case BindMethod.Distance:
				weights = BindSmooth( mesh, skeleton );
				report = new BindReport { Requested = method, Used = method, OtherVertices = mesh.VertexCount };
				var max = (options ?? new AutoWeightOptions()).MaxInfluences;

				for ( var v = 0; v < weights.Count; v++ )
					weights[v] = SkinWeights.Prune( weights[v], max );

				break;

			default:
				throw new ArgumentOutOfRangeException( nameof( method ), $"Unknown bind method {method}" );
		}

		var pinned = 0;

		foreach ( var range in ranges )
		{
			if ( bodyIdToBoneName.TryGetValue( range.BodyId, out var boneName ) )
			{
				var bone = skeleton.IndexOf( boneName );

				if ( bone < 0 )
					throw new InvalidOperationException(
						$"Body '{range.BodyName}' is bound to bone '{boneName}', which is not in the skeleton. "
						+ "Re-assign the body in the Rig workspace, or clear its assignment." );

				var rigid = new[] { new BoneWeight( bone, 1f ) };

				for ( var i = range.Start; i < range.Start + range.Count && i < weights.Count; i++ )
					weights[i] = rigid;

				pinned++;
				continue;
			}

			if ( range.HasOwnWeights && mesh.IsRigged )
			{
				for ( var i = range.Start; i < range.Start + range.Count && i < weights.Count; i++ )
					if ( mesh.Skin[i] is { Length: > 0 } own )
						weights[i] = own;
			}
		}

		if ( pinned > 0 )
			report.Notes.Add( $"{pinned} assigned bod{(pinned == 1 ? "y" : "ies")} kept rigid to their bone" );

		return weights;
	}

	/// <summary>The bind method's name as scripts and the document spell it: bodies, heat, voxel, distance.</summary>
	public static string MethodName( BindMethod method ) => method.ToString().ToLowerInvariant();

	/// <summary>Parse <see cref="MethodName"/>'s spelling (case-insensitive). False for anything else.</summary>
	public static bool TryParseMethod( string text, out BindMethod method )
	{
		method = BindMethod.Bodies;

		switch ( (text ?? "").Trim().ToLowerInvariant() )
		{
			case "bodies": case "body": case "parts": method = BindMethod.Bodies; return true;
			case "heat": case "auto": method = BindMethod.Heat; return true;
			case "voxel": case "geodesic": method = BindMethod.Voxel; return true;
			case "distance": case "envelope": method = BindMethod.Distance; return true;
			default: return false;
		}
	}
}
