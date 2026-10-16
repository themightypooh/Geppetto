using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>Colour for the inside of a mouth (the base head's own oral cavity, opened by the lip rip).</summary>
public static partial class CreatureGen
{
	/// <summary>
	/// Colour inside the mouth by how deep a point sits behind the lips: vermilion on the roll,
	/// wet red mucosa, then darkening to near black in the throat (readable red to 0.8 in deep,
	/// nearly black by 2 in, after Bauer's subdermal shader values).
	/// </summary>
	public static Vec3 MouthColour( float depth, int ringIndex )
	{
		var lip = new Vec3( 0.62f, 0.27f, 0.27f );
		var mucosa = new Vec3( 0.55f, 0.13f, 0.17f );
		var throat = new Vec3( 0.06f, 0.01f, 0.012f );
		if ( ringIndex <= 1 ) return lip;
		// The cheek lining stays wet dark red back past the molars; only the throat goes black.
		var c = Vec3.Lerp( Vec3.Lerp( lip, mucosa, Smoothstep( 0f, 0.3f, depth ) ), throat, Smoothstep( 1.4f, 3.4f, depth ) );
		return c * (1f - 0.5f * Smoothstep( 2.2f, 3.6f, depth ));
	}
}
