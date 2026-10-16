using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Effigy.Render;

/// <summary>A rectangular light panel painted into a <see cref="ShadeEnvironment"/>: a softbox, a strip,
/// a bounce card. Glossy surfaces mirror it; rough ones pick up its light.</summary>
public sealed class ShadeSoftbox
{
	/// <summary>Toward the panel's centre, in the environment's frame (see <see cref="ShadeEnvironment.CameraRelative"/>).</summary>
	public Vec3 Direction;
	/// <summary>Angular half-width and half-height, radians. A tall thin panel (0.06 × 0.7) is a rim strip.</summary>
	public float Width = 0.35f, Height = 0.28f;
	/// <summary>Radiance, linear. A panel this bright shows as a white shape in chrome even at the dark end of the tonemap.</summary>
	public float Intensity = 4f;
	public Vec3 Colour = Vec3.One;
	/// <summary>How much of each half-extent is a soft fall-off at the edge, 0..1.</summary>
	public float Softness = 0.25f;
	/// <summary>Turn about <see cref="Direction"/>, radians.</summary>
	public float Roll;

	public ShadeSoftbox( Vec3 direction, float width, float height, float intensity, Vec3? colour = null )
	{
		Direction = direction.Normal; Width = width; Height = height; Intensity = intensity;
		if ( colour is Vec3 c ) Colour = c;
	}
}

/// <summary>
/// A procedural HDR environment for image-based lighting: a dark studio gradient, a bright horizon line
/// and any number of softboxes. <see cref="ShadedRender"/> pre-filters it once (split-sum GGX, cached
/// until a field changes) so metals, clear coat and every roughness in between reflect a believable
/// world rather than a two-colour gradient, and rough surfaces take its diffuse light through
/// spherical harmonics. It is not drawn as the backdrop: the scene's background stays as set.
/// </summary>
public sealed class ShadeEnvironment
{
	public Vec3 Zenith = new( 0.03f, 0.033f, 0.04f ), Horizon = new( 0.06f, 0.064f, 0.075f ), Ground = new( 0.014f, 0.014f, 0.016f );
	/// <summary>A thin bright line along the horizon, the floor-to-wall seam of a studio cyclorama.</summary>
	public Vec3 HorizonGlow = new( 0.35f, 0.37f, 0.42f );
	/// <summary>Half-height of the horizon line, as a sine of elevation.</summary>
	public float HorizonWidth = 0.035f;
	public readonly List<ShadeSoftbox> Softboxes = new();

	/// <summary>
	/// True (the default): x points from the subject toward the camera, y to the camera's right, z up, so
	/// every view of a turnaround is lit the same way, like the studio lights of <see cref="ShadedRender.Sheet"/>.
	/// False: the frame is world space.
	/// </summary>
	public bool CameraRelative = true;
	/// <summary>Turns the whole environment about z, degrees.</summary>
	public float Yaw;
	/// <summary>Scales the light the environment adds to rough (diffuse) shading.</summary>
	public float Diffuse = 0.6f;
	/// <summary>Scales its reflections.</summary>
	public float Specular = 1f;

	/// <summary>A dark product-shot studio: a big warm key front-right, a cool fill left, a top light, two
	/// tall rim strips behind, a bright horizon seam.</summary>
	public static ShadeEnvironment Studio()
	{
		var e = new ShadeEnvironment();
		e.Softboxes.Add( new ShadeSoftbox( new Vec3( 0.62f, 0.62f, 0.55f ), 0.36f, 0.28f, 6f, new Vec3( 1f, 0.95f, 0.88f ) ) );
		e.Softboxes.Add( new ShadeSoftbox( new Vec3( 0.75f, -0.62f, 0.12f ), 0.5f, 0.4f, 0.9f, new Vec3( 0.82f, 0.9f, 1.05f ) ) );
		e.Softboxes.Add( new ShadeSoftbox( new Vec3( 0.12f, 0.05f, 1f ), 0.55f, 0.22f, 2.4f ) );
		e.Softboxes.Add( new ShadeSoftbox( new Vec3( -0.55f, -0.8f, 0.18f ), 0.055f, 0.75f, 9f, new Vec3( 0.85f, 0.93f, 1.05f ) ) { Softness = 0.4f } );
		e.Softboxes.Add( new ShadeSoftbox( new Vec3( -0.55f, 0.8f, 0.18f ), 0.055f, 0.75f, 8f ) { Softness = 0.4f } );
		e.Softboxes.Add( new ShadeSoftbox( new Vec3( 0.95f, 0.05f, -0.12f ), 0.7f, 0.06f, 0.6f ) { Softness = 0.6f } );
		return e;
	}

	/// <summary>Radiance arriving from direction <paramref name="d"/> (unit, environment frame).</summary>
	public Vec3 Radiance( Vec3 d ) => Radiance( d, Prepare() );

	internal Vec3 Radiance( Vec3 d, Prepared[] panels )
	{
		var z = d.z;
		var sky = z >= 0f ? Vec3.Lerp( Horizon, Zenith, MathF.Sqrt( z ) ) : Vec3.Lerp( Horizon, Ground, MathF.Min( 1f, -z * 6f ) );
		var hz = z / MathF.Max( HorizonWidth, 1e-4f );
		var c = sky + HorizonGlow * MathF.Exp( -hz * hz );
		foreach ( var b in panels ) c += b.At( d );
		return c;
	}

	/// <summary>A softbox with its frame and extents worked out once.</summary>
	internal readonly struct Prepared
	{
		readonly Vec3 _dir, _right, _up, _colour; readonly float _tw, _th, _soft;
		public Prepared( ShadeSoftbox b )
		{
			_dir = b.Direction.Normal;
			_right = Vec3.Cross( _dir, MathF.Abs( _dir.z ) < 0.98f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ) ).Normal;
			_up = Vec3.Cross( _right, _dir );
			if ( b.Roll != 0f )
			{
				var cr = MathF.Cos( b.Roll ); var sr = MathF.Sin( b.Roll );
				(_right, _up) = (_right * cr + _up * sr, _up * cr - _right * sr);
			}
			_tw = MathF.Tan( Math.Clamp( b.Width, 1e-3f, 1.4f ) ); _th = MathF.Tan( Math.Clamp( b.Height, 1e-3f, 1.4f ) );
			_soft = Math.Clamp( b.Softness, 0.01f, 1f );
			_colour = b.Colour * b.Intensity;
		}

		public Vec3 At( Vec3 d )
		{
			var cosA = Vec3.Dot( d, _dir );
			if ( cosA <= 0.05f ) return Vec3.Zero;
			var x = MathF.Abs( Vec3.Dot( d, _right ) / cosA ); var y = MathF.Abs( Vec3.Dot( d, _up ) / cosA );
			if ( x >= _tw || y >= _th ) return Vec3.Zero;
			var mask = Edge( _tw, _tw * (1f - _soft), x ) * Edge( _th, _th * (1f - _soft), y );
			// A diffuser is brighter in the middle: a gentle hotspot keeps reflections from reading as flat stickers.
			var r2 = x * x / (_tw * _tw) + y * y / (_th * _th);
			return _colour * (mask * (1.1f - 0.25f * r2));
		}
	}

	internal Prepared[] Prepare()
	{
		var p = new Prepared[Softboxes.Count];
		for ( var i = 0; i < p.Length; i++ ) p[i] = new Prepared( Softboxes[i] );
		return p;
	}

	static float Edge( float a, float b, float x ) { var t = Math.Clamp( (x - a) / (b - a), 0f, 1f ); return t * t * (3f - 2f * t); }

	internal ulong Signature()
	{
		var h = 1469598103934665603UL;
		void F( float f ) { h = (h ^ (uint)BitConverter.SingleToInt32Bits( f )) * 1099511628211UL; }
		void V( Vec3 v ) { F( v.x ); F( v.y ); F( v.z ); }
		V( Zenith ); V( Horizon ); V( Ground ); V( HorizonGlow ); F( HorizonWidth );
		foreach ( var b in Softboxes ) { V( b.Direction ); F( b.Width ); F( b.Height ); F( b.Intensity ); V( b.Colour ); F( b.Softness ); F( b.Roll ); }
		return h;
	}

	EnvBake _bake; ulong _bakeSig;
	readonly object _lock = new();

	/// <summary>The pre-filtered radiance and irradiance, rebuilt only when a field changed.</summary>
	internal EnvBake Baked()
	{
		var sig = Signature();
		lock ( _lock )
		{
			if ( _bake is null || _bakeSig != sig ) { _bake = new EnvBake( this ); _bakeSig = sig; }
			return _bake;
		}
	}
}

/// <summary>
/// The environment ready for shading: a lat-long mip chain, GGX pre-filtered copies at fixed roughnesses
/// (the first half of the split sum), and nine spherical-harmonic coefficients for diffuse.
/// </summary>
sealed class EnvBake
{
	/// <summary>Roughnesses (the renderer's perceptual roughness, GGX alpha = r²) of the pre-filtered levels.
	/// Level 0 is not baked: it is the analytic environment, so chrome stays crisp.</summary>
	static readonly float[] Levels = { 0f, 0.05f, 0.1f, 0.17f, 0.25f, 0.35f, 0.5f, 0.7f, 1f };
	readonly ShadeEnvironment _env;
	readonly ShadeEnvironment.Prepared[] _panels;
	readonly LatLong[] _filtered;
	readonly Vec3[] _sh = new Vec3[9];

	public EnvBake( ShadeEnvironment env )
	{
		_env = env;
		_panels = env.Prepare();
		// Base image with 2×2 supersampling so thin strips do not alias into the mips.
		var baseImg = new LatLong( 512, 256 );
		Parallel.For( 0, baseImg.H, y =>
		{
			for ( var x = 0; x < baseImg.W; x++ )
			{
				var acc = Vec3.Zero;
				for ( var j = 0; j < 2; j++ )
					for ( var i = 0; i < 2; i++ )
						acc += env.Radiance( LatLong.Dir( (x + 0.25f + 0.5f * i) / baseImg.W, (y + 0.25f + 0.5f * j) / baseImg.H ), _panels );
				baseImg.Px[y * baseImg.W + x] = acc * 0.25f;
			}
		} );
		var mips = new List<LatLong> { baseImg };
		while ( mips[^1].W > 8 ) mips.Add( mips[^1].Half() );

		_filtered = new LatLong[Levels.Length];
		for ( var l = 1; l < Levels.Length; l++ )
		{
			var r = Levels[l];
			var w = r <= 0.1f ? 256 : r <= 0.25f ? 128 : r <= 0.5f ? 64 : 32;
			_filtered[l] = Prefilter( mips, r, w );
		}

		// Diffuse: project the 64×32 mip onto SH9, weighting texels by solid angle.
		var src = mips.Find( m => m.W == 64 ) ?? mips[^1];
		var dTheta = MathF.PI / src.H; var dPhi = 2f * MathF.PI / src.W;
		for ( var y = 0; y < src.H; y++ )
		{
			var v = (y + 0.5f) / src.H;
			var dw = MathF.Sin( v * MathF.PI ) * dTheta * dPhi;
			for ( var x = 0; x < src.W; x++ )
			{
				var d = LatLong.Dir( (x + 0.5f) / src.W, v );
				var c = src.Px[y * src.W + x] * dw;
				Span<float> b = stackalloc float[9];
				ShBasis( d, b );
				for ( var k = 0; k < 9; k++ ) _sh[k] += c * b[k];
			}
		}
		// Convolve with the clamped cosine (Ramamoorthi & Hanrahan) and fold in the 1/π of a Lambert BRDF.
		float[] band = { MathF.PI, 2.094395f, 2.094395f, 2.094395f, 0.785398f, 0.785398f, 0.785398f, 0.785398f, 0.785398f };
		for ( var k = 0; k < 9; k++ ) _sh[k] *= band[k] / MathF.PI;
	}

	/// <summary>Light a Lambert surface facing <paramref name="n"/> receives, already divided by π (multiply by albedo).</summary>
	public Vec3 Diffuse( Vec3 n )
	{
		Span<float> b = stackalloc float[9];
		ShBasis( n, b );
		var c = Vec3.Zero;
		for ( var k = 0; k < 9; k++ ) c += _sh[k] * b[k];
		return new Vec3( MathF.Max( 0f, c.x ), MathF.Max( 0f, c.y ), MathF.Max( 0f, c.z ) );
	}

	/// <summary>Pre-filtered radiance along reflection <paramref name="r"/> for a surface of roughness <paramref name="rough"/>.</summary>
	public Vec3 Specular( Vec3 r, float rough )
	{
		rough = Math.Clamp( rough, 0f, 1f );
		var hi = 1;
		while ( hi < Levels.Length - 1 && Levels[hi] < rough ) hi++;
		var lo = hi - 1;
		var t = Math.Clamp( (rough - Levels[lo]) / (Levels[hi] - Levels[lo]), 0f, 1f );
		var a = lo == 0 ? _env.Radiance( r, _panels ) : _filtered[lo].Sample( r );
		if ( t <= 0f ) return a;
		return Vec3.Lerp( a, _filtered[hi].Sample( r ), t );
	}

	static LatLong Prefilter( List<LatLong> mips, float rough, int w )
	{
		const int samples = 96;
		var a = MathF.Max( rough * rough, 1e-4f );
		var a2 = a * a;
		var outImg = new LatLong( w, w / 2 );
		var texelSolid = 4f * MathF.PI / (mips[0].W * mips[0].H);
		// Hammersley half-vectors in tangent space, with each sample's mip from its pdf (filtered importance sampling).
		var hs = new (float X, float Y, float Z, float Lod)[samples];
		for ( var s = 0; s < samples; s++ )
		{
			var u = (s + 0.5f) / samples; var v = RadicalInverse( (uint)s );
			var phi = 2f * MathF.PI * v;
			var cosT = MathF.Sqrt( (1f - u) / (1f + (a2 - 1f) * u) );
			var sinT = MathF.Sqrt( 1f - cosT * cosT );
			var dd = cosT * cosT * (a2 - 1f) + 1f;
			var D = a2 / (MathF.PI * dd * dd);
			var pdf = D / 4f; // N = V = R: pdf(l) = D·nh / (4·vh) with nh = vh
			var solid = 1f / (samples * pdf + 1e-6f);
			var lod = MathF.Max( 0f, 0.5f * MathF.Log2( solid / texelSolid ) + 1f );
			hs[s] = (sinT * MathF.Cos( phi ), sinT * MathF.Sin( phi ), cosT, lod);
		}
		Parallel.For( 0, outImg.H, y =>
		{
			for ( var x = 0; x < outImg.W; x++ )
			{
				var n = LatLong.Dir( (x + 0.5f) / outImg.W, (y + 0.5f) / outImg.H );
				var t = Vec3.Cross( MathF.Abs( n.z ) < 0.999f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ), n ).Normal;
				var b = Vec3.Cross( n, t );
				var acc = Vec3.Zero; var wsum = 0f;
				foreach ( var (hx, hy, hz, lod) in hs )
				{
					var h = t * hx + b * hy + n * hz;
					var l = h * (2f * hz) - n;
					var nl = Vec3.Dot( n, l );
					if ( nl <= 0f ) continue;
					acc += SampleMips( mips, l, lod ) * nl;
					wsum += nl;
				}
				outImg.Px[y * outImg.W + x] = wsum > 0f ? acc / wsum : Vec3.Zero;
			}
		} );
		return outImg;
	}

	static Vec3 SampleMips( List<LatLong> mips, Vec3 d, float lod )
	{
		lod = Math.Clamp( lod, 0f, mips.Count - 1 );
		var i = (int)lod; var f = lod - i;
		var c = mips[i].Sample( d );
		return f > 0f && i + 1 < mips.Count ? Vec3.Lerp( c, mips[i + 1].Sample( d ), f ) : c;
	}

	static float RadicalInverse( uint b )
	{
		b = (b << 16) | (b >> 16);
		b = ((b & 0x55555555u) << 1) | ((b & 0xAAAAAAAAu) >> 1);
		b = ((b & 0x33333333u) << 2) | ((b & 0xCCCCCCCCu) >> 2);
		b = ((b & 0x0F0F0F0Fu) << 4) | ((b & 0xF0F0F0F0u) >> 4);
		b = ((b & 0x00FF00FFu) << 8) | ((b & 0xFF00FF00u) >> 8);
		return b * 2.3283064365386963e-10f;
	}

	static void ShBasis( Vec3 d, Span<float> b )
	{
		b[0] = 0.282095f;
		b[1] = 0.488603f * d.y; b[2] = 0.488603f * d.z; b[3] = 0.488603f * d.x;
		b[4] = 1.092548f * d.x * d.y; b[5] = 1.092548f * d.y * d.z; b[6] = 0.315392f * (3f * d.z * d.z - 1f);
		b[7] = 1.092548f * d.x * d.z; b[8] = 0.546274f * (d.x * d.x - d.y * d.y);
	}

	/// <summary>An equirectangular image: u round the horizon from +x, v from the zenith down.</summary>
	internal sealed class LatLong
	{
		public readonly int W, H; public readonly Vec3[] Px;
		public LatLong( int w, int h ) { W = w; H = h; Px = new Vec3[w * h]; }

		public static Vec3 Dir( float u, float v )
		{
			var phi = (u - 0.5f) * 2f * MathF.PI; var theta = v * MathF.PI;
			var s = MathF.Sin( theta );
			return new Vec3( s * MathF.Cos( phi ), s * MathF.Sin( phi ), MathF.Cos( theta ) );
		}

		public Vec3 Sample( Vec3 d )
		{
			var u = MathF.Atan2( d.y, d.x ) / (2f * MathF.PI) + 0.5f;
			var v = MathF.Acos( Math.Clamp( d.z, -1f, 1f ) ) / MathF.PI;
			var fx = u * W - 0.5f; var fy = v * H - 0.5f;
			var x0 = (int)MathF.Floor( fx ); var y0 = (int)MathF.Floor( fy );
			var tx = fx - x0; var ty = fy - y0;
			Vec3 At( int x, int y ) { x = ((x % W) + W) % W; y = Math.Clamp( y, 0, H - 1 ); return Px[y * W + x]; }
			return Vec3.Lerp( Vec3.Lerp( At( x0, y0 ), At( x0 + 1, y0 ), tx ), Vec3.Lerp( At( x0, y0 + 1 ), At( x0 + 1, y0 + 1 ), tx ), ty );
		}

		public LatLong Half()
		{
			var h = new LatLong( W / 2, H / 2 );
			for ( var y = 0; y < h.H; y++ )
				for ( var x = 0; x < h.W; x++ )
					h.Px[y * h.W + x] = (Px[2 * y * W + 2 * x] + Px[2 * y * W + 2 * x + 1] + Px[(2 * y + 1) * W + 2 * x] + Px[(2 * y + 1) * W + 2 * x + 1]) * 0.25f;
			return h;
		}
	}
}

/// <summary>The second half of the split sum: GGX directional albedo as scale (A) and bias (B) on F0,
/// tabulated over n·v and roughness. Also gives the multiple-scattering energy a single-bounce GGX loses,
/// which is what keeps rough metals from going dark.</summary>
static class BrdfLut
{
	const int N = 32;
	static readonly (float A, float B)[] Table = Build();

	static (float, float)[] Build()
	{
		const int samples = 256;
		var t = new (float, float)[N * N];
		Parallel.For( 0, N, ri =>
		{
			var rough = (ri + 0.5f) / N;
			var a = MathF.Max( rough * rough, 1e-4f ); var a2 = a * a;
			var k = a * 0.5f;
			for ( var vi = 0; vi < N; vi++ )
			{
				var nv = (vi + 0.5f) / N;
				var v = new Vec3( MathF.Sqrt( 1f - nv * nv ), 0f, nv );
				float A = 0, B = 0;
				for ( var s = 0; s < samples; s++ )
				{
					var u = (s + 0.5f) / samples; var w = Radical( (uint)s );
					var phi = 2f * MathF.PI * w;
					var cosT = MathF.Sqrt( (1f - u) / (1f + (a2 - 1f) * u) );
					var sinT = MathF.Sqrt( 1f - cosT * cosT );
					var h = new Vec3( sinT * MathF.Cos( phi ), sinT * MathF.Sin( phi ), cosT );
					var vh = Vec3.Dot( v, h );
					var l = h * (2f * vh) - v;
					var nl = l.z; var nh = h.z;
					if ( nl <= 0f ) continue;
					vh = MathF.Max( vh, 0f );
					var G = nl / (nl * (1f - k) + k) * (nv / (nv * (1f - k) + k));
					var gv = G * vh / (nh * nv);
					var fc = MathF.Pow( 1f - vh, 5f );
					A += (1f - fc) * gv; B += fc * gv;
				}
				t[ri * N + vi] = (A / samples, B / samples);
			}
		} );
		return t;
	}

	static float Radical( uint b )
	{
		b = (b << 16) | (b >> 16);
		b = ((b & 0x55555555u) << 1) | ((b & 0xAAAAAAAAu) >> 1);
		b = ((b & 0x33333333u) << 2) | ((b & 0xCCCCCCCCu) >> 2);
		b = ((b & 0x0F0F0F0Fu) << 4) | ((b & 0xF0F0F0F0u) >> 4);
		b = ((b & 0x00FF00FFu) << 8) | ((b & 0xFF00FF00u) >> 8);
		return b * 2.3283064365386963e-10f;
	}

	public static (float A, float B) Get( float nv, float rough )
	{
		var fx = Math.Clamp( nv * N - 0.5f, 0f, N - 1.001f ); var fy = Math.Clamp( rough * N - 0.5f, 0f, N - 1.001f );
		var x0 = (int)fx; var y0 = (int)fy; var tx = fx - x0; var ty = fy - y0;
		var p00 = Table[y0 * N + x0]; var p10 = Table[y0 * N + x0 + 1]; var p01 = Table[(y0 + 1) * N + x0]; var p11 = Table[(y0 + 1) * N + x0 + 1];
		float L( float a, float b, float c, float d ) => (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
		return (L( p00.A, p10.A, p01.A, p11.A ), L( p00.B, p10.B, p01.B, p11.B ));
	}
}
