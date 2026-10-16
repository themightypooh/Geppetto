using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>
/// Rig verbs for build scripts (and so the CLI and effigy_run):
///
///   bone Name [parent=P|none] [head=pt] tail=pt [roll=deg] [up=x,y,z]   (head defaults to the parent's tail; re-running moves it)
///   chain Name [parent=P] along=a;b;c|spline:Feature count=N [smooth=1]
///   subdivide Bone N
///   twist Bone|all [count=2]
///   mirror bones [side=L|R]
///   rig                       (list the rig)
///   rig clear
///   rig human|citizen|biped|quadruped [height=] [heads=] [style=] [legs=] [scale=] [fingers=1] [twist=0] [replace=1]
///   rig from=rigging_cables.json [right=cable3,cable4] [counts=cable0:12] [replace=1|merge=1]
///   bind Body[,Body]|all [method=smooth|envelope|rigid|heat] [falloff=2] [smooth=3] [twist=1] [max=4]
///   skin [Name] [radii="spine_1 4; arm_* 1.5; * 1"] [levels=2]
/// </summary>
public static partial class BuildScript
{
	const string RigVerbs = "bone, chain, subdivide, twist, mirror bones, rig, bind, skin";

	static bool RigCommand( PartStudio studio, string verb, List<string> args, string baseDir, Result r, out bool ok )
	{
		ok = false;

		switch ( verb )
		{
			case "bone": ok = BoneVerb( studio, args, r ); return true;
			case "chain": ok = ChainVerb( studio, args, r ); return true;
			case "subdivide": ok = SubdivideVerb( studio, args, r ); return true;
			case "twist": ok = TwistVerb( studio, args, r ); return true;
			case "mirror": ok = MirrorVerb( studio, args, r ); return true;
			case "rig": ok = RigVerb( studio, args, baseDir, r ); return true;
			case "bind": ok = BindVerb( studio, args, r ); return true;
			case "skin": ok = SkinVerb( studio, args, r ); return true;
			default: return false;
		}
	}

	static string BoneList( Skeleton rig ) =>
		rig.Count == 0 ? "the rig is empty" : "Bones: " + string.Join( ", ", rig.Bones.Select( b => b.Name ).Take( 60 ) ) + (rig.Count > 60 ? "…" : "");

	static bool FindBone( PartStudio studio, string name, Result r, out int index )
	{
		index = studio.Rig.IndexOf( name );
		return index >= 0 || Fail( r, $"No bone called '{name}'; {BoneList( studio.Rig )}." );
	}

	static bool BoneVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 || args[0].Contains( '=' ) )
			return Fail( r, "bone needs a name: bone Upper parent=Root head=@Root.tail tail=0,0,20" );

		var rig = studio.Rig;
		var name = args[0];
		var opts = Pairs( args.Skip( 1 ) );
		var existing = rig.IndexOf( name );
		var parent = existing >= 0 ? rig.Bones[existing].Parent : -1;

		if ( opts.TryGetValue( "parent", out var p ) )
		{
			if ( p is "" or "none" or "-" ) parent = -1;
			else if ( !FindBone( studio, p, r, out parent ) ) return false;
		}

		Vec3 head;
		if ( opts.TryGetValue( "head", out var headText ) )
		{
			if ( !Point( studio, headText, out head, out var why ) ) return Fail( r, why );
		}
		else if ( parent >= 0 ) head = rig.TailWorld( parent );
		else return Fail( r, $"bone {name} needs head=x,y,z (or parent= to start at its tail)." );

		Vec3 tail;
		if ( opts.TryGetValue( "tail", out var tailText ) )
		{
			if ( !Point( studio, tailText, out tail, out var why ) ) return Fail( r, why );
		}
		else return Fail( r, $"bone {name} needs tail=x,y,z or tail=@landmark." );

		if ( (tail - head).Length < 1e-4f )
			return Fail( r, $"bone {name}: head and tail are the same point. Move the tail." );

		Vec3? up = null;
		if ( opts.TryGetValue( "up", out var upText ) && !ModelMeasure.TryParseVec3( upText, out var u ) )
			return Fail( r, $"up={upText}: write it as x,y,z." );
		else if ( upText is not null ) { ModelMeasure.TryParseVec3( upText, out var u2 ); up = u2; }

		var roll = opts.TryGetValue( "roll", out var rollText ) ? Number( rollText ) : 0f;
		RigBuild.Bone( rig, name, parent, head, tail, RigBuild.UpHint( head, tail, roll, up ), out var moved );
		r.Log.Add( $"{(moved ? "moved" : "added")} bone {name}, length {N( (tail - head).Length )}{(parent >= 0 ? $" under {rig.Bones[parent].Name}" : " (root)")}" );
		return AfterRig( studio, r );
	}

	static bool ChainVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 || args[0].Contains( '=' ) )
			return Fail( r, "chain needs a name: chain Tail parent=pelvis along=0,0,30;-10,0,28;-20,0,20 count=6" );

		var opts = Pairs( args.Skip( 1 ) );
		var parent = -1;

		if ( opts.TryGetValue( "parent", out var p ) && !FindBone( studio, p, r, out parent ) )
			return false;

		if ( !opts.TryGetValue( "along", out var along ) )
			return Fail( r, $"chain {args[0]} needs along=a;b;c (points or landmarks) or along=spline:FeatureName." );

		if ( !opts.TryGetValue( "count", out var countText ) )
			return Fail( r, $"chain {args[0]} needs count=N, how many bones." );

		var points = new List<Vec3>();

		if ( along.StartsWith( "spline:", StringComparison.OrdinalIgnoreCase ) )
		{
			if ( FindFeature( studio, along[7..] ) is not SplineFeature spline )
				return Fail( r, $"No Spline feature called '{along[7..]}'." );

			points = spline.ToSpline().Sample( 16 );
		}
		else
		{
			foreach ( var part in along.Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
			{
				if ( !Point( studio, part, out var pt, out var why ) ) return Fail( r, why );
				points.Add( pt );
			}

			if ( Truthy( opts.GetValueOrDefault( "smooth", "0" ) ) && points.Count > 2 )
				points = new Spline3 { Points = points, Smooth = true }.Sample( 16 );
		}

		var bones = RigBuild.Chain( studio.Rig, args[0], parent, points, (int)Number( countText ) );
		r.Log.Add( $"chain {args[0]}: {bones.Count} bones, {studio.Rig.Bones[bones[0]].Name}..{studio.Rig.Bones[bones[^1]].Name}, each {N( studio.Rig.Bones[bones[0]].Length )} long" );
		return AfterRig( studio, r );
	}

	static bool SubdivideVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 2 )
			return Fail( r, "subdivide needs a bone and a count: subdivide spine_1 3" );

		if ( !FindBone( studio, args[0], r, out var bone ) ) return false;
		var pieces = RigBuild.Subdivide( studio.Rig, bone, (int)Number( args[1] ) );
		r.Log.Add( $"split {args[0]} into {pieces.Count} bones" );
		return AfterRig( studio, r );
	}

	static bool TwistVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 )
			return Fail( r, "twist needs a bone (or all): twist arm_lower_L count=2" );

		var opts = Pairs( args.Skip( 1 ) );
		var count = (int)Number( opts.GetValueOrDefault( "count", "2" ) );
		var targets = args[0].Equals( "all", StringComparison.OrdinalIgnoreCase )
			? RigTemplates.TwistLimbs.Where( n => studio.Rig.IndexOf( n ) >= 0 && studio.Rig.IndexOf( n + "_twist0" ) < 0 ).ToList()
			: args[0].Split( ',' ).ToList();

		if ( targets.Count == 0 )
			return Fail( r, "twist all: no arm_upper/arm_lower/leg_upper/leg_lower bones without twist bones. Name the bone instead." );

		foreach ( var t in targets )
		{
			if ( !FindBone( studio, t, r, out var bone ) ) return false;
			RigBuild.AddTwist( studio.Rig, bone, count );
		}

		r.Log.Add( $"added {count} twist bone(s) to {string.Join( ", ", targets )}" );
		return AfterRig( studio, r );
	}

	static bool MirrorVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 || !args[0].Equals( "bones", StringComparison.OrdinalIgnoreCase ) )
			return Fail( r, "mirror needs 'bones': mirror bones side=L (copies every _L bone without a partner to _R across y=0)." );

		var side = Pairs( args.Skip( 1 ) ).GetValueOrDefault( "side", "L" );
		var added = RigBuild.MirrorSide( studio.Rig, side.Length > 0 ? side[0] : 'L', out var skipped );

		if ( added.Count == 0 )
			return Fail( r, $"mirror bones: no {side} bone without a partner" + (skipped.Count > 0 ? $" ({skipped.Count} already mirrored)." : $". Name side bones with _{side}.") );

		r.Log.Add( $"mirrored {added.Count} bone(s) from side {side}" + (skipped.Count > 0 ? $"; {skipped.Count} already had partners" : "") );
		return AfterRig( studio, r );
	}

	static bool RigVerb( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		var rig = studio.Rig;

		if ( args.Count == 0 )
		{
			r.Log.Add( $"rig: {rig.Count} bones. {BoneList( rig )}" );
			return true;
		}

		if ( args[0].Equals( "clear", StringComparison.OrdinalIgnoreCase ) )
		{
			rig.Bones.Clear();
			studio.BodyBoneMap.Clear();
			r.Log.Add( "rig cleared" );
			return AfterRig( studio, r );
		}

		var first = args[0].Contains( '=' ) ? null : args[0].ToLowerInvariant();
		var opts = Pairs( args.Skip( first is null ? 0 : 1 ) );
		var replace = Truthy( opts.GetValueOrDefault( "replace", "0" ) );
		var merge = Truthy( opts.GetValueOrDefault( "merge", "0" ) );

		if ( rig.Count > 0 && !replace && !merge )
			return Fail( r, $"The model already has {rig.Count} bones. Add replace=1 to start over, or run `rig clear` first." );

		if ( replace )
			rig.Bones.Clear();

		if ( first is null )
		{
			if ( !opts.TryGetValue( "from", out var from ) )
				return Fail( r, $"rig needs a template ({string.Join( ", ", RigTemplates.Names )}) or from=rigging_cables.json." );

			var path = Resolve( baseDir, from );
			if ( !File.Exists( path ) )
				return Fail( r, $"No cable file at {path}." );

			var cables = RigCables.Parse( File.ReadAllText( path ) );
			var report = RigCables.Build( rig, cables,
				opts.TryGetValue( "right", out var right ) ? right.Split( ',', StringSplitOptions.RemoveEmptyEntries ) : null,
				opts.TryGetValue( "counts", out var counts ) ? RigCables.ParseCounts( counts ) : null );

			r.Log.Add( $"rig from {from}: {report.Bones} bones. " + string.Join( "; ", report.Lines ) );
			return AfterRig( studio, r );
		}

		if ( merge )
			return Fail( r, "merge=1 is for rig from=; a template is a whole skeleton. Use replace=1." );

		var scale = Number( opts.GetValueOrDefault( "scale", "1" ) );
		var height = Number( opts.GetValueOrDefault( "height", RigTemplates.HumanHeight.ToString( System.Globalization.CultureInfo.InvariantCulture ) ) ) * scale;
		var fingers = Truthy( opts.GetValueOrDefault( "fingers", "1" ) );
		var twist = Truthy( opts.GetValueOrDefault( "twist", "0" ) );
		Skeleton built;
		string summary;

		switch ( first )
		{
			case "human":
				built = RigTemplates.Human( height, fingers, twist, out var hr );
				summary = hr.Summary;
				break;
			case "citizen":
				if ( MathF.Abs( scale - 1f ) > 1e-4f || opts.ContainsKey( "height" ) )
					return Fail( r, "rig citizen is citizen's exact bind pose, which its animations need; it cannot be scaled. Use rig human height= for a resized human." );
				built = RigTemplates.Citizen();
				summary = "citizen, engine-exact bind pose";
				break;
			case "biped":
				built = RigTemplates.Biped( Number( opts.GetValueOrDefault( "heads", "0" ) ), opts.GetValueOrDefault( "style", "realistic" ), height, fingers, twist, out var br );
				summary = br.Summary;
				break;
			case "quadruped":
				built = RigTemplates.Quadruped( scale, opts.GetValueOrDefault( "legs", "digitigrade" ), out var qr );
				summary = qr.Summary;
				break;
			default:
				return Fail( r, $"No rig template '{args[0]}'. Templates: {string.Join( ", ", RigTemplates.Names )}; or rig from=file.json." );
		}

		RigBuild.Append( rig, built );

		// Body bindings to bones that are gone would make export throw.
		foreach ( var key in studio.BodyBoneMap.Where( kv => rig.IndexOf( kv.Value ) < 0 ).Select( kv => kv.Key ).ToList() )
			studio.BodyBoneMap.Remove( key );

		r.Log.Add( $"rig {first}: {rig.Count} bones; {summary}" );
		return AfterRig( studio, r );
	}

	static bool BindVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 || args[0].Contains( '=' ) )
			return Fail( r, "bind needs bodies (or all): bind Body method=smooth" );

		var opts = Pairs( args.Skip( 1 ) );
		var feature = new BindFeature();
		var pairs = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

		if ( !args[0].Equals( "all", StringComparison.OrdinalIgnoreCase ) ) pairs["bodies"] = args[0];
		if ( opts.TryGetValue( "method", out var m ) ) pairs["method"] = m;
		if ( opts.TryGetValue( "falloff", out var f ) ) pairs["falloff"] = f;
		if ( opts.TryGetValue( "smooth", out var s ) ) pairs["smoothing"] = s;
		if ( opts.TryGetValue( "twist", out var t ) ) pairs["usetwistbones"] = t;
		if ( opts.TryGetValue( "max", out var x ) ) pairs["bonespervertex"] = x;
		pairs["name"] = opts.GetValueOrDefault( "name", $"{args[0]} weights" );

		if ( !Assign( studio, feature, pairs, r ) )
			return false;

		studio.Add( feature );
		return RebuildAndCheck( studio, feature, r, $"bound {args[0]}" );
	}

	static bool SkinVerb( PartStudio studio, List<string> args, Result r )
	{
		var named = args.Count > 0 && !args[0].Contains( '=' );
		var pairs = Pairs( args.Skip( named ? 1 : 0 ) );
		pairs["name"] = named ? args[0] : pairs.GetValueOrDefault( "name", "Body" );
		var feature = new BodyFromBonesFeature();

		if ( !Assign( studio, feature, pairs, r ) )
			return false;

		studio.Add( feature );
		return RebuildAndCheck( studio, feature, r, $"grew {feature.Name} from {studio.Rig.Count} bones" );
	}

	/// <summary>After a rig edit: rebuild what reads the rig, and stop if it now fails.</summary>
	static bool AfterRig( PartStudio studio, Result r )
	{
		if ( !studio.Features.Any( f => f.IsStale ) )
			return true;

		studio.Rebuild();

		var broken = studio.Features.FirstOrDefault( f => f is BodyFromBonesFeature or BindFeature && f.Error is not null );
		return broken is null || Fail( r, $"{broken.TypeName} '{broken.Name}' fails after that rig edit: {broken.Error}" );
	}
}
