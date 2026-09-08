using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A plane you place yourself, so a sketch has somewhere to live that is not one of the three
/// global planes and not a face that happens to already exist. Onshape's Plane feature; FreeCAD
/// calls it a datum plane.
///
/// WHAT THE ABSENCE OF THIS COSTS. A sketch could sit on Top, Front, Right, or on a face of a
/// solid, and that is the whole list. So a rib halfway up a part had to be drawn on a face at the
/// bottom and extruded a distance nobody wanted to think about; a wing at 30° could not be sketched
/// at all, only built square and rotated afterwards by a Transform that everything downstream then
/// had to be reasoned about through. Both of those are the same missing idea: a plane is a thing
/// you should be able to PUT somewhere, and everything else in the modeller was already ready for
/// one.
///
/// IT PRODUCES NO GEOMETRY, exactly like SketchFeature. It publishes into
/// <see cref="FeatureContext.Planes"/> and a later feature reads it back by this feature's id —
/// the same indirection sketches already use, and for the same reason: move this plane, rebuild,
/// and everything drawn on it follows without a second reference anywhere to keep in step.
///
/// THREE THINGS IT CAN BE BUILT FROM, and they are mutually exclusive:
///
///   a global plane   — Top / Front / Right, so a plane 12 units above the world floor is one
///                      number and never moves under you.
///   a face           — stored as a FaceRef, so the plane RIDES that face. Make the part taller and
///                      a plane 2 units off its top face is still 2 units off its top face. This is
///                      the one that makes the feature parametric rather than a bookmark.
///   another plane    — <see cref="BasePlaneId"/>, so planes stack. Three ribs at 10, 20 and 30 can
///                      each be "10 further on" rather than each measured from the floor, which is
///                      the difference between changing one number and changing three.
///
/// OFFSET FIRST, THEN TILT, and the tilt pivots where the offset left it (see SketchPlane.Tilted).
/// The other order — lean it over and then push it along its new normal — is a plane that slides
/// sideways as you change the angle, and "raise it 10 and lean it 30" stops describing where it
/// ended up.
/// </summary>
public sealed class PlaneFeature : Feature
{
	public override string TypeName => "Plane";

	/// <summary>A picked face is a plane to build from, the same click a sketch already accepts as
	/// its plane.</summary>
	public override GeometryKind Accepts => GeometryKind.Face;

	/// <summary>Which global plane this is built from, when it is built from one. Ignored when
	/// <see cref="Face"/> or <see cref="BasePlaneId"/> says otherwise — see ResolveBasePlane for
	/// the order.</summary>
	public readonly ChoiceParam Base = new( "From", new[] { "Top (XY)", "Front (XZ)", "Right (YZ)" } );

	public readonly FloatParam Offset = new( "Offset", 0f, unit: "u" );

	/// <summary>
	/// How far the plane leans off what it was built from.
	///
	/// BOUNDED AT A QUARTER TURN EITHER WAY rather than a half. Past 90° the plane has passed
	/// through edge-on and is coming back round to being parallel again, with its normal flipped —
	/// so every angle beyond that names a plane already reachable below it, and reaching one by
	/// dragging through the degenerate case in between is not a thing anyone was trying to do.
	/// </summary>
	public readonly FloatParam Angle = new( "Angle", 0f, -90f, 90f, unit: "deg" );

	/// <summary>Which of the plane's own two axes the <see cref="Angle"/> hinges on. See
	/// SketchPlane.Tilted for why it is an axis of the plane rather than an edge you point at.</summary>
	public readonly ChoiceParam Hinge = new( "Tilt about", new[] { "Its X axis", "Its Y axis" } );

	/// <summary>
	/// A face of an existing body to build from, instead of a global plane.
	///
	/// Stored as geometry and re-found on every rebuild, for the reason FaceRef's own header gives:
	/// an index would silently attach itself to a different face the moment anything upstream
	/// changed, which is the topological naming problem and is not a bug anyone enjoys finding.
	/// </summary>
	public FaceRef? Face;

	/// <summary>
	/// Feature id of another PlaneFeature to build from, instead of a global plane or a face. Empty
	/// for either of those.
	///
	/// Only a plane ABOVE this one in the tree can be named — a plane below it has not run, so it is
	/// not in the context and this refuses rather than reaching for last rebuild's answer. That
	/// makes a cycle unrepresentable rather than something to detect.
	/// </summary>
	public string BasePlaneId = "";

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Base, Offset, Angle, Hinge };

	/// <summary>
	/// Which way this plane's <see cref="Offset"/> travels, given the plane the last rebuild
	/// published for it.
	///
	/// IT IS NOT THE PUBLISHED PLANE'S OWN NORMAL, on any plane with an angle on it. Execute offsets
	/// FIRST and tilts afterwards, about the origin the offset left it at — so the origin slides
	/// along the BASE plane's normal and the frame then leans away from that direction. Read the
	/// published normal instead and you get the direction the plane is FACING, which past a few
	/// degrees is not the direction it MOVES.
	///
	/// WHO NEEDS TO KNOW: anything that drives Offset by pointing at the model rather than by typing
	/// a number — the viewport's offset handle. An arrow drawn along the published normal tracks the
	/// cursor on a flat plane and then, on a tilted one, drifts off it at cos(angle) of the rate,
	/// which reads as a broken handle rather than as a rotation.
	///
	/// TAKING THE TILT BACK OFF RECOVERS IT EXACTLY rather than approximately, and the reason is the
	/// hinge: a tilt turns the frame about one of its own axes, and an axis is left alone by its own
	/// rotation. So the hinge axis is the same vector before and after, and Tilted( -Angle ) about it
	/// undoes precisely the Tilted( Angle ) that Execute applied.
	/// </summary>
	public Vec3 OffsetAxis( SketchPlane published ) =>
		published is null ? Vec3.Zero : published.Tilted( -Angle.Clamped, Hinge.Index == 1 ).Normal;

	protected override void Execute( FeatureContext ctx )
	{
		// Clamped rather than raw, the same way every other bounded parameter in the kernel is read:
		// a document can carry any number a field once accepted, and the bound is the feature's rule
		// rather than the dialog's.
		var plane = ResolveBasePlane( ctx )
			.Offset( Offset.Value )
			.Tilted( Angle.Clamped, Hinge.Index == 1 );

		ctx.Planes[Id] = plane;

		// WHICH PART THIS PLANE GREW OUT OF, carried through so an extrude drawn on it can add to
		// that part rather than starting a new one. A sketch on a face already publishes this (see
		// SketchFeature.Execute) and a plane a few units off that same face means the same thing: a
		// boss standing clear of a surface is still part of the thing it stands on.
		//
		// It travels along a chain of planes, because a plane off a plane off a face is still about
		// that face. A plane built from a global plane clears the entry instead — sketching in space
		// starts a new part, and a plane that used to sit on a body must not keep merging into it
		// after being moved off.
		if ( HostBodyId( ctx ) is { Length: > 0 } host )
			ctx.PlaneHostBodies[Id] = host;
		else
			ctx.PlaneHostBodies.Remove( Id );
	}

	/// <summary>The body this plane is ultimately measured from, or null when it comes off a global
	/// plane.</summary>
	string HostBodyId( FeatureContext ctx )
	{
		if ( !string.IsNullOrEmpty( BasePlaneId ) )
			return ctx.PlaneHostBodies.TryGetValue( BasePlaneId, out var inherited ) ? inherited : null;

		return Face?.BodyId;
	}

	/// <summary>
	/// What this plane is built from, before the offset and the tilt.
	///
	/// THE ORDER IS THE MOST SPECIFIC ANSWER FIRST. All three can be set at once in a document — a
	/// plane moved from a face to a global plane leaves its FaceRef behind unless something clears
	/// it — so precedence has to be written down somewhere rather than left to whichever branch got
	/// tested. The editor's selector clears the other two on every pick, so in practice only one is
	/// ever set; this is what a file that disagrees gets.
	/// </summary>
	SketchPlane ResolveBasePlane( FeatureContext ctx )
	{
		if ( !string.IsNullOrEmpty( BasePlaneId ) )
		{
			if ( ctx.Planes.TryGetValue( BasePlaneId, out var parent ) )
				return parent;

			Fail(
				"The plane this one is built from is not there any more",
				"It was deleted, suppressed, or moved below this feature in the tree — a plane can "
				+ "only be built from one that has already run.",
				"Move this feature below the plane it is built from",
				"Unsuppress the plane it is built from",
				"Build this plane from a face or a global plane instead" );
		}

		if ( Face is not { } face )
		{
			return Base.Index switch
			{
				0 => SketchPlane.XY,
				1 => SketchPlane.XZ,
				2 => SketchPlane.YZ,
				_ => SketchPlane.XY
			};
		}

		if ( !FacePlane.TryResolve( ctx.Bodies, face, out var resolved ) )
		{
			Fail(
				"The face this plane was built from is gone — nothing at that point faces that way "
				+ "any more",
				"The stored face reference no longer matches any face of any body at this point in the tree.",
				"Build this plane from another face",
				"Build it from one of the global planes instead" );
		}

		return resolved;
	}
}
