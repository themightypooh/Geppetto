using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Geometric crossings turned into shared vertices, on a COPY of the sketch.
///
/// Profile finding walks an integer graph: coincidence is identity, and two loops that overlap
/// in the plane without sharing a point index are two separate regions even when they plainly
/// share an area. That is the right editing model — dragging one rectangle must not invent
/// vertices on the other — but it is the wrong region model. The lens between two overlapping
/// circles is a face someone can point a tool at, and it is not a cycle of the unsplit graph.
///
/// This builds the overlay the walk needs without touching the sketch the user is editing. Line,
/// arc and circle crossings use <see cref="SketchIntersect"/> so the split points are the same
/// ones trim already believes in. Endpoint-on-endpoint touches are left alone: two rectangles
/// that merely kiss at a corner must not become a bowtie.
/// </summary>
public static class SketchArrangement
{
	const float Eps = 1e-5f;

	/// <summary>
	/// A sketch whose crossing curves share a vertex at each crossing, or <paramref name="sketch"/>
	/// itself when nothing crosses. Never mutates the input.
	/// </summary>
	public static Sketch ImprintCrossings( Sketch sketch )
	{
		if ( sketch is null || !HasProperCrossing( sketch ) )
			return sketch;

		var working = sketch.Clone();
		SplitAtCrossings( working );
		return working;
	}

	/// <summary>
	/// Split crossing curves on the sketch itself. Profile finding does this on a copy so dragging
	/// one rectangle cannot invent vertices on another. The line tool does not call this: splitting
	/// replaces the curve, and Horizontal/Vertical constraints stored against its id would then
	/// point at nothing. Returns whether anything was split.
	/// </summary>
	public static bool SplitLive( Sketch sketch )
	{
		if ( sketch is null || !HasProperCrossing( sketch ) )
			return false;

		SplitAtCrossings( sketch );
		return true;
	}

	/// <summary>
	/// Whether two sketch planes are the same plane in space — parallel, and the same offset along
	/// the normal. In-plane origin and axes may differ; profile finding projects through world.
	/// Opposite normals still count: the underside of a plate is the same plane as the top, flipped.
	/// </summary>
	public static bool Coplanar( SketchPlane a, SketchPlane b )
	{
		if ( a is null || b is null )
			return false;

		if ( MathF.Abs( MathF.Abs( Vec3.Dot( a.Normal, b.Normal ) ) - 1f ) > 1e-4f )
			return false;

		return MathF.Abs( Vec3.Dot( b.Origin - a.Origin, a.Normal ) ) < 1e-4f;
	}

	/// <summary>
	/// Guest sketches laid into the host's plane coordinates, construction dropped. The host is
	/// not mutated: a clone carries its curves, then each coplanar guest is appended with points
	/// projected through world. Returns the host itself when nothing was added, so the caller can
	/// skip work that is just the host again.
	/// </summary>
	public static Sketch Overlay( Sketch host, IEnumerable<Sketch> guests )
	{
		if ( host is null || guests is null )
			return host;

		Sketch combined = null;

		foreach ( var guest in guests )
		{
			if ( guest is null || ReferenceEquals( guest, host ) || !Coplanar( host.Plane, guest.Plane ) )
				continue;

			combined ??= host.Clone();
			Append( combined, guest );
		}

		return combined ?? host;
	}

	static void Append( Sketch host, Sketch guest )
	{
		var map = new int[guest.Points.Count];

		for ( var i = 0; i < guest.Points.Count; i++ )
			map[i] = host.AddPoint( host.Plane.ToPlane( guest.Plane.ToWorld( guest.Points[i] ) ) );

		foreach ( var curve in guest.Curves )
		{
			if ( curve.Construction )
				continue;

			var copy = Remap( curve, map );

			if ( copy is not null )
				host.Add( copy );
		}
	}

	static SketchCurve Remap( SketchCurve curve, int[] map ) => curve switch
	{
		SketchLine line => new SketchLine( map[line.Start], map[line.End] ),
		SketchArc arc => new SketchArc( map[arc.Center], map[arc.Start], map[arc.End], arc.Clockwise ),
		SketchCircle circle => new SketchCircle( map[circle.Center], circle.Radius ),
		SketchEllipse ellipse => new SketchEllipse( map[ellipse.Center], map[ellipse.MajorPoint], ellipse.MinorRadius ),
		SketchSpline spline => new SketchSpline( spline.Points.Select( i => map[i] ).ToList(), spline.Closed ),
		_ => null
	};

	static bool HasProperCrossing( Sketch sketch )
	{
		var live = Splittable( sketch );

		for ( var i = 0; i < live.Count; i++ )
		{
			for ( var j = i + 1; j < live.Count; j++ )
			{
				foreach ( var hit in SketchIntersect.Between( sketch, live[i], live[j] ) )
				{
					if ( IsProperCrossing( live[i], live[j], hit ) )
						return true;
				}
			}
		}

		return false;
	}

	static void SplitAtCrossings( Sketch sketch )
	{
		var live = Splittable( sketch );
		var cuts = new Dictionary<SketchCurve, List<Cut>>();

		void Add( SketchCurve curve, float t, Vec2 point )
		{
			if ( !cuts.TryGetValue( curve, out var list ) )
				cuts[curve] = list = new List<Cut>();

			list.Add( new Cut( t, point ) );
		}

		for ( var i = 0; i < live.Count; i++ )
		{
			for ( var j = i + 1; j < live.Count; j++ )
			{
				foreach ( var hit in SketchIntersect.Between( sketch, live[i], live[j] ) )
				{
					if ( !IsProperCrossing( live[i], live[j], hit ) )
						continue;

					Add( live[i], hit.TA, hit.Point );
					Add( live[j], hit.TB, hit.Point );
				}
			}
		}

		foreach ( var (curve, list) in cuts.ToList() )
			SplitCurve( sketch, curve, list );
	}

	static List<SketchCurve> Splittable( Sketch sketch ) =>
		sketch.Curves.Where( c => !c.Construction && CanSplit( c ) ).ToList();

	static bool CanSplit( SketchCurve curve ) => curve is SketchLine or SketchArc or SketchCircle;

	/// <summary>
	/// A real crossing rather than two corners sitting on the same point. Circles have no
	/// endpoints — t = 0 is just the +X of the parameterisation — so a hit on a circle is always
	/// a split. Two open curves that only meet at their ends already share a vertex if they were
	/// drawn as a join, and must not gain one if they were not: that is how two rectangles
	/// touching at a corner stay two rectangles.
	/// </summary>
	static bool IsProperCrossing( SketchCurve a, SketchCurve b, CurveHit hit ) =>
		!(IsOpenEnd( a, hit.TA ) && IsOpenEnd( b, hit.TB ));

	static bool IsOpenEnd( SketchCurve curve, float t ) =>
		!curve.IsClosed && (t < Eps || t > 1f - Eps);

	static void SplitCurve( Sketch sketch, SketchCurve curve, List<Cut> cuts )
	{
		switch ( curve )
		{
			case SketchLine line:
				SplitLine( sketch, line, InteriorCuts( cuts, closed: false ) );
				break;

			case SketchArc arc:
				SplitArc( sketch, arc, InteriorCuts( cuts, closed: false ) );
				break;

			case SketchCircle circle:
				SplitCircle( sketch, circle, InteriorCuts( cuts, closed: true ) );
				break;
		}
	}

	static List<Cut> InteriorCuts( List<Cut> cuts, bool closed )
	{
		var unique = new List<Cut>();

		foreach ( var cut in cuts.OrderBy( c => c.T ) )
		{
			var t = cut.T;
			var point = cut.Point;

			if ( closed )
			{
				t -= MathF.Floor( t );

				if ( t > 1f - Eps )
					t = 0f;
			}
			else if ( t <= Eps || t >= 1f - Eps )
			{
				continue;
			}

			if ( unique.Count > 0 && SameCut( unique[^1], t, point ) )
				continue;

			unique.Add( new Cut( t, point ) );
		}

		if ( closed && unique.Count > 1 && SameCut( unique[0], unique[^1].T, unique[^1].Point ) )
			unique.RemoveAt( unique.Count - 1 );

		return unique;
	}

	static bool SameCut( Cut existing, float t, Vec2 point ) =>
		MathF.Abs( existing.T - t ) < Eps
		|| MathF.Abs( existing.T - t + 1f ) < Eps
		|| MathF.Abs( existing.T - t - 1f ) < Eps
		|| (existing.Point - point).LengthSquared < Eps * Eps;

	static void SplitLine( Sketch sketch, SketchLine line, List<Cut> cuts )
	{
		if ( cuts.Count == 0 )
			return;

		var start = line.Start;
		var end = line.End;
		var construction = line.Construction;

		sketch.Curves.Remove( line );

		var prev = start;

		foreach ( var cut in cuts )
		{
			var vertex = FindOrAdd( sketch, cut.Point );
			sketch.Add( new SketchLine( prev, vertex ) { Construction = construction } );
			prev = vertex;
		}

		sketch.Add( new SketchLine( prev, end ) { Construction = construction } );
	}

	static void SplitArc( Sketch sketch, SketchArc arc, List<Cut> cuts )
	{
		if ( cuts.Count == 0 )
			return;

		var start = arc.Start;
		var end = arc.End;
		var construction = arc.Construction;

		sketch.Curves.Remove( arc );

		var prev = start;

		foreach ( var cut in cuts )
		{
			var vertex = FindOrAdd( sketch, cut.Point );
			sketch.Add( new SketchArc( arc.Center, prev, vertex, arc.Clockwise )
			{
				Construction = construction
			} );
			prev = vertex;
		}

		sketch.Add( new SketchArc( arc.Center, prev, end, arc.Clockwise )
		{
			Construction = construction
		} );
	}

	static void SplitCircle( Sketch sketch, SketchCircle circle, List<Cut> cuts )
	{
		// A single tangent point does not split a circle into faces. Leave it as a closed curve
		// so the ordinary loop path still sees it.
		if ( cuts.Count < 2 )
			return;

		var construction = circle.Construction;

		sketch.Curves.Remove( circle );

		for ( var i = 0; i < cuts.Count; i++ )
		{
			var a = FindOrAdd( sketch, cuts[i].Point );
			var b = FindOrAdd( sketch, cuts[(i + 1) % cuts.Count].Point );
			sketch.Add( new SketchArc( circle.Center, a, b )
			{
				Construction = construction
			} );
		}
	}

	static int FindOrAdd( Sketch sketch, Vec2 point )
	{
		var merge = Eps * Eps;

		for ( var i = 0; i < sketch.Points.Count; i++ )
		{
			if ( (sketch.Points[i] - point).LengthSquared < merge )
				return i;
		}

		return sketch.AddPoint( point );
	}

	readonly struct Cut
	{
		public readonly float T;
		public readonly Vec2 Point;

		public Cut( float t, Vec2 point )
		{
			T = t;
			Point = point;
		}
	}
}
