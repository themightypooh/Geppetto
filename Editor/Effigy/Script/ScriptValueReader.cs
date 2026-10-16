namespace Effigy;

/// <summary>
/// A feature that reads some `key=value` pairs of a build script itself, before the usual lookup
/// by parameter label or field name. For values whose script spelling is not the parameter's own
/// type: Profile's `along=1,0,1` is a direction, where Along is otherwise a named option.
/// </summary>
public interface IScriptValueReader
{
	/// <summary>True when this feature took the pair. <paramref name="error"/> is then null if it was
	/// applied, or says what was wrong with the value.</summary>
	bool HandlesScriptValue( string key, string value, out string error );
}
