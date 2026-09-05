using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

// ============================================================================
//  Watching the soft bones actually wobble.
//
//  WHY A PREVIEW IS NOT OPTIONAL HERE. Stiffness, damping, weight and a cone are
//  four numbers with no readable relationship to what they produce. SoftBone's
//  own summary has to explain that stiffness is an acceleration whose frequency
//  is sqrt of it, and that damping is per SECOND rather than per step - both true,
//  both useless for answering "is 60 too stiff for this tail". That question is
//  answered by looking, and until this file there was nowhere to look: the solver
//  ran in tests and, if a game wired it up, at runtime.
//
//  WHAT DRIVES THE MOTION. Gravity, and the pose gizmo. Nothing else, and that is
//  deliberate rather than minimal:
//
//    - GRAVITY ALONE MAKES ALL FOUR NUMBERS LEGIBLE. Turn preview on and a soft
//      bone sags off its authored direction and settles. How far it sags is
//      stiffness against weight, how long it rings on the way is damping, and
//      where it stops if it would have gone further is the cone. That is the whole
//      parameter set, visible in one second, with no gesture to learn.
//    - DRAGGING A BONE ADDS THE SWING. The pose gizmo already exists and already
//      moves a bone; while the preview runs, everything soft below it lags behind
//      the drag. No new gesture, and it is the real authoring loop - pose the
//      shoulder, watch the tail follow.
//
//  NO CANNED SHAKE. An automatic sway was the other candidate and it is worse in
//  a way that is easy to miss: a rig that moves on its own cannot show you that it
//  has SETTLED, and settling is most of what damping is for. SoftSolver's own test
//  suite asserts that a still rig stays still; a preview that never lets it be
//  still is hiding the property the solver is proudest of.
//
//  THE PREVIEW NEVER TOUCHES THE SKELETON. It writes into its own Xform array and
//  the drawing reads that instead of WorldBind. Turning it off is one field going
//  null - there is no pose to unwind, nothing to restore, and no way for a wobble
//  to end up saved into the document as bind data. That is the same split
//  SoftBone/SoftPose already make between authoring data and solver state, kept at
//  this level too.
// ============================================================================

internal sealed partial class EffigyViewport
{
	/// <summary>
	/// The solved pose, or null when the preview is off.
	///
	/// DOUBLES AS THE ON/OFF FLAG, rather than sitting beside a bool that could disagree with it.
	/// Everything that draws a bone asks <see cref="BoneWorld"/>, which falls back to the bind pose
	/// when this is null, so "not previewing" and "previewing a rig with nothing soft in it" are
	/// the same picture by construction.
	/// </summary>
	private Xform[] _softPreviewPose;

	private SoftPose _softPose;

	/// <summary>When the last solve ran, for the step length. RealTime rather than a frame counter:
	/// the editor's frame rate is whatever the machine and the rest of the window leave over, and
	/// SoftBone's whole damping contract is written per second for exactly that reason.</summary>
	private float _softPreviewLast;

	/// <summary>Whether the soft-bone preview is running, for the bar's tick.</summary>
	public bool SoftPreviewRunning => _softPreviewPose is not null;

	/// <summary>
	/// The longest step the solver is ever handed, in seconds.
	///
	/// A DROPPED FRAME IS NOT A LONG FRAME. Rebuilding the studio, opening a file dialog or a
	/// shader compile can stall the editor for a second or more, and handing that whole second to a
	/// spring integrator explodes it - the bone leaves its cone in one step and the clamp snaps it
	/// somewhere arbitrary. Clamping means a stall shows up as the wobble having happened slightly
	/// slower than real time, which nobody can see, instead of the rig detonating, which everybody
	/// can.
	/// </summary>
	private const float MaxSoftStep = 1f / 30f;

	/// <summary>
	/// Where a bone actually is right now — the solved pose while previewing, the bind pose
	/// otherwise.
	///
	/// EVERYTHING THAT DRAWS A BONE GOES THROUGH HERE, including the pose gizmo and the hit spheres,
	/// so a bone is grabbed where it is SEEN rather than where it would be if it were not swinging.
	/// Two answers to "where is this bone" is exactly the kind of split that produces a handle you
	/// cannot click.
	/// </summary>
	private Xform BoneWorld( int index )
	{
		if ( _softPreviewPose is not null && index >= 0 && index < _softPreviewPose.Length )
			return _softPreviewPose[index];

		return RigSkeleton.WorldBind( index );
	}

	/// <summary>Start or stop the preview. Returns whether it is now running, so the caller can
	/// tick its own button without asking again.</summary>
	public bool ToggleSoftPreview()
	{
		if ( SoftPreviewRunning )
		{
			StopSoftPreview();
			return false;
		}

		if ( RigSkeleton is null || RigSkeleton.Count == 0 )
			return false;

		_softPose = new SoftPose( RigSkeleton.Count );
		_softPreviewPose = SoftSolver.BindPose( RigSkeleton );
		_softPreviewLast = RealTime.Now;

		Update();

		return true;
	}

	public void StopSoftPreview()
	{
		_softPreviewPose = null;
		_softPose = null;

		Update();
	}

	/// <summary>
	/// Forget the motion — every soft bone snaps back onto its pose and starts again from rest.
	///
	/// SoftPose.Rest is the kernel's own word for this and does the whole job: the next solve
	/// PLACES the tails instead of easing them in from wherever they had swung to. Worth a button
	/// because tuning means typing a number, watching, and typing another, and a bone still ringing
	/// from the last value makes the next one impossible to judge.
	/// </summary>
	public void RestSoftPreview()
	{
		if ( !SoftPreviewRunning )
			return;

		_softPose.Rest();
		_softPreviewPose = SoftSolver.BindPose( RigSkeleton );
		_softPreviewLast = RealTime.Now;

		Update();
	}

	/// <summary>
	/// One step, run from the frame loop just before the skeleton is drawn.
	///
	/// THE ANIMATED POSE IS REBUILT FROM THE SKELETON EVERY FRAME rather than carried forward from
	/// the last solve. That is what makes dragging a bone with the pose gizmo drive the wobble: the
	/// gizmo writes the bone's Local, BindPose picks the change up on the next frame, and the
	/// solver sees a target that moved and lags behind it. Feeding last frame's SOLVED pose back in
	/// would instead compound the softness into itself, and a chain would drift away and never come
	/// back.
	/// </summary>
	private void SoftPreviewFrame()
	{
		if ( !SoftPreviewRunning || RigSkeleton is null )
			return;

		// The skeleton changed size under us — a bone placed or deleted while previewing. The pose
		// arrays are per-bone, so they are rebuilt rather than indexed past their end.
		if ( _softPose.Tail.Length != RigSkeleton.Count )
		{
			_softPose = new SoftPose( RigSkeleton.Count );
			_softPreviewPose = SoftSolver.BindPose( RigSkeleton );
			_softPreviewLast = RealTime.Now;
			return;
		}

		var now = RealTime.Now;
		var dt = MathF.Min( now - _softPreviewLast, MaxSoftStep );

		_softPreviewLast = now;

		// Solve writes back into the array it is given, so the animated pose has to be a fresh
		// read of the skeleton rather than the previous result. See the summary.
		_softPreviewPose = SoftSolver.BindPose( RigSkeleton );

		SoftSolver.Solve( RigSkeleton, _softPreviewPose, _softPose, dt );

		// A spring that has not settled has more to show next frame, and nothing else in this
		// viewport is going to ask for a repaint while the mouse is still.
		Update();
	}
}
