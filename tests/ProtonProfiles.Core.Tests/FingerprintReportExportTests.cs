using System.Text.Json;
using ProtonProfiles.Core.Diagnostics;

namespace ProtonProfiles.Core.Tests;

public class FingerprintReportExportTests
{
    private const string Report = """
        {"sections":{"Сеть":{"IP (ipinfo.io)":"198.51.100.1","IPv4 (ipify)":"198.51.100.1","IPv6 (ipify)":"2001:db8::1","Хост":"customer.example.test","Часовой пояс IP":"Europe/Berlin"},
        "Заголовки, которые видит сервер (httpbin.org)":{"x-forwarded-for":"198.51.100.1","X-Real-IP":"198.51.100.1","User-Agent":"native"}},
        "networkObservations":{"ipv4":{"status":"AddressObserved","address":"198.51.100.1"}},"fingerprintId":"synthetic-id","verification":{"proxyRoutes":"NotPerformed"}}
        """;

    [Fact]
    public void Default_export_hides_known_network_addresses_and_preserves_evidence_and_environment_id()
    {
        var safe = FingerprintReportExport.Prepare(Report);
        Assert.DoesNotContain("198.51.100.1", safe); Assert.DoesNotContain("2001:db8::1", safe); Assert.DoesNotContain("customer.example.test", safe);
        Assert.Contains("Europe/Berlin", safe); Assert.Contains("synthetic-id", safe); Assert.Contains("AddressObserved", safe); Assert.Contains("NotPerformed", safe);
        using var doc = JsonDocument.Parse(safe);
        Assert.True(doc.RootElement.GetProperty("networkAddressesHidden").GetBoolean());
        Assert.Equal("native", doc.RootElement.GetProperty("sections").GetProperty("Заголовки, которые видит сервер (httpbin.org)").GetProperty("User-Agent").GetString());
        Assert.Contains("198.51.100.1", Report); // original local report is never changed
    }

    [Fact]
    public void Explicit_raw_export_keeps_addresses_and_partial_error_reports_are_also_scrubbed()
    {
        var raw = FingerprintReportExport.Prepare(Report, hideNetworkAddresses: false);
        Assert.Contains("198.51.100.1", raw);
        Assert.DoesNotContain("198.51.100.1", FingerprintReportExport.Prepare("{\"error\":\"synthetic\",\"partial\":" + Report + "}"));
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Invalid_reports_do_not_fall_back_to_unmasked_text(string report) => Assert.ThrowsAny<JsonException>(() => FingerprintReportExport.Prepare(report));
}
