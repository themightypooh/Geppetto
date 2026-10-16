using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>What the trouser hem does at a boot top.</summary>
public enum TrouserHem
{
	/// <summary>Tucked straight in: the leg below the boot top lies flat inside the shaft.</summary>
	Tucked,

	/// <summary>Bloused: tucked, and the slack above the boot top hangs over it in a drooping skirt
	/// with radiating folds - the military look (DA PAM 670-1).</summary>
	Bloused,

	/// <summary>Left as it was fitted.</summary>
	Leave,
}

/// <summary>
/// Boots, and what trousers do at them.
///
/// BLOUSING (outfit-research.md §6, DA PAM 670-1): the trouser is held by a band just above the
/// boot top, the leg below it lies inside the boot, and the slack above it hangs over the boot top
/// as a skirt 1-3 eyelets deep (an inch or two), with radiating folds and a rounded lip. Tucked is
/// the same without the skirt. Both are done to the trousers' own mesh, from the rig's leg axis,
/// so the trousers stay one garment and keep their weights.
/// </summary>
public static class GarmentBoots
{
	/// <summary>The boot top's height on each leg, from the recipe's shaft length: +1 left, -1 right.</summary>
	public static Dictionary<int, float> TopHeights( BodyRegions.Map map, float length )
	{
		var tops = new Dictionary<int, float>();

		foreach ( var side in new[] { 1, -1 } )
			if ( map.Chain( BodyRegion.LowerLeg, side, out var knee, out var ankle ) )
				tops[side] = knee.z + (ankle.z - knee.z) * (1f - Math.Clamp( length, 0.05f, 1f ));

		return tops;
	}

	static (Vec3 From, Vec3 Axis)? LegAxis( BodyRegions.Map map, int side )
	{
		if ( !map.Chain( BodyRegion.LowerLeg, side, out var knee, out var ankle ) )
			return null;

		return (knee, (ankle - knee).Normal);
	}

	/// <summary>The near/far wall pairing of a solidified garment: far[i] for a near vertex i, or -1.</summary>
	static int Far( PolyMesh mesh, int offset, int i ) => offset > 0 && i < offset ? i + offset : -1;

	/// <summary>
	/// Lay every trouser vertex below the boot top (plus a quarter inch) onto the leg, just off the
	/// skin, keeping the cloth's thickness: the part of the leg that goes inside the boot. Returns
	/// how many vertices moved.
	/// </summary>
	public static int Tuck( PolyMesh trousers, BodyRegions.Map map, IReadOnlyList<PolyMesh> wearers, Dictionary<int, float> tops,
		float skinGap = 0.1f )
	{
		var trees = wearers.Select( MeshBVH.Build ).ToList();
		var sheet = MeshSolidify.OuterSheet( trousers, out var offset );
		var count = sheet is null ? trousers.VertexCount : offset;
		var moved = 0;

		for ( var i = 0; i < count; i++ )
		{
			var p = trousers.Positions[i];
			var side = p.y > 0 ? 1 : -1;

			if ( !tops.TryGetValue( side, out var top ) || p.z > top + 0.25f )
				continue;

			if ( map.Locate( p ).Region is not (BodyRegion.LowerLeg or BodyRegion.Foot) )
				continue;

			MeshHit? best = null;

			for ( var b = 0; b < wearers.Count; b++ )
				if ( trees[b].NearestSurface( wearers[b], p, 6f ) is { } hit && (best is null || hit.Distance < best.Value.Distance) )
					best = hit;

			if ( best is not { } h )
				continue;

			var along = Vec3.Dot( p - h.Point, h.Normal );

			if ( along <= skinGap )
				continue;

			var near = h.Point + h.Normal * skinGap;
			var far = Far( trousers, offset, i );

			if ( far >= 0 )
			{
				var thick = (trousers.Positions[far] - p).Length;
				trousers.Positions[far] = near + h.Normal * thick;
			}

			trousers.Positions[i] = near;
			moved++;
		}

		return moved;
	}

	/// <summary>
	/// Hang the slack above each boot top over it: a skirt that stands <paramref name="overhang"/>
	/// out from the boot and droops <paramref name="drop"/> down over its top, with
	/// <paramref name="folds"/> radiating folds round the leg. Kept off <paramref name="boot"/>.
	/// Returns how many vertices moved.
	/// </summary>
	public static int Blouse( PolyMesh trousers, PolyMesh boot, BodyRegions.Map map, Dictionary<int, float> tops,
		float overhang = 0.9f, float drop = 1.2f, int folds = 7 )
	{
		var sheet = MeshSolidify.OuterSheet( trousers, out var offset );
		var count = sheet is null ? trousers.VertexCount : offset;
		var bootTree = boot is { FaceCount: > 0 } ? MeshBVH.Build( boot ) : null;
		const float Above = 3.5f;
		var moved = 0;

		for ( var i = 0; i < count; i++ )
		{
			var p = trousers.Positions[i];
			var side = p.y > 0 ? 1 : -1;

			if ( !tops.TryGetValue( side, out var top ) || LegAxis( map, side ) is not { } leg )
				continue;

			var h = p.z - top;

			if ( h < -0.25f || h > Above )
				continue;

			// Round the leg's own axis, level: the skirt stands out horizontally.
			var q = p - leg.From;
			var axial = leg.Axis * Vec3.Dot( q, leg.Axis );
			var radial = q - axial;
			var r = radial.Length;

			if ( r < 1e-4f )
				continue;

			var outward = radial / r;
			var e1 = Vec3.Cross( new Vec3( 0, 0, 1 ), leg.Axis );
			var e2 = Vec3.Cross( leg.Axis, e1 );
			var angle = MathF.Atan2( Vec3.Dot( outward, e2 ), Vec3.Dot( outward, e1 ) );

			// A bell: fullest just above the band, gone three and a half inches up.
			var t = Math.Clamp( (h + 0.25f) / (Above + 0.25f), 0f, 1f );
			var bell = MathF.Sin( MathF.PI * MathF.Sqrt( t ) );
			var fold = 1f + 0.35f * MathF.Cos( folds * angle );
			var push = overhang * bell * fold;

			// The lip: the cloth just above the top comes down over it.
			var lip = h < 1.5f ? drop * (1f - MathF.Max( 0f, h ) / 1.5f) : 0f;

			var delta = outward * push - new Vec3( 0, 0, lip );
			var far = Far( trousers, offset, i );
			trousers.Positions[i] = p + delta;

			if ( far >= 0 )
				trousers.Positions[far] += delta;

			moved++;
		}

		// Off the boot: the lip sits on the boot top's outside, never through it.
		if ( bootTree is not null )
		{
			for ( var pass = 0; pass < 3; pass++ )
				for ( var i = 0; i < count; i++ )
				{
					var p = trousers.Positions[i];
					var side = p.y > 0 ? 1 : -1;

					if ( !tops.TryGetValue( side, out var top ) || p.z < top - drop - 0.5f || p.z > top + 1f )
						continue;

					if ( bootTree.NearestSurface( boot, p, 2f ) is not { } hit )
						continue;

					var along = Vec3.Dot( p - hit.Point, hit.Normal );

					// Only what is just outside or grazing the boot's outer wall; what is deep
					// inside the shaft is the tucked leg, where it belongs.
					if ( along >= 0.08f || along < -0.6f )
						continue;

					var delta = hit.Normal * (0.08f - along);
					trousers.Positions[i] = p + delta;

					if ( Far( trousers, offset, i ) is var far and >= 0 )
						trousers.Positions[far] += delta;
				}
		}

		return moved;
	}

	/// <summary>
	/// A sole: the boot's outside lowered by <paramref name="sole"/> inches wherever it faces the
	/// ground near the bottom, so the boot stands on a slab rather than on cloth. Works on the far
	/// wall of a solidified boot; a sheet is lowered directly.
	/// </summary>
	public static int Sole( PolyMesh boot, float sole = 0.35f )
	{
		if ( boot.VertexCount == 0 )
			return 0;

		var normals = boot.ComputeVertexNormals();
		var bottom = boot.Positions.Min( p => p.z );
		var sheet = MeshSolidify.OuterSheet( boot, out var offset );
		var from = sheet is null ? 0 : offset;
		var to = sheet is null ? boot.VertexCount : 2 * offset;
		var moved = 0;

		for ( var i = from; i < to; i++ )
		{
			var p = boot.Positions[i];
			var low = 1f - Math.Clamp( (p.z - bottom) / 1.2f, 0f, 1f );

			if ( low <= 0f || normals[i].z > 0.2f )
				continue;

			boot.Positions[i] = new Vec3( p.x, p.y, p.z - sole * low * MathF.Min( 1f, -normals[i].z * 2f + 0.5f ) );
			moved++;
		}

		return moved;
	}

	/// <summary>Each boot's top opening on its sheet: average height per side.</summary>
	public static Dictionary<int, float> MeasuredTops( PolyMesh boot )
	{
		var sheet = MeshSolidify.OuterSheet( boot, out _ ) ?? boot;
		var tops = new Dictionary<int, float>();

		foreach ( var loop in GarmentTrim.OrderedBoundaryLoops( sheet ) )
		{
			var c = loop.Aggregate( Vec3.Zero, ( a, v ) => a + sheet.Positions[v] ) / loop.Count;
			var side = c.y > 0 ? 1 : -1;

			if ( !tops.TryGetValue( side, out var z ) || c.z > z )
				tops[side] = c.z;
		}

		return tops;
	}
}
