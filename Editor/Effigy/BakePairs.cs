using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Effigy;

/// <summary>
/// Which bodies bake onto which: low/high pairs matched by name, and Selected to Active.
///
/// THE NAMING IS THE INDUSTRY'S, NOT OURS. Texture bakers match a low body to its high bodies by a
/// shared base name and a suffix - Arm_low takes Arm_high (and Arm_high_2, Arm_high3 ...), and
/// Arm_cage, if there is one, is its cage. Artists already name their parts this way for those
/// tools, so a model arriving from elsewhere bakes here without renaming. Case does not matter.
///
/// Every body that looks like it belongs to a pair but has no partner is REPORTED, never dropped
/// quietly: a high with no low is detail that silently would not be baked anywhere.
/// </summary>
public static class BakePairs
{
	/// <summary>One matched pair: a low body, the high bodies it bakes from, and its cage if named.</summary>
	public sealed class Pair
	{
		public string Name;
		public string LowName;
		public PolyMesh Low;
		public List<(string Name, PolyMesh Mesh)> Highs = new();
		public string CageName;
		public PolyMesh Cage;
	}

	public enum Role { None, Low, High, Cage }

	static readonly Regex Suffix = new( @"^(?<base>.+?)[_\-\.](?<role>low|high|cage)(?:[_\-\.]?\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

	/// <summary>The base name and role a body's name gives it, or Role.None.</summary>
	public static (string Base, Role Role) Parse( string name )
	{
		var m = name is null ? null : Suffix.Match( name.Trim() );

		if ( m is null || !m.Success )
			return (name, Role.None);

		var role = m.Groups["role"].Value.ToLowerInvariant() switch
		{
			"low" => Role.Low,
			"high" => Role.High,
			_ => Role.Cage,
		};

		return (m.Groups["base"].Value, role);
	}

	/// <summary>
	/// Every _low body with the _high (and _cage) bodies of the same base name. Bodies without a
	/// suffix are ignored; ones with a suffix but no partner go into <paramref name="problems"/>,
	/// each naming the body and what it is missing.
	/// </summary>
	public static List<Pair> Match( IEnumerable<(string Name, PolyMesh Mesh)> bodies, out List<string> problems )
	{
		problems = new List<string>();
		var lows = new Dictionary<string, Pair>( StringComparer.OrdinalIgnoreCase );
		var order = new List<string>();
		var highs = new List<(string Base, string Name, PolyMesh Mesh)>();
		var cages = new List<(string Base, string Name, PolyMesh Mesh)>();

		foreach ( var (name, mesh) in bodies )
		{
			if ( mesh is null )
				continue;

			var (b, role) = Parse( name );

			switch ( role )
			{
				case Role.Low:
					if ( lows.ContainsKey( b ) )
					{
						problems.Add( $"Two low bodies are named for '{b}' ({lows[b].LowName} and {name}); rename one, or only the first is baked." );
						continue;
					}

					lows[b] = new Pair { Name = b, LowName = name, Low = mesh };
					order.Add( b );
					break;
				case Role.High:
					highs.Add( (b, name, mesh) );
					break;
				case Role.Cage:
					cages.Add( (b, name, mesh) );
					break;
			}
		}

		foreach ( var (b, name, mesh) in highs )
		{
			if ( lows.TryGetValue( b, out var pair ) )
				pair.Highs.Add( (name, mesh) );
			else
				problems.Add( $"{name} has no {b}_low to bake onto, so it is not baked." );
		}

		foreach ( var (b, name, mesh) in cages )
		{
			if ( !lows.TryGetValue( b, out var pair ) )
				problems.Add( $"{name} has no {b}_low to be the cage of, so it is not used." );
			else if ( pair.Cage is not null )
				problems.Add( $"{b} has two cages ({pair.CageName} and {name}); {pair.CageName} is used." );
			else if ( NormalBake.CageMismatch( pair.Low, mesh ) is { } why )
				problems.Add( $"{name} {why} It is not used: make the cage from a copy of {pair.LowName}." );
			else
			{
				pair.Cage = mesh;
				pair.CageName = name;
			}
		}

		var result = new List<Pair>();

		foreach ( var b in order )
		{
			var pair = lows[b];

			if ( pair.Highs.Count == 0 )
				problems.Add( $"{pair.LowName} has no {b}_high to bake from, so it is not baked." );
			else
				result.Add( pair );
		}

		return result;
	}

	/// <summary>
	/// Selected to Active: the active body is the low; every other selected body is a high, except
	/// one whose name says it is a cage (…_cage), which becomes the cage. Null with the reason in
	/// <paramref name="problem"/> when there is nothing to bake from or the cage does not fit.
	/// </summary>
	public static Pair SelectedToActive( (string Name, PolyMesh Mesh) active, IEnumerable<(string Name, PolyMesh Mesh)> selected, out string problem )
	{
		problem = null;

		if ( active.Mesh is null )
		{
			problem = "The active body has no mesh to bake onto.";
			return null;
		}

		var pair = new Pair { Name = active.Name, LowName = active.Name, Low = active.Mesh };

		foreach ( var (name, mesh) in selected )
		{
			if ( mesh is null || ReferenceEquals( mesh, active.Mesh ) )
				continue;

			if ( Parse( name ).Role == Role.Cage && pair.Cage is null )
			{
				if ( NormalBake.CageMismatch( active.Mesh, mesh ) is { } why )
				{
					problem = $"{name} is named as a cage but {why} Make the cage from a copy of {active.Name}, or rename it to bake from it instead.";
					return null;
				}

				pair.Cage = mesh;
				pair.CageName = name;
				continue;
			}

			pair.Highs.Add( (name, mesh) );
		}

		if ( pair.Highs.Count == 0 )
		{
			problem = $"Nothing to bake onto {active.Name}: select the detailed bodies first, then Shift+click {active.Name} last so it is the active one.";
			return null;
		}

		return pair;
	}

	/// <summary>
	/// The pairs as bake groups. Several highs are combined into one mesh per group;
	/// <paramref name="idByBody"/> makes the ID map colour by high body (numbered in the order the
	/// pairs list them, from 0) instead of by material slot.
	/// </summary>
	public static List<BakeGroup> ToGroups( IEnumerable<Pair> pairs, bool idByBody = false )
	{
		var groups = new List<BakeGroup>();
		var next = 0;

		foreach ( var pair in pairs )
		{
			var high = Combine( pair.Highs.Select( h => h.Mesh ).ToList(), out var source );
			int[] ids = null;

			if ( idByBody )
			{
				ids = new int[source.Length];

				for ( var f = 0; f < source.Length; f++ )
					ids[f] = next + source[f];
			}

			next += pair.Highs.Count;
			groups.Add( new BakeGroup { Name = pair.Name, Low = pair.Low, High = high, Cage = pair.Cage, HighIds = ids } );
		}

		return groups;
	}

	/// <summary>Several meshes as one (positions and faces appended); <paramref name="source"/> says which mesh each face came from.</summary>
	public static PolyMesh Combine( IReadOnlyList<PolyMesh> meshes, out int[] source )
	{
		if ( meshes.Count == 1 )
		{
			source = new int[meshes[0].FaceCount];
			return meshes[0];
		}

		var all = new PolyMesh();
		var from = new List<int>();

		for ( var m = 0; m < meshes.Count; m++ )
		{
			var offset = all.Positions.Count;
			all.Positions.AddRange( meshes[m].Positions );

			foreach ( var f in meshes[m].Faces )
			{
				var indices = new int[f.Count];

				for ( var c = 0; c < f.Count; c++ )
					indices[c] = f.Indices[c] + offset;

				all.Faces.Add( new Face( indices, f.UVs is null ? null : (Vec2[])f.UVs.Clone(), f.Material ) );
				from.Add( m );
			}
		}

		source = from.ToArray();
		return all;
	}
}
