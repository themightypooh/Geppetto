using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>What happened to a man's face. Each is a different system wrecked, so four heads
/// read as four fates, not one face four times.</summary>
public enum HeadFate
{
	/// <summary>Still mostly him: slack jaw, heavy lids, one eye wandering, a swelling under the skin.</summary>
	Intact,
	/// <summary>Screaming past the limit: jaw dislocated to one side, the cheek torn along the bite on that side.</summary>
	Howl,
	/// <summary>Dragged sideways: features carried off along the drag, compressed ahead, striated behind.</summary>
	Smear,
	/// <summary>Sliding off the skull: lids pulled from the eyes, lip hanging, tongue out, no tears.</summary>
	Droop,
}

public static partial class CreatureGen
{
	/// <summary>
	/// The sculpted head with a fate, at <paramref name="f"/> (origin at the skull centre). All
	/// numbers are in head inches: x forward, z up; chin z -4.7, lips -3.1, nose tip x 5.0, eyes
	/// z +0.4 at y ±1.25. Skin, eyes, teeth, tongue and wound contents are real meshes; the neck
	/// root comes back as a field for the flesh to grow around.
	/// </summary>
	public static Sdf FatedHead( Random rng, Frame f, string tag, HeadFate fate, int bone = -1, float neckLength = 1f, float jawDegrees = 0f )
	{
		// The effective pivot of a wide opening: the joint rotates AND slides forward onto the
		// eminence, which together is a turn about a point behind the mouth at bite height. A
		// turn about the joint alone swings the chin back into the throat.
		var tmj = new Vec3( 0.6f, 0, -0.4f );
		var seamPlane = (Point: new Vec3( 4.5f, 0, -3.14f ), Normal: new Vec3( 2.74f, 0, 3.9f ).Normal);
		Func<Vec3, float> jawGate = p => Math.Clamp( (p.x + 0.8f) / 1.4f, 0f, 1f ) * Math.Clamp( (p.z + 6.2f) / 0.9f, 0f, 1f );

		float jawDeg = jawDegrees > 0f ? jawDegrees : fate switch { HeadFate.Intact => 9f, HeadFate.Howl => 20f, HeadFate.Smear => 11f, _ => 17f };
		float dislocate = fate == HeadFate.Howl ? 6f : 0f;
		var mouthWidth = fate == HeadFate.Howl ? 1.2f : 1.05f;

		// --- cuts made on purpose ------------------------------------------------------------
		var cuts = new List<(Vec3 A, Vec3 B, float Width)>();
		var strands = new List<(Vec3, Vec3, float)>();
		void Cut( Vec3 a, Vec3 b, float width, int strandCount )
		{
			cuts.Add( (a, b, width) );
			for ( var i = 0; i < strandCount; i++ )
			{
				var t = 0.25f + 0.55f * i / Math.Max( strandCount - 1, 1 );
				var c = Vec3.Lerp( a, b, t );
				strands.Add( (c + new Vec3( 0.05f, 0, 0.28f ), c + new Vec3( 0.05f, 0, -0.28f ), Rand( rng, 0.015f, 0.025f )) );
			}
		}
		// (The howl's cheek tear along the bite came out as a hairline sliver that never opened; the
		// dropped jaw carries the horror now, so it and the masseter behind it are gone.)

		var mouthC = new Vec3( 4.3f, 0, -3.12f );
		var seamRy = fate == HeadFate.Howl ? 1.0f : 0.75f;
		// Built the way the base was made, a level below where it was finished: the artist's
		// 3.3k-vertex cage (closed mouth, lips sealed by one row of faces at the bottom of the slit
		// groove, a full oral cavity behind) is ripped open along that seal; their Multires sculpt
		// is projected back onto it as detail; the big moves (jaw, drag) then pose the cage and the
		// detail rides them through MultiresSculpt, instead of a dense surface being pushed about.
		var cage = KeepFaces( HumanParts.Regions( "headCage" ), c => !(c.z < -5.9f || (c.z < -3.8f && (MathF.Abs( c.y ) > 2.4f || c.x < -3.3f))) );
		cage = RipLips( cage, seamRy + 0.1f, out var upperCage, out var lowerCage );
		var sculpt = new MultiresSculpt( cage );
		sculpt.AddLevel();
		var projected = DetailProjection.Capture( sculpt, 1, HumanParts.Mesh( "head" ), 0.3f );
		sculpt.AddLevel();
		if ( Environment.GetEnvironmentVariable( "RIP_DEBUG" ) is not null )
			Console.WriteLine( $"  cage {cage.VertexCount}v/{cage.FaceCount}f, lips ripped: {upperCage.Count} upper / {lowerCage.Count} lower, detail projected {projected:0.000} in mean" );

		// --- sculpt strokes per fate (before warps) ----------------------------------------
		var strokes = new List<Stroke>();
		IEnumerable<Stroke> Both( Stroke s ) => new[] { s, s.Mirrored };
		switch ( fate )
		{
			case HeadFate.Intact:
				// The growth under the cheek (Pavlovich's mass under skin, S8): a broad taut dome, a
				// plateau on top, a dimple where the skin is pinned at its peak, a broken recess ring
				// at 1.1 R where it meets the cheek, and veins running off its rim.
				{
					var g = new Vec3( 3.2f, 2.4f, -1.05f );
					strokes.Add( Stroke.Dab( g, 1.3f, 0.42f ) );
					strokes.Add( Stroke.Dab( g + new Vec3( 0.1f, 0.08f, 0 ), 0.8f, 0.18f ) );
					strokes.Add( Stroke.Dab( g + new Vec3( 0.22f, 0.12f, 0.05f ), 0.22f, -0.07f ) );
					for ( var k = 0; k < 9; k++ )
					{
						if ( k % 3 == 2 ) continue;   // broken, not a perfect ring
						var a0 = k * MathF.PI * 2f / 9f; var a1 = a0 + MathF.PI * 2f / 13f;
						Vec3 Ring( float a ) => g + new Vec3( -0.35f * MathF.Cos( a ), 0.9f * MathF.Cos( a ), 0.95f * MathF.Sin( a ) ) * 1.05f;
						strokes.Add( new Stroke( Ring( a0 ), Ring( a1 ), 0.07f, -0.05f ) );
					}
					foreach ( var end in new[] { new Vec3( 2.4f, 2.8f, -0.1f ), new Vec3( 2.6f, 2.7f, -2.5f ), new Vec3( 3.9f, 1.4f, -2.2f ) } )
						strokes.Add( new Stroke( g + (end - g).Normal * 0.9f, end, 0.06f, 0.07f ) );
				}
				break;
			case HeadFate.Howl:
				strokes.AddRange( Both( new Stroke( new Vec3( 4.6f, 0.8f, -1.6f ), new Vec3( 4.1f, 1.6f, -3.0f ), 0.15f, -0.1f ) ) );
				// Corrugator furrows: two short vertical cuts between the knotted brows.
				strokes.AddRange( Both( new Stroke( new Vec3( 4.45f, 0.22f, 0.85f ), new Vec3( 4.4f, 0.27f, 1.45f ), 0.07f, -0.045f ) ) );
				break;
			case HeadFate.Smear:
				// No folds sculpted before the drag: they get crumpled by it. They're laid in after (below).
				break;
			case HeadFate.Droop:
				strokes.AddRange( Both( Stroke.Dab( new Vec3( 3.9f, 1.4f, -3.2f ), 0.4f, 0.2f ) ) );       // fat bunched at the mouth corners
				// The nasolabial fold is swept in after the pose (MeshPart.Folds below), not carved here:
				// carved before the pull it came out a knife slit.
				strokes.AddRange( Both( Stroke.Dab( new Vec3( 3.75f, 1.3f, -0.6f ), 0.45f, 0.08f ) ) );     // soft bags under the lids
				strokes.AddRange( Both( Stroke.Dab( new Vec3( 2.8f, 2.4f, -4.2f ), 0.9f, 0.22f ) ) );      // jowls collect the slack
				strokes.Add( Stroke.Dab( new Vec3( 3.9f, -1.4f, -3.4f ), 0.45f, 0.3f ) );                                   // modiolus lump
				strokes.Add( new Stroke( new Vec3( 1.5f, -2.45f, -3.9f ), new Vec3( 3.5f, -1.1f, -4.95f ), 0.16f, 0.15f ) );   // jowl fold
				strokes.Add( new Stroke( new Vec3( 2.6f, -1.0f, -5.0f ), new Vec3( 2.6f, 1.0f, -5.0f ), 0.15f, 0.2f ) );
				strokes.Add( new Stroke( new Vec3( 2.4f, -1.0f, -5.4f ), new Vec3( 2.4f, 1.0f, -5.4f ), 0.15f, 0.2f ) );
				break;
		}
		// Lids everted off the globe (ectropion): the droop's both, the smear's dragged side.
		var everts = fate switch
		{
			HeadFate.Droop => new[] { (1f, 0.6f), (-1f, 0.7f) },
			HeadFate.Smear => new[] { (1f, 0.45f) },
			_ => Array.Empty<(float, float)>(),
		};
		// Sculpted detail goes into the top level, so it rides the cage pose like the artist's own.
		// (Small detail only: a big move recorded as detail tears when the cage moves under it.)
		if ( strokes.Count > 0 )
			sculpt.Record( 2, Sculpted( sculpt.Evaluate( 2 ), strokes ) );
		// The skin's own surface (tissue lumps, crease network) one level finer, as sculpt detail so
		// it rides every pull below.
		sculpt.AddLevel();
		sculpt.Record( 3, SkinDetail( sculpt.Evaluate( 3 ), rng.Next() ) );
		var dense = sculpt.Evaluate( 3 );

		// Jaw weights the way a rigger paints them, on the cage, by distance over it from the two
		// lip edges the rip left: nearer the lower edge goes with the jaw, nearer the upper stays.
		// The cavity's floor and the lower inner lip land on the jaw whatever their height; the
		// corners, equally far from both, come out half and half by themselves.
		var cageBag = new bool[cage.VertexCount];
		foreach ( var face in cage.Faces )
			if ( face.Material == 6 ) foreach ( var i in face.Indices ) cageBag[i] = true;
		var nbCage = MeshLoops.Neighbours( cage.VertexCount, cage.Faces );
		var dUp = MeshLoops.EdgeDistance( cage.Positions, nbCage, upperCage, 5f );
		var dDown = MeshLoops.EdgeDistance( cage.Positions, nbCage, lowerCage, 5f );
		var splitW = new float[cage.VertexCount];
		var splitM = new float[cage.VertexCount];
		for ( var i = 0; i < cage.VertexCount; i++ )
		{
			var a = MathF.Min( dUp[i], 5f ); var b = MathF.Min( dDown[i], 5f );
			if ( a >= 5f && b >= 5f ) continue;
			splitW[i] = Smoothstep( 0.15f, 0.85f, a / MathF.Max( a + b, 1e-4f ) );
			// Overrides the plane gate fully in the cavity and near the lips, fading out over the face.
			splitM[i] = cageBag[i] ? 1f : Smoothstep( 1.2f, 0.6f, MathF.Min( a, b ) );
		}

		// --- warps ------------------------------------------------------------------------
		// Three chains: the skin gets everything; the eyes skip the lid-local pulls; the upper
		// teeth skip the jaw; the lower teeth and tongue turn rigidly with it.
		var skinW = new List<SdfWarp>();
		var eyeW = new List<SdfWarp>();
		var upperW = new List<SdfWarp>();
		var jawW = new List<SdfWarp>();
		void All( SdfWarp w ) { skinW.Add( w ); eyeW.Add( w ); upperW.Add( w ); jawW.Add( w ); }
		void SkinOnly( SdfWarp w ) => skinW.Add( w );

		// The jaw: hinge about the joint (gated across the seam plane for skin), condyle slide,
		// and for the howl a dislocation that swings it to one side and drops that side.
		// Tight at the lips so they part cleanly, wide out on the cheeks where skin stretches.
		Func<Vec3, float> band = p => 0.25f + 0.3f * Math.Clamp( (MathF.Abs( p.y ) - 0.9f) / 0.9f, 0f, 1f );
		// The slide keeps the lower arch under the upper as it drops, instead of tucking it back.
		// Solved so the howl's lower incisal edge lands at (3.4, 0, -6.0) and the chin at
		// (2.9, 0, -7.2): a 20° turn on the joint and the jaw dropped out of its sockets.
		// Smaller openings: the condyle glides forward as the jaw turns, so the lower incisors stay
		// under the upper ones instead of swinging back behind them (6° about the joint alone pulls
		// them 0.4 in back, leaving only the canine tips showing).
		var incisal = new Vec3( 3.6f, 0, -3.3f );
		var turned = tmj + Xform.Rotate( new Vec3( 0, 1, 0 ), jawDeg * MathF.PI / 180f ).TransformDirection( incisal - tmj );
		// The howl's jaw is out of its sockets: a 20° turn and the whole jaw dropped 1 in and carried
		// forward, so the lower arch lands under the upper one (incisal edge ~(3.3, 0, -5.15)) facing up,
		// instead of a 30° turn swinging it 1.5 in back behind the upper teeth.
		var slide = fate == HeadFate.Howl ? new Vec3( 0.85f, 0, -1.0f ) : new Vec3( (incisal.x - turned.x) * 0.5f, 0, 0 );
		// The mouth corners ride a share of the jaw: half on the howl, so the mouth opens as an
		// oval rather than a slit pinned at the upper lip.
		var cornerShare = fate == HeadFate.Howl ? 0.5f : fate == HeadFate.Droop ? 0.3f : 0f;
		Func<Vec3, float, float> corners = cornerShare <= 0f ? null : ( p, w ) =>
		{
			var d = MathF.Min( (p - new Vec3( 3.75f, 1.12f, -3.12f )).Length, (p - new Vec3( 3.75f, -1.12f, -3.12f )).Length );
			var k = SdfWarp.Falloff( d, 0.75f );
			return w + (MathF.Max( w, cornerShare ) - w) * k;
		};
		// The jaw split, as a rigger would paint it: the slit between the lips at z -3.05 parts
		// sharply (upper lip stays, lower lip goes), the band widening to 0.3 in at the corners
		// and through the oral cavity, so its floor drops with the jaw while its roof stays.
		Func<Vec3, float, float> remap = corners;
		// The jaw's weight for cage vertex i: the hinge's own plane gate, overridden near the lips
		// and in the cavity by the painted split, then the corners' share. Turn and swing both use it.
		float JawWeight( int i )
		{
			var p = cage.Positions[i];
			var s0 = Vec3.Dot( p - seamPlane.Point, seamPlane.Normal );
			var bw = band( p );
			var t = Math.Clamp( (bw * 0.4f - s0) / bw, 0f, 1f );
			var w = t * t * (3f - 2f * t) * Math.Clamp( jawGate( p ), 0f, 1f );
			w += (splitW[i] * Math.Clamp( jawGate( p ), 0f, 1f ) - w) * splitM[i];
			// Behind the lips the artist's closed mouth runs the upper lip's lining straight down to the
			// floor (there were no teeth): below the seal, inside the lips, it's the floor, so it's the jaw's.
			if ( p.x > 3.3f && p.x < 4.02f && MathF.Abs( p.y ) < 1.3f && p.z < -3.15f )
				w = MathF.Max( w, Smoothstep( -3.15f, -3.45f, p.z ) * Math.Clamp( jawGate( p ), 0f, 1f ) );
			return Math.Clamp( corners?.Invoke( p, w ) ?? w, 0f, 1f );
		}
		var hinge = new WarpHinge( tmj, new Vec3( 0, 1, 0 ), jawDeg, seamPlane.Point, seamPlane.Normal, 0.35f ) { Gate = jawGate, BandAt = band, Slide = slide, Remap = remap };
		var rigid = new WarpHinge( tmj, new Vec3( 0, 1, 0 ), jawDeg, new Vec3( 0, 0, 100f ), new Vec3( 0, 0, 1 ), 0.01f ) { Slide = slide };
		skinW.Add( hinge ); eyeW.Add( hinge ); jawW.Add( rigid );
		// The jaw's own turns: on the cage they are applied by painted weight (JawWeight), not as warps.
		var jawTurns = new List<SdfWarp> { hinge };
		if ( fate == HeadFate.Howl )
		{

			var swing = new WarpHinge( tmj, new Vec3( 0, 0, 1 ), dislocate, seamPlane.Point, seamPlane.Normal, 0.35f ) { Gate = jawGate, BandAt = band, Remap = remap };
			var swingRigid = new WarpHinge( tmj, new Vec3( 0, 0, 1 ), dislocate, new Vec3( 0, 0, 100f ), new Vec3( 0, 0, 1 ), 0.01f );
			skinW.Add( swing ); jawW.Add( swingRigid ); jawTurns.Add( swing );
			var drop = new WarpGrab( new Vec3( 2.6f, -1.6f, -4.6f ), new Vec3( 0, -0.3f, -0.25f ), 2.4f );
			skinW.Add( drop ); jawW.Add( drop );
		}

		switch ( fate )
		{
			case HeadFate.Intact:
				// Upper face: see the units below.
				SkinOnly( new WarpGrab( new Vec3( 4.3f, 0, -3.35f ), new Vec3( 0.1f, 0, 0 ), 0.8f ) );                     // lower lip relaxed forward
				SkinOnly( new WarpGrab( new Vec3( 4.3f, 0, -2.95f ), new Vec3( 0, 0, 0.12f ), 0.8f ) );                    // upper lip off the incisors: the slack mouth shows teeth
				SkinOnly( new WarpGrab( new Vec3( 4.3f, 0, -3.45f ), new Vec3( 0, 0, -0.15f ), 0.8f ) );                   // lower lip slack off the lower incisors
				SkinOnly( new WarpGrab( new Vec3( 3.6f, 1.0f, -3.3f ), new Vec3( 0, 0.05f, -0.2f ), 0.5f ) );                // the lump drags that corner down
				SkinOnly( new WarpGrab( new Vec3( 3.8f, 1.12f, -3.12f ), new Vec3( -0.05f, 0.22f, 0.2f ), 0.55f ) );      // the mouth corner hauled toward the growth
				break;
			case HeadFate.Howl:
				SkinOnly( new WarpGrab( new Vec3( 4.45f, 0, -2.95f ), new Vec3( -0.08f, 0, 0.42f ), 1.0f ) );                // upper lip snarled off the teeth to the gum
				SkinOnly( new WarpGrab( new Vec3( 4.2f, 0, -4.6f ), new Vec3( 0.15f, 0, -0.25f ), 0.8f ) );                  // lower lip stretched down over the dropped arch
				foreach ( var sy in new[] { 1f, -1f } )
				{
					// The cheeks dragged down after the jaw: skin over the masseter pulled taut.
					SkinOnly( new WarpGrab( new Vec3( 3.2f, 2.05f * sy, -1.9f ), new Vec3( 0, 0, -0.5f ), 1.4f ) );
				}
				break;
			case HeadFate.Smear:
			{
				var drag = new WarpDragSmooth( new Vec3( 4.0f, 0.9f, -0.9f ), new Vec3( 0.1f, 1f, 0.22f ), 1.05f, 3.2f, 2.3f, rng.Next() ) { Noise = 0.06f };
				All( drag );
				// The smear itself, as one smooth pull on the cage (eye and all): back, across, down.
				// Centred on the cheek so the nose (2 in away) keeps its shape and only leans into the pull.
				All( new WarpGrab( new Vec3( 3.8f, 1.95f, -0.7f ), new Vec3( -0.35f, 0.75f, -0.8f ), 1.9f ) );
				SkinOnly( new WarpGrab( new Vec3( 4.25f, 1.6f, 1.2f ), new Vec3( -0.05f, 0.3f, -0.22f ), 0.95f ) );         // the brow on that side carried off with the drag
				SkinOnly( new WarpGrab( new Vec3( 3.9f, 1.0f, -3.1f ), new Vec3( -0.1f, 0.47f, 0.16f ), 1.1f ) );
				SkinOnly( new WarpGrab( new Vec3( 3.65f, 1.0f, -3.12f ), new Vec3( 0, 0.5f, 0.15f ), 0.6f ) );              // the mouth corner dragged with the face
				SkinOnly( new WarpGrab( new Vec3( 4.8f, -0.15f, -0.9f ), new Vec3( 0, 0.08f, 0 ), 0.35f ) );               // septum kinks
				break;
			}
			case HeadFate.Droop:
				foreach ( var sy in new[] { 1f, -1f } )
				{
					var more = sy < 0 ? 1.25f : 1f;
					SkinOnly( new WarpGrab( new Vec3( 3.9f, 1.5f * sy, -1.0f ), new Vec3( 0, 0, -0.45f * more ), 1.0f ) );    // fat pads slide
					SkinOnly( new WarpGrab( new Vec3( 1.5f, 2.4f * sy, -3.8f ), new Vec3( 0, 0, -0.5f ), 1.5f ) );           // jawline lost
				}
				// The lower lip hangs everted off the lower teeth: down and out, the whole lip together
				// (its edge is free now, so a narrow pull would string it out into a ribbon).
				SkinOnly( new WarpGrab( new Vec3( 4.15f, 0, -3.7f ), new Vec3( 0.18f, 0, -0.32f ), 1.5f ) );
				All( new WarpGrab( new Vec3( 3.5f, -1.5f, -2.5f ), new Vec3( 0, 0, -0.9f ), 2.5f ) );                       // the -y side slides further
				SkinOnly( new WarpGrab( new Vec3( 2.5f, -2.3f, -4.4f ), new Vec3( 0, 0, -0.2f ), 0.9f ) );                  // jowl below the jaw border
				SkinOnly( new WarpGrab( new Vec3( 3.0f, 0, -3.4f ), new Vec3( 0, 0, -0.35f ), 3.0f ) );                     // everything below the cheekbones slides
				break;
		}

		// The upper face takes part in the event (FACS units, HeadExpression.cs): + is the +y side.
		var (plusU, minusU) = fate switch
		{
			// Pain and dullness: the swollen side knits and squints as the mass shoves the cheek up;
			// both lids half down over the iris.
			HeadFate.Intact => (new BrowUnits { BrowKnit = 0.75f, InnerBrowUp = 0.35f, LidDrop = 0.75f, LowerLidUp = 1.4f },
			                    new BrowUnits { BrowKnit = 0.45f, InnerBrowUp = 0.4f, LidDrop = 0.55f }),
			// Terror past the limit: inner brows hauled up and knotted, lids wide, nose wrinkled and flared.
			HeadFate.Howl => (new BrowUnits { InnerBrowUp = 1.15f, OuterBrowUp = 0.35f, BrowKnit = 0.8f, LidUp = 1.1f, NoseWrinkle = 0.6f, NostrilFlare = 1.1f, CheekUp = 0.35f },
			                  new BrowUnits { InnerBrowUp = 1.0f, OuterBrowUp = 0.25f, BrowKnit = 0.9f, LidUp = 1.0f, NoseWrinkle = 0.7f, NostrilFlare = 1.0f, CheekUp = 0.45f }),
			// The side the flesh was pulled from bunches: knit, squint, nose wrinkled toward the drag.
			HeadFate.Smear => (new BrowUnits { LidDrop = 0.3f },
			                   new BrowUnits { BrowKnit = 0.8f, InnerBrowUp = 0.3f, CheekUp = 0.7f, LowerLidUp = 0.6f, NoseWrinkle = 0.8f }),
			// Everything sliding: brows hang, the outer ends most, lids heavy over vacant eyes.
			_ => (new BrowUnits { BrowSag = 1.0f, LidDrop = 0.3f, InnerBrowUp = 0.2f },
			      new BrowUnits { BrowSag = 1.3f, LidDrop = 0.4f, InnerBrowUp = 0.15f }),
		};

		// Everyone's face is a little crooked.
		All( new WarpGrab( new Vec3( 4.2f, 1.3f, 1.1f ), new Vec3( 0, 0, 0.05f ), 1.2f ) );
		All( new WarpGrab( new Vec3( 5.0f, 0, -1.2f ), new Vec3( 0, 0.05f, 0 ), 0.8f ) );
		All( new WarpNoise( 0.04f, 0.4f, rng.Next() ) );

		// Pose the cage: the jaw by its painted weights, then every other pull on the skin (drag,
		// slides, lid and lip pulls, the crooked face, noise). On the cage, the subdivision keeps a
		// pull smooth and the artist's detail rides it; on the dense skin the same pulls crumpled it.
		var cageW = skinW.Where( w => !jawTurns.Contains( w ) ).ToList();
		if ( Environment.GetEnvironmentVariable( "CURTAIN_DEBUG" ) == tag )
			for ( var i = 0; i < cage.VertexCount; i++ )
			{
				var p = cage.Positions[i];
				if ( MathF.Abs( p.y ) < 0.35f && p.x > 3.5f && p.z < -2.6f && p.z > -3.6f )
					Console.WriteLine( $"  cage {i} {p} w {JawWeight( i ):0.00} split {splitW[i]:0.00} m {splitM[i]:0.00} up {dUp[i]:0.00} down {dDown[i]:0.00} {(upperCage.Contains( i ) ? "U" : lowerCage.Contains( i ) ? "L" : "")}" );
			}
		var denseW = new List<SdfWarp>();
		var posedCage = cage.Positions.Select( ( p, i ) =>
		{
			var w = JawWeight( i );
			p += ExpressionDelta( p, plusU, minusU );
			var q = tmj + Xform.Rotate( new Vec3( 0, 1, 0 ), jawDeg * w * MathF.PI / 180f ).TransformDirection( p - tmj ) + slide * w;
			if ( dislocate != 0f ) q = tmj + Xform.Rotate( new Vec3( 0, 0, 1 ), dislocate * w * MathF.PI / 180f ).TransformDirection( q - tmj );
			foreach ( var cw in cageW ) q = cw.Forward( q );
			return q;
		} ).ToList();
		sculpt.SetCage( new PolyMesh( posedCage, cage.Faces.Select( face => new Face( (int[])face.Indices.Clone(), null, face.Material ) ) ) );
		var posedDense = sculpt.Evaluate( 3 );
		// Lids everted after the pose, weighted by where each vertex was at rest, turning about the
		// hinge line where the pose carried it: smooth whatever the pulls did to the face around it.
		foreach ( var (side, severity) in everts )
			Ectropion( dense.Positions, posedDense.Positions, side, severity );

		// Tears on purpose are cut out of the dense skin (at rest), keeping the pose per vertex.
		var skin = KeepFacesMapped( dense, c =>
		{
			foreach ( var (a, b, w) in cuts )
				if ( SkinBinder.DistanceToSegment( c, a, b ) < w ) return false;
			// That lining, where it hangs in front of the teeth: gone, or it curtains them off.
			if ( c.x > 3.45f && c.x < 4.0f && MathF.Abs( c.y ) < 0.8f && c.z > -3.45f && c.z < -2.95f ) return false;
			return true;
		}, out var fromDense );
		var posed = fromDense.Select( i => posedDense.Positions[i] ).ToArray();
		if ( Environment.GetEnvironmentVariable( "CURTAIN_DEBUG" ) == tag )
			for ( var i = 0; i < posed.Length; i++ )
				if ( MathF.Abs( posed[i].y - 0.2f ) < 0.06f && posed[i].x > 3.5f && posed[i].x < 4.1f && posed[i].z < -2.8f && posed[i].z > -3.5f )
					Console.WriteLine( $"  curtain {skin.Positions[i]} -> {posed[i]}" );
		var (upperLip, lowerLip) = LipEdges( skin, seamRy + 0.3f );

		// Regions per vertex from the artists' face sets: colour the cavity by depth, the lips red.
		var inBag = new bool[skin.VertexCount];
		var onLips = new bool[skin.VertexCount];
		var inSocket = new bool[skin.VertexCount];
		foreach ( var face in skin.Faces )
			foreach ( var i in face.Indices )
			{
				if ( face.Material is 2 or 3 ) inSocket[i] = true;
				if ( face.Material == 6 ) inBag[i] = true;
				if ( face.Material == 7 ) onLips[i] = true;
			}
		var colours = new Vec3?[skin.VertexCount];
		var lipRed = new Vec3( 0.62f, 0.3f, 0.29f );
		for ( var i = 0; i < skin.VertexCount; i++ )
		{
			var p = skin.Positions[i];
			if ( inBag[i] ) { colours[i] = MouthColour( 4.1f - p.x, 4.1f - p.x < 0.12f ? 1 : 2 ); continue; }
			// The eye sockets' inside is conjunctiva: pink-red at the lid margin, darker deeper in.
			// Hidden while the lids sit on the globe; a lid pulled off it shows the red band.
			// The lids' inner faces (tarsal conjunctiva) are the only surface this close to the globe.
			var eyeC = new Vec3( 3.08f, 1.25f * MathF.Sign( p.y ), 0.1f );
			if ( (p - eyeC).Length < 0.64f && p.x > 3.2f ) { colours[i] = Vec3.Lerp( new Vec3( 0.74f, 0.32f, 0.32f ), new Vec3( 0.55f, 0.16f, 0.18f ), Smoothstep( 0.6f, 0.5f, (p - eyeC).Length ) ); continue; }
			if ( inSocket[i] ) { colours[i] = Vec3.Lerp( new Vec3( 0.7f, 0.3f, 0.3f ), new Vec3( 0.35f, 0.08f, 0.1f ), Smoothstep( 3.4f, 2.6f, p.x ) ); continue; }
			var dz = p.z + 3.05f;
			var vermilion = onLips[i] && p.x > 3.95f ? Smoothstep( dz > 0 ? 0.42f : 0.5f, dz > 0 ? 0.26f : 0.3f, MathF.Abs( dz ) ) * Smoothstep( 1.15f, 0.8f, MathF.Abs( p.y ) ) : 0f;
			if ( vermilion > 0f ) colours[i] = Vec3.Lerp( Palette[SkinMat], lipRed, 0.75f * vermilion );
		}

		var place = HumanParts.Placement( f );
		void Part( PolyMesh m, int mat, string t, List<SdfWarp> chain, bool tension = false ) =>
			MeshParts.Add( new MeshPart { Mesh = m, Warps = chain, Placement = place, Scale = f.Scale, Material = mat, Tag = t, Bone = bone, Tension = tension, Seed = rng.Next(), TearAt = 99f, Strands = tension ? strands : new() } );

		Part( skin, SkinMat, tag, denseW, tension: true );
		MeshParts[^1].Posed = posed;
		MeshParts[^1].Colours = colours;
		// No strain folds or striae on the lips or inside the mouth: they compress hard when the jaw
		// opens and folds there come out as shards along the lip edge.
		MeshParts[^1].SkinResponseBlocked = Enumerable.Range( 0, skin.VertexCount ).Select( i =>
			inBag[i] || (onLips[i] && skin.Positions[i].x > 3.5f && MathF.Abs( skin.Positions[i].z + 3.05f ) < 0.55f && MathF.Abs( skin.Positions[i].y ) < 1.5f) ).ToArray();
		if ( fate == HeadFate.Howl )
		{
			// The mouth tore open faster than the tissue between the lips could let go: sinew
			// still bridging the gap, pinned to the real lip edges, one of them snapped.
			// Ends picked by where the pose put them: the upper end on the lip that stayed, the lower on
			// the lip that went down with the jaw (the rip's edge sets mix the two near the corners).
			int EdgeAt( HashSet<int> edge, float y, bool low ) => edge.Where( i => posed[i].x > 3.4f && (low ? posed[i].z < -4.2f : posed[i].z > -3.4f) )
				.OrderBy( i => MathF.Abs( posed[i].y - y ) - 0.3f * posed[i].x ).First();
			foreach ( var (y, r, reach) in new[] { (-0.6f, 0.06f, 1f), (0.7f, 0.04f, 0.3f) } )
				MeshParts[^1].Tendons.Add( (EdgeAt( upperLip, y, false ), EdgeAt( lowerLip.Concat( upperLip ).ToHashSet(), y + Rand( rng, -0.12f, 0.12f ), true ), r, reach) );
		}
		// The neck and trapezius cuts are fitted to the neck field: skirt them into it, no wound wall.
		// The eye sockets are holes in the base skin, closed by the eyeballs: no wound rims there.
		MeshParts[^1].IgnoreEdge = r => (r.z > -3.55f && r.z < -2.7f && r.x > 3.4f && MathF.Abs( r.y ) < seamRy + 0.25f) || (r.z < -3.7f && (MathF.Abs( r.y ) > 2.3f || r.x < -3.2f)) || (r - new Vec3( 3.1f, 1.25f * MathF.Sign( r.y ), 0.15f )).Length < 0.95f;
		MeshParts[^1].CleanIgnored = r => r.x < 4.02f && r.z < -2.9f;
		MeshParts[^1].SkirtEdge = r => r.z < -3.7f;
		MeshParts[^1].SkirtTo = (new Vec3( -1.25f, 0, -4.7f ), new Vec3( -1.6f, 0, -9.4f * neckLength - 1.5f ));
		// Skin answering the warps: swept folds where squeezed, striae where stretched past 1.3.
		MeshParts[^1].FoldWavelength = 0.3f;
		// Lids have their own folds; strain folds there fight the lid margin.
		// Local smoothing where the pose left ropes or crumples (by rest position), and no strain folds
		// laid back over them.
		var smooth = fate switch
		{
			HeadFate.Droop => new[] { (new Vec3( 4.0f, -1.8f, -1.8f ), 1.0f, 8), (new Vec3( 4.1f, 1.3f, -0.55f ), 0.6f, 6), (new Vec3( 4.1f, -1.25f, -0.3f ), 0.5f, 6) },
			HeadFate.Smear => new[] { (new Vec3( 3.3f, 2.6f, -0.9f ), 0.8f, 8) },
			_ => Array.Empty<(Vec3, float, int)>(),
		};
		MeshParts[^1].RelaxAt.AddRange( smooth );
		MeshParts[^1].FoldMask = r => (r - new Vec3( 3.3f, 1.25f * MathF.Sign( r.y ), 0.15f )).Length > 0.8f && !(r.z > 0.6f && r.x > 2.5f) && smooth.All( z => (r - z.Item1).Length > z.Item2 );
		MeshParts[^1].StriaeSpacing = 0.17f;
		MeshParts[^1].StriaeAbove = 1.6f;
		// The smear's stretch reads as streaks along the pull (SWtD's dragged faces), not stretch marks.
		MeshParts[^1].StriaeAlong = fate == HeadFate.Smear;
		if ( fate == HeadFate.Droop )
			foreach ( var sy in new[] { 1f, -1f } )
				// A hanging nasolabial fold: the cheek rolls over a soft groove (no knife slit).
				MeshParts[^1].Folds.Add( new Fold( new[] { new Vec3( 4.55f, 0.85f * sy, -1.55f ), new Vec3( 4.35f, 1.2f * sy, -2.3f ), new Vec3( 4.05f, 1.5f * sy, -3.0f ), new Vec3( 3.7f, 1.6f * sy, -3.5f ) }, 0.05f, 0.32f, 0.12f, sy > 0 ? 1 : -1, 0.15f ) );
		// The expression's furrows, snapped onto the rest skin's front surface.
		var front = skin.Positions.Select( ( q, i ) => (q, i) ).Where( t => t.q.x > 2.2f && !inBag[t.i] && !inSocket[t.i] ).Select( t => t.q ).ToArray();
		Vec3 OnSkin( float y, float z )
		{
			var best = front[0]; var bd = float.MaxValue;
			foreach ( var q in front )
			{
				var d = (q.y - y) * (q.y - y) + (q.z - z) * (q.z - z) - 0.02f * q.x;
				if ( d < bd ) { bd = d; best = q; }
			}
			return best;
		}
		MeshParts[^1].Folds.AddRange( ExpressionFurrows( plusU, minusU, OnSkin ) );
		MeshParts[^1].StriaeDepth = 0.007f;
		MeshParts[^1].RelaxStretched = fate switch { HeadFate.Howl => 5, HeadFate.Smear => 6, HeadFate.Droop => 6, _ => 2 };

		// Eyes: cornea, recessed iris, deeper pupil; aimed on their own.
		var eye = Sculpted( HumanParts.Mesh( "eye" ), new[]
		{
			Stroke.Dab( new Vec3( 0.67f, 0, 0 ), 0.5f, 0.07f ),
			Stroke.Dab( new Vec3( 0.72f, 0, 0 ), 0.27f, -0.09f ),
			Stroke.Dab( new Vec3( 0.7f, 0, 0 ), 0.11f, -0.06f ),
		} );
		// Iris and pupil by angle from the eye's own forward axis: a white ball reads as blind.
		// Sclera off-white warming toward the back; iris a dark hazel with a darker limbal ring.
		var eyeColours = eye.Positions.Select( q =>
		{
			var c = q.x / MathF.Max( q.Length, 1e-5f );
			if ( c > 0.988f ) return (Vec3?)new Vec3( 0.02f, 0.015f, 0.015f );
			if ( c > 0.93f ) return Vec3.Lerp( new Vec3( 0.36f, 0.26f, 0.15f ), new Vec3( 0.12f, 0.08f, 0.05f ), Smoothstep( 0.95f, 0.93f, c ) );
			return Vec3.Lerp( new Vec3( 0.78f, 0.62f, 0.58f ), new Vec3( 0.88f, 0.86f, 0.82f ), Smoothstep( -0.2f, 0.6f, c ) );
		} ).ToArray();
		foreach ( var sy in new[] { 1f, -1f } )
		{
			// Only the droop's eye wanders off (vacant); a wandering eye on the others read as an accident.
			var wander = sy > 0 && fate == HeadFate.Droop ? 18f : 0f;
			var yaw = -sy * 2f + wander;
			var pitch = Rand( rng, -2f, 2f ) + (fate == HeadFate.Droop ? -6f : fate == HeadFate.Intact ? -10f : 0f) - (wander > 0 ? 5f : 0f);
			var rot = Xform.Rotate( new Vec3( 0, 0, 1 ), yaw * MathF.PI / 180f ) * Xform.Rotate( new Vec3( 0, 1, 0 ), pitch * MathF.PI / 180f );
			Part( Posed( eye, rot, new Vec3( 3.08f, 1.25f * sy, 0.1f ), 0.82f ), EyeMat, tag + "_eye", eyeW );
			MeshParts[^1].Colours = eyeColours;
		}

		// Mouth: real arches, a tongue on the lower floor, a palate with a uvula, a deep bag.
		var jaw = HumanParts.Mesh( "jaw" );
		// At 0.9 the upper central incisor's crown is ~0.42 in, the real size for this head.
		var ts = 0.9f * MathF.Max( 1f, mouthWidth * 0.85f );
		var at = new Vec3( 4.02f - 1.15f * ts, 0, -3.27f );
		// Split by whole tooth (each is its own piece), never by slicing through crowns; the gum
		// piece is split by height and loses its tongue. Seated 0.12 in behind the lips.
		at += new Vec3( -0.12f, 0, 0 );
		var upperFaces = new List<int>();
		var lowerFaces = new List<int>();
		var upperGum = new List<int>();
		var lowerGum = new List<int>();
		foreach ( var comp in Components( jaw ) )
		{
			var cen = Vec3.Zero; var nv = 0;
			foreach ( var fi in comp ) foreach ( var vi in jaw.Faces[fi].Indices ) { cen += jaw.Positions[vi]; nv++; }
			cen /= MathF.Max( nv, 1 );
			if ( comp.Count < 400 )
				(cen.z > 0.02f ? upperFaces : lowerFaces).AddRange( comp );
			else
				foreach ( var fi in comp )
				{
					var c = Vec3.Zero;
					foreach ( var vi in jaw.Faces[fi].Indices ) c += jaw.Positions[vi];
					c /= jaw.Faces[fi].Indices.Length;
					if ( c.x < -1.3f ) continue;
					if ( c.z > 0.15f ) upperGum.Add( fi );
					else if ( c.z < -0.15f && (MathF.Abs( c.y ) > 0.55f || c.x > 0.9f) ) lowerGum.Add( fi );
				}
		}
		// The howl's arches sit forward, the lower one most, so the teeth show past the lips
		// instead of hiding behind them; the droop's lower arch comes forward under the hanging lip.
		var upperAt = at + (fate == HeadFate.Howl ? new Vec3( 0.1f, 0, 0 ) : Vec3.Zero);
		var lowerAt = at + new Vec3( fate == HeadFate.Howl ? -0.15f : fate == HeadFate.Droop ? 0.2f : 0f, 0, -0.08f );
		Part( Moved( FacesOf( jaw, upperFaces ), upperAt, ts ), BoneMat, tag + "_teeth", upperW );
		Part( Moved( FacesOf( jaw, lowerFaces ), lowerAt, ts ), BoneMat, tag + "_teeth", jawW );
		// Gums are wet pink-red, not tooth-coloured: in an open mouth they're half of what reads.
		Part( Moved( FacesOf( jaw, upperGum ), upperAt, ts ), GumMat, tag + "_gum", upperW );
		Part( Moved( FacesOf( jaw, lowerGum ), at + (lowerAt - at) * 0.5f, ts ), GumMat, tag + "_gum", jawW );
		var lolls = fate == HeadFate.Droop;
		// The droop's tongue slides out of the low corner, not over the middle, so the lower
		// incisors stay bare; the howl's is pushed up and forward against the lower teeth.
		var path = fate switch
		{
			// Out of the low corner and over the lower lip, then hanging: rooted in the floor.
			HeadFate.Droop => new[] { new Vec3( 1.5f, 0, -3.85f ), new Vec3( 2.6f, -0.25f, -3.6f ), new Vec3( 3.6f, -0.75f, -3.45f ), new Vec3( 4.3f, -1.05f, -3.4f ), new Vec3( 4.75f, -1.15f, -3.75f ), new Vec3( 4.95f, -1.2f, -4.3f ), new Vec3( 4.98f, -1.21f, -4.5f ) },
			// Tip just behind the forward lower incisors (labial face x ~3.76), never through the lip.
			HeadFate.Howl => new[] { new Vec3( 1.5f, 0, -4.1f ), new Vec3( 2.3f, 0, -3.85f ), new Vec3( 2.95f, 0, -3.72f ), new Vec3( 3.4f, 0, -3.7f ), new Vec3( 3.6f, 0, -3.75f ) },
			_ => new[] { new Vec3( 1.5f, 0, -3.95f ), new Vec3( 2.2f, 0, -3.75f ), new Vec3( 2.9f, 0, -3.62f ), new Vec3( 3.45f, 0, -3.64f ), new Vec3( 3.75f, 0, -3.7f ) },
		};
		var radii = lolls ? new[] { 0.5f, 0.55f, 0.48f, 0.38f, 0.3f, 0.22f, 0.12f } : new[] { 0.55f, 0.6f, 0.55f, 0.45f, 0.3f };
		Part( Tongue( path, radii, lolls ? 1.15f : 1f, rng.Next(), frenulum: !lolls ), GumMat, tag + "_tongue", jawW );
		MeshParts[^1].Shade = TongueShade( path );
		// Hangs from the back of the soft palate (the cavity's roof is at z -1.65 there).
		Part( EllipsoidMesh( new Vec3( 0.75f, 0, -1.95f ), new Vec3( 0.11f, 0.11f, 0.32f ), 6, 8 ), GumMat, tag + "_uvula", upperW );


		// Behind the howl's torn cheek: the masseter, fibres running from the arch to the jaw angle.
		if ( false )
		{
			var fibres = new SdfBlend( 0.03f );
			for ( var i = 0; i < 9; i++ )
			{
				var o = (i - 4) * 0.13f;
				var a = new Vec3( 2.3f + o * 0.6f, 2.2f, -0.6f + o * 0.2f );
				var b = new Vec3( 1.0f + o * 0.5f, 2.3f, -3.6f + o * 0.3f );
				fibres.Add( new SdfTube( new[] { a, Vec3.Lerp( a, b, 0.5f ) + new Vec3( 0.15f, 0.1f, 0 ), b }, new[] { 0.07f, 0.09f, 0.06f } ) );
			}
			Part( MeshFromField( fibres, 0.035f, 1 ), FleshMat, tag + "_masseter", skinW );
		}

		Sdf neck = new SdfRoundCone( new Vec3( -1.25f, 0, -4.7f ), 2.75f, new Vec3( -1.6f, 0, -9.4f * neckLength - 1.5f ), 3.0f );
		// The neck field's top reaches x 1.5, inside the artist's oral cavity (its throat runs back to
		// x 0.16): carve the cavity's volume out, and for a wide-open jaw the volume it swung down
		// into, or the neck's surface shows inside the mouth.
		var hollow = new List<Sdf> { new SdfCapsule( new Vec3( 0.0f, 0, -3.3f ), new Vec3( 3.2f, 0, -3.0f ), 1.15f ) };
		if ( jawDeg > 15f ) hollow.Add( new SdfCapsule( new Vec3( 0.3f, 0, -4.1f ), new Vec3( 3.0f, 0, -4.6f ), 0.95f ) );
		neck = new SdfSubtract( neck, 0.2f, hollow.ToArray() );
		var neckInst = new SdfInstance( neck, place, f.Scale ) { Material = SkinMat, Tag = tag, Bone = bone };
		foreach ( var w in skinW.Where( w => w is not WarpHinge ) ) neckInst.Warp( w );
		return neckInst;
	}

	/// <summary>
	/// A tongue as a muscle, not a sausage (the T-series recipes in tutorials.md): a flattened tube
	/// along a midline, a median groove deepest toward the tip and fading at the back, a notch at
	/// the tip, the lateral borders scalloped where they press on the teeth, a frenulum web under
	/// the tip, and papillae as fine bumps on the upper surface only. Head inches.
	/// </summary>
	static PolyMesh Tongue( Vec3[] path, float[] radii, float widen, int seed, bool frenulum )
	{
		var mid = path[path.Length / 2];
		Sdf body = new SdfInstance( new SdfTube( path, radii ), Xform.Identity )
			.Warp( new WarpStretch( mid, new Vec3( 0, 1, 0 ), widen ) )
			.Warp( new WarpStretch( mid, new Vec3( 0, 0, 1 ), 0.55f ) );
		var n = path.Length;
		// Groove: a thin tube just under the top surface, thicker toward the tip.
		var groove = new SdfTube( path.Select( ( q, i ) => q + new Vec3( 0, 0, 0.55f * radii[i] - 0.015f - 0.02f * i / (n - 1f) ) ).Skip( 1 ).ToArray(),
			path.Select( ( _, i ) => 0.025f + 0.035f * i / (n - 1f) ).Skip( 1 ).ToArray() );
		var cuts = new List<Sdf> { groove };
		// Tip notch.
		var tip = path[^1]; var back = path[^2];
		var fwd = (tip - back).Normal;
		cuts.Add( new SdfCapsule( tip + fwd * (radii[^1] * 0.95f) + new Vec3( 0, 0, 0.12f ), tip + fwd * (radii[^1] * 0.95f) - new Vec3( 0, 0, 0.12f ), 0.045f ) );
		// Scallops along both borders, front two thirds only, every ~0.22 in.
		var curve = Resample( path.ToList(), 0.22f );
		for ( var i = curve.Count / 3; i < curve.Count - 1; i++ )
		{
			var t = (float)i / (curve.Count - 1);
			var r = Lerp( radii, t ) * widen;
			var d = (curve[Math.Min( i + 1, curve.Count - 1 )] - curve[Math.Max( i - 1, 0 )]).Normal;
			var side = Vec3.Cross( new Vec3( 0, 0, 1 ), d ).Normal;
			foreach ( var sy in new[] { 1f, -1f } )
				cuts.Add( new SdfSphere( curve[i] + side * sy * (r + 0.02f), 0.06f ) );
		}
		Sdf field = new SdfSubtract( body, 0.025f, cuts.ToArray() );
		if ( frenulum )
		{
			var under = tip - fwd * 0.45f;
			var web = new SdfInstance( new SdfCapsule( under + new Vec3( 0, 0, -0.12f ), under + new Vec3( -0.35f, 0, -0.5f ), 0.05f ), Xform.Identity )
				.Warp( new WarpStretch( under, new Vec3( 0, 1, 0 ), 0.4f ) );
			var blend = new SdfBlend( 0.06f );
			blend.Add( field ); blend.Add( web );
			field = blend;
		}
		var mesh = MeshFromField( field, 0.03f );
		// Papillae: fine bumps where the surface faces up, none underneath (that side is smooth and veined).
		var noise = new Noise3( seed );
		var normals = VertexNormals( mesh.Positions, mesh.Faces );
		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var up = Smoothstep( 0.2f, 0.7f, normals[i].z );
			if ( up <= 0f ) continue;
			var b = MathF.Max( 0f, noise.Sample( mesh.Positions[i] * 22f ) );
			mesh.Positions[i] += normals[i] * up * 0.012f * b;
		}
		return mesh;
	}

	/// <summary>
	/// Ectropion, sculpted at rest: the lower lid turned forward and down off the globe about a
	/// line 0.22 in below its margin, by 25° + 55°·<paramref name="severity"/>. The turn's weight is
	/// smooth: full at the margin across the lid's middle, nothing 0.06 in above it (the eye side),
	/// fading below the hinge line and toward the corners, so the lid rolls out in one piece and the
	/// socket's inside (conjunctiva, coloured red) shows between lid and eye. <paramref name="side"/>
	/// +1 is the +y eye. Numbers from the eyelid research in tutorials.md (EH).
	/// </summary>
	static void Ectropion( IList<Vec3> rest, IList<Vec3> posed, float side, float severity )
	{
		// Measured on the base at the eye's centre line: lower margin (3.68, z -0.10); the lid's inner
		// face runs back from it to (3.3, -0.47); its outer skin sits at x 3.70-3.75.
		var margin = new Vec3( 3.68f, 1.25f * side, -0.10f );
		var axis = margin + new Vec3( -0.06f, 0, -0.3f );
		var deg = (25f + 55f * Math.Clamp( severity, 0f, 1f )) * MathF.PI / 180f;
		// Where the pose took the hinge line: the posed margin vertex, plus the rest offset.
		var near = Enumerable.Range( 0, rest.Count ).OrderBy( i => (rest[i] - margin).LengthSquared ).First();
		var pivot = posed[near] + (axis - margin);
		for ( var i = 0; i < rest.Count; i++ )
		{
			var p = rest[i];
			var w = Smoothstep( 0.5f, 0.15f, MathF.Abs( p.y - margin.y ) )
				* Smoothstep( 3.2f, 3.4f, p.x )
				* Smoothstep( margin.z + 0.1f, margin.z + 0.03f, p.z )
				* Smoothstep( axis.z - 0.3f, axis.z + 0.06f, p.z );
			if ( w <= 0f ) continue;
			posed[i] = pivot + Xform.Rotate( new Vec3( 0, 1, 0 ), deg * w ).TransformDirection( posed[i] - pivot );
		}
	}

	/// <summary>
	/// Rips the lips apart along the seal at the bottom of the slit groove, the way a modeller
	/// rips an edge loop: regions grow over the faces from the upper lip's front and the lower
	/// lip's front at once; where they meet in the mouth's middle (|y| under
	/// <paramref name="halfWidth"/>) is the seal, and <see cref="MeshLoops.Rip"/> opens it with the
	/// corners kept joined. Returns the two lip edges (upper originals, lower copies).
	/// </summary>
	static PolyMesh RipLips( PolyMesh mesh, float halfWidth, out HashSet<int> upper, out HashSet<int> lower )
	{
		var cen = MeshLoops.Centroids( mesh );
		bool Zone( Vec3 c ) => c.x > 3.3f && MathF.Abs( c.y ) < 1.6f && c.z > -3.9f && c.z < -2.2f;
		var seeds = new List<(int, int)>();
		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var c = cen[fi];
			if ( !Zone( c ) || MathF.Abs( c.y ) > 0.7f ) continue;
			if ( c.x > 4.15f && c.z > -2.95f ) seeds.Add( (fi, 1) );
			else if ( c.x > 3.95f && c.z < -3.2f ) seeds.Add( (fi, 2) );
		}
		var labels = MeshLoops.GrowRegions( mesh, seeds, fi => Zone( cen[fi] ) );
		var seal = MeshLoops.Border( mesh, labels, 1, 2, p => MathF.Abs( p.y ) < halfWidth );
		var ripped = MeshLoops.Rip( mesh, seal, fi => labels[fi] == 2, out var twins );
		upper = new HashSet<int>( twins.Keys );
		lower = new HashSet<int>( twins.Values );
		if ( Environment.GetEnvironmentVariable( "RIP_DEBUG" ) is not null )
		{
			var deg = new Dictionary<int, int>();
			foreach ( var (a, b) in seal ) { deg[a] = deg.GetValueOrDefault( a ) + 1; deg[b] = deg.GetValueOrDefault( b ) + 1; }
			Console.WriteLine( $"  rip: seeds {seeds.Count( s => s.Item2 == 1 )}/{seeds.Count( s => s.Item2 == 2 )}, seal {seal.Count} edges, ends {deg.Count( kv => kv.Value == 1 )}, branch points {deg.Count( kv => kv.Value > 2 )}, z {seal.Select( e => mesh.Positions[e.Item1].z ).DefaultIfEmpty().Min():0.00}..{seal.Select( e => mesh.Positions[e.Item1].z ).DefaultIfEmpty().Max():0.00}" );
		}
		return ripped;
	}

	/// <summary>As KeepFaces, also giving each kept vertex's index in the source mesh.</summary>
	static PolyMesh KeepFacesMapped( PolyMesh mesh, Func<Vec3, bool> keep, out int[] fromSource )
	{
		var map = new Dictionary<int, int>();
		var src = new List<int>();
		var positions = new List<Vec3>();
		var faces = new List<Face>();
		foreach ( var f in mesh.Faces )
		{
			var c = Vec3.Zero;
			foreach ( var i in f.Indices ) c += mesh.Positions[i];
			if ( !keep( c / f.Indices.Length ) ) continue;
			var idx = new int[f.Indices.Length];
			for ( var k = 0; k < idx.Length; k++ )
			{
				if ( !map.TryGetValue( f.Indices[k], out var ni ) )
				{
					ni = positions.Count;
					positions.Add( mesh.Positions[f.Indices[k]] );
					src.Add( f.Indices[k] );
					map[f.Indices[k]] = ni;
				}
				idx[k] = ni;
			}
			faces.Add( new Face( idx, null, f.Material ) );
		}
		fromSource = src.ToArray();
		return new PolyMesh( positions, faces );
	}

	/// <summary>The open edges around the mouth slit, split into the upper lip's and the lower lip's
	/// by which side of the slit's own mid-height (at that y) each vertex sits.</summary>
	static (HashSet<int> Upper, HashSet<int> Lower) LipEdges( PolyMesh mesh, float halfWidth )
	{
		var use = new Dictionary<(int, int), int>();
		foreach ( var f in mesh.Faces )
			for ( var k = 0; k < f.Indices.Length; k++ )
			{
				int a = f.Indices[k], b = f.Indices[(k + 1) % f.Indices.Length];
				var key = a < b ? (a, b) : (b, a);
				use[key] = use.GetValueOrDefault( key ) + 1;
			}
		var edge = new HashSet<int>();
		foreach ( var (key, n) in use )
		{
			if ( n != 1 ) continue;
			foreach ( var v in new[] { key.Item1, key.Item2 } )
			{
				var p = mesh.Positions[v];
				if ( p.x > 3.4f && MathF.Abs( p.y ) < halfWidth && MathF.Abs( p.z + 3.05f ) < 0.4f ) edge.Add( v );
			}
		}
		// Mid-height of the slit per y bin, from the edge's own extremes.
		var mid = edge.GroupBy( v => (int)MathF.Floor( mesh.Positions[v].y / 0.1f ) )
			.ToDictionary( g => g.Key, g => (g.Max( v => mesh.Positions[v].z ) + g.Min( v => mesh.Positions[v].z )) * 0.5f );
		var upper = new HashSet<int>(); var lower = new HashSet<int>();
		foreach ( var v in edge )
		{
			var p = mesh.Positions[v];
			(p.z > mid[(int)MathF.Floor( p.y / 0.1f )] ? upper : lower).Add( v );
		}
		return (upper, lower);
	}

	static float Lerp( float[] a, float t )
	{
		var x = Math.Clamp( t, 0f, 1f ) * (a.Length - 1);
		var i = Math.Min( (int)x, a.Length - 2 );
		return a[i] + (a[i + 1] - a[i]) * (x - i);
	}

	/// <summary>Tongue colour: dark at the root, redder and wetter toward the tip.</summary>
	static Func<Vec3, Vec3> TongueShade( Vec3[] path )
	{
		var root = path[0]; var tip = path[^1];
		var len = MathF.Max( (tip - root).Length, 1e-3f );
		return r =>
		{
			var t = Math.Clamp( Vec3.Dot( r - root, (tip - root).Normal ) / len, 0f, 1f );
			return Vec3.Lerp( Palette[GumMat] * 0.55f, new Vec3( 0.72f, 0.22f, 0.25f ), Smoothstep( 0.2f, 0.9f, t ) );
		};
	}
}

/// <summary>
/// The kernel's drag with the strength of its noise exposed. At the kernel's fixed 30% the drag
/// amount wobbles by ±0.3 in over half an inch on a face, which crumples the skin into lumps; a
/// smear pulled by one hand is smooth, and its texture comes from the folds laid in afterwards.
/// </summary>
public sealed class WarpDragSmooth : SdfWarp
{
	public Vec3 Center, Dir;
	public float Amount, Tail, Width, Noise = 0.3f;
	readonly Noise3 _noise;

	public WarpDragSmooth( Vec3 center, Vec3 dir, float amount, float tail, float width, int seed = 1 )
	{
		Center = center; Dir = dir.Normal; Amount = amount; Tail = tail; Width = width; _noise = new Noise3( seed );
	}

	float Weight( Vec3 p )
	{
		var d = p - Center;
		var t = Vec3.Dot( d, Dir );
		var q = (d - Dir * t).Length;
		var across = Falloff( q, Width );
		float along;
		if ( t < 0f ) { var u = Math.Clamp( 1f + t / Tail, 0f, 1f ); along = u * u * (3f - 2f * u); }
		else along = MathF.Exp( -(t * t) / MathF.Max( 0.09f * Tail * Tail, 1e-4f ) );
		return across * along * (1f - Noise + Noise * _noise.Fbm( p * 2f, 2 ));
	}

	public override Vec3 Forward( Vec3 p ) => p + Dir * Amount * Weight( p );

	public override Vec3 Inverse( Vec3 p )
	{
		var q = p;
		for ( var i = 0; i < 4; i++ )
			q = p - Dir * Amount * Weight( q );
		return q;
	}

	public override float Reach => MathF.Abs( Amount );
}
