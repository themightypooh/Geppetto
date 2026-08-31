using Effigy;
using HalfEdgeMesh;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// The engine-backed mesh boolean — Effigy's <see cref="IMeshBoolean"/> sitting on top of
/// <c>Sandbox.PolygonMesh.PerformBoolean</c>. This is the adapter EffigyBooleanProbe was written to
/// make writable, and the piece Remove has been waiting on.
///
/// WRITTEN FROM THE ENGINE'S OWN CALL SITE, not from a guess. Facepunch's Boolean tool
/// (addons/tools/Code/Scene/Mesh/Tools/BooleanTool.cs) does this in six lines, and every decision
/// here is copied from it rather than reasoned out from a distance: PerformBoolean MUTATES its
/// receiver and returns whether it worked, the relative transform is how the second mesh is placed
/// against the first, and the UVs have to be recomputed afterwards because the boolean produces
/// faces that never had any. The probe's reflection dump supplied the rest — how a vertex goes in
/// (AddVertex), how a face goes in (AddFace over the returned handles), and how both come back out.
///
/// Transform.Zero is the relative transform because both PolyMeshes are already in the same part
/// studio space. BooleanTool needs A.WorldTransform.ToLocal( B.WorldTransform ) only because its
/// two meshes hang off different GameObjects; there is no such gap here.
/// </summary>
public sealed class EffigyMeshBoolean : IMeshBoolean
{
	/// <summary>
	/// Put this in front of the kernel, once.
	///
	/// Idempotent and non-clobbering: the tests install their own stub into the same slot, and a
	/// second window opening must not walk over a provider someone deliberately set.
	/// </summary>
	public static void Install()
	{
		MeshBoolean.Provider ??= new EffigyMeshBoolean();
	}

	public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
	{
		result = null;
		error = null;

		var a = ToPolygonMesh( target );
		var b = ToPolygonMesh( tool );

		if ( a is null || b is null )
		{
			// Both inputs reached here through the kernel, so an empty one means an earlier feature
			// produced nothing and did not say so — worth a real message rather than a crash.
			error = "one of the solids has no faces";
			return false;
		}

		if ( !a.PerformBoolean( b, Transform.Zero, Operation( op ) ) )
		{
			// The engine's own answer for "these two meshes could not be combined". It does not say
			// why, so neither can this — the kernel wraps it with which operation was being tried.
			error = "the engine's boolean rejected these two solids - they may not overlap, or the "
				+ "geometry may be self-intersecting";
			return false;
		}

		// The boolean cuts faces into new ones that carry no texture coordinates. Same call, same
		// place in the sequence, as BooleanTool.
		a.ComputeFaceTextureCoordinatesFromParameters();

		result = ToPolyMesh( a );

		if ( result.FaceCount == 0 )
		{
			// MeshBoolean.Apply turns an empty result into its own message, which is a better one
			// than anything available here, so this reports success and lets it.
			return true;
		}

		TransferMaterials( result, target, tool );

		return true;
	}

	static PolygonMesh.BooleanOperation Operation( BooleanOp op ) => op switch
	{
		BooleanOp.Union => PolygonMesh.BooleanOperation.Union,
		BooleanOp.Subtract => PolygonMesh.BooleanOperation.Subtract,
		BooleanOp.Intersect => PolygonMesh.BooleanOperation.Intersect,
		_ => throw new ArgumentOutOfRangeException( nameof( op ), op, "unknown boolean operation" )
	};

	// --- PolyMesh -> PolygonMesh ------------------------------------------------------------------

	/// <summary>Null for a mesh with nothing in it, which the caller turns into an error — an empty
	/// PolygonMesh would go through the boolean and come back empty, losing the reason.</summary>
	static PolygonMesh ToPolygonMesh( PolyMesh mesh )
	{
		if ( mesh is null || mesh.FaceCount == 0 || mesh.VertexCount == 0 )
			return null;

		var polygon = new PolygonMesh();

		var handles = new VertexHandle[mesh.VertexCount];

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			handles[i] = polygon.AddVertex( new Vector3( p.x, p.y, p.z ) );
		}

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			var corners = new VertexHandle[face.Count];

			for ( var i = 0; i < face.Count; i++ )
				corners[i] = handles[face.Indices[i]];

			polygon.AddFace( corners );
		}

		return polygon;
	}

	// --- PolygonMesh -> PolyMesh ------------------------------------------------------------------

	/// <summary>
	/// Read the result back out, welding by POSITION rather than by vertex handle.
	///
	/// The handles are the engine's own bookkeeping and nothing here can map one back to an index —
	/// VertexHandleFromIndex goes one way only. Position works because the two vertices a boolean
	/// leaves at the same coordinate are the same vertex as far as every consumer downstream is
	/// concerned, and exact equality is safe here: these floats come straight back out of the
	/// engine untouched, so a shared corner is bit-identical rather than merely close.
	/// </summary>
	static PolyMesh ToPolyMesh( PolygonMesh polygon )
	{
		var mesh = new PolyMesh();

		var indexOf = new Dictionary<Vec3, int>();

		foreach ( var face in polygon.FaceHandles )
		{
			var corners = polygon.GetFaceVertices( face );

			if ( corners is null || corners.Length < 3 )
				continue;

			var indices = new int[corners.Length];

			for ( var i = 0; i < corners.Length; i++ )
			{
				var p = polygon.GetVertexPosition( corners[i] );
				var v = new Vec3( p.x, p.y, p.z );

				if ( !indexOf.TryGetValue( v, out var index ) )
				{
					index = mesh.AddVertex( v );
					indexOf[v] = index;
				}

				indices[i] = index;
			}

			mesh.AddFace( indices, TextureCoords( polygon, face, corners.Length ) );
		}

		// The Skin is deliberately NOT carried across. Skinning is per-vertex and a boolean does not
		// preserve vertices — it splits, merges and invents them — so the weights on the way in say
		// nothing about the vertices on the way out. Rigging a cut body means re-binding it.

		return mesh;
	}

	/// <summary>Null rather than a wrong-length array when the engine has nothing to give: Face
	/// treats null UVs as "none", where a mismatched array is a bug that surfaces at export.</summary>
	static Vec2[] TextureCoords( PolygonMesh polygon, FaceHandle face, int corners )
	{
		var coords = polygon.GetFaceTextureCoords( face );

		if ( coords is null || coords.Length != corners )
			return null;

		var uvs = new Vec2[corners];

		for ( var i = 0; i < corners; i++ )
			uvs[i] = new Vec2( coords[i].x, coords[i].y );

		return uvs;
	}

	// --- material slots -------------------------------------------------------------------------

	/// <summary>How far off a source face's plane a result face may sit and still be counted as
	/// lying in it. Effigy works in inches at model scale, where a hundredth is far below anything
	/// anyone draws and far above the drift of a boolean's arithmetic.</summary>
	const float PlaneTolerance = 0.01f;

	/// <summary>Two normals count as the same plane's above this. Deliberately not 1.0 — the
	/// boolean rebuilds a face's normal from new corners, so it lands very close, not exactly.
	/// </summary>
	const float NormalTolerance = 0.999f;

	/// <summary>
	/// Give every result face the material slot of the input face it came from.
	///
	/// GEOMETRICALLY RATHER THAN THROUGH THE ENGINE, because there is no channel to carry a slot
	/// through PerformBoolean that does not involve guessing at engine behaviour. SetFaceMaterial
	/// takes a real Material, not an integer, and whether a face attribute survives being split by a
	/// boolean is not something the probe can answer or this code should assume. What IS certain is
	/// the geometry: every face a boolean emits lies in the plane of one of the faces that went in,
	/// so matching plane and orientation finds its source, and the nearest centroid picks between
	/// several faces sharing one plane.
	///
	/// The comparison is on |dot| rather than dot, which matters for Subtract: the walls of the hole
	/// are the tool's faces turned around to face into the cavity, and they should take the tool's
	/// slot even though their normals now point the other way.
	///
	/// A face that matches nothing keeps slot 0, which is what a new face gets anyway.
	/// </summary>
	static void TransferMaterials( PolyMesh result, PolyMesh target, PolyMesh tool )
	{
		var sources = new List<(Vec3 Centroid, Vec3 Normal, int Material)>();

		Collect( sources, target );
		Collect( sources, tool );

		// Nothing to carry: every face that went in was on the default slot, and every face coming
		// out is already there.
		var interesting = false;

		foreach ( var source in sources )
		{
			if ( source.Material == 0 )
				continue;

			interesting = true;
			break;
		}

		if ( !interesting )
			return;

		foreach ( var face in result.Faces )
		{
			var centroid = result.FaceCentroid( face );
			var normal = result.FaceNormal( face );

			var best = -1;
			var bestDistance = float.MaxValue;

			foreach ( var source in sources )
			{
				if ( MathF.Abs( Vec3.Dot( normal, source.Normal ) ) < NormalTolerance )
					continue;

				if ( MathF.Abs( Vec3.Dot( centroid - source.Centroid, source.Normal ) ) > PlaneTolerance )
					continue;

				var distance = (centroid - source.Centroid).LengthSquared;

				if ( distance >= bestDistance )
					continue;

				bestDistance = distance;
				best = source.Material;
			}

			if ( best >= 0 )
				face.Material = best;
		}
	}

	static void Collect( List<(Vec3 Centroid, Vec3 Normal, int Material)> sources, PolyMesh mesh )
	{
		if ( mesh is null )
			return;

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			sources.Add( (mesh.FaceCentroid( face ), mesh.FaceNormal( face ), face.Material) );
		}
	}

	// --- checking it from the console -----------------------------------------------------------

	/// <summary>
	/// Run all three operations on two overlapping boxes and report what came back.
	///
	/// The adapter cannot be unit tested where the rest of the kernel is: Effigy.Tests is plain .NET
	/// with no engine to call, which is the whole reason IMeshBoolean is an interface and the tests
	/// install a stub. So the check that the real one works has to happen in the editor, and a
	/// console command is the cheapest place to put it — same reasoning as effigy_probe_boolean,
	/// which is still there for the next engine API that needs reading rather than guessing.
	///
	/// Two unit boxes offset by half their width overlap in an eighth of their volume, so all three
	/// operations have something real to do and each has an arithmetic answer to check against:
	/// union is bigger than either, intersect is smaller, subtract sits between. Any of them coming
	/// back open means the boolean produced a shell rather than a solid.
	/// </summary>
	[ConCmd( "effigy_test_boolean" )]
	public static void Test()
	{
		Install();

		var a = Primitives.Box( 1f, 1f, 1f );
		var b = MeshTransform.Transformed( Primitives.Box( 1f, 1f, 1f ), Xform.Translate( new Vec3( 0.5f, 0.5f, 0.5f ) ) );

		Log.Info( $"[effigy] provider: {MeshBoolean.Provider?.GetType().Name ?? "none"}" );
		Log.Info( $"[effigy] target  : {a.VertexCount} verts, {a.FaceCount} faces, {MeshValidator.Validate( a )}" );
		Log.Info( $"[effigy] tool    : {b.VertexCount} verts, {b.FaceCount} faces, {MeshValidator.Validate( b )}" );

		foreach ( var op in new[] { BooleanOp.Union, BooleanOp.Subtract, BooleanOp.Intersect } )
		{
			try
			{
				// Through MeshBoolean.Apply rather than straight at the provider, so this exercises
				// the path a feature actually takes, empty-result message and all.
				var result = MeshBoolean.Apply( op, a, b );

				Log.Info( $"[effigy] {op,-9}: {result.VertexCount} verts, {result.FaceCount} faces, "
					+ $"{MeshValidator.Validate( result )}" );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[effigy] {op,-9}: {e.Message}" );
			}
		}
	}
}
