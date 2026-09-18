using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// A set of player bodies, each built, rigged and fitted onto citizen's skeleton without the editor.
///
///     Effigy.Tests --playermodels &lt;project Assets folder&gt; [preview.png]
///
/// writes, under the Assets folder:
///     models/players/&lt;name&gt;.effigy   the studio, rig included - open it to keep editing
///     models/players/&lt;name&gt;.dmx     mesh, fitted citizen bind pose, weights
///     models/players/&lt;name&gt;.vmdl    citizen's anim graph and prefabs, via <see cref="Playermodel.Vmdl"/>
///     materials/players/&lt;mat&gt;.vmat  one flat colour per material the set uses
///
/// THE SKELETON COMES FROM JOINTS, NOT FROM PART SHAPES. The tutorial's route (Bone from Part) needs
/// every part longest along its bone, which rules out a round egg head or a fist wider than it is
/// long. Here each variant states its joints through <see cref="Frame"/>, the bones are laid between
/// them, and the parts are then free to be any shape - each is bound to a bone by name.
///
/// THE ONE PROPORTION NOT ON OFFER is hip height. Citizen's walk decides where the hips ride, so
/// every variant keeps the tutorial robot's pelvis (28 to 35) and legs; torso, neck, head, shoulder
/// width and arm length are what vary.
///
/// UNITS ARE INCHES, T-pose facing +X, soles at z=0, `_L` on +Y - the same as <see cref="HumanoidSample"/>.
/// </summary>
public static class PlayermodelGen
{
	public static int Run( string assetsRoot, string previewPath )
	{
		var modelDir = Path.Combine( assetsRoot, "models", "players" );
		var materialDir = Path.Combine( assetsRoot, "materials", "players" );
		Directory.CreateDirectory( modelDir );
		Directory.CreateDirectory( materialDir );

		var variants = new (string Name, Action<Kit> Build)[]
		{
			("line_cook", LineCook),
			("gearhead", Gearhead),
			("egghead", Egghead),
			("brute", Brute),
			("stringbean", Stringbean),
			("mannequin", Mannequin),
		};

		var failures = 0;
		var usedMaterials = new SortedSet<string>( StringComparer.Ordinal );
		var tiles = new List<PngPreview.Tile>();

		if ( !RollDoesNotLeak() )
			failures++;

		foreach ( var (name, build) in variants )
		{
			try
			{
				var kit = new Kit();
				build( kit );
				var studio = kit.Finish();
				var fit = Playermodel.Build( studio );

				var problems = Check( studio, fit );

				if ( problems.Count > 0 )
				{
					Console.WriteLine( $"  FAIL {name}: {string.Join( "; ", problems )}" );
					failures++;
					continue;
				}

				StudioDocument.WriteFile( studio, Path.Combine( modelDir, $"{name}.effigy" ) );
				DmxWriter.WriteFile( fit.Mesh, Path.Combine( modelDir, $"{name}.dmx" ), fit.Skeleton,
					materialName: studio.NameForSlot, modelName: name );
				File.WriteAllText( Path.Combine( modelDir, $"{name}.vmdl" ),
					Playermodel.Vmdl( $"models/players/{name}.dmx", fit.Skeleton ) );

				foreach ( var material in kit.Materials )
					usedMaterials.Add( material );

				tiles.Add( new PngPreview.Tile( studio.ToMesh(), name ) );

				var height = fit.Mesh.Positions.Max( p => p.z );
				Console.WriteLine( $"  ok   {name}: {studio.Bodies.Count} parts, {fit.Mesh.VertexCount} verts, "
					+ $"{studio.Rig.Count} bones -> {fit.Skeleton.Count} citizen bones, {height:0.#}in tall" );
			}
			catch ( Exception e )
			{
				Console.WriteLine( $"  FAIL {name}: {e.Message}" );
				failures++;
			}
		}

		foreach ( var material in usedMaterials )
			File.WriteAllText( Path.Combine( materialDir, $"{material}.vmat" ), Vmat( Palette[material] ) );

		if ( !string.IsNullOrEmpty( previewPath ) && tiles.Count > 0 )
		{
			Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( previewPath ) )! );
			PngPreview.WriteSheet( tiles, previewPath, columns: 3, tileSize: 360 );
		}

		Console.WriteLine( $"{variants.Length - failures} of {variants.Length} playermodels written to {modelDir}" );

		return failures == 0 ? 0 : 1;
	}

	/// <summary>
	/// What the pipeline itself cannot be trusted to shout about - the same checks the tutorial's
	/// test makes, applied to every variant. A model that fails one of these still compiles and
	/// still loads; it just walks wrong.
	/// </summary>
	static List<string> Check( PartStudio studio, Playermodel.Result fit )
	{
		var problems = new List<string>();

		var unbound = studio.Bodies.Where( b => !studio.BodyBoneMap.ContainsKey( b.Id ) ).Select( b => b.Name ).ToList();

		if ( unbound.Count > 0 )
			problems.Add( $"parts bound to no bone: {string.Join( ", ", unbound )}" );

		if ( fit.Unmapped.Count > 0 )
			problems.Add( $"bones citizen has no name for: {string.Join( ", ", fit.Unmapped )}" );

		if ( fit.VerticesStranded > 0 )
			problems.Add( $"{fit.VerticesStranded} stranded vertices" );

		var lowest = fit.Mesh.Positions.Min( p => p.z );

		if ( MathF.Abs( lowest ) > 0.05f )
			problems.Add( $"soles at z={lowest:0.###}, not on the floor" );

		// Citizen's joints land on the model's own, which is the promise Fit makes.
		var map = CitizenBoneMap.Playermodel();

		foreach ( var bone in new[] { "hand_L", "hand_R", "forearm_L", "thigh_R", "calf_L", "head" } )
		{
			var fitted = fit.Skeleton.IndexOf( map[bone] );
			var drift = (fit.Skeleton.HeadWorld( fitted ) - studio.Rig.HeadWorld( studio.Rig.IndexOf( bone ) )).Length;

			if ( drift > 0.5f )
				problems.Add( $"citizen's {bone} landed {drift:0.##}in from the model's" );
		}

		return problems;
	}

	/// <summary>
	/// The up hints handed to AddBoneFromPoints here are a choice, not a measurement, so prove the
	/// fit does not care about them: build one rig with two different sets of hints and demand the
	/// same fitted skeleton. If this ever fails, the hints need to copy what BoneFromBody measures.
	/// </summary>
	static bool RollDoesNotLeak()
	{
		var a = Playermodel.Build( new Kit().Also( Mannequin ).Finish( upHint: null ) ).Skeleton;
		var b = Playermodel.Build( new Kit().Also( Mannequin ).Finish( upHint: new Vec3( 0.3f, 0.7f, 0.2f ) ) ).Skeleton;
		var worst = 0f;

		for ( var i = 0; i < a.Count; i++ )
		{
			var wa = a.WorldBind( i );
			var wb = b.WorldBind( i );
			worst = MathF.Max( worst, (wa.Origin - wb.Origin).Length );
			worst = MathF.Max( worst, (wa.X - wb.X).Length );
			worst = MathF.Max( worst, (wa.Z - wb.Z).Length );
		}

		var ok = worst < 1e-3f;
		Console.WriteLine( $"  {(ok ? "ok  " : "FAIL")} fitted skeleton ignores source roll (worst difference {worst:0.#####})" );
		return ok;
	}

	// -------------------------------------------------------------------------------------------
	// Joints

	/// <summary>
	/// Where a variant's joints are. Defaults are the tutorial robot's, so a Frame left alone rigs
	/// exactly like the sample the test suite already walks onto citizen.
	/// </summary>
	public sealed class Frame
	{
		// Fixed: the hips and legs ride where citizen's walk puts them.
		public float PelvisLo => 28f;
		public float PelvisHi => 35f;
		public float HipZ => 30f;
		public float KneeZ => 17f;
		public float AnkleZ => 4.5f;
		public float FootZ => 1.5f;
		public float HeelX => -1.5f;

		public float Torso = 22.5f;       // top of pelvis to top of chest
		public float Neck = 3.5f;
		public float Head = 8.5f;
		public float ShoulderDrop = 1.5f; // shoulder line below the top of the chest
		public float ClavicleIn = 2.5f;
		public float Clavicle = 5.5f;
		public float UpperArm = 9.5f;
		public float Forearm = 8.5f;
		public float Hand = 5f;
		public float HipY = 3.2f;
		public float ToeX = 6.5f;

		public float Spine1Hi => PelvisHi + Torso * (7f / 22.5f);
		public float Spine2Hi => PelvisHi + Torso * (14.5f / 22.5f);
		public float ChestHi => PelvisHi + Torso;
		public float NeckHi => ChestHi + Neck;
		public float HeadHi => NeckHi + Head;
		public float ShoulderZ => ChestHi - ShoulderDrop;
		public float ShoulderY => ClavicleIn + Clavicle;
		public float ElbowY => ShoulderY + UpperArm;
		public float WristY => ElbowY + Forearm;
		public float FingerY => WristY + Hand;

		public Vec3 Shoulder => new( 0, ShoulderY, ShoulderZ );
		public Vec3 Elbow => new( 0, ElbowY, ShoulderZ );
		public Vec3 Wrist => new( 0, WristY, ShoulderZ );
		public Vec3 Hip => new( 0, HipY, HipZ );
		public Vec3 Knee => new( 0, HipY, KneeZ );
		public Vec3 Ankle => new( 0, HipY, AnkleZ );
		public float HeadMid => (NeckHi + HeadHi) * 0.5f;

		/// <summary>Every bone, parent first, left side only for the limbs - mirrored by the caller.</summary>
		public IEnumerable<(string Name, string Parent, Vec3 Head, Vec3 Tail)> Bones()
		{
			yield return ("pelvis", null, Z( PelvisLo ), Z( PelvisHi ));
			yield return ("spine_01", "pelvis", Z( PelvisHi ), Z( Spine1Hi ));
			yield return ("spine_02", "spine_01", Z( Spine1Hi ), Z( Spine2Hi ));
			yield return ("chest", "spine_02", Z( Spine2Hi ), Z( ChestHi ));
			yield return ("neck", "chest", Z( ChestHi ), Z( NeckHi ));
			yield return ("head", "neck", Z( NeckHi ), Z( HeadHi ));

			yield return ("clavicle_L", "chest", new Vec3( 0, ClavicleIn, ShoulderZ ), Shoulder);
			yield return ("upperarm_L", "clavicle_L", Shoulder, Elbow);
			yield return ("forearm_L", "upperarm_L", Elbow, Wrist);
			yield return ("hand_L", "forearm_L", Wrist, new Vec3( 0, FingerY, ShoulderZ ));

			yield return ("thigh_L", "pelvis", Hip, Knee);
			yield return ("calf_L", "thigh_L", Knee, Ankle);
			yield return ("foot_L", "calf_L", new Vec3( HeelX, HipY, FootZ ), new Vec3( ToeX, HipY, FootZ ));
		}

		static Vec3 Z( float z ) => new( 0, 0, z );
	}

	// -------------------------------------------------------------------------------------------
	// Parts

	public enum Shape { Box, Cylinder, Egg }

	/// <summary>
	/// Collects a variant's frame and parts, then turns them into a rigged studio. Every part names
	/// the bone it rides on; a part on an `_L` bone is mirrored onto `_R` automatically.
	/// </summary>
	public sealed class Kit
	{
		public Frame F = new();
		public readonly SortedSet<string> Materials = new( StringComparer.Ordinal );

		readonly List<(string Label, string Bone, Shape Shape, Vec3 Centre, Vec3 Size, string Material)> parts = new();

		public Kit Also( Action<Kit> build )
		{
			build( this );
			return this;
		}

		/// <summary>An axis-aligned part. A cylinder runs along the longest of the three sizes.</summary>
		public void Part( string label, string bone, Shape shape, Vec3 centre, Vec3 size, string material )
		{
			if ( !Palette.ContainsKey( material ) )
				throw new ArgumentException( $"no material called {material}" );

			parts.Add( (label, bone, shape, centre, size, material) );

			if ( !bone.EndsWith( "_L", StringComparison.Ordinal ) )
				return;

			var right = bone[..^2] + "_R";
			parts.Add( (label + " R", right, shape, new Vec3( centre.x, -centre.y, centre.z ), size, material) );
		}

		/// <summary>
		/// A part spanning two points on one axis, <paramref name="thick"/> across it (front-to-back
		/// first, then the remaining axis), overhanging each end by <paramref name="overhang"/>.
		/// </summary>
		public void Span( string label, string bone, Shape shape, Vec3 a, Vec3 b, Vec2 thick, string material, float overhang = 0f )
		{
			var d = b - a;
			var centre = (a + b) * 0.5f;
			var len = d.Length + overhang * 2f;
			Vec3 size;

			if ( MathF.Abs( d.z ) >= MathF.Abs( d.x ) && MathF.Abs( d.z ) >= MathF.Abs( d.y ) )
				size = new Vec3( thick.x, thick.y, len );
			else if ( MathF.Abs( d.y ) >= MathF.Abs( d.x ) )
				size = new Vec3( thick.x, len, thick.y );
			else
				size = new Vec3( len, thick.x, thick.y );

			Part( label, bone, shape, centre, size, material );
		}

		/// <summary>A round cap at a joint, riding on the bone that starts there.</summary>
		public void Ball( string label, string bone, Vec3 at, float diameter, string material ) =>
			Part( label, bone, Shape.Egg, at, new Vec3( diameter, diameter, diameter ), material );

		public PartStudio Finish( Vec3? upHint = null )
		{
			var studio = new PartStudio();
			var slots = new Dictionary<string, int>( StringComparer.Ordinal );
			var boneOfFeature = new Dictionary<string, (string Feature, string Bone, string Label)>();

			foreach ( var p in parts )
			{
				if ( !slots.TryGetValue( p.Material, out var slot ) )
				{
					slot = slots.Count;
					slots[p.Material] = slot;
					studio.MaterialNames[slot] = $"materials/players/{p.Material}.vmat";
					Materials.Add( p.Material );
				}

				var primitive = studio.Add( new PrimitiveFeature() );
				primitive.Name = p.Label;
				primitive.Material.Value = slot;

				switch ( p.Shape )
				{
					case Shape.Box:
						primitive.Shape.Index = 0;
						primitive.SizeX.Value = p.Size.x;
						primitive.SizeY.Value = p.Size.y;
						primitive.SizeZ.Value = p.Size.z;
						primitive.Position.Value = p.Centre;
						break;

					case Shape.Egg:
						primitive.Shape.Index = 2;
						primitive.Radius.Value = 0.5f;
						primitive.Divisions.Value = 5;
						primitive.Scale.Value = p.Size;
						primitive.Position.Value = p.Centre;
						break;

					case Shape.Cylinder:
					{
						// Built along Z at the origin, then turned onto its long axis and moved into
						// place. A quarter turn about X carries local Z to world Y and local Y to Z;
						// about Y it carries local Z to X and local X to Z - hence the swapped scales.
						primitive.Shape.Index = 1;
						primitive.Radius.Value = 0.5f;
						primitive.SizeZ.Value = 1f;
						primitive.Segments.Value = 16;

						var s = p.Size;
						Vec3 axis;

						if ( s.z >= s.x && s.z >= s.y )
						{
							primitive.Scale.Value = s;
							axis = Vec3.Zero;
						}
						else if ( s.y >= s.x )
						{
							primitive.Scale.Value = new Vec3( s.x, s.z, s.y );
							axis = new Vec3( 1, 0, 0 );
						}
						else
						{
							primitive.Scale.Value = new Vec3( s.z, s.y, s.x );
							axis = new Vec3( 0, 1, 0 );
						}

						if ( axis.LengthSquared == 0f )
						{
							primitive.Position.Value = p.Centre;
							break;
						}

						var turn = studio.Add( new TransformFeature() );
						turn.Name = $"{p.Label} placed";
						turn.Bodies.BodyIds.Add( primitive.Id + "b0" );
						turn.RotationAxis.Value = axis;
						turn.RotationAngle.Value = 90f;
						turn.Translate.Value = p.Centre;
						break;
					}
				}

				boneOfFeature[primitive.Id + "b0"] = (primitive.Id, p.Bone, p.Label);
			}

			var report = studio.Rebuild();

			if ( report.HasErrors )
				throw new InvalidOperationException( report.ToString() );

			foreach ( var body in studio.Bodies )
			{
				if ( !boneOfFeature.TryGetValue( body.Id, out var owner ) )
					continue;

				studio.BodyNames[body.Id] = owner.Label;
				studio.BodyBoneMap[body.Id] = owner.Bone;
			}

			var rig = studio.Rig;

			foreach ( var (name, parent, head, tail) in F.Bones() )
			{
				AddBone( rig, name, parent, head, tail, upHint );

				if ( !name.EndsWith( "_L", StringComparison.Ordinal ) )
					continue;

				AddBone( rig, name[..^2] + "_R", parent.EndsWith( "_L", StringComparison.Ordinal ) ? parent[..^2] + "_R" : parent,
					Mirror( head ), Mirror( tail ), upHint is Vec3 u ? Mirror( u ) : null );
			}

			studio.Rebuild();
			return studio;
		}

		/// <summary>
		/// Spine and legs lean their roll forward (+X); arms lean up (+Z). Only a default - see
		/// <see cref="RollDoesNotLeak"/> for why it does not matter which.
		/// </summary>
		static void AddBone( Skeleton rig, string name, string parent, Vec3 head, Vec3 tail, Vec3? upHint )
		{
			var along = tail - head;
			var up = upHint ?? (MathF.Abs( along.y ) > MathF.Abs( along.z ) ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ));

			if ( MathF.Abs( Vec3.Dot( up, along ) ) > 0.99f * along.Length * up.Length )
				up = new Vec3( 0, 0, 1 );

			rig.AddBoneFromPoints( name, parent is null ? -1 : rig.IndexOf( parent ), head, tail, up );
		}

		static Vec3 Mirror( Vec3 v ) => new( v.x, -v.y, v.z );
	}

	// -------------------------------------------------------------------------------------------
	// The variants

	static Vec3 P( float x, float y, float z ) => new( x, y, z );
	static Vec2 T( float front, float other ) => new( front, other );

	/// <summary>The diner's own: white double-breasted coat, check trousers, toque, neckerchief.</summary>
	static void LineCook( Kit k )
	{
		var f = k.F;

		k.Span( "Trousers seat", "pelvis", Shape.Box, P( 0, 0, 27.5f ), P( 0, 0, f.Spine1Hi - 3f ), T( 6.8f, 8.6f ), "check_trousers" );
		k.Span( "Coat skirt", "spine_01", Shape.Box, P( 0, 0, f.PelvisHi - 1f ), P( 0, 0, f.Spine1Hi ), T( 7.4f, 9f ), "chef_white" );
		k.Span( "Coat waist", "spine_02", Shape.Box, P( 0, 0, f.Spine1Hi ), P( 0, 0, f.Spine2Hi ), T( 7.8f, 9.6f ), "chef_white" );
		k.Span( "Coat chest", "chest", Shape.Box, P( 0, 0, f.Spine2Hi ), P( 0, 0, f.ChestHi ), T( 8.4f, 10.6f ), "chef_white", 0.3f );
		k.Part( "Apron", "pelvis", Shape.Box, P( 3.9f, 0, 30f ), P( 0.5f, 8.6f, 9f ), "apron_blue" );
		k.Part( "Apron tie", "spine_01", Shape.Box, P( 0, 0, f.PelvisHi + 0.6f ), P( 7.6f, 9.2f, 0.9f ), "apron_blue" );

		foreach ( var (z, bone) in new[] { (f.Spine2Hi - 2.2f, "spine_02"), (f.Spine2Hi + 1.8f, "chest"), (f.ChestHi - 2.4f, "chest") } )
		{
			k.Ball( "Button L", bone, P( 4.25f, 1.9f, z ), 0.9f, "button_black" );
			k.Ball( "Button R", bone, P( 4.25f, -1.9f, z ), 0.9f, "button_black" );
		}

		k.Span( "Neck", "neck", Shape.Cylinder, P( 0, 0, f.ChestHi ), P( 0, 0, f.NeckHi ), T( 3f, 3f ), "skin_light", 0.5f );
		k.Part( "Neckerchief", "neck", Shape.Egg, P( 0.4f, 0, f.ChestHi + 0.9f ), P( 4.4f, 4.4f, 1.8f ), "kerchief_red" );
		k.Part( "Head", "head", Shape.Egg, P( 0, 0, f.HeadMid ), P( 7f, 6.4f, 9f ), "skin_light" );
		k.Part( "Nose", "head", Shape.Egg, P( 3.5f, 0, f.HeadMid - 0.3f ), P( 1.4f, 1.1f, 1.6f ), "skin_light" );
		k.Part( "Eye L", "head", Shape.Egg, P( 3.1f, 1.3f, f.HeadMid + 0.9f ), P( 0.7f, 0.8f, 0.9f ), "button_black" );
		k.Part( "Moustache", "head", Shape.Egg, P( 3.3f, 0, f.HeadMid - 1.3f ), P( 0.9f, 3.2f, 0.8f ), "hair_brown" );
		k.Part( "Toque band", "head", Shape.Cylinder, P( 0, 0, f.HeadHi - 0.8f ), P( 6.8f, 6.8f, 3.2f ), "chef_white" );
		k.Part( "Toque puff", "head", Shape.Egg, P( 0, 0, f.HeadHi + 2.6f ), P( 8.4f, 8.4f, 5.4f ), "chef_white" );

		k.Span( "Shoulder", "clavicle_L", Shape.Box, P( 0, f.ClavicleIn, f.ShoulderZ ), f.Shoulder, T( 4f, 3.8f ), "chef_white", 0.6f );
		k.Span( "Sleeve", "upperarm_L", Shape.Cylinder, f.Shoulder, f.Elbow, T( 3.6f, 3.6f ), "chef_white", 0.4f );
		k.Span( "Cuff", "forearm_L", Shape.Cylinder, f.Elbow, f.Elbow + P( 0, 2.2f, 0 ), T( 3.8f, 3.8f ), "chef_white" );
		k.Span( "Forearm", "forearm_L", Shape.Cylinder, f.Elbow, f.Wrist, T( 2.6f, 2.6f ), "skin_light" );
		k.Span( "Hand", "hand_L", Shape.Egg, f.Wrist, f.Wrist + P( 0, f.Hand, 0 ), T( 2.1f, 3.4f ), "skin_light", 0.2f );
		k.Part( "Thumb", "hand_L", Shape.Egg, f.Wrist + P( 1.2f, 1.4f, 0.2f ), P( 1.4f, 2.2f, 1.2f ), "skin_light" );

		k.Span( "Trouser leg", "thigh_L", Shape.Cylinder, f.Hip, f.Knee, T( 4.6f, 4.6f ), "check_trousers", 0.8f );
		k.Span( "Shin", "calf_L", Shape.Cylinder, f.Knee, f.Ankle, T( 4f, 4f ), "check_trousers" );
		k.Part( "Clog", "foot_L", Shape.Box, P( 2.4f, f.HipY, 1.6f ), P( 9.4f, 4.2f, 3.2f ), "clog_black" );
	}

	/// <summary>A kitchen robot: orange shell, steel limbs, a cyan visor and chest light.</summary>
	static void Gearhead( Kit k )
	{
		var f = k.F;
		f.Torso = 23f;
		f.Clavicle = 6.5f;
		f.Head = 7.5f;
		f.HipY = 3.6f;

		k.Span( "Hip block", "pelvis", Shape.Box, P( 0, 0, 27f ), P( 0, 0, f.PelvisHi ), T( 6f, 8.4f ), "steel" );
		k.Span( "Spine piston", "spine_01", Shape.Cylinder, P( 0, 0, f.PelvisHi ), P( 0, 0, f.Spine1Hi ), T( 3.4f, 3.4f ), "rubber_black", 0.5f );
		k.Part( "Belly ring", "spine_01", Shape.Cylinder, P( 0, 0, (f.PelvisHi + f.Spine1Hi) / 2 ), P( 5.4f, 7f, 1.4f ), "steel" );
		k.Span( "Lower chest", "spine_02", Shape.Box, P( 0, 0, f.Spine1Hi ), P( 0, 0, f.Spine2Hi ), T( 7.4f, 9.6f ), "robot_orange" );
		k.Span( "Chest shell", "chest", Shape.Box, P( 0, 0, f.Spine2Hi ), P( 0, 0, f.ChestHi ), T( 9f, 12.6f ), "robot_orange", 0.5f );
		k.Part( "Chest light", "chest", Shape.Egg, P( 4.6f, 0, f.ShoulderZ - 2.4f ), P( 1.4f, 3f, 3f ), "visor_cyan" );
		k.Part( "Vent L", "chest", Shape.Box, P( 4.55f, 3.6f, f.ShoulderZ - 3.2f ), P( 0.4f, 2.4f, 0.5f ), "rubber_black" );
		k.Part( "Vent R", "chest", Shape.Box, P( 4.55f, -3.6f, f.ShoulderZ - 3.2f ), P( 0.4f, 2.4f, 0.5f ), "rubber_black" );

		k.Span( "Neck rod", "neck", Shape.Cylinder, P( 0, 0, f.ChestHi ), P( 0, 0, f.NeckHi ), T( 2.2f, 2.2f ), "steel", 0.6f );
		k.Part( "Head box", "head", Shape.Box, P( 0, 0, f.HeadMid ), P( 7.6f, 7.8f, 7f ), "robot_orange" );
		k.Part( "Visor", "head", Shape.Box, P( 3.85f, 0, f.HeadMid + 0.6f ), P( 0.6f, 6.4f, 2.2f ), "visor_cyan" );
		k.Part( "Jaw grille", "head", Shape.Box, P( 3.8f, 0, f.HeadMid - 2f ), P( 0.6f, 4f, 1.4f ), "steel" );
		k.Part( "Ear bolt L", "head", Shape.Cylinder, P( 0, 4.1f, f.HeadMid ), P( 2f, 1.2f, 2f ), "steel" );
		k.Part( "Ear bolt R", "head", Shape.Cylinder, P( 0, -4.1f, f.HeadMid ), P( 2f, 1.2f, 2f ), "steel" );
		k.Part( "Antenna", "head", Shape.Cylinder, P( -1.5f, 2f, f.HeadHi + 2f ), P( 0.35f, 0.35f, 4f ), "steel" );
		k.Ball( "Antenna tip", "head", P( -1.5f, 2f, f.HeadHi + 4.2f ), 1.1f, "kerchief_red" );

		k.Part( "Shoulder pod", "clavicle_L", Shape.Egg, f.Shoulder + P( 0, -0.8f, 0.6f ), P( 5.4f, 5.6f, 5.4f ), "robot_orange" );
		k.Span( "Clavicle bar", "clavicle_L", Shape.Box, P( 0, f.ClavicleIn, f.ShoulderZ ), f.Shoulder, T( 2.4f, 2.4f ), "steel" );
		k.Span( "Upper arm", "upperarm_L", Shape.Cylinder, f.Shoulder, f.Elbow, T( 2.6f, 2.6f ), "steel" );
		k.Ball( "Elbow", "forearm_L", f.Elbow, 3.4f, "rubber_black" );
		k.Span( "Forearm shell", "forearm_L", Shape.Box, f.Elbow + P( 0, 1.4f, 0 ), f.Wrist, T( 3.8f, 3.8f ), "robot_orange" );
		k.Span( "Wrist", "hand_L", Shape.Cylinder, f.Wrist, f.Wrist + P( 0, 1f, 0 ), T( 2.4f, 2.4f ), "rubber_black" );
		k.Span( "Claw palm", "hand_L", Shape.Box, f.Wrist + P( 0, 0.9f, 0 ), f.Wrist + P( 0, 3.2f, 0 ), T( 2.6f, 3.4f ), "steel" );
		k.Span( "Claw finger", "hand_L", Shape.Box, f.Wrist + P( 0.7f, 3.2f, 0 ), f.Wrist + P( 0.7f, f.Hand, 0 ), T( 0.9f, 2.8f ), "steel" );
		k.Span( "Claw thumb", "hand_L", Shape.Box, f.Wrist + P( -0.8f, 3.2f, 0 ), f.Wrist + P( -0.8f, 4.4f, 0 ), T( 0.8f, 2.8f ), "steel" );

		k.Span( "Thigh strut", "thigh_L", Shape.Cylinder, f.Hip, f.Knee, T( 3.2f, 3.2f ), "steel", 0.5f );
		k.Ball( "Knee", "calf_L", f.Knee, 4f, "rubber_black" );
		k.Span( "Shin shell", "calf_L", Shape.Box, f.Knee + P( 0, 0, -1.6f ), f.Ankle, T( 4.8f, 4.6f ), "robot_orange" );
		k.Part( "Foot", "foot_L", Shape.Box, P( 2.2f, f.HipY, 1.5f ), P( 10f, 5f, 3f ), "steel" );
		k.Part( "Toe cap", "foot_L", Shape.Box, P( 6.4f, f.HipY, 1.7f ), P( 1.6f, 5.2f, 3.4f ), "robot_orange" );
	}

	/// <summary>A mascot: an egg for a head, yolk-yellow suit, white cartoon gloves, red sneakers.</summary>
	static void Egghead( Kit k )
	{
		var f = k.F;
		f.Torso = 21f;
		f.Neck = 2f;
		f.Head = 15f;
		f.UpperArm = 8.5f;
		f.Forearm = 8f;
		f.Hand = 5.5f;
		f.ToeX = 7f;

		k.Part( "Seat", "pelvis", Shape.Egg, P( 0, 0, 31f ), P( 8.4f, 9.6f, 9f ), "yolk" );
		k.Part( "Belly", "spine_01", Shape.Egg, P( 0.3f, 0, (f.PelvisHi + f.Spine1Hi) / 2 ), P( 8.2f, 9.2f, 8.4f ), "yolk" );
		k.Part( "Ribs", "spine_02", Shape.Egg, P( 0, 0, (f.Spine1Hi + f.Spine2Hi) / 2 ), P( 8f, 9.4f, 8.4f ), "yolk" );
		k.Part( "Chest", "chest", Shape.Egg, P( 0, 0, (f.Spine2Hi + f.ChestHi) / 2 + 0.4f ), P( 8f, 10.4f, 8.6f ), "yolk" );
		k.Part( "Bib", "chest", Shape.Egg, P( 3.4f, 0, f.Spine2Hi + 1.4f ), P( 1.6f, 5.4f, 5.4f ), "chef_white" );

		k.Span( "Neck", "neck", Shape.Cylinder, P( 0, 0, f.ChestHi - 0.5f ), P( 0, 0, f.NeckHi ), T( 3f, 3f ), "egg_shell", 0.5f );
		k.Part( "Egg", "head", Shape.Egg, P( 0, 0, f.NeckHi + 7.2f ), P( 12f, 11.2f, 15.2f ), "egg_shell" );
		k.Part( "Eye L", "head", Shape.Egg, P( 5.3f, 2f, f.NeckHi + 8.4f ), P( 1.4f, 2.2f, 3.2f ), "chef_white" );
		k.Part( "Eye R", "head", Shape.Egg, P( 5.3f, -2f, f.NeckHi + 8.4f ), P( 1.4f, 2.2f, 3.2f ), "chef_white" );
		k.Part( "Pupil L", "head", Shape.Egg, P( 5.95f, 2f, f.NeckHi + 8.1f ), P( 0.6f, 1.1f, 1.6f ), "button_black" );
		k.Part( "Pupil R", "head", Shape.Egg, P( 5.95f, -2f, f.NeckHi + 8.1f ), P( 0.6f, 1.1f, 1.6f ), "button_black" );
		k.Part( "Smile", "head", Shape.Egg, P( 5.4f, 0, f.NeckHi + 4.6f ), P( 0.7f, 3.6f, 0.9f ), "kerchief_red" );
		k.Part( "Crack", "head", Shape.Box, P( 0.5f, 5.4f, f.NeckHi + 11f ), P( 3.6f, 0.3f, 0.5f ), "hair_brown" );

		k.Span( "Arm", "upperarm_L", Shape.Cylinder, f.Shoulder + P( 0, -2.6f, 0 ), f.Elbow, T( 2.4f, 2.4f ), "yolk", 0.3f );
		k.Span( "Clavicle", "clavicle_L", Shape.Cylinder, P( 0, f.ClavicleIn, f.ShoulderZ ), f.Shoulder + P( 0, -2.6f, 0 ), T( 2.6f, 2.6f ), "yolk" );
		k.Span( "Forearm", "forearm_L", Shape.Cylinder, f.Elbow, f.Wrist, T( 2.2f, 2.2f ), "yolk", 0.3f );
		k.Span( "Cuff", "hand_L", Shape.Cylinder, f.Wrist, f.Wrist + P( 0, 1.2f, 0 ), T( 3.4f, 3.4f ), "chef_white" );
		k.Part( "Glove", "hand_L", Shape.Egg, f.Wrist + P( 0, 3.4f, 0 ), P( 3.2f, 5f, 4.6f ), "chef_white" );
		k.Part( "Glove thumb", "hand_L", Shape.Egg, f.Wrist + P( 1.8f, 2.6f, 0.6f ), P( 1.8f, 2.4f, 1.6f ), "chef_white" );

		k.Span( "Leg", "thigh_L", Shape.Cylinder, f.Hip, f.Knee, T( 3.2f, 3.2f ), "yolk", 0.4f );
		k.Span( "Shin", "calf_L", Shape.Cylinder, f.Knee, f.Ankle + P( 0, 0, -1f ), T( 2.8f, 2.8f ), "yolk", 0.3f );
		k.Part( "Sneaker", "foot_L", Shape.Egg, P( 2.6f, f.HipY, 2.2f ), P( 10.4f, 5.4f, 4.4f ), "kerchief_red" );
		k.Part( "Sole", "foot_L", Shape.Box, P( 2.6f, f.HipY, 0.4f ), P( 9.6f, 4.8f, 0.8f ), "chef_white" );
	}

	/// <summary>Big shoulders, big fists: a red tank top, denim, work boots, a beard.</summary>
	static void Brute( Kit k )
	{
		var f = k.F;
		f.Torso = 24f;
		f.Neck = 2.5f;
		f.Head = 7.5f;
		f.ShoulderDrop = 2f;
		f.Clavicle = 7.5f;
		f.UpperArm = 10f;
		f.Forearm = 9f;
		f.Hand = 5f;
		f.HipY = 4.2f;
		f.ToeX = 7.5f;

		k.Span( "Jeans seat", "pelvis", Shape.Box, P( 0, 0, 27f ), P( 0, 0, f.PelvisHi ), T( 8f, 11f ), "denim" );
		k.Part( "Belt", "pelvis", Shape.Box, P( 0, 0, f.PelvisHi - 0.8f ), P( 8.6f, 11.4f, 1.6f ), "leather_brown" );
		k.Part( "Buckle", "pelvis", Shape.Box, P( 4.35f, 0, f.PelvisHi - 0.8f ), P( 0.4f, 2.2f, 1.8f ), "steel" );
		k.Part( "Gut", "spine_01", Shape.Egg, P( 0.8f, 0, (f.PelvisHi + f.Spine1Hi) / 2 ), P( 9.6f, 11f, 9.4f ), "tank_red" );
		k.Part( "Ribcage", "spine_02", Shape.Egg, P( 0.3f, 0, (f.Spine1Hi + f.Spine2Hi) / 2 ), P( 9.6f, 12.6f, 10f ), "tank_red" );
		k.Part( "Barrel chest", "chest", Shape.Egg, P( 0.4f, 0, (f.Spine2Hi + f.ChestHi) / 2 ), P( 10.6f, 15.6f, 11f ), "tank_red" );
		k.Part( "Traps", "chest", Shape.Egg, P( -0.6f, 0, f.ChestHi - 0.6f ), P( 7f, 12f, 4.4f ), "skin_tan" );

		k.Span( "Neck", "neck", Shape.Cylinder, P( 0, 0, f.ChestHi - 1f ), P( 0, 0, f.NeckHi ), T( 4.6f, 4.6f ), "skin_tan", 0.4f );
		k.Part( "Head", "head", Shape.Egg, P( 0.2f, 0, f.HeadMid ), P( 6.8f, 6.2f, 8f ), "skin_tan" );
		k.Part( "Beard", "head", Shape.Egg, P( 1.8f, 0, f.HeadMid - 2f ), P( 4.4f, 5.6f, 4.4f ), "hair_brown" );
		k.Part( "Brow", "head", Shape.Box, P( 3f, 0, f.HeadMid + 1.3f ), P( 1f, 4.6f, 0.8f ), "hair_brown" );
		k.Part( "Buzz cut", "head", Shape.Egg, P( -0.3f, 0, f.HeadHi - 1.8f ), P( 6.6f, 6.2f, 4.2f ), "hair_brown" );

		k.Part( "Shoulder", "clavicle_L", Shape.Egg, f.Shoulder + P( 0, -1.2f, 0.4f ), P( 6.4f, 6.8f, 6.2f ), "skin_tan" );
		k.Span( "Bicep", "upperarm_L", Shape.Egg, f.Shoulder, f.Elbow, T( 5.8f, 5.4f ), "skin_tan", 0.8f );
		k.Span( "Forearm", "forearm_L", Shape.Egg, f.Elbow, f.Wrist, T( 5f, 4.8f ), "skin_tan", 0.6f );
		k.Part( "Tattoo", "forearm_L", Shape.Box, f.Elbow + P( 0, 3.6f, 2.35f ), P( 2f, 2.4f, 0.3f ), "apron_blue" );
		k.Span( "Fist", "hand_L", Shape.Box, f.Wrist + P( 0, 0.2f, 0 ), f.Wrist + P( 0, f.Hand, 0 ), T( 4.2f, 4.6f ), "skin_tan", 0.2f );

		k.Span( "Jeans thigh", "thigh_L", Shape.Cylinder, f.Hip, f.Knee, T( 5.6f, 5.6f ), "denim", 1f );
		k.Span( "Jeans shin", "calf_L", Shape.Cylinder, f.Knee, f.Ankle + P( 0, 0, 2f ), T( 5f, 5f ), "denim" );
		k.Part( "Boot shaft", "calf_L", Shape.Cylinder, P( 0, f.HipY, 5.6f ), P( 5.4f, 5.4f, 5.2f ), "leather_brown" );
		k.Part( "Boot", "foot_L", Shape.Box, P( 2.9f, f.HipY, 1.7f ), P( 11.2f, 5.6f, 3.4f ), "leather_brown" );
		k.Part( "Boot sole", "foot_L", Shape.Box, P( 2.9f, f.HipY, 0.35f ), P( 11.6f, 5.9f, 0.7f ), "rubber_black" );
	}

	/// <summary>Tall and narrow: long neck and arms, olive shirt, beanie, long shoes.</summary>
	static void Stringbean( Kit k )
	{
		var f = k.F;
		f.Torso = 26f;
		f.Neck = 5.5f;
		f.Head = 9f;
		f.ShoulderDrop = 1.2f;
		f.Clavicle = 4.4f;
		f.UpperArm = 11.5f;
		f.Forearm = 10.8f;
		f.Hand = 6f;
		f.HipY = 2.7f;
		f.ToeX = 8f;

		k.Span( "Hips", "pelvis", Shape.Box, P( 0, 0, 27.5f ), P( 0, 0, f.PelvisHi ), T( 5f, 7f ), "denim" );
		k.Span( "Waist", "spine_01", Shape.Cylinder, P( 0, 0, f.PelvisHi ), P( 0, 0, f.Spine1Hi ), T( 5f, 6.6f ), "olive_shirt", 0.5f );
		k.Span( "Belly", "spine_02", Shape.Cylinder, P( 0, 0, f.Spine1Hi ), P( 0, 0, f.Spine2Hi ), T( 5.2f, 7f ), "olive_shirt", 0.5f );
		k.Span( "Chest", "chest", Shape.Box, P( 0, 0, f.Spine2Hi ), P( 0, 0, f.ChestHi ), T( 5.6f, 8.4f ), "olive_shirt", 0.3f );
		k.Part( "Pocket", "chest", Shape.Box, P( 2.9f, 2f, f.ShoulderZ - 3f ), P( 0.3f, 2f, 2.2f ), "denim" );

		k.Span( "Neck", "neck", Shape.Cylinder, P( 0, 0, f.ChestHi ), P( 0, 0, f.NeckHi ), T( 2.4f, 2.4f ), "skin_deep", 0.6f );
		k.Part( "Head", "head", Shape.Egg, P( 0, 0, f.HeadMid ), P( 6.2f, 5.6f, 9.2f ), "skin_deep" );
		k.Part( "Nose", "head", Shape.Egg, P( 3.1f, 0, f.HeadMid - 0.2f ), P( 1.2f, 0.9f, 2f ), "skin_deep" );
		k.Part( "Glasses", "head", Shape.Box, P( 2.95f, 0, f.HeadMid + 0.9f ), P( 0.4f, 4.6f, 1.2f ), "button_black" );
		k.Part( "Beanie", "head", Shape.Egg, P( -0.2f, 0, f.HeadHi - 1.4f ), P( 6.6f, 6f, 5.2f ), "beanie_mustard" );
		k.Part( "Beanie cuff", "head", Shape.Cylinder, P( -0.2f, 0, f.HeadHi - 3.2f ), P( 6.6f, 6f, 1.4f ), "beanie_mustard" );

		k.Span( "Shoulder", "clavicle_L", Shape.Cylinder, P( 0, f.ClavicleIn, f.ShoulderZ ), f.Shoulder, T( 2.6f, 2.6f ), "olive_shirt", 0.8f );
		k.Span( "Sleeve", "upperarm_L", Shape.Cylinder, f.Shoulder, f.Elbow, T( 2.3f, 2.3f ), "olive_shirt", 0.3f );
		k.Span( "Forearm", "forearm_L", Shape.Cylinder, f.Elbow, f.Wrist, T( 1.8f, 1.8f ), "skin_deep", 0.2f );
		k.Span( "Hand", "hand_L", Shape.Egg, f.Wrist, f.Wrist + P( 0, f.Hand, 0 ), T( 1.6f, 3f ), "skin_deep", 0.2f );

		k.Span( "Thigh", "thigh_L", Shape.Cylinder, f.Hip, f.Knee, T( 3f, 3f ), "denim", 0.6f );
		k.Span( "Shin", "calf_L", Shape.Cylinder, f.Knee, f.Ankle + P( 0, 0, 0.5f ), T( 2.6f, 2.6f ), "denim" );
		k.Part( "Sock", "calf_L", Shape.Cylinder, P( 0, f.HipY, 3.6f ), P( 2.2f, 2.2f, 2.6f ), "chef_white" );
		k.Part( "Shoe", "foot_L", Shape.Box, P( 3f, f.HipY, 1.2f ), P( 11.4f, 3.2f, 2.4f ), "leather_brown" );
	}

	/// <summary>An artist's wooden mannequin: pale segments, darker ball joints, no face. A blank to dress.</summary>
	static void Mannequin( Kit k )
	{
		var f = k.F;
		const string body = "mannequin";
		const string joint = "mannequin_joint";

		k.Part( "Hips", "pelvis", Shape.Egg, P( 0, 0, 31.2f ), P( 6.2f, 8.4f, 7.4f ), body );
		k.Ball( "Waist joint", "spine_01", P( 0, 0, f.PelvisHi ), 4.2f, joint );
		k.Part( "Abdomen", "spine_01", Shape.Egg, P( 0, 0, (f.PelvisHi + f.Spine1Hi) / 2 + 0.4f ), P( 5.4f, 6.8f, 6.4f ), body );
		k.Part( "Ribcage", "spine_02", Shape.Egg, P( 0, 0, (f.Spine1Hi + f.Spine2Hi) / 2 + 0.6f ), P( 6f, 8f, 7.4f ), body );
		k.Part( "Chest", "chest", Shape.Egg, P( 0, 0, (f.Spine2Hi + f.ChestHi) / 2 ), P( 6.4f, 9.8f, 8.6f ), body );

		k.Span( "Neck", "neck", Shape.Cylinder, P( 0, 0, f.ChestHi - 0.8f ), P( 0, 0, f.NeckHi ), T( 2.4f, 2.4f ), body, 0.4f );
		k.Part( "Head", "head", Shape.Egg, P( 0.3f, 0, f.HeadMid ), P( 6.6f, 5.8f, 8.4f ), body );

		k.Span( "Clavicle", "clavicle_L", Shape.Egg, P( 0, f.ClavicleIn, f.ShoulderZ ), f.Shoulder, T( 2.4f, 2.4f ), body, 0.4f );
		k.Ball( "Shoulder joint", "upperarm_L", f.Shoulder, 3.2f, joint );
		k.Span( "Upper arm", "upperarm_L", Shape.Egg, f.Shoulder, f.Elbow, T( 2.9f, 2.9f ), body, -0.6f );
		k.Ball( "Elbow joint", "forearm_L", f.Elbow, 2.4f, joint );
		k.Span( "Forearm", "forearm_L", Shape.Egg, f.Elbow, f.Wrist, T( 2.5f, 2.5f ), body, -0.5f );
		k.Ball( "Wrist joint", "hand_L", f.Wrist, 1.8f, joint );
		k.Span( "Hand", "hand_L", Shape.Egg, f.Wrist + P( 0, 0.6f, 0 ), f.Wrist + P( 0, f.Hand, 0 ), T( 1.4f, 3f ), body );

		k.Ball( "Hip joint", "thigh_L", f.Hip, 3.8f, joint );
		k.Span( "Thigh", "thigh_L", Shape.Egg, f.Hip, f.Knee, T( 4.2f, 4.2f ), body, -0.8f );
		k.Ball( "Knee joint", "calf_L", f.Knee, 3f, joint );
		k.Span( "Calf", "calf_L", Shape.Egg, f.Knee, f.Ankle, T( 3.4f, 3.4f ), body, -0.6f );
		k.Ball( "Ankle joint", "foot_L", f.Ankle, 2.2f, joint );
		// A tessellated sphere has no vertex at its pole, so its lowest point sits above centre - radius.
		k.Part( "Foot", "foot_L", Shape.Egg, P( 2.4f, f.HipY, 1.44f ), P( 9f, 3.4f, 3f ), body );
	}

	// -------------------------------------------------------------------------------------------
	// Materials

	/// <summary>Flat colours: linear-ish RGB, roughness, metalness.</summary>
	static readonly Dictionary<string, (float R, float G, float B, float Rough, float Metal)> Palette = new( StringComparer.Ordinal )
	{
		["skin_light"] = (0.92f, 0.74f, 0.61f, 0.65f, 0f),
		["skin_tan"] = (0.74f, 0.52f, 0.37f, 0.65f, 0f),
		["skin_deep"] = (0.40f, 0.26f, 0.18f, 0.6f, 0f),
		["chef_white"] = (0.94f, 0.94f, 0.92f, 0.8f, 0f),
		["check_trousers"] = (0.16f, 0.16f, 0.18f, 0.85f, 0f),
		["apron_blue"] = (0.20f, 0.30f, 0.52f, 0.85f, 0f),
		["kerchief_red"] = (0.78f, 0.12f, 0.12f, 0.7f, 0f),
		["button_black"] = (0.05f, 0.05f, 0.06f, 0.3f, 0f),
		["hair_brown"] = (0.24f, 0.15f, 0.09f, 0.9f, 0f),
		["clog_black"] = (0.08f, 0.08f, 0.09f, 0.45f, 0f),
		["steel"] = (0.72f, 0.73f, 0.75f, 0.35f, 1f),
		["robot_orange"] = (0.95f, 0.45f, 0.10f, 0.4f, 0f),
		["visor_cyan"] = (0.20f, 0.85f, 1.00f, 0.15f, 0f),
		["rubber_black"] = (0.07f, 0.07f, 0.07f, 0.9f, 0f),
		["egg_shell"] = (0.97f, 0.93f, 0.84f, 0.55f, 0f),
		["yolk"] = (1.00f, 0.72f, 0.12f, 0.6f, 0f),
		["denim"] = (0.20f, 0.29f, 0.46f, 0.9f, 0f),
		["leather_brown"] = (0.36f, 0.22f, 0.12f, 0.6f, 0f),
		["tank_red"] = (0.70f, 0.14f, 0.14f, 0.85f, 0f),
		["olive_shirt"] = (0.38f, 0.42f, 0.24f, 0.9f, 0f),
		["beanie_mustard"] = (0.80f, 0.58f, 0.16f, 0.95f, 0f),
		["mannequin"] = (0.86f, 0.76f, 0.60f, 0.5f, 0f),
		["mannequin_joint"] = (0.55f, 0.38f, 0.22f, 0.45f, 0f),
	};

	/// <summary>
	/// A flat-coloured complex material. The colour is the tint over citizen's own white texture, and
	/// model tint is left on so a Tint set on the renderer still reaches it.
	/// </summary>
	static string Vmat( (float R, float G, float B, float Rough, float Metal) c ) =>
		"// Written by Effigy.Tests --playermodels. Flat colour; edit the tint, not the texture.\n"
		+ "Layer0\n{\n"
		+ "\tshader \"shaders/complex.shader\"\n\n"
		+ "\tg_flModelTintAmount \"1.000000\"\n"
		+ $"\tg_vColorTint \"[{F( c.R )} {F( c.G )} {F( c.B )} 1.000000]\"\n\n"
		+ "\tTextureColor \"materials/default/default_color.tga\"\n"
		+ "\tTextureNormal \"materials/default/default_normal.tga\"\n"
		+ "\tTextureRoughness \"materials/default/default_rough.tga\"\n"
		+ "\tTextureAmbientOcclusion \"materials/default/default_ao.tga\"\n\n"
		+ $"\tg_flMetalness \"{F( c.Metal )}\"\n"
		+ $"\tg_flRoughness \"{F( c.Rough )}\"\n"
		+ "}\n";

	static string F( float v ) => v.ToString( "0.000000", CultureInfo.InvariantCulture );
}
