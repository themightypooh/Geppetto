using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>What a bake produced. RGB8, row 0 at v = 0.</summary>
public sealed class BakedMap
{
	public readonly int Width;
	public readonly int Height;

	/// <summary>
	/// Three bytes per texel, row-major, with row 0 at v = 0.
	///
	/// WHICH END OF THE IMAGE v = 0 LANDS AT IS THE CALLER'S PROBLEM, and it is a real one: writing
	/// row 0 first puts +v down the image, which half the tools in the world disagree with. An
	/// upside-down normal map lights exactly as wrongly as one with the green channel inverted, and
	/// neither is visible in a thumbnail. See BakeOptions.FlipGreen for the other half of the same
	/// coin flip.
	/// </summary>
	public readonly byte[] Rgb;

	/// <summary>Which texels a ray actually landed on. Everything else is padding or flat.</summary>
	public readonly bool[] Filled;

	/// <summary>
	/// The same texels at 16 bits a channel, or null when the bake was 8-bit
	/// (<see cref="BakeOptions.SixteenBit"/>). Kept beside <see cref="Rgb"/> rather than instead of it:
	/// every 8-bit reader (tests, previews, the paint canvas) keeps working, and PngWriter writes a
	/// 16-bit file only when this is here.
	///
	/// WHY IT EXISTS. A height map spread over 255 greys steps visibly once a parallax shader or a
	/// displacement multiplies it up, and a normal map's 8 bits quantise a gentle slope into
	/// terraces. Only the maps made from a continuous number carry it: height, the three normal
	/// maps and position.
	/// </summary>
	public ushort[] Rgb16;

	public BakedMap( int width, int height )
	{
		Width = width;
		Height = height;
		Rgb = new byte[width * height * 3];
		Filled = new bool[width * height];
	}

	/// <summary>Give this map a 16-bit copy (<see cref="Rgb16"/>), zeroed, if it has none yet.</summary>
	public void EnableSixteenBit() => Rgb16 ??= new ushort[Width * Height * 3];

	/// <summary>The texel at 16 bits, or the 8-bit value widened (×257) when there is no 16-bit copy.</summary>
	public (ushort R, ushort G, ushort B) At16( int x, int y )
	{
		var i = (y * Width + x) * 3;

		if ( Rgb16 is null )
			return ((ushort)(Rgb[i] * 257), (ushort)(Rgb[i + 1] * 257), (ushort)(Rgb[i + 2] * 257));

		return (Rgb16[i], Rgb16[i + 1], Rgb16[i + 2]);
	}

	public int FilledCount
	{
		get
		{
			var n = 0;

			foreach ( var f in Filled )
			{
				if ( f )
					n++;
			}

			return n;
		}
	}

	public (byte R, byte G, byte B) At( int x, int y )
	{
		var i = (y * Width + x) * 3;
		return (Rgb[i], Rgb[i + 1], Rgb[i + 2]);
	}

	/// <summary>
	/// The texel decoded back to a unit vector, which is what a test wants to reason about. Raw
	/// decode: for a default (s&amp;box) normal map +y is UP the image, i.e. towards smaller v.
	/// </summary>
	public Vec3 NormalAt( int x, int y )
	{
		var (r, g, b) = At( x, y );
		return new Vec3( r / 127.5f - 1f, g / 127.5f - 1f, b / 127.5f - 1f ).Normal;
	}
}

public sealed class BakeOptions
{
	/// <summary>
	/// How far either side of the cage surface to look for the sculpt. Zero means "work it out from
	/// the cage's size".
	///
	/// THIS IS THE KNOB THAT PRODUCES BAD BAKES. Too small and a tall detail is missed, leaving flat
	/// patches. Too large and a ray fired from outside meets something else entirely first — the far
	/// side of a sphere, the neighbouring finger — and paints that surface's normal onto this one.
	/// Real bakers solve it with an explicit cage envelope, which is <see cref="Cage"/>; with a cage
	/// set, this is only how far past the low surface the rays carry on.
	/// </summary>
	public float MaxDistance;

	/// <summary>
	/// Write DirectX-style green (+green down the image) instead of the default, which is what
	/// s&amp;box reads: OpenGL-style, +green UP the image (towards v = 0, since row 0 is v = 0).
	///
	/// THE DEFAULT WAS READ OFF THE ENGINE'S SOURCE, NOT GUESSED. s&amp;box builds its bitangent as
	/// cross(N, T)·w, and its own mesh tool (PolygonMesh.CalcTangentAndFlipFromBasis in sbox-public)
	/// picks w so that this is +dP/dv — down the image. Then normal.hlsl's TransformNormal negates
	/// the map's y before using it ("Tools still generate tangent space the inverted Source1 way
	/// where positive y is down"), so +green lands on −dP/dv: up the image. This baker's own frame
	/// has its y along +dP/dv, so <see cref="Encode"/> writes −y by default.
	///
	/// Until 2026-10-16 the default wrote +y and called it "OpenGL", which made every bake light
	/// dents as bumps in s&amp;box. The switch stays for other targets (Unreal, DirectX tools).
	/// </summary>
	public bool FlipGreen;

	/// <summary>
	/// How many texels to bleed the edges outward. A bake stops at the island's edge, and a shader
	/// filtering across that edge picks up whatever is outside it, so seams glow at a distance once
	/// mipmaps get involved. Four is the usual figure.
	/// </summary>
	public int Padding = 4;

	/// <summary>
	/// An explicit cage: how far out along the averaged cage normal the rays start, in model units.
	/// Zero means no cage, and the search runs <see cref="MaxDistance"/> either side of the surface.
	///
	/// With a cage the two directions are decided separately, the way Substance and Marmoset do it:
	/// rays start this far OUT and travel inward through the low surface and up to
	/// <see cref="MaxDistance"/> beyond it (the cage distance again when that is zero). Detail that
	/// stands proud is caught without the ray also being long enough to reach the far side of a thin
	/// part from inside.
	/// </summary>
	public float Cage;

	/// <summary>
	/// Rays per texel side: 1 is one ray at the texel centre, 2 is a 2x2 grid, 4 is 4x4. The samples
	/// are averaged, which is what stops a fine crease from aliasing into a stair-step line. Costs the
	/// square of itself for the normal and height bakes; AO and thickness divide their rays between
	/// the samples, so those cost about the same at any setting.
	/// </summary>
	public int Supersample = 1;

	/// <summary>Hemisphere rays per texel for ambient occlusion (split between supersamples).</summary>
	public int AoRays = 64;

	/// <summary>How far an occluder counts for AO. Zero means a twentieth of the sculpt's diagonal.</summary>
	public float AoDistance;

	/// <summary>
	/// Half the span the curvature is measured across. Zero means one and a half texels, which is
	/// the finest scale the map can hold.
	/// </summary>
	public float CurvatureRadius;

	/// <summary>
	/// How wide a neighbourhood a texel is compared against for cavity. Zero means four texels: a
	/// pit narrower than about eight texels reads, a broad dip does not.
	/// </summary>
	public float CavityRadius;

	/// <summary>Rays fired into the sculpt per texel for thickness (split between supersamples).</summary>
	public int ThicknessRays = 32;

	/// <summary>The thickness that reads as white. Zero means a quarter of the sculpt's diagonal.</summary>
	public float ThicknessDistance;

	/// <summary>
	/// Keep what each texel's ray found on the sculpt - the point, its smooth normal, the face and the
	/// raw AO, curvature and cavity numbers - in <see cref="BakeSet.Texels"/>. What a material bake
	/// evaluates on, so its masks are measured on the detailed surface, not the game mesh.
	/// </summary>
	public bool KeepHits;

	/// <summary>
	/// An explicit cage MESH, the alternative to the scalar <see cref="Cage"/>: a copy of the low
	/// body pushed out by hand (or inflated) where the detail stands proud. Must be the low body's
	/// own topology - same vertex count and the same faces in the same order - because each texel's
	/// ray starts at the matching point on the cage and travels through the matching point on the low
	/// surface. Rays then carry on <see cref="MaxDistance"/> past it (the furthest cage offset when
	/// zero). Null means none.
	///
	/// WHY A MESH AND NOT ONLY A DISTANCE. One distance is a compromise across the whole model: a
	/// finger needs a tight cage or it catches its neighbour, a horn needs a wide one or its tip
	/// bakes flat. A cage mesh sets that per vertex, and its rays aim along the cage-to-low line, so
	/// a cage pushed out at an angle also steers rays around a concave corner.
	/// </summary>
	public PolyMesh CageMesh;

	/// <summary>
	/// Also keep a 16-bit copy (<see cref="BakedMap.Rgb16"/>) of the maps made from a continuous
	/// number - height, normal, bent normal, object-space normal and position - so PngWriter writes
	/// them as 16-bit PNGs. Off by default: an 8-bit PNG is what every existing material and test
	/// expects, and how s&amp;box's texture compiler treats a 16-bit PNG has not been checked in the
	/// engine yet.
	/// </summary>
	public bool SixteenBit;

	/// <summary>
	/// What the ID map (<see cref="BakeMaps.MaterialId"/>) colours by: one id per face of the high
	/// mesh. Null means each face's material slot (<see cref="Face.Material"/>). A by-name or
	/// multi-high bake (<see cref="NormalBake.BakeGroups"/>) can colour by which high body was hit
	/// instead (<see cref="BakeGroup.HighIds"/>).
	/// </summary>
	public int[] FaceIds;
}

/// <summary>
/// One low body and what it is baked from, for <see cref="NormalBake.BakeGroups"/>: its own high
/// mesh, optionally its own cage mesh, and the per-face ids its ID map colours by.
///
/// GROUPS ARE BAKED APART, WHICH IS WHAT AN EXPLODED BAKE IS FOR. Each group's rays only ever see
/// that group's high mesh, so a low body's AO is not darkened by the next part along and its normal
/// map never catches a neighbour's surface - the result an artist gets by pulling every pair apart
/// before baking, without moving anything. The groups still write into ONE set of maps, because
/// low bodies baked by name usually share a texture atlas.
/// </summary>
public sealed class BakeGroup
{
	/// <summary>A name for reports and errors: the base name matched (Arm for Arm_low / Arm_high).</summary>
	public string Name;

	/// <summary>The body that carries the maps: its UVs are where they land.</summary>
	public PolyMesh Low;

	/// <summary>The detail, already combined if there were several high bodies.</summary>
	public PolyMesh High;

	/// <summary>An explicit cage for this low body (see <see cref="BakeOptions.CageMesh"/>), or null.</summary>
	public PolyMesh Cage;

	/// <summary>Per high face, the id the ID map colours it by; null uses <see cref="BakeOptions.FaceIds"/>
	/// or the face's material slot.</summary>
	public int[] HighIds;
}

/// <summary>
/// Per texel, what the bake's rays found on the sculpt (<see cref="BakeOptions.KeepHits"/>). Arrays
/// are width × height, row 0 at v = 0, averaged over the supersamples that hit. <see cref="Hit"/> is
/// false where no ray found the sculpt; everything else there is zero.
/// </summary>
public sealed class BakeTexels
{
	public int Width, Height;
	public bool[] Hit;
	/// <summary>Where on the sculpt, and its smooth normal there.</summary>
	public Vec3[] Point, Normal;
	/// <summary>The sculpt face hit (the last supersample's), -1 where none. In a grouped bake
	/// (<see cref="NormalBake.BakeGroups"/>) an index into that texel's group's high mesh.</summary>
	public int[] Face;
	/// <summary>Occlusion, 0 open to 1 shut in, over the bake's AO distance. Zero unless AO was baked.</summary>
	public float[] Occlusion;
	/// <summary>Curvature in 1 / model units, convex positive. Zero unless curvature was baked.</summary>
	public float[] Curvature;
	/// <summary>How far below its neighbours the texel sits, model units, 0 on flats and ridges.
	/// Zero unless cavity was baked.</summary>
	public float[] Cavity;
}

/// <summary>Which maps <see cref="NormalBake.BakeAll"/> writes.</summary>
[Flags]
public enum BakeMaps
{
	None = 0,
	Normal = 1,
	AmbientOcclusion = 2,
	Curvature = 4,
	Cavity = 8,
	Height = 16,
	Thickness = 32,

	/// <summary>
	/// Bent normal: the average direction light can still arrive from, tangent space like the normal
	/// map. Made from the same hemisphere rays as AO. What s&amp;box's skin shader reads under
	/// F_USE_BENT_NORMALS (TextureBentNormal).
	/// </summary>
	BentNormal = 64,

	/// <summary>The sculpt's normal in object space: x, y, z straight into R, G, B (−1 black, +1 white).</summary>
	ObjectNormal = 128,

	/// <summary>Where on the sculpt each texel landed, x, y, z into R, G, B across the sculpt's bounds.</summary>
	Position = 256,

	/// <summary>A flat colour per material slot (or per high body), for masking in a texture painter.</summary>
	MaterialId = 512,

	/// <summary>The six classic maps. Kept to these so existing scripts' maps=all write what they always did.</summary>
	All = Normal | AmbientOcclusion | Curvature | Cavity | Height | Thickness,

	/// <summary>Every map there is.</summary>
	Everything = All | BentNormal | ObjectNormal | Position | MaterialId,
}

/// <summary>
/// What <see cref="NormalBake.BakeAll"/> produced: one map per bake asked for, and the numbers that
/// say what the greys in each one mean. A map that was not asked for is null.
///
/// THE RANGES ARE PART OF THE RESULT, NOT A DETAIL. A height map's 0 and 255 mean nothing without
/// the distance they stand for — the shader's height scale has to be set from it — and a curvature
/// map normalised to its own data reads the same on a pebble and a planet. Each is reported so the
/// caller can say it, and so two bakes can be compared.
/// </summary>
public sealed class BakeSet
{
	/// <summary>Tangent-space normal map, MikkTSpace-style frame.</summary>
	public BakedMap Normal;

	/// <summary>Ambient occlusion: white is open to the sky, black is shut in.</summary>
	public BakedMap AmbientOcclusion;

	/// <summary>Curvature: mid grey is flat, brighter is convex, darker is concave.</summary>
	public BakedMap Curvature;

	/// <summary>Cavity: white on flats and ridges, darker the further a texel sits below its neighbours.</summary>
	public BakedMap Cavity;

	/// <summary>Height: mid grey is on the cage, white is <see cref="HeightRange"/> out, black that far in.</summary>
	public BakedMap Height;

	/// <summary>Thickness: how far in the sculpt goes behind the surface, white at <see cref="ThicknessDistance"/>.</summary>
	public BakedMap Thickness;

	/// <summary>Bent normal, tangent space, encoded like <see cref="Normal"/> (same green convention).</summary>
	public BakedMap BentNormal;

	/// <summary>The sculpt's normal in object space, (n + 1) / 2 straight into RGB.</summary>
	public BakedMap ObjectNormal;

	/// <summary>The sculpt point per texel, black at <see cref="PositionMin"/>, white at <see cref="PositionMax"/>.</summary>
	public BakedMap Position;

	/// <summary>A flat colour per id (material slot or high body); the colours are in <see cref="IdColors"/>.</summary>
	public BakedMap MaterialId;

	/// <summary>The box the position map spans, per axis: what black and white mean in it.</summary>
	public Vec3 PositionMin, PositionMax;

	/// <summary>Which colour each id was given in the ID map, so a painter can pick a mask by number.</summary>
	public Dictionary<int, (byte R, byte G, byte B)> IdColors;

	/// <summary>The lowest and highest the sculpt sat off the cage, along the cage normal, in model units.</summary>
	public float HeightMin, HeightMax;

	/// <summary>The distance the height map's black and white stand for, either side of mid grey.</summary>
	public float HeightRange;

	/// <summary>The curvature (1 / model units) that reads as white; its negative reads black.</summary>
	public float CurvatureRange;

	/// <summary>The depth below the neighbours, in model units, that reads as black in the cavity map.</summary>
	public float CavityRange;

	/// <summary>The occluder distance the AO rays looked to.</summary>
	public float AoDistance;

	/// <summary>The thickness that reads white.</summary>
	public float ThicknessDistance;

	/// <summary>
	/// The thickness straight through, in model units per texel: one ray from the sculpt's surface
	/// along −N to the far wall (capped at <see cref="ThicknessDistance"/>), −1 where no ray found
	/// the sculpt. Null unless <see cref="BakeMaps.Thickness"/> was baked.
	///
	/// NOT THE SAME NUMBER AS THE GREY MAP. <see cref="Thickness"/> is the Substance-style local
	/// thickness, the mean over a cosine lobe of inward rays, which on a thin sheet comes out about
	/// twice the sheet (the slanted rays travel further). Light going straight through a membrane
	/// travels the −N path, so transmission is built from this.
	///
	/// WHY THE RAW NUMBERS ARE KEPT. Transmission lives in the first few millimetres
	/// (SkinMaterial.TransmissionMask): skin passes 31% red at 1 mm and nothing at 3 mm. The grey map
	/// spreads 255 steps over ThicknessDistance, a quarter of the sculpt's diagonal by default — about
	/// 0.7 mm a step on a 30 in creature — so the whole interesting range would be three greys.
	/// </summary>
	public float[] ThicknessUnits;

	/// <summary>Rays fired from the cage, and how many of them found the sculpt.</summary>
	public int Samples, Hits;

	/// <summary>What each texel's ray found, when <see cref="BakeOptions.KeepHits"/> asked for it; else null.</summary>
	public BakeTexels Texels;

	/// <summary>The maps that were baked, with the short name each is written under.</summary>
	public IEnumerable<(string Name, BakedMap Map)> Maps
	{
		get
		{
			if ( Normal is not null ) yield return ("normal", Normal);
			if ( AmbientOcclusion is not null ) yield return ("ao", AmbientOcclusion);
			if ( Curvature is not null ) yield return ("curvature", Curvature);
			if ( Cavity is not null ) yield return ("cavity", Cavity);
			if ( Height is not null ) yield return ("height", Height);
			if ( Thickness is not null ) yield return ("thickness", Thickness);
			if ( BentNormal is not null ) yield return ("bentnormal", BentNormal);
			if ( ObjectNormal is not null ) yield return ("objectnormal", ObjectNormal);
			if ( Position is not null ) yield return ("position", Position);
			if ( MaterialId is not null ) yield return ("id", MaterialId);
		}
	}

	/// <summary>One line per number a reader needs to use the maps.</summary>
	public string Report
	{
		get
		{
			var c = System.Globalization.CultureInfo.InvariantCulture;
			var lines = new List<string> { string.Format( c, "{0:N0} of {1:N0} rays hit the sculpt ({2:P0})", Hits, Samples, Samples == 0 ? 0f : (float)Hits / Samples ) };
			if ( Height is not null ) lines.Add( string.Format( c, "height: sculpt sits {0:0.####} to {1:0.####} off the cage; map is +-{2:0.####} about mid grey", HeightMin, HeightMax, HeightRange ) );
			if ( AmbientOcclusion is not null ) lines.Add( string.Format( c, "ao: occluders counted out to {0:0.####}", AoDistance ) );
			if ( Curvature is not null ) lines.Add( string.Format( c, "curvature: white is {0:0.####} (1/units), black its negative", CurvatureRange ) );
			if ( Cavity is not null ) lines.Add( string.Format( c, "cavity: black is {0:0.####} below the neighbours", CavityRange ) );
			if ( Thickness is not null ) lines.Add( string.Format( c, "thickness: white is {0:0.####} or more", ThicknessDistance ) );
			if ( BentNormal is not null ) lines.Add( "bentnormal: tangent space, same green as the normal map" );
			if ( ObjectNormal is not null ) lines.Add( "objectnormal: object space, x y z in R G B" );
			if ( Position is not null ) lines.Add( string.Format( c, "position: black ({0:0.####}, {1:0.####}, {2:0.####}) to white ({3:0.####}, {4:0.####}, {5:0.####})",
				PositionMin.x, PositionMin.y, PositionMin.z, PositionMax.x, PositionMax.y, PositionMax.z ) );
			if ( MaterialId is not null && IdColors is not null ) lines.Add( "id: " + string.Join( ", ", IdColors.OrderBy( p => p.Key ).Select( p => $"{p.Key} #{p.Value.R:X2}{p.Value.G:X2}{p.Value.B:X2}" ) ) );
			return string.Join( "\n", lines );
		}
	}
}

/// <summary>
/// Cage + sculpted mesh + the cage's UVs, in; a tangent-space normal map, out.
///
/// THIS IS WHERE THE PIPELINE PAYS OFF. Everything before it exists to get a coarse quad cage that is
/// still parametric and a dense sculpted surface that rides it. This is the step that puts the second
/// one onto the first as texture, so the model that ships is the cage — a few hundred faces, already
/// unwrapped, already rigged — wearing the detail of something a thousand times heavier.
///
/// For each texel: find the cage point that texel belongs to, fire a ray along the cage normal, hit
/// the sculpt, and write the difference between the two surfaces as a direction in the cage's own
/// tangent frame. Tangent-space rather than object-space because the cage moves: it deforms with the
/// rig, and an object-space map would be wrong the moment a bone turned.
///
/// UVS MUST NOT OVERLAP. Two faces sharing texels bake over each other and the last one wins. Box
/// projection, the tool's default, overlaps by construction — it maps +X and -X onto the same square
/// on purpose, which is exactly right for tiling a wall and exactly wrong for a bake. Ask
/// <see cref="Measure"/> before trusting a map.
/// </summary>
public static class NormalBake
{
	public static BakedMap Bake( PolyMesh cage, PolyMesh sculpted, int size, BakeOptions options = null ) =>
		Bake( cage, sculpted, size, size, options );

	public static BakedMap Bake( PolyMesh cage, PolyMesh sculpted, int width, int height, BakeOptions options = null ) =>
		BakeAll( cage, sculpted, width, height, BakeMaps.Normal, options ).Normal;

	/// <summary>
	/// The whole set a Substance or Marmoset bake gives: normal, ambient occlusion, curvature, cavity,
	/// height and thickness, each one PER TEXEL off the sculpt rather than per vertex.
	///
	/// ONE RAY FROM THE CAGE FEEDS EVERY MAP. Each sample finds the sculpt once, then each map asks
	/// its own question at that hit: AO fires a cosine-weighted hemisphere of rays outward, thickness
	/// the same inward, curvature compares the sculpt's normal a short step either side, cavity
	/// compares the height against a ring of neighbours. Nothing is interpolated from vertices, so a
	/// crease narrower than the sculpt's edges still reads, which is the whole point of this over
	/// <see cref="BakeScalar"/>.
	///
	/// Samples run in parallel in chunks; the result does not depend on the scheduling, because every
	/// sample's random rotation is seeded from its own texel and subsample.
	/// </summary>
	public static BakeSet BakeAll( PolyMesh cage, PolyMesh sculpted, int width, int height, BakeMaps maps, BakeOptions options = null )
	{
		if ( cage is null )
			throw new ArgumentNullException( nameof( cage ) );

		if ( sculpted is null )
			throw new ArgumentNullException( nameof( sculpted ) );

		if ( width < 1 || height < 1 )
			throw new ArgumentOutOfRangeException( nameof( width ), "A map needs at least one texel." );

		options ??= new BakeOptions();
		return BakeGroups( new[] { new BakeGroup { Name = "low", Low = cage, High = sculpted, Cage = options.CageMesh, HighIds = options.FaceIds } },
			width, height, maps, options );
	}

	/// <summary>
	/// Several low bodies, each baked from its OWN high mesh, into one set of maps — the by-name
	/// (exploded) bake. See <see cref="BakeGroup"/> for why the groups never see each other. The
	/// lows' UVs share the one texture, so between them they must not overlap
	/// (<see cref="Measure(IEnumerable{PolyMesh}, int)"/>); every range in the result (height,
	/// position) is shared by all groups, so one material reads them all.
	///
	/// A group's cage mesh must match its low body's topology; anything else throws
	/// <see cref="ArgumentException"/> naming the group and the mismatch.
	/// </summary>
	public static BakeSet BakeGroups( IReadOnlyList<BakeGroup> groups, int width, int height, BakeMaps maps, BakeOptions options = null )
	{
		if ( groups is null || groups.Count == 0 )
			throw new ArgumentException( "A bake needs at least one low and high pair.", nameof( groups ) );

		if ( width < 1 || height < 1 )
			throw new ArgumentOutOfRangeException( nameof( width ), "A map needs at least one texel." );

		foreach ( var g in groups )
		{
			if ( g?.Low is null || g.High is null )
				throw new ArgumentException( $"Bake pair '{g?.Name}' is missing its {(g?.Low is null ? "low" : "high")} mesh." );

			if ( g.Cage is not null && CageMismatch( g.Low, g.Cage ) is { } why )
				throw new ArgumentException( $"The cage for '{g.Name}' {why} Make the cage from a copy of the low body (duplicate it, then push it out) so the two match." );

			if ( g.HighIds is not null && g.HighIds.Length != g.High.FaceCount )
				throw new ArgumentException( $"'{g.Name}' has {g.HighIds.Length} face ids for a high mesh of {g.High.FaceCount} faces." );
		}

		options ??= new BakeOptions();
		return new Baker( groups, width, height, maps, options ).Run();
	}

	/// <summary>Why <paramref name="cage"/> cannot be a cage for <paramref name="low"/>, or null if it can.</summary>
	public static string CageMismatch( PolyMesh low, PolyMesh cage )
	{
		if ( cage.VertexCount != low.VertexCount || cage.FaceCount != low.FaceCount )
			return $"has {cage.VertexCount} vertices and {cage.FaceCount} faces, the low body {low.VertexCount} and {low.FaceCount}.";

		for ( var f = 0; f < low.FaceCount; f++ )
		{
			var a = low.Faces[f].Indices;
			var b = cage.Faces[f].Indices;

			if ( a.Length != b.Length )
				return $"has a {b.Length}-sided face {f} where the low body's is {a.Length}-sided.";

			for ( var c = 0; c < a.Length; c++ )
				if ( a[c] != b[c] )
					return $"joins its vertices differently from the low body (face {f}).";
		}

		return null;
	}

	/// <summary>Several meshes' UVs measured as one atlas: what a grouped bake writes into.</summary>
	public static UVCoverage Measure( IEnumerable<PolyMesh> meshes, int resolution = 256 )
	{
		var all = new PolyMesh();

		foreach ( var m in meshes )
		{
			var offset = all.Positions.Count;
			all.Positions.AddRange( m.Positions );

			foreach ( var f in m.Faces )
			{
				var indices = new int[f.Count];

				for ( var c = 0; c < f.Count; c++ )
					indices[c] = f.Indices[c] + offset;

				all.Faces.Add( new Face( indices, f.UVs is null ? null : (Vec2[])f.UVs.Clone(), f.Material ) );
			}
		}

		return Measure( all, resolution );
	}

	/// <summary>
	/// Bake a per-vertex value of the sculpted mesh onto the cage's UVs: its vertex colours, or a
	/// mask computed on it (cavity, ambient occlusion, a material id). Same rays as the normal bake —
	/// fired along the cage normal, landing on the topmost sculpted surface — and the hit's value
	/// interpolated across the face it landed on. <paramref name="value"/> gives RGB 0..1 per
	/// sculpted vertex. Unhit texels take <paramref name="background"/> before padding.
	/// </summary>
	public static BakedMap BakeVertexValues( PolyMesh cage, PolyMesh sculpted, int width, int height, Func<int, Vec3> value, Vec3 background = default, BakeOptions options = null )
	{
		if ( cage is null ) throw new ArgumentNullException( nameof( cage ) );
		if ( sculpted is null ) throw new ArgumentNullException( nameof( sculpted ) );
		if ( value is null ) throw new ArgumentNullException( nameof( value ) );
		if ( width < 1 || height < 1 ) throw new ArgumentOutOfRangeException( nameof( width ), "A map needs at least one texel." );
		options ??= new BakeOptions();
		var map = new BakedMap( width, height );
		for ( var i = 0; i < width * height; i++ ) Write( map, i, background );
		var cageNormals = cage.ComputeVertexNormals();
		var bvh = MeshBVH.Build( sculpted );
		var reach = options.MaxDistance > 0f ? options.MaxDistance : MeasuredReach( cage, sculpted, bvh );
		for ( var fi = 0; fi < cage.FaceCount; fi++ )
		{
			var face = cage.Faces[fi];
			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;
			foreach ( var (ka, kb, kc) in FaceTriangles( cage, fi ) )
			{
				var t = new Triangle(
					cage.Positions[face.Indices[ka]], cage.Positions[face.Indices[kb]], cage.Positions[face.Indices[kc]],
					cageNormals[face.Indices[ka]], cageNormals[face.Indices[kb]], cageNormals[face.Indices[kc]],
					face.UVs[ka], face.UVs[kb], face.UVs[kc] );
				Rasterise( t.U0, t.U1, t.U2, map.Width, map.Height, ( x, y, a, b, c ) =>
				{
					var point = t.P0 * a + t.P1 * b + t.P2 * c;
					var normal = (t.N0 * a + t.N1 * b + t.N2 * c).Normal;
					if ( normal.LengthSquared < 0.5f ) return;
					var hit = bvh.Raycast( sculpted, point + normal * reach, -normal );
					if ( hit is null || hit.Value.Distance > reach * 2f ) return;
					var index = y * map.Width + x;
					Write( map, index, ValueAt( sculpted, hit.Value, value ) );
					map.Filled[index] = true;
				} );
			}
		}
		Dilate( map, options.Padding );
		return map;
	}

	/// <summary>The sculpted mesh's vertex colours baked onto the cage (white where it has none).</summary>
	public static BakedMap BakeVertexColours( PolyMesh cage, PolyMesh sculpted, int width, int height, BakeOptions options = null ) =>
		BakeVertexValues( cage, sculpted, width, height,
			v => sculpted.VertexColors is { } c && v < c.Length ? new Vec3( c[v].x, c[v].y, c[v].z ) : new Vec3( 1, 1, 1 ),
			new Vec3( 0.5f, 0.5f, 0.5f ), options );

	/// <summary>
	/// A per-vertex scalar (cavity, AO, thickness...) baked as greyscale, <paramref name="lo"/> to
	/// black and <paramref name="hi"/> to white.
	/// </summary>
	public static BakedMap BakeScalar( PolyMesh cage, PolyMesh sculpted, int width, int height, float[] values, float lo, float hi, BakeOptions options = null )
	{
		if ( values is null || values.Length != sculpted.VertexCount )
			throw new ArgumentException( "One value per sculpted vertex.", nameof( values ) );
		var span = MathF.Abs( hi - lo ) < 1e-9f ? 1f : hi - lo;
		return BakeVertexValues( cage, sculpted, width, height, v => { var g = Math.Clamp( (values[v] - lo) / span, 0f, 1f ); return new Vec3( g, g, g ); }, new Vec3( 0.5f, 0.5f, 0.5f ), options );
	}

	static Vec3 ValueAt( PolyMesh mesh, MeshHit hit, Func<int, Vec3> value )
	{
		var face = mesh.Faces[hit.FaceIndex];
		var corners = new List<Vec3>( face.Count );
		for ( var c = 0; c < face.Count; c++ ) corners.Add( mesh.Positions[face.Indices[c]] );
		foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
		{
			if ( !Barycentric( hit.Point, corners[ia], corners[ib], corners[ic], out var u, out var v, out var w ) )
				continue;
			return value( face.Indices[ia] ) * u + value( face.Indices[ib] ) * v + value( face.Indices[ic] ) * w;
		}
		// Off every triangle (a non-planar quad's far side): the nearest corner's value.
		var best = 0; var bd = float.MaxValue;
		for ( var c = 0; c < face.Count; c++ ) { var d = (corners[c] - hit.Point).LengthSquared; if ( d < bd ) { bd = d; best = c; } }
		return value( face.Indices[best] );
	}

	static void Write( BakedMap map, int index, Vec3 rgb )
	{
		map.Rgb[index * 3] = (byte)Math.Clamp( (int)MathF.Round( rgb.x * 255f ), 0, 255 );
		map.Rgb[index * 3 + 1] = (byte)Math.Clamp( (int)MathF.Round( rgb.y * 255f ), 0, 255 );
		map.Rgb[index * 3 + 2] = (byte)Math.Clamp( (int)MathF.Round( rgb.z * 255f ), 0, 255 );

		if ( map.Rgb16 is { } wide )
		{
			wide[index * 3] = Word( rgb.x );
			wide[index * 3 + 1] = Word( rgb.y );
			wide[index * 3 + 2] = Word( rgb.z );
		}
	}

	/// <summary>0..1 to the full 16-bit range.</summary>
	static ushort Word( float v ) => (ushort)Math.Clamp( (int)MathF.Round( v * 65535f ), 0, 65535 );

	readonly struct Triangle
	{
		public readonly Vec3 P0, P1, P2;
		public readonly Vec3 N0, N1, N2;
		public readonly Vec2 U0, U1, U2;

		public Triangle( Vec3 p0, Vec3 p1, Vec3 p2, Vec3 n0, Vec3 n1, Vec3 n2, Vec2 u0, Vec2 u1, Vec2 u2 )
		{
			P0 = p0; P1 = p1; P2 = p2;
			N0 = n0; N1 = n1; N2 = n2;
			U0 = u0; U1 = u1; U2 = u2;
		}
	}

	/// <summary>
	/// Tangent and bitangent from the UV gradient — the standard derivation, and the per-triangle
	/// half of the MikkTSpace frame (see <see cref="CornerTangents"/> for the per-vertex half). A
	/// triangle whose UVs are degenerate has no frame to give and is skipped rather than guessed at.
	/// </summary>
	static bool TangentFrame( Vec3 p0, Vec3 p1, Vec3 p2, Vec2 u0, Vec2 u1, Vec2 u2, out Vec3 tangent, out Vec3 bitangent )
	{
		tangent = Vec3.Zero;
		bitangent = Vec3.Zero;

		var e1 = p1 - p0;
		var e2 = p2 - p0;
		var d1 = new Vec2( u1.x - u0.x, u1.y - u0.y );
		var d2 = new Vec2( u2.x - u0.x, u2.y - u0.y );

		var det = d1.x * d2.y - d2.x * d1.y;

		if ( MathF.Abs( det ) < 1e-12f )
			return false;

		var r = 1f / det;
		tangent = (e1 * d2.y - e2 * d1.y) * r;
		bitangent = (e2 * d1.x - e1 * d2.x) * r;

		return tangent.LengthSquared > 1e-16f && bitangent.LengthSquared > 1e-16f;
	}

	/// <summary>
	/// The sculpted surface's normal where the ray landed, interpolated across the face rather than
	/// taken flat from it. A face normal would bake the sculpt's own faceting into the map, which is
	/// the one thing a normal map exists to avoid.
	/// </summary>
	static Vec3 SmoothNormal( PolyMesh mesh, Vec3[] normals, MeshHit hit )
	{
		var face = mesh.Faces[hit.FaceIndex];
		var corners = new List<Vec3>( face.Count );

		for ( var c = 0; c < face.Count; c++ )
			corners.Add( mesh.Positions[face.Indices[c]] );

		// THE SAME TRIANGULATION THE RAYCAST USED, not a fan from corner 0.
		//
		// Triangulate.Face splits a quad along whichever diagonal suits its shape, and a sculpted quad
		// is rarely planar. Fanning from corner 0 instead can put the hit point outside every triangle
		// this looks at, in which case the search finds nothing and the fallback writes the FACE
		// normal into that texel — a faceted speck in a map whose whole purpose is to not be faceted.
		//
		// Honest note: this was changed on the reasoning above, not on a measurement. Swapping it made
		// no visible difference to the smoothness numbers on the plane fixture, whose quads are near
		// enough planar that both triangulations agree. It is the correct thing to do and the fallback
		// is real; how often it fires on a heavily sculpted model has not been measured.
		foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
		{
			if ( !Barycentric( hit.Point, corners[ia], corners[ib], corners[ic], out var u, out var v, out var w ) )
				continue;

			var n = normals[face.Indices[ia]] * u + normals[face.Indices[ib]] * v + normals[face.Indices[ic]] * w;

			if ( n.LengthSquared > 1e-12f )
				return n.Normal;
		}

		return hit.Normal;
	}

	/// <summary>Barycentric coordinates of a point already known to lie in the triangle's plane.</summary>
	static bool Barycentric( Vec3 p, Vec3 a, Vec3 b, Vec3 c, out float u, out float v, out float w )
	{
		u = v = w = 0f;

		var v0 = b - a;
		var v1 = c - a;
		var v2 = p - a;

		var d00 = Vec3.Dot( v0, v0 );
		var d01 = Vec3.Dot( v0, v1 );
		var d11 = Vec3.Dot( v1, v1 );
		var d20 = Vec3.Dot( v2, v0 );
		var d21 = Vec3.Dot( v2, v1 );

		var denom = d00 * d11 - d01 * d01;

		if ( MathF.Abs( denom ) < 1e-20f )
			return false;

		v = (d11 * d20 - d01 * d21) / denom;
		w = (d00 * d21 - d01 * d20) / denom;
		u = 1f - v - w;

		const float slack = 1e-4f;
		return u >= -slack && v >= -slack && w >= -slack;
	}

	/// <summary>
	/// Walk the texels a UV triangle covers, sampling at texel centres. Shared by the bake and by
	/// <see cref="Measure"/>, so the two can never disagree about which texels a face owns.
	///
	/// Now also the paint dab, which is why it is internal rather than private: a dab rasterises a
	/// face's UV triangle with the same walk the bake uses, and two walks that disagreed would put
	/// paint and normals on different texels.
	/// </summary>
	internal static void Rasterise( Vec2 a, Vec2 b, Vec2 c, int width, int height, Action<int, int, float, float, float> texel )
	{
		var minX = (int)MathF.Floor( MathF.Min( a.x, MathF.Min( b.x, c.x ) ) * width - 1f );
		var maxX = (int)MathF.Ceiling( MathF.Max( a.x, MathF.Max( b.x, c.x ) ) * width + 1f );
		var minY = (int)MathF.Floor( MathF.Min( a.y, MathF.Min( b.y, c.y ) ) * height - 1f );
		var maxY = (int)MathF.Ceiling( MathF.Max( a.y, MathF.Max( b.y, c.y ) ) * height + 1f );

		minX = Math.Max( minX, 0 );
		minY = Math.Max( minY, 0 );
		maxX = Math.Min( maxX, width - 1 );
		maxY = Math.Min( maxY, height - 1 );

		var area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);

		if ( MathF.Abs( area ) < 1e-16f )
			return;

		// Wound consistently so the fill rule below has a fixed sense of "inside".
		var flipped = area < 0f;
		var p1 = flipped ? c : b;
		var p2 = flipped ? b : c;
		var doubled = MathF.Abs( area );

		for ( var y = minY; y <= maxY; y++ )
		{
			for ( var x = minX; x <= maxX; x++ )
			{
				var px = (x + 0.5f) / width;
				var py = (y + 0.5f) / height;

				var e0 = Edge( p1, p2, px, py );
				var e1 = Edge( p2, a, px, py );
				var e2 = Edge( a, p1, px, py );

				if ( !Covers( e0, p1, p2 ) || !Covers( e1, p2, a ) || !Covers( e2, a, p1 ) )
					continue;

				var w0 = e0 / doubled;
				var wp1 = e1 / doubled;
				var wp2 = e2 / doubled;

				texel( x, y, w0, flipped ? wp2 : wp1, flipped ? wp1 : wp2 );
			}
		}
	}

	static float Edge( Vec2 u, Vec2 v, float px, float py ) =>
		(v.x - u.x) * (py - u.y) - (v.y - u.y) * (px - u.x);

	/// <summary>
	/// The top-left fill rule, which is what makes a shared edge belong to exactly ONE face.
	///
	/// THIS REPLACED A TOLERANCE, AND THE TOLERANCE WAS WRONG. Accepting any texel within a slack of
	/// the triangle meant a texel centre landing on the edge between two coplanar faces satisfied
	/// both of them — so `Measure` reported overlapping UVs on a mesh whose UVs were perfect, and the
	/// bake wrote the same texel twice. It showed up as exactly one texel on a quadsphere, which is
	/// the kind of number that invites tuning the threshold instead of fixing the rule.
	///
	/// A point exactly on an edge is awarded to the triangle for which that edge is a left or a top
	/// edge; the neighbour, walking the same edge the other way, declines it. Standard, and the only
	/// answer that is exact rather than nearly exact.
	///
	/// Now also the paint dab's fill rule, which is why it is internal rather than private: the dab
	/// and the bake must agree about which face owns a texel on a shared edge, or the dab paints a
	/// seam the bake never touched.
	/// </summary>
	internal static bool Covers( float e, Vec2 u, Vec2 v )
	{
		if ( e > 0f )
			return true;

		if ( e < 0f )
			return false;

		// y is up here, and the winding above is counter-clockwise, so the interior lies to the left
		// of each directed edge: a left edge climbs, a top edge runs right to left.
		var dy = v.y - u.y;

		return dy > 0f || (dy == 0f && v.x < u.x);
	}

	/// <summary>
	/// A normal in the bake frame (x along dP/du, y along dP/dv, z out) to RGB. y is NEGATED by
	/// default: s&amp;box applies +green along −dP/dv (see <see cref="BakeOptions.FlipGreen"/>).
	/// </summary>
	static void Encode( BakedMap map, int index, Vec3 n, bool flipGreen )
	{
		var y = flipGreen ? n.y : -n.y;
		var i = index * 3;

		map.Rgb[i] = Byte( n.x );
		map.Rgb[i + 1] = Byte( y );
		map.Rgb[i + 2] = Byte( n.z );

		if ( map.Rgb16 is { } wide )
		{
			wide[i] = Word( (n.x + 1f) * 0.5f );
			wide[i + 1] = Word( (y + 1f) * 0.5f );
			wide[i + 2] = Word( (n.z + 1f) * 0.5f );
		}
	}

	static byte Byte( float v ) => (byte)Math.Clamp( MathF.Round( (v + 1f) * 127.5f ), 0f, 255f );

	/// <summary>
	/// Bleed filled texels outward, so a shader filtering across an island's edge finds something
	/// sensible there. Each pass takes the average of the filled neighbours; without it seams glow
	/// once mipmaps start mixing in whatever sat outside the island.
	///
	/// Now also the paint canvas, which is why it is internal rather than private: a painted dab
	/// bleeds outward at its edge for exactly the same reason a bake does.
	/// </summary>
	internal static void Dilate( BakedMap map, int passes )
	{
		if ( passes <= 0 )
			return;

		var filled = (bool[])map.Filled.Clone();
		var wide = map.Rgb16;

		for ( var pass = 0; pass < passes; pass++ )
		{
			var added = new List<(int Index, int R, int G, int B)>();
			var added16 = wide is null ? null : new List<(int R, int G, int B)>();

			for ( var y = 0; y < map.Height; y++ )
			{
				for ( var x = 0; x < map.Width; x++ )
				{
					var index = y * map.Width + x;

					if ( filled[index] )
						continue;

					int r = 0, g = 0, b = 0, n = 0;
					long r16 = 0, g16 = 0, b16 = 0;

					for ( var dy = -1; dy <= 1; dy++ )
					{
						for ( var dx = -1; dx <= 1; dx++ )
						{
							var nx = x + dx;
							var ny = y + dy;

							if ( nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height )
								continue;

							var other = ny * map.Width + nx;

							if ( !filled[other] )
								continue;

							r += map.Rgb[other * 3];
							g += map.Rgb[other * 3 + 1];
							b += map.Rgb[other * 3 + 2];
							n++;

							if ( wide is not null )
							{
								r16 += wide[other * 3];
								g16 += wide[other * 3 + 1];
								b16 += wide[other * 3 + 2];
							}
						}
					}

					if ( n > 0 )
					{
						added.Add( (index, r / n, g / n, b / n) );
						added16?.Add( ((int)(r16 / n), (int)(g16 / n), (int)(b16 / n)) );
					}
				}
			}

			if ( added.Count == 0 )
				return;

			for ( var k = 0; k < added.Count; k++ )
			{
				var (index, r, g, b) = added[k];
				map.Rgb[index * 3] = (byte)r;
				map.Rgb[index * 3 + 1] = (byte)g;
				map.Rgb[index * 3 + 2] = (byte)b;
				filled[index] = true;

				if ( wide is not null )
				{
					wide[index * 3] = (ushort)added16[k].R;
					wide[index * 3 + 1] = (ushort)added16[k].G;
					wide[index * 3 + 2] = (ushort)added16[k].B;
				}
			}
		}
	}

	/// <summary>
	/// <see cref="Dilate"/> for a map whose colours are labels, not quantities (the ID map): each
	/// padding texel copies ONE filled neighbour instead of averaging them, so the border between two
	/// ids never grows a third colour that a mask picker would read as another material.
	/// </summary>
	internal static void DilateNearest( BakedMap map, int passes )
	{
		var filled = (bool[])map.Filled.Clone();

		for ( var pass = 0; pass < passes; pass++ )
		{
			var added = new List<(int Index, int From)>();

			for ( var y = 0; y < map.Height; y++ )
			{
				for ( var x = 0; x < map.Width; x++ )
				{
					var index = y * map.Width + x;

					if ( filled[index] )
						continue;

					// Edge neighbours before corners, so the copy comes from the nearest texel.
					foreach ( var (dx, dy) in Neighbours )
					{
						var nx = x + dx;
						var ny = y + dy;

						if ( nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height || !filled[ny * map.Width + nx] )
							continue;

						added.Add( (index, ny * map.Width + nx) );
						break;
					}
				}
			}

			if ( added.Count == 0 )
				return;

			foreach ( var (index, from) in added )
			{
				Array.Copy( map.Rgb, from * 3, map.Rgb, index * 3, 3 );
				filled[index] = true;
			}
		}
	}

	static readonly (int, int)[] Neighbours = { (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1) };

	/// <summary>
	/// How far to search, MEASURED off the two surfaces rather than guessed from the model's size.
	///
	/// A FRACTION OF THE DIAGONAL IS NOT GOOD ENOUGH, and the case that proves it is the ordinary one:
	/// a SculptFeature's cage IS the coarse body and the sculpt IS its Catmull-Clark subdivision, and
	/// subdivision pulls a cube's corners a very long way in. On a 2x2x2 box the two surfaces are 2.6
	/// units apart at the corners while a tenth of the diagonal is 0.35 — so the old default missed
	/// three quarters of the map and the bake came out mostly flat. Nothing about that is exotic; it
	/// is what pressing Bake on a box does.
	///
	/// So: probe from every cage vertex along its own normal, far enough to cross both models, and
	/// take the largest separation actually found. Times 1.5, because texels sit between vertices and
	/// the surface can bow further out between two of them than at either.
	///
	/// Still bounded above by the cage's own diagonal. The failure at the other end — a reach so long
	/// that a ray meets the far side of the model and paints its normal onto this one — is worse than
	/// a flat patch, because it looks like detail.
	/// </summary>
	static float MeasuredReach( PolyMesh cage, PolyMesh sculpted, MeshBVH bvh )
	{
		var diagonal = cage.BoundsDiagonal;

		if ( diagonal <= 1e-6f )
			return 1f;

		var probe = diagonal + sculpted.BoundsDiagonal;
		var normals = cage.ComputeVertexNormals();
		var worst = 0f;

		for ( var i = 0; i < cage.VertexCount; i++ )
		{
			var normal = normals[i];

			if ( normal.LengthSquared < 0.5f )
				continue;

			var hit = bvh.Raycast( sculpted, cage.Positions[i] + normal * probe, -normal );

			if ( hit is null )
				continue;

			worst = MathF.Max( worst, MathF.Abs( hit.Value.Distance - probe ) );
		}

		// The floor covers a cage sitting exactly on its sculpt, where every probe returns zero and a
		// reach of zero would find nothing at all.
		return Math.Clamp( worst * 1.5f, diagonal * 0.1f, diagonal );
	}

	// --- the frame the engine shades with ---------------------------------------------------------

	/// <summary>
	/// The triangles a face is split into, as corner indices into the face — THE ONE TRIANGULATION
	/// shared by the bake and the exporters (DmxWriter, FbxWriter, ObjWriter with triangulate on).
	///
	/// WHY IT HAS TO BE SHARED. A tangent-space map is only right across the triangles it was baked
	/// on. A sculpted cage's quads are rarely planar, and a non-planar quad split along the other
	/// diagonal is a different surface with a different interpolated normal and tangent, so the map
	/// lights a faint diagonal crease into every quad. Exporting n-gons left the split to ModelDoc,
	/// whose rule is not documented; exporting these triangles takes the question away.
	///
	/// THE RULE: a fan from corner 0 — what the bake always did, so existing bakes do not move —
	/// unless that fan folds, in UV (a triangle wound against the rest, which would bake texels
	/// outside the face) or in 3D (a concave cap filled in). Then a fan from corner 1 for a quad (the
	/// other diagonal), and ear clipping on the face's own plane (Triangulate.Face) for anything
	/// bigger. A face where nothing is clean keeps the fan.
	/// </summary>
	public static List<(int A, int B, int C)> FaceTriangles( PolyMesh mesh, int faceIndex )
	{
		var face = mesh.Faces[faceIndex];
		var n = face.Count;

		if ( n < 3 )
			return new List<(int, int, int)>();

		var fan = Fan( n, 0 );

		if ( n == 3 || Clean( mesh, face, fan ) )
			return fan;

		if ( n == 4 )
		{
			var other = Fan( 4, 1 );

			if ( Clean( mesh, face, other ) )
				return other;
		}

		var corners = new Vec3[n];

		for ( var c = 0; c < n; c++ )
			corners[c] = mesh.Positions[face.Indices[c]];

		var clipped = Triangulate.Face( corners );
		return clipped.Count == n - 2 && Clean( mesh, face, clipped ) ? clipped : fan;
	}

	static List<(int A, int B, int C)> Fan( int n, int start )
	{
		var list = new List<(int, int, int)>( n - 2 );

		for ( var i = 1; i < n - 1; i++ )
			list.Add( (start, (start + i) % n, (start + i + 1) % n) );

		return list;
	}

	/// <summary>Every triangle faces the same way as the polygon, in 3D and (when there are UVs) in UV.</summary>
	static bool Clean( PolyMesh mesh, Face face, List<(int A, int B, int C)> triangles )
	{
		var normal = Vec3.Zero;

		for ( var c = 0; c < face.Count; c++ )
		{
			var a = mesh.Positions[face.Indices[c]];
			var b = mesh.Positions[face.Indices[(c + 1) % face.Count]];
			normal += new Vec3( (a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y) );
		}

		var uvs = face.UVs is not null && face.UVs.Length == face.Count ? face.UVs : null;
		var uvArea = 0f;

		if ( uvs is not null )
		{
			for ( var c = 0; c < face.Count; c++ )
				uvArea += Vec2.Cross( uvs[c], uvs[(c + 1) % face.Count] );
		}

		foreach ( var (a, b, c) in triangles )
		{
			var pa = mesh.Positions[face.Indices[a]];
			var cross = Vec3.Cross( mesh.Positions[face.Indices[b]] - pa, mesh.Positions[face.Indices[c]] - pa );

			if ( Vec3.Dot( cross, normal ) <= 0f )
				return false;

			if ( uvs is not null && MathF.Abs( uvArea ) > 1e-12f && Vec2.Cross( uvs[b] - uvs[a], uvs[c] - uvs[a] ) * uvArea <= 0f )
				return false;
		}

		return true;
	}

	/// <summary>
	/// The tangent the bake used at every face corner, as the engine wants it: xyz the tangent
	/// (unit, in the plane of that corner's shading normal) and w the bitangent sign, so that the
	/// shader's cross(N, T)·w is +dP/dv — the convention s&amp;box's own mesh tool writes
	/// (PolygonMesh.CalcTangentAndFlipFromBasis: w = −1 only for a mirrored UV island).
	///
	/// Pass the same corner normals the exporter writes (MeshNormals.ComputeCornerNormals at the
	/// same smoothing angle), or the frame will not be the one the bake inverted. Indexed
	/// [face][corner]; a face with no UVs gets null, a corner with no usable tangent gets one built
	/// from its normal so the file never carries a zero vector.
	/// </summary>
	public static Vec4[][] ExportTangents( PolyMesh mesh, int[][] cornerNormals, List<Vec3> normals )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( cornerNormals is null || normals is null ) throw new ArgumentNullException( nameof( cornerNormals ) );

		var (tangents, signs) = CornerTangents( mesh, cornerNormals, normals );
		var result = new Vec4[mesh.FaceCount][];

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];
			result[fi] = new Vec4[face.Count];

			for ( var c = 0; c < face.Count; c++ )
			{
				var n = normals[cornerNormals[fi][c]].Normal;
				var t = tangents[fi]?[c] ?? Vec3.Zero;
				t = t - n * Vec3.Dot( n, t );
				t = t.LengthSquared > 1e-12f ? t.Normal : AnyPerpendicular( n );
				result[fi][c] = new Vec4( t.x, t.y, t.z, tangents[fi] is null ? 1f : signs[fi] );
			}
		}

		return result;
	}

	// --- the bake set ----------------------------------------------------------------------------

	/// <summary>
	/// Per-vertex tangents the way MikkTSpace builds them, for every face corner of the cage.
	///
	/// WHY THIS AND NOT THE TRIANGLE'S OWN UV GRADIENT. A normal map is only right if the renderer
	/// rebuilds the same tangent frame the baker used, and a renderer does not have the triangle's
	/// gradient — it has a tangent PER VERTEX, interpolated across the face. s&amp;box gets those from
	/// ModelDoc on import (the exported .obj carries no tangents), and the import side of every
	/// current engine and DCC builds them the MikkTSpace way. A bake from the per-triangle gradient
	/// differs from that at every triangle border, which shows as faint seams along the edges once the
	/// model is lit; this matches it instead.
	///
	/// What is matched: each triangle's UV-gradient tangent, projected into the plane of the corner's
	/// shading normal, normalised, and summed weighted by the corner's angle; summed only among corners
	/// that share a vertex, a shading normal, a UV and a handedness, so a UV seam or a mirrored island
	/// splits the vertex exactly as Mikk does. The bitangent is not stored — it is
	/// sign · cross(normal, tangent), rebuilt per texel, the way the shader rebuilds it.
	///
	/// Honest note: this is the algorithm, not a port of mikktspace.c, and nothing here has compared
	/// it to ModelDoc's tangents byte for byte — the engine's import does not expose them. Mikk's
	/// degenerate-triangle handling is simplified (such a corner just gets no tangent, and the texel
	/// falls back to a frame built from the normal).
	/// </summary>
	static (Vec3[][] Tangents, float[] Signs) CornerTangents( PolyMesh mesh, int[][] cornerNormals, List<Vec3> normals )
	{
		var tangents = new Vec3[mesh.FaceCount][];
		var signs = new float[mesh.FaceCount];
		var sums = new Dictionary<(int, int, int, int, int), Vec3>();
		var keys = new (int, int, int, int, int)[mesh.FaceCount][];

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			// The face's handedness: the sign of its UV area against its winding. One per face, so a
			// fan's triangles never disagree with each other.
			var area = 0f;

			for ( var i = 1; i < face.Count - 1; i++ )
				area += Vec2.Cross( face.UVs[i] - face.UVs[0], face.UVs[i + 1] - face.UVs[0] );

			var sign = area < 0f ? -1f : 1f;
			signs[fi] = sign;
			keys[fi] = new (int, int, int, int, int)[face.Count];

			for ( var c = 0; c < face.Count; c++ )
			{
				var uv = face.UVs[c];
				keys[fi][c] = (face.Indices[c], cornerNormals[fi][c], (int)MathF.Round( uv.x * 1e5f ), (int)MathF.Round( uv.y * 1e5f ), (int)sign);
			}

			foreach ( var (ta, tb, tc) in FaceTriangles( mesh, fi ) )
			{
				var corners = new[] { ta, tb, tc };
				var p = new[] { mesh.Positions[face.Indices[ta]], mesh.Positions[face.Indices[tb]], mesh.Positions[face.Indices[tc]] };

				if ( !TangentFrame( p[0], p[1], p[2], face.UVs[ta], face.UVs[tb], face.UVs[tc], out var tangent, out _ ) )
					continue;

				for ( var k = 0; k < 3; k++ )
				{
					var n = normals[cornerNormals[fi][corners[k]]];
					var projected = (tangent - n * Vec3.Dot( n, tangent )).Normal;

					if ( projected.LengthSquared < 0.5f )
						continue;

					var a = (p[(k + 1) % 3] - p[k]).Normal;
					var b = (p[(k + 2) % 3] - p[k]).Normal;
					var angle = MathF.Acos( Math.Clamp( Vec3.Dot( a, b ), -1f, 1f ) );
					var key = keys[fi][corners[k]];
					sums[key] = (sums.TryGetValue( key, out var sum ) ? sum : Vec3.Zero) + projected * angle;
				}
			}
		}

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			if ( keys[fi] is null )
				continue;

			tangents[fi] = new Vec3[keys[fi].Length];

			for ( var c = 0; c < keys[fi].Length; c++ )
				tangents[fi][c] = sums.TryGetValue( keys[fi][c], out var sum ) ? sum.Normal : Vec3.Zero;
		}

		return (tangents, signs);
	}

	/// <summary>
	/// One bake group, ready to fire at: the high mesh with its normals and BVH, the low body (for
	/// cavity's look back at the cage), and how far either side of the low surface the rays search.
	/// </summary>
	sealed class Target
	{
		public PolyMesh Low, High, Cage;
		public Vec3[] Normals;
		public MeshBVH Bvh, LowBvh;
		public int[] Ids;
		public float Front, Back;
	}

	/// <summary>One fan triangle of the cage with everything a sample needs from its corners.</summary>
	sealed class BakeTriangle
	{
		public Target G;
		public Vec3 P0, P1, P2;      // positions
		public Vec3 C0, C1, C2;      // the cage mesh's matching corners, when there is one
		public Vec3 R0, R1, R2;      // averaged normals, which the rays travel along
		public Vec3 S0, S1, S2;      // shading normals, as exported (split at hard edges)
		public Vec3 T0, T1, T2;      // Mikk tangents
		public float Sign;
		public Vec2 U0, U1, U2;
		public float CurvatureRadius, CavityRadius;
	}

	struct BakeSample
	{
		public BakeTriangle Triangle;
		public float A, B, C;
		public int Texel;
		public uint Seed;
	}

	struct SampleResult
	{
		public bool Hit;
		public Vec3 Normal, Bent;
		public Vec3 Point, Smooth;
		public int Face, Id;
		public float Ao, Height, Thickness, Through, Curvature, Cavity;
		public bool HasCurvature, HasCavity;
	}

	/// <summary>The state of one <see cref="BakeGroups"/> run.</summary>
	sealed class Baker
	{
		readonly List<Target> _targets = new();
		readonly int _width, _height, _ss;
		readonly BakeMaps _maps;
		readonly BakeOptions _o;
		readonly float _eps, _aoDistance, _thickDistance;
		readonly int _aoRays, _thickRays;
		readonly Vec3 _boundsMin, _boundsMax;

		// Per-texel sums over the supersamples that hit.
		readonly int[] _hits;
		readonly Vec3[] _normal, _bent, _object;
		readonly float[] _ao, _heightSum, _thick, _through, _curv, _cav;
		readonly int[] _curvN, _cavN, _id;
		readonly Vec3[] _hitPoint, _hitNormal;
		readonly int[] _hitFace;
		int _samples, _hitCount;

		const int Chunk = 1 << 16;
		const int NoId = int.MinValue;

		public Baker( IReadOnlyList<BakeGroup> groups, int width, int height, BakeMaps maps, BakeOptions options )
		{
			_width = width;
			_height = height;
			_maps = maps == BakeMaps.None ? BakeMaps.Normal : maps;
			_o = options;
			_ss = Math.Clamp( options.Supersample, 1, 8 );

			// The extent of every high mesh together: what the AO, thickness and epsilon defaults
			// scale with, and what the position map spans. One group gives exactly its own bounds.
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

			foreach ( var g in groups )
			{
				foreach ( var p in g.High.Positions )
				{
					min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
					max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
				}
			}

			if ( min.x > max.x )
				min = max = Vec3.Zero;

			_boundsMin = min;
			_boundsMax = max;

			foreach ( var g in groups )
			{
				var t = new Target
				{
					Low = g.Low, High = g.High, Cage = g.Cage, Ids = g.HighIds ?? (groups.Count == 1 ? options.FaceIds : null),
					Normals = g.High.ComputeVertexNormals(),
					Bvh = MeshBVH.Build( g.High ),
				};

				t.LowBvh = Has( BakeMaps.Cavity ) ? MeshBVH.Build( g.Low ) : null;

				if ( g.Cage is not null )
				{
					// The rays start ON the cage, so the window in front is the furthest the cage
					// sits off the low surface; behind, the distance asked for or that same again.
					var furthest = 0f;

					for ( var i = 0; i < g.Low.VertexCount; i++ )
						furthest = MathF.Max( furthest, (g.Cage.Positions[i] - g.Low.Positions[i]).Length );

					t.Front = MathF.Max( furthest, g.Low.BoundsDiagonal * 1e-3f );
					t.Back = options.MaxDistance > 0f ? options.MaxDistance : t.Front;
				}
				else if ( options.Cage > 0f )
				{
					t.Front = options.Cage;
					t.Back = options.MaxDistance > 0f ? options.MaxDistance : options.Cage;
				}
				else
				{
					var reach = options.MaxDistance > 0f ? options.MaxDistance : MeasuredReach( g.Low, g.High, t.Bvh );
					t.Front = reach;
					t.Back = reach;
				}

				_targets.Add( t );
			}

			var diagonal = MathF.Max( (max - min).Length, 1e-4f );
			_eps = diagonal * 1e-4f;
			_aoDistance = options.AoDistance > 0f ? options.AoDistance : diagonal * 0.05f;
			_thickDistance = options.ThicknessDistance > 0f ? options.ThicknessDistance : diagonal * 0.25f;

			// AO and thickness share their rays out between the supersamples, so a 4x4 bake costs
			// about what a 1x1 bake does for those two and the average is just as converged.
			var perTexel = _ss * _ss;
			_aoRays = Math.Max( 4, (options.AoRays + perTexel - 1) / perTexel );
			_thickRays = Math.Max( 4, (options.ThicknessRays + perTexel - 1) / perTexel );

			var n = width * height;
			_hits = new int[n];
			_normal = Has( BakeMaps.Normal ) ? new Vec3[n] : null;
			_bent = Has( BakeMaps.BentNormal ) ? new Vec3[n] : null;
			_object = Has( BakeMaps.ObjectNormal ) ? new Vec3[n] : null;
			_ao = Has( BakeMaps.AmbientOcclusion ) ? new float[n] : null;
			_heightSum = new float[n];
			_thick = Has( BakeMaps.Thickness ) ? new float[n] : null;
			_through = Has( BakeMaps.Thickness ) ? new float[n] : null;
			_curv = Has( BakeMaps.Curvature ) ? new float[n] : null;
			_curvN = Has( BakeMaps.Curvature ) ? new int[n] : null;
			_cav = Has( BakeMaps.Cavity ) ? new float[n] : null;
			_cavN = Has( BakeMaps.Cavity ) ? new int[n] : null;

			if ( Has( BakeMaps.MaterialId ) )
			{
				_id = new int[n];
				Array.Fill( _id, NoId );
			}

			// Positions are kept for the position map as well as for KeepHits: one average per texel.
			if ( options.KeepHits || Has( BakeMaps.Position ) )
				_hitPoint = new Vec3[n];

			if ( options.KeepHits )
			{
				_hitNormal = new Vec3[n];
				_hitFace = new int[n];
				Array.Fill( _hitFace, -1 );
			}
		}

		bool Has( BakeMaps map ) => (_maps & map) != 0;

		public BakeSet Run()
		{
			var triangles = Triangles();
			var pending = new List<BakeSample>( Chunk );
			var sw = _width * _ss;
			var sh = _height * _ss;

			// SUPERSAMPLING IS THE SAME RASTER AT A FINER GRID. Rasterising at ss times the size and
			// folding each sample back onto its texel keeps the fill rule exact — a sample on an edge
			// between two faces still belongs to exactly one of them — which jittering inside each
			// texel would not.
			foreach ( var t in triangles )
			{
				Rasterise( t.U0, t.U1, t.U2, sw, sh, ( x, y, a, b, c ) =>
				{
					var texel = (y / _ss) * _width + x / _ss;
					var sub = (y % _ss) * _ss + x % _ss;
					pending.Add( new BakeSample { Triangle = t, A = a, B = b, C = c, Texel = texel, Seed = Hash( (uint)texel * 73u + (uint)sub ) } );

					if ( pending.Count >= Chunk )
						Flush( pending );
				} );
			}

			Flush( pending );
			return Finish();
		}

		List<BakeTriangle> Triangles()
		{
			var list = new List<BakeTriangle>();

			foreach ( var g in _targets )
			{
				var low = g.Low;
				var rayNormals = low.ComputeVertexNormals();
				var (cornerNormals, shadeNormals) = MeshNormals.ComputeCornerNormals( low, MeshNormals.DefaultSmoothingAngleDegrees );
				var (tangents, signs) = CornerTangents( low, cornerNormals, shadeNormals );

				for ( var fi = 0; fi < low.FaceCount; fi++ )
				{
					var face = low.Faces[fi];

					if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
						continue;

					// THE EXPORT'S TRIANGULATION (FaceTriangles), so the triangles the map was baked
					// across are the triangles the engine shades.
					foreach ( var (c0, c1, c2) in FaceTriangles( low, fi ) )
					{
						var t = new BakeTriangle
						{
							G = g,
							P0 = low.Positions[face.Indices[c0]], P1 = low.Positions[face.Indices[c1]], P2 = low.Positions[face.Indices[c2]],
							R0 = rayNormals[face.Indices[c0]], R1 = rayNormals[face.Indices[c1]], R2 = rayNormals[face.Indices[c2]],
							S0 = shadeNormals[cornerNormals[fi][c0]], S1 = shadeNormals[cornerNormals[fi][c1]], S2 = shadeNormals[cornerNormals[fi][c2]],
							T0 = tangents[fi][c0], T1 = tangents[fi][c1], T2 = tangents[fi][c2],
							Sign = signs[fi],
							U0 = face.UVs[c0], U1 = face.UVs[c1], U2 = face.UVs[c2],
						};

						if ( g.Cage is not null )
						{
							t.C0 = g.Cage.Positions[face.Indices[c0]];
							t.C1 = g.Cage.Positions[face.Indices[c1]];
							t.C2 = g.Cage.Positions[face.Indices[c2]];
						}

						// How much surface one texel covers here, so "1.5 texels" means the same on a
						// densely packed island as on a sparse one.
						var worldArea = Vec3.Cross( t.P1 - t.P0, t.P2 - t.P0 ).Length;
						var uvArea = MathF.Abs( Vec2.Cross( t.U1 - t.U0, t.U2 - t.U0 ) ) * _width * _height;
						var texel = uvArea > 1e-12f ? MathF.Sqrt( worldArea / uvArea ) : 0f;
						t.CurvatureRadius = _o.CurvatureRadius > 0f ? _o.CurvatureRadius : texel * 1.5f;
						t.CavityRadius = _o.CavityRadius > 0f ? _o.CavityRadius : texel * 4f;
						list.Add( t );
					}
				}
			}

			return list;
		}

		void Flush( List<BakeSample> pending )
		{
			if ( pending.Count == 0 )
				return;

			var samples = pending.ToArray();
			var results = new SampleResult[samples.Length];
			System.Threading.Tasks.Parallel.For( 0, samples.Length, i => results[i] = Sample( samples[i] ) );
			pending.Clear();

			for ( var i = 0; i < samples.Length; i++ )
			{
				_samples++;
				var r = results[i];

				if ( !r.Hit )
					continue;

				_hitCount++;
				var texel = samples[i].Texel;
				_hits[texel]++;
				_heightSum[texel] += r.Height;
				if ( _normal is not null ) _normal[texel] += r.Normal;
				if ( _bent is not null ) _bent[texel] += r.Bent;
				if ( _object is not null ) _object[texel] += r.Smooth;
				if ( _ao is not null ) _ao[texel] += r.Ao;
				if ( _thick is not null ) _thick[texel] += r.Thickness;
				if ( _through is not null ) _through[texel] += r.Through;
				if ( _curv is not null && r.HasCurvature ) { _curv[texel] += r.Curvature; _curvN[texel]++; }
				if ( _cav is not null && r.HasCavity ) { _cav[texel] += r.Cavity; _cavN[texel]++; }

				// The first sample to land decides a texel's id: ids are labels, and an average of two
				// labels is a third label nobody asked for. Samples are flushed in raster order, so
				// "first" does not depend on the thread scheduling.
				if ( _id is not null && _id[texel] == NoId ) _id[texel] = r.Id;

				if ( _hitPoint is not null )
					_hitPoint[texel] += r.Point;

				if ( _hitNormal is not null )
				{
					_hitNormal[texel] += r.Smooth;
					_hitFace[texel] = r.Face;
				}
			}
		}

		/// <summary>The sculpt along the cage normal from <paramref name="p"/>, inside the search window.</summary>
		static bool Cast( Target g, Vec3 p, Vec3 n, out MeshHit hit )
		{
			// Fired from outside the surface inward, so the nearest hit is the topmost sculpted
			// surface rather than whatever is behind it.
			var found = g.Bvh.Raycast( g.High, p + n * g.Front, -n );
			hit = found ?? default;
			return found is not null && found.Value.Distance <= g.Front + g.Back;
		}

		/// <summary>
		/// The main ray of a sample. With a cage mesh it runs from the matching cage point through the
		/// low point and <see cref="Target.Back"/> beyond; without one, along the averaged normal.
		/// </summary>
		static bool CastFromCage( BakeTriangle t, BakeSample s, Vec3 p, Vec3 n, out MeshHit hit )
		{
			var g = t.G;

			if ( g.Cage is null )
				return Cast( g, p, n, out hit );

			var origin = t.C0 * s.A + t.C1 * s.B + t.C2 * s.C;
			var along = p - origin;
			var length = along.Length;

			// A cage vertex left on the low surface has no direction to give; that corner falls back
			// to the normal, with the cage's furthest offset as the window.
			if ( length < 1e-6f || Vec3.Dot( along, n ) >= 0f )
				return Cast( g, p, n, out hit );

			var found = g.Bvh.Raycast( g.High, origin, along / length );
			hit = found ?? default;
			return found is not null && found.Value.Distance <= length + g.Back;
		}

		SampleResult Sample( BakeSample s )
		{
			var t = s.Triangle;
			var g = t.G;
			var result = new SampleResult();
			var p = t.P0 * s.A + t.P1 * s.B + t.P2 * s.C;
			var n = (t.R0 * s.A + t.R1 * s.B + t.R2 * s.C).Normal;

			if ( n.LengthSquared < 0.5f || !CastFromCage( t, s, p, n, out var hit ) )
				return result;

			var hn = SmoothNormal( g.High, g.Normals, hit );
			result.Hit = true;
			result.Point = hit.Point;
			result.Smooth = hn;
			result.Face = hit.FaceIndex;
			result.Height = Vec3.Dot( hit.Point - p, n );

			if ( _id is not null )
				result.Id = g.Ids is not null && hit.FaceIndex < g.Ids.Length ? g.Ids[hit.FaceIndex] : g.High.Faces[hit.FaceIndex].Material;

			// The texel's tangent frame exactly as the shader will rebuild it: the vertex normal and
			// tangent interpolated and NOT renormalised, the bitangent sign · cross(n, t). Inverting
			// that matrix, rather than dotting with an orthonormalised copy, is what makes the shader's
			// normalize(x·T + y·B + z·N) land back on the sculpt's normal where the frame is skewed.
			var sn = t.S0 * s.A + t.S1 * s.B + t.S2 * s.C;
			var tg = t.T0 * s.A + t.T1 * s.B + t.T2 * s.C;

			if ( (tg - sn.Normal * Vec3.Dot( tg, sn.Normal )).LengthSquared < 1e-8f )
				tg = AnyPerpendicular( sn.Normal );

			var bt = Vec3.Cross( sn, tg ) * t.Sign;

			if ( _normal is not null )
				result.Normal = TangentSpace( hn, tg, bt, sn );

			// An orthonormal frame on the cage for the probes that step sideways.
			var u = (tg - n * Vec3.Dot( tg, n )).Normal;
			var v = Vec3.Cross( n, u );

			if ( _ao is not null || _bent is not null )
			{
				var occlusion = Occlusion( g, hit, hn, s.Seed, out var open );

				if ( _ao is not null )
					result.Ao = 1f - occlusion;

				// THE BENT NORMAL IS THE SAME RAYS' OTHER ANSWER: not how many escaped but which way.
				// Fully shut in, nothing escaped, and the honest direction is the surface's own.
				if ( _bent is not null )
					result.Bent = TangentSpace( open.LengthSquared > 1e-12f ? open.Normal : hn, tg, bt, sn );
			}

			if ( _thick is not null )
			{
				result.Thickness = Thickness( g, hit, hn, s.Seed ^ 0x9e3779b9u );

				// And the straight path in along −N, which is what transmission travels.
				var through = g.Bvh.Raycast( g.High, hit.Point - hit.Normal * (_eps * 2f) - hn * _eps, -hn );
				result.Through = through is null ? _thickDistance : MathF.Min( through.Value.Distance, _thickDistance );
			}

			if ( _curv is not null )
				result.HasCurvature = Curvature( g, p, n, u, v, t.CurvatureRadius, out result.Curvature );

			if ( _cav is not null )
				result.HasCavity = Cavity( g, p, n, u, v, t.CavityRadius, result.Height, out result.Cavity );

			return result;
		}

		/// <summary>
		/// The fraction of a cosine-weighted hemisphere above the hit that meets the sculpt within the
		/// AO distance, and (<paramref name="open"/>) the sum of the directions that did not.
		/// </summary>
		float Occlusion( Target g, MeshHit hit, Vec3 hn, uint seed, out Vec3 open )
		{
			var origin = hit.Point + hit.Normal * (_eps * 2f) + hn * _eps;
			var (bx, by) = Basis( hn );
			var (jx, jy) = (Unit( seed ), Unit( Hash( seed ) ));
			var counted = 0;
			var blocked = 0;
			open = Vec3.Zero;

			for ( var k = 0; k < _aoRays; k++ )
			{
				var dir = Cosine( k, _aoRays, jx, jy, hn, bx, by );

				// Below the face the ray leaves from: it would start inside the sculpt. Not counted
				// either way rather than counted as blocked, which would darken every faceted hit.
				if ( Vec3.Dot( dir, hit.Normal ) <= 0f )
					continue;

				counted++;
				var h = g.Bvh.Raycast( g.High, origin, dir );

				if ( h is not null && h.Value.Distance <= _aoDistance )
					blocked++;
				else
					open += dir;
			}

			return counted == 0 ? 0f : (float)blocked / counted;
		}

		/// <summary>The mean distance through the sculpt behind the hit, as a fraction of the thickness distance.</summary>
		float Thickness( Target g, MeshHit hit, Vec3 hn, uint seed )
		{
			var origin = hit.Point - hit.Normal * (_eps * 2f) - hn * _eps;
			var (bx, by) = Basis( -hn );
			var (jx, jy) = (Unit( seed ), Unit( Hash( seed ) ));
			var total = 0f;

			for ( var k = 0; k < _thickRays; k++ )
			{
				var h = g.Bvh.Raycast( g.High, origin, Cosine( k, _thickRays, jx, jy, -hn, bx, by ) );
				total += h is null ? _thickDistance : MathF.Min( h.Value.Distance, _thickDistance );
			}

			return total / (_thickRays * _thickDistance);
		}

		/// <summary>
		/// Curvature of the sculpt at this texel, 1 / units, convex positive: how fast its normal
		/// turns over a step either side, along both tangent directions, averaged. A sphere of radius
		/// R reads 1/R.
		/// </summary>
		static bool Curvature( Target g, Vec3 p, Vec3 n, Vec3 u, Vec3 v, float radius, out float curvature )
		{
			curvature = 0f;

			if ( radius <= 0f )
				return false;

			var sum = 0f;
			var count = 0;

			foreach ( var dir in new[] { u, v } )
			{
				if ( !Cast( g, p - dir * radius, n, out var a ) || !Cast( g, p + dir * radius, n, out var b ) )
					continue;

				var d = b.Point - a.Point;

				if ( d.LengthSquared < 1e-12f )
					continue;

				var na = SmoothNormal( g.High, g.Normals, a );
				var nb = SmoothNormal( g.High, g.Normals, b );
				sum += Vec3.Dot( nb - na, d ) / d.LengthSquared;
				count++;
			}

			if ( count == 0 )
				return false;

			curvature = sum / count;
			return true;
		}

		/// <summary>
		/// How far this texel's height stands above (positive) or below (negative) a ring of
		/// neighbours <paramref name="radius"/> away. Each neighbour's height is measured against the
		/// CAGE under it, not the tangent plane, so the cage's own curve is not mistaken for a pit
		/// or a ridge — only the detail the sculpt added counts.
		/// </summary>
		static bool Cavity( Target g, Vec3 p, Vec3 n, Vec3 u, Vec3 v, float radius, float height, out float cavity )
		{
			cavity = 0f;

			if ( radius <= 0f )
				return false;

			const int ring = 6;
			var sum = 0f;
			var count = 0;

			for ( var k = 0; k < ring; k++ )
			{
				var angle = k * (MathF.PI * 2f / ring);
				var q = p + (u * MathF.Cos( angle ) + v * MathF.Sin( angle )) * radius;

				if ( !Cast( g, q, n, out var high ) )
					continue;

				var low = g.LowBvh.Raycast( g.Low, q + n * g.Front, -n );

				if ( low is null )
					continue;

				sum += Vec3.Dot( high.Point - low.Value.Point, n );
				count++;
			}

			if ( count == 0 )
				return false;

			cavity = height - sum / count;
			return true;
		}

		BakeSet Finish()
		{
			var set = new BakeSet { Samples = _samples, Hits = _hitCount, AoDistance = _aoDistance, ThicknessDistance = _thickDistance };
			var n = _width * _height;
			var heights = new float[n];
			var lo = float.MaxValue;
			var hi = float.MinValue;

			for ( var i = 0; i < n; i++ )
			{
				if ( _hits[i] == 0 )
					continue;

				heights[i] = _heightSum[i] / _hits[i];
				lo = MathF.Min( lo, heights[i] );
				hi = MathF.Max( hi, heights[i] );
			}

			set.HeightMin = lo == float.MaxValue ? 0f : lo;
			set.HeightMax = hi == float.MinValue ? 0f : hi;

			if ( Has( BakeMaps.Normal ) )
				set.Normal = TangentMap( _normal );

			if ( Has( BakeMaps.BentNormal ) )
				set.BentNormal = TangentMap( _bent );

			if ( Has( BakeMaps.ObjectNormal ) )
			{
				// Object space has no "flat": an unhit texel is mid grey, (0, 0, 0), which no surface
				// can face, so a reader can tell it from data.
				set.ObjectNormal = Colour( i => _hits[i] > 0 && _object[i].LengthSquared > 1e-12f ? Half( _object[i].Normal ) : null, new Vec3( 0.5f, 0.5f, 0.5f ), _o.SixteenBit );
			}

			if ( Has( BakeMaps.Position ) )
			{
				var span = _boundsMax - _boundsMin;
				float Axis( float value, float min, float extent ) => extent > 1e-9f ? Math.Clamp( (value - min) / extent, 0f, 1f ) : 0.5f;
				set.PositionMin = _boundsMin;
				set.PositionMax = _boundsMax;
				set.Position = Colour( i =>
				{
					if ( _hits[i] == 0 )
						return null;

					var q = _hitPoint[i] / _hits[i];
					return new Vec3( Axis( q.x, _boundsMin.x, span.x ), Axis( q.y, _boundsMin.y, span.y ), Axis( q.z, _boundsMin.z, span.z ) );
				}, Vec3.Zero, _o.SixteenBit );
			}

			if ( Has( BakeMaps.MaterialId ) )
				set.MaterialId = IdMap( set );

			if ( Has( BakeMaps.AmbientOcclusion ) )
				set.AmbientOcclusion = Grey( i => _hits[i] > 0 ? _ao[i] / _hits[i] : (float?)null, 1f );

			if ( Has( BakeMaps.Height ) )
			{
				// Symmetric about mid grey, so 0.5 is always "on the cage" and one number — the range —
				// is all a parallax shader needs.
				var range = MathF.Max( MathF.Max( MathF.Abs( set.HeightMin ), MathF.Abs( set.HeightMax ) ), 1e-6f );
				set.HeightRange = range;
				set.Height = Grey( i => _hits[i] > 0 ? 0.5f + 0.5f * heights[i] / range : (float?)null, 0.5f, _o.SixteenBit );
			}

			if ( Has( BakeMaps.Curvature ) )
			{
				var range = Percentile( Values( _curv, _curvN, MathF.Abs ), 0.98f );
				set.CurvatureRange = range;
				set.Curvature = Grey( i => _curvN[i] > 0 ? 0.5f + 0.5f * Math.Clamp( _curv[i] / _curvN[i] / range, -1f, 1f ) : (float?)null, 0.5f );
			}

			if ( Has( BakeMaps.Cavity ) )
			{
				var range = Percentile( Values( _cav, _cavN, c => MathF.Max( 0f, -c ) ), 0.98f );
				set.CavityRange = range;
				set.Cavity = Grey( i => _cavN[i] > 0 ? 1f - Math.Clamp( -_cav[i] / _cavN[i] / range, 0f, 1f ) : (float?)null, 1f );
			}

			if ( Has( BakeMaps.Thickness ) )
			{
				set.Thickness = Grey( i => _hits[i] > 0 ? _thick[i] / _hits[i] : (float?)null, 0.5f );
				set.ThicknessUnits = new float[n];

				for ( var i = 0; i < n; i++ )
					set.ThicknessUnits[i] = _hits[i] > 0 ? _through[i] / _hits[i] : -1f;
			}

			if ( _hitNormal is not null )
			{
				var t = new BakeTexels
				{
					Width = _width, Height = _height, Hit = new bool[n], Point = new Vec3[n], Normal = new Vec3[n], Face = _hitFace,
					Occlusion = new float[n], Curvature = new float[n], Cavity = new float[n],
				};

				for ( var i = 0; i < n; i++ )
				{
					if ( _hits[i] == 0 )
						continue;

					t.Hit[i] = true;
					t.Point[i] = _hitPoint[i] / _hits[i];
					t.Normal[i] = _hitNormal[i].LengthSquared > 1e-12f ? _hitNormal[i].Normal : new Vec3( 0, 0, 1 );
					if ( _ao is not null ) t.Occlusion[i] = 1f - _ao[i] / _hits[i];
					if ( _curv is not null && _curvN[i] > 0 ) t.Curvature[i] = _curv[i] / _curvN[i];
					if ( _cav is not null && _cavN[i] > 0 ) t.Cavity[i] = MathF.Max( 0f, -_cav[i] / _cavN[i] );
				}

				set.Texels = t;
			}

			return set;
		}

		/// <summary>A tangent-space map (normal or bent normal) from per-texel sums, flat where nothing hit.</summary>
		BakedMap TangentMap( Vec3[] sums )
		{
			var map = new BakedMap( _width, _height );

			if ( _o.SixteenBit )
				map.EnableSixteenBit();

			for ( var i = 0; i < _width * _height; i++ )
			{
				// Flat where nothing was hit. An unhit texel reading as "no change from the cage"
				// is the harmless answer; leaving it black would light as a crease.
				var local = _hits[i] > 0 ? sums[i].Normal : new Vec3( 0, 0, 1 );

				if ( local.LengthSquared < 0.5f )
					local = new Vec3( 0, 0, 1 );

				Encode( map, i, local, _o.FlipGreen );
				map.Filled[i] = _hits[i] > 0;
			}

			return Finished( map );
		}

		/// <summary>
		/// The ID map: one flat colour per id, spread round the hue circle by the golden angle so ids
		/// next to each other in number are far apart in colour. Padded by copying, never averaging.
		/// </summary>
		BakedMap IdMap( BakeSet set )
		{
			var map = new BakedMap( _width, _height );
			var ids = new SortedSet<int>();

			for ( var i = 0; i < _id.Length; i++ )
				if ( _id[i] != NoId )
					ids.Add( _id[i] );

			set.IdColors = new Dictionary<int, (byte, byte, byte)>();
			var k = 0;

			foreach ( var id in ids )
				set.IdColors[id] = IdColour( k++ );

			for ( var i = 0; i < _id.Length; i++ )
			{
				if ( _id[i] == NoId )
					continue;

				var (r, g, b) = set.IdColors[_id[i]];
				map.Rgb[i * 3] = r;
				map.Rgb[i * 3 + 1] = g;
				map.Rgb[i * 3 + 2] = b;
				map.Filled[i] = true;
			}

			DilateNearest( map, _o.Padding );
			return map;
		}

		BakedMap Grey( Func<int, float?> value, float background, bool sixteen = false ) =>
			Colour( i => value( i ) is { } g ? new Vec3( g, g, g ) : null, new Vec3( background, background, background ), sixteen );

		BakedMap Colour( Func<int, Vec3?> value, Vec3 background, bool sixteen )
		{
			var map = new BakedMap( _width, _height );

			if ( sixteen )
				map.EnableSixteenBit();

			for ( var i = 0; i < _width * _height; i++ )
			{
				var c = value( i );
				Write( map, i, c ?? background );
				map.Filled[i] = c is not null;
			}

			return Finished( map );
		}

		BakedMap Finished( BakedMap map )
		{
			Dilate( map, _o.Padding );
			return map;
		}

		static Vec3 Half( Vec3 n ) => new( n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f );

		static List<float> Values( float[] sums, int[] counts, Func<float, float> f )
		{
			var list = new List<float>();

			for ( var i = 0; i < sums.Length; i++ )
				if ( counts[i] > 0 )
					list.Add( f( sums[i] / counts[i] ) );

			return list;
		}

		/// <summary>
		/// The value below which <paramref name="q"/> of the data falls: the normalisation for a map
		/// whose natural range is whatever the sculpt has. The top two percent clip, so one spike
		/// does not flatten everything else to grey.
		/// </summary>
		static float Percentile( List<float> values, float q )
		{
			if ( values.Count == 0 )
				return 1f;

			values.Sort();
			var v = values[Math.Clamp( (int)(q * (values.Count - 1)), 0, values.Count - 1 )];
			return v > 1e-9f ? v : MathF.Max( values[^1], 1e-6f );
		}
	}

	/// <summary>
	/// The k-th ID colour: hue stepped by the golden angle, full saturation, value alternating
	/// between full and three quarters so the 7th id is not a near-twin of the 1st.
	/// </summary>
	public static (byte R, byte G, byte B) IdColour( int k )
	{
		var h = (k * 0.618033988749895f) % 1f * 6f;
		var v = k % 2 == 0 ? 1f : 0.75f;
		var x = 1f - MathF.Abs( h % 2f - 1f );
		var (r, g, b) = (int)h switch
		{
			0 => (1f, x, 0f),
			1 => (x, 1f, 0f),
			2 => (0f, 1f, x),
			3 => (0f, x, 1f),
			4 => (x, 0f, 1f),
			_ => (1f, 0f, x),
		};

		return ((byte)MathF.Round( r * v * 255f ), (byte)MathF.Round( g * v * 255f ), (byte)MathF.Round( b * v * 255f ));
	}

	/// <summary>
	/// <paramref name="target"/> in the frame (t, b, n), solved exactly so that
	/// normalize(x·t + y·b + z·n) gives it back — not by dot products, which are only right when the
	/// frame is orthonormal, and an interpolated Mikk frame is not.
	/// </summary>
	static Vec3 TangentSpace( Vec3 target, Vec3 t, Vec3 b, Vec3 n )
	{
		var bn = Vec3.Cross( b, n );
		var det = Vec3.Dot( t, bn );

		if ( MathF.Abs( det ) > 1e-8f )
		{
			var solved = new Vec3( Vec3.Dot( target, bn ), Vec3.Dot( t, Vec3.Cross( target, n ) ), Vec3.Dot( t, Vec3.Cross( b, target ) ) ) / det;

			if ( solved.LengthSquared > 1e-12f )
				return solved.Normal;
		}

		return new Vec3( Vec3.Dot( target, t.Normal ), Vec3.Dot( target, b.Normal ), Vec3.Dot( target, n.Normal ) ).Normal;
	}

	static Vec3 AnyPerpendicular( Vec3 n )
	{
		var axis = MathF.Abs( n.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
		return Vec3.Cross( axis, n ).Normal;
	}

	/// <summary>Two unit vectors completing an orthonormal frame with <paramref name="n"/>.</summary>
	static (Vec3, Vec3) Basis( Vec3 n )
	{
		var x = AnyPerpendicular( n );
		return (x, Vec3.Cross( n, x ));
	}

	/// <summary>
	/// The k-th of <paramref name="count"/> cosine-weighted directions about <paramref name="n"/>: a
	/// Hammersley set, rotated per sample by (jx, jy). Stratified rather than random, so 16 rays
	/// converge about as well as 64 random ones, and rotated so neighbouring texels do not share one
	/// pattern and band.
	/// </summary>
	static Vec3 Cosine( int k, int count, float jx, float jy, Vec3 n, Vec3 bx, Vec3 by )
	{
		var e1 = ((k + 0.5f) / count + jx) % 1f;
		var e2 = (RadicalInverse( (uint)k ) + jy) % 1f;
		var r = MathF.Sqrt( e1 );
		var phi = e2 * MathF.PI * 2f;
		return (bx * (r * MathF.Cos( phi )) + by * (r * MathF.Sin( phi )) + n * MathF.Sqrt( MathF.Max( 0f, 1f - e1 ) )).Normal;
	}

	static float RadicalInverse( uint bits )
	{
		bits = (bits << 16) | (bits >> 16);
		bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
		bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
		bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
		bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
		return bits * 2.3283064365386963e-10f;
	}

	static uint Hash( uint x )
	{
		x ^= x >> 16;
		x *= 0x7feb352du;
		x ^= x >> 15;
		x *= 0x846ca68bu;
		x ^= x >> 16;
		return x;
	}

	static float Unit( uint x ) => (Hash( x ) >> 8) * (1f / 16777216f);

	// --- the check the bake depends on ----------------------------------------------------------

	/// <summary>
	/// What a mesh's UVs look like as an atlas: how much of the square they cover, how much they
	/// cover twice, and whether they stay inside it at all.
	/// </summary>
	public static UVCoverage Measure( PolyMesh mesh, int resolution = 256 )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( resolution < 1 )
			throw new ArgumentOutOfRangeException( nameof( resolution ) );

		var counts = new int[resolution * resolution];
		var outside = 0;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			var escapes = false;

			foreach ( var uv in face.UVs )
			{
				if ( uv.x < -1e-4f || uv.x > 1f + 1e-4f || uv.y < -1e-4f || uv.y > 1f + 1e-4f )
					escapes = true;
			}

			if ( escapes )
				outside++;

			// One face must not count a texel twice, however many fan triangles cover it.
			var mine = new HashSet<int>();

			foreach ( var (a, b, c) in FaceTriangles( mesh, fi ) )
			{
				Rasterise( face.UVs[a], face.UVs[b], face.UVs[c], resolution, resolution,
					( x, y, _, _, _ ) => mine.Add( y * resolution + x ) );
			}

			foreach ( var index in mine )
				counts[index]++;
		}

		var covered = 0;
		var overlapping = 0;

		foreach ( var n in counts )
		{
			if ( n > 0 )
				covered++;

			if ( n > 1 )
				overlapping++;
		}

		return new UVCoverage( resolution, covered, overlapping, outside, mesh.FaceCount );
	}
}

/// <summary>
/// The verdict on a mesh's UVs as a bake target.
///
/// The plan has said since it was written that a bake needs non-overlapping UVs and that nothing
/// checked it. This is that check. It is worth running before a bake rather than after, because an
/// overlapping bake does not fail — it produces a map that looks plausible and is wrong wherever two
/// faces shared a texel.
/// </summary>
public sealed class UVCoverage
{
	public readonly int Resolution;
	public readonly int CoveredTexels;
	public readonly int OverlappingTexels;
	public readonly int FacesOutsideTheSquare;
	public readonly int FaceCount;

	public UVCoverage( int resolution, int covered, int overlapping, int outside, int faces )
	{
		Resolution = resolution;
		CoveredTexels = covered;
		OverlappingTexels = overlapping;
		FacesOutsideTheSquare = outside;
		FaceCount = faces;
	}

	public float CoveredFraction => (float)CoveredTexels / (Resolution * Resolution);

	public float OverlapFraction => CoveredTexels == 0 ? 0f : (float)OverlappingTexels / CoveredTexels;

	/// <summary>Whether these UVs can carry a bake at all.</summary>
	public bool CanBake => OverlappingTexels == 0 && FacesOutsideTheSquare == 0 && CoveredTexels > 0;

	/// <summary>The refusal, in the shape a diagnostic wants: what is wrong, with this model's numbers.</summary>
	public string Problem
	{
		get
		{
			if ( CoveredTexels == 0 )
				return "These UVs cover none of the texture square, so a bake would have nowhere to write.";

			if ( FacesOutsideTheSquare > 0 )
				return $"{FacesOutsideTheSquare} of {FaceCount} faces have UVs outside the 0-1 square. "
					+ "Box projection tiles by design and is meant for repeating a texture, not for a bake.";

			if ( OverlappingTexels > 0 )
				return $"{OverlappingTexels} texels ({OverlapFraction:P0} of those covered) are claimed by more "
					+ "than one face. A bake writes both and keeps whichever ran last.";

			return null;
		}
	}
}
