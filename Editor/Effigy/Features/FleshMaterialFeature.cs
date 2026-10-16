using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>
/// Flesh Material: a layered organic material baked onto a body's UVs - wet flesh, raw meat, lung,
/// gut, bone, tooth, gum - as an albedo and a roughness texture and a Complex .vmat (see
/// <see cref="FleshMaterial"/> for the layers and the masks they ride on).
///
///   add FleshMaterial bodies=Sac preset=raw_meat resolution=2048 folder=materials/creature
///
/// THE MAPS FOLLOW THE MESH. Cavity, occlusion and height are read off the body as built, so the
/// material is a feature: sculpt a deeper crease upstream and the crease darkens on the next rebuild.
/// A body with no UVs is unwrapped first (and the feature says so), because a texture has nowhere
/// to go otherwise.
///
/// WHERE THE FILES GO. With Folder set, each body writes flesh_&lt;body&gt;_color.png, _rough.png and
/// .vmat there - relative to the working directory, which for `effigy.sh script` is the caller's -
/// and the .vmat names the PNGs by that same relative path, the way an asset path is written. With
/// Show on model on, the albedo also goes onto the body as its paint canvas so the editor can show
/// it; any paint already there is read first, as the Painted mask.
/// </summary>
public sealed class FleshMaterialFeature : Feature
{
	public override string TypeName => "Flesh Material";

	public override GeometryKind Accepts => GeometryKind.Body;

	/// <summary>Which bodies. Empty is every body.</summary>
	public readonly BodySelectionParam Bodies = new( "On" );

	public readonly ChoiceParam Preset = new( "Preset", FleshMaterial.Presets );

	public readonly IntParam Resolution = new( "Resolution", 1024, 64, 4096 );

	/// <summary>Multiplies every noise feature size: 2 is blobs, pits and veins twice as big. The
	/// presets are measured at life size in inches.</summary>
	public readonly FloatParam Detail = new( "Detail size", 1f, 0.05f, 20f );

	public readonly IntParam Seed = new( "Seed", 1, 0, 9999 );

	/// <summary>Where the PNGs and .vmat are written. Empty writes nothing.</summary>
	public readonly StringParam Folder = new( "Folder", "" );

	public readonly BoolParam Show = new( "Show on model", true );

	/// <summary>Colour the vertices instead of baking maps: no UVs needed, nothing unwrapped, no files.
	/// For previewing Blob meshes and for meshes headed to a CUSTOM shader that reads vertex colour.
	/// Detail finer than the edges is lost.
	///
	/// NOT FOR AN s&amp;box GAME MODEL AS IS: complex.vfx and skin.shader do not read the COLOR stream,
	/// so the exported model shows its material's textures and none of this. The feature says so
	/// with a warning whenever it is on; turn it off to bake textures instead.</summary>
	public readonly BoolParam PerVertex = new( "Per vertex", false );

	/// <summary>Corruption seed points, "x,y,z; x,y,z". Empty reads corruption from the vertex colours' red
	/// channel (the packed-mask convention). Drives Infected skin; any preset can read it.</summary>
	public readonly StringParam Seeds = new( "Corruption seeds", "" );

	/// <summary>How far the corruption spreads over the surface from each seed, in inches.</summary>
	public readonly FloatParam Reach = new( "Corruption reach", 12f, 0.1f, 1000f );

	/// <summary>Width of its ragged front, in inches. As wide as the reach (the default) ramps corruption
	/// from the seed to the edge, so Infected skin bands skin → bruise → raw → oil; a narrow front turns
	/// everything inside fully and leaves a sharp edge.</summary>
	public readonly FloatParam Front = new( "Corruption front", 12f, 0.05f, 500f );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Preset, Resolution, Detail, Seed, Folder, Show, PerVertex, Seeds, Reach, Front };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Seed, Show, PerVertex, Seeds, Reach, Front };

	/// <summary>The stack this feature applies.</summary>
	public FleshStack Stack => FleshMaterial.Preset( PresetName );

	/// <summary>Per-vertex results last baked, by body id (Per vertex on).</summary>
	public IReadOnlyDictionary<string, FleshVertexMaterial> VertexMaterials => _vertex;

	readonly Dictionary<string, FleshVertexMaterial> _vertex = new();
	readonly Dictionary<string, Vec4[]> _shownColours = new();
	readonly Dictionary<string, Vec4[]> _underColours = new();

	public string PresetName => FleshMaterial.Presets[Math.Clamp( Preset.Index, 0, FleshMaterial.Presets.Length - 1 )];

	/// <summary>The maps last baked, by body id.</summary>
	public IReadOnlyDictionary<string, FleshMaps> Maps => _maps;

	/// <summary>Files the last rebuild wrote or found already current.</summary>
	public IReadOnlyList<string> Written { get; private set; } = Array.Empty<string>();

	/// <summary>The material slots on the bodies this covers, for binding the .vmat.</summary>
	public IReadOnlyList<int> ResolvedSlots { get; private set; } = Array.Empty<int>();

	readonly Dictionary<string, FleshMaps> _maps = new();
	readonly Dictionary<string, string> _recipes = new();
	readonly Dictionary<string, PaintCanvas> _shown = new();
	readonly Dictionary<string, PaintCanvas> _under = new();

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );
		var written = new List<string>();
		var unwrapped = new List<string>();
		var perVertex = new List<string>();

		foreach ( var body in targets )
		{
			var mesh = body.Mesh;
			if ( mesh is null || mesh.Faces.Count == 0 ) continue;

			// The canvas this feature put on the body last time is not paint: if the body came back
			// from the cache still wearing it, read the paint that was under it instead.
			if ( _shown.TryGetValue( body.Id, out var shown ) && ReferenceEquals( mesh.Paint, shown ) )
				mesh.Paint = _under.GetValueOrDefault( body.Id );

			if ( _shownColours.TryGetValue( body.Id, out var shownColours ) && ReferenceEquals( mesh.VertexColors, shownColours ) )
				mesh.VertexColors = _underColours.GetValueOrDefault( body.Id );

			var corruption = CorruptionFor( mesh );

			if ( PerVertex.Value )
			{
				var vm = FleshMaterial.BakeVertices( mesh, Stack, Detail.Clamped, Seed.Clamped, corruption );
				_vertex[body.Id] = vm;
				perVertex.Add( body.Name ?? body.Id );
				if ( Show.Value )
				{
					_underColours[body.Id] = mesh.VertexColors;
					mesh.VertexColors = _shownColours[body.Id] = vm.Colour;
				}
				continue;
			}

			if ( !HasUvArea( mesh ) )
			{
				UVUnwrap.Unwrap( mesh );
				unwrapped.Add( body.Name ?? body.Id );
			}

			// A recipe over everything the bake reads, so a rebuild for an unrelated feature does not
			// pay for the bake again.
			var recipe = $"{PresetName}|{Resolution.Clamped}|{Detail.Clamped}|{Seed.Clamped}|{Seeds.Value}|{Reach.Clamped}|{Front.Clamped}|{VertexHash( mesh )}|{MultiresSculpt.TopologyId( mesh )}|{AtlasId.Of( mesh )}|{PositionHash( mesh )}|{PaintHash( mesh.Paint )}";

			if ( !_maps.TryGetValue( body.Id, out var maps ) || !_recipes.TryGetValue( body.Id, out var was ) || was != recipe )
			{
				maps = FleshMaterial.Bake( mesh, Stack, Resolution.Clamped, Detail.Clamped, Seed.Clamped, corruption );
				_maps[body.Id] = maps;
				_recipes[body.Id] = recipe;
			}

			if ( maps.Covered.All( c => !c ) )
			{
				Fail(
					"The material has nowhere to go",
					$"{body.Name ?? body.Id} has no faces with a UV area, even after unwrapping.",
					"Check the part is a closed surface with real faces" );
			}

			if ( !string.IsNullOrWhiteSpace( Folder.Value ) )
				written.AddRange( WriteFiles( body, maps ) );

			if ( Show.Value )
			{
				_under[body.Id] = mesh.Paint;
				mesh.Paint = _shown[body.Id] = maps.ToCanvas();
			}
		}

		Written = written;
		ResolvedSlots = targets.Where( b => b.Mesh is not null ).SelectMany( b => b.Mesh.Faces ).Select( f => f.Material )
			.Where( s => !ctx.ShellSlots.Contains( s ) ).Distinct().OrderBy( s => s ).ToList();

		if ( unwrapped.Count > 0 )
			Warn( "Unwrapped first", $"{string.Join( ", ", unwrapped )} had no UVs, so it was unwrapped to have somewhere to bake.",
				"Unwrap it yourself upstream to choose the seams" );

		// Said every time, not once: the colours look right in Effigy and the exported model is
		// written with them (raw RGBA), so nothing else would ever tell you the game ignores them.
		if ( perVertex.Count > 0 )
			Warn( "Vertex colour does not show in s&box",
				$"{string.Join( ", ", perVertex )}: Per vertex puts the material in the vertex colours, which only Effigy's viewport "
				+ "and a custom shader read. s&box's complex and skin shaders ignore vertex colour, so the game model shows plain material.",
				"Turn Per vertex off to bake colour and roughness textures (it unwraps the body if it has no UVs)" );
	}

	IEnumerable<string> WriteFiles( Body body, FleshMaps maps )
	{
		var folder = Folder.Value.Trim().Replace( '\\', '/' ).TrimEnd( '/' );
		var dir = Path.GetFullPath( folder );
		Directory.CreateDirectory( dir );

		// The .vmat names its images as an asset path: the folder as given, or from Assets/ on when
		// an absolute folder was given inside a project.
		var assetFolder = folder;
		var cut = folder.IndexOf( "/Assets/", StringComparison.OrdinalIgnoreCase );
		if ( cut >= 0 ) assetFolder = folder[(cut + 8)..];
		else if ( Path.IsPathRooted( folder ) ) assetFolder = "";

		var tag = Safe( body.Name ?? body.Id );
		var color = $"flesh_{tag}_color.png";
		var rough = $"flesh_{tag}_rough.png";
		var vmat = $"flesh_{tag}.vmat";
		string Asset( string file ) => assetFolder.Length == 0 ? file : $"{assetFolder}/{file}";

		WriteIfChanged( Path.Combine( dir, color ), PngWriter.ToBytesRgba( maps.Albedo, maps.Size, maps.Size ) );
		WriteIfChanged( Path.Combine( dir, rough ), PngWriter.ToBytesRgba( maps.Roughness, maps.Size, maps.Size ) );
		var files = new List<string> { Path.Combine( dir, color ), Path.Combine( dir, rough ) };

		// The glow goes in as a grey mask; the engine colours it from the albedo (SelfIllumAlbedoFactor 1).
		string glow = null;
		if ( maps.Emission is { } emission )
		{
			glow = $"flesh_{tag}_glow.png";
			var grey = new byte[emission.Length];
			for ( var i = 0; i < grey.Length; i += 4 )
			{
				var g = Math.Max( emission[i], Math.Max( emission[i + 1], emission[i + 2] ) );
				grey[i] = grey[i + 1] = grey[i + 2] = g;
				grey[i + 3] = 255;
			}
			WriteIfChanged( Path.Combine( dir, glow ), PngWriter.ToBytesRgba( grey, maps.Size, maps.Size ) );
			files.Add( Path.Combine( dir, glow ) );
		}

		var stack = Stack;
		WriteIfChanged( Path.Combine( dir, vmat ), System.Text.Encoding.UTF8.GetBytes(
			FleshMaterial.VmatSource( Asset( color ), Asset( rough ), stack, stack.Metalness, glow is null ? null : Asset( glow ) ) ) );
		files.Add( Path.Combine( dir, vmat ) );

		return files;
	}

	float[] CorruptionFor( PolyMesh mesh )
	{
		var seeds = ParsePoints( Seeds.Value );
		if ( seeds.Count == 0 ) return null;
		return FleshMaterial.Corruption( mesh, seeds, Reach.Clamped, Front.Clamped, seed: Seed.Clamped );
	}

	/// <summary>"x,y,z; x,y,z" (spaces or semicolons between points) as points. Bad entries are skipped.</summary>
	internal static List<Vec3> ParsePoints( string text )
	{
		var o = new List<Vec3>();
		foreach ( var part in (text ?? "").Split( new[] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries ) )
		{
			var c = part.Split( ',' );
			if ( c.Length == 3
				&& float.TryParse( c[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x )
				&& float.TryParse( c[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y )
				&& float.TryParse( c[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z ) )
				o.Add( new Vec3( x, y, z ) );
		}
		return o;
	}

	static long VertexHash( PolyMesh mesh )
	{
		if ( !mesh.HasVertexColors ) return 0;
		unchecked
		{
			long h = 17;
			foreach ( var c in mesh.VertexColors )
				h = (h ^ BitConverter.SingleToInt32Bits( c.x + c.y * 3f + c.z * 7f + c.w * 11f )) * 1099511628211;
			return h;
		}
	}

	static void WriteIfChanged( string path, byte[] bytes )
	{
		if ( File.Exists( path ) && File.ReadAllBytes( path ).AsSpan().SequenceEqual( bytes ) ) return;
		File.WriteAllBytes( path, bytes );
	}

	static bool HasUvArea( PolyMesh mesh )
	{
		var area = 0f;
		foreach ( var f in mesh.Faces )
		{
			if ( f.UVs is null || f.UVs.Length != f.Count ) continue;
			for ( var i = 1; i < f.Count - 1; i++ )
			{
				var a = f.UVs[0]; var b = f.UVs[i]; var c = f.UVs[i + 1];
				area += MathF.Abs( (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y) ) * 0.5f;
			}
		}
		return area > 1e-4f;
	}

	static long PositionHash( PolyMesh mesh )
	{
		unchecked
		{
			long h = 1469598103934665603;
			foreach ( var p in mesh.Positions )
			{
				h = (h ^ BitConverter.SingleToInt32Bits( p.x )) * 1099511628211;
				h = (h ^ BitConverter.SingleToInt32Bits( p.y )) * 1099511628211;
				h = (h ^ BitConverter.SingleToInt32Bits( p.z )) * 1099511628211;
			}
			return h;
		}
	}

	static long PaintHash( PaintCanvas canvas )
	{
		if ( canvas is null ) return 0;
		unchecked
		{
			long h = canvas.Width * 31 + canvas.Height;
			for ( var i = 3; i < canvas.Rgba.Length; i += 4 * 7 ) h = h * 31 + canvas.Rgba[i];
			return h;
		}
	}

	static string Safe( string name )
	{
		var chars = (name ?? "part").Trim().ToLowerInvariant().Select( c => char.IsLetterOrDigit( c ) ? c : '_' ).ToArray();
		return chars.Length == 0 ? "part" : new string( chars );
	}
}
