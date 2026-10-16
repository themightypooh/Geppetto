using Editor;
using Editor.Mcp;
using Effigy;
using Marionette.EditorTools;
using Sandbox;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;

using Skeleton = Effigy.Skeleton;

namespace Marionette.Tools;

/// <summary>
/// Marionette for an agent connected to the editor - mostly so the tool can be CHECKED in the real
/// engine rather than argued about.
///
/// marionette_selftest builds a scene of its own, plays clips made for the purpose through the real
/// RigAnimPlayerComponent, and reads back what the engine did: bone transforms, morph overrides,
/// spawned cameras, marks. Every number is compared against the answer that does not need the
/// engine - BoneTrack.Evaluate, the kernel's RigAnimFile reading the clip as serialized, the
/// export's sampler - so a pass means the editor, the game and the headless checks agree.
///
/// marionette_window opens (or uses) the Marionette window and drives it: tabs built, scrub,
/// onion skin and trail filled, mirror pose round-trip, and a picture of the viewport.
///
/// Neither touches anything on disk. The self-test's scene is thrown away when it returns.
/// </summary>
[McpToolset( "marionette", "Marionette, the control-rig animator: run its in-engine self-test, and open and drive its window." )]
public static partial class MarionetteMcpTools
{
	/// <summary>
	/// Build a scene, play purpose-made clips through RigAnimPlayerComponent, and check the engine's
	/// results against the engine-free answers: cubic and monotone curves (engine vs kernel through the
	/// clip's own JSON), morph events applied and released, live IK at chain lengths 2 and 3 (engine
	/// vs kernel vs export), keyed cameras, marks, and the export's scale. Returns a pass/fail line per
	/// check with the numbers, and a render of the posed model.
	/// </summary>
	[McpTool( "marionette_selftest", Hints = McpToolHints.ReadOnly )]
	public static McpResult SelfTest(
		[Description( "The model to test on. Citizen has morphs and a full arm chain." )] string model = "models/citizen/citizen.vmdl" )
	{
		var report = new Report();
		Bitmap picture = null;

		try
		{
			picture = RunSelfTest( model, report );
		}
		catch ( Exception e )
		{
			report.Fail( "the self-test ran to the end", $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}" );
		}

		var text = report.ToString();
		return picture is not null ? McpResult.Image( picture ).WithText( text ) : McpResult.Text( text );
	}

	/// <summary>
	/// Open the Marionette window (or use the open one) and drive it: check every tab was built, scrub,
	/// fill onion skin and the trail, round-trip Mirror Pose, and return a render of the viewport.
	/// Uses an in-memory clip; nothing is saved.
	/// </summary>
	[McpTool( "marionette_window" )]
	public static McpResult Window(
		[Description( "Frame to scrub to" )] float frame = 10f )
	{
		var report = new Report();
		Bitmap picture = null;

		try
		{
			picture = RunWindowCheck( frame, report );
		}
		catch ( Exception e )
		{
			report.Fail( "the window check ran to the end", $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}" );
		}

		var text = report.ToString();
		return picture is not null ? McpResult.Image( picture ).WithText( text ) : McpResult.Text( text );
	}

	// --- self-test -------------------------------------------------------------------------------

	sealed class Report
	{
		readonly StringBuilder _lines = new();
		public int Passed, Failed;

		public void Check( string what, bool ok, string detail = null )
		{
			if ( ok ) Passed++; else Failed++;
			_lines.AppendLine( $"{(ok ? "ok  " : "FAIL")}  {what}{(detail is null ? "" : $"  [{detail}]")}" );
		}

		public void Fail( string what, string detail ) => Check( what, false, detail );
		public void Note( string text ) => _lines.AppendLine( $"      {text}" );

		public override string ToString() => $"{_lines}\n{Passed} passed, {Failed} failed";
	}

	/// <summary>RigKeyOps on a track built for the purpose - no model needed.</summary>
	static void CheckKeyTools( Report report )
	{
		BoneTrack Track()
		{
			var t = new BoneTrack { BoneName = "k" };
			t.SetKeyframe( 0, new Transform( Vector3.Zero ) );
			t.SetKeyframe( 10, new Transform( new Vector3( 10, 0, 0 ) ) );
			t.SetKeyframe( 30, new Transform( new Vector3( 10, 20, 0 ) ) );
			t.Keyframes[0].Interpolation = KeyInterpolation.EaseIn;
			t.Keyframes[1].Interpolation = KeyInterpolation.Linear;
			return t;
		}

		List<(BoneTrack, BoneKeyframe)> All( BoneTrack t ) => t.Keyframes.Select( k => (t, k) ).ToList();

		var reversed = Track();
		var before = Track();
		RigKeyOps.Reverse( All( reversed ) );
		var mirrored = Enumerable.Range( 0, 31 ).All( f => (reversed.Evaluate( f ).Position - before.Evaluate( 30 - f ).Position).Length < 1e-3f );
		report.Check( "Reverse plays the keys backwards, easing included", mirrored,
			string.Join( " ", Enumerable.Range( 0, 31 ).Where( f => f % 5 == 0 ).Select( f => $"{f}:{F( reversed.Evaluate( f ).Position )}/{F( before.Evaluate( 30 - f ).Position )}" ) ) );

		var slow = Track();
		RigKeyOps.Retime( All( slow ), 2f );
		report.Check( "Retime ×2 doubles every key's distance from the first", string.Join( ",", slow.Keyframes.Select( k => k.Frame ) ) == "0,20,60",
			string.Join( ",", slow.Keyframes.Select( k => k.Frame ) ) );

		var squeezed = Track();
		RigKeyOps.Retime( All( squeezed ), 0.01f );
		report.Check( "a squeeze that lands keys on one frame keeps one key there", squeezed.Keyframes.Count == 1 && squeezed.Keyframes[0].Frame == 0, string.Join( ",", squeezed.Keyframes.Select( k => k.Frame ) ) );

		var a = Track();
		var b = Track();
		b.BoneName = "k2";
		RigKeyOps.Offset( All( a ).Concat( All( b ) ), t => t == b ? 3 : 0 );
		report.Check( "Offset shifts each track by its own amount", a.Keyframes[1].Frame == 10 && b.Keyframes[1].Frame == 13 );

		var dense = new BoneTrack { BoneName = "d" };
		RigKeyOps.Bake( dense, 0, 30, f => new Transform( new Vector3( f, 0, 0 ) ), mode: KeyInterpolation.Linear );
		report.Check( "Bake writes a key on every frame", dense.Keyframes.Count == 31 );
		var removed = RigKeyOps.Reduce( All( dense ) );
		report.Check( "Reduce takes a straight line down to its two ends", dense.Keyframes.Count == 2 && removed == 29, $"{dense.Keyframes.Count} keys left" );
	}

	/// <summary>Clip layering, keyed IK weight and spline IK on the engine's own types - the kernel
	/// tests cover the maths on Effigy's; this proves the s&amp;box twins agree with them.</summary>
	static void CheckLayerWeightSpline( Report report )
	{
		var under = new RigAnimDocument();
		var t = under.GetOrAddTrack( "b" );
		t.SetKeyframe( 0, new Transform( Vector3.Zero ) );
		t.SetKeyframe( 60, new Transform( new Vector3( 0, 0, 60 ) ) );
		t.Keyframes[0].Interpolation = KeyInterpolation.Linear;

		var top = new RigAnimDocument();
		var p = top.GetOrAddTrack( "b" );
		p.SetKeyframe( 0, new Transform( Vector3.Zero ) );
		p.SetKeyframe( 10, new Transform( new Vector3( 5, 0, 0 ), Rotation.Identity, 1.04f ) );
		p.SetKeyframe( 20, new Transform( Vector3.Zero ) );
		top.Marks.Add( new RigMark { Name = "beat", Frame = 10 } );

		var merged = RigClipLayer.MergeLayer( under, top, new RigClipLayer.Settings { Loop = true } );
		var at50 = t.Evaluate( 50 );
		report.Check( "Layer Clip adds a looping layer onto the base", merged.Ok && (at50.Position - new Vector3( 5, 0, 50 )).Length < 0.05f && MathF.Abs( at50.Scale.x - 1.04f ) < 0.005f,
			$"{merged} / at 50: {F( at50.Position )} scale {at50.Scale.x:0.###}" );
		report.Check( "and repeats its marks every loop", under.Marks.Count( m => m.Name == "beat" ) == 3, string.Join( ",", under.Marks.Select( m => m.Frame ) ) );

		under.SetIkWeightKey( "hand", 0, 1f );
		under.SetIkWeightKey( "hand", 20, 0f );
		under.FindIkWeightTrack( "hand" ).Keyframes[0].Interpolation = KeyInterpolation.Linear;
		report.Check( "a keyed IK weight reads back from scale.x, 1 with no track",
			MathF.Abs( under.IkWeightAt( "hand", 10 ) - 0.5f ) < 1e-3f && under.IkWeightAt( "other", 10 ) == 1f );

		// A straight 6-bone chain along +x, the goal pulled in to 60 % and up: one even arc.
		var chain = Enumerable.Range( 0, 7 ).Select( k => new Transform( new Vector3( k * 10f, 0, 0 ) ) ).ToArray();
		var goal = new Vector3( 36, 0, 8 );
		var solved = RigConstraintSolver.SolveChain( chain, goal, null, Vector3.Up, 20, 1f, IkSolver.Spline );
		var bends = Enumerable.Range( 1, 5 ).Select( k => MathF.Acos( Vector3.Dot( (chain[k].Position - chain[k - 1].Position).Normal, (chain[k + 1].Position - chain[k].Position).Normal ).Clamp( -1f, 1f ) ).RadianToDegree() ).ToArray();
		report.Check( "Spline IK reaches the goal with every joint bending about the same", solved && chain[6].Position.Distance( goal ) < 0.05f && bends.Max() - bends.Min() < 6f,
			$"end {F( chain[6].Position )}, bends {string.Join( " ", bends.Select( b => b.ToString( "0.#" ) ) )}" );

		var arc = RigConstraintSolver.ArcJoints( Vector3.Zero, goal, Vector3.Zero, Vector3.Up, Enumerable.Repeat( 10f, 6 ).ToArray() );
		var kernel = Effigy.ChainIk.ArcJoints( new Effigy.Vec3( 0, 0, 0 ), new Effigy.Vec3( 36, 0, 8 ), new Effigy.Vec3( 0, 0, 0 ), new Effigy.Vec3( 0, 0, 1 ), Enumerable.Repeat( 10f, 6 ).ToArray() );
		report.Check( "and lays the same arc as the kernel's", arc is not null && kernel is not null
			&& arc.Zip( kernel ).All( z => (z.First - new Vector3( z.Second.x, z.Second.y, z.Second.z )).Length < 1e-3f ) );
	}

	static GameObject FindBoneObject( GameObject root, string name )
	{
		var queue = new Queue<GameObject>();
		queue.Enqueue( root );

		while ( queue.Count > 0 )
		{
			foreach ( var child in queue.Dequeue().Children )
			{
				if ( child.Name == name )
					return child;

				queue.Enqueue( child );
			}
		}

		return null;
	}

	static string F( Vector3 v ) => $"{v.x:0.###},{v.y:0.###},{v.z:0.###}";

	static Vec3 V( Vector3 v ) => new( v.x, v.y, v.z );

	static Vector3 V( Vec3 v ) => new( v.x, v.y, v.z );

	/// <summary>The end of a chain this model has: hand_R if it is there, else the first bone three
	/// deep that has a child.</summary>
	static BoneCollection.Bone PickHand( Model model )
	{
		if ( model.Bones.GetBone( "hand_R" ) is { } hand )
			return hand;

		return model.Bones.AllBones.FirstOrDefault( b => b.Parent?.Parent?.Parent is not null && model.Bones.AllBones.Any( c => c.Parent == b ) );
	}

	static Bitmap RunSelfTest( string modelPath, Report report )
	{
		var model = Model.Load( modelPath );

		if ( model is null || model.IsError || model.Bones is null || model.Bones.AllBones.Count() < 4 )
		{
			report.Fail( "the test model loads with a skeleton", modelPath );
			return null;
		}

		var hand = PickHand( model );
		var mid = hand?.Parent;
		var top = mid?.Parent;

		if ( hand is null || top is null )
		{
			report.Fail( "the model has a three-bone chain to test IK on", modelPath );
			return null;
		}

		report.Note( $"model {modelPath}: {model.Bones.AllBones.Count()} bones, chain {top.Name} > {mid.Name} > {hand.Name}, {model.MorphCount} morph(s)" );

		var scene = Scene.CreateEditorScene();
		using var scope = scene.Push();

		var cameraObject = new GameObject( true, "test camera" );
		var camera = cameraObject.AddComponent<CameraComponent>();
		camera.FieldOfView = 50f;
		camera.ZFar = 4096f;
		cameraObject.WorldPosition = new Vector3( 130f, -40f, 50f );
		cameraObject.WorldRotation = Rotation.LookAt( new Vector3( 0f, 0f, 45f ) - cameraObject.WorldPosition );

		var sun = new GameObject( true, "sun" ).AddComponent<DirectionalLight>();
		sun.WorldRotation = Rotation.From( 50, 30, 0 );
		new GameObject( true, "ambient" ).AddComponent<AmbientLight>().Color = Color.Gray * 0.5f;

		var subject = new GameObject( true, "subject" );
		var renderer = subject.AddComponent<SkinnedModelRenderer>();
		renderer.Model = model;
		renderer.CreateBoneObjects = true;
		renderer.UseAnimGraph = false;

		for ( var i = 0; i < 3; i++ )
			scene.EditorTick( RealTime.Now, 1f / 60f );

		var handObject = FindBoneObject( subject, hand.Name );
		var midObject = FindBoneObject( subject, mid.Name );
		var topObject = FindBoneObject( subject, top.Name );

		report.Check( "the renderer makes bone objects in an editor scene", handObject.IsValid() && midObject.IsValid() && topObject.IsValid() );

		if ( !handObject.IsValid() )
			return null;

		// --- the clip -----------------------------------------------------------------------------

		var rig = new RigDocument { SourceModel = model };
		var anim = new RigAnimDocument { SourceModel = model, RigAsset = rig, AnimationSpeed = 30, FrameCount = 60 };

		// Curves: the upper arm swung through four keys, cubic out of the first two, monotone after.
		var bindTop = top.Parent is { } tp ? tp.LocalTransform.ToLocal( top.LocalTransform ) : top.LocalTransform;
		var curve = anim.GetOrAddTrack( top.Name );
		var angles = new[] { 0f, 25f, -10f, 30f };

		for ( var k = 0; k < angles.Length; k++ )
		{
			curve.SetKeyframe( k * 10, new Transform( bindTop.Position, bindTop.Rotation * Rotation.FromAxis( Vector3.Up, angles[k] ), bindTop.Scale ) );
			curve.Keyframes[k].Interpolation = k < 2 ? KeyInterpolation.Cubic : KeyInterpolation.Monotone;
		}

		// One key with its own tangent, so the JSON round trip carries one.
		curve.Keyframes[1].TangentOut = curve.AutoTangent( 1, false ).Select( v => v * 0.5f ).ToList();
		curve.Keyframes[1].TangentIn = curve.Keyframes[1].TangentOut.ToList();

		// --- engine curve vs kernel, through the clip's own JSON -------------------------------------

		string json = null;

		try
		{
			json = anim.Serialize().ToJsonString();
		}
		catch ( Exception e )
		{
			report.Fail( "the clip serializes", e.Message );
		}

		if ( json is not null )
		{
			var file = RigAnimFile.Read( json );
			var kernelTrack = file.Find( top.Name );
			report.Check( "the serialized clip reads back with its modes and tangents",
				kernelTrack is { Keys.Count: 4 } && kernelTrack.Keys[0].Ease == RigEase.Cubic && kernelTrack.Keys[2].Ease == RigEase.Monotone && kernelTrack.Keys[1].TangentOut is { Length: 10 },
				kernelTrack is null ? "no track" : $"{kernelTrack.Keys.Count} keys, {kernelTrack.Keys[0].Ease}/{kernelTrack.Keys[2].Ease}, tangent {(kernelTrack.Keys[1].TangentOut?.Length ?? 0)}" );

			var worstP = 0f;
			var worstR = 0f;

			for ( var f = 0f; f <= 30f; f += 0.5f )
			{
				var e = curve.Evaluate( f );
				var k = kernelTrack.Evaluate( f );
				worstP = MathF.Max( worstP, (e.Position - V( k.Position )).Length );
				var dot = MathF.Abs( e.Rotation.x * k.Rotation.x + e.Rotation.y * k.Rotation.y + e.Rotation.z * k.Rotation.z + e.Rotation.w * k.Rotation.w );
				worstR = MathF.Max( worstR, MathF.Acos( MathF.Min( dot, 1f ) ) * 2f * 180f / MathF.PI );
			}

			report.Check( "the engine's curve matches the kernel's through the saved JSON", worstP < 1e-3f && worstR < 0.01f, $"worst {worstP:0.######} units, {worstR:0.#####} deg" );
		}

		// --- player: the curve reaches the bone -------------------------------------------------------

		var player = subject.AddComponent<RigAnimPlayerComponent>( false );
		player.Anim = anim;
		player.Target = renderer;
		player.PlayOnStart = false;
		player.Loop = false;
		player.Enabled = true;

		player.Seek( 15f );
		var expectTop = curve.Evaluate( 15f );
		report.Check( "Seek puts a keyed bone where its curve says", (topObject.LocalPosition - expectTop.Position).Length < 1e-3f && topObject.LocalRotation.Distance( expectTop.Rotation ) < 0.05f,
			$"rot off {topObject.LocalRotation.Distance( expectTop.Rotation ):0.###} deg" );
		report.Check( "and flags it ProceduralBone so the renderer keeps it", topObject.Flags.Contains( GameObjectFlags.ProceduralBone ) );

		scene.EditorTick( RealTime.Now, 1f / 60f );

		if ( renderer.TryGetBoneTransform( top, out var rendered ) )
		{
			var off = (rendered.Position - topObject.WorldPosition).Length;
			report.Check( "the renderer draws the bone where the player put it", off < 0.05f, $"{off:0.###} units" );
		}

		// --- morphs -------------------------------------------------------------------------------

		if ( model.MorphCount > 0 )
		{
			var morph = model.GetMorphName( 0 );
			anim.MorphEvents.Add( new MorphEvent { Name = "test", MorphName = morph, StartFrame = 0, EndFrame = 10, Value = 0.7f } );

			player.Seek( 5f );
			var during = renderer.Morphs.Get( morph );
			report.Check( $"a morph event drives '{morph}' inside its range", MathF.Abs( during - 0.7f ) < 1e-3f, $"{during:0.###}" );

			anim.MorphEvents[^1].FadeIn = 4;
			player.Seek( 2f );
			var fading = renderer.Morphs.Get( morph );
			report.Check( "a 4-frame fade in is halfway (eased) at frame 2", MathF.Abs( fading - 0.35f ) < 1e-3f, $"{fading:0.###} (expect 0.35)" );
			anim.MorphEvents[^1].FadeIn = 0;

			player.Seek( 20f );
			report.Check( "and lets it go outside it", !renderer.Morphs.ContainsOverride( morph ), $"override {(renderer.Morphs.ContainsOverride( morph ) ? "still set" : "cleared")}, value {renderer.Morphs.Get( morph ):0.###}" );
		}
		else
		{
			report.Note( "the model has no morphs - morph checks skipped (try models/citizen/citizen.vmdl)" );
		}

		// --- live IK, two bones -------------------------------------------------------------------

		player.Seek( 0f );
		var fkHand = renderer.WorldTransform.ToLocal( handObject.WorldTransform );
		var towardShoulder = (renderer.WorldTransform.ToLocal( topObject.WorldTransform ).Position - fkHand.Position).Normal;
		var goalLocal = new Transform( fkHand.Position + towardShoulder * 4f + Vector3.Up * 3f, fkHand.Rotation * Rotation.FromAxis( Vector3.Forward, 30f ) );

		var ik = new IkConstraint { Name = "arm", TargetBone = hand.Name, ChainLength = 2, PoleDirection = Vector3.Up, Live = true, GoalRotation = true };
		rig.IkConstraints.Add( ik );
		anim.GetOrAddIkGoalTrack( hand.Name ).SetKeyframe( 0, goalLocal );

		// The chain is resolved on Play(), as a scene that binds parts later would need.
		player.Play();
		player.Pause();
		player.Seek( 0f );

		var handModel = renderer.WorldTransform.ToLocal( handObject.WorldTransform );
		var reach = (handModel.Position - goalLocal.Position).Length;
		report.Check( "live IK (2 bones) puts the hand on its goal", reach < 0.05f, $"{reach:0.###} units off" );
		report.Check( "and turns it to the goal's rotation", handModel.Rotation.Distance( goalLocal.Rotation ) < 0.5f, $"{handModel.Rotation.Distance( goalLocal.Rotation ):0.##} deg" );

		var upper = (midObject.WorldPosition - topObject.WorldPosition).Length;
		var lower = (handObject.WorldPosition - midObject.WorldPosition).Length;
		var bindUpper = (mid.LocalTransform.Position - top.LocalTransform.Position).Length;
		var bindLower = (hand.LocalTransform.Position - mid.LocalTransform.Position).Length;
		report.Check( "without stretching the arm", MathF.Abs( upper - bindUpper ) < 0.05f && MathF.Abs( lower - bindLower ) < 0.05f, $"upper {upper:0.##}/{bindUpper:0.##}, lower {lower:0.##}/{bindLower:0.##}" );

		// Engine vs kernel vs export, on the same clip.
		var skeleton = RigAnimExport.SkeletonFromModel( model );

		if ( json is not null )
		{
			var file = RigAnimFile.Read( anim.Serialize().ToJsonString() );
			file.UseRig( rig.Serialize().ToJsonString() );
			var world = RigAnimFile.ModelSpace( skeleton, file.SampleLocal( skeleton, 0f ) );
			var kernelHand = V( world[skeleton.IndexOf( hand.Name )].Origin );
			report.Check( "the kernel solves the same goal to the same place", (kernelHand - handModel.Position).Length < 0.1f, $"kernel {F( kernelHand )} vs engine {F( handModel.Position )}" );
		}

		var clip = EffigyAnimExport.ToClip( anim, skeleton, "test", false, out _, out _ );
		var exported = V( RigAnimFile.ModelSpace( skeleton, clip.Frames[0] )[skeleton.IndexOf( hand.Name )].Origin );
		report.Check( "and the export bakes it there too", (exported - handModel.Position).Length < 0.1f, $"export {F( exported )} vs engine {F( handModel.Position )}" );

		// --- live IK, three bones ------------------------------------------------------------------

		if ( top.Parent is not null )
		{
			ik.ChainLength = 3;
			ik.Iterations = 20;
			var far = new Transform( fkHand.Position + towardShoulder * 2f + Vector3.Up * 8f + Vector3.Forward * 4f, fkHand.Rotation );
			anim.GetOrAddIkGoalTrack( hand.Name ).SetKeyframe( 0, far );

			player.Play();
			player.Pause();
			player.Seek( 0f );

			var reach3 = (renderer.WorldTransform.ToLocal( handObject.WorldTransform ).Position - far.Position).Length;
			report.Check( "live IK (3 bones, 20 passes) reaches its goal", reach3 < 0.25f, $"{reach3:0.###} units off" );

			ik.Iterations = 1;
			player.Play();
			player.Pause();
			player.Seek( 0f );
			var reach1 = (renderer.WorldTransform.ToLocal( handObject.WorldTransform ).Position - far.Position).Length;
			report.Check( "and Iterations matter (1 pass lands further off)", reach1 >= reach3 - 1e-4f, $"1 pass {reach1:0.###} vs 20 passes {reach3:0.###}" );

			ik.Iterations = 20;
			ik.Solver = IkSolver.Fabrik;
			player.Play();
			player.Pause();
			player.Seek( 0f );
			var fabrikHand = renderer.WorldTransform.ToLocal( handObject.WorldTransform ).Position;
			report.Check( "live IK with FABRIK (3 bones) reaches its goal", (fabrikHand - far.Position).Length < 0.25f, $"{(fabrikHand - far.Position).Length:0.###} units off" );

			var fabrikFile = RigAnimFile.Read( anim.Serialize().ToJsonString() );
			fabrikFile.UseRig( rig.Serialize().ToJsonString() );
			report.Check( "the rig saves the solver and the kernel reads it", fabrikFile.Ik.Any( c => c.Fabrik ) );
			var fabrikKernel = V( RigAnimFile.ModelSpace( skeleton, fabrikFile.SampleLocal( skeleton, 0f ) )[skeleton.IndexOf( hand.Name )].Origin );
			report.Check( "and solves FABRIK to the same place", (fabrikKernel - fabrikHand).Length < 0.1f, $"kernel {F( fabrikKernel )} vs engine {F( fabrikHand )}" );

			ik.Solver = IkSolver.Ccd;
			ik.ChainLength = 2;
			ik.Iterations = 10;
			anim.GetOrAddIkGoalTrack( hand.Name ).SetKeyframe( 0, goalLocal );
		}

		// --- stretch IK ----------------------------------------------------------------------------

		{
			var shoulder = renderer.WorldTransform.ToLocal( topObject.WorldTransform ).Position;
			var outward = (fkHand.Position - shoulder).Normal;
			var beyond = new Transform( shoulder + outward * (bindUpper + bindLower) * 1.3f, fkHand.Rotation );

			ik.MaxStretch = 1.5f;
			anim.GetOrAddIkGoalTrack( hand.Name ).SetKeyframe( 0, beyond );
			player.Play();
			player.Pause();
			player.Seek( 0f );

			var stretched = renderer.WorldTransform.ToLocal( handObject.WorldTransform ).Position;
			report.Check( "stretch IK reaches a goal 1.3x past the arm", (stretched - beyond.Position).Length < 0.1f, $"{(stretched - beyond.Position).Length:0.###} units off" );

			var su = (midObject.WorldPosition - topObject.WorldPosition).Length / bindUpper;
			var sl = (handObject.WorldPosition - midObject.WorldPosition).Length / bindLower;
			report.Check( "by lengthening both bones alike", MathF.Abs( su - 1.3f ) < 0.02f && MathF.Abs( sl - 1.3f ) < 0.02f, $"upper x{su:0.###}, lower x{sl:0.###}" );
			report.Check( "without scaling them", MathF.Abs( midObject.WorldScale.x - topObject.WorldScale.x ) < 1e-3f, $"{midObject.WorldScale.x:0.###}" );

			if ( json is not null )
			{
				var file = RigAnimFile.Read( anim.Serialize().ToJsonString() );
				file.UseRig( rig.Serialize().ToJsonString() );
				report.Check( "the rig saves Max Stretch and the kernel reads it", file.Ik.Any( c => MathF.Abs( c.MaxStretch - 1.5f ) < 1e-4f ) );
				var kernel = V( RigAnimFile.ModelSpace( skeleton, file.SampleLocal( skeleton, 0f ) )[skeleton.IndexOf( hand.Name )].Origin );
				report.Check( "and stretches to the same place", (kernel - stretched).Length < 0.1f, $"kernel {F( kernel )} vs engine {F( stretched )}" );
			}

			var stretchClip = EffigyAnimExport.ToClip( anim, skeleton, "test", false, out _, out _ );
			var stretchExport = V( RigAnimFile.ModelSpace( skeleton, stretchClip.Frames[0] )[skeleton.IndexOf( hand.Name )].Origin );
			report.Check( "and the export bakes the stretch", (stretchExport - stretched).Length < 0.1f, $"export {F( stretchExport )} vs engine {F( stretched )}" );

			ik.MaxStretch = 1f;
			anim.GetOrAddIkGoalTrack( hand.Name ).SetKeyframe( 0, goalLocal );
		}

		// --- goal spaces --------------------------------------------------------------------------

		{
			// A pipe beside the hand, turned, so a wrong angle convention shows as a wrong place.
			var pipe = new RigObject { Name = "pipe", Position = goalLocal.Position + Vector3.Forward * 6f, Rotation = new Angles( 20f, 70f, -15f ) };
			anim.Objects.Add( pipe );

			// Moving the goal about must not depend on whether this model's arm happens to reach.
			ik.MaxStretch = 2f;

			report.Check( "a goal can be keyed against an object", anim.SetIkGoalSpace( hand.Name, IkGoalSpace.Object, "pipe" ) );
			var same = anim.IkGoalAt( hand.Name, 0f ) is { } g0 ? (g0.Position - goalLocal.Position).Length : 999f;
			report.Check( "and converting its keys leaves the goal where it was", same < 1e-3f, $"{same:0.####} units off" );
			report.Check( "and a typo is refused", !anim.SetIkGoalSpace( hand.Name, IkGoalSpace.Object, "no such pipe" ) );

			// The pipe moves; the hand rides it.
			var lift = Vector3.Up * 2f + Vector3.Left * 1f;
			pipe.Position += lift;
			player.Play();
			player.Pause();
			player.Seek( 0f );

			var riding = renderer.WorldTransform.ToLocal( handObject.WorldTransform ).Position;
			var expected = goalLocal.Position + lift;
			report.Check( "the hand follows the object it is keyed on", (riding - expected).Length < 0.05f, $"{(riding - expected).Length:0.###} units off" );

			if ( json is not null )
			{
				var file = RigAnimFile.Read( anim.Serialize().ToJsonString() );
				file.UseRig( rig.Serialize().ToJsonString() );
				report.Check( "the clip saves the goal's space and the kernel reads it", file.GoalSpaces.TryGetValue( hand.Name, out var space ) && space.Space == RigGoalSpace.Object && space.Object == "pipe" );
				var kernel = V( RigAnimFile.ModelSpace( skeleton, file.SampleLocal( skeleton, 0f ) )[skeleton.IndexOf( hand.Name )].Origin );
				report.Check( "and places it on the turned object the same way", (kernel - riding).Length < 0.1f, $"kernel {F( kernel )} vs engine {F( riding )}" );
			}

			var spaceClip = EffigyAnimExport.ToClip( anim, skeleton, "test", false, out _, out _ );
			var spaceExport = V( RigAnimFile.ModelSpace( skeleton, spaceClip.Frames[0] )[skeleton.IndexOf( hand.Name )].Origin );
			report.Check( "and the export bakes it there too", (spaceExport - riding).Length < 0.1f, $"export {F( spaceExport )} vs engine {F( riding )}" );

			// Bound in game to a real pipe somewhere else: the hand goes to that one.
			var real = new GameObject( true, "real pipe" );
			var placed = renderer.WorldTransform.ToWorld( pipe.LocalTransform );
			real.WorldTransform = new Transform( placed.Position + Vector3.Forward * 3f, placed.Rotation );
			player.Parts.Add( new AnimPartBinding { Name = "pipe", Target = real } );
			player.Play();
			player.Pause();
			player.Seek( 0f );

			var onReal = handObject.WorldPosition;
			var wantReal = real.WorldTransform.ToWorld( anim.FindIkGoalTrack( hand.Name ).Evaluate( 0f ) ).Position;
			report.Check( "bound to a scene object, the hand clamps that one", (onReal - wantReal).Length < 0.05f, $"{(onReal - wantReal).Length:0.###} units off" );

			player.Parts.RemoveAll( b => b.Name == "pipe" );
			real.Destroy();

			// World: planted where the clip started while the GameObject walks away.
			anim.SetIkGoalSpace( hand.Name, IkGoalSpace.World, null );
			player.Stop();
			player.Play();
			player.Pause();
			player.Seek( 0f );
			var planted = handObject.WorldPosition;

			var home = subject.WorldPosition;
			subject.WorldPosition = home + Vector3.Forward * 2f;
			player.Seek( 0f );
			var afterMove = handObject.WorldPosition;
			subject.WorldPosition = home;

			report.Check( "a World goal stays planted while the GameObject moves", (afterMove - planted).Length < 0.05f, $"{(afterMove - planted).Length:0.###} units drift" );

			anim.SetIkGoalSpace( hand.Name, IkGoalSpace.Model, null );
			report.Check( "and back in model space nothing is left saying otherwise", anim.IkGoalAnchors.Count == 0 );
			anim.Objects.Remove( pipe );
			ik.MaxStretch = 1f;
			anim.GetOrAddIkGoalTrack( hand.Name ).SetKeyframe( 0, goalLocal );
		}

		// --- key tools ----------------------------------------------------------------------------

		CheckKeyTools( report );
		CheckLayerWeightSpline( report );

		// --- secondary bake -------------------------------------------------------------------------

		var swingGoal = anim.GetOrAddIkGoalTrack( hand.Name );
		swingGoal.SetKeyframe( 6, new Transform( goalLocal.Position + Vector3.Up * 6f + towardShoulder * 3f, goalLocal.Rotation ) );
		swingGoal.SetKeyframe( 20, new Transform( goalLocal.Position + Vector3.Up * 6f + towardShoulder * 3f, goalLocal.Rotation ) );

		var fingers = model.Bones.AllBones.Where( b => b.Parent == hand ).ToList();

		if ( fingers.Count > 0 )
		{
			var bake = RigSecondaryBake.Bake( anim, hand.Name, RigSecondaryBake.Presets[0].ToSoftBone(), loop: false );
			report.Check( "the secondary bake runs below a live-IK hand", bake.Ok && bake.Bones > 0, bake.Message );
			report.Check( "and skips the hand the goal turns", bake.SkippedForIk.Contains( hand.Name ), string.Join( ",", bake.SkippedForIk ) );

			var finger = anim.FindTrack( fingers[0].Name );
			var moved = finger is not null && Enumerable.Range( 6, 14 ).Any( f => finger.Evaluate( f ).Rotation.Distance( finger.Evaluate( 0 ).Rotation ) > 0.5f );
			report.Check( "and the fingers lag (their keys turn after the hand moves)", moved, $"{finger?.Keyframes.Count ?? 0} keys on {fingers[0].Name}" );

			foreach ( var f in fingers )
				anim.BoneTracks.RemoveAll( t => t.Target == TrackTarget.Bone && t.BoneName.StartsWith( f.Name ) );
		}
		else
		{
			report.Note( $"{hand.Name} has no child bones - secondary bake check skipped" );
		}

		swingGoal.Keyframes.RemoveAll( k => k.Frame > 0 );

		// --- cameras ------------------------------------------------------------------------------

		var shot = new RigCamera { Name = "shot", Export = true, Position = new Vector3( 50, 0, 50 ), FieldOfView = 70f };
		anim.Cameras.Add( shot );
		var camTrack = anim.GetOrAddCameraTrack( shot.Name );
		camTrack.SetKeyframe( 0, RigAnimDocument.CameraKey( new Vector3( 50, 0, 50 ), Rotation.Identity, 70f ) );
		camTrack.SetKeyframe( 30, RigAnimDocument.CameraKey( new Vector3( 80, 20, 60 ), Rotation.FromYaw( 40f ), 40f ) );
		camTrack.Keyframes[0].Interpolation = KeyInterpolation.Linear;

		// Cameras are spawned on enable.
		player.Enabled = false;
		player.Enabled = true;
		player.Seek( 15f );

		var spawned = subject.Children.FirstOrDefault( c => c.Name == "shot" );
		var cam = spawned?.GetComponent<CameraComponent>();
		var (expectCam, expectFov) = anim.CameraAt( shot, 15f );
		report.Check( "an exported camera is spawned", spawned.IsValid() && cam.IsValid() );

		if ( cam.IsValid() )
		{
			report.Check( "and follows its keys", (spawned.LocalPosition - expectCam.Position).Length < 1e-3f, $"{F( spawned.LocalPosition )} vs {F( expectCam.Position )}" );
			report.Check( "with the keyed field of view", MathF.Abs( cam.FieldOfView - expectFov ) < 1e-3f && MathF.Abs( expectFov - 55f ) < 1e-3f, $"{cam.FieldOfView:0.##} (expect 55)" );
		}

		// --- marks --------------------------------------------------------------------------------

		anim.Marks.Add( new RigMark { Name = "start", Frame = 0 } );
		anim.Marks.Add( new RigMark { Name = "contact", Frame = 12 } );
		anim.Marks.Add( new RigMark { Name = "release", Frame = 20 } );

		var fired = new List<string>();
		player.MarkReached = m => fired.Add( m.Name );

		var fire = typeof( RigAnimPlayerComponent ).GetMethod( "FireMarks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic );

		if ( fire is null )
		{
			report.Fail( "marks can be exercised", "FireMarks not found" );
		}
		else
		{
			fire.Invoke( player, new object[] { -1f, 12f } );
			fire.Invoke( player, new object[] { 12f, 19.9f } );
			fire.Invoke( player, new object[] { 19.9f, 30f } );
			report.Check( "marks fire once each, in order, frame 0 included", string.Join( ",", fired ) == "start,contact,release", string.Join( ",", fired ) );
		}

		// --- export scale ------------------------------------------------------------------------

		var scaled = anim.GetOrAddTrack( mid.Name );
		var bindMid = mid.Parent is { } mp ? mp.LocalTransform.ToLocal( mid.LocalTransform ) : mid.LocalTransform;
		scaled.SetKeyframe( 0, bindMid );
		scaled.SetKeyframe( 30, new Transform( bindMid.Position, bindMid.Rotation, 1.5f ) );
		scaled.Keyframes[0].Interpolation = KeyInterpolation.Linear;

		var scaledClip = EffigyAnimExport.ToClip( anim, skeleton, "test", false, out _, out _ );
		var midIndex = skeleton.IndexOf( mid.Name );
		report.Check( "the export carries a scaled bone's scale", scaledClip.Scales is not null && MathF.Abs( scaledClip.ScaleAt( 30, midIndex ) - 1.5f ) < 1e-3f,
			scaledClip.Scales is null ? "no scales" : $"{scaledClip.ScaleAt( 30, midIndex ):0.###}" );
		report.Check( "and writes a scale channel for it", DmxAnimWriter.Write( skeleton, scaledClip ).Contains( $"\"{mid.Name}_s\"" ) );

		// --- picture -----------------------------------------------------------------------------

		player.Seek( 15f );
		scene.EditorTick( RealTime.Now, 1f / 60f );

		var bitmap = new Bitmap( 640, 480 );
		camera.RenderToBitmap( bitmap );
		return bitmap;
	}

	// --- window ----------------------------------------------------------------------------------

	static Bitmap RunWindowCheck( float frame, Report report )
	{
		var window = RigControlWindow.Current is { IsValid: true } open ? open : new RigControlWindow();

		report.Check( "the Marionette window opens", window.IsValid );
		report.Check( "the Graph and Take Check panels were built", window.Graph.IsValid() && window.TakeCheck.IsValid() );

		var viewport = window.Viewport;
		var anim = window.Anim;

		if ( viewport?.Renderer is not { } renderer || renderer.Model?.Bones is not { } bones )
		{
			report.Fail( "the viewport has a model", "none loaded" );
			return null;
		}

		report.Note( $"viewport model {renderer.Model.Name}, {bones.AllBones.Count()} bones" );

		// A bone to animate: one with a side, so mirror has something to do.
		var sided = bones.AllBones.FirstOrDefault( b => Skeleton.FlipSideSuffix( b.Name ) is { } other && bones.GetBone( other ) is not null && b.Parent is not null );

		if ( sided is null )
		{
			report.Fail( "the model has a left/right pair", "none found" );
			return null;
		}

		var partner = bones.GetBone( Skeleton.FlipSideSuffix( sided.Name ) );
		var bind = sided.Parent.LocalTransform.ToLocal( sided.LocalTransform );
		var track = anim.GetOrAddTrack( sided.Name );
		track.SetKeyframe( 0, bind );
		track.SetKeyframe( 20, new Transform( bind.Position, bind.Rotation * Rotation.FromAxis( Vector3.Up, 35f ), bind.Scale ) );

		window.ScrubForTools( frame );

		viewport.ShowOnionSkin = true;
		viewport.ShowMotionTrail = true;
		viewport.Select( sided.Name );
		viewport.InvalidateOverlays();

		var counts = viewport.OverlayCounts();
		report.Check( "onion skin builds ghosts at the playhead", counts.Ghosts > 0, $"{counts.Ghosts} ghosts" );
		report.Check( "the motion trail follows the selected bone", counts.TrailPoints > 1 && counts.TrailKeys > 0, $"{counts.TrailPoints} points, {counts.TrailKeys} keys" );

		// Copy to the other side, then flip twice: a flip of a flip is the pose you started with.
		viewport.Select( sided.Name );
		window.MirrorForTools( false );
		window.ScrubForTools( frame );

		if ( viewport.TryGetLocalTransform( sided.Name, out var sideLocal ) && viewport.TryGetLocalTransform( partner.Name, out var partnerLocal ) )
		{
			var mirrored = anim.FindTrack( partner.Name ) is { Keyframes.Count: > 0 };
			report.Check( "Copy Pose to Other Side keys the partner", mirrored, partner.Name );
		}

		var before = viewport.MirroredLocals( null, true );
		var twice = new Dictionary<string, Transform>();

		if ( viewport.TryGetLocalTransform( sided.Name, out var original ) )
		{
			window.MirrorForTools( true );
			window.ScrubForTools( frame );
			window.MirrorForTools( true );
			window.ScrubForTools( frame );

			viewport.TryGetLocalTransform( sided.Name, out var back );
			var drift = (back.Position - original.Position).Length;
			var turn = back.Rotation.Distance( original.Rotation );
			report.Check( "Mirror Pose twice gives the pose back", drift < 0.01f && turn < 0.5f, $"{drift:0.####} units, {turn:0.###} deg" );
		}

		report.Note( $"{before.Count} bone(s) in a full mirror" );

		// The picture: the viewport's own camera. Gizmo overlays are drawn in the widget, not into the
		// scene, so ghosts and trails are counted above rather than seen here.
		var bitmap = new Bitmap( 640, 480 );
		viewport.ViewCameraComponent?.RenderToBitmap( bitmap );
		return bitmap;
	}
}
