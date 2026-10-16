using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// The parts a body-horror creature is assembled from — heads, hands, boots, limbs, gut coils,
/// tendril sprays, spikes — and the three concepts built from them. Everything is in s&amp;box
/// space: x forward, y left, z up, inches, ground at z = 0.
/// </summary>
public static partial class CreatureGen
{
	public const int BootMat = 10, HatMat = 11;

	/// <summary>A local frame: origin, forward, left, up. Parts are built in local space and
	/// placed through one of these.</summary>
	public readonly struct Frame
	{
		public readonly Vec3 Origin, Forward, Left, Up;
		public readonly float Scale;

		public Frame( Vec3 origin, Vec3 forward, Vec3 up, float scale = 1f )
		{
			Origin = origin;
			Forward = forward.Normal;
			var left = Vec3.Cross( up, Forward );
			if ( left.LengthSquared < 1e-6f )
				Orthonormal( Forward, out left, out _ );
			Left = left.Normal;
			Up = Vec3.Cross( Forward, Left ).Normal;
			Scale = scale;
		}

		public Vec3 P( float x, float y, float z ) => Origin + (Forward * x + Left * y + Up * z) * Scale;
		public Vec3 P( Vec3 l ) => P( l.x, l.y, l.z );
		public Vec3 D( float x, float y, float z ) => (Forward * x + Left * y + Up * z).Normal;
		public float S( float r ) => r * Scale;
		public Xform Rotation => new( Forward, Left, Up, Vec3.Zero );

		public Frame Moved( Vec3 localOffset ) => new( P( localOffset ), Forward, Up, Scale );
		public Frame Turned( Vec3 axis, float degrees ) =>
			new( Origin, Rotate( Forward, axis, degrees ), Rotate( Up, axis, degrees ), Scale );
	}

	// --- human parts ----------------------------------------------------------------------------

	/// <summary>
	/// A middle-aged man's head, 9.5 in tall at scale 1: brow ridge, deep sockets, nose with a
	/// bridge, cheekbones, lips as a rolled rim, jaw line, ears, hair mass. Built so the smear
	/// options can wreck it: <paramref name="smear"/> widens the face sideways,
	/// <paramref name="droop"/> pulls the lower face down, <paramref name="eyes"/> is how many
	/// eyes to scatter when more than two, <paramref name="jawDrop"/> hangs the jaw open.
	/// The mouth is a real cavity with teeth. Frame origin is the skull centre.
	/// </summary>
	public static Sdf Head( Random rng, Frame f, string tag, int bone = -1, float smear = 1f, float droop = 0f, int eyes = 2,
		float jawDrop = 0f, bool hair = true, float mouthWidth = 1f, bool eyelidsShut = false )
	{
		var skull = new SdfBlend( f.S( 0.7f ) );
		var rot = f.Rotation;

		Sdf E( Vec3 c, Vec3 r, int mat ) => new SdfEllipsoid( f.P( new Vec3( c.x, c.y * smear, c.z ) ), new Vec3( f.S( r.x ), f.S( r.y * smear ), f.S( r.z ) ), rot ) { Material = mat, Tag = tag, Bone = bone };
		Sdf S( Vec3 c, float r, int mat ) => new SdfSphere( f.P( new Vec3( c.x, c.y * smear, c.z ) ), f.S( r ) ) { Material = mat, Tag = tag, Bone = bone };
		Sdf C( Vec3 a, float ra, Vec3 b2, float rb, int mat ) => new SdfRoundCone( f.P( new Vec3( a.x, a.y * smear, a.z ) ), f.S( ra ), f.P( new Vec3( b2.x, b2.y * smear, b2.z ) ), f.S( rb ) ) { Material = mat, Tag = tag, Bone = bone };

		var jd = droop + jawDrop;
		Sdf T( int mat, float r0, float r1, params Vec3[] pts ) =>
			new SdfTube( pts.Select( q => f.P( new Vec3( q.x, q.y * smear, q.z ) ) ).ToArray(),
				pts.Select( ( _, i ) => f.S( r0 + (r1 - r0) * MathF.Sin( MathF.PI * i / Math.Max( 1, pts.Length - 1 ) ) ) ).ToArray() ) { Material = mat, Tag = tag, Bone = bone };

		// Cranium and face masses.
		skull.Add( E( new Vec3( -0.5f, 0, 0.9f ), new Vec3( 3.9f, 3.15f, 3.6f ), SkinMat ) );                 // cranium
		skull.Add( E( new Vec3( 1.5f, 0, 0.6f ), new Vec3( 2.4f, 2.7f, 2.4f ), SkinMat ) );                    // forehead/upper face
		skull.Add( E( new Vec3( 1.9f, 0, -1.4f - droop * 0.5f ), new Vec3( 2.1f, 2.3f, 1.9f ), SkinMat ) );    // muzzle
		skull.Add( E( new Vec3( 2.0f, 0, -3.5f - jd ), new Vec3( 1.7f, 1.5f, 1.15f ), SkinMat ) );              // chin
		skull.Add( T( SkinMat, 0.85f, 1.0f, new Vec3( -0.3f, 3.0f, -0.2f ), new Vec3( 1.0f, 2.6f, -2.4f - jd * 0.7f ), new Vec3( 2.3f, 1.2f, -3.6f - jd ), new Vec3( 2.5f, 0, -3.8f - jd ), new Vec3( 2.3f, -1.2f, -3.6f - jd ), new Vec3( 1.0f, -2.6f, -2.4f - jd * 0.7f ), new Vec3( -0.3f, -3.0f, -0.2f ) ) ); // jaw line
		// Brow: two arcs with a dip between them.
		skull.Add( T( SkinMat, 0.45f, 0.62f, new Vec3( 3.25f, 0.3f, 1.55f ), new Vec3( 3.25f, 1.3f, 1.8f ), new Vec3( 2.9f, 2.4f, 1.55f ) ) );
		skull.Add( T( SkinMat, 0.45f, 0.62f, new Vec3( 3.25f, -0.3f, 1.55f ), new Vec3( 3.25f, -1.3f, 1.8f ), new Vec3( 2.9f, -2.4f, 1.55f ) ) );
		skull.Add( E( new Vec3( 2.6f, 2.35f, -0.1f - droop * 0.4f ), new Vec3( 0.7f, 0.75f, 0.55f ), SkinMat ) );       // cheekbones
		skull.Add( E( new Vec3( 2.6f, -2.35f, -0.1f - droop * 0.4f ), new Vec3( 0.7f, 0.75f, 0.55f ), SkinMat ) );
		// Nose: bridge, tip, wings.
		skull.Add( C( new Vec3( 3.3f, 0, 1.25f ), 0.38f, new Vec3( 4.25f, 0, -0.55f - droop * 0.5f ), 0.55f, SkinMat ) );
		skull.Add( S( new Vec3( 4.15f, 0, -0.7f - droop * 0.5f ), 0.55f, SkinMat ) );
		skull.Add( S( new Vec3( 3.8f, 0.62f, -0.85f - droop * 0.5f ), 0.42f, SkinMat ) );
		skull.Add( S( new Vec3( 3.8f, -0.62f, -0.85f - droop * 0.5f ), 0.42f, SkinMat ) );
		// Nasolabial folds: from the nose wings round the mouth corners.
		skull.Add( T( SkinMat, 0.28f, 0.34f, new Vec3( 3.6f, 0.9f, -0.9f - droop * 0.5f ), new Vec3( 3.4f, 1.55f, -1.9f - droop ), new Vec3( 3.0f, 1.8f, -2.9f - jd ) ) );
		skull.Add( T( SkinMat, 0.28f, 0.34f, new Vec3( 3.6f, -0.9f, -0.9f - droop * 0.5f ), new Vec3( 3.4f, -1.55f, -1.9f - droop ), new Vec3( 3.0f, -1.8f, -2.9f - jd ) ) );
		skull.Add( E( new Vec3( -0.4f, 3.55f, 0.2f ), new Vec3( 0.55f, 0.35f, 1.4f ), SkinMat ) );               // ears
		skull.Add( E( new Vec3( -0.4f, -3.55f, 0.2f ), new Vec3( 0.55f, 0.35f, 1.4f ), SkinMat ) );
		skull.Add( C( new Vec3( -0.2f, 0, -3.4f ), 1.9f, new Vec3( -0.9f, 0, -8.5f ), 2.2f, SkinMat ) );          // neck
		skull.Add( C( new Vec3( 1.2f, 1.2f, -5.2f ), 0.5f, new Vec3( -1.2f, 2.2f, -1.6f ), 0.45f, SkinMat ) );     // neck cords
		skull.Add( C( new Vec3( 1.2f, -1.2f, -5.2f ), 0.5f, new Vec3( -1.2f, -2.2f, -1.6f ), 0.45f, SkinMat ) );
		skull.Add( S( new Vec3( 1.5f, 0, -5.4f ), 0.6f, SkinMat ) );                                          // larynx

		// Mouth: lips as curved tubes in the skull itself so they merge into the face.
		var mouthC = new Vec3( 3.45f, 0, -2.05f - droop - jawDrop * 0.5f );
		var mw = 1.25f * mouthWidth;
		var open = 0.12f + jawDrop * 0.85f;
		skull.Add( T( GumMat, 0.24f, 0.36f, mouthC + new Vec3( -0.5f, -mw, 0.05f ), mouthC + new Vec3( 0.15f, -mw * 0.5f, open + 0.15f ), mouthC + new Vec3( 0.3f, 0, open + 0.12f ), mouthC + new Vec3( 0.15f, mw * 0.5f, open + 0.15f ), mouthC + new Vec3( -0.5f, mw, 0.05f ) ) );
		skull.Add( T( GumMat, 0.26f, 0.42f, mouthC + new Vec3( -0.5f, -mw, -0.05f ), mouthC + new Vec3( 0.1f, -mw * 0.5f, -open - 0.2f ), mouthC + new Vec3( 0.25f, 0, -open - 0.25f ), mouthC + new Vec3( 0.1f, mw * 0.5f, -open - 0.2f ), mouthC + new Vec3( -0.5f, mw, -0.05f ) ) );

		if ( hair )
			skull.Add( new SdfDisplace( E( new Vec3( -1.5f, 0, 2.1f ), new Vec3( 3.4f, 3.25f, 2.6f ), HairMat ), f.S( 0.25f ), 1.6f / f.Scale, 2, rng.Next() ) );

		var cut = new SdfSubtract( skull, f.S( 0.3f ) );
		var after = new SdfBlend( f.S( 0.18f ), cut );

		// Eye sockets under the brow, eyeballs set back in them, lids as arcs over them.
		var eyeSpots = new List<Vec3> { new( 3.25f, 1.3f, 0.75f ), new( 3.25f, -1.3f, 0.75f ) };
		for ( var i = 2; i < eyes; i++ )
		{
			// Extra eyes stay on the face plane, crowding where they do not belong.
			var y = Rand( rng, -2.1f, 2.1f );
			var z = Rand( rng, -1.4f, 2.4f );
			eyeSpots.Add( new Vec3( 3.1f - MathF.Abs( y ) * 0.25f, y, z ) );
		}

		foreach ( var e in eyeSpots )
		{
			cut.Add( S( e + new Vec3( 0.1f, 0, 0 ), 0.78f, CavityMat ) );
			if ( eyelidsShut )
				after.Add( T( SkinMat, 0.2f, 0.32f, e + new Vec3( -0.1f, -0.75f, 0.05f ), e + new Vec3( 0.25f, 0, -0.05f ), e + new Vec3( -0.1f, 0.75f, 0.05f ) ) );
			else
			{
				after.Add( S( e - new Vec3( 0.12f, 0, 0 ), 0.6f, EyeMat ) );
				after.Add( T( SkinMat, 0.16f, 0.22f, e + new Vec3( -0.15f, -0.7f, 0.05f ), e + new Vec3( 0.15f, 0, 0.42f ), e + new Vec3( -0.15f, 0.7f, 0.05f ) ) );   // upper lid
				after.Add( T( SkinMat, 0.13f, 0.18f, e + new Vec3( -0.15f, -0.65f, -0.1f ), e + new Vec3( 0.1f, 0, -0.45f ), e + new Vec3( -0.15f, 0.65f, -0.1f ) ) ); // lower lid
			}
		}

		// The mouth cavity, then blunt human teeth set in it.
		cut.Add( E( mouthC + new Vec3( -0.2f, 0, 0 ), new Vec3( 1.1f, mw * 0.92f, open + 0.12f ), CavityMat ) );

		var teeth = Math.Max( 6, (int)(8 * mouthWidth) );
		for ( var i = 0; i < teeth; i++ )
		{
			var t = (float)i / (teeth - 1);
			var y = (-0.8f + 1.6f * t) * mw;
			var x = mouthC.x + 0.05f - 0.75f * MathF.Pow( MathF.Abs( -1f + 2f * t ), 1.6f );
			var w = i == teeth / 2 || i == teeth / 2 - 1 ? 0.24f : 0.2f;
			after.Add( new SdfEllipsoid( f.P( new Vec3( x, y * smear, mouthC.z + open * 0.75f ) ), new Vec3( f.S( 0.14f ), f.S( w ), f.S( 0.3f ) ), rot ) { Material = BoneMat, Tag = tag, Bone = bone } );
			if ( jawDrop > 0.2f )
				after.Add( new SdfEllipsoid( f.P( new Vec3( x - 0.05f, y * smear, mouthC.z - open * 0.75f ) ), new Vec3( f.S( 0.13f ), f.S( w * 0.95f ), f.S( 0.26f ) ), rot ) { Material = BoneMat, Tag = tag, Bone = bone } );
		}

		return after;
	}

	/// <summary>
	/// A hand, 7.5 in long at scale 1: wrist at the frame origin, fingers along forward, back of
	/// the hand facing up. Knuckle rows stand proud, fingers bend at every joint by
	/// <paramref name="curl"/>, the thumb opposes. <paramref name="fingerScale"/> stretches the
	/// fingers; <paramref name="fingers"/> more than five is the point. <paramref name="planted"/>
	/// flattens it onto the floor with fingertips pressing.
	/// </summary>
	public static Sdf Hand( Random rng, Frame f, string tag, int bone = -1, int fingers = 5, float fingerScale = 1f, float curl = 25f, float spread = 1f, int material = SkinMat, bool planted = false, bool fist = false )
	{
		var hand = new SdfBlend( f.S( 0.22f ) );
		// Palm: thick at the heel, thinner at the knuckles, with the thenar pad under the thumb.
		hand.Add( new SdfEllipsoid( f.P( 2.0f, 0, 0.1f ), new Vec3( f.S( 2.2f ), f.S( 1.85f * spread ), f.S( 0.85f ) ), f.Rotation ) { Material = material, Tag = tag, Bone = bone } );
		hand.Add( new SdfEllipsoid( f.P( 1.1f, 1.2f * spread, -0.35f ), new Vec3( f.S( 1.4f ), f.S( 0.9f ), f.S( 0.65f ) ), f.Rotation ) { Material = material, Tag = tag, Bone = bone } ); // thenar
		hand.Add( new SdfEllipsoid( f.P( 1.4f, -1.4f * spread, -0.3f ), new Vec3( f.S( 1.3f ), f.S( 0.6f ), f.S( 0.55f ) ), f.Rotation ) { Material = material, Tag = tag, Bone = bone } ); // hypothenar
		hand.Add( new SdfRoundCone( f.P( 0.3f, 0, 0 ), f.S( 1.15f ), f.P( -1.5f, 0, 0 ), f.S( 1.2f ) ) { Material = material, Tag = tag, Bone = bone } ); // wrist
		hand.Add( new SdfSphere( f.P( -0.6f, 1.1f * spread, 0.35f ), f.S( 0.45f ) ) { Material = material, Tag = tag, Bone = bone } ); // wrist bone

		var lengths = new[] { 1.75f, 1.2f, 0.95f };
		for ( var i = 0; i < fingers; i++ )
		{
			var t = fingers == 1 ? 0.5f : (float)i / (fingers - 1);
			var y = (-1.75f + 3.5f * t) * spread;
			// Middle finger longest, little shortest.
			var lenMul = (0.78f + 0.36f * MathF.Sin( (0.12f + 0.76f * t) * MathF.PI )) * fingerScale;
			var p = f.P( 3.7f, y, 0.05f );
			var dir = f.D( 1f, (y / 4f) * (planted ? 0.6f : 0.25f), planted ? 0.15f : 0f );
			var r = f.S( 0.4f );
			var bend = fist ? 85f : curl + Rand( rng, -8f, 8f );
			if ( planted ) bend = 10f;

			// Knuckle: a knob proud of the back of the hand.
			hand.Add( new SdfSphere( p + f.Up * f.S( 0.18f ) - dir * f.S( 0.1f ), r * 0.82f ) { Material = material, Tag = tag, Bone = bone } );

			for ( var j = 0; j < lengths.Length; j++ )
			{
				var q = p + dir * f.S( lengths[j] * lenMul );
				hand.Add( new SdfRoundCone( p, r, q, r * 0.9f ) { Material = material, Tag = tag, Bone = bone } );
				if ( j < 2 )
					hand.Add( new SdfSphere( q + f.Up * r * 0.25f, r * 0.78f ) { Material = material, Tag = tag, Bone = bone } ); // joint knob
				p = q;
				r *= 0.9f;
				dir = Rotate( dir, f.Left, bend * (j == 0 ? 1f : 1.2f) );
			}
			hand.Add( new SdfEllipsoid( p + dir * r * 0.4f + f.Up * r * 0.3f, new Vec3( r * 0.9f, r * 0.7f, r * 0.3f ), new Xform( dir, f.Left, Vec3.Cross( dir, f.Left ).Normal, Vec3.Zero ) ) { Material = BoneMat, Tag = tag, Bone = bone } ); // nail
		}

		// Thumb: from the thenar, angled across, two segments, opposing.
		{
			var p = f.P( 1.4f, 1.9f * spread, -0.25f );
			var dir = f.D( 0.6f, 0.8f, -0.25f );
			var r = f.S( 0.6f );
			hand.Add( new SdfSphere( p, r * 1.1f ) { Material = material, Tag = tag, Bone = bone } );
			foreach ( var l in new[] { 1.5f, 1.15f } )
			{
				var q = p + dir * f.S( l * fingerScale );
				hand.Add( new SdfRoundCone( p, r, q, r * 0.85f ) { Material = material, Tag = tag, Bone = bone } );
				hand.Add( new SdfSphere( q, r * 0.95f ) { Material = material, Tag = tag, Bone = bone } );
				p = q;
				r *= 0.85f;
				dir = Rotate( dir, f.Up, fist ? -70f : -35f );
				dir = Rotate( dir, f.Forward, 20f );
			}
		}

		return hand;
	}

	/// <summary>A work boot, 12 x 4.5 x 5 in: heel at the frame origin, toe along forward, a
	/// 1-in sole lip, a heel block, an ankle shaft with a cuff.</summary>
	public static Sdf Boot( Frame f, string tag, int bone = -1 )
	{
		var boot = new SdfBlend( f.S( 0.35f ) );
		Sdf E( Vec3 c, Vec3 r, int mat ) => new SdfEllipsoid( f.P( c ), new Vec3( f.S( r.x ), f.S( r.y ), f.S( r.z ) ), f.Rotation ) { Material = mat, Tag = tag, Bone = bone };

		boot.Add( E( new Vec3( 5.5f, 0, 2.2f ), new Vec3( 6.2f, 2.3f, 1.8f ), BootMat ) );        // upper
		boot.Add( E( new Vec3( 9.8f, 0, 2.0f ), new Vec3( 2.6f, 2.2f, 1.9f ), BootMat ) );        // toe box
		boot.Add( E( new Vec3( 1.6f, 0, 3.0f ), new Vec3( 2.6f, 2.3f, 2.4f ), BootMat ) );        // heel counter
		boot.Add( new SdfRoundCone( f.P( 1.8f, 0, 3.0f ), f.S( 2.3f ), f.P( 1.4f, 0, 8.0f ), f.S( 2.5f ) ) { Material = BootMat, Tag = tag, Bone = bone } ); // shaft
		boot.Add( E( new Vec3( 1.4f, 0, 8.2f ), new Vec3( 2.8f, 2.8f, 0.7f ), BootMat ) );       // cuff
		boot.Add( E( new Vec3( 3.4f, 0, 5.0f ), new Vec3( 2.2f, 1.6f, 2.0f ), BootMat ) );        // tongue/instep
		// The lacing: a raised ridge up the instep with eyelet bumps either side, so it reads as a boot.
		boot.Add( new SdfRoundCone( f.P( 7.2f, 0, 3.6f ), f.S( 0.4f ), f.P( 2.6f, 0, 7.0f ), f.S( 0.45f ) ) { Material = BootMat, Tag = tag, Bone = bone } );
		for ( var k = 0; k < 4; k++ )
			foreach ( var side in new[] { 1f, -1f } )
				boot.Add( new SdfSphere( f.P( 6.6f - 1.2f * k, side * 1.0f, 3.9f + 0.85f * k ), f.S( 0.28f ) ) { Material = SpikeMat, Tag = tag, Bone = bone } );
		// Sole and heel: hard-edged slabs (a soft blend melts them into the upper), the sole a lip
		// 0.4 in proud of the upper all round, the heel a separate 1.5 in block.
		var sole = new SdfBlend( f.S( 0.06f ) );
		sole.Add( new SdfRoundCone( f.P( 9.6f, 0, 0.55f ), f.S( 2.55f ), f.P( 3.0f, 0, 0.55f ), f.S( 2.7f ) ) { Material = SpikeMat, Tag = tag, Bone = bone } );
		sole.Add( new SdfIntersect( E( new Vec3( 1.5f, 0, 0.8f ), new Vec3( 2.5f, 2.5f, 1.4f ), SpikeMat ), new HumanParts.HalfSpace( f.P( 0, 0, 1.5f ), f.Up ), 0.1f ) { Material = SpikeMat, Tag = tag } );
		var flat = new SdfIntersect( sole, new HumanParts.HalfSpace( f.P( 0, 0, 1.15f ), f.Up ), 0.08f ) { Material = SpikeMat, Tag = tag };
		return new SdfBlend( f.S( 0.12f ), boot, new SdfIntersect( flat, new HumanParts.HalfSpace( f.P( 0, 0, -0.01f ), -f.Up ), 0.08f ) { Material = SpikeMat, Tag = tag } );
	}

	/// <summary>A hard hat sitting on a head frame (frame origin = skull centre).</summary>
	public static Sdf HardHat( Frame f, string tag, int bone = -1 )
	{
		var hat = new SdfBlend( f.S( 0.3f ) );
		hat.Add( new SdfEllipsoid( f.P( -0.3f, 0, 2.3f ), new Vec3( f.S( 4.4f ), f.S( 4.0f ), f.S( 3.2f ) ), f.Rotation ) { Material = HatMat, Tag = tag, Bone = bone } );
		hat.Add( new SdfEllipsoid( f.P( 0.6f, 0, 1.6f ), new Vec3( f.S( 5.8f ), f.S( 4.7f ), f.S( 0.9f ) ), f.Rotation ) { Material = HatMat, Tag = tag, Bone = bone } ); // brim: thicker than 2 voxels or it aliases into teeth
		hat.Add( new SdfTube( new[] { f.P( 3.6f, 0, 2.6f ), f.P( 1.5f, 0, 5.0f ), f.P( -1.5f, 0, 5.3f ), f.P( -4.0f, 0, 3.0f ) }, new[] { f.S( 0.35f ), f.S( 0.55f ), f.S( 0.55f ), f.S( 0.35f ) } ) { Material = HatMat, Tag = tag, Bone = bone } ); // ridge
		return new SdfSubtract( hat, f.S( 0.2f ), new SdfEllipsoid( f.P( -0.3f, 0, 1.2f ), new Vec3( f.S( 4.0f ), f.S( 3.6f ), f.S( 3.0f ) ), f.Rotation ) { Material = HatMat, Tag = tag, Bone = bone } );
	}

	/// <summary>
	/// A chain of segments: each entry is a direction and a length, radius tapering from
	/// <paramref name="r0"/> to <paramref name="r1"/>. Returns the end frame, facing along the last
	/// segment. Joints get a knob so an elbow reads as an elbow.
	/// </summary>
	public static Frame Limb( SdfBlend into, Vec3 start, IReadOnlyList<(Vec3 Dir, float Length)> segments, float r0, float r1,
		int material, string tag, int bone = -1, float knob = 1.15f, Vec3? up = null )
	{
		var p = start;
		var n = segments.Count;
		var dir = segments[0].Dir.Normal;

		for ( var i = 0; i < n; i++ )
		{
			var (d, len) = segments[i];
			dir = d.Normal;
			var ra = r0 + (r1 - r0) * ((float)i / n);
			var rb = r0 + (r1 - r0) * ((float)(i + 1) / n);
			var q = p + dir * len;
			into.Add( new SdfRoundCone( p, ra, q, rb ) { Material = material, Tag = tag, Bone = bone } );

			if ( i + 1 < n )
				into.Add( new SdfSphere( q, rb * knob ) { Material = material, Tag = tag, Bone = bone } );

			p = q;
		}

		return new Frame( p, dir, up ?? new Vec3( 0, 0, 1 ) );
	}

	/// <summary>
	/// A limb with anatomy in it: each segment is a muscle belly (a fat ellipsoid in the upper
	/// half), a tendon taper to the joint, and a bony knob at the joint set slightly to the
	/// outside of the bend. <paramref name="stretch"/> lengthens every segment while keeping
	/// the landmarks in proportion, which is how a human arm stays an arm after it has been
	/// pulled to three times its length. The last segment can be solved to end at
	/// <paramref name="endZ"/>, so a foot or a hand lands on the floor.
	/// Segments: direction, length, belly radius (the thickest point), joint radius.
	/// </summary>
	public static Frame AnatomicalLimb( SdfBlend into, Vec3 start, IReadOnlyList<(Vec3 Dir, float Length, float Belly, float Joint)> segments,
		int material, string tag, int bone = -1, float stretch = 1f, float? endZ = null, float sag = 0f, Random rng = null )
	{
		var p = start;
		var n = segments.Count;
		var dir = segments[0].Dir.Normal;
		var prevDir = dir;
		// A stretched limb keeps some of its mass, or it reads as a stick.
		var massScale = MathF.Pow( MathF.Max( stretch, 1f ), 0.4f );

		for ( var i = 0; i < n; i++ )
		{
			var (d, len, belly, joint) = segments[i];
			dir = d.Normal;
			len *= stretch;
			belly *= massScale;

			if ( i == n - 1 && endZ is { } z && MathF.Abs( dir.z ) > 0.05f )
			{
				var need = (z - p.z) / dir.z;
				if ( need > 0f )
					len = Math.Clamp( need, len * 0.3f, len * 4f );
			}

			var q = p + dir * len;
			// Proximal joint radius: the previous segment's joint, or the belly-ish root.
			var jr0 = i == 0 ? MathF.Max( joint * 1.15f, belly * 0.8f ) : segments[i - 1].Joint * 1.05f;
			var jr1 = joint;
			// The distal segment of a limb carries its mass high and tapers hard to the wrist.
			var peakAt = i == 0 ? 0.38f : 0.22f;

			// One continuous profile: joint, swell to the belly, taper to the next joint.
			const int samples = 9;
			var pts = new Vec3[samples];
			var radii = new float[samples];
			Orthonormal( dir, out var sx, out var sy );
			for ( var k = 0; k < samples; k++ )
			{
				var t = (float)k / (samples - 1);
				var swell = t < peakAt ? MathF.Sin( t / peakAt * MathF.PI * 0.5f ) : MathF.Cos( (t - peakAt) / (1f - peakAt) * MathF.PI * 0.5f );
				var baseR = jr0 + (jr1 - jr0) * t;
				radii[k] = baseR + (belly - baseR) * MathF.Pow( MathF.Max( swell, 0f ), 1.3f );
				// A slight S along the segment so it is not a turned spindle.
				pts[k] = p + dir * len * t + sx * MathF.Sin( t * MathF.PI ) * belly * 0.15f;
			}
			into.Add( new SdfTube( pts, radii ) { Material = material, Tag = tag, Bone = bone } );

			// A bony edge along the distal segment (ulna / shin).
			if ( i > 0 )
			{
				var edge = Vec3.Cross( dir, prevDir );
				edge = edge.LengthSquared > 1e-4f ? edge.Normal : sy;
				var e0 = p + dir * len * 0.1f + edge * jr0 * 0.75f;
				var e1 = q - dir * len * 0.08f + edge * jr1 * 0.7f;
				into.Add( new SdfCapsule( e0, e1, MathF.Min( jr0, jr1 ) * 0.38f ) { Material = material, Tag = tag, Bone = bone } );
			}

			// Sag: loose skin hanging under the belly of a stretched limb.
			if ( sag > 0f )
			{
				var at = p + dir * len * peakAt;
				into.Add( new SdfEllipsoid( at + new Vec3( 0, 0, -belly * 0.55f * sag ), new Vec3( len * 0.18f, belly * 0.55f, belly * 0.45f * sag ), new Xform( dir, sx, sy, Vec3.Zero ) ) { Material = material, Tag = tag, Bone = bone } );
			}

			if ( i + 1 < n )
			{
				// The joint: a pointed bony process on the outside of the bend (olecranon,
				// patella), and the pinched skin folds on the inside.
				var next = segments[i + 1].Dir.Normal;
				var outside = dir - next;
				outside = outside.LengthSquared > 1e-4f ? outside.Normal : sx;
				into.Add( new SdfRoundCone( q - outside * jr1 * 0.2f, jr1 * 1.05f, q + outside * jr1 * 1.05f, jr1 * 0.35f ) { Material = material, Tag = tag, Bone = bone } );
				var inside = -outside;
				for ( var f = -1; f <= 1; f += 2 )
				{
					var fp = q + inside * jr1 * 0.75f + (dir + next).Normal * f * jr1 * 0.35f;
					into.Add( new SdfSphere( fp, jr1 * 0.42f ) { Material = material, Tag = tag, Bone = bone } );
				}
			}

			prevDir = dir;
			p = q;
		}

		return new Frame( p, dir, new Vec3( 0, 0, 1 ) );
	}

	/// <summary>Human arm segments for <see cref="AnatomicalLimb"/>: upper arm then forearm,
	/// with the radii of a big man's arm.</summary>
	public static List<(Vec3, float, float, float)> ArmSegments( Vec3 upperDir, Vec3 foreDir, float scale = 1f ) => new()
	{
		(upperDir, 12.5f * scale, 2.6f * scale, 1.9f * scale),
		(foreDir, 11.5f * scale, 2.3f * scale, 1.4f * scale),
	};

	/// <summary>Human leg segments: thigh then shin.</summary>
	public static List<(Vec3, float, float, float)> LegSegments( Vec3 thighDir, Vec3 shinDir, float scale = 1f ) => new()
	{
		(thighDir, 18f * scale, 3.6f * scale, 2.6f * scale),
		(shinDir, 17f * scale, 2.9f * scale, 1.9f * scale),
	};

	/// <summary>Packed intestine-like tubes wandering inside a sphere.</summary>
	public static void GutCoils( SdfBlend into, Random rng, Vec3 center, float region, int count, float radius, string tag, int bone = -1, int material = FleshMat, float stepLen = -1f )
	{
		if ( stepLen < 0f ) stepLen = radius * 2.2f;

		for ( var c = 0; c < count; c++ )
		{
			var points = new List<Vec3>();
			var radii = new List<float>();
			var p = center + RandDir( rng ) * Rand( rng, 0f, region * 0.8f );
			var d = RandDir( rng );
			var steps = rng.Next( 8, 18 );

			for ( var i = 0; i < steps; i++ )
			{
				points.Add( p );
				// Segmented: a bulge every other step, like a gut.
				radii.Add( radius * ((i & 1) == 0 ? Rand( rng, 1.05f, 1.2f ) : Rand( rng, 0.78f, 0.9f )) );
				d = (d + RandDir( rng ) * 0.9f).Normal;
				var q = p + d * stepLen;

				// Stay in the region: bounce off its wall.
				if ( (q - center).Length > region )
				{
					d = (-(q - center).Normal + RandDir( rng ) * 0.5f).Normal;
					q = p + d * stepLen;
				}
				p = q;
			}

			if ( points.Count >= 2 )
				into.Add( new SdfTube( points, radii ) { Material = material, Tag = tag, Bone = bone } );
		}
	}

	/// <summary>A fan of thin ropy tendrils out of a root, arcing along <paramref name="dir"/>
	/// and falling under gravity.</summary>
	public static void TendrilSpray( SdfBlend into, Random rng, Vec3 root, Vec3 dir, float cone, int count,
		float length, float r0, float r1, string tag, int bone = -1, float gravity = 0.25f, float curl = 0.2f, float beads = 0.15f, Vec3? floor = null )
	{
		Orthonormal( dir, out var x, out var y );

		for ( var i = 0; i < count; i++ )
		{
			var a = Rand( rng, 0f, MathF.PI * 2f );
			var spread = Rand( rng, 0f, cone );
			var d = (dir.Normal + (x * MathF.Cos( a ) + y * MathF.Sin( a )) * spread).Normal;
			var len = length * Rand( rng, 0.6f, 1.2f );
			into.Add( Tendril( rng, root + d * r0 * 0.5f, d, len, r0 * Rand( rng, 0.8f, 1.2f ), r1, Math.Max( 6, (int)(len / 2.5f) ), curl, gravity, beads, 1.1f, floor )
				.With( FleshMat, tag, bone ) );
		}
	}

	/// <summary>Long black glossy rods, splayed in a fan.</summary>
	public static void SpikeFan( SdfBlend into, Random rng, Vec3 root, Vec3 dir, float cone, int count, float length, float baseR, string tag, int bone = -1, bool beads = true, SdfBlend collars = null )
	{
		Orthonormal( dir, out var x, out var y );

		for ( var i = 0; i < count; i++ )
		{
			var a = Rand( rng, 0f, MathF.PI * 2f );
			var spread = Rand( rng, cone * 0.3f, cone );
			var d = (dir.Normal + (x * MathF.Cos( a ) + y * MathF.Sin( a )) * spread).Normal;
			var len = length * Rand( rng, 0.6f, 1.25f );
			var points = new List<Vec3>();
			var radii = new List<float>();
			var segs = 5;
			var bend = RandDir( rng ) * 0.12f;

			for ( var s = 0; s <= segs; s++ )
			{
				var t = (float)s / segs;
				var dd = (d + bend * t).Normal;
				points.Add( root + dd * len * t );
				radii.Add( MathF.Max( baseR * (1f - t * 0.85f), 0.25f ) );
			}

			into.Add( new SdfTube( points, radii ) { Material = SpikeMat, Tag = tag, Bone = bone } );

			// A puckered collar of skin where it comes out.
			collars?.Add( new SdfSphere( root + d * baseR * 1.5f, baseR * 2.6f ) { Material = FleshMat, Tag = tag, Bone = bone } );

			// Bead nodes along it.
			for ( var b = 0; b < (beads ? 3 : 0); b++ )
			{
				var t = Rand( rng, 0.2f, 0.9f );
				into.Add( new SdfSphere( root + d * len * t, baseR * (1f - t * 0.6f) * 1.5f ) { Material = SpikeMat, Tag = tag, Bone = bone } );
			}
		}
	}

	/// <summary>A flattened lumpy flesh pool on the ground under a point.</summary>
	public static void FloorCarpet( SdfBlend into, Random rng, Vec3 center, float radius, float thickness, string tag, int bone = -1 )
	{
		into.Add( new SdfEllipsoid( new Vec3( center.x, center.y, thickness * 0.3f ), new Vec3( radius, radius * Rand( rng, 0.7f, 1f ), thickness ) ) { Material = FleshMat, Tag = tag, Bone = bone } );
		Lumps( into, rng, new Vec3( center.x, center.y, thickness * 0.4f ), radius * 0.9f, 14, thickness * 0.6f, thickness * 1.3f, FleshMat, bone, tag );

		for ( var i = 0; i < 10; i++ )
		{
			var a = Rand( rng, 0, MathF.PI * 2 );
			var start = new Vec3( center.x + MathF.Cos( a ) * radius * 0.5f, center.y + MathF.Sin( a ) * radius * 0.5f, thickness * 0.8f );
			into.Add( Tendril( rng, start, new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0.05f ), radius * Rand( rng, 0.8f, 1.6f ), thickness * 0.7f, 0.35f, 10, 0.35f, 0.5f, 0.3f, 1f, new Vec3( 0, 0, 0 ) )
				.With( FleshMat, tag, bone ) );
		}
	}

	/// <summary>A vertical gash: a cavity with a rounded lip, lined with inward-curving tooth spikes.</summary>
	public static void Maw( SdfBlend body, SdfSubtract cut, SdfBlend after, Random rng, Vec3 center, Vec3 normal, Vec3 along, float length, float width, float depth, string tag, int bone = -1, int teeth = 24, List<Sdf> cuts = null )
	{
		var n = normal.Normal;
		var a = along.Normal;
		var side = Vec3.Cross( n, a ).Normal;

		// The lip: a rolled tube of flesh around the opening.
		var ring = 24;
		var lipPts = new List<Vec3>();
		var lipR = new List<float>();
		for ( var i = 0; i <= ring; i++ )
		{
			var t = (float)i / ring * MathF.PI * 2f;
			lipPts.Add( center + a * MathF.Cos( t ) * length * 0.5f + side * MathF.Sin( t ) * width * 0.55f + n * depth * 0.12f );
			lipR.Add( width * 0.17f * (1f + 0.25f * MathF.Sin( t * 3f )) );
		}
		body.Add( new SdfTube( lipPts, lipR ) { Material = GumMat, Tag = tag, Bone = bone } );

		// The cavity.
		var cavity = new SdfEllipsoid( center - n * depth * 0.2f, new Vec3( depth * 0.9f, width * 0.45f, length * 0.47f ), new Xform( n, side, a, Vec3.Zero ) ) { Material = CavityMat, Tag = tag, Bone = bone };
		cut?.Add( cavity );
		cuts?.Add( cavity );

		// Teeth: curved in from the rim.
		for ( var i = 0; i < teeth; i++ )
		{
			var t = (float)i / teeth * MathF.PI * 2f + Rand( rng, -0.1f, 0.1f );
			var rim = center + a * MathF.Cos( t ) * length * 0.42f + side * MathF.Sin( t ) * width * 0.42f;
			var inward = (center - rim).Normal;
			var len = Rand( rng, width * 0.22f, width * 0.4f );
			var tip = rim + (inward * 0.75f - n * 0.35f).Normal * len;
			var molar = rng.NextDouble() < 0.4;
			after.Add( new SdfRoundCone( rim + n * 0.3f, molar ? 0.75f : Rand( rng, 0.45f, 0.6f ), tip, molar ? 0.45f : 0.12f ) { Material = BoneMat, Tag = tag, Bone = bone } );
		}
	}

	/// <summary>One human arm and one leg, built anatomically, to check the limb builder.</summary>
	static Sdf LimbTest( Options o )
	{
		var rng = new Random( o.Seed );
		var body = new SdfBlend( 1.2f );
		var hard = new SdfBlend( 0.5f );
		var shoulder = new Vec3( 0, 10, 40 );
		var end = AnatomicalLimb( body, shoulder, ArmSegments( new Vec3( 0.2f, 0.9f, -0.5f ), new Vec3( 0.9f, 0.3f, -0.4f ) ), SkinMat, "arm" );
		hard.Add( Hand( rng, new Frame( end.Origin, end.Forward, new Vec3( 0, 0, 1 ) ), "hand" ) );
		var armS = AnatomicalLimb( body, new Vec3( 0, 30, 40 ), ArmSegments( new Vec3( 0.3f, 0.6f, -0.8f ), new Vec3( 0.8f, 0.1f, -0.7f ) ), SkinMat, "armS", stretch: 2.2f, sag: 0.6f, endZ: 1.5f );
		hard.Add( Hand( rng, new Frame( armS.Origin, armS.Forward, new Vec3( 0, 0, 1 ), 1.3f ), "handS", fingers: 6, fingerScale: 1.7f ) );
		var hip = new Vec3( 0, -14, 38 );
		var leg = AnatomicalLimb( body, hip, LegSegments( new Vec3( 0.25f, 0.1f, -1f ), new Vec3( -0.15f, 0, -1f ) ), ClothMat, "leg", endZ: 4f );
		hard.Add( Boot( new Frame( leg.Origin + new Vec3( -3.5f, 0, -4f ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) ), "boot" ) );
		body.Add( new SdfSphere( new Vec3( 0, 0, 42 ), 9f ) { Material = FleshMat, Tag = "mass" } );
		return new SdfBlend( 0.5f, new SdfDisplace( body, 0.25f, 0.6f, 2, o.Seed ), hard );
	}

	/// <summary>A head, a hand and a boot at voxel scale, to check the human parts read in clay.</summary>
	static Sdf PartsTest( Options o )
	{
		var rng = new Random( o.Seed );
		var all = new SdfBlend( 0.3f );
		all.Add( Head( rng, new Frame( new Vec3( 0, 0, 20 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) ), "head" ) );
		all.Add( Head( rng, new Frame( new Vec3( 0, 16, 20 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) ), "headOpen", smear: 1.4f, droop: 0.8f, jawDrop: 1.6f, mouthWidth: 1.4f, eyes: 4 ) );
		all.Add( Hand( rng, new Frame( new Vec3( -4, -14, 2 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) ), "hand", planted: true ) );
		all.Add( Hand( rng, new Frame( new Vec3( -4, -26, 6 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) ), "fist", fist: true ) );
		all.Add( Boot( new Frame( new Vec3( -6, -40, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) ), "boot" ) );
		return all;
	}
}
