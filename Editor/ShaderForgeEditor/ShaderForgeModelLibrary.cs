using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.ShaderForgeEditor;

/// <summary>One entry in the model picker — a stock primitive or a model from the project.</summary>
internal sealed class ShaderForgeModelEntry
{
	/// <summary>Shown in the picker.</summary>
	public string Label;

	/// <summary>Set for stock primitives; null for project models.</summary>
	public ShaderForgePrimitiveKind? Primitive;

	/// <summary>Set for project models; null for primitives.</summary>
	public string AssetPath;

	public bool IsPrimitive => Primitive.HasValue;
}

/// <summary>
/// Finds what the preview can be shown on: the four stock primitives, then every .vmdl in the
/// open project.
///
/// The scan goes through AssetSystem rather than walking the filesystem, so it sees the same set
/// the asset browser does — including models from mounted packages, which a raw directory walk of
/// the project folder would miss.
/// </summary>
internal static class ShaderForgeModelLibrary
{
	/// <summary>Project models found on the last scan, capped — a large project can hold thousands
	/// of .vmdl files and a combo box with all of them is not a picker, it is a wall.</summary>
	public const int MaxProjectModels = 400;

	public static List<ShaderForgeModelEntry> Build()
	{
		var entries = new List<ShaderForgeModelEntry>();

		foreach ( ShaderForgePrimitiveKind kind in Enum.GetValues( typeof( ShaderForgePrimitiveKind ) ) )
		{
			entries.Add( new ShaderForgeModelEntry
			{
				Label = ShaderForgePrimitives.Label( kind ),
				Primitive = kind,
			} );
		}

		entries.AddRange( ScanProject() );

		return entries;
	}

	private static List<ShaderForgeModelEntry> ScanProject()
	{
		var found = new List<ShaderForgeModelEntry>();

		try
		{
			var models = AssetSystem.All
				.Where( a => a is not null && a.AssetType == AssetType.Model )
				.Select( a => a.Path )
				.Where( p => !string.IsNullOrWhiteSpace( p ) )
				.Distinct( StringComparer.OrdinalIgnoreCase )
				.OrderBy( p => p, StringComparer.OrdinalIgnoreCase )
				.Take( MaxProjectModels );

			foreach ( var path in models )
				found.Add( new ShaderForgeModelEntry { Label = path, AssetPath = path } );
		}
		catch ( Exception e )
		{
			// A failed scan leaves the primitives usable, which is enough to keep working.
			Log.Warning( $"[ShaderForge] could not scan the project for models — {e.GetType().Name}: {e.Message}. " +
				"Stock primitives are still available." );
		}

		return found;
	}

	/// <summary>Load whatever an entry points at. Null if the asset will not load.</summary>
	public static Model Resolve( ShaderForgeModelEntry entry )
	{
		if ( entry is null )
			return null;

		if ( entry.IsPrimitive )
			return ShaderForgePrimitives.Build( entry.Primitive.Value );

		try
		{
			return Model.Load( entry.AssetPath );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[ShaderForge] could not load {entry.AssetPath} — {e.GetType().Name}: {e.Message}" );
			return null;
		}
	}

	/// <summary>
	/// The material slots on a model, as names.
	///
	/// Wrapped because Model.Materials is one of the API shapes this tool assumes rather than
	/// knows. An empty list means "show one whole-model row" to the slot panel, which is the
	/// correct fallback for a single-material mesh anyway.
	/// </summary>
	public static List<string> MaterialSlots( Model model )
	{
		var slots = new List<string>();

		if ( model is null )
			return slots;

		try
		{
			if ( model.Materials is { } materials )
			{
				foreach ( var material in materials )
					slots.Add( material?.ResourceName ?? material?.Name ?? $"slot {slots.Count}" );
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[ShaderForge] could not read material slots — {e.GetType().Name}: {e.Message}. " +
				"Falling back to a whole-model override." );
		}

		return slots;
	}
}
