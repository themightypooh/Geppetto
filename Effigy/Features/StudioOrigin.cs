using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Where Set Origin puts an origin.</summary>
public enum OriginTarget
{
	/// <summary>The middle of the bounding box.</summary>
	Geometry,
	/// <summary>The middle of the underside: the point a prop stands on.</summary>
	Bottom,
	/// <summary>A point the caller supplies — a click on the model, a landmark.</summary>
	Point,
	/// <summary>(0,0,0): the model exports where it was built.</summary>
	World,
}

/// <summary>
/// Set Origin: the arithmetic behind Object ▸ Origin to Geometry / Bottom / Clicked Point / World
/// Centre, and the build script's `pivot`.
///
/// TWO ORIGINS, BOTH SAVED. <see cref="PartStudio.Origin"/> is the model's: every whole-model
/// export (the editor's Compile, OBJ, FBX, DMX, and the script's `export`) subtracts it, so it is
/// where the model sits on (0,0,0) in the game. <see cref="PartStudio.BodyOrigins"/> holds one per
/// body, for exports that write each body as its own model (the script's `export split=1`): the
/// point a door swings about, a wheel turns about. Neither is read by the rebuild — moving an
/// origin changes what the writers subtract, never what the features build.
///
/// THE MEASURE IS THE BODIES' OWN MESHES AS BUILT, in the coordinates the features build in, which
/// is also the viewport's. Nothing here is cached: the bounds are read when the command runs, so a
/// later edit that grows the model does not drag the origin with it. That is what a modeller
/// expects of "origin to geometry" — it is a one-off placement, not a constraint.
/// </summary>
public static class StudioOrigin
{
	/// <summary>The bodies Set Origin measures when none is named: the ones that export.</summary>
	public static List<Body> Exported( PartStudio studio ) =>
		studio?.Bodies.Where( b => b?.Mesh is not null && b.Mesh.Positions.Count > 0 && b.Visible && !b.IsReference ).ToList()
		?? new List<Body>();

	/// <summary>
	/// The point <paramref name="target"/> means for <paramref name="bodies"/>. False, with a reason
	/// that says what to do, when the target needs geometry and there is none to measure.
	/// </summary>
	public static bool TryResolve( IReadOnlyCollection<Body> bodies, OriginTarget target, Vec3 point, out Vec3 origin, out string why )
	{
		origin = default;
		why = null;

		switch ( target )
		{
			case OriginTarget.World:
				return true;

			case OriginTarget.Point:
				if ( float.IsNaN( point.x ) || float.IsNaN( point.y ) || float.IsNaN( point.z ) )
				{
					why = "The picked point isn't a number. Click on the model, or on the ground past it.";
					return false;
				}

				origin = point;
				return true;
		}

		var meshes = bodies?.Where( b => b?.Mesh is not null && b.Mesh.Positions.Count > 0 ).ToList() ?? new List<Body>();

		if ( meshes.Count == 0 )
		{
			why = "There is no geometry to measure: add a body, or show the hidden ones, then set the origin again.";
			return false;
		}

		var (min, max) = ModelMeasure.Bounds( meshes );
		var centre = (min + max) * 0.5f;
		origin = target == OriginTarget.Bottom ? new Vec3( centre.x, centre.y, min.z ) : centre;
		return true;
	}

	/// <summary>
	/// Move the model's origin. Measures <paramref name="bodyIds"/> when any are given (the selection),
	/// otherwise every body that exports. <paramref name="message"/> says where it went, or why not.
	/// </summary>
	public static bool SetModel( PartStudio studio, OriginTarget target, IEnumerable<string> bodyIds, Vec3 point, out string message )
	{
		if ( studio is null )
		{
			message = "No model is open.";
			return false;
		}

		var ids = bodyIds?.ToHashSet() ?? new HashSet<string>();
		var bodies = ids.Count > 0
			? studio.Bodies.Where( b => ids.Contains( b.Id ) ).ToList()
			: Exported( studio );

		if ( !TryResolve( bodies, target, point, out var origin, out var why ) )
		{
			message = $"Origin not moved: {why}";
			return false;
		}

		var was = studio.Origin;
		studio.Origin = origin;

		var of = ids.Count == 0 ? "the model" : bodies.Count == 1 ? bodies[0].Name ?? "the selected body" : $"{bodies.Count} selected bodies";
		message = target switch
		{
			OriginTarget.World => $"Origin back at the world centre (was {Fmt( was )}). The model exports where it was built.",
			OriginTarget.Point => $"Origin at {Fmt( origin )} (was {Fmt( was )}). Exports put this point at (0,0,0).",
			OriginTarget.Bottom => $"Origin at the bottom centre of {of}, {Fmt( origin )} (was {Fmt( was )}). Exports stand it on (0,0,0).",
			_ => $"Origin at the centre of {of}, {Fmt( origin )} (was {Fmt( was )}). Exports put this point at (0,0,0).",
		};

		return true;
	}

	/// <summary>Give one body its own origin, used when each body is exported as its own model.</summary>
	public static bool SetBody( PartStudio studio, string bodyId, OriginTarget target, Vec3 point, out string message )
	{
		var body = studio?.Bodies.FirstOrDefault( b => b.Id == bodyId );

		if ( body is null )
		{
			message = $"Origin not moved: there is no body '{bodyId}' in the model.";
			return false;
		}

		if ( !TryResolve( new[] { body }, target, point, out var origin, out var why ) )
		{
			message = $"Origin not moved: {why}";
			return false;
		}

		studio.BodyOrigins[body.Id] = origin;
		message = $"{body.Name ?? body.Id}: origin at {Fmt( origin )}.";
		return true;
	}

	/// <summary>The body's own saved origin, if it has one.</summary>
	public static bool TryBody( PartStudio studio, Body body, out Vec3 origin )
	{
		origin = default;
		return studio is not null && body?.Id is not null && studio.BodyOrigins.TryGetValue( body.Id, out origin );
	}

	/// <summary>Whether the model's origin has been moved off the world centre.</summary>
	public static bool HasModelOrigin( PartStudio studio ) => studio is not null && studio.Origin.Length > 1e-6f;

	static string Fmt( Vec3 v ) => $"({N( v.x )}, {N( v.y )}, {N( v.z )})";
	static string N( float v ) => MathF.Abs( v ) < 0.0005f ? "0" : v.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture );
}
