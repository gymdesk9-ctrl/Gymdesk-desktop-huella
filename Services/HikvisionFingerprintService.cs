using System.Management;
using SourceAFIS;

namespace GymDesk.Fingerprint.Services;

/// <summary>
/// Lector de huella Hikvision DS-K1F820-F por USB, con FPModule_SDK.dll (la misma DLL que usa iVMS-4200).
///
/// El lector entrega la IMAGEN de la huella (256 x 288, 8 bits, 508 dpi). La plantilla y la comparación
/// las hace SourceAFIS en la PC, así el acceso por huella (1:N contra los socios) funciona igual que con
/// el ZK9500. Las plantillas se guardan con el prefijo "SAFIS1:" + base64 para distinguirlas de las del
/// ZKTeco (los dos formatos no son comparables entre sí).
///
/// El lector aparece en Windows como una unidad de CD-ROM (almacenamiento USB): es normal, no hace falta
/// driver ni "expulsar" nada.
/// </summary>
public class HikvisionFingerprintService : IDisposable
{
    public const string Model = "Hikvision DS-K1F820-F";
    public const string TemplatePrefix = "SAFIS1:";

    /// <summary>Puntuación SourceAFIS a partir de la cual dos huellas son la misma (40 ≈ 0,01 % de falsos positivos).</summary>
    public const double MatchThreshold = 40;

    private const double ImageDpi = 508;

    /// <summary>Sin dedo el sensor devuelve casi blanco (gris medio ≈ 252); con dedo baja claramente.</summary>
    private const int GrisMaximoConDedo = 235;

    private readonly ILogger<HikvisionFingerprintService> _logger;
    private readonly object _sync = new();

    private bool _dllDisponible = true;
    private bool _abierto;
    private string _sdkVersion = "";
    private string _serialNumber = "";
    private string _deviceName = "";
    private string _unidad = "";
    private int _imageWidth;
    private int _imageHeight;
    private Task<CaptureResult>? _captureEnCurso;

    // Un FingerprintMatcher es caro de crear: se guarda el de la última huella consultada (identificación 1:N)
    private static readonly object _matcherLock = new();
    private static string? _matcherKey;
    private static FingerprintMatcher? _matcher;

    public bool IsReady => _abierto;
    public string SerialNumber => _serialNumber;
    public string DeviceName => _deviceName;

    public HikvisionFingerprintService(ILogger<HikvisionFingerprintService> logger)
    {
        _logger = logger;
    }

    #region Conexión

    public bool OpenDevice()
    {
        lock (_sync)
        {
            if (_abierto) return true;
            if (!_dllDisponible) return false;
            try
            {
                if (string.IsNullOrEmpty(_sdkVersion))
                {
                    var v = new byte[64];
                    HikvisionFpModule.FPModule_GetSDKVersion(v);
                    _sdkVersion = HikvisionFpModule.Texto(v);
                    _logger.LogInformation("FPModule SDK (Hikvision): {V}", _sdkVersion);
                }

                int r = HikvisionFpModule.FPModule_OpenDevice();
                if (r != 0)
                {
                    _logger.LogDebug("No hay lector Hikvision conectado (OpenDevice={R})", r);
                    return false;
                }
                var info = new byte[64];
                HikvisionFpModule.FPModule_GetDeviceInfo(info);
                _deviceName = HikvisionFpModule.Texto(info);
                if (string.IsNullOrEmpty(_deviceName)) _deviceName = Model;
                (_serialNumber, _unidad) = LeerSerieYUnidad();
                _abierto = true;
                _logger.LogInformation("✅ Lector Hikvision conectado: {N} SN {S}{U}", _deviceName, _serialNumber == "" ? "?" : _serialNumber,
                    _unidad == "" ? "" : $" (unidad {_unidad})");
                return true;
            }
            catch (DllNotFoundException ex)
            {
                _dllDisponible = false;
                _logger.LogWarning("⚠️ FPModule_SDK.dll no encontrada; el lector Hikvision no estará disponible: {M}", ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error al abrir el lector Hikvision");
                return false;
            }
        }
    }

    /// <summary>El lector es una unidad de CD-ROM USB: de ahí se sacan la letra y el número de serie (WMI).</summary>
    private (string serie, string unidad) LeerSerieYUnidad()
    {
        try
        {
            using var buscador = new ManagementObjectSearcher("SELECT Drive, Caption, PNPDeviceID FROM Win32_CDROMDrive");
            foreach (ManagementObject d in buscador.Get())
            {
                string caption = d["Caption"]?.ToString() ?? "";
                string pnp = d["PNPDeviceID"]?.ToString() ?? "";
                if (!caption.Contains("K1F8", StringComparison.OrdinalIgnoreCase) && !pnp.Contains("K1F8", StringComparison.OrdinalIgnoreCase)) continue;
                // USBSTOR\CDROM&VEN_&PROD_DS-K1F820-F&REV_0110\2027300413&0
                string serie = pnp.Split('\\').LastOrDefault() ?? "";
                int amp = serie.IndexOf('&');
                if (amp > 0) serie = serie.Substring(0, amp);
                return (serie, d["Drive"]?.ToString() ?? "");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("No se pudo leer la serie del lector por WMI: {M}", ex.Message);
        }
        return ("", "");
    }

    public void CloseDevice()
    {
        lock (_sync)
        {
            if (!_abierto) return;
            try { HikvisionFpModule.FPModule_CloseDevice(); } catch { /* ya cerrado */ }
            _abierto = false;
            _logger.LogInformation("Lector Hikvision cerrado");
        }
    }

    public bool Reconnect()
    {
        _logger.LogInformation("🔄 Reconectando lector Hikvision...");
        CloseDevice();
        return OpenDevice();
    }

    /// <summary>Se llama cuando una función del SDK falla: el lector se desenchufó. La próxima llamada vuelve a buscarlo.</summary>
    private void MarcarDesconectado(string motivo)
    {
        _logger.LogWarning("⚠️ Lector Hikvision desconectado ({M})", motivo);
        try { HikvisionFpModule.FPModule_CloseDevice(); } catch { }
        _abierto = false;
    }

    #endregion

    #region Captura

    /// <summary>
    /// Espera un dedo y devuelve la plantilla SourceAFIS. Si ya hay una captura en curso, la nueva llamada
    /// se suma a ella para no perder el dedo que se apoye.
    /// </summary>
    public Task<CaptureResult> CaptureAsync(int timeoutMs = 10000)
    {
        lock (_sync)
        {
            if (_captureEnCurso != null && !_captureEnCurso.IsCompleted) return _captureEnCurso;
            _captureEnCurso = Task.Run(() => CapturarBloqueante(timeoutMs));
            return _captureEnCurso;
        }
    }

    private CaptureResult CapturarBloqueante(int timeoutMs)
    {
        if (!IsReady && !OpenDevice()) return CaptureResult.Failure("Dispositivo no disponible");

        int ms = Math.Clamp(timeoutMs, 1000, 60000);
        _logger.LogInformation("👆 Esperando huella en Hikvision... (hasta {S}s)", ms / 1000);

        var imagen = CapturarImagen(ms, out string? error, out int ancho, out int alto);
        if (imagen == null) return CaptureResult.Failure(error ?? "No se pudo capturar la huella");

        try
        {
            var template = TemplateDesdeImagen(imagen, ancho, alto);
            var bytes = template.ToByteArray();
            _logger.LogInformation("✅ Huella capturada ({W}x{H}, plantilla SourceAFIS de {N} bytes)", ancho, alto, bytes.Length);
            return new CaptureResult
            {
                Success = true,
                Template = bytes,
                TemplateBase64 = Codificar(bytes),
                ImageWidth = ancho,
                ImageHeight = alto,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando la imagen de la huella");
            return CaptureResult.Failure("No se pudo procesar la huella; vuelva a intentarlo");
        }
    }

    /// <summary>
    /// Espera a que haya un dedo (DetectFinger) y pide la imagen. Si la imagen sale casi blanca (el dedo
    /// se levantó o apoyó muy poco) vuelve a intentarlo mientras quede tiempo.
    /// </summary>
    private byte[]? CapturarImagen(int timeoutMs, out string? error, out int ancho, out int alto)
    {
        error = null; ancho = 0; alto = 0;
        lock (_sync)
        {
            if (!_abierto) { error = "Dispositivo no disponible"; return null; }

            var limite = DateTime.Now.AddMilliseconds(timeoutMs);
            var buffer = new byte[1024 * 1024];
            bool huboIntento = false;
            while (DateTime.Now < limite)
            {
                int estado = 0;
                int r = HikvisionFpModule.FPModule_DetectFinger(ref estado);
                if (r != 0)
                {
                    MarcarDesconectado($"DetectFinger={r}");
                    error = "Lector de huella desconectado";
                    return null;
                }
                if (estado != 1) { Thread.Sleep(80); continue; }

                int w = 0, h = 0;
                r = HikvisionFpModule.FPModule_CaptureImage(buffer, ref w, ref h);
                if (r != 0)
                {
                    MarcarDesconectado($"CaptureImage={r}");
                    error = "Lector de huella desconectado";
                    return null;
                }
                if (w <= 0 || h <= 0 || w * h > buffer.Length)
                {
                    error = $"Imagen inválida ({w}x{h})";
                    return null;
                }
                huboIntento = true;
                long suma = 0;
                for (int i = 0; i < w * h; i++) suma += buffer[i];
                int gris = (int)(suma / (w * h));
                if (gris > GrisMaximoConDedo)
                {
                    // Dedo apenas apoyado: esperar un poco y repetir
                    _logger.LogDebug("Imagen casi vacía (gris {G}); reintentando", gris);
                    Thread.Sleep(150);
                    continue;
                }
                _imageWidth = w; _imageHeight = h;
                ancho = w; alto = h;
                var imagen = new byte[w * h];
                Array.Copy(buffer, imagen, w * h);
                return imagen;
            }
            error = huboIntento ? "Huella de mala calidad; apoye bien el dedo y vuelva a intentarlo" : "Timeout esperando huella";
            return null;
        }
    }

    /// <summary>Entre capturas del registro: espera a que se levante el dedo (para que sean apoyos distintos).</summary>
    private void EsperarDedoFuera(int timeoutMs)
    {
        var limite = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < limite)
        {
            int estado = 0;
            lock (_sync)
            {
                if (!_abierto || HikvisionFpModule.FPModule_DetectFinger(ref estado) != 0) return;
            }
            if (estado != 1) return;
            Thread.Sleep(100);
        }
    }

    private static FingerprintTemplate TemplateDesdeImagen(byte[] pixeles, int ancho, int alto)
    {
        var imagen = new FingerprintImage(ancho, alto, pixeles, new FingerprintImageOptions { Dpi = ImageDpi });
        return new FingerprintTemplate(imagen);
    }

    #endregion

    #region Registro (3 capturas)

    /// <summary>
    /// Registro: 3 capturas del mismo dedo. SourceAFIS no fusiona plantillas, así que se guarda la captura
    /// que mejor se parece a las otras dos (la más representativa).
    /// </summary>
    public async Task<EnrollResult> EnrollAsync(int captureTimeoutMs = 15000)
    {
        if (!IsReady && !OpenDevice()) return EnrollResult.Failure("Dispositivo no disponible");

        const int capturas = 3;
        var plantillas = new FingerprintTemplate[capturas];
        var bytes = new byte[capturas][];
        _logger.LogInformation("🔐 Registro Hikvision: se requieren {N} capturas", capturas);

        for (int i = 0; i < capturas; i++)
        {
            _logger.LogInformation("  Captura {C}/{T}...", i + 1, capturas);
            var cap = await CaptureAsync(captureTimeoutMs);
            if (!cap.Success || cap.Template == null) return EnrollResult.Failure($"Captura {i + 1} fallida: {cap.ErrorMessage}");
            bytes[i] = cap.Template;
            plantillas[i] = new FingerprintTemplate(cap.Template);
            if (i > 0)
            {
                double score = new FingerprintMatcher(plantillas[i]).Match(plantillas[i - 1]);
                if (score < MatchThreshold) return EnrollResult.Failure("Las capturas no coinciden. Use el mismo dedo.");
            }
            _logger.LogInformation("  ✅ Captura {C}/{T} OK", i + 1, capturas);
            if (i < capturas - 1)
            {
                await Task.Run(() => EsperarDedoFuera(3000));
                await Task.Delay(300);
            }
        }

        int mejor = 0; double mejorSuma = -1;
        for (int i = 0; i < capturas; i++)
        {
            var m = new FingerprintMatcher(plantillas[i]);
            double suma = 0;
            for (int j = 0; j < capturas; j++) if (j != i) suma += m.Match(plantillas[j]);
            if (suma > mejorSuma) { mejorSuma = suma; mejor = i; }
        }
        _logger.LogInformation("✅ Registro completado (captura {M} elegida, {N} bytes)", mejor + 1, bytes[mejor].Length);
        return new EnrollResult { Success = true, Template = bytes[mejor], TemplateBase64 = Codificar(bytes[mejor]) };
    }

    #endregion

    #region Verificación y comparación

    public async Task<VerifyResult> VerifyAsync(string storedTemplateBase64, int timeoutMs = 10000)
    {
        if (!IsReady && !OpenDevice()) return VerifyResult.Failure("Dispositivo no disponible");
        if (!EsPlantillaPropia(storedTemplateBase64))
            return VerifyResult.Failure("La huella guardada se registró con el lector ZKTeco; vuelva a registrarla con este lector");

        var cap = await CaptureAsync(timeoutMs);
        if (!cap.Success || cap.Template == null) return VerifyResult.Failure(cap.ErrorMessage ?? "Error en captura");

        var r = Comparar(cap.TemplateBase64!, storedTemplateBase64);
        _logger.LogInformation(r.IsMatch ? "✅ Verificada (score: {S})" : "❌ No coincide (score: {S})", r.Score);
        return new VerifyResult { Success = true, IsMatch = r.IsMatch, Score = r.Score, CapturedTemplate = cap.TemplateBase64 };
    }

    /// <summary>true si la cadena es una plantilla SourceAFIS de este servicio (prefijo SAFIS1:).</summary>
    public static bool EsPlantillaPropia(string? template) =>
        !string.IsNullOrEmpty(template) && template.StartsWith(TemplatePrefix, StringComparison.Ordinal);

    public static string Codificar(byte[] bytes) => TemplatePrefix + Convert.ToBase64String(bytes);

    private static FingerprintTemplate? Decodificar(string template)
    {
        try { return new FingerprintTemplate(Convert.FromBase64String(template.Substring(TemplatePrefix.Length))); }
        catch { return null; }
    }

    /// <summary>Compara dos plantillas SourceAFIS (sin lector). El matcher de la primera se reutiliza entre llamadas.</summary>
    public static MatchResult Comparar(string template1, string template2)
    {
        if (!EsPlantillaPropia(template1) || !EsPlantillaPropia(template2))
            return new MatchResult { Success = false, ErrorMessage = "Template(s) inválido(s)" };
        var candidata = Decodificar(template2);
        if (candidata == null) return new MatchResult { Success = false, ErrorMessage = "Template(s) inválido(s)" };

        FingerprintMatcher? matcher;
        lock (_matcherLock)
        {
            if (_matcher == null || _matcherKey != template1)
            {
                var sonda = Decodificar(template1);
                if (sonda == null) return new MatchResult { Success = false, ErrorMessage = "Template(s) inválido(s)" };
                _matcher = new FingerprintMatcher(sonda);
                _matcherKey = template1;
            }
            matcher = _matcher;
        }
        double score = matcher.Match(candidata);
        return new MatchResult { Success = true, IsMatch = score >= MatchThreshold, Score = (int)Math.Round(score) };
    }

    #endregion

    public DeviceStatus GetStatus() => new()
    {
        SdkInitialized = _dllDisponible,
        DeviceConnected = IsReady,
        DeviceCount = IsReady ? 1 : 0,
        ImageWidth = IsReady ? (_imageWidth > 0 ? _imageWidth : 256) : 0,
        ImageHeight = IsReady ? (_imageHeight > 0 ? _imageHeight : 288) : 0,
        IsReady = IsReady,
    };

    public void Dispose()
    {
        CloseDevice();
        GC.SuppressFinalize(this);
    }
}
