using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>How a jacket or vest's front is worn: done up, hanging open, or done up at the top only.</summary>
public enum FrontMode
{
	Closed,
	Open,

	/// <summary>Fastened down to the chest, open below it - the top button of a coat done up.</summary>
	OpenBelowChest,
}

/// <summary>What fastens the front. Decides the overlap (extension) past centre front.</summary>
public enum FrontClosure
{
	Zip,
	Buttons,
	DoubleBreasted,
	Snaps,
	Toggles,
}

/// <summary>
/// A front opening: the garment's body split down centre front into two fronts, each extended past
/// CF by its closure's overlap, hung open about its side seam, and lined with a facing strip and a
/// lining on the inside.
///
/// BAKED, NOT SIMULATED. The open hang is solved once, statically, from the fabric's stiffness and
/// weight, and written into the mesh - see research-open-jackets.md, "How Effigy should build it".
/// Live cloth only adds motion on top. Per front, per height:
///
/// 1. the front rotates about a vertical hinge at its side seam by θ(z) = Open × θmax(fabric) ×
///    s(z), where s rises from 0 at the shoulder (pinned by the yoke) to 1 at the chest apex and
///    on a little to the hem;
/// 2. below the chest a panel longer than the fabric's bending length c is pulled back to the
///    swing it had at the chest - gravity: the edge drops nearly vertically from the apex;
/// 3. soft fabrics (c under 3 in) wrap back toward what is under them;
/// 4. the front-edge hem corner drops a little, because nothing holds it level;
/// 5. smoothed, then pushed back out of the body and the inner layers, so the front rests ON the
///    chest and the shirt rather than through them.
///
/// The numbers - θmax 10-35°, c 1-2.5 in, the overlaps - are the research file's defaults, tagged
/// there as estimates to be checked by eye; they are constants here, not hidden magic.
/// </summary>
public static class GarmentFront
{
	public sealed class Options
	{
		public FrontMode Mode = FrontMode.Open;

		/// <summary>How far it hangs open, 0 just parted to 1 swung right back.</summary>
		public float Open = 0.6f;

		public FrontClosure Closure = FrontClosure.Zip;

		/// <summary>Which front laps over the other: +1 the wearer's left over the right (menswear),
		/// -1 the right over the left (womenswear).</summary>
		public int Lap = 1;

		public Fabric Fabric = Fabric.Cotton;

		/// <summary>The garment's Puff, in inches: a padded front is far stiffer in bending.</summary>
		public float Puff;

		/// <summary>Hem flare, in inches: a flared hem swings further.</summary>
		public float Flare;

		/// <summary>Cloth thickness, for the overlap's stack.</summary>
		public float Thickness = 0.12f;

		/// <summary>Material slot for the lining; -1 leaves the inside in the shell's slot.</summary>
		public int LiningSlot = -1;
	}

	/// <summary>What the split and the hang came out as, in inches, for the feature's info line and
	/// for checking against research-open-jackets.md §1.</summary>
	public sealed class Result
	{
		public float Extension;
		public float GapChest, GapWaist, GapHem;
		public float ThetaMaxDegrees, BendingLength;
		public int EdgeVertices;

		/// <summary>The cut edge on each front, top to bottom: sheet vertex indices.</summary>
		public List<int> LeftEdge = new(), RightEdge = new();

		public int LiningFaces, FacingFaces;

		public override string ToString() =>
			$"front gap {GapChest:0.0} in at the chest, {GapWaist:0.0} at the waist, {GapHem:0.0} at the hem; overlap {Extension:0.##} in each side";
	}

	/// <summary>Swing at full Open, degrees, per fabric (research §4 step 4).</summary>
	public static float ThetaMax( Fabric fabric, float puff ) => fabric switch
	{
		Fabric.Leather => 35f,
		Fabric.Denim => 28f,
		Fabric.Stretch => 10f,
		_ => 20f,
	} + Math.Clamp( puff, 0f, 2f ) * 5f;

	/// <summary>Peirce bending length, inches (research §2): how far the cloth holds its cut shape
	/// before it hangs like a curtain.</summary>
	public static float BendingLength( Fabric fabric, float puff ) => fabric switch
	{
		Fabric.Leather => 2.5f,
		Fabric.Denim => 1.4f,
		Fabric.Stretch => 1f,
		_ => 1.2f,
	} + Math.Clamp( puff, 0f, 2f ) * 2.5f;

	/// <summary>The overlap past CF on each side, inches. Buttons follow FreeSewing Jaeger's 1.5 % of
	/// the chest, clamped to 1/2-1 in; double-breasted is Müller &amp; Sohn's 7 cm.</summary>
	public static float Extension( FrontClosure closure, float chest ) => closure switch
	{
		FrontClosure.Zip => 0f,
		FrontClosure.Buttons => Math.Clamp( chest * 0.015f, 0.5f, 1f ),
		FrontClosure.DoubleBreasted => 2.75f,
		FrontClosure.Snaps => 1.25f,
		_ => 1.25f,
	};

	/// <summary>Front facing width at a height, inches: 2.25 at the shoulder, 2.75 at the waist, 3
	/// at the hem (historical-tailoring / North Shore draft, research §3).</summary>
	public static float FacingWidth( float z, float top, float waist, float hem )
	{
		if ( z >= waist )
			return 2.25f + 0.5f * Math.Clamp( (top - z) / MathF.Max( 1e-3f, top - waist ), 0f, 1f );

		return 2.75f + 0.25f * Math.Clamp( (waist - z) / MathF.Max( 1e-3f, waist - hem ), 0f, 1f );
	}

	/// <summary>
	/// Split <paramref name="garment"/> (a sheet, before thickness) down centre front, extend and
	/// hang the two fronts. <paramref name="torso"/> marks the vertices on the body of the garment
	/// (not the sleeves); <paramref name="chestZ"/> is the chest apex, <paramref name="waistZ"/> the
	/// waist. The garment is pushed back out of <paramref name="bodies"/> afterwards.
	/// </summary>
	public static Result Build( PolyMesh garment, Func<int, bool> torso, float chestZ, float waistZ, Options o,
		IReadOnlyList<PolyMesh> bodies, List<MeshBVH> trees, float clearance )
	{
		var result = new Result();
		var torsoIds = Enumerable.Range( 0, garment.VertexCount ).Where( torso ).ToList();

		if ( torsoIds.Count < 8 )
			return result;

		var cx = torsoIds.Average( i => garment.Positions[i].x );
		var cy = torsoIds.Average( i => garment.Positions[i].y );
		var top = torsoIds.Max( i => garment.Positions[i].z );
		var hem = torsoIds.Min( i => garment.Positions[i].z );

		var wasTorso = new bool[garment.VertexCount];
		foreach ( var i in torsoIds ) wasTorso[i] = true;

		var (left, right, isTorso) = Split( garment, cx, cy, wasTorso );
		result.LeftEdge = left;
		result.RightEdge = right;
		result.EdgeVertices = left.Count + right.Count;

		if ( left.Count == 0 || right.Count == 0 )
			return result;

		// The chest round, for the button overlap: twice the half-widths at the chest.
		var chestRound = 0f;
		{
			var ring = torsoIds.Where( i => MathF.Abs( garment.Positions[i].z - chestZ ) < 0.75f ).Select( i => garment.Positions[i] ).ToList();
			if ( ring.Count > 4 )
				chestRound = MathF.PI * ((ring.Max( p => p.x ) - ring.Min( p => p.x )) + (ring.Max( p => p.y ) - ring.Min( p => p.y ))) * 0.5f;
		}

		result.Extension = Extension( o.Closure, chestRound > 0f ? chestRound : 38f );
		var side = new int[garment.VertexCount];

		for ( var i = 0; i < garment.VertexCount; i++ )
			side[i] = isTorso[i] && garment.Positions[i].x > cx ? (garment.Positions[i].y >= cy ? 1 : -1) : 0;

		foreach ( var v in left ) side[v] = 1;
		foreach ( var v in right ) side[v] = -1;

		Extend( garment, side, cy, result.Extension, o );

		var c = BendingLength( o.Fabric, o.Puff );
		var thetaMax = ThetaMax( o.Fabric, o.Puff );
		result.BendingLength = c;
		result.ThetaMaxDegrees = thetaMax;

		if ( o.Mode != FrontMode.Closed && o.Open > 0f )
			Hang( garment, side, isTorso, cx, cy, top, chestZ, hem, left, right, c, thetaMax, o );

		// Back off the body and off what is worn under it, which the swing can carry the front into.
		if ( bodies is { Count: > 0 } )
			for ( var pass = 0; pass < 3; pass++ )
				GarmentFit.PushOut( garment, bodies, trees, clearance, 6f );

		result.GapChest = Gap( garment, left, right, chestZ );
		result.GapWaist = Gap( garment, left, right, waistZ );
		result.GapHem = Gap( garment, left, right, hem + 0.5f );
		return result;
	}

	/// <summary>
	/// Cut every front face that crosses the plane y = <paramref name="cy"/> in two, giving each half
	/// its own copy of the cut points, so the garment comes apart down centre front from the collar to
	/// the hem. The back (faces whose centre is behind <paramref name="cx"/>) stays whole.
	/// </summary>
	public static (List<int> Left, List<int> Right, bool[] Torso) Split( PolyMesh garment, float cx, float cy, bool[] torso )
	{
		// A vertex exactly on CF belongs to neither side; nudge it onto the left so the cut goes
		// through edges, not corners.
		for ( var i = 0; i < garment.VertexCount; i++ )
		{
			var p = garment.Positions[i];

			if ( p.x > cx && MathF.Abs( p.y - cy ) < 1e-3f )
				garment.Positions[i] = new Vec3( p.x, cy + 2e-3f, p.z );
		}

		var faces = garment.Faces.ToList();
		garment.Faces.Clear();
		var cuts = new Dictionary<(int, int, int), int>();
		var leftEdge = new HashSet<int>();
		var rightEdge = new HashSet<int>();
		var torsoList = torso.ToList();

		int CutPoint( int a, int b, int s )
		{
			var key = (Math.Min( a, b ), Math.Max( a, b ), s);

			if ( cuts.TryGetValue( key, out var at ) )
				return at;

			var pa = garment.Positions[a];
			var pb = garment.Positions[b];
			var t = Math.Clamp( (cy - pa.y) / (pb.y - pa.y), 0f, 1f );
			var cut = Vec3.Lerp( pa, pb, t );
			at = garment.AddVertex( new Vec3( cut.x, cy, cut.z ) );
			torsoList.Add( true );
			cuts[key] = at;
			(s > 0 ? leftEdge : rightEdge).Add( at );
			return at;
		}

		foreach ( var face in faces )
		{
			var n = face.Count;
			var centroid = garment.FaceCentroid( face );
			var hasLeft = false;
			var hasRight = false;

			for ( var c = 0; c < n; c++ )
			{
				if ( garment.Positions[face.Indices[c]].y >= cy ) hasLeft = true;
				else hasRight = true;
			}

			if ( centroid.x <= cx || !hasLeft || !hasRight )
			{
				garment.Faces.Add( face );
				continue;
			}

			foreach ( var s in new[] { 1, -1 } )
			{
				var poly = new List<int>();
				var uvs = new List<Vec2>();

				for ( var c = 0; c < n; c++ )
				{
					var a = face.Indices[c];
					var b = face.Indices[(c + 1) % n];
					var ina = (garment.Positions[a].y >= cy) == (s > 0);
					var inb = (garment.Positions[b].y >= cy) == (s > 0);

					if ( ina )
						poly.Add( a );

					if ( ina != inb )
						poly.Add( CutPoint( a, b, s ) );
				}

				if ( poly.Count >= 3 )
					garment.AddFace( poly.ToArray(), null, face.Material );
			}
		}

		var isTorso = torsoList.ToArray();

		List<int> Ordered( HashSet<int> set ) => set.OrderByDescending( v => garment.Positions[v].z ).ToList();
		return (Ordered( leftEdge ), Ordered( rightEdge ), isTorso);
	}

	/// <summary>The fronts carried past CF by the closure's extension, the lap side lifted over the
	/// under side by the stack so the overlap reads as two layers rather than one fighting itself.</summary>
	static void Extend( PolyMesh garment, int[] side, float cy, float extension, Options o )
	{
		if ( extension <= 0f )
			return;

		var reach = MathF.Max( 2f, extension * 3f );
		var normals = garment.ComputeVertexNormals();
		var lift = o.Mode == FrontMode.Closed ? o.Thickness + 0.06f : 0f;

		for ( var i = 0; i < garment.VertexCount; i++ )
		{
			if ( side[i] == 0 )
				continue;

			var p = garment.Positions[i];
			var d = MathF.Abs( p.y - cy );

			if ( d >= reach )
				continue;

			var w = 1f - d / reach;
			p = new Vec3( p.x, p.y - side[i] * extension * w, p.z );

			if ( side[i] == o.Lap && lift > 0f )
				p += normals[i] * (lift * MathF.Min( 1f, w * 2f ));

			garment.Positions[i] = p;
		}
	}

	static float Smooth( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	static void Hang( PolyMesh garment, int[] side, bool[] torso, float cx, float cy, float top, float chestZ, float hem,
		List<int> left, List<int> right, float c, float thetaMax, Options o )
	{
		var start = garment.Positions.ToArray();

		// The hinge: the side seam of each row, where the front meets the back - the widest point of
		// the body of the garment at that height, near the plane of the body's centre.
		const float Row = 0.5f;
		var rows = (int)MathF.Ceiling( (top - hem) / Row ) + 2;
		var halfWidth = new float[rows];

		for ( var i = 0; i < garment.VertexCount; i++ )
		{
			if ( !torso[i] || MathF.Abs( start[i].x - cx ) > 2.5f )
				continue;

			var r = Math.Clamp( (int)((top - start[i].z) / Row), 0, rows - 1 );
			halfWidth[r] = MathF.Max( halfWidth[r], MathF.Abs( start[i].y - cy ) );
		}

		// Rows with no side points borrow their neighbours'.
		for ( var r = 1; r < rows; r++ ) if ( halfWidth[r] <= 0f ) halfWidth[r] = halfWidth[r - 1];
		for ( var r = rows - 2; r >= 0; r-- ) if ( halfWidth[r] <= 0f ) halfWidth[r] = halfWidth[r + 1];

		float Hinge( float z ) => halfWidth[Math.Clamp( (int)((top - z) / Row), 0, rows - 1 )];

		var flare = 1.15f + 0.3f * Math.Clamp( o.Flare / 2f, 0f, 1f );
		var fastenedAbove = o.Mode == FrontMode.OpenBelowChest ? chestZ : float.MaxValue;

		float Swing( float z )
		{
			if ( z >= fastenedAbove )
				return 0f;

			var s = z >= chestZ
				? Smooth( (top - z) / MathF.Max( 1e-3f, top - chestZ ) )
				: 1f + (flare - 1f) * Math.Clamp( (chestZ - z) / MathF.Max( 1e-3f, chestZ - hem ), 0f, 1f );

			// Fastened at the chest: the swing grows from there instead of from the shoulder.
			if ( o.Mode == FrontMode.OpenBelowChest )
				s *= Smooth( (chestZ - z) / 4f );

			return Math.Clamp( o.Open, 0f, 1f ) * thetaMax * MathF.PI / 180f * s;
		}

		Vec3 Rotate( Vec3 p, int s, float theta )
		{
			var hy = cy + s * Hinge( p.z );
			var dx = p.x - cx;
			var dy = p.y - hy;
			var a = s * theta;
			var cos = MathF.Cos( a );
			var sin = MathF.Sin( a );
			return new Vec3( cx + dx * cos - dy * sin, hy + dx * sin + dy * cos, p.z );
		}

		var soft = Math.Clamp( (1f - c / 3f) * 0.5f, 0f, 0.5f );
		var hemDrop = 0.25f + 0.5f * Math.Clamp( 1f - c / 6f, 0f, 1f );
		var thetaChest = Swing( chestZ );
		var moved = new List<int>();

		for ( var i = 0; i < garment.VertexCount; i++ )
		{
			var s = side[i];

			if ( s == 0 )
				continue;

			var p = start[i];

			// Faded in from the side seam, so the front turns about it rather than kinking there.
			var w = Smooth( (p.x - cx) / 2f );

			if ( w <= 0f )
				continue;

			var swung = Rotate( p, s, Swing( p.z ) * w );

			// Gravity: below the chest a panel longer than the bending length hangs back to the
			// swing it had at the apex.
			if ( p.z < chestZ && o.Mode != FrontMode.OpenBelowChest )
			{
				var below = chestZ - p.z;
				var g = 1f - MathF.Min( 1f, c / MathF.Max( 1e-3f, below ) );
				swung = Vec3.Lerp( swung, Rotate( p, s, thetaChest * w ), g );
			}

			// Soft cloth falls back onto what is under it.
			swung = Vec3.Lerp( swung, p, soft );

			// The front-edge hem corner drops: nothing holds it level any more.
			var fromEdge = MathF.Abs( p.y - cy );
			var fromHem = p.z - hem;

			if ( fromEdge < 4f && fromHem < 4f )
				swung = new Vec3( swung.x, swung.y, swung.z - hemDrop * (1f - fromEdge / 4f) * (1f - MathF.Max( 0f, fromHem ) / 4f) );

			garment.Positions[i] = swung;
			moved.Add( i );
		}

		// A few light passes over what moved: no kink at the hinge, no step between rows.
		var neighbours = new HashSet<int>[garment.VertexCount];

		foreach ( var f in garment.Faces )
			for ( var k = 0; k < f.Count; k++ )
			{
				var a = f.Indices[k];
				var b = f.Indices[(k + 1) % f.Count];
				(neighbours[a] ??= new HashSet<int>()).Add( b );
				(neighbours[b] ??= new HashSet<int>()).Add( a );
			}

		var edge = new HashSet<int>( left.Concat( right ) );

		for ( var pass = 0; pass < 4; pass++ )
		{
			var next = garment.Positions.ToArray();

			foreach ( var i in moved )
			{
				if ( neighbours[i] is not { Count: > 0 } n )
					continue;

				var avg = Vec3.Zero;
				foreach ( var j in n ) avg += garment.Positions[j];
				avg /= n.Count;
				next[i] = Vec3.Lerp( garment.Positions[i], avg, edge.Contains( i ) ? 0.1f : 0.3f );
			}

			for ( var i = 0; i < next.Length; i++ )
				garment.Positions[i] = next[i];
		}
	}

	/// <summary>The edge point of a front at height <paramref name="z"/>, interpolated along the
	/// edge; null when the edge does not reach that height.</summary>
	public static Vec3? EdgeAt( PolyMesh garment, List<int> edge, float z )
	{
		var points = edge.Select( v => garment.Positions[v] ).OrderByDescending( p => p.z ).ToList();

		for ( var k = 0; k + 1 < points.Count; k++ )
		{
			var a = points[k];
			var b = points[k + 1];

			if ( z <= a.z && z >= b.z )
			{
				var t = a.z - b.z < 1e-5f ? 0f : (a.z - z) / (a.z - b.z);
				return Vec3.Lerp( a, b, t );
			}
		}

		return null;
	}

	/// <summary>The horizontal distance between the two front edges at a height, inches.</summary>
	public static float Gap( PolyMesh garment, List<int> left, List<int> right, float z )
	{
		if ( EdgeAt( garment, left, z ) is not { } l || EdgeAt( garment, right, z ) is not { } r )
			return 0f;

		var d = l - r;
		return MathF.Sqrt( d.x * d.x + d.y * d.y );
	}

	/// <summary>
	/// Put the inside of a solidified jacket on its lining slot, except the facing - the strip of
	/// shell fabric turned in along each front edge (2.25-3 in, see <see cref="FacingWidth"/>) - and
	/// an inch and a half inside every other opening, which is turned-back shell too (hems, cuffs,
	/// the back neck). <paramref name="sheet"/> is the garment before thickness: the solid's near
	/// wall is its vertices, and each sheet face k became faces 2k (outside) and 2k+1 (inside).
	/// </summary>
	public static (int Lining, int Facing) Line( PolyMesh solid, PolyMesh sheet, Result front, int liningSlot,
		float top, float waist, float hem )
	{
		if ( liningSlot < 0 || solid.FaceCount < 2 * sheet.FaceCount )
			return (0, 0);

		var edge = new HashSet<int>( front.LeftEdge.Concat( front.RightEdge ) );
		var boundary = GarmentFit.BoundaryVertices( sheet );
		var edgePoints = edge.Select( v => sheet.Positions[v] ).ToList();
		var otherPoints = Enumerable.Range( 0, sheet.VertexCount ).Where( v => boundary[v] && !edge.Contains( v ) )
			.Select( v => sheet.Positions[v] ).ToList();

		static float Nearest( Vec3 p, List<Vec3> points )
		{
			var best = float.MaxValue;
			foreach ( var q in points ) best = MathF.Min( best, (q - p).LengthSquared );
			return points.Count == 0 ? float.MaxValue : MathF.Sqrt( best );
		}

		int lining = 0, facing = 0;

		for ( var k = 0; k < sheet.FaceCount; k++ )
		{
			var c = sheet.FaceCentroid( sheet.Faces[k] );
			var inFacing = Nearest( c, edgePoints ) < FacingWidth( c.z, top, waist, hem )
				|| Nearest( c, otherPoints ) < 1.5f;

			if ( inFacing )
			{
				facing++;
				continue;
			}

			solid.Faces[2 * k + 1].Material = liningSlot;
			lining++;
		}

		return (lining, facing);
	}
}
