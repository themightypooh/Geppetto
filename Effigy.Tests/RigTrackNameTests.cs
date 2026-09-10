using Marionette;

namespace Effigy.Tests;

/// <summary>
/// How a track names the thing it drives, now that a clip can animate several objects at once.
///
/// WHY THIS IS WORTH A TEST FILE. Every .riganim ever saved names the main model's bones bare -
/// "hand_R", not "/hand_R" - and playback matches tracks to bones BY NAME. Get the split wrong in
/// either direction and an existing clip animates nothing: no error, no missing asset, just a
/// model standing in its bind pose. That is the worst shape a regression can take, so the rule
/// lives somewhere a test can reach it.
/// </summary>
public static class RigTrackNameTests
{
	public static void Run()
	{
		Report.Section( "rig track names: an old clip's bare bone names still mean the main model" );
		TestBareNames();

		Report.Section( "rig track names: a second object qualifies its bones" );
		TestQualified();

		Report.Section( "rig track names: what a person is shown" );
		TestDisplay();
	}

	static void TestBareNames()
	{
		var (subject, bone) = RigTrackName.Split( "hand_R" );

		Report.Check( "a bare name is the main model's", subject == RigTrackName.RootSubject );
		Report.Check( "and the bone is left exactly as it was", bone == "hand_R" );

		Report.Check( "qualifying against the main model changes nothing",
			RigTrackName.Qualify( RigTrackName.RootSubject, "hand_R" ) == "hand_R" );

		Report.Check( "and neither does qualifying against null",
			RigTrackName.Qualify( null, "hand_R" ) == "hand_R" );
	}

	static void TestQualified()
	{
		Report.Check( "an object and a bone join with a slash",
			RigTrackName.Qualify( "magazine", "follower" ) == "magazine/follower" );

		var (subject, bone) = RigTrackName.Split( "magazine/follower" );

		Report.Check( "which splits back to the object", subject == "magazine" );
		Report.Check( "and the bone", bone == "follower" );

		// The point of the whole scheme: two objects are each allowed a bone called "root", and
		// the clip has to be able to tell them apart.
		Report.Check( "two objects can both own a bone of the same name",
			RigTrackName.Qualify( "door", "root" ) != RigTrackName.Qualify( "frame", "root" ) );

		Report.Check( "the object is read back on its own", RigTrackName.SubjectOf( "door/hinge" ) == "door" );
		Report.Check( "and so is the bone", RigTrackName.BoneOf( "door/hinge" ) == "hinge" );

		// A prop named with a slash would otherwise swallow the bone. First slash wins, so the
		// bone survives and only the object name is odd.
		Report.Check( "only the first slash separates", RigTrackName.BoneOf( "a/b/c" ) == "b/c" );

		Report.Check( "an empty name has no bone and no object",
			RigTrackName.Split( "" ).Subject == RigTrackName.RootSubject && RigTrackName.Split( "" ).Bone == "" );
	}

	static void TestDisplay()
	{
		Report.Check( "a main-model bone reads exactly as it always did",
			RigTrackName.Display( "hand_R" ) == "hand_R" );

		Report.Check( "another object's bone says which object it is in",
			RigTrackName.Display( "magazine/follower" ) == "follower (magazine)" );

		// Three deep - a bone inside a part inside an object. The leaf is the useful half, which is
		// why Display splits at the LAST separator where Split uses the first.
		Report.Check( "a part's own bone names the part it is in",
			RigTrackName.Display( "door/handle/screw" ) == "screw (door/handle)" );

		Report.Check( "and Split still answers which object owns it",
			RigTrackName.SubjectOf( "door/handle/screw" ) == "door" );

		Report.Check( "an empty name displays as nothing rather than throwing",
			RigTrackName.Display( "" ) == "" );
	}
}
