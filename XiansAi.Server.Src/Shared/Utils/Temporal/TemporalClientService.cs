using Temporalio.Client;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Shared.Utils.Temporal;

public interface ITemporalClientService
{
    Task<ITemporalClient> GetClientAsync(string tenantId);
    ITemporalClient GetClient(string tenantId);

}

public class TemporalClientService : ITemporalClientService, IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ITemporalClient> _clients = new();
    private readonly ConcurrentDictionary<string, CloudService> _serviceClients = new();
    private readonly ILogger<TemporalClientService> _logger;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
    private volatile bool _disposed = false;
    private readonly object _disposeLock = new object();

    public TemporalClientService(
        ILogger<TemporalClientService> logger,
        IConfiguration configuration)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public ITemporalClient GetClient(string tenantId)
    {
        // For backward compatibility, use async version but block
        // Consider making all callers async to avoid this
        return GetClientAsync(tenantId).GetAwaiter().GetResult();
    }

    public async Task<ITemporalClient> GetClientAsync(string tenantId)
    {
        ThrowIfDisposed();
        
        if (_clients.TryGetValue(tenantId, out var existingClient))
        {
            return existingClient;
        }

        // Use semaphore to prevent concurrent creation of the same client
        await _connectionSemaphore.WaitAsync();
        try
        {
            // Double-check pattern
            if (_clients.TryGetValue(tenantId, out existingClient))
            {
                return existingClient;
            }

            var config = GetTemporalConfig(tenantId);
            
            var options = new TemporalClientConnectOptions(new(config.FlowServerUrl))
            {
                Namespace = config.FlowServerNamespace!,
            };
            
            // Configure TLS if certificates are available
            var tlsOptions = GetTlsOptions(config);
            if (tlsOptions != null)
            {
                options.Tls = tlsOptions;
            }
            
            _logger.LogInformation("Connecting to temporal server for tenant {TenantId}: {Url}, namespace: {Namespace}", 
                tenantId, config.FlowServerUrl, config.FlowServerNamespace);

            var client = await TemporalClient.ConnectAsync(options);
            _clients.TryAdd(tenantId, client);
            
            return client;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    private TemporalConfig GetTemporalConfig(string tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId is required");

        // First try to get tenant-specific temporal config
        var temporalConfig = _configuration.GetSection($"Tenants:{tenantId}:Temporal").Get<TemporalConfig>();

        if (temporalConfig == null)
        {
            // Fallback to the root temporal config
            temporalConfig = _configuration.GetSection("Temporal").Get<TemporalConfig>();
        }

        // If neither tenant-specific nor default config is found, throw an error
        if (temporalConfig == null)
        {
            throw new InvalidOperationException($"Temporal configuration for tenant {tenantId} not found");
        }

        // Validate required fields
        if (temporalConfig.FlowServerUrl == null)
            throw new InvalidOperationException($"FlowServerUrl is required for tenant {tenantId}");

        return temporalConfig;
    }

    /// <summary>
    /// Gets TLS options for Temporal connection.
    /// Uses centralized certificates: Certificates__AppServerPfxBase64, Certificates__ServerRootCACertBase64
    /// </summary>
    private TlsOptions? GetTlsOptions(TemporalConfig config)
    {
        // Use centralized certificates (preferred approach for server's own connection)
        var certSection = _configuration.GetSection("Certificates");
        var pfxBase64 = certSection["AppServerPfxBase64"];
        var pfxPassword = certSection["AppServerCertPassword"];
        var caBase64 = certSection["ServerRootCACertBase64"];

        if (!string.IsNullOrEmpty(pfxBase64) && !string.IsNullOrEmpty(caBase64))
        {
            _logger.LogInformation("Using centralized certificate configuration from Certificates section");
            try
            {
                var (clientCert, clientKey) = ExtractCertAndKeyFromPfx(pfxBase64, pfxPassword);
                return new TlsOptions()
                {
                    ClientCert = clientCert,
                    ClientPrivateKey = clientKey,
                    ServerRootCACert = Convert.FromBase64String(caBase64),
                    Domain = config.ServerName,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract certificates from centralized PFX bundle");
                throw;
            }
        }

        _logger.LogInformation("No TLS certificates configured, using plain connection");
        return null;
    }
    
    /// <summary>
    /// Extracts certificate and private key from a PFX (PKCS#12) bundle.
    /// </summary>
    private (byte[] certificate, byte[] privateKey) ExtractCertAndKeyFromPfx(string pfxBase64, string? password)
    {
        try
        {
            var pfxBytes = Convert.FromBase64String(pfxBase64);
            
            // Load the PFX certificate using the modern API
            using var cert = string.IsNullOrEmpty(password)
                ? X509CertificateLoader.LoadPkcs12(pfxBytes, null)
                : X509CertificateLoader.LoadPkcs12(pfxBytes, password);
            
            if (!cert.HasPrivateKey)
            {
                throw new InvalidOperationException("PFX certificate does not contain a private key");
            }
            
            // Export certificate (public key) as PEM
            var certPem = cert.ExportCertificatePem();
            var certBytes = System.Text.Encoding.UTF8.GetBytes(certPem);
            
            // Export private key as PEM
            var privateKey = cert.GetRSAPrivateKey() ?? cert.GetECDsaPrivateKey() as AsymmetricAlgorithm;
            if (privateKey == null)
            {
                throw new InvalidOperationException("Unable to extract private key from PFX certificate");
            }
            
            string keyPem;
            if (privateKey is RSA rsa)
            {
                keyPem = rsa.ExportRSAPrivateKeyPem();
            }
            else if (privateKey is ECDsa ecdsa)
            {
                keyPem = ecdsa.ExportECPrivateKeyPem();
            }
            else
            {
                throw new InvalidOperationException($"Unsupported private key type: {privateKey.GetType().Name}");
            }
            
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(keyPem);
            
            _logger.LogDebug("Successfully extracted certificate and private key from PFX");
            
            return (certBytes, keyBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract certificate and key from PFX");
            throw new InvalidOperationException("Failed to extract certificate and key from PFX bundle", ex);
        }
    }


    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TemporalClientService));
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();
        Dispose(false);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            lock (_disposeLock)
            {
                if (_disposed) return;
                
                _logger.LogInformation("Disposing Temporal client service synchronously");
                
                try
                {
                    // Use a timeout to prevent hanging during shutdown
                    var disposeTask = DisposeAsyncCore();
                    if (!disposeTask.AsTask().Wait(TimeSpan.FromSeconds(10)))
                    {
                        _logger.LogWarning("Temporal client service disposal timed out after 10 seconds");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during synchronous disposal of Temporal client service");
                }
                finally
                {
                    _disposed = true;
                    _connectionSemaphore?.Dispose();
                }
            }
        }
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (_disposed) return;
        
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _logger.LogInformation("Disposing Temporal client service asynchronously");
        
        var disposeTimeout = TimeSpan.FromSeconds(10);
        var cancellationTokenSource = new CancellationTokenSource(disposeTimeout);
        
        try
        {
            // Dispose all cached clients with timeout
            var disposeTasks = _clients.Values.Select(async client =>
            {
                try
                {
                    if (client is IAsyncDisposable asyncDisposableClient)
                    {
                        await asyncDisposableClient.DisposeAsync();
                    }
                    else if (client is IDisposable disposableClient)
                    {
                        disposableClient.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing individual Temporal client");
                }
            });

            // Wait for all disposals to complete with timeout
            await Task.WhenAll(disposeTasks).WaitAsync(cancellationTokenSource.Token);
            
            _clients.Clear();
            _serviceClients.Clear();
            
            _logger.LogInformation("Temporal client service disposed successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Temporal client service disposal timed out after {TimeoutSeconds} seconds", disposeTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during async disposal of Temporal client service");
        }
        finally
        {
            _connectionSemaphore?.Dispose();
        }
    }
}