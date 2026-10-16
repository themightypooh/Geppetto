using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Texel density: how many texture pixels one unit of surface gets, at a given texture size - the
/// number a texture artist matches across every part of a model so a bolt is as sharp as the hull.
///
/// MEASURED BY AREA. An island's density is sqrt(UV area / surface area) times the texture size:
/// the side, in pixels, of the square of texture one square unit of surface is given. That is the
/// average over the island; how evenly it is spread inside the island is stretch, which
/// <see cref="UVIslands.Distortion"/> measures separately. Faces with no area are left out.
///
/// SETTING IT SCALES EACH ISLAND ABOUT ITS OWN CENTRE, so islands keep their places and only grow
/// or shrink. A layout packed to fill the square will then overlap or leave gaps: pack again
/// afterwards (<see cref="UVPack"/>), which keeps the density when given the same scale.
/// </summary>
public static class UVDensity
{
	/// <summary>One island's density.</summary>
	public readonly struct Island
	{
		public readonly int Index;
		public readonly int Faces;
		public readonly float SurfaceArea;
		public readonly float PixelsPerUnit;

		public Island( int index, int faces, float surfaceArea, float pixelsPerUnit )
		{
			Index = index;
			Faces = faces;
			SurfaceArea = surfaceArea;
			PixelsPerUnit = pixelsPerUnit;
		}
	}

	/// <summary>What <see cref="Measure"/> found.</summary>
	public sealed class Report
	{
		public int TextureSize;

		/// <summary>Over every measured face together: total UV area against total surface area.</summary>
		public float PixelsPerUnit;

		public float Min;
		public float Max;
		public List<Island> Islands = new();

		public override string ToString() => Islands.Count == 0
			? "no faces with area to measure"
			: Islands.Count == 1 || Max - Min < 0.005f * MathF.Max( 1f, Max )
				? $"{PixelsPerUnit:0.##} px per unit at {TextureSize} px"
				: $"{PixelsPerUnit:0.##} px per unit at {TextureSize} px (islands {Min:0.##} to {Max:0.##})";
	}

	/// <summary>Pixels per unit over <paramref name="faces"/> as one piece.</summary>
	public static float PixelsPerUnit( PolyMesh mesh, IEnumerable<int> faces, int textureSize )
	{
		var (uv, surface) = Areas( mesh, faces );
		return surface > 1e-12 && uv > 0 ? (float)(Math.Sqrt( uv / surface ) * textureSize) : 0f;
	}

	/// <summary>Density per island and overall, for the islands that touch <paramref name="faces"/>
	/// (every island when null).</summary>
	public static Report Measure( PolyMesh mesh, int textureSize, IEnumerable<int> faces = null, UVIslands.Layout layout = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		if ( textureSize <= 0 )
			throw new InvalidOperationException( "The texture size has to be above zero pixels." );

		layout ??= UVIslands.Find( mesh );
		var report = new Report { TextureSize = textureSize, Min = float.MaxValue, Max = 0f };
		var totalUV = 0.0;
		var totalSurface = 0.0;

		foreach ( var i in IslandsOf( layout, faces ) )
		{
			var (uv, surface) = Areas( mesh, layout.Islands[i] );
			if ( surface <= 1e-12 || uv <= 0 )
				continue;

			var ppu = (float)(Math.Sqrt( uv / surface ) * textureSize);
			report.Islands.Add( new Island( i, layout.Islands[i].Count, (float)surface, ppu ) );
			report.Min = MathF.Min( report.Min, ppu );
			report.Max = MathF.Max( report.Max, ppu );
			totalUV += uv;
			totalSurface += surface;
		}

		if ( report.Islands.Count == 0 )
			report.Min = 0f;

		report.PixelsPerUnit = totalSurface > 1e-12 ? (float)(Math.Sqrt( totalUV / totalSurface ) * textureSize) : 0f;
		return report;
	}

	/// <summary>
	/// Scale every island touching <paramref name="faces"/> (all when null) about its own centre so
	/// it gets <paramref name="pixelsPerUnit"/> at <paramref name="textureSize"/>. Returns how many
	/// islands were scaled; an island with no area cannot be and is left alone.
	/// </summary>
	public static int Set( PolyMesh mesh, float pixelsPerUnit, int textureSize, IEnumerable<int> faces = null, UVIslands.Layout layout = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		if ( !(pixelsPerUnit > 0f) || !float.IsFinite( pixelsPerUnit ) )
			throw new InvalidOperationException( "The texel density has to be above zero: how many pixels one unit of surface should get." );
		if ( textureSize <= 0 )
			throw new InvalidOperationException( "The texture size has to be above zero pixels." );

		layout ??= UVIslands.Find( mesh );
		var scaled = 0;

		foreach ( var i in IslandsOf( layout, faces ) )
		{
			var island = layout.Islands[i];
			var now = PixelsPerUnit( mesh, island, textureSize );
			if ( now <= 0f )
				continue;

			var k = pixelsPerUnit / now;
			UVIslands.Scale( mesh, island, new Vec2( k, k ) );
			scaled++;
		}

		return scaled;
	}

	static IEnumerable<int> IslandsOf( UVIslands.Layout layout, IEnumerable<int> faces )
	{
		if ( faces is null )
			return Enumerable.Range( 0, layout.Count );

		var set = new SortedSet<int>();
		foreach ( var f in faces )
			if ( f >= 0 && f < layout.IslandOfFace.Length )
				set.Add( layout.IslandOfFace[f] );
		return set;
	}

	/// <summary>UV area and surface area over a set of faces, fanned from corner 0.</summary>
	static (double UV, double Surface) Areas( PolyMesh mesh, IEnumerable<int> faces )
	{
		var uv = 0.0;
		var surface = 0.0;
		foreach ( var f in faces )
		{
			var face = mesh.Faces[f];
			if ( face.UVs is null || face.UVs.Length != face.Count )
				continue;

			var p0 = mesh.Positions[face.Indices[0]];
			for ( var i = 1; i + 1 < face.Count; i++ )
			{
				surface += 0.5 * Vec3.Cross( mesh.Positions[face.Indices[i]] - p0, mesh.Positions[face.Indices[i + 1]] - p0 ).Length;
				uv += 0.5 * Math.Abs( Vec2.Cross( face.UVs[i] - face.UVs[0], face.UVs[i + 1] - face.UVs[0] ) );
			}
		}

		return (uv, surface);
	}
}
