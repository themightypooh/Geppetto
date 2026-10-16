using System;
using System.Diagnostics;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// A voxel skin of a SOLID is one skin. SdfMesh used to call the middle of anything thicker than two
/// bands "outside", so Voxel remesh hollowed thick parts into two nested shells; and surface nets let
/// a lone inside sample through as a zero-size speck. Measured by volume and piece count, not by eye.
/// </summary>
public static class VoxelSolidTests
{
	public static void Run()
	{
		Section( "voxel skins of thick solids are solid" );
		TestThickBoxRemeshesSolid();
		TestThickSphereRemeshesSolid();
		TestOpenPatchStillReadsOutside();
		TestBlobHasNoSpeck();
	}

	static void TestThickBoxRemeshesSolid()
	{
		var studio = new PartStudio();
		var clock = Stopwatch.StartNew();
		var r = BuildScript.Run( studio, "add Primitive name=A shape=Box sizex=10 sizey=10 sizez=10\nadd Remesh mode=Voxel voxel=0.25" );
		var seconds = clock.Elapsed.TotalSeconds;
		var m = studio.Bodies.FirstOrDefault()?.Mesh;
		var v = m?.SignedVolume() ?? 0f;

		Check( "a 10 in box remeshes at 0.25", r.Ok && m is not null, r.ToString() );
		Check( "as one piece, not a shell inside a shell", m is not null && MeshSplit.PieceCount( m ) == 1, m is null ? "" : $"{MeshSplit.PieceCount( m )} pieces" );
		Check( "holding the box's volume (within 3%)", MathF.Abs( v - 1000f ) < 30f, $"{v:0.#}" );
		Check( "in reasonable time", seconds < 10, $"{seconds:0.0} s" );
	}

	static void TestThickSphereRemeshesSolid()
	{
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Primitive name=S shape=Sphere radius=6 divisions=12\nadd Remesh mode=Voxel voxel=0.3" );
		var plain = new PartStudio();
		BuildScript.Run( plain, "add Primitive name=S shape=Sphere radius=6 divisions=12" );
		var m = studio.Bodies.FirstOrDefault()?.Mesh;
		var want = plain.Bodies[0].Mesh.SignedVolume();
		var v = m?.SignedVolume() ?? 0f;
		Check( "a 12 in sphere remeshes as one solid piece", r.Ok && m is not null && MeshSplit.PieceCount( m ) == 1, r.ToString() );
		Check( "with its volume (within 3%)", MathF.Abs( v - want ) < want * 0.03f, $"{v:0.#} vs {want:0.#}" );
	}

	static void TestOpenPatchStillReadsOutside()
	{
		// An open sheet has no inside; far below it must still read as outside, which is what the
		// callers that build fields from cut-off patches of a body rely on.
		var sheet = Primitives.Plane( 20, 20 );
		var field = new SdfMesh( sheet, 1f );
		Check( "an open patch still reads outside past its band", field.Eval( new Vec3( 0, 0, -5 ) ) > 0f && field.Eval( new Vec3( 0, 0, 5 ) ) > 0f );

		var box = new SdfMesh( Primitives.Box( 10, 10, 10 ), 1f );
		Check( "a closed box reads inside at its centre", box.Eval( Vec3.Zero ) < 0f, $"{box.Eval( Vec3.Zero )}" );
		Check( "and outside beside it", box.Eval( new Vec3( 5.5f, 0, 0 ) ) > 0f && box.Eval( new Vec3( 4.5f, 4.5f, 5.8f ) ) > 0f );
	}

	static void TestBlobHasNoSpeck()
	{
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Blob name=B shapes=\"ellipsoid 0,0,0 4,3,2.5\" voxel=0.25" );
		var m = studio.Bodies.FirstOrDefault()?.Mesh;
		Check( "the ellipsoid blob builds", r.Ok && m is not null, r.ToString() );
		if ( m is null ) return;

		var zero = m.Faces.Count( f => m.FaceArea( f ) < 1e-6f );
		Check( "with no zero-size speck faces", zero == 0, $"{zero} zero-area faces" );
		Check( "as one piece", MeshSplit.PieceCount( m ) == 1, $"{MeshSplit.PieceCount( m )}" );
	}
}
