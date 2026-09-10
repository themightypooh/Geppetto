using Editor;
using System;
using System.IO;

namespace Marionette.EditorTools;

/// <summary>
/// The test scene File → Make Player writes: a floor, a light, and a player wearing the model that
/// was just compiled. Press Play and walk around in it.
///
/// WHY A WHOLE SCENE AND NOT A PREVIEW. A playermodel is the one export whose correctness only
/// shows up under the thing that drives it. The viewport can show the bind pose and Rig Control can
/// pose it by hand, and neither says whether citizen's walk cycle lands the feet on the floor,
/// whether the hips ride at the right height, or whether a limb came out fitted inside out. Ten
/// seconds of walking answers all three, and building the scene by hand is twenty minutes of
/// guid-matching that nobody does twice.
///
/// WHY THE JSON IS A STRING IN HERE. It was templated from a scene the editor itself saved, so the
/// spelling is the engine's own rather than a guess — but that scene is a working file that is not
/// in the repo, so depending on it at runtime would make this work on exactly one machine. The text
/// is the dependency instead.
///
/// WHAT THE NUMBERS MEAN, since a scene file explains nothing about itself:
///   - The floor is `models/dev/box.vmdl` scaled 40×40×1 and sunk to z −25 with a 50-unit box
///     collider, which puts its TOP at z 0. Everything else is measured from a floor at zero.
///   - The player stands at z 10 and falls the rest of the way. Its collider bottom is at the
///     object's own origin, so the model's feet are at the object's origin too — which is why the
///     playermodel export refuses to apply a pivot.
///   - The Body child sits at LOCAL 0,0,0. It was at −10 while the export still snapped the mesh
///     onto citizen, to hide the height that snap introduced; a fitted model needs no correction
///     and the offset became a ten-inch sink into the floor.
///   - `CreateBoneObjects` is false. A citizen skeleton is 95 bones and the editor writes one
///     GameObject per bone into the saved scene when this is on — it turned a 6 KB file into 115 KB
///     of things nobody reads, and parented the camera to a rib.
/// </summary>
internal static class EffigyPlayerScene
{
	/// <summary>
	/// Write <c>Assets/scenes/{name}_walk.scene</c> for a compiled model, and return its absolute
	/// path — or null if the scenes folder could not be resolved.
	///
	/// OVERWRITES. Running Make Player twice on the same model should leave one scene rather than
	/// `gearhead_walk` beside `gearhead_walk1`, and there is nothing in here anybody edits by hand
	/// that would be worth preserving. Anything you DO add to it is worth saving under another name.
	/// </summary>
	public static string Write( string modelAssetPath, string name )
	{
		if ( string.IsNullOrWhiteSpace( modelAssetPath ) || string.IsNullOrWhiteSpace( name ) )
			return null;

		var folder = EffigyAssetFolder.ResolveAssetFolder( "scenes" );

		if ( folder is null )
			return null;

		Directory.CreateDirectory( folder );

		var path = Path.Combine( folder, $"{name}_walk.scene" );

		File.WriteAllText( path, SceneJson( modelAssetPath, $"{name} walk" ) );

		EffigyAssetFolder.Register( folder );

		return path;
	}

	/// <summary>
	/// Fresh guids every time.
	///
	/// NOT CONSTANTS, even though constants would be simpler and would still load. Two scenes
	/// written from the same template would then hold the same object and component ids, and the
	/// engine treats a guid as an identity — open both and one is liable to be treated as the other.
	/// The three references the PlayerController holds (its renderer, its rigidbody, its collider
	/// object) are threaded through from the same variables, so they stay consistent with whatever
	/// this run generated.
	/// </summary>
	static string NewGuid() => Guid.NewGuid().ToString();

	static string SceneJson( string model, string title )
	{
		var scene = NewGuid();
		var info = NewGuid();
		var infoComponent = NewGuid();
		var sun = NewGuid();
		var sunComponent = NewGuid();
		var floor = NewGuid();
		var floorRenderer = NewGuid();
		var floorCollider = NewGuid();
		var player = NewGuid();
		var controller = NewGuid();
		var rigidbody = NewGuid();
		var moveMode = NewGuid();
		var colliders = NewGuid();
		var capsule = NewGuid();
		var box = NewGuid();
		var body = NewGuid();
		var renderer = NewGuid();
		var camera = NewGuid();
		var cameraComponent = NewGuid();

		// $$ so a single brace is literal JSON and {{name}} is the interpolation — otherwise every
		// brace in the document would have to be doubled and the template would stop being readable
		// as the file it produces.
		return $$"""
			{
			  "__guid": "{{scene}}",
			  "GameObjects": [
			    {
			      "__guid": "{{info}}",
			      "__version": 2,
			      "Flags": 0,
			      "Name": "Scene Information",
			      "Position": "0,0,0",
			      "Rotation": "0,0,0,1",
			      "Scale": "1,1,1",
			      "Tags": "",
			      "Enabled": true,
			      "NetworkMode": 2,
			      "Components": [
			        {
			          "__type": "Sandbox.SceneInformation",
			          "__guid": "{{infoComponent}}",
			          "__enabled": true,
			          "Description": "",
			          "SceneTags": "",
			          "Title": "{{title}}"
			        }
			      ],
			      "Children": []
			    },
			    {
			      "__guid": "{{sun}}",
			      "__version": 2,
			      "Flags": 0,
			      "Name": "Sun",
			      "Position": "0,0,512",
			      "Rotation": "-0.2,0.35,0.45,0.79",
			      "Scale": "1,1,1",
			      "Tags": "light_directional,light",
			      "Enabled": true,
			      "NetworkMode": 2,
			      "Components": [
			        {
			          "__type": "Sandbox.DirectionalLight",
			          "__guid": "{{sunComponent}}",
			          "__enabled": true,
			          "FogMode": "Enabled",
			          "FogStrength": 1,
			          "LightColor": "0.94,0.94,0.9,1",
			          "Shadows": true,
			          "SkyColor": "0.2,0.24,0.29,1"
			        }
			      ],
			      "Children": []
			    },
			    {
			      "__guid": "{{floor}}",
			      "__version": 2,
			      "Flags": 0,
			      "Name": "Floor",
			      "Position": "0,0,-25",
			      "Rotation": "0,0,0,1",
			      "Scale": "40,40,1",
			      "Tags": "",
			      "Enabled": true,
			      "NetworkMode": 2,
			      "Components": [
			        {
			          "__type": "Sandbox.ModelRenderer",
			          "__guid": "{{floorRenderer}}",
			          "__enabled": true,
			          "BodyGroups": 18446744073709551615,
			          "Model": "models/dev/box.vmdl",
			          "RenderType": "On",
			          "Tint": "1,1,1,1"
			        },
			        {
			          "__type": "Sandbox.BoxCollider",
			          "__guid": "{{floorCollider}}",
			          "__enabled": true,
			          "Center": "0,0,0",
			          "IsTrigger": false,
			          "Scale": "50,50,50",
			          "Static": false
			        }
			      ],
			      "Children": []
			    },
			    {
			      "__guid": "{{player}}",
			      "__version": 2,
			      "Flags": 0,
			      "Name": "Player",
			      "Position": "0,0,10",
			      "Rotation": "0,0,0,1",
			      "Scale": "1,1,1",
			      "Tags": "",
			      "Enabled": true,
			      "NetworkMode": 2,
			      "Components": [
			        {
			          "__type": "Sandbox.PlayerController",
			          "__guid": "{{controller}}",
			          "__enabled": true,
			          "AimStrengthBody": 1,
			          "AimStrengthEyes": 1,
			          "AimStrengthHead": 1,
			          "AirFriction": 0.1,
			          "AltMoveButton": "run",
			          "Body": {
			            "_type": "component",
			            "component_id": "{{rigidbody}}",
			            "go": "{{player}}",
			            "component_type": "Rigidbody"
			          },
			          "BodyHeight": 72,
			          "BodyMass": 500,
			          "BodyRadius": 16,
			          "CameraOffset": "256,0,12",
			          "ColliderObject": {
			            "_type": "gameobject",
			            "go": "{{colliders}}"
			          },
			          "DuckedHeight": 36,
			          "DuckedSpeed": 70,
			          "EnableFootstepSounds": true,
			          "EnablePressing": true,
			          "EyeDistanceFromTop": 8,
			          "HideBodyInFirstPerson": true,
			          "JumpSpeed": 300,
			          "LookSensitivity": 1,
			          "PitchClamp": 90,
			          "ReachLength": 130,
			          "Renderer": {
			            "_type": "component",
			            "component_id": "{{renderer}}",
			            "go": "{{body}}",
			            "component_type": "SkinnedModelRenderer"
			          },
			          "RotateWithGround": true,
			          "RotationAngleLimit": 45,
			          "RotationSpeed": 1,
			          "RunSpeed": 320,
			          "ThirdPerson": true,
			          "ToggleCameraModeButton": "view",
			          "UseAnimatorControls": true,
			          "UseButton": "use",
			          "UseCameraControls": true,
			          "UseFovFromPreferences": true,
			          "UseInputControls": true,
			          "UseLookControls": true,
			          "WalkSpeed": 110
			        },
			        {
			          "__type": "Sandbox.Rigidbody",
			          "__guid": "{{rigidbody}}",
			          "__enabled": true,
			          "Flags": 1,
			          "AngularDamping": 1,
			          "Gravity": true,
			          "GravityScale": 1,
			          "LinearDamping": 0.1,
			          "Locking": {
			            "X": false,
			            "Y": false,
			            "Z": false,
			            "Pitch": true,
			            "Yaw": true,
			            "Roll": true
			          },
			          "MassCenterOverride": "0,0,36",
			          "MassOverride": 500,
			          "MotionEnabled": true,
			          "OverrideMassCenter": true,
			          "RigidbodyFlags": "DisableCollisionSounds",
			          "SleepThreshold": 2,
			          "StartAsleep": false
			        },
			        {
			          "__type": "Sandbox.Movement.MoveModeWalk",
			          "__guid": "{{moveMode}}",
			          "__enabled": true,
			          "GroundAngle": 45,
			          "StepDownHeight": 18,
			          "StepUpHeight": 18
			        }
			      ],
			      "Children": [
			        {
			          "__guid": "{{colliders}}",
			          "__version": 2,
			          "Flags": 1,
			          "Name": "Colliders",
			          "Position": "0,0,0",
			          "Rotation": "0,0,0,1",
			          "Scale": "1,1,1",
			          "Tags": "",
			          "Enabled": true,
			          "NetworkMode": 2,
			          "Components": [
			            {
			              "__type": "Sandbox.CapsuleCollider",
			              "__guid": "{{capsule}}",
			              "__enabled": true,
			              "Flags": 1,
			              "End": "0,0,36",
			              "Friction": 0,
			              "IsTrigger": false,
			              "Radius": 11.313708,
			              "Start": "0,0,60.6862907",
			              "Static": false
			            },
			            {
			              "__type": "Sandbox.BoxCollider",
			              "__guid": "{{box}}",
			              "__enabled": true,
			              "Flags": 1,
			              "Center": "0,0,18",
			              "Friction": 0,
			              "IsTrigger": false,
			              "Scale": "16,16,36",
			              "Static": false
			            }
			          ],
			          "Children": []
			        },
			        {
			          "__guid": "{{body}}",
			          "__version": 2,
			          "Flags": 0,
			          "Name": "Body",
			          "Position": "0,0,0",
			          "Rotation": "0,0,0,1",
			          "Scale": "1,1,1",
			          "Tags": "",
			          "Enabled": true,
			          "NetworkMode": 2,
			          "Components": [
			            {
			              "__type": "Sandbox.SkinnedModelRenderer",
			              "__guid": "{{renderer}}",
			              "__enabled": true,
			              "BodyGroups": 18446744073709551615,
			              "CreateAttachments": false,
			              "CreateBoneObjects": false,
			              "Model": "{{model}}",
			              "Morphs": {},
			              "PlaybackRate": 1,
			              "RenderType": "On",
			              "Tint": "1,1,1,1",
			              "UseAnimGraph": true
			            }
			          ],
			          "Children": []
			        },
			        {
			          "__guid": "{{camera}}",
			          "__version": 2,
			          "Flags": 0,
			          "Name": "Camera",
			          "Position": "0,0,64",
			          "Rotation": "0,0,0,1",
			          "Scale": "1,1,1",
			          "Tags": "maincamera",
			          "Enabled": true,
			          "NetworkMode": 2,
			          "Components": [
			            {
			              "__type": "Sandbox.CameraComponent",
			              "__guid": "{{cameraComponent}}",
			              "__enabled": true,
			              "BackgroundColor": "0.33333,0.46275,0.52157,1",
			              "ClearFlags": "All",
			              "FieldOfView": 60,
			              "FovAxis": "Horizontal",
			              "IsMainCamera": true,
			              "Orthographic": false,
			              "Priority": 1,
			              "ZFar": 10000,
			              "ZNear": 10
			            }
			          ],
			          "Children": []
			        }
			      ]
			    }
			  ],
			  "SceneProperties": {
			    "NetworkInterpolation": true,
			    "PhysicsMode": "Physics3D",
			    "TimeScale": 1,
			    "WantsSystemScene": true,
			    "Metadata": {
			      "Title": "{{title}}"
			    },
			    "NavMesh": {
			      "Enabled": false
			    }
			  },
			  "ResourceVersion": 3,
			  "Title": "{{title}}",
			  "Description": null,
			  "__references": [],
			  "__version": 3
			}

			""";
	}
}
