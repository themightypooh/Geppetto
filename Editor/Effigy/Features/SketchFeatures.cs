using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Holds a sketch and publishes it for later features to consume. Produces no geometry itself,
/// exactly like Onshape's Sketch feature.
///
/// Downstream features reference this by feature Id rather than holding the Sketch object, so
/// editing the sketch and rebuilding flows through automatically — there is no second reference to
/// keep in step.
/// </summary>
public sealed class SketchFeature : Feature
{
	public override string TypeName => "Sketch";

	public Sketch Sketch = new();

	public readonly ChoiceParam Plane = new( "Plane", new[] { "Top (XY)", "Front (XZ)", "Right (YZ)" } );
	public readonly FloatParam PlaneOffset = new( "Offset", 0f, unit: "u" );

	/// <summary>
	/// A face of an existing body to sketch on, instead of one of the three global planes.
	///
	/// Stored as geometry (a point and a normal) rather than a face index, and re-found on every
	/// rebuild — see FaceRef for why an index would silently attach itself to a different face the
	/// moment anything upstream changed.
	/// </summary>
	public FaceRef? Face;

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Plane, PlaneOffset };

	protected override void Execute( FeatureContext ctx )
	{
		var basePlane = ResolveBasePlane( ctx );

		Sketch.Plane = PlaneOffset.Value == 0f ? basePlane : basePlane.Offset( PlaneOffset.Value );

		// Constraints are intent; points are what everything downstream reads. Solving here, before
		// the sketch is published, is what makes the two agree — profile finding, extrude and
		// revolve all see coordinates that already satisfy the rules, and none of them needs to
		// know a solver exists. A sketch with no constraints costs one comparison.
		if ( Sketch.Constraints.Count > 0 )
		{
			var solve = Sketch.Solve();

			if ( !solve.Converged )
			{
				// A warning rather than an error, deliberately: the points are left at the solver's
				// best attempt, which is still a drawable sketch. Failing the feature would blank
				// the model the moment a sketch became momentarily over-constrained mid-edit.
				Warning = $"Sketch constraints did not fully solve — residual {solve.Residual:0.###e0} "
					+ $"after {solve.Iterations} iterations. The geometry is the closest fit found.";
			}
			else if ( solve.RedundantConstraints > 0 )
			{
				Warning = $"{solve.RedundantConstraints} constraint(s) repeat something already "
					+ "implied by the others. Harmless, but removing them makes the sketch easier "
					+ "to reason about.";
			}
		}

		ctx.Sketches[Id] = Sketch;

		// Publish what this sketch is growing out of, so a consumer can add to that body instead of
		// starting a new one. Cleared when there is no face, or a sketch moved back onto a global
		// plane would keep merging into whatever it used to sit on.
		if ( Face is { } attached )
			ctx.SketchHostBodies[Id] = attached.BodyId;
		else
			ctx.SketchHostBodies.Remove( Id );
	}

	SketchPlane ResolveBasePlane( FeatureContext ctx )
	{
		if ( Face is not { } face )
		{
			return Plane.Index switch
			{
				0 => SketchPlane.XY,
				1 => SketchPlane.XZ,
				2 => SketchPlane.YZ,
				_ => SketchPlane.XY
			};
		}

		if ( !FacePlane.TryResolve( ctx.Bodies, face, out var resolved ) )
		{
			throw new InvalidOperationException(
				"The face this sketch was placed on is gone — nothing at that point faces that way "
				+ "any more. Move the sketch to another face, or back to one of the global planes." );
		}

		return resolved;
	}
}

/// <summary>Shared plumbing for the features that turn a sketch profile into a solid.</summary>
public abstract class SketchConsumingFeature : Feature
{
	public readonly ChoiceParam Sketch = new( "Sketch", new[] { "" } );

	/// <summary>Feature id of the SketchFeature to consume. Empty means "the most recent one",
	/// which is what you want while there is only one sketch in the tree.</summary>
	public string SketchFeatureId = "";

	/// <summary>
	/// Which closed region of the sketch to build from, as a point inside it in plane coordinates.
	/// Null means every region, which is the old behaviour and stays the default.
	///
	/// A POINT RATHER THAN AN INDEX, deliberately. Profiles have no identity of their own — they
	/// are re-found from the curve graph on every rebuild, and their order is whatever order the
	/// walk happens to discover them in. "Region 2" would silently come to mean a different face
	/// the moment a curve was added upstream. A point inside the region is stable under every edit
	/// that does not destroy the region itself, and it is also exactly what a click in the viewport
	/// already gives us.
	/// </summary>
	public Vec2? RegionSeed;

	/// <summary>
	/// What the result does to the model: start a new part, or become part of the one it grows out
	/// of, or cut into it. Onshape calls this Result and puts New / Add / Remove / Intersect in it.
	///
	/// AUTO IS THE DEFAULT AND IT IS THE INTERESTING ONE. Extruding three bosses off the same block
	/// used to leave four separate parts in the list, which is not what "I built this up out of four
	/// extrudes" means to anyone. Auto adds to the body whose face the sketch was drawn on, and
	/// starts a new part when the sketch is on a global plane instead. So building on something
	/// keeps one part, and sketching in space starts another, with no parameter to set for either.
	/// </summary>
	/// AUTO NEVER REMOVES. Adding and removing look identical from the geometry — the same profile
	/// pulled the same distance off the same face — so there is nothing for Auto to read that would
	/// tell them apart, and a rule that guessed would eventually guess a hole into someone's part.
	/// Removing is always asked for.
	///
	/// Add and Remove are also not two flavours of one thing. Add merges the meshes and leaves the
	/// interface between them uncut, which is cheap and right for a boss standing on a face. There
	/// is no equivalent shortcut for a cut: taking material away means genuinely recomputing the
	/// surface, so Remove goes through MeshBoolean and needs a provider installed — the engine's,
	/// inside the s&box editor. Intersect is not offered because nothing has asked for it yet.
	/// </summary>
	public readonly ChoiceParam Result = new( "Result",
		new[] { "Auto", "New body", "Add to the body it grows from", "Remove from the body it cuts into" } );

	/// <summary>Index into Result for the cut. Named rather than written as a bare 3 at each use,
	/// since these options are also the dropdown a user reads and reordering them is a live
	/// possibility.</summary>
	protected const int ResultRemove = 3;

	/// <summary>Which sketch feature this consumes, resolved the same way ResolveSketch resolves
	/// the sketch itself. Both have to agree about what "the most recent one" means, so they read
	/// the same dictionary in the same order.</summary>
	protected string ResolveSketchId( FeatureContext ctx )
	{
		if ( ctx.Sketches.Count == 0 )
			return null;

		return string.IsNullOrEmpty( SketchFeatureId ) ? ctx.Sketches.Keys.Last() : SketchFeatureId;
	}

	protected Sketch ResolveSketch( FeatureContext ctx )
	{
		if ( ctx.Sketches.Count == 0 )
			throw new InvalidOperationException( "There is no sketch to use — add a Sketch feature first" );

		if ( string.IsNullOrEmpty( SketchFeatureId ) )
			return ctx.Sketches.Values.Last();

		if ( !ctx.Sketches.TryGetValue( SketchFeatureId, out var sketch ) )
			throw new InvalidOperationException( $"Sketch '{SketchFeatureId}' is not available at this point in the tree" );

		return sketch;
	}

	/// <summary>
	/// Put a built solid into the model: as its own body, or merged into the one it grows from.
	///
	/// WHAT MERGING IS AND IS NOT. The two meshes are combined into one body. It is not a boolean
	/// union — nothing cuts the interface between them, so the face the boss stands on is still in
	/// there, now on the inside where it is never seen. For what this is for, that is the right
	/// trade: the part list reads as one part, the render and every exporter are correct, and none
	/// of it waits on a robust CSG. What it costs is that the merged mesh is non-manifold along
	/// that interface, so the operations that need clean topology — shell especially — will refuse
	/// it rather than produce something wrong. That refusal is the honest failure and it is why
	/// merging is not silently forced on features that never asked for it.
	/// </summary>
	protected void Emit( FeatureContext ctx, PolyMesh mesh )
	{
		var target = ResolveTarget( ctx );

		if ( target is null )
		{
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
			return;
		}

		if ( Result.Index == ResultRemove )
		{
			// ASKED BEFORE THE ENGINE IS, because the engine cannot tell these two apart. A tool
			// that misses the target entirely and a tool that genuinely defeats the boolean both
			// come back as one refusal, and its text sends you looking at the adapter.
			//
			// Bounding boxes only, so this never rejects a cut that would have worked: two boxes
			// that do not overlap contain no solids that do. Two that DO overlap may still hold
			// solids that miss each other, and that case is left to the engine — a conservative
			// check that is always right about what it refuses beats an exact one that guesses.
			RefuseIfItMisses( target.Mesh, mesh );

			// The built solid is the TOOL here, not the result: it is the shape of the hole, and
			// what stays in the studio is the target with that shape taken out of it. Replacing the
			// mesh rather than the Body keeps the body's id, which everything built on this part is
			// holding — a cut must not invalidate the face a later sketch sits on.
			target.Mesh = MeshBoolean.Apply( BooleanOp.Subtract, target.Mesh, mesh );
			return;
		}

		MeshTransform.Append( target.Mesh, mesh );
	}

	/// <summary>
	/// Refuse a cut whose tool solid does not reach the body at all, and say which way it went
	/// wrong rather than that it went wrong.
	///
	/// The overwhelmingly common cause is direction — see ExtrudeFeature.DirectionSign — so the
	/// message names the axis it missed along and how far short it fell. "It did not work" sends
	/// someone to read the boolean adapter; "the cut sits 0.4 above the material" does not.
	/// </summary>
	protected static void RefuseIfItMisses( PolyMesh target, PolyMesh tool )
	{
		if ( target is null || tool is null || target.VertexCount == 0 || tool.VertexCount == 0 )
			return;

		Extent( target, out var targetMin, out var targetMax );
		Extent( tool, out var toolMin, out var toolMax );

		var gapX = Gap( targetMin.x, targetMax.x, toolMin.x, toolMax.x );
		var gapY = Gap( targetMin.y, targetMax.y, toolMin.y, toolMax.y );
		var gapZ = Gap( targetMin.z, targetMax.z, toolMin.z, toolMax.z );

		var worst = MathF.Max( gapX, MathF.Max( gapY, gapZ ) );

		// STRICTLY NEGATIVE, not merely non-positive. A gap of exactly zero is the two solids
		// touching on a plane, which is precisely what a cut extruded the wrong way off a face
		// looks like — it sits ON the material with zero volume in common, and subtracting it
		// removes nothing. Treating "touching" as "overlapping" is what let the original bug
		// through this check on its first run.
		if ( worst < -1e-6f )
			return;

		var axis = worst == gapX ? "X" : worst == gapY ? "Y" : "Z";

		var how = worst > 1e-6f
			? $"it clears the part by {worst:0.###} along {axis}"
			: $"it only touches the part along {axis}, enclosing none of it";

		throw new InvalidOperationException(
			$"This cut does not reach into the part — {how}, so there is nothing to take away. "
			+ "A profile drawn on a face extrudes into that face by default; check Flip direction, "
			+ "or increase the distance." );
	}

	/// <summary>How far two spans are apart. Zero or negative means they overlap.</summary>
	static float Gap( float aMin, float aMax, float bMin, float bMax ) =>
		MathF.Max( bMin - aMax, aMin - bMax );

	static void Extent( PolyMesh mesh, out Vec3 min, out Vec3 max )
	{
		min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
	}

	/// <summary>The body this result acts on — merges into, or cuts into — or null to start a new
	/// one.</summary>
	Body ResolveTarget( FeatureContext ctx )
	{
		if ( Result.Index == 1 )
			return null;

		var host = ResolveSketchId( ctx ) is { } id && ctx.SketchHostBodies.TryGetValue( id, out var bodyId )
			? ctx.Bodies.FirstOrDefault( b => b.Id == bodyId )
			: null;

		if ( host is not null )
			return host;

		// Auto with no host starts a new part. Add and Remove were asked for explicitly, so they
		// have to find something to act on.
		if ( Result.Index is not (2 or ResultRemove) )
			return null;

		// One body in the studio is unambiguous, so use it — that is the sketch-drawn-on-a-global-
		// plane-over-the-only-part case, which is how most cuts get drawn. More than one and there
		// is no way to tell which was meant, so say so instead of picking: a cut landing on the
		// wrong part is worse than a cut that did not happen.
		if ( ctx.Bodies.Count == 1 )
			return ctx.Bodies[0];

		var verb = Result.Index == ResultRemove ? "remove from" : "add to";

		throw new InvalidOperationException( ctx.Bodies.Count == 0
			? $"There is no body to {verb}. Set Result to New body, or draw the sketch on a face of an existing part."
			: $"There is more than one body and nothing says which to {verb}. Draw the sketch on a face of the part you mean, or set Result to New body." );
	}

	/// <summary>
	/// The regions this feature builds from: every closed region in the sketch, or just the one
	/// under RegionSeed when a face has been picked.
	///
	/// Instance rather than static because of the seed. A missing seed region throws by name
	/// rather than falling back to "all regions" — silently extruding the whole sketch because the
	/// face you picked stopped existing is exactly the kind of thing you notice three features
	/// later.
	/// </summary>
	protected List<Profile> ResolveProfiles( Sketch sketch )
	{
		var all = FindProfiles( sketch );

		if ( RegionSeed is not { } seed )
			return all;

		var picked = all.Where( p => p.Contains( seed ) ).ToList();

		if ( picked.Count == 0 )
		{
			throw new InvalidOperationException(
				"The selected region no longer exists — the sketch changed underneath it. "
				+ "Pick a region again, or clear the selection to use every closed region." );
		}

		return picked;
	}

	List<Profile> FindProfiles( Sketch sketch )
	{
		var found = ProfileFinder.Find( sketch );

		// ProfileFinder reports what it could not make sense of - a point where three curves meet,
		// for instance, which it will not guess at.
		//
		// This used to THROW whenever there was any such note, even with perfectly good regions
		// alongside it, so a single stray line left anywhere in a sketch failed every feature that
		// read it and there was no way to proceed but to hunt the stray down. Silently ignoring
		// them is the opposite mistake - that extruded one arbitrary sub-loop and looked like it
		// had worked. So: build from what did close, and say plainly what was skipped.
		if ( found.Warnings.Count > 0 && found.Profiles.Count > 0 )
		{
			Warning = $"Built from {found.Profiles.Count} closed region"
				+ (found.Profiles.Count == 1 ? "" : "s")
				+ $"; ignored: {string.Join( "; ", found.Warnings )}";
		}

		if ( found.Profiles.Count == 0 )
		{
			throw new InvalidOperationException( found.OpenChains > 0
				? "The sketch has no closed region — its curves do not join up"
				: "The sketch has no closed region" );
		}

		// Holes used to be refused HERE, for every consumer at once, on the grounds that capping
		// around one was "really the same problem as a boolean subtract". That was wrong: it is a
		// 2D triangulation problem, and ear clipping has been in the kernel for a while. Extrude
		// handles holes now; Revolve does not, and says so itself. The refusal belongs with whoever
		// cannot do it rather than in the shared path, or one feature's limit keeps standing in for
		// everyone's.
		return found.Profiles;
	}
}

/// <summary>
/// Extrudes sketch profiles into solids along the sketch plane's normal. Onshape's Extrude.
///
/// Caps are emitted as single n-gons rather than triangle fans. Catmull-Clark turns an n-gon into
/// n clean quads, so a hexagonal boss subdivides properly; a fan would leave a high-valence hub in
/// the middle of every face that puckers the moment anyone sculpts near it.
/// </summary>
public sealed class ExtrudeFeature : SketchConsumingFeature
{
	public override string TypeName => "Extrude";

	/// <summary>
	/// Where the extrude stops.
	///
	/// Blind is a typed distance and is what it has always done. The other two ask the model instead:
	/// UP TO NEXT stops at the first thing in the way, and THROUGH ALL goes past everything. Neither
	/// needs a boolean — both are questions about DISTANCE, answered by a raycast, and the solid they
	/// produce is an ordinary prism. That is worth saying because "up to face" sits next to "cut" in
	/// every CAD tool and reads like it must need one.
	/// </summary>
	public readonly ChoiceParam Termination = new( "Termination",
		new[] { "Blind", "Up to next", "Through all" } );

	public readonly FloatParam Distance = new( "Distance", 1f, unit: "u" );
	public readonly BoolParam Symmetric = new( "Symmetric", false );
	public readonly BoolParam Flip = new( "Flip direction", false );

	/// <summary>
	/// How far it also goes the OTHER way. Zero means one-sided, which is what it has always been.
	///
	/// Onshape calls this the second end position and gives it its own depth rather than a symmetric
	/// checkbox, because the two are not the same question: symmetric splits ONE distance in half,
	/// while this is genuinely independent — a boss 3 up and 1 down is a thing you cannot ask a
	/// symmetric extrude for at all. Symmetric wins when both are set, since it is the simpler
	/// intent and silently doubling up would be worse than ignoring one.
	/// </summary>
	public readonly FloatParam SecondDistance = new( "Second distance", 0f, 0f, unit: "u" );

	/// <summary>
	/// Draft angle, in degrees. Positive narrows toward the far end.
	///
	/// A moulded or cast part needs draft to come out of its tool, and a game asset usually wants it
	/// for the same reason it wants a bevel: a face that leans catches light instead of reading as a
	/// flat slab. It costs no boolean — the far cap is the near one offset by distance × tan(angle),
	/// and every wall leans by exactly that angle because both its ends are that far apart.
	/// </summary>
	public readonly FloatParam Taper = new( "Taper", 0f, -89f, 89f, unit: "deg" );

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 );

	/// <summary>
	/// Which way the extrude actually travels: +1 along the sketch plane's normal, -1 against it.
	///
	/// FLIP IS NOT THE WHOLE ANSWER, and that is the fix this method exists for. A sketch on a face
	/// takes that face's OUTWARD normal (FacePlane.Capture reads mesh.FaceNormal straight off the
	/// mesh), so the default direction points away from the solid. For a boss that is exactly right
	/// — it grows off the part. For a cut it is exactly backwards: the tool solid ends up floating
	/// outside the material, touching it on one plane and enclosing no common volume with it, and
	/// subtracting something that is not there removes nothing.
	///
	/// The engine's boolean reports that as "these two solids could not be combined - they may not
	/// overlap", which is true and reads like an adapter fault. It cost a session.
	///
	/// So a Remove whose sketch sits on a face of the body it is cutting defaults to travelling INTO
	/// that body, and Flip still means what it always meant: the other way from the sensible default.
	/// That is Onshape's behaviour too — picking Remove there points the arrow into the material.
	///
	/// Deliberately NOT applied when the sketch is on a global plane. The outward normal of a face
	/// says which way the material lies; a free-standing plane's normal says nothing of the kind, so
	/// there is nothing to infer from and guessing would be worse than the honest default.
	/// </summary>
	float DirectionSign( FeatureContext ctx )
	{
		var sign = Flip.Value ? -1f : 1f;

		return CutsIntoItsHostFace( ctx ) ? -sign : sign;
	}

	/// <summary>Removing material, through a sketch drawn on a face of the very body being cut.</summary>
	bool CutsIntoItsHostFace( FeatureContext ctx )
	{
		if ( Result.Index != ResultRemove )
			return false;

		if ( ResolveSketchId( ctx ) is not { } id || !ctx.SketchHostBodies.TryGetValue( id, out var bodyId ) )
			return false;

		// The host has to still be there. ResolveTarget falls back to "the only body" when it is
		// not, and that body is not necessarily the one the normal was measured against.
		return ctx.Bodies.Any( b => b.Id == bodyId );
	}

	public override IReadOnlyList<IParam> Parameters => Termination.Index == 0
		? new IParam[] { Sketch, Termination, Distance, SecondDistance, Symmetric, Flip, Taper, Result, Material }
		: new IParam[] { Sketch, Termination, Flip, Taper, Result, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var sketch = ResolveSketch( ctx );
		var profiles = ResolveProfiles( sketch );

		var sign = DirectionSign( ctx );

		var reach = Termination.Index == 0 ? Distance.Value : MeasuredDistance( ctx, sketch, profiles, sign );

		if ( MathF.Abs( reach ) < 1e-6f )
			throw new InvalidOperationException( "Distance cannot be zero" );

		var distance = reach * sign;
		var second = MathF.Abs( SecondDistance.Value );

		// Three ways to place the two ends, in priority order: symmetric splits the one distance,
		// a second distance runs back the other way from the plane, and otherwise it starts at the
		// plane. Flip mirrors all of it, which is why `second` is applied against the sign of
		// `distance` rather than against the plane's normal directly.
		var near = 0f;
		var far = distance;

		if ( Symmetric.Value )
		{
			near = -distance * 0.5f;
			far = distance * 0.5f;
		}
		else if ( second > 1e-6f )
		{
			near = distance >= 0f ? -second : second;
		}

		foreach ( var profile in profiles )
		{
			var mesh = BuildPrism( sketch.Plane, profile, near, far, Taper.Clamped, Material.Clamped );
			Emit( ctx, mesh );
		}
	}

	/// <summary>
	/// How far the extrude reaches when the model is what decides, rather than a typed number.
	///
	/// Rays are cast from inside the profile along the extrude direction and the NEAREST hit wins.
	/// Nearest rather than furthest because the solid has to stop at the first thing in the way; a
	/// further hit is something the first surface is already hiding.
	///
	/// THE CAP STAYS FLAT, and that is the honest limitation of doing this without a boolean. A real
	/// "up to face" trims the new solid against the target surface, so a boss meeting an angled face
	/// ends in a matching slope. This ends flat, at the nearest point of contact — exactly right when
	/// the target is parallel to the sketch, and short of it by a visible gap when it is not. Visible
	/// rather than silent, and warned about besides: if the sample rays disagree about the distance,
	/// the target is not parallel and the feature says so.
	/// </summary>
	float MeasuredDistance( FeatureContext ctx, Sketch sketch, List<Profile> profiles, float sign )
	{
		var direction = sketch.Plane.Normal * sign;

		// Everything already built. A sketch drawn on a face of one of these starts ON it, which is
		// what the epsilon below is for.
		var targets = ctx.Bodies.Where( b => b.Mesh is { FaceCount: > 0 } ).ToList();

		if ( targets.Count == 0 )
		{
			throw new InvalidOperationException( Termination.Index == 1
				? "Up to next needs something to stop at, and there is nothing else in the studio yet."
				: "Through all needs something to pass through, and there is nothing else in the studio yet." );
		}

		if ( Termination.Index == 2 )
			return ThroughAll( targets, sketch, direction );

		var nearest = float.MaxValue;
		var furthest = 0f;
		var hits = 0;

		foreach ( var origin in SampleOrigins( sketch, profiles ) )
		{
			if ( MeshRaycast.Raycast( targets, origin, direction ) is not { } hit )
				continue;

			// A sketch ON a face starts flush with it, and that face is not something to stop at —
			// it is where the extrude begins.
			if ( hit.Hit.Distance < 1e-4f )
				continue;

			nearest = MathF.Min( nearest, hit.Hit.Distance );
			furthest = MathF.Max( furthest, hit.Hit.Distance );
			hits++;
		}

		if ( hits == 0 )
		{
			throw new InvalidOperationException(
				"Up to next found nothing in the way — no face lies ahead of this profile. Use a blind "
				+ "distance, or flip the direction." );
		}

		if ( furthest - nearest > 1e-3f )
		{
			Warning = $"The face ahead is not parallel to the sketch: it is between {nearest:0.###} and "
				+ $"{furthest:0.###} away. The extrude stops flat at the nearest point, so it will not "
				+ "meet the far side.";
		}

		return nearest;
	}

	/// <summary>Far enough to clear everything: the furthest any target reaches along the direction,
	/// plus a margin. A prism that stops exactly on a surface is a coplanar face waiting to confuse
	/// whatever consumes it next.</summary>
	static float ThroughAll( List<Body> targets, Sketch sketch, Vec3 direction )
	{
		var origin = sketch.Plane.Origin;
		var reach = 0f;

		foreach ( var body in targets )
		{
			foreach ( var p in body.Mesh.Positions )
				reach = MathF.Max( reach, Vec3.Dot( p - origin, direction ) );
		}

		if ( reach <= 0f )
		{
			throw new InvalidOperationException(
				"Through all found nothing ahead of this profile — everything is behind it. Flip the direction." );
		}

		// Ten percent past the last thing it has to clear, and never less than a whole unit, so a
		// tiny model does not end up with a margin too small to matter.
		return reach + MathF.Max( reach * 0.1f, 1f );
	}

	/// <summary>
	/// Points inside the profile to cast from: its centroid plus each corner pulled toward that
	/// centroid.
	///
	/// The corners matter. Casting from the centroid alone reads one point of the target and calls
	/// it the answer, which is how a profile that overhangs an edge — or sits over a hole — measures
	/// against something it barely touches. Pulling each corner inward keeps every ray inside the
	/// material rather than balanced on its boundary.
	/// </summary>
	static IEnumerable<Vec3> SampleOrigins( Sketch sketch, List<Profile> profiles )
	{
		foreach ( var profile in profiles )
		{
			var loop = profile.Outer;
			var centroid = Vec2.Zero;

			foreach ( var p in loop )
				centroid += p;

			centroid /= loop.Count;

			yield return sketch.Plane.ToWorld( centroid );

			foreach ( var p in loop )
				yield return sketch.Plane.ToWorld( p + (centroid - p) * 0.05f );
		}
	}

	/// <summary>
	/// Outer loop arrives counter-clockwise in plane coordinates, which fixes every winding
	/// question: the far cap keeps that order, the near cap reverses, and the side quads run
	/// bottom edge then up. Verified by the enclosed-volume test rather than by inspection.
	///
	/// HOLES COST EXACTLY TWO THINGS. Each hole loop gets walls of its own, built by the same code
	/// as the outer ones — and because ProfileFinder hands holes back wound the opposite way, those
	/// walls face into the hole with no sign handling anywhere. And the caps can no longer be single
	/// n-gons, because a face with a hole in it is not a polygon; they are triangulated around the
	/// holes instead.
	///
	/// That cap is a real tradeoff and worth stating plainly. This kernel prefers n-gons because
	/// Catmull-Clark turns one into n clean quads, and a triangulated cap subdivides worse — the
	/// README's whole argument about quads applies. A holed profile has no n-gon available, so the
	/// choice is a triangulated cap or no feature at all, and a plate with bolt holes is hard
	/// surface that rarely gets subdivided anyway. Profiles WITHOUT holes are untouched and still
	/// get their single n-gon.
	/// </summary>
	static PolyMesh BuildPrism( SketchPlane plane, Profile profile, float near, float far, float taper, int material )
	{
		var mesh = new PolyMesh();

		// A negative extrusion puts the "far" cap behind the "near" one and flips the solid inside
		// out. Ordering them here means the rest of the function never has to think about sign.
		var (low, high) = near <= far ? (near, far) : (far, near);

		// Outer first, then each hole — the same order Triangulate.WithHoles indexes them in, which
		// is what lets its triples map straight onto these vertices.
		var loops = new List<List<Vec2>> { profile.Outer };
		loops.AddRange( profile.Holes );

		// TAPER IS APPLIED FROM THE START OF THE SWEEP, so the whole solid is one frustum and every
		// wall leans by exactly the angle asked for, whichever way the extrude runs.
		//
		// The alternative is worth naming rather than dismissing: measuring draft from the SKETCH
		// PLANE would make a symmetric extrude draft away from that plane in both directions, which
		// is what a moulded part with a parting line down its middle actually wants. Onshape does
		// that. This does the simpler thing, because one consistent lean is easier to reason about
		// and is what a game asset usually wants; if a parting-line draft is ever needed, it belongs
		// as its own option rather than as a hidden difference in what Symmetric means.
		var drawn = high - low;
		var inset = taper == 0f ? 0f : drawn * MathF.Tan( taper * MathF.PI / 180f );

		var highLoops = loops;

		if ( inset != 0f )
		{
			highLoops = new List<List<Vec2>>( loops.Count );

			foreach ( var loop in loops )
			{
				if ( !LoopOffset.TryOffset( loop, inset, out var offsetLoop, out var error ) )
				{
					throw new InvalidOperationException(
						$"A taper of {taper:0.##} degrees over {drawn:0.###} does not fit this profile: {error}. "
						+ "Use a shallower angle, a shorter distance, or a profile without such a narrow neck." );
				}

				highLoops.Add( offsetLoop );
			}
		}

		// Where each loop's low ring starts. Its high ring follows immediately after it.
		var lowStart = new int[loops.Count];
		var highStart = new int[loops.Count];

		for ( var index = 0; index < loops.Count; index++ )
		{
			lowStart[index] = mesh.Positions.Count;

			foreach ( var p in loops[index] )
				mesh.AddVertex( plane.ToWorld( p ) + plane.Normal * low );

			highStart[index] = mesh.Positions.Count;

			foreach ( var p in highLoops[index] )
				mesh.AddVertex( plane.ToWorld( p ) + plane.Normal * high );
		}

		for ( var index = 0; index < loops.Count; index++ )
			AddWalls( mesh, loops[index], lowStart[index], highStart[index], material );

		AddCaps( mesh, profile, loops, highLoops, lowStart, highStart, material );

		return mesh;
	}

	/// <summary>One loop's side wall. Cumulative perimeter drives U so the texture does not stretch
	/// on long edges.</summary>
	static void AddWalls( PolyMesh mesh, List<Vec2> loop, int lowStart, int highStart, int material )
	{
		var n = loop.Count;
		var perimeter = 0f;
		var distances = new float[n + 1];

		for ( var i = 0; i < n; i++ )
		{
			var a = loop[i];
			var b = loop[(i + 1) % n];
			distances[i] = perimeter;
			perimeter += MathF.Sqrt( (b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y) );
		}

		distances[n] = perimeter;

		for ( var i = 0; i < n; i++ )
		{
			var j = (i + 1) % n;
			var u0 = perimeter > 0f ? distances[i] / perimeter : 0f;
			var u1 = perimeter > 0f ? distances[i + 1] / perimeter : 1f;

			mesh.AddFace(
				new[] { lowStart + i, lowStart + j, highStart + j, highStart + i },
				new[] { new Vec2( u0, 0 ), new Vec2( u1, 0 ), new Vec2( u1, 1 ), new Vec2( u0, 1 ) },
				material );
		}
	}

	/// <summary>
	/// Top and bottom. Caps use plane coordinates directly as UVs, so a face keeps the proportions
	/// it was drawn with instead of being squashed into a unit square.
	/// </summary>
	static void AddCaps( PolyMesh mesh, Profile profile, List<List<Vec2>> loops, List<List<Vec2>> highLoops,
		int[] lowStart, int[] highStart, int material )
	{
		if ( !profile.HasHoles )
		{
			var loop = profile.Outer;
			var n = loop.Count;

			var topIndices = new int[n];
			var topUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				topIndices[i] = highStart[0] + i;

				// The TAPERED position, so a drafted face's texture follows the face it is on rather
				// than the shape it was drawn from.
				topUVs[i] = highLoops[0][i];
			}

			mesh.AddFace( topIndices, topUVs, material );

			var bottomIndices = new int[n];
			var bottomUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				bottomIndices[i] = lowStart[0] + n - 1 - i;
				bottomUVs[i] = loop[n - 1 - i];
			}

			mesh.AddFace( bottomIndices, bottomUVs, material );
			return;
		}

		// Flatten the loops into the one list WithHoles indexes against, so a triple it returns can
		// be read as "the nth point, counting outer first then each hole in turn".
		var flat = new List<Vec2>();
		var loopOf = new List<int>();
		var withinLoop = new List<int>();

		for ( var index = 0; index < loops.Count; index++ )
		{
			for ( var i = 0; i < loops[index].Count; i++ )
			{
				flat.Add( loops[index][i] );
				loopOf.Add( index );
				withinLoop.Add( i );
			}
		}

		// The top's own loops, which differ from the bottom's under taper. Triangulated separately
		// rather than reusing the bottom's triples: an inset loop can need a different bridge, and
		// forcing the bottom's onto it is how a tapered cap ends up with crossed triangles.
		var tapered = new List<Vec2>();

		foreach ( var loop in highLoops )
			tapered.AddRange( loop );

		var bottomTriangles = Triangulate.WithHoles( loops[0], loops.Skip( 1 ).Cast<IReadOnlyList<Vec2>>().ToList() );
		var topTriangles = ReferenceEquals( highLoops, loops )
			? bottomTriangles
			: Triangulate.WithHoles( highLoops[0], highLoops.Skip( 1 ).Cast<IReadOnlyList<Vec2>>().ToList() );

		if ( bottomTriangles.Count == 0 || topTriangles.Count == 0 )
		{
			throw new InvalidOperationException(
				$"This profile's {profile.Holes.Count} hole(s) could not be capped — the loops may cross each other. "
				+ "Check that every inner loop lies fully inside the outer one." );
		}

		foreach ( var (a, b, c) in topTriangles )
		{
			mesh.AddFace(
				new[] { High( a ), High( b ), High( c ) },
				new[] { tapered[a], tapered[b], tapered[c] },
				material );
		}

		// The bottom is the same surface seen from the other side, so it is wound backwards.
		foreach ( var (a, b, c) in bottomTriangles )
		{
			mesh.AddFace(
				new[] { Low( c ), Low( b ), Low( a ) },
				new[] { flat[c], flat[b], flat[a] },
				material );
		}

		int High( int flatIndex ) => highStart[loopOf[flatIndex]] + withinLoop[flatIndex];
		int Low( int flatIndex ) => lowStart[loopOf[flatIndex]] + withinLoop[flatIndex];
	}
}

/// <summary>
/// Revolves sketch profiles about an axis lying in the sketch plane. Onshape's Revolve.
///
/// Points sitting ON the axis are the awkward case — every revolved copy of them lands in the same
/// place. Rather than special-casing that, construction runs through the vertex welder, so those
/// copies collapse to one vertex, the quad next to them degenerates to a triangle, and a profile
/// touching the axis produces a proper closed solid. A full revolution closes the same way: the
/// last ring welds onto the first.
/// </summary>
public sealed class RevolveFeature : SketchConsumingFeature
{
	public override string TypeName => "Revolve";

	public readonly Vec3Param AxisPoint = new( "Axis through (sketch coords)", Vec3.Zero );
	public readonly Vec3Param AxisDirection = new( "Axis direction (sketch coords)", new Vec3( 1, 0, 0 ) );
	public readonly FloatParam Angle = new( "Angle", 360f, unit: "deg" );
	public readonly IntParam Segments = new( "Segments", 24, 3, 512 );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Sketch, AxisPoint, AxisDirection, Angle, Segments, Result, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var sketch = ResolveSketch( ctx );
		var profiles = ResolveProfiles( sketch );

		// PAST A FULL TURN THE SWEEP OVERLAPS ITSELF, and the overlap welds: the result comes back
		// with edges shared by four or more faces and no error reported. Only exactly +-360 is
		// treated as a full revolution, so 720 was never going to mean "twice round" - it meant
		// "a broken mesh, quietly".
		if ( MathF.Abs( Angle.Value ) > 360f + 1e-3f )
			throw new InvalidOperationException(
				$"A revolve cannot exceed a full turn ({Angle.Value} degrees) — past 360 the sweep passes through itself." );

		if ( MathF.Abs( Angle.Value ) < 1e-4f )
			throw new InvalidOperationException( "Angle cannot be zero" );

		var plane = sketch.Plane;

		// The axis is authored in sketch coordinates and lifted into world space, so it moves with
		// the plane like everything else in the sketch.
		var axisOrigin = plane.ToWorld( new Vec2( AxisPoint.Value.x, AxisPoint.Value.y ) );
		var axisDir = plane.XAxis * AxisDirection.Value.x + plane.YAxis * AxisDirection.Value.y;

		if ( axisDir.LengthSquared < 1e-12f )
			throw new InvalidOperationException( "Axis direction cannot be zero" );

		var full = MathF.Abs( MathF.Abs( Angle.Value ) - 360f ) < 1e-3f;

		foreach ( var profile in profiles )
		{
			// Every loop, not just the outer one. A hole straddling the axis is as meaningless as an
			// outer loop doing it, and for the same reason — each half sweeps the same surface.
			RejectIfCrossingAxis( profile.Outer );

			foreach ( var hole in profile.Holes )
				RejectIfCrossingAxis( hole );

			var mesh = BuildRevolve( plane, profile, axisOrigin, axisDir,
				Angle.Value, Segments.Clamped, full, Material.Clamped );

			OrientOutward( mesh );

			Emit( ctx, mesh );
		}
	}

	/// <summary>
	/// A profile straddling the axis is rejected, the way Onshape rejects it.
	///
	/// It is not merely unsupported, it is meaningless: each half sweeps the same solid, so every
	/// face is generated twice with opposite winding. The result passes a casual look, encloses
	/// zero volume, and welds vertices that should have stayed apart. Catching it here gives the
	/// user the real reason instead of a mesh that is quietly nonsense.
	/// </summary>
	void RejectIfCrossingAxis( List<Vec2> loop )
	{
		var a = new Vec2( AxisPoint.Value.x, AxisPoint.Value.y );
		var d = new Vec2( AxisDirection.Value.x, AxisDirection.Value.y );
		var length = MathF.Sqrt( d.x * d.x + d.y * d.y );

		if ( length < 1e-9f )
			return;

		var minSide = float.MaxValue;
		var maxSide = float.MinValue;

		foreach ( var p in loop )
		{
			// 2D cross product: signed perpendicular distance from the axis line.
			var side = (d.x * (p.y - a.y) - d.y * (p.x - a.x)) / length;
			minSide = MathF.Min( minSide, side );
			maxSide = MathF.Max( maxSide, side );
		}

		const float eps = 1e-5f;

		if ( minSide < -eps && maxSide > eps )
		{
			// Name the numbers. The default axis runs through the sketch origin, and people draw
			// around the origin, so this is the FIRST thing most Revolves hit - a message that just
			// says "move it" leaves you guessing which way and how far.
			throw new InvalidOperationException(
				$"The profile crosses the axis of revolution - it reaches {MathF.Abs( minSide ):0.###} "
				+ $"one side and {maxSide:0.###} the other. Move the axis at least {MathF.Abs( minSide ):0.###} "
				+ "so the whole profile sits on one side of it, or move the profile." );
		}
	}

	/// <summary>
	/// Flip every face if the finished solid encloses negative volume.
	///
	/// Whether a sweep comes out inside-out depends on the axis direction, the sign of the angle,
	/// and which side of the axis the profile sits on. Enumerating those cases invites getting one
	/// of them wrong silently; measuring the result instead is one cheap pass and is correct for
	/// all of them. Safe because a revolve is always closed — wrapped when full, capped when not.
	/// </summary>
	static void OrientOutward( PolyMesh mesh )
	{
		var volume = 0f;

		foreach ( var f in mesh.Faces )
			volume += Vec3.Dot( mesh.FaceCentroid( f ), mesh.FaceNormal( f ) ) * mesh.FaceArea( f );

		if ( volume >= 0f )
			return;

		foreach ( var f in mesh.Faces )
		{
			Array.Reverse( f.Indices );
			Array.Reverse( f.UVs );
		}
	}

	/// <summary>
	/// Sweep a profile around the axis.
	///
	/// HOLES COST THE SAME TWO THINGS THEY COST AN EXTRUDE. Every loop sweeps, not just the outer
	/// one — and because ProfileFinder hands holes back wound the opposite way, the hole's quads come
	/// out facing into the hole with no sign handling anywhere. And a partial revolution's two end
	/// caps stop being single n-gons, because a face with a hole in it is not a polygon; they are
	/// triangulated around the holes instead, exactly as BuildPrism's are, with the same tradeoff
	/// against subdivision quality and the same guarantee that unholed profiles keep their n-gons.
	///
	/// A FULL revolution needs no caps at all, so a holed profile revolved all the way round pays
	/// nothing for its holes beyond the extra sweep.
	/// </summary>
	static PolyMesh BuildRevolve(
		SketchPlane plane, Profile profile, Vec3 axisOrigin, Vec3 axisDir,
		float angleDegrees, int segments, bool full, int material )
	{
		var mesh = new PolyMesh();
		var weld = new VertexWelder( mesh );

		// Outer first, then each hole — the order Triangulate.WithHoles indexes them in, so its
		// triples map straight onto the rings built below.
		var loops = new List<List<Vec2>> { profile.Outer };
		loops.AddRange( profile.Holes );

		var rings = segments;
		var step = angleDegrees / segments * MathF.PI / 180f;

		// ring[loop][k][i] is loop point i rotated by k steps. A full turn reuses ring 0 as the last
		// ring, which the welder achieves on its own by landing on identical positions.
		var ring = new int[loops.Count][][];

		for ( var li = 0; li < loops.Count; li++ )
		{
			var loop = loops[li];
			ring[li] = new int[rings + 1][];

			for ( var k = 0; k <= rings; k++ )
			{
				ring[li][k] = new int[loop.Count];
				var xform = Xform.RotateAbout( axisOrigin, axisDir, step * k );

				for ( var i = 0; i < loop.Count; i++ )
					ring[li][k][i] = weld.Add( xform.TransformPoint( plane.ToWorld( loop[i] ) ) );
			}
		}

		for ( var li = 0; li < loops.Count; li++ )
		{
			var n = loops[li].Count;

			for ( var k = 0; k < rings; k++ )
			{
				for ( var i = 0; i < n; i++ )
				{
					var j = (i + 1) % n;

					var quad = new[] { ring[li][k][i], ring[li][k][j], ring[li][k + 1][j], ring[li][k + 1][i] };

					var uvs = new[]
					{
						new Vec2( k / (float)rings, i / (float)n ),
						new Vec2( k / (float)rings, (i + 1) / (float)n ),
						new Vec2( (k + 1) / (float)rings, (i + 1) / (float)n ),
						new Vec2( (k + 1) / (float)rings, i / (float)n )
					};

					AddNonDegenerate( mesh, quad, uvs, material );
				}
			}
		}

		// A partial revolution is open at both ends and needs capping; a full one is already
		// closed, and adding caps would leave two faces buried inside the solid.
		if ( full )
			return mesh;

		if ( !profile.HasHoles )
		{
			var loop = profile.Outer;
			var n = loop.Count;

			var startCap = new int[n];
			var startUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				startCap[i] = ring[0][0][n - 1 - i];
				startUVs[i] = loop[n - 1 - i];
			}

			AddNonDegenerate( mesh, startCap, startUVs, material );

			var endCap = new int[n];
			var endUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				endCap[i] = ring[0][rings][i];
				endUVs[i] = loop[i];
			}

			AddNonDegenerate( mesh, endCap, endUVs, material );

			return mesh;
		}

		// Flatten the loops into the single list WithHoles indexes against, so a triple it returns
		// reads as "the nth point, counting outer first then each hole in turn".
		var flat = new List<Vec2>();
		var loopOf = new List<int>();
		var withinLoop = new List<int>();

		for ( var li = 0; li < loops.Count; li++ )
		{
			for ( var i = 0; i < loops[li].Count; i++ )
			{
				flat.Add( loops[li][i] );
				loopOf.Add( li );
				withinLoop.Add( i );
			}
		}

		var triangles = Triangulate.WithHoles( profile.Outer, profile.Holes.Cast<IReadOnlyList<Vec2>>().ToList() );

		if ( triangles.Count == 0 )
		{
			throw new InvalidOperationException(
				$"This profile's {profile.Holes.Count} hole(s) could not be capped — the loops may cross each other. "
				+ "Check that every inner loop lies fully inside the outer one." );
		}

		foreach ( var (a, b, c) in triangles )
		{
			// The two caps are the same surface seen from opposite sides, so one is wound backwards.
			AddNonDegenerate( mesh,
				new[] { At( 0, c ), At( 0, b ), At( 0, a ) },
				new[] { flat[c], flat[b], flat[a] }, material );

			AddNonDegenerate( mesh,
				new[] { At( rings, a ), At( rings, b ), At( rings, c ) },
				new[] { flat[a], flat[b], flat[c] }, material );
		}

		return mesh;

		int At( int k, int flatIndex ) => ring[loopOf[flatIndex]][k][withinLoop[flatIndex]];
	}

	/// <summary>
	/// Add a face, dropping repeated indices first and skipping it entirely if fewer than three
	/// remain.
	///
	/// This is what makes a profile touching the axis work. Those points weld to a single vertex,
	/// so the quad beside them arrives as (a, a, b, c) — collapsing it gives the triangle the
	/// geometry actually wants, and a fully degenerate face disappears instead of becoming a
	/// zero-area sliver that breaks normals downstream.
	/// </summary>
	static void AddNonDegenerate( PolyMesh mesh, int[] indices, Vec2[] uvs, int material )
	{
		var keptIndices = new List<int>( indices.Length );
		var keptUVs = new List<Vec2>( indices.Length );

		for ( var i = 0; i < indices.Length; i++ )
		{
			// Compare against the previous kept corner, and wrap for the last one, so runs of
			// duplicates collapse whether they are adjacent or straddle the end of the face.
			if ( keptIndices.Count > 0 && keptIndices[^1] == indices[i] )
				continue;

			keptIndices.Add( indices[i] );
			keptUVs.Add( uvs[i] );
		}

		while ( keptIndices.Count > 1 && keptIndices[0] == keptIndices[^1] )
		{
			keptIndices.RemoveAt( keptIndices.Count - 1 );
			keptUVs.RemoveAt( keptUVs.Count - 1 );
		}

		if ( keptIndices.Count < 3 )
			return;

		mesh.AddFace( keptIndices.ToArray(), keptUVs.ToArray(), material );
	}
}
