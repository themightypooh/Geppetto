using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Effigy.Tests;

/// <summary>
/// RIGGING: "it hauls itself on tendons." Two or three crew mashed into one low heap that can't
/// walk, dragging itself on five muscle cables that shoot out of their wrist and ankle stumps,
/// taper into white tendon and end in the crewman's own hand. The focal point is the one face
/// left whole, screaming over a toothed mouth where his chest was. Brief:
/// ~/claude-handoff/creature-theme.md. Inches, x forward (the main camera), y left, z up.
///
/// Hero pose: two cables taut to props (a pipe overhead, a rail), one planted on a bare foot,
/// one reeling a rat in, one loose and curling mid-strike. Every cable is tagged cableN (its
/// hand cableN_hand) and its centreline is written to rigging_cables.json, so each can be
/// split off and rigged as a chain in Marionette.
/// </summary>
public static partial class CreatureGen
{
	/// <summary>One cable as built: centreline, radius per point, and where it ends.</summary>
	sealed class RigCable
	{
		public string Name;
		public string Role;
		public List<Vec3> Points = new();
		public List<float> Radii = new();
		public Vec3 Root, RootDir;
		public Frame End;
	}

	static readonly List<RigCable> RigCables = new();

	static Sdf Rigging( Options o )
	{
		var rng = new Random( o.Seed );
		RigCables.Clear();
		var L = new Layers( 2.4f, 1.4f );

		// --- the heap --------------------------------------------------------------------------
		// Low and wide, sagging onto the floor, the heavy side on the right (-y); the cables pull
		// it left (+y). No single smooth dome: the core is only there to be covered by bodies and sacs.
		var core = new SdfBlend( 4f );
		core.Add( new SdfEllipsoid( new Vec3( -3, -7, 10 ), new Vec3( 16, 19, 11 ) ) { Material = FleshMat, Tag = "heap" } );
		core.Add( new SdfEllipsoid( new Vec3( 1, 9, 8 ), new Vec3( 12, 12, 8 ) ) { Material = FleshMat, Tag = "heap" } );
		// It flattens onto the floor: a broad skirt 2–3 in thick spreading past the mass.
		core.Add( new SdfEllipsoid( new Vec3( -2, -3, 1.5f ), new Vec3( 22, 27, 2.6f ) ) { Material = FleshMat, Tag = "heapSkirt" } );
		L.Flesh.Add( core );

		// --- body A: the face man ---------------------------------------------------------------
		// Sunk to the chest at the front, leaning out toward the camera; his chest is the mouth.
		var tA = new Frame( new Vec3( 6, -3, 4 ), new Vec3( 1, 0.05f, 0.22f ), new Vec3( -0.22f, 0, 1 ), 1.2f );
		L.Skin.Add( HumanParts.RealTorso( tA, "torsoA", armStump: 7.5f ) );
		var headA = HumanParts.SeatHead( tA.P( 1.4f, 0, 16.2f ), new Vec3( 1, -0.08f, 0.12f ), new Vec3( -0.12f, 0.05f, 1 ), 1.03f );
		L.Skin.Add( FatedHead( rng, headA, "headA", HeadFate.Howl, neckLength: 0.45f, jawDegrees: 36f ) );
		// A torn work-shirt collar round the base of his neck: the fastest "he was crew" read up there.
		{
			var pts = new List<Vec3>();
			var rad = new List<float>();
			for ( var i = 0; i <= 14; i++ )
			{
				var a = MathF.PI * (-0.95f + 1.9f * i / 14f);
				pts.Add( tA.P( 0.6f + 3.4f * MathF.Cos( a ), 4.6f * MathF.Sin( a ), 14.2f + 0.5f * MathF.Cos( a ) ) );
				rad.Add( 0.42f + 0.12f * MathF.Sin( 3.1f * i ) );
			}
			L.Skin.Add( new SdfTube( pts, rad ) { Material = ClothMat, Tag = "collar" } );
		}

		// --- body B: a hunched back on top --------------------------------------------------------
		// Face down across the top rear, sunk to the shoulder blades: spine and scapulae break the
		// surface, his head and arms are inside.
		var tB = new Frame( new Vec3( -9, 3, 16 ), new Vec3( 0.25f, -0.1f, -1 ), new Vec3( -0.7f, 0.7f, 0.1f ), 1.15f );
		L.Skin.Add( HumanParts.RealTorso( tB, "torsoB", neck: 1.2f ) );
		{
			var spine = new List<Vec3>();
			for ( var z = -7f; z <= 15f; z += 2.2f )
				spine.Add( tB.P( -4.6f - 0.25f * MathF.Sin( (z + 7f) / 22f * MathF.PI ), 0.15f * MathF.Sin( z * 0.4f ), z ) );
			Spine( L.Skin, spine, 1.55f, 0.72f, "spineB" );
			foreach ( var side in new[] { 1f, -1f } )
				L.Skin.Add( new SdfEllipsoid( tB.P( -3.9f, 4.6f * side, 10.5f ), new Vec3( 1.1f, 3.0f, 3.6f ) * 1.15f, new Xform( tB.Forward, tB.Left, tB.Up, Vec3.Zero ) * Xform.Rotate( new Vec3( 1, 0, 0 ), 0.25f * side ) ) { Material = SkinMat, Tag = "scapulaB" } );
		}

		// --- body C: a trousered leg and boot kicking up out of the heavy side --------------------
		{
			var hip = new Vec3( -9, -17, 13 );
			var knee = new Vec3( -15, -27, 27 );
			var ankle = new Vec3( -25.5f, -24, 17.5f );
			L.Skin.Add( HumanParts.RealLegSegment( true, false, hip, knee, 1.15f, "legC", trousers: 0.45f, material: ClothMat ) );
			L.Skin.Add( HumanParts.RealLegSegment( false, false, knee, ankle, 1.15f, "legC", trousers: 0.4f, material: ClothMat ) );
			// Trouser hem bunched over the boot shaft, two folds behind the knee.
			RibArc( L.Skin, ankle + (knee - ankle).Normal * 1.2f, (knee - ankle).Normal, new Vec3( 0, 0, 1 ), 2.4f, 2.4f, 0, 360, 0.55f, ClothMat, "legC", 18 );
			foreach ( var k in new[] { 0.12f, 0.22f } )
			{
				var c = Vec3.Lerp( knee, ankle, k );
				RibArc( L.Skin, c, (ankle - knee).Normal, new Vec3( 1, 0, -0.3f ), 2.5f, 2.6f, 120, 260, 0.38f, ClothMat, "legC", 8 );
			}
			// Boot: sole to the sky behind, toe pointing back and out.
			var bootF = new Frame( ankle + new Vec3( -1.0f, 0.4f, -0.8f ), new Vec3( -0.55f, -0.25f, 0.55f ), (ankle - knee).Normal * -1f, 1.05f );
			var bootFrame = new Frame( ankle - (ankle - knee).Normal * -0.5f + new Vec3( 0, 0, 0 ), (new Vec3( -0.6f, 0.2f, 0.35f )).Normal, (knee - ankle).Normal, 1.05f );
			L.Hard.Add( Boot( new Frame( ankle + (ankle - knee).Normal * 8.2f, bootFrame.Forward, (ankle - knee).Normal * -1f, 1.05f ), "bootC" ) );
		}

		// --- a second, smaller face: upside down and sunk in the heavy side, still in his hard hat
		{
			var h = new Frame( new Vec3( 4, -21, 10.5f ), new Vec3( 0.55f, -0.8f, -0.1f ), new Vec3( 0.1f, 0.15f, -1 ), 0.92f );
			L.Skin.Add( FatedHead( rng, h, "headC", HeadFate.Smear, neckLength: 0.3f, jawDegrees: 14f ) );
			L.Hard.Add( HardHat( new Frame( h.P( 0, 0, 0.3f ), h.Forward, h.Up, 0.92f ), "hatC" ) );
		}

		// Everything so far, as one field to find the heap's skin on (sacs, cords, stumps).
		var heapNow = new SdfBlend( 2.2f, L.Flesh, L.Skin );
		heapNow.Prepare( 0f );
		Vec3 Surface( Vec3 from, Vec3 dir )
		{
			dir = dir.Normal;
			var t = 0f;
			for ( var i = 0; i < 400; i++ )
			{
				if ( heapNow.Eval( from + dir * t ) > 0f ) break;
				t += 0.25f;
			}
			// Back to the zero crossing.
			var a = t - 0.25f;
			var b = t;
			for ( var i = 0; i < 12; i++ )
			{
				var m = 0.5f * (a + b);
				if ( heapNow.Eval( from + dir * m ) > 0f ) b = m; else a = m;
			}
			return from + dir * (0.5f * (a + b));
		}

		// --- the mouth ------------------------------------------------------------------------
		var mouthC = tA.P( 6.0f, 0.3f, 6.2f );
		BuildRiggingMouth( L, rng, mouthC, tA, o );

		// --- the cables -------------------------------------------------------------------------
		// Props first, so the hands have something to close on.
		var pipeC = new Vec3( 6, 30, 75 );
		var pipeAxis = new Vec3( 1, 0.15f, 0 ).Normal;
		const float pipeR = 1.6f;
		PropCylinder( pipeC - pipeAxis * 40f, pipeC + pipeAxis * 40f, pipeR, "pipe" );
		// Flange collars along the pipe so it reads as plant pipe, not a rod.
		foreach ( var s in new[] { -28f, 12f } )
			PropCylinder( pipeC + pipeAxis * (s - 0.6f), pipeC + pipeAxis * (s + 0.6f), pipeR + 0.7f, "pipe" );
		var railC = new Vec3( 2, 74, 36 );
		var railAxis = new Vec3( 1, -0.05f, 0 ).Normal;
		const float railR = 1.0f;
		PropCylinder( railC - railAxis * 46f, railC + railAxis * 46f, railR, "rail" );
		PropCylinder( railC - railAxis * 46f - new Vec3( 0, 0, 18 ), railC + railAxis * 46f - new Vec3( 0, 0, 18 ), 0.8f, "rail" );
		foreach ( var s in new[] { -40f, -8f, 24f } )
			PropCylinder( railC + railAxis * s - new Vec3( 0, 0, 36 ), railC + railAxis * s + new Vec3( 0, 0, 0.5f ), 1.1f, "rail" );

		// Stumps: where each cable leaves a man. (elbow or knee buried in the heap, the outward
		// direction, which limb, and a sleeve or trouser.)
		var heapMid = new Vec3( -2, -2, 11 );
		var stumps = new (string Name, Vec3 From, Vec3 Dir, bool Leg, bool Left, int Sleeve)[]
		{
			("cable0", heapMid + new Vec3( 2, 4, 6 ), new Vec3( 0.15f, 0.55f, 0.82f ), false, true, ClothMat),
			("cable1", heapMid + new Vec3( -2, 6, 0 ), new Vec3( -0.05f, 1f, 0.42f ), false, true, ClothMat),
			("cable2", heapMid + new Vec3( 4, 6, -5 ), new Vec3( 0.75f, 0.62f, 0.12f ), true, true, ClothMat),
			("cable3", heapMid + new Vec3( 6, -8, -5 ), new Vec3( 0.8f, -0.55f, 0.05f ), false, false, ClothMat),
			("cable4", heapMid + new Vec3( -4, -8, 6 ), new Vec3( -0.05f, -0.55f, 0.85f ), false, false, ClothMat),
		};
		var roots = new List<(Vec3 Wrist, Vec3 Dir, float R)>();
		foreach ( var s in stumps )
		{
			var exit = Surface( s.From, s.Dir );
			var dir = s.Dir.Normal;
			// The limb comes out 5–6 in past the skin; elbow/knee 7 in under it.
			var wrist = exit + dir * (s.Leg ? 6.5f : 5.5f);
			var joint = exit - dir * 7f;
			if ( s.Leg )
				L.Skin.Add( HumanParts.RealLegSegment( false, s.Left, joint, wrist, 1.0f, s.Name + "_stump", trousers: 0.45f, material: s.Sleeve ) );
			else
				L.Skin.Add( HumanParts.RealArmSegment( false, s.Left, joint, wrist, 1.25f, s.Name + "_stump", material: SkinMat ) );
			var stumpR = s.Leg ? 1.75f : 1.45f;
			StumpCollar( L, rng, exit, wrist, dir, stumpR, s.Leg, s.Name );
			roots.Add( (wrist, dir, stumpR) );
		}

		// cable0: up to the overhead pipe, taut.
		{
			var (w, d, r) = roots[0];
			var hand = GripFrame( pipeC + pipeAxis * -4f, pipeAxis, pipeR, w, true );
			var path = TautPath( w, d, hand.Origin, hand.Forward, 0.018f );
			BuildCable( L, rng, "cable0", "taut_pipe", path, r * 0.92f, fangs: 26, taut: true );
			GripHand( rng, hand, "cable0_hand", left: true, curl: (55f, 85f, 55f) );
			RigCables[^1].End = hand;
		}
		// cable1: out to the rail, taut.
		{
			var (w, d, r) = roots[1];
			var hand = GripFrame( railC + railAxis * 4f, railAxis, railR, w, true );
			var path = TautPath( w, d, hand.Origin, hand.Forward, 0.022f );
			BuildCable( L, rng, "cable1", "taut_rail", path, r * 0.92f, fangs: 24, taut: true );
			GripHand( rng, hand, "cable1_hand", left: true, curl: (60f, 90f, 60f) );
			RigCables[^1].End = hand;
		}
		// cable2: an ankle stump, arching out and planting its bare foot flat.
		{
			var (w, d, r) = roots[2];
			var foot = new Frame( new Vec3( 44, 30, 0 ), new Vec3( 0.75f, 0.45f, 0 ), new Vec3( 0, 0, 1 ), 1.0f );
			var ankleTop = foot.P( 1.6f, 0, 8.5f );
			var path = CurvePath( new[] { w, w + d * 8f + new Vec3( 0, 0, 5 ), Vec3.Lerp( w, ankleTop, 0.5f ) + new Vec3( 0, 2, 9 ), ankleTop + new Vec3( -5, -2, 6 ), ankleTop } );
			BuildCable( L, rng, "cable2", "planted_foot", path, r * 0.85f, fangs: 14, taut: false );
			L.Skin.Add( RealFoot( rng, foot, "cable2_foot", left: true, toeStretch: 1.1f ) );
			RigCables[^1].End = foot;
		}
		// cable3: out along the floor and back, dragging a rat to the mouth.
		{
			var (w, d, r) = roots[3];
			var ratC = new Vec3( 38, -25, 2.0f );
			var hand = new Frame( ratC + new Vec3( -6.2f, 2.4f, 2.0f ), new Vec3( 0.85f, -0.45f, -0.2f ), new Vec3( 0.15f, -0.1f, 1 ), 1.0f );
			var path = CurvePath( new[] { w, w + d * 7f + new Vec3( 0, 0, -1 ), new Vec3( 26, -10, 2.2f ), new Vec3( 30, -22, 3.5f ), hand.Origin - hand.Forward * 3f, hand.Origin } );
			BuildCable( L, rng, "cable3", "reeling_prey", path, r * 0.9f, fangs: 22, taut: false );
			GripHand( rng, hand, "cable3_hand", left: false, curl: (50f, 75f, 45f) );
			RigCables[^1].End = hand;
			Rat( L, rng, ratC, new Vec3( -0.9f, 0.4f, 0 ), "prey" );
		}
		// cable4: loose, whipping up and over in a curl, hand open to strike.
		{
			var (w, d, r) = roots[4];
			var tip = new Vec3( 26, -40, 44 );
			var hand = new Frame( tip, new Vec3( 0.75f, -0.15f, -0.65f ), new Vec3( 0.55f, 0.1f, 0.8f ), 1.0f );
			var path = CurvePath( new[] { w, w + d * 9f, new Vec3( -14, -34, 46 ), new Vec3( -4, -48, 60 ), new Vec3( 14, -46, 58 ), tip - hand.Forward * 4f, tip } );
			BuildCable( L, rng, "cable4", "strike", path, r * 0.9f, fangs: 16, taut: false );
			GripHand( rng, hand, "cable4_hand", left: false, curl: (12f, 18f, 10f), spread: 1.3f );
			RigCables[^1].End = hand;
		}

		// --- sacs and vein cords between the bodies --------------------------------------------
		FillSacs( L, rng, heapNow, mouthC, roots, o );

		// --- the floor: a puddle out in front, a smear trail behind -----------------------------
		RiggingFloor( L, rng );

		WriteRigCables( o );
		Detail = ( mesh, nearest ) => RiggingDetail( mesh, nearest, o.Seed, roots );
		return L.Assemble( o.Seed, fleshAmp: 0.9f, joinK: 1.8f, cutK: 0.9f );
	}

	// --- mouth -------------------------------------------------------------------------------

	/// <summary>
	/// The mouth where torso A's chest was: a lopsided torn opening about 18 × 12 in, a throat
	/// falling back and down out of sight, thick rolled gums with each tooth in its own bulb,
	/// 30-odd teeth in two staggered rows (fangs, incisors, molars, some missing), tissue strands
	/// across the gap, coils at the back, saliva to the floor, and one flap peeled down.
	/// </summary>
	static void BuildRiggingMouth( Layers L, Random rng, Vec3 mc, Frame tA, Options o )
	{
		var fwd = new Vec3( 1, 0.05f, 0 ).Normal;
		var left = Vec3.Cross( new Vec3( 0, 0, 1 ), fwd ).Normal;
		const float ha = 8.8f, hb = 5.6f;
		// Heavy on the right: the right corner drops and the bottom lip sags; the top is torn into
		// a notch under the chin.
		float Wob( float a ) => 1f + 0.13f * MathF.Sin( 2 * a + 0.7f ) + 0.08f * MathF.Sin( 5 * a + 2.1f ) + 0.05f * MathF.Sin( 9 * a );
		Vec3 Rim( float a, float grow = 0f, float back = 0f )
		{
			var c = MathF.Cos( a );
			var s = MathF.Sin( a );
			var y = (ha * Wob( a ) + grow) * c;
			var z = (hb * Wob( a + 1.3f ) + grow) * s;
			// Right corner (y<0) drops 2 in; the lower lip sags in the middle.
			z -= 2.0f * MathF.Max( 0f, -c ) * MathF.Max( 0f, -c );
			z -= 1.4f * MathF.Max( 0f, -s ) * (1f - c * c);
			// The rim curls back into the body at the corners.
			var x = -2.6f * c * c - back;
			return mc + fwd * x + left * y + new Vec3( 0, 0, z );
		}

		// Cavity: the opening, then a throat that drops back and down into dark.
		L.Cuts.Add( new SdfBlend( 2.2f,
			new SdfEllipsoid( mc - fwd * 2.5f + new Vec3( 0, 0, -0.6f ), new Vec3( 6.5f, ha * 0.92f, hb * 1.05f ), new Xform( fwd, left, new Vec3( 0, 0, 1 ), Vec3.Zero ) ),
			new SdfEllipsoid( mc - fwd * 10f + new Vec3( 0, -1, -2.5f ), new Vec3( 7f, ha * 0.6f, hb * 0.8f ), new Xform( fwd, left, new Vec3( 0, 0, 1 ), Vec3.Zero ) ),
			new SdfEllipsoid( mc - fwd * 16f + new Vec3( 0, -2, -4.5f ), new Vec3( 5f, 3.5f, 3.5f ) ) ) { Material = CavityMat, Tag = "throat" } );

		// The gum roll right round the rim, thick and uneven (1.0–1.7 in radius), heavier below.
		var gums = new SdfBlend( 0.55f );
		var rp = new List<Vec3>();
		var rr = new List<float>();
		for ( var i = 0; i <= 56; i++ )
		{
			var a = 2 * MathF.PI * i / 56;
			rp.Add( Rim( a, 0.6f ) );
			rr.Add( 1.05f + 0.3f * MathF.Sin( 5 * a + 1 ) + 0.18f * MathF.Sin( 11 * a ) + 0.35f * MathF.Max( 0f, -MathF.Sin( a ) ) );
		}
		gums.Add( new SdfTube( rp, rr ) { Material = GumMat, Tag = "gumRoll" } );

		// Teeth: outer row long and mixed (fangs, human incisors, molars), inner row short, set
		// back between them; a few slots left empty and some teeth tilted out of line.
		var teeth = 0;
		foreach ( var row in new[] { 0, 1 } )
		{
			var n = row == 0 ? 22 : 14;
			for ( var i = 0; i < n; i++ )
			{
				var a = 2 * MathF.PI * (i + (row == 1 ? 0.5f : 0f) + Rand( rng, -0.2f, 0.2f )) / n;
				if ( rng.NextDouble() < 0.12 ) continue; // missing
				var upper = MathF.Sin( a ) > 0;
				var mid = MathF.Abs( MathF.Sin( a ) );
				if ( mid < 0.3f && rng.NextDouble() < 0.5 ) continue; // corners mostly bare
				var basePos = Rim( a, row == 0 ? -0.1f : -1.6f, row == 0 ? -0.6f : 1.2f );
				var kind = rng.NextDouble();
				float len, baseR, tipR;
				if ( row == 0 && kind < 0.45 ) { len = Rand( rng, 2.6f, 4.0f ) + 2.2f * mid; baseR = len * Rand( rng, 0.17f, 0.22f ); tipR = 0.04f; }       // fang
				else if ( kind < 0.75 ) { len = Rand( rng, 1.4f, 2.4f ); baseR = Rand( rng, 0.42f, 0.55f ); tipR = baseR * 0.7f; }                          // incisor: blunt chisel
				else { len = Rand( rng, 1.0f, 1.5f ); baseR = Rand( rng, 0.55f, 0.7f ); tipR = baseR * 0.8f; }                                             // molar: a squat block
				var vertical = new Vec3( 0, 0, upper ? -1f : 1f );
				var inward = (mc - basePos);
				inward = (inward - fwd * Vec3.Dot( inward, fwd )).Normal;
				var dir = (vertical * 0.75f + inward * 0.25f + fwd * (row == 0 ? 0.35f : 0.05f) + RandDir( rng ) * 0.22f).Normal;
				Fang( basePos - dir * (baseR * 0.6f), dir, -fwd, len, baseR, tipR, tipR > 0.1f ? Rand( rng, 0f, 0.15f ) : Rand( rng, 0.3f, 0.7f ), BoneMat, "tooth", sides: tipR > 0.1f ? 7 : 10, rings: tipR > 0.1f ? 4 : 7 );
				var bulbR = baseR * Rand( rng, 1.25f, 1.5f ) + 0.15f;
				gums.Add( new SdfEllipsoid( basePos, new Vec3( bulbR * 1.15f, bulbR, bulbR * 0.9f ) ) { Material = GumMat, Tag = "gumBulb" } );
				teeth++;
			}
		}
		Console.WriteLine( $"  mouth teeth: {teeth}" );
		L.Teeth.Add( new SdfDisplace( gums, 0.12f, 0.7f, 2, o.Seed + 7 ) );

		// Tissue strands across the gap (round, 0.6–0.8 in, sagging) and coils packed at the back.
		foreach ( var (a0, a1, r) in new[] { (1.25f, 4.9f, 0.34f), (1.9f, 4.2f, 0.3f), (0.7f, 5.6f, 0.38f) } )
		{
			var p0 = Rim( a0, -1.2f, 1.5f );
			var p1 = Rim( a1, -1.2f, 1.5f );
			var m = Vec3.Lerp( p0, p1, 0.5f ) + new Vec3( 0, 0, -1.8f ) - fwd * 0.8f;
			L.Teeth.Add( new SdfTube( new[] { p0, Vec3.Lerp( p0, m, 0.6f ) + new Vec3( 0, 0, -0.4f ), m, Vec3.Lerp( p1, m, 0.6f ) + new Vec3( 0, 0, -0.4f ), p1 }, new[] { r * 1.4f, r, r * 0.85f, r, r * 1.4f } ) { Material = GumMat, Tag = "mouthStrand" } );
		}
		for ( var i = 0; i < 4; i++ )
		{
			var start = mc - fwd * Rand( rng, 6f, 9f ) + left * Rand( rng, -4f, 4f ) + new Vec3( 0, 0, Rand( rng, -3.5f, -1f ) );
			GutLoop( L.Teeth, rng, start, (left * Rand( rng, -1f, 1f ) + new Vec3( 0, 0, 0.3f )).Normal, Rand( rng, 9f, 13f ), Rand( rng, 0.75f, 1.0f ), "mouthCoil" );
		}

		// The flap: the skin of his belly torn below the right corner and peeled down to hang,
		// thick at the hinge and thin at its torn edge.
		{
			var hinge = Rim( 4.3f, 1.2f );
			var hang = hinge + fwd * 2.5f + new Vec3( 0, 0, -6.5f ) + left * -1.2f;
			L.Skin.Add( new SdfRoundCone( hinge, 1.3f, hang, 0.45f ) { Material = SkinMat, Tag = "flap" } );
			L.Skin.Add( new SdfEllipsoid( Vec3.Lerp( hinge, hang, 0.55f ), new Vec3( 1.0f, 3.6f, 3.4f ), new Xform( fwd, left, new Vec3( 0, 0, 1 ), Vec3.Zero ) ) { Material = SkinMat, Tag = "flap" } );
		}

		// Saliva: thin glassy strings from the upper teeth to the lower gum and from the lower lip
		// to the puddle. Real meshes: they are thinner than the voxels.
		for ( var i = 0; i < 14; i++ )
		{
			var a = Rand( rng, 0.35f, 2.8f );
			var top = Rim( a, -0.8f, -0.2f );
			Vec3 bottom;
			if ( i < 8 )
				bottom = Rim( -a + Rand( rng, -0.3f, 0.3f ), -0.6f, -0.2f );
			else
			{
				var b = Rim( Rand( rng, 3.6f, 5.9f ), 0.4f, -1.2f );
				top = b;
				bottom = new Vec3( b.x + Rand( rng, 1f, 4f ), b.y + Rand( rng, -1f, 1f ), 0.2f );
			}
			var sag = Rand( rng, 0.3f, 1.2f );
			var pts = new List<Vec3>();
			for ( var k = 0; k <= 10; k++ )
			{
				var t = k / 10f;
				pts.Add( Vec3.Lerp( top, bottom, t ) + fwd * (sag * 4f * t * (1f - t)) );
			}
			var r0 = Rand( rng, 0.07f, 0.13f );
			TubeMesh( pts, k => r0 * (1f - 0.45f * MathF.Sin( MathF.PI * k )) + 0.025f, GumMat, "saliva", 6 );
		}
	}

	// --- stumps -------------------------------------------------------------------------------

	/// <summary>
	/// Where a cable leaves a man: the sleeve or trouser cuff a few inches back, then the skin
	/// torn and rolled back from the wrist like a sleeve (a ragged roll 1+ in thick with tatters),
	/// and a cobblestone bruise of raised cells on the stretched skin between.
	/// </summary>
	static void StumpCollar( Layers L, Random rng, Vec3 exit, Vec3 wrist, Vec3 dir, float r, bool leg, string tag )
	{
		Orthonormal( dir, out var u, out var v );
		// The sleeve: an overall/hi-vis sleeve (or trouser) cuff 1.5 in out from the heap, bunched.
		if ( !leg )
		{
			var cuffAt = exit + dir * 1.2f;
			var sleeve = new List<Vec3> { exit - dir * 3f, cuffAt };
			L.Skin.Add( new SdfRoundCone( exit - dir * 3f, r + 0.75f, cuffAt, r + 0.62f ) { Material = ClothMat, Tag = tag + "_sleeve" } );
			Ring( L.Skin, cuffAt, dir, r + 0.55f, 0.36f, ClothMat, tag + "_sleeve" );
			// Two folds bunched against the heap.
			Ring( L.Skin, exit - dir * 0.6f, (dir + u * 0.15f).Normal, r + 0.78f, 0.3f, ClothMat, tag + "_sleeve" );
		}
		// The rolled skin: a ring 0.5 in back from the end, thick and uneven, curled back on itself.
		var rollAt = wrist - dir * 0.9f;
		var pts = new List<Vec3>();
		var rad = new List<float>();
		var ph = Rand( rng, 0f, 6f );
		for ( var i = 0; i <= 20; i++ )
		{
			var a = 2 * MathF.PI * i / 20f;
			var c = MathF.Cos( a );
			var s = MathF.Sin( a );
			var torn = 0.4f * MathF.Sin( 3 * a + ph ) + 0.25f * MathF.Sin( 7 * a + 2 * ph );
			pts.Add( rollAt + (u * c + v * s) * (r + 0.35f + 0.2f * torn) - dir * (0.5f * torn) );
			rad.Add( MathF.Max( 0.32f, 0.55f + 0.22f * torn ) );
		}
		L.Skin.Add( new SdfTube( pts, rad ) { Material = SkinMat, Tag = tag + "_roll" } );
		// Tatters: three or four tongues of skin peeled back down the limb from the roll.
		var tatters = rng.Next( 3, 5 );
		for ( var i = 0; i < tatters; i++ )
		{
			var a = 2 * MathF.PI * (i + Rand( rng, -0.25f, 0.25f )) / tatters;
			var radial = u * MathF.Cos( a ) + v * MathF.Sin( a );
			var p0 = rollAt + radial * (r + 0.45f);
			var p1 = p0 - dir * Rand( rng, 1.5f, 2.6f ) + radial * 0.35f;
			L.Skin.Add( new SdfRoundCone( p0, 0.42f, p1, 0.18f ) { Material = SkinMat, Tag = tag + "_roll" } );
		}
		// Bruise cells: a jittered band of raised cobbles on the stretched skin from the roll back
		// toward the sleeve (or onto the heap), 0.8–1.6 in, getting smaller and sparser at the edge.
		var bruise = new SdfBlend( 0.1f );
		var limbLen = (wrist - exit).Length;
		var pitch = 1.15f;
		for ( var s0 = 1.4f; s0 < limbLen + 3.5f; s0 += pitch * 0.87f )
		{
			var edge = s0 / (limbLen + 3.5f);
			var ringR = s0 < limbLen ? r + 0.05f : r + 1.2f + (s0 - limbLen) * 1.3f;
			var n = (int)(2 * MathF.PI * ringR / pitch);
			var off = Rand( rng, 0f, 1f );
			for ( var k = 0; k < n; k++ )
			{
				if ( edge > 0.6f && rng.NextDouble() < (edge - 0.6f) * 1.6f ) continue;
				var a = 2 * MathF.PI * (k + off + Rand( rng, -0.2f, 0.2f )) / n;
				var radial = u * MathF.Cos( a ) + v * MathF.Sin( a );
				var at = wrist - dir * s0 + radial * ringR;
				var cr = (pitch * 0.5f - 0.08f) * Rand( rng, 0.85f, 1.15f ) * (1f - 0.35f * edge);
				bruise.Add( new SdfEllipsoid( at, new Vec3( cr, cr * 0.85f, cr * 0.42f ), new Xform( Vec3.Cross( radial, dir ).Normal, dir, radial, Vec3.Zero ) ) { Material = FleshMat, Tag = tag + "_bruise" } );
			}
		}
		L.Skin.Add( bruise );
	}

	// --- cable geometry ---------------------------------------------------------------------------

	/// <summary>A taut cable: leaves the stump along its own line, runs dead straight with a slight
	/// catenary sag, and meets the wrist along the hand.</summary>
	static List<Vec3> TautPath( Vec3 from, Vec3 fromDir, Vec3 to, Vec3 toDir, float sag )
	{
		var len = (to - from).Length;
		var pts = new List<Vec3>();
		var p1 = from + fromDir.Normal * (len * 0.06f);
		var p2 = to - toDir.Normal * (len * 0.05f);
		for ( var i = 0; i <= 80; i++ )
		{
			var t = i / 80f;
			var a = Vec3.Lerp( from, p1, t );
			var b = Vec3.Lerp( p1, p2, t );
			var c = Vec3.Lerp( p2, to, t );
			var q = Vec3.Lerp( Vec3.Lerp( a, b, t ), Vec3.Lerp( b, c, t ), t );
			pts.Add( q + new Vec3( 0, 0, -len * sag * 4f * t * (1f - t) ) );
		}
		return pts;
	}

	/// <summary>A loose curve through control points (Catmull-Rom), resampled evenly.</summary>
	static List<Vec3> CurvePath( IReadOnlyList<Vec3> c )
	{
		var dense = new List<Vec3>();
		for ( var i = 0; i < c.Count - 1; i++ )
		{
			var p0 = c[Math.Max( i - 1, 0 )];
			var p1 = c[i];
			var p2 = c[i + 1];
			var p3 = c[Math.Min( i + 2, c.Count - 1 )];
			for ( var k = 0; k < 24; k++ )
			{
				var t = k / 24f;
				var t2 = t * t;
				var t3 = t2 * t;
				dense.Add( (p1 * 2f + (p2 - p0) * t + (p0 * 2f - p1 * 5f + p2 * 4f - p3) * t2 + (p1 * 3f - p0 - p2 * 3f + p3) * t3) * 0.5f );
			}
		}
		dense.Add( c[^1] );
		return Resample( dense, 80 );
	}

	static List<Vec3> Resample( List<Vec3> pts, int n )
	{
		var cum = new List<float> { 0f };
		for ( var i = 1; i < pts.Count; i++ )
			cum.Add( cum[^1] + (pts[i] - pts[i - 1]).Length );
		var outPts = new List<Vec3>();
		var j = 0;
		for ( var k = 0; k <= n; k++ )
		{
			var s = cum[^1] * k / n;
			while ( j < pts.Count - 2 && cum[j + 1] < s ) j++;
			var seg = MathF.Max( cum[j + 1] - cum[j], 1e-5f );
			outPts.Add( Vec3.Lerp( pts[j], pts[j + 1], Math.Clamp( (s - cum[j]) / seg, 0f, 1f ) ) );
		}
		return outPts;
	}

	/// <summary>
	/// One cable along <paramref name="path"/>: red striated muscle at the root (a bundle of
	/// fibres twisted round a core, so the grooves are real geometry), a muscle belly about a
	/// quarter of the way out, tapering through the middle into a pale glossy tendon over the last
	/// third (1 : 0.18), which splits into four slips fanning into the wrist. Hooked fang barbs in
	/// two staggered rows along the underside of the last third, hooked back toward the heap.
	/// </summary>
	static void BuildCable( Layers L, Random rng, string name, string role, List<Vec3> path, float rootR, int fangs, bool taut )
	{
		var n = path.Count;
		var cable = new RigCable { Name = name, Role = role, Root = path[0], RootDir = (path[1] - path[0]).Normal };
		// Frames along the path (parallel transport) for the fibres and barbs.
		var tan = new Vec3[n];
		for ( var i = 0; i < n; i++ )
			tan[i] = (path[Math.Min( i + 1, n - 1 )] - path[Math.Max( i - 1, 0 )]).Normal;
		var nrm = new Vec3[n];
		Orthonormal( tan[0], out nrm[0], out _ );
		for ( var i = 1; i < n; i++ )
		{
			var q = nrm[i - 1] - tan[i] * Vec3.Dot( nrm[i - 1], tan[i] );
			nrm[i] = q.LengthSquared > 1e-8f ? q.Normal : nrm[i - 1];
		}

		float Radius( float t )
		{
			// Muscle: belly at 0.22, narrowing to the musculotendinous junction at 0.62–0.7.
			var tendonR = MathF.Max( 0.34f, rootR * 0.19f );
			if ( t < 0.62f )
			{
				var belly = 1f + 0.16f * MathF.Exp( -MathF.Pow( (t - 0.22f) / 0.12f, 2 ) );
				return rootR * belly * (1f - 0.42f * MathF.Pow( t / 0.62f, 1.4f ));
			}
			if ( t < 0.72f )
			{
				var k = (t - 0.62f) / 0.1f;
				var a = rootR * 0.58f;
				var s = k * k * (3 - 2 * k);
				return a + (tendonR * 1.25f - a) * s;
			}
			// Tendon: slowly thinner, then a small flare at the wrist.
			var tt = (t - 0.72f) / 0.28f;
			var r = tendonR * (1.25f - 0.3f * tt);
			if ( tt > 0.9f ) r += (tt - 0.9f) / 0.1f * 0.25f;
			return r;
		}

		var muscle = new SdfBlend( 0.16f );
		var tendon = new SdfBlend( 0.12f );
		const int fibres = 6;
		var twist = Rand( rng, 0.6f, 1.2f ) * (rng.Next( 2 ) == 0 ? 1 : -1);
		// Each piece is a short tube so the blend can cull by bounds.
		const int chunk = 8;
		for ( var c0 = 0; c0 < n - 1; c0 += chunk )
		{
			var c1 = Math.Min( c0 + chunk, n - 1 );
			var coreP = new List<Vec3>();
			var coreR = new List<float>();
			var fibreP = Enumerable.Range( 0, fibres ).Select( _ => new List<Vec3>() ).ToArray();
			var fibreR = Enumerable.Range( 0, fibres ).Select( _ => new List<float>() ).ToArray();
			var tenP = new List<Vec3>();
			var tenR = new List<float>();
			for ( var i = c0; i <= c1; i++ )
			{
				var t = i / (float)(n - 1);
				var r = Radius( t );
				var side = Vec3.Cross( tan[i], nrm[i] ).Normal;
				// Muscle out to 0.7, overlapping the tendon from 0.6 so the junction is one piece.
				if ( t <= 0.72f )
				{
					coreP.Add( path[i] );
					coreR.Add( r * 0.72f );
					for ( var f = 0; f < fibres; f++ )
					{
						var a = 2 * MathF.PI * f / fibres + twist * t * 6f;
						// Fibres converge onto the tendon: their spread shrinks through the junction.
						var spread = r * 0.58f;
						fibreP[f].Add( path[i] + (nrm[i] * MathF.Cos( a ) + side * MathF.Sin( a )) * spread );
						fibreR[f].Add( r * (0.44f + 0.05f * MathF.Sin( 3.7f * f + 9f * t )) );
					}
				}
				if ( t >= 0.6f )
				{
					tenP.Add( path[i] );
					tenR.Add( t < 0.72f ? MathF.Min( r, Radius( 0.72f ) ) : r );
				}
			}
			if ( coreP.Count >= 2 )
			{
				muscle.Add( new SdfTube( coreP, coreR ) { Material = GumMat, Tag = name } );
				for ( var f = 0; f < fibres; f++ )
					muscle.Add( new SdfTube( fibreP[f], fibreR[f] ) { Material = GumMat, Tag = name } );
			}
			if ( tenP.Count >= 2 )
				tendon.Add( new SdfTube( tenP, tenR ) { Material = FatMat, Tag = name + "_tendon" } );
		}
		// Slips: the tendon splits into four over the last 2.5 in into the wrist.
		{
			var end = path[^1];
			var back = path[Math.Max( 0, n - 8 )];
			var side = Vec3.Cross( tan[^1], nrm[^1] ).Normal;
			for ( var k = 0; k < 4; k++ )
			{
				var a = 2 * MathF.PI * (k + 0.5f) / 4;
				var off = (nrm[^1] * MathF.Cos( a ) + side * MathF.Sin( a )) * 0.55f;
				tendon.Add( new SdfTube( new[] { back, Vec3.Lerp( back, end, 0.6f ) + off * 0.6f, end + off + tan[^1] * 0.8f }, new[] { 0.3f, 0.24f, 0.26f } ) { Material = FatMat, Tag = name + "_tendon" } );
			}
		}
		L.Teeth.Add( new SdfBlend( 0.3f, muscle, tendon ) );

		// Fang barbs: under the last third, in two rows ±40° either side of straight down (for a
		// loose cable "down" is the inside of its curve), 0.6–1.0 in apart, hooked back to the heap.
		var placed = 0;
		var spacing = 0.78f;
		var cum = new float[n];
		for ( var i = 1; i < n; i++ ) cum[i] = cum[i - 1] + (path[i] - path[i - 1]).Length;
		var total = cum[^1];
		var start = total * 0.6f;
		var stop = total * 0.94f;
		var row = 0;
		for ( var s = start; s < stop && placed < fangs; s += spacing * Rand( rng, 0.85f, 1.2f ), row ^= 1 )
		{
			var i = Array.FindIndex( cum, c => c >= s );
			if ( i <= 0 ) continue;
			var t = i / (float)(n - 1);
			var side = Vec3.Cross( tan[i], nrm[i] ).Normal;
			Vec3 under;
			if ( taut )
			{
				var down = new Vec3( 0, 0, -1 );
				under = (down - tan[i] * Vec3.Dot( down, tan[i] ));
				under = under.LengthSquared > 1e-4f ? under.Normal : nrm[i];
			}
			else
			{
				// The inside of the curve.
				var curv = path[Math.Min( i + 3, n - 1 )] + path[Math.Max( i - 3, 0 )] - path[i] * 2f;
				curv -= tan[i] * Vec3.Dot( curv, tan[i] );
				under = curv.LengthSquared > 1e-4f ? curv.Normal : nrm[i];
			}
			var across = Vec3.Cross( tan[i], under ).Normal;
			var ang = (row == 0 ? 1 : -1) * Rand( rng, 0.55f, 0.8f );
			var outDir = (under * MathF.Cos( ang ) + across * MathF.Sin( ang )).Normal;
			var r = Radius( t );
			var len = Rand( rng, 0.55f, 1.0f ) + 0.55f * MathF.Sin( MathF.PI * (s - start) / (stop - start) );
			// Out of the skin, raked back toward the heap and hooking further back.
			var dir = (outDir * 0.75f - tan[i] * 0.45f).Normal;
			Fang( path[i] + outDir * (r * 0.75f), dir, -tan[i], len, MathF.Max( 0.13f, len * 0.2f ), 0.02f, Rand( rng, 0.7f, 1.1f ), BoneMat, name + "_barb", sides: 7, rings: 6 );
			placed++;
		}
		Console.WriteLine( $"  {name}: {total:0.0} in long, root r {rootR:0.00}, {placed} barbs" );

		cable.Points = path;
		cable.Radii = Enumerable.Range( 0, n ).Select( i => Radius( i / (float)(n - 1) ) ).ToList();
		RigCables.Add( cable );
	}

	// --- hands, prey and props ----------------------------------------------------------------

	/// <summary>
	/// The frame for a hand closing on a bar (centre <paramref name="at"/>, axis, radius), coming
	/// from <paramref name="from"/>: fingers run over the bar away from the cable, the palm on the
	/// near side, so the hinge curl wraps them round it. Origin is the wrist.
	/// </summary>
	static Frame GripFrame( Vec3 at, Vec3 axis, float barR, Vec3 from, bool palmToBar )
	{
		var toBar = at - from;
		var f = (toBar - axis * Vec3.Dot( toBar, axis )).Normal;
		// The palm faces the bar: frame up (back of the hand) points away from it, across f.
		var up = Vec3.Cross( axis, f ).Normal;
		// The hand sits on the side of the bar the cable comes from, so its back faces "up" the cable.
		if ( Vec3.Dot( up, new Vec3( 0, 0, 1 ) ) < 0 ) up = -up;
		// Knuckles (7.4 in from the wrist at scale 1) sit over the bar's centre line, palm 0.9 off it.
		var knuckle = at + up * (barR + 0.85f) - f * 1.2f;
		var wrist = knuckle - f * 7.4f;
		return new Frame( wrist, f, up, 1.0f );
	}

	/// <summary>
	/// The real sculpted hand (no forearm stub; the cable's tendon slips meet it), placed by its
	/// wrist, with the fingers curled at the three knuckles by hinges: distal first, so each
	/// hinge's plane is still where the bone was.
	/// </summary>
	static void GripHand( Random rng, Frame wrist, string tag, bool left, (float Mcp, float Pip, float Dip) curl, float spread = 1f )
	{
		var warps = new List<SdfWarp>();
		if ( MathF.Abs( spread - 1f ) > 0.02f )
			warps.Add( new WarpStretch( new Vec3( 8.5f, 0, 0 ), new Vec3( 0, 1, 0 ), spread, 3.2f ) );
		// Hinge axes through the finger's middle (z −0.45); each moves everything past its plane.
		Func<Vec3, float> fingers = p => p.y < (left ? 2.6f : 2.6f) && p.y > -2.6f ? 1f : 0f;
		foreach ( var (x, deg) in new[] { (9.85f, curl.Dip), (8.85f, curl.Pip), (7.45f, curl.Mcp) } )
			warps.Add( new WarpHinge( new Vec3( x, 0, -0.5f ), new Vec3( 0, 1, 0 ), deg, new Vec3( x, 0, 0 ), new Vec3( -1, 0, 0 ), 0.35f ) { Gate = fingers } );
		warps.Add( new WarpNoise( 0.03f, 0.6f, rng.Next() ) );
		var mesh = KeepFaces( HumanParts.Mesh( "hand", left ), c => c.x > 1.6f );
		// The hand's own frame starts at its forearm cut, 3 in behind the wrist.
		var f = new Frame( wrist.Origin - wrist.Forward * 3.0f * wrist.Scale, wrist.Forward, wrist.Up, wrist.Scale );
		MeshParts.Add( new MeshPart { Mesh = mesh, Warps = warps, Placement = HumanParts.Placement( f ), Scale = f.Scale, Material = SkinMat, Tag = tag } );
	}

	/// <summary>A dead rat on its side, the size SWtD's rats are (body 6 in, tail 7 in).</summary>
	static void Rat( Layers L, Random rng, Vec3 c, Vec3 fwd, string tag )
	{
		fwd = fwd.Normal;
		var up = new Vec3( 0, 0, 1 );
		var left = Vec3.Cross( up, fwd ).Normal;
		var rat = new SdfBlend( 0.6f );
		var rot = new Xform( fwd, left, up, Vec3.Zero );
		rat.Add( new SdfEllipsoid( c, new Vec3( 3.2f, 1.5f, 1.9f ), rot ) { Material = HairMat, Tag = tag } );
		rat.Add( new SdfEllipsoid( c + fwd * 2.2f + new Vec3( 0, 0, 0.1f ), new Vec3( 1.8f, 1.4f, 1.5f ), rot ) { Material = HairMat, Tag = tag } );
		rat.Add( new SdfRoundCone( c + fwd * 3.6f, 1.05f, c + fwd * 6.2f + new Vec3( 0, 0, -0.5f ), 0.3f ) { Material = HairMat, Tag = tag } );
		foreach ( var s in new[] { 1f, -1f } )
			rat.Add( new SdfEllipsoid( c + fwd * 4.0f + left * (0.7f * s) + new Vec3( 0, 0, 0.9f ), new Vec3( 0.25f, 0.5f, 0.55f ), rot ) { Material = HairMat, Tag = tag } );
		foreach ( var (x, s) in new[] { (1.8f, 1f), (1.8f, -1f), (-1.6f, 1f), (-1.6f, -1f) } )
			rat.Add( new SdfRoundCone( c + fwd * x + left * (0.9f * s) + new Vec3( 0, 0, -0.8f ), 0.35f, c + fwd * (x + 0.8f) + left * (1.9f * s) + new Vec3( 0, 0, -1.6f ), 0.18f ) { Material = HairMat, Tag = tag } );
		L.Skin.Add( rat );
		// The tail, trailing back over the floor, too thin for the voxels.
		var pts = new List<Vec3>();
		for ( var k = 0; k <= 14; k++ )
		{
			var t = k / 14f;
			pts.Add( c - fwd * (3.0f + 7.5f * t) + left * (1.6f * MathF.Sin( 3.2f * t )) + new Vec3( 0, 0, -1.2f - 0.5f * t ) );
		}
		TubeMesh( pts, t => 0.3f - 0.22f * t, GumMat, tag + "_tail", 6 );
	}

	/// <summary>A capped cylinder as its own mesh: plant pipe and rail, outside the voxel grid.</summary>
	static void PropCylinder( Vec3 a, Vec3 b, float r, string tag ) =>
		TubeMesh( new List<Vec3> { a, b }, _ => r, SpikeMat, tag, 20, caps: true );

	/// <summary>A tube as its own mesh along <paramref name="pts"/>, radius by fraction along it.</summary>
	static void TubeMesh( List<Vec3> pts, Func<float, float> radius, int material, string tag, int sides, bool caps = true )
	{
		var verts = new List<Vec3>();
		var faces = new List<Face>();
		var n = pts.Count;
		Orthonormal( (pts[1] - pts[0]).Normal, out var u0, out _ );
		var u = u0;
		for ( var i = 0; i < n; i++ )
		{
			var d = (pts[Math.Min( i + 1, n - 1 )] - pts[Math.Max( i - 1, 0 )]).Normal;
			u = (u - d * Vec3.Dot( u, d )).Normal;
			var v = Vec3.Cross( d, u ).Normal;
			var r = radius( i / (float)(n - 1) );
			for ( var s = 0; s < sides; s++ )
			{
				var a = 2 * MathF.PI * s / sides;
				verts.Add( pts[i] + (u * MathF.Cos( a ) + v * MathF.Sin( a )) * r );
			}
		}
		for ( var i = 0; i < n - 1; i++ )
			for ( var s = 0; s < sides; s++ )
			{
				var a = i * sides + s;
				var b = i * sides + (s + 1) % sides;
				faces.Add( new Face( new[] { a, a + sides, b + sides, b } ) );
			}
		if ( caps )
		{
			var c0 = verts.Count;
			verts.Add( pts[0] );
			var c1 = verts.Count;
			verts.Add( pts[^1] );
			var last = (n - 1) * sides;
			for ( var s = 0; s < sides; s++ )
			{
				faces.Add( new Face( new[] { (s + 1) % sides, s, c0 } ) );
				faces.Add( new Face( new[] { last + s, last + (s + 1) % sides, c1 } ) );
			}
		}
		MeshParts.Add( new MeshPart { Mesh = new PolyMesh( verts, faces ), Material = material, Tag = tag } );
	}

	// --- sacs and cords -------------------------------------------------------------------------

	/// <summary>
	/// The meat packed between the bodies (anchor: Living Flesh Blob): sacs 2–9 in across at a
	/// 4:1 spread, pushed out of the heap so 25–55% of each shows and squashed against each other,
	/// more of them on the side being hauled (+y). Then raised vein cords (0.8 in, half proud)
	/// that walk over them, Y-fork, and run into the cable roots: veins and tendons one system.
	/// </summary>
	static void FillSacs( Layers L, Random rng, Sdf heap, Vec3 mouthC, List<(Vec3 Wrist, Vec3 Dir, float R)> roots, Options o )
	{
		var sacs = new SdfBlend( 0.35f );
		var placed = new List<(Vec3 C, float R)>();
		var centre = new Vec3( -3, -1, 6 );
		var want = (int)o.Get( "sacs", 34 );
		for ( var i = 0; i < want; i++ )
		{
			var t = i / (float)(want - 1);
			var r = MathF.Max( 1.0f, 4.6f * MathF.Pow( 1f - 0.78f * t, 1.4f ) + Rand( rng, -0.3f, 0.3f ) );
			for ( var attempt = 0; attempt < 300; attempt++ )
			{
				var dir = RandDir( rng );
				if ( dir.z < 0.05f ) continue;
				// Hauled side favoured; the front (face and mouth) kept clear.
				if ( dir.y < -0.2f && rng.NextDouble() < 0.5 ) continue;
				var s0 = SurfaceOn( heap, centre, dir );
				if ( s0.x > 4f && MathF.Abs( s0.y - mouthC.y ) < 12f ) continue;
				if ( roots.Any( q => (q.Wrist - q.Dir * 6f - s0).Length < q.R + r + 1.5f ) ) continue;
				var nn = Gradient( heap, s0 );
				var show = Rand( rng, 0.25f, 0.55f );
				var c = s0 - nn * (r * (1f - 2f * show) * 0.8f);
				if ( placed.Any( p => (p.C - c).Length < 0.72f * (p.R + r) ) ) continue;
				placed.Add( (c, r) );
				var u = Perp( nn );
				var rot = new Xform( u, Vec3.Cross( nn, u ).Normal, nn, Vec3.Zero ) * Xform.Rotate( new Vec3( 0, 0, 1 ), Rand( rng, 0f, 3f ) );
				sacs.Add( new SdfEllipsoid( c, new Vec3( r * Rand( rng, 0.95f, 1.15f ), r * Rand( rng, 0.85f, 1.05f ), r * Rand( rng, 0.62f, 0.8f ) ), rot ) { Material = FleshMat, Tag = "sac" } );
				break;
			}
		}
		Console.WriteLine( $"  sacs: {placed.Count}" );
		L.Teeth.Add( new SdfDisplace( sacs, 0.1f, 0.6f, 2, o.Seed + 11 ) );

		// Cords walk the combined surface toward a cable root.
		var skin = new SdfBlend( 0.35f, heap, sacs );
		skin.Prepare( 0f );
		var cords = new SdfBlend( 0.15f );
		var walkers = new List<(Vec3 P, int Target, float R, int Gen)>();
		var cordStarts = (int)o.Get( "cords", 12 );
		for ( var i = 0; i < cordStarts; i++ )
		{
			var target = i % roots.Count;
			Vec3 from;
			var tries = 0;
			do
			{
				var d = RandDir( rng );
				from = SurfaceOn( skin, centre, new Vec3( d.x, d.y, MathF.Abs( d.z ) + 0.3f ) );
				tries++;
			} while ( tries < 50 && ((from.x > 4f && MathF.Abs( from.y - mouthC.y ) < 12f) || (from - roots[target].Wrist).Length < 10f) );
			walkers.Add( (from, target, Rand( rng, 0.38f, 0.5f ), 0) );
		}
		while ( walkers.Count > 0 )
		{
			var (p, target, r, gen) = walkers[^1];
			walkers.RemoveAt( walkers.Count - 1 );
			var goal = roots[target].Wrist - roots[target].Dir * 5f;
			var pts = new List<Vec3>();
			var rads = new List<float>();
			var wander = Rand( rng, -1f, 1f );
			for ( var step = 0; step < 160; step++ )
			{
				var nn = Gradient( skin, p );
				pts.Add( p + nn * (r * 0.6f) );
				rads.Add( r );
				var to = goal - p;
				if ( to.Length < roots[target].R + 1.2f ) break;
				var tangent = (to - nn * Vec3.Dot( to, nn )).Normal;
				var sideV = Vec3.Cross( nn, tangent ).Normal;
				wander = Math.Clamp( wander + Rand( rng, -0.35f, 0.35f ), -1.2f, 1.2f );
				var d = (tangent + sideV * wander * 0.45f).Normal;
				p = ToSurface( skin, p + d * 0.5f );
				// Thicker as it nears the root: feeding the cable.
				r = MathF.Min( 0.62f, r * 1.003f );
				if ( gen < 2 && step > 6 && rng.NextDouble() < 0.05 )
					walkers.Add( (p, (target + 1 + rng.Next( roots.Count - 1 )) % roots.Count, r * 0.7f, gen + 1) );
			}
			if ( pts.Count >= 3 )
				for ( var c0 = 0; c0 < pts.Count - 1; c0 += 10 )
				{
					var c1 = Math.Min( c0 + 10, pts.Count - 1 );
					if ( c1 - c0 >= 1 )
						cords.Add( new SdfTube( pts.GetRange( c0, c1 - c0 + 1 ), rads.GetRange( c0, c1 - c0 + 1 ) ) { Material = GumMat, Tag = "cord" } );
				}
		}
		L.Teeth.Add( cords );
	}

	static Vec3 SurfaceOn( Sdf f, Vec3 from, Vec3 dir )
	{
		dir = dir.Normal;
		var t = 0f;
		for ( var i = 0; i < 300; i++ )
		{
			if ( f.Eval( from + dir * t ) > 0f ) break;
			t += 0.3f;
		}
		var a = MathF.Max( 0f, t - 0.3f );
		var b = t;
		for ( var i = 0; i < 10; i++ )
		{
			var m = 0.5f * (a + b);
			if ( f.Eval( from + dir * m ) > 0f ) b = m; else a = m;
		}
		return from + dir * (0.5f * (a + b));
	}

	// --- floor --------------------------------------------------------------------------------

	/// <summary>A wet puddle 1 T out in front under the mouth, and a smear trail behind it on the
	/// right where it was dragged from, streaked along the drag.</summary>
	static void RiggingFloor( Layers L, Random rng )
	{
		var floor = new SdfBlend( 1.2f );
		floor.Add( new SdfEllipsoid( new Vec3( 20, -3, 0 ), new Vec3( 13, 15, 0.75f ) ) { Material = FleshMat, Tag = "puddle" } );
		floor.Add( new SdfEllipsoid( new Vec3( 31, 4, 0 ), new Vec3( 6, 5, 0.55f ) ) { Material = FleshMat, Tag = "puddle" } );
		// Trail: from under the right side back and away along -y, narrowing, broken into smears.
		for ( var i = 0; i < 9; i++ )
		{
			var t = i / 8f;
			var c = new Vec3( -6 - 6 * t + Rand( rng, -1.5f, 1.5f ), -22 - 34 * t, 0 );
			floor.Add( new SdfEllipsoid( c, new Vec3( 7f - 3.5f * t, 5.5f, 0.55f - 0.2f * t ), new Xform( new Vec3( 0.2f, 1, 0 ).Normal, new Vec3( -1, 0.2f, 0 ).Normal, new Vec3( 0, 0, 1 ), Vec3.Zero ) ) { Material = FleshMat, Tag = "trail" } );
		}
		L.Flesh.Add( floor );
		// Drag streaks: low ridges along the trail.
		for ( var k = 0; k < 6; k++ )
		{
			var x0 = Rand( rng, -12f, -2f );
			var pts = new List<Vec3>();
			var rad = new List<float>();
			for ( var i = 0; i <= 8; i++ )
			{
				var t = i / 8f;
				pts.Add( new Vec3( x0 - 6 * t + 0.6f * MathF.Sin( 5 * t + k ), -24 - 30 * t * Rand( rng, 0.7f, 1f ), 0.2f ) );
				rad.Add( 0.45f * (1f - 0.6f * t) + 0.12f );
			}
			L.Teeth.Add( new SdfTube( pts, rad ) { Material = FleshMat, Tag = "trail" } );
		}
	}

	// --- surface pass ------------------------------------------------------------------------

	/// <summary>
	/// The surface pass, kept to two zones: bruise crackle round each stump, and fine grain on the
	/// sacs. The face and teeth carry their own detail; the rest stays quiet.
	/// </summary>
	static void RiggingDetail( PolyMesh mesh, Sdf[] nearest, int seed, List<(Vec3 Wrist, Vec3 Dir, float R)> roots )
	{
		float Bruise( int v )
		{
			var leaf = nearest[v];
			if ( leaf is null ) return 0f;
			if ( leaf.Tag is not null && leaf.Tag.EndsWith( "_bruise" ) ) return 1f;
			var p = mesh.Positions[v];
			var best = 0f;
			foreach ( var (w, d, r) in roots )
			{
				var dist = (p - (w - d * 4f)).Length;
				best = MathF.Max( best, Math.Clamp( (9f - dist) / 4f, 0f, 1f ) );
			}
			return leaf.Material is SkinMat or FleshMat ? best : 0f;
		}
		SurfaceDetail.Grain( mesh, 0.04f, 0.6f, 1.1f, 0.09f, seed + 4, Bruise );
		SurfaceDetail.Grain( mesh, 0.03f, 0.35f, 0.45f, 0.03f, seed + 5, v => nearest[v]?.Tag == "sac" ? 0.8f : 0f );
	}

	/// <summary>Each cable as a chain for Marionette: points, radii, role and end frame.</summary>
	static void WriteRigCables( Options o )
	{
		if ( !o.Extra.TryGetValue( "out", out var dir ) ) dir = Environment.GetEnvironmentVariable( "RIGGING_OUT" );
		if ( dir is null ) return;
		var sb = new StringBuilder();
		string V( Vec3 p ) => string.Format( CultureInfo.InvariantCulture, "[{0:0.###},{1:0.###},{2:0.###}]", p.x, p.y, p.z );
		sb.Append( "{\n  \"cables\": [\n" );
		for ( var i = 0; i < RigCables.Count; i++ )
		{
			var c = RigCables[i];
			sb.Append( $"    {{ \"name\": \"{c.Name}\", \"role\": \"{c.Role}\", \"root\": {V( c.Root )}, \"rootDir\": {V( c.RootDir )},\n" );
			sb.Append( $"      \"end\": {{ \"origin\": {V( c.End.Origin )}, \"forward\": {V( c.End.Forward )}, \"up\": {V( c.End.Up )} }},\n" );
			sb.Append( "      \"points\": [" + string.Join( ",", c.Points.Select( V ) ) + "],\n" );
			sb.Append( "      \"radii\": [" + string.Join( ",", c.Radii.Select( r => r.ToString( "0.###", CultureInfo.InvariantCulture ) ) ) + "] }" );
			sb.Append( i < RigCables.Count - 1 ? ",\n" : "\n" );
		}
		sb.Append( "  ]\n}\n" );
		Directory.CreateDirectory( dir );
		File.WriteAllText( Path.Combine( dir, "rigging_cables.json" ), sb.ToString() );
	}
}
