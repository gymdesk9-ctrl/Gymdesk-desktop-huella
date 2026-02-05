# GymDesk Fingerprint Service

Servicio REST para captura y verificación de huellas digitales usando el lector **ZKTeco ZK9500**.

## Dispositivo

- **Modelo:** ZKTeco ZK9500
- **S/N:** BXUC223460364
- **Tipo:** USB Fingerprint Reader
- **DPI:** 500

## Requisitos

1. **Driver ZKTeco** - Instalar el driver USB del ZK9500
   - Descarga desde el sitio oficial de ZKTeco
   - O usar los drivers del ZKFinger Standard SDK

2. **.NET 8.0 Runtime** (si no está compilado como self-contained)

## Instalación

```powershell
cd GymDesk-hikvision-fingerprint
dotnet restore
dotnet build
```

## Ejecución

```powershell
dotnet run
```

El servicio iniciará en: `http://localhost:5051`

## API Endpoints

### Estado del Servicio

```http
GET http://localhost:5051/api/fingerprint/status
```

Respuesta:
```json
{
  "success": true,
  "service": "GymDesk Fingerprint Service",
  "version": "2.0.0",
  "device": {
    "model": "ZKTeco ZK9500",
    "serialNumber": "BXUC223460364",
    "connected": true,
    "ready": true
  }
}
```

### Conectar Dispositivo

```http
POST http://localhost:5051/api/fingerprint/connect
```

### Capturar Huella

```http
POST http://localhost:5051/api/fingerprint/capture?timeout=10000
```

Respuesta:
```json
{
  "success": true,
  "message": "Huella capturada exitosamente",
  "template": "BASE64_ENCODED_TEMPLATE..."
}
```

### Registrar Huella (Enroll)

Captura 3 muestras y genera un template de alta calidad para almacenamiento.

```http
POST http://localhost:5051/api/fingerprint/enroll?timeout=15000
```

Respuesta:
```json
{
  "success": true,
  "message": "Huella registrada exitosamente (3 capturas fusionadas)",
  "template": "BASE64_ENCODED_TEMPLATE..."
}
```

### Verificar Huella

Captura una huella y la compara contra un template almacenado.

```http
POST http://localhost:5051/api/fingerprint/verify
Content-Type: application/json

{
  "template": "BASE64_ENCODED_STORED_TEMPLATE...",
  "timeout": 10000
}
```

Respuesta:
```json
{
  "success": true,
  "message": "✅ Huella verificada correctamente",
  "verified": true,
  "score": 85,
  "capturedTemplate": "BASE64_TEMPLATE..."
}
```

### Comparar Templates (Offline)

Compara dos templates sin necesidad de capturar.

```http
POST http://localhost:5051/api/fingerprint/match
Content-Type: application/json

{
  "template1": "BASE64_TEMPLATE_1...",
  "template2": "BASE64_TEMPLATE_2..."
}
```

Respuesta:
```json
{
  "success": true,
  "message": "Templates coinciden",
  "match": true,
  "score": 92
}
```

## Swagger

Documentación interactiva disponible en:
```
http://localhost:5051/swagger
```

## Flujo de Uso Típico

### 1. Registro de Usuario

```javascript
// 1. Registrar huella (3 capturas)
const enrollResponse = await fetch('http://localhost:5051/api/fingerprint/enroll', {
  method: 'POST'
});
const { template } = await enrollResponse.json();

// 2. Guardar template en base de datos
await saveUserFingerprint(userId, template);
```

### 2. Verificación de Usuario

```javascript
// 1. Obtener template almacenado
const storedTemplate = await getUserFingerprint(userId);

// 2. Verificar huella
const verifyResponse = await fetch('http://localhost:5051/api/fingerprint/verify', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ template: storedTemplate })
});
const { verified, score } = await verifyResponse.json();

if (verified) {
  console.log('Usuario autenticado con score:', score);
}
```

## Estructura del Proyecto

```
GymDesk-hikvision-fingerprint/
├── Controllers/
│   └── FingerprintController.cs   # API REST
├── Services/
│   └── ZKFingerprintService.cs    # Lógica de negocio
├── SDK/
│   ├── ZKFingerSDK.cs             # Wrapper del SDK
│   ├── libzkfpcsharp.dll          # SDK ZKTeco
│   ├── zkfinger10.dll             # Driver
│   └── ...
├── Program.cs                      # Punto de entrada
└── appsettings.json
```

## Notas Técnicas

- El template de huella tiene ~1500-2000 bytes
- Score > 0 indica coincidencia
- Score típico para match exitoso: 50-100+
- El servicio maneja un solo dispositivo a la vez
- El SDK se inicializa automáticamente al primer request

## Solución de Problemas

### "No hay dispositivos ZKTeco conectados"

1. Verificar que el ZK9500 esté conectado por USB
2. Verificar que el driver esté instalado (ver Administrador de Dispositivos)
3. Reiniciar el servicio

### "DLL no encontrada"

1. Verificar que `libzkfpcsharp.dll` esté en la carpeta `SDK/`
2. Verificar que las DLLs se copien al directorio de salida

### "Error en captura"

1. Limpiar el sensor del lector
2. Colocar el dedo correctamente (centro del sensor)
3. Mantener el dedo quieto durante la captura
