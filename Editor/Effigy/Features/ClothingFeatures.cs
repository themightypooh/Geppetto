using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A garment fitted to the bodies in this studio: a T-shirt, trousers, a beanie, gloves.
///
/// A RECIPE, NOT A MESH. The rig says which faces the recipe covers; the garment is lifted off those
/// faces, pushed out to the fit, collided against every body, draped, and thickened - see
/// <see cref="GarmentFit"/>. So the same T-shirt fits the Citizen, Camhead and Gearhead, and follows
/// any of them when the body changes upstream.
///
/// Garments made earlier in the tree are collided with too but never cut from, which is what makes
/// layering work: a jacket added after a shirt goes over the shirt.
/// </summary>
public sealed class GarmentFeature : Feature
{
	public override string TypeName => "Garment";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly ChoiceParam Recipe = new( "Garment", GarmentRecipe.Names );

	/// <summary>Which bodies it is cut from and fitted to. Empty = every body that is not a garment.</summary>
	public readonly BodySelectionParam Bodies = new( "Wear on" );

	public readonly FloatParam Length = new( "Length", 0.6f, 0f, 1f );
	public readonly FloatParam Sleeve = new( "Sleeve", 0.3f, 0f, 1f );

	/// <summary>
	/// How far down the torso the garment starts, 0 at the neck.
	///
	/// THE TOP EDGE, which nothing could say before. Length has always set where a garment ENDS,
	/// and the other end was fixed at the collarbone - so every top this tool could make was a
	/// crew neck. Raise this and it becomes a scoop, then bares the shoulders, then a tube top.
	/// The sleeves come down with it, so "off the shoulder" is one slider rather than a modelling
	/// session.
	/// </summary>
	public readonly FloatParam Neckline = new( "Neckline", 0f, 0f, 0.8f );

	/// <summary>Which sleeve to keep. A one-sleeved top is an ordinary garment and was impossible
	/// to ask for - see GarmentRecipe.Spans on why the side lives on the span.</summary>
	public readonly ChoiceParam Sleeves = new( "Sleeves", GarmentRecipe.SideNames );

	/// <summary>The recipe these parameters describe. Public because the .clothing definition is
	/// written from it too, and two copies of this line would drift.</summary>
	public GarmentRecipe BuildRecipe() => GarmentRecipe.Build( Recipe.Index, Length.Clamped, Sleeve.Clamped,
		Neckline.Clamped, GarmentRecipe.SideOf( Sleeves.Index ) );

	/// <summary>
	/// How close it sits to the body, in words rather than in a number.
	///
	/// A PRESET, NOT A SLIDER, because "0.25" answers a question nobody asked. Close, Regular and
	/// Loose are how clothing is actually described, and they are what somebody who has never made
	/// a garment can choose between correctly on the first try. Custom hands the number back for
	/// anyone who wants it.
	/// </summary>
	public readonly ChoiceParam Fit = new( "Fit", new[] { "Close", "Regular", "Loose", "Baggy", "Custom" }, 1 );

	/// <summary>0 = shrinkwrapped, 1 = baggy. Only read when Fit is Custom. See <see cref="Offset"/>.</summary>
	public readonly FloatParam Looseness = new( "Looseness", 0.25f, 0f, 1f );

	public readonly FloatParam Flare = new( "Hem flare", 0f, 0f, 4f, "in" );

	/// <summary>Inflate along the surface's own normals: puffed sleeves, a padded jacket, a quilted
	/// vest. Marvelous Designer calls it Pressure. Not the same as Looseness — see GarmentFit.</summary>
	public readonly FloatParam Puff = new( "Puff", 0f, 0f, 2f, "in" );

	/// <summary>Tighten the openings: a waistband, elastic cuffs, a gathered ankle.</summary>
	public readonly FloatParam Cinch = new( "Cinch the openings", 0f, 0f, 1f );

	/// <summary>How far up from an opening the cinch reaches. A waistband is an inch; a gathered
	/// ankle is three.</summary>
	public readonly FloatParam CinchReach = new( "Cinch reach", 1.5f, 0.2f, 8f, "in" );

	/// <summary>Fine creases on top of whatever the drape found. See GarmentFit.Options.</summary>
	public readonly FloatParam Wrinkles = new( "Wrinkles", 0f, 0f, 1f );

	/// <summary>How big a wrinkle is. Small is linen, large is a heavy coat.</summary>
	public readonly FloatParam WrinkleScale = new( "Wrinkle size", 2.2f, 0.4f, 8f, "in" );
	public readonly FloatParam Thickness = new( "Thickness", 0.12f, 0f, 1f, "in" );
	public readonly BoolParam Drape = new( "Drape", true );

	/// <summary>What the cloth is made of. See GarmentFit.Options.Fabric — it is the first thing
	/// both Marvelous Designer and Simply Cloth ask, and for good reason.</summary>
	public readonly ChoiceParam Fabric = new( "Fabric",
		new[] { "Cotton", "Denim", "Leather", "Stretch" } );

	public readonly FloatParam Stiffness = new( "Stiffness", 0.8f, 0f, 1f );

	/// <summary>
	/// Which layer of an outfit it is: Base next to the skin, then Shirt, Jacket, Outer.
	///
	/// WHAT GOES OVER WHAT. A garment is fitted over every garment of a lower layer made before
	/// it - its ease is measured over them, the way a tailor measures a jacket over a shirt - and
	/// as live cloth it is kept outside them while it moves. Auto reads the garment: a bodysuit,
	/// socks and gloves are Base, a leather top is a Jacket, everything else is a Shirt.
	/// </summary>
	public readonly ChoiceParam Layer = new( "Layer", LayerNames );

	public static readonly string[] LayerNames = { "Auto", "Base", "Shirt", "Jacket", "Outer" };

	/// <summary>The layer as a number, 0 Base to 3 Outer, with Auto worked out.</summary>
	public int LayerValue => Layer.Index > 0
		? Layer.Index - 1
		: BuildRecipe().Name switch
		{
			"Bodysuit" or "Socks" or "Gloves" => 0,
			"T-shirt" or "Long sleeve" or "Tank top" when FabricValue == Effigy.Fabric.Leather => 2,
			"Jacket" or "Vest" or "Belt" or "Boots" => 2,
			"Beanie" => 3,
			_ => 1,
		};

	/// <summary>The room this garment takes up under one worn over it, in inches: its own
	/// thickness and the clearance it sits off what is under it. See GarmentFit.Options.Inner.</summary>
	public float LayerAllowance => Thickness.Clamped + Clearance.Clamped;

	public Effigy.Fabric FabricValue => Fabric.Index switch
	{
		1 => Effigy.Fabric.Denim,
		2 => Effigy.Fabric.Leather,
		3 => Effigy.Fabric.Stretch,
		_ => Effigy.Fabric.Cotton,
	};

	/// <summary>How much of the garment's height live cloth holds on its bones, from the top:
	/// the collar and yoke of a top, the waistband of a bottom. Hats and gloves and socks are
	/// held everywhere - they are fitted, and have nothing to swing.</summary>
	public float LivePinFraction => BuildRecipe().Name switch
	{
		"Trousers" or "Shorts" => 0.1f,
		"Beanie" or "Gloves" or "Socks" or "Belt" or "Boots" => 1f,
		_ => 0.14f,
	};

	/// <summary>Weight the garment so it moves with the body. On by default — see the block in
	/// Execute for why this is not something to be opted into.</summary>
	public readonly BoolParam Weights = new( "Move with the body", true );

	/// <summary>
	/// What the cloth is cut from: the wearer's own surface, or a smooth envelope around it.
	///
	/// AUTO IS RIGHT ALMOST ALWAYS. A skin (Citizen, anything sculpted) is fitted exactly; a body
	/// built from separate parts - a robot, a kitbash - gets the envelope, because cloth lifted
	/// off a hundred struts is a suit of armour, not a shirt. See GarmentEnvelope.
	/// </summary>
	public readonly ChoiceParam FitTo = new( "Fit to", new[] { "Auto", "Skin", "Envelope" } );

	public readonly FloatParam Clearance = new( "Clearance", 0.15f, 0f, 8f, "in" );
	public readonly IntParam DrapeSteps = new( "Drape steps", 40, 1, 300 );
	public readonly FloatParam PinTop = new( "Pin top", -1f, -1f, 1f );

	/// <summary>Material slot for the garment's faces. -1 picks the next free slot, so dropping a
	/// fabric on the garment does not recolour the body.</summary>
	public readonly IntParam MaterialSlot = new( "Material slot", -1, -1, 63 ) { Slider = false };

	/// <summary>A jacket or vest's front: done up, hanging open, or done up at the top only. Auto is
	/// open for a jacket and closed for a vest. See GarmentFront.</summary>
	public readonly ChoiceParam Front = new( "Front", new[] { "Auto", "Closed", "Open", "Open below the chest" } );

	/// <summary>How far an open front hangs open, 0 just parted to 1 swung right back.</summary>
	public readonly FloatParam OpenAmount = new( "Hang open", 0.6f, 0f, 1f );

	/// <summary>What fastens the front; it decides the overlap past centre front.</summary>
	public readonly ChoiceParam Closure = new( "Fastening", new[] { "Auto", "Zip", "Buttons", "Double-breasted", "Snaps", "Toggles" } );

	/// <summary>Which front goes on top. Left over right is menswear, right over left womenswear.</summary>
	public readonly ChoiceParam Lap = new( "Overlap", new[] { "Left over right", "Right over left" } );

	/// <summary>A lining inside a jacket or vest, on its own material slot, with a facing strip of
	/// the outer fabric along each front edge.</summary>
	public readonly BoolParam Lining = new( "Lining", true );

	/// <summary>Boots only: what the trousers worn under them do at the boot top.</summary>
	public readonly ChoiceParam TrouserHem = new( "Trouser hem", new[] { "Bloused", "Tucked in", "Left alone" } );

	public override IReadOnlyList<IParam> Parameters
	{
		get
		{
			var list = new List<IParam>
			{
				Recipe, Bodies, Fit, FitTo, Length, Neckline, Sleeve, Sleeves, Flare, Puff, Cinch, Wrinkles,
				Thickness, Drape, Fabric, Stiffness, Layer, Weights,
			};

			var recipe = BuildRecipe();

			if ( recipe.FrontOpening )
				list.AddRange( new IParam[] { Front, OpenAmount, Closure, Lap, Lining } );

			if ( recipe.Name == "Boots" )
				list.Add( TrouserHem );

			list.AddRange( new IParam[] { Looseness, CinchReach, WrinkleScale, Clearance, DrapeSteps, PinTop, MaterialSlot } );
			return list;
		}
	}

	public FrontMode FrontValue => Front.Index switch
	{
		1 => FrontMode.Closed,
		2 => FrontMode.Open,
		3 => FrontMode.OpenBelowChest,
		_ => BuildRecipe().Name == "Vest" ? FrontMode.Closed : FrontMode.Open,
	};

	public FrontClosure ClosureValue => Closure.Index switch
	{
		1 => FrontClosure.Zip,
		2 => FrontClosure.Buttons,
		3 => FrontClosure.DoubleBreasted,
		4 => FrontClosure.Snaps,
		5 => FrontClosure.Toggles,
		_ => BuildRecipe().Name == "Vest" ? FrontClosure.Buttons : FrontClosure.Zip,
	};

	public TrouserHem TrouserHemValue => TrouserHem.Index switch
	{
		1 => Effigy.TrouserHem.Tucked,
		2 => Effigy.TrouserHem.Leave,
		_ => Effigy.TrouserHem.Bloused,
	};

	/// <summary>The front opening as the last rebuild left it. Null without one.</summary>
	public GarmentFront.Result LastFront { get; private set; }

	public int ResolvedLiningSlot { get; private set; } = -1;

	public int LastTucked { get; private set; }
	public int LastBloused { get; private set; }

	/// <summary>A one-line account of what the last rebuild made.</summary>
	public string Summary { get; private set; }

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[]
	{
		Looseness, CinchReach, WrinkleScale, Clearance, DrapeSteps, PinTop, MaterialSlot,
	};

	/// <summary>The room the last rebuild left for the garments under this one, in inches.</summary>
	public float LastAllowance { get; private set; }

	/// <summary>The slot the last rebuild put the garment on.</summary>
	public int ResolvedSlot { get; private set; } = -1;

	/// <summary>What Fit means as a looseness, 0..1. Custom defers to the Looseness slider.</summary>
	public float LoosenessValue => Fit.Index switch
	{
		0 => 0.04f,  // Close   - a fitted tee, following the body
		1 => 0.25f,  // Regular - the old default, unchanged, so existing garments are untouched
		2 => 0.5f,   // Loose   - hangs off the chest and shoulders
		3 => 0.85f,  // Baggy   - oversized
		_ => Looseness.Clamped,
	};

	/// <summary>How far off the body the garment sits, in inches.</summary>
	public float Offset => Clearance.Clamped + 0.1f + LoosenessValue * 2.4f;

	/// <summary>
	/// For a top that hangs: how much bigger round the chest the garment is than the body, in
	/// inches - ease, the way a pattern is specified. Close 1.5, Regular 3, Loose 6, Baggy 10:
	/// pattern-making's fitted / semi-fitted / loose / very loose, and a Gildan 5000 size M is
	/// a 40-inch chest for a 38-40 body. Custom reads Looseness as 0..12 inches.
	///
	/// NOT THE SKIN OFFSET. Measured against a real size M, a Regular T-shirt off the old offset
	/// was 45.7 inches round the chest on a 37.5-inch man - eight inches of ease, a size and a
	/// half too big - because the hang already bridges the hollows, and the offset went on top.
	/// </summary>
	public float EaseRound => Fit.Index switch
	{
		0 => 1.5f,
		1 => 3f,
		2 => 6f,
		3 => 10f,
		_ => Looseness.Clamped * 12f,
	} + RecipeEase;

	/// <summary>Extra chest ease a jacket (fitted 3.75-4.25, semi-fitted 4.4-5.4, Threads) or vest
	/// is cut with over a shirt's. Custom is the number as typed.</summary>
	float RecipeEase => Fit.Index == 4 ? 0f : BuildRecipe().Name switch
	{
		"Jacket" => 2f,
		"Vest" => 1f,
		_ => 0f,
	};

	protected override void Execute( FeatureContext ctx )
	{
		ResolvedSlot = -1;
		ResolvedLiningSlot = -1;
		LastFront = null;
		LastTucked = LastBloused = 0;
		Summary = null;

		if ( ctx.Rig is null || ctx.Rig.Count == 0 )
		{
			Fail(
				RemedyAction.OpenWorkspace( "Open Rig", "Rig" ),
				"A garment needs a rig",
				"Garments find the torso, arms and legs from the bones, and this part has none.",
				"Place bones in the Rig workspace",
				"Or use Make Player, which fits Citizen's skeleton" );
		}

		var map = new BodyRegions.Map( ctx.Rig );

		if ( map.Count == 0 )
		{
			Fail(
				"None of the bones are recognisable",
				"Bones are matched by name - spine, arm_upper, thigh, head and so on - and none of these matched.",
				"Rename the bones, or use Make Player to fit Citizen's skeleton" );
		}

		var wearers = ctx.Bodies.Where( b => !b.IsGarment && Bodies.Matches( b ) ).ToList();

		if ( wearers.Count == 0 )
		{
			Fail(
				"Nothing to wear it",
				"The bodies this garment names are not in the model, or are all garments.",
				"Clear Wear on to fit every body" );
		}

		// Hidden is a view. A jacket still has to clear a shirt that is hidden, or showing the
		// shirt again reveals a garment that was fitted as if the shirt were not there.
		//
		// Not a garment of a HIGHER layer, though: a shirt made after the jacket that goes over it
		// would be pushed out over the jacket. It is fitted as if the jacket were not there, and
		// told to move up the tree so the jacket can be fitted over it.
		var layer = LayerValue;
		var earlier = ctx.Bodies.Where( b => b.IsGarment ).ToList();
		// A thickened garment is two walls, and only its OUTER wall is what a garment over it rests
		// on. Against both, a jacket that started under a loose shirt found the shirt's lining
		// nearest, whose normal faces the body, read itself as outside and stayed there - the shirt
		// showed through the jacket in patches all over the torso.
		var others = earlier.Where( b => b.Layer <= layer )
			.Select( b => MeshSolidify.OuterSheet( b.Mesh, out _ ) ?? b.Mesh ).ToList();
		var inner = earlier.Where( b => b.Layer >= 0 && b.Layer < layer )
			.Select( b => (b.Mesh, b.Layer, b.LayerAllowance) ).ToList();
		var over = earlier.Where( b => b.Layer > layer ).Select( b => b.Name ).ToList();
		var recipe = BuildRecipe();
		var slot = MaterialSlot.Value >= 0
			? MaterialSlot.Value
			: ctx.Bodies.SelectMany( b => b.Mesh.Faces ).Select( f => f.Material ).DefaultIfEmpty( 0 ).Max() + 1;

		var pin = PinTop.Value >= 0f
			? PinTop.Value
			: recipe.Name is "Trousers" or "Shorts" ? 0.06f : 0f;

		// Boots tuck the trousers in BEFORE they are fitted, so the shaft goes on over a flat leg.
		var bootTops = recipe.Name == "Boots" ? GarmentBoots.TopHeights( map, Length.Clamped ) : null;
		var trouserBodies = recipe.Name == "Boots" && TrouserHemValue != Effigy.TrouserHem.Leave
			? earlier.Where( b => b.Layer >= 0 && b.Layer < layer && b.Visible ).ToList()
			: new List<Body>();

		foreach ( var t in trouserBodies )
			LastTucked += GarmentBoots.Tuck( t.Mesh, map, wearers.Select( w => w.Mesh ).ToList(), bootTops );

		var liningSlot = recipe.FrontOpening && Lining.Value ? slot + 1 : -1;

		// The two expensive steps, dropped while a slider is moving. The drape is DrapeSteps
		// simulation steps and the thickness doubles the mesh and runs a solidify; neither changes
		// the silhouette under the cursor, and together they are most of the cost of a rebuild.
		// Collision passes come down too - two is enough to keep the cloth off the body at a glance.
		var preview = ctx.Preview;

		var cutFrom = wearers.Select( b => b.Mesh ).ToList();
		List<BodyRegion> cutRegions = null;
		List<Capsule> capsules = null;

		if ( FitTo.Index == 2 || (FitTo.Index == 0 && GarmentEnvelope.IsKitbashed( cutFrom )) )
		{
			var envelope = GarmentEnvelope.Build( cutFrom, map );

			if ( envelope.Count > 0 )
			{
				// Capsules from the REAL body, before cutFrom is swapped for the envelope: they are
				// what the shirt hangs on, and they should be as thick as Camhead is, not as thick
				// as the hull drawn round him.
				capsules = BoneCapsules.Build( map, cutFrom );
				cutFrom = envelope.Select( e => e.Mesh ).ToList();
				cutRegions = envelope.Select( e => e.Region ).ToList();
			}
		}

		var result = GarmentFit.Fit( cutFrom, map, recipe, new GarmentFit.Options
		{
			// The ease is the finished garment against the body, so the cloth's own thickness is
			// inside it, not added on top; the clearance is the least it can be.
			Offset = recipe.Hangs ? MathF.Max( Clearance.Clamped, EaseRound / (2f * MathF.PI) - Thickness.Clamped ) : Offset,
			Clearance = Clearance.Clamped,
			Flare = Flare.Clamped,
			Thickness = preview ? 0f : Thickness.Clamped,
			Drape = Drape.Value && !preview && !recipe.Rigid,
			Fabric = FabricValue,
			DrapeSteps = DrapeSteps.Clamped,
			Passes = preview ? 2 : 4,
			Stiffness = Stiffness.Clamped,
			Puff = Puff.Clamped,
			Cinch = Cinch.Clamped,
			CinchReach = CinchReach.Clamped,
			Wrinkles = recipe.Rigid ? 0f : Wrinkles.Clamped,
			WrinkleScale = WrinkleScale.Clamped,
			PinTop = pin,
			Front = recipe.FrontOpening
				? new GarmentFront.Options
				{
					Mode = FrontValue,
					Open = OpenAmount.Clamped,
					Closure = ClosureValue,
					Lap = Lap.Index == 1 ? -1 : 1,
					Fabric = FabricValue,
					Puff = Puff.Clamped,
					Flare = Flare.Clamped,
					Thickness = Thickness.Clamped,
					LiningSlot = liningSlot,
				}
				: null,
			MaterialSlot = slot,

			// On capsules the cloth is free to actually hang - off the shoulders, loose round
			// the chest - so it gets a longer leash than a garment lying on a skin.
			Capsules = capsules,
			Tether = capsules is null ? 0f : 6f,
			Inner = inner,
		}, others, cutRegions );

		if ( result is null )
		{
			Fail(
				$"The {recipe.Name.ToLowerInvariant()} covers nothing",
				"No face of the body sits nearest the bones this garment covers.",
				"Check the bones sit inside the body they belong to",
				"Or raise Length / Sleeve" );
		}

		var summary = new List<string>();

		if ( result.Front is { } front )
		{
			LastFront = front;

			if ( front.EdgeVertices == 0 )
				Warn( $"The {recipe.Name.ToLowerInvariant()}'s front could not be opened",
					"No part of the garment's front crosses the body's centre line, so there was nothing to split.",
					"Lower Neckline so the body of the garment reaches the front",
					"Or check the rig's spine runs up the middle of the body" );
			else
			{
				summary.Add( front.ToString() );

				if ( front.LiningFaces > 0 )
				{
					ResolvedLiningSlot = liningSlot;
					summary.Add( $"lined on slot {liningSlot}" );
				}
			}
		}

		if ( recipe.Name == "Belt" && !preview )
		{
			if ( GarmentBelt.AddBuckle( result.Mesh, slot, recipe.BandWidth ) is null )
				Warn( "The belt has no front for a buckle", "The band came out too small to find its front.",
					"Raise Length so the belt sits lower on the hips", "Or check the hips have bones" );

			summary.Add( $"belt {GarmentBelt.Round( result.Mesh ):0.0} in round" );
		}

		if ( recipe.Name == "Boots" )
		{
			if ( !preview )
				GarmentBoots.Sole( result.Mesh );

			if ( TrouserHemValue == Effigy.TrouserHem.Bloused )
				foreach ( var t in trouserBodies )
					LastBloused += GarmentBoots.Blouse( t.Mesh, result.Mesh, map, bootTops );

			if ( TrouserHemValue != Effigy.TrouserHem.Leave && trouserBodies.Count == 0 )
				Warn( "No trousers to tuck into the boots",
					"Trouser hem only acts on garments of a lower layer made before the boots, and there are none.",
					"Add the trousers above the boots in the tree",
					"Or set Trouser hem to Left alone" );
		}

		Summary = summary.Count > 0 ? string.Join( "; ", summary ) : null;

		// WEIGHTED HERE, NOT LATER, AND ON BY DEFAULT.
		//
		// A garment with no skin weights does not move: the body walks and the shirt stays behind in
		// the air. That is not a subtle fault, but it is an INVISIBLE one - the model looks perfect
		// in the editor, where nothing is animating - so it is found in the game, by which point the
		// asset has been exported and maybe published. Making it something you must know to go and
		// do, in another workspace, under a button called Copy weights, is making the default answer
		// the broken one.
		//
		// TWO SOURCES, IN ORDER. The body's own weights first, through TransferFrom, because the
		// garment was lifted off that body's surface and the weighting that already works there is
		// the weighting that will work on cloth lying against it. Failing that - a wearer loaded
		// from a .vmdl arrives as a surface, its skin weights are not readable through the engine's
		// vertex buffer - fall back to binding against the skeleton directly, which is what every
		// other Effigy body does when it is rigged.
		// Weighting is invisible on screen and costs a BVH build plus a nearest-surface query per
		// vertex, so it waits for the rebuild that follows the drag.
		if ( Weights.Value && !preview )
			ApplyWeights( ctx, result.Mesh, wearers );

		ResolvedSlot = slot;
		LastAllowance = result.Allowance;
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? recipe.Name, result.Mesh )
		{
			IsGarment = true,
			Layer = layer,
			LayerAllowance = preview ? Clearance.Clamped : LayerAllowance,
		} );

		if ( over.Count > 0 )
		{
			Warn(
				$"{string.Join( ", ", over )} goes over this, but was made first",
				$"This is a {LayerNames[layer + 1]} layer and {string.Join( ", ", over )} is worn over it, so it has to be fitted over this one - "
				+ "and a garment is only fitted over the ones above it in the tree.",
				"Drag this garment above it in the tree",
				"Or set Layer on one of them" );
		}

		if ( result.SolidifyProblem is not null )
		{
			Warn(
				"The garment is single-sided",
				"Thickness could not be added: " + result.SolidifyProblem,
				"Set Thickness to 0 to silence this",
				"Or Remesh the body first - a tidier surface solidifies cleanly" );
		}
	}

	/// <summary>
	/// Make the garment move with the body. See the block in Execute for the argument.
	///
	/// NEVER FATAL. Weighting is a courtesy on top of a garment that already built, so a body whose
	/// weights cannot be read or a skeleton that will not bind leaves the mesh exactly as it was
	/// rather than throwing away a shirt that is otherwise finished. Check says plainly when a
	/// garment came out unweighted, which is the right place for that to be noticed.
	/// </summary>
	/// <summary>
	/// The body's weights onto the garment, each part of the garment from the same part of the
	/// body: the torso from the torso, a sleeve from its own arm and hand, a trouser leg from its
	/// own leg.
	///
	/// NEAREST SURFACE ALONE PICKED THE WRONG LIMB. Citizen stands with its hands beside its hips,
	/// so the nearest skin to a shirt's hem was often the back of a hand - the hem took the hand's
	/// weights, and raising the arms dragged it up through the body. A cuff did the same with the
	/// thigh. The parts are read off the rig the way the cut reads them (BodyRegions), so a
	/// garment vertex only ever looks at skin it was cut from.
	/// </summary>
	static int TransferByPart( PolyMesh garment, PolyMesh body, Skeleton rig )
	{
		var map = rig is { Count: > 0 } ? new BodyRegions.Map( rig ) : null;

		if ( map is null || map.Count == 0 )
			return SkinBinder.TransferFrom( garment, body );

		static string Part( (BodyRegion Region, float T) at, Vec3 p ) => at.Region switch
		{
			BodyRegion.UpperArm or BodyRegion.LowerArm or BodyRegion.Hand => p.y > 0 ? "arm L" : "arm R",
			BodyRegion.UpperLeg or BodyRegion.LowerLeg or BodyRegion.Foot => p.y > 0 ? "leg L" : "leg R",
			_ => "body",
		};

		var faceParts = body.Faces.Select( f =>
		{
			var c = body.FaceCentroid( f );
			return Part( map.Locate( c ), c );
		} ).ToArray();

		var vertexParts = garment.Positions.Select( p => Part( map.Locate( p ), p ) ).ToArray();
		var weights = garment.IsRigged ? garment.Skin.Clone() : new SkinWeights( garment.VertexCount );
		var reached = 0;

		foreach ( var part in vertexParts.Distinct() )
		{
			// The body with only this part's faces. Same vertices and weights, so the transfer
			// reads the right corners; the faces are what the nearest-surface search can land on.
			var skin = new PolyMesh { Skin = body.Skin };
			skin.Positions.AddRange( body.Positions );

			for ( var fi = 0; fi < body.FaceCount; fi++ )
				if ( faceParts[fi] == part )
					skin.Faces.Add( body.Faces[fi] );

			// A part of the garment the body has no skin for (a skirt below the hands, say)
			// falls back to the whole body rather than to nothing.
			if ( skin.FaceCount == 0 )
				skin.Faces.AddRange( body.Faces );

			var picked = Enumerable.Range( 0, garment.VertexCount ).Where( v => vertexParts[v] == part ).ToList();
			var piece = new PolyMesh();

			foreach ( var v in picked )
				piece.AddVertex( garment.Positions[v] );

			reached += SkinBinder.TransferFrom( piece, skin );

			for ( var k = 0; k < picked.Count; k++ )
				weights[picked[k]] = piece.Skin[k];
		}

		garment.Skin = weights;
		return reached;
	}

	void ApplyWeights( FeatureContext ctx, PolyMesh garment, List<Body> wearers )
	{
		try
		{
			// The most-weighted body first: with a wearer and a prop in the same document, the one
			// carrying real weights is the one the garment should learn from.
			var source = wearers.FirstOrDefault( b => b.Mesh.IsRigged );

			if ( source is not null && TransferByPart( garment, source.Mesh, ctx.Rig ) > 0 )
				return;

			if ( ctx.Rig is not { Count: > 0 } )
				return;

			// NO WEIGHTED BODY - a wearer read off a compiled model never has one. Bind the BODY,
			// then hand its weights across, rather than binding the garment on its own. Bound
			// separately, the two disagree wherever a joint bends: the shirt's armpit followed the
			// arm while the body's stayed with the chest, and Test put a sixth of the shirt inside
			// the wearer with the arms up. Test binds the wearer this same way, so what it shows is
			// what the garment was given.
			var body = wearers.FirstOrDefault( b => b.Mesh.FaceCount > 0 )?.Mesh;

			if ( body is not null )
			{
				var bound = body.Clone();
				bound.Skin = SkinBinder.BindSmooth( bound, ctx.Rig );

				if ( TransferByPart( garment, bound, ctx.Rig ) > 0 )
					return;
			}

			garment.Skin = SkinBinder.BindSmooth( garment, ctx.Rig );
		}
		catch ( Exception )
		{
			// Left unweighted on purpose - see the summary.
		}
	}
}

/// <summary>
/// Fur for s&amp;box's fur.shader: shell layers over a garment's faces, on their own material slot. The
/// editor writes the fur material from this feature's settings and binds it to the slot.
/// </summary>
public sealed class FurFeature : Feature
{
	public override string TypeName => "Fur";

	public override GeometryKind Accepts => GeometryKind.Face | GeometryKind.Body;

	/// <summary>Picked faces. Empty = the bodies below, or every garment when that is empty too.</summary>
	public List<FaceRef> Faces = new();

	public readonly BodySelectionParam Bodies = new( "Bodies" );

	public readonly ChoiceParam Coverage = new( "Coverage", new[] { "Everywhere", "Trim the openings" } );
	public readonly FloatParam TrimWidth = new( "Trim width", 2f, 0.1f, 12f, "in" );

	public readonly FloatParam Length = new( "Length", 0.8f, 0.05f, 12f, "in" );
	public readonly IntParam Layers = new( "Layers", 12, 2, 48 );
	public readonly FloatParam Droop = new( "Droop", 0.3f, 0f, 1f );

	public readonly StringParam Colour = new( "Colour", "#8a6e55" );
	public readonly FloatParam Density = new( "Density", 24f, 1f, 64f );
	public readonly FloatParam Clump = new( "Clump", 0.4f, 0f, 1f );
	public readonly FloatParam DarkRoots = new( "Dark roots", 0.5f, 0f, 1f );
	public readonly StringParam RimColour = new( "Rim colour", "#3a342e" );
	public readonly FloatParam Wind = new( "Wind", 0f, 0f, 5f );

	public readonly IntParam MaterialSlot = new( "Material slot", -1, -1, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
	{
		Bodies, Coverage, TrimWidth, Length, Layers, Droop, Colour, Density, Clump, DarkRoots, RimColour, Wind, MaterialSlot,
	};

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Clump, DarkRoots, RimColour, MaterialSlot };

	/// <summary>The slot the last rebuild put the shells on, or -1 when it did not run.</summary>
	public int ResolvedSlot { get; private set; } = -1;

	public int ShellFaces { get; private set; }

	protected override void Execute( FeatureContext ctx )
	{
		ResolvedSlot = -1;
		ShellFaces = 0;

		var slot = MaterialSlot.Value >= 0
			? MaterialSlot.Value
			: ctx.Bodies.SelectMany( b => b.Mesh.Faces ).Select( f => f.Material ).DefaultIfEmpty( 0 ).Max() + 1;

		var options = new FurShells.Options
		{
			Length = Length.Clamped,
			Layers = Layers.Clamped,
			Droop = Droop.Clamped,
			MaterialSlot = slot,
		};

		if ( Faces.Count > 0 )
		{
			var picked = new Dictionary<Body, List<int>>();

			foreach ( var reference in Faces )
			{
				if ( !FacePlane.TryResolveFace( ctx.Bodies, reference, out var body, out var index ) )
					continue;

				if ( !picked.TryGetValue( body, out var list ) )
					picked[body] = list = new List<int>();

				list.Add( index );
			}

			if ( picked.Count == 0 )
			{
				Fail(
					"None of the picked faces are still there",
					"A change further up the tree removed or replaced them.",
					"Pick the faces again, or clear the picks to cover the whole garment" );
			}

			foreach ( var (body, list) in picked )
				ShellFaces += FurShells.Grow( body.Mesh, list, options );

			ResolvedSlot = slot;

			if ( slot >= 0 )
				ctx.ShellSlots.Add( slot );

			return;
		}

		var targets = Bodies.BodyIds.Count > 0
			? ctx.Bodies.Where( Bodies.Matches ).ToList()
			: ctx.Bodies.Where( b => b.IsGarment ).ToList();

		if ( targets.Count == 0 )
		{
			Fail(
				"Nothing to grow fur on",
				"No faces are picked, no bodies are named, and there is no garment in the model yet.",
				"Add a Garment first",
				"Or pick the faces or the part to grow it on" );
		}

		foreach ( var body in targets )
		{
			var mesh = body.Mesh;
			var all = Enumerable.Range( 0, mesh.FaceCount ).Where( fi => mesh.Faces[fi].Material != slot );
			var faces = Coverage.Index == 1 ? FurShells.TrimFaces( mesh, TrimWidth.Clamped ) : all.ToList();

			// The outer wall only, on a garment with Thickness: the inner wall faces the body, and
			// shells grown off it went straight into the wearer. Read off the layout where there is
			// one (MeshSolidify.OuterSheet - the far wall is every even face of its pairs); the
			// centre-of-mass guess is for a garment without it, and gets sleeves wrong.
			if ( body.IsGarment && MeshSolidify.OuterSheet( mesh, out _ ) is { } sheet )
				faces = faces.Where( fi => fi < 2 * sheet.FaceCount && fi % 2 == 0 ).ToList();
			else if ( body.IsGarment )
				faces = FurShells.OutwardFaces( mesh, faces );

			faces = faces.Where( fi => !ctx.ShellSlots.Contains( mesh.Faces[fi].Material ) ).ToList();
			ShellFaces += FurShells.Grow( mesh, faces, options );
		}

		ResolvedSlot = slot;

		if ( slot >= 0 )
			ctx.ShellSlots.Add( slot );

		if ( ShellFaces > 60000 )
		{
			Warn(
				$"{ShellFaces:N0} fur faces is heavy",
				"Every layer copies every furred face, and the store's ceiling is 20-30k triangles for the whole item.",
				"Lower Layers, or use Trim the openings",
				"Or Remesh the garment before the fur" );
		}
	}
}

/// <summary>
/// A body brought in from a compiled model, to be dressed rather than published: Citizen, the
/// Gearhead, anything already finished that you want to make clothes for.
///
/// WHY CLOTHING NEEDED THIS AT ALL. <see cref="GarmentFeature"/> is a recipe, not a mesh — it finds
/// the torso and arms from the rig and lifts the garment off the body's own surface. So it needs a
/// rigged body in the document, and until now the only way to have one was to have MODELLED it
/// here. That made "make clothes for Citizen" impossible in a tool whose whole clothing workflow
/// is about fitting a garment to somebody. The Wearer is the way in, the same way
/// <see cref="ImportFeature"/> is the way in for a sculpt made in Blender.
///
/// AN IMPORT, DELIBERATELY. It IS an ImportFeature — the editor hands it the model's surface as OBJ
/// bytes and every hard part is already solved: the side-car keeps the triangles out of the
/// document, the pieces arrive as separate bodies, a piece can be deleted, and a reopen does not
/// depend on the model still being installed. The only differences are the name and the flag.
///
/// THE SKELETON DOES NOT COME THROUGH HERE. A rebuild runs this again and again, and the rig is one
/// shared document-wide skeleton — a feature quietly rewriting it every rebuild would fight the Rig
/// workspace for ownership of the same data. The editor installs the skeleton ONCE, as its own
/// undoable step, when the wearer is loaded; see EffigyWindow.Clothing.cs.
///
/// IT IS NOT EXPORTED. That is the whole meaning of the flag: you are making a jacket, not
/// republishing somebody else's character, and a wearer that shipped inside your .vmdl would be
/// both a surprise and, for a model you do not own, a licensing problem you did not opt into.
/// </summary>
public sealed class WearerFeature : ImportFeature
{
	public override string TypeName => "Wearer";

	protected override bool ReferenceBodies => true;

	/// <summary>
	/// The model asset this body was taken from.
	///
	/// PROVENANCE, NOT A SOURCE PATH. The triangles come from the side-car — reading a .vmdl needs
	/// the engine and the kernel has none — so nothing rebuilds from this. It is here to be SHOWN,
	/// because a body in the tree called "citizen" with nothing saying where it came from is a body
	/// nobody can check, and to be re-picked: the dialog's browse button loads a different model
	/// over this wearer, which is how you try the same jacket on somebody else.
	/// </summary>
	public readonly StringParam Model = new( "Wearing" );

	/// <summary>
	/// Import's own two parameters are deliberately NOT inherited into the dialog.
	///
	/// Source would offer to browse for an OBJ, and a wearer pointed at an arbitrary OBJ is a body
	/// whose skeleton no longer matches its surface — the rig was installed from the model, so the
	/// garment would go on looking for a torso that had been replaced by something else. Material
	/// slot is just as wrong: a reference body is never exported, so the slot it would claim is one
	/// taken away from the garment. Both keep working underneath; neither is offered.
	/// </summary>
	public override IReadOnlyList<IParam> Parameters => new IParam[] { Model };

	public override IReadOnlyList<IParam> AdvancedParameters => Array.Empty<IParam>();
}

/// <summary>
/// Frills, ruffles, ribbons, pleats and fringe, hung off a garment's own openings.
///
/// A FEATURE OVER THE GARMENT, not a setting inside it, for the reason every other detail tool here
/// is separate: you want several. A dress with a frill at the hem, a ribbon at the collar and pleats
/// at both cuffs is four trims with four sets of numbers, and folding them into GarmentFeature would
/// make it a feature with four of everything. As separate features they stack, reorder, suppress and
/// roll back one at a time, and a trim can be put on something that is not a garment at all.
///
/// IT FOLLOWS THE GARMENT. Trim is rebuilt from whatever the garment is NOW, so changing the shirt's
/// length moves its frill down with the hem rather than leaving it behind in mid-air. That is the
/// whole argument for these being recipes rather than meshes - see GarmentFeature.
/// </summary>
public sealed class TrimFeature : Feature
{
	public override string TypeName => "Trim";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly ChoiceParam Style = new( "Style",
		new[] { "Frill", "Ribbon", "Pleats", "Fringe" } );

	/// <summary>Which garments to trim. Empty = every garment.</summary>
	public readonly BodySelectionParam Bodies = new( "On" );

	/// <summary>
	/// Which opening, named rather than numbered.
	///
	/// Openings come back ordered by height, so on a top the lowest is the hem and the highest is
	/// the collar and everything between is cuffs. That naming is a convention, not a fact about the
	/// mesh - which is why "All openings" is the default and the others are offered as the shortcut
	/// they are.
	/// </summary>
	public readonly ChoiceParam Where = new( "Where",
		new[] { "All openings", "Hem (lowest)", "Collar (highest)", "Cuffs (the rest)" } );

	public readonly FloatParam Width = new( "How far it hangs", 1.2f, 0.05f, 12f, "in" );

	/// <summary>0 is a flat band, 1 is heavily bunched. What makes a frill a frill.</summary>
	public readonly FloatParam Gather = new( "Gather", 0.5f, 0f, 1f );

	public readonly IntParam Waves = new( "Waves", 24, 2, 200 );

	public readonly IntParam Rows = new( "Rows", 3, 1, 12 );

	/// <summary>Fringe only: how much of each strip's slot is cut away.</summary>
	public readonly FloatParam Gap = new( "Gap between strips", 0.4f, 0f, 0.9f );

	public readonly IntParam MaterialSlot = new( "Material slot", -1, -1, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
	{
		Style, Bodies, Where, Width, Gather, Waves, Rows, Gap, MaterialSlot,
	};

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Rows, Gap, MaterialSlot };

	/// <summary>The slot the last rebuild put the trim on.</summary>
	public int ResolvedSlot { get; private set; } = -1;

	protected override void Execute( FeatureContext ctx )
	{
		ResolvedSlot = -1;

		// Trim goes on garments unless you say otherwise. Naming a body explicitly overrides that,
		// so a frill can be hung off the hem of something modelled by hand.
		var targets = Bodies.BodyIds.Count == 0
			? ctx.Bodies.Where( b => b.IsGarment && !b.IsGarmentDetail ).ToList()
			: ctx.Bodies.Where( b => Bodies.Matches( b ) ).ToList();

		if ( targets.Count == 0 )
		{
			Fail(
				"Nothing to trim",
				Bodies.BodyIds.Count == 0
					? "Trim hangs off a garment's openings, and there are no garments here yet."
					: "The bodies this trim names are not in the model.",
				"Add a Garment first",
				"Or name a body in On to trim something else" );
		}

		var slot = MaterialSlot.Value >= 0
			? MaterialSlot.Value
			: ctx.Bodies.SelectMany( b => b.Mesh.Faces ).Select( f => f.Material ).DefaultIfEmpty( 0 ).Max() + 1;

		var options = new GarmentTrim.Options
		{
			Style = Style.Index switch
			{
				1 => TrimStyle.Ribbon,
				2 => TrimStyle.Pleat,
				3 => TrimStyle.Fringe,
				_ => TrimStyle.Frill,
			},
			Width = Width.Clamped,
			Gather = Gather.Clamped,
			Waves = Waves.Clamped,
			// One row is enough to see where the trim is and how far it hangs. The wave needs rows
			// to develop in, and developing it is what costs.
			Rows = ctx.Preview ? 1 : Rows.Clamped,
			Gap = Gap.Clamped,
			MaterialSlot = slot,
		};

		var made = 0;
		string problem = null;

		// What the trim must stay off: everything that is not clothing. A strip carries on the way
		// the cloth was going at its edge, and at a collar that is up and in - into the neck.
		var worn = ctx.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).Where( m => m.FaceCount > 0 ).ToList();
		var wornTrees = worn.Count > 0 ? worn.Select( MeshBVH.Build ).ToList() : null;

		foreach ( var target in targets )
		{
			// A garment with Thickness is closed at every opening; the trim hangs off the outer
			// wall's edge, which is where the hem is. See MeshSolidify.OuterSheet.
			var sheet = MeshSolidify.OuterSheet( target.Mesh, out _ ) ?? target.Mesh;
			var loops = GarmentTrim.OrderedBoundaryLoops( sheet );
			var which = Chosen( loops.Count );

			var result = GarmentTrim.Build( sheet, which, options );

			if ( result.Mesh.FaceCount == 0 )
			{
				problem ??= result.Problem;
				continue;
			}

			// Off the wearer by the garment's own default clearance. Only what is inside or
			// grazing moves; a frill already standing clear is left exactly where it hangs.
			// A few times, not once: a frill on a hem that reaches past the crotch hangs between the
			// thighs, and one push out of the left leg can put it into the right.
			if ( wornTrees is not null )
				for ( var pass = 0; pass < 4; pass++ )
					GarmentFit.PushOut( result.Mesh, worn, wornTrees, 0.15f, Width.Clamped + 2f );

			// The trim inherits the garment's weights at the seam, so it moves with it. Without this
			// a frilled hem is the one part of the outfit that stays behind when the wearer walks -
			// and it is the part people look at.
			if ( target.Mesh.IsRigged && !ctx.Preview )
			{
				try
				{
					SkinBinder.TransferFrom( result.Mesh, target.Mesh );
				}
				catch ( Exception )
				{
					// Not fatal. Check reports an unweighted body plainly enough.
				}
			}

			ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{Name ?? Style.Options[Style.Index]} on {target.Name}",
				result.Mesh )
			{
				IsGarment = true,
			} );

			made++;
		}

		if ( made == 0 )
		{
			Fail(
				"The trim came out empty",
				problem ?? "none of the chosen openings could be walked",
				"Try All openings",
				"Or check the garment has an open hem - Thickness closes a garment into a solid" );
		}

		ResolvedSlot = slot;
	}

	/// <summary>Turn Where into loop indices, given how many openings this body turned out to have.
	/// Null means all of them.</summary>
	List<int> Chosen( int count )
	{
		if ( count == 0 )
			return null;

		switch ( Where.Index )
		{
			case 1:
				return new List<int> { 0 };

			case 2:
				return new List<int> { count - 1 };

			case 3:
			{
				// Everything that is neither the lowest nor the highest. On a shirt that is exactly
				// the two cuffs; on something with two openings there is nothing in between, and
				// saying so through the empty-result path is better than silently trimming the hem.
				var middle = new List<int>();

				for ( var i = 1; i < count - 1; i++ )
					middle.Add( i );

				return middle.Count > 0 ? middle : new List<int> { -1 };
			}

			default:
				return null;
		}
	}
}

/// <summary>
/// Fabric: what a garment is made of, as a real material.
///
/// A GARMENT IN DEFAULT GREY IS A SHAPE, NOT A SHIRT. The Garment's Fabric choice already sets
/// how it drapes and how live cloth swings; this is the same word for what it looks like. It
/// writes a tiling colour, normal and roughness set from the weave (see FabricMaterial) and a
/// Complex .vmat with the cloth or anisotropic switch that kind of cloth wants, and binds it to
/// the garment's material slot - the way Fur binds its shell material. No textures to find, and
/// the fabric is a feature: change the colour and the .vmat is rewritten on the next rebuild.
/// </summary>
public sealed class FabricFeature : Feature
{
	public override string TypeName => "Fabric";

	public override GeometryKind Accepts => GeometryKind.Body;

	/// <summary>Which garments. Empty is every garment.</summary>
	public readonly BodySelectionParam Bodies = new( "On" );

	public readonly ChoiceParam Preset = new( "Fabric", FabricMaterial.Presets );
	public readonly StringParam Colour = new( "Colour", "#5a6e8c" );

	/// <summary>The second thread of a plaid or gingham, and the pale weft of denim.</summary>
	public readonly StringParam Accent = new( "Second colour", "#e8e2d4" );

	/// <summary>Repeats of the weave across a foot of cloth. More is finer.</summary>
	public readonly FloatParam Scale = new( "Weave size", 6f, 1f, 24f );
	public readonly FloatParam Wear = new( "Wear", 0.2f, 0f, 1f );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Preset, Colour, Accent, Scale, Wear };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Wear };

	/// <summary>The material slots the last rebuild found on the garments this dresses. The editor
	/// binds the written .vmat to each.</summary>
	public IReadOnlyList<int> ResolvedSlots { get; private set; } = Array.Empty<int>();

	public string PresetName => FabricMaterial.Presets[Math.Clamp( Preset.Index, 0, FabricMaterial.Presets.Length - 1 )];

	protected override void Execute( FeatureContext ctx )
	{
		ResolvedSlots = Array.Empty<int>();

		var targets = Bodies.BodyIds.Count > 0
			? ctx.Bodies.Where( Bodies.Matches ).ToList()
			: ctx.Bodies.Where( b => b.IsGarment ).ToList();

		if ( targets.Count == 0 )
		{
			Fail(
				"Nothing to make of this fabric",
				Bodies.BodyIds.Count == 0
					? "There is no garment in the model yet."
					: "The bodies this fabric names are not in the model.",
				"Add a Garment first",
				"Or name the part to dress in On" );
		}

		// The garment already has a slot of its own (see GarmentFeature.MaterialSlot); the fabric
		// goes on that rather than renumbering. Shell slots claimed earlier this rebuild (fur)
		// are not cloth, and binding the weave onto them fights the fur material.
		ResolvedSlots = targets.SelectMany( b => b.Mesh.Faces ).Select( f => f.Material )
			.Where( s => !ctx.ShellSlots.Contains( s ) ).Distinct().OrderBy( s => s ).ToList();
	}
}
