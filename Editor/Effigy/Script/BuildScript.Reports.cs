using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// The measuring verbs an agent reads a model through, beside describe/measure/symmetry:
///
///   topology [Body|all] [poles=12]                       face kinds, poles, edge evenness, quad shape,
///                                                         manifold, symmetry, loops per joint
///   section Body|all z=10,20,30 | x=… | y=… | along=Bone [at=0,0.5,1]
///                                                         width, depth, perimeter and area of each slice;
///                                                         a list may be a range: z=0..60:7
///   flex Body|all [bones=a,b] [angles=90,140] [axis=auto|x|y|z|x,y,z] [render=out/flex] [views=4] [size=320]
///                                                         bend each joint, report volume kept and new crossings
///   match front=ref_front.png side=ref_side.png [out=dir] several views at once, with a combined score
///
/// Reports, not gates: a model that misses a target still runs the script on; the report marks
/// the line with "!". A verb fails only when it cannot measure at all, and then says why.
/// </summary>
public static partial class BuildScript
{
	const string SectionUsage = "section Body|all z=10,20,30 | x=… | y=… | along=Bone [at=0,0.5,1] — a list may be a range, z=0..60:7";
	const string FlexUsage = "flex Body|all [bones=a,b] [angles=90,140] [axis=auto|x|y|z|x,y,z] [render=out/flex] [views=4] [size=320]";

	static bool Topology( PartStudio studio, List<string> args, Result r )
	{
		var named = args.Where( a => !a.Contains( '=' ) ).ToList();
		var opts = Pairs( args.Where( a => a.Contains( '=' ) ) );

		foreach ( var key in opts.Keys )
			if ( key is not "poles" )
				return Fail( r, $"topology has no option '{key}'. topology [Body|all] [poles=12]" );

		var maxPoles = (int)Number( opts.GetValueOrDefault( "poles", "12" ) );

		if ( !Targets( studio, named.FirstOrDefault(), out var bodies, out var why ) )
			return Fail( r, why );

		foreach ( var body in bodies )
		{
			var name = body.Name ?? body.Id;
			var report = TopologyReport.Measure( body.Mesh, name, studio.Rig );
			foreach ( var line in report.Describe( maxPoles ).Split( '\n' ) )
				r.Log.Add( line );
		}

		return true;
	}

	static bool Section( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 2 || args[0].Contains( '=' ) )
			return Fail( r, $"section needs a body (or all) and where to cut: {SectionUsage}" );

		if ( !Targets( studio, args[0], out var bodies, out var why ) )
			return Fail( r, why );

		var opts = Pairs( args.Skip( 1 ) );

		foreach ( var key in opts.Keys )
			if ( key is not ("x" or "y" or "z" or "along" or "at") )
				return Fail( r, $"section has no option '{key}'. {SectionUsage}" );

		var meshes = bodies.Select( b => b.Mesh ).ToList();
		var label = args[0];
		var slices = new List<SectionReport.Slice>();

		foreach ( var (axisName, normal) in new[] { ("x", new Vec3( 1, 0, 0 )), ("y", new Vec3( 0, 1, 0 )), ("z", new Vec3( 0, 0, 1 )) } )
		{
			if ( !opts.TryGetValue( axisName, out var listText ) )
				continue;

			if ( !NumberList( listText, out var values, out var whyList ) )
				return Fail( r, $"{axisName}={listText}: {whyList}" );

			foreach ( var value in values )
				slices.Add( SectionReport.Cut( meshes, normal * value, normal, label: $"{label} {axisName}={N( value )}" ) );
		}

		if ( opts.TryGetValue( "along", out var boneName ) )
		{
			if ( studio.Rig is not { Count: > 0 } rig )
				return Fail( r, $"along={boneName} needs a rig, and this studio has no bones. Cut at a height instead (z=…), or add bones first." );

			var bone = ModelMeasure.FindBone( rig, boneName );
			if ( bone < 0 )
				return Fail( r, $"No bone called '{boneName}'. Bones: {string.Join( ", ", rig.Bones.Select( b => b.Name ) )}." );

			if ( !NumberList( opts.GetValueOrDefault( "at", "0.5" ), out var ts, out var whyAt ) )
				return Fail( r, $"at={opts["at"]}: {whyAt} (fractions along the bone, 0 at its head, 1 at its tail)" );

			var head = rig.HeadWorld( bone );
			var tail = rig.TailWorld( bone );
			var dir = rig.BoneDirection( bone );

			foreach ( var t in ts )
				slices.Add( SectionReport.Cut( meshes, head + (tail - head) * t, dir, rig.WorldBind( bone ).X, $"{label} along {rig.Bones[bone].Name} at {N( t )}" ) );
		}
		else if ( opts.ContainsKey( "at" ) )
		{
			return Fail( r, $"at= is a place along a bone; name the bone with along=. {SectionUsage}" );
		}

		if ( slices.Count == 0 )
			return Fail( r, $"section needs where to cut. {SectionUsage}" );

		if ( slices.All( s => !s.Hit ) )
		{
			var (min, max) = ModelMeasure.Bounds( bodies );
			return Fail( r, $"No slice touches {label}: it spans x {N( min.x )}..{N( max.x )}, y {N( min.y )}..{N( max.y )}, z {N( min.z )}..{N( max.z )}. Cut inside that." );
		}

		foreach ( var slice in slices )
			foreach ( var line in slice.Describe().Split( '\n' ) )
				r.Log.Add( line );

		return true;
	}

	static bool Flex( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count < 1 || args[0].Contains( '=' ) )
			return Fail( r, $"flex needs a body (or all): {FlexUsage}" );

		if ( studio.Rig is not { Count: > 0 } rig )
			return Fail( r, "flex bends the rig's joints, and this studio has no bones. Add bones first (the Rig workspace, or import a skeleton there), then flex." );

		var opts = Pairs( args.Skip( 1 ) );

		foreach ( var key in opts.Keys )
			if ( key is not ("bones" or "angles" or "axis" or "render" or "views" or "size") )
				return Fail( r, $"flex has no option '{key}'. {FlexUsage}" );

		var all = args[0].Equals( "all", StringComparison.OrdinalIgnoreCase );
		var target = all ? null : ModelMeasure.FindBody( studio, args[0] );

		if ( !all && target?.Mesh is null )
			return Fail( r, $"No body called '{args[0]}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}." );

		if ( !all && target.IsReference )
			return Fail( r, $"{args[0]} is a reference (a wearer), which is never skinned. Flex the body that is." );

		// The bind export makes, over the whole model, so painted weights line up; then this body's
		// share of it.
		var (merged, ranges) = studio.ToMeshWithBodies();
		if ( merged.VertexCount == 0 )
			return Fail( r, "Nothing to flex: no body has vertices." );

		var weights = SkinBinder.SmoothWeights( merged, SkinBinder.BindBodies( merged, ranges, studio.BodyBoneMap, rig ) );

		if ( studio.WeightPaint is { Count: > 0 } paint )
		{
			paint.Apply( merged, weights, rig, out var missing );
			if ( paint.IsStale ) r.Log.Add( $"flex: painted weights not used - the mesh changed since they were painted ({paint.Count} vertices)" );
			else if ( missing.Count > 0 ) r.Log.Add( $"flex: painted weights for missing bone(s) {string.Join( ", ", missing.Take( 4 ) )} were not used" );
		}

		var mesh = merged;
		if ( !all )
		{
			var range = ranges.FirstOrDefault( x => x.BodyId == target.Id );
			if ( range.Count == 0 )
				return Fail( r, $"{args[0]} has no vertices in the skinned model." );
			(mesh, weights) = Part( merged, weights, range );
		}

		var name = all ? "model" : args[0];

		List<int> bones;
		if ( opts.TryGetValue( "bones", out var boneText ) )
		{
			bones = new List<int>();
			foreach ( var boneName in boneText.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
			{
				var index = ModelMeasure.FindBone( rig, boneName );
				if ( index < 0 )
					return Fail( r, $"No bone called '{boneName}'. Bones: {string.Join( ", ", rig.Bones.Select( b => b.Name ) )}." );
				if ( rig.Bones[index].Parent < 0 )
					return Fail( r, $"{boneName} is a root bone: it has no joint to bend at. Name a bone below it." );
				bones.Add( index );
			}
		}
		else
		{
			bones = FlexReport.DefaultBones( rig, weights );
			if ( bones.Count == 0 )
				return Fail( r, $"No joint of the rig has {name}'s skin on both sides of it, so there is nothing to bend. Bind the body to more than one bone, or name bones= to bend." );
		}

		if ( !NumberList( opts.GetValueOrDefault( "angles", "90,140" ), out var angles, out var whyAngles ) )
			return Fail( r, $"angles={opts["angles"]}: {whyAngles}" );

		Vec3? axis = null;
		if ( opts.TryGetValue( "axis", out var axisText ) && !axisText.Equals( "auto", StringComparison.OrdinalIgnoreCase ) )
		{
			axis = axisText.ToLowerInvariant() switch
			{
				"x" => new Vec3( 1, 0, 0 ),
				"y" => new Vec3( 0, 1, 0 ),
				"z" => new Vec3( 0, 0, 1 ),
				_ => ModelMeasure.TryParseVec3( axisText, out var v ) && v.Length > 1e-6f ? v : null,
			};

			if ( axis is null )
				return Fail( r, $"axis={axisText}: use auto, x, y, z or a direction x,y,z." );
		}

		var render = opts.GetValueOrDefault( "render" );
		var result = FlexReport.Run( mesh, weights, rig, bones, angles, axis, name, keepPoses: render is not null );

		foreach ( var line in result.ToString().Split( '\n' ) )
			r.Log.Add( line );

		if ( result.Bends.Count == 0 )
			return Fail( r, $"No joint could be bent: {string.Join( "; ", result.Skipped )}" );

		if ( render is not null )
		{
			var dir = Resolve( baseDir, render );
			Directory.CreateDirectory( dir );
			var views = (int)Number( opts.GetValueOrDefault( "views", "4" ) );
			var size = (int)Number( opts.GetValueOrDefault( "size", "320" ) );

			foreach ( var bend in result.Bends )
			{
				var posed = mesh.Clone();
				posed.Positions = bend.Posed.ToList();
				var scene = new ShadeScene();
				scene.Add( posed, ShadeMaterial.Clay() );
				var path = Path.Combine( dir, $"{Safe( name )}_{Safe( bend.Bone )}_{N( bend.Degrees )}.png" );
				ShadedRender.Sheet( scene, views, size, 12f, 1, $"OURS · {bend.Bone} {N( bend.Degrees )}°" ).SavePng( path );
				r.Written.Add( path );
				r.Log.Add( $"rendered {bend.Bone} at {N( bend.Degrees )}° to {path}" );
			}
		}

		return true;
	}

	static bool MatchViews( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		var opts = Pairs( args );
		var outDir = opts.TryGetValue( "out", out var o ) ? Resolve( baseDir, o ) : null;
		var scores = new List<(string View, float Overlap)>();
		const int size = 256;

		foreach ( var (viewName, file) in opts.Where( kv => kv.Key != "out" ) )
		{
			var view = RenderView.Named( viewName );
			if ( view is null )
				return Fail( r, $"No view called '{viewName}'. Views: {string.Join( ", ", RenderView.All.Select( v => v.Name ) )}, or turnN." );

			var refPath = Resolve( baseDir, file );
			if ( !File.Exists( refPath ) )
				return Fail( r, $"No reference image at {refPath} (for {viewName})." );

			var (w, h, rgb, alpha) = Raster.LoadPng( refPath );
			var reference = ReferenceMatch.ReferenceSilhouette( w, h, rgb, alpha, size, out var aspect );
			var match = ReferenceMatch.Compare( studio.Bodies, view, reference, aspect, size );

			foreach ( var line in match.Report.Split( '\n' ) )
				r.Log.Add( $"{viewName}: {line}" );

			scores.Add( (viewName, match.Overlap) );

			if ( outDir is not null )
			{
				Directory.CreateDirectory( outDir );
				var diff = Path.Combine( outDir, $"match_{Safe( viewName )}.png" );
				match.Diff.SavePng( diff );
				r.Written.Add( diff );
			}
		}

		if ( scores.Count == 0 )
			return Fail( r, "match needs at least one view=reference.png: match front=ref_front.png side=ref_side.png [out=dir]" );

		var worst = scores.OrderBy( s => s.Overlap ).First();
		r.Log.Add( $"combined: overlap {scores.Average( s => s.Overlap ):0.###} over {scores.Count} view{(scores.Count == 1 ? "" : "s")}; worst {worst.View} {worst.Overlap:0.###} (1 is a perfect match, 0.8 recognisably the same thing)" );
		return true;
	}

	// --- helpers -----------------------------------------------------------------------------------

	/// <summary>One body by name, or every visible body with faces for `all` or nothing.</summary>
	static bool Targets( PartStudio studio, string name, out List<Body> bodies, out string why )
	{
		why = null;

		if ( string.IsNullOrWhiteSpace( name ) || name.Equals( "all", StringComparison.OrdinalIgnoreCase ) )
		{
			bodies = studio.Bodies.Where( b => b.Mesh is { FaceCount: > 0 } && b.Visible ).ToList();
			if ( bodies.Count == 0 ) why = "No body with faces to measure. Add one first.";
			return bodies.Count > 0;
		}

		var body = ModelMeasure.FindBody( studio, name );
		bodies = body?.Mesh is { FaceCount: > 0 } ? new List<Body> { body } : new List<Body>();

		if ( body is null )
			why = $"No body called '{name}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}.";
		else if ( bodies.Count == 0 )
			why = $"{name} has no faces to measure.";

		return bodies.Count > 0;
	}

	/// <summary>`10,20,30` or a range `0..60:7` (seven values, both ends included), or a mix.</summary>
	static bool NumberList( string text, out List<float> values, out string why )
	{
		values = new List<float>();
		why = null;

		foreach ( var part in (text ?? "").Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var dots = part.IndexOf( "..", StringComparison.Ordinal );
			if ( dots > 0 )
			{
				var colon = part.IndexOf( ':', dots );
				var countText = colon > 0 ? part[(colon + 1)..] : "5";
				var toText = colon > 0 ? part[(dots + 2)..colon] : part[(dots + 2)..];

				if ( !TryNumber( part[..dots], out var from ) || !TryNumber( toText, out var to ) || !int.TryParse( countText, out var count ) || count < 1 || count > 200 )
				{
					why = $"'{part}' is not a range from..to:count (count 1 to 200).";
					return false;
				}

				for ( var i = 0; i < count; i++ )
					values.Add( count == 1 ? from : from + (to - from) * i / (count - 1) );
				continue;
			}

			if ( !TryNumber( part, out var v ) )
			{
				why = $"'{part}' is not a number.";
				return false;
			}

			values.Add( v );
		}

		if ( values.Count == 0 )
			why = "no values given.";

		return values.Count > 0;
	}

	/// <summary>One body's vertices and faces out of the merged model, with its weights.</summary>
	static (PolyMesh Mesh, SkinWeights Weights) Part( PolyMesh merged, SkinWeights weights, BodyRange range )
	{
		var mesh = new PolyMesh();
		var part = new SkinWeights( range.Count );

		for ( var i = 0; i < range.Count; i++ )
		{
			mesh.Positions.Add( merged.Positions[range.Start + i] );
			part[i] = weights[range.Start + i];
		}

		foreach ( var face in merged.Faces )
			if ( face.Indices.All( range.Contains ) )
				mesh.Faces.Add( new Face( face.Indices.Select( i => i - range.Start ).ToArray(), face.UVs, face.Material ) );

		return (mesh, part);
	}
}
