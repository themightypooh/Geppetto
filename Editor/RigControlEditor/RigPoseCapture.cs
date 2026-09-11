using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// Takes a pose the model's own animation graph is holding and writes it into a .riganim as
/// keyframes — the bridge from "the engine already knows how to stand/sit/aim like this" to
/// "now animate away from it by hand".
///
///   rig_capture_pose &lt;model&gt; &lt;output.riganim&gt; [params] [props] [frame]
///
/// The case it was written for is sitting. s&box's BaseChair and SitMoveMode drive citizen's
/// graph into a sit, and that pose is a good hundred numbers nobody wants to author twice — but
/// there is no way to get at it from Marionette, which only ever poses bones by hand. So this
/// runs the graph in a scratch scene, waits for the blend to finish, and reads the result off the
/// skeleton in exactly the parent-space form a keyframe stores.
///
/// WHY IT DOES NOT READ THE LIVE SCENE. Sampling the editor's own scene would mean pressing play
/// in it, and a scene that has been played is not the scene that was saved. A scratch scene has
/// no such cost and gets an identical answer: the graph is deterministic given its parameters.
///
/// WHAT COMES OUT IS FK, NOT A REFERENCE TO THE CLIP. The keyframes are baked. Change citizen's
/// sit animation later and this clip keeps the pose it captured, which is the point — a starting
/// pose that moves under you is worse than no starting pose.
///
/// Examples, from the editor console:
///
///   rig_capture_pose models/effigy/gearhead_citizen.vmdl animations/gearhead_sit.riganim "sit=1"
///
///   rig_capture_pose models/effigy/gearhead_citizen.vmdl animations/gearhead_sit.riganim "sit=1" \
///       "models/effigy/broadcast_chair.vmdl;models/effigy/broadcast_desk.vmdl@15,0,0"
///
/// The second adds the chair and the desk as reference props, placed where the scene puts them
/// relative to his feet, so the clip opens with something to aim his hands at rather than empty
/// space.
/// </summary>
internal static class RigPoseCapture
{
	/// <summary>How long to let the graph run before reading the pose, in ticks at 60hz — three
	/// seconds. Generous: ticks are cheap, and the cost of stopping one short is a pose captured
	/// mid-blend, which looks plausible and is wrong everywhere.</summary>
	private const int SettleTicks = 180;

	/// <summary>How many ticks to give the renderer to build its bone array before giving up. It
	/// is usually one; how many it really takes is an engine detail and clearly differs per model
	/// (see RigWaveBuilder.WaitForBones).</summary>
	private const int MaxSetupTicks = 64;

	/// <summary>How still the skeleton has to be, in inches of total movement across every bone
	/// in one tick, to stop waiting early.
	///
	/// EARLY EXIT, NOT A REQUIREMENT. A living idle never goes still — citizen's graph breathes
	/// and sways under every pose it holds, including the sit — so waiting for stillness waits
	/// for ever. This only saves time on a graph that does hold perfectly still.</summary>
	private const float SettleEpsilon = 0.002f;

	/// <summary>Consecutive still ticks needed to take that early exit. More than one because a
	/// blend can pass through a momentary standstill at the top of an ease.</summary>
	private const int StillTicks = 10;

	// CONSOLE ONLY, NO [Menu] — same reasoning as RigSampleBuilder.Build. It takes a model path
	// and a parameter string, which is not a thing a menu item can ask for, and a menu entry that
	// opens a dialog to collect them is a tool rather than the one-liner this is.
	[ConCmd( "rig_capture_pose" )]
	public static void Capture( string modelPath, string output, string parameters = "",
		string props = "", int frame = 0 )
	{
		if ( string.IsNullOrWhiteSpace( modelPath ) || string.IsNullOrWhiteSpace( output ) )
		{
			Log.Error( "[capture] usage: rig_capture_pose <model> <output.riganim> [params] [props] [frame]" );
			return;
		}

		var model = Model.Load( modelPath );

		if ( model?.Bones is null )
		{
			Log.Error( $"[capture] couldn't load {modelPath}" );
			return;
		}

		if ( frame < 0 )
		{
			Log.Error( $"[capture] frame {frame} is before the start of the clip" );
			return;
		}

		var scene = Scene.CreateEditorScene();

		try
		{
			using var scope = scene.Push();

			var renderer = new GameObject( true, "capture" ).GetOrAddComponent<SkinnedModelRenderer>( false );
			renderer.Model = model;

			// The whole point: the graph is what holds the pose. RigSampleBuilder and
			// RigWaveBuilder both turn this OFF, because they author poses themselves and a graph
			// would fight them for the skeleton. Here it is the source.
			renderer.UseAnimGraph = true;
			renderer.Enabled = true;

			var wanted = ParseParameters( parameters );

			if ( !Settle( scene, renderer, model, wanted, out var world ) )
			{
				Log.Error( $"[capture] no bone of {modelPath} ever read back a pose, after " +
					$"{MaxSetupTicks} ticks. The model loaded, so this is the renderer never " +
					$"building its bone array rather than a naming problem — and it reads from " +
					$"the outside as every bone sitting at the origin." );
				return;
			}

			Landmarks( world );

			var doc = Open( output, model, out var asset );

			if ( doc is null )
				return;

			// Parent space, not world: that is the form a keyframe stores, and the conversion is
			// the same one RigViewport.TryGetLocalTransform does when you key a bone by hand.
			// Skipped for a bone whose parent didn't read back, rather than falling back to the
			// model's transform — a root's parent IS the model, but a child's silently is not,
			// and that mistake puts a limb across the room.
			var captured = 0;

			foreach ( var bone in model.Bones.AllBones )
			{
				if ( !world.TryGetValue( bone.Name, out var boneWorld ) )
					continue;

				var parentWorld = bone.Parent is { } parent
					? world.TryGetValue( parent.Name, out var p ) ? p : (Transform?)null
					: renderer.WorldTransform;

				if ( parentWorld is not { } into )
				{
					Log.Warning( $"[capture] skipped {bone.Name}: its parent {bone.Parent?.Name} never read back" );
					continue;
				}

				doc.GetOrAddTrack( bone.Name ).SetKeyframe( frame, into.ToLocal( boneWorld ) );
				captured++;
			}

			// A clip whose FrameCount is behind the frame just keyed has a keyframe past the end
			// of its own timeline, which reads in the editor as the capture having done nothing.
			if ( doc.FrameCount <= frame )
				doc.FrameCount = frame + 1;

			foreach ( var prop in ParseProps( props ) )
			{
				// Replaced by name rather than appended, so re-running the command with a moved
				// desk moves the desk instead of stacking a second one on top of the first.
				doc.ReferenceProps.RemoveAll( p => p?.Name == prop.Name );
				doc.ReferenceProps.Add( prop );
			}

			asset.SaveToDisk( doc );

			Log.Info( $"[capture] wrote {output} — {captured} bones keyed at frame {frame}" +
				( wanted.Count > 0 ? $", graph at {string.Join( ", ", wanted.Select( p => $"{p.Key}={p.Value}" ) )}" : "" ) +
				( doc.ReferenceProps.Count > 0 ? $", {doc.ReferenceProps.Count} reference props" : "" ) );
		}
		catch ( Exception e )
		{
			Log.Error( $"[capture] threw: {e}" );
		}
		finally
		{
			scene.Destroy();
		}
	}

	/// <summary>
	/// Let the graph run itself in, and hand back the world pose it arrives at.
	///
	/// A GRAPH BLENDS, so this cannot sample on the frame the parameter is set. Ask citizen for a
	/// sit and it takes the better part of a second to get there; sample early and you get a
	/// real, readable, completely wrong pose — the standing one, or a frame somewhere between.
	/// That is the same trap BroadcastChair.Measure hit when it measured the fit on the frame of
	/// the press, and it is the failure this method exists to avoid.
	///
	/// AND A GRAPH NEVER FULLY STOPS. The first version of this waited for the skeleton to go
	/// still and gave up after four hundred ticks having never seen it, because citizen's idle
	/// breathes and sways under every pose it holds — the sit included. So the wait is a fixed
	/// three seconds, long past any blend, with an early exit for the rare graph that does hold
	/// perfectly still. Whatever residual sway is left gets logged rather than hidden: a pose
	/// captured on an inhale is a fine starting pose, but you should be told that is what it is.
	/// </summary>
	private static bool Settle( Scene scene, SkinnedModelRenderer renderer, Model model,
		Dictionary<string, string> parameters, out Dictionary<string, Transform> world )
	{
		world = new Dictionary<string, Transform>();

		var previous = new Dictionary<string, Vector3>();
		var still = 0;
		var setup = 0;
		var moved = 0f;
		var readable = 0;

		for ( var tick = 0; tick < MaxSetupTicks + SettleTicks; tick++ )
		{
			// Re-applied every tick rather than once at the start. Some graph parameters are
			// declared auto-reset, and a parameter set before the graph has finished its own
			// setup is a parameter set on nothing.
			Apply( renderer, parameters );

			scene.EditorTick( ( tick + 1 ) / 60f, 1f / 60f );

			var current = new Dictionary<string, Transform>();
			moved = 0f;
			readable = 0;

			foreach ( var bone in model.Bones.AllBones )
			{
				if ( !renderer.TryGetBoneTransform( bone, out var boneWorld ) )
					continue;

				current[bone.Name] = boneWorld;
				readable++;

				if ( previous.TryGetValue( bone.Name, out var was ) )
					moved += ( boneWorld.Position - was ).Length;

				previous[bone.Name] = boneWorld.Position;
			}

			// Every bone at the origin is what an unbuilt bone array looks like, and it is
			// perfectly still — so the setup wait cannot be "did anything read back", it has to
			// be "did anything read back somewhere other than the origin".
			var spread = current.Count > 0 ? current.Values.Max( t => t.Position.Length ) : 0f;

			if ( readable == 0 || spread < 0.001f )
			{
				if ( ++setup >= MaxSetupTicks )
					return false;

				still = 0;
				continue;
			}

			world = current;

			// Only count stillness once the blend has had time to be over, or a graph that
			// happens to start where it is going exits on its first tick.
			if ( tick > 30 && moved < SettleEpsilon && ++still >= StillTicks )
			{
				Log.Info( $"[capture] the graph went still after {tick + 1} ticks, " +
					$"{readable} of {model.Bones.AllBones.Count()} bones readable" );
				return true;
			}

			if ( moved >= SettleEpsilon )
				still = 0;
		}

		Log.Info( $"[capture] ran the graph {MaxSetupTicks + SettleTicks} ticks, {readable} of " +
			$"{model.Bones.AllBones.Count()} bones readable. Still drifting {moved:0.000}in per " +
			$"tick across the whole skeleton when sampled — that is the idle breathing, not an " +
			$"unfinished blend, unless it is a large number." );

		return readable > 0;
	}

	/// <summary>
	/// The few heights worth knowing about the pose that was just taken, in the model's own
	/// space — which is the space a reference prop's offset is in.
	///
	/// Here because a captured sit is the one pose whose props have to be placed against the
	/// FIGURE rather than against the floor. A chair placed at z 0 with its seat at 18.4 is
	/// correct as furniture and wrong under this pose unless the pose happens to seat him at
	/// 18.4 too, and the gap is a couple of inches of him sunk into the cushion — small enough
	/// to look like bad weighting and be chased for an hour. These numbers make it arithmetic.
	/// </summary>
	private static void Landmarks( Dictionary<string, Transform> world )
	{
		if ( world.Count == 0 )
			return;

		var lowest = world.Values.Min( t => t.Position.z );
		var highest = world.Values.Max( t => t.Position.z );

		var hips = world.FirstOrDefault( b =>
			b.Key.Equals( "pelvis", StringComparison.OrdinalIgnoreCase )
			|| b.Key.Equals( "hips", StringComparison.OrdinalIgnoreCase ) );

		Log.Info( $"[capture] pose spans z {lowest:0.0} to {highest:0.0}" +
			( hips.Key is null ? "" : $", {hips.Key} at z {hips.Value.Position.z:0.0}, " +
				$"x {hips.Value.Position.x:0.0} — a seat belongs a little under that" ) );

		// The bones nearest the floor, named. On a sit that is the feet and the thighs, and the
		// thigh root is the closest thing the skeleton has to "where the cushion is" — a seat
		// height read off the pose rather than assumed from the chair.
		var low = world.OrderBy( b => b.Value.Position.z ).Take( 6 )
			.Select( b => $"{b.Key} {b.Value.Position.z:0.0}" );

		Log.Info( "[capture] lowest bones: " + string.Join( ", ", low ) );
	}

	/// <summary>Push the requested graph parameters at the renderer, typed by what they look
	/// like: true/false is a bool, a whole number an int, anything else a float. An animgraph
	/// enum — "sit" is one — takes its index as an int.</summary>
	private static void Apply( SkinnedModelRenderer renderer, Dictionary<string, string> parameters )
	{
		foreach ( var (name, value) in parameters )
		{
			if ( bool.TryParse( value, out var flag ) )
				renderer.Set( name, flag );
			else if ( int.TryParse( value, out var whole ) )
				renderer.Set( name, whole );
			else if ( float.TryParse( value, out var number ) )
				renderer.Set( name, number );
			else
				Log.Warning( $"[capture] don't know what type \"{name}={value}\" is - skipped" );
		}
	}

	/// <summary>"sit=1,sit_offset_height=0" - comma separated, because a console command's
	/// argument is one string and spaces would end it.</summary>
	private static Dictionary<string, string> ParseParameters( string text )
	{
		var parsed = new Dictionary<string, string>();

		if ( string.IsNullOrWhiteSpace( text ) )
			return parsed;

		foreach ( var pair in text.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var split = pair.Split( '=', 2 );

			if ( split.Length != 2 )
			{
				Log.Warning( $"[capture] \"{pair}\" isn't name=value - skipped" );
				continue;
			}

			parsed[split[0].Trim()] = split[1].Trim();
		}

		return parsed;
	}

	/// <summary>
	/// "path.vmdl;path.vmdl@x,y,z;path.vmdl@x,y,z@pitch,yaw,roll" - semicolons between props,
	/// because commas are already spoken for by the vectors inside one.
	///
	/// The offsets are relative to the MODEL'S ORIGIN, which for a playermodel is between his
	/// feet on the floor. That is the same frame the scene places the furniture in relative to
	/// him, so the numbers can be copied straight across rather than re-derived.
	/// </summary>
	private static IEnumerable<ReferenceProp> ParseProps( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			yield break;

		foreach ( var entry in text.Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var fields = entry.Split( '@' );
			var path = fields[0].Trim();
			var model = Model.Load( path );

			if ( model is null )
			{
				Log.Warning( $"[capture] couldn't load prop {path} - skipped" );
				continue;
			}

			var prop = new ReferenceProp
			{
				Name = System.IO.Path.GetFileNameWithoutExtension( path ),
				Model = model
			};

			if ( fields.Length > 1 && Vector3.TryParse( fields[1], out var position ) )
				prop.Position = position;

			if ( fields.Length > 2 && Angles.TryParse( fields[2], out var angles ) )
				prop.Rotation = angles;

			yield return prop;
		}
	}

	/// <summary>
	/// The clip to write into: the one already at that path, or a new one.
	///
	/// Reused rather than always created so a second capture at a different frame lands in the
	/// same clip - "sit at 0, lean forward at 30" is two runs of this command, and a version that
	/// clobbered would make the second one throw the first away.
	/// </summary>
	private static RigAnimDocument Open( string output, Model model, out Asset asset )
	{
		asset = null;

		if ( Project.Current?.GetAssetsPath() is not { } assetsPath )
		{
			Log.Error( "[capture] no current project" );
			return null;
		}

		var relative = output.EndsWith( ".riganim", StringComparison.OrdinalIgnoreCase )
			? output
			: output + ".riganim";

		var absolute = System.IO.Path.Combine( assetsPath,
			relative.Replace( '/', System.IO.Path.DirectorySeparatorChar ) );

		System.IO.Directory.CreateDirectory( System.IO.Path.GetDirectoryName( absolute ) );

		if ( AssetSystem.FindByPath( relative ) is { } existing
			&& existing.TryLoadResource<RigAnimDocument>( out var doc ) && doc is not null )
		{
			asset = existing;

			// A clip captured against one model and then re-captured against another is almost
			// certainly a typo, and silently mixing two skeletons' tracks in one document is not
			// a thing anyone would ask for on purpose.
			if ( doc.SourceModel is not null && doc.SourceModel != model )
			{
				Log.Error( $"[capture] {relative} is a clip for {doc.SourceModel.ResourcePath}, " +
					$"not {model.ResourcePath}. Capture into a different file, or delete that one first." );
				return null;
			}

			doc.SourceModel = model;
			return doc;
		}

		asset = AssetSystem.CreateResource( "riganim", absolute );

		if ( asset is null )
		{
			Log.Error( $"[capture] couldn't create {absolute}" );
			return null;
		}

		return new RigAnimDocument { SourceModel = model };
	}
}
