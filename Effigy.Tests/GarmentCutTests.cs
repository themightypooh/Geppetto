using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The two things that made a generated garment look generated: a sawtooth hem, and a sleeve
/// texture smeared along the arm. Both are checked on shapes where the right answer is exact.
/// </summary>
public static class GarmentCutTests
{
	public static void Run()
	{
		Report.Section( "garment cut: a hem is a plane, not a sawtooth" );
		TestPlanarHem();

		Report.Section( "garment cut: a sleeve's UVs follow the arm" );
		TestLimbUVs();
	}

	/// <summary>A tube round an axis: rings x segments quads.</summary>
	static PolyMesh Tube( Vec3 from, Vec3 to, float radius, int rings, int segments )
	{
		var mesh = new PolyMesh();
		var axis = (to - from).Normal;
		var reference = MathF.Abs( axis.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var e1 = Vec3.Cross( reference, axis ).Normal;
		var e2 = Vec3.Cross( axis, e1 );

		for ( var r = 0; r <= rings; r++ )
		{
			var centre = from + (to - from) * (r / (float)rings);

			for ( var s = 0; s < segments; s++ )
			{
				var a = s / (float)segments * MathF.PI * 2f;
				mesh.AddVertex( centre + (e1 * MathF.Cos( a ) + e2 * MathF.Sin( a )) * radius );
			}
		}

		for ( var r = 0; r < rings; r++ )
			for ( var s = 0; s < segments; s++ )
			{
				var n = (s + 1) % segments;
				mesh.AddFace( new[] { r * segments + s, r * segments + n, (r + 1) * segments + n, (r + 1) * segments + s } );
			}

		return mesh;
	}

	static void TestPlanarHem()
	{
		// A vertical tube body on a spine, cut by a T-shirt whose hem falls between two rings, so
		// the cut has to go THROUGH faces, not between them.
		var rig = new Skeleton();
		var root = rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -20 ), new Vec3( 0, 0, -19 ) );
		var pelvis = rig.AddBoneFromPoints( "pelvis", root, new Vec3( 0, 0, -10 ), new Vec3( 0, 0, 0 ) );
		rig.AddBoneFromPoints( "spine", pelvis, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 10 ) );

		var body = Tube( new Vec3( 0, 0, -12 ), new Vec3( 0, 0, 12 ), 4f, 7, 24 );
		var map = new BodyRegions.Map( rig );

		// Hips span 0..0.55 of pelvis: hem at z = -5.5, between the rings at -4.6 and -8.0.
		var recipe = GarmentRecipe.Build( 0, 0.55f, 0f );
		var fit = GarmentFit.Fit( new[] { body }, map, recipe, new GarmentFit.Options
		{
			Offset = 0f, Clearance = 0f, Passes = 0, Drape = false, Thickness = 0f, DrapeSteps = 0,
		} );

		Report.Check( "the shirt was made", fit?.Mesh is { FaceCount: > 0 } );

		if ( fit?.Mesh is not { } shirt )
			return;

		var loops = GarmentTrim.OrderedBoundaryLoops( shirt );
		var hem = loops.OrderBy( l => l.Average( i => shirt.Positions[i].z ) ).First();
		var zs = hem.Select( i => shirt.Positions[i].z ).ToList();

		Report.Check( "two openings, collar and hem", loops.Count == 2, $"{loops.Count}" );
		Report.Check( "the hem lies in one plane", zs.Max() - zs.Min() < 0.05f, $"spread {zs.Max() - zs.Min():0.###}" );
		Report.Check( "at the height the recipe asked for", MathF.Abs( zs.Average() + 5.5f ) < 0.3f, $"z {zs.Average():0.##}" );
		Report.Check( "the hem has as many vertices as the tube has segments", hem.Count == 24, $"{hem.Count}" );
		Report.Check( "and the shirt is still a valid mesh", MeshValidator.Validate( shirt ).Errors.Count == 0 );
	}

	static void TestLimbUVs()
	{
		// An arm along +y, and a sleeve tube round it. With the old projection about the vertical
		// axis the tube's UVs collapse; wrapped round the arm, UV distances match world distances
		// in both directions, which is what "no stretching" means.
		var rig = new Skeleton();
		var root = rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ) );
		var spine = rig.AddBoneFromPoints( "spine", root, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 20 ) );
		rig.AddBoneFromPoints( "arm_upper_L", spine, new Vec3( 0, 6, 18 ), new Vec3( 0, 18, 18 ) );

		var sleeve = Tube( new Vec3( 0, 8, 18 ), new Vec3( 0, 16, 18 ), 2f, 4, 16 );
		GarmentFit.LimbUVs( sleeve, new BodyRegions.Map( rig ) );

		// Every quad edge: |uv| * 12 should be |world|, along the arm and round it alike.
		var worst = 0f;
		var count = 0;

		foreach ( var face in sleeve.Faces )
		{
			for ( var c = 0; c < face.Count; c++ )
			{
				var n = (c + 1) % face.Count;
				var world = (sleeve.Positions[face.Indices[n]] - sleeve.Positions[face.Indices[c]]).Length;
				var uv = (face.UVs[n] - face.UVs[c]).Length * 12f;

				// Round the arm the tube is a polygon and the projection is the circle through
				// its corners, so a chord is measured against an arc: a few percent, not a smear.
				worst = MathF.Max( worst, MathF.Abs( uv - world ) / world );
				count++;
			}
		}

		Report.Check( "every sleeve edge is the same length in UV as in the world, within 5%", worst < 0.05f, $"worst {worst:P1} over {count} edges" );

		// The old projection, for contrast: the same sleeve smeared.
		var smeared = Tube( new Vec3( 0, 8, 18 ), new Vec3( 0, 16, 18 ), 2f, 4, 16 );
		GarmentFit.CylinderUVs( smeared );
		var smearWorst = 0f;

		foreach ( var face in smeared.Faces )
			for ( var c = 0; c < face.Count; c++ )
			{
				var n = (c + 1) % face.Count;
				var world = (smeared.Positions[face.Indices[n]] - smeared.Positions[face.Indices[c]]).Length;
				var uv = (face.UVs[n] - face.UVs[c]).Length * 12f;
				smearWorst = MathF.Max( smearWorst, MathF.Abs( uv - world ) / world );
			}

		Report.Check( "which the body-cylinder projection was not", smearWorst > 0.3f, $"worst {smearWorst:P0}" );
	}
}
