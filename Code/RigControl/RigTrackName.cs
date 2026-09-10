namespace Marionette;

/// <summary>
/// How a track names the thing it drives when a clip animates more than one object.
///
/// A CLIP IS A SCENE, NOT A MODEL. A reload is the arms, the weapon and the magazine; opening a
/// fridge is the hand and the door. Each of those can carry its own skeleton, and two of them can
/// each have a bone called "root". So a track's name is the object it belongs to plus the bone
/// inside it, joined by a slash: "magazine/latch".
///
/// THE MAIN MODEL'S BONES KEEP THEIR BARE NAMES - it is the object with no name, so "hand_R" is
/// still "hand_R". That is not tidiness: every .riganim ever saved names its bones that way, and a
/// scheme that qualified them would read every existing clip as animating bones that don't exist.
///
/// Slash because a bone name cannot contain one (they come from the model's skeleton, which is
/// compiled from a DMX/FBX node name) and because it is the separator every path-shaped name in
/// the engine already uses, so it reads as a path without being explained.
/// </summary>
public static class RigTrackName
{
	public const char Separator = '/';

	/// <summary>The main model - the object a clip has always had. Empty rather than a word like
	/// "root", so it can never collide with a prop somebody names.</summary>
	public const string RootSubject = "";

	/// <summary>Joins an object and a bone into the name a track is stored under.</summary>
	public static string Qualify( string subject, string bone ) =>
		string.IsNullOrEmpty( subject ) ? bone : $"{subject}{Separator}{bone}";

	/// <summary>Splits a track name back into the object and the bone. A bare name is the main
	/// model's, which is what every pre-existing clip contains.</summary>
	public static (string Subject, string Bone) Split( string name )
	{
		if ( string.IsNullOrEmpty( name ) )
			return (RootSubject, "");

		var slash = name.IndexOf( Separator );

		return slash < 0
			? (RootSubject, name)
			: (name[..slash], name[(slash + 1)..]);
	}

	/// <summary>The object a track belongs to - empty for the main model.</summary>
	public static string SubjectOf( string name ) => Split( name ).Subject;

	/// <summary>The bone alone, for anything that has to match the model's own skeleton.</summary>
	public static string BoneOf( string name ) => Split( name ).Bone;

	/// <summary>
	/// What to show a person: the last segment, with everything above it in brackets.
	///
	/// SPLIT AT THE LAST SEPARATOR, not the first, which is the one place these two differ. A name
	/// can be three deep - "door/handle/screw" is a bone inside a part of an object - and the
	/// useful half of that is the leaf. Split answers "which object owns this", which is the first
	/// segment; Display answers "what is this called", which is the last.
	///
	/// A bare name is left exactly as it is, so the common case reads no differently than before.
	/// </summary>
	public static string Display( string name )
	{
		if ( string.IsNullOrEmpty( name ) )
			return "";

		var slash = name.LastIndexOf( Separator );

		return slash < 0 ? name : $"{name[(slash + 1)..]} ({name[..slash]})";
	}
}
