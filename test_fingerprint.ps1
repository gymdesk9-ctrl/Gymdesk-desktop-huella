# ============================================
# Test Script - ZKTeco ZK9500 Fingerprint
# ============================================

$baseUrl = "http://localhost:5051/api/fingerprint"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Test ZKTeco ZK9500 Fingerprint Reader" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Verificar estado del servicio
Write-Host "[1] Verificando estado del servicio..." -ForegroundColor Yellow
try {
    $status = Invoke-RestMethod -Uri "$baseUrl/status" -Method Get
    Write-Host "    Servicio: $($status.service)" -ForegroundColor Green
    Write-Host "    Dispositivo: $($status.device.model)" -ForegroundColor Green
    Write-Host "    SDK Inicializado: $($status.device.sdkInitialized)" -ForegroundColor Green
    Write-Host "    Conectado: $($status.device.connected)" -ForegroundColor Green
    Write-Host "    Listo: $($status.device.ready)" -ForegroundColor Green
} catch {
    Write-Host "    ERROR: No se pudo conectar al servicio" -ForegroundColor Red
    Write-Host "    Asegurese de que el servicio este corriendo en puerto 5051" -ForegroundColor Red
    exit 1
}

Write-Host ""

# 2. Conectar dispositivo
Write-Host "[2] Conectando dispositivo ZK9500..." -ForegroundColor Yellow
try {
    $connect = Invoke-RestMethod -Uri "$baseUrl/connect" -Method Post
    if ($connect.success) {
        Write-Host "    $($connect.message)" -ForegroundColor Green
        Write-Host "    Dispositivos detectados: $($connect.device.count)" -ForegroundColor Green
    } else {
        Write-Host "    ERROR: $($connect.message)" -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "    ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

Write-Host ""

# 3. Capturar huella
Write-Host "[3] CAPTURA DE HUELLA" -ForegroundColor Yellow
Write-Host "    ========================================" -ForegroundColor Cyan
Write-Host "    Coloque su dedo en el lector ZK9500..." -ForegroundColor Cyan
Write-Host "    Tiene 15 segundos para capturar" -ForegroundColor Cyan
Write-Host "    ========================================" -ForegroundColor Cyan
Write-Host ""

try {
    $capture = Invoke-RestMethod -Uri "$baseUrl/capture?timeout=15000" -Method Post
    
    if ($capture.success) {
        Write-Host "    HUELLA CAPTURADA EXITOSAMENTE!" -ForegroundColor Green
        Write-Host "    Mensaje: $($capture.message)" -ForegroundColor Green
        
        if ($capture.template) {
            $templateLength = $capture.template.Length
            Write-Host "    Template (Base64): $templateLength caracteres" -ForegroundColor Green
            Write-Host "    Primeros 50 chars: $($capture.template.Substring(0, [Math]::Min(50, $templateLength)))..." -ForegroundColor Gray
            
            # Guardar template para pruebas posteriores
            $capture.template | Out-File -FilePath "captured_template.txt" -Encoding UTF8
            Write-Host "    Template guardado en: captured_template.txt" -ForegroundColor Cyan
        }
        
        if ($capture.image) {
            Write-Host "    Imagen: $($capture.image.width)x$($capture.image.height) px" -ForegroundColor Green
        }
    } else {
        Write-Host "    CAPTURA FALLIDA" -ForegroundColor Red
        Write-Host "    Error: $($capture.message)" -ForegroundColor Red
    }
} catch {
    Write-Host "    ERROR: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Test completado" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
