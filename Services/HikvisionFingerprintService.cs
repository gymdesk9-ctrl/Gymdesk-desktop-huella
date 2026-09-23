using System.Runtime.InteropServices;
using SourceAFIS;

namespace GymDesk.Fingerprint.Services;

/// <summary>
/// Lector de huella Hikvision DS-K1F820-F por USB (USB SDK de Hikvision).
///
/// El lector entrega la IMAGEN de la huella (256 x 360, 8 bits, 508 dpi). La plantilla y la comparación
/// las hace SourceAFIS en la PC, así el acceso por huella (1:N contra los socios) funciona igual que con
/// el ZK9500. Las plantillas se guardan con el prefijo "SAFIS1:" + base64 para distinguirlas de las del
/// ZKTeco (los dos formatos no son comparables entre sí).
/// </summary>
public class HikvisionFingerprintService : IDisposable
{
    public const string Model = "Hikvision DS-K1F820-F";
    public const string TemplatePrefix = "SAFIS1:";

    /// <summary>Puntuación SourceAFIS a partir de la cual dos huellas son la misma (40 ≈ 0,01 % de falsos positivos).</summary>
    public const double MatchThreshold = 40;

    private const int ImageWidth = 256;
    private const int ImageHeight = 360;
    private const double ImageDpi = 508;

    private readonly ILogger<HikvisionFingerprintService> _logger;
    private readonly object _sync = new();

    private bool _sdkInitialized;
    private int _userId = HikvisionUsbSdk.InvalidUserId;
    private int _deviceCount;
    private string _serialNumber = "";
    private string _deviceName = "";
    private Task<CaptureResult>? _captureEnCurso;

    // Un FingerprintMatcher es caro de crear: se guarda el de la última huella consultada (identificación 1:N)
    private static readonly object _matcherLock = new();
    private static string? _matcherKey;
    private static FingerprintMatcher? _matcher;

    public bool IsReady => _userId != HikvisionUsbSdk.InvalidUserId;
    public string SerialNumber => _serialNumber;
    public string DeviceName => _deviceName;

    public HikvisionFingerprintService(ILogger<HikvisionFingerprintService> logger)
    {
        _logger = logger;
    }

    #region Inicialización

    public bool Initialize()
    {
        lock (_sync)
        {
            if (_sdkInitialized) return true;
            try
            {
                if (!HikvisionUsbSdk.USB_SDK_Init())
                {
                    _logger.LogError("❌ USB_SDK_Init (Hikvision) falló: {Err}", HikvisionUsbSdk.LastError());
                    return false;
                }
                _sdkInitialized = true;
                // Los tamaños tienen que coincidir con HCUsbSDK.h (192/128/64/64/32/32/32); si no, el SDK leería basura
                int[] tam = {
                    Marshal.SizeOf<HikvisionUsbSdk.LoginInfo>(), Marshal.SizeOf<HikvisionUsbSdk.DeviceRegRes>(),
                    Marshal.SizeOf<HikvisionUsbSdk.ConfigInput>(), Marshal.SizeOf<HikvisionUsbSdk.ConfigOutput>(),
                    Marshal.SizeOf<HikvisionUsbSdk.FingerPrintOperParam>(), Marshal.SizeOf<HikvisionUsbSdk.FingerPrintCond>(),
                    Marshal.SizeOf<HikvisionUsbSdk.FingerPrint>() };
                int[] esperados = { 192, 128, 64, 64, 32, 32, 32 };
                if (!tam.SequenceEqual(esperados))
                {
                    _logger.LogError("❌ Tamaños de estructuras del USB SDK incorrectos: {T} (esperado {E})", string.Join(",", tam), string.Join(",", esperados));
                    return false;
                }
                uint v = HikvisionUsbSdk.USB_SDK_GetSDKVersion();
                _logger.LogInformation("✅ USB SDK Hikvision inicializado (v{V})", $"{(v >> 24) & 0xff}.{(v >> 16) & 0xff}.{v & 0xffff}");
                return true;
            }
            catch (DllNotFoundException ex)
            {
                _logger.LogWarning("⚠️ HCUsbSDK.dll no encontrada: {M}", ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Excepción al inicializar el USB SDK de Hikvision");
                return false;
            }
        }
    }

    /// <summary>Vendor ID USB de Hikvision Digital Technology.</summary>
    private const uint HikvisionVid = 0x2BDF;

    /// <summary>
    /// El SDK enumera TODOS los dispositivos HID de la PC (teclado, touchpad...). Iniciar sesión en uno que no
    /// sea de Hikvision hace que el SDK se caiga (y con él este servicio), así que solo se aceptan equipos
    /// Hikvision o que se anuncien como lector de huella. GYMDESK_HIK_VID permite añadir otro VID (hex).
    /// </summary>
    private static bool EsLectorHikvision(HikvisionUsbSdk.DeviceInfo d)
    {
        if (d.dwVID == HikvisionVid) return true;
        var extra = Environment.GetEnvironmentVariable("GYMDESK_HIK_VID");
        if (!string.IsNullOrEmpty(extra) && uint.TryParse(extra.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var vid) && vid == d.dwVID) return true;
        var texto = (HikvisionUsbSdk.Texto(d.szDeviceName) + " " + HikvisionUsbSdk.Texto(d.szManufacturer)).ToLowerInvariant();
        return texto.Contains("hik") || texto.Contains("finger") || texto.Contains("k1f8") || texto.Contains("huella");
    }

    private List<HikvisionUsbSdk.DeviceInfo> Enumerar()
    {
        var lista = new List<HikvisionUsbSdk.DeviceInfo>();
        HikvisionUsbSdk.EnumDeviceCallback cb = (ref HikvisionUsbSdk.DeviceInfo info, IntPtr _) => lista.Add(info);
        bool ok = HikvisionUsbSdk.USB_SDK_EnumDevice(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        if (!ok) _logger.LogDebug("USB_SDK_EnumDevice devolvió false: {Err}", HikvisionUsbSdk.LastError());

        // El SDK repite cada dispositivo por cada interfaz HID: quedarse con uno por VID/PID/serie
        var unicos = lista.GroupBy(d => (d.dwVID, d.dwPID, HikvisionUsbSdk.Texto(d.szSerialNumber))).Select(g => g.First()).ToList();
        foreach (var d in unicos)
        {
            _logger.LogInformation("🔎 USB HID: VID 0x{V:X4} PID 0x{P:X4} \"{N}\" ({F}) SN {S}{H}", d.dwVID, d.dwPID,
                HikvisionUsbSdk.Texto(d.szDeviceName), HikvisionUsbSdk.Texto(d.szManufacturer), HikvisionUsbSdk.Texto(d.szSerialNumber),
                EsLectorHikvision(d) ? " → lector Hikvision" : "");
        }
        return unicos.Where(EsLectorHikvision).ToList();
    }

    public bool OpenDevice()
    {
        lock (_sync)
        {
            if (IsReady) return true;
            if (!_sdkInitialized && !Initialize()) return false;

            try
            {
                var dispositivos = Enumerar();
                _deviceCount = dispositivos.Count;
                if (dispositivos.Count == 0)
                {
                    _logger.LogDebug("No hay lector Hikvision conectado");
                    return false;
                }
                // Si hay varios equipos Hikvision (por ejemplo un enrolador de tarjetas), preferir el de huella
                var dev = dispositivos.FirstOrDefault(d =>
                {
                    var n = (HikvisionUsbSdk.Texto(d.szDeviceName) + " " + HikvisionUsbSdk.Texto(d.szManufacturer)).ToLowerInvariant();
                    return n.Contains("finger") || n.Contains("k1f8") || n.Contains("huella");
                });
                if (dev.dwVID == 0 && dev.dwPID == 0) dev = dispositivos[0];
                _logger.LogInformation("🔌 Conectando al lector Hikvision VID 0x{V:X4} PID 0x{P:X4}...", dev.dwVID, dev.dwPID);

                // Las estaciones de Hikvision aceptan admin/12345 (usuario por defecto del SDK); si no, sin credenciales
                foreach (var (usuario, clave) in new[] { ("admin", "12345"), ("", "") })
                {
                    var login = new HikvisionUsbSdk.LoginInfo
                    {
                        dwSize = (uint)Marshal.SizeOf<HikvisionUsbSdk.LoginInfo>(),
                        dwTimeout = 5000,
                        dwVID = dev.dwVID,
                        dwPID = dev.dwPID,
                        szUserName = HikvisionUsbSdk.Bytes(usuario, 32),
                        szPassword = HikvisionUsbSdk.Bytes(clave, 16),
                        szSerialNumber = dev.szSerialNumber ?? new byte[48],
                        byRes = new byte[80],
                    };
                    var res = new HikvisionUsbSdk.DeviceRegRes
                    {
                        dwSize = (uint)Marshal.SizeOf<HikvisionUsbSdk.DeviceRegRes>(),
                        szDeviceName = new byte[32], szSerialNumber = new byte[48], byRes = new byte[40],
                    };
                    int id = HikvisionUsbSdk.USB_SDK_Login(ref login, ref res);
                    if (id == HikvisionUsbSdk.InvalidUserId)
                    {
                        _logger.LogWarning("⚠️ Login Hikvision ({U}) falló: {Err}", usuario == "" ? "sin usuario" : usuario, HikvisionUsbSdk.LastError());
                        continue;
                    }
                    _userId = id;
                    _deviceName = HikvisionUsbSdk.Texto(res.szDeviceName);
                    _serialNumber = HikvisionUsbSdk.Texto(res.szSerialNumber);
                    if (string.IsNullOrEmpty(_serialNumber)) _serialNumber = HikvisionUsbSdk.Texto(dev.szSerialNumber);
                    _logger.LogInformation("✅ Lector Hikvision conectado: {N} SN {S} (fw {V})", _deviceName, _serialNumber,
                        $"{res.dwSoftwareVersion >> 16}.{res.dwSoftwareVersion & 0xffff}");
                    break;
                }
                if (!IsReady) return false;

                // Pedimos IMAGEN (la comparación la hace la PC con SourceAFIS)
                if (!ConfigurarCaptura())
                {
                    _logger.LogError("❌ No se pudo configurar la captura por imagen: {Err}", HikvisionUsbSdk.LastError());
                    CloseDevice();
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error al abrir el lector Hikvision");
                return false;
            }
        }
    }

    private bool ConfigurarCaptura()
    {
        var param = new HikvisionUsbSdk.FingerPrintOperParam
        {
            dwSize = (uint)Marshal.SizeOf<HikvisionUsbSdk.FingerPrintOperParam>(),
            byFPCompareType = 2,   // compara la plataforma
            byFPCaptureType = 2,   // imagen
            byFPCompareTimeout = 5,
            byFPCompareMatchLevel = 3,
            byRes = new byte[24],
        };
        IntPtr pParam = Marshal.AllocHGlobal(Marshal.SizeOf<HikvisionUsbSdk.FingerPrintOperParam>());
        IntPtr pInput = Marshal.AllocHGlobal(Marshal.SizeOf<HikvisionUsbSdk.ConfigInput>());
        try
        {
            Marshal.StructureToPtr(param, pParam, false);
            var input = new HikvisionUsbSdk.ConfigInput
            {
                lpInBuffer = pParam,
                dwInBufferSize = (uint)Marshal.SizeOf<HikvisionUsbSdk.FingerPrintOperParam>(),
                byRes = new byte[48],
            };
            Marshal.StructureToPtr(input, pInput, false);
            return HikvisionUsbSdk.USB_SDK_SetDeviceConfig(_userId, HikvisionUsbSdk.CmdSetFingerPrintOperParam, pInput, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(pParam);
            Marshal.FreeHGlobal(pInput);
        }
    }

    public void CloseDevice()
    {
        lock (_sync)
        {
            if (!IsReady) return;
            try { HikvisionUsbSdk.USB_SDK_Logout(_userId); } catch { /* ya cerrado */ }
            _userId = HikvisionUsbSdk.InvalidUserId;
            _logger.LogInformation("Lector Hikvision cerrado");
        }
    }

    public bool Reconnect()
    {
        _logger.LogInformation("🔄 Reconectando lector Hikvision...");
        CloseDevice();
        return OpenDevice();
    }

    #endregion

    #region Captura

    /// <summary>
    /// Espera un dedo y devuelve la plantilla SourceAFIS. El lector espera de 10 a 60 s (mínimo del SDK);
    /// si ya hay una captura en curso, la nueva llamada se suma a ella para no perder el dedo que se apoye.
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

        int segundos = Math.Clamp((int)Math.Ceiling(timeoutMs / 1000.0), 10, 60);
        _logger.LogInformation("👆 Esperando huella en Hikvision... (hasta {S}s)", segundos);

        var imagen = CapturarImagen(segundos, out string? error);
        if (imagen == null) return CaptureResult.Failure(error ?? "No se pudo capturar la huella");

        try
        {
            var template = TemplateDesdeImagen(imagen);
            var bytes = template.ToByteArray();
            _logger.LogInformation("✅ Huella capturada (plantilla SourceAFIS de {N} bytes)", bytes.Length);
            return new CaptureResult
            {
                Success = true,
                Template = bytes,
                TemplateBase64 = Codificar(bytes),
                ImageWidth = ImageWidth,
                ImageHeight = ImageHeight,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando la imagen de la huella");
            return CaptureResult.Failure("No se pudo procesar la huella; vuelva a intentarlo");
        }
    }

    /// <summary>Llama al SDK (bloquea hasta que hay dedo o vence la espera) y devuelve la imagen en bruto.</summary>
    private byte[]? CapturarImagen(int segundos, out string? error)
    {
        error = null;
        lock (_sync)
        {
            if (!IsReady) { error = "Dispositivo no disponible"; return null; }

            int tamCond = Marshal.SizeOf<HikvisionUsbSdk.FingerPrintCond>();
            int tamFp = Marshal.SizeOf<HikvisionUsbSdk.FingerPrint>();
            IntPtr pCond = Marshal.AllocHGlobal(tamCond);
            IntPtr pFp = Marshal.AllocHGlobal(tamFp);
            IntPtr pInput = Marshal.AllocHGlobal(Marshal.SizeOf<HikvisionUsbSdk.ConfigInput>());
            IntPtr pOutput = Marshal.AllocHGlobal(Marshal.SizeOf<HikvisionUsbSdk.ConfigOutput>());
            IntPtr pBuffer = Marshal.AllocHGlobal(HikvisionUsbSdk.MaxFingerPrint);
            try
            {
                var cond = new HikvisionUsbSdk.FingerPrintCond { dwSize = (uint)tamCond, byWait = (byte)segundos, byRes = new byte[27] };
                Marshal.StructureToPtr(cond, pCond, false);
                var fp = new HikvisionUsbSdk.FingerPrint
                {
                    dwSize = (uint)tamFp,
                    dwFPSize = HikvisionUsbSdk.MaxFingerPrint,
                    pFPBuffer = pBuffer,
                    byRes = new byte[17],
                };
                Marshal.StructureToPtr(fp, pFp, false);
                var input = new HikvisionUsbSdk.ConfigInput { lpInBuffer = pCond, dwInBufferSize = (uint)tamCond, byRes = new byte[48] };
                Marshal.StructureToPtr(input, pInput, false);
                var output = new HikvisionUsbSdk.ConfigOutput { lpOutBuffer = pFp, dwOutBufferSize = (uint)tamFp, byRes = new byte[56] };
                Marshal.StructureToPtr(output, pOutput, false);

                if (!HikvisionUsbSdk.USB_SDK_GetDeviceConfig(_userId, HikvisionUsbSdk.CmdCaptureFingerPrint, pInput, pOutput))
                {
                    uint code = HikvisionUsbSdk.USB_SDK_GetLastError();
                    string err = HikvisionUsbSdk.LastError();
                    if (code == HikvisionUsbSdk.ErrTimeout) { error = "Timeout esperando huella"; return null; }
                    _logger.LogWarning("⚠️ Captura Hikvision falló: {Err}", err);
                    if (code == HikvisionUsbSdk.ErrNoDevice || code == HikvisionUsbSdk.ErrDevNotReady || code >= 7 && code <= 9)
                    {
                        // El lector se desconectó: la próxima llamada vuelve a buscarlo
                        try { HikvisionUsbSdk.USB_SDK_Logout(_userId); } catch { }
                        _userId = HikvisionUsbSdk.InvalidUserId;
                        error = "Lector de huella desconectado";
                        return null;
                    }
                    error = "Error del lector: " + err;
                    return null;
                }

                var salida = Marshal.PtrToStructure<HikvisionUsbSdk.FingerPrint>(pFp);
                switch (salida.byResult)
                {
                    case 1: break;
                    case 3: error = "Timeout esperando huella"; return null;
                    case 4: error = "Huella de mala calidad; limpie el dedo y vuelva a intentarlo"; return null;
                    default: error = "El lector no pudo capturar la huella"; return null;
                }
                if (salida.byFPType != 2)
                {
                    error = "El lector devolvió una plantilla en vez de la imagen";
                    _logger.LogError("Captura Hikvision: byFPType={T}; se esperaba 2 (imagen). Revise byFPCompareType/byFPCaptureType", salida.byFPType);
                    return null;
                }
                int esperado = ImageWidth * ImageHeight;
                int largo = salida.dwFPSize > 0 && salida.dwFPSize <= HikvisionUsbSdk.MaxFingerPrint ? (int)salida.dwFPSize : esperado;
                if (largo < esperado)
                {
                    error = $"Imagen incompleta ({largo} bytes)";
                    return null;
                }
                var imagen = new byte[esperado];
                Marshal.Copy(pBuffer, imagen, 0, esperado);
                return imagen;
            }
            finally
            {
                Marshal.FreeHGlobal(pCond);
                Marshal.FreeHGlobal(pFp);
                Marshal.FreeHGlobal(pInput);
                Marshal.FreeHGlobal(pOutput);
                Marshal.FreeHGlobal(pBuffer);
            }
        }
    }

    private static FingerprintTemplate TemplateDesdeImagen(byte[] pixeles)
    {
        var imagen = new FingerprintImage(ImageWidth, ImageHeight, pixeles, new FingerprintImageOptions { Dpi = ImageDpi });
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
            if (i < capturas - 1) await Task.Delay(500);
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
        SdkInitialized = _sdkInitialized,
        DeviceConnected = IsReady,
        DeviceCount = IsReady ? Math.Max(1, _deviceCount) : _deviceCount,
        ImageWidth = IsReady ? ImageWidth : 0,
        ImageHeight = IsReady ? ImageHeight : 0,
        IsReady = IsReady,
    };

    public void Dispose()
    {
        CloseDevice();
        if (_sdkInitialized)
        {
            try { HikvisionUsbSdk.USB_SDK_Cleanup(); } catch { }
            _sdkInitialized = false;
        }
        GC.SuppressFinalize(this);
    }
}
