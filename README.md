# Isla Dinámica para Windows 11

Una "isla dinámica" estilo iPhone, pegada al borde superior de la pantalla. Es liviana: usa unos 13 MB de RAM y casi nada de CPU.

## Qué hace

- **Música**: muestra lo que suena (Spotify, YouTube en el navegador, etc.) con la carátula, controles y barra de progreso.
- **Volumen**: al subir o bajar el volumen se ve en la isla en lugar del cuadro de Windows.
- **Notificaciones**: WhatsApp, Discord, correo… aparecen en la isla con una campanita, y el aviso de Windows de la esquina inferior derecha se esconde para que solo se vean arriba.
- **Agenda**: los próximos eventos de tus calendarios de Google, con aviso 10 minutos antes.
- **Monitor del sistema**: CPU, RAM y GPU.
- **Portapapeles**: lo último que copiaste, para volver a copiarlo con un clic.
- Se esconde sola cuando juegas o ves algo en pantalla completa.
- Arranca con Windows y tiene un ícono en la bandeja (verde = visible, rojo = oculta).

**Cómo se usa:** pasa el mouse por encima para abrirla y usa la rueda del mouse para cambiar de página (Música → Agenda → Sistema → Portapapeles).

---

## Instalación paso a paso

### 1. Instalar lo necesario (solo la primera vez)

1. Descarga e instala el **.NET 10 SDK** desde <https://dotnet.microsoft.com/download/dotnet/10.0>. Elige el instalador para **Windows x64**.
2. Para comprobar que quedó bien, abre **PowerShell** (búscalo en el menú Inicio) y escribe:
   ```
   dotnet --version
   ```
   Si aparece un número que empieza con `10`, está listo.

### 2. Descargar el código

- **Opción fácil:** en esta página de GitHub, pulsa el botón verde **Code → Download ZIP** y descomprímelo donde quieras (por ejemplo, en el Escritorio).
- **Con Git:**
  ```
  git clone https://github.com/matiudev-random/dynamic_island.git
  ```

### 3. Instalar la isla

1. Abre la carpeta que descargaste.
2. Haz clic en la barra de direcciones del Explorador, escribe `powershell` y pulsa Enter. Se abre PowerShell ya ubicado en esa carpeta.
3. Pega esto y pulsa Enter:
   ```
   powershell -ExecutionPolicy Bypass -File .\instalar.ps1
   ```
4. Espera un momento. Al terminar, la isla aparece arriba de la pantalla.

La isla queda instalada en `%LOCALAPPDATA%\Programs\IslaDinamica` y se abrirá sola cada vez que enciendas el PC.

> Para **actualizarla** después de cambiar el código, repite el paso 3. El script cierra la versión anterior y abre la nueva.

### 4. Dar permiso a las notificaciones

La primera vez, Windows puede pedir permiso para que la isla lea tus notificaciones. Acéptalo. Si no lo pidió o lo rechazaste:
**Configuración → Privacidad y seguridad → Notificaciones** y activa el acceso para las aplicaciones.

---

## Configuración (se hace a mano, desde la isla)

> Si escuchas **dos sonidos** al llegar una notificación (la campanita y el de Windows), desactiva uno: el de la isla en el menú de abajo, o el de Windows en **Configuración → Sistema → Notificaciones** (desmarca «Reproducir sonido» en las apps que quieras).

Todo se maneja con **clic derecho sobre el ícono de la isla en la bandeja** (junto al reloj; puede estar dentro de la flechita `^`):

| Opción | Para qué sirve |
|---|---|
| Ocultar / Mostrar isla | Esconde la isla (el ícono se pone rojo) |
| Mostrar notificaciones | Activa o desactiva las notificaciones en la isla |
| Ocultar avisos de Windows | Esconde el aviso de abajo a la derecha (siguen llegando al centro de notificaciones) |
| Sonido de notificaciones | Activa o desactiva la campanita de la isla |
| Mostrar portapapeles | Activa o desactiva el aviso al copiar algo |
| Ocultar volumen de Windows | Usa el volumen de la isla en vez del de Windows |
| Conectar calendarios… | Agrega tus calendarios de Google (ver abajo) |
| Iniciar con Windows | Activa o desactiva el arranque automático |
| Salir | Cierra la isla |

### Conectar Google Calendar

Los calendarios **no vienen en el código**: cada persona agrega los suyos.

1. Abre Google Calendar en el navegador y entra a **Configuración (⚙)**.
2. A la izquierda, en **Configuración de mis calendarios**, elige un calendario y luego **Integrar calendario**.
3. Copia la **Dirección secreta en formato iCal**.
4. En la isla: clic derecho en el ícono de la bandeja → **Conectar calendarios…** y pega el enlace. Si tienes varios calendarios, pon **un enlace por línea**.
5. Pulsa **Guardar**.

---

## Privacidad

- **Los enlaces de tus calendarios son secretos.** Quien los tenga puede ver tu calendario, así que no los compartas ni los pegues en el código. La isla los guarda solo en tu PC, en el registro de Windows (`HKEY_CURRENT_USER\Software\IslaDinamica`), nunca en este repositorio.
- Si alguna vez se filtra un enlace, en Google Calendar usa **Restablecer** junto a la dirección secreta para crear uno nuevo y vuelve a pegarlo en la isla.
- El historial del portapapeles vive solo en memoria y se borra al cerrar la isla. Tampoco guarda lo que copias desde administradores de contraseñas ni los enlaces secretos de calendario.
- La isla no envía nada a internet: lo único que descarga son tus calendarios.

---

## Desinstalar

1. Clic derecho en el ícono de la bandeja → desmarca **Iniciar con Windows** → **Salir**.
2. Borra la carpeta `%LOCALAPPDATA%\Programs\IslaDinamica` (pégala en la barra del Explorador para llegar).
3. (Opcional) Para borrar también tu configuración y enlaces de calendario, abre PowerShell y ejecuta:
   ```
   Remove-Item HKCU:\Software\IslaDinamica -Recurse
   ```

---

## Para desarrolladores

- Hecho en C# con WPF sobre .NET 10, sin librerías externas.
- Compilar y probar sin instalar: `dotnet run` dentro de esta carpeta.
- Archivos principales:
  - `MainWindow.xaml(.cs)`: la isla y sus estados/animaciones.
  - `MediaService.cs`: música (controles multimedia de Windows).
  - `VolumeService.cs` y `VolumeKeyHook.cs`: volumen.
  - `NotificationService.cs`: notificaciones.
  - `ToastHider.cs`: esconde el aviso nativo de Windows.
  - `NotificationSound.cs`: la campanita (generada en código, sin archivos de audio).
  - `CalendarService.cs` y `IcsParser.cs`: calendarios iCal.
  - `SystemMonitor.cs`: CPU, RAM y GPU.
  - `ClipboardWatcher.cs`: portapapeles.
  - `TrayIcon.cs`: ícono de la bandeja.
  - `Settings.cs` y `AutoStart.cs`: configuración e inicio con Windows.
