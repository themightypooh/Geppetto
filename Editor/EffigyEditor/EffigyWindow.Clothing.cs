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
		new() { BuildWearerStage(), BuildClothingStage(), BuildGarmentShapeStage(), BuildGarmentMaterialStage(),
			BuildGarmentTestStage(), BuildGarmentCheckStage(), BuildGarmentPublishStage() };

	/// <summary>
	/// Material: what the garment is made of, to look at.
	///
	/// ONE BUTTON, BECAUSE IT IS ONE DECISION. Painting is a workspace of its own and always will
	/// be; picking "denim, this blue" is not painting, it is the thing you do before deciding
	/// whether to paint at all, and most garments never need more. See FabricFeature.
	/// </summary>
	private EffigyStage BuildGarmentMaterialStage()
	{
		var stage = new EffigyStage { Name = "Material" };

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.FaceMaterial,
			Label = "Fabric",
			Tip = "Give the garment a fabric - jersey, denim, leather, satin, plaid and more - as a real "
				+ "material: a tiling weave with normal and roughness maps, and the Complex shader switch "
				+ "that kind of cloth needs. Select a garment first, or it goes on every garment.",
			Clicked = () => AddFeature( NewFeature( ToolKind.Fabric, -1 ) ),
		} );

		return stage;
	}

	/// <summary>
	/// Write each Fabric feature's maps and .vmat and bind them to its garments' slots. The same
	/// shape as SyncFurMaterials: after every rebuild, and only rewriting what changed.
	/// </summary>
	private void SyncFabricMaterials()
	{
		if ( _studio is null )
			return;

		foreach ( var fabric in _studio.Features.OfType<FabricFeature>() )
		{
			if ( fabric.Suppressed || fabric.ResolvedSlots.Count == 0 || fabric.Error is not null )
				continue;

			try
			{
				var folder = EffigyAssetFolder.ResolveAssetFolder( "models/effigy/fabric" );
				Directory.CreateDirectory( folder );

				var tag = new string( fabric.Id.Select( c => char.IsLetterOrDigit( c ) ? c : '_' ).ToArray() );
				var colour = FurMaterial.ParseHex( fabric.Colour.Value, new Vec3( 0.35f, 0.43f, 0.55f ) );
				var accent = FurMaterial.ParseHex( fabric.Accent.Value, new Vec3( 0.91f, 0.89f, 0.83f ) );

				var relColor = $"models/effigy/fabric/fabric_{tag}_color.png";
				var relNormal = $"models/effigy/fabric/fabric_{tag}_normal.png";
				var relRough = $"models/effigy/fabric/fabric_{tag}_rough.png";
				var relVmat = $"models/effigy/fabric/fabric_{tag}.vmat";

				// The maps are half a second of maths at 512; a recipe string over the settings says
				// whether that has to be paid again, so a rebuild for an unrelated slider does not.
				var recipe = $"{fabric.PresetName}|{fabric.Colour.Value}|{fabric.Accent.Value}|{fabric.Scale.Clamped:0.##}|{fabric.Wear.Clamped:0.###}";
				var recipeFile = Path.Combine( folder, $"fabric_{tag}.recipe" );
				var changed = false;

				if ( !File.Exists( recipeFile ) || File.ReadAllText( recipeFile ) != recipe
					|| !File.Exists( Path.Combine( folder, $"fabric_{tag}_color.png" ) ) )
				{
					var (color, normal, rough) = FabricMaterial.Maps( fabric.PresetName, colour, accent,
						fabric.Scale.Clamped, fabric.Wear.Clamped, fabric.Id.GetHashCode() & 0xffff );

					changed |= WriteIfChanged( Path.Combine( folder, $"fabric_{tag}_color.png" ),
						PngWriter.ToBytesRgba( color, FabricMaterial.Size, FabricMaterial.Size ) );
					changed |= WriteIfChanged( Path.Combine( folder, $"fabric_{tag}_normal.png" ),
						PngWriter.ToBytesRgba( normal, FabricMaterial.Size, FabricMaterial.Size ) );
					changed |= WriteIfChanged( Path.Combine( folder, $"fabric_{tag}_rough.png" ),
						PngWriter.ToBytesRgba( rough, FabricMaterial.Size, FabricMaterial.Size ) );
					File.WriteAllText( recipeFile, recipe );
				}

				changed |= WriteIfChanged( Path.Combine( folder, $"fabric_{tag}.vmat" ),
					System.Text.Encoding.UTF8.GetBytes( FabricMaterial.VmatSource( fabric.PresetName, relColor, relNormal, relRough ) ) );

				if ( changed )
				{
					foreach ( var rel in new[] { relColor, relNormal, relRough, relVmat } )
					{
						var asset = AssetSystem.FindByPath( rel )
							?? AssetSystem.RegisterFile( Path.Combine( folder, Path.GetFileName( rel ) ) );
						asset?.Compile( true );
					}

					Log.Info( $"[Effigy] fabric material {relVmat} written ({fabric.PresetName})" );
				}

				foreach ( var slot in fabric.ResolvedSlots )
				{
					if ( !_studio.MaterialNames.TryGetValue( slot, out var bound ) || bound != relVmat )
					{
						_studio.MaterialNames[slot] = relVmat;
						_livePreview = null;
					}
				}
			}
			catch ( Exception e )
			{
				Log.Warning( $"[Effigy] could not write the fabric material for {fabric.Name ?? fabric.Id}: {e.Message}" );
			}
		}
	}

	// --- Test: the garment in the poses that break it ---------------------------------------

	/// <summary>The rig's bind pose while a test pose is on it, and null otherwise. The document's
	/// rig is bent IN PLACE for the test, so this is what puts it back - see TestRelax.</summary>
	private Skeleton _testBind;

	/// <summary>Nearest-bone weights for bodies that carry none (a wearer read off a compiled
	/// model), keyed by body id, so eight poses do not bind the wearer eight times.</summary>
	private readonly Dictionary<string, (int Vertices, SkinWeights Weights)> _testWeights = new();

	/// <summary>
	/// Test: the garment with the arms up, bent over, sat down.
	///
	/// THE FIT IS ONLY TRUE FOR THE POSE IT WAS MADE IN. Check finds a shoulder that is through a
	/// sleeve now; it cannot find one that will be through it the first time the character reaches
	/// for something, and that is the one people ship. Every clothing tool has a pose library for
	/// this reason. Each button bends the rig into one extreme, deforms the wearer and the garment
	/// with it, and marks every vertex that ends up inside the body. Relax puts the rig back.
	/// See GarmentPoses for the poses.
	/// </summary>
	private EffigyStage BuildGarmentTestStage()
	{
		var stage = new EffigyStage { Name = "Test" };

		foreach ( var pose in GarmentPoses.All )
		{
			var captured = pose;

			stage.Add( new EffigyStageTool
			{
				Icon = EffigyIcon.Bone,
				Label = pose.Name,
				Tip = pose.Tip + " Clipping vertices are marked in red and counted in the prompt.",
				Clicked = () => TestPose( captured ),
			} );
		}

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.SoftRest,
			Label = "Relax",
			Tip = "Back to the bind pose, and the marks come off.",
			Clicked = () => TestRelax( true ),
		} );

		return stage;
	}

	/// <summary>The pose on the rig now, for the scrub to re-apply at another amount.</summary>
	private GarmentPoses.Pose _testPose;

	private void OnTestScrubbed( float amount )
	{
		if ( _testPose is not null )
			TestPose( _testPose, amount );
	}

	/// <summary>Bend the rig into a pose, show everything deformed by it, and mark what clips.</summary>
	private void TestPose( GarmentPoses.Pose pose, float amount = 1f )
	{
		if ( _studio is null || _viewport is null )
			return;

		if ( _studio.Rig.Count == 0 )
		{
			SetPrompt( "Nothing to pose - load a Wearer or rig the body first." );
			return;
		}

		if ( !_studio.Bodies.Any( b => b.IsGarment ) )
		{
			SetPrompt( "Nothing to test yet - add a Garment first." );
			return;
		}

		// Any pose before this one comes off first, so poses do not stack. The bind snapshot is
		// taken once, on the first pose, and kept until Relax.
		if ( _testBind is not null )
			RestoreTestBind();
		else
			_testBind = _studio.Rig.Clone();

		var turned = GarmentPoses.Apply( _studio.Rig, pose, amount );

		if ( turned == 0 )
		{
			SetPrompt( $"{pose.Name}: nothing moved - Effigy does not recognise the bones this pose bends "
				+ "(it reads names like arm_upper_L, spine_1, leg_upper_R)." );
			return;
		}

		_testPose = pose;
		_testBar?.Show( pose.Name, amount );

		var bind = new Xform[_testBind.Count];
		for ( var i = 0; i < bind.Length; i++ )
			bind[i] = _testBind.WorldBind( i );

		var posed = new Xform[_studio.Rig.Count];
		for ( var i = 0; i < posed.Length; i++ )
			posed[i] = _studio.Rig.WorldBind( i );

		var shown = new PolyMesh();
		var garments = new List<(string Name, PolyMesh Mesh)>();
		var worn = new List<PolyMesh>();

		foreach ( var body in _studio.Bodies )
		{
			if ( !body.Visible || _studio.HiddenBodyIds.Contains( body.Id ) )
				continue;

			var deformed = DeformForTest( body, bind, posed );

			if ( body.IsGarment )
				garments.Add( (body.Name, deformed) );
			else
				worn.Add( deformed );

			MeshTransform.Append( shown, deformed );
		}

		_viewport.SetModel( BuildPreview( shown ), frameCamera: false );

		var (clipping, deepest) = MarkClipping( garments, worn, $"{pose.Name} {amount:P0}" );

		SetPrompt( clipping == 0
			? $"{pose.Name}: nothing clips. Scrub the bar to see where it starts; try the others, then Relax."
			: $"{pose.Name}: {clipping} {(clipping == 1 ? "vertex clips" : "vertices clip")} through the body, deepest "
				+ $"{deepest:0.##} in - marked yellow to red. Push out on Check fixes it, or more Looseness there." );
	}

	/// <summary>
	/// Check every garment against what it is worn over and mark what clips in the viewport,
	/// yellow for a graze and red for the deepest. Returns the count and the depth for the prompt;
	/// the per-garment detail goes to the console. Shared by Test and Check: the picture is the
	/// same, only the pose differs.
	/// </summary>
	private (int Clipping, float Deepest) MarkClipping( List<(string Name, PolyMesh Mesh)> garments, List<PolyMesh> worn, string label )
	{
		var marks = new List<(Vector3, float)>();
		var clipping = 0;
		var deepest = 0f;
		var reports = new List<(string Name, GarmentReport Report)>();

		foreach ( var (name, mesh) in garments )
		{
			var report = GarmentCheck.Run( mesh, worn );
			reports.Add( (name, report) );
			clipping += report.Clipping;
			deepest = Math.Max( deepest, report.DeepestClip );
		}

		// Heat is depth against the worst in this check, so the picture always has a red end
		// to read from, and a garment that grazes everywhere is not painted all-red for it.
		var scale = deepest > 1e-4f ? 1f / deepest : 0f;

		foreach ( var (name, report) in reports )
		{
			for ( var i = 0; i < report.ClipPoints.Count; i++ )
			{
				var p = report.ClipPoints[i];
				marks.Add( (new Vector3( p.x, p.y, p.z ), report.ClipDepths[i] * scale) );
			}

			if ( report.Clipping > 0 )
				Log.Info( $"[Effigy] {label}: {name} - {report.Clipping} vertices clip, deepest {report.DeepestClip:0.##} in" );
		}

		_viewport.Markers = marks;
		_viewport.Update();

		return (clipping, deepest);
	}

	/// <summary>A body deformed from the bind pose to the posed one. Its own weights when it has
	/// them; a wearer read off a compiled model has none Effigy can read, so it gets a nearest-bone
	/// bind, which is right enough to show where a shoulder goes.</summary>
	private PolyMesh DeformForTest( Body body, Xform[] bind, Xform[] posed )
	{
		var mesh = body.Mesh;
		SkinWeights weights;

		if ( mesh.IsRigged )
			weights = mesh.Skin;
		else
		{
			if ( !_testWeights.TryGetValue( body.Id, out var cached ) || cached.Vertices != mesh.VertexCount )
			{
				cached = (mesh.VertexCount, SkinBinder.BindSmooth( mesh, _testBind ));
				_testWeights[body.Id] = cached;
			}

			weights = cached.Weights;
		}

		if ( weights is null || weights.Count != mesh.VertexCount )
			return mesh;

		var result = mesh.Clone();
		var moved = SkinBinder.Deform( mesh.Positions, weights, bind, posed );

		for ( var i = 0; i < moved.Length; i++ )
			result.Positions[i] = moved[i];

		return result;
	}

	private void RestoreTestBind()
	{
		if ( _testBind is null || _studio is null )
			return;

		for ( var i = 0; i < _studio.Rig.Count; i++ )
		{
			var at = _testBind.IndexOf( _studio.Rig.Bones[i].Name );

			if ( at < 0 )
				continue;

			_studio.Rig.Bones[i].Local = _testBind.Bones[at].Local;
			_studio.Rig.Bones[i].Length = _testBind.Bones[at].Length;
		}
	}

	/// <summary>
	/// Put the rig back and take the marks off. Called by Relax, and by anything that is about to
	/// rebuild or save: the rig is bent in place, and a garment cut on a bent rig, or a document
	/// saved with one, would be the pose baked in.
	/// </summary>
	private void TestRelax( bool refresh )
	{
		if ( _testBind is null )
		{
			if ( refresh && _viewport is not null && _viewport.Markers.Count > 0 )
			{
				_viewport.Markers = Array.Empty<(Vector3, float)>();
				_viewport.Update();
			}

			return;
		}

		RestoreTestBind();
		_testBind = null;
		_testPose = null;

		if ( _testBar is not null )
			_testBar.Visible = false;

		if ( _viewport is not null )
		{
			_viewport.Markers = Array.Empty<(Vector3, float)>();
			_viewport.Update();
		}

		if ( refresh )
		{
			RefreshPreview();
			SetPrompt( "Relaxed - back to the bind pose." );
		}
	}

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
				+ "UVs and skin weights, how many triangles, how many openings. Clipping is marked on the "
				+ "model, yellow to red by depth; the rest is in the console.",
			Clicked = CheckGarments,
		} );

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Shrinkwrap,
			Label = "Push out",
			Tip = "Fix the clipping the last Check found: each garment that clips gets enough more "
				+ "Clearance to clear its deepest point, and is rebuilt. One undo step.",
			Clicked = PushOutClipping,
		} );

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.SoftRest,
			Label = "Clear marks",
			Tip = "Take the clipping marks off the model.",
			Clicked = () => TestRelax( true ),
		} );

		return stage;
	}

	/// <summary>What the last Check found clipping, per garment feature, for Push out.</summary>
	private readonly Dictionary<string, float> _lastClipDepth = new();

	/// <summary>
	/// Push out: the one-click answer to a red mark.
	///
	/// A mark says where; it does not say what to do, and "more Clearance" is a slider three
	/// panels away. So this reads the depths the last Check measured and gives each clipping
	/// garment exactly that much more room, plus a hair, then rebuilds. It is the fix a person
	/// would make, made for them, and it is one undo step because that is what it would be by hand.
	/// </summary>
	private void PushOutClipping()
	{
		if ( _studio is null )
			return;

		if ( _lastClipDepth.Count == 0 )
		{
			SetPrompt( "Nothing to push out - run Check first, or the last Check found no clipping." );
			return;
		}

		var targets = _lastClipDepth
			.Select( kv => (Garment: _studio.Features.FirstOrDefault( f => f.Id == kv.Key ) as GarmentFeature, Depth: kv.Value) )
			.Where( t => t.Garment is not null )
			.ToList();

		_lastClipDepth.Clear();

		if ( targets.Count == 0 )
		{
			SetPrompt( "The garments that clipped are no longer in the document." );
			return;
		}

		// The snapshot goes on the stack BEFORE the change, the way every other edit here does.
		RecordUndo();

		foreach ( var (garment, depth) in targets )
		{
			garment.Clearance.Value = MathF.Round( garment.Clearance.Clamped + depth + 0.05f, 2 );
			garment.Clearance.Expr = null;
			_studio.MarkDirty( garment );
		}

		RebuildStudio();
		CheckGarments();
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
		var marks = new List<(Vector3, float)>();
		var deepest = 0f;
		var reports = new List<(Body Body, GarmentReport Report)>();

		_lastClipDepth.Clear();

		foreach ( var garment in garments )
		{
			var report = GarmentCheck.Run( garment.Mesh, worn );
			report.Name = garment.Name;
			reports.Add( (garment, report) );
			deepest = Math.Max( deepest, report.DeepestClip );

			if ( !report.Clean )
				faults++;

			if ( report.Clipping > 0 && garment.FeatureId is { } id )
				_lastClipDepth[id] = Math.Max( _lastClipDepth.GetValueOrDefault( id ), report.DeepestClip );

			foreach ( var line in report.Lines() )
				Log.Info( $"[Effigy] {garment.Name}: {line}" );
		}

		// THE ANSWER IS ON THE MODEL, not only in the console: a count says how much, a mark says
		// where, and where is the thing you need to know to fix it. Heat is depth against the
		// worst found, so there is always a red end to read the scale from.
		var scale = deepest > 1e-4f ? 1f / deepest : 0f;

		foreach ( var (_, report) in reports )
			for ( var i = 0; i < report.ClipPoints.Count; i++ )
			{
				var p = report.ClipPoints[i];
				marks.Add( (new Vector3( p.x, p.y, p.z ), report.ClipDepths[i] * scale) );
			}

		if ( _viewport is not null )
		{
			_viewport.Markers = marks;
			_viewport.Update();
		}

		var clipping = reports.Sum( r => r.Report.Clipping );

		SetPrompt( faults == 0
			? $"Checked {garments.Count} garment{(garments.Count == 1 ? "" : "s")}: nothing wrong. Details in the console."
			: clipping > 0
				? $"{clipping} vertices clip, deepest {deepest:0.##} in - marked yellow to red. Push out fixes it; the rest is in the console."
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

			var rel = $"models/effigy/{name}.cloth.json";
			SetPrompt( WearLiveCloth( rel ) ?? $"{rel} is ready - add a Garment Cloth component to the character and point it at this file." );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Effigy] could not write the live cloth for {name}: {e.Message}" );
		}
	}

	// --- wearing it, in the open scene ------------------------------------------------------

	/// <summary>
	/// The renderer in the open scene that is the wearer, or null with a reason.
	///
	/// PUBLISH ENDED WITH HOMEWORK. "Add a Garment Cloth component and point it at this file" is a
	/// sentence that assumes you know what a component is, where the file went and which of the
	/// forty objects in the scene is the character - and the whole point of the workspace is that
	/// you do not have to. So Publish finishes the job when it can tell where the job is: the
	/// wearer is the character whose model this document was loaded from, and if it is standing
	/// in the open scene, the garment goes on it. When it cannot tell, it says what it wanted.
	/// </summary>
	private SkinnedModelRenderer FindSceneWearer( out string why )
	{
		why = null;

		var session = SceneEditorSession.Active;

		if ( session?.Scene is not { } scene )
		{
			why = "no scene is open";
			return null;
		}

		var renderers = scene.GetAllComponents<SkinnedModelRenderer>()
			.Where( r => r.IsValid() && r.Model is not null && !r.GameObject.Flags.HasFlag( GameObjectFlags.NotSaved ) )
			.ToList();

		if ( renderers.Count == 0 )
		{
			why = "there is no character in the open scene";
			return null;
		}

		// The model the document was dressed on, from its Wearer. Matching that is the only
		// answer that is right for certain.
		var wanted = _studio?.Features.OfType<WearerFeature>()
			.Select( w => w.Model.Value )
			.FirstOrDefault( v => !string.IsNullOrWhiteSpace( v ) );

		static string Norm( string path ) => (path ?? "").Replace( '\\', '/' ).Trim().ToLowerInvariant();

		if ( !string.IsNullOrEmpty( wanted ) )
		{
			var want = Norm( wanted );
			var matches = renderers.Where( r => Norm( r.Model.ResourcePath ) == want || Norm( r.Model.Name ) == want ).ToList();

			if ( matches.Count == 1 )
				return matches[0];

			if ( matches.Count > 1 )
			{
				// Several of the same character: the selected one, if one of them is selected.
				var selected = matches.FirstOrDefault( r => EditorScene.Selection.Contains( r.GameObject ) );

				if ( selected is not null )
					return selected;

				why = $"{matches.Count} objects in the scene wear {wanted} - select the one to dress";
				return null;
			}
		}

		// No Wearer, or it is not in the scene: the selected character, or the only one.
		var picked = renderers.FirstOrDefault( r => EditorScene.Selection.Contains( r.GameObject ) );

		if ( picked is not null )
			return picked;

		if ( renderers.Count == 1 )
			return renderers[0];

		why = string.IsNullOrEmpty( wanted )
			? "the scene has several characters - select the one to dress"
			: $"{wanted} is not in the open scene - select the character to dress";
		return null;
	}

	/// <summary>Put a Garment Cloth on the scene's wearer, pointed at the file. Returns what
	/// happened, or null when it could not, so the caller can fall back to telling the user what
	/// to do by hand.</summary>
	private string WearLiveCloth( string clothPath )
	{
		var wearer = FindSceneWearer( out var why );

		if ( wearer is null )
		{
			Log.Info( $"[Effigy] {clothPath} not put on a character: {why}." );
			return null;
		}

		var session = SceneEditorSession.Active;

		using ( session.UndoScope( "Wear garment" ).WithComponentCreations().WithComponentChanges( wearer ).Push() )
		{
			// The same garment again replaces itself; a different one is a second component,
			// because a jacket over a shirt is two cloths.
			var cloth = wearer.GameObject.Components.GetAll<GarmentCloth>( FindMode.EverythingInSelf )
				.FirstOrDefault( c => string.Equals( c.Cloth, clothPath, StringComparison.OrdinalIgnoreCase ) )
				?? wearer.GameObject.Components.Create<GarmentCloth>();

			cloth.Wearer = wearer;
			cloth.Cloth = clothPath;
		}

		EditorScene.Selection.Set( wearer.GameObject );

		return $"{clothPath} is on {wearer.GameObject.Name} in the scene - it is hanging there now. Ctrl+Z takes it off.";
	}

	/// <summary>Put a .clothing item on the scene's wearer's Dresser. Only a citizen has a
	/// Dresser, and a Dresser only fits citizen's body, so anything else falls back to the
	/// drag-it-on prompt.</summary>
	private string WearClothing( string clothingPath )
	{
		var wearer = FindSceneWearer( out _ );
		var dresser = wearer?.GameObject.Components.Get<Dresser>( FindMode.EverythingInSelfAndParent );

		if ( wearer is null || dresser is null )
			return null;

		var clothing = ResourceLibrary.Get<Clothing>( clothingPath );

		if ( clothing is null )
			return null;

		var session = SceneEditorSession.Active;

		using ( session.UndoScope( "Wear clothing" ).WithComponentChanges( dresser ).Push() )
		{
			dresser.Source = Dresser.ClothingSource.Manual;
			dresser.Clothing ??= new List<ClothingContainer.ClothingEntry>();

			if ( dresser.Clothing.All( e => e.Clothing != clothing ) )
				dresser.Clothing.Add( new ClothingContainer.ClothingEntry( clothing ) );
		}

		return $"{clothingPath} is on {wearer.GameObject.Name}'s Dresser in the scene. Ctrl+Z takes it off.";
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

			SetPrompt( WearClothing( $"models/effigy/{name}.clothing" )
				?? $"models/effigy/{name}.clothing is ready - drag it onto a Dresser or a citizen to wear it." );
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
