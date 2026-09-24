using System.Runtime.InteropServices;

namespace GymDesk.Fingerprint.Services;

/// <summary>
/// P/Invoke de FPModule_SDK.dll: la DLL con la que iVMS-4200 (el cliente oficial de Hikvision) maneja el
/// lector de huella USB DS-K1F820-F. El lector se presenta a Windows como una unidad de CD-ROM (almacenamiento
/// USB, VID 0x2109) y la DLL le habla por la letra de unidad ("\\.\D:"), por eso no necesita ningún driver.
///
/// Se extrajo del instalador de iVMS-4200 AC 1.4.0.10 (x86, "FPModuleSDK_Win_x86_V2.0.0_Build180629").
/// Todas las funciones devuelven 0 si salió bien. Firmas comprobadas con el lector real (stdcall):
///   OpenDevice/CloseDevice(), GetSDKVersion(char[64]), GetDeviceInfo(char[64]),
///   DetectFinger(int* estado: 1 = hay dedo), CaptureImage(byte* imagen, int* ancho, int* alto) → 256 x 288, 8 bits.
/// FpEnroll / MatchTemplate / GetQuality existen pero no hay cabecera pública: la plantilla y la comparación
/// las hace SourceAFIS en la PC.
/// </summary>
internal static class HikvisionFpModule
{
    private const string Dll = "FPModule_SDK.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_GetSDKVersion(byte[] version);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_OpenDevice();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_CloseDevice();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_GetDeviceInfo(byte[] info);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_DetectFinger(ref int estado);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_CaptureImage(byte[] imagen, ref int ancho, ref int alto);

    public static string Texto(byte[] buf)
    {
        int n = Array.IndexOf(buf, (byte)0);
        return System.Text.Encoding.ASCII.GetString(buf, 0, n < 0 ? buf.Length : n).Trim();
    }
}
