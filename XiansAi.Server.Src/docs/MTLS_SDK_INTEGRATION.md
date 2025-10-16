# mTLS SDK Integration

This document describes the implementation of mTLS (Mutual TLS) integration between the XiansAi SDK and Temporal server, enabling secure communication across all platform components.

## Overview

The XiansAi platform now supports end-to-end mTLS communication:
- **Server ↔ Temporal**: Already implemented using centralized PFX certificates
- **SDK ↔ Temporal**: Newly implemented using server-provided certificates
- **SDK ↔ Server**: Uses client certificates for API authentication

## Architecture

```
┌─────────────┐    mTLS     ┌─────────────┐    mTLS     ┌─────────────┐
│    SDK      │◄──────────► │   Server    │◄──────────► │  Temporal   │
│             │             │             │             │             │
│ - Client    │             │ - API Key   │             │ - Server    │
│   Cert      │             │   Provider  │             │   Cert      │
│ - CA Cert   │             │ - Settings  │             │ - CA Cert   │
│ - Domain    │             │   Endpoint  │             │             │
└─────────────┘             └─────────────┘             └─────────────┘
```

## Changes Made

### 1. Server-Side Changes

#### CertificateService.cs
- **Added new properties** to `FlowServerSettings`:
  - `FlowServerRootCACertBase64`: CA certificate for server validation
  - `FlowServerDomain`: TLS domain for SNI (Server Name Indication)

- **Added new methods**:
  - `GetFlowServerRootCACertBase64()`: Retrieves CA certificate from config
  - `GetFlowServerDomain()`: Retrieves TLS domain from config

#### TemporalClientService.cs
- **Simplified TLS configuration** to use only centralized PFX approach
- **Removed direct certificate fallback** that was causing certificate conflicts
- **Server now exclusively uses** `Certificates__AppServerPfxBase64` for its own Temporal connection

### 2. SDK-Side Changes

#### SettingsService.cs
- **Added new properties** to `ServerSettings`:
  - `FlowServerRootCACertBase64`: CA certificate for server validation
  - `FlowServerDomain`: TLS domain for SNI

- **Enhanced debug logging** to show mTLS parameter availability

#### TemporalClientService.cs
- **Updated `getTlsConfig()` method** to use new mTLS parameters:
  - `TlsOptions.ServerRootCACert` from `FlowServerRootCACertBase64`
  - `TlsOptions.Domain` from `FlowServerDomain`

- **Added comprehensive debug logging** for TLS configuration analysis

## Configuration

### Server Environment Variables

The server requires these environment variables to provide mTLS configuration to the SDK:

```bash
# Temporal connection details
Temporal__FlowServerUrl=temporal:7233
Temporal__FlowServerUrlExternal=localhost:7233
Temporal__FlowServerNamespace=xiansai

# SDK mTLS parameters (passed to SDK)
Temporal__ServerName=temporal
Temporal__CertificateBase64=<base64-encoded-client-cert>
Temporal__PrivateKeyBase64=<base64-encoded-private-key>

# Server's own mTLS configuration (centralized)
Certificates__AppServerPfxBase64=<base64-encoded-pfx>
Certificates__AppServerCertPassword=<pfx-password>
Certificates__ServerRootCACertBase64=<base64-encoded-ca-cert>
```

### SDK Environment Variables

The SDK requires these environment variables for server authentication:

```bash
APP_SERVER_URL=http://localhost:5001
APP_SERVER_API_KEY=<base64-encoded-client-certificate>
```

## API Endpoints

### Settings Endpoint
- **URL**: `GET /api/agent/settings/flowserver`
- **Authentication**: Requires client certificate (mTLS)
- **Response**: `FlowServerSettings` with mTLS parameters

```json
{
  "flowServerUrl": "localhost:7233",
  "flowServerNamespace": "xiansai",
  "flowServerCertBase64": "<client-cert>",
  "flowServerPrivateKeyBase64": "<private-key>",
  "flowServerRootCACertBase64": "<ca-cert>",
  "flowServerDomain": "temporal",
  "apiKey": "<api-key>",
  "providerName": "openai",
  "modelName": "gpt-4",
  "baseUrl": "https://api.openai.com"
}
```

## Certificate Flow

### 1. Certificate Generation
- Community Edition generates root CA and server certificates
- Server uses `CertificateGenerator` to create client certificates
- Client certificates are generated with `clientAuth` EKU only

### 2. Certificate Distribution
- **Server → Temporal**: Uses centralized PFX bundle
- **SDK → Server**: Uses `APP_SERVER_API_KEY` (client certificate)
- **SDK → Temporal**: Uses certificates provided by server settings endpoint

### 3. Certificate Validation
- All certificates are signed by the same root CA
- Server validates client certificates against root CA
- Temporal validates server certificates against root CA
- SDK validates Temporal server certificates against provided CA

## Security Considerations

### Certificate Isolation
- Server's own Temporal connection uses centralized PFX (proven approach)
- SDK's Temporal connection uses separate certificates (passed via API)
- No certificate sharing between server and SDK connections

### Key Usage
- Client certificates have `clientAuth` EKU only
- Server certificates have both `serverAuth` and `clientAuth` EKU
- Private keys are properly secured and not logged

### SNI Support
- TLS domain is properly configured for Server Name Indication
- Prevents certificate name mismatch errors

## Testing

### Local Development
1. Start Community Edition stack: `./start-all.sh`
2. Build and run test agent with local SDK reference
3. Check debug logs for mTLS parameter availability
4. Verify successful Temporal connection

### Debug Logging
The implementation includes comprehensive debug logging:
- Server response analysis
- TLS configuration details
- Certificate availability status
- Connection attempt results

## Troubleshooting

### Common Issues

#### "UnknownIssuer" Error
- **Cause**: CA certificate mismatch between server and Temporal
- **Solution**: Ensure `Certificates__ServerRootCACertBase64` matches Temporal's CA

#### "Connection failed: transport error"
- **Cause**: Missing or invalid mTLS parameters
- **Solution**: Verify all required environment variables are set

#### Certificate Validation Errors
- **Cause**: Certificate chain issues or wrong CA
- **Solution**: Check certificate generation and CA consistency

### Debug Steps
1. Check server logs for TLS configuration details
2. Verify environment variables are properly set
3. Test server → Temporal connection independently
4. Check SDK debug logs for parameter availability
5. Validate certificate chain and CA consistency

## Future Enhancements

### Planned Improvements
- Certificate rotation support
- Multiple CA support
- Certificate validation caching
- Enhanced error reporting

### Configuration Management
- Centralized certificate management
- Automated certificate renewal
- Environment-specific configurations

## References

- [Temporal .NET SDK TlsOptions](https://dotnet.temporal.io/api/Temporalio.Client.TlsOptions.html)
- [Community Edition mTLS Setup](../community-edition/scripts/MTLS_SETUP.md)
- [Temporal TLS Configuration](../community-edition/temporal/docs/TLS_CONFIGURATION.md)
