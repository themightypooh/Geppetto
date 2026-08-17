using System;
using Sandbox;

// Own-authored (not extracted from Halo) procedural gunmetal PBR texture generator.
// Used instead of Halo's real diffuse maps -- see HaloRenderModelLoader.BuildMaterial for why
// (BC7 decode produced plausible bytes but rendered flat black through complex.shader, wasn't
// worth chasing further). Since these are generated from scratch, not Halo assets, there's no
// GPL/copyright concern with them specifically -- they still only get used on a Halo-derived
// mesh, so this stays under Editor/HaloMount with everything else regardless.
public static class ProceduralMetal
{
	static float Hash( int x, int y, int seed )
	{
		unchecked
		{
			int h = x * 374761393 + y * 668265263 + seed * 1274126177;
			h = ( h ^ ( h >> 13 ) ) * 1274126177;
			h ^= h >> 16;
			return ( h & 0x7fffffff ) / (float)0x7fffffff;
		}
	}

	static float ValueNoise( float x, float y, int seed )
	{
		int x0 = (int)MathF.Floor( x ), y0 = (int)MathF.Floor( y );
		int x1 = x0 + 1, y1 = y0 + 1;
		float tx = x - x0, ty = y - y0;

		float v00 = Hash( x0, y0, seed ), v10 = Hash( x1, y0, seed );
		float v01 = Hash( x0, y1, seed ), v11 = Hash( x1, y1, seed );

		float sx = tx * tx * ( 3 - 2 * tx );
		float sy = ty * ty * ( 3 - 2 * ty );

		float a = v00 + ( v10 - v00 ) * sx;
		float b = v01 + ( v11 - v01 ) * sx;
		return a + ( b - a ) * sy;
	}

	static float Fbm( float x, float y, int seed, int octaves )
	{
		float sum = 0, amp = 0.5f, freq = 1f, total = 0;
		for ( var i = 0; i < octaves; i++ )
		{
			sum += ValueNoise( x * freq, y * freq, seed + i * 101 ) * amp;
			total += amp;
			amp *= 0.5f;
			freq *= 2f;
		}
		return sum / total;
	}

	static float DistanceToSegment( float px, float py, float x0, float y0, float x1, float y1 )
	{
		float dx = x1 - x0, dy = y1 - y0;
		float lenSq = dx * dx + dy * dy;
		float t = lenSq > 0.0001f ? ( ( px - x0 ) * dx + ( py - y0 ) * dy ) / lenSq : 0;
		t = Math.Clamp( t, 0, 1 );
		float cx = x0 + t * dx, cy = y0 + t * dy;
		float ex = px - cx, ey = py - cy;
		return MathF.Sqrt( ex * ex + ey * ey );
	}

	public static (byte[] albedo, byte[] normal, byte[] roughness) Generate( int size, byte baseR, byte baseG, byte baseB, int seed )
	{
		var heightMap = new float[size * size];

		// A handful of random machined-panel groove lines for visual interest -- distinct from
		// pure noise, reads as "designed part" rather than "procedural blob".
		var rand = new Random( seed );
		var grooveCount = 4 + rand.Next( 3 );
		var grooves = new (float x0, float y0, float x1, float y1, float width)[grooveCount];
		for ( var i = 0; i < grooveCount; i++ )
		{
			grooves[i] = (
				(float)rand.NextDouble(), (float)rand.NextDouble(),
				(float)rand.NextDouble(), (float)rand.NextDouble(),
				0.008f + (float)rand.NextDouble() * 0.012f );
		}

		for ( var y = 0; y < size; y++ )
		{
			var v = y / (float)size;
			for ( var x = 0; x < size; x++ )
			{
				var u = x / (float)size;

				// Fine speckle + streaks stretched along V for a brushed-metal look.
				var speckle = Fbm( u * 40f, v * 40f, seed, 3 );
				var brushed = Fbm( u * 6f, v * 60f, seed + 999, 2 );
				var h = speckle * 0.35f + brushed * 0.65f;

				foreach ( var g in grooves )
				{
					var d = DistanceToSegment( u, v, g.x0, g.y0, g.x1, g.y1 );
					if ( d < g.width )
						h -= ( 1f - d / g.width ) * 0.6f;
				}

				heightMap[y * size + x] = h;
			}
		}

		var albedo = new byte[size * size * 4];
		var normal = new byte[size * size * 4];
		var roughness = new byte[size * size * 4];

		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				var i = y * size + x;
				var h = heightMap[i];
				var dst = i * 4;

				var shade = Math.Clamp( 0.8f + h * 0.5f, 0.35f, 1.25f );
				albedo[dst + 0] = (byte)Math.Clamp( baseR * shade, 0, 255 );
				albedo[dst + 1] = (byte)Math.Clamp( baseG * shade, 0, 255 );
				albedo[dst + 2] = (byte)Math.Clamp( baseB * shade, 0, 255 );
				albedo[dst + 3] = 255;

				var hL = heightMap[y * size + Math.Max( x - 1, 0 )];
				var hR = heightMap[y * size + Math.Min( x + 1, size - 1 )];
				var hD = heightMap[Math.Max( y - 1, 0 ) * size + x];
				var hU = heightMap[Math.Min( y + 1, size - 1 ) * size + x];

				const float strength = 3.5f;
				var n = new Vector3( ( hL - hR ) * strength, ( hD - hU ) * strength, 1f ).Normal;

				normal[dst + 0] = (byte)( ( n.x * 0.5f + 0.5f ) * 255 );
				normal[dst + 1] = (byte)( ( n.y * 0.5f + 0.5f ) * 255 );
				normal[dst + 2] = (byte)( ( n.z * 0.5f + 0.5f ) * 255 );
				normal[dst + 3] = 255;

				var rough = Math.Clamp( 0.5f - h * 0.25f, 0.15f, 0.75f );
				var rb = (byte)( rough * 255 );
				roughness[dst + 0] = rb;
				roughness[dst + 1] = rb;
				roughness[dst + 2] = rb;
				roughness[dst + 3] = 255;
			}
		}

		return (albedo, normal, roughness);
	}
}
