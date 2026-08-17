using System;
using System.Collections.Generic;
using Sandbox;

// Shared geometry/material conversion logic, factored out of HaloRenderModelLoader so
// HaloBspLoader (level geometry) can reuse it without duplicating -- both RenderModelTag and
// ScenarioStructureBspTag produce the exact same Reclaimer Model/Mesh/Segment shape via
// GetContent(), so the actual mesh-building code doesn't care which kind of tag it came from.
internal static class HaloMeshConverter
{
	const float HaloWorldUnitToInches = 120f;

	public static List<Mesh> ConvertMesh( dynamic reclaimerMesh, ref int materialIndex )
	{
		var results = new List<Mesh>();

		dynamic vertexBuffer = reclaimerMesh.VertexBuffer;
		dynamic indexBuffer = reclaimerMesh.IndexBuffer;

		int vertexCount = (int)vertexBuffer.Count;
		if ( vertexCount == 0 || indexBuffer is null )
			return results;

		bool hasNormals = (bool)vertexBuffer.HasNormals;
		bool hasUVs = (bool)vertexBuffer.HasTextureCoordinates;

		dynamic positions = vertexBuffer.PositionChannels[0];
		dynamic normals = hasNormals ? vertexBuffer.NormalChannels[0] : null;
		dynamic uvs = hasUVs ? vertexBuffer.TextureCoordinateChannels[0] : null;

		// Positions/UVs are stored normalized [0,1] per-axis against the mesh's own
		// PositionBounds/TextureBounds -- NOT already real-world values. Reclaimer's own
		// viewer (Controls/DirectX/MeshLoader.cs) expands via
		// PositionBounds.CreateExpansionMatrix(); since that matrix is just a diagonal
		// scale + translate (no rotation), it's equivalent to lerping each axis from its
		// own Min/Max independently. Skipping this is what produced the squished pistol --
		// a flat uniform multiplier can't fix a per-axis normalized-to-real mismatch.
		dynamic posBounds = reclaimerMesh.PositionBounds;
		bool posCompressed = !(bool)posBounds.IsEmpty;
		dynamic posMin = posBounds.Min;
		var positionMin = new Vector3( (float)posMin.X, (float)posMin.Y, (float)posMin.Z );
		var positionScale = posCompressed
			? new Vector3( (float)posBounds.XLength, (float)posBounds.YLength, (float)posBounds.ZLength )
			: Vector3.One;

		dynamic uvBounds = reclaimerMesh.TextureBounds;
		bool uvCompressed = hasUVs && !(bool)uvBounds.IsEmpty;
		dynamic uvMin = uvBounds.Min;
		var uvMinVec = new Vector2( (float)uvMin.X, (float)uvMin.Y );
		var uvScale = uvCompressed
			? new Vector2( (float)uvBounds.XLength, (float)uvBounds.YLength )
			: Vector2.One;

		var positionsSb = new Vector3[vertexCount];
		var normalsSb = new Vector3[vertexCount];
		var uvsSb = new Vector2[vertexCount];

		for ( var i = 0; i < vertexCount; i++ )
		{
			dynamic p = positions[i];
			var raw = new Vector3( (float)p.X, (float)p.Y, (float)p.Z );
			var real = posCompressed ? positionMin + raw * positionScale : raw;
			positionsSb[i] = real * HaloWorldUnitToInches;

			var normal = Vector3.Up;
			if ( hasNormals )
			{
				dynamic n = normals[i];
				normal = new Vector3( (float)n.X, (float)n.Y, (float)n.Z );
			}
			normalsSb[i] = normal;

			var uv = Vector2.Zero;
			if ( hasUVs )
			{
				dynamic uvVal = uvs[i];
				var rawUv = new Vector2( (float)uvVal.X, (float)uvVal.Y );
				uv = uvCompressed ? uvMinVec + rawUv * uvScale : rawUv;
			}
			uvsSb[i] = uv;
		}

		var meshBounds = BBox.FromPoints( positionsSb );

		// Halo3 index buffers are usually triangle strips -- unwind to a triangle list.
		string layout = indexBuffer.Layout?.ToString() ?? "";
		bool needsUnstrip = layout.Contains( "Strip" ) || layout.Contains( "Default" );

		var rawIndices = new List<int>( (int)indexBuffer.Count );
		for ( var i = 0; i < (int)indexBuffer.Count; i++ )
			rawIndices.Add( (int)indexBuffer[i] );

		// Segments are Halo's submeshes -- each has its own shader/material and its own
		// slice of the shared index buffer. Strips don't continue across segment
		// boundaries, so each segment's slice gets unstripped independently. Collect them all
		// first (rather than building meshes immediately) since tangent computation below
		// needs every triangle that touches each shared vertex, across all segments.
		var segments = new List<(dynamic material, List<int> triangles)>();
		foreach ( dynamic segment in reclaimerMesh.Segments )
		{
			int indexStart = (int)segment.IndexStart;
			int indexLength = (int)segment.IndexLength;
			if ( indexLength <= 0 || indexStart + indexLength > rawIndices.Count )
				continue;

			var segmentRaw = rawIndices.GetRange( indexStart, indexLength );
			var triangleIndices = needsUnstrip ? UnstripTriangles( segmentRaw ) : segmentRaw;

			if ( triangleIndices.Count < 3 )
				continue;

			segments.Add( (segment.Material, triangleIndices) );
		}

		var tangentsSb = ComputeTangents( vertexCount, positionsSb, normalsSb, uvsSb, segments );

		var vertices = new List<SimpleVertex>( vertexCount );
		for ( var i = 0; i < vertexCount; i++ )
			vertices.Add( new SimpleVertex( positionsSb[i], normalsSb[i], tangentsSb[i], uvsSb[i] ) );

		foreach ( var (reclaimerMaterial, triangleIndices) in segments )
		{
			var material = BuildMaterial( reclaimerMaterial, materialIndex++ );

			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( triangleIndices.Count, triangleIndices );
			mesh.Bounds = meshBounds;

			results.Add( mesh );
		}

		return results;
	}

	// Standard per-triangle tangent accumulation (Lengyel's method) from position + UV deltas,
	// then Gram-Schmidt orthogonalized against each vertex's normal. Needed because a normal
	// map is meaningless without a tangent-space basis to orient it in -- leaving this at zero
	// (as earlier versions did) makes the tangent-to-world transform degenerate.
	static Vector3[] ComputeTangents( int vertexCount, Vector3[] positions, Vector3[] normals, Vector2[] uvs, List<(dynamic material, List<int> triangles)> segments )
	{
		var accum = new Vector3[vertexCount];

		foreach ( var (_, triangles) in segments )
		{
			for ( var t = 0; t + 2 < triangles.Count; t += 3 )
			{
				int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
				if ( i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount )
					continue;

				var edge1 = positions[i1] - positions[i0];
				var edge2 = positions[i2] - positions[i0];
				var deltaUv1 = uvs[i1] - uvs[i0];
				var deltaUv2 = uvs[i2] - uvs[i0];

				var denom = deltaUv1.x * deltaUv2.y - deltaUv2.x * deltaUv1.y;
				if ( MathF.Abs( denom ) < 1e-8f )
					continue;

				var f = 1f / denom;
				var tangent = ( edge1 * deltaUv2.y - edge2 * deltaUv1.y ) * f;

				accum[i0] += tangent;
				accum[i1] += tangent;
				accum[i2] += tangent;
			}
		}

		var result = new Vector3[vertexCount];
		for ( var i = 0; i < vertexCount; i++ )
		{
			var n = normals[i];
			var t = accum[i] - n * Vector3.Dot( n, accum[i] ); // Gram-Schmidt orthogonalize

			result[i] = t.LengthSquared > 1e-10f ? t.Normal : ArbitraryPerpendicular( n );
		}

		return result;
	}

	static Vector3 ArbitraryPerpendicular( Vector3 n )
	{
		var fallback = MathF.Abs( n.x ) < 0.9f ? Vector3.Right : Vector3.Up;
		return Vector3.Cross( n, fallback ).Normal;
	}

	public static List<int> UnstripTriangles( List<int> strip )
	{
		var result = new List<int>();
		int position = 0, i0 = 0, i1 = 0, i2 = 0;

		foreach ( var index in strip )
		{
			(i0, i1, i2) = (i1, i2, index);

			if ( position++ < 2 || i0 == i1 || i0 == i2 || i1 == i2 )
				continue;

			result.Add( i0 );

			if ( position % 2 == 1 )
			{
				result.Add( i1 );
				result.Add( i2 );
			}
			else
			{
				result.Add( i2 );
				result.Add( i1 );
			}
		}

		return result;
	}

	// Gave up chasing Halo's actual textures -- BC7 decode produced plausible-looking bytes
	// (verified by sampling) but still rendered flat black through complex.shader regardless
	// of alpha/mip/PBR-default tweaks, and it wasn't worth further burning time on. Own
	// procedural gunmetal materials instead (ProceduralMetal.cs) -- brushed-metal noise +
	// machined groove lines baked into a real normal map and roughness variation, not just a
	// flat colour.
	static readonly (byte r, byte g, byte b)[] GunmetalPalette =
	[
		(58, 58, 62),   // dark gunmetal body
		(24, 24, 26),   // near-black grip/rubber
		(110, 110, 116) // lighter brushed-metal highlight parts
	];

	const int MaterialTextureSize = 512;

	public static Material BuildMaterial( dynamic reclaimerMaterial, int index )
	{
		var material = Material.Create( $"halo_mat_{index}", "shaders/complex.shader" );
		material.Set( "g_flMetalness", 0.6f );

		// Real Halo texture first -- this combination (actual BC7-decoded diffuse + the
		// correct "g_tColor" raw parameter name) was never actually tried together. Earlier
		// attempts used "TextureColor" (the .vmat FILE-format friendly name, confirmed from
		// this project's own vmat files e.g. AccentA.vmat) with the real texture and got flat
		// black; then switched to procedural before "g_tColor" was found to be the real
		// code-level name. Falling back to procedural gunmetal only if a real texture can't be
		// found or decoded, not as the default.
		var albedoTex = TryDecodeRealTexture( reclaimerMaterial );

		Texture normalTex, roughTex;
		if ( albedoTex is not null )
		{
			(normalTex, roughTex) = (null, null);
		}
		else
		{
			var (r, g, b) = GunmetalPalette[index % GunmetalPalette.Length];
			var (albedo, normal, roughness) = ProceduralMetal.Generate( MaterialTextureSize, r, g, b, seed: index * 7919 + 1 );

			albedoTex = Texture.Create( MaterialTextureSize, MaterialTextureSize ).WithData( albedo ).Finish();
			normalTex = Texture.Create( MaterialTextureSize, MaterialTextureSize ).WithData( normal ).Finish();
			roughTex = Texture.Create( MaterialTextureSize, MaterialTextureSize ).WithData( roughness ).Finish();
		}

		material.Set( "TextureColor", albedoTex );
		material.Set( "g_tColor", albedoTex );
		if ( normalTex is not null )
		{
			material.Set( "TextureNormal", normalTex );
			material.Set( "TextureRoughness", roughTex );
		}

		return material;
	}

	static Texture TryDecodeRealTexture( dynamic reclaimerMaterial )
	{
		if ( reclaimerMaterial is null )
			return null;

		try
		{
			dynamic diffuseMapping = null;
			foreach ( dynamic mapping in reclaimerMaterial.TextureMappings )
			{
				string usage = mapping.Usage;
				diffuseMapping ??= mapping;
				if ( usage == "diffuse" )
				{
					diffuseMapping = mapping;
					break;
				}
			}

			if ( diffuseMapping is null )
				return null;

			dynamic reclaimerTexture = diffuseMapping.Texture;
			if ( reclaimerTexture is null )
				return null;

			dynamic dds = reclaimerTexture.GetDds();
			if ( dds is null )
				return null;

			dynamic uncompressed = dds.AsUncompressed();
			int width = (int)uncompressed.Width;
			int height = (int)uncompressed.Height;
			byte[] bgra = (byte[])uncompressed.CopyPixelData();

			int expected = width * height * 4;
			if ( width <= 0 || height <= 0 || bgra.Length < expected )
				return null;

			var rgba = new byte[expected];
			for ( var i = 0; i < expected; i += 4 )
			{
				rgba[i + 0] = bgra[i + 2]; // R
				rgba[i + 1] = bgra[i + 1]; // G
				rgba[i + 2] = bgra[i + 0]; // B
				rgba[i + 3] = 255;         // force opaque -- Halo often packs a gloss/spec mask
											// here, not real transparency
			}

			return Texture.Create( width, height ).WithData( rgba ).Finish();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[HaloMount] Real texture decode failed, falling back to procedural: {ex.Message}" );
			return null;
		}
	}
}
