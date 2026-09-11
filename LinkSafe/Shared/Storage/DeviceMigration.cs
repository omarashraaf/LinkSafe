using Shared.Models;

namespace Shared.Storage;

public static class DeviceMigration
{
    public static List<RoamingRestoreCandidate> OtherDevices(IEnumerable<RoamingRestoreCandidate> candidates, string domainUser, string computer) =>
        candidates.Where(c => c.SourceDomainUser.Equals(domainUser, StringComparison.OrdinalIgnoreCase) &&
                              !c.SourceComputerName.Equals(computer, StringComparison.OrdinalIgnoreCase) && c.AvailableFolders.Count > 0)
            .OrderByDescending(c => c.LastBackupTime ?? c.LastSeenAt).ToList();

    public static string Preview(string user, string sourceComputer, MissingRestorePlan plan, IReadOnlyDictionary<string, string> roots)
    {
        var destinations = roots.Where(root => plan.Missing.Any(file =>
                file.File.RelativePath.StartsWith(root.Key + "/", StringComparison.OrdinalIgnoreCase)))
            .Select(root => root.Key + " -> " + root.Value).Take(12);
        return $"Copy backup data for {user} from {sourceComputer} to this device?\n\n" +
            $"{plan.Missing.Count:N0} missing files will be copied into these local locations:\n" +
            string.Join("\n", destinations) +
            $"\n\n{plan.Existing:N0} existing local files will be kept. Source/server backups will NOT be deleted.\n" +
            "This places files inside the real user/app folders, not in a downloaded ZIP or a separate device folder.\n" +
            "Close affected apps/browsers first. This does not install apps or transfer Windows registry settings, saved passwords or device-bound credentials.\n" +
            $"\nWarnings: {plan.Warnings.Count:N0}. " + string.Join("; ", plan.Warnings.Take(3)) + "\n\nCopy missing files now?";
    }
}
