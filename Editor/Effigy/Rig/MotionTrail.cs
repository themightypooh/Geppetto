using System;
using System.Collections.Generic;
using System.Linq;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// A picture of a clip's motion: side, front and top views side by side, the skeleton ghosted at
/// even intervals, and the path of each traced bone with a dot per frame coloured by speed - blue
/// slow, red fast. Spacing is what you read: dots bunched at the ends of a move are ease, even dots
/// are linear, a gap is a pop. Marionette's motion trail, for a clip nobody has opened.
/// </summary>
public static class MotionTrail
{
	public static Raster Render( Skeleton skeleton, Func<float, Xform[]> pose, float from, float to,
		IReadOnlyList<int> traced, int ghosts = 6, int panel = 360, string title = null )
	{
		var frames = Math.Max( (int)MathF.Round( to - from ), 0 );
		var helper = skeleton.Bones.Select( b => MotionLint.Matches( b.Name, MotionLint.DefaultHelpers ) ).ToArray();
		var poses = Enumerable.Range( 0, frames + 1 ).Select( f => pose( from + f ) ).ToArray();
		var all = poses.SelectMany( p => p.Where( ( _, i ) => !helper[i] ).Select( x => x.Origin ) ).ToList();

		var min = new Vec3( all.Min( p => p.x ), all.Min( p => p.y ), all.Min( p => p.z ) );
		var max = new Vec3( all.Max( p => p.x ), all.Max( p => p.y ), all.Max( p => p.z ) );
		var span = MathF.Max( MathF.Max( max.x - min.x, max.y - min.y ), MathF.Max( max.z - min.z, 1f ) ) * 1.1f;
		var centre = (min + max) * 0.5f;

		var raster = new Raster( panel * 3, panel + 24 );

		// Side looks along -Y (forward X to the right, up Z), front along X (left Y to the left),
		// top down Z.
		var views = new (string Name, Func<Vec3, Vec2> Map)[]
		{
			("side", p => new Vec2( p.x - centre.x, p.z - centre.z )),
			("front", p => new Vec2( centre.y - p.y, p.z - centre.z )),
			("top", p => new Vec2( p.x - centre.x, p.y - centre.y )),
		};

		var maxSpeed = 1e-4f;

		foreach ( var b in traced )
		{
			for ( var f = 1; f < poses.Length; f++ )
				maxSpeed = MathF.Max( maxSpeed, (poses[f][b].Origin - poses[f - 1][b].Origin).Length );
		}

		for ( var v = 0; v < views.Length; v++ )
		{
			var (name, map) = views[v];
			var ox = v * panel;

			Vec2 Screen( Vec3 p )
			{
				var q = map( p );
				return new Vec2( ox + panel * 0.5f + q.x / span * panel, 24 + panel * 0.5f - q.y / span * panel );
			}

			raster.Line( ox, 24, ox, panel + 24, Raster.Rgb( 50, 54, 60 ) );
			raster.Text( ox + 6, 4, name, Raster.Rgb( 170, 175, 185 ) );

			// Ghosts, oldest faintest.
			var count = Math.Max( Math.Min( ghosts, poses.Length ), 1 );

			for ( var g = 0; g < count; g++ )
			{
				var f = count == 1 ? 0 : (int)MathF.Round( g * (poses.Length - 1) / (count - 1f) );
				var alpha = 0.25f + 0.6f * g / Math.Max( count - 1, 1 );

				for ( var b = 0; b < skeleton.Count; b++ )
				{
					var parent = skeleton.Bones[b].Parent;
					if ( parent < 0 || helper[b] ) continue;
					var a = Screen( poses[f][parent].Origin );
					var c = Screen( poses[f][b].Origin );
					raster.Line( a.x, a.y, c.x, c.y, Raster.Rgb( 200, 200, 210 ), alpha );
				}
			}

			foreach ( var b in traced )
			{
				for ( var f = 0; f < poses.Length; f++ )
				{
					var p = Screen( poses[f][b].Origin );

					if ( f > 0 )
					{
						var q = Screen( poses[f - 1][b].Origin );
						raster.Line( q.x, q.y, p.x, p.y, Raster.Rgb( 90, 100, 120 ), 0.7f );
					}

					var speed = f > 0 ? (poses[f][b].Origin - poses[f - 1][b].Origin).Length / maxSpeed : 0f;
					var colour = Raster.Rgb( (int)(60 + 195 * speed), 90, (int)(255 - 195 * speed) );
					raster.Rect( (int)p.x - 1, (int)p.y - 1, 3, 3, colour );
				}
			}
		}

		if ( title is not null )
			raster.Text( panel * 3 - Raster.TextWidth( title ) - 6, 4, title, Raster.Rgb( 140, 145, 155 ) );

		return raster;
	}

	/// <summary>The bones worth tracing when none are named: every chain's end - leaves, and IK goal
	/// bones - capped at <paramref name="most"/>.</summary>
	public static List<int> DefaultTraced( Skeleton skeleton, RigAnimFile clip, int most = 12 )
	{
		var goals = clip.IkGoalTracks.Select( g => skeleton.IndexOf( g.Name ) ).Where( i => i >= 0 );
		var hasChild = new bool[skeleton.Count];

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			if ( skeleton.Bones[i].Parent >= 0 )
				hasChild[skeleton.Bones[i].Parent] = true;
		}

		// Leaves that the clip actually moves - a still finger tip is noise.
		var moved = new HashSet<string>( clip.BoneTracks.Where( t => t.Keys.Count > 1 ).Select( t => t.Name ) );
		var leaves = Enumerable.Range( 0, skeleton.Count ).Where( i => !hasChild[i] && !MotionLint.Matches( skeleton.Bones[i].Name, MotionLint.DefaultHelpers ) && Ancestors( skeleton, i ).Any( a => moved.Contains( skeleton.Bones[a].Name ) ) );

		// A hand's fingertips are five traces of one thing: trace the bone they branch from instead.
		var childCount = new int[skeleton.Count];

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			if ( skeleton.Bones[i].Parent >= 0 )
				childCount[skeleton.Bones[i].Parent]++;
		}

		int Branch( int leaf )
		{
			for ( var b = skeleton.Bones[leaf].Parent; b >= 0; b = skeleton.Bones[b].Parent )
			{
				if ( childCount[b] > 1 )
					return b;
			}

			return leaf;
		}

		var ends = leaves.Select( l => Branch( l ) ).Distinct().ToList();

		// A branch point shared by most of the body (a pelvis, a heap) is no chain end: keep the leaf.
		var shared = ends.Where( e => leaves.Count( l => Branch( l ) == e ) > 6 ).ToHashSet();
		var traced = leaves.Select( l => shared.Contains( Branch( l ) ) ? l : Branch( l ) ).Distinct();

		return goals.Concat( traced ).Distinct().Take( most ).ToList();
	}

	static IEnumerable<int> Ancestors( Skeleton s, int i )
	{
		for ( var b = i; b >= 0; b = s.Bones[b].Parent )
			yield return b;
	}
}
