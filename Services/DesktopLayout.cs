using System.Windows;

namespace DevShell.Services;

/// <summary>
/// Grid arrangement for desktop icons, matching how Explorer lays them out: columns are filled
/// top-to-bottom from the left edge, then the next column starts to the right.
/// </summary>
/// <remarks>
/// The pitches are derived from the icon size using the same padding Explorer's default 48px
/// desktop uses (a 78px column and an 88px row), so labels get two lines and neighbouring icons
/// never touch. Tiles are exclusive - one icon per cell - and everything here is worked out in
/// whole cells, so two icons can never end up sharing one.
/// </remarks>
internal static class DesktopLayout
{
    private const double MarginLeft = 8;
    private const double MarginTop = 8;
    private const double HorizontalPadding = 30;
    private const double VerticalPadding = 40;

    public static double CellWidth(double iconSize) => iconSize + HorizontalPadding;

    public static double CellHeight(double iconSize) => iconSize + VerticalPadding;

    /// <summary>
    /// Top-left corner of every icon, in surface coordinates. Everything past the bottom of the
    /// surface spills into the next column.
    /// </summary>
    public static List<Point> Arrange(int count, double surfaceHeight, double iconSize)
    {
        var placements = new List<Point>(count);
        if (count <= 0)
        {
            return placements;
        }

        var cellWidth = CellWidth(iconSize);
        var cellHeight = CellHeight(iconSize);

        var usable = surfaceHeight - MarginTop;
        var rows = Math.Max(1, (int)Math.Floor(usable / cellHeight));

        for (var index = 0; index < count; index++)
        {
            var column = index / rows;
            var row = index % rows;
            placements.Add(new Point(MarginLeft + (column * cellWidth), MarginTop + (row * cellHeight)));
        }

        return placements;
    }

    /// <summary>
    /// The cell a dropped icon lands in: the one whose top-left corner is nearest to where it was
    /// let go, kept inside the surface. Dropping is what snaps - the icon still follows the
    /// pointer freely while it is being dragged.
    /// </summary>
    public static Point Snap(double x, double y, double surfaceWidth, double surfaceHeight, double iconSize)
    {
        var cellWidth = CellWidth(iconSize);
        var cellHeight = CellHeight(iconSize);

        var column = Math.Clamp(ColumnAt(x, iconSize), 0, LastColumn(surfaceWidth, iconSize));
        var row = Math.Clamp(RowAt(y, iconSize), 0, LastRow(surfaceHeight, iconSize));

        return new Point(MarginLeft + (column * cellWidth), MarginTop + (row * cellHeight));
    }

    /// <summary>
    /// The cell nearest to (<paramref name="x"/>, <paramref name="y"/>) that none of the spots in
    /// <paramref name="occupied"/> is sitting on, so letting an icon go on a taken tile slides it to
    /// the closest free one instead of stacking it on its neighbour.
    /// </summary>
    /// <remarks>
    /// The cell the pointer asked for wins whenever it is free, so a drop that collides with nothing
    /// lands exactly where it always did and the icon keeps following the pointer until it is let go.
    /// Failing that the closest free cell wins, measured in whole cells, and a cell in the same column
    /// is preferred to one out to the side at the same distance: a column is what this desktop fills,
    /// one tile at a time. A surface with no free cell left hands back the cell that was asked for,
    /// since a full desktop has nowhere else to put the icon.
    /// </remarks>
    public static Point NearestFreeCell(
        double x, double y, double surfaceWidth, double surfaceHeight, double iconSize, IReadOnlyList<Point> occupied)
    {
        var wanted = Snap(x, y, surfaceWidth, surfaceHeight, iconSize);
        var cellWidth = CellWidth(iconSize);
        var cellHeight = CellHeight(iconSize);

        var wantedColumn = ColumnAt(wanted.X, iconSize);
        var wantedRow = RowAt(wanted.Y, iconSize);

        var taken = TakenCells(iconSize, occupied);

        // Nowhere free at all leaves this as it was: the tile that was asked for.
        var best = wanted;
        var bestDistance = double.MaxValue;
        var bestSideways = 1;
        var bestRow = 0;
        var bestColumn = 0;

        for (var row = 0; row <= LastRow(surfaceHeight, iconSize); row++)
        {
            for (var column = 0; column <= LastColumn(surfaceWidth, iconSize); column++)
            {
                if (taken.Contains((column, row)))
                {
                    continue;
                }

                var across = column - wantedColumn;
                var down = row - wantedRow;

                var distance = (across * across) + (down * down);
                var sideways = across == 0 ? 0 : 1;

                // Distance first, then straight down rather than out to the side, and only after that
                // the topmost and then the leftmost of what is left.
                var closer = distance < bestDistance;
                var betterShape = distance == bestDistance
                    && (sideways < bestSideways
                        || (sideways == bestSideways && (row < bestRow || (row == bestRow && column < bestColumn))));

                if (!closer && !betterShape)
                {
                    continue;
                }

                bestDistance = distance;
                bestSideways = sideways;
                bestRow = row;
                bestColumn = column;
                best = new Point(MarginLeft + (column * cellWidth), MarginTop + (row * cellHeight));
            }
        }

        return best;
    }

    /// <summary>
    /// Which cells the given spots sit on - a spot is read as a cell, so an icon that was placed off
    /// the grid still holds down the tile it is nearest to.
    /// </summary>
    private static HashSet<(int Column, int Row)> TakenCells(double iconSize, IReadOnlyList<Point> occupied)
    {
        var taken = new HashSet<(int Column, int Row)>();
        foreach (var spot in occupied)
        {
            taken.Add((ColumnAt(spot.X, iconSize), RowAt(spot.Y, iconSize)));
        }

        return taken;
    }

    /// <summary>The column a spot's left edge falls in, before any clamping.</summary>
    private static int ColumnAt(double x, double iconSize)
        => (int)Math.Round((x - MarginLeft) / CellWidth(iconSize), MidpointRounding.AwayFromZero);

    /// <summary>The row a spot's top edge falls in, before any clamping.</summary>
    private static int RowAt(double y, double iconSize)
        => (int)Math.Round((y - MarginTop) / CellHeight(iconSize), MidpointRounding.AwayFromZero);

    /// <summary>
    /// The grid is as wide and as tall as the surface has room for; past that an icon belongs on the
    /// last cell rather than half outside the screen.
    /// </summary>
    private static int LastColumn(double surfaceWidth, double iconSize)
        => Math.Max(0, (int)Math.Floor((surfaceWidth - MarginLeft - CellWidth(iconSize)) / CellWidth(iconSize)));

    private static int LastRow(double surfaceHeight, double iconSize)
        => Math.Max(0, (int)Math.Floor((surfaceHeight - MarginTop - CellHeight(iconSize)) / CellHeight(iconSize)));
}
