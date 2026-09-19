using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// Which part of a body a face belongs to, read from the rig rather than from the shape.
///
/// A GARMENT IS A RECIPE, NOT A MESH. "Torso and the top of the upper arms" means the same thing on
/// the Citizen, on Camhead and on Gearhead, and the rig is the one thing all three have in common.
/// The shape comes from the body itself - see <see cref="GarmentFit"/>.
/// </summary>
public enum BodyRegion
{
	None,
	Head,
	Neck,
	Torso,
	Hips,
	UpperArm,
	LowerArm,
	Hand,
	UpperLeg,
	LowerLeg,
	Foot,
}

/// <summary>A garment's coverage: which regions, and how far along each region's bone.</summary>
public sealed class GarmentRecipe
{
	public string Name;

	/// <summary>
	/// Region, the span of the bone it covers (0 at the bone's head, 1 at its tail), and which side
	/// of the body it is on: 0 both, +1 left, -1 right.
	///
	/// THE SIDE IS WHAT MAKES REAL CLOTHES POSSIBLE. Everything a span system builds is symmetric by
	/// construction, and a great deal of clothing is not - one sleeve, a strap over one shoulder, a
	/// shirt hanging off one side. Tagging the span rather than the recipe means asymmetry composes:
	/// a garment can be one-sleeved and off-shoulder at once, and each span still means what it
	/// meant before for anything that does not use it.
	/// </summary>
	public List<(BodyRegion Region, float From, float To, int Side)> Spans = new();

	public static readonly string[] Names =
	{
		"T-shirt", "Long sleeve", "Tank top", "Trousers", "Shorts", "Beanie", "Gloves", "Socks", "Bodysuit",
	};

	/// <summary>Which sleeves or legs a recipe keeps. Sides are the body's own - left is +y, the
	/// way Source measures it - so "Left only" is the wearer's left, not the viewer's.</summary>
	public static readonly string[] SideNames = { "Both", "Left only", "Right only" };

	/// <summary>SideNames index to the span tag: both, left (+y), right (-y).</summary>
	public static int SideOf( int index ) => index switch { 1 => 1, 2 => -1, _ => 0 };

	/// <summary>
	/// The recipe with its two lengths applied.
	///
	/// <paramref name="length"/> is the hem: how far a top reaches down over the hips, how far a leg
	/// reaches down the leg, how deep a hat sits. <paramref name="sleeve"/> is how far down the arm a
	/// sleeve goes, 0 bare to 1 at the wrist. Both are fractions so they mean the same thing on a
	/// short body and a tall one.
	/// </summary>
	/// <param name="neckline">How far down the torso the garment STARTS, 0 at the neck. This is the
	/// top edge, the counterpart of <paramref name="length"/>: raise it and the collar becomes a
	/// scoop, then bares the shoulders, then becomes a tube top. The sleeve starts at the same drop,
	/// because a sleeve still attached to a shoulder the body of the shirt has left behind is a
	/// floating ring, not a garment.</param>
	/// <param name="sleeveSide">Which sleeve to keep: 0 both, +1 left, -1 right.</param>
	public static GarmentRecipe Build( int index, float length, float sleeve, float neckline = 0f,
		int sleeveSide = 0 )
	{
		length = Math.Clamp( length, 0f, 1f );
		sleeve = Math.Clamp( sleeve, 0f, 1f );

		// Capped well short of 1: a neckline at the hem is a garment with no garment left, and the
		// fit would refuse with "covers nothing" rather than explain itself.
		neckline = Math.Clamp( neckline, 0f, 0.8f );

		var r = new GarmentRecipe { Name = Names[Math.Clamp( index, 0, Names.Length - 1 )] };

		void Arms( float s )
		{
			if ( s <= 0f )
				return;

			// The shoulder drop, shared with the torso. Halved because the upper arm's span runs
			// shoulder to elbow and the torso's runs neck to waist - the same fraction would drop
			// the sleeve twice as far down the arm as the neckline dropped down the chest.
			var from = Math.Min( 0.6f, neckline * 0.5f );

			r.Spans.Add( (BodyRegion.UpperArm, from, Math.Max( from, Math.Min( 1f, s * 2f ) ), sleeveSide) );

			if ( s > 0.5f )
				r.Spans.Add( (BodyRegion.LowerArm, 0f, (s - 0.5f) * 2f, sleeveSide) );
		}

		void Legs( float s )
		{
			r.Spans.Add( (BodyRegion.UpperLeg, 0f, Math.Min( 1f, Math.Max( 0.05f, s ) * 2f ), 0) );

			if ( s > 0.5f )
				r.Spans.Add( (BodyRegion.LowerLeg, 0f, (s - 0.5f) * 2f, 0) );
		}

		switch ( r.Name )
		{
			case "T-shirt":
			case "Long sleeve":
			case "Tank top":
				r.Spans.Add( (BodyRegion.Torso, neckline, 1f, 0) );
				r.Spans.Add( (BodyRegion.Hips, 0f, length, 0) );
				Arms( r.Name == "Tank top" ? 0f : sleeve );
				break;

			case "Trousers":
			case "Shorts":
				r.Spans.Add( (BodyRegion.Hips, 0f, 1f, 0) );
				Legs( length );
				break;

			case "Beanie":
				r.Spans.Add( (BodyRegion.Head, 1f - Math.Max( 0.1f, length ), 1.01f, 0) );
				break;

			case "Gloves":
				r.Spans.Add( (BodyRegion.Hand, 0f, 1.01f, sleeveSide) );
				r.Spans.Add( (BodyRegion.LowerArm, 1f - length * 0.5f, 1.01f, sleeveSide) );
				break;

			case "Socks":
				r.Spans.Add( (BodyRegion.Foot, 0f, 1.01f, 0) );
				r.Spans.Add( (BodyRegion.LowerLeg, 1f - length * 0.6f, 1.01f, 0) );
				break;

			case "Bodysuit":
				r.Spans.Add( (BodyRegion.Torso, neckline, 1f, 0) );
				r.Spans.Add( (BodyRegion.Hips, 0f, 1f, 0) );
				Arms( sleeve );
				Legs( length );
				break;
		}

		return r;
	}

	/// <summary>The defaults a recipe opens with: (length, sleeve).</summary>
	public static (float Length, float Sleeve) Defaults( int index ) => Names[Math.Clamp( index, 0, Names.Length - 1 )] switch
	{
		"T-shirt" => (0.6f, 0.3f),
		"Long sleeve" => (0.7f, 1f),
		"Tank top" => (0.6f, 0f),
		"Trousers" => (1f, 0f),
		"Shorts" => (0.4f, 0f),
		"Beanie" => (0.55f, 0f),
		"Gloves" => (0.3f, 0f),
		"Socks" => (0.4f, 0f),
		_ => (1f, 1f),
	};

	/// <param name="side">Which side of the body the face is on: +1 left, -1 right. A span tagged 0
	/// covers both, which is what every symmetric garment is made of.</param>
	public bool Covers( BodyRegion region, float t, int side = 0 ) =>
		Spans.Any( s => s.Region == region && t >= s.From - 1e-4f && t <= s.To + 1e-4f
			&& (s.Side == 0 || s.Side == side) );
}

/// <summary>
/// Classify a skeleton's bones into regions by name. Covers Citizen's names (<c>arm_upper_R</c>,
/// <c>spine_2</c>), Unreal/Mixamo style (<c>upperarm_r</c>, <c>thigh_l</c>, <c>mixamorig:LeftForeArm</c>)
/// and plain English ones people type into the rig panel.
/// </summary>
public static class BodyRegions
{
	public static BodyRegion Classify( string boneName )
	{
		var n = (boneName ?? "").ToLowerInvariant();

		if ( n.Contains( "twist" ) )
			n = n.Replace( "twist", "" );

		bool Any( params string[] keys ) => keys.Any( n.Contains );

		if ( Any( "finger", "thumb", "index", "middle", "ring_", "pinky", "hand" ) ) return BodyRegion.Hand;
		if ( Any( "toe", "foot", "ball_" ) ) return BodyRegion.Foot;
		if ( Any( "head", "eye", "jaw", "face" ) ) return BodyRegion.Head;
		if ( Any( "neck" ) ) return BodyRegion.Neck;
		if ( Any( "arm_lower", "forearm", "lowerarm", "lower_arm", "elbow" ) ) return BodyRegion.LowerArm;
		if ( Any( "arm_upper", "upperarm", "upper_arm" ) ) return BodyRegion.UpperArm;
		if ( Any( "leg_lower", "calf", "shin", "lowerleg", "lower_leg", "knee" ) ) return BodyRegion.LowerLeg;
		if ( Any( "leg_upper", "thigh", "upperleg", "upper_leg" ) ) return BodyRegion.UpperLeg;
		if ( Any( "pelvis", "hip" ) ) return BodyRegion.Hips;
		if ( Any( "spine", "chest", "clavicle", "torso", "abdomen", "belly", "collar", "shoulder" ) ) return BodyRegion.Torso;
		if ( Any( "arm" ) ) return BodyRegion.UpperArm;
		if ( Any( "leg" ) ) return BodyRegion.UpperLeg;

		return BodyRegion.None;
	}

	/// <summary>
	/// The region nearest a point, and how far along that region's bone it sits.
	///
	/// "How far along" is measured on the nearest bone of the region, and a region with a chain of
	/// bones (a spine, a twist chain) is measured along the whole chain, so 0.5 is halfway down the
	/// upper arm whether that arm is one bone or three.
	/// </summary>
	public sealed class Map
	{
		readonly List<(BodyRegion Region, Vec3 A, Vec3 B)> _segments = new();
		readonly List<int> _segmentBones = new();

		public int Count => _segments.Count;

		/// <summary>Every region bone as a segment, for fitting capsules to.</summary>
		public IReadOnlyList<(BodyRegion Region, Vec3 A, Vec3 B)> Segments => _segments;

		/// <summary>The skeleton bone each of <see cref="Segments"/> came from.</summary>
		public IReadOnlyList<int> SegmentBones => _segmentBones;

		public Map( Skeleton skeleton )
		{

			for ( var i = 0; i < skeleton.Count; i++ )
			{
				var region = Classify( skeleton.Bones[i].Name );

				if ( region == BodyRegion.None )
					continue;

				var a = skeleton.HeadWorld( i );
				var b = skeleton.TailWorld( i );

				if ( (b - a).LengthSquared < 1e-8f )
					continue;

				_segments.Add( (region, a, b) );
				_segmentBones.Add( i );
			}

		}

		/// <summary>Nearest region to <paramref name="p"/>, and t along it (0 at the joint nearest
		/// the body's centre, 1 at the far end).</summary>
		public (BodyRegion Region, float T) Locate( Vec3 p ) => Locate( p, BodyRegion.None );

		/// <summary>As <see cref="Locate(Vec3)"/>, but only among <paramref name="only"/>'s bones -
		/// for a surface that already knows which region it belongs to, such as an envelope hull.
		/// None means any region.</summary>
		public (BodyRegion Region, float T) Locate( Vec3 p, BodyRegion only )
		{
			var best = float.MaxValue;
			var region = BodyRegion.None;
			var chainT = 0f;

			foreach ( var (r, a, b ) in _segments )
			{
				if ( only != BodyRegion.None && r != only )
					continue;

				var ab = b - a;
				var t = Math.Clamp( Vec3.Dot( p - a, ab ) / ab.LengthSquared, 0f, 1f );
				var d = (a + ab * t - p).LengthSquared;

				if ( d < best )
				{
					best = d;
					region = r;
					chainT = t;
				}
			}

			if ( region == BodyRegion.None )
				return (region, 0f);

			// Chains: measure t over the whole region on this side, start to end, so a limb reads
			// 0..1 shoulder to wrist whether it is one bone or a bone with twist bones laid over it
			// (Citizen's are - summing their lengths would count the arm twice).
			if ( Chain( region, Side( p ), out var from, out var to ) )
			{
				var axis = to - from;
				chainT = Math.Clamp( Vec3.Dot( p - from, axis ) / axis.LengthSquared, 0f, 1f );
			}

			// The head is measured bottom-up (a beanie covers the TOP), the torso and hips top-down
			// (a hem reads down from the collar).
			if ( region == BodyRegion.Head )
				chainT = 1f - chainT;

			return (region, chainT);
		}

		/// <summary>
		/// The line a region runs along, on one side of the body: from the end nearest the body's
		/// centre (the top, for anything on the spine or a leg) to the far end. This is the axis t
		/// is measured on and the axis a limb's UVs wrap around, so both agree about where a
		/// sleeve starts. False for a region the skeleton does not have.
		/// </summary>
		public bool Chain( BodyRegion region, int side, out Vec3 from, out Vec3 to )
		{
			from = to = Vec3.Zero;
			var ends = new List<Vec3>();

			foreach ( var s in _segments )
			{
				if ( s.Region != region || (!IsCentral( region ) && Side( (s.A + s.B) * 0.5f ) != side) )
					continue;

				ends.Add( s.A );
				ends.Add( s.B );
			}

			if ( ends.Count < 2 )
				return false;

			from = IsCentral( region ) || region is BodyRegion.UpperLeg or BodyRegion.LowerLeg or BodyRegion.Foot
				? ends.OrderByDescending( e => e.z ).First()
				: ends.OrderBy( e => MathF.Abs( e.y ) ).First();

			var start = from;
			to = ends.OrderByDescending( e => (e - start).LengthSquared ).First();

			return (to - from).LengthSquared > 1e-8f;
		}

		public static bool IsCentral( BodyRegion r ) => r is BodyRegion.Head or BodyRegion.Neck or BodyRegion.Torso or BodyRegion.Hips;

		static int Side( Vec3 p ) => p.y > 0 ? 1 : -1;


	}
}

/// <summary>
/// Make a garment that fits a particular body.
///
/// THE BODY DOES MOST OF THE WORK. The rig only says which faces a recipe covers; the garment is then
/// lifted off the body's own surface, so it starts with that body's proportions and topology - which
/// is also the topology the s&box quality bar asks clothing to follow. After that:
///
/// 1. every vertex is pushed out to the fit offset along its normal,
/// 2. collision: any vertex closer than the clearance to ANY body - the chest pressing into an arm, a
///    collar into a cog - is pushed back out along that surface's normal, and smoothed, a few times,
/// 3. the openings flare out if asked,
/// 4. the sheet is given thickness with <see cref="MeshSolidify"/>.
///
/// It does not drape. Folds come from the flare and from sculpting until editor cloth arrives.
/// </summary>
public static class GarmentFit
{
	public sealed class Options
	{
		/// <summary>Round colliders for the drape instead of the bodies' triangles. See Capsule.
		/// Null drapes against the meshes, as before.</summary>
		public List<Capsule> Capsules;

		/// <summary>How far the drape may move the cloth from where the fit put it. 0 takes the
		/// default: a couple of inches, room to settle and no more.</summary>
		public float Tether;

		/// <summary>How far off the body the garment sits, in model units (inches on the Citizen).</summary>
		public float Offset = 0.4f;

		/// <summary>The closest any part of the garment may come to any body, after collision.</summary>
		public float Clearance = 0.15f;

		/// <summary>How far the openings (hems, cuffs) flare out beyond the offset.</summary>
		public float Flare;

		/// <summary>How far up from an opening the flare reaches.</summary>
		public float FlareReach = 4f;

		/// <summary>Cloth thickness; 0 leaves a single-sided sheet.</summary>
		public float Thickness = 0.12f;

		/// <summary>
		/// Inflate the garment along its own normals, in inches. Puffed sleeves, a padded jacket,
		/// a quilted vest.
		///
		/// Marvelous Designer calls this Pressure and Simply Cloth calls it the same; it is on both
		/// tools' front page because it is the cheapest way to turn a fitted shape into a different
		/// GARMENT rather than the same garment at a different size. Looseness moves the whole
		/// surface away from the body uniformly, which reads as a bigger shirt. This pushes along
		/// the local normal, which reads as stuffing.
		/// </summary>
		public float Puff;

		/// <summary>
		/// Pull the openings in towards their own centres, 0 none to 1 closed. A waistband, elastic
		/// cuffs, a gathered ankle on a jogger.
		///
		/// The hems are the only part of a garment that has nothing holding it in, so they are the
		/// part that reads as loose whatever the rest is doing - which is why "tighten the waistband"
		/// is a thing people ask for and "make the shirt smaller" is not the same request.
		/// </summary>
		public float Cinch;

		/// <summary>How far up from an opening the cinch reaches, in inches. A waistband is an inch;
		/// a gathered ankle is three.</summary>
		public float CinchReach = 1.5f;

		/// <summary>
		/// Creases along the surface, 0 smooth to 1 rumpled.
		///
		/// NOT A SUBSTITUTE FOR THE DRAPE. The solver makes the folds that gravity and collision
		/// actually produce, and those are the ones that sit in the right places. This is the fine
		/// noise on top - the crumple of cloth that has been worn - which a solver would need a much
		/// finer mesh and much longer to find, for something nobody looks at closely enough to tell
		/// apart from noise.
		/// </summary>
		public float Wrinkles;

		/// <summary>Size of a wrinkle, in inches. Small is linen, large is a heavy coat.</summary>
		public float WrinkleScale = 2.2f;

		/// <summary>Collision passes. Each pass pushes out and relaxes once.</summary>
		public int Passes = 4;

		public int MaterialSlot;

		/// <summary>Let the garment settle under gravity against the bodies before it is thickened.</summary>
		public bool Drape = true;

		/// <summary>
		/// What the cloth is made of, as a set of solver settings.
		///
		/// THE FIRST THING EITHER OF THE BIG TOOLS ASKS. Marvelous Designer opens on a fabric
		/// library and Simply Cloth ships presets, because the difference between cotton and leather
		/// is most of the difference between two garments of the same shape - it decides whether the
		/// folds are many and soft or few and standing. Stiffness alone could only say how much a
		/// garment resists being stretched, which is the one property people do not think in.
		///
		/// A STARTING POINT, NOT A MEASUREMENT. These are artistic presets tuned to look right at
		/// this scale, not the physical constants of real textiles; Stiffness still overrides the
		/// stretch on top of whichever is chosen.
		/// </summary>
		public Fabric Fabric = Fabric.Cotton;

		/// <summary>Simulation steps, at 1/60 s each. More settles further and costs more.</summary>
		public int DrapeSteps = 40;

		/// <summary>0 = stretchy jersey, 1 = stiff canvas. How hard the edges hold their fitted length.</summary>
		public float Stiffness = 0.8f;

		/// <summary>Pin the top fraction of the garment (by height) where it is, so trousers keep their
		/// waistband. 0 pins nothing - a top rests on the shoulders by collision alone.</summary>
		public float PinTop;

		/// <summary>
		/// Stop the garment passing through itself where it folds. OFF by default, and deliberately:
		/// it is several times the cost of the whole rest of the drape, and this runs on every
		/// rebuild while a slider moves. The fit pass has already pushed the garment out of the body,
		/// so what this buys is folds resolving against each other - worth asking for on a finished
		/// garment, not worth paying for on every drag.
		/// </summary>
		public bool SelfCollision;
	}

	public sealed class Result
	{
		public PolyMesh Mesh;
		public int SourceFaces;
		public int DroppedInside;
		public bool Solidified;
		public string SolidifyProblem;
	}

	/// <summary>
	/// Build <paramref name="recipe"/> on <paramref name="bodies"/>, located with <paramref name="map"/>.
	/// Returns null when the recipe covers no face at all.
	/// </summary>
	public static Result Fit( IReadOnlyList<PolyMesh> wearers, BodyRegions.Map map, GarmentRecipe recipe, Options o,
		IReadOnlyList<PolyMesh> otherGarments = null, IReadOnlyList<BodyRegion> wearerRegions = null )
	{
		// Everything collides; only the wearers are cut from.
		var bodies = wearers.Concat( otherGarments ?? Array.Empty<PolyMesh>() ).ToList();
		var trees = bodies.Select( MeshBVH.Build ).ToList();
		var closed = bodies.Select( b => MeshValidator.Validate( b ).IsClosed ).ToList();
		var result = new Result { Mesh = new PolyMesh() };
		var garment = result.Mesh;
		var remap = new Dictionary<(int Body, int Vertex), int>();
		var cutRemap = new Dictionary<(int Body, int A, int B, BodyRegion Region, int End), int>();

		for ( var bi = 0; bi < wearers.Count; bi++ )
		{
			var body = bodies[bi];

			// A wearer that already knows its region - an envelope hull - keeps it, and is only
			// measured along it. Left to the nearest bone, the front of Camhead's torso hull was
			// claimed by the arms hanging beside it and cut out of the shirt.
			var only = wearerRegions is not null && bi < wearerRegions.Count ? wearerRegions[bi] : BodyRegion.None;

			int Vertex( int vi )
			{
				var key = (bi, vi);

				if ( !remap.TryGetValue( key, out var index ) )
				{
					index = garment.AddVertex( body.Positions[vi] );
					remap[key] = index;
				}

				return index;
			}

			for ( var fi = 0; fi < body.FaceCount; fi++ )
			{
				var face = body.Faces[fi];

				if ( face.Count < 3 )
					continue;

				var centroid = body.FaceCentroid( face );
				var (region, t) = map.Locate( centroid, only );

				// The side is the face's own position, not the bone's: +y is the body's left, the
				// way Source measures it. Taken from the face rather than from the bone name so an
				// imported skeleton with no _L / _R convention still makes a one-sleeved shirt.
				var side = centroid.y > 0 ? 1 : -1;
				var spans = recipe.Spans.Where( s => s.Region == region && (s.Side == 0 || s.Side == side) ).ToList();

				if ( spans.Count == 0 )
					continue;

				static bool Within( (BodyRegion Region, float From, float To, int Side) s, float t ) =>
					t >= s.From - 1e-4f && t <= s.To + 1e-4f;

				// THE FACE IS CUT AT THE HEM, not kept or dropped whole. Whole faces gave every
				// opening a sawtooth edge one face deep - the jagged cuffs and collars that said
				// "generated" from across the room - and no amount of relaxing put a straight line
				// back. The span's two ends are two planes across the bone (t is linear along it),
				// and the polygon is clipped to each (Sutherland-Hodgman), so the edge is a clean
				// line round the limb. An end at 0 or 1 is open - a sleeve runs into the shoulder,
				// a glove past the fingertips - and is not cut. A face is in if any corner of it
				// is, and it is the span round the centre, or failing that round a corner, that
				// does the cutting.
				var nodes = new List<CutNode>( face.Count + 2 );
				var span = spans[0];
				var chained = map.Chain( region, side, out var from, out var to );

				if ( chained )
				{
					var axis = to - from;
					var lengthSq = axis.LengthSquared;

					for ( var c = 0; c < face.Count; c++ )
					{
						var vi = face.Indices[c];
						var p = body.Positions[vi];
						nodes.Add( new CutNode( vi, vi, -1, p, Vec3.Dot( p - from, axis ) / lengthSq ) );
					}

					var covering = spans.FirstOrDefault( s => Within( s, t ) );

					if ( covering.Region != region )
						covering = spans.FirstOrDefault( s => nodes.Any( n => Within( s, n.T ) ) );

					if ( covering.Region != region )
						continue;

					span = covering;
				}
				else if ( !spans.Any( s => Within( s, t ) ) )
					continue;

				// A face buried inside another part - an arm cylinder's end inside the torso block,
				// the way CAD characters are built - would be pushed out onto the surface as a crumpled
				// flap. It is not skin; skip it.
				if ( InsideOther( bodies, trees, closed, bi, centroid ) )
				{
					result.DroppedInside++;
					continue;
				}

				if ( chained )
				{
					if ( span.From > 0f )
						nodes = ClipSpan( nodes, span.From, keepAbove: true, end: 0 );

					if ( span.To < 1f && nodes.Count >= 3 )
						nodes = ClipSpan( nodes, span.To, keepAbove: false, end: 1 );

					if ( nodes.Count < 3 )
						continue;
				}
				else
				{
					for ( var c = 0; c < face.Count; c++ )
						nodes.Add( new CutNode( face.Indices[c], face.Indices[c], -1, body.Positions[face.Indices[c]], 0f ) );
				}

				var indices = new int[nodes.Count];

				for ( var c = 0; c < nodes.Count; c++ )
				{
					var node = nodes[c];

					// An original corner; or a cut point on an original edge, which the face on the
					// other side of that edge finds again by the same key, so the cut is watertight.
					if ( node.End < 0 )
						indices[c] = Vertex( node.A );
					else if ( node.A >= 0 )
					{
						var (lo, hi) = node.A < node.B ? (node.A, node.B) : (node.B, node.A);
						var key = (bi, lo, hi, region, node.End);

						if ( !cutRemap.TryGetValue( key, out var index ) )
						{
							index = garment.AddVertex( node.P );
							cutRemap[key] = index;
						}

						indices[c] = index;
					}
					else
						indices[c] = garment.AddVertex( node.P );
				}

				garment.AddFace( indices, null, o.MaterialSlot );
				result.SourceFaces++;
			}
		}

		if ( garment.FaceCount == 0 )
			return null;

		var start = garment.Positions.ToArray();
		var normals = garment.ComputeVertexNormals();
		var boundary = BoundaryVertices( garment );
		var flare = FlareWeights( garment, boundary, o.FlareReach );

		// 1. Out to the fit offset, flaring toward the openings.
		for ( var i = 0; i < garment.VertexCount; i++ )
			garment.Positions[i] = start[i] + normals[i] * (o.Offset + o.Flare * flare[i]);

		// 2. Collision against every body, with a relax between passes so a push does not leave a dent.
		var neighbours = Neighbours( garment );
		var reach = o.Offset + o.Flare + o.Clearance + 2f;

		for ( var pass = 0; pass < Math.Max( 1, o.Passes ); pass++ )
		{
			PushOut( garment, bodies, trees, o.Clearance, reach );
			Relax( garment, neighbours, boundary, 0.35f );
		}

		PushOut( garment, bodies, trees, o.Clearance, reach );

		if ( o.Drape && o.DrapeSteps > 0 )
			Drape( garment, bodies, o );

		// SHAPING COMES AFTER THE DRAPE AND BEFORE THE THICKNESS, and the order is the whole
		// correctness argument. After the drape, because these are deliberate style decisions and
		// the solver would flatten a puff straight back out as if it were an error. Before the
		// thickness, because Solidify offsets the surface it is given - shaping a solid would move
		// its inner and outer shells apart by different amounts and split the garment open.
		//
		// Then pushed out again: a puff or a wrinkle can drive the cloth back into the body, and a
		// garment that clips is worse than one that is not quite as puffy as asked.
		if ( o.Puff > 0f )
			Inflate( garment, o.Puff );

		if ( o.Cinch > 0f )
			Cinch( garment, o.Cinch, o.CinchReach );

		if ( o.Wrinkles > 0f )
			Wrinkle( garment, o.Wrinkles, o.WrinkleScale );

		if ( o.Puff > 0f || o.Wrinkles > 0f )
			PushOut( garment, bodies, trees, o.Clearance, reach );

		LimbUVs( garment, map );

		if ( o.Thickness > 0f )
		{
			try
			{
				garment = MeshSolidify.Solidify( garment, o.Thickness );
				result.Solidified = true;
			}
			catch ( Exception e )
			{
				result.SolidifyProblem = e.Message;
			}
		}

		result.Mesh = garment;
		return result;
	}

	/// <summary>A corner of a face being cut: an original vertex (A == B, End -1), or a point on
	/// the original edge A-B where the span's end (0 the start, 1 the finish) crosses it. A is -1
	/// for a point on an edge the cut itself made, which no other face shares.</summary>
	readonly record struct CutNode( int A, int B, int End, Vec3 P, float T );

	/// <summary>One Sutherland-Hodgman pass: keep the side of t = limit the span is on.</summary>
	static List<CutNode> ClipSpan( List<CutNode> nodes, float limit, bool keepAbove, int end )
	{
		var kept = new List<CutNode>( nodes.Count + 2 );

		for ( var i = 0; i < nodes.Count; i++ )
		{
			var cur = nodes[i];
			var nxt = nodes[(i + 1) % nodes.Count];
			var inCur = keepAbove ? cur.T >= limit : cur.T <= limit;
			var inNxt = keepAbove ? nxt.T >= limit : nxt.T <= limit;

			if ( inCur )
				kept.Add( cur );

			if ( inCur == inNxt )
				continue;

			var s = Math.Clamp( (limit - cur.T) / (nxt.T - cur.T), 0f, 1f );
			var (a, b) = EdgeUnder( cur, nxt );
			kept.Add( new CutNode( a, b, end, cur.P + (nxt.P - cur.P) * s, limit ) );
		}

		return kept;
	}

	/// <summary>The original edge a segment between two nodes lies on, or (-1, -1) when it is a
	/// line the first cut drew across the face and nothing else can share.</summary>
	static (int, int) EdgeUnder( CutNode x, CutNode y )
	{
		static bool OnEdge( CutNode cut, int vertex ) => cut.A == vertex || cut.B == vertex;

		if ( x.End < 0 && y.End < 0 )
			return (x.A, y.A);

		if ( x.End < 0 && y.A >= 0 && OnEdge( y, x.A ) )
			return (y.A, y.B);

		if ( y.End < 0 && x.A >= 0 && OnEdge( x, y.A ) )
			return (x.A, x.B);

		if ( x.End >= 0 && y.End >= 0 && x.A >= 0 && x.A == y.A && x.B == y.B )
			return (x.A, x.B);

		return (-1, -1);
	}

	/// <summary>
	/// Push every vertex out along its own normal. Pressure, in Marvelous Designer's terms.
	///
	/// The normals are computed ONCE, from the shape as it arrives, rather than per vertex as it
	/// moves. Recomputing as you go makes a bulge feed itself - the more a region inflates the more
	/// outward its neighbours' normals become - and a padded jacket turns into a balloon.
	/// </summary>
	static void Inflate( PolyMesh garment, float amount )
	{
		var normals = garment.ComputeVertexNormals();

		for ( var i = 0; i < garment.VertexCount; i++ )
			garment.Positions[i] += normals[i] * amount;
	}

	/// <summary>
	/// Pull the openings in towards their own centres: waistbands, elastic cuffs, gathered ankles.
	///
	/// The pull falls off with distance from the edge, so the garment is gathered at the hem and
	/// untouched an inch above it - which is what elastic in a casing actually does, and what makes
	/// this read as a waistband rather than as a garment that tapers.
	/// </summary>
	static void Cinch( PolyMesh garment, float amount, float reach )
	{
		var loops = GarmentTrim.OrderedBoundaryLoops( garment );

		if ( loops.Count == 0 || reach <= 1e-4f )
			return;

		amount = Math.Clamp( amount, 0f, 1f );

		// Every vertex moves towards the centre of the NEAREST opening, weighted by how close it is
		// to that opening's edge. Nearest rather than "the opening it belongs to": a garment has no
		// notion of which panel a vertex is on, and distance answers the question correctly anyway.
		var centres = new List<Vec3>();
		var rings = new List<List<Vec3>>();

		foreach ( var loop in loops )
		{
			var points = loop.Select( v => garment.Positions[v] ).ToList();
			var centre = Vec3.Zero;

			foreach ( var p in points )
				centre += p;

			centres.Add( centre / points.Count );
			rings.Add( points );
		}

		for ( var i = 0; i < garment.VertexCount; i++ )
		{
			var p = garment.Positions[i];
			var best = float.MaxValue;
			var which = -1;

			for ( var li = 0; li < rings.Count; li++ )
			{
				foreach ( var q in rings[li] )
				{
					var d = (q - p).LengthSquared;

					if ( d >= best )
						continue;

					best = d;
					which = li;
				}
			}

			if ( which < 0 )
				continue;

			var distance = MathF.Sqrt( best );

			if ( distance >= reach )
				continue;

			// Smoothstep rather than linear, so the top of the band blends into the garment instead
			// of leaving a crease exactly `reach` inches up.
			var t = 1f - distance / reach;
			var falloff = t * t * (3f - 2f * t);

			var centre = centres[which];

			// Along the opening's own plane only: pulling towards the centre POINT would drag the
			// hem up the body as well as in, and shorten the garment as a side effect of tightening
			// it. Only the part of the offset perpendicular to the ring's axis is wanted.
			var toCentre = centre - p;
			var axis = RingAxis( rings[which], centre );

			toCentre -= axis * Vec3.Dot( toCentre, axis );

			garment.Positions[i] = p + toCentre * (amount * falloff);
		}
	}

	/// <summary>The direction an opening faces: the normal of the plane its vertices best lie in.
	/// Found by summing the fan of cross products around the ring, which is the ring's own area
	/// vector and needs no fitting.</summary>
	static Vec3 RingAxis( List<Vec3> ring, Vec3 centre )
	{
		var area = Vec3.Zero;

		for ( var i = 0; i < ring.Count; i++ )
			area += Vec3.Cross( ring[i] - centre, ring[(i + 1) % ring.Count] - centre );

		return area.LengthSquared < 1e-8f ? new Vec3( 0, 0, 1 ) : area.Normal;
	}

	/// <summary>
	/// Fine creases along the surface. See Options.Wrinkles for what this is and is not.
	///
	/// Three octaves of a cheap value noise over the vertex's own position, displaced along the
	/// normal. Position-based rather than UV-based on purpose: UVs are laid out per panel, so a
	/// UV-space wrinkle would stop dead at every seam, and cloth does not.
	/// </summary>
	static void Wrinkle( PolyMesh garment, float amount, float scale )
	{
		if ( scale <= 1e-3f )
			return;

		var normals = garment.ComputeVertexNormals();

		for ( var i = 0; i < garment.VertexCount; i++ )
		{
			var p = garment.Positions[i] / scale;

			var n = Noise( p ) * 0.6f
				+ Noise( p * 2.3f ) * 0.3f
				+ Noise( p * 4.7f ) * 0.1f;

			garment.Positions[i] += normals[i] * (n * amount * scale * 0.09f);
		}
	}

	/// <summary>A cheap deterministic value noise in -1..1. Deterministic matters more than
	/// quality: the same garment must rebuild to the same wrinkles, or every rebuild would be a
	/// different model and undo would be a surprise.</summary>
	static float Noise( Vec3 p )
	{
		var x = MathF.Sin( p.x * 12.9898f + p.y * 78.233f + p.z * 37.719f ) * 43758.5453f;
		var y = MathF.Sin( p.x * 93.9898f + p.y * 67.345f + p.z * 24.123f ) * 28001.8384f;

		return (x - MathF.Floor( x )) + (y - MathF.Floor( y )) - 1f;
	}

	static bool InsideOther( IReadOnlyList<PolyMesh> bodies, List<MeshBVH> trees, List<bool> closed, int self, Vec3 p )
	{
		for ( var bi = 0; bi < bodies.Count; bi++ )
		{
			if ( bi == self || !closed[bi] )
				continue;

			if ( trees[bi].NearestSurface( bodies[bi], p, 1e6f ) is not { } hit )
				continue;

			if ( hit.Distance > 1e-4f && Vec3.Dot( p - hit.Point, hit.Normal ) < 0 )
				return true;
		}

		return false;
	}

	internal static void PushOut( PolyMesh garment, IReadOnlyList<PolyMesh> bodies, List<MeshBVH> trees, float clearance, float reach )
	{
		for ( var i = 0; i < garment.VertexCount; i++ )
		{
			var p = garment.Positions[i];

			for ( var bi = 0; bi < bodies.Count; bi++ )
			{
				if ( trees[bi].NearestSurface( bodies[bi], p, reach ) is not { } hit )
					continue;

				var along = Vec3.Dot( p - hit.Point, hit.Normal );

				if ( along < clearance )
					p += hit.Normal * (clearance - along);
			}

			garment.Positions[i] = p;
		}
	}

	static void Relax( PolyMesh mesh, List<int>[] neighbours, bool[] boundary, float amount )
	{
		var next = mesh.Positions.ToArray();

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var n = neighbours[i];

			if ( n.Count == 0 )
				continue;

			var avg = Vec3.Zero;

			foreach ( var j in n )
				avg += mesh.Positions[j];

			avg /= n.Count;
			next[i] = Vec3.Lerp( mesh.Positions[i], avg, boundary[i] ? amount * 0.3f : amount );
		}

		for ( var i = 0; i < next.Length; i++ )
			mesh.Positions[i] = next[i];
	}

	static List<int>[] Neighbours( PolyMesh mesh )
	{
		var sets = new HashSet<int>[mesh.VertexCount];

		for ( var i = 0; i < sets.Length; i++ )
			sets[i] = new HashSet<int>();

		foreach ( var f in mesh.Faces )
		{
			for ( var c = 0; c < f.Count; c++ )
			{
				var a = f.Indices[c];
				var b = f.Indices[(c + 1) % f.Count];
				sets[a].Add( b );
				sets[b].Add( a );
			}
		}

		return sets.Select( s => s.ToList() ).ToArray();
	}

	/// <summary>
	/// Let the fitted garment settle under gravity against the bodies, using the kernel's one cloth
	/// solver. <see cref="Options.Stiffness"/> becomes XPBD stretch compliance — 1 is canvas that
	/// holds its fitted length, 0 is jersey that gives — and <see cref="Options.PinTop"/> pins the
	/// top slice of the mesh so a waistband stays where it was fitted.
	/// </summary>
	static void Drape( PolyMesh garment, IReadOnlyList<PolyMesh> bodies, Options o )
	{
		// The fabric first, then the garment's own settings over the top. Order matters: the preset
		// is what the cloth IS and Stiffness is the adjustment somebody made to it, so the
		// adjustment has to win - the other way round, moving the slider would do nothing on any
		// fabric that sets its own stretch.
		var sim = ClothSim.WithFabric( garment, PinnedTop( garment, o.PinTop ), o.Fabric );

		sim.Thickness = MathF.Max( o.Clearance, 1e-3f );
		sim.SelfCollision = o.SelfCollision;

		// Stiffness runs 0..1 and compliance runs rigid..slack over four decades, so the map is
		// exponential: 1 -> 1e-7 (canvas), 0.5 -> 1e-5, 0 -> 1e-3 (jersey).
		var stretch = MathF.Pow( 10f, -7f + 4f * (1f - Math.Clamp( o.Stiffness, 0f, 1f )) );

		// Stretch is scaled rather than replaced, so a stretch fabric stays stretchier than denim at
		// the same slider position instead of every fabric collapsing onto one curve.
		sim.StretchCompliance = MathF.Max( sim.StretchCompliance, 1e-7f ) / 1e-5f * stretch;
		sim.BendCompliance = MathF.Max( sim.BendCompliance, sim.StretchCompliance * 10f );
		// The garment was fitted to lie where it should; the drape only settles it. Two inches, or
		// four clearances on a loose fit, is room for a hem to fall and nowhere near room to drop
		// through a body with gaps in it. See ClothSim.Tether.
		sim.Tether = o.Tether > 0f ? o.Tether : MathF.Max( 2f, o.Clearance * 4f );

		if ( o.Capsules is { Count: > 0 } )
			sim.Capsules.AddRange( o.Capsules );
		else
			sim.SetColliders( bodies );
		sim.Run( o.DrapeSteps / 60f );

		for ( var i = 0; i < garment.VertexCount; i++ )
			garment.Positions[i] = sim.Positions[i];
	}

	/// <summary>Vertices in the top <paramref name="fraction"/> of the mesh's height. Empty when the
	/// fraction is 0, which pins nothing and lets the garment rest on the body by collision alone.</summary>
	static IEnumerable<int> PinnedTop( PolyMesh mesh, float fraction )
	{
		fraction = Math.Clamp( fraction, 0f, 1f );

		if ( fraction <= 0f || mesh.VertexCount == 0 )
			yield break;

		var top = mesh.Positions.Max( p => p.z );
		var bottom = mesh.Positions.Min( p => p.z );
		var cut = top - (top - bottom) * fraction;

		for ( var i = 0; i < mesh.VertexCount; i++ )
			if ( mesh.Positions[i].z >= cut )
				yield return i;
	}

	public static bool[] BoundaryVertices( PolyMesh mesh )
	{
		var boundary = new bool[mesh.VertexCount];

		foreach ( var (edge, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count == 1 )
			{
				boundary[edge.A] = true;
				boundary[edge.B] = true;
			}
		}

		return boundary;
	}

	/// <summary>1 at an opening, falling to 0 at <paramref name="reach"/> away from it.</summary>
	public static float[] FlareWeights( PolyMesh mesh, bool[] boundary, float reach )
	{
		var weights = new float[mesh.VertexCount];
		var edge = new List<Vec3>();

		for ( var i = 0; i < boundary.Length; i++ )
		{
			if ( boundary[i] )
				edge.Add( mesh.Positions[i] );
		}

		if ( edge.Count == 0 || reach <= 0f )
			return weights;

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			var best = float.MaxValue;

			foreach ( var e in edge )
				best = Math.Min( best, (e - p).LengthSquared );

			var d = MathF.Sqrt( best );
			var w = Math.Clamp( 1f - d / reach, 0f, 1f );
			weights[i] = w * w;
		}

		return weights;
	}

	/// <summary>
	/// UVs wrapped round each limb on its own bone, a foot to a UV unit everywhere.
	///
	/// ONE CYLINDER ROUND THE WHOLE BODY WAS WRONG FOR THE SLEEVES. CylinderUVs wraps everything
	/// about the body's vertical axis, which is right for the torso and stretches a sleeve into a
	/// smear: the arm is a tube lying across that projection, so its texture ran the wrong way and
	/// was squeezed to a sliver on the top and bottom of the arm. Trouser legs the same, less so.
	/// So each face is projected round the bone chain its region runs along - the torso's spine,
	/// each arm, each leg - with the angle scaled by that limb's own girth, so a check the size of
	/// a thumbnail on the chest is the size of a thumbnail on the cuff. The seam between a region
	/// and the next is a UV seam, as it is on every real garment.
	/// </summary>
	public static void LimbUVs( PolyMesh mesh, BodyRegions.Map map )
	{
		const float UnitsPerUv = 12f;

		// Every face's chain, and every chain's girth from the faces on it.
		var chains = new Dictionary<(BodyRegion, int), (Vec3 From, Vec3 Axis, Vec3 E1, Vec3 E2, float Radius, int Count)>();
		var faceChain = new (BodyRegion, int)[mesh.FaceCount];

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var centroid = mesh.FaceCentroid( mesh.Faces[fi] );
			var (region, _) = map.Locate( centroid );
			var side = BodyRegions.Map.IsCentral( region ) ? 0 : centroid.y > 0 ? 1 : -1;
			var key = (region, side);
			faceChain[fi] = key;

			if ( !chains.TryGetValue( key, out var chain ) )
			{
				Vec3 from, to;

				if ( region == BodyRegion.None || !map.Chain( region, side == 0 ? 1 : side, out from, out to ) )
				{
					// Nothing to hang it on: the old projection about the vertical.
					from = Vec3.Zero;
					to = new Vec3( 0f, 0f, -1f );
				}

				var axis = (to - from).Normal;
				var reference = MathF.Abs( axis.z ) < 0.9f ? new Vec3( 0f, 0f, 1f ) : new Vec3( 1f, 0f, 0f );
				var e1 = Vec3.Cross( reference, axis ).Normal;
				var e2 = Vec3.Cross( axis, e1 );
				chain = (from, axis, e1, e2, 0f, 0);
			}

			var q = centroid - chain.From;
			var radial = q - chain.Axis * Vec3.Dot( q, chain.Axis );
			chain.Radius += radial.Length;
			chain.Count++;
			chains[key] = chain;
		}

		foreach ( var fi in Enumerable.Range( 0, mesh.FaceCount ) )
		{
			var face = mesh.Faces[fi];
			var chain = chains[faceChain[fi]];
			var radius = MathF.Max( 1f, chain.Radius / Math.Max( 1, chain.Count ) );
			var span = MathF.PI * 2f * radius / UnitsPerUv;
			var uvs = new Vec2[face.Count];

			for ( var c = 0; c < face.Count; c++ )
			{
				var q = mesh.Positions[face.Indices[c]] - chain.From;
				var angle = MathF.Atan2( Vec3.Dot( q, chain.E2 ), Vec3.Dot( q, chain.E1 ) );
				uvs[c] = new Vec2( angle * radius / UnitsPerUv, Vec3.Dot( q, chain.Axis ) / UnitsPerUv );
			}

			// A face straddling the seam at +-180 degrees would stretch across the whole texture.
			var max = uvs.Max( u => u.x );

			for ( var c = 0; c < uvs.Length; c++ )
				if ( max - uvs[c].x > span * 0.5f )
					uvs[c] = new Vec2( uvs[c].x + span, uvs[c].y );

			face.UVs = uvs;
		}
	}

	/// <summary>
	/// A wrap-around projection about the vertical axis, a foot to a UV unit. Fabric tiles and fur
	/// noise both want something even; the body's own UVs are laid out for the body, and a CAD part
	/// may have none. LimbUVs is what Fit uses now; this is kept for a mesh with no rig to go by.
	/// </summary>
	public static void CylinderUVs( PolyMesh mesh )
	{
		const float UnitsPerUv = 12f;
		var centre = Vec3.Zero;

		foreach ( var p in mesh.Positions )
			centre += p;

		centre /= Math.Max( 1, mesh.VertexCount );

		foreach ( var face in mesh.Faces )
		{
			var uvs = new Vec2[face.Count];

			for ( var c = 0; c < face.Count; c++ )
			{
				var p = mesh.Positions[face.Indices[c]];
				var angle = MathF.Atan2( p.y - centre.y, p.x - centre.x );
				uvs[c] = new Vec2( angle / (MathF.PI * 2f) * (MathF.PI * 2f * 8f) / UnitsPerUv, -p.z / UnitsPerUv );
			}

			// A face straddling the seam at +-180 degrees would stretch across the whole texture.
			var span = MathF.PI * 2f * 8f / UnitsPerUv;
			var max = uvs.Max( u => u.x );

			for ( var c = 0; c < uvs.Length; c++ )
			{
				if ( max - uvs[c].x > span * 0.5f )
					uvs[c] = new Vec2( uvs[c].x + span, uvs[c].y );
			}

			face.UVs = uvs;
		}
	}
}

/// <summary>
/// What is wrong with a garment before you export it, said in the terms of the job.
///
/// WHY THIS IS NOT MeshValidator. That answers "is this a mesh" — corners, indices in range,
/// manifold edges — and a garment can pass every one of those checks and still be unwearable. The
/// questions that decide whether a piece of clothing is finished are different ones: does it clip
/// through the body when it is worn, can it be textured, will it move with the skeleton, is it
/// affordable. Structure is included because it matters too, but it is one line of five.
///
/// IT REPORTS, IT DOES NOT REFUSE. Every number here has a legitimate reason to be non-zero. A
/// garment SHOULD have openings — a T-shirt has four — and calling a neck hole an error would teach
/// people to fear the thing that makes it a shirt. Clipping is the one that is nearly always a
/// fault, and even that is a judgement about how deep and how much rather than a boolean. So this
/// hands back counts and depths for somebody to read, and says which of them usually mean trouble.
/// </summary>
public sealed class GarmentReport
{
	public string Name = "";

	public int Faces;
	public int Triangles;

	/// <summary>Vertices sitting INSIDE a body rather than on or outside it — the garment clipping
	/// through whoever is wearing it. The number that most often means real trouble.</summary>
	public int Clipping;

	/// <summary>How far the worst of them is in, in inches. A hundredth of an inch is the solver
	/// resting on the surface; a quarter of an inch is a shoulder through a sleeve.</summary>
	public float DeepestClip;

	/// <summary>Where they are, one point per clipping vertex, so a viewport can mark them.</summary>
	public List<Vec3> ClipPoints = new();

	/// <summary>How deep each of <see cref="ClipPoints"/> is, in inches, in the same order.</summary>
	public List<float> ClipDepths = new();

	/// <summary>Faces with no area — invisible, and a source of broken normals and bad lighting
	/// downstream. Always a fault.</summary>
	public int Degenerate;

	/// <summary>Boundary loops: the holes a garment is MEANT to have. A T-shirt has four — neck,
	/// hem, two cuffs. Reported so the number can be recognised, never as an error.</summary>
	public int Openings;

	public bool HasUVs;
	public bool HasWeights;

	/// <summary>Structural problems, straight from MeshValidator. These are faults.</summary>
	public List<string> Structure = new();

	/// <summary>Everything worth saying, in the order somebody reading it would want it — worst
	/// first, then the merely notable, then the reassuring.</summary>
	public IEnumerable<string> Lines()
	{
		if ( Clipping > 0 )
			yield return $"{Clipping} {(Clipping == 1 ? "vertex clips" : "vertices clip")} through the body, "
				+ $"deepest {DeepestClip:0.##} in. Raise Looseness or Clearance, or shrinkwrap it back out.";

		if ( Degenerate > 0 )
			yield return $"{Degenerate} face{(Degenerate == 1 ? " has" : "s have")} no area. "
				+ "Merge by distance will usually take them out.";

		foreach ( var problem in Structure )
			yield return problem;

		if ( !HasWeights )
			yield return "No skin weights - it will not move with the body. Copy weights from the body you cut it from.";

		if ( !HasUVs )
			yield return "No UVs - it cannot be textured yet.";

		yield return $"{Triangles} triangles, {Openings} opening{(Openings == 1 ? "" : "s")}"
			+ $"{(HasUVs ? ", UVs" : "")}{(HasWeights ? ", weighted" : "")}.";
	}

	/// <summary>Nothing here is a fault. Openings and triangle count are not faults.</summary>
	public bool Clean => Clipping == 0 && Degenerate == 0 && Structure.Count == 0 && HasUVs && HasWeights;

	public override string ToString() => string.Join( " ", Lines() );
}

/// <summary>Check a garment against the bodies wearing it. See <see cref="GarmentReport"/>.</summary>
public static class GarmentCheck
{
	/// <summary>How far inside a surface counts as clipping rather than as resting on it. The
	/// drape solver settles vertices ON the body by design, and a tolerance smaller than its own
	/// resting error would report every well-fitted garment as broken.</summary>
	public const float ClipTolerance = 0.01f;

	public static GarmentReport Run( PolyMesh garment, IReadOnlyList<PolyMesh> bodies )
	{
		var report = new GarmentReport();

		if ( garment is null )
			return report;

		report.Faces = garment.FaceCount;
		report.HasWeights = garment.IsRigged;

		foreach ( var face in garment.Faces )
		{
			report.Triangles += Math.Max( 0, face.Count - 2 );

			if ( face.UVs is { Length: > 0 } && face.UVs.Any( uv => uv.x != 0f || uv.y != 0f ) )
				report.HasUVs = true;

			if ( FaceArea( garment, face ) <= 1e-9f )
				report.Degenerate++;
		}

		var validation = MeshValidator.Validate( garment );
		report.Structure.AddRange( validation.Errors );

		report.Openings = BoundaryLoops( garment );

		// The same signed-distance test the fitter uses to push a garment out - see
		// GarmentFit.PushOut. Reusing the idiom matters: a check that disagreed with the solver
		// about which side of a surface a point is on would report faults the fitter cannot fix.
		foreach ( var body in bodies ?? Array.Empty<PolyMesh>() )
		{
			if ( body is null || body.FaceCount == 0 || ReferenceEquals( body, garment ) )
				continue;

			var tree = MeshBVH.Build( body );

			foreach ( var p in garment.Positions )
			{
				if ( tree.NearestSurface( body, p, 1e6f ) is not { } hit )
					continue;

				var depth = -Vec3.Dot( p - hit.Point, hit.Normal );

				if ( depth <= ClipTolerance )
					continue;

				report.Clipping++;
				report.DeepestClip = Math.Max( report.DeepestClip, depth );
				report.ClipPoints.Add( p );
				report.ClipDepths.Add( depth );
			}
		}

		return report;
	}

	static float FaceArea( PolyMesh mesh, Face face )
	{
		if ( face.Count < 3 )
			return 0f;

		var area = Vec3.Zero;
		var origin = mesh.Positions[face.Indices[0]];

		for ( var i = 1; i + 1 < face.Count; i++ )
		{
			var a = mesh.Positions[face.Indices[i]] - origin;
			var b = mesh.Positions[face.Indices[i + 1]] - origin;
			area += Vec3.Cross( a, b );
		}

		return area.Length * 0.5f;
	}

	/// <summary>
	/// How many separate holes the mesh has, by walking the boundary edges into loops.
	///
	/// LOOPS, NOT EDGES. MeshValidator already counts boundary edges, and that number is useless
	/// here: a T-shirt's four openings are a few hundred edges, which says nothing a person can
	/// check. Four is a number you can recognise as right.
	/// </summary>
	static int BoundaryLoops( PolyMesh mesh )
	{
		var next = new Dictionary<int, List<int>>();

		foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count != 1 )
				continue;

			if ( !next.TryGetValue( key.A, out var a ) )
				next[key.A] = a = new List<int>();

			if ( !next.TryGetValue( key.B, out var b ) )
				next[key.B] = b = new List<int>();

			a.Add( key.B );
			b.Add( key.A );
		}

		var seen = new HashSet<int>();
		var loops = 0;

		foreach ( var start in next.Keys )
		{
			if ( !seen.Add( start ) )
				continue;

			loops++;

			// Flood the connected run of boundary vertices. A figure-of-eight boundary counts once,
			// which is the honest answer - it is one hole pinched, not two.
			var stack = new Stack<int>();
			stack.Push( start );

			while ( stack.Count > 0 )
			{
				foreach ( var n in next[stack.Pop()] )
				{
					if ( seen.Add( n ) )
						stack.Push( n );
				}
			}
		}

		return loops;
	}
}

/// <summary>Where a trim goes, and what it is.</summary>
public enum TrimStyle
{
	/// <summary>A gathered strip: longer than the edge it hangs from, so it waves. Frills, ruffles,
	/// a gathered hem.</summary>
	Frill,

	/// <summary>A flat band following the edge. Ribbon, binding, a cuff band, a waistband.</summary>
	Ribbon,

	/// <summary>Folded rather than gathered - the wave is angular instead of round. Pleats, a
	/// box-pleated hem, an accordion cuff.</summary>
	Pleat,

	/// <summary>Cut into separate hanging strips. Fringe, tassels, a shredded hem.</summary>
	Fringe,
}

/// <summary>
/// Frills, ruffles, ribbons, pleats and fringe: the details that make a garment a piece of FASHION
/// rather than a fitted bag.
///
/// THEY HANG OFF THE OPENINGS, and that is the whole idea. A garment already has exactly the edges
/// trim belongs on - the hem, the collar, the cuffs - because they are its boundary loops, the holes
/// it is made to have. Nothing has to be selected, drawn or marked: the shape of the garment already
/// says where a frill could go, so asking for one is choosing which opening and how much.
///
/// LACE IS NOT HERE, DELIBERATELY. Lace is holes in cloth, and holes in cloth are an alpha-masked
/// material - that is how every shipped game does it, because modelling the mesh of a lace edging
/// costs thousands of triangles to render something a 512px texture renders better. Put lace on the
/// garment as a material with a cut-out; use a Frill for the shape the lace edging follows.
///
/// GATHERED, NOT SCALED. A real frill is a strip of cloth LONGER than the edge it is sewn to, forced
/// to fit by being bunched. That is why it waves, and why the waves get deeper further from the seam
/// while the seam itself stays smooth. So this displaces along the surface normal by an amount that
/// grows with the distance hung, rather than making a wavy ring and pulling it down - which looks
/// like a cake decoration and not like cloth.
/// </summary>
public static class GarmentTrim
{
	public sealed class Options
	{
		public TrimStyle Style = TrimStyle.Frill;

		/// <summary>How far the trim hangs from the edge, in inches.</summary>
		public float Width = 1.2f;

		/// <summary>How much longer than its edge the strip is, 0 flat to 1 heavily bunched. What
		/// makes a frill a frill.</summary>
		public float Gather = 0.5f;

		/// <summary>How many waves around the whole opening. More waves, finer gathers.</summary>
		public int Waves = 24;

		/// <summary>Rows of quads from the seam to the free edge. One is enough for a ribbon; a deep
		/// frill wants four or five so the wave has somewhere to develop.</summary>
		public int Rows = 3;

		/// <summary>For Fringe: what fraction of each strip's slot is cut away.</summary>
		public float Gap = 0.4f;

		public int MaterialSlot = 0;
	}

	public sealed class Result
	{
		public PolyMesh Mesh = new();
		public int Openings;
		public string Problem;
	}

	/// <summary>
	/// Build trim along some of a mesh's boundary loops.
	///
	/// <paramref name="which"/> picks loops by index into the order OrderedBoundaryLoops returns
	/// them in, which is by height, lowest first - so on a shirt 0 is the hem and the last is the
	/// collar. Null or empty means every opening.
	/// </summary>
	public static Result Build( PolyMesh garment, IReadOnlyList<int> which, Options o )
	{
		var result = new Result();

		if ( garment is null || garment.FaceCount == 0 )
		{
			result.Problem = "there is no garment to trim";
			return result;
		}

		var loops = OrderedBoundaryLoops( garment );

		if ( loops.Count == 0 )
		{
			result.Problem = "this garment has no openings - trim hangs from an edge, and a closed "
				+ "shape has none";
			return result;
		}

		var normals = garment.ComputeVertexNormals();
		var rows = Math.Clamp( o.Rows, 1, 12 );

		for ( var li = 0; li < loops.Count; li++ )
		{
			if ( which is { Count: > 0 } && !which.Contains( li ) )
				continue;

			if ( loops[li].Count < 3 )
				continue;

			AddStrip( result.Mesh, garment, loops[li], normals, rows, o );
			result.Openings++;
		}

		if ( result.Openings == 0 )
			result.Problem = $"none of this garment's {loops.Count} openings were chosen";

		return result;
	}

	static void AddStrip( PolyMesh into, PolyMesh garment, List<int> ring, Vec3[] normals, int rows,
		Options o )
	{
		var n = ring.Count;

		// Arc length around the ring, so the wave count means the same thing whether the ring is a
		// cuff or a hem and whether its vertices are evenly spaced or not. Spacing waves by INDEX
		// instead puts every wave of a hem into the densest quarter of it.
		var arc = new float[n + 1];

		for ( var i = 0; i < n; i++ )
		{
			var a = garment.Positions[ring[i]];
			var b = garment.Positions[ring[(i + 1) % n]];
			arc[i + 1] = arc[i] + (b - a).Length;
		}

		var total = arc[n];

		if ( total <= 1e-5f )
			return;

		var centre = Vec3.Zero;

		foreach ( var v in ring )
			centre += garment.Positions[v];

		centre /= n;

		// Which way the trim hangs: the direction the cloth would carry on in if the garment did not
		// stop at its own hem. Taken from the edge rather than as "straight down", so a cuff's frill
		// runs on down the arm and a collar's stands off the neck.
		var away = new Vec3[n];

		for ( var i = 0; i < n; i++ )
		{
			var p = garment.Positions[ring[i]];
			var prev = garment.Positions[ring[(i - 1 + n) % n]];
			var next = garment.Positions[ring[(i + 1) % n]];

			var tangent = (next - prev).Normal;
			var outward = Vec3.Cross( tangent, normals[ring[i]] );

			if ( outward.LengthSquared < 1e-8f )
				outward = p - centre;

			outward = outward.Normal;

			// The cross product's sign flips with the ring's winding, which is not something a
			// caller should have to know about. Point it away from the ring's own centre.
			if ( Vec3.Dot( outward, p - centre ) < 0f )
				outward = -outward;

			away[i] = outward;
		}

		var waves = Math.Max( 1, o.Waves );
		var gather = Math.Clamp( o.Gather, 0f, 1f );
		var width = Math.Max( 0f, o.Width );

		// Row 0 is the seam: it sits exactly on the garment's own edge, unmoved, so the trim is
		// attached rather than floating near it.
		var grid = new int[rows + 1][];

		for ( var r = 0; r <= rows; r++ )
		{
			grid[r] = new int[n];

			var t = r / (float)rows;

			for ( var i = 0; i < n; i++ )
			{
				var p = garment.Positions[ring[i]];
				var s = arc[i] / total;

				var swing = o.Style switch
				{
					TrimStyle.Frill => MathF.Sin( s * MathF.Tau * waves ),
					TrimStyle.Pleat => Triangle( s * waves ),
					_ => 0f,
				};

				// The gather grows with distance from the seam, which is what keeps the seam itself
				// from rippling. A ribbon has no gather at all and stays a flat band.
				var offset = away[i] * (width * t)
					+ normals[ring[i]] * (swing * gather * width * 0.6f * t);

				grid[r][i] = into.Positions.Count;
				into.Positions.Add( p + offset );
			}
		}

		var fringe = o.Style == TrimStyle.Fringe;
		var keep = Math.Clamp( 1f - o.Gap, 0.05f, 1f );

		for ( var r = 0; r < rows; r++ )
		{
			for ( var i = 0; i < n; i++ )
			{
				var j = (i + 1) % n;

				// Fringe is the same strip with most of it cut away: whole slots are dropped rather
				// than narrowed, so what is left hangs as separate tassels and not as a comb.
				if ( fringe && r > 0 )
				{
					var phase = arc[i] / total * waves;

					if ( phase - MathF.Floor( phase ) > keep )
						continue;
				}

				into.Faces.Add( new Face( new[] { grid[r][i], grid[r][j], grid[r + 1][j], grid[r + 1][i] } )
				{
					Material = o.MaterialSlot,
				} );
			}
		}
	}

	/// <summary>A 0..1 sawtooth as a -1..1 triangle wave: a pleat's fold, angular where a frill's
	/// is round.</summary>
	static float Triangle( float x )
	{
		var f = x - MathF.Floor( x );
		return f < 0.5f ? f * 4f - 1f : 3f - f * 4f;
	}

	/// <summary>
	/// Every boundary loop as an ORDERED ring of vertices, lowest first.
	///
	/// ORDERED IS THE WHOLE JOB. GarmentCheck only has to COUNT openings, so it floods them; trim
	/// has to walk one in sequence to lay quads along it. Height ordering is what makes "opening 0"
	/// mean the hem on every garment rather than whichever vertex happened to be found first.
	/// </summary>
	public static List<List<int>> OrderedBoundaryLoops( PolyMesh mesh )
	{
		var neighbours = new Dictionary<int, List<int>>();

		foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count != 1 )
				continue;

			if ( !neighbours.TryGetValue( key.A, out var a ) )
				neighbours[key.A] = a = new List<int>();

			if ( !neighbours.TryGetValue( key.B, out var b ) )
				neighbours[key.B] = b = new List<int>();

			a.Add( key.B );
			b.Add( key.A );
		}

		var loops = new List<List<int>>();
		var used = new HashSet<int>();

		foreach ( var start in neighbours.Keys )
		{
			if ( used.Contains( start ) )
				continue;

			var loop = new List<int>();
			var current = start;
			var previous = -1;

			// Walk the ring. A boundary vertex normally has exactly two boundary neighbours; where
			// it has more the loop is pinched, and taking the first unused one keeps the walk going
			// rather than abandoning the opening.
			while ( current >= 0 && used.Add( current ) )
			{
				loop.Add( current );

				var next = -1;

				foreach ( var candidate in neighbours[current] )
				{
					if ( candidate == previous || used.Contains( candidate ) )
						continue;

					next = candidate;
					break;
				}

				previous = current;
				current = next;
			}

			if ( loop.Count >= 3 )
				loops.Add( loop );
		}

		loops.Sort( ( a, b ) => Height( mesh, a ).CompareTo( Height( mesh, b ) ) );

		return loops;
	}

	static float Height( PolyMesh mesh, List<int> loop )
	{
		var z = 0f;

		foreach ( var v in loop )
			z += mesh.Positions[v].z;

		return z / Math.Max( 1, loop.Count );
	}
}

/// <summary>
/// Turn a garment into an s&amp;box `.clothing` definition: the record that makes a mesh into a
/// wearable item rather than a prop.
///
/// THE STEP THIS TOOL STOPPED ONE SHORT OF. Effigy could already compile a garment to a skinned
/// `.vmdl`, and a `.vmdl` is not clothing. s&amp;box dresses a citizen from a Clothing resource,
/// which names the model and then answers three questions the mesh cannot: what KIND of garment it
/// is, which body slots it OCCUPIES so two hats do not both get worn, and which parts of the body it
/// HIDES so the chest does not poke through the shirt. Without it a finished garment cannot be put
/// on a character at all, and the modelling was the easy part of making clothes.
///
/// THE SLOTS COME FROM THE RECIPE, not from the garment's name. The recipe already says exactly
/// which regions of the body it covers - that is what it IS - so a long sleeve claims the wrists and
/// a T-shirt does not, and adjusting Sleeve moves the answer with it. Deriving them from the name
/// would be a second, parallel description of the same garment, and the two would drift apart the
/// first time somebody dragged a slider.
///
/// WRITTEN AS TEXT, IN THE KERNEL. A Clothing resource is JSON, so it needs no engine to produce and
/// can be tested headlessly - which matters more here than usual, because the failure mode of a
/// wrong slot name is an asset that compiles, loads, and silently does not work.
/// </summary>
public static class ClothingDefinition
{
	/// <summary>What a garment covers, in s&amp;box's own slot vocabulary. Anything not listed maps
	/// to nothing, which is the right answer for Neck.</summary>
	public static IEnumerable<string> SlotsFor( GarmentRecipe recipe )
	{
		var slots = new List<string>();

		void Add( string slot )
		{
			if ( !slots.Contains( slot ) )
				slots.Add( slot );
		}

		// A span tagged for one side claims only that side's slots. That is the whole reason the
		// side is on the span: a one-sleeved top that claimed both arms would stop you wearing
		// anything on the bare one.
		void Sided( int side, string left, string right )
		{
			if ( side >= 0 )
				Add( left );

			if ( side <= 0 )
				Add( right );
		}

		foreach ( var (region, from, to, side) in recipe.Spans )
		{
			switch ( region )
			{
				case BodyRegion.Torso:
					Add( "Chest" );
					break;

				case BodyRegion.Hips:
					Add( "Groin" );
					Add( "Waist" );
					break;

				case BodyRegion.UpperArm:
					Sided( side, "LeftArm", "RightArm" );
					break;

				case BodyRegion.LowerArm:
					Sided( side, "LeftArm", "RightArm" );

					// Only a sleeve that actually reaches the wrist claims it. A three-quarter
					// sleeve stopping at the forearm must leave the wrist free for a watch.
					if ( to > 0.9f )
						Sided( side, "LeftWrist", "RightWrist" );
					break;

				case BodyRegion.Hand:
					Sided( side, "LeftHand", "RightHand" );
					break;

				case BodyRegion.UpperLeg:
					Sided( side, "LeftThigh", "RightThigh" );

					if ( to > 0.9f )
						Sided( side, "LeftKnee", "RightKnee" );
					break;

				case BodyRegion.LowerLeg:
					Sided( side, "LeftKnee", "RightKnee" );
					Sided( side, "LeftShin", "RightShin" );
					break;

				case BodyRegion.Foot:
					Sided( side, "LeftFoot", "RightFoot" );
					break;

				case BodyRegion.Head:
					Add( "HeadTop" );

					// A hat that comes down past the ears covers the lower head too - the
					// difference between a cap and a balaclava, and what stops both being wearable
					// at once.
					if ( from < 0.35f )
						Add( "HeadBottom" );
					break;
			}
		}

		return slots;
	}

	/// <summary>
	/// Which parts of the BODY to stop drawing underneath, in s&amp;box's BodyGroups vocabulary.
	///
	/// NOT THE SAME QUESTION AS THE SLOTS, and conflating them is how clothing ends up with skin
	/// poking through it. Slots are about what else can be worn; this is about the body being drawn
	/// inside the cloth. It is deliberately conservative - only whole groups the garment substantially
	/// covers - because hiding a limb a garment only half covers leaves a character with no forearm.
	/// </summary>
	public static IEnumerable<string> HideBodyFor( GarmentRecipe recipe )
	{
		var hide = new List<string>();

		void Add( string group )
		{
			if ( !hide.Contains( group ) )
				hide.Add( group );
		}

		foreach ( var (region, from, to, _) in recipe.Spans )
		{
			switch ( region )
			{
				case BodyRegion.Torso when to > 0.9f:
					Add( "Chest" );
					break;

				// Legs come off only for something that reaches the ankle. Shorts hide nothing:
				// the leg below them is the leg you are meant to see.
				case BodyRegion.LowerLeg when to > 0.9f:
					Add( "Legs" );
					break;

				case BodyRegion.Hand:
					Add( "Hands" );
					break;

				case BodyRegion.Foot:
					Add( "Feet" );
					break;
			}
		}

		return hide;
	}

	/// <summary>The s&amp;box ClothingCategory a recipe belongs in. Named from the enum this engine
	/// actually has — see the effigy_clothing_probe console command.</summary>
	public static string CategoryFor( GarmentRecipe recipe ) => recipe?.Name switch
	{
		"T-shirt" => "TShirt",
		"Long sleeve" => "Shirt",
		"Tank top" => "Vest",
		"Trousers" => "Trousers",
		"Shorts" => "Shorts",
		"Beanie" => "HatBeanie",
		"Gloves" => "Gloves",
		"Socks" => "Socks",
		"Bodysuit" => "Fullbody",
		_ => "Tops",
	};

	/// <summary>
	/// The `.clothing` file's text.
	///
	/// Only the fields that mean something for a garment made here are written. The rest - human
	/// skin overrides, alternate male and female models, tint gradients, a Steam item id - are
	/// s&amp;box's own defaults, and writing guesses at them would be claiming a garment supports
	/// things it has never been tested with.
	/// </summary>
	/// <param name="modelPath">The compiled model, as an asset path: models/effigy/shirt.vmdl.</param>
	public static string Write( GarmentRecipe recipe, string modelPath, string title,
		string subtitle = null )
	{
		var slots = string.Join( ", ", SlotsFor( recipe ) );
		var hide = string.Join( ", ", HideBodyFor( recipe ) );

		var sb = new StringBuilder();

		sb.Append( "{\n" );
		sb.Append( $"  \"Title\": {Json( title )},\n" );
		sb.Append( $"  \"Subtitle\": {Json( subtitle ?? "" )},\n" );
		sb.Append( $"  \"Category\": {Json( CategoryFor( recipe ) )},\n" );
		sb.Append( $"  \"Model\": {Json( modelPath )},\n" );

		// An empty flags enum serialises as 0, not as "". s&box writes it that way itself - see any
		// shipped .clothing with no SlotsOver - and a "" here is read as an unknown flag name.
		sb.Append( $"  \"SlotsUnder\": {(slots.Length > 0 ? Json( slots ) : "0")},\n" );
		sb.Append( "  \"SlotsOver\": 0,\n" );
		sb.Append( $"  \"HideBody\": {(hide.Length > 0 ? Json( hide ) : "0")},\n" );
		sb.Append( "  \"__references\": [],\n" );
		sb.Append( "  \"__version\": 0\n" );
		sb.Append( "}\n" );

		return sb.ToString();
	}

	/// <summary>
	/// Several garments as the one recipe their shared model wears.
	///
	/// ONE MODEL, ONE DEFINITION. Compile .vmdl bakes every garment in the document into a single
	/// model, so a shirt and trousers made together are one item and must claim both sets of slots -
	/// a .clothing that only named the shirt's would let a second pair of trousers go on over the
	/// first. The category is the first garment's: s&amp;box has exactly one per item, and the first
	/// thing added is the best guess at what the item is.
	/// </summary>
	public static GarmentRecipe Combine( IEnumerable<GarmentRecipe> recipes )
	{
		GarmentRecipe merged = null;

		foreach ( var recipe in recipes )
		{
			if ( recipe is null )
				continue;

			merged ??= new GarmentRecipe { Name = recipe.Name };
			merged.Spans.AddRange( recipe.Spans );
		}

		return merged;
	}

	/// <summary>A JSON string literal. Small by design - this writes four known fields, and pulling
	/// in a serialiser for them would put an engine dependency in the kernel.</summary>
	static string Json( string value )
	{
		if ( value is null )
			return "null";

		var sb = new StringBuilder( value.Length + 2 );
		sb.Append( '"' );

		foreach ( var c in value )
		{
			switch ( c )
			{
				case '"': sb.Append( "\\\"" ); break;
				case '\\': sb.Append( "\\\\" ); break;
				case '\n': sb.Append( "\\n" ); break;
				case '\r': sb.Append( "\\r" ); break;
				case '\t': sb.Append( "\\t" ); break;
				default:
					if ( c < 0x20 )
						sb.Append( "\\u" ).Append( ((int)c).ToString( "x4" ) );
					else
						sb.Append( c );
					break;
			}
		}

		sb.Append( '"' );
		return sb.ToString();
	}
}

/// <summary>
/// A smooth stand-in body to cut a garment from, for a wearer that is not a skin.
///
/// WHY A GARMENT NEEDS ONE. GarmentFit lifts the cloth straight off the wearer's own surface,
/// which is exactly right for a skin like Citizen's and exactly wrong for a body built from
/// parts. Camhead is struts, plates and cables with air between them; lifted off that, a
/// T-shirt came out as a tight shell over every bolt, and the drape had nothing to hang across
/// the gaps. A shirt on a robot is a shirt on the robot's SILHOUETTE.
///
/// ONE CONVEX HULL PER REGION AND SIDE - the torso, the hips, each upper arm, each forearm - out
/// of the vertices nearest that region's bones. Convex, so it spans every gap; per region, so
/// the armpit and the crotch stay open instead of the whole body becoming one blob. The hulls
/// overlap at the joints, and Fit already drops faces buried inside another closed body, which
/// is what turns overlapping hulls into one clean outer surface.
///
/// SUBDIVIDED LINEARLY, not smoothed. Catmull-Clark would round the hull inward and let the
/// corners of the real parts poke through the cloth; midpoint splits keep the shape and only add
/// the vertices the drape needs to have something to bend.
/// </summary>
public static class GarmentEnvelope
{
	/// <summary>How many separate pieces a mesh is made of. A skin is one or a handful (a head, a
	/// body, two eyes); a kitbashed robot is hundreds.</summary>
	public static int ShellCount( PolyMesh mesh )
	{
		var parent = new int[mesh.VertexCount];

		for ( var i = 0; i < parent.Length; i++ )
			parent[i] = i;

		int Find( int x )
		{
			while ( parent[x] != x )
				x = parent[x] = parent[parent[x]];

			return x;
		}

		foreach ( var face in mesh.Faces )
			for ( var c = 1; c < face.Count; c++ )
				parent[Find( face.Indices[c] )] = Find( face.Indices[0] );

		var roots = new HashSet<int>();

		foreach ( var face in mesh.Faces )
			if ( face.Count > 0 )
				roots.Add( Find( face.Indices[0] ) );

		return roots.Count;
	}

	/// <summary>Should a garment be cut from an envelope instead of the surface? More than this
	/// many pieces is a body built from parts. Citizen is under ten.</summary>
	public const int PartsThreshold = 24;

	public static bool IsKitbashed( IEnumerable<PolyMesh> bodies ) =>
		bodies.Sum( ShellCount ) > PartsThreshold;

	/// <summary>The stand-in: one closed, subdivided hull per body region and side.</summary>
	public static List<(BodyRegion Region, PolyMesh Mesh)> Build( IReadOnlyList<PolyMesh> bodies, BodyRegions.Map map, float edge = 1.5f )
	{
		var groups = new Dictionary<(BodyRegion, int), List<Vec3>>();

		foreach ( var body in bodies )
		{
			foreach ( var p in body.Positions )
			{
				var (region, _) = map.Locate( p );

				if ( region == BodyRegion.None )
					continue;

				var central = region is BodyRegion.Head or BodyRegion.Neck or BodyRegion.Torso or BodyRegion.Hips;
				var key = (region, central ? 0 : p.y > 0 ? 1 : -1);

				if ( !groups.TryGetValue( key, out var list ) )
					groups[key] = list = new List<Vec3>();

				list.Add( p );
			}
		}

		// THE TORSO HANGS. Cloth falls from the widest thing it rests on - shoulders, chest - and
		// does not follow the body back in at the waist, which is the whole difference between a
		// shirt and a wetsuit. So the torso's hull is dropped straight down to the bottom of the
		// hips: a column as wide as the chest, and the drape has something to hang from rather
		// than a waist to crumple into.
		if ( groups.TryGetValue( (BodyRegion.Torso, 0), out var torso ) && groups.TryGetValue( (BodyRegion.Hips, 0), out var hips ) )
		{
			var floor = hips.Min( p => p.z );

			// Only the chest between the arms. Dropped from the shoulder points too, the column is
			// as wide as the shoulders all the way down, swallows an A-posed upper arm, and the
			// sleeves - the faces of the arm hulls inside it - are cut away with it.
			var inner = groups.Where( g => g.Key.Item1 == BodyRegion.UpperArm )
				.SelectMany( g => g.Value ).Select( p => MathF.Abs( p.y ) ).DefaultIfEmpty( float.MaxValue ).Min();
			var dropped = torso.Where( p => MathF.Abs( p.y ) < inner )
				.Select( p => new Vec3( p.x, p.y, floor ) ).ToList();

			torso.AddRange( dropped );
		}

		var hulls = new List<(BodyRegion, PolyMesh)>();

		foreach ( var ((groupRegion, _), points) in groups )
		{
			if ( points.Count < 4 )
				continue;

			PolyMesh hull;

			try
			{
				hull = ConvexHull.ToMesh( Extremes( points ), 1e-4f );
			}
			catch ( Exception )
			{
				// Flat or degenerate - a single plate on its own. Nothing to wrap.
				continue;
			}

			if ( hull is null || hull.FaceCount == 0 )
				continue;

			hulls.Add( (groupRegion, Refine( hull, edge )) );
		}

		return hulls;
	}

	/// <summary>
	/// The points furthest along a few hundred directions spread evenly over the sphere.
	///
	/// ConvexHull is incremental and sized for CAD bodies of a few hundred vertices; a robot's
	/// torso is thousands, nearly all of them inside, and hulling them all took minutes. The
	/// extremes are the hull's own corners to within a fraction of an inch, which is far closer
	/// than cloth sitting a clearance off it can tell apart.
	/// </summary>
	static List<Vec3> Extremes( List<Vec3> points, int directions = 320 )
	{
		if ( points.Count <= directions )
			return points;

		var keep = new HashSet<int>();
		var golden = MathF.PI * (3f - MathF.Sqrt( 5f ));

		for ( var d = 0; d < directions; d++ )
		{
			var z = 1f - 2f * (d + 0.5f) / directions;
			var r = MathF.Sqrt( MathF.Max( 0f, 1f - z * z ) );
			var dir = new Vec3( r * MathF.Cos( golden * d ), r * MathF.Sin( golden * d ), z );

			var best = 0;
			var bestDot = float.MinValue;

			for ( var i = 0; i < points.Count; i++ )
			{
				var dot = Vec3.Dot( points[i], dir );

				if ( dot > bestDot )
				{
					bestDot = dot;
					best = i;
				}
			}

			keep.Add( best );
		}

		return keep.Select( i => points[i] ).ToList();
	}

	/// <summary>Triangulate, then split every triangle four ways until the longest edge is under
	/// <paramref name="edge"/>. Uniform on purpose: every triangle splits every round, so shared
	/// edges always split together and the hull stays closed - a split that followed each edge's
	/// own length would leave T-junctions wherever a long face met a short one.</summary>
	public static PolyMesh Refine( PolyMesh mesh, float edge, int maxTriangles = 12000 )
	{
		var positions = new List<Vec3>( mesh.Positions );
		var tris = new List<(int A, int B, int C)>();

		foreach ( var face in mesh.Faces )
			for ( var c = 1; c + 1 < face.Count; c++ )
				tris.Add( (face.Indices[0], face.Indices[c], face.Indices[c + 1]) );

		var limit = MathF.Max( edge, 0.05f );

		float Longest() => tris.Count == 0 ? 0f : tris.Max( t => MathF.Max( (positions[t.A] - positions[t.B]).Length,
			MathF.Max( (positions[t.B] - positions[t.C]).Length, (positions[t.C] - positions[t.A]).Length ) ) );

		while ( Longest() > limit && tris.Count * 4 <= maxTriangles )
		{
			var mids = new Dictionary<EdgeKey, int>();
			var next = new List<(int, int, int)>( tris.Count * 4 );

			int Mid( int a, int b )
			{
				var key = new EdgeKey( a, b );

				if ( !mids.TryGetValue( key, out var m ) )
				{
					m = positions.Count;
					positions.Add( (positions[a] + positions[b]) * 0.5f );
					mids[key] = m;
				}

				return m;
			}

			foreach ( var (a, b, c) in tris )
			{
				var ab = Mid( a, b );
				var bc = Mid( b, c );
				var ca = Mid( c, a );

				next.Add( (a, ab, ca) );
				next.Add( (ab, b, bc) );
				next.Add( (ca, bc, c) );
				next.Add( (ab, bc, ca) );
			}

			tris = next;
		}

		var result = new PolyMesh();

		foreach ( var p in positions )
			result.AddVertex( p );

		foreach ( var (a, b, c) in tris )
			result.AddFace( new[] { a, b, c }, null, 0 );

		return result;
	}
}
