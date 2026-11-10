using System.IO;

namespace Pad.Core;

/// <summary>
/// 一条「最近会话」：哪个实例、哪个会话、标题、最后一次动过的时间。
/// 跨实例汇总，用来做托盘/快捷键唤出的面板（不关心它属于哪种 bundle——打开时由 JumpPlanner 决定）。
/// </summary>
public sealed record RecentSession(
    string InstanceId,
    string InstanceName,
    string SessionId,
    string Title,
    string Detail,
    DateTimeOffset? At)
{
    public string AgeText => Age(At);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>「刚刚 / 12 分钟前 / 3 小时前 / 2 天前 / 11-03」。</summary>
    public static string Age(DateTimeOffset? at)
    {
        if (at is null) return "";
        var span = DateTimeOffset.Now - at.Value;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} 天前";
        return at.Value.ToLocalTime().ToString("MM-dd");
    }
}

public static class RecentSessions
{
    /// <summary>
    /// 扫每个实例的 home，取最近的会话（每个 home 最多 perHome 条），按时间合并排序。
    /// 只读 session 头部，不解析完整转录。
    /// </summary>
    public static IReadOnlyList<RecentSession> List(Launcher launcher, int perHome = 8, int max = 40)
    {
        var rows = new List<RecentSession>();
        IEnumerable<Instance> instances;
        try { instances = launcher.Instances(); }
        catch { return rows; }

        foreach (var inst in instances)
        {
            if (string.IsNullOrWhiteSpace(inst.Home) || !Directory.Exists(inst.Home)) continue;
            IReadOnlyList<DialogRow> dialogs;
            try { dialogs = Dialogs.Read(inst.Home, perHome); }
            catch { continue; }

            foreach (var d in dialogs)
            {
                if (d.Blank || d.Turns == 0) continue;
                if (!string.IsNullOrWhiteSpace(d.Subagent)) continue;   // 子 agent 的会话不单列
                var title = string.IsNullOrWhiteSpace(d.Title) ? ShortId(d.Id) : d.Title.Trim();
                var detail = string.IsNullOrWhiteSpace(d.Cwd) ? d.Model : d.Cwd;
                rows.Add(new RecentSession(inst.Id, inst.Name, d.Id, title, detail ?? "", d.LastActivity));
            }
        }

        return rows
            .OrderByDescending(r => r.At ?? DateTimeOffset.MinValue)
            .Take(max)
            .ToList();
    }

    static string ShortId(string id) =>
        string.IsNullOrWhiteSpace(id) ? "(无名会话)" : id.Length <= 10 ? id : id[..10];
}
