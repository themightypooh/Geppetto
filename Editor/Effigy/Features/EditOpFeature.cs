using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// One edit-mode operation - extrude, inset, bevel, loop cut, bridge, symmetrize, dissolve - as a
/// step in the history rather than a baked result.
///
/// WHY A FEATURE AND NOT A MESH EDIT. <see cref="MeshEditFeature"/> stores the mesh an edit session
/// ended with, which is right for hand edits and useless to a build script: nothing upstream can
/// change without the edit being thrown away, and an agent cannot say "extrude the top" in a file.
/// This stores WHAT WAS ASKED - the operation, its numbers and a selection rule - and replays it
/// through <see cref="MeshEditSession"/> on every rebuild, so `set Torso sizez=40` upstream moves
/// the extrusion with it.
///
/// THE SELECTION IS A RULE, NOT A LIST. Face indices are the one thing an upstream change is sure
/// to renumber, so the rule picks by what survives: which way a face points, where it sits, how
/// near it is to a point. Indices are still accepted for the rare case they are what you mean.
/// See <see cref="MeshSelector"/> for the grammar.
///
/// NOTHING IS SILENT. A rule that picks nothing, an operation the session says changed nothing,
/// a value the session had to hold to its range: the first two fail the feature with the reason,
/// the last warns.
/// </summary>
public sealed class EditOpFeature : Feature
{
	public override string TypeName => "Edit op";

	public override GeometryKind Accepts => GeometryKind.Body;

	public static readonly string[] Operations =
	{
		"Extrude", "Extrude individual", "Extrude along normals", "Inset", "Bevel", "Loop cut", "Bridge",
		"Symmetrize", "Dissolve", "Limited dissolve", "Delete",
	};

	public static readonly string[] Elements = { "Auto", "Faces", "Edges", "Vertices" };

	public static readonly string[] Axes = { "X", "Y", "Z" };

	public static readonly string[] Sides = { "Keep positive", "Keep negative" };

	public readonly BodySelectionParam Bodies = new( "Body" );
	public readonly ChoiceParam Op = new( "Operation", Operations );
	/// <summary>The selection rule. Empty is the operation's own default: the up-facing faces for the
	/// extrudes (extruding everything has no one direction), everything for the rest.</summary>
	public readonly StringParam Select = new( "Select", "" );

	string Rule => !string.IsNullOrWhiteSpace( Select.Value ) ? Select.Value : Op.Index <= 2 ? "normal:up" : "all";
	public readonly ChoiceParam Element = new( "Element", Elements );
	public readonly FloatParam Distance = new( "Distance", 1f );
	public readonly IntParam Segments = new( "Segments", 1, 1, 32 );
	public readonly Vec3Param At = new( "At", Vec3.Zero );
	public readonly ChoiceParam Axis = new( "Axis", Axes, 2 );
	public readonly ChoiceParam Side = new( "Side", Sides );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Op, Select, Element, Distance, Segments, At, Axis, Side };

	string OpName => Op.Value;

	protected override void Execute( FeatureContext ctx )
	{
		if ( !MeshSelector.TryParse( Rule, out var selector, out var why ) )
			FailOn( "Select", $"The selection '{Rule}' can't be read", why, MeshSelector.Help );

		var notes = new List<string>();

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			if ( body.Mesh is null || body.Mesh.FaceCount == 0 )
				Fail( $"'{body.Name ?? body.Id}' has no faces to {OpName.ToLowerInvariant()}", "The body arrived empty." );

			var session = new MeshEditSession( body.Mesh );
			var name = body.Name ?? body.Id;

			try
			{
				session = Apply( session, selector, name );
			}
			catch ( InvalidOperationException e )
			{
				Fail( $"{OpName} on '{name}' failed", e.Message, "Change the selection or the numbers" );
			}

			if ( session.LastChangedNothing )
				Fail( $"{OpName} on '{name}' changed nothing", session.Notice ?? "The operation found nothing to do.",
					"Change the selection or the numbers" );

			if ( session.Notice is not null )
				notes.Add( $"{name}: {session.Notice}" );

			body.Mesh = session.Mesh;
		}

		if ( notes.Count > 0 )
			Warn( $"{OpName} did not do all that was asked", string.Join( " ", notes ) );
	}

	/// <summary>Run the operation; returns the session holding the result (Symmetrize on Y or Z
	/// works in a turned copy, so it hands back a different one).</summary>
	MeshEditSession Apply( MeshEditSession s, MeshSelector selector, string name )
	{
		var mesh = s.Mesh;

		switch ( Op.Index )
		{
			case 0: // Extrude
			case 1: // Extrude individual
			case 2: // Extrude along normals
				if ( ElementFor( EditElement.Face ) == EditElement.Edge )
				{
					SelectEdges( s, selector, name );
					s.Extrude( Distance.Value );
					return s;
				}

				SelectFaces( s, selector, name );
				if ( Op.Index == 0 ) s.Extrude( Distance.Value );
				else if ( Op.Index == 1 ) s.ExtrudeIndividual( Distance.Value );
				else s.ExtrudeAlongNormals( Distance.Value );
				return s;

			case 3: // Inset
				SelectFaces( s, selector, name );
				s.Inset( Distance.Value );
				return s;

			case 4: // Bevel
				if ( ElementFor( EditElement.Face ) == EditElement.Edge )
					SelectEdges( s, selector, name );
				else if ( ElementFor( EditElement.Face ) == EditElement.Vertex )
				{
					SelectVertices( s, selector, name );
					s.BevelVertices( Distance.Value );
					return s;
				}
				else
					SelectFaces( s, selector, name );

				s.Bevel( Distance.Value, Segments.Clamped );
				return s;

			case 5: // Loop cut
				LoopCut( s, selector, name );
				return s;

			case 6: // Bridge
			{
				// Only rim edges can bridge, so the rule picks among the open boundary.
				var picked = selector.Edges( mesh ).Where( e => e.Value.Faces == 1 ).Select( e => e.Key ).ToList();

				if ( picked.Count == 0 )
					FailOn( "Select", $"Nothing to bridge on '{name}'",
						$"The selection '{Rule}' matches no open boundary edge, and Bridge joins two open rims. "
						+ (mesh.BuildEdgeFaces().Values.Any( f => f.Count == 1 ) ? "The body has open rims; widen the selection to reach two of them." : "The body is closed: it has no open rims at all."),
						"Delete or leave open the faces where the band should go", "Select both rims, e.g. select=boundary" );

				s.SetMode( EditElement.Edge );
				s.ClearSelection();
				s.SelectedEdges.UnionWith( picked );
				s.BridgeSelectedLoops();
				return s;
			}

			case 7: // Symmetrize
				return Symmetrize( s );

			case 8: // Dissolve
			{
				var element = ElementFor( EditElement.Edge );

				if ( element == EditElement.Vertex )
				{
					SelectVertices( s, selector, name );
					s.Dissolve();
					return s;
				}

				if ( element == EditElement.Face )
				{
					// Dissolving faces is dissolving the edges between them: the region becomes one face.
					var faces = selector.Faces( mesh ).ToHashSet();
					RequireAny( faces.Count, "face", name );

					var inner = mesh.BuildEdgeFaces().Where( e => e.Value.Count == 2 && e.Value.All( faces.Contains ) ).Select( e => e.Key ).ToList();

					if ( inner.Count == 0 )
						FailOn( "Select", $"Nothing to dissolve on '{name}'",
							$"The {faces.Count} selected face(s) share no edge, so there is nothing between them to remove.",
							"Select faces that touch each other" );

					s.SetMode( EditElement.Edge );
					s.ClearSelection();
					s.SelectedEdges.UnionWith( inner );
					s.Dissolve();
					return s;
				}

				SelectEdges( s, selector, name );
				s.Dissolve();
				return s;
			}

			case 9: // Limited dissolve
				s.ClearNotice();
				s.LimitedDissolve();
				return s;

			case 10: // Delete
			{
				var element = ElementFor( EditElement.Face );
				if ( element == EditElement.Edge ) SelectEdges( s, selector, name );
				else if ( element == EditElement.Vertex ) SelectVertices( s, selector, name );
				else SelectFaces( s, selector, name );

				if ( element == EditElement.Face && s.SelectedFaces.Count == s.Mesh.FaceCount )
					FailOn( "Select", $"Delete would remove every face of '{name}'", "The selection picks all of it, which would leave an empty body.",
						"Narrow the selection", "Remove the feature that made the body instead" );

				s.Delete();
				return s;
			}
		}

		Fail( $"'{OpName}' is not an operation", "The operation index is out of range.", $"Use one of {string.Join( ", ", Operations )}" );
		return s;
	}

	EditElement ElementFor( EditElement auto ) => Element.Index switch
	{
		1 => EditElement.Face,
		2 => EditElement.Edge,
		3 => EditElement.Vertex,
		_ => auto,
	};

	void SelectFaces( MeshEditSession s, MeshSelector selector, string name )
	{
		var faces = selector.Faces( s.Mesh ).ToList();
		RequireAny( faces.Count, "face", name, selector, s.Mesh );
		s.SetMode( EditElement.Face );
		s.ClearSelection();
		s.SelectedFaces.UnionWith( faces );
		s.ClearNotice();
	}

	void SelectEdges( MeshEditSession s, MeshSelector selector, string name )
	{
		var edges = selector.Edges( s.Mesh ).Select( e => e.Key ).ToList();
		RequireAny( edges.Count, "edge", name, selector, s.Mesh );
		s.SetMode( EditElement.Edge );
		s.ClearSelection();
		s.SelectedEdges.UnionWith( edges );
		s.ClearNotice();
	}

	void SelectVertices( MeshEditSession s, MeshSelector selector, string name )
	{
		var verts = selector.Vertices( s.Mesh ).ToList();
		RequireAny( verts.Count, "vertex", name, selector, s.Mesh );
		s.SetMode( EditElement.Vertex );
		s.ClearSelection();
		s.SelectedVertices.UnionWith( verts );
		s.ClearNotice();
	}

	void RequireAny( int count, string what, string name, MeshSelector selector = null, PolyMesh mesh = null )
	{
		if ( count > 0 )
			return;

		var hint = selector is not null && mesh is not null ? selector.Explain( mesh ) : "";
		FailOn( "Select", $"The selection '{Rule}' picks no {what} on '{name}'",
			$"{OpName} needs at least one {what} to work on. {hint}".Trim(),
			"Loosen the rule (a wider ~angle, a longer band), or check the body with describe" );
	}

	void LoopCut( MeshEditSession s, MeshSelector selector, string name )
	{
		// The ring to cut is the one crossing At: of the edges running along Axis, the one passing
		// nearest the point, cut where the point projects onto it.
		var mesh = s.Mesh;
		var axis = AxisVector( Axis.Index );
		var at = At.Value;
		var best = default( EdgeKey );
		var bestD = float.MaxValue;
		var bestT = 0.5f;
		var candidates = 0;

		foreach ( var (key, info) in selector.Edges( mesh ) )
		{
			if ( info.Faces != 2 )
				continue;

			var a = mesh.Positions[key.A];
			var b = mesh.Positions[key.B];
			var d = b - a;
			var len = d.Length;

			if ( len < 1e-6f || MathF.Abs( Vec3.Dot( d / len, axis ) ) < 0.7f )
				continue;

			var t = Vec3.Dot( at - a, d ) / (len * len);

			if ( t <= 0.02f || t >= 0.98f )
				continue;

			candidates++;
			var dist = (a + d * t - at).Length;

			if ( dist < bestD )
			{
				bestD = dist;
				best = key;
				bestT = t;
			}
		}

		if ( candidates == 0 )
			FailOn( "At", $"No edge ring on '{name}' crosses {Fmt( at )} along {Axes[Axis.Index]}",
				$"A loop cut splits the edges running along the axis ({Axes[Axis.Index]}) where the point projects onto them, "
				+ $"and none of the {(Rule == "all" ? "body's" : "selected")} edges along {Axes[Axis.Index]} spans the point. "
				+ $"The body runs {Fmt( ModelMeasure.Bounds( mesh ).Min )} to {Fmt( ModelMeasure.Bounds( mesh ).Max )}.",
				"Put at= inside the body's span along the axis", "Pick the axis the cut edges run along (axis=Z cuts a vertical cylinder across)" );

		s.LoopCut( best, bestT );
	}

	MeshEditSession Symmetrize( MeshEditSession s )
	{
		// The session mirrors across x = 0 only, so Y and Z turn the mesh until that axis is X,
		// symmetrize, and turn it back. A rotation, not a swap: a swap is a reflection and would
		// turn every face inside out.
		var turn = Axis.Index switch
		{
			1 => Xform.Rotate( new Vec3( 0, 0, 1 ), -MathF.PI / 2f ),
			2 => Xform.Rotate( new Vec3( 0, 1, 0 ), MathF.PI / 2f ),
			_ => Xform.Identity,
		};
		var back = Axis.Index switch
		{
			1 => Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2f ),
			2 => Xform.Rotate( new Vec3( 0, 1, 0 ), -MathF.PI / 2f ),
			_ => Xform.Identity,
		};

		if ( Axis.Index == 0 )
		{
			s.Symmetrize( Side.Index == 0 );
			return s;
		}

		var turned = s.Mesh.Clone();
		MeshTransform.Apply( turned, turn );
		var inner = new MeshEditSession( turned );
		inner.Symmetrize( Side.Index == 0 );

		if ( inner.LastChangedNothing )
			return inner;

		var result = inner.Mesh.Clone();
		MeshTransform.Apply( result, back );
		var done = new MeshEditSession( result );
		return done;
	}

	internal static Vec3 AxisVector( int index ) => index switch
	{
		0 => new Vec3( 1, 0, 0 ),
		1 => new Vec3( 0, 1, 0 ),
		_ => new Vec3( 0, 0, 1 ),
	};

	static string Fmt( Vec3 v ) => string.Format( CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##}", v.x, v.y, v.z );
}

/// <summary>
/// Which faces, edges or vertices of a mesh a rule picks. The grammar, as a script writes it:
///
///   all                      everything
///   index:3,5,10..20         faces or vertices by index; edges as index:3/4,4/9
///   normal:up~20             pointing within 20° (default 30) of up/down/front/back/left/right or x,y,z
///   z:10..20  height:80%..   centre within a band on x, y or z (height = z); a % is of the body's bounds
///   near:x,y,z~3             centre within 3 of a point (a script also takes near:@Torso.top~3)
///   sharp:30                 edges whose faces meet at more than 30° (a face or vertex: on such an edge)
///   boundary                 edges with one face (a face or vertex: on such an edge)
///
/// Terms joined by &amp; must all hold; | separates alternatives; ! in front of a term negates it.
/// A face is judged by its centroid and normal, an edge by its midpoint and its faces' mean normal,
/// a vertex by its position and its faces' mean normal.
/// </summary>
public sealed class MeshSelector
{
	public const string Help = "Rules: all, index:1,2,5..9, normal:up~30, z:a..b (or x:, y:, height:, % of bounds), near:x,y,z~r, sharp:30, boundary; join with & (and), | (or), ! (not)";

	enum Kind { All, Index, EdgeIndex, Normal, Band, Near, Sharp, Boundary }

	sealed class Term
	{
		public Kind Kind;
		public bool Not;
		public HashSet<int> Indices;
		public HashSet<EdgeKey> EdgeIndices;
		public Vec3 Vector;
		public float Angle = 30f;
		public int Axis;
		public float? Lo, Hi;
		public bool LoPct, HiPct;
		public float Radius;
		public string Text;
	}

	readonly List<List<Term>> _alternatives = new();

	public readonly struct EdgeInfo
	{
		public readonly int Faces;
		public EdgeInfo( int faces ) => Faces = faces;
	}

	public static bool TryParse( string text, out MeshSelector selector, out string why )
	{
		selector = new MeshSelector();
		why = null;
		text = (text ?? "").Trim();

		if ( text.Length == 0 )
			text = "all";

		foreach ( var alt in text.Split( '|' ) )
		{
			var group = new List<Term>();

			foreach ( var raw in alt.Split( '&' ) )
			{
				var t = raw.Trim();

				if ( t.Length == 0 )
				{
					why = $"'{text}' has an empty term (two & or | in a row, or one at an end).";
					return false;
				}

				if ( !TryTerm( t, out var term, out why ) )
					return false;

				group.Add( term );
			}

			selector._alternatives.Add( group );
		}

		return true;
	}

	static bool TryTerm( string t, out Term term, out string why )
	{
		term = new Term { Text = t };
		why = null;

		if ( t.StartsWith( '!' ) )
		{
			term.Not = true;
			t = t[1..].Trim();
		}

		var colon = t.IndexOf( ':' );
		var head = (colon < 0 ? t : t[..colon]).Trim().ToLowerInvariant();
		var arg = colon < 0 ? "" : t[(colon + 1)..].Trim();

		switch ( head )
		{
			case "all":
				term.Kind = Kind.All;
				return true;

			case "boundary":
				term.Kind = Kind.Boundary;
				return true;

			case "sharp":
				term.Kind = Kind.Sharp;
				if ( arg.Length > 0 && !TryF( arg, out term.Angle ) ) { why = $"sharp:{arg} - the angle is not a number."; return false; }
				return true;

			case "index":
			case "faces":
			case "face":
			case "verts":
			case "vertices":
				if ( arg.Contains( '/' ) )
				{
					term.Kind = Kind.EdgeIndex;
					term.EdgeIndices = new HashSet<EdgeKey>();

					foreach ( var pair in arg.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
					{
						var ab = pair.Split( '/' );
						if ( ab.Length != 2 || !int.TryParse( ab[0], out var a ) || !int.TryParse( ab[1], out var b ) ) { why = $"'{pair}' is not an edge a/b of two vertex indices."; return false; }
						term.EdgeIndices.Add( new EdgeKey( a, b ) );
					}

					return true;
				}

				term.Kind = Kind.Index;
				term.Indices = new HashSet<int>();

				foreach ( var part in arg.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
				{
					var range = part.Split( "..", StringSplitOptions.None );

					if ( range.Length == 2 && int.TryParse( range[0], out var lo ) && int.TryParse( range[1], out var hi ) && hi >= lo && hi - lo < 1_000_000 )
					{
						for ( var i = lo; i <= hi; i++ ) term.Indices.Add( i );
						continue;
					}

					if ( !int.TryParse( part, out var n ) ) { why = $"'{part}' in index: is not a whole number or a range a..b."; return false; }
					term.Indices.Add( n );
				}

				if ( term.Indices.Count == 0 ) { why = "index: needs at least one number."; return false; }
				return true;

			case "normal":
			{
				term.Kind = Kind.Normal;
				var dir = SplitTilde( arg, out var angleText );

				if ( angleText is not null && !TryF( angleText, out term.Angle ) ) { why = $"normal:{arg} - the angle after ~ is not a number."; return false; }

				var named = Named( dir );

				if ( named is { } v )
					term.Vector = v;
				else if ( ModelMeasure.TryParseVec3( dir, out var p ) && p.LengthSquared > 1e-12f )
					term.Vector = p.Normal;
				else { why = $"normal:{arg} - '{dir}' is not up/down/front/back/left/right or a direction x,y,z."; return false; }

				return true;
			}

			case "x":
			case "y":
			case "z":
			case "height":
			{
				term.Kind = Kind.Band;
				term.Axis = head switch { "x" => 0, "y" => 1, _ => 2 };
				var range = arg.Split( "..", StringSplitOptions.None );

				if ( range.Length != 2 ) { why = $"{head}:{arg} - a band is lo..hi, either end optional (z:10.., z:..5, z:80%..100%)."; return false; }

				if ( !TryEnd( range[0], out term.Lo, out term.LoPct ) || !TryEnd( range[1], out term.Hi, out term.HiPct ) )
				{
					why = $"{head}:{arg} - each end is a number or a percentage of the body's bounds.";
					return false;
				}

				if ( term.Lo is null && term.Hi is null ) { why = $"{head}:{arg} - give at least one end of the band."; return false; }
				return true;
			}

			case "near":
			{
				term.Kind = Kind.Near;
				var point = SplitTilde( arg, out var radiusText );

				if ( radiusText is null || !TryF( radiusText, out term.Radius ) || term.Radius <= 0f ) { why = $"near:{arg} - give a radius after ~, e.g. near:0,0,10~3."; return false; }
				if ( !ModelMeasure.TryParseVec3( point, out term.Vector ) ) { why = $"near:{arg} - '{point}' is not a point x,y,z (a script resolves @landmarks to one)."; return false; }
				return true;
			}
		}

		why = $"'{t}' is not a selection rule. {Help}.";
		return false;
	}

	static string SplitTilde( string arg, out string after )
	{
		var tilde = arg.IndexOf( '~' );
		after = tilde < 0 ? null : arg[(tilde + 1)..].Trim();
		return (tilde < 0 ? arg : arg[..tilde]).Trim();
	}

	static Vec3? Named( string dir ) => dir.ToLowerInvariant() switch
	{
		"up" or "top" or "+z" => new Vec3( 0, 0, 1 ),
		"down" or "bottom" or "-z" => new Vec3( 0, 0, -1 ),
		"front" or "+x" => new Vec3( 1, 0, 0 ),
		"back" or "-x" => new Vec3( -1, 0, 0 ),
		"left" or "+y" => new Vec3( 0, 1, 0 ),
		"right" or "-y" => new Vec3( 0, -1, 0 ),
		_ => null,
	};

	static bool TryEnd( string text, out float? value, out bool pct )
	{
		text = text.Trim();
		value = null;
		pct = false;

		if ( text.Length == 0 )
			return true;

		if ( text.EndsWith( '%' ) )
		{
			pct = true;
			text = text[..^1];
		}

		if ( !TryF( text, out var v ) )
			return false;

		value = pct ? v / 100f : v;
		return true;
	}

	static bool TryF( string text, out float v ) => float.TryParse( text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v );

	/// <summary>Whether any term needs to know about edges' faces (sharp, boundary).</summary>
	bool NeedsEdges => _alternatives.Any( g => g.Any( t => t.Kind is Kind.Sharp or Kind.Boundary ) );

	// --- evaluating ----------------------------------------------------------------------------

	sealed class Context
	{
		public PolyMesh Mesh;
		public Vec3 Min, Max;
		public Vec3[] FaceNormals;
		public Dictionary<EdgeKey, List<int>> EdgeFaces;
		public HashSet<EdgeKey> SharpEdges(float angle) => _sharp.TryGetValue( angle, out var s ) ? s : _sharp[angle] = Sharp( angle );
		readonly Dictionary<float, HashSet<EdgeKey>> _sharp = new();

		HashSet<EdgeKey> Sharp( float angle )
		{
			var cos = MathF.Cos( angle * MathF.PI / 180f );
			var set = new HashSet<EdgeKey>();
			foreach ( var (k, f) in EdgeFaces )
				if ( f.Count == 2 && Vec3.Dot( FaceNormals[f[0]], FaceNormals[f[1]] ) < cos )
					set.Add( k );
			return set;
		}
	}

	Context Prepare( PolyMesh mesh )
	{
		var (min, max) = ModelMeasure.Bounds( mesh );
		var c = new Context { Mesh = mesh, Min = min, Max = max, FaceNormals = new Vec3[mesh.FaceCount], EdgeFaces = mesh.BuildEdgeFaces() };

		for ( var f = 0; f < mesh.FaceCount; f++ )
			c.FaceNormals[f] = mesh.FaceNormal( mesh.Faces[f] );

		return c;
	}

	bool Holds( Term t, Context c, int index, EdgeKey? edge, Vec3 position, Vec3 normal, Func<Term, bool> touches )
	{
		var r = t.Kind switch
		{
			Kind.All => true,
			Kind.Index => edge is null && t.Indices.Contains( index ),
			Kind.EdgeIndex => edge is { } e && t.EdgeIndices.Contains( e ),
			Kind.Normal => normal.LengthSquared > 1e-12f && Vec3.Dot( normal.Normal, t.Vector ) >= MathF.Cos( t.Angle * MathF.PI / 180f ) - 1e-5f,
			Kind.Band => InBand( t, c, position ),
			Kind.Near => (position - t.Vector).Length <= t.Radius,
			Kind.Sharp or Kind.Boundary => touches( t ),
			_ => false,
		};

		return r != t.Not;
	}

	static bool InBand( Term t, Context c, Vec3 p )
	{
		var v = t.Axis == 0 ? p.x : t.Axis == 1 ? p.y : p.z;
		var lo = t.Axis == 0 ? c.Min.x : t.Axis == 1 ? c.Min.y : c.Min.z;
		var hi = t.Axis == 0 ? c.Max.x : t.Axis == 1 ? c.Max.y : c.Max.z;
		const float eps = 1e-4f;

		if ( t.Lo is { } a && v < (t.LoPct ? lo + (hi - lo) * a : a) - eps ) return false;
		if ( t.Hi is { } b && v > (t.HiPct ? lo + (hi - lo) * b : b) + eps ) return false;
		return true;
	}

	bool Matches( Context c, int index, EdgeKey? edge, Vec3 position, Vec3 normal, Func<Term, bool> touches ) =>
		_alternatives.Any( g => g.All( t => Holds( t, c, index, edge, position, normal, touches ) ) );

	public IEnumerable<int> Faces( PolyMesh mesh )
	{
		var c = Prepare( mesh );
		Dictionary<int, List<EdgeKey>> faceEdges = null;

		if ( NeedsEdges )
		{
			faceEdges = new Dictionary<int, List<EdgeKey>>();
			foreach ( var (k, fs) in c.EdgeFaces )
				foreach ( var f in fs )
					(faceEdges.TryGetValue( f, out var l ) ? l : faceEdges[f] = new List<EdgeKey>()).Add( k );
		}

		for ( var f = 0; f < mesh.FaceCount; f++ )
		{
			var face = f;
			bool Touch( Term t ) => faceEdges is not null && faceEdges.TryGetValue( face, out var es )
				&& es.Any( k => t.Kind == Kind.Boundary ? c.EdgeFaces[k].Count == 1 : c.SharpEdges( t.Angle ).Contains( k ) );

			if ( Matches( c, f, null, mesh.FaceCentroid( mesh.Faces[f] ), c.FaceNormals[f], Touch ) )
				yield return f;
		}
	}

	public IEnumerable<KeyValuePair<EdgeKey, EdgeInfo>> Edges( PolyMesh mesh )
	{
		var c = Prepare( mesh );

		foreach ( var (k, fs) in c.EdgeFaces )
		{
			var n = Vec3.Zero;
			foreach ( var f in fs ) n += c.FaceNormals[f];

			var key = k;
			bool Touch( Term t ) => t.Kind == Kind.Boundary ? fs.Count == 1 : c.SharpEdges( t.Angle ).Contains( key );

			if ( Matches( c, -1, k, (mesh.Positions[k.A] + mesh.Positions[k.B]) * 0.5f, n, Touch ) )
				yield return new KeyValuePair<EdgeKey, EdgeInfo>( k, new EdgeInfo( fs.Count ) );
		}
	}

	public IEnumerable<int> Vertices( PolyMesh mesh )
	{
		var c = Prepare( mesh );
		var normals = new Vec3[mesh.VertexCount];

		for ( var f = 0; f < mesh.FaceCount; f++ )
			foreach ( var v in mesh.Faces[f].Indices )
				normals[v] += c.FaceNormals[f];

		Dictionary<int, List<EdgeKey>> vertEdges = null;

		if ( NeedsEdges )
		{
			vertEdges = new Dictionary<int, List<EdgeKey>>();
			foreach ( var k in c.EdgeFaces.Keys )
			{
				(vertEdges.TryGetValue( k.A, out var la ) ? la : vertEdges[k.A] = new List<EdgeKey>()).Add( k );
				(vertEdges.TryGetValue( k.B, out var lb ) ? lb : vertEdges[k.B] = new List<EdgeKey>()).Add( k );
			}
		}

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			if ( normals[v].LengthSquared == 0f )
				continue; // not used by any face

			var vert = v;
			bool Touch( Term t ) => vertEdges is not null && vertEdges.TryGetValue( vert, out var es )
				&& es.Any( k => t.Kind == Kind.Boundary ? c.EdgeFaces[k].Count == 1 : c.SharpEdges( t.Angle ).Contains( k ) );

			if ( Matches( c, v, null, mesh.Positions[v], normals[v], Touch ) )
				yield return v;
		}
	}

	/// <summary>Why a rule picked nothing, term by term: how many faces each term on its own would
	/// pick, and for a normal rule how close the nearest face came. What turns "picks nothing" into
	/// what to change.</summary>
	public string Explain( PolyMesh mesh )
	{
		var c = Prepare( mesh );
		var parts = new List<string>();

		foreach ( var term in _alternatives.SelectMany( g => g ) )
		{
			var single = new MeshSelector();
			single._alternatives.Add( new List<Term> { term } );
			var count = single.Faces( mesh ).Count();
			var line = $"'{term.Text}' alone picks {count} of {mesh.FaceCount} faces";

			if ( term.Kind == Kind.Normal && !term.Not && count == 0 && mesh.FaceCount > 0 )
			{
				var bestDot = c.FaceNormals.Max( n => Vec3.Dot( n, term.Vector ) );
				line += $" (the closest face is {MathF.Acos( Math.Clamp( bestDot, -1f, 1f ) ) * 180f / MathF.PI:0}° off)";
			}

			parts.Add( line );
		}

		parts.Add( string.Format( CultureInfo.InvariantCulture, "The body spans x {0:0.##}..{1:0.##}, y {2:0.##}..{3:0.##}, z {4:0.##}..{5:0.##}",
			c.Min.x, c.Max.x, c.Min.y, c.Max.y, c.Min.z, c.Max.z ) );

		return string.Join( "; ", parts ) + ".";
	}
}
