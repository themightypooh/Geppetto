using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// The two mouths the other two repairs decline: one across MORE than two faces, and one across
/// faces that do not share a plane at all.
///
/// WHAT-IS-LEFT named both. "A cut through a curved face — the mouth is not planar, so
/// FindContainingFace finds nothing and declines; the repair will need per-face loop splitting
/// rather than one whole loop." And the same shape arrives a second way: the single-face repair
/// closes a hole by triangulating, so the next cut into that surface lands across a dozen coplanar
/// triangles, which is neither one face nor two.
///
/// MEASURED BY BOUNDARY EDGES, ENCLOSED VOLUME AND SURFACE AREA, never by eye — the standing rule
/// in this file's neighbour, and it earned it: every bug ever fixed in this boolean produced a
/// mesh that was closed, manifold, Euler-correct and wrong.
/// </summary>
public static class CurvedHoleTests
{
	public static void Run()
	{
		Report.Section( "curved: a mouth across THREE coplanar faces, one of them crossed twice" );
		TestMouthAcrossThreeStrips();

		Report.Section( "curved: a mouth across two faces that do NOT share a plane" );
		TestMouthAcrossARidge();

		Report.Section( "curved: a second cut into a face the first repair triangulated" );
		TestSecondCutIntoARepairedFace();

		Report.Section( "curved: what it refuses, and leaves visibly open" );
		TestRefusals();
	}

	/// <summary>
	/// A lid in three strips with a rectangular mouth spanning all of them.
	///
	/// THE MIDDLE STRIP IS THE POINT. The mouth enters it on one side and leaves on the other, so
	/// that face is crossed TWICE and has to come back as three regions — material, hole, material —
	/// rather than being notched once. That is the same topology a hole drilled through the wall of
	/// a cylinder has, laid out flat where a test can state the answer exactly.
	/// </summary>
	static void TestMouthAcrossThreeStrips()
	{
		var mesh = ThreeStripFixture();

		var before = MeshValidator.Validate( mesh );

		Report.Check( "the fixture starts with the mouth open", before.BoundaryEdges == 8,
			$"{before.BoundaryEdges} boundary edges" );

		Report.Check( "and neither existing repair will touch it",
			MeshHoleRepairSpan.CloseLoopsSpanningFaces( ThreeStripFixture() ) == 0,
			"the span repair spliced a three-face mouth" );

		var closed = MeshHoleRepairCurved.CloseCurvedLoops( mesh );

		Report.Check( "the curved repair closes it", closed == 1, $"closed {closed}" );

		var after = MeshValidator.Validate( mesh );

		Report.Check( "no boundary edges are left", after.BoundaryEdges == 0, $"{after.BoundaryEdges} left" );
		Report.Check( "nothing was made non-manifold", after.NonManifoldEdges == 0, $"{after.NonManifoldEdges}" );
		Report.Check( "the mesh is valid", after.IsValid, after.ToString() );

		// THE CHECK THAT CANNOT BE FAKED. Sealing the mouth over rather than around it also reports
		// zero boundary edges, and encloses the pocket as solid.
		var volume = MathF.Abs( mesh.SignedVolume() );

		Report.Check( "and it encloses a 6x4x2 block less a 4x2x1 pocket",
			MathF.Abs( volume - 40f ) < 0.01f, $"{volume:0.####}, expected 40" );

		// Four faces at the lid: the outer strips notched once each, the middle one cut into two.
		var lid = mesh.Faces.Where( f =>
			MathF.Abs( mesh.FaceNormal( f ).Normal.z - 1f ) < 1e-3f
			&& MathF.Abs( mesh.FaceCentroid( f ).z ) < 1e-3f ).ToList();

		Report.Check( "the lid comes back as four faces, not a fan", lid.Count == 4,
			$"{lid.Count} faces at the lid" );

		// THE BOW-TIE CHECK. A region spliced together the wrong way round keeps its vertex count,
		// keeps a boundary count of zero and keeps a +Z Newell normal. Its area does not survive.
		var lidArea = lid.Sum( f => mesh.FaceArea( f ) );

		Report.Check( "and together they cover the lid less the mouth, so none is folded over itself",
			MathF.Abs( lidArea - 16f ) < 0.01f, $"{lidArea:0.####}, expected 16" );

		// The middle strip is the one that was crossed twice: its two pieces are the 2x1 strips
		// above and below the mouth. If materiality had been inherited down the split rather than
		// read off each finished region, the lower one would have been dropped as hole.
		var middle = lid.Where( f => mesh.FaceCentroid( f ).x > -1f && mesh.FaceCentroid( f ).x < 1f ).ToList();

		Report.Check( "the twice-crossed face came back as TWO regions", middle.Count == 2,
			$"{middle.Count}" );
		Report.Check( "one on each side of the mouth",
			middle.Count == 2 && middle.Any( f => mesh.FaceCentroid( f ).y > 0 ) && middle.Any( f => mesh.FaceCentroid( f ).y < 0 ),
			middle.Count == 2 ? string.Join( ", ", middle.Select( f => $"y={mesh.FaceCentroid( f ).y:0.##}" ) ) : "" );

		// And it is reachable the ordinary way, without a caller knowing which shape of mouth it has.
		var frontDoor = ThreeStripFixture();

		Report.Check( "the ordinary repair reaches it without being asked specially",
			MeshHoleRepair.CloseBoundaryLoopsIntoFaces( frontDoor ) == 1
			&& MeshValidator.Validate( frontDoor ).BoundaryEdges == 0,
			$"{MeshValidator.Validate( frontDoor ).BoundaryEdges} boundary edges left" );
	}

	/// <summary>
	/// The case no planar argument can even be stated in: a mouth across a RIDGE, where the two
	/// faces it lies in face different ways.
	///
	/// The loop has no plane, so it has no normal, so FindContainingFace has nothing to compare and
	/// the span repair's single shared basis does not exist. Every decision here has to come from
	/// the wall's own winding, which is why the repair reads materiality off that rather than off a
	/// containment test.
	/// </summary>
	static void TestMouthAcrossARidge()
	{
		var mesh = RidgeFixture( out var expectedVolume, out var expectedRoofArea );

		var before = MeshValidator.Validate( mesh );

		Report.Check( "the fixture starts with the mouth open", before.BoundaryEdges == 4,
			$"{before.BoundaryEdges}" );

		// Stated rather than assumed: the loop really is non-planar, so this is not the span case
		// wearing a hat.
		Report.Check( "and the mouth really is non-planar", !IsPlanar( mesh, MouthOfRidge( mesh ) ) );

		Report.Check( "so neither existing repair touches it",
			MeshHoleRepairSpan.CloseLoopsSpanningFaces( RidgeFixture( out _, out _ ) ) == 0 );

		var closed = MeshHoleRepairCurved.CloseCurvedLoops( mesh );

		Report.Check( "the curved repair closes it", closed == 1, $"closed {closed}" );

		var after = MeshValidator.Validate( mesh );

		Report.Check( "no boundary edges are left", after.BoundaryEdges == 0, $"{after.BoundaryEdges} left" );
		Report.Check( "nothing was made non-manifold", after.NonManifoldEdges == 0, $"{after.NonManifoldEdges}" );
		Report.Check( "the mesh is valid", after.IsValid, after.ToString() );

		var volume = MathF.Abs( mesh.SignedVolume() );

		Report.Check( "and the solid is the tent less the shaft driven through its ridge",
			MathF.Abs( volume - expectedVolume ) < 0.02f,
			$"{volume:0.####}, expected about {expectedVolume:0.####}" );

		// Both roof panels are still one face each, notched - not a patch, not a fan.
		var roof = mesh.Faces.Where( f => MathF.Abs( mesh.FaceNormal( f ).Normal.z ) > 0.5f
			&& mesh.FaceCentroid( f ).z > 0.1f ).ToList();

		Report.Check( "the roof is still two faces, each notched", roof.Count == 2, $"{roof.Count}" );

		Report.Check( "covering the roof less the mouth, so neither is folded over itself",
			MathF.Abs( roof.Sum( f => mesh.FaceArea( f ) ) - expectedRoofArea ) < 0.02f,
			$"{roof.Sum( f => mesh.FaceArea( f ) ):0.####}, expected {expectedRoofArea:0.####}" );
	}

	/// <summary>
	/// Cut once, repair, cut again into the same surface. The second mouth lands across the fan of
	/// triangles the first repair left, which is the case WHAT-IS-LEFT filed under "cutting a body
	/// that has already been cut".
	/// </summary>
	static void TestSecondCutIntoARepairedFace()
	{
		var mesh = TwicePocketedFixture();

		var before = MeshValidator.Validate( mesh );

		Report.Check( "the second mouth starts open", before.BoundaryEdges == 4,
			$"{before.BoundaryEdges}" );

		// The lid is a fan by now, so there is no single containing face and there are more than two
		// coplanar candidates - both earlier repairs decline, which is what leaves this to the third.
		var closed = MeshHoleRepair.CloseBoundaryLoopsIntoFaces( mesh );

		Report.Check( "the repair closes the second mouth too", closed == 1, $"closed {closed}" );

		var after = MeshValidator.Validate( mesh );

		Report.Check( "no boundary edges are left", after.BoundaryEdges == 0, $"{after.BoundaryEdges} left" );
		Report.Check( "and the mesh is valid and manifold",
			after.IsValid && after.NonManifoldEdges == 0, after.ToString() );

		var volume = MathF.Abs( mesh.SignedVolume() );

		// A 6x4x2 block is 48, and each pocket is a 2x2 print one deep. Both open is 40; one sealed
		// over would read 44 and still pass every closed-and-manifold check above it.
		Report.Check( "and BOTH pockets are voids rather than one being filled in",
			MathF.Abs( volume - 40f ) < 0.02f, $"{volume:0.####}, expected 40" );
	}

	/// <summary>
	/// The refusals. A repair with this much machinery in it has to be trusted to decline, and the
	/// rollback in CloseCurvedLoops is what makes that a guarantee rather than an intention.
	/// </summary>
	static void TestRefusals()
	{
		// A mouth SLID SIDEWAYS, so its corners no longer land on the strips' shared edges and the
		// crossings fall in the middle of the mouth's own edges instead. Each of those edges runs
		// from one strip into the next, so no single face contains it, and closing anything here
		// would mean inventing the crossing - which means splitting a face this was not asked to
		// touch. The same refusal the span repair makes, for the same reason.
		var offset = ThreeStripFixture( shiftMouthX: 0.3f );

		Report.Check( "a mouth crossing between vertices is declined rather than guessed at",
			MeshHoleRepairCurved.CloseCurvedLoops( offset ) == 0,
			"it spliced a crossing it could not name" );
		Report.Check( "and the opening is still there to be seen",
			MeshValidator.Validate( offset ).BoundaryEdges > 0 );

		// A mouth inside ONE face belongs to the single-face repair, which splices a hole rather
		// than notching a boundary. Doing it here would be a different and worse answer.
		var single = OneFaceFixture();

		Report.Check( "a mouth inside one face is left to the single-face repair",
			MeshHoleRepairCurved.CloseCurvedLoops( single ) == 0 );
		Report.Check( "which then closes it",
			MeshHoleRepair.CloseBoundaryLoopsIntoFaces( single ) == 1 );

		// And the fixtures the other repairs own are still theirs: running the whole chain must not
		// let the last pass take work off the first two.
		var closedMesh = Primitives.Box( 2, 2, 2 );

		Report.Check( "a closed solid is left completely alone",
			MeshHoleRepairCurved.CloseCurvedLoops( closedMesh ) == 0
			&& closedMesh.FaceCount == 6, $"{closedMesh.FaceCount} faces" );
	}

	// --- fixtures ---------------------------------------------------------------------------------

	/// <summary>
	/// A 6x4x2 block whose lid at z = 0 is THREE coplanar strips, with a 4x2 rectangular pocket one
	/// unit deep whose mouth spans all three of them.
	///
	/// The mouth's corners on the strip boundaries are ring vertices, which is what makes the repair
	/// possible at all: the crossings already exist and nothing has to be invented. `shiftMouthX`
	/// slides the mouth sideways so they no longer do, which is the case that must be declined.
	/// </summary>
	static PolyMesh ThreeStripFixture( float shiftMouthX = 0f )
	{
		var mesh = new PolyMesh();

		// The lid, at z = 0, split at x = -1 and x = +1.
		var t = new Dictionary<(float, float), int>();

		int Lid( float x, float y )
		{
			if ( !t.TryGetValue( (x, y), out var index ) )
				t[(x, y)] = index = mesh.AddVertex( new Vec3( x, y, 0 ) );

			return index;
		}

		mesh.AddFace( new[] { Lid( -3, -2 ), Lid( -1, -2 ), Lid( -1, 2 ), Lid( -3, 2 ) } );
		mesh.AddFace( new[] { Lid( -1, -2 ), Lid( 1, -2 ), Lid( 1, 2 ), Lid( -1, 2 ) } );
		mesh.AddFace( new[] { Lid( 1, -2 ), Lid( 3, -2 ), Lid( 3, 2 ), Lid( 1, 2 ) } );

		// The mouth, counter-clockwise seen from above. Its corners at x = +-1 are what the strips'
		// shared edges are crossed at.
		var ring = new[]
		{
			new Vec2( -2, -1 ), new Vec2( -1, -1 ), new Vec2( 1, -1 ), new Vec2( 2, -1 ),
			new Vec2( 2, 1 ), new Vec2( 1, 1 ), new Vec2( -1, 1 ), new Vec2( -2, 1 ),
		};

		var top = new int[ring.Length];
		var bottom = new int[ring.Length];

		for ( var i = 0; i < ring.Length; i++ )
		{
			top[i] = mesh.AddVertex( new Vec3( ring[i].x + shiftMouthX, ring[i].y, 0 ) );
			bottom[i] = mesh.AddVertex( new Vec3( ring[i].x + shiftMouthX, ring[i].y, -1 ) );
		}

		// The pocket wall faces INWARD - the material is outside the bore, so the surface bounding
		// it points into the void. Backwards here builds a plug rather than a hole, and the volume
		// check is what says which one was built.
		for ( var i = 0; i < ring.Length; i++ )
		{
			var next = (i + 1) % ring.Length;
			mesh.AddFace( new[] { top[i], top[next], bottom[next], bottom[i] } );
		}

		mesh.AddFace( (int[])bottom.Clone() );

		// The base, and the four outer walls. The two that meet the split lid are six-sided, because
		// their top edge is broken twice by the lid's own splits - a quad there would leave those
		// edges unmatched, which is an opening the repair never touched and would be blamed for.
		var e0 = mesh.AddVertex( new Vec3( -3, -2, -2 ) );
		var e1 = mesh.AddVertex( new Vec3( 3, -2, -2 ) );
		var e2 = mesh.AddVertex( new Vec3( 3, 2, -2 ) );
		var e3 = mesh.AddVertex( new Vec3( -3, 2, -2 ) );

		mesh.AddFace( new[] { e0, e3, e2, e1 } );

		mesh.AddFace( new[] { Lid( -3, -2 ), e0, e1, Lid( 3, -2 ), Lid( 1, -2 ), Lid( -1, -2 ) } );
		mesh.AddFace( new[] { Lid( -3, 2 ), Lid( -1, 2 ), Lid( 1, 2 ), Lid( 3, 2 ), e2, e3 } );
		mesh.AddFace( new[] { Lid( -3, -2 ), Lid( -3, 2 ), e3, e0 } );
		mesh.AddFace( new[] { Lid( 3, -2 ), e1, e2, Lid( 3, 2 ) } );

		return mesh;
	}

	/// <summary>
	/// A tent: two roof panels meeting at a ridge along y, walls down to z = 0, and a square shaft
	/// driven straight down through the ridge.
	///
	/// The shaft's mouth is four points, two of them ON the ridge and two off it to either side, so
	/// the loop is not planar and the two faces it lies in do not share a plane. Nothing about this
	/// can be phrased as "the face containing the loop".
	/// </summary>
	static PolyMesh RidgeFixture( out float volume, out float roofArea )
	{
		const float half = 2f;   // the tent runs from x = -2 to 2
		const float length = 4f; // and from y = -2 to 2
		const float peak = 2f;   // with its ridge 2 up, along x = 0
		const float bore = 1f;   // the shaft's print is the diamond |x| + |y| <= 1

		var mesh = new PolyMesh();

		// Roof corners and the ridge, with the mouth's crossings already present as ridge vertices.
		var lw = mesh.AddVertex( new Vec3( -half, -length * 0.5f, 0 ) );
		var lb = mesh.AddVertex( new Vec3( -half, length * 0.5f, 0 ) );
		var rw = mesh.AddVertex( new Vec3( half, -length * 0.5f, 0 ) );
		var rb = mesh.AddVertex( new Vec3( half, length * 0.5f, 0 ) );

		var ridgeFront = mesh.AddVertex( new Vec3( 0, -length * 0.5f, peak ) );
		var ridgeA = mesh.AddVertex( new Vec3( 0, -bore, peak ) );
		var ridgeB = mesh.AddVertex( new Vec3( 0, bore, peak ) );
		var ridgeBack = mesh.AddVertex( new Vec3( 0, length * 0.5f, peak ) );

		// The mouth's two off-ridge corners, one on each panel, half way down the slope.
		var slope = 0.5f;
		var leftMouth = mesh.AddVertex( new Vec3( -half * slope, 0, peak * (1f - slope) ) );
		var rightMouth = mesh.AddVertex( new Vec3( half * slope, 0, peak * (1f - slope) ) );

		// Each roof panel is one face, its ridge edge broken at the two crossings.
		mesh.AddFace( new[] { lw, ridgeFront, ridgeA, ridgeB, ridgeBack, lb } );
		mesh.AddFace( new[] { rw, rb, ridgeBack, ridgeB, ridgeA, ridgeFront } );

		// The shaft. Four points: two on the ridge, one on each panel. Its walls face inward, same
		// convention as the other fixture and for the same reason.
		var top = new[] { ridgeA, rightMouth, ridgeB, leftMouth };
		var bottom = new int[4];

		for ( var i = 0; i < 4; i++ )
			bottom[i] = mesh.AddVertex( new Vec3( mesh.Positions[top[i]].x, mesh.Positions[top[i]].y, 0 ) );

		for ( var i = 0; i < 4; i++ )
		{
			var next = (i + 1) % 4;
			mesh.AddFace( new[] { top[i], top[next], bottom[next], bottom[i] } );
		}

		// The two gable ends, and the floor. The shaft's own bottom is capped at z = 0 too: the shaft
		// reaches the floor plane exactly, so the void it encloses is bounded below by that cap
		// sitting against the floor. Both lie at z = 0 and so contribute nothing to the enclosed
		// volume either way, which keeps the number below a statement about the shaft rather than
		// about how the fixture was closed off.
		mesh.AddFace( new[] { lw, rw, ridgeFront } );
		mesh.AddFace( new[] { lb, ridgeBack, rb } );
		mesh.AddFace( new[] { lw, lb, rb, rw } );
		mesh.AddFace( new[] { bottom[0], bottom[1], bottom[2], bottom[3] } );

		// THE TENT: a triangular prism, half base times height times length.
		var tentVolume = 0.5f * (half * 2f) * peak * length;

		// THE SHAFT: a prism on the diamond |x| + |y| <= bore, whose area is 2*bore^2 (its diagonals
		// are 2*bore each), rising to a roof at z = peak - |x| * peak / half. The mean of |x| over
		// that diamond is bore/3, so the mean height is peak * (1 - bore / (3 * half)).
		var shaftArea = 2f * bore * bore;
		var shaftHeight = peak * (1f - bore / (3f * half));

		volume = tentVolume - shaftArea * shaftHeight;

		// Each panel is a rectangle `length` by the slope, less the triangle the mouth takes out of
		// it: base 2*bore along the ridge, height the slope distance out to the mouth's corner.
		var slopeLength = MathF.Sqrt( half * half + peak * peak );
		var panelArea = length * slopeLength;
		var mouthTriangle = 0.5f * (bore * 2f) * (slopeLength * slope);

		roofArea = 2f * (panelArea - mouthTriangle);

		return mesh;
	}

	/// <summary>
	/// A block already cut once and repaired — so its lid is a fan of triangles — with a SECOND
	/// mouth open in it.
	///
	/// Built by running the first repair for real rather than by hand-writing a fan, because the fan
	/// this has to cope with is the one the repair actually produces.
	/// </summary>
	static PolyMesh TwicePocketedFixture()
	{
		var mesh = new PolyMesh();

		// A 6x4x2 block with a single lid face.
		var t0 = mesh.AddVertex( new Vec3( -3, -2, 0 ) );
		var t1 = mesh.AddVertex( new Vec3( 3, -2, 0 ) );
		var t2 = mesh.AddVertex( new Vec3( 3, 2, 0 ) );
		var t3 = mesh.AddVertex( new Vec3( -3, 2, 0 ) );

		mesh.AddFace( new[] { t0, t1, t2, t3 } );

		var e0 = mesh.AddVertex( new Vec3( -3, -2, -2 ) );
		var e1 = mesh.AddVertex( new Vec3( 3, -2, -2 ) );
		var e2 = mesh.AddVertex( new Vec3( 3, 2, -2 ) );
		var e3 = mesh.AddVertex( new Vec3( -3, 2, -2 ) );

		mesh.AddFace( new[] { e0, e3, e2, e1 } );
		mesh.AddFace( new[] { t0, e0, e1, t1 } );
		mesh.AddFace( new[] { t3, t2, e2, e3 } );
		mesh.AddFace( new[] { t0, t3, e3, e0 } );
		mesh.AddFace( new[] { t1, e1, e2, t2 } );

		Pocket( mesh, centreX: -1.5f );

		// The first repair runs for real: the lid becomes a fan with the first pocket spliced in.
		var first = MeshHoleRepair.CloseBoundaryLoopsIntoFaces( mesh );

		if ( first != 1 )
			throw new InvalidOperationException( $"the fixture's first repair closed {first} loops, not 1" );

		Pocket( mesh, centreX: 1.5f );

		return mesh;

		// A 2x2 pocket one unit deep, as a bare mouth and a lining - exactly what the engine's
		// boolean hands back.
		static void Pocket( PolyMesh mesh, float centreX )
		{
			var corners = new[]
			{
				new Vec2( centreX - 1, -1 ), new Vec2( centreX + 1, -1 ),
				new Vec2( centreX + 1, 1 ), new Vec2( centreX - 1, 1 ),
			};

			var top = new int[4];
			var bottom = new int[4];

			for ( var i = 0; i < 4; i++ )
			{
				top[i] = mesh.AddVertex( new Vec3( corners[i].x, corners[i].y, 0 ) );
				bottom[i] = mesh.AddVertex( new Vec3( corners[i].x, corners[i].y, -1 ) );
			}

			for ( var i = 0; i < 4; i++ )
			{
				var next = (i + 1) % 4;
				mesh.AddFace( new[] { top[i], top[next], bottom[next], bottom[i] } );
			}

			mesh.AddFace( (int[])bottom.Clone() );
		}
	}

	/// <summary>A mouth wholly inside one face — the single-face repair's own case.</summary>
	static PolyMesh OneFaceFixture()
	{
		var mesh = new PolyMesh();

		mesh.AddFace( new[]
		{
			mesh.AddVertex( new Vec3( -3, -2, 0 ) ),
			mesh.AddVertex( new Vec3( 3, -2, 0 ) ),
			mesh.AddVertex( new Vec3( 3, 2, 0 ) ),
			mesh.AddVertex( new Vec3( -3, 2, 0 ) ),
		} );

		var top = new int[4];
		var bottom = new int[4];
		var corners = new[] { new Vec2( -1, -1 ), new Vec2( 1, -1 ), new Vec2( 1, 1 ), new Vec2( -1, 1 ) };

		for ( var i = 0; i < 4; i++ )
		{
			top[i] = mesh.AddVertex( new Vec3( corners[i].x, corners[i].y, 0 ) );
			bottom[i] = mesh.AddVertex( new Vec3( corners[i].x, corners[i].y, -1 ) );
		}

		for ( var i = 0; i < 4; i++ )
		{
			var next = (i + 1) % 4;
			mesh.AddFace( new[] { top[i], top[next], bottom[next], bottom[i] } );
		}

		mesh.AddFace( (int[])bottom.Clone() );

		return mesh;
	}

	// --- helpers ----------------------------------------------------------------------------------

	/// <summary>The open boundary loop of a mesh with exactly one, as vertex indices.</summary>
	static List<int> MouthOfRidge( PolyMesh mesh )
	{
		var single = new List<EdgeKey>();

		foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count == 1 )
				single.Add( key );
		}

		var loop = new List<int>();

		if ( single.Count == 0 )
			return loop;

		var current = single[0].A;
		var used = new HashSet<int>();

		while ( used.Add( current ) )
		{
			loop.Add( current );

			var next = -1;

			foreach ( var key in single )
			{
				var other = key.A == current ? key.B : key.B == current ? key.A : -1;

				if ( other >= 0 && !used.Contains( other ) )
				{
					next = other;
					break;
				}
			}

			if ( next < 0 )
				break;

			current = next;
		}

		return loop;
	}

	/// <summary>Whether every point of a loop sits in one plane. Stated as a check so the ridge
	/// fixture's whole reason for existing is asserted rather than assumed.</summary>
	static bool IsPlanar( PolyMesh mesh, List<int> loop )
	{
		if ( loop.Count < 4 )
			return true;

		var a = mesh.Positions[loop[0]];
		var normal = Vec3.Cross( mesh.Positions[loop[1]] - a, mesh.Positions[loop[2]] - a );

		if ( normal.LengthSquared < 1e-12f )
			return true;

		normal = normal.Normal;

		foreach ( var index in loop )
		{
			if ( MathF.Abs( Vec3.Dot( mesh.Positions[index] - a, normal ) ) > 1e-3f )
				return false;
		}

		return true;
	}
}
