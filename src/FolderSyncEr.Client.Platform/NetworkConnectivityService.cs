using System;
using System.Linq;
using System.Net.NetworkInformation;

namespace FolderSyncEr.Client.Platform;

public interface INetworkConnectivityService : IDisposable
{
    bool IsNetworkAvailable { get; }
    event EventHandler<bool>? ConnectivityChanged;
    event EventHandler? AddressOrRouteChanged;
    bool CheckNetworkAvailable();
}

/// <summary>
/// Monitors local OS network availability and route changes across Windows and Linux.
/// Handles multi-port systems (Wi-Fi + Ethernet, dual NICs) by checking for any operational
/// physical, wireless, or VPN network interface with assigned IP addresses.
/// </summary>
public class NetworkConnectivityService : INetworkConnectivityService
{
    private bool _isDisposed;
    private bool _lastReportedState = true;

    public bool IsNetworkAvailable => CheckNetworkAvailable();

    public event EventHandler<bool>? ConnectivityChanged;
    public event EventHandler? AddressOrRouteChanged;

    public NetworkConnectivityService()
    {
        try
        {
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _lastReportedState = CheckNetworkAvailable();
        }
        catch
        {
            // Some restricted sandbox or container environments may restrict network event hooks
            _lastReportedState = true;
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        var isAvailable = CheckNetworkAvailable();
        if (isAvailable != _lastReportedState)
        {
            _lastReportedState = isAvailable;
            ConnectivityChanged?.Invoke(this, isAvailable);
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        var isAvailable = CheckNetworkAvailable();
        _lastReportedState = isAvailable;
        AddressOrRouteChanged?.Invoke(this, EventArgs.Empty);
    }

    public virtual bool CheckNetworkAvailable()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            if (interfaces == null || interfaces.Length == 0)
                return false;

            // Check if any non-loopback network adapter is operational
            return interfaces.Any(IsUsableNetworkInterface);
        }
        catch
        {
            // Fail-safe: if querying the network interfaces fails, assume online to allow HTTP calls to probe
            return true;
        }
    }

    /// <summary>
    /// Determines whether a network adapter is usable for LAN/WAN communication.
    /// Excludes loopback adapters and down interfaces.
    /// </summary>
    public static bool IsUsableNetworkInterface(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up)
            return false;

        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            return false;

        var name = (nic.Name ?? string.Empty).ToLowerInvariant();
        var desc = (nic.Description ?? string.Empty).ToLowerInvariant();

        // Skip obvious loopback aliases
        if (name.StartsWith("lo") || desc.Contains("loopback"))
            return false;

        try
        {
            var ipProps = nic.GetIPProperties();
            var unicast = ipProps.UnicastAddresses;
            if (unicast == null || unicast.Count == 0)
                return false;

            // Has at least one valid IP assigned (IPv4 or IPv6)
            return unicast.Any(u => u.Address != null &&
                                    !System.Net.IPAddress.IsLoopback(u.Address));
        }
        catch
        {
            // If IP properties cannot be read, accept the adapter if it is marked Up
            return true;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        }
        catch
        {
            // Suppress cleanup errors
        }
    }
}

