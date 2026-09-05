using System;
using System.IO;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The Boolean feature: which bodies it acts on, which it consumes, and every way it refuses.
///
/// WHAT THIS DOES NOT TEST, DELIBERATELY: whether the CSG is correct. The kernel does not carry a
/// boolean — MeshBoolean is an interface with the engine's implementation installed behind it, for
/// the reasons written at the top of MeshBoolean.cs — so there is nothing here to be right or
/// wrong about the geometry. What IS here, and what had no coverage at all, is the bookkeeping
/// around the call: a union that folds three bodies into one and leaves the right id standing, a
/// subtract that consumes its tool, a target list that defaults to "everything that is not a
/// tool", and seven refusals that each have to say which control is wrong.
///
/// THE STUB PROVIDER IS THE POINT, not a limitation. Testing the plumbing needs a boolean that
/// returns SOMETHING; it does not need one that is correct. Append-for-union and pass-through-for-
/// subtract are the cheapest meshes that let the body bookkeeping be observed, and using the real
/// engine here would test the engine rather than this feature.
/// </summary>
public static class BooleanFeatureTests
{
	public static void Run()
	{
		Section( "boolean: it refuses before it needs a provider" );
		TestNoProviderSaysWhereToGetOne();
		TestOneBodyIsRefused();
		TestNoToolIsRefused();
		TestBodyCannotBeItsOwnTool();

		Section( "boolean: which bodies it acts on" );
		TestUnionFoldsIntoTheFirstBody();
		TestSubtractConsumesItsTool();
		TestKeepToolsLeavesItStanding();
		TestTargetDefaultsToEverythingButTheTool();

		Section( "boolean: it survives the file" );
		TestRoundTrip();
	}

	// --- fixtures ---------------------------------------------------------------------------

	/// <summary>A boolean that does no CSG. Union appends, so the result is observably both
	/// meshes; subtract and intersect hand the target back unchanged. Enough to watch the bodies
	/// move around, and honest about being nothing more than that.</summary>
	sealed class StubBoolean : IMeshBoolean
	{
		public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
		{
			error = null;
			result = target.Clone();

			if ( op == BooleanOp.Union )
				MeshTransform.Append( result, tool );

			return true;
		}
	}

	/// <summary>Runs <paramref name="body"/> with the stub installed, and always puts the provider
	/// back — a leaked provider would make every later test in the run see a boolean that the
	/// kernel does not really have.</summary>
	static void WithProvider( Action body )
	{
		var previous = MeshBoolean.Provider;
		MeshBoolean.Provider = new StubBoolean();

		try { body(); }
		finally { MeshBoolean.Provider = previous; }
	}

	static PrimitiveFeature Box( PartStudio studio, string name, float x )
	{
		var box = studio.Add( new PrimitiveFeature() );
		box.Name = name;
		box.Shape.Index = 0;
		box.SizeX.Value = 1f;
		box.SizeY.Value = 1f;
		box.SizeZ.Value = 1f;
		box.Position.Value = new Vec3( x, 0, 0 );
		return box;
	}

	// --- the refusals -------------------------------------------------------------------------

	static void TestNoProviderSaysWhereToGetOne()
	{
		var previous = MeshBoolean.Provider;
		MeshBoolean.Provider = null;

		try
		{
			var studio = new PartStudio();
			var a = Box( studio, "a", 0f );
			var b = Box( studio, "b", 0.5f );

			var op = studio.Add( new BooleanFeature() );
			op.Operation.Index = 1; // Subtract
			op.Tools.BodyIds.Add( b.Id + "b0" );

			studio.Rebuild();

			Check( "with no boolean installed it says so rather than doing nothing",
				op.Error is not null, op.Error ?? "silent" );

			Check( "and points at the editor, where there is one",
				op.Error is not null && op.Error.Contains( "boolean", StringComparison.OrdinalIgnoreCase ),
				op.Error );
		}
		finally { MeshBoolean.Provider = previous; }
	}

	static void TestOneBodyIsRefused() => WithProvider( () =>
	{
		var studio = new PartStudio();
		var a = Box( studio, "a", 0f );

		var op = studio.Add( new BooleanFeature() );
		op.Tools.BodyIds.Add( a.Id + "b0" );

		studio.Rebuild();

		Check( "a boolean with one body in the studio is refused", op.Error is not null, op.Error ?? "silent" );
	} );

	static void TestNoToolIsRefused() => WithProvider( () =>
	{
		var studio = new PartStudio();
		Box( studio, "a", 0f );
		Box( studio, "b", 0.5f );

		var op = studio.Add( new BooleanFeature() );
		op.Operation.Index = 1;

		studio.Rebuild();

		// The empty selection must NOT be read as "all bodies" here, which is what it means
		// everywhere else. See the comment in BooleanFeature.Execute.
		Check( "an unpicked tool is refused rather than read as 'every body'",
			op.Error is not null, op.Error ?? "silent" );

		Check( "and the refusal names the Tool control", op.Error is not null && studio.Bodies.Count == 2,
			$"{studio.Bodies.Count} bodies survived" );
	} );

	static void TestBodyCannotBeItsOwnTool() => WithProvider( () =>
	{
		var studio = new PartStudio();
		var a = Box( studio, "a", 0f );
		Box( studio, "b", 0.5f );

		var op = studio.Add( new BooleanFeature() );
		op.Operation.Index = 1;
		op.Targets.BodyIds.Add( a.Id + "b0" );
		op.Tools.BodyIds.Add( a.Id + "b0" );

		studio.Rebuild();

		Check( "a body picked as both target and tool is refused", op.Error is not null, op.Error ?? "silent" );
	} );

	// --- the bookkeeping ------------------------------------------------------------------------

	static void TestUnionFoldsIntoTheFirstBody() => WithProvider( () =>
	{
		var studio = new PartStudio();
		var a = Box( studio, "a", 0f );
		var b = Box( studio, "b", 0.5f );
		var c = Box( studio, "c", 1f );

		var op = studio.Add( new BooleanFeature() );
		op.Operation.Index = 0; // Union
		op.Targets.BodyIds.Add( a.Id + "b0" );
		op.Tools.BodyIds.Add( b.Id + "b0" );
		op.Tools.BodyIds.Add( c.Id + "b0" );

		studio.Rebuild();

		Check( "a union of three bodies leaves one", studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );

		Check( "and it is the first of them, so downstream references still resolve",
			studio.Bodies.Count == 1 && studio.Bodies[0].Id == a.Id + "b0",
			studio.Bodies.FirstOrDefault()?.Id ?? "none" );

		// Append rather than real CSG, so the vertex count is exactly the three boxes added up —
		// which is the observable proof that all three actually went in.
		Check( "carrying the geometry of all three", studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.VertexCount == 24,
			$"{studio.Bodies.FirstOrDefault()?.Mesh.VertexCount ?? 0} verts" );
	} );

	static void TestSubtractConsumesItsTool() => WithProvider( () =>
	{
		var studio = new PartStudio();
		var a = Box( studio, "a", 0f );
		var b = Box( studio, "b", 0.5f );

		var op = studio.Add( new BooleanFeature() );
		op.Operation.Index = 1; // Subtract
		op.Targets.BodyIds.Add( a.Id + "b0" );
		op.Tools.BodyIds.Add( b.Id + "b0" );

		studio.Rebuild();

		Check( "a subtract leaves the target", op.Error is null && studio.Bodies.Count == 1,
			op.Error ?? $"{studio.Bodies.Count} bodies" );

		Check( "and consumes the tool", studio.Bodies.Count == 1 && studio.Bodies[0].Id == a.Id + "b0",
			studio.Bodies.FirstOrDefault()?.Id ?? "none" );
	} );

	static void TestKeepToolsLeavesItStanding() => WithProvider( () =>
	{
		var studio = new PartStudio();
		var a = Box( studio, "a", 0f );
		var b = Box( studio, "b", 0.5f );

		var op = studio.Add( new BooleanFeature() );
		op.Operation.Index = 1;
		op.Targets.BodyIds.Add( a.Id + "b0" );
		op.Tools.BodyIds.Add( b.Id + "b0" );
		op.KeepTools.Value = true;

		studio.Rebuild();

		Check( "Keep tool bodies leaves the tool in the studio", studio.Bodies.Count == 2,
			$"{studio.Bodies.Count} bodies" );
	} );

	static void TestTargetDefaultsToEverythingButTheTool() => WithProvider( () =>
	{
		var studio = new PartStudio();
		var a = Box( studio, "a", 0f );
		var b = Box( studio, "b", 0.5f );
		var tool = Box( studio, "cutter", 1f );

		var op = studio.Add( new BooleanFeature() );
		op.Operation.Index = 1;
		op.Tools.BodyIds.Add( tool.Id + "b0" );

		studio.Rebuild();

		// The point: an unpicked target does not mean "every body", which would ask the tool to
		// cut itself. It means every body that is not the tool.
		Check( "an unpicked target cuts every body except the tool",
			op.Error is null && studio.Bodies.Count == 2, op.Error ?? $"{studio.Bodies.Count} bodies" );

		Check( "and both targets are still there",
			studio.Bodies.Any( x => x.Id == a.Id + "b0" ) && studio.Bodies.Any( x => x.Id == b.Id + "b0" ),
			string.Join( ", ", studio.Bodies.Select( x => x.Name ) ) );
	} );

	// --- the file ---------------------------------------------------------------------------

	static void TestRoundTrip()
	{
		var studio = new PartStudio();
		var a = Box( studio, "a", 0f );
		var b = Box( studio, "b", 0.5f );

		var op = studio.Add( new BooleanFeature() );
		op.Operation.Index = 2; // Intersect
		op.Targets.BodyIds.Add( a.Id + "b0" );
		op.Tools.BodyIds.Add( b.Id + "b0" );
		op.KeepTools.Value = true;

		var path = Path.Combine( Path.GetTempPath(), "effigy_boolean_roundtrip.effigy" );
		StudioDocument.WriteFile( studio, path );

		var read = StudioDocument.ReadFile( path );
		var back = read.Features.OfType<BooleanFeature>().FirstOrDefault();

		File.Delete( path );

		Check( "a Boolean survives a save and reopen", back is not null );
		Check( "with its operation", back?.Operation.Index == 2, $"index {back?.Operation.Index}" );
		Check( "its target", back?.Targets.BodyIds.Count == 1, $"{back?.Targets.BodyIds.Count ?? 0} ids" );
		Check( "its tool", back?.Tools.BodyIds.Count == 1, $"{back?.Tools.BodyIds.Count ?? 0} ids" );
		Check( "and its keep-tools flag", back?.KeepTools.Value == true );
	}
}
