using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// What soft bones collide with, as Marionette's .ctrlrig stores it (RigDocument.SoftColliders,
/// Ground, GroundHeight, SoftThickness): capsules on body bones and a floor. On the rig, not the
/// clip or the player, so Bake Secondary Motion, the headless sim's bake= and the game's live Soft
/// Chains all read the one body. See <see cref="ChainSpring.Options"/>.
/// </summary>
public sealed class RigSoftCollision
{
	public List<(string Bone, float Radius)> Capsules = new();

	/// <summary>The floor's height on the model's Z; null for none.</summary>
	public float? Ground;

	/// <summary>The soft bones' own radius - how far a tail stays off a capsule or the floor.</summary>
	public float Thickness = 1f;

	public bool Any => Capsules.Count > 0 || Ground is not null;

	public static RigSoftCollision ReadRig( string json )
	{
		var result = new RigSoftCollision();
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;

		if ( root.TryGetProperty( "SoftColliders", out var array ) && array.ValueKind == JsonValueKind.Array )
		{
			foreach ( var e in array.EnumerateArray() )
			{
				var bone = e.TryGetProperty( "Bone", out var b ) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
				var radius = e.TryGetProperty( "Radius", out var r ) && r.ValueKind == JsonValueKind.Number ? r.GetSingle() : 4f;
				var enabled = !e.TryGetProperty( "Enabled", out var en ) || en.ValueKind != JsonValueKind.False;

				if ( enabled && !string.IsNullOrEmpty( bone ) )
					result.Capsules.Add( (bone, radius) );
			}
		}

		if ( root.TryGetProperty( "Ground", out var ground ) && ground.ValueKind == JsonValueKind.True )
			result.Ground = root.TryGetProperty( "GroundHeight", out var h ) && h.ValueKind == JsonValueKind.Number ? h.GetSingle() : 0f;

		if ( root.TryGetProperty( "SoftThickness", out var t ) && t.ValueKind == JsonValueKind.Number )
			result.Thickness = t.GetSingle();

		return result;
	}

	/// <summary>The bake's options on <paramref name="skeleton"/>: capsules by index (a bone the model
	/// lacks is dropped), the floor, the thickness, and <paramref name="limits"/>.</summary>
	public ChainSpring.Options ToOptions( Skeleton skeleton, IReadOnlyList<RigLimit> limits = null )
	{
		var options = new ChainSpring.Options { Ground = Ground, Thickness = Thickness, Limits = limits };

		foreach ( var (bone, radius) in Capsules )
		{
			var index = skeleton.IndexOf( bone );

			if ( index >= 0 )
				options.Capsules.Add( new ChainSpring.Capsule( index, radius ) );
		}

		return options;
	}

	/// <summary>Capsules from text, "bone:radius,bone:radius" - the CLI's collide=.</summary>
	public static List<(string Bone, float Radius)> Parse( string text ) =>
		(text ?? "").Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
			.Select( s => s.Split( ':' ) )
			.Select( p => (p[0], p.Length > 1 ? float.Parse( p[1], System.Globalization.CultureInfo.InvariantCulture ) : 4f) )
			.ToList();
}
