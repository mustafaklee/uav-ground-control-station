using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace Gcs.Api.Http;

/// <summary>
/// The API behind a TLS-terminating reverse proxy (Nginx, Phase 11). The proxy opens the TCP connection, so without this
/// every request would look like it came from the proxy: one shared rate-limit bucket for all clients, the proxy's address
/// in the logs, and <c>http</c> as the scheme although the client used HTTPS.
/// </summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>Read <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>. Off by default: without a proxy anyone could send them.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Networks (CIDR, e.g. <c>172.30.0.0/24</c>) the proxy connects from. The headers are honoured only on connections from
    /// these addresses, so a client that reaches the API directly cannot pretend to be someone else.
    /// </summary>
    public string[] TrustedNetworks { get; set; } = [];
}

public static class ReverseProxyExtensions
{
    public static IServiceCollection AddGcsReverseProxySupport(this IServiceCollection services)
    {
        services.AddOptions<ReverseProxyOptions>()
            .BindConfiguration(ReverseProxyOptions.SectionName)
            .Validate(
                o => !o.Enabled || (o.TrustedNetworks.Length > 0 && o.TrustedNetworks.All(n => IPNetwork.TryParse(n, out _))),
                "ReverseProxy:TrustedNetworks must list at least one valid CIDR network when ReverseProxy:Enabled is true.")
            .ValidateOnStart();

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ReverseProxyOptions>>((forwarded, proxy) =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.ForwardLimit = 1; // exactly one proxy in front: take only the address it saw
            forwarded.KnownProxies.Clear(); // the default trusts loopback only; replace it with the configured networks
            forwarded.KnownIPNetworks.Clear();
            foreach (var network in proxy.Value.TrustedNetworks)
            {
                forwarded.KnownIPNetworks.Add(IPNetwork.Parse(network));
            }
        });

        return services;
    }

    /// <summary>Must run first, so logging, rate limiting and HSTS all see the client's address and scheme.</summary>
    public static IApplicationBuilder UseGcsReverseProxySupport(this IApplicationBuilder app)
    {
        var enabled = app.ApplicationServices.GetRequiredService<IOptions<ReverseProxyOptions>>().Value.Enabled;
        return enabled ? app.UseForwardedHeaders() : app;
    }
}
