using Sandbox;
using System.Collections.Generic;

/// <summary>
/// Original 1996 Tamagotchi P1 as separate physics parts.
/// Real internals: shells, LCD stack, PCB, CPU blob, crystal, piezo, LR44 cells,
/// battery contacts, screws, keypad membrane, keychain. Press Use (E) to break apart.
/// </summary>
public sealed class TamagotchiTeardownKit : Component
{
	[Property] public bool DisassembleOnUse { get; set; } = true;
	[Property] public bool StartAssembled { get; set; } = true;

	bool _broke;

	record PartSpec( string Name, Vector3 LocalPos, Color Tint );

	static readonly PartSpec[] Parts =
	[
		new( "tama_front_shell", new Vector3( -0.000000f, 0.155176f, 0.000000f ), new Color( 0.9300f, 0.9000f, 0.8600f ) ),
		new( "tama_rear_shell", new Vector3( -0.000000f, -0.164370f, 0.000000f ), new Color( 0.9000f, 0.8700f, 0.8200f ) ),
		new( "tama_battery_cover", new Vector3( 0.000000f, -0.328740f, -0.314961f ), new Color( 0.8800f, 0.8500f, 0.8000f ) ),
		new( "tama_faceplate", new Vector3( 0.000000f, 0.312992f, 0.138707f ), new Color( 0.9500f, 0.4200f, 0.5800f ) ),
		new( "tama_screen_lens", new Vector3( 0.000000f, 0.328740f, 0.275591f ), new Color( 0.5500f, 0.7200f, 0.7000f ) ),
		new( "tama_lcd_polarizer_front", new Vector3( 0.000000f, 0.163386f, 0.275591f ), new Color( 0.1200f, 0.1600f, 0.1200f ) ),
		new( "tama_lcd_glass", new Vector3( 0.000000f, 0.141732f, 0.275591f ), new Color( 0.1800f, 0.2800f, 0.1600f ) ),
		new( "tama_lcd_reflector", new Vector3( 0.000000f, 0.122047f, 0.275591f ), new Color( 0.7800f, 0.8000f, 0.7200f ) ),
		new( "tama_lcd_holder", new Vector3( 0.000000f, 0.137795f, 0.275591f ), new Color( 0.8500f, 0.8200f, 0.7800f ) ),
		new( "tama_lcd_foam", new Vector3( 0.000000f, 0.096457f, 0.275591f ), new Color( 0.7800f, 0.7400f, 0.5500f ) ),
		new( "tama_zebra_strip", new Vector3( 0.000000f, 0.133858f, -0.023622f ), new Color( 0.4500f, 0.4200f, 0.3600f ) ),
		new( "tama_lcd_icons", new Vector3( 0.008337f, 0.301181f, 0.271654f ), new Color( 0.0800f, 0.1000f, 0.0800f ) ),
		new( "tama_button_a", new Vector3( -0.314961f, 0.287220f, -0.649606f ), new Color( 0.9000f, 0.2800f, 0.4800f ) ),
		new( "tama_button_b", new Vector3( 0.000000f, 0.287220f, -0.649606f ), new Color( 0.9000f, 0.2800f, 0.4800f ) ),
		new( "tama_button_c", new Vector3( 0.314961f, 0.287220f, -0.649606f ), new Color( 0.9000f, 0.2800f, 0.4800f ) ),
		new( "tama_carbon_a", new Vector3( -0.314961f, 0.045276f, -0.649606f ), new Color( 0.0800f, 0.0800f, 0.0800f ) ),
		new( "tama_carbon_b", new Vector3( 0.000000f, 0.045276f, -0.649606f ), new Color( 0.0800f, 0.0800f, 0.0800f ) ),
		new( "tama_carbon_c", new Vector3( 0.314961f, 0.045276f, -0.649606f ), new Color( 0.0800f, 0.0800f, 0.0800f ) ),
		new( "tama_keypad_membrane", new Vector3( 0.000000f, 0.055118f, -0.649606f ), new Color( 0.1800f, 0.1800f, 0.1900f ) ),
		new( "tama_reset_button", new Vector3( 0.000000f, -0.208661f, -0.649606f ), new Color( 0.1200f, 0.1200f, 0.1300f ) ),
		new( "tama_pcb", new Vector3( 0.000000f, 0.009843f, 0.059055f ), new Color( 0.1000f, 0.3800f, 0.2000f ) ),
		new( "tama_lcd_pads", new Vector3( 0.000000f, 0.024409f, -0.086614f ), new Color( 0.8300f, 0.6500f, 0.2200f ) ),
		new( "tama_button_pads", new Vector3( 0.000000f, 0.024409f, -0.649606f ), new Color( 0.8300f, 0.6500f, 0.2200f ) ),
		new( "tama_reset_pads", new Vector3( 0.000000f, 0.024409f, -0.649606f ), new Color( 0.8300f, 0.6500f, 0.2200f ) ),
		new( "tama_cpu_blob", new Vector3( 0.000000f, 0.088780f, 0.157480f ), new Color( 0.0500f, 0.0500f, 0.0500f ) ),
		new( "tama_cpu_foam", new Vector3( 0.000000f, 0.066929f, 0.157480f ), new Color( 0.7800f, 0.7400f, 0.5500f ) ),
		new( "tama_crystal", new Vector3( 0.334646f, 0.034449f, -0.196850f ), new Color( 0.7200f, 0.7400f, 0.7600f ) ),
		new( "tama_smd_passives", new Vector3( 0.000000f, 0.027559f, 0.122047f ), new Color( 0.1200f, 0.1200f, 0.1200f ) ),
		new( "tama_pcb_pad_bat_p", new Vector3( -0.275591f, 0.024409f, -0.314961f ), new Color( 0.8300f, 0.6500f, 0.2200f ) ),
		new( "tama_pcb_pad_bat_n", new Vector3( 0.275591f, 0.024409f, -0.314961f ), new Color( 0.8300f, 0.6500f, 0.2200f ) ),
		new( "tama_pcb_pad_piezo_a", new Vector3( -0.196850f, 0.027559f, 0.582677f ), new Color( 0.8300f, 0.6500f, 0.2200f ) ),
		new( "tama_pcb_pad_piezo_b", new Vector3( -0.110236f, 0.027559f, 0.582677f ), new Color( 0.8300f, 0.6500f, 0.2200f ) ),
		new( "tama_battery_l", new Vector3( -0.244094f, -0.093425f, -0.314961f ), new Color( 0.5500f, 0.5600f, 0.5800f ) ),
		new( "tama_battery_r", new Vector3( 0.244094f, -0.093425f, -0.314961f ), new Color( 0.5500f, 0.5600f, 0.5800f ) ),
		new( "tama_contact_pos", new Vector3( -0.244094f, 0.024409f, -0.314961f ), new Color( 0.7800f, 0.7900f, 0.8100f ) ),
		new( "tama_contact_neg", new Vector3( 0.244078f, -0.236153f, -0.314957f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_contact_series", new Vector3( 0.000000f, -0.098917f, -0.098425f ), new Color( 0.7800f, 0.7900f, 0.8100f ) ),
		new( "tama_pull_tab", new Vector3( -0.244094f, -0.051969f, -0.433071f ), new Color( 0.9500f, 0.8800f, 0.2000f ) ),
		new( "tama_piezo_disc", new Vector3( 0.000000f, -0.123720f, 0.570866f ), new Color( 0.7800f, 0.6200f, 0.2800f ) ),
		new( "tama_piezo_wire_p", new Vector3( -0.127243f, -0.058368f, 0.576763f ), new Color( 0.7200f, 0.0800f, 0.0800f ) ),
		new( "tama_piezo_wire_n", new Vector3( -0.025004f, -0.058594f, 0.576765f ), new Color( 0.0800f, 0.0800f, 0.0800f ) ),
		new( "tama_shell_gasket", new Vector3( -0.000000f, -0.000000f, 0.000000f ), new Color( 0.1000f, 0.1000f, 0.1100f ) ),
		new( "tama_keychain_ring", new Vector3( 0.000000f, 0.000000f, 0.984252f ), new Color( 0.8200f, 0.6800f, 0.2200f ) ),
		new( "tama_keychain_chain", new Vector3( 0.068048f, 0.000000f, 1.417323f ), new Color( 0.8200f, 0.6800f, 0.2200f ) ),
		new( "tama_keychain_clasp", new Vector3( 0.216535f, 0.000000f, 1.795276f ), new Color( 0.8200f, 0.6800f, 0.2200f ) ),
		new( "tama_screw_cover_01", new Vector3( -0.334646f, -0.163110f, -0.118110f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_screw_cover_02", new Vector3( 0.334646f, -0.163110f, -0.118110f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_screw_cover_03", new Vector3( -0.334646f, -0.163110f, -0.511811f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_screw_cover_04", new Vector3( 0.334646f, -0.163110f, -0.511811f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_screw_case_01", new Vector3( -0.472441f, -0.104055f, 0.590551f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_screw_case_02", new Vector3( 0.472441f, -0.104055f, 0.590551f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_screw_case_03", new Vector3( -0.472441f, -0.104055f, -0.590551f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_screw_case_04", new Vector3( 0.472441f, -0.104055f, -0.590551f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_screw_pcb_01", new Vector3( -0.433071f, 0.143976f, 0.472441f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_screw_pcb_02", new Vector3( 0.433071f, 0.143976f, 0.472441f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_screw_pcb_03", new Vector3( -0.433071f, 0.143976f, -0.314961f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
		new( "tama_screw_pcb_04", new Vector3( 0.433071f, 0.143976f, -0.314961f ), new Color( 0.7000f, 0.7100f, 0.7300f ) ),
	];

	protected override void OnStart()
	{
		var preview = Components.Get<ModelRenderer>();
		if ( preview is not null )
			preview.Enabled = false;

		if ( GameObject.Children.Count > 0 )
			return;

		foreach ( var spec in Parts )
		{
			var model = Model.Load( $"models/tamagotchi/{spec.Name}.vmdl" );
			if ( model is null || model.IsError )
			{
				Log.Warning( $"[tamagotchi] missing {spec.Name}.vmdl" );
				continue;
			}

			var go = new GameObject( true, spec.Name );
			go.Parent = GameObject;
			go.LocalPosition = spec.LocalPos;
			go.LocalRotation = Rotation.Identity;
			go.Tags.Add( "tama_part" );

			var renderer = go.Components.Create<ModelRenderer>();
			renderer.Model = model;
			renderer.Tint = spec.Tint;

			var collider = go.Components.Create<ModelCollider>();
			collider.Model = model;

			var rb = go.Components.Create<Rigidbody>();
			rb.Gravity = true;
			rb.MotionEnabled = !StartAssembled;
			rb.StartAsleep = StartAssembled;
		}
	}

	protected override void OnUpdate()
	{
		if ( DisassembleOnUse && Input.Pressed( "use" ) )
			BreakApart();
	}

	public void BreakApart()
	{
		if ( _broke ) return;
		_broke = true;

		foreach ( var child in GameObject.Children.ToArray() )
		{
			var rb = child.Components.Get<Rigidbody>();
			if ( rb is not null )
			{
				rb.MotionEnabled = true;
				rb.StartAsleep = false;
			}
			child.SetParent( null, true );
		}
	}
}
