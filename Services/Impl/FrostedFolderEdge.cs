namespace MacExplorer.Services.Impl;

internal static class FrostedFolderEdge
{
    internal readonly record struct Lighting(float Highlight, float Shade, float Scatter, float Bend);

    internal static Lighting Sample(float distance, float verticalPosition, float light)
    {
        if (distance >= 3) return default;
        var t = Math.Clamp((verticalPosition - .55f) / .43f, 0, 1);
        var lower = t * t * (3 - 2 * t);
        var rim = distance / (.85f - .25f * lower);
        var highlight = Gaussian(rim - 1, .65f) * (.08f + .55f * Math.Max(light, 0));
        var shade = Gaussian(rim - 1.9f, .6f) * .012f * (1 - lower);
        var scatter = Gaussian(rim - .7f, .9f) * lower * .055f;
        var bend = Math.Clamp(1 - Math.Max(distance, 0) / 2.5f, 0, 1);
        return new(highlight, shade, scatter, bend * bend * .45f);
    }

    private static float Gaussian(float x, float width) => MathF.Exp(-x * x / (width * width));
}
