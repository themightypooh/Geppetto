using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Stand-in models for testing Marionette clips with no editor and no s&box: a skeleton and a
/// skinned body, as a <see cref="ModelDump"/> - the same thing the editor's effigy_model_dump writes
/// - so the take check, the motion lint and the stills all run on it unchanged.
///
/// THE BODY IS TUBES. Every bone gets a tapered tube from its head to each child (a stub for a
/// leaf), skinned to the bone with a short blend into its parent at the joint. Crude to look at, but
/// it deforms like a real skin at the joints and it clips like one, which is what a headless check
/// needs. A real model's dump is always the better test when there is one.
///
/// THE BONE AXES ARE DELIBERATELY UNHELPFUL (past the heap). The tentacle's cable bones are rolled 90 degrees each about
/// their own length, so a tool that assumes "+X is along the bone" breaks here before it breaks on
/// a real rig. The humanoid is Citizen's own bind pose, axes and all.
/// </summary>
public static class SimRig
{
	/// <summary>
	/// A heap with <paramref name="cables"/> tendon arms, each <paramref name="bones"/> long and
	/// ending in a hand of three three-bone fingers - the shape of the Rigging creature. Names:
	/// <c>root</c>, <c>heap</c>, <c>cableN_00</c>.., <c>cableN_hand</c>, <c>cableN_fK_0..2</c>.
	/// Cable lengths differ (never twinned), and they droop toward the ground.
	/// </summary>
	public static ModelDump Tentacle( int cables = 5, int bones = 12, float length = 90f, bool fingers = true )
	{
		cables = Math.Clamp( cables, 1, 12 );
		bones = Math.Clamp( bones, 2, 32 );

		var s = new Skeleton();
		var world = new List<Xform>();
		var radius = new List<float>();

		int Add( string name, int parent, Vec3 at, Vec3 along, float roll, float r )
		{
			var basis = Basis( along, roll ).ToXform( at, Vec3.One );
			var local = parent >= 0 ? world[parent].Inverse * basis : basis;
			var index = s.AddBone( name, parent, local );
			world.Add( basis );
			radius.Add( r );
			return index;
		}

		var root = Add( "root", -1, Vec3.Zero, new Vec3( 1, 0, 0 ), 0f, 0f );
		var heapAt = new Vec3( 0, 0, 22 );
		// Root and heap keep the identity rotation, so a clip keying them by hand ("0,0,22" and
		// "0,0,0,1") means what it says. The cables are where the awkward axes live.
		var heap = Add( "heap", root, heapAt, new Vec3( 1, 0, 0 ), 0f, 16f );

		for ( var c = 0; c < cables; c++ )
		{
			var yaw = MathF.PI * 2f * c / cables + 0.3f;
			var outward = new Vec3( MathF.Cos( yaw ), MathF.Sin( yaw ), 0f );
			var cableLength = length * (0.8f + 0.1f * ((c * 7) % 5));
			var step = cableLength / bones;
			var at = heapAt + outward * 13f + new Vec3( 0, 0, 4f );
			var parent = heap;

			for ( var b = 0; b < bones; b++ )
			{
				// Out and down: the first bones leave the heap level, the rest droop to the floor.
				var t = (float)b / bones;
				var along = (outward * (1f - 0.6f * t) + new Vec3( 0, 0, -0.55f * t - 0.1f )).Normal;
				var name = b == bones - 1 ? $"cable{c}_hand" : $"cable{c}_{b:00}";
				var r = MathF.Max( 3.2f * (1f - 0.65f * t), 0.9f );

				parent = Add( name, parent, at, along, b * MathF.PI / 2f, r );
				at += along * step;
			}

			if ( !fingers )
				continue;

			var hand = parent;
			var handDir = world[hand].X.Normal;
			var side = Vec3.Cross( handDir, new Vec3( 0, 0, 1 ) ).Normal;

			for ( var f = 0; f < 3; f++ )
			{
				var spread = (f - 1) * 0.45f;
				var dir = (handDir + side * spread).Normal;
				var fat = world[hand].Origin + handDir * step * 0.9f + side * (f - 1) * 1.2f;
				var fp = hand;

				for ( var k = 0; k < 3; k++ )
				{
					fp = Add( $"cable{c}_f{f}_{k}", fp, fat, dir, 0f, 0.55f );
					fat += dir * 1.8f;
				}
			}
		}

		return Skin( s, world.ToArray(), radius.ToArray(), "sim/tentacle" );
	}

	/// <summary>Citizen's skeleton in its own bind pose, wrapped in tubes - for clips authored on
	/// models/citizen/citizen.vmdl.</summary>
	public static ModelDump Citizen()
	{
		var s = CitizenSkeleton.Build();
		var world = RigAnimFile.ModelSpace( s, s.Bones.Select( b => b.Local ).ToArray() );
		var radius = s.Bones.Select( b => RadiusFor( b.Name ) ).ToArray();
		return Skin( s, world, radius, "models/citizen/citizen.vmdl" );
	}

	/// <summary>A pipe to clamp onto: a reference prop, a cylinder from <paramref name="a"/> to
	/// <paramref name="b"/>.</summary>
	public static ModelDump.Prop Pipe( string name, Vec3 a, Vec3 b, float radius )
	{
		var part = new ModelDump.Part { Name = name, Material = "sim_pipe" };
		Tube( a, b, radius, radius, out var positions, out var triangles );
		part.Positions = positions.ToArray();
		part.Triangles = triangles.ToArray();
		return new ModelDump.Prop { Name = name, Model = "sim/pipe", Meshes = { part } };
	}

	/// <summary>Any skeleton wrapped in tubes - the generic form of the two above.</summary>
	public static ModelDump Skin( Skeleton s, Xform[] world, float[] radius, string model )
	{
		var children = new List<int>[s.Count];

		for ( var i = 0; i < s.Count; i++ )
			children[i] = new List<int>();

		for ( var i = 0; i < s.Count; i++ )
		{
			if ( s.Bones[i].Parent >= 0 )
				children[s.Bones[i].Parent].Add( i );
		}

		var positions = new List<Vec3>();
		var triangles = new List<int>();
		var weights = new List<BoneWeight[]>();

		for ( var i = 0; i < s.Count; i++ )
		{
			if ( radius[i] <= 0f )
				continue;

			var head = world[i].Origin;
			var parent = s.Bones[i].Parent;
			var ends = children[i].Select( c => world[c].Origin ).ToList();

			if ( ends.Count == 0 )
			{
				// A leaf: a stub carrying on the way the bone arrived.
				var arrive = parent >= 0 ? (head - world[parent].Origin) : new Vec3( 0, 0, 1 );
				var len = MathF.Max( arrive.Length * 0.5f, radius[i] );
				ends.Add( head + (arrive.LengthSquared > 1e-8f ? arrive.Normal : new Vec3( 0, 0, 1 )) * len );
			}

			foreach ( var end in ends )
			{
				if ( (end - head).Length < 1e-3f )
					continue;

				var start = positions.Count;
				var endRadius = children[i].Count > 0 ? radius[i] * 0.85f : radius[i] * 0.5f;
				Tube( head, end, radius[i], endRadius, out var p, out var t );

				foreach ( var v in p )
				{
					// Along the tube: the first fifth blends half into the parent, so the joint bends
					// smoothly instead of shearing.
					var along = Vec3.Dot( v - head, (end - head).Normal ) / (end - head).Length;
					var blend = parent >= 0 && radius[parent] > 0f ? Math.Clamp( 0.5f - along * 2.5f, 0f, 0.5f ) : 0f;

					weights.Add( blend > 0f
						? new[] { new BoneWeight( i, 1f - blend ), new BoneWeight( parent, blend ) }
						: new[] { new BoneWeight( i, 1f ) } );
				}

				positions.AddRange( p );
				triangles.AddRange( t.Select( x => x + start ) );
			}
		}

		var skin = new SkinWeights();
		skin.Vertices.AddRange( weights );

		return new ModelDump
		{
			Model = model,
			Skeleton = s,
			BindWorld = world,
			Meshes =
			{
				new ModelDump.Part
				{
					Name = "body", Material = "sim_skin",
					Positions = positions.ToArray(), Triangles = triangles.ToArray(), Weights = skin,
				}
			},
		};
	}

	static float RadiusFor( string bone )
	{
		var n = bone.ToLowerInvariant();

		if ( n.Contains( "ikrule" ) || n.Contains( "hold_" ) || n.Contains( "lid" ) || n.Contains( "eye" ) || n.Contains( "ear" ) || n.Contains( "twist" ) || n.Contains( "ik_" ) )
			return 0f;

		if ( n.Contains( "finger" ) ) return 0.45f;
		if ( n.Contains( "head" ) ) return 4.5f;
		if ( n.Contains( "neck" ) ) return 2.2f;
		if ( n.Contains( "spine" ) || n.Contains( "pelvis" ) ) return 5f;
		if ( n.Contains( "clavicle" ) ) return 2f;
		if ( n.Contains( "arm_upper" ) ) return 2f;
		if ( n.Contains( "arm_lower" ) ) return 1.6f;
		if ( n.Contains( "hand" ) ) return 1.3f;
		if ( n.Contains( "leg_upper" ) ) return 3f;
		if ( n.Contains( "leg_lower" ) ) return 2.2f;
		if ( n.Contains( "ankle" ) || n.Contains( "foot" ) ) return 1.8f;
		if ( n.Contains( "ball" ) || n.Contains( "toe" ) ) return 1.4f;
		return 1f;
	}

	/// <summary>A rotation whose X is <paramref name="along"/>, rolled about it.</summary>
	static Quat Basis( Vec3 along, float roll )
	{
		var x = along.Normal;
		var up = MathF.Abs( x.z ) < 0.95f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var y = Vec3.Cross( up, x ).Normal;
		var z = Vec3.Cross( x, y );
		var aligned = Quat.FromXform( new Xform( x, y, z, Vec3.Zero ) );
		return aligned * Quat.FromAxisAngle( new Vec3( 1, 0, 0 ), roll );
	}

	/// <summary>An open tapered tube, 8 sides, 4 rings.</summary>
	static void Tube( Vec3 a, Vec3 b, float ra, float rb, out List<Vec3> positions, out List<int> triangles )
	{
		const int sides = 8, rings = 4;
		positions = new List<Vec3>();
		triangles = new List<int>();

		var axis = (b - a).Normal;
		var up = MathF.Abs( axis.z ) < 0.95f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var u = Vec3.Cross( axis, up ).Normal;
		var v = Vec3.Cross( axis, u );

		for ( var r = 0; r <= rings; r++ )
		{
			var t = (float)r / rings;
			var centre = Vec3.Lerp( a, b, t );
			var rad = ra + (rb - ra) * t;

			for ( var k = 0; k < sides; k++ )
			{
				var ang = MathF.PI * 2f * k / sides;
				positions.Add( centre + (u * MathF.Cos( ang ) + v * MathF.Sin( ang )) * rad );
			}
		}

		for ( var r = 0; r < rings; r++ )
		{
			for ( var k = 0; k < sides; k++ )
			{
				var i0 = r * sides + k;
				var i1 = r * sides + (k + 1) % sides;
				var i2 = i0 + sides;
				var i3 = i1 + sides;
				triangles.AddRange( new[] { i0, i2, i1, i1, i2, i3 } );
			}
		}
	}
}
