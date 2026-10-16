using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Padding in a garment: a quilted shoulder yoke, an elbow patch, a shoulder pad, a stiff collar
/// band, a rib-knit waistband.
///
/// A REGION OF THE GARMENT, NAMED FROM THE BONES. "The right shoulder yoke" is found the way the
/// garment itself is cut - from where the rig puts the neck, the shoulder and the elbow - so the
/// pad follows the garment when its length or fit changes, and goes on the Citizen and Camhead in
/// the same place. Feather fades it out at its edge so it tapers instead of stepping.
///
/// INSIDE OR OUTSIDE. Batting between the shell and the lining (Inside) puffs the outside of the
/// garment and leaves the side against the body where it was: the shell moves out, the lining
/// stays. A patch sewn on top (Outside) is the same thickness added on the outside, with its edge
/// stitched down crisp - feather is capped at a fifth of an inch. Either way only the outer wall
/// moves, so the garment never gets tighter on the body.
///
/// QUILTING pinches the padding down along stitch lines - diamonds or channels - so it reads as
/// quilted rather than as a lump. The lines are hinges in the live cloth too.
///
/// FOR LIVE CLOTH, THE DATA IS KEPT. Each rebuild records, per garment, how much each vertex of
/// its outer sheet was padded (<see cref="Fields"/>), and <see cref="PaddingParticles.For"/> turns
/// every Padding on a garment into per-particle thickness, bend stiffness and mass - indexed the way
/// Make live cloth numbers its particles. Writing those into the .cloth.json and stepping them in
/// GarmentCloth is the live-cloth runtime's job; until it reads them, a padded garment is thicker
/// in the model and moves in the game like the unpadded cloth.
/// </summary>
public sealed class PaddingFeature : Feature
{
	public override string TypeName => "Padding";

	public override GeometryKind Accepts => GeometryKind.Body;

	public static readonly string[] RegionNames =
	{
		"Shoulder yoke", "Shoulder pad", "Elbow", "Collar band", "Cuffs", "Hem band", "Chest", "Back", "Whole garment",

		// Appended, not in body order: Where is saved as an index, and documents made before it
		// must keep the region they had.
		"Knee",
	};

	/// <summary>Which part of the garment is padded.</summary>
	public readonly ChoiceParam Where = new( "Where", RegionNames );

	/// <summary>Which side of the body. Left is the wearer's left (+y), the way Source measures it.</summary>
	public readonly ChoiceParam Side = new( "Side", GarmentRecipe.SideNames );

	/// <summary>Garments to pad. Empty = the garment made last before this.</summary>
	public readonly BodySelectionParam Bodies = new( "On" );

	/// <summary>Inside: batting between shell and lining. Outside: a patch sewn on top.</summary>
	public readonly ChoiceParam Placement = new( "Padding goes", new[] { "Inside", "Outside" } );

	/// <summary>How much thicker the garment gets at the middle of the pad, in inches. A quilted
	/// yoke is 0.15-0.2, a leather patch 0.05, a tailored shoulder pad 0.4, armour 0.6.</summary>
	public readonly FloatParam Thickness = new( "Thickness", 0.2f, 0f, 2f, "in" );

	/// <summary>How far in from its edge the pad reaches full thickness.</summary>
	public readonly FloatParam Feather = new( "Feather", 0.75f, 0f, 4f, "in" );

	/// <summary>Grow or shrink the region, 1 = a size M jacket's.</summary>
	public readonly FloatParam Size = new( "Size", 1f, 0.3f, 3f );

	/// <summary>Spacing of the quilt stitching, in inches. 0 = not quilted.</summary>
	public readonly FloatParam Quilt = new( "Quilt lines", 0f, 0f, 6f, "in" );

	public readonly ChoiceParam QuiltPattern = new( "Quilt pattern", new[] { "Diamonds", "Channels" } );

	/// <summary>How much harder the padded cloth is to fold, at full thickness. Quilted batting is
	/// three to ten times stiffer than the shell alone.</summary>
	public readonly FloatParam Stiffer = new( "Stiffer", 4f, 1f, 10f, "x" );

	/// <summary>How much heavier the padded cloth is, at full thickness. Kept to 4x or under: much
	/// more and the light cloth beside it cannot hold it up.</summary>
	public readonly FloatParam Heavier = new( "Heavier", 2f, 1f, 4f, "x" );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
	{
		Where, Side, Bodies, Placement, Thickness, Feather, Quilt, QuiltPattern, Size, Stiffer, Heavier,
	};

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Size, Stiffer, Heavier };

	/// <summary>The bodies the last rebuild padded, by id. Make live cloth reads it to know which
	/// garment's particles this padding is in.</summary>
	public HashSet<string> PaddedBodies { get; } = new();

	/// <summary>Vertices the last rebuild moved, over every garment.</summary>
	public int PaddedVertices { get; private set; }

	/// <summary>What the last rebuild did to each garment it padded, by body id. See PaddingField.</summary>
	public IReadOnlyDictionary<string, PaddingField> Fields => _fields;

	readonly Dictionary<string, PaddingField> _fields = new();

	/// <summary>Over this share of a garment, padding counts towards the room a garment worn over
	/// it leaves (Body.LayerAllowance). A smaller pad is a local bump, and the fit's collision
	/// against the garment's outside walks the jacket over it there and nowhere else.</summary>
	public const float AllowanceCoverage = 0.5f;

	float FeatherValue => Placement.Index == 1 ? MathF.Min( Feather.Clamped, 0.2f ) : Feather.Clamped;

	protected override void Execute( FeatureContext ctx )
	{
		PaddedBodies.Clear();
		PaddedVertices = 0;
		_fields.Clear();

		if ( ctx.Rig is null || ctx.Rig.Count == 0 )
		{
			Fail(
				"Padding needs a rig",
				"The shoulder, elbow and collar are found from the wearer's bones, and there are none.",
				"Load a Wearer in the Clothing workspace" );
		}

		var map = new BodyRegions.Map( ctx.Rig );

		var targets = Bodies.BodyIds.Count == 0
			? ctx.Bodies.Where( b => b.IsGarment && b.Layer >= 0 ).TakeLast( 1 ).ToList()
			: ctx.Bodies.Where( b => Bodies.Matches( b ) ).ToList();

		if ( targets.Count == 0 )
		{
			Fail(
				"Nothing to pad",
				"Padding goes in a garment, and there is none before it in the tree.",
				"Add a Garment first, then Padding" );
		}

		foreach ( var body in targets )
		{
			var mesh = body.Mesh;
			var sheet = MeshSolidify.OuterSheet( mesh, out var offset ) ?? mesh;

			if ( sheet.VertexCount == 0 )
				continue;

			if ( sheet == mesh )
				offset = 0;

			var pad = Field( sheet, map, out var weight );
			// Smoothed: a vertex normal at a cut edge or in a drape crease points any which way, and
			// a collar band pushed along those came out as a ragged rim.
			var normals = SmoothNormals( mesh, mesh.ComputeVertexNormals(), 3 );
			var moved = 0;
			var deepest = 0f;

			for ( var i = 0; i < pad.Length; i++ )
			{
				if ( pad[i] <= 1e-5f )
					continue;

				var vi = i + offset;
				mesh.Positions[vi] += normals[vi] * pad[i];
				moved++;
				deepest = MathF.Max( deepest, pad[i] );
			}

			if ( moved > 0 )
			{
				PaddedBodies.Add( body.Id );
				PaddedVertices += moved;
				_fields[body.Id] = new PaddingField( pad, weight, Stiffer.Clamped, Heavier.Clamped );

				// A garment padded all over - a puffer, a gambeson - is thicker everywhere a jacket
				// goes over it, so the jacket is fitted further out. See AllowanceCoverage.
				if ( moved >= AllowanceCoverage * pad.Length )
					body.LayerAllowance += deepest;
			}
		}

		if ( PaddedVertices == 0 )
		{
			Warn(
				$"The {RegionNames[Where.Index].ToLowerInvariant()} is not on this garment",
				"No part of the garment lies in that region - a sleeveless top has no elbow, a cropped one may have no hem band where it was looked for.",
				"Pick another Where or Side",
				"Or raise Size" );
		}
	}

	static Vec3[] SmoothNormals( PolyMesh mesh, Vec3[] normals, int passes )
	{
		var neighbours = new List<int>[mesh.VertexCount];

		for ( var i = 0; i < neighbours.Length; i++ )
			neighbours[i] = new List<int>();

		foreach ( var f in mesh.Faces )
			for ( var c = 0; c < f.Count; c++ )
			{
				var a = f.Indices[c];
				var b = f.Indices[(c + 1) % f.Count];
				neighbours[a].Add( b );
				neighbours[b].Add( a );
			}

		for ( var pass = 0; pass < passes; pass++ )
		{
			var next = new Vec3[normals.Length];

			for ( var i = 0; i < normals.Length; i++ )
			{
				var sum = normals[i];

				foreach ( var j in neighbours[i] )
					sum += normals[j];

				next[i] = sum.Normal;
			}

			normals = next;
		}

		return normals;
	}

	/// <summary>
	/// How much thicker each vertex of a garment sheet is from this padding, in inches - the
	/// region's mask, feathered, times the quilting's puff, times Thickness. <paramref name="weight"/>
	/// is the same without Thickness (0..1), for stiffness and mass.
	/// </summary>
	public float[] Field( PolyMesh sheet, BodyRegions.Map map, out float[] weight )
	{
		var n = sheet.VertexCount;
		var pad = new float[n];
		weight = new float[n];

		var mask = Mask( sheet, map );
		var normals = sheet.ComputeVertexNormals();
		var thickness = Thickness.Clamped;

		for ( var i = 0; i < n; i++ )
		{
			if ( mask[i] <= 0f )
				continue;

			var w = mask[i] * QuiltPuff( sheet.Positions[i], normals[i] );
			weight[i] = w;
			pad[i] = thickness * w;
		}

		return pad;
	}

	/// <summary>Stitching pinches the batting to about a third of its loft at the line and leaves
	/// it full between lines. Projected the way a box map is: onto the plane the surface faces most.</summary>
	float QuiltPuff( Vec3 p, Vec3 n )
	{
		var spacing = Quilt.Clamped;

		if ( spacing < 0.05f )
			return 1f;

		var ax = MathF.Abs( n.x );
		var ay = MathF.Abs( n.y );
		var az = MathF.Abs( n.z );

		var (u, v) = az >= ax && az >= ay ? (p.x, p.y) : ax >= ay ? (p.y, p.z) : (p.x, p.z);

		static float Crown( float t ) => MathF.Sin( MathF.PI * (t - MathF.Floor( t )) );

		float crown;

		if ( QuiltPattern.Index == 1 )
			crown = Crown( v / spacing );
		else
		{
			var s = spacing * MathF.Sqrt( 2f );
			crown = MathF.Min( Crown( (u + v) / s ), Crown( (u - v) / s ) );
		}

		return 0.3f + 0.7f * crown;
	}

	/// <summary>0..1 per vertex: inside the region, faded over Feather at its edge.</summary>
	float[] Mask( PolyMesh sheet, BodyRegions.Map map )
	{
		var n = sheet.VertexCount;
		var mask = new float[n];
		var size = Size.Clamped;
		var feather = FeatherValue;
		var sideWanted = GarmentRecipe.SideOf( Side.Index );
		var sides = sideWanted == 0 ? new[] { 1, -1 } : new[] { sideWanted };

		// d is how far inside the region a point is (positive inside), in inches.
		float Fade( float d )
		{
			if ( d <= 0f )
				return 0f;

			if ( feather < 1e-3f )
				return 1f;

			var t = Math.Clamp( d / feather, 0f, 1f );
			return t * t * (3f - 2f * t);
		}

		bool OnSide( Vec3 p ) => sideWanted == 0 || MathF.Sign( p.y ) == sideWanted;

		var hasTorso = map.Chain( BodyRegion.Torso, 0, out var torsoTop, out var waist );
		var neckBase = map.Chain( BodyRegion.Neck, 0, out _, out var nb ) ? nb : torsoTop;
		var forward = new Vec3( 1, 0, 0 );

		static float SegmentDistance( Vec3 p, Vec3 a, Vec3 b, out float t )
		{
			var ab = b - a;
			t = ab.LengthSquared < 1e-8f ? 0f : Math.Clamp( Vec3.Dot( p - a, ab ) / ab.LengthSquared, 0f, 1f );
			return (a + ab * t - p).Length;
		}

		void Max( int i, float w ) => mask[i] = MathF.Max( mask[i], w );

		var region = RegionNames[Math.Clamp( Where.Index, 0, RegionNames.Length - 1 )];

		switch ( region )
		{
			case "Shoulder yoke":
			case "Shoulder pad":
				foreach ( var side in sides )
				{
					if ( !map.Chain( BodyRegion.UpperArm, side, out var shoulder, out _ ) )
						continue;

					for ( var i = 0; i < n; i++ )
					{
						var p = sheet.Positions[i];

						if ( MathF.Sign( p.y ) != side )
							continue;

						if ( region == "Shoulder yoke" )
						{
							// Over the top of the shoulder, from beside the collar to the sleeve's
							// seam, a hand's width front and back.
							var a = neckBase + (shoulder - neckBase) * 0.2f;
							var b = neckBase + (shoulder - neckBase) * 0.9f;
							var d = SegmentDistance( p, a, b, out _ );
							Max( i, Fade( 3.2f * size - d ) * Fade( p.z - (shoulder.z - 3.5f * size) + 0.5f ) );
						}
						else
						{
							// A wedge: thickest at the point of the shoulder, nothing at the neck,
							// half an inch into the sleeve cap.
							var d = SegmentDistance( p, neckBase, shoulder + (shoulder - neckBase).Normal * 0.5f, out var t );
							Max( i, Fade( 3f * size - d ) * t );
						}
					}
				}

				break;

			case "Elbow":
				foreach ( var side in sides )
				{
					if ( !map.Chain( BodyRegion.UpperArm, side, out _, out var elbow ) )
						continue;

					// On the point of the elbow, which is behind it when the arm hangs.
					var centre = elbow - forward * 1.2f;

					for ( var i = 0; i < n; i++ )
						if ( MathF.Sign( sheet.Positions[i].y ) == side )
							Max( i, Fade( 2.2f * size - (sheet.Positions[i] - centre).Length ) );
				}

				break;

			case "Collar band":
			case "Cuffs":
			case "Hem band":
			{
				var loops = GarmentTrim.OrderedBoundaryLoops( sheet );
				Vec3 Centre( List<int> l ) => l.Aggregate( Vec3.Zero, ( acc, i ) => acc + sheet.Positions[i] ) / l.Count;

				var middle = loops.Where( l => MathF.Abs( Centre( l ).y ) < 2.5f ).ToList();
				var picked = region switch
				{
					"Collar band" => middle.Count > 0 ? new List<List<int>> { middle[^1] } : new List<List<int>>(),
					"Hem band" => middle.Count > 0 ? new List<List<int>> { middle[0] } : new List<List<int>>(),
					_ => loops.Where( l => MathF.Abs( Centre( l ).y ) >= 2.5f && OnSide( Centre( l ) ) ).ToList(),
				};

				// How deep the band is: a stand collar 1.75 in, a rib cuff 2.5, a waistband 2.
				var width = (region switch { "Collar band" => 1.75f, "Cuffs" => 2.5f, _ => 2f }) * size;

				foreach ( var loop in picked )
				{
					var points = loop.Select( i => sheet.Positions[i] ).ToArray();

					for ( var i = 0; i < n; i++ )
					{
						var best = float.MaxValue;

						foreach ( var q in points )
							best = MathF.Min( best, (q - sheet.Positions[i]).LengthSquared );

						Max( i, Fade( width - MathF.Sqrt( best ) + feather * 0.5f ) );
					}
				}

				break;
			}

			case "Chest":
			case "Back":
			{
				if ( !hasTorso )
					break;

				var front = region == "Chest" ? 1f : -1f;
				var cx = (torsoTop.x + waist.x) * 0.5f;
				var top = map.Chain( BodyRegion.UpperArm, 1, out var shoulder, out _ ) ? shoulder.z - 1f : torsoTop.z - 3f;
				var halfHeight = (top - waist.z) * 0.5f * size;
				var mid = (top + waist.z) * 0.5f;

				for ( var i = 0; i < n; i++ )
				{
					var p = sheet.Positions[i];

					if ( !OnSide( p ) || map.Locate( p ).Region is not (BodyRegion.Torso or BodyRegion.Hips) )
						continue;

					Max( i, Fade( (p.x - cx) * front ) * Fade( halfHeight - MathF.Abs( p.z - mid ) ) );
				}

				break;
			}

			case "Knee":
				foreach ( var side in sides )
				{
					if ( !map.Chain( BodyRegion.UpperLeg, side, out _, out var knee ) )
						continue;

					// On the kneecap, which is in front of the joint: a hard knee pad is about
					// five inches tall and wraps the front half of the leg, not the hollow behind.
					var centre = knee + forward * 1.5f;

					for ( var i = 0; i < n; i++ )
					{
						var p = sheet.Positions[i];

						if ( MathF.Sign( p.y ) == side )
							Max( i, Fade( 3.2f * size - (p - centre).Length ) * Fade( p.x - knee.x + 0.5f ) );
					}
				}

				break;

			default:
				for ( var i = 0; i < n; i++ )
					if ( OnSide( sheet.Positions[i] ) )
						mask[i] = 1f;

				break;
		}

		return mask;
	}
}
