using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Scripted sculpting on a generated mesh with the kernel's own brushes: a stroke is a curve in
/// space, snapped onto the surface and spaced the way a hand-drawn stroke is (a fifth of the radius
/// apart), then run through Brush.Apply exactly as the editor's sculpt session runs a drag. For a
/// generator's sculpt pass: creases along lines, clay built up in strips, cloth folds dabbed on.
/// </summary>
public sealed class Sculptor
{
	readonly PolyMesh _mesh;
	readonly MeshBVH _bvh;
	readonly List<EdgeKey>[] _neighbors;

	/// <summary>Per-vertex 0..1 (1 free), or null: what strokes may move.</summary>
	public float[] Mask;

	public Sculptor( PolyMesh mesh )
	{
		_mesh = mesh;
		_bvh = MeshBVH.Build( mesh );
		_neighbors = mesh.BuildVertexEdges();
	}

	public PolyMesh Mesh => _mesh;

	/// <summary>A stroke along <paramref name="curve"/> (points near the surface), snapped onto it.</summary>
	public void Stroke( BrushKind kind, IReadOnlyList<Vec3> curve, float radius, float strength, BrushFalloff falloff = BrushFalloff.Smooth, float spacing = 0.2f )
	{
		if ( curve.Count == 0 ) return;
		var points = Resample( curve, MathF.Max( radius * spacing, 1e-3f ) );
		var stroke = new BrushStroke { Kind = kind, Falloff = falloff };
		for ( var i = 0; i < points.Count; i++ )
		{
			var hit = _bvh.NearestSurface( _mesh, points[i], radius * 3f );
			if ( hit is not { } h ) continue;
			var dir = i + 1 < points.Count ? points[i + 1] - points[i] : i > 0 ? points[i] - points[i - 1] : Vec3.Zero;
			stroke.Samples.Add( new BrushSample( h.Point, h.Normal, radius, strength, dir ) );
		}
		Apply( stroke );
	}

	/// <summary>One dab at <paramref name="point"/> (snapped onto the surface).</summary>
	public void Dab( BrushKind kind, Vec3 point, float radius, float strength, Vec3 direction = default, BrushFalloff falloff = BrushFalloff.Smooth )
	{
		var hit = _bvh.NearestSurface( _mesh, point, radius * 3f );
		if ( hit is not { } h ) return;
		var stroke = new BrushStroke { Kind = kind, Falloff = falloff };
		stroke.Samples.Add( new BrushSample( h.Point, h.Normal, radius, strength, direction ) );
		Apply( stroke );
	}

	void Apply( BrushStroke stroke )
	{
		if ( stroke.Samples.Count == 0 ) return;
		// Frames are read from the surface as the stroke finds it, rebuilt per stroke as the editor does.
		var frames = Brush.UsesSurfaceFrames( stroke.Kind ) ? SculptFrames.Build( _mesh ) : null;
		Brush.Apply( _mesh, stroke, frames, Mask, _bvh, _neighbors );
	}

	static List<Vec3> Resample( IReadOnlyList<Vec3> curve, float step )
	{
		var outp = new List<Vec3> { curve[0] };
		var carry = 0f;
		for ( var i = 1; i < curve.Count; i++ )
		{
			var a = curve[i - 1]; var b = curve[i];
			var len = (b - a).Length;
			var t = step - carry;
			while ( t <= len )
			{
				outp.Add( Vec3.Lerp( a, b, t / MathF.Max( len, 1e-9f ) ) );
				t += step;
			}
			carry = len - (t - step);
		}
		if ( (outp[^1] - curve[^1]).Length > step * 0.3f ) outp.Add( curve[^1] );
		return outp;
	}
}
