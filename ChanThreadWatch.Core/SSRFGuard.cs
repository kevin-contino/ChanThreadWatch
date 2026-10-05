using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace JDP {
    // A request was not sent because its host resolves to an address that service mode blocks
    public class BlockedAddressException : Exception {
        public BlockedAddressException(string host)
            : this(host, "it is a local or private address") { }

        public BlockedAddressException(string host, string reason)
            : base("requests to " + host + " are blocked: " + reason) { }
    }

    // Security item 9 (SSRF). Every connection of the HTTP transport is made here (it is the handler's
    // ConnectCallback), so every redirect and meta refresh hop is checked, against the address the
    // connection actually goes to. Off in desktop mode: the desktop app downloads from wherever its
    // user points it, LAN boards included. In service mode, loopback, private, link-local, CGNAT,
    // unique local, multicast, unspecified and reserved addresses are refused, unless the host or
    // address is in AllowedHosts.
    public static class SSRFGuard {
        private static readonly object _settingsSync = new object();
        private static volatile bool _serviceMode;
        private static volatile string[] _allowedHosts = new string[0];
        // Set by the first connection; the settings cannot change after it
        private static volatile bool _connected;

        // Set once at startup by a host that serves other users (no UI sets it yet). A change after the
        // first connection throws InvalidOperationException: a pooled connection made before it would
        // not be checked again. Service mode never connects through a proxy, whose address is all the
        // guard would see.
        public static bool ServiceMode {
            get { return _serviceMode; }
            set {
                lock (_settingsSync) {
                    if (value != _serviceMode) ThrowIfConnected();
                    _serviceMode = value;
                }
            }
        }

        // Host names or IP addresses (e.g. a board on the LAN) that service mode lets through. A copy
        // is kept and given out, so changing an array never changes the setting. Set once, like
        // ServiceMode.
        public static string[] AllowedHosts {
            get { return (string[])_allowedHosts.Clone(); }
            set {
                string[] hosts = value != null ? (string[])value.Clone() : new string[0];
                lock (_settingsSync) {
                    if (!hosts.SequenceEqual(_allowedHosts)) ThrowIfConnected();
                    _allowedHosts = hosts;
                }
            }
        }

        private static void ThrowIfConnected() {
            if (_connected) throw new InvalidOperationException("The SSRF guard settings cannot change once a connection has been made.");
        }

        // Test only: lets the next test set the settings again. Tests that change them use their own
        // hosts and ports, so no pooled connection carries over.
        internal static void AllowSettingsChangeForTesting() {
            _connected = false;
        }

        // Test only: lets loopback addresses through in service mode, so the loopback test servers can
        // stand in for a board. Never set by production code.
        internal static bool AllowLoopbackForTesting { get; set; }

        // Test only: stands in for the DNS lookup
        internal static Func<string, CancellationToken, Task<IPAddress[]>> ResolveHost { get; set; } = Dns.GetHostAddressesAsync;

        // Test only: sees the addresses just before the socket connects to them, and may throw instead
        internal static Action<IPAddress[]> BeforeConnect { get; set; } = addresses => { };

        // Resolves the host, checks every address it resolves to (one blocked address blocks the host,
        // so a DNS answer cannot mix in a private address), then connects to those same addresses
        internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken) {
            lock (_settingsSync) {
                _connected = true;
            }
            DnsEndPoint endPoint = context.DnsEndPoint;
            IPAddress literal;
            // The DNS lookup refuses an unspecified address (0.0.0.0) instead of returning it
            IPAddress[] addresses = IPAddress.TryParse(endPoint.Host, out literal) ? new[] { literal } :
                await ResolveHost(endPoint.Host, cancellationToken).ConfigureAwait(false);
            if (ServiceMode) {
                ThrowIfProxied(endPoint, context.InitialRequestMessage.RequestUri);
                ThrowIfBlocked(endPoint.Host, addresses);
            }
            BeforeConnect(addresses);
            Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try {
                await socket.ConnectAsync(addresses, endPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, true);
            }
            catch {
                socket.Dispose();
                throw;
            }
        }

        // Through a proxy the connection goes to the proxy, and the proxy resolves the target, so no
        // check here could stop a private target. Service mode refuses such a connection.
        internal static void ThrowIfProxied(DnsEndPoint endPoint, Uri requestUri) {
            if (!String.Equals(endPoint.Host.Trim('[', ']'), requestUri.IdnHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) || endPoint.Port != requestUri.Port) {
                throw new BlockedAddressException(requestUri.IdnHost, "it would be reached through a proxy");
            }
        }

        private static void ThrowIfBlocked(string host, IPAddress[] addresses) {
            if (IsAllowedHost(host)) return;
            foreach (IPAddress address in addresses) {
                if (!IsAllowedAddress(address)) throw new BlockedAddressException(host);
            }
        }

        private static bool IsAllowedAddress(IPAddress address) {
            return IsAllowedHost(address.ToString()) ||
                   (AllowLoopbackForTesting && IPAddress.IsLoopback(Unmap(address))) ||
                   !IsBlockedAddress(address);
        }

        private static bool IsAllowedHost(string host) {
            foreach (string allowed in _allowedHosts) {
                if (String.Equals(allowed, host, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Network address and prefix length
        private sealed class AddressRange {
            public readonly byte[] Network;
            public readonly int PrefixLength;

            public AddressRange(string network, int prefixLength) {
                Network = IPAddress.Parse(network).GetAddressBytes();
                PrefixLength = prefixLength;
            }

            public bool Contains(byte[] address) {
                if (address.Length != Network.Length) return false;
                for (int bit = 0; bit < PrefixLength; bit++) {
                    int mask = 0x80 >> (bit % 8);
                    if ((address[bit / 8] & mask) != (Network[bit / 8] & mask)) return false;
                }
                return true;
            }
        }

        private static readonly AddressRange[] _blockedRanges = {
            new AddressRange("0.0.0.0", 8),        // "this network", unspecified
            new AddressRange("10.0.0.0", 8),       // private
            new AddressRange("100.64.0.0", 10),    // CGNAT
            new AddressRange("127.0.0.0", 8),      // loopback
            new AddressRange("169.254.0.0", 16),   // link-local, cloud metadata (169.254.169.254)
            new AddressRange("172.16.0.0", 12),    // private
            new AddressRange("192.0.0.0", 24),     // IETF protocol assignments
            new AddressRange("192.168.0.0", 16),   // private
            new AddressRange("198.18.0.0", 15),    // benchmarking
            new AddressRange("224.0.0.0", 3),      // multicast, reserved, broadcast
            new AddressRange("::", 128),           // unspecified
            new AddressRange("::1", 128),          // loopback
            new AddressRange("::ffff:0:0:0", 96),  // IPv4-translated
            new AddressRange("64:ff9b:1::", 48),   // local-use IPv4/IPv6 translation
            new AddressRange("100::", 64),         // discard
            new AddressRange("2001::", 32),        // Teredo, which carries an IPv4 address
            new AddressRange("2002::", 16),        // 6to4, which carries an IPv4 address
            new AddressRange("fc00::", 7),         // unique local
            new AddressRange("fe80::", 10),        // link-local
            new AddressRange("fec0::", 10),        // site-local (deprecated)
            new AddressRange("ff00::", 8)          // multicast
        };

        // IPv6 forms that carry an IPv4 address in their last 4 bytes (IPv4-mapped is handled by IPAddress)
        private static readonly AddressRange _nat64 = new AddressRange("64:ff9b::", 96);
        private static readonly AddressRange _ipv4Compatible = new AddressRange("::", 96);
        private static readonly AddressRange _unspecifiedOrLoopback = new AddressRange("::", 127);

        // The IPv4 address an IPv6 address stands for (IPv4-mapped ::ffff:a.b.c.d, IPv4-compatible ::a.b.c.d
        // and NAT64 64:ff9b::a.b.c.d), or the address itself
        private static IPAddress Unmap(IPAddress address) {
            if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
            byte[] b = address.GetAddressBytes();
            if (!EmbedsIPv4(b)) return address;
            return new IPAddress(new[] { b[12], b[13], b[14], b[15] });
        }

        private static bool EmbedsIPv4(byte[] address) {
            return _nat64.Contains(address) || (_ipv4Compatible.Contains(address) && !_unspecifiedOrLoopback.Contains(address));
        }

        internal static bool IsBlockedAddress(IPAddress address) {
            byte[] bytes = Unmap(address).GetAddressBytes();
            foreach (AddressRange range in _blockedRanges) {
                if (range.Contains(bytes)) return true;
            }
            return false;
        }
    }
}
