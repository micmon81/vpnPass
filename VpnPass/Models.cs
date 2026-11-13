using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace VpnPass;

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public List<string> Targets { get; set; } = [];
}

public sealed class Settings
{
    public string AdapterId { get; set; } = "";
    public List<Profile> Profiles { get; set; } = [new() { Name = "ChatGPT", Targets = ["chatgpt.com"] }];
}

public static class TargetRules
{
    public static string Normalize(string value)
    {
        value = value.Trim().TrimEnd('.');
        if (IPAddress.TryParse(value, out var address))
        {
            if (!IsPublic(address)) throw new ArgumentException("Use a public IP address; local and private destinations are not supported.");
            return address.ToString();
        }
        if (value.Length == 0 || value.IndexOfAny(['/', '\\', ':', '*', '%', ' ', '@']) >= 0)
            throw new ArgumentException("Enter a domain such as chatgpt.com or a single public IP. URLs, wildcards and subnet ranges are not supported.");
        value = new IdnMapping().GetAscii(value).ToLowerInvariant();
        if (value.Length > 253 || !value.Contains('.') || value.Split('.').Any(label =>
            label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-') ||
            label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("Enter a valid domain name, such as chatgpt.com.");
        if (value.EndsWith(".local") || value.EndsWith(".localhost") || value.EndsWith(".internal"))
            throw new ArgumentException("Local network domains are not supported.");
        return value;
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
                !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
                !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
                !(b[0] == 192 && b[1] == 168) && !(b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) &&
                !(b[0] == 198 && b[1] is 18 or 19) && !(b[0] == 198 && b[1] == 51 && b[2] == 100) &&
                !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 &&
            (b[0] & 0xE0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8);
    }

    public static List<string> ParseLines(string text)
    {
        var targets = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0) throw new ArgumentException("Add at least one domain or IP address.");
        if (targets.Count > 32) throw new ArgumentException("Use up to 32 destinations per rule.");
        return targets;
    }

    public static void Validate(Settings settings)
    {
        if (settings.AdapterId is null || (settings.AdapterId.Length > 0 && !Guid.TryParse(settings.AdapterId, out _)))
            throw new ArgumentException("The home connection ID must be empty (automatic) or a valid adapter GUID.");
        if (settings.Profiles is null || settings.Profiles.Count > 32) throw new ArgumentException("Use up to 32 routing rules.");
        var ids = new HashSet<string>();
        foreach (var profile in settings.Profiles)
        {
            if (profile is null || !Guid.TryParse(profile.Id, out _) || !ids.Add(profile.Id))
                throw new ArgumentException("Rule IDs must be unique GUIDs.");
            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 60)
                throw new ArgumentException("Rule names must contain 1 to 60 characters.");
            if (profile.Targets is null) throw new ArgumentException("Missing rule destinations.");
            profile.Targets = ParseLines(string.Join('\n', profile.Targets));
        }
        if (settings.Profiles.SelectMany(p => p.Targets).Distinct().Count() > 64)
            throw new ArgumentException("Use up to 64 different domains or IPs across all rules.");
    }
}

public sealed record Resolution(string[] Addresses, string[] Warnings);

public static class Resolver
{
    public static async Task<Resolution> ResolveAsync(IEnumerable<string> targets,
        Func<string, Task<IPAddress[]>> lookup, CancellationToken token = default)
    {
        using var slots = new SemaphoreSlim(4);
        var results = await Task.WhenAll(targets.Distinct(StringComparer.OrdinalIgnoreCase).Select(async target =>
        {
            await slots.WaitAsync(token);
            try
            {
                var normalized = TargetRules.Normalize(target);
                var addresses = IPAddress.TryParse(normalized, out var literal) ? [literal] :
                    await lookup(normalized).WaitAsync(TimeSpan.FromSeconds(3), token);
                var publicAddresses = addresses.Where(TargetRules.IsPublic).Select(a => a.ToString()).Distinct().ToArray();
                var warnings = new List<string>();
                if (publicAddresses.Length == 0) warnings.Add($"{target}: no public addresses resolved.");
                else if (publicAddresses.Length != addresses.Distinct().Count()) warnings.Add($"{target}: non-public addresses were ignored.");
                return new Resolution(publicAddresses, warnings.ToArray());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new Resolution([], [$"{target}: {ex.Message}"]);
            }
            finally { slots.Release(); }
        }));
        return new(results.SelectMany(r => r.Addresses).Distinct().ToArray(), results.SelectMany(r => r.Warnings).ToArray());
    }
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static Settings Load(string path)
    {
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Empty settings file.");
        TargetRules.Validate(settings);
        return settings;
    }
    public static void Save(string path, Settings settings)
    {
        TargetRules.Validate(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
