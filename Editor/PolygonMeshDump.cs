using Editor;
using Sandbox;
using System;
using System.Reflection;
using System.Linq;

namespace Marionette.EditorTools;

public static class PolygonMeshDump
{
    [Menu( "Editor", "Marionette/Dump PolygonMesh", "bug_report" )]
    public static void DumpPolygonMesh()
    {
        var t = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany( a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } } )
            .FirstOrDefault( x => x.Name == "PolygonMesh" );

        if ( t == null ) { Log.Info( "PolygonMesh not found in any loaded assembly" ); return; }

        Log.Info( $"Found: {t.FullName} in {t.Assembly.GetName().Name}" );

        var props = t.GetProperties( BindingFlags.Public | BindingFlags.Instance );
        Log.Info( "PROPERTIES: " + string.Join( ", ", props.Select( p => $"{p.PropertyType.Name} {p.Name}" ) ) );

        var methods = t.GetMethods( BindingFlags.Public | BindingFlags.Instance )
            .Where( m => !m.IsSpecialName ).Select( m => m.Name ).Distinct();
        Log.Info( "METHODS: " + string.Join( ", ", methods ) );
    }
}
