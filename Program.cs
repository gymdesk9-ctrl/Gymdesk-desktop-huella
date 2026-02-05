using GymDesk.Fingerprint.Services;
using System.Net.NetworkInformation;

var builder = WebApplication.CreateBuilder(args);

// =============================================================================
// Verificación de puerto
// =============================================================================
static bool IsPortInUse(int port)
{
    var ipProperties = IPGlobalProperties.GetIPGlobalProperties();
    var tcpEndPoints = ipProperties.GetActiveTcpListeners();
    return tcpEndPoints.Any(e => e.Port == port);
}

static void KillProcessOnPort(int port)
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c for /f \"tokens=5\" %a in ('netstat -aon ^| findstr :{port} ^| findstr LISTENING') do taskkill /F /PID %a",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(5000);
        Thread.Sleep(1000);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"No se pudo liberar el puerto {port}: {ex.Message}");
    }
}

const int port = 5051;
if (IsPortInUse(port))
{
    Console.WriteLine($"⚠️  Puerto {port} en uso. Intentando liberarlo...");
    KillProcessOnPort(port);

    if (IsPortInUse(port))
    {
        Console.WriteLine($"❌ No se pudo liberar el puerto {port}. Saliendo...");
        Environment.Exit(1);
    }
    Console.WriteLine($"✅ Puerto {port} liberado");
}

// =============================================================================
// Configuración de la aplicación
// =============================================================================
builder.WebHost.UseUrls($"http://localhost:{port}");

// Servicios
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() 
    { 
        Title = "GymDesk Fingerprint API", 
        Version = "v2.0",
        Description = "API para lector de huellas ZKTeco ZK9500"
    });
});

// Registrar servicio de huellas como Singleton
builder.Services.AddSingleton<ZKFingerprintService>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// =============================================================================
// Pipeline HTTP
// =============================================================================
app.UseCors("AllowAll");

app.UseSwagger();
app.UseSwaggerUI(c => 
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "GymDesk Fingerprint API v2.0");
    c.RoutePrefix = "swagger";
});

app.MapControllers();

// Health check
app.MapGet("/", () => new
{
    service = "GymDesk Fingerprint Service",
    device = "ZKTeco ZK9500",
    serialNumber = "BXUC223460364",
    status = "running",
    version = "2.0.0",
    port = port,
    endpoints = new
    {
        status = "GET /api/fingerprint/status",
        connect = "POST /api/fingerprint/connect",
        capture = "POST /api/fingerprint/capture",
        enroll = "POST /api/fingerprint/enroll",
        verify = "POST /api/fingerprint/verify",
        match = "POST /api/fingerprint/match",
        swagger = "/swagger"
    }
});

// =============================================================================
// Inicio - Inicializar dispositivo automáticamente
// =============================================================================
Console.WriteLine();
Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
Console.WriteLine("║       GymDesk Fingerprint Service v2.0                    ║");
Console.WriteLine("╠═══════════════════════════════════════════════════════════╣");
Console.WriteLine("║  Dispositivo: ZKTeco ZK9500                               ║");
Console.WriteLine("║  S/N: BXUC223460364                                       ║");
Console.WriteLine($"║  URL: http://localhost:{port}                              ║");
Console.WriteLine("║  Swagger: http://localhost:5051/swagger                   ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
Console.WriteLine();

// Inicializar el dispositivo ZKTeco automáticamente al arrancar
try
{
    var fingerprintService = app.Services.GetRequiredService<ZKFingerprintService>();
    Console.WriteLine("🔌 Inicializando dispositivo ZKTeco ZK9500...");
    
    if (fingerprintService.OpenDevice())
    {
        var status = fingerprintService.GetStatus();
        Console.WriteLine($"✅ Dispositivo conectado - Imagen: {status.ImageWidth}x{status.ImageHeight}px");
    }
    else
    {
        Console.WriteLine("⚠️  No se detectó lector USB. Conecte el dispositivo y llame a /api/fingerprint/connect");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"⚠️  Error al inicializar dispositivo: {ex.Message}");
}

app.Run();
