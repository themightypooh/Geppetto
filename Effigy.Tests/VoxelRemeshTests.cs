using System;
using System.Collections.Generic;
using System.Linq;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Remesh's Voxel mode (Dynamesh): parts become one even quad skin, overlapping parts melt into one
/// body, material slots ride through, and an open part is warned about rather than silently holed.
/// Driven through a build script, the way an agent uses it.
/// </summary>
public static class VoxelRemeshTests
{
	public static void Run()
	{
		Section( "remesh: voxel mode fuses parts into one even skin" );

		// A 3 in sphere with a 1 in sphere pushed half into its side (slot 1): a horn bud on a head.
		const string parts =
			"add Primitive name=Head shape=Sphere radius=3 segments=24\n" +
			"add Primitive name=Bud shape=Sphere radius=1 segments=12 position=3.2,0,0 material=1\n";

		var studio = new PartStudio();
		var r = BuildScript.Run( studio, parts + "add Remesh mode=Voxel voxel=0.25\n" );
		var feature = studio.Features.OfType<RemeshFeature>().FirstOrDefault();
		Check( "the script sets Voxel mode", r.Ok && feature is { Mode.Index: RemeshFeature.ModeVoxel }, r.ToString() );
		Check( "two overlapping parts come out as one body", studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );

		var mesh = studio.Bodies.FirstOrDefault()?.Mesh;
		if ( mesh is null ) { Check( "there is a mesh", false ); return; }

		Check( "all quads", mesh.Faces.All( f => f.Count == 4 ), $"{mesh.Faces.Count( f => f.Count != 4 )} not quads of {mesh.FaceCount}" );
		var uses = new Dictionary<EdgeKey, int>();
		var lengths = new List<float>();
		foreach ( var f in mesh.Faces )
			for ( var i = 0; i < f.Count; i++ )
			{
				var k = new EdgeKey( f.Indices[i], f.Indices[(i + 1) % f.Count] );
				uses[k] = uses.GetValueOrDefault( k ) + 1;
				lengths.Add( (mesh.Positions[k.A] - mesh.Positions[k.B]).Length );
			}
		Check( "closed: every edge has two faces", uses.Values.All( u => u == 2 ) );
		var mean = lengths.Average();
		Check( "edges are about one voxel long", mean is > 0.15f and < 0.35f, $"mean {mean:0.000} in" );

		var maxX = mesh.Positions.Max( p => p.x );
		Check( "the bud is still there, melted on", maxX is > 4.0f and < 4.35f, $"reaches x {maxX:0.00} (bud tip 4.2)" );
		var budFaces = mesh.Faces.Count( f => f.Material == 1 );
		Check( "and keeps its own material slot", budFaces > 10 && budFaces < mesh.FaceCount / 3, $"{budFaces} of {mesh.FaceCount} faces on slot 1" );

		var apart = new PartStudio();
		var r2 = BuildScript.Run( apart, parts + "add Remesh mode=Voxel voxel=0.25 fuse=0\n" );
		Check( "Fuse off re-skins each part on its own", r2.Ok && apart.Bodies.Count == 2, $"{apart.Bodies.Count} bodies" );

		var filleted = new PartStudio();
		BuildScript.Run( filleted, parts + "add Remesh mode=Voxel voxel=0.25 fillet=0.8\n" );
		var fm = filleted.Bodies.FirstOrDefault()?.Mesh;
		// Where the bud meets the head (x ≈ 2.95, off the axis) a fillet fills the crease outward.
		float Neck( PolyMesh m ) => m.Positions.Where( p => MathF.Abs( p.x - 2.75f ) < 0.15f ).Select( p => MathF.Sqrt( p.y * p.y + p.z * p.z ) ).DefaultIfEmpty( 0 ).Min();
		Check( "a fillet fills the crease where parts meet", fm is not null && Neck( fm ) > Neck( mesh ) + 0.05f, fm is null ? "" : $"neck {Neck( fm ):0.00} vs {Neck( mesh ):0.00}" );

		var tiny = new PartStudio();
		var r3 = BuildScript.Run( tiny, "add Primitive name=A shape=Sphere radius=200 segments=12\nadd Remesh mode=Voxel voxel=0.01\n" );
		Check( "a voxel too small for the part fails instead of eating the machine", tiny.Features.OfType<RemeshFeature>().First().Error?.Contains( "voxel is too small" ) == true,
			tiny.Features.OfType<RemeshFeature>().First().Error?.ToString() ?? r3.ToString() );
	}
}
