namespace Odx.Capture;

/// <summary>Required Arma 3 game-window client resolution for a valid ODX Live round.</summary>
public static class Arma3ResolutionRequirement
{
    public const int Width = 1280;
    public const int Height = 720;

    public static bool IsSatisfied(int width, int height) => width == Width && height == Height;
}
