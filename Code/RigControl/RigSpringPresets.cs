namespace Marionette;

/// <summary>
/// The spring presets as plain numbers, engine-free so Effigy.Tests compiles this file and pins it to
/// the kernel's ChainSpring.Presets - a live soft chain and a baked one mean the same by "loose".
/// Order is RigSoftPreset's (Custom is not in here; Rope, appended after it, is the last entry).
/// </summary>
public static class RigSpringPresets
{
	/// <summary>Stiffness, damping (fraction of speed kept per second), gravity weight, cone degrees;
	/// Rope = the chain's line keeps its length and drapes instead of springing (Effigy ChainSpring.Rope).</summary>
	public readonly record struct Values( float Stiffness, float Damping, float Weight, float MaxAngle, bool Rope = false );

	public static readonly Values[] All =
	{
		new( 120f, 0.001f, 0.3f, 60f ),   // loose: cable, tentacle, tail
		new( 50f, 0.0005f, 1f, 25f ),     // heavy: flesh, sac, belly
		new( 300f, 0.003f, 0.1f, 30f ),   // springy: antenna, ear, jiggle
		new( 500f, 0.0001f, 0.05f, 15f ), // stiff: fingers, small tips
		new( 0f, 0.05f, 1f, 180f, true ), // rope: slack cable, drapes and piles
	};
}
