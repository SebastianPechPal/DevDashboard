using System.Text;

namespace DevDashboard.Views;

/// <summary>Turns a rendered frame into an in-place repaint. Homing + redrawing avoids the
/// clear-screen flicker, but Spectre only writes as many columns as a line needs — so when a
/// line shrinks (a column collapses, content shifts left) the previous frame's wider tail stays
/// on screen. Draw each line at an absolute row and clear its tail (ESC[K), then clear everything
/// below the frame (ESC[0J), so every stale cell is overwritten.</summary>
public static class FrameBlitter
{
    private const char Esc = (char)27;

    public static string ToInPlaceRepaint(string frame)
    {
        var lines = frame.Replace("\r", "").TrimEnd('\n').Split('\n');
        var sb = new StringBuilder();
        for (var row = 0; row < lines.Length; row++)
        {
            sb.Append(Esc).Append('[').Append(row + 1).Append(";1H"); // cursor to start of this row
            sb.Append(lines[row]);
            sb.Append(Esc).Append("[0m").Append(Esc).Append("[K");    // reset, then clear to line end
        }
        sb.Append(Esc).Append('[').Append(lines.Length + 1).Append(";1H"); // just below the frame
        sb.Append(Esc).Append("[0J");                                       // clear everything below
        return sb.ToString();
    }
}
