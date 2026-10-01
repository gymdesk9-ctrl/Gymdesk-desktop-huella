using System.Management;

namespace GymDesk.Fingerprint.Services;

public enum LectorHuella { Ninguno, ZKTeco, Hikvision }

/// <summary>
/// Elige el lector USB conectado (ZKTeco ZK9500 o Hikvision DS-K1F820-F) y reparte las operaciones.
/// El API REST habla siempre con este servicio; el formato de plantilla depende del lector:
///   - ZKTeco: plantilla del SDK ZKFinger (base64).
///   - Hikvision: plantilla SourceAFIS con prefijo "SAFIS1:".
/// Las plantillas de un lector no se pueden comparar con las del otro.
///
/// USO COMPARTIDO: el lector solo lo puede tener abierto un programa a la vez, y en muchos gimnasios GymDesk
/// convive con otro programa que usa el mismo lector. Por eso aquí el lector se abre SOLO mientras se usa
/// (capturar, registrar, verificar) y se suelta a los pocos segundos de quedar inactivo. Consultar el estado
/// no lo abre: la presencia se mira en la lista de dispositivos de Windows.
/// </summary>
public class FingerprintManager : IDisposable
{
    private readonly ZKFingerprintService _zk;
    private readonly HikvisionFingerprintService _hik;
    private readonly ILogger<FingerprintManager> _logger;
    private readonly object _sync = new();
    private DateTime _ultimoIntento = DateTime.MinValue;

    /// <summary>Segundos sin uso tras los que se suelta el lector.</summary>
    private const double SegundosInactivo = 4;
    private int _operaciones;                                  // capturas/registros en curso
    private DateTime _ultimoUso = DateTime.MinValue;
    private readonly Timer _liberador;
    private (LectorHuella lector, DateTime cuando) _presencia = (LectorHuella.Ninguno, DateTime.MinValue);

    public LectorHuella Activo { get; private set; } = LectorHuella.Ninguno;

    public FingerprintManager(ZKFingerprintService zk, HikvisionFingerprintService hik, ILogger<FingerprintManager> logger)
    {
        _zk = zk;
        _hik = hik;
        _logger = logger;
        _liberador = new Timer(_ => LiberarSiInactivo(), null, 1000, 1000);
    }

    /// <summary>Suelta el lector si lleva unos segundos sin usarse: queda disponible para otros programas.</summary>
    private void LiberarSiInactivo()
    {
        if (Volatile.Read(ref _operaciones) > 0) return;
        if ((DateTime.UtcNow - _ultimoUso).TotalSeconds < SegundosInactivo) return;
        Liberar();
    }

    /// <summary>Suelta el lector ahora mismo (salvo que haya una captura en curso).</summary>
    private void Liberar()
    {
        try
        {
            lock (_sync)
            {
                if (Volatile.Read(ref _operaciones) > 0) return;
                if (Activo == LectorHuella.Ninguno && !_zk.SdkActivo && !_hik.IsReady) return;
                _zk.LiberarTodo();
                _hik.CloseDevice();
                Activo = LectorHuella.Ninguno;
                _ultimoIntento = DateTime.MinValue;
            }
            _logger.LogInformation("🔓 Lector de huella liberado (disponible para otros programas)");
        }
        catch (Exception ex)
        {
            _logger.LogDebug("No se pudo liberar el lector: {M}", ex.Message);
        }
    }

    private static string MensajeCedido(string programa) =>
        $"Lector de huella cedido a otro programa ({programa}) mientras se trabaja en él. Al volver a GymDesk se retoma solo.";

    /// <summary>¿Qué lector hay enchufado? Se mira en Windows (WMI), sin abrir el aparato.</summary>
    private LectorHuella DetectarPresencia()
    {
        if ((DateTime.UtcNow - _presencia.cuando).TotalSeconds < 2) return _presencia.lector;
        var lector = LectorHuella.Ninguno;
        try
        {
            // ZKTeco (ZK9500 y similares): fabricante USB 1B55
            using (var zk = new ManagementObjectSearcher(@"SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\VID_1B55%'"))
            {
                if (zk.Get().Count > 0) lector = LectorHuella.ZKTeco;
            }
            if (lector == LectorHuella.Ninguno)
            {
                // Hikvision DS-K1F820-F: se presenta como unidad de CD-ROM USB
                using var cd = new ManagementObjectSearcher("SELECT Caption, PNPDeviceID FROM Win32_CDROMDrive");
                foreach (ManagementObject d in cd.Get())
                {
                    string texto = (d["Caption"]?.ToString() ?? "") + " " + (d["PNPDeviceID"]?.ToString() ?? "");
                    if (texto.Contains("K1F8", StringComparison.OrdinalIgnoreCase)) { lector = LectorHuella.Hikvision; break; }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("No se pudo consultar los dispositivos de Windows: {M}", ex.Message);
        }
        _presencia = (lector, DateTime.UtcNow);
        return lector;
    }

    /// <summary>Ejecuta una operación con el lector tomado y lo deja marcado para soltarlo al quedar inactivo.</summary>
    private async Task<T> Usar<T>(Func<Task<T>> operacion, Func<string, T> fallo)
    {
        Interlocked.Increment(ref _operaciones);
        try
        {
            if (!AsegurarLector())
            {
                return fallo(DetectarPresencia() != LectorHuella.Ninguno
                    ? "El lector de huella lo está usando otro programa en este momento. Espere a que lo suelte y vuelva a intentarlo."
                    : "Dispositivo no disponible");
            }
            return await operacion();
        }
        finally
        {
            _ultimoUso = DateTime.UtcNow;
            Interlocked.Decrement(ref _operaciones);
        }
    }

    public bool IsReady => Activo switch
    {
        LectorHuella.ZKTeco => _zk.IsReady,
        LectorHuella.Hikvision => _hik.IsReady,
        _ => false,
    };

    public string Modelo => Activo switch
    {
        LectorHuella.ZKTeco => "ZKTeco ZK9500",
        LectorHuella.Hikvision => HikvisionFingerprintService.Model,
        _ => "Sin lector",
    };

    public string SerialNumber => Activo == LectorHuella.Hikvision ? _hik.SerialNumber : "";

    /// <summary>Busca un lector: primero ZKTeco, luego Hikvision.</summary>
    public bool OpenDevice(int index = 0)
    {
        lock (_sync)
        {
            _ultimoUso = DateTime.UtcNow;   // abierto a mano (Conectar): se suelta al quedar inactivo
            if (IsReady) return true;
            _ultimoIntento = DateTime.Now;
            if (_zk.OpenDevice(index))
            {
                Activo = LectorHuella.ZKTeco;
                _logger.LogInformation("🖐️ Lector activo: ZKTeco ZK9500");
                return true;
            }
            if (_hik.OpenDevice())
            {
                Activo = LectorHuella.Hikvision;
                _logger.LogInformation("🖐️ Lector activo: {M}", HikvisionFingerprintService.Model);
                return true;
            }
            Activo = LectorHuella.Ninguno;
            return false;
        }
    }

    public void CloseDevice()
    {
        lock (_sync)
        {
            _zk.CloseDevice();
            _hik.CloseDevice();
            Activo = LectorHuella.Ninguno;
        }
    }

    public bool Reconnect()
    {
        lock (_sync)
        {
            CloseDevice();
            if (_zk.Reconnect()) { Activo = LectorHuella.ZKTeco; return true; }
            if (_hik.Reconnect()) { Activo = LectorHuella.Hikvision; return true; }
            return false;
        }
    }

    /// <summary>Toma el lector para usarlo ahora (si no hay, reintenta como mucho cada segundo).</summary>
    private bool AsegurarLector()
    {
        // Con el candado: si justo se está soltando el lector por inactividad, se espera y se vuelve a tomar
        lock (_sync)
        {
            if (IsReady) return true;
            if ((DateTime.Now - _ultimoIntento).TotalSeconds < 1) return false;
            return OpenDevice();
        }
    }

    private Task<CaptureResult> Capturar(int timeoutMs, CancellationToken cancelar) =>
        Usar(() => Activo == LectorHuella.Hikvision ? _hik.CaptureAsync(timeoutMs, cancelar) : _zk.CaptureAsync(timeoutMs, cancelar), CaptureResult.Failure);

    /// <summary>
    /// Captura una huella. Con <paramref name="ceder"/> (el monitor de accesos, que espera dedos sin parar):
    /// si el usuario pasa a trabajar en otro programa que usa el lector, se corta la espera y se le suelta
    /// el lector en el momento; al volver a GymDesk la siguiente captura lo retoma.
    /// </summary>
    public async Task<CaptureResult> CaptureAsync(int timeoutMs, bool ceder = false)
    {
        if (!ceder) return await Capturar(timeoutMs, default);

        if (ProgramaEnPrimerPlano.UsaLectorDeHuella(out string delante))
        {
            Liberar();
            return CaptureResult.Failure(MensajeCedido(delante));
        }

        using var corte = new CancellationTokenSource();
        string cedidoA = "";
        var vigia = Task.Run(async () =>
        {
            while (!corte.IsCancellationRequested)
            {
                if (ProgramaEnPrimerPlano.UsaLectorDeHuella(out string programa)) { cedidoA = programa; corte.Cancel(); break; }
                try { await Task.Delay(300, corte.Token); } catch (OperationCanceledException) { break; }
            }
        });

        var resultado = await Capturar(timeoutMs, corte.Token);
        corte.Cancel();
        await vigia;

        if (!resultado.Success && cedidoA.Length > 0)
        {
            Liberar();
            _logger.LogInformation("🤝 Lector cedido a {P}", cedidoA);
            return CaptureResult.Failure(MensajeCedido(cedidoA));
        }
        return resultado;
    }

    public Task<EnrollResult> EnrollAsync(int timeoutMs) =>
        Usar(() => Activo == LectorHuella.Hikvision ? _hik.EnrollAsync(timeoutMs) : _zk.EnrollAsync(timeoutMs), EnrollResult.Failure);

    public Task<VerifyResult> VerifyAsync(string template, int timeoutMs) =>
        Usar(() =>
        {
            if (Activo == LectorHuella.Hikvision) return _hik.VerifyAsync(template, timeoutMs);
            if (HikvisionFingerprintService.EsPlantillaPropia(template))
                return Task.FromResult(VerifyResult.Failure("La huella guardada se registró con el lector Hikvision; vuelva a registrarla con este lector"));
            return _zk.VerifyAsync(template, timeoutMs);
        }, VerifyResult.Failure);

    /// <summary>Compara dos plantillas. Las SourceAFIS no necesitan lector; las ZKTeco usan el SDK del ZK9500.</summary>
    public MatchResult MatchTemplates(string t1, string t2)
    {
        bool a = HikvisionFingerprintService.EsPlantillaPropia(t1);
        bool b = HikvisionFingerprintService.EsPlantillaPropia(t2);
        if (a && b) return HikvisionFingerprintService.Comparar(t1, t2);
        if (a != b)
        {
            // Una es del ZKTeco y la otra del Hikvision: no son la misma persona (ni comparables)
            return new MatchResult { Success = true, IsMatch = false, Score = 0, ErrorMessage = "Plantillas de lectores distintos" };
        }
        // Plantillas ZKTeco: se comparan con su SDK, sin abrir el lector. Mientras haya comparaciones no se suelta el SDK.
        Interlocked.Increment(ref _operaciones);
        try
        {
            return _zk.MatchTemplates(t1, t2);
        }
        finally
        {
            _ultimoUso = DateTime.UtcNow;
            Interlocked.Decrement(ref _operaciones);
        }
    }

    public DeviceStatus GetStatus()
    {
        // Consultar el estado NO abre el lector. Si GymDesk lo está usando ahora mismo se informa del lector abierto;
        // si no, se mira en Windows si hay uno enchufado ("conectado y listo" = se puede usar cuando haga falta).
        if (IsReady)
        {
            var s = Activo == LectorHuella.Hikvision ? _hik.GetStatus() : _zk.GetStatus();
            s.Model = Modelo;
            s.SerialNumber = SerialNumber;
            s.Reader = Activo.ToString();
            return s;
        }
        var presente = DetectarPresencia();
        bool hay = presente != LectorHuella.Ninguno;
        return new DeviceStatus
        {
            SdkInitialized = true,
            DeviceConnected = hay,
            DeviceCount = hay ? 1 : 0,
            IsReady = hay,
            Model = presente switch { LectorHuella.ZKTeco => "ZKTeco ZK9500", LectorHuella.Hikvision => HikvisionFingerprintService.Model, _ => "Sin lector" },
            SerialNumber = "",
            Reader = presente.ToString(),
        };
    }

    public void Dispose()
    {
        _liberador.Dispose();
        _zk.Dispose();
        _hik.Dispose();
        GC.SuppressFinalize(this);
    }
}
