using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// ChainSpring: Marionette's secondary-motion bake. A cable of four bones along +x whose root is
/// keyed to jump sideways and hold - the simplest move that has to leave a lag, a swing and a settle
/// behind it.
/// </summary>
public static class ChainSpringTests
{
	public static void Run()
	{
		Section( "secondary motion baked over a clip (ChainSpring)" );
		TestLagSwingSettle();
		TestOnlySoftBonesChange();
		TestStillClipStaysStill();
		TestLoopWarmsUp();
	}

	static Skeleton Cable()
	{
		var s = new Skeleton();
		var root = s.AddBone( "root", -1, Xform.Identity );
		var c1 = s.AddBone( "c1", root, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
		var c2 = s.AddBone( "c2", c1, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
		var c3 = s.AddBone( "c3", c2, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
		s.AddBone( "tip", c3, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
		return s;
	}

	/// <summary>Root at rest for 5 frames, eases 20 units along +y over 6, and holds to frame 90.</summary>
	static List<Xform[]> Jump( Skeleton s )
	{
		var frames = new List<Xform[]>();

		for ( var f = 0; f <= 90; f++ )
		{
			var pose = s.Bones.Select( b => b.Local ).ToArray();
			var t = Math.Clamp( (f - 5) / 6f, 0f, 1f );
			pose[0] = Xform.Translate( new Vec3( 0, 20f * t * t * (3f - 2f * t), 0 ) );
			frames.Add( pose );
		}

		return frames;
	}

	static Dictionary<int, SoftBone> Soft( Skeleton s, params string[] names ) =>
		names.ToDictionary( s.IndexOf, _ => new SoftBone { Stiffness = 150f, Damping = 0.001f, Weight = 0f, MaxAngle = 80f } );

	static Vec3 Tip( Skeleton s, Xform[] local ) => RigAnimFile.ModelSpace( s, local )[s.IndexOf( "tip" )].Origin;

	static void TestLagSwingSettle()
	{
		var s = Cable();
		var clip = Jump( s );
		var baked = ChainSpring.Bake( s, clip, Soft( s, "c1", "c2", "c3", "tip" ), 30f );

		Check( "one pose out per frame in", baked.Count == clip.Count );

		var animated = Tip( s, clip[9] );
		var lagging = Tip( s, baked[9] );
		Check( "the tip lags behind a sudden move", lagging.y < animated.y - 2f, $"baked y {lagging.y:0.##}, animated {animated.y:0.##}" );

		var overshoot = Enumerable.Range( 6, 40 ).Max( f => Tip( s, baked[f] ).y );
		Check( "then swings past where the animation put it", overshoot > 20.5f, $"max y {overshoot:0.##}" );

		var end = Tip( s, baked[90] );
		Check( "and settles back onto it", (end - Tip( s, clip[90] )).Length < 0.5f, end.ToString() );

		var world = RigAnimFile.ModelSpace( s, baked[10] );
		var lengths = new[] { "c1", "c2", "c3", "tip" }.Select( b => (world[s.IndexOf( b )].Origin - world[s.Bones[s.IndexOf( b )].Parent].Origin).Length );
		Check( "every bone keeps its length", lengths.All( l => MathF.Abs( l - 10f ) < 1e-3f ), string.Join( ", ", lengths.Select( l => l.ToString( "0.###" ) ) ) );
	}

	static void TestOnlySoftBonesChange()
	{
		var s = Cable();
		var clip = Jump( s );
		var baked = ChainSpring.Bake( s, clip, Soft( s, "c2" ), 30f );

		Check( "a bone that is not soft keeps its keyed local", baked.All( p => p[s.IndexOf( "c1" )].Origin.AlmostEquals( clip[0][s.IndexOf( "c1" )].Origin ) && p[s.IndexOf( "c3" )].X.AlmostEquals( clip[0][s.IndexOf( "c3" )].X ) ) );
		Check( "while the soft one moves", baked.Skip( 5 ).Take( 10 ).Any( p => !p[s.IndexOf( "c2" )].X.AlmostEquals( clip[0][s.IndexOf( "c2" )].X, 1e-3f ) ) );
		Check( "and an empty set changes nothing", ChainSpring.Bake( s, clip, new Dictionary<int, SoftBone>(), 30f ).Zip( clip ).All( p => Tip( s, p.First ).AlmostEquals( Tip( s, p.Second ) ) ) );
	}

	static void TestStillClipStaysStill()
	{
		var s = Cable();
		var still = Enumerable.Range( 0, 30 ).Select( _ => s.Bones.Select( b => b.Local ).ToArray() ).ToList();
		var baked = ChainSpring.Bake( s, still, Soft( s, "c1", "c2", "c3" ), 30f );

		Check( "with no gravity a clip that does not move does not wobble", baked.All( p => (Tip( s, p ) - new Vec3( 40, 0, 0 )).Length < 1e-3f ) );

		var heavy = Soft( s, "c1", "c2", "c3" );

		foreach ( var b in heavy.Values )
			b.Weight = 1f;

		var sag = Tip( s, ChainSpring.Bake( s, still, heavy, 30f )[^1] );
		Check( "with weight it sags under gravity", sag.z < -1f, sag.ToString() );
	}

	static void TestLoopWarmsUp()
	{
		var s = Cable();
		var clip = Jump( s );
		var once = ChainSpring.Bake( s, clip, Soft( s, "c1", "c2", "c3" ), 30f );
		var looped = ChainSpring.Bake( s, clip, Soft( s, "c1", "c2", "c3" ), 30f, loop: true );

		// Played as a loop the jump goes from y=20 back to y=0 at the seam, so the springs come into
		// frame 0 already swinging - the pass that started at rest does not.
		Check( "a looped bake comes into its first frames moving", (Tip( s, looped[2] ) - Tip( s, once[2] )).Length > 0.5f,
			$"{Tip( s, looped[2] )} vs {Tip( s, once[2] )}" );
	}
}
