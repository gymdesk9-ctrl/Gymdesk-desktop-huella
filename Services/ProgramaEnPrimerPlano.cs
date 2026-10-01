using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GymDesk.Fingerprint.Services;

/// <summary>
/// ¿El programa con el que está trabajando el usuario ahora mismo (ventana en primer plano) es OTRO programa
/// que usa el lector de huella? Sirve para que el monitor de accesos de GymDesk le ceda el lector: un lector USB
/// solo puede estar abierto en un programa a la vez.
///
/// Se reconoce al otro programa por cualquiera de estas señales:
///   - tiene cargada una librería de lector de huella (ZKTeco / Hikvision),
///   - en su carpeta hay una de esas librerías,
///   - es otro programa de gimnasio (su nombre o carpeta contiene "gym" o "gimnas").
/// Si no se reconoce (navegador, WhatsApp, Excel...), GymDesk conserva el lector.
/// </summary>
public static class ProgramaEnPrimerPlano
{
    private static readonly string[] MarcasLector = { "zkfp", "zkfinger", "biokey", "fpmodule" };
    private static readonly string[] MarcasGimnasio = { "gym", "gimnas" };

    private static readonly object _candado = new();
    private static uint _pid;
    private static bool _usa;
    private static string _nombre = "";
    private static DateTime _cuando = DateTime.MinValue;
    private static readonly Dictionary<string, bool> _carpetas = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint acceso, bool heredar, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr proceso, uint flags, StringBuilder ruta, ref uint tamano);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>true si la ventana en primer plano es de otro programa que usa el lector de huella.</summary>
    public static bool UsaLectorDeHuella(out string nombre)
    {
        nombre = "";
        try
        {
            IntPtr ventana = GetForegroundWindow();
            if (ventana == IntPtr.Zero) return false;
            GetWindowThreadProcessId(ventana, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

            lock (_candado)
            {
                // El mismo programa sigue delante: no volver a examinarlo en cada consulta
                double hace = (DateTime.UtcNow - _cuando).TotalSeconds;
                if (pid == _pid && hace < (_usa ? 30 : 3)) { nombre = _nombre; return _usa; }

                _pid = pid;
                _cuando = DateTime.UtcNow;
                _usa = Examinar(pid, out _nombre);
                nombre = _nombre;
                return _usa;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool Examinar(uint pid, out string nombre)
    {
        nombre = "";
        string ruta = RutaDelProceso(pid);
        using var proceso = Process.GetProcessById((int)pid);
        string exe = ruta.Length > 0 ? Path.GetFileNameWithoutExtension(ruta) : proceso.ProcessName;
        nombre = exe;

        // GymDesk (la aplicación, este servicio o Electron en desarrollo) no es "otro programa"
        if (exe.StartsWith("GymDesk", StringComparison.OrdinalIgnoreCase) || exe.Equals("electron", StringComparison.OrdinalIgnoreCase)) return false;

        // 1. Librería de lector de huella cargada (solo se puede mirar en programas de 32 bits sin permisos de administrador)
        try
        {
            foreach (ProcessModule m in proceso.Modules)
            {
                if (Contiene(m.ModuleName, MarcasLector)) return true;
            }
        }
        catch { /* 64 bits o administrador: se mira la carpeta */ }

        if (ruta.Length == 0) return false;

        // Programas de Windows (explorador, bloc de notas...): no se mira su carpeta ni su nombre
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (ruta.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return false;

        // 2. Otro programa de gimnasio (por nombre, descripción o carpeta)
        string descripcion = "";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(ruta);
            descripcion = (info.ProductName ?? "") + " " + (info.FileDescription ?? "");
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) nombre = info.FileDescription.Trim();
        }
        catch { }
        string identidad = (ruta + " " + descripcion).Replace("gymdesk", "", StringComparison.OrdinalIgnoreCase);
        if (Contiene(identidad, MarcasGimnasio)) return true;

        // 3. Librería de lector de huella en su carpeta
        string carpeta = Path.GetDirectoryName(ruta) ?? "";
        if (carpeta.Length == 0) return false;
        if (_carpetas.TryGetValue(carpeta, out bool conocida)) return conocida;
        bool tiene = false;
        try
        {
            var opciones = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true };
            int vistos = 0;
            foreach (string archivo in Directory.EnumerateFiles(carpeta, "*", opciones))
            {
                if (++vistos > 4000) break;
                string ext = Path.GetExtension(archivo);
                if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".ocx", StringComparison.OrdinalIgnoreCase)) continue;
                if (Contiene(Path.GetFileName(archivo), MarcasLector)) { tiene = true; break; }
            }
        }
        catch { }
        _carpetas[carpeta] = tiene;
        return tiene;
    }

    private static bool Contiene(string? texto, string[] marcas)
    {
        if (string.IsNullOrEmpty(texto)) return false;
        foreach (string m in marcas)
        {
            if (texto.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string RutaDelProceso(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var ruta = new StringBuilder(1024);
            uint tamano = (uint)ruta.Capacity;
            return QueryFullProcessImageName(h, 0, ruta, ref tamano) ? ruta.ToString() : "";
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
