using System.Text.RegularExpressions;

namespace SwitchPortViewer;

public static class OtherConfigParsers
{
    private sealed class ComwarePort
    {
        public bool IsTrunk;
        public int Pvid = 1;
        public HashSet<int>? Permit;
        public bool PermitAll;
        public string? Aggregation;
        public string? Description;
        public bool Disabled;
    }

    public static SwitchConfig ParseComware(string text)
    {
        var config = new SwitchConfig { Format = "HPE Comware" };
        var physical = new Dictionary<int, ComwarePort>();
        var aggregates = new Dictionary<string, ComwarePort>(StringComparer.OrdinalIgnoreCase);
        ComwarePort? current = null;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "#") { if (line == "#") current = null; continue; }
            var sysname = Regex.Match(line, @"^sysname\s+(.+)$", RegexOptions.IgnoreCase);
            if (sysname.Success) { config.Hostname = Unquote(sysname.Groups[1].Value); continue; }
            var vlan = Regex.Match(line, @"^vlan\s+(\d+)(?:\s+to\s+(\d+))?$", RegexOptions.IgnoreCase);
            if (vlan.Success)
            {
                foreach (var id in ExpandIds(vlan.Groups[1].Value + (vlan.Groups[2].Success ? "-" + vlan.Groups[2].Value : "")))
                    config.Vlans.TryAdd(id, $"VLAN {id}");
                current = null; continue;
            }
            var aggregate = Regex.Match(line, @"^interface\s+Bridge-Aggregation(\d+)$", RegexOptions.IgnoreCase);
            if (aggregate.Success)
            {
                var name = "BAGG" + aggregate.Groups[1].Value;
                if (!aggregates.TryGetValue(name, out current)) aggregates[name] = current = new ComwarePort();
                continue;
            }
            var iface = Regex.Match(line, @"^interface\s+(?:GigabitEthernet|Ten-GigabitEthernet)(\d+/\d+/(\d+))$", RegexOptions.IgnoreCase);
            if (iface.Success)
            {
                int id = int.Parse(iface.Groups[2].Value);
                if (!physical.TryGetValue(id, out current)) physical[id] = current = new ComwarePort();
                continue;
            }
            if (line.StartsWith("interface ", StringComparison.OrdinalIgnoreCase)) { current = null; continue; }
            if (current is null) continue;
            var access = Regex.Match(line, @"^port access vlan\s+(\d+)$", RegexOptions.IgnoreCase);
            if (access.Success) { current.Pvid = int.Parse(access.Groups[1].Value); current.IsTrunk = false; continue; }
            if (line.Equals("port link-type trunk", StringComparison.OrdinalIgnoreCase)) { current.IsTrunk = true; continue; }
            var permit = Regex.Match(line, @"^port trunk permit vlan\s+(.+)$", RegexOptions.IgnoreCase);
            if (permit.Success)
            {
                var ids = permit.Groups[1].Value;
                current.PermitAll = ids.Equals("all", StringComparison.OrdinalIgnoreCase);
                current.Permit = current.PermitAll ? null : ExpandIds(ids).ToHashSet();
                continue;
            }
            var pvid = Regex.Match(line, @"^port trunk pvid vlan\s+(\d+)$", RegexOptions.IgnoreCase);
            if (pvid.Success) { current.Pvid = int.Parse(pvid.Groups[1].Value); continue; }
            var group = Regex.Match(line, @"^port link-aggregation group\s+(\d+)$", RegexOptions.IgnoreCase);
            if (group.Success) { current.Aggregation = "BAGG" + group.Groups[1].Value; continue; }
            if (line.StartsWith("description ", StringComparison.OrdinalIgnoreCase)) current.Description = Unquote(line[12..]);
            if (line.Equals("shutdown", StringComparison.OrdinalIgnoreCase)) current.Disabled = true;
            if (line.Equals("undo shutdown", StringComparison.OrdinalIgnoreCase)) current.Disabled = false;
        }
        config.Vlans.TryAdd(1, "VLAN 1");
        foreach (var source in physical.Values)
        {
            config.Vlans.TryAdd(source.Pvid, $"VLAN {source.Pvid}");
            if (source.Permit is not null) foreach (var id in source.Permit) config.Vlans.TryAdd(id, $"VLAN {id}");
        }
        int max = physical.Keys.DefaultIfEmpty(0).Max();
        if (max == 0) throw new FormatException("找不到 Comware 實體埠。");
        for (int id = 1; id <= max; id++)
        {
            var source = physical.GetValueOrDefault(id) ?? new ComwarePort();
            var policy = source.Aggregation is not null && aggregates.TryGetValue(source.Aggregation, out var agg) ? agg : source;
            bool isTrunk = source.IsTrunk || policy.IsTrunk;
            int pvid = source.Pvid != 1 ? source.Pvid : policy.Pvid;
            var port = new PortInfo { Number = id, Pvid = pvid, Trunk = source.Aggregation, TrunkMode = source.Aggregation is null ? null : "LACP", LinkType = isTrunk ? "trunk" : null, Description = source.Description, Disabled = source.Disabled };
            port.UntaggedVlans.Add(pvid);
            if (isTrunk)
            {
                IEnumerable<int> allowed = source.PermitAll || policy.PermitAll ? config.Vlans.Keys : source.Permit ?? policy.Permit ?? new HashSet<int> { 1 };
                foreach (var vlanId in allowed.Where(x => x != pvid)) port.TaggedVlans.Add(vlanId);
            }
            config.Ports.Add(port);
        }
        config.Model = physical.Keys.Max() > 24 ? "HPE Comware 交換器" : "HPE Comware";
        return config;
    }

    public static SwitchConfig ParseOfficeConnect(string text)
    {
        var config = new SwitchConfig { Format = "HPE OfficeConnect" };
        var ports = new Dictionary<int, PortInfo>();
        int? current = null;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('!'))
            {
                var product = Regex.Match(line, "^!System Description \"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (product.Success) config.Model = product.Groups[1].Value.Split(',')[0];
                continue;
            }
            var sysname = Regex.Match(line, "^snmp-server sysname\\s+\"?([^\"]+)\"?$", RegexOptions.IgnoreCase);
            if (sysname.Success) { config.Hostname = sysname.Groups[1].Value; continue; }
            var iface = Regex.Match(line, @"^interface\s+(\d+)$", RegexOptions.IgnoreCase);
            if (iface.Success)
            {
                current = int.Parse(iface.Groups[1].Value);
                if (!ports.ContainsKey(current.Value)) { var port = new PortInfo { Number = current.Value, Pvid = 1 }; port.UntaggedVlans.Add(1); ports[current.Value] = port; }
                continue;
            }
            if (line.Equals("exit", StringComparison.OrdinalIgnoreCase)) { current = null; continue; }
            var declaration = Regex.Match(line, @"^vlan\s+([\d,\-]+)$", RegexOptions.IgnoreCase);
            if (declaration.Success && current is null)
            {
                foreach (var id in ExpandIds(declaration.Groups[1].Value)) config.Vlans.TryAdd(id, $"VLAN {id}");
                continue;
            }
            if (current is not int number) continue;
            var target = ports[number];
            var pvid = Regex.Match(line, @"^vlan pvid\s+(\d+)$", RegexOptions.IgnoreCase);
            if (pvid.Success) { target.Pvid = int.Parse(pvid.Groups[1].Value); target.UntaggedVlans.Add(target.Pvid.Value); continue; }
            var member = Regex.Match(line, @"^vlan participation\s+(include|exclude)\s+(.+)$", RegexOptions.IgnoreCase);
            if (member.Success)
            {
                foreach (var id in ExpandIds(member.Groups[2].Value))
                {
                    if (member.Groups[1].Value.Equals("exclude", StringComparison.OrdinalIgnoreCase)) { target.UntaggedVlans.Remove(id); target.TaggedVlans.Remove(id); }
                    else if (!target.TaggedVlans.Contains(id)) target.UntaggedVlans.Add(id);
                }
                continue;
            }
            var tagging = Regex.Match(line, @"^vlan tagging\s+(.+)$", RegexOptions.IgnoreCase);
            if (tagging.Success)
                foreach (var id in ExpandIds(tagging.Groups[1].Value)) { target.UntaggedVlans.Remove(id); target.TaggedVlans.Add(id); }
            if (line.StartsWith("description ", StringComparison.OrdinalIgnoreCase)) target.Description = Unquote(line[12..]);
            if (line.Equals("shutdown", StringComparison.OrdinalIgnoreCase)) target.Disabled = true;
        }
        int max = ports.Keys.DefaultIfEmpty(0).Max();
        if (max == 0) throw new FormatException("找不到 OfficeConnect 實體埠。");
        config.Vlans.TryAdd(1, "VLAN 1");
        for (int id = 1; id <= max; id++)
        {
            if (!ports.TryGetValue(id, out var port)) { port = new PortInfo { Number = id, Pvid = 1 }; port.UntaggedVlans.Add(1); }
            config.Ports.Add(port);
            foreach (var vlanId in port.UntaggedVlans.Concat(port.TaggedVlans)) config.Vlans.TryAdd(vlanId, $"VLAN {vlanId}");
        }
        return config;
    }

    private sealed class ZyxelVlan
    {
        public string Name = "";
        public HashSet<int> Fixed = [];
        public HashSet<int> Untagged = [];
    }

    public static SwitchConfig ParseZyxel(string text)
    {
        var config = new SwitchConfig { Format = "Zyxel XGS" };
        var vlans = new Dictionary<int, ZyxelVlan>();
        var pvids = new Dictionary<int, int>();
        var trunking = new HashSet<int>();
        int? vlan = null, port = null;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("; Product Name =", StringComparison.OrdinalIgnoreCase)) { config.Model = line[16..].Trim(); continue; }
            if (line.StartsWith(';')) continue;
            var vlanMatch = Regex.Match(line, @"^vlan\s+(\d+)$", RegexOptions.IgnoreCase);
            if (vlanMatch.Success) { vlan = int.Parse(vlanMatch.Groups[1].Value); vlans[vlan.Value] = new(); port = null; continue; }
            var portMatch = Regex.Match(line, @"^interface port-channel\s+(\d+)$", RegexOptions.IgnoreCase);
            if (portMatch.Success) { port = int.Parse(portMatch.Groups[1].Value); vlan = null; continue; }
            if (line.StartsWith("interface ", StringComparison.OrdinalIgnoreCase)) { vlan = null; port = null; continue; }
            if (line.Equals("exit", StringComparison.OrdinalIgnoreCase)) { vlan = null; port = null; continue; }
            if (vlan is int vlanId)
            {
                var data = vlans[vlanId];
                if (line.StartsWith("name ", StringComparison.OrdinalIgnoreCase)) data.Name = Unquote(line[5..]);
                else if (line.StartsWith("fixed ", StringComparison.OrdinalIgnoreCase)) data.Fixed = ExpandIds(Unquote(line[6..])).ToHashSet();
                else if (line.StartsWith("untagged ", StringComparison.OrdinalIgnoreCase)) data.Untagged = ExpandIds(Unquote(line[9..])).ToHashSet();
            }
            if (port is int number)
            {
                var pvidMatch = Regex.Match(line, @"^pvid\s+(\d+)$", RegexOptions.IgnoreCase);
                if (pvidMatch.Success) pvids[number] = int.Parse(pvidMatch.Groups[1].Value);
                if (line.Equals("vlan-trunking", StringComparison.OrdinalIgnoreCase)) trunking.Add(number);
            }
        }
        int max = Math.Max(pvids.Keys.DefaultIfEmpty(0).Max(), vlans.Values.SelectMany(x => x.Fixed.Concat(x.Untagged)).DefaultIfEmpty(0).Max());
        if (max == 0) throw new FormatException("找不到 Zyxel 實體埠。");
        foreach (var (id, data) in vlans) config.Vlans[id] = data.Name.Length == 0 ? $"VLAN {id}" : data.Name;
        for (int id = 1; id <= max; id++)
        {
            var result = new PortInfo { Number = id, Pvid = pvids.GetValueOrDefault(id, 1), LinkType = trunking.Contains(id) ? "trunk" : null };
            foreach (var (vlanId, data) in vlans)
            {
                if (data.Untagged.Contains(id)) result.UntaggedVlans.Add(vlanId);
                else if (data.Fixed.Contains(id)) result.TaggedVlans.Add(vlanId);
            }
            config.Ports.Add(result);
        }
        return config;
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n');
    private static string Unquote(string text) => text.Trim().Trim('"', '\'').Trim();
    private static IEnumerable<int> ExpandIds(string source)
    {
        foreach (var token in Regex.Split(source.Replace(" to ", "-", StringComparison.OrdinalIgnoreCase), @"[\s,]+"))
        {
            var match = Regex.Match(token, @"^(\d+)(?:-(\d+))?$");
            if (!match.Success) continue;
            int start = int.Parse(match.Groups[1].Value), end = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : start;
            if (start < 1 || end < start || end > 4094) continue;
            for (int id = start; id <= end; id++) yield return id;
        }
    }
}
