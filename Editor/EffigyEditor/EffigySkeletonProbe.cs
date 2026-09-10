using Effigy;
using Sandbox;
using System;
using System.Linq;

using Skeleton = Effigy.Skeleton;

namespace Marionette.EditorTools;

/// <summary>
/// Import a compiled model's skeleton and report whether it survived - from the console, because
/// the answer needs a running engine and the kernel test suite deliberately has none.
///
///     effigy_skeleton_probe                                     citizen
///     effigy_skeleton_probe models/citizen/citizen.vmdl         any model
///     effigy_skeleton_probe models/citizen/citizen.vmdl names   plus every bone name
///
/// WHAT IT IS ACTUALLY ASKING. Reading names and parents out of a model is easy and proves
/// nothing; the question that decides whether a playermodel can be built on top of an imported
/// skeleton is whether the BIND POSE is still the same pose afterwards. So this rebuilds each
/// bone's world transform by walking Effigy's parent chain and compares it against the engine's
/// own <c>GetBoneTransform</c>. A non-zero worst error means the import rotated or moved a bone,
/// and every animation authored against that skeleton would be wrong by exactly that much.
/// </summary>
public static class EffigySkeletonProbe
{
	const string Citizen = "models/citizen/citizen.vmdl";

	[ConCmd( "effigy_skeleton_probe" )]
	public static void Probe( string path = Citizen, string option = "" )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			path = Citizen;

		var model = Model.Load( path );

		if ( model is null || model.IsError )
		{
			Log.Error( $"[skel] could not load {path}" );
			return;
		}

		var result = EffigySkeletonImport.FromModel( model );
		var skeleton = result.Skeleton;

		Log.Info( $"[skel] {path}" );
		// Attachments used to be counted here. Model.AttachmentCount is obsolete and this line is
		// diagnostic only, so it is dropped rather than guessed at - nothing downstream reads it.
		Log.Info( $"[skel] engine bones {model.BoneCount}, imported {skeleton.Count}, "
			+ $"roots {result.Roots}, "
			+ $"anim graph {(model.AnimGraph is null ? "none" : "yes")}" );

		Log.Info( $"[skel] topological order {(TopologicallyOrdered( skeleton ) ? "held" : "BROKEN")}" );

		ReportBindPose( model, skeleton );

		foreach ( var note in result.Notes )
			Log.Info( $"[skel] note: {note}" );

		// A DUMP RATHER THAN 95 LOG LINES. Printing a whole skeleton to the console is both hard
		// to read back and enough output to make the editor stall; a file can be diffed, parsed
		// and compared against another skeleton offline, which is what mapping one rig onto
		// another actually needs.
		if ( option.Contains( "dump", StringComparison.OrdinalIgnoreCase ) )
		{
			var to = System.IO.Path.Combine( Environment.GetFolderPath(
				Environment.SpecialFolder.LocalApplicationData ), "skeleton-dump.txt" );
			var text = new System.Text.StringBuilder();

			text.AppendLine( $"# {path}  bones={skeleton.Count} roots={result.Roots}" );
			text.AppendLine( "# index	name	parent	model-space x,y,z	length" );

			for ( var i = 0; i < skeleton.Count; i++ )
			{
				var bone = skeleton.Bones[i];
				var parent = bone.Parent < 0 ? "-" : skeleton.Bones[bone.Parent].Name;
				var w = skeleton.WorldBind( i ).Origin;

				text.AppendLine( $"{i}	{bone.Name}	{parent}	{w.x:0.####},{w.y:0.####},{w.z:0.####}	{bone.Length:0.####}" );
			}

			System.IO.File.WriteAllText( to, text.ToString() );
			Log.Info( $"[skel] wrote {to}" );
		}

		if ( option.Contains( "names", StringComparison.OrdinalIgnoreCase ) )
		{
			for ( var i = 0; i < skeleton.Count; i++ )
			{
				var bone = skeleton.Bones[i];
				var parent = bone.Parent < 0 ? "-" : skeleton.Bones[bone.Parent].Name;

				Log.Info( $"[skel] {i,3}  {bone.Name,-24} parent {parent,-24} len {bone.Length:0.##}" );
			}
		}

		// THE ORIENTATION DUMP. The plain dump above writes model-space positions only, which is
		// exactly the blind spot that let the bind-pose roll be wrong for years - positions are
		// roll-independent, so a skeleton whose orientations are all wrong reads back perfect.
		// This writes each bone's LOCAL basis (the three axes plus origin, in the parent's frame)
		// as a ready-to-paste `Add(...)` row, so the CitizenSkeleton table can be regenerated with
		// the roll the engine actually reports rather than whatever the old converter produced.
		if ( option.Contains( "local", StringComparison.OrdinalIgnoreCase ) )
		{
			var to = System.IO.Path.Combine( Environment.GetFolderPath(
				Environment.SpecialFolder.LocalApplicationData ), "skeleton-local.txt" );
			var text = new System.Text.StringBuilder();

			text.AppendLine( $"# {path}  bones={skeleton.Count} roots={result.Roots}" );

			for ( var i = 0; i < skeleton.Count; i++ )
			{
				var bone = skeleton.Bones[i];
				var l = bone.Local;
				text.AppendLine( $"Add( s, \"{bone.Name}\", {bone.Parent}, "
					+ $"{l.X.x:0.000000}f, {l.X.y:0.000000}f, {l.X.z:0.000000}f, "
					+ $"{l.Y.x:0.000000}f, {l.Y.y:0.000000}f, {l.Y.z:0.000000}f, "
					+ $"{l.Z.x:0.000000}f, {l.Z.y:0.000000}f, {l.Z.z:0.000000}f, "
					+ $"{l.Origin.x:0.000000}f, {l.Origin.y:0.000000}f, {l.Origin.z:0.000000}f );" );
			}

			System.IO.File.WriteAllText( to, text.ToString() );
			Log.Info( $"[skel] wrote {to}" );
		}
	}

	/// <summary>The invariant Skeleton.AddBone exists to enforce - a parent always sits at a lower
	/// index. Checked rather than assumed, since the import chose the order itself.</summary>
	static bool TopologicallyOrdered( Skeleton skeleton )
	{
		for ( var i = 0; i < skeleton.Count; i++ )
		{
			if ( skeleton.Bones[i].Parent >= i )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Worst disagreement between Effigy's rebuilt world bind and the engine's, over every bone,
	/// in inches and in degrees. Position and orientation are reported separately because they
	/// fail for different reasons: a position drift is a units or parenting mistake, an
	/// orientation drift is the axis convention.
	/// </summary>
	static void ReportBindPose( Model model, Skeleton skeleton )
	{
		var worstPos = 0f;
		var worstAng = 0f;
		var worstPosBone = "-";
		var worstAngBone = "-";

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			var name = skeleton.Bones[i].Name;
			var engine = model.GetBoneTransform( name );
			var mine = skeleton.WorldBind( i );

			var dp = Vector3.DistanceBetween( engine.Position, EffigySkeletonImport.ToVector3( mine.Origin ) );

			if ( dp > worstPos )
			{
				worstPos = dp;
				worstPosBone = name;
			}

			// Compare the three axes rather than the quaternions, which have a sign ambiguity and
			// would report 180 degrees for two identical rotations.
			var da = MathF.Max(
				Angle( engine.Rotation.Forward, mine.X ),
				MathF.Max( Angle( engine.Rotation.Left, mine.Y ), Angle( engine.Rotation.Up, mine.Z ) ) );

			if ( da > worstAng )
			{
				worstAng = da;
				worstAngBone = name;
			}
		}

		Log.Info( $"[skel] worst bind error: {worstPos:0.####} in at '{worstPosBone}', "
			+ $"{worstAng:0.####} deg at '{worstAngBone}'" );

		// THE RIVAL HYPOTHESIS. If BoneCollection's LocalTransform is not parent-relative after
		// all but already model-space, then walking the parent chain compounds it and the error
		// above is meaningless. Measuring both tells the two apart in one run instead of two.
		var worstFlat = 0f;
		var flatBone = "-";

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			var name = skeleton.Bones[i].Name;
			var d = Vector3.DistanceBetween( model.GetBoneTransform( name ).Position,
				EffigySkeletonImport.ToVector3( skeleton.Bones[i].Local.Origin ) );

			if ( d > worstFlat )
			{
				worstFlat = d;
				flatBone = name;
			}
		}

		Log.Info( $"[skel] if local were already model-space: worst {worstFlat:0.####} in at '{flatBone}'" );

		for ( var i = 0; i < Math.Min( 6, skeleton.Count ); i++ )
		{
			var name = skeleton.Bones[i].Name;

			Log.Info( $"[skel]   {name,-20} engine {model.GetBoneTransform( name ).Position} "
				+ $"| local {EffigySkeletonImport.ToVector3( skeleton.Bones[i].Local.Origin )} "
				+ $"| walked {EffigySkeletonImport.ToVector3( skeleton.WorldBind( i ).Origin )}" );
		}
	}

	static float Angle( Vector3 engine, Vec3 mine )
	{
		var a = engine.Normal;
		var b = EffigySkeletonImport.ToVector3( mine ).Normal;

		return MathF.Acos( Math.Clamp( Vector3.Dot( a, b ), -1f, 1f ) ).RadianToDegree();
	}
}
