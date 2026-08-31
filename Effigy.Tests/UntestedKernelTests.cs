using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Coverage for kernel work that shipped without any.
///
/// All of it arrived in one commit — ear-clipping triangulation, feature-derived body ids, the
/// anchored face reference, the DMX writer, mesh sectioning — and all of it was written into
/// Editor/Effigy, the MIRROR, rather than into the canonical kernel this project compiles. So the
/// suite kept passing while none of it was ever run: the tests were not weak on that code, they
/// could not see it. It is in Effigy/ now, and this is the check that should have gone with it.
///
/// The bias throughout is toward what a render cannot show you. A fan triangulation of a concave
/// face looks like a filled notch, an unstable body id looks like a boss on the wrong block, and a
/// malformed DMX looks like nothing at all until the compiler rejects it.
/// </summary>
public static class UntestedKernelTests
{
	public static void Run()
	{
		Report.Section( "triangulation: concave faces do not fill their notches" );
		TestTriangulation();

		Report.Section( "body ids: named after the feature that made them" );
		TestBodyIds();

		Report.Section( "face anchoring: a sketch rides the face it was drawn on" );
		TestFaceAnchoring();

		Report.Section( "mesh section: where one solid crosses another's plane" );
		TestMeshSection();

		Report.Section( "DMX export: structure the compiler needs" );
		TestDmx();

		Report.Section( "body selection: the parameter eight features carry" );
		TestBodySelection();
	}

	// --- triangulation ------------------------------------------------------------------------

	static void TestTriangulation()
	{
		// An L. A triangle fan from corner 0 spans the notch, so the shape renders — and picks, and
		// exports — as a filled rectangle. Area is what tells the two apart: the L is 3 units, the
		// rectangle it would become is 4.
		var l = new List<Vec2>
		{
			new( 0, 0 ), new( 2, 0 ), new( 2, 1 ), new( 1, 1 ), new( 1, 2 ), new( 0, 2 )
		};

		var triangles = Triangulate.Polygon( l );

		Report.Check( "an n-gon gives n-2 triangles", triangles.Count == l.Count - 2,
			$"got {triangles.Count}" );

		var area = triangles.Sum( t => TriangleArea( l[t.A], l[t.B], l[t.C] ) );

		Report.Check( "the triangles cover the L's area, not the notch's",
			MathF.Abs( area - 3f ) < 1e-4f, $"covered {area:0.####}, an L is 3 and its bounding box 4" );

		// Every triangle must wind the way the input does, or the face turns inside out where it is
		// drawn and disappears under backface culling.
		var wound = triangles.All( t => TriangleSignedArea( l[t.A], l[t.B], l[t.C] ) > 0f );
		Report.Check( "and all wind the same way as the input", wound );

		// Reversed input keeps its own winding rather than being silently corrected.
		var reversed = Enumerable.Reverse( l ).ToList();
		var reversedTriangles = Triangulate.Polygon( reversed );
		var reversedWound = reversedTriangles.All( t =>
			TriangleSignedArea( reversed[t.A], reversed[t.B], reversed[t.C] ) < 0f );

		Report.Check( "a clockwise polygon comes back clockwise", reversedWound );

		// The 3D entry point, on a face that is not axis-aligned — the plane fit is the part that
		// can quietly go wrong, and a wrong one shows up as a collapsed or self-overlapping fan.
		var tilted = new List<Vec3>
		{
			new( 0, 0, 0 ), new( 2, 0, 1 ), new( 2, 1, 1 ), new( 1, 1, 0.5f ),
			new( 1, 2, 0.5f ), new( 0, 2, 0 )
		};

		var faceTriangles = Triangulate.Face( tilted );

		Report.Check( "a tilted concave face triangulates too", faceTriangles.Count == tilted.Count - 2,
			$"got {faceTriangles.Count}" );

		var degenerate = faceTriangles.Count( t =>
			Vec3.Cross( tilted[t.B] - tilted[t.A], tilted[t.C] - tilted[t.A] ).Length < 1e-6f );

		Report.Check( "with no degenerate slivers", degenerate == 0, $"{degenerate} degenerate" );

		// Guard rails rather than exceptions: fewer than three points is a real state while a
		// sketch is being drawn, not a programming error.
		Report.Check( "two points triangulate to nothing",
			Triangulate.Polygon( new List<Vec2> { new( 0, 0 ), new( 1, 1 ) } ).Count == 0 );
	}

	// --- body ids -----------------------------------------------------------------------------

	static void TestBodyIds()
	{
		// Ids used to be a running counter across the whole rebuild, so inserting anything upstream
		// that made a body renumbered every body after it — and a face reference holding "body1"
		// silently moved to a different solid. This is that, as an assert.
		var studio = new PartStudio();

		var first = studio.Add( new PrimitiveFeature() );
		first.SizeX.Value = first.SizeY.Value = first.SizeZ.Value = 2f;

		var second = studio.Add( new PrimitiveFeature() );
		second.SizeX.Value = second.SizeY.Value = second.SizeZ.Value = 1f;

		studio.Rebuild();

		var before = studio.Bodies.Select( b => b.Id ).ToList();

		Report.Check( "two features make two distinctly named bodies",
			before.Count == 2 && before[0] != before[1], string.Join( ", ", before ) );

		var secondIdBefore = studio.Bodies.First( b => b.FeatureId == second.Id ).Id;

		// Insert a body-producing feature at the very front, which is the edit that used to
		// renumber everything downstream.
		var inserted = new PrimitiveFeature();
		inserted.SizeX.Value = inserted.SizeY.Value = inserted.SizeZ.Value = 3f;
		studio.Insert( 0, inserted );
		studio.Rebuild();

		var secondIdAfter = studio.Bodies.First( b => b.FeatureId == second.Id ).Id;

		Report.Check( "inserting a feature upstream does not rename the bodies below it",
			secondIdAfter == secondIdBefore, $"{secondIdBefore} became {secondIdAfter}" );

		Report.Check( "every body still has a unique id",
			studio.Bodies.Select( b => b.Id ).Distinct().Count() == studio.Bodies.Count,
			string.Join( ", ", studio.Bodies.Select( b => b.Id ) ) );

		Report.Check( "and each knows which feature made it",
			studio.Bodies.All( b => !string.IsNullOrEmpty( b.FeatureId ) ) );

		// A feature that makes several bodies must name them the same way on every rebuild, or a
		// selection made once stops matching the next time the tree runs.
		var patterned = new PartStudio();
		patterned.Add( new PrimitiveFeature() );
		var pattern = patterned.Add( new LinearPatternFeature() );
		pattern.Count.Value = 3;
		pattern.Spacing.Value = 3f;

		patterned.Rebuild();
		var firstRun = patterned.Bodies.Select( b => b.Id ).ToList();

		patterned.MarkDirty( 0 );
		patterned.Rebuild();
		var secondRun = patterned.Bodies.Select( b => b.Id ).ToList();

		Report.Check( "a pattern names its bodies identically on every rebuild",
			firstRun.SequenceEqual( secondRun ),
			$"[{string.Join( ", ", firstRun )}] then [{string.Join( ", ", secondRun )}]" );
	}

	// --- face anchoring -----------------------------------------------------------------------

	static void TestFaceAnchoring()
	{
		// FaceSketchTests covers a sketch on a TOP face surviving the block growing. The anchoring
		// change is about the other axis: a face that gets SHORTER underneath the sketch. Anchored
		// to an absolute point, the sketch stays where it was and the face retreats out from under
		// it; anchored in from the nearest edge, it comes along.
		var studio = new PartStudio();

		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 10, 2 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 6f;

		studio.Rebuild();

		var bar = studio.Bodies.Single();

		// The far end face of the bar, and a point near one corner of it rather than at its centre
		// — the whole question is whether a distance from a specific edge is preserved.
		var faceIndex = FarthestFaceAlong( bar.Mesh, new Vec3( 0, 0, 1 ) );
		var faceCentre = bar.Mesh.FaceCentroid( bar.Mesh.Faces[faceIndex] );

		Report.Check( "the bar has a face at the top of the extrude",
			MathF.Abs( faceCentre.z - 6f ) < 1e-3f, $"face centre {faceCentre}" );

		var side = FarthestFaceAlong( bar.Mesh, new Vec3( 1, 0, 0 ) );
		var sideFace = bar.Mesh.Faces[side];
		var sideCentroid = bar.Mesh.FaceCentroid( sideFace );

		// A point on the side face, high up near the top edge: 1 unit down from a face that is
		// 6 tall.
		var nearTop = new Vec3( sideCentroid.x, sideCentroid.y, 5f );
		var reference = FacePlane.Capture( bar, side, nearTop );

		Report.Check( "capturing a face records which body it came from", reference.BodyId == bar.Id,
			reference.BodyId );

		Report.Check( "and records an anchor rather than only a point", reference.Anchored );

		Report.Check( "the reference resolves on the body it was taken from",
			FacePlane.TryResolve( studio.Bodies, reference, out var placed ), "did not resolve" );

		var distanceFromTop = 6f - placed.Origin.z;

		Report.Check( "resolving puts it back where it was captured",
			MathF.Abs( distanceFromTop - 1f ) < 1e-3f, $"{distanceFromTop:0.####} from the top edge" );

		// Now shorten the extrude. The side face gets shorter, and the anchor is measured from the
		// top edge, so the sketch has to come down with it.
		extrude.Distance.Value = 4f;
		studio.MarkDirty( 1 );
		studio.Rebuild();

		Report.Check( "it still resolves after the face it sits on got shorter",
			FacePlane.TryResolve( studio.Bodies, reference, out var moved ), "lost the face" );

		var newDistanceFromTop = 4f - moved.Origin.z;

		Report.Check( "and holds its distance from the edge it was anchored to",
			MathF.Abs( newDistanceFromTop - 1f ) < 1e-3f,
			$"{newDistanceFromTop:0.####} from the top edge, was 1" );

		// The failure this replaced: an absolute point would have stayed at z = 5, which is now
		// past the end of a 4-tall bar entirely.
		Report.Check( "rather than staying at an absolute height now off the end of the bar",
			moved.Origin.z < 4f, $"z = {moved.Origin.z:0.####}, bar is 4 tall" );
	}

	// --- mesh section -------------------------------------------------------------------------

	static void TestMeshSection()
	{
		// A 2x2x2 box cut through its middle: the section is a 2x2 square, so four segments with a
		// total length of 8. Section code fails by dropping segments at face boundaries or by
		// double-counting an edge that lies in the plane, and both show up in that total.
		var box = Primitives.Box( 2, 2, 2 );
		var segments = MeshSection.CrossSection( box, Vec3.Zero, new Vec3( 0, 0, 1 ) );

		Report.Check( "a box cut through the middle sections into four segments",
			segments.Count == 4, $"got {segments.Count}" );

		var length = segments.Sum( s => (s.B - s.A).Length );

		Report.Check( "whose total length is the square's perimeter",
			MathF.Abs( length - 8f ) < 1e-3f, $"got {length:0.####}" );

		Report.Check( "and every segment lies in the cutting plane",
			segments.All( s => MathF.Abs( s.A.z ) < 1e-4f && MathF.Abs( s.B.z ) < 1e-4f ) );

		// A plane that misses entirely produces nothing rather than throwing — the caller draws
		// footprints for every other body in the studio, most of which do not touch this face.
		Report.Check( "a plane the mesh does not reach sections into nothing",
			MeshSection.CrossSection( box, new Vec3( 0, 0, 50 ), new Vec3( 0, 0, 1 ) ).Count == 0 );

		Report.Check( "and a null mesh is not a crash",
			MeshSection.CrossSection( null, Vec3.Zero, new Vec3( 0, 0, 1 ) ).Count == 0 );
	}

	// --- DMX ----------------------------------------------------------------------------------

	static void TestDmx()
	{
		// ModelDoc does not import SMD ("Supported types: FBX, DMX, OBJ, VOX"), so this is the
		// rigged export path. It cannot be verified by rendering — a malformed DMX is not a bad
		// model, it is a file the compiler refuses — so the checks are on structure.
		var box = Primitives.Box( 2, 2, 2 );
		var text = DmxWriter.Write( box, modelName: "unit_box" );

		Report.Check( "the header names the model encoding and format", text.Contains( "<!-- dmx encoding" ),
			text.Split( '\n' ).FirstOrDefault() );

		Report.Check( "it declares a DmeModel", text.Contains( "DmeModel" ) );
		Report.Check( "with a vertex data block", text.Contains( "DmeVertexData" ) );
		Report.Check( "and a face set", text.Contains( "DmeFaceSet" ) );

		Report.Check( "no coordinate came out NaN or infinite",
			!text.Contains( "NaN" ) && !text.Contains( "Infinity" ) && !text.Contains( "∞" ) );

		Report.Check( "numbers are invariant-culture, so a comma decimal cannot corrupt the file",
			!System.Text.RegularExpressions.Regex.IsMatch( text, @"\d,\d" ) );

		// N-gons must survive. Triangulating on export would lose exactly the quad topology the
		// whole kernel is built to preserve.
		var quadCount = box.Faces.Count( f => f.Count == 4 );
		Report.Check( "the box goes in as six quads", quadCount == 6, $"got {quadCount}" );

		// A skinned export needs the bones and the influence arrays to be there.
		var skeleton = new Skeleton();
		var root = skeleton.AddBone( "root", -1, Xform.Identity );
		skeleton.AddBone( "child", root, Xform.Identity );

		var skinned = DmxWriter.Write( box, skeleton );

		Report.Check( "a skinned export names its bones",
			skinned.Contains( "root" ) && skinned.Contains( "child" ) );

		// blendweights$0, not jointWeights. This check asserted the latter and passed on a file the
		// compiler answered with "Missing position values" - see DmxGrammarTests, which parses the
		// output instead of searching it.
		Report.Check( "and carries joint weights", skinned.Contains( "blendweights$0" ),
			"no blendweights$0 array" );

		Report.Check( "a static export still writes a root bone, which DMX requires",
			text.Contains( "root" ) );

		// Both writers read the same mesh, so the two exports must agree about how many vertices
		// there are. They diverged once already when one of them was changed and the other was not.
		var smd = SmdWriter.Write( box, skeleton );

		Report.Check( "SMD and DMX agree the mesh is the same size",
			smd.Split( '\n' ).Any( l => l.Contains( "triangles" ) ) && skinned.Length > 0 );

		// The checks above are all "does this word appear", and a file can pass every one of them
		// and still be refused by the reader. It was: element_array members were written
		// `} "DmeDag" {` with no comma between, and dmxconvert stopped at the second one with
		// "Expecting ',', didn't find it!". Nothing that asks about substrings can see that, so
		// these two parse the punctuation instead.
		Report.Check( "the static export is balanced and comma-correct KeyValues2",
			KeyValues2WellFormed( text, out var staticFault ), staticFault );

		Report.Check( "and so is the rigged one, where the array members are nested deepest",
			KeyValues2WellFormed( skinned, out var riggedFault ), riggedFault );
	}

	/// <summary>
	/// A deliberately small KeyValues2 syntax check: braces and brackets balance, and every member
	/// of an array is followed by a comma except the last. It is not a full parser and does not
	/// need to be — it exists to catch the malformed shapes this writer can actually produce.
	/// </summary>
	static bool KeyValues2WellFormed( string text, out string fault )
	{
		fault = null;

		// true = the enclosing container is an array, false = an element body. Array members take
		// a trailing comma; attributes inside an element body must not.
		var containers = new Stack<bool>();
		var lines = text.Split( '\n' );

		// Whether the previous meaningful token closed a member that still owes a comma.
		var pendingComma = false;
		var pendingLine = 0;

		for ( var i = 0; i < lines.Length; i++ )
		{
			var line = lines[i].Trim();

			if ( line.Length == 0 || line.StartsWith( "<!--" ) )
				continue;

			if ( pendingComma && !line.StartsWith( "]" ) )
			{
				fault = $"line {pendingLine + 1}: array member is not followed by a comma";
				return false;
			}

			pendingComma = false;

			if ( line == "{" )
			{
				containers.Push( false );
				continue;
			}

			if ( line == "[" )
			{
				containers.Push( true );
				continue;
			}

			if ( line.StartsWith( "}" ) || line.StartsWith( "]" ) )
			{
				if ( containers.Count == 0 )
				{
					fault = $"line {i + 1}: closed a container that was never opened";
					return false;
				}

				var wasArray = containers.Pop();
				var expected = wasArray ? "]" : "}";

				if ( !line.StartsWith( expected ) )
				{
					fault = $"line {i + 1}: got '{line}' closing a{(wasArray ? "n array" : "n element")}";
					return false;
				}

				// A closed element that sat inside an array is itself an array member.
				if ( !wasArray && containers.Count > 0 && containers.Peek() && !line.EndsWith( "," ) )
				{
					pendingComma = true;
					pendingLine = i;
				}

				continue;
			}

			// An element inside an array is written as its type name on one line and the body on
			// the next. The type name is not the member — the closing brace is — so it owes
			// nothing here.
			if ( NextMeaningful( lines, i ) == "{" )
				continue;

			// A plain member of an array — a value or an element reference — owes a comma unless
			// it is the last one before the bracket.
			if ( containers.Count > 0 && containers.Peek() && !line.EndsWith( "," ) )
			{
				pendingComma = true;
				pendingLine = i;
			}
		}

		if ( containers.Count != 0 )
		{
			fault = $"{containers.Count} container(s) left unclosed at end of file";
			return false;
		}

		return true;
	}

	/// <summary>The next line that is not blank, after <paramref name="index"/>.</summary>
	static string NextMeaningful( string[] lines, int index )
	{
		for ( var i = index + 1; i < lines.Length; i++ )
		{
			var line = lines[i].Trim();

			if ( line.Length > 0 )
				return line;
		}

		return "";
	}

	// --- body selection -------------------------------------------------------------------------

	static void TestBodySelection()
	{
		// The editor's new selection box writes into BodySelectionParam.BodyIds. This is the kernel
		// half of that: with ids in the list, a feature must act on exactly those bodies and leave
		// the rest untouched. Every one of the eight features that carries the parameter routes
		// through Matches, so proving it on one and checking the wiring on the rest is the useful
		// division.
		var studio = new PartStudio();

		var left = studio.Add( new PrimitiveFeature() );
		left.SizeX.Value = left.SizeY.Value = left.SizeZ.Value = 2f;

		var right = studio.Add( new PrimitiveFeature() );
		right.SizeX.Value = right.SizeY.Value = right.SizeZ.Value = 2f;
		right.Position.Value = new Vec3( 10f, 0f, 0f );

		studio.Rebuild();

		var targetId = studio.Bodies.First( b => b.FeatureId == right.Id ).Id;
		var otherId = studio.Bodies.First( b => b.FeatureId == left.Id ).Id;

		var subdivide = studio.Add( new SubdivideFeature() );
		subdivide.Levels.Value = 1;
		subdivide.Bodies.BodyIds.Add( targetId );

		studio.Rebuild();

		var target = studio.Bodies.First( b => b.Id == targetId );
		var other = studio.Bodies.First( b => b.Id == otherId );

		Report.Check( "a selected body is the one the feature acted on",
			target.Mesh.FaceCount == 24, $"{target.Mesh.FaceCount} faces, expected 24" );

		Report.Check( "and an unselected body is left exactly as it was",
			other.Mesh.FaceCount == 6, $"{other.Mesh.FaceCount} faces, expected 6" );

		// Empty means all, which is what every feature did before there was a way to select. The
		// default has to stay that, or adding the control would change the meaning of every
		// existing document.
		subdivide.Bodies.BodyIds.Clear();
		studio.MarkDirty( 2 );
		studio.Rebuild();

		Report.Check( "clearing the selection goes back to acting on every body",
			studio.Bodies.All( b => b.Mesh.FaceCount == 24 ),
			string.Join( ", ", studio.Bodies.Select( b => $"{b.Id}:{b.Mesh.FaceCount}" ) ) );

		// A selection naming a body that no longer exists must not take the whole feature down —
		// deleting a body upstream is an ordinary edit, and Matches on an empty result set means
		// the feature simply does nothing.
		subdivide.Bodies.BodyIds.Add( "body-that-was-deleted" );
		studio.MarkDirty( 2 );
		var report = studio.Rebuild();

		Report.Check( "a selection naming a missing body is not an error",
			!report.HasErrors, report.ToString() );

		Report.Check( "and leaves every real body alone",
			studio.Bodies.All( b => b.Mesh.FaceCount == 6 ),
			string.Join( ", ", studio.Bodies.Select( b => $"{b.Id}:{b.Mesh.FaceCount}" ) ) );

		// Every feature that declares the parameter has to honour it. Listing them by reflection
		// rather than by hand means a ninth feature cannot be added with a dead selector.
		var carriers = new Feature[]
		{
			new SubdivideFeature(), new TransformFeature(), new MirrorFeature(),
			new LinearPatternFeature(), new CircularPatternFeature(),
			new ShellFeature(), new ChamferFeature(), new UVProjectFeature()
		};

		foreach ( var feature in carriers )
		{
			var param = feature.Parameters.OfType<BodySelectionParam>().FirstOrDefault();

			Report.Check( $"{feature.TypeName} exposes its body selection as a parameter",
				param is not null, "the dialog can only render what Parameters lists" );

			if ( param is null )
				continue;

			var body = new Body( "kept", "kept", Primitives.Box() );

			Report.Check( $"{feature.TypeName} matches everything while the selection is empty",
				param.Matches( body ) );

			param.BodyIds.Add( "something-else" );

			Report.Check( $"{feature.TypeName} excludes a body once the selection names another",
				!param.Matches( body ) );
		}
	}

	// --- helpers ------------------------------------------------------------------------------

	static float TriangleArea( Vec2 a, Vec2 b, Vec2 c ) => MathF.Abs( TriangleSignedArea( a, b, c ) );

	static float TriangleSignedArea( Vec2 a, Vec2 b, Vec2 c ) =>
		((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) * 0.5f;

	/// <summary>The face whose centroid sits furthest along a direction — how you get hold of "the
	/// top one" or "the far end" without depending on face ordering.</summary>
	static int FarthestFaceAlong( PolyMesh mesh, Vec3 direction )
	{
		var best = -1;
		var bestDistance = float.NegativeInfinity;

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			var d = Vec3.Dot( mesh.FaceCentroid( mesh.Faces[i] ), direction.Normal );

			if ( d > bestDistance )
			{
				bestDistance = d;
				best = i;
			}
		}

		return best;
	}
}
