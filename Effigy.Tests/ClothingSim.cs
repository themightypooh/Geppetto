using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy.Render;

namespace Effigy.Tests;

/// <summary>
/// The Clothing workspace, run without the editor: every stage of the bar in the order it is laid
/// out (Wearer, Garment, Shape, Material, Test, Check, Publish), against the kernel the buttons call,
/// and then the live-cloth file stepped by a port of GarmentCloth's solver on a moving rig.
///
/// THE EDITOR GLUE IS MIRRORED HERE, NOT CALLED. EffigyWindow.Clothing.cs is editor code and the
/// suite never compiles it, so the few dozen lines each button adds on top of the kernel (the weld
/// in WearerSurface, the deform in DeformForTest, the loop in PushOutClipping, the assembly in
/// PublishLiveCloth) are restated below. Keep them in step with the window when it changes.
///
///   effigy clothing outdir [recipe] [quick]
///
/// Exits 0 when every check passes, 3 when any fails. The report is printed and also written to
/// outdir/clothing.md, with renders of each garment at bind and in each test pose.
/// </summary>
public static class ClothingSim
{
	sealed class Finding
	{
		public string Recipe, Stage, What, Detail;
		public bool Ok;
	}

	static readonly List<Finding> Findings = new();
	static readonly List<string> Notes = new();
	static string _recipe = "";

	/// <summary>"pinfirst": step the solver with Pin before Collide, to compare against the
	/// component's order (Collide, Tether, Pin).</summary>
	static bool _pinFirst;

	/// <summary>"old": the solver as GarmentCloth stepped it before long-range attachments and
	/// collision-last, for comparing.</summary>
	static bool _oldSolver;

	/// <summary>RunLive without the layers and padding the file carries, to compare against.</summary>
	static bool _noLayers;

	/// <summary>The last runtime run's worst 99th-percentile stretch over the poses, and its drift.</summary>
	internal static float LastStretch, LastDrift;
	internal static float LastUnderShare, LastUnderDepth, SettledUnderShare;

	static void Check( string stage, string what, bool ok, string detail = null )
	{
		Findings.Add( new Finding { Recipe = _recipe, Stage = stage, What = what, Ok = ok, Detail = detail } );
		Console.WriteLine( $"  {(ok ? "ok  " : "FAIL")} [{stage}] {what}{(detail is null ? "" : "  - " + detail)}" );
	}

	static void Note( string line )
	{
		Notes.Add( $"{_recipe}: {line}" );
		Console.WriteLine( $"       {line}" );
	}

	public static int Run( string[] a )
	{

		if ( a.Length < 2 ) { Console.Error.WriteLine( "effigy clothing outdir [recipe] [quick]" ); return 1; }

		var outDir = Path.GetFullPath( a[1] );
		Directory.CreateDirectory( outDir );
		var quick = a.Contains( "quick" );
		_pinFirst = a.Contains( "pinfirst" );
		_oldSolver = a.Contains( "old" );
		var only = a.Skip( 2 ).FirstOrDefault( x => x is not ("quick" or "pinfirst" or "old" or "blockout" or "female") );

		Findings.Clear();
		Notes.Clear();

		// The s&box human when s&box is installed (or "blockout" asks for the body grown from the
		// Citizen skeleton instead). See SboxHuman.
		var rig = CitizenSkeleton.Build();
		var skin = SkinBlockout.Build( rig );
		var human = a.Contains( "blockout" ) ? null : SboxHuman.Find( female: a.Contains( "female" ) );

		if ( human is not null )
		{
			(skin, rig) = SboxHuman.Load( human );
			Console.WriteLine( $"wearer: {Path.GetFileName( human )}" );
		}

		_recipe = "(wearer)";
		Console.WriteLine( "== Wearer" );
		var wearerObj = WearerStage( skin );

		var names = GarmentRecipe.Names.Where( n => only is null || n.Equals( only, StringComparison.OrdinalIgnoreCase ) ).ToList();

		foreach ( var name in names )
		{
			_recipe = name;
			Console.WriteLine( $"== {name}" );

			try
			{
				RunRecipe( name, rig, wearerObj, outDir, quick );
			}
			catch ( Exception e )
			{
				Check( "crash", "the recipe ran to the end", false, $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}" );
			}
		}

		var failed = Findings.Count( f => !f.Ok );
		WriteReport( Path.Combine( outDir, "clothing.md" ) );
		Console.WriteLine( $"\n{Findings.Count - failed} passed, {failed} failed - {Path.Combine( outDir, "clothing.md" )}" );
		return failed == 0 ? 0 : 3;
	}

	// --- Wearer ------------------------------------------------------------------------------

	/// <summary>The body as a compiled model hands it over: every triangle with corners of its own,
	/// the way a render vertex buffer is split at every seam, then welded back the way
	/// EffigyWindow.WearerSurface does it. Returns the OBJ the window gives WearerFeature.</summary>
	static byte[] WearerStage( PolyMesh skin )
	{
		var split = new List<Vec3>();
		var indices = new List<int>();

		foreach ( var face in skin.Faces )
			for ( var c = 1; c + 1 < face.Count; c++ )
				foreach ( var vi in new[] { face.Indices[0], face.Indices[c], face.Indices[c + 1] } )
				{
					indices.Add( split.Count );
					split.Add( skin.Positions[vi] );
				}

		var welded = WeldLikeWearerSurface( split, indices );
		var triangles = skin.Faces.Sum( f => f.Count - 2 );

		Check( "wearer", "weld puts the split render mesh back together", welded.VertexCount == skin.VertexCount,
			$"{split.Count} corners -> {welded.VertexCount} vertices, body has {skin.VertexCount}" );
		Check( "wearer", "every triangle survives the weld", welded.FaceCount == triangles, $"{welded.FaceCount} of {triangles}" );

		return System.Text.Encoding.UTF8.GetBytes( ObjWriter.Write( welded, "wearer" ) );
	}

	/// <summary>EffigyWindow.WearerSurface, minus the Model.</summary>
	static PolyMesh WeldLikeWearerSurface( List<Vec3> vertices, List<int> indices )
	{
		var mesh = new PolyMesh();
		var weld = new Dictionary<(long, long, long), int>();
		var remap = new int[vertices.Count];

		for ( var i = 0; i < vertices.Count; i++ )
		{
			var p = vertices[i];
			var key = ((long)MathF.Round( p.x * 1000f ), (long)MathF.Round( p.y * 1000f ), (long)MathF.Round( p.z * 1000f ));

			if ( !weld.TryGetValue( key, out var index ) )
			{
				index = mesh.Positions.Count;
				weld[key] = index;
				mesh.Positions.Add( p );
			}

			remap[i] = index;
		}

		for ( var i = 0; i + 2 < indices.Count; i += 3 )
		{
			int a = remap[indices[i]], b = remap[indices[i + 1]], c = remap[indices[i + 2]];

			if ( a == b || b == c || a == c )
				continue;

			mesh.Faces.Add( new Face( new[] { a, b, c } ) );
		}

		return mesh;
	}

	// --- one garment through the whole bar ---------------------------------------------------

	/// <summary>
	/// Live cloth for a document that is already built - a jacket over a shirt, padded - written
	/// and run through the runtime port, quick. What it checked comes back for the caller to report.
	/// </summary>
	internal static List<(string What, bool Ok, string Detail)> RunLive( PartStudio studio, string outDir, string file, bool layers = true )
	{
		_noLayers = !layers;
		Findings.Clear();
		Notes.Clear();
		_recipe = file;
		var cloth = LiveClothStage( studio, outDir, file );

		if ( cloth is not null )
			RuntimeStage( cloth, studio, outDir, file, quick: true );

		_noLayers = false;
		return Findings.Select( f => ($"[{f.Stage}] {f.What}", f.Ok, f.Detail) ).ToList();
	}

	static void RunRecipe( string name, Skeleton rig, byte[] wearerObj, string outDir, bool quick )
	{
		var index = Array.IndexOf( GarmentRecipe.Names, name );
		var file = name.ToLowerInvariant().Replace( ' ', '_' ).Replace( "-", "" );

		// Wearer: the rig is installed once, outside the feature (InstallWearerRig), then the
		// surface goes in as a WearerFeature.
		var studio = new PartStudio { Rig = rig.Clone() };
		var wearer = studio.Add( new WearerFeature { Name = "citizen" } );
		wearer.Model.Value = "models/citizen/citizen.vmdl";
		wearer.LoadMesh( wearerObj );

		// Garment, at the recipe's own defaults the way the dialog sets them.
		var garment = studio.Add( new GarmentFeature { Name = name } );
		garment.Recipe.Index = index;
		var (length, sleeve) = GarmentRecipe.Defaults( index );
		garment.Length.Value = length;
		garment.Sleeve.Value = sleeve;

		var clock = Stopwatch.StartNew();
		var rebuild = studio.Rebuild();
		clock.Stop();

		Check( "garment", "builds without errors", !rebuild.HasErrors,
			rebuild.HasErrors ? string.Join( "; ", rebuild.Errors.Select( e => e.Message ) ) : $"{clock.ElapsedMilliseconds} ms" );

		foreach ( var w in rebuild.Warnings )
			Note( $"rebuild warning: {w.Message}" );

		var made = studio.Bodies.Where( b => b.IsGarment ).ToList();
		Check( "garment", "a garment body came out", made.Count > 0, garment.Diagnostic?.Problem );

		if ( made.Count == 0 )
			return;

		var body = made[0];
		Check( "garment", "garment carries skin weights", body.Mesh.IsRigged && body.Mesh.Skin.Count == body.Mesh.VertexCount );
		Check( "garment", "garment is on its own material slot", garment.ResolvedSlot >= 0
			&& studio.Bodies.Where( b => !b.IsGarment ).SelectMany( b => b.Mesh.Faces ).All( f => f.Material != garment.ResolvedSlot ),
			$"slot {garment.ResolvedSlot}" );
		Check( "garment", "wearer is a reference body (never exported)",
			studio.Bodies.Where( b => !b.IsGarment ).All( b => b.IsReference ) );
		Note( $"{body.Mesh.VertexCount} v, {body.Mesh.FaceCount} f, built in {clock.ElapsedMilliseconds} ms" );

		// Trim at the lowest opening, Fur on the openings, a Fabric over the garment: the three
		// other buttons on the bar, stacked the way somebody finishing a garment would.
		var trim = studio.Add( new TrimFeature { Name = "frill" } );
		var fur = studio.Add( new FurFeature { Name = "fur" } );
		fur.Coverage.Index = 1;
		var fabric = studio.Add( new FabricFeature { Name = "fabric" } );
		fabric.Preset.Index = index % FabricMaterial.Presets.Length;

		rebuild = studio.Rebuild();
		Check( "garment", "trim, fur and fabric build on it", !rebuild.HasErrors,
			rebuild.HasErrors ? string.Join( "; ", rebuild.Errors.Select( e => e.Message ) ) : null );

		var trimBodies = studio.Bodies.Where( b => b.FeatureId == trim.Id ).ToList();
		Check( "garment", "trim made geometry", trimBodies.Sum( b => b.Mesh.FaceCount ) > 0 || trim.Diagnostic is not null,
			trim.Diagnostic?.Problem ?? $"{trimBodies.Sum( b => b.Mesh.FaceCount )} faces" );
		Check( "garment", "trim body counts as a garment", trimBodies.All( b => b.IsGarment ) );
		Check( "garment", "fur made shells", fur.ShellFaces > 0 || fur.Diagnostic is not null,
			fur.Diagnostic?.Problem ?? $"{fur.ShellFaces} shell faces on slot {fur.ResolvedSlot}" );

		MaterialStage( studio, fabric, fur, outDir, file );

		ModelRender.Named( studio.Bodies, "sheet", quick ? 256 : 512, new RenderOptions { Wireframe = false, Labels = false }, null )
			.SavePng( Path.Combine( outDir, file + "_bind.png" ) );

		TestStage( studio, outDir, file, quick );
		CheckStage( studio );
		var cloth = LiveClothStage( studio, outDir, file );

		if ( cloth is not null )
			RuntimeStage( cloth, studio, outDir, file, quick );

		ClothingStage( studio, outDir, file );
		RoundTripStage( studio, outDir, file );
	}

	// --- Material ----------------------------------------------------------------------------

	/// <summary>SyncFabricMaterials without the asset system: the maps, the vmat, and the slots
	/// the window would bind - never one the fur is on.</summary>
	static void MaterialStage( PartStudio studio, FabricFeature fabric, FurFeature fur, string outDir, string file )
	{
		Check( "material", "fabric resolved a slot", fabric.ResolvedSlots.Count > 0 && fabric.Error is null,
			fabric.Error ?? $"slots {string.Join( ",", fabric.ResolvedSlots )}" );

		var garmentSlots = studio.Features.OfType<GarmentFeature>().Select( g => g.ResolvedSlot ).ToHashSet();
		Check( "material", "fabric is on the garment's slot", fabric.ResolvedSlots.Any( garmentSlots.Contains ) );

		var wearerSlots = studio.Bodies.Where( b => !b.IsGarment ).SelectMany( b => b.Mesh.Faces ).Select( f => f.Material ).ToHashSet();
		Check( "material", "fabric leaves the wearer's slot alone", !fabric.ResolvedSlots.Any( wearerSlots.Contains ),
			$"wearer on {string.Join( ",", wearerSlots )}" );

		if ( fur.ResolvedSlot >= 0 && fabric.ResolvedSlots.Contains( fur.ResolvedSlot ) )
			Note( $"fabric resolves the fur slot {fur.ResolvedSlot} too; the window skips it when binding" );

		var colour = FurMaterial.ParseHex( fabric.Colour.Value, new Vec3( 0.35f, 0.43f, 0.55f ) );
		var accent = FurMaterial.ParseHex( fabric.Accent.Value, new Vec3( 0.91f, 0.89f, 0.83f ) );
		var (color, normal, rough) = FabricMaterial.Maps( fabric.PresetName, colour, accent,
			fabric.Scale.Clamped, fabric.Wear.Clamped, fabric.Id.GetHashCode() & 0xffff );
		var size = FabricMaterial.Size * FabricMaterial.Size * 4;

		Check( "material", $"{fabric.PresetName} maps are {FabricMaterial.Size}² RGBA",
			color.Length == size && normal.Length == size && rough.Length == size );

		File.WriteAllBytes( Path.Combine( outDir, $"{file}_fabric_color.png" ), PngWriter.ToBytesRgba( color, FabricMaterial.Size, FabricMaterial.Size ) );

		var vmat = FabricMaterial.VmatSource( fabric.PresetName, "a_color.png", "a_normal.png", "a_rough.png" );
		Check( "material", "vmat names all three maps", vmat.Contains( "a_color.png" ) && vmat.Contains( "a_normal.png" ) && vmat.Contains( "a_rough.png" ) );
	}

	// --- Test --------------------------------------------------------------------------------

	static void TestStage( PartStudio studio, string outDir, string file, bool quick )
	{
		{
			// How much of each limb is weighted to bones of another part - what BindSmooth bleeds.
			var map = new BodyRegions.Map( studio.Rig );
			var wearer = studio.Bodies.First( b => !b.IsGarment ).Mesh;
			var bound = SkinBinder.BindSmooth( wearer, studio.Rig );
			string PartOf( BodyRegion r, float y ) => r switch
			{
				BodyRegion.UpperArm or BodyRegion.LowerArm or BodyRegion.Hand => y > 0 ? "arm L" : "arm R",
				BodyRegion.UpperLeg or BodyRegion.LowerLeg or BodyRegion.Foot => y > 0 ? "leg L" : "leg R",
				_ => "body",
			};
			var bonePart = Enumerable.Range( 0, studio.Rig.Count ).Select( i => PartOf( BodyRegions.Classify( studio.Rig.Bones[i].Name ), studio.Rig.HeadWorld( i ).y ) ).ToArray();
			var bleed = new Dictionary<string, (int N, float W)>();

			for ( var v = 0; v < wearer.VertexCount; v++ )
			{
				var (r, _) = map.Locate( wearer.Positions[v] );
				var part = PartOf( r, wearer.Positions[v].y );
				var foreign = bound[v].Where( w => bonePart[w.Bone] != part && part != "body" ).Sum( w => w.Weight );
				if ( foreign > 0.05f )
				{
					var e = bleed.GetValueOrDefault( part );
					bleed[part] = (e.N + 1, MathF.Max( e.W, foreign ));
				}
			}

			Note( $"wearer BindSmooth bleed: {string.Join( ", ", bleed.Select( kv => $"{kv.Key} {kv.Value.N} verts (up to {kv.Value.W:P0} foreign)" ) )}" );
		}

		var bindSnapshot = studio.Rig.Clone();
		var bindWorld = Enumerable.Range( 0, bindSnapshot.Count ).Select( bindSnapshot.WorldBind ).ToArray();
		var testWeights = new Dictionary<string, SkinWeights>();

		foreach ( var pose in GarmentPoses.All )
		{
			foreach ( var amount in quick ? new[] { 1f } : new[] { 0.5f, 1f } )
			{
				// TestPose: restore, then bend in place.
				RestoreBind( studio.Rig, bindSnapshot );
				var turned = GarmentPoses.Apply( studio.Rig, pose, amount );
				Check( "test", $"{pose.Name} {amount:P0} moves bones", turned > 0, $"{turned} turned" );

				var posed = Enumerable.Range( 0, studio.Rig.Count ).Select( studio.Rig.WorldBind ).ToArray();
				var garments = new List<(string Name, PolyMesh Mesh)>();
				var worn = new List<PolyMesh>();
				var shown = new List<Body>();

				foreach ( var body in studio.Bodies )
				{
					var deformed = DeformForTest( body, bindSnapshot, bindWorld, posed, testWeights );
					(body.IsGarment ? null : worn)?.Add( deformed );

					if ( body.IsGarment )
						garments.Add( (body.Name, deformed) );

					shown.Add( new Body( body.Id, body.Name, deformed ) { IsGarment = body.IsGarment } );
				}

				var clip = 0;
				var deepest = 0f;

				var where = new Dictionary<string, int>();
				var map = new BodyRegions.Map( bindSnapshot );

				foreach ( var (gname, mesh) in garments )
				{
					var cloth = WithoutFur( mesh, FurSlots( studio ) );
					var r = GarmentCheck.Run( cloth, worn );
					clip += r.Clipping;
					deepest = Math.Max( deepest, r.DeepestClip );

					// Where on the body, at bind, the clipping vertices sit.
					var bindCloth = WithoutFur( studio.Bodies.First( b => b.Name == gname && b.IsGarment ).Mesh, FurSlots( studio ) );
					var index = new Dictionary<(float, float, float), int>();
					for ( var i = 0; i < cloth.VertexCount; i++ ) index[(cloth.Positions[i].x, cloth.Positions[i].y, cloth.Positions[i].z)] = i;

					foreach ( var cp in r.ClipPoints )
						if ( index.TryGetValue( (cp.x, cp.y, cp.z), out var vi ) && vi < bindCloth.VertexCount )
						{
							var (reg, t) = map.Locate( bindCloth.Positions[vi] );
							var key = $"{reg}@{MathF.Round( t * 4f ) / 4f:0.##}";
							where[key] = where.GetValueOrDefault( key ) + 1;
						}
				}

				if ( where.Count > 0 && clip > 0 )
					Note( $"  where: {string.Join( ", ", where.OrderByDescending( kv => kv.Value ).Take( 6 ).Select( kv => $"{kv.Key} x{kv.Value}" ) )}" );

				var total = garments.Sum( g => WithoutFur( g.Mesh, FurSlots( studio ) ).VertexCount );
				Note( $"test {pose.Name} {amount:P0}: {clip}/{total} garment vertices clip, deepest {deepest:0.##} in" );

				// A pose that drives a sizeable share of the garment inside the body is not a
				// graze a Push out can fix - it is the weights failing to follow the limb.
				Check( "test", $"{pose.Name} {amount:P0}: under 5% of the garment ends up inside the body",
					total == 0 || clip <= total * 0.05f, $"{clip}/{total}, deepest {deepest:0.##} in" );

				if ( amount == 1f )
					ModelRender.Render( shown, RenderView.Turn( 25f, 10f ), quick ? 200 : 320, new RenderOptions { Grid = false, Axes = false, Labels = false } )
						.SavePng( Path.Combine( outDir, $"{file}_pose_{pose.Name.ToLowerInvariant().Replace( ' ', '_' )}.png" ) );
			}
		}

		// Relax.
		RestoreBind( studio.Rig, bindSnapshot );
		var back = Enumerable.Range( 0, studio.Rig.Count ).All( i => studio.Rig.WorldBind( i ).Origin.AlmostEquals( bindWorld[i].Origin, 1e-4f ) );
		Check( "test", "Relax puts every bone back", back );
	}

	/// <summary>EffigyWindow.RestoreTestBind.</summary>
	static void RestoreBind( Skeleton rig, Skeleton bind )
	{
		for ( var i = 0; i < rig.Count; i++ )
		{
			var at = bind.IndexOf( rig.Bones[i].Name );

			if ( at < 0 )
				continue;

			rig.Bones[i].Local = bind.Bones[at].Local;
			rig.Bones[i].Length = bind.Bones[at].Length;
		}
	}

	/// <summary>EffigyWindow.DeformForTest.</summary>
	static PolyMesh DeformForTest( Body body, Skeleton bindRig, Xform[] bind, Xform[] posed, Dictionary<string, SkinWeights> cache )
	{
		var mesh = body.Mesh;
		SkinWeights weights;

		if ( mesh.IsRigged )
			weights = mesh.Skin;
		else if ( !cache.TryGetValue( body.Id, out weights ) || weights.Count != mesh.VertexCount )
			cache[body.Id] = weights = SkinBinder.BindSmooth( mesh, bindRig );

		if ( weights is null || weights.Count != mesh.VertexCount )
			return mesh;

		var result = mesh.Clone();
		var moved = SkinBinder.Deform( mesh.Positions, weights, bind, posed );

		for ( var i = 0; i < moved.Length; i++ )
			result.Positions[i] = moved[i];

		return result;
	}

	// --- Check + Push out --------------------------------------------------------------------

	static void CheckStage( PartStudio studio )
	{
		for ( var round = 0; round < 4; round++ )
		{
			var worn = studio.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).ToList();
			var depths = new Dictionary<string, float>();
			var clipping = 0;

			foreach ( var g in studio.Bodies.Where( b => b.IsGarment ) )
			{
				var report = GarmentCheck.Run( WithoutFur( g.Mesh, FurSlots( studio ) ), worn );
				clipping += report.Clipping;

				if ( round == 0 )
				{
					Note( $"check {g.Name}: {report}" );
					Check( "check", $"{g.Name} has UVs", report.HasUVs );
					Check( "check", $"{g.Name} has weights", report.HasWeights );
					Check( "check", $"{g.Name} has no degenerate faces", report.Degenerate == 0, $"{report.Degenerate}" );
					Check( "check", $"{g.Name} has no structure faults", report.Structure.Count == 0, string.Join( "; ", report.Structure.Take( 3 ) ) );
					Check( "check", $"{g.Name} has openings", report.Openings > 0 || g.Name.Contains( "on " ), $"{report.Openings}" );
				}

				if ( report.Clipping > 0 && g.FeatureId is { } id )
					depths[id] = Math.Max( depths.GetValueOrDefault( id ), report.DeepestClip );
			}

			if ( clipping == 0 )
			{
				Check( "check", round == 0 ? "nothing clips at bind" : $"Push out cleared the clipping in {round} round(s)", true );
				return;
			}

			Note( $"check round {round}: {clipping} vertices clip" );

			// PushOutClipping.
			var hitCap = false;

			foreach ( var kv in depths )
			{
				var feature = studio.Features.FirstOrDefault( f => f.Id == kv.Key );
				var targets = feature switch
				{
					GarmentFeature g => new[] { g },
					TrimFeature t => studio.Features.OfType<GarmentFeature>().ToArray(),
					_ => Array.Empty<GarmentFeature>(),
				};

				foreach ( var g in targets )
				{
					var next = g.Clearance.Clamped + kv.Value + 0.05f;
					hitCap |= next > g.Clearance.Max;
					g.Clearance.Value = MathF.Round( MathF.Min( next, g.Clearance.Max ), 2 );
					g.Clearance.Expr = null;
					studio.MarkDirty( g );
				}
			}

			studio.Rebuild();

			if ( hitCap )
			{
				Check( "check", "Push out clears the clipping before Clearance runs out", false );
				return;
			}
		}

		Check( "check", "Push out clears the clipping within 3 rounds", false );
	}

	// --- Publish: live cloth -----------------------------------------------------------------

	sealed class ClothFile
	{
		public string Material { get; set; }
		public FabricData Fabric { get; set; }
		public float[] Pins { get; set; }
		public float[] Fitted { get; set; }
		public float[] Mass { get; set; }
		public float[] BendScale { get; set; }
		public float[] Under { get; set; }
		public float[] Over { get; set; }
		public int[] Layers { get; set; }
		public float[] Positions { get; set; }
		public float[] UVs { get; set; }
		public int[] Triangles { get; set; }
		public string[] VertexBones { get; set; }
		public CapsuleData[] Capsules { get; set; }
	}

	sealed class FabricData
	{
		public float Stiffness { get; set; } = 0.9f;
		public float Bend { get; set; } = 0.15f;
		public float Damping { get; set; } = 0.03f;
		public float Friction { get; set; } = 0.6f;
	}

	sealed class CapsuleData
	{
		public string Bone { get; set; }
		public float[] A { get; set; }
		public float[] B { get; set; }
		public float Radius { get; set; }
	}

	/// <summary>EffigyWindow.PublishLiveCloth, writing to outDir, then the file read back with
	/// the same shape GarmentCloth deserialises into.</summary>
	static ClothFile LiveClothStage( PartStudio studio, string outDir, string file )
	{
		var mesh = new PolyMesh();
		var pins = new List<float>();
		ClothExport.Settings fabric = null;

		// Which layer each particle is in and the padding sewn into it, so the jacket stays over
		// the shirt and a quilted yoke is heavier and stiffer in the game too. See ClothExport.Layering.
		var layering = new ClothExport.Layering();
		var pads = studio.Features.OfType<PaddingFeature>().ToList();
		var regions = new BodyRegions.Map( studio.Rig );

		var furSlots = studio.Features.OfType<FurFeature>()
			.Where( f => !f.Suppressed && f.ResolvedSlot >= 0 )
			.Select( f => f.ResolvedSlot )
			.ToHashSet();

		foreach ( var body in studio.Bodies.Where( b => b.IsGarment ) )
		{
			var cloth = WithoutFur( MeshSolidify.OuterSheet( body.Mesh, out _ ) ?? body.Mesh, furSlots );

			if ( cloth.VertexCount == 0 )
				continue;

			var offset = mesh.VertexCount;

			foreach ( var p in cloth.Positions )
				mesh.AddVertex( p );

			foreach ( var face in cloth.Faces )
				mesh.AddFace( face.Indices.Select( i => i + offset ).ToArray(), face.UVs, face.Material );

			var feature = studio.Features.OfType<GarmentFeature>().FirstOrDefault( g => g.Id == body.FeatureId );
			fabric ??= feature is null ? null : ClothExport.FabricSettings( feature.FabricValue, feature.Stiffness.Clamped );
			pins.AddRange( ClothExport.PinTop( cloth, feature?.LivePinFraction ?? 0.12f ) );
			layering.Add( cloth, body, pads, regions );
		}

		var map = new BodyRegions.Map( studio.Rig );
		var worn = studio.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).ToList();
		var capsules = BoneCapsules.Build( map, worn );

		Check( "live cloth", "capsules found on the wearer", capsules.Count > 0, $"{capsules.Count}" );

		if ( capsules.Count == 0 )
			return null;

		var json = ClothExport.Write( mesh, capsules, studio.Rig, "models/effigy/fabric/x.vmat", fabric, pins.ToArray(), layering );
		File.WriteAllText( Path.Combine( outDir, $"{file}.cloth.json" ), json );

		ClothFile data;

		try
		{
			data = JsonSerializer.Deserialize<ClothFile>( json );
		}
		catch ( Exception e )
		{
			Check( "live cloth", "file is valid JSON", false, e.Message );
			return null;
		}

		var count = data.Positions.Length / 3;
		Check( "live cloth", "file is valid JSON", true, $"{json.Length / 1024} KB" );
		Check( "live cloth", "positions match the cloth mesh", count == mesh.VertexCount, $"{count} vs {mesh.VertexCount}" );
		Check( "live cloth", "no fur shells in the cloth", mesh.Faces.All( f => !furSlots.Contains( f.Material ) ) );
		Check( "live cloth", "triangle indices in range", data.Triangles.Length % 3 == 0 && data.Triangles.All( t => t >= 0 && t < count ) );
		Check( "live cloth", "one bone per vertex, all real bones",
			data.VertexBones.Length == count && data.VertexBones.All( b => studio.Rig.IndexOf( b ) >= 0 ),
			$"{data.VertexBones.Distinct().Count()} bones used" );
		Check( "live cloth", "capsule bones are real bones", data.Capsules.All( c => studio.Rig.IndexOf( c.Bone ) >= 0 ) );
		Check( "live cloth", "pins written, one per vertex", data.Pins is not null && data.Pins.Length == count );
		Check( "live cloth", "fabric written", data.Fabric is not null );
		Check( "live cloth", "UVs written, one per vertex", data.UVs.Length == count * 2 );
		Check( "live cloth", "every number finite", data.Positions.All( float.IsFinite ) && data.UVs.All( float.IsFinite ) );

		if ( data.Pins is not null )
			Note( $"live cloth: {count} particles, {data.Pins.Count( p => p > 0.99f )} fully pinned, {data.Pins.Count( p => p > 0f && p <= 0.99f )} partly" );

		return data;
	}

	/// <summary>EffigyWindow.FurSlots.</summary>
	static HashSet<int> FurSlots( PartStudio studio ) => studio.Features.OfType<FurFeature>()
		.Where( f => !f.Suppressed && f.ResolvedSlot >= 0 )
		.Select( f => f.ResolvedSlot )
		.ToHashSet();

	/// <summary>EffigyWindow.WithoutFur.</summary>
	static PolyMesh WithoutFur( PolyMesh source, HashSet<int> furSlots )
	{
		if ( source is null || furSlots.Count == 0 || !source.Faces.Any( f => furSlots.Contains( f.Material ) ) )
			return source;

		// IN THE ORDER THEY CAME. Shells are appended after the cloth, so keeping every surviving
		// vertex where it was leaves the cloth's own layout alone - and that layout is what
		// MeshSolidify.OuterSheet reads the openings off. Numbering them as the faces met them
		// shuffled a solidified garment back to "0 openings".
		var map = new int[source.VertexCount];
		Array.Fill( map, -1 );

		foreach ( var face in source.Faces )
			if ( !furSlots.Contains( face.Material ) )
				foreach ( var vi in face.Indices )
					if ( (uint)vi < (uint)map.Length )
						map[vi] = 0;

		var cloth = new PolyMesh();

		// The weights come along: Check reports a garment without them as unrigged.
		var skin = source.IsRigged ? new SkinWeights() : null;

		for ( var vi = 0; vi < map.Length; vi++ )
		{
			if ( map[vi] < 0 )
				continue;

			map[vi] = cloth.VertexCount;
			cloth.AddVertex( source.Positions[vi] );
			skin?.Vertices.Add( source.Skin[vi] );
		}

		foreach ( var face in source.Faces )
		{
			if ( furSlots.Contains( face.Material ) || face.Indices.Any( vi => (uint)vi >= (uint)map.Length ) )
				continue;

			cloth.AddFace( face.Indices.Select( vi => map[vi] ).ToArray(), face.UVs, face.Material );
		}

		cloth.Skin = skin;
		return cloth;
	}

	// --- the runtime: GarmentCloth's solver --------------------------------------------------

	/// <summary>
	/// GarmentCloth.Load/Step/Collide/Tether/Pin on Vec3 and Xform, with the component's default
	/// sliders and FabricFromFile on. The wearer stands still (Carry is a no-op), settles for two
	/// seconds, then goes through each test pose over half a second, holds it, and comes back.
	/// </summary>
	static void RuntimeStage( ClothFile data, PartStudio studio, string outDir, string file, bool quick )
	{
		const float Gravity = 386f, Leash = 6f, Skin = 0.25f;
		const int Iterations = 6, Substeps = 2;
		const float Dt = 1f / 60f;

		var rig = studio.Rig;
		var count = data.Positions.Length / 3;

		var bones = new List<string>();
		int BoneIndex( string name )
		{
			if ( rig.IndexOf( name ) < 0 )
				name = rig.Bones[0].Name;

			var i = bones.IndexOf( name );
			if ( i >= 0 ) return i;
			bones.Add( name );
			return bones.Count - 1;
		}

		Vec3 V( float[] a, int i ) => new( a[i], a[i + 1], a[i + 2] );

		var vertexBone = new int[count];
		var bindOf = new Func<int, Xform>( b => rig.WorldBind( rig.IndexOf( bones[b] ) ) );
		for ( var i = 0; i < count; i++ ) vertexBone[i] = BoneIndex( data.VertexBones[i] );
		var caps = data.Capsules.Select( c => (Bone: BoneIndex( c.Bone ), A: V( c.A, 0 ), B: V( c.B, 0 ), c.Radius) ).ToArray();

		// GarmentCloth.ReadFitted: per particle and capsule, how far from the core it was fitted.
		var fittedGap = new Dictionary<(int, int), float>();
		if ( data.Fitted is { } ft && !_oldSolver )
			for ( var k = 0; k + 2 < ft.Length; k += 3 )
				fittedGap[((int)ft[k], (int)ft[k + 1])] = ft[k + 2];

		float Allowed( int i, int c, float radius ) => fittedGap.TryGetValue( (i, c), out var g ) ? MathF.Min( radius, g ) : radius;

		var bindBone = Enumerable.Range( 0, bones.Count ).Select( bindOf ).ToArray();
		var local = new Vec3[count];
		for ( var i = 0; i < count; i++ ) local[i] = bindBone[vertexBone[i]].Inverse.TransformPoint( V( data.Positions, i * 3 ) );
		var capsLocal = caps.Select( c => (c.Bone, A: bindBone[c.Bone].Inverse.TransformPoint( c.A ), B: bindBone[c.Bone].Inverse.TransformPoint( c.B ), c.Radius) ).ToArray();

		var links = BuildLinks( data.Positions, data.Triangles );
		var fabric = data.Fabric;
		var pins = data.Pins is { } p && p.Length == count ? p : null;

		var anchors = _oldSolver ? null : BuildAnchors( data.Positions, links, pins );

		// GarmentCloth's padding and layers.
		var invMass = !_noLayers && data.Mass is { } ms && ms.Length == count ? ms.Select( v => 1f / MathF.Max( v, 0.1f ) ).ToArray() : null;
		var bendScale = !_noLayers && data.BendScale is { } bs && bs.Length == count ? bs : null;
		var under = !_noLayers && data.Under is { } un && un.Length == count ? un : null;
		var over = new List<(int O, int I, float Gap)>();
		if ( data.Over is { } ov && !_noLayers )
			for ( var k = 0; k + 2 < ov.Length; k += 3 )
				if ( (int)ov[k] >= 0 && (int)ov[k] < count && (int)ov[k + 1] >= 0 && (int)ov[k + 1] < count )
					over.Add( ((int)ov[k], (int)ov[k + 1], MathF.Max( 0f, ov[k + 2] )) );
		float Share( int a, int b ) => invMass is null || invMass[a] + invMass[b] < 1e-6f ? 0.5f : invMass[a] / (invMass[a] + invMass[b]);
		var normals = new Vec3[count];
		var tris = data.Triangles;
		var fans = new List<int>[count];
		for ( var i = 0; i < count; i++ ) fans[i] = new List<int>();
		for ( var t = 0; t + 2 < tris.Length; t += 3 ) { fans[tris[t]].Add( t ); fans[tris[t + 1]].Add( t ); fans[tris[t + 2]].Add( t ); }
		var around = new List<int>[count];
		for ( var i = 0; i < count; i++ ) around[i] = new List<int>();
		foreach ( var (a, b, _, bend) in links ) if ( !bend ) { around[a].Add( b ); around[b].Add( a ); }

		// GarmentCloth.Nearest.
		int Nearest( Vec3[] x, Vec3 p, int from )
		{
			var best = (x[from] - p).LengthSquared;
			for ( var hop = 0; hop < 16; hop++ )
			{
				var next = from;
				foreach ( var j in around[from] ) { var d = (x[j] - p).LengthSquared; if ( d < best ) { best = d; next = j; } }
				if ( next == from ) break;
				from = next;
			}
			return from;
		}

		var x = new Vec3[count];
		var prev = new Vec3[count];
		for ( var i = 0; i < count; i++ ) x[i] = prev[i] = bindBone[vertexBone[i]].TransformPoint( local[i] );

		var boneWorld = (Xform[])bindBone.Clone();
		var capsWorld = new (Vec3 A, Vec3 B, float R)[caps.Length];

		void ReadBones( Skeleton posedRig )
		{
			for ( var b = 0; b < bones.Count; b++ )
				boneWorld[b] = posedRig.WorldBind( posedRig.IndexOf( bones[b] ) );

			for ( var c = 0; c < capsLocal.Length; c++ )
			{
				var w = boneWorld[capsLocal[c].Bone];
				capsWorld[c] = (w.TransformPoint( capsLocal[c].A ), w.TransformPoint( capsLocal[c].B ), capsLocal[c].Radius);
			}
		}

		Vec3 Home( int i ) => boneWorld[vertexBone[i]].TransformPoint( local[i] );

		void Step( float h )
		{
			var gravity = new Vec3( 0, 0, -1 ) * (Gravity * h * h);
			var keep = 1f - fabric.Damping;

			for ( var i = 0; i < count; i++ )
			{
				var velocity = (x[i] - prev[i]) * keep;
				prev[i] = x[i];
				x[i] += velocity + gravity;
			}

			var bend = fabric.Bend * fabric.Bend;

			for ( var it = 0; it < Iterations; it++ )
				foreach ( var (a, b, rest, isBend) in links )
				{
					var d = x[a] - x[b];
					var len = d.Length;
					if ( len < 1e-6f ) continue;
					var k = isBend ? bend : fabric.Stiffness;
					if ( isBend && bendScale is not null ) k = MathF.Min( 1f, k * MathF.Sqrt( bendScale[a] * bendScale[b] ) );
					var share = Share( a, b );
					var correction = d * ((len - rest) / len * k);
					x[a] -= correction * share;
					x[b] += correction * (1f - share);
				}

			if ( _pinFirst || !_oldSolver )
				Pin();

			if ( !_oldSolver )
			{
				Tether();
				Attach();
			}

			Collide();

			if ( over.Count > 0 )
			{
				Layers( false );
				Collide();
				for ( var round = 0; round < 2; round++ )
				{
					Layers( true );
					Collide();
				}
			}

			if ( _oldSolver )
			{
				Tether();

				if ( !_pinFirst )
					Pin();
			}
		}

		void Normals()
		{
			Array.Clear( normals );
			for ( var t = 0; t + 2 < data.Triangles.Length; t += 3 )
			{
				var (a, b, c) = (data.Triangles[t], data.Triangles[t + 1], data.Triangles[t + 2]);
				var face = Vec3.Cross( x[b] - x[a], x[c] - x[a] );
				normals[a] += face; normals[b] += face; normals[c] += face;
			}
		}

		// GarmentCloth.Layers.
		void Layers( bool outerOnly )
		{
			for ( var k = 0; k < over.Count; k++ )
			{
				var (o, i, gap) = over[k];
				i = Nearest( x, x[o], i );
				over[k] = (o, i, gap);
				var best = -1; var bestD = float.MaxValue; var bary = (A: 1f, B: 0f, C: 0f);
				foreach ( var t in fans[i] )
				{
					var w = ClosestOnTriangle( x[o], x[tris[t]], x[tris[t + 1]], x[tris[t + 2]] );
					var q = x[tris[t]] * w.A + x[tris[t + 1]] * w.B + x[tris[t + 2]] * w.C;
					var d = (x[o] - q).LengthSquared;
					if ( d < bestD ) { bestD = d; best = t; bary = w; }
				}
				if ( best < 0 ) continue;
				var (a, b, c) = (tris[best], tris[best + 1], tris[best + 2]);
				var normal = Vec3.Cross( x[b] - x[a], x[c] - x[a] ).Normal;
				var on = x[a] * bary.A + x[b] * bary.B + x[c] * bary.C;
				var along = Vec3.Dot( x[o] - on, normal );
				if ( along >= gap ) continue;
				float W( int p ) => (invMass?[p] ?? 1f) * (1f - (pins?[p] ?? 0f));
				var wo = outerOnly ? 1f : W( o );
				var (wa, wb, wc) = outerOnly ? (0f, 0f, 0f) : (W( a ), W( b ), W( c ));
				var denom = wo + bary.A * bary.A * wa + bary.B * bary.B * wb + bary.C * bary.C * wc;
				if ( denom < 1e-6f ) continue;
				var s = (gap - along) / denom;
				x[o] += normal * (s * wo);
				x[a] -= normal * (s * bary.A * wa);
				x[b] -= normal * (s * bary.B * wb);
				x[c] -= normal * (s * bary.C * wc);
			}
		}

		void Collide()
		{
			for ( var i = 0; i < count; i++ )
				for ( var ci = 0; ci < capsWorld.Length; ci++ )
				{
					var (a, b, radius) = capsWorld[ci];
					var ab = b - a;
					var lenSq = ab.LengthSquared;
					var t = lenSq < 1e-8f ? 0f : Math.Clamp( Vec3.Dot( x[i] - a, ab ) / lenSq, 0f, 1f );
					var core = a + ab * t;
					var d = x[i] - core;
					var dist = d.Length;
					var want = Allowed( i, ci, radius + Skin + (under?[i] ?? 0f) );
					if ( dist >= want ) continue;
					var normal = dist > 1e-5f ? d / dist : new Vec3( 0, 0, 1 );
					var pushed = core + normal * want;
					var moved = pushed - prev[i];
					var slide = moved - normal * Vec3.Dot( moved, normal );
					x[i] = pushed - slide * fabric.Friction;
				}
		}

		void Tether()
		{
			for ( var i = 0; i < count; i++ )
			{
				var home = Home( i );
				var d = x[i] - home;
				var len = d.Length;
				if ( len > Leash ) { x[i] = home + d * (Leash / len); prev[i] = x[i]; }
			}
		}

		void Attach()
		{
			if ( anchors is null )
				return;

			for ( var i = 0; i < count; i++ )
			{
				var (anchor, reach) = anchors[i];

				if ( anchor < 0 )
					continue;

				var d = x[i] - x[anchor];
				var len = d.Length;

				if ( len > reach )
					x[i] = x[anchor] + d * (reach / len);
			}
		}

		void Pin()
		{
			if ( pins is null )
				return;

			for ( var i = 0; i < count; i++ )
			{
				if ( pins[i] <= 0f ) continue;
				var pull = (Home( i ) - x[i]) * pins[i];
				x[i] += pull;
				prev[i] += pull;
			}
		}

		// The outer layer against the inner one's real surface: each outer particle that has a
		// layer under it, signed distance to the nearest triangle of that layer. Under is the
		// share of them on the wrong side, and how deep the deepest is.
		var layerOf = data.Layers;
		void UnderSurface( string when )
		{
			if ( layerOf is null || layerOf.Length != count ) return;
			var top = layerOf.Max();
			var inner = new PolyMesh();
			var map = new int[count];
			for ( var i = 0; i < count; i++ ) map[i] = layerOf[i] < top ? inner.AddVertex( x[i] ) : -1;
			for ( var t = 0; t + 2 < data.Triangles.Length; t += 3 )
			{
				var (a, b, c) = (data.Triangles[t], data.Triangles[t + 1], data.Triangles[t + 2]);
				if ( map[a] >= 0 && map[b] >= 0 && map[c] >= 0 ) inner.AddFace( new[] { map[a], map[b], map[c] } );
			}
			if ( inner.FaceCount == 0 ) return;
			var tree = MeshBVH.Build( inner );
			var outer = Enumerable.Range( 0, count ).Where( i => layerOf[i] == top ).ToList();
			// Only over the surface: the nearest point straight under the particle. Past an open
			// edge (a cuff, the neckline) the nearest point is on the rim and in or out means nothing.
			float Over( int i )
			{
				if ( tree.NearestSurface( inner, x[i], 1.5f ) is not { } h ) return float.NaN;
				var d = x[i] - h.Point;
				var along = Vec3.Dot( d, h.Normal );
				return (d - h.Normal * along).Length < 0.1f ? along : float.NaN;
			}
			var depths = outer.Select( Over )
				.Where( float.IsFinite ).ToList();
			if ( depths.Count == 0 ) return;
			var share = depths.Count( d => d < -0.05f ) / (float)depths.Count;
			if ( when == "settled" ) SettledUnderShare = share;
			LastUnderShare = MathF.Max( LastUnderShare, share );
			LastUnderDepth = MathF.Max( LastUnderDepth, -depths.Min() );
			var bonesUnder = outer.Where( i => Over( i ) < -0.05f )
				.GroupBy( i => data.VertexBones[i] ).OrderByDescending( g => g.Count() ).Take( 5 ).Select( g => $"{g.Key} {g.Count()}" );
			Note( $"runtime {when}: under by bone: {string.Join( ", ", bonesUnder )}" );
			Note( $"runtime {when}: {depths.Count( d => d < -0.05f )} of {depths.Count} outer-layer particles under the layer below, deepest {-depths.Min():0.##} in" );
		}

		// Measures, per frame.
		var finite = true;
		var worstInside = 0f;
		var worstStretch = 0f;
		var maxSpeed = 0f;
		var worstThrough = 0f;
		LastStretch = 0f;
		LastUnderShare = 0f;
		LastUnderDepth = 0f;
		SettledUnderShare = 0f;
		var strains = new List<float>();

		void Measure()
		{
			for ( var i = 0; i < count; i++ )
			{
				if ( !float.IsFinite( x[i].x ) || !float.IsFinite( x[i].y ) || !float.IsFinite( x[i].z ) ) { finite = false; return; }
				maxSpeed = MathF.Max( maxSpeed, (x[i] - prev[i]).Length / (Dt / Substeps) );

				// Deeper than the cloth was fitted, which is the only depth the component resists.
				for ( var ci = 0; ci < capsWorld.Length; ci++ )
				{
					var (a, b, r) = capsWorld[ci];
					var ab = b - a;
					var t = ab.LengthSquared < 1e-8f ? 0f : Math.Clamp( Vec3.Dot( x[i] - a, ab ) / ab.LengthSquared, 0f, 1f );
					worstInside = MathF.Max( worstInside, Allowed( i, ci, r ) - (x[i] - (a + ab * t)).Length );
				}
			}

			// A layer through the one under it: how far an outer particle is on the wrong side
			// of the inner particle it was fitted over.
			if ( over.Count > 0 )
			{
				Normals();
				foreach ( var (o, i, _) in over )
					worstThrough = MathF.Max( worstThrough, -Vec3.Dot( x[o] - x[i], normals[i].Normal ) );
			}

			var frame = new List<float>( links.Length );

			foreach ( var (a, b, rest, isBend) in links )
				// Not the hair-length edges a cut leaves beside a corner: a hundredth of an inch
				// moved by a hundredth reads as double, and says nothing about the cloth.
				if ( !isBend && rest > 0.05f )
					frame.Add( (x[a] - x[b]).Length / rest );

			frame.Sort();
			worstStretch = MathF.Max( worstStretch, frame[^1] );
			strains.Add( frame[(int)(frame.Count * 0.99f)] );
		}

		void Frames( int n, Func<int, Skeleton> poseAt )
		{
			for ( var f = 0; f < n && finite; f++ )
			{
				ReadBones( poseAt( f ) );
				for ( var s = 0; s < Substeps; s++ ) Step( Dt / Substeps );
				Measure();
			}
		}

		Skeleton Posed( GarmentPoses.Pose pose, float amount )
		{
			var s = rig.Clone();
			if ( amount > 0f ) GarmentPoses.Apply( s, pose, amount );
			return s;
		}

		// Settle at bind.
		var bindRig = rig.Clone();
		Frames( quick ? 30 : 60, _ => bindRig );
		worstThrough = 0f;
		Frames( quick ? 30 : 60, _ => bindRig );

		UnderSurface( "settled" );
		var sag = 0f;
		var free = 0;
		for ( var i = 0; i < count; i++ )
			if ( pins is null || pins[i] < 0.01f ) { sag += Home( i ).z - x[i].z; free++; }
		sag = free > 0 ? sag / free : 0f;

		Check( "runtime", "stays finite while settling", finite );
		if ( over.Count > 0 )
		{
			Note( $"runtime layers: {over.Count} particles held over the layer under them, worst through it {worstThrough:0.##} in" );
			Check( "runtime", "settled: no layer through the one under it (> 0.1 in)", worstThrough < 0.1f, $"{worstThrough:0.##} in" );
		}
		Note( $"runtime settle: free cloth sags {sag:0.##} in on average, worst inside a capsule {worstInside:0.##} in, 99th-percentile stretch {strains.Max():0.##}x (one edge {worstStretch:0.##}x)" );
		Check( "runtime", "settled cloth hangs within the leash, not on the floor", sag < Leash * 0.9f, $"mean sag {sag:0.##} in" );

		var settled = MeshAt( x, data.Triangles );
		var bindWorn = studio.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).ToList();
		var settleReport = GarmentCheck.Run( settled, bindWorn );
		Note( $"runtime settle vs the real body: {settleReport.Clipping}/{count} particles inside, deepest {settleReport.DeepestClip:0.##} in" );

		var bodyRender = new List<Body>();
		foreach ( var b in studio.Bodies.Where( b => !b.IsGarment ) ) bodyRender.Add( b );
		bodyRender.Add( new Body( "cloth", "cloth", settled ) { IsGarment = true } );
		var fitted = studio.Bodies.Where( b => !b.IsGarment ).ToList();
		fitted.Add( new Body( "fitted", "fitted", MeshAt( Enumerable.Range( 0, count ).Select( i => V( data.Positions, i * 3 ) ).ToArray(), data.Triangles ) ) { IsGarment = true } );

		foreach ( var (view, tag) in new[] { (RenderView.Turn( 0f, 0f ), "front"), (RenderView.Turn( 90f, 0f ), "side") } )
		{
			var size = quick ? 240 : 480;
			ModelRender.Render( fitted, view, size, new RenderOptions { Grid = false, Axes = false, Labels = false } )
				.SavePng( Path.Combine( outDir, $"{file}_live_{tag}_fitted.png" ) );
			ModelRender.Render( bodyRender, view, size, new RenderOptions { Grid = false, Axes = false, Labels = false } )
				.SavePng( Path.Combine( outDir, $"{file}_live_{tag}_settled.png" ) );
		}

		// Each pose: half a second in, half held, half back.
		foreach ( var pose in quick ? GarmentPoses.All.Take( 2 ) : GarmentPoses.All )
		{
			worstInside = 0f; worstStretch = 0f; maxSpeed = 0f; worstThrough = 0f; strains.Clear();
			const int In = 30, Hold = 30;
			var full = Posed( pose, 1f );
			var steps = Enumerable.Range( 0, In + 1 ).Select( k => Posed( pose, k / (float)In ) ).ToArray();

			Frames( In, f => steps[f + 1] );
			Frames( Hold, _ => full );
			UnderSurface( pose.Name );
			Frames( In, f => steps[In - 1 - f] );

			Check( "runtime", $"{pose.Name}: stays finite", finite );
			var p99 = strains.Max();
			LastStretch = MathF.Max( LastStretch, p99 );
			Note( $"runtime {pose.Name}: worst inside a capsule {worstInside:0.##} in, 99th-percentile stretch {p99:0.##}x (one edge {worstStretch:0.##}x), max speed {maxSpeed:0} in/s" );
			Check( "runtime", $"{pose.Name}: no particle left deep inside a capsule (> 1 in)", worstInside < 1f, $"{worstInside:0.##} in" );
			Check( "runtime", $"{pose.Name}: 99% of edges within 1.5x their length", p99 < 1.5f, $"{p99:0.##}x" );

			if ( over.Count > 0 )
				Note( $"runtime {pose.Name}: layer pairs, worst through by particle normal {worstThrough:0.##} in" );

			if ( !finite ) return;
		}

		// And settled again afterwards: back where it started.
		Frames( 120, _ => bindRig );
		var drift = 0f;
		for ( var i = 0; i < count; i++ ) drift += (x[i] - settled.Positions[i]).Length;
		drift /= Math.Max( 1, count );
		LastDrift = drift;
		Note( $"runtime: after every pose and back, cloth is {drift:0.##} in from where it first settled (mean)" );
		Check( "runtime", "comes back to where it settled after the poses (mean < 1 in)", drift < 1f, $"{drift:0.##} in" );
	}

	/// <summary>GarmentCloth.ClosestOnTriangle: barycentric weights of the point on triangle abc nearest p (Ericson, Real-Time
	/// Collision Detection 5.1.5).</summary>
	static (float A, float B, float C) ClosestOnTriangle( Vec3 p, Vec3 a, Vec3 b, Vec3 c )
	{
		var ab = b - a;
		var ac = c - a;
		var ap = p - a;
		var d1 = Vec3.Dot( ab, ap );
		var d2 = Vec3.Dot( ac, ap );
		if ( d1 <= 0f && d2 <= 0f ) return (1f, 0f, 0f);
		var bp = p - b;
		var d3 = Vec3.Dot( ab, bp );
		var d4 = Vec3.Dot( ac, bp );
		if ( d3 >= 0f && d4 <= d3 ) return (0f, 1f, 0f);
		var vc = d1 * d4 - d3 * d2;
		if ( vc <= 0f && d1 >= 0f && d3 <= 0f ) { var v = d1 / (d1 - d3); return (1f - v, v, 0f); }
		var cp = p - c;
		var d5 = Vec3.Dot( ab, cp );
		var d6 = Vec3.Dot( ac, cp );
		if ( d6 >= 0f && d5 <= d6 ) return (0f, 0f, 1f);
		var vb = d5 * d2 - d1 * d6;
		if ( vb <= 0f && d2 >= 0f && d6 <= 0f ) { var w = d2 / (d2 - d6); return (1f - w, 0f, w); }
		var va = d3 * d6 - d5 * d4;
		if ( va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f ) { var w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return (0f, 1f - w, w); }
		var denom = 1f / (va + vb + vc);
		var vv = vb * denom;
		var ww = vc * denom;
		return (1f - vv - ww, vv, ww);
	}

	/// <summary>GarmentCloth.BuildAnchors.</summary>
	static (int Anchor, float Reach)[] BuildAnchors( float[] positions, (int A, int B, float Length, bool Bend)[] links, float[] pins )
	{
		var count = positions.Length / 3;
		var result = new (int, float)[count];
		Array.Fill( result, (-1, 0f) );

		if ( pins is null || !pins.Any( w => w >= 0.5f ) )
			return result;

		var adjacency = new List<(int To, float Length)>[count];
		for ( var i = 0; i < count; i++ ) adjacency[i] = new();

		foreach ( var (a, b, length, bend) in links )
		{
			if ( bend ) continue;
			adjacency[a].Add( (b, length) );
			adjacency[b].Add( (a, length) );
		}

		var dist = new float[count];
		Array.Fill( dist, float.MaxValue );
		var queue = new PriorityQueue<int, float>();

		for ( var i = 0; i < count; i++ )
			if ( pins[i] >= 0.5f ) { dist[i] = 0f; result[i] = (i, 0f); queue.Enqueue( i, 0f ); }

		while ( queue.TryDequeue( out var v, out var d ) )
		{
			if ( d > dist[v] ) continue;

			foreach ( var (to, length) in adjacency[v] )
			{
				var nd = d + length;
				if ( nd >= dist[to] ) continue;
				dist[to] = nd;
				result[to] = (result[v].Item1, 0f);
				queue.Enqueue( to, nd );
			}
		}

		for ( var i = 0; i < count; i++ )
		{
			if ( result[i].Item1 < 0 || result[i].Item1 == i )
			{
				result[i] = (-1, 0f);
				continue;
			}

			result[i] = (result[i].Item1, dist[i] * 1.05f);
		}

		return result;
	}

	/// <summary>GarmentCloth.BuildLinks.</summary>
	static (int A, int B, float Length, bool Bend)[] BuildLinks( float[] positions, int[] tris )
	{
		Vec3 P( int i ) => new( positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2] );

		var links = new List<(int, int, float, bool)>();
		var edges = new Dictionary<(int, int), int>();

		void Edge( int a, int b, int opposite )
		{
			var key = a < b ? (a, b) : (b, a);

			if ( edges.TryGetValue( key, out var other ) )
			{
				if ( other >= 0 && other != opposite )
				{
					links.Add( (other, opposite, (P( other ) - P( opposite )).Length, true) );
					edges[key] = -1;
				}

				return;
			}

			edges[key] = opposite;
			links.Add( (a, b, (P( a ) - P( b )).Length, false) );
		}

		for ( var t = 0; t + 2 < tris.Length; t += 3 )
		{
			Edge( tris[t], tris[t + 1], tris[t + 2] );
			Edge( tris[t + 1], tris[t + 2], tris[t] );
			Edge( tris[t + 2], tris[t], tris[t + 1] );
		}

		return links.ToArray();
	}

	static PolyMesh MeshAt( Vec3[] x, int[] tris )
	{
		var m = new PolyMesh();
		foreach ( var p in x ) m.AddVertex( p );
		for ( var t = 0; t + 2 < tris.Length; t += 3 ) m.AddFace( new[] { tris[t], tris[t + 1], tris[t + 2] } );
		return m;
	}

	// --- Publish: Make clothing --------------------------------------------------------------

	/// <summary>PublishClothing's compile (the rigged branch of CompileVmdl, garments only) and the
	/// .clothing written beside it.</summary>
	static void ClothingStage( PartStudio studio, string outDir, string file )
	{
		var (mesh, ranges) = studio.ToMeshWithBodies( b => b.IsGarment );
		Check( "clothing", "compile takes garments only, never the wearer",
			ranges.All( r => studio.Bodies.First( b => b.Id == r.BodyId ).IsGarment ) && ranges.Count > 0 );

		var weights = SkinBinder.BindBodies( mesh, ranges, new Dictionary<string, string>(), studio.Rig );
		weights = SkinBinder.SmoothWeights( mesh, weights );
		mesh.Skin = weights;

		var sums = Enumerable.Range( 0, weights.Count ).Select( i => weights[i].Sum( w => w.Weight ) ).ToArray();
		Check( "clothing", "compiled weights sum to 1", sums.All( s => MathF.Abs( s - 1f ) < 1e-3f ) );

		var dmx = DmxWriter.Write( mesh, studio.Rig, materialName: studio.NameForSlot, modelName: file );
		File.WriteAllText( Path.Combine( outDir, $"{file}.dmx" ), dmx );
		Check( "clothing", "DMX written", dmx.Length > 0, $"{dmx.Length / 1024} KB" );

		var garments = studio.Features.OfType<GarmentFeature>().Where( g => !g.Suppressed ).ToList();
		var recipe = ClothingDefinition.Combine( garments.Select( g => g.BuildRecipe() ) );
		var text = ClothingDefinition.Write( recipe, $"models/effigy/{file}.vmdl", garments[0].Name ?? recipe.Name );
		File.WriteAllText( Path.Combine( outDir, $"{file}.clothing" ), text );

		try
		{
			using var doc = JsonDocument.Parse( text );
			var root = doc.RootElement;
			Check( "clothing", ".clothing is valid JSON", true,
				$"{root.GetProperty( "Category" )}, slots {root.GetProperty( "SlotsUnder" )}, hides {root.GetProperty( "HideBody" )}" );
			Check( "clothing", ".clothing claims at least one slot", ClothingDefinition.SlotsFor( recipe ).Any() );
		}
		catch ( Exception e )
		{
			Check( "clothing", ".clothing is valid JSON", false, e.Message );
		}
	}

	// --- Save and reopen ---------------------------------------------------------------------

	static void RoundTripStage( PartStudio studio, string outDir, string file )
	{
		var path = Path.Combine( outDir, $"{file}.effigy" );
		var before = studio.Bodies.Where( b => b.IsGarment ).Sum( b => b.Mesh.VertexCount );

		StudioDocument.WriteFile( studio, path );
		ImportSidecar.Save( studio, path );

		var reopened = StudioDocument.ReadFile( path );
		ImportSidecar.Load( reopened, path );
		var report = reopened.Rebuild();
		var after = reopened.Bodies.Where( b => b.IsGarment ).Sum( b => b.Mesh.VertexCount );

		Check( "save", "reopens without errors", !report.HasErrors && reopened.ReadWarnings.Count == 0,
			string.Join( "; ", report.Errors.Select( e => e.Message ).Concat( reopened.ReadWarnings ) ) );
		Check( "save", "same garments after reopening", after == before, $"{before} -> {after} vertices" );
		Check( "save", "same rig after reopening", reopened.Rig.Count == studio.Rig.Count );
	}

	// --- report ------------------------------------------------------------------------------

	static void WriteReport( string path )
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine( "# Clothing workspace, headless" );
		sb.AppendLine();
		sb.AppendLine( $"{Findings.Count( f => f.Ok )} passed, {Findings.Count( f => !f.Ok )} failed." );
		sb.AppendLine();
		sb.AppendLine( "## Failures" );
		sb.AppendLine();

		foreach ( var f in Findings.Where( f => !f.Ok ) )
			sb.AppendLine( $"- **{f.Recipe}** [{f.Stage}] {f.What}{(f.Detail is null ? "" : $" — {f.Detail}")}" );

		sb.AppendLine();
		sb.AppendLine( "## Measurements" );
		sb.AppendLine();

		foreach ( var n in Notes )
			sb.AppendLine( $"- {n}" );

		File.WriteAllText( path, sb.ToString() );
	}
}
