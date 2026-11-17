using System.IO;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>A EuroScope process Launchpad started, bound to the launch-set entry and profile it used.</summary>
public sealed record EuroScopeInstanceRecord(Guid EntryId, string? ProfilePath, string ProfileName, int Order,
    ApplicationProcessIdentity Process);

/// <summary>The parts of a launch-set entry that editor and row warnings inspect.</summary>
public sealed record EuroScopeLaunchCheck(string ProfilePath, bool Enabled);

public enum EuroScopeLaunchStepKind { Start, AlreadyRunning, Skip }

public sealed record EuroScopeLaunchStep(EuroScopeLaunchEntry Entry, string ProfileName, int Order,
    EuroScopeLaunchStepKind Kind, int DelaySeconds, string? Message);

/// <summary>Pure planning for the ordered launch set. It never starts or inspects processes itself.</summary>
public static class EuroScopeLaunchPlanner
{
    public static string ProfileName(string? profilePath) =>
        string.IsNullOrWhiteSpace(profilePath) ? "No profile" : Path.GetFileNameWithoutExtension(profilePath);

    public static IReadOnlyList<EuroScopeLaunchEntry> Normalize(IEnumerable<EuroScopeLaunchEntry>? entries) =>
        (entries ?? []).Where(entry => entry is not null).Take(EuroScopeLaunchEntry.MaxEntries)
            .Select(entry => entry with
            {
                Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
                ProfilePath = entry.ProfilePath ?? string.Empty,
                DelaySeconds = Math.Clamp(entry.DelaySeconds, 0, EuroScopeLaunchEntry.MaxDelaySeconds)
            }).ToArray();

    /// <summary>Names of enabled profiles whose file is gone (or cannot be checked), in launch order.</summary>
    public static IReadOnlyList<string> MissingProfiles(IEnumerable<EuroScopeLaunchCheck> entries, Func<string, bool> exists) =>
        entries.Where(entry => entry.Enabled && !string.IsNullOrWhiteSpace(entry.ProfilePath) && !Exists(entry.ProfilePath, exists))
            .Select(entry => ProfileName(entry.ProfilePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Names of profiles used by more than one enabled entry.</summary>
    public static IReadOnlyList<string> DuplicateProfiles(IEnumerable<EuroScopeLaunchCheck> entries) =>
        entries.Where(entry => entry.Enabled && !string.IsNullOrWhiteSpace(entry.ProfilePath))
            .GroupBy(entry => entry.ProfilePath.Trim(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
            .Select(group => ProfileName(group.Key)).ToArray();

    /// <summary>
    /// Inline editor warnings. Neither blocks saving or launching: duplicates share session files, and
    /// missing profiles are skipped at launch.
    /// </summary>
    public static IReadOnlyList<string> Warnings(IReadOnlyCollection<EuroScopeLaunchCheck> entries, Func<string, bool> exists)
    {
        var warnings = new List<string>();

        var duplicates = DuplicateProfiles(entries);
        if (duplicates.Count > 0)
            warnings.Add($"{string.Join(", ", duplicates)} {(duplicates.Count == 1 ? "is" : "are")} used by more than one instance. " +
                "Those instances write the same session and settings files.");

        var missing = MissingProfiles(entries, exists);
        if (missing.Count > 0)
            warnings.Add($"{string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not in the data folder. " +
                $"Launch skips {(missing.Count == 1 ? "it" : "them")} until you choose another profile.");

        return warnings;
    }

    private static bool Exists(string path, Func<string, bool> exists)
    {
        try { return exists(path); }
        catch { return false; }
    }

    /// <param name="validate">Returns a skip reason for an entry, or null when it may start.</param>
    public static IReadOnlyList<EuroScopeLaunchStep> Build(IEnumerable<EuroScopeLaunchEntry> entries,
        IReadOnlyCollection<EuroScopeInstanceRecord> running, Func<EuroScopeLaunchEntry, string?> validate)
    {
        var steps = new List<EuroScopeLaunchStep>();
        var runningIds = running.Select(record => record.EntryId).ToHashSet();

        int order = 0;
        bool firstStart = true;

        foreach (var entry in Normalize(entries).Where(entry => entry.Enabled))
        {
            order++;
            var name = ProfileName(entry.ProfilePath);

            if (runningIds.Contains(entry.Id))
            {
                steps.Add(new(entry, name, order, EuroScopeLaunchStepKind.AlreadyRunning, 0, "Already running · left open."));
                continue;
            }

            string? blocked;
            try { blocked = validate(entry); }
            catch (Exception error) { blocked = error.Message; }
            if (blocked is not null)
            {
                steps.Add(new(entry, name, order, EuroScopeLaunchStepKind.Skip, 0, "Skipped · " + blocked));
                continue;
            }

            // A profile used twice is allowed; the editor warns about it, so launching does not.
            steps.Add(new(entry, name, order, EuroScopeLaunchStepKind.Start, firstStart ? 0 : entry.DelaySeconds, null));
            firstStart = false;
        }

        return steps;
    }
}
