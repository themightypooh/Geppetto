using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>
/// Flesh Material: a layered organic material baked onto a body's UVs - wet flesh, raw meat, lung,
/// gut, bone, tooth, gum - as an albedo and a roughness texture and a .vmat (see
/// <see cref="FleshMaterial"/> for the layers and the masks they ride on).
///
///   add FleshMaterial bodies=Sac preset=raw_meat resolution=2048 folder=materials/creature [shader=skin|complex] [lightthrough=2]
///
/// THE SHADER FOLLOWS THE PRESET unless set. Living tissue goes out on s&amp;box's skin shader (SSS,
/// the red AO bleed, a wet second lobe, light through thin walls), with an occlusion map and a
/// transmission mask baked from the wall thickness; bone, tooth, oil, spikes and the glowing ribbon
/// go out on complex (see FleshMaterial.WithShader). The vmat turns morphs on when the body has
/// shape keys.
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

	/// <summary>Which s&amp;box shader the .vmat is for. Preset's: skin for living tissue, complex for
	/// bone, enamel and the glowing or see-through presets.</summary>
	public readonly ChoiceParam Shader = new( "Shader", new[] { "Preset's", "Skin", "Complex" } );

	/// <summary>How far light gets through thin walls on the skin shader, times the preset's own
	/// (1 is real skin: a 1 mm wall passes a third of the red, 3 mm nothing). 0 turns it off.</summary>
	public readonly FloatParam LightThrough = new( "Light through", 1f, 0f, 20f );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Preset, Shader, Resolution, Detail, LightThrough, Seed, Folder, Show, PerVertex, Seeds, Reach, Front };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { LightThrough, Seed, Show, PerVertex, Seeds, Reach, Front };

	/// <summary>The shader the .vmat is written for: the choice, or the preset's.</summary>
	public FleshShader ShaderUsed => Shader.Index switch
	{
		1 => FleshShader.Skin,
		2 => FleshShader.Complex,
		_ => Stack.Shader,
	};

	/// <summary>The k the transmission mask is baked with: the preset's (1 where the preset has none
	/// and skin was asked for) times Light through.</summary>
	public float LightThroughUsed
	{
		get
		{
			var k = Stack.LightThrough > 0f ? Stack.LightThrough : 1f;
			return k * LightThrough.Clamped;
		}
	}

	/// <summary>The skin shader's maps last baked, by body id (Shader skin, Folder set).</summary>
	public IReadOnlyDictionary<string, FleshShaderMaps> ShaderMaps => _shaderMaps;

	readonly Dictionary<string, FleshShaderMaps> _shaderMaps = new();
	readonly Dictionary<string, string> _shaderRecipes = new();

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

	/// <summary>The asset path of the .vmat the last rebuild wrote (the first body's), or null. What
	/// the export binds the covered slots to unless the slot has a material of its own.</summary>
	public string VmatAsset { get; private set; }

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
		VmatAsset = null;
		var unwrapped = new List<string>();
		var unwrappedIds = new List<string>();
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
				unwrappedIds.Add( body.Id );
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
				written.AddRange( WriteFiles( body, maps, recipe ) );

			if ( Show.Value )
			{
				_under[body.Id] = mesh.Paint;
				mesh.Paint = _shown[body.Id] = maps.ToCanvas();
			}
		}

		Written = written;
		ResolvedSlots = targets.Where( b => b.Mesh is not null ).SelectMany( b => b.Mesh.Faces ).Select( f => f.Material )
			.Where( s => !ctx.ShellSlots.Contains( s ) ).Distinct().OrderBy( s => s ).ToList();

		// Least pressing first: a later Warn replaces the diagnostic, so the callout shows the one that
		// matters most.
		var baked = targets.Count - perVertex.Count;

		if ( baked > 0 && string.IsNullOrWhiteSpace( Folder.Value ) )
			Warn( RemedyAction.ChooseFolder( "Choose folder", Folder.Label ),
				"No texture files written",
				"Folder is empty, so the colour and roughness maps are only shown here. The exported model will not have this material.",
				"Choose a folder for the PNGs and .vmat (inside your project's Assets)" );

		if ( unwrapped.Count > 0 )
			Warn( RemedyAction.InsertFeatureAbove( "Unwrap above", "UVProject", unwrappedIds, ("mode", "Organic") ),
				"Unwrapped first", $"{string.Join( ", ", unwrapped )} had no UVs, so it was unwrapped to have somewhere to bake.",
				"Unwrap it yourself upstream to choose the seams" );

		if ( ShaderUsed == FleshShader.Skin && !PerVertex.Value )
		{
			var stack = Stack;
			var dropped = new List<string>();
			if ( stack.Layers.Any( l => l.Emission > 0f ) ) dropped.Add( "glow" );
			if ( stack.Translucency > 0f ) dropped.Add( "see-through" );
			if ( dropped.Count > 0 )
				Warn( $"The skin shader drops the {string.Join( " and ", dropped )}",
					$"{PresetName} has {string.Join( " and ", dropped )}, which the skin shader cannot do: it has no glow, and it refuses see-through alongside its subsurface scattering.",
					"Set Shader to Complex (or Preset's) to keep them" );

			if ( string.IsNullOrWhiteSpace( Folder.Value ) )
				Warn( RemedyAction.ChooseFolder( "Choose folder", Folder.Label ),
					"No texture files written",
					"Folder is empty, so the maps are only shown here, and the skin shader's occlusion and light-through maps are baked with the .vmat. The exported model will not have this material.",
					"Choose a folder for the PNGs and .vmat (inside your project's Assets)" );

			foreach ( var (id, sm) in _shaderMaps )
			{
				if ( sm.TransmissionPerVertex is not { Length: > 0 } t ) continue;
				var lit = t.Count( v => v > 0.02f ) / (float)t.Length;
				// realtime-flesh-shading.md §3.3: the human head transmits on ~1.5% of its texels.
				if ( lit > 0.4f )
					Warn( "Light comes through most of it",
						$"{ctx.Bodies.FirstOrDefault( b => b.Id == id )?.Name ?? id}: {lit:P0} of the surface lets light through, so the whole part will glow. Real skin does it only on thin parts (ears, membranes).",
						"Lower Light through, or thicken the walls" );
			}
		}

		// Said every time, not once: the colours look right in Effigy and the exported model is
		// written with them (raw RGBA), so nothing else would ever tell you the game ignores them.
		if ( perVertex.Count > 0 )
			Warn( "Vertex colour does not show in s&box",
				$"{string.Join( ", ", perVertex )}: Per vertex puts the material in the vertex colours, which only Effigy's viewport "
				+ "and a custom shader read. s&box's complex and skin shaders ignore vertex colour, so the game model shows plain material.",
				"Turn Per vertex off to bake colour and roughness textures (it unwraps the body if it has no UVs)" );
	}

	IEnumerable<string> WriteFiles( Body body, FleshMaps maps, string recipe )
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
		string source;

		if ( ShaderUsed == FleshShader.Skin )
		{
			var k = LightThrough.Clamped > 0f ? LightThroughUsed : 0f;
			var shaderRecipe = $"{recipe}|{k}";
			if ( !_shaderMaps.TryGetValue( body.Id, out var sm ) || _shaderRecipes.GetValueOrDefault( body.Id ) != shaderRecipe )
			{
				sm = FleshMaterial.BakeSkinMaps( body.Mesh, maps.Size, k );
				_shaderMaps[body.Id] = sm;
				_shaderRecipes[body.Id] = shaderRecipe;
			}

			var ao = $"flesh_{tag}_ao.png";
			WriteIfChanged( Path.Combine( dir, ao ), PngWriter.ToBytes( sm.Ao.Rgb, sm.Ao.Width, sm.Ao.Height ) );
			files.Add( Path.Combine( dir, ao ) );

			string trans = null;
			if ( sm.Transmission is not null )
			{
				trans = $"flesh_{tag}_transmission.png";
				WriteIfChanged( Path.Combine( dir, trans ), PngWriter.ToBytes( sm.Transmission.Rgb, sm.Transmission.Width, sm.Transmission.Height ) );
				files.Add( Path.Combine( dir, trans ) );
			}

			source = FleshMaterial.SkinVmatSource( Asset( color ), Asset( rough ), stack, Asset( ao ),
				trans is null ? null : Asset( trans ), morphs: body.Mesh.HasMorphs );
		}
		else
		{
			source = FleshMaterial.VmatSource( Asset( color ), Asset( rough ), stack, stack.Metalness, glow is null ? null : Asset( glow ) );
		}

		WriteIfChanged( Path.Combine( dir, vmat ), System.Text.Encoding.UTF8.GetBytes( source ) );
		VmatAsset ??= Asset( vmat );
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
