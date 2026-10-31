using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace SwiftGet.Engine.Turbo;

/// <summary>
/// Detects physical network adapters, link speeds (e.g. 100 Mbps vs 1 GbE vs 10 GbE), and gateway signatures.
/// </summary>
public static class NetworkAdapterDetector
{
    public static IReadOnlyList<NetworkAdapterInfo> GetActiveAdapters()
    {
        var list = new List<NetworkAdapterInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                string? gatewayMac = null;
                try
                {
                    var ipProps = ni.GetIPProperties();
                    var gw = ipProps.GatewayAddresses.FirstOrDefault();
                    if (gw != null)
                    {
                        gatewayMac = gw.Address.ToString();
                    }
                }
                catch { }

                list.Add(new NetworkAdapterInfo(
                    Id: ni.Id,
                    Name: ni.Name,
                    Description: ni.Description,
                    InterfaceType: ni.NetworkInterfaceType.ToString(),
                    SpeedBitsPerSec: Math.Max(0, ni.Speed),
                    GatewayMacAddress: gatewayMac));
            }
        }
        catch
        {
            // Fallback gracefully on restricted environments
        }

        return list;
    }

    public static long GetPrimaryAdapterSpeedBytesPerSec()
    {
        var adapters = GetActiveAdapters();
        if (adapters.Count == 0) return 0;

        // Choose adapter with fastest link speed
        var maxSpeedBits = adapters.Max(a => a.SpeedBitsPerSec);
        return maxSpeedBits / 8;
    }
}
