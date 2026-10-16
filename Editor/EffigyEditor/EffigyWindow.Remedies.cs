using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

// ============================================================================
//  Fix buttons on feature callouts. A diagnostic's RemedyActions are the fixes it can name
//  exactly — the Subdivide a stroke needs above it, the Unwrap a bake needs, the folder it writes
//  to, the workspace a garment's rig lives in — and each becomes a button next to Open.
//  The edit itself is the kernel's FeatureRemedy.Apply, which scripts (`remedy`) share.
// ============================================================================

public sealed partial class EffigyWindow
{
	private void ApplyRemedy( Feature feature, RemedyAction action )
	{
		if ( _studio is null || feature is null || action is null )
			return;

		string folder = null;

		switch ( action.Kind )
		{
			case RemedyKind.OpenWorkspace:
				if ( Enum.TryParse<EffigyWorkspace>( action.Workspace, true, out var workspace ) )
				{
					SetWorkspace( workspace );
					SetPrompt( feature.Diagnostic is { Remedies.Count: > 0 } diagnostic
						? $"{feature.Name}: {diagnostic.Remedies[0]}."
						: $"{feature.Name}: {action.Label}." );
				}
				else
				{
					Refuse( $"There is no {action.Workspace} workspace to open." );
				}

				return;

			case RemedyKind.ChooseFolder:
			{
				var fd = new FileDialog( null )
				{
					Title = $"{feature.Name}: choose a folder for its files...",
					Directory = Project.Current?.GetAssetsPath() ?? "",
				};

				fd.SetFindDirectory();

				if ( !fd.Execute() || string.IsNullOrWhiteSpace( fd.SelectedFile ) )
				{
					SetPrompt( "No folder chosen, so nothing changed." );
					return;
				}

				folder = fd.SelectedFile;
				break;
			}
		}

		if ( _dialog?.Feature is not null && !ReferenceEquals( _dialog.Feature, feature ) )
			_dialog.AcceptNow();

		RecordUndo();
		var outcome = FeatureRemedy.Apply( _studio, feature, action, folder );

		if ( !outcome.Ok )
		{
			Refuse( outcome.Message );
			return;
		}

		RebuildStudio();

		if ( ReferenceEquals( _dialog?.Feature, feature ) )
			_dialog.Open( feature, isNew: false );

		SetPrompt( feature.Diagnostic is null
			? $"{outcome.Message} {feature.Name} builds now. Ctrl+Z takes the fix back."
			: $"{outcome.Message} {feature.Name} still says: {feature.Diagnostic.Problem}." );
	}
}
