using System;
using System.Collections.Generic;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// One side of the face's upper-face muscles, as FACS action units (0 = rest, 1 = a strong
/// expression; past 1 is beyond what a face does). Clay has no eyebrow hair, so a brow lift alone
/// reads as nothing: each unit also lays the furrows its muscle makes, which is what reads.
/// </summary>
public sealed class BrowUnits
{
	/// <summary>AU1, frontalis pars medialis: inner brow up, forehead bunched over it.</summary>
	public float InnerBrowUp;
	/// <summary>AU2, frontalis pars lateralis: outer brow up.</summary>
	public float OuterBrowUp;
	/// <summary>AU4, corrugator + procerus: brow pulled down and in, glabella bunched.</summary>
	public float BrowKnit;
	/// <summary>AU5, levator palpebrae: upper lid off the iris.</summary>
	public float LidUp;
	/// <summary>AU43 partial: upper lid dropped over the iris (heavy, vacant).</summary>
	public float LidDrop;
	/// <summary>AU7: lower lid pushed up.</summary>
	public float LowerLidUp;
	/// <summary>AU6, orbicularis oculi: cheek up into the eye, crow's feet.</summary>
	public float CheekUp;
	/// <summary>AU9: nose wrinkled, alae up, bridge creased.</summary>
	public float NoseWrinkle;
	/// <summary>AU38: nostril flared.</summary>
	public float NostrilFlare;
	/// <summary>Gravity: the whole brow slides down off the frontal bone, the outer end most.</summary>
	public float BrowSag;
}

public static partial class CreatureGen
{
	/// <summary>
	/// Upper-face expression on the head at rest (head inches: x forward, y left, z up). Both sides,
	/// each from its own units; <see cref="ExpressionDelta"/> moves a cage vertex, and
	/// <see cref="ExpressionFurrows"/> gives the folds those moves make, at rest on the skin.
	/// Landmarks measured on the base: brow ridge z 1.0–1.25 (x 4.13 at y 1.0, 3.64 at y 2.0), upper
	/// lid z 0.5 at y 1.25, lower lid z −0.25, alae y ±0.75 z −1.25, forehead to z 3.5.
	/// </summary>
	static Vec3 ExpressionDelta( Vec3 p, BrowUnits plus, BrowUnits minus )
	{
		var front = Smoothstep( 2.4f, 3.3f, p.x );
		if ( front <= 0f ) return Vec3.Zero;
		var d = Vec3.Zero;
		foreach ( var sy in new[] { 1f, -1f } )
		{
			var u = sy > 0 ? plus : minus;
			// On this side's own axes: y' runs out from the midline. Each side owns its half and
			// shares the midline half and half.
			var q = new Vec3( p.x, p.y * sy, p.z );
			var share = Smoothstep( -0.3f, 0.3f, q.y );
			if ( share <= 0f ) continue;
			var s = Vec3.Zero;
			static float E( Vec3 q, Vec3 c, Vec3 r )
			{
				var m = q - c;
				var t = (m.x / r.x) * (m.x / r.x) + (m.y / r.y) * (m.y / r.y) + (m.z / r.z) * (m.z / r.z);
				return t >= 1f ? 0f : (1f - t) * (1f - t);
			}
			var aboveEye = Smoothstep( 0.12f, 0.32f, q.z );
			var belowEye = Smoothstep( 0.05f, -0.12f, q.z );

			if ( u.InnerBrowUp != 0f )
			{
				s += new Vec3( 0.02f, -0.05f, 0.5f ) * (u.InnerBrowUp * E( q, new Vec3( 4.15f, 0.55f, 1.15f ), new Vec3( 1.2f, 0.75f, 0.8f ) ));
				// The forehead above moves less: it bunches between, where the furrows go.
				s += new Vec3( 0.03f, 0, 0.18f ) * (u.InnerBrowUp * E( q, new Vec3( 4.05f, 0.5f, 2.1f ), new Vec3( 1.2f, 0.9f, 0.9f ) ));
			}
			if ( u.OuterBrowUp != 0f )
				s += new Vec3( 0, 0, 0.4f ) * (u.OuterBrowUp * E( q, new Vec3( 3.75f, 1.8f, 1.15f ), new Vec3( 1.2f, 0.75f, 0.75f ) ));
			if ( u.BrowKnit != 0f )
			{
				s += new Vec3( 0.06f, -0.25f, -0.22f ) * (u.BrowKnit * E( q, new Vec3( 4.2f, 0.6f, 1.0f ), new Vec3( 1.0f, 0.65f, 0.65f ) ));
				// The corrugator bunch over the inner brow, and the glabella between them.
				s += new Vec3( 0.13f, 0, 0.02f ) * (u.BrowKnit * E( q, new Vec3( 4.2f, 0.45f, 1.05f ), new Vec3( 0.5f, 0.38f, 0.32f ) ));
				s += new Vec3( 0.1f, 0, 0 ) * (u.BrowKnit * E( q, new Vec3( 4.25f, 0, 0.75f ), new Vec3( 0.5f, 0.32f, 0.42f ) ));
			}
			if ( u.LidUp != 0f )
				s += new Vec3( -0.05f, 0, 0.2f ) * (u.LidUp * aboveEye * E( q, new Vec3( 3.88f, 1.25f, 0.45f ), new Vec3( 0.6f, 0.68f, 0.38f ) ));
			if ( u.LidDrop != 0f )
				s += new Vec3( 0.05f, 0, -0.24f ) * (u.LidDrop * aboveEye * E( q, new Vec3( 3.88f, 1.25f, 0.45f ), new Vec3( 0.6f, 0.68f, 0.38f ) ));
			if ( u.LowerLidUp != 0f )
				s += new Vec3( 0.02f, 0, 0.13f ) * (u.LowerLidUp * belowEye * E( q, new Vec3( 3.78f, 1.25f, -0.22f ), new Vec3( 0.55f, 0.65f, 0.35f ) ));
			if ( u.CheekUp != 0f )
				s += new Vec3( 0.09f, 0.02f, 0.2f ) * (u.CheekUp * belowEye * E( q, new Vec3( 3.85f, 1.6f, -0.75f ), new Vec3( 0.9f, 0.85f, 0.75f ) ));
			if ( u.NoseWrinkle != 0f )
			{
				s += new Vec3( 0.02f, 0, 0.12f ) * (u.NoseWrinkle * E( q, new Vec3( 4.4f, 0.45f, -0.35f ), new Vec3( 0.55f, 0.42f, 0.5f ) ));
				s += new Vec3( 0, 0.03f, 0.16f ) * (u.NoseWrinkle * E( q, new Vec3( 4.5f, 0.75f, -1.3f ), new Vec3( 0.45f, 0.42f, 0.38f ) ));
				s += new Vec3( 0, -0.05f, -0.1f ) * (u.NoseWrinkle * E( q, new Vec3( 4.2f, 0.5f, 0.95f ), new Vec3( 0.8f, 0.5f, 0.45f ) ));
			}
			if ( u.NostrilFlare != 0f )
				s += new Vec3( 0.01f, 0.2f, 0.05f ) * (u.NostrilFlare * E( q, new Vec3( 4.45f, 0.82f, -1.3f ), new Vec3( 0.42f, 0.38f, 0.38f ) ));
			if ( u.BrowSag != 0f )
			{
				s += new Vec3( 0.02f, 0, -0.24f ) * (u.BrowSag * E( q, new Vec3( 4.0f, 1.25f, 1.1f ), new Vec3( 1.4f, 1.25f, 0.85f ) ));
				s += new Vec3( 0, 0.02f, -0.2f ) * (u.BrowSag * E( q, new Vec3( 3.55f, 2.0f, 1.05f ), new Vec3( 0.9f, 0.65f, 0.65f ) ));
				// The brow rolls forward over the upper lid as it slides: an overhang, not a shelf.
				s += new Vec3( 0.15f, 0, -0.05f ) * (u.BrowSag * aboveEye * E( q, new Vec3( 4.0f, 1.3f, 0.85f ), new Vec3( 0.6f, 0.75f, 0.4f ) ));
			}
			d += new Vec3( s.x, s.y * sy, s.z ) * share;
		}
		return d * front;
	}

	/// <summary>
	/// The furrows the units make, as rest-space curves on the skin. <paramref name="onSkin"/>
	/// snaps a (y, z) to the face's surface at rest.
	/// </summary>
	static List<Fold> ExpressionFurrows( BrowUnits plus, BrowUnits minus, Func<float, float, Vec3> onSkin )
	{
		var folds = new List<Fold>();
		Vec3[] Curve( Func<float, (float y, float z)> at, int n = 7 )
		{
			var pts = new Vec3[n];
			for ( var i = 0; i < n; i++ ) { var (y, z) = at( i / (float)(n - 1) ); pts[i] = onSkin( y, z ); }
			return pts;
		}

		// Every furrow is a shallow trough (no raised worm, no shelf) that tapers to nothing at its
		// ends, with the flesh either side pinched in toward it so it reads as skin bunched by the
		// muscle rather than a carved line. Heights are at rest: the brows' own lift carries them up
		// to where they belong (frontalis 1.8-2.5, corrugator 0.9-1.6, judged on the posed face).
		const float Lip = 0.012f;

		// Frontalis: two troughs across the forehead, symmetric, bowed up in the middle when the
		// inner brows lead (the agony brow), each side's depth from that side's lift.
		float Avg( Func<BrowUnits, float> f ) => (f( plus ) + f( minus )) * 0.5f;
		var inner = Avg( u => u.InnerBrowUp );
		var outer = Avg( u => u.OuterBrowUp );
		var lift = MathF.Max( inner, outer );
		if ( lift > 0.05f )
		{
			var bow = 0.12f * inner / MathF.Max( inner + outer, 1e-3f );
			foreach ( var z0 in new[] { 2.2f, 2.75f } )
				folds.Add( new Fold( Curve( t =>
				{
					var y = (t * 2f - 1f) * 1.75f;
					return (y, z0 + bow * MathF.Cos( MathF.PI * 0.5f * y / 1.75f ) - 0.05f * y * y);
				}, 11 ), 0.05f * MathF.Min( lift, 1.2f ), 0.3f, 0f, 1, 0.1f ) );
		}

		var knit = Avg( u => u.BrowKnit );
		// Glabella: one vertical trough between the corrugator mounds (ExpressionDelta raises them).
		if ( knit > 0.05f )
			folds.Add( new Fold( Curve( t => (0f, 0.8f + 0.6f * t), 5 ), 0.05f * MathF.Min( knit, 1f ), 0.22f, 0f, 1, 0.1f ) );

		foreach ( var sy in new[] { 1f, -1f } )
		{
			var u = sy > 0 ? plus : minus;
			// Nose wrinkle: short creases across the bridge.
			if ( u.NoseWrinkle > 0.05f )
				foreach ( var z in new[] { 0.3f, 0.02f } )
					folds.Add( new Fold( Curve( t => (sy * (0.04f + 0.34f * t), z - 0.08f * t), 4 ), 0.035f * u.NoseWrinkle, 0.2f, Lip, 1, 0.25f ) );
			// Crow's feet: short lines fanning from the outer eye corner.
			if ( u.CheekUp > 0.05f )
				for ( var k = 0; k < 3; k++ )
				{
					var a = (-25f + 25f * k) * MathF.PI / 180f;
					folds.Add( new Fold( Curve( t => (sy * (1.95f + (0.15f + 0.55f * t) * MathF.Cos( a )), 0.15f + (0.15f + 0.55f * t) * MathF.Sin( a )), 4 ), 0.035f * u.CheekUp, 0.2f, Lip, 1, 0.25f ) );
				}
			// Under the eye when the cheek rises: the lid-cheek fold.
			if ( u.CheekUp > 0.05f )
				folds.Add( new Fold( Curve( t => (sy * (0.75f + 1.05f * t), -0.5f - 0.12f * MathF.Sin( MathF.PI * t )), 6 ), 0.045f * u.CheekUp, 0.28f, 0.03f * u.CheekUp, -1, 0.25f ) );
			// The upper lid crease deepens as the lid lifts into it. A sagging brow rolls over the lid
			// instead (BrowSag in ExpressionDelta): a crease there reads as a cut shelf.
			var crease = MathF.Max( u.LidUp, u.LidDrop );
			if ( crease > 0.05f && u.BrowSag <= 0f )
				folds.Add( new Fold( Curve( t => (sy * (0.78f + 1.0f * t), 0.62f + 0.12f * MathF.Sin( MathF.PI * t )), 6 ), 0.04f * crease, 0.24f, Lip, 1, 0.2f ) );
		}
		return folds;
	}
}
