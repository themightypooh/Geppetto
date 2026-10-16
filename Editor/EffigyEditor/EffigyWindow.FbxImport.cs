using Effigy;
using Sandbox;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// What picking an FBX for an Import does beyond what picking an OBJ does: the file's skeleton
/// becomes the rig when the document has none, and the status bar says so.
///
/// THE SAME RULE AS THE WEARER. Only an empty rig is filled (see
/// <see cref="ImportFeature.InstallSkeleton"/>); a rig somebody already drew is kept, and the note
/// says how to take the file's bones instead. Like the wearer's skeleton, the bones are document
/// state outside the undo snapshot, so cancelling the Import leaves them - the note says that too.
/// </summary>
public sealed partial class EffigyWindow
{
	/// <summary>Install the import's FBX skeleton if the rig is empty. Returns a line for the status
	/// bar, or null when the source is not an FBX with bones.</summary>
	private string TakeImportedSkeleton( ImportFeature import )
	{
		if ( _studio is null || import is null || !import.IsFbxSource )
			return null;

		var bones = ImportFeature.InstallSkeleton( _studio, import, out var message );

		if ( string.IsNullOrEmpty( message ) )
			return null;

		if ( bones > 0 )
		{
			_rigPanel?.Refresh();
			message += " Its skin weights ride on the imported parts. Cancelling the Import keeps the bones; delete them in Rig if you don't want them.";
		}

		Log.Info( $"[Effigy] {import.Name}: {message}" );
		return message;
	}

	/// <summary>Called by the Import dialog's browse button after it re-points the source.</summary>
	private void OnImportSourcePicked( ImportFeature import )
	{
		if ( TakeImportedSkeleton( import ) is { } note )
			SetPrompt( note );
	}
}
