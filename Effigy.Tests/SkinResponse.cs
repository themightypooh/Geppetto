using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// The generator's side of how skin answers the warps: it hands the baked, relaxed skin to the
/// kernel's <see cref="SkinStrain"/> (swept compression folds, striae) with the part's settings,
/// carries explicit fold curves through the part's warps, and cleans ragged open edges before
/// they are rolled. The recipes are written up in
/// ~/.claude/skills/organic-modeling/references/techniques/tutorials.md.
/// </summary>
public static partial class CreatureGen
{
	/// <summary>
	/// A fold swept along a curve, given in part space at rest; its points are snapped onto the
	/// rest skin and carried through the part's pose and warps, so the fold lands where the skin
	/// went. See <see cref="SweptFold"/> for the profile. Sizes in part inches.
	/// </summary>
	public sealed record Fold( Vec3[] Points, float Depth, float Width, float Overhang = 0f, int Side = 1, float Pinch = 0.25f );

	static Vec3[] VertexNormals( IList<Vec3> pos, IEnumerable<Face> faces ) => SkinStrain.VertexNormals( pos, faces );

	static float Smoothstep( float a, float b, float x ) => SkinStrain.Smoothstep( a, b, x );

	static List<Vec3> Resample( List<Vec3> pts, float step ) => SkinStrain.CatmullRom( pts, step );

	static float[] EdgeDistance( IList<Vec3> rest, int[][] nb, IEnumerable<int> sources, float limit ) => MeshLoops.EdgeDistance( rest, nb, sources, limit );

	/// <summary>The part's strain settings applied to its relaxed skin in place; the tone goes to <see cref="MeshPart.Tone"/>.</summary>
	static void SkinResponse( MeshPart part, IList<Face> faces, Vec3[] rest, Vec3[] warped, Vec3[] outp )
	{
		bool Blocked( int v ) => part.SkinResponseBlocked is not null && v < part.SkinResponseBlocked.Length && part.SkinResponseBlocked[v];
		var o = new SkinStrainOptions
		{
			FoldWavelength = part.FoldWavelength * part.Scale,
			FoldBelow = part.FoldBelow,
			FoldMax = part.FoldMax * part.Scale,
			FoldMinVerts = part.FoldMinVerts,
			FoldAllowed = v => v < part.Mesh.VertexCount && !Blocked( v ) && (part.FoldMask is null || part.FoldMask( part.Mesh.Positions[v] )),
			StriaeAllowed = v => v < part.Mesh.VertexCount && !Blocked( v ),
			StriaeSpacing = part.StriaeSpacing * part.Scale,
			StriaeAbove = part.StriaeAbove,
			StriaeWidth = part.StriaeWidth * part.Scale,
			StriaeDepth = part.StriaeDepth * part.Scale,
			StriaeAlong = part.StriaeAlong,
			Scale = part.Scale,
			Seed = part.Seed,
		};
		part.Tone = SkinStrain.Respond( faces, rest, warped, outp, o );
	}

	/// <summary>The part's fold curves, snapped to its rest skin and carried through its pose and warps, laid in.</summary>
	static void ApplyFolds( MeshPart part, IList<Face> faces, Vec3[] outp )
	{
		if ( part.Folds.Count == 0 ) return;
		var folds = part.Folds.Select( f => new SweptFold(
			f.Points.Select( q => part.BakeAt( Nearest( part.Mesh.Positions, q ) ) ).ToList(),
			f.Depth * part.Scale, f.Width * part.Scale, f.Overhang * part.Scale, f.Side, f.Pinch ) );
		SkinStrain.SweptFolds( faces, outp, folds );
	}

	/// <summary>
	/// Ragged open edges, gone: repeatedly drop faces with two or more open edges (spikes and
	/// slivers hanging off a cut) and pieces cut loose from the skin, so what is left has a clean
	/// boundary to roll. Edges on <paramref name="ignored"/> vertices (the neck cut) don't count.
	/// </summary>
	static List<Face> CleanEdges( List<Face> faces, Func<int, bool> ignored, int minPiece = 150, int passes = 3 )
	{
		for ( var pass = 0; pass < passes; pass++ )
		{
			var use = new Dictionary<(int, int), int>();
			foreach ( var f in faces )
				for ( var i = 0; i < f.Indices.Length; i++ )
				{
					int a = f.Indices[i], b = f.Indices[(i + 1) % f.Indices.Length];
					var key = a < b ? (a, b) : (b, a);
					use[key] = use.GetValueOrDefault( key ) + 1;
				}
			var before = faces.Count;
			faces = faces.Where( f =>
			{
				var open = 0;
				for ( var i = 0; i < f.Indices.Length; i++ )
				{
					int a = f.Indices[i], b = f.Indices[(i + 1) % f.Indices.Length];
					if ( ignored( a ) || ignored( b ) ) continue;
					if ( use[a < b ? (a, b) : (b, a)] == 1 ) open++;
				}
				return open < 2;
			} ).ToList();
			if ( faces.Count == before ) break;
		}
		// Loose pieces.
		var parent = new Dictionary<int, int>();
		int Find( int x ) { while ( parent[x] != x ) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
		foreach ( var f in faces )
			foreach ( var i in f.Indices )
				parent.TryAdd( i, i );
		foreach ( var f in faces )
			for ( var i = 1; i < f.Indices.Length; i++ )
			{
				var a = Find( f.Indices[0] ); var b = Find( f.Indices[i] );
				if ( a != b ) parent[a] = b;
			}
		var size = new Dictionary<int, int>();
		foreach ( var f in faces ) { var r = Find( f.Indices[0] ); size[r] = size.GetValueOrDefault( r ) + 1; }
		return faces.Where( f => size[Find( f.Indices[0] )] >= minPiece ).ToList();
	}

}
