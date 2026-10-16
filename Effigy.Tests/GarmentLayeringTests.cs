using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Garments worn over garments: a jacket fitted over a shirt stays over it, a jacket cut over a
/// tee keeps its faces, the garment-vs-garment check finds what goes under, padding does what its
/// sliders say, and the .clothing claims a jacket's slots as over-slots.
/// </summary>
public static class GarmentLayeringTests
{
	public static void Run()
	{
		Report.Section( "layering: a later garment collides with the earlier one's outside" );
		TestCollidesWithOuterSheet();

		Report.Section( "layering: a garment is only cut where the wearer is buried, not under other garments" );
		TestInsideOtherWearersOnly();

		Report.Section( "layering: garment-vs-garment check" );
		TestLayerCheck();

		Report.Section( "layering: jacket over a loose shirt on a body, at bind and posed" );
		TestJacketOverShirt();

		Report.Section( "padding: quilt, inside vs outside, feather, knee, allowance, live-cloth data" );
		TestPadding();

		Report.Section( "layering: a jacket's .clothing slots go over, a shirt's under" );
		TestClothingSlotsOver();
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );

	static PolyMesh Tube( float radius, float z0, float z1, int rings = 6, int segments = 24 )
	{
		var mesh = new PolyMesh();

		for ( var r = 0; r <= rings; r++ )
			for ( var s = 0; s < segments; s++ )
			{
				var a = s / (float)segments * MathF.PI * 2f;
				mesh.AddVertex( new Vec3( MathF.Cos( a ) * radius, MathF.Sin( a ) * radius, z0 + (z1 - z0) * r / rings ) );
			}

		// Counter-clockwise seen from outside, so the normals face out.
		for ( var r = 0; r < rings; r++ )
			for ( var s = 0; s < segments; s++ )
			{
				var n = (s + 1) % segments;
				mesh.AddFace( new[] { r * segments + s, r * segments + n, (r + 1) * segments + n, (r + 1) * segments + s } );
			}

		return mesh;
	}

	/// <summary>
	/// The mechanism behind "the jacket stays under the shirt": a vertex between the body and a
	/// thickened shirt's lining. Against the whole solid the nearest wall is the lining, whose normal
	/// faces the body, so the vertex reads as outside and is left where it is. Against the shirt's
	/// outer wall it is inside and goes out over the shirt.
	/// </summary>
	static void TestCollidesWithOuterSheet()
	{
		var shirt = MeshSolidify.Solidify( Tube( 6f, -6f, 6f ), 0.15f );
		var solidTree = new List<MeshBVH> { MeshBVH.Build( shirt ) };
		var outer = MeshSolidify.OuterSheet( shirt, out _ );

		Check( "the shirt's outer wall is found", outer is not null );

		var jacket = new PolyMesh();
		jacket.AddVertex( new Vec3( 5.5f, 0f, 0f ) );

		var againstSolid = jacket.Clone();
		GarmentFit.PushOut( againstSolid, new[] { shirt }, solidTree, 0.15f, 3f );
		Check( "against the two-wall solid the vertex stays under the shirt (the old fault)", againstSolid.Positions[0].x < 6f,
			$"x {againstSolid.Positions[0].x:0.###}" );

		var againstSheet = jacket.Clone();
		GarmentFit.PushOut( againstSheet, new[] { outer }, new List<MeshBVH> { MeshBVH.Build( outer ) }, 0.15f, 3f );
		var off = MeshBVH.Build( outer ).NearestSurface( outer, againstSheet.Positions[0], 5f );
		Check( "against the outer wall it goes out over the shirt, by the clearance",
			off is { } h && Vec3.Dot( againstSheet.Positions[0] - h.Point, h.Normal ) >= 0.15f - 1e-3f, $"x {againstSheet.Positions[0].x:0.###}" );

		Check( "the layer check agrees: the old place is under, the new one over",
			GarmentLayerCheck.Pair( againstSolid, shirt ).Through == 1 && GarmentLayerCheck.Pair( againstSheet, shirt ).Through == 0 );
	}

	/// <summary>A box wearer with a spine, the fixture the garment tests use.</summary>
	static (PolyMesh Body, Skeleton Rig) BoxWearer()
	{
		var body = Primitives.Box( 12f, 8f, 16f );
		var rig = new Skeleton();
		var root = rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -7 ), new Vec3( 0, 0, -5 ) );
		var pelvis = rig.AddBoneFromPoints( "pelvis", root, new Vec3( 0, 0, -5 ), new Vec3( 0, 0, -3 ) );
		rig.AddBoneFromPoints( "spine", pelvis, new Vec3( 0, 0, -3 ), new Vec3( 0, 0, 7 ) );
		return (body, rig);
	}

	/// <summary>
	/// A garment worn already is OVER the skin, so asking "is this skin face buried in another
	/// body" of it dropped the faces it covers. Here the earlier garment is a closed shell round the
	/// whole wearer: asked of it, every face of the wearer went, and the jacket covered nothing.
	/// </summary>
	static void TestInsideOtherWearersOnly()
	{
		var (body, rig) = BoxWearer();
		var map = new BodyRegions.Map( rig );
		var recipe = GarmentRecipe.Build( Array.IndexOf( GarmentRecipe.Names, "T-shirt" ), 1f, 1f, 0.5f, 0 );
		var options = new GarmentFit.Options { Drape = false, Thickness = 0f };

		var alone = GarmentFit.Fit( new[] { body }, map, recipe, options );
		Check( "a T-shirt on the box covers it", alone is { SourceFaces: > 0 }, $"{alone?.SourceFaces}" );

		// A closed garment-shaped shell round the whole box, normals out.
		var shell = Primitives.Box( 14f, 10f, 18f );
		var bodies = new List<PolyMesh> { body, shell };
		var trees = bodies.Select( MeshBVH.Build ).ToList();
		var closed = bodies.Select( b => MeshValidator.Validate( b ).IsClosed ).ToList();

		Check( "the shell is closed, so the test asks it", closed[1] );
		Check( "a skin point is inside the shell (what the old loop asked)", GarmentFit.InsideOther( bodies, trees, closed, 0, new Vec3( 0, 0, 0 ) + new Vec3( 6f, 0, 0 ), 2 ) );
		Check( "and is not buried in another wearer (what is asked now)", !GarmentFit.InsideOther( bodies, trees, closed, 0, new Vec3( 6f, 0, 0 ), 1 ) );

		var over = GarmentFit.Fit( new[] { body }, map, recipe, options, new[] { shell } );
		Check( "a T-shirt cut over the shell keeps every face it has alone", over is not null && over.SourceFaces == alone?.SourceFaces && over.DroppedInside == alone.DroppedInside,
			over is null ? "covered nothing" : $"{over.SourceFaces} faces, {over.DroppedInside} dropped, alone {alone?.SourceFaces}/{alone?.DroppedInside}" );
	}

	static void TestLayerCheck()
	{
		var shirt = MeshSolidify.Solidify( Tube( 5f, 0f, 12f ), 0.2f );
		var wall = MeshSolidify.OuterSheet( shirt, out _ );
		var wallRadius = wall.Positions.Average( p => MathF.Sqrt( p.x * p.x + p.y * p.y ) );
		Check( "the shirt's outside is out from its sheet", wallRadius > 5.1f, $"{wallRadius:0.###}" );

		var over = MeshSolidify.Solidify( Tube( wallRadius + 0.2f, 1f, 11f ), 0.2f );
		var r = GarmentLayerCheck.Pair( over, shirt );
		Check( "a jacket clear of the shirt does not go under it", r.Through == 0 && r.Tested > 0, $"{r.Through}/{r.Tested}" );
		Check( "only the jacket's inside wall is measured", r.OuterVertices * 2 == over.VertexCount, $"{r.OuterVertices} of {over.VertexCount}" );

		var under = Tube( wallRadius - 0.5f, 1f, 11f );
		var u = GarmentLayerCheck.Pair( under, shirt );
		Check( "a jacket half an inch inside the shirt's outside goes under at every vertex", u.Through == under.VertexCount, $"{u.Through}/{under.VertexCount}" );
		Check( "and says how deep, in inches", MathF.Abs( u.Deepest - 0.5f ) < 0.08f, $"{u.Deepest:0.###}" );

		// Tolerance: a graze of a twentieth of an inch is the fit resting on the shirt.
		var graze = Tube( wallRadius - 0.05f, 1f, 11f );
		Check( "a graze inside the settled tolerance is not through", GarmentLayerCheck.Pair( graze, shirt ).Through == 0 );
		Check( "but is under a stricter one", GarmentLayerCheck.Pair( graze, shirt, 0.01f ).Through > 0 );

		// Past the shirt's open end the nearest point is on its edge: not measured.
		var past = Tube( wallRadius - 1f, 14f, 18f );
		var pr = GarmentLayerCheck.Pair( past, shirt );
		Check( "a sleeve past the shirt's cuff is not counted as under it", pr.Through == 0, $"{pr.Through}/{pr.Tested}" );

		// Pairs: a lower layer under a higher one only.
		var garments = new List<(string, PolyMesh, int)> { ("shirt", shirt, 1), ("jacket", under, 2), ("tee", Tube( wallRadius - 0.5f, 1f, 11f ), 1), ("frill", under, -1) };
		var all = GarmentLayerCheck.Run( garments );
		Check( "only the jacket over the shirt and the tee is measured", all.Count == 2 && all.All( c => c.Outer == "jacket" ),
			string.Join( "; ", all.Select( c => $"{c.Outer} over {c.Inner}" ) ) );
		Check( "and the report says which and what to do", all.Any( c => c.Inner == "shirt" && c.ToString().Contains( "under shirt" ) && c.ToString().Contains( "Clearance" ) ),
			all.FirstOrDefault()?.ToString() );
	}

	/// <summary>The human the Clothing workspace dresses when there is no s&amp;box install: the
	/// body grown from Citizen's bones (SkinBlockout), through OBJ the way a compiled model arrives.</summary>
	static PartStudio Dressed( params Action<PartStudio>[] garments )
	{
		var rig = CitizenSkeleton.Build();
		var skin = SkinBlockout.Build( rig );
		var studio = new PartStudio { Rig = rig.Clone() };
		var wearer = studio.Add( new WearerFeature { Name = "human" } );
		wearer.LoadMesh( System.Text.Encoding.UTF8.GetBytes( ObjWriter.Write( skin, "human" ) ) );

		foreach ( var add in garments )
			add( studio );

		return studio;
	}

	static GarmentFeature Garment( PartStudio studio, string name, string recipe, int fit, int layer )
	{
		var g = studio.Add( new GarmentFeature { Name = name } );
		g.Recipe.Index = Array.IndexOf( GarmentRecipe.Names, recipe );
		var (length, sleeve) = GarmentRecipe.Defaults( g.Recipe.Index );
		g.Length.Value = length;
		g.Sleeve.Value = sleeve;
		g.Fit.Index = fit;
		g.Layer.Index = layer;
		return g;
	}

	static void TestJacketOverShirt()
	{
		var clock = Stopwatch.StartNew();
		var studio = Dressed(
			s => Garment( s, "shirt", "Long sleeve", 2, 2 ),   // Loose, Shirt layer
			s => Garment( s, "jacket", "Long sleeve", 1, 3 ) ); // Regular, Jacket layer

		var rebuild = studio.Rebuild();
		Check( "a loose shirt and a jacket over it build", !rebuild.HasErrors, string.Join( "; ", rebuild.Errors.Select( e => e.Message ) ) );

		var shirt = studio.Bodies.FirstOrDefault( b => b.Name == "shirt" );
		var jacket = studio.Bodies.FirstOrDefault( b => b.Name == "jacket" );

		if ( shirt is null || jacket is null )
		{
			Check( "both garments came out", false );
			return;
		}

		Check( "the layers are recorded", shirt.Layer == 1 && jacket.Layer == 2, $"{shirt.Layer}, {jacket.Layer}" );

		var settled = GarmentLayerCheck.Pair( jacket.Mesh, shirt.Mesh, GarmentLayerCheck.SettledTolerance );
		Console.WriteLine( $"       settled: {settled.Through}/{settled.OuterVertices} under, deepest {settled.Deepest:0.##} in, {settled.Tested} measured ({clock.ElapsedMilliseconds} ms)" );
		// One vertex in 1750 at the armpit-side torso (0.24 in) is still under on the blockout body;
		// the fit's collision is per vertex, not per triangle. Held to half a percent.
		Check( "at bind, under 0.5% of the jacket is more than 0.1 in under the shirt", settled.Share < 0.005f,
			$"{settled.Through}/{settled.OuterVertices}, deepest {settled.Deepest:0.##} in" );
		Check( "and the jacket was measured against it at all", settled.Tested > settled.OuterVertices / 4, $"{settled.Tested}/{settled.OuterVertices}" );

		// Posed: both garments deformed with the rig, the way Test does it.
		var bind = studio.Rig.Clone();
		var bindWorld = Enumerable.Range( 0, bind.Count ).Select( bind.WorldBind ).ToArray();

		foreach ( var poseName in new[] { "Arms out", "Arms up", "Reach" } )
		{
			var pose = GarmentPoses.All.FirstOrDefault( p => p.Name == poseName );

			if ( pose is null )
			{
				Check( $"there is a {poseName} pose", false );
				continue;
			}

			var rig = bind.Clone();
			GarmentPoses.Apply( rig, pose );
			var posed = Enumerable.Range( 0, rig.Count ).Select( rig.WorldBind ).ToArray();

			PolyMesh Deform( PolyMesh m )
			{
				var copy = m.Clone();
				var moved = SkinBinder.Deform( m.Positions, m.Skin, bindWorld, posed );
				for ( var i = 0; i < moved.Length; i++ )
					copy.Positions[i] = moved[i];
				return copy;
			}

			if ( !shirt.Mesh.IsRigged || !jacket.Mesh.IsRigged )
			{
				Check( "both garments are weighted", false );
				break;
			}

			var p = GarmentLayerCheck.Pair( Deform( jacket.Mesh ), Deform( shirt.Mesh ), GarmentLayerCheck.PosedTolerance );
			Console.WriteLine( $"       {poseName}: {p.Through}/{p.OuterVertices} under by more than 0.25 in, deepest {p.Deepest:0.##} in" );
			Check( $"{poseName}: under 5% of the jacket goes more than 0.25 in under the shirt", p.Share < 0.05f,
				$"{p.Through}/{p.OuterVertices} ({p.Share:P1}), deepest {p.Deepest:0.##} in" );
		}
	}

	static void TestPadding()
	{
		var studio = Dressed( s => Garment( s, "jacket", "Long sleeve", 1, 3 ) );
		studio.Rebuild();
		var plain = studio.Bodies.First( b => b.Name == "jacket" ).Mesh.Clone();
		var plainAllowance = studio.Bodies.First( b => b.Name == "jacket" ).LayerAllowance;
		var map = new BodyRegions.Map( studio.Rig );
		var sheet = MeshSolidify.OuterSheet( plain, out var offset );
		Check( "the jacket is thickened, so it has an outer wall", sheet is not null );

		if ( sheet is null )
			return;

		PaddingFeature Pad( Action<PaddingFeature> set )
		{
			var pad = new PaddingFeature();
			set( pad );
			return pad;
		}

		// QUILT. Whole garment, diamonds two inches apart: full loft between the stitching, a third
		// of it on the lines, never less.
		var quilt = Pad( p => { p.Where.Index = Array.IndexOf( PaddingFeature.RegionNames, "Whole garment" ); p.Quilt.Value = 2f; p.Thickness.Value = 0.3f; } );
		var qf = quilt.Field( sheet, map, out var qw );
		Check( "quilted: the loft reaches full thickness between the lines", qf.Max() > 0.29f, $"{qf.Max():0.###}" );
		Check( "and pinches to about a third on them", qf.Min() >= 0.3f * 0.3f - 1e-4f && qf.Min() < 0.3f * 0.4f, $"{qf.Min():0.###}" );
		var unquilted = Pad( p => { p.Where.Index = quilt.Where.Index; p.Thickness.Value = 0.3f; } ).Field( sheet, map, out _ );
		Check( "unquilted, the same pad is even", unquilted.All( v => MathF.Abs( v - 0.3f ) < 1e-4f ) );
		var channels = Pad( p => { p.Where.Index = quilt.Where.Index; p.Quilt.Value = 2f; p.QuiltPattern.Index = 1; p.Thickness.Value = 0.3f; } ).Field( sheet, map, out _ );
		Check( "channels quilt differently from diamonds", channels.Zip( qf ).Count( t => MathF.Abs( t.First - t.Second ) > 0.02f ) > qf.Length / 10 );

		// FEATHER. The elbow with no feather is in or out; with two inches it fades.
		static int Partial( float[] w ) => w.Count( v => v > 0.02f && v < 0.98f );
		var elbow = Array.IndexOf( PaddingFeature.RegionNames, "Elbow" );
		Pad( p => { p.Where.Index = elbow; p.Feather.Value = 0f; } ).Field( sheet, map, out var hard );
		Pad( p => { p.Where.Index = elbow; p.Feather.Value = 2f; } ).Field( sheet, map, out var soft );
		Check( "elbow, no feather: every padded vertex is at full thickness", hard.Any( v => v > 0.98f ) && Partial( hard ) == 0, $"{Partial( hard )} partial" );
		Check( "two inches of feather fades it in", Partial( soft ) > 10, $"{Partial( soft )} partial" );

		// INSIDE VS OUTSIDE. A patch sewn on top has a crisp stitched edge; batting inside fades.
		var inside = Pad( p => { p.Where.Index = elbow; p.Feather.Value = 2f; p.Placement.Index = 0; } );
		var outside = Pad( p => { p.Where.Index = elbow; p.Feather.Value = 2f; p.Placement.Index = 1; } );
		inside.Field( sheet, map, out var wi );
		outside.Field( sheet, map, out var wo );
		Check( "outside, the feather is capped: a narrower fade than inside", Partial( wo ) < Partial( wi ) / 2, $"{Partial( wo )} vs {Partial( wi )}" );

		foreach ( var (label, feature) in new[] { ("inside", inside), ("outside", outside) } )
		{
			var s = Dressed( x => Garment( x, "jacket", "Long sleeve", 1, 3 ) );
			s.Add( feature );
			var rebuild = s.Rebuild();
			var mesh = s.Bodies.First( b => b.Name == "jacket" ).Mesh;
			var liningMoved = Enumerable.Range( 0, offset ).Count( i => (mesh.Positions[i] - plain.Positions[i]).Length > 1e-5f );
			var shellMoved = Enumerable.Range( offset, offset ).Count( i => (mesh.Positions[i] - plain.Positions[i]).Length > 1e-5f );

			Check( $"{label}: padding builds", !rebuild.HasErrors && feature.Error is null, feature.Error );
			Check( $"{label}: the lining against the body does not move", liningMoved == 0, $"{liningMoved} moved" );
			Check( $"{label}: the outside does", shellMoved > 0 && shellMoved == feature.PaddedVertices, $"{shellMoved}, feature says {feature.PaddedVertices}" );
		}

		// KNEE, on trousers: in front of the knee, near its height, on the side asked for.
		var legs = Dressed( x => Garment( x, "trousers", "Trousers", 1, 1 ) );
		var knee = legs.Add( Pad( p => { p.Where.Index = Array.IndexOf( PaddingFeature.RegionNames, "Knee" ); p.Side.Index = Array.IndexOf( GarmentRecipe.SideNames, "Left" ); } ) );
		legs.Rebuild();
		Check( "a knee pad builds on trousers", knee.Error is null && knee.PaddedVertices > 0, knee.Error ?? $"{knee.PaddedVertices}" );

		if ( knee.PaddedVertices > 0 && new BodyRegions.Map( legs.Rig ).Chain( BodyRegion.UpperLeg, 1, out _, out var kneeJoint ) )
		{
			var trousers = legs.Bodies.First( b => b.Name == "trousers" );
			var tsheet = MeshSolidify.OuterSheet( trousers.Mesh, out _ );
			var field = knee.Fields[trousers.Id];
			var padded = Enumerable.Range( 0, field.Thickness.Length ).Where( i => field.Thickness[i] > 1e-4f ).Select( i => tsheet.Positions[i] ).ToList();

			Check( "the knee pad is on the left leg only", padded.All( p => p.y > 0f ), $"{padded.Count( p => p.y <= 0f )} on the right" );
			Check( "in front of the knee", padded.All( p => p.x > kneeJoint.x - 0.5f ), $"{padded.Min( p => p.x ):0.##} vs knee {kneeJoint.x:0.##}" );
			Check( "within three inches of its height", padded.All( p => MathF.Abs( p.z - kneeJoint.z ) < 3.5f ), $"{padded.Min( p => p.z ):0.##}..{padded.Max( p => p.z ):0.##}, knee {kneeJoint.z:0.##}" );
		}

		// ALLOWANCE: padding the whole garment makes room for what goes over it; an elbow patch does not.
		var puffer = Dressed( x => Garment( x, "jacket", "Long sleeve", 1, 3 ) );
		var whole = puffer.Add( Pad( p => { p.Where.Index = quilt.Where.Index; p.Thickness.Value = 0.4f; } ) );
		puffer.Rebuild();
		var allowance = puffer.Bodies.First( b => b.Name == "jacket" ).LayerAllowance;
		Check( "padded all over, the garment leaves the pad's thickness more room over it", MathF.Abs( allowance - (plainAllowance + 0.4f) ) < 1e-3f, $"{plainAllowance:0.###} -> {allowance:0.###}" );

		var patched = Dressed( x => Garment( x, "jacket", "Long sleeve", 1, 3 ) );
		var patch = patched.Add( Pad( p => p.Where.Index = elbow ) );
		patched.Rebuild();
		Check( "an elbow patch leaves the allowance alone", MathF.Abs( patched.Bodies.First( b => b.Name == "jacket" ).LayerAllowance - plainAllowance ) < 1e-5f );

		// LIVE CLOTH DATA: per particle, the way Make live cloth numbers them.
		var body = patched.Bodies.First( b => b.Name == "jacket" );
		var particles = MeshSolidify.OuterSheet( body.Mesh, out _ ).VertexCount;
		var pp = PaddingParticles.For( body.Id, particles, new[] { patch } );
		var pf = patch.Fields[body.Id];
		var full = Enumerable.Range( 0, particles ).FirstOrDefault( i => pf.Weight[i] > 0.99f, -1 );
		var bare = Enumerable.Range( 0, particles ).FirstOrDefault( i => pf.Weight[i] <= 0f, -1 );

		Check( "one value per particle", pp.Thickness.Length == particles && pp.BendScale.Length == particles && pp.Mass.Length == particles );
		Check( "a fully padded particle is the pad's thickness, Stiffer and Heavier", full >= 0
			&& MathF.Abs( pp.Thickness[full] - patch.Thickness.Clamped * pf.Weight[full] ) < 1e-4f
			&& MathF.Abs( pp.BendScale[full] - (1f + (patch.Stiffer.Clamped - 1f) * pf.Weight[full]) ) < 1e-3f
			&& MathF.Abs( pp.Mass[full] - (1f + (patch.Heavier.Clamped - 1f) * pf.Weight[full]) ) < 1e-3f,
			full < 0 ? "none full" : $"{pp.Thickness[full]:0.###} in, bend {pp.BendScale[full]:0.##}, mass {pp.Mass[full]:0.##}" );
		Check( "an unpadded one is the plain cloth", bare >= 0 && pp.Thickness[bare] == 0f && pp.BendScale[bare] == 1f && pp.Mass[bare] == 1f );

		var twice = PaddingParticles.For( body.Id, particles, new[] { patch, patch, patch } );
		Check( "overlapping pads stay under the sliders' tops", twice.Mass.Max() <= PaddingParticles.MaxMass && twice.BendScale.Max() <= PaddingParticles.MaxBend,
			$"mass {twice.Mass.Max():0.##}, bend {twice.BendScale.Max():0.##}" );
		Check( "another garment's padding is not this one's", !PaddingParticles.For( "nobody", particles, new[] { patch } ).Any );
		Check( "nor is a field for a garment rebuilt to a different size", !PaddingParticles.For( body.Id, particles + 1, new[] { patch } ).Any );
	}

	static void TestClothingSlotsOver()
	{
		var shirt = GarmentRecipe.Build( Array.IndexOf( GarmentRecipe.Names, "Long sleeve" ), 1f, 1f, 0.5f, 0 );
		var jacket = GarmentRecipe.Build( Array.IndexOf( GarmentRecipe.Names, "Long sleeve" ), 1f, 1f, 0.5f, 0 );

		string Field( string json, string key )
		{
			using var doc = System.Text.Json.JsonDocument.Parse( json );
			return doc.RootElement.GetProperty( key ).ToString();
		}

		var asShirt = ClothingDefinition.Write( new[] { (shirt, 1) }, "models/effigy/shirt.vmdl", "Shirt" );
		Check( "a shirt claims its slots under", Field( asShirt, "SlotsUnder" ).Contains( "LeftArm" ) && Field( asShirt, "SlotsOver" ) == "0", asShirt );

		var asJacket = ClothingDefinition.Write( new[] { (jacket, 3) }, "models/effigy/jacket.vmdl", "Jacket" );
		Check( "a jacket claims them over, so it goes on without taking the shirt off",
			Field( asJacket, "SlotsOver" ).Contains( "LeftArm" ) && Field( asJacket, "SlotsOver" ).Contains( "Chest" ) && Field( asJacket, "SlotsUnder" ) == "0", asJacket );

		var outfit = ClothingDefinition.Write( new[] { (shirt, 1), (jacket, 2) }, "models/effigy/outfit.vmdl", "Outfit" );
		Check( "an outfit of both claims each set where it belongs",
			Field( outfit, "SlotsUnder" ).Contains( "Chest" ) && Field( outfit, "SlotsOver" ).Contains( "RightArm" ), outfit );
		Check( "and still hides the chest", Field( outfit, "HideBody" ).Contains( "Chest" ), outfit );
	}
}
