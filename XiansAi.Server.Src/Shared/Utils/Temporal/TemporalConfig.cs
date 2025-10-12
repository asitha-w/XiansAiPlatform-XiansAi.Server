
namespace Shared.Utils.Temporal;

public class TemporalConfig
{
    public string? FlowServerUrl { get; set; }

    public string? FlowServerUrlExternal { get; set; }

    public string? FlowServerNamespace { get; set; }

    // Certificate configuration (base64 encoded)
    public string? CertificateBase64 { get; set; }
    public string? PrivateKeyBase64 { get; set; }
    public string? ServerRootCACertBase64 { get; set; }  // CA certificate for server validation
    
    // TLS server name for SNI (Server Name Indication)
    public string? ServerName { get; set; }
}