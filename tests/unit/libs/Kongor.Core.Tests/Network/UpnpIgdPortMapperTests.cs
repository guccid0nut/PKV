// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Core.Network;
using Xunit;

namespace Kongor.Core.Tests.Network;

public sealed class UpnpIgdPortMapperTests
{
    private const string SampleGatewayXml = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <URLBase>http://192.168.1.1:49000/</URLBase>
          <device>
            <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
            <deviceList>
              <device>
                <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
                <deviceList>
                  <device>
                    <deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
                    <serviceList>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                        <controlURL>/igdupnp/control/WANIPConn1</controlURL>
                      </service>
                    </serviceList>
                  </device>
                </deviceList>
              </device>
            </deviceList>
          </device>
        </root>
        """;

    private sealed class FakeUpnpTransport : IUpnpDiscoveryTransport
    {
        public IReadOnlyList<string> Locations { get; init; } = [];
        public string? DescriptorXml { get; init; }
        public Func<string, (bool, int, string)> SoapHandler { get; init; } = _ => (true, 200, "<ok/>");
        public List<(Uri ControlUri, string ServiceType, string Action, string Body)> SentActions { get; } = [];

        public Task<IReadOnlyList<string>> DiscoverLocationUrlsAsync(TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(Locations);

        public Task<string?> FetchDeviceDescriptionXmlAsync(Uri locationUri, CancellationToken cancellationToken)
            => Task.FromResult(DescriptorXml);

        public Task<(bool Succeeded, int StatusCode, string ResponseBody)> SendSoapActionAsync(
            Uri controlUri,
            string serviceType,
            string actionName,
            string soapBodyXml,
            CancellationToken cancellationToken)
        {
            SentActions.Add((controlUri, serviceType, actionName, soapBodyXml));
            return Task.FromResult(SoapHandler(actionName));
        }
    }

    [Fact]
    public async Task TryMapUdpPortAsync_ValidLanGateway_SendsAddPortMappingAndParsesExternalIp()
    {
        // protects: UPnP IGD UDP port-mapping SOAP contract & external IP extraction for DirectPlay hosting.
        // fails_when: SSDP location parsing, WANIPConnection controlURL resolution, or SOAP body XML breaks.
        // seam: UpnpIgdPortMapper.TryMapUdpPortAsync with injected IUpnpDiscoveryTransport.
        FakeUpnpTransport transport = new()
        {
            Locations = ["http://192.168.1.1:49000/igddesc.xml"],
            DescriptorXml = SampleGatewayXml,
            SoapHandler = action => action == "GetExternalIPAddress"
                ? (true, 200, "<Envelope><Body><GetExternalIPAddressResponse><NewExternalIPAddress>203.0.113.42</NewExternalIPAddress></GetExternalIPAddressResponse></Body></Envelope>")
                : (true, 200, "<Envelope><Body><AddPortMappingResponse/></Body></Envelope>")
        };

        UpnpPortMappingResult result = await UpnpIgdPortMapper.TryMapUdpPortAsync(
            port: 11550,
            internalClientIp: "192.168.1.50",
            description: "Project KONGOR DirectPlay",
            transport: transport);

        result.Succeeded.Should().BeTrue();
        result.ExternalPort.Should().Be(11550);
        result.GatewayAddress.Should().Be("192.168.1.1");
        result.ExternalIpAddress.Should().Be("203.0.113.42");
        result.ServiceType.Should().Be("urn:schemas-upnp-org:service:WANIPConnection:1");
        transport.SentActions.Should().HaveCount(2);
        transport.SentActions[0].Action.Should().Be("AddPortMapping");
        transport.SentActions[0].ControlUri.ToString().Should().Be("http://192.168.1.1:49000/igdupnp/control/WANIPConn1");
        transport.SentActions[0].Body.Should().Contain("<NewExternalPort>11550</NewExternalPort>");
        transport.SentActions[0].Body.Should().Contain("<NewInternalClient>192.168.1.50</NewInternalClient>");
    }

    [Fact]
    public async Task TryMapUdpPortAsync_RejectsPublicSsdpLocationHost_ToPreventSsrf()
    {
        // protects: LAN-only SSRF boundary on untrusted SSDP LOCATION headers.
        // fails_when: UpnpIgdPortMapper fetches a non-RFC1918 public HTTP descriptor URL.
        // seam: UpnpIgdPortMapper private-host validation prior to FetchDeviceDescriptionXmlAsync.
        FakeUpnpTransport transport = new()
        {
            Locations = ["http://198.51.100.7:80/evil.xml"],
            DescriptorXml = SampleGatewayXml
        };

        UpnpPortMappingResult result = await UpnpIgdPortMapper.TryMapUdpPortAsync(
            port: 11550,
            internalClientIp: "192.168.1.50",
            transport: transport);

        result.Succeeded.Should().BeFalse();
        transport.SentActions.Should().BeEmpty();
    }

    [Fact]
    public async Task TryDeleteUdpPortMappingAsync_ReleasesUdpPortLease()
    {
        // protects: UPnP UDP mapping teardown on host stop.
        // fails_when: DeletePortMapping SOAP envelope omits port/protocol or fails to target gateway controlURL.
        // seam: UpnpIgdPortMapper.TryDeleteUdpPortMappingAsync.
        FakeUpnpTransport transport = new()
        {
            Locations = ["http://192.168.1.1:49000/igddesc.xml"],
            DescriptorXml = SampleGatewayXml
        };

        bool deleted = await UpnpIgdPortMapper.TryDeleteUdpPortMappingAsync(11550, transport: transport);

        deleted.Should().BeTrue();
        transport.SentActions.Should().ContainSingle(a => a.Action == "DeletePortMapping" && a.Body.Contains("<NewExternalPort>11550</NewExternalPort>"));
    }
}
