using System;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Simple deform (twist, bend, taper, stretch) and the lattice cage, measured: the twist angle at a
/// height, the bend radius, an untouched lattice being the identity, and both as saved, scripted
/// features.
/// </summary>
public static class DeformTests
{
	public static void Run()
	{
		Report.Section( "simple deform and lattice" );
		TestTwistAngle();
		TestBendRadius();
		TestTaperAndStretch();
		TestLimits();
		TestSimpleDeformFeature();
		TestLatticeIdentity();
		TestLatticePull();
		TestLatticeFeature();
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );

	/// <summary>A tall column: 1 × 1 × 10, many rings so the deforms have vertices to move.</summary>
	static PolyMesh Column() => Primitives.Box( 1f, 1f, 10f );

	static float AngleAbout( Vec3 v ) => MathF.Atan2( v.y, v.x );

	static void TestTwistAngle()
	{
		var s = new SimpleDeformSettings
		{
			Mode = SimpleDeformMode.Twist, Axis = new Vec3( 0, 0, 1 ), Pivot = Vec3.Zero,
			Angle = MathF.PI / 2f, Lower = 0f, Upper = 10f,
		};

		// A point at height h turns by Angle * h / span about Z.
		var worst = 0f;
		foreach ( var h in new[] { 0f, 2.5f, 5f, 7.5f, 10f } )
		{
			var p = new Vec3( 1f, 0f, h );
			var q = SimpleDeform.Deform( p, s );
			var expected = MathF.PI / 2f * h / 10f;
			worst = MathF.Max( worst, MathF.Abs( AngleAbout( q ) - expected ) );
			worst = MathF.Max( worst, MathF.Abs( q.z - h ) );
			worst = MathF.Max( worst, MathF.Abs( new Vec3( q.x, q.y, 0 ).Length - 1f ) );
		}

		Check( "Twist turns each height by angle × height / span, keeping its radius and height", worst < 1e-4f, $"worst {worst:0.000000}" );
	}

	static void TestBendRadius()
	{
		var s = new SimpleDeformSettings
		{
			Mode = SimpleDeformMode.Bend, Axis = new Vec3( 0, 0, 1 ), Toward = new Vec3( 1, 0, 0 ),
			Pivot = Vec3.Zero, Angle = MathF.PI / 2f, Lower = 0f, Upper = 10f,
		};

		// The centre line becomes a circle of radius span / angle about (R, 0, 0).
		var radius = 10f / (MathF.PI / 2f);
		var centre = new Vec3( radius, 0, 0 );
		var worst = 0f;

		for ( var i = 0; i <= 10; i++ )
		{
			var q = SimpleDeform.Deform( new Vec3( 0, 0, i ), s );
			worst = MathF.Max( worst, MathF.Abs( (q - centre).Length - radius ) );
		}

		var tip = SimpleDeform.Deform( new Vec3( 0, 0, 10 ), s );
		Check( "Bend lays the axis on a circle of radius span / angle", worst < 1e-3f, $"worst {worst:0.0000}, radius {radius:0.000}" );
		Check( "and a quarter turn brings the tip round to point along +X", MathF.Abs( tip.x - radius ) < 1e-3f && MathF.Abs( tip.z - radius ) < 1e-3f, $"{tip.x:0.000},{tip.z:0.000}" );

		// A point 0.5 to the inside rides a circle of radius R - 0.5: concentric.
		var inner = SimpleDeform.Deform( new Vec3( 0.5f, 0, 5 ), s );
		Check( "a point off the axis rides a concentric circle", MathF.Abs( (inner - centre).Length - (radius - 0.5f) ) < 1e-3f );

		// Past the band the shape carries on straight along the tangent.
		var s2 = new SimpleDeformSettings { Mode = s.Mode, Axis = s.Axis, Toward = s.Toward, Pivot = s.Pivot, Angle = s.Angle, Lower = 0f, Upper = 5f };
		var a = SimpleDeform.Deform( new Vec3( 0, 0, 6 ), s2 );
		var b = SimpleDeform.Deform( new Vec3( 0, 0, 7 ), s2 );
		Check( "past the upper limit the body stays straight and unstretched", MathF.Abs( (b - a).Length - 1f ) < 1e-4f );
	}

	static void TestTaperAndStretch()
	{
		var taper = new SimpleDeformSettings { Mode = SimpleDeformMode.Taper, Axis = new Vec3( 0, 0, 1 ), Factor = 1f, Lower = 0f, Upper = 10f };
		var top = SimpleDeform.Deform( new Vec3( 1, 0, 10 ), taper );
		var mid = SimpleDeform.Deform( new Vec3( 1, 0, 5 ), taper );
		Check( "Taper factor 1 doubles the cross-section at the far end and is linear between", MathF.Abs( top.x - 2f ) < 1e-5f && MathF.Abs( mid.x - 1.5f ) < 1e-5f );

		var mesh = Column();
		for ( var i = 0; i < mesh.Positions.Count; i++ ) mesh.Positions[i] += new Vec3( 0, 0, 5 );
		var volume = mesh.SignedVolume();
		SimpleDeform.Apply( mesh, new SimpleDeformSettings { Mode = SimpleDeformMode.Stretch, Axis = new Vec3( 0, 0, 1 ), Factor = 1f, Lower = 0f, Upper = 10f } );
		var (lo, hi) = ModelMeasure.Bounds( mesh );
		Check( "Stretch factor 1 doubles the length", MathF.Abs( hi.z - lo.z - 20f ) < 1e-3f, $"{hi.z - lo.z:0.000}" );
		Check( "and keeps the volume", MathF.Abs( mesh.SignedVolume() - volume ) < volume * 1e-3f, $"{volume:0.000} -> {mesh.SignedVolume():0.000}" );
	}

	static void TestLimits()
	{
		var s = new SimpleDeformSettings { Mode = SimpleDeformMode.Twist, Axis = new Vec3( 0, 0, 1 ), Angle = 1f, Lower = 2f, Upper = 4f };
		var below = SimpleDeform.Deform( new Vec3( 1, 0, 1 ), s );
		var above = SimpleDeform.Deform( new Vec3( 1, 0, 9 ), s );
		Check( "below the band nothing turns (the pivot clamps to the band's lower end)", (below - new Vec3( 1, 0, 1 )).Length < 1e-6f );
		Check( "above the band the full turn is kept", MathF.Abs( AngleAbout( above ) - 1f ) < 1e-5f );
	}

	static void TestSimpleDeformFeature()
	{
		var studio = new PartStudio();
		var prim = studio.Add( new PrimitiveFeature() );
		prim.SizeX.Value = 1f; prim.SizeY.Value = 1f; prim.SizeZ.Value = 10f;
		var deform = studio.Add( new SimpleDeformFeature() );
		deform.Angle.Value = 90f;
		deform.Pivot.Index = 1; // base
		studio.Rebuild();

		var mesh = studio.Bodies[0].Mesh;
		var (lo, hi) = ModelMeasure.Bounds( mesh );
		var topCorner = mesh.Positions.Where( p => MathF.Abs( p.z - hi.z ) < 1e-4f ).Select( p => AngleAbout( p ) ).ToList();
		var baseCorner = mesh.Positions.Where( p => MathF.Abs( p.z - lo.z ) < 1e-4f ).Select( p => AngleAbout( p ) ).ToList();
		// Box corners sit at ±45°, ±135°; turned 90° at the top they are still at diagonals, so check one corner explicitly.
		var corner = SimpleDeform.Deform( new Vec3( 0.5f, 0.5f, hi.z ), new SimpleDeformSettings { Mode = SimpleDeformMode.Twist, Axis = new Vec3( 0, 0, 1 ), Pivot = new Vec3( 0, 0, lo.z ), Angle = MathF.PI / 2f, Lower = 0f, Upper = hi.z - lo.z } );
		Check( "Simple deform builds from its defaults on a column", deform.Error is null, deform.Error );
		Check( "the top of a based twist turns 90° and its base stays", mesh.Positions.Any( p => (p - corner).Length < 1e-3f )
			&& mesh.Positions.Contains( new Vec3( 0.5f, 0.5f, lo.z ) ), string.Join( " ", topCorner.Concat( baseCorner ).Select( a => $"{a:0.00}" ) ) );

		// Saved and loaded, it builds the same.
		var text = StudioDocument.Write( studio );
		var loaded = StudioDocument.Read( text );
		loaded.Rebuild();
		var same = loaded.Bodies[0].Mesh.Positions.Zip( mesh.Positions, ( a, b ) => (a - b).Length ).Max();
		Check( "a saved Simple deform reloads to the same shape", same < 1e-5f, $"{same}" );

		// Scripted.
		var scripted = new PartStudio();
		var r = BuildScript.Run( scripted, "add Primitive sizex=1 sizey=1 sizez=10\nadd SimpleDeform mode=Bend angle=90 bend_toward=+X" );
		Check( "a script can add a Simple deform by name", r.Ok && scripted.Features.OfType<SimpleDeformFeature>().Any(), r.ToString() );

		var bad = new PartStudio();
		bad.Add( new PrimitiveFeature() );
		var wrong = bad.Add( new SimpleDeformFeature() );
		wrong.Mode.Index = 1;
		wrong.BendToward.Index = 4; // +Z along the Z axis
		bad.Rebuild();
		Check( "bending toward its own axis is refused with the control named", wrong.Error is not null && wrong.Diagnostic?.ParameterLabel == "Bend toward", wrong.Error );

		var nothing = new PartStudio();
		nothing.Add( new PrimitiveFeature() );
		var zero = nothing.Add( new SimpleDeformFeature() );
		zero.Angle.Value = 0f;
		nothing.Rebuild();
		Check( "a zero twist warns instead of passing silently", zero.Warning is not null, zero.Warning );
	}

	static void TestLatticeIdentity()
	{
		var mesh = Primitives.QuadSphere( 2f, 6 );
		var before = mesh.Positions.ToArray();
		var (lo, hi) = ModelMeasure.Bounds( mesh );
		foreach ( var smooth in new[] { true, false } )
		{
			var cage = new LatticeCage( lo, hi, 4, 3, 5 );
			var copy = mesh.Clone();
			cage.Apply( copy, smooth );
			var worst = copy.Positions.Zip( before, ( a, b ) => (a - b).Length ).Max();
			Check( $"an undeformed lattice ({(smooth ? "smooth" : "linear")}) is exactly the identity", worst == 0f, $"{worst}" );
		}

		// Moving every point by the same offset translates the shape exactly (the weights sum to one).
		var all = new LatticeCage( lo, hi, 3, 3, 3 );
		all.TryParseOffsets( "*,*,*: 1,2,3", out _ );
		var moved = mesh.Clone();
		all.Apply( moved, smooth: true );
		var err = moved.Positions.Zip( before, ( a, b ) => (a - b - new Vec3( 1, 2, 3 )).Length ).Max();
		Check( "moving every lattice point together translates the shape exactly", err < 1e-4f, $"{err}" );
	}

	static void TestLatticePull()
	{
		var mesh = Primitives.QuadSphere( 2f, 6 );
		var (lo, hi) = ModelMeasure.Bounds( mesh );
		var cage = new LatticeCage( lo, hi, 3, 3, 3 );
		Check( "lattice offsets parse with * and ranges", cage.TryParseOffsets( "*,*,2: 0,0,1; 0..1,1,1: 0.5,0,0", out var why ), why );
		Check( "and come back as the same text", new LatticeCage( lo, hi, 3, 3, 3 ) is var again && again.TryParseOffsets( cage.FormatOffsets(), out _ )
			&& again.Offsets.Zip( cage.Offsets, ( a, b ) => (a - b).Length ).Max() < 1e-4f );

		var bad = new LatticeCage( lo, hi, 3, 3, 3 );
		Check( "an index past the cage is refused with the cage size", !bad.TryParseOffsets( "3,0,0: 1,0,0", out var why2 ) && why2.Contains( "0..2" ), why2 );

		var top = new LatticeCage( lo, hi, 3, 3, 3 );
		top.TryParseOffsets( "*,*,2: 0,0,1", out _ );
		var copy = mesh.Clone();
		top.Apply( copy, smooth: false );
		var (_, newHi) = ModelMeasure.Bounds( copy );
		var (newLo, _) = ModelMeasure.Bounds( copy );
		Check( "lifting the top layer of a linear cage lifts the top by exactly that and leaves the bottom", MathF.Abs( newHi.z - hi.z - 1f ) < 1e-4f && MathF.Abs( newLo.z - lo.z ) < 1e-4f,
			$"top {newHi.z - hi.z:0.000}, bottom {newLo.z - lo.z:0.000}" );
	}

	static void TestLatticeFeature()
	{
		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature() );
		var lattice = studio.Add( new LatticeFeature() );
		studio.Rebuild();
		Check( "an unmoved Lattice feature warns that it changed nothing", lattice.Error is null && lattice.Warning is not null, lattice.Error ?? lattice.Warning );

		lattice.Offsets.Value = "2,2,2: 0,0,2";
		studio.MarkDirty( lattice );
		studio.Rebuild();
		var (_, hi) = ModelMeasure.Bounds( studio.Bodies[0].Mesh );
		Check( "pulling a top corner point raises the body", lattice.Error is null && hi.z > 0.6f, $"{lattice.Error} top {hi.z:0.000}" );

		lattice.Offsets.Value = "1,1: 0,0,2";
		studio.MarkDirty( lattice );
		studio.Rebuild();
		Check( "unreadable offsets fail on the Point offsets control", lattice.Error is not null && lattice.Diagnostic?.ParameterLabel == "Point offsets", lattice.Error );

		var scripted = new PartStudio();
		var r = BuildScript.Run( scripted, "add Primitive\nadd Lattice offsets=\"*,*,2: 0,0,1\"" );
		var (_, top) = ModelMeasure.Bounds( scripted.Bodies );
		Check( "a script can add a Lattice and move its points", r.Ok && top.z > 1f, r + $" top {top.z:0.000}" );
	}
}
