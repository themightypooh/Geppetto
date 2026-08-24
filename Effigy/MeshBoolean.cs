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
/// the interesting cases and does so by producing a mesh rather than an error. s&box ships
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
			throw new InvalidOperationException( "A boolean needs two solids" );

		if ( Provider is null )
		{
			throw new InvalidOperationException( UnavailableReason is { Length: > 0 } reason
				? $"{Name( op )} needs a mesh boolean. {reason}"
				: $"{Name( op )} needs a mesh boolean, and none is installed in this build. The kernel does "
					+ "not carry its own — see MeshBoolean for why." );
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
			throw new InvalidOperationException( $"{Name( op )} failed: {e.Message}" );
		}

		if ( !ok )
			throw new InvalidOperationException( $"{Name( op )} failed: {error ?? "the solids could not be combined"}" );

		if ( result is null || result.FaceCount == 0 )
		{
			// An empty result is a real answer to some inputs — cutting a solid with something that
			// swallows it whole — and it is never a useful one, because a body with no faces is
			// indistinguishable from a broken feature everywhere downstream.
			throw new InvalidOperationException(
				$"{Name( op )} left nothing behind. The cut probably covers the whole part — check the profile and the distance." );
		}

		return result;
	}

	static string Name( BooleanOp op ) => op switch
	{
		BooleanOp.Union => "Union",
		BooleanOp.Subtract => "Remove",
		BooleanOp.Intersect => "Intersect",
		_ => "Boolean"
	};
}
