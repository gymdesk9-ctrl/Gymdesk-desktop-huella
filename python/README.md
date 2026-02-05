# README - Scripts Python para Lectores de Huella

## Estructura de archivos:

- `requirements.txt` - Dependencias necesarias
- `setup.bat` - Configura el entorno Python
- `test_usb_devices.py` - Detecta dispositivos USB
- `test_zkteco.py` - Prueba el lector ZKTeco
- `test_zkteco.bat` - Ejecuta la prueba ZKTeco
- `server.py` - Servidor Flask con API REST

## Pasos para usar:

### 1. Configurar entorno (primera vez):
```
setup.bat
```

### 2. Ver dispositivos USB conectados:
```
venv\Scripts\activate
python test_usb_devices.py
```

### 3. Probar captura con ZKTeco:
```
test_zkteco.bat
```

### 4. Iniciar servidor API:
```
venv\Scripts\activate
python server.py
```

El servidor correrá en: http://localhost:5052

## Endpoints del servidor:

- GET /api/devices - Lista dispositivos conectados
- POST /api/capture - Captura huella
  Body: {"deviceType": "zkteco", "timeout": 30000}

## Notas:

- Para ZKTeco se usa la librería `pyzkfp`
- Para Hikvision necesitarás drivers específicos o SDK
- Los scripts de prueba muestran resultados en consola
