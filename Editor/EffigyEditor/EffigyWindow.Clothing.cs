using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The Clothing workspace: bring a body in to dress, cut a garment from it, grow fur on it — and
/// the fur materials the Fur features need.
///
/// A WORKSPACE RATHER THAN A STRIP, since v-next. Clothing started as two buttons borrowed onto the
/// Model home bar and the Rig bar, which was the right size for two buttons and the wrong size for
/// the job: dressing a body is its own pass with its own order (wearer, garment, fur, fabric), and
/// a workflow that has an order wants a bar that states it. It also needed docks of its own —
/// Materials open, because a fabric is what you reach for the moment the garment fits.
///
/// NOTHING TO FINISH, exactly like Rig. Every tool here adds an ordinary feature, so the work is in
/// the tree the instant the button is pressed; a green tick that only changed which glyphs were on
/// screen would teach the wrong thing about what Finish means in this editor.
/// </summary>
public sealed partial class EffigyWindow
{
	/// <summary>The clothing stage set, built with the others in RebuildStages.</summary>
	private List<EffigyStage> _clothingStages;

	/// <summary>Which clothing stage was last looked at, so leaving and coming back lands where you
	/// were — the same courtesy _partStage does for CAD and _rigStage for Rig.</summary>
	private int _clothingStage;

	/// <summary>
	/// "Clothing" as a workspace. A plain mode change, for the reason EnterRig gives: there is no
	/// session to open and no feature to roll back to, only a different set of tools.
	/// </summary>
	private void EnterClothing()
	{
		if ( _viewport is null || _stageBar is null )
			return;

		LeaveCurrentWorkspace();

		// A modal feature dialog and the clothing bar would be two things claiming the model at
		// once — same argument EnterSculpt and EnterRig both make.
		_dialog?.Close();

		BarMode = EffigyBarMode.Clothing;

		_stageBar.Mode = "CLOTHING";
		_stageBar.SetFinish( null, null );
		_stageBar.SetStages( _clothingStages, _clothingStage );

		SetPrompt( HasWearableBody()
			? "Clothing: select the body to dress, then add a Garment."
			: "Clothing: press Wearer to load a rigged model to dress, or rig a body first." );
	}

	/// <summary>Is there anything here a garment could be cut from — a body that is not itself a
	/// garment, under a rig with bones? The prompt asks this and nothing else does.</summary>
	private bool HasWearableBody() => _studio is { Rig.Count: > 0 } && _studio.Features.Count > 0;

	private List<EffigyStage> BuildClothingStages() =>
		new() { BuildWearerStage(), BuildClothingStage(), BuildGarmentShapeStage(), BuildGarmentCheckStage(),
			BuildGarmentPublishStage() };

	/// <summary>
	/// Check: is this fit to publish?
	///
	/// THE STEP THAT IS MISSING FROM EVERY HOBBY PIPELINE. You can see a shirt is wrong when it is
	/// obviously wrong; you cannot see a shoulder poking a hundredth of an inch through a sleeve, or
	/// that the thing has no skin weights and will hang in the air when its wearer walks, until it is
	/// on a character in a game. That is the worst moment to find out, so the question gets asked
	/// here instead, before export.
	///
	/// IT CHECKS EVERYTHING, WITHOUT A SELECTION. "Are my clothes all right" is the question people
	/// actually have, and making them pick a garment first would turn one button into a chore for
	/// anybody wearing more than one thing. See GarmentCheck for what it reports and why almost none
	/// of it is phrased as a refusal.
	/// </summary>
	private EffigyStage BuildGarmentCheckStage()
	{
		var stage = new EffigyStage { Name = "Check" };

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.ProfileInspectorTool,
			Label = "Check",
			Tip = "Check every garment before you export it: does it clip through the body, has it got "
				+ "UVs and skin weights, how many triangles, how many openings. Reported in the console.",
			Clicked = CheckGarments,
		} );

		return stage;
	}

	/// <summary>Run GarmentCheck over every garment in the document and report it.</summary>
	private void CheckGarments()
	{
		if ( _studio is null )
			return;

		var garments = _studio.Bodies.Where( b => b.IsGarment ).ToList();

		if ( garments.Count == 0 )
		{
			SetPrompt( "Nothing to check yet - add a Garment first." );
			return;
		}

		// Everything that is not a garment is something the garment is worn OVER, wearer bodies
		// included. A garment is not checked against itself, and layered garments are deliberately
		// not checked against each other: inter-garment collision is not implemented, so reporting
		// a jacket as clipping the shirt underneath would be reporting a fault nothing can fix.
		var worn = _studio.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).ToList();

		var faults = 0;

		foreach ( var garment in garments )
		{
			var report = GarmentCheck.Run( garment.Mesh, worn );
			report.Name = garment.Name;

			if ( !report.Clean )
				faults++;

			foreach ( var line in report.Lines() )
				Log.Info( $"[Effigy] {garment.Name}: {line}" );
		}

		SetPrompt( faults == 0
			? $"Checked {garments.Count} garment{(garments.Count == 1 ? "" : "s")}: nothing wrong. Details in the console."
			: $"{faults} of {garments.Count} garments have something to fix - see the console." );
	}

	/// <summary>
	/// Publish: compile the garment and write the .clothing item that makes it wearable.
	///
	/// A .vmdl IS NOT CLOTHING. s&amp;box dresses a citizen from a Clothing resource that names the
	/// model and says what kind of garment it is, which slots it takes and which parts of the body it
	/// hides. Compile .vmdl stopped one step short of that, so a finished shirt could not be put on
	/// anybody. This is that step - see ClothingDefinition for where every field comes from.
	/// </summary>
	private EffigyStage BuildGarmentPublishStage()
	{
		var stage = new EffigyStage { Name = "Publish" };

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Wearer,
			Label = "Make clothing",
			Tip = "Compile the garments to a skinned .vmdl and write a .clothing item beside it - the "
				+ "asset a citizen can actually wear. Slots and hidden body parts come from the garment's "
				+ "own recipe. The wearer is never exported.",
			Clicked = PublishClothing,
		} );

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Fabric,
			Label = "Make live cloth",
			Tip = "Write the garment as live cloth - models/effigy/NAME.cloth.json - for the Garment Cloth "
				+ "component. Put that component on the character and the shirt hangs and swings with "
				+ "real cloth physics instead of riding the bones stiffly. Works in the editor without Play.",
			Clicked = PublishLiveCloth,
		} );

		return stage;
	}

	/// <summary>
	/// Write every garment in the document as one live-cloth file. See ClothExport.
	///
	/// NO COMPILE. Live cloth is not a model, so there is nothing for the asset system to build -
	/// the component reads the JSON directly, which is also why this works on a garment whose
	/// .vmdl has never been compiled.
	/// </summary>
	private void PublishLiveCloth()
	{
		if ( _studio is null )
			return;

		var garments = _studio.Bodies.Where( b => b.IsGarment ).ToList();

		if ( garments.Count == 0 )
		{
			SetPrompt( "Nothing to make live - add a Garment first." );
			return;
		}

		if ( _studio.Rig.Count == 0 )
		{
			SetPrompt( "Live cloth hangs on the wearer's bones - load a Wearer first." );
			return;
		}

		var mesh = new PolyMesh();
		var pins = new List<float>();
		ClothExport.Settings fabric = null;

		foreach ( var body in garments )
		{
			var offset = mesh.VertexCount;

			foreach ( var p in body.Mesh.Positions )
				mesh.AddVertex( p );

			foreach ( var face in body.Mesh.Faces )
				mesh.AddFace( face.Indices.Select( i => i + offset ).ToArray(), face.UVs, face.Material );

			// The feature that made it says what it is made of and where it is held on. One fabric
			// per file - the first garment's - because the component has one set of sliders; a
			// jacket over a shirt is two files and two components.
			var feature = _studio.Features.OfType<GarmentFeature>().FirstOrDefault( g => g.Id == body.FeatureId );
			fabric ??= feature is null ? null : ClothExport.FabricSettings( feature.FabricValue, feature.Stiffness.Clamped );
			pins.AddRange( ClothExport.PinTop( body.Mesh, feature?.LivePinFraction ?? 0.12f ) );
		}

		var map = new BodyRegions.Map( _studio.Rig );
		var worn = _studio.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).ToList();
		var capsules = BoneCapsules.Build( map, worn );

		if ( capsules.Count == 0 )
		{
			SetPrompt( "No bones to hang the cloth on - the wearer's skeleton has no torso or arms Effigy recognises." );
			return;
		}

		var slot = mesh.Faces.Select( f => f.Material ).DefaultIfEmpty( 0 ).First();
		var material = _studio.MaterialNames.TryGetValue( slot, out var bound ) ? bound : "";
		var name = ExportBaseName();

		if ( name is null )
			return;

		try
		{
			var folder = EffigyAssetFolder.ResolveAssetFolder( "models/effigy" );
			Directory.CreateDirectory( folder );

			var file = Path.Combine( folder, $"{name}.cloth.json" );
			File.WriteAllText( file, ClothExport.Write( mesh, capsules, _studio.Rig, material, fabric, pins.ToArray() ) );

			Log.Info( $"[Effigy] wrote {file} - {mesh.VertexCount} cloth vertices on {capsules.Count} capsules" );
			SetPrompt( $"models/effigy/{name}.cloth.json is ready - add a Garment Cloth component to the character and point it at this file." );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Effigy] could not write the live cloth for {name}: {e.Message}" );
		}
	}

	/// <summary>Compile, then write models/effigy/NAME.clothing pointing at the compiled model.</summary>
	private void PublishClothing()
	{
		if ( _studio is null )
			return;

		var garments = _studio.Features.OfType<GarmentFeature>().Where( g => !g.Suppressed ).ToList();

		if ( garments.Count == 0 )
		{
			SetPrompt( "Nothing to publish - add a Garment first." );
			return;
		}

		if ( _rigPanel is not { HasBones: true } )
		{
			SetPrompt( "Clothing needs a rig to move with its wearer - load a Wearer first." );
			return;
		}

		CompileVmdl();

		// Null unless the SKINNED compile got all the way through. A failed compile has already
		// said why in the console; a .clothing pointing at it would load and silently show nothing.
		if ( _lastSkinnedVmdl is not { } modelPath )
		{
			SetPrompt( "The model did not compile, so no clothing was written - see the console." );
			return;
		}

		var recipe = ClothingDefinition.Combine( garments.Select( g => g.BuildRecipe() ) );
		var name = Path.GetFileNameWithoutExtension( modelPath );
		var title = garments.Count == 1 ? garments[0].Name ?? recipe.Name : name;

		try
		{
			var folder = EffigyAssetFolder.ResolveAssetFolder( "models/effigy" );
			var file = Path.Combine( folder, $"{name}.clothing" );

			File.WriteAllText( file, ClothingDefinition.Write( recipe, modelPath, title ) );

			var asset = AssetSystem.FindByPath( $"models/effigy/{name}.clothing" )
				?? AssetSystem.RegisterFile( file );
			asset?.Compile( true );

			Log.Info( $"[Effigy] wrote {file} - {ClothingDefinition.CategoryFor( recipe )}, slots "
				+ $"{string.Join( ", ", ClothingDefinition.SlotsFor( recipe ) )}" );

			SetPrompt( $"models/effigy/{name}.clothing is ready - drag it onto a Dresser or a citizen to wear it." );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Effigy] could not write the .clothing for {name}: {e.Message}" );
			SetPrompt( "The model compiled, but the .clothing could not be written - see the console." );
		}
	}

	/// <summary>
	/// Shape: the step between "the garment exists" and "the garment is right".
	///
	/// ONE BUTTON INTO TOOLS THAT ALREADY EXIST. Shrinkwrap, Solidify, Drape and the fabric presets
	/// all live on Model > Edit's Surface and Cloth stages and have done since the mesh editor
	/// landed. They are exactly the fitting tools the clothing workflow calls for — and somebody who
	/// came here to make a shirt has no reason to suspect they are behind a different workspace, a
	/// different bar and a mode they have to know to open. So Clothing points at them rather than
	/// growing a second copy: press this with the garment selected and you are editing it, with
	/// Cloth one tab along.
	/// </summary>
	private EffigyStage BuildGarmentShapeStage()
	{
		var stage = new EffigyStage { Name = "Shape" };

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.MeshEdit,
			Label = "Shape garment",
			Tip = "Edit the selected garment by hand - move its vertices, shrinkwrap it to the body, "
				+ "solidify it, or pin part of it and let the rest drape. Saved as a Mesh edit in the history.",
			Clicked = AddMeshEdit,
		} );

		return stage;
	}

	/// <summary>
	/// The Wearer stage: get a body in here to dress.
	///
	/// THE STAGE CLOTHING WAS MISSING. A Garment is a recipe read off the rig — it finds the torso
	/// and the arms by bone name and lifts the cloth off the body's own surface — so it needs a
	/// RIGGED BODY in the document before it can do anything at all. Every way of having one ran
	/// through modelling it here, which meant the one thing people actually want from a clothing
	/// tool, making a jacket for a character that already exists, was the one thing it could not do.
	/// </summary>
	private EffigyStage BuildWearerStage()
	{
		var stage = new EffigyStage { Name = "Wearer" };

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Wearer,
			Label = "Wearer",
			Tip = "Load a rigged model to dress - Citizen, your own playermodel, anything compiled. "
				+ "Its surface comes in as a body to cut the garment from and its skeleton becomes this "
				+ "document's rig. It is never exported.",
			Clicked = PickWearer,
		} );

		return stage;
	}

	/// <summary>Ask for a model, then load it as the wearer. Cancelling adds nothing, the same way
	/// cancelling Import's file picker does.</summary>
	private void PickWearer()
	{
		if ( _studio is null )
			return;

		var picker = AssetPicker.Create( this, AssetType.Model, new AssetPicker.PickerOptions() );
		picker.Title = "Choose a body to dress";

		picker.OnAssetPicked = assets =>
		{
			if ( assets.FirstOrDefault() is { } asset )
				LoadWearer( asset.Path, asset.Name );
		};

		picker.Show();
	}

	/// <summary>
	/// Bring a compiled model in as the body to be dressed: its surface as a reference body, its
	/// skeleton as this document's rig.
	///
	/// THE MESH GOES THROUGH OBJ, which looks like a detour and is not. WearerFeature IS an
	/// ImportFeature, and every hard part of holding a borrowed mesh is already solved there - the
	/// triangles live in a side-car instead of bloating the .effigy, a reopen does not depend on the
	/// model still being installed, and a piece can be deleted without touching the source. Handing
	/// it OBJ bytes in memory reuses all of it and writes nothing to the project.
	/// </summary>
	private void LoadWearer( string path, string name )
	{
		if ( _studio is null )
			return;

		var model = Model.Load( path );

		if ( model is null || model.IsError )
		{
			SetPrompt( $"{name} did not load as a model." );
			return;
		}

		var mesh = WearerSurface( model );

		if ( mesh is null || mesh.FaceCount == 0 )
		{
			SetPrompt( $"{name} has no render mesh to dress - a garment needs a surface to be cut from." );
			return;
		}

		var feature = new WearerFeature { Name = name };
		feature.Model.Value = path;
		feature.LoadMesh( System.Text.Encoding.UTF8.GetBytes( ObjWriter.Write( mesh, name ) ) );

		var bones = InstallWearerRig( model, name );

		AddFeature( feature );

		SetPrompt( bones > 0
			? $"{name} is in, {bones} bones. Add a Garment to dress it."
			: $"{name} is in, but it has no skeleton - a garment needs bones to find the torso and arms." );
	}

	/// <summary>
	/// A compiled model's render surface as a PolyMesh.
	///
	/// GetVertices/GetIndices is the whole of it, and it is worth saying why the obvious alternative
	/// is wrong: Model.Physics is the mesh this codebase already reads (RigViewport's posing
	/// collision takes it), but a physics hull is a handful of convex lumps. You cannot cut a collar
	/// out of it. Dressing needs the surface you can see.
	///
	/// ONE SLOT, NO SEAMS PRESERVED. The wearer is measured against, not shipped, so per-drawcall
	/// material groups would be detail nothing downstream reads. Triangles are taken as triangles -
	/// GarmentFit works off vertex normals and the BVH, neither of which wants them merged.
	/// </summary>
	internal static PolyMesh WearerSurface( Model model )
	{
		var vertices = model.GetVertices();
		var indices = model.GetIndices();

		if ( vertices is null || indices is null || indices.Length < 3 )
			return null;

		var mesh = new PolyMesh();

		// WELDED BY POSITION. A render vertex buffer is split wherever the normal or the UV
		// changes, which on a hard-surface model is nearly every edge - Camhead came in as 21k
		// triangles that shared no corners at all. A garment lifted off that is 21k loose
		// scraps, the drape has nothing holding them together, and the shirt came out shattered.
		// The wearer is only measured against, so the seams the split kept are worth nothing here.
		var weld = new Dictionary<(long, long, long), int>();
		var remap = new int[vertices.Length];

		for ( var i = 0; i < vertices.Length; i++ )
		{
			var p = vertices[i].Position;
			var key = ((long)MathF.Round( p.x * 1000f ), (long)MathF.Round( p.y * 1000f ), (long)MathF.Round( p.z * 1000f ));

			if ( !weld.TryGetValue( key, out var index ) )
			{
				index = mesh.Positions.Count;
				weld[key] = index;
				mesh.Positions.Add( new Vec3( p.x, p.y, p.z ) );
			}

			remap[i] = index;
		}

		for ( var i = 0; i + 2 < indices.Length; i += 3 )
		{
			var a = indices[i] < remap.Length ? remap[indices[i]] : -1;
			var b = indices[i + 1] < remap.Length ? remap[indices[i + 1]] : -1;
			var c = indices[i + 2] < remap.Length ? remap[indices[i + 2]] : -1;

			if ( a < 0 || b < 0 || c < 0 )
				continue;

			// A degenerate triangle is not a refusal - compiled models carry them, and one dropped
			// face in a body nobody exports is not worth stopping a load for.
			if ( a == b || b == c || a == c )
				continue;

			if ( a >= mesh.Positions.Count || b >= mesh.Positions.Count || c >= mesh.Positions.Count )
				continue;

			mesh.Faces.Add( new Face( new[] { a, b, c } ) );
		}

		return mesh;
	}

	/// <summary>
	/// Put the model's skeleton on the document, and say how many bones landed.
	///
	/// NOT THROUGH THE FEATURE, deliberately. A rebuild re-runs every feature, and the rig is one
	/// skeleton shared by the whole document - a feature quietly rewriting it on every rebuild would
	/// be fighting the Rig workspace for ownership of the same data, and the last writer would win
	/// by accident. So this happens ONCE, here, when the wearer is loaded.
	///
	/// IT REFUSES TO OVERWRITE A RIG YOU BUILT. Bones placed by hand are work that undo cannot get
	/// back - the snapshot captures features and parameters, not the skeleton, the same way bone
	/// edits in the Rig workspace have never been undoable. Better to leave the rig alone and say so
	/// than to be right about the common case and destructive about the other one.
	/// </summary>
	private int InstallWearerRig( Model model, string name )
	{
		if ( _studio is null || model.BoneCount <= 0 )
			return 0;

		if ( _studio.Rig.Count > 0 )
		{
			Log.Info( $"[Effigy] {name}'s skeleton was left out - this document already has a rig with "
				+ $"{_studio.Rig.Count} bones. Delete those bones first if you want the model's own." );

			return 0;
		}

		var result = EffigySkeletonImport.FromModel( model );

		_studio.Rig.Bones.Clear();
		_studio.Rig.Bones.AddRange( result.Skeleton.Bones );

		foreach ( var note in result.Notes )
			Log.Info( $"[Effigy] wearer rig: {note}" );

		_rigPanel?.Refresh();

		return _studio.Rig.Count;
	}

	private EffigyStage BuildClothingStage()
	{
		var stage = new EffigyStage { Name = "Garment" };

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Shell,
			Label = "Garment",
			Tip = "Add a Garment - a T-shirt, trousers, beanie, gloves and more, cut from the body, fitted "
				+ "to it, draped and thickened. Needs a rig. Select the body to wear it first, or it goes on every body.",
			Clicked = () => AddFeature( NewFeature( ToolKind.Garment, -1 ) ),
		} );

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Frill,
			Label = "Trim",
			Tip = "Frills, ruffles, ribbons, pleats and fringe, hung off the garment's own openings - "
				+ "the hem, the collar, the cuffs. Add one per detail: a frill at the hem and a ribbon "
				+ "at the collar are two Trims. (Lace is a cut-out material, not geometry.)",
			Clicked = () => AddFeature( NewFeature( ToolKind.Trim, -1 ) ),
		} );

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Paint,
			Label = "Fur",
			Tip = "Add Fur - shell layers for s&box's fur shader. Pick faces first, or it covers every garment. "
				+ "Coverage > Trim the openings does collars, cuffs and hems.",
			Clicked = () => AddFeature( NewFeature( ToolKind.Fur, -1 ) ),
		} );

		return stage;
	}

	/// <summary>
	/// Write each Fur feature's material and bind it to the feature's slot. Run after every rebuild,
	/// and cheap when nothing changed: a file is only rewritten (and recompiled) when its content
	/// differs from what is on disk.
	/// </summary>
	private void SyncFurMaterials()
	{
		if ( _studio is null )
			return;

		foreach ( var fur in _studio.Features.OfType<FurFeature>() )
		{
			if ( fur.Suppressed || fur.ResolvedSlot < 0 || fur.Error is not null )
				continue;

			try
			{
				var folder = EffigyAssetFolder.ResolveAssetFolder( "models/effigy/fur" );
				Directory.CreateDirectory( folder );

				var tag = new string( fur.Id.Select( c => char.IsLetterOrDigit( c ) ? c : '_' ).ToArray() );
				var colour = FurMaterial.ParseHex( fur.Colour.Value, new Vec3( 0.54f, 0.43f, 0.33f ) );
				var rim = FurMaterial.ParseHex( fur.RimColour.Value, new Vec3( 0.23f, 0.2f, 0.18f ) );

				var relColor = $"models/effigy/fur/fur_{tag}_color.png";
				var relNoise = $"models/effigy/fur/fur_{tag}_noise.png";
				var relVmat = $"models/effigy/fur/fur_{tag}.vmat";

				var changed = false;
				changed |= WriteIfChanged( Path.Combine( folder, $"fur_{tag}_color.png" ),
					PngWriter.ToBytesRgba( FurMaterial.ColorRgba( colour ), 16, 16 ) );
				changed |= WriteIfChanged( Path.Combine( folder, $"fur_{tag}_noise.png" ),
					PngWriter.ToBytesRgba( FurMaterial.NoiseRgba( fur.Id.GetHashCode() & 0xffff, fur.Clump.Clamped ),
						FurMaterial.NoiseSize, FurMaterial.NoiseSize ) );
				changed |= WriteIfChanged( Path.Combine( folder, $"fur_{tag}.vmat" ),
					System.Text.Encoding.UTF8.GetBytes( FurMaterial.VmatSource( relColor, relNoise,
						fur.Density.Clamped, fur.DarkRoots.Clamped, rim, fur.Wind.Clamped ) ) );

				if ( changed )
				{
					foreach ( var rel in new[] { relColor, relNoise, relVmat } )
					{
						var asset = AssetSystem.FindByPath( rel )
							?? AssetSystem.RegisterFile( Path.Combine( folder, Path.GetFileName( rel ) ) );
						asset?.Compile( true );
					}

					Log.Info( $"[Effigy] fur material {relVmat} written for slot {fur.ResolvedSlot}" );
				}

				if ( !_studio.MaterialNames.TryGetValue( fur.ResolvedSlot, out var bound ) || bound != relVmat )
				{
					_studio.MaterialNames[fur.ResolvedSlot] = relVmat;
					_livePreview = null;
				}
			}
			catch ( Exception e )
			{
				Log.Warning( $"[Effigy] could not write the fur material for {fur.Name ?? fur.Id}: {e.Message}" );
			}
		}
	}

	static bool WriteIfChanged( string path, byte[] bytes )
	{
		if ( File.Exists( path ) && File.ReadAllBytes( path ).AsSpan().SequenceEqual( bytes ) )
			return false;

		File.WriteAllBytes( path, bytes );
		return true;
	}
}
