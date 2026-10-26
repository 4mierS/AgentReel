namespace Glasstake.Export;

internal static class Changes
{
    /// <summary>
    /// The smallest rectangle that holds every pixel that differs between two frames; at least 1x1.
    /// </summary>
    public static (int X, int Y, int W, int H) Bounds(uint[] previous, uint[] current, int width, int height)
    {
        int top = -1, bottom = -1;

        for (var y = 0; y < height && top < 0; y++)
            if (!current.AsSpan(y * width, width).SequenceEqual(previous.AsSpan(y * width, width)))
                top = y;

        if (top < 0)
            return (0, 0, 1, 1);

        for (var y = height - 1; y >= top && bottom < 0; y--)
            if (!current.AsSpan(y * width, width).SequenceEqual(previous.AsSpan(y * width, width)))
                bottom = y;

        int left = width, right = -1;

        for (var y = top; y <= bottom; y++)
        {
            var row = y * width;

            for (var x = 0; x < left; x++)
            {
                if (current[row + x] != previous[row + x])
                {
                    left = x;
                    break;
                }
            }

            for (var x = width - 1; x > right; x--)
            {
                if (current[row + x] != previous[row + x])
                {
                    right = x;
                    break;
                }
            }
        }

        return (left, top, right - left + 1, bottom - top + 1);
    }
}
