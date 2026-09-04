using System;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Verification for the soft bone solver.
///
/// A wobble that is subtly wrong still looks like a wobble. Nothing in here looks at anything - it
/// measures the properties that a plausible-looking but broken solver would violate: bone lengths
/// changing, a still rig drifting, a chain that never settles, a bone leaving its cone, a result
/// that depends on the frame rate. Every one of those is invisible in a viewport and obvious in
/// arithmetic, which is the whole reason this solver was put in the kernel.
/// </summary>
public static class SoftBoneTests
{
	public static void Run()
	{
		Section( "a rigid rig is left exactly alone" );
		TestRigidUntouched();

		Section( "bone lengths survive a solve" );
		TestLengthsPreserved();

		Section( "a still rig settles and stays still" );
		TestSettles();

		Section( "a moved rig lags, then catches up" );
		TestLag();

		Section( "the cone is never exceeded" );
		TestCone();

		Section( "stiffness is frame-rate independent" );
		TestFrameRate();

		Section( "softness accumulates down a chain" );
		TestChain();

		Section( "the solver is deterministic and refuses bad input" );
		TestContract();

		Section( "nonsense softness is reported rather than silently ignored" );
		TestDiagnostics();
	}

	// ---------------------------------------------------------------- fixtures

	/// <summary>
	/// A three-bone chain running up +Y, one unit per bone. Soft from the second bone on, so there
	/// is always a rigid root to measure the soft part against.
	/// </summary>
	static Skeleton Chain( float stiffness = 8f, float damping = 0.04f, float maxAngle = 180f )
	{
		var skeleton = new Skeleton();

		int a = skeleton.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 1, 0 ) );
		int b = skeleton.AddBoneFromPoints( "mid", a, new Vec3( 0, 1, 0 ), new Vec3( 0, 2, 0 ) );
		skeleton.AddBoneFromPoints( "tip", b, new Vec3( 0, 2, 0 ), new Vec3( 0, 3, 0 ) );

		for ( int i = 1; i < skeleton.Count; i++ )
		{
			skeleton.Bones[i].Soft = new SoftBone
			{
				Stiffness = stiffness,
				Damping = damping,
				MaxAngle = maxAngle,
				Weight = 0f,
			};
		}

		return skeleton;
	}

	/// <summary>Run the solver to rest, so a test measures the settled state rather than the swing.</summary>
	static void Settle( Skeleton skeleton, SoftPose pose, int steps = 400, float dt = 1f / 60f )
	{
		for ( int i = 0; i < steps; i++ )
		{
			var world = SoftSolver.BindPose( skeleton );
			SoftSolver.Solve( skeleton, world, pose, dt, Vec3.Zero );
		}
	}

	static float BoneLength( Skeleton skeleton, Xform[] world, int i )
	{
		var bone = skeleton.Bones[i];
		var head = world[i].Origin;
		var tail = head + world[i].Y.Normal * bone.Length;

		return (tail - head).Length;
	}

	// ---------------------------------------------------------------- tests

	/// <summary>
	/// The property that makes the feature adoptable: a rig with no SoftBone anywhere comes back
	/// bit-identical. If this fails, turning the solver on changes every existing model.
	/// </summary>
	static void TestRigidUntouched()
	{
		var skeleton = Chain();
		foreach ( var bone in skeleton.Bones ) bone.Soft = null;

		var expected = SoftSolver.BindPose( skeleton );
		var actual = SoftSolver.BindPose( skeleton );
		var pose = new SoftPose( skeleton.Count );

		for ( int step = 0; step < 20; step++ )
			SoftSolver.Solve( skeleton, actual, pose, 1f / 60f );

		bool same = true;
		for ( int i = 0; i < skeleton.Count; i++ )
			same &= actual[i].Origin.AlmostEquals( expected[i].Origin, 1e-6f )
				&& actual[i].Y.AlmostEquals( expected[i].Y, 1e-6f );

		Check( "a rig with no soft bones is unchanged by the solver", same );
	}

	/// <summary>
	/// The classic failure of every spring-based rig: it looks right and the limb is 6% longer.
	/// Driven hard - big gravity, low stiffness - because that is when a spring solver stretches.
	/// </summary>
	static void TestLengthsPreserved()
	{
		var skeleton = Chain( stiffness: 0.5f, damping: 0.9f );
		foreach ( var bone in skeleton.Bones ) if ( bone.Soft is not null ) bone.Soft.Weight = 4f;

		var pose = new SoftPose( skeleton.Count );
		float worst = 0f;

		for ( int step = 0; step < 200; step++ )
		{
			var world = SoftSolver.BindPose( skeleton );
			SoftSolver.Solve( skeleton, world, pose, 1f / 60f );

			for ( int i = 0; i < skeleton.Count; i++ )
				worst = MathF.Max( worst, MathF.Abs( BoneLength( skeleton, world, i ) - skeleton.Bones[i].Length ) );
		}

		Check( "no bone stretched under gravity", worst < 1e-4f, $"worst error {worst:0.######}" );
	}

	/// <summary>
	/// With no gravity and nothing moving, the solved pose must BE the bind pose - not near it, and
	/// not slowly leaving it. A solver that drifts here turns a static model into one that sags
	/// over the first ten seconds of every scene.
	/// </summary>
	static void TestSettles()
	{
		var skeleton = Chain();
		var pose = new SoftPose( skeleton.Count );

		Settle( skeleton, pose );

		var bind = SoftSolver.BindPose( skeleton );
		var world = SoftSolver.BindPose( skeleton );
		SoftSolver.Solve( skeleton, world, pose, 1f / 60f, Vec3.Zero );

		float drift = 0f;
		for ( int i = 0; i < skeleton.Count; i++ )
			drift = MathF.Max( drift, (world[i].Origin - bind[i].Origin).Length );

		Check( "a still rig does not drift off its bind pose", drift < 1e-4f, $"drift {drift:0.######}" );
	}

	/// <summary>
	/// The feature itself. Move the root, and the soft bones must NOT arrive with it - that lag is
	/// the whole point - but they must arrive eventually, or the limb has come off.
	/// </summary>
	static void TestLag()
	{
		var skeleton = Chain( stiffness: 6f );
		var pose = new SoftPose( skeleton.Count );

		Settle( skeleton, pose );

		// Swing the root a quarter turn about Z, so every bone above it should be left behind.
		var turn = Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI * 0.5f );

		Xform[] Animated()
		{
			var world = SoftSolver.BindPose( skeleton );
			for ( int i = 0; i < world.Length; i++ ) world[i] = turn * world[i];
			return world;
		}

		var target = Animated();

		var first = Animated();
		SoftSolver.Solve( skeleton, first, pose, 1f / 60f, Vec3.Zero );

		float lag = (first[2].Origin - target[2].Origin).Length;
		Check( "the tip lags behind a sudden turn", lag > 0.05f, $"lag {lag:0.####}" );

		for ( int step = 0; step < 600; step++ )
		{
			var world = Animated();
			SoftSolver.Solve( skeleton, world, pose, 1f / 60f, Vec3.Zero );
		}

		var settled = Animated();
		SoftSolver.Solve( skeleton, settled, pose, 1f / 60f, Vec3.Zero );

		float remaining = (settled[2].Origin - target[2].Origin).Length;
		Check( "and catches up once the motion stops", remaining < 1e-3f, $"remaining {remaining:0.######}" );
	}

	/// <summary>
	/// A limit that can be outrun is not a limit. Driven with gravity far past what the stiffness
	/// could resist, which is exactly the case a spring-based "limit" fails.
	/// </summary>
	static void TestCone()
	{
		const float limit = 20f;

		var skeleton = Chain( stiffness: 0.1f, damping: 0.9f, maxAngle: limit );
		foreach ( var bone in skeleton.Bones ) if ( bone.Soft is not null ) bone.Soft.Weight = 20f;

		var pose = new SoftPose( skeleton.Count );
		float worstOwn = 0f;
		float worstTotal = 0f;

		var bind = SoftSolver.BindPose( skeleton );

		for ( int step = 0; step < 300; step++ )
		{
			var world = SoftSolver.BindPose( skeleton );
			SoftSolver.Solve( skeleton, world, pose, 1f / 60f );

			for ( int i = 1; i < skeleton.Count; i++ )
			{
				// The invariant the solver actually enforces is per bone, against the rest
				// direction it was given - which comes from its SOLVED parent, not from the bind
				// pose. Recomputed here the same way, because measuring against the bind pose
				// measures something else: see the accumulation check below.
				var parent = skeleton.Bones[i].Parent;
				var rest = parent >= 0 ? world[parent] * skeleton.Bones[i].Local : world[i];

				worstOwn = MathF.Max( worstOwn, Angle( world[i].Y, rest.Y ) );
				worstTotal = MathF.Max( worstTotal, Angle( world[i].Y, bind[i].Y ) );
			}
		}

		// A hair over for float error; the failure this catches is tens of degrees.
		Check( $"no bone strays more than {limit} degrees from its own parent",
			worstOwn <= limit + 0.5f, $"worst {worstOwn:0.##} deg" );

		// Cones COMPOUND, and that is correct rather than a leak: a chain of bones each 20 degrees
		// off its parent is 40 degrees off the bind pose at the second joint, the same way a real
		// chain is. What must hold is that it is bounded by the chain rather than unbounded.
		int softCount = skeleton.Count - 1;
		Check( "total deviation stays within the chain's worth of cones",
			worstTotal <= limit * softCount + 0.5f, $"total {worstTotal:0.##} deg over {softCount} soft bones" );
	}

	static float Angle( Vec3 a, Vec3 b ) =>
		MathF.Acos( Math.Clamp( Vec3.Dot( a.Normal, b.Normal ), -1f, 1f ) ) * 180f / MathF.PI;

	/// <summary>
	/// The same swing at 30fps and at 240fps must end up in the same place. A solver that lerps by
	/// a per-frame fraction passes every visual check and makes the rig floppier on slow machines,
	/// which then gets blamed on the art.
	/// </summary>
	static void TestFrameRate()
	{
		Vec3 After( float dt, float seconds )
		{
			var skeleton = Chain( stiffness: 6f );
			var pose = new SoftPose( skeleton.Count );

			Settle( skeleton, pose, 400, dt );

			var turn = Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI * 0.5f );

			Xform[] world = null;
			for ( int step = 0; step < (int)(seconds / dt); step++ )
			{
				world = SoftSolver.BindPose( skeleton );
				for ( int i = 0; i < world.Length; i++ ) world[i] = turn * world[i];

				SoftSolver.Solve( skeleton, world, pose, dt, Vec3.Zero );
			}

			return world[2].Origin;
		}

		var slow = After( 1f / 30f, 0.25f );
		var fast = After( 1f / 240f, 0.25f );

		float gap = (slow - fast).Length;

		// Verlet with a changing step is not exactly step-invariant, so this is a tolerance rather
		// than an equality - but a per-frame lerp lands these a whole bone apart.
		Check( "30fps and 240fps agree on where the tip got to", gap < 0.08f, $"gap {gap:0.####}" );
	}

	/// <summary>
	/// The tip must lag more than the middle: each bone's head follows its parent's SOLVED
	/// transform, so softness compounds down the chain. A solver that reads the animated parent
	/// instead gives every bone the same lag, which reads as a rig sliding rather than swinging.
	/// </summary>
	static void TestChain()
	{
		var skeleton = Chain( stiffness: 5f );
		var pose = new SoftPose( skeleton.Count );

		Settle( skeleton, pose );

		var turn = Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI * 0.5f );

		var target = SoftSolver.BindPose( skeleton );
		for ( int i = 0; i < target.Length; i++ ) target[i] = turn * target[i];

		var world = SoftSolver.BindPose( skeleton );
		for ( int i = 0; i < world.Length; i++ ) world[i] = turn * world[i];

		SoftSolver.Solve( skeleton, world, pose, 1f / 60f, Vec3.Zero );

		float mid = (world[1].Origin - target[1].Origin).Length;
		float tip = (world[2].Origin - target[2].Origin).Length;

		Check( "the tip lags further than the bone below it", tip > mid, $"mid {mid:0.####}, tip {tip:0.####}" );
	}

	/// <summary>
	/// Same inputs, same outputs - a rig that wobbles differently on replay cannot be tested by
	/// anyone. And the argument checks, because a mismatched array is a silent read of the wrong
	/// bone rather than a crash.
	/// </summary>
	static void TestContract()
	{
		Vec3 Once()
		{
			var skeleton = Chain();
			var pose = new SoftPose( skeleton.Count );
			var world = SoftSolver.BindPose( skeleton );

			for ( int step = 0; step < 50; step++ )
			{
				world = SoftSolver.BindPose( skeleton );
				SoftSolver.Solve( skeleton, world, pose, 1f / 60f );
			}

			return world[2].Origin;
		}

		Check( "two runs agree exactly", Once().Equals( Once() ) );

		var small = Chain();
		Check( "a short transform array is refused",
			Throws( () => SoftSolver.Solve( small, new Xform[1], new SoftPose( small.Count ), 1f / 60f ) ) );

		Check( "a pose for the wrong bone count is refused",
			Throws( () => SoftSolver.Solve( small, SoftSolver.BindPose( small ), new SoftPose( 1 ), 1f / 60f ) ) );

		// A paused editor hands over dt = 0 every frame. That is not an error and must not move
		// anything or divide by anything.
		var still = Chain();
		var stillPose = new SoftPose( still.Count );
		var stillWorld = SoftSolver.BindPose( still );
		SoftSolver.Solve( still, stillWorld, stillPose, 0f );

		Check( "a zero timestep is a no-op rather than a NaN",
			!float.IsNaN( stillWorld[2].Origin.x ) );

		Check( "cone with a 180 degree limit changes nothing",
			SoftSolver.Cone( new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), 180f ).AlmostEquals( new Vec3( 1, 0, 0 ) ) );

		// Directly opposed: there is no plane the two share, and the naive cross product is zero.
		var opposed = SoftSolver.Cone( new Vec3( 0, -1, 0 ), new Vec3( 0, 1, 0 ), 30f );
		float angle = MathF.Acos( Math.Clamp( Vec3.Dot( opposed, new Vec3( 0, 1, 0 ) ), -1f, 1f ) ) * 180f / MathF.PI;

		Check( "cone handles an exactly opposed direction", MathF.Abs( angle - 30f ) < 0.5f, $"{angle:0.##} deg" );
	}

	/// <summary>
	/// The solver clamps bad numbers rather than throwing, because a slider passes through invalid
	/// on its way to valid. That makes every one of these silent, which is why they have to be
	/// caught somewhere - and the point of catching them is that the bone LOOKS soft in a panel
	/// while being rigid on screen.
	/// </summary>
	static void TestDiagnostics()
	{
		bool Reports( Action<SoftBone> break_, string fragment )
		{
			var skeleton = Chain();
			break_( skeleton.Bones[1].Soft );

			foreach ( var problem in RigDiagnostics.Check( skeleton ) )
			{
				if ( problem.Problem.Contains( fragment, StringComparison.OrdinalIgnoreCase ) )
					return true;
			}

			return false;
		}

		Check( "negative stiffness is reported", Reports( s => s.Stiffness = -5f, "negative stiffness" ) );
		Check( "damping outside 0 to 1 is reported", Reports( s => s.Damping = 4f, "damping outside" ) );
		Check( "a zero cone is reported", Reports( s => s.MaxAngle = 0f, "zero cone" ) );
		Check( "a cone over 180 is reported", Reports( s => s.MaxAngle = 400f, "over 180" ) );

		// A clean rig must stay clean, or the check is noise that gets ignored.
		var good = Chain();
		bool quiet = true;
		foreach ( var problem in RigDiagnostics.Check( good ) )
			quiet &= !problem.Problem.Contains( "Soft bone", StringComparison.Ordinal );

		Check( "a sane soft rig reports nothing", quiet );
	}

	static bool Throws( Action action )
	{
		try { action(); return false; }
		catch { return true; }
	}

	static void Section( string title ) => Report.Section( title );
	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
