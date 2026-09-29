# Lazo

Aplicación para enviar archivos directamente entre equipos Windows 10 y 11 de una misma subred privada. Funciona en segundo plano desde la bandeja y se abre con **doble Alt izquierdo** o **Ctrl+Alt+L**. La ventana emerge desde la base de la pantalla donde está el cursor al abrir y queda centrada horizontalmente. La alerta de recepción aparece abajo a la derecha, sin robar el foco, y muestra un previo si el archivo es una imagen. Al terminar, ofrece **Mostrar en carpeta** y **Abrir**, y permanece visible hasta elegir una opción o cerrar.

Tu ícono aparece en la cabecera de Minimal y Standard y abre tu perfil al pulsarlo. La foto se muestra ampliada en Ajustes y se actualiza inmediatamente al cambiarla. El engranaje de la esquina superior derecha abre los ajustes y guarda la elección. El panel permanece abierto mientras se cambian las opciones. La ventana se arrastra; Escape o un clic fuera la cierra. El nombre que ven los demás es el del usuario, y se puede cambiar en ajustes. Ahí también se elige si Lazo abre con Windows, si busca actualizaciones en GitHub (`maxhine/lazo`) y se cambia o quita la foto de perfil. La imagen se recorta al centro, se reduce a 96 × 96 píxeles en escala de grises y se comparte con los equipos de la subred; sin foto se muestran las iniciales. El acercamiento del cursor al borde de la pantalla ya no abre ninguna ventana.

- **Minimal**: al abrir solo se ve la barra de búsqueda, con círculos de iniciales encima para los equipos conectados. No hay textos. Al escribir, la ventana se expande y muestra resultados; las iniciales de cada fila envían el archivo.
- **Standard**: al abrir se ven los equipos como íconos grandes, en una ventana ajustada a cuántos hay. Al pulsar uno se abre el diálogo para elegir el archivo. También se puede arrastrar un archivo desde el Explorador hasta el ícono: el envío empieza al soltarlo.
- **Apariencia**: Plano, Vidrio u Oscuro, en el mismo panel.

La interfaz usa Bahnschrift con respaldo en Segoe UI. El contenido se recorta siguiendo las esquinas redondeadas del recuadro; al abrir, una silueta líquida asciende desde la base y se transforma en la ventana. Los ajustes usan controles de selección en forma de pastilla. La sombra de la ventana se eliminó para evitar un parche rectangular translúcido en las esquinas.

![Lazo Minimal compacto](docs/interfaz-colapsada.png)

## Instalar y compartir

Comparte **`dist/Lazo-Setup-0.4.13.exe`**. Es un solo archivo: contiene Lazo, crea accesos directos, registra la desinstalación en Configuración de Windows y configura dos reglas entrantes limitadas al perfil **Privado** y a la **subred local**. Solicita permisos de administrador. Puede iniciar con Windows si se deja marcada la opción del instalador.

1. Cierra cualquier copia anterior de Lazo desde el icono de la bandeja.
2. Ejecuta `Lazo-Setup-0.4.13.exe` y acepta el aviso de Windows.
3. Abre Lazo desde el menú Inicio. Repite la instalación en el otro equipo.
4. Asegúrate de que ambos equipos estén en una red marcada como **Privada** en Windows.

El instalador no tiene firma digital. Windows puede mostrar **Editor desconocido** o SmartScreen al compartirlo fuera de este equipo. Para distribuirlo sin esa advertencia hace falta firmarlo con un certificado de código de confianza. No se incluye ningún certificado ni clave privada en el proyecto.

También puede generarse un paquete portable con `scripts/package.ps1`, pero el instalador es la opción recomendada.

## Usar

1. Abre la ventana con doble Alt izquierdo o `Ctrl+Alt+L`.
2. En Minimal, escribe parte del nombre. Lazo consulta el índice local de [Everything](https://www.voidtools.com/support/everything/sdk/ipc/), si está abierto en ese equipo. Muestra hasta 24 archivos por búsqueda. También puedes arrastrar un archivo o usar `Ctrl+O`, incluso sin Everything. En Standard, pulsa el equipo para elegir uno o varios archivos, o arrastra varios desde el Explorador hasta su ícono: salen a la vez.
3. En Minimal, pulsa las iniciales del destinatario junto al archivo. En Standard, el archivo elegido o soltado se envía a ese equipo. **Esa acción inicia el envío inmediatamente**.
4. En el receptor aparece una alerta abajo a la derecha. Si llegan varios archivos del mismo equipo, se agrupan y se aceptan todos de una vez. Si el archivo es una imagen, incluye un previo. La solicitud caduca tras 90 segundos.
5. El archivo aceptado se guarda en `Descargas\Lazo`. Se verifica con SHA-256 antes de conservarlo; la alerta se repliega al terminar.

Cerrar la ventana principal la oculta en la bandeja. **Salir** en el menú de la bandeja detiene Lazo.

## Red y requisitos

Lazo anuncia su presencia por UDP `48351` y transfiere por TCP `48352`. Los nombres aparecen solo si **ambos equipos tienen Lazo abierto**, están en la misma subred IPv4 privada y el firewall permite la conexión. No usa carpetas compartidas ni permisos SMB. Una red Wi‑Fi con aislamiento entre clientes puede impedir el descubrimiento.

La búsqueda rápida requiere Everything instalado y ejecutándose en el equipo que envía. Lazo no instala Everything ni copia su base de datos; consulta su índice mediante IPC local. Los resultados dependen de las carpetas que Everything tenga indexadas. El receptor no necesita Everything.

Requiere .NET Framework 4.8 o superior. El ejecutable se compiló y probó en Windows 11; la validación entre dos equipos físicos, incluida Windows 10, sigue pendiente.

## Desarrollo y verificación

No requiere SDK de .NET ni paquetes NuGet en el equipo de desarrollo; usa el compilador de .NET Framework.

```powershell
.\scripts\build.ps1 -Release
.\scripts\test-everything.ps1
.\scripts\test-network.ps1
.\scripts\build-installer.ps1
.\scripts\test-installer.ps1
```

`test-everything.ps1` comprueba la consulta y respuesta Unicode contra un servidor IPC simulado. `test-network.ps1` hace una transferencia real a una IP privada del propio equipo y compara el archivo recibido. `test-installer.ps1` comprueba el ejecutable incluido. La conexión de búsqueda con una instancia activa de Everything aún debe comprobarse en una sesión de escritorio normal: la instancia instalada en este entorno no expone su ventana IPC a la sesión de prueba.

## Alcance de esta versión

- Un archivo por envío, hasta 20 GB; una recepción activa a la vez.
- El protocolo aún **no cifra ni autentica** equipos. El nombre del remitente puede suplantarse. Utiliza Lazo solo en redes de confianza y verifica el remitente y su IP antes de aceptar.
- Doble Alt deja intacto el comportamiento normal de Alt en otras aplicaciones; alguna puede activar su menú tras el primer toque. `Ctrl+Alt+L` queda como alternativa.
- El instalador y el ejecutable son para Windows. No hay actualización automática.
