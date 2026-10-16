using Sandbox;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Marionette;

/// <summary>
/// Drives an Effigy model's corrective shapes from its joints: every morph named
/// <c>bone_bend90</c> (what Effigy's Corrective shape exports) is set each frame from how far that
/// bone is bent from its bind pose against its parent — 0 straight, 1 at the angle in the name,
/// following the volume a linear-blend joint loses in between ((1/cos(θ/2) − 1) / (1/cos(A/2) − 1),
/// Effigy's CorrectiveMorph.WeightAt).
///
/// Add it next to the SkinnedModelRenderer. It finds the shapes itself; nothing to set up. A morph
/// something else is already overriding this frame (a Marionette clip keying it) is left alone.
/// </summary>
[Title( "Corrective Shapes" ), Category( "Marionette" ), Icon( "open_in_full" )]
public sealed class CorrectiveMorphDriver : Component
{
	[Property] public SkinnedModelRenderer Target { get; set; }

	/// <summary>Multiplies every shape: 0 turns them off, 1 is exact.</summary>
	[Property, Range( 0f, 2f )] public float Strength { get; set; } = 1f;

	private readonly record struct Shape( string Morph, string Bone, float Angle );

	private readonly List<Shape> _shapes = new();
	private Model _scanned;

	protected override void OnEnabled()
	{
		Target ??= GetComponent<SkinnedModelRenderer>();
	}

	protected override void OnUpdate()
	{
		if ( !Target.IsValid() || Target.Model is not { } model )
			return;

		if ( !ReferenceEquals( model, _scanned ) )
			Scan( model );

		foreach ( var shape in _shapes )
		{
			if ( Target.Morphs.ContainsOverride( shape.Morph ) && !_driving.Contains( shape.Morph ) )
				continue;

			var bent = BendDegrees( model, shape.Bone );
			if ( bent is null )
				continue;

			Target.Morphs.Set( shape.Morph, WeightAt( bent.Value, shape.Angle ) * Strength );
			_driving.Add( shape.Morph );
		}
	}

	private readonly HashSet<string> _driving = new();

	/// <summary>The model's morphs that read as bone_bendN with a bone of that name.</summary>
	private void Scan( Model model )
	{
		_scanned = model;
		_shapes.Clear();
		_driving.Clear();

		for ( var i = 0; i < model.MorphCount; i++ )
		{
			var name = model.GetMorphName( i );
			var cut = name?.LastIndexOf( "_bend", StringComparison.Ordinal ) ?? -1;
			if ( cut <= 0 )
				continue;

			var bone = name[..cut];
			if ( !float.TryParse( name[(cut + 5)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var angle ) || angle <= 0f )
				continue;

			if ( model.Bones.GetBone( bone ) is null )
				continue;

			_shapes.Add( new Shape( name, bone, angle ) );
		}
	}

	/// <summary>How far the bone has turned against its parent since the bind pose, in degrees.</summary>
	private float? BendDegrees( Model model, string boneName )
	{
		var bone = model.Bones.GetBone( boneName );
		if ( bone?.Parent is not { } parent )
			return null;

		if ( !Target.TryGetBoneTransform( boneName, out var world ) || !Target.TryGetBoneTransform( parent.Name, out var parentWorld ) )
			return null;

		// Bone.LocalTransform is MODEL space despite its name (see RigAnimPlayerComponent).
		var bindRelative = parent.LocalTransform.Rotation.Inverse * bone.LocalTransform.Rotation;
		var nowRelative = parentWorld.Rotation.Inverse * world.Rotation;
		return (bindRelative.Inverse * nowRelative).Angle();
	}

	private static float WeightAt( float degrees, float keyDegrees )
	{
		static float Loss( float deg ) => 1f / MathF.Max( MathF.Cos( MathF.Abs( deg ) * MathF.PI / 360f ), 0.2f ) - 1f;
		var key = Loss( keyDegrees );
		return key <= 1e-6f ? 0f : Math.Clamp( Loss( degrees ) / key, 0f, 2f );
	}
}
