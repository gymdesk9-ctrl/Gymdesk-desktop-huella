using libzkfpcsharp;

namespace GymDesk.Fingerprint.Services;

/// <summary>
/// Servicio para captura de huellas digitales con ZKTeco ZK9500.
/// Usa libzkfpcsharp.dll del SDK oficial de ZKTeco.
/// </summary>
public class ZKFingerprintService : IDisposable
{
    private readonly ILogger<ZKFingerprintService> _logger;
    private readonly object _lock = new();

    private bool _sdkInitialized;
    private IntPtr _deviceHandle = IntPtr.Zero;
    private IntPtr _dbHandle = IntPtr.Zero;

    private int _imageWidth = 0;
    private int _imageHeight = 0;
    private byte[]? _imageBuffer;

    public bool IsReady => _sdkInitialized && _deviceHandle != IntPtr.Zero;
    public int ImageWidth => _imageWidth;
    public int ImageHeight => _imageHeight;

    public ZKFingerprintService(ILogger<ZKFingerprintService> logger)
    {
        _logger = logger;
    }

    #region Inicialización

    public bool Initialize()
    {
        lock (_lock)
        {
            if (_sdkInitialized) return true;

            try
            {
                _logger.LogInformation("🔧 Inicializando ZKTeco SDK...");

                int ret = zkfp2.Init();
                if (ret != zkfperrdef.ZKFP_ERR_OK)
                {
                    _logger.LogError("❌ Error al inicializar SDK: código {Code}", ret);
                    return false;
                }

                _sdkInitialized = true;

                int deviceCount = zkfp2.GetDeviceCount();
                _logger.LogInformation("✅ SDK inicializado. Dispositivos: {Count}", deviceCount);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Excepción al inicializar SDK");
                return false;
            }
        }
    }

    public bool OpenDevice(int index = 0)
    {
        lock (_lock)
        {
            if (_deviceHandle != IntPtr.Zero) return true;

            if (!_sdkInitialized && !Initialize())
                return false;

            try
            {
                int deviceCount = zkfp2.GetDeviceCount();
                _logger.LogInformation("📱 Dispositivos disponibles: {Count}", deviceCount);

                if (deviceCount <= 0)
                {
                    _logger.LogWarning("⚠️ No hay dispositivos ZKTeco conectados");
                    return false;
                }

                _deviceHandle = zkfp2.OpenDevice(index);
                if (_deviceHandle == IntPtr.Zero)
                {
                    _logger.LogError("❌ No se pudo abrir el dispositivo");
                    return false;
                }

                _logger.LogInformation("✅ Dispositivo ZK9500 abierto");

                // Obtener dimensiones de imagen
                byte[] paramValue = new byte[4];
                int size = 4;

                zkfp2.GetParameters(_deviceHandle, 1, paramValue, ref size);
                zkfp2.ByteArray2Int(paramValue, ref _imageWidth);

                size = 4;
                zkfp2.GetParameters(_deviceHandle, 2, paramValue, ref size);
                zkfp2.ByteArray2Int(paramValue, ref _imageHeight);

                _imageBuffer = new byte[_imageWidth * _imageHeight];
                _logger.LogInformation("📐 Imagen: {W}x{H}", _imageWidth, _imageHeight);

                // Inicializar DB para matching
                _dbHandle = zkfp2.DBInit();
                if (_dbHandle != IntPtr.Zero)
                {
                    _logger.LogInformation("✅ Base de datos inicializada");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error al abrir dispositivo");
                return false;
            }
        }
    }

    public void CloseDevice()
    {
        lock (_lock)
        {
            if (_dbHandle != IntPtr.Zero)
            {
                zkfp2.DBFree(_dbHandle);
                _dbHandle = IntPtr.Zero;
            }

            if (_deviceHandle != IntPtr.Zero)
            {
                zkfp2.CloseDevice(_deviceHandle);
                _deviceHandle = IntPtr.Zero;
                _logger.LogInformation("Dispositivo cerrado");
            }
        }
    }

    #endregion

    #region Captura

    public async Task<CaptureResult> CaptureAsync(int timeoutMs = 10000)
    {
        if (!IsReady && !OpenDevice())
        {
            return CaptureResult.Failure("Dispositivo no disponible");
        }

        return await Task.Run(() =>
        {
            try
            {
                byte[] template = new byte[2048];
                int templateSize = 2048;

                _logger.LogInformation("👆 Esperando huella... (timeout: {T}ms)", timeoutMs);

                DateTime start = DateTime.Now;

                while ((DateTime.Now - start).TotalMilliseconds < timeoutMs)
                {
                    int ret = zkfp2.AcquireFingerprint(_deviceHandle, _imageBuffer, template, ref templateSize);

                    if (ret == zkfp.ZKFP_ERR_OK)
                    {
                        _logger.LogInformation("✅ Huella capturada ({Size} bytes)", templateSize);

                        byte[] trimmedTemplate = new byte[templateSize];
                        Array.Copy(template, trimmedTemplate, templateSize);

                        return new CaptureResult
                        {
                            Success = true,
                            Template = trimmedTemplate,
                            TemplateBase64 = zkfp2.BlobToBase64(template, templateSize),
                            ImageWidth = _imageWidth,
                            ImageHeight = _imageHeight
                        };
                    }

                    Thread.Sleep(100);
                }

                _logger.LogWarning("⏱️ Timeout");
                return CaptureResult.Failure("Timeout esperando huella");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en captura");
                return CaptureResult.Failure(ex.Message);
            }
        });
    }

    #endregion

    #region Enroll

    public async Task<EnrollResult> EnrollAsync(int captureTimeoutMs = 15000)
    {
        if (!IsReady && !OpenDevice())
        {
            return EnrollResult.Failure("Dispositivo no disponible");
        }

        const int requiredCaptures = 3;
        byte[][] templates = new byte[requiredCaptures][];

        _logger.LogInformation("🔐 Registro: se requieren {N} capturas", requiredCaptures);

        for (int i = 0; i < requiredCaptures; i++)
        {
            _logger.LogInformation("  Captura {C}/{T}...", i + 1, requiredCaptures);

            var capture = await CaptureAsync(captureTimeoutMs);

            if (!capture.Success || capture.Template == null)
            {
                return EnrollResult.Failure($"Captura {i + 1} fallida: {capture.ErrorMessage}");
            }

            // Verificar mismo dedo
            if (i > 0)
            {
                int score = zkfp2.DBMatch(_dbHandle, capture.Template, templates[i - 1]);
                if (score <= 0)
                {
                    return EnrollResult.Failure("Las capturas no coinciden. Use el mismo dedo.");
                }
            }

            templates[i] = capture.Template;
            _logger.LogInformation("  ✅ Captura {C}/{T} OK", i + 1, requiredCaptures);

            if (i < requiredCaptures - 1)
            {
                await Task.Delay(500);
            }
        }

        // Fusionar templates
        try
        {
            byte[] regTemplate = new byte[2048];
            int regSize = 2048;

            int ret = zkfp2.DBMerge(_dbHandle, templates[0], templates[1], templates[2], regTemplate, ref regSize);

            if (ret == zkfp.ZKFP_ERR_OK)
            {
                byte[] finalTemplate = new byte[regSize];
                Array.Copy(regTemplate, finalTemplate, regSize);

                _logger.LogInformation("✅ Registro completado ({Size} bytes)", regSize);

                return new EnrollResult
                {
                    Success = true,
                    Template = finalTemplate,
                    TemplateBase64 = zkfp2.BlobToBase64(regTemplate, regSize)
                };
            }
            else
            {
                return EnrollResult.Failure($"Error al fusionar templates: {ret}");
            }
        }
        catch (Exception ex)
        {
            return EnrollResult.Failure(ex.Message);
        }
    }

    #endregion

    #region Verificación

    public async Task<VerifyResult> VerifyAsync(string storedTemplateBase64, int timeoutMs = 10000)
    {
        if (!IsReady && !OpenDevice())
        {
            return VerifyResult.Failure("Dispositivo no disponible");
        }

        byte[] storedTemplate = zkfp2.Base64ToBlob(storedTemplateBase64);
        if (storedTemplate == null || storedTemplate.Length == 0)
        {
            return VerifyResult.Failure("Template inválido");
        }

        var capture = await CaptureAsync(timeoutMs);
        if (!capture.Success || capture.Template == null)
        {
            return VerifyResult.Failure(capture.ErrorMessage ?? "Error en captura");
        }

        int score = zkfp2.DBMatch(_dbHandle, capture.Template, storedTemplate);
        bool isMatch = score > 0;

        _logger.LogInformation(isMatch ? "✅ Verificada (score: {S})" : "❌ No coincide (score: {S})", score);

        return new VerifyResult
        {
            Success = true,
            IsMatch = isMatch,
            Score = score,
            CapturedTemplate = capture.TemplateBase64
        };
    }

    public MatchResult MatchTemplates(string template1Base64, string template2Base64)
    {
        if (!IsReady && !OpenDevice())
        {
            return new MatchResult { Success = false, ErrorMessage = "Dispositivo no disponible" };
        }

        var t1 = zkfp2.Base64ToBlob(template1Base64);
        var t2 = zkfp2.Base64ToBlob(template2Base64);

        if (t1 == null || t2 == null)
        {
            return new MatchResult { Success = false, ErrorMessage = "Template(s) inválido(s)" };
        }

        int score = zkfp2.DBMatch(_dbHandle, t1, t2);

        return new MatchResult
        {
            Success = true,
            IsMatch = score > 0,
            Score = score
        };
    }

    #endregion

    #region Estado

    /// <summary>
    /// Reconecta el dispositivo (útil cuando se desconecta/reconecta USB)
    /// </summary>
    public bool Reconnect()
    {
        lock (_lock)
        {
            _logger.LogInformation("🔄 Reconectando dispositivo ZKTeco...");
            
            // Cerrar dispositivo actual si está abierto
            CloseDevice();
            
            // Re-inicializar SDK
            if (_sdkInitialized)
            {
                try { zkfp2.Terminate(); } catch { }
                _sdkInitialized = false;
            }
            
            // Volver a inicializar
            if (!Initialize())
            {
                _logger.LogWarning("⚠️ No se pudo reinicializar SDK");
                return false;
            }
            
            // Intentar abrir dispositivo
            return OpenDevice();
        }
    }

    public DeviceStatus GetStatus()
    {
        int deviceCount = 0;
        if (_sdkInitialized)
        {
            try { deviceCount = zkfp2.GetDeviceCount(); } catch { }
        }

        return new DeviceStatus
        {
            SdkInitialized = _sdkInitialized,
            DeviceConnected = _deviceHandle != IntPtr.Zero,
            DeviceCount = deviceCount,
            ImageWidth = _imageWidth,
            ImageHeight = _imageHeight,
            IsReady = IsReady
        };
    }

    #endregion

    public void Dispose()
    {
        CloseDevice();

        if (_sdkInitialized)
        {
            zkfp2.Terminate();
            _sdkInitialized = false;
        }

        GC.SuppressFinalize(this);
    }
}

#region DTOs

public class CaptureResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public byte[]? Template { get; set; }
    public string? TemplateBase64 { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    public static CaptureResult Failure(string msg) => new() { Success = false, ErrorMessage = msg };
}

public class EnrollResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public byte[]? Template { get; set; }
    public string? TemplateBase64 { get; set; }

    public static EnrollResult Failure(string msg) => new() { Success = false, ErrorMessage = msg };
}

public class VerifyResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsMatch { get; set; }
    public int Score { get; set; }
    public string? CapturedTemplate { get; set; }

    public static VerifyResult Failure(string msg) => new() { Success = false, ErrorMessage = msg };
}

public class MatchResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsMatch { get; set; }
    public int Score { get; set; }
}

public class DeviceStatus
{
    public bool SdkInitialized { get; set; }
    public bool DeviceConnected { get; set; }
    public int DeviceCount { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public bool IsReady { get; set; }
}

#endregion
