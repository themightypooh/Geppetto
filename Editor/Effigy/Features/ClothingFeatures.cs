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
		"Beanie" or "Gloves" or "Socks" => 1f,
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

	public readonly FloatParam Clearance = new( "Clearance", 0.15f, 0f, 2f, "in" );
	public readonly IntParam DrapeSteps = new( "Drape steps", 40, 1, 300 );
	public readonly FloatParam PinTop = new( "Pin top", -1f, -1f, 1f );

	/// <summary>Material slot for the garment's faces. -1 picks the next free slot, so dropping a
	/// fabric on the garment does not recolour the body.</summary>
	public readonly IntParam MaterialSlot = new( "Material slot", -1, -1, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
	{
		Recipe, Bodies, Fit, FitTo, Length, Neckline, Sleeve, Sleeves, Flare, Puff, Cinch, Wrinkles,
		Thickness, Drape, Fabric, Stiffness, Weights,
		Looseness, CinchReach, WrinkleScale, Clearance, DrapeSteps, PinTop, MaterialSlot,
	};

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[]
	{
		Looseness, CinchReach, WrinkleScale, Clearance, DrapeSteps, PinTop, MaterialSlot,
	};

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

	protected override void Execute( FeatureContext ctx )
	{
		ResolvedSlot = -1;

		if ( ctx.Rig is null || ctx.Rig.Count == 0 )
		{
			Fail(
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

		var others = ctx.Bodies.Where( b => b.IsGarment && b.Visible ).Select( b => b.Mesh ).ToList();
		var recipe = BuildRecipe();
		var slot = MaterialSlot.Value >= 0
			? MaterialSlot.Value
			: ctx.Bodies.SelectMany( b => b.Mesh.Faces ).Select( f => f.Material ).DefaultIfEmpty( 0 ).Max() + 1;

		var pin = PinTop.Value >= 0f
			? PinTop.Value
			: recipe.Name is "Trousers" or "Shorts" ? 0.06f : 0f;

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
			Offset = Offset,
			Clearance = Clearance.Clamped,
			Flare = Flare.Clamped,
			Thickness = preview ? 0f : Thickness.Clamped,
			Drape = Drape.Value && !preview,
			Fabric = FabricValue,
			DrapeSteps = DrapeSteps.Clamped,
			Passes = preview ? 2 : 4,
			Stiffness = Stiffness.Clamped,
			Puff = Puff.Clamped,
			Cinch = Cinch.Clamped,
			CinchReach = CinchReach.Clamped,
			Wrinkles = Wrinkles.Clamped,
			WrinkleScale = WrinkleScale.Clamped,
			PinTop = pin,
			MaterialSlot = slot,

			// On capsules the cloth is free to actually hang - off the shoulders, loose round
			// the chest - so it gets a longer leash than a garment lying on a skin.
			Capsules = capsules,
			Tether = capsules is null ? 0f : 6f,
		}, others, cutRegions );

		if ( result is null )
		{
			Fail(
				$"The {recipe.Name.ToLowerInvariant()} covers nothing",
				"No face of the body sits nearest the bones this garment covers.",
				"Check the bones sit inside the body they belong to",
				"Or raise Length / Sleeve" );
		}

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
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? recipe.Name, result.Mesh ) { IsGarment = true } );

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
	void ApplyWeights( FeatureContext ctx, PolyMesh garment, List<Body> wearers )
	{
		try
		{
			// The most-weighted body first: with a wearer and a prop in the same document, the one
			// carrying real weights is the one the garment should learn from.
			var source = wearers.FirstOrDefault( b => b.Mesh.IsRigged );

			if ( source is not null && SkinBinder.TransferFrom( garment, source.Mesh ) > 0 )
				return;

			if ( ctx.Rig is { Count: > 0 } )
				garment.Skin = SkinBinder.BindSmooth( garment, ctx.Rig );
		}
		catch ( Exception )
		{
			// Left unweighted on purpose - see the summary.
		}
	}
}

/// <summary>
/// Fur for s&box's fur.shader: shell layers over a garment's faces, on their own material slot. The
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

			if ( body.IsGarment )
				faces = FurShells.OutwardFaces( mesh, faces );

			ShellFaces += FurShells.Grow( mesh, faces, options );
		}

		ResolvedSlot = slot;

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
			? ctx.Bodies.Where( b => b.IsGarment ).ToList()
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

		foreach ( var target in targets )
		{
			var loops = GarmentTrim.OrderedBoundaryLoops( target.Mesh );
			var which = Chosen( loops.Count );

			var result = GarmentTrim.Build( target.Mesh, which, options );

			if ( result.Mesh.FaceCount == 0 )
			{
				problem ??= result.Problem;
				continue;
			}

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
