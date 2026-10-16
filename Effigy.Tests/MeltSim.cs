using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Effigy.Tests;

/// <summary>
/// A headless melt: a solid sampled into particles that heat up from the surface in, soften, and
/// flow as a very viscous fluid over what is still solid. After Carlson et al. 2002 ("Melting and
/// Flowing": a solid is a fluid whose viscosity is effectively infinite, and viscosity falls as
/// temperature crosses the melt point) and Paiva et al. (the same on particles), solved here with
/// position-based fluids (Macklin and Müller 2013) for the incompressibility.
///
/// What that buys over a warp: thin parts and ridges melt first (more surface per volume); the
/// melted skin runs down over the still-rigid core and gathers at the lowest point, where it
/// beads and drips; the core shrinks as the puddle grows (volume is kept); and negative pressure
/// pulls the liquid together, which rounds detail and necks drips into beads.
///
/// Units are inches and seconds. Unmelted particles are pinned to where they started (the candy
/// is held by its stick); a softening particle is pulled back to its rest place less and less,
/// which is the droop; a melted one is free.
/// </summary>
public static class MeltSim
{
	public sealed class Setup
	{
		public Sdf Body;        // what melts
		public Sdf Support;     // what holds it and never melts (the stick); also a collider
		public Func<Vec3, Vec3> ColourAt; // the body's colour at a rest point
		public float Floor;     // z of the ground
	}

	public static int Run( string outDir, Dictionary<string, float> o, Setup setup )
	{
		float Get( string k, float d ) => o.TryGetValue( k, out var v ) ? v : d;
		var s = Get( "spacing", 0.06f );          // particle spacing
		var H = s * Get( "kernel", 2.1f );        // neighbour radius
		var frames = (int)Get( "frames", 96 );
		var simTime = Get( "seconds", 14f );      // simulated seconds, played as a time-lapse
		var dt = 1f / Get( "rate", 240f );
		var g = Get( "gravity", 386f );           // in/s²
		var visc = Get( "visc", 0.9f );           // XSPH blend per pass: how thick the melt is
		var viscPasses = (int)Get( "viscPasses", 6 ); // more passes reach further: a stand-in for an implicit solve
		var cohesion = Get( "cohesion", 0.5f );  // how much negative pressure is kept: surface tension
		var ambient = Get( "ambient", 1.5f );     // air temperature, melt point is 1
		var heatIn = Get( "heatIn", 0.6f );      // surface heating rate
		var sunIn = Get( "sun", 0.25f );          // extra heating on faces turned to the sun
		var diffuse = Get( "diffuse", 0.4f );     // conduction inside
		var soft = Get( "soft", 0.55f );          // softening starts (glass transition)
		var melt = Get( "melt", 1.0f );           // fully liquid
		var voxel = Get( "voxel", s * 0.55f );
		var sun = new Vec3( 0.55f, 0.25f, 0.8f ).Normal;
		Directory.CreateDirectory( outDir );

		// --- sample the body on a lattice ---------------------------------------------------
		setup.Body.Prepare( 0f );
		setup.Support.Prepare( 0f );
		var min = setup.Body.Min; var max = setup.Body.Max;
		var pos = new List<Vec3>();
		for ( var x = min.x; x <= max.x; x += s )
			for ( var y = min.y; y <= max.y; y += s )
				for ( var z = min.z; z <= max.z; z += s )
				{
					var p = new Vec3( x, y, z );
					if ( setup.Body.Eval( p ) < 0f && setup.Support.Eval( p ) > s * 0.4f ) pos.Add( p );
				}
		var N = pos.Count;
		var px = new float[N]; var py = new float[N]; var pz = new float[N];
		var rx = new float[N]; var ry = new float[N]; var rz = new float[N];
		var vx = new float[N]; var vy = new float[N]; var vz = new float[N];
		var qx = new float[N]; var qy = new float[N]; var qz = new float[N]; // predicted
		var T = new float[N]; var free = new float[N]; var rho = new float[N]; var lam = new float[N];
		var col = new Vec3[N];
		Parallel.For( 0, N, i =>
		{
			px[i] = rx[i] = pos[i].x; py[i] = ry[i] = pos[i].y; pz[i] = rz[i] = pos[i].z;
			col[i] = setup.ColourAt( pos[i] );
		} );
		Console.WriteLine( $"meltsim: {N:N0} particles at {s} in" );

		// --- kernels (unnormalised; rest density is the full-lattice sum) --------------------
		var H2 = H * H;
		float W( float r2 ) { if ( r2 >= H2 ) return 0f; var q = 1f - r2 / H2; return q * q * q; }
		// dW/dr divided by r, so grad = (xi - xj) * GradOverR
		float GradOverR( float r2 ) { if ( r2 >= H2 ) return 0f; var q = 1f - r2 / H2; return -6f / H2 * q * q; }
		var rho0 = 0f;
		var lat = (int)MathF.Ceiling( H / s );
		for ( var a = -lat; a <= lat; a++ ) for ( var b = -lat; b <= lat; b++ ) for ( var c = -lat; c <= lat; c++ )
					rho0 += W( (a * a + b * b + c * c) * s * s );

		// --- neighbour grid ------------------------------------------------------------------
		var gmin = new Vec3( -9f, -9f, setup.Floor - 1f );
		var gx = (int)(18f / H) + 1; var gy = gx; var gz = (int)((max.z + 2f - gmin.z) / H) + 1;
		var cellStart = new int[gx * gy * gz + 1];
		var cellOf = new int[N]; var sorted = new int[N];
		const int MaxN = 96;
		var nbr = new int[N * MaxN]; var nbrCount = new int[N];
		int Cell( float x, float y, float z )
		{
			var cx = Math.Clamp( (int)((x - gmin.x) / H ), 0, gx - 1 );
			var cy = Math.Clamp( (int)((y - gmin.y) / H ), 0, gy - 1 );
			var cz = Math.Clamp( (int)((z - gmin.z) / H ), 0, gz - 1 );
			return (cx * gy + cy) * gz + cz;
		}
		void BuildGrid( float[] x, float[] y, float[] z )
		{
			Array.Clear( cellStart );
			for ( var i = 0; i < N; i++ ) { cellOf[i] = Cell( x[i], y[i], z[i] ); cellStart[cellOf[i] + 1]++; }
			for ( var c = 0; c < cellStart.Length - 1; c++ ) cellStart[c + 1] += cellStart[c];
			var fill = (int[])cellStart.Clone();
			for ( var i = 0; i < N; i++ ) sorted[fill[cellOf[i]]++] = i;
			Parallel.For( 0, N, i =>
			{
				var cx = Math.Clamp( (int)((x[i] - gmin.x) / H ), 0, gx - 1 );
				var cy = Math.Clamp( (int)((y[i] - gmin.y) / H ), 0, gy - 1 );
				var cz = Math.Clamp( (int)((z[i] - gmin.z) / H ), 0, gz - 1 );
				var n = 0;
				for ( var a = Math.Max( 0, cx - 1 ); a <= Math.Min( gx - 1, cx + 1 ); a++ )
					for ( var b = Math.Max( 0, cy - 1 ); b <= Math.Min( gy - 1, cy + 1 ); b++ )
						for ( var c = Math.Max( 0, cz - 1 ); c <= Math.Min( gz - 1, cz + 1 ); c++ )
						{
							var cell = (a * gy + b) * gz + c;
							for ( var k = cellStart[cell]; k < cellStart[cell + 1]; k++ )
							{
								var j = sorted[k];
								if ( j == i ) continue;
								var dx = x[i] - x[j]; var dy = y[i] - y[j]; var dz = z[i] - z[j];
								if ( dx * dx + dy * dy + dz * dz < H2 && n < MaxN ) nbr[i * MaxN + n++] = j;
							}
						}
				nbrCount[i] = n;
			} );
		}

		// --- support collider: push out of the stick, and the floor -------------------------
		var rad = s * 0.5f;
		void Collide( int i, float ox, float oy, float oz )
		{
			if ( qz[i] < setup.Floor + rad )
			{
				qz[i] = setup.Floor + rad;
				// Sticky floor: a puddle creeps rather than skates.
				qx[i] = ox + (qx[i] - ox) * 0.35f; qy[i] = oy + (qy[i] - oy) * 0.35f;
			}
			var p = new Vec3( qx[i], qy[i], qz[i] );
			var d = setup.Support.Eval( p );
			if ( d < rad )
			{
				const float e = 0.002f;
				var n = new Vec3(
					setup.Support.Eval( p + new Vec3( e, 0, 0 ) ) - setup.Support.Eval( p - new Vec3( e, 0, 0 ) ),
					setup.Support.Eval( p + new Vec3( 0, e, 0 ) ) - setup.Support.Eval( p - new Vec3( 0, e, 0 ) ),
					setup.Support.Eval( p + new Vec3( 0, 0, e ) ) - setup.Support.Eval( p - new Vec3( 0, 0, e ) ) ).Normal;
				p += n * (rad - d);
				// Candy wets the stick: slide down it slowly.
				var o3 = new Vec3( ox, oy, oz );
				var moved = p - o3;
				var normalPart = n * Vec3.Dot( moved, n );
				p = o3 + normalPart + (moved - normalPart) * 0.6f;
				qx[i] = p.x; qy[i] = p.y; qz[i] = p.z;
			}
		}

		var steps = (int)MathF.Round( simTime / dt );
		var perFrame = Math.Max( 1, steps / frames );
		var frame = 0;
		var nx = new float[N]; var ny = new float[N]; var nz = new float[N];
		var dT = new float[N];
		var tvx = new float[N]; var tvy = new float[N]; var tvz = new float[N];
		var clock = System.Diagnostics.Stopwatch.StartNew();
		WriteFrame( frame++ );

		for ( var step = 1; step <= steps; step++ )
		{
			// Predict.
			Parallel.For( 0, N, i =>
			{
				if ( free[i] > 0f ) vz[i] -= g * dt * free[i];
				qx[i] = px[i] + vx[i] * dt; qy[i] = py[i] + vy[i] * dt; qz[i] = pz[i] + vz[i] * dt;
			} );
			BuildGrid( qx, qy, qz );

			// Density constraint: incompressible, and negative pressure kept (scaled) for cohesion.
			for ( var iter = 0; iter < 3; iter++ )
			{
				Parallel.For( 0, N, i =>
				{
					var r = 1f; var gsx = 0f; var gsy = 0f; var gsz = 0f; var g2 = 0f;
					var b = i * MaxN;
					for ( var k = 0; k < nbrCount[i]; k++ )
					{
						var j = nbr[b + k];
						var dx = qx[i] - qx[j]; var dy = qy[i] - qy[j]; var dz = qz[i] - qz[j];
						var r2 = dx * dx + dy * dy + dz * dz;
						r += W( r2 );
						var go = GradOverR( r2 ) / rho0;
						gsx += dx * go; gsy += dy * go; gsz += dz * go;
						g2 += go * go * r2;
					}
					rho[i] = r;
					var C = r / rho0 - 1f;
					if ( C < 0f ) C *= cohesion;
					lam[i] = -C / (g2 + gsx * gsx + gsy * gsy + gsz * gsz + 1e-2f);
				} );
				Parallel.For( 0, N, i =>
				{
					if ( free[i] <= 0f ) { dT[i] = 0; return; }
					var ddx = 0f; var ddy = 0f; var ddz = 0f;
					var b = i * MaxN;
					for ( var k = 0; k < nbrCount[i]; k++ )
					{
						var j = nbr[b + k];
						var dx = qx[i] - qx[j]; var dy = qy[i] - qy[j]; var dz = qz[i] - qz[j];
						var go = GradOverR( dx * dx + dy * dy + dz * dz ) / rho0 * (lam[i] + (free[j] > 0.5f ? lam[j] : lam[i])); // solid neighbours are boundary: they push back with this particle's own pressure
						ddx += dx * go; ddy += dy * go; ddz += dz * go;
					}
					// Relaxed and clamped: Jacobi corrections stack up and overshoot otherwise.
					var w = 0.5f * free[i]; ddx *= w; ddy *= w; ddz *= w;
					var l2 = ddx * ddx + ddy * ddy + ddz * ddz; var cap = 0.15f * s;
					if ( l2 > cap * cap ) { var f = cap / MathF.Sqrt( l2 ); ddx *= f; ddy *= f; ddz *= f; }
					nx[i] = ddx; ny[i] = ddy; nz[i] = ddz;
					dT[i] = 1;
				} );
				Parallel.For( 0, N, i =>
				{
					if ( dT[i] == 0 ) return;
					qx[i] += nx[i]; qy[i] += ny[i]; qz[i] += nz[i];
					Collide( i, px[i], py[i], pz[i] );
				} );
			}

			// Softening: what isn't liquid yet is pulled back to its rest place, harder the colder.
			Parallel.For( 0, N, i =>
			{
				var hold = 1f - free[i];
				if ( hold > 0f )
				{
					qx[i] += (rx[i] - qx[i]) * hold; qy[i] += (ry[i] - qy[i]) * hold; qz[i] += (rz[i] - qz[i]) * hold;
				}
				var ivdt = 1f / dt;
				vx[i] = (qx[i] - px[i]) * ivdt; vy[i] = (qy[i] - py[i]) * ivdt; vz[i] = (qz[i] - pz[i]) * ivdt;
				// Cap the speed at a third of a particle per step: viscous candy never gets near it,
				// so this only stops a bad step running away.
				var vmax = s * 0.33f * ivdt; var v2 = vx[i] * vx[i] + vy[i] * vy[i] + vz[i] * vz[i];
				if ( v2 > vmax * vmax ) { var f = vmax / MathF.Sqrt( v2 ); vx[i] *= f; vy[i] *= f; vz[i] *= f; }
				px[i] = qx[i]; py[i] = qy[i]; pz[i] = qz[i];
			} );

			// Viscosity (XSPH), twice: neighbours' velocities, solid ones at rest, so a melt film drags
			// on the core it runs over. Thick as honey-thinned candy.
			for ( var pass = 0; pass < viscPasses; pass++ )
			{
				Parallel.For( 0, N, i =>
				{
					tvx[i] = vx[i]; tvy[i] = vy[i]; tvz[i] = vz[i];
					if ( free[i] <= 0f ) return;
					var sw = 0f; var ax = 0f; var ay = 0f; var az = 0f;
					var b = i * MaxN;
					for ( var k = 0; k < nbrCount[i]; k++ )
					{
						var j = nbr[b + k];
						var dx = px[i] - px[j]; var dy = py[i] - py[j]; var dz = pz[i] - pz[j];
						var w = W( dx * dx + dy * dy + dz * dz );
						ax += (vx[j] - vx[i]) * w; ay += (vy[j] - vy[i]) * w; az += (vz[j] - vz[i]) * w;
						sw += w;
					}
					if ( sw > 0f )
					{
						var c = visc / sw;
						tvx[i] += ax * c; tvy[i] += ay * c; tvz[i] += az * c;
					}
				} );
				(vx, tvx) = (tvx, vx); (vy, tvy) = (tvy, vy); (vz, tvz) = (tvz, vz);
			}

			// Heat: the air warms the surface (exposure is the density deficit), the sun warms the faces
			// turned to it, and it conducts inward. Melted candy no longer cools; free only ever grows.
			Parallel.For( 0, N, i =>
			{
				var b = i * MaxN;
				var lap = 0f; var sw = 0f; var gxs = 0f; var gys = 0f; var gzs = 0f;
				for ( var k = 0; k < nbrCount[i]; k++ )
				{
					var j = nbr[b + k];
					var dx = px[i] - px[j]; var dy = py[i] - py[j]; var dz = pz[i] - pz[j];
					var w = W( dx * dx + dy * dy + dz * dz );
					lap += (T[j] - T[i]) * w; sw += w;
					gxs += dx * w; gys += dy * w; gzs += dz * w; // points out of the body
				}
				// Exposure: how lopsided the neighbourhood is (the colour-field gradient). Zero inside, about
				// 0.3 on a flat face, more on edges and ridges: those melt first. Density deficit would do
				// too, but cohesion packs the skin and hides it.
				var expose = sw > 0 ? Math.Clamp( 3.5f * MathF.Sqrt( gxs * gxs + gys * gys + gzs * gzs ) / (sw * H), 0f, 1.5f ) : 1.5f;
				var gl = MathF.Sqrt( gxs * gxs + gys * gys + gzs * gzs ) + 1e-6f;
				var facing = MathF.Max( 0f, (gxs * sun.x + gys * sun.y + gzs * sun.z) / gl );
				// On the ground the puddle sits in the warm: no extra sun term needed.
				dT[i] = (sw > 0 ? diffuse * lap / sw : 0f) + expose * (heatIn + sunIn * facing) * (ambient - T[i]);
			} );
			Parallel.For( 0, N, i =>
			{
				T[i] += dT[i] * dt;
				var f = Math.Clamp( (T[i] - soft) / (melt - soft), 0f, 1f );
				f = f * f * (3f - 2f * f);
				if ( f > free[i] ) free[i] = f;
			} );

			if ( step % perFrame == 0 && frame < frames )
			{
				WriteFrame( frame++ );
			}
		}
		Console.WriteLine( $"meltsim: {frame} frames in {clock.Elapsed.TotalSeconds:0}s" );
		return 0;

		void WriteFrame( int f )
		{
			var field = new ParticleField( px, py, pz, col, H, s * Get( "blob", 0.85f ), setup.Support, Get( "stickK", 0.05f ) );
			var res = SurfaceNets.Mesh( field, voxel );
			var mesh = res.Mesh;
			MeshSmooth.Taubin( mesh, (int)Get( "smooth", 4 ) );
			var vc = new Vec4[mesh.VertexCount];
			Parallel.For( 0, mesh.VertexCount, v => vc[v] = field.ColourAt( mesh.Positions[v] ) );
			mesh.VertexColors = vc;
			ObjWriter.WriteFile( mesh, Path.Combine( outDir, $"f{f:000}.obj" ), "melt" );
			var liquid = 0; var soft2 = 0; var tSum = 0f; var tMax = 0f; var eSum = 0f;
			for ( var i = 0; i < N; i++ )
			{
				if ( free[i] > 0.99f ) liquid++; else if ( free[i] > 0f ) soft2++;
				tSum += T[i]; tMax = MathF.Max( tMax, T[i] ); eSum += rho[i] / rho0;
			}
			Console.WriteLine( $"frame {f}: {liquid * 100f / N:0}% liquid, {soft2 * 100f / N:0}% soft, T mean {tSum / N:0.00} max {tMax:0.00}, rho {eSum / N:0.00}, {mesh.VertexCount:N0} verts, {clock.Elapsed.TotalSeconds:0}s" );
		}
	}

	/// <summary>The particles as a surface (Zhu and Bridson 2005: distance to the kernel-weighted
	/// mean position, minus a radius), smooth-unioned with the support so the stick is wetted.</summary>
	sealed class ParticleField : SdfLeaf
	{
		readonly float[] _x, _y, _z; readonly Vec3[] _c; readonly float _R, _r, _k; readonly Sdf _support;
		readonly Dictionary<long, List<int>> _grid = new();
		readonly Vec3 _min, _max;
		public ParticleField( float[] x, float[] y, float[] z, Vec3[] c, float R, float r, Sdf support, float k )
		{
			_x = x; _y = y; _z = z; _c = c; _R = R; _r = r; _support = support; _k = k;
			var mn = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue ); var mx = -mn;
			for ( var i = 0; i < x.Length; i++ )
			{
				mn = new Vec3( MathF.Min( mn.x, x[i] ), MathF.Min( mn.y, y[i] ), MathF.Min( mn.z, z[i] ) );
				mx = new Vec3( MathF.Max( mx.x, x[i] ), MathF.Max( mx.y, y[i] ), MathF.Max( mx.z, z[i] ) );
				var key = Key( x[i], y[i], z[i] );
				if ( !_grid.TryGetValue( key, out var l ) ) _grid[key] = l = new List<int>();
				l.Add( i );
			}
			var sMin = support.Min; var sMax = support.Max;
			_min = new Vec3( MathF.Min( mn.x, sMin.x ), MathF.Min( mn.y, sMin.y ), MathF.Min( mn.z, sMin.z ) ) - Vec3.One * R;
			_max = new Vec3( MathF.Max( mx.x, sMax.x ), MathF.Max( mx.y, sMax.y ), MathF.Max( mx.z, sMax.z ) ) + Vec3.One * R;
		}
		long Key( float x, float y, float z ) => Key( (int)MathF.Floor( x / _R ), (int)MathF.Floor( y / _R ), (int)MathF.Floor( z / _R ) );
		static long Key( int a, int b, int c ) => ((long)(a + 100000) << 40) | ((long)(b + 100000) << 20) | (long)(c + 100000);

		float Particles( Vec3 p, out Vec3 colour )
		{
			int a = (int)MathF.Floor( p.x / _R ), b = (int)MathF.Floor( p.y / _R ), c = (int)MathF.Floor( p.z / _R );
			var sw = 0f; var mx = 0f; var my = 0f; var mz = 0f; var cc = Vec3.Zero; var R2 = _R * _R;
			for ( var i = a - 1; i <= a + 1; i++ )
				for ( var j = b - 1; j <= b + 1; j++ )
					for ( var k = c - 1; k <= c + 1; k++ )
						if ( _grid.TryGetValue( Key( i, j, k ), out var l ) )
							foreach ( var n in l )
							{
								var dx = p.x - _x[n]; var dy = p.y - _y[n]; var dz = p.z - _z[n];
								var d2 = dx * dx + dy * dy + dz * dz;
								if ( d2 >= R2 ) continue;
								var q = 1f - d2 / R2; var w = q * q * q;
								sw += w; mx += _x[n] * w; my += _y[n] * w; mz += _z[n] * w; cc += _c[n] * w;
							}
			if ( sw <= 0f ) { colour = Vec3.Zero; return _R; }
			colour = cc / sw;
			var m = new Vec3( mx / sw, my / sw, mz / sw );
			return (p - m).Length - _r;
		}

		public override float Eval( Vec3 p ) => SmoothMin( Particles( p, out _ ), _support.Eval( p ), _k );

		public Vec4 ColourAt( Vec3 p )
		{
			var d = Particles( p, out var c );
			var st = _support.Eval( p );
			// Alpha 0 marks the stick for the candy shading preset.
			var t = Math.Clamp( 0.5f + (st - d) / 0.06f, 0f, 1f );
			var white = new Vec3( 0.93f, 0.93f, 0.9f );
			var colour = d >= _R ? white : Vec3.Lerp( white, c, t );
			return new Vec4( colour.x, colour.y, colour.z, t );
		}

		public override Vec3 Min => _min;
		public override Vec3 Max => _max;
	}
}
