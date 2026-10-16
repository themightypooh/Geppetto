using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Effigy;

/// <summary>What a <see cref="MorphDriver"/> reads off its bone. THE ORDER IS MARIONETTE'S
/// DriverChannel AND MUST STAY IT: a .ctrlrig may carry the channel as its ordinal.</summary>
public enum DriverChannel
{
	/// <summary>Signed degrees the bone has turned about <see cref="MorphDriver.Axis"/> since its
	/// rest pose, turns about the other two axes ignored. A bend about a hinge axis (knee, elbow,
	/// thigh pitch), or a twist when the axis is the bone's length.</summary>
	Rotation,

	/// <summary>Unsigned degrees the bone's <see cref="MorphDriver.Axis"/> has swung away from where
	/// it pointed at rest, in any direction, twist about it ignored. A cone reader for a shoulder or
	/// a hip that lifts every way.</summary>
	Swing,

	/// <summary>Distance in model units from the bone's head to <see cref="MorphDriver.OtherBone"/>'s.</summary>
	Distance
}

/// <summary>One of the bone's own rest axes. Ordinal-stable like <see cref="DriverChannel"/>.</summary>
public enum DriverAxis { X, Y, Z }

/// <summary>
/// A driver: a morph's weight read off a bone's pose - pose-space deformation, a corrective shape
/// key that fades in as the knee bends.
///
/// THE ANGLE IS THE CHILD'S TURN RELATIVE TO ITS PARENT, measured from its rest (bind) local:
/// delta = rest⁻¹ · current, so the reading is in the bone's own rest frame and is 0 at bind
/// whatever the bone's bind orientation. Never world space - a thigh corrective must not fire
/// because the whole body leaned (CloudRig's SKH bones exist for this, research-sitting.md §9.4).
///
/// THE MAPPING is a two-key curve: the reading at <see cref="Rest"/> gives 0, at <see cref="Target"/>
/// gives <see cref="Value"/>, eased between by a key ease (<see cref="RigKey.Apply"/>, Marionette's
/// KeyInterpolation). Clamped to the two keys unless <see cref="Clamp"/> is off, when it carries on
/// in a straight line. Cubic and Monotone through two keys are a straight line, and read as Linear.
///
/// SEVERAL DRIVERS ON ONE MORPH MULTIPLY: a "corrective of correctives" (thigh up AND out) fires only
/// when every one of its drivers does.
///
/// TWINNED with Marionette's RigMorphDriver (Code/RigControl), whose math is RigDriverMath. Effigy.Tests
/// compiles RigDriverMath beside this and checks the two agree, so the game, the viewport, the
/// headless take and the DMX bake all see the same weight.
/// </summary>
public sealed class MorphDriver
{
	public string Morph = "";
	public string Bone = "";
	public DriverChannel Channel = DriverChannel.Rotation;
	public DriverAxis Axis = DriverAxis.Y;

	/// <summary>The second bone of a <see cref="DriverChannel.Distance"/> reading.</summary>
	public string OtherBone = "";

	/// <summary>Degrees (or units for Distance) where the weight is 0, and where it reaches Value.</summary>
	public float Rest, Target = 90f;

	public RigEase Ease = RigEase.Smooth;
	public bool Clamp = true;

	/// <summary>The weight at Target. 1 is the shape as sculpted.</summary>
	public float Value = 1f;

	public bool Enabled = true;

	public MorphDriver Clone() => (MorphDriver)MemberwiseClone();

	public static Vec3 AxisVector( DriverAxis axis ) => axis switch
	{
		DriverAxis.X => new Vec3( 1, 0, 0 ),
		DriverAxis.Y => new Vec3( 0, 1, 0 ),
		_ => new Vec3( 0, 0, 1 ),
	};

	/// <summary>One line for a panel or a log: "knee_L Rotation Y 0° → 90°, Smooth".</summary>
	public string Describe()
	{
		var unit = Channel == DriverChannel.Distance ? "" : "°";
		var what = Channel switch
		{
			DriverChannel.Rotation => $"{Bone} rotation about {Axis}",
			DriverChannel.Swing => $"{Bone} swing of its {Axis} axis",
			_ => $"distance {Bone} to {OtherBone}",
		};

		return $"{what}, {N( Rest )}{unit} → {N( Target )}{unit}, {Ease}{(Clamp ? "" : ", unclamped")}";
	}

	// --- the math (RigDriverMath is the game's copy) -------------------------------------------

	/// <summary>
	/// Signed degrees of <paramref name="delta"/>'s turn about a unit <paramref name="axis"/>: the
	/// twist half of a swing-twist split about that axis. In (-180, 180].
	/// </summary>
	public static float RotationAbout( Quat delta, Vec3 axis )
	{
		var p = delta.x * axis.x + delta.y * axis.y + delta.z * axis.z;
		var degrees = 2f * MathF.Atan2( p, delta.w ) * 180f / MathF.PI;
		return Wrap( degrees );
	}

	/// <summary>Unsigned degrees a unit <paramref name="axis"/> has swung once turned by
	/// <paramref name="delta"/>. [0, 180].</summary>
	public static float SwingOf( Quat delta, Vec3 axis )
	{
		var moved = delta.Rotate( axis );
		var dot = Math.Clamp( Vec3.Dot( moved.Normal, axis ), -1f, 1f );
		return MathF.Acos( dot ) * 180f / MathF.PI;
	}

	static float Wrap( float degrees )
	{
		while ( degrees > 180f ) degrees -= 360f;
		while ( degrees <= -180f ) degrees += 360f;
		return degrees;
	}

	/// <summary>The reading mapped through Rest → Target and the ease, times Value.</summary>
	public float Map( float reading )
	{
		float t;
		var span = Target - Rest;

		if ( MathF.Abs( span ) < 1e-6f )
			t = reading >= Target ? 1f : 0f;
		else
			t = (reading - Rest) / span;

		float eased;

		if ( t >= 0f && t <= 1f )
			eased = Ease == RigEase.Stepped ? (t >= 1f ? 1f : 0f) : RigKey.Apply( Ease, t );
		else if ( Clamp )
			eased = t < 0f ? 0f : 1f;
		else
			eased = t; // past either key: a straight line, as the curve is defined only between them

		return eased * Value;
	}

	/// <summary>
	/// What the bone reads in this pose. <paramref name="local"/> is every bone's parent-space
	/// transform in the pose, <paramref name="world"/> the same in model space (only read for
	/// Distance; may be null otherwise). Null when a bone it names is not in the skeleton.
	/// </summary>
	public float? Reading( Skeleton skeleton, IReadOnlyList<Xform> local, IReadOnlyList<Xform> world = null )
	{
		var b = skeleton.IndexOf( Bone );

		if ( b < 0 )
			return null;

		if ( Channel == DriverChannel.Distance )
		{
			var o = skeleton.IndexOf( OtherBone );

			if ( o < 0 )
				return null;

			world ??= RigAnimFile.ModelSpace( skeleton, local );
			return (world[b].Origin - world[o].Origin).Length;
		}

		var rest = Quat.FromXform( skeleton.Bones[b].Local );
		var now = Quat.FromXform( local[b] );
		var delta = rest.Inverse * now;
		var axis = AxisVector( Axis );

		return Channel == DriverChannel.Swing ? SwingOf( delta, axis ) : RotationAbout( delta, axis );
	}

	/// <summary>The weight in this pose: 0 when the driver is off or its bone is missing.</summary>
	public float Evaluate( Skeleton skeleton, IReadOnlyList<Xform> local, IReadOnlyList<Xform> world = null ) =>
		Enabled && Reading( skeleton, local, world ) is { } reading ? Map( reading ) : 0f;

	/// <summary>
	/// Every driven morph's weight in a pose, into <paramref name="into"/> (cleared first). Drivers
	/// on one morph multiply. A disabled driver, or one whose morph has no name, drives nothing.
	/// </summary>
	public static Dictionary<string, float> WeightsAt( IReadOnlyList<MorphDriver> drivers, Skeleton skeleton,
		IReadOnlyList<Xform> local, Dictionary<string, float> into = null )
	{
		into ??= new Dictionary<string, float>( StringComparer.Ordinal );
		into.Clear();

		if ( drivers is not { Count: > 0 } || skeleton is null || local is null )
			return into;

		Xform[] world = null;

		foreach ( var d in drivers )
		{
			if ( d is null || !d.Enabled || string.IsNullOrWhiteSpace( d.Morph ) )
				continue;

			if ( d.Channel == DriverChannel.Distance )
				world ??= RigAnimFile.ModelSpace( skeleton, local );

			var w = d.Evaluate( skeleton, local, world );
			into[d.Morph] = into.TryGetValue( d.Morph, out var before ) ? before * w : w;
		}

		return into;
	}

	/// <summary>What is wrong with a driver against a skeleton, one sentence each with the fix.
	/// Empty when it will evaluate.</summary>
	public List<string> Problems( Skeleton skeleton )
	{
		var list = new List<string>();

		if ( string.IsNullOrWhiteSpace( Morph ) )
			list.Add( "The driver names no morph, so it drives nothing. Set the morph (shape key) name." );

		if ( string.IsNullOrWhiteSpace( Bone ) )
			list.Add( "The driver names no bone to read. Set Bone." );
		else if ( skeleton is not null && skeleton.IndexOf( Bone ) < 0 )
			list.Add( $"Bone '{Bone}' is not in the skeleton, so '{Morph}' stays at 0. Pick one of the rig's bones." );

		if ( Channel == DriverChannel.Distance )
		{
			if ( string.IsNullOrWhiteSpace( OtherBone ) )
				list.Add( "A Distance driver needs a second bone to measure to. Set Other bone." );
			else if ( skeleton is not null && skeleton.IndexOf( OtherBone ) < 0 )
				list.Add( $"Other bone '{OtherBone}' is not in the skeleton. Pick one of the rig's bones." );
		}

		if ( MathF.Abs( Target - Rest ) < 1e-4f )
			list.Add( $"Rest and Target are both {N( Rest )}, so the morph snaps fully on at that reading. Set Target to the pose the shape was sculpted in." );

		return list;
	}

	// --- .ctrlrig JSON -------------------------------------------------------------------------

	/// <summary>The MorphDrivers of a .ctrlrig file (Marionette's RigDocument.MorphDrivers), in order.</summary>
	public static List<MorphDriver> ReadRig( string json )
	{
		var list = new List<MorphDriver>();

		if ( string.IsNullOrWhiteSpace( json ) )
			return list;

		using var doc = JsonDocument.Parse( json );

		if ( !doc.RootElement.TryGetProperty( "MorphDrivers", out var array ) || array.ValueKind != JsonValueKind.Array )
			return list;

		foreach ( var e in array.EnumerateArray() )
		{
			if ( e.ValueKind != JsonValueKind.Object )
				continue;

			list.Add( new MorphDriver
			{
				Morph = Str( e, "Morph" ) ?? "",
				Bone = Str( e, "Bone" ) ?? "",
				OtherBone = Str( e, "OtherBone" ) ?? "",
				Channel = Enum<DriverChannel>( e, "Channel", DriverChannel.Rotation ),
				Axis = Enum<DriverAxis>( e, "Axis", DriverAxis.Y ),
				Rest = Num( e, "Rest", 0f ),
				Target = Num( e, "Target", 90f ),
				Ease = Enum<RigEase>( e, "Interpolation", RigEase.Smooth ),
				Clamp = Bool( e, "Clamp", true ),
				Value = Num( e, "Value", 1f ),
				Enabled = Bool( e, "Enabled", true ),
			} );
		}

		return list;
	}

	/// <summary>This driver as RigDocument stores it - enums by name, the way s&amp;box writes them.</summary>
	public JsonObject ToJson() => new()
	{
		["Morph"] = Morph,
		["Bone"] = Bone,
		["Channel"] = Channel.ToString(),
		["Axis"] = Axis.ToString(),
		["OtherBone"] = OtherBone ?? "",
		["Rest"] = Rest,
		["Target"] = Target,
		["Interpolation"] = Ease.ToString(),
		["Clamp"] = Clamp,
		["Value"] = Value,
		["Enabled"] = Enabled,
	};

	/// <summary>
	/// A .ctrlrig with <paramref name="drivers"/> in its MorphDrivers: an existing driver on one of
	/// these morphs is replaced, any other is kept, and every other field of the rig (IK, limits,
	/// soft colliders) is left exactly as it was. <paramref name="existing"/> null or empty starts a
	/// new rig for <paramref name="sourceModel"/>.
	/// </summary>
	public static string MergeIntoRig( string existing, IReadOnlyList<MorphDriver> drivers, string sourceModel = null )
	{
		var root = string.IsNullOrWhiteSpace( existing ) ? new JsonObject() : JsonNode.Parse( existing ) as JsonObject ?? new JsonObject();

		if ( !string.IsNullOrWhiteSpace( sourceModel ) && root["SourceModel"] is null )
			root["SourceModel"] = sourceModel;

		var ours = new HashSet<string>( drivers.Select( d => d.Morph ), StringComparer.Ordinal );
		var array = new JsonArray();

		if ( root["MorphDrivers"] is JsonArray old )
		{
			foreach ( var node in old )
			{
				var morph = node?["Morph"]?.GetValue<string>();

				if ( node is not null && (morph is null || !ours.Contains( morph )) )
					array.Add( node.DeepClone() );
			}
		}

		foreach ( var d in drivers )
			array.Add( d.ToJson() );

		root["MorphDrivers"] = array;
		return root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>
	/// Merge <paramref name="drivers"/> into the .ctrlrig at <paramref name="path"/> (<see cref="MergeIntoRig"/>),
	/// creating it for <paramref name="sourceModel"/> when it does not exist. The rig Marionette and
	/// the game's player read the correctives from. Returns false, writing nothing, when there are none.
	/// </summary>
	public static bool WriteRigFile( string path, IReadOnlyList<MorphDriver> drivers, string sourceModel )
	{
		if ( drivers is not { Count: > 0 } )
			return false;

		var existing = System.IO.File.Exists( path ) ? System.IO.File.ReadAllText( path ) : null;
		System.IO.File.WriteAllText( path, MergeIntoRig( existing, drivers, sourceModel ) );
		return true;
	}

	/// <summary>The drivers riding a mesh's shape keys (<see cref="MeshMorph.Driver"/>), named for their morph.</summary>
	public static List<MorphDriver> FromMesh( PolyMesh mesh ) =>
		mesh?.ValidMorphs.Where( m => m.Driver is not null ).Select( m =>
		{
			var d = m.Driver.Clone();
			d.Morph = m.Name;
			return d;
		} ).ToList() ?? new List<MorphDriver>();

	static string N( float v ) => v.ToString( "0.##", CultureInfo.InvariantCulture );

	static string Str( JsonElement e, string name ) =>
		e.TryGetProperty( name, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	static float Num( JsonElement e, string name, float fallback ) =>
		e.TryGetProperty( name, out var v ) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

	static bool Bool( JsonElement e, string name, bool fallback ) =>
		!e.TryGetProperty( name, out var v ) ? fallback : v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : fallback;

	/// <summary>By name or by ordinal; a value this reader does not know is the fallback.</summary>
	static T Enum<T>( JsonElement e, string name, T fallback ) where T : struct, System.Enum
	{
		if ( !e.TryGetProperty( name, out var v ) )
			return fallback;

		if ( v.ValueKind == JsonValueKind.Number && v.TryGetInt32( out var n ) && System.Enum.IsDefined( typeof( T ), n ) )
			return (T)(object)n;

		if ( v.ValueKind == JsonValueKind.String && System.Enum.TryParse<T>( (v.GetString() ?? "").Replace( " ", "" ), true, out var parsed ) )
			return parsed;

		return fallback;
	}
}
