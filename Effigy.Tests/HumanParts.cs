using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Sculpted human parts from Blender Studio's Human Base Meshes (CC0), as fields a creature can
/// fuse with: a realistic head, hand, foot, jaw (teeth and gums), eye, whole bodies to cut torsos
/// and limbs out of, and skeleton bones. Each mesh is baked into a canonical frame on load so the
/// builders below can place it with a <see cref="CreatureGen.Frame"/> and distort it with warps.
///
/// The OBJs live outside the repo (they are 26 MB): $EFFIGY_HUMAN_PARTS, or
/// ~/.local/share/effigy/human-base-meshes. tools/export_human_parts.py writes them from the
/// bundle's .blend.
/// </summary>
public static class HumanParts
{
	public static string Dir =>
		Environment.GetEnvironmentVariable( "EFFIGY_HUMAN_PARTS" )
		?? Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".local/share/effigy/human-base-meshes" );

	public static bool Available => File.Exists( Path.Combine( Dir, "geo_head_sculpting_realistic.obj" ) );

	/// <summary>Where each part's canonical origin sits in the exported file, and how its axes map.</summary>
	sealed record Canon( string File, Vec3 Origin, Xform Rotation );

	// Source hand hangs down: fingers -z, back of hand +x. Canonical: fingers +x, back of hand +z.
	static readonly Xform HandRotation = new( new Vec3( 0, 0, 1 ), new Vec3( 0, 1, 0 ), new Vec3( -1, 0, 0 ), Vec3.Zero );

	static readonly Dictionary<string, Canon> Canons = new()
	{
		// Skull centre; chin at z -4.7, lips -3.1, nose tip x 5.0, eyes z +0.3, crown +4.8.
		["head"] = new( "geo_head_sculpting_realistic.obj", new Vec3( 1.6f, 70.3f, 30.0f ), Xform.Identity ),
		// Wrist centre; a right hand, thumb on +y.
		// The same head's level-0 cage (before Multires), for building at cage level.
		["headCage"] = new( "head_cage_realistic.obj", new Vec3( 1.6f, 70.3f, 30.0f ), Xform.Identity ),
		["hand"] = new( "hand_realistic.obj", new Vec3( -0.04f, 55.47f, 16.8f ), HandRotation ),
		// Heel bottom; toe along +x.
		["foot"] = new( "foot_realistic.obj", new Vec3( -1.27f, 77.0f, 8.97f ), Xform.Identity ),
		["jaw"] = new( "jaw_realistic.obj", new Vec3( 2.48f, 57.58f, 37.61f ), Xform.Identity ),
		["eye"] = new( "eye_realistic.obj", new Vec3( 0.225f, 61.0f, 38.08f ), Xform.Identity ),
		// Between the feet on the floor, facing +x.
		["bodyM"] = new( "geo_body_male_realistic.obj", new Vec3( 1.33f, -89.15f, 0f ), Xform.Identity ),
		["bodyF"] = new( "geo_body_female_realistic.obj", new Vec3( 1.33f, -89.15f, 0f ), Xform.Identity ),
		// The male body again, origin mid-torso (body z 42): chest faces +x, neck base at z +14, crotch at -12.
		["torsoM"] = new( "geo_body_male_realistic.obj", new Vec3( 1.33f, -89.15f, 42f ), Xform.Identity ),
		["ribcage"] = new( "geo_skeletion_ripcage.obj", new Vec3( 1.15f, -291.75f, 52.26f ), Xform.Identity ),
		["pelvis"] = new( "geo_skeletion_hip.obj", new Vec3( 0.42f, -291.75f, 37.8f ), Xform.Identity ),
		["skull"] = new( "geo_skeletion_skull.obj", new Vec3( 1.9f, -291.75f, 65.2f ), Xform.Identity ),
	};

	static readonly ConcurrentDictionary<string, PolyMesh> Meshes = new();
	static readonly ConcurrentDictionary<string, SdfMesh> Fields = new();

	/// <summary>The canonical mesh for a part, mirrored across y for the left side when asked.</summary>
	public static PolyMesh Mesh( string part, bool mirror = false ) => Meshes.GetOrAdd( part + (mirror ? ":m" : ""), _ =>
	{
		var canon = Canons[part];
		var mesh = ObjReader.ReadFile( Path.Combine( Dir, canon.File ) );
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = canon.Rotation.TransformDirection( mesh.Positions[i] - canon.Origin );
			mesh.Positions[i] = mirror ? new Vec3( p.x, -p.y, p.z ) : p;
		}

		// A mirror reverses winding; reverse it back so the field's inside stays inside.
		if ( mirror )
			foreach ( var f in mesh.Faces )
				Array.Reverse( f.Indices );

		return mesh;
	} );

	/// <summary>
	/// The part with each face's Material set to the face set the bundle's artists painted on it
	/// (exported next to the OBJ by tools/export_human_parts.py). For the head: 11 cranium, 8 face,
	/// 7 lips and jaw, 6 mouth bag (a full oral cavity: palate vault, floor, vestibules), 2/3 eye
	/// sockets, 9 nose, 4/5 ears, 10 neck, 12 neck cap. Null when the sidecar is missing.
	/// </summary>
	public static PolyMesh Regions( string part, bool mirror = false )
	{
		var path = Path.Combine( Dir, Path.ChangeExtension( Canons[part].File, ".facesets" ) );
		if ( !File.Exists( path ) ) return null;
		var ids = File.ReadAllLines( path ).Where( l => l.Length > 0 ).Select( int.Parse ).ToArray();
		var mesh = Mesh( part, mirror );
		if ( ids.Length != mesh.FaceCount ) return null;
		return new PolyMesh( mesh.Positions, mesh.Faces.Select( ( f, i ) => new Face( (int[])f.Indices.Clone(), null, ids[i] ) ) );
	}

	/// <summary>The part as a field in its canonical frame.</summary>
	public static SdfMesh Field( string part, bool mirror = false, float band = 4f ) =>
		Fields.GetOrAdd( part + (mirror ? ":m" : "") + ":" + band, _ => new SdfMesh( Mesh( part, mirror ), band ) );

	/// <summary>The part as a thin shell around its surface: for parts made of overlapping
	/// shells (teeth in gums), where an inside test cannot be trusted.</summary>
	public static SdfMesh Shell( string part, float thickness, bool mirror = false ) =>
		Fields.GetOrAdd( part + (mirror ? ":m" : "") + ":shell" + thickness, _ => new SdfMesh( Mesh( part, mirror ), 2f, thickness ) );

	public static Xform Placement( CreatureGen.Frame f ) => new( f.Forward, f.Left, f.Up, f.Origin );

	/// <summary>
	/// A real man's torso from the CC0 body: crotch to neck base, arms off at the deltoid, as a
	/// field at <paramref name="f"/> (origin mid-torso; chest along Forward, neck along Up). Pecs,
	/// clavicles, abs, navel and ribs are the sculptor's, so it reads as a person at any size.
	/// <paramref name="neck"/> keeps that much neck above the base (body inches) for a head to sit on.
	/// </summary>
	public static SdfInstance RealTorso( CreatureGen.Frame f, string tag, int material = CreatureGen.SkinMat, float neck = 2.5f, float armStump = 8.6f )
	{
		// A soft intersection (k 2): a hard one leaves the capsule's flank as a flat-looking cut.
		Sdf crop = new SdfIntersect( Field( "torsoM" ), new SdfCapsule( new Vec3( 0.3f, 0, -12f ), new Vec3( 0.3f, 0, 11.5f ), armStump ), 2.0f );
		crop = new SdfIntersect( crop, new HalfSpace( new Vec3( 0, 0, 14f + neck ), new Vec3( 0, 0, 1 ) ), 0.5f );
		crop = new SdfIntersect( crop, new HalfSpace( new Vec3( 0, 0, -12f ), new Vec3( 0, 0, -1 ) ), 0.5f );
		return new SdfInstance( crop, Placement( f ), f.Scale ) { Material = material, Tag = tag };
	}

	// The body's arm, in bodyM inches (right arm; the left mirrors y): shoulder, elbow, wrist.
	static readonly Vec3 ArmShoulder = new( -1.0f, -8.2f, 51.5f ), ArmElbow = new( -1.3f, -11.6f, 42.5f ), ArmWrist = new( 1.2f, -14.9f, 33.5f );

	/// <summary>
	/// One segment of the body's real arm (upper arm or forearm) laid from <paramref name="from"/>
	/// to <paramref name="to"/> in the world: rotated onto that line, stretched along its own bone to
	/// fit (girth unchanged, so biceps, elbow and forearm taper stay the sculptor's), at
	/// <paramref name="girth"/> times life size. <paramref name="roll"/> turns it about its bone (degrees).
	/// </summary>
	public static SdfInstance RealArmSegment( bool upper, bool left, Vec3 from, Vec3 to, float girth, string tag, float roll = 0f, int material = CreatureGen.SkinMat )
	{
		var m = left ? new Vec3( 1, -1, 1 ) : new Vec3( 1, 1, 1 );
		Vec3 M( Vec3 v ) => new( v.x * m.x, v.y * m.y, v.z * m.z );
		var a = M( upper ? ArmShoulder : ArmElbow );
		var b = M( upper ? ArmElbow : ArmWrist );
		// Just the arm's triangles: a field over the whole body costs ten times as much per sample.
		var arm = Fields.GetOrAdd( left ? "armL" : "armR", _ =>
		{
			var sh = M( ArmShoulder ); var wr = M( ArmWrist ) + new Vec3( 0, 0, -2.5f );
			return new SdfMesh( CreatureGen.KeepFaces( Mesh( "bodyM" ), c => SkinBinder.DistanceToSegment( c, sh, M( ArmElbow ) ) < 4.5f || SkinBinder.DistanceToSegment( c, M( ArmElbow ), wr ) < 4.5f ), 4f );
		} );
		Sdf crop = new SdfIntersect( arm, new SdfCapsule( a, b, upper ? 2.7f : 2.4f ), 0.5f );
		// Off the torso at the shoulder; off the hand at the wrist.
		crop = upper
			? new SdfIntersect( crop, new HalfSpace( M( new Vec3( 0, -7.2f, 0 ) ), new Vec3( 0, left ? -1 : 1, 0 ) ), 0.4f )
			: new SdfIntersect( crop, new HalfSpace( new Vec3( 0, 0, 33.3f ), new Vec3( 0, 0, -1 ) ), 0.3f );
		var u = (b - a).Normal;
		// Flat at the elbow, both sides: the capsule's round end past the joint gets stretched with
		// the bone and comes out as a cap inches long.
		crop = upper
			? new SdfIntersect( crop, new HalfSpace( b + u * 0.6f, u ), 0.4f )
			: new SdfIntersect( crop, new HalfSpace( a - u * 0.3f, -u ), 0.4f );
		var d = (to - from).Normal;
		var axis = Vec3.Cross( u, d );
		var rot = axis.LengthSquared > 1e-8f ? Xform.Rotate( axis.Normal, MathF.Acos( Math.Clamp( Vec3.Dot( u, d ), -1f, 1f ) ) ) : Xform.Identity;
		if ( roll != 0f ) rot = Xform.Rotate( d, roll * MathF.PI / 180f ) * rot;
		var x = rot.TransformDirection( new Vec3( 1, 0, 0 ) );
		var y = rot.TransformDirection( new Vec3( 0, 1, 0 ) );
		var z = rot.TransformDirection( new Vec3( 0, 0, 1 ) );
		var placement = new Xform( x, y, z, Vec3.Zero );
		placement = new Xform( x, y, z, from - placement.TransformDirection( a * girth ) );
		var inst = new SdfInstance( crop, placement, girth ) { Material = material, Tag = tag };
		var stretch = (to - from).Length / ((b - a).Length * girth);
		inst.Warp( new SegmentStretch( a, u, stretch, (b - a).Length + 3f ) );
		return inst;
	}

	// The body's leg, in bodyM inches (right leg; the left mirrors y): hip, knee, ankle.
	static readonly Vec3 LegHip = new( -0.5f, -3.8f, 30f ), LegKnee = new( -2.2f, -5.6f, 17.5f ), LegAnkle = new( -3.5f, -6.7f, 4.5f );

	/// <summary>
	/// A thigh or shin of the body's real leg laid from <paramref name="from"/> to <paramref name="to"/>,
	/// as <see cref="RealArmSegment"/> does for the arm. <paramref name="trousers"/> inflates the
	/// surface by that much: loose cloth over the leg rather than a stocking.
	/// </summary>
	public static SdfInstance RealLegSegment( bool thigh, bool left, Vec3 from, Vec3 to, float girth, string tag, float trousers = 0f, int material = CreatureGen.SkinMat )
	{
		var m = left ? new Vec3( 1, -1, 1 ) : new Vec3( 1, 1, 1 );
		Vec3 M( Vec3 v ) => new( v.x * m.x, v.y * m.y, v.z * m.z );
		var a = M( thigh ? LegHip : LegKnee );
		var b = M( thigh ? LegKnee : LegAnkle );
		var leg = Fields.GetOrAdd( left ? "legL" : "legR", _ =>
			new SdfMesh( CreatureGen.KeepFaces( Mesh( "bodyM" ), c => (c.y * m.y) < -0.2f && (SkinBinder.DistanceToSegment( c, M( LegHip ), M( LegKnee ) ) < 6f || SkinBinder.DistanceToSegment( c, M( LegKnee ), M( LegAnkle ) ) < 5f) ), 4f ) );
		Sdf crop = new SdfIntersect( trousers > 0f ? new SdfOffset( leg, trousers ) : leg, new SdfCapsule( a, b, thigh ? 5.2f : 4.2f ), 0.8f );
		crop = thigh
			? new SdfIntersect( crop, new HalfSpace( new Vec3( 0, 0, 29.5f ), new Vec3( 0, 0, 1 ) ), 0.4f )
			: new SdfIntersect( crop, new HalfSpace( new Vec3( 0, 0, 4.5f ), new Vec3( 0, 0, -1 ) ), 0.3f );
		var u = (b - a).Normal;
		var d = (to - from).Normal;
		var axis = Vec3.Cross( u, d );
		var rot = axis.LengthSquared > 1e-8f ? Xform.Rotate( axis.Normal, MathF.Acos( Math.Clamp( Vec3.Dot( u, d ), -1f, 1f ) ) ) : Xform.Identity;
		var x = rot.TransformDirection( new Vec3( 1, 0, 0 ) );
		var y = rot.TransformDirection( new Vec3( 0, 1, 0 ) );
		var z = rot.TransformDirection( new Vec3( 0, 0, 1 ) );
		var placement = new Xform( x, y, z, Vec3.Zero );
		placement = new Xform( x, y, z, from - placement.TransformDirection( a * girth ) );
		var inst = new SdfInstance( crop, placement, girth ) { Material = material, Tag = tag };
		inst.Warp( new SegmentStretch( a, u, (to - from).Length / ((b - a).Length * girth), (b - a).Length + 5f ) );
		return inst;
	}

	/// <summary>A head frame seated so its own neck root (head inches -1.25, 0, -4.7) lands on <paramref name="neckTop"/>.</summary>
	public static CreatureGen.Frame SeatHead( Vec3 neckTop, Vec3 forward, Vec3 up, float scale = 1f )
	{
		var probe = new CreatureGen.Frame( Vec3.Zero, forward, up, scale );
		return new CreatureGen.Frame( neckTop - probe.P( -1.25f, 0, -4.7f ), forward, up, scale );
	}

	public static SdfInstance Place( Sdf canonical, CreatureGen.Frame f, int material, string tag, int bone = -1 ) =>
		new( canonical, Placement( f ), f.Scale ) { Material = material, Tag = tag, Bone = bone };

	/// <summary>A uniform stretch along an axis from a point, for a part that spans
	/// <paramref name="length"/> along it: its reach is what that span grows by (a plain
	/// WarpStretch with no radius reports none, and the bounds clip what it stretched).</summary>
	public sealed class SegmentStretch : SdfWarp
	{
		readonly Vec3 _center, _axis;
		readonly float _factor, _length;
		public SegmentStretch( Vec3 center, Vec3 axis, float factor, float length ) { _center = center; _axis = axis.Normal; _factor = factor; _length = length; }
		public override Vec3 Inverse( Vec3 p ) { var along = Vec3.Dot( p - _center, _axis ); return p - _axis * along * (1f - 1f / _factor); }
		public override Vec3 Forward( Vec3 p ) { var along = Vec3.Dot( p - _center, _axis ); return p + _axis * along * (_factor - 1f); }
		public override float Reach => _length * MathF.Abs( _factor - 1f );
	}

	/// <summary>A half space: inside where dot(p - point, normal) is negative. For slicing parts.</summary>
	public sealed class HalfSpace : SdfLeaf
	{
		public Vec3 Point, Normal;
		public HalfSpace( Vec3 point, Vec3 normal ) { Point = point; Normal = normal.Normal; }
		public override float Eval( Vec3 p ) => Vec3.Dot( p - Point, Normal );
		public override Vec3 Min => new( -1e4f, -1e4f, -1e4f );
		public override Vec3 Max => new( 1e4f, 1e4f, 1e4f );
	}
}

public static partial class CreatureGen
{
	/// <summary>A sculpt stroke: displacement along the surface normal, height h (negative
	/// carves), falling off with distance from segment a-b over radius r. A == b is a dab.</summary>
	public readonly record struct Stroke( Vec3 A, Vec3 B, float R, float H )
	{
		public static Stroke Dab( Vec3 at, float r, float h ) => new( at, at, r, h );
		public Stroke Mirrored => new( new Vec3( A.x, -A.y, A.z ), new Vec3( B.x, -B.y, B.z ), R, H );
		public Stroke Scaled( float k ) => this with { H = H * k };
	}

	/// <summary>Both sides of the face at once.</summary>
	static IEnumerable<Stroke> Both( Stroke s ) => new[] { s, s.Mirrored };

	/// <summary>A copy of the mesh with the strokes pushed along its vertex normals.</summary>
	public static PolyMesh Sculpted( PolyMesh mesh, IEnumerable<Stroke> strokes )
	{
		var list = strokes.ToList();
		var normals = new Vec3[mesh.VertexCount];
		foreach ( var f in mesh.Faces )
		{
			var n = Vec3.Zero;
			for ( var i = 0; i < f.Indices.Length; i++ )
			{
				var a = mesh.Positions[f.Indices[i]];
				var b = mesh.Positions[f.Indices[(i + 1) % f.Indices.Length]];
				n += new Vec3( (a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y) );
			}
			foreach ( var i in f.Indices ) normals[i] += n;
		}
		var positions = new Vec3[mesh.VertexCount];
		System.Threading.Tasks.Parallel.For( 0, mesh.VertexCount, i =>
		{
			var p = mesh.Positions[i];
			var n = normals[i].LengthSquared > 1e-12f ? normals[i].Normal : Vec3.Zero;
			var d = 0f;
			foreach ( var s in list )
			{
				var dist = SkinBinder.DistanceToSegment( p, s.A, s.B );
				if ( dist >= s.R ) continue;
				var t = dist / s.R;
				d += s.H * 0.5f * (1f + MathF.Cos( t * MathF.PI ));
			}
			positions[i] = p + n * d;
		} );
		return new PolyMesh( positions, mesh.Faces.Select( f => new Face( (int[])f.Indices.Clone(), null, f.Material ) ) );
	}

	/// <summary>Who the man was before: the build and age sculpted into the base head.</summary>
	public enum HeadBuild { None, Rigger, Gaunt, Round, Old }

	/// <summary>Aging every rigger shares: brow mass, lines, bags, jowls, a thicker neck.</summary>
	static IEnumerable<Stroke> Aging( float k )
	{
		var s = new List<Stroke>();
		s.AddRange( Both( Stroke.Dab( new Vec3( 4.2f, 1.3f, 1.1f ), 0.95f, 0.26f ) ) );                                  // brow ridge
		s.AddRange( Both( new Stroke( new Vec3( 4.55f, 0.22f, 0.75f ), new Vec3( 4.45f, 0.3f, 1.5f ), 0.09f, -0.06f * k ) ) ); // frown lines
		s.Add( new Stroke( new Vec3( 4.05f, -1.7f, 2.0f ), new Vec3( 4.05f, 1.7f, 2.0f ), 0.1f, -0.05f * k ) );            // forehead creases
		s.Add( new Stroke( new Vec3( 3.75f, -1.5f, 2.6f ), new Vec3( 3.75f, 1.5f, 2.6f ), 0.1f, -0.045f * k ) );
		s.AddRange( Both( new Stroke( new Vec3( 4.6f, 0.72f, -1.55f ), new Vec3( 4.1f, 1.5f, -3.35f ), 0.2f, -0.13f * k ) ) );  // nasolabial groove
		s.AddRange( Both( Stroke.Dab( new Vec3( 4.0f, 1.45f, -1.85f ), 0.8f, 0.14f * k ) ) );                             // cheek fat over it
		s.AddRange( Both( Stroke.Dab( new Vec3( 3.2f, 2.0f, -4.0f ), 1.0f, 0.3f * k ) ) );                                // jowls
		s.AddRange( Both( Stroke.Dab( new Vec3( 0.8f, 2.6f, -3.6f ), 1.0f, 0.22f ) ) );                                   // jaw angle
		s.Add( Stroke.Dab( new Vec3( 2.8f, 0, -5.0f ), 0.95f, 0.28f * k ) );                                               // under the chin
		s.Add( new Stroke( new Vec3( -0.9f, 0, -4.6f ), new Vec3( -1.2f, 0, -6.2f ), 3.2f, 0.45f ) );                      // neck
		s.AddRange( Both( Stroke.Dab( new Vec3( 4.3f, 1.25f, -0.3f ), 0.5f, 0.1f * k ) ) );                               // eye bags
		s.AddRange( Both( new Stroke( new Vec3( 4.3f, 0.6f, -0.6f ), new Vec3( 4.05f, 1.9f, -0.65f ), 0.12f, -0.06f * k ) ) ); // tear trough
		s.AddRange( Both( Stroke.Dab( new Vec3( 4.4f, 1.35f, 0.78f ), 0.48f, 0.09f ) ) );                                 // hooded lids
		s.Add( Stroke.Dab( new Vec3( 5.0f, 0, -1.2f ), 0.5f, 0.15f ) );                                                    // fleshier nose tip
		for ( var i = 0; i < 3; i++ )                                                                                        // crow's feet
			s.AddRange( Both( new Stroke( new Vec3( 3.6f, 2.15f, 0.15f + i * 0.28f ), new Vec3( 3.3f, 2.55f, 0.05f + i * 0.4f ), 0.07f, -0.045f * k ) ) );
		s.AddRange( Both( new Stroke( new Vec3( 2.2f, 1.2f, -5.6f ), new Vec3( 1.4f, 1.6f, -5.9f ), 0.12f, -0.06f * k ) ) );  // neck lines
		return s;
	}

	static IEnumerable<Stroke> BuildStrokes( HeadBuild build ) => BuildStrokesRaw( build ).Select( s => s.Scaled( 2.2f ) );

	static IEnumerable<Stroke> BuildStrokesRaw( HeadBuild build ) => build switch
	{
		HeadBuild.Rigger => Aging( 1f ).Concat( new[] { Stroke.Dab( new Vec3( 5.0f, 0, -0.9f ), 0.7f, 0.12f ) } ),            // broad nose
		HeadBuild.Gaunt => Aging( 1.1f ).Concat( Both( Stroke.Dab( new Vec3( 3.8f, 1.85f, -2.0f ), 1.05f, -0.32f ) ) )        // hollow cheeks
			.Concat( Both( Stroke.Dab( new Vec3( 2.8f, 2.7f, 1.4f ), 0.95f, -0.2f ) ) )                                    // temples
			.Concat( Both( Stroke.Dab( new Vec3( 3.9f, 2.15f, -0.45f ), 0.6f, 0.16f ) ) ),                                 // cheekbones
		HeadBuild.Round => Aging( 0.7f ).Concat( Both( Stroke.Dab( new Vec3( 3.5f, 2.0f, -1.8f ), 1.4f, 0.38f ) ) )           // fat cheeks
			.Concat( new[] { Stroke.Dab( new Vec3( 2.6f, 0, -5.2f ), 1.4f, 0.5f ), new Stroke( new Vec3( -0.9f, 0, -4.4f ), new Vec3( -1.2f, 0, -6.2f ), 3.4f, 0.5f ) } ),
		HeadBuild.Old => Aging( 1.7f ).Concat( Both( Stroke.Dab( new Vec3( 3.0f, 2.2f, -4.6f ), 0.8f, 0.25f ) ) ),            // second jowl
		_ => Aging( 1f ),
	};

	/// <summary>Warps that set the build's proportions before anything goes wrong.</summary>
	static IEnumerable<SdfWarp> BuildWarps( HeadBuild build ) => build switch
	{
		HeadBuild.Gaunt => new SdfWarp[]
		{
			new WarpStretch( new Vec3( 2.5f, 0, -1.0f ), new Vec3( 0, 0, 1 ), 1.08f, 6f ),                                 // long face
			new WarpBulge( new Vec3( -0.4f, 3.55f, 0.2f ), 1.25f, 1.5f ), new WarpBulge( new Vec3( -0.4f, -3.55f, 0.2f ), 1.25f, 1.5f ), // big ears
		},
		HeadBuild.Round => new SdfWarp[] { new WarpStretch( new Vec3( 2.0f, 0, -2.0f ), new Vec3( 0, 1, 0 ), 1.08f, 5f ) },
		HeadBuild.Old => new SdfWarp[]
		{
			new WarpGrab( new Vec3( 3.4f, 0, -4.6f ), new Vec3( -0.4f, 0, 0 ), 1.3f ),                                    // receding chin
			new WarpGrab( new Vec3( 4.9f, 0, -1.2f ), new Vec3( 0.05f, 0, -0.25f ), 0.7f ),                               // nose sags
		},
		_ => new SdfWarp[] { new WarpGrab( new Vec3( 4.7f, 0, 0.1f ), new Vec3( 0, 0.15f, 0 ), 0.5f ) },                  // old break in the bridge
	};

	/// <summary>Where each man's hair still grows: a horseshoe, a ring, or a full cap. Null for none.</summary>
	static Sdf Hair( HeadBuild build, Random rng )
	{
		// The scalp itself, pushed out a little: hair that follows the skull, not a helmet.
		var scalp = new SdfOffset( HumanParts.Field( "head" ), 0.14f );
		Sdf cap = build switch
		{
			HeadBuild.Rigger => new SdfSubtract( scalp, 0.6f, new SdfSphere( new Vec3( 1.2f, 0, 4.6f ), 2.9f ) ),               // horseshoe
			HeadBuild.Gaunt => scalp,                                                                                            // close-cropped full head
			HeadBuild.Round => new SdfSubtract( scalp, 0.6f, new SdfSphere( new Vec3( 2.2f, 0, 3.6f ), 3.3f ) ),                 // receding
			_ => new SdfSubtract( scalp, 0.8f, new SdfSphere( new Vec3( 0.6f, 0, 4.0f ), 3.4f ) ),                           // a ring of white
		};
		var region = new SdfIntersect( new SdfIntersect( cap, new HumanParts.HalfSpace( new Vec3( 0, 0, 0.1f ), new Vec3( 0, 0, -1 ) ), 0.4f ), new HumanParts.HalfSpace( new Vec3( 2.6f, 0, 0 ), new Vec3( 1, 0, 0.35f ) ), 0.4f );
		return new SdfDisplace( region, 0.04f, 3.0f, 2, rng.Next() );
	}

	/// <summary>
	/// The sculpted head as a real mesh at <paramref name="f"/> (origin at the skull centre):
	/// aged and built into a particular man, cut off below the neck, eyes with irises in their
	/// sockets. jawDrop hinges the mandible open on its joint, the lips torn apart, real teeth
	/// seated behind them, tongue and mouth bag inside; smear and droop distort the face. Every
	/// piece rides the same warps, so the teeth go where the jaw goes. Returns the neck root and
	/// hair as a field for the flesh to grow around.
	/// </summary>
	public static Sdf RealHead( Random rng, Frame f, string tag, int bone = -1, float smear = 1f, float droop = 0f,
		float jawDrop = 0f, float mouthWidth = 1f, int extraEyes = 0, float neckLength = 1f, float sick = 0.35f, HeadBuild build = HeadBuild.None )
	{
		var neckEnd = -9.4f * neckLength;
		var mouthC = new Vec3( 4.3f, 0, -3.12f );

		var skin = KeepFaces( HumanParts.Mesh( "head" ), c =>
		{
			if ( c.z < -5.9f ) return false;
			if ( c.z < -4.0f && (MathF.Abs( c.y ) > 2.45f || c.x < -3.4f) ) return false;
			if ( jawDrop > 0.05f )
			{
				// The lips torn apart along the seam, the tear running a little into the cheeks.
				var m = c - mouthC;
				var ry = 1.22f * mouthWidth;
				if ( (m.x / 1.15f) * (m.x / 1.15f) + (m.y / ry) * (m.y / ry) + (m.z / 0.2f) * (m.z / 0.2f) < 1f )
					return false;
			}
			return true;
		} );
		if ( build != HeadBuild.None )
			skin = Sculpted( skin, BuildStrokes( build ) );

		var warps = new List<SdfWarp>( build == HeadBuild.None ? Array.Empty<SdfWarp>() : BuildWarps( build ) );
		var tmj = new Vec3( 0.6f, 0, -0.4f );
		var jawDeg = 14f * jawDrop;
		if ( jawDrop > 0.05f )
		{
			// The mandible turns on the jaw joint. For the skin, the plane through the joint and
			// the lip seam separates what moves (lower lip, chin) from what stays.
			warps.Add( new WarpHinge( tmj, new Vec3( 0, 1, 0 ), jawDeg,
				new Vec3( 4.5f, 0, -3.14f ), new Vec3( 2.74f, 0, 3.9f ), 0.35f )
			{
				Gate = p => Math.Clamp( (p.x + 0.8f) / 1.4f, 0f, 1f ) * Math.Clamp( (p.z + 6.2f) / 0.9f, 0f, 1f ),
			} );
			// The condyle slides forward and down onto the eminence as the jaw opens past normal.
			var sev = Math.Clamp( (jawDrop - 1.4f) / 1.6f, 0f, 1f );
			if ( sev > 0f )
				warps.Add( new WarpGrab( new Vec3( 2.2f, 0, -3.9f ), new Vec3( 0.35f, 0, -0.3f ) * sev, 2.6f ) );
			// The upper lip lifts off the teeth; the corners pull wide.
			warps.Add( new WarpGrab( new Vec3( 4.5f, 0, -2.75f ), new Vec3( 0, 0, 0.2f * jawDrop ), 1.0f ) );
			warps.Add( new WarpGrab( new Vec3( 3.95f, 1.1f, -3.25f ), new Vec3( -0.1f, 0.18f * jawDrop, 0 ), 0.7f ) );
			warps.Add( new WarpGrab( new Vec3( 3.95f, -1.1f, -3.25f ), new Vec3( -0.1f, -0.18f * jawDrop, 0 ), 0.7f ) );
		}
		if ( droop > 0.05f )
			warps.Add( new WarpGrab( new Vec3( 3.2f, 0, -2.6f ), new Vec3( 0.15f, 0, -droop * 1.0f ), 3.6f ) );
		if ( MathF.Abs( smear - 1f ) > 0.02f )
		{
			// A drag, not a widen: the face slides toward +y with a tail behind it, one eye and
			// mouth corner carried off, the far side left where it was.
			var amt = (smear - 1f) * 4.5f;
			warps.Add( new WarpDrag( new Vec3( 4.0f, 0.9f, -0.9f ), new Vec3( 0.1f, 1f, 0.22f ), amt, 3.2f, 2.3f, rng.Next() ) );
			warps.Add( new WarpGrab( new Vec3( 3.9f, 1.0f, -3.1f ), new Vec3( -0.1f, amt * 0.35f, amt * 0.12f ), 1.1f ) );
		}
		// Nobody's face is symmetric: one brow higher, one eye lower, the nose off true.
		warps.Add( new WarpGrab( new Vec3( 4.2f, 1.3f, 1.1f ), new Vec3( 0, 0, 0.05f ), 1.2f ) );
		warps.Add( new WarpGrab( new Vec3( 3.6f, -1.25f, 0.1f ), new Vec3( 0, 0, -0.04f ), 1.0f ) );
		warps.Add( new WarpGrab( new Vec3( 5.0f, 0, -1.2f ), new Vec3( 0, 0.05f, 0 ), 0.8f ) );
		warps.Add( new WarpGrab( new Vec3( 3.6f, -1.35f, -3.12f ), new Vec3( 0, 0, -0.04f ), 0.6f ) );
		if ( sick > 0f )
			warps.Add( new WarpNoise( sick * 0.25f, 0.4f, rng.Next() ) );

		// The lower teeth and tongue turn rigidly with the mandible; everything after the hinge
		// in the chain (lip pulls, smear, noise) still applies to them.
		var hingeAt = warps.FindIndex( w => w is WarpHinge );
		var rigidJaw = new List<SdfWarp>( warps );
		var upperOnly = new List<SdfWarp>( warps );
		if ( hingeAt >= 0 )
		{
			rigidJaw[hingeAt] = new WarpHinge( tmj, new Vec3( 0, 1, 0 ), jawDeg, new Vec3( 0, 0, 100f ), new Vec3( 0, 0, 1 ), 0.01f );
			upperOnly.RemoveAt( hingeAt );
		}

		void AddWith( PolyMesh m, int material, string t, List<SdfWarp> chain ) =>
			MeshParts.Add( new MeshPart { Mesh = m, Warps = chain, Placement = HumanParts.Placement( f ), Scale = f.Scale, Material = material, Tag = t, Bone = bone } );
		void Add( PolyMesh m, int material, string t ) => AddWith( m, material, t, warps );

		MeshParts.Add( new MeshPart { Mesh = skin, Warps = warps, Placement = HumanParts.Placement( f ), Scale = f.Scale, Material = SkinMat, Tag = tag, Bone = bone, Tension = true, Seed = rng.Next() } );

		// Eyes: a cornea bulge and a recessed iris carved into the eyeball, so the gaze reads.
		// A real corneal bulge (about 0.1 in at size) and a recessed iris.
		var eye = Sculpted( HumanParts.Mesh( "eye" ), new[] { Stroke.Dab( new Vec3( 0.67f, 0, 0 ), 0.5f, 0.12f ), Stroke.Dab( new Vec3( 0.72f, 0, 0 ), 0.26f, -0.08f ) } );
		// Each eye aimed on its own: a slight vergence, and for the wrecked ones one eye wandering.
		var wander = jawDrop > 1.5f || smear > 1.15f ? Rand( rng, 6f, 13f ) : 0f;
		foreach ( var sy in new[] { 1f, -1f } )
		{
			var yaw = -sy * 2f + (sy > 0 ? wander : 0f);
			var pitch = Rand( rng, -3f, 3f ) + (sy > 0 ? -wander * 0.4f : 0f);
			var rot = Xform.Rotate( new Vec3( 0, 0, 1 ), yaw * MathF.PI / 180f ) * Xform.Rotate( new Vec3( 0, 1, 0 ), pitch * MathF.PI / 180f );
			Add( Posed( eye, rot, new Vec3( 3.08f, 1.25f * sy, 0.1f ), 0.82f ), EyeMat, tag + "_eye" );
		}

		if ( jawDrop > 0.05f )
		{
			// Upper incisal edges a quarter inch behind the upper lip; the lower arch seated under
			// them, so the hinge carries it.
			var jaw = HumanParts.Mesh( "jaw" );
			var ts = 0.8f * MathF.Max( 1f, mouthWidth * 0.85f );
			var at = new Vec3( 4.02f - 1.15f * ts, 0, -3.27f );
			AddWith( Moved( KeepFaces( jaw, c => c.z > 0.02f && c.x > -1.3f ), at, ts ), BoneMat, tag + "_teeth", upperOnly );
			AddWith( Moved( KeepFaces( jaw, c => c.z <= 0.02f && c.x > -1.3f ), at + new Vec3( 0, 0, -0.08f ), ts ), BoneMat, tag + "_teeth", rigidJaw );
			// The tongue: a thick rounded tube from the throat to just behind the lower incisors,
			// flattened, with a median groove.
			var tongueField = new SdfSubtract( new SdfInstance(
				new SdfTube( new[] { new Vec3( 1.5f, 0, -3.95f ), new Vec3( 2.2f, 0, -3.75f ), new Vec3( 2.9f, 0, -3.62f ), new Vec3( 3.4f, 0, -3.62f ), new Vec3( 3.75f, 0, -3.7f ) },
					new[] { 0.55f, 0.6f, 0.55f, 0.45f, 0.28f } ), Xform.Identity ).Warp( new WarpStretch( new Vec3( 2.8f, 0, -3.7f ), new Vec3( 0, 0, 1 ), 0.55f ) ).Warp( new WarpStretch( new Vec3( 2.8f, 0, -3.7f ), new Vec3( 0, 1, 0 ), mouthWidth ) ),
				0.04f, new SdfCapsule( new Vec3( 2.0f, 0, -3.42f ), new Vec3( 3.6f, 0, -3.4f ), 0.06f ) );
			AddWith( MeshFromField( tongueField, 0.04f ), GumMat, tag + "_tongue", rigidJaw );
			Add( EllipsoidMesh( new Vec3( 2.2f, 0, -3.45f ), new Vec3( 1.75f, 1.75f * mouthWidth, 0.95f ), 14, 24 ), CavityMat, tag + "_mouth" );
		}

		// Neck root and hair, in head space, through the same warps (as a field instance).
		var neck = new SdfRoundCone( new Vec3( -1.25f, 0, -4.7f ), 2.75f, new Vec3( -1.6f, 0, neckEnd - 1.5f ), 3.0f );
		var fieldParts = new SdfBlend( 0.3f, neck );
		var hairInst = build == HeadBuild.None ? null : Hair( build, rng );
		var neckInst = new SdfInstance( neck, HumanParts.Placement( f ), f.Scale ) { Material = SkinMat, Tag = tag, Bone = bone };
		foreach ( var w in warps ) neckInst.Warp( w );
		if ( hairInst is null )
			return neckInst;
		var hair = new SdfInstance( hairInst, HumanParts.Placement( f ), f.Scale ) { Material = HairMat, Tag = tag + "_hair", Bone = bone };
		foreach ( var w in warps ) hair.Warp( w );
		return new SdfBlend( 0.15f, neckInst, hair );
	}

	/// <summary>
	/// The sculpted hand as a real mesh: forearm cut at the frame origin, fingers forward, back of
	/// the hand up. <paramref name="left"/> mirrors it. Returns the forearm stub as a field.
	/// </summary>
	public static Sdf RealHand( Random rng, Frame f, string tag, bool left, int bone = -1, float fingerStretch = 1f, float spread = 1f, float sick = 0.2f, bool stub = true )
	{
		var warps = new List<SdfWarp>();
		if ( MathF.Abs( fingerStretch - 1f ) > 0.02f )
			warps.Add( new WarpStretch( new Vec3( 7.8f, 0, 0 ), new Vec3( 1, 0, 0 ), fingerStretch, 3.5f ) );
		if ( MathF.Abs( spread - 1f ) > 0.02f )
			warps.Add( new WarpStretch( new Vec3( 8.5f, 0, 0 ), new Vec3( 0, 1, 0 ), spread, 3.2f ) );
		if ( sick > 0f )
			warps.Add( new WarpNoise( sick * 0.2f, 0.6f, rng.Next() ) );
		// Without the stub, the hand's own 3 in of forearm goes too (it would lie on the floor behind a
		// planted hand): only the wrist forward, met by whatever arm comes down to it.
		var handMesh = stub ? HumanParts.Mesh( "hand", left ) : KeepFaces( HumanParts.Mesh( "hand", left ), c => c.x > 2.3f );
		MeshParts.Add( new MeshPart { Mesh = handMesh, Warps = warps, Placement = HumanParts.Placement( f ), Scale = f.Scale, Material = SkinMat, Tag = tag, Bone = bone } );
		// The forearm stub runs back along the hand's axis: without an arm on that line (a planted
		// hand fed from above) it's a heel nub, so only the wrist's own volume.
		if ( !stub ) return new SdfSphere( f.P( 2.4f, 0, 0 ), f.S( 1.05f ) ) { Material = SkinMat, Tag = tag, Bone = bone };
		return new SdfRoundCone( f.P( 1.8f, 0, 0 ), f.S( 1.15f ), f.P( -2.5f, 0, 0 ), f.S( 1.35f ) ) { Material = SkinMat, Tag = tag, Bone = bone };
	}

	/// <summary>The sculpted hand placed by its wrist rather than its forearm cut, which sits about
	/// 3 in back up the arm; left is chosen from the side of the body the hand is on.</summary>
	public static Sdf HandAtWrist( Random rng, Frame wrist, string tag, int bone = -1, float fingerStretch = 1f, float spread = 1f, bool? left = null, bool stub = true )
	{
		var f = new Frame( wrist.Origin - wrist.Forward * 3.0f * wrist.Scale, wrist.Forward, wrist.Up, wrist.Scale );
		return RealHand( rng, f, tag, left ?? wrist.Origin.y > 0f, bone, fingerStretch, spread, stub: stub );
	}

	/// <summary>A bare sculpted foot as a real mesh, heel bottom at the origin, toe forward.
	/// Returns the ankle as a field.</summary>
	public static Sdf RealFoot( Random rng, Frame f, string tag, bool left, int bone = -1, float toeStretch = 1f )
	{
		var warps = new List<SdfWarp>();
		if ( MathF.Abs( toeStretch - 1f ) > 0.02f )
			warps.Add( new WarpStretch( new Vec3( 8.5f, 0, 0.5f ), new Vec3( 1, 0, 0 ), toeStretch, 2.5f ) );
		MeshParts.Add( new MeshPart { Mesh = HumanParts.Mesh( "foot", left ), Warps = warps, Placement = HumanParts.Placement( f ), Scale = f.Scale, Material = SkinMat, Tag = tag, Bone = bone } );
		return new SdfRoundCone( f.P( 1.7f, 0, 5.5f ), f.S( 1.4f ), f.P( 1.5f, 0, 9f ), f.S( 1.6f ) ) { Material = SkinMat, Tag = tag, Bone = bone };
	}

	/// <summary>
	/// The four head variants the creatures use, side by side 16 in apart along y, for judging
	/// the heads on their own: a crewman, a howl with the jaw torn open, a smeared face with
	/// extra eyes, and a droop with the jaw hanging.
	/// </summary>
	static Sdf HeadsTest( Options o )
	{
		var rng = new Random( o.Seed );
		var all = new SdfBlend( 0.3f );
		Frame At( float y ) => new( new Vec3( 0, y, 20 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) );
		all.Add( FatedHead( rng, At( 24 ), "crewman", HeadFate.Intact ) );
		all.Add( FatedHead( rng, At( 8 ), "howl", HeadFate.Howl ) );
		all.Add( FatedHead( rng, At( -8 ), "smear", HeadFate.Smear ) );
		all.Add( FatedHead( rng, At( -24 ), "droop", HeadFate.Droop ) );
		return all;
	}

	/// <summary>Real head, hands and a foot, plain and wrecked, to check the part library.</summary>
	static Sdf RealPartsTest( Options o )
	{
		var rng = new Random( o.Seed );
		var all = new SdfBlend( 0.3f );
		all.Add( RealHead( rng, new Frame( new Vec3( 0, 0, 20 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) ), "head", sick: 0f ) );
		all.Add( RealHead( rng, new Frame( new Vec3( 0, 14, 20 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) ), "headScream", smear: 1.2f, droop: 0.5f, jawDrop: 1.6f, mouthWidth: 1.25f, extraEyes: 0 ) );
		all.Add( RealHand( rng, new Frame( new Vec3( -2, -14, 1.2f ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) ), "handR", left: false ) );
		all.Add( RealHand( rng, new Frame( new Vec3( -2, -28, 1.2f ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) ), "handL", left: true, fingerStretch: 1.6f, spread: 1.3f ) );
		all.Add( RealFoot( rng, new Frame( new Vec3( -4, -40, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) ), "foot", left: false ) );
		return all;
	}
}
