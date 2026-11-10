namespace Pad.Core;

/// <summary>
/// What a person recognizes a home by: the projects and the conversations in it.
/// Sessions live in the instance home, not in the profile that gets launched.
/// </summary>
public static class HomeHover
{
    public const int Take = 3;

    public static string Text(string home) => Format(Dialogs.Read(home, 24));

    public static string Format(IReadOnlyList<DialogRow> rows)
    {
        var talk = (rows ?? [])
            .Where(r => r.Subagent.Length == 0 && !(r.Blank && r.Turns == 0))
            .ToList();

        var projects = new List<string>();
        foreach (var row in talk)
        {
            if (row.Workspace.Length == 0) continue;
            if (projects.Contains(row.Workspace)) continue;
            projects.Add(row.Workspace);
            if (projects.Count == Take) break;
        }

        var sessions = talk.Take(Take).ToList();
        if (projects.Count == 0 && sessions.Count == 0) return "还没有会话";

        var lines = new List<string>();
        if (projects.Count > 0)
        {
            lines.Add("项目");
            lines.AddRange(projects);
        }
        if (sessions.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add("会话");
            foreach (var row in sessions)
                lines.Add(row.Title + "  " + row.AgeText);
        }
        return string.Join("\n", lines);
    }
}
