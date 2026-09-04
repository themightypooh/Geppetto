using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// One grease-pencil mark: a freehand ribbon of points in world space, and optionally something
/// written next to it.
///
/// ANNOTATION, NOT GEOMETRY, AND THE DISTINCTION IS THE WHOLE POINT. A note is a thing you leave
/// for yourself — "this face is 2mm proud", "ask about the hinge" — and the one guarantee it has to
/// make is that it never becomes part of the model. That guarantee is structural rather than
/// careful: notes hang off <see cref="PartStudio.Notes"/>, which is not the feature list, so
/// Rebuild never sees them and <see cref="PartStudio.ToMesh"/> has nothing to exclude. There is no
/// "skip the notes" branch anywhere in the export path, because a branch is a thing that can be
/// forgotten in the next writer somebody adds. NoteTests holds the line.
///
/// POINTS AND TEXT ON ONE OBJECT rather than two types. The way this actually gets used is: circle
/// the thing, then say what is wrong with it. Splitting that into a Stroke and a Label makes the
/// pair something the user has to keep together by hand, and a caption that drifts away from the
/// squiggle it was explaining is worse than no caption. A note with no text is a plain scribble; a
/// note with one point and text is a plain pin. Both fall out of the same class.
///
/// WORLD SPACE, DELIBERATELY, rather than a body-and-face reference like SketchReference carries.
/// A note is about the shape at a moment, and a topology reference does not survive the remesh or
/// the boolean that the note is very often complaining about. A stroke that stays put while the
/// model changes underneath is legible — you can see it no longer lines up. A stroke that silently
/// orphans itself is just gone.
/// </summary>
public sealed class Note
{
	/// <summary>The ribbon, in world units, in the order it was drawn. A single point is a pin.</summary>
	public List<Vec3> Points = new();

	/// <summary>What the note says, or null for a bare scribble. Drawn at <see cref="Anchor"/>.</summary>
	public string Text;

	/// <summary>Index into <see cref="NotePalette"/>. An INDEX rather than an rgb triple, for the
	/// reason ChoiceParam stores an index: the palette is a small fixed set the UI offers as
	/// swatches, and storing the swatch means a later tweak to what "red" looks like reaches every
	/// document instead of only new ones.</summary>
	public int Color;

	/// <summary>
	/// Line thickness in SCREEN PIXELS.
	///
	/// Pixels rather than world units, which is the odd one out among the distances this feature
	/// carries and is deliberate. Everything else about a note is world-space so it stays put on the
	/// model; the thickness of the line is about legibility at whatever zoom you are at, and a
	/// world-space thickness would make a note on a big part unreadably thin and one on a small part
	/// a blob.
	/// </summary>
	public float Width = 2f;

	/// <summary>Where the caption hangs, and where an erase click measures from.</summary>
	public Vec3 Anchor => Points.Count > 0 ? Points[0] : Vec3.Zero;

	/// <summary>A note with neither a mark nor a message is not worth keeping — see
	/// NoteSession.EndStroke, which drops one rather than leaving an invisible entry in the list
	/// that only the file ever mentions.</summary>
	public bool IsEmpty => Points.Count == 0 && string.IsNullOrWhiteSpace( Text );

	public Note Clone() => new()
	{
		Points = new List<Vec3>( Points ),
		Text = Text,
		Color = Color,
		Width = Width,
	};

	/// <summary>Closest approach from this note's ribbon to a ray, used for hover and erase.
	/// float.MaxValue when there is nothing to measure.</summary>
	public float DistanceToRay( Vec3 origin, Vec3 direction )
	{
		var dir = direction.Normal;

		if ( dir.LengthSquared < 0.5f || Points.Count == 0 )
			return float.MaxValue;

		var best = float.MaxValue;

		foreach ( var p in Points )
		{
			// Behind the camera does not count. Without the clamp a stroke a metre back down the
			// ray measures as being right under the cursor, so erasing near one edge of the model
			// deletes a note on the far side of it.
			var along = Vec3.Dot( p - origin, dir );

			if ( along < 0f )
				continue;

			best = MathF.Min( best, (p - (origin + dir * along)).Length );
		}

		return best;
	}
}

/// <summary>
/// The colours a note can be.
///
/// A FIXED SHORT LIST rather than a colour picker. Notes are read at a glance against a shaded
/// model, and the useful thing about a note's colour is that it MEANS something the next time you
/// open the file — red is a problem, green is settled, blue is a measurement. Six named swatches
/// invite that; a picker gives you fifty shades of nearly-grey, none of which you will remember
/// choosing. These are all picked bright and saturated so they read on a dark viewport, which a
/// free picker also cannot promise.
/// </summary>
public static class NotePalette
{
	public readonly struct Swatch
	{
		public readonly string Name;
		public readonly float R, G, B;

		public Swatch( string name, float r, float g, float b )
		{
			Name = name;
			R = r;
			G = g;
			B = b;
		}
	}

	public static readonly Swatch[] Swatches =
	{
		new( "Yellow", 1f, 0.85f, 0.25f ),
		new( "Red", 1f, 0.35f, 0.32f ),
		new( "Green", 0.42f, 0.95f, 0.5f ),
		new( "Blue", 0.4f, 0.72f, 1f ),
		new( "Magenta", 1f, 0.45f, 0.9f ),
		new( "White", 0.95f, 0.95f, 0.95f ),
	};

	public static int Count => Swatches.Length;

	/// <summary>Clamped rather than throwing: a colour index out of range comes from a document
	/// written by a build with a longer palette, and a note in the wrong colour is a far better
	/// outcome than a file that will not open.</summary>
	public static Swatch At( int index ) => Swatches[Math.Clamp( index, 0, Swatches.Length - 1 )];

	public static string NameAt( int index ) => At( index ).Name;
}
