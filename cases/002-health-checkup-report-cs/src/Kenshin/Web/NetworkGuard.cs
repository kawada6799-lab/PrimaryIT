using System.Net.NetworkInformation;

namespace Kenshin.Web;

/// <summary>「既定ゲートウェイを持つ稼働中のアダプタ」があれば、ネットワーク（インターネットへの経路）につながっているとみなして止める。通信はしない。</summary>
public sealed class NetworkGuard
{
    readonly bool _enabled;
    readonly HashSet<string> _exclude;
    public NetworkGuard(bool enabled, IEnumerable<string>? excludeAdapters = null)
    {
        _enabled = enabled;
        _exclude = new HashSet<string>(excludeAdapters ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }
    public bool Enabled => _enabled;

    public sealed record Status(bool Blocked, string Reason, DateTime CheckedAt);

    public Status Check()
    {
        if (!_enabled) return new(false, "起動チェックは無効（開発用）", DateTime.Now);
        var offenders = Offenders();
        return offenders.Count == 0
            ? new(false, "", DateTime.Now)
            : new(true, "ネットワークにつながっています（" + string.Join("、", offenders) + "）。LAN ケーブルを抜くか、ゲートウェイの設定を消してから続けてください。", DateTime.Now);
    }

    public List<string> Offenders()
    {
        var list = new List<string>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { return list; }
        foreach (var n in nics)
        {
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (_exclude.Contains(n.Name)) continue;
            IPInterfaceProperties props;
            try { props = n.GetIPProperties(); } catch { continue; }
            var hasGateway = props.GatewayAddresses.Any(g => g.Address != null && !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any) && g.Address.ToString() != "0.0.0.0");
            if (hasGateway) list.Add(n.Name);
        }
        return list;
    }
}
