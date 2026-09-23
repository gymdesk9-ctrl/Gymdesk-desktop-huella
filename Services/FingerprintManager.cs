namespace GymDesk.Fingerprint.Services;

public enum LectorHuella { Ninguno, ZKTeco, Hikvision }

/// <summary>
/// Elige el lector USB conectado (ZKTeco ZK9500 o Hikvision DS-K1F820-F) y reparte las operaciones.
/// El API REST habla siempre con este servicio; el formato de plantilla depende del lector:
///   - ZKTeco: plantilla del SDK ZKFinger (base64).
///   - Hikvision: plantilla SourceAFIS con prefijo "SAFIS1:".
/// Las plantillas de un lector no se pueden comparar con las del otro.
/// </summary>
public class FingerprintManager : IDisposable
{
    private readonly ZKFingerprintService _zk;
    private readonly HikvisionFingerprintService _hik;
    private readonly ILogger<FingerprintManager> _logger;
    private readonly object _sync = new();
    private DateTime _ultimoIntento = DateTime.MinValue;

    public LectorHuella Activo { get; private set; } = LectorHuella.Ninguno;

    public FingerprintManager(ZKFingerprintService zk, HikvisionFingerprintService hik, ILogger<FingerprintManager> logger)
    {
        _zk = zk;
        _hik = hik;
        _logger = logger;
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

    /// <summary>Si no hay lector, vuelve a buscarlo (como mucho cada 3 s para no frenar las consultas de estado).</summary>
    private bool AsegurarLector()
    {
        if (IsReady) return true;
        if ((DateTime.Now - _ultimoIntento).TotalSeconds < 3) return false;
        return OpenDevice();
    }

    public Task<CaptureResult> CaptureAsync(int timeoutMs)
    {
        if (!AsegurarLector()) return Task.FromResult(CaptureResult.Failure("Dispositivo no disponible"));
        return Activo == LectorHuella.Hikvision ? _hik.CaptureAsync(timeoutMs) : _zk.CaptureAsync(timeoutMs);
    }

    public Task<EnrollResult> EnrollAsync(int timeoutMs)
    {
        if (!AsegurarLector()) return Task.FromResult(EnrollResult.Failure("Dispositivo no disponible"));
        return Activo == LectorHuella.Hikvision ? _hik.EnrollAsync(timeoutMs) : _zk.EnrollAsync(timeoutMs);
    }

    public Task<VerifyResult> VerifyAsync(string template, int timeoutMs)
    {
        if (!AsegurarLector()) return Task.FromResult(VerifyResult.Failure("Dispositivo no disponible"));
        if (Activo == LectorHuella.Hikvision) return _hik.VerifyAsync(template, timeoutMs);
        if (HikvisionFingerprintService.EsPlantillaPropia(template))
            return Task.FromResult(VerifyResult.Failure("La huella guardada se registró con el lector Hikvision; vuelva a registrarla con este lector"));
        return _zk.VerifyAsync(template, timeoutMs);
    }

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
        if (!_zk.IsReady && Activo == LectorHuella.Hikvision)
            return new MatchResult { Success = false, ErrorMessage = "Huella registrada con el lector ZKTeco: conecte ese lector para compararla" };
        return _zk.MatchTemplates(t1, t2);
    }

    public DeviceStatus GetStatus()
    {
        AsegurarLector();
        var s = Activo switch
        {
            LectorHuella.ZKTeco => _zk.GetStatus(),
            LectorHuella.Hikvision => _hik.GetStatus(),
            _ => new DeviceStatus { SdkInitialized = true },
        };
        s.Model = Modelo;
        s.SerialNumber = SerialNumber;
        s.Reader = Activo.ToString();
        return s;
    }

    public void Dispose()
    {
        _zk.Dispose();
        _hik.Dispose();
        GC.SuppressFinalize(this);
    }
}
