using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Parametric parts for kitbashing: a cog, a bolt, a rivet, a hinge, a pipe elbow, a strap, a
/// buckle. Each is a few numbers, and each lands at a landmark. Composing named parts is a much
/// better fit for an agent than shaping a surface from nothing, and it is how a lot of hard
/// surface work is actually done by people too.
///
/// LATHED WHERE POSSIBLE. A bolt, a rivet and a knob are one profile turned about an axis, which
/// makes them a single closed manifold; the hinge and the buckle are appended pieces, so they are
/// one body with internal faces, the way a boolean Add leaves things (see Emit's comment in
/// SketchFeatures). Every part is built standing on z = 0 at the origin, pointing up, so
/// Position and Rotation on the feature mean what they say.
/// </summary>
public static class PartLibrary
{
	public static readonly string[] Names = { "Cog", "Bolt", "Hex bolt", "Rivet", "Knob", "Hinge", "Panel", "Pipe elbow", "Strap", "Buckle" };

	/// <summary>What each part reads from the dials, so a dialog and a script can say it.</summary>
	public static string Uses( string part ) => part switch
	{
		"Cog" => "Size = outer radius, Thickness = height, Count = teeth, Length = tooth depth",
		"Bolt" or "Hex bolt" => "Size = head radius, Length = shank length, Thickness = shank radius, Count = sides",
		"Rivet" => "Size = radius, Thickness = height, Count = sides",
		"Knob" => "Size = radius, Length = height, Count = sides",
		"Hinge" => "Size = knuckle radius, Length = hinge length, Thickness = leaf thickness, Count = knuckles, Angle = how far open",
		"Panel" => "Size = width, Length = height, Thickness = thickness",
		"Pipe elbow" => "Size = pipe radius, Length = bend radius, Angle = bend, Count = sides",
		"Strap" => "Size = width, Length = length, Thickness = thickness",
		"Buckle" => "Size = width, Length = height, Thickness = bar thickness",
		_ => "",
	};

	public static PolyMesh Build( string part, float size, float length, float thickness, int count, float angle, int material = 0 )
	{
		size = MathF.Max( size, 0.01f );
		length = MathF.Max( length, 0.01f );
		thickness = MathF.Max( thickness, 0.01f );
		count = Math.Max( count, 3 );

		return part switch
		{
			"Cog" => Cog( size, thickness, count, length, material ),
			"Bolt" => Bolt( size, length, thickness, Math.Max( count, 8 ), material ),
			"Hex bolt" => Bolt( size, length, thickness, 6, material ),
			"Rivet" => Rivet( size, thickness, count, material ),
			"Knob" => Knob( size, length, count, material ),
			"Hinge" => Hinge( size, length, thickness, Math.Max( count, 2 ), angle, material ),
			"Panel" => Standing( Primitives.Box( size, thickness, length, material ), length ),
			"Pipe elbow" => PipeElbow( size, length, angle, count, material ),
			"Strap" => Standing( Primitives.Box( length, size, thickness, material ), thickness ),
			"Buckle" => Buckle( size, length, thickness, material ),
			_ => throw new ArgumentException( $"No part called '{part}'. Parts are {string.Join( ", ", Names )}.", nameof( part ) ),
		};
	}

	/// <summary>A centred primitive lifted to stand on z = 0.</summary>
	static PolyMesh Standing( PolyMesh mesh, float height )
	{
		MeshTransform.Apply( mesh, Xform.Translate( new Vec3( 0, 0, height * 0.5f ) ) );
		return mesh;
	}

	/// <summary>Lift or drop a mesh so its lowest vertex is on z = 0, for parts whose low point
	/// moves with their dials (an open hinge leaf, an elbow's tilted end).</summary>
	static PolyMesh Grounded( PolyMesh mesh )
	{
		if ( mesh.Positions.Count == 0 )
			return mesh;

		var minZ = mesh.Positions.Min( p => p.z );
		MeshTransform.Apply( mesh, Xform.Translate( new Vec3( 0, 0, -minZ ) ) );
		return mesh;
	}

	/// <summary>Rings of a profile turned about z: (radius, height) pairs, bottom first. A radius
	/// of zero at either end becomes a point via the cap.</summary>
	public static PolyMesh Lathe( IReadOnlyList<(float R, float Z)> profile, int sides, int material )
	{
		var rings = new List<List<Vec3>>();

		foreach ( var (r, z) in profile )
		{
			var ring = new List<Vec3>( sides );
			var radius = MathF.Max( r, 1e-4f );

			for ( var k = 0; k < sides; k++ )
			{
				var a = k * MathF.PI * 2f / sides;
				ring.Add( new Vec3( MathF.Cos( a ) * radius, MathF.Sin( a ) * radius, z ) );
			}

			rings.Add( ring );
		}

		return Skinner.Skin( rings, capEnds: true, material );
	}

	/// <summary>A flat outline extruded up by <paramref name="height"/>, standing on z = 0.</summary>
	public static PolyMesh Extrude( IReadOnlyList<Vec2> outline, float height, int material )
	{
		var bottom = outline.Select( p => new Vec3( p.x, p.y, 0f ) ).ToList();
		var top = outline.Select( p => new Vec3( p.x, p.y, height ) ).ToList();
		return Skinner.Skin( new[] { bottom, top }, capEnds: true, material );
	}

	static PolyMesh Cog( float radius, float height, int teeth, float depth, int material )
	{
		depth = MathF.Min( MathF.Max( depth, radius * 0.05f ), radius * 0.6f );
		var inner = radius - depth;
		var outline = new List<Vec2>();

		// Four points per tooth: root, rise, crown, fall — a trapezoid tooth, which is what a
		// drawn cog looks like and what reads as one at any size.
		for ( var t = 0; t < teeth; t++ )
		{
			var a0 = t * MathF.PI * 2f / teeth;
			var step = MathF.PI * 2f / teeth;

			outline.Add( Polar( inner, a0 ) );
			outline.Add( Polar( inner, a0 + step * 0.35f ) );
			outline.Add( Polar( radius, a0 + step * 0.45f ) );
			outline.Add( Polar( radius, a0 + step * 0.80f ) );
		}

		return Extrude( outline, height, material );
	}

	static Vec2 Polar( float r, float a ) => new( MathF.Cos( a ) * r, MathF.Sin( a ) * r );

	static PolyMesh Bolt( float headRadius, float shankLength, float shankRadius, int sides, int material )
	{
		shankRadius = MathF.Min( shankRadius, headRadius * 0.9f );
		var headHeight = headRadius * 0.7f;

		return Lathe( new (float, float)[]
		{
			(shankRadius, 0f),
			(shankRadius, shankLength),
			(headRadius, shankLength),
			(headRadius, shankLength + headHeight * 0.85f),
			(headRadius * 0.85f, shankLength + headHeight),
		}, sides, material );
	}

	static PolyMesh Rivet( float radius, float height, int sides, int material )
	{
		// A dome: a quarter circle of profile, standing on a short cylinder.
		var profile = new List<(float, float)> { (radius, 0f), (radius, height * 0.25f) };

		for ( var i = 1; i <= 5; i++ )
		{
			var a = i / 5f * MathF.PI * 0.5f;
			profile.Add( (MathF.Cos( a ) * radius, height * 0.25f + MathF.Sin( a ) * height * 0.75f) );
		}

		return Lathe( profile, sides, material );
	}

	static PolyMesh Knob( float radius, float height, int sides, int material ) =>
		Lathe( new (float, float)[]
		{
			(radius * 0.8f, 0f),
			(radius * 0.8f, height * 0.15f),
			(radius * 0.6f, height * 0.2f),
			(radius * 0.6f, height * 0.5f),
			(radius, height * 0.6f),
			(radius, height * 0.9f),
			(radius * 0.7f, height),
		}, sides, material );

	static PolyMesh Hinge( float radius, float length, float leaf, int knuckles, float openDegrees, int material )
	{
		var mesh = new PolyMesh();
		var pitch = length / knuckles;
		var gap = pitch * 0.08f;

		// Knuckles along y, alternating leaves; the pin axis runs along y at the origin.
		for ( var k = 0; k < knuckles; k++ )
		{
			var barrel = Primitives.Cylinder( radius, pitch - gap, 12, material );
			MeshTransform.Apply( barrel, Xform.Rotate( new Vec3( 1, 0, 0 ), -MathF.PI * 0.5f ) );
			MeshTransform.Apply( barrel, Xform.Translate( new Vec3( 0, -length * 0.5f + k * pitch + gap * 0.5f + (pitch - gap) * 0.5f, 0 ) ) );
			MeshTransform.Append( mesh, barrel );
		}

		var leafWidth = radius * 4f;
		var open = openDegrees * MathF.PI / 180f;

		var left = Primitives.Box( leafWidth, length, leaf, material );
		MeshTransform.Apply( left, Xform.Translate( new Vec3( -leafWidth * 0.5f - radius * 0.6f, 0, 0 ) ) );
		MeshTransform.Append( mesh, left );

		var right = Primitives.Box( leafWidth, length, leaf, material );
		MeshTransform.Apply( right, Xform.Translate( new Vec3( leafWidth * 0.5f + radius * 0.6f, 0, 0 ) ) );
		MeshTransform.Apply( right, Xform.Rotate( new Vec3( 0, 1, 0 ), open ) );
		MeshTransform.Append( mesh, right );

		return Grounded( mesh );
	}

	static PolyMesh PipeElbow( float radius, float bendRadius, float degrees, int sides, int material )
	{
		bendRadius = MathF.Max( bendRadius, radius * 1.05f );
		var angle = Math.Clamp( degrees, 5f, 355f ) * MathF.PI / 180f;
		var stations = Math.Max( 4, (int)MathF.Ceiling( angle / (MathF.PI / 16f) ) );
		var path = new List<Vec3>();

		// From the origin, running along +x, bending up toward +z about a centre above the start.
		for ( var i = 0; i <= stations; i++ )
		{
			var a = angle * i / stations;
			path.Add( new Vec3( MathF.Sin( a ) * bendRadius, 0f, bendRadius - MathF.Cos( a ) * bendRadius ) );
		}

		return Grounded( SplineTube.Build( path, false, radius, radius, sides, 0f, material ) );
	}

	static PolyMesh Buckle( float width, float height, float bar, int material )
	{
		var mesh = new PolyMesh();
		bar = MathF.Min( bar, MathF.Min( width, height ) * 0.4f );

		void Piece( float sx, float sy, float x, float y )
		{
			var box = Primitives.Box( sx, sy, bar, material );
			MeshTransform.Apply( box, Xform.Translate( new Vec3( x, y, bar * 0.5f ) ) );
			MeshTransform.Append( mesh, box );
		}

		// A frame lying flat: two long bars along y, two short across x, plus the centre bar.
		Piece( bar, height, -width * 0.5f + bar * 0.5f, 0f );
		Piece( bar, height, width * 0.5f - bar * 0.5f, 0f );
		Piece( width - bar * 2f, bar, 0f, height * 0.5f - bar * 0.5f );
		Piece( width - bar * 2f, bar, 0f, -height * 0.5f + bar * 0.5f );
		Piece( width - bar * 2f, bar * 0.7f, 0f, 0f );

		return mesh;
	}
}

/// <summary>A part from the library, placed. See <see cref="PartLibrary"/> for what each dial means.</summary>
public sealed class PartFeature : Feature
{
	public override string TypeName => "Part";

	public readonly ChoiceParam Part = new( "Part", PartLibrary.Names );
	public readonly FloatParam Size = new( "Size", 4f, 0.01f, unit: "u" );
	public readonly FloatParam Length = new( "Length", 8f, 0.01f, unit: "u" );
	public readonly FloatParam Thickness = new( "Thickness", 1f, 0.01f, unit: "u" );
	public readonly IntParam Count = new( "Count", 12, 3, 128 );
	public readonly FloatParam Angle = new( "Angle", 90f, 0f, 360f, unit: "deg" );
	public readonly Vec3Param Position = new( "Position", Vec3.Zero );
	public readonly Vec3Param RotationAxis = new( "Rotation axis", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam RotationAngle = new( "Rotation", 0f, unit: "deg" );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Part, Size, Length, Thickness, Count, Angle, Position, RotationAxis, RotationAngle, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { RotationAxis, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var mesh = PartLibrary.Build( Part.Value, Size.Clamped, Length.Clamped, Thickness.Clamped, Count.Clamped, Angle.Clamped, Material.Clamped );

		if ( RotationAngle.Value != 0f && RotationAxis.Value.LengthSquared > 1e-8f )
			MeshTransform.Apply( mesh, Xform.Rotate( RotationAxis.Value.Normal, RotationAngle.Value * MathF.PI / 180f ) );

		if ( Position.Value.LengthSquared > 0f )
			MeshTransform.Apply( mesh, Xform.Translate( Position.Value ) );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? Part.Value, mesh ) );
	}
}
