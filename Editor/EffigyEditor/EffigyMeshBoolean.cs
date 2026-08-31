using Effigy;
using HalfEdgeMesh;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

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

	/// <summary>How many times the engine boolean has actually been asked, this editor session.
	///
	/// Zero after a cut that "did nothing" is the single most useful fact available: it means the
	/// feature never got as far as the engine, so the fault is upstream of everything in this file.
	/// Read by effigy_dump_tree.</summary>
	public static int CallCount;

	/// <summary>What happened on the last call, for the same diagnostic.</summary>
	public static string LastOutcome;

	public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
	{
		result = null;
		error = null;

		CallCount++;
		LastOutcome = $"{op} in progress";

		var a = ToPolygonMesh( target );
		var b = ToPolygonMesh( tool );

		if ( a is null || b is null )
		{
			// Both inputs reached here through the kernel, so an empty one means an earlier feature
			// produced nothing and did not say so — worth a real message rather than a crash.
			error = "one of the solids has no faces";
			LastOutcome = $"{op}: {error}";
			return false;
		}

		if ( !a.PerformBoolean( b, Transform.Zero, Operation( op ) ) )
		{
			// The engine's own answer for "these two meshes could not be combined". It does not say
			// why, so neither can this — the kernel wraps it with which operation was being tried.
			error = "the engine's boolean rejected these two solids - they may not overlap, or the "
				+ "geometry may be self-intersecting";
			LastOutcome = $"{op}: engine refused";
			return false;
		}

		// The boolean cuts faces into new ones that carry no texture coordinates. Same call, same
		// place in the sequence, as BooleanTool.
		a.ComputeFaceTextureCoordinatesFromParameters();

		result = ToPolyMesh( a );

		// PUT BACK THE OPENINGS THE ENGINE COULD NOT DESCRIBE. PolygonMesh has no way to express a
		// face with a hole in it and no API to ask for one, so the face a cut entered through comes
		// back as its outer contour alone and the opening survives only as a ring of boundary edges
		// nothing closes. Left alone that renders as a tunnel with a lid on it - the exact symptom
		// that made a working boolean look like a broken one. See MeshHoleRepair.
		var reopened = MeshHoleRepair.CloseBoundaryLoopsIntoFaces( result );

		if ( result.FaceCount == 0 )
		{
			// MeshBoolean.Apply turns an empty result into its own message, which is a better one
			// than anything available here, so this reports success and lets it.
			return true;
		}

		TransferMaterials( result, target, tool );

		LastOutcome = $"{op}: ok, {result.VertexCount} verts / {result.FaceCount} faces"
			+ $", {reopened} opening(s) reinstated";

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
	/// concerned.
	///
	/// WITHIN A TOLERANCE, NOT EXACTLY. This used to weld on exact float equality, justified by a
	/// comment claiming the floats "come straight back out of the engine untouched, so a shared
	/// corner is bit-identical rather than merely close". That is true of a corner the boolean
	/// merely copied and false of one it CALCULATED: an intersection vertex reached along two
	/// different edges is computed twice, and the two answers agree to about six digits rather than
	/// to the last bit. Welding exactly then leaves two vertices a hair apart where there should be
	/// one, every edge through them is claimed by one face rather than two, and the mesh reads as
	/// open along a seam that looks perfectly closed.
	/// </summary>
	static PolyMesh ToPolyMesh( PolygonMesh polygon )
	{
		var mesh = new PolyMesh();

		// Quantised to a grid one tolerance across, then the 27 surrounding cells are searched, so
		// two points either side of a cell boundary still find each other. A plain rounded key
		// would weld most pairs and miss precisely the ones that straddle a boundary — the same
		// "works until it doesn't" this replaces.
		var buckets = new Dictionary<(int, int, int), List<int>>();

		WeldCount = 0;

		foreach ( var face in polygon.FaceHandles )
		{
			var corners = polygon.GetFaceVertices( face );

			if ( corners is null || corners.Length < 3 )
				continue;

			var indices = new int[corners.Length];

			for ( var i = 0; i < corners.Length; i++ )
			{
				var p = polygon.GetVertexPosition( corners[i] );

				indices[i] = Weld( mesh, buckets, new Vec3( p.x, p.y, p.z ) );
			}

			AddFaceSplittingBridges( mesh, indices, TextureCoords( polygon, face, corners.Length ) );
		}

		// The Skin is deliberately NOT carried across. Skinning is per-vertex and a boolean does not
		// preserve vertices — it splits, merges and invents them — so the weights on the way in say
		// nothing about the vertices on the way out. Rigging a cut body means re-binding it.

		return mesh;
	}

	/// <summary>How close two positions must be to be the same vertex. Effigy works at model scale
	/// in inches, so a ten-thousandth is far below anything anyone draws and far above the drift of
	/// a boolean recomputing one intersection twice.</summary>
	const float WeldTolerance = 1e-4f;

	/// <summary>How many vertices the last conversion merged that exact equality would have left
	/// apart. Reported by effigy_dump_tree: a non-zero count is a seam that would otherwise have
	/// read as an open edge.</summary>
	public static int WeldCount;

	static int Weld( PolyMesh mesh, Dictionary<(int, int, int), List<int>> buckets, Vec3 v )
	{
		var cell = Cell( v );

		for ( var dx = -1; dx <= 1; dx++ )
		for ( var dy = -1; dy <= 1; dy++ )
		for ( var dz = -1; dz <= 1; dz++ )
		{
			if ( !buckets.TryGetValue( (cell.Item1 + dx, cell.Item2 + dy, cell.Item3 + dz), out var candidates ) )
				continue;

			foreach ( var index in candidates )
			{
				var q = mesh.Positions[index];

				if ( MathF.Abs( q.x - v.x ) > WeldTolerance
					|| MathF.Abs( q.y - v.y ) > WeldTolerance
					|| MathF.Abs( q.z - v.z ) > WeldTolerance )
					continue;

				// Anything not bit-identical is a pair exact equality would have missed, which is
				// the entire reason this exists.
				if ( q.x != v.x || q.y != v.y || q.z != v.z )
					WeldCount++;

				return index;
			}
		}

		var added = mesh.AddVertex( v );

		if ( !buckets.TryGetValue( cell, out var bucket ) )
		{
			bucket = new List<int>( 4 );
			buckets[cell] = bucket;
		}

		bucket.Add( added );

		return added;
	}

	static (int, int, int) Cell( Vec3 v ) => (
		(int)MathF.Floor( v.x / WeldTolerance ),
		(int)MathF.Floor( v.y / WeldTolerance ),
		(int)MathF.Floor( v.z / WeldTolerance ) );

	/// <summary>
	/// Add one face from the engine, splitting it into triangles first if it is BRIDGED.
	///
	/// THIS IS WHAT MAKES A CUT HOLE ACTUALLY APPEAR. A half-edge mesh cannot express a face with a
	/// hole in it - a face is one closed loop of half-edges - so the engine returns a holed face as
	/// a single loop that runs out to the inner boundary and back along the same seam, visiting the
	/// two seam vertices twice. That is a bridge, and it is a perfectly good description of a hole.
	///
	/// PolyMesh does not accept one. MeshValidator errors on a face that repeats a vertex, and
	/// nothing in this kernel produces such a face: Effigy's own holed caps go through
	/// Triangulate.WithHoles and are stored as TRIANGLES for exactly this reason - see the "profiles
	/// with holes" section of Effigy's README, which explains the same trade being made there.
	///
	/// So a bridged n-gon handed straight to AddFace produced an invalid mesh that looked plausible
	/// everywhere and was wrong in the one way that mattered: ObjWriter emits an n-gon verbatim, so
	/// the OBJ carried a self-touching 30-gon that Blender filled solid, and the cut's tunnel was
	/// there while its opening was not. The hole had arrived and was being painted over.
	///
	/// Ear clipping is bridge-aware already - IsEar refuses a zero-area corner, which is the whole
	/// reason WithHoles can splice a hole in along a seam and then clip normally - so the loop only
	/// needs handing to it. Degenerate triangles are dropped rather than added: a corner using both
	/// visits of a seam vertex would rebuild the very defect being removed.
	///
	/// Unbridged faces are passed through untouched and keep their n-gon, which is what preserves
	/// the quads the subdivision cage needs.
	/// </summary>
	static void AddFaceSplittingBridges( PolyMesh mesh, int[] indices, Vec2[] uvs )
	{
		if ( indices.Distinct().Count() == indices.Length )
		{
			mesh.AddFace( indices, uvs );
			return;
		}

		var positions = new List<Vec3>( indices.Length );

		foreach ( var index in indices )
			positions.Add( mesh.Positions[index] );

		// TWO N-GONS FIRST, triangles only if that fails.
		//
		// Triangulating is correct and it is expensive in the currency the user actually spends. A
		// Face is the unit of selection and of material assignment, so a 24-gon cap with a pocket cut
		// into it came back as 29 triangles and clicking it to paint it painted ONE of them. Splitting
		// the ring on a second bridge gives two n-gons instead, which is the fewest a face with a hole
		// in it can ever be - a face is one loop of corners, so one is not on offer at any price.
		//
		// The splitter refuses anything it is not certain of and says so by returning null, because a
		// wrong split is a self-intersecting face that is closed, manifold and Euler-correct. Falling
		// through to the triangulator is never wrong, only coarse.
		if ( TrySplitIntoFaces( mesh, indices, uvs, positions ) )
			return;

		// BridgedFace, not Face. Face routes to the simple-polygon ear clipper, which does not fail
		// on a bridged loop - it returns an overlapping fan that covers the hole back in. See
		// Triangulate.BridgedLoop.
		foreach ( var (a, b, c) in Triangulate.BridgedFace( positions ) )
		{
			var ia = indices[a];
			var ib = indices[b];
			var ic = indices[c];

			// Both visits of a seam vertex landing in one triangle is a zero-area sliver, and it
			// would repeat a vertex exactly the way the bridge did.
			if ( ia == ib || ib == ic || ia == ic )
				continue;

			mesh.AddFace( new[] { ia, ib, ic },
				uvs is null ? null : new[] { uvs[a], uvs[b], uvs[c] } );
		}
	}

	/// <summary>
	/// Rebuild a bridged face as two n-gons, or report that it cannot be done and change nothing.
	///
	/// EVERY FACE IS BUILT BEFORE ANY IS ADDED. A split that turns out bad halfway through would
	/// otherwise leave one good face in the mesh and the caller triangulating the same corners on top
	/// of it, which is a doubled surface rather than a fallback.
	/// </summary>
	static bool TrySplitIntoFaces( PolyMesh mesh, int[] indices, Vec2[] uvs, List<Vec3> positions )
	{
		var loops = Triangulate.SplitBridgedFace( positions );

		if ( loops is null )
			return false;

		var faces = new List<(int[] Indices, Vec2[] UVs)>( loops.Count );

		foreach ( var loop in loops )
		{
			var faceIndices = new int[loop.Count];
			var faceUVs = uvs is null ? null : new Vec2[loop.Count];

			for ( var i = 0; i < loop.Count; i++ )
			{
				faceIndices[i] = indices[loop[i]];

				if ( faceUVs is not null )
					faceUVs[i] = uvs[loop[i]];
			}

			// The entire purpose of this path is to stop handing PolyMesh a face that repeats a
			// vertex. One that does it anyway is worse than no split at all. Note this checks the MESH
			// indices, not the loop's: two corners the mesh weld already merged are one vertex here
			// however distinct they looked to the splitter.
			if ( faceIndices.Length < 3 || faceIndices.Distinct().Count() != faceIndices.Length )
				return false;

			faces.Add( (faceIndices, faceUVs) );
		}

		foreach ( var (faceIndices, faceUVs) in faces )
			mesh.AddFace( faceIndices, faceUVs );

		return true;
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
