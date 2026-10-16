using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Profile body upgrades (effigy-gaps P1-8): a custom stacking direction, cross-section shape that
/// changes with height, an outline met by the SMOOTHED surface rather than the cage, and limbs
/// grown out of a torso as one quad mesh. Every check is a number, not a picture.
/// </summary>
public static class ProfileUpgradeTests
{
	static readonly List<Vec2> Front = new() { new( 0, 2 ), new( 5, 3 ), new( 10, 1 ) };
	static readonly List<Vec2> Side = new() { new( 0, 1.5f ), new( 5, 2.5f ), new( 10, 1 ) };

	public static void Run()
	{
		CustomDirection();
		CrossSectionCurves();
		LimitOperator();
		LimitFit();
		Branching();
	}

	// --- 1. along=x,y,z --------------------------------------------------------------------------

	static void CustomDirection()
	{
		Report.Section( "profile: custom direction (along=x,y,z)" );

		var up = ProfileBody.Build( Front, Side, new ProfileShape { Axis = 2 } ).Mesh;
		var customUp = ProfileBody.Build( Front, Side, new ProfileShape { Axis = ProfileBody.AxisCustom, Direction = new Vec3( 0, 0, 5 ) } ).Mesh;
		var same = up.Positions.Zip( customUp.Positions, ( a, b ) => (a - b).Length ).Max();
		Report.Check( "custom direction 0,0,1 is exactly Up", same < 1e-5f, $"{same:g3}" );

		var dir = new Vec3( 1, 0, 1 ).Normal;
		var slanted = ProfileBody.Build( Front, Side, new ProfileShape { Axis = ProfileBody.AxisCustom, Direction = new Vec3( 1, 0, 1 ) } );
		var topCentre = RingCentre( slanted.Mesh, slanted.Rings - 1, slanted.Sides );
		Report.Check( "slanted profile runs along its direction", (topCentre - dir * 10f).Length < 1e-3f, $"{topCentre}" );
		Report.Check( "slanted profile winds outward with the same volume as Up",
			slanted.Mesh.SignedVolume() > 0f && MathF.Abs( slanted.Mesh.SignedVolume() - up.SignedVolume() ) < 1e-3f * up.SignedVolume(),
			$"{slanted.Mesh.SignedVolume():0.###} vs {up.SignedVolume():0.###}" );

		var down = ProfileBody.Build( Front, Side, new ProfileShape { Axis = ProfileBody.AxisCustom, Direction = new Vec3( 0, 0, -1 ) } ).Mesh;
		Report.Check( "straight-down direction hangs below the origin and winds outward",
			down.Positions.Max( p => p.z ) < 1e-4f && down.Positions.Min( p => p.z ) < -9.99f && down.SignedVolume() > 0f, $"{down.SignedVolume():0.###}" );

		// Roll turns the section: rolled 90 degrees, the width (3 at its widest) runs along x.
		var rolled = ProfileBody.Build( Front, Side, new ProfileShape { Axis = ProfileBody.AxisCustom, Direction = new Vec3( 0, 0, 1 ), Roll = 90f } ).Mesh;
		var xExtent = rolled.Positions.Max( p => MathF.Abs( p.x ) );
		Report.Check( "roll 90 puts the width across x", MathF.Abs( xExtent - 3f ) < 1e-3f, $"{xExtent:0.###}" );

		var studio = new PartStudio();
		var result = BuildScript.Run( studio, "add Profile name=Horn along=1,0,1 front=0,2;10,0.5 position=0,0,20" );
		var horn = ModelMeasure.FindBody( studio, "Horn" );
		var feature = studio.Features.OfType<ProfileFeature>().FirstOrDefault();
		Report.Check( "script along=1,0,1 sets a custom direction", result.Ok && horn is not null && feature?.Along.Index == ProfileBody.AxisCustom
			&& (feature.AlongDirection.Value - new Vec3( 1, 0, 1 )).Length < 1e-6f, result.ToString() );

		if ( horn is not null )
		{
			var tip = horn.Mesh.Positions.OrderByDescending( p => p.x + p.z ).First();
			Report.Check( "the horn's far end sits along 1,0,1 from its position", Vec3.Dot( tip - new Vec3( 0, 0, 20 ), dir ) > 9.9f, $"{tip}" );
		}

		var bad = BuildScript.Run( new PartStudio(), "add Profile along=1,2 front=0,2;10,1" );
		Report.Check( "along=1,2 is refused with what to write", !bad.Ok && bad.Error.Contains( "x,y,z" ), bad.Error );

		var zero = BuildScript.Run( new PartStudio(), "add Profile along=0,0,0 front=0,2;10,1" );
		Report.Check( "a zero direction is refused with a remedy", !zero.Ok && zero.Error.Contains( "zero" ), zero.Error );

		var named = BuildScript.Run( new PartStudio(), "add Profile along=Left front=0,2;10,1" );
		Report.Check( "named directions still read (along=Left)", named.Ok, named.ToString() );
	}

	// --- 2. per-height cross-section ---------------------------------------------------------------

	static void CrossSectionCurves()
	{
		Report.Section( "profile: cross-section shape by height" );

		var round = new List<Vec2> { new( 0, 2 ), new( 10, 2 ) };

		var squared = ProfileBody.Build( round, round, new ProfileShape
		{
			Sides = 16,
			SquarenessCurve = new List<Vec2> { new( 0, 0 ), new( 10, 1 ) },
		} );

		// Vertex 2 of 16 is the 45-degree point: on a circle of radius 2 it is 2 out; squarer is further.
		var bottomDiagonal = Radial( squared.Mesh.Positions[2] );
		var topDiagonal = Radial( squared.Mesh.Positions[(squared.Rings - 1) * 16 + 2] );
		Report.Check( "squareness 0 at the bottom is round", MathF.Abs( bottomDiagonal - 2f ) < 1e-3f, $"{bottomDiagonal:0.###}" );
		Report.Check( "squareness 1 at the top is boxy", topDiagonal > 2.5f, $"{topDiagonal:0.###}" );

		var flattened = ProfileBody.Build( round, round, new ProfileShape
		{
			Sides = 16,
			Squareness = 0f,
			Flatten = new List<Vec2> { new( 0, 0 ), new( 10, 1 ) },
		} );

		var top = TopRing( flattened );
		var bottom = flattened.Mesh.Positions.Take( 16 ).ToList();
		Report.Check( "flatten 1 makes a flat back (no depth behind the centre)", top.Min( p => p.x ) > -1e-4f, $"{top.Min( p => p.x ):0.###}" );
		Report.Check( "flatten leaves the front and the bottom alone",
			MathF.Abs( top.Max( p => p.x ) - 2f ) < 1e-3f && MathF.Abs( bottom.Min( p => p.x ) + 2f ) < 1e-3f );

		var frontFlat = ProfileBody.Build( round, round, new ProfileShape { Sides = 16, Squareness = 0f, Flatten = new List<Vec2> { new( 0, -1 ), new( 10, -1 ) } } );
		Report.Check( "negative flatten flattens the front instead", frontFlat.Mesh.Positions.Max( p => p.x ) < 1e-4f && frontFlat.Mesh.Positions.Min( p => p.x ) < -1.99f );

		var keeled = ProfileBody.Build( round, round, new ProfileShape { Sides = 16, Squareness = 0f, Keel = new List<Vec2> { new( 0, 0.5f ), new( 10, 0.5f ) } } );
		var keelFront = keeled.Mesh.Positions[0];
		var keelSide = keeled.Mesh.Positions[4];
		Report.Check( "keel 0.5 pushes the front centre out by half the depth", MathF.Abs( keelFront.x - 3f ) < 1e-3f, $"{keelFront.x:0.###}" );
		Report.Check( "keel leaves the flanks where they were", MathF.Abs( keelSide.y - 2f ) < 1e-3f && MathF.Abs( keelSide.x ) < 1e-3f, $"{keelSide}" );

		var studio = new PartStudio();
		var ok = BuildScript.Run( studio, "add Profile name=Chest front=0,4;20,5 squarenesscurve=0,0.1;20,0.8 flatten=0,0;20,0.5 keel=10,0.2" );
		Report.Check( "the three curves set from a script build", ok.Ok && ModelMeasure.FindBody( studio, "Chest" ) is not null, ok.ToString() );

		var typo = BuildScript.Run( new PartStudio(), "add Profile front=0,4;20,5 keel=10;0.2" );
		Report.Check( "a keel that does not read is refused, not ignored", !typo.Ok && typo.Error.Contains( "height,amount" ), typo.Error );
	}

	// --- 3. fit=limit ------------------------------------------------------------------------------

	static void LimitOperator()
	{
		Report.Section( "subdivision limit positions" );

		// The limit point is where repeated subdivision takes a vertex; originals stay first in the
		// layout at every level, so six levels of a cube put vertex i within a hair of its limit.
		foreach ( var (name, cage) in new[] { ("cube", Primitives.Box( 2, 3, 4 )), ("cylinder", Primitives.Cylinder( 1, 3, 7 )), ("open plane", Primitives.Plane( 4, 4, 3, 3 )) } )
		{
			var limit = SubdivisionLimit.LimitPositions( cage );
			var deep = CatmullClark.Subdivide( cage, 6 );
			var miss = 0f;

			for ( var i = 0; i < cage.VertexCount; i++ )
				miss = MathF.Max( miss, (deep.Positions[i] - limit[i]).Length );

			Report.Check( $"limit positions match deep subdivision ({name})", miss < 5e-3f, $"{miss:g3}" );
		}
	}

	static void LimitFit()
	{
		Report.Section( "profile: fit=limit (the smoothed surface meets the outline)" );

		var leg = new List<Vec2> { new( 0, 2 ), new( 10, 2.5f ), new( 20, 3 ) };

		var plain = ProfileBody.Build( leg, leg, new ProfileShape { Sides = 16, QuadCaps = true } );
		var fitted = ProfileBody.Build( leg, leg, new ProfileShape { Sides = 16, QuadCaps = true, FitLimit = true } );

		var (plainLo, plainHi) = LimitBounds( plain.Mesh );
		var (lo, hi) = LimitBounds( fitted.Mesh );

		Report.Check( "without the fit, a subdivided leg ends short of the ground", plainLo.z > 0.2f, $"bottom at {plainLo.z:0.###}" );
		Report.Check( "fit=limit: the smoothed leg reaches the ground", MathF.Abs( lo.z ) < 0.02f, $"bottom at {lo.z:0.####}" );
		Report.Check( "fit=limit: the smoothed leg reaches its top height", MathF.Abs( hi.z - 20f ) < 0.02f, $"top at {hi.z:0.####}" );
		Report.Check( "fit=limit: the smoothed width matches the outline", MathF.Abs( hi.y - 3f ) < 0.06f && MathF.Abs( hi.x - 3f ) < 0.06f, $"{hi.x:0.###}, {hi.y:0.###} vs 3" );
		Report.Check( "fit=limit: every vertex's limit point lands on its outline target", fitted.FitMiss < 5e-3f, $"{fitted.FitMiss:g3}" );

		// Each ring's limit points sit on the outline at that height.
		var ringLimit = SubdivisionLimit.LimitPositions( fitted.Mesh );
		var midSide = ringLimit[1 * 16 + 4];
		Report.Check( "the middle ring's limit is 2.5 wide at height 10", MathF.Abs( midSide.y - 2.5f ) < 5e-3f && MathF.Abs( midSide.z - 10f ) < 5e-3f, $"{midSide}" );

		// One-face caps cannot be pinned at the centre: the feature builds and says so.
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Profile name=Leg front=0,2;10,2.5;20,3 fit=limit" );
		var feature = studio.Features.OfType<ProfileFeature>().First();
		Report.Check( "fit=limit with one-face caps warns that the ends stop short", r.Ok && feature.Warning is not null && feature.Warning.Contains( "short" ), feature.Warning ?? "(no warning)" );

		studio = new PartStudio();
		r = BuildScript.Run( studio, "add Profile name=Leg front=0,2;10,2.5;20,3 fit=limit caps=quads" );
		feature = studio.Features.OfType<ProfileFeature>().First();
		Report.Check( "fit=limit caps=quads builds clean from a script", r.Ok && feature.Warning is null && ModelMeasure.FindBody( studio, "Leg" ) is not null, feature.Warning ?? r.ToString() );

		studio = new PartStudio();
		r = BuildScript.Run( studio, "add Profile name=Leg front=0,2;20,3 caps=quads sides=7" );
		feature = studio.Features.OfType<ProfileFeature>().First();
		Report.Check( "quad caps on an odd side count warns and names the fix", r.Ok && feature.Warning is not null && feature.Warning.Contains( "one face" ), feature.Warning ?? "(no warning)" );

		var quadOnly = plain.Mesh.Faces.All( f => f.Count == 4 );
		var check = MeshValidator.Validate( plain.Mesh );
		Report.Check( "quad caps: the profile is closed, manifold and all quads", quadOnly && check.IsValid && check.IsClosed && MeshValidator.EulerCharacteristic( plain.Mesh ) == 2, check.ToString() );
		Report.Check( "quad caps wind outward", plain.Mesh.SignedVolume() > 0f, $"{plain.Mesh.SignedVolume():0.###}" );
	}

	// --- 4. branching ------------------------------------------------------------------------------

	static void Branching()
	{
		Report.Section( "profile: branch a limb out of a torso" );

		const string torso = "add Profile name=Torso front=0,4;2.5,4;5,4;7.5,4;10,4;12.5,4;15,4;17.5,4;20,4 sides=16 caps=quads";

		var studio = new PartStudio();
		var r = BuildScript.Run( studio, torso );
		var torsoVolume = ModelMeasure.FindBody( studio, "Torso" ).Mesh.SignedVolume();

		r = BuildScript.Run( studio, "add Profile name=Arm along=Left front=0,1.2;8,0.8 sides=8 caps=quads position=0,4.3,10 branch=Torso" );
		var body = ModelMeasure.FindBody( studio, "Torso" );
		var arm = studio.Features.OfType<ProfileFeature>().Last();
		Report.Check( "the arm joins the torso: one body, no separate arm", r.Ok && body is not null && ModelMeasure.FindBody( studio, "Arm" ) is null
			&& studio.Bodies.Count == 1, r.ToString() );

		if ( body is null )
			return;

		var mesh = body.Mesh;
		var check = MeshValidator.Validate( mesh );
		Report.Check( "the joined body is closed and manifold", check.IsValid && check.IsClosed && check.NonManifoldEdges == 0, check.ToString() );
		Report.Check( "the joined body is all quads", mesh.Faces.All( f => f.Count == 4 ), $"{mesh.Faces.Count( f => f.Count != 4 )} non-quads" );
		Report.Check( "the joined body is one piece of genus 0 (Euler 2)", MeshValidator.EulerCharacteristic( mesh ) == 2 && MeshSplit.PieceCount( mesh ) == 1,
			$"Euler {MeshValidator.EulerCharacteristic( mesh )}" );
		Report.Check( "no loose vertices are left from the opened patch", AllUsed( mesh ) );
		Report.Check( "the joined body winds outward and holds more than the torso", mesh.SignedVolume() > torsoVolume, $"{mesh.SignedVolume():0.#} vs {torsoVolume:0.#}" );
		Report.Check( "the arm reaches out to its tip", mesh.Positions.Max( p => p.y ) > 12.2f, $"{mesh.Positions.Max( p => p.y ):0.##}" );
		Report.Check( "an 8-sided arm took a 2 × 2 patch (N/4 each way)", mesh.Faces.Count == 16 * 8 + 16 - 4 + 8 + 4 + 8,
			$"{mesh.Faces.Count} faces" );
		Report.Check( "a clean join raises no warning", arm.Warning is null, arm.Warning ?? "" );

		var smooth = CatmullClark.Subdivide( mesh, 2 );
		var smoothCheck = MeshValidator.Validate( smooth );
		Report.Check( "the joined body subdivides cleanly", smoothCheck.IsValid && smoothCheck.IsClosed && MeshValidator.EulerCharacteristic( smooth ) == 2, smoothCheck.ToString() );

		// Both arms from one line.
		studio = new PartStudio();
		r = BuildScript.Run( studio, torso + "\nadd Profile name=Arm along=Left front=0,1.2;8,0.8 sides=8 caps=quads mirror=1 position=0,4.3,12 branch=Torso" );
		body = ModelMeasure.FindBody( studio, "Torso" );
		check = body is null ? null : MeshValidator.Validate( body.Mesh );
		Report.Check( "mirror=1 joins both arms into the torso", r.Ok && studio.Bodies.Count == 1 && check is { IsValid: true, IsClosed: true }
			&& body.Mesh.Positions.Max( p => p.y ) > 12.2f && body.Mesh.Positions.Min( p => p.y ) < -12.2f
			&& MeshValidator.EulerCharacteristic( body.Mesh ) == 2, r.ToString() );

		// A 16-sided neck takes a 4 × 4 patch; ups and downs: a leg down from the hip.
		studio = new PartStudio();
		r = BuildScript.Run( studio, torso + "\nadd Profile name=Leg along=0,0,-1 front=0,1.5;12,1 sides=8 caps=quads position=2,0,-0.3 branch=Torso" );
		body = ModelMeasure.FindBody( studio, "Torso" );
		Report.Check( "a limb joins through the quad cap too", r.Ok && body is not null && MeshValidator.Validate( body.Mesh ) is { IsValid: true, IsClosed: true }
			&& body.Mesh.Positions.Min( p => p.z ) < -12f, r.ToString() );

		var parent = ProfileBody.Build( Ring( 6, 13 ), null, new ProfileShape { Sides = 32, QuadCaps = true } ).Mesh;
		var neck = ProfileBody.Build( new List<Vec2> { new( 0, 2 ), new( 5, 1.5f ) }, null, new ProfileShape { Sides = 16, QuadCaps = true, Axis = 0 } );
		MeshTransform.Apply( neck.Mesh, Xform.Translate( new Vec3( 6.3f, 0, 15 ) ) );
		var joined = MeshBranch.Join( parent, neck.Mesh, neck.NearCap );
		Report.Check( "a 16-sided limb takes a 4 × 4 patch", joined.Mesh is not null && joined.PatchA == 4 && joined.PatchB == 4, joined.Problem ?? $"{joined.PatchA} × {joined.PatchB}" );
		Report.Check( "the 16-sided join is closed, manifold and all quads", joined.Mesh is not null && MeshValidator.Validate( joined.Mesh ) is { IsValid: true, IsClosed: true }
			&& joined.Mesh.Faces.All( f => f.Count == 4 ) && MeshValidator.EulerCharacteristic( joined.Mesh ) == 2 );

		// Refusals name the cause.
		var missing = BuildScript.Run( new PartStudio(), torso + "\nadd Profile name=Arm along=Left front=0,1;8,1 sides=8 position=0,4.3,10 branch=Nope" );
		Report.Check( "branching from a body that is not there is refused", !missing.Ok && missing.Error.Contains( "Nope" ) && missing.Error.Contains( "Torso" ), missing.Error );

		var odd = BuildScript.Run( new PartStudio(), torso + "\nadd Profile name=Arm along=Left front=0,1;8,1 sides=7 position=0,4.3,10 branch=Torso" );
		Report.Check( "an odd-sided limb is refused with the reason", !odd.Ok && odd.Error.Contains( "even" ), odd.Error );

		var inside = new PartStudio();
		r = BuildScript.Run( inside, torso + "\nadd Profile name=Arm along=Left front=0,1;8,1 sides=8 caps=quads position=0,2,10 branch=Torso" );
		var insideFeature = inside.Features.OfType<ProfileFeature>().Last();
		Report.Check( "a limb that starts inside the body warns", insideFeature.Warning is not null && insideFeature.Warning.Contains( "inside" ), insideFeature.Warning ?? r.ToString() );
	}

	// --- helpers -----------------------------------------------------------------------------------

	static List<Vec2> Ring( float halfWidth, int stations ) =>
		Enumerable.Range( 0, stations ).Select( i => new Vec2( i * 2.5f, halfWidth ) ).ToList();

	static Vec3 RingCentre( PolyMesh mesh, int ring, int sides )
	{
		var c = Vec3.Zero;
		for ( var k = 0; k < sides; k++ )
			c += mesh.Positions[ring * sides + k];
		return c / sides;
	}

	static List<Vec3> TopRing( ProfileBuild r ) =>
		Enumerable.Range( 0, r.Sides ).Select( k => r.Mesh.Positions[(r.Rings - 1) * r.Sides + k] ).ToList();

	static float Radial( Vec3 p ) => MathF.Sqrt( p.x * p.x + p.y * p.y );

	/// <summary>The bounds of the actual limit surface: four levels of subdivision, every vertex then
	/// pushed onto its limit point.</summary>
	static (Vec3 Lo, Vec3 Hi) LimitBounds( PolyMesh cage )
	{
		var dense = CatmullClark.Subdivide( cage, 4 );
		var limit = SubdivisionLimit.LimitPositions( dense );
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in limit )
		{
			lo = new Vec3( MathF.Min( lo.x, p.x ), MathF.Min( lo.y, p.y ), MathF.Min( lo.z, p.z ) );
			hi = new Vec3( MathF.Max( hi.x, p.x ), MathF.Max( hi.y, p.y ), MathF.Max( hi.z, p.z ) );
		}

		return (lo, hi);
	}

	static bool AllUsed( PolyMesh mesh )
	{
		var used = new bool[mesh.VertexCount];
		foreach ( var f in mesh.Faces )
			foreach ( var i in f.Indices )
				used[i] = true;
		return used.All( u => u );
	}
}
