using Sandbox;

namespace Marionette.EditorTools;

/// <summary>
/// Where the viewport's screen-space chrome draws — the view cube, the radial menu. In the editor
/// it is a scene object on the overlay layer (CubeOverlayObject); in the headless render
/// (tools/viewcube-sim) it is an SVG writer, so the chrome can be looked at and got right without
/// s&amp;box running. Everything is in render pixels.
/// </summary>
internal interface IOverlayCanvas
{
	void Circle( Vector2 center, float radius, Color fill, Color border = default, float borderSize = 0f );
	void Line( Vector2 start, Vector2 end, float thickness, Color color );
	void Text( string text, Vector2 pos, float size, Color color );
	void Polygon( Vector2[] points, Color color );
	void Icon( string icon, Rect rect, float size, Color color );
	void RoundedRect( Rect rect, float radius, Color fill, Color border = default, float borderSize = 0f );
}
