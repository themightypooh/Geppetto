using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The MaterialGroupList node, which is the step that had been missing since face materials could
/// be assigned: the slots were right, the exporters named them, and the .vmdl bound none of them.
///
/// WHAT THESE CAN AND CANNOT CHECK. Whether the ENGINE honours a remap is a compile, and the keys
/// here were not guessed — they are the ones this project's lightswitch and first-person arms
/// models already ship. What a headless test can say is that the node names the slots the mesh
/// uses, that it never asks ModelDoc to replace everything with default, and that a part nobody
/// painted still writes a list so the compiler cannot fill one in.
/// </summary>
public static class VmdlMaterialsTests
{
	public static void Run()
	{
		Section( "vmdl materials: a bound slot reaches the node" );
		TestBoundSlot();

		Section( "vmdl materials: the whole part, which is slot 0" );
		TestBaseMaterial();

		Section( "vmdl materials: the node, and what it does with nothing" );
		TestAlwaysWritesTheList();
		TestDisplayNameIsNotARemap();

		Section( "vmdl materials: several slots, several spellings" );
		TestAliases();
		TestTwoSlots();
	}

	static void TestBoundSlot()
	{
		var studio = Painted( out var mesh, slot: 2, "materials/diner/diner_tile_floor.vmat" );
		var text = VmdlMaterials.GroupList( studio, mesh );

		Check( "it is a DefaultMaterialGroup inside a MaterialGroupList",
			CountOf( text, "_class = \"DefaultMaterialGroup\"" ) == 1
			&& CountOf( text, "_class = \"MaterialGroupList\"" ) == 1 );

		Check( "braces balance", CountOf( text, "{" ) == CountOf( text, "}" ),
			$"{CountOf( text, "{" )} open, {CountOf( text, "}" )} close" );
		Check( "and brackets balance", CountOf( text, "[" ) == CountOf( text, "]" ) );

		Check( "the node is a complete child entry, comma and all",
			text.TrimEnd( '\n' ).EndsWith( "}," ) );

		Check( "use_global_default is false — true is how every slot becomes default.vmat",
			text.Contains( "use_global_default = false" )
			&& !text.Contains( "use_global_default = true" ) );

		Check( "and there is no global default material waiting to replace them",
			text.Contains( "global_default_material = \"\"" )
			&& !text.Contains( "materials/default.vmat" ) );

		Check( "the bound vmat is the remap target",
			text.Contains( "to = \"materials/diner/diner_tile_floor.vmat\"" ) );
	}

	static void TestBaseMaterial()
	{
		// Double-click in the Materials dock binds slot 0, which is every face nobody has painted.
		// A remap that only walked FaceMaterialFeature would miss it entirely, and the whole part
		// would compile as default — the original complaint, for the most ordinary assignment.
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;
		studio.MaterialNames[0] = "materials/wood/oak.vmat";
		studio.Rebuild();

		var mesh = studio.ToMesh();
		var text = VmdlMaterials.GroupList( studio, mesh );

		Check( "slot 0 is remapped when it carries a vmat",
			text.Contains( "to = \"materials/wood/oak.vmat\"" ) );

		Check( "and every face is still on slot 0, so the whole part is that material",
			mesh.Faces.All( f => f.Material == 0 ) );
	}

	static void TestAlwaysWritesTheList()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;
		studio.Rebuild();

		var text = VmdlMaterials.GroupList( studio, studio.ToMesh() );

		Check( "an unpainted part still writes a MaterialGroupList",
			text.Contains( "_class = \"MaterialGroupList\"" ) );

		Check( "with no remaps — there is nothing to bind",
			VmdlMaterials.Remaps( studio.ToMesh(), studio.NameForSlot, studio.MaterialNames ).Count == 0 );

		Check( "and still refuses the global default, so ModelDoc cannot fill one in",
			text.Contains( "use_global_default = false" ) );
	}

	static void TestDisplayNameIsNotARemap()
	{
		// A name that is not a vmat path is what the mesh writers already emit. Pointing `to` at
		// it would be a remap to an asset that does not exist.
		var studio = Painted( out var mesh, slot: 3, "anodised" );
		var remaps = VmdlMaterials.Remaps( mesh, studio.NameForSlot, studio.MaterialNames );

		Check( "a hand-typed display name does not become a remap", remaps.Count == 0,
			string.Join( ", ", remaps.Select( r => $"{r.From}->{r.To}" ) ) );
	}

	static void TestAliases()
	{
		var studio = Painted( out var mesh, slot: 1, "materials/halo/characters/elite/halo_3.vmat" );
		var remaps = VmdlMaterials.Remaps( mesh, studio.NameForSlot, studio.MaterialNames );
		var froms = remaps.Select( r => r.From ).ToList();

		Check( "the full path the exporters write is a from",
			froms.Contains( "materials/halo/characters/elite/halo_3.vmat" ) );

		Check( "so is the filename, which is what the lightswitch files remap from",
			froms.Contains( "halo_3.vmat" ) );

		Check( "and both with .vmat stripped, because ModelDoc drops everything after a period",
			froms.Contains( "halo_3" ) && froms.Contains( "materials/halo/characters/elite/halo_3" ) );

		Check( "every alias points at the same vmat",
			remaps.All( r => r.To == "materials/halo/characters/elite/halo_3.vmat" ) );
	}

	static void TestTwoSlots()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 3f;
		box.SizeZ.Value = 2f;
		studio.Rebuild();

		var body = studio.Bodies.Single();
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );
		var side = FaceIndexFacing( body.Mesh, new Vec3( 1, 0, 0 ) );

		var paintTop = studio.Add( new FaceMaterialFeature() );
		paintTop.Material.Value = 1;
		paintTop.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );

		var paintSide = studio.Add( new FaceMaterialFeature() );
		paintSide.Material.Value = 2;
		paintSide.Faces.Add( FacePlane.Capture( body, side, body.Mesh.FaceCentroid( body.Mesh.Faces[side] ) ) );

		studio.MaterialNames[0] = "materials/wood/oak.vmat";
		studio.MaterialNames[1] = "materials/metal/brushed.vmat";
		studio.MaterialNames[2] = "materials/rubber/grip.vmat";
		studio.Rebuild();

		var mesh = studio.ToMesh();
		var remaps = VmdlMaterials.Remaps( mesh, studio.NameForSlot, studio.MaterialNames );
		var targets = remaps.Select( r => r.To ).Distinct().OrderBy( t => t ).ToList();

		Check( "three bound slots make three remap targets",
			targets.Count == 3,
			string.Join( ", ", targets ) );

		Check( "oak, brushed and grip are all there",
			targets.Contains( "materials/wood/oak.vmat" )
			&& targets.Contains( "materials/metal/brushed.vmat" )
			&& targets.Contains( "materials/rubber/grip.vmat" ) );

		// A name sitting on a slot no face wears must not appear. Slot 7 is named and unused.
		studio.MaterialNames[7] = "materials/unused/spare.vmat";
		var after = VmdlMaterials.Remaps( mesh, studio.NameForSlot, studio.MaterialNames );

		Check( "a named slot with no faces is not remapped",
			after.All( r => r.To != "materials/unused/spare.vmat" ) );
	}

	// --- helpers ----------------------------------------------------------------------------------

	static PartStudio Painted( out PolyMesh mesh, int slot, string name )
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 3f;
		box.SizeZ.Value = 2f;
		studio.Rebuild();

		var body = studio.Bodies.Single();
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var paint = studio.Add( new FaceMaterialFeature() );
		paint.Material.Value = slot;
		paint.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );
		studio.MaterialNames[slot] = name;
		studio.Rebuild();

		mesh = studio.ToMesh();
		return studio;
	}

	static int FaceIndexFacing( PolyMesh mesh, Vec3 direction )
	{
		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( Vec3.Dot( mesh.FaceNormal( mesh.Faces[i] ), direction.Normal ) > 0.99f )
				return i;
		}

		return -1;
	}

	static int CountOf( string text, string needle )
	{
		var count = 0;
		var at = 0;

		while ( (at = text.IndexOf( needle, at, StringComparison.Ordinal )) >= 0 )
		{
			count++;
			at += needle.Length;
		}

		return count;
	}
}
