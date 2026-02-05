using Microsoft.AspNetCore.Mvc;
using GymDesk.Fingerprint.Services;

namespace GymDesk.Fingerprint.Controllers;

/// <summary>
/// API REST para lector de huellas ZKTeco ZK9500.
/// </summary>
[ApiController]
[Route("api/fingerprint")]
public class FingerprintController : ControllerBase
{
    private readonly ZKFingerprintService _fingerprintService;
    private readonly ILogger<FingerprintController> _logger;

    public FingerprintController(
        ZKFingerprintService fingerprintService,
        ILogger<FingerprintController> logger)
    {
        _fingerprintService = fingerprintService;
        _logger = logger;
    }

    /// <summary>
    /// Obtiene el estado del servicio y dispositivo.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var status = _fingerprintService.GetStatus();

        return Ok(new
        {
            success = true,
            service = "GymDesk Fingerprint Service",
            version = "2.0.0",
            device = new
            {
                model = "ZKTeco ZK9500",
                serialNumber = "BXUC223460364",
                sdkInitialized = status.SdkInitialized,
                connected = status.DeviceConnected,
                count = status.DeviceCount,
                ready = status.IsReady
            },
            image = new
            {
                width = status.ImageWidth,
                height = status.ImageHeight
            }
        });
    }

    /// <summary>
    /// Inicializa/conecta el dispositivo ZKTeco.
    /// </summary>
    [HttpPost("init")]
    [HttpPost("connect")]
    public IActionResult Initialize([FromQuery] int deviceIndex = 0)
    {
        _logger.LogInformation("🔌 Conectando dispositivo ZK9500...");

        bool success = _fingerprintService.OpenDevice(deviceIndex);
        var status = _fingerprintService.GetStatus();

        return Ok(new
        {
            success,
            message = success 
                ? "Dispositivo ZK9500 conectado correctamente" 
                : "Error al conectar dispositivo. Verifique la conexión USB.",
            device = new
            {
                connected = status.DeviceConnected,
                count = status.DeviceCount,
                ready = status.IsReady
            }
        });
    }

    /// <summary>
    /// Desconecta el dispositivo.
    /// </summary>
    [HttpPost("disconnect")]
    public IActionResult Disconnect()
    {
        _fingerprintService.CloseDevice();
        
        return Ok(new
        {
            success = true,
            message = "Dispositivo desconectado"
        });
    }

    /// <summary>
    /// Reconecta el dispositivo (útil cuando se desconecta/reconecta USB).
    /// </summary>
    [HttpPost("reconnect")]
    [HttpPost("refresh")]
    public IActionResult Reconnect()
    {
        _logger.LogInformation("🔄 Reconectando dispositivo ZK9500...");

        bool success = _fingerprintService.Reconnect();
        var status = _fingerprintService.GetStatus();

        return Ok(new
        {
            success,
            message = success 
                ? "Dispositivo ZK9500 reconectado correctamente" 
                : "No se pudo reconectar. Verifique la conexión USB.",
            device = new
            {
                connected = status.DeviceConnected,
                count = status.DeviceCount,
                ready = status.IsReady
            }
        });
    }

    /// <summary>
    /// Captura una huella digital.
    /// </summary>
    /// <param name="timeout">Tiempo de espera en milisegundos (default: 10000)</param>
    [HttpPost("capture")]
    [HttpGet("capture")]
    public async Task<IActionResult> Capture([FromQuery] int timeout = 10000)
    {
        _logger.LogInformation("👆 Capturando huella...");

        var result = await _fingerprintService.CaptureAsync(timeout);

        return Ok(new
        {
            success = result.Success,
            message = result.Success ? "Huella capturada exitosamente" : result.ErrorMessage,
            template = result.TemplateBase64,
            image = result.Success ? new
            {
                width = result.ImageWidth,
                height = result.ImageHeight
            } : null
        });
    }

    /// <summary>
    /// Registra una huella (3 capturas + fusión).
    /// Usado para crear un template de alta calidad para almacenar.
    /// </summary>
    /// <param name="timeout">Tiempo de espera por captura en ms (default: 30000)</param>
    [HttpPost("enroll")]
    [HttpPost("register")]
    public async Task<IActionResult> Enroll([FromQuery] int timeout = 30000)
    {
        _logger.LogInformation("🔐 Iniciando registro de huella...");

        var result = await _fingerprintService.EnrollAsync(timeout);

        return Ok(new
        {
            success = result.Success,
            message = result.Success 
                ? "Huella registrada exitosamente (3 capturas fusionadas)" 
                : result.ErrorMessage,
            template = result.TemplateBase64
        });
    }

    /// <summary>
    /// Verifica una huella contra un template almacenado.
    /// Captura una huella y la compara con el template proporcionado.
    /// </summary>
    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] VerifyRequest request)
    {
        if (string.IsNullOrEmpty(request.Template))
        {
            return BadRequest(new { success = false, message = "Template requerido" });
        }

        _logger.LogInformation("🔍 Verificando huella...");

        var result = await _fingerprintService.VerifyAsync(request.Template, request.Timeout);

        return Ok(new
        {
            success = result.Success,
            message = result.Success
                ? (result.IsMatch ? "✅ Huella verificada correctamente" : "❌ Huella no coincide")
                : result.ErrorMessage,
            verified = result.IsMatch,
            score = result.Score,
            capturedTemplate = result.CapturedTemplate
        });
    }

    /// <summary>
    /// Compara dos templates sin capturar (verificación offline).
    /// </summary>
    [HttpPost("match")]
    public IActionResult Match([FromBody] MatchRequest request)
    {
        if (string.IsNullOrEmpty(request.Template1) || string.IsNullOrEmpty(request.Template2))
        {
            return BadRequest(new { success = false, message = "Se requieren ambos templates" });
        }

        var result = _fingerprintService.MatchTemplates(request.Template1, request.Template2);

        return Ok(new
        {
            success = result.Success,
            message = result.Success
                ? (result.IsMatch ? "Templates coinciden" : "Templates no coinciden")
                : result.ErrorMessage,
            match = result.IsMatch,
            score = result.Score
        });
    }
}

#region Request DTOs

public class VerifyRequest
{
    /// <summary>
    /// Template almacenado en Base64.
    /// </summary>
    public string Template { get; set; } = "";
    
    /// <summary>
    /// Timeout para la captura en milisegundos.
    /// </summary>
    public int Timeout { get; set; } = 10000;
}

public class MatchRequest
{
    /// <summary>
    /// Primer template en Base64.
    /// </summary>
    public string Template1 { get; set; } = "";
    
    /// <summary>
    /// Segundo template en Base64.
    /// </summary>
    public string Template2 { get; set; } = "";
}

#endregion
