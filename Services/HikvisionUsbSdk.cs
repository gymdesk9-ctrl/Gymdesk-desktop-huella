using System.Runtime.InteropServices;

namespace GymDesk.Fingerprint.Services;

/// <summary>
/// Enlace P/Invoke al USB SDK de Hikvision (HCUsbSDK.dll v1.0.2.5, 32 bits) para el lector de huella
/// DS-K1F820-F. El lector es HID: no necesita driver. Solo se usan las funciones de huella
/// (comandos 1025-1027 del SDK). Las estructuras siguen al pie de la letra HCUsbSDK.h (tamaños comentados ahí).
/// </summary>
internal static class HikvisionUsbSdk
{
    private const string Dll = "HCUsbSDK.dll";

    public const int InvalidUserId = -1;

    /// <summary>USB_SDK_SET_FINGER_PRINT_OPER_PARAM: modo de comparación y tipo de captura.</summary>
    public const uint CmdSetFingerPrintOperParam = 1025;
    /// <summary>USB_SDK_CAPTURE_FINGER_PRINT: espera un dedo y devuelve plantilla o imagen.</summary>
    public const uint CmdCaptureFingerPrint = 1026;
    /// <summary>USB_SDK_GET_FINGER_PRINT_CONTRAST_RESULT: resultado de la comparación interna.</summary>
    public const uint CmdGetFingerPrintContrastResult = 1027;

    /// <summary>MAX_FINGER_PRINT: tamaño del búfer de captura (la imagen ocupa ~90 KB).</summary>
    public const int MaxFingerPrint = 1024 * 100;

    // Errores del SDK (USB_ERROR_*)
    public const uint ErrNoDevice = 3;
    public const uint ErrDevNotReady = 6;
    public const uint ErrTimeout = 10;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void EnumDeviceCallback(ref DeviceInfo info, IntPtr user);

    /// <summary>USB_SDK_DEVICE_INFO (192 bytes): lo que informa cada dispositivo al enumerar.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DeviceInfo
    {
        public uint dwSize;
        public uint dwVID;
        public uint dwPID;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] szManufacturer;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] szDeviceName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szSerialNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 68)] public byte[] byRes;
    }

    /// <summary>USB_SDK_USER_LOGIN_INFO (192 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct LoginInfo
    {
        public uint dwSize;
        public uint dwTimeout;
        public uint dwVID;
        public uint dwPID;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] szUserName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szPassword;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szSerialNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 80)] public byte[] byRes;
    }

    /// <summary>USB_SDK_DEVICE_REG_RES (128 bytes): respuesta del login.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DeviceRegRes
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] szDeviceName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szSerialNumber;
        public uint dwSoftwareVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)] public byte[] byRes;
    }

    /// <summary>USB_CONFIG_INPUT_INFO (64 bytes en x86).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ConfigInput
    {
        public IntPtr lpCondBuffer;
        public uint dwCondBufferSize;
        public IntPtr lpInBuffer;
        public uint dwInBufferSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] byRes;
    }

    /// <summary>USB_CONFIG_OUTPUT_INFO (64 bytes en x86).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ConfigOutput
    {
        public IntPtr lpOutBuffer;
        public uint dwOutBufferSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 56)] public byte[] byRes;
    }

    /// <summary>USB_SDK_FINGER_PRINT_OPER_PARAM (32 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FingerPrintOperParam
    {
        public uint dwSize;
        /// <summary>0 sin comparar, 1 compara el propio lector, 2 compara la plataforma (nosotros).</summary>
        public byte byFPCompareType;
        /// <summary>1 plantilla Hikvision, 2 imagen (solo con byFPCompareType = 2).</summary>
        public byte byFPCaptureType;
        public byte byFPCompareTimeout;
        public byte byFPCompareMatchLevel;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)] public byte[] byRes;
    }

    /// <summary>USB_SDK_FINGER_PRINT_COND (32 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FingerPrintCond
    {
        public uint dwSize;
        /// <summary>Segundos que espera el dedo: 10-60.</summary>
        public byte byWait;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public byte[] byRes;
    }

    /// <summary>USB_SDK_FINGER_PRINT (32 bytes en x86): resultado de la captura.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FingerPrint
    {
        public uint dwSize;
        public uint dwFPSize;
        public IntPtr pFPBuffer;
        /// <summary>1 plantilla, 2 imagen.</summary>
        public byte byFPType;
        /// <summary>1 ok, 2 falló, 3 tiempo agotado, 4 imagen de mala calidad.</summary>
        public byte byResult;
        public byte byFPTemplateQuality;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public byte[] byRes;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool USB_SDK_Init();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool USB_SDK_Cleanup();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint USB_SDK_GetSDKVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint USB_SDK_GetLastError();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr USB_SDK_GetErrorMsg(uint dwErrorCode);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool USB_SDK_EnumDevice(EnumDeviceCallback callback, IntPtr user);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int USB_SDK_Login(ref LoginInfo loginInfo, ref DeviceRegRes regRes);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool USB_SDK_Logout(int userId);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool USB_SDK_SetDeviceConfig(int userId, uint command, IntPtr input, IntPtr output);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool USB_SDK_GetDeviceConfig(int userId, uint command, IntPtr input, IntPtr output);

    /// <summary>Último error del SDK como "código (mensaje)".</summary>
    public static string LastError()
    {
        uint code = USB_SDK_GetLastError();
        string msg;
        try { msg = Marshal.PtrToStringAnsi(USB_SDK_GetErrorMsg(code)) ?? ""; } catch { msg = ""; }
        return $"{code}{(msg.Length > 0 ? " (" + msg + ")" : "")}";
    }

    public static string Texto(byte[]? bytes)
    {
        if (bytes == null) return "";
        int n = Array.IndexOf(bytes, (byte)0);
        if (n < 0) n = bytes.Length;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, n).Trim();
    }

    public static byte[] Bytes(string texto, int largo)
    {
        var b = new byte[largo];
        var src = System.Text.Encoding.ASCII.GetBytes(texto ?? "");
        Array.Copy(src, b, Math.Min(src.Length, largo - 1));
        return b;
    }
}
