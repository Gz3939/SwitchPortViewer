using System.Text.RegularExpressions;

namespace SwitchPortViewer;

public sealed class PortInfo
{
    public int Number { get; init; }
    public int? Pvid { get; set; }
    public SortedSet<int> UntaggedVlans { get; } = [];
    public SortedSet<int> TaggedVlans { get; } = [];
    public string? Trunk { get; set; }
    public string? TrunkMode { get; set; }
    public string? LinkType { get; set; }
    public string? Description { get; set; }
    public bool Disabled { get; set; }
    public string? SpeedDuplex { get; set; }
    public string Type => Trunk is not null ? $"802.1Q 聚合埠 ({TrunkMode ?? "Trunk"})" : LinkType == "trunk" || TaggedVlans.Count > 0 ? Pvid is null ? "802.1Q 純標記埠" : "802.1Q Trunk/混合埠" : "Access 埠";
}

public sealed class SwitchConfig
{
    public string Hostname { get; set; } = "未命名交換器";
    public string Model { get; set; } = "未識別型號";
    public string Format { get; set; } = "Aruba/HP ProCurve";
    public SortedDictionary<int, string> Vlans { get; } = [];
    public List<PortInfo> Ports { get; } = [];
    public List<string> Warnings { get; } = [];
}

public static partial class SwitchConfigParser
{
    [GeneratedRegex(@"^(?<from>\d+)(?:-(?<to>\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex PortRangeRegex();

    [GeneratedRegex("^hostname\\s+\"?(?<value>[^\"]+)\"?$", RegexOptions.IgnoreCase)]
    private static partial Regex HostnameRegex();

    [GeneratedRegex(@"^module\s+\d+\s+type\s+(?<value>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ModuleRegex();

    [GeneratedRegex(@"^trunk\s+(?<members>\S+)\s+(?<name>\S+)\s*(?<mode>\S*)", RegexOptions.IgnoreCase)]
    private static partial Regex TrunkRegex();

    [GeneratedRegex(@"^vlan\s+(?<id>\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VlanRegex();

    [GeneratedRegex(@"^interface\s+(?<id>\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex InterfaceRegex();

    [GeneratedRegex("^name\\s+\"?(?<value>[^\"]+)\"?$", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    private sealed record VlanCommand(int Vlan, string Action, string Members);
    private sealed record TrunkGroup(string Name, string Mode, List<int> Members);

    public static SwitchConfig Parse(string text)
    {
        if (text.Contains("interface GigabitEthernet", StringComparison.OrdinalIgnoreCase) || text.Contains("interface Ten-GigabitEthernet", StringComparison.OrdinalIgnoreCase))
            return OtherConfigParsers.ParseComware(text);
        if (text.Contains("vlan participation", StringComparison.OrdinalIgnoreCase) || text.Contains("vlan database", StringComparison.OrdinalIgnoreCase))
            return OtherConfigParsers.ParseOfficeConnect(text);
        if (text.Contains("interface port-channel", StringComparison.OrdinalIgnoreCase) && text.Contains("fixed ", StringComparison.OrdinalIgnoreCase))
            return OtherConfigParsers.ParseZyxel(text);
        return ParseProCurve(text);
    }

    private static SwitchConfig ParseProCurve(string text)
    {
        var result = new SwitchConfig();
        var commands = new List<VlanCommand>();
        var groups = new Dictionary<string, TrunkGroup>(StringComparer.OrdinalIgnoreCase);
        var explicitPorts = new HashSet<int>();
        var interfaceData = new Dictionary<int, (bool Disabled, string? Speed)>();
        int? currentVlan = null;
        int? currentInterface = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            var hostname = HostnameRegex().Match(line);
            if (hostname.Success) { result.Hostname = hostname.Groups["value"].Value.Trim(); continue; }
            var module = ModuleRegex().Match(line);
            if (module.Success) { result.Model = module.Groups["value"].Value; continue; }
            var trunk = TrunkRegex().Match(line);
            if (trunk.Success)
            {
                var members = ExpandNumericPorts(trunk.Groups["members"].Value).ToList();
                foreach (var port in members) explicitPorts.Add(port);
                groups[trunk.Groups["name"].Value] = new(trunk.Groups["name"].Value, trunk.Groups["mode"].Value.ToUpperInvariant(), members);
                currentVlan = null; currentInterface = null;
                continue;
            }
            var vlan = VlanRegex().Match(line);
            if (vlan.Success)
            {
                currentVlan = int.Parse(vlan.Groups["id"].Value);
                result.Vlans.TryAdd(currentVlan.Value, $"VLAN {currentVlan}");
                currentInterface = null;
                continue;
            }
            var iface = InterfaceRegex().Match(line);
            if (iface.Success)
            {
                currentInterface = int.Parse(iface.Groups["id"].Value);
                explicitPorts.Add(currentInterface.Value);
                currentVlan = null;
                interfaceData.TryAdd(currentInterface.Value, (false, null));
                continue;
            }
            if (line.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                currentVlan = null; currentInterface = null; continue;
            }
            if (currentVlan is int vlanId)
            {
                var name = NameRegex().Match(line);
                if (name.Success) { result.Vlans[vlanId] = name.Groups["value"].Value.Trim(); continue; }
                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                var action = parts.Length >= 2 && parts[0].Equals("no", StringComparison.OrdinalIgnoreCase) ? $"no {parts[1].ToLowerInvariant()}" : parts[0].ToLowerInvariant();
                var members = action.StartsWith("no ") && parts.Length == 3 ? parts[2] : parts.Length >= 2 ? line[(parts[0].Length + 1)..].Trim() : "";
                if (action is "tagged" or "untagged" or "no tagged" or "no untagged") commands.Add(new(vlanId, action, members));
                continue;
            }
            if (currentInterface is int portNumber)
            {
                var data = interfaceData[portNumber];
                if (line.Equals("disable", StringComparison.OrdinalIgnoreCase)) data.Disabled = true;
                else if (line.Equals("enable", StringComparison.OrdinalIgnoreCase)) data.Disabled = false;
                else if (line.StartsWith("speed-duplex ", StringComparison.OrdinalIgnoreCase)) data.Speed = line[13..].Trim();
                interfaceData[portNumber] = data;
            }
        }

        int maxPort = explicitPorts.DefaultIfEmpty(0).Max();
        foreach (var command in commands)
            foreach (var token in command.Members.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                foreach (var number in ExpandNumericPorts(token)) maxPort = Math.Max(maxPort, number);
        if (maxPort == 0) throw new FormatException("找不到交換器埠。請確認這是 Aruba/HP ProCurve 格式配置檔。");
        if (maxPort > 512) throw new FormatException("埠號超過 512；請確認配置檔格式。");

        var portMap = Enumerable.Range(1, maxPort).ToDictionary(n => n, n => new PortInfo { Number = n, Pvid = 1 });
        result.Vlans.TryAdd(1, "DEFAULT_VLAN");
        foreach (var group in groups.Values)
            foreach (var number in group.Members)
            {
                portMap[number].Trunk = group.Name;
                portMap[number].TrunkMode = group.Mode;
            }
        foreach (var command in commands)
        {
            foreach (var token in command.Members.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                IEnumerable<int> targetPorts;
                if (groups.TryGetValue(token, out var group)) targetPorts = group.Members;
                else if (PortRangeRegex().IsMatch(token)) targetPorts = ExpandNumericPorts(token);
                else { result.Warnings.Add($"無法解析 VLAN {command.Vlan} 的成員：{token}"); continue; }
                foreach (var number in targetPorts)
                {
                    var port = portMap[number];
                    switch (command.Action)
                    {
                        case "untagged": port.Pvid = command.Vlan; port.UntaggedVlans.Clear(); port.UntaggedVlans.Add(command.Vlan); port.TaggedVlans.Remove(command.Vlan); break;
                        case "tagged": port.TaggedVlans.Add(command.Vlan); port.UntaggedVlans.Remove(command.Vlan); if (port.Pvid == command.Vlan) port.Pvid = null; break;
                        case "no untagged": port.UntaggedVlans.Remove(command.Vlan); if (port.Pvid == command.Vlan) port.Pvid = null; break;
                        case "no tagged": port.TaggedVlans.Remove(command.Vlan); break;
                    }
                }
            }
        }
        foreach (var (number, data) in interfaceData)
        {
            portMap[number].Disabled = data.Disabled;
            portMap[number].SpeedDuplex = data.Speed;
        }
        foreach (var port in portMap.Values)
            if (port.Pvid is int pvid && port.UntaggedVlans.Count == 0) port.UntaggedVlans.Add(pvid);
        result.Ports.AddRange(portMap.Values.OrderBy(p => p.Number));
        return result;
    }

    private static IEnumerable<int> ExpandNumericPorts(string text)
    {
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = PortRangeRegex().Match(token);
            if (!match.Success) continue;
            var start = int.Parse(match.Groups["from"].Value);
            var end = match.Groups["to"].Success ? int.Parse(match.Groups["to"].Value) : start;
            if (start < 1 || end < start || end > 512) continue;
            for (var i = start; i <= end; i++) yield return i;
        }
    }
}
